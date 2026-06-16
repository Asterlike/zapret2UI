using System.Diagnostics;
using System.IO;
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

    public EngineState State { get; private set; } = EngineState.Stopped;

    /// <summary>The preset that is currently running (null when stopped).</summary>
    public Preset? ActivePreset { get; private set; }

    /// <summary>Raised on the thread pool for every line of engine output.</summary>
    public event Action<string>? LogLine;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    public event Action<EngineState>? StateChanged;

    public bool IsRunning => State == EngineState.Running;

    /// <summary>Build the full winws2 argument list for a preset (for preview/launch).</summary>
    public static List<string> BuildArguments(Preset preset, string? hostlistPath)
    {
        var args = new List<string>
        {
            // Mandatory: load the bundled Lua libraries (helpers + strategy library).
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-lib.lua"),
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-antidpi.lua"),
        };

        string hostlistArg = !string.IsNullOrWhiteSpace(hostlistPath)
            ? $"--hostlist={hostlistPath}"
            : "";

        // {IPSET} expands to --ipset=<file> only if the Discord ipset has been built.
        string ipsetArg = File.Exists(AppPaths.IpsetDiscordFile)
            ? $"--ipset={AppPaths.IpsetDiscordFile}"
            : "";

        foreach (var raw in preset.Args)
        {
            string a = raw
                .Replace("{FILES}", AppPaths.FilesDir, StringComparison.Ordinal)
                .Replace("{WF}", AppPaths.WinDivertFilterDir, StringComparison.Ordinal)
                .Replace("{HOSTLIST}", hostlistArg, StringComparison.Ordinal)
                .Replace("{IPSET}", ipsetArg, StringComparison.Ordinal);

            // Named hostlist token {HOSTLIST:name} -> --hostlist=<lists>\name.txt (or "" if the
            // list is missing) — lets one preset route different SNIs to different strategies.
            a = ExpandNamedHostlists(a);

            // A {HOSTLIST}/{IPSET} that resolved to nothing leaves an empty token — drop it.
            if (a.Length == 0) continue;
            args.Add(a);
        }
        return args;
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

    /// <summary>Human-readable preview of the command line that will be launched.</summary>
    public static string PreviewCommandLine(Preset preset, string? hostlistPath)
    {
        var sb = new StringBuilder("winws2.exe");
        foreach (var a in BuildArguments(preset, hostlistPath))
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
            foreach (var a in BuildArguments(preset, hostlistPath))
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
    }
}
