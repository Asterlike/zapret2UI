using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Manages user-defined bypass targets. Each target is a root domain whose related
/// domains (found via crt.sh Certificate Transparency, or entered by hand) are stored
/// as a <c>target-&lt;name&gt;.txt</c> hostlist under the lists folder. The union of all
/// target domains is mirrored to <c>targets.txt</c>, which:
///   • feeds the diagnostics matrix + auto-select/generation goal hosts, and
///   • is subtracted from the catch-all exclude by <see cref="EngineService"/> so the
///     active strategy actually desyncs these domains (even sensitive ones like yandex.ru
///     that the default exclude protects).
/// </summary>
public sealed class TargetService
{
    private const string Prefix = "target-";
    public const string AggregateName = "targets";

    private readonly HttpClient _http;

    public TargetService()
    {
        AppPaths.EnsureCreated();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZapretUI", "1.0"));
    }

    private static string PathFor(string name) => Path.Combine(AppPaths.ListsDir, Prefix + name + ".txt");

    /// <summary>All saved targets with their domain counts.</summary>
    public List<CustomTarget> GetTargets()
    {
        try
        {
            return Directory.EnumerateFiles(AppPaths.ListsDir, Prefix + "*.txt")
                .Select(f => Path.GetFileNameWithoutExtension(f)!.Substring(Prefix.Length))
                .Where(n => n.Length > 0)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => new CustomTarget { Name = n, DomainCount = ReadDomains(n).Count })
                .ToList();
        }
        catch { return new(); }
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    public List<string> ReadDomains(string name)
    {
        try
        {
            string p = PathFor(name);
            if (!File.Exists(p)) return new();
            return File.ReadAllLines(p)
                .Select(l => l.Trim().ToLowerInvariant())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Distinct()
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>Union of every target's domains (what gets probed / bypassed).</summary>
    public List<string> AllDomains()
    {
        try
        {
            return Directory.EnumerateFiles(AppPaths.ListsDir, Prefix + "*.txt")
                .SelectMany(f => File.ReadAllLines(f))
                .Select(l => l.Trim().ToLowerInvariant())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new(); }
    }

    public void Save(string name, IEnumerable<string> domains)
    {
        name = Normalize(name);
        if (name.Length == 0) return;
        var clean = domains
            .Select(d => d.Trim().ToLowerInvariant())
            .Where(d => d.Length > 0 && !d.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        File.WriteAllText(PathFor(name), string.Join('\n', clean));
        WriteAggregate();
    }

    public void Delete(string name)
    {
        try { var p = PathFor(name); if (File.Exists(p)) File.Delete(p); } catch { }
        WriteAggregate();
    }

    /// <summary>Mirror the union of all target domains to targets.txt (engine + diagnostics read this).</summary>
    private void WriteAggregate()
    {
        try { File.WriteAllText(Path.Combine(AppPaths.ListsDir, AggregateName + ".txt"), string.Join('\n', AllDomains())); }
        catch { /* non-fatal */ }
    }

    /// <summary>Strip scheme/path/port from user input, leaving a bare host. Returns "" for anything
    /// that can't be a safe file-name component (the host becomes target-&lt;name&gt;.txt).</summary>
    public static string Normalize(string input)
    {
        string s = (input ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return "";
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        s = s.Split('/', '\\', '?', '#')[0];     // also split on '\' so it can't escape the lists folder
        int colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        s = s.Trim('.');
        // The result is used to build a file path — reject path traversal / invalid file-name chars.
        if (s.Length == 0 || s.Contains("..") || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "";
        return s;
    }

    /// <summary>
    /// Expand a root domain into related domains via two free, key-less sources, run in parallel:
    ///   1. <b>crt.sh</b> (Certificate Transparency) — every subdomain / SAN under the root
    ///      (e.g. yandex.ru → mail.yandex.ru, an.yandex.ru …).
    ///   2. <b>cross-TLD brand probe</b> — generate &lt;brand&gt;.&lt;tld&gt; over a curated TLD set
    ///      and keep the ones that actually resolve in DNS (e.g. yandex.ru → yandex.md, yandex.kz …).
    ///      No paid API: a real brand TLD resolves, garbage does not.
    /// Returns the root plus up to <paramref name="cap"/> related domains (deduped, wildcards stripped).
    /// </summary>
    public async Task<List<string>> ExpandAsync(string rootDomain, int cap, CancellationToken ct)
    {
        string root = Normalize(rootDomain);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.Length == 0) return result.ToList();
        result.Add(root);

        var crt = CrtShSubdomainsAsync(root, ct);
        var tld = CrossTldVariantsAsync(root, ct);
        foreach (var d in await crt.ConfigureAwait(false)) result.Add(d);
        foreach (var d in await tld.ConfigureAwait(false)) result.Add(d);

        return result
            .OrderBy(d => d.Length).ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, cap))
            .ToList();
    }

    /// <summary>crt.sh subdomains/SAN under the root (best-effort; empty if crt.sh is offline/flaky).</summary>
    private async Task<List<string>> CrtShSubdomainsAsync(string root, CancellationToken ct)
    {
        var found = new List<string>();
        try
        {
            string url = $"https://crt.sh/?q=%25.{Uri.EscapeDataString(root)}&output=json";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("name_value", out var nv)) continue;
                foreach (var raw in (nv.GetString() ?? "").Split('\n'))
                {
                    string d = raw.Trim().ToLowerInvariant();
                    if (d.StartsWith("*.", StringComparison.Ordinal)) d = d[2..];
                    if (d.Length == 0 || d.Contains(' ') || d.Contains('@') || !d.Contains('.')) continue;
                    if (d == root || d.EndsWith("." + root, StringComparison.Ordinal)) found.Add(d);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* crt.sh flaky/offline — cross-TLD probe still works */ }
        return found;
    }

    /// <summary>TLDs the brand probe tries — gTLDs + RU-relevant ccTLDs (incl. common multi-label ones).</summary>
    private static readonly string[] BrandTlds =
    {
        "ru", "com", "net", "org", "info", "biz", "io", "app", "dev", "me", "tv", "online",
        "site", "store", "xyz", "pro", "su", "рф",
        "by", "kz", "ua", "uz", "md", "ge", "am", "az", "kg", "tj", "tm", "ee", "lv", "lt",
        "pl", "de", "fr", "fi", "tr", "il", "cz", "rs", "bg",
        "com.tr", "com.ua", "com.ge", "co.il", "co.uk",
    };

    /// <summary>Same-brand domains on other TLDs that actually resolve in DNS (no paid API).</summary>
    private static async Task<List<string>> CrossTldVariantsAsync(string root, CancellationToken ct)
    {
        string brand = BrandLabel(root);
        if (brand.Length < 2) return new();

        var candidates = BrandTlds.Select(t => brand + "." + t).Distinct(StringComparer.OrdinalIgnoreCase);
        var alive = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var gate = new SemaphoreSlim(16);
        var tasks = candidates.Select(async host =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { if (await ResolvesAsync(host, ct).ConfigureAwait(false)) alive.Add(host); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return alive.ToList();
    }

    /// <summary>The registrable brand label (yandex.ru → "yandex", a.yandex.com.tr → "yandex").</summary>
    private static string BrandLabel(string host)
    {
        var p = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2) return p.Length == 1 ? p[0] : "";
        // Multi-label suffix (com.tr, co.uk): brand sits one label further left.
        bool multi = p.Length >= 3 && p[^1].Length == 2 &&
                     p[^2] is "com" or "co" or "net" or "org" or "edu" or "gov" or "ac";
        return multi ? p[^3] : p[^2];
    }

    private static async Task<bool> ResolvesAsync(string host, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            return addrs.Length > 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SocketException) { return false; }
        catch { return false; }
    }
}
