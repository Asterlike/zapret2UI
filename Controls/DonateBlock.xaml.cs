using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace Zapret2UI.Controls;

/// <summary>
/// "Support the author" block: a scannable donate QR plus a clickable text link, both opening the same
/// tribute.tg page in the browser. Collapses to a compact button (QR hidden) and back — the expanded/
/// collapsed state and the toggle live on the view-model (DonateExpanded / ToggleDonateCommand), so it
/// persists and can't clobber itself. Reused in both Simple and Advanced layouts.
/// </summary>
public partial class DonateBlock : UserControl
{
    private const string DonateUrl = "https://web.tribute.tg/d/HFh";

    public DonateBlock() => InitializeComponent();

    private void OnOpen(object sender, MouseButtonEventArgs e) => Open(DonateUrl);

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* opening a browser is best-effort */ }
    }
}
