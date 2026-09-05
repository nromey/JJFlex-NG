#nullable enable

using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The stranded-focus sentinel's decision table (#395 follow-on). The one
    /// rule that must never loosen is pinned first: a FOREIGN foreground is
    /// never repaired over — an operator reading email while a dialog of ours
    /// waits is a choice, and a timer that steals the foreground back is
    /// worse than the outage the sentinel exists to end.
    /// </summary>
    public class StrandedFocusSentinelTests
    {
        [Fact]
        public void AForeignForegroundIsNeverRepairedOver()
        {
            var sentinel = new StrandedFocusSentinel();

            // However long another application holds the foreground, the
            // sentinel does nothing — this is the operator being elsewhere.
            for (int i = 0; i < 100; i++)
            {
                Assert.False(sentinel.NoteAndDecide(
                    StrandedFocusSentinel.Observation.ForeignForeground));
            }
        }

        [Fact]
        public void AForeignForegroundResetsTheDebounce()
        {
            // One bad sample, then the operator lands in another app: the
            // black-hole evidence is stale and must not be banked.
            var sentinel = new StrandedFocusSentinel();
            sentinel.NoteAndDecide(StrandedFocusSentinel.Observation.NoForegroundAnywhere);
            sentinel.NoteAndDecide(StrandedFocusSentinel.Observation.ForeignForeground);

            Assert.False(sentinel.NoteAndDecide(
                StrandedFocusSentinel.Observation.NoForegroundAnywhere));
        }

        [Fact]
        public void HealthyResetsTheDebounce()
        {
            var sentinel = new StrandedFocusSentinel();
            sentinel.NoteAndDecide(StrandedFocusSentinel.Observation.OursWithDeadFocus);
            sentinel.NoteAndDecide(StrandedFocusSentinel.Observation.Healthy);

            Assert.False(sentinel.NoteAndDecide(
                StrandedFocusSentinel.Observation.OursWithDeadFocus));
        }

        [Fact]
        public void OneBadObservationIsATransitionNotABlackHole()
        {
            // Window churn passes through no-foreground moments legitimately —
            // one window closing into the next opening. A single sample must
            // never repair.
            var sentinel = new StrandedFocusSentinel();

            Assert.False(sentinel.NoteAndDecide(
                StrandedFocusSentinel.Observation.NoForegroundAnywhere));
        }

        [Fact]
        public void SustainedNoForegroundRepairs()
        {
            var sentinel = new StrandedFocusSentinel();
            bool repaired = false;
            for (int i = 0;
                 i < StrandedFocusSentinel.ConsecutiveBadObservationsBeforeRepair;
                 i++)
            {
                repaired = sentinel.NoteAndDecide(
                    StrandedFocusSentinel.Observation.NoForegroundAnywhere);
            }

            Assert.True(repaired);
        }

        [Fact]
        public void SustainedDeadFocusInOurOwnProcessRepairs()
        {
            var sentinel = new StrandedFocusSentinel();
            bool repaired = false;
            for (int i = 0;
                 i < StrandedFocusSentinel.ConsecutiveBadObservationsBeforeRepair;
                 i++)
            {
                repaired = sentinel.NoteAndDecide(
                    StrandedFocusSentinel.Observation.OursWithDeadFocus);
            }

            Assert.True(repaired);
        }

        [Fact]
        public void TheTwoBlackHoleKindsAccumulateTogether()
        {
            // A black hole can drift between "no foreground at all" and
            // "activation stranded on our shell" as Windows shuffles; the
            // debounce counts the outage, not the flavour.
            var sentinel = new StrandedFocusSentinel();
            sentinel.NoteAndDecide(StrandedFocusSentinel.Observation.NoForegroundAnywhere);

            Assert.True(sentinel.NoteAndDecide(
                StrandedFocusSentinel.Observation.OursWithDeadFocus));
        }

        [Fact]
        public void ARepairResetsTheDebounceForTheNextRound()
        {
            // A repair that does not take is retried a full debounce later,
            // not every tick — reactivation attempts must not machine-gun.
            var sentinel = new StrandedFocusSentinel();
            sentinel.NoteAndDecide(StrandedFocusSentinel.Observation.NoForegroundAnywhere);
            Assert.True(sentinel.NoteAndDecide(
                StrandedFocusSentinel.Observation.NoForegroundAnywhere));

            Assert.False(sentinel.NoteAndDecide(
                StrandedFocusSentinel.Observation.NoForegroundAnywhere));
        }

        [Fact]
        public void TheOutageWindowIsSecondsNotMinutes()
        {
            // The whole point: interval times debounce is the worst-case
            // outage before repair. The field baseline this replaces was 195
            // seconds of silence (2026-08-30, session one) ended only by the
            // operator's own Alt+Tab. Keep the product well under ten
            // seconds; keep the interval slow enough to be inert.
            int worstCaseMs = StrandedFocusSentinel.CheckIntervalMs
                * StrandedFocusSentinel.ConsecutiveBadObservationsBeforeRepair;

            Assert.InRange(worstCaseMs, 2_000, 10_000);
            Assert.InRange(StrandedFocusSentinel.CheckIntervalMs, 1_000, 5_000);
            Assert.True(StrandedFocusSentinel.ConsecutiveBadObservationsBeforeRepair >= 2,
                "a single mid-transition sample must never repair");
        }

        // ────────────────────────────────────────────────────────────────
        //  The foreground-theft rule (Sprint 44 Track Q, #529)
        //
        //  The measured case: a modal of ours up, the operator idle, and the
        //  foreground gone to another process with no keystroke and no
        //  click. The discriminator is the OS's last-input time against the
        //  last tick at which the foreground was ours.
        // ────────────────────────────────────────────────────────────────

        private const int Tick = StrandedFocusSentinel.CheckIntervalMs;
        private const int Debounce = StrandedFocusSentinel.ConsecutiveBadObservationsBeforeRepair;

        private static StrandedFocusSentinel.Sample At(
            StrandedFocusSentinel.Observation observation, long now, long lastInput,
            bool modal = true, bool protectedForeign = false)
            => new(observation, now, lastInput, InputEvidenceKnown: true,
                OurModalIsUp: modal, ForeignIsProtected: protectedForeign);

        /// <summary>Feed foreign samples through one debounce and return the last verdict.</summary>
        private static StrandedFocusSentinel.Verdict ForeignThroughDebounce(
            StrandedFocusSentinel sentinel, ref long now, long lastInput,
            bool modal = true, bool protectedForeign = false)
        {
            var verdict = StrandedFocusSentinel.Verdict.Nothing;
            for (int i = 0; i < Debounce; i++)
            {
                now += Tick;
                verdict = sentinel.Decide(At(StrandedFocusSentinel.Observation.ForeignForeground,
                    now, lastInput, modal, protectedForeign));
            }
            return verdict;
        }

        [Fact]
        public void TheMeasuredCaseIsReclaimedAndAnnounced()
        {
            // 2026-09-02: Select Radio up and modal, the operator's last
            // input long before, the foreground ours — then another process
            // has it, and the operator has still touched nothing.
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000, lastInput = 1_000;
            Assert.Equal(StrandedFocusSentinel.Verdict.Nothing,
                sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, lastInput)));

            Assert.Equal(StrandedFocusSentinel.Verdict.ReclaimFromForeignThief,
                ForeignThroughDebounce(sentinel, ref now, lastInput));
        }

        [Fact]
        public void OneForeignSampleIsNotATheft()
        {
            // A window that flashes up and goes away is not a thief; the
            // same debounce as the black holes applies.
            var sentinel = new StrandedFocusSentinel();
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, 10_000, 1_000));

            Assert.Equal(StrandedFocusSentinel.Verdict.Nothing,
                sentinel.Decide(At(StrandedFocusSentinel.Observation.ForeignForeground, 12_000, 1_000)));
        }

        [Fact]
        public void InputAfterOurLastTenureMeansTheOperatorWentThere_NeverRepaired()
        {
            // THE rule that must never loosen. An Alt+Tab, a click, a
            // keystroke in the other window — any input after the last
            // moment we held the foreground — and the sentinel stands down
            // for as long as it stays true, however long that is.
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, 1_000));

            long theAltTab = now + 500;   // after our last tenure
            for (int i = 0; i < 300; i++)
            {
                now += Tick;
                Assert.Equal(StrandedFocusSentinel.Verdict.Nothing,
                    sentinel.Decide(At(StrandedFocusSentinel.Observation.ForeignForeground, now, theAltTab)));
            }
        }

        [Fact]
        public void InputAtOurLastTenureIsAmbiguousAndCountsAsIdle()
        {
            // Input stamped at the very tick we last held the foreground is
            // on the "idle" side: the transition happened after it.
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, now));

            Assert.Equal(StrandedFocusSentinel.Verdict.ReclaimFromForeignThief,
                ForeignThroughDebounce(sentinel, ref now, lastInput: 10_000));
        }

        [Fact]
        public void WithoutAModalOfOursNothingIsReclaimed()
        {
            // A modeless window left open while the operator uses the rest of
            // the application, or another program, is not a lockout.
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, 1_000, modal: false));

            for (int round = 0; round < 5; round++)
            {
                Assert.Equal(StrandedFocusSentinel.Verdict.Nothing,
                    ForeignThroughDebounce(sentinel, ref now, 1_000, modal: false));
            }
        }

        [Fact]
        public void AProtectedForeignWindowIsNeverTakenFrom()
        {
            // A credential picker, UAC, the secure desktop, a sign-in flow
            // of our own: taking the keyboard from those is the one actively
            // harmful case.
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, 1_000));

            for (int round = 0; round < 5; round++)
            {
                Assert.Equal(StrandedFocusSentinel.Verdict.Nothing,
                    ForeignThroughDebounce(sentinel, ref now, 1_000, protectedForeign: true));
            }
        }

        [Fact]
        public void AForegroundWeNeverHeldWasNotTakenFromUs()
        {
            // The dialog opened behind something and never got the
            // foreground: that is the 2026-08-18 activation defect, handled
            // at ContentRendered, not a theft.
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000;

            for (int round = 0; round < 5; round++)
            {
                Assert.Equal(StrandedFocusSentinel.Verdict.Nothing,
                    ForeignThroughDebounce(sentinel, ref now, 1_000));
            }
        }

        [Fact]
        public void WithoutInputEvidenceAForeignForegroundIsNeverTouched()
        {
            // The original API, and any sample that could not read the
            // last-input clock: no evidence, no theft, no reclaim.
            var sentinel = new StrandedFocusSentinel();
            sentinel.Decide(StrandedFocusSentinel.Sample.WithoutInputEvidence(
                StrandedFocusSentinel.Observation.Healthy));

            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(StrandedFocusSentinel.Verdict.Nothing,
                    sentinel.Decide(StrandedFocusSentinel.Sample.WithoutInputEvidence(
                        StrandedFocusSentinel.Observation.ForeignForeground)));
            }
        }

        [Fact]
        public void DeadFocusOnOurOwnForegroundStillCountsAsOurTenure()
        {
            // The foreground was ours (with no focus window) when the thief
            // took it; the operator was idle in us all the same.
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.OursWithDeadFocus, now, 1_000));

            Assert.Equal(StrandedFocusSentinel.Verdict.ReclaimFromForeignThief,
                ForeignThroughDebounce(sentinel, ref now, 1_000));
        }

        [Fact]
        public void APersistentThiefIsFoughtTwiceThenReportedOnceThenLeftAlone()
        {
            // Reclaim, the thief re-steals, reclaim again, it re-steals again:
            // stand down and say so ONCE, then nothing — not a tug-of-war
            // announced every four seconds.
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000, lastInput = 1_000;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, lastInput));

            var verdicts = new System.Collections.Generic.List<StrandedFocusSentinel.Verdict>();
            for (int round = 0; round < StrandedFocusSentinel.MaxReclaimsPerIdleStretch + 3; round++)
            {
                verdicts.Add(ForeignThroughDebounce(sentinel, ref now, lastInput));
                // Our reclaim took, briefly: one healthy tick before the
                // thief takes it back.
                now += Tick;
                sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, lastInput));
            }

            var expected = new System.Collections.Generic.List<StrandedFocusSentinel.Verdict>();
            for (int i = 0; i < StrandedFocusSentinel.MaxReclaimsPerIdleStretch; i++)
                expected.Add(StrandedFocusSentinel.Verdict.ReclaimFromForeignThief);
            expected.Add(StrandedFocusSentinel.Verdict.StandDownThiefPersists);
            expected.Add(StrandedFocusSentinel.Verdict.Nothing);
            expected.Add(StrandedFocusSentinel.Verdict.Nothing);
            Assert.Equal(expected, verdicts);
        }

        [Fact]
        public void AFailedReclaimIsRetriedAFullDebounceLaterThenCapped()
        {
            // SetForegroundWindow refused: the foreground stays foreign with
            // the operator still idle. Retry once, then stop machine-gunning.
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000, lastInput = 1_000;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, lastInput));

            Assert.Equal(StrandedFocusSentinel.Verdict.ReclaimFromForeignThief,
                ForeignThroughDebounce(sentinel, ref now, lastInput));
            now += Tick;
            Assert.Equal(StrandedFocusSentinel.Verdict.Nothing,
                sentinel.Decide(At(StrandedFocusSentinel.Observation.ForeignForeground, now, lastInput)));
            now += Tick;
            Assert.Equal(StrandedFocusSentinel.Verdict.ReclaimFromForeignThief,
                sentinel.Decide(At(StrandedFocusSentinel.Observation.ForeignForeground, now, lastInput)));
            Assert.Equal(StrandedFocusSentinel.Verdict.StandDownThiefPersists,
                ForeignThroughDebounce(sentinel, ref now, lastInput));
            Assert.Equal(StrandedFocusSentinel.Verdict.Nothing,
                ForeignThroughDebounce(sentinel, ref now, lastInput));
        }

        [Fact]
        public void OperatorInputAfterAStandDownOpensANewStretch()
        {
            // The cap is per idle stretch. Once the operator does anything at
            // all, a later theft over a fresh idle stretch is repaired again.
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000, lastInput = 1_000;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, lastInput));
            for (int i = 0; i < StrandedFocusSentinel.MaxReclaimsPerIdleStretch; i++)
            {
                ForeignThroughDebounce(sentinel, ref now, lastInput);
                now += Tick;
                sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, lastInput));
            }
            Assert.Equal(StrandedFocusSentinel.Verdict.StandDownThiefPersists,
                ForeignThroughDebounce(sentinel, ref now, lastInput));

            // The operator presses a key in our dialog, then idles again.
            now += Tick;
            long keypress = now;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, keypress));
            now += 60_000;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, keypress));

            Assert.Equal(StrandedFocusSentinel.Verdict.ReclaimFromForeignThief,
                ForeignThroughDebounce(sentinel, ref now, keypress));
        }

        [Fact]
        public void AReclaimResetsTheTheftDebounceLikeTheBlackHoleOne()
        {
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000, lastInput = 1_000;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, lastInput));
            Assert.Equal(StrandedFocusSentinel.Verdict.ReclaimFromForeignThief,
                ForeignThroughDebounce(sentinel, ref now, lastInput));

            now += Tick;
            Assert.Equal(StrandedFocusSentinel.Verdict.Nothing,
                sentinel.Decide(At(StrandedFocusSentinel.Observation.ForeignForeground, now, lastInput)));
        }

        [Fact]
        public void AHealthyTickBetweenForeignTicksResetsTheTheftDebounce()
        {
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000, lastInput = 1_000;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, lastInput));
            now += Tick;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.ForeignForeground, now, lastInput));
            now += Tick;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, lastInput));
            now += Tick;

            Assert.Equal(StrandedFocusSentinel.Verdict.Nothing,
                sentinel.Decide(At(StrandedFocusSentinel.Observation.ForeignForeground, now, lastInput)));
        }

        [Fact]
        public void TheBlackHoleRulesAreUnchangedByTheFullSample()
        {
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, 1_000));

            var verdict = StrandedFocusSentinel.Verdict.Nothing;
            for (int i = 0; i < Debounce; i++)
            {
                now += Tick;
                verdict = sentinel.Decide(At(StrandedFocusSentinel.Observation.NoForegroundAnywhere, now, 1_000));
            }
            Assert.Equal(StrandedFocusSentinel.Verdict.ReactivateOverBlackHole, verdict);
        }

        [Fact]
        public void TheReclaimIsConservativeInItsNumbers()
        {
            // Two reclaims per idle stretch and no more; the announcement
            // waits long enough for the reader's own window announcement to
            // land, and not so long the explanation arrives as a non sequitur.
            Assert.InRange(StrandedFocusSentinel.MaxReclaimsPerIdleStretch, 1, 3);
            Assert.InRange(StrandedFocusSentinel.ReclaimAnnounceDelayMs, 300, 2_000);
        }

        // ────────────────────────────────────────────────────────────────
        //  The operator's off switch (Sprint 45 Track D2)
        //
        //  A note sent to a tester on 2026-09-05 promises "tell me and I will
        //  switch it off". These pin the two halves of keeping that promise:
        //  the switch actually withholds the act, and it withholds NOTHING
        //  else — not the decision, not the gates, not the black-hole repair.
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void TheOffSwitchWithholdsTheReclaimAndNamesItAsSuppressed()
        {
            Assert.Equal(StrandedFocusSentinel.Verdict.ReclaimSuppressedByPreference,
                StrandedFocusSentinel.WithOperatorPreference(
                    StrandedFocusSentinel.Verdict.ReclaimFromForeignThief, reclaimEnabled: false));

            // A distinct verdict rather than Nothing, so the caller can write
            // down what it would have done. "Off" must not mean "invisible":
            // the whole point is that a later diagnostic bundle can say this
            // would have rescued you and you had it switched off.
            Assert.NotEqual(StrandedFocusSentinel.Verdict.Nothing,
                StrandedFocusSentinel.WithOperatorPreference(
                    StrandedFocusSentinel.Verdict.ReclaimFromForeignThief, reclaimEnabled: false));
        }

        [Fact]
        public void OnChangesNothingAtAll()
        {
            foreach (StrandedFocusSentinel.Verdict v in
                     System.Enum.GetValues<StrandedFocusSentinel.Verdict>())
            {
                Assert.Equal(v, StrandedFocusSentinel.WithOperatorPreference(v, reclaimEnabled: true));
            }
        }

        [Fact]
        public void TheOffSwitchNeverTouchesTheBlackHoleRepair()
        {
            // The two provable black holes are not "you were doing something
            // else": in one, nothing on the desktop has the keyboard; in the
            // other, we already hold it. The 2026-08-30 rescue stays armed at
            // every setting, and anyone widening this switch to cover it is
            // answering a complaint that was never about it.
            Assert.Equal(StrandedFocusSentinel.Verdict.ReactivateOverBlackHole,
                StrandedFocusSentinel.WithOperatorPreference(
                    StrandedFocusSentinel.Verdict.ReactivateOverBlackHole, reclaimEnabled: false));
        }

        [Fact]
        public void TheStandDownStillComesThroughWhenSwitchedOff()
        {
            // It marks where a running watchdog would have given up, which
            // belongs in the counterfactual record. The caller says in its own
            // words that nothing was actually taken back.
            Assert.Equal(StrandedFocusSentinel.Verdict.StandDownThiefPersists,
                StrandedFocusSentinel.WithOperatorPreference(
                    StrandedFocusSentinel.Verdict.StandDownThiefPersists, reclaimEnabled: false));
        }

        [Fact]
        public void TheDecisionIsIdenticalAtEitherSetting()
        {
            // The gates were measured and one was verified at the radio on
            // 2026-09-05. The switch is applied AFTER Decide, so the same
            // evidence must reach the same verdict either way — that is what
            // makes the suppressed trace worth reading. Two sentinels, the
            // same 2026-09-02 sequence, compared verdict for verdict.
            var armed = new StrandedFocusSentinel();
            var disarmed = new StrandedFocusSentinel();

            long now = 10_000, lastInput = 1_000;
            armed.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, lastInput));
            disarmed.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, lastInput));

            for (int i = 0; i < 12; i++)
            {
                now += Tick;
                var s = At(StrandedFocusSentinel.Observation.ForeignForeground, now, lastInput);
                Assert.Equal(armed.Decide(s), disarmed.Decide(s));
            }
        }

        [Fact]
        public void TheSuppressedVerdictIsNeverReachedByDecideItself()
        {
            // Decide is the pure policy and knows nothing about preferences.
            // If this ever fails, a preference has been folded into a gate.
            var sentinel = new StrandedFocusSentinel();
            long now = 10_000, lastInput = 1_000;
            sentinel.Decide(At(StrandedFocusSentinel.Observation.Healthy, now, lastInput));

            foreach (var observation in System.Enum.GetValues<StrandedFocusSentinel.Observation>())
            {
                for (int i = 0; i < 8; i++)
                {
                    now += Tick;
                    Assert.NotEqual(StrandedFocusSentinel.Verdict.ReclaimSuppressedByPreference,
                        sentinel.Decide(At(observation, now, lastInput)));
                }
            }
        }
    }
}
