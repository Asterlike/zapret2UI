using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ZapretUI.Models;
using ZapretUI.Services;

namespace ZapretUI.Mvvm;

/// <summary>
/// Two bound values -> Visibility: Visible when they are the SAME instance.
/// Used to flag the currently-running preset inside the preset list.
/// </summary>
public sealed class RefEqualsToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object? p, CultureInfo c) =>
        values.Length == 2 && ReferenceEquals(values[0], values[1]) && values[0] is not null
            ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object? v, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

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

/// <summary>DiagStatus -> short cell label.</summary>
public sealed class DiagStatusToTextConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        DiagStatus.Ok => "OK",
        DiagStatus.Fail => "ОШИБКА",
        DiagStatus.Timeout => "таймаут",
        DiagStatus.NotSupported => "н/д",
        DiagStatus.Running => "…",
        DiagStatus.Skip => "—",
        _ => "·",
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>DiagStatus -> cell foreground colour.</summary>
public sealed class DiagStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ok = new(Color.FromRgb(0x34, 0xD3, 0x99));
    private static readonly SolidColorBrush Bad = new(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush Warn = new(Color.FromRgb(0xF5, 0xA6, 0x23));
    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x6B, 0x72, 0x80));

    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        DiagStatus.Ok => Ok,
        DiagStatus.Fail => Bad,
        DiagStatus.Timeout => Warn,
        _ => Muted,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
