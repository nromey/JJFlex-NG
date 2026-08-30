using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 36 Track J, task #93: the connect cluster's prose, pinned as
    /// ASSEMBLED SENTENCES rather than as the fragments that build them.
    ///
    /// <para><b>Why assembled.</b> Every line here is stitched together from a
    /// template and its arguments, and product copy in this codebase defaults to
    /// fragments that read fine in a diff and land badly in the ear. A template
    /// reading <c>"Connecting to {radioName} {via}"</c> looks unobjectionable
    /// until you notice the finished sentence has no full stop, so a screen
    /// reader runs it into whatever comes next. Nothing catches that except
    /// reading the output.</para>
    ///
    /// <para><b>Why pinned.</b> These are the words an operator lives with
    /// during the most anxious ten seconds the application has, and they are
    /// reviewed by a person. A change to any of them should be a decision
    /// somebody made, which means it should have to come here and say so.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ConnectSentenceTests
    {
        // ------------------------------------------------------------------
        // Handing the connect on — an intention, in intention-shaped words
        // ------------------------------------------------------------------

        /// <summary>
        /// Spoken before the dialog closes and before a byte has travelled, so
        /// it is a PREDICTION. It says "Trying" for that reason: if the walk
        /// falls back, nothing said here has been falsified.
        /// </summary>
        [Theory]
        [InlineData("the local network", "Connecting to K5NER. Trying the local network.")]
        [InlineData("SmartLink", "Connecting to K5NER. Trying SmartLink.")]
        [InlineData("SmartLink as noel@example.com",
                    "Connecting to K5NER. Trying SmartLink as noel@example.com.")]
        public void The_handover_line_states_an_intention(string legName, string expected)
        {
            Assert.Equal(expected, Lexicon.Get("connect.selector.connecting",
                ("radioName", "K5NER"), ("legName", legName)));
        }

        /// <summary>The leg names, which the whole walk now shares.</summary>
        [Fact]
        public void The_leg_names_read_the_same_everywhere_they_appear()
        {
            Assert.Equal("the local network", Lexicon.Get("connect.walk.leg_local"));
            Assert.Equal("SmartLink", Lexicon.Get("connect.walk.leg_smartlink"));
            Assert.Equal("SmartLink as noel@example.com",
                Lexicon.Get("connect.walk.leg_smartlink_as", ("acctEmail", "noel@example.com")));
        }

        // ------------------------------------------------------------------
        // The connecting window
        // ------------------------------------------------------------------

        /// <summary>
        /// The window carries the handover sentence whole. It is already
        /// finished punctuation, so nothing is appended to it — the plain form
        /// keeps its ellipsis, the carried one does not gain a second terminator.
        /// </summary>
        [Fact]
        public void The_connecting_window_carries_the_handover_sentence_whole()
        {
            var lead = Lexicon.Get("connect.selector.connecting",
                ("radioName", "K5NER"), ("legName", "SmartLink as noel@example.com"));

            Assert.Equal("Connecting to K5NER. Trying SmartLink as noel@example.com.",
                Lexicon.Get("connect.connecting.initial_lead", ("lead", lead)));
        }

        [Fact]
        public void The_connecting_window_without_a_lead_says_the_plain_thing()
        {
            Assert.Equal("Connecting to FLEX-8600...",
                Lexicon.Get("connect.connecting.initial", ("radioName", "FLEX-8600")));
        }

        /// <summary>The phase ladder, as a listener hears it in order.</summary>
        [Fact]
        public void The_phase_lines_read_as_a_progression()
        {
            Assert.Equal("Connected to FLEX-8600. Waiting for slice...",
                Lexicon.Get("connect.connecting.phase_slice_wait", ("radioName", "FLEX-8600")));
            Assert.Equal("Slice acquired. Setting up...",
                Lexicon.Get("connect.connecting.phase_setup"));
        }

        /// <summary>
        /// The heartbeats. Terse is bare; Chatty adds the object and nothing
        /// else — the same shape discovery's "Still looking." / "Still looking
        /// for radios." already established, so the two surfaces sound like one
        /// application.
        /// </summary>
        [Fact]
        public void The_heartbeats_name_the_stage_they_are_covering()
        {
            Assert.Equal("Still connecting.",
                Lexicon.Get("connect.connecting.still_connecting_terse"));
            Assert.Equal("Still connecting to FLEX-8600.",
                Lexicon.Get("connect.connecting.still_connecting_chatty", ("radioName", "FLEX-8600")));

            Assert.Equal("Still waiting.",
                Lexicon.Get("connect.connecting.still_waiting_terse"));
            Assert.Equal("Still waiting for FLEX-8600 to answer.",
                Lexicon.Get("connect.connecting.still_waiting_chatty", ("radioName", "FLEX-8600")));

            Assert.Equal("Still setting up.",
                Lexicon.Get("connect.connecting.still_setting_up_terse"));
            Assert.Equal("Still setting up FLEX-8600.",
                Lexicon.Get("connect.connecting.still_setting_up_chatty", ("radioName", "FLEX-8600")));
        }

        // ------------------------------------------------------------------
        // A SmartLink account pass is not a connect (task #294)
        // ------------------------------------------------------------------

        /// <summary>
        /// The picker's SmartLink passes get their own words, about the ACCOUNT.
        /// </summary>
        /// <remarks>
        /// <para>They ran in SILENCE until this task. The window they borrowed
        /// worked out its own subject by scraping "Connecting to SmartLink..."
        /// for a "Connecting to " prefix, so it believed it was connecting to a
        /// radio named "SmartLink" — and the #212 heartbeat therefore had to be
        /// gated off there rather than announce "Still connecting to radio."
        /// about an account refresh.</para>
        /// <para>So the operator pressed something, an account refresh ran for
        /// seconds, and nothing said it was happening. A blind operator has no
        /// spinner: that is indistinguishable from a keypress that did
        /// nothing.</para>
        /// </remarks>
        [Fact]
        public void An_account_pass_names_the_account_and_not_a_radio()
        {
            Assert.Equal("Refreshing the radio list for noel@example.com.",
                Lexicon.Get("connect.selector.refreshing_for_account", ("email", "noel@example.com")));
            Assert.Equal("Connecting to SmartLink as noel@example.com.",
                Lexicon.Get("connect.selector.connecting_as_account", ("email", "noel@example.com")));
        }

        /// <summary>
        /// The heartbeat that covers an account pass, in the same shape as every
        /// other one: Terse bare, Chatty adding the object — which here is the
        /// account, because that is what is being waited on.
        /// </summary>
        [Fact]
        public void The_account_pass_heartbeats_say_the_operation_then_the_account()
        {
            Assert.Equal("Still refreshing.",
                Lexicon.Get("connect.selector.still_refreshing_terse"));
            Assert.Equal("Still refreshing the radio list for noel@example.com.",
                Lexicon.Get("connect.selector.still_refreshing_chatty", ("email", "noel@example.com")));

            Assert.Equal("Still reaching SmartLink.",
                Lexicon.Get("connect.selector.still_reaching_smartlink_terse"));
            Assert.Equal("Still reaching SmartLink as noel@example.com.",
                Lexicon.Get("connect.selector.still_reaching_smartlink_chatty", ("email", "noel@example.com")));
        }

        /// <summary>
        /// None of them may say "radio" in the singular subject position — the
        /// exact sentence the borrowed window would have produced was "Still
        /// connecting to radio.", and that word appearing here would mean the
        /// borrowing had crept back in under new keys.
        /// </summary>
        [Fact]
        public void No_account_pass_line_claims_to_be_connecting_to_a_radio()
        {
            string[] lines =
            {
                Lexicon.Get("connect.selector.still_refreshing_terse"),
                Lexicon.Get("connect.selector.still_refreshing_chatty", ("email", "noel@example.com")),
                Lexicon.Get("connect.selector.still_reaching_smartlink_terse"),
                Lexicon.Get("connect.selector.still_reaching_smartlink_chatty", ("email", "noel@example.com")),
            };

            foreach (var line in lines)
            {
                Assert.DoesNotContain("to radio", line, System.StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith(".", line, System.StringComparison.Ordinal);
            }
        }

        // ------------------------------------------------------------------
        // Failing, and stopping, are different things
        // ------------------------------------------------------------------

        /// <summary>
        /// Said while the connecting window still holds focus, so it lands even
        /// if the verdict behind it does not.
        /// </summary>
        [Fact]
        public void The_two_ways_setup_dies_each_name_themselves()
        {
            Assert.Equal("The radio did not finish setting up.",
                Lexicon.Get("connect.connecting.setup_never_finished"));
            Assert.Equal("The connection dropped during setup.",
                Lexicon.Get("connect.connecting.dropped_during_setup"));
        }

        /// <summary>
        /// A leg that could not be reached at all, versus a leg that was reached
        /// and then would not finish. Track E's walk-resume needed the second
        /// one and had only the first, which would have sent an operator
        /// debugging the network when the network had worked.
        /// </summary>
        [Fact]
        public void A_leg_that_connected_and_then_failed_does_not_claim_it_never_connected()
        {
            Assert.Equal("Could not connect over SmartLink. Trying the local network.",
                Lexicon.Get("connect.walk.falling_back",
                    ("legName", "SmartLink"), ("nextName", "the local network")));

            Assert.Equal(
                "Connected over SmartLink, but the radio did not finish setting up. "
                + "Trying the local network.",
                Lexicon.Get("connect.walk.opened_failed",
                    ("legName", "SmartLink"), ("nextName", "the local network")));
        }

        /// <summary>
        /// Stopping sounds like stopping. The cancel line is what an operator
        /// who pressed Escape hears, and nothing follows it claiming a failure.
        /// </summary>
        [Fact]
        public void A_cancel_sounds_like_stopping()
        {
            Assert.Equal("Connection attempt cancelled",
                Lexicon.Get("connect.connecting.cancelled"));
            Assert.Equal("Cancelling...", Lexicon.Get("connect.connecting.cancelling"));
        }

        /// <summary>
        /// The verdict, held back until the shell window is in front again so it
        /// is not issued into the transition that destroys it.
        /// </summary>
        [Fact]
        public void The_verdict_carries_its_evidence_when_there_is_any()
        {
            Assert.Equal("Connection failed", Lexicon.Get("connect.walk.failed"));
            Assert.Equal(
                "Connection failed. FLEX-8600 was not found on the local network.",
                Lexicon.Get("connect.walk.failed_with_advice",
                    ("advice", "FLEX-8600 was not found on the local network.")));
        }

        // ------------------------------------------------------------------
        // The roster row's three account states (#340, #382)
        //
        // Assembled through connect.row.display, because that is the string a
        // screen reader actually reads. The fragment "not signed in to
        // dbreda@mail.com" is unobjectionable on its own; whether the finished
        // row reads as a sentence is a different question, and it is the only
        // one that matters. Sprint 38 shipped "last seen remote via SmartLink"
        // out of two individually-correct fragments.
        // ------------------------------------------------------------------

        private static string Row(string whereText) =>
            Lexicon.Get("connect.row.display",
                ("fav", ""), ("autoConn", ""), ("lbw", ""),
                ("namePart", "6300inshack"), ("modelPart", "FLEX-6300"),
                ("whereText", whereText),
                // Empty on purpose: these tests pin the row's account states,
                // which are all NOT-live states, and a row that cannot hear
                // the radio makes no claim about who is on it — the occupancy
                // token must vanish without a trace. (Live rows always carry
                // the clause now, zero included; RadioOccupancyTests owns that
                // wording.)
                ("occupancy", ""));

        /// <summary>
        /// The state #382 added, and the reason it had to exist: opening the
        /// picker now dials the accounts the roster depends on, so for a second
        /// or two the row is neither signed in nor unasked. Every clause is
        /// about US — we are checking, we are signing in — which is the rule the
        /// whole row family follows.
        /// </summary>
        [Fact]
        public void A_row_whose_account_is_being_dialled_says_so_and_names_it()
        {
            Assert.Equal("checking, signing in to dbreda@mail.com",
                Lexicon.Get("connect.row.account_signing_in",
                    ("account", "dbreda@mail.com")));

            Assert.Equal(
                "6300inshack, FLEX-6300, checking, signing in to dbreda@mail.com",
                Row(Lexicon.Get("connect.row.account_signing_in",
                    ("account", "dbreda@mail.com"))));
        }

        /// <summary>
        /// The settled sentence stays exactly as #340 shipped it. This is NOT a
        /// state the fix removes: an account whose sign-in was cleared genuinely
        /// is not signed in, and that is the one row here the operator can act
        /// on — it also tells them what Enter is about to do.
        /// </summary>
        [Fact]
        public void A_row_whose_account_has_no_sign_in_still_says_that_and_keeps_its_age()
        {
            var age = Lexicon.Get("connect.row.age_suffix",
                ("lastSeenText", "last seen 3 days ago"));

            Assert.Equal(
                "6300inshack, FLEX-6300, not checked, not signed in to dbreda@mail.com, "
                + "last seen 3 days ago",
                Row(Lexicon.Get("connect.row.account_not_signed_in",
                    ("account", "dbreda@mail.com"), ("age", age))));
        }

        /// <summary>
        /// The transitional sentence carries no age, deliberately. It is about to
        /// be replaced, and a "last seen 3 days ago" hung off the end is a clause
        /// the operator listens past twice — once here and once on the verdict.
        /// Pinned so a future author restoring symmetry with the settled string
        /// has to decide to.
        /// </summary>
        [Fact]
        public void The_transitional_sentence_does_not_carry_a_last_seen_age()
        {
            Assert.DoesNotContain("last seen",
                Lexicon.Get("connect.row.account_signing_in", ("account", "dbreda@mail.com")),
                System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// F2 answers "which halves have loaded?" on demand, and #382 gave it a
        /// fourth answer. Without it F2 said "Remote not loaded." during the
        /// very seconds the app was signing in to load it — a row reading is a
        /// one-shot, and F2 is where an operator goes when they missed it.
        /// </summary>
        [Fact]
        public void F2_can_say_that_remote_is_signing_in_rather_than_absent()
        {
            Assert.Equal("Remote signing in.",
                Lexicon.Get("connect.selector.f2_remote_signing_in"));

            Assert.Equal("Local loaded, still listening. Remote signing in. 1 radio online.",
                Lexicon.Get("connect.selector.f2_summary",
                    ("local", Lexicon.Get("connect.selector.f2_local_loaded")),
                    ("remote", Lexicon.Get("connect.selector.f2_remote_signing_in")),
                    ("count", Lexicon.Get("connect.selector.f2_count_one", ("live", 1)))));
        }
    }
}
