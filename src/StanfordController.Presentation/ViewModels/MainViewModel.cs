using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using StanfordController.Core.Audit;
using StanfordController.Core.Discovery;
using StanfordController.Core.Profiles;
using StanfordController.Presentation.Mvvm;
using StanfordController.Presentation.Services;

namespace StanfordController.Presentation.ViewModels;

/// <summary>A configuration file in the File ▸ Open recent list.</summary>
public sealed record RecentConfiguration(string Path, string Name, ICommand OpenCommand);

/// <summary>
/// The main window: the open configuration (saved instruments and their setups), the instrument list,
/// and the selected instrument.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    public const string AppName = AppPaths.AppName;

    private const int MaxRecent = 8;

    private static readonly FileFilter ConfigurationFilter = new(ConfigurationStore.FileFilterName, "*" + ConfigurationStore.FileExtension);

    // Opening also lists files saved by release 1.0.14, when the program was called Strobe.
    private static readonly FileFilter OpenConfigurationFilter = new(ConfigurationStore.FileFilterName,
        "*" + ConfigurationStore.FileExtension, "*" + ConfigurationStore.PreviousFileExtension);

    private readonly string _defaultConfigurationPath;
    private readonly IDialogService _dialogs;
    private readonly AppSettingsStore? _settingsStore;
    private ConfigurationStore _store;
    private AppSettings _settings = new();
    private InstrumentItemViewModel? _selected;
    private string? _statusMessage;
    private bool _statusIsError;
    private bool _loading;

    /// <param name="defaultConfigurationPath">The configuration used when none has been opened (Default.tempo).</param>
    public MainViewModel(string defaultConfigurationPath, IDialogService dialogs, AppSettingsStore? settingsStore = null)
    {
        _defaultConfigurationPath = Path.GetFullPath(defaultConfigurationPath);
        _store = new ConfigurationStore(_defaultConfigurationPath);
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        Audit = new AuditLog(Path.Combine(Path.GetDirectoryName(_defaultConfigurationPath) ?? ".", "Audit trail"), $"{AppName} {Help.AppInfo.Version}");
        Alerts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAlerts));
        ChannelOverview = new ChannelOverviewViewModel(Instruments, item => SelectedInstrument = item);
        Instruments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasInstruments));
            OnPropertyChanged(nameof(HasUnsavedChanges));
            OnPropertyChanged(nameof(WindowTitle));
        };

        // Instruments
        AddInstrumentCommand = new AsyncRelayCommand(() => AddFromEditorAsync(new InstrumentProfile()));
        ConnectInstrumentCommand = new RelayCommand<InstrumentItemViewModel>(item => item?.ConnectCommand.Execute(null));
        EditInstrumentCommand = new AsyncRelayCommand<InstrumentItemViewModel>(item => EditInstrumentAsync(item ?? _selected), item => (item ?? _selected) is not null);
        RemoveInstrumentCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => _selected is not null);
        DuplicateInstrumentCommand = new AsyncRelayCommand(DuplicateSelectedAsync, () => _selected is not null);
        DiscoverCommand = new AsyncRelayCommand(DiscoverAsync);
        AddDemoInstrumentCommand = new RelayCommand(AddDemoInstrument);
        ImportInstrumentsCommand = new AsyncRelayCommand(ImportInstrumentsAsync);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => _selected is not null && Instruments.IndexOf(_selected) > 0);
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => _selected is not null && Instruments.IndexOf(_selected) < Instruments.Count - 1);

        // Configuration files
        NewConfigurationCommand = new AsyncRelayCommand(NewConfigurationAsync);
        OpenConfigurationCommand = new AsyncRelayCommand(OpenConfigurationAsync);
        OpenRecentCommand = new AsyncRelayCommand<string>(path => path is null ? Task.CompletedTask : OpenConfigurationAsync(path));
        SaveConfigurationAsCommand = new AsyncRelayCommand(SaveConfigurationAsAsync);
        SaveSetupsCommand = new AsyncRelayCommand(() => SaveAllSetupsAsync());

        // The selected instrument (menu items and keyboard shortcuts)
        ConnectSelectedCommand = new RelayCommand(() => _selected?.ConnectCommand.Execute(null));
        DisconnectSelectedCommand = new RelayCommand(() => _selected?.DisconnectCommand.Execute(null));
        SaveSelectedSetupCommand = ItemCommand(i => i.SaveSetupCommand);
        RestoreSelectedSetupCommand = ItemCommand(i => i.RestoreSetupCommand);
        RefreshCommand = SessionCommand(s => s.RefreshAllCommand);
        TriggerCommand = SessionCommand(s => s.TriggerCommand);
        ExportSetupCommand = SessionCommand(s => s.ExportSettingsCommand);
        ImportSetupCommand = SessionCommand(s => s.ImportSettingsCommand);

        // View and help
        ToggleLogCommand = new RelayCommand(() => ShowLog = !ShowLog);
        SetThemeCommand = new RelayCommand<string>(name =>
        {
            if (Enum.TryParse<AppTheme>(name, out var theme))
            {
                Theme = theme;
            }
        });
        HelpCommand = new RelayCommand<string>(topic => _dialogs.ShowHelp(topic));
        AboutCommand = new AsyncRelayCommand(_dialogs.ShowAboutAsync);
        AuditTrailCommand = new RelayCommand(() => _dialogs.ShowAuditTrail(new AuditTrailViewModel(Audit, _dialogs, SetAuditFolder)));
    }

    /// <summary>The audit trail: every change made to an instrument or to the saved instruments.</summary>
    public AuditLog Audit { get; }

    public ObservableCollection<InstrumentItemViewModel> Instruments { get; } = [];

    public bool HasInstruments => Instruments.Count > 0;

    /// <summary>The All channels page: the channels and labels of every connected instrument.</summary>
    public ChannelOverviewViewModel ChannelOverview { get; }

    public InstrumentItemViewModel? SelectedInstrument
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                RaiseSelectionCommands();
                if (value?.StatusMessage is { } message)
                {
                    StatusMessage = message;
                    StatusIsError = value.StatusIsError;
                }

                if (!_loading)
                {
                    _settings.SelectedInstrumentId = value?.Profile.Id;
                    SaveSettings();
                }
            }
        }
    }

    /// <summary>"Default — Tempo", with an asterisk while setups have unsaved changes.</summary>
    public string WindowTitle => $"{ConfigurationName}{(HasUnsavedChanges ? " *" : string.Empty)} — {AppName}";

    /// <summary>Shown in the About box and status bar, e.g. "Version 1.0.42".</summary>
    public string VersionText { get; init; } = string.Empty;

    /// <summary>True for the portable build, which keeps its files beside the program.</summary>
    public bool IsPortable { get; init; }

    public string ConfigurationPath => _store.FilePath;

    public string ConfigurationName => _store.Name;

    public ObservableCollection<RecentConfiguration> RecentConfigurations { get; } = [];

    public bool HasRecentConfigurations => RecentConfigurations.Count > 0;

    /// <summary>True when any connected instrument has settings that are not saved in the configuration.</summary>
    public bool HasUnsavedChanges => Instruments.Any(i => i.HasUnsavedChanges);

    public AppTheme Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value)
            {
                return;
            }

            _settings.Theme = value;
            RaiseThemeProperties();
            SaveSettings();
        }
    }

    public bool IsSystemTheme => Theme == AppTheme.System;

    public bool IsLightTheme => Theme == AppTheme.Light;

    public bool IsDarkTheme => Theme == AppTheme.Dark;

    /// <summary>Faults and connection problems, newest first, shown in the bar at the top of the window.</summary>
    public ObservableCollection<AlertViewModel> Alerts { get; } = [];

    public bool HasAlerts => Alerts.Count > 0;

    /// <summary>Raised when a new alert appears, so the window can ask for attention.</summary>
    public event EventHandler<AlertViewModel>? AlertRaised;

    /// <summary>Whether a new alert also plays the system's alert sound.</summary>
    public bool AlertSound
    {
        get => _settings.AlertSound;
        set
        {
            if (_settings.AlertSound != value)
            {
                _settings.AlertSound = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    /// <summary>Whether opening a configuration connects all of its instruments, not only those marked to connect on opening.</summary>
    public bool ConnectAllOnOpen
    {
        get => _settings.ConnectAllOnOpen;
        set
        {
            if (_settings.ConnectAllOnOpen != value)
            {
                _settings.ConnectAllOnOpen = value;
                OnPropertyChanged();
                SaveSettings();
            }
        }
    }

    /// <summary>Whether communication logs are shown under the instrument pages (applies to every instrument).</summary>
    public bool ShowLog
    {
        get => _settings.ShowLog;
        set
        {
            if (_settings.ShowLog == value)
            {
                return;
            }

            _settings.ShowLog = value;
            foreach (var item in Instruments)
            {
                item.Log.IsPanelVisible = value;
            }

            OnPropertyChanged();
            SaveSettings();
        }
    }

    /// <summary>The window placement saved last time, if any.</summary>
    public WindowPlacement? WindowPlacement => _settings.Window;

    public void SaveWindowPlacement(WindowPlacement placement)
    {
        _settings.Window = placement;
        SaveSettings();
    }

    /// <summary>The status bar message: the latest from this window or from the selected instrument.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            SetProperty(ref _statusMessage, value);
            StatusIsError = false;
        }
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public ICommand AddInstrumentCommand { get; }

    /// <summary>Connects the given instrument (double-click in the list).</summary>
    public ICommand ConnectInstrumentCommand { get; }

    public ICommand EditInstrumentCommand { get; }

    public ICommand RemoveInstrumentCommand { get; }

    public ICommand DuplicateInstrumentCommand { get; }

    public ICommand DiscoverCommand { get; }

    public ICommand AddDemoInstrumentCommand { get; }

    /// <summary>Adds the instruments from another configuration file to this one.</summary>
    public ICommand ImportInstrumentsCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand NewConfigurationCommand { get; }

    public ICommand OpenConfigurationCommand { get; }

    /// <summary>Parameter: the configuration file path.</summary>
    public ICommand OpenRecentCommand { get; }

    public ICommand SaveConfigurationAsCommand { get; }

    /// <summary>Saves the complete setup of every connected instrument into the configuration (Ctrl+S).</summary>
    public ICommand SaveSetupsCommand { get; }

    public ICommand ConnectSelectedCommand { get; }

    public ICommand DisconnectSelectedCommand { get; }

    public ICommand SaveSelectedSetupCommand { get; }

    public ICommand RestoreSelectedSetupCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand TriggerCommand { get; }

    public ICommand ExportSetupCommand { get; }

    public ICommand ImportSetupCommand { get; }

    public ICommand ToggleLogCommand { get; }

    /// <summary>Parameter: "System", "Light" or "Dark".</summary>
    public ICommand SetThemeCommand { get; }

    /// <summary>Parameter: a user guide heading anchor, or null for the start of the guide.</summary>
    public ICommand HelpCommand { get; }

    public ICommand AboutCommand { get; }

    /// <summary>Opens the audit trail window.</summary>
    public ICommand AuditTrailCommand { get; }

    /// <summary>Reads preferences and opens the configuration used last time (or the default one).</summary>
    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            if (_settingsStore is not null)
            {
                _settings = _settingsStore.Load();
                Audit.SetFolder(_settings.AuditFolder);
                RaiseThemeProperties();
                OnPropertyChanged(nameof(ShowLog));
                RefreshRecent();
            }

            var remembered = _settings.ConfigurationPath;
            var path = remembered is { Length: > 0 } && File.Exists(remembered) ? remembered : _defaultConfigurationPath;
            if (!await LoadConfigurationAsync(path) && path != _defaultConfigurationPath)
            {
                await LoadConfigurationAsync(_defaultConfigurationPath);
            }

            if (remembered is { Length: > 0 } && !File.Exists(remembered))
            {
                StatusMessage = $"{Path.GetFileName(remembered)} could not be found, so the default configuration was opened.";
            }

            SelectedInstrument = Instruments.FirstOrDefault(i => i.Profile.Id == _settings.SelectedInstrumentId) ?? Instruments.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Connects the instruments marked "connect automatically" (or all of them, if the user chose that), one after
    /// another. One that cannot be reached is marked failed and the rest still connect.
    /// </summary>
    public async Task ConnectStartupInstrumentsAsync()
    {
        await ConnectEachAsync(Instruments.Where(i => _settings.ConnectAllOnOpen || i.Profile.ConnectOnStartup).ToList());
    }

    /// <summary>Connects instruments one after another, until the user asks to stop. Returns false if they did.</summary>
    private static async Task<bool> ConnectEachAsync(IReadOnlyList<InstrumentItemViewModel> items)
    {
        foreach (var item in items)
        {
            if (!await item.ConnectAsync(inBatch: true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// After the user opens a configuration: offers to connect all of its instruments when some are not marked to
    /// connect on opening, then connects them. Each saved setup is then checked as each instrument connects.
    /// </summary>
    private async Task ConnectOnOpeningAsync()
    {
        var unmarked = Instruments.Count(i => !i.Profile.ConnectOnStartup);
        if (unmarked == 0 || _settings.ConnectAllOnOpen)
        {
            await ConnectStartupInstrumentsAsync();
            return;
        }

        var marked = Instruments.Count - unmarked;
        var names = string.Join("\n", Instruments.Select(i => "  •  " + i.Name + (i.Profile.ConnectOnStartup ? " (connects on opening)" : string.Empty)));
        var answer = await _dialogs.AskAsync(
            "Connect instruments",
            $"{ConfigurationName}{ConfigurationStore.FileExtension} has {Plural(Instruments.Count, "instrument")}:\n\n{names}\n\n" +
            "Connect to all of them now? As each one connects, Tempo compares it with its saved setup.",
            "Connect all",
            marked == 0 ? "Not now" : $"Only the {Plural(marked, "marked one")}",
            "Always connect every instrument when I open a configuration",
            showCancel: false);
        if (answer.Choice != DialogChoice.Yes)
        {
            await ConnectStartupInstrumentsAsync();
            return;
        }

        if (answer.Remember)
        {
            ConnectAllOnOpen = true;
        }

        if (!await ConnectEachAsync(Instruments.ToList()))
        {
            StatusMessage = $"Stopped connecting the instruments in {Path.GetFileName(_store.FilePath)}.";
            return;
        }

        var failed = Instruments.Where(i => i.State == ConnectionState.Failed).ToList();
        StatusMessage = failed.Count == 0
            ? $"Connected {Plural(Instruments.Count, "instrument")} from {Path.GetFileName(_store.FilePath)}."
            : $"Connected {Instruments.Count - failed.Count} of {Instruments.Count} instruments; not connected: {string.Join(", ", failed.Select(i => i.Name))}.";
    }

    /// <summary>
    /// Before closing: offers to save setups that have unsaved changes. Returns false if the user cancelled.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync() => await OfferToSaveSetupsAsync("closing");

    /// <summary>
    /// Disconnects every instrument (returning front panels to local control) and writes any audit trail records
    /// still waiting, saying so if some could not be written.
    /// </summary>
    public async Task ShutdownAsync()
    {
        foreach (var item in Instruments.Where(i => i.Session is not null).ToList())
        {
            await item.DisconnectAsync();
        }

        var unwritten = await Audit.FlushOnExitAsync();
        if (unwritten > 0)
        {
            await _dialogs.ShowErrorAsync("Audit trail",
                $"{Plural(unwritten, "audit trail record")} could not be written to {Audit.Folder} or {Audit.DefaultFolder}, and will be lost.\n\n{Audit.Problem}");
        }
    }

    public InstrumentItemViewModel AddProfile(InstrumentProfile profile)
    {
        var item = CreateItem(profile);
        Instruments.Add(item);
        Save();
        RecordConfigurationChange(profile, AuditActions.InstrumentAdded, $"Added to {ConfigurationName}: {DescribeProfile(profile)}");
        SelectedInstrument = item;
        return item;
    }

    /// <summary>
    /// Saves the setup of every connected instrument into the configuration. Returns false if any could not be read.
    /// </summary>
    public async Task<bool> SaveAllSetupsAsync()
    {
        var connected = Instruments.Where(i => i.IsConnected).ToList();
        if (connected.Count == 0)
        {
            StatusMessage = "No instruments are connected, so there are no setups to save. Instrument details are saved automatically.";
            return true;
        }

        var saved = 0;
        foreach (var item in connected)
        {
            if (await item.SaveSetupAsync())
            {
                saved++;
            }
        }

        StatusMessage = saved == connected.Count
            ? $"Saved the setup of {Plural(saved, "instrument")} to {ConfigurationName}{ConfigurationStore.FileExtension}."
            : $"Saved {saved} of {connected.Count} setups; see the instrument's status line for the problem.";
        return saved == connected.Count;
    }

    /// <summary>Switches to another configuration file (asking to save unsaved setups first).</summary>
    public async Task OpenConfigurationAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (string.Equals(path, _store.FilePath, PathComparison))
        {
            return;
        }

        if (!File.Exists(path))
        {
            await _dialogs.ShowErrorAsync("Open configuration", $"{path} no longer exists.");
            RemoveRecent(path);
            return;
        }

        if (!await OfferToSaveSetupsAsync("opening another configuration"))
        {
            return;
        }

        await ShutdownAsync();
        if (await LoadConfigurationAsync(path))
        {
            SelectedInstrument = Instruments.FirstOrDefault();
            StatusMessage = $"Opened {Path.GetFileName(path)} ({Plural(Instruments.Count, "instrument")}).";
            await ConnectOnOpeningAsync();
        }
    }

    private async Task OpenConfigurationAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Open configuration", OpenConfigurationFilter, FileFilter.AllFiles);
        if (path is not null)
        {
            await OpenConfigurationAsync(path);
        }
    }

    private async Task NewConfigurationAsync()
    {
        var path = await _dialogs.PickSaveFileAsync("New configuration", "New configuration" + ConfigurationStore.FileExtension, ConfigurationFilter);
        if (path is null)
        {
            return;
        }

        if (!await OfferToSaveSetupsAsync("starting a new configuration"))
        {
            return;
        }

        try
        {
            new ConfigurationStore(path).Save([]);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("New configuration", $"Could not create {path}:\n{ex.Message}");
            return;
        }

        await ShutdownAsync();
        if (await LoadConfigurationAsync(path))
        {
            StatusMessage = $"Started {Path.GetFileName(path)}. Add instruments with Instrument ▸ Add (Ctrl+N).";
        }
    }

    /// <summary>Saves everything (instruments and setups, including any unsaved setup changes) under a new name and switches to it.</summary>
    private async Task SaveConfigurationAsAsync()
    {
        var path = await _dialogs.PickSaveFileAsync("Save configuration as", ConfigurationName + ConfigurationStore.FileExtension, ConfigurationFilter);
        if (path is null)
        {
            return;
        }

        var store = new ConfigurationStore(path);
        _store = store;
        await SaveAllSetupsAsync();
        try
        {
            store.Save(Instruments.Select(i => i.Profile));
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Save configuration", $"Could not save {path}:\n{ex.Message}");
            return;
        }

        RememberConfiguration(store.FilePath);
        RaiseConfigurationProperties();
        StatusMessage = $"Saved the configuration as {Path.GetFileName(path)}.";
    }

    /// <summary>Asks whether to save unsaved setup changes. Returns false if the user cancelled.</summary>
    private async Task<bool> OfferToSaveSetupsAsync(string action)
    {
        var unsaved = Instruments.Where(i => i.HasUnsavedChanges).ToList();
        if (unsaved.Count == 0)
        {
            return true;
        }

        var names = string.Join("\n", unsaved.Select(i => "  •  " + i.Name));
        var answer = await _dialogs.AskAsync(
            "Unsaved setups",
            $"These instruments have settings that are not saved in {ConfigurationName}{ConfigurationStore.FileExtension}:\n\n{names}\n\nSave their setups before {action}?",
            "Save",
            "Don't save");
        return answer.Choice switch
        {
            DialogChoice.Yes => await SaveAllSetupsAsync() || await _dialogs.ConfirmAsync("Unsaved setups", "Some setups could not be saved. Continue anyway?", "Continue"),
            DialogChoice.No => true,
            _ => false,
        };
    }

    /// <summary>Replaces the instrument list with the contents of <paramref name="path"/>. Returns false if it could not be read.</summary>
    private async Task<bool> LoadConfigurationAsync(string path)
    {
        var store = new ConfigurationStore(path);
        IReadOnlyList<InstrumentProfile> profiles;
        try
        {
            profiles = store.Load();
        }
        catch (Exception ex)
        {
            // Keep the unreadable file for the user rather than overwriting it later.
            var backup = store.FilePath + $".unreadable-{DateTime.Now:yyyyMMddHHmmss}";
            try
            {
                File.Copy(store.FilePath, backup, overwrite: false);
            }
            catch
            {
                backup = store.FilePath;
            }

            await _dialogs.ShowErrorAsync("Open configuration", $"{Path.GetFileName(store.FilePath)} could not be read. A copy has been kept as:\n{backup}\n\n{ex.Message}");
            if (store.FilePath != _defaultConfigurationPath)
            {
                return false;
            }

            profiles = [];
        }

        _store = store;
        Alerts.Clear();
        Instruments.Clear();
        foreach (var profile in profiles)
        {
            Instruments.Add(CreateItem(profile));
        }

        if (!string.Equals(store.FilePath, _defaultConfigurationPath, PathComparison))
        {
            RememberConfiguration(store.FilePath);
        }
        else
        {
            _settings.ConfigurationPath = null;
            SaveSettings();
        }

        RaiseConfigurationProperties();
        RaiseSelectionCommands();
        return true;
    }

    private InstrumentItemViewModel CreateItem(InstrumentProfile profile)
    {
        var item = new InstrumentItemViewModel(profile, _dialogs, _ => Save(), Audit, () => ConfigurationName);
        item.Log.IsPanelVisible = _settings.ShowLog;
        item.Log.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogViewModel.IsPanelVisible))
            {
                ShowLog = item.Log.IsPanelVisible;
            }
        };
        item.PropertyChanged += OnItemPropertyChanged;
        item.AlertRaised += OnAlertRaised;
        item.AlertResolved += OnAlertResolved;
        return item;
    }

    private void OnAlertRaised(object? sender, InstrumentAlert alert)
    {
        if (sender is not InstrumentItemViewModel item)
        {
            return;
        }

        // The same problem again updates its alert instead of adding another.
        if (Alerts.FirstOrDefault(a => a.Item == item && a.Kind == alert.Kind) is { } existing)
        {
            existing.Repeat(alert);
            return;
        }

        var added = new AlertViewModel(item, alert, a => Alerts.Remove(a), i => SelectedInstrument = i);
        Alerts.Insert(0, added);
        AlertRaised?.Invoke(this, added);
    }

    private void OnAlertResolved(object? sender, string kind)
    {
        foreach (var alert in Alerts.Where(a => a.Item == sender && a.Kind == kind).ToList())
        {
            Alerts.Remove(alert);
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstrumentItemViewModel.HasUnsavedChanges) or "")
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
            OnPropertyChanged(nameof(WindowTitle));
        }

        if (sender is InstrumentItemViewModel item && item == _selected
            && e.PropertyName is nameof(InstrumentItemViewModel.StatusMessage) or nameof(InstrumentItemViewModel.StatusIsError)
            && item.StatusMessage is { } message)
        {
            StatusMessage = message;
            StatusIsError = item.StatusIsError;
        }
    }

    private ICommand SessionCommand(Func<InstrumentSessionViewModel, ICommand> pick) => new RelayCommand(() =>
    {
        if (_selected?.Session is { } session && pick(session) is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    });

    private ICommand ItemCommand(Func<InstrumentItemViewModel, ICommand> pick) => new RelayCommand(() =>
    {
        if (_selected is { } item && pick(item) is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    });

    private void SaveSettings() => _settingsStore?.Save(_settings);

    /// <summary>Writes the audit trail to <paramref name="folder"/> from now on (null: the default folder) and remembers it.</summary>
    private void SetAuditFolder(string? folder)
    {
        _settings.AuditFolder = folder;
        SaveSettings();
        StatusMessage = $"The audit trail is now written to {Audit.Folder}.";
    }

    private void RecordConfigurationChange(InstrumentProfile profile, string action, string details) =>
        Audit.Record(profile, null, action, details: details);

    private static string DescribeProfile(InstrumentProfile profile) =>
        (profile.IsSimulator ? "simulated instrument" : profile.Address)
        + (string.IsNullOrWhiteSpace(profile.Location) ? string.Empty : $", {profile.Location}");

    /// <summary>The instrument details changed in the editor, for the audit trail.</summary>
    private static IEnumerable<(string Setting, string? Before, string? After)> CompareProfiles(InstrumentProfile before, InstrumentProfile after)
    {
        static string OnOff(bool on) => on ? "On" : "Off";
        static string Describe(SetupOnConnect choice) =>
            ProfileEditorViewModel.SetupOnConnectOptions.FirstOrDefault(o => o.Value == (int)choice)?.Label ?? choice.ToString();
        var fields = new (string Setting, string? Before, string? After)[]
        {
            ("Name", before.Name, after.Name),
            ("Address", before.Host, after.Host),
            ("Port", before.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), after.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Timeout", $"{before.TimeoutMs} ms", $"{after.TimeoutMs} ms"),
            ("Location", before.Location, after.Location),
            ("Notes", before.Notes, after.Notes),
            ("Connect when the configuration opens", OnOff(before.ConnectOnStartup), OnOff(after.ConnectOnStartup)),
            ("Return to local control on disconnecting", OnOff(before.ReturnToLocalOnDisconnect), OnOff(after.ReturnToLocalOnDisconnect)),
            ("Saved setup on connecting", Describe(before.SetupOnConnect), Describe(after.SetupOnConnect)),
        };
        return fields.Where(f => !string.Equals(f.Before ?? string.Empty, f.After ?? string.Empty, StringComparison.Ordinal));
    }

    private void Save()
    {
        try
        {
            _store.Save(Instruments.Select(i => i.Profile));
        }
        catch (Exception ex)
        {
            _ = _dialogs.ShowErrorAsync("Save configuration", $"The configuration could not be saved to {_store.FilePath}:\n{ex.Message}");
        }

        RaiseSelectionCommands();
    }

    private async Task AddFromEditorAsync(InstrumentProfile draft)
    {
        var editor = new ProfileEditorViewModel(draft, Instruments.Select(i => i.Profile), isNew: true);
        if (await _dialogs.EditProfileAsync(editor))
        {
            AddProfile(editor.Result);
            StatusMessage = $"Added {editor.Result.Name}.";
        }
    }

    private async Task EditInstrumentAsync(InstrumentItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var editor = new ProfileEditorViewModel(item.Profile, Instruments.Select(i => i.Profile), isNew: false);
        if (!await _dialogs.EditProfileAsync(editor))
        {
            return;
        }

        var addressChanged = editor.Result.Host != item.Profile.Host || editor.Result.Port != item.Profile.Port;
        var before = item.Profile;
        item.UpdateProfile(editor.Result);
        Save();
        foreach (var (setting, oldValue, newValue) in CompareProfiles(before, editor.Result))
        {
            Audit.Record(editor.Result, null, AuditActions.InstrumentEdited, setting, oldValue, newValue, details: $"Saved in {ConfigurationName}");
        }

        StatusMessage = addressChanged && item.IsConnected
            ? $"Saved {item.Name}. Reconnect to use the new address."
            : $"Saved {item.Name}.";
    }

    private async Task RemoveSelectedAsync()
    {
        if (_selected is not { } item)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Remove instrument",
                $"Remove \"{item.Name}\" and its saved setup from {ConfigurationName}? The instrument itself is not affected.", "Remove"))
        {
            return;
        }

        await item.DisconnectAsync();
        var index = Instruments.IndexOf(item);
        item.PropertyChanged -= OnItemPropertyChanged;
        item.AlertRaised -= OnAlertRaised;
        item.AlertResolved -= OnAlertResolved;
        foreach (var alert in Alerts.Where(a => a.Item == item).ToList())
        {
            Alerts.Remove(alert);
        }

        Instruments.Remove(item);
        Save();
        RecordConfigurationChange(item.Profile, AuditActions.InstrumentRemoved,
            $"Removed from {ConfigurationName}{(item.Profile.StoredSetup is null ? string.Empty : ", with its saved setup")}");
        SelectedInstrument = Instruments.Count == 0 ? null : Instruments[Math.Min(index, Instruments.Count - 1)];
        StatusMessage = $"Removed {item.Name}.";
    }

    private async Task DuplicateSelectedAsync()
    {
        if (_selected is not { } item)
        {
            return;
        }

        var copy = item.Profile.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = UniqueName(item.Name + " (copy)");
        copy.LastConnectedUtc = null;
        copy.LastKnownIdentity = null;
        await AddFromEditorAsync(copy);
    }

    private async Task DiscoverAsync()
    {
        var discovery = new DiscoveryViewModel();
        if (!await _dialogs.ShowDiscoveryAsync(discovery) || discovery.Selected is not { } found)
        {
            return;
        }

        var existing = Instruments.FirstOrDefault(i => !i.IsSimulator && i.Profile.Host == found.Address && i.Profile.Port == found.Port);
        if (existing is not null)
        {
            SelectedInstrument = existing;
            await _dialogs.ShowInfoAsync("Find instruments", $"{found.Address} is already saved as \"{existing.Name}\".");
            return;
        }

        await AddFromEditorAsync(new InstrumentProfile
        {
            Name = UniqueName(SuggestName(found)),
            Host = found.Address,
            Port = found.Port,
            LastKnownIdentity = found.Identity,
        });
    }

    private void AddDemoInstrument()
    {
        AddProfile(new InstrumentProfile
        {
            Name = UniqueName("Demo DG645 (simulated)"),
            IsSimulator = true,
            Notes = "A software simulation of a DG645 for trying out the program without hardware.",
        });
        StatusMessage = "Added a simulated DG645. Select it and choose Connect.";
    }

    private async Task ImportInstrumentsAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Import instruments from configuration", OpenConfigurationFilter, new FileFilter("Instrument list", "*.json"), FileFilter.AllFiles);
        if (path is null)
        {
            return;
        }

        try
        {
            var imported = ConfigurationStore.Deserialize(await File.ReadAllTextAsync(path));
            var added = 0;
            foreach (var profile in imported)
            {
                if (Instruments.Any(i => i.Profile.Id == profile.Id))
                {
                    continue;
                }

                profile.Name = UniqueName(profile.Name);
                Instruments.Add(CreateItem(profile));
                added++;
                RecordConfigurationChange(profile, AuditActions.InstrumentAdded,
                    $"Imported into {ConfigurationName} from {Path.GetFileName(path)}: {DescribeProfile(profile)}");
            }

            Save();
            StatusMessage = $"Imported {Plural(added, "instrument")} ({imported.Count - added} already present).";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Import instruments", $"Could not import {Path.GetFileName(path)}:\n{ex.Message}");
        }
    }

    private void MoveSelected(int delta)
    {
        if (_selected is not { } item)
        {
            return;
        }

        var index = Instruments.IndexOf(item);
        var target = index + delta;
        if (target < 0 || target >= Instruments.Count)
        {
            return;
        }

        Instruments.Move(index, target);
        Save();
        RaiseSelectionCommands();
    }

    private void RememberConfiguration(string path)
    {
        _settings.ConfigurationPath = string.Equals(path, _defaultConfigurationPath, PathComparison) ? null : path;
        _settings.RecentConfigurations.RemoveAll(p => string.Equals(p, path, PathComparison));
        _settings.RecentConfigurations.Insert(0, path);
        if (_settings.RecentConfigurations.Count > MaxRecent)
        {
            _settings.RecentConfigurations.RemoveRange(MaxRecent, _settings.RecentConfigurations.Count - MaxRecent);
        }

        SaveSettings();
        RefreshRecent();
    }

    private void RemoveRecent(string path)
    {
        _settings.RecentConfigurations.RemoveAll(p => string.Equals(p, path, PathComparison));
        SaveSettings();
        RefreshRecent();
    }

    private void RefreshRecent()
    {
        RecentConfigurations.Clear();
        foreach (var path in _settings.RecentConfigurations)
        {
            RecentConfigurations.Add(new RecentConfiguration(path, Path.GetFileNameWithoutExtension(path), OpenRecentCommand));
        }

        OnPropertyChanged(nameof(HasRecentConfigurations));
    }

    private void RaiseConfigurationProperties()
    {
        OnPropertyChanged(nameof(ConfigurationPath));
        OnPropertyChanged(nameof(ConfigurationName));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void RaiseThemeProperties()
    {
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var i = 2; Instruments.Any(x => string.Equals(x.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
        {
            candidate = $"{name} {i}";
        }

        return candidate;
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string SuggestName(DiscoveredInstrument found) =>
        string.IsNullOrEmpty(found.SerialNumber) ? $"{found.Model} at {found.Address}" : $"{found.Model} {found.SerialNumber}";

    private void RaiseSelectionCommands()
    {
        foreach (var command in new[] { EditInstrumentCommand, RemoveInstrumentCommand, DuplicateInstrumentCommand, MoveUpCommand, MoveDownCommand })
        {
            (command as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
        }
    }
}
