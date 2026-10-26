using System.Windows.Input;
using StanfordController.Core;
using StanfordController.Core.Protocol;
using StanfordController.Core.Units;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.ViewModels;

/// <summary>
/// One output pulse (AB, CD, EF or GH) edited as a start time from T0 and a width. Tempo works out the two channel
/// delays: the start channel keeps its reference and gets the delay that puts it at the start time, and the end channel
/// follows the start channel by the width, so moving the start later moves the whole pulse.
/// </summary>
public sealed class PulseViewModel : ParameterViewModel
{
    /// <summary>Longest delay the DG645 sets.</summary>
    public const double MaxSeconds = 2000;

    private readonly InstrumentSessionViewModel _session;
    private string _startText = string.Empty;
    private string _widthText = string.Empty;
    private string _committedStart = string.Empty;
    private string _committedWidth = string.Empty;
    private double? _start;
    private double? _end;
    private bool _applying;

    internal PulseViewModel(InstrumentSessionViewModel session, Dg645Output output)
        : base(session, $"{output} pulse")
    {
        _session = session;
        Output = output;
        var name = output.ToString();
        StartChannel = session.Channels.Single(c => c.Channel == Enum.Parse<Dg645Channel>(name[..1]));
        EndChannel = session.Channels.Single(c => c.Channel == Enum.Parse<Dg645Channel>(name[1..]));
        CommitStartCommand = new AsyncRelayCommand(CommitStartAsync);
        CommitWidthCommand = new AsyncRelayCommand(CommitWidthAsync);
        RevertStartCommand = new RelayCommand(() => { StartText = _committedStart; Error = null; });
        RevertWidthCommand = new RelayCommand(() => { WidthText = _committedWidth; Error = null; });
    }

    public Dg645Output Output { get; }

    public string Name => Output.ToString();

    public ChannelDelayViewModel StartChannel { get; }

    public ChannelDelayViewModel EndChannel { get; }

    /// <summary>e.g. "A → B", or "A (Q-switch) → B" with labels.</summary>
    public string Channels => $"{Labelled(StartChannel)} → {Labelled(EndChannel)}";

