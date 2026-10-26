using StanfordController.Core;
using StanfordController.Core.Audit;
using StanfordController.Core.Profiles;
using StanfordController.Core.Protocol;
using StanfordController.Core.Simulation;
using StanfordController.Core.Transport;
using StanfordController.Presentation.Mvvm;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.Tests;

/// <summary>Undo and redo, pulses set by start and width, previews before replacing settings, and delay scans.</summary>
public sealed class EditingToolsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sc-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestFolders.Delete(_directory);
    }

    private async Task<(InstrumentSessionViewModel Session, Dg645Simulator Simulator, AuditLog Audit, FakeDialogs Dialogs)> CreateSessionAsync()
    {
        var simulator = new Dg645Simulator();
        var client = new Dg645Client(new SimulatorTransport(simulator)) { QueryTimeout = TimeSpan.FromMilliseconds(300) };
        await client.ConnectAsync();
        var audit = new AuditLog(Path.Combine(_directory, "Audit trail"), "Tempo 1.0.0");
        var dialogs = new FakeDialogs();
        var session = new InstrumentSessionViewModel(new InstrumentProfile { Name = "Bench", IsSimulator = true }, client, dialogs, new LogViewModel(), audit);
        await session.RefreshAllAsync();
        return (session, simulator, audit, dialogs);
    }

    private static async Task<IReadOnlyList<AuditRecord>> RecordsAsync(AuditLog audit)
    {
        Assert.Equal(0, await audit.FlushAsync());
        return audit.Store.Months().SelectMany(audit.Store.ReadMonth).ToList();
    }

    private static string Query(Dg645Simulator simulator, string query) => simulator.Process(query).Single();

    private static ChannelDelayViewModel Channel(InstrumentSessionViewModel session, Dg645Channel channel) =>
        session.Channels.Single(c => c.Channel == channel);

    private static async Task SetDelayAsync(InstrumentSessionViewModel session, Dg645Channel channel, string text)
    {
        var vm = Channel(session, channel);
        vm.DelayText = text;
        await ((AsyncRelayCommand)vm.CommitCommand).ExecuteAsync();
    }

    private static Task UndoAsync(InstrumentSessionViewModel session) => ((AsyncRelayCommand)session.UndoCommand).ExecuteAsync();

    private static Task RedoAsync(InstrumentSessionViewModel session) => ((AsyncRelayCommand)session.RedoCommand).ExecuteAsync();

    // ---- Undo and redo ----

    [Fact]
    public async Task UndoPutsADelayBackAndRedoMakesTheChangeAgain()
    {
        var (session, simulator, audit, _) = await CreateSessionAsync();
        Assert.False(session.CanUndo);
        Assert.False(session.UndoCommand.CanExecute(null));
        Assert.Equal("Nothing to undo: no change has been made since connecting.", ((IExplainUnavailable)session.UndoCommand).UnavailableReason);

        await SetDelayAsync(session, Dg645Channel.A, "1 us");
        await SetDelayAsync(session, Dg645Channel.A, "2 us");
        Assert.Equal("_Undo Delay A", session.UndoMenuText);
        Assert.Equal("Undo Delay A: back to T0 + 1 µs (Ctrl+Z)", session.UndoTip);

        await UndoAsync(session);
        Assert.Equal("0,+0.000001000000", Query(simulator, "DLAY?2"));
        Assert.Equal("1 µs", Channel(session, Dg645Channel.A).DelayText);
        Assert.True(session.CanRedo);
        Assert.Equal("_Redo Delay A", session.RedoMenuText);

        await UndoAsync(session);
        Assert.Equal("0,+0.000000000000", Query(simulator, "DLAY?2"));
        Assert.False(session.CanUndo);

        await RedoAsync(session);
        await RedoAsync(session);
        Assert.Equal("0,+0.000002000000", Query(simulator, "DLAY?2"));
        Assert.False(session.CanRedo);

        // A new change after undoing forgets what could be redone.
        await UndoAsync(session);
        await SetDelayAsync(session, Dg645Channel.C, "5 ns");
        Assert.False(session.CanRedo);

        var records = await RecordsAsync(audit);
        Assert.Contains(records, r => r is { Action: AuditActions.Undone, Setting: "Delay A", OldValue: "T0 + 2 µs", NewValue: "T0 + 1 µs" });
        Assert.Contains(records, r => r is { Action: AuditActions.Redone, Setting: "Delay A", OldValue: "T0 + 1 µs", NewValue: "T0 + 2 µs" });
    }

    [Fact]
    public async Task ChoicesTogglesStepsAndPresetsCanBeUndone()
    {
        var (session, simulator, _, _) = await CreateSessionAsync();

        session.Trigger.Source.Selected = session.Trigger.Source.Options.Single(o => o.Value == (int)TriggerSource.SingleShot);
        await WaitForAsync(() => session.CanUndo);
        Assert.Equal("5", Query(simulator, "TSRC?"));
        await UndoAsync(session);
        Assert.Equal("0", Query(simulator, "TSRC?"));
        Assert.Equal((int)TriggerSource.Internal, session.Trigger.Source.SelectedValue);

        session.Burst.Enabled.IsChecked = true;
        await WaitForAsync(() => Query(simulator, "BURM?") == "1" && session.CanUndo);
        await UndoAsync(session);
        Assert.Equal("0", Query(simulator, "BURM?"));
        Assert.False(session.Burst.Enabled.IsChecked);

        var ab = session.Outputs.Single(o => o.Output == Dg645Output.AB);
        await ((AsyncRelayCommand)ab.NimCommand).ExecuteAsync();
        Assert.Equal("0", Query(simulator, "LPOL?1"));
        Assert.Equal("_Undo AB levels", session.UndoMenuText);
        await UndoAsync(session);
        Assert.Equal("1", Query(simulator, "LPOL?1"));
        Assert.Equal(2.5, double.Parse(Query(simulator, "LAMP?1"), System.Globalization.CultureInfo.InvariantCulture), 3);

        await ((AsyncRelayCommand)Channel(session, Dg645Channel.A).StepUpCommand).ExecuteAsync();
        Assert.Equal("0,+0.000000001000", Query(simulator, "DLAY?2"));
        await UndoAsync(session);
        Assert.Equal("0,+0.000000000000", Query(simulator, "DLAY?2"));
    }

    [Fact]
    public async Task ResetIsUndoneInOneStep()
    {
        var (session, simulator, audit, _) = await CreateSessionAsync();
        simulator.Process("DLAY 2,0,0.000001;DLAY 3,2,0.000002;TRAT 500");
        await session.RefreshAllAsync();

        await ((AsyncRelayCommand)session.ResetCommand).ExecuteAsync();
        Assert.Equal("_Undo Reset", session.UndoMenuText);
        await UndoAsync(session);

        Assert.Equal("0,+0.000001000000", Query(simulator, "DLAY?2"));
        Assert.Equal("2,+0.000002000000", Query(simulator, "DLAY?3"));
        Assert.Equal(500, double.Parse(Query(simulator, "TRAT?"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("500 Hz", session.Trigger.Rate.Text);
        Assert.True(session.CanRedo);

        var records = await RecordsAsync(audit);
        Assert.Contains(records, r => r is { Action: AuditActions.Undone, Setting: "Delay A", NewValue: "T0 + 1 µs" });
    }

    [Fact]
    public void DelayChangesAreOrderedSoNoChannelIsEverLinkedToItself()
    {
        // Now: A follows B. Wanted: B follows A. Setting B first would link B → A → B.
        var references = new Dictionary<Dg645Channel, Dg645Channel> { [Dg645Channel.A] = Dg645Channel.B, [Dg645Channel.B] = Dg645Channel.T0 };
        var targets = new[]
        {
            new ChannelDelaySettings { Channel = Dg645Channel.B, Reference = Dg645Channel.A, DelaySeconds = 1e-6 },
            new ChannelDelaySettings { Channel = Dg645Channel.A, Reference = Dg645Channel.T0, DelaySeconds = 1e-6 },
        };

        var order = InstrumentSessionViewModel.OrderWithoutLoops(targets, references).Select(t => t.Channel).ToList();
        Assert.Equal([Dg645Channel.A, Dg645Channel.B], order);
    }

    // ---- Pulses ----

    [Fact]
    public async Task APulseIsSetByItsStartAndWidth()
    {
        var (session, simulator, audit, _) = await CreateSessionAsync();
        var ab = session.Pulses.Single(p => p.Output == Dg645Output.AB);

        ab.StartText = "1 us";
        await ((AsyncRelayCommand)ab.CommitStartCommand).ExecuteAsync();
        ab.WidthText = "2.5us";
        await ((AsyncRelayCommand)ab.CommitWidthCommand).ExecuteAsync();

        Assert.Equal("0,+0.000001000000", Query(simulator, "DLAY?2"));
        Assert.Equal("2,+0.000002500000", Query(simulator, "DLAY?3"));
        Assert.Equal("1 µs", ab.StartText);
        Assert.Equal("2.5 µs", ab.WidthText);
        Assert.Equal("T0 + 3.5 µs", ab.EndText);
        Assert.False(ab.HasNote);

        // Moving the start keeps the width.
        ab.StartText = "4 us";
        await ((AsyncRelayCommand)ab.CommitStartCommand).ExecuteAsync();
        Assert.Equal("T0 + 6.5 µs", ab.EndText);
        Assert.Equal("2.5 µs", ab.WidthText);

        // One undo puts both channels back.
        Assert.Equal("_Undo AB pulse", session.UndoMenuText);
        await UndoAsync(session);
        Assert.Equal("0,+0.000001000000", Query(simulator, "DLAY?2"));
        Assert.Equal("1 µs", ab.StartText);

        var records = await RecordsAsync(audit);
        Assert.Contains(records, r => r is { Action: AuditActions.Changed, Setting: "Delay B", NewValue: "A + 2.5 µs" }
                                      && r.Details!.StartsWith("AB pulse set to start at T0 + 1 µs, 2.5 µs wide", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APulseWhoseStartFollowsItsEndIsSetWithoutALoop()
    {
        var (session, simulator, _, _) = await CreateSessionAsync();

        // A follows B, 1 µs after it, so the pulse ends before it starts.
        simulator.Process("DLAY 3,0,0.000003;DLAY 2,3,0.000001");
        await session.RefreshAllAsync();
        var ab = session.Pulses.Single(p => p.Output == Dg645Output.AB);
        Assert.Equal("4 µs", ab.StartText);
        Assert.True(ab.HasNote);
        Assert.Contains("No pulse", ab.Note);

        Assert.True(await ab.SetAsync(2e-6, 500e-9));
        Assert.Null(ab.Error);
        Assert.Equal("0,+0.000002000000", Query(simulator, "DLAY?2"));
        Assert.Equal("2,+0.000000500000", Query(simulator, "DLAY?3"));
        Assert.Equal("0", Query(simulator, "LERR?"));
    }

    [Fact]
    public async Task APulseKeepsTheStartChannelsReference()
    {
        var (session, simulator, _, _) = await CreateSessionAsync();
        simulator.Process("DLAY 4,0,0.000001");
        await session.RefreshAllAsync();
        var ef = session.Pulses.Single(p => p.Output == Dg645Output.EF);

        // E follows C (at 1 µs): a start of 3 µs makes E = C + 2 µs.
        simulator.Process("DLAY 6,4,0");
        await session.RefreshAllAsync();
        Assert.True(await ef.SetAsync(3e-6, 1e-6));
        Assert.Equal("4,+0.000002000000", Query(simulator, "DLAY?6"));
        Assert.Equal("6,+0.000001000000", Query(simulator, "DLAY?7"));

        // A start before C's time cannot follow C, so E is referenced to T0.
        Assert.True(await ef.SetAsync(500e-9, 1e-6));
        Assert.Equal("0,+0.000000500000", Query(simulator, "DLAY?6"));
    }

    [Fact]
    public async Task APulseRejectsTextThatIsNotATime()
    {
        var (session, _, _, _) = await CreateSessionAsync();
        var cd = session.Pulses.Single(p => p.Output == Dg645Output.CD);
        cd.WidthText = "soon";
        await ((AsyncRelayCommand)cd.CommitWidthCommand).ExecuteAsync();
        Assert.True(cd.HasError);
        Assert.StartsWith("\"soon\" is not a time", cd.Error);
        Assert.False(session.CanUndo);
    }

    // ---- Previews ----

    [Fact]
    public async Task RestoringASavedSetupShowsWhatItWouldChange()
    {
        var dialogs = new FakeDialogs();
        var main = new MainViewModel(Path.Combine(_directory, "Default.tempo"), dialogs, new AppSettingsStore(Path.Combine(_directory, "settings.json")));
        await main.LoadAsync();
        var item = main.AddProfile(new InstrumentProfile { Name = "Pump", IsSimulator = true });
        await item.ConnectAsync();
        await item.SaveSetupAsync();

        // Nothing differs: nothing to restore.
        await ((AsyncRelayCommand)item.RestoreSetupCommand).ExecuteAsync();
        Assert.Empty(dialogs.ChangesShown);

        await SetDelayAsync(item.Session!, Dg645Channel.A, "7 us");
        dialogs.ConfirmResult = false;
        await ((AsyncRelayCommand)item.RestoreSetupCommand).ExecuteAsync();
        var change = Assert.Single(Assert.Single(dialogs.ChangesShown));
        Assert.Equal(new SettingChange("Delay A", "T0 + 7 µs", "T0 + 0 s"), change);
        Assert.Equal("7 µs", Channel(item.Session!, Dg645Channel.A).DelayText);

        dialogs.ConfirmResult = true;
        await ((AsyncRelayCommand)item.RestoreSetupCommand).ExecuteAsync();
        Assert.Equal("0 s", Channel(item.Session!, Dg645Channel.A).DelayText);
        Assert.Equal("_Undo Apply setup", item.Session!.UndoMenuText);
    }

    [Fact]
    public async Task ImportingASetupFileShowsWhatItWouldChange()
    {
        var (session, simulator, _, dialogs) = await CreateSessionAsync();
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "setup" + Dg645Settings.FileExtension);
        var settings = await session.Client.ReadSettingsAsync();
        settings.Trigger.RateHz = 250;
        settings.Delays.Single(d => d.Channel == Dg645Channel.C).DelaySeconds = 3e-6;
        await settings.SaveAsync(path);
        dialogs.OpenFilePath = path;

        await ((AsyncRelayCommand)session.ImportSettingsCommand).ExecuteAsync();

        var changes = Assert.Single(dialogs.ChangesShown);
        Assert.Equal(["Trigger rate", "Delay C"], changes.Select(c => c.Setting));
        Assert.Equal("0,+0.000003000000", Query(simulator, "DLAY?4"));
    }

    // ---- Delay scans ----

    [Fact]
    public async Task AScanStepsTheDelayAndPutsItBack()
    {
        var (session, simulator, audit, _) = await CreateSessionAsync();
        simulator.Process("DLAY 4,2,0.000000005");
        await session.RefreshAllAsync();
        var seen = new List<string>();
        var scan = session.Scan;
        scan.Channel = scan.ChannelOptions.Single(o => o.Value == (int)Dg645Channel.C);
        scan.FromText = "10 ns";
        scan.ToText = "30 ns";
        scan.CountText = "3";
        scan.DwellText = "50 ms";
        Assert.Equal("3 values of C from A + 10 ns to A + 30 ns, 10 ns apart, 50 ms at each: about 150 ms.", scan.Description);
        Channel(session, Dg645Channel.C).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChannelDelayViewModel.DelaySummary))
            {
                seen.Add(Channel(session, Dg645Channel.C).DelaySummary);
            }
        };

        await ((AsyncRelayCommand)scan.StartCommand).ExecuteAsync();

        Assert.Equal(["A + 10 ns", "A + 20 ns", "A + 30 ns", "A + 5 ns"], seen);
        Assert.Equal("2,+0.000000005000", Query(simulator, "DLAY?4"));
        Assert.False(scan.IsRunning);
        Assert.Equal(1, scan.Progress);
        Assert.StartsWith("All 3 values done, then put back to A + 5 ns", scan.ProgressText);
        Assert.False(session.CanUndo);

        var records = await RecordsAsync(audit);
        Assert.Contains(records, r => r is { Action: AuditActions.ScanStarted, Setting: "Delay C", OldValue: "A + 5 ns" });
        Assert.Contains(records, r => r is { Action: AuditActions.ScanEnded, NewValue: "A + 5 ns" });
        Assert.DoesNotContain(records, r => r.Action == AuditActions.Command);
    }

    [Fact]
    public async Task AStoppedScanCanLeaveTheLastValueForUndo()
    {
        var (session, simulator, _, _) = await CreateSessionAsync();
        var scan = session.Scan;
        scan.FromText = "1 us";
        scan.ToText = "0";
        scan.CountText = "1000";
        scan.DwellText = "50 ms";
        scan.ReturnToStart = false;

        var running = ((AsyncRelayCommand)scan.StartCommand).ExecuteAsync();
        await WaitForAsync(() => scan.Progress > 0);
        Assert.True(scan.IsRunning);
        Assert.False(session.UndoCommand.CanExecute(null));
        Assert.False(scan.StartCommand.CanExecute(null));
        scan.StopCommand.Execute(null);
        await running;

        Assert.False(scan.IsRunning);
        Assert.StartsWith("Stopped after", scan.ProgressText);
        Assert.NotEqual("0,+0.000000000000", Query(simulator, "DLAY?2"));
        Assert.True(session.CanUndo);
        await UndoAsync(session);
        Assert.Equal("0,+0.000000000000", Query(simulator, "DLAY?2"));
    }

    [Theory]
    [InlineData("x", "1 us", "11", "1 s", "From and To must be times")]
    [InlineData("0", "1 us", "1", "1 s", "Values must be a whole number from 2")]
    [InlineData("0", "1 us", "11", "1 ms", "Time at each value must be from 50 ms")]
    [InlineData("0", "3000 s", "11", "1 s", "From and To must be within")]
    public async Task AScanWithBadSettingsSaysWhatIsWrongAndDoesNotStart(string from, string to, string count, string dwell, string error)
    {
        var (session, simulator, _, _) = await CreateSessionAsync();
        var scan = session.Scan;
        (scan.FromText, scan.ToText, scan.CountText, scan.DwellText) = (from, to, count, dwell);
        Assert.StartsWith(error, scan.Description);

        await ((AsyncRelayCommand)scan.StartCommand).ExecuteAsync();
        Assert.StartsWith(error, scan.Error);
        Assert.Equal("0,+0.000000000000", Query(simulator, "DLAY?2"));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
