using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.ViewModels;

/// <summary>
/// The All channels page: every delay channel of every connected instrument, with its label, so the channel that
/// drives a particular piece of equipment can be found without opening each instrument in turn.
/// </summary>
public sealed class ChannelOverviewViewModel : ObservableObject
{
    private readonly ObservableCollection<InstrumentItemViewModel> _instruments;
    private readonly Action<InstrumentItemViewModel> _show;
    private readonly HashSet<InstrumentItemViewModel> _watched = [];
    private readonly HashSet<ChannelDelayViewModel> _watchedChannels = [];
    private string _filter = string.Empty;

    /// <param name="show">Selects an instrument in the main window.</param>
    public ChannelOverviewViewModel(ObservableCollection<InstrumentItemViewModel> instruments, Action<InstrumentItemViewModel> show)
    {
        _instruments = instruments;
        _show = show;
        _instruments.CollectionChanged += OnInstrumentsChanged;
        ClearFilterCommand = new RelayCommand(() => Filter = string.Empty);
        Rebuild();
    }

    /// <summary>One group per connected instrument, in the order of the instrument list.</summary>
    public ObservableCollection<ChannelOverviewGroup> Groups { get; } = [];

    /// <summary>Shows only channels whose label, channel letter or instrument contains this text.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? string.Empty))
            {
                ApplyFilter();
                OnPropertyChanged(nameof(IsFiltered));
            }
        }
    }

    public bool IsFiltered => _filter.Trim().Length > 0;

    public ICommand ClearFilterCommand { get; }

    public bool HasGroups => Groups.Count > 0;

    /// <summary>True when instruments are connected but none of their channels matches the filter.</summary>
    public bool NothingMatches => HasGroups && Groups.All(g => !g.IsVisible);

    /// <summary>e.g. "2 instruments connected, 16 channels, 5 labelled. 1 more instrument is not connected."</summary>
    public string Summary
    {
        get
        {
            var channels = Groups.SelectMany(g => g.Channels).ToList();
            var text = Groups.Count == 0
                ? "No instruments are connected."
                : $"{Plural(Groups.Count, "instrument")} connected, {channels.Count} channels, {channels.Count(c => c.HasLabel)} labelled.";
            var others = _instruments.Count - Groups.Count;
            return others == 0 ? text : $"{text} {Plural(others, "more instrument")} {(others == 1 ? "is" : "are")} not connected.";
        }
    }

    private void OnInstrumentsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnInstrumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // "" after Instrument ▸ Edit, which may rename it.
        if (e.PropertyName is nameof(InstrumentItemViewModel.Session) or "")
        {
            Rebuild();
        }
    }

    private void OnChannelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelDelayViewModel.ChannelLabel))
        {
            ApplyFilter();
            OnPropertyChanged(nameof(Summary));
        }
    }

    private void Rebuild()
    {
        foreach (var item in _watched.Except(_instruments).ToList())
        {
            item.PropertyChanged -= OnInstrumentPropertyChanged;
            _watched.Remove(item);
        }

        foreach (var item in _instruments.Where(_watched.Add))
        {
            item.PropertyChanged += OnInstrumentPropertyChanged;
        }

        var channels = _instruments.Select(i => i.Session).OfType<InstrumentSessionViewModel>().SelectMany(s => s.Channels).ToHashSet();
        foreach (var channel in _watchedChannels.Except(channels).ToList())
        {
            channel.PropertyChanged -= OnChannelPropertyChanged;
            _watchedChannels.Remove(channel);
        }

        foreach (var channel in channels.Where(_watchedChannels.Add))
        {
            channel.PropertyChanged += OnChannelPropertyChanged;
        }

        Groups.Clear();
        foreach (var item in _instruments.Where(i => i.Session is not null))
        {
            Groups.Add(new ChannelOverviewGroup(item, item.Session!, new RelayCommand(() => _show(item))));
        }

        ApplyFilter();
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(Summary));
    }

    private void ApplyFilter()
    {
        var words = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var group in Groups)
        {
            group.ApplyFilter(words);
        }

        OnPropertyChanged(nameof(NothingMatches));
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

/// <summary>A connected instrument on the All channels page, with the channels that match the filter.</summary>
public sealed class ChannelOverviewGroup : ObservableObject
{
    private IReadOnlyList<ChannelDelayViewModel> _visibleChannels;

    public ChannelOverviewGroup(InstrumentItemViewModel instrument, InstrumentSessionViewModel session, ICommand showCommand)
    {
        Instrument = instrument;
        Session = session;
        ShowCommand = showCommand;
        _visibleChannels = session.Channels;
    }

    public InstrumentItemViewModel Instrument { get; }

    public InstrumentSessionViewModel Session { get; }

    public IReadOnlyList<ChannelDelayViewModel> Channels => Session.Channels;

    public IReadOnlyList<ChannelDelayViewModel> VisibleChannels
    {
        get => _visibleChannels;
        private set
        {
            if (SetProperty(ref _visibleChannels, value))
            {
                OnPropertyChanged(nameof(IsVisible));
            }
        }
    }

    public bool IsVisible => _visibleChannels.Count > 0;

    /// <summary>e.g. "Laser room  ·  serial s/n001234".</summary>
    public string Details => string.Join("  ·  ",
        new[] { Instrument.Subtitle, Session.SerialNumber.Length > 0 ? $"serial {Session.SerialNumber}" : null }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Selects this instrument in the main window.</summary>
    public ICommand ShowCommand { get; }

    /// <summary>
    /// Keeps the channels that contain every word, looking in the label, the channel letter, its output and the
    /// instrument's name and location. A word that matches only the instrument keeps all of its channels.
    /// </summary>
    public void ApplyFilter(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            VisibleChannels = Channels;
            return;
        }

        var instrument = $"{Instrument.Name} {Instrument.Profile.Location}";
        VisibleChannels = Channels
            .Where(c => words.All(w => Contains(c.ChannelLabel, w) || Contains(instrument, w)
                                       || string.Equals(c.Name, w, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(c.Output.ToString(), w, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        static bool Contains(string? text, string word) => text?.Contains(word, StringComparison.CurrentCultureIgnoreCase) == true;
    }
}
