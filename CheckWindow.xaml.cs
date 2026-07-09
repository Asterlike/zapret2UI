using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Zapret2UI.ViewModels;

namespace Zapret2UI;

/// <summary>
/// Review popup shown while the auto-selector / generator runs: live per-candidate progress on the
/// left, strategies that passed on the right. It does NOT auto-close when the run finishes — it stays
/// open so the user can review results and save/apply any strategy straight from here. Closing it while
/// a run is still in progress cancels the run.
/// </summary>
public partial class CheckWindow : Window
{
    public CheckWindow() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Honor the app-wide UI zoom so the popup matches the main window on high-DPI / 4K screens.
        if (DataContext is MainViewModel vm && vm.UiScale > 1.0)
        {
            ContentRoot.LayoutTransform = new ScaleTransform(vm.UiScale, vm.UiScale);
            var wa = SystemParameters.WorkArea;
            Width = System.Math.Min(920 * vm.UiScale, wa.Width);
            Height = System.Math.Min(660 * vm.UiScale, wa.Height);
        }
        if (Owner is not null)
        {
            Left = Owner.Left + (Owner.ActualWidth - Width) / 2;
            Top = Owner.Top + (Owner.ActualHeight - Height) / 2;
        }
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing while still probing cancels the run; once finished, closing just dismisses the review.
        if (DataContext is MainViewModel { IsAutoRunning: true } vm)
            vm.StopAutoSelectCommand.Execute(null);
        base.OnClosing(e);
    }
}
