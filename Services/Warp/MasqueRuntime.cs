using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Zapret2UI.Localization;
using Zapret2UI.Services.Infrastructure;

namespace Zapret2UI.Services.Warp;

/// <summary>How to reach Cloudflare: the knobs that differ between networks.
///
/// <para>Cloudflare's own client carries the same two settings — its Windows build reports «HTTP
/// Version: MASQUE (HTTP/3 with HTTP/2 fallback)» — and that fallback is not decoration. On the owner's
/// ISP, QUIC to the MASQUE entry points failed on all seven ports while plain TLS to the very same
/// addresses completed on four, so the transport, not the port, is what has to vary.</para></summary>
/// <param name="Http2">MASQUE over TCP+TLS instead of QUIC — the official client's own fallback.</param>
/// <param name="ConnectPort">Port on the entry point. 443, 4443, 8443, 500, 1701, 4500 or 8095.</param>
/// <param name="InitialPacketSize">QUIC only: cap the initial packet. Censors commonly drop large QUIC
/// initials by length alone, so a smaller one can pass where the auto-sized one cannot. 0 = auto.</param>
/// <param name="Sni">Name to open the tunnel under; empty keeps usque's default.</param>
/// <param name="Ipv6">Dial the IPv6 entry point instead of the IPv4 one. A different address block
/// entirely (2606:4700:103:: rather than 162.159.198.0/24), so it can land somewhere else.</param>
/// <param name="Endpoint">Entry point to dial, overriding the one the registration handed us. There is
/// no command-line flag for it — usque reads it from the config — so this is applied by deriving a
/// config file. Empty keeps the registered one.</param>
public readonly record struct MasqueTransport(
    bool Http2, int ConnectPort, int InitialPacketSize = 0, string Sni = "",
    bool Ipv6 = false, string Endpoint = "")
{
    /// <summary>Short label for the journal, so a sweep reads as a table rather than a wall.</summary>
    public string Describe() =>
        (Http2 ? "HTTP/2 (TCP)" : "HTTP/3 (QUIC)")
        + $" :{ConnectPort}"
        + (Endpoint.Length > 0 ? $" via {Endpoint}" : Ipv6 ? " via IPv6" : "")
        + (InitialPacketSize > 0 ? $" initial={InitialPacketSize}" : "")
        + (Sni.Length > 0 ? $" sni={Sni}" : "");
}

/// <summary>
/// The bundled MASQUE client (usque): unpacked from our own exe on first use and driven headlessly.
///
/// <para><b>Why this exists next to <see cref="WireGuardRuntime"/>.</b> WireGuard to WARP is cut at the
/// TRANSPORT level on censored Russian networks — the handshake is let through and the data stream is
/// dropped, which is why the tunnel came up, reported a completed handshake, and carried nothing. The
/// engine cannot repair that: a desync disguises the FIRST packet of a flow, and a steady stream of
/// fixed-shape WireGuard transport packets has nothing left to hide behind. MASQUE is Cloudflare's own
/// second transport — CONNECT-IP over HTTP/3 (RFC 9484) — and looks like ordinary web traffic on 443.</para>
///
/// <para><b>Why proxy mode rather than a tunnel.</b> usque can raise a real interface, and that would
/// drag back every problem the WireGuard path had: colliding routes, a kill switch that cuts the machine
/// off, plaintext crossing the engine's capture on its way in, and administrator rights. In proxy mode it
/// is an ordinary child process listening on loopback. Nothing about the system's networking changes, so
/// a failure here can never leave the user without internet — which was the single worst outcome of the
/// tunnel design.</para>
/// </summary>
internal sealed class MasqueRuntime : IDisposable
{
    private const string ExeName = "usque.exe";
    private const string LicenseName = "LICENSE.txt";

    private const string ExeResource = "Zapret2UI.usque.usque.exe";
    private const string LicenseResource = "Zapret2UI.usque.LICENSE.txt";

