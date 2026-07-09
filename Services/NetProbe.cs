using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Zapret2UI.Models;

namespace Zapret2UI.Services;

/// <summary>Verdict of a DPI-signature probe: is the provider actively interfering by SNI, or not?</summary>
public enum DpiVerdict
{
    Clean,          // TLS ClientHello with the real SNI completed — no DPI drop on this host
    Reset,          // connection reset (RST) mid-handshake — classic DPI reset injection
    Freeze,         // ClientHello sent, no answer — the SNI-bearing packet is being dropped/frozen
    NoConnection,   // TCP never connected — a routing/IP issue, not name-based DPI
}

/// <summary>
/// Low-level connectivity probes shared by the diagnostics matrix and the
/// auto-selector. Each probe is a single real attempt against a host: a browser-like
/// HTTP/2 reachability check on 443, a TLS handshake (forced version) on 443, or a latency
/// ping. They return <see cref="DiagStatus"/> so callers can both display and score.
/// </summary>
public static class NetProbe
{
    public static async Task<(bool ok, long ms)> PingAsync(string host, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(2), cancellationToken: ct);
            if (reply.Status == IPStatus.Success) return (true, reply.RoundtripTime);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* ICMP often filtered for CDNs — fall back to TCP connect latency */ }

        var sw = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(host, 443, cts.Token);
            sw.Stop();
            return (true, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (false, 0); }
    }

    /// <summary>
    /// Plain TCP-connect reachability to host:port. Used for protocols without TLS/SNI
    /// (e.g. a Telegram MTProto data-center IP) — it answers "is the endpoint reachable",
    /// NOT "is it throttled" (throttling still lets the connect succeed).
    /// </summary>
    public static async Task<DiagStatus> TcpAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(host, port, cts.Token);
            return DiagStatus.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return DiagStatus.Timeout; }
        catch { return DiagStatus.Fail; }
    }

    // Chrome UA — some DPIs treat a non-browser User-Agent differently, and blockcheck uses "Mozilla"
    // for the same reason. The point of ReachAsync is to be as browser-like as .NET allows.
    private const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/126.0.0.0 Safari/537.36";

    // Russian DPI stub-page fingerprints. On HTTPS a provider can't inject a stub without breaking TLS
    // (no valid cert), so a block is normally an RST — but if a transparent proxy ever returns one of
    // these in the body, it's a block page, not the site. All lowercase (compared against a lowercased body).
    private static readonly string[] BlockMarkers =
    {
        "доступ ограничен", "доступ заблокирован", "ресурс заблокирован", "единый реестр",
        "запрещен на территории", "warning.rt.ru", "eais.rkn.gov.ru", "blocked by",
    };

    /// <summary>Per-host check recipe: the real URL a browser/client hits, plus a body marker that
    /// only the genuine service returns. A DPI stub or a truncated/reset response won't contain the
    /// marker → honest ✗, instead of the old "any bytes starting with HTTP/ = OK" false positive.
    /// Hosts without a recipe fall back to a plain GET of their root (marker = null → any completed
    /// response over a clean TLS session counts as reachable).</summary>
    private static (string url, string? marker) Recipe(string host) => host.ToLowerInvariant() switch
    {
        // Discord's own login page — exactly what "на сайт/в логин не пускает" is about. Served by
        // Cloudflare, not rate-limited, always contains "discord" in title/meta/script URLs.
        "discord.com" => ("https://discord.com/login", "discord"),
        // The YouTube web shell always embeds the ytcfg bootstrap object.
        "www.youtube.com" => ("https://www.youtube.com/", "ytcfg"),
        _ => ($"https://{host}/", null),
    };

    /// <summary>
    /// Real browser-like reachability on 443: an HTTP/2 request (ALPN h2/http1.1, Chrome User-Agent,
    /// certificate ignored) to the host's real endpoint, then reading the BODY and checking a
    /// service-specific marker. This is what makes a green result mean "the page actually loads",
    /// not just "TLS handshook":
    ///   • the ALPN/HTTP-2 ClientHello is far closer to a browser than a bare SslStream (which winws2
    ///     desyncs differently — a mismatched ClientHello size shifts every split position);
    ///   • a mid-stream RST (headers OK, then reset — the Discord-login signature) throws during the
    ///     body read → Fail;
    ///   • a stub/blocked body fails the marker or matches a block fingerprint → Fail.
    /// Managed HTTP/2 over TCP — no native deps, works in single-file (unlike the removed QUIC probe).
    /// </summary>
    public static async Task<DiagStatus> ReachAsync(string host, CancellationToken ct)
    {
        var (url, marker) = Recipe(host);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,   // on HTTPS a real block is an RST, not a redirect; keep redirects visible
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, _, _, _) => true, // identity irrelevant; the DPI reset is the signal
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    SslApplicationProtocol.Http2, SslApplicationProtocol.Http11, // ALPN like a browser
                },
            },
        };
        try
        {
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            using var req = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/json,*/*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,ru;q=0.8");

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            // Read a chunk of the BODY under the same token so a mid-stream RST throws here (→ Fail).
            // Use a STATEFUL decoder so a multi-byte UTF-8 char split across two socket reads can't
            // corrupt a (Cyrillic) block-page marker at the chunk boundary.
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            var decoder = Encoding.UTF8.GetDecoder();
            var buf = new byte[16384];
            var chars = new char[16384];
            var sb = new StringBuilder();
            int total = 0, n;
            while (total < 65536 && (n = await stream.ReadAsync(buf, cts.Token)) > 0)
            {
                total += n;
                int c = decoder.GetChars(buf, 0, n, chars, 0, flush: false);
                sb.Append(chars, 0, c);
                if (marker is not null && sb.ToString().Contains(marker, StringComparison.OrdinalIgnoreCase)) break;
            }
            string lower = sb.ToString().ToLowerInvariant();

            if (BlockMarkers.Any(m => lower.Contains(m))) return DiagStatus.Fail;        // provider stub page
            if ((int)resp.StatusCode is >= 300 and < 400) return DiagStatus.Ok;          // HTTPS 3xx = server-origin (DPI can't forge a valid-cert redirect) → reachable
            if (marker is not null) return lower.Contains(marker) ? DiagStatus.Ok : DiagStatus.Fail;
            return DiagStatus.Ok; // no-marker host: a completed HTTP response over a clean TLS session = reachable
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return DiagStatus.Timeout; }
        catch { return DiagStatus.Fail; }
    }

    /// <summary>Full per-host probe used by the auto-selector, the generator and the no-bypass baseline:
    /// TLS 1.2 + TLS 1.3 handshakes and a real HTTP/2 reachability check, run in parallel.</summary>
    public static async Task<AutoHostResult> ProbeHostAsync(string host, CancellationToken ct)
    {
        var p12 = TlsAsync(host, SslProtocols.Tls12, ct);
        var p13 = TlsAsync(host, SslProtocols.Tls13, ct);
        var pr = ReachAsync(host, ct);
        await Task.WhenAll(p12, p13, pr).ConfigureAwait(false);
        return new AutoHostResult(host, p12.Result, p13.Result, pr.Result);
    }

    public static async Task<DiagStatus> TlsAsync(string host, SslProtocols proto, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(host, 443, cts.Token);

            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            var opts = new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = proto };
            await ssl.AuthenticateAsClientAsync(opts, cts.Token);
            return DiagStatus.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return DiagStatus.Timeout; }
        catch (PlatformNotSupportedException) { return DiagStatus.NotSupported; }
        catch (AuthenticationException) { return DiagStatus.Fail; }
        catch { return DiagStatus.Fail; }
    }

    /// <summary>DPI-signature probe: does the provider actively block this host by SNI? First a plain
    /// TCP-connect on 443 (if that already fails it's routing/IP, not name-DPI); then a TLS ClientHello
    /// with the REAL SNI. Because the server already completed the TCP handshake, a reset (RST) or a
    /// silent drop on the ClientHello is a middlebox injecting — the classic Russian DPI signature.</summary>
    public static async Task<DpiVerdict> DpiProbeAsync(string host, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await tcp.ConnectAsync(host, 443, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return DpiVerdict.NoConnection; } // TCP never connected → routing/IP, not SNI-DPI

        // Server accepted the connection, so it's up. Now the real-SNI ClientHello: a reset or a freeze
        // on THIS packet is the DPI signature (the origin already completed the TCP handshake).
        try
        {
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            var opts = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            };
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(TimeSpan.FromSeconds(4));
            await ssl.AuthenticateAsClientAsync(opts, hs.Token);
            return DpiVerdict.Clean;                                     // handshake completed → no DPI drop
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return DpiVerdict.Freeze; }  // ClientHello sent, no answer → dropped
        catch (AuthenticationException) { return DpiVerdict.Clean; }      // server answered at TLS level → reached
        catch (IOException ioe) when (IsConnectionReset(ioe)) { return DpiVerdict.Reset; }
        catch (SocketException se) when (se.SocketErrorCode == SocketError.ConnectionReset) { return DpiVerdict.Reset; }
        catch { return DpiVerdict.Reset; }                               // abrupt failure after a good TCP connect → DPI
    }

    private static bool IsConnectionReset(IOException ioe) =>
        ioe.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }
        || ioe.Message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
        || ioe.Message.Contains("reset", StringComparison.OrdinalIgnoreCase);
}
