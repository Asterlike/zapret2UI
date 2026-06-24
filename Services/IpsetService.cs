using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace ZapretUI.Services;

/// <summary>
/// Builds an IP-set for IP-based bypass (the lever for hard IP-blocks where
/// domain/SNI matching is useless). It resolves the Discord domains with the
/// bundled <c>mdig.exe</c> and aggregates the addresses into CIDR subnets with
/// <c>ip2net.exe</c> — both userspace tools, no admin needed.
/// </summary>
public sealed class IpsetService
{
    public sealed record IpsetResult(string Path, int Subnets);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Official Telegram DC ranges feed (CIDR, one per line, IPv4+IPv6).</summary>
    private const string TelegramCidrUrl = "https://core.telegram.org/resources/cidr.txt";

    /// <summary>Offline fallback — Telegram's published DC ranges (snapshot, June 2026).</summary>
    private static readonly string[] TelegramCidrFallback =
    {
        "91.108.56.0/22", "91.108.4.0/22", "91.108.8.0/22", "91.108.16.0/22", "91.108.12.0/22",
        "149.154.160.0/20", "91.105.192.0/23", "91.108.20.0/22", "185.76.151.0/24",
        "2001:b28:f23d::/48", "2001:b28:f23f::/48", "2001:67c:4e8::/48", "2001:b28:f23c::/48",
        "2a0a:f280::/32",
    };

    /// <summary>Seed ipset-telegram.txt from the embedded snapshot on first run, so the
    /// Telegram-by-IP profile works out of the box (the «Собрать» button refreshes it live).</summary>
    public static void SeedTelegramDefault()
    {
        try
        {
            string path = AppPaths.IpsetFile("telegram");
            if (File.Exists(path)) return;
            Directory.CreateDirectory(AppPaths.ListsDir);
            File.WriteAllText(path, string.Join("\n", TelegramCidrFallback) + "\n");
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Build the Telegram ipset from the official CIDR feed (no DNS — these are the
    /// MTProto data-center ranges, not CDN). Falls back to the embedded snapshot offline.
    /// </summary>
    public async Task<IpsetResult> BuildTelegramIpsetAsync(CancellationToken ct)
    {
        List<string> lines;
        try
        {
            string text = await Http.GetStringAsync(TelegramCidrUrl, ct);
            lines = text.Replace("\r\n", "\n").Split('\n')
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0 && !s.StartsWith('#'))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
            if (lines.Count == 0) lines = TelegramCidrFallback.ToList();
        }
        catch
        {
            lines = TelegramCidrFallback.ToList();
        }

        Directory.CreateDirectory(AppPaths.ListsDir);
        string path = AppPaths.IpsetFile("telegram");
        await File.WriteAllTextAsync(path, string.Join("\n", lines) + "\n", ct);
        return new IpsetResult(path, lines.Count);
    }

    /// <summary>
    /// Resolve a single MTProxy host to its IP(s) and write ipset-proxy.txt, so a combo profile
    /// can scope the ee-proxy ClientHello desync to ONLY that destination IP — keeping YouTube/
    /// Discord/general TLS untouched (the catch-all conflict we hit when applying it broadly).
    /// Plain DNS, no admin and no external tools. Accepts a bare IP literal too.
    /// </summary>
    public async Task<IpsetResult> BuildProxyIpsetAsync(string host, CancellationToken ct)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0) throw new InvalidOperationException("Хост прокси не задан.");

        System.Net.IPAddress[] addrs;
        if (System.Net.IPAddress.TryParse(host, out var literal))
            addrs = new[] { literal };
        else
            addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct);

        var lines = addrs.Select(a => a.ToString())
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList();
        if (lines.Count == 0)
            throw new InvalidOperationException("Не удалось разрезолвить хост прокси (DNS-блокировка?).");

        Directory.CreateDirectory(AppPaths.ListsDir);
        string path = AppPaths.IpsetFile("proxy");
        await File.WriteAllTextAsync(path, string.Join("\n", lines) + "\n", ct);
        return new IpsetResult(path, lines.Count);
    }

    /// <summary>Resolve <paramref name="domains"/> and write an aggregated ipset file. Returns the path + subnet count.</summary>
    public async Task<IpsetResult> BuildDiscordIpsetAsync(IEnumerable<string> domains, CancellationToken ct)
    {
        if (!File.Exists(AppPaths.MdigExe) || !File.Exists(AppPaths.Ip2NetExe))
            throw new FileNotFoundException("mdig.exe / ip2net.exe не найдены — дождитесь загрузки движка.");

        var list = domains.Select(d => d.Trim())
                          .Where(d => d.Length > 0 && !d.StartsWith('#'))
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();
        if (list.Count == 0) throw new InvalidOperationException("Список доменов Discord пуст.");

        string ips = await ResolveAsync(list, ct);
        if (string.IsNullOrWhiteSpace(ips))
            throw new InvalidOperationException("Не удалось разрезолвить ни одного домена (DNS-блокировка?).");

        string subnets = await AggregateAsync(ips, ct);
        var lines = subnets.Replace("\r\n", "\n").Split('\n')
                           .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        Directory.CreateDirectory(AppPaths.ListsDir);
        await File.WriteAllTextAsync(AppPaths.IpsetDiscordFile, string.Join("\n", lines) + "\n", ct);
        return new IpsetResult(AppPaths.IpsetDiscordFile, lines.Count);
    }

    private static Task<string> ResolveAsync(IReadOnlyList<string> domains, CancellationToken ct) =>
        RunPipeAsync(AppPaths.MdigExe, "--family=4", string.Join("\n", domains) + "\n", ct);

    private static Task<string> AggregateAsync(string ips, CancellationToken ct) =>
        RunPipeAsync(AppPaths.Ip2NetExe, "-4", ips, ct);

    /// <summary>Run a tool feeding <paramref name="stdin"/> and returning its stdout.</summary>
    private static async Task<string> RunPipeAsync(string exe, string args, string stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = AppPaths.EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.ASCII,
            StandardInputEncoding = Encoding.ASCII, // match stdout: domains/IPs are ASCII, no code-page mojibake
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        try
        {
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
            string outp = await outTask;
            await p.WaitForExitAsync(ct);
            return outp;
        }
        finally
        {
            // Disposing a Process doesn't terminate it: on cancellation/exception kill the child
            // (mdig/ip2net) so it can't linger after we've stopped reading its output.
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }
}
