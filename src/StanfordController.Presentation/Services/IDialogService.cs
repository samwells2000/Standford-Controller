using StanfordController.Presentation.ViewModels;

namespace StanfordController.Presentation.Services;

/// <summary>Windows, message boxes and file pickers, implemented by the WPF application.</summary>
public interface IDialogService
{
    /// <summary>Shows the add/edit instrument dialog. Returns true if the user saved.</summary>
    bool EditProfile(ProfileEditorViewModel editor);

    /// <summary>Shows the network scan dialog. Returns true if the user chose an instrument to add.</summary>
    bool ShowDiscovery(DiscoveryViewModel discovery);

    bool Confirm(string title, string message);

    void ShowError(string title, string message);

    void ShowInfo(string title, string message);

    /// <param name="filter">A file dialog filter such as "DG645 settings (*.dg645.json)|*.dg645.json".</param>
    string? PickSaveFile(string title, string filter, string defaultFileName);

    string? PickOpenFile(string title, string filter);

    void CopyToClipboard(string text);
}
