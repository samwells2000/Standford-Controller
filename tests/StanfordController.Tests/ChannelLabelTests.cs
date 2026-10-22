using StanfordController.Core.Profiles;
using StanfordController.Core.Protocol;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.Tests;

/// <summary>Channel labels, kept with each instrument in the configuration, and the All channels page.</summary>
public sealed class ChannelLabelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sc-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestFolders.Delete(_directory);
    }

    private async Task<MainViewModel> CreateMainAsync()
    {
        var main = new MainViewModel(Path.Combine(_directory, "Default.tempo"), new FakeDialogs(),
            new AppSettingsStore(Path.Combine(_directory, "settings.json")));
        await main.LoadAsync();
        return main;
    }

    private static void Label(InstrumentSessionViewModel session, Dg645Channel channel, string text)
    {
        var vm = session.Channels.Single(c => c.Channel == channel);
        vm.LabelText = text;
        vm.CommitLabelCommand.Execute(null);
    }

    [Fact]
    public async Task ALabelIsSavedInTheConfigurationAndShownWhenTheInstrumentConnectsAgain()
    {
        var main = await CreateMainAsync();
        var item = main.AddProfile(new InstrumentProfile { Name = "Pump", IsSimulator = true });
        await item.ConnectAsync();

        Label(item.Session!, Dg645Channel.A, "  Q-switch ");
        Label(item.Session!, Dg645Channel.C, "Camera gate");

        var saved = new ConfigurationStore(main.ConfigurationPath).Load().Single();
        Assert.Equal("Q-switch", saved.LabelOf(Dg645Channel.A));
        Assert.Equal("Camera gate", saved.LabelOf(Dg645Channel.C));
        Assert.Equal(string.Empty, saved.LabelOf(Dg645Channel.B));
        Assert.Equal(2, saved.ChannelLabels.Count);

        await item.DisconnectAsync();
        await main.OpenConfigurationAsync(main.ConfigurationPath);
        var reopened = main.Instruments.Single();
        await reopened.ConnectAsync();
        var a = reopened.Session!.Channels.Single(c => c.Channel == Dg645Channel.A);
        Assert.Equal("Q-switch", a.ChannelLabel);
        Assert.Equal("Q-switch", a.LabelText);
        Assert.Equal("Start of AB pulse: Q-switch", a.RoleAndLabel);
    }

    [Fact]
    public async Task ClearingALabelRemovesItAndEscapePutsTheOldOneBack()
    {
        var main = await CreateMainAsync();
        var item = main.AddProfile(new InstrumentProfile { Name = "Pump", IsSimulator = true });
        await item.ConnectAsync();
        var a = item.Session!.Channels.Single(c => c.Channel == Dg645Channel.A);

        Label(item.Session!, Dg645Channel.A, "Q-switch");
        a.LabelText = "Typo";
        a.RevertLabelCommand.Execute(null);
        Assert.Equal("Q-switch", a.LabelText);

        Label(item.Session!, Dg645Channel.A, "   ");
        Assert.False(a.HasLabel);
        Assert.Empty(new ConfigurationStore(main.ConfigurationPath).Load().Single().ChannelLabels);
    }

    [Fact]
    public async Task ALabelSetAfterEditingTheInstrumentIsKeptWithTheEditedInstrument()
    {
        var main = await CreateMainAsync();
        var item = main.AddProfile(new InstrumentProfile { Name = "Pump", IsSimulator = true });
        await item.ConnectAsync();
        Label(item.Session!, Dg645Channel.A, "Q-switch");

        // Instrument ▸ Edit replaces the saved instrument with an edited copy while it stays connected.
        var edited = item.Profile.Clone();
        edited.Name = "Pump laser";
        item.UpdateProfile(edited);
        Label(item.Session!, Dg645Channel.B, "Q-switch end");

        var saved = new ConfigurationStore(main.ConfigurationPath).Load().Single();
        Assert.Equal("Pump laser", saved.Name);
        Assert.Equal("Q-switch", saved.LabelOf(Dg645Channel.A));
        Assert.Equal("Q-switch end", saved.LabelOf(Dg645Channel.B));
    }

    [Fact]
    public void CopiesHaveTheirOwnLabelsAndOlderFilesHaveNone()
    {
        var profile = new InstrumentProfile { Name = "Pump" };
        profile.SetLabel(Dg645Channel.A, "Q-switch");
        var copy = profile.Clone();
        copy.SetLabel(Dg645Channel.A, "Flash lamp");
        Assert.Equal("Q-switch", profile.LabelOf(Dg645Channel.A));
        Assert.False(profile.SetLabel(Dg645Channel.A, "Q-switch"));

        var older = ConfigurationStore.Deserialize("""{ "FormatVersion": 2, "Instruments": [ { "Name": "A" }, { "Name": "B", "ChannelLabels": null } ] }""");
        Assert.All(older, p => Assert.Empty(p.ChannelLabels));
    }

    [Fact]
    public async Task AllChannelsListsEveryConnectedInstrumentAndFindsChannelsByLabel()
    {
        var main = await CreateMainAsync();
        var pump = main.AddProfile(new InstrumentProfile { Name = "Pump", IsSimulator = true, Location = "Laser room" });
        var probe = main.AddProfile(new InstrumentProfile { Name = "Probe", IsSimulator = true });
        var spare = main.AddProfile(new InstrumentProfile { Name = "Spare", IsSimulator = true });
        await pump.ConnectAsync();
        await probe.ConnectAsync();
        var overview = main.ChannelOverview;

        Assert.Equal(["Pump", "Probe"], overview.Groups.Select(g => g.Instrument.Name));
        Assert.All(overview.Groups, g => Assert.Equal(8, g.VisibleChannels.Count));
        Assert.Equal("2 instruments connected, 16 channels, 0 labelled. 1 more instrument is not connected.", overview.Summary);

        Label(pump.Session!, Dg645Channel.A, "Q-switch");
        Label(probe.Session!, Dg645Channel.E, "Camera gate");
        Label(probe.Session!, Dg645Channel.G, "Pockels cell");
        Assert.Contains("3 labelled", overview.Summary);

        overview.Filter = "camera";
        Assert.False(overview.Groups[0].IsVisible);
        Assert.Equal([Dg645Channel.E], overview.Groups[1].VisibleChannels.Select(c => c.Channel));

        // A label changed while searching shows or hides its channel straight away.
        Label(pump.Session!, Dg645Channel.D, "Spare camera trigger");
        Assert.Equal([Dg645Channel.D], overview.Groups[0].VisibleChannels.Select(c => c.Channel));

        // The instrument (name or location), channel letter and output narrow it down too.
        overview.Filter = "laser room";
        Assert.Equal(8, overview.Groups[0].VisibleChannels.Count);
        Assert.False(overview.Groups[1].IsVisible);
        overview.Filter = "probe gh";
        Assert.Equal([Dg645Channel.G, Dg645Channel.H], overview.Groups[1].VisibleChannels.Select(c => c.Channel));

        overview.Filter = "nothing like this";
        Assert.True(overview.NothingMatches);
        overview.ClearFilterCommand.Execute(null);
        Assert.False(overview.NothingMatches);

        // Connecting and disconnecting update the page.
        await spare.ConnectAsync();
        Assert.Equal(3, overview.Groups.Count);
        await pump.DisconnectAsync();
        Assert.Equal(["Probe", "Spare"], overview.Groups.Select(g => g.Instrument.Name));

        overview.Groups[1].ShowCommand.Execute(null);
        Assert.Same(spare, main.SelectedInstrument);
    }
}
