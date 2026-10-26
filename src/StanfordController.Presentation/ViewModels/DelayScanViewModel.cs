using System.Globalization;
using System.Windows.Input;
using StanfordController.Core.Audit;
using StanfordController.Core.Protocol;
using StanfordController.Core.Units;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.ViewModels;

/// <summary>
/// Steps one channel's delay through evenly spaced values, waiting at each, for example to find the best timing
/// for an experiment. The channel keeps its reference; the values are its delay from that reference. The start and
/// end of the scan are recorded in the audit trail, with every value it uses.
/// </summary>
public sealed class DelayScanViewModel : ObservableObject
{
    /// <summary>Most values one scan takes.</summary>
    public const int MaxValues = 10_000;

    /// <summary>Shortest wait at each value.</summary>
    public const double MinDwellSeconds = 0.05;

    private readonly InstrumentSessionViewModel _session;
    private readonly AsyncRelayCommand _startCommand;
    private readonly RelayCommand _stopCommand;
    private ChoiceOption? _channel;
    private string _fromText = "0 s";
    private string _toText = "1 µs";
    private string _countText = "11";
    private string _dwellText = "1 s";
    private bool _returnToStart = true;
    private bool _isRunning;
    private string? _error;
    private string _progressText = string.Empty;
    private double _progress;
    private CancellationTokenSource? _cancellation;

    internal DelayScanViewModel(InstrumentSessionViewModel session)
    {
        _session = session;
        _channel = ChannelOptions[0];
        _startCommand = new AsyncRelayCommand(RunAsync, () => !_isRunning && !_session.IsBusy,
            unavailableReason: () => _isRunning ? "A scan is already running." : _session.BusyReason);
        _stopCommand = new RelayCommand(Stop, () => _isRunning, unavailableReason: () => "No scan is running.");
    }

    public IReadOnlyList<ChoiceOption> ChannelOptions { get; } =
        Dg645Names.DelayChannels.Select(c => new ChoiceOption((int)c, c.ToString())).ToList();

    public ChoiceOption? Channel
    {
        get => _channel;
        set
        {
            if (SetProperty(ref _channel, value))
            {
                RaiseDescription();
            }
        }
    }

    /// <summary>The first delay, from the channel's reference.</summary>
    public string FromText
    {
        get => _fromText;
        set
        {
            if (SetProperty(ref _fromText, value ?? string.Empty))
            {
                RaiseDescription();
            }
        }
    }

    public string ToText
    {
        get => _toText;
        set
        {
            if (SetProperty(ref _toText, value ?? string.Empty))
            {
                RaiseDescription();
            }
        }
    }

    /// <summary>How many values, the first and last included.</summary>
    public string CountText
    {
        get => _countText;
        set
        {
            if (SetProperty(ref _countText, value ?? string.Empty))
            {
                RaiseDescription();
            }
        }
    }

    /// <summary>How long to stay at each value.</summary>
    public string DwellText
    {
        get => _dwellText;
        set
        {
            if (SetProperty(ref _dwellText, value ?? string.Empty))
            {
                RaiseDescription();
            }
        }
    }

