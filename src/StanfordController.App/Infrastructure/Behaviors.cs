using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StanfordController.App.Infrastructure;

/// <summary>
/// Text box behaviours for instrument fields: Enter or leaving the field runs <c>CommitCommand</c>,
/// Escape runs <c>RevertCommand</c>, and the text is selected when the field gains keyboard focus.
/// </summary>
public static class FieldBehavior
{
    public static readonly DependencyProperty CommitCommandProperty = DependencyProperty.RegisterAttached(
        "CommitCommand", typeof(ICommand), typeof(FieldBehavior), new PropertyMetadata(null, OnCommitCommandChanged));

    public static readonly DependencyProperty RevertCommandProperty = DependencyProperty.RegisterAttached(
        "RevertCommand", typeof(ICommand), typeof(FieldBehavior), new PropertyMetadata(null));

    public static ICommand? GetCommitCommand(DependencyObject element) => (ICommand?)element.GetValue(CommitCommandProperty);

    public static void SetCommitCommand(DependencyObject element, ICommand? value) => element.SetValue(CommitCommandProperty, value);

    public static ICommand? GetRevertCommand(DependencyObject element) => (ICommand?)element.GetValue(RevertCommandProperty);

    public static void SetRevertCommand(DependencyObject element, ICommand? value) => element.SetValue(RevertCommandProperty, value);

    private static void OnCommitCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.PreviewKeyDown -= OnPreviewKeyDown;
        box.LostKeyboardFocus -= OnLostKeyboardFocus;
        box.GotKeyboardFocus -= OnGotKeyboardFocus;
        box.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        if (e.NewValue is not null)
        {
            box.PreviewKeyDown += OnPreviewKeyDown;
            box.LostKeyboardFocus += OnLostKeyboardFocus;
            box.GotKeyboardFocus += OnGotKeyboardFocus;
            box.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        switch (e.Key)
        {
            case Key.Enter:
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                Run(GetCommitCommand(box));
                box.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape:
                Run(GetRevertCommand(box));
                box.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Run(GetCommitCommand(box));
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).SelectAll();

    // Without this, the click that focuses the box would immediately clear the selection.
    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;
        if (!box.IsKeyboardFocusWithin)
        {
            box.Focus();
            e.Handled = true;
        }
    }

    private static void Run(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }
}

/// <summary>Keeps a list scrolled to its newest item while the user is already at the bottom.</summary>
public static class AutoScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(AutoScrollBehavior), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list || e.NewValue is not true)
        {
            return;
        }

        void Attach()
        {
            if (list.Items is INotifyCollectionChanged items)
            {
                items.CollectionChanged += (_, args) =>
                {
                    if (args.Action != NotifyCollectionChangedAction.Add || list.Items.Count == 0)
                    {
                        return;
                    }

                    var viewer = FindChild<ScrollViewer>(list);
                    var atBottom = viewer is null || viewer.VerticalOffset >= viewer.ScrollableHeight - 2;
                    if (atBottom)
                    {
                        list.ScrollIntoView(list.Items[^1]);
                    }
                };
            }
        }

        if (list.IsLoaded)
        {
            Attach();
        }
        else
        {
            list.Loaded += (_, _) => Attach();
        }
    }

    internal static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}

/// <summary>Runs a command with the clicked item when a list item is double-clicked.</summary>
public static class DoubleClickBehavior
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(DoubleClickBehavior), new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list)
        {
            return;
        }

        list.MouseDoubleClick -= OnDoubleClick;
        if (e.NewValue is not null)
        {
            list.MouseDoubleClick += OnDoubleClick;
        }
    }

    private static void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var list = (ItemsControl)sender;
        var container = ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource);
        if (container is null)
        {
            return;
        }

        var item = list.ItemContainerGenerator.ItemFromContainer(container);
        var command = GetCommand(list);
        if (command?.CanExecute(item) == true)
        {
            command.Execute(item);
            e.Handled = true;
        }
    }
}
