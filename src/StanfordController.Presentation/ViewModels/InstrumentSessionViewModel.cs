using System.IO;
using System.Net.Sockets;
using System.Windows.Input;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using StanfordController.Core;
using StanfordController.Core.Audit;
using StanfordController.Core.Profiles;
using StanfordController.Core.Protocol;
using StanfordController.Core.Units;
using StanfordController.Presentation.Mvvm;
using StanfordController.Presentation.Services;

namespace StanfordController.Presentation.ViewModels;

/// <summary>One pulse for the timing diagram, in seconds from T0.</summary>
public sealed record PulseTiming(string Output, string StartChannel, string EndChannel, double? Start, double? End, bool Positive)
{
    /// <summary>The labels of the start and end channels, for the hover read-out; empty if they have none.</summary>
    public string StartLabel { get; init; } = string.Empty;

    public string EndLabel { get; init; } = string.Empty;

    public bool IsValid => Start is not null && End is not null && End >= Start;

    public double Width => IsValid ? End!.Value - Start!.Value : 0;
}

/// <summary>
/// Burst mode as the timing diagram draws it: each trigger starts <paramref name="Count"/> delay cycles,
/// <paramref name="Period"/> apart, the first <paramref name="Delay"/> after the trigger. T0 fires on every
/// cycle or only the first.
/// </summary>
public sealed record BurstTiming(long Count, double Period, double Delay, bool T0EveryCycle)
{
    /// <summary>Time from the trigger to the start of delay cycle <paramref name="cycle"/> (0-based).</summary>
    public double CycleStart(long cycle) => Delay + cycle * Period;
}

/// <summary>Everything shown for a connected instrument.</summary>
public sealed class InstrumentSessionViewModel : ObservableObject, IInstrumentContext
{
    private static readonly FileFilter SetupFilter = new("DG645 setup", "*" + Dg645Settings.FileExtension);

    private readonly LogViewModel _log;
    private readonly AuditLog? _audit;
    private CancellationTokenSource? _heartbeatCancellation;
    private bool _heartbeatOn;
    private double _heartbeatIntervalSeconds = 1;
    private int _missedBeats;
    private long _beats;
    private DateTime? _lastReply;
    private TimeSpan? _latency;
    private bool _isBusy;
    private string? _busyText;
    private string? _statusMessage;
    private bool _statusIsError;
    private int _saveSlot = 1;
    private ChoiceOption? _recallSlot;
    private IReadOnlyList<PulseTiming> _timing = [];
    private BurstTiming? _timingBurst;
    private bool _disconnectRequested;
    private bool _settingsMayBeStale;
    private bool _hasUnsavedChanges;
    private bool _isArmed;
    private int? _armedSource;
    private string _changeReason = string.Empty;

