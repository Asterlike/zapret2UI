using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Zapret2UI.Localization;

namespace Zapret2UI.Services.Warp;

/// <summary>A SOCKS5 proxy to reach Cloudflare through: address, port and — when the proxy asks for
/// them — a login and password.</summary>
/// <param name="Host">Host name or address. Usually <c>127.0.0.1</c>: the port a VLESS/Shadowsocks
/// client already listens on locally.</param>
/// <param name="Port">The proxy's port.</param>
/// <param name="User">Login, empty when the proxy wants none.</param>
/// <param name="Password">Password, empty when the proxy wants none.</param>
internal readonly record struct ProxyEndpoint(string Host, int Port, string User, string Password)
{
    internal bool HasCredentials => User.Length > 0;

    /// <summary>Address only, never the credentials — this string ends up in the journal.</summary>
    public override string ToString() =>
        (Host.Contains(':') ? "[" + Host + "]" : Host) + ":" + Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>Read what the user typed. Deliberately forgiving about the shapes people actually paste —
    /// <c>127.0.0.1:10808</c>, <c>socks5://host:1080</c>, <c>socks5://user:pass@host:1080</c>,
    /// <c>[::1]:1080</c> — and deliberately strict about the protocol: an <c>http://</c> or a
    /// <c>vless://</c> address here would fail later as a silent connection refusal, which is the worst
    /// possible moment to learn about a typo.</summary>
    internal static bool TryParse(string text, out ProxyEndpoint endpoint, out string error)
    {
        endpoint = default;
        error = "";

        string s = text.Trim();
        if (s.Length == 0)
        {
            error = Loc.T("Адрес прокси не указан.");
            return false;
        }

        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            string name = s[..scheme].ToLowerInvariant();
            if (name is not ("socks5" or "socks5h" or "socks"))
            {
                error = Loc.T("Нужен SOCKS5-прокси, а «{0}://» — это другой протокол. У клиентов VLESS и "
                              + "Shadowsocks порт SOCKS5 обычно открыт на 127.0.0.1.", name);
                return false;
            }
            s = s[(scheme + 3)..];
        }

        // Trailing slash from a copied URL, and any path after it: neither means anything to a proxy.
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];

        string user = "", pass = "";
        int at = s.LastIndexOf('@');
        if (at >= 0)
        {
            string creds = s[..at];
            s = s[(at + 1)..];
            int mark = creds.IndexOf(':');
            user = mark >= 0 ? creds[..mark] : creds;
            pass = mark >= 0 ? creds[(mark + 1)..] : "";
            if (user.Length == 0)
            {
                error = Loc.T("В адресе есть «@», но логин перед ним пустой.");
                return false;
            }
        }

        string host, portText;
        if (s.StartsWith('['))                                  // [::1]:1080
        {
            int close = s.IndexOf(']');
            if (close < 0)
            {
                error = Loc.T("В адресе не закрыта скобка: ожидается вид [::1]:1080.");
                return false;
            }
            host = s[1..close];
            portText = s[(close + 1)..].TrimStart(':');
        }
        else
        {
            int mark = s.LastIndexOf(':');
            if (mark < 0)
            {
                error = Loc.T("Укажите адрес вместе с портом, например 127.0.0.1:10808.");
                return false;
            }
            host = s[..mark];
            portText = s[(mark + 1)..];
        }

        if (host.Length == 0)
        {
            error = Loc.T("Укажите адрес вместе с портом, например 127.0.0.1:10808.");
            return false;
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535)
        {
            error = Loc.T("«{0}» — это не порт. Порт — число от 1 до 65535.", portText);
            return false;
        }

        endpoint = new ProxyEndpoint(host, port, user, pass);
        return true;
    }
}

