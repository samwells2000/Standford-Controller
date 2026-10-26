using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using StanfordController.Core;
using StanfordController.Presentation.Services;

namespace StanfordController.App.Dialogs;

public enum MessageKind
{
    Information,
    Question,
    Warning,
    Error,
}

/// <summary>
/// A message box: an icon, the message, up to three buttons, an optional "remember" check box and an optional table of
/// the settings an action would change.
/// </summary>
public partial class MessageDialog : Window
{
    private DialogChoice _choice = DialogChoice.Cancel;

    public MessageDialog()
    {
        InitializeComponent();
    }

    public static async Task<DialogAnswer> ShowAsync(Window? owner, MessageKind kind, string title, string message,
        string yesText, string? noText = null, bool showCancel = false, string? rememberText = null, IReadOnlyList<SettingChange>? changes = null)
    {
        var dialog = new MessageDialog { Title = title };
        if (changes is { Count: > 0 })
        {
            // Wide enough for "Delay A   T0 + 1.000 µs   A + 2.5 µs" without wrapping.
            dialog.Width = 620;
            dialog.ChangesList.ItemsSource = changes;
            dialog.ChangesPanel.IsVisible = true;
        }

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
