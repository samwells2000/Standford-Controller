using StanfordController.Core.Audit;
using StanfordController.Core.Profiles;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.Services;

/// <summary>
/// Records changes in the audit trail. Records are written in the background, in order, so recording never holds up
/// the instrument. If the folder cannot be written (a network share is offline, a file is held open elsewhere), the
/// records wait in memory, <see cref="Problem"/> says so, and they are written as soon as the folder can be reached.
/// </summary>
public sealed class AuditLog : ObservableObject
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly object _sync = new();
    private readonly List<AuditRecord> _pending = [];
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly Timer _retry;
    private AuditTrailStore _store;
    private Task _writer = Task.CompletedTask;
    private bool _writing;
    private string? _problem;
    private int _pendingCount;

    public AuditLog(string defaultFolder, string software)
    {
        DefaultFolder = Path.GetFullPath(defaultFolder);
        Software = software;
        _store = new AuditTrailStore(DefaultFolder);
        _retry = new Timer(_ => Kick());
    }

    /// <summary>Raised on the UI thread after records have been written.</summary>
    public event EventHandler? Written;

    /// <summary>The computer account changes are recorded against, e.g. LAB\jsmith on Windows.</summary>
    public static string CurrentUser { get; } = OperatingSystem.IsWindows() && !string.IsNullOrEmpty(Environment.UserDomainName)
        ? $"{Environment.UserDomainName}\\{Environment.UserName}"
        : Environment.UserName;

    public static string CurrentComputer { get; } = Environment.MachineName;

    /// <summary>"Tempo 1.0.21": the program recorded with each change.</summary>
    public string Software { get; }

    /// <summary>Where records go when no other folder has been chosen: "Audit trail" in the data folder.</summary>
    public string DefaultFolder { get; }

    public string Folder
    {
        get
        {
            lock (_sync)
            {
                return _store.Folder;
            }
        }
    }

    public bool UsesDefaultFolder => string.Equals(Folder, DefaultFolder, StringComparison.Ordinal);

    public AuditTrailStore Store
    {
        get
        {
            lock (_sync)
            {
                return _store;
            }
        }
    }

    /// <summary>Why records could not be written, or null when everything has been written.</summary>
    public string? Problem
    {
        get => _problem;
        private set
        {
            if (SetProperty(ref _problem, value))
            {
                OnPropertyChanged(nameof(HasProblem));
            }
        }
    }

    public bool HasProblem => _problem is not null;

    /// <summary>Records waiting to be written.</summary>
    public int PendingCount
    {
        get => _pendingCount;
        private set => SetProperty(ref _pendingCount, value);
    }

    /// <summary>Writes from now on to <paramref name="folder"/>, or to <see cref="DefaultFolder"/> when null.</summary>
    public void SetFolder(string? folder)
    {
        lock (_sync)
        {
            _store = new AuditTrailStore(string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder);
        }

        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(UsesDefaultFolder));
        OnPropertyChanged(nameof(Store));
        Kick();
    }

    /// <summary>Adds a record about an instrument. Time, user, computer and program are filled in here.</summary>
    public void Record(InstrumentProfile profile, string? serial, string action, string? setting = null,
        string? oldValue = null, string? newValue = null, string? reason = null, string? details = null) =>
        Record(new AuditRecord
        {
            Instrument = profile.Name,
            Serial = serial ?? SerialFromIdentity(profile.LastKnownIdentity),
            Address = profile.IsSimulator ? "Simulator" : profile.Address,
            Action = action,
            Setting = setting,
            OldValue = oldValue,
            NewValue = newValue,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            Details = details,
        });

    public void Record(AuditRecord record)
    {
        record = record with
        {
            Time = record.Time == default ? DateTimeOffset.Now : record.Time,
            User = record.User.Length == 0 ? CurrentUser : record.User,
            Computer = record.Computer.Length == 0 ? CurrentComputer : record.Computer,
            Software = record.Software.Length == 0 ? Software : record.Software,
        };

        lock (_sync)
        {
            _pending.Add(record);
        }

        Kick();
    }

    /// <summary>Waits until every record has been written, or writing has failed. Returns the number still waiting.</summary>
    public async Task<int> FlushAsync()
    {
        Kick();
        Task writer;
        lock (_sync)
        {
            writer = _writer;
        }

        await writer.ConfigureAwait(false);
        lock (_sync)
        {
            return _pending.Count;
        }
    }

    /// <summary>
    /// On closing, if records could not be written to the chosen folder, writes them to <see cref="DefaultFolder"/>
    /// instead so they are not lost. Returns the number that could not be written anywhere.
    /// </summary>
    public async Task<int> FlushOnExitAsync()
    {
        var left = await FlushAsync().ConfigureAwait(false);
        if (left > 0 && !UsesDefaultFolder)
        {
            lock (_sync)
            {
                _store = new AuditTrailStore(DefaultFolder);
            }

            left = await FlushAsync().ConfigureAwait(false);
        }

        _retry.Dispose();
        return left;
    }

    /// <summary>"s/n001234" from "Stanford Research Systems,DG645,s/n001234,ver1.14.10E".</summary>
    public static string? SerialFromIdentity(string? identity) =>
        identity?.Split(',') is { Length: > 2 } parts && parts[2].Trim().Length > 0 ? parts[2].Trim() : null;

    private void Kick()
    {
        lock (_sync)
        {
            if (_writing || _pending.Count == 0)
            {
                return;
            }

            _writing = true;
            _writer = Task.Run(Drain);
        }
    }

    private void Drain()
    {
        string? problem = null;
        var wrote = false;
        int left;
        while (true)
        {
            AuditRecord next;
            AuditTrailStore store;
            lock (_sync)
            {
                if (_pending.Count == 0)
                {
                    _writing = false;
                    left = 0;
                    break;
                }

                next = _pending[0];
                store = _store;
            }

            try
            {
                store.Append(next);
                wrote = true;
                lock (_sync)
                {
                    _pending.RemoveAt(0);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                lock (_sync)
                {
                    _writing = false;
                    left = _pending.Count;
                }

                problem = $"The audit trail could not be written to {store.Folder}: {ex.Message} " +
                          $"{(left == 1 ? "1 record is" : $"{left} records are")} waiting and will be written once the folder can be reached.";
                _retry.Change(RetryInterval, Timeout.InfiniteTimeSpan);
                break;
            }
        }

        Post(() =>
        {
            Problem = problem;
            PendingCount = left;
            if (wrote)
            {
                Written?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void Post(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}
