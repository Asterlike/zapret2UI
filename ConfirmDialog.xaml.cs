using System.Windows;
using System.Windows.Input;

namespace Zapret2UI;

/// <summary>
/// App-styled replacement for the unthemed Windows confirm MessageBox. Used for
/// destructive actions (delete preset / hostlist).
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    /// <summary>Shows a modal confirmation; returns true if the user confirmed.</summary>
    public static bool Show(string title, string message, string confirmText = "Удалить")
    {
        var owner = Application.Current?.MainWindow;
        var dlg = new ConfirmDialog(title, message, confirmText);
        if (owner is not null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
