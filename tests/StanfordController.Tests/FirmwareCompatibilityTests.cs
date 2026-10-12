using System.Diagnostics;
using StanfordController.Core;
using StanfordController.Core.Profiles;
using StanfordController.Core.Protocol;
using StanfordController.Core.Simulation;
using StanfordController.Core.Transport;
using StanfordController.Presentation.Mvvm;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.Tests;

/// <summary>
/// Instruments with older firmware: a command the firmware does not have gets no reply and error 111 (undefined
/// command). Tempo recognises that, greys the setting out and carries on, instead of reporting errors.
/// </summary>
public sealed class FirmwareCompatibilityTests : IDisposable
{
    private static readonly string[] OldFirmware = ["HOLD", "PRES", "PHAS", "SSDL", "TIMB", "EMAC"];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tempo-firmware-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.Delete(_directory);

    private static async Task<(Dg645Client Client, Dg645Simulator Simulator)> ConnectAsync(params string[] unsupported)
    {
        var simulator = new Dg645Simulator();
        foreach (var command in unsupported)
        {
            simulator.UnsupportedCommands.Add(command);
        }

        var client = new Dg645Client(new SimulatorTransport(simulator)) { QueryTimeout = TimeSpan.FromMilliseconds(150) };
        await client.ConnectAsync();
        return (client, simulator);
    }

