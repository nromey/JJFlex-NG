#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.Facts
{
    /// <summary>
    /// Which class of speech a contract's information belongs to. Fixed by the
    /// contract, never by the message key.
    /// </summary>
    /// <remarks>
    /// <b>A key cannot buy priority.</b> An urgent-sounding lexicon entry grants
    /// nothing; a producer gets the priority its registered contract declares
    /// and no other.
    /// </remarks>
    public enum DeliveryPriority
    {
        Ordinary = 0,
        OperatorAlarm = 1,
        TransmitSafety = 2,
    }

    /// <summary>
    /// The logical condition a slot stands for: a fixed condition, or an alarm
    /// definition and its revision.
    /// </summary>
    /// <remarks>
    /// <b>A description, not an authority.</b> Two radios with the same
    /// condition are two slot allocations; identical text grants nothing.
    /// The definition revision is carried so continuity across a reconnect can
    /// say which definition a pause was recorded under — and a CHANGED
    /// definition is deliberately not evidence of a new onset.
    /// </remarks>
    public sealed class ConditionKey : IEquatable<ConditionKey>
    {
        public ConditionKey(string condition, int definitionRevision = 0)
        {
            if (string.IsNullOrWhiteSpace(condition)) throw new ArgumentNullException(nameof(condition));
            Condition = condition;
            DefinitionRevision = definitionRevision;
        }

        public string Condition { get; }
        public int DefinitionRevision { get; }

        public bool Equals(ConditionKey? other) =>
            other != null && Condition == other.Condition && DefinitionRevision == other.DefinitionRevision;

        public override bool Equals(object? obj) => Equals(obj as ConditionKey);
        public override int GetHashCode() => HashCode.Combine(Condition, DefinitionRevision);
        public override string ToString() => Condition + "@" + DefinitionRevision;
    }

    /// <summary>
    /// What one owner is allowed to publish about one kind of condition.
    /// Immutable and versioned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is where authority is narrowed.</b> A contract declares the
    /// claims its owner may make, the message keys it may present them with,
    /// the priority class they carry, the typed evidence it accepts, and
    /// whether its owner may report a worsening or a positive resolution. An
    /// owner can only publish through a contract the composition root declared
    /// for it; a producer cannot register itself as the stop-confirmation owner
    /// by supplying a name.
    /// </para>
    /// <para>
    /// Repeated registration by the same owner is idempotent only when the
    /// ENTIRE contract agrees, which is what <see cref="SameAs"/> answers.
    /// </para>
    /// </remarks>
    public sealed class ConditionContract
    {
        private readonly HashSet<string> _claims;
        private readonly HashSet<string> _messageKeys;
        private readonly Dictionary<string, EvidenceField> _evidence;

        public ConditionContract(
            string name,
            int revision,
            IEnumerable<string> claims,
            IEnumerable<string> messageKeys,
            DeliveryPriority priority,
            IEnumerable<EvidenceField>? evidence = null,
            bool mayReportWorsening = false,
            bool mayResolve = false)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            Name = name;
            Revision = revision;
            _claims = new HashSet<string>(claims ?? throw new ArgumentNullException(nameof(claims)), StringComparer.Ordinal);
            _messageKeys = new HashSet<string>(messageKeys ?? throw new ArgumentNullException(nameof(messageKeys)), StringComparer.Ordinal);
            if (_claims.Count == 0) throw new ArgumentException("a contract must allow at least one claim", nameof(claims));
            if (_messageKeys.Count == 0) throw new ArgumentException("a contract must allow at least one message key", nameof(messageKeys));
            _evidence = new Dictionary<string, EvidenceField>(StringComparer.Ordinal);
            foreach (EvidenceField field in evidence ?? Array.Empty<EvidenceField>())
            {
                if (_evidence.ContainsKey(field.Name))
                    throw new ArgumentException("duplicate evidence field '" + field.Name + "'", nameof(evidence));
                _evidence[field.Name] = field;
            }
            Priority = priority;
            MayReportWorsening = mayReportWorsening;
            MayResolve = mayResolve;
        }

        public string Name { get; }
        public int Revision { get; }
        public DeliveryPriority Priority { get; }

        /// <summary>
        /// Whether this contract's owner may issue a worsening transition — the
        /// one automatic crossing of an operator's quiet (#617, ruled 2026-09-24).
        /// </summary>
        public bool MayReportWorsening { get; }

        /// <summary>Whether this contract's owner may assert that the condition positively resolved.</summary>
        public bool MayResolve { get; }

        public IReadOnlyCollection<string> Claims => _claims;
        public IReadOnlyCollection<string> MessageKeys => _messageKeys;
        public IReadOnlyCollection<EvidenceField> Evidence => _evidence.Values;

        public bool AllowsClaim(string? claim) => claim != null && _claims.Contains(claim);
        public bool AllowsMessage(string? key) => key != null && _messageKeys.Contains(key);

        /// <summary>
        /// Does this observation fit the declared evidence shape? Required
        /// fields present, every field of the declared kind, nothing undeclared.
        /// </summary>
        public bool Accepts(FactObservation observation, out string? why)
        {
            why = null;
            if (observation == null) { why = "no observation"; return false; }
            foreach (var pair in observation.Values)
            {
                if (!_evidence.TryGetValue(pair.Key, out EvidenceField? field))
                {
                    why = "'" + pair.Key + "' is not evidence this contract accepts";
                    return false;
                }
                if (field.Kind != pair.Value.Kind)
                {
                    why = "'" + pair.Key + "' is " + pair.Value.Kind + ", the contract declares " + field.Kind;
                    return false;
                }
            }
            foreach (EvidenceField field in _evidence.Values)
            {
                if (field.Required && !observation.Values.ContainsKey(field.Name))
                {
                    why = "required evidence '" + field.Name + "' is missing";
                    return false;
                }
            }
            return true;
        }

        /// <summary>True when every declared term agrees, which is the only case a repeat registration is idempotent.</summary>
        public bool SameAs(ConditionContract? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Name == other.Name
                && Revision == other.Revision
                && Priority == other.Priority
                && MayReportWorsening == other.MayReportWorsening
                && MayResolve == other.MayResolve
                && _claims.SetEquals(other._claims)
                && _messageKeys.SetEquals(other._messageKeys)
                && _evidence.Count == other._evidence.Count
                && _evidence.All(p => other._evidence.TryGetValue(p.Key, out EvidenceField? f) && f.Equals(p.Value));
        }

        public override string ToString() => Name + " r" + Revision;
    }

    /// <summary>Why a unit of material information exists.</summary>
    public enum MaterialKind
    {
        /// <summary>Declared when the occurrence was opened.</summary>
        Initial = 0,

        /// <summary>An owner correction. It supersedes the earlier unit of the same name.</summary>
        Correction = 1,

        /// <summary>
        /// An owner-issued worsening. It RELATES to the earlier information and
        /// does not replace it: the old unit keeps its own delivery and review
        /// status, and the worse one is newly owed.
        /// </summary>
        Worsening = 2,

        /// <summary>
        /// The occurrence itself, added by the store when an owner declares no
        /// other material. Conveyed by any rendering of the message.
        /// </summary>
        Core = 3,
    }

    /// <summary>What an owner declares as owed information. The store allocates its identity.</summary>
    public sealed class MaterialDeclaration
    {
        public MaterialDeclaration(string name, FactValue value)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            Name = name;
            Value = value;
        }

        /// <summary>
        /// The clause name. A registered renderer declares which names its
        /// text conveys; for the lexicon renderer, a name is conveyed exactly
        /// when the resolved tier's text contains the <c>{name}</c>
        /// placeholder.
        /// </summary>
        public string Name { get; }
        public FactValue Value { get; }

        internal string Fingerprint => Name + "=" + (int)Value.Kind + ":" + Value.Invariant;
    }

    /// <summary>
    /// A reference to one immutable assertion: the episode that holds it and
    /// the unit's identity within that episode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is how continuing information keeps its identity across a
    /// reconnect.</b> A successor episode that represents an assertion its
    /// predecessor already held names it by this reference, and evidence for
    /// the assertion — a completion, a review — is found through the
    /// reference rather than copied once at the moment of reconnect. Matching
    /// by clause name, by raw unit number, by material revision or by equal
    /// rendered text is exactly what this exists to forbid.
    /// </para>
    /// </remarks>
    public readonly struct AssertionRef : IEquatable<AssertionRef>
    {
        public AssertionRef(EpisodeId episode, long unit)
        {
            if (episode.IsDefault) throw new ArgumentException("an assertion needs its episode", nameof(episode));
            if (unit <= 0) throw new ArgumentOutOfRangeException(nameof(unit));
            Episode = episode;
            Unit = unit;
        }

        public EpisodeId Episode { get; }
        public long Unit { get; }

        public bool Equals(AssertionRef other) => Episode == other.Episode && Unit == other.Unit;
        public override bool Equals(object? obj) => obj is AssertionRef other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Episode, Unit);
        public static bool operator ==(AssertionRef a, AssertionRef b) => a.Equals(b);
        public static bool operator !=(AssertionRef a, AssertionRef b) => !a.Equals(b);
        public override string ToString() => Episode + "/" + Unit;
    }

    /// <summary>
    /// An owner's declaration that one of the units it is declaring on a
    /// continuation IS an assertion the predecessor already held.
    /// </summary>
    /// <remarks>
    /// The store checks the link: the named unit must be declared with exactly
    /// the assertion's recorded value, and the assertion must be one the
    /// continuity view offered. A link that does not check is refused — an
    /// unproved equivalence transfers neither coverage nor permission.
    /// </remarks>
    public sealed class MaterialLink
    {
        public MaterialLink(string name, AssertionRef assertion)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            Name = name;
            Assertion = assertion;
        }

        /// <summary>The clause name of the declared unit, on the successor.</summary>
        public string Name { get; }

        /// <summary>The predecessor assertion it reuses, as the continuity view named it.</summary>
        public AssertionRef Assertion { get; }

        internal string Fingerprint => Name + "->" + Assertion;
        public override string ToString() => Name + " reuses " + Assertion;
    }

    /// <summary>
    /// One immutable owed assertion. Its identity is allocated by the store and
    /// never reused, so a late completion can only ever discharge what its plan
    /// actually carried.
    /// </summary>
    public sealed class MaterialUnit
    {
        internal MaterialUnit(long id, string name, FactValue value, MaterialKind kind,
                              long? supersedes, long? relatesTo, long introducedAtRevision,
                              AssertionRef? origin, AssertionRef root)
        {
            Id = id;
            Name = name;
            Value = value;
            Kind = kind;
            Supersedes = supersedes;
            RelatesTo = relatesTo;
            IntroducedAtRevision = introducedAtRevision;
            Origin = origin;
            Root = root;
        }

        /// <summary>Unique within its episode, allocated in order, never reused.</summary>
        public long Id { get; }
        public string Name { get; }
        public FactValue Value { get; }
        public MaterialKind Kind { get; }

        /// <summary>The unit this one corrects, if any.</summary>
        public long? Supersedes { get; }

        /// <summary>The unit a worsening is worse than, if any.</summary>
        public long? RelatesTo { get; }

        public long IntroducedAtRevision { get; }

        /// <summary>
        /// The predecessor assertion this unit continues, when a reconnect's
        /// owner declared it as the same information; null for information
        /// this episode introduced.
        /// </summary>
        public AssertionRef? Origin { get; }

        /// <summary>
        /// The assertion's identity across the whole occurrence: the first
        /// episode and unit that introduced it. Evidence for the assertion is
        /// resolved by this, in every episode that carries it.
        /// </summary>
        public AssertionRef Root { get; }

        /// <summary>The reserved clause name of the occurrence itself.</summary>
        public const string CoreName = "core";

        public override string ToString() => "#" + Id + " " + Name + "=" + Value.Invariant + " (" + Kind + ")";
    }
}
