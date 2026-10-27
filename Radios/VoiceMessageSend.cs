#nullable enable
using System;

namespace Radios
{
    /// <summary>
    /// The handful of things a voice-message send needs from the radio,
    /// behind an interface so the send's state machine can be driven by a
    /// test with no radio present.
    /// </summary>
    public interface IVoiceMessageRig
    {
        /// <summary>The radio's own report of whether it is transmitting;
        /// setting it asks the radio to key or unkey.</summary>
        bool Transmit { get; set; }

        /// <summary>True while the loaded recording is replacing the microphone.</summary>
        bool FilePlaying { get; }

        /// <summary>True once the recording has played to its end.</summary>
        bool FileReachedEnd { get; }

        /// <summary>How long the loaded recording is.</summary>
        double FileSeconds { get; }

        /// <summary>Engage the recording in place of the microphone.</summary>
        void FileStart();

        /// <summary>
        /// Engage the recording AND let it reach the radio while receiving, so
        /// the radio's VOX can key on it. The deliberate-send half of the
        /// discriminator #151 names; <see cref="FileStart"/> is the armed half.
        /// </summary>
        void FileStartForVox();

        /// <summary>Release the recording and give the microphone back.</summary>
        void FileStop();

        /// <summary>
        /// True when the radio's VOX is on for the transmit slice's mode. The
        /// rig-side source is mode-dependent (<c>SimpleVOXEnable</c> in voice
        /// modes, <c>CWBreakIn</c> in CW); a voice send is refused in CW
        /// before it gets here, so this is the voice-mode answer in practice.
        /// </summary>
        bool VoxOn { get; }
    }

    /// <summary>The <see cref="IVoiceMessageRig"/> over a live <see cref="FlexBase"/>.</summary>
    public sealed class FlexBaseVoiceMessageRig : IVoiceMessageRig
    {
        private readonly FlexBase _rig;
        public FlexBaseVoiceMessageRig(FlexBase rig) { _rig = rig ?? throw new ArgumentNullException(nameof(rig)); }
        public bool Transmit { get => _rig.Transmit; set => _rig.Transmit = value; }
        public bool FilePlaying => _rig.TxFilePlaying;
        public bool FileReachedEnd => _rig.TxFilePlayer.ReachedEnd;
        public double FileSeconds => _rig.TxFilePlayer.ContentSeconds;
        public void FileStart() => _rig.TxFileStart();
        public void FileStartForVox() => _rig.TxFileStartForVox();
        public void FileStop() => _rig.TxFileStop();
        public bool VoxOn => _rig.Vox == FlexBase.OffOnValues.on;
    }

    /// <summary>How a voice-message send ended.</summary>
    public enum VoiceMessageOutcome
    {
        /// <summary>Still going.</summary>
        None,
        /// <summary>Played to the end; the radio was unkeyed if this send keyed it.</summary>
        Sent,
        /// <summary>The operator stopped it — the same key again, another message key, or Escape.</summary>
        Stopped,
        /// <summary>This send asked the radio to key and it never reported transmitting.</summary>
        RadioDidNotKey,
        /// <summary>Something else unkeyed the radio while the recording was going out.</summary>
        Interrupted,
        /// <summary>The recording did not report its end within its own length plus a grace period.</summary>
        RanLong,
        /// <summary>This send let VOX key the radio and the radio never reported transmitting.</summary>
        VoxDidNotKey,
        /// <summary>
        /// A box send (<see cref="KeyedSendOptions"/>): the operator keyed the
        /// radio themselves while it was going out — push to talk or the lock —
        /// so the recording stopped, the microphone came back, and the carrier
        /// and its unkey are the operator's now.
        /// </summary>
        TakenOver,
        /// <summary>
        /// A box send: the recording never started, or stopped under the send
        /// before its end while the radio was still keyed. The send unkeyed,
        /// because it keyed.
        /// </summary>
        SourceStopped,
    }

    /// <summary>
    /// What a box send needs beyond the rig (#835, Don's box): who else might
    /// take the transmitter, and whether the recording can start before the
    /// radio is keyed.
    /// </summary>
    /// <remarks>
    /// Every member is optional, so a test can drive the state machine with
    /// none of them. Each is a question the send asks on its tick; nothing here
    /// is called from the audio thread.
    /// </remarks>
    public sealed class KeyedSendOptions
    {
        /// <summary>
        /// True once the operator has keyed the radio themselves — push to talk
        /// or the transmit lock. Read on every tick of a box send. Noel,
        /// 2026-10-27 15:26: <i>"yep ptt takes over"</i>.
        /// </summary>
        public Func<bool>? OperatorKeyed { get; init; }

