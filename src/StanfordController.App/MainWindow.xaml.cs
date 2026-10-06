using System.ComponentModel;
using System.Windows;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App;

public partial class MainWindow : Window
{
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Disconnects every instrument (returning front panels to local) before closing.</summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete || DataContext is not MainViewModel viewModel || viewModel.Instruments.All(i => i.Session is null))
        {
            return;
        }

        e.Cancel = true;
        IsEnabled = false;
        try
        {
            await viewModel.ShutdownAsync();
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }
}
