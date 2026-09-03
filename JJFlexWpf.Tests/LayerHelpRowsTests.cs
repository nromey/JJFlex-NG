using System.Linq;
using Xunit;

namespace JJFlexWpf.Tests
{
    /// <summary>
    /// The audio and filter layers as Track K's H surface sees them (Sprint
    /// 44 Track N, #524). Pure static data — no window, no dispatcher, no
    /// desktop — so it runs green under DeskGuard on the interactive
    /// desktop, in the LeaderNearMissTests tradition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This file replaces LayerHelpSpeechTests, which pinned
    /// <c>KeyInventory.LayerHelpSpeech</c> — a second builder of "this
    /// layer's keys, spoken" that Track I wrote beside the one Track K had
    /// already published. Both worked, they merged with zero conflict, and
    /// the operator got the worse one: 1,100 characters in one breath. The
    /// duplicate is deleted and the layers call <see cref="KeyLayerHelp"/>.
    /// </para>
    /// <para>
    /// The load-bearing fact is the COUNT: both layers are longer than
    /// <see cref="KeyLayerHelp.SpokenLimit"/>, so H opens the navigable list
    /// rather than reciting. A future edit that trims a layer below the
    /// limit changes H from a list to a sentence, and that is a bench
    /// decision, not a side effect — this test makes it a red build.
    /// </para>
    /// </remarks>
    public class LayerHelpRowsTests
    {
        public LayerHelpRowsTests()
        {
            Radios.Lexicon.Load(Radios.Lexicon.Partitions);
        }

        [Fact]
        public void The_audio_layer_is_long_so_H_opens_the_list()
        {
            var rows = KeyLayerHelp.Rows(KeyInventory.AudioLayerContext);
            Assert.False(KeyLayerHelp.IsShort(rows.Count),
                $"the audio layer has {rows.Count} rows; H must open the list, not recite (#524)");
            Assert.Equal("Audio layer, " + rows.Count + " keys",
                KeyLayerHelp.ListTitle(KeyInventory.AudioLayerContext, rows.Count));
        }

        [Fact]
        public void The_filter_layer_is_long_so_H_opens_the_list()
        {
            var rows = KeyLayerHelp.Rows(KeyInventory.FilterLayerContext);
            Assert.False(KeyLayerHelp.IsShort(rows.Count));
            Assert.Equal("Filter layer, " + rows.Count + " keys",
                KeyLayerHelp.ListTitle(KeyInventory.FilterLayerContext, rows.Count));
        }

        [Fact]
        public void The_audio_layer_lists_the_four_targets_Noel_found_missing()
        {
            // #524: slice volume, mute, PC audio on/off, binaural. Letters
            // PROVISIONAL until ruled; the row set is what this pins.
            var rows = KeyLayerHelp.Rows(KeyInventory.AudioLayerContext);
            Assert.Contains(rows, r => r.key == "V" && r.description.StartsWith("Slice volume", System.StringComparison.Ordinal));
            Assert.Contains(rows, r => r.key == "Ctrl+M" && r.description.StartsWith("Mute or unmute", System.StringComparison.Ordinal));
            Assert.Contains(rows, r => r.key == "Ctrl+P" && r.description.StartsWith("Turn PC audio on or off", System.StringComparison.Ordinal));
            Assert.Contains(rows, r => r.key == "Ctrl+B" && r.description.StartsWith("Binaural receive on or off", System.StringComparison.Ordinal));
        }

        [Fact]
        public void The_spoken_fallback_leads_with_the_count_and_names_keystrokes()
        {
            // The one spoken form left — reached only by a host with no
            // surface (#200). Count first (#519); "?" is "Shift slash" (#303).
            var rows = KeyLayerHelp.Rows(KeyInventory.AudioLayerContext);
            string s = KeyLayerHelp.SpokenList(KeyInventory.AudioLayerContext);
            Assert.StartsWith(rows.Count + " keys in Audio layer: V, Slice volume", s);
            Assert.Contains("; P, Pan for the slice you're on", s);
            Assert.Contains("; Shift slash, Open the JJ key explorer", s);
            Assert.DoesNotContain("; ?, ", s);
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
            Assert.Contains(items, i => i.KeyDisplay == "V" && i.Description.Contains("(on Audio layer)"));
            Assert.Contains(items, i => i.KeyDisplay == "Ctrl+M" && i.Description.Contains("(on Audio layer)"));
            Assert.Contains(items, i => i.KeyDisplay == "Ctrl+H" && i.Description.Contains("(on Audio layer)"));
            Assert.Contains(items, i => i.KeyDisplay == "Left Shift + Left / Right" && i.Description.Contains("(on Filter layer)"));
            Assert.DoesNotContain(items, i => i.Description.Contains("Volume mode") || i.Description.Contains("Pan mode"));
        }
    }
}
