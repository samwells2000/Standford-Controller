using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace StanfordController.App.Views;

public partial class DelaysView : UserControl
{
    public DelaysView()
    {
        InitializeComponent();
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Diagram.ZoomIn();

    private void OnZoomOut(object? sender, RoutedEventArgs e) => Diagram.ZoomOut();

    private void OnFit(object? sender, RoutedEventArgs e) => Diagram.Fit();

    private void OnClearCursors(object? sender, RoutedEventArgs e) => Diagram.ClearCursors();

    private async void OnSaveImage(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save timing diagram",
            SuggestedFileName = $"Timing {DateTime.Now:yyyy-MM-dd HHmm}.png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
            ShowOverwritePrompt = true,
        });
        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            Diagram.SaveImage(stream);
        }
        catch (Exception ex)
        {
            await Dialogs.MessageDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, Dialogs.MessageKind.Error, "Save timing diagram",
                $"Could not save the picture:\n{ex.Message}", "OK");
        }
    }
}
