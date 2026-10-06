using System.Windows.Input;
using StanfordController.Core;
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
    private InstrumentProfile _profile;
    private ConnectionState _state;
    private string? _lastError;
    private InstrumentSessionViewModel? _session;

    public InstrumentItemViewModel(InstrumentProfile profile, IDialogService dialogs, Action<InstrumentItemViewModel> profileChanged)
    {
        _profile = profile;
        _dialogs = dialogs;
        _profileChanged = profileChanged;
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
        ConnectionState.Connected => "Connected",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Failed => "Connection failed",
        _ => "Not connected",
    };

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
            }

            if (SetProperty(ref _session, value))
            {
                if (value is not null)
                {
                    value.PropertyChanged += OnSessionPropertyChanged;
                }

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

    public void UpdateProfile(InstrumentProfile profile)
    {
        _profile = profile;
        OnPropertyChanged(string.Empty);
        RaiseSetupCommands();
    }

    public async Task ConnectAsync()
    {
        if (_state is ConnectionState.Connecting or ConnectionState.Connected)
        {
            return;
        }

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
            return;
        }

        _profile.LastKnownIdentity = client.Identity;
        _profile.LastConnectedUtc = DateTime.UtcNow;
        OnPropertyChanged(nameof(LastSeen));
        _profileChanged(this);

        var session = new InstrumentSessionViewModel(_profile, client, _dialogs, Log) { Owner = this };
        session.DisconnectRequested += async (_, reason) => await DisconnectAsync(reason);
        Session = session;
        State = ConnectionState.Connected;
        await session.InitializeAsync();
        await OfferStoredSetupAsync(session);
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

        _profile.StoredSetup = setup;
        _profileChanged(this);
        OnStoredSetupChanged();
        session.MarkSetupSaved($"Setup saved to the configuration at {DateTime.Now:HH:mm:ss}.");
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
            await session.ApplySetupAsync(stored, $"the setup saved {stored.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        }
        else
        {
            session.MarkInstrumentDiffers();
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
    }

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

        session.StopPolling();
        Session = null;
        try
        {
            await session.Client.DisconnectAsync(returnToLocal: failureReason is null && _profile.ReturnToLocalOnDisconnect);
        }
        catch (Exception ex)
        {
            Log.Add(CommunicationDirection.Error, $"Error while disconnecting: {ex.Message}");
        }

        LastError = failureReason;
        State = failureReason is null ? ConnectionState.Disconnected : ConnectionState.Failed;
    }
}
