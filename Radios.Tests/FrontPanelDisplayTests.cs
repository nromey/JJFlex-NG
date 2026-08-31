using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The restore capture reports whether a radio can SHOW its front-panel
    /// screensaver. The trap this file exists for: FlexLib's obvious flag,
    /// <c>HasBacklitFrontPanel</c>, is the WRONG one — it is true for radios
    /// with a lit panel and no screen, which is most of what we support.
    /// </summary>
    public class FrontPanelDisplayTests
    {
        [Theory]
        // A lit panel with NO screen. HasBacklitFrontPanel is true for every
        // one of these, which is exactly why the answer must not read it.
        [InlineData("FLEX-6400")]
        [InlineData("FLEX-6600")]
        [InlineData("FLEX-8400")]
        [InlineData("FLEX-8600")]
        // No panel at all.
        [InlineData("FLEX-6300")]
        // Aurora. Present in FlexLib's table in its own right, NOT falling
        // through to DEFAULT - the non-M pair mirrors the 8400/8600 exactly.
        [InlineData("AU-510")]
        [InlineData("AU-520")]
        public void A_lit_panel_is_not_a_screen(string model)
        {
            Assert.False(ProfileReporter.CanShowScreensaver(model),
                model + " has no screen to paint a screensaver on; if this "
                + "flipped, the check is probably reading HasBacklitFrontPanel");
        }

        [Theory]
        [InlineData("FLEX-6500")]   // OLED
        [InlineData("FLEX-6700")]   // OLED
        [InlineData("FLEX-6700R")]  // OLED, no transmitter
        [InlineData("FLEX-6400M")]  // M panel
        [InlineData("FLEX-6600M")]
        [InlineData("FLEX-8400M")]
        [InlineData("FLEX-8600M")]
        [InlineData("AU-510M")]
        [InlineData("AU-520M")]
        public void The_OLED_and_M_models_can_show_one(string model)
        {
            Assert.True(ProfileReporter.CanShowScreensaver(model), model);
        }

        [Fact]
        public void Aurora_is_answered_from_its_OWN_row_not_the_default_fallback()
        {
            // The positive control for the two Aurora cases above. Both
            // AU-510 and AU-520 answer "no screen", which is ALSO what an
            // unrecognised model answers via FlexLib's DEFAULT row — so the
            // False on its own proves nothing about whether FlexLib knows
            // what an Aurora is.
            //
            // The M variants are the discriminator: DEFAULT has IsMModel
            // false, so a fallback could never return true for them. If
            // AU-510M answers "yes", the table has real Aurora rows.
            //
            // Worth a test because a survey of this table on 2026-08-31
            // reported Aurora ABSENT — the survey's own pattern required
            // "new()" and these four rows are written "new ()". The data was
            // right and the instrument was wrong.
            Assert.True(ProfileReporter.CanShowScreensaver("AU-510M"));
            Assert.True(ProfileReporter.CanShowScreensaver("AU-520M"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("FLEX-9999-NOT-A-RADIO")]
        public void An_unknown_model_does_not_claim_a_screen(string model)
        {
            // FlexLib falls back to its DEFAULT row, which has neither flag.
            // Claiming a screen we cannot verify would put a setting in the
            // capture that the operator can never act on.
            Assert.False(ProfileReporter.CanShowScreensaver(model));
        }
    }
}
