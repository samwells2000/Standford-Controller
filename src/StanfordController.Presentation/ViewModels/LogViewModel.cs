using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using StanfordController.Core;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.ViewModels;

public sealed record LogEntry(DateTime Timestamp, CommunicationDirection Direction, string Text)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

    public string Marker => Direction switch
    {
        CommunicationDirection.Sent => "→",
        CommunicationDirection.Received => "←",
        CommunicationDirection.Error => "✕",
        _ => "•",
    };

    public override string ToString() => $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff}  {Marker}  {Text}";
}

/// <summary>
/// Communication log for one instrument. Safe to append from any thread: entries are marshalled to the
/// thread that created the log (the UI thread).
/// </summary>
public sealed class LogViewModel : ObservableObject
{
    public const int MaxEntries = 5000;

    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private bool _showTraffic = true;

    public LogViewModel()
    {
        ClearCommand = new RelayCommand(Entries.Clear);
    }

    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>When false, only connection events and errors are shown.</summary>
    public bool ShowTraffic
    {
        get => _showTraffic;
        set => SetProperty(ref _showTraffic, value);
    }

    public ICommand ClearCommand { get; }

    public event EventHandler<LogEntry>? EntryAdded;

    public void Add(CommunicationDirection direction, string text)
    {
        var entry = new LogEntry(DateTime.Now, direction, text);
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            Append(entry);
        }
        else
        {
            _uiContext.Post(_ => Append(entry), null);
        }
    }

    public string ExportText()
    {
        var builder = new StringBuilder();
        foreach (var entry in Entries)
        {
            builder.AppendLine(entry.ToString());
        }

        return builder.ToString();
    }

    private void Append(LogEntry entry)
    {
        if (!_showTraffic && entry.Direction is CommunicationDirection.Sent or CommunicationDirection.Received)
        {
            return;
        }

        Entries.Add(entry);
        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }

        EntryAdded?.Invoke(this, entry);
    }
}
