using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Zapret2UI.Localization;
using Zapret2UI.Services.Infrastructure;

namespace Zapret2UI.Services.Warp;

/// <summary>
/// Cloudflare WARP over MASQUE, exposed as a local SOCKS5 proxy.
///
/// <para><b>Why this replaces the WireGuard path rather than joining it.</b> Measured on a censored
/// Russian ISP, and reported independently by others: WireGuard to WARP completes its handshake and then
/// carries nothing. The engine cannot mend that — a desync disguises the first packet of a flow, and
/// there is nothing to hide a steady stream of transport packets behind. MASQUE is Cloudflare's own
/// second transport and looks like ordinary HTTP/3 on 443, which is why the official client keeps
/// working where a hand-rolled WireGuard config does not.</para>
///
/// <para><b>Why nothing here can break the machine's network.</b> The whole failure mode that made the
/// tunnel version so painful — routes captured, a kill switch installed, the user left with no internet
/// and no idea why — came from owning a network interface. This owns a listening socket on loopback.
/// When it fails, it fails alone.</para>
/// </summary>
public sealed class MasqueService : IDisposable
{
    private readonly MasqueRuntime _runtime = new();

    public MasqueService() => _runtime.LogLine += line => LogLine?.Invoke(line);

    public event Action<string>? LogLine;

    private void Log(string line) => LogLine?.Invoke(line);

    /// <summary>True once a device is enrolled and its config is on disk.</summary>
    public static bool IsRegistered => MasqueRuntime.IsRegistered;

    /// <summary>True while the local proxy is listening.</summary>
    public bool IsRunning => _runtime.IsRunning;

    /// <summary>Where Cloudflare said we came out, last time a connection was proved. The country is the
    /// part that matters: free WARP is anycast and lands on the nearest edge, so a user in a censored
    /// country can easily be handed an exit inside it — which changes their address without lifting a
    /// single geo block.</summary>
    internal WarpTrace.Result? LastExit { get; private set; }

    /// <summary>Where callers should point their applications while the proxy is up.</summary>
    public static string ProxyAddress(int port) =>
        $"{MasqueRuntime.BindAddress}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    // ---- registration ------------------------------------------------------

    /// <summary>Enrol a MASQUE device. Separate from the WireGuard device and not derived from it.</summary>
    public async Task<WarpResult> RegisterAsync(CancellationToken ct = default)
    {
        Log(Loc.T("[masque] регистрирую устройство в Cloudflare…"));
        var r = await MasqueRuntime.RegisterAsync(ct).ConfigureAwait(false);
        Log(r.Ok
            ? Loc.T("[masque] устройство зарегистрировано")
            : Loc.T("[masque] регистрация не удалась: {0}", r.Message));
        return r;
    }

    /// <summary>Forget the device. The enrolment stays on Cloudflare's side — we simply stop using it —
    /// so the next registration issues a new one.</summary>
    public void Reset()
    {
        Stop();
        try { if (File.Exists(AppPaths.MasqueConfigFile)) File.Delete(AppPaths.MasqueConfigFile); }
        catch { /* a config we cannot delete is re-registered over anyway */ }
    }

    // ---- the proxy ---------------------------------------------------------

