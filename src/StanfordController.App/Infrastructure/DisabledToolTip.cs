using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.App.Infrastructure;

/// <summary>
/// Hovering over a greyed-out control says why it is unavailable. The reason comes from the control's command
/// (<see cref="IExplainUnavailable"/>), or from <c>inf:DisabledToolTip.Reason</c> where the control is disabled
/// some other way. The reason goes first, then the control's usual tooltip.
/// </summary>
public static class DisabledToolTip
{
    public static readonly AttachedProperty<string?> ReasonProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Reason", typeof(DisabledToolTip));

    /// <summary>The disabled tooltip in force, removed again when the control is enabled.</summary>
    private static readonly AttachedProperty<IDisposable?> OverrideProperty =
        AvaloniaProperty.RegisterAttached<Control, IDisposable?>("Override", typeof(DisabledToolTip));

    /// <summary>Starts explaining greyed-out controls throughout the app. Call once at startup.</summary>
    public static void Install()
    {
        InputElement.IsEffectivelyEnabledProperty.Changed.AddClassHandler<Control>((control, _) => Update(control));
        ReasonProperty.Changed.AddClassHandler<Control>((control, _) => Update(control));
        // Still disabled, but now for a different reason, e.g. connected (IsEnabled) and the command not yet allowed.
        InputElement.IsEnabledProperty.Changed.AddClassHandler<Control>((control, _) => Update(control));
        Button.CommandProperty.Changed.AddClassHandler<Button>((control, _) => Update(control));
        MenuItem.CommandProperty.Changed.AddClassHandler<MenuItem>((control, _) => Update(control));
        // A command's reason can change while its control stays disabled, e.g. from "not connected" to "busy".
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((control, _) => Update(control));
    }

    public static string? GetReason(Control control) => control.GetValue(ReasonProperty);

    public static void SetReason(Control control, string? value) => control.SetValue(ReasonProperty, value);

    /// <summary>Why <paramref name="control"/> is unavailable: its command's reason first, then the one set on it.</summary>
    public static string? ReasonFor(Control control) =>
        ((control as ICommandSource)?.Command as IExplainUnavailable)?.UnavailableReason is { Length: > 0 } fromCommand
            ? fromCommand
            : GetReason(control) is { Length: > 0 } set ? set : null;

    private static void Update(Control control)
    {
        var reason = control.IsEffectivelyEnabled ? null : ReasonFor(control);
        if (reason is null && control.GetValue(OverrideProperty) is null)
        {
            return;
        }

        control.GetValue(OverrideProperty)?.Dispose();
        control.SetValue(OverrideProperty, null);
        if (reason is null)
        {
            return;
        }

        var usual = control.GetValue(ToolTip.TipProperty) as string;
        var text = string.IsNullOrWhiteSpace(usual) ? reason : $"{reason}{Environment.NewLine}{Environment.NewLine}{usual}";
        control.SetValue(ToolTip.ShowOnDisabledProperty, true);
        // Above the control's own tooltip (a local value or binding), which carries on underneath and is back once enabled.
        control.SetValue(OverrideProperty, control.SetValue(ToolTip.TipProperty, text, BindingPriority.Animation));
    }
}
