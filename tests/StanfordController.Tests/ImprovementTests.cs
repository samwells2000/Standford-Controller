using StanfordController.Core;
using StanfordController.Core.Profiles;
using StanfordController.Core.Protocol;
using StanfordController.Core.Simulation;
using StanfordController.Core.Transport;
using StanfordController.Presentation.Mvvm;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.Tests;

public sealed class ImprovementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sc-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static async Task<(InstrumentSessionViewModel Session, LogViewModel Log)> CreateSessionAsync()
    {
        var log = new LogViewModel();
        var client = new Dg645Client(new SimulatorTransport(new Dg645Simulator())) { QueryTimeout = TimeSpan.FromMilliseconds(200) };
        client.Communication += (_, e) => log.Add(e.Direction, e.Text);
        await client.ConnectAsync();
        var session = new InstrumentSessionViewModel(new InstrumentProfile { Name = "Test", IsSimulator = true }, client, new FakeDialogs(), log);
        await session.RefreshAllAsync();
        return (session, log);
    }

    [Fact]
    public void SettingsRoundTripAndSurviveACorruptFile()
    {
        var store = new AppSettingsStore(Path.Combine(_directory, "settings.json"));
        Assert.Equal(AppTheme.System, store.Load().Theme);

        var id = Guid.NewGuid();
        store.Save(new AppSettings
        {
            Theme = AppTheme.Dark,
            SelectedInstrumentId = id,
            ShowLog = false,
            Window = new WindowPlacement { Left = 10, Top = 20, Width = 1400, Height = 900, Maximized = true },
        });

        var loaded = store.Load();
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(id, loaded.SelectedInstrumentId);
        Assert.False(loaded.ShowLog);
        Assert.True(loaded.Window!.Maximized);
        Assert.Contains("\"Dark\"", File.ReadAllText(store.FilePath));

        File.WriteAllText(store.FilePath, "{ not json");
        Assert.Equal(AppTheme.System, store.Load().Theme);
    }

    [Fact]
    public void MainViewModelRemembersThemeSelectionAndLogVisibility()
    {
        var profiles = new ProfileStore(Path.Combine(_directory, "instruments.json"));
        var settings = new AppSettingsStore(Path.Combine(_directory, "settings.json"));

        var first = new MainViewModel(profiles, new FakeDialogs(), settings);
        first.Load();
        first.AddDemoInstrumentCommand.Execute(null);
        first.AddDemoInstrumentCommand.Execute(null);
        var second = first.Instruments[1];
        first.SelectedInstrument = second;
        first.IsDarkTheme = true;
        first.ShowLog = false;
        Assert.All(first.Instruments, i => Assert.False(i.Log.IsPanelVisible));
        Assert.Equal($"{second.Name} — {MainViewModel.AppName}", first.WindowTitle);

        var reopened = new MainViewModel(profiles, new FakeDialogs(), settings);
        reopened.Load();
        Assert.Equal(AppTheme.Dark, reopened.Theme);
        Assert.True(reopened.IsDarkTheme);
        Assert.False(reopened.IsLightTheme);
        Assert.Equal(second.Profile.Id, reopened.SelectedInstrument!.Profile.Id);
        Assert.False(reopened.ShowLog);
        Assert.All(reopened.Instruments, i => Assert.False(i.Log.IsPanelVisible));

        // Toggling one instrument's log applies to all.
        reopened.Instruments[0].Log.IsPanelVisible = true;
        Assert.True(reopened.ShowLog);
        Assert.True(reopened.Instruments[1].Log.IsPanelVisible);
    }

    [Fact]
    public async Task TriggerButtonIsOnlyAvailableInSingleShotModes()
    {
        var (session, _) = await CreateSessionAsync();
        Assert.False(session.TriggerCommand.CanExecute(null));

        var changed = 0;
        session.TriggerCommand.CanExecuteChanged += (_, _) => changed++;
        session.Trigger.Source.Selected = session.Trigger.Source.Options.Single(o => o.Value == (int)TriggerSource.SingleShot);
        for (var i = 0; i < 200 && !session.Trigger.IsSingleShot; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(session.Trigger.IsSingleShot);
        Assert.True(session.TriggerCommand.CanExecute(null));
        Assert.True(changed > 0);
    }

    [Fact]
    public async Task TerminalSetCommandsFlagPagesUntilRefreshed()
    {
        var (session, _) = await CreateSessionAsync();
        session.Terminal.Input = "TRAT?";
        await session.Terminal.SendAsync();
        Assert.False(session.SettingsMayBeStale);

        session.Terminal.Input = "TRAT 2500";
        await session.Terminal.SendAsync();
        Assert.True(session.SettingsMayBeStale);
        Assert.Equal("10 kHz", session.Trigger.Rate.Text);

        await session.RefreshAllAsync();
        Assert.False(session.SettingsMayBeStale);
        Assert.Equal("2.5 kHz", session.Trigger.Rate.Text);
    }

    [Fact]
    public async Task StatusPollingKeepsTheLogQuiet()
    {
        var (session, log) = await CreateSessionAsync();
        var before = log.Entries.Count;
        await session.Status.PollAsync();
        await session.Status.PollAsync();
        Assert.Equal(before, log.Entries.Count);

        // Ordinary queries are still logged.
        await session.Client.GetTriggerRateAsync();
        Assert.Contains(log.Entries.Skip(before), e => e.Text == "TRAT?");
    }

    [Fact]
    public async Task OutputCardsDescribeTheirPulse()
    {
        var (session, _) = await CreateSessionAsync();
        await session.Client.SetDelayAsync(Dg645Channel.A, Dg645Channel.T0, 1e-6);
        await session.Client.SetDelayAsync(Dg645Channel.B, Dg645Channel.A, 250e-9);
        await session.Client.SetDelayAsync(Dg645Channel.D, Dg645Channel.T0, 0);
        await session.Client.SetDelayAsync(Dg645Channel.C, Dg645Channel.T0, 2e-6);
        await session.RefreshAllAsync();

        Assert.Equal("Pulse T0 + 1 µs → T0 + 1.25 µs, 250 ns wide", session.Outputs.Single(o => o.Output == Dg645Output.AB).TimingText);
        Assert.Equal("No pulse: D is set before C.", session.Outputs.Single(o => o.Output == Dg645Output.CD).TimingText);
        Assert.Equal("Fires at the start of every delay cycle.", session.Outputs.Single(o => o.Output == Dg645Output.T0).TimingText);
    }

    [Theory]
    [InlineData("192.168.1.300", false)]
    [InlineData("10.0.1", false)]
    [InlineData("192.168.1.30", true)]
    [InlineData("dg645-lab2", true)]
    [InlineData("dg645.example.org", true)]
    public void HostValidationCatchesMalformedAddresses(string host, bool valid)
    {
        var problems = new InstrumentProfile { Name = "X", Host = host }.Validate([]);
        Assert.Equal(valid, problems.Count == 0);
    }

    [Fact]
    public async Task SuccessfulChangeClearsAnEarlierErrorFromTheStatusLine()
    {
        var (session, _) = await CreateSessionAsync();
        session.Burst.Count.Text = "0";
        await ((AsyncRelayCommand)session.Burst.Count.CommitCommand).ExecuteAsync();
        Assert.True(session.Burst.Count.HasError);

        session.Burst.Count.Text = "5";
        await ((AsyncRelayCommand)session.Burst.Count.CommitCommand).ExecuteAsync();
        Assert.False(session.StatusIsError);
        Assert.StartsWith("Change applied", session.StatusMessage);
    }
}
