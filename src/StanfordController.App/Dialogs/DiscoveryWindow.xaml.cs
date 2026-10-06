using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App.Dialogs;

public partial class DiscoveryWindow : Window
{
    public DiscoveryWindow()
    {
        InitializeComponent();
    }

    private DiscoveryViewModel? ViewModel => DataContext as DiscoveryViewModel;

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Selected is not null)
        {
            DialogResult = true;
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => OnAdd(sender, e);

    // Stop any scan still running when the window closes.
    private void OnClosing(object? sender, CancelEventArgs e) => ViewModel?.CancelCommand.Execute(null);
}
