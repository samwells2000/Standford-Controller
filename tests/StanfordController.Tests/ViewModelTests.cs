using StanfordController.Core;
using StanfordController.Core.Profiles;
using StanfordController.Core.Protocol;
using StanfordController.Core.Simulation;
using StanfordController.Core.Transport;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.Tests;

internal sealed class FakeDialogs : IDialogService
{
    public bool ConfirmResult { get; set; } = true;

    /// <summary>The answer to every Yes/No/Cancel question.</summary>
    public DialogAnswer AskAnswer { get; set; } = new(DialogChoice.Cancel);

    public List<string> Errors { get; } = [];

    public List<string> Questions { get; } = [];

    public List<string?> HelpTopicsShown { get; } = [];

    public string? SaveFilePath { get; set; }

    public string? OpenFilePath { get; set; }

    public Func<ProfileEditorViewModel, bool>? OnEditProfile { get; set; }

    public Task<bool> EditProfileAsync(ProfileEditorViewModel editor) => Task.FromResult(OnEditProfile?.Invoke(editor) ?? false);

    public Task<bool> ShowDiscoveryAsync(DiscoveryViewModel discovery) => Task.FromResult(false);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK") => Task.FromResult(ConfirmResult);

    public Task<DialogAnswer> AskAsync(string title, string message, string yesText, string noText, string? rememberText = null, bool showCancel = true)
    {
        Questions.Add(message);
        return Task.FromResult(AskAnswer);
    }

    public Task ShowErrorAsync(string title, string message)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }

    public Task ShowInfoAsync(string title, string message) => Task.CompletedTask;

    public Task<string?> PickSaveFileAsync(string title, string defaultFileName, params FileFilter[] filters) => Task.FromResult(SaveFilePath);

    public Task<string?> PickOpenFileAsync(string title, params FileFilter[] filters) => Task.FromResult(OpenFilePath);

    public Task CopyToClipboardAsync(string text) => Task.CompletedTask;

    public Task ShowAboutAsync() => Task.CompletedTask;

    public void ShowHelp(string? topic = null) => HelpTopicsShown.Add(topic);
}

