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
    }
}
