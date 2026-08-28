using System;
using Radios.Fixer;
using Xunit;
using static Radios.Fixer.FixerTransmitGate;

namespace Radios.Tests
{
    /// <summary>
    /// The gate between a button on a web page and RF leaving the radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property under test is one sentence: <b>the host never takes the
    /// caller's word for a safety fact.</b> Everything that decides whether the
    /// transmitter keys is held by the gate or read from the radio; nothing is
    /// accepted from the request.
    /// </para>
    /// <para>
    /// The failure these guard against is not an attacker. It is a
    /// double-bound event handler, a page left open from an earlier run, or a
    /// retry loop — on a surface that by definition only opens when something
    /// is already wrong.
    /// </para>
    /// </remarks>
    public class FixerTransmitGateTests
    {
        private const string Run = "TX-4K2M";
        private const string Stage = "transmitter-check";

        /// <summary>A clock the test drives, so burst and budget rules are
        /// exercised without any waiting.</summary>
        private sealed class Clock
        {
            public DateTime Now = new DateTime(2026, 8, 25, 7, 0, 0, DateTimeKind.Utc);
            public DateTime Read() => Now;
            public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
        }

        /// <summary>A gate open for a run with the load declared — the state in
        /// which a legitimate request is expected to succeed.</summary>
        private static FixerTransmitGate Ready(Clock clock = null)
        {
            var g = new FixerTransmitGate(clock == null ? (Func<DateTime>)null : clock.Read);
            g.BeginRun(Run);
            g.DeclareLoad("50 ohm dummy load on ANT1", FixerLoadKind.DummyLoad);
            return g;
        }

        private static Decision Ask(FixerTransmitGate g, string stageId = Stage,
                                    string runId = Run, bool transmits = true,
                                    bool radio = true, bool keyed = false,
                                    int power = -1)
            => g.Request(runId, stageId, transmits, radio, keyed, power);

        // ---- the happy path exists, so a refusal below means something ----

        [Fact]
        public void A_legitimate_request_is_granted()
        {
            // The positive control. Without it every refusal test below could
            // pass on a gate that refuses everything.
            Decision d = Ask(Ready());
            Assert.True(d.Allowed);
            Assert.Equal(Refusal.None, d.Why);
        }

        [Fact]
        public void A_grant_says_nothing()
        {
            // Nothing to explain, and an explanation on a grant would get
            // spoken over the thing about to happen.
            Assert.Equal("", Ask(Ready()).Explanation);
        }

        // ---- the load declaration is the gate's reason for existing ----

        [Fact]
        public void Nothing_transmits_until_the_operator_says_what_is_connected()
        {
            var g = new FixerTransmitGate();
            g.BeginRun(Run);          // note: no DeclareLoad

            Decision d = Ask(g);
            Assert.False(d.Allowed);
            Assert.Equal(Refusal.LoadNotDeclared, d.Why);
        }

        [Fact]
        public void A_new_run_forgets_the_load_declaration()
        {
            // THE ONE THAT MATTERS MOST. Carrying a declaration forward lets a
            // run inherit a fact nobody restated — which is exactly how an
            // operator transmits into an antenna the app still believes is a
            // dummy load. The station may have been re-cabled between runs and
            // the app has no way to know.
            var g = Ready();
            Assert.True(Ask(g).Allowed);

            g.BeginRun("TX-9P7Q");
            Assert.Equal("", g.LoadDeclaration);
            Assert.Equal(Refusal.LoadNotDeclared, Ask(g, runId: "TX-9P7Q").Why);
        }

        [Fact]
        public void A_blank_declaration_is_no_declaration()
        {
            var g = new FixerTransmitGate();
            g.BeginRun(Run);
            g.DeclareLoad("   ", FixerLoadKind.DummyLoad);
            Assert.Equal(Refusal.LoadNotDeclared, Ask(g).Why);
        }

        [Fact]
        public void What_was_declared_is_kept_verbatim_for_the_report()
        {
            // FlexRadio will ask what the measurement was taken into, and a
            // measurement whose load is unrecorded cannot be read afterwards.
            var g = Ready();
            Assert.Equal("50 ohm dummy load on ANT1", g.LoadDeclaration);
        }

        // ---- what the load KIND permits (#244, #180) ----

