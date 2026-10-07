using Avalonia.Controls;
using Avalonia.Input;
using StanfordController.App.Services;
using StanfordController.Presentation.Help;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App.Dialogs;

/// <summary>The audit trail: a month's records, filtered and searchable, with the selected record in full below.</summary>
public partial class AuditTrailWindow : Window
{
    public AuditTrailWindow()
    {
        InitializeComponent();
        Closed += (_, _) => ViewModel?.Detach();
    }

    public AuditTrailViewModel? ViewModel => DataContext as AuditTrailViewModel;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.F5:
                ViewModel?.Reload();
                break;
            case Key.F1:
                DialogService.Current?.ShowHelp(HelpTopics.AuditTrail);
                break;
            case Key.F when e.KeyModifiers == KeyModifiers.Control:
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
