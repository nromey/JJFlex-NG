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
        public void The_OLED_and_M_models_can_show_one(string model)
        {
            Assert.True(ProfileReporter.CanShowScreensaver(model), model);
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
