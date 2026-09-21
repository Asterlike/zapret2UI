using System.Windows;
using System.Windows.Controls;

namespace Zapret2UI.Controls;

/// <summary>
/// A side column that moves underneath the content when there is no room for it beside it.
///
/// <para><b>Why this exists.</b> The WARP and HMS tabs used to sit in a 780-pixel column pinned to the
/// left, which on the default 1280-pixel window left the right third of the screen empty while the
/// explanation fought the controls for the same narrow column and the page scrolled for no reason. The
/// explanation belongs in that empty third — but the window can be made as narrow as 880 pixels, and
/// three columns there are three columns of a couple of hundred pixels each, with every button wrapped
/// onto its own line. WPF has no media query, so this is one: below the threshold the side column
/// folds under the content, above it it stands beside it.</para>
///
/// <para><b>What it expects.</b> A Grid with three columns — content, gap, side — and two rows, the
/// second empty until it is needed. The element marked <see cref="IsSideProperty"/> is the one that
/// moves; everything else is left where the XAML put it.</para>
/// </summary>
public static class SidePanel
{
    /// <summary>Width, in pixels of the Grid itself, below which the side column folds underneath.</summary>
    public static readonly DependencyProperty CollapseBelowProperty = DependencyProperty.RegisterAttached(
        "CollapseBelow", typeof(double), typeof(SidePanel), new PropertyMetadata(0.0, OnCollapseBelowChanged));

    public static double GetCollapseBelow(DependencyObject o) => (double)o.GetValue(CollapseBelowProperty);

    public static void SetCollapseBelow(DependencyObject o, double value) => o.SetValue(CollapseBelowProperty, value);

    /// <summary>How wide the side column is while it stands beside the content.</summary>
    public static readonly DependencyProperty SideWidthProperty = DependencyProperty.RegisterAttached(
        "SideWidth", typeof(double), typeof(SidePanel), new PropertyMetadata(340.0));

    public static double GetSideWidth(DependencyObject o) => (double)o.GetValue(SideWidthProperty);

    public static void SetSideWidth(DependencyObject o, double value) => o.SetValue(SideWidthProperty, value);

    /// <summary>Marks the child that moves.</summary>
    public static readonly DependencyProperty IsSideProperty = DependencyProperty.RegisterAttached(
        "IsSide", typeof(bool), typeof(SidePanel), new PropertyMetadata(false));

    public static bool GetIsSide(DependencyObject o) => (bool)o.GetValue(IsSideProperty);

    public static void SetIsSide(DependencyObject o, bool value) => o.SetValue(IsSideProperty, value);

    private static void OnCollapseBelowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid) return;
        grid.SizeChanged -= OnSizeChanged;
        grid.SizeChanged += OnSizeChanged;
        Arrange(grid);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Folding the column changes the Grid's HEIGHT, which raises this again; only a change of width
        // can change the answer, so that is the only one acted on.
        if (e.WidthChanged && sender is Grid grid) Arrange(grid);
    }

    private static void Arrange(Grid grid)
    {
        if (grid.ColumnDefinitions.Count < 3 || grid.RowDefinitions.Count < 2 || grid.ActualWidth <= 0) return;

        bool narrow = grid.ActualWidth < GetCollapseBelow(grid);

        grid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 18);
        grid.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : GetSideWidth(grid));

        foreach (UIElement child in grid.Children)
        {
            if (!GetIsSide(child) || child is not FrameworkElement side) continue;

            Grid.SetRow(side, narrow ? 1 : 0);
            Grid.SetColumn(side, narrow ? 0 : 2);
            Grid.SetColumnSpan(side, narrow ? 3 : 1);
            side.Margin = new Thickness(0, narrow ? 16 : 0, 0, 0);
        }
    }
}
