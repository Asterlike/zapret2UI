using System.Windows;
using System.Windows.Input;
using Zapret2UI.Localization;

namespace Zapret2UI.Views;

/// <summary>
/// App-styled replacement for the unthemed Windows confirm MessageBox. Used for
/// destructive actions (delete preset / hostlist).
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirmText, bool danger)
    {
        InitializeComponent();
        // Localize here so every caller is covered, including the "Удалить" default parameter (a
        // default can't itself be Loc.T). Callers that already pass localized text are unaffected:
        // Loc.T of an English string returns it unchanged.
        TitleText.Text = Loc.T(title);
        MessageText.Text = Loc.T(message);
        ConfirmButton.Content = Loc.T(confirmText);
        // Red confirm reads as "destructive"; a benign action (e.g. the language restart) uses the
        // ordinary accent button instead.
        if (!danger) ConfirmButton.Style = (Style)FindResource("PrimaryButton");
    }

    /// <summary>Shows a modal confirmation; returns true if the user confirmed. <paramref name="danger"/>
    /// paints the confirm button red (destructive); pass false for a benign confirm.</summary>
    public static bool Show(string title, string message, string confirmText = "Удалить", bool danger = true)
    {
        var owner = Application.Current?.MainWindow;
        var dlg = new ConfirmDialog(title, message, confirmText, danger);
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
