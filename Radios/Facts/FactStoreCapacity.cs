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
        /// <para>
        /// Schema 2 persists attempt evidence, typed material and event history
        /// that schema 1 threw away, so the one megabyte Track M chose no longer
        /// holds the bounded record set. <b>Sized by measurement, not by
        /// arithmetic:</b> <c>FactJournalCapacityTests</c> fills every bound at
        /// once — every historical record, every attempt, every event line,
        /// every unit, the longest detail — renders it, and asserts it fits here
        /// with room to spare. If that test fails, a bound grew; change the
        /// bound or this number deliberately, never by guessing. Measured
        /// 2026-09-24 (Sprint 45 Track M2): 4,572,586 bytes, about 57 percent
        /// of the 8 MB allowance that then held. Measured again the same day
        /// (Sprint 45 Track M3), after schema 3 gave every material unit its
        /// origin and root assertion references and every continuity entry a
        /// per-assertion evidence summary: 11,041,622 bytes, about 66 percent
        /// of the 16 MB allowance that then held. <b>Neither of those was the
        /// fullest image.</b> Sol's review of M3 found the test filled attempts
        /// on one fact in sixteen and no fact's event history, so both were
        /// samples. Measured a third time the same day (Sprint 45 Track M4)
        /// with every bound asserted full first — every record, every unit,
        /// the longest detail, every event line, every attempt with its
        /// evidence plus every tombstone, one continuity entry per record with
        /// every assertion, every issue row with every key and more exemplars
        /// than it keeps plus the overflow row, and every overlay: 18,160,150
        /// bytes, over the 16 MB allowance. Hence this 32 MB, about 54 percent
        /// used. The number moved because the measurement did, not the other
        /// way round.
        /// </para>
        /// <para>
        /// Hitting it at run time means something is wrong rather than busy,
        /// and the store reports an unsaved state instead of overwriting
        /// anything it cannot safely compact.
        /// </para>
        /// </remarks>
        public const int MaxJournalBytes = 32 * 1024 * 1024;

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

        // ────────────────────────────────────────────────────────────────
        //  Bounds added with the authority and history rework. Each counts
        //  toward declared capacity; none decides truth.
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Store-issue rows. Deliberately separate from fact capacity: a
        /// pressure event must still have somewhere to be written when the
        /// facts are what filled. Beyond this, one reserved summary row counts
        /// the rest, so even the table of problems cannot fill silently.
        /// </summary>
        public const int MaxIssues = 32;

        /// <summary>Exemplars kept per issue row, with an explicit marker when more were dropped.</summary>
        public const int MaxIssueExemplars = 8;

        /// <summary>
        /// Deduplication keys kept per issue, so the same corrupt generation or
        /// the same refused event is not counted twice. Beyond this the count
        /// becomes a lower bound, said so.
        /// </summary>
        public const int MaxIssueDedupeKeys = 256;

        /// <summary>Event history lines per fact; older ones become a dropped count.</summary>
        public const int MaxEventHistoryPerFact = 16;

        /// <summary>Applied-event identities remembered per fact, for duplicate and conflict detection.</summary>
        public const int MaxAppliedEventsPerFact = 64;

        /// <summary>Units of owed information per fact.</summary>
        public const int MaxMaterialUnitsPerFact = 32;

        /// <summary>Operator quiet positions remembered, for the pause evidence a detail can explain.</summary>
        public const int MaxQuietHistory = 32;

        /// <summary>
        /// Continuity records across disconnects. Losing one is exposed as a
        /// row, and a reconnect then needs positive onset evidence or an
        /// explicit resume — absence of a record is never read as clear.
        /// </summary>
        public const int MaxContinuityRecords = 128;

        /// <summary>
        /// Retired attempts kept only so late evidence can still be attributed.
        /// Beyond this, late evidence is uncorrelatable and the debt it might
        /// have settled stays unknown.
        /// </summary>
        public const int MaxAttemptTombstones = 64;

        /// <summary>Display tokens one open view keeps valid at once.</summary>
        public const int MaxTokensPerView = 64;
    }
}
