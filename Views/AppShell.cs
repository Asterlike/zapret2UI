using System.ComponentModel;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Zapret2UI.Localization;
using Zapret2UI.Services.Engine;
using Zapret2UI.Services.Platform;
using Zapret2UI.ViewModels;

namespace Zapret2UI.Views;

/// <summary>
/// What lives for the whole process: the view model, the tray icon, the corner toasts. The window comes
/// and goes.
///
/// <para><b>Why the window is not the owner any more.</b> It used to hold all of this, so it could only be
/// hidden, never closed — and a hidden window keeps its whole visual tree. Measured (2026-11-03): hidden it
/// held 169 MB and burned 3–5 % of a core doing nothing; closed and collected, the process went back to
/// idle CPU. With «Выгружать окно в трее» on, going to the tray now closes the window for real and opening
/// it from the tray builds a new one over the same view model — the selected tab, the logs and every
/// running service are in the view model and survive.</para>
///
/// <para>The shell itself is never collected (App holds it), so nothing it subscribes to can pin a window.
/// The window, for its part, unsubscribes from the view model when it closes (MainWindow.Detach) — the
/// view model outlives it and would otherwise keep every closed window alive.</para>
/// </summary>
internal sealed class AppShell
{
    private readonly MainViewModel _vm = new();
    private readonly ToastNotifier _toasts = new();
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _trayToggle;
    private Drawing.Icon? _iconIdle;
    private Drawing.Icon? _iconRunning;
    private EngineState _lastState = EngineState.Stopped;

    private MainWindow? _window;
    private Rect? _bounds;                        // where the last window was, so a new one opens there
    private WindowState _windowState = WindowState.Normal;
    private bool _started;
    private bool _cleanedUp;

    internal AppShell()
    {
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Notify += (title, msg) => ShowToast(title, msg);
        // OS logoff/shutdown: tear down even though minimize-to-tray would normally cancel a close, so
        // winws2 is never left running.
        Application.Current.SessionEnding += (_, _) => Exit();
    }

    /// <summary>True from the moment the app starts shutting down: a window closing then really closes.</summary>
    internal bool IsExiting => _cleanedUp;

    /// <summary>
    /// Start this launch: show the window — or, for the logon task (<c>--tray</c>) and the «start
    /// minimised» setting, bring up everything except the window. <c>--tray</c> means the tray whatever
    /// «Сворачивать в трей» says: that switch is about the close button, and the autostart that ignored
    /// it with the switch off opened a window at logon instead (see MainWindow.Logon.cs).
    /// </summary>
    internal void Launch(bool inTray)
    {
        if (inTray || _vm.Settings.StartMinimized) _ = StartAsync(interactive: false);
        else ShowWindow();     // its first Loaded runs the start, so the advisories sit over a drawn window
    }

    /// <summary>The window's Loaded. Only the first one of a launch that began with the window starts
    /// anything; a window rebuilt from the tray finds everything running.</summary>
    internal async void OnWindowLoaded()
    {
        if (!_started) await StartAsync(interactive: true);
    }

    /// <summary>What every launch does once: the tray icon, the startup advisories — only with the window
    /// on screen, nobody reads them from the tray — and the view model.</summary>
    private async Task StartAsync(bool interactive)
    {
        _started = true;
        SetupTray();
        _vm.RefreshAutostartTask();

        // Startup-only conflict check: another DPI-bypass tool (shares the WinDivert driver) or a VPN
        // (re-routes traffic past the engine) commonly stops the bypass from working. Advisory dialog —
        // it never blocks the launch. Skipped when starting hidden in the tray.
        if (interactive)
        {
            var conflicts = await Task.Run(ConflictScanService.ScanConflicts);
            if (conflicts.Count > 0) ConflictDialog.Show(conflicts);
        }

        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Loc.T("Ошибка инициализации"), MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // First run: open the walkthrough once, after init so it sits over a populated window.
        if (interactive && _vm.NeedsWelcome)
            _vm.OpenWelcome(withCountdown: true);
    }

    // ---- the window --------------------------------------------------------

