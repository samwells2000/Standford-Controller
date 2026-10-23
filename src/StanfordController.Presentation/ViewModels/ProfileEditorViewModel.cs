using System.Globalization;
using System.Windows.Input;
using StanfordController.Core.Profiles;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.ViewModels;

/// <summary>The add/edit instrument dialog: friendly name, address and connection options.</summary>
public sealed class ProfileEditorViewModel : ObservableObject
{
    private readonly InstrumentProfile _original;
    private readonly IReadOnlyList<InstrumentProfile> _others;
    private string _name;
    private string _host;
    private string _portText;
    private string _timeoutText;
    private string _location;
    private string _notes;
    private bool _returnToLocal;
    private bool _connectOnStartup;
    private ChoiceOption _setupOnConnect;
    private IReadOnlyList<string> _problems = [];
    private bool _showProblems;
    private string? _testResult;
    private bool? _testSucceeded;

    public ProfileEditorViewModel(InstrumentProfile profile, IEnumerable<InstrumentProfile> others, bool isNew)
    {
        _original = profile;
        _others = others.ToList();
        IsNew = isNew;
        _name = profile.Name;
        _host = profile.Host;
        _portText = profile.Port.ToString(CultureInfo.InvariantCulture);
        _timeoutText = profile.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        _location = profile.Location ?? string.Empty;
        _notes = profile.Notes ?? string.Empty;
        _returnToLocal = profile.ReturnToLocalOnDisconnect;
        _connectOnStartup = profile.ConnectOnStartup;
        _setupOnConnect = SetupOnConnectOptions.First(o => o.Value == (int)profile.SetupOnConnect);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => IsSimulator || !string.IsNullOrWhiteSpace(_host),
            unavailableReason: () => "Enter the instrument's address first.");
    }

    public bool IsNew { get; }

    public string Title => IsNew ? "Add instrument" : $"Edit {_original.Name}";

    public bool IsSimulator => _original.IsSimulator;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Host
    {
        get => _host;
        set
        {
            if (Set(ref _host, value))
            {
                (TestConnectionCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string PortText
    {
        get => _portText;
        set => Set(ref _portText, value);
    }

    public string TimeoutText
    {
        get => _timeoutText;
        set => Set(ref _timeoutText, value);
    }

    public string Location
    {
        get => _location;
        set => Set(ref _location, value);
    }

    public string Notes
    {
        get => _notes;
        set => Set(ref _notes, value);
    }

    public bool ConnectOnStartup
    {
        get => _connectOnStartup;
        set => Set(ref _connectOnStartup, value);
    }

    public static IReadOnlyList<ChoiceOption> SetupOnConnectOptions { get; } =
    [
        new((int)Core.Profiles.SetupOnConnect.Ask, "Ask which settings to use"),
        new((int)Core.Profiles.SetupOnConnect.Restore, "Load the setup saved in Tempo"),
        new((int)Core.Profiles.SetupOnConnect.KeepInstrument, "Keep the settings on the instrument"),
    ];

    /// <summary>What happens on connect when the instrument's settings differ from the saved setup.</summary>
    public ChoiceOption SetupOnConnectChoice
    {
        get => _setupOnConnect;
        set => Set(ref _setupOnConnect, value ?? SetupOnConnectOptions[0]);
    }

    public string StoredSetupText => _original.StoredSetup is { } setup
        ? $"Setup saved {setup.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}."
        : "No setup saved yet. Connect, then use Save setup (Ctrl+S).";

    public bool ReturnToLocalOnDisconnect
    {
        get => _returnToLocal;
        set => Set(ref _returnToLocal, value);
    }

    public IReadOnlyList<string> Problems
    {
        get => _problems;
        private set
        {
            if (SetProperty(ref _problems, value))
            {
                OnPropertyChanged(nameof(ProblemsText));
                OnPropertyChanged(nameof(HasVisibleProblems));
            }
        }
    }

    public string ProblemsText => string.Join(Environment.NewLine, _problems);

    public bool HasVisibleProblems => _showProblems && _problems.Count > 0;

    public string? TestResult
    {
        get => _testResult;
        private set => SetProperty(ref _testResult, value);
    }

    /// <summary>null while untested or testing.</summary>
    public bool? TestSucceeded
    {
        get => _testSucceeded;
        private set => SetProperty(ref _testSucceeded, value);
    }

    public ICommand TestConnectionCommand { get; }

    /// <summary>The edited profile; valid after <see cref="TrySave"/> returns true.</summary>
    public InstrumentProfile Result { get; private set; } = new();

    /// <summary>Validates; on success fills <see cref="Result"/>. On failure the problems become visible.</summary>
    public bool TrySave()
    {
        var candidate = Build();
        Problems = candidate.Validate(_others).Concat(ParseProblems()).ToList();
        _showProblems = true;
        OnPropertyChanged(nameof(HasVisibleProblems));
        if (Problems.Count > 0)
        {
            return false;
        }

        Result = candidate;
        return true;
    }

    private InstrumentProfile Build()
    {
        var profile = _original.Clone();
        profile.Name = _name.Trim();
        profile.Host = _host.Trim();
        profile.Port = int.TryParse(_portText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : profile.Port;
        profile.TimeoutMs = int.TryParse(_timeoutText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) ? timeout : profile.TimeoutMs;
        profile.Location = string.IsNullOrWhiteSpace(_location) ? null : _location.Trim();
        profile.Notes = string.IsNullOrWhiteSpace(_notes) ? null : _notes.Trim();
        profile.ReturnToLocalOnDisconnect = _returnToLocal;
        profile.ConnectOnStartup = _connectOnStartup;
        profile.SetupOnConnect = (SetupOnConnect)_setupOnConnect.Value;
        return profile;
    }

    private IEnumerable<string> ParseProblems()
    {
        if (!IsSimulator && !int.TryParse(_portText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            yield return "The port must be a whole number (the DG645 uses 5025).";
        }

        if (!int.TryParse(_timeoutText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            yield return "The timeout must be a whole number of milliseconds.";
        }
    }

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        if (_showProblems)
        {
            Problems = Build().Validate(_others).Concat(ParseProblems()).ToList();
        }

        return true;
    }

    private async Task TestConnectionAsync()
    {
        var profile = Build();

        // Only the connection details matter for a test; the friendly name may not be filled in yet.
        var probe = profile.Clone();
        probe.Name = "test";
        var problems = probe.Validate([]).Concat(ParseProblems()).ToList();
        if (problems.Count > 0)
        {
            TestSucceeded = false;
            TestResult = problems[0];
            return;
        }

        TestSucceeded = null;
        TestResult = $"Connecting to {profile.Address}…";
        var client = profile.CreateClient();
        try
        {
            var identity = await client.ConnectAsync();
            TestSucceeded = true;
            TestResult = identity.Contains("DG645", StringComparison.OrdinalIgnoreCase)
                ? $"Connected: {identity}"
                : $"Connected, but this does not look like a DG645: {identity}";
            await client.DisconnectAsync(profile.ReturnToLocalOnDisconnect);
        }
        catch (Exception ex)
        {
            TestSucceeded = false;
            TestResult = ex.Message;
            await client.DisposeAsync();
        }
    }
}
