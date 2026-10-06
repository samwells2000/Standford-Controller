using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App.Infrastructure;

/// <summary>Terminal line colours. The terminal is dark in both themes, so these are fixed.</summary>
public sealed class TerminalLineBrushConverter : IValueConverter
{
    public static TerminalLineBrushConverter Instance { get; } = new();

    private static readonly IBrush Command = new ImmutableSolidColorBrush(Color.Parse("#61D6D6"));
    private static readonly IBrush Response = new ImmutableSolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IBrush Error = new ImmutableSolidColorBrush(Color.Parse("#F48771"));
    private static readonly IBrush Info = new ImmutableSolidColorBrush(Color.Parse("#8C8C8C"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TerminalLineKind.Command => Command,
        TerminalLineKind.Response => Response,
        TerminalLineKind.Error => Error,
        _ => Info,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
