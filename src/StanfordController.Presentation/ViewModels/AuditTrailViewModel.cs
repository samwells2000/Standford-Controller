using System.ComponentModel;
using System.Windows.Input;
using StanfordController.Core.Audit;
using StanfordController.Presentation.Mvvm;
using StanfordController.Presentation.Services;

namespace StanfordController.Presentation.ViewModels;

/// <summary>The audit trail window: a month's records, filtered by instrument and text, and a check that none were altered.</summary>
public sealed class AuditTrailViewModel : ObservableObject
{
    public const string AllInstruments = "All instruments";

    private readonly AuditLog _log;
    private readonly IDialogService _dialogs;
    private readonly Action<string?> _folderChosen;
    private IReadOnlyList<AuditRecord> _monthRecords = [];
    private IReadOnlyList<AuditRecord> _records = [];
    private IReadOnlyList<string> _months = [];
    private IReadOnlyList<string> _instruments = [AllInstruments];
    private string? _selectedMonth;
    private string _selectedInstrument = AllInstruments;
    private string _searchText = string.Empty;
    private AuditRecord? _selectedRecord;
    private string? _integrityText;
    private bool _integrityIsProblem;
    private string? _readError;

    /// <param name="folderChosen">Called with a newly chosen folder (null for the default) so it can be remembered.</param>
    public AuditTrailViewModel(AuditLog log, IDialogService dialogs, Action<string?> folderChosen)
    {
        _log = log;
        _dialogs = dialogs;
        _folderChosen = folderChosen;
        RefreshCommand = new RelayCommand(Reload);
        CheckIntegrityCommand = new RelayCommand(CheckIntegrity, () => _selectedMonth is not null);
        ChangeFolderCommand = new AsyncRelayCommand(ChangeFolderAsync);
        UseDefaultFolderCommand = new RelayCommand(() => UseFolder(null), () => !_log.UsesDefaultFolder);
        OpenFolderCommand = new AsyncRelayCommand(() => _dialogs.OpenFolderAsync(_log.Folder));
        _log.Written += OnWritten;
        _log.PropertyChanged += OnLogPropertyChanged;
        Reload();
    }

    public string Folder => _log.Folder;

    public bool UsesDefaultFolder => _log.UsesDefaultFolder;

    /// <summary>Why records are not being written, if they are not.</summary>
    public string? Problem => _log.Problem;

    public bool HasProblem => _log.HasProblem;

