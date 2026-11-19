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
        /// Whether a successful restart is spoken. The orchestrator's default,
        /// recorded as a question for Noel: a restart while the radio is not
        /// keyed is traced only (the operator heard at most a moment's gap and
        /// nothing is left for them to do); a restart while keyed is said,
        /// because part of that transmission went out silent.
        /// </summary>
        public static bool SpeakRestart(Decision decision, bool keyed) =>
            decision.Verdict == Verdict.Recover && decision.Speakable && keyed;
    }
}