        [Fact]
        public void Nothing_or_unsure_is_an_explicit_refusal_not_a_silent_wait()
        {
            var g = new FixerTransmitGate();
            g.BeginRun(Run);
            g.DeclareLoad("Nothing, or I am not sure", FixerLoadKind.NothingOrUnsure);

            Decision d = Ask(g);
            Assert.Equal(Refusal.LoadForbidsTransmit, d.Why);
            Assert.Contains("not sure", d.Explanation);
            Assert.Contains("unknown load", d.Explanation);
        }

        [Fact]
        public void An_antenna_caps_the_power_and_the_ceiling_is_enforced_not_recorded()
        {
            var g = new FixerTransmitGate();
            g.BeginRun(Run);
            g.DeclareLoad("An antenna", FixerLoadKind.Antenna);

            // At or under the ceiling: an on-air low-power test is fine.
            Assert.True(Ask(g, power: FixerTransmitGate.LowPowerCeilingWatts).Allowed);

            // Over it: refused, with the number and both ways out spoken.
            Decision high = Ask(g, "another-stage", power: 100);
            Assert.Equal(Refusal.PowerTooHighForLoad, high.Why);
            Assert.Contains("100 watts", high.Explanation);
            Assert.Contains("dummy load", high.Explanation);
        }

        [Fact]
        public void Unreadable_power_into_an_antenna_refuses_rather_than_hopes()
        {
            // -1 is "could not be read". A transmit into a live antenna at a
            // power nobody could read is the exact gamble the gate exists to
            // prevent — this must fail CLOSED.
            var g = new FixerTransmitGate();
            g.BeginRun(Run);
            g.DeclareLoad("An antenna", FixerLoadKind.Antenna);

            Assert.Equal(Refusal.PowerTooHighForLoad, Ask(g, power: -1).Why);
        }

        [Fact]
        public void An_amplifier_is_capped_like_an_antenna()
        {
            var g = new FixerTransmitGate();
            g.BeginRun(Run);
            g.DeclareLoad("An amplifier", FixerLoadKind.Amplifier);

            Assert.True(Ask(g, power: 5).Allowed);
            Assert.Equal(Refusal.PowerTooHighForLoad,
                         Ask(g, "another-stage", power: 50).Why);
        }

        [Fact]
        public void A_dummy_load_ignores_the_power_entirely()
        {
            // Nothing radiates, so full test power and unreadable power are
            // both fine — the ceiling belongs to the loads where RF leaves
            // the building.
            var g = Ready();
            Assert.True(Ask(g, power: 100).Allowed);
        }

        [Fact]
        public void The_keyed_witnesses_fire_once_per_real_keying()
        {
            // #236: the host arms the PTT controller's live health watch on
            // these. Once per keying — NoteUnkeyed is deliberately safe to
            // repeat, and a repeat must not disarm someone else's watch.
            var g = Ready();
            int keyed = 0, unkeyed = 0;
            g.OnKeyed = () => keyed++;
            g.OnUnkeyed = () => unkeyed++;

            g.NoteKeyed(Stage);
            g.NoteUnkeyed();
            g.NoteUnkeyed();   // the safe repeat
            g.NoteUnkeyed();

            Assert.Equal(1, keyed);
            Assert.Equal(1, unkeyed);

            // And an unmatched unkey before any keying tells nobody anything.
            var fresh = Ready();
            int phantom = 0;
            fresh.OnUnkeyed = () => phantom++;
            fresh.NoteUnkeyed();
            Assert.Equal(0, phantom);
        }

        [Fact]
        public void A_throwing_witness_never_breaks_the_keying_accounting()
        {
            var g = Ready();
            g.OnKeyed = () => throw new InvalidOperationException("observer bug");
            g.OnUnkeyed = () => throw new InvalidOperationException("observer bug");

            g.NoteKeyed(Stage);
            Assert.True(g.InFlight);
            g.NoteUnkeyed();
            Assert.False(g.InFlight);
            Assert.Equal(1, g.TransmitCount);
        }

        [Fact]
        public void A_remote_declaration_carries_who_and_when_for_the_report()
        {
            // #247: the load was once accepted from an operator a thousand
            // miles from the socket with nothing recorded. The report form
            // now carries the WHEN, from the gate's own clock, and the
            // remote provenance — a declaration with neither cannot be
            // re-evaluated later, and later is when a support conversation
            // will need it.
            var clock = new Clock();
            var g = new FixerTransmitGate(clock.Read);
            g.BeginRun(Run);
            g.DeclareLoad("A dummy load — someone at the station has confirmed "
                          + "it is connected",
                          FixerLoadKind.DummyLoad, declaredRemotely: true);

            Assert.True(g.LoadDeclaredRemotely);
            Assert.Equal(clock.Now, g.LoadDeclaredAtUtc);
            Assert.Equal(
                "A dummy load — someone at the station has confirmed it is connected "
                + "(declared 2026-08-25 07:00 UTC, over a remote session, by an "
                + "operator not at the station)",
                g.LoadDeclarationForReport);

            // And it resets with the run, like every declared fact.
            g.BeginRun("TX-NEW1");
            Assert.False(g.LoadDeclaredRemotely);
            Assert.Null(g.LoadDeclaredAtUtc);
            Assert.Equal("", g.LoadDeclarationForReport);
        }

