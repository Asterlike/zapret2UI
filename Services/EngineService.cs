using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Zapret2UI.Models;

namespace Zapret2UI.Services;

public enum EngineState { Stopped, Running, Starting, Stopping }

/// <summary>
/// Owns the winws2.exe child process: builds its arguments from a preset,
/// starts/stops it, and streams its output to subscribers.
/// </summary>
public sealed class EngineService : IDisposable
{
    private readonly object _lock = new();
    private Process? _proc;
    private StreamWriter? _logFile;
    // Job object with KILL_ON_JOB_CLOSE: winws2 is assigned to it so the OS terminates the engine
    // whenever this (the parent) process dies — including a crash or Task Manager kill, where the
    // normal Stop()/Dispose() path never runs and would otherwise orphan winws2 + WinDivert.
    private IntPtr _job = IntPtr.Zero;

    // Enables Windows TCP timestamps for the duration of a run (and restores the prior value on stop),
    // so the tcp_ts/tcp_ts_up fooling used by most presets isn't a silent no-op. See TcpTimestampsService.
    private readonly TcpTimestampsService _tsTune = new();

    public EngineState State { get; private set; } = EngineState.Stopped;

    /// <summary>Flowseal-style game filter: widen the WF capture to all high ports (&gt;1023) when true.
    /// Set from settings by the VM; consumed by <see cref="Start"/> and the {WF_TCP}/{WF_UDP} tokens.</summary>
    public bool GameFilter { get; set; }

    /// <summary>When false (default) the engine runs in allow-list mode like Flowseal: only the
    /// per-service hostlists (YouTube/Discord/Telegram) + the user's custom targets/hostlists are
    /// desynced, and the catch-all "all other TLS/QUIC" profiles are re-scoped to targets or dropped,
    /// so games/apps not in any list are never touched. True = bypass every site (catch-all, kept
    /// safe by the exclude list). Set from settings by the VM; consumed by <see cref="Start"/>.</summary>
    public bool BypassAllSites { get; set; }

    /// <summary>When true, the desynced services' QUIC (HTTP/3) is DROPPED so the browser falls back to
    /// TCP/H2 (which the TLS profiles handle). The July-2026 TSPU drops QUIC v1 to :443 with payload
    /// ≥1001 bytes — there the fake-QUIC bypass can't win, but forcing TCP does. Set from settings.</summary>
    public bool DisableQuic { get; set; }

    /// <summary>When true, the engine ALSO covers the built-in Telegram proxy's own Cloudflare upstream
    /// (443) with a TLS desync so mobile DPI can't kill its tunnel mid-stream. Off by default — most
    /// users don't need it, and it's inert unless the proxy is actually connecting. Set from settings.</summary>
    public bool CoverTgProxy { get; set; }

    /// <summary>The preset that is currently running (null when stopped).</summary>
    public Preset? ActivePreset { get; private set; }

    /// <summary>Raised on the thread pool for every line of engine output.</summary>
    public event Action<string>? LogLine;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    public event Action<EngineState>? StateChanged;

    public bool IsRunning => State == EngineState.Running;

