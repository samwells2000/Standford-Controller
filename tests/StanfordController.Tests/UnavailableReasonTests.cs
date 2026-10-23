using StanfordController.Core.Profiles;
using StanfordController.Presentation.Mvvm;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.Tests;

/// <summary>Greyed-out commands say why they are unavailable, for the tooltip shown when hovering over them.</summary>
public sealed class UnavailableReasonTests
{
    private static string? ReasonOf(System.Windows.Input.ICommand command) => ((IExplainUnavailable)command).UnavailableReason;

    [Fact]
    public void ACommandGivesItsReasonOnlyWhileItCannotRun()
    {
        var allowed = false;
        var command = new RelayCommand(() => { }, () => allowed, () => "Not yet.");
        Assert.Equal("Not yet.", ReasonOf(command));

        allowed = true;
        Assert.Null(ReasonOf(command));
    }

    [Fact]
    public async Task ARunningCommandSaysItIsAlreadyInProgress()
    {
        var finish = new TaskCompletionSource();
        var command = new AsyncRelayCommand(() => finish.Task);
        Assert.Null(ReasonOf(command));

        var running = command.ExecuteAsync();
        Assert.Contains("in progress", ReasonOf(command));

        finish.SetResult();
        await running;
        Assert.Null(ReasonOf(command));
    }

    [Fact]
    public void AnInstrumentThatIsNotConnectedSaysSo()
    {
        var item = new InstrumentItemViewModel(new InstrumentProfile { Name = "Test", IsSimulator = true }, new FakeDialogs(), _ => { });
        Assert.Equal(InstrumentItemViewModel.NotConnectedReason, ReasonOf(item.DisconnectCommand));
        Assert.Equal(InstrumentItemViewModel.NotConnectedReason, ReasonOf(item.SaveSetupCommand));
        Assert.Contains("No setup has been saved", ReasonOf(item.RestoreSetupCommand));
        Assert.Null(ReasonOf(item.ConnectCommand));
    }

    [Fact]
    public async Task AConnectedInstrumentExplainsWhyTriggerIsGreyedOut()
    {
        var item = new InstrumentItemViewModel(new InstrumentProfile { Name = "Test", IsSimulator = true }, new FakeDialogs(), _ => { });
        await item.ConnectAsync();
        var session = item.Session!;
        try
        {
            Assert.Contains("Already connected", ReasonOf(item.ConnectCommand));
            Assert.Null(ReasonOf(item.SaveSetupCommand));
            Assert.False(session.Trigger.IsSingleShot);
            Assert.Contains("single-shot", ReasonOf(session.TriggerCommand));
        }
        finally
        {
            await item.DisconnectAsync();
        }
    }
}