        [Fact]
        public void A_local_declaration_records_when_it_was_made_too()
        {
            // The WHEN is not a remote nicety: a local declaration is also a
            // statement about one moment, and the station can be re-cabled an
            // hour after the report is written.
            var clock = new Clock();
            var g = Ready(clock);
            Assert.Equal(clock.Now, g.LoadDeclaredAtUtc);
            Assert.Equal("50 ohm dummy load on ANT1 (declared 2026-08-25 07:00 UTC)",
                         g.LoadDeclarationForReport);
        }

        [Fact]
        public void A_remote_dummy_load_is_capped_like_an_antenna()
        {
            // #247: locally the operator can SEE the dummy load; remotely the
            // declaration is on someone else's word, and if that word is
            // stale the cost lands at a station the operator is not at. So a
            // remote declaration keeps every transmit at the ceiling, a dummy
            // load included — the provenance alone is a note, and notes do
            // not stop transmitters.
            var g = new FixerTransmitGate();
            g.BeginRun(Run);
            g.DeclareLoad("A dummy load — someone at the station has confirmed "
                          + "it is connected",
                          FixerLoadKind.DummyLoad, declaredRemotely: true);

            // At or under the ceiling: the checks run, low and useful.
            Assert.True(Ask(g, power: FixerTransmitGate.LowPowerCeilingWatts).Allowed);

            // Over it: refused, and the sentence says whose word the load is on.
            Decision high = Ask(g, "another-stage", power: 100);
            Assert.Equal(Refusal.PowerTooHighForLoad, high.Why);
            Assert.Contains("100 watts", high.Explanation);
            Assert.Contains("remote session", high.Explanation);
            Assert.Contains("turn the power down", high.Explanation);

            // Unreadable power refuses rather than hopes, exactly as it does
            // into a declared antenna.
            Assert.Equal(Refusal.PowerTooHighForLoad,
                         Ask(g, "yet-another-stage", power: -1).Why);
        }

        [Fact]
        public void Not_confirmed_from_a_remote_operator_is_refused_with_the_distance_named()
        {
            // #247: the honest remote answer is "I have not confirmed", and
            // its refusal must not tell the operator to go and connect a
            // dummy load — an instruction a remote operator cannot follow
            // reads as the tool being broken. It names the station's
            // distance and the one thing that WILL open the gate: asking
            // someone who is there.
            var g = new FixerTransmitGate();
            g.BeginRun(Run);
            g.DeclareLoad("I have not confirmed what is connected",
                          FixerLoadKind.NothingOrUnsure, declaredRemotely: true);

            Decision d = Ask(g);
            Assert.Equal(Refusal.LoadForbidsTransmit, d.Why);
            Assert.Contains("unknown load", d.Explanation);
            Assert.Contains("a station you are not at", d.Explanation);
            Assert.Contains("Ask someone at the station", d.Explanation);
            Assert.DoesNotContain("Connect a dummy load", d.Explanation);
        }

        // ---- the radio's own state beats anything the caller claims ----

        [Fact]
        public void A_rig_that_is_already_keyed_stops_a_second_transmit()
        {
            // Keyed by a foot pedal, by another client on a MultiFlex station,
            // or by a previous stage that never came down. The gate does not
            // stack a transmit on top of any of them.
            Decision d = Ask(Ready(), keyed: true);
            Assert.False(d.Allowed);
            Assert.Equal(Refusal.AlreadyInFlight, d.Why);
        }

        [Fact]
        public void No_radio_means_no_transmit()
        {
            Assert.Equal(Refusal.NoRadio, Ask(Ready(), radio: false).Why);
        }

        // ---- a stale caller ----

        [Fact]
        public void A_request_from_an_earlier_run_is_refused()
        {
            var g = Ready();
            Assert.Equal(Refusal.WrongRun, Ask(g, runId: "TX-OLD1").Why);
        }

