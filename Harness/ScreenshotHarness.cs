using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows;
using Zapret2UI.Services.Platform;
using Zapret2UI.Startup;
using Zapret2UI.ViewModels;
using Zapret2UI.Views;

namespace Zapret2UI.Harness;

/// <summary>
/// <c>Zapret2UI.exe --screenshot &lt;outDir&gt;</c> — render the app's screens to PNGs and exit, so the
/// documentation shots can be regenerated without a manual desktop capture.
///
/// <para>It drives the REAL window through the real view model, with the same renderer and the same
/// bundled fonts as production, so a shot cannot flatter the app. Needs no administrator, unlike the
/// engine itself — flip <c>app.manifest</c> to <c>asInvoker</c> for the run and put it back after.</para>
/// </summary>
internal static class ScreenshotHarness
{
    internal static async Task RunAsync(string outDir)
    {
        var app = Application.Current;
        try
        {
            Directory.CreateDirectory(outDir);
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 60, Top = 40, Width = 1280, Height = 800,
            };
            app.MainWindow = window;
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
                (3, "diagnostics.png"), (5, "telegram.png"), (6, "warp.png"), (7, "settings.png"),
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
        catch (Exception ex) { CrashLog.Write(ex); }
        finally { app.Shutdown(0); }
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
}