    public InstrumentSessionViewModel(InstrumentProfile profile, Dg645Client client, IDialogService dialogs, LogViewModel log, AuditLog? audit = null)
    {
        Profile = profile;
        Client = client;
        Dialogs = dialogs;
        _log = log;
        _audit = audit;

        // A safety net: anything that changes the instrument without recording itself is recorded as sent.
        Client.UnrecordedCommandSent += (_, line) => Audit(AuditActions.Command, newValue: line, details: "Sent by Tempo");

        Channels = Dg645Names.DelayChannels.Select(c => new ChannelDelayViewModel(this, c)).ToList();
        foreach (var channel in Channels)
        {
            channel.ApplyLabel(profile.LabelOf(channel.Channel));
            channel.LabelCommitted += OnChannelLabelCommitted;
        }

        Outputs = Dg645Names.Outputs.Select(o => new OutputViewModel(this, o)).ToList();
        Trigger = new TriggerViewModel(this);
        Burst = new BurstViewModel(this);
        Status = new StatusViewModel(this);
        Interface = new InterfaceViewModel(this);
        Terminal = new TerminalViewModel(this);

        RecallSlots = new[] { new ChoiceOption(0, "0 — factory defaults") }
            .Concat(Enumerable.Range(1, 9).Select(i => new ChoiceOption(i, $"Location {i}")))
            .ToList();
        _recallSlot = RecallSlots[1];

        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync, () => !IsBusy);
        TriggerCommand = new AsyncRelayCommand(TriggerOrArmAsync, () => Trigger.IsSingleShot);
        Trigger.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TriggerViewModel.IsSingleShot))
            {
                (TriggerCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
            }
        };
        SaveToSlotCommand = new AsyncRelayCommand(SaveToSlotAsync, () => !IsBusy);
        RecallSlotCommand = new AsyncRelayCommand(RecallSlotAsync, () => !IsBusy);
        ExportSettingsCommand = new AsyncRelayCommand(ExportSettingsAsync, () => !IsBusy);
        ImportSettingsCommand = new AsyncRelayCommand(ImportSettingsAsync, () => !IsBusy);
        ResetCommand = new AsyncRelayCommand(ResetAsync, () => !IsBusy);
        CopyLogCommand = new AsyncRelayCommand(() => Dialogs.CopyToClipboardAsync(_log.ExportText()));
        SaveLogCommand = new AsyncRelayCommand(SaveLogAsync);

        // The status bar summarises the trigger and burst settings, like the instrument's own display.
        Trigger.Source.ValueChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TriggerSummary));
            OnTriggerSourceRead();
        };
        Trigger.Rate.ValueChanged += (_, _) => OnPropertyChanged(nameof(TriggerSummary));
        Status.InstrumentStatusRead += (_, status) => OnInstrumentStatusRead(status);
        Burst.Enabled.ValueChanged += (_, _) => OnPropertyChanged(nameof(BurstSummary));
        Burst.Count.ValueChanged += (_, _) => OnPropertyChanged(nameof(BurstSummary));
        Burst.Period.ValueChanged += (_, _) => OnPropertyChanged(nameof(BurstSummary));

        // The timing diagram repeats the pulses for each delay cycle while burst mode is on.
        Burst.Enabled.ValueChanged += (_, _) => RecomputeBurstTiming();
        Burst.Count.ValueChanged += (_, _) => RecomputeBurstTiming();
        Burst.Period.ValueChanged += (_, _) => RecomputeBurstTiming();
        Burst.Delay.ValueChanged += (_, _) => RecomputeBurstTiming();
        Burst.T0Mode.ValueChanged += (_, _) => RecomputeBurstTiming();
    }

    public event EventHandler<string>? DisconnectRequested;

    /// <summary>The saved instrument this session belongs to (its saved setup and connection settings).</summary>
    public InstrumentItemViewModel? Owner { get; internal set; }

    public InstrumentProfile Profile { get; }

    public Dg645Client Client { get; }

    public IDialogService Dialogs { get; }

    public LogViewModel Log => _log;

    public string Identity => Client.Identity ?? string.Empty;

    public string Model => IdentityPart(1);

    public string SerialNumber => IdentityPart(2);

    public string Firmware => IdentityPart(3);

    public IReadOnlyList<ChannelDelayViewModel> Channels { get; }

    public IReadOnlyList<OutputViewModel> Outputs { get; }

    public TriggerViewModel Trigger { get; }

    public BurstViewModel Burst { get; }

    public StatusViewModel Status { get; }

    public InterfaceViewModel Interface { get; }

    public TerminalViewModel Terminal { get; }

    public IReadOnlyList<PulseTiming> Timing
    {
        get => _timing;
        private set => SetProperty(ref _timing, value);
    }

    /// <summary>The burst the timing diagram draws, or null when burst mode is off or not yet read.</summary>
    public BurstTiming? TimingBurst
    {
        get => _timingBurst;
        private set
        {
            if (SetProperty(ref _timingBurst, value))
            {
                OnPropertyChanged(nameof(TimingCaption));
            }
        }
    }

    public string TimingCaption => TimingBurst is null
        ? "Outputs relative to T0. Negative-polarity outputs are drawn inverted. Hover for exact edges."
        : "Burst mode: outputs relative to the trigger, repeated for each delay cycle. Negative-polarity outputs are drawn inverted. Hover for exact edges.";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                foreach (var command in new[] { RefreshAllCommand, SaveToSlotCommand, RecallSlotCommand, ExportSettingsCommand, ImportSettingsCommand, ResetCommand })
                {
                    (command as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
                }
            }
        }
    }

    public string? BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>True after settings were changed from the terminal, until the next Refresh all.</summary>
    public bool SettingsMayBeStale
    {
        get => _settingsMayBeStale;
        private set => SetProperty(ref _settingsMayBeStale, value);
    }

    /// <summary>True once a setting has been changed since the setup was last saved to (or restored from) the configuration.</summary>
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }

    /// <summary>e.g. "Internal, 10 kHz" or "Single shot".</summary>
    public string TriggerSummary => Trigger.Source.Selected is not { } source
        ? "—"
        : Trigger.Source.SelectedValue == (int)TriggerSource.Internal && Trigger.Rate.HasValue
            ? $"{source.Label}, {Trigger.Rate.Text}"
            : source.Label;

    /// <summary>
    /// True from arming a single-shot external trigger (*TRG) until the delay cycle that the next external edge
    /// starts has run. While armed, the status register is read in the background even if Live status is off.
    /// </summary>
    public bool IsArmed
    {
        get => _isArmed;
        private set
        {
            if (SetProperty(ref _isArmed, value))
            {
                OnTriggerActionChanged();
                UpdateHeartbeat();
            }
        }
    }

    /// <summary>The command bar's trigger button: "Trigger", or "Arm" and then "Ready" in the single-shot external modes.</summary>
    public string TriggerActionText => !Trigger.IsExternalSingleShot ? "Trigger" : _isArmed ? "Ready" : "Arm";

    /// <summary>The Trigger page's button: "Trigger now", "Arm" or "Ready".</summary>
    public string TriggerNowText => Trigger.IsExternalSingleShot ? TriggerActionText : "Trigger now";

    /// <summary>The Instrument menu entry for Ctrl+T.</summary>
    public string TriggerMenuText => !Trigger.IsExternalSingleShot ? "Single-shot _trigger" : _isArmed ? "_Arm again" : "_Arm external trigger";

    /// <summary>What the trigger button does at the moment, for its tooltip and the Trigger page.</summary>
    public string TriggerActionHint => Trigger.Source.SelectedValue switch
    {
        (int)TriggerSource.SingleShot => "Starts one delay cycle (*TRG, Ctrl+T).",
        (int)TriggerSource.SingleShotExternalRising or (int)TriggerSource.SingleShotExternalFalling when _isArmed =>
            $"Ready: the next {TriggerEdge} edge at the trigger input starts one delay cycle. Click to arm again (*TRG, Ctrl+T).",
        (int)TriggerSource.SingleShotExternalRising or (int)TriggerSource.SingleShotExternalFalling =>
            $"Arms the instrument, so that the next {TriggerEdge} edge at the trigger input starts one delay cycle (*TRG, Ctrl+T).",
        _ => "Available when the trigger source is a single-shot mode.",
    };

    private string TriggerEdge => Trigger.Source.SelectedValue == (int)TriggerSource.SingleShotExternalFalling ? "falling" : "rising";

    /// <summary>The reason for the changes being made, recorded with each of them in the audit trail.</summary>
    public string ChangeReason
    {
        get => _changeReason;
        set => SetProperty(ref _changeReason, value ?? string.Empty);
    }

    /// <summary>e.g. "Off" or "10 × 1 µs".</summary>
    public string BurstSummary => !Burst.Enabled.IsChecked
        ? "Off"
        : Burst.Count.HasValue && Burst.Period.HasValue ? $"{Burst.Count.Text} × {Burst.Period.Text}" : "On";

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    /// <summary>Missed heartbeats in a row before the connection counts as lost.</summary>
    public const int MissedBeatsBeforeLost = 3;

    /// <summary>
    /// How often the heartbeat asks the instrument for its status (INSR?) while connected. Each reply shows the
    /// connection is alive, and carries the fault bits (PLL and rubidium unlock) that raise alerts.
    /// </summary>
    public double HeartbeatIntervalSeconds
    {
        get => _heartbeatIntervalSeconds;
        set
        {
            if (SetProperty(ref _heartbeatIntervalSeconds, Math.Clamp(value, 0.2, 60)) && _heartbeatCancellation is not null)
            {
                StopHeartbeatLoop();
                UpdateHeartbeat();
            }
        }
    }

    /// <summary>True while the heartbeat (or an armed single shot) is reading the instrument in the background.</summary>
    public bool IsHeartbeatRunning => _heartbeatCancellation is not null;

    /// <summary>False from the first missed heartbeat until the instrument answers again.</summary>
    public bool IsResponding => _missedBeats == 0;

    /// <summary>Round trip of the last heartbeat, e.g. "4 ms"; or how long the instrument has been silent.</summary>
    public string HeartbeatText => _missedBeats > 0
        ? $"No reply for {SilenceSeconds} s"
        : _latency is { } latency ? (latency.TotalMilliseconds < 1 ? "<1 ms" : $"{latency.TotalMilliseconds:0} ms") : string.Empty;

    public string HeartbeatTip => _missedBeats > 0
        ? $"The instrument has not answered the heartbeat since {_lastReply:HH:mm:ss}. After {MissedBeatsBeforeLost} missed replies in a row Tempo reports the connection as lost."
        : _lastReply is { } at
            ? $"Heartbeat: the instrument answered at {at:HH:mm:ss}, in {HeartbeatText}. Tempo checks it every {_heartbeatIntervalSeconds:0.#} s and alerts you if it stops answering or reports a fault."
            : "Tempo checks the instrument every second while connected and alerts you if it stops answering or reports a fault.";

    private int SilenceSeconds => _lastReply is { } at ? (int)Math.Round((DateTime.Now - at).TotalSeconds) : 0;

    /// <summary>A fault or connection problem to show in the alert bar.</summary>
    public event EventHandler<InstrumentAlert>? AlertRaised;

    /// <summary>A problem that has cleared by itself (identified by its <see cref="InstrumentAlert.Kind"/>).</summary>
    public event EventHandler<string>? AlertResolved;

    public IReadOnlyList<int> SaveSlots { get; } = Enumerable.Range(1, 9).ToList();

    public int SaveSlot
    {
        get => _saveSlot;
        set => SetProperty(ref _saveSlot, value);
    }

    public IReadOnlyList<ChoiceOption> RecallSlots { get; }

    public ChoiceOption? RecallSlot
    {
        get => _recallSlot;
        set => SetProperty(ref _recallSlot, value);
    }

    public ICommand RefreshAllCommand { get; }

    public ICommand TriggerCommand { get; }

    public ICommand SaveToSlotCommand { get; }

    public ICommand RecallSlotCommand { get; }

    public ICommand ExportSettingsCommand { get; }

    public ICommand ImportSettingsCommand { get; }

    public ICommand ResetCommand { get; }

    public ICommand CopyLogCommand { get; }

    public ICommand SaveLogCommand { get; }

    /// <summary>Reads everything from the instrument and starts the heartbeat.</summary>
    public async Task InitializeAsync()
    {
        await RefreshAllAsync();
        StartHeartbeat();
    }

    /// <summary>Starts checking the connection and the instrument's fault bits in the background.</summary>
    public void StartHeartbeat()
    {
        _heartbeatOn = true;
        UpdateHeartbeat();
    }

    public async Task RefreshAllAsync()
    {
        await BusyAsync("Reading settings…", async () =>
        {
            var sections = new (string Text, IEnumerable<ParameterViewModel> Items)[]
            {
                ("Reading delays…", Channels),
                ("Reading outputs…", Outputs),
                ("Reading trigger and burst…", [Trigger, Burst]),
                ("Reading status…", [Status]),
                ("Reading interfaces…", [Interface]),
            };

            foreach (var (text, items) in sections)
            {
                BusyText = text;
                foreach (var item in items)
                {
                    // Stop early rather than logging a failure for every remaining setting.
                    if (_disconnectRequested)
                    {
                        return;
                    }

                    await item.RefreshAsync();
                }
            }
        });

        RecomputeTiming();
        if (!_disconnectRequested)
        {
            SettingsMayBeStale = false;
        }

        if (!_statusIsError)
        {
            var missing = Client.UnsupportedCommands;
            SetStatus(missing.Count == 0
                ? $"Settings read at {DateTime.Now:HH:mm:ss}."
                : $"Settings read at {DateTime.Now:HH:mm:ss}. This instrument's firmware does not have {string.Join(", ", missing)}; those settings are greyed out.");
        }
    }

    /// <summary>Stops all background reading, e.g. on disconnecting.</summary>
    public void StopHeartbeat()
    {
        _heartbeatOn = false;
        IsArmed = false;
        StopHeartbeatLoop();
    }

    // ---- IInstrumentContext ----

    public void ReportError(string context, Exception exception)
    {
        if (exception is Dg645Exception dg)
        {
            Status.AddError(dg.ErrorCode, dg.Command);
        }

        var message = $"{context}: {ParameterViewModel.DescribeError(exception)}";
        _log.Add(CommunicationDirection.Error, message);
        SetStatus(message, isError: true);

        if (IsConnectionFailure(exception))
        {
            RequestDisconnect($"Lost connection to the instrument: {exception.Message}");
        }
    }

    public void NotifySettingsChanged()
    {
        RecomputeTiming();
        HasUnsavedChanges = true;
        SetStatus($"Change applied at {DateTime.Now:HH:mm:ss}.");
    }

    public void NotifyExternalChange()
    {
        SettingsMayBeStale = true;
        HasUnsavedChanges = true;
    }

    public IDisposable BeginChange() => Client.BeginAuditedChange();

    public void Audit(string action, string? setting = null, string? oldValue = null, string? newValue = null, string? details = null) =>
        _audit?.Record(Profile, SerialNumber.Length > 0 ? SerialNumber : null, action, setting, oldValue, newValue, _changeReason, details);

    // ---- Channel labels ----

    /// <summary>Keeps a new channel label with the instrument in the configuration, and records it in the audit trail.</summary>
    private void OnChannelLabelCommitted(object? sender, (string Before, string After) change)
    {
        if (sender is not ChannelDelayViewModel channel)
        {
            return;
        }

        // The saved instrument, which an edit (Instrument ▸ Edit) replaces while connected.
        var profile = Owner?.Profile ?? Profile;
        if (!profile.SetLabel(channel.Channel, change.After))
        {
            return;
        }

        Owner?.SaveProfile();
        RecomputeTiming();
        Audit(AuditActions.ChannelLabelled, $"Channel {channel.Channel} label", Blank(change.Before), Blank(change.After),
            "Saved in the configuration");
        SetStatus(change.After.Length == 0
            ? $"Removed the label of channel {channel.Channel}."
            : $"Labelled channel {channel.Channel} \"{change.After}\".");

        static string Blank(string label) => label.Length == 0 ? "(none)" : label;
    }

    // ---- Saved setups ----

    /// <summary>Reads the instrument's complete setup, or returns null (with the error reported) if it could not be read.</summary>
    public async Task<Dg645Settings?> CaptureSetupAsync()
    {
        Dg645Settings? captured = null;
        await BusyAsync("Reading setup…", async () =>
        {
            captured = await Client.ReadSettingsAsync();
            captured.Description = $"Saved from {Profile.Name}";
        });
        return captured;
    }

    /// <summary>
    /// Writes a complete setup to the instrument and reads every page back. Each setting it changes is recorded in the
    /// audit trail; <paramref name="before"/> is the instrument's setup beforehand, if it has just been read.
    /// Returns true if it was applied.
    /// </summary>
    public async Task<bool> ApplySetupAsync(Dg645Settings settings, string description, Dg645Settings? before = null)
    {
        IsArmed = false;
        var applied = await ReplaceSettingsAsync("Applying setup…", AuditActions.SetupApplied, $"Applied {description}",
            () => Client.ApplySettingsAsync(settings, new Progress<string>(step => BusyText = $"Applying {step.ToLowerInvariant()}…")),
            before);

        await RefreshAllAsync();
        if (applied)
        {
            HasUnsavedChanges = false;
            SetStatus($"Applied {description}.");
        }

        return applied;
    }

    /// <summary>Called when the instrument keeps settings that differ from the setup stored in the configuration.</summary>
    public void MarkInstrumentDiffers()
    {
        HasUnsavedChanges = true;
        SetStatus("Using the settings on the instrument, which differ from the setup saved in Tempo.");
    }

    /// <summary>Called once the current setup has been stored in the configuration.</summary>
    public void MarkSetupSaved(string message)
    {
        HasUnsavedChanges = false;
        SetStatus(message);
    }

    public void RequestDisconnect(string reason)
    {
        if (_disconnectRequested)
        {
            return;
        }

        _disconnectRequested = true;
        StopHeartbeat();
        DisconnectRequested?.Invoke(this, reason);
    }

    // ---- Timing ----

    private void RecomputeBurstTiming() =>
        TimingBurst = Burst.Enabled.IsChecked && Burst.Count is { HasValue: true, Value: >= 1 } && Burst.Period.HasValue && Burst.Delay.HasValue
            ? new BurstTiming(Burst.Count.Value, Burst.Period.Value, Burst.Delay.Value,
                Burst.T0Mode.SelectedValue != (int)BurstT0Mode.FirstDelayCycleOnly)
            : null;

    public void RecomputeTiming()
    {
        var absolute = new Dictionary<Dg645Channel, double?>();
        foreach (var channel in Channels)
        {
            absolute[channel.Channel] = Resolve(channel.Channel, []);
            channel.AbsoluteSeconds = absolute[channel.Channel];
        }

        var pulses = new List<PulseTiming>();
        foreach (var output in Outputs.Where(o => o.Output != Dg645Output.T0))
        {
            var name = output.Output.ToString();
            var start = Enum.Parse<Dg645Channel>(name[..1]);
            var end = Enum.Parse<Dg645Channel>(name[1..]);
            pulses.Add(new PulseTiming(name, start.ToString(), end.ToString(), absolute[start], absolute[end], output.IsPositive)
            {
                StartLabel = Channels.First(c => c.Channel == start).ChannelLabel,
                EndLabel = Channels.First(c => c.Channel == end).ChannelLabel,
            });
        }

        Timing = pulses;

        foreach (var output in Outputs)
        {
            var pulse = pulses.FirstOrDefault(p => p.Output == output.Output.ToString());
            output.TimingText = pulse is null
                ? "Fires at the start of every delay cycle."
                : pulse.Start is null || pulse.End is null
                    ? "Pulse timing cannot be resolved from the channel references."
                    : pulse.End < pulse.Start
                        ? $"No pulse: {pulse.EndChannel} is set before {pulse.StartChannel}."
                        : $"Pulse T0 + {EngineeringUnits.Format(pulse.Start.Value, Quantity.Time)} → T0 + {EngineeringUnits.Format(pulse.End.Value, Quantity.Time)}, {EngineeringUnits.Format(pulse.Width, Quantity.Time)} wide";
        }

        double? Resolve(Dg645Channel channel, HashSet<Dg645Channel> visiting)
        {
            if (channel is Dg645Channel.T0 or Dg645Channel.T1)
            {
                return 0;
            }

            var vm = Channels.FirstOrDefault(c => c.Channel == channel);
            if (vm?.Reference is null || !visiting.Add(channel))
            {
                return null;
            }

            var reference = Resolve(vm.ReferenceChannel, visiting);
            return reference is null ? null : reference + vm.DelaySeconds;
        }
    }

    // ---- Commands ----

    /// <summary>
    /// *TRG. In single-shot mode it starts a delay cycle; in the single-shot external modes it arms the instrument,
    /// and the button shows Ready until the next external edge has started the cycle.
    /// </summary>
    private async Task TriggerOrArmAsync()
    {
        using var change = BeginChange();
        if (!Trigger.IsExternalSingleShot)
        {
            if (await RunAsync("Trigger", () => Client.TriggerAsync()))
            {
                Audit(AuditActions.Triggered, details: "One delay cycle started (*TRG)");
                SetStatus($"Triggered at {DateTime.Now:HH:mm:ss}.");
            }

            return;
        }

        var source = Trigger.Source.SelectedValue;
        var armed = await RunAsync("Arm", async () =>
        {
            // Read (and so clear) the status register first, so only a delay cycle started after arming ends Ready.
            await Status.PollAsync();
            _armedSource = source;
            IsArmed = true;
            await Client.TriggerAsync();
        });

        if (armed)
        {
            Audit(AuditActions.Armed, details: $"Armed for one delay cycle on the next {TriggerEdge} edge at the trigger input (*TRG)");
            SetStatus($"Armed at {DateTime.Now:HH:mm:ss}: waiting for a {TriggerEdge} edge at the trigger input.");
        }
        else
        {
            IsArmed = false;
        }
    }

    private void OnInstrumentStatusRead(InstrumentStatusFlags status)
    {
        if (status.HasFlag(InstrumentStatusFlags.PllUnlock))
        {
            RaiseFault(new InstrumentAlert(InstrumentAlert.PllUnlocked, AlertSeverity.Error, "Timebase PLL unlocked",
                "The DG645 reports that its 100 MHz phase-locked loop came unlocked (PLL_UNLOCK), so output timing may have been wrong. " +
                "Check the timebase, and the external 10 MHz reference if one is connected."));
        }

        if (status.HasFlag(InstrumentStatusFlags.RbUnlock))
        {
            RaiseFault(new InstrumentAlert(InstrumentAlert.RubidiumUnlocked, AlertSeverity.Error, "Rubidium timebase unlocked",
                "The DG645 reports that its rubidium timebase is unlocked (RB_UNLOCK), so its timing is not referenced to the rubidium standard. " +
                "It can take several minutes to lock after power-on."));
        }

        // End of delay (or burst), not TRIG: the cycle the external edge started has run, so the shot is spent.
        if (_isArmed && (status & (InstrumentStatusFlags.EndOfDelay | InstrumentStatusFlags.EndOfBurst)) != 0)
        {
            IsArmed = false;
            SetStatus($"Triggered at {DateTime.Now:HH:mm:ss}. Arm again for the next shot.");
        }
    }

    private void OnTriggerSourceRead()
    {
        // Choosing another source cancels an armed single shot. (The source is read again after every change.)
        if (_isArmed && Trigger.Source.SelectedValue != _armedSource)
        {
            IsArmed = false;
        }

        OnTriggerActionChanged();
    }

    private void OnTriggerActionChanged()
    {
        OnPropertyChanged(nameof(TriggerActionText));
        OnPropertyChanged(nameof(TriggerNowText));
        OnPropertyChanged(nameof(TriggerMenuText));
        OnPropertyChanged(nameof(TriggerActionHint));
    }

    private async Task SaveToSlotAsync()
    {
        var slot = SaveSlot;
        if (!await Dialogs.ConfirmAsync("Save to instrument memory", $"Overwrite location {slot} in the DG645's memory with the current settings?", "Save"))
        {
            return;
        }

        bool saved;
        using (BeginChange())
        {
            saved = await RunAsync($"Save to location {slot}", () => Client.SaveSettingsAsync(slot));
        }

        if (saved)
        {
            Audit(AuditActions.SavedToMemory, $"Location {slot}", details: "The instrument's settings were stored in its own memory (*SAV)");
            SetStatus($"Settings saved to instrument location {slot}.");
        }
    }

    private async Task RecallSlotAsync()
    {
        if (RecallSlot is not { } slot)
        {
            return;
        }

        var question = slot.Value == 0
            ? "Recall the factory default settings? All current settings will be replaced."
            : $"Recall settings from location {slot.Value}? All current settings will be replaced.";
        if (!await Dialogs.ConfirmAsync("Recall settings", question, "Recall"))
        {
            return;
        }

        if (await ReplaceSettingsAsync("Recalling settings…", AuditActions.Recalled, $"Recalled {slot.Label.ToLowerInvariant()} (*RCL {slot.Value})",
                () => Client.RecallSettingsAsync(slot.Value)))
        {
            IsArmed = false;
            await RefreshAllAsync();
            HasUnsavedChanges = true;
            SetStatus($"Recalled {slot.Label.ToLowerInvariant()}.");
        }
    }

    private async Task ResetAsync()
    {
        if (!await Dialogs.ConfirmAsync("Reset instrument", "Reset the DG645 to its default settings (*RST)? All current delays, levels and trigger settings will be lost.", "Reset"))
        {
            return;
        }

        if (await ReplaceSettingsAsync("Resetting…", AuditActions.Reset, "Reset to the default settings (*RST)", () => Client.ResetAsync()))
        {
            IsArmed = false;
            await RefreshAllAsync();
            HasUnsavedChanges = true;
            SetStatus("Instrument reset to default settings.");
        }
    }

    private async Task ExportSettingsAsync()
    {
        var safeName = string.Concat(Profile.Name.Split(Path.GetInvalidFileNameChars()));
        var path = await Dialogs.PickSaveFileAsync("Export setup", $"{safeName} {DateTime.Now:yyyy-MM-dd HHmm}{Dg645Settings.FileExtension}", SetupFilter);
        if (path is null)
        {
            return;
        }

        await BusyAsync("Reading settings…", async () =>
        {
            var settings = await Client.ReadSettingsAsync();
            settings.Description = $"Saved from {Profile.Name}";
            await settings.SaveAsync(path);
            SetStatus($"Setup exported to {Path.GetFileName(path)}.");
        });
    }

    private async Task ImportSettingsAsync()
    {
        var path = await Dialogs.PickOpenFileAsync("Import setup", SetupFilter, FileFilter.AllFiles);
        if (path is null)
        {
            return;
        }

        Dg645Settings settings;
        try
        {
            settings = await Dg645Settings.LoadAsync(path);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync("Import setup", $"Could not read {Path.GetFileName(path)}:\n{ex.Message}");
            return;
        }

        var source = settings.SourceIdentity is null ? string.Empty : $"\n\nSaved from: {settings.SourceIdentity}\nSaved: {settings.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        if (!await Dialogs.ConfirmAsync("Import setup", $"Apply the setup in {Path.GetFileName(path)} to {Profile.Name}? Current delays, levels, trigger and burst settings will be replaced.{source}", "Apply"))
        {
            return;
        }

        if (await ApplySetupAsync(settings, Path.GetFileName(path)))
        {
            // The imported setup is not yet the one stored in the configuration.
            HasUnsavedChanges = true;
        }
    }

    private async Task SaveLogAsync()
    {
        var path = await Dialogs.PickSaveFileAsync("Save communication log", $"{Profile.Name} log {DateTime.Now:yyyy-MM-dd HHmm}.txt", new FileFilter("Text files", "*.txt"));
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, _log.ExportText());
        }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync("Save log", ex.Message);
        }
    }

    // ---- Helpers ----

    /// <summary>
    /// Runs an operation that replaces many settings at once (applying a setup, recalling, resetting). The setup is read
    /// before and after, and each setting that changed is recorded in the audit trail, also when the operation fails
    /// part-way. Returns true if the operation succeeded.
    /// </summary>
    private async Task<bool> ReplaceSettingsAsync(string busyText, string action, string details, Func<Task> operation, Dg645Settings? before = null)
    {
        Exception? failure = null;
        Dg645Settings? after = null;
        await BusyAsync(busyText, async () =>
        {
            using var change = BeginChange();
            before ??= await Client.ReadSettingsAsync();
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            try
            {
                after = await Client.ReadSettingsAsync();
            }
            catch when (failure is not null)
            {
                // Report the operation's own failure.
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Throw(failure);
            }
        });

        if (before is not null)
        {
            if (failure is not null)
            {
                details += $". Failed part-way: {ParameterViewModel.DescribeError(failure)}";
            }

            var changes = after is null ? [] : before.Compare(after);
            if (changes.Count == 0)
            {
                Audit(action, details: after is null ? details : details + ". No settings changed");
            }

            foreach (var change in changes)
            {
                Audit(action, change.Setting, change.Before, change.After, details);
            }
        }

        return failure is null && before is not null;
    }

    private async Task<bool> RunAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            ReportError(what, ex);
            return false;
        }
    }

    private async Task BusyAsync(string text, Func<Task> action)
    {
        IsBusy = true;
        BusyText = text;
        StatusIsError = false;
        try
        {
            await RunAsync(text.TrimEnd('…'), action);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    private void RaiseFault(InstrumentAlert alert)
    {
        Audit(AuditActions.Fault, details: $"{alert.Title}: {alert.Message}");
        SetStatus($"{alert.Title} at {DateTime.Now:HH:mm:ss}.", isError: true);
        AlertRaised?.Invoke(this, alert);
    }

    // The heartbeat runs while connected, and while a single shot is armed (so its shot is noticed in tests too).
    private void UpdateHeartbeat()
    {
        if (!(_heartbeatOn || _isArmed) || _disconnectRequested)
        {
            StopHeartbeatLoop();
            return;
        }

        if (_heartbeatCancellation is null)
        {
            var cancellation = new CancellationTokenSource();
            _heartbeatCancellation = cancellation;
            OnPropertyChanged(nameof(IsHeartbeatRunning));
            _ = HeartbeatLoopAsync(TimeSpan.FromSeconds(_heartbeatIntervalSeconds), cancellation.Token);
        }
    }

    private void StopHeartbeatLoop()
    {
        if (_heartbeatCancellation is not null)
        {
            _heartbeatCancellation.Cancel();
            _heartbeatCancellation = null;
            OnPropertyChanged(nameof(IsHeartbeatRunning));
        }
    }

    private async Task HeartbeatLoopAsync(TimeSpan interval, CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                // A long operation (applying a setup, a refresh) talks to the instrument itself and reports failures.
                if (_isBusy)
                {
                    continue;
                }

                var started = Stopwatch.GetTimestamp();
                try
                {
                    await Status.PollAsync();
                    var latency = Stopwatch.GetElapsedTime(started);
                    if (++_beats % 10 == 0)
                    {
                        await CheckTimebaseAsync();
                    }

                    OnHeartbeat(latency);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    if (IsConnectionFailure(ex))
                    {
                        RequestDisconnect($"The connection to the instrument was closed: {ParameterViewModel.DescribeError(ex).TrimEnd('.')}.");
                        return;
                    }

                    if (OnMissedHeartbeat())
                    {
                        RequestDisconnect(
                            $"The instrument stopped answering: no reply to {MissedBeatsBeforeLost} heartbeats in a row since {_lastReply:HH:mm:ss}. " +
                            "It may be switched off, or its network cable unplugged.");
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnHeartbeat(TimeSpan latency)
    {
        var missed = _missedBeats;
        _missedBeats = 0;
        _lastReply = DateTime.Now;
        _latency = latency;
        RaiseHeartbeatProperties();
        if (missed > 0)
        {
            Audit(AuditActions.RespondingAgain, details: $"The instrument answered again after {missed} missed heartbeat{(missed == 1 ? string.Empty : "s")}");
            SetStatus($"The instrument is answering again ({DateTime.Now:HH:mm:ss}).");
            AlertResolved?.Invoke(this, InstrumentAlert.NotResponding);
        }
    }

    /// <summary>Returns true when enough heartbeats have been missed in a row to call the connection lost.</summary>
    private bool OnMissedHeartbeat()
    {
        _lastReply ??= DateTime.Now;
        _missedBeats++;
        RaiseHeartbeatProperties();
        if (_missedBeats == 1)
        {
            var message = $"No reply to the heartbeat since {_lastReply:HH:mm:ss}. If this continues, Tempo will report the connection as lost.";
            Audit(AuditActions.NotResponding, details: message);
            SetStatus($"The instrument is not responding ({DateTime.Now:HH:mm:ss}).", isError: true);
            AlertRaised?.Invoke(this, new InstrumentAlert(InstrumentAlert.NotResponding, AlertSeverity.Warning, "Not responding", message));
        }

        return _missedBeats >= MissedBeatsBeforeLost;
    }

    private async Task CheckTimebaseAsync()
    {
        if (await Status.CheckTimebaseAsync() is { } change)
        {
            RaiseFault(new InstrumentAlert(InstrumentAlert.TimebaseChanged, AlertSeverity.Error, "Timebase changed",
                $"The instrument's timebase changed from {change.Before} to {change.After}. " +
                "If an external reference was in use, it may have been disconnected or lost."));
        }
    }

    private void RaiseHeartbeatProperties()
    {
        OnPropertyChanged(nameof(IsResponding));
        OnPropertyChanged(nameof(HeartbeatText));
        OnPropertyChanged(nameof(HeartbeatTip));
    }

    private static bool IsConnectionFailure(Exception exception) =>
        exception is IOException or SocketException or ObjectDisposedException
        || exception.InnerException is IOException or SocketException
        || (exception is InvalidOperationException && exception.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase));

    private string IdentityPart(int index)
    {
        var parts = Identity.Split(',');
        return parts.Length > index ? parts[index].Trim() : string.Empty;
    }
}
