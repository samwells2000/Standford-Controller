using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace StanfordController.App.Infrastructure;

/// <summary>
/// Value fields: Enter (or leaving the field) applies the edit, Esc puts the instrument's value back,
/// and focusing a field selects its text so a new value can simply be typed over it.
/// </summary>
public static class FieldBehavior
{
    public static readonly AttachedProperty<ICommand?> CommitCommandProperty =
        AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("CommitCommand", typeof(FieldBehavior));

    public static readonly AttachedProperty<ICommand?> RevertCommandProperty =
        AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("RevertCommand", typeof(FieldBehavior));

    private static readonly AttachedProperty<bool> AttachedProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Attached", typeof(FieldBehavior));

    static FieldBehavior()
    {
        CommitCommandProperty.Changed.AddClassHandler<TextBox>((box, _) => Attach(box));
        RevertCommandProperty.Changed.AddClassHandler<TextBox>((box, _) => Attach(box));
    }

    public static ICommand? GetCommitCommand(TextBox box) => box.GetValue(CommitCommandProperty);

    public static void SetCommitCommand(TextBox box, ICommand? value) => box.SetValue(CommitCommandProperty, value);

    public static ICommand? GetRevertCommand(TextBox box) => box.GetValue(RevertCommandProperty);

    public static void SetRevertCommand(TextBox box, ICommand? value) => box.SetValue(RevertCommandProperty, value);

    private static void Attach(TextBox box)
    {
        if (box.GetValue(AttachedProperty))
        {
            return;
        }

        box.SetValue(AttachedProperty, true);
        box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) => Execute(GetCommitCommand(box));
        box.GotFocus += (_, e) =>
        {
            if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional)
            {
                Dispatcher.UIThread.Post(box.SelectAll);
            }
        };
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (e.Key == Key.Enter && !box.AcceptsReturn)
        {
            Execute(GetCommitCommand(box));
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Execute(GetRevertCommand(box));
            box.SelectAll();
            e.Handled = true;
        }
    }

    private static void Execute(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }
}

/// <summary>
/// Clicking somewhere that cannot take the keyboard focus (a page's background, a label, the timing diagram)
/// ends text editing: the field loses the focus, which applies its value, and the caret goes away. Without this,
/// a field keeps the focus until Enter is pressed or another control is clicked.
/// </summary>
public static class ClickAwayBehavior
{
    /// <summary>Applies to every window, dialogs included. Call once at start-up.</summary>
    public static void Install() =>
        InputElement.PointerPressedEvent.AddClassHandler<Window>(OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

    /// <summary>True if pressing <paramref name="source"/> ends editing: neither it nor a parent can take the focus.</summary>
    public static bool EndsEditing(Visual? source)
    {
        for (var element = source; element is not null and not TopLevel; element = element.GetVisualParent())
        {
            if (element is InputElement { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true })
            {
                // That control takes the focus itself, which ends editing in the usual way.
                return false;
            }
        }

        return true;
    }

    private static void OnPointerPressed(Window window, PointerPressedEventArgs e)
    {
        if (window.FocusManager is { } focus && focus.GetFocusedElement() is TextBox && EndsEditing(e.Source as Visual))
        {
            focus.Focus(null);
        }
    }
}

/// <summary>List helpers: keep the newest item in view, and run a command on double-click.</summary>
public static class ListBehavior
{
    public static readonly AttachedProperty<bool> AutoScrollProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("AutoScroll", typeof(ListBehavior));

    public static readonly AttachedProperty<ICommand?> DoubleTapCommandProperty =
        AvaloniaProperty.RegisterAttached<ListBox, ICommand?>("DoubleTapCommand", typeof(ListBehavior));

    static ListBehavior()
    {
        AutoScrollProperty.Changed.AddClassHandler<ListBox>((list, e) =>
        {
            if (e.NewValue is true)
            {
                list.PropertyChanged += (_, args) =>
                {
                    if (args.Property == ItemsControl.ItemsSourceProperty)
                    {
                        Subscribe(list);
                    }
                };
                Subscribe(list);
            }
        });
        DoubleTapCommandProperty.Changed.AddClassHandler<ListBox>((list, _) =>
        {
            list.DoubleTapped -= OnDoubleTapped;
            list.DoubleTapped += OnDoubleTapped;
        });
    }

    public static bool GetAutoScroll(ListBox list) => list.GetValue(AutoScrollProperty);

    public static void SetAutoScroll(ListBox list, bool value) => list.SetValue(AutoScrollProperty, value);

    public static ICommand? GetDoubleTapCommand(ListBox list) => list.GetValue(DoubleTapCommandProperty);

    public static void SetDoubleTapCommand(ListBox list, ICommand? value) => list.SetValue(DoubleTapCommandProperty, value);

    private static void Subscribe(ListBox list)
    {
        if (list.ItemsSource is INotifyCollectionChanged items)
        {
            items.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && list.ItemCount > 0)
                {
                    Dispatcher.UIThread.Post(() => list.ScrollIntoView(list.ItemCount - 1), DispatcherPriority.Background);
                }
            };
        }
    }

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: { } item } list
            && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is not null
            && GetDoubleTapCommand(list) is { } command && command.CanExecute(item))
        {
            command.Execute(item);
        }
    }
}

/// <summary>
/// The user guide heading for a part of the window. Inherited, so F1 anywhere inside a page opens the
/// guide at that page's section.
/// </summary>
public static class HelpContext
{
    public static readonly AttachedProperty<string?> TopicProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Topic", typeof(HelpContext), inherits: true);

    public static string? GetTopic(Control control) => control.GetValue(TopicProperty);

    public static void SetTopic(Control control, string? value) => control.SetValue(TopicProperty, value);
}