    /// <summary>Loopback only. usque's own default is <c>0.0.0.0</c>, which would publish an open,
    /// unauthenticated proxy into the user's local network — on a café or dormitory Wi-Fi that is handing
    /// strangers a tunnel out of the country under this machine's account. Never pass the default.</summary>
    internal const string BindAddress = "127.0.0.1";

    private readonly object _lock = new();
    private Process? _proc;
    private TaskCompletionSource<bool>? _listening;

    /// <summary>The line usque prints once its listener is up. Watched instead of poking the port with a
    /// throwaway TCP connection: that connection reaches the SOCKS server, which quite rightly logs it as
    /// a client that hung up mid-negotiation — a scary-looking line that is entirely our own doing.</summary>
    private const string ListeningMarker = "SOCKS proxy listening";

    /// <summary>Raised for every line the client writes. Wired into the ordinary journal, because the one
    /// thing this program has learned is that an unexplained failure is worse than a loud one.</summary>
    public event Action<string>? LogLine;

    public bool IsRunning
    {
        get { lock (_lock) return _proc is { HasExited: false }; }
    }

    // ---- unpacking ---------------------------------------------------------

    /// <summary>Unpack the client if needed. Cheap to call repeatedly: a file is rewritten only when it is
    /// missing or a different size.
    ///
    /// <para>Deliberately no <c>icacls</c> lockdown, unlike <see cref="WireGuardRuntime.EnsureReady"/>.
    /// That folder is closed to everyone but SYSTEM because a SYSTEM service executes out of it, so a
    /// user-writable copy would be a privilege escalation. Nothing here runs elevated, so the same lock
    /// would protect nothing and would only stop the owner reading their own logs.</para></summary>
    internal static void EnsureReady()
    {
        Directory.CreateDirectory(AppPaths.MasqueDir);
        Extract(ExeResource, AppPaths.MasqueExe);
        Extract(LicenseResource, Path.Combine(AppPaths.MasqueDir, LicenseName));
    }

    /// <summary>Write an embedded binary out, skipping the copy when it is already byte-count identical.
    /// A half-written executable would be worse than none, so it lands under a temporary name first.</summary>
    private static void Extract(string resource, string path)
    {
        using Stream? src = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException($"embedded resource missing: {resource}");

        if (File.Exists(path) && new FileInfo(path).Length == src.Length) return;

        string tmp = path + ".new";
        using (var dst = File.Create(tmp)) src.CopyTo(dst);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>True once a device has been registered and its config is on disk.</summary>
    internal static bool IsRegistered => File.Exists(AppPaths.MasqueConfigFile);

    // ---- registration ------------------------------------------------------

    /// <summary>Enrol a fresh MASQUE device with Cloudflare.
    ///
    /// <para>A SEPARATE device from the WireGuard one and not interchangeable: MASQUE enrols an ECDSA
    /// P-256 key and receives a licence, an id and an access token, where WireGuard registration produces
    /// a curve25519 pair. The request goes to <c>api.cloudflareclient.com</c>, whose name is cut by SNI on
    /// a censored network — the engine already covers it unconditionally through the
    /// <see cref="AppPaths.WarpApiFile"/> hostlist, so this works for the same reason the WireGuard
    /// registration finally did.</para></summary>
    internal static async Task<WarpResult> RegisterAsync(CancellationToken ct = default)
    {
        try
        {
            EnsureReady();
        }
        catch (Exception ex)
        {
            return WarpResult.Fail(Loc.T("Не удалось распаковать клиент MASQUE: {0}", ex.Message));
        }

        // -a accepts Cloudflare's terms non-interactively. Without it usque stops and waits for a
        // keypress on a console this process does not have, which is not a failure the user could ever
        // see or escape — the button would simply never come back.
        var r = await Task.Run(() => Run(
            new[] { "register", "-c", AppPaths.MasqueConfigFile, "-a", "-m", "PC", "-n", "Zapret2UI" },
            TimeSpan.FromSeconds(60)), ct).ConfigureAwait(false);

        if (r.ExitCode != 0)
            return WarpResult.Fail(Loc.T("Cloudflare не выдал устройство MASQUE: {0}", Describe(r)));

        if (!IsRegistered)
            return WarpResult.Fail(Loc.T("Клиент отчитался об успехе, но файл настроек не появился."));

        return WarpResult.Success(Loc.T("Устройство MASQUE зарегистрировано."));
    }

    // ---- the proxy ---------------------------------------------------------

    /// <summary>Start the SOCKS5 proxy on loopback and leave it running.
    ///
    /// <para><paramref name="connectPort"/> is the port used to REACH Cloudflare, not the one we listen
    /// on. MASQUE's entry points are a tiny fixed pool — 162.159.198.1 and .2 on 443, 4443, 8443, 500,
    /// 1701, 4500 and 8095 — so when one port is throttled the next is a single retry away, with none of
    /// the 57-address sweeping the WireGuard path needed.</para></summary>
    public bool Start(int listenPort, MasqueTransport transport, out string error)
    {
        error = "";
        lock (_lock)
        {
            _listening = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_proc is { HasExited: false }) return true;

            try
            {
                EnsureReady();
            }
            catch (Exception ex)
            {
                error = Loc.T("Не удалось распаковать клиент MASQUE: {0}", ex.Message);
                return false;
            }

            if (!IsRegistered)
            {
                error = Loc.T("Сначала создайте конфигурацию.");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = AppPaths.MasqueExe,
                WorkingDirectory = AppPaths.MasqueDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string a in BuildProxyArguments(listenPort, transport)) psi.ArgumentList.Add(a);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => Emit(e.Data);
            proc.ErrorDataReceived += (_, e) => Emit(e.Data);

            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                proc.Dispose();
                error = ex.Message;
                return false;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _proc = proc;
            return true;
        }
    }

