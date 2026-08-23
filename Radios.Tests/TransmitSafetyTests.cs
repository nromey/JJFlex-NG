using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The live warning that fires while the operator is transmitting into an
    /// antenna port with nothing on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The readings are from the bench 8600 on 2026-08-22, converted to watts:
    /// into an EMPTY ANT1 connector, 17.5 W forward and 13.4 W reflected, and
    /// into the dummy load on ANT2 minutes later, 101.2 W forward and 0.054 W
    /// reflected. The radio reported an SWR of 1.008 for the first of those.
    /// </para>
    /// <para>
    /// Every test here is a positive control before it is anything else. The
    /// failure this guards against is not a warning that says the wrong thing;
    /// it is a warning that says nothing, which looks exactly like a station
    /// with nothing wrong.
    /// </para>
    /// </remarks>
    public class TransmitSafetyTests
    {
        // Both bench pairs, in watts.
        private const float OpenForward = 17.5f;
        private const float OpenReflected = 13.4f;
        private const float LoadForward = 101.2f;
        private const float LoadReflected = 0.054f;

        private const int Settled = TransmitSafety.ReflectedWarnSeconds;

        [Fact]
        public void The_empty_antenna_port_warns()
        {
            // THE positive control. If this ever passes by not warning, the
            // whole feature is decorative.
            Assert.True(TransmitSafety.ShouldWarnReflected(
                OpenForward, OpenReflected, Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void The_good_dummy_load_stays_quiet()
        {
            // The negative control, without which the test above proves nothing:
            // a function that returned true unconditionally would also pass it.
            Assert.False(TransmitSafety.ShouldWarnReflected(
                LoadForward, LoadReflected, Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void A_running_tune_cycle_is_silent_even_into_an_open_port()
        {
            // A tuner deliberately transmits into a bad match. Warning on every
            // tune-up trains the operator to ignore the one that matters.
            Assert.False(TransmitSafety.ShouldWarnReflected(
                OpenForward, OpenReflected, Settled, tuning: true, alreadyWarned: false));
        }

        [Fact]
        public void The_first_second_of_transmit_is_given_to_the_meters()
        {
            // Meters have not necessarily caught up with key-down, and a false
            // alarm on every single transmission would be the end of it.
            Assert.False(TransmitSafety.ShouldWarnReflected(
                OpenForward, OpenReflected, 0, tuning: false, alreadyWarned: false));
            Assert.False(TransmitSafety.ShouldWarnReflected(
                OpenForward, OpenReflected, Settled - 1, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void It_speaks_once_per_transmission_not_once_per_second()
        {
            Assert.False(TransmitSafety.ShouldWarnReflected(
                OpenForward, OpenReflected, Settled + 30, tuning: false, alreadyWarned: true));
        }

        [Fact]
        public void Almost_no_forward_power_is_not_a_fault()
        {
            // A meter wandering around zero can produce any ratio at all. The
            // operator dead-keying at a fraction of a watt has not broken
            // anything and must not be told they have.
            Assert.False(TransmitSafety.ShouldWarnReflected(
                0.2f, 0.19f, Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void An_unreadable_meter_is_not_treated_as_a_fault_or_as_health()
        {
            Assert.True(float.IsNaN(TransmitSafety.ReflectedFractionOf(float.NaN, 1f)));
            Assert.True(float.IsNaN(TransmitSafety.ReflectedFractionOf(1f, float.NaN)));
            Assert.False(TransmitSafety.ShouldWarnReflected(
                float.NaN, float.NaN, Settled, tuning: false, alreadyWarned: false));
        }

        [Fact]
        public void The_two_bench_readings_are_nowhere_near_the_threshold()
        {
            // The threshold is only defensible because the measured cases sit in
            // a huge empty gap either side of it. If a future change narrows
            // that gap, 40 percent stops being a measurement and becomes a
            // guess, and this test is where that shows up.
            float open = TransmitSafety.ReflectedFractionOf(OpenForward, OpenReflected);
            float load = TransmitSafety.ReflectedFractionOf(LoadForward, LoadReflected);

            Assert.True(open > 0.70f, "open port measured 76 percent back; got " + open);
            Assert.True(load < 0.01f, "dummy load measured 0.05 percent back; got " + load);
            Assert.True(open > TransmitSafety.ReflectedWarnFraction * 1.5f);
            Assert.True(load < TransmitSafety.ReflectedWarnFraction / 10f);
        }

        [Fact]
        public void The_sentence_names_the_port_when_the_radio_knows_it()
        {
            // "Check the antenna" is advice. "Check ANT1" is an instruction —
            // and the operator cannot read the labels on the back panel.
            string named = TransmitSafety.ReflectedWarningText(0.76f, "ANT1");

            Assert.Contains("ANT1", named);
            Assert.Contains("76", named);
        }

        [Fact]
        public void A_declared_dummy_load_does_not_silence_the_warning()
        {
            // The gate that was here originally, copied from the dead-carrier
            // check where skipping IS correct, would have silenced this warning
            // in the exact scenario it was written for: on 2026-08-22 the load
            // was connected to the port that was not selected. A declared dummy
            // load makes a high reflected reading MORE diagnostic, not less,
            // because the operator has just told us to expect nothing back.
            //
            // ShouldWarnReflected deliberately takes no dummy-load parameter, so
            // there is no knob to get backwards a second time. This test stands
            // guard over the wording instead.
            string s = TransmitSafety.ReflectedWarningText(0.76f, "ANT2", dummyLoadDeclared: true);

            Assert.NotEmpty(s);
            Assert.Contains("76", s);
            Assert.Contains("ANT2", s);
            Assert.Contains("dummy load", s);
        }

        [Fact]
        public void Without_a_declared_load_the_sentence_does_not_mention_one()
        {
            // The negative control for the test above. A sentence that always
            // mentioned a dummy load would pass it while being wrong for every
            // operator on a real antenna.
            string s = TransmitSafety.ReflectedWarningText(0.76f, "ANT2");

            Assert.DoesNotContain("dummy load", s);
        }

        [Fact]
        public void The_sentence_still_works_when_the_antenna_is_unknown()
        {
            // A missing antenna name must not produce "coming back on ." or a
            // dangling placeholder read aloud as "open brace antenna".
            string plain = TransmitSafety.ReflectedWarningText(0.76f, "");

            Assert.Contains("76", plain);
            Assert.DoesNotContain("{", plain);
            Assert.DoesNotContain("  ", plain);
        }
    }
}
