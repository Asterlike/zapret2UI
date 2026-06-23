using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Low-level connectivity probes shared by the diagnostics matrix and the
/// auto-selector. Each probe is a single real attempt against a host: a full HTTPS
/// GET on 443, a TLS handshake (forced version) on 443, or a latency
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

    /// <summary>
    /// Full HTTPS GET on 443: TLS handshake + a real request + reading a chunk of the BODY.
    /// Many DPIs let the handshake and headers through, then RST the connection mid-stream (the
    /// Discord-login signature: "200 (OK)" then ERR_CONNECTION_RESET / HTTP2_PING_FAILED). Peeking
    /// only the status line therefore false-positives. We keep reading ~16 KB: a mid-stream reset
    /// surfaces as a thrown read = Fail. (Still not a 100% login guarantee — login is a stateful
    /// POST + JS-challenge flow; a reset that fires after 16 KB or only on POSTs can slip through.)
    /// </summary>
    public static async Task<DiagStatus> HttpsAsync(string host, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            await tcp.ConnectAsync(host, 443, cts.Token);

            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);

            byte[] req = Encoding.ASCII.GetBytes(
                $"GET / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: ZapretUI\r\nConnection: close\r\n\r\n");
            await ssl.WriteAsync(req, cts.Token);

            var buf = new byte[8192];
            int first = await ssl.ReadAsync(buf, cts.Token);
            if (first <= 0 || !Encoding.ASCII.GetString(buf, 0, first).StartsWith("HTTP/", StringComparison.Ordinal))
                return DiagStatus.Fail;

            // Keep pulling the body so a mid-stream RST throws here (→ Fail) instead of a false OK.
            int total = first;
            while (total < 16384)
            {
                int n = await ssl.ReadAsync(buf, cts.Token);
                if (n <= 0) break; // clean EOF (server closed after Connection: close)
                total += n;
            }
            return DiagStatus.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return DiagStatus.Timeout; }
        catch { return DiagStatus.Fail; }
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
}