    /// <summary>The command line for proxy mode. Split out so it can be read back in a test — the bind
    /// address in particular is a security property, not a preference.</summary>
    internal static List<string> BuildProxyArguments(int listenPort, MasqueTransport t)
    {
        var args = new List<string>
        {
            "socks",
            "-c", ConfigPathFor(t),
            "-b", BindAddress,
            "-p", Num(listenPort),
            "-P", Num(t.ConnectPort),
            // usque drops an idle connection with H3_NO_ERROR the way the official client does, and by
            // default only redials when something asks it to. Reconnecting on its own keeps the proxy
            // from answering the first request after a quiet spell with a stall.
            "--always-reconnect",
        };

        // MASQUE over TCP+TLS instead of QUIC. Cloudflare offers both and the config carries a separate
        // endpoint for it, because a network that drops UDP to these addresses still passes TLS to the
        // very same ones — measured here: QUIC failed on all seven ports while TLS 1.3 completed on four.
        if (t.Http2) args.Add("--http2");

        // QUIC only. A censored network commonly drops large QUIC initial packets by length rather than
        // by content, so a smaller one can pass where the auto-sized one cannot.
        if (t.InitialPacketSize > 0) { args.Add("-i"); args.Add(Num(t.InitialPacketSize)); }

        // A different address block entirely, so it can land on a different edge.
        if (t.Ipv6) args.Add("-6");

        // The name the tunnel is opened under. Cloudflare answers on several, and which one stays up
        // differs between networks.
        if (t.Sni.Length > 0) { args.Add("-s"); args.Add(t.Sni); }

        return args;
    }