    /// <summary>Build the full winws2 argument list for a preset (for preview/launch).
    /// <paramref name="forLaunch"/> must be true ONLY for a real engine start: it lets the
    /// effective-exclude list be materialised to disk. The preview path passes false so that
    /// merely showing the command line never writes a file.</summary>
    public static List<string> BuildArguments(Preset preset, string? hostlistPath,
        bool gameFilter = false, bool bypassAll = false, bool disableQuic = false,
        bool coverTgProxy = false, bool forLaunch = false)
    {
        var args = new List<string>
        {
            // Mandatory: load the bundled Lua libraries, in the SAME order and set as the canonical
            // zapret2 launch (init.d/.../functions): base helpers, the DPI-attack verbs, AND the
            // automation/orchestration library. zapret-auto.lua is where the orchestrators live
            // (circular/repeater/stopif/condition) — without it winws2 rejects e.g. the adaptive
            // circular preset with "desync function 'circular' does not exist" (exit 87). It's inert
            // for presets that don't use an orchestrator, so it's safe to always load.
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-lib.lua"),
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-antidpi.lua"),
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-auto.lua"),
        };

        // {WF_TCP}/{WF_UDP}: WinDivert capture width (the ports the kernel hands to winws2 — a profile's
        // --filter-tcp can only match what was captured). TCP is ALWAYS 80,443-65535: the per-service
        // profiles filter --filter-tcp=443-65535, and Discord media/CDN + HTTPS on non-443 ports live on
        // high TCP ports, so a narrow 80,443 capture would silently starve those filters (a green :443
        // checker probe hides it — and the auto-select catalog already tests with the wide capture, so a
        // narrow runtime capture = "green on test, dead in practice"). Non-listed traffic stays safe via
        // SNI/hostlist scoping (allow-list mode drops the catch-alls), NOT via a narrow capture — so
        // widening TCP can't touch games. The game filter only widens UDP to all high ports; the default
        // UDP is QUIC(443) + STUN + the WHOLE Discord voice range 50000-65535 (a narrower voice range
        // shows up as a permanent 5000 ping).
        string wfTcp = "--wf-tcp-out=80,443-65535";
        string wfUdp = gameFilter ? "--wf-udp-out=443-65535" : "--wf-udp-out=443,19294-19344,50000-65535";

        string hostlistArg = !string.IsNullOrWhiteSpace(hostlistPath)
            ? $"--hostlist={hostlistPath}"
            : "";

        // {IPSET} expands to --ipset=<file> only if the Discord ipset has been built.
        string ipsetArg = File.Exists(AppPaths.IpsetDiscordFile)
            ? $"--ipset={AppPaths.IpsetDiscordFile}"
            : "";

        // Custom targets: the catch-all profile must actually desync the user's target domains,
        // so subtract them from the exclude list (otherwise targets like yandex.ru — protected by
        // the default exclude — would never be bypassed). Done by swapping the catch-all's exclude
        // for a filtered copy; if there are no targets, the original exclude is used unchanged.
        // Only needed when actually bypassing everything — in allow-list mode the catch-all is
        // re-scoped/dropped afterwards (see ScopeCatchAllToTargets), so the exclude is irrelevant.
        string excludeName = bypassAll ? EffectiveExcludeName(forLaunch) : "exclude";

        foreach (var raw in preset.Args)
        {
            string a = raw
                .Replace("{FILES}", AppPaths.FilesDir, StringComparison.Ordinal)
                .Replace("{WF}", AppPaths.WinDivertFilterDir, StringComparison.Ordinal)
                .Replace("{WF_TCP}", wfTcp, StringComparison.Ordinal)
                .Replace("{WF_UDP}", wfUdp, StringComparison.Ordinal)
                .Replace("{HOSTLIST}", hostlistArg, StringComparison.Ordinal)
                .Replace("{EXCLUDE:exclude}", "{EXCLUDE:" + excludeName + "}", StringComparison.Ordinal)
                .Replace("{IPSET}", ipsetArg, StringComparison.Ordinal);

            // Named hostlist token {HOSTLIST:name} -> --hostlist=<lists>\name.txt (or "" if the
            // list is missing) — lets one preset route different SNIs to different strategies.
            a = ExpandNamedHostlists(a);
            // Named exclude token {EXCLUDE:name} -> --hostlist-exclude=<lists>\name.txt (or "")
            // — protects sensitive domains (banks/gov/…) from a catch-all desync profile.
            a = ExpandNamedExcludes(a);
            // Named ipset token {IPSET:name} -> --ipset=<lists>\ipset-name.txt (or "" if missing)
            // — IP-based matching for protocols without SNI (e.g. Telegram MTProto).
            a = ExpandNamedIpsets(a);

            // A {HOSTLIST}/{IPSET} that resolved to nothing leaves an empty token — drop it.
            if (a.Length == 0) continue;
            args.Add(a);
        }

        // Allow-list mode (default): the catch-all "all other TLS/QUIC" profiles are what break
        // non-listed games/apps. Re-scope them to the user's custom targets, or drop them, so only
        // the explicit lists + targets get desynced (like Flowseal).
        var scoped = bypassAll ? args : ScopeCatchAllToTargets(args);
        // "QUIC off": force the desynced services onto TCP by dropping their QUIC instead of faking it.
        var result = disableQuic ? ForceQuicDrop(scoped) : scoped;
        // Optionally cover the built-in Telegram proxy's own Cloudflare upstream (443) so mobile DPI can't
        // kill its tunnel mid-stream. Off by default (opt-in setting) — appended AFTER the scope/QUIC
        // transforms so it stays hostlist-scoped: never treated as a catch-all (nor dropped) nor QUIC-rewritten.
        if (coverTgProxy) AppendTgProxyCoverage(result);
        return result;
    }

