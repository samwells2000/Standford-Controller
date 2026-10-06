using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using StanfordController.Core;
using StanfordController.Core.Protocol;
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

/// <summary>Maps an output (or a channel/output name such as "AB" or "C") to its theme colour.</summary>
public sealed class OutputBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Dg645Output output => output.ToString(),
            Dg645Channel channel => channel switch
            {
                Dg645Channel.A or Dg645Channel.B => "AB",
                Dg645Channel.C or Dg645Channel.D => "CD",
                Dg645Channel.E or Dg645Channel.F => "EF",
                Dg645Channel.G or Dg645Channel.H => "GH",
                _ => "T0",
            },
            string s => s,
            _ => "T0",
        };

        return Application.Current.TryFindResource($"Output{key}Brush") as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ConnectionStateBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ConnectionState.Connected => "SuccessBrush",
            ConnectionState.Connecting => "WarningBrush",
            ConnectionState.Failed => "DangerBrush",
            _ => "SidebarMutedBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
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

public sealed class LogDirectionBrushConverter : IValueConverter
{
    private static readonly Brush Sent = TerminalLineBrushConverter.Frozen("#2563EB");
    private static readonly Brush Received = TerminalLineBrushConverter.Frozen("#059669");
    private static readonly Brush Error = TerminalLineBrushConverter.Frozen("#DC2626");
    private static readonly Brush Info = TerminalLineBrushConverter.Frozen("#64748B");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CommunicationDirection.Sent => Sent,
        CommunicationDirection.Received => Received,
        CommunicationDirection.Error => Error,
        _ => Info,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Picks the soft background for the connection badge in the instrument header.</summary>
public sealed class ConnectionStateSoftBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ConnectionState.Connected => "SuccessSoftBrush",
            ConnectionState.Connecting => "WarningSoftBrush",
            ConnectionState.Failed => "DangerSoftBrush",
            _ => "SurfaceAltBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