    private static string Num(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The config usque should read for this attempt.
    ///
    /// <para>Normally the registered one. When an entry point is being overridden there is no flag to do
    /// it with — usque takes the address from the config — so a derived copy is written beside it with
    /// just the endpoint fields changed. The registration itself, keys and token included, is never
    /// touched: these copies are disposable and the real file stays the single source of the device.</para></summary>
    internal static string ConfigPathFor(MasqueTransport t)
    {
        if (t.Endpoint.Length == 0) return AppPaths.MasqueConfigFile;

        // File name derived from the address so repeated attempts reuse one file instead of littering.
        string safe = string.Concat(t.Endpoint.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        string path = Path.Combine(AppPaths.MasqueDir, $"config-{safe}.json");

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(AppPaths.MasqueConfigFile))?.AsObject();
            if (root is null) return AppPaths.MasqueConfigFile;

            bool v6 = t.Endpoint.Contains(':');
            root[v6 ? "endpoint_v6" : "endpoint_v4"] = t.Endpoint;
            root[v6 ? "endpoint_h2_v6" : "endpoint_h2_v4"] = t.Endpoint;
            File.WriteAllText(path, root.ToJsonString());
            return path;
        }
        catch
        {
            // A derived config we cannot write is not worth failing the attempt over — fall back to the
            // registered endpoint and let the result speak for the address it actually used.
            return AppPaths.MasqueConfigFile;
        }
    }

    /// <summary>Stop the proxy. Nothing about the system's networking has to be undone — that is the
    /// whole point of proxy mode — so this is only ever a process going away.</summary>
    public void Stop()
    {
        Process? proc;
        lock (_lock)
        {
            proc = _proc;
            _proc = null;
        }
        if (proc is null) return;

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { /* already gone between the check and the kill */ }
        catch { /* best effort: a proxy we cannot kill is still not a broken network */ }
        finally { proc.Dispose(); }
    }

    /// <summary>Kill a proxy left behind by a crash. Ours is the one running out of our own folder —
    /// another copy of usque is somebody else's program and none of our business.</summary>
    internal static void DropStaleProxy()
    {
        try
        {
            if (!File.Exists(AppPaths.MasqueExe)) return;

            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, AppPaths.MasqueExe,
                                      StringComparison.OrdinalIgnoreCase))
                        p.Kill(entireProcessTree: true);
                }
                catch { /* a process we may not inspect is not ours */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* never block startup on cleanup */ }
    }

    public void Dispose() => Stop();

    // ---- process plumbing --------------------------------------------------

    /// <summary>Wait until the client reports its listener is up, or give up. Reading its own output
    /// rather than probing the port keeps a throwaway TCP connection out of the SOCKS server's log, where
    /// it appeared — correctly but very alarmingly — as a client that hung up mid-negotiation.</summary>
    public async Task<bool> WaitUntilListeningAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Task<bool>? wait;
        lock (_lock) wait = _listening?.Task;
        if (wait is null) return false;

        var done = await Task.WhenAny(wait, Task.Delay(timeout, ct)).ConfigureAwait(false);
        return done == wait && wait.Result;
    }

    private void Emit(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.Contains(ListeningMarker, StringComparison.OrdinalIgnoreCase))
            _listening?.TrySetResult(true);
        LogLine?.Invoke("[masque] " + line.Trim());
    }

    internal readonly record struct RunResult(int ExitCode, string Output, string Error);

    private static string Describe(RunResult r)
    {
        string text = r.Error.Trim().Length > 0 ? r.Error.Trim() : r.Output.Trim();
        return text.Length > 0 ? text : Loc.T("код выхода {0}", r.ExitCode);
    }

    /// <summary>Run the client to completion, hidden. Synchronous on purpose: the only caller is already
    /// on a background thread. A client that hangs is killed rather than left holding the button.</summary>
    private static RunResult Run(IEnumerable<string> args, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(AppPaths.MasqueExe)
            {
                WorkingDirectory = AppPaths.MasqueDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return new RunResult(-1, "", Loc.T("процесс не запустился"));

            // Read both pipes before waiting: a full pipe buffer deadlocks the child.
            Task<string> outp = p.StandardOutput.ReadToEndAsync();
            Task<string> err = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new RunResult(-1, "", Loc.T("превышено время ожидания"));
            }

            return new RunResult(p.ExitCode, outp.GetAwaiter().GetResult(), err.GetAwaiter().GetResult());
        }
        catch (Exception ex) { return new RunResult(-1, "", ex.Message); }
    }
}
