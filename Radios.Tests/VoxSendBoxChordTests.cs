using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The VOX send box's JJ key chord is Ctrl+K (#835; Noel, 2026-10-27 20:49:
    /// <i>"ctrl+k is fine"</i>), and it reaches the one toggle every other door
    /// uses.
    /// </summary>
    /// <remarks>
    /// Read from source, in the family of <see cref="JjKeyGrammarTests"/> and
    /// <see cref="LeaderLayerConsistencyTests"/>, because Radios.Tests cannot
    /// load the WPF assembly. Those two already hold that every advertised
    /// chord is handled and every handled chord advertised, and
    /// <c>LeaderDocCoverageTests</c> holds both help pages to the inventory; this
    /// holds the MEANING, which none of them can: a chord that is bound,
    /// documented and handled can still run the wrong command.
    /// </remarks>
    public sealed class VoxSendBoxChordTests
    {
        [Fact]
        public void CtrlKOnTheJjKeyRunsTheVoxSendBoxToggleAndNothingElse()
        {
            var arm = LeaderSourceScan.RealSwitchArms().Single(a => a.Labels.Contains(Keys.Control | Keys.K));
            var calls = Regex.Matches(arm.Body, @"\b(\w+Handler)\s*\(").Select(m => m.Groups[1].Value).ToArray();
            Assert.Equal(new[] { "ToggleVoxSendBoxHandler" }, calls);
        }

        [Fact]
        public void TheHandlerGoesThroughTheOneDoorTheOtherDoorsUse()
        {
            string kc = LeaderSourceScan.ReadSource("JJFlexWpf/KeyCommands.cs");
            Assert.Contains("private void ToggleVoxSendBoxHandler() => VoxSendBoxControl.Toggle();", kc);
        }

        [Fact]
        public void TheCommandsUnboundNoteNamesTheChordRatherThanSayingNoneYet()
        {
            string kc = LeaderSourceScan.ReadSource("JJFlexWpf/KeyCommands.cs");
            int at = kc.IndexOf("[CommandValues.ToggleVoxSendBox] = new(", System.StringComparison.Ordinal);
            Assert.True(at > 0, "the box's unbound note has moved; re-anchor this test");
            string note = kc.Substring(at, kc.IndexOf("),", at, System.StringComparison.Ordinal) - at);
            Assert.Contains("UnboundReason.LeaderLayer", note);
            Assert.Contains("Ctrl+J, Ctrl+K", note);
            Assert.DoesNotContain("Noel's to allocate", note);
        }

        [Fact]
        public void TheInventoryAdvertisesTheChordOnTheCtrlTier()
        {
            var advertised = LeaderSourceScan.RealAdvertised(out _);
            Assert.Contains(Keys.Control | Keys.K, advertised);
        }
    }
}
