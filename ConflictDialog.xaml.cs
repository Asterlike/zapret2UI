using System.Windows;
using System.Windows.Input;
using Zapret2UI.Services;

namespace Zapret2UI;

/// <summary>
/// App-styled advisory listing what in the environment can stop the bypass from working
/// (see <see cref="ConflictScanService"/>). Every row carries the fix, not just the diagnosis.
/// Shown automatically at startup when conflicts are found, and on demand from
/// Настройки → «Проверить окружение» — where it also renders the all-clear state.
/// Purely informational — it never blocks the launch.
/// </summary>
public partial class ConflictDialog : Window
{
    private ConflictDialog(IReadOnlyList<EnvFinding> items)
    {
        InitializeComponent();

        if (items.Count == 0)
        {
            HeadText.Text = "Всё в порядке";
            SubText.Text = "Проверка не нашла в системе ничего, что мешало бы обходу.";
            items = new[]
            {
                new EnvFinding(EnvSeverity.Ok, "Помех не найдено",
                    "Конфликтующих программ и VPN-туннелей не видно, движок на месте. Если обход всё " +
                    "равно не работает — причина в стратегии, а не в окружении: попробуйте «Подобрать " +
                    "стратегию» на главной странице.",
                    ""),
            };
        }
        else
        {
            HeadText.Text = "Проверка окружения";
            SubText.Text = "Вот что может помешать обходу. У каждого пункта — что это значит и что с этим делать.";
        }

        List.ItemsSource = items;
    }

    /// <summary>
    /// Harness hook: build the dialog WITHOUT showing it modally, so the screenshot harness can render
    /// it (App.xaml.cs <c>--screenshot</c>). A styling bug in here is invisible in the tab screenshots,
    /// which is exactly how the unreadable "Что делать" line shipped once — hence this.
    /// </summary>
    internal static Window CreateForHarness(IReadOnlyList<EnvFinding> items) => new ConflictDialog(items);

    /// <summary>Shows the advisory modally (no-op semantics — nothing is returned).</summary>
    public static void Show(IReadOnlyList<EnvFinding> items)
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
