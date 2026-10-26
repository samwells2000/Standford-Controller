using StanfordController.Core;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.Presentation.Services;

/// <summary>A file type for open/save pickers, e.g. ("Tempo configuration", "*.tempo").</summary>
public sealed record FileFilter(string Name, params string[] Patterns)
{
    public static FileFilter AllFiles { get; } = new("All files", "*");
}

/// <summary>The answer to a three-way question such as "Save before closing?".</summary>
public enum DialogChoice
{
    Cancel,
    Yes,
    No,
}

/// <param name="Remember">Whether the "remember my choice" box was ticked (false if there was none).</param>
public sealed record DialogAnswer(DialogChoice Choice, bool Remember = false);

/// <summary>Windows, message boxes, file pickers and the clipboard, implemented by the desktop application.</summary>
public interface IDialogService
{
    /// <summary>Shows the add/edit instrument dialog. Returns true if the user saved.</summary>
    Task<bool> EditProfileAsync(ProfileEditorViewModel editor);

    /// <summary>Shows the network scan dialog. Returns true if the user chose an instrument to add.</summary>
    Task<bool> ShowDiscoveryAsync(DiscoveryViewModel discovery);

    /// <summary>Asks an OK/Cancel question. <paramref name="confirmText"/> labels the OK button.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK");

    /// <summary>
    /// Asks a two-way question with custom button labels, plus a Cancel button when <paramref name="showCancel"/>.
    /// Closing the window answers Cancel. <paramref name="rememberText"/>, when given, adds a "remember my choice" check box.
    /// </summary>
    Task<DialogAnswer> AskAsync(string title, string message, string yesText, string noText, string? rememberText = null, bool showCancel = true);

    /// <summary>
    /// Asks an OK/Cancel question under a table of the settings it would change, each with its value now and after.
    /// <paramref name="confirmText"/> labels the OK button.
    /// </summary>
    Task<bool> ConfirmChangesAsync(string title, string message, IReadOnlyList<SettingChange> changes, string confirmText);

    Task ShowErrorAsync(string title, string message);

    Task ShowInfoAsync(string title, string message);

    Task<string?> PickSaveFileAsync(string title, string defaultFileName, params FileFilter[] filters);

    Task<string?> PickOpenFileAsync(string title, params FileFilter[] filters);

    /// <summary>Asks for a folder. Returns null if the user cancelled.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Opens a folder in the system's file manager.</summary>
    Task OpenFolderAsync(string path);

    Task CopyToClipboardAsync(string text);

    /// <summary>Shows the About window (version, data folder, links).</summary>
    Task ShowAboutAsync();

    /// <summary>Opens the user guide, at <paramref name="topic"/> (a heading anchor) if given.</summary>
    void ShowHelp(string? topic = null);

    /// <summary>Opens the audit trail window, or brings it to the front.</summary>
    void ShowAuditTrail(AuditTrailViewModel auditTrail);
}
