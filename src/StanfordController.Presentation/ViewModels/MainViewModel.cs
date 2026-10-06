using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using StanfordController.Core.Discovery;
using StanfordController.Core.Profiles;
using StanfordController.Presentation.Mvvm;
using StanfordController.Presentation.Services;

namespace StanfordController.Presentation.ViewModels;

/// <summary>The main window: the saved-instrument list and the selected instrument.</summary>
public sealed class MainViewModel : ObservableObject
{
    private const string ProfilesFilter = "Instrument list (*.json)|*.json|All files (*.*)|*.*";

    private readonly ProfileStore _store;
    private readonly IDialogService _dialogs;
    private readonly AppSettingsStore? _settingsStore;
    private AppSettings _settings = new();
    private InstrumentItemViewModel? _selected;
    private string? _statusMessage;
    private bool _loading;

    public MainViewModel(ProfileStore store, IDialogService dialogs, AppSettingsStore? settingsStore = null)
    {
        _store = store;
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        Instruments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasInstruments));

        AddInstrumentCommand = new RelayCommand(AddInstrument);
        ConnectInstrumentCommand = new RelayCommand<InstrumentItemViewModel>(item => item?.ConnectCommand.Execute(null));
        EditInstrumentCommand = new RelayCommand<InstrumentItemViewModel>(item => EditInstrument(item ?? _selected), item => (item ?? _selected) is not null);
        RemoveInstrumentCommand = new AsyncRelayCommand(RemoveSelectedAsync, () => _selected is not null);
        DuplicateInstrumentCommand = new RelayCommand(DuplicateSelected, () => _selected is not null);
        DiscoverCommand = new RelayCommand(Discover);
        AddDemoInstrumentCommand = new RelayCommand(AddDemoInstrument);
        ImportInstrumentsCommand = new RelayCommand(ImportInstruments);
        ExportInstrumentsCommand = new RelayCommand(ExportInstruments, () => Instruments.Count > 0);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => _selected is not null && Instruments.IndexOf(_selected) > 0);
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => _selected is not null && Instruments.IndexOf(_selected) < Instruments.Count - 1);
        AboutCommand = new RelayCommand(_dialogs.ShowAbout);

        // Keyboard shortcuts act on the selected instrument.
        ConnectSelectedCommand = new RelayCommand(() => _selected?.ConnectCommand.Execute(null));
        DisconnectSelectedCommand = new RelayCommand(() => _selected?.DisconnectCommand.Execute(null));
        RefreshCommand = SessionCommand(s => s.RefreshAllCommand);
        TriggerCommand = SessionCommand(s => s.TriggerCommand);
        SaveSettingsFileCommand = SessionCommand(s => s.ExportSettingsCommand);
        LoadSettingsFileCommand = SessionCommand(s => s.ImportSettingsCommand);
        ToggleLogCommand = new RelayCommand(() => ShowLog = !ShowLog);
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
                OnPropertyChanged(nameof(WindowTitle));
                if (!_loading)
                {
                    _settings.SelectedInstrumentId = value?.Profile.Id;
                    SaveSettings();
                }
            }
        }
    }

    public string WindowTitle => _selected is null ? AppName : $"{_selected.Name} — {AppName}";

    public const string AppName = "DG645 Controller";

    /// <summary>Shown in the sidebar footer, e.g. "Version 1.0.42".</summary>
    public string VersionText { get; init; } = string.Empty;

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
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSystemTheme));
            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(IsDarkTheme));
            SaveSettings();
        }
    }

    // Two-way targets for the System / Light / Dark segmented switch.
    public bool IsSystemTheme
    {
        get => Theme == AppTheme.System;
        set
        {
            if (value)
            {
                Theme = AppTheme.System;
            }
        }
    }

    public bool IsLightTheme
    {
        get => Theme == AppTheme.Light;
        set
        {
            if (value)
            {
                Theme = AppTheme.Light;
            }
        }
    }

    public bool IsDarkTheme
    {
        get => Theme == AppTheme.Dark;
        set
        {
            if (value)
            {
                Theme = AppTheme.Dark;
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

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string ProfilesPath => _store.FilePath;

    public ICommand AddInstrumentCommand { get; }

    /// <summary>Connects the given instrument (double-click in the list).</summary>
    public ICommand ConnectInstrumentCommand { get; }

    public ICommand EditInstrumentCommand { get; }

    public ICommand RemoveInstrumentCommand { get; }

    public ICommand DuplicateInstrumentCommand { get; }

    public ICommand DiscoverCommand { get; }

    public ICommand AddDemoInstrumentCommand { get; }

    public ICommand ImportInstrumentsCommand { get; }

    public ICommand ExportInstrumentsCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand AboutCommand { get; }

    public ICommand ConnectSelectedCommand { get; }

    public ICommand DisconnectSelectedCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand TriggerCommand { get; }

    public ICommand SaveSettingsFileCommand { get; }

    public ICommand LoadSettingsFileCommand { get; }

    public ICommand ToggleLogCommand { get; }

    public void Load()
    {
        _loading = true;
        try
        {
            LoadCore();
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadCore()
    {
        if (_settingsStore is not null)
        {
            _settings = _settingsStore.Load();
            OnPropertyChanged(nameof(Theme));
            OnPropertyChanged(nameof(IsSystemTheme));
            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(ShowLog));
        }

        IReadOnlyList<InstrumentProfile> profiles;
        try
        {
            profiles = _store.Load();
        }
        catch (Exception ex)
        {
            // Keep the unreadable file for the user rather than overwriting it with an empty list.
            var backup = _store.FilePath + $".unreadable-{DateTime.Now:yyyyMMddHHmmss}";
            try
            {
                File.Copy(_store.FilePath, backup, overwrite: false);
            }
            catch
            {
                backup = _store.FilePath;
            }

            _dialogs.ShowError("Saved instruments", $"The saved instrument list could not be read and has been set aside as:\n{backup}\n\n{ex.Message}");
            profiles = [];
        }

        Instruments.Clear();
        foreach (var profile in profiles)
        {
            Instruments.Add(CreateItem(profile));
        }

        SelectedInstrument = Instruments.FirstOrDefault(i => i.Profile.Id == _settings.SelectedInstrumentId) ?? Instruments.FirstOrDefault();
        RaiseSelectionCommands();
    }

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
        return item;
    }

    private ICommand SessionCommand(Func<InstrumentSessionViewModel, ICommand> pick) => new RelayCommand(() =>
    {
        if (_selected?.Session is { } session && pick(session) is { } command && command.CanExecute(null))
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
            _dialogs.ShowError("Saved instruments", $"The instrument list could not be saved to {_store.FilePath}:\n{ex.Message}");
        }

        (ExportInstrumentsCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
        RaiseSelectionCommands();
    }

    private void AddInstrument() => AddFromEditor(new InstrumentProfile());

    private void AddFromEditor(InstrumentProfile draft)
    {
        var editor = new ProfileEditorViewModel(draft, Instruments.Select(i => i.Profile), isNew: true);
        if (_dialogs.EditProfile(editor))
        {
            AddProfile(editor.Result);
            StatusMessage = $"Added {editor.Result.Name}.";
        }
    }

    private void EditInstrument(InstrumentItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var editor = new ProfileEditorViewModel(item.Profile, Instruments.Select(i => i.Profile), isNew: false);
        if (!_dialogs.EditProfile(editor))
        {
            return;
        }

        var addressChanged = editor.Result.Host != item.Profile.Host || editor.Result.Port != item.Profile.Port;
        item.UpdateProfile(editor.Result);
        Save();
        OnPropertyChanged(nameof(WindowTitle));
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

        if (!_dialogs.Confirm("Remove instrument", $"Remove \"{item.Name}\" from the saved instruments? The instrument itself is not affected."))
        {
            return;
        }

        await item.DisconnectAsync();
        var index = Instruments.IndexOf(item);
        Instruments.Remove(item);
        Save();
        SelectedInstrument = Instruments.Count == 0 ? null : Instruments[Math.Min(index, Instruments.Count - 1)];
        StatusMessage = $"Removed {item.Name}.";
    }

    private void DuplicateSelected()
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
        AddFromEditor(copy);
    }

    private void Discover()
    {
        var discovery = new DiscoveryViewModel();
        if (!_dialogs.ShowDiscovery(discovery) || discovery.Selected is not { } found)
        {
            return;
        }

        var existing = Instruments.FirstOrDefault(i => !i.IsSimulator && i.Profile.Host == found.Address && i.Profile.Port == found.Port);
        if (existing is not null)
        {
            SelectedInstrument = existing;
            _dialogs.ShowInfo("Find instruments", $"{found.Address} is already saved as \"{existing.Name}\".");
            return;
        }

        AddFromEditor(new InstrumentProfile
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

    private void ImportInstruments()
    {
        var path = _dialogs.PickOpenFile("Import instrument list", ProfilesFilter);
        if (path is null)
        {
            return;
        }

        try
        {
            var imported = ProfileStore.Deserialize(File.ReadAllText(path));
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
            StatusMessage = $"Imported {added} instrument{(added == 1 ? "" : "s")} ({imported.Count - added} already present).";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Import instrument list", $"Could not import {Path.GetFileName(path)}:\n{ex.Message}");
        }
    }

    private void ExportInstruments()
    {
        var path = _dialogs.PickSaveFile("Export instrument list", ProfilesFilter, "DG645 instruments.json");
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, ProfileStore.Serialize(Instruments.Select(i => i.Profile)));
            StatusMessage = $"Exported {Instruments.Count} instruments to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Export instrument list", ex.Message);
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
