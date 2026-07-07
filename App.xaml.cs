using System.IO;
using System.Text;
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

        // Telegram-proxy self-test harness: `ZapretUI.exe --tgproxytest <outFile>` probes the upstream
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

        // Bridge self-test: `ZapretUI.exe --tgbridgetest <outFile>` drives the REAL bridge from a loopback
        // client and checks that Telegram's resPQ survives the round-trip decodable (re-encryption/splitter
        // correctness) — isolates a bridge bug from a censored-network drop. No admin needed.
        int bi = Array.FindIndex(e.Args, a => a.Equals("--tgbridgetest", StringComparison.OrdinalIgnoreCase));
        if (bi >= 0)
        {
            string outFile = bi + 1 < e.Args.Length ? e.Args[bi + 1] : "tgbridgetest.txt";
            _ = RunTgBridgeTestAsync(outFile);
            return;
        }

        // Engine command-line dump: `ZapretUI.exe --enginedump <outFile>` seeds the bundled lists and
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
