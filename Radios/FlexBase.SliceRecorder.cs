using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;
using Radios.Listenback;

namespace Radios;

/// <summary>
/// Which slice a recorder control acts on (#787). The flat keys and the JJ
/// key act on the ACTIVE slice (Noel, 2026-10-21 08:59); Home and the Slice
/// menu can name a slice by its letter, so a slice you are not listening to
/// can be recorded (11:00); the Audio Workshop's Loopback Check records on its
/// own listening slice.
/// </summary>
public readonly struct SliceRecorderTarget : IEquatable<SliceRecorderTarget>
{
    private enum Kind { Active, LoopbackEars, Letter, Transmit }

    private readonly Kind _kind;
    private readonly char _letter;

    private SliceRecorderTarget(Kind kind, char letter) { _kind = kind; _letter = letter; }

    /// <summary>Whatever slice is active when the command runs.</summary>
    public static SliceRecorderTarget ActiveSlice => new(Kind.Active, '\0');
    public static SliceRecorderTarget TransmitSlice => new(Kind.Transmit, '\0');
    public bool IsTransmitSlice => _kind == Kind.Transmit;

    /// <summary>The Loopback Check's listening slice, while it is arranged; otherwise the active slice.</summary>
    public static SliceRecorderTarget LoopbackEars => new(Kind.LoopbackEars, '\0');

    /// <summary>The slice with this letter, A to H, whether or not it is the active one.</summary>
    public static SliceRecorderTarget Letter(char letter) => new(Kind.Letter, char.ToUpperInvariant(letter));

    public bool IsActiveSlice => _kind == Kind.Active;
    public bool IsLoopbackEars => _kind == Kind.LoopbackEars;
    public char NamedLetter => _kind == Kind.Letter ? _letter : '\0';

    public bool Equals(SliceRecorderTarget other) => _kind == other._kind && _letter == other._letter;
    public override bool Equals(object obj) => obj is SliceRecorderTarget t && Equals(t);
    public override int GetHashCode() => ((int)_kind * 397) ^ _letter;
    public override string ToString() => _kind == Kind.Letter ? "slice " + _letter : _kind.ToString();
    public static bool operator ==(SliceRecorderTarget a, SliceRecorderTarget b) => a.Equals(b);
    public static bool operator !=(SliceRecorderTarget a, SliceRecorderTarget b) => !a.Equals(b);
}

/// <summary>
/// What a recorder control did, as a sentence to speak and the subject to
/// speak it under. One shape for the radio's slice recorder and the
/// computer recorder, so every door speaks the same way.
/// </summary>
/// <param name="Success">True when the requested state now holds.</param>
/// <param name="Message">A complete sentence, already worded from the lexicon.</param>
/// <param name="Subject">The <see cref="Speech.SpeechSubject"/> it belongs to.</param>
/// <param name="AlreadyInState">True when nothing needed changing: the recorder was already doing what was asked.</param>
public sealed record RecorderOutcome(bool Success, string Message, string Subject, bool AlreadyInState = false);

/// <summary>
/// The radio's own per-slice recorder (#787): one reply-and-status-checked surface that
/// every door uses — the Audio Check's "Record and play back", the Audio
/// Workshop's Record slice and Play recording buttons, JJ Flexible Home, the
/// Slice menu and the JJ key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one surface.</b> Until Tape Deck track 2 there were two. The Audio
/// Check drove <c>SliceRecordOn</c> and <c>SlicePlayOn</c>, setters that
/// enqueued <c>slice set N record=</c> and checked no reply, and nothing
/// refused playback while transmitting. Round 8 of #456 then added
/// <c>ToggleWorkshopSliceRecordAsync</c> and <c>ToggleWorkshopSlicePlayAsync</c>,
/// which did check replies and did refuse — so one recorder was reached through
/// two code paths that disagreed about safety. Those setters are gone; this is
/// the only code that sets a slice's <c>record</c> or <c>play</c>, and
/// <c>SliceRecorderOneSurfaceTests</c> holds the tree to that.
/// </para>
/// <para>
/// <b>Recorder behavior.</b> The buffer keeps the most recent 120 seconds.
/// Recordings are kept per slice. The recorder captures received audio and
/// transmit audio after the transmit filter, with the monitor off. Playback
/// replaces the slice's live audio. The radio offers no recording download.
/// </para>
/// <para>
/// <b>Per-slice state.</b> Each slice has its own recorder in the radio, so
/// each slice has its own state here: recording slice B does not stop slice A
/// playing. On ONE slice, record and play are exclusive — starting either
/// stops the other first, which is what an operator means by "record that,
/// now play it back".
/// </para>
/// <para>
/// <b>Intent is fixed at the press.</b> A local listen pauses VOX and
/// stops on a transmit edge. On-air playback joins only our existing
/// transmission and ends when that transmission ends. Play never requests keying.
/// </para>
/// <para>
/// <b>A running recording can always be stopped</b>, whoever started it — the
/// radio front panel, an earlier session, or a crash. Stopping destroys
/// nothing; the buffer stays playable. Round 8's buttons refused to touch a
/// recording they had not started; that refusal protected nothing and left an
/// operator with no way to stop it from here.
/// </para>
/// </remarks>
public partial class FlexBase
{
    /// <summary>The radio's record buffer, in seconds.</summary>
    public const int SliceRecordBufferSeconds = 120;

