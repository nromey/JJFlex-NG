using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using JJPortaudio;
using Radios;
using Xunit;
using Holder = JJFlexWpf.Dialogs.CheckYourAudioDialog.ReferenceEndHolder;

namespace JJFlexWpf.Tests;

/// <summary>
/// Unified takes, round five (7c): after a reference ends under the app's own
/// key, the controller does not say "Receiving"; the finished sentence is the
/// only thing said (Noel, 2026-11-06 03:13). Until then "Receiving", at
/// Interrupt, cut the finished sentence at its first word and the speech
/// arbiter replayed it whole 600 ms later, so the operator heard
/// "Transmitting... Receiving. Transmitting reference complete."
/// </summary>
/// <remarks>
/// <para>
/// <b>What is counted.</b> Every utterance the speech layer emits is recorded
/// by <see cref="OutputChannelRecorder"/> to a transcript of this test alone,
/// with render off; a sentence counts when it passed the verbosity gate and was
/// not suppressed. Each test waits out the arbiter's salvage window before it
/// counts, so a replay would be seen as a second copy.
/// </para>
/// <para>
/// <b>Desk safety,</b> as <see cref="ReferenceEndTests"/>: no window is
/// constructed (the dialog is <see cref="RuntimeHelpers.GetUninitializedObject"/>),
/// the thread is asserted MTA, render is off throughout (it is only the
/// recording that is switched on and off), chirps are off, and the rig is an
/// unconnected <see cref="FlexBase"/>. The controller's own speech is ON here,
/// because "Receiving" is what is being counted; with render off it reaches no
/// screen reader.
/// </para>
/// </remarks>
public sealed class ReferenceEndSpeechTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const int Rate = 48000;
    private const int Floats = 960;

    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly CheckYourAudioDialog _dialog;
    private readonly Func<PttSafetyController?>? _sourceBefore;
    private readonly Func<bool>? _endBefore;
    private readonly VerbosityLevel _verbosityBefore;
    private string? _transcript;

    public ReferenceEndSpeechTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = true });
        _dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
        Field("_rig").SetValue(_dialog, _rig);
        Field("_injectionSavedMicSource").SetValue(_dialog, "");
        _sourceBefore = AudioWorkshopDialog.PttControllerSource;
        _endBefore = CheckYourAudioDialog.EndAppKeyForReference;
        AudioWorkshopDialog.PttControllerSource = () => _controller;
        CheckYourAudioDialog.EndAppKeyForReference = null;     // the controller alone, as with no main window
        CheckYourAudioDialog.ReferenceSilenceWaitForTests = (rig, then) => { };
    }

    public void Dispose()
    {
        OutputChannelRecorder.Configure(render: false, record: false);
        if (_transcript != null) try { File.Delete(_transcript); } catch { }
        CheckYourAudioDialog.ReferenceSilenceWaitForTests = null;
        AudioWorkshopDialog.PttControllerSource = _sourceBefore;
        CheckYourAudioDialog.EndAppKeyForReference = _endBefore;
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
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

    private static string Receiving => Lexicon.Get("audio.ptt.announce_receiving");

    /// <summary>Start counting: a transcript of this test from here on, render still off.</summary>
    private void Listen()
    {
        _transcript = Path.Combine(Path.GetTempPath(), "jjflex-reference-end-speech-" + Guid.NewGuid().ToString("N") + ".jsonl");
        OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _transcript);
        Assert.False(OutputChannelRecorder.RenderEnabled, "Refusing: render is on");
    }

    /// <summary>Every sentence said since <see cref="Listen"/>, in order, after the arbiter's salvage window.</summary>
    private List<string> Said()
    {
        Thread.Sleep(900);   // past SpeechArbiter's 600 ms salvage settle, so a replay is counted
        using var stream = new FileStream(_transcript!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("event").GetString() == "speech"
                        && !e.GetProperty("gated").GetBoolean() && !e.GetProperty("suppressed").GetBoolean())
            .Select(e => e.GetProperty("text").GetString()!)
            .ToList();
    }

    private void ReferencePassAtItsEnd()
    {
        var player = _rig.TxFilePlayer;
        var content = new float[Rate / 5];
        Array.Fill(content, 0.4f);
        player.Load(content, Rate, "reference");
        player.StartHoldingSilenceAtEnd();
        for (int i = 0; i < 400 && !(player.ReachedEnd && !player.Engaged && player.HoldingEndSilence); i++)
        {
            var b = new float[Floats];
            Array.Fill(b, 0.9f);
            player.Process(b, Floats, Rate);
        }
        Assert.True(player.HoldingEndSilence, "positive control: the pass is holding its end silence");
        Field("_refPassRunning").SetValue(_dialog, true);
    }

    /// <summary>The whole spoken ending under the app's key: the finished sentence, then, after the tail, the unkey.</summary>
    [Theory]
    [InlineData(VerbosityLevel.Chatty, true, "Transmitting of the reference recording is complete.")]
    [InlineData(VerbosityLevel.Terse, true, "Transmitting reference complete.")]
    [InlineData(VerbosityLevel.Chatty, false, "Transmitting of the reference recording is complete.")]
    [InlineData(VerbosityLevel.Terse, false, "Transmitting reference complete.")]
    public void Under_the_apps_key_the_finished_sentence_is_said_once_and_Receiving_not_at_all(VerbosityLevel level, bool lockKey, string finished)
    {
        AssertTheDeskIsSafe();
        ScreenReaderOutput.CurrentVerbosity = level;
        if (lockKey) _controller.ToggleLock(); else _controller.PttDown();
        Assert.True(_controller.IsTransmitting);
        ReferencePassAtItsEnd();
        int keying = _controller.Keyings;
        Listen();

        _dialog.FinishReferencePass(_rig, Holder.App, hardwareLine: false);
        Assert.Equal(Holder.App, _dialog.EndTransmissionForReference(_rig, Holder.App, null, keying));
        Assert.False(_controller.IsTransmitting);

        // Counted, not listed, so another test's late speech cannot make it
        // fail; the mutation (unkey with EscapeUnlock) gives "finished,
        // Receiving, finished": the bench's doubled ending, reproduced.
        var said = Said();
        Assert.Equal(1, said.Count(x => x == finished));
        Assert.Equal(0, said.Count(x => x == Receiving));
    }

    /// <summary>The suppression is the one call's: the next transmission's own end still says "Receiving".</summary>
    [Fact]
    public void The_next_transmissions_Receiving_is_not_swallowed()
    {
        AssertTheDeskIsSafe();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        _controller.ToggleLock();
        ReferencePassAtItsEnd();
        _dialog.FinishReferencePass(_rig, Holder.App, hardwareLine: false);
        _dialog.EndTransmissionForReference(_rig, Holder.App, null, _controller.Keyings);
        Assert.False(_controller.AudioCheckNarration, "the quiet unkey left the controller quiet");

        _controller.ToggleLock();
        Listen();
        _controller.ToggleLock();
        AssertReceivingOnce(Said());
    }

    /// <summary>
    /// "Receiving" said exactly once. Only "Receiving" is counted here: the
    /// finished sentence queued a moment earlier may be replayed after it,
    /// because this unkey comes inside that sentence's speaking time and the
    /// arbiter rescues what an interrupt cut, which is its general rule and
    /// not this round's to change.
    /// </summary>
    private static void AssertReceivingOnce(List<string> said) =>
        Assert.Equal(1, said.Count(s => s == Receiving));

    /// <summary>
    /// Guarded against a new transmission: the operator lets go and keys again
    /// inside the 300 ms tail. The tail's unkey must neither end that
    /// transmission nor end it quietly; its own end still says "Receiving".
    /// </summary>
    [Fact]
    public void A_transmission_begun_inside_the_tail_is_not_ended_by_it_and_keeps_its_Receiving()
    {
        AssertTheDeskIsSafe();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        _controller.ToggleLock();
        ReferencePassAtItsEnd();
        _dialog.FinishReferencePass(_rig, Holder.App, hardwareLine: false);
        int keying = _controller.Keyings;            // what the tail captured at the end

        _controller.ToggleLock();                    // the operator unlocks inside the tail...
        _controller.ToggleLock();                    // ...and locks again: a new transmission
        Assert.NotEqual(keying, _controller.Keyings);

        Assert.Equal(Holder.Nobody, _dialog.EndTransmissionForReference(_rig, Holder.App, null, keying));
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);   // the mutation: drop the keying check and the tail ends it, quietly

        Listen();
        _controller.ToggleLock();
        AssertReceivingOnce(Said());
    }

    /// <summary>A hardware line held through the end: no unkey, so the release sentence alone, once.</summary>
    [Fact]
    public void Under_a_hardware_line_the_release_sentence_is_said_once_and_nothing_else()
    {
        AssertTheDeskIsSafe();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        ReferencePassAtItsEnd();
        Listen();
        _dialog.FinishReferencePass(_rig, Holder.Elsewhere, hardwareLine: true);
        var said = Said();
        Assert.Equal(1, said.Count(x => x == "Transmitting of the reference recording is complete. Release your push to talk to stop transmitting dead air."));
        Assert.DoesNotContain(Receiving, said);
    }

    /// <summary>The check's key, unchanged: its controller was already quiet (AudioCheckNarration), and its unkey says nothing.</summary>
    [Fact]
    public void The_checks_own_unkey_is_unchanged_and_says_nothing()
    {
        AssertTheDeskIsSafe();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        _controller.AudioCheckNarration = true;
        try
        {
            _controller.KeyForCheck();
            Assert.True(_controller.IsTransmitting);
            Listen();
            _controller.EscapeUnlock();                  // what the check's stop does to the controller
            Assert.DoesNotContain(Receiving, Said());
        }
        finally { _controller.AudioCheckNarration = false; }
    }

    /// <summary>The control: an ordinary lock release still says "Receiving", once. The transcript sees it.</summary>
    [Fact]
    public void An_ordinary_lock_release_still_says_Receiving_once()
    {
        AssertTheDeskIsSafe();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
        _controller.ToggleLock();
        Listen();
        _controller.ToggleLock();
        AssertReceivingOnce(Said());
    }
}