public class ViewModelTests
{
    private static async Task<(InstrumentSessionViewModel Session, Dg645Simulator Simulator)> CreateSessionAsync()
    {
        var simulator = new Dg645Simulator();
        var client = new Dg645Client(new SimulatorTransport(simulator)) { QueryTimeout = TimeSpan.FromMilliseconds(200) };
        await client.ConnectAsync();
        var session = new InstrumentSessionViewModel(new InstrumentProfile { Name = "Test", IsSimulator = true }, client, new FakeDialogs(), new LogViewModel());
        await session.RefreshAllAsync();
        return (session, simulator);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task RefreshReadsEverySection()
    {
        var (session, _) = await CreateSessionAsync();

        Assert.Equal(8, session.Channels.Count);
        Assert.All(session.Channels, c => Assert.Equal("0 s", c.DelayText));
        Assert.All(session.Outputs, o => Assert.Equal("2.50 V", o.Amplitude.Text));
        Assert.Equal("10 kHz", session.Trigger.Rate.Text);
        Assert.Equal("Internal", session.Trigger.Source.Selected?.Label);
        Assert.Equal("10", session.Burst.Count.Text);
        Assert.Equal("9600 baud", session.Interface.BaudRate.Selected?.Label);
        Assert.Equal("00:19:b3:0a:06:45", session.Interface.MacAddress);
        Assert.Equal("Internal crystal", session.Status.Timebase);
        Assert.Equal("DG645", session.Model);
        Assert.False(session.StatusIsError, session.StatusMessage);
    }

    [Fact]
    public async Task EditingADelayWritesAndUpdatesTheTimingDiagram()
    {
        var (session, simulator) = await CreateSessionAsync();
        var a = session.Channels.Single(c => c.Channel == Dg645Channel.A);
        var b = session.Channels.Single(c => c.Channel == Dg645Channel.B);

        a.DelayText = "1.5 us";
        Assert.True(a.IsDirty);
        await ((Presentation.Mvvm.AsyncRelayCommand)a.CommitCommand).ExecuteAsync();
        Assert.False(a.IsDirty);
        Assert.Equal("1.5 µs", a.DelayText);
        Assert.Contains("DLAY 2,0,0.000001500000", simulator.ReceivedLines);

        b.Reference = b.ReferenceOptions.Single(o => o.Label == "A");
        await WaitUntilAsync(() => b.ReferenceChannel == Dg645Channel.A && !b.IsBusy);
        b.DelayText = "250 ns";
        await ((Presentation.Mvvm.AsyncRelayCommand)b.CommitCommand).ExecuteAsync();

        Assert.Equal(1.75e-6, b.AbsoluteSeconds!.Value, 1e-15);
        var ab = session.Timing.Single(p => p.Output == "AB");
        Assert.True(ab.IsValid);
        Assert.Equal(250e-9, ab.Width, 1e-15);
    }

    [Fact]
    public async Task InvalidInputIsRejectedBeforeReachingTheInstrument()
    {
        var (session, simulator) = await CreateSessionAsync();
        var sent = simulator.ReceivedLines.Count;

        session.Trigger.Rate.Text = "fast";
        await ((Presentation.Mvvm.AsyncRelayCommand)session.Trigger.Rate.CommitCommand).ExecuteAsync();

        Assert.True(session.Trigger.Rate.HasError);
        Assert.Equal(sent, simulator.ReceivedLines.Count);

        session.Trigger.Rate.RevertCommand.Execute(null);
        Assert.False(session.Trigger.Rate.HasError);
        Assert.Equal("10 kHz", session.Trigger.Rate.Text);
    }

    [Fact]
    public async Task InstrumentErrorsAreShownOnTheFieldAndRevertTheValue()
    {
        var (session, _) = await CreateSessionAsync();
        var a = session.Channels.Single(c => c.Channel == Dg645Channel.A);
        var b = session.Channels.Single(c => c.Channel == Dg645Channel.B);

        b.Reference = b.ReferenceOptions.Single(o => o.Label == "A");
        await WaitUntilAsync(() => b.ReferenceChannel == Dg645Channel.A && !b.IsBusy);

        // A relative to B would create a loop.
        a.Reference = a.ReferenceOptions.Single(o => o.Label == "B");
        await WaitUntilAsync(() => a.HasError && !a.IsBusy);

        Assert.Contains("Illegal link", a.Error);
        Assert.Equal(Dg645Channel.T0, a.ReferenceChannel);
        Assert.Contains(session.Status.Errors, e => e.Code == 13);
        Assert.True(session.StatusIsError);
    }

    [Fact]
    public async Task OutputPresetsSetLevels()
    {
        var (session, _) = await CreateSessionAsync();
        var ab = session.Outputs.Single(o => o.Output == Dg645Output.AB);

        await ((Presentation.Mvvm.AsyncRelayCommand)ab.NimCommand).ExecuteAsync();
        Assert.False(ab.HasError, ab.Error);
        Assert.Equal(-0.8, ab.Offset.Value, 1e-9);
        Assert.Equal(0.8, ab.Amplitude.Value, 1e-9);
        Assert.False(ab.IsPositive);
        Assert.Equal(0.0, ab.IdleVolts!.Value, 1e-9);
        Assert.Equal(-0.8, ab.ActiveVolts!.Value, 1e-9);

        await ((Presentation.Mvvm.AsyncRelayCommand)ab.TtlCommand).ExecuteAsync();
        Assert.Equal(0.0, ab.IdleVolts!.Value, 1e-9);
        Assert.Equal(4.0, ab.ActiveVolts!.Value, 1e-9);
    }

    [Fact]
    public async Task PrescalersSplitIntoTriggerInputAndOutputs()
    {
        var (session, simulator) = await CreateSessionAsync();
        Assert.Null(session.Trigger.TriggerPrescaler.Phase);
        Assert.Equal(["Output AB", "Output CD", "Output EF", "Output GH"], session.Trigger.OutputPrescalers.Select(p => p.Name));
        Assert.All(session.Trigger.OutputPrescalers, p => Assert.NotNull(p.Phase));

        var cd = session.Trigger.OutputPrescalers[1];
        cd.Factor.Text = "4";
        await ((Presentation.Mvvm.AsyncRelayCommand)cd.Factor.CommitCommand).ExecuteAsync();
        cd.Phase!.Text = "3";
        await ((Presentation.Mvvm.AsyncRelayCommand)cd.Phase.CommitCommand).ExecuteAsync();
        Assert.Contains("PRES 2,4", simulator.ReceivedLines);
        Assert.Contains("PHAS 2,3", simulator.ReceivedLines);

        // Phase must be below the factor; the instrument rejects it and the field shows the reason.
        cd.Phase.Text = "9";
        await ((Presentation.Mvvm.AsyncRelayCommand)cd.Phase.CommitCommand).ExecuteAsync();
        Assert.True(cd.Phase.HasError);
        Assert.Equal("3", cd.Phase.Text);
    }

    [Fact]
    public async Task TerminalShowsRepliesAndErrors()
    {
        var (session, _) = await CreateSessionAsync();
        session.Terminal.Input = "*IDN?";
        await session.Terminal.SendAsync();
        Assert.Contains(session.Terminal.Lines, l => l.Kind == TerminalLineKind.Response && l.Text.Contains("DG645"));

        session.Terminal.Input = "BURC 0";
        await session.Terminal.SendAsync();
        Assert.Contains(session.Terminal.Lines, l => l.Kind == TerminalLineKind.Error && l.Text.Contains("Illegal value"));
    }

    [Fact]
    public async Task StatusPollingLatchesBits()
    {
        var (session, _) = await CreateSessionAsync();
        await session.Status.PollAsync();
        Assert.True(session.Status.InstrumentBits.Single(b => b.Name == "TRIG").IsSet);

        session.Status.ClearIndicatorsCommand.Execute(null);
        Assert.False(session.Status.InstrumentBits.Single(b => b.Name == "TRIG").IsSet);
    }

    [Fact]
    public async Task MainViewModelConnectsToTheDemoInstrumentAndRemembersIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "sc-tests-" + Guid.NewGuid().ToString("N"), "Default.tempo");
        try
        {
            var main = new MainViewModel(path, new FakeDialogs());
            await main.LoadAsync();
            Assert.False(main.HasInstruments);

            main.AddDemoInstrumentCommand.Execute(null);
            var item = Assert.Single(main.Instruments);
            Assert.Same(item, main.SelectedInstrument);

            await item.ConnectAsync();
            Assert.True(item.IsConnected);
            Assert.NotNull(item.Session);
            Assert.Equal("Connected", item.StateText);

            await item.DisconnectAsync();
            Assert.False(item.IsConnected);
            Assert.Null(item.Session);

            var reloaded = new MainViewModel(path, new FakeDialogs());
            await reloaded.LoadAsync();
            var saved = Assert.Single(reloaded.Instruments);
            Assert.Equal("Demo DG645 (simulated)", saved.Name);
            Assert.NotNull(saved.Profile.LastConnectedUtc);
            Assert.StartsWith("Stanford Research Systems", saved.Profile.LastKnownIdentity);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task FailedConnectionIsReportedOnTheItem()
    {
        var item = new InstrumentItemViewModel(
            new InstrumentProfile { Name = "Nowhere", Host = "127.0.0.1", Port = 1, TimeoutMs = 500 },
            new FakeDialogs(),
            _ => { });

        await item.ConnectAsync();
        Assert.Equal(ConnectionState.Failed, item.State);
        Assert.False(string.IsNullOrEmpty(item.LastError));
    }

    [Fact]
    public void ProfileEditorValidatesBeforeSaving()
    {
        var existing = new InstrumentProfile { Name = "Bench 1", Host = "10.0.0.1" };
        var editor = new ProfileEditorViewModel(new InstrumentProfile(), [existing], isNew: true);

        Assert.False(editor.HasVisibleProblems);
        Assert.False(editor.TrySave());
        Assert.True(editor.HasVisibleProblems);

        editor.Name = "bench 1";
        editor.Host = "10.0.0.2";
        Assert.False(editor.TrySave());
        Assert.Contains("already called", editor.ProblemsText);

        editor.Name = "Bench 2";
        editor.PortText = "5025";
        Assert.True(editor.TrySave());
        Assert.Equal("Bench 2", editor.Result.Name);
        Assert.Equal("10.0.0.2", editor.Result.Host);
    }

    [Fact]
    public async Task ProfileEditorTestsTheConnection()
    {
        var editor = new ProfileEditorViewModel(new InstrumentProfile { Name = "Demo", IsSimulator = true }, [], isNew: false);
        await ((Presentation.Mvvm.AsyncRelayCommand)editor.TestConnectionCommand).ExecuteAsync();
        Assert.True(editor.TestSucceeded);
        Assert.Contains("DG645", editor.TestResult);
    }
}
