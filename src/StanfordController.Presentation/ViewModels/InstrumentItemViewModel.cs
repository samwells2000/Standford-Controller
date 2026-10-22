using System.Windows.Input;
using StanfordController.Core;
using StanfordController.Core.Audit;
using StanfordController.Core.Profiles;
using StanfordController.Presentation.Mvvm;
using StanfordController.Presentation.Services;

namespace StanfordController.Presentation.ViewModels;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}

/// <summary>A saved instrument in the sidebar, and its connection (if any).</summary>
public sealed class InstrumentItemViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly Action<InstrumentItemViewModel> _profileChanged;
    private readonly AuditLog? _audit;
    private readonly Func<string>? _configurationName;
    private InstrumentProfile _profile;
    private ConnectionState _state;
    private string? _lastError;
    private InstrumentSessionViewModel? _session;
    private bool _connectionLost;

    /// <param name="configurationName">The name of the configuration this instrument is saved in, for the audit trail.</param>
    public InstrumentItemViewModel(InstrumentProfile profile, IDialogService dialogs, Action<InstrumentItemViewModel> profileChanged,
        AuditLog? audit = null, Func<string>? configurationName = null)
    {
        _profile = profile;
        _dialogs = dialogs;
        _profileChanged = profileChanged;
        _audit = audit;
        _configurationName = configurationName;
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => _state is ConnectionState.Disconnected or ConnectionState.Failed);
        DisconnectCommand = new AsyncRelayCommand(() => DisconnectAsync(), () => _state == ConnectionState.Connected);
        SaveSetupCommand = new AsyncRelayCommand(SaveSetupAsync, () => _session is { IsBusy: false });
        RestoreSetupCommand = new AsyncRelayCommand(RestoreSetupAsync, () => _session is { IsBusy: false } && _profile.StoredSetup is not null);
    }

    public InstrumentProfile Profile => _profile;

    public string Name => _profile.Name;

    public string Address => _profile.Address;

    public string? Location => _profile.Location;

    public string? Notes => _profile.Notes;

    public bool IsSimulator => _profile.IsSimulator;

    public string Subtitle => string.IsNullOrWhiteSpace(_profile.Location) ? Address : $"{Address}  ·  {_profile.Location}";

    public string LastSeen => _profile.LastConnectedUtc is { } t
        ? $"Last connected {t.ToLocalTime():yyyy-MM-dd HH:mm}" + (string.IsNullOrEmpty(_profile.LastKnownIdentity) ? string.Empty : $" — {_profile.LastKnownIdentity}")
        : "Never connected";

    /// <summary>Model, serial number and firmware while connected; otherwise when it was last connected.</summary>
    public string Details => _session is { } s
        ? $"{s.Model}  ·  serial {s.SerialNumber}  ·  firmware {s.Firmware}"
        : LastSeen;

    public bool HasStoredSetup => _profile.StoredSetup is not null;

    /// <summary>e.g. "Saved 2026-10-06 14:02" or "Not saved".</summary>
    public string StoredSetupText => _profile.StoredSetup is { } setup
        ? $"Saved {setup.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "Not saved";

    /// <summary>True when a connected instrument has changes not yet saved to the configuration.</summary>
    public bool HasUnsavedChanges => _session?.HasUnsavedChanges == true;

    public LogViewModel Log { get; } = new();

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(IsConnecting));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(StateText));
                (ConnectCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
                (DisconnectCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsConnected => _state == ConnectionState.Connected;

    public bool IsConnecting => _state == ConnectionState.Connecting;

    public bool IsFailed => _state == ConnectionState.Failed;

    /// <summary>The connected session's latest status line, if any.</summary>
    public string? StatusMessage => _session?.StatusMessage;

    public bool StatusIsError => _session?.StatusIsError == true;

    public string StateText => _state switch
    {
        ConnectionState.Connected when IsNotResponding => "Not responding",
        ConnectionState.Connected => "Connected",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Failed when _connectionLost => "Connection lost",
        ConnectionState.Failed => "Connection failed",
        _ => "Not connected",
    };

    /// <summary>Connected, but the instrument has missed a heartbeat.</summary>
    public bool IsNotResponding => _session is { IsResponding: false };

    /// <summary>The heartbeat's round trip, e.g. "4 ms", or how long the instrument has been silent.</summary>
    public string HeartbeatText => _session?.HeartbeatText ?? string.Empty;

    public string? HeartbeatTip => _session?.HeartbeatTip;

    /// <summary>A fault or connection problem for the alert bar.</summary>
    public event EventHandler<InstrumentAlert>? AlertRaised;

    /// <summary>A problem that has cleared by itself, by <see cref="InstrumentAlert.Kind"/>.</summary>
    public event EventHandler<string>? AlertResolved;

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public InstrumentSessionViewModel? Session
    {
        get => _session;
        private set
        {
            if (_session is not null)
            {
                _session.PropertyChanged -= OnSessionPropertyChanged;
                _session.AlertRaised -= OnSessionAlertRaised;
                _session.AlertResolved -= OnSessionAlertResolved;
            }

            if (SetProperty(ref _session, value))
            {
                if (value is not null)
                {
                    value.PropertyChanged += OnSessionPropertyChanged;
                    value.AlertRaised += OnSessionAlertRaised;
                    value.AlertResolved += OnSessionAlertResolved;
                }

                OnHeartbeatChanged();
                OnPropertyChanged(nameof(HasUnsavedChanges));
                OnPropertyChanged(nameof(Details));
                OnPropertyChanged(nameof(StatusMessage));
                OnPropertyChanged(nameof(StatusIsError));
                RaiseSetupCommands();
            }
        }
    }

    public ICommand ConnectCommand { get; }

    public ICommand DisconnectCommand { get; }

    /// <summary>Reads the instrument's complete setup and stores it in the configuration on this computer.</summary>
    public ICommand SaveSetupCommand { get; }

    /// <summary>Writes the setup stored in the configuration back to the instrument (asks first).</summary>
    public ICommand RestoreSetupCommand { get; }

    /// <summary>Saves the configuration after something in <see cref="Profile"/> changed, such as a channel label.</summary>
    internal void SaveProfile() => _profileChanged(this);

    public void UpdateProfile(InstrumentProfile profile)
    {
        _profile = profile;
        OnPropertyChanged(string.Empty);
        RaiseSetupCommands();
    }

    public Task ConnectAsync() => ConnectAsync(inBatch: false);

    /// <summary>
    /// Connects. <paramref name="inBatch"/> is true while connecting several instruments, such as on opening a
    /// configuration; returns false if the user then asked to stop connecting the rest.
    /// </summary>
    public async Task<bool> ConnectAsync(bool inBatch)
    {
        if (_state is ConnectionState.Connecting or ConnectionState.Connected)
        {
            return true;
        }

        _connectionLost = false;
        State = ConnectionState.Connecting;
        LastError = null;
        var client = _profile.CreateClient();
        client.Communication += (_, e) => Log.Add(e.Direction, e.Text);

        try
        {
            await client.ConnectAsync();
        }
        catch (Exception ex)
        {
            await client.DisposeAsync();
            LastError = ex is TimeoutException
                ? ex.Message
                : $"Could not connect to {_profile.Address}: {ex.Message}";
            Log.Add(CommunicationDirection.Error, LastError);
            State = ConnectionState.Failed;
            return true;
        }

        var (identityNote, keepGoing) = await ConfirmSameInstrumentAsync(client, inBatch);
        if (identityNote is null)
        {
            return keepGoing;
        }

        _profile.LastKnownIdentity = client.Identity;
        _profile.LastConnectedUtc = DateTime.UtcNow;
        if (_profile.UnsupportedCommandsIdentity == client.Identity)
        {
            // Known from last time: do not wait for commands this firmware does not have.
            client.AssumeUnsupported(_profile.UnsupportedCommands);
        }

        OnPropertyChanged(nameof(LastSeen));
        _profileChanged(this);

        var session = new InstrumentSessionViewModel(_profile, client, _dialogs, Log, _audit) { Owner = this };
        session.DisconnectRequested += async (_, reason) => await DisconnectAsync(reason);
        session.Audit(AuditActions.Connected, details: client.Identity + identityNote);
        Session = session;
        State = ConnectionState.Connected;
        AlertResolved?.Invoke(this, InstrumentAlert.ConnectionLost);
        await session.InitializeAsync();
        RememberUnsupportedCommands(client);
        await OfferStoredSetupAsync(session);
        return true;
    }

    /// <summary>
    /// If a different DG645 (another serial number) answers at this address than last time, asks whether to use it in
    /// place of the old one, so a saved setup is not put on the wrong unit without asking. Returns a note for the audit
    /// trail (empty when the unit is the same), or null, disconnected, if the user cancelled; then KeepGoing is false
    /// if they also asked to stop connecting the rest of a batch.
    /// </summary>
    private async Task<(string? Note, bool KeepGoing)> ConfirmSameInstrumentAsync(Dg645Client client, bool inBatch)
    {
        var expected = InstrumentProfile.SerialNumberOf(_profile.LastKnownIdentity);
        var found = InstrumentProfile.SerialNumberOf(client.Identity);
        if (expected is null || found is null || string.Equals(expected, found, StringComparison.OrdinalIgnoreCase))
        {
            return (string.Empty, true);
        }

        var setup = _profile.StoredSetup is null ? string.Empty : " Tempo then compares it with the saved setup as usual.";
        var message = $"A different DG645 answered at {_profile.Address}.\n\nSaved for {Name}: serial {expected}\nAnswering now: serial {found}\n\n" +
                      $"Use the new unit in place of the old one?{setup}";
        var answer = inBatch
            ? await _dialogs.AskAsync("Different instrument",
                message + "\n\nSkip leaves this instrument disconnected and goes on to the next; Cancel stops connecting the rest.",
                "Use new unit", "Skip", showCancel: true)
            : await _dialogs.AskAsync("Different instrument", message, "Use new unit", "Cancel", showCancel: false);
        if (answer.Choice == DialogChoice.Yes)
        {
            Log.Add(CommunicationDirection.Info, $"Serial {found} answered instead of {expected}; using it in place of the old unit as {Name}.");
            return ($". A different unit answered (serial {found}, previously {expected}) and was used in place of the old one", true);
        }

        try
        {
            await client.DisconnectAsync(_profile.ReturnToLocalOnDisconnect);
        }
        catch (Exception ex)
        {
            Log.Add(CommunicationDirection.Error, $"Error while disconnecting: {ex.Message}");
        }

        LastError = $"Not connected: serial {found} answered at {_profile.Address}, but {Name} is serial {expected}.";
        Log.Add(CommunicationDirection.Error, LastError);
        State = ConnectionState.Failed;
        return (null, !inBatch || answer.Choice == DialogChoice.No);
    }

    /// <summary>Keeps what this instrument's firmware lacks with its profile, so the next connection need not find out again.</summary>
    private void RememberUnsupportedCommands(Dg645Client client)
    {
        var found = client.UnsupportedCommands.ToArray();
        if (client.Identity is null || (_profile.UnsupportedCommandsIdentity == client.Identity && found.SequenceEqual(_profile.UnsupportedCommands)))
        {
            return;
        }

        _profile.UnsupportedCommands = found;
        _profile.UnsupportedCommandsIdentity = client.Identity;
        _profileChanged(this);
    }

    /// <summary>
    /// Stores the instrument's current setup in the configuration. Returns false if it could not be read.
    /// </summary>
    public async Task<bool> SaveSetupAsync()
    {
        if (_session is not { } session || await session.CaptureSetupAsync() is not { } setup)
        {
            return false;
        }

        var previous = _profile.StoredSetup;
        _profile.StoredSetup = setup;
        _profileChanged(this);
        OnStoredSetupChanged();
        session.MarkSetupSaved($"Setup saved to the configuration at {DateTime.Now:HH:mm:ss}.");

        // Record what the saved setup now says, compared with the one it replaces.
        var details = $"Saved in the configuration {_configurationName?.Invoke() ?? string.Empty}".TrimEnd();
        var changes = previous?.Compare(setup) ?? [];
        if (previous is null)
        {
            session.Audit(AuditActions.SetupSaved, details: details + ", the first saved setup for this instrument");
        }
        else if (changes.Count == 0)
        {
            session.Audit(AuditActions.SetupSaved, details: details + ". It matches the setup saved before");
        }

        foreach (var change in changes)
        {
            session.Audit(AuditActions.SetupSaved, change.Setting, change.Before, change.After, details);
        }

        return true;
    }

    private async Task RestoreSetupAsync()
    {
        if (_session is not { } session || _profile.StoredSetup is not { } setup)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Restore saved setup",
                $"Replace the current settings of {Name} with the setup saved {setup.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}?",
                "Restore"))
        {
            return;
        }

        await session.ApplySetupAsync(setup, $"the setup saved {setup.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
    }

    /// <summary>
    /// After connecting: if a setup is saved for this instrument and the instrument's settings differ from it,
    /// keep the instrument's settings or load the saved setup, asking unless the user chose a default.
    /// </summary>
    private async Task OfferStoredSetupAsync(InstrumentSessionViewModel session)
    {
        if (_profile.StoredSetup is not { } stored)
        {
            return;
        }

        if (await session.CaptureSetupAsync() is not { } current || _session != session)
        {
            return;
        }

        var differences = stored.DescribeDifferences(current);
        if (differences.Count == 0)
        {
            session.MarkSetupSaved("The instrument's settings match the setup saved in Tempo.");
            return;
        }

        var restore = _profile.SetupOnConnect == SetupOnConnect.Restore;
        if (_profile.SetupOnConnect == SetupOnConnect.Ask)
        {
            const int shown = 8;
            var list = string.Join("\n", differences.Take(shown).Select(d => "  •  " + d));
            if (differences.Count > shown)
            {
                list += $"\n  •  and {differences.Count - shown} more";
            }

            var answer = await _dialogs.AskAsync(
                "Saved setup",
                $"The settings on {Name} differ from the setup saved in Tempo on {stored.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}:\n\n{list}\n\n" +
                "Which settings do you want to use?",
                "Load saved setup",
                "Keep instrument settings",
                "Remember my choice for this instrument",
                showCancel: false);
            if (answer.Choice == DialogChoice.Cancel || _session != session)
            {
                session.MarkInstrumentDiffers();
                session.Audit(AuditActions.SetupKept,
                    details: $"On connecting, the instrument's own settings were kept. They differ from the setup saved {stored.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm} in: {string.Join(", ", differences)}");
                return;
            }

            restore = answer.Choice == DialogChoice.Yes;
            if (answer.Remember)
            {
                _profile.SetupOnConnect = restore ? SetupOnConnect.Restore : SetupOnConnect.KeepInstrument;
                _profileChanged(this);
            }
        }

        if (restore)
        {
            await session.ApplySetupAsync(stored, $"the setup saved {stored.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}, on connecting", before: current);
        }
        else
        {
            session.MarkInstrumentDiffers();
            session.Audit(AuditActions.SetupKept,
                details: $"On connecting, the instrument's own settings were kept. They differ from the setup saved {stored.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm} in: {string.Join(", ", differences)}");
        }
    }

    private void OnStoredSetupChanged()
    {
        OnPropertyChanged(nameof(HasStoredSetup));
        OnPropertyChanged(nameof(StoredSetupText));
        RaiseSetupCommands();
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstrumentSessionViewModel.HasUnsavedChanges))
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        else if (e.PropertyName is nameof(InstrumentSessionViewModel.StatusMessage) or nameof(InstrumentSessionViewModel.StatusIsError))
        {
            OnPropertyChanged(e.PropertyName);
        }
        else if (e.PropertyName == nameof(InstrumentSessionViewModel.IsBusy))
        {
            RaiseSetupCommands();
        }
        else if (e.PropertyName is nameof(InstrumentSessionViewModel.IsResponding) or nameof(InstrumentSessionViewModel.HeartbeatText)
                 or nameof(InstrumentSessionViewModel.HeartbeatTip))
        {
            OnHeartbeatChanged();
        }
    }

    private void OnHeartbeatChanged()
    {
        OnPropertyChanged(nameof(IsNotResponding));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HeartbeatText));
        OnPropertyChanged(nameof(HeartbeatTip));
    }

    private void OnSessionAlertRaised(object? sender, InstrumentAlert alert) => AlertRaised?.Invoke(this, alert);

    private void OnSessionAlertResolved(object? sender, string kind) => AlertResolved?.Invoke(this, kind);

    private void RaiseSetupCommands()
    {
        (SaveSetupCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
        (RestoreSetupCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
    }

    /// <param name="failureReason">Set when the connection ended because of a problem rather than by request.</param>
    public async Task DisconnectAsync(string? failureReason = null)
    {
        var session = Session;
        if (session is null)
        {
            return;
        }

        session.StopHeartbeat();
        Session = null;
        RememberUnsupportedCommands(session.Client);
        var returnToLocal = failureReason is null && _profile.ReturnToLocalOnDisconnect;
        try
        {
            using (session.BeginChange())
            {
                await session.Client.DisconnectAsync(returnToLocal);
            }
        }
        catch (Exception ex)
        {
            Log.Add(CommunicationDirection.Error, $"Error while disconnecting: {ex.Message}");
        }

        session.Audit(failureReason is null ? AuditActions.Disconnected : AuditActions.ConnectionLost,
            details: failureReason ?? (returnToLocal ? "Front panel returned to local control" : "Front panel left under remote control"));

        LastError = failureReason;
        _connectionLost = failureReason is not null;
        State = failureReason is null ? ConnectionState.Disconnected : ConnectionState.Failed;
        AlertResolved?.Invoke(this, InstrumentAlert.NotResponding);
        if (failureReason is not null)
        {
            AlertRaised?.Invoke(this, new InstrumentAlert(InstrumentAlert.ConnectionLost, AlertSeverity.Error, "Connection lost",
                $"{failureReason} Reconnect once it is back."));
        }
    }
}