        /// <summary>
        /// The recording can only start once the radio is transmitting: the
        /// radio's own slice playback, which plays to the speakers when started
        /// unkeyed and to the air when started keyed
        /// (<c>FlexBase.StartSlicePlayAsync</c> fixes that choice at the press).
        /// A file in our own player does not need this: it waits, parked, for
        /// the key-down.
        /// </summary>
        public bool StartSourceWhenKeyed { get; init; }

        /// <summary>
        /// True when a recording started with <see cref="StartSourceWhenKeyed"/>
        /// has failed to start. Lets the send unkey at once rather than waiting
        /// out <see cref="VoiceMessageSend.SourceStartWaitMs"/>.
        /// </summary>
        public Func<bool>? SourceFailed { get; init; }

        /// <summary>Called once, on the tick that first sees the radio transmitting.</summary>
        public Action? Keyed { get; init; }
    }

    /// <summary>
    /// One voice message going out: key the radio if nobody has, play the
    /// recording in place of the microphone, unkey when it ends, and put the
    /// microphone back on every road out (Sprint 48 Track A, #151).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the key keys the radio.</b> In CW the same key hands text to the
    /// radio's keyer and the radio keys itself; nobody holds a PTT. Noel's
    /// ruling is that <c>Ctrl+1</c> means "send my CQ" in both modes, so in
    /// a voice mode it has to key too, or the two halves of one key would
    /// need two different hands. The deciding case is Don's: during the New
    /// York QSO Party his hands are in N3FJP, where a key that only ARMS a
    /// recording and waits for a push-to-talk he cannot reach does nothing
    /// for him. This is the radio's own voice keyer's behaviour, which is the
    /// shape operators already know.
    /// </para>
    /// <para>
    /// <b>If the radio is already transmitting, this send does not touch
    /// the key.</b> An operator holding PTT, or a transmit lock, or VOX, is
    /// keying the radio themselves; the recording replaces their microphone
    /// for its length and the microphone comes back when it ends, and the
    /// radio stays keyed for as long as they keep it so.
    /// </para>
    /// <para>
    /// <b>With VOX on and the radio receiving, this send does not key either
    /// — it lets VOX do it</b> (Sprint 48 Track E; Noel, 2026-10-05: <i>"if
    /// vox is on, which is how Don likes to do it, then it could use that,
    /// otherwise it keys and unkeys"</i>). The recording is started with
    /// <see cref="IVoiceMessageRig.FileStartForVox"/>, which lets it reach the
    /// radio while receiving, and the radio's own VOX detector keys on it.
    /// Three things differ from the keyed send, all because VOX is the
    /// radio's and not ours: the send waits up to <see cref="VoxKeyWaitMs"/>
    /// for the radio to report transmitting and ends as
    /// <see cref="VoiceMessageOutcome.VoxDidNotKey"/> if it never does; a
    /// transmit that DROPS mid-recording is not an interrupt, because VOX
    /// drops between words at a short delay and keys again on the next one,
    /// so the send ends only when the recording ends, or when something
    /// releases the player under it (a kill), or when it runs long; and
    /// nothing here ever unkeys — VOX drops the carrier by itself once the
    /// recording has stopped and its delay has run.
    /// </para>
    /// <para>
    /// <b>Every exit restores the microphone.</b> Played to the end, stopped,
    /// interrupted by someone else unkeying, the radio never keying, or the
    /// recording running long — each one calls <see cref="IVoiceMessageRig.FileStop"/>,
    /// and each one that this send keyed for unkeys. The distinction matters
    /// at the interrupt: when PTT dropped under us, the operator unkeyed on
    /// purpose and we must not re-key or hold the key; when WE keyed, nobody
    /// else is going to unkey, so we must.
    /// </para>
    /// <para>
    /// <b>The run-long guard is a watchdog, not a feature.</b> The file
    /// player reports its end through <see cref="IVoiceMessageRig.FileReachedEnd"/>,
    /// which is set by the audio thread as it plays. If nothing is pacing it
    /// — the mode changed to CW mid-send, the stream died — that flag never
    /// comes, and a keyed radio with a dead source would stay keyed. So a
    /// send also ends when it has run past the recording's own length plus
    /// <see cref="RunLongGraceMs"/>, and says that it did.
    /// </para>
    /// <para>
    /// <b>No clock of its own.</b> <see cref="Tick"/> takes the time, so the
    /// UI drives it from a dispatcher timer and a test drives it from a
    /// number. Everything here is UI-thread state; the audio thread is only
    /// ever read through the rig.
    /// </para>
    /// </remarks>
    public sealed class VoiceMessageSend
    {
        /// <summary>
        /// How long to wait for the radio to report transmitting after asking
        /// it to key. The ask is queued to the radio and answered by its own
        /// status echo; one second is the same patience the Audio Workshop's
        /// injection-path arm gives a transmit-input change.
        /// </summary>
        public const int KeyUpWaitMs = 1000;

