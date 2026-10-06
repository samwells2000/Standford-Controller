using Avalonia;
using Avalonia.Controls;

namespace StanfordController.App.Controls;

/// <summary>
/// The colour that identifies an output (T0, AB, CD, EF, GH) in tables and the timing diagram.
/// Always placed next to the output's name, so the colour is never the only cue.
/// </summary>
public sealed class SeriesSwatch : Border
{
    public static readonly StyledProperty<object?> OutputProperty =
        AvaloniaProperty.Register<SeriesSwatch, object?>(nameof(Output));

    private IDisposable? _brush;

    public object? Output
    {
        get => GetValue(OutputProperty);
        set => SetValue(OutputProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OutputProperty)
        {
            _brush?.Dispose();
            _brush = this.Bind(BackgroundProperty, this.GetResourceObservable($"Series{change.NewValue ?? "T0"}Brush"));
        }
    }
}
