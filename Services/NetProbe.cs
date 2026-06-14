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
/// auto-selector. Each probe is a single real attempt against a host: an HTTP
/// request on port 80, a TLS handshake (forced version) on 443, or a latency
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

    public static async Task<DiagStatus> HttpAsync(string host, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(host, 80, cts.Token);

            var stream = tcp.GetStream();
            byte[] req = Encoding.ASCII.GetBytes(
                $"GET / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: ZapretUI\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(req, cts.Token);

            var buf = new byte[64];
            int read = await stream.ReadAsync(buf, cts.Token);
            string head = Encoding.ASCII.GetString(buf, 0, Math.Max(0, read));
            return read > 0 && head.StartsWith("HTTP/", StringComparison.Ordinal)
                ? DiagStatus.Ok : DiagStatus.Fail;
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
