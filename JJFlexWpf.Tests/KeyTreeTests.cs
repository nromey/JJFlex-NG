using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace JJFlexWpf.Tests
{
    /// <summary>
    /// The JJ key tree, derived from the REAL inventory and from planted ones.
    /// Sprint 44 Track K (#158, #519).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure data — no window, no dispatcher, no desktop — so it runs green
    /// under DeskGuard on the interactive desktop, the same footing as
    /// LeaderNearMissTests. The dialog that renders the tree is swept by the
    /// Tier 1 suite like every other window; what is pinned here is the
    /// STRUCTURE, because two other tracks are reshaping the inventory while
    /// this is written and the whole point of deriving the tree is that it
    /// follows them.
    /// </para>
    /// <para>
    /// Nothing below asserts a specific count, a specific letter set or a
    /// specific layer name beyond the two layers that exist today and are
    /// used as positive controls. The planted-inventory tests are the ones
    /// that state the contract: which conventions link a layer to its door,
    /// and — as a negative control — which do not.
    /// </para>
    /// </remarks>
    public class KeyTreeTests
    {
        private static IEnumerable<KeyTreeNode> Chords(KeyTreeNode root)
            => KeyTree.Flatten(root).Where(n => n.Kind == KeyTreeNodeKind.Chord);

        // ────────────────────────────────────────────────────────────────
        //  The real inventory
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_tree_actually_saw_the_layer()
        {
            // Positive control before anything else: an empty tree would make
            // every "no duplicate" and "no bare glyph" check below pass for
            // nothing.
            var root = KeyTree.Build();
            int chords = Chords(root).Count();
            int layers = Chords(root).Count(c => c.HasChildren);

            Assert.True(chords >= 30, $"only {chords} chords — the Leader rows were not found");
            Assert.True(layers >= 2, $"only {layers} layers — volume mode and pan mode should both be found");
            Assert.True(root.Children.Count >= 3, "fewer than three tiers");
        }

        [Fact]
        public void Every_leader_row_appears_exactly_once_as_a_chord()
        {
            var root = KeyTree.Build();
            var leaderRows = KeyInventory.All().Where(e => e.Context == KeyTree.LeaderContext).ToList();
            var chordEntries = Chords(root).Select(c => c.Entry).ToList();

            Assert.Equal(leaderRows.Count, chordEntries.Count);
            foreach (var row in leaderRows)
                Assert.Single(chordEntries, e => ReferenceEquals(e, row));
        }

        [Fact]
        public void Tiers_partition_the_chords_and_every_chord_sits_in_its_tier()
        {
            var root = KeyTree.Build();
            int underTiers = root.Children.Sum(t => t.Children.Count);
            Assert.Equal(Chords(root).Count(), underTiers);

            foreach (var tier in root.Children)
            {
                Assert.Equal(KeyTreeNodeKind.Tier, tier.Kind);
                foreach (var chord in tier.Children)
                    Assert.Equal(tier.Tier, KeyTree.TierOf(chord.Entry!));
            }
        }

        [Fact]
        public void The_help_rows_and_escape_sit_in_the_help_tier()
        {
            var root = KeyTree.Build();
            var help = root.Children.SingleOrDefault(t => t.Tier == KeyTier.Help);
            Assert.NotNull(help);
            Assert.Contains(help!.Children, c => c.Entry!.KeyDisplay.Contains("Escape"));
            Assert.Contains(help.Children, c => c.Entry!.KeyDisplay.StartsWith("Ctrl+J, H", System.StringComparison.Ordinal));
        }

        [Fact]
        public void Audio_layer_rows_sit_under_the_V_chord_by_the_declared_link()
        {
            // Written for "VolumeMode" and its "Ctrl+J, V, H" prefix rows;
            // Track I folded volume mode into the audio layer, whose rows are
            // bare keys ("Ctrl+H", "V") that no prefix can attach to the
            // chord. OpensLayer carries the link now (Sprint 44 Track N, #524).
            var root = KeyTree.Build();
            var v = Chords(root).Single(c => c.Entry!.KeyDisplay == "Ctrl+J, V");
            var audioRows = KeyInventory.All().Where(e => e.Context == KeyInventory.AudioLayerContext).ToList();

            Assert.True(v.HasChildren, "Ctrl+J, V opens the audio layer and must have children");
            Assert.Equal(audioRows.Count, v.Children.Count);
            Assert.Equal(KeyInventory.AudioLayerContext, v.LayerContext);
            // Inside the layer the keys are spoken as themselves, not as the
            // whole three-key chord: "H", not "Ctrl+J, V, H".
            Assert.Contains(v.Children, c => c.Key == "H");
            Assert.DoesNotContain(v.Children, c => c.Key.StartsWith("Ctrl+J", System.StringComparison.Ordinal));
        }

        [Fact]
        public void Alt_P_opens_the_same_audio_layer_as_V_does()
        {
            // Three doors, one room: Ctrl+J, A; Ctrl+J, V; and Ctrl+J, Alt+P
            // (which lands on pan) all declare the audio layer, so the tree
            // shows the same branch under each.
            var root = KeyTree.Build();
            var pan = Chords(root).Single(c => c.Entry!.KeyDisplay == "Ctrl+J, Alt+P");
            var v = Chords(root).Single(c => c.Entry!.KeyDisplay == "Ctrl+J, V");

            Assert.True(pan.HasChildren);
            Assert.Equal(KeyInventory.AudioLayerContext, pan.LayerContext);
            Assert.Equal(v.Children.Select(c => c.Key), pan.Children.Select(c => c.Key));
            Assert.Contains(pan.Children, c => c.Key == "Ctrl+P");
        }

        [Fact]
        public void Chord_keys_drop_the_leader_prefix_the_parent_already_said()
        {
            var root = KeyTree.Build();
            foreach (var chord in Chords(root))
            {
                Assert.False(chord.Key.StartsWith("Ctrl+J", System.StringComparison.Ordinal),
                    $"chord key still carries the leader prefix: {chord.Key}");
                Assert.False(string.IsNullOrWhiteSpace(chord.Key));
            }
        }

        [Fact]
        public void No_node_text_carries_a_bare_question_mark()
        {
            // #303: a literal "?" may not be voiced at all with punctuation set
            // low, so the row naming the help key would lose the key. Every
            // node goes through SpokenKey.
            var root = KeyTree.Build();
            foreach (var node in KeyTree.Flatten(root))
            {
                Assert.DoesNotContain("?", node.Text);
                Assert.DoesNotContain("?", node.Key);
            }
            Assert.Contains(KeyTree.Flatten(root), n => n.Text.Contains("Shift slash"));
        }

        [Fact]
        public void Every_node_has_text_and_every_chord_names_its_description()
        {
            var root = KeyTree.Build();
            foreach (var node in KeyTree.Flatten(root))
            {
                Assert.False(string.IsNullOrWhiteSpace(node.Text), $"empty text on a {node.Kind} node");
                Assert.False(Radios.Lexicon.LooksLikeKey(node.Text), $"unresolved lexicon key in node text: {node.Text}");
                if (node.Entry != null)
                    Assert.Contains(node.Entry.Description, node.Text);
            }
        }

        [Fact]
        public void A_layer_chord_says_how_many_keys_are_inside()
        {
            var root = KeyTree.Build();
            var v = Chords(root).Single(c => c.Entry!.KeyDisplay == "Ctrl+J, V");
            Assert.Contains(v.Children.Count.ToString(), v.Text);
        }

        [Fact]
        public void Chords_within_a_tier_are_alphabetical_by_key()
        {
            var root = KeyTree.Build();
            var plain = root.Children.Single(t => t.Tier == KeyTier.Plain);
            var letters = plain.Children.Select(c => KeyTree.JumpLetter(c)).ToList();
            Assert.Equal(letters.OrderBy(l => l).ToList(), letters);
        }

        [Fact]
        public void FindLayer_locates_a_layer_and_the_root()
        {
            var root = KeyTree.Build();
            Assert.Same(root, KeyTree.FindLayer(root, KeyTree.LeaderContext));
            Assert.NotNull(KeyTree.FindLayer(root, KeyInventory.AudioLayerContext));
            Assert.NotNull(KeyTree.FindLayer(root, KeyInventory.FilterLayerContext));
            Assert.Null(KeyTree.FindLayer(root, "NoSuchLayer"));
        }

        [Fact]
        public void Jump_letters_ignore_the_modifier()
        {
            var root = KeyTree.Build();
            var shift = root.Children.Single(t => t.Tier == KeyTier.Shift);
            var shiftN = shift.Children.Single(c => c.Entry!.KeyDisplay == "Ctrl+J, Shift+N");
            Assert.Equal('N', KeyTree.JumpLetter(shiftN));
        }

        [Fact]
        public void LayerRows_answer_for_the_top_level_and_for_a_sub_layer()
        {
            var leader = KeyTree.LayerRows(KeyTree.LeaderContext);
            Assert.Equal(KeyInventory.All().Count(e => e.Context == KeyTree.LeaderContext), leader.Count);

            var audio = KeyTree.LayerRows(KeyInventory.AudioLayerContext);
            Assert.Equal(KeyInventory.All().Count(e => e.Context == KeyInventory.AudioLayerContext), audio.Count);
            Assert.All(audio, r => Assert.Equal(KeyInventory.AudioLayerContext, r.Context));

            // The audio layer has three doors; the first declared one is its
            // opener, and under the four-tier grammar that is JJ key A.
            Assert.NotNull(KeyTree.OpenerOf(KeyInventory.AudioLayerContext));
            Assert.Equal("Ctrl+J, A", KeyTree.OpenerOf(KeyInventory.AudioLayerContext)!.KeyDisplay);
            Assert.Equal("Ctrl+J, F", KeyTree.OpenerOf(KeyInventory.FilterLayerContext)!.KeyDisplay);
            Assert.Null(KeyTree.OpenerOf(KeyTree.LeaderContext));
        }

        // ────────────────────────────────────────────────────────────────
        //  Planted inventories — the contract another track builds against
        // ────────────────────────────────────────────────────────────────

        private static KeyInventory.FixedKeyEntry Row(string context, string key, string desc,
            string group = "General", string opens = "")
            => new(context, context + " label", key, desc, new[] { "kw" }, "Radio", group) { OpensLayer = opens };

        [Fact]
        public void A_layer_written_with_the_chord_prefix_is_found_with_nothing_declared()
        {
            var planted = new[]
            {
                Row("Leader", "Ctrl+J, F", "Enter the filter layer"),
                Row("Filter", "Ctrl+J, F, S", "Speak the filter"),
                Row("Filter", "Ctrl+J, F, Escape", "Leave the filter layer"),
            };
            var root = KeyTree.Build(planted);
            var f = Chords(root).Single();

            Assert.True(f.HasChildren);
            Assert.Equal(2, f.Children.Count);
            Assert.Equal("Filter", f.LayerContext);
            Assert.Equal("S", f.Children[0].Key);
            Assert.Equal("Escape", f.Children[1].Key);
            Assert.Equal("Ctrl+J, F", KeyTree.OpenerOf("Filter", planted)!.KeyDisplay);
        }

        [Fact]
        public void A_layer_written_as_bare_keys_is_found_when_its_door_declares_it()
        {
            var planted = new[]
            {
                Row("Leader", "Ctrl+J, A", "Enter the audio layer", opens: "Audio"),
                Row("Audio", "Up / Down", "Adjust the picked target"),
                Row("Audio", "Ctrl+H", "Earphones"),
            };
            var root = KeyTree.Build(planted);
            var a = Chords(root).Single();

            Assert.True(a.HasChildren);
            Assert.Equal(2, a.Children.Count);
            Assert.Equal("Audio", a.LayerContext);
            Assert.Equal("Up / Down", a.Children[0].Key);
            Assert.Equal("Ctrl+H", a.Children[1].Key);
        }

        [Fact]
        public void A_layer_that_follows_neither_convention_is_NOT_found()
        {
            // The negative control. A track that writes its rows as bare keys
            // and declares no door gets a chord with no children — and this
            // test is what makes that a known shape rather than a silence.
            var planted = new[]
            {
                Row("Leader", "Ctrl+J, M", "Enter the mode layer"),
                Row("Mode", "A", "AM"),
                Row("Mode", "S", "SAM"),
            };
            var root = KeyTree.Build(planted);
            var m = Chords(root).Single();

            Assert.False(m.HasChildren);
            Assert.Null(KeyTree.OpenerOf("Mode", planted));
            // ...but the layer can still LIST itself, by Context, so its own
            // H is not blocked on the door being registered.
            Assert.Equal(2, KeyTree.LayerRows("Mode", planted).Count);
        }

        [Fact]
        public void Both_links_together_yield_each_row_once_in_inventory_order()
        {
            var planted = new[]
            {
                Row("Leader", "Ctrl+J, F", "Enter the filter layer", opens: "Filter"),
                Row("Filter", "Ctrl+J, F, S", "Speak the filter"),   // matched by both rules
                Row("Filter", "Left Shift + Left / Right", "Walk the low edge"),
                Row("Filter", "Ctrl+J, F, T", "Transmit filter"),
            };
            var root = KeyTree.Build(planted);
            var f = Chords(root).Single();

            Assert.Equal(new[] { "S", "Left Shift + Left / Right", "T" }, f.Children.Select(c => c.Key).ToArray());
        }

        [Fact]
        public void Layers_nest()
        {
            var planted = new[]
            {
                Row("Leader", "Ctrl+J, A", "Enter the audio layer", opens: "Audio"),
                Row("Audio", "P", "Enter pan inside audio", opens: "AudioPan"),
                Row("AudioPan", "Left / Right", "Nudge the pan"),
            };
            var root = KeyTree.Build(planted);
            var a = Chords(root).Single();
            var p = a.Children.Single();

            Assert.True(p.HasChildren);
            Assert.Equal("AudioPan", p.LayerContext);
            Assert.Equal("Left / Right", p.Children.Single().Key);
            Assert.NotNull(KeyTree.FindLayer(root, "AudioPan"));
        }

        [Fact]
        public void Tier_follows_the_modifier_and_help_follows_the_group()
        {
            Assert.Equal(KeyTier.Plain, KeyTree.TierOf(Row("Leader", "Ctrl+J, N", "x")));
            Assert.Equal(KeyTier.Shift, KeyTree.TierOf(Row("Leader", "Ctrl+J, Shift+N", "x")));
            Assert.Equal(KeyTier.Ctrl, KeyTree.TierOf(Row("Leader", "Ctrl+J, Ctrl+N", "x")));
            Assert.Equal(KeyTier.Alt, KeyTree.TierOf(Row("Leader", "Ctrl+J, Alt+N", "x")));
            Assert.Equal(KeyTier.Shift, KeyTree.TierOf(Row("Leader", "Ctrl+J, Shift+A through Shift+H", "x")));
            Assert.Equal(KeyTier.Help, KeyTree.TierOf(Row("Leader", "Ctrl+J, H or ?", "x", group: "help")));
            Assert.Equal(KeyTier.Help, KeyTree.TierOf(Row("Leader", "Ctrl+J, Escape", "x", group: "help")));
            Assert.Equal(KeyTier.Other, KeyTree.TierOf(Row("Leader", "Ctrl+J, Escape", "x")));
        }

        [Fact]
        public void An_empty_inventory_yields_an_empty_but_well_formed_tree()
        {
            var root = KeyTree.Build(System.Array.Empty<KeyInventory.FixedKeyEntry>());
            Assert.Equal(KeyTreeNodeKind.Root, root.Kind);
            Assert.Empty(root.Children);
            Assert.False(string.IsNullOrWhiteSpace(root.Text));
        }
    }
}
