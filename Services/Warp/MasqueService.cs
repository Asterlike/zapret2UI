using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Zapret2UI.Localization;
using Zapret2UI.Services.Infrastructure;

namespace Zapret2UI.Services.Warp;

/// <summary>Where the connection to Cloudflare is dialled FROM — which, measured, is what decides the
/// country it comes OUT in.</summary>
public enum MasqueEntranceKind
{
    /// <summary>Straight out of this machine. The exit lands in the user's own country; what changes is
    /// the reputation of the address, not where it is. Every version before this one did only this.</summary>
    Direct,

    /// <summary>Through a SOCKS5 proxy the user already runs — typically the local port of a VLESS
    /// client. Fast, and the country is whatever that server's country is.</summary>
    Proxy,

    /// <summary>Through Tor, with the exit pinned to a chosen country. Free and needs nothing of the
    /// user's own, which is why it exists: most people have no foreign proxy. Costs latency.</summary>
    Tor,
}

/// <summary>The chosen entrance and everything it needs. A struct with three strings rather than three
/// parameters, because these travel together from the settings file to the tunnel and splitting them up
/// is how one of them gets forgotten.</summary>
/// <param name="Kind">Direct, the user's proxy, or Tor.</param>
/// <param name="Proxy">SOCKS5 address for <see cref="MasqueEntranceKind.Proxy"/>.</param>
/// <param name="Country">Two-letter country for <see cref="MasqueEntranceKind.Tor"/>.</param>
/// <param name="Bridges">Bridge lines for Tor; empty falls back to the ones inside the bundle.</param>
public readonly record struct MasqueEntrance(
    MasqueEntranceKind Kind, string Proxy = "", string Country = "", string Bridges = "",
    string Exit = "")
{
    /// <summary>The way every earlier version connected, and still the default.</summary>
    public static MasqueEntrance Straight { get; } = new(MasqueEntranceKind.Direct);

    internal bool IsExternal => Kind != MasqueEntranceKind.Direct;

    // default(MasqueEntrance) leaves the strings null, and that shape reaches here from callers that
    // simply did not ask for an entrance.
    internal string ProxyText => Proxy ?? "";
    internal string CountryText => Country ?? "";
    internal string BridgesText => Bridges ?? "";

    /// <summary>The one exit address the user pinned, empty when any node in the country will do.</summary>
    internal string ExitText => Exit ?? "";
}

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

    /// <summary>The way in, when the user asked for one. Idle — and costing nothing — while the
    /// connection is dialled directly, which is still the default.</summary>
    private readonly UpstreamRelay _relay = new();

    /// <summary>Set when a connection attempt died at the ENTRANCE rather than at the transport, so the
    /// sweep can stop instead of asking the same dead proxy four more times.</summary>
    private bool _entranceFailed;

    /// <summary>Tor, for the users who have no foreign proxy of their own — which is most of them.
    /// Nothing is downloaded until that entrance is actually chosen.</summary>
    private readonly TorRuntime _tor = new();

    /// <summary>The sessions opened after the first one proved the route. Empty while a single one is
    /// enough — the Tor entrance, and every attempt that has not got past its first session yet.</summary>
    private readonly List<MasqueRuntime> _extra = new();

    /// <summary>The port the user actually points a browser at, spreading its connections over
    /// <see cref="_runtime"/> and <see cref="_extra"/>.</summary>
    private readonly SessionBalancer _balancer = new();

    /// <summary>How many MASQUE sessions one proxy keeps open.
    ///
    /// <para>MEASURED, on a 20.5 MB/s line: one session 5.9 MB/s, two 7.2, three 10.6, four 10.3. The
    /// ceiling belongs to the session — not to the line, and not to the registered device, which carried
    /// all four at once quite happily — so a second and third session are close to free speed, and a
    /// fourth pays 15 MB of memory for nothing.</para></summary>
    internal const int ParallelSessions = 3;

    /// <summary>How many sessions a Tor entrance may open once its exit is pinned.
    ///
    /// <para>MEASURED, four interleaved pairs through a pinned exit: one session 2.4/4.4/3.2/4.4 (mean
    /// 3.6 Mbit/s), two sessions 4.7/6.2/5.3/5.7 (mean 5.5) — samples that do not overlap, so the second
    /// session is worth about 1.5x. A third was measured too and did not separate from two (4.1 against
    /// 4.8, overlapping), so it is not taken: every session is another circuit through the SAME exit,
    /// and that relay is a volunteer's machine rather than Cloudflare's.</para></summary>
    internal const int TorSessions = 2;

    public MasqueService()
    {
        _runtime.LogLine += line => LogLine?.Invoke(line);
        _relay.LogLine += line => LogLine?.Invoke(line);
        _tor.LogLine += line => LogLine?.Invoke(line);
        _tor.Status += line => Status?.Invoke(line);
        _balancer.LogLine += line => LogLine?.Invoke(line);
    }

    public event Action<string>? LogLine;

    /// <summary>Short lines meant for the screen rather than the journal: a 22 MB download and a Tor
    /// bootstrap are both long enough that silence reads as a hang.</summary>
    public event Action<string>? Status;

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

    /// <summary>The country of the address the tunnel was dialled FROM, when it was dialled through the
    /// user's proxy. Kept because it is the half of the story that explains the other half: the exit
    /// country follows the ingress country, so «вход DE → выход DE» is the feature working and
    /// «вход DE → выход RU» is it failing, and both look identical if only the exit is shown.</summary>
    internal string LastIngressLocation { get; private set; } = "";

    /// <summary>Where callers should point their applications while the proxy is up.</summary>
    public static string ProxyAddress(int port) =>
        $"{MasqueRuntime.BindAddress}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    // ---- registration ------------------------------------------------------

    /// <summary>Enrol a MASQUE device.</summary>
    public async Task<WarpResult> RegisterAsync(CancellationToken ct = default)
    {
        Log(Loc.T("[masque] регистрация устройства в Cloudflare…"));
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
                                             MasqueEntrance entrance = default,
                                             int sessions = ParallelSessions, CancellationToken ct = default)
    {
        if (!IsRegistered) return WarpResult.Fail(Loc.T("Сначала создайте устройство."));

        // A port already in use is the one failure that would otherwise look like a broken tunnel: usque
        // exits, the switch flips back, and nothing says why. 1080 is the conventional SOCKS port, so
        // another proxy sitting on it is common rather than exotic.
        if (!IsPortFree(listenPort))
            return WarpResult.Fail(Loc.T("Порт {0} уже занят другой программой. Выберите другой.", listenPort));

        string ingress = "";
        bool external = entrance.IsExternal;
        _entranceFailed = false;
        if (external)
        {
            var prepared = await PrepareIngressAsync(entrance, transport, listenPort, ct).ConfigureAwait(false);
            if (!prepared.Result.Ok)
            {
                // Tor that would not come up, or a proxy that is not answering: another transport to
                // Cloudflare cannot mend either, and trying all of them would cost the user minutes of
                // waiting for the same answer four times over.
                _entranceFailed = true;
                return prepared.Result;
            }
            transport = prepared.Transport;
            ingress = prepared.Ingress;
        }

        Log(Loc.T("[masque] запуск прокси на {0}, транспорт {1}…",
                  ProxyAddress(listenPort), transport.Describe()));

        // The first session is dialled on a port of its own and proved there. Only once Cloudflare has
        // answered through it do the others follow, and only then does anything listen on the port the
        // user points a browser at — so a route that does not work costs one start-up, not three, and
        // the public port never exists in a half-working state.
        int primary = FreeLoopbackPort();
        if (!_runtime.Start(primary, transport, out string error))
        {
            StopParts();
            return WarpResult.Fail(Loc.T("Не удалось запустить клиент MASQUE: {0}", error));
        }

        if (!await _runtime.WaitUntilListeningAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
        {
            StopParts();
            return WarpResult.Fail(Loc.T("Клиент MASQUE запустился, но не начал принимать соединения."));
        }

        var trace = await ReadTraceThroughProxyAsync(primary, ct).ConfigureAwait(false);
        if (trace is not { } t)
        {
            // With a proxy in the way, the relay usually knows exactly which leg broke, and its answer is
            // far more useful than «Cloudflare не отвечает» — which is what the user would otherwise read
            // about a proxy of theirs that is simply not running.
            string why = external && _relay.LastError.Length > 0
                ? Loc.T(" Прокси входа сообщает: {0}.", _relay.LastError)
                : "";
            StopParts();
            return WarpResult.Fail(Loc.T(
                "Прокси запущен, но Cloudflare через него не отвечает — соединение MASQUE не "
                + "установилось ({0}).", transport.Describe()) + why);
        }

        if (!t.InsideWarp)
        {
            StopParts();
            return WarpResult.Fail(Loc.T(
                "Cloudflare отвечает через прокси, но говорит, что трафик идёт мимо WARP (адрес {0}). "
                + "Обычно так бывает, когда параллельно поднят другой туннель.", t.Ip));
        }

        // MEASURED, twice: a MASQUE session re-established without the proxy comes back with warp=on and
        // the user's OWN country, silently — everything above would call that a success. The guard is the
        // relay's own count rather than a comparison of countries: the country a connection «should» have
        // is unknowable (the owner's own machine sits behind a VPN, so «своя страна» is whatever that VPN
        // says, and a first draft of this check refused a perfectly good chain for matching it), while a
        // connection that never crossed the proxy is a fact this side of the wire.
        if (external && _relay.Carried == 0)
        {
            StopParts();
            return WarpResult.Fail(Loc.T(
                "Соединение с Cloudflare прошло мимо вашего прокси, поэтому прокси остановлен: так "
                + "страну выхода не сменить."));
        }

        // With Tor the expected country is not a guess — the user picked it and torrc pins it — so a
        // mismatch means the pin did not hold (a stale GeoIP, a relay lying about itself) and the user
        // would be coming out somewhere they never chose. Retrying another transport cannot fix that,
        // hence the full stop rather than the partial one above.
        string expected = entrance.Kind == MasqueEntranceKind.Tor
            ? TorRuntime.SanitiseCountry(entrance.CountryText)
            : "";
        if (expected.Length > 0 && !string.Equals(expected, t.Location, StringComparison.OrdinalIgnoreCase))
        {
            Stop();
            return WarpResult.Fail(Loc.T(
                "Выход получился в стране {0}, а вы просили {1} — прокси остановлен. Попробуйте ещё раз "
                + "или выберите другую страну: подходящих выходных узлов Tor может не оказаться.",
                Describe(t.Location), expected.ToUpperInvariant()));
        }

        var ports = new List<int> { primary };
        // With Tor, the pin that counts is the one in force rather than the one asked for: a pinned exit
        // that carries nothing is dropped for any node in the country, and then a second session would
        // draw an exit of its own — the very split the pin exists to prevent.
        MasqueEntrance inForce = entrance.Kind == MasqueEntranceKind.Tor
            ? entrance with { Exit = _tor.ActivePin }
            : entrance;
        int want = SessionsFor(inForce, sessions);
        for (int i = 1; i < want; i++)
        {
            int extra = await AddSessionAsync(transport, ct).ConfigureAwait(false);
            if (extra > 0) ports.Add(extra);
        }

        if (!_balancer.Start(listenPort, ports, out string spread))
        {
            StopParts();
            return WarpResult.Fail(Loc.T("Не удалось занять порт {0}: {1}", listenPort, spread));
        }

        if (ports.Count > 1)
            Log(Loc.T("[masque] соединений с Cloudflare: {0} — они делят один адрес выхода", ports.Count));

        LastExit = t;
        LastIngressLocation = ingress;
        Log(Loc.T("[masque] Cloudflare подтверждает: WARP работает, выход {0} ({1}), узел {2}",
                  t.Ip, t.Location, t.Colo));
        return WarpResult.Success(Loc.T("WARP работает. Адрес выхода: {0} ({1}). Прокси: {2}",
                                        t.Ip, t.Location, ProxyAddress(listenPort)));
    }

    /// <summary>How many MASQUE sessions this entrance may open.
    ///
    /// <para><b>Why Tor is not simply three like the rest.</b> MEASURED, before the exit could be pinned:
    /// four chains over four circuits summed to 1.7x one chain, one of the four stalled outright, and the
    /// exits came out in three different COUNTRIES — DE, DE, DE and FR — which is the one thing the whole
    /// entrance exists to pin. The countries diverged because every circuit drew its own exit, so the
    /// answer is not «fewer sessions» but «one exit»: with an address pinned, <c>StrictNodes</c> sends
    /// every circuit out of the same relay and the sessions share one address again. Without a pin the
    /// old single session stands, because a chain that moves country is worse than a slow one.</para>
    ///
    /// <para>The sessions still have to be told apart, or Tor would put them all on one circuit —
    /// see <see cref="UpstreamRelay.Isolated"/>.</para></summary>
    internal static int SessionsFor(MasqueEntrance entrance, int sessions)
    {
        int want = Math.Max(1, sessions);
        if (entrance.Kind != MasqueEntranceKind.Tor) return want;

        return ExitDirectory.SanitiseAddressList(entrance.ExitText).Length > 0
            ? Math.Min(want, TorSessions)
            : 1;
    }

    /// <summary>Open one more session on a transport the first one has already proved, and return the
    /// loopback port it listens on — or 0 when it did not come up.
    ///
    /// <para>Best-effort by design: the proxy already works at this point, and a session that fails to
    /// join is a little less speed, never a failure to report. It is still checked against Cloudflare
    /// rather than merely started, because a session that listens but carries nothing would take its
    /// share of the browser's connections and fail every one of them.</para></summary>
    private async Task<int> AddSessionAsync(MasqueTransport transport, CancellationToken ct)
    {
        var session = new MasqueRuntime();
        int port = FreeLoopbackPort();

        if (!session.Start(port, transport, out string error))
        {
            Log(Loc.T("[masque] дополнительное соединение не запустилось: {0}", error));
            session.Dispose();
            return 0;
        }

        _extra.Add(session);   // registered before the checks, so a failure is still stopped by StopParts

        if (!await session.WaitUntilListeningAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false) ||
            await ReadTraceThroughProxyAsync(port, ct).ConfigureAwait(false) is not { InsideWarp: true })
        {
            Log(Loc.T("[masque] дополнительное соединение не дошло до Cloudflare — дальше без него"));
            session.Stop();
            return 0;
        }

        return port;
    }

    /// <summary>A loopback port the OS says is free, asked for the way <see cref="IsPortFree"/> asks: by
    /// binding. Sessions sit on ports nothing else needs to know, so there is nothing to configure and
    /// nothing to collide with.</summary>
    internal static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Put the user's proxy in front of the connection: check that it works, learn which country
    /// it comes out in, and point the transport at a relay instead of at Cloudflare.
    ///
    /// <para>The order matters. The proxy is probed BEFORE usque is started, because a proxy that is not
    /// running is the most likely thing to be wrong here, and finding that out after a full transport
    /// sweep would cost the user a minute of «подключаюсь…» to learn that their VLESS client is
    /// closed.</para></summary>
    private async Task<(WarpResult Result, MasqueTransport Transport, string Ingress)>
        PrepareIngressAsync(MasqueEntrance entrance, MasqueTransport transport, int listenPort,
                            CancellationToken ct)
    {
        string upstreamProxy = entrance.ProxyText;

        if (entrance.Kind == MasqueEntranceKind.Tor)
        {
            // The bundle has to be here before anything else can happen, and the site it lives on is
            // blocked on the very networks this feature is for — so it is fetched THROUGH WARP, dialled
            // directly. Downloading it before WARP is up, from a site that refuses to answer, is the
            // deadlock this avoids: no Tor without the download, no download without a way out.
            if (!TorRuntime.IsInstalled)
            {
                WarpResult fetched = await FetchTorThroughWarpAsync(listenPort, transport, ct)
                                           .ConfigureAwait(false);
                if (!fetched.Ok) return (fetched, transport, "");
            }

            // A no-op when Tor is already up with these very options — which matters because the
            // transport sweep calls this once per attempt, and re-bootstrapping Tor for each of them
            // would turn seconds into minutes.
            WarpResult tor = await _tor.StartAsync(entrance.CountryText, entrance.BridgesText, "",
                                                   entrance.ExitText, ct)
                                       .ConfigureAwait(false);
            if (!tor.Ok) return (tor, transport, "");
            upstreamProxy = MasqueRuntime.BindAddress + ":"
                + _tor.SocksPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!ProxyEndpoint.TryParse(upstreamProxy, out ProxyEndpoint proxy, out string problem))
            return (WarpResult.Fail(problem), transport, "");

        // Asked before anything is started: a proxy client that is closed is the likeliest thing to be
        // wrong here, and the answer also tells us where the entrance comes out.
        if (await ReadTraceAsync(SocksProxy(proxy), ct).ConfigureAwait(false) is not { } seen)
            return (WarpResult.Fail(Loc.T(
                "Прокси {0} не отвечает. Проверьте, что клиент запущен и этот порт — его порт SOCKS5.",
                proxy)), transport, "");

        string target = MasqueRuntime.RegisteredEndpoint(http2: true);
        if (target.Length == 0)
            return (WarpResult.Fail(Loc.T(
                "Не удалось прочитать адрес входа Cloudflare из настроек устройства. Пересоздайте "
                + "устройство на этой вкладке.")), transport, "");

        // Isolation is asked for only with Tor at the far end: it is the one upstream that puts streams
        // sharing a login on one circuit, and the one whose login we are free to make up.
        if (!_relay.Start(proxy, target, transport.ConnectPort, out string error,
                          isolate: entrance.Kind == MasqueEntranceKind.Tor))
            return (WarpResult.Fail(Loc.T("Не удалось открыть переходник к прокси: {0}", error)),
                    transport, "");

        Log(Loc.T("[masque] вход через {0}: адрес {1} ({2}); дальше Cloudflare {3}:{4}",
                  proxy, seen.Ip, Describe(seen.Location), target, transport.ConnectPort));

        // SOCKS5 CONNECT is a TCP tunnel, so MASQUE has to take its HTTP/2 transport — QUIC cannot be
        // carried this way at all. IPv6 goes with it: the entry point is now loopback.
        var routed = transport with
        {
            Http2 = true,
            Ipv6 = false,
            Endpoint = MasqueRuntime.BindAddress,
            ConnectPort = _relay.Port,
        };
        return (WarpResult.Success(""), routed, seen.Location);
    }

    private static string Describe(string location) => location.Length > 0 ? location : Loc.T("страна неизвестна");

    /// <summary>Get the Tor bundle onto the disk on a network that blocks the site it comes from.
    ///
    /// <para>WARP is raised the ordinary way first — dialled directly, which is the one path proven to
    /// work from a censored Russian ISP once the bypass is running — and the bundle is pulled through
    /// that tunnel: to Cloudflare it is an ordinary request, and the name is resolved at the far end
    /// where nobody is filtering it. The tunnel is then dropped, because the connection the user
    /// actually asked for has to be dialled through Tor instead.</para>
    ///
    /// <para>If WARP itself will not come up, the plain download is still attempted: on an uncensored
    /// network it simply works, and there the whole detour would be a waste.</para></summary>
    private async Task<WarpResult> FetchTorThroughWarpAsync(int listenPort, MasqueTransport transport,
                                                           CancellationToken ct)
    {
        Status?.Invoke(Loc.T("Tor ещё не скачан — сначала запускается WARP, Tor скачается через него…"));
        Log(Loc.T("[tor] бандла нет: WARP запускается напрямую, чтобы скачать Tor через него"));

        // One session: this proxy exists to pull a 22 MB file down a single connection, and two more
        // would be started, proved and thrown away without ever carrying a byte.
        WarpResult direct = await StartAsync(listenPort, transport, MasqueEntrance.Straight, 1, ct)
                                  .ConfigureAwait(false);
        if (direct.Ok)
        {
            WarpResult fetched = await _tor.EnsureReadyAsync(ProxyAddress(listenPort), ct)
                                           .ConfigureAwait(false);
            StopParts();
            if (fetched.Ok) return fetched;

            Log(Loc.T("[tor] через WARP скачать не вышло: {0}", fetched.Message));
        }
        else
        {
            StopParts();
            Log(Loc.T("[tor] WARP напрямую не поднялся ({0}) — Tor скачивается обычным путём",
                      direct.Message));
        }

        WarpResult plain = await _tor.EnsureReadyAsync("", ct).ConfigureAwait(false);
        if (plain.Ok) return plain;

        return WarpResult.Fail(Loc.T(
            "Не удалось получить Tor. Программа пробовала скачать его через WARP и напрямую — оба раза "
            + "мимо. Включите обход на «Главной»: без него не встаёт ни WARP, ни загрузка. Подробности "
            + "в журнале."));
    }

    /// <summary>Every way worth trying to reach Cloudflare, best first.
    ///
    /// <para>The order is measured, not guessed. On a censored Russian ISP, HTTP/2 over TCP on 443
    /// connected every time with the bypass running; without it 443 and 8443 were cut moments after
    /// connecting and only 4443 survived; QUIC reached nothing on any of the seven ports. So TCP leads,
    /// and the QUIC attempts that trail it vary the initial packet size, which is what a censor dropping
    /// by length keys on. What worked last time goes first — the rest is there for the day it stops.</para></summary>
    internal static List<MasqueTransport> Sweep(bool preferHttp2, int preferredPort, bool tcpOnly = false)
    {
        var order = new List<MasqueTransport> { new(tcpOnly || preferHttp2, preferredPort) };

        void Add(MasqueTransport t) { if (!order.Contains(t)) order.Add(t); }

        foreach (int p in new[] { 443, 4443, 8443, 500 }) Add(new MasqueTransport(Http2: true, ConnectPort: p));

        // A SOCKS5 proxy carries TCP and nothing else, so with one in front of us the QUIC attempts are
        // not a fallback — they are three guaranteed failures the user waits through.
        if (tcpOnly) return order;

        Add(new MasqueTransport(Http2: false, ConnectPort: 443, InitialPacketSize: 1200));
        Add(new MasqueTransport(Http2: false, ConnectPort: 443, InitialPacketSize: 800));
        Add(new MasqueTransport(Http2: false, ConnectPort: 443));
        return order;
    }

    /// <summary>Bring the proxy up, trying each transport until one carries traffic. Returns the one that
    /// worked so the caller can remember it.</summary>
    public async Task<(WarpResult Result, MasqueTransport? Winner)> ConnectAsync(
        int listenPort, bool preferHttp2, int preferredPort, MasqueEntrance entrance = default,
        CancellationToken ct = default)
    {
        WarpResult last = WarpResult.Fail(Loc.T("Не удалось подключиться."));

        foreach (var t in Sweep(preferHttp2, preferredPort, tcpOnly: entrance.IsExternal))
        {
            ct.ThrowIfCancellationRequested();
            last = await StartAsync(listenPort, t, entrance, ParallelSessions, ct).ConfigureAwait(false);
            if (last.Ok) return (last, t);
            // Between attempts only the client and the relay go down: Tor took a minute to bootstrap and
            // the next transport needs the very same entrance.
            StopParts();
            // …unless the entrance is what failed, in which case the sweep has nothing left to vary.
            if (_entranceFailed) break;
        }

        Stop();
        return (WarpResult.Fail(Loc.T(
            "Ни один способ подключения к Cloudflare не сработал. Последняя ошибка: {0}", last.Message)), null);
    }

    /// <summary>Stop the proxy and everything raised for it, Tor included. Nothing about the system's
    /// networking has to be put back.</summary>
    public void Stop()
    {
        bool anything = _runtime.IsRunning || _relay.IsRunning || _tor.IsRunning || _balancer.IsRunning;
        StopParts();
        _tor.Stop();
        if (anything) Log(Loc.T("[masque] прокси остановлен"));
    }

    /// <summary>The client and the relay, but NOT Tor: this is what runs between two attempts of the
    /// same connection, and Tor is the slow part that both of them share.</summary>
    private void StopParts()
    {
        // The public port goes first: while it is open the browser keeps handing connections to sessions
        // that are on their way out, and every one of them fails in the user's face.
        _balancer.Stop();
        _runtime.Stop();
        foreach (var session in _extra)
        {
            session.Stop();
            session.Dispose();
        }
        _extra.Clear();
        _relay.Stop();
    }

    /// <summary>Kill a Tor left behind by a crash. Called once at startup, beside the proxy's own
    /// sweep.</summary>
    public static void DropStaleTor() => TorRuntime.DropStale();

    /// <summary>Drop a proxy left behind by a crash. Called once at startup.</summary>
    public static void DropStaleProxy() => MasqueRuntime.DropStaleProxy();

    public void Dispose()
    {
        _balancer.Dispose();
        _runtime.Dispose();
        foreach (var session in _extra) session.Dispose();
        _extra.Clear();
        _relay.Dispose();
        _tor.Dispose();
    }

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
    private static Task<WarpTrace.Result?> ReadTraceThroughProxyAsync(int port, CancellationToken ct) =>
        ReadTraceAsync(new WebProxy($"socks5://{ProxyAddress(port)}"), ct);

    /// <summary>The same question asked down a chosen path: through a proxy, or — with null — down the
    /// machine's ordinary route, which is how the user's own country is learned.</summary>
    private static async Task<WarpTrace.Result?> ReadTraceAsync(WebProxy? proxy, CancellationToken ct)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = proxy,
                // Explicitly false rather than «no proxy set»: this app writes Windows' own proxy
                // setting, and a direct probe that quietly went through it would answer the wrong
                // question — twice, since the setting can point at the very tunnel being tested.
                UseProxy = proxy is not null,
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

    /// <summary>The user's own SOCKS5 proxy as .NET's HTTP stack wants it, credentials included.</summary>
    private static WebProxy SocksProxy(ProxyEndpoint proxy)
    {
        var web = new WebProxy($"socks5://{proxy}");
        if (proxy.HasCredentials) web.Credentials = new NetworkCredential(proxy.User, proxy.Password);
        return web;
    }
}
