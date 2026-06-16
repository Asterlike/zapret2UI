using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ZapretUI.ViewModels;

namespace ZapretUI;

/// <summary>
/// Popup shown while the auto-selector runs: live per-candidate progress against
/// the chosen targets. Closes itself when the VM signals the run finished; if the
/// user closes it early, the run is cancelled.
/// </summary>
public partial class CheckWindow : Window
{
    private bool _closingFromVm;

    public CheckWindow() => InitializeComponent();

    /// <summary>Close programmatically (run finished) without cancelling.</summary>
    public void CloseFromVm()
    {
        _closingFromVm = true;
        Close();
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // User-initiated close while still probing: cancel the run.
        if (!_closingFromVm && DataContext is MainViewModel { IsAutoSelecting: true } vm)
            vm.StopAutoSelectCommand.Execute(null);
        base.OnClosing(e);
    }
}