/// <summary>
/// A loopback listener that hands every connection it accepts to the user's SOCKS5 proxy, asking it for
/// one fixed destination: Cloudflare's MASQUE entry point.
///
/// <para><b>Why this exists at all.</b> usque has no upstream-proxy option — it dials the address in its
/// config directly. The address in the config can be changed, though, and it is the only thing the
/// client checks by address: the server itself is verified by a pinned key, so a relay in the middle is
/// invisible to that check. Pointing usque at <c>127.0.0.1</c> and putting this in the way is therefore
/// the whole trick, and it needs no changes to usque and no elevation.</para>
///
/// <para><b>Why it is worth the trouble.</b> Measured: Cloudflare picks the exit country from the address
/// that dials it. Dial from Germany and WARP hands out a German exit — which is what turns «меняет
/// репутацию адреса» into «меняет страну», and it is the only way the AI services that refuse by country
/// can be reached at all. The proxy at the far end is the user's own; we only carry bytes to it.</para>
///
/// <para><b>TCP only, by construction.</b> SOCKS5 CONNECT is a TCP tunnel, so MASQUE has to run over its
/// HTTP/2 transport rather than QUIC. That is not a limitation we chose — it is the shape of the
/// protocol, and it is why the sweep drops its QUIC attempts when an ingress proxy is set.</para>
/// </summary>
internal sealed class UpstreamRelay : IDisposable
{
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _lastError = "";

    /// <summary>Raised for anything worth seeing in the journal. A connection failing here looks exactly
    /// like Cloudflare being unreachable unless it says which leg broke, so it says.</summary>
    public event Action<string>? LogLine;

    /// <summary>The loopback port usque should be pointed at. 0 until <see cref="Start"/> succeeds.</summary>
    public int Port { get; private set; }

    public bool IsRunning
    {
        get { lock (_lock) return _listener is not null; }
    }

    /// <summary>Why the last connection through the proxy failed, in the user's language. Empty until
    /// one does.</summary>
    public string LastError
    {
        get { lock (_lock) return _lastError; }
    }

    /// <summary>Connections the proxy actually accepted and tunnelled for us.
    ///
    /// <para>This is the proof that the chain is a chain. usque has exactly one endpoint — this loopback
    /// port — so a tunnel that came up while this stayed at zero would mean Cloudflare was reached some
    /// other way, and the country on the screen would be telling the user a story about a route nothing
    /// took. Counted after the proxy's own CONNECT succeeds: an accepted socket proves nothing about the
    /// far end.</para></summary>
    public long Carried => Interlocked.Read(ref _carried);

    private long _carried;
    private long _stream;
    private bool _isolate;

    /// <summary>A copy of the upstream with a login nothing else will use, or the upstream unchanged.
    ///
    /// <para><b>Why this exists.</b> Tor keeps streams that share a SOCKS login on ONE circuit, and
    /// everything here dials the same destination, so several MASQUE sessions would otherwise queue
    /// behind each other on a single circuit and gain nothing at all. Tor's own answer to that is
    /// <c>IsolateSOCKSAuth</c>, on by default: a different login means a different circuit. The login is
    /// a label, not a secret — tor's SOCKS port asks for no password and accepts any.</para>
    ///
    /// <para>Only ever applied when the upstream has no credentials of its own: those belong to the
    /// user's proxy and replacing them would turn a working chain into a refused one.</para></summary>
    internal ProxyEndpoint Isolated(ProxyEndpoint upstream)
    {
        if (!_isolate || upstream.HasCredentials) return upstream;

        long n = Interlocked.Increment(ref _stream);
        return upstream with
        {
            User = "hms" + n.ToString(CultureInfo.InvariantCulture),
            Password = "x",
        };
    }

