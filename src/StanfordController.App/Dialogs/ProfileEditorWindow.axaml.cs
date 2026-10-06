using Avalonia.Controls;
using Avalonia.Interactivity;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App.Dialogs;

public partial class ProfileEditorWindow : Window
{
    public ProfileEditorWindow()
    {
        InitializeComponent();
        Opened += (_, _) => NameBox.Focus();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProfileEditorViewModel editor && editor.TrySave())
        {
            Close(true);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