    /// <summary>Start the proxy and prove it carries traffic before reporting success.
    ///
    /// <para>Three separate things have to be true and each is checked rather than assumed: the port is
    /// free, the client is listening, and Cloudflare answers through it. The middle one is where the old
    /// design stopped, and «поднялось, но не работает» is precisely the gap between it and the last.</para></summary>
    public async Task<WarpResult> StartAsync(int listenPort, MasqueTransport transport,
                                             CancellationToken ct = default)
    {
        if (!IsRegistered) return WarpResult.Fail(Loc.T("Сначала создайте конфигурацию."));

        // A port already in use is the one failure that would otherwise look like a broken tunnel: usque
        // exits, the switch flips back, and nothing says why. 1080 is the conventional SOCKS port, so
        // another proxy sitting on it is common rather than exotic.
        if (!IsPortFree(listenPort))
            return WarpResult.Fail(Loc.T("Порт {0} уже занят другой программой. Выберите другой.", listenPort));

        Log(Loc.T("[masque] запускаю прокси на {0}, транспорт {1}…",
                  ProxyAddress(listenPort), transport.Describe()));

        if (!_runtime.Start(listenPort, transport, out string error))
            return WarpResult.Fail(Loc.T("Не удалось запустить клиент MASQUE: {0}", error));

        if (!await _runtime.WaitUntilListeningAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
        {
            _runtime.Stop();
            return WarpResult.Fail(Loc.T("Клиент MASQUE запустился, но не начал принимать соединения."));
        }

        var trace = await ReadTraceThroughProxyAsync(listenPort, ct).ConfigureAwait(false);
        if (trace is not { } t)
        {
            _runtime.Stop();
            return WarpResult.Fail(Loc.T(
                "Прокси запущен, но Cloudflare через него не отвечает — соединение MASQUE не "
                + "установилось ({0}).", transport.Describe()));
        }

        if (!t.InsideWarp)
        {
            _runtime.Stop();
            return WarpResult.Fail(Loc.T(
                "Cloudflare отвечает через прокси, но говорит, что трафик идёт мимо WARP (адрес {0}). "
                + "Обычно так бывает, когда параллельно поднят другой туннель.", t.Ip));
        }

        LastExit = t;
        Log(Loc.T("[masque] Cloudflare подтверждает: WARP работает, выход {0} ({1}), узел {2}",
                  t.Ip, t.Location, t.Colo));
        return WarpResult.Success(Loc.T("WARP работает. Адрес выхода: {0} ({1}). Прокси: {2}",
                                        t.Ip, t.Location, ProxyAddress(listenPort)));
    }

    /// <summary>Every way worth trying to reach Cloudflare, best first.
    ///
    /// <para>The order is measured, not guessed. On a censored Russian ISP, HTTP/2 over TCP on 443
    /// connected every time with the bypass running; without it 443 and 8443 were cut moments after
    /// connecting and only 4443 survived; QUIC reached nothing on any of the seven ports. So TCP leads,
    /// and the QUIC attempts that trail it vary the initial packet size, which is what a censor dropping
    /// by length keys on. What worked last time goes first — the rest is there for the day it stops.</para></summary>
    internal static List<MasqueTransport> Sweep(bool preferHttp2, int preferredPort)
    {
        var order = new List<MasqueTransport> { new(preferHttp2, preferredPort) };

        void Add(MasqueTransport t) { if (!order.Contains(t)) order.Add(t); }

        foreach (int p in new[] { 443, 4443, 8443, 500 }) Add(new MasqueTransport(Http2: true, ConnectPort: p));
        Add(new MasqueTransport(Http2: false, ConnectPort: 443, InitialPacketSize: 1200));
        Add(new MasqueTransport(Http2: false, ConnectPort: 443, InitialPacketSize: 800));
        Add(new MasqueTransport(Http2: false, ConnectPort: 443));
        return order;
    }

    /// <summary>Bring the proxy up, trying each transport until one carries traffic. Returns the one that
    /// worked so the caller can remember it.</summary>
    public async Task<(WarpResult Result, MasqueTransport? Winner)> ConnectAsync(
        int listenPort, bool preferHttp2, int preferredPort, CancellationToken ct = default)
    {
        WarpResult last = WarpResult.Fail(Loc.T("Не удалось подключиться."));

        foreach (var t in Sweep(preferHttp2, preferredPort))
        {
            ct.ThrowIfCancellationRequested();
            last = await StartAsync(listenPort, t, ct).ConfigureAwait(false);
            if (last.Ok) return (last, t);
            Stop();
        }

        return (WarpResult.Fail(Loc.T(
            "Ни один способ подключения к Cloudflare не сработал. Последняя ошибка: {0}", last.Message)), null);
    }

    /// <summary>Stop the proxy. Nothing about the system's networking has to be put back.</summary>
    public void Stop()
    {
        if (!_runtime.IsRunning) return;
        _runtime.Stop();
        Log(Loc.T("[masque] прокси остановлен"));
    }

    /// <summary>Drop a proxy left behind by a crash. Called once at startup.</summary>
    public static void DropStaleProxy() => MasqueRuntime.DropStaleProxy();

    public void Dispose() => _runtime.Dispose();

    // ---- checks ------------------------------------------------------------

    /// <summary>True when nothing is listening on the port yet. Asked by binding rather than by reading
    /// a table: the table can be stale by the time it is read, the bind cannot.</summary>
    internal static bool IsPortFree(int port)
    {
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>Ask Cloudflare where it thinks we are, THROUGH the proxy — which is the only way the
    /// answer means anything. Null when nothing usable came back.</summary>
    private static async Task<WarpTrace.Result?> ReadTraceThroughProxyAsync(int port, CancellationToken ct)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://{ProxyAddress(port)}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

            // Addressed by IP so no DNS is involved, and 1.1.1.1 is Cloudflare's own resolver — its
            // certificate really does carry the address, so this stays a normally validated request.
            string body = await http.GetStringAsync("https://1.1.1.1/cdn-cgi/trace", ct).ConfigureAwait(false);
            return WarpTrace.Parse(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
