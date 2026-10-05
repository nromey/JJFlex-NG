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

        /// <summary>Release the recording and give the microphone back.</summary>
        void FileStop();
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
        public void FileStop() => _rig.TxFileStop();
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

        private readonly IVoiceMessageRig _rig;
        private readonly long _startedMs;
        private bool _sawTransmit;

        /// <summary>The slot's label, for the sentences about this send.</summary>
        public string Label { get; }

        /// <summary>True when this send asked the radio to key, so it must unkey.</summary>
        public bool WeKeyed { get; }

        /// <summary>True once the send has ended, however it ended.</summary>
        public bool Finished => Outcome != VoiceMessageOutcome.None;

        /// <summary>How it ended; <see cref="VoiceMessageOutcome.None"/> while going.</summary>
        public VoiceMessageOutcome Outcome { get; private set; } = VoiceMessageOutcome.None;

        private VoiceMessageSend(IVoiceMessageRig rig, string label, long nowMs, bool weKeyed)
        {
            _rig = rig;
            Label = label;
            _startedMs = nowMs;
            WeKeyed = weKeyed;
            _sawTransmit = !weKeyed;   // already transmitting: nothing to wait for
        }

        /// <summary>
        /// Start sending. The recording must already be loaded into the rig's
        /// player. Engages the recording first, then keys if the radio is not
        /// already transmitting — in that order, so that when the radio keys,
        /// the thing in place of the microphone is already the recording and
        /// not a few frames of room noise.
        /// </summary>
        public static VoiceMessageSend Begin(IVoiceMessageRig rig, string label, long nowMs)
        {
            if (rig == null) throw new ArgumentNullException(nameof(rig));
            bool already = rig.Transmit;
            rig.FileStart();
            if (!already) rig.Transmit = true;
            return new VoiceMessageSend(rig, label ?? "", nowMs, weKeyed: !already);
        }

        /// <summary>Advance the send. Call often; 50 ms is plenty.</summary>
        public void Tick(long nowMs)
        {
            if (Finished) return;

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
            if (WeKeyed && outcome != VoiceMessageOutcome.Interrupted)
                _rig.Transmit = false;
        }
    }
}
