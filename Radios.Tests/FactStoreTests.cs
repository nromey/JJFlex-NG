#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Radios;
using Radios.Facts;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The store that owns the information owed to the operator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Most of this file is about what the store REFUSES to conclude.</b>
    /// Unknown observation is not clear, silence is not acknowledgement,
    /// refusal is not delivery, a completion is not understanding, and capacity
    /// pressure is not a reason for a warning to disappear. Each of those was a
    /// real path in the code before there was a store to hold the fact apart
    /// from the sentence.
    /// </para>
    /// <para>
    /// <b>No test here moves a clock, and that is the point.</b> There is no
    /// elapsed time this store responds to.
    /// </para>
    /// </remarks>
    // In the RadioConfig statics collection because the store's operator-facing
    // sentences come from the lexicon, which loads its partitions into
    // process-wide state on first use.
    [Collection(RadioConfigStaticsCollection.Name)]
    public class FactStoreTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

        private static LexiconMessage Classified(
            string key, ShelfLife life, string validity = "transmit.active")
        {
            var entry = LexiconEntry.Plain("words").WithDelivery(
                DeliveryClassification.Message,
                new DeliveryDescriptor(life, validity,
                    life == ShelfLife.Forgettable ? null : key + ".history",
                    ReceiptPolicy.Warning));
            return new LexiconMessage(key, entry, Array.Empty<(string, object?)>(), 1);
        }

        private static LexiconMessage Unclassified(string key)
            => new LexiconMessage(key, LexiconEntry.Plain("words"), Array.Empty<(string, object?)>(), 1);

        private static LexiconMessage TextOnly(string key)
        {
            var entry = LexiconEntry.Plain("a label").WithDelivery(DeliveryClassification.TextOnly, null);
            return new LexiconMessage(key, entry, Array.Empty<(string, object?)>(), 1);
        }

        private static (FactStore Store, RadioSessionFacts Session, ProducerCapability Cap) Fresh(
            string? radio = "SERIAL-1")
        {
            var store = new FactStore(processIncarnation: 1);
            var session = new RadioSessionFacts(store, radio);
            return (store, session, session.IssueCapability("test producer"));
        }

        private static FactAdmission Admit(
            FactStore store, RadioSessionFacts session, ProducerCapability cap,
            LexiconMessage message, string occurrence = "occ-1",
            ValiditySnapshot? validity = null, string slot = "transmit.reflected")
            => store.Admit(
                cap,
                session.NewOccurrence(slot, occurrence),
                message,
                validity ?? ValiditySnapshot.Establish(T0),
                session.Observe(T0));

        // ────────────────────────────────────────────────────────────────
        //  Admission comes first, and depends on nothing
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void AFactIsRetainedWithoutAnyToneOrSentenceHavingHappened()
        {
            var (store, session, cap) = Fresh();
            FactAdmission result = Admit(store, session, cap,
                Classified("a.warning", ShelfLife.Perishable));

            Assert.True(result.Accepted);
            Assert.Equal(ReceiptState.NotRequested, result.Fact!.Receipt);
            Assert.Empty(result.Fact.Attempts);

            // The ordering the whole design turns on: the fact is owned before
            // a tone is asked for and before speech is attempted, so neither
            // failing can lose it.
            Assert.True(result.Fact.IsPending);
        }

        [Fact]
        public void TheSameOccurrenceTwiceChangesNothing()
        {
            var (store, session, cap) = Fresh();
            var message = Classified("a.warning", ShelfLife.Persistent);

            FactAdmission first = Admit(store, session, cap, message);
            FactAdmission second = Admit(store, session, cap, message);

            Assert.True(first.Accepted);
            Assert.Equal(AdmissionOutcome.AlreadyKnown, second.Outcome);
            Assert.Single(store.All);
        }

        [Fact]
        public void TwoRadiosUsingOneKeyKeepTwoSeparateEpisodes()
        {
            var store = new FactStore(1);
            var a = new RadioSessionFacts(store, "SERIAL-A");
            var b = new RadioSessionFacts(store, "SERIAL-B");
            var message = Classified("a.warning", ShelfLife.Persistent);

            Admit(store, a, a.IssueCapability("a"), message);
            Admit(store, b, b.IssueCapability("b"), message);

            // A subject string is not an identity. One radio's warning must
            // never be able to retire another's.
            Assert.Equal(2, store.All.Count);
        }

        [Fact]
        public void AReconnectToTheSameSerialIsANewIncarnation()
        {
            var store = new FactStore(1);
            var first = new RadioSessionFacts(store, "SERIAL-1");
            var second = new RadioSessionFacts(store, "SERIAL-1");

            Assert.NotEqual(first.ConnectionIncarnation, second.ConnectionIncarnation);
        }

        [Fact]
        public void ARevokedCapabilityCannotAdmitAnythingAsCurrent()
        {
            var (store, session, cap) = Fresh();
            session.Detach(T0, "the radio went away");

            FactAdmission result = Admit(store, session, cap,
                Classified("a.warning", ShelfLife.Persistent));

            Assert.Equal(AdmissionOutcome.NotAuthorised, result.Outcome);
            Assert.Empty(store.All);
        }

        [Fact]
        public void AnOlderObservationCannotRegressALiveFact()
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;

            FactProvenance late = session.Observe(T0);          // stamped now
            FactProvenance later = session.Observe(T0);         // stamped after it

            Assert.True(store.UpdateValidity(cap, fact.Identity.EpisodeId,
                ValiditySnapshot.End(EndedKind.ResolvedCondition, T0), later, material: true));

            // The earlier stamp arriving afterwards adds nothing: a late
            // callback may append evidence to history, never reopen current
            // truth.
            Assert.False(store.UpdateValidity(cap, fact.Identity.EpisodeId,
                ValiditySnapshot.Establish(T0), late));
            Assert.True(fact.Validity.IsResolved);
        }

        // ────────────────────────────────────────────────────────────────
        //  Unknown observation is not clear
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(UnknownReason.ObservationFailed)]
        [InlineData(UnknownReason.DataUnavailable)]
        [InlineData(UnknownReason.ProvenanceAmbiguous)]
        [InlineData(UnknownReason.EvaluationFailed)]
        public void AFailedObservationKeepsTheObligationAndClearsNothing(UnknownReason reason)
        {
            // Four different failures used to arrive as one success: a throwing
            // refresh, a null service, a missing meter and a disconnect all
            // came back null and were read as "the condition cleared", which
            // removed the pending warning.
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;

            store.UpdateValidity(cap, fact.Identity.EpisodeId,
                ValiditySnapshot.NotKnown(reason, T0), session.Observe(T0));

            Assert.False(fact.Validity.IsCurrent);
            Assert.False(fact.Validity.IsResolved);
            Assert.True(fact.IsPending);

            // Suspended, not withdrawn: no current wording, obligation intact.
            Assert.False(store.IsEligibleForAutomaticDelivery(fact));
        }

        [Fact]
        public void OnlyAnOwnerTransitionReachesResolved()
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;

            session.Detach(T0, "the connection closed");

            // A disconnect ends the observation CONTEXT. It is not evidence
            // that a hot PA cooled down.
            Assert.Equal(ValidityState.Ended, fact.Validity.State);
            Assert.Equal(EndedKind.EndedObservationContext, fact.Validity.Ended);
            Assert.False(fact.Validity.IsResolved);
            Assert.True(fact.IsPending);
        }

        [Fact]
        public void AnUnheardPersistentConditionThatResolvesStaysOwedAsHistory()
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;

            store.UpdateValidity(cap, fact.Identity.EpisodeId,
                ValiditySnapshot.End(EndedKind.ResolvedCondition, T0), session.Observe(T0), material: false);

            // The condition going away does not prove anybody heard about it.
            Assert.True(fact.IsHistorical);
            Assert.True(fact.IsPending);
            Assert.Contains(fact, store.Pending());
        }

        // ────────────────────────────────────────────────────────────────
        //  Decision point two: an unclassified key that reaches a run
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void AnUnclassifiedMessageIsKeptAndIsSilent()
        {
            var (store, session, cap) = Fresh();
            FactAdmission result = Admit(store, session, cap, Unclassified("a.mystery"));

            // Kept: throwing away a safety event because its formatting
            // metadata was missing is much the worse failure.
            Assert.True(result.Accepted);
            Assert.True(result.Fact!.IsPending);
            Assert.Contains(result.Fact, store.Pending());

            // Silent: nothing knows how long the information stays worth
            // saying, so nothing may volunteer it.
            Assert.False(store.IsEligibleForAutomaticDelivery(result.Fact));

            // But it can still be READ, because asking is its own permission.
            Assert.True(store.MayReadOnRequest(result.Fact));
        }

        [Fact]
        public void ATextOnlyKeyOfferedAsAMessageIsAlsoSilent()
        {
            // A different refusal for a different reason: somebody
            // affirmatively said this string is not a message, so treating it
            // as one overrides a decision rather than filling a gap.
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, TextOnly("a.label")).Fact!;

            Assert.False(store.IsEligibleForAutomaticDelivery(fact));
        }

        // ────────────────────────────────────────────────────────────────
        //  Decision point one: Ctrl, and what releases it
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void QuietPausesTheCohortWithoutAcknowledgingOrClearingAnything()
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;

            store.AdvanceQuietBarrier("the operator pressed Ctrl");

            Assert.True(fact.AutomaticPaused);
            Assert.False(store.IsEligibleForAutomaticDelivery(fact));

            // Nothing acknowledged, nothing cleared, nothing less true.
            Assert.False(fact.Reviewed);
            Assert.True(fact.Validity.IsCurrent);
            Assert.True(fact.IsPending);
            Assert.Contains(fact, store.Pending());
        }

        [Fact]
        public void APausedFactIsNotResumedByAReconnectOrABackendRecovery()
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;
            store.AdvanceQuietBarrier("the operator pressed Ctrl");

            // Things that happen afterwards and confer nothing.
            store.NoteChannelHealth(healthy: false);
            store.NoteChannelHealth(healthy: true);
            var reconnected = new RadioSessionFacts(store, "SERIAL-1");
            reconnected.IssueCapability("after the reconnect");

            Assert.True(fact.AutomaticPaused);
            Assert.False(store.IsEligibleForAutomaticDelivery(fact));
        }

        [Fact]
        public void OnlyAnExplicitResumeForTheSelectedRecordReleasesIt()
        {
            var (store, session, cap) = Fresh();
            Fact one = Admit(store, session, cap, Classified("a.one", ShelfLife.Persistent), "occ-1").Fact!;
            Fact two = Admit(store, session, cap, Classified("a.two", ShelfLife.Persistent), "occ-2",
                slot: "transmit.temperature").Fact!;
            store.AdvanceQuietBarrier("quiet");

            store.ResumeAutomatic(one.Identity.EpisodeId);

            Assert.True(store.IsEligibleForAutomaticDelivery(one));

            // One successful read cannot silently resume everything — that
            // would turn one deliberate action into permission never given.
            Assert.False(store.IsEligibleForAutomaticDelivery(two));
        }

        [Fact]
        public void AnUnknownCancellationPausesRatherThanRetires()
        {
            // A zero-mark cancellation from an external cause is not proof that
            // no presentation began, and proves nothing about why it stopped.
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;

            store.RecordAttempt(fact.Identity.EpisodeId,
                DeliveryState.UnknownCompletion, T0, coveredRequiredDetail: false);

            Assert.True(fact.AutomaticPaused);
            Assert.True(fact.IsPending);
        }

        // ────────────────────────────────────────────────────────────────
        //  Shelf life and finite attempts
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void APersistentConditionIsStillEligibleAfterMoreThanTwoFailedAttempts()
        {
            // The struck-out contract said two attempts and then terminal
            // silence. The number survived as a fairness rule; the terminal
            // state did not, because the condition makes the fact true.
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;

            for (int i = 0; i < 5; i++)
            {
                store.RecordAttempt(fact.Identity.EpisodeId,
                    DeliveryState.Refused, T0, coveredRequiredDetail: false);
            }

            Assert.True(fact.IsPending);
            Assert.True(store.IsEligibleForAutomaticDelivery(fact));

            // What the burst limit DOES do: yield the slot to somebody waiting.
            Assert.True(store.ShouldYieldToWaitingRequest(fact));
        }

        [Fact]
        public void TheBurstLimitIsTwoAndItOnlyYields()
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;

            Assert.Equal(2, FactStoreCapacity.AutomaticBurstAttempts);
            Assert.False(store.ShouldYieldToWaitingRequest(fact));

            store.RecordAttempt(fact.Identity.EpisodeId, DeliveryState.Refused, T0, false);
            Assert.False(store.ShouldYieldToWaitingRequest(fact));

            store.RecordAttempt(fact.Identity.EpisodeId, DeliveryState.Refused, T0, false);
            Assert.True(store.ShouldYieldToWaitingRequest(fact));

            // Yielding is not retiring.
            Assert.True(store.IsEligibleForAutomaticDelivery(fact));
        }

        [Fact]
        public void AForgettableMessageCreatesNoContinuingDebt()
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap,
                Classified("a.pc_audio_on", ShelfLife.Forgettable, ValidityContracts.RequestScoped)).Fact!;

            store.RecordAttempt(fact.Identity.EpisodeId, DeliveryState.Refused, T0, false);

            Assert.False(fact.HasUndeliveredDetail);
            Assert.False(fact.IsPending);
            Assert.DoesNotContain(fact, store.Pending());
            Assert.False(store.IsEligibleForAutomaticDelivery(fact));
        }

        [Fact]
        public void APerishableEventKeepsItsHistoryEvenWhenTheSpeechCompleted()
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.cut", ShelfLife.Perishable)).Fact!;

            store.RecordAttempt(fact.Identity.EpisodeId,
                DeliveryState.TrackedCompletion, T0, coveredRequiredDetail: true);

            // Delivered, so off the pending list...
            Assert.False(fact.IsPending);
            // ...and still in the history view, which is the other half of the
            // ruling: what happened remains readable afterwards.
            Assert.Contains(fact, store.History());
        }

        [Fact]
        public void AShortSentenceThatCompletedDoesNotSettleDetailItDidNotCarry()
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.capture", ShelfLife.Persistent)).Fact!;

            store.RecordAttempt(fact.Identity.EpisodeId,
                DeliveryState.TrackedCompletion, T0, coveredRequiredDetail: false);

            // The capture acknowledgement completing says nothing about the
            // duration the chosen verbosity tier left out.
            Assert.True(fact.IsPending);
        }

        [Fact]
        public void ACorrectionMakesAnAlreadyDeliveredFactOwedAgain()
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;
            store.RecordAttempt(fact.Identity.EpisodeId, DeliveryState.TrackedCompletion, T0, true);
            Assert.False(fact.IsPending);

            store.UpdateValidity(cap, fact.Identity.EpisodeId,
                ValiditySnapshot.Establish(T0, "corrected"), session.Observe(T0), material: true);

            Assert.True(fact.IsPending);
        }

        [Fact]
        public void AFreshSampleDoesNotMakeADeliveredFactOwedAgain()
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;
            store.RecordAttempt(fact.Identity.EpisodeId, DeliveryState.TrackedCompletion, T0, true);

            store.UpdateValidity(cap, fact.Identity.EpisodeId,
                ValiditySnapshot.Establish(T0, "same condition, newer reading"),
                session.Observe(T0), material: false);

            // A newer sample must not manufacture an incident, undo a silence
            // or replenish an allowance.
            Assert.False(fact.IsPending);
        }

        // ────────────────────────────────────────────────────────────────
        //  Review
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void ReviewingIsScopedToWhatWasShown()
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;
            long shown = fact.MaterialRevision;

            // A newer revision arrives behind the selected row while he reads.
            store.UpdateValidity(cap, fact.Identity.EpisodeId,
                ValiditySnapshot.Establish(T0, "something new"), session.Observe(T0), material: true);

            Assert.False(store.MarkReviewed(fact.Identity.EpisodeId, shown));
            Assert.True(fact.IsPending);

            Assert.True(store.MarkReviewed(fact.Identity.EpisodeId, fact.MaterialRevision));
            Assert.False(fact.IsPending);
        }

        // ────────────────────────────────────────────────────────────────
        //  Capacity, and the ninth warning
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void SlotExhaustionComesBackAsWordsAndNotAsATraceLine()
        {
            var (store, _, cap) = Fresh();
            for (int i = 0; i < FactStoreCapacity.MaxCurrentSlots; i++)
                Assert.True(store.RegisterSlot(cap, "slot-" + i).Granted);

            RegistrationResult refused = store.RegisterSlot(cap, "one too many");

            // "It cannot be spoken while only a trace knows its retention
            // failed." The refusal has to be something an operator can be told.
            Assert.Equal(RegistrationOutcome.Exhausted, refused.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(refused.Explanation));

            // Real words, not the key spoken back. Deliberately not an
            // assertion about WHICH words: every sentence on this surface is
            // provisional until Noel rules on it, and a test that pins
            // unapproved prose makes changing it look like breaking something.
            Assert.False(Lexicon.LooksLikeKey(refused.Explanation));
        }

        [Fact]
        public void AFullStoreRecordsWhatItCouldNotKeepRatherThanDroppingIt()
        {
            var (store, session, cap) = Fresh();

            // Fill it with pending facts, which may never be compacted away.
            for (int i = 0; i < FactStoreCapacity.MaxHistoricalRecords; i++)
                Admit(store, session, cap, Classified("a.w", ShelfLife.Persistent), "occ-" + i);

            FactAdmission overflowed = Admit(store, session, cap,
                Classified("a.w", ShelfLife.Persistent), "occ-overflow");

            Assert.Equal(AdmissionOutcome.CapacityRecorded, overflowed.Outcome);
            Assert.True(store.Overflow.Any);
            Assert.Equal(1, store.Overflow.LostCount);
            Assert.NotNull(store.Overflow.FirstLostUtc);
            Assert.False(string.IsNullOrWhiteSpace(overflowed.Explanation));
        }

        [Fact]
        public void CompactionTakesReviewedHistoryAndNeverSomethingStillOwed()
        {
            var (store, session, cap) = Fresh();

            Fact reviewed = Admit(store, session, cap,
                Classified("a.old", ShelfLife.Persistent), "occ-reviewed").Fact!;
            store.MarkReviewed(reviewed.Identity.EpisodeId, reviewed.MaterialRevision);

            for (int i = 1; i < FactStoreCapacity.MaxHistoricalRecords; i++)
                Admit(store, session, cap, Classified("a.w", ShelfLife.Persistent), "occ-" + i);

            FactAdmission next = Admit(store, session, cap,
                Classified("a.w", ShelfLife.Persistent), "occ-new");

            // Room was made from the reviewed record, and nothing pending was
            // touched — compaction can never decrement the unpresented count as
            // though delivery had occurred.
            Assert.True(next.Accepted);
            Assert.False(store.Overflow.Any);
            Assert.Null(store.Find(reviewed.Identity.EpisodeId));
        }

        [Fact]
        public void AStationFilterNeverHidesTheOnlyPendingItem()
        {
            var (store, session, cap) = Fresh("SERIAL-1");
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;

            Assert.Contains(fact, store.Pending("SERIAL-1"));

            // A filter that empties a non-empty list is a filter that lies
            // about the state of the station.
            Assert.Contains(fact, store.Pending("SOME-OTHER-RADIO"));
        }

        [Fact]
        public void TruncatedDetailSaysSo()
        {
            var (store, session, cap) = Fresh();
            FactAdmission result = store.Admit(
                cap, session.NewOccurrence("slot", "occ"),
                Classified("a.warning", ShelfLife.Persistent),
                ValiditySnapshot.Establish(T0), session.Observe(T0),
                detail: new string('x', FactStoreCapacity.MaxDetailBytes + 100));

            Assert.True(result.Fact!.DetailTruncated);
            Assert.True(result.Fact.Detail.Length <= FactStoreCapacity.MaxDetailBytes);
        }

        // ────────────────────────────────────────────────────────────────
        //  Receipt
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void TheReceiptTokenIsIssuedOnceAndNotReplayedPerRetry()
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;

            store.RecordReceipt(fact.Identity.EpisodeId, ReceiptState.Requested);
            string? token = fact.ReceiptToken;

            store.RecordAttempt(fact.Identity.EpisodeId, DeliveryState.Refused, T0, false);
            store.RecordReceipt(fact.Identity.EpisodeId, ReceiptState.Requested);

            // A second automatic attempt at the same warning makes no new
            // sound: a failing warning must not become a beeping one.
            Assert.Equal(token, fact.ReceiptToken);
        }

        [Theory]
        [InlineData(ReceiptState.Requested)]
        [InlineData(ReceiptState.PlaybackReported)]
        [InlineData(ReceiptState.Unavailable)]
        [InlineData(ReceiptState.Suppressed)]
        public void NoReceiptStateMeansHeardAndNoneOfThemClearsTheDebt(ReceiptState state)
        {
            var (store, session, cap) = Fresh();
            Fact fact = Admit(store, session, cap, Classified("a.warning", ShelfLife.Persistent)).Fact!;

            store.RecordReceipt(fact.Identity.EpisodeId, state);

            // The earcon is the receipt as a product role. A broken output
            // device, a disabled category and deliberate suppression all remain
            // possible, so the pending indication is held independently of it.
            Assert.True(fact.IsPending);
        }

        // ────────────────────────────────────────────────────────────────
        //  Channel failure
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void TheFailureNoticeFiresOnceOnTheEdgeAndNotPerFact()
        {
            var store = new FactStore(1);

            Assert.True(store.NoteChannelHealth(healthy: false));
            Assert.False(store.NoteChannelHealth(healthy: false));
            Assert.False(store.NoteChannelHealth(healthy: false));

            store.NoteChannelHealth(healthy: true);
            Assert.True(store.NoteChannelHealth(healthy: false));
        }

        // ────────────────────────────────────────────────────────────────
        //  No clock
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void NothingInTheStoreAsksHowOldAnythingIs()
        {
            var (store, session, cap) = Fresh();

            // Admitted with an observation time from years ago. If any rule in
            // here counted seconds, this is where it would fire.
            var ancient = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            FactAdmission result = store.Admit(
                cap, session.NewOccurrence("slot", "occ"),
                Classified("a.warning", ShelfLife.Persistent),
                ValiditySnapshot.Establish(ancient),
                new FactProvenance(session.ConnectionIncarnation, store.NextIngestionStamp(), ancient));

            Assert.True(result.Fact!.Validity.IsCurrent);
            Assert.True(result.Fact.IsPending);
            Assert.True(store.IsEligibleForAutomaticDelivery(result.Fact));
        }
    }

    /// <summary>The store's disk half.</summary>
    public class FactJournalTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "jjflex-facts-" + Guid.NewGuid().ToString("N"));

        private static readonly DateTime T0 = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* a temp directory we could not remove is not a test failure */ }
            GC.SuppressFinalize(this);
        }

        private static Fact OneFact(FactStore store, RadioSessionFacts session, ProducerCapability cap)
        {
            var entry = LexiconEntry.Plain("words").WithDelivery(
                DeliveryClassification.Message,
                new DeliveryDescriptor(ShelfLife.Persistent, "pa.temperature", "a.w.history", ReceiptPolicy.Warning));
            return store.Admit(
                cap, session.NewOccurrence("pa.temperature", "occ-1"),
                new LexiconMessage("a.w", entry, Array.Empty<(string, object?)>(), 1),
                ValiditySnapshot.Establish(T0), session.Observe(T0),
                detail: "The PA reached 70 degrees.").Fact!;
        }

        [Fact]
        public void ARestoredRecordIsHistoryAndUnknownWhateverItSaidWhenItWasWritten()
        {
            var written = new FactStore(1);
            var session = new RadioSessionFacts(written, "SERIAL-1");
            Fact live = OneFact(written, session, session.IssueCapability("p"));
            Assert.True(live.Validity.IsCurrent);

            string json = FactJournal.Render(written);
            IReadOnlyList<Fact> restored = FactJournal.ReadShard(json);

            Assert.Single(restored);

            // A loaded file can never assert that a condition is current, and a
            // restored record carries no capability, so it cannot speak on its
            // own. A new connection creates new authority and may relate to an
            // older condition without retroactively confirming it.
            Assert.Equal(ValidityState.Unknown, restored[0].Validity.State);
            Assert.True(restored[0].RestoredFromDisk);
            Assert.Equal("The PA reached 70 degrees.", restored[0].Detail);
        }

        [Fact]
        public void AFileFromAnotherSchemaIsRefusedRatherThanGuessedAt()
        {
            Assert.Throws<System.Text.Json.JsonException>(
                () => FactJournal.ReadShard("""{ "schema": 99, "facts": [] }"""));
        }

        [Fact]
        public void AWriteLandsAtomicallyAndTheStatusSaysSo()
        {
            var store = new FactStore(1);
            var session = new RadioSessionFacts(store, "SERIAL-1");
            OneFact(store, session, session.IssueCapability("p"));

            using var journal = new FactJournal(store, _dir, "shard-a");
            Assert.True(journal.TakeLease());
            Assert.True(journal.Write());

            Assert.True(File.Exists(journal.ShardPath));
            Assert.Equal(PersistenceStatus.UpToDate, store.Persistence);
        }

        [Fact]
        public void TwoProcessesWriteTwoShardsAndNeitherOverwritesTheOther()
        {
            var one = new FactStore(1);
            var two = new FactStore(2);

            using var a = new FactJournal(one, _dir, "shard-a");
            using var b = new FactJournal(two, _dir, "shard-b");

            Assert.True(a.TakeLease());
            Assert.True(b.TakeLease());
            Assert.NotEqual(a.ShardPath, b.ShardPath);

            Assert.True(a.Write());
            Assert.True(b.Write());
            Assert.Equal(2, Directory.GetFiles(_dir, "facts-*.json").Length);
        }

        [Fact]
        public void AnUnreadableShardBecomesAReachableRecoveryGapAndNotAnEmptyHealthyList()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "facts-broken.json"), "{ this is not json");

            var store = new FactStore(1);
            using var journal = new FactJournal(store, _dir, "mine");

            int restored = journal.LoadHistory();

            // An empty list after a corrupt file is the worst possible answer,
            // because it looks exactly like nothing having gone wrong.
            Assert.Equal(0, restored);
            Assert.Equal(PersistenceStatus.RecoveryGap, store.Persistence);
            Assert.NotNull(store.PersistenceNote);
            Assert.False(Lexicon.LooksLikeKey(store.PersistenceNote));
        }

        [Fact]
        public void AnotherProcessesShardLoadsAsHistoryOnRestart()
        {
            var old = new FactStore(1);
            var session = new RadioSessionFacts(old, "SERIAL-1");
            OneFact(old, session, session.IssueCapability("p"));

            using (var writer = new FactJournal(old, _dir, "old-process"))
            {
                Assert.True(writer.TakeLease());
                Assert.True(writer.Write());
            }   // lease released, as a process exiting would

            var fresh = new FactStore(2);
            using var reader = new FactJournal(fresh, _dir, "new-process");
            Assert.Equal(1, reader.LoadHistory());
            Assert.Single(fresh.All);
            Assert.True(fresh.All[0].RestoredFromDisk);
        }

        [Fact]
        public void ALiveShardIsSkippedBecauseAHalfWrittenSnapshotIsNotHistory()
        {
            var other = new FactStore(1);
            var session = new RadioSessionFacts(other, "SERIAL-1");
            OneFact(other, session, session.IssueCapability("p"));

            using var held = new FactJournal(other, _dir, "still-running");
            Assert.True(held.TakeLease());
            Assert.True(held.Write());

            var fresh = new FactStore(2);
            using var reader = new FactJournal(fresh, _dir, "new-process");
            Assert.Equal(0, reader.LoadHistory());
        }
    }
}