    /// <summary>Put the channel's delay back as it was when the scan ends, also when it is stopped.</summary>
    public bool ReturnToStart
    {
        get => _returnToStart;
        set => SetProperty(ref _returnToStart, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                RaiseCanStartChanged();
                _stopCommand.RaiseCanExecuteChanged();
                _session.OnScanRunningChanged();
            }
        }
    }

    public bool IsIdle => !_isRunning;

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    /// <summary>e.g. "Step 4 of 11: A = T0 + 300 ns".</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>0 to 1.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>The scan in words, e.g. "11 values of A from T0 + 0 s to T0 + 1 µs, 100 ns apart, 1 s each: about 11 s.", or what is wrong.</summary>
    public string Description => TryPlan(out var plan, out var error)
        ? $"{plan.Count:N0} values of {plan.Channel} from {Delay(plan.Reference, plan.From)} to {Delay(plan.Reference, plan.To)}, " +
          $"{EngineeringUnits.Format(Math.Abs(plan.Step), Quantity.Time)} apart, {EngineeringUnits.Format(plan.Dwell, Quantity.Time)} at each: " +
          $"about {Duration(plan.Count * plan.Dwell)}."
        : error!;

    public ICommand StartCommand => _startCommand;

    public ICommand StopCommand => _stopCommand;

    public void Stop() => _cancellation?.Cancel();

    internal void RaiseCanStartChanged() => _startCommand.RaiseCanExecuteChanged();

    /// <summary>The values a scan takes, in order.</summary>
    internal sealed record Plan(Dg645Channel Channel, Dg645Channel Reference, double From, double To, int Count, double Dwell)
    {
        public double Step => Count > 1 ? (To - From) / (Count - 1) : 0;

        public double ValueAt(int index) => index == Count - 1 ? To : From + Step * index;
    }

    internal bool TryPlan(out Plan plan, out string? error)
    {
        plan = null!;
        error = null;
        if (_channel is null)
        {
            error = "Choose a channel.";
            return false;
        }

        var channel = (Dg645Channel)_channel.Value;
        var reference = _session.Channels.First(c => c.Channel == channel).ReadReference;
        if (!EngineeringUnits.TryParse(_fromText.Trim(), Quantity.Time, out var from) || !EngineeringUnits.TryParse(_toText.Trim(), Quantity.Time, out var to))
        {
            error = "From and To must be times, e.g. 1.5 us, 250 ns or 1e-6.";
            return false;
        }

        if (Math.Abs(from) > PulseViewModel.MaxSeconds || Math.Abs(to) > PulseViewModel.MaxSeconds)
        {
            error = $"From and To must be within {EngineeringUnits.Format(PulseViewModel.MaxSeconds, Quantity.Time)}.";
            return false;
        }

        if (!int.TryParse(_countText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 2 || count > MaxValues)
        {
            error = $"Values must be a whole number from 2 to {MaxValues:N0}.";
            return false;
        }

        if (!EngineeringUnits.TryParse(_dwellText.Trim(), Quantity.Time, out var dwell) || dwell < MinDwellSeconds || dwell > 3600)
        {
            error = $"Time at each value must be from {EngineeringUnits.Format(MinDwellSeconds, Quantity.Time)} to 1 hour.";
            return false;
        }

        plan = new Plan(channel, reference, from, to, count, dwell);
        return true;
    }

    private async Task RunAsync()
    {
        if (!TryPlan(out var plan, out var error))
        {
            Error = error;
            return;
        }

        Error = null;
        var channel = _session.Channels.First(c => c.Channel == plan.Channel);
        var (original, originalText) = (channel.DelaySeconds, channel.ReadingText);
        var setting = $"Delay {plan.Channel}";
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsRunning = true;
        _session.Audit(AuditActions.ScanStarted, setting, originalText, details: Description.TrimEnd('.'));

        var done = 0;
        string ending;
        try
        {
            for (var i = 0; i < plan.Count; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var value = plan.ValueAt(i);
                using (_session.BeginChange())
                {
                    await _session.Client.SetDelayAsync(plan.Channel, plan.Reference, value);
                }

                await channel.TryReadBackAsync();
                _session.RecomputeTiming();
                done = i + 1;
                Progress = (double)done / plan.Count;
                ProgressText = $"Value {done:N0} of {plan.Count:N0}: {plan.Channel} = {channel.ReadingText}";
                await Task.Delay(TimeSpan.FromSeconds(plan.Dwell), cancellation.Token);
            }

            ending = $"All {plan.Count:N0} values done";
        }
        catch (OperationCanceledException)
        {
            ending = $"Stopped after {done:N0} of {plan.Count:N0} values";
        }
        catch (Exception ex)
        {
            ending = $"Failed after {done:N0} of {plan.Count:N0} values: {ParameterViewModel.DescribeError(ex)}";
            _session.ReportError("Delay scan", ex);
            Error = ParameterViewModel.DescribeError(ex);
        }

        var last = channel.DelaySeconds;
        if (_returnToStart && done > 0)
        {
            try
            {
                using (_session.BeginChange())
                {
                    await _session.Client.SetDelayAsync(plan.Channel, plan.Reference, original);
                }

                ending += $", then put back to {originalText}";
            }
            catch (Exception ex)
            {
                ending += $". Could not put it back to {originalText}: {ParameterViewModel.DescribeError(ex)}";
                _session.ReportError("Delay scan", ex);
            }

            await channel.TryReadBackAsync();
        }

        _cancellation = null;
        cancellation.Dispose();
        _session.Audit(AuditActions.ScanEnded, setting, originalText, channel.ReadingText, ending);
        if (done > 0)
        {
            _session.NotifySettingsChanged();
        }

        // Leaving the channel at the last value is one change Undo can take back.
        if (channel.ReadingText != originalText)
        {
            var reference = plan.Reference;
            _session.RecordUndo(new UndoStep(setting, originalText, channel.ReadingText,
                () => channel.WriteAsync(reference, original), () => channel.WriteAsync(reference, last)));
        }

        ProgressText = ending + ".";
        IsRunning = false;
    }

    private void RaiseDescription() => OnPropertyChanged(nameof(Description));

    private static string Delay(Dg645Channel reference, double seconds) =>
        Core.Dg645Settings.DescribeDelay(reference, seconds);

    private static string Duration(double seconds) =>
        seconds < 60 ? EngineeringUnits.Format(Math.Round(seconds, 3), Quantity.Time)
        : seconds < 3600 ? $"{seconds / 60:0.#} min"
        : $"{seconds / 3600:0.#} h";
}
