using System.Windows;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App.Dialogs;

public partial class ProfileEditorWindow : Window
{
    public ProfileEditorWindow()
    {
        InitializeComponent();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProfileEditorViewModel editor && editor.TrySave())
        {
            DialogResult = true;
        }
    }
}
