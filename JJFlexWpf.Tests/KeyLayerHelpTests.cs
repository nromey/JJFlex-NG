using System.Linq;
using Xunit;

namespace JJFlexWpf.Tests
{
    /// <summary>
    /// The H surface's decision and its words, against the real inventory.
    /// Sprint 44 Track K (#158, #519). Pure data — no window is opened; the
    /// long case's dialog is swept by the Tier 1 suite like every other.
    /// </summary>
    /// <remarks>
    /// Written against "VolumeMode" and "PanMode", which Track I folded into
    /// one audio layer on a sibling branch the same day; the contexts no
    /// longer existed by the merge, and every assertion about them was
    /// either vacuous (an unknown context has zero rows, and zero is
    /// "short") or red. Re-pointed at the audio and filter layers by Sprint
    /// 44 Track N (#524), which is also when the answer changed: both real
    /// layers are LONG, so H opens the list for them.
    /// </remarks>
    public class KeyLayerHelpTests
    {
        private static readonly string Audio = KeyInventory.AudioLayerContext;
        private static readonly string Filter = KeyInventory.FilterLayerContext;

        [Fact]
        public void Short_layers_speak_and_long_layers_list()
        {
            Assert.True(KeyLayerHelp.IsShort(1));
            Assert.True(KeyLayerHelp.IsShort(6), "six volume targets are one sentence (#158)");
            Assert.True(KeyLayerHelp.IsShort(KeyLayerHelp.SpokenLimit));
            Assert.False(KeyLayerHelp.IsShort(KeyLayerHelp.SpokenLimit + 1));
            Assert.False(KeyLayerHelp.IsShort(30), "thirty items are not a sentence (#158)");
        }

        [Fact]
        public void The_top_level_and_both_layers_are_long_and_list()
        {
            // The measurement #158 was built on: the JJ key layer is the one
            // that took 51 to 85 seconds to recite. It must open the list —
            // and so must the audio layer, which recited 1,100 characters in
            // one breath until #524.
            Assert.False(KeyLayerHelp.IsShort(KeyLayerHelp.Rows(KeyLayerHelp.LeaderContext).Count));
            Assert.False(KeyLayerHelp.IsShort(KeyLayerHelp.Rows(Audio).Count));
            Assert.False(KeyLayerHelp.IsShort(KeyLayerHelp.Rows(Filter).Count));
        }

        [Fact]
        public void The_spoken_form_leads_with_the_count()
        {
            int count = KeyLayerHelp.Rows(Audio).Count;
            string spoken = KeyLayerHelp.SpokenList(Audio);

            Assert.StartsWith(count.ToString(), spoken);
            Assert.Contains("keys in", spoken);
            Assert.Contains("Audio layer", spoken);
        }

        [Fact]
        public void The_spoken_form_names_every_row_by_its_in_layer_key()
        {
            string spoken = KeyLayerHelp.SpokenList(Audio);
            foreach (var (key, description) in KeyLayerHelp.Rows(Audio))
            {
                Assert.Contains(key + ", " + description, spoken);
                Assert.False(key.StartsWith("Ctrl+J", System.StringComparison.Ordinal),
                    "inside the layer the key is spoken as itself: " + key);
            }
        }

        [Fact]
        public void The_words_name_keystrokes_not_glyphs()
        {
            // #303. Every layer's "?" row must be spoken as "Shift slash",
            // never as the glyph. This was pinned on the top level's "H or ?"
            // row; Track J made JJ key slash the explorer (#519), so the top
            // level no longer has a "?" at all and the layers are where the
            // glyph lives now.
            foreach (var context in new[] { Audio, Filter })
            {
                string spoken = KeyLayerHelp.SpokenList(context);
                Assert.DoesNotContain("?", spoken);
                Assert.Contains("Shift slash", spoken);
            }

            foreach (var context in new[] { KeyLayerHelp.LeaderContext, Audio, Filter })
                foreach (var (key, _) in KeyLayerHelp.Rows(context))
                    Assert.DoesNotContain("?", key);
        }

        [Fact]
        public void The_list_title_carries_the_layer_and_the_count()
        {
            int count = KeyLayerHelp.Rows(KeyLayerHelp.LeaderContext).Count;
            string title = KeyLayerHelp.ListTitle(KeyLayerHelp.LeaderContext, count);

            Assert.Contains("JJ key", title);
            Assert.Contains(count.ToString(), title);
            Assert.False(Radios.Lexicon.LooksLikeKey(title));
        }

        [Fact]
        public void Layer_names_resolve_for_the_top_level_and_for_a_sub_layer()
        {
            Assert.Equal("the JJ key layer", KeyLayerHelp.LayerName(KeyLayerHelp.LeaderContext));
            Assert.Equal("JJ key layer", KeyLayerHelp.LayerTitle(KeyLayerHelp.LeaderContext));
            Assert.Equal("Audio layer", KeyLayerHelp.LayerName(Audio));
            Assert.Equal("Filter layer", KeyLayerHelp.LayerTitle(Filter));
        }

        [Fact]
        public void Every_row_has_a_key_and_a_description()
        {
            foreach (var context in new[] { KeyLayerHelp.LeaderContext, Audio, Filter })
            {
                var rows = KeyLayerHelp.Rows(context);
                Assert.NotEmpty(rows);
                Assert.All(rows, r =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(r.key));
                    Assert.False(string.IsNullOrWhiteSpace(r.description));
                });
            }
        }

        [Fact]
        public void The_list_rows_are_the_tree_rows()
        {
            // One table, two surfaces: what the explorer shows under V is what
            // the audio layer's H lists, key for key and word for word.
            var root = KeyTree.Build();
            var v = KeyTree.Flatten(root).Single(n => n.Entry?.KeyDisplay == "Ctrl+J, V");
            var listed = KeyLayerHelp.Rows(Audio);

            Assert.Equal(v.Children.Select(c => c.Key).ToArray(), listed.Select(r => r.key).ToArray());
            Assert.Equal(v.Children.Select(c => c.Entry!.Description).ToArray(), listed.Select(r => r.description).ToArray());
        }
    }
}
