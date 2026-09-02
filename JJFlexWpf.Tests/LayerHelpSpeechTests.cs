using System.Linq;
using Xunit;

namespace JJFlexWpf.Tests
{
    /// <summary>
    /// The spoken H list of each value layer, generated from the layer's
    /// own inventory table (Sprint 44 Track I, #514, #516, #519). Pure
    /// static data — no window, no dispatcher, no desktop — so it runs
    /// green under DeskGuard on the interactive desktop, in the
    /// LeaderNearMissTests tradition.
    /// </summary>
    /// <remarks>
    /// Radios.Tests exercises the engine with a harness that stubs the help
    /// sentence; THIS is where the real sentence is pinned, because the
    /// inventory lives in JJFlexWpf. The count comes first (#519) so an
    /// operator knows at once how long the list is and never waits through
    /// a recitation wondering when it ends; the keys are spoken forms, so
    /// "?" arrives as "Shift slash" (#303).
    /// </remarks>
    public class LayerHelpSpeechTests
    {
        public LayerHelpSpeechTests()
        {
            Radios.Lexicon.Load(Radios.Lexicon.Partitions);
        }

        [Fact]
        public void The_audio_layer_list_leads_with_its_headline_and_count()
        {
            string s = KeyInventory.LayerHelpSpeech(KeyInventory.AudioLayerContext, "Audio layer, no target picked");
            Assert.StartsWith("Audio layer, no target picked. 13 keys: Ctrl+H, On-radio headphone volume", s);
            Assert.Contains("; Ctrl+P, Pan for the slice you're on", s);
            Assert.Contains("; Shift slash, Explore this layer's keys", s);
            Assert.DoesNotContain("; ?, ", s);
            Assert.EndsWith(".", s);
        }

        [Fact]
        public void The_filter_layer_list_names_both_shifts_and_both_sides()
        {
            string s = KeyInventory.LayerHelpSpeech(KeyInventory.FilterLayerContext, "Filter layer, RX filter 100 to 2800, 2.7 kilohertz");
            Assert.StartsWith("Filter layer, RX filter 100 to 2800, 2.7 kilohertz. 15 keys: Left Shift + Left / Right, Walk the low edge", s);
            Assert.Contains("Right Shift + Left / Right, Walk the high edge", s);
            Assert.Contains("T, Work on the transmit filter", s);
            Assert.Contains("R, Work on the receive filter", s);
        }

        [Fact]
        public void Every_row_of_both_layers_is_describable()
        {
            // Track K's explorer reads these rows; a row with no readable key
            // or description is a key that works and is invisible.
            foreach (string context in new[] { KeyInventory.AudioLayerContext, KeyInventory.FilterLayerContext })
            {
                var rows = KeyInventory.LayerCommands(context);
                Assert.NotEmpty(rows);
                Assert.All(rows, r =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(r.SpokenKey), context + " row with no spoken key");
                    Assert.False(string.IsNullOrWhiteSpace(r.Description), context + " row with no description");
                    Assert.False(string.IsNullOrWhiteSpace(r.ContextLabel), context + " row with no context label");
                    Assert.NotEmpty(r.Keywords);
                });
            }
        }

        [Fact]
        public void The_layers_appear_in_the_command_finder_rows()
        {
            var items = KeyInventory.CommandFinderItems();
            Assert.Contains(items, i => i.KeyDisplay == "Ctrl+H" && i.Description.Contains("(on Audio layer)"));
            Assert.Contains(items, i => i.KeyDisplay == "Left Shift + Left / Right" && i.Description.Contains("(on Filter layer)"));
            Assert.DoesNotContain(items, i => i.Description.Contains("Volume mode") || i.Description.Contains("Pan mode"));
        }
    }
}
