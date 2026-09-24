#nullable enable
using System;
using System.Collections.Generic;

namespace Radios.Facts
{
    /// <summary>Whether a current rendering of a fact is still justified.</summary>
    /// <remarks>
    /// <para>
    /// <b>This replaces a nullable refresh, and the replacement is the point.</b>
    /// A refresh delegate that returns null, or throws, or finds no meter, or
    /// runs while disconnected, was being read as "the condition cleared" —
    /// so a failed measurement arrived as good news and the pending warning was
    /// removed. Four different failures and one success all looked the same.
    /// </para>
    /// <para>
    /// There is deliberately no way to produce <see cref="Current"/> by
    /// accident. It is issued by an owner that positively established the
    /// condition; everything else lands in <see cref="Unknown"/> or
    /// <see cref="Ended"/>, both of which RETAIN the obligation.
    /// </para>
    /// </remarks>
    public enum ValidityState
    {
        /// <summary>This owner establishes the condition, for these dependencies, now.</summary>
        Current = 0,

        /// <summary>A named positive observation or lifecycle event invalidated this rendering.</summary>
        Ended = 1,

        /// <summary>
        /// Observation failed, data became unavailable, provenance was
        /// ambiguous, or evaluation failed. The last known observation is
        /// preserved with its time and scope, current assertions are suspended,
        /// and <b>the obligation is retained</b>.
        /// </summary>
        Unknown = 2,

        /// <summary>A named newer revision replaces specified information in this same episode.</summary>
        Superseded = 3,
    }

    /// <summary>Which ending — three different things that all stop a sentence.</summary>
    public enum EndedKind
    {
        /// <summary>The condition itself resolved. The one that is genuinely good news.</summary>
        ResolvedCondition = 0,

        /// <summary>
        /// The context the observation lived in ended — disconnect, disposal,
        /// connection replacement, definition edit. Says nothing about whether
        /// the condition holds.
        /// </summary>
        EndedObservationContext = 1,

        /// <summary>The operation this fact described was cancelled.</summary>
        CancelledOperation = 2,
    }

    /// <summary>Why nobody can say. Each is a failure to observe, never an observation.</summary>
    public enum UnknownReason
    {
        ObservationFailed = 0,
        DataUnavailable = 1,
        ProvenanceAmbiguous = 2,
        EvaluationFailed = 3,

        /// <summary>
        /// Scope could not be established at all — which radio, which
        /// connection, which occurrence. Such a report is retained only as an
        /// unscoped, unconfirmed historical exception: it can supersede nothing
        /// and asserts no current station state.
        /// </summary>
        ScopeUnestablished = 4,
    }

    /// <summary>
    /// A typed answer to "is saying this still justified", and what is known
    /// when the answer is no.
    /// </summary>
    public sealed class ValiditySnapshot
    {
        private ValiditySnapshot(
            ValidityState state, EndedKind? ended, UnknownReason? unknown,
            long? supersededBy, string? note, DateTime asOfUtc)
        {
            State = state;
            Ended = ended;
            Unknown = unknown;
            SupersededByRevision = supersededBy;
            Note = note;
            AsOfUtc = asOfUtc;
        }

        public ValidityState State { get; }
        public EndedKind? Ended { get; }
        public UnknownReason? Unknown { get; }

        /// <summary>The revision that covers this one, when superseded.</summary>
        public long? SupersededByRevision { get; }

        /// <summary>What the owner said about it, for the trace and the detail view.</summary>
        public string? Note { get; }

        /// <summary>
        /// When this answer was established. <b>Ordering and explanation
        /// only</b> — no amount of elapsed time turns Current into anything
        /// else, which is the whole of the no-clock rule in one field.
        /// </summary>
        public DateTime AsOfUtc { get; }

        /// <summary>True only when an owner positively established the condition.</summary>
        public bool IsCurrent => State == ValidityState.Current;

        /// <summary>
        /// True when the CONDITION is genuinely gone — the only ending that is
        /// good news, and the only one that may be described as resolved.
        /// </summary>
        public bool IsResolved => State == ValidityState.Ended && Ended == EndedKind.ResolvedCondition;

        /// <summary>
        /// An owner establishes the condition. The only route to
        /// <see cref="ValidityState.Current"/>, and it takes a decision, never
        /// the absence of a failure.
        /// </summary>
        public static ValiditySnapshot Establish(DateTime asOfUtc, string? note = null)
            => new ValiditySnapshot(ValidityState.Current, null, null, null, note, asOfUtc);

        /// <summary>
        /// A named positive observation or lifecycle event ended this
        /// rendering. <b>Only an owner transition reaches
        /// <see cref="EndedKind.ResolvedCondition"/></b>; a disconnect or a
        /// disposal is an ended observation context and claims nothing about
        /// the condition.
        /// </summary>
        public static ValiditySnapshot End(EndedKind kind, DateTime asOfUtc, string? note = null)
            => new ValiditySnapshot(ValidityState.Ended, kind, null, null, note, asOfUtc);

        /// <summary>
        /// Nobody can say. The last known observation stays, the current
        /// assertion is suspended, and the obligation is retained.
        /// </summary>
        public static ValiditySnapshot NotKnown(UnknownReason reason, DateTime asOfUtc, string? note = null)
            => new ValiditySnapshot(ValidityState.Unknown, null, reason, null, note, asOfUtc);

        /// <summary>A newer revision in this same episode covers this information.</summary>
        public static ValiditySnapshot Superseded(long byRevision, DateTime asOfUtc, string? note = null)
            => new ValiditySnapshot(ValidityState.Superseded, null, null, byRevision, note, asOfUtc);

        public override string ToString() =>
            State switch
            {
                ValidityState.Ended => "ended (" + Ended + ")",
                ValidityState.Unknown => "unknown (" + Unknown + ")",
                ValidityState.Superseded => "superseded by revision " + SupersededByRevision,
                _ => "current",
            };
    }

    /// <summary>
    /// The validity contract names domain owners have registered, so a message
    /// citing one can be checked rather than believed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The JSON names a contract; this says who can evaluate it.</b> Nothing
    /// here evaluates anything — registering is a domain owner declaring that
    /// it will answer for that contract. The separation is what stops a wording
    /// file from being able to decide whether a radio is transmitting.
    /// </para>
    /// <para>
    /// Empty in a process where no domain has started, and the schema check
    /// treats an empty registry as "no opinion" rather than as "every contract
    /// is broken".
    /// </para>
    /// </remarks>
    public static class ValidityRegistry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string> Owners =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Declare that <paramref name="owner"/> answers for this contract.</summary>
        public static void Register(string contract, string owner)
        {
            if (string.IsNullOrWhiteSpace(contract)) throw new ArgumentNullException(nameof(contract));
            lock (Gate) Owners[contract] = owner;
        }

        /// <summary>Has anyone taken responsibility for this contract?</summary>
        public static bool IsRegistered(string? contract)
        {
            if (string.IsNullOrEmpty(contract)) return false;
            if (ValidityContracts.IsBuiltIn(contract)) return true;
            lock (Gate) return Owners.ContainsKey(contract!);
        }

        /// <summary>Every registered contract name, for the schema gate.</summary>
        public static IReadOnlyCollection<string> Registered
        {
            get { lock (Gate) return new List<string>(Owners.Keys); }
        }

        /// <summary>For tests. Never called in the running application.</summary>
        internal static void Forget()
        {
            lock (Gate) Owners.Clear();
        }
    }
}