        [Fact]
        public void With_no_run_open_nothing_transmits()
        {
            var g = new FixerTransmitGate();
            g.DeclareLoad("dummy load", FixerLoadKind.DummyLoad);
            Assert.Equal(Refusal.NoRun, Ask(g).Why);
        }

        [Fact]
        public void Run_ids_are_matched_exactly()
        {
            // Case folding here would let two runs that differ only in case be
            // treated as one, and run ids are read down a telephone.
            var g = Ready();
            Assert.Equal(Refusal.WrongRun, Ask(g, runId: Run.ToLowerInvariant()).Why);
        }

        // ---- the double-fire, which is the realistic page bug ----

        [Fact]
        public void A_stage_cannot_transmit_twice_without_being_asked_to()
        {
            var g = Ready();
            Assert.True(Ask(g).Allowed);
            g.NoteKeyed(Stage);
            g.NoteUnkeyed();

            Assert.Equal(Refusal.StageAlreadyTransmitted, Ask(g).Why);
        }

        [Fact]
        public void An_explicit_re_run_clears_it_and_a_double_fire_does_not()
        {
            // The distinction the flag exists for: a re-run announces itself, a
            // double-fire never does.
            var g = Ready();
            Ask(g);
            g.NoteKeyed(Stage);
            g.NoteUnkeyed();

            g.AllowReRun(Stage);
            Assert.True(Ask(g).Allowed);
        }

        [Fact]
        public void Re_running_one_stage_does_not_unlock_another()
        {
            var g = Ready();
            Ask(g); g.NoteKeyed(Stage); g.NoteUnkeyed();
            Ask(g, "tone-ladder"); g.NoteKeyed("tone-ladder"); g.NoteUnkeyed();

            g.AllowReRun(Stage);
            Assert.True(Ask(g).Allowed);
            Assert.Equal(Refusal.StageAlreadyTransmitted, Ask(g, "tone-ladder").Why);
        }

        [Fact]
        public void A_transmit_in_flight_blocks_the_next_one()
        {
            var g = Ready();
            Ask(g);
            g.NoteKeyed(Stage);          // deliberately no unkey

            Assert.Equal(Refusal.AlreadyInFlight, Ask(g, "tone-ladder").Why);
        }

        // ---- the stage must have said it transmits ----

        [Fact]
        public void A_stage_that_never_declared_itself_a_transmitting_stage_is_refused()
        {
            // The page shows "this step transmits" next to the run control. A
            // step that keys without having said so is a blind operator being
            // surprised by their own radio.
            Assert.Equal(Refusal.StageDoesNotTransmit,
                         Ask(Ready(), transmits: false).Why);
        }

        // ---- abandoning ----

        [Fact]
        public void After_abandoning_a_run_nothing_transmits_again()
        {
            var g = Ready();
            g.AbortRun();
            Assert.True(g.Aborted);
            Assert.Equal(Refusal.RunAborted, Ask(g).Why);
        }

        [Fact]
        public void Abandoning_outranks_every_other_reason()
        {
            // Whatever else is wrong, "you stopped it" is the true and useful
            // thing to say.
            var g = Ready();
            g.AbortRun();
            Assert.Equal(Refusal.RunAborted,
                         Ask(g, runId: "TX-OLD1", radio: false, keyed: true).Why);
        }

        [Fact]
        public void A_new_run_clears_an_abandonment()
        {
            var g = Ready();
            g.AbortRun();
            g.BeginRun("TX-NEW1");
            g.DeclareLoad("dummy load", FixerLoadKind.DummyLoad);
            Assert.False(g.Aborted);
            Assert.True(Ask(g, runId: "TX-NEW1").Allowed);
        }

        // ---- the runaway guard, which is about rate, not volume ----

        [Fact]
        public void A_repeating_handler_is_stopped_within_a_few_transmits()
        {
            // No clock advance at all: the shape of a loop.
            var clock = new Clock();
            var g = Ready(clock);

            int granted = 0;
            for (int i = 0; i < 40; i++)
            {
                if (!Ask(g, "stage-" + i).Allowed) break;
                granted++;
                g.NoteKeyed("stage-" + i);
                g.NoteUnkeyed();
            }

            Assert.Equal(BurstLimit, granted);
            Assert.Equal(Refusal.TooFast, Ask(g, "stage-99").Why);
        }

