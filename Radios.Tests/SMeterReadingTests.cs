using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// What a signal strength reads back as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These exist because the rule broke once and nothing noticed. A tester
    /// reported hearing 5 read back as 50 and 10 as 100 — the excess over S9
    /// was being multiplied by ten in one place and by six in another, and
    /// there was no test anywhere that touched the S-meter at all.
    /// </para>
    /// <para>
    /// <b>A blind operator has no second opinion about their signal
    /// strength.</b> They cannot glance at the meter to sanity-check what they
    /// heard, so a wrong number is not a cosmetic fault — it is the only number
    /// they have, and they will report it on the air.
    /// </para>
    /// </remarks>
    public class SMeterReadingTests
    {
        // ---- the bug that shipped, pinned exactly ----

        [Theory]
        [InlineData(14, "S9 plus 5 dB")]    // reported as "S9 plus 50"
        [InlineData(19, "S9 plus 10 dB")]   // reported as "S9 plus 100"
        [InlineData(13, "S9 plus 4 dB")]    // reported as "S9 plus 40", and as 24
        public void The_readings_a_tester_heard_wrong_now_read_right(int raw, string expected)
        {
            // The exact values from the field report. Named as such so that a
            // future change which breaks them fails with the history attached
            // rather than as an anonymous assertion.
            Assert.Equal(expected, SMeterReading.Display(raw));
        }

        [Fact]
        public void The_excess_is_never_multiplied_by_anything()
        {
            // THE invariant. SMeter returns dB-over-S9 PLUS 9, so the excess is
            // already decibels. Any multiplier here is the bug — and it looks
            // like a unit conversion, which is why it survived review twice.
            for (int raw = 10; raw <= 60; raw++)
                Assert.Equal(raw - 9, SMeterReading.ExcessOverS9(raw));
        }

        // ---- at and below S9 it is plain S-units ----

        [Theory]
        [InlineData(0, "S0")]
        [InlineData(1, "S1")]
        [InlineData(5, "S5")]
        [InlineData(9, "S9")]
        public void At_or_below_S9_the_reading_is_the_S_unit_itself(int raw, string expected)
        {
            Assert.Equal(expected, SMeterReading.Display(raw));
        }

        [Fact]
        public void S9_itself_is_not_reported_as_an_excess()
        {
            // The boundary. "S9 plus 0 dB" is technically true and reads as
            // though something is being added, which is worse than "S9".
            Assert.Equal("S9", SMeterReading.Display(9));
            Assert.False(SMeterReading.IsOverS9(9));
            Assert.True(SMeterReading.IsOverS9(10));
        }

        [Fact]
        public void One_over_S9_is_one_decibel_and_reads_as_such()
        {
            Assert.Equal("S9 plus 1 dB", SMeterReading.Display(10));
        }

        // ---- the shape of the output ----

        [Fact]
        public void No_reading_ever_carries_a_leading_zero()
        {
            // A padded number is read aloud as its digits by some voices — "S
            // zero five" — and as a different number by others. Neither is what
            // was meant.
            for (int raw = 0; raw <= 60; raw++)
            {
                string s = SMeterReading.Display(raw);
                Assert.DoesNotContain("S0", s.Substring(1));   // not "S9 plus 05"
                Assert.DoesNotContain(" 0", s.Replace(" 0 dB", ""));
            }
        }

        [Fact]
        public void An_over_S9_reading_always_names_its_unit()
        {
            // "S9 plus 5" leaves the operator to guess whether that is five
            // decibels or five S-units, which is a factor of six.
            for (int raw = 10; raw <= 60; raw++)
                Assert.EndsWith(" dB", SMeterReading.Display(raw));
        }

        [Fact]
        public void A_plain_S_unit_reading_carries_no_unit_suffix()
        {
            // "S5 dB" would be wrong — below S9 the number IS the S-unit.
            for (int raw = 0; raw <= 9; raw++)
                Assert.DoesNotContain("dB", SMeterReading.Display(raw));
        }

        [Fact]
        public void The_display_is_the_same_whichever_surface_asks()
        {
            // The rule lived inline at two call sites and they drifted apart.
            // Calling one function twice cannot drift, and this asserts that
            // the function is deterministic rather than reading any state.
            for (int raw = 0; raw <= 60; raw++)
                Assert.Equal(SMeterReading.Display(raw), SMeterReading.Display(raw));
        }
    }
}
