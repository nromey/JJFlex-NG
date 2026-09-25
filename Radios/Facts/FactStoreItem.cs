#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.Facts
{
    /// <summary>
    /// The kinds of problem the store and journal can observe about
    /// THEMSELVES. None is a radio fact, none carries a radio capability, and
    /// none asserts any physical condition.
    /// </summary>
    public enum IssueKind
    {
        /// <summary>Saved history could not be read back, or a newer interval is missing.</summary>
        RecoveryGap = 0,

        /// <summary>Two records claim one identity with incompatible content. Both are kept.</summary>
        IdentityConflict = 1,

        /// <summary>A condition could not be registered for lack of room, so it is not monitored.</summary>
        RegistrationPressure = 2,

        /// <summary>A fact could not be retained in full for lack of room.</summary>
        RetentionPressure = 3,

        /// <summary>Detail was compacted or lost; some counts may be lower bounds.</summary>
        DetailLoss = 4,

        /// <summary>Some saved sources were skipped, inaccessible or over the load budget. Not corruption.</summary>
        IncompleteInventory = 5,

        /// <summary>This store's own history could not be saved.</summary>
        PersistenceFailure = 6,

        /// <summary>A publication was refused for authority or content reasons.</summary>
        IntegrityRefusal = 7,

        /// <summary>History from an older file format lacks evidence the current one requires.</summary>
        MigrationGap = 8,

        /// <summary>The reserved last summary, used when even the issue table filled.</summary>
        IssueOverflow = 9,
    }

    /// <summary>How well a count is known.</summary>
    public enum ExtentCertainty
    {
        Exact = 0,
        LowerBound = 1,
        Unknown = 2,
    }

    public enum IssueState
    {
        /// <summary>The problem is still true now.</summary>
        Active = 0,

        /// <summary>No longer true, and kept so the history of it stays readable.</summary>
        ResolvedWithHistory = 1,

        /// <summary>
        /// A standing limitation of the saved record — history from an older
        /// format that lacks evidence this one keeps. It does not change, so
        /// it is neither "still happening" nor "no longer happening": once
        /// the operator has reviewed it, it leaves the default view and stays
        /// in history, and it keeps qualifying every claim about the record.
        /// </summary>
        Limitation = 2,
    }

    /// <summary>One store issue as the store holds it. Mutable only under the store's gate.</summary>
    internal sealed class IssueRecord
    {
        public long Id;
        public IssueKind Kind;
        public string SourceKey = string.Empty;
        public string Source = string.Empty;
        public string Reason = string.Empty;
        public long Revision = 1;
        public long ReviewedRevision;
        public IssueState State = IssueState.Active;
        public long Count;
        public ExtentCertainty Extent = ExtentCertainty.Exact;
        public readonly List<string> Exemplars = new();
        public bool ExemplarsOverflowed;
        public readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        public bool SeenOverflowed;
        public bool FromPreviousProcess;

        public StoreIssueSnapshot Freeze(long projectionRevision) => new StoreIssueSnapshot(
            Id, Kind, SourceKey, Source, Reason, Revision, ReviewedRevision, State, Count, Extent,
            Exemplars.ToArray(), ExemplarsOverflowed, FromPreviousProcess, projectionRevision);
    }

    /// <summary>
    /// An immutable view of one store issue. A selectable row with ordinary
    /// detail, reached through the same projection as every fact.
    /// </summary>
    public sealed class StoreIssueSnapshot
    {
        internal StoreIssueSnapshot(long id, IssueKind kind, string sourceKey, string source, string reason, long revision,
                                    long reviewedRevision, IssueState state, long count, ExtentCertainty extent,
                                    IReadOnlyList<string> exemplars, bool exemplarsOverflowed, bool fromPreviousProcess,
                                    long projectionRevision)
        {
            Id = id;
            Kind = kind;
            SourceKey = sourceKey;
            Source = source;
            Reason = reason;
            Revision = revision;
            ReviewedRevision = reviewedRevision;
            State = state;
            Count = count;
            Extent = extent;
            Exemplars = exemplars;
            ExemplarsOverflowed = exemplarsOverflowed;
            FromPreviousProcess = fromPreviousProcess;
            ProjectionRevision = projectionRevision;
        }

        public long Id { get; }
        public IssueKind Kind { get; }

        /// <summary>The stable key it deduplicates by: kind plus affected source or scope.</summary>
        public string SourceKey { get; }

        /// <summary>The affected source, shard or scope, for display.</summary>
        public string Source { get; }

        /// <summary>A diagnostic reason. Operator wording comes from the lexicon, keyed by kind.</summary>
        public string Reason { get; }

        /// <summary>Advances whenever NEW information about the problem arrives.</summary>
        public long Revision { get; }

        /// <summary>The revision the operator explicitly reviewed on a displayed snapshot.</summary>
        public long ReviewedRevision { get; }

        public IssueState State { get; }
        public long Count { get; }
        public ExtentCertainty Extent { get; }
        public IReadOnlyList<string> Exemplars { get; }
        public bool ExemplarsOverflowed { get; }
        public bool FromPreviousProcess { get; }
        public long ProjectionRevision { get; }

        /// <summary>
        /// Reviewing records that the displayed information was reviewed. It
        /// does not repair anything, free anything, or make an active fault
        /// healthy.
        /// </summary>
        public bool Unreviewed => ReviewedRevision < Revision;

        /// <summary>On the default view: still active, or carrying information nobody has reviewed.</summary>
        public bool Outstanding => State == IssueState.Active || Unreviewed;

        /// <summary>
        /// Kinds that mean some history is missing or uncertain, so the
        /// inventory is not complete. A standing limitation keeps affecting it
        /// after review: reading about missing evidence does not supply it.
        /// </summary>
        public bool AffectsInventory =>
            State != IssueState.ResolvedWithHistory
            && Kind is IssueKind.RecoveryGap or IssueKind.IdentityConflict or IssueKind.IncompleteInventory
                or IssueKind.MigrationGap;

        /// <summary>Kinds that mean information was lost or compacted rather than delivered.</summary>
        public bool IsLoss =>
            Kind is IssueKind.RetentionPressure or IssueKind.DetailLoss or IssueKind.IssueOverflow
                or IssueKind.RecoveryGap;

        public override string ToString() => Kind + " (" + Source + ") r" + Revision + (Unreviewed ? " unreviewed" : "");
    }

    /// <summary>Which list the surface shows.</summary>
    public enum FactView
    {
        /// <summary>Owed information and outstanding store issues.</summary>
        Pending = 0,

        /// <summary>History: perishable events, ended premises, restored records, and every retained issue.</summary>
        History = 1,
    }

    public enum ItemKind
    {
        Fact = 0,
        Issue = 1,
    }

    /// <summary>
    /// One immutable row of the discriminated projection. Stable identity, so
    /// selection survives an update without being moved.
    /// </summary>
    public sealed class ItemSnapshot
    {
        internal ItemSnapshot(string itemId, FactSnapshot fact)
        {
            ItemId = itemId;
            Kind = ItemKind.Fact;
            Fact = fact;
        }

        internal ItemSnapshot(string itemId, StoreIssueSnapshot issue)
        {
            ItemId = itemId;
            Kind = ItemKind.Issue;
            Issue = issue;
        }

        public string ItemId { get; }
        public ItemKind Kind { get; }
        public FactSnapshot? Fact { get; }
        public StoreIssueSnapshot? Issue { get; }

        /// <summary>Filled by the presenter: the row's words, from the lexicon.</summary>
        public string RowText { get; internal set; } = string.Empty;

        public static string IdFor(EpisodeId id, int variant) =>
            "f:" + id + (variant > 0 ? "#v" + variant.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");

        public static string IdFor(long issueId) =>
            "i:" + issueId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override string ToString() => ItemId;
    }

    /// <summary>
    /// The truth conditions of the surface, as structured data. The presenter
    /// selects approved wording roles from these; it never infers them from
    /// final text, and a zero row count never implies universal delivery.
    /// </summary>
    public sealed class FactListPredicates
    {
        internal FactListPredicates(
            bool loading, bool journalAttached, bool filtered, int excludedOutstanding, int pendingFactsInScope,
            int outstandingIssues, bool emptyPendingInScope, bool completeInventory, bool savedThroughCurrent,
            bool presentationComplete, int reviewedNotDelivered, int forgettableUnpresented, bool unaccountedLoss)
        {
            Loading = loading;
            JournalAttached = journalAttached;
            Filtered = filtered;
            ExcludedOutstanding = excludedOutstanding;
            PendingFactsInScope = pendingFactsInScope;
            OutstandingIssues = outstandingIssues;
            EmptyPendingInScope = emptyPendingInScope;
            CompleteInventory = completeInventory;
            SavedThroughCurrent = savedThroughCurrent;
            PresentationComplete = presentationComplete;
            ReviewedNotDelivered = reviewedNotDelivered;
            ForgettableUnpresented = forgettableUnpresented;
            UnaccountedLoss = unaccountedLoss;
        }

        /// <summary>History is still being loaded. A distinct state, never "empty".</summary>
        public bool Loading { get; }

        /// <summary>A journal is attached. Without one nothing can be said about saved history.</summary>
        public bool JournalAttached { get; }

        /// <summary>A station filter is narrowing the view.</summary>
        public bool Filtered { get; }

        /// <summary>Outstanding facts the filter hides. Store issues are never hidden by a filter.</summary>
        public int ExcludedOutstanding { get; }

        public int PendingFactsInScope { get; }
        public int OutstandingIssues { get; }

        /// <summary>
        /// Nothing owed in the stated scope, no outstanding issue, no
        /// unaccounted loss, and the projection is complete.
        /// </summary>
        public bool EmptyPendingInScope { get; }

        /// <summary>
        /// Every relevant source accounted for: no inaccessible, unsupported,
        /// corrupt, conflicting or skipped live source, no budget left
        /// unprocessed, no unresolved recovery interval. Claims remain limited
        /// to recorded history.
        /// </summary>
        public bool CompleteInventory { get; }

        /// <summary>
        /// Every mutation needing persistence has committed and no write is
        /// failing. Says nothing about a separate history gap, nor about the
        /// next event surviving a crash.
        /// </summary>
        public bool SavedThroughCurrent { get; }

        /// <summary>
        /// Every required unit in the retained set has attributable,
        /// undisputed completion. Review, a tone request, a request-only
        /// attempt, a forgettable omission and lost detail do not count.
        /// </summary>
        public bool PresentationComplete { get; }

        /// <summary>Facts in scope discharged by review rather than presentation.</summary>
        public int ReviewedNotDelivered { get; }

        /// <summary>Forgettable messages in scope that were not presented. Not owed — but not delivered either.</summary>
        public int ForgettableUnpresented { get; }

        /// <summary>A loss or retention summary is outstanding.</summary>
        public bool UnaccountedLoss { get; }
    }

    /// <summary>
    /// One immutable projection: rows, predicates and summary, all from the
    /// same projection revision and scope.
    /// </summary>
    public sealed class FactListSnapshot
    {
        internal FactListSnapshot(long projectionRevision, FactView view, string? stationFilter,
                                  IReadOnlyList<ItemSnapshot> items, FactListPredicates predicates)
        {
            ProjectionRevision = projectionRevision;
            View = view;
            StationFilter = stationFilter;
            Items = items;
            Predicates = predicates;
        }

        public long ProjectionRevision { get; }
        public FactView View { get; }
        public string? StationFilter { get; }
        public IReadOnlyList<ItemSnapshot> Items { get; }
        public FactListPredicates Predicates { get; }

        public ItemSnapshot? Find(string itemId) => Items.FirstOrDefault(i => i.ItemId == itemId);
    }

    /// <summary>
    /// An opaque token binding one displayed detail to the exact information
    /// it represented. Issued to one view, invalidated when that view closes.
    /// </summary>
    /// <remarks>
    /// <b>Issuing a token is not evidence of a rendered surface.</b> It becomes
    /// the view's active shown token only when the UI says it has installed the
    /// text into the read-only control.
    /// </remarks>
    public sealed class DisplayToken
    {
        internal DisplayToken(FactStore store, long viewId, long id, string itemId, EpisodeId? episode, int variant,
                              long? issueId, IReadOnlyCollection<long> represented, long issueRevision,
                              string contentFingerprint, string materialFingerprint, long projectionRevision)
        {
            Store = store;
            ViewId = viewId;
            Id = id;
            ItemId = itemId;
            Episode = episode;
            Variant = variant;
            IssueId = issueId;
            Represented = represented;
            IssueRevision = issueRevision;
            ContentFingerprint = contentFingerprint;
            MaterialFingerprint = materialFingerprint;
            ProjectionRevision = projectionRevision;
        }

        internal FactStore Store { get; }
        internal long ViewId { get; }
        internal EpisodeId? Episode { get; }
        internal int Variant { get; }
        internal long? IssueId { get; }
        internal long IssueRevision { get; }
        internal string ContentFingerprint { get; }

        /// <summary>The material the shown text was rendered from. A selected read is bound to this.</summary>
        internal string MaterialFingerprint { get; }

        public long Id { get; }
        public string ItemId { get; }

        /// <summary>The material units this display represented. Review covers these and nothing else.</summary>
        public IReadOnlyCollection<long> Represented { get; }

        public long ProjectionRevision { get; }
    }

    /// <summary>
    /// The exact detail a view put in front of the operator, with the token
    /// that names it.
    /// </summary>
    public sealed class RenderedDetailSnapshot
    {
        internal RenderedDetailSnapshot(string itemId, string text, DisplayToken token, bool historical,
                                        bool omittedSomething)
        {
            ItemId = itemId;
            Text = text;
            Token = token;
            Historical = historical;
            OmittedRequiredDetail = omittedSomething;
        }

        public string ItemId { get; }

        /// <summary>The formatted text assigned to the detail control.</summary>
        public string Text { get; }

        public DisplayToken Token { get; }

        /// <summary>This detail describes history, not a current condition.</summary>
        public bool Historical { get; }

        /// <summary>Something required was left out of this display, so reviewing it cannot cover that.</summary>
        public bool OmittedRequiredDetail { get; }

        public IReadOnlyCollection<long> Represented => Token.Represented;
    }

    public enum ReviewOutcome
    {
        /// <summary>The represented information is now recorded as reviewed.</summary>
        Reviewed = 0,

        /// <summary>Everything represented was already reviewed or presented.</summary>
        NothingNew = 1,

        /// <summary>The item is gone, conflicted, or changed so the token no longer resolves. Nothing was reviewed.</summary>
        StaleTarget = 2,

        /// <summary>The token was issued but never installed as shown.</summary>
        NotInstalled = 3,

        /// <summary>The view that owned the token has closed.</summary>
        ViewClosed = 4,

        /// <summary>A token from another view or another store.</summary>
        Foreign = 5,
    }

    public enum ResumeOutcome
    {
        Resumed = 0,

        /// <summary>History with no current owner. Resume is not available for it.</summary>
        Unavailable = 1,

        /// <summary>Nothing represented is paused.</summary>
        NothingPaused = 2,

        StaleTarget = 3,
        NotInstalled = 4,
        ViewClosed = 5,
        Foreign = 6,

        /// <summary>A quiet was observed after the operator chose to resume. The later quiet wins.</summary>
        OvertakenByQuiet = 7,
    }

    /// <summary>
    /// A selected automatic-resume action, positioned in the ordered stream
    /// when the operator took it. Committed separately, so a quiet observed in
    /// between wins.
    /// </summary>
    public sealed class ResumeRequest
    {
        internal ResumeRequest(DisplayToken token, long actionSequence)
        {
            Token = token;
            ActionSequence = actionSequence;
        }

        internal DisplayToken Token { get; }
        public long ActionSequence { get; }
    }
}