        /// <summary>Grace past the recording's length before a send is declared run long.</summary>
        public const int RunLongGraceMs = 3000;

        /// <summary>
        /// How long a VOX send waits for the radio to report transmitting
        /// before concluding VOX did not key. Longer than
        /// <see cref="KeyUpWaitMs"/> because the trigger is the recording's
        /// own level reaching the radio's detector, not a command the radio
        /// echoes: the first frames must travel the self-clock, the encoder
        /// and the network, and a recording may open with a breath of
        /// silence. Short enough that an operator whose VOX gain is too low
        /// hears the refusal well before the recording would have ended.
        /// Nobody has ruled a number; this one is a judgement and the bench
        /// may move it.
        /// </summary>
        public const int VoxKeyWaitMs = 2000;

        /// <summary>
        /// A box send whose recording starts only once the radio is keyed waits
        /// this long after the first transmitting tick before starting it, so
        /// the key-down's own transmit edge — which stops any slice playback
        /// (<c>FlexBase.EnsureKeyedWatch</c>) — has certainly been handled
        /// before the playback it would otherwise cancel is asked for.
        /// </summary>
        public const int SourceStartSettleMs = 100;

        /// <summary>
        /// How long a box send waits for a recording started on the key to
        /// report playing. The radio's slice recorder confirms by reply and
        /// status within its own four-second wait
        /// (<c>FlexBase.SliceRecorderConfirmationWait</c>); this is that and a
        /// second more, after which the send unkeys and says so.
        /// </summary>
        public const int SourceStartWaitMs = 5000;

        private readonly IVoiceMessageRig _rig;
        private readonly long _startedMs;
        private bool _sawTransmit;
        private readonly KeyedSendOptions? _box;
        private long _keyedAtMs;
        private bool _sourceStarted;
        private long _sourceStartedAtMs;
        private bool _sourceSeenPlaying;
        private long _sourcePlayingSinceMs;

        /// <summary>The slot's label, for the sentences about this send.</summary>
        public string Label { get; }

        /// <summary>True when this send asked the radio to key, so it must unkey.</summary>
        public bool WeKeyed { get; }

        /// <summary>
        /// True when this send left the keying to the radio's VOX: it neither
        /// keyed nor will unkey, it does not read a dropped transmit as an
        /// interrupt, and it ends when the recording does.
        /// </summary>
        public bool ViaVox { get; }

        /// <summary>True once the send has ended, however it ended.</summary>
        public bool Finished => Outcome != VoiceMessageOutcome.None;

        /// <summary>How it ended; <see cref="VoiceMessageOutcome.None"/> while going.</summary>
        public VoiceMessageOutcome Outcome { get; private set; } = VoiceMessageOutcome.None;

        /// <summary>
        /// True for a box send (<see cref="BeginKeyed"/>): this send keyed the
        /// radio with VOX held off by its caller, will unkey at the end, and
        /// gives the transmitter up if the operator keys it themselves.
        /// </summary>
        public bool Boxed => _box != null;

        private VoiceMessageSend(IVoiceMessageRig rig, string label, long nowMs, bool weKeyed, bool viaVox,
            KeyedSendOptions? box = null)
        {
            _rig = rig;
            Label = label;
            _startedMs = nowMs;
            WeKeyed = weKeyed;
            ViaVox = viaVox;
            _box = box;
            _sawTransmit = !weKeyed && !viaVox;   // already transmitting: nothing to wait for
            _sourceStarted = box == null || !box.StartSourceWhenKeyed;
            _sourceSeenPlaying = _sourceStarted;
            _sourcePlayingSinceMs = nowMs;
        }

