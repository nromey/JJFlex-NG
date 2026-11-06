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
    /// <remarks>
    /// It also counts the asynchronous work begun under it (part 7k). An
    /// <c>async void</c> method, which is what the tail is, tells the context
    /// it captured when it starts (<c>OperationStarted</c>, inside the call
    /// that starts it) and again when it finishes (<c>OperationCompleted</c>).
    /// So whether the call under test planned a tail is known the moment that
    /// call returns, and a planned tail can be waited on until it has really
    /// run, however late its timer fires. A fixed sleep could only guess at
    /// that, and a negative test that guesses short passes without having
    /// seen the continuation at all (Codex, round five repair 3, follow-up).
    /// </remarks>
    private sealed class HeldContinuations : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _work = new();
        private int _started, _finished;
        public override void Post(SendOrPostCallback d, object? state) => _work.Enqueue((d, state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public override void OperationStarted() => Interlocked.Increment(ref _started);
        public override void OperationCompleted() => Interlocked.Increment(ref _finished);
        public int Count => _work.Count;
        /// <summary>Asynchronous operations begun under this context: a planned tail is one.</summary>
        public int Started => Volatile.Read(ref _started);
        /// <summary>Begun and not yet finished.</summary>
        public int Outstanding => Started - Volatile.Read(ref _finished);
        public void RunAll() { while (_work.TryDequeue(out var w)) w.Callback(w.State); }
    }

    /// <summary>
    /// Run what the call left until every operation it began has finished,
    /// with no fixed wait (part 7k): a tail it planned is waited on until its
    /// continuation has been posted and has run; with nothing planned, nothing
    /// is waited for. A continuation that never comes fails here, instead of
    /// the test passing without it.
    /// </summary>
    private static void EverythingItBeganHasRun(HeldContinuations held)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (held.Outstanding > 0)
        {
            Assert.True(DateTime.UtcNow < until,
                $"a planned tail's continuation never came: {held.Outstanding} of {held.Started} still outstanding");
            if (held.Count == 0) Thread.Sleep(5);
            held.RunAll();
        }
        held.RunAll();
    }

    /// <summary>The production tail, <c>EndTransmissionAfterReferenceTail</c>, started as the poll starts it, its continuation held.</summary>
    private HeldContinuations StartTheRealTail(Holder holder = Holder.App)
    {
        var tail = typeof(CheckYourAudioDialog).GetMethod("EndTransmissionAfterReferenceTail", Any);
        Assert.True(tail != null, "production entry CheckYourAudioDialog.EndTransmissionAfterReferenceTail is missing");
        var held = new HeldContinuations();
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(held);
        try { tail!.Invoke(_dialog, new object[] { _rig, holder }); }
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

    // ── a check-owned tail, bound to its transmission (round five repair 2, 7i-1) ──
    //
    // The session cannot be started here: StartAsync counts down, arms a
    // recorder and writes power, all against a connected radio. So it is the
    // production object, uninitialized, given the fields its keyed state has
    // after StartAsync (Codex's created state), and keyed through the
    // controller's own KeyForCheck, as StartAsync keys it.

    private static readonly Type SessionType =
        typeof(CheckYourAudioDialog).GetNestedType("AudioCheckSession", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("production type CheckYourAudioDialog.AudioCheckSession is missing");

    private static FieldInfo SessionField(string name)
    {
        var f = SessionType.GetField(name, Any);
        Assert.True(f != null, "production field AudioCheckSession." + name + " is missing");
        return f!;
    }

    private static bool SessionKeyed(object session) =>
        (bool)SessionType.GetProperty("Keyed", Any)!.GetValue(session)!;

    /// <summary>
    /// A check keyed as StartAsync keys it, as this window's session: a Monitor
    /// check unless told otherwise, keyed <paramref name="keyedSecondsAgo"/>
    /// seconds ago, holding the identity StartAsync takes the moment it keys
    /// (7i-2; that line is pinned in Radios.Tests.UnifiedTakesReferenceEndTests).
    /// </summary>
    private object TheCheckKeys(AudioCheckListenMethods method = AudioCheckListenMethods.Monitor, double keyedSecondsAgo = 0)
    {
        var session = RuntimeHelpers.GetUninitializedObject(SessionType);
        SessionField("_owner").SetValue(session, _dialog);
        SessionField("_ptt").SetValue(session, _controller);
        SessionField("_changedRig").SetValue(session, _rig);
        SessionField("_method").SetValue(session, method);
        SessionField("_recorderTarget").SetValue(session, SliceRecorderTarget.TransmitSlice);
        SessionField("_restores").SetValue(session, Activator.CreateInstance(SessionField("_restores").FieldType));
        SessionField("_keyedAt").SetValue(session, DateTime.UtcNow.AddSeconds(-keyedSecondsAgo));
        Field("_checkStarts").SetValue(_dialog, new AudioCheckStartGate());
        // Its watcher, built and never started: End stops it (part 7j's close and end paths).
        SessionField("_watcher").SetValue(session, Activator.CreateInstance(SessionField("_watcher").FieldType));
        _controller.AudioCheckNarration = true;
        _controller.KeyForCheck();
        Assert.True(_controller.IsTransmitting, "positive control: the check keyed");
        SessionField("_ownKeying").SetValue(session, CheckYourAudioDialog.ReferenceKeying.Now(_rig, _controller));
        SessionField("_phase").SetValue(session, Enum.Parse(SessionField("_phase").FieldType, "Keyed"));
        SessionType.GetProperty("Active", Any)!.SetValue(session, true);
        Field("_session").SetValue(_dialog, session);
        Assert.True(SessionKeyed(session), "positive control: the session reads keyed");
        return session;
    }

    /// <summary>The real system-wide lock key, as MainWindow handles it, on this controller.</summary>
    private void TheLockKeyIsPressed()
    {
        var main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        var ptt = typeof(MainWindow).GetField("_pttController", Any);
        var powered = typeof(MainWindow).GetField("_radioPowerOn", Any);
        var toggle = typeof(MainWindow).GetMethod("HandleSystemWideLockToggle", Any);
        Assert.True(ptt != null && powered != null && toggle != null, "production MainWindow lock door is missing");
        ptt!.SetValue(main, _controller);
        powered!.SetValue(main, true);
        toggle!.Invoke(main, null);
    }

    /// <summary>
    /// Codex's round five repair B2, as a created state through the real
    /// delayed continuation and the real system-wide lock door: a check-owned
    /// reference ends, and inside the 300 ms tail the operator unlocks the
    /// check's key and locks again. The session still reads Keyed, because
    /// its watcher samples once a second. When the old tail comes due, the
    /// new key must stay up, and the session must stop believing it is keyed.
    /// </summary>
    [Fact]
    public void A_check_tail_leaves_a_key_the_operator_locked_inside_it_alone()
    {
        AssertTheDeskIsSafe();
        try
        {
            var session = TheCheckKeys();
            ReferencePassAtItsEnd();
            _dialog.FinishReferencePass(_rig, Holder.Check, hardwareLine: false);
            var held = StartTheRealTail(Holder.Check);

            TheLockKeyIsPressed();                   // the operator unlocks the check's key...
            TheLockKeyIsPressed();                   // ...and locks again: their own transmission
            Assert.Equal(2, _controller.Keyings);
            Assert.True(SessionKeyed(session), "positive control: the session has not seen the release");

            TheTailComesDue(held);
            Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);   // red at dc355793: the old tail's StopCheck ended it
            Assert.False(SessionKeyed(session), "the session goes on believing the operator's key is its own");
        }
        finally
        {
            _controller.AudioCheckNarration = false;
            Field("_session").SetValue(_dialog, null);
        }
    }

    /// <summary>The control: the same check, nothing pressed inside the tail. The tail ends the check as Stop does.</summary>
    [Fact]
    public void A_check_tail_with_nothing_pressed_inside_it_still_stops_the_check()
    {
        AssertTheDeskIsSafe();
        try
        {
            var session = TheCheckKeys();
            ReferencePassAtItsEnd();
            _dialog.FinishReferencePass(_rig, Holder.Check, hardwareLine: false);
            var held = StartTheRealTail(Holder.Check);
            Assert.True(_controller.IsTransmitting, "nothing ends before the tail comes due");

            TheTailComesDue(held);
            Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
            Assert.Equal(1, _controller.Keyings);
            Assert.False(SessionKeyed(session), "the check did not stop");
        }
        finally
        {
            _controller.AudioCheckNarration = false;
            Field("_session").SetValue(_dialog, null);
        }
    }

    // ── the check's own key, with no reference at all (round five repair 2, 7i-2) ──

    /// <summary>The session's one-second watcher, its real tick handler.</summary>
    private static void TheWatcherTicks(object session)
    {
        var tick = SessionType.GetMethod("Watcher_Tick", Any);
        Assert.True(tick != null, "production entry AudioCheckSession.Watcher_Tick is missing");
        tick!.Invoke(session, new object?[] { null, EventArgs.Empty });
    }

    /// <summary>
    /// The same schedule as B2 without a reference, on the session's own
    /// clock: a recording check has run to the radio's two-minute buffer, and
    /// between two ticks the operator unlocked its key and locked again. The
    /// next tick must not end the operator's key at the recorder's limit; the
    /// check's own key ended when they unlocked it, and the session says so.
    /// </summary>
    [Fact]
    public void A_key_made_between_two_ticks_is_not_ended_at_a_recording_checks_limit()
    {
        AssertTheDeskIsSafe();
        try
        {
            var session = TheCheckKeys(AudioCheckListenMethods.RecordPlayback, FlexBase.SliceRecordBufferSeconds + 1);
            TheLockKeyIsPressed();                   // the operator unlocks the check's key...
            TheLockKeyIsPressed();                   // ...and locks again, inside one second
            Assert.Equal(2, _controller.Keyings);
            Assert.True(SessionKeyed(session), "positive control: the watcher has not seen the release");

            TheWatcherTicks(session);
            Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);   // red at dc355793: the recorder's limit ended it
            Assert.False(SessionKeyed(session), "the session goes on believing the operator's key is its own");
        }
        finally
        {
            _controller.AudioCheckNarration = false;
            Field("_session").SetValue(_dialog, null);
        }
    }

    /// <summary>The control: the check's own key at the same limit, nothing pressed. The limit still ends it.</summary>
    [Fact]
    public void A_recording_checks_own_key_still_ends_at_its_limit()
    {
        AssertTheDeskIsSafe();
        try
        {
            var session = TheCheckKeys(AudioCheckListenMethods.RecordPlayback, FlexBase.SliceRecordBufferSeconds + 1);
            TheWatcherTicks(session);
            Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
            Assert.False(SessionKeyed(session), "the check did not move on from keyed");
        }
        finally
        {
            _controller.AudioCheckNarration = false;
            Field("_session").SetValue(_dialog, null);
        }
    }

    /// <summary>The other control: the check's own key, well inside the limit. A tick leaves it keyed.</summary>
    [Fact]
    public void A_tick_inside_the_limit_leaves_the_checks_own_key_alone()
    {
        AssertTheDeskIsSafe();
        try
        {
            var session = TheCheckKeys(AudioCheckListenMethods.RecordPlayback, 5);
            TheWatcherTicks(session);
            Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
            Assert.True(SessionKeyed(session), "the watcher took the check's own key for someone else's");
        }
        finally
        {
            _controller.AudioCheckNarration = false;
            Field("_session").SetValue(_dialog, null);
        }
    }

    // ── one owner for the check's key (part 7j) ──
    //
    // Every consumer that decides "this is the check's own key" asks the
    // session's own recorded keying (OwnKeyHeld), and the controller's own
    // speech and warning ladder ask its record of the same keying. Codex's
    // round five repair 2 findings, as created states: B3 (the close and the
    // stop), B4 (a key made before the end poll), B5 (the surviving key's
    // "Receiving"), and the distinct-controller control. Each beside the
    // ordinary check with nothing pressed, which must not change.

    private static readonly string StillTransmitting = Lexicon.Get("audio.check.still_transmitting");
    private static string Locked => Lexicon.Get("audio.ptt.announce_locked");

    private static object Phase(object session) => SessionField("_phase").GetValue(session)!;
    private static bool Is(object session, string phase) => Phase(session).ToString() == phase;
    private static bool Active(object session) => (bool)SessionType.GetProperty("Active", Any)!.GetValue(session)!;
    private static bool RecordStopAsked(object session) => SessionField("_recordStop").GetValue(session) != null;

    /// <summary>The operator unlocks the check's key and locks again, through the real system-wide lock door.</summary>
    private void TheOperatorRelocks()
    {
        TheLockKeyIsPressed();
        TheLockKeyIsPressed();
        Assert.Equal(2, _controller.Keyings);
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
    }

    /// <summary>The session's <c>ForceEnd</c>, as the window's Closed handler, SetRig and a teardown call it.</summary>
    private static void TheWindowCloses(object session) =>
        SessionType.GetMethod("ForceEnd", Any)!.Invoke(session, new object?[] { null });

    /// <summary>The session's <c>StopCheck</c>, as the Stop button, Ctrl+Enter and Escape call it.</summary>
    private static void StopIsPressed(object session) =>
        SessionType.GetMethod("StopCheck", Any)!.Invoke(session, new object?[] { false });

    private void EndCheck(object session)
    {
        _controller.AudioCheckNarration = false;
        _controller.PlannedStopSeconds = null;
        Field("_session").SetValue(_dialog, null);
    }

    /// <summary>
    /// B3, the close: a recording check keyed, and between two ticks the
    /// operator unlocked its key and locked again. The window closes. The
    /// operator's key stays up; the session ends.
    /// </summary>
    [Fact]
    public void Closing_the_window_leaves_a_key_the_operator_locked_between_two_ticks()
    {
        AssertTheDeskIsSafe();
        var session = TheCheckKeys(AudioCheckListenMethods.RecordPlayback, 5);
        try
        {
            TheOperatorRelocks();
            Assert.True(SessionKeyed(session), "positive control: the watcher has not seen the release");
            TheWindowCloses(session);
            Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);   // red at 6c8777f5: ForceEnd asked "transmitting"
            Assert.False(Active(session), "the session outlived its window");
        }
        finally { EndCheck(session); }
    }

    /// <summary>The control: the same close with nothing pressed ends the check's own key, as ever.</summary>
    [Fact]
    public void Closing_the_window_still_ends_the_checks_own_key()
    {
        AssertTheDeskIsSafe();
        var session = TheCheckKeys(AudioCheckListenMethods.RecordPlayback, 5);
        try
        {
            TheWindowCloses(session);
            Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
            Assert.Equal(1, _controller.Keyings);
            Assert.False(Active(session));
        }
        finally { EndCheck(session); }
    }

    /// <summary>
    /// B3, the stop: the same re-key, then Stop, before the watcher has seen it
    /// and after. A press over a key that is not the check's still lets it go,
    /// with "Still transmitting. Stop the check first.": the operator's explicit
    /// choice, kept as ruled for part 7j. What changes is that the session's
    /// own key is recognised as ended (its recorder stopped through the
    /// external path, whatever the watcher's timing), and the operator's key's
    /// "Receiving" is no longer swallowed by the check's narration.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_stop_over_a_key_the_operator_locked_lets_it_go_as_their_choice(bool watcherSawItFirst)
    {
        AssertTheDeskIsSafe();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        var session = TheCheckKeys(AudioCheckListenMethods.RecordPlayback, 5);
        try
        {
            TheOperatorRelocks();
            if (watcherSawItFirst)
            {
                TheWatcherTicks(session);
                Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
                Assert.True(Is(session, "AwaitPlayback"), "the watcher did not see the check's key end");
            }
            Listen();
            StopIsPressed(session);
            Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);   // the press's explicit choice
            Assert.True(Is(session, "AwaitPlayback"));
            Assert.True(RecordStopAsked(session), "the check's recorder was not stopped");
            var said = Said();
            Assert.Contains(StillTransmitting, said);
            Assert.Contains(Receiving, said);       // red at 6c8777f5: the check's narration swallowed it
        }
        finally { EndCheck(session); }
    }

    /// <summary>The control: Stop over the check's own key is unchanged, and quiet.</summary>
    [Fact]
    public void A_stop_of_the_checks_own_key_is_unchanged()
    {
        AssertTheDeskIsSafe();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        var session = TheCheckKeys(AudioCheckListenMethods.RecordPlayback, 5);
        try
        {
            Listen();
            StopIsPressed(session);
            Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
            Assert.Equal(1, _controller.Keyings);
            Assert.True(Is(session, "AwaitPlayback"));
            Assert.True(RecordStopAsked(session));
            var said = Said();
            Assert.DoesNotContain(Receiving, said);
            Assert.DoesNotContain(StillTransmitting, said);
        }
        finally { EndCheck(session); }
    }

    /// <summary>The window's real end poll, <c>FinishPassIfItEnded</c>, any tail it starts held.</summary>
    private HeldContinuations ThePollSeesTheEnd()
    {
        var finish = typeof(CheckYourAudioDialog).GetMethod("FinishPassIfItEnded", Any);
        Assert.True(finish != null, "production entry CheckYourAudioDialog.FinishPassIfItEnded is missing");
        var held = new HeldContinuations();
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(held);
        try { Assert.True((bool)finish!.Invoke(_dialog, new object[] { _rig })!, "positive control: the poll saw the end"); }
        finally { SynchronizationContext.SetSynchronizationContext(prior); }
        return held;
    }

    /// <summary>
    /// B4: the reference reaches its end, and before the 500 ms poll the
    /// operator unlocks the check's key and locks again. The poll must not
    /// plan the new key as the check's; with the watcher's tick first, nor as
    /// the app's. Either way it is left up.
    /// </summary>
    /// <remarks>
    /// Judged after anything the poll began has really run (part 7k), not
    /// after a fixed sleep: a tail the poll wrongly planned is caught however
    /// late its continuation comes. And "planned no tail" is asserted in its
    /// own right, with the control below showing the count sees a real one.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_key_locked_before_the_end_poll_is_not_the_references_to_end(bool watcherSawItFirst)
    {
        AssertTheDeskIsSafe();
        var session = TheCheckKeys(AudioCheckListenMethods.RecordPlayback, 5);
        try
        {
            ReferencePassAtItsEnd();
            TheOperatorRelocks();
            if (watcherSawItFirst) TheWatcherTicks(session);
            var held = ThePollSeesTheEnd();
            EverythingItBeganHasRun(held);
            Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);   // red at 6c8777f5 (without the tick: planned as the check's)
            Assert.Equal(2, _controller.Keyings);
            Assert.Equal(0, held.Started);           // the poll planned no tail for the operator's key
        }
        finally { EndCheck(session); }
    }

    /// <summary>The control: nothing pressed before the poll. The poll's tail stops the check, as ever.</summary>
    [Fact]
    public void With_nothing_pressed_before_the_end_poll_its_tail_stops_the_check()
    {
        AssertTheDeskIsSafe();
        var session = TheCheckKeys(AudioCheckListenMethods.RecordPlayback, 5);
        try
        {
            ReferencePassAtItsEnd();
            var held = ThePollSeesTheEnd();
            Assert.Equal(1, held.Started);           // positive control: the count sees the tail the poll plans
            EverythingItBeganHasRun(held);
            Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
            Assert.Equal(1, _controller.Keyings);
            Assert.True(Is(session, "AwaitPlayback"), "the check did not stop");
            Assert.True(RecordStopAsked(session));
        }
        finally { EndCheck(session); }
    }

    /// <summary>
    /// B5: the stale tail of 7i-1 leaves the operator's key up and tells the
    /// session its key ended; the session goes on (Monitor's grace, or a
    /// recording check's playback). When the operator lets their key go, they
    /// hear "Receiving", once.
    /// </summary>
    [Fact]
    public void The_operators_surviving_key_still_says_Receiving_at_release()
    {
        AssertTheDeskIsSafe();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        var session = TheCheckKeys();
        try
        {
            ReferencePassAtItsEnd();
            _dialog.FinishReferencePass(_rig, Holder.Check, hardwareLine: false);
            var held = StartTheRealTail(Holder.Check);
            TheOperatorRelocks();
            TheTailComesDue(held);
            Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
            Assert.True(Active(session) && Is(session, "AwaitPlayback"), "positive control: the session is still open");
            Assert.True(_controller.AudioCheckNarration, "positive control: the check still narrates its own");

            Listen();
            TheLockKeyIsPressed();                   // the operator lets their own key go
            Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
            AssertReceivingOnce(Said());             // red at 6c8777f5: nothing was said
        }
        finally { EndCheck(session); }
    }

    /// <summary>
    /// The same narration, at the other end of the operator's key: while the
    /// check is open, their own key-down is said, and the check's own release,
    /// which they pressed, stays as quiet as the check's release always was.
    /// </summary>
    [Fact]
    public void The_operators_own_key_down_during_a_check_is_said_and_the_checks_release_is_not()
    {
        AssertTheDeskIsSafe();
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        var session = TheCheckKeys();
        try
        {
            Listen();
            TheLockKeyIsPressed();                   // the check's key, let go: the check's own, quiet
            TheLockKeyIsPressed();                   // the operator's own key
            var said = Said();
            Assert.Equal(1, said.Count(x => x == Locked));   // red at 6c8777f5: swallowed
            Assert.DoesNotContain(Receiving, said);
        }
        finally { EndCheck(session); }
    }

    /// <summary>The controller's warning ladder: its private rule, read as the tick reads it.</summary>
    private bool PlannedStopOwnsTheCut()
    {
        var rule = typeof(PttSafetyController).GetProperty("PlannedStopOwnsTheCut", Any);
        Assert.True(rule != null, "production rule PttSafetyController.PlannedStopOwnsTheCut is missing");
        return (bool)rule!.GetValue(_controller)!;
    }

    /// <summary>
    /// A recording check's planned stop stands the warning ladder down for the
    /// check's own key only. A key the operator makes while the check waits
    /// for its playback is not the check's to stop, and keeps its warnings.
    /// </summary>
    [Fact]
    public void A_key_the_operator_makes_during_a_recording_check_keeps_its_timeout_warnings()
    {
        AssertTheDeskIsSafe();
        _controller.PlannedStopSeconds = FlexBase.SliceRecordBufferSeconds;   // as StartAsync sets it, before keying
        var session = TheCheckKeys(AudioCheckListenMethods.RecordPlayback, 5);
        try
        {
            Assert.True(PlannedStopOwnsTheCut(), "positive control: the check's own key's ladder stands down");
            TheOperatorRelocks();
            Assert.False(PlannedStopOwnsTheCut());   // red at 6c8777f5: the operator's key had no warnings
        }
        finally { EndCheck(session); }
    }

    /// <summary>
    /// An off-air check whose key ended while the operator's own key is up:
    /// its post-unkey wait ends, rather than holding the session (and the
    /// window's push to talk and lock, refused while it runs) open for as long
    /// as the operator transmits. The operator's key is left alone.
    /// </summary>
    [Fact]
    public void An_off_air_check_past_its_key_ends_while_the_operator_transmits()
    {
        AssertTheDeskIsSafe();
        var session = TheCheckKeys(AudioCheckListenMethods.OffAir, 5);
        try
        {
            TheOperatorRelocks();
            TheWatcherTicks(session);                // the check's key ended elsewhere
            Assert.True(Is(session, "AwaitPlayback"));
            TheWatcherTicks(session);
            Assert.False(Active(session), "the off-air check waited on the operator's key");   // red at 6c8777f5
            Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        }
        finally { EndCheck(session); }
    }

    /// <summary>The control: the off-air check's own key ends (a timeout's unkey), and its wait ends as before.</summary>
    [Fact]
    public void An_off_air_check_whose_own_key_ended_still_ends()
    {
        AssertTheDeskIsSafe();
        var session = TheCheckKeys(AudioCheckListenMethods.OffAir, 5);
        try
        {
            _controller.EscapeUnlock();              // ended by the controller, as a timeout ends it
            TheWatcherTicks(session);
            TheWatcherTicks(session);
            Assert.False(Active(session));
            Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        }
        finally { EndCheck(session); }
    }

    /// <summary>
    /// Codex's distinct-controller control: the check keyed through one
    /// controller, and the app's (MainWindow's) is a different instance, idle.
    /// The check's own holder and route must be the session's controller,
    /// through the real end poll: with nothing pressed, the tail stops the
    /// check's key; with the operator's re-key on the check's controller
    /// inside the tail, it stays up. A holder or a route asking the app's
    /// controller gets one of these two wrong.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_checks_route_is_its_own_controller_not_the_apps(bool relock)
    {
        AssertTheDeskIsSafe();
        var app = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        var session = TheCheckKeys();
        try
        {
            AudioWorkshopDialog.PttControllerSource = () => app;
            ReferencePassAtItsEnd();
            var held = ThePollSeesTheEnd();
            if (relock) TheOperatorRelocks();
            TheTailComesDue(held);
            Assert.Equal(relock ? PttSafetyController.PttState.Locked : PttSafetyController.PttState.Idle, _controller.State);
            Assert.Equal(PttSafetyController.PttState.Idle, app.State);
            Assert.False(SessionKeyed(session), "the session still reads keyed");
        }
        finally
        {
            AudioWorkshopDialog.PttControllerSource = () => _controller;
            app.Dispose();
            EndCheck(session);
        }
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
        Assert.Equal(1, held.Started);               // positive control (part 7k): the count sees the tail a switch plans

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
            EverythingItBeganHasRun(none);           // part 7k: anything it planned has run, not a fixed sleep
            Assert.Equal(PttSafetyController.PttState.Locked, main.State);
            Assert.Equal(0, none.Started);           // the mutation: drop KeysRadio from the holder
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
