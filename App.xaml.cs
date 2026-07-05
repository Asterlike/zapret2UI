using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZapretUI.Services;
using ZapretUI.ViewModels;

namespace ZapretUI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteFatal(args.ExceptionObject as Exception);

        // Screenshot harness: `ZapretUI.exe --screenshot <outDir>` renders each tab to PNG and exits
        // (used to regenerate docs/*.png without a manual desktop capture). Needs no admin, so flip the
        // manifest to asInvoker while capturing, then restore it.
        int si = Array.FindIndex(e.Args, a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (si >= 0)
        {
            string outDir = si + 1 < e.Args.Length ? e.Args[si + 1] : ".";
            _ = RunScreenshotsAsync(outDir);
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

            window.Close();
        }
        catch (Exception ex) { WriteFatal(ex); }
        finally { Shutdown(0); }
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
