using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using StanfordController.App.Infrastructure;
using StanfordController.Presentation.Help;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private MainViewModel? _alertSource;

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_alertSource is not null)
        {
            _alertSource.AlertRaised -= OnAlertRaised;
        }

        _alertSource = ViewModel;
        if (_alertSource is not null)
        {
            _alertSource.AlertRaised += OnAlertRaised;
        }
    }

    /// <summary>A new fault or a lost connection: ask for the user's attention, with a sound if that is turned on.</summary>
    private void OnAlertRaised(object? sender, AlertViewModel alert)
    {
        if (ViewModel?.AlertSound == true)
        {
            AlertSound.Play(alert.IsError);
        }

        // On Windows this flashes the taskbar button if another program has the focus.
        if (!IsActive)
        {
            Activate();
        }
    }

    /// <summary>Puts the window back where it was last time, if that spot is still on a screen.</summary>
    public void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is null || placement.Width < MinWidth || placement.Height < MinHeight)
        {
            return;
        }

        var position = new PixelPoint((int)placement.Left, (int)placement.Top);
        var titleBar = new PixelRect(position, new PixelSize((int)placement.Width, 40));
        if (Screens.All.Count > 0 && !Screens.All.Any(s => s.WorkingArea.Intersects(titleBar)))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = position;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>F1 opens the user guide at the part of the window that has the keyboard focus.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.F1)
        {
            ShowContextHelp();
            e.Handled = true;
        }
    }

    internal string? CurrentHelpTopic()
    {
        if (FocusManager?.GetFocusedElement() is Control focused && HelpContext.GetTopic(focused) is { } topic)
        {
            return topic;
        }

        // Nothing focused inside a page: use the page on show.
        if (this.GetVisualDescendants().OfType<TabControl>().FirstOrDefault() is { SelectedContent: Control page }
            && HelpContext.GetTopic(page) is { } pageTopic)
        {
            return pageTopic;
        }

        return ViewModel?.SelectedInstrument switch
        {
            null => HelpTopics.GettingStarted,
            { IsConnected: false } => HelpTopics.Connecting,
            _ => null,
        };
    }

    private void ShowContextHelp() => ViewModel?.HelpCommand.Execute(CurrentHelpTopic());

    private void OnContextHelp(object? sender, RoutedEventArgs e) => ShowContextHelp();

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenReleases(object? sender, RoutedEventArgs e) => Launcher?.LaunchUriAsync(new Uri(AppInfo.ReleasesUrl));

    /// <summary>Opens the two-sided quick reference card in the PDF viewer, ready to print.</summary>
    private async void OnQuickReference(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "Tempo-Quick-Reference.pdf");
            await using (var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("Tempo.QuickReference.pdf")
                                        ?? throw new FileNotFoundException("The quick reference card is missing from this build."))
            await using (var file = File.Create(path))
            {
                await resource.CopyToAsync(file);
            }

            if (Launcher is null || !await Launcher.LaunchFileInfoAsync(new FileInfo(path)))
            {
                ViewModel!.StatusMessage = $"The quick reference card is saved at {path}; open it with a PDF viewer.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.StatusMessage = $"The quick reference card could not be opened: {ex.Message}";
            }
        }
    }

    /// <summary>Offers to save unsaved setups, remembers the window placement, then disconnects every instrument.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || ViewModel is not { } viewModel || e.Cancel)
        {
            return;
        }

        e.Cancel = true;
        _ = CloseAsync(viewModel);
    }

    private async Task CloseAsync(MainViewModel viewModel)
    {
        if (!await viewModel.ConfirmCloseAsync())
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        viewModel.SaveWindowPlacement(new WindowPlacement
        {
            Left = Position.X,
            Top = Position.Y,
            Width = maximized ? viewModel.WindowPlacement?.Width ?? Width : Width,
            Height = maximized ? viewModel.WindowPlacement?.Height ?? Height : Height,
            Maximized = maximized,
        });

        IsEnabled = false;
        try
        {
            await viewModel.ShutdownAsync();
        }
        finally
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
