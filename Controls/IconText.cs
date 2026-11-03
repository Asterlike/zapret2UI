using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Zapret2UI.Controls;

/// <summary>
/// A line icon followed by its label — what the buttons used to say with a colour emoji.
///
/// <para><b>Why not emoji.</b> WPF cannot draw colour fonts, so the emoji came from Emoji.Wpf, which parses
/// Segoe UI Emoji (13 MB) into managed objects the first time any emoji is laid out and never lets go.
/// Measured in a bare WPF process: one emoji TextBlock took the managed heap from 1 to 47 MB and the
/// private bytes up by 68 MB, and removing it freed nothing. That was most of what the window cost to
/// keep in memory, so the six emoji became line icons.</para>
///
/// <para>The icons are Lucide outlines (ISC) on Lucide's own 24×24 grid with its stroke of 2: drawn
/// inside a Viewbox, the stroke scales with the icon exactly as in the source, and the outline takes the
/// text colour, so a button's hover or disabled state recolours both.</para>
/// </summary>
public sealed class IconText : Control
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(IconText), new PropertyMetadata(null));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(IconText), new PropertyMetadata(""));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(IconText), new PropertyMetadata(14.0));

    /// <summary>The outline's colour. The default style binds it to <see cref="Control.Foreground"/>; set it
    /// for an icon in its own colour (the green heart beside the purple «Поддержать автора»).</summary>
    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
        nameof(IconBrush), typeof(Brush), typeof(IconText), new PropertyMetadata(null));

    static IconText()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(IconText), new FrameworkPropertyMetadata(typeof(IconText)));
        FocusableProperty.OverrideMetadata(typeof(IconText), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(IconText), new FrameworkPropertyMetadata(false));
    }

    /// <summary>A Lucide outline on the 24×24 grid (the <c>Icon.*</c> geometries in Theme.xaml).</summary>
    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public Brush? IconBrush
    {
        get => (Brush?)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }
}
