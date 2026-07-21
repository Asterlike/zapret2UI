using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Zapret2UI.Services;
using Zapret2UI.ViewModels;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Zapret2UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private CheckWindow? _checkWindow;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _trayToggle;
    private Drawing.Icon? _iconIdle;
    private Drawing.Icon? _iconRunning;
    private bool _reallyClose;
    private EngineState _lastState = EngineState.Stopped;
    private readonly ToastNotifier _toasts = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.LogLines.CollectionChanged += OnLogChanged;
        _vm.ProxyLogLines.CollectionChanged += OnProxyLogChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Notify += (title, msg) => ShowToast(title, msg);
        _vm.AutoCheckStarted += OnAutoCheckStarted;
        _vm.AutoCheckFinished += OnAutoCheckFinished;
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
        SourceInitialized += OnSourceInitialized; // constrain borderless maximize to work area

        // OS logoff/shutdown: tear down even though minimize-to-tray would normally
        // cancel a close, so winws2 is never left running.
        Application.Current.SessionEnding += (_, _) => { _reallyClose = true; CleanupAndShutdown(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetupTray();
        UpdateMaxButtonGlyph();

        bool startInTray =
            Environment.GetCommandLineArgs().Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase))
            || _vm.Settings.StartMinimized;
        if (startInTray && _vm.MinimizeToTray)
            HideToTray();

        // Startup-only conflict check: another DPI-bypass tool (shares the WinDivert driver) or a VPN
        // (re-routes traffic past the engine) commonly stops the bypass from working. Advisory dialog —
        // it never blocks the launch. Skipped when starting hidden in the tray or under the screenshot harness.
        bool screenshotHarness = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
        if (!screenshotHarness && !(startInTray && _vm.MinimizeToTray))
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
            MessageBox.Show(ex.Message, "Ошибка инициализации", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // First run: open the walkthrough once, after init so it sits over a populated window. Skipped
        // under the screenshot harness (it would cover every tab shot) and when starting into the tray.
        if (!screenshotHarness && !(startInTray && _vm.MinimizeToTray) && _vm.NeedsWelcome)
            _vm.OpenWelcome(withCountdown: true);
    }

    /// <summary>
    /// Настройки → «Проверить окружение»: the full check (conflicts + engine state), on demand.
    /// Unlike the startup advisory this ALWAYS shows the dialog — including the all-clear state — so
    /// pressing the button always produces a visible answer.
    /// </summary>
    private async void OnCheckEnvironment(object sender, RoutedEventArgs e)
    {
        bool installed = _vm.IsEngineInstalled;
        bool complete = _vm.IsEngineComplete;
        var findings = await Task.Run(() => ConflictScanService.ScanEnvironment(installed, complete));
        ConflictDialog.Show(findings);
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _vm.LogLines.Count > 0)
            LogList.ScrollIntoView(_vm.LogLines[^1]);
    }

    private void OnProxyLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _vm.ProxyLogLines.Count > 0)
            ProxyLogList.ScrollIntoView(_vm.ProxyLogLines[^1]);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.State) or nameof(MainViewModel.SelectedPreset))
            UpdateTrayForState(_vm.State);
        else if (e.PropertyName == nameof(MainViewModel.UiScale))
            ApplyUiScale(_vm.UiScale);
    }

    // ---- app-wide UI scale (DPI-independent zoom) --------------------------

    /// <summary>Scales the whole UI via a LayoutTransform on the content root, on top of the OS DPI
    /// scaling. The borderless drag region and the window's min/target size are grown with it so the
    /// enlarged content isn't clipped and dragging still lines up with the visible caption bar.</summary>
    private void ApplyUiScale(double scale)
    {
        scale = Math.Clamp(scale, 1.0, 2.5);
        ContentRoot.LayoutTransform = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale);

        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome is not null) chrome.CaptionHeight = 46 * scale;

        MinWidth = 880 * scale;
        MinHeight = 580 * scale;

        if (WindowState == WindowState.Normal)
        {
            var wa = SystemParameters.WorkArea;
            Width = Math.Min(1280 * scale, wa.Width);
            Height = Math.Min(720 * scale, wa.Height);
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }
    }

    // Fade + slight slide-up of the tab content when the user switches tabs.
    private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged also bubbles from inner ListBoxes — only react to the TabControl itself.
        if (e.OriginalSource is not TabControl) return;
        if (MainTabs.Template?.FindName("PART_SelectedContentHost", MainTabs) is not ContentPresenter host)
            return;

        var dur = TimeSpan.FromMilliseconds(220);
        host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
        // Use a fresh (unfrozen) transform — the one baked into the template is sealed/frozen.
        var tt = new TranslateTransform();
        host.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ---- window caption ----------------------------------------------------

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // WM_GETMINMAXINFO now clamps the maximized window to the monitor work area,
        // so there is no overflow to compensate — just drop the border when maximized.
        RootBorder.Margin = new Thickness(0);
        RootBorder.BorderThickness = WindowState == WindowState.Maximized
            ? new Thickness(0) : new Thickness(1);
        UpdateMaxButtonGlyph();
    }

    private void UpdateMaxButtonGlyph()
    {
        string glyph = WindowState == WindowState.Maximized ? "" : "";
        if (MaxButton.Content is System.Windows.Controls.TextBlock tb) tb.Text = glyph;
    }

    // ---- auto-select popup -------------------------------------------------

    private void OnAutoCheckStarted()
    {
        // A review popup left open from a previous run is reused — just bring it forward.
        if (_checkWindow is not null) { _checkWindow.Activate(); return; }
        _checkWindow = new CheckWindow { Owner = this, DataContext = _vm };
        _checkWindow.Closed += (_, _) => _checkWindow = null;
        _checkWindow.Show();
    }

    // Don't close the popup when the run finishes — it stays open for review so the user can save/apply
    // strategies from it. Just bring it forward so they notice it's done.
    private void OnAutoCheckFinished() => _checkWindow?.Activate();

    // ---- tray --------------------------------------------------------------

    private void SetupTray()
    {
        _iconIdle = LoadIcon("app.ico");
        _iconRunning = LoadIcon("app-on.ico");

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Открыть", null, (_, _) => ShowFromTray());
        _trayToggle = new Forms.ToolStripMenuItem("Запустить обход", null, (_, _) => _vm.ToggleCommand.Execute(null));
        _trayMenu.Items.Add(_trayToggle);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("Выход", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Icon = _iconIdle ?? Drawing.SystemIcons.Application,
            Visible = true,
            Text = "Zapret2UI — остановлен",
            ContextMenuStrip = _trayMenu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void UpdateTrayForState(EngineState state)
    {
        if (_tray is null) return;

        bool running = state == EngineState.Running;
        _tray.Icon = running ? (_iconRunning ?? _iconIdle) : _iconIdle;
        string status = state switch
        {
            EngineState.Running => "работает",
            EngineState.Starting => "запуск…",
            EngineState.Stopping => "остановка…",
            _ => "остановлен",
        };
        string? preset = _vm.SelectedPreset?.Name;
        string text = $"Zapret2UI — {status}";
        if (running && !string.IsNullOrEmpty(preset)) text += $"\n{preset}";
        // NotifyIcon tooltip is capped at 127 chars.
        _tray.Text = text.Length > 127 ? text[..127] : text;
        if (_trayToggle is not null)
            _trayToggle.Text = running ? "Остановить обход" : "Запустить обход";

        // Our own corner toast on settled transitions (replaces the Windows balloon tips).
        if (state == EngineState.Running && _lastState != EngineState.Running)
            ShowToast("Zapret2UI", "Обход включён");
        else if (state == EngineState.Stopped && _lastState is EngineState.Running or EngineState.Stopping)
            ShowToast("Zapret2UI", "Обход выключен");
        _lastState = state;
    }

    /// <summary>Show one of our corner toasts, honouring the notifications + sound settings. Safe to
    /// call from any thread (marshals to the UI thread). Replaces the old Windows tray balloon tips.</summary>
    private void ShowToast(string title, string message)
    {
        if (!_vm.Settings.NotificationsEnabled) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowToast(title, message)); return; }
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

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Bring this window back to the user — used when a SECOND launch is folded into this instance
    /// (see <see cref="App"/>'s single-instance claim). Unlike the tray path, the request comes from
    /// another process, so Windows' foreground lock applies to us and <c>Activate()</c> alone can
    /// leave the window buried; a brief Topmost flip reliably raises it.
    /// </summary>
    internal void SurfaceWindow()
    {
        ShowFromTray();
        Topmost = true;
        Topmost = false;
    }

    // ---- close / exit ------------------------------------------------------

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_reallyClose) { CleanupAndShutdown(); return; }

        if (_vm.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
        }
        else
        {
            CleanupAndShutdown();
        }
    }

    private void ExitApp()
    {
        _reallyClose = true;
        Close();
    }

    private bool _cleanedUp;
    private void CleanupAndShutdown()
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
