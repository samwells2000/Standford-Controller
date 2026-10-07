using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using StanfordController.App.Dialogs;
using StanfordController.Presentation.Help;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App.Services;

public sealed class DialogService : IDialogService
{
    private readonly AppPaths _paths;
    private HelpWindow? _help;
    private AuditTrailWindow? _auditTrail;

    public DialogService(AppPaths paths)
    {
        _paths = paths;
        Current = this;
    }

    /// <summary>The service used by the main window (for reporting unexpected errors).</summary>
    public static DialogService? Current { get; private set; }

    public Window? Owner { get; set; }

    /// <summary>The help window, if it is open.</summary>
    public HelpWindow? Help => _help;

    public async Task<bool> EditProfileAsync(ProfileEditorViewModel editor) =>
        Owner is null ? false : await new ProfileEditorWindow { DataContext = editor }.ShowDialog<bool>(Owner);

    public async Task<bool> ShowDiscoveryAsync(DiscoveryViewModel discovery) =>
        Owner is null ? false : await new DiscoveryWindow { DataContext = discovery }.ShowDialog<bool>(Owner);

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK") =>
        (await MessageDialog.ShowAsync(Owner, MessageKind.Question, title, message, confirmText, showCancel: true)).Choice == DialogChoice.Yes;

    public Task<DialogAnswer> AskAsync(string title, string message, string yesText, string noText, string? rememberText = null, bool showCancel = true) =>
        MessageDialog.ShowAsync(Owner, MessageKind.Question, title, message, yesText, noText, showCancel, rememberText);

    public Task ShowErrorAsync(string title, string message) =>
        MessageDialog.ShowAsync(Owner, MessageKind.Error, title, message, "OK");

    public Task ShowInfoAsync(string title, string message) =>
        MessageDialog.ShowAsync(Owner, MessageKind.Information, title, message, "OK");

    public async Task<string?> PickSaveFileAsync(string title, string defaultFileName, params FileFilter[] filters)
    {
        if (Owner?.StorageProvider is not { CanSave: true } storage)
        {
            await ReportNoFileDialogAsync(title);
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultFileName,
            FileTypeChoices = ToFileTypes(filters),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenFileAsync(string title, params FileFilter[] filters)
    {
        if (Owner?.StorageProvider is not { CanOpen: true } storage)
        {
            await ReportNoFileDialogAsync(title);
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ToFileTypes(filters),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner?.StorageProvider is not { CanPickFolder: true } storage)
        {
            await ReportNoFileDialogAsync(title);
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task OpenFolderAsync(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (Owner?.Launcher is { } launcher)
            {
                await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync("Open folder", $"{path} could not be opened: {ex.Message}");
        }
    }

    public async Task CopyToClipboardAsync(string text)
    {
        try
        {
            if (Owner?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Copy", $"Could not copy to the clipboard: {ex.Message}");
        }
    }

    public async Task ShowAboutAsync()
    {
        if (Owner is not null)
        {
            await new AboutWindow(_paths).ShowDialog(Owner);
        }
    }

    public void ShowHelp(string? topic = null)
    {
        if (_help is null)
        {
            _help = new HelpWindow();
            _help.Closed += (_, _) => _help = null;
            if (Owner is not null)
            {
                _help.Show(Owner);
            }
            else
            {
                _help.Show();
            }
        }
        else
        {
            _help.Activate();
        }

        _help.Navigate(topic ?? HelpTopics.GettingStarted);
    }

    public void ShowAuditTrail(AuditTrailViewModel auditTrail)
    {
        if (_auditTrail is not null)
        {
            auditTrail.Detach();
            _auditTrail.ViewModel?.Reload();
            _auditTrail.Activate();
            return;
        }

        _auditTrail = new AuditTrailWindow { DataContext = auditTrail };
        _auditTrail.Closed += (_, _) => _auditTrail = null;
        if (Owner is not null)
        {
            _auditTrail.Show(Owner);
        }
        else
        {
            _auditTrail.Show();
        }
    }

    /// <summary>The audit trail window, if it is open.</summary>
    public AuditTrailWindow? AuditTrail => _auditTrail;

    // On Linux the file dialogs come from the desktop's portal service or GTK; a bare window manager may have neither.
    private Task ReportNoFileDialogAsync(string title) => ShowErrorAsync(title,
        "This system has no file dialog available. On Linux, install xdg-desktop-portal (with the portal for your desktop) or GTK 3, then try again.");

    private static List<FilePickerFileType> ToFileTypes(FileFilter[] filters) =>
        filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList();
}
