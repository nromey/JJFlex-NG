#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios;
using Radios.Facts;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// E-series, headless: the projection, wording roles and displayed-snapshot
    /// actions the real dialog consumes.
    /// </summary>
    /// <remarks>
    /// <b>What these prove and what they do not (#622).</b> They prove the
    /// presenter's projection and action logic, through the production journal
    /// and store. They do NOT prove that a screen reader or braille display can
    /// reach a row, that the real window binds these rows, or that its button
    /// sends the installed token — those are the realized-dialog tests in
    /// JJFlexWpf.Tests, which need a desk run. No name here claims otherwise.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class FactSurfacePresenterTests
    {
        private static readonly DateTime T0 = FactKit.T0;

        [Fact]
        public void E1_Presenter_CorruptHistoryAndPressureProjectAsSelectableIssueRows_WithNoNormalFacts()
        {
            using var dir = new TempFactDir();
            File.WriteAllText(Path.Combine(dir.Path, "facts-broken.json"), "{ this is not json");

            var kit = new FactKit();
            var journal = new FactJournal(kit.Store, dir.Path);
            journal.TakeLease();
            journal.LoadHistory();
            journal.Dispose();

            using FactListView view = new FactListPresenter(kit.Store).OpenView();
            FactListSnapshot list = view.Snapshot(FactView.Pending);

            Assert.Empty(kit.Store.All);                                   // no normal facts at all
            ItemSnapshot gap = Assert.Single(list.Items, i => i.Issue?.Kind == IssueKind.RecoveryGap);
            Assert.False(string.IsNullOrWhiteSpace(gap.RowText));
            Assert.False(Lexicon.LooksLikeKey(gap.RowText));
            Assert.False(list.Predicates.EmptyPendingInScope);
            Assert.False(list.Predicates.CompleteInventory);
            Assert.Empty(FactListPresenter.EmptyStateRoles(list));          // there IS a row, so no empty state
            RenderedDetailSnapshot detail = view.RenderDetail(gap)!;
            Assert.False(string.IsNullOrWhiteSpace(detail.Text));
            Assert.Contains("facts-broken.json", detail.Text, StringComparison.Ordinal);

            // Capacity exhaustion through production registration, before any fact.
            var full = new FactKit();
            FactSession session = full.Session();
            for (int i = 0; i < FactStoreCapacity.MaxCurrentSlots; i++) full.HotSlot(session, "c" + i);
            RegistrationResult refused = full.Registry.Register(full.Hot, session, FactKit.Temperature, new ConditionKey("one too many"));
            Assert.Equal(RegistrationOutcome.Exhausted, refused.Outcome);
            using FactListView fullView = new FactListPresenter(full.Store).OpenView();
            FactListSnapshot fullList = fullView.Snapshot(FactView.Pending);
            Assert.Empty(full.Store.All);
            Assert.Single(fullList.Items, i => i.Issue?.Kind == IssueKind.RegistrationPressure);
            Assert.Equal("facts.status.problems_present", FactListPresenter.StatusSummaryRoles(fullList).First().Key);

            // POSITIVE CONTROL: an ordinary fact through the same route is a row.
            var ok = new FactKit();
            EpisodeId id = FactKit.OpenHot(ok.HotSlot(ok.Session())).Handle!.Id;
            using FactListView okView = new FactListPresenter(ok.Store).OpenView();
            Assert.Single(okView.Snapshot(FactView.Pending).Items, i => i.Fact?.Id == id);
        }

        [Fact]
        public void E2_Presenter_ReviewCoversTheInstalledSnapshotAndNothingNewer()
        {
            var kit = new FactKit();
            SlotPublisher publisher = kit.HotSlot(kit.Session());
            PublicationResult opened = FactKit.OpenHot(publisher, 70m);
            EpisodeId id = opened.Handle!.Id;
            using FactListView view = new FactListPresenter(kit.Store).OpenView();

            // Revision 1 displayed and installed.
            RenderedDetailSnapshot shown = view.RenderDetail(view.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == id))!;
            Assert.Equal(ReviewOutcome.NotInstalled, view.Review(shown.Token));   // issuing is not showing
            Assert.True(view.Installed(shown));

            // Revision 2 arrives behind it.
            publisher.Update(opened.Handle, FactKit.Capture(publisher, FactKit.Temp(72m)),
                FactTransition.Correction(new[] { new MaterialDeclaration("temperature", FactValue.Of(72m)) }),
                kit.Store.Find(id)!.Revision);

            Assert.Equal(ReviewOutcome.Reviewed, view.Review(shown.Token));
            FactSnapshot afterOld = kit.Store.Find(id)!;
            MaterialUnit newer = afterOld.Materials.Last(m => m.Name == "temperature");
            Assert.Contains(newer.Id, afterOld.Unpresented);                     // revision 2 stays owed
            Assert.True(afterOld.IsPending);

            // POSITIVE CONTROL: refresh, see the new detail, review it.
            RenderedDetailSnapshot refreshed = view.RenderDetail(view.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == id))!;
            Assert.Contains("72", refreshed.Text, StringComparison.Ordinal);
            Assert.True(view.Installed(refreshed));
            Assert.Equal(ReviewOutcome.Reviewed, view.Review(refreshed.Token));
            Assert.False(kit.Store.Find(id)!.IsPending);

            // Omitted detail: review covers the represented subset only.
            var extras = Enumerable.Range(1, FactListPresenter.MaxDetailMaterialLines + 2)
                                   .Select(i => new MaterialDeclaration("x" + i, FactValue.Of((long)i))).ToArray();
            EpisodeId many = FactKit.OpenNote(kit.NotesSlot(kit.Session("SERIAL-2")), FactKit.CutKey, materials: extras).Handle!.Id;
            RenderedDetailSnapshot partial = view.RenderDetail(view.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == many))!;
            Assert.True(partial.OmittedRequiredDetail);
            view.Installed(partial);
            view.Review(partial.Token);
            Assert.Equal(2, kit.Store.Find(many)!.Unpresented.Count);

            // A growing loss summary: reviewing the older revision leaves the newer unreviewed.
            kit.Store.NoteIssue(IssueKind.DetailLoss, "loss", "test", "first loss", 1, ExtentCertainty.Exact, null, "l1");
            ItemSnapshot lossRow = view.Snapshot(FactView.Pending).Items.Single(i => i.Issue?.SourceKey == "loss");
            RenderedDetailSnapshot lossShown = view.RenderDetail(lossRow)!;
            view.Installed(lossShown);
            kit.Store.NoteIssue(IssueKind.DetailLoss, "loss", "test", "second loss", 1, ExtentCertainty.Exact, null, "l2");
            Assert.Equal(ReviewOutcome.Reviewed, view.Review(lossShown.Token));
            StoreIssueSnapshot loss = kit.Store.Issues.Single(i => i.SourceKey == "loss");
            Assert.True(loss.Unreviewed);
            Assert.Equal(2, loss.Count);                                        // reviewed loss is not recovered loss
        }

        [Fact]
        public void E4_Presenter_EmptyStatePredicatesDescribeTheirScope()
        {
            using var dir = new TempFactDir();
            static IReadOnlyList<string> Roles(FactStore store, string? filter = null) =>
                FactListPresenter.EmptyStateRoles(store.Project(FactView.Pending, filter));

            // No journal attached: nothing can be said about saved history.
            var detached = new FactKit();
            Assert.Equal(new[] { "facts.window.nothing_pending_unverified" }, Roles(detached.Store));

            // First run: attached, loaded, saved, genuinely nothing ON RECORD.
            // The sentence chosen says every recorded item was read out; the
            // one claiming the radio had nothing else to say is never chosen,
            // because an unrecorded event is unknowable (Sol, E).
            var first = new FactKit();
            var journal = new FactJournal(first.Store, dir.Path);
            journal.TakeLease();
            journal.LoadHistory();
            journal.Write();
            Assert.Equal(new[] { "facts.window.nothing_pending_recorded" }, Roles(first.Store));

            // Loading is its own state.
            first.Store.SetLoadState(HistoryLoadState.Loading);
            Assert.Equal(new[] { "facts.window.loading" }, Roles(first.Store));
            first.Store.SetLoadState(HistoryLoadState.Loaded);

            // Full confirmed coverage: still the delivered sentence.
            var tracked = new RecordingTransport(first.Registry, "t", TransportCapability.ReportsCompletion);
            EpisodeId heard = FactKit.OnsetHot(first.HotSlot(first.Session())).Handle!.Id;
            AttemptHandle a = first.Allocate(first.PlanAutomatic(heard), tracked.Binding);
            AttemptRunner.Run(a, tracked.Submit);
            a.Report(TransportEvidence.Completed(5));
            journal.Write();
            Assert.Equal(new[] { "facts.window.nothing_pending_recorded" }, Roles(first.Store));

            // Newer unsaved mutation: the unsaved line joins it.
            EpisodeId brief = FactKit.OnsetNote(first.NotesSlot(first.Session("SERIAL-2")), FactKit.BriefKey).Handle!.Id;
            Assert.Contains("facts.storage.unsaved", Roles(first.Store));

            // A forgettable omission: not owed, not delivered either.
            first.Registry.Quiet.Observe("ctrl");
            journal.Write();
            IReadOnlyList<string> forgot = Roles(first.Store);
            Assert.Contains("facts.window.nothing_pending_unverified", forgot);
            Assert.Contains("facts.window.nothing_pending_forgettable", forgot);
            Assert.DoesNotContain("facts.window.nothing_pending_recorded", forgot);
            _ = brief;

            // All reviewed but not delivered.
            var reviewedKit = new FactKit();
            var rj = new FactJournal(reviewedKit.Store, Path.Combine(dir.Path, "r"));
            rj.TakeLease();
            rj.LoadHistory();
            EpisodeId r = FactKit.OpenHot(reviewedKit.HotSlot(reviewedKit.Session())).Handle!.Id;
            using (FactListView v = new FactListPresenter(reviewedKit.Store).OpenView())
            {
                RenderedDetailSnapshot d = v.RenderDetail(v.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == r))!;
                v.Installed(d);
                v.Review(d.Token);
            }
            rj.Write();
            IReadOnlyList<string> reviewedRoles = Roles(reviewedKit.Store);
            Assert.Contains("facts.window.nothing_pending_reviewed", reviewedRoles);
            Assert.DoesNotContain("facts.window.nothing_pending_recorded", reviewedRoles);
            rj.Dispose();

            // Filtered-out pending detail: empty in scope, and says what it hides.
            var filtered = new FactKit();
            FactKit.OpenHot(filtered.HotSlot(filtered.Session("SERIAL-OTHER")));
            FactListSnapshot scoped = filtered.Store.Project(FactView.Pending, "SERIAL-MINE");
            Assert.Empty(scoped.Items);
            Assert.Equal(1, scoped.Predicates.ExcludedOutstanding);
            Assert.NotNull(FactListView.ExcludedText(scoped));
            Assert.DoesNotContain("facts.window.nothing_pending_recorded", FactListPresenter.EmptyStateRoles(scoped));

            // THE UNIVERSAL-DELIVERY SENTENCE IS NEVER CHOSEN. Not on a first
            // run, not after full coverage, not anywhere: no predicate can
            // establish that the radio had nothing else to say.
            Assert.DoesNotContain("facts.window.nothing_pending", Roles(first.Store));
            Assert.DoesNotContain("facts.window.nothing_pending", AllRoleKeys());

            // An active issue that was reviewed stays a row; so does unresolved loss.
            var issues = new FactKit();
            issues.Store.NoteIssue(IssueKind.PersistenceFailure, "p", "shard", "cannot write", 1, ExtentCertainty.Unknown, null, "p1");
            using (FactListView v = new FactListPresenter(issues.Store).OpenView())
            {
                ItemSnapshot row = v.Snapshot(FactView.Pending).Items.Single();
                RenderedDetailSnapshot d = v.RenderDetail(row)!;
                v.Installed(d);
                v.Review(d.Token);
                Assert.Single(v.Snapshot(FactView.Pending).Items);           // reviewed, still active, still reachable
            }
            issues.Store.NoteIssue(IssueKind.RetentionPressure, "retention", "facts", "lost one", 1, ExtentCertainty.Exact, null, "x");
            FactListSnapshot lossList = issues.Store.Project(FactView.Pending, null);
            Assert.True(lossList.Predicates.UnaccountedLoss);
            Assert.False(lossList.Predicates.PresentationComplete);
            Assert.Empty(FactListPresenter.EmptyStateRoles(lossList));

            // A live skipped shard: partial inventory, a row, never "complete".
            using var shared = new TempFactDir();
            var other = new FactKit();
            using var holder = new FactJournal(other.Store, shared.Path);
            holder.TakeLease();
            FactKit.OpenHot(other.HotSlot(other.Session()));
            holder.Write();
            var mine = new FactKit();
            var mj = new FactJournal(mine.Store, shared.Path);
            mj.TakeLease();
            mj.LoadHistory();
            FactListSnapshot partial = mine.Store.Project(FactView.Pending, null);
            Assert.False(partial.Predicates.CompleteInventory);
            Assert.Contains(partial.Items, i => i.Issue?.Kind == IssueKind.IncompleteInventory);
            mj.Dispose();
            journal.Dispose();
        }

        [Fact]
        public void E4_Presenter_FalseReceiptAndStateSentencesAreChosenOnlyWhereTheyAreTrue()
        {
            // Sol's findings: the words stay Noel's and stay unchanged; the
            // CODE now uses each only in the case it is true.
            var kit = new FactKit();
            SlotPublisher publisher = kit.HotSlot(kit.Session());

            EpisodeId requested = FactKit.OnsetHot(publisher).Handle!.Id;
            new ReceiptRequestAdapter(kit.Registry.RegisterReceiptAdapter("a"), _ => ToneRequestResult.Requested).RequestFor(requested);
            Assert.Equal("facts.receipt.request_issued", FactListPresenter.ReceiptRole(kit.Store.Find(requested)!));

            EpisodeId played = FactKit.OnsetHot(kit.HotSlot(kit.Session("SERIAL-2"))).Handle!.Id;
            new ReceiptRequestAdapter(kit.Registry.RegisterReceiptAdapter("b"), _ => ToneRequestResult.PlaybackReported).RequestFor(played);
            Assert.Equal("facts.receipt.requested", FactListPresenter.ReceiptRole(kit.Store.Find(played)!));   // "the tone was played"

            // Unknown-cause pause is not "because you asked for quiet".
            var tracked = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            EpisodeId cancelled = FactKit.OnsetHot(kit.HotSlot(kit.Session("SERIAL-3"))).Handle!.Id;
            AttemptHandle c = kit.Allocate(kit.PlanAutomatic(cancelled), tracked.Binding);
            AttemptRunner.Run(c, tracked.Submit);
            c.Report(TransportEvidence.Cancelled(5, CancelCause.Unknown));
            Assert.Equal("facts.state.paused_unknown_cause", FactListPresenter.StateRole(kit.Store.Find(cancelled)!));
            kit.Registry.Quiet.Observe("ctrl");
            EpisodeId quiet = requested;
            Assert.Equal("facts.state.paused", FactListPresenter.StateRole(kit.Store.Find(quiet)!));

            // "Read out in full" only when the words carried every required unit.
            EpisodeId shortOne = FactKit.OnsetHot(kit.HotSlot(kit.Session("SERIAL-4"))).Handle!.Id;
            AttemptHandle s = kit.Allocate(kit.PlanAutomatic(shortOne, VerbosityLevel.Terse), tracked.Binding);
            AttemptRunner.Run(s, tracked.Submit);
            s.Report(TransportEvidence.Completed(5));
            Assert.Equal("facts.delivery.completed_short", FactListPresenter.DeliveryRole(kit.Store.Find(shortOne)!));
            AttemptHandle f = kit.Allocate(kit.PlanAutomatic(shortOne, VerbosityLevel.Chatty), tracked.Binding);
            AttemptRunner.Run(f, tracked.Submit);
            f.Report(TransportEvidence.Completed(5));
            Assert.Equal("facts.delivery.completed", FactListPresenter.DeliveryRole(kit.Store.Find(shortOne)!));

            // "Withdrawn because what it described had ended" only on an owner resolution.
            SlotPublisher p5 = kit.HotSlot(kit.Session("SERIAL-5"));
            PublicationResult o5 = FactKit.OnsetHot(p5);
            AttemptHandle w = kit.Allocate(kit.PlanAutomatic(o5.Handle!.Id), tracked.Binding);
            p5.Update(o5.Handle, FactKit.Capture(p5, FactKit.Temp(70m)), FactTransition.ObservationUnknown(UnknownReason.ObservationFailed), o5.Fact!.Revision);
            AttemptRunner.Run(w, tracked.Submit);
            Assert.Equal("facts.delivery.withdrawn_context_ended", FactListPresenter.DeliveryRole(kit.Store.Find(o5.Handle.Id)!));

            SlotPublisher p6 = kit.HotSlot(kit.Session("SERIAL-6"));
            PublicationResult o6 = FactKit.OnsetHot(p6);
            AttemptHandle r = kit.Allocate(kit.PlanAutomatic(o6.Handle!.Id), tracked.Binding);
            p6.Resolve(o6.Handle, FactKit.Capture(p6, FactKit.Temp(60m)), o6.Fact!.Revision);
            AttemptRunner.Run(r, tracked.Submit);
            Assert.Equal("facts.delivery.withdrawn", FactListPresenter.DeliveryRole(kit.Store.Find(o6.Handle.Id)!));

            // Every role the presenter can name exists in the shipped lexicon.
            foreach (string key in AllRoleKeys()) Assert.True(Lexicon.Contains(key), key + " is not in the lexicon");
        }

        [Fact]
        public void E6_Presenter_ARoleThatClaimsAPauseIsChosenOnlyWhenSomethingWasPaused()
        {
            // Sol, E (M3 review): three chosen roles claimed a pause where
            // nothing establishes one, and the recorded-delivered sentence was
            // chosen for a continuation never spoken. These are his
            // counterexamples, each with the truth condition on the SENTENCE
            // the code chose — not a check that some role exists.
            static bool ClaimsAPause(string role) => Lexicon.Get(role).Contains("paused", StringComparison.Ordinal);
            static IReadOnlyList<string> Roles(FactStore store) => FactListPresenter.EmptyStateRoles(store.Project(FactView.Pending, null));

            // POSITIVE CONTROL for the instrument: the pause roles do say so.
            Assert.True(ClaimsAPause("facts.state.paused"));
            Assert.True(ClaimsAPause("facts.state.paused_continuity"));
            Assert.True(ClaimsAPause("facts.state.paused_continuity_unknown"));
            Assert.True(ClaimsAPause("facts.state.held_continuity_lost"));

            // (a) ContinuityLost: the record was lost to capacity. Nothing on
            // that station was ever paused, so the row must not say "paused".
            using var lostDir = new TempFactDir();
            var before = new FactKit();
            var stations = new List<string>();
            for (int i = 0; i <= FactStoreCapacity.MaxContinuityRecords; i++)
            {
                string radio = "SERIAL-" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
                FactSession s = before.Session(radio);
                FactKit.OpenHot(before.HotSlot(s), 70m);
                s.End(T0, "gone");
                stations.Add(radio);
            }
            Save(before, lostDir.Path);
            var (afterLoss, _) = Load(lostDir.Path);
            string? lost = null;
            foreach (string radio in stations)
            {
                FactSession probe = afterLoss.Session(radio);
                if (afterLoss.HotSlot(probe).Continuity == null) { lost = radio; break; }
                probe.End(T0, "survived; not needed");
            }
            Assert.NotNull(lost);
            PublicationResult lostOne = FactKit.OpenHot(afterLoss.HotSlot(afterLoss.Session(lost!)), 70m,
                options: new OpenOptions { Continuity = ContinuityClaim.Continuation });
            Assert.Equal(PauseCause.ContinuityLost, lostOne.Fact!.Pause);
            string lostRole = FactListPresenter.StateRole(lostOne.Fact);
            Assert.Equal("facts.state.held_record_lost", lostRole);
            Assert.False(ClaimsAPause(lostRole));

            // (b) ContinuityUnknown from a missing predecessor: a claimed
            // continuation with no reference. No pause evidence either.
            var kitM = new FactKit();
            FactSession sM = kitM.Session("SERIAL-M");
            FactKit.OnsetHot(kitM.HotSlot(sM), 70m);
            sM.End(T0, "gone");
            PublicationResult missing = FactKit.OpenHot(kitM.HotSlot(kitM.Session("SERIAL-M")), 70m,
                options: new OpenOptions { Continuity = ContinuityClaim.Continuation });
            Assert.Equal(PauseCause.ContinuityUnknown, missing.Fact!.Pause);
            string unknownRole = FactListPresenter.StateRole(missing.Fact);
            Assert.Equal("facts.state.held_continuity_unknown", unknownRole);
            Assert.False(ClaimsAPause(unknownRole));

            // (c) A restart continuation of a condition whose prior HOLD was
            // not a pause — unknown continuity, or an unestablished onset —
            // must not become "paused, ... when it had been paused".
            using var restartDir = new TempFactDir();
            var writer = new FactKit();
            FactSession sU = writer.Session("SERIAL-U");
            FactKit.OpenHot(writer.HotSlot(sU), 70m, options: new OpenOptions { Continuity = ContinuityClaim.ContinuityUnknown });
            sU.End(T0, "closing");
            FactSession sO = writer.Session("SERIAL-O");
            FactKit.OpenHot(writer.HotSlot(sO), 70m);                                   // OnsetNotEstablished
            sO.End(T0, "closing");
            FactSession sQ = writer.Session("SERIAL-Q");
            FactKit.OnsetHot(writer.HotSlot(sQ), 70m);
            writer.Registry.Quiet.Observe("ctrl");                                      // genuinely paused by the operator
            sQ.End(T0, "closing");
            Save(writer, restartDir.Path);
            var (restarted, _) = Load(restartDir.Path);
            foreach (string station in new[] { "SERIAL-U", "SERIAL-O" })
            {
                SlotPublisher p = restarted.HotSlot(restarted.Session(station));
                Assert.True(p.Continuity!.Paused);                                      // the VIEW says held; the cause decides the words
                PublicationResult held = FactKit.ContinueHot(p, 70m, 3);
                Assert.Equal(PublicationOutcome.Accepted, held.Outcome);
                Assert.Equal(PauseCause.ContinuityAcrossRestart, held.Fact!.Pause);
                string role = FactListPresenter.StateRole(held.Fact);
                Assert.Equal("facts.state.held_continuity_restart", role);
                Assert.False(ClaimsAPause(role));
            }
            // POSITIVE CONTROL: the operator's own quiet before the close IS a
            // pause, and "when it had been paused" is true of it.
            PublicationResult wasPaused = FactKit.ContinueHot(restarted.HotSlot(restarted.Session("SERIAL-Q")), 70m, 3);
            Assert.Equal(PauseCause.ContinuityInherited, wasPaused.Fact!.Pause);
            Assert.Equal("facts.state.paused_continuity", FactListPresenter.StateRole(wasPaused.Fact));
            Assert.True(ClaimsAPause("facts.state.paused_continuity"));

            // In the same process a predecessor that held no permission passes
            // its reason on, so the successor's row is not left saying only
            // "still true now".
            var same = new FactKit();
            FactSession sS = same.Session("SERIAL-S");
            FactKit.OpenHot(same.HotSlot(sS), 70m);
            sS.End(T0, "disconnected");
            PublicationResult carried = FactKit.ContinueHot(same.HotSlot(same.Session("SERIAL-S")), 70m, 3);
            Assert.Empty(carried.Fact!.Grants);
            Assert.Equal(PauseCause.OnsetNotEstablished, carried.Fact.Pause);
            Assert.Equal("facts.state.held_onset_not_established", FactListPresenter.StateRole(carried.Fact));

            // (d) An unchanged, fully covered continuation: complete through
            // its predecessor, never spoken itself. "Every item on record was
            // read out in full" is not chosen; the sentence that says some of
            // it carries on from one that was, is.
            using var dir = new TempFactDir();
            var kit = new FactKit();
            var journal = new FactJournal(kit.Store, dir.Path);
            journal.TakeLease();
            journal.LoadHistory();
            var tracked = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            FactSession s1 = kit.Session("SERIAL-K");
            EpisodeId first = FactKit.OnsetHot(kit.HotSlot(s1), 70m, 3).Handle!.Id;
            AttemptHandle a = kit.Allocate(kit.PlanAutomatic(first), tracked.Binding);
            AttemptRunner.Run(a, tracked.Submit);
            a.Report(TransportEvidence.Completed(5));
            journal.Write();
            Assert.Equal(new[] { "facts.window.nothing_pending_recorded" }, Roles(kit.Store));   // POSITIVE CONTROL: it WAS read out
            s1.End(T0, "disconnected");
            PublicationResult continued = FactKit.ContinueHot(kit.HotSlot(kit.Session("SERIAL-K")), 70m, 3);
            Assert.Equal(PublicationOutcome.Accepted, continued.Outcome);
            Assert.True(continued.Fact!.PresentationComplete);
            Assert.Empty(continued.Fact.Attempts);
            journal.Write();
            FactListSnapshot list = kit.Store.Project(FactView.Pending, null);
            Assert.True(list.Predicates.PresentationComplete);
            Assert.Equal(1, list.Predicates.PresentedInEarlierEpisode);
            IReadOnlyList<string> roles = FactListPresenter.EmptyStateRoles(list);
            Assert.Equal(new[] { "facts.window.nothing_pending_recorded_continued" }, roles);
            Assert.DoesNotContain("facts.window.nothing_pending_recorded", roles);
            Assert.Equal(1, tracked.NativeCalls);                                        // never spoken again
            journal.Dispose();

            // The two M3 drafts whose words claim a pause are still in the
            // file, unchanged, for Noel — and chosen nowhere.
            Assert.True(Lexicon.Contains("facts.state.paused_continuity_unknown"));
            Assert.True(Lexicon.Contains("facts.state.held_continuity_lost"));
            Assert.DoesNotContain("facts.state.paused_continuity_unknown", AllRoleKeys());
            Assert.DoesNotContain("facts.state.held_continuity_lost", AllRoleKeys());
        }

        private static void Save(FactKit kit, string dir)
        {
            using var journal = new FactJournal(kit.Store, dir);
            Assert.True(journal.TakeLease());
            Assert.True(journal.Write());
        }

        private static (FactKit Kit, LoadReport Report) Load(string dir)
        {
            var kit = new FactKit();
            var journal = new FactJournal(kit.Store, dir);
            Assert.True(journal.TakeLease());
            LoadReport report = journal.LoadHistory();
            journal.Dispose();
            return (kit, report);
        }

        [Fact]
        public void E5_Presenter_OpeningFocusQuietAndBackgroundUpdatesNeverReview()
        {
            var kit = new FactKit();
            SlotPublisher publisher = kit.HotSlot(kit.Session());
            PublicationResult opened = FactKit.OnsetHot(publisher);
            EpisodeId id = opened.Handle!.Id;
            var presenter = new FactListPresenter(kit.Store);

            FactListView view = presenter.OpenView();
            RenderedDetailSnapshot shown = view.RenderDetail(view.Snapshot(FactView.Pending).Items.Single())!;
            view.Installed(shown);
            view.RequestRead(shown.Token, out _);                       // a selected read
            kit.Registry.Quiet.Observe("ctrl");                          // Ctrl
            publisher.Update(opened.Handle, FactKit.Capture(publisher, FactKit.Temp(71m)), FactTransition.Sample(), opened.Fact!.Revision);
            kit.Store.NoteIssue(IssueKind.DetailLoss, "bg", "test", "background", 1, ExtentCertainty.Exact, null, "bg1");
            view.Snapshot(FactView.Pending);                             // a refresh of the list
            Assert.Empty(kit.Store.Find(id)!.Reviewed);
            Assert.True(kit.Store.Find(id)!.AutomaticPaused);            // nothing resumed either

            view.Close();
            Assert.Equal(ReviewOutcome.ViewClosed, view.Review(shown.Token));
            Assert.Empty(kit.Store.Find(id)!.Reviewed);                 // no leaked token

            // POSITIVE CONTROL: a deliberate review on an open view.
            using FactListView live = presenter.OpenView();
            RenderedDetailSnapshot d = live.RenderDetail(live.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == id))!;
            live.Installed(d);
            Assert.Equal(ReviewOutcome.Reviewed, live.Review(d.Token));
            Assert.NotEmpty(kit.Store.Find(id)!.Reviewed);
        }

        [Fact]
        public void E3_Presenter_TheSurfaceNeedsNoRadioSessionAndNoSpeech()
        {
            // The headless half of E3: a projection, a detail and a review work
            // on a store with no session at all. Reachability through the real
            // Command Finder, Status and UIA tree is the desk test.
            var kit = new FactKit();
            using FactListView view = new FactListPresenter(kit.Store).OpenView();
            FactListSnapshot empty = view.Snapshot(FactView.Pending);
            Assert.Empty(empty.Items);
            Assert.False(string.IsNullOrWhiteSpace(FactListView.EmptyStateText(empty)));
            Assert.False(string.IsNullOrWhiteSpace(FactListPresenter.StatusSummaryText(empty)));
        }

        internal static IEnumerable<string> AllRoleKeys()
        {
            foreach (PauseCause cause in Enum.GetValues<PauseCause>())
                yield return cause switch
                {
                    PauseCause.OperatorQuiet => "facts.state.paused",
                    PauseCause.UnknownCancellation => "facts.state.paused_unknown_cause",
                    PauseCause.ContinuityInherited => "facts.state.paused_continuity",
                    PauseCause.ContinuityUnknown => "facts.state.held_continuity_unknown",
                    PauseCause.ContinuityLost => "facts.state.held_record_lost",
                    PauseCause.ContinuityAcrossRestart => "facts.state.held_continuity_restart",
                    PauseCause.OnsetNotEstablished => "facts.state.held_onset_not_established",
                    PauseCause.LegacyUnknownCause => "facts.state.paused_cause_not_recorded",
                    _ => "facts.state.current",
                };
            foreach (IssueState state in Enum.GetValues<IssueState>()) yield return FactListPresenter.IssueStateRole(state);
            foreach (string k in new[]
                     {
                         "facts.state.current", "facts.state.historical", "facts.state.unknown", "facts.state.resolved",
                         "facts.state.silent_no_metadata", "facts.state.not_a_message", "facts.state.conflicting",
                         "facts.delivery.legacy_claimed_delivered", "facts.delivery.legacy_claimed_undelivered",
                         "facts.detail.legacy_claimed", "facts.window.nothing_pending_recorded",
                         "facts.window.nothing_pending_recorded_continued",
                         "facts.delivery.not_attempted", "facts.delivery.legacy_unverified", "facts.delivery.not_started_quiet",
                         "facts.delivery.not_started_paused", "facts.delivery.withdrawn_superseded", "facts.delivery.withdrawn",
                         "facts.delivery.withdrawn_context_ended", "facts.delivery.started", "facts.delivery.requested_only",
                         "facts.delivery.request_failed", "facts.delivery.refused", "facts.delivery.partial",
                         "facts.delivery.completed", "facts.delivery.completed_short", "facts.delivery.cancelled_known",
                         "facts.delivery.unknown", "facts.delivery.unobservable", "facts.delivery.disputed",
                         "facts.delivery.interrupted", "facts.receipt.request_issued", "facts.receipt.requested",
                         "facts.receipt.unavailable", "facts.receipt.suppressed", "facts.receipt.withheld",
                         "facts.receipt.outcome_unknown", "facts.window.loading", "facts.window.nothing_historical",
                         "facts.window.nothing_pending_unverified",
                         "facts.window.nothing_pending_reviewed", "facts.window.nothing_pending_forgettable",
                         "facts.storage.unsaved", "facts.window.excluded_by_filter", "facts.status.problems_present",
                         "facts.status.pending_summary", "facts.status.pending_summary_plural", "facts.status.nothing_pending",
                         "facts.status.nothing_waiting", "facts.row.issue_summary", "facts.issue.state.active",
                         "facts.issue.state.resolved", "facts.issue.count.exact", "facts.issue.count.lower_bound",
                         "facts.issue.count.unknown", "facts.issue.source", "facts.issue.exemplars_more",
                         "facts.detail.no_rendering", "facts.detail.material_line", "facts.detail.more_not_shown",
                         "facts.detail.legacy", "facts.detail.conflict", "facts.detail.restored_issue",
                         "facts.detail.changed", "facts.action.refresh",
                     })
                yield return k;
            foreach (IssueKind kind in Enum.GetValues<IssueKind>()) yield return FactListPresenter.IssueKindRole(kind);
        }
    }
}
