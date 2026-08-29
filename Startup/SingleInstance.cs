using System.IO;
using Zapret2UI.Services.Infrastructure;

namespace Zapret2UI.Startup;

/// <summary>
/// The one-running-copy lock. A second launch (shortcut, autostart, Explorer) must NOT open a rival
/// window: two copies would fight over the winws2 engine, the WinDivert driver and the proxy's listen
/// port. Instead the copy already running — usually sitting in the tray — is asked to come to the
/// front, and the newcomer exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    // Global\ (machine-wide), NOT Local\ (per-session): the app is launched two different ways — a
    // desktop double-click on the interactive desktop, and the elevated logon SCHEDULED TASK
    // (AutostartService). Those can land in different session namespaces, and a Local\ object created
    // in one is invisible to the other — so both would think they're the only copy and two windows
    // would open. Global\ is shared across sessions, closing that gap. The app is always elevated
    // (requireAdministrator), so it always has the privilege to create Global\ objects, and both
    // copies sit at the same integrity level and can always open each other's objects.
    private const string MutexName = @"Global\Zapret2UI.SingleInstance";
    private const string SurfaceEventName = @"Global\Zapret2UI.SurfaceWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _surfaceSignal;

    private SingleInstance() { }

    /// <summary>
    /// Try to become the one running copy. Returns the claim when we are it — and starts listening for
    /// later launches asking us to surface, calling <paramref name="onSurfaceRequested"/> for each —
    /// or <c>null</c> when another copy already holds the slot, in which case it has been signalled to
    /// show itself and the caller should exit.
    /// </summary>
    internal static SingleInstance? Claim(string[] args, Action onSurfaceRequested)
    {
        var claim = new SingleInstance();
        try
        {
            return claim.TryClaim(args, onSurfaceRequested) ? claim : null;
        }
        catch (Exception ex)
        {
            // Never let a convenience feature stop the app from starting. If the named objects can't
            // be created or opened (e.g. policy denies Global\, or an OS quirk), degrade to the old
            // behaviour — launch normally — rather than dying with no window at all. Logged to the
            // small startup.log (not the noisy fatal.log) so a still-doubling launch is diagnosable.
            Log("single-instance ОШИБКА, запускаюсь обычным образом: " + ex.Message);
            return claim;
        }
    }

    private bool TryClaim(string[] args, Action onSurfaceRequested)
    {
        // Open/create the signal BEFORE deciding who is primary: both copies then hold a handle to the
        // same kernel object, so a launch that lands during our own startup can never find "no event
        // to poke" and be silently swallowed.
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SurfaceEventName);
        _surfaceSignal = signal;
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isPrimary);

        if (!isPrimary)
        {
            // An autostart/tray launch is meant to stay hidden, so it bows out silently instead of
            // yanking the window open; every other launch is a person asking to see the app.
            bool trayStart = CommandLine.Has(args, "--tray");
            if (!trayStart)
            {
                try { signal.Set(); } catch { /* the other copy died mid-handshake — just exit */ }
            }
            Log(trayStart
                ? "вторая копия (--tray) — выхожу тихо"
                : "вторая копия — сигналю первой развернуться и выхожу");
            return false;
        }

        var waiter = new Thread(() =>
        {
            while (signal.WaitOne())
                onSurfaceRequested();
        })
        {
            IsBackground = true,
            Name = "single-instance-watch",
        };
        waiter.Start();
        Log("первая копия — работаю, слушаю сигнал разворота");
        return true;
    }

    public void Dispose()
    {
        _surfaceSignal?.Dispose();
        _mutex?.Dispose();   // closing the handle releases the mutex for the next launch
    }

    /// <summary>
    /// One-line, low-noise startup journal (logs\startup.log) recording the single-instance decision.
    /// Separate from fatal.log so that, if two windows still open on a real elevated launch, the file
    /// says plainly whether the second copy saw the first — which pinpoints whether the shared object
    /// is the problem. Best-effort; a logging failure never affects startup.
    /// </summary>
    private static void Log(string msg)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(
                Path.Combine(AppPaths.LogsDir, "startup.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] pid {Environment.ProcessId}: {msg}\n");
        }
        catch { }
    }
}
