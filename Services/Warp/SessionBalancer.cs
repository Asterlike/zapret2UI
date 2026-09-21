using System.Net;
using System.Net.Sockets;
using Zapret2UI.Localization;

namespace Zapret2UI.Services.Warp;

/// <summary>
/// One proxy port in front of several MASQUE sessions, handing each new connection to the next session
/// in turn.
///
/// <para><b>Why it exists.</b> MEASURED on a 20.5 MB/s line: a single usque session carried 5.9 MB/s and
/// would not go faster — the ceiling is the session, not the line and not the device. Three sessions on
/// the SAME registered device carried 10.6 MB/s together; a fourth added nothing. A browser opens half a
/// dozen connections per site anyway, so spreading them is free speed rather than a trick.</para>
///
/// <para><b>Why it is safe to spread them.</b> Cloudflare picks the exit address from the address that
/// dialled in, and every session here dials from the same place — measured: four sessions, one exit
/// address (<c>104.28.197.9</c>) and one country for all of them. A site therefore sees what it saw
/// before: one address opening several connections. That does NOT hold through Tor, where every session
/// would leave from a different exit — which is why the Tor entrance keeps a single session.</para>
///
/// <para><b>It does not speak SOCKS.</b> The raw TCP connection is spliced to a session and this class
/// steps out of the way; the SOCKS5 handshake then happens between the browser and usque exactly as it
/// did when the browser talked to usque directly. Nothing here can corrupt a protocol it never
/// parses.</para>
/// </summary>
internal sealed class SessionBalancer : IDisposable
{
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int[] _sessions = Array.Empty<int>();
    private string _lastError = "";
    private long _next = -1;

    /// <summary>Raised for anything worth seeing in the journal — in practice, a session that stopped
    /// answering while the others carry on.</summary>
    public event Action<string>? LogLine;

    /// <summary>The port the user points their browser at. 0 until <see cref="Start"/> succeeds.</summary>
    public int Port { get; private set; }

    public bool IsRunning
    {
        get { lock (_lock) return _listener is not null; }
    }

    /// <summary>How many sessions are being spread across.</summary>
    public int Sessions
    {
        get { lock (_lock) return _sessions.Length; }
    }

    /// <summary>Take the public port and start spreading connections over the sessions behind it. The
    /// caller has already proved each of those sessions reaches Cloudflare; this only carries bytes.</summary>
    public bool Start(int listenPort, IReadOnlyList<int> sessionPorts, out string error)
    {
        error = "";
        Stop();

        int[] ports = sessionPorts.ToArray();
        if (ports.Length == 0)
        {
            error = Loc.T("Не осталось ни одного рабочего соединения с Cloudflare.");
            return false;
        }

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, listenPort);
            listener.Start();

            var cts = new CancellationTokenSource();
            lock (_lock)
            {
                _listener = listener;
                _cts = cts;
                _sessions = ports;
                _lastError = "";
                Interlocked.Exchange(ref _next, -1);
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            _ = Task.Run(() => AcceptAsync(listener, ports, cts.Token));
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
            _sessions = Array.Empty<int>();
            Port = 0;
        }

        try { cts?.Cancel(); } catch { /* nothing left to cancel */ }
        try { listener?.Stop(); } catch { /* already down */ }
        cts?.Dispose();
    }

    public void Dispose() => Stop();

    // ---- forwarding --------------------------------------------------------

    private async Task AcceptAsync(TcpListener listener, int[] ports, CancellationToken ct)
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

            _ = Task.Run(() => HandleAsync(client, ports, ct), CancellationToken.None);
        }
    }

    /// <summary>Round-robin, with the rest of the sessions as the fallback: one that has died is a reason
    /// to use another, never a reason to fail a request the others can carry perfectly well.</summary>
    private async Task HandleAsync(TcpClient inbound, int[] ports, CancellationToken ct)
    {
        using var client = inbound;
        client.NoDelay = true;

        int first = (int)((ulong)Interlocked.Increment(ref _next) % (ulong)ports.Length);

        for (int i = 0; i < ports.Length; i++)
        {
            int port = ports[(first + i) % ports.Length];
            var outbound = new TcpClient { NoDelay = true };
            try
            {
                await outbound.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { outbound.Dispose(); return; }
            catch (Exception ex)
            {
                outbound.Dispose();
                Fail(port, ex.Message);
                continue;
            }

            using (outbound)
            {
                try
                {
                    NetworkStream up = outbound.GetStream();
                    NetworkStream down = client.GetStream();
                    await Task.WhenAny(PumpAsync(down, up, ct), PumpAsync(up, down, ct)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* shutting down */ }
                catch (Exception ex) { Fail(port, ex.Message); }
            }
            return;
        }
    }

    /// <summary>Copy until one side is done, then half-close the other so the far end sees a clean end of
    /// stream rather than a connection that simply stops answering.</summary>
    private static async Task PumpAsync(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        try { await from.CopyToAsync(to, 32 * 1024, ct).ConfigureAwait(false); }
        catch { /* the other direction reports the failure; this one just ends */ }
        try { to.Socket.Shutdown(SocketShutdown.Send); } catch { /* already closed */ }
    }

    private void Fail(int port, string message)
    {
        string line = Loc.T("[masque] соединение {0} не отвечает: {1}", port, message);
        lock (_lock)
        {
            // A dead session fails every connection the browser opens, and there are dozens. One line per
            // distinct cause is what keeps the journal readable.
            if (string.Equals(_lastError, line, StringComparison.Ordinal)) return;
            _lastError = line;
        }
        LogLine?.Invoke(line);
    }
}
