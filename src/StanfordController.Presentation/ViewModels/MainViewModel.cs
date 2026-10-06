using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
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

    private readonly string _defaultConfigurationPath;
    private readonly IDialogService _dialogs;
    private readonly AppSettingsStore? _settingsStore;
    private ConfigurationStore _store;
    private AppSettings _settings = new();
    private InstrumentItemViewModel? _selected;
    private string? _statusMessage;
    private bool _statusIsError;
    private bool _loading;

    /// <param name="defaultConfigurationPath">The configuration used when none has been opened (Default.strobe).</param>
    public MainViewModel(string defaultConfigurationPath, IDialogService dialogs, AppSettingsStore? settingsStore = null)
    {
        _defaultConfigurationPath = Path.GetFullPath(defaultConfigurationPath);
        _store = new ConfigurationStore(_defaultConfigurationPath);
        _dialogs = dialogs;
        _settingsStore = settingsStore;
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
    }

    public ObservableCollection<InstrumentItemViewModel> Instruments { get; } = [];

    public bool HasInstruments => Instruments.Count > 0;

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

    /// <summary>"Default — Strobe", with an asterisk while setups have unsaved changes.</summary>
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

    /// <summary>Reads preferences and opens the configuration used last time (or the default one).</summary>
    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            if (_settingsStore is not null)
            {
                _settings = _settingsStore.Load();
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

    /// <summary>Connects the instruments marked "connect automatically", one after another.</summary>
    public async Task ConnectStartupInstrumentsAsync()
    {
        foreach (var item in Instruments.Where(i => i.Profile.ConnectOnStartup).ToList())
        {
            await item.ConnectAsync();
        }
    }

    /// <summary>
    /// Before closing: offers to save setups that have unsaved changes. Returns false if the user cancelled.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync() => await OfferToSaveSetupsAsync("closing");

    /// <summary>Disconnects every instrument (returning front panels to local control).</summary>
    public async Task ShutdownAsync()
    {
        foreach (var item in Instruments.Where(i => i.Session is not null).ToList())
        {
            await item.DisconnectAsync();
        }
    }

    public InstrumentItemViewModel AddProfile(InstrumentProfile profile)
    {
        var item = CreateItem(profile);
        Instruments.Add(item);
        Save();
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
            await ConnectStartupInstrumentsAsync();
        }
    }

    private async Task OpenConfigurationAsync()
    {
        var path = await _dialogs.PickOpenFileAsync("Open configuration", ConfigurationFilter, FileFilter.AllFiles);
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
        var item = new InstrumentItemViewModel(profile, _dialogs, _ => Save());
        item.Log.IsPanelVisible = _settings.ShowLog;
        item.Log.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogViewModel.IsPanelVisible))
            {
                ShowLog = item.Log.IsPanelVisible;
            }
        };
        item.PropertyChanged += OnItemPropertyChanged;
        return item;
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
        item.UpdateProfile(editor.Result);
        Save();
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
        Instruments.Remove(item);
        Save();
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
        var path = await _dialogs.PickOpenFileAsync("Import instruments from configuration", ConfigurationFilter, new FileFilter("Instrument list", "*.json"), FileFilter.AllFiles);
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
