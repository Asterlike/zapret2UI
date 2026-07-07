using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace ZapretUI.Services;

/// <summary>
/// Minimal DNS-over-HTTPS resolver (RFC 8484 JSON form). Resolves a hostname to IPv4 addresses via
/// Cloudflare/Google endpoints addressed by IP literal — so it keeps working when the ISP resolver
/// poisons or blocks the lookup (the usual reason the built-in Telegram proxy "pings but won't
/// connect" on mobile carriers). Returns an empty array on any failure; the caller then falls back to
/// the OS resolver. Positive results are cached ~5 min so a burst of proxy connections doesn't
/// re-query per socket, and a fully-unreachable DoH is remembered for 30 s so failures stay fast.
/// </summary>
public sealed class DohResolver : IDisposable
{
    // JSON DoH endpoints reachable by IP literal (no bootstrap DNS needed): their TLS certs carry the
    // IP as a SAN, so validation passes. Cloudflare answers JSON at /dns-query, Google at /resolve.
    private static readonly string[] Endpoints =
    {
        "https://1.1.1.1/dns-query",
        "https://8.8.8.8/resolve",
    };

    private readonly HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(4) })
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    private readonly ConcurrentDictionary<string, (string[] Ips, long ExpiresTick)> _cache = new();
    private long _dohDownUntil; // TickCount64 until which DoH is presumed blocked (skip → OS resolver)

    /// <summary>Resolve <paramref name="host"/> to IPv4 strings via DoH; empty array on failure.</summary>
    public async Task<string[]> ResolveAsync(string host, CancellationToken ct)
    {
        if (_cache.TryGetValue(host, out var hit) && hit.ExpiresTick > Environment.TickCount64)
            return hit.Ips;
        if (Environment.TickCount64 < Volatile.Read(ref _dohDownUntil))
            return Array.Empty<string>();

        bool anyReachable = false;
        foreach (string url in Endpoints)
        {
            try
            {
                string[] ips = await QueryAsync(url, host, ct).ConfigureAwait(false);
                anyReachable = true; // got an HTTP response (even 0 answers) → the endpoint itself is up
                if (ips.Length > 0)
                {
                    _cache[host] = (ips, Environment.TickCount64 + 300_000);
                    return ips;
                }
            }
            catch { /* endpoint blocked/timed out → try the next one */ }
        }

        // Both endpoints unreachable → the network is eating DoH too; skip it briefly so subsequent
        // resolves don't each pay two connect timeouts before falling back to the OS resolver.
        if (!anyReachable) Volatile.Write(ref _dohDownUntil, Environment.TickCount64 + 30_000);
        return Array.Empty<string>();
    }

    private async Task<string[]> QueryAsync(string url, string host, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}?name={Uri.EscapeDataString(host)}&type=A");
        req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return Array.Empty<string>();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("Answer", out var answer) || answer.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var ips = new List<string>();
        foreach (var a in answer.EnumerateArray())
            if (a.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number && t.GetInt32() == 1 &&
                a.TryGetProperty("data", out var d) && d.GetString() is { Length: > 0 } ip)
                ips.Add(ip);
        return ips.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
