using System.Diagnostics;
using System.Text;

namespace Zapret2UI.Services;

/// <summary>
/// Runs a THROWAWAY winws2 process for probing — shared by the auto-selector and the strategy
/// generator, which both cycle "launch a candidate → probe the goal hosts → kill it" dozens of times.
///
/// Deliberately NOT <see cref="EngineService"/>: that one owns the long-lived bypass process (state
/// machine, journal streaming, TCP-timestamps tuning, restart-on-strategy-change) and must stay
/// separate. This class only starts, waits for readiness, and kills.
///
/// It exists because the two callers previously carried byte-identical private copies of all of this,
/// and the readiness wait below had to be written twice when it was introduced. Argument building
/// stays with the callers — that part legitimately differs (bypassAll vs gameFilter).
/// </summary>
internal sealed class ProbeEngineRunner : IDisposable
{
    /// <summary>Cap on the WinDivert-attach wait. Equal to the fixed delay this replaced, so an absent
    /// or renamed log line degrades to exactly the old timing rather than to a hang.</summary>
    private const int ReadyCapMs = 1500;

    /// <summary>Let WinDivert go live after the log line before traffic is probed.</summary>
    private const int SettleMs = 150;

    /// <summary>Grace period for the killed process to actually detach from WinDivert.</summary>
    private const int ExitWaitMs = 4000;

    private Process? _proc;
    // Signalled when the running candidate's winws2 reports WinDivert is attached. Recreated per
    // Start; each process's handlers capture their own instance.
    private TaskCompletionSource? _ready;

    private readonly string? _exeOverride;
    private readonly string? _workDirOverride;

    /// <summary>Production use: the installed winws2.</summary>
    public ProbeEngineRunner() { }

    /// <summary>Test seam. winws2 needs admin and monopolises WinDivert, so the process lifecycle here
    /// (readiness detection, early-exit unblock, kill) can only be exercised with a stand-in binary.</summary>
    internal ProbeEngineRunner(string exePath, string workDir)
    {
        _exeOverride = exePath;
        _workDirOverride = workDir;
    }

    /// <summary>False once the candidate's engine has died — e.g. bad arguments — so the caller can
    /// score it as a total failure instead of probing against no engine at all.</summary>
    public bool IsAlive => _proc is { HasExited: false };

    /// <summary>Test seam: identifies the current process so a test can prove that a second
    /// <see cref="Start"/> really replaced the first one. 0 when nothing is running.</summary>
    internal int ProcessId
    {
        get { try { return _proc?.Id ?? 0; } catch { return 0; } }
    }

    public void Start(List<string> args)
    {
        Stop(); // defensive: never leave a previous candidate's winws2 alive (two engines would fight
                // over WinDivert and poison every subsequent probe).
        var psi = new ProcessStartInfo
        {
            // Resolved per Start (not cached in the ctor) so the paths keep the same late binding they
            // had when each service built the ProcessStartInfo itself.
            FileName = _exeOverride ?? AppPaths.WinwsExe,
            WorkingDirectory = _workDirOverride ?? AppPaths.EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ready = ready;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Signal readiness on the WinDivert line; also unblock on early exit (bad args) so a candidate
        // whose engine dies instantly fails fast instead of waiting out the cap.
        p.OutputDataReceived += (_, e) => { if (IsWinDivertReady(e.Data)) ready.TrySetResult(); };
        p.ErrorDataReceived += (_, e) => { if (IsWinDivertReady(e.Data)) ready.TrySetResult(); };
        p.Exited += (_, _) => ready.TrySetResult();
        _proc = p;
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    /// <summary>Wait until the engine reports WinDivert is attached, capped at <see cref="ReadyCapMs"/>
    /// so an absent/changed log line falls back to the old fixed-delay behaviour, then settle briefly
    /// so the filter is live before probing.</summary>
    public async Task WaitReadyAsync(CancellationToken ct)
    {
        var ready = _ready;
        if (ready is null)
        {
            await Task.Delay(ReadyCapMs, ct).ConfigureAwait(false);
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadyCapMs);
        try
        {
            await ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            await Task.Delay(SettleMs, ct).ConfigureAwait(false); // skipped on timeout — the cap was already waited
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // No ready line within the cap → behave exactly like the old fixed delay.
        }
    }

    public void Stop()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(ExitWaitMs);
            }
        }
        catch { /* already gone / access denied — the finally still clears the handle */ }
        finally
        {
            try { _proc?.Dispose(); } catch { /* ignore */ }
            _proc = null;
        }
    }

    /// <summary>True for a winws2 log line that means WinDivert is attached and capturing.</summary>
    private static bool IsWinDivertReady(string? line) =>
        line is not null &&
        (line.Contains("capture is started", StringComparison.OrdinalIgnoreCase) ||
         line.Contains("windivert initialized", StringComparison.OrdinalIgnoreCase));

    public void Dispose() => Stop();
}
