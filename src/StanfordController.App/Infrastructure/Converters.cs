using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App.Infrastructure;

/// <summary>true → Visible. Pass ConverterParameter="Invert" to reverse.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is "Invert")
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible ^ parameter is "Invert";
}

/// <summary>non-null (and non-empty string) → Visible. ConverterParameter="Invert" reverses.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value is not null && (value is not string s || !string.IsNullOrWhiteSpace(s));
        if (parameter is "Invert")
        {
            present = !present;
        }

        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>non-null → true.</summary>
public sealed class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class TerminalLineBrushConverter : IValueConverter
{
    private static readonly Brush Command = Frozen("#93C5FD");
    private static readonly Brush Response = Frozen("#E2E8F0");
    private static readonly Brush Error = Frozen("#FCA5A5");
    private static readonly Brush Info = Frozen("#64748B");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TerminalLineKind.Command => Command,
        TerminalLineKind.Response => Response,
        TerminalLineKind.Error => Error,
        _ => Info,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

