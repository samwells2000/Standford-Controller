using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StanfordController.App.Controls;
using StanfordController.App.Dialogs;
using StanfordController.App.Services;
using StanfordController.Core.Profiles;
using StanfordController.Core.Protocol;
using StanfordController.Core.Transport;
using StanfordController.Presentation.Help;
using StanfordController.Presentation.Mvvm;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App;

/// <summary>
/// <c>Tempo --smoke-test &lt;folder&gt; [--headless]</c>: drives the real UI against the simulated instrument,
/// opens every page and dialog in both themes, saves screenshots, and exits with 0 only if nothing failed and
/// no binding errors were logged. CI runs it on Windows and Linux to catch problems that compile cleanly.
/// </summary>
internal sealed class SmokeTest
{
    private readonly MainWindow _window;
    private readonly MainViewModel _viewModel;
    private readonly DialogService _dialogs;
    private readonly string _output;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly StringBuilder _log = new();
    private int _bindingErrors;
    private bool _failed;

    public SmokeTest(MainWindow window, MainViewModel viewModel, DialogService dialogs, string outputDirectory, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _window = window;
        _viewModel = viewModel;
        _dialogs = dialogs;
        _output = outputDirectory;
        _lifetime = lifetime;
        Current = this;
        Logger.Sink = new BindingErrorSink(this);
        Log($"Smoke test started {DateTime.Now:O} (portable build: {App.IsPortableBuild})");
    }

    public static SmokeTest? Current { get; private set; }

    public async Task RunAsync()
    {
        try
        {
            _window.Width = 1440;
            _window.Height = 920;
            await IdleAsync();
            Capture(_window, "01-start");

            _viewModel.AddDemoInstrumentCommand.Execute(null);
            var item = _viewModel.SelectedInstrument ?? throw new InvalidOperationException("Adding the demo instrument did not select it.");
            await IdleAsync();
            Capture(_window, "02-not-connected");

            await item.ConnectAsync();
            if (item.Session is not { } session)
            {
                throw new InvalidOperationException($"Could not connect to the simulator: {item.LastError}");
            }

            // A realistic setup so the screenshots show something meaningful.
            var client = session.Client;
            await client.SetDelayAsync(Dg645Channel.A, Dg645Channel.T0, 1e-6);
            await client.SetDelayAsync(Dg645Channel.B, Dg645Channel.A, 2.5e-6);
            await client.SetDelayAsync(Dg645Channel.C, Dg645Channel.A, 1.5e-6);
            await client.SetDelayAsync(Dg645Channel.D, Dg645Channel.C, 500e-9);
            await client.SetDelayAsync(Dg645Channel.E, Dg645Channel.T0, 4e-6);
            await client.SetDelayAsync(Dg645Channel.F, Dg645Channel.E, 1e-6);
            await client.SetDelayAsync(Dg645Channel.G, Dg645Channel.T0, 6e-6);
            await client.SetDelayAsync(Dg645Channel.H, Dg645Channel.G, 250e-9);
            await client.SetPolarityAsync(Dg645Output.EF, OutputPolarity.Negative);
            await session.RefreshAllAsync();
            session.Terminal.Input = "*IDN?";
            await session.Terminal.SendAsync();
            session.Terminal.Input = "DLAY?3";
            await session.Terminal.SendAsync();
            session.NotifySettingsChanged();
            await IdleAsync();

            // Every page and dialog, in the light theme and then the dark theme. Switching at runtime also
            // proves every colour is a DynamicResource that both palettes define.
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                _viewModel.Theme = theme;
                await IdleAsync();
                var suffix = theme == AppTheme.Dark ? "-dark" : string.Empty;
                await CapturePagesAsync(suffix);
                await CaptureDialogsAsync(item, suffix);
            }

            _viewModel.Theme = AppTheme.Light;
            await IdleAsync();

            // The pages at the smallest window size allowed, to catch clipped layouts.
            _window.Width = _window.MinWidth;
            _window.Height = _window.MinHeight;
            await IdleAsync();
            var small = Tabs();
            for (var i = 0; i < small.ItemCount; i++)
            {
                small.SelectedIndex = i;
                await IdleAsync();
                Capture(_window, $"{i + 3:00}-{Slug((small.Items[i] as TabItem)?.Header?.ToString() ?? "tab")}-minimum-size");
            }

            _window.Width = 1440;
            _window.Height = 920;
            await IdleAsync();

            // F1 opens the guide at the page on show.
            var tabs = Tabs();
            tabs.SelectedIndex = 1;
            await IdleAsync();
            _window.Focus();
            Check(_window.CurrentHelpTopic() == HelpTopics.Outputs, $"F1 on the Outputs page should open '{HelpTopics.Outputs}', not '{_window.CurrentHelpTopic()}'.");
            tabs.SelectedIndex = 0;

            // Saving setups writes them into the configuration file.
            Check(_viewModel.HasUnsavedChanges, "Changing settings did not mark the setup as unsaved.");
            Check(await _viewModel.SaveAllSetupsAsync(), "Saving setups failed.");
            var saved = new ConfigurationStore(_viewModel.ConfigurationPath).Load().Single();
            Check(saved.StoredSetup?.Delays.Any(d => d.Channel == Dg645Channel.B && Math.Abs(d.DelaySeconds - 2.5e-6) < 1e-12) == true,
                "The saved configuration does not contain the instrument's setup.");
            Check(!_viewModel.HasUnsavedChanges, "Saving setups did not clear the unsaved flag.");

            // A set command from the terminal flags the other pages as possibly out of date.
            session.Terminal.Input = "TRAT 2500";
            await session.Terminal.SendAsync();
            Check(session.SettingsMayBeStale, "A set command from the terminal did not flag the pages as out of date.");
            await IdleAsync();
            Capture(_window, "93-terminal-changed-settings");
            await session.RefreshAllAsync();
            Check(!session.SettingsMayBeStale && session.Trigger.Rate.Text == "2.5 kHz",
                $"Refresh all did not pick up the terminal change (rate shows {session.Trigger.Rate.Text}).");
            Check(!session.StatusIsError, $"The session reported an error: {session.StatusMessage}");

            await CheckSingleShotExternalAsync(session);
            await CheckClickAwayAsync(session);

            await item.DisconnectAsync();
            await IdleAsync();
            Capture(_window, "95-disconnected");
        }
        catch (Exception ex)
        {
            Fail(ex);
        }

