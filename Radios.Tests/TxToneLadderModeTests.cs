using System;
using Radios.ChainChecks;
using Xunit;
using static Radios.ChainChecks.TxToneLadder;

namespace Radios.Tests
{
    /// <summary>
    /// What the ladder does about the mode the radio happens to be in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ruled by Noel 2026-08-25: "switch to an appropriate mode if the rig is
    /// in FM or something weird, then switch back after test." So the ladder
    /// drives the radio rather than declining — with CW as the one exception,
    /// and that exception is the interesting case.
    /// </para>
    /// <para>
    /// The property that matters most here is not covered by any single test
    /// below and so is stated as its own: <b>a plan that switches must always
    /// name what to switch to AND what to put back.</b> A switch with no
    /// recorded original is how an operator ends up in a mode they did not
    /// choose, on a radio they are about to transmit with.
    /// </para>
    /// </remarks>
    public class TxToneLadderModeTests
    {
        private const ulong TwentyMetres = 14_200_000UL;
        private const ulong FortyMetres = 7_150_000UL;

        // ---- the modes the ladder can measure as they stand ----

        [Theory]
        [InlineData("USB")]
        [InlineData("LSB")]
        [InlineData("usb")]
        [InlineData(" LSB ")]
        public void A_voice_sideband_runs_as_it_is(string mode)
        {
            ModePlan p = PlanForMode(mode, TwentyMetres);
            Assert.Equal(ModeAction.RunAsIs, p.Action);
            Assert.Equal(mode, p.CurrentMode);   // reported verbatim, not normalised
        }

        // ---- CW is refused, not switched, and the distinction is the point ----

        [Fact]
        public void CW_is_refused_rather_than_switched_out_of()
        {
            // Switching out of CW to run a tone ladder would measure a transmit
            // audio path the operator never uses, and report it as theirs. The
            // refusal is the honest answer, not a limitation.
            ModePlan p = PlanForMode("CW", TwentyMetres);
            Assert.Equal(ModeAction.Refuse, p.Action);
        }

        [Fact]
        public void The_CW_refusal_points_at_the_test_that_does_work_there()
        {
            // A refusal that leaves the operator with nowhere to go is a dead
            // end. The TUNE probe (#222) works in CW natively.
            string why = PlanForMode("CW", TwentyMetres).Reason;
            Assert.Contains("transmitter check", why, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CW", why);
        }

        // ---- everything else switches ----

        [Theory]
        [InlineData("FM")]
        [InlineData("NFM")]
        [InlineData("AM")]
        [InlineData("SAM")]
        [InlineData("DIGU")]
        [InlineData("DIGL")]
        [InlineData("RTTY")]
        public void Modes_without_a_voice_transmit_filter_are_switched(string mode)
        {
            ModePlan p = PlanForMode(mode, TwentyMetres);
            Assert.Equal(ModeAction.SwitchAndRestore, p.Action);
        }

        [Fact]
        public void A_switch_names_both_where_it_is_going_and_what_to_put_back()
        {
            // THE invariant. A switch with no recorded original is how an
            // operator is left in a mode they did not choose.
            foreach (string mode in new[] { "FM", "AM", "DIGU", "RTTY", "SAM" })
            {
                ModePlan p = PlanForMode(mode, TwentyMetres);
                Assert.Equal(ModeAction.SwitchAndRestore, p.Action);
                Assert.False(string.IsNullOrWhiteSpace(p.SwitchTo), mode + ": no target");
                Assert.Equal(mode, p.CurrentMode);
                Assert.NotEqual(p.CurrentMode, p.SwitchTo);
            }
        }

        [Fact]
        public void A_switch_explains_itself_in_terms_the_operator_can_check()
        {
            // Announced before it happens, so this text is heard rather than
            // logged. It must name both modes.
            ModePlan p = PlanForMode("FM", TwentyMetres);
            Assert.Contains("FM", p.Reason);
            Assert.Contains(p.SwitchTo, p.Reason);
        }

        // ---- which sideband ----

        [Fact]
        public void Above_ten_megahertz_it_switches_to_upper_sideband()
        {
            Assert.Equal("USB", PlanForMode("FM", TwentyMetres).SwitchTo);
        }

        [Fact]
        public void Below_ten_megahertz_it_switches_to_lower_sideband()
        {
            // Only matters transmitting into a real antenna, but it costs
            // nothing to be right, and a test on the wrong sideband for the
            // band is a small avoidable rudeness.
            Assert.Equal("LSB", PlanForMode("FM", FortyMetres).SwitchTo);
        }

        [Theory]
        [InlineData(1_800_000UL, "LSB")]
        [InlineData(3_750_000UL, "LSB")]
        [InlineData(7_150_000UL, "LSB")]
        [InlineData(9_999_999UL, "LSB")]
        [InlineData(10_000_000UL, "USB")]
        [InlineData(14_200_000UL, "USB")]
        [InlineData(21_300_000UL, "USB")]
        [InlineData(50_125_000UL, "USB")]
        public void The_sideband_convention_holds_across_the_bands(ulong hz, string expected)
        {
            Assert.Equal(expected, ConventionalSideband(hz));
        }

        // ---- unknown ----

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void An_unreported_mode_refuses_rather_than_guessing(string mode)
        {
            // Guessing here would mean switching a radio into a mode on the
            // strength of not knowing what it was in. Absence of a reading is
            // not a reading.
            ModePlan p = PlanForMode(mode, TwentyMetres);
            Assert.Equal(ModeAction.Refuse, p.Action);
            Assert.False(string.IsNullOrWhiteSpace(p.Reason));
        }

        // ---- the ordering trap, stated as a test so it cannot be forgotten ----

        [Fact]
        public void The_plan_carries_no_filter_information_at_all()
        {
            // Deliberate, and worth asserting. TX filter cuts are PER MODE, so
            // a plan that carried cuts would be carrying the cuts of the mode
            // being left. The caller must read TXFilterLow/High AFTER acting on
            // the plan. Making the plan structurally incapable of holding them
            // is how that ordering is enforced rather than merely documented.
            ModePlan p = PlanForMode("FM", TwentyMetres);
            System.Reflection.FieldInfo[] fields = typeof(ModePlan).GetFields();
            foreach (System.Reflection.FieldInfo f in fields)
                Assert.DoesNotContain("filter", f.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ModeAction.SwitchAndRestore, p.Action);
        }

        [Fact]
        public void Every_refusal_and_every_switch_gives_a_reason()
        {
            // A plan the operator cannot act on is a dead end whichever way it
            // went.
            foreach (string mode in new[] { "CW", "", "FM", "AM", "DIGU", "RTTY" })
            {
                ModePlan p = PlanForMode(mode, TwentyMetres);
                if (p.Action != ModeAction.RunAsIs)
                    Assert.False(string.IsNullOrWhiteSpace(p.Reason), mode + ": no reason");
            }
        }
    }
}
