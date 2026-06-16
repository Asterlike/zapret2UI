using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace ZapretUI.Controls;

/// <summary>
/// Small "support the author" block: a scannable donate QR plus a clickable
/// text link, both opening the same tribute.tg page in the default browser.
/// Reused in both Simple and Advanced layouts.
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