        /// <summary>
        /// Start a BOX send (#835, Don's box; Noel 2026-10-27 14:54, 15:28,
        /// 15:29): the radio is keyed by this send whatever VOX would have done,
        /// the whole recording goes out, and the send unkeys at the end.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The caller holds VOX off first</b>, through the radio's one VOX
        /// owner, and confirmed by the radio, and lets it go only after the
        /// unkey and the drain (<c>FlexBase.PauseVoxForLocalPlaybackAsync</c>).
        /// This class never touches VOX: it is the same key, send, unkey as the
        /// keyed branch of <see cref="Begin"/>, with three differences that are
        /// all the box's rulings.
        /// </para>
        /// <para>
        /// <b>It keys even if the radio reads transmitting.</b> The one way that
        /// happens on a box send is the tail of VOX's own hold, which counts as
        /// unkeyed for the box (15:28); the caller has already let it drop, and
        /// an operator's push to talk or lock never reaches here.
        /// </para>
        /// <para>
        /// <b>The operator keying takes over</b> (15:26): the recording stops,
        /// the microphone comes back, and the send does NOT unkey, because the
        /// carrier is now under the operator's thumb. It is a stop, never a
        /// pause (15:28), because resuming would mean this send keying the
        /// radio again on its own when they let go.
        /// </para>
        /// <para>
        /// <b>A recording that starts only once keyed</b> (the radio's slice
        /// playback) is started here on the tick after the key is confirmed,
        /// and the send unkeys if it fails to start or stops before its end.
        /// </para>
        /// </remarks>
        public static VoiceMessageSend BeginKeyed(IVoiceMessageRig rig, string label, long nowMs,
            KeyedSendOptions options)
        {
            if (rig == null) throw new ArgumentNullException(nameof(rig));
            if (options == null) throw new ArgumentNullException(nameof(options));
            var send = new VoiceMessageSend(rig, label ?? "", nowMs, weKeyed: true, viaVox: false, box: options);
            // Engage first, then key, as the keyed branch does, so the thing in
            // place of the microphone at key-down is already the recording. A
            // source that starts on the key is not engaged here: started
            // unkeyed, the radio's playback goes to the speakers.
            if (!options.StartSourceWhenKeyed) rig.FileStart();
            rig.Transmit = true;
            return send;
        }

        /// <summary>
        /// Start sending. The recording must already be loaded into the rig's
        /// player. Engages the recording first, then keys if the radio is not
        /// already transmitting — in that order, so that when the radio keys,
        /// the thing in place of the microphone is already the recording and
        /// not a few frames of room noise. With VOX on and the radio
        /// receiving, the recording is started flowing instead and the radio
        /// keys itself (<see cref="ViaVox"/>).
        /// </summary>
        public static VoiceMessageSend Begin(IVoiceMessageRig rig, string label, long nowMs)
        {
            if (rig == null) throw new ArgumentNullException(nameof(rig));
            bool already = rig.Transmit;
            if (already)
            {
                rig.FileStart();
                return new VoiceMessageSend(rig, label ?? "", nowMs, weKeyed: false, viaVox: false);
            }
            if (rig.VoxOn)
            {
                rig.FileStartForVox();
                return new VoiceMessageSend(rig, label ?? "", nowMs, weKeyed: false, viaVox: true);
            }
            rig.FileStart();
            rig.Transmit = true;
            return new VoiceMessageSend(rig, label ?? "", nowMs, weKeyed: true, viaVox: false);
        }

        /// <summary>Advance the send. Call often; 50 ms is plenty.</summary>
        public void Tick(long nowMs)
        {
            if (Finished) return;

            if (ViaVox)
            {
                TickViaVox(nowMs);
                return;
            }

            if (_box != null)
            {
                TickBoxed(nowMs, _box);
                return;
            }

            if (!_sawTransmit)
            {
                if (_rig.Transmit)
                {
                    _sawTransmit = true;
                }
                else if (nowMs - _startedMs > KeyUpWaitMs)
                {
                    Finish(VoiceMessageOutcome.RadioDidNotKey);
                }
                return;
            }

            if (!_rig.Transmit)
            {
                // Somebody unkeyed under us: PTT released, Escape, a kill, the
                // radio's own interlock. Whatever it was, it was not this send,
                // and it has already decided the key.
                Finish(VoiceMessageOutcome.Interrupted);
                return;
            }

            if (_rig.FileReachedEnd)
            {
                Finish(VoiceMessageOutcome.Sent);
                return;
            }

            long allowedMs = (long)(_rig.FileSeconds * 1000.0) + KeyUpWaitMs + RunLongGraceMs;
            if (nowMs - _startedMs > allowedMs)
                Finish(VoiceMessageOutcome.RanLong);
        }

