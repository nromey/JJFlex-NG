#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using JJTrace;

namespace Radios.Speech
{
    /// <summary>
    /// Counts of speech said, cut before a word, dropped and refused, written to the ordinary
    /// log as one short line (#506).
    /// </summary>
    /// <remarks>
    /// <para>The ordinary log records every DROPPED utterance at Warning, but
    /// the per-utterance "Spoke" lines are Verbose, so an ordinary capture
    /// held a numerator with no denominator: 89 drops on 2026-09-01, out of
    /// an unknown number said. Moving every "Spoke" line up to the ordinary
    /// level was tried and rejected by Noel on 2026-10-27, because a log
    /// that writes every sentence all day grows fast. The full sentences
    /// stay in detailed capture, which records everything spoken; the
    /// ordinary log gets these counts instead.</para>
    /// <para>The line is written at most every <see cref="Interval"/>, only
    /// when something changed, and always at disconnect and at shutdown,
    /// so a quiet tail is never lost. Each line counts since the previous
    /// one, so the lines can be summed.</para>
    /// </remarks>
    internal sealed class SpeechTally
    {
        internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

        /// <summary>The one tally the application uses.</summary>
        internal static readonly SpeechTally Shared =
            new SpeechTally(() => Environment.TickCount64, () => DateTime.Now, TraceLineAtWarning);

        private readonly Func<long> _ticks;
        private readonly Func<DateTime> _now;
        private readonly Action<string> _write;
        private readonly object _gate = new object();

        private long _said, _unheard, _dropped, _refused;
        private long _windowStartTicks;
        private DateTime _windowStart;

        internal SpeechTally(Func<long> ticks, Func<DateTime> now, Action<string> write)
        {
            _ticks = ticks;
            _now = now;
            _write = write;
            _windowStartTicks = ticks();
            _windowStart = now();
        }

        /// <summary>One utterance handed to the screen reader.</summary>
        internal void NoteSaid()
        {
            Interlocked.Increment(ref _said);
            WriteIfDue();
        }

        /// <summary>One paced utterance withdrawn or cut before its first word.</summary>
        internal void NoteUnheard() => Interlocked.Increment(ref _unheard);

        /// <summary>One salvage the arbiter gave up on.</summary>
        internal void NoteDropped() => Interlocked.Increment(ref _dropped);

        /// <summary>One utterance the screen reader refused.</summary>
        internal void NoteRefused() => Interlocked.Increment(ref _refused);

        /// <summary>What one paced outcome adds to the tally.</summary>
        internal enum PacedCount { Said, Unheard, CountedElsewhere }

        /// <summary>
        /// Classify the one outcome the pump reports per ticket. A paced
        /// utterance is NOT counted when it is queued, because the pump can
        /// still withdraw it at word zero, and a refusal arrives later as an
        /// outcome; counting at the queue made one refused sentence both said
        /// and refused.
        /// </summary>
        /// <remarks>
        /// Refused is counted by the delivery check that reports it. The
        /// reasons that re-send through the plain path (no channel, bad SSML,
        /// an unclassified code) are counted by that path, said or refused.
        /// Timeout and Escaped count as said: the reader may well have spoken,
        /// and the pump deliberately does not re-send them.
        /// </remarks>
        internal static PacedCount Classify(SpeechOutcome outcome)
        {
            switch (outcome.Kind)
            {
                case SpeechOutcomeKind.Completed:
                    return PacedCount.Said;
                case SpeechOutcomeKind.Cancelled:
                    return outcome.MarksReached > 0 ? PacedCount.Said : PacedCount.Unheard;
                default:
                    return outcome.UnknownReason is SpeechUnknownReason.Timeout or SpeechUnknownReason.Escaped
                        ? PacedCount.Said
                        : PacedCount.CountedElsewhere;
            }
        }

        /// <summary>Count one paced outcome; see <see cref="Classify"/>.</summary>
        internal void NoteOutcome(SpeechOutcome outcome)
        {
            switch (Classify(outcome))
            {
                case PacedCount.Said: NoteSaid(); break;
                case PacedCount.Unheard: NoteUnheard(); break;
            }
        }

        /// <summary>
        /// Write the line now if anything was counted since the last one.
        /// <paramref name="reason"/> names the occasion: "disconnect" or
        /// "shutdown".
        /// </summary>
        internal void Write(string reason)
        {
            lock (_gate) WriteLocked(reason);
        }

        private void WriteIfDue()
        {
            if (_ticks() - Interlocked.Read(ref _windowStartTicks) < (long)Interval.TotalMilliseconds) return;
            lock (_gate)
            {
                // Re-checked under the gate: two threads can both see the
                // window expire, and only one of them may write.
                if (_ticks() - _windowStartTicks < (long)Interval.TotalMilliseconds) return;
                WriteLocked("every " + (int)Interval.TotalMinutes + " minutes");
            }
        }

        private void WriteLocked(string reason)
        {
            long said = Interlocked.Exchange(ref _said, 0);
            long unheard = Interlocked.Exchange(ref _unheard, 0);
            long dropped = Interlocked.Exchange(ref _dropped, 0);
            long refused = Interlocked.Exchange(ref _refused, 0);
            DateTime since = _windowStart;
            _windowStart = _now();
            Interlocked.Exchange(ref _windowStartTicks, _ticks());

            if (said == 0 && unheard == 0 && dropped == 0 && refused == 0) return;
            // A count line must never throw into the speech path or into
            // disconnect; the interval's counts are lost instead.
            try { _write(Format(since, said, unheard, dropped, refused, reason)); }
            catch { /* ignore */ }
        }

        internal static string Format(DateTime since, long said, long unheard, long dropped, long refused, string reason) =>
            $"SpeechTally: since {since:HH:mm:ss}, {said} said, {unheard} cut before a word, "
            + $"{dropped} salvages dropped by the arbiter, {refused} refused by the reader ({reason})";

        private static void TraceLineAtWarning(string line) =>
            Tracing.TraceLine(line, TraceLevel.Warning);
    }
}
