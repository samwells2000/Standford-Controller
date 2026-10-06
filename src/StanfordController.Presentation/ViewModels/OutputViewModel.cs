using System.Windows.Input;
using StanfordController.Core.Protocol;
using StanfordController.Core.Units;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.ViewModels;

/// <summary>One front-panel BNC output (T0, AB, CD, EF, GH): amplitude, offset, polarity and their step sizes.</summary>
public sealed class OutputViewModel : ParameterViewModel
{
    public const double TtlAmplitude = 4.0;
    public const double NimAmplitude = 0.8;
    public const double NimOffset = -0.8;

    public OutputViewModel(IInstrumentContext context, Dg645Output output)
        : base(context, $"Output {output}")
    {
        Output = output;
        var b = (int)output;

        Amplitude = Param.Number(context, $"{output} amplitude", Quantity.Voltage,
            ct => Client.GetAmplitudeAsync(output, ct), (v, ct) => Client.SetAmplitudeAsync(output, v, ct),
            hint: $"Pulse amplitude in volts (LAMP {b},v).");
        Offset = Param.Number(context, $"{output} offset", Quantity.Voltage,
            ct => Client.GetOffsetAsync(output, ct), (v, ct) => Client.SetOffsetAsync(output, v, ct),
            hint: $"Baseline offset in volts (LOFF {b},v).");
        Polarity = new ChoiceParameterViewModel(context, $"{output} polarity",
            [
                new ChoiceOption((int)OutputPolarity.Positive, "Positive"),
                new ChoiceOption((int)OutputPolarity.Negative, "Negative"),
            ],
            async ct => (int)await Client.GetPolarityAsync(output, ct),
            (v, ct) => Client.SetPolarityAsync(output, (OutputPolarity)v, ct))
        {
            Hint = $"Positive pulses rise from the offset; negative pulses fall to it (LPOL {b},i).",
        };
        AmplitudeStep = Param.Number(context, $"{output} amplitude step", Quantity.Voltage,
            ct => Client.GetAmplitudeStepAsync(output, ct), (v, ct) => Client.SetAmplitudeStepAsync(output, v, ct),
            min: 0, hint: $"Amount the amplitude ▲/▼ buttons change it (SSLA {b},v).");
        OffsetStep = Param.Number(context, $"{output} offset step", Quantity.Voltage,
            ct => Client.GetOffsetStepAsync(output, ct), (v, ct) => Client.SetOffsetStepAsync(output, v, ct),
            min: 0, hint: $"Amount the offset ▲/▼ buttons change it (SSLO {b},v).");

        Amplitude.ValueChanged += (_, _) => OnLevelsChanged();
        Offset.ValueChanged += (_, _) => OnLevelsChanged();
        Polarity.ValueChanged += (_, _) => OnLevelsChanged();

        AmplitudeUpCommand = new AsyncRelayCommand(() => StepAsync(() => Client.StepAmplitudeAsync(output, true), Amplitude));
        AmplitudeDownCommand = new AsyncRelayCommand(() => StepAsync(() => Client.StepAmplitudeAsync(output, false), Amplitude));
        OffsetUpCommand = new AsyncRelayCommand(() => StepAsync(() => Client.StepOffsetAsync(output, true), Offset));
        OffsetDownCommand = new AsyncRelayCommand(() => StepAsync(() => Client.StepOffsetAsync(output, false), Offset));
        TtlCommand = new AsyncRelayCommand(() => ApplyPresetAsync(TtlAmplitude, 0, OutputPolarity.Positive));
        NimCommand = new AsyncRelayCommand(() => ApplyPresetAsync(NimAmplitude, NimOffset, OutputPolarity.Negative));
    }

    public Dg645Output Output { get; }

    public string Name => Output.ToString();

    public string Description => Output == Dg645Output.T0
        ? "Marks the start of each delay cycle."
        : $"High from {Output.ToString()[0]} to {Output.ToString()[1]} (positive polarity).";

    public EditableParameterViewModel<double> Amplitude { get; }

    public EditableParameterViewModel<double> Offset { get; }

    public ChoiceParameterViewModel Polarity { get; }

    public EditableParameterViewModel<double> AmplitudeStep { get; }

    public EditableParameterViewModel<double> OffsetStep { get; }

    public ICommand AmplitudeUpCommand { get; }

    public ICommand AmplitudeDownCommand { get; }

    public ICommand OffsetUpCommand { get; }

    public ICommand OffsetDownCommand { get; }

    public ICommand TtlCommand { get; }

    public ICommand NimCommand { get; }

    public bool IsPositive => Polarity.SelectedValue != (int)OutputPolarity.Negative;

    /// <summary>Output level between pulses.</summary>
    public double? IdleVolts => Levels()?.Idle;

    /// <summary>Output level during the pulse.</summary>
    public double? ActiveVolts => Levels()?.Active;

    public string LevelSummary => Levels() is { } l
        ? $"Idle {EngineeringUnits.Format(l.Idle, Quantity.Voltage)}  →  pulse {EngineeringUnits.Format(l.Active, Quantity.Voltage)}"
        : "—";

    public override async Task RefreshAsync()
    {
        await Amplitude.RefreshAsync();
        await Offset.RefreshAsync();
        await Polarity.RefreshAsync();
        await AmplitudeStep.RefreshAsync();
        await OffsetStep.RefreshAsync();
    }

    private (double Idle, double Active)? Levels()
    {
        if (!Amplitude.HasValue || !Offset.HasValue)
        {
            return null;
        }

        var low = Offset.Value;
        var high = Offset.Value + Amplitude.Value;
        return IsPositive ? (low, high) : (high, low);
    }

    private void OnLevelsChanged()
    {
        OnPropertyChanged(nameof(IsPositive));
        OnPropertyChanged(nameof(IdleVolts));
        OnPropertyChanged(nameof(ActiveVolts));
        OnPropertyChanged(nameof(LevelSummary));
    }

    private async Task StepAsync(Func<Task> step, EditableParameterViewModel<double> readBack)
    {
        if (await RunAsync(step))
        {
            await readBack.RefreshAsync();
            Context.NotifySettingsChanged();
        }
    }

    private async Task ApplyPresetAsync(double amplitude, double offset, OutputPolarity polarity)
    {
        // Lower the amplitude before moving the offset so the combined swing stays within limits.
        await RunAsync(async () =>
        {
            await Client.SetAmplitudeAsync(Output, Math.Min(amplitude, Amplitude.HasValue ? Amplitude.Value : amplitude));
            await Client.SetOffsetAsync(Output, offset);
            await Client.SetAmplitudeAsync(Output, amplitude);
            await Client.SetPolarityAsync(Output, polarity);
        });
        await Amplitude.RefreshAsync();
        await Offset.RefreshAsync();
        await Polarity.RefreshAsync();
        Context.NotifySettingsChanged();
    }
}
