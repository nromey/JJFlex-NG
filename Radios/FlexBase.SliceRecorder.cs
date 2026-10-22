using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;

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
    private enum Kind { Active, LoopbackEars, Letter }

    private readonly Kind _kind;
    private readonly char _letter;

    private SliceRecorderTarget(Kind kind, char letter) { _kind = kind; _letter = letter; }

    /// <summary>Whatever slice is active when the command runs.</summary>
    public static SliceRecorderTarget ActiveSlice => new(Kind.Active, '\0');

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
/// The radio's own per-slice recorder (#787): ONE reply-checked surface that
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
/// <b>Facts this rests on.</b> Verified telemetry on the 8600 (2026-08-07
/// live): the buffer caps at 120 seconds and behaves ring-like at the cap —
/// the most recent audio is kept — and two takes can coexist. The record tap
/// sits upstream of the transmit audio mute, so it captures demodulated audio
/// even with full duplex off. On Don's 6300 over SmartLink (2026-10-21):
/// recordings are kept PER SLICE (a recording on slice A is not offered on
/// slice B); the recorder captures transmit audio after the transmit filter
/// with the monitor off; playback replaces the slice's live audio rather than
/// mixing with it. The radio offers no way to download a recording.
/// </para>
/// <para>
/// <b>Per-slice state.</b> Each slice has its own recorder in the radio, so
/// each slice has its own state here: recording slice B does not stop slice A
/// playing. On ONE slice, record and play are exclusive — starting either
/// stops the other first, which is what an operator means by "record that,
/// now play it back".
/// </para>
/// <para>
/// <b>Play never keys the radio, and Play is refused while transmitting</b>
/// (Noel, 2026-10-21 10:57: keyed, Play sending to air is phase 2). A
/// playback that is running when a transmission starts is stopped.
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
    /// <summary>The radio's record buffer, in seconds (verified live, 2026-08-07).</summary>
    public const int SliceRecordBufferSeconds = 120;

    /// <summary>
    /// How long into a recording the "nearly full" warning speaks: ten seconds
    /// before the cap, the same moment the Audio Check has always warned at.
    /// Settable for tests only.
    /// </summary>
    internal TimeSpan SliceRecordWarningAfter { get; set; } = TimeSpan.FromSeconds(SliceRecordBufferSeconds - 10);

    /// <summary>How long Play waits for the radio to report a recording after stopping one. Settable for tests.</summary>
    internal TimeSpan SlicePlayableWait { get; set; } = TimeSpan.FromSeconds(2);

    private sealed class SliceRecorderState
    {
        internal Slice Slice;
        internal Radio Radio;
        internal CommandConnection Connection;
        internal bool Recording, Playing;
        // Which door started what this slice is doing, or null for a door
        // that does not need to clean up after itself (Home, the menus, the
        // keys). The Audio Workshop tags its buttons' work so that closing the
        // Workshop stops what the Workshop started and nothing else.
        internal object Owner;
        internal Timer CapWarning;
        internal PropertyChangedEventHandler Status;
    }

    private readonly SemaphoreSlim _sliceRecorderGate = new(1, 1);
    private readonly Dictionary<Slice, SliceRecorderState> _sliceRecorders = new(ReferenceEqualityComparer.Instance);
    private TransmitChangeDel _sliceRecorderKeyed;

    /// <summary>
    /// Raised after any slice recorder state changes — a start, a stop, a
    /// playback the radio finished on its own. Doors refresh their labels on
    /// it. May arrive on any thread.
    /// </summary>
    public event Action SliceRecorderChanged;

    /// <summary>Raised with the slice letter when a recording is ten seconds from the buffer's cap.</summary>
    public event Action<string> SliceRecordingNearlyFull;

    private void RaiseSliceRecorderChanged()
    {
        try { SliceRecorderChanged?.Invoke(); }
        catch (Exception ex) { Tracing.TraceLine("SliceRecorderChanged handler: " + ex, TraceLevel.Warning); }
    }

    // ── Resolving a target ──────────────────────────────────────────────

    private Slice ResolveSliceRecorderTarget(SliceRecorderTarget target)
    {
        var radio = theRadio;
        if (radio == null) return null;
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
        if (slice == null) return false;
        lock (_sliceRecorders)
            if (_sliceRecorders.TryGetValue(slice, out var state) && StateIsLive(state)) return state.Recording || slice.RecordOn;
        return slice.RecordOn;
    }

    /// <summary>True while the target slice is playing its recording.</summary>
    public bool IsSlicePlaying(SliceRecorderTarget target)
    {
        var slice = ResolveSliceRecorderTarget(target);
        if (slice == null) return false;
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

    private bool StateIsLive(SliceRecorderState state) => state.Radio != null &&
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
                _sliceRecorders[slice] = state;
            }
            return state;
        }
    }

    private void DropStateLocked(Slice slice)
    {
        if (!_sliceRecorders.TryGetValue(slice, out var state)) return;
        state.CapWarning?.Dispose();
        if (state.Status != null) slice.PropertyChanged -= state.Status;
        _sliceRecorders.Remove(slice);
    }

    private void OnRecorderSliceStatus(SliceRecorderState state, string property)
    {
        bool changed = false;
        lock (_sliceRecorders)
        {
            // The radio finished the buffer on its own, or something else
            // stopped it: our state follows the radio's.
            if (property == nameof(Slice.PlayOn) && !state.Slice.PlayOn && state.Playing) { state.Playing = false; changed = true; }
            if (property == nameof(Slice.RecordOn) && !state.Slice.RecordOn && state.Recording)
            {
                state.Recording = false;
                state.CapWarning?.Dispose();
                state.CapWarning = null;
                changed = true;
            }
            if (property == nameof(Slice.PlayEnabled) || property == nameof(Slice.RecordOn) || property == nameof(Slice.PlayOn))
                changed = true;
        }
        if (changed) RaiseSliceRecorderChanged();
    }

    private async Task<bool> SliceRecorderCommandAsync(SliceRecorderState state, string property, bool enabled)
    {
        var slice = state.Slice;
        string command = $"slice set {slice.Index} {property}={(enabled ? 1 : 0)}";
        var radio = state.Radio;
        var connection = state.Connection;
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await IqQueue(() =>
        {
            if (!StateIsLive(state) || !ReferenceEquals(radio, theRadio) || !ReferenceEquals(connection, radio.CurrentCommandConnection) ||
                !radio.SliceList.Contains(slice) || !mySlices.Contains(slice) ||
                (property == "play" && enabled && (Transmit || radio.Mox || !slice.PlayEnabled)))
                throw new InvalidOperationException("slice-recorder-connection-or-state-changed");
            int sent = radio.SendReplyCommand((sequence, code, text) =>
            {
                Tracing.TraceLine($"Slice recorder reply: sequence={sequence} code={code:X8} text={text} command={command}", TraceLevel.Info);
                reply.TrySetResult(code == 0);
            }, command);
            Tracing.TraceLine($"Slice recorder sent: sequence={sent} command={command}", TraceLevel.Info);
        }).ConfigureAwait(false);
        try { return await reply.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Tracing.TraceLine($"Slice recorder reply timeout: {command}", TraceLevel.Warning);
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
            if (!keyed) return;
            List<SliceRecorderState> playing;
            lock (_sliceRecorders) playing = _sliceRecorders.Values.Where(v => v.Playing).ToList();
            foreach (var state in playing) _ = StopPlayingForTransmitAsync(state);
        };
        TransmitChange += _sliceRecorderKeyed;
    }

    private async Task StopPlayingForTransmitAsync(SliceRecorderState state)
    {
        await _sliceRecorderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!state.Playing) return;
            Tracing.TraceLine($"Slice recorder: transmit began, stopping playback on slice {LetterOf(state.Slice)}", TraceLevel.Info);
            if (await SendSafelyAsync(state, "play", false).ConfigureAwait(false)) state.Playing = false;
        }
        finally { _sliceRecorderGate.Release(); RaiseSliceRecorderChanged(); }
    }

    // ── Record ──────────────────────────────────────────────────────────

    /// <summary>Start recording on the target slice, or say why not.</summary>
    public async Task<RecorderOutcome> StartSliceRecordAsync(SliceRecorderTarget target, object owner = null)
    {
        await _sliceRecorderGate.WaitAsync().ConfigureAwait(false);
        try { return await StartRecordLockedAsync(target, owner).ConfigureAwait(false); }
        finally { _sliceRecorderGate.Release(); RaiseSliceRecorderChanged(); }
    }

    /// <summary>Stop recording on the target slice, whoever started it.</summary>
    public async Task<RecorderOutcome> StopSliceRecordAsync(SliceRecorderTarget target, object owner = null)
    {
        await _sliceRecorderGate.WaitAsync().ConfigureAwait(false);
        try { return await StopRecordLockedAsync(target, owner).ConfigureAwait(false); }
        finally { _sliceRecorderGate.Release(); RaiseSliceRecorderChanged(); }
    }

    /// <summary>Record on the target slice, or stop the recording it is making.</summary>
    public async Task<RecorderOutcome> ToggleSliceRecordAsync(SliceRecorderTarget target, object owner = null)
    {
        await _sliceRecorderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return IsSliceRecording(target)
                ? await StopRecordLockedAsync(target, owner).ConfigureAwait(false)
                : await StartRecordLockedAsync(target, owner).ConfigureAwait(false);
        }
        finally { _sliceRecorderGate.Release(); RaiseSliceRecorderChanged(); }
    }

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

    private async Task<RecorderOutcome> StartRecordLockedAsync(SliceRecorderTarget target, object owner = null)
    {
        var slice = ResolveSliceRecorderTarget(target);
        if (!SliceRecorderTargetIsOurs(slice)) return Unreachable(target, slice);
        string letter = LetterOf(slice);
        var state = StateFor(slice);
        if (state.Recording || slice.RecordOn)
        {
            // Never blind-toggle a recorder that is already running: a re-arm
            // race once nearly wiped an operator's takes (2026-08-07).
            return Outcome(true, "audio.recorder.already_recording", letter, already: true);
        }
        if (state.Playing || slice.PlayOn)
        {
            if (!await SendSafelyAsync(state, "play", false).ConfigureAwait(false))
                return Outcome(false, "audio.recorder.play_stop_unconfirmed", letter);
            state.Playing = false;
        }
        bool started = await SendSafelyAsync(state, "record", true).ConfigureAwait(false);
        // A lost start reply leaves the radio's state unknown, so a
        // compensating stop is sent even though the start was not confirmed.
        state.Recording = true;
        if (!started)
        {
            if (await SendSafelyAsync(state, "record", false).ConfigureAwait(false)) state.Recording = false;
            return Outcome(false, "audio.recorder.record_unconfirmed", letter);
        }
        state.Owner = owner;
        ArmCapWarning(state);
        // DRAFT PROSE (#787): awaiting Noel
        bool ears = target.IsLoopbackEars && _loopbackArranged && ReferenceEquals(slice, _lbResources?.Slice);
        return Outcome(true, ears ? "audio.recorder.recording_loopback" : "audio.recorder.recording", letter);
    }

    private async Task<RecorderOutcome> StopRecordLockedAsync(SliceRecorderTarget target, object owner = null)
    {
        var slice = ResolveSliceRecorderTarget(target);
        if (!SliceRecorderTargetIsOurs(slice)) return Unreachable(target, slice);
        string letter = LetterOf(slice);
        var state = StateFor(slice);
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

    /// <summary>Play the target slice's recording to the speakers, or say why not. Never keys the radio.</summary>
    public async Task<RecorderOutcome> StartSlicePlayAsync(SliceRecorderTarget target, object owner = null)
    {
        await _sliceRecorderGate.WaitAsync().ConfigureAwait(false);
        try { return await StartPlayLockedAsync(target, owner).ConfigureAwait(false); }
        finally { _sliceRecorderGate.Release(); RaiseSliceRecorderChanged(); }
    }

    /// <summary>Stop the target slice's playback.</summary>
    public async Task<RecorderOutcome> StopSlicePlayAsync(SliceRecorderTarget target, object owner = null)
    {
        await _sliceRecorderGate.WaitAsync().ConfigureAwait(false);
        try { return await StopPlayLockedAsync(target, owner).ConfigureAwait(false); }
        finally { _sliceRecorderGate.Release(); RaiseSliceRecorderChanged(); }
    }

    /// <summary>Play the target slice's recording, or stop the playback running on it.</summary>
    public async Task<RecorderOutcome> ToggleSlicePlayAsync(SliceRecorderTarget target, object owner = null)
    {
        await _sliceRecorderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return IsSlicePlaying(target)
                ? await StopPlayLockedAsync(target, owner).ConfigureAwait(false)
                : await StartPlayLockedAsync(target, owner).ConfigureAwait(false);
        }
        finally { _sliceRecorderGate.Release(); RaiseSliceRecorderChanged(); }
    }

    private async Task<RecorderOutcome> StartPlayLockedAsync(SliceRecorderTarget target, object owner = null)
    {
        var slice = ResolveSliceRecorderTarget(target);
        if (!SliceRecorderTargetIsOurs(slice)) return Unreachable(target, slice);
        string letter = LetterOf(slice);
        // DRAFT PROSE (#787): awaiting Noel. Phase 2 sends a recording to air
        // while keyed; until it is built, Play is refused while transmitting.
        if (Transmit || theRadio.Mox) return Outcome(false, "audio.recorder.play_refused_transmitting", letter);
        var state = StateFor(slice);
        if (state.Playing && slice.PlayOn) return Outcome(true, "audio.recorder.playing", letter, already: true);
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
        if (Transmit || theRadio.Mox) return Outcome(false, "audio.recorder.play_refused_transmitting", letter);
        EnsureKeyedWatch();
        bool started = await SendSafelyAsync(state, "play", true).ConfigureAwait(false);
        state.Playing = true;
        if (!started)
        {
            if (await SendSafelyAsync(state, "play", false).ConfigureAwait(false)) state.Playing = false;
            return Outcome(false, "audio.recorder.play_unconfirmed", letter);
        }
        state.Owner = owner;
        if (Transmit || theRadio.Mox)
        {
            // Keyed while the play command was in flight.
            if (await SendSafelyAsync(state, "play", false).ConfigureAwait(false)) state.Playing = false;
            return Outcome(false, "audio.recorder.play_stopped_by_transmit", letter);
        }
        return Outcome(true, "audio.recorder.playing", letter);
    }

    private async Task<RecorderOutcome> StopPlayLockedAsync(SliceRecorderTarget target, object owner = null)
    {
        var slice = ResolveSliceRecorderTarget(target);
        if (!SliceRecorderTargetIsOurs(slice)) return Unreachable(target, slice);
        string letter = LetterOf(slice);
        var state = StateFor(slice);
        if (!state.Playing && !slice.PlayOn)
            return Outcome(true, "audio.recorder.playing_stopped", letter, already: true);
        if (!await SendSafelyAsync(state, "play", false).ConfigureAwait(false))
            return Outcome(false, "audio.recorder.play_stop_unconfirmed", letter);
        state.Playing = false;
        return Outcome(true, "audio.recorder.playing_stopped", letter);
    }

    /// <summary>
    /// Stop everything this target slice's recorder is doing — playback and
    /// recording. Used when a door is closing over a slice that is about to
    /// go away (the Loopback Check's listening slice).
    /// </summary>
    public async Task<bool> StopSliceRecorderAsync(SliceRecorderTarget target)
    {
        await _sliceRecorderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var slice = ResolveSliceRecorderTarget(target);
            if (!SliceRecorderTargetIsOurs(slice)) return true;
            bool ok = true;
            if (IsSlicePlaying(target)) ok &= (await StopPlayLockedAsync(target).ConfigureAwait(false)).Success;
            if (IsSliceRecording(target)) ok &= (await StopRecordLockedAsync(target).ConfigureAwait(false)).Success;
            return ok;
        }
        finally { _sliceRecorderGate.Release(); RaiseSliceRecorderChanged(); }
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
        await _sliceRecorderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<SliceRecorderState> owned;
            lock (_sliceRecorders) owned = _sliceRecorders.Values.Where(v => ReferenceEquals(v.Owner, owner)).ToList();
            bool ok = true;
            foreach (var state in owned)
            {
                if (!StateIsLive(state)) continue;
                if (state.Playing && await SendSafelyAsync(state, "play", false).ConfigureAwait(false)) state.Playing = false;
                if (state.Recording && await SendSafelyAsync(state, "record", false).ConfigureAwait(false))
                {
                    state.Recording = false;
                    state.CapWarning?.Dispose();
                    state.CapWarning = null;
                }
                bool clean = !state.Playing && !state.Recording;
                ok &= clean;
                if (clean) state.Owner = null;
            }
            return ok;
        }
        finally { _sliceRecorderGate.Release(); RaiseSliceRecorderChanged(); }
    }
}
