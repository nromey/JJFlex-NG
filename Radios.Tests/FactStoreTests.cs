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
    /// The store that owns the information owed to the operator — the rules
    /// Track M established that still stand, now exercised through issued
    /// publishers, plans and displayed-snapshot review instead of the raw
    /// recorders that could not tell who was speaking.
    /// </summary>
    /// <remarks>
    /// <b>Most of this file is about what the store REFUSES to conclude.</b>
    /// Unknown observation is not clear, silence is not acknowledgement,
    /// refusal is not delivery, a completion is not understanding, and capacity
    /// pressure is not a reason for a warning to disappear. Owners here are
    /// synthetic.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class FactStoreTests
    {
        private static readonly DateTime T0 = FactKit.T0;

        [Fact]
        public void AFactIsRetainedBeforeAnyToneOrSentence()
        {
            var kit = new FactKit();
            FactSnapshot fact = FactKit.OnsetHot(kit.HotSlot(kit.Session())).Fact!;

            Assert.Equal(ReceiptState.NotRequested, fact.Receipt.State);
            Assert.Empty(fact.Attempts);
            Assert.True(fact.IsPending);
        }

        [Theory]
        [InlineData(UnknownReason.ObservationFailed)]
        [InlineData(UnknownReason.DataUnavailable)]
        [InlineData(UnknownReason.ProvenanceAmbiguous)]
        [InlineData(UnknownReason.EvaluationFailed)]
        public void AFailedObservationKeepsTheObligationAndClearsNothing(UnknownReason reason)
        {
            var kit = new FactKit();
            SlotPublisher publisher = kit.HotSlot(kit.Session());
            PublicationResult opened = FactKit.OnsetHot(publisher);

            publisher.Update(opened.Handle!, FactKit.Capture(publisher, FactKit.Temp(70m)),
                             FactTransition.ObservationUnknown(reason), opened.Fact!.Revision);

            FactSnapshot fact = kit.Store.Find(opened.Handle!.Id)!;
            Assert.False(fact.Validity.IsCurrent);
            Assert.False(fact.Validity.IsResolved);
            Assert.True(fact.IsPending);
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(fact));
        }

        [Fact]
        public void ADisconnectEndsTheContextAndNeverResolvesTheCondition()
        {
            var kit = new FactKit();
            FactSession session = kit.Session();
            EpisodeId id = FactKit.OpenHot(kit.HotSlot(session)).Handle!.Id;

            session.End(T0, "the connection closed");

            FactSnapshot fact = kit.Store.Find(id)!;
            Assert.Equal(EndedKind.EndedObservationContext, fact.Validity.Ended);
            Assert.False(fact.Validity.IsResolved);
            Assert.True(fact.IsPending);
        }

        [Fact]
        public void AnUnheardPersistentConditionThatResolvesStaysOwedAsHistory()
        {
            var kit = new FactKit();
            SlotPublisher publisher = kit.HotSlot(kit.Session());
            PublicationResult opened = FactKit.OnsetHot(publisher);

            publisher.Resolve(opened.Handle!, FactKit.Capture(publisher, FactKit.Temp(50m)), opened.Fact!.Revision);

            FactSnapshot fact = kit.Store.Find(opened.Handle!.Id)!;
            Assert.True(fact.IsHistorical);
            Assert.True(fact.IsPending);
            Assert.Contains(kit.Store.Pending(), f => f.Id == fact.Id);
        }

        [Fact]
        public void AnUnclassifiedMessageIsKeptAndIsSilent()
        {
            var kit = new FactKit();
            FactSnapshot fact = FactKit.OpenNote(kit.NotesSlot(kit.Session()), FactKit.MysteryKey).Fact!;

            Assert.True(fact.IsPending);
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(fact));
            Assert.True(kit.Store.MayReadOnRequest(fact));
            Assert.Equal(ReceiptPolicy.None, fact.Receipt.Policy);
        }

        [Fact]
        public void ATextOnlyKeyOfferedAsAMessageIsAlsoSilent()
        {
            var kit = new FactKit();
            FactSnapshot fact = FactKit.OpenNote(kit.NotesSlot(kit.Session()), FactKit.LabelKey).Fact!;
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(fact));
        }

        [Fact]
        public void QuietPausesWithoutAcknowledgingOrClearingAnything()
        {
            var kit = new FactKit();
            EpisodeId id = FactKit.OnsetHot(kit.HotSlot(kit.Session())).Handle!.Id;

            kit.Registry.Quiet.Observe("the operator pressed Ctrl");

            FactSnapshot fact = kit.Store.Find(id)!;
            Assert.True(fact.AutomaticPaused);
            Assert.Equal(PauseCause.OperatorQuiet, fact.Pause);
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(fact));
            Assert.Empty(fact.Reviewed);
            Assert.True(fact.Validity.IsCurrent);
            Assert.True(fact.IsPending);
        }

        [Fact]
        public void APersistentConditionIsStillEligibleAfterMoreThanTwoFailedAttempts()
        {
            var kit = new FactKit();
            EpisodeId id = FactKit.OnsetHot(kit.HotSlot(kit.Session())).Handle!.Id;
            var refusing = new RecordingTransport(kit.Registry, "refuses", TransportCapability.ReportsAcceptance);

            for (int i = 0; i < 5; i++)
            {
                AttemptHandle a = kit.Allocate(kit.PlanAutomatic(id), refusing.Binding);
                AttemptRunner.Run(a, refusing.Submit);
                a.Report(TransportEvidence.BackendRefused(5));
            }

            FactSnapshot fact = kit.Store.Find(id)!;
            Assert.True(fact.IsPending);
            Assert.True(kit.Store.IsEligibleForAutomaticDelivery(fact));
            Assert.True(kit.Store.ShouldYieldToWaitingRequest(fact));   // fairness, never retirement
        }

        [Fact]
        public void TheBurstLimitIsTwoAndItOnlyYields()
        {
            var kit = new FactKit();
            EpisodeId id = FactKit.OnsetHot(kit.HotSlot(kit.Session())).Handle!.Id;
            var refusing = new RecordingTransport(kit.Registry, "refuses", TransportCapability.ReportsAcceptance);
            Assert.Equal(2, FactStoreCapacity.AutomaticBurstAttempts);
            Assert.False(kit.Store.ShouldYieldToWaitingRequest(kit.Store.Find(id)!));

            for (int i = 0; i < 2; i++)
            {
                AttemptHandle a = kit.Allocate(kit.PlanAutomatic(id), refusing.Binding);
                AttemptRunner.Run(a, refusing.Submit);
                a.Report(TransportEvidence.BackendRefused(5));
            }
            Assert.True(kit.Store.ShouldYieldToWaitingRequest(kit.Store.Find(id)!));
            Assert.True(kit.Store.IsEligibleForAutomaticDelivery(kit.Store.Find(id)!));
        }

        [Fact]
        public void AForgettableMessageGetsExactlyOnePresentationAndNoContinuingDebt()
        {
            var kit = new FactKit();
            EpisodeId id = FactKit.OnsetNote(kit.NotesSlot(kit.Session()), FactKit.BriefKey).Handle!.Id;
            var refusing = new RecordingTransport(kit.Registry, "refuses", TransportCapability.ReportsAcceptance);

            // Track M never made it eligible even once: its eligibility ended
            // on "has retained debt", which forgettable information never has.
            FactSnapshot before = kit.Store.Find(id)!;
            Assert.False(before.IsPending);
            Assert.True(kit.Store.IsEligibleForAutomaticDelivery(before));

            AttemptHandle a = kit.Allocate(kit.PlanAutomatic(id), refusing.Binding);
            AttemptRunner.Run(a, refusing.Submit);
            a.Report(TransportEvidence.BackendRefused(5));

            FactSnapshot after = kit.Store.Find(id)!;
            Assert.False(after.HasUndeliveredDetail);
            Assert.False(after.IsPending);
            Assert.DoesNotContain(kit.Store.Pending(), f => f.Id == id);
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(after));
        }

        [Fact]
        public void APerishableEventKeepsItsHistoryEvenWhenTheSpeechCompleted()
        {
            var kit = new FactKit();
            EpisodeId id = FactKit.OnsetNote(kit.NotesSlot(kit.Session()), FactKit.CutKey).Handle!.Id;
            var tracked = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            AttemptHandle a = kit.Allocate(kit.PlanAutomatic(id), tracked.Binding);
            AttemptRunner.Run(a, tracked.Submit);
            a.Report(TransportEvidence.Completed(5));

            Assert.False(kit.Store.Find(id)!.IsPending);
            Assert.Contains(kit.Store.History(), f => f.Id == id);
        }

        [Fact]
        public void ACorrectionMakesADeliveredFactOwedAgainAndAFreshSampleDoesNot()
        {
            var kit = new FactKit();
            SlotPublisher publisher = kit.HotSlot(kit.Session());
            PublicationResult opened = FactKit.OnsetHot(publisher);
            EpisodeId id = opened.Handle!.Id;
            var tracked = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            AttemptHandle a = kit.Allocate(kit.PlanAutomatic(id), tracked.Binding);
            AttemptRunner.Run(a, tracked.Submit);
            a.Report(TransportEvidence.Completed(5));
            Assert.False(kit.Store.Find(id)!.IsPending);

            publisher.Update(opened.Handle!, FactKit.Capture(publisher, FactKit.Temp(71m)), FactTransition.Sample(),
                             kit.Store.Find(id)!.Revision);
            Assert.False(kit.Store.Find(id)!.IsPending);

            publisher.Update(opened.Handle!, FactKit.Capture(publisher, FactKit.Temp(71m)),
                FactTransition.Correction(new[] { new MaterialDeclaration("temperature", FactValue.Of(71m)) }),
                kit.Store.Find(id)!.Revision);
            Assert.True(kit.Store.Find(id)!.IsPending);
        }

        [Fact]
        public void SlotExhaustionIsAReachableRowAndNeverATraceLineOnly()
        {
            var kit = new FactKit();
            FactSession session = kit.Session();
            for (int i = 0; i < FactStoreCapacity.MaxCurrentSlots; i++) kit.HotSlot(session, "slot-" + i);

            RegistrationResult refused = kit.Registry.Register(kit.Hot, session, FactKit.Temperature, new ConditionKey("one too many"));

            Assert.Equal(RegistrationOutcome.Exhausted, refused.Outcome);
            Assert.Null(refused.Publisher);
            Assert.False(Lexicon.LooksLikeKey(refused.Explanation));
            Assert.Contains(kit.Store.Issues, i => i.Kind == IssueKind.RegistrationPressure && i.Outstanding);
        }

        [Fact]
        public void AFullStoreRecordsWhatItCouldNotKeepAndKeepsTheReservedCurrentSlot()
        {
            var kit = new FactKit();
            FactSession session = kit.Session();
            SlotPublisher publisher = kit.HotSlot(session);

            // Fill history with owed facts that may never be compacted, one
            // slot at a time so the slot bound is not what is measured.
            for (int i = 0; i < FactStoreCapacity.MaxHistoricalRecords; i++)
            {
                FactSession s = kit.Session("SERIAL-" + i);
                FactKit.OpenHot(kit.HotSlot(s, "c"));
                s.End(T0, "filled");
            }

            // The reserved current slot still accepts its first current record.
            PublicationResult current = FactKit.OpenHot(publisher);
            Assert.Equal(PublicationOutcome.Accepted, current.Outcome);

            // A second current record on the same slot is counted, not kept.
            PublicationResult overflow = FactKit.OpenHot(publisher);
            Assert.Equal(PublicationOutcome.CapacityRecorded, overflow.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(overflow.Explanation));
            StoreIssueSnapshot pressure = Assert.Single(kit.Store.Issues, i => i.Kind == IssueKind.RetentionPressure);
            Assert.Equal(1, pressure.Count);
        }

        [Fact]
        public void CompactionTakesDischargedHistoryAndNeverSomethingStillOwed()
        {
            var kit = new FactKit();
            // A discharged one first.
            FactSession dischargedSession = kit.Session("SERIAL-D");
            EpisodeId discharged = FactKit.OpenNote(kit.NotesSlot(dischargedSession), FactKit.CutKey).Handle!.Id;
            using (FactListView view = new FactListPresenter(kit.Store).OpenView())
            {
                RenderedDetailSnapshot d = view.RenderDetail(view.Snapshot(FactView.Pending).Items.Single())!;
                view.Installed(d);
                view.Review(d.Token);
            }
            dischargedSession.End(T0, "history now");
            for (int i = 1; i < FactStoreCapacity.MaxHistoricalRecords; i++)
            {
                FactSession s = kit.Session("SERIAL-" + i);
                FactKit.OpenHot(kit.HotSlot(s, "c"));
                s.End(T0, "filled");
            }

            PublicationResult next = FactKit.OpenHot(kit.HotSlot(kit.Session("SERIAL-NEW"), "c"));

            Assert.Equal(PublicationOutcome.Accepted, next.Outcome);
            Assert.Null(kit.Store.Find(discharged));
            Assert.Equal(FactStoreCapacity.MaxHistoricalRecords, kit.Store.Pending().Count);
            // The loss of discharged detail is history, not an outstanding problem.
            StoreIssueSnapshot compaction = Assert.Single(kit.Store.Issues, i => i.SourceKey == "compaction");
            Assert.False(compaction.Outstanding);
            // And it keeps the delivered sentence from being chosen.
            Assert.True(kit.Store.Project(FactView.Pending, null).Predicates.ReviewedNotDelivered >= 1);
        }

        [Fact]
        public void AStationFilterNamesWhatItHidesRatherThanHidingItSilently()
        {
            var kit = new FactKit();
            EpisodeId id = FactKit.OpenHot(kit.HotSlot(kit.Session("SERIAL-1"))).Handle!.Id;

            Assert.Contains(kit.Store.Pending("SERIAL-1"), f => f.Id == id);
            FactListSnapshot other = kit.Store.Project(FactView.Pending, "SOME-OTHER-RADIO");
            Assert.Empty(other.Items);
            Assert.Equal(1, other.Predicates.ExcludedOutstanding);
        }

        [Fact]
        public void TruncatedDetailSaysSo()
        {
            var kit = new FactKit();
            FactSnapshot fact = FactKit.OpenNote(kit.NotesSlot(kit.Session()), FactKit.CutKey,
                detail: new string('x', FactStoreCapacity.MaxDetailBytes + 100)).Fact!;

            Assert.True(fact.DetailTruncated);
            Assert.True(fact.Detail.Length <= FactStoreCapacity.MaxDetailBytes);
        }

        [Theory]
        [InlineData(ToneRequestResult.Requested)]
        [InlineData(ToneRequestResult.PlaybackReported)]
        [InlineData(ToneRequestResult.Unavailable)]
        [InlineData(ToneRequestResult.Suppressed)]
        public void NoReceiptOutcomeMeansHeardAndNoneOfThemClearsTheDebt(ToneRequestResult result)
        {
            var kit = new FactKit();
            EpisodeId id = FactKit.OnsetHot(kit.HotSlot(kit.Session())).Handle!.Id;
            new ReceiptRequestAdapter(kit.Registry.RegisterReceiptAdapter("r"), _ => result).RequestFor(id);
            Assert.True(kit.Store.Find(id)!.IsPending);
        }

        [Fact]
        public void TheFailureNoticeFiresOnceOnTheEdgeAndNotPerFact()
        {
            var kit = new FactKit();
            Assert.True(kit.Store.NoteChannelHealth(healthy: false));
            Assert.False(kit.Store.NoteChannelHealth(healthy: false));
            kit.Store.NoteChannelHealth(healthy: true);
            Assert.True(kit.Store.NoteChannelHealth(healthy: false));
        }

        [Fact]
        public void NothingInTheStoreAsksHowOldAnythingIs()
        {
            // An observation from years ago is exactly as current as its owner
            // says it is. If any eligibility rule compared a time, it would
            // fire here.
            var kit = new FactKit();
            SlotPublisher publisher = kit.HotSlot(kit.Session());
            CapturedFactEvent ancient = publisher.Capture(FactKit.Temp(70m), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Event!;
            PublicationResult opened = publisher.Open(ancient, "condition.hot", FactKit.HotKey,
                new[] { new MaterialDeclaration("temperature", FactValue.Of(70m)) }, FactKit.Onset());

            Assert.True(opened.Fact!.Validity.IsCurrent);
            Assert.True(kit.Store.IsEligibleForAutomaticDelivery(opened.Fact));
        }

        /// <summary>
        /// A source-name denylist over the fact layer. <b>It proves exactly
        /// this and no more:</b> none of these clock APIs is named on a code
        /// line in <c>Radios/Facts</c>. It cannot prove the absence of every
        /// possible clock-based expiry — a time could arrive as a parameter
        /// and be compared — which is why the staleness rule is also stated as
        /// an invariant on eligibility and pinned by the behavioural test above.
        /// </summary>
        [Fact]
        public void TheFactLayerSourceNamesNoClockApi()
        {
            string dir = Path.Combine(RepoRoot(), "Radios", "Facts");
            Assert.True(Directory.Exists(dir), "Radios/Facts was not found at " + dir);
            string[] files = Directory.GetFiles(dir, "*.cs");
            Assert.True(files.Length >= 15, "only " + files.Length + " files were scanned");

            string[] clocks =
            {
                "Timer", "Stopwatch", "Elapsed", "TimeSpan", "AddSeconds", "AddMinutes", "AddMilliseconds",
                "AddHours", "UtcNow", "DateTime.Now", "DateTimeOffset.Now", "TickCount", "GetTimestamp",
            };

            // POSITIVE CONTROL: the scan really does find a clock name on a
            // code line when one is there.
            Assert.NotEmpty(Offenders(new[] { "    var t = DateTime.UtcNow;" }, clocks));

            var offenders = new List<string>();
            foreach (string file in files)
                foreach (string hit in Offenders(File.ReadAllLines(file), clocks))
                    offenders.Add(Path.GetFileName(file) + ": " + hit);

            Assert.True(offenders.Count == 0,
                "The fact layer names a clock. Nothing here decides staleness by counting time — the owner's "
                + "transition does — and a clock in this layer is a shelf life under another name:\n  "
                + string.Join("\n  ", offenders));
        }

        private static IEnumerable<string> Offenders(IEnumerable<string> lines, string[] clocks)
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal)) continue;
                foreach (string clock in clocks)
                    if (line.Contains(clock, StringComparison.Ordinal)) yield return line;
            }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }

    /// <summary>The application composition point: one store, one journal, started from the settings root.</summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class ApplicationFactsStartupTests
    {
        [Fact]
        public void StartLoadsHistoryUnderTheGivenRootAndHandsTheIssuerOutOnce()
        {
            using var root = new TempFactDir();

            // A previous run left a released shard with an owed fact.
            var previous = new FactKit();
            EpisodeId id = FactKit.OpenHot(previous.HotSlot(previous.Session("SERIAL-P"))).Handle!.Id;
            using (var j = new FactJournal(previous.Store, Path.Combine(root.Path, "facts")))
            {
                j.TakeLease();
                j.Write();
            }

            ApplicationFacts.Forget();
            try
            {
                FactAuthorityRegistry? issuer = ApplicationFacts.Start(root.Path);
                Assert.NotNull(issuer);
                Assert.Null(ApplicationFacts.Start(root.Path));                 // the issuer is handed out once
                Assert.Same(issuer!.Store, ApplicationFacts.Store);            // one store, not a second incidental one
                Assert.Equal(HistoryLoadState.Loaded, ApplicationFacts.Store.Load);
                Assert.NotNull(ApplicationFacts.Store.Find(id));
                Assert.StartsWith(Path.Combine(root.Path, "facts"), ApplicationFacts.Journal!.ShardPath, StringComparison.Ordinal);

                // The status summary the Status dialog shows comes from the same projection.
                Assert.Equal("facts.status.pending_summary",
                    FactListPresenter.StatusSummaryRoles(ApplicationFacts.Store.Project(FactView.Pending, null)).Single().Key);
            }
            finally
            {
                ApplicationFacts.Shutdown();
                ApplicationFacts.Forget();
            }
        }

        [Fact]
        public void ApplicationEventsStartsTheStoreBeforeSpeechAndShutsItDown()
        {
            string root = new DirectoryInfo(AppContext.BaseDirectory).Parent!.FullName;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string vb = File.ReadAllText(Path.Combine(dir!.FullName, "ApplicationEvents.vb"));

            int start = vb.IndexOf("Radios.Facts.ApplicationFacts.Start(", StringComparison.Ordinal);
            int speech = vb.IndexOf("Radios.ScreenReaderOutput.Initialize()", StringComparison.Ordinal);
            Assert.True(speech > 0, "the speech initialisation line was not found, so this test checks nothing");
            Assert.True(start > 0 && start < speech, "the fact store must start before speech is initialised");
            Assert.Contains("Radios.Facts.ApplicationFacts.Shutdown()", vb, StringComparison.Ordinal);
            _ = root;
        }
    }
}
