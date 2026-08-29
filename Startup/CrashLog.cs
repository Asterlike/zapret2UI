using System.IO;
using System.Windows;
using System.Windows.Threading;
using Zapret2UI.Localization;
using Zapret2UI.Services.Infrastructure;

namespace Zapret2UI.Startup;

/// <summary>
/// Last-resort crash journal (<c>logs\fatal.log</c>). Every write is best-effort: a logger that can
/// throw would turn a recoverable UI exception into a silent process death.
/// </summary>
internal static class CrashLog
{
    /// <summary>Catch what escapes the dispatcher (shown to the user, then swallowed so the app keeps
    /// running) and what escapes any other thread (nothing can be done, but it gets recorded).</summary>
    internal static void Install(Application app)
    {
        app.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Write(args.ExceptionObject as Exception);
    }

    internal static void Write(Exception? ex)
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

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Write(e.Exception);
        MessageBox.Show(e.Exception.Message, Loc.T("Zapret UI — ошибка"),
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