    /// <summary>
    /// How long into a recording the "nearly full" warning speaks: ten seconds
    /// before the cap, the same moment the Audio Check has always warned at.
    /// Settable for tests only.
    /// </summary>
    internal TimeSpan SliceRecordWarningAfter { get; set; } = TimeSpan.FromSeconds(SliceRecordBufferSeconds - 10);

    /// <summary>How long Play waits for the radio to report a recording after stopping one. Settable for tests.</summary>
    internal TimeSpan SlicePlayableWait { get; set; } = TimeSpan.FromSeconds(2);

    internal TimeSpan SliceRecorderConfirmationWait { get; set; } = TimeSpan.FromSeconds(4);
    internal TimeSpan SliceVoxOffWait { get; set; } = TimeSpan.FromSeconds(1.5);

    // Per-instance output seam keeps deterministic failure tests silent.
    internal Action<RecorderOutcome> RecorderStopFailureOutput { get; set; } = outcome =>
        ScreenReaderOutput.Speak(outcome.Message, Speech.SpeechIntent.Queue,
            VerbosityLevel.Critical, subject: outcome.Subject);

    private sealed class SliceRecorderState
    {
        internal Slice Slice;
        internal Radio Radio;
        internal CommandConnection Connection;
        internal bool Recording, Playing;
        internal volatile bool Retired;
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal Radio.SliceRemovedEventHandler Removed;
        // Which door started what this slice is doing, or null for a door
        // that does not need to clean up after itself (Home, the menus, the
        // keys). The Audio Workshop tags its buttons' work so that closing the
        // Workshop stops what the Workshop started and nothing else.
        internal object Owner;
        internal Timer CapWarning;
        internal PropertyChangedEventHandler Status;
        internal PropertyChangedEventHandler RadioStatus;
        internal PlaybackIntent Intent;
        internal VoxPauseOwner.Lease PlaybackVoxLease;
        internal double? RecordStarted, RecordedSeconds;
    }

    private readonly Dictionary<Slice, SliceRecorderState> _sliceRecorders = new(ReferenceEqualityComparer.Instance);
    private TransmitChangeDel _sliceRecorderKeyed;
    private long _slicePlaybackGeneration;
    internal Func<double> SliceRecorderClock { get; set; } = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>The host's common PTT admission check. Missing or throwing means no on-air playback.</summary>
    public Func<bool> SlicePlaybackTransmitAllowed { get; set; }

    private sealed record PlaybackIntent(bool OnAir, long Generation, Radio Radio, Slice Transmitter);

    private PlaybackIntent CapturePlaybackIntent()
    {
        EnsureKeyedWatch();
        // Sample the epoch first: an edge during the following state reads
        // must invalidate this press, not lend it the next transmission's epoch.
        long generation = Interlocked.Read(ref _slicePlaybackGeneration);
        return new(Transmit || theRadio?.Mox == true, generation,
            theRadio, theRadio?.TransmitSlice);
    }

    private bool LocalPlaybackVoxOff(Radio radio)
    {
        if (radio == null) return false;
        return radio.TransmitSlice?.DemodMode == "CW" ? !radio.CWBreakIn : !radio.SimpleVOXEnable;
    }

    private bool PlaybackIntentIsCurrent(PlaybackIntent intent, bool requireLocalVoxOff = true)
    {
        if (intent == null || intent.Generation != Interlocked.Read(ref _slicePlaybackGeneration) ||
            !ReferenceEquals(intent.Radio, theRadio) || theRadio?.Connected != true) return false;
        bool keyed = Transmit || theRadio.Mox;
        if (!intent.OnAir) return !keyed && (!requireLocalVoxOff || LocalPlaybackVoxOff(theRadio));
        if (!keyed || !CanTransmit || intent.Transmitter == null ||
            !ReferenceEquals(intent.Transmitter, theRadio.TransmitSlice) ||
            theRadio.TXClientHandle != theRadio.ClientHandle || !SliceRecorderTargetIsOurs(intent.Transmitter)) return false;
        try { return SlicePlaybackTransmitAllowed?.Invoke() == true; }
        catch { return false; }
    }

