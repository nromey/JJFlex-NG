using System.Collections.Generic;

namespace Radios
{
    /// <summary>
    /// How often a PC-audio stream that died on its own may be restarted
    /// before JJ Flexible gives up and says so (#1145). One per direction
    /// (microphone, speaker) per PC-audio run. Pure: no clock, no audio, no
    /// thread; the caller passes the time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At most <see cref="MaxRecoveries"/> recoveries in any
    /// <see cref="WindowMilliseconds"/>; the next death inside the window gives
    /// up, and a given-up direction stays given up for the rest of the run.
    /// A recovery older than the window no longer counts, so a stream that
    /// hiccups once an hour is restarted every time. The numbers are the
    /// design's judgement; the bench tunes them.
    /// </para>
    /// <para>
    /// <b>Words, once.</b> A retry loop never repeats a sentence: only the
    /// first recovery after a quiet window may be spoken
    /// (<see cref="Decision.Speakable"/>), and the give-up is spoken once
    /// (<see cref="TakeGiveUpSentence"/>).
    /// </para>
    /// </remarks>
    public sealed class StreamRecoveryPolicy
    {
        public const int MaxRecoveries = 3;
        public const long WindowMilliseconds = 30_000;

        /// <summary>
        /// A restart while keyed is said only when the stream was down at
        /// least this long. Ruled by Noel 2026-11-19 10:38: "for transmitting
        /// I'd say 3 seconds." A shorter restart lost almost nothing and is
        /// traced only, as a restart while receiving always is.
        /// </summary>
        public const long SpokenRestartMinimumMilliseconds = 3_000;

        public enum Verdict
        {
            /// <summary>Restart the stream.</summary>
            Recover,
            /// <summary>Too many deaths in the window: stop trying, say so once.</summary>
            GiveUp,
            /// <summary>Already given up this run; nothing to do.</summary>
            AlreadyGivenUp,
        }

        public readonly struct Decision
        {
            public Decision(Verdict verdict, int recoveryInWindow, bool speakable)
            { Verdict = verdict; RecoveryInWindow = recoveryInWindow; Speakable = speakable; }
            public Verdict Verdict { get; }
            /// <summary>1-based number of this recovery within the window, for the trace.</summary>
            public int RecoveryInWindow { get; }
            /// <summary>True only for the first recovery after a quiet window.</summary>
            public bool Speakable { get; }
        }

        private readonly Queue<long> _recent = new Queue<long>();
        private bool _giveUpSaid;
        private long _restartSaidAtMs = long.MinValue;

        public bool GivenUp { get; private set; }
        public int TotalRecoveries { get; private set; }

        /// <summary>A stream died at <paramref name="nowMs"/>: what now?</summary>
        public Decision OnDeath(long nowMs)
        {
            if (GivenUp) return new Decision(Verdict.AlreadyGivenUp, 0, false);
            while (_recent.Count > 0 && nowMs - _recent.Peek() >= WindowMilliseconds) _recent.Dequeue();
            if (_recent.Count >= MaxRecoveries)
            {
                GivenUp = true;
                return new Decision(Verdict.GiveUp, _recent.Count, false);
            }
            bool quietBefore = _recent.Count == 0;
            _recent.Enqueue(nowMs);
            TotalRecoveries++;
            return new Decision(Verdict.Recover, _recent.Count, quietBefore);
        }

        /// <summary>A recovery failed outright: give up now, whatever the count.</summary>
        public void GiveUpNow() => GivenUp = true;

        /// <summary>True exactly once per run, when the give-up is to be spoken.</summary>
        public bool TakeGiveUpSentence()
        {
            if (!GivenUp || _giveUpSaid) return false;
            _giveUpSaid = true;
            return true;
        }

        /// <summary>
        /// Whether a successful restart is spoken, and if so, takes the
        /// sentence. A restart while the radio is not keyed is traced only
        /// (Noel, J6, 2026-11-19: "I don't need to hear every restart"). A
        /// restart while keyed is said when the stream was down at least
        /// <see cref="SpokenRestartMinimumMilliseconds"/> (Noel, 2026-11-19),
        /// and at most once in any <see cref="WindowMilliseconds"/>, so a
        /// retry loop never repeats it. Counting the sentence rather than the
        /// first death means a short, unspoken restart does not use up the
        /// one sentence a long one after it is owed.
        /// </summary>
        public bool TakeRestartSentence(Decision decision, bool keyed, long downMilliseconds, long nowMs)
        {
            if (decision.Verdict != Verdict.Recover || !keyed) return false;
            if (downMilliseconds < SpokenRestartMinimumMilliseconds) return false;
            if (_restartSaidAtMs != long.MinValue && nowMs - _restartSaidAtMs < WindowMilliseconds) return false;
            _restartSaidAtMs = nowMs;
            return true;
        }
    }
}
