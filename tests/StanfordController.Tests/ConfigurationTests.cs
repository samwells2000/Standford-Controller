using StanfordController.Core;
using StanfordController.Core.Profiles;
using StanfordController.Core.Protocol;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.Tests;

/// <summary>Configuration files, saved setups and what happens to them when an instrument connects.</summary>
public sealed class ConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "strobe-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string DefaultPath => Path.Combine(_directory, "Default.strobe");

    private async Task<(MainViewModel Main, FakeDialogs Dialogs)> OpenAsync(string? settingsName = "settings.json")
    {
        var dialogs = new FakeDialogs();
        var main = new MainViewModel(DefaultPath, dialogs, settingsName is null ? null : new AppSettingsStore(Path.Combine(_directory, settingsName)));
        await main.LoadAsync();
        return (main, dialogs);
    }

    /// <summary>Connects the demo instrument and changes a few settings so it differs from a fresh simulator.</summary>
    private static async Task<InstrumentItemViewModel> ConnectAndConfigureAsync(MainViewModel main)
    {
        var item = main.Instruments.Single();
        await item.ConnectAsync();
        var client = item.Session!.Client;
        await client.SetDelayAsync(Dg645Channel.A, Dg645Channel.T0, 1.5e-6);
        await client.SetTriggerRateAsync(2500);
        await client.SetAmplitudeAsync(Dg645Output.CD, 1.2);
        await item.Session.RefreshAllAsync();
        return item;
    }

    [Fact]
    public async Task SavedSetupsAreStoredInTheConfigurationFile()
    {
        var (main, _) = await OpenAsync();
        main.AddDemoInstrumentCommand.Execute(null);
        var item = await ConnectAndConfigureAsync(main);
        item.Session!.NotifySettingsChanged();
        Assert.True(main.HasUnsavedChanges);
        Assert.EndsWith("*" + " — Strobe", main.WindowTitle);

        Assert.True(await main.SaveAllSetupsAsync());
        Assert.False(main.HasUnsavedChanges);
        Assert.True(item.HasStoredSetup);

        var stored = Assert.Single(new ConfigurationStore(DefaultPath).Load()).StoredSetup;
        Assert.NotNull(stored);
        Assert.Equal(1.5e-6, stored.Delays.Single(d => d.Channel == Dg645Channel.A).DelaySeconds, 12);
        Assert.Equal(2500, stored.Trigger.RateHz, 6);
        Assert.Equal(1.2, stored.Outputs.Single(o => o.Output == Dg645Output.CD).AmplitudeVolts, 2);
        Assert.Contains("\"StoredSetup\"", await File.ReadAllTextAsync(DefaultPath));
    }

    [Fact]
    public async Task ConnectingAsksWhichSettingsToUseWhenTheyDiffer()
    {
        var (main, dialogs) = await OpenAsync();
        main.AddDemoInstrumentCommand.Execute(null);
        var item = await ConnectAndConfigureAsync(main);
        await item.SaveSetupAsync();
        await item.DisconnectAsync();

        // A fresh simulator has the factory settings, so it differs from the saved setup.
        dialogs.AskAnswer = new DialogAnswer(DialogChoice.Yes, Remember: false);
        await item.ConnectAsync();

        var question = Assert.Single(dialogs.Questions);
        Assert.Contains("Delay A", question);
        Assert.Contains("Trigger rate", question);
        Assert.Contains("CD amplitude", question);
        Assert.Equal("2.5 kHz", item.Session!.Trigger.Rate.Text);
        Assert.Equal("1.20 V", item.Session.Outputs.Single(o => o.Output == Dg645Output.CD).Amplitude.Text);
        Assert.False(item.HasUnsavedChanges);
        Assert.Equal(SetupOnConnect.Ask, item.Profile.SetupOnConnect);
    }

    [Fact]
    public async Task KeepingTheInstrumentSettingsLeavesThemAndCanBeRemembered()
    {
        var (main, dialogs) = await OpenAsync();
        main.AddDemoInstrumentCommand.Execute(null);
        var item = await ConnectAndConfigureAsync(main);
        await item.SaveSetupAsync();
        await item.DisconnectAsync();

        dialogs.AskAnswer = new DialogAnswer(DialogChoice.No, Remember: true);
        await item.ConnectAsync();

        Assert.Single(dialogs.Questions);
        Assert.Equal("10 kHz", item.Session!.Trigger.Rate.Text);
        Assert.True(item.HasUnsavedChanges);
        Assert.Equal(SetupOnConnect.KeepInstrument, item.Profile.SetupOnConnect);
        Assert.Equal(SetupOnConnect.KeepInstrument, Assert.Single(new ConfigurationStore(DefaultPath).Load()).SetupOnConnect);

        // Remembered: the next connection does not ask.
        await item.DisconnectAsync();
        await item.ConnectAsync();
        Assert.Single(dialogs.Questions);
        Assert.Equal("10 kHz", item.Session!.Trigger.Rate.Text);
    }

    [Fact]
    public async Task RestoreOnConnectAppliesTheSavedSetupWithoutAsking()
    {
        var (main, dialogs) = await OpenAsync();
        main.AddDemoInstrumentCommand.Execute(null);
        var item = await ConnectAndConfigureAsync(main);
        await item.SaveSetupAsync();
        await item.DisconnectAsync();
        item.Profile.SetupOnConnect = SetupOnConnect.Restore;

        await item.ConnectAsync();

        Assert.Empty(dialogs.Questions);
        Assert.Equal("2.5 kHz", item.Session!.Trigger.Rate.Text);
        Assert.Equal("1.5 µs", item.Session.Channels.Single(c => c.Channel == Dg645Channel.A).DelayText);
    }

    [Fact]
    public async Task NoQuestionWhenTheInstrumentAlreadyMatches()
    {
        var (main, dialogs) = await OpenAsync();
        main.AddDemoInstrumentCommand.Execute(null);
        var item = main.Instruments.Single();
        await item.ConnectAsync();
        await item.SaveSetupAsync(); // factory settings, same as a fresh simulator
        await item.DisconnectAsync();

        await item.ConnectAsync();
        Assert.Empty(dialogs.Questions);
        Assert.False(item.HasUnsavedChanges);
        Assert.Contains("match", item.Session!.StatusMessage);
    }

    [Fact]
    public async Task ClosingWithUnsavedSetupsOffersToSave()
    {
        var (main, dialogs) = await OpenAsync();
        main.AddDemoInstrumentCommand.Execute(null);
        var item = await ConnectAndConfigureAsync(main);
        item.Session!.NotifySettingsChanged();

        dialogs.AskAnswer = new DialogAnswer(DialogChoice.Cancel);
        Assert.False(await main.ConfirmCloseAsync());

        dialogs.AskAnswer = new DialogAnswer(DialogChoice.No);
        Assert.True(await main.ConfirmCloseAsync());
        Assert.Null(Assert.Single(new ConfigurationStore(DefaultPath).Load()).StoredSetup);

        dialogs.AskAnswer = new DialogAnswer(DialogChoice.Yes);
        Assert.True(await main.ConfirmCloseAsync());
        Assert.NotNull(Assert.Single(new ConfigurationStore(DefaultPath).Load()).StoredSetup);
        Assert.False(main.HasUnsavedChanges);
    }

    [Fact]
    public async Task OpenedConfigurationIsReopenedAtStartupAndListedAsRecent()
    {
        var lab = new ConfigurationStore(Path.Combine(_directory, "Laser lab.strobe"));
        lab.Save([new InstrumentProfile { Name = "Lab DG645", Host = "10.0.0.20" }]);

        var (main, _) = await OpenAsync();
        Assert.Equal("Default", main.ConfigurationName);
        await main.OpenConfigurationAsync(lab.FilePath);
        Assert.Equal("Laser lab", main.ConfigurationName);
        Assert.Equal("Lab DG645", Assert.Single(main.Instruments).Name);
        Assert.Equal(lab.FilePath, Assert.Single(main.RecentConfigurations).Path);

        var (reopened, _) = await OpenAsync();
        Assert.Equal("Laser lab", reopened.ConfigurationName);
        Assert.Equal("Laser lab — Strobe", reopened.WindowTitle);

        // If that file disappears, the default configuration opens instead.
        File.Delete(lab.FilePath);
        var (fallback, _) = await OpenAsync();
        Assert.Equal("Default", fallback.ConfigurationName);
        Assert.Contains("could not be found", fallback.StatusMessage);
    }

    [Fact]
    public async Task SaveConfigurationAsCopiesEverythingAndSwitchesToTheNewFile()
    {
        var (main, dialogs) = await OpenAsync();
        main.AddDemoInstrumentCommand.Execute(null);
        await ConnectAndConfigureAsync(main);

        dialogs.SaveFilePath = Path.Combine(_directory, "Experiment 7.strobe");
        main.SaveConfigurationAsCommand.Execute(null);
        for (var i = 0; i < 200 && main.ConfigurationName != "Experiment 7"; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("Experiment 7", main.ConfigurationName);
        var saved = Assert.Single(new ConfigurationStore(dialogs.SaveFilePath).Load());
        Assert.NotNull(saved.StoredSetup);
    }

    [Fact]
    public void VersionOneInstrumentListsStillOpen()
    {
        var json = """
                   { "FormatVersion": 1, "Instruments": [ { "Name": "Old", "Host": "10.1.1.1", "Port": 5025 } ] }
                   """;
        var profile = Assert.Single(ConfigurationStore.Deserialize(json));
        Assert.Equal("Old", profile.Name);
        Assert.Null(profile.StoredSetup);
        Assert.Equal(SetupOnConnect.Ask, profile.SetupOnConnect);

        Assert.Throws<InvalidDataException>(() => ConfigurationStore.Deserialize("""{ "FormatVersion": 99, "Instruments": [] }"""));
    }

    [Fact]
    public void DifferencesIgnoreTheInstrumentsResolution()
    {
        var a = new Dg645Settings
        {
            Delays = [new ChannelDelaySettings { Channel = Dg645Channel.A, DelaySeconds = 1e-6 }],
            Outputs = [new OutputLevelSettings { Output = Dg645Output.AB, AmplitudeVolts = 2.5 }],
        };
        var b = Dg645Settings.FromJson(a.ToJson());
        Assert.Empty(a.DescribeDifferences(b));

        b.Delays[0].DelaySeconds = 1e-6 + 1e-13; // below the 1 ps resolution
        b.Outputs[0].AmplitudeVolts = 2.501;
        Assert.Empty(a.DescribeDifferences(b));

        b.Delays[0].Reference = Dg645Channel.T1;
        b.Outputs[0].Polarity = OutputPolarity.Negative;
        b.Burst.Enabled = true;
        Assert.Equal(["Burst mode", "Delay A", "AB polarity"], a.DescribeDifferences(b));
    }

    [Fact]
    public void PortableDataLivesBesideTheProgram()
    {
        var paths = AppPaths.ForPortable(_directory);
        Assert.NotNull(paths);
        Assert.True(paths.IsPortable);
        Assert.Equal(Path.Combine(_directory, "Strobe-data"), paths.DataDirectory);
        Assert.True(Directory.Exists(paths.DataDirectory));
        Assert.Equal(Path.Combine(paths.DataDirectory, "Default.strobe"), paths.DefaultConfigurationPath);
        Assert.False(paths.MigrateLegacyInstruments());
    }

    [Fact]
    public async Task StatusBarSummariesFollowTriggerAndBurst()
    {
        var (main, _) = await OpenAsync(settingsName: null);
        main.AddDemoInstrumentCommand.Execute(null);
        var item = main.Instruments.Single();
        await item.ConnectAsync();
        var session = item.Session!;
        Assert.Equal("Internal, 10 kHz", session.TriggerSummary);
        Assert.Equal("Off", session.BurstSummary);

        await session.Client.SetBurstModeAsync(true);
        await session.Client.SetBurstCountAsync(5);
        await session.Client.SetTriggerSourceAsync(TriggerSource.SingleShot);
        await session.RefreshAllAsync();
        Assert.Equal("Single shot", session.TriggerSummary);
        Assert.Equal("5 × 1 µs", session.BurstSummary);
    }
}
