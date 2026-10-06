using System.ComponentModel;
using System.Windows;
using StanfordController.App.Infrastructure;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App;

public partial class MainWindow : Window
{
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>Restores the size and position from last time, if that spot is still on a screen.</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ThemeManager.ApplyTitleBar(this);
        if (ViewModel?.WindowPlacement is not { } placement || placement.Width < MinWidth || placement.Height < MinHeight)
        {
            return;
        }

        var visible = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var titleBar = new Rect(placement.Left, placement.Top, placement.Width, 40);
        if (!visible.IntersectsWith(titleBar))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = Math.Min(placement.Width, visible.Width);
        Height = Math.Min(placement.Height, visible.Height);
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Remembers the window placement, then disconnects every instrument (front panels back to local).</summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete || ViewModel is not { } viewModel)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            viewModel.SaveWindowPlacement(new WindowPlacement
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = WindowState == WindowState.Maximized,
            });
        }

        if (viewModel.Instruments.All(i => i.Session is null))
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