    /// <summary>Invalidate pending plays immediately, then stop running sources through the recorder queue.</summary>
    public void CancelSlicePlayback()
    {
        long generation = Interlocked.Increment(ref _slicePlaybackGeneration);
        List<SliceRecorderState> playing;
        lock (_sliceRecorders) playing = _sliceRecorders.Values.Where(s => s.Playing || s.Intent != null).ToList();
        foreach (var state in playing) _ = StopPlayingForTransmitAsync(state, generation);
    }

    /// <summary>Measured duration in this connection; null for a pre-existing take of unknown length.</summary>
    /// <remarks>FlexLib ignores record_time, whose units and meaning are undocumented here. Do not guess from it.</remarks>
    public double? SliceRecordingSeconds(SliceRecorderTarget target)
    {
        var slice = ResolveSliceRecorderTarget(target);
        if (!SliceRecorderTargetIsOurs(slice)) return null;
        var observed = StateFor(slice);
        lock (_sliceRecorders)
        {
            var state = observed;
            if (!StateIsLive(state)) return null;
            if (state.RecordStarted is double start)
                return Math.Clamp(SliceRecorderClock() - start, 0, SliceRecordBufferSeconds);
            return slice.PlayEnabled ? state.RecordedSeconds : null;
        }
    }

    private void NoteRecording(SliceRecorderState state, bool recording)
    {
        state.Recording = recording;
        if (!recording && state.RecordStarted is double start)
        {
            state.RecordedSeconds = Math.Clamp(SliceRecorderClock() - start, 0, SliceRecordBufferSeconds);
            state.RecordStarted = null;
        }
    }

    /// <summary>Raised with the slice letter when a recording is ten seconds from the buffer's cap.</summary>
    public event Action<string> SliceRecordingNearlyFull;

    // ── Resolving a target ──────────────────────────────────────────────

    private Slice ResolveSliceRecorderTarget(SliceRecorderTarget target)
    {
        var radio = theRadio;
        if (radio == null) return null;
        if (target.IsTransmitSlice) return radio.TransmitSlice;
        if (target.IsLoopbackEars && _loopbackArranged && _lbResources?.Slice != null) return _lbResources.Slice;
        if (target.IsActiveSlice || target.IsLoopbackEars) return radio.ActiveSlice;
        return SliceByLetter(target.NamedLetter);
    }

    /// <summary>
    /// Our slice with this letter. The radio's own letter wins; the index
    /// arithmetic <see cref="LetterToVFO"/> uses is the fallback for a slice
    /// whose letter has not been reported yet.
    /// </summary>
    private Slice SliceByLetter(char letter)
    {
        string wanted = letter.ToString();
        lock (mySlices)
        {
            var named = mySlices.FirstOrDefault(s => string.Equals(s.Letter, wanted, StringComparison.OrdinalIgnoreCase));
            if (named != null) return named;
        }
        int vfo = LetterToVFO(letter);
        return vfo < 0 ? null : VFOToSlice(vfo);
    }

    private bool SliceRecorderTargetIsOurs(Slice slice) => slice != null && theRadio != null && theRadio.Connected &&
        slice.ClientHandle == theRadio.ClientHandle && theRadio.SliceList.Contains(slice) && mySlices.Contains(slice);

    private static string LetterOf(Slice slice) =>
        string.IsNullOrEmpty(slice?.Letter) ? ((char)('A' + Math.Max(0, slice?.Index ?? 0))).ToString() : slice.Letter;

    private static string Say(string key, string letter) => Lexicon.Get(key, ("letter", letter));

    private static RecorderOutcome Outcome(bool success, string key, string letter, bool already = false) =>
        new(success, Say(key, letter), Speech.SpeechSubject.SliceRecorder(letter), already);

    /// <summary>The letter of the slice this target resolves to now, or empty when none does.</summary>
    public string SliceRecorderLetter(SliceRecorderTarget target)
    {
        var slice = ResolveSliceRecorderTarget(target);
        return slice == null ? "" : LetterOf(slice);
    }

    /// <summary>True while the target slice is recording, as far as this app or the radio knows.</summary>
    public bool IsSliceRecording(SliceRecorderTarget target)
    {
        var slice = ResolveSliceRecorderTarget(target);
        if (!SliceRecorderTargetIsOurs(slice)) return false;
        lock (_sliceRecorders)
            if (_sliceRecorders.TryGetValue(slice, out var state) && StateIsLive(state)) return state.Recording || slice.RecordOn;
        return slice.RecordOn;
    }

    /// <summary>True while the target slice is playing its recording.</summary>
    public bool IsSlicePlaying(SliceRecorderTarget target)
    {
        var slice = ResolveSliceRecorderTarget(target);
        if (!SliceRecorderTargetIsOurs(slice)) return false;
        lock (_sliceRecorders)
            if (_sliceRecorders.TryGetValue(slice, out var state) && StateIsLive(state)) return state.Playing;
        return slice.PlayOn;
    }

