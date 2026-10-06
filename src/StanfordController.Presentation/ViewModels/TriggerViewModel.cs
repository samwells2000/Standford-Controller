using System.Windows.Input;
using StanfordController.Core.Protocol;
using StanfordController.Core.Units;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.ViewModels;

/// <summary>Trigger source, rate, threshold, holdoff, inhibit and the advanced-triggering prescalers.</summary>
public sealed class TriggerViewModel : ParameterViewModel
{
    public TriggerViewModel(IInstrumentContext context)
        : base(context, "Trigger")
    {
        Source = new ChoiceParameterViewModel(context, "Trigger source",
            Enum.GetValues<TriggerSource>().Select(s => new ChoiceOption((int)s, Dg645Names.Describe(s))).ToList(),
            async ct => (int)await Client.GetTriggerSourceAsync(ct),
            (v, ct) => Client.SetTriggerSourceAsync((TriggerSource)v, ct))
        {
            Hint = "What starts each delay cycle (TSRC).",
        };
        Rate = Param.Number(context, "Internal rate", Quantity.Frequency,
            ct => Client.GetTriggerRateAsync(ct), (v, ct) => Client.SetTriggerRateAsync(v, ct),
            min: 100e-6, max: 10e6, hint: "Trigger rate when the source is Internal, 100 µHz to 10 MHz (TRAT).");
        Level = Param.Number(context, "Threshold", Quantity.Voltage,
            ct => Client.GetTriggerLevelAsync(ct), (v, ct) => Client.SetTriggerLevelAsync(v, ct),
            min: -3.5, max: 3.5, hint: "External trigger threshold, ±3.5 V (TLVL).");
        Holdoff = Param.Number(context, "Holdoff", Quantity.Time,
            ct => Client.GetHoldoffAsync(ct), (v, ct) => Client.SetHoldoffAsync(v, ct),
            min: 0, max: 2000, hint: "Minimum time after a trigger before another is accepted (HOLD). Needs advanced triggering.");
        Inhibit = new ChoiceParameterViewModel(context, "Inhibit",
            Enum.GetValues<InhibitMode>().Select(m => new ChoiceOption((int)m, Dg645Names.Describe(m))).ToList(),
            async ct => (int)await Client.GetInhibitAsync(ct),
            (v, ct) => Client.SetInhibitAsync((InhibitMode)v, ct))
        {
            Hint = "What the rear-panel INHIBIT input blocks while it is high (INHB).",
        };
        AdvancedTriggering = new ToggleParameterViewModel(context, "Advanced triggering",
            ct => Client.GetAdvancedTriggeringAsync(ct), (v, ct) => Client.SetAdvancedTriggeringAsync(v, ct))
        {
            Hint = "Enables holdoff and the trigger/output prescalers (ADVT).",
        };

        Prescalers = Enum.GetValues<PrescaleInput>().Select(i => new PrescalerViewModel(context, i)).ToList();

        Source.ValueChanged += (_, _) => OnPropertyChanged(nameof(IsSingleShot));
        TriggerNowCommand = new AsyncRelayCommand(TriggerNowAsync);
    }

    public ChoiceParameterViewModel Source { get; }

    public EditableParameterViewModel<double> Rate { get; }

    public EditableParameterViewModel<double> Level { get; }

    public EditableParameterViewModel<double> Holdoff { get; }

    public ChoiceParameterViewModel Inhibit { get; }

    public ToggleParameterViewModel AdvancedTriggering { get; }

    public IReadOnlyList<PrescalerViewModel> Prescalers { get; }

    /// <summary>True when *TRG (the Trigger button) starts a cycle.</summary>
    public bool IsSingleShot => Source.SelectedValue is (int)TriggerSource.SingleShot
        or (int)TriggerSource.SingleShotExternalRising
        or (int)TriggerSource.SingleShotExternalFalling;

    public ICommand TriggerNowCommand { get; }

    public override async Task RefreshAsync()
    {
        await Source.RefreshAsync();
        await Rate.RefreshAsync();
        await Level.RefreshAsync();
        await Holdoff.RefreshAsync();
        await Inhibit.RefreshAsync();
        await AdvancedTriggering.RefreshAsync();
        foreach (var prescaler in Prescalers)
        {
            await prescaler.RefreshAsync();
        }
    }

    private Task TriggerNowAsync() => RunAsync(() => Client.TriggerAsync());
}

/// <summary>A prescaler: divides the trigger input (index 0) or gates an output pair (AB-GH) to every Nth cycle.</summary>
public sealed class PrescalerViewModel : ParameterViewModel
{
    public PrescalerViewModel(IInstrumentContext context, PrescaleInput input)
        : base(context, input == PrescaleInput.Trigger ? "Trigger input" : $"Output {input}")
    {
        Input = input;
        var b = (int)input;
        Factor = Param.Integer(context, $"{Label} prescale factor",
            ct => Client.GetPrescaleFactorAsync(input, ct), (v, ct) => Client.SetPrescaleFactorAsync(input, v, ct),
            min: 1, max: (1L << 30) - 1,
            hint: input == PrescaleInput.Trigger
                ? $"Only every Nth trigger starts a delay cycle (PRES {b},i)."
                : $"The output pulses on every Nth delay cycle (PRES {b},i).");

        if (input != PrescaleInput.Trigger)
        {
            Phase = Param.Integer(context, $"{Label} prescale phase",
                ct => Client.GetPrescalePhaseAsync(input, ct), (v, ct) => Client.SetPrescalePhaseAsync(input, v, ct),
                min: 0, max: (1L << 30) - 2,
                hint: $"Which cycle of the N the output fires on, 0 to N-1 (PHAS {b},i).");
        }
    }

    public PrescaleInput Input { get; }

    public string Name => Label;

    public EditableParameterViewModel<long> Factor { get; }

    public EditableParameterViewModel<long>? Phase { get; }

    public bool HasPhase => Phase is not null;

    public override async Task RefreshAsync()
    {
        await Factor.RefreshAsync();
        if (Phase is not null)
        {
            await Phase.RefreshAsync();
        }
    }
}
