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
        var keying = CheckYourAudioDialog.ReferenceKeying.Now(_rig, _controller);
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
        _dialog.EndTransmissionForReference(_rig, Holder.App, null, CheckYourAudioDialog.ReferenceKeying.Now(_rig, _controller));
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
        var keying = CheckYourAudioDialog.ReferenceKeying.Now(_rig, _controller);   // what the tail captured at the end

        _controller.ToggleLock();                    // the operator unlocks inside the tail...
        _controller.ToggleLock();                    // ...and locks again: a new transmission
        Assert.NotEqual(keying.Keyings, _controller.Keyings);

        Assert.Equal(Holder.Nobody, _dialog.EndTransmissionForReference(_rig, Holder.App, null, keying));
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);   // the mutation: drop the keying check and the tail ends it, quietly

        Listen();
        _controller.ToggleLock();
        AssertReceivingOnce(Said());
    }

    // ── the real delayed tail (round five repair, 7h-1) ──

    /// <summary>
    /// Holds the tail's continuation instead of running it, so a test can put
    /// something between the reference's end and the moment the tail acts,
    /// then run that continuation exactly as the dispatcher would have.
    /// </summary>
    private sealed class HeldContinuations : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _work = new();
        public override void Post(SendOrPostCallback d, object? state) => _work.Enqueue((d, state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public int Count => _work.Count;
        public void RunAll() { while (_work.TryDequeue(out var w)) w.Callback(w.State); }
    }

    /// <summary>The production tail, <c>EndTransmissionAfterReferenceTail</c>, started as the poll starts it, its continuation held.</summary>
    private HeldContinuations StartTheRealTail()
    {
        var tail = typeof(CheckYourAudioDialog).GetMethod("EndTransmissionAfterReferenceTail", Any);
        Assert.True(tail != null, "production entry CheckYourAudioDialog.EndTransmissionAfterReferenceTail is missing");
        var held = new HeldContinuations();
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(held);
        try { tail!.Invoke(_dialog, new object[] { _rig, Holder.App }); }
        finally { SynchronizationContext.SetSynchronizationContext(prior); }
        return held;
    }

    /// <summary>Wait out the real 300 ms tail, then run its continuation as the dispatcher would.</summary>
    private static void TheTailComesDue(HeldContinuations held)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (held.Count == 0 && DateTime.UtcNow < until) Thread.Sleep(10);
        Assert.True(held.Count > 0, "positive control: the tail's delayed continuation was posted");
        held.RunAll();
    }

    /// <summary>
    /// Codex's round five B1, as a created state through the real delayed
    /// continuation: a reference ends under controller A's first key, A's key
    /// ends, the controller is replaced on the SAME radio (power-on builds a new
    /// one, an operator change too), and B's first key is up when A's tail comes
    /// due. Both counts are 1. The tail must leave B keyed, and B's own release
    /// must still say "Receiving".
    /// </summary>
    [Fact]
    public void A_tail_planned_on_a_replaced_controller_leaves_the_new_controllers_key_alone()
    {
        AssertTheDeskIsSafe();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        _controller.ToggleLock();
        ReferencePassAtItsEnd();
        _dialog.FinishReferencePass(_rig, Holder.App, hardwareLine: false);
        var held = StartTheRealTail();

        _controller.ToggleLock();                    // A's key ends inside the tail
        var replacement = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = true });
        try
        {
            AudioWorkshopDialog.PttControllerSource = () => replacement;
            replacement.ToggleLock();                // B's first real transmission
            Assert.Equal(_controller.Keyings, replacement.Keyings);   // positive control: the collision itself, 1 against 1

            TheTailComesDue(held);
            Assert.Equal(PttSafetyController.PttState.Locked, replacement.State);   // red before 7h-1: the tail ended it, quietly

            Listen();
            replacement.ToggleLock();
            AssertReceivingOnce(Said());
        }
        finally
        {
            AudioWorkshopDialog.PttControllerSource = () => _controller;
            if (replacement.IsTransmitting) replacement.EscapeUnlock();
            replacement.Dispose();
        }
    }

    /// <summary>
    /// The control for the one above: the same controller, no re-key, the same
    /// real delayed continuation. The tail still ends the key, quietly.
    /// </summary>
    [Fact]
    public void The_real_delayed_tail_still_ends_the_same_controllers_key_quietly()
    {
        AssertTheDeskIsSafe();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        _controller.ToggleLock();
        ReferencePassAtItsEnd();
        Listen();
        _dialog.FinishReferencePass(_rig, Holder.App, hardwareLine: false);
        var held = StartTheRealTail();
        Assert.True(_controller.IsTransmitting, "nothing ends before the tail comes due");

        TheTailComesDue(held);
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        var said = Said();
        Assert.Equal(1, said.Count(x => x == "Transmitting of the reference recording is complete."));
        Assert.Equal(0, said.Count(x => x == Receiving));
    }

    /// <summary>
    /// The connection half of the plan: the same controller, no keying since,
    /// but the radio's connection replaced inside the tail (a reconnect on the
    /// same radio wrapper that kept its controller). The tail ends nothing.
    /// The binding is set as the transport's Connecting report sets it, a new
    /// object; no socket is opened.
    /// </summary>
    [Fact]
    public void A_tail_planned_on_an_earlier_connection_ends_nothing()
    {
        AssertTheDeskIsSafe();
        var binding = typeof(FlexBase).GetProperty(nameof(FlexBase.CurrentConnectionBinding));
        Assert.True(binding?.SetMethod != null, "production property FlexBase.CurrentConnectionBinding has no setter");
        var before = binding!.GetValue(_rig);
        try
        {
            _controller.ToggleLock();
            ReferencePassAtItsEnd();
            _dialog.FinishReferencePass(_rig, Holder.App, hardwareLine: false);
            var held = StartTheRealTail();

            binding.SetValue(_rig, RuntimeHelpers.GetUninitializedObject(typeof(RadioConnectionBinding)));
            TheTailComesDue(held);
            Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);   // the mutation: drop the connection from the plan
        }
        finally { binding.SetValue(_rig, before); }
    }

    // ── a window switching straight to another radio (round five repair, 7h-2) ──
    //
    // SetRig itself cannot run here: it builds and starts the poll timer and
    // rebuilds the window's sections, which needs the built window. So the
    // helper it calls is driven in the order SetRig calls it, _rig already
    // moved to the arriving radio, and that order is pinned in source by
    // Radios.Tests.UnifiedTakesReferenceEndTests.

    private void TheWindowSwitchesTo(FlexBase arriving) => Field("_rig").SetValue(_dialog, arriving);

    /// <summary>SetRig's release of the departing radio, <c>ReleaseReferenceAtSwitch</c>, any tail it starts held.</summary>
    private HeldContinuations ReleaseAtSwitch(FlexBase departing)
    {
        var release = typeof(CheckYourAudioDialog).GetMethod("ReleaseReferenceAtSwitch", Any);
        Assert.True(release != null, "production entry CheckYourAudioDialog.ReleaseReferenceAtSwitch is missing");
        var held = new HeldContinuations();
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(held);
        try { release!.Invoke(_dialog, new object[] { departing }); }
        finally { SynchronizationContext.SetSynchronizationContext(prior); }
        return held;
    }

    /// <summary>
    /// A tail already planned when the window switches: until 7h-2 it returned
    /// because the window no longer had the radio, and the departing radio's
    /// lock went on keying it over silence. Now it lets go, quietly.
    /// </summary>
    [Fact]
    public void A_tail_planned_before_a_switch_still_lets_go_of_the_departing_radios_key()
    {
        AssertTheDeskIsSafe();
        using var arriving = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller.ToggleLock();
        ReferencePassAtItsEnd();
        _dialog.FinishReferencePass(_rig, Holder.App, hardwareLine: false);
        var held = StartTheRealTail();

        TheWindowSwitchesTo(arriving);
        ReleaseAtSwitch(_rig);                       // the pass is already finished: nothing new is said or planned
        TheTailComesDue(held);
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);   // red before 7h-2: Locked, over silence
    }

    /// <summary>
    /// A switch inside the pass's last half second, before the poll saw its
    /// end: the switch finishes it for the departing radio, the finished
    /// sentence once, and the app's key let go after the tail, with no
    /// "Receiving". The silence goes to that radio's own wait.
    /// </summary>
    [Fact]
    public void A_switch_before_the_poll_saw_the_end_finishes_the_pass_and_lets_go_after_the_tail()
    {
        AssertTheDeskIsSafe();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        int waits = 0;
        CheckYourAudioDialog.ReferenceSilenceWaitForTests = (rig, then) => { if (ReferenceEquals(rig, _rig)) waits++; };
        using var arriving = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller.ToggleLock();
        ReferencePassAtItsEnd();                     // at its end, the window not told
        Listen();

        TheWindowSwitchesTo(arriving);
        var held = ReleaseAtSwitch(_rig);
        Assert.True(_rig.TxReferenceEndSilenceHeld, "the switch ended the silence with the radio still keyed");
        Assert.True(waits >= 1, "the departing radio was left no wait to end its silence");
        Assert.True(_controller.IsTransmitting, "nothing ends before the tail comes due");

        TheTailComesDue(held);                       // red before 7h-2: no tail was ever planned
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        var said = Said();
        Assert.Equal(1, said.Count(x => x == "Transmitting of the reference recording is complete."));
        Assert.Equal(0, said.Count(x => x == Receiving));
    }

    /// <summary>
    /// The guard, both ways round: the controller keys whichever radio the main
    /// window has. When that is the ARRIVING radio, neither a tail planned on
    /// the departing one nor the switch's own finishing may end its key.
    /// </summary>
    [Fact]
    public void A_departing_radios_reference_never_ends_the_arriving_radios_key()
    {
        AssertTheDeskIsSafe();
        using var arriving = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        FlexBase keyed = _rig;
        var main = new PttSafetyController(() => keyed, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        try
        {
            AudioWorkshopDialog.PttControllerSource = () => main;

            // A tail planned while the controller keyed the departing radio,
            // which then keys the arriving one by the time it comes due.
            main.ToggleLock();
            ReferencePassAtItsEnd();
            _dialog.FinishReferencePass(_rig, Holder.App, hardwareLine: false);
            var held = StartTheRealTail();
            TheWindowSwitchesTo(arriving);
            keyed = arriving;
            TheTailComesDue(held);
            Assert.Equal(PttSafetyController.PttState.Locked, main.State);   // the mutation: drop KeysRadio from the holder, and the tail ends it

            // An end the switch finishes, the controller keying the arriving
            // radio throughout: not the app's, so no tail at all.
            _rig.TxFileStop();
            TheWindowSwitchesTo(_rig);
            ReferencePassAtItsEnd();
            TheWindowSwitchesTo(arriving);
            var none = ReleaseAtSwitch(_rig);
            Thread.Sleep((int)CheckYourAudioDialog.ReferenceEndTail.TotalMilliseconds + 200);
            Assert.Equal(0, none.Count);             // the mutation: drop KeysRadio from the holder
            Assert.Equal(PttSafetyController.PttState.Locked, main.State);
        }
        finally
        {
            AudioWorkshopDialog.PttControllerSource = () => _controller;
            if (main.IsTransmitting) main.EscapeUnlock();
            main.Dispose();
        }
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