        [Fact]
        public void A_person_pressing_a_button_repeatedly_is_never_stopped()
        {
            // The guard must not fire during honest work. A guard that does
            // teaches operators to distrust guards.
            var clock = new Clock();
            var g = Ready(clock);

            for (int i = 0; i < 20; i++)
            {
                Decision d = Ask(g, "stage-" + i);
                Assert.True(d.Allowed, "refused an honest press at " + i + ": " + d.Why);
                g.NoteKeyed("stage-" + i);
                clock.Advance(2);       // two seconds of transmit
                g.NoteUnkeyed();
                clock.Advance(4);       // and four before pressing again
            }
        }

        [Fact]
        public void The_window_rolls_so_the_refusal_is_temporary()
        {
            var clock = new Clock();
            var g = Ready(clock);

            for (int i = 0; i < BurstLimit; i++)
            {
                Ask(g, "s" + i);
                g.NoteKeyed("s" + i);
                g.NoteUnkeyed();
            }
            Assert.Equal(Refusal.TooFast, Ask(g, "later").Why);

            clock.Advance(BurstWindowSeconds + 1);
            Assert.True(Ask(g, "later").Allowed);
        }

        // ---- budget accounting ----

        [Fact]
        public void Key_down_time_accumulates_across_transmits()
        {
            var clock = new Clock();
            var g = Ready(clock);

            for (int i = 0; i < 3; i++)
            {
                Ask(g, "s" + i);
                g.NoteKeyed("s" + i);
                clock.Advance(2);
                g.NoteUnkeyed();
                clock.Advance(5);
            }

            Assert.Equal(6, g.KeyDownSeconds, 3);
            Assert.Equal(3, g.TransmitCount);
        }

        [Fact]
        public void TransmitCount_is_the_run_total_not_the_recent_window()
        {
            // The window is trimmed as it rolls; the count must not be trimmed
            // with it, or the run backstop would never fire.
            var clock = new Clock();
            var g = Ready(clock);

            for (int i = 0; i < 5; i++)
            {
                Ask(g, "s" + i);
                g.NoteKeyed("s" + i);
                g.NoteUnkeyed();
                clock.Advance(BurstWindowSeconds + 1);
            }

            Assert.Equal(5, g.TransmitCount);
        }

        [Fact]
        public void A_run_that_has_spent_its_key_down_budget_stops()
        {
            var clock = new Clock();
            var g = Ready(clock);

            Ask(g, "long");
            g.NoteKeyed("long");
            clock.Advance(RunKeyDownBudgetSeconds + 1);
            g.NoteUnkeyed();

            Assert.Equal(Refusal.BudgetSpent, Ask(g, "next").Why);
        }

        [Fact]
        public void A_grant_that_never_keyed_does_not_spend_the_budget()
        {
            // Grant and key-down are separated by however long the radio takes
            // to confirm a queued write, and a grant the radio never honoured
            // must not be charged for.
            var clock = new Clock();
            var g = Ready(clock);

            Ask(g);
            clock.Advance(60);           // time passes, nothing keyed

            Assert.Equal(0, g.KeyDownSeconds);
            Assert.Equal(0, g.TransmitCount);
        }

        [Fact]
        public void A_new_run_starts_the_budget_over()
        {
            var clock = new Clock();
            var g = Ready(clock);
            Ask(g, "long");
            g.NoteKeyed("long");
            clock.Advance(RunKeyDownBudgetSeconds + 1);
            g.NoteUnkeyed();

            g.BeginRun("TX-NEW1");
            g.DeclareLoad("dummy load", FixerLoadKind.DummyLoad);
            Assert.Equal(0, g.KeyDownSeconds);
            Assert.Equal(0, g.TransmitCount);
            Assert.True(Ask(g, runId: "TX-NEW1").Allowed);
        }

        // ---- the unkey path can never be the thing that throws ----

        [Fact]
        public void Unkeying_twice_is_harmless_and_charged_once()
        {
            var clock = new Clock();
            var g = Ready(clock);
            Ask(g);
            g.NoteKeyed(Stage);
            clock.Advance(3);
            g.NoteUnkeyed();
            clock.Advance(10);
            g.NoteUnkeyed();             // the finally, running after a catch

            Assert.Equal(3, g.KeyDownSeconds, 3);
            Assert.False(g.InFlight);
        }

        [Fact]
        public void Unkeying_something_that_never_keyed_is_harmless()
        {
            // An unkey path that throws is an unkey path a caller is tempted to
            // guard with an if — and the unkey is the one step that must never
            // be skippable.
            var g = Ready();
            g.NoteUnkeyed();
            Assert.Equal(0, g.KeyDownSeconds);
            Assert.False(g.InFlight);
        }