    /// <summary>Start time from T0, as typed; applied on Enter or on leaving the field.</summary>
    public string StartText
    {
        get => _startText;
        set
        {
            if (SetProperty(ref _startText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsStartDirty));
            }
        }
    }

    public string WidthText
    {
        get => _widthText;
        set
        {
            if (SetProperty(ref _widthText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsWidthDirty));
            }
        }
    }

    public bool IsStartDirty => !string.Equals(_startText.Trim(), _committedStart, StringComparison.Ordinal);

    public bool IsWidthDirty => !string.Equals(_widthText.Trim(), _committedWidth, StringComparison.Ordinal);

    /// <summary>The end of the pulse from T0, e.g. "T0 + 3.5 µs".</summary>
    public string EndText => _end is { } end ? "T0 + " + EngineeringUnits.Format(end, Quantity.Time) : "—";

    /// <summary>How the two channels are set, e.g. "B follows A", or why the pulse cannot be shown.</summary>
    public string Note => _start is null || _end is null ? "The channel references cannot be resolved."
        : _end < _start ? $"No pulse: {EndChannel.Name} is set before {StartChannel.Name}. Set a width to fix it."
        : EndChannel.ReadReference == StartChannel.Channel ? $"{EndChannel.Name} follows {StartChannel.Name}, so moving the start keeps the width."
        : $"{EndChannel.Name} does not follow {StartChannel.Name}; setting the start or width makes it follow.";

    public bool HasNote => _start is null || _end is null || _end < _start || EndChannel.ReadReference != StartChannel.Channel;

    public ICommand CommitStartCommand { get; }

    public ICommand CommitWidthCommand { get; }

    public ICommand RevertStartCommand { get; }

    public ICommand RevertWidthCommand { get; }

    /// <summary>The pulse is worked out from the channel delays; there is nothing more to read.</summary>
    public override Task RefreshAsync() => Task.CompletedTask;

    /// <summary>Shows the pulse as the channel delays now place it (called whenever the timing is worked out again).</summary>
    public void Update()
    {
        // Keep what is being typed in a field when another change moves the pulse.
        var (keepStart, keepWidth) = _applying ? (false, false) : (IsStartDirty, IsWidthDirty);
        _start = StartChannel.AbsoluteSeconds;
        _end = EndChannel.AbsoluteSeconds;
        _committedStart = _start is { } s ? EngineeringUnits.Format(s, Quantity.Time) : string.Empty;
        _committedWidth = _start is { } a && _end is { } b ? EngineeringUnits.Format(b - a, Quantity.Time) : string.Empty;
        if (!keepStart)
        {
            StartText = _committedStart;
        }

        if (!keepWidth)
        {
            WidthText = _committedWidth;
        }

        OnPropertyChanged(nameof(IsStartDirty));
        OnPropertyChanged(nameof(IsWidthDirty));
        OnPropertyChanged(nameof(EndText));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(Channels));
    }

    private Task CommitStartAsync() => CommitAsync(IsStartDirty, StartText, "start", (start, _) => start);

    private Task CommitWidthAsync() => CommitAsync(IsWidthDirty, WidthText, "width", (_, width) => width);

    private async Task CommitAsync(bool dirty, string text, string what, Func<double, double, double> pick)
    {
        if (!dirty)
        {
            return;
        }

        if (!EngineeringUnits.TryParse(text.Trim(), Quantity.Time, out var value))
        {
            Error = $"\"{text.Trim()}\" is not a time. Examples: 1.5 us, 250 ns, 2 ms, 1e-6.";
            return;
        }

        if (value < 0 || value > MaxSeconds)
        {
            Error = $"The {what} must be between 0 and {EngineeringUnits.Format(MaxSeconds, Quantity.Time)}.";
            return;
        }

        // Typing a start keeps the width (a broken pulse gets none), and typing a width keeps the start.
        var start = what == "start" ? value : _start ?? 0;
        var width = what == "width" ? value : _start is { } s && _end is { } e && e >= s ? e - s : 0;
        await SetAsync(start, width);
    }

    /// <summary>Places the pulse at <paramref name="start"/> from T0, <paramref name="width"/> wide. Returns true on success.</summary>
    public async Task<bool> SetAsync(double start, double width)
    {
        if (start + width > MaxSeconds + 1e-12)
        {
            Error = $"The pulse would end after {EngineeringUnits.Format(MaxSeconds, Quantity.Time)}, the longest delay.";
            return false;
        }

        Error = null;

        // The start channel keeps its reference, unless that reference follows the end channel, which is about to follow it.
        var reference = StartChannel.ReadReference;
        var referenceTime = Absolute(reference);
        if (referenceTime is null || referenceTime > start || DependsOn(reference, EndChannel.Channel))
        {
            (reference, referenceTime) = (Dg645Channel.T0, 0);
        }

        var targets = new List<ChannelDelaySettings>
        {
            new() { Channel = StartChannel.Channel, Reference = reference, DelaySeconds = start - referenceTime.Value },
            new() { Channel = EndChannel.Channel, Reference = StartChannel.Channel, DelaySeconds = width },
        };

        var details = $"{Name} pulse set to start at T0 + {EngineeringUnits.Format(start, Quantity.Time)}, {EngineeringUnits.Format(width, Quantity.Time)} wide";
        string? error;
        _applying = true;
        try
        {
            error = await _session.SetDelaysAsync($"{Name} pulse", targets, details);
            Update();
        }
        finally
        {
            _applying = false;
        }

        Error = error;
        return error is null;
    }

    private double? Absolute(Dg645Channel channel) =>
        channel is Dg645Channel.T0 or Dg645Channel.T1 ? 0 : _session.Channels.FirstOrDefault(c => c.Channel == channel)?.AbsoluteSeconds;

    /// <summary>True if <paramref name="channel"/> is, or is referenced through others to, <paramref name="other"/>.</summary>
    private bool DependsOn(Dg645Channel channel, Dg645Channel other)
    {
        for (var hops = 0; hops <= _session.Channels.Count; hops++)
        {
            if (channel == other)
            {
                return true;
            }

            if (_session.Channels.FirstOrDefault(c => c.Channel == channel) is not { } vm)
            {
                return false;
            }

            channel = vm.ReadReference;
        }

        return true;
    }

    private static string Labelled(ChannelDelayViewModel channel) => channel.HasLabel ? $"{channel.Name} ({channel.ChannelLabel})" : channel.Name;
}
