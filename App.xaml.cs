using System.Diagnostics;
using System.Windows;
using Zapret2UI.Harness;
using Zapret2UI.Localization;
using Zapret2UI.Services.Infrastructure;
using Zapret2UI.Startup;
using Zapret2UI.Views;

namespace Zapret2UI;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashLog.Install(this);
        AwaitPreviousCopy(e.Args);
        InitLanguage(e.Args);

        // Deliberately before the single-instance claim below: the headless modes are one-shots that
        // must still run alongside a live app, and each shuts the process down when it is done.
        if (RunHeadlessMode(e.Args)) return;

        _instance = SingleInstance.Claim(e.Args, SurfaceExistingWindow);
        if (_instance is null)
        {
            Shutdown(0);   // another copy holds the slot and has been asked to surface
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
            CrashLog.Write(ex);
            MessageBox.Show(ex.ToString(), Loc.T("Zapret UI — критическая ошибка запуска"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>A later launch asked the copy already running to come to the front. Marshalled onto the
    /// UI thread: the signal arrives on the single-instance watcher thread.</summary>
    private void SurfaceExistingWindow() =>
        Dispatcher.BeginInvoke(() => (MainWindow as Views.MainWindow)?.SurfaceWindow());

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Restart-to-apply for the language switch: start a fresh copy that waits for THIS one to exit, then
    /// shut down. <c>UseShellExecute=false</c> makes the child inherit our already-elevated token with no
    /// second UAC prompt; its <c>--awaitpid</c> gate holds it until our single-instance mutex is released
    /// on exit (otherwise the child would see the slot taken and just re-surface us). The language itself
    /// is persisted by the caller before this runs.
    /// </summary>
    public static void RelaunchSelf()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    Arguments = "--awaitpid " + Environment.ProcessId,
                });
        }
        catch (Exception ex) { CrashLog.Write(ex); }
        finally { Current.Shutdown(0); }
    }

    /// <summary>
    /// The relaunch gate (see <see cref="RelaunchSelf"/>): a copy started by the language switch carries
    /// <c>--awaitpid &lt;old&gt;</c> and waits for the previous copy to exit, so the single-instance mutex
    /// is free by the time it is claimed. Not a mode of its own — it just holds, then falls through to a
    /// normal startup in the newly-chosen language.
    /// </summary>
    private static void AwaitPreviousCopy(string[] args)
    {
        if (!CommandLine.TryValue(args, "--awaitpid", "", out string pid) || !int.TryParse(pid, out int id))
            return;
        try { using var prev = Process.GetProcessById(id); prev.WaitForExit(5000); }
        catch { /* already gone — nothing to wait for */ }
    }

    /// <summary>
    /// The UI language is chosen once, before any window is parsed: the XAML <c>{loc:Loc …}</c> extension
    /// resolves at parse time and switching is restart-to-apply, so a plain read is enough here. A
    /// <c>--lang ru|en</c> argument overrides the saved setting — that is how the screenshot harness
    /// renders either language without touching settings.json. Best-effort: a failed settings read just
    /// leaves the app on Russian.
    /// </summary>
    private static void InitLanguage(string[] args)
    {
        string? lang = CommandLine.Value(args, "--lang");
        try { Loc.Init(lang ?? new SettingsService().Settings.Language); } catch { Loc.Init(lang); }
    }

    /// <summary>
    /// The developer-facing launch modes, each implemented in its own file under Harness. They render or
    /// probe something and shut the process down themselves, so all this decides is whether a window
    /// should be opened at all. Returns true when one of them took the launch.
    /// </summary>
    private static bool RunHeadlessMode(string[] args)
    {
        if (CommandLine.TryValue(args, "--screenshot", ".", out string outDir))
        {
            _ = ScreenshotHarness.RunAsync(outDir);
            return true;
        }
        if (CommandLine.TryValue(args, "--tgproxytest", "tgproxytest.txt", out string upstreamFile))
        {
            _ = TelegramSelfTest.RunUpstreamAsync(upstreamFile);
            return true;
        }
        if (CommandLine.TryValue(args, "--tgbridgetest", "tgbridgetest.txt", out string bridgeFile))
        {
            _ = TelegramSelfTest.RunBridgeAsync(bridgeFile);
            return true;
        }
        if (CommandLine.TryValue(args, "--enginedump", "enginedump.txt", out string dumpFile))
        {
            EngineDump.Run(dumpFile);
            return true;
        }
        if (CommandLine.TryValue(args, "--masquetest", "443", out string exitPort))
        {
            _ = MasqueSelfTest.RunAsync(int.TryParse(exitPort, out int port) ? port : 443);
            return true;
        }
        if (CommandLine.Has(args, "--masqueregion"))
        {
            _ = MasqueSelfTest.RunRegionScanAsync();
            return true;
        }
        return false;
    }
}