    /// <summary>True when the radio reports a playable recording on the target slice.</summary>
    public bool SliceHasRecording(SliceRecorderTarget target)
    {
        var slice = ResolveSliceRecorderTarget(target);
        return SliceRecorderTargetIsOurs(slice) && slice.PlayEnabled;
    }

    // ── Wire ────────────────────────────────────────────────────────────

    private bool StateIsLive(SliceRecorderState state) => !state.Retired && state.Radio != null &&
        ReferenceEquals(theRadio, state.Radio) && state.Radio.Connected &&
        ReferenceEquals(state.Connection, state.Radio.CurrentCommandConnection) &&
        state.Connection?.State == CommandConnectionState.Connected;

    private SliceRecorderState StateFor(Slice slice)
    {
        lock (_sliceRecorders)
        {
            // A state left over from an earlier connection describes a radio
            // that is gone; nothing it says about recording is true any more.
            foreach (var stale in _sliceRecorders.Where(p => !StateIsLive(p.Value)).Select(p => p.Key).ToList())
                DropStateLocked(stale);
            if (!_sliceRecorders.TryGetValue(slice, out var state))
            {
                state = new SliceRecorderState
                {
                    Slice = slice, Radio = theRadio, Connection = theRadio?.CurrentCommandConnection,
                };
                state.Status = (_, args) => OnRecorderSliceStatus(state, args.PropertyName);
                slice.PropertyChanged += state.Status;
                state.RadioStatus = (_, args) =>
                {
                    if (args.PropertyName is nameof(Radio.TXClientHandle) or nameof(Radio.TransmitSlice) or
                        nameof(Radio.SimpleVOXEnable) or nameof(Radio.CWBreakIn) or nameof(Radio.Connected) or nameof(Radio.SliceList))
                    {
                        if (state.Intent != null && !PlaybackIntentIsCurrent(state.Intent)) CancelSlicePlayback();
                    }
                };
                state.Radio.PropertyChanged += state.RadioStatus;
                state.Removed = removed =>
                {
                    if (!ReferenceEquals(removed, slice)) return;
                    // FlexLib raises this event while holding its slice-list
                    // lock during disconnect. Retire now so queries and the
                    // warning cannot use the removed slice. Do the cleanup
                    // after the event returns; it must not wait for our lock.
                    state.Retired = true;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        lock (_sliceRecorders)
                            if (_sliceRecorders.TryGetValue(slice, out var current) && ReferenceEquals(current, state))
                                DropStateLocked(slice);
                    });
                };
                state.Radio.SliceRemoved += state.Removed;
                _sliceRecorders[slice] = state;
            }
            return state;
        }
    }

    private void DropStateLocked(Slice slice)
    {
        if (!_sliceRecorders.TryGetValue(slice, out var state)) return;
        state.Retired = true;
        state.Recording = state.Playing = false;
        state.CapWarning?.Dispose();
        state.CapWarning = null;
        if (state.Status != null) slice.PropertyChanged -= state.Status;
        state.Status = null;
        if (state.Removed != null) state.Radio.SliceRemoved -= state.Removed;
        if (state.RadioStatus != null) state.Radio.PropertyChanged -= state.RadioStatus;
        state.RadioStatus = null;
        state.Intent = null;
        ReleasePlaybackVox(state);
        state.Removed = null;
        state.Owner = null;
        _sliceRecorders.Remove(slice);
    }

    private void OnRecorderSliceStatus(SliceRecorderState state, string property)
    {
        // The doors poll the queries above (Home at its 100 ms poll, the
        // Workshop on its meter tick, the menu when it opens), so there is no
        // change event to raise; a hook nobody wires is a defect class here.
        lock (_sliceRecorders)
        {
            // The radio finished the buffer on its own, or something else
            // stopped it: our state follows the radio's.
            if (state.Retired) return;
            if (property == nameof(Slice.RecordOn) && state.Slice.RecordOn && !state.Recording)
            {
                state.RecordedSeconds = null;
                state.RecordStarted = null;
                state.Recording = true;
            }
            if (property == nameof(Slice.PlayOn))
            {
                state.Playing = state.Slice.PlayOn;
                if (!state.Playing)
                {
                    state.Intent = null;
                    ReleasePlaybackVox(state);
                }
            }
            if (property == nameof(Slice.RecordOn) && !state.Slice.RecordOn && state.Recording)
            {
                NoteRecording(state, false);
                state.CapWarning?.Dispose();
                state.CapWarning = null;
            }
        }
    }

    private async Task<bool> SliceRecorderCommandAsync(SliceRecorderState state, string property, bool enabled)
    {
        var slice = state.Slice;
        string command = $"slice set {slice.Index} {property}={(enabled ? 1 : 0)}";
        var radio = state.Radio;
        var connection = state.Connection;
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var status = new RadioStatusTap(radio);
        bool sentCommand = false;
        bool? reported = null;
        status.Status += raw =>
        {
            // The trailing space prevents slice 10 from confirming slice 1.
            if (!raw.Contains($"|slice {slice.Index} ", StringComparison.Ordinal)) return;
            var values = RadioStatusTap.Values(raw);
            if (!values.TryGetValue(property, out string value)) return;
            if (value != "0" && value != "1" && !(property == "play" && value == "disabled")) return;
            lock (_sliceRecorders)
            {
                if (!sentCommand || !StateIsLive(state)) return;
                reported = value == "1";
                if (property == "record") NoteRecording(state, reported.Value);
                else state.Playing = reported.Value;
                statusSeen.TrySetResult(true);
            }
        };
        await IqQueue(() =>
        {
            if (!StateIsLive(state) || !ReferenceEquals(radio, theRadio) || !ReferenceEquals(connection, radio.CurrentCommandConnection) ||
                (property == "play" && enabled && (!PlaybackIntentIsCurrent(state.Intent) || !slice.PlayEnabled)))
                throw new InvalidOperationException("slice-recorder-connection-or-state-changed");
            lock (_sliceRecorders)
            {
                sentCommand = true;
                if (property == "record" && enabled)
                {
                    state.RecordedSeconds = null;
                    state.RecordStarted = SliceRecorderClock();
                }
            }
            int sent = radio.SendReplyCommand((sequence, code, text) =>
            {
                Tracing.TraceLine($"Slice recorder reply: sequence={sequence} code={code:X8} text={text} command={command}", TraceLevel.Info);
                reply.TrySetResult(code == 0);
            }, command);
            Tracing.TraceLine($"Slice recorder sent: sequence={sent} command={command}", TraceLevel.Info);
        }).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(SliceRecorderConfirmationWait);
            if (!await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(false)) return false;
            await statusSeen.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            lock (_sliceRecorders) return StateIsLive(state) && reported == enabled;
        }
        catch (OperationCanceledException)
        {
            Tracing.TraceLine($"Slice recorder reply/status timeout: {command}", TraceLevel.Warning);
            return false;
        }
    }

    private async Task<bool> SendSafelyAsync(SliceRecorderState state, string property, bool enabled)
    {
        try { return await SliceRecorderCommandAsync(state, property, enabled).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Tracing.TraceLine($"Slice recorder {property}={(enabled ? 1 : 0)}: {ex.Message}", TraceLevel.Warning);
            return false;
        }
    }

    // ── The two-minute warning ──────────────────────────────────────────

    private async Task<bool> StopAndReportAsync(SliceRecorderState state, string property)
    {
        bool stopped = await SendSafelyAsync(state, property, false).ConfigureAwait(false);
        if (!stopped && !state.Retired)
        {
            string letter = LetterOf(state.Slice);
            RecorderStopFailureOutput(Outcome(false, property == "record" ? "audio.recorder.record_stop_unconfirmed" :
                "audio.recorder.play_stop_unconfirmed", letter));
        }
        return stopped;
    }

    private async Task<RecorderOutcome> WithRecorderAsync(SliceRecorderTarget target,
        Func<SliceRecorderState, Task<RecorderOutcome>> action)
    {
        var slice = ResolveSliceRecorderTarget(target);
        if (!SliceRecorderTargetIsOurs(slice)) return Unreachable(target, slice);
        var state = StateFor(slice);
        await state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!StateIsLive(state)) return Unreachable(target, null);
            return await action(state).ConfigureAwait(false);
        }
        finally { state.Gate.Release(); }
    }

    private void ArmCapWarning(SliceRecorderState state)
    {
        state.CapWarning?.Dispose();
        string letter = LetterOf(state.Slice);
        state.CapWarning = new Timer(_ =>
        {
            lock (_sliceRecorders)
            {
                if (!state.Recording || !_sliceRecorders.TryGetValue(state.Slice, out var current) || !ReferenceEquals(current, state)) return;
            }
            // A recording on a radio that has since disconnected is not
            // ten seconds from anything this operator can act on.
            if (!StateIsLive(state)) return;
            Tracing.TraceLine($"Slice recorder: slice {letter} is ten seconds from the {SliceRecordBufferSeconds}-second buffer cap", TraceLevel.Info);
            // DRAFT PROSE (#787): awaiting Noel
            ScreenReaderOutput.Speak(Say("audio.recorder.nearly_full", letter), Speech.SpeechIntent.Queue,
                VerbosityLevel.Terse, subject: Speech.SpeechSubject.SliceRecorder(letter));
            try { SliceRecordingNearlyFull?.Invoke(letter); }
            catch (Exception ex) { Tracing.TraceLine("SliceRecordingNearlyFull handler: " + ex, TraceLevel.Warning); }
        }, null, SliceRecordWarningAfter, Timeout.InfiniteTimeSpan);
    }

    // ── Keying stops playback ───────────────────────────────────────────

    private void EnsureKeyedWatch()
    {
        if (_sliceRecorderKeyed != null) return;
        _sliceRecorderKeyed = (_, keyed) =>
        {
            CancelSlicePlayback();
        };
        TransmitChange += _sliceRecorderKeyed;
    }

    private async Task StopPlayingForTransmitAsync(SliceRecorderState state, long generation)
    {
        await state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A delayed stop belongs to the invalidated press, not to a new
            // press admitted after that edge or safety cut.
            if (state.Intent?.Generation >= generation) return;
            if (!state.Playing && !state.Slice.PlayOn) { state.Intent = null; ReleasePlaybackVox(state); return; }
            Tracing.TraceLine($"Slice recorder: playback intent ended, stopping slice {LetterOf(state.Slice)}", TraceLevel.Info);
            if (await StopAndReportAsync(state, "play").ConfigureAwait(false))
            {
                state.Playing = false;
                state.Intent = null;
                ReleasePlaybackVox(state);
            }
        }
        finally { state.Gate.Release(); }
    }

    // ── Record ──────────────────────────────────────────────────────────

    /// <summary>Start recording on the target slice, or say why not.</summary>
    public Task<RecorderOutcome> StartSliceRecordAsync(SliceRecorderTarget target, object owner = null) =>
        WithRecorderAsync(target, state => StartRecordLockedAsync(state, target, owner));

    /// <summary>Stop recording on the target slice, whoever started it.</summary>
    public Task<RecorderOutcome> StopSliceRecordAsync(SliceRecorderTarget target, object owner = null) =>
        WithRecorderAsync(target, state => StopRecordLockedAsync(state, target, owner));

    /// <summary>Record on the target slice, or stop the recording it is making.</summary>
    public Task<RecorderOutcome> ToggleSliceRecordAsync(SliceRecorderTarget target, object owner = null) =>
        WithRecorderAsync(target, state => (state.Recording || state.Slice.RecordOn) ? StopRecordLockedAsync(state, target, owner) : StartRecordLockedAsync(state, target, owner));

    private RecorderOutcome Unreachable(SliceRecorderTarget target, Slice slice)
    {
        if (theRadio == null || !theRadio.Connected)
            return new(false, Lexicon.Get("audio.no_radio_connected"), Speech.SpeechSubject.SliceRecorder(""));
        if (target.NamedLetter != '\0' && slice == null)
            return Outcome(false, "audio.recorder.not_your_slice", target.NamedLetter.ToString());
        if (slice != null && !SliceRecorderTargetIsOurs(slice))
            return Outcome(false, "audio.recorder.not_your_slice", LetterOf(slice));
        return new(false, Lexicon.Get("audio.recorder.no_slice"), Speech.SpeechSubject.SliceRecorder(""));
    }

    private async Task<RecorderOutcome> StartRecordLockedAsync(SliceRecorderState state, SliceRecorderTarget target, object owner = null)
    {
        var slice = state.Slice;
        if (!SliceRecorderTargetIsOurs(slice)) return Unreachable(target, slice);
        string letter = LetterOf(slice);
        if (state.Recording || slice.RecordOn)
        {
            // Never blind-toggle a recorder that is already running: a re-arm
            // could otherwise overwrite the take the operator is making.
            return Outcome(true, "audio.recorder.already_recording", letter, already: true);
        }
        if (state.Playing || slice.PlayOn)
        {
            if (!await SendSafelyAsync(state, "play", false).ConfigureAwait(false))
                return Outcome(false, "audio.recorder.play_stop_unconfirmed", letter);
            state.Playing = false;
        }
        state.Owner = owner;
        state.Recording = true;
        bool started = await SendSafelyAsync(state, "record", true).ConfigureAwait(false);
        // A lost start reply leaves the radio's state unknown, so a
        // compensating stop is sent even though the start was not confirmed.
        if (!started)
        {
            if (!await SendSafelyAsync(state, "record", false).ConfigureAwait(false))
                return Outcome(false, "audio.recorder.record_stop_unconfirmed", letter);
            state.Recording = false;
            return Outcome(false, "audio.recorder.record_unconfirmed", letter);
        }
        lock (_sliceRecorders)
            if (StateIsLive(state)) ArmCapWarning(state);
        // DRAFT PROSE (#787): awaiting Noel
        bool ears = target.IsLoopbackEars && _loopbackArranged && ReferenceEquals(slice, _lbResources?.Slice);
        return Outcome(true, ears ? "audio.recorder.recording_loopback" : "audio.recorder.recording", letter);
    }

    private async Task<RecorderOutcome> StopRecordLockedAsync(SliceRecorderState state, SliceRecorderTarget target, object owner = null)
    {
        var slice = state.Slice;
        if (!SliceRecorderTargetIsOurs(slice)) return Unreachable(target, slice);
        string letter = LetterOf(slice);
        if (!state.Recording && !slice.RecordOn)
            return Outcome(true, "audio.recorder.recording_stopped", letter, already: true);
        if (!await SendSafelyAsync(state, "record", false).ConfigureAwait(false))
            return Outcome(false, "audio.recorder.record_stop_unconfirmed", letter);
        state.Recording = false;
        state.CapWarning?.Dispose();
        state.CapWarning = null;
        return Outcome(true, "audio.recorder.recording_stopped", letter);
    }

    // ── Play ────────────────────────────────────────────────────────────

    /// <summary>Play locally or into the existing transmission, as chosen at the press. Never requests keying.</summary>
    public Task<RecorderOutcome> StartSlicePlayAsync(SliceRecorderTarget target, object owner = null)
    {
        var intent = CapturePlaybackIntent();
        return WithRecorderAsync(target, state => StartPlayLockedAsync(state, target, intent, owner));
    }

    /// <summary>Stop the target slice's playback.</summary>
    public Task<RecorderOutcome> StopSlicePlayAsync(SliceRecorderTarget target, object owner = null) =>
        WithRecorderAsync(target, state => StopPlayLockedAsync(state, target, owner));

    /// <summary>Play the target slice's recording, or stop the playback running on it.</summary>
    public Task<RecorderOutcome> ToggleSlicePlayAsync(SliceRecorderTarget target, object owner = null)
    {
        var intent = CapturePlaybackIntent();
        return WithRecorderAsync(target, state => (state.Playing || state.Slice.PlayOn) ? StopPlayLockedAsync(state, target, owner) : StartPlayLockedAsync(state, target, intent, owner));
    }

    private static void ReleasePlaybackVox(SliceRecorderState state) =>
        Interlocked.Exchange(ref state.PlaybackVoxLease, null)?.Dispose();

    private async Task<RecorderOutcome> StartPlayLockedAsync(SliceRecorderState state, SliceRecorderTarget target, PlaybackIntent intent, object owner = null)
    {
        var slice = state.Slice;
        if (!SliceRecorderTargetIsOurs(slice)) return Unreachable(target, slice);
        string letter = LetterOf(slice);
        if (!PlaybackIntentIsCurrent(intent, requireLocalVoxOff: false))
            return Outcome(false, "audio.recorder.play_intent_changed", letter);
        if (state.Playing && slice.PlayOn) return Outcome(true, "audio.recorder.playing", letter, already: true);
        VoxPauseOwner.Lease lease = null;
        try
        {
            if (!intent.OnAir)
            {
                var setting = intent.Radio.TransmitSlice?.DemodMode == "CW"
                    ? VoxSetting.CwBreakIn : VoxSetting.SimpleVox;
                bool needsOffReport = !LocalPlaybackVoxOff(intent.Radio);
                using var voxStatus = new RadioStatusTap(intent.Radio);
                var offReported = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                string field = setting == VoxSetting.CwBreakIn ? "break_in" : "vox_enable";
                voxStatus.Status += raw =>
                {
                    if (!raw.Contains("|transmit ", StringComparison.Ordinal)) return;
                    if (RadioStatusTap.Values(raw).TryGetValue(field, out string value) && value == "0")
                        offReported.TrySetResult(true);
                };
                lease = VoxPauses.Acquire(setting);
                if (lease == null) return Outcome(false, "audio.recorder.play_vox_unconfirmed", letter);
                if (needsOffReport)
                    await Task.WhenAny(offReported.Task, Task.Delay(SliceVoxOffWait)).ConfigureAwait(false);
                if (!PlaybackIntentIsCurrent(intent, requireLocalVoxOff: false))
                    return Outcome(false, "audio.recorder.play_intent_changed", letter);
                if ((needsOffReport && !offReported.Task.IsCompletedSuccessfully) ||
                    !LocalPlaybackVoxOff(intent.Radio))
                    return Outcome(false, "audio.recorder.play_vox_unconfirmed", letter);
            }
            if (state.Recording || slice.RecordOn)
            {
                // "Record that, now play it back": stop the recording first.
                if (!await SendSafelyAsync(state, "record", false).ConfigureAwait(false))
                    return Outcome(false, "audio.recorder.record_stop_unconfirmed", letter);
                state.Recording = false;
                state.CapWarning?.Dispose();
                state.CapWarning = null;
                var until = DateTime.UtcNow + SlicePlayableWait;
                while (!slice.PlayEnabled && DateTime.UtcNow < until) await Task.Delay(50).ConfigureAwait(false);
            }
            if (!slice.PlayEnabled) return Outcome(false, "audio.recorder.nothing_to_play", letter);
            if (!PlaybackIntentIsCurrent(intent)) return Outcome(false, "audio.recorder.play_intent_changed", letter);
            EnsureKeyedWatch();
            state.Owner = owner;
            state.Intent = intent;
            state.Playing = true;
            state.PlaybackVoxLease = lease;
            bool started = await SendSafelyAsync(state, "play", true).ConfigureAwait(false);
            if (!started)
            {
                if (!await SendSafelyAsync(state, "play", false).ConfigureAwait(false))
                    return Outcome(false, "audio.recorder.play_stop_unconfirmed", letter);
                state.Playing = false;
                ReleasePlaybackVox(state);
                return Outcome(false, "audio.recorder.play_unconfirmed", letter);
            }
            if (!PlaybackIntentIsCurrent(intent))
            {
                // The transmission or permission changed while the command was in flight.
                if (!await SendSafelyAsync(state, "play", false).ConfigureAwait(false))
                    return Outcome(false, "audio.recorder.play_stop_unconfirmed", letter);
                state.Playing = false;
                ReleasePlaybackVox(state);
                return Outcome(false, "audio.recorder.play_intent_changed", letter);
            }
            return Outcome(true, "audio.recorder.playing", letter);
        }
        finally
        {
            // A confirmed start belongs to the slice state until the radio
            // reports play off. Every earlier exit gives the lease back here.
            if (!ReferenceEquals(state.PlaybackVoxLease, lease)) lease?.Dispose();
        }
    }

    private async Task<RecorderOutcome> StopPlayLockedAsync(SliceRecorderState state, SliceRecorderTarget target, object owner = null)
    {
        var slice = state.Slice;
        if (!SliceRecorderTargetIsOurs(slice)) return Unreachable(target, slice);
        string letter = LetterOf(slice);
        if (!state.Playing && !slice.PlayOn)
        {
            ReleasePlaybackVox(state);
            return Outcome(true, "audio.recorder.playing_stopped", letter, already: true);
        }
        if (!await SendSafelyAsync(state, "play", false).ConfigureAwait(false))
            return Outcome(false, "audio.recorder.play_stop_unconfirmed", letter);
        state.Playing = false;
        ReleasePlaybackVox(state);
        return Outcome(true, "audio.recorder.playing_stopped", letter);
    }

    /// <summary>
    /// Stop everything this target slice's recorder is doing — playback and
    /// recording. Used when a door is closing over a slice that is about to
    /// go away (the Loopback Check's listening slice).
    /// </summary>
    public async Task<bool> StopSliceRecorderAsync(SliceRecorderTarget target)
    {
        if (!SliceRecorderTargetIsOurs(ResolveSliceRecorderTarget(target))) return true;
        var outcome = await WithRecorderAsync(target, async state =>
        {
            bool ok = true;
            if (state.Playing || state.Slice.PlayOn) ok &= (await StopPlayLockedAsync(state, target).ConfigureAwait(false)).Success;
            if (state.Recording || state.Slice.RecordOn) ok &= (await StopRecordLockedAsync(state, target).ConfigureAwait(false)).Success;
            return new RecorderOutcome(ok, "", "");
        }).ConfigureAwait(false);
        return outcome.Success;
    }

    /// <summary>
    /// Stop whatever the given door started — playback and recording, on any
    /// slice — and leave everything else alone. The Audio Workshop calls this
    /// when it closes or its radio changes, so a take does not outlive the
    /// window that started it, while a recording made from Home carries on.
    /// </summary>
    public async Task<bool> StopSliceRecorderOwnedByAsync(object owner)
    {
        if (owner == null) return true;
        List<SliceRecorderState> owned;
        lock (_sliceRecorders) owned = _sliceRecorders.Values.Where(v => ReferenceEquals(v.Owner, owner)).ToList();
        var results = await Task.WhenAll(owned.Select(StopOwnedAsync)).ConfigureAwait(false);
        return results.All(ok => ok);

        async Task<bool> StopOwnedAsync(SliceRecorderState state)
        {
            await state.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (state.Retired || !ReferenceEquals(state.Owner, owner)) return true;
                bool ok = true;
                if (state.Playing) ok &= await StopAndReportAsync(state, "play").ConfigureAwait(false);
                if (state.Recording) ok &= await StopAndReportAsync(state, "record").ConfigureAwait(false);
                if (ok)
                {
                    state.CapWarning?.Dispose();
                    state.CapWarning = null;
                    state.Owner = null;
                }
                return ok;
            }
            finally { state.Gate.Release(); }
        }
    }
}
