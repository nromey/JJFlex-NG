#nullable enable
using System;

namespace Radios.Facts
{
    /// <summary>
    /// The right to update one episode, issued by the owner whose lifetime it
    /// belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Spelling a subject does not grant authority.</b> Before this existed,
    /// anything that could name a <c>SpeechSubject</c> could retire anything
    /// else wearing the same subject — which is how one radio's warning could
    /// cancel another's, and how a caller could obtain transmit-safety standing
    /// by choosing an urgent-sounding key. A capability is opaque, is issued,
    /// and can be revoked; a string is none of those.
    /// </para>
    /// <para>
    /// <b>It is never restored from disk.</b> Facts survive a restart as
    /// history; capabilities do not. A token loaded from a file would be
    /// authority over a live store granted by a previous process's belief, and
    /// a reconnect to the same radio is a new incarnation, not a continuation.
    /// </para>
    /// </remarks>
    public sealed class ProducerCapability
    {
        private readonly Guid _token;

        internal ProducerCapability(
            Guid token, string producerName, long connectionIncarnation, string? radioIdentity)
        {
            _token = token;
            ProducerName = producerName;
            ConnectionIncarnation = connectionIncarnation;
            RadioIdentity = radioIdentity;
        }

        /// <summary>Who holds it, for the trace. Not identity and not authority.</summary>
        public string ProducerName { get; }

        /// <summary>The connection this capability was issued against.</summary>
        public long ConnectionIncarnation { get; }

        /// <summary>
        /// The stable radio identity when it is known, and null when it is not.
        /// <b>Unknown stays unknown</b> — never a nickname and never whichever
        /// rig happens to be selected later.
        /// </summary>
        public string? RadioIdentity { get; }

        /// <summary>Revoked at detach, disposal, connection replacement and shutdown.</summary>
        public bool Revoked { get; private set; }

        internal void Revoke() => Revoked = true;

        internal bool Matches(ProducerCapability? other) =>
            other != null && other._token == _token;

        public override string ToString() =>
            ProducerName + " on connection " + ConnectionIncarnation
            + (RadioIdentity == null ? " (radio unknown)" : " (" + RadioIdentity + ")")
            + (Revoked ? " [revoked]" : string.Empty);
    }

    /// <summary>
    /// Where an observation came from and how good it was — kept beside the
    /// fact rather than folded into it.
    /// </summary>
    /// <remarks>
    /// <b>Captured at the event source, never read when the callback runs.</b>
    /// Reading the current generation at the moment a delayed callback finally
    /// executes manufactures provenance: it stamps an old observation with a
    /// new connection's authority, and nothing downstream can tell. Every
    /// callback closure captures its incarnation when it is subscribed.
    /// </remarks>
    public sealed class FactProvenance
    {
        public FactProvenance(
            long connectionIncarnation,
            long ingestionSequence,
            DateTime observedUtc,
            string? sourceIdentity = null,
            bool observationComplete = true)
        {
            ConnectionIncarnation = connectionIncarnation;
            IngestionSequence = ingestionSequence;
            ObservedUtc = observedUtc;
            SourceIdentity = sourceIdentity;
            ObservationComplete = observationComplete;
        }

        /// <summary>The connection incarnation captured when the callback was subscribed.</summary>
        public long ConnectionIncarnation { get; }

        /// <summary>
        /// Position in the one ingestion sequence shared with the quiet barrier.
        /// <b>Ordering comes from this, not from timestamps</b> — two delayed
        /// queues cannot be ordered reliably by the clock, and the whole point
        /// of the barrier is to know which side of it an event was captured on.
        /// </summary>
        public long IngestionSequence { get; }

        /// <summary>
        /// When the observation was made. For ORDERING and for explaining an
        /// observation to a person — never for deciding that anything became
        /// false.
        /// </summary>
        public DateTime ObservedUtc { get; }

        /// <summary>The concrete source object or stream, where one can be named.</summary>
        public string? SourceIdentity { get; }

        /// <summary>
        /// False when the observation was partial, cached or of uncertain
        /// quality. An incomplete observation can still be recorded; it just
        /// cannot assert current station state.
        /// </summary>
        public bool ObservationComplete { get; }
    }

    /// <summary>
    /// Everything that answers "which episode is this" — separate fields,
    /// because collapsing any two of them is how one incident comes to be read
    /// as a revision of another.
    /// </summary>
    public sealed class FactIdentity
    {
        public FactIdentity(
            long processIncarnation,
            long connectionIncarnation,
            string? radioIdentity,
            string conditionSlot,
            string occurrenceId,
            long? transmitGeneration = null)
        {
            ProcessIncarnation = processIncarnation;
            ConnectionIncarnation = connectionIncarnation;
            RadioIdentity = radioIdentity;
            ConditionSlot = conditionSlot;
            OccurrenceId = occurrenceId;
            TransmitGeneration = transmitGeneration;
        }

        /// <summary>This run of the application. Restored records carry an older one.</summary>
        public long ProcessIncarnation { get; }

        /// <summary>
        /// This attachment to a radio. A reconnect to the same serial gets a
        /// NEW one, because the gap is exactly where the world may have
        /// changed without us.
        /// </summary>
        public long ConnectionIncarnation { get; }

        /// <summary>The stable radio identity, or null when it was never established.</summary>
        public string? RadioIdentity { get; }

        /// <summary>
        /// The registered condition slot — which fixed transmit condition, or
        /// which alarm definition. Registered before monitoring starts, so
        /// there is always somewhere for its information to go.
        /// </summary>
        public string ConditionSlot { get; }

        /// <summary>
        /// The owner-issued occurrence. <b>Another radio, another connection or
        /// another hazard occurrence is never a revision of an older incident
        /// merely because its condition kind matches.</b>
        /// </summary>
        public string OccurrenceId { get; }

        /// <summary>
        /// The transmit attempt or watch generation, for facts that have one.
        /// Null on a non-transmit fact — <b>no fabricated transmit ID</b>,
        /// because a fabricated one would let an unrelated rekey invalidate
        /// something it has nothing to do with.
        /// </summary>
        public long? TransmitGeneration { get; }

        /// <summary>
        /// The store-wide unique identity of this episode. Globally unique so a
        /// record restored from another process's shard can sit beside a live
        /// one without either claiming to be the other.
        /// </summary>
        public string EpisodeId =>
            ProcessIncarnation.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":" + ConnectionIncarnation.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":" + ConditionSlot + ":" + OccurrenceId;

        public override string ToString() => EpisodeId;
    }
}
