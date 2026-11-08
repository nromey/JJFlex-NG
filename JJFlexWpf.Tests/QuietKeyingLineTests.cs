using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Push to talk in a dialog, the first time on a new split, says only the
/// longer transmit-slice notice (ruled by Noel 2026-11-06, J12). The door
/// asks the controller to leave out its key-down line for that one key; the
/// key, its state and its tones are unchanged, and a notice that could not
/// be said gets the line back.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> No window: the controller and an unconnected
/// <see cref="FlexBase"/> only, on an MTA thread (asserted first), where WPF
/// refuses to build any element. The rig's transmit setter enqueues onto a
/// command queue that is not running, so nothing reaches a radio.
/// <see cref="QuietRun"/> has render off, so the spoken history is read, not
/// heard. Chirps are off.
/// </remarks>
public sealed class QuietKeyingLineTests : IDisposable
{
    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly VerbosityLevel _verbosityBefore;

    public QuietKeyingLineTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(
            () => _rig,
            () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = true });
    }

    public void Dispose()
    {
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        _controller.Dispose();
        _rig.Dispose();
    }

    private static string Mark()
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
        return marker;
    }

    private void Guard()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
    }

    [Fact]
    public void A_quiet_key_keys_and_says_no_key_down_line()
    {
        Guard();
        string marker = Mark();
        _controller.QuietNextKeyingLine = true;
        _controller.PttDown();

        Assert.Equal(PttSafetyController.PttState.PttHold, _controller.State);   // keyed as ever
        Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault()); // nothing said
        Assert.False(_controller.QuietNextKeyingLine);                           // consumed
    }

    [Fact]
    public void Control_an_ordinary_key_says_its_key_down_line()
    {
        Guard();
        string marker = Mark();
        _controller.PttDown();

        Assert.Equal(PttSafetyController.PttState.PttHold, _controller.State);
        Assert.NotEqual(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
    }

    [Fact]
    public void The_quiet_is_for_one_key_only()
    {
        Guard();
        _controller.QuietNextKeyingLine = true;
        _controller.PttDown();
        _controller.PttUp();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        string marker = Mark();
        _controller.PttDown();
        Assert.NotEqual(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());
    }

    [Fact]
    public void A_notice_that_was_not_said_gets_the_line_back_while_still_keyed()
    {
        Guard();
        _controller.QuietNextKeyingLine = true;
        _controller.PttDown();
        string marker = Mark();
        _controller.SpeakKeyingLineLate();
        Assert.NotEqual(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());

        // Not keyed any more: nothing to say.
        _controller.PttUp();
        string after = Mark();
        _controller.SpeakKeyingLineLate();
        Assert.Equal(after, ScreenReaderOutput.RecentMessages.FirstOrDefault());
    }
}
