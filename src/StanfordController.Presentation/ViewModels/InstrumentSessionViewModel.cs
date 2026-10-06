using System.IO;
using System.Net.Sockets;
using System.Windows.Input;
using StanfordController.Core;
using StanfordController.Core.Profiles;
using StanfordController.Core.Protocol;
using StanfordController.Presentation.Mvvm;
using StanfordController.Presentation.Services;

namespace StanfordController.Presentation.ViewModels;

/// <summary>One pulse for the timing diagram, in seconds from T0.</summary>
public sealed record PulseTiming(string Output, string StartChannel, string EndChannel, double? Start, double? End, bool Positive)
{
    public bool IsValid => Start is not null && End is not null && End >= Start;

    public double Width => IsValid ? End!.Value - Start!.Value : 0;
}

/// <summary>Everything shown for a connected instrument.</summary>
public sealed class InstrumentSessionViewModel : ObservableObject, IInstrumentContext
{
    private const string SettingsFilter = "DG645 settings (*.dg645.json)|*.dg645.json|All files (*.*)|*.*";

    private readonly LogViewModel _log;
    private CancellationTokenSource? _pollCancellation;
    private bool _isPolling;
    private double _pollIntervalSeconds = 1;
    private int _pollFailures;
    private bool _isBusy;
    private string? _busyText;
    private string? _statusMessage;
    private bool _statusIsError;
    private int _saveSlot = 1;
    private ChoiceOption? _recallSlot;
    private IReadOnlyList<PulseTiming> _timing = [];
    private bool _disconnectRequested;

    public InstrumentSessionViewModel(InstrumentProfile profile, Dg645Client client, IDialogService dialogs, LogViewModel log)
    {
        Profile = profile;
        Client = client;
        Dialogs = dialogs;
        _log = log;

        Channels = Dg645Names.DelayChannels.Select(c => new ChannelDelayViewModel(this, c)).ToList();
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
        TriggerCommand = new AsyncRelayCommand(() => RunAsync("Trigger", () => Client.TriggerAsync()));
        SaveToSlotCommand = new AsyncRelayCommand(SaveToSlotAsync, () => !IsBusy);
        RecallSlotCommand = new AsyncRelayCommand(RecallSlotAsync, () => !IsBusy);
        ExportSettingsCommand = new AsyncRelayCommand(ExportSettingsAsync, () => !IsBusy);
        ImportSettingsCommand = new AsyncRelayCommand(ImportSettingsAsync, () => !IsBusy);
        ResetCommand = new AsyncRelayCommand(ResetAsync, () => !IsBusy);
        CopyLogCommand = new RelayCommand(() => Dialogs.CopyToClipboard(_log.ExportText()));
        SaveLogCommand = new RelayCommand(SaveLog);
    }

    public event EventHandler<string>? DisconnectRequested;

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

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    /// <summary>Poll the instrument status register in the background.</summary>
    public bool IsPolling
    {
        get => _isPolling;
        set
        {
            if (SetProperty(ref _isPolling, value))
            {
                if (value)
                {
                    StartPolling();
                }
                else
                {
                    StopPolling();
                }
            }
        }
    }

    public double PollIntervalSeconds
    {
        get => _pollIntervalSeconds;
        set
        {
            if (SetProperty(ref _pollIntervalSeconds, Math.Clamp(value, 0.2, 60)) && _isPolling)
            {
                StopPolling();
                StartPolling();
            }
        }
    }

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

    /// <summary>Reads everything from the instrument and starts status polling.</summary>
    public async Task InitializeAsync()
    {
        await RefreshAllAsync();
        IsPolling = true;
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
        if (!_statusIsError)
        {
            SetStatus($"Settings read at {DateTime.Now:HH:mm:ss}.");
        }
    }

    public void StopPolling()
    {
        _pollCancellation?.Cancel();
        _pollCancellation = null;
        if (_isPolling)
        {
            _isPolling = false;
            OnPropertyChanged(nameof(IsPolling));
        }
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

    public void NotifySettingsChanged() => RecomputeTiming();

    public void RequestDisconnect(string reason)
    {
        if (_disconnectRequested)
        {
            return;
        }

        _disconnectRequested = true;
        StopPolling();
        DisconnectRequested?.Invoke(this, reason);
    }

    // ---- Timing ----

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
            pulses.Add(new PulseTiming(name, start.ToString(), end.ToString(), absolute[start], absolute[end], output.IsPositive));
        }

