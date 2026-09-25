#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Radios.Facts
{
    /// <summary>
    /// The headless presenter the real dialog uses: projection, wording roles,
    /// detail rendering and the displayed-snapshot actions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The application owns the formatter; the window does not infer meaning
    /// from enum names.</b> Every word here is a lexicon lookup, and which
    /// lookup is chosen is decided from structured predicates — never from the
    /// final translated text, and never from a row count.
    /// </para>
    /// <para>
    /// <b>What a test of this class proves, and what it does not.</b> It proves
    /// projection and action logic. It does not prove that a screen reader or a
    /// braille display can reach anything; that is the realized dialog's test.
    /// </para>
    /// </remarks>
    public sealed class FactListPresenter
    {
        public FactListPresenter(FactStore store)
        {
            Store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public FactStore Store { get; }

        /// <summary>The most material lines one detail shows. Units beyond it are not represented, and say so.</summary>
        public const int MaxDetailMaterialLines = 8;

        public FactListView OpenView() => new FactListView(this, Store.OpenView());

        // ────────────────────────────────────────────────────────────────
        //  Wording roles — each returns a lexicon KEY chosen from structure
        // ────────────────────────────────────────────────────────────────

        /// <summary>Which state wording a fact row may truthfully use.</summary>
        public static string StateRole(FactSnapshot fact)
        {
            if (fact.ConflictVariant) return "facts.state.conflicting";

            // A pause is described only with its OWN cause. "Because you asked
            // for quiet" is said only when the operator did; "it had been
            // paused" only when it had; and a lost record is not a pause at
            // all, so it does not borrow the word.
            switch (fact.Pause)
            {
                case PauseCause.OperatorQuiet: return "facts.state.paused";
                case PauseCause.UnknownCancellation: return "facts.state.paused_unknown_cause";
                case PauseCause.ContinuityInherited: return "facts.state.paused_continuity";
                case PauseCause.ContinuityUnknown: return "facts.state.paused_continuity_unknown";
                case PauseCause.ContinuityLost: return "facts.state.held_continuity_lost";
                case PauseCause.ContinuityAcrossRestart: return "facts.state.held_continuity_restart";
                case PauseCause.OnsetNotEstablished: return "facts.state.held_onset_not_established";
                case PauseCause.LegacyUnknownCause: return "facts.state.paused_cause_not_recorded";
            }

            if (fact.Classification == DeliveryClassification.Unclassified) return "facts.state.silent_no_metadata";
            if (fact.Classification == DeliveryClassification.TextOnly) return "facts.state.not_a_message";

            if (fact.RestoredFromDisk)
                return fact.Validity.State is ValidityState.Ended or ValidityState.Superseded
                    ? (fact.Validity.IsResolved ? "facts.state.resolved" : "facts.state.historical")
                    : "facts.state.unknown";

            return fact.Validity.State switch
            {
                // "Still true now" only with a live owner behind it.
                ValidityState.Current => fact.IsLive ? "facts.state.current" : "facts.state.unknown",
                ValidityState.Unknown => "facts.state.unknown",
                _ => fact.Validity.IsResolved ? "facts.state.resolved" : "facts.state.historical",
            };
        }

        /// <summary>Which delivery wording the newest attempt's evidence supports.</summary>
        public static string DeliveryRole(FactSnapshot fact)
        {
            if (fact.Attempts.Count == 0)
            {
                // A schema-1 record: what the old aggregate said is stated as
                // its claim, and none of the three readings is verified.
                if (fact.Legacy)
                    return fact.LegacyClaim switch
                    {
                        LegacyDeliveryClaim.ClaimedDelivered => "facts.delivery.legacy_claimed_delivered",
                        LegacyDeliveryClaim.ClaimedUndelivered => "facts.delivery.legacy_claimed_undelivered",
                        _ => "facts.delivery.legacy_unverified",
                    };
                return "facts.delivery.not_attempted";
            }

            AttemptSnapshot last = fact.Attempts[fact.Attempts.Count - 1];
            switch (last.Disposition)
            {
                case AttemptDisposition.NotStarted:
                    return last.NotStarted switch
                    {
                        NotStartedReason.QuietAfterPermission => "facts.delivery.not_started_quiet",
                        NotStartedReason.PausedUnknownCause => "facts.delivery.not_started_paused",
                        NotStartedReason.Superseded or NotStartedReason.CatalogChanged
                            or NotStartedReason.HistoricalRecordChanged => "facts.delivery.withdrawn_superseded",
                        // "Because what it described had ended" only when the
                        // OWNER said the condition resolved.
                        NotStartedReason.NotCurrent when fact.Validity.IsResolved => "facts.delivery.withdrawn",
                        _ => "facts.delivery.withdrawn_context_ended",
                    };
                case AttemptDisposition.Allocated:
                case AttemptDisposition.InFlight: return "facts.delivery.started";
                case AttemptDisposition.RequestedOnly: return "facts.delivery.requested_only";
                case AttemptDisposition.RequestFailed: return "facts.delivery.request_failed";
                case AttemptDisposition.Refused: return "facts.delivery.refused";
                case AttemptDisposition.Partial: return "facts.delivery.partial";
                case AttemptDisposition.Completed:
                    {
                        // "Read out in full" only when this attempt's words
                        // carried every required unit.
                        var carried = new HashSet<long>(last.EstablishedCoverage);
                        return fact.Required.All(carried.Contains)
                            ? "facts.delivery.completed"
                            : "facts.delivery.completed_short";
                    }
                case AttemptDisposition.CancelledByUs: return "facts.delivery.cancelled_known";
                case AttemptDisposition.CancelledUnknownCause: return "facts.delivery.unknown";
                case AttemptDisposition.Unobservable: return "facts.delivery.unobservable";
                case AttemptDisposition.Disputed: return "facts.delivery.disputed";
                case AttemptDisposition.Interrupted: return "facts.delivery.interrupted";
                default: return "facts.delivery.not_attempted";
            }
        }

        /// <summary>Which receipt wording the receipt evidence supports, or null when there is nothing to say.</summary>
        public static string? ReceiptRole(FactSnapshot fact) => fact.Receipt.State switch
        {
            // A request is not a tone anybody heard, and not even a tone that
            // played.
            ReceiptState.Requested => "facts.receipt.request_issued",
            ReceiptState.PlaybackReported => "facts.receipt.requested",
            ReceiptState.Unavailable => "facts.receipt.unavailable",
            ReceiptState.Suppressed => "facts.receipt.suppressed",
            ReceiptState.Withheld => "facts.receipt.withheld",
            ReceiptState.OutcomeUnknown => "facts.receipt.outcome_unknown",
            _ => null,
        };

        /// <summary>
        /// The empty-state wording roles for a projection, in order. Empty when
        /// the list has rows. <b>A zero row count never becomes a claim of
        /// universal delivery</b> — and neither does anything else: the store
        /// can establish that every item on RECORD was read out, and cannot
        /// establish that the radio had nothing else to say, so the sentence
        /// that claims the latter is never chosen by this code.
        /// </summary>
        public static IReadOnlyList<string> EmptyStateRoles(FactListSnapshot snapshot)
        {
            var roles = new List<string>();
            FactListPredicates p = snapshot.Predicates;

            if (p.Loading)
            {
                roles.Add("facts.window.loading");
                return roles;
            }
            if (snapshot.Items.Count > 0) return roles;

            if (snapshot.View == FactView.History)
            {
                roles.Add("facts.window.nothing_historical");
                return roles;
            }

            bool recordedDelivered = p.EmptyPendingInScope && p.PresentationComplete && p.CompleteInventory
                                     && !p.Filtered && p.ReviewedNotDelivered == 0 && p.ForgettableUnpresented == 0;
            roles.Add(recordedDelivered ? "facts.window.nothing_pending_recorded" : "facts.window.nothing_pending_unverified");
            if (p.ReviewedNotDelivered > 0) roles.Add("facts.window.nothing_pending_reviewed");
            if (p.ForgettableUnpresented > 0) roles.Add("facts.window.nothing_pending_forgettable");
            if (p.JournalAttached && !p.SavedThroughCurrent) roles.Add("facts.storage.unsaved");
            return roles;
        }

        /// <summary>The Status dialog's one-line summary, from the same projection the list uses.</summary>
        public string StatusSummaryText() => StatusSummaryText(Store.Project(FactView.Pending, null));

        public static string StatusSummaryText(FactListSnapshot snapshot) =>
            string.Join(" ", StatusSummaryRoles(snapshot).Select(r => Lexicon.Get(r.Key, ("count", r.Count))));

        public static IReadOnlyList<(string Key, int Count)> StatusSummaryRoles(FactListSnapshot snapshot)
        {
            FactListPredicates p = snapshot.Predicates;
            var roles = new List<(string, int)>();
            if (p.OutstandingIssues > 0) roles.Add(("facts.status.problems_present", p.OutstandingIssues));
            if (p.PendingFactsInScope == 1) roles.Add(("facts.status.pending_summary", 1));
            else if (p.PendingFactsInScope > 1) roles.Add(("facts.status.pending_summary_plural", p.PendingFactsInScope));
            if (roles.Count == 0)
            {
                bool everything = p.EmptyPendingInScope && p.PresentationComplete && p.CompleteInventory;
                roles.Add((everything ? "facts.status.nothing_pending" : "facts.status.nothing_waiting", 0));
            }
            return roles;
        }

        /// <summary>
        /// A signature of everything a row's detail would say. When it changes
        /// for the item on screen, the surface signals "changed" and offers a
        /// refresh — it never rewrites the text the operator is reading.
        /// </summary>
        public static string DetailSignature(ItemSnapshot item)
        {
            if (item.Kind == ItemKind.Issue)
                return "i|" + item.Issue!.Revision + "|" + item.Issue.State + "|" + item.Issue.Count;
            FactSnapshot f = item.Fact!;
            AttemptSnapshot? last = f.Attempts.Count > 0 ? f.Attempts[f.Attempts.Count - 1] : null;
            return "f|" + f.ContentFingerprint + "|" + f.Pause + "|" + f.Attempts.Count + "|" + last?.Disposition
                   + "|" + f.Receipt.State + "|" + string.Join(",", f.Unpresented);
        }

        public static string IssueKindRole(IssueKind kind) => kind switch
        {
            IssueKind.RecoveryGap => "facts.issue.kind.recovery_gap",
            IssueKind.IdentityConflict => "facts.issue.kind.identity_conflict",
            IssueKind.RegistrationPressure => "facts.issue.kind.registration_pressure",
            IssueKind.RetentionPressure => "facts.issue.kind.retention_pressure",
            IssueKind.DetailLoss => "facts.issue.kind.detail_loss",
            IssueKind.IncompleteInventory => "facts.issue.kind.incomplete_inventory",
            IssueKind.PersistenceFailure => "facts.issue.kind.persistence_failure",
            IssueKind.IntegrityRefusal => "facts.issue.kind.integrity_refusal",
            IssueKind.MigrationGap => "facts.issue.kind.migration_gap",
            _ => "facts.issue.kind.issue_overflow",
        };

        // ────────────────────────────────────────────────────────────────
        //  Rows and detail
        // ────────────────────────────────────────────────────────────────

        /// <summary>Which state wording an issue row may truthfully use: happening, no longer, or a standing limitation.</summary>
        public static string IssueStateRole(IssueState state) => state switch
        {
            IssueState.Active => "facts.issue.state.active",
            IssueState.Limitation => "facts.issue.state.limitation",
            _ => "facts.issue.state.resolved",
        };

        internal static string RowText(ItemSnapshot item)
        {
            if (item.Kind == ItemKind.Issue)
            {
                StoreIssueSnapshot issue = item.Issue!;
                return Lexicon.Get("facts.row.issue_summary",
                    ("problem", Lexicon.Get(IssueKindRole(issue.Kind))),
                    ("state", Lexicon.Get(IssueStateRole(issue.State))));
            }

            FactSnapshot fact = item.Fact!;
            string station = fact.RadioIdentity ?? Lexicon.Get("facts.row.unknown_station");
            return Lexicon.Get("facts.row.summary",
                ("station", station),
                ("when", fact.ObservedUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)),
                ("state", Lexicon.Get(StateRole(fact))));
        }

        /// <summary>
        /// Compose the exact detail text for a fact, and the exact material
        /// units it represents.
        /// </summary>
        internal (string Text, HashSet<long> Represented, bool Omitted, bool Historical) FactDetail(FactSnapshot fact)
        {
            var lines = new List<string> { RowText(new ItemSnapshot(string.Empty, fact)) };
            var represented = new HashSet<long>();
            var required = new HashSet<long>(fact.Required);
            bool historical = fact.IsHistorical || !fact.IsLive;

            // The occurrence itself is what the row says: that it happened, where and when.
            MaterialUnit core = fact.Materials[0];
            represented.Add(core.Id);

            FactRendering? rendering = Store.Renderer.Render(fact, VerbosityLevel.Chatty, historical, Store.Catalog);
            if (rendering != null)
            {
                lines.Add(rendering.Text);
                // The same mapping a plan uses: the newest unit of each clause
                // the words carry. An older unit of that clause is listed below
                // with its own value, so reviewing covers it only when shown.
                represented.UnionWith(FactStore.PlanCoverage(fact, rendering).Coverage.Keys);
            }
            else
            {
                lines.Add(Lexicon.Get("facts.detail.no_rendering"));
            }

            if (!string.IsNullOrEmpty(fact.Detail)) lines.Add(fact.Detail);
            if (fact.DetailTruncated) lines.Add(Lexicon.Get("facts.detail.truncated"));

            // Every required unit not already carried by the sentence gets its
            // own line, up to a bound. Anything past the bound is NOT
            // represented, and the display says so, so reviewing this detail
            // cannot cover what it never showed.
            var unlisted = fact.Materials.Where(u => required.Contains(u.Id) && !represented.Contains(u.Id)).ToList();
            int shown = 0;
            foreach (MaterialUnit unit in unlisted)
            {
                if (shown == MaxDetailMaterialLines) break;
                lines.Add(Lexicon.Get("facts.detail.material_line", ("name", unit.Name), ("value", unit.Value.Invariant)));
                represented.Add(unit.Id);
                shown++;
            }
            bool omitted = unlisted.Count > shown;
            if (omitted) lines.Add(Lexicon.Get("facts.detail.more_not_shown", ("count", unlisted.Count - shown)));

            if (fact.RestoredFromDisk) lines.Add(Lexicon.Get("facts.detail.restored"));
            if (fact.Legacy)
                lines.Add(Lexicon.Get(fact.LegacyClaim == LegacyDeliveryClaim.ClaimedDelivered
                    ? "facts.detail.legacy_claimed" : "facts.detail.legacy"));
            if (fact.ConflictVariant) lines.Add(Lexicon.Get("facts.detail.conflict"));

            lines.Add(Lexicon.Get(DeliveryRole(fact)));
            string? receipt = ReceiptRole(fact);
            if (receipt != null) lines.Add(Lexicon.Get(receipt));

            return (string.Join(Environment.NewLine + Environment.NewLine, lines), represented, omitted, historical);
        }

        internal static string IssueDetail(StoreIssueSnapshot issue)
        {
            var lines = new List<string>
            {
                Lexicon.Get(IssueKindRole(issue.Kind)),
                Lexicon.Get(IssueStateRole(issue.State)),
                issue.Extent switch
                {
                    ExtentCertainty.Exact => Lexicon.Get("facts.issue.count.exact", ("count", issue.Count)),
                    ExtentCertainty.LowerBound => Lexicon.Get("facts.issue.count.lower_bound", ("count", issue.Count)),
                    _ => Lexicon.Get("facts.issue.count.unknown"),
                },
                Lexicon.Get("facts.issue.source", ("source", issue.Source)),
            };
            foreach (string exemplar in issue.Exemplars) lines.Add(exemplar);
            if (issue.ExemplarsOverflowed) lines.Add(Lexicon.Get("facts.issue.exemplars_more"));
            if (issue.FromPreviousProcess) lines.Add(Lexicon.Get("facts.detail.restored_issue"));
            return string.Join(Environment.NewLine + Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// One open view: the only thing that issues display tokens, and the only
    /// route by which review, resume and a selected read are taken.
    /// </summary>
    /// <remarks>
    /// Closing it invalidates every token it issued, so a stale window can
    /// never review anything after it is gone.
    /// </remarks>
    public sealed class FactListView : IDisposable
    {
        private readonly FactListPresenter _presenter;
        private readonly long _viewId;
        private bool _closed;

        internal FactListView(FactListPresenter presenter, long viewId)
        {
            _presenter = presenter;
            _viewId = viewId;
        }

        public FactStore Store => _presenter.Store;
        public bool IsClosed => _closed;

        /// <summary>The immutable projection, rows worded, all from one projection revision.</summary>
        public FactListSnapshot Snapshot(FactView view, string? stationFilter = null)
        {
            FactListSnapshot snapshot = Store.Project(view, stationFilter);
            foreach (ItemSnapshot item in snapshot.Items) item.RowText = FactListPresenter.RowText(item);
            return snapshot;
        }

        /// <summary>
        /// Render the detail for one row and issue its token. The token is not
        /// yet the shown token: call <see cref="Installed"/> once the text is
        /// actually in the read-only control.
        /// </summary>
        public RenderedDetailSnapshot? RenderDetail(ItemSnapshot item)
        {
            if (_closed || item == null) return null;

            if (item.Kind == ItemKind.Issue)
            {
                StoreIssueSnapshot? issue = Store.FindIssue(item.Issue!.Id);
                if (issue == null) return null;
                string signature = "issue:" + issue.Id + ":" + issue.Revision;
                DisplayToken? token = Store.IssueToken(_viewId, item.ItemId, null, 0, issue.Id, Array.Empty<long>(),
                                                       issue.Revision, signature, signature, issue.ProjectionRevision);
                return token == null ? null
                    : new RenderedDetailSnapshot(item.ItemId, FactListPresenter.IssueDetail(issue), token, false, false);
            }

            FactSnapshot? fact = Store.FindVariant(item.Fact!.Id, item.Fact.VariantIndex);
            if (fact == null) return null;
            var (text, represented, omitted, historical) = _presenter.FactDetail(fact);
            DisplayToken? factToken = Store.IssueToken(_viewId, item.ItemId, fact.Id, fact.VariantIndex, null, represented,
                                                       0, fact.ContentFingerprint, fact.MaterialFingerprint, fact.ProjectionRevision);
            return factToken == null ? null : new RenderedDetailSnapshot(item.ItemId, text, factToken, historical, omitted);
        }

        /// <summary>The UI has put this detail's text into the read-only control. Only now is its token the shown one.</summary>
        public bool Installed(RenderedDetailSnapshot detail) => !_closed && detail != null && Store.InstallToken(detail.Token);

        /// <summary>Record that the information in THIS displayed snapshot was reviewed — and nothing newer.</summary>
        public ReviewOutcome Review(DisplayToken token) => _closed ? ReviewOutcome.ViewClosed : Store.Review(token);

        /// <summary>Take the operator's resume action now; commit it separately.</summary>
        public ResumeRequest? BeginResume(DisplayToken token, out ResumeOutcome outcome)
        {
            if (_closed) { outcome = ResumeOutcome.ViewClosed; return null; }
            return Store.BeginResume(token, out outcome);
        }

        public ResumeOutcome CommitResume(ResumeRequest request) =>
            _closed ? ResumeOutcome.ViewClosed : Store.CommitResume(request);

        /// <summary>Resume automatic delivery of the displayed, paused information — only that.</summary>
        public ResumeOutcome Resume(DisplayToken token)
        {
            ResumeRequest? request = BeginResume(token, out ResumeOutcome outcome);
            return request == null ? outcome : CommitResume(request);
        }

        /// <summary>A single-use permission to read this displayed record, for the scheduler's selected read.</summary>
        public SelectedReadGrant? RequestRead(DisplayToken token, out ReviewOutcome outcome)
        {
            if (_closed) { outcome = ReviewOutcome.ViewClosed; return null; }
            return Store.RequestRead(token, out outcome);
        }

        /// <summary>The empty-state wording for a projection, as text lines from the chosen roles.</summary>
        public static string EmptyStateText(FactListSnapshot snapshot) =>
            string.Join(Environment.NewLine + Environment.NewLine,
                        FactListPresenter.EmptyStateRoles(snapshot).Select(r => Lexicon.Get(r)));

        /// <summary>The line naming outstanding items a filter hides, or null.</summary>
        public static string? ExcludedText(FactListSnapshot snapshot) =>
            snapshot.Predicates.ExcludedOutstanding > 0
                ? Lexicon.Get("facts.window.excluded_by_filter", ("count", snapshot.Predicates.ExcludedOutstanding))
                : null;

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            Store.CloseView(_viewId);
        }

        public void Dispose() => Close();
    }
}