    /// <summary>Append a TLS-desync profile for the built-in Telegram proxy's own Cloudflare upstream
    /// (<c>kws*.&lt;front&gt;.co.uk</c>). Since 2026 the mobile TSPU corrupts the proxy's WebSocket tunnel
    /// mid-stream — the WS upgrade succeeds and a few frames relay, then the stream dies — and the proxy
    /// alone can't beat that; only a continuous packet-level desync of those 443 connections survives it.
    /// The profile is SCOPED to the fronts hostlist, so it is inert unless the proxy is actually
    /// connecting (no matching SNI ⇒ winws2 touches nothing): safe for users who don't run the proxy or
    /// aren't censored. The fooling is the recommended combo's own gateway/home-NAT-safe pipeline
    /// (<c>hostfakesplit</c> + negative <c>tcp_ts</c> + <c>tcp_md5</c>) — transparent on an uncensored
    /// network, since the fake segment is rejected by the real server (unkeyed TCP-MD5) and the real
    /// ClientHello arrives intact. No-op until the fronts list has been seeded.</summary>
    private static void AppendTgProxyCoverage(List<string> args)
    {
        string fronts = AppPaths.TgProxyFrontsFile;
        if (!File.Exists(fronts)) return;
        args.Add("--new");
        args.Add("--filter-tcp=443-65535");
        args.Add("--filter-l7=tls");
        args.Add($"--hostlist={fronts}");
        args.Add("--out-range=-d10");
        args.Add("--payload=tls_client_hello");
        args.Add("--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4");
    }

