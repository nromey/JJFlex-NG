#nullable enable
using System;
using System.Threading;
using Radios.Speech;

namespace Radios
{
    /// <summary>
    /// The owner of what is said about one tune carrier (#1127, batch 12 repair
    /// round 2): the radio's answer to it ("Tune on", or that it did not key),
    /// a door's refusal while it is up, and the SWR it measured. Each carrier
    /// gets an identity when it is asked for; what is said about it is never
    /// delivered after the carrier has ended or a later one has started.
    /// </summary>
    /// <remarks>
    /// <para><b>Why an owner.</b> On the 8600 bench a held tune key keyed on
    /// its first down and turned the carrier off at its first repeat, half a
    /// second later, while "Tune on" still waited behind the press's own
    /// sentence. "Tune off" interrupted, the arbiter rescued the unheard
    /// "Tune on" behind it, and that was the last thing said, with nothing on
    /// the air. Twelve seconds later the next carrier's start rescued the
    /// previous carrier's "SWR not measured" into the new carrier. Nothing
    /// covered either sentence, because "Tune on" had no subject and nothing
    /// retired the SWR. The Sprint 49 noise rounds gave queued notices an owner
    /// that retires them when what they describe changes; this is that, for a
    /// carrier.</para>
    ///
    /// <para><b>The two retirements, and why they differ.</b> A carrier ending
    /// (<see cref="Ended"/>, <see cref="RadioDropped"/>) retires "Tune on" and
    /// the refusals, which claim the carrier is up; it leaves the SWR alone,
    /// because the SWR is the ended carrier's own result and is said after it.
    /// A new carrier starting (<see cref="Started"/>) retires all of them,
    /// the previous carrier's SWR included: spoken inside the new carrier it
    /// reads as that carrier's answer. This narrows #503's rule that a retry
    /// must deliver the earlier reading: a press that starts nothing (refused,
    /// or more of a held key) retires nothing, and a carrier that does start
    /// gives its own reading when it ends.</para>
    ///
    /// <para><b>Retirement, and a check besides.</b> Retiring reaches what the
    /// arbiter holds for rescue and what the delivery queue has not yet sent
    /// (<see cref="ScreenReaderOutput.Supersede"/>). "Tune on" also carries
    /// <see cref="KeyedAnswerStillTrue"/>, asked before any replay, for an end
    /// this class was not told of in time.</para>
    ///
    /// <para>Thread-safe and lock-free: the radio's fall arrives on FlexLib's
    /// read loop, and the check is asked inside the arbiter's lock.</para>
    /// </remarks>
    public sealed class TuneCarrierAnswers
    {
        private readonly Action<string, string> _retire;
        private long _current;   // the newest carrier asked for; 0 before the first
        private long _keyed;     // the newest carrier the radio reported keyed
        private long _ended;     // the newest carrier known to have ended

        /// <param name="retire">For tests: stands in for the arbiter's supersession.</param>
        public TuneCarrierAnswers(Action<string, string>? retire = null)
        {
            _retire = retire ?? ((subject, why) => ScreenReaderOutput.Supersede(subject, why));
        }

        /// <summary>The subjects a carrier's end retires: what claims it is up.</summary>
        public static readonly string[] EndRetires =
        {
            SpeechSubject.TuneKeyed, SpeechSubject.TuneNotKeyed, SpeechSubject.TuneRefused,
        };

        /// <summary>The subjects a new carrier retires: everything said about an earlier one.</summary>
        public static readonly string[] StartRetires =
        {
            SpeechSubject.TuneKeyed, SpeechSubject.TuneNotKeyed, SpeechSubject.TuneRefused, SpeechSubject.SwrAfterTune,
        };

        /// <summary>The newest carrier asked for (0 before the first).</summary>
        public long Current => Volatile.Read(ref _current);

        /// <summary>
        /// A new carrier has been asked for (a tune carrier, or an ATU's tune
        /// cycle). Retires everything still unheard about earlier carriers,
        /// before the new carrier's own speech, and returns its identity.
        /// </summary>
        public long Started(string why)
        {
            long id = Interlocked.Increment(ref _current);
            foreach (string subject in StartRetires) _retire(subject, why);
            return id;
        }

        /// <summary>The radio reported that carrier <paramref name="id"/> keyed.</summary>
        public void Keyed(long id)
        {
            if (id == Volatile.Read(ref _current)) Volatile.Write(ref _keyed, id);
        }

        /// <summary>
        /// We ended the current carrier, or the connection that carried it went:
        /// retire whatever still says it is up. Before the end's own sentence.
        /// </summary>
        public void Ended(string why)
        {
            Volatile.Write(ref _ended, Volatile.Read(ref _current));
            foreach (string subject in EndRetires) _retire(subject, why);
        }

        /// <summary>
        /// The radio says tune went down, or it went to receive, however that
        /// came about. Taken as the current carrier's end only once the radio
        /// has reported that carrier keyed: before then a fall is the previous
        /// carrier's, arriving late, and must not retire the new one's answer.
        /// Returns whether it was taken.
        /// </summary>
        public bool RadioDropped(string why)
        {
            long current = Volatile.Read(ref _current);
            if (current == 0 || Volatile.Read(ref _keyed) != current) return false;
            if (Volatile.Read(ref _ended) == current) return false;   // already ended and retired
            Ended(why);
            return true;
        }

        /// <summary>
        /// For "Tune on": still true while carrier <paramref name="id"/> is the
        /// newest, has keyed, and has not ended. Never waits and never throws.
        /// </summary>
        public Func<bool?> KeyedAnswerStillTrue(long id) => () =>
            id == Volatile.Read(ref _current)
            && id == Volatile.Read(ref _keyed)
            && id != Volatile.Read(ref _ended);
    }
}