    private static int Sent(Dg645Simulator simulator, string prefix) =>
        simulator.GetReceivedLines().Count(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task AMissingQueryIsRecognisedOnceAndThenNotSentAgain()
    {
        var (client, simulator) = await ConnectAsync("HOLD");

        var error = await Assert.ThrowsAsync<Dg645UnsupportedCommandException>(() => client.GetHoldoffAsync());
        Assert.Equal("HOLD", error.Mnemonic);
        Assert.Equal(["HOLD"], client.UnsupportedCommands);
        Assert.False(client.Supports("HOLD 0.001"));

        // The second time costs nothing: no wait for a reply that will not come.
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<Dg645UnsupportedCommandException>(() => client.SetHoldoffAsync(1e-3));
        Assert.True(watch.ElapsedMilliseconds < 100, $"took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(1, Sent(simulator, "HOLD"));
    }

    [Fact]
    public async Task TheMissingCommandsErrorIsNotBlamedOnTheNextChange()
    {
        var (client, simulator) = await ConnectAsync("HOLD");
        await Assert.ThrowsAsync<Dg645UnsupportedCommandException>(() => client.GetHoldoffAsync());

        // Before, the 111 left in the error queue made this perfectly good change fail with "Undefined command".
        await client.SetTriggerRateAsync(2000);
        Assert.Equal(2000, await client.GetTriggerRateAsync());
        Assert.Equal("0", simulator.Process("LERR?").Single());
    }

    [Fact]
    public async Task AMissingSetCommandIsRecognisedToo()
    {
        var (client, _) = await ConnectAsync("SSDL");
        await Assert.ThrowsAsync<Dg645UnsupportedCommandException>(() => client.SetDelayStepAsync(Dg645Channel.A, 1e-9));
        Assert.Contains("SSDL", client.UnsupportedCommands);
    }

    [Fact]
    public async Task NoReplyAtAllIsStillALostConnection()
    {
        var (client, simulator) = await ConnectAsync();
        simulator.IsResponding = false;

        var error = await Assert.ThrowsAsync<TimeoutException>(() => client.GetHoldoffAsync());
        Assert.Contains("connection may have been lost", error.Message);
        Assert.Empty(client.UnsupportedCommands);
    }

    [Fact]
    public async Task CommandsKnownToBeMissingAreNotSent()
    {
        var (client, simulator) = await ConnectAsync();
        client.AssumeUnsupported(["PRES?1", "phas"]);

        await Assert.ThrowsAsync<Dg645UnsupportedCommandException>(() => client.GetPrescaleFactorAsync(PrescaleInput.AB));
        await Assert.ThrowsAsync<Dg645UnsupportedCommandException>(() => client.GetPrescalePhaseAsync(PrescaleInput.AB));
        Assert.Equal(0, Sent(simulator, "PRES"));
        Assert.Equal(0, Sent(simulator, "PHAS"));
    }

    [Fact]
    public async Task AWholeSetupIsReadAndWrittenWithoutWhatTheFirmwareLacks()
    {
        var (client, simulator) = await ConnectAsync(OldFirmware);
        simulator.Process("TRAT 1234;DLAY 3,2,2.5e-6;LAMP 1,3.3");

        var settings = await client.ReadSettingsAsync();
        Assert.Equal(1234, settings.Trigger.RateHz);
        Assert.Equal(2.5e-6, settings.Delays.Single(d => d.Channel == Dg645Channel.B).DelaySeconds, 12);
        Assert.Equal(3.3, settings.Outputs.Single(o => o.Output == Dg645Output.AB).AmplitudeVolts, 6);
        Assert.Equal(0, settings.Trigger.HoldoffSeconds);
        Assert.All(settings.Trigger.PrescaleFactors, factor => Assert.Equal(1, factor));

        settings.Trigger.RateHz = 500;
        settings.Trigger.HoldoffSeconds = 1e-3;
        await client.ApplySettingsAsync(settings);
        Assert.Equal(500, await client.GetTriggerRateAsync());
        Assert.Equal(1, Sent(simulator, "HOLD"));
    }

    [Fact]
    public async Task ASessionGreysOutWhatTheFirmwareLacksAndCarriesOn()
    {
        var (client, simulator) = await ConnectAsync(OldFirmware);
        var session = new InstrumentSessionViewModel(new InstrumentProfile { Name = "Old DG645", IsSimulator = true }, client, new FakeDialogs(), new LogViewModel())
        {
            HeartbeatIntervalSeconds = 0.2,
        };
        await session.InitializeAsync();

        Assert.True(session.Trigger.Holdoff.IsUnsupported);
        Assert.False(session.Trigger.Holdoff.HasError);
        Assert.True(session.Trigger.TriggerPrescaler.Factor.IsUnsupported);
        Assert.All(session.Trigger.OutputPrescalers, p => Assert.True(p.Factor.IsUnsupported && p.Phase!.IsUnsupported));
        Assert.All(session.Channels, c => Assert.True(c.StepSize.IsUnsupported && !c.HasError && !c.IsUnsupported));
        Assert.False(session.Trigger.Rate.IsUnsupported);
        Assert.False(session.Status.HasError);
        Assert.Equal("Not reported by this firmware", session.Status.Timebase);
        Assert.Equal("Not reported by this firmware", session.Interface.MacAddress);
        Assert.Contains("does not have EMAC, HOLD, PHAS, PRES, SSDL, TIMB", session.StatusMessage);

        // A setting the firmware has still works, with no stray error 111.
        session.Trigger.Rate.Text = "2 kHz";
        await ((AsyncRelayCommand)session.Trigger.Rate.CommitCommand).ExecuteAsync();
        Assert.False(session.Trigger.Rate.HasError);
        Assert.Equal(2000, double.Parse(simulator.Process("TRAT?").Single(), System.Globalization.CultureInfo.InvariantCulture));

        // Reading everything again does not ask again, and the heartbeat keeps going without false alarms.
        var alerts = new List<InstrumentAlert>();
        session.AlertRaised += (_, alert) => alerts.Add(alert);
        await session.RefreshAllAsync();
        Assert.Equal(1, Sent(simulator, "HOLD"));
        Assert.Equal(1, Sent(simulator, "PRES"));
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Empty(alerts);
        Assert.True(session.IsResponding);

        // Saving the setup works too.
        Assert.NotNull(await session.CaptureSetupAsync());
        session.StopHeartbeat();
    }

    [Fact]
    public async Task WhatAnInstrumentLacksIsRememberedForItsFirmware()
    {
        var main = new MainViewModel(Path.Combine(_directory, "Default.tempo"), new FakeDialogs());
        await main.LoadAsync();
        main.AddDemoInstrumentCommand.Execute(null);
        var item = main.Instruments.Single();

        await item.ConnectAsync();
        var simulator = ((SimulatorTransport)item.Session!.Client.Transport).Simulator;
        simulator.UnsupportedCommands.Add("HOLD");
        simulator.UnsupportedCommands.Add("TIMB");
        item.Session.Client.QueryTimeout = TimeSpan.FromMilliseconds(150);
        await item.Session.RefreshAllAsync();
        await item.DisconnectAsync();

        Assert.Equal(["HOLD", "TIMB"], item.Profile.UnsupportedCommands);
        Assert.Equal(simulator.Identity, item.Profile.UnsupportedCommandsIdentity);

        // Next time the instrument is not asked: the session knows straight away.
        await item.ConnectAsync();
        var again = ((SimulatorTransport)item.Session!.Client.Transport).Simulator;
        Assert.Equal(0, Sent(again, "HOLD"));
        Assert.True(item.Session.Trigger.Holdoff.IsUnsupported);
        await item.DisconnectAsync();

        // New firmware starts afresh.
        item.Profile.UnsupportedCommandsIdentity = "Stanford Research Systems,DG645,s/n000000,ver9.99";
        await item.ConnectAsync();
        Assert.False(item.Session!.Trigger.Holdoff.IsUnsupported);
        await item.DisconnectAsync();
        Assert.Empty(item.Profile.UnsupportedCommands);
    }
}
