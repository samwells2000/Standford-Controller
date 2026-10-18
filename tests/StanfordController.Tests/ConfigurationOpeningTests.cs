using StanfordController.Core;
using StanfordController.Core.Profiles;
using StanfordController.Core.Protocol;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.Tests;

/// <summary>Opening a configuration of several instruments: connecting them all, skipping one that is not there, and noticing a different unit.</summary>
public sealed class ConfigurationOpeningTests : IDisposable
{
    private const string OtherUnit = "Stanford Research Systems,DG645,s/n001234,ver1.14.10E";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sc-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestFolders.Delete(_directory);
    }

    /// <summary>Nothing listens on this port, so connecting fails straight away.</summary>
    private static InstrumentProfile Unreachable(string name) => new() { Name = name, Host = "127.0.0.1", Port = 1, TimeoutMs = 500 };

    private async Task<(MainViewModel Main, FakeDialogs Dialogs, AppSettingsStore Settings)> CreateMainAsync()
    {
        var dialogs = new FakeDialogs();
        var settings = new AppSettingsStore(Path.Combine(_directory, "settings.json"));
        var main = new MainViewModel(Path.Combine(_directory, "Default.tempo"), dialogs, settings);
        await main.LoadAsync();
        return (main, dialogs, settings);
    }

    private string SaveConfiguration(string name, params InstrumentProfile[] instruments)
    {
        var store = new ConfigurationStore(Path.Combine(_directory, name + ConfigurationStore.FileExtension));
        store.Save(instruments);
        return store.FilePath;
    }

    private static InstrumentItemViewModel Item(MainViewModel main, string name) => main.Instruments.Single(i => i.Name == name);

    [Fact]
    public async Task OpeningOffersToConnectEveryInstrumentAndSkipsOneThatCannotBeReached()
    {
        var (main, dialogs, _) = await CreateMainAsync();
        var path = SaveConfiguration("Laser lab",
            new InstrumentProfile { Name = "Pump", IsSimulator = true },
            Unreachable("Probe"),
            new InstrumentProfile { Name = "Camera", IsSimulator = true });
        dialogs.AskAnswer = new DialogAnswer(DialogChoice.Yes);

        await main.OpenConfigurationAsync(path);

        Assert.Contains(dialogs.Questions, q => q.Contains("has 3 instruments") && q.Contains("Connect to all of them now?"));
        Assert.True(Item(main, "Pump").IsConnected);
        Assert.Equal(ConnectionState.Failed, Item(main, "Probe").State);
        Assert.True(Item(main, "Camera").IsConnected);
        Assert.Contains("2 of 3", main.StatusMessage);
        Assert.Contains("Probe", main.StatusMessage);
        Assert.False(main.ConnectAllOnOpen);
    }

    [Fact]
    public async Task DecliningConnectsOnlyTheInstrumentsMarkedToConnectOnOpening()
    {
        var (main, dialogs, _) = await CreateMainAsync();
        var path = SaveConfiguration("Laser lab",
            new InstrumentProfile { Name = "Marked", IsSimulator = true, ConnectOnStartup = true },
            new InstrumentProfile { Name = "Unmarked", IsSimulator = true });
        dialogs.AskAnswer = new DialogAnswer(DialogChoice.No);

        await main.OpenConfigurationAsync(path);

        Assert.True(Item(main, "Marked").IsConnected);
        Assert.False(Item(main, "Unmarked").IsConnected);
    }

    [Fact]
    public async Task RememberingTheChoiceConnectsEverythingNextTimeWithoutAsking()
    {
        var (main, dialogs, settings) = await CreateMainAsync();
        var first = SaveConfiguration("First", new InstrumentProfile { Name = "A", IsSimulator = true });
        var second = SaveConfiguration("Second", new InstrumentProfile { Name = "B", IsSimulator = true });
        dialogs.AskAnswer = new DialogAnswer(DialogChoice.Yes, Remember: true);

        await main.OpenConfigurationAsync(first);
        Assert.True(main.ConnectAllOnOpen);
        Assert.True(settings.Load().ConnectAllOnOpen);

        dialogs.Questions.Clear();
        await main.OpenConfigurationAsync(second);
        Assert.Empty(dialogs.Questions);
        Assert.True(Item(main, "B").IsConnected);
    }

    [Fact]
    public async Task EachInstrumentGetsItsOwnSavedSetupOnOpening()
    {
        static Dg645Settings Setup(double delayA) => new()
        {
            Delays = [new ChannelDelaySettings { Channel = Dg645Channel.A, Reference = Dg645Channel.T0, DelaySeconds = delayA }],
        };

        var (main, dialogs, _) = await CreateMainAsync();
        var path = SaveConfiguration("Laser lab",
            new InstrumentProfile { Name = "One", IsSimulator = true, StoredSetup = Setup(1e-6), SetupOnConnect = SetupOnConnect.Restore },
            new InstrumentProfile { Name = "Two", IsSimulator = true, StoredSetup = Setup(2e-6), SetupOnConnect = SetupOnConnect.Restore });
        dialogs.AskAnswer = new DialogAnswer(DialogChoice.Yes);

        await main.OpenConfigurationAsync(path);

        Assert.Equal(1e-6, (await Item(main, "One").Session!.Client.GetDelayAsync(Dg645Channel.A)).Seconds, 12);
        Assert.Equal(2e-6, (await Item(main, "Two").Session!.Client.GetDelayAsync(Dg645Channel.A)).Seconds, 12);
    }

    [Fact]
    public async Task ADifferentUnitCanBeUsedInPlaceOfTheOldOne()
    {
        var (main, dialogs, _) = await CreateMainAsync();
        var item = main.AddProfile(new InstrumentProfile { Name = "Pump", IsSimulator = true, LastKnownIdentity = OtherUnit });
        dialogs.AskAnswer = new DialogAnswer(DialogChoice.Yes);

        await item.ConnectAsync();

        Assert.Contains(dialogs.Questions, q => q.Contains("A different DG645 answered") && q.Contains("s/n001234") && q.Contains("s/n000000"));
        Assert.True(item.IsConnected);
        Assert.Equal("s/n000000", InstrumentProfile.SerialNumberOf(item.Profile.LastKnownIdentity));

        // The new unit is now the one expected, so reconnecting does not ask again.
        await item.DisconnectAsync();
        dialogs.Questions.Clear();
        await item.ConnectAsync();
        Assert.Empty(dialogs.Questions);
    }

    [Fact]
    public async Task CancellingLeavesADifferentUnitDisconnectedAndKeepsTheOldSerial()
    {
        var (main, dialogs, _) = await CreateMainAsync();
        var item = main.AddProfile(new InstrumentProfile { Name = "Pump", IsSimulator = true, LastKnownIdentity = OtherUnit });
        dialogs.AskAnswer = new DialogAnswer(DialogChoice.No);

        await item.ConnectAsync();

        Assert.False(item.IsConnected);
        Assert.Equal(ConnectionState.Failed, item.State);
        Assert.Contains("s/n001234", item.LastError);
        Assert.Equal(OtherUnit, item.Profile.LastKnownIdentity);
    }

    [Fact]
    public async Task OnOpeningEachDifferentUnitCanBeSkippedOrTheRestCancelled()
    {
        var (main, dialogs, _) = await CreateMainAsync();
        var path = SaveConfiguration("Laser lab",
            new InstrumentProfile { Name = "One", IsSimulator = true, LastKnownIdentity = OtherUnit },
            new InstrumentProfile { Name = "Two", IsSimulator = true, LastKnownIdentity = OtherUnit },
            new InstrumentProfile { Name = "Three", IsSimulator = true },
            new InstrumentProfile { Name = "Four", IsSimulator = true, LastKnownIdentity = OtherUnit },
            new InstrumentProfile { Name = "Five", IsSimulator = true });
        dialogs.AskAnswers.Enqueue(new DialogAnswer(DialogChoice.Yes)); // Connect all
        dialogs.AskAnswers.Enqueue(new DialogAnswer(DialogChoice.Yes)); // One: use the new unit
        dialogs.AskAnswers.Enqueue(new DialogAnswer(DialogChoice.No)); // Two: skip
        dialogs.AskAnswers.Enqueue(new DialogAnswer(DialogChoice.Cancel)); // Four: stop connecting the rest

        await main.OpenConfigurationAsync(path);

        Assert.True(Item(main, "One").IsConnected);
        Assert.False(Item(main, "Two").IsConnected);
        Assert.True(Item(main, "Three").IsConnected);
        Assert.False(Item(main, "Four").IsConnected);
        Assert.Equal(ConnectionState.Disconnected, Item(main, "Five").State);
        Assert.StartsWith("Stopped connecting", main.StatusMessage);
    }

    [Theory]
    [InlineData("Stanford Research Systems,DG645,s/n001234,ver1.14.10E", "s/n001234")]
    [InlineData("Stanford Research Systems,DG645", null)]
    [InlineData(null, null)]
    public void SerialNumberIsReadFromTheIdentity(string? identity, string? serial) =>
        Assert.Equal(serial, InstrumentProfile.SerialNumberOf(identity));
}
