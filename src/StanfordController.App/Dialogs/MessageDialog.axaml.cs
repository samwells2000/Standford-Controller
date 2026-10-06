using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using StanfordController.Presentation.Services;

namespace StanfordController.App.Dialogs;

public enum MessageKind
{
    Information,
    Question,
    Warning,
    Error,
}

/// <summary>A message box: an icon, the message, up to three buttons and an optional "remember" check box.</summary>
public partial class MessageDialog : Window
{
    private DialogChoice _choice = DialogChoice.Cancel;

    public MessageDialog()
    {
        InitializeComponent();
    }

    public static async Task<DialogAnswer> ShowAsync(Window? owner, MessageKind kind, string title, string message,
        string yesText, string? noText = null, bool showCancel = false, string? rememberText = null)
    {
        var dialog = new MessageDialog { Title = title };
        dialog.MessageText.Text = message;
        dialog.YesButton.Content = yesText;
        dialog.NoButton.Content = noText;
        dialog.NoButton.IsVisible = noText is not null;
        dialog.CancelButton.IsVisible = showCancel;
        dialog.CancelButton.IsCancel = showCancel;
        dialog.RememberBox.Content = rememberText;
        dialog.RememberBox.IsVisible = rememberText is not null;
        var (glyph, classes) = kind switch
        {
            MessageKind.Error => ("GlyphError", "error"),
            MessageKind.Warning => ("GlyphWarning", "warning"),
            _ => ("GlyphInfo", "info"),
        };
        dialog.IconPath.Data = dialog.FindResource(glyph) as Geometry;
        dialog.IconPath.Classes.Add(classes);

        if (owner is { IsVisible: true })
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Show();
            await closed.Task;
        }

        return new DialogAnswer(dialog._choice, dialog.RememberBox.IsChecked == true);
    }

    private void OnYes(object? sender, RoutedEventArgs e) => Finish(DialogChoice.Yes);

    private void OnNo(object? sender, RoutedEventArgs e) => Finish(DialogChoice.No);

    private void OnCancel(object? sender, RoutedEventArgs e) => Finish(DialogChoice.Cancel);

    private void Finish(DialogChoice choice)
    {
        _choice = choice;
        Close();
    }
}