        [Fact]
        public void InFlight_tracks_key_down_and_key_up()
        {
            var g = Ready();
            Assert.False(g.InFlight);
            Ask(g);
            g.NoteKeyed(Stage);
            Assert.True(g.InFlight);
            g.NoteUnkeyed();
            Assert.False(g.InFlight);
        }

        // ---- every refusal has to be worth hearing ----

        [Fact]
        public void Every_refusal_explains_itself()
        {
            // A blind operator who presses a button and hears nothing cannot
            // tell "refused" from "broken" from "still working".
            foreach (Decision d in EveryRefusal())
            {
                Assert.False(d.Allowed);
                Assert.NotEqual(Refusal.None, d.Why);
                Assert.False(string.IsNullOrWhiteSpace(d.Explanation),
                             d.Why + " refused silently");
            }
        }

        [Fact]
        public void No_refusal_says_that_something_was_transmitted()
        {
            // The one thing the words must never leave in doubt.
            foreach (Decision d in EveryRefusal())
                Assert.DoesNotContain("was transmitted successfully", d.Explanation,
                                      StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Refusals_are_written_in_words_an_operator_can_act_on()
        {
            // These are spoken as they stand. Jargon here is a dead end read
            // aloud — the operator cannot act on a run id or a delegate.
            string[] jargon = { "delegate", "runId", "null", "exception", "boolean",
                                "invalid state", "precondition", "callback", "handler" };

            foreach (Decision d in EveryRefusal())
                foreach (string bad in jargon)
                    Assert.DoesNotContain(bad, d.Explanation, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Every_refusal_reason_is_reachable()
        {
            // A reason nothing can produce is a reason that will drift out of
            // step with the code silently. NoRadio and the rest each have a
            // path above; this asserts the set is covered rather than trusting
            // the list.
            var seen = new System.Collections.Generic.HashSet<Refusal>();
            foreach (Decision d in EveryRefusal()) seen.Add(d.Why);

            foreach (Refusal r in (Refusal[])Enum.GetValues(typeof(Refusal)))
            {
                if (r == Refusal.None) continue;
                Assert.Contains(r, seen);
            }
        }

        /// <summary>One decision per refusal reason, each produced by putting a
        /// gate into the state that causes it.</summary>
        private static System.Collections.Generic.IEnumerable<Decision> EveryRefusal()
        {
            yield return Ask(Ready(), transmits: false);                 // StageDoesNotTransmit
            yield return Ask(Ready(), radio: false);                     // NoRadio
            yield return Ask(Ready(), keyed: true);                      // AlreadyInFlight
            yield return Ask(Ready(), runId: "TX-OLD1");                 // WrongRun

            var noRun = new FixerTransmitGate();
            yield return Ask(noRun);                                     // NoRun

            var noLoad = new FixerTransmitGate();
            noLoad.BeginRun(Run);
            yield return Ask(noLoad);                                    // LoadNotDeclared

            var aborted = Ready();
            aborted.AbortRun();
            yield return Ask(aborted);                                   // RunAborted

            var unsure = new FixerTransmitGate();
            unsure.BeginRun(Run);
            unsure.DeclareLoad("Nothing, or I am not sure", FixerLoadKind.NothingOrUnsure);
            yield return Ask(unsure);                                    // LoadForbidsTransmit

            var hot = new FixerTransmitGate();
            hot.BeginRun(Run);
            hot.DeclareLoad("An antenna", FixerLoadKind.Antenna);
            yield return Ask(hot, power: 100);                           // PowerTooHighForLoad

            var once = Ready();
            Ask(once); once.NoteKeyed(Stage); once.NoteUnkeyed();
            yield return Ask(once);                                      // StageAlreadyTransmitted

            var burst = Ready(new Clock());
            for (int i = 0; i < BurstLimit; i++)
            {
                Ask(burst, "b" + i);
                burst.NoteKeyed("b" + i);
                burst.NoteUnkeyed();
            }
            yield return Ask(burst, "b-next");                           // TooFast

            var clock = new Clock();
            var spent = Ready(clock);
            Ask(spent, "long");
            spent.NoteKeyed("long");
            clock.Advance(RunKeyDownBudgetSeconds + 1);
            spent.NoteUnkeyed();
            clock.Advance(BurstWindowSeconds + 1);
            yield return Ask(spent, "after");                            // BudgetSpent
        }
    }
}