        /// <summary>
        /// The VOX send's tick. The radio's transmit state is read only to
        /// learn whether VOX ever keyed; after that the recording is the
        /// authority, because VOX dropping between words is VOX working.
        /// </summary>
        private void TickViaVox(long nowMs)
        {
            if (!_rig.FilePlaying && !_rig.FileReachedEnd)
            {
                // The player was released under us before the recording ended
                // — the hard kill's disarm (#699), or a load replacing the
                // content. Not this send, and nothing here holds a key.
                Finish(VoiceMessageOutcome.Interrupted);
                return;
            }

            if (!_sawTransmit)
            {
                if (_rig.Transmit)
                {
                    _sawTransmit = true;
                }
                else if (nowMs - _startedMs > VoxKeyWaitMs || _rig.FileReachedEnd)
                {
                    Finish(VoiceMessageOutcome.VoxDidNotKey);
                }
                return;
            }

            if (_rig.FileReachedEnd)
            {
                Finish(VoiceMessageOutcome.Sent);
                return;
            }

            long allowedMs = (long)(_rig.FileSeconds * 1000.0) + VoxKeyWaitMs + RunLongGraceMs;
            if (nowMs - _startedMs > allowedMs)
                Finish(VoiceMessageOutcome.RanLong);
        }

        /// <summary>
        /// The box send's tick. The order of the checks is the order of the
        /// rulings' precedence: the operator's own key first, then the radio's
        /// report, then the recording.
        /// </summary>
        private void TickBoxed(long nowMs, KeyedSendOptions box)
        {
            bool operatorKeyed;
            try { operatorKeyed = box.OperatorKeyed?.Invoke() == true; }
            catch { operatorKeyed = false; }
            if (operatorKeyed)
            {
                Finish(VoiceMessageOutcome.TakenOver);
                return;
            }

            if (!_sawTransmit)
            {
                if (_rig.Transmit)
                {
                    _sawTransmit = true;
                    _keyedAtMs = nowMs;
                    if (_sourceStarted) _sourcePlayingSinceMs = nowMs;
                    try { box.Keyed?.Invoke(); }
                    catch { /* hearing the send is a courtesy; it must never stop the send */ }
                }
                else if (nowMs - _startedMs > KeyUpWaitMs)
                {
                    Finish(VoiceMessageOutcome.RadioDidNotKey);
                }
                return;
            }

            if (!_rig.Transmit)
            {
                // Something else unkeyed: Escape is a Stop and arrives as one,
                // so this is the hard kill, a reflected-power cut, the radio's
                // own interlock or another client. It has already decided the key.
                Finish(VoiceMessageOutcome.Interrupted);
                return;
            }

            if (!_sourceStarted)
            {
                if (nowMs - _keyedAtMs >= SourceStartSettleMs)
                {
                    _sourceStarted = true;
                    _sourceStartedAtMs = nowMs;
                    _rig.FileStart();
                }
                return;
            }

            if (_rig.FileReachedEnd)
            {
                Finish(VoiceMessageOutcome.Sent);
                return;
            }

            if (!_sourceSeenPlaying)
            {
                bool failed;
                try { failed = box.SourceFailed?.Invoke() == true; }
                catch { failed = true; }
                if (_rig.FilePlaying)
                {
                    _sourceSeenPlaying = true;
                    _sourcePlayingSinceMs = nowMs;
                }
                else if (failed || nowMs - _sourceStartedAtMs > SourceStartWaitMs)
                {
                    Finish(VoiceMessageOutcome.SourceStopped);
                }
                return;
            }

            if (!_rig.FilePlaying)
            {
                // Released under us before its end with the carrier still up:
                // the kill's disarm arrives before the radio's unkey echo, or a
                // load replaced the content. We keyed, so we unkey.
                Finish(VoiceMessageOutcome.SourceStopped);
                return;
            }

            long allowedMs = (long)(_rig.FileSeconds * 1000.0) + RunLongGraceMs;
            if (nowMs - _sourcePlayingSinceMs > allowedMs)
                Finish(VoiceMessageOutcome.RanLong);
        }

        /// <summary>The operator asked for it to stop. Idempotent.</summary>
        public void Stop() => Finish(VoiceMessageOutcome.Stopped);

        private void Finish(VoiceMessageOutcome outcome)
        {
            if (Finished) return;
            Outcome = outcome;
            // The microphone comes back on every road out, unconditionally,
            // and before the key is touched: a release that waited on the
            // unkey would be a release that could be skipped by a throw.
            _rig.FileStop();
            // Interrupted: someone else already decided the key. TakenOver: the
            // operator is holding it, and dropping it under their thumb is the
            // one thing a box send must never do (Noel, 2026-10-27 15:26).
            if (WeKeyed && outcome != VoiceMessageOutcome.Interrupted
                        && outcome != VoiceMessageOutcome.TakenOver)
                _rig.Transmit = false;
        }
    }
}
