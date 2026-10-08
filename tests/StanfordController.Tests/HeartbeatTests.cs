using StanfordController.Core;
using StanfordController.Core.Audit;
using StanfordController.Core.Profiles;
using StanfordController.Core.Protocol;
using StanfordController.Core.Simulation;
using StanfordController.Core.Transport;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.Tests;

/// <summary>The connection heartbeat: noticing a silent or dropped instrument and faults it reports, and alerting.</summary>
public sealed class HeartbeatTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tempo-heartbeat-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.Delete(_directory);

    private sealed record Rig(
        InstrumentSessionViewModel Session, Dg645Simulator Simulator, SimulatorTransport Transport, AuditLog Audit,
        List<InstrumentAlert> Alerts, List<string> Resolved, List<string> Disconnects);

    private async Task<Rig> ConnectAsync(Action<Dg645Simulator>? prepare = null)
    {
        var simulator = new Dg645Simulator();
        prepare?.Invoke(simulator);
        var transport = new SimulatorTransport(simulator);
        var client = new Dg645Client(transport) { QueryTimeout = TimeSpan.FromMilliseconds(150) };
        await client.ConnectAsync();
        var audit = new AuditLog(Path.Combine(_directory, "Audit trail"), "Tempo 1.0.0");
        var profile = new InstrumentProfile { Name = "Bench DG645", IsSimulator = true };
        var session = new InstrumentSessionViewModel(profile, client, new FakeDialogs(), new LogViewModel(), audit)
        {
            HeartbeatIntervalSeconds = 0.2,
        };

        var rig = new Rig(session, simulator, transport, audit, [], [], []);
        session.AlertRaised += (_, alert) => { lock (rig.Alerts) { rig.Alerts.Add(alert); } };
        session.AlertResolved += (_, kind) => { lock (rig.Resolved) { rig.Resolved.Add(kind); } };
        session.DisconnectRequested += (_, reason) => { lock (rig.Disconnects) { rig.Disconnects.Add(reason); } };
        await session.InitializeAsync();
        return rig;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        for (var waited = 0; !condition() && waited < timeoutMilliseconds; waited += 20)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private static bool Has(Rig rig, string kind)
    {
        lock (rig.Alerts)
        {
            return rig.Alerts.Any(a => a.Kind == kind);
        }
    }

    [Fact]
    public async Task TheHeartbeatShowsTheRoundTripWhileTheInstrumentAnswers()
    {
        var rig = await ConnectAsync();
        Assert.True(rig.Session.IsHeartbeatRunning);
        await WaitForAsync(() => rig.Session.HeartbeatText.EndsWith(" ms", StringComparison.Ordinal));
        Assert.True(rig.Session.IsResponding);
        Assert.StartsWith("Heartbeat: the instrument answered at", rig.Session.HeartbeatTip);
        Assert.Empty(rig.Alerts);

        rig.Session.StopHeartbeat();
        Assert.False(rig.Session.IsHeartbeatRunning);
    }

    [Fact]
    public async Task ASilentInstrumentIsNotRespondingThenLost()
    {
        var rig = await ConnectAsync();
        await WaitForAsync(() => rig.Session.HeartbeatText.Length > 0);

        rig.Simulator.IsResponding = false;
        await WaitForAsync(() => Has(rig, InstrumentAlert.NotResponding));
        var alert = rig.Alerts.First(a => a.Kind == InstrumentAlert.NotResponding);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);

        await WaitForAsync(() => rig.Disconnects.Count > 0);
        Assert.Contains($"no reply to {InstrumentSessionViewModel.MissedBeatsBeforeLost} heartbeats in a row", rig.Disconnects[0]);
        Assert.False(rig.Session.IsHeartbeatRunning);

        await rig.Audit.FlushAsync();
        var records = rig.Audit.Store.Months().SelectMany(rig.Audit.Store.ReadMonth).ToList();
        Assert.Contains(records, r => r.Action == AuditActions.NotResponding);
    }

    [Fact]
    public async Task AnInstrumentThatAnswersAgainClearsTheWarning()
    {
        var rig = await ConnectAsync();
        rig.Simulator.IsResponding = false;
        await WaitForAsync(() => Has(rig, InstrumentAlert.NotResponding));
        Assert.False(rig.Session.IsResponding);
        Assert.StartsWith("No reply for", rig.Session.HeartbeatText);

        rig.Simulator.IsResponding = true;
        await WaitForAsync(() => rig.Resolved.Contains(InstrumentAlert.NotResponding));
        Assert.True(rig.Session.IsResponding);
        Assert.Empty(rig.Disconnects);

        await rig.Audit.FlushAsync();
        var records = rig.Audit.Store.Months().SelectMany(rig.Audit.Store.ReadMonth).ToList();
        Assert.Contains(records, r => r.Action == AuditActions.RespondingAgain);
    }

    [Fact]
    public async Task ADroppedConnectionIsLostStraightAway()
    {
        var rig = await ConnectAsync();
        rig.Transport.Drop();
        await WaitForAsync(() => rig.Disconnects.Count > 0, timeoutMilliseconds: 1500);
        Assert.Contains("connection to the instrument was closed", rig.Disconnects[0]);
    }

    [Fact]
    public async Task FaultBitsRaiseAlerts()
    {
        var rig = await ConnectAsync();
        rig.Simulator.RaiseStatus(InstrumentStatusFlags.PllUnlock | InstrumentStatusFlags.RbUnlock);
        await WaitForAsync(() => Has(rig, InstrumentAlert.PllUnlocked) && Has(rig, InstrumentAlert.RubidiumUnlocked));
        Assert.All(rig.Alerts, a => Assert.Equal(AlertSeverity.Error, a.Severity));
        Assert.True(rig.Session.Status.InstrumentBits.Single(b => b.Name == "PLL_UNLOCK").IsSet);

        await rig.Audit.FlushAsync();
        var records = rig.Audit.Store.Months().SelectMany(rig.Audit.Store.ReadMonth).ToList();
        Assert.Equal(2, records.Count(r => r.Action == AuditActions.Fault));
    }

    [Fact]
    public async Task ALostExternalReferenceIsReported()
    {
        var rig = await ConnectAsync(simulator => simulator.Timebase = Timebase.External);
        Assert.Equal("External", rig.Session.Status.Timebase.Split(' ')[0]);

        rig.Simulator.Timebase = Timebase.Internal;
        await WaitForAsync(() => Has(rig, InstrumentAlert.TimebaseChanged), timeoutMilliseconds: 8000);
        var alert = rig.Alerts.Single(a => a.Kind == InstrumentAlert.TimebaseChanged);
        Assert.Contains("from External", alert.Message);
    }

    [Fact]
    public async Task TheAlertBarShowsEachProblemOnceAndClearsItWhenTheInstrumentIsBack()
    {
        var dialogs = new FakeDialogs();
        var main = new MainViewModel(Path.Combine(_directory, "Default.tempo"), dialogs);
        await main.LoadAsync();
        main.AddDemoInstrumentCommand.Execute(null);
        var item = main.Instruments.Single();
        var raised = new List<AlertViewModel>();
        main.AlertRaised += (_, alert) => raised.Add(alert);

        await item.ConnectAsync();
        var session = item.Session!;
        session.HeartbeatIntervalSeconds = 0.2;
        var simulator = ((SimulatorTransport)session.Client.Transport).Simulator;

        // A fault that happens twice is one alert, counted twice.
        simulator.RaiseStatus(InstrumentStatusFlags.PllUnlock);
        await WaitForAsync(() => main.Alerts.Count == 1);
        simulator.RaiseStatus(InstrumentStatusFlags.PllUnlock);
        await WaitForAsync(() => main.Alerts.Single().Count == 2);
        Assert.Single(raised);
        Assert.Equal("Demo DG645 (simulated): Timebase PLL unlocked", main.Alerts[0].Title);
        Assert.False(main.Alerts[0].CanReconnect);

        // The connection drops: a second alert, which offers to reconnect.
        ((SimulatorTransport)session.Client.Transport).Drop();
        await WaitForAsync(() => main.Alerts.Any(a => a.Kind == InstrumentAlert.ConnectionLost));
        var lost = main.Alerts.First(a => a.Kind == InstrumentAlert.ConnectionLost);
        Assert.True(lost.IsError);
        Assert.True(lost.CanReconnect);
        Assert.True(item.IsFailed);
        Assert.Equal(lost, raised.Last());

        // Reconnecting clears the connection alert; the fault stays until dismissed.
        lost.ReconnectCommand.Execute(null);
        await WaitForAsync(() => item.IsConnected && main.Alerts.All(a => a.Kind != InstrumentAlert.ConnectionLost));
        Assert.Same(item, main.SelectedInstrument);
        main.Alerts.Single().DismissCommand.Execute(null);
        Assert.False(main.HasAlerts);

        await item.DisconnectAsync();
    }
}
