using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The settling rule (#453): the reflected-power alarm judges the SHAPE of
    /// the reflected share, not only its level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stable readings are real. On 2026-09-01 the bench 8600 at five
    /// watts into a genuine open port measured <b>4.1 W forward, 3.10 W
    /// back</b> — 76 percent, three judged samples, and the alarm fired as
    /// designed at the third. On 2026-08-22 the same radio into an empty port
    /// at a higher setting measured <b>17.5 W forward, 13.4 W back</b>: the
    /// same 76 percent. A stable fault reads the same number sample after
    /// sample, weeks apart, at different powers.
    /// </para>
    /// <para>
    /// The hunting readings are synthesised, because nobody may key a
    /// transmitter to collect them. What is known: a tester's remote tuner,
    /// driven from a radio with no internal one, hunts for about ten seconds
    /// by his own account and settles to 1.7; while it hunts, the reflected
    /// share is genuinely high and genuinely moving, and the alarm as it stood
    /// ended his transmission a second before the tuner got there.
    /// </para>
    /// <para>
    /// Every test that makes the alarm quieter sits beside one that proves the
    /// measured fault still gets through. A rule that can only be watched not
    /// firing is not yet evidence of anything.
    /// </para>
    /// </remarks>
    public sealed class ReflectedSettlingRuleTests
    {
        // 2026-09-01, five watts into an open port. THE case that must still alarm.
        private const float OpenForward0901 = 4.1f;
        private const float OpenReflected0901 = 3.10f;

        // 2026-08-22, the original bench measurement.
        private const float OpenForward0822 = 17.5f;
        private const float OpenReflected0822 = 13.4f;

        private const double Bound = TransmitSafety.ReflectedSettleBoundSeconds;

        private static TransmitPowerReading Reading(float forwardWatts, float reflectedWatts) =>
            new TransmitPowerReading(forwardWatts, reflectedWatts,
                                     skewMilliseconds: 0f, ageMilliseconds: 15f);

        /// <summary>
        /// A reading whose reflected SHARE is the given fraction, on a steady
        /// twenty-watt carrier.
        /// </summary>
        private static TransmitPowerReading Share(float share, float forwardWatts = 20f) =>
            Reading(forwardWatts, forwardWatts * share);

        /// <summary>
        /// A reading too weak to judge once a twenty-watt peak has set the
        /// floor at two watts — a syllable trough.
        /// </summary>
        private static readonly TransmitPowerReading Trough = Reading(0.5f, 0.4f);

        /// <summary>
        /// One transmission, driven exactly as the live paths drive it: observe,
        /// judge with the same clock, record a deferral, latch the warning.
        /// </summary>
        private sealed class Transmission
        {
            public readonly ReflectedPowerRun Run = new ReflectedPowerRun();
            public bool Warned;
            public readonly List<(double At, TransmitSafety.ReflectedVerdict Verdict)> Ticks =
                new List<(double, TransmitSafety.ReflectedVerdict)>();

            public TransmitSafety.ReflectedVerdict Tick(
                double at, in TransmitPowerReading reading, bool tuning = false)
            {
                Run.Observe(reading, at);
                var verdict = TransmitSafety.JudgeReflected(reading, Run, at, tuning, Warned);
                if (verdict == TransmitSafety.ReflectedVerdict.Deferred) Run.NoteDeferred();
                if (verdict == TransmitSafety.ReflectedVerdict.Warn) Warned = true;
                Ticks.Add((at, verdict));
                return verdict;
            }

            public double? FirstWarnAt =>
                Ticks.Where(t => t.Verdict == TransmitSafety.ReflectedVerdict.Warn)
                     .Select(t => (double?)t.At).FirstOrDefault();

            public double? FirstDeferralAt =>
                Ticks.Where(t => t.Verdict == TransmitSafety.ReflectedVerdict.Deferred)
                     .Select(t => (double?)t.At).FirstOrDefault();

            public int Deferrals =>
                Ticks.Count(t => t.Verdict == TransmitSafety.ReflectedVerdict.Deferred);
        }

        /// <summary>One sample a second — the PTT controller's cadence.</summary>
        private static Transmission OncePerSecond(IEnumerable<float> shares, bool tuning = false)
        {
            var tx = new Transmission();
            int t = 0;
            foreach (float share in shares) tx.Tick(++t, Share(share), tuning);
            return tx;
        }

        // ---- the measured faults still alarm, at the same tick ----

        [Fact]
        public void The_open_port_of_2026_09_01_still_alarms_at_the_third_sample()
        {
            // THE positive control for this whole file. Three identical
            // readings — what the trace shows the alarm fired on — are the
            // definition of a share that is holding still, and a rule that
            // waited on them would have taken the measured, validated alarm
            // and made it late.
            var tx = new Transmission();
            var reading = Reading(OpenForward0901, OpenReflected0901);

            Assert.Equal(TransmitSafety.ReflectedVerdict.Quiet, tx.Tick(1, reading));
            Assert.Equal(TransmitSafety.ReflectedVerdict.Quiet, tx.Tick(2, reading));
            Assert.Equal(TransmitSafety.ReflectedVerdict.Warn, tx.Tick(3, reading));

            Assert.Equal(ReflectedShape.Settled, tx.Run.Shape);
            Assert.Equal(0, tx.Deferrals);
            Assert.True(tx.Run.Sustained);
            Assert.NotEqual(ReflectedShape.TooFew, tx.Run.Shape);
        }

        [Fact]
        public void The_bench_open_port_of_2026_08_22_still_alarms()
        {
            var tx = new Transmission();
            var reading = Reading(OpenForward0822, OpenReflected0822);
            tx.Tick(1, reading);
            tx.Tick(2, reading);

            Assert.Equal(TransmitSafety.ReflectedVerdict.Warn, tx.Tick(3, reading));
            Assert.Equal(0, tx.Deferrals);
        }

        [Fact]
        public void A_high_and_stable_share_alarms_exactly_as_before()
        {
            // The rule that stands: over the threshold, sustained, judged.
            // Holding still is what a bad antenna does, and it is announced at
            // the first sustained sample.
            var tx = OncePerSecond(new[] { 0.76f, 0.76f, 0.76f, 0.76f, 0.76f });

            Assert.Equal(3, tx.FirstWarnAt);
            Assert.Equal(0, tx.Deferrals);
        }

        // ---- the tester's tuner: falling and settling defers, then quiet ----

        [Fact]
        public void A_tuner_hunting_and_settling_is_deferred_and_then_nothing_is_said()
        {
            // The shape that ended a real transmission: three sustained bad
            // samples that were on their way somewhere, then the match. The
            // old rule warned at the third sample. This one holds off, and when
            // the good sample arrives there is nothing left to say.
            var shares = new[] { 0.85f, 0.60f, 0.72f, 0.30f, 0.10f, 0.05f, 0.05f };
            var tx = new Transmission();

            tx.Tick(1, Share(shares[0]));
            tx.Tick(2, Share(shares[1]));
            var third = tx.Tick(3, Share(shares[2]));

            // Positive control for the scenario: this IS the tick the alarm
            // used to fire on. If the run were not sustained here the test
            // would be proving nothing about the deferral.
            Assert.True(tx.Run.Sustained, "three bad samples must be a sustained run");
            Assert.Equal(TransmitSafety.ReflectedVerdict.Deferred, third);
            Assert.Equal(ReflectedShape.Changing, tx.Run.Shape);

            for (int i = 3; i < shares.Length; i++) tx.Tick(i + 1, Share(shares[i]));

            Assert.Null(tx.FirstWarnAt);
            Assert.False(tx.Warned);
            Assert.Equal(1, tx.Deferrals);
            Assert.Equal(1, tx.Run.Recoveries);
            Assert.Equal(0, tx.Run.BadSamples);
        }

        [Fact]
        public void The_recovery_is_recorded_in_words_a_bundle_can_corroborate()
        {
            // A tester's "my tuner said 1.7" could never be checked against a
            // trace, because nothing recorded what the alarm saw the match do.
            // The recovery names the streak's length, where it started, where
            // it was on its last bad sample and what it settled to.
            var tx = new Transmission();
            tx.Tick(1, Share(0.85f));
            tx.Tick(2, Share(0.60f));
            tx.Tick(3, Share(0.72f));
            Assert.False(tx.Run.JustRecovered);

            tx.Tick(4, Share(0.30f));

            Assert.True(tx.Run.JustRecovered);
            Assert.Contains("85%", tx.Run.LastRecovery);
            Assert.Contains("72%", tx.Run.LastRecovery);
            Assert.Contains("30%", tx.Run.LastRecovery);
            Assert.Contains("deferred 1 time", tx.Run.LastRecovery);

            // Once. The next observation is a new fact, not the same one again.
            tx.Tick(5, Share(0.10f));
            Assert.False(tx.Run.JustRecovered);
            Assert.Equal(1, tx.Run.Recoveries);
        }

        // ---- deferring is not cancelling ----

        [Fact]
        public void A_share_that_settles_high_alarms_when_it_settles()
        {
            // The tuner stopped, and where it stopped is still bad. Later than
            // the old rule, but it fires — before the bound, at the first
            // sample where the recent shares agree.
            var tx = OncePerSecond(new[] { 0.90f, 0.60f, 0.80f, 0.72f, 0.72f, 0.72f });

            Assert.Equal(3, tx.FirstDeferralAt);
            Assert.Equal(5, tx.FirstWarnAt);
            Assert.Equal(2, tx.Deferrals);
            Assert.True(tx.FirstWarnAt < 1 + Bound, "settling high must not wait for the bound");
            Assert.Equal(ReflectedShape.Settled, tx.Run.Shape);
        }

        [Fact]
        public void A_share_that_never_settles_alarms_at_the_outer_bound()
        {
            // A tuner that never finds a match is precisely the case the
            // operator most needs telling about. Past the bound the level
            // alone decides, and the level is bad.
            var shares = Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? 0.90f : 0.60f);
            var tx = OncePerSecond(shares);

            double expected = 1 + Bound;   // the streak started at t = 1
            Assert.Equal(expected, tx.FirstWarnAt);
            Assert.DoesNotContain(tx.Ticks,
                t => t.Verdict == TransmitSafety.ReflectedVerdict.Warn && t.At < expected);
            Assert.Equal((int)Bound - 2, tx.Deferrals);   // every tick from 3 to the bound
            Assert.Equal(ReflectedShape.Changing, tx.Run.Shape);
        }

        [Fact]
        public void The_bound_counts_from_the_streak_and_not_from_key_down()
        {
            // A remote tuner that re-hunts a minute into a transmission — the
            // antenna moved — gets the same patience as one hunting at key-
            // down. A bound from key-down would judge it on level alone and
            // end the transmission on the third sample of a legitimate search.
            var good = Enumerable.Repeat(0.02f, 60);
            var hunt = Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? 0.90f : 0.60f);
            var tx = OncePerSecond(good.Concat(hunt));

            Assert.Equal(61, tx.Run.BadStreakStartSeconds);
            Assert.Equal(61 + Bound, tx.FirstWarnAt);
            Assert.Equal(0, tx.Run.Recoveries);   // the good samples ended no streak
        }

        [Fact]
        public void A_deferred_alarm_defers_the_cut_with_it()
        {
            // The cut requires the warning to have fired on an earlier sample,
            // so while the warning is held off nothing can end the
            // transmission — and once it has fired, the next bad sample above
            // ten watts cuts as before.
            var tx = new Transmission();
            var shares = new[] { 0.90f, 0.60f, 0.80f, 0.72f, 0.72f };
            for (int i = 0; i < shares.Length; i++)
            {
                var reading = Share(shares[i]);
                var verdict = tx.Tick(i + 1, reading);
                if (verdict != TransmitSafety.ReflectedVerdict.Warn)
                    Assert.False(TransmitSafety.ShouldCutReflected(
                        settingEnabled: true, tx.Warned, reading, tuning: false),
                        "nothing may cut before the warning has fired");
            }
            Assert.True(tx.Warned);
            Assert.True(TransmitSafety.ShouldCutReflected(
                settingEnabled: true, tx.Warned, Share(0.72f), tuning: false));
        }

        // ---- the operator's own tune carrier (the manual-tune half of #453) ----

        [Fact]
        public void A_manual_tune_stands_the_alarm_down_and_hands_it_straight_back()
        {
            // While the operator's tune carrier is up, even a settled-high
            // share says nothing — the same treatment an ATU cycle gets. The
            // run keeps observing throughout, so the moment the carrier drops
            // the verdict is available at once rather than three samples
            // later.
            var tx = new Transmission();
            var open = Reading(OpenForward0901, OpenReflected0901);
            for (int t = 1; t <= 10; t++)
                Assert.Equal(TransmitSafety.ReflectedVerdict.Quiet, tx.Tick(t, open, tuning: true));

            Assert.True(tx.Run.Sustained, "the run must keep watching while the alarm stands down");
            Assert.Equal(TransmitSafety.ReflectedVerdict.Warn, tx.Tick(11, open, tuning: false));
        }

        [Fact]
        public void The_rule_holds_no_memory_of_the_tuning_flag_so_it_cannot_latch()
        {
            // The hazard the manual-tune wiring had to avoid: the tuner
            // start/stop event carries a start for the operator's carrier and
            // no stop, so a flag latched from it would silence the alarm for
            // good. The rule takes the flag fresh every tick; a hundred ticks
            // of "tuning" leave nothing behind, and the first tick without it
            // warns.
            var tx = new Transmission();
            var open = Reading(OpenForward0901, OpenReflected0901);
            for (int t = 1; t <= 100; t++) tx.Tick(t, open, tuning: true);

            Assert.False(tx.Warned);
            Assert.Equal(TransmitSafety.ReflectedVerdict.Warn, tx.Tick(101, open, tuning: false));
        }

        // ---- the window: time AND samples ----

        [Fact]
        public void At_the_kill_switch_cadence_a_tuner_stepping_once_a_second_is_seen_across_the_step()
        {
            // Four samples a second, the check watcher's cadence. Three
            // samples there cover under a second, so a tuner that steps its
            // relays once a second would look settled between steps and the
            // watch would alarm mid-search. The two-second window sees both
            // sides of the step.
            var tx = new Transmission();
            for (int i = 1; i <= 16; i++)
            {
                double t = i * 0.25;
                float share = t <= 1.0 ? 0.90f : t <= 2.0 ? 0.60f : t <= 3.0 ? 0.80f : 0.30f;
                tx.Tick(t, Share(share));
            }

            Assert.Null(tx.FirstWarnAt);
            Assert.Equal(2.0, tx.FirstDeferralAt);   // the first tick the level rule may speak on
            Assert.Equal(1, tx.Run.Recoveries);
        }

        [Fact]
        public void At_the_kill_switch_cadence_a_stable_open_port_alarms_at_two_seconds()
        {
            // The negative control for the test above: the same cadence, the
            // measured fault, and the alarm at the first tick the meters are
            // trusted — exactly where it was before.
            var tx = new Transmission();
            var open = Reading(OpenForward0901, OpenReflected0901);
            for (int i = 1; i <= 12; i++) tx.Tick(i * 0.25, open);

            Assert.Equal(TransmitSafety.ReflectedWarnSeconds, tx.FirstWarnAt);
            Assert.Equal(0, tx.Deferrals);
        }

        [Fact]
        public void On_speech_the_shape_is_read_over_the_last_judged_samples_however_far_apart()
        {
            // Voice: most ticks fall below the floor and are not judged, so
            // the last three judged samples may be many seconds apart. The
            // window's sample minimum keeps the shape judgeable there; a
            // purely time-based window would hold one sample and have no
            // shape to read.
            var tx = new Transmission();
            tx.Tick(1, Share(0.85f));
            tx.Tick(2, Trough); tx.Tick(3, Trough); tx.Tick(4, Trough);
            tx.Tick(5, Share(0.60f));
            tx.Tick(6, Trough); tx.Tick(7, Trough); tx.Tick(8, Trough);
            var verdict = tx.Tick(9, Share(0.72f));

            Assert.Equal(3, tx.Run.JudgedSamples);
            Assert.Equal(3, tx.Run.RecentShares.Count);
            Assert.Equal(ReflectedShape.Changing, tx.Run.Shape);
            Assert.Equal(TransmitSafety.ReflectedVerdict.Deferred, verdict);
        }

        [Fact]
        public void The_good_sample_before_a_streak_is_not_part_of_its_shape()
        {
            // An abrupt fault — fine, then open — is a change in the match,
            // but it is not the match MOVING. If the good sample were in the
            // window every sudden fault would be deferred by a window's
            // length. Only the streak's own samples count.
            var tx = new Transmission();
            for (int i = 1; i <= 8; i++) tx.Tick(i * 0.25, Share(0.02f));
            var open = Reading(OpenForward0901, OpenReflected0901);
            tx.Tick(2.25, open);
            tx.Tick(2.50, open);
            var third = tx.Tick(2.75, open);

            Assert.Equal(3, tx.Run.RecentShares.Count);
            Assert.Equal(ReflectedShape.Settled, tx.Run.Shape);
            Assert.Equal(TransmitSafety.ReflectedVerdict.Warn, third);
        }

        // ---- bookkeeping ----

        [Fact]
        public void The_deferral_is_counted_per_streak_so_it_can_be_traced_once()
        {
            var run = new ReflectedPowerRun();
            Assert.Equal(1, run.NoteDeferred());
            Assert.Equal(2, run.NoteDeferred());
            Assert.Equal(2, run.DeferredSamples);

            // Three bad then a good: the streak ends, and with it the count.
            run.Observe(Share(0.9f), 1);
            run.Observe(Share(0.6f), 2);
            run.Observe(Share(0.8f), 3);
            run.NoteDeferred();
            run.Observe(Share(0.1f), 4);
            Assert.Equal(0, run.DeferredSamples);
        }

        [Fact]
        public void A_fresh_transmission_forgets_the_shape()
        {
            var tx = OncePerSecond(new[] { 0.85f, 0.60f, 0.72f, 0.30f });
            Assert.Equal(1, tx.Run.Recoveries);

            tx.Run.Reset();

            Assert.Equal(ReflectedShape.TooFew, tx.Run.Shape);
            Assert.Empty(tx.Run.RecentShares);
            Assert.True(double.IsNaN(tx.Run.BadStreakStartSeconds));
            Assert.Equal(0, tx.Run.DeferredSamples);
            Assert.Equal(0, tx.Run.Recoveries);
            Assert.Equal("", tx.Run.LastRecovery);
            Assert.False(tx.Run.JustRecovered);
        }

        [Fact]
        public void The_trace_carries_the_recent_shares_and_their_shape()
        {
            // A deferral that cannot be read back from a bundle is
            // indistinguishable from an alarm that was asleep.
            var tx = OncePerSecond(new[] { 0.85f, 0.60f, 0.72f });
            string s = tx.Run.ToString();

            Assert.Contains("recent shares 85% 60% 72%", s);
            Assert.Contains("changing", s);
            Assert.Contains("deferred 1x", s);

            tx.Tick(4, Share(0.30f));
            Assert.Contains("recovered 1x", tx.Run.ToString());
        }

        [Fact]
        public void The_constants_describe_a_tune_and_not_a_fault()
        {
            // Noel's brackets: ten seconds of hunting is the tester's normal;
            // a minute is not. The window is "the last second or two". The
            // settle span is a meter's jitter, not a threshold.
            Assert.InRange(TransmitSafety.ReflectedSettleBoundSeconds, 10.0, 60.0);
            Assert.InRange(TransmitSafety.ReflectedSettleWindowSeconds, 1.0, 3.0);
            Assert.InRange(TransmitSafety.ReflectedSettleSpan, 0.01f, 0.20f);
            Assert.True(TransmitSafety.ReflectedSettleWindowSeconds
                        < TransmitSafety.ReflectedSettleBoundSeconds);

            // The window can never be shorter than the persistence rule, or a
            // sustained streak could be judged on fewer samples than it took
            // to believe it.
            var run = new ReflectedPowerRun();
            for (int i = 1; i <= TransmitSafety.ReflectedWarnSustainedSamples; i++)
                run.Observe(Share(0.9f), i * 10);   // ten seconds apart: time alone would drop them
            Assert.Equal(TransmitSafety.ReflectedWarnSustainedSamples, run.RecentShares.Count);
        }

        [Fact]
        public void ShouldWarnReflected_is_the_verdict_as_a_plain_answer()
        {
            // The bool wrapper the older tests use must agree with the
            // verdict, in both directions, or the two paths drift apart.
            var open = Reading(OpenForward0901, OpenReflected0901);
            var settled = new ReflectedPowerRun();
            for (int i = 1; i <= 3; i++) settled.Observe(open, i);
            Assert.True(TransmitSafety.ShouldWarnReflected(open, settled, 3, false, false));

            var moving = new ReflectedPowerRun();
            moving.Observe(Share(0.9f), 1);
            moving.Observe(Share(0.6f), 2);
            moving.Observe(Share(0.8f), 3);
            Assert.False(TransmitSafety.ShouldWarnReflected(Share(0.8f), moving, 3, false, false));
            Assert.Equal(TransmitSafety.ReflectedVerdict.Deferred,
                TransmitSafety.JudgeReflected(Share(0.8f), moving, 3, false, false));
        }
    }
}
