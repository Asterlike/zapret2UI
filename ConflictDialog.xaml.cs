using System.Windows;
using System.Windows.Input;

namespace Zapret2UI;

/// <summary>
/// App-styled, single-button advisory shown once at startup when a conflicting VPN or another
/// DPI-bypass tool is detected (see <see cref="Services.ConflictScanService"/>). Purely informational —
/// it never blocks the launch.
/// </summary>
public partial class ConflictDialog : Window
{
    private ConflictDialog(IReadOnlyList<string> items)
    {
        InitializeComponent();
        List.ItemsSource = items;
    }

    /// <summary>Shows the advisory modally (no-op semantics — nothing is returned).</summary>
    public static void Show(IReadOnlyList<string> items)
    {
        var owner = Application.Current?.MainWindow;
        var dlg = new ConflictDialog(items);
        if (owner is not null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
    }

    private void OnOk(object sender, RoutedEventArgs e) => Close();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