    public IReadOnlyList<string> Months
    {
        get => _months;
        private set
        {
            if (SetProperty(ref _months, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>"2026-10": the month on show.</summary>
    public string? SelectedMonth
    {
        get => _selectedMonth;
        set
        {
            if (SetProperty(ref _selectedMonth, value))
            {
                LoadMonth();
                (CheckIntegrityCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<string> Instruments
    {
        get => _instruments;
        private set => SetProperty(ref _instruments, value);
    }

    public string SelectedInstrument
    {
        get => _selectedInstrument;
        set
        {
            if (SetProperty(ref _selectedInstrument, value ?? AllInstruments))
            {
                ApplyFilter();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>The records on show, newest first.</summary>
    public IReadOnlyList<AuditRecord> Records
    {
        get => _records;
        private set
        {
            if (SetProperty(ref _records, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public AuditRecord? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            if (SetProperty(ref _selectedRecord, value))
            {
                OnPropertyChanged(nameof(HasSelectedRecord));
            }
        }
    }

    public bool HasSelectedRecord => _selectedRecord is not null;

    /// <summary>True when the folder holds no records yet.</summary>
    public bool IsEmpty => _months.Count == 0;

    public string Summary => _readError
        ?? (_records.Count == _monthRecords.Count
            ? Plural(_records.Count, "record")
            : $"{_records.Count} of {Plural(_monthRecords.Count, "record")}");

    /// <summary>The result of the last integrity check, or null if the month has not been checked.</summary>
    public string? IntegrityText
    {
        get => _integrityText;
        private set
        {
            if (SetProperty(ref _integrityText, value))
            {
                OnPropertyChanged(nameof(HasIntegrityText));
            }
        }
    }

    public bool HasIntegrityText => _integrityText is not null;

    public bool IntegrityIsProblem
    {
        get => _integrityIsProblem;
        private set => SetProperty(ref _integrityIsProblem, value);
    }

    public ICommand RefreshCommand { get; }

    public ICommand CheckIntegrityCommand { get; }

    public ICommand ChangeFolderCommand { get; }

    public ICommand UseDefaultFolderCommand { get; }

    public ICommand OpenFolderCommand { get; }

    /// <summary>Stops following new records; called when the window closes.</summary>
    public void Detach()
    {
        _log.Written -= OnWritten;
        _log.PropertyChanged -= OnLogPropertyChanged;
    }

    /// <summary>Reads the folder again, keeping the month on show if it still has records.</summary>
    public void Reload()
    {
        var store = _log.Store;
        IReadOnlyList<string> months;
        try
        {
            months = store.Months();
            _readError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            months = [];
            _readError = $"The folder could not be read: {ex.Message}";
        }

        Months = months;
        var month = _selectedMonth is not null && months.Contains(_selectedMonth) ? _selectedMonth : months.FirstOrDefault();
        if (month == _selectedMonth)
        {
            LoadMonth();
        }
        else
        {
            SelectedMonth = month;
        }
    }

    /// <summary>Checks every file for the month on show for records that were changed, added or removed.</summary>
    public void CheckIntegrity()
    {
        if (_selectedMonth is not { } month)
        {
            return;
        }

        try
        {
            var results = _log.Store.Files().Where(f => AuditTrailStore.MonthOf(f) == month).Select(AuditTrailStore.Verify).ToList();
            var problems = results.Where(r => !r.IsIntact).ToList();
            IntegrityIsProblem = problems.Count > 0;
            IntegrityText = problems.Count > 0
                ? string.Join("\n", problems.Select(p => p.Message))
                : $"Checked {Plural(results.Count, "file")} for {month}: all {Plural(results.Sum(r => r.Records), "record")} are intact. " +
                  "None has been changed, and none added or removed before the latest.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IntegrityIsProblem = true;
            IntegrityText = $"The files could not be read: {ex.Message}";
        }
    }

    private void LoadMonth()
    {
        IntegrityText = null;
        IntegrityIsProblem = false;
        try
        {
            _monthRecords = _selectedMonth is { } month ? _log.Store.ReadMonth(month) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _monthRecords = [];
            _readError = $"The records could not be read: {ex.Message}";
        }

        Instruments = [AllInstruments, .. _monthRecords.Select(r => r.Instrument).Where(n => n.Length > 0).Distinct().Order(StringComparer.CurrentCultureIgnoreCase)];
        if (!Instruments.Contains(_selectedInstrument))
        {
            _selectedInstrument = AllInstruments;
            OnPropertyChanged(nameof(SelectedInstrument));
        }

        ApplyFilter();
        OnPropertyChanged(nameof(Summary));
    }

    private void ApplyFilter()
    {
        var text = _searchText.Trim();
        var selected = _selectedRecord;
        Records = _monthRecords
            .Where(r => _selectedInstrument == AllInstruments || r.Instrument == _selectedInstrument)
            .Where(r => text.Length == 0 || Matches(r, text))
            .OrderByDescending(r => r.Time)
            .ToList();
        SelectedRecord = selected is not null && _records.Contains(selected) ? selected : null;
    }

    private static bool Matches(AuditRecord record, string text) =>
        new[]
        {
            record.Instrument, record.User, record.Action, record.Setting, record.OldValue, record.NewValue,
            record.Reason, record.Details, record.Serial, record.Address, record.Computer,
        }.Any(field => field?.Contains(text, StringComparison.CurrentCultureIgnoreCase) == true);

    private async Task ChangeFolderAsync()
    {
        if (await _dialogs.PickFolderAsync("Choose the audit trail folder") is { } folder)
        {
            UseFolder(folder);
        }
    }

    private void UseFolder(string? folder)
    {
        _log.SetFolder(folder);
        _folderChosen(folder);
        (UseDefaultFolderCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
        Reload();
    }

    private void OnWritten(object? sender, EventArgs e) => Reload();

    private void OnLogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AuditLog.Problem):
                OnPropertyChanged(nameof(Problem));
                OnPropertyChanged(nameof(HasProblem));
                break;
            case nameof(AuditLog.Folder):
                OnPropertyChanged(nameof(Folder));
                OnPropertyChanged(nameof(UsesDefaultFolder));
                break;
        }
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
