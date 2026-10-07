using System.Collections.ObjectModel;
using System.Windows.Input;
using StanfordController.Core;
using StanfordController.Core.Audit;
using StanfordController.Core.Protocol;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.ViewModels;

/// <summary>One bit of a status register, with its enable-mask checkbox.</summary>
public sealed class StatusBitViewModel(StatusBitInfo info) : ObservableObject
{
    private bool _isSet;
    private bool _isEnabled;

    public int Bit => info.Bit;

    public string Name => info.Name;

    public string Description => info.Description;

    public bool IsUsed => info.Name != "—";

    /// <summary>Latched: stays set until the indicators are cleared, because reading the register clears it.</summary>
    public bool IsSet
    {
        get => _isSet;
        set => SetProperty(ref _isSet, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
}

public sealed record ErrorEntry(DateTime Timestamp, int Code, string Description, string? Command)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss");

    public string Summary => Command is null ? $"{Code}: {Description}" : $"{Code}: {Description} — {Command}";
}

/// <summary>Status registers, error queue, diagnostics (self test / calibration), timebase and front-panel display.</summary>
public sealed class StatusViewModel : ParameterViewModel
{
    private string _timebase = "—";
    private string? _selfTestResult;
    private string? _calibrationResult;
    private ChoiceOption? _displayMode;
    private ChoiceOption? _displayChannel;
    private IReadOnlyList<ChoiceOption> _displayChannelOptions = [];
    private DateTime? _lastPoll;
    private bool _timebaseUnsupported;

    private static readonly IReadOnlyList<ChoiceOption> ChannelChoices =
        Enum.GetValues<Dg645Channel>().Select(c => new ChoiceOption((int)c, c.ToString())).ToList();

    private static readonly IReadOnlyList<ChoiceOption> OutputChoices =
        Enum.GetValues<Dg645Output>().Select(o => new ChoiceOption((int)o, o.ToString())).ToList();

    public StatusViewModel(IInstrumentContext context)
        : base(context, "Status")
    {
        InstrumentBits = Dg645Names.InstrumentStatusBits.Select(b => new StatusBitViewModel(b)).ToList();
        EventBits = Dg645Names.StandardEventBits.Select(b => new StatusBitViewModel(b)).ToList();
        StatusByteBits = Dg645Names.StatusByteBits.Select(b => new StatusBitViewModel(b)).ToList();
        PowerOnStatusClear = new ToggleParameterViewModel(context, "Clear enable registers at power on",
            ct => Client.GetPowerOnStatusClearAsync(ct), (v, ct) => Client.SetPowerOnStatusClearAsync(v, ct))
        {
            Hint = "*PSC: when set, the enable registers are cleared each time the instrument powers up.",
        };

        DisplayModes = Enum.GetValues<DisplayMode>().Select(m => new ChoiceOption((int)m, Dg645Names.Describe(m))).ToList();

        ReadStatusCommand = new AsyncRelayCommand(() => RunAsync(ReadRegistersAsync));
        ClearIndicatorsCommand = new RelayCommand(ClearIndicators);
        ApplyEnableMasksCommand = new AsyncRelayCommand(() => RunAsync(ApplyEnableMasksAsync));
        ClearStatusCommand = new AsyncRelayCommand(() => RunAsync(ClearStatusAsync));
        ReadErrorsCommand = new AsyncRelayCommand(() => RunAsync(ReadErrorsAsync));
        ClearErrorListCommand = new RelayCommand(Errors.Clear);
        SelfTestCommand = new AsyncRelayCommand(() => RunAsync(SelfTestAsync));
        AutoCalibrateCommand = new AsyncRelayCommand(() => RunAsync(AutoCalibrateAsync));
        ApplyDisplayCommand = new AsyncRelayCommand(() => RunAsync(ApplyDisplayAsync), () => _displayMode is not null && _displayChannel is not null);
    }

    public IReadOnlyList<StatusBitViewModel> InstrumentBits { get; }

    public IReadOnlyList<StatusBitViewModel> EventBits { get; }

    public IReadOnlyList<StatusBitViewModel> StatusByteBits { get; }

    public ToggleParameterViewModel PowerOnStatusClear { get; }

    public ObservableCollection<ErrorEntry> Errors { get; } = [];

