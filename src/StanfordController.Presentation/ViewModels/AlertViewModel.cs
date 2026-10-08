using System.Windows.Input;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.ViewModels;

public enum AlertSeverity
{
    Warning,
    Error,
}

/// <summary>Something wrong with an instrument: a fault it reported, or trouble with the connection.</summary>
public sealed record InstrumentAlert(string Kind, AlertSeverity Severity, string Title, string Message)
{
    public const string NotResponding = "not-responding";
    public const string ConnectionLost = "connection-lost";
    public const string PllUnlocked = "pll-unlocked";
    public const string RubidiumUnlocked = "rb-unlocked";
    public const string TimebaseChanged = "timebase-changed";
}

/// <summary>
/// An alert in the bar at the top of the main window. It stays until dismissed, or until the problem clears by
/// itself (the instrument answers again, or reconnects). The same problem happening again updates the alert
/// rather than adding another.
/// </summary>
public sealed class AlertViewModel : ObservableObject
{
    private string _message;
    private DateTime _lastSeen;
    private int _count = 1;

    public AlertViewModel(InstrumentItemViewModel item, InstrumentAlert alert, Action<AlertViewModel> dismiss, Action<InstrumentItemViewModel> show)
    {
        Item = item;
        Kind = alert.Kind;
        Severity = alert.Severity;
        Title = $"{item.Name}: {alert.Title}";
        _message = alert.Message;
        FirstSeen = _lastSeen = DateTime.Now;
        DismissCommand = new RelayCommand(() => dismiss(this));
        ShowCommand = new RelayCommand(() => show(item));
        ReconnectCommand = new RelayCommand(() =>
        {
            show(item);
            item.ConnectCommand.Execute(null);
        });
    }

    public InstrumentItemViewModel Item { get; }

    public string Kind { get; }

    public AlertSeverity Severity { get; }

    public bool IsError => Severity == AlertSeverity.Error;

    public string Title { get; }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public DateTime FirstSeen { get; }

    public DateTime LastSeen
    {
        get => _lastSeen;
        private set
        {
            if (SetProperty(ref _lastSeen, value))
            {
                OnPropertyChanged(nameof(TimeText));
            }
        }
    }

    public int Count
    {
        get => _count;
        private set => SetProperty(ref _count, value);
    }

    /// <summary>"14:03:12", or "3 times since 14:03:12, last at 14:05:40".</summary>
    public string TimeText => _count == 1
        ? $"{FirstSeen:HH:mm:ss}"
        : $"{_count} times since {FirstSeen:HH:mm:ss}, last at {_lastSeen:HH:mm:ss}";

    /// <summary>Only a lost connection offers to reconnect.</summary>
    public bool CanReconnect => Kind == InstrumentAlert.ConnectionLost;

    public ICommand DismissCommand { get; }

    public ICommand ShowCommand { get; }

    public ICommand ReconnectCommand { get; }

    /// <summary>The same problem has happened again.</summary>
    public void Repeat(InstrumentAlert alert)
    {
        Message = alert.Message;
        Count++;
        LastSeen = DateTime.Now;
    }
}
