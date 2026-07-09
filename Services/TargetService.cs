using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zapret2UI.Models;

namespace Zapret2UI.Services;

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
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zapret2UI", "1.0"));
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
    ///   2. <b>cross-TLD brand probe</b> — generate &lt;brand&gt;.&lt;tld&gt; over a curated TLD set,
    ///      then keep ONLY the ones that verifiably belong to the same owner: each candidate must
    ///      present a TLS certificate whose CN/SAN carries the brand label (see
    ///      <see cref="BelongsToBrandAsync"/>). Merely resolving in DNS is not enough — squatters and
    ///      parking pages (discord.ru, vk.io …) resolve too, and adding them to a desync hostlist would
    ///      break unrelated sites. The cert check is what rejects them.
    /// Progress is reported through <paramref name="progress"/> so the UI can show live activity.
    /// Returns the root plus up to <paramref name="cap"/> related domains (deduped, wildcards stripped).
    /// </summary>
    public async Task<List<string>> ExpandAsync(string rootDomain, int cap, IProgress<string>? progress, CancellationToken ct)
    {
        string root = Normalize(rootDomain);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.Length == 0) return result.ToList();
        result.Add(root);

        progress?.Report($"Ищу поддомены «{root}» (crt.sh) и проверяю зоны бренда…");
        var crt = CrtShSubdomainsAsync(root, progress, ct);
        var tld = CrossTldVariantsAsync(root, progress, ct);
        foreach (var d in await crt.ConfigureAwait(false)) result.Add(d);
        foreach (var d in await tld.ConfigureAwait(false)) result.Add(d);

        return result
            .OrderBy(d => d.Length).ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, cap))
            .ToList();
    }

    /// <summary>crt.sh subdomains/SAN under the root (best-effort; empty if crt.sh is offline/flaky).</summary>
    private async Task<List<string>> CrtShSubdomainsAsync(string root, IProgress<string>? progress, CancellationToken ct)
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
        if (found.Count > 0) progress?.Report($"crt.sh: поддоменов найдено — {found.Count}. Проверяю зоны бренда…");
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

    /// <summary>Same-brand domains on other TLDs that verifiably belong to the same owner. A candidate
    /// is kept only if it resolves AND presents a TLS certificate whose CN/SAN carries the brand label —
    /// so real siblings (yandex.kz → cert *.yandex.kz) pass while squatters/parking pages that merely
    /// resolve (discord.ru, vk.io …) are rejected. No paid API. Progress is streamed as zones are checked.</summary>
    private static async Task<List<string>> CrossTldVariantsAsync(string root, IProgress<string>? progress, CancellationToken ct)
    {
        string brand = BrandLabel(root);
        if (brand.Length < 2) return new();

        var candidates = BrandTlds
            .Select(t => brand + "." + t)
            .Where(h => !string.Equals(h, root, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var owned = new System.Collections.Concurrent.ConcurrentBag<string>();
        int done = 0;
        int total = candidates.Count;
        using var gate = new SemaphoreSlim(12);
        var tasks = candidates.Select(async host =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Cheap DNS gate first; only pay for a TLS handshake on hosts that actually resolve.
                if (await ResolvesAsync(host, ct).ConfigureAwait(false) &&
                    await BelongsToBrandAsync(host, brand, ct).ConfigureAwait(false))
                    owned.Add(host);
            }
            finally
            {
                gate.Release();
                int n = Interlocked.Increment(ref done);
                var hits = owned.OrderBy(x => x.Length).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).Take(6).ToList();
                string tail = hits.Count == 0 ? "" :
                    " · нашёл: " + string.Join(", ", hits) + (owned.Count > hits.Count ? "…" : "");
                progress?.Report($"Проверяю зоны бренда: {n}/{total}{tail}");
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return owned.ToList();
    }

    /// <summary>Verifies <paramref name="host"/> really belongs to the brand (not a squatter) by reading
    /// the TLS certificate it serves: a genuine sibling (yandex.kz → CN/SAN <c>*.yandex.kz</c>) carries the
    /// brand label; a parking/other-owner cert (sedoparking.com …) does not. The cert is only READ, never
    /// trusted, so a self-signed/blocked edge doesn't throw before we can inspect it. A failed handshake or
    /// a brand-less cert ⇒ reject.</summary>
    private static async Task<bool> BelongsToBrandAsync(string host, string brand, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, 443, cts.Token).ConfigureAwait(false);

            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = host }, cts.Token).ConfigureAwait(false);

            return ssl.RemoteCertificate is X509Certificate2 cert && CertMentionsBrand(cert, brand);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    /// <summary>True when the certificate's subject DN or SAN mentions the brand as a whole domain/DN
    /// label — bounded by non-alphanumerics so short brands like "vk" match "vk.kz"/"*.vk.kz" but not the
    /// middle of "network".</summary>
    private static bool CertMentionsBrand(X509Certificate2 cert, string brand)
    {
        var hay = new StringBuilder();
        hay.Append(cert.Subject).Append('\n');          // CN=…, O=…
        foreach (var ext in cert.Extensions)
            if (ext.Oid?.Value == "2.5.29.17")           // subjectAltName
                hay.Append(ext.Format(false)).Append('\n');
        string s = hay.ToString().ToLowerInvariant();
        return Regex.IsMatch(s, $@"(^|[^a-z0-9]){Regex.Escape(brand)}([^a-z0-9]|$)");
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
