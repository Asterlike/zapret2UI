using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Zapret2UI.Localization;

namespace Zapret2UI.Services.Network;

/// <summary>
/// HTTP whose connections survive a poisoned resolver.
///
/// <para>Every hostname is resolved over DoH first and through the OS resolver second, then dialled at
/// the first address that answers. This is what makes a download survive the common Russian failure
/// where the page opens but the file host does not: the ISP poisons that one name in the system
/// resolver, while browsers — which do their own DoH — still reach it. On a healthy network DoH returns
/// the same addresses and nothing changes.</para>
///
/// <para>TLS is still negotiated by the handler afterwards with the real hostname, so SNI and
/// certificate validation are untouched: this bypasses a bad DNS answer, it does not weaken
/// verification.</para>
///
/// <para>Shared by the engine updater and the Tor download, which need exactly the same thing for
/// exactly the same reason — <c>github.com</c> and <c>torproject.org</c> are both names an ISP is
/// likelier to spoil than to route away.</para>
/// </summary>
internal static class DohHttp
{
    private static readonly DohResolver Resolver = new();

    /// <summary>An <see cref="HttpClient"/> that dials through <see cref="ConnectAsync"/>.</summary>
    internal static HttpClient Create(TimeSpan timeout) =>
        new(new SocketsHttpHandler { ConnectCallback = ConnectAsync }) { Timeout = timeout };

    /// <summary>The same, but through a SOCKS5 proxy on loopback — which is how a download reaches a
    /// site this network refuses to carry at all.
    ///
    /// <para>No DoH here, and that is the point: the proxy resolves the name at its own end, so neither
    /// the ISP's resolver nor its DPI ever sees which host is being fetched.</para></summary>
    internal static HttpClient CreateThrough(string socks5HostPort, TimeSpan timeout) =>
        new(new SocketsHttpHandler
        {
            Proxy = new WebProxy("socks5://" + socks5HostPort),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        { Timeout = timeout };

    /// <summary>Resolve over DoH, then the OS, and connect to the first address that answers.</summary>
    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        string host = ctx.DnsEndPoint.Host;
        int port = ctx.DnsEndPoint.Port;

        var candidates = new List<IPAddress>();
        if (IPAddress.TryParse(host, out var literal))
            candidates.Add(literal); // already an IP — no resolution needed
        else
        {
            try
            {
                foreach (var ip in await Resolver.ResolveAsync(host, ct).ConfigureAwait(false))
                    if (IPAddress.TryParse(ip, out var addr)) candidates.Add(addr);
            }
            catch { /* DoH unavailable → OS resolver below */ }

            try
            {
                foreach (var addr in await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false))
                    if (!candidates.Contains(addr)) candidates.Add(addr);
            }
            catch { /* OS resolver failed too — any DoH candidates may still connect */ }
        }

        if (candidates.Count == 0)
            throw new IOException(Loc.T(
                "Не удалось определить адрес {0} (ни DoH, ни системный DNS не ответили).", host));

        Exception? last = null;
        foreach (var addr in candidates)
        {
            var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(addr, port, connectCts.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                last = ex;
                socket.Dispose();
            }
        }
        throw last ?? new IOException(Loc.T("Не удалось подключиться к {0}.", host));
    }
}
