using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App.Dialogs;

public partial class DiscoveryWindow : Window
{
    public DiscoveryWindow()
    {
        InitializeComponent();

        // Stop any scan still running when the window closes.
        Closing += (_, _) => ViewModel?.CancelCommand.Execute(null);
    }

    private DiscoveryViewModel? ViewModel => DataContext as DiscoveryViewModel;

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.Selected is not null)
        {
            Close(true);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e) => OnAdd(sender, e);
}
