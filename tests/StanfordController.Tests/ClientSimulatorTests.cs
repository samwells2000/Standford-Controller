using StanfordController.Core;
using StanfordController.Core.Protocol;
using StanfordController.Core.Simulation;
using StanfordController.Core.Transport;

namespace StanfordController.Tests;

public class ClientSimulatorTests
{
    private static async Task<(Dg645Client Client, Dg645Simulator Simulator)> ConnectAsync()
    {
        var simulator = new Dg645Simulator();
        var client = new Dg645Client(new SimulatorTransport(simulator)) { QueryTimeout = TimeSpan.FromMilliseconds(300) };
        await client.ConnectAsync();
        return (client, simulator);
    }

    [Fact]
    public async Task ConnectReadsIdentity()
    {
        var (client, _) = await ConnectAsync();
        Assert.StartsWith("Stanford Research Systems,DG645", client.Identity);
    }

    [Fact]
    public async Task DelayRoundTripsThroughTheWireFormat()
    {
        var (client, simulator) = await ConnectAsync();
        await client.SetDelayAsync(Dg645Channel.B, Dg645Channel.A, 1.25e-6);

        Assert.Contains("DLAY 3,2,0.000001250000", simulator.ReceivedLines);
        var (reference, seconds) = await client.GetDelayAsync(Dg645Channel.B);
        Assert.Equal(Dg645Channel.A, reference);
        Assert.Equal(1.25e-6, seconds, 1e-15);
    }

    [Fact]
    public async Task RejectedCommandThrowsWithTheInstrumentErrorCode()
    {
        var (client, _) = await ConnectAsync();
        await client.SetDelayAsync(Dg645Channel.B, Dg645Channel.A, 1e-6);

        // A → B → A would be a loop.
        var error = await Assert.ThrowsAsync<Dg645Exception>(() => client.SetDelayAsync(Dg645Channel.A, Dg645Channel.B, 1e-6));
        Assert.Equal(13, error.ErrorCode);
        Assert.Equal("Illegal link", error.ErrorDescription);

        // The error queue was drained, so the next command succeeds cleanly.
        await client.SetTriggerRateAsync(500);
        Assert.Equal(500, await client.GetTriggerRateAsync());
    }

    [Fact]
    public async Task OutOfRangeValueReportsIllegalValue()
    {
        var (client, _) = await ConnectAsync();
        var error = await Assert.ThrowsAsync<Dg645Exception>(() => client.SetAmplitudeAsync(Dg645Output.AB, 9));
        Assert.Equal(10, error.ErrorCode);
    }

    [Fact]
    public async Task UnsupportedQueryIsRecognisedAndTheLinkStaysUsable()
    {
        var (client, _) = await ConnectAsync();
        await Assert.ThrowsAsync<Dg645UnsupportedCommandException>(() => client.QueryAsync("FOOO?"));
        Assert.Equal(0, await client.GetLastErrorAsync()); // the 111 was read, so it is not blamed on a later command
        Assert.Equal(TriggerSource.Internal, await client.GetTriggerSourceAsync());
    }

    [Fact]
    public async Task StepCommandsUseTheStepSize()
    {
        var (client, _) = await ConnectAsync();
        await client.SetDelayStepAsync(Dg645Channel.C, 10e-9);
        await client.StepDelayAsync(Dg645Channel.C, up: true);
        await client.StepDelayAsync(Dg645Channel.C, up: true);
        Assert.Equal(20e-9, (await client.GetDelayAsync(Dg645Channel.C)).Seconds, 1e-15);

        await client.SetAmplitudeStepAsync(Dg645Output.CD, 0.5);
        await client.StepAmplitudeAsync(Dg645Output.CD, up: false);
        Assert.Equal(2.0, await client.GetAmplitudeAsync(Dg645Output.CD), 1e-9);
    }

