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
    }
}