    /// <summary>
    /// Rewrite every QUIC profile (<c>--filter-l7=quic</c>) so its desync becomes a plain
    /// <c>--lua-desync=drop</c>: winws2 drops the outbound QUIC ClientHello, the browser gives up on
    /// HTTP/3 and falls back to TCP/H2 (handled by the TLS profiles). Used when the TSPU kills QUIC v1
    /// anyway, so a fake-QUIC bypass is pointless. The Discord VOICE profile (<c>--filter-l7=discord,stun</c>,
    /// not <c>quic</c>) is left untouched — dropping it would kill voice. Profiles are delimited by
    /// <c>--new</c>; the filter/payload/hostlist args are kept, only the <c>--lua-desync=</c> line(s) change.
    /// </summary>
    private static List<string> ForceQuicDrop(List<string> args)
    {
        var result = new List<string>();
        var profile = new List<string>();

        void Flush()
        {
            if (profile.Count == 0) return;
            bool isQuic = profile.Any(a =>
                a.StartsWith("--filter-l7=", StringComparison.Ordinal) &&
                a.Contains("quic", StringComparison.Ordinal));
            if (isQuic)
            {
                bool dropAdded = false;
                foreach (var a in profile)
                {
                    if (a.StartsWith("--lua-desync=", StringComparison.Ordinal))
                    {
                        if (!dropAdded) { result.Add("--lua-desync=drop"); dropAdded = true; }
                        // drop the remaining fake desync lines of this QUIC profile
                    }
                    else result.Add(a);
                }
                if (!dropAdded) result.Add("--lua-desync=drop");
            }
            else result.AddRange(profile);
            profile.Clear();
        }

        foreach (var a in args)
        {
            if (a == "--new") Flush();
            profile.Add(a);
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Allow-list mode: neutralise every "catch-all" profile so non-listed sites are never touched.
    /// A catch-all is a broad TLS/QUIC/HTTP desync that isn't pinned to a hostlist/ipset — either it
    /// carries <c>--hostlist-exclude</c> ("desync everything except these"), or it's a BARE
    /// <c>--filter-l7=tls/quic/http</c> with no list at all (e.g. a proxy profile whose
    /// <c>{IPSET:proxy}</c> resolved to nothing, or a stale global auto-select preset). Exclude
    /// catch-alls are re-pointed at the user's custom targets (<c>--hostlist=&lt;targets&gt;</c>) so
    /// those still bypass; bare globals carry no scope intent and are dropped. Profiles are delimited
    /// by <c>--new</c>; dropping one together with its leading <c>--new</c> keeps the arg list valid.
    /// The FIRST segment is always kept — it holds the global setup (<c>--wf-*</c>, blobs) plus the
    /// first real profile.
    /// </summary>
    private static List<string> ScopeCatchAllToTargets(List<string> args)
    {
        string targetsPath = Path.Combine(AppPaths.ListsDir, TargetService.AggregateName + ".txt");
        bool hasTargets = File.Exists(targetsPath) &&
                          File.ReadAllLines(targetsPath).Any(l => l.Trim().Length > 0);

        var result = new List<string>();
        var profile = new List<string>();
        bool first = true;

        void Flush()
        {
            if (profile.Count == 0) return;
            int ex = profile.FindIndex(a => a.StartsWith("--hostlist-exclude=", StringComparison.Ordinal));
            bool scoped = profile.Any(a => a.StartsWith("--hostlist=", StringComparison.Ordinal)
                                        || a.StartsWith("--ipset=", StringComparison.Ordinal));
            // Bare global = a broad protocol desync with no list pinning it (and not the first segment,
            // which carries the global setup args + first real profile). Match PARSED comma-tokens of
            // --filter-l7, not a substring: so a proto name that merely contains "tls"/"quic"/"http"
            // can't be misclassified, and the voice profile (--filter-l7=discord,stun) is never a bare
            // global (dropping it would kill voice).
            bool bareGlobal = !first && ex < 0 && !scoped && profile.Any(a =>
                a.StartsWith("--filter-l7=", StringComparison.Ordinal) &&
                a["--filter-l7=".Length..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(p => p is "tls" or "quic" or "http"));
            first = false;

            if (ex < 0 && !bareGlobal)
                result.AddRange(profile);                       // normal (listed/scoped) profile — keep
            else if (ex >= 0 && hasTargets)
            {
                profile[ex] = $"--hostlist={targetsPath}";       // exclude catch-all → targets-only
                result.AddRange(profile);
            }
            // else: exclude catch-all without targets, OR any bare global → drop the whole profile.
            profile.Clear();
        }

        foreach (var a in args)
        {
            if (a == "--new") Flush();   // close the previous profile before starting a new one
            profile.Add(a);
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Expand every <c>{HOSTLIST:name}</c> token in an argument to
    /// <c>--hostlist=&lt;lists&gt;\name.txt</c>, or to an empty string if that list does
    /// not exist (the profile then matches by its other filters).
    /// </summary>
    private static string ExpandNamedHostlists(string a)
    {
        const string marker = "{HOSTLIST:";
        int i;
        while ((i = a.IndexOf(marker, StringComparison.Ordinal)) >= 0)
        {
            int end = a.IndexOf('}', i + marker.Length);
            if (end < 0) break;
            string name = a.Substring(i + marker.Length, end - i - marker.Length);
            string path = Path.Combine(AppPaths.ListsDir, name + ".txt");
            string repl = File.Exists(path) ? $"--hostlist={path}" : "";
            a = a[..i] + repl + a[(end + 1)..];
        }
        return a;
    }

    /// <summary>
    /// Expand every <c>{EXCLUDE:name}</c> token to
    /// <c>--hostlist-exclude=&lt;lists&gt;\name.txt</c>, or to an empty string if that
    /// list does not exist. Used on catch-all profiles so excluded domains
    /// (banks, gov, etc.) are left untouched.
    /// </summary>
    private static string ExpandNamedExcludes(string a)
    {
        const string marker = "{EXCLUDE:";
        int i;
        while ((i = a.IndexOf(marker, StringComparison.Ordinal)) >= 0)
        {
            int end = a.IndexOf('}', i + marker.Length);
            if (end < 0) break;
            string name = a.Substring(i + marker.Length, end - i - marker.Length);
            string path = Path.Combine(AppPaths.ListsDir, name + ".txt");
            string repl = File.Exists(path) ? $"--hostlist-exclude={path}" : "";
            a = a[..i] + repl + a[(end + 1)..];
        }
        return a;
    }

    /// <summary>
    /// Expand every <c>{IPSET:name}</c> token to <c>--ipset=&lt;lists&gt;\ipset-name.txt</c>,
    /// or to an empty string if that ipset file does not exist yet.
    /// </summary>
    private static string ExpandNamedIpsets(string a)
    {
        const string marker = "{IPSET:";
        int i;
        while ((i = a.IndexOf(marker, StringComparison.Ordinal)) >= 0)
        {
            int end = a.IndexOf('}', i + marker.Length);
            if (end < 0) break;
            string name = a.Substring(i + marker.Length, end - i - marker.Length);
            string path = AppPaths.IpsetFile(name);
            string repl = File.Exists(path) ? $"--ipset={path}" : "";
            a = a[..i] + repl + a[(end + 1)..];
        }
        return a;
    }

    /// <summary>
    /// Returns the exclude-list name the catch-all profiles should use. When the user has custom
    /// targets, returns "exclude-eff" (= exclude.txt minus the target domains) so the active strategy
    /// desyncs those domains; otherwise "exclude". The exclude-eff.txt file is (re)written ONLY when
    /// <paramref name="write"/> is true (a real launch) — never from the preview path. When not
    /// writing, the eff name is claimed only if the file already exists, so the preview stays honest.
    /// </summary>
    private static string EffectiveExcludeName(bool write)
    {
        try
        {
            string targetsPath = Path.Combine(AppPaths.ListsDir, TargetService.AggregateName + ".txt");
            string excludePath = Path.Combine(AppPaths.ListsDir, "exclude.txt");
            if (!File.Exists(targetsPath) || !File.Exists(excludePath)) return "exclude";

            var targets = new HashSet<string>(
                File.ReadAllLines(targetsPath).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            if (targets.Count == 0) return "exclude";

            string effPath = Path.Combine(AppPaths.ListsDir, "exclude-eff.txt");
            if (write)
            {
                var kept = File.ReadAllLines(excludePath)
                    .Where(line =>
                    {
                        string t = line.Trim().ToLowerInvariant();
                        return t.Length == 0 || !targets.Contains(t);
                    });
                File.WriteAllLines(effPath, kept);
                return "exclude-eff";
            }
            return File.Exists(effPath) ? "exclude-eff" : "exclude";
        }
        catch { return "exclude"; }
    }

    /// <summary>Human-readable preview of the command line that will be launched.</summary>
    public static string PreviewCommandLine(Preset preset, string? hostlistPath,
        bool gameFilter = false, bool bypassAll = false, bool disableQuic = false, bool coverTgProxy = false)
    {
        var sb = new StringBuilder("winws2.exe");
        foreach (var a in BuildArguments(preset, hostlistPath, gameFilter, bypassAll, disableQuic, coverTgProxy))
            sb.Append(a.Contains(' ') ? $" \"{a}\"" : $" {a}");
        return sb.ToString();
    }

    public void Start(Preset preset, string? hostlistPath)
    {
        lock (_lock)
        {
            if (State is EngineState.Running or EngineState.Starting)
                throw new InvalidOperationException("Движок уже запущен.");

            if (!File.Exists(AppPaths.WinwsExe))
                throw new FileNotFoundException("winws2.exe не найден. Дождитесь загрузки движка.");

            SetState(EngineState.Starting);

            var psi = new ProcessStartInfo
            {
                FileName = AppPaths.WinwsExe,
                WorkingDirectory = AppPaths.EngineDir, // so WinDivert.dll/.sys resolve
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in BuildArguments(preset, hostlistPath, GameFilter, BypassAllSites, DisableQuic, CoverTgProxy, forLaunch: true))
                psi.ArgumentList.Add(a);

            OpenLogFile(preset);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => OnOutput(e.Data);
            proc.ErrorDataReceived += (_, e) => OnOutput(e.Data);
            proc.Exited += OnProcessExited;

            try
            {
                proc.Start();
            }
            catch
            {
                CloseLogFile();
                SetState(EngineState.Stopped);
                throw;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Tie winws2 to the kill-on-close job so it can never outlive this app process.
            EnsureJobAndAssign(proc);

            // Turn on TCP timestamps for the session so tcp_ts/tcp_ts_up fooling actually applies
            // (Windows leaves them "allowed" by default → the fake never dies). Restored on stop.
            _tsTune.EnableForSession(Emit);

            _proc = proc;
            ActivePreset = preset;
            SetState(EngineState.Running);
            Emit($"=== Запущен пресет «{preset.Name}» (PID {proc.Id}) ===");
        }
    }

    public void Stop()
    {
        Process? proc;
        lock (_lock)
        {
            if (_proc is null || State == EngineState.Stopped) return;
            SetState(EngineState.Stopping);
            proc = _proc;
        }
        // Wait OUTSIDE the lock: OnProcessExited needs the lock to finalize, and
        // event marshaling to the UI thread must not be blocked by a held lock.
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Emit($"Ошибка остановки: {ex.Message}");
        }
        // OnProcessExited finalizes state and closes the log.
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            int? code = null;
            try { code = _proc?.ExitCode; } catch { }
            Emit($"=== Движок остановлен (код {code?.ToString() ?? "?"}) ===");

            // Restore the TCP timestamps setting we flipped at start (no-op if it was already enabled).
            _tsTune.RestoreAfterSession(Emit);

            _proc?.Dispose();
            _proc = null;
            ActivePreset = null;
            CloseLogFile();
            SetState(EngineState.Stopped);
        }
    }

    // ---- output / logging --------------------------------------------------

    private void OnOutput(string? line)
    {
        if (line is null) return;
        Emit(line);
    }

    private void Emit(string line)
    {
        try { _logFile?.WriteLine(line); _logFile?.Flush(); } catch { }
        LogLine?.Invoke(line);
    }

    private void OpenLogFile(Preset preset)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            string path = Path.Combine(AppPaths.LogsDir,
                $"engine-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _logFile = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
            _logFile.WriteLine($"# preset: {preset.Name}");
        }
        catch { _logFile = null; }
    }

    private void CloseLogFile()
    {
        try { _logFile?.Dispose(); } catch { }
        _logFile = null;
    }

    private void SetState(EngineState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
        CloseLogFile();
        // Stop() already killed winws2; releasing the job handle now is just cleanup. (If we ever
        // crash before here, the OS closes the handle for us and kill-on-close ends winws2 anyway.)
        if (_job != IntPtr.Zero) { try { CloseHandle(_job); } catch { } _job = IntPtr.Zero; }
    }

    // ---- kill-on-close job object ------------------------------------------

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    /// <summary>Create the kill-on-close job once and assign <paramref name="proc"/> to it. Best-effort:
    /// if any step fails the engine still runs (graceful Stop/Dispose covers a normal exit).</summary>
    private void EnsureJobAndAssign(Process proc)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return;

                var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
                };
                int len = Marshal.SizeOf(ext);
                IntPtr buf = Marshal.AllocHGlobal(len);
                try
                {
                    Marshal.StructureToPtr(ext, buf, false);
                    if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, buf, (uint)len))
                        _job = job;
                    else
                        CloseHandle(job);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            if (_job != IntPtr.Zero && !AssignProcessToJobObject(_job, proc.Handle))
                Emit("Предупреждение: не удалось привязать движок к job-объекту — " +
                     "автозакрытие при падении приложения может не сработать.");
        }
        catch { /* best-effort; graceful Stop() still handles a clean exit */ }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
