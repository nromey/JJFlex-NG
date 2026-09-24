#nullable enable
using System;
using System.Globalization;

namespace Radios.Facts
{
    /// <summary>How an episode identity came to exist.</summary>
    public enum EpisodeOrigin
    {
        /// <summary>Allocated by a live store: writer incarnation plus a monotonic ordinal.</summary>
        Allocated = 0,

        /// <summary>
        /// Derived, stably, from a schema-1 record's preserved origin fields
        /// (its writer's process incarnation and its saved episode). Legacy
        /// evidence, never proof the new contract existed.
        /// </summary>
        Legacy = 1,

        /// <summary>
        /// A schema-1 record whose origin could not be established, namespaced
        /// by the file it came from so two different files can never be taken
        /// for the same episode.
        /// </summary>
        LegacySourceSpecific = 2,
    }

    /// <summary>
    /// The durable identity of one episode: the writer incarnation that
    /// allocated it plus that writer's ordinal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Structured fields, never a delimited string.</b> Track M's identity
    /// concatenated process, connection, slot and occurrence into text, and the
    /// journal then rebuilt it with process zero — so two released shards with
    /// equal local counters produced the same identity and one silently
    /// replaced the other. Here the writer incarnation is random and minted
    /// once per store, so two legitimate writers can never collide, and no
    /// label, slot text or timestamp takes part.
    /// </para>
    /// <para>
    /// Moving or compacting a record never changes it. A loader may give a row
    /// a local handle; it may never replace this with one.
    /// </para>
    /// </remarks>
    public readonly struct EpisodeId : IEquatable<EpisodeId>
    {
        public EpisodeId(Guid writer, long ordinal, EpisodeOrigin origin = EpisodeOrigin.Allocated)
        {
            if (writer == Guid.Empty) throw new ArgumentException("an episode needs its writer", nameof(writer));
            if (ordinal <= 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
            Writer = writer;
            Ordinal = ordinal;
            Origin = origin;
        }

        public Guid Writer { get; }
        public long Ordinal { get; }
        public EpisodeOrigin Origin { get; }

        public bool IsDefault => Writer == Guid.Empty;

        public bool Equals(EpisodeId other) =>
            Writer == other.Writer && Ordinal == other.Ordinal && Origin == other.Origin;

        public override bool Equals(object? obj) => obj is EpisodeId other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Writer, Ordinal, Origin);
        public static bool operator ==(EpisodeId a, EpisodeId b) => a.Equals(b);
        public static bool operator !=(EpisodeId a, EpisodeId b) => !a.Equals(b);

        /// <summary>For diagnostics and item identifiers. Never parsed back for equality.</summary>
        public override string ToString() =>
            (Origin == EpisodeOrigin.Allocated ? "e" : Origin == EpisodeOrigin.Legacy ? "l" : "s")
            + Writer.ToString("N") + "." + Ordinal.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One attempt's identity: the writer that allocated it plus an ordinal.</summary>
    public readonly struct AttemptId : IEquatable<AttemptId>
    {
        public AttemptId(Guid writer, long ordinal)
        {
            Writer = writer;
            Ordinal = ordinal;
        }

        public Guid Writer { get; }
        public long Ordinal { get; }

        public bool Equals(AttemptId other) => Writer == other.Writer && Ordinal == other.Ordinal;
        public override bool Equals(object? obj) => obj is AttemptId other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Writer, Ordinal);
        public static bool operator ==(AttemptId a, AttemptId b) => a.Equals(b);
        public static bool operator !=(AttemptId a, AttemptId b) => !a.Equals(b);
        public override string ToString() => "a" + Writer.ToString("N") + "." + Ordinal.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>What kind of lifetime an observation scope is.</summary>
    public enum FactScopeKind
    {
        /// <summary>One concrete attachment to a radio.</summary>
        RadioSession = 0,

        /// <summary>An application activity that has nothing to do with a radio — a capture, say.</summary>
        Activity = 1,
    }

    /// <summary>
    /// An issued observation scope: a radio session or an application
    /// activity. Held by its lifecycle owner, which is the only thing that can
    /// end it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Identity is derived from the issued scope, never accepted from a
    /// producer.</b> The scope carries its connection identity and the
    /// established radio identity or an explicit unknown; a producer publishing
    /// through a slot in this scope gets these and cannot supply others.
    /// </para>
    /// <para>
    /// An activity uses its own handle rather than a fictitious radio, so a
    /// capture's detail depends on the capture, not on whichever radio happens
    /// to be selected.
    /// </para>
    /// </remarks>
    public abstract class FactScope
    {
        private protected FactScope(FactStore store, long scopeId, string label)
        {
            Store = store;
            ScopeId = scopeId;
            Label = label ?? string.Empty;
        }

        internal FactStore Store { get; }

        /// <summary>Allocated by the store. A reconnect to the same radio is a new scope.</summary>
        public long ScopeId { get; }

        /// <summary>For the trace. Not identity and not authority.</summary>
        public string Label { get; }

        public abstract FactScopeKind Kind { get; }

        /// <summary>The established stable radio identity, or null when it is unknown or this is an activity.</summary>
        public abstract string? RadioIdentity { get; }

        // Written only under the store's gate.
        internal bool EndedLocked;
        internal long EndedAtSequenceLocked;

        /// <summary>True once the lifecycle owner has ended this scope.</summary>
        public bool Ended => Store.ScopeEnded(this);

        /// <summary>
        /// End the observation context. Atomically revokes every publisher in
        /// this scope and every current plan depending on it, and ends the
        /// current rendering of what it was observing.
        /// </summary>
        /// <remarks>
        /// <b>An ended observation context, never a resolved condition.</b> A
        /// disconnect is not evidence a hot PA cooled; what was last established
        /// stays readable and current state becomes unknown.
        /// </remarks>
        public int End(DateTime asOfUtc, string why) => Store.EndScope(this, asOfUtc, why);

        public override string ToString() => Kind + " " + ScopeId + (Label.Length > 0 ? " (" + Label + ")" : "");
    }

    /// <summary>One concrete attachment to a radio.</summary>
    public sealed class FactSession : FactScope
    {
        internal FactSession(FactStore store, long scopeId, string? radioIdentity, string label)
            : base(store, scopeId, label)
        {
            RadioIdentity = string.IsNullOrWhiteSpace(radioIdentity) ? null : radioIdentity;
        }

        public override FactScopeKind Kind => FactScopeKind.RadioSession;

        /// <summary>
        /// <b>Unknown stays unknown</b> — never a nickname and never whichever
        /// rig happens to be selected later.
        /// </summary>
        public override string? RadioIdentity { get; }
    }

    /// <summary>An application activity with a lifetime of its own.</summary>
    public sealed class FactActivity : FactScope
    {
        internal FactActivity(FactStore store, long scopeId, string label) : base(store, scopeId, label) { }

        public override FactScopeKind Kind => FactScopeKind.Activity;
        public override string? RadioIdentity => null;
    }

    /// <summary>
    /// A registered owner instance. Opaque: its name is for the trace, and the
    /// object itself is the token.
    /// </summary>
    /// <remarks>
    /// Declared by the composition root with the contracts it may publish
    /// under. A replacement owner is a NEW declaration with a new token; any
    /// transfer of a continuing condition is explicit at the lifecycle
    /// registry, never implied by a matching name.
    /// </remarks>
    public sealed class FactOwner
    {
        internal FactOwner(FactStore store, string name, ConditionContract[] contracts)
        {
            Store = store;
            Name = name;
            Contracts = contracts;
        }

        internal FactStore Store { get; }

        /// <summary>For the trace. Two owners with the same name are two owners.</summary>
        public string Name { get; }

        public System.Collections.Generic.IReadOnlyList<ConditionContract> Contracts { get; }

        internal bool Declares(ConditionContract contract)
        {
            foreach (ConditionContract c in Contracts)
                if (ReferenceEquals(c, contract) || c.SameAs(contract)) return true;
            return false;
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// An immutable captured source event, sealed at the earliest trusted
    /// producer boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Captured before any asynchronous handoff.</b> Its position in the one
    /// sequence shared with quiet, lifecycle and start authorisation is taken
    /// when it is captured, not when a delayed evaluator finally admits it — so
    /// an event captured before Ctrl and delivered after it still sits BEFORE
    /// the quiet, and cannot earn fresh permission by arriving late.
    /// </para>
    /// <para>
    /// A derived transition carries this event; it cannot capture a new
    /// position to make its result look newer.
    /// </para>
    /// </remarks>
    public sealed class CapturedFactEvent
    {
        internal CapturedFactEvent(
            SlotPublisher publisher, long eventOrdinal, long sequence, long quietObserved,
            string? sourceEventId, FactObservation observation, DateTime observedUtc)
        {
            Publisher = publisher;
            EventOrdinal = eventOrdinal;
            Sequence = sequence;
            QuietObserved = quietObserved;
            SourceEventId = sourceEventId;
            Observation = observation;
            ObservedUtc = observedUtc;
        }

        internal SlotPublisher Publisher { get; }

        /// <summary>Allocated at capture, unique within this writer. The event's identity.</summary>
        public long EventOrdinal { get; }

        /// <summary>
        /// Its position in the one ordered stream. Local to this writer
        /// incarnation; never compared across restarts.
        /// </summary>
        public long Sequence { get; }

        /// <summary>
        /// The most recent quiet position when this was captured. Explanatory
        /// only — which side of a quiet an event is on is decided by comparing
        /// <see cref="Sequence"/> with the quiet's own position.
        /// </summary>
        public long QuietObserved { get; }

        /// <summary>The source's own identity for the event, where it has one.</summary>
        public string? SourceEventId { get; }

        public FactObservation Observation { get; }

        /// <summary>When the source says it observed this. For ordering display and explanation, never for staleness.</summary>
        public DateTime ObservedUtc { get; }

        public override string ToString() => "event " + EventOrdinal + " at " + Sequence;
    }

    /// <summary>
    /// The handle an owner holds for one occurrence it opened. Opaque; it
    /// binds the episode to the publisher that opened it.
    /// </summary>
    public sealed class EpisodeHandle
    {
        internal EpisodeHandle(SlotPublisher publisher, EpisodeId id)
        {
            Publisher = publisher;
            Id = id;
        }

        internal SlotPublisher Publisher { get; }
        public EpisodeId Id { get; }

        public override string ToString() => Id.ToString();
    }
}