    /// <summary>Bind a loopback port and start forwarding. The port is chosen by the OS rather than
    /// fixed: nothing else needs to know it, and a hard-coded one is a collision waiting to happen on
    /// a machine that already runs a proxy client.</summary>
    /// <param name="isolate">Give every connection a login of its own, so Tor puts each one on its own
    /// circuit. See <see cref="Isolated"/> for why that is the difference between three sessions and
    /// one.</param>
    public bool Start(ProxyEndpoint upstream, string targetHost, int targetPort, out string error,
                      bool isolate = false)
    {
        _isolate = isolate;
        error = "";
        Stop();

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var cts = new CancellationTokenSource();
            lock (_lock)
            {
                _listener = listener;
                _cts = cts;
                _lastError = "";
                Interlocked.Exchange(ref _carried, 0);      // each attempt counts for itself
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            _ = Task.Run(() => AcceptAsync(listener, upstream, targetHost, targetPort, cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
            Port = 0;
        }

        try { cts?.Cancel(); } catch { /* nothing left to cancel */ }
        try { listener?.Stop(); } catch { /* already down */ }
        cts?.Dispose();
    }

    public void Dispose() => Stop();

    // ---- forwarding --------------------------------------------------------

    private async Task AcceptAsync(TcpListener listener, ProxyEndpoint upstream,
                                   string targetHost, int targetPort, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }

            _ = Task.Run(() => HandleAsync(client, upstream, targetHost, targetPort, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient inbound, ProxyEndpoint upstream,
                                   string targetHost, int targetPort, CancellationToken ct)
    {
        using var client = inbound;
        using var outbound = new TcpClient();

        try
        {
            client.NoDelay = true;
            outbound.NoDelay = true;

            // A proxy that accepts the connection and then says nothing would otherwise hold the whole
            // start-up wait open; the tunnel it is meant to carry has its own timeout downstream.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));

            await outbound.ConnectAsync(upstream.Host, upstream.Port, deadline.Token).ConfigureAwait(false);
            NetworkStream up = outbound.GetStream();
            await Socks5ConnectAsync(up, Isolated(upstream), targetHost, targetPort, deadline.Token)
                  .ConfigureAwait(false);
            Interlocked.Increment(ref _carried);

            NetworkStream down = client.GetStream();
            await Task.WhenAny(PumpAsync(down, up, ct), PumpAsync(up, down, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutting down, or the deadline above */ }
        catch (Exception ex) { Fail(ex.Message); }
    }

    /// <summary>Copy until one side is done, then half-close the other so the far end sees a clean end of
    /// stream rather than a connection that simply stops answering.</summary>
    private static async Task PumpAsync(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        try { await from.CopyToAsync(to, 32 * 1024, ct).ConfigureAwait(false); }
        catch { /* the other direction reports the failure; this one just ends */ }
        try { to.Socket.Shutdown(SocketShutdown.Send); } catch { /* already closed */ }
    }

    private void Fail(string message)
    {
        lock (_lock)
        {
            // usque redials on its own, so a proxy that is simply down would otherwise fill the journal
            // with the same line. One line per distinct cause is what makes it readable.
            if (string.Equals(_lastError, message, StringComparison.Ordinal)) return;
            _lastError = message;
        }
        LogLine?.Invoke(Loc.T("[masque] прокси входа: {0}", message));
    }

    // ---- SOCKS5 (RFC 1928 + RFC 1929) --------------------------------------

    private static async Task Socks5ConnectAsync(NetworkStream stream, ProxyEndpoint proxy,
                                                 string host, int port, CancellationToken ct)
    {
        // Greeting: version 5, and the methods we can actually perform.
        byte[] greeting = proxy.HasCredentials ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 };
        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);

        byte[] choice = await ReadExactlyAsync(stream, 2, ct).ConfigureAwait(false);
        if (choice[0] != 5) throw new IOException(Loc.T("ответ не похож на SOCKS5"));

        switch (choice[1])
        {
            case 0x00:
                break;
            case 0x02:
                if (!proxy.HasCredentials) throw new IOException(Loc.T("прокси требует логин и пароль"));
                await AuthenticateAsync(stream, proxy, ct).ConfigureAwait(false);
                break;
            case 0xFF:
                throw new IOException(proxy.HasCredentials
                    ? Loc.T("прокси отказал и логину с паролем, и входу без них")
                    : Loc.T("прокси не пускает без логина и пароля"));
            default:
                throw new IOException(Loc.T("прокси предлагает способ входа, который здесь не поддерживается ({0})", choice[1]));
        }

        await stream.WriteAsync(BuildConnect(host, port), ct).ConfigureAwait(false);

        byte[] reply = await ReadExactlyAsync(stream, 4, ct).ConfigureAwait(false);
        if (reply[1] != 0) throw new IOException(DescribeReply(reply[1]));

        // The bound address comes back after the reply and has to be read off the stream before the
        // tunnel starts, or its bytes would be handed to usque as if Cloudflare had sent them.
        int rest = reply[3] switch
        {
            1 => 4,
            4 => 16,
            3 => (await ReadExactlyAsync(stream, 1, ct).ConfigureAwait(false))[0],
            _ => throw new IOException(Loc.T("прокси вернул адрес непонятного вида")),
        };
        await ReadExactlyAsync(stream, rest + 2, ct).ConfigureAwait(false);
    }

    private static async Task AuthenticateAsync(NetworkStream stream, ProxyEndpoint proxy, CancellationToken ct)
    {
        byte[] user = Encoding.UTF8.GetBytes(proxy.User);
        byte[] pass = Encoding.UTF8.GetBytes(proxy.Password);
        if (user.Length > 255 || pass.Length > 255)
            throw new IOException(Loc.T("логин или пароль длиннее, чем допускает SOCKS5"));

        var packet = new byte[3 + user.Length + pass.Length];
        packet[0] = 1;                                    // the auth sub-negotiation has its own version
        packet[1] = (byte)user.Length;
        user.CopyTo(packet, 2);
        packet[2 + user.Length] = (byte)pass.Length;
        pass.CopyTo(packet, 3 + user.Length);

        await stream.WriteAsync(packet, ct).ConfigureAwait(false);
        byte[] answer = await ReadExactlyAsync(stream, 2, ct).ConfigureAwait(false);
        if (answer[1] != 0) throw new IOException(Loc.T("прокси не принял логин или пароль"));
    }

    /// <summary>CONNECT, addressed the way the destination allows: an address literal goes as one, a name
    /// goes as a name so the proxy resolves it at its own end rather than ours.</summary>
    internal static byte[] BuildConnect(string host, int port)
    {
        var packet = new List<byte> { 5, 1, 0 };          // version, CONNECT, reserved

        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            byte[] bytes = ip.GetAddressBytes();
            packet.Add((byte)(bytes.Length == 4 ? 1 : 4));
            packet.AddRange(bytes);
        }
        else
        {
            byte[] name = Encoding.ASCII.GetBytes(host);
            if (name.Length > 255) throw new IOException(Loc.T("слишком длинное имя узла"));
            packet.Add(3);
            packet.Add((byte)name.Length);
            packet.AddRange(name);
        }

        packet.Add((byte)(port >> 8));
        packet.Add((byte)(port & 0xFF));
        return packet.ToArray();
    }

    /// <summary>The proxy's own refusal, in words. «Connection refused» from the far end of a chain is
    /// otherwise indistinguishable from the chain itself being broken.</summary>
    private static string DescribeReply(byte code) => code switch
    {
        1 => Loc.T("прокси сообщил об ошибке"),
        2 => Loc.T("прокси запрещает это соединение"),
        3 => Loc.T("сеть Cloudflare недоступна со стороны прокси"),
        4 => Loc.T("прокси не дозвонился до Cloudflare"),
        5 => Loc.T("Cloudflare отказал в соединении со стороны прокси"),
        6 => Loc.T("соединение через прокси истекло"),
        7 => Loc.T("прокси не поддерживает обычные соединения (CONNECT)"),
        8 => Loc.T("прокси не понял адрес назначения"),
        _ => Loc.T("прокси отказал (код {0})", code),
    };

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        if (count > 0) await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }
}
