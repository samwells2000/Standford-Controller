using System.Windows;
using Microsoft.Win32;
using StanfordController.App.Dialogs;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App.Services;

public sealed class DialogService : IDialogService
{
    public Window? Owner { get; set; }

    public bool EditProfile(ProfileEditorViewModel editor) =>
        new ProfileEditorWindow { DataContext = editor, Owner = Owner }.ShowDialog() == true;

    public bool ShowDiscovery(DiscoveryViewModel discovery) =>
        new DiscoveryWindow { DataContext = discovery, Owner = Owner }.ShowDialog() == true;

    public bool Confirm(string title, string message) =>
        Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public void ShowError(string title, string message) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string title, string message) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public string? PickSaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, AddExtension = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public void ShowAbout() => new AboutWindow { Owner = Owner }.ShowDialog();

    public void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            ShowError("Copy", $"Could not copy to the clipboard: {ex.Message}");
        }
    }

    private MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        Owner is { IsVisible: true }
            ? MessageBox.Show(Owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
}
