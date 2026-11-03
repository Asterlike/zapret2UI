using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Zapret2UI.Services.Platform;
using Zapret2UI.ViewModels;

namespace Zapret2UI.Views;

/// <summary>
/// The window and nothing but the window: the view model, the tray icon and the toasts belong to
/// <see cref="AppShell"/>, which builds a window when one is wanted and lets it go when it goes to the tray.
/// A null shell is the screenshot harness — the window then simply closes.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppShell? _shell;
    private readonly Rect? _restoreBounds;
    private readonly WindowState _restoreState;
    private CheckWindow? _checkWindow;
    private bool _unloading;

    internal MainWindow(MainViewModel vm, AppShell? shell, Rect? restoreBounds = null,
                        WindowState restoreState = WindowState.Normal)
    {
        _vm = vm;
        _shell = shell;
        _restoreBounds = restoreBounds;
        _restoreState = restoreState;
        InitializeComponent();
        DataContext = _vm;

        _vm.LogLines.CollectionChanged += OnLogChanged;
        _vm.ProxyLogLines.CollectionChanged += OnProxyLogChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.AutoCheckStarted += OnAutoCheckStarted;
        _vm.AutoCheckFinished += OnAutoCheckFinished;
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged; // repaint a window first drawn behind the logon screen
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
        SourceInitialized += OnSourceInitialized; // constrain borderless maximize to work area
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateMaxButtonGlyph();
        _shell?.OnWindowLoaded();
    }

    /// <summary>
    /// Close for good, called by the shell when the window goes to the tray. The view model outlives the
    /// window, so every subscription to it is taken back first — one left behind keeps the whole closed
    /// window alive (verified with a weak reference: with the handlers cut it is collected, without them
    /// it is not).
    /// </summary>
    internal void Unload()
    {
        _vm.LogLines.CollectionChanged -= OnLogChanged;
        _vm.ProxyLogLines.CollectionChanged -= OnProxyLogChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.AutoCheckStarted -= OnAutoCheckStarted;
        _vm.AutoCheckFinished -= OnAutoCheckFinished;
        _unloading = true;
        Close();
        DataContext = null;
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
        if (e.PropertyName == nameof(MainViewModel.UiScale))
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

    // ---- close -------------------------------------------------------------

    /// <summary>
    /// The close button. With «Сворачивать в трей» on the shell takes the window to the tray (unloading
    /// or hiding it); off, the app quits. A close the shell itself started — unloading, or the app
    /// shutting down — goes through, and so does every close without a shell (the screenshot harness).
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shell is null || _unloading || _shell.IsExiting) return;

        if (_vm.MinimizeToTray)
        {
            e.Cancel = true;
            // Deferred: unloading closes this window, and WPF refuses Close() from inside Closing.
            var shell = _shell;
            Dispatcher.BeginInvoke(shell.HideToTray);
        }
        else
        {
            _shell.Exit();   // as before the shell existed: let this close go through and shut down
        }
    }
}
