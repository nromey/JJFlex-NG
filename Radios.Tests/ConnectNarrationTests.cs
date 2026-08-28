using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 36 Track J, task #212: a connect must never be silent for long
    /// enough to be mistaken for a hang.
    ///
    /// <para><b>The incident these tests are the memory of.</b> 2026-08-26, at
    /// the radio: the operator pressed Connect, heard "slice acquired", then
    /// "setting up", then nothing at all. He waited 12.5 seconds, concluded the
    /// application had hung, and killed it with Alt+F4. The connection had
    /// genuinely failed — but from where he sat a failure and a slow success
    /// produce the identical experience, because a blind operator has no
    /// spinner, no greyed-out button and no progress bar. Silence IS the
    /// failure mode.</para>
    ///
    /// <para>So the bar these tests hold is not "does it announce more". It is:
    /// at every moment of a connect, can a listener answer "is this still
    /// happening, and what is it doing" — and when it dies, is the reason
    /// SPOKEN rather than only written to a log.</para>
    ///
    /// <para>Every one of these runs without a radio, a window or a voice,
    /// which is the point of the narrator being a model rather than a method on
    /// the connecting form. The form can only be exercised by connecting to
    /// hardware; a decision object can be exercised by a list of strings.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ConnectNarrationTests
    {
        private const string Radio = "FLEX-8600";

        /// <summary>A clock the test drives by hand, in milliseconds.</summary>
        private sealed class Clock
        {
            public long Now;
            public Func<long> Read => () => Now;
            public void Advance(long ms) => Now += ms;
        }

        /// <summary>
        /// Everything a listener would get out of one event, flattened so a
        /// test can read a whole connect as a script.
        /// </summary>
        private sealed record Heard(string Spoken, string Armed, bool Stopped, int Tone);

        private static Heard Apply(ConnectNarrationStep s)
        {
            var spoken = s.SpeakExtra ?? (s.Speak ? s.StatusText : null);
            return new Heard(spoken, s.Arm?.StillTerse, s.StopVoice, s.PlayPhaseTone ? s.Phase : 0);
        }

        private static List<Heard> Run(ConnectNarrator n, params string[] events) =>
            events.Select(e => Apply(n.OnEvent(e))).ToList();

        // ------------------------------------------------------------------
        // The hole the operator fell into
        // ------------------------------------------------------------------

        /// <summary>
        /// THE REGRESSION TEST FOR THE INCIDENT. The station-name wait runs for
        /// up to 45 seconds and, before this, said nothing for any of them.
        /// </summary>
        [Fact]
        public void The_station_name_wait_arms_a_heartbeat()
        {
            var clock = new Clock();
            var n = new ConnectNarrator(Radio, clock.Read);

            var step = n.OnEvent("start_station_name_wait_begin");

            Assert.NotNull(step.Arm);
            Assert.Equal("Still setting up.", step.Arm.StillTerse);
            Assert.Equal("Still setting up FLEX-8600.", step.Arm.StillChatty);
        }

        /// <summary>
        /// The connect leg before Start() — a SmartLink session, a sign-in, a
        /// hole punch, a TLS handshake — is outside the phase ladder entirely,
        /// so the window has to arm its own cover the moment it appears.
        /// </summary>
        [Fact]
        public void The_window_opens_with_a_heartbeat_already_armed()
        {
            var n = new ConnectNarrator(Radio, new Clock().Read);

            var opening = n.OpeningVoice();

            Assert.Equal("Still connecting.", opening.StillTerse);
            Assert.Equal("Still connecting to FLEX-8600.", opening.StillChatty);
        }

        /// <summary>
        /// Every wait in the walk is covered by something. Nothing between the
        /// window opening and the radio answering leaves the operator with no
        /// voice running.
        /// </summary>
        [Fact]
        public void No_wait_in_the_walk_is_left_uncovered()
        {
            var clock = new Clock();
            var n = new ConnectNarrator(Radio, clock.Read);

            // Opening covers the connect leg.
            Assert.NotNull(n.OpeningVoice());

            clock.Advance(4_000);
            // Slices in: the antenna round trip starts, and gets its own cover.
            Assert.NotNull(n.OnEvent("start_slices_available").Arm);

            clock.Advance(9_000);
            // Antennas in: the station-name wait starts within microseconds and
            // arms the cover for it.
            n.OnEvent("start_antenna_available");
            Assert.NotNull(n.OnEvent("start_station_name_wait_begin").Arm);
        }

        // ------------------------------------------------------------------
        // The fast case must not become a conversation
        // ------------------------------------------------------------------

        /// <summary>
        /// A LAN connect settles in about three seconds with sub-second phases.
        /// It said nothing before and it says nothing now — the heartbeats it
        /// arms are all stopped before their first repeat is due.
        /// </summary>
        [Fact]
        public void A_fast_connect_says_nothing()
        {
            var clock = new Clock();
            var n = new ConnectNarrator(Radio, clock.Read);

            var heard = new List<Heard>();
            foreach (var e in new[] { "start_slices_available", "start_antenna_available",
                                      "start_station_name_wait_begin", "station_name_set" })
            {
                clock.Advance(120);
                heard.Add(Apply(n.OnEvent(e)));
            }

            Assert.All(heard, h => Assert.Null(h.Spoken));
            Assert.All(heard, h => Assert.Equal(0, h.Tone));
        }

        /// <summary>
        /// The threshold is a floor, not a rounding. A phase that lasted exactly
        /// as long as the threshold is announced.
        /// </summary>
        [Fact]
        public void A_phase_at_exactly_the_threshold_is_announced()
        {
            var clock = new Clock();
            var n = new ConnectNarrator(Radio, clock.Read);

            clock.Advance(ConnectNarrator.PhaseAnnounceThresholdMs);
            var step = n.OnEvent("start_slices_available");

            Assert.True(step.Speak);
            Assert.True(step.PlayPhaseTone);
        }

        // ------------------------------------------------------------------
        // The sentences, in order
        // ------------------------------------------------------------------

        /// <summary>
        /// The whole of a slow connect that succeeds, as a listener hears it.
        /// Pinned verbatim because this is prose an operator lives with, and a
        /// wording change should be a decision rather than a side effect.
        /// </summary>
        [Fact]
        public void A_slow_connect_that_succeeds_reads_as_a_sequence()
        {
            var clock = new Clock();
            var n = new ConnectNarrator(Radio, clock.Read);

            clock.Advance(3_000);
            var slices = n.OnEvent("start_slices_available");
            clock.Advance(9_000);
            var antennas = n.OnEvent("start_antenna_available");
            var wait = n.OnEvent("start_station_name_wait_begin");
            clock.Advance(11_000);
            var done = n.OnEvent("station_name_set");

            Assert.Equal("Connected to FLEX-8600. Waiting for slice...", slices.StatusText);
            Assert.True(slices.Speak);
            Assert.Equal("Still waiting.", slices.Arm.StillTerse);
            Assert.Equal("Still waiting for FLEX-8600 to answer.", slices.Arm.StillChatty);

            Assert.Equal("Slice acquired. Setting up...", antennas.StatusText);
            Assert.True(antennas.Speak);

            Assert.Equal("Still setting up.", wait.Arm.StillTerse);

            // The radio answered. Nothing is said, because whoever asked for the
            // connect is about to announce it by name.
            Assert.Null(done.StatusText);
            Assert.Null(done.SpeakExtra);
            Assert.True(done.StopVoice);
        }

        /// <summary>
        /// The counting earcon still runs 1, 2, 3 and still only sounds for
        /// phases the operator actually waited through.
        /// </summary>
        [Fact]
        public void The_counting_earcon_counts_the_phases_that_took_time()
        {
            var clock = new Clock();
            var n = new ConnectNarrator(Radio, clock.Read);

            clock.Advance(3_000);
            var two = n.OnEvent("start_slices_available");
            clock.Advance(3_000);
            var three = n.OnEvent("start_antenna_available");

            Assert.Equal(2, two.Phase);
            Assert.True(two.PlayPhaseTone);
            Assert.Equal(3, three.Phase);
            Assert.True(three.PlayPhaseTone);
        }

        // ------------------------------------------------------------------
        // Failure has to be heard, not logged
        // ------------------------------------------------------------------

        /// <summary>
        /// The radio never sent its station name. The reason is spoken HERE,
        /// while the connecting window still holds focus, because the verdict
        /// that follows it is issued across a window change and a screen reader
        /// flushes its queue on one.
        /// </summary>
        [Fact]
        public void A_setup_that_never_finished_says_so_out_loud()
        {
            var n = new ConnectNarrator(Radio, new Clock().Read);

            var step = n.OnEvent("station_name_timeout");

            Assert.Equal("The radio did not finish setting up.", step.SpeakExtra);
            Assert.True(step.StopVoice);
        }

        /// <summary>The other way setup dies on its own.</summary>
        [Fact]
        public void A_connection_lost_during_setup_says_so_out_loud()
        {
            var n = new ConnectNarrator(Radio, new Clock().Read);

            var step = n.OnEvent("start_connection_lost");

            Assert.Equal("The connection dropped during setup.", step.SpeakExtra);
            Assert.True(step.StopVoice);
        }

        /// <summary>
        /// The retry used to change the window's label and nothing else, which
        /// for a screen-reader operator is the same as saying nothing: a
        /// WinForms label is not read because its text changed.
        /// </summary>
        [Theory]
        [InlineData("start_early_abort")]
        [InlineData("start_grace_abort")]
        public void A_retry_is_spoken_not_just_written(string ev)
        {
            var n = new ConnectNarrator(Radio, new Clock().Read);

            var step = n.OnEvent(ev);

            Assert.Equal("Connection slow, retrying...", step.StatusText);
            Assert.True(step.Speak);
            Assert.True(step.StopVoice);
        }

        /// <summary>
        /// The operator asked to stop. The cancel path owns that sentence and
        /// says it at Critical; the narrator's only job is to stop reassuring
        /// them about work they have just abandoned.
        /// </summary>
        [Theory]
        [InlineData("start_cancelled")]
        [InlineData("start_cancelled_in_station_wait")]
        public void A_cancel_silences_the_heartbeat_without_speaking_over_it(string ev)
        {
            var n = new ConnectNarrator(Radio, new Clock().Read);

            var step = n.OnEvent(ev);

            Assert.Equal("Cancelling...", step.StatusText);
            Assert.False(step.Speak);
            Assert.Null(step.SpeakExtra);
            Assert.True(step.StopVoice);
        }

        // ------------------------------------------------------------------
        // Bookkeeping that has bitten before
        // ------------------------------------------------------------------

        /// <summary>
        /// The first Start() must not re-arm: the opening heartbeat is already
        /// covering that stretch, and re-arming would push its first line a
        /// whole repeat interval further away — quietly widening the very
        /// silence this exists to close.
        /// </summary>
        [Fact]
        public void The_first_start_does_not_restart_the_opening_heartbeat()
        {
            var n = new ConnectNarrator(Radio, new Clock().Read);

            var step = n.OnEvent("start_begin");

            Assert.True(step.IsEmpty);
        }

        /// <summary>
        /// A LATER Start() is a retry after an aborted attempt. The ladder
        /// genuinely begins again, so the cover does too.
        /// </summary>
        [Fact]
        public void A_retry_attempt_starts_the_ladder_over()
        {
            var clock = new Clock();
            var n = new ConnectNarrator(Radio, clock.Read);

            n.OnEvent("start_begin");
            clock.Advance(3_000);
            n.OnEvent("start_slices_available");
            Assert.Equal(2, n.Phase);

            n.OnEvent("start_grace_abort");
            var again = n.OnEvent("start_begin");

            Assert.Equal(1, n.Phase);
            Assert.NotNull(again.Arm);
            Assert.Equal("Still connecting.", again.Arm.StillTerse);

            // And the ladder can now climb again from the bottom.
            clock.Advance(3_000);
            Assert.Equal(2, n.OnEvent("start_slices_available").Phase);
        }

        /// <summary>
        /// A repeated event never walks the phase backwards, but it does still
        /// correct the window text — whatever the radio last said about itself
        /// beats whatever is on screen.
        /// </summary>
        [Fact]
        public void A_repeated_event_updates_the_text_without_moving_the_phase()
        {
            var clock = new Clock();
            var n = new ConnectNarrator(Radio, clock.Read);

            clock.Advance(3_000);
            n.OnEvent("start_slices_available");
            clock.Advance(3_000);
            n.OnEvent("start_antenna_available");

            clock.Advance(3_000);
            var repeat = n.OnEvent("start_slices_available");

            Assert.Equal(3, n.Phase);
            Assert.Equal(0, repeat.Phase);
            Assert.False(repeat.Speak);
            Assert.False(repeat.PlayPhaseTone);
            Assert.Equal("Connected to FLEX-8600. Waiting for slice...", repeat.StatusText);
        }

        /// <summary>
        /// An event nobody narrates costs nothing. The profiler records dozens
        /// of them and the window must not react to each one.
        /// </summary>
        [Fact]
        public void An_unnarrated_event_does_nothing()
        {
            var n = new ConnectNarrator(Radio, new Clock().Read);

            Assert.True(n.OnEvent("hole_punch_port_selected").IsEmpty);
            Assert.True(n.OnEvent("gui_client_added").IsEmpty);
            Assert.True(n.OnEvent(null).IsEmpty);
        }

        /// <summary>
        /// With no name for the radio the sentences still have to parse. They
        /// fall back to the same word the connecting window's own title uses.
        /// </summary>
        [Fact]
        public void An_unnamed_radio_still_produces_a_sentence()
        {
            var n = new ConnectNarrator("   ", new Clock().Read);

            Assert.Equal("Still connecting to radio.", n.OpeningVoice().StillChatty);
        }

        /// <summary>
        /// The heartbeat inherits ProgressVoice's cadence rather than inventing
        /// its own. #212 asked for the mechanism already in use, not a second
        /// one beside it.
        /// </summary>
        [Fact]
        public void The_heartbeat_uses_the_existing_cadence_and_ceiling()
        {
            var n = new ConnectNarrator(Radio, new Clock().Read);

            var v = n.OnEvent("start_station_name_wait_begin").Arm;

            Assert.Equal(ProgressVoice.DefaultRepeatMs, v.RepeatMs);
            Assert.Equal(ProgressVoice.DefaultMaxMs, v.MaxMs);
        }

        // ------------------------------------------------------------------
        // The ceiling has to outlast the wait it covers
        // ------------------------------------------------------------------

        /// <summary>
        /// THE 55.7-SECOND FINDING, and what closing it changed. The
        /// station-name wait declared a 45,000 ms budget and implemented it as
        /// 1,800 turns of a loop that slept 25 ms each — a count of sleeps, so
        /// the wall clock always ran over, measured at 55.7 seconds in the field
        /// trace of the incident.
        ///
        /// <para>A heartbeat that stopped at 45,000 would have gone quiet with
        /// ten seconds still to run, which is the original defect reappearing at
        /// the worst possible moment. It was covered here with a 1.5 multiplier,
        /// and task #293 then fixed the loop instead: it honours a deadline now,
        /// so 45,000 means 45 seconds and the ceiling only has to outlast the
        /// wait's last turn.</para>
        ///
        /// <para>The 55.7-second measurement is kept as the reason the ceiling
        /// is not simply equal to the budget. It no longer has to be COVERED —
        /// that wait cannot happen any more — but a ceiling that lands exactly
        /// on the deadline would still fall silent a turn early.</para>
        /// </summary>
        [Fact]
        public void The_ceiling_outlasts_the_declared_budget()
        {
            var n = new ConnectNarrator(Radio, new Clock().Read);

            var v = n.OnEvent("start_station_name_wait_begin",
                new Dictionary<string, object> { ["maxWaitMs"] = 45_000 }).Arm;

            Assert.Equal(47_000, v.MaxMs);
            Assert.True(v.MaxMs > 45_000,
                "the ceiling must outlast the budget it covers, or the heartbeat "
                + "stops a turn before the wait does");
        }

        /// <summary>
        /// A budget arrives from a JSON-ish payload, so it may not be an int.
        /// Anything unreadable falls back rather than throwing inside a connect.
        /// </summary>
        [Theory]
        [InlineData("not a number")]
        [InlineData(null)]
        [InlineData(0)]
        [InlineData(-1)]
        public void An_unusable_budget_falls_back_to_the_default_ceiling(object budget)
        {
            var n = new ConnectNarrator(Radio, new Clock().Read);

            var v = n.OnEvent("start_station_name_wait_begin",
                new Dictionary<string, object> { ["maxWaitMs"] = budget }).Arm;

            Assert.Equal(ProgressVoice.DefaultMaxMs, v.MaxMs);
        }

        /// <summary>
        /// A wait that declares a SMALL budget still gets the ordinary cover. A
        /// component should not be rewarded for honest timing with less voice
        /// than one that publishes nothing at all.
        /// </summary>
        [Fact]
        public void A_short_budget_never_shrinks_the_cover()
        {
            var n = new ConnectNarrator(Radio, new Clock().Read);

            var v = n.OnEvent("start_station_name_wait_begin",
                new Dictionary<string, object> { ["maxWaitMs"] = 1_000 }).Arm;

            Assert.Equal(ProgressVoice.DefaultMaxMs, v.MaxMs);
        }

        /// <summary>
        /// The cover is now the declared budget plus a small fixed allowance for
        /// the last turn of the wait's loop — NOT a proportional correction.
        /// </summary>
        /// <remarks>
        /// Task #293. The old contract multiplied every budget by 1.5 to cover a
        /// loop that counted sleeps instead of reading a clock: 45,000 declared,
        /// 55.7 seconds measured. The loops honour a deadline now, so the
        /// remaining overshoot is one turn — additive, and the same size whether
        /// the budget is one second or one minute.
        /// </remarks>
        [Fact]
        public void The_cover_is_the_budget_plus_a_fixed_last_turn_allowance()
        {
            var n = new ConnectNarrator(Radio, new Clock().Read);

            var v = n.OnEvent("start_station_name_wait_begin",
                new Dictionary<string, object> { ["maxWaitMs"] = 45_000 }).Arm;

            Assert.Equal(45_000 + ConnectNarrator.WaitCeilingSlackMs, v.MaxMs);
        }

        /// <summary>
        /// The allowance does not scale with the budget. A multiplier would grow
        /// the fudge in proportion to the number it was correcting, which is the
        /// shape #293 removed.
        /// </summary>
        [Fact]
        public void The_allowance_is_the_same_size_at_every_budget()
        {
            var n = new ConnectNarrator(Radio, new Clock().Read);

            var small = n.OnEvent("start_station_name_wait_begin",
                new Dictionary<string, object> { ["maxWaitMs"] = 60_000 }).Arm;
            var large = n.OnEvent("start_station_name_wait_begin",
                new Dictionary<string, object> { ["maxWaitMs"] = 240_000 }).Arm;

            Assert.Equal(60_000 - 240_000, small.MaxMs - large.MaxMs);
        }
    }
}
