using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ZapretUI.Services;

namespace ZapretUI.Mvvm;

/// <summary>Inverts a boolean.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => !(v is true);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => !(v is true);
}

/// <summary>bool -> Visibility, with optional "invert" parameter.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        bool b = v is true;
        if (p as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        v is Visibility.Visible;
}

/// <summary>EngineState -> status dot colour.</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        EngineState.Running => new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)),
        EngineState.Starting or EngineState.Stopping => new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)),
        _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>EngineState -> Russian label.</summary>
public sealed class StateToTextConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        EngineState.Running => "Работает",
        EngineState.Starting => "Запуск…",
        EngineState.Stopping => "Остановка…",
        _ => "Остановлен",
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