        Finish();
    }

    /// <summary>
    /// In a single-shot external mode the trigger button arms the instrument and shows Ready, on the command bar,
    /// the Trigger page and the status bar, until the external edge has started a delay cycle.
    /// </summary>
    private async Task CheckSingleShotExternalAsync(InstrumentSessionViewModel session)
    {
        var tabs = Tabs();
        tabs.SelectedIndex = 2;
        await SetTriggerSourceAsync(session, TriggerSource.SingleShotExternalRising);
        Check(TriggerButtonText(onCommandBar: true) == "Arm" && TriggerButtonText(onCommandBar: false) == "Arm",
            $"In single-shot external mode the trigger buttons should read Arm, not {TriggerButtonText(true)} and {TriggerButtonText(false)}.");

        await ((AsyncRelayCommand)session.TriggerCommand).ExecuteAsync();
        await IdleAsync();
        var shown = VisibleTexts("Ready");
        Check(session.IsArmed && TriggerButtonText(onCommandBar: true) == "Ready" && TriggerButtonText(onCommandBar: false) == "Ready" && shown == 3,
            $"After arming, the command bar, Trigger page and status bar should show Ready (armed: {session.IsArmed}, Ready shown {shown} times).");
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            _viewModel.Theme = theme;
            await IdleAsync();
            Capture(_window, "05-trigger-ready" + (theme == AppTheme.Dark ? "-dark" : string.Empty));
        }

        _viewModel.Theme = AppTheme.Light;
        tabs.SelectedIndex = 0;
        await IdleAsync();
        Check(TriggerButtonText(onCommandBar: true) == "Ready", "Ready is not shown on the command bar on other pages.");

        (session.Client.Transport as SimulatorTransport)?.Simulator.ExternalTrigger();
        Check(await WaitAsync(() => !session.IsArmed), "The external trigger did not end the Ready state.");
        await IdleAsync();
        Check(TriggerButtonText(onCommandBar: true) == "Arm" && VisibleTexts("Ready") == 0,
            $"After the external trigger the button should read Arm again, not {TriggerButtonText(true)}.");

        await SetTriggerSourceAsync(session, TriggerSource.Internal);
        Check(TriggerButtonText(onCommandBar: true) == "Trigger", "Back on the internal trigger the button should read Trigger.");
    }

    /// <summary>Clicking a page's background ends editing: the field loses the focus and its value is applied.</summary>
    private async Task CheckClickAwayAsync(InstrumentSessionViewModel session)
    {
        var tabs = Tabs();
        tabs.SelectedIndex = 2;
        await IdleAsync();
        var page = tabs.SelectedContent as Control ?? throw new InvalidOperationException("The Trigger page is not on show.");
        var field = page.GetVisualDescendants().OfType<TextBox>().First(t => ReferenceEquals(t.DataContext, session.Trigger.Rate));
        field.Focus();
        field.Text = "5 kHz";
        await IdleAsync();
        Check(field.IsFocused && session.Trigger.Rate.IsDirty, "Could not start editing the internal rate.");

        var background = page.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("caption") && t.IsEffectivelyVisible);
        Click(background);
        Check(await WaitAsync(() => !session.Trigger.Rate.IsDirty), "Clicking the page background did not apply the edited value.");
        Check(!field.IsFocused && _window.FocusManager?.GetFocusedElement() is not TextBox,
            "Clicking the page background left the field focused.");
        Check(Math.Abs(await session.Client.GetTriggerRateAsync() - 5000) < 1e-6, "The instrument did not receive the edited rate.");
        tabs.SelectedIndex = 0;
        await IdleAsync();
    }

    private async Task SetTriggerSourceAsync(InstrumentSessionViewModel session, TriggerSource source)
    {
        session.Trigger.Source.Selected = session.Trigger.Source.Options.Single(o => o.Value == (int)source);
        Check(await WaitAsync(() => session.Trigger.Source.SelectedValue == (int)source && !session.Trigger.Source.IsBusy),
            $"The trigger source did not change to {source}.");
        await IdleAsync();
    }

    /// <summary>The text on the command bar's trigger button, or on the Trigger page's.</summary>
    private string? TriggerButtonText(bool onCommandBar) =>
        _window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("tool") == onCommandBar && b.IsEffectivelyVisible
                        && ReferenceEquals(b.Command, _viewModel.SelectedInstrument?.Session?.TriggerCommand))
            .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>())
            .Select(t => t.Text)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t));

    private int VisibleTexts(string text) =>
        _window.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text == text && t.IsEffectivelyVisible);

    /// <summary>A left-button press on <paramref name="target"/>, routed as the input system routes one.</summary>
    private void Click(Control target)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var position = target.TranslatePoint(new Point(2, 2), _window) ?? default;
        var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        target.RaiseEvent(new PointerPressedEventArgs(target, pointer, _window, position, (ulong)Environment.TickCount64, properties, KeyModifiers.None));
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        for (var waited = 0; !condition() && waited < timeoutMilliseconds; waited += 50)
        {
            await Task.Delay(50);
        }

        return condition();
    }

    private TabControl Tabs() =>
        _window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault() ?? throw new InvalidOperationException("No tab control found.");

    private async Task CapturePagesAsync(string suffix)
    {
        var tabs = Tabs();
        for (var i = 0; i < tabs.ItemCount; i++)
        {
            tabs.SelectedIndex = i;
            await IdleAsync();
            var header = (tabs.Items[i] as TabItem)?.Header?.ToString() ?? $"tab{i}";
            Capture(_window, $"{i + 3:00}-{Slug(header)}{suffix}");

            // Also capture the bottom of pages that scroll, with the timing diagram showing its hover read-out.
            if (tabs.SelectedContent is Control page
                && page.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } viewer
                && viewer.Extent.Height > viewer.Viewport.Height + 1)
            {
                viewer.Offset = new Vector(0, viewer.Extent.Height);
                await IdleAsync();
                var diagram = page.GetVisualDescendants().OfType<TimingDiagram>().FirstOrDefault();
                diagram?.SimulateHover(new Point(diagram.Bounds.Width * 0.33, diagram.Bounds.Height * 0.32));
                await IdleAsync();
                Capture(_window, $"{i + 3:00}-{Slug(header)}-end{suffix}");
                diagram?.SimulateHover(null);
                viewer.Offset = default;
            }
        }

        tabs.SelectedIndex = 0;
        await IdleAsync();
    }

    private async Task CaptureDialogsAsync(InstrumentItemViewModel item, string suffix)
    {
        var dialogs = new (Window Window, string Name)[]
        {
            (new ProfileEditorWindow { DataContext = new ProfileEditorViewModel(item.Profile, [], isNew: false) }, "90-edit-instrument"),
            (new DiscoveryWindow { DataContext = new DiscoveryViewModel() }, "91-find-instruments"),
            (new AboutWindow(App.Paths), "94-about"),
        };
        foreach (var (dialog, name) in dialogs)
        {
            dialog.Show(_window);
            await IdleAsync();
            Capture(dialog, name + suffix);
            dialog.Close();
        }

        // The question asked when an instrument's settings differ from its saved setup.
        var question = MessageDialog.ShowAsync(_window, MessageKind.Question, "Saved setup",
            $"The settings on {item.Name} differ from the setup saved in Tempo on 2026-10-06 14:02:\n\n  •  Delay C\n  •  Trigger rate\n  •  CD amplitude\n\nWhich settings do you want to use?",
            "Load saved setup", "Keep instrument settings", showCancel: false, rememberText: "Remember my choice for this instrument");
        await IdleAsync();
        if (_lifetime.Windows.OfType<MessageDialog>().LastOrDefault() is { } message)
        {
            Capture(message, "96-setup-question" + suffix);
            message.Close();
        }

        await question;

        _dialogs.ShowHelp(HelpTopics.Delays);
        await IdleAsync();
        if (_dialogs.Help is { } help)
        {
            Capture(help, "97-help" + suffix);
            help.Navigate(HelpTopics.Shortcuts);
            await IdleAsync();
            Capture(help, "98-help-shortcuts" + suffix);
            help.Close();
            await IdleAsync();
        }
        else
        {
            Check(false, "The help window did not open.");
        }
    }

    public void Fail(Exception exception)
    {
        _failed = true;
        Log("FAILED: " + exception);
    }

    private void Check(bool condition, string message)
    {
        if (!condition)
        {
            _failed = true;
            Log("FAILED: " + message);
        }
    }

    private void Finish()
    {
        var exitCode = _failed ? 1 : _bindingErrors > 0 ? 2 : 0;
        Log($"Binding errors: {_bindingErrors}");
        Log($"Exit code: {exitCode}");
        File.WriteAllText(Path.Combine(_output, "smoke-test.log"), _log.ToString());
        _lifetime.Shutdown(exitCode);
    }

    private void Log(string message)
    {
        lock (_log)
        {
            _log.AppendLine(message);
        }
    }

    private static async Task IdleAsync()
    {
        await Task.Delay(150);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private void Capture(Window target, string name)
    {
        var size = target.ClientSize;
        if (size.Width < 1 || size.Height < 1)
        {
            Log($"Skipped screenshot {name}: nothing rendered.");
            return;
        }

        var scale = target.RenderScaling;
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(target);
        bitmap.Save(Path.Combine(_output, name + ".png"), new PngBitmapEncoderOptions());
        Log($"Saved {name}.png ({bitmap.PixelSize.Width}×{bitmap.PixelSize.Height})");
    }

    private static string Slug(string text) =>
        new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    /// <summary>Counts binding warnings and errors (a mistyped path, a converter failure) as test failures.</summary>
    private sealed class BindingErrorSink(SmokeTest owner) : ILogSink
    {
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Record(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Record(level, area, source, messageTemplate, propertyValues);

        private void Record(LogEventLevel level, string area, object? source, string template, object?[] values)
        {
            var text = $"{level} [{area}] {template} | {string.Join(" | ", values)} (source: {source})";
            if (area == LogArea.Binding)
            {
                Interlocked.Increment(ref owner._bindingErrors);
                owner.Log("BINDING: " + text);
            }
            else
            {
                owner.Log("LOG: " + text);
            }
        }
    }
}
