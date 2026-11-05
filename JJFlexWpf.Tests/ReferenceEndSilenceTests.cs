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

    [Theory]
    [InlineData("Check", false)]
    [InlineData("App", false)]
    [InlineData("Elsewhere", true)]    // a hardware push to talk
    [InlineData("Elsewhere", false)]   // transmitting, not ours, not hardware
    [InlineData("Nobody", false)]
    [InlineData("App", true)]          // a hand mic pressed under the app's own key
    public void For_every_holder_the_microphone_never_reaches_the_transmitter_between_the_end_and_receive(string holderName, bool hardware)
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
    /// The window's own ways out reach <c>DisarmReference</c>: its close, the
    /// radio's teardown and a radio switch. Each ends the silence at once, lets
    /// the watch go, and a later end of the radio's wait does nothing.
    /// </summary>
    [Fact]
    public void A_closing_window_or_a_departing_radio_ends_the_silence_at_once()
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

    /// <summary>The connection went: the radio's wait has already handed the microphone back; the window puts the input back and lets go.</summary>
    [Fact]
    public void A_lost_connection_ends_the_silence_and_the_window_lets_go()
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

    /// <summary>An older pass's wait ending cannot act for a newer pass, or for one this window has let go of.</summary>
    [Fact]
    public void An_older_wait_or_another_radio_cannot_act_for_this_pass()
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
}