        Timing = pulses;

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

    private async Task SaveToSlotAsync()
    {
        var slot = SaveSlot;
        if (!Dialogs.Confirm("Save to instrument memory", $"Overwrite location {slot} in the DG645's memory with the current settings?"))
        {
            return;
        }

        await RunAsync($"Save to location {slot}", () => Client.SaveSettingsAsync(slot));
        if (!_statusIsError)
        {
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
        if (!Dialogs.Confirm("Recall settings", question))
        {
            return;
        }

        if (await RunAsync($"Recall location {slot.Value}", () => Client.RecallSettingsAsync(slot.Value)))
        {
            await RefreshAllAsync();
            SetStatus($"Recalled {slot.Label.ToLowerInvariant()}.");
        }
    }

    private async Task ResetAsync()
    {
        if (!Dialogs.Confirm("Reset instrument", "Reset the DG645 to its default settings (*RST)? All current delays, levels and trigger settings will be lost."))
        {
            return;
        }

        if (await RunAsync("Reset", () => Client.ResetAsync()))
        {
            await RefreshAllAsync();
            SetStatus("Instrument reset to default settings.");
        }
    }

    private async Task ExportSettingsAsync()
    {
        var safeName = string.Concat(Profile.Name.Split(Path.GetInvalidFileNameChars()));
        var path = Dialogs.PickSaveFile("Save DG645 settings", SettingsFilter, $"{safeName} {DateTime.Now:yyyy-MM-dd HHmm}{Dg645Settings.FileExtension}");
        if (path is null)
        {
            return;
        }

        await BusyAsync("Reading settings…", async () =>
        {
            var settings = await Client.ReadSettingsAsync();
            settings.Description = $"Saved from {Profile.Name}";
            await settings.SaveAsync(path);
            SetStatus($"Settings saved to {Path.GetFileName(path)}.");
        });
    }

    private async Task ImportSettingsAsync()
    {
        var path = Dialogs.PickOpenFile("Load DG645 settings", SettingsFilter);
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
            Dialogs.ShowError("Load settings", $"Could not read {Path.GetFileName(path)}:\n{ex.Message}");
            return;
        }

        var source = settings.SourceIdentity is null ? string.Empty : $"\n\nSaved from: {settings.SourceIdentity}\nSaved: {settings.SavedUtc.ToLocalTime():g}";
        if (!Dialogs.Confirm("Load settings", $"Apply the settings in {Path.GetFileName(path)} to {Profile.Name}? Current delays, levels, trigger and burst settings will be replaced.{source}"))
        {
            return;
        }

        var applied = false;
        await BusyAsync("Applying settings…", async () =>
        {
            await Client.ApplySettingsAsync(settings, new Progress<string>(step => BusyText = $"Applying {step.ToLowerInvariant()}…"));
            applied = true;
        });

        await RefreshAllAsync();
        if (applied)
        {
            SetStatus($"Applied {Path.GetFileName(path)}.");
        }
    }

    private void SaveLog()
    {
        var path = Dialogs.PickSaveFile("Save communication log", "Text files (*.txt)|*.txt", $"{Profile.Name} log {DateTime.Now:yyyy-MM-dd HHmm}.txt");
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
            Dialogs.ShowError("Save log", ex.Message);
        }
    }

    // ---- Helpers ----

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

    private void StartPolling()
    {
        _pollCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _pollCancellation = cancellation;
        _ = PollLoopAsync(TimeSpan.FromSeconds(_pollIntervalSeconds), cancellation.Token);
    }

    private async Task PollLoopAsync(TimeSpan interval, CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_isBusy)
                {
                    continue;
                }

                try
                {
                    await Status.PollAsync();
                    _pollFailures = 0;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    _pollFailures++;
                    if (IsConnectionFailure(ex) || _pollFailures >= 3)
                    {
                        RequestDisconnect($"Lost contact with the instrument: {ParameterViewModel.DescribeError(ex)}");
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
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
