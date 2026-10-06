using StanfordController.Core.Protocol;
using StanfordController.Core.Units;

namespace StanfordController.Presentation.ViewModels;

/// <summary>Burst mode: each trigger produces a train of delay cycles.</summary>
public sealed class BurstViewModel : ParameterViewModel
{
    public BurstViewModel(IInstrumentContext context)
        : base(context, "Burst")
    {
        Enabled = new ToggleParameterViewModel(context, "Burst mode",
            ct => Client.GetBurstModeAsync(ct), (v, ct) => Client.SetBurstModeAsync(v, ct))
        {
            Hint = "Each trigger produces a burst of delay cycles (BURM).",
        };
        Count = Param.Integer(context, "Burst count",
            ct => Client.GetBurstCountAsync(ct), (v, ct) => Client.SetBurstCountAsync(v, ct),
            min: 1, max: uint.MaxValue, hint: "Number of delay cycles in each burst (BURC).");
        Period = Param.Number(context, "Burst period", Quantity.Time,
            ct => Client.GetBurstPeriodAsync(ct), (v, ct) => Client.SetBurstPeriodAsync(v, ct),
            min: 100e-9, max: 2000, hint: "Time between delay cycles within a burst, from 100 ns (BURP).");
        Delay = Param.Number(context, "Burst delay", Quantity.Time,
            ct => Client.GetBurstDelayAsync(ct), (v, ct) => Client.SetBurstDelayAsync(v, ct),
            min: 0, max: 2000, hint: "Time from the trigger to the first delay cycle of the burst (BURD).");
        T0Mode = new ChoiceParameterViewModel(context, "T0 output",
            Enum.GetValues<BurstT0Mode>().Select(m => new ChoiceOption((int)m, Dg645Names.Describe(m))).ToList(),
            async ct => (int)await Client.GetBurstT0ModeAsync(ct),
            (v, ct) => Client.SetBurstT0ModeAsync((BurstT0Mode)v, ct))
        {
            Hint = "Whether T0 fires for every cycle in the burst or only the first (BURT).",
        };

        Count.ValueChanged += (_, _) => OnPropertyChanged(nameof(Summary));
        Period.ValueChanged += (_, _) => OnPropertyChanged(nameof(Summary));
        Delay.ValueChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    public ToggleParameterViewModel Enabled { get; }

    public EditableParameterViewModel<long> Count { get; }

    public EditableParameterViewModel<double> Period { get; }

    public EditableParameterViewModel<double> Delay { get; }

    public ChoiceParameterViewModel T0Mode { get; }

    /// <summary>Plain-English description of the configured burst.</summary>
    public string Summary
    {
        get
        {
            if (!Count.HasValue || !Period.HasValue || !Delay.HasValue)
            {
                return string.Empty;
            }

            var total = Delay.Value + Math.Max(0, Count.Value - 1) * Period.Value;
            return $"Each trigger starts {Count.Value} delay cycle{(Count.Value == 1 ? "" : "s")}, " +
                   $"{EngineeringUnits.Format(Period.Value, Quantity.Time)} apart, beginning {EngineeringUnits.Format(Delay.Value, Quantity.Time)} after the trigger. " +
                   $"The last cycle starts {EngineeringUnits.Format(total, Quantity.Time)} after the trigger.";
        }
    }

    public override async Task RefreshAsync()
    {
        await Enabled.RefreshAsync();
        await Count.RefreshAsync();
        await Period.RefreshAsync();
        await Delay.RefreshAsync();
        await T0Mode.RefreshAsync();
    }
}
