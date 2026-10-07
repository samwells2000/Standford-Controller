using StanfordController.Core;
using StanfordController.Core.Profiles;
using StanfordController.Core.Protocol;
using StanfordController.Core.Simulation;
using StanfordController.Core.Transport;
using StanfordController.Presentation.Mvvm;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.Tests;

/// <summary>Single-shot external triggering: the trigger button arms the instrument and shows Ready until the edge arrives.</summary>
public sealed class TriggerArmTests
{
    private static async Task<(InstrumentSessionViewModel Session, Dg645Simulator Simulator)> CreateSessionAsync(TriggerSource source)
    {
        var simulator = new Dg645Simulator();
        simulator.Process($"TSRC {(int)source}");
        var client = new Dg645Client(new SimulatorTransport(simulator)) { QueryTimeout = TimeSpan.FromMilliseconds(300) };
        await client.ConnectAsync();
        var session = new InstrumentSessionViewModel(new InstrumentProfile { Name = "Test", IsSimulator = true }, client, new FakeDialogs(), new LogViewModel())
        {
            PollIntervalSeconds = 0.2,
        };
        await session.RefreshAllAsync();
        return (session, simulator);
    }

    private static Task TriggerAsync(InstrumentSessionViewModel session) => ((AsyncRelayCommand)session.TriggerCommand).ExecuteAsync();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public void SimulatorArmsOnTrgAndFiresOnceOnTheNextExternalEdge()
    {
        var simulator = new Dg645Simulator();
        simulator.Process("TSRC 3");
        simulator.ExternalTrigger();
        Assert.Equal("0", simulator.Process("INSR?").Single()); // not armed: the edge is ignored

        simulator.Process("*TRG");
        Assert.True(simulator.IsArmed);
        Assert.Equal("0", simulator.Process("INSR?").Single()); // arming is not a trigger

        simulator.ExternalTrigger();
        Assert.False(simulator.IsArmed);
        var status = (InstrumentStatusFlags)int.Parse(simulator.Process("INSR?").Single());
        Assert.True(status.HasFlag(InstrumentStatusFlags.EndOfDelay));

        simulator.ExternalTrigger();
        Assert.Equal("0", simulator.Process("INSR?").Single()); // one shot per arming

        simulator.Process("*TRG;TSRC 3");
        Assert.False(simulator.IsArmed); // choosing a source cancels arming
    }

    [Fact]
    public async Task ExternalSingleShotShowsArmThenReadyUntilTheEdgeArrives()
    {
        var (session, simulator) = await CreateSessionAsync(TriggerSource.SingleShotExternalRising);
        Assert.False(session.IsPolling);
        Assert.True(session.TriggerCommand.CanExecute(null));
        Assert.Equal("Arm", session.TriggerActionText);
        Assert.Equal("Arm", session.TriggerNowText);
        Assert.Contains("rising edge", session.TriggerActionHint);

        await TriggerAsync(session);
        Assert.True(session.IsArmed);
        Assert.True(simulator.IsArmed);
        Assert.Equal("Ready", session.TriggerActionText);
        Assert.Equal("Ready", session.TriggerNowText);
        Assert.Equal("_Arm again", session.TriggerMenuText);
        Assert.StartsWith("Armed at", session.StatusMessage);

        // With nothing at the trigger input it stays Ready, though Live status is off.
        await Task.Delay(600);
        Assert.True(session.IsArmed);
        Assert.False(session.IsPolling);

        simulator.ExternalTrigger();
        await WaitForAsync(() => !session.IsArmed);
        Assert.Equal("Arm", session.TriggerActionText);
        Assert.StartsWith("Triggered at", session.StatusMessage);
        Assert.True(session.Status.InstrumentBits.Single(b => b.Name == "END_OF_DELAY").IsSet);
    }

    [Fact]
    public async Task EventsFromBeforeArmingDoNotEndReady()
    {
        var (session, simulator) = await CreateSessionAsync(TriggerSource.SingleShotExternalFalling);
        simulator.Process("TSRC 5;*TRG;TSRC 4"); // a delay cycle the session has not seen yet

        await TriggerAsync(session);
        await Task.Delay(500);
        Assert.True(session.IsArmed);
        Assert.Contains("falling edge", session.TriggerActionHint);
    }

    [Fact]
    public async Task ChoosingAnotherSourceCancelsReady()
    {
        var (session, _) = await CreateSessionAsync(TriggerSource.SingleShotExternalRising);
        await TriggerAsync(session);
        Assert.True(session.IsArmed);

        session.Trigger.Source.Selected = session.Trigger.Source.Options.Single(o => o.Value == (int)TriggerSource.Internal);
        await WaitForAsync(() => !session.IsArmed && !session.Trigger.Source.IsBusy);
        Assert.Equal("Trigger", session.TriggerActionText);
        Assert.False(session.TriggerCommand.CanExecute(null));
    }

    [Fact]
    public async Task SingleShotStillTriggersStraightAway()
    {
        var (session, simulator) = await CreateSessionAsync(TriggerSource.SingleShot);
        Assert.Equal("Trigger", session.TriggerActionText);
        Assert.Equal("Trigger now", session.TriggerNowText);
        Assert.Equal("Single-shot _trigger", session.TriggerMenuText);

        await TriggerAsync(session);
        Assert.False(session.IsArmed);
        Assert.StartsWith("Triggered at", session.StatusMessage);
        Assert.Contains("*TRG", simulator.GetReceivedLines());
    }

    [Fact]
    public async Task DisconnectingClearsReady()
    {
        var (session, _) = await CreateSessionAsync(TriggerSource.SingleShotExternalRising);
        await TriggerAsync(session);
        session.StopPolling();
        Assert.False(session.IsArmed);
        Assert.Equal("Arm", session.TriggerActionText);
    }
}