    /// <summary>Bring the window up — building it if the tray unloaded it.</summary>
    internal void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(_vm, this, _bounds, _windowState);
            Application.Current.MainWindow = _window;
        }
        _window.Show();
        _window.ShowInTaskbar = true;
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>
    /// Bring the window back to the user — used when a SECOND launch is folded into this instance (see
    /// <see cref="App"/>'s single-instance claim). Unlike the tray path, the request comes from another
    /// process, so Windows' foreground lock applies to us and <c>Activate()</c> alone can leave the window
    /// buried; a brief Topmost flip reliably raises it.
    /// </summary>
    internal void SurfaceWindow()
    {
        ShowWindow();
        if (_window is null) return;
        _window.Topmost = true;
        _window.Topmost = false;
    }

    /// <summary>The close button with «Сворачивать в трей» on: unload the window, or just hide it.</summary>
    internal void HideToTray()
    {
        if (_window is not { } w) return;
        if (!_vm.UnloadWindowInTray)
        {
            w.Hide();
            w.ShowInTaskbar = false;
            return;
        }

        _window = null;
        _bounds = w.RestoreBounds;
        _windowState = w.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        w.Unload();
        if (Application.Current.MainWindow == w) Application.Current.MainWindow = null;

        // Hand the memory back now rather than at some later collection: the point of unloading is a
        // smaller process in Task Manager, and a closed window's tree is mostly large, long-lived objects
        // the GC would otherwise leave for a while. Once per trip to the tray.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        });
    }

    // ---- tray --------------------------------------------------------------

    private void SetupTray()
    {
        MainWindow.AllowTaskbarCreated();
        _iconIdle = LoadIcon("app.ico");
        _iconRunning = LoadIcon("app-on.ico");

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add(Loc.T("Открыть"), null, (_, _) => ShowWindow());
        _trayToggle = new Forms.ToolStripMenuItem(Loc.T("Запустить обход"), null, (_, _) => _vm.ToggleCommand.Execute(null));
        _trayMenu.Items.Add(_trayToggle);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(Loc.T("Выход"), null, (_, _) => Exit());

        _tray = new Forms.NotifyIcon
        {
            Icon = _iconIdle ?? Drawing.SystemIcons.Application,
            Visible = true,
            Text = Loc.T("Zapret2UI — остановлен"),
            ContextMenuStrip = _trayMenu,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.State) or nameof(MainViewModel.SelectedPreset))
            UpdateTrayForState(_vm.State);
    }

    private void UpdateTrayForState(EngineState state)
    {
        if (_tray is null) return;

        bool running = state == EngineState.Running;
        _tray.Icon = running ? (_iconRunning ?? _iconIdle) : _iconIdle;
        string status = state switch
        {
            EngineState.Running => Loc.T("работает"),
            EngineState.Starting => Loc.T("запуск…"),
            EngineState.Stopping => Loc.T("остановка…"),
            _ => Loc.T("остановлен"),
        };
        string? preset = _vm.SelectedPreset?.Name;
        string text = $"Zapret2UI — {status}";
        if (running && !string.IsNullOrEmpty(preset)) text += $"\n{preset}";
        // NotifyIcon tooltip is capped at 127 chars.
        _tray.Text = text.Length > 127 ? text[..127] : text;
        if (_trayToggle is not null)
            _trayToggle.Text = running ? Loc.T("Остановить обход") : Loc.T("Запустить обход");

        // Our own corner toast on settled transitions (replaces the Windows balloon tips).
        if (state == EngineState.Running && _lastState != EngineState.Running)
            ShowToast("Zapret2UI", Loc.T("Обход включён"));
        else if (state == EngineState.Stopped && _lastState is EngineState.Running or EngineState.Stopping)
            ShowToast("Zapret2UI", Loc.T("Обход выключен"));
        _lastState = state;
    }

    /// <summary>Show one of our corner toasts, honouring the notifications + sound settings. Safe to
    /// call from any thread (marshals to the UI thread). Replaces the old Windows tray balloon tips.</summary>
    private void ShowToast(string title, string message)
    {
        if (!_vm.Settings.NotificationsEnabled) return;
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(() => ShowToast(title, message)); return; }
        // A notification is non-critical: Notify runs inline inside _engine.Start() (via SetState),
        // so a throw here would abort the launch. Never let a toast break the caller.
        try { _toasts.Show(title, message, _vm.Settings.NotificationSound); }
        catch { /* toast is best-effort */ }
    }

    private static Drawing.Icon? LoadIcon(string name)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/{name}");
            var info = Application.GetResourceStream(uri);
            if (info is null) return null;
            using var s = info.Stream;
            return new Drawing.Icon(s);
        }
        catch { return null; }
    }

    // ---- exit --------------------------------------------------------------

    /// <summary>Quit for real: stop what runs, take the tray icon down, end the process.</summary>
    internal void Exit()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        try { _vm.Shutdown(); } catch { }
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _trayMenu?.Dispose(); _trayMenu = null;
        _iconIdle?.Dispose();
        _iconRunning?.Dispose();
        Application.Current.Shutdown();
    }
}
