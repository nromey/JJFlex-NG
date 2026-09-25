#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Radios;
using Radios.Facts;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A-series: authority is an issued relationship, checked atomically on
    /// every mutation — never a name, never a connection number.
    /// </summary>
    /// <remarks>
    /// Every owner here is SYNTHETIC. These tests prove the store attributes a
    /// publication to the component that registered for it; they prove nothing
    /// about any real producer, and nothing about a radio.
    /// </remarks>
    public class FactAuthorityTests
    {
        private static readonly DateTime T0 = FactKit.T0;

        [Fact]
        public void A1_RegisteredOwnerAndSlotAreRequiredAtPublication_SyntheticOwners()
        {
            var kit = new FactKit();
            FactSession session = kit.Session();
            SlotPublisher rightful = kit.HotSlot(session, "pa");
            SlotPublisher otherOwnerSameConnection = kit.Slot(session, kit.OtherHot, FactKit.Temperature, "pa");
            SlotPublisher sameOwnerOtherSlot = kit.HotSlot(session, "psu");

            PublicationResult opened = FactKit.OpenHot(rightful);
            Assert.Equal(PublicationOutcome.Accepted, opened.Outcome);
            EpisodeHandle target = opened.Handle!;
            FactSnapshot before = kit.Store.Find(target.Id)!;

            var refusals = new List<PublicationOutcome>
            {
                // Wrong owner, same connection.
                otherOwnerSameConnection.Update(target, FactKit.Capture(otherOwnerSameConnection, FactKit.Temp(99m)),
                                                FactTransition.Sample(), before.Revision).Outcome,
                // Right owner, wrong slot.
                sameOwnerOtherSlot.Update(target, FactKit.Capture(sameOwnerOtherSlot, FactKit.Temp(99m)),
                                          FactTransition.Sample(), before.Revision).Outcome,
                // The rightful publisher with an event another slot captured.
                rightful.Update(target, FactKit.Capture(sameOwnerOtherSlot, FactKit.Temp(99m)),
                                FactTransition.Sample(), before.Revision).Outcome,
                // Wrong descriptor: a message this contract does not allow.
                rightful.Open(FactKit.Capture(rightful, FactKit.Temp(71m)), "condition.hot", FactKit.CutKey).Outcome,
                // Wrong claim.
                rightful.Open(FactKit.Capture(rightful, FactKit.Temp(71m)), "stop.confirmed", FactKit.HotKey).Outcome,
            };
            Assert.All(refusals, o => Assert.Equal(PublicationOutcome.AuthorityMismatch, o));

            // Unregistered: an owner cannot register under a contract the
            // composition root never declared for it.
            Assert.Equal(RegistrationOutcome.AuthorityMismatch,
                kit.Registry.Register(kit.Hot, session, FactKit.StopConfirm, new ConditionKey("stop")).Outcome);

            // Another store's publisher cannot touch this store's episode.
            var foreign = new FactKit();
            SlotPublisher foreignSlot = foreign.HotSlot(foreign.Session());
            Assert.Equal(PublicationOutcome.AuthorityMismatch,
                foreignSlot.Update(target, FactKit.Capture(foreignSlot, FactKit.Temp(99m)), FactTransition.Sample(),
                                   before.Revision).Outcome);

            // An ended session's publisher cannot even capture.
            FactSession old = kit.Session("SERIAL-OLD");
            SlotPublisher stale = kit.HotSlot(old);
            old.End(T0, "detached");
            Assert.Equal(CaptureOutcome.ScopeEnded, stale.Capture(FactKit.Temp(99m), T0).Outcome);

            // The target is exactly as it was: evidence, permission, coverage.
            FactSnapshot after = kit.Store.Find(target.Id)!;
            Assert.Equal(before.Revision, after.Revision);
            Assert.Equal(before.ContentFingerprint, after.ContentFingerprint);
            Assert.Equal(before.PermittedNow, after.PermittedNow);
            Assert.Equal(before.Covered.OrderBy(x => x), after.Covered.OrderBy(x => x));
            Assert.Single(kit.Store.All);

            // The refusals aggregated into bounded, reachable integrity rows —
            // one per kind of refusal, counting, not one history entry each.
            var integrity = kit.Store.Issues.Where(i => i.Kind == IssueKind.IntegrityRefusal).ToList();
            Assert.InRange(integrity.Count, 1, 3);
            Assert.True(integrity.Sum(i => i.Count) >= 6);

            // POSITIVE CONTROL: the rightful owner, through the same endpoint.
            PublicationResult update = rightful.Update(target, FactKit.Capture(rightful, FactKit.Temp(71m)),
                                                       FactTransition.Sample(), before.Revision);
            Assert.Equal(PublicationOutcome.Accepted, update.Outcome);
            Assert.Equal(before.Revision + 1, kit.Store.Find(target.Id)!.Revision);
        }

        [Fact]
        public void A1_AdmissionWithoutRegistrationIsNotExpressibleThroughAnyPublicSurface()
        {
            // The legacy shapes Sol found: a public Admit taking a
            // caller-built identity, string slots, and raw recorders.
            string[] forbidden =
            {
                "Admit", "RegisterSlot", "UpdateValidity", "RecordAttempt", "RecordReceipt",
                "RevokeConnection", "Restore", "ResumeAutomatic", "MarkReviewed", "AdvanceQuietBarrier",
                "NextIngestionStamp",
            };
            var publicMethods = typeof(FactStore).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                                                 .Select(m => m.Name).ToHashSet();

            // Positive control: the reflection really sees the store's surface.
            Assert.Contains("Find", publicMethods);
            Assert.Contains("IsEligibleForAutomaticDelivery", publicMethods);

            foreach (string name in forbidden) Assert.DoesNotContain(name, publicMethods);

            // Nothing that carries authority can be constructed by a caller.
            foreach (Type type in new[]
                     {
                         typeof(FactStore), typeof(SlotPublisher), typeof(EpisodeHandle), typeof(CapturedFactEvent),
                         typeof(FactOwner), typeof(FactSession), typeof(FactActivity), typeof(PresentationPlan),
                         typeof(AttemptHandle), typeof(TransportBinding), typeof(ReceiptPermit), typeof(DisplayToken),
                         typeof(SelectedReadGrant), typeof(FactPresentation), typeof(ReceiptEndpoint),
                         typeof(ContinuityReference), typeof(ContinuityView), typeof(ContinuityAssertion),
                     })
            {
                Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            }
        }

        [Fact]
        public void A2_RetireAndPublishHaveOneBoundary_BothOrderings_SyntheticOwner()
        {
            var kit = new FactKit();
            FactSession session = kit.Session();
            SlotPublisher publisher = kit.HotSlot(session);
            PublicationResult opened = FactKit.OpenHot(publisher);
            EpisodeHandle handle = opened.Handle!;

            // A presentation allocated before retirement...
            var transport = new RecordingTransport(kit.Registry, "t", TransportCapability.RequestOnly);
            AttemptHandle attempt = kit.Allocate(kit.PlanAutomatic(handle.Id), transport.Binding);

            // ...and an event captured before it.
            CapturedFactEvent capturedBefore = FactKit.Capture(publisher, FactKit.Temp(71m));

            session.End(T0, "detached");

            // Retirement won: no new start, no current assertion.
            Assert.Equal(AttemptRunOutcome.NotStarted, AttemptRunner.Run(attempt, transport.Submit));
            Assert.Equal(0, transport.NativeCalls);
            Assert.Equal(CaptureOutcome.ScopeEnded, publisher.Capture(FactKit.Temp(72m), T0).Outcome);

            // The genuinely earlier event is admissible only as history.
            PublicationResult late = publisher.Update(handle, capturedBefore, FactTransition.Sample(), opened.Fact!.Revision);
            Assert.Equal(PublicationOutcome.AcceptedAsHistory, late.Outcome);
            FactSnapshot now = kit.Store.Find(handle.Id)!;
            Assert.False(now.Validity.IsCurrent);
            Assert.Equal(EndedKind.EndedObservationContext, now.Validity.Ended);
            Assert.False(now.IsLive);
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(now));

            // The other ordering: a publication that wins the gate is current,
            // and a later retirement ends it as a context, not a resolution.
            var kit2 = new FactKit();
            FactSession s2 = kit2.Session();
            SlotPublisher p2 = kit2.HotSlot(s2);
            PublicationResult o2 = FactKit.OpenHot(p2);
            PublicationResult u2 = p2.Update(o2.Handle!, FactKit.Capture(p2, FactKit.Temp(71m)), FactTransition.Sample(), o2.Fact!.Revision);
            Assert.Equal(PublicationOutcome.Accepted, u2.Outcome);
            Assert.True(kit2.Store.Find(o2.Handle!.Id)!.Validity.IsCurrent);
            s2.End(T0, "detached");
            Assert.False(kit2.Store.Find(o2.Handle!.Id)!.Validity.IsResolved);

            // Ending one session touches nobody else's.
            var kit3 = new FactKit();
            FactSession a = kit3.Session("SERIAL-A");
            FactSession b = kit3.Session("SERIAL-B");
            PublicationResult onA = FactKit.OpenHot(kit3.HotSlot(a));
            FactKit.OpenHot(kit3.HotSlot(b));
            b.End(T0, "B went away");
            FactSnapshot stillA = kit3.Store.Find(onA.Handle!.Id)!;
            Assert.True(stillA.Validity.IsCurrent);
            Assert.True(kit3.Store.IsEligibleForAutomaticDelivery(stillA));
        }

        [Fact]
        public void A2_RetirementRacingPublicationNeverAdmitsACurrentEventCapturedAfterIt()
        {
            // Deterministic barrier, many rounds: one thread publishes, one
            // retires. Whatever interleaving occurs, no update is accepted as
            // current once its event was captured after the retirement point.
            for (int round = 0; round < 40; round++)
            {
                var kit = new FactKit();
                FactSession session = kit.Session();
                SlotPublisher publisher = kit.HotSlot(session);
                PublicationResult opened = FactKit.OpenHot(publisher);
                long revision = opened.Fact!.Revision;
                var accepted = new List<long>();
                using var start = new Barrier(2);

                var publishing = new Thread(() =>
                {
                    start.SignalAndWait();
                    for (int i = 0; i < 50; i++)
                    {
                        CaptureResult c = publisher.Capture(FactKit.Temp(70m + i), T0);
                        if (!c.Captured) break;
                        PublicationResult r = publisher.Update(opened.Handle!, c.Event!, FactTransition.Sample(), revision);
                        if (r.Outcome == PublicationOutcome.Accepted)
                        {
                            accepted.Add(c.Event!.Sequence);
                            revision = r.Fact!.Revision;
                        }
                    }
                });
                publishing.Start();
                start.SignalAndWait();
                Thread.SpinWait(round * 50);
                session.End(T0, "retired mid-stream");
                publishing.Join();

                long endedAt = session.EndedAtSequenceLocked;
                Assert.All(accepted, s => Assert.True(s < endedAt, "an event captured after retirement was admitted as current"));
                Assert.False(kit.Store.Find(opened.Handle!.Id)!.Validity.IsCurrent);
            }
        }

        [Fact]
        public void A3_IdentityDoesNotComeFromLabels_TwoRadiosTwoOwnersEqualLabels()
        {
            var kit = new FactKit();
            // Two owners with IDENTICAL display names, on two radios, same
            // condition text and same occurrence label.
            FactOwner first = kit.Registry.DeclareOwner("same name", FactKit.Temperature);
            FactOwner second = kit.Registry.DeclareOwner("same name", FactKit.Temperature);
            SlotPublisher one = kit.Slot(kit.Session("SERIAL-A"), first, FactKit.Temperature, "pa");
            SlotPublisher two = kit.Slot(kit.Session("SERIAL-B"), second, FactKit.Temperature, "pa");
            Assert.NotEqual(one.SlotOrdinal, two.SlotOrdinal);

            var label = new OpenOptions { OccurrenceLabel = "occ-1" };
            PublicationResult a = FactKit.OpenHot(one, options: label);
            PublicationResult b = FactKit.OpenHot(two, options: label);

            Assert.NotEqual(a.Handle!.Id, b.Handle!.Id);
            Assert.Equal(2, kit.Store.All.Count);

            // Independent updates.
            one.Update(a.Handle, FactKit.Capture(one, FactKit.Temp(71m)), FactTransition.Sample(), a.Fact!.Revision);
            Assert.Equal(a.Fact.Revision + 1, kit.Store.Find(a.Handle.Id)!.Revision);
            Assert.Equal(b.Fact!.Revision, kit.Store.Find(b.Handle.Id)!.Revision);

            // An exact replay is idempotent; the same event identity with other
            // content is a conflict, with a reachable row.
            CapturedFactEvent ev = FactKit.Capture(one, FactKit.Temp(72m));
            long rev = kit.Store.Find(a.Handle.Id)!.Revision;
            Assert.Equal(PublicationOutcome.Accepted, one.Update(a.Handle, ev, FactTransition.Sample(), rev).Outcome);
            Assert.Equal(PublicationOutcome.Duplicate, one.Update(a.Handle, ev, FactTransition.Sample(), rev).Outcome);
            Assert.Equal(PublicationOutcome.EvidenceConflict,
                one.Update(a.Handle, ev, FactTransition.Correction(new[] { new MaterialDeclaration("temperature", FactValue.Of(80m)) }), rev).Outcome);
            Assert.Contains(kit.Store.Issues, i => i.Kind == IssueKind.IntegrityRefusal);

            // Known-good descriptor publication: classification bound from the
            // catalogue through the contract, not from the caller.
            FactSnapshot published = kit.Store.Find(a.Handle.Id)!;
            Assert.Equal(DeliveryClassification.Message, published.Classification);
            Assert.Equal(ShelfLife.Persistent, published.Delivery!.ShelfLife);
            Assert.Equal(FactKit.HotKey, published.MessageKey);
            Assert.Equal(DeliveryPriority.OperatorAlarm, published.Priority);
            Assert.Equal("SERIAL-A", published.RadioIdentity);
        }

        [Fact]
        public void A4_DomainClaimAuthorityIsNarrow_SyntheticWitnessAndConfirmer()
        {
            var kit = new FactKit();
            FactSession session = kit.Session();
            SlotPublisher witness = kit.Slot(session, kit.Witness, FactKit.StopRequest, "stop");
            SlotPublisher confirmer = kit.Slot(session, kit.Confirmer, FactKit.StopConfirm, "stop");

            // The request-only witness cannot assert the confirmation.
            Assert.Equal(PublicationOutcome.AuthorityMismatch,
                witness.Open(FactKit.Capture(witness), "stop.confirmed", FactKit.StopConfirmedKey).Outcome);

            // It can assert what it witnessed.
            PublicationResult requested = witness.Open(FactKit.Capture(witness), "stop.requested", FactKit.StopRequestedKey);
            Assert.Equal(PublicationOutcome.Accepted, requested.Outcome);
            Assert.Equal("stop.requested", requested.Fact!.Claim);
            Assert.Equal(FactKit.StopRequestedKey, requested.Fact.MessageKey);

            // Nor may the witness assert a resolution its contract does not grant.
            Assert.Equal(PublicationOutcome.AuthorityMismatch,
                witness.Resolve(requested.Handle!, FactKit.Capture(witness), requested.Fact.Revision).Outcome);

            // The confirmer, with its typed observation, can.
            PublicationResult confirmed = confirmer.Open(
                FactKit.Capture(confirmer, FactObservation.Of(("confirmed", FactValue.Of(true)))),
                "stop.confirmed", FactKit.StopConfirmedKey);
            Assert.Equal(PublicationOutcome.Accepted, confirmed.Outcome);
            Assert.Equal("stop.confirmed", confirmed.Fact!.Claim);
            Assert.Equal(FactKit.StopConfirmedKey, confirmed.Fact.MessageKey);

            // And its evidence shape is enforced: no flag, no capture.
            Assert.Equal(CaptureOutcome.EvidenceRejected, confirmer.Capture(FactObservation.Empty, FactKit.T0).Outcome);

            // An urgent key cannot buy priority: an ordinary owner cannot use it.
            SlotPublisher notes = kit.NotesSlot(session);
            Assert.Equal(PublicationOutcome.AuthorityMismatch,
                notes.Open(FactKit.Capture(notes), "note", FactKit.StopConfirmedKey).Outcome);
            Assert.Equal(DeliveryPriority.Ordinary, FactKit.OpenNote(notes, FactKit.CutKey).Fact!.Priority);
        }

        [Fact]
        public void A_SequenceExhaustionRefusesRatherThanWrapping()
        {
            var kit = new FactKit();
            SlotPublisher publisher = kit.HotSlot(kit.Session());
            kit.Store.SequenceCeiling = kit.Store.CurrentSequence + 1;

            Assert.True(publisher.Capture(FactKit.Temp(70m), FactKit.T0).Captured);
            CaptureResult refused = publisher.Capture(FactKit.Temp(70m), FactKit.T0);

            Assert.Equal(CaptureOutcome.SequenceExhausted, refused.Outcome);
            Assert.Contains(kit.Store.Issues, i => i.Kind == IssueKind.IntegrityRefusal && i.SourceKey == "sequence");

            // A quiet must still win when the sequence is exhausted.
            long q = kit.Registry.Quiet.Observe("ctrl");
            Assert.True(q >= kit.Store.CurrentSequence);
        }
    }
}
