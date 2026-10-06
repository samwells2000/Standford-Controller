using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StanfordController.App.Dialogs;
using StanfordController.App.Infrastructure;
using StanfordController.Core.Protocol;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App;

/// <summary>
/// <c>StanfordController.exe --smoke-test &lt;folder&gt;</c>: drives the real UI against the simulated
/// instrument, opens every page and dialog, saves screenshots, and exits with 0 only if nothing failed
/// and no data binding errors were reported. Used by CI to catch XAML problems that compile cleanly.
/// </summary>
internal sealed class SmokeTest(MainWindow window, MainViewModel viewModel, string outputDirectory)
{
    private readonly StringBuilder _log = new();
    private int _bindingErrors;
    private bool _failed;

    public static SmokeTest? Current { get; private set; }

    public void Install()
    {
        Current = this;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingListener(this));
        Log($"Smoke test started {DateTime.Now:O}");
    }

    public async Task RunAsync()
    {
        try
        {
            window.Width = 1440;
            window.Height = 920;
            await IdleAsync();
            Capture(window, "01-welcome");

            viewModel.AddDemoInstrumentCommand.Execute(null);
            var item = viewModel.SelectedInstrument ?? throw new InvalidOperationException("Adding the demo instrument did not select it.");
            await IdleAsync();
            Capture(window, "02-not-connected");

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
            await IdleAsync();

            var tabs = AutoScrollBehavior.FindChild<TabControl>(window) ?? throw new InvalidOperationException("No tab control found.");
            for (var i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                await IdleAsync();
                var header = (tabs.Items[i] as TabItem)?.Header?.ToString() ?? $"tab{i}";
                Capture(window, $"{i + 3:00}-{Slug(header)}");

                // Also capture the bottom of pages that scroll.
                if ((tabs.Items[i] as TabItem)?.Content is DependencyObject page
                    && AutoScrollBehavior.FindChild<ScrollViewer>(page) is { ScrollableHeight: > 0 } viewer)
                {
                    viewer.ScrollToEnd();
                    await IdleAsync();
                    Capture(window, $"{i + 3:00}-{Slug(header)}-end");
                    viewer.ScrollToHome();
                }
            }

            var editor = new ProfileEditorWindow { DataContext = new ProfileEditorViewModel(item.Profile, [], isNew: false), Owner = window };
            editor.Show();
            await IdleAsync();
            Capture(editor, "90-edit-instrument");
            editor.Close();

            var discovery = new DiscoveryWindow { DataContext = new DiscoveryViewModel(), Owner = window };
            discovery.Show();
            await IdleAsync();
            Capture(discovery, "91-find-instruments");
            discovery.Close();

            await item.DisconnectAsync();
            await IdleAsync();
            Capture(window, "92-disconnected");

            if (session.StatusIsError)
            {
                throw new InvalidOperationException($"The session reported an error: {session.StatusMessage}");
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }

        Finish();
    }

    public void Fail(Exception exception)
    {
        _failed = true;
        Log("FAILED: " + exception);
    }

    private void Finish()
    {
        var exitCode = _failed ? 1 : _bindingErrors > 0 ? 2 : 0;
        Log($"Binding errors: {_bindingErrors}");
        Log($"Exit code: {exitCode}");
        File.WriteAllText(Path.Combine(outputDirectory, "smoke-test.log"), _log.ToString());

        // The generated WPF Main returns void, so the process exit code comes from Environment.ExitCode.
        Environment.ExitCode = exitCode;
        Application.Current.Shutdown(exitCode);
    }

    private void Log(string message) => _log.AppendLine(message);

    private static async Task IdleAsync()
    {
        await Task.Delay(150);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private void Capture(Window target, string name)
    {
        if (target.Content is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1)
        {
            Log($"Skipped screenshot {name}: nothing rendered.");
            return;
        }

        Save(root, name, new Size(root.ActualWidth, root.ActualHeight), VisualTreeHelper.GetDpi(target));
    }

    private void Save(FrameworkElement root, string name, Size size, DpiScale dpi)
    {
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(root);

        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(outputDirectory, name + ".png")))
        {
            png.Save(stream);
        }

        Log($"Saved {name}.png ({bitmap.PixelWidth}×{bitmap.PixelHeight})");
    }

    private static string Slug(string text) =>
        new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    private sealed class BindingListener(SmokeTest owner) : TraceListener
    {
        private readonly StringBuilder _pending = new();

        public override void Write(string? message) => _pending.Append(message);

        public override void WriteLine(string? message)
        {
            _pending.Append(message);
            var text = _pending.ToString();
            _pending.Clear();
            owner._bindingErrors++;
            owner.Log("BINDING: " + text);
        }
    }
}
