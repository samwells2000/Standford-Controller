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

/// <summary>The audit trail: the files, the background writer, and what gets recorded.</summary>
public sealed class AuditTrailTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tempo-audit-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.Delete(_directory);

    private string AuditFolder => Path.Combine(_directory, "Audit trail");

    private static AuditRecord Record(string setting, string? oldValue, string? newValue, int second = 0) => new()
    {
        Time = new DateTimeOffset(2026, 10, 7, 14, 3, second, TimeSpan.FromHours(1)),
        User = @"LAB\jsmith",
        Computer = "LABPC1",
        Instrument = "Laser lab DG645",
        Serial = "s/n001234",
        Address = "192.168.1.50:5025",
        Action = AuditActions.Changed,
        Setting = setting,
        OldValue = oldValue,
        NewValue = newValue,
        Software = "Tempo 1.0.0",
    };

    private async Task<(InstrumentSessionViewModel Session, Dg645Simulator Simulator, AuditLog Audit)> CreateSessionAsync()
    {
        var simulator = new Dg645Simulator();
        var client = new Dg645Client(new SimulatorTransport(simulator)) { QueryTimeout = TimeSpan.FromMilliseconds(300) };
        await client.ConnectAsync();
        var audit = new AuditLog(AuditFolder, "Tempo 1.0.0");
        var profile = new InstrumentProfile { Name = "Bench DG645", IsSimulator = true, LastKnownIdentity = client.Identity };
        var session = new InstrumentSessionViewModel(profile, client, new FakeDialogs(), new LogViewModel(), audit);
        await session.RefreshAllAsync();
        return (session, simulator, audit);
    }

    /// <summary>The records written so far, waiting until there are at least <paramref name="atLeast"/>.</summary>
    private static async Task<IReadOnlyList<AuditRecord>> RecordsAsync(AuditLog audit, int atLeast = 0)
    {
        for (var attempt = 0; ; attempt++)
        {
            Assert.Equal(0, await audit.FlushAsync());
            var records = audit.Store.Months().SelectMany(audit.Store.ReadMonth).ToList();
            if (records.Count >= atLeast || attempt == 100)
            {
                return records;
            }

            await Task.Delay(20);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public void RecordsRoundTripThroughTheFileAndTheChainIsIntact()
    {
        var store = new AuditTrailStore(AuditFolder);
        var first = store.Append(Record("Delay A", "T0 + 0 s", "T0 + 1.5 µs") with { Reason = "Job 4711, \"alignment\"" });
        store.Append(Record("AB offset", "0.00 V", "-0.80 V", second: 1) with { Details = "line one\nline two" });
        store.Append(Record("Notes", null, "=SUM(A1)", second: 2));

        var path = store.FilePathFor(first.Time, "LABPC1");
        Assert.Equal("Tempo audit trail 2026-10 LABPC1.csv", Path.GetFileName(path));
        Assert.Equal(["2026-10"], store.Months());
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));

        var records = AuditTrailStore.ReadFile(path);
        Assert.Equal(3, records.Count);
        Assert.Equal(first with { Hash = records[0].Hash }, records[0]);
        Assert.Equal("Job 4711, \"alignment\"", records[0].Reason);
        Assert.Equal("−0.80 V", records[1].NewValue); // a minus sign, so a spreadsheet does not take it for a formula
        Assert.Equal("line one; line two", records[1].Details);
        Assert.Equal("'=SUM(A1)", records[2].NewValue);
        Assert.All(records, r => Assert.Matches("^[0-9a-f]{64}$", r.Hash!));

        var check = AuditTrailStore.Verify(path);
        Assert.True(check.IsIntact, check.Message);
        Assert.Equal(3, check.Records);
    }

    [Fact]
    public void ChangedOrRemovedRecordsAreFound()
    {
        var store = new AuditTrailStore(AuditFolder);
        for (var i = 0; i < 4; i++)
        {
            store.Append(Record("Trigger rate", $"{i} kHz", $"{i + 1} kHz", second: i));
        }

        var path = store.FilePathFor(Record("x", null, null).Time, "LABPC1");
        File.SetAttributes(path, FileAttributes.Normal);
        var lines = File.ReadAllLines(path).ToList();

        // Someone edits a value in a spreadsheet...
        File.WriteAllLines(path, lines.Select((l, i) => i == 2 ? l.Replace("2 kHz", "9 kHz") : l));
        var edited = AuditTrailStore.Verify(path);
        Assert.False(edited.IsIntact);
        Assert.Equal(3, edited.FirstBadLine);

        // ...or deletes a record from the middle.
        File.WriteAllLines(path, lines.Where((_, i) => i != 2));
        var deleted = AuditTrailStore.Verify(path);
        Assert.False(deleted.IsIntact);
        Assert.Equal(3, deleted.FirstBadLine);

        File.WriteAllLines(path, lines);
        Assert.True(AuditTrailStore.Verify(path).IsIntact);
    }

    [Fact]
    public async Task RecordsWaitWhileTheFolderCannotBeWrittenAndAreNotLost()
    {
        var audit = new AuditLog(AuditFolder, "Tempo 1.0.0");
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "not a folder");
        await File.WriteAllTextAsync(blocked, "a file where the folder should be");
        audit.SetFolder(blocked);

        audit.Record(new InstrumentProfile { Name = "Bench DG645", Host = "10.0.0.2" }, "s/n000001", AuditActions.Changed, "Trigger rate", "10 kHz", "1 kHz", reason: "  Job 12  ");
        Assert.Equal(1, await audit.FlushAsync());
        await WaitForAsync(() => audit.HasProblem);
        Assert.Contains("1 record is waiting", audit.Problem);

        // On closing, the waiting records go to the default folder.
        Assert.Equal(0, await audit.FlushOnExitAsync());
        var record = Assert.Single(new AuditTrailStore(AuditFolder).ReadMonth(DateTimeOffset.Now.ToString("yyyy-MM")));
        Assert.Equal("Job 12", record.Reason);
        Assert.Equal(AuditLog.CurrentUser, record.User);
        Assert.Equal(Environment.MachineName, record.Computer);
        Assert.Equal("10.0.0.2", record.Address);
        Assert.Equal("Tempo 1.0.0", record.Software);
    }

    [Fact]
    public async Task ChangesOnThePagesAreRecordedWithOldAndNewValues()
    {
        var (session, _, audit) = await CreateSessionAsync();
        session.ChangeReason = "Change request 31";

        session.Trigger.Rate.Text = "2.5 kHz";
        await ((AsyncRelayCommand)session.Trigger.Rate.CommitCommand).ExecuteAsync();

        session.Trigger.Source.Selected = session.Trigger.Source.Options.Single(o => o.Value == (int)TriggerSource.SingleShot);
        await RecordsAsync(audit, atLeast: 2);

        var a = session.Channels.Single(c => c.Channel == Dg645Channel.A);
        a.DelayText = "1.5 us";
        await ((AsyncRelayCommand)a.CommitCommand).ExecuteAsync();

        session.Burst.Enabled.IsChecked = true;

        var records = await RecordsAsync(audit, atLeast: 4);
        Assert.Equal(4, records.Count);
        Assert.All(records, r =>
        {
            Assert.Equal(AuditActions.Changed, r.Action);
            Assert.Equal("Bench DG645", r.Instrument);
            Assert.Equal("s/n000000", r.Serial);
            Assert.Equal("Simulator", r.Address);
            Assert.Equal("Change request 31", r.Reason);
        });
        Assert.Equal(("Internal rate", "10 kHz", "2.5 kHz"), (records[0].Setting, records[0].OldValue, records[0].NewValue));
        Assert.Equal(("Trigger source", "Internal", "Single shot"), (records[1].Setting, records[1].OldValue, records[1].NewValue));
        Assert.Equal(("Delay A", "T0 + 0 s", "T0 + 1.5 µs"), (records[2].Setting, records[2].OldValue, records[2].NewValue));
        Assert.Equal("Off", records[3].OldValue);
        Assert.Equal("On", records[3].NewValue);
    }

    [Fact]
    public async Task RejectedChangesAndReadsAreNotRecorded()
    {
        var (session, _, audit) = await CreateSessionAsync();
        session.Trigger.Rate.Text = "50 MHz"; // above the 10 MHz limit
        await ((AsyncRelayCommand)session.Trigger.Rate.CommitCommand).ExecuteAsync();
        await session.RefreshAllAsync();

        Assert.Empty(await RecordsAsync(audit));
    }

    [Fact]
    public async Task ResetRecordsEachSettingItChanged()
    {
        var (session, simulator, audit) = await CreateSessionAsync();
        simulator.Process("DLAY 2,0,0.000001;TRAT 500");
        await session.RefreshAllAsync();

        await ((AsyncRelayCommand)session.ResetCommand).ExecuteAsync();
        var records = await RecordsAsync(audit);

        Assert.All(records, r => Assert.Equal(AuditActions.Reset, r.Action));
        Assert.Contains(records, r => r is { Setting: "Delay A", OldValue: "T0 + 1 µs", NewValue: "T0 + 0 s" });
        Assert.Contains(records, r => r is { Setting: "Trigger rate", OldValue: "500 Hz", NewValue: "10 kHz" });
        Assert.All(records, r => Assert.StartsWith("Reset to the default settings", r.Details));
    }

    [Fact]
    public async Task TerminalCommandsAndAnythingElseThatChangesTheInstrumentAreRecorded()
    {
        var (session, _, audit) = await CreateSessionAsync();

        session.Terminal.Input = "TRAT 500";
        await session.Terminal.SendAsync();
        session.Terminal.Input = "TRAT?";
        await session.Terminal.SendAsync();
        session.Terminal.Input = "DLAY 2,0,5000";
        await session.Terminal.SendAsync();

        // A change made without recording itself is still caught.
        await session.Client.ExecuteAsync("TLVL 0.5");

        var records = await RecordsAsync(audit);
        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.Equal(AuditActions.Command, r.Action));
        Assert.Equal(("Terminal", "TRAT 500", "No errors reported"), (records[0].Setting, records[0].NewValue, records[0].Details));
        Assert.Equal("DLAY 2,0,5000", records[1].NewValue);
        Assert.Contains("error", records[1].Details);
        Assert.Equal("TLVL 0.5", records[2].NewValue);
    }

    [Fact]
    public async Task ConfigurationChangesAreRecordedAndTheWindowShowsThem()
    {
        var dialogs = new FakeDialogs();
        var settings = new AppSettingsStore(Path.Combine(_directory, "settings.json"));
        var main = new MainViewModel(Path.Combine(_directory, "Default.tempo"), dialogs, settings);
        await main.LoadAsync();
        Assert.Equal(AuditFolder, main.Audit.Folder);

        var item = main.AddProfile(new InstrumentProfile { Name = "Beamline", Host = "10.0.0.7", Location = "Hutch 2" });
        dialogs.OnEditProfile = editor =>
        {
            editor.Host = "10.0.0.8";
            editor.Location = "Hutch 3";
            return editor.TrySave();
        };
        main.EditInstrumentCommand.Execute(item);
        await WaitForAsync(() => item.Profile.Host == "10.0.0.8");
        await ((AsyncRelayCommand)main.RemoveInstrumentCommand).ExecuteAsync();

        var records = await RecordsAsync(main.Audit);
        Assert.Equal(
            [AuditActions.InstrumentAdded, AuditActions.InstrumentEdited, AuditActions.InstrumentEdited, AuditActions.InstrumentRemoved],
            records.Select(r => r.Action));
        Assert.Equal("Added to Default: 10.0.0.7, Hutch 2", records[0].Details);
        Assert.Contains(records, r => r is { Setting: "Address", OldValue: "10.0.0.7", NewValue: "10.0.0.8" });
        Assert.Contains(records, r => r is { Setting: "Location", OldValue: "Hutch 2", NewValue: "Hutch 3" });

        main.AuditTrailCommand.Execute(null);
        var window = Assert.Single(dialogs.AuditTrailsShown);
        Assert.Equal(4, window.Records.Count);
        Assert.Equal(AuditActions.InstrumentRemoved, window.Records[0].Action); // newest first
        window.SearchText = "hutch 3";
        Assert.Single(window.Records);
        window.CheckIntegrity();
        Assert.False(window.IntegrityIsProblem, window.IntegrityText);

        // Choosing another folder is remembered.
        dialogs.FolderPath = Path.Combine(_directory, "Shared audit");
        await ((AsyncRelayCommand)window.ChangeFolderCommand).ExecuteAsync();
        Assert.Equal(dialogs.FolderPath, main.Audit.Folder);
        Assert.Equal(dialogs.FolderPath, settings.Load().AuditFolder);
        Assert.True(window.IsEmpty);
    }
}
