using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using StanfordController.Presentation.Help;

namespace StanfordController.App.Dialogs;

/// <summary>The user guide: contents and search on the left, one topic at a time on the right.</summary>
public partial class HelpWindow : Window
{
    private readonly HelpViewModel _help = new(HelpGuide.LoadEmbedded());

    public HelpWindow()
    {
        InitializeComponent();
        DataContext = _help;
        _help.Navigated += (_, _) => Dispatcher.UIThread.Post(ScrollToAnchor, DispatcherPriority.Background);
        Viewer.LinkClicked += (_, target) =>
        {
            if (_help.ActivateLink(target) is { } url)
            {
                Launcher.LaunchUriAsync(new Uri(url));
            }
        };
    }

    public HelpViewModel ViewModel => _help;

    /// <summary>Shows the topic containing <paramref name="anchor"/>, or the first topic if there is none.</summary>
    public void Navigate(string anchor)
    {
        if (!_help.Navigate(anchor) && _help.Guide.Topics.Count > 0)
        {
            _help.Navigate(_help.Guide.Topics[0].Anchor);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    /// <summary>Scrolls so the heading being navigated to is at the top (or to the top for a whole topic).</summary>
    private void ScrollToAnchor()
    {
        if (_help.CurrentAnchor is not { } anchor || _help.CurrentTopic?.Anchor == anchor || Viewer.FindAnchor(anchor) is not { } heading)
        {
            Scroller.Offset = new Vector(0, 0);
            return;
        }

        if (heading.TranslatePoint(new Point(0, 0), Viewer) is { } position)
        {
            Scroller.Offset = new Vector(0, Math.Max(0, position.Y + Viewer.Margin.Top - 12));
        }
    }
}
