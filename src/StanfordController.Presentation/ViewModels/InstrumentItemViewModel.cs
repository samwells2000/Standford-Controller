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
    }

    public InstrumentProfile Profile => _profile;

    public string Name => _profile.Name;

    public string Address => _profile.Address;

    public string? Location => _profile.Location;

    public string? Notes => _profile.Notes;

    public bool IsSimulator => _profile.IsSimulator;

    public string Subtitle => string.IsNullOrWhiteSpace(_profile.Location) ? Address : $"{Address}  ·  {_profile.Location}";

    public string LastSeen => _profile.LastConnectedUtc is { } t
        ? $"Last connected {t.ToLocalTime():g}" + (string.IsNullOrEmpty(_profile.LastKnownIdentity) ? string.Empty : $" — {_profile.LastKnownIdentity}")
        : "Never connected";

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
                OnPropertyChanged(nameof(StateText));
                (ConnectCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
                (DisconnectCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsConnected => _state == ConnectionState.Connected;

    public bool IsConnecting => _state == ConnectionState.Connecting;

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
        private set => SetProperty(ref _session, value);
    }

    public ICommand ConnectCommand { get; }

    public ICommand DisconnectCommand { get; }

    public void UpdateProfile(InstrumentProfile profile)
    {
        _profile = profile;
        OnPropertyChanged(string.Empty);
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

        var session = new InstrumentSessionViewModel(_profile, client, _dialogs, Log);
        session.DisconnectRequested += async (_, reason) => await DisconnectAsync(reason);
        Session = session;
        State = ConnectionState.Connected;
        await session.InitializeAsync();
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
