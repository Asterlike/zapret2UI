using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ZapretUI.Models;

namespace ZapretUI.Services;

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

    /// <summary>The preset that is currently running (null when stopped).</summary>
    public Preset? ActivePreset { get; private set; }

    /// <summary>Raised on the thread pool for every line of engine output.</summary>
    public event Action<string>? LogLine;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    public event Action<EngineState>? StateChanged;

    public bool IsRunning => State == EngineState.Running;

    /// <summary>Build the full winws2 argument list for a preset (for preview/launch).</summary>
    public static List<string> BuildArguments(Preset preset, string? hostlistPath,
        bool gameFilter = false, bool bypassAll = false)
    {
        var args = new List<string>
        {
            // Mandatory: load the bundled Lua libraries (helpers + strategy library).
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-lib.lua"),
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-antidpi.lua"),
        };

        // {WF_TCP}/{WF_UDP}: WinDivert capture width. Game filter ON → all high ports (games + media);
        // OFF (default) → narrow (80,443 + Discord voice ranges) so game traffic is left untouched.
        string wfTcp = gameFilter ? "--wf-tcp-out=80,443-65535" : "--wf-tcp-out=80,443";
        string wfUdp = gameFilter ? "--wf-udp-out=443-65535" : "--wf-udp-out=443,19294-19344,50000-50100";

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
        string excludeName = bypassAll ? EffectiveExcludeName() : "exclude";

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
        return bypassAll ? args : ScopeCatchAllToTargets(args);
    }

    /// <summary>
    /// Allow-list mode: every profile carrying a <c>--hostlist-exclude</c> is a catch-all
    /// ("desync everything except these"). Re-point it at the user's custom targets
    /// (<c>--hostlist=&lt;targets&gt;</c>) so they still bypass, or drop the whole profile when there
    /// are no targets. Profiles are delimited by <c>--new</c>; the catch-all is never the first
    /// profile, so dropping it together with its leading <c>--new</c> keeps the arg list valid.
    /// </summary>
    private static List<string> ScopeCatchAllToTargets(List<string> args)
    {
        string targetsPath = Path.Combine(AppPaths.ListsDir, TargetService.AggregateName + ".txt");
        bool hasTargets = File.Exists(targetsPath) &&
                          File.ReadAllLines(targetsPath).Any(l => l.Trim().Length > 0);

        var result = new List<string>();
        var profile = new List<string>();

        void Flush()
        {
            if (profile.Count == 0) return;
            int ex = profile.FindIndex(a => a.StartsWith("--hostlist-exclude=", StringComparison.Ordinal));
            if (ex < 0)
                result.AddRange(profile);                       // normal (listed) profile — keep
            else if (hasTargets)
            {
                profile[ex] = $"--hostlist={targetsPath}";       // catch-all → targets-only
                result.AddRange(profile);
            }
            // else: catch-all with no targets → drop the whole profile (incl. its leading --new)
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
    /// targets, writes <c>exclude-eff.txt</c> = exclude.txt minus the target domains and returns
    /// "exclude-eff" so the active strategy desyncs those domains; otherwise returns "exclude".
    /// </summary>
    private static string EffectiveExcludeName()
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

            var kept = File.ReadAllLines(excludePath)
                .Where(line =>
                {
                    string t = line.Trim().ToLowerInvariant();
                    return t.Length == 0 || !targets.Contains(t);
                });
            File.WriteAllLines(Path.Combine(AppPaths.ListsDir, "exclude-eff.txt"), kept);
            return "exclude-eff";
        }
        catch { return "exclude"; }
    }

    /// <summary>Human-readable preview of the command line that will be launched.</summary>
    public static string PreviewCommandLine(Preset preset, string? hostlistPath,
        bool gameFilter = false, bool bypassAll = false)
    {
        var sb = new StringBuilder("winws2.exe");
        foreach (var a in BuildArguments(preset, hostlistPath, gameFilter, bypassAll))
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
            foreach (var a in BuildArguments(preset, hostlistPath, GameFilter, BypassAllSites))
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

            if (_job != IntPtr.Zero)
                AssignProcessToJobObject(_job, proc.Handle);
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
