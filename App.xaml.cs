using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zapret2UI.Services;
using Zapret2UI.ViewModels;

namespace Zapret2UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteFatal(args.ExceptionObject as Exception);

        // Screenshot harness: `Zapret2UI.exe --screenshot <outDir>` renders each tab to PNG and exits
        // (used to regenerate docs/*.png without a manual desktop capture). Needs no admin, so flip the
        // manifest to asInvoker while capturing, then restore it.
        int si = Array.FindIndex(e.Args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (si >= 0)
        {
            string outDir = si + 1 < e.Args.Length ? e.Args[si + 1] : ".";
            _ = RunScreenshotsAsync(outDir);
            return;
        }

        // Telegram-proxy self-test harness: `Zapret2UI.exe --tgproxytest <outFile>` probes the upstream
        // paths to Telegram (DoH / DNS / direct IP / Cloudflare fronts) and writes the report to a file,
        // then exits. Needs no admin (loopback + outbound TLS), so it can be run to diagnose a user whose
        // proxy "pings but won't connect".
        int ti = Array.FindIndex(e.Args, a => a.Equals("--tgproxytest", StringComparison.OrdinalIgnoreCase));
        if (ti >= 0)
        {
            string outFile = ti + 1 < e.Args.Length ? e.Args[ti + 1] : "tgproxytest.txt";
            _ = RunTgProxyTestAsync(outFile);
            return;
        }

        // Bridge self-test: `Zapret2UI.exe --tgbridgetest <outFile>` drives the REAL bridge from a loopback
        // client and checks that Telegram's resPQ survives the round-trip decodable (re-encryption/splitter
        // correctness) — isolates a bridge bug from a censored-network drop. No admin needed.
        int bi = Array.FindIndex(e.Args, a => a.Equals("--tgbridgetest", StringComparison.OrdinalIgnoreCase));
        if (bi >= 0)
        {
            string outFile = bi + 1 < e.Args.Length ? e.Args[bi + 1] : "tgbridgetest.txt";
            _ = RunTgBridgeTestAsync(outFile);
            return;
        }

        // Engine command-line dump: `Zapret2UI.exe --enginedump <outFile>` seeds the bundled lists and
        // writes the winws2 command line for the recommended preset — so the Telegram-proxy coverage
        // profile (and the seeded fronts list) can be inspected without admin. The engine itself still
        // needs elevation to actually RUN, so this only verifies argument construction, not desync.
        int ni = Array.FindIndex(e.Args, a => a.Equals("--enginedump", StringComparison.OrdinalIgnoreCase));
        if (ni >= 0)
        {
            string outFile = ni + 1 < e.Args.Length ? e.Args[ni + 1] : "enginedump.txt";
            RunEngineDump(outFile);
            return;
        }

        // Single instance. A second launch (shortcut, autostart, Explorer) must NOT open a rival
        // window: two copies would fight over the winws2 engine, the WinDivert driver and the proxy's
        // listen port. Instead the copy that's already running — usually sitting in the tray — is
        // brought to the front, and this process exits. Deliberately placed after the harness modes
        // above, which return earlier: those are headless one-shots and must still run alongside a
        // live app.
        if (!ClaimSingleInstance(e.Args))
        {
            Shutdown(0);
            return;
        }

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            WriteFatal(ex);
            MessageBox.Show(ex.ToString(), "Zapret UI — критическая ошибка запуска",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Render the main window's key views to PNGs in <paramref name="outDir"/>, then exit.
    /// Drives the real UI (same renderer/fonts as production) so the docs shots match the app exactly.</summary>
    // Global\ (machine-wide), NOT Local\ (per-session): the app is launched two different ways — a
    // desktop double-click on the interactive desktop, and the elevated logon SCHEDULED TASK
    // (AutostartService). Those can land in different session namespaces, and a Local\ object created
    // in one is invisible to the other — so both would think they're the only copy and two windows
    // would open. Global\ is shared across sessions, closing that gap. The app is always elevated
    // (requireAdministrator), so it always has the privilege to create Global\ objects, and both
    // copies sit at the same integrity level and can always open each other's objects.
    private const string InstanceMutexName = @"Global\Zapret2UI.SingleInstance";
    private const string SurfaceEventName = @"Global\Zapret2UI.SurfaceWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _surfaceSignal;

    /// <summary>
    /// Try to become the one running copy. Returns true if we are it (and starts listening for later
    /// launches asking us to surface); false if another copy already holds the slot — it has been
    /// signalled to show itself and the caller should exit.
    /// </summary>
    private bool ClaimSingleInstance(string[] args)
    {
        try
        {
            return TryClaimSingleInstance(args);
        }
        catch (Exception ex)
        {
            // Never let a convenience feature stop the app from starting. If the named objects can't
            // be created or opened (e.g. policy denies Global\, or an OS quirk), degrade to the old
            // behaviour — launch normally — rather than dying with no window at all. Logged to the
            // small startup.log (not the noisy fatal.log) so a still-doubling launch is diagnosable.
            LogStartup("single-instance ОШИБКА, запускаюсь обычным образом: " + ex.Message);
            return true;
        }
    }

    private bool TryClaimSingleInstance(string[] args)
    {
        // Open/create the signal BEFORE deciding who is primary: both copies then hold a handle to the
        // same kernel object, so a launch that lands during our own startup can never find "no event
        // to poke" and be silently swallowed.
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SurfaceEventName);
        _surfaceSignal = signal;
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isPrimary);

        if (!isPrimary)
        {
            // An autostart/tray launch is meant to stay hidden, so it bows out silently instead of
            // yanking the window open; every other launch is a person asking to see the app.
            bool trayStart = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            if (!trayStart)
            {
                try { signal.Set(); } catch { /* the other copy died mid-handshake — just exit */ }
            }
            LogStartup(trayStart
                ? "вторая копия (--tray) — выхожу тихо"
                : "вторая копия — сигналю первой развернуться и выхожу");
            return false;
        }

        var waiter = new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.SurfaceWindow());
        })
        {
            IsBackground = true,
            Name = "single-instance-watch",
        };
        waiter.Start();
        LogStartup("первая копия — работаю, слушаю сигнал разворота");
        return true;
    }

    /// <summary>
    /// One-line, low-noise startup journal (logs\startup.log) recording the single-instance decision.
    /// Separate from fatal.log so that, if two windows still open on a real elevated launch, the file
    /// says plainly whether the second copy saw the first — which pinpoints whether the shared object
    /// is the problem. Best-effort; a logging failure never affects startup.
    /// </summary>
    private static void LogStartup(string msg)
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

    protected override void OnExit(ExitEventArgs e)
    {
        _surfaceSignal?.Dispose();
        _instanceMutex?.Dispose();   // closing the handle releases the mutex for the next launch
        base.OnExit(e);
    }

    private async Task RunScreenshotsAsync(string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 60, Top = 40, Width = 1280, Height = 800,
            };
            MainWindow = window;
            window.Show();

            // Let Loaded + InitializeAsync (presets/version) + the entrance animation settle.
            await Task.Delay(4000);

            var vm = (MainViewModel)window.DataContext;

            vm.IsSimpleMode = true;
            await SettleAndSnap(window, Path.Combine(outDir, "home-simple.png"));

            vm.IsSimpleMode = false;
            await Task.Delay(400);
            foreach (var (idx, file) in new[]
            {
                (0, "home-advanced.png"), (1, "strategies.png"), (2, "hostlists.png"),
                (3, "diagnostics.png"), (5, "telegram.png"), (6, "settings.png"),
            })
            {
                vm.SelectedTabIndex = idx;
                await SettleAndSnap(window, Path.Combine(outDir, file));
            }

            // Modals and dialogs get their own shots: they render ON TOP of the window, so a broken
            // style in one of them cannot be spotted in any of the tab screenshots above.
            // Walkthrough in both states: the held confirm button (first launch) and, after the
            // countdown drains, the unlocked one — proves the timer actually releases the button.
            vm.SelectedTabIndex = 0;
            vm.OpenWelcome(withCountdown: true);
            await SettleAndSnap(window, Path.Combine(outDir, "welcome.png"));
            await Task.Delay(7000);
            await SettleAndSnap(window, Path.Combine(outDir, "welcome-ready.png"));
            vm.ShowWelcome = false;

            vm.ShowHowItWorks = true;
            await SettleAndSnap(window, Path.Combine(outDir, "howitworks.png"));
            vm.ShowHowItWorks = false;

            // Both branches of the environment check: findings (long "Что делать" text) and all-clear.
            await SnapDialog(window, ConflictScanService.ScanEnvironment(false, false),
                Path.Combine(outDir, "envcheck.png"));
            await SnapDialog(window, Array.Empty<EnvFinding>(),
                Path.Combine(outDir, "envcheck-clean.png"));

            window.Close();
        }
        catch (Exception ex) { WriteFatal(ex); }
        finally { Shutdown(0); }
    }

    /// <summary>Run the Telegram proxy's upstream self-test and write the report to <paramref name="outFile"/>,
    /// then exit. Mirrors what the in-app «Проверить соединение» button does, but headless.</summary>
    private async Task RunTgProxyTestAsync(string outFile)
    {
        var sb = new StringBuilder();
        try
        {
            using var svc = new TelegramProxyService();
            svc.LogLine += line => sb.AppendLine(line);
            await svc.SelfTestAsync();
        }
        catch (Exception ex) { sb.AppendLine("EXC: " + ex); }
        finally
        {
            try { File.WriteAllText(outFile, sb.ToString()); } catch { /* best effort */ }
            Shutdown(0);
        }
    }

    /// <summary>Start the proxy and run the loopback bridge self-test, writing the verdict to a file.</summary>
    private async Task RunTgBridgeTestAsync(string outFile)
    {
        var sb = new StringBuilder();
        TelegramProxyService? svc = null;
        try
        {
            svc = new TelegramProxyService();
            svc.LogLine += line => sb.AppendLine(line);
            svc.Start();
            await Task.Delay(400);
            await svc.BridgeSelfTestAsync();
        }
        catch (Exception ex) { sb.AppendLine("EXC: " + ex); }
        finally
        {
            try { svc?.Stop(); } catch { /* ignore */ }
            try { File.WriteAllText(outFile, sb.ToString()); } catch { /* best effort */ }
            Shutdown(0);
        }
    }

    /// <summary>Seed the bundled lists and dump the winws2 command line for the recommended preset to a
    /// file, then exit — lets the Telegram-proxy coverage profile be inspected without admin (the engine
    /// still needs elevation to actually run). Mirrors the Settings command-line preview.</summary>
    private void RunEngineDump(string outFile)
    {
        var sb = new StringBuilder();
        try
        {
            AppPaths.EnsureCreated();
            var hostlists = new HostlistService();
            hostlists.SeedDefaults(); // writes lists/tgproxy-fronts.txt from the proxy balancer
            var presets = new PresetService();
            var rec = presets.All.FirstOrDefault(p => p.IsRecommended) ?? presets.All[0];

            sb.AppendLine("# preset: " + rec.Name);
            sb.AppendLine(EngineService.PreviewCommandLine(rec, null));
            sb.AppendLine();
            sb.AppendLine($"# tgproxy-fronts.txt ({hostlists.ReadDomains("tgproxy-fronts").Count} domains):");
            sb.AppendLine(hostlists.Read("tgproxy-fronts"));
        }
        catch (Exception ex) { sb.AppendLine("EXC: " + ex); }
        finally
        {
            try { File.WriteAllText(outFile, sb.ToString()); } catch { /* best effort */ }
            Shutdown(0);
        }
    }

    /// <summary>Render the environment-check dialog non-modally (harness only) and capture it.</summary>
    private static async Task SnapDialog(Window owner, IReadOnlyList<EnvFinding> findings, string path)
    {
        var dlg = ConflictDialog.CreateForHarness(findings);
        dlg.Owner = owner;
        dlg.Show();
        await SettleAndSnap(dlg, path);
        dlg.Close();
    }

    private static async Task SettleAndSnap(Window w, string path)
    {
        w.UpdateLayout();
        await Task.Delay(700); // let the tab-switch fade + layout settle before capturing
        int width = (int)Math.Ceiling(w.ActualWidth);
        int height = (int)Math.Ceiling(w.ActualHeight);
        if (width <= 0 || height <= 0) return;
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteFatal(e.Exception);
        MessageBox.Show(e.Exception.Message, "Zapret UI — ошибка",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteFatal(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(
                Path.Combine(AppPaths.LogsDir, "fatal.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
