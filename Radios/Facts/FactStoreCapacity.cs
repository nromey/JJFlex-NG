#nullable enable

namespace Radios.Facts
{
    /// <summary>
    /// Every number this store is bounded by, declared in one place with what
    /// would settle it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These are SIZING parameters, not safety thresholds.</b> None of them
    /// decides whether information matters, whether a condition holds, or
    /// whether anything was heard. They decide how much memory and disk the
    /// store may use, and what happens at the edge — and the edge behaviour is
    /// an honest reduced guarantee, never a quiet drop.
    /// </para>
    /// <para>
    /// <b>They are declared rather than chosen well.</b> The design requires
    /// the builder to name the capacities and the evidence at and beyond them,
    /// and points at the coordinator's twenty seconds as the example of what
    /// not to do — a number picked because it sat between two other numbers is
    /// arithmetic, not measurement. Each value below says what it was sized
    /// against and what experiment would move it.
    /// </para>
    /// <para>
    /// <b>None of them is a clock.</b> There is no age, no lifetime and no
    /// expiry in this file, and there must never be one. Unreviewed history
    /// leaves only by explicit review or by capacity compaction that RECORDS
    /// the loss of detail.
    /// </para>
    /// </remarks>
    public static class FactStoreCapacity
    {
        /// <summary>
        /// How many current condition slots may be registered at once.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Sized from what a station can actually have monitored: the fixed
        /// transmit-safety conditions, pre-reserved when a session is admitted,
        /// plus one slot per enabled operator alarm across the radios in use.
        /// Sixty-four leaves room for several radios' worth without pretending
        /// the number is unlimited.
        /// </para>
        /// <para>
        /// <b>What settles it:</b> the ninth independent warning — and the
        /// sixty-fifth. Registration exhaustion must be REPORTED visibly; a
        /// newly configured alarm whose delivery record could not be allocated
        /// must never be described as monitored. The refusal concerns delivery
        /// registration only and never delays a protective stop.
        /// </para>
        /// </remarks>
        public const int MaxCurrentSlots = 64;

        /// <summary>
        /// How much detail text one fact may retain.
        /// </summary>
        /// <remarks>
        /// Sized well above the longest bounded safety presentation, so no real
        /// message is truncated by it; it exists to stop an unbounded producer
        /// from turning one fact into the whole store. Exceeding it truncates
        /// with an explicit marker rather than silently.
        /// </remarks>
        public const int MaxDetailBytes = 4096;

        /// <summary>
        /// How many historical records are kept before the oldest REVIEWED
        /// ones are compacted.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Compaction takes reviewed and completed detail first, and preserves
        /// occurrence counts, first and last times, condition and station where
        /// available, the last supported outcome, coverage and review counts,
        /// and an explicit detail-compacted flag. <b>Compaction cannot
        /// decrement the unpresented count as though delivery occurred.</b>
        /// </para>
        /// <para>
        /// <b>What settles it:</b> a session long enough to fill it, read by
        /// the operator, with the compacted summary still answering "what
        /// happened and how many times".
        /// </para>
        /// </remarks>
        public const int MaxHistoricalRecords = 256;

        /// <summary>How many attempts one fact retains evidence for.</summary>
        /// <remarks>
        /// Enough to show a pattern of failure without letting a wedged channel
        /// grow a fact without limit. Older attempts coalesce into a count; the
        /// count never shrinks.
        /// </remarks>
        public const int MaxAttemptEvidence = 16;

        /// <summary>Total bytes this process's journal shard may occupy.</summary>
        /// <remarks>
        /// One megabyte of readable JSON is far more than the bounded record
        /// set above can produce, so hitting it means something is wrong rather
        /// than busy — and the store reports pressure instead of overwriting
        /// anything it cannot safely compact.
        /// </remarks>
        public const int MaxJournalBytes = 1024 * 1024;

        /// <summary>
        /// How many automatic attempts a fact makes in one burst before
        /// yielding to a waiting explicit request.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>THIS IS A FAIRNESS LIMIT AND NOTHING ELSE.</b> It says when a
        /// repeat waits its turn behind somebody who asked a question. It is
        /// NOT a lifetime cap, NOT an attempt quota, and it can never turn a
        /// still-true persistent condition into a terminal silent state.
        /// </para>
        /// <para>
        /// The distinction is load-bearing because an earlier contract used the
        /// same number as a terminal allowance — after two attempts, stop — and
        /// that was struck out for contradicting Noel's ruling that the
        /// condition makes a fact true rather than the clock or the counter. The
        /// number survived; its meaning did not. Anyone tempted to read this as
        /// "two tries and we give up" is reading the version that was removed.
        /// </para>
        /// <para>
        /// After a burst the slot rotates: one waiting explicit read or status
        /// request goes ahead of a third repeat. A genuinely new immediate
        /// transmit hazard still outranks that request; repeated old
        /// information does not.
        /// </para>
        /// </remarks>
        public const int AutomaticBurstAttempts = 2;

        /// <summary>
        /// The bounded backoff between automatic opportunities, in
        /// milliseconds, capped at the last value.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>These delays limit WORK; they never determine truth.</b> Reaching
        /// the cap does not retire anything — a still-eligible persistent fact
        /// keeps getting opportunities at the capped interval for as long as
        /// its condition holds. There is no step after the last one that means
        /// "stop".
        /// </para>
        /// <para>
        /// <b>What settles them:</b> the builder of the scheduler must exercise
        /// these concrete delays and show that a known-bad bridge receives only
        /// bounded probes while the rest stay pending. They are scheduler
        /// parameters, not an operator threshold, and they are here rather than
        /// in the scheduler so that every number the store's behaviour depends
        /// on is in one file.
        /// </para>
        /// </remarks>
        public static readonly int[] AutomaticBackoffMs = { 2000, 5000, 15000, 30000 };
    }
}