    [Fact]
    public async Task SettingsSnapshotAppliesToAnotherInstrument()
    {
        var (source, _) = await ConnectAsync();
        await source.SetTriggerSourceAsync(TriggerSource.ExternalFalling);
        await source.SetTriggerLevelAsync(-1.25);
        await source.SetPrescaleFactorAsync(PrescaleInput.AB, 4);
        await source.SetPrescalePhaseAsync(PrescaleInput.AB, 3);
        await source.SetBurstCountAsync(25);
        await source.SetBurstModeAsync(true);
        await source.SetDelayAsync(Dg645Channel.A, Dg645Channel.T0, 2e-6);
        await source.SetDelayAsync(Dg645Channel.B, Dg645Channel.A, 100e-9);
        await source.SetPolarityAsync(Dg645Output.GH, OutputPolarity.Negative);

        var snapshot = Dg645Settings.FromJson((await source.ReadSettingsAsync()).ToJson());

        // The target starts with the opposite link (A relative to B), which a naive apply would turn into a loop.
        var (target, _) = await ConnectAsync();
        await target.SetDelayAsync(Dg645Channel.A, Dg645Channel.B, 5e-9);
        await target.ApplySettingsAsync(snapshot);

        var copy = await target.ReadSettingsAsync();
        Assert.Equal(TriggerSource.ExternalFalling, copy.Trigger.Source);
        Assert.Equal(-1.25, copy.Trigger.LevelVolts, 1e-9);
        Assert.Equal(4, copy.Trigger.PrescaleFactors[(int)PrescaleInput.AB]);
        Assert.Equal(3, copy.Trigger.PrescalePhases[0]);
        Assert.True(copy.Burst.Enabled);
        Assert.Equal(25, copy.Burst.Count);
        Assert.Equal(Dg645Channel.A, copy.Delays.Single(d => d.Channel == Dg645Channel.B).Reference);
        Assert.Equal(2e-6, copy.Delays.Single(d => d.Channel == Dg645Channel.A).DelaySeconds, 1e-15);
        Assert.Equal(OutputPolarity.Negative, copy.Outputs.Single(o => o.Output == Dg645Output.GH).Polarity);
    }

    [Fact]
    public void DelaysAreOrderedSoReferencesComeFirst()
    {
        var delays = new[]
        {
            new ChannelDelaySettings { Channel = Dg645Channel.B, Reference = Dg645Channel.C },
            new ChannelDelaySettings { Channel = Dg645Channel.C, Reference = Dg645Channel.A },
            new ChannelDelaySettings { Channel = Dg645Channel.A, Reference = Dg645Channel.T0 },
        };

        var order = Dg645Client.OrderByReference(delays).Select(d => d.Channel).ToList();
        Assert.Equal([Dg645Channel.A, Dg645Channel.C, Dg645Channel.B], order);
    }

    [Fact]
    public async Task SaveAndRecallUseInstrumentMemory()
    {
        var (client, _) = await ConnectAsync();
        await client.SetTriggerRateAsync(1234);
        await client.SaveSettingsAsync(3);
        await client.SetTriggerRateAsync(99);
        await client.RecallSettingsAsync(3);
        Assert.Equal(1234, await client.GetTriggerRateAsync(), 1e-6);

        var error = await Assert.ThrowsAsync<Dg645Exception>(() => client.RecallSettingsAsync(7));
        Assert.Equal(14, error.ErrorCode);
    }

    [Fact]
    public async Task RawTerminalCommandCollectsEveryReply()
    {
        var (client, _) = await ConnectAsync();
        var result = await client.ExecuteRawAsync("TSRC?;BURC?", checkErrors: true);
        Assert.Equal(["0", "10"], result.Responses);
        Assert.Empty(result.Errors);

        var bad = await client.ExecuteRawAsync("TSRC 42", checkErrors: true);
        Assert.Equal([10], bad.Errors);
    }

    [Fact]
    public async Task StatusRegistersReportActivity()
    {
        var (client, _) = await ConnectAsync();
        await client.SetTriggerSourceAsync(TriggerSource.SingleShot);
        await client.GetInstrumentStatusAsync(); // clear
        await client.TriggerAsync();
        var status = await client.GetInstrumentStatusAsync();
        Assert.True(status.HasFlag(InstrumentStatusFlags.Trigger));
        Assert.True(status.HasFlag(InstrumentStatusFlags.EndOfDelay));
        Assert.Equal(InstrumentStatusFlags.None, await client.GetInstrumentStatusAsync());
    }

    [Fact]
    public async Task InterfaceSettingsAreValidated()
    {
        var (client, _) = await ConnectAsync();
        await client.SetInterfaceSettingAsync(InterfaceSetting.StaticIpAddress, "192.168.10.20");
        Assert.Equal("192.168.10.20", await client.GetInterfaceSettingAsync(InterfaceSetting.StaticIpAddress));
        await Assert.ThrowsAsync<Dg645Exception>(() => client.SetInterfaceSettingAsync(InterfaceSetting.Rs232BaudRate, "1234"));
    }

    [Fact]
    public async Task DisplayRoundTrips()
    {
        var (client, _) = await ConnectAsync();
        await client.SetDisplayAsync(DisplayMode.ChannelOutputLevels, (int)Dg645Output.EF);
        Assert.Equal((DisplayMode.ChannelOutputLevels, 3), await client.GetDisplayAsync());
    }
}
