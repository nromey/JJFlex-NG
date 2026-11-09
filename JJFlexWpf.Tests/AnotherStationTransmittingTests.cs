using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The lock and push to talk are refused, up front and with a sentence, while
/// the radio clearly reports another station transmitting on it (#911). The
/// 7n bench: with a second client keyed, the lock said "Transmitting, locked"
/// and went Locked though the radio refused its key. The refusal never blocks
/// the operator's own unlock or release, and never keys anything.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> No window: the controller and an unconnected
/// <see cref="FlexBase"/> only, on an MTA thread (asserted first). The
/// controller's "another station?" question is replaced, so no radio is read;
/// the rig's transmit setter enqueues onto a command queue that is not
/// running. <see cref="QuietRun"/> has render off. Chirps are off.
/// </remarks>
public sealed class AnotherStationTransmittingTests : IDisposable
{
    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly VerbosityLevel _verbosityBefore;
    private bool _another;

    public AnotherStationTransmittingTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(
            () => _rig,
            () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = true });
        _controller.AnotherStationTransmitting = _ => _another;
    }

    public void Dispose()
    {
        _another = false;
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        _controller.Dispose();
        _rig.Dispose();
    }

    private static void Guard()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
    }

    private static string Refusal => Lexicon.Get("audio.ptt.another_station_transmitting");

    [Fact]
    public void The_lock_is_refused_and_says_why()
    {
        Guard();
        _another = true;
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(_controller.IsTransmitting);
        Assert.Equal(Refusal, ScreenReaderOutput.RecentMessages.FirstOrDefault());
    }

    [Fact]
    public void Push_to_talk_is_refused_and_says_why()
    {
        Guard();
        _another = true;
        _controller.PttDown();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.Equal(Refusal, ScreenReaderOutput.RecentMessages.FirstOrDefault());
    }

    [Fact]
    public void Control_with_nobody_else_transmitting_both_key()
    {
        Guard();
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        _controller.PttDown();
        Assert.Equal(PttSafetyController.PttState.PttHold, _controller.State);
    }

    [Fact]
    public void The_operators_own_unlock_and_release_are_never_refused()
    {
        Guard();
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        _another = true;                       // someone else appears while we are locked
        _controller.ToggleLock();              // the unlock
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        _another = false;
        _controller.PttDown();
        _another = true;
        _controller.PttUp();                   // the release
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void An_unclear_reading_is_never_a_refusal()
    {
        Guard();
        // The default question, on a rig with no radio: nothing to read, so no refusal.
        var fresh = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        try
        {
            Assert.False(fresh.AnotherStationTransmitting(_rig));
            Assert.False(fresh.AnotherStationTransmitting(null));
            fresh.ToggleLock();
            Assert.Equal(PttSafetyController.PttState.Locked, fresh.State);
            fresh.EscapeUnlock();
        }
        finally { fresh.Dispose(); }
    }
}
