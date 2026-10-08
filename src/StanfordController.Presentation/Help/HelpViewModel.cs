using System.Collections.ObjectModel;
using System.Windows.Input;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.Help;

/// <summary>A line in the help window's contents list: a topic, or a section indented under it.</summary>
public sealed record HelpContentsEntry(HelpTopic Topic, HelpSection? Section)
{
    public string Title => Section?.Title ?? Topic.Title;

    public bool IsSection => Section is not null;

    public string Anchor => Section?.Anchor ?? Topic.Anchor;
}

/// <summary>The help window: contents, search, the topic on show, and back/forward history.</summary>
public sealed class HelpViewModel : ObservableObject
{
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private string _searchText = string.Empty;
    private HelpContentsEntry? _selectedEntry;
    private HelpTopic? _currentTopic;
    private string? _currentAnchor;
    private bool _syncingSelection;

    public HelpViewModel(HelpGuide guide)
    {
        Guide = guide;
        BackCommand = new RelayCommand(() => Step(-1), () => _historyIndex > 0);
        ForwardCommand = new RelayCommand(() => Step(1), () => _historyIndex < _history.Count - 1);
        RebuildContents();
        if (guide.Topics.Count > 0)
        {
            Navigate(guide.Topics[0].Anchor);
        }
    }

    /// <summary>Raised after every navigation, including to the same place, so the view can scroll.</summary>
    public event EventHandler? Navigated;

    /// <summary>Raised when a link to the quick reference card is followed, to open the copy built into the program.</summary>
    public event EventHandler? QuickReferenceRequested;

    public HelpGuide Guide { get; }

    public ObservableCollection<HelpContentsEntry> Contents { get; } = [];

    /// <summary>Filters the contents to topics containing every word typed.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                RebuildContents();
            }
        }
    }

    public string? SearchSummary => string.IsNullOrWhiteSpace(_searchText)
        ? null
        : Contents.Count == 0 ? "No topics match." : $"{Contents.Count} topic{(Contents.Count == 1 ? "" : "s")} match.";

    public HelpContentsEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value) && value is not null && !_syncingSelection)
            {
                Navigate(value.Anchor);
            }
        }
    }

    public HelpTopic? CurrentTopic
    {
        get => _currentTopic;
        private set => SetProperty(ref _currentTopic, value);
    }

    /// <summary>The heading to scroll to: a section anchor, or the topic's own anchor for the top.</summary>
    public string? CurrentAnchor
    {
        get => _currentAnchor;
        private set => SetProperty(ref _currentAnchor, value);
    }

    public ICommand BackCommand { get; }

    public ICommand ForwardCommand { get; }

    /// <summary>Shows the topic containing <paramref name="anchor"/>. Returns false if there is no such heading.</summary>
    public bool Navigate(string anchor) => Show(anchor, addToHistory: true);

    /// <summary>
    /// Follows a link in the guide. Links within the guide navigate here and return null, as does the quick reference
    /// card (see <see cref="QuickReferenceRequested"/>); anything else returns the web address to open in the browser.
    /// </summary>
    public string? ActivateLink(string target)
    {
        if (target.StartsWith('#'))
        {
            Navigate(target[1..]);
            return null;
        }

        if (target.TrimStart('.', '/') is HelpGuide.QuickReferenceFile or "docs/" + HelpGuide.QuickReferenceFile)
        {
            QuickReferenceRequested?.Invoke(this, EventArgs.Empty);
            return null;
        }

        return AppInfo.ResolveExternalLink(target);
    }

    private bool Show(string anchor, bool addToHistory)
    {
        anchor = anchor.TrimStart('#');
        if (Guide.FindTopic(anchor) is not { } topic)
        {
            return false;
        }

        if (addToHistory)
        {
            if (_historyIndex < _history.Count - 1)
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            }

            if (_historyIndex < 0 || _history[_historyIndex] != anchor)
            {
                _history.Add(anchor);
                _historyIndex = _history.Count - 1;
            }
        }

        CurrentTopic = topic;
        CurrentAnchor = anchor;
        SyncSelection();
        (BackCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
        (ForwardCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
        Navigated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Step(int delta)
    {
        var index = _historyIndex + delta;
        if (index < 0 || index >= _history.Count)
        {
            return;
        }

        _historyIndex = index;
        Show(_history[index], addToHistory: false);
    }

    private void RebuildContents()
    {
        var words = _searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Contents.Clear();
        foreach (var topic in Guide.Topics)
        {
            var searchable = topic.Title + "\n" + topic.SearchText;
            if (words.Length > 0 && !words.All(w => searchable.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Contents.Add(new HelpContentsEntry(topic, null));
            if (words.Length == 0)
            {
                foreach (var section in topic.Sections)
                {
                    Contents.Add(new HelpContentsEntry(topic, section));
                }
            }
        }

        OnPropertyChanged(nameof(SearchSummary));
        SyncSelection();
    }

    private void SyncSelection()
    {
        _syncingSelection = true;
        try
        {
            SelectedEntry = Contents.FirstOrDefault(e => e.Anchor == _currentAnchor)
                            ?? Contents.FirstOrDefault(e => !e.IsSection && e.Topic == _currentTopic);
        }
        finally
        {
            _syncingSelection = false;
        }
    }
}
