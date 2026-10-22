using System.Windows.Input;
using StanfordController.Core;
using StanfordController.Core.Protocol;
using StanfordController.Core.Units;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.ViewModels;

/// <summary>One delay channel (A-H): "channel = reference + delay", plus its step size and step buttons.</summary>
public sealed class ChannelDelayViewModel : ParameterViewModel
{
    private ChoiceOption? _reference;
    private bool _updating;
    private string _delayText = string.Empty;
    private string _committedDelayText = string.Empty;
    private double _delaySeconds;
    private Dg645Channel _readReference;
    private double? _absoluteSeconds;
    private string _label = string.Empty;
    private string _labelText = string.Empty;

    public ChannelDelayViewModel(IInstrumentContext context, Dg645Channel channel)
        : base(context, $"Channel {channel}")
    {
        Channel = channel;
        ReferenceOptions = new[] { Dg645Channel.T0 }
            .Concat(Dg645Names.DelayChannels)
            .Where(c => c != channel)
            .Select(c => new ChoiceOption((int)c, c.ToString()))
            .ToList();

        StepSize = Param.Number(
            context,
            $"{channel} step size",
            Quantity.Time,
            ct => Client.GetDelayStepAsync(channel, ct),
            (v, ct) => Client.SetDelayStepAsync(channel, v, ct),
            min: 0,
            max: 2000,
            hint: $"Amount the up/down buttons change the delay (SSDL {(int)channel},t).");

        CommitCommand = new AsyncRelayCommand(CommitAsync);
        RevertCommand = new RelayCommand(Revert);
        StepUpCommand = new AsyncRelayCommand(() => StepAsync(up: true));
        StepDownCommand = new AsyncRelayCommand(() => StepAsync(up: false));
        CommitLabelCommand = new RelayCommand(CommitLabel);
        RevertLabelCommand = new RelayCommand(() => LabelText = _label);
    }

    /// <summary>The user has changed the label: (old label, new label). Blank means no label.</summary>
    public event EventHandler<(string Before, string After)>? LabelCommitted;

    public Dg645Channel Channel { get; }

    public string Name => Channel.ToString();

    public Dg645Output Output => Channel switch
    {
        Dg645Channel.A or Dg645Channel.B => Dg645Output.AB,
        Dg645Channel.C or Dg645Channel.D => Dg645Output.CD,
        Dg645Channel.E or Dg645Channel.F => Dg645Output.EF,
        _ => Dg645Output.GH,
    };

    /// <summary>A, C, E and G start their output's pulse; B, D, F and H end it.</summary>
    public bool StartsPulse => (int)Channel % 2 == 0;

    public string Role => StartsPulse ? $"Start of {Output} pulse" : $"End of {Output} pulse";

    /// <summary>e.g. "AB start" for the table column.</summary>
    public string RoleShort => StartsPulse ? $"{Output} start" : $"{Output} end";

    /// <summary>The user's name for the channel, e.g. "Q-switch", kept with the instrument in the configuration; empty if none.</summary>
    public string ChannelLabel => _label;

    public bool HasLabel => _label.Length > 0;

    /// <summary>The label as typed, applied on Enter or on leaving the field.</summary>
    public string LabelText
    {
        get => _labelText;
        set => SetProperty(ref _labelText, value ?? string.Empty);
    }

    /// <summary>e.g. "Start of AB pulse: Q-switch", for the channel's tooltip.</summary>
    public string RoleAndLabel => HasLabel ? $"{Role}: {_label}" : Role;

    public ICommand CommitLabelCommand { get; }

    public ICommand RevertLabelCommand { get; }

    public IReadOnlyList<ChoiceOption> ReferenceOptions { get; }

    public ChoiceOption? Reference
    {
        get => _reference;
        set
        {
            if (value is null || Equals(value, _reference))
            {
                return;
            }

            _reference = value;
            OnPropertyChanged();
            if (!_updating)
            {
                _ = ChangeReferenceAsync((Dg645Channel)value.Value);
            }
        }
    }

    public Dg645Channel ReferenceChannel => (Dg645Channel)(_reference?.Value ?? 0);

