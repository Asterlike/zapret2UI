using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using ZapretUI.Services;
using ZapretUI.ViewModels;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ZapretUI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _trayToggle;
    private Drawing.Icon? _iconIdle;
    private Drawing.Icon? _iconRunning;
    private bool _reallyClose;
    private EngineState _lastState = EngineState.Stopped;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.LogLines.CollectionChanged += OnLogChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Notify += (title, msg) => _tray?.ShowBalloonTip(3500, title, msg, Forms.ToolTipIcon.Info);
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

        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ошибка инициализации", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _vm.LogLines.Count > 0)
            LogList.ScrollIntoView(_vm.LogLines[^1]);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.State) or nameof(MainViewModel.SelectedPreset))
            UpdateTrayForState(_vm.State);
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
            Text = "Zapret UI — остановлен",
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
        string text = $"Zapret UI — {status}";
        if (running && !string.IsNullOrEmpty(preset)) text += $"\n{preset}";
        // NotifyIcon tooltip is capped at 127 chars.
        _tray.Text = text.Length > 127 ? text[..127] : text;
        if (_trayToggle is not null)
            _trayToggle.Text = running ? "Остановить обход" : "Запустить обход";

        // Balloon only on settled transitions.
        if (state == EngineState.Running && _lastState != EngineState.Running)
            _tray.ShowBalloonTip(2500, "Zapret UI", "Обход DPI запущен", Forms.ToolTipIcon.Info);
        else if (state == EngineState.Stopped && _lastState is EngineState.Running or EngineState.Stopping)
            _tray.ShowBalloonTip(2500, "Zapret UI", "Обход DPI остановлен", Forms.ToolTipIcon.Info);
        _lastState = state;
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
