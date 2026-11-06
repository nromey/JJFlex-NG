using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using JJPortaudio;
using Radios;
using Xunit;
using Holder = JJFlexWpf.Dialogs.CheckYourAudioDialog.ReferenceEndHolder;

namespace JJFlexWpf.Tests;

/// <summary>
/// Unified takes, round four (part 6): the window's end of a reference pass,
/// for every holder. From the end until the radio receives, the microphone
/// never reaches the encoder and the transmit input is not moved back; both
/// come back only when the radio's wait ends (Noel, 2026-11-05 01:37, 01:38).
/// The sentence follows the verbosity tier and the holder (01:39 to 01:41).
/// </summary>
/// <remarks>
/// <b>Desk safety, as <see cref="ReferenceEndTests"/>.</b> No window is
/// constructed: the dialog is <see cref="RuntimeHelpers.GetUninitializedObject"/>,
/// so no constructor runs, no XAML loads and no HWND exists, and its
/// <c>Dispatcher</c> is null, so its callbacks run inline. The test thread is
/// asserted MTA, where WPF refuses to build any element. The rig is an
/// unconnected <see cref="FlexBase"/>; its player is driven frame by frame as
/// the audio thread drives it. The radio's own wait needs a connected radio,
/// which this assembly cannot build, so it is stood in by
/// <c>ReferenceSilenceWaitForTests</c>; that wait itself is
/// <c>Radios.Tests.ReferenceEndSilenceTests</c>, on the vendor radio fixture.
/// </remarks>
public sealed class ReferenceEndSilenceTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const int Rate = 48000;
    private const int Floats = 960;     // one 10 ms frame, interleaved stereo
    private const float Mic = 0.9f;

    private readonly FlexBase _rig;
    private readonly CheckYourAudioDialog _dialog;
    private readonly VerbosityLevel _verbosityBefore;
    private Action<ReferenceSilenceEnd>? _waiting;
    private int _waitsAsked;

    public ReferenceEndSilenceTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
        Field("_rig").SetValue(_dialog, _rig);
        Field("_injectionSavedMicSource").SetValue(_dialog, "");
        CheckYourAudioDialog.ReferenceSilenceWaitForTests = (rig, then) => { _waitsAsked++; _waiting = then; };
    }

    public void Dispose()
    {
        CheckYourAudioDialog.ReferenceSilenceWaitForTests = null;
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        _rig.TxFileStop();
        _rig.Dispose();
    }

    private static FieldInfo Field(string name)
    {
        var f = typeof(CheckYourAudioDialog).GetField(name, Any);
        Assert.True(f != null, "production field CheckYourAudioDialog." + name + " is missing");
        return f!;
    }

    private static void AssertTheDeskIsSafe()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    private static float[] MicBuffer()
    {
        var b = new float[Floats];
        Array.Fill(b, Mic);
        return b;
    }

    /// <summary>A reference pass started the way the rig starts one, played off its end.</summary>
    private void ReferencePassAtItsEnd()
    {
        var player = _rig.TxFilePlayer;
        var content = new float[Rate / 5];
        Array.Fill(content, 0.4f);
        player.Load(content, Rate, "reference");
        player.StartHoldingSilenceAtEnd();
        for (int i = 0; i < 400 && !(player.ReachedEnd && !player.Engaged && (player.HoldingEndSilence || player.Idle)); i++)
            player.Process(MicBuffer(), Floats, Rate);
        Assert.True(player.ReachedEnd, "positive control: the pass ran off its end");
        Field("_refPassRunning").SetValue(_dialog, true);
    }

    private bool MicNeverReaches(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            var b = MicBuffer();
            _rig.TxFilePlayer.Process(b, Floats, Rate);
            foreach (float s in b) if (s != 0f) return false;
        }
        return true;
    }

    private bool MicIsBack()
    {
        for (int i = 0; i < 5; i++) _rig.TxFilePlayer.Process(MicBuffer(), Floats, Rate);
        var b = MicBuffer();
        _rig.TxFilePlayer.Process(b, Floats, Rate);
        foreach (float s in b) if (Math.Abs(s - Mic) > 1e-5) return false;
        return true;
    }

    /// <summary>What the radio's wait does when the radio reports receive: the reference's own disarm, then the window.</summary>
    private void RadioReportsReceive()
    {
        Assert.NotNull(_waiting);
        _rig.TxFileStop();
        _waiting!(ReferenceSilenceEnd.Received);
    }

    private sealed class Watch : IDisposable
    {
        public int Disposed;
        public void Dispose() => Disposed++;
    }

    /// <summary>
    /// Named for what it drives (round four repair, 6e-3): the holder is HANDED
    /// to <c>FinishReferencePass</c>, not detected, and the radio's wait is the
    /// stand-in. Detecting the holder is <see cref="ReferenceEndTests"/>; the
    /// wait itself, and every way the microphone is kept off the encoder, is
    /// <c>Radios.Tests.ReferenceEndSilenceTests</c>.
    /// </summary>
    [Theory]
    [InlineData("Check", false)]
    [InlineData("App", false)]
    [InlineData("Elsewhere", true)]    // a hardware push to talk
    [InlineData("Elsewhere", false)]   // transmitting, not ours, not hardware
    [InlineData("Nobody", false)]
    [InlineData("App", true)]          // a hand mic pressed under the app's own key
    public void For_each_holder_named_the_window_keeps_the_microphone_off_until_the_radios_wait_ends(string holderName, bool hardware)
    {
        var holder = Enum.Parse<Holder>(holderName);
        AssertTheDeskIsSafe();
        ReferencePassAtItsEnd();
        var watch = new Watch();
        Field("_refPttWatch").SetValue(_dialog, watch);
        // An arm that moved the transmit input to this computer: it must not go back while transmitting.
        Field("_injectionSavedMicSource").SetValue(_dialog, "MIC");

        _dialog.FinishReferencePass(_rig, holder, hardware);

        Assert.Equal(1, _waitsAsked);
        Assert.True(_rig.TxReferenceEndSilenceHeld, "the end path let the player go");
        Assert.True(MicNeverReaches(100), "the microphone reached the encoder before the radio received");
        Assert.Equal("MIC", (string)Field("_injectionSavedMicSource").GetValue(_dialog)!);
        Assert.Equal(0, watch.Disposed);

        RadioReportsReceive();
        Assert.True(MicIsBack(), "the radio received and the microphone is back");
        Assert.Equal("", (string)Field("_injectionSavedMicSource").GetValue(_dialog)!);
        Assert.Equal(1, watch.Disposed);
        Assert.Null(Field("_refPttWatch").GetValue(_dialog));
    }

    /// <summary>
    /// <c>DisarmReference</c>, on a named radio, as the radio's teardown and a
    /// radio leaving with none arriving call it: it ends the silence at once,
    /// lets the watch go, and a later end of the radio's wait does nothing.
    /// Called directly: <c>SetRig</c> and <c>EndCheckBeforeTeardown</c> need
    /// the built window.
    /// </summary>
    /// <remarks>
    /// <b>Changed in round five (7b).</b> Until then a closing window and a
    /// switch to another radio came here too, and this test's remark said it
    /// pinned that close behaviour rather than a safety guarantee: under a held
    /// hand mic the close gave the desk microphone back while the radio
    /// transmitted (Codex's round four check, B2). Noel ruled 2026-11-06 03:12
    /// that the silence may outlive the window, so those two now hand the
    /// silence to the radio's own wait instead
    /// (<see cref="Closing_the_window_with_the_end_silence_held_keeps_it_until_the_radio_receives"/>).
    /// What is left here is the teardown, where the connection itself is ending.
    /// </remarks>
    [Fact]
    public void A_teardowns_DisarmReference_ends_the_silence_at_once_and_a_later_wait_does_nothing()
    {
        AssertTheDeskIsSafe();
        ReferencePassAtItsEnd();
        var watch = new Watch();
        Field("_refPttWatch").SetValue(_dialog, watch);
        _dialog.FinishReferencePass(_rig, Holder.Elsewhere, hardwareLine: true);
        Assert.True(_rig.TxReferenceEndSilenceHeld);

        typeof(CheckYourAudioDialog).GetMethod("DisarmReference", Any)!.Invoke(_dialog, new object?[] { false, _rig });
        Assert.False(_rig.TxReferenceEndSilenceHeld);
        Assert.True(MicIsBack());
        Assert.Equal(1, watch.Disposed);

        // The radio's wait ends later: nothing more happens, and nothing throws.
        Field("_injectionSavedMicSource").SetValue(_dialog, "MIC");
        _waiting!(ReferenceSilenceEnd.AlreadyReleased);
        Assert.Equal("MIC", (string)Field("_injectionSavedMicSource").GetValue(_dialog)!);
        Assert.Equal(1, watch.Disposed);
    }

    /// <summary>
    /// The radio's wait reports the connection gone, having handed the
    /// microphone back itself (stood in here by <c>TxFileStop</c>): the window
    /// puts the input back and lets go. The disconnect itself is
    /// <c>Radios.Tests.ReferenceEndSilenceTests</c>.
    /// </summary>
    [Fact]
    public void When_the_radios_wait_reports_the_connection_gone_the_window_lets_go()
    {
        AssertTheDeskIsSafe();
        ReferencePassAtItsEnd();
        var watch = new Watch();
        Field("_refPttWatch").SetValue(_dialog, watch);
        _dialog.FinishReferencePass(_rig, Holder.App, hardwareLine: false);
        _rig.TxFileStop();
        _waiting!(ReferenceSilenceEnd.ConnectionGone);
        Assert.True(MicIsBack());
        Assert.Equal(1, watch.Disposed);
    }

    /// <summary>
    /// The WINDOW's guard: an older pass's wait ending does nothing in the
    /// window for a newer pass, and neither does one for a radio the window no
    /// longer has (stood in by clearing <c>_rig</c>). The radio's own guard, on
    /// the player, is <c>Radios.Tests.ReferenceEndSilenceTests.An_older_wait_held_up_across_a_newer_pass_cannot_end_that_pass_silence</c>.
    /// </summary>
    [Fact]
    public void The_window_ignores_an_older_waits_end_and_one_for_a_radio_it_has_let_go()
    {
        AssertTheDeskIsSafe();
        ReferencePassAtItsEnd();
        _dialog.FinishReferencePass(_rig, Holder.App, hardwareLine: false);
        var older = _waiting!;
        ReferencePassAtItsEnd();
        var watch = new Watch();
        Field("_refPttWatch").SetValue(_dialog, watch);
        Field("_injectionSavedMicSource").SetValue(_dialog, "MIC");
        _dialog.FinishReferencePass(_rig, Holder.App, hardwareLine: false);
        older(ReferenceSilenceEnd.Received);
        Assert.Equal(0, watch.Disposed);
        Assert.Equal("MIC", (string)Field("_injectionSavedMicSource").GetValue(_dialog)!);

        Field("_rig").SetValue(_dialog, null);   // the window let this radio go
        _waiting!(ReferenceSilenceEnd.Received);
        Assert.Equal(0, watch.Disposed);
    }

    /// <summary>Ruled 01:39 to 01:41: chatty and terse as Noel gave them; critical as terse and diagnostic as chatty, the house convention.</summary>
    [Fact]
    public void The_sentence_chosen_follows_the_verbosity_tier()
    {
        AssertTheDeskIsSafe();
        string S(bool hardware, VerbosityLevel level) => CheckYourAudioDialog.ReferenceFinishedSentence(Holder.Elsewhere, hardware, level);
        Assert.Equal("Transmitting of the reference recording is complete.", S(false, VerbosityLevel.Chatty));
        Assert.Equal("Transmitting reference complete.", S(false, VerbosityLevel.Terse));
        Assert.Equal("Transmitting reference complete.", S(false, VerbosityLevel.Critical));
        Assert.Equal("Transmitting of the reference recording is complete.", S(false, VerbosityLevel.Diagnostic));
        Assert.Equal("Transmitting of the reference recording is complete. Release your push to talk to stop transmitting dead air.",
            S(true, VerbosityLevel.Chatty));
        Assert.Equal("Transmitting reference complete. Release push to talk.", S(true, VerbosityLevel.Terse));
        Assert.Equal("Transmitting reference complete. Release push to talk.", S(true, VerbosityLevel.Critical));
        Assert.Equal(S(true, VerbosityLevel.Chatty), S(true, VerbosityLevel.Diagnostic));

        // And the pass speaks the operator's tier, not a fixed one.
        ReferencePassAtItsEnd();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
        Assert.Equal("Transmitting reference complete.", _dialog.FinishReferencePass(_rig, Holder.App, hardwareLine: false));
        RadioReportsReceive();
        ReferencePassAtItsEnd();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        Assert.Equal("Transmitting of the reference recording is complete. Release your push to talk to stop transmitting dead air.",
            _dialog.FinishReferencePass(_rig, Holder.Elsewhere, hardwareLine: true));

        // Asking for a key to be let go is said even at critical verbosity; the plain end from terse up, as before.
        Assert.Equal(VerbosityLevel.Critical, CheckYourAudioDialog.ReferenceFinishedLevel(Holder.Elsewhere, true));
        Assert.Equal(VerbosityLevel.Terse, CheckYourAudioDialog.ReferenceFinishedLevel(Holder.App, false));
        Assert.Equal(VerbosityLevel.Terse, CheckYourAudioDialog.ReferenceFinishedLevel(Holder.Nobody, true));
    }

    // ── Round five (7b): the window closing, or switching away, under held silence ──

    private void CloseTheWindowsReference() =>
        typeof(CheckYourAudioDialog).GetMethod("ReleaseReferenceAtClose", Any)!.Invoke(_dialog, null);

    /// <summary>
    /// Ruled by Noel 2026-11-06 03:12 (C3): closing the window while a hand mic
    /// still keys the radio after a reference's end leaves the silence on, and
    /// the radio's own receive wait ends it at release. Driven through
    /// <c>ReleaseReferenceAtClose</c>, the call the <c>Closed</c> handler makes
    /// (that the handler makes it is pinned in source by
    /// <c>Radios.Tests.CheckYourAudioWiringTests</c>); the handler itself is a
    /// lambda in the window's constructor and cannot run without the built
    /// window. Also for the app's key, whose tail may still be running.
    /// </summary>
    [Theory]
    [InlineData("Elsewhere", true)]    // a hand mic: the ruled case
    [InlineData("App", false)]         // the app's key, inside its 300 ms tail
    public void Closing_the_window_with_the_end_silence_held_keeps_it_until_the_radio_receives(string holderName, bool hardware)
    {
        AssertTheDeskIsSafe();
        ReferencePassAtItsEnd();
        var watch = new Watch();
        Field("_refPttWatch").SetValue(_dialog, watch);
        _dialog.FinishReferencePass(_rig, Enum.Parse<Holder>(holderName), hardware);
        var windowsWait = _waiting!;
        Field("_injectionSavedMicSource").SetValue(_dialog, "MIC");

        CloseTheWindowsReference();

        Assert.True(_rig.TxReferenceEndSilenceHeld, "the close ended the silence with the radio still transmitting");
        Assert.True(MicNeverReaches(100), "the desk microphone reached the encoder after the close");
        Assert.Equal(2, _waitsAsked);                       // handed to the radio's own wait
        Assert.Equal(1, watch.Disposed);                    // the watch ends at the close
        Assert.Equal("", (string)Field("_injectionSavedMicSource").GetValue(_dialog)!);   // the input goes to the restore owner, which writes at receive

        // The window's own wait ending now does nothing in the closed window.
        windowsWait(ReferenceSilenceEnd.AlreadyReleased);
        Assert.True(_rig.TxReferenceEndSilenceHeld);

        // The hand mic is let go: the radio receives, and only then is the microphone back.
        RadioReportsReceive();
        Assert.True(MicIsBack(), "the radio received and the microphone is back");
    }

    /// <summary>A close inside the last moments of a pass, before the window's poll ever saw its end, still leaves the radio a wait to end it: nothing is stranded.</summary>
    [Fact]
    public void A_close_before_the_window_saw_the_end_still_hands_the_silence_to_the_radio()
    {
        AssertTheDeskIsSafe();
        ReferencePassAtItsEnd();
        Assert.Equal(0, _waitsAsked);
        CloseTheWindowsReference();
        Assert.Equal(1, _waitsAsked);
        Assert.True(_rig.TxReferenceEndSilenceHeld);
        Assert.True(MicNeverReaches(50));
        RadioReportsReceive();
        Assert.True(MicIsBack());
    }

    /// <summary>The control: a reference still playing when the window closes is disarmed at once, exactly as before.</summary>
    [Fact]
    public void Closing_the_window_with_the_reference_still_playing_disarms_it_as_before()
    {
        AssertTheDeskIsSafe();
        var player = _rig.TxFilePlayer;
        var content = new float[Rate];
        Array.Fill(content, 0.4f);
        player.Load(content, Rate, "reference");
        player.StartHoldingSilenceAtEnd();
        for (int i = 0; i < 10; i++) player.Process(MicBuffer(), Floats, Rate);
        Assert.True(player.Engaged, "positive control: the reference is still playing");
        Field("_refPassRunning").SetValue(_dialog, true);

        CloseTheWindowsReference();

        Assert.Equal(0, _waitsAsked);
        Assert.False(player.Engaged);
        Assert.False(_rig.TxReferenceEndSilenceHeld);
        Assert.True(MicIsBack(), "a reference still playing is disarmed at the close, as before");
    }

    /// <summary>A close, and then the disconnect: the disconnect's VOX release ends the silence the window handed over.</summary>
    [Fact]
    public void A_close_then_a_disconnect_ends_the_silence()
    {
        AssertTheDeskIsSafe();
        ReferencePassAtItsEnd();
        _dialog.FinishReferencePass(_rig, Holder.Elsewhere, hardwareLine: true);
        CloseTheWindowsReference();
        Assert.True(_rig.TxReferenceEndSilenceHeld);
        typeof(FlexBase).GetMethod("DisconnectVoxPauses", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_rig, null);
        Assert.False(_rig.TxReferenceEndSilenceHeld);
        Assert.True(MicIsBack());
    }
}