    public string DelayText
    {
        get => _delayText;
        set
        {
            if (SetProperty(ref _delayText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public bool IsDirty => !string.Equals(_delayText.Trim(), _committedDelayText, StringComparison.Ordinal);

    public double DelaySeconds => _delaySeconds;

    /// <summary>Delay from T0, resolved through the reference chain; null if it cannot be resolved.</summary>
    public double? AbsoluteSeconds
    {
        get => _absoluteSeconds;
        set
        {
            if (SetProperty(ref _absoluteSeconds, value))
            {
                OnPropertyChanged(nameof(AbsoluteText));
            }
        }
    }

    /// <summary>The delay as read, e.g. "A + 1.5 µs".</summary>
    public string DelaySummary => _reference is null ? "—" : ReadingText;

    public string AbsoluteText => _absoluteSeconds is { } a ? "T0 + " + EngineeringUnits.Format(a, Quantity.Time) : "—";

    public EditableParameterViewModel<double> StepSize { get; }

    public ICommand CommitCommand { get; }

    public ICommand RevertCommand { get; }

    public ICommand StepUpCommand { get; }

    public ICommand StepDownCommand { get; }

    public override async Task RefreshAsync()
    {
        await RunAsync(ReadBackAsync, reading: true);
        await StepSize.RefreshAsync();
    }

    public void ApplyReading(Dg645Channel reference, double seconds)
    {
        _updating = true;
        try
        {
            _reference = ReferenceOptions.FirstOrDefault(o => o.Value == (int)reference);
            OnPropertyChanged(nameof(Reference));
            OnPropertyChanged(nameof(ReferenceChannel));
            if (_reference is null)
            {
                Error = $"Referenced to {reference}, which this view does not offer.";
            }

            _readReference = reference;
            _delaySeconds = seconds;
            _committedDelayText = EngineeringUnits.Format(seconds, Quantity.Time);
            DelayText = _committedDelayText;
            OnPropertyChanged(nameof(DelaySeconds));
            OnPropertyChanged(nameof(DelaySummary));
            OnPropertyChanged(nameof(IsDirty));
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Shows <paramref name="label"/> without reporting it as changed, e.g. the label saved in the configuration.</summary>
    public void ApplyLabel(string? label)
    {
        _label = label?.Trim() ?? string.Empty;
        LabelText = _label;
        OnPropertyChanged(nameof(ChannelLabel));
        OnPropertyChanged(nameof(HasLabel));
        OnPropertyChanged(nameof(RoleAndLabel));
    }

    private void CommitLabel()
    {
        var before = _label;
        ApplyLabel(LabelText);
        if (_label != before)
        {
            LabelCommitted?.Invoke(this, (before, _label));
        }
    }

    private async Task ReadBackAsync()
    {
        var (reference, seconds) = await Client.GetDelayAsync(Channel);
        ApplyReading(reference, seconds);
    }

    private async Task CommitAsync()
    {
        if (!IsDirty)
        {
            return;
        }

        if (!EngineeringUnits.TryParse(DelayText.Trim(), Quantity.Time, out var seconds))
        {
            Error = $"\"{DelayText.Trim()}\" is not a time. Examples: 1.5 us, 250 ns, 2 ms, 1e-6.";
            return;
        }

        await WriteAsync(ReferenceChannel, seconds);
    }

    private async Task ChangeReferenceAsync(Dg645Channel reference)
    {
        await Task.Yield();
        await WriteAsync(reference, _delaySeconds);
    }

    private Task WriteAsync(Dg645Channel reference, double seconds) =>
        ChangeAsync(ReadingText, () => Client.SetDelayAsync(Channel, reference, seconds), TryReadBackAsync, () => ReadingText, $"Delay {Channel}");

    private Task StepAsync(bool up) =>
        ChangeAsync(ReadingText, () => Client.StepDelayAsync(Channel, up), TryReadBackAsync, () => ReadingText, $"Delay {Channel}");

    /// <summary>The delay last read from the instrument, e.g. "A + 1.5 µs", for the audit trail.</summary>
    private string ReadingText => Dg645Settings.DescribeDelay(_readReference, _delaySeconds);

    private async Task TryReadBackAsync()
    {
        try
        {
            await ReadBackAsync();
        }
        catch
        {
            // Keep the original error.
        }
    }

    private void Revert()
    {
        DelayText = _committedDelayText;
        Error = null;
    }
}