    public string Timebase
    {
        get => _timebase;
        private set => SetProperty(ref _timebase, value);
    }

    public string? SelfTestResult
    {
        get => _selfTestResult;
        private set => SetProperty(ref _selfTestResult, value);
    }

    public string? CalibrationResult
    {
        get => _calibrationResult;
        private set => SetProperty(ref _calibrationResult, value);
    }

    public string LastPollText => _lastPoll is { } t ? $"Last read {t:HH:mm:ss}" : "Not read yet";

    public IReadOnlyList<ChoiceOption> DisplayModes { get; }

    public ChoiceOption? DisplayMode
    {
        get => _displayMode;
        set
        {
            if (SetProperty(ref _displayMode, value))
            {
                UpdateDisplayChannelOptions();
                (ApplyDisplayCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<ChoiceOption> DisplayChannelOptions
    {
        get => _displayChannelOptions;
        private set => SetProperty(ref _displayChannelOptions, value);
    }

    public ChoiceOption? DisplayChannel
    {
        get => _displayChannel;
        set
        {
            if (SetProperty(ref _displayChannel, value))
            {
                (ApplyDisplayCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand ReadStatusCommand { get; }

    public ICommand ClearIndicatorsCommand { get; }

    public ICommand ApplyEnableMasksCommand { get; }

    public ICommand ClearStatusCommand { get; }

    public ICommand ReadErrorsCommand { get; }

    public ICommand ClearErrorListCommand { get; }

    public ICommand SelfTestCommand { get; }

    public ICommand AutoCalibrateCommand { get; }

    public ICommand ApplyDisplayCommand { get; }

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        await ReadRegistersAsync();
        Latch(InstrumentBits, (int)await Client.GetInstrumentStatusEnableAsync(), enable: true);
        Latch(EventBits, (int)await Client.GetStandardEventEnableAsync(), enable: true);
        Latch(StatusByteBits, (int)await Client.GetServiceRequestEnableAsync(), enable: true);
        await PowerOnStatusClear.RefreshAsync();
        await ReadDisplayAsync();

        // Ask once per session: firmware without TIMB? would otherwise time out on every refresh.
        if (!_timebaseUnsupported)
        {
            try
            {
                Timebase = Dg645Names.Describe(await Client.GetTimebaseAsync());
            }
            catch (TimeoutException)
            {
                _timebaseUnsupported = true;
                Timebase = "Not reported by this firmware";
            }
        }
    });

    /// <summary>
    /// Raised with the bits each time the instrument status register is read. Reading clears the register,
    /// so this is the only place its events (such as the end of a triggered delay cycle) can be seen.
    /// </summary>
    public event EventHandler<InstrumentStatusFlags>? InstrumentStatusRead;

    /// <summary>Background poll of the instrument status register. Throws on communication failure.</summary>
    public async Task PollAsync()
    {
        InstrumentStatusFlags status;
        using (Client.SuppressTrafficLogging())
        {
            status = await Client.GetInstrumentStatusAsync();
        }

        Latch(InstrumentBits, (int)status, enable: false);
        _lastPoll = DateTime.Now;
        OnPropertyChanged(nameof(LastPollText));
        InstrumentStatusRead?.Invoke(this, status);
    }

    /// <summary>Records an error the instrument reported for one of our commands.</summary>
    public void AddError(int code, string? command)
    {
        Errors.Insert(0, new ErrorEntry(DateTime.Now, code, Dg645Names.DescribeError(code), command));
        while (Errors.Count > 200)
        {
            Errors.RemoveAt(Errors.Count - 1);
        }
    }

    private async Task ReadRegistersAsync()
    {
        await PollAsync();
        Latch(EventBits, (int)await Client.GetStandardEventStatusAsync(), enable: false);
        Latch(StatusByteBits, (int)await Client.GetStatusByteAsync(), enable: false, replace: true);
    }

    private static void Latch(IReadOnlyList<StatusBitViewModel> bits, int value, bool enable, bool replace = false)
    {
        foreach (var bit in bits)
        {
            var set = (value & (1 << bit.Bit)) != 0;
            if (enable)
            {
                bit.IsEnabled = set;
            }
            else if (replace)
            {
                bit.IsSet = set;
            }
            else if (set)
            {
                bit.IsSet = true;
            }
        }
    }

    private static int Mask(IEnumerable<StatusBitViewModel> bits) =>
        bits.Where(b => b.IsEnabled).Aggregate(0, (mask, b) => mask | (1 << b.Bit));

    private void ClearIndicators()
    {
        foreach (var bit in InstrumentBits.Concat(EventBits))
        {
            bit.IsSet = false;
        }
    }

    private async Task ApplyEnableMasksAsync()
    {
        using (Context.BeginChange())
        {
            await Client.SetInstrumentStatusEnableAsync((InstrumentStatusFlags)Mask(InstrumentBits));
            await Client.SetStandardEventEnableAsync((StandardEventFlags)Mask(EventBits));
            await Client.SetServiceRequestEnableAsync((StatusByteFlags)Mask(StatusByteBits));
        }

        Context.Audit(AuditActions.Changed, "Status enable registers",
            newValue: $"INSE {Mask(InstrumentBits)}, *ESE {Mask(EventBits)}, *SRE {Mask(StatusByteBits)}");
    }

    private async Task ClearStatusAsync()
    {
        using (Context.BeginChange())
        {
            await Client.ClearStatusAsync();
        }

        Context.Audit(AuditActions.StatusCleared, details: "Status registers and error queue cleared (*CLS)");
        ClearIndicators();
        await ReadRegistersAsync();
    }

    private async Task ReadErrorsAsync()
    {
        var codes = await Client.ReadErrorQueueAsync();
        foreach (var code in codes)
        {
            AddError(code, null);
        }

        if (codes.Count == 0)
        {
            await Context.Dialogs.ShowInfoAsync("Error queue", "The instrument's error queue is empty.");
        }
    }

    private async Task SelfTestAsync()
    {
        SelfTestResult = "Running self test…";
        var code = await Client.SelfTestAsync();
        SelfTestResult = code == 0 ? $"Passed ({DateTime.Now:HH:mm})" : $"Failed: {Dg645Names.DescribeError(code)} (error {code})";
        Context.Audit(AuditActions.SelfTest, details: code == 0 ? "Passed" : $"Failed: {Dg645Names.DescribeError(code)} (error {code})");
    }

    private async Task AutoCalibrateAsync()
    {
        if (!await Context.Dialogs.ConfirmAsync("Auto calibration",
                "Run the DG645's auto calibration now? Outputs may be disturbed while it runs, which can take some time.", "Calibrate"))
        {
            return;
        }

        CalibrationResult = "Calibrating…";
        var code = await Client.AutoCalibrateAsync();
        CalibrationResult = code == 0 ? $"Passed ({DateTime.Now:HH:mm})" : $"Failed: {Dg645Names.DescribeError(code)} (error {code})";
        Context.Audit(AuditActions.AutoCalibration, details: code == 0 ? "Passed" : $"Failed: {Dg645Names.DescribeError(code)} (error {code})");
    }

    private async Task ReadDisplayAsync()
    {
        var (mode, channel) = await Client.GetDisplayAsync();
        DisplayMode = DisplayModes.FirstOrDefault(m => m.Value == (int)mode);
        DisplayChannel = DisplayChannelOptions.FirstOrDefault(c => c.Value == channel);
    }

    private async Task ApplyDisplayAsync()
    {
        if (_displayMode is null || _displayChannel is null)
        {
            return;
        }

        using (Context.BeginChange())
        {
            await Client.SetDisplayAsync((DisplayMode)_displayMode.Value, _displayChannel.Value);
        }

        Context.Audit(AuditActions.Changed, "Front-panel display", newValue: $"{_displayMode.Label}, {_displayChannel.Label}");
    }

    private void UpdateDisplayChannelOptions()
    {
        var usesOutputs = _displayMode is not null && Dg645Names.DisplayModeUsesOutputs((DisplayMode)_displayMode.Value);
        var options = usesOutputs ? OutputChoices : ChannelChoices;
        if (!ReferenceEquals(options, _displayChannelOptions))
        {
            var previous = _displayChannel?.Value;
            DisplayChannelOptions = options;
            DisplayChannel = options.FirstOrDefault(o => o.Value == previous) ?? options[0];
        }
    }
}
