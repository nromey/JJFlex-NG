using System;
using System.Linq;
using System.Text.RegularExpressions;
using Radios.KeyOwnership;
using Xunit;
using static Radios.Tests.KeyOwn;

namespace Radios.Tests
{
    /// <summary>
    /// The live Home field handlers held to the ruled map, for ONLY the keys
    /// #517 delivered in the handlers (2026-11-09): bare Up and Down on the
    /// Slice and Slice operations fields tune by the coarse step, and Page Up
    /// and Page Down on the slice row say where pan went.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why only these.</b> <see cref="RuledHomeKeyMap"/> declares bare Up
    /// and Down as tuning at Home scope, inherited by every Home field. The
    /// handlers deliver that on two fields only, because which other fields
    /// give up their own arrows is unruled (night question J5) and the
    /// arbiter that would make the map the executor is sprint work, by its
    /// own design. The differences below are KNOWN and listed, not fixed to
    /// satisfy the map.
    /// </para>
    /// <para>
    /// <b>Known differences, recorded 2026-11-09:</b>
    /// </para>
    /// <list type="bullet">
    /// <item>Mode, Volume, VOX, Split, Transmit slice, Squelch, Squelch level,
    /// Offset, RIT, XIT, S Meter, Mute, Record and Play recording keep their
    /// own meaning for the bare arrows (J5).</item>
    /// <item>Classic Frequency tunes by the digit under the cursor. The map
    /// agrees for Classic mode (its Classic step is the digit), so this is a
    /// difference only for Modern.</item>
    /// <item>In CLASSIC mode the map steps the Slice and Slice operations
    /// arrows by the Classic digit; the brief and the handlers use the coarse
    /// step on those fields in both modes. Reported to the orchestrator, not
    /// resolved here.</item>
    /// </list>
    /// <para>
    /// Expected values are literals from the rulings, never constants read
    /// from the map under test (the fixture's rule).
    /// </para>
    /// </remarks>
    // Loads the lexicon, which is process-wide; shares the collection the
    // other lexicon-loading classes use.
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RuledHomeKeyMapAgreementTests
    {
        private static string Handlers =>
            FieldKeyMapScan.ReadSource(FieldKeyMapScan.HandlersFile).Replace("\r\n", "\n");

        [Theory]
        [InlineData("home.slice", "AdjustSlice", GestureKey.Up, "home.tune-up", 1)]
        [InlineData("home.slice", "AdjustSlice", GestureKey.Down, "home.tune-down", -1)]
        [InlineData("home.slice-operations", "AdjustSliceOps", GestureKey.Up, "home.tune-up", 1)]
        [InlineData("home.slice-operations", "AdjustSliceOps", GestureKey.Down, "home.tune-down", -1)]
        public void Bare_arrows_on_Slice_and_Slice_operations_tune_by_the_coarse_step_in_the_map_and_in_the_handler(
            string field, string handler, GestureKey key, string command, int direction)
        {
            // The map, in Modern mode (coarse 1000 Hz in the fixture).
            var fake = new FakeActions();
            var action = Assert.IsType<ExecuteAction>(fake.Press(OnHome(field, Bare(key))));
            Assert.Equal(command, action.CommandId);
            Assert.Equal(direction * 1000L, action.SignedStepHz);
            Assert.Empty(fake.SliceGainChanges);
            Assert.Empty(fake.SliceSelections);
            Assert.Empty(fake.PanChanges);

            // The handler: the bare arrow's case tunes by the coarse step in
            // the same direction, and does nothing else.
            string body = SliceRowTuningKeysTests.Body(Handlers,
                "public void " + handler + "(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            string keyName = key == GestureKey.Up ? "Up" : "Down";
            var arm = Regex.Match(body, @"case Key\." + keyName + @":\s*(?://[^\n]*\n\s*)*(?<call>[^\n]*)\n");
            Assert.True(arm.Success, handler + " has no case for Key." + keyName);
            Assert.Equal("TuneByStep(coarse: true, " + direction + ");", arm.Groups["call"].Value.Trim());
        }

        [Theory]
        [InlineData("home.slice", GestureKey.PageUp)]
        [InlineData("home.slice", GestureKey.PageDown)]
        [InlineData("home.slice-operations", GestureKey.PageUp)]
        [InlineData("home.slice-operations", GestureKey.PageDown)]
        [InlineData("home.frequency", GestureKey.PageDown)]
        [InlineData("home.vox", GestureKey.PageDown)]
        public void Page_Up_and_Page_Down_say_where_pan_went_in_the_map(string field, GestureKey key)
        {
            var fake = new FakeActions();
            var refusal = Assert.IsType<Refuse>(fake.Press(OnHome(field, Bare(key))));
            Assert.Equal(RefusalReason.Relocated, refusal.Reason);
            Assert.Equal("audio", refusal.Relocation!.LayerId);
            Assert.Equal("pan", refusal.Relocation.LayerTargetId);
            Assert.Empty(fake.PanChanges);
        }

        /// <summary>
        /// The handler side of the same rule, on EVERY field of the slice row:
        /// the scan finds Page Up and Page Down claimed on each, by the one
        /// slice-row step, which says the relocation sentence and pans nothing.
        /// </summary>
        [Fact]
        public void Page_Up_and_Page_Down_say_where_pan_went_on_every_slice_row_field()
        {
            var result = FieldKeyMapScan.ScanRepository();
            Assert.True(result.Fields.Count >= 12);
            var missing = result.Fields
                .SelectMany(f => new[] { "Page Up", "Page Down" }
                    .Where(k => !f.Claims.Any(c => c.Binding.Display == k && c.Method == "TryHandleSliceRowKey"))
                    .Select(k => f.Context + " " + k))
                .ToList();
            Assert.True(missing.Count == 0, "not answered by the slice-row step: " + string.Join(", ", missing));

            string step = SliceRowTuningKeysTests.Body(Handlers,
                "private bool TryHandleSliceRowKey(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            Assert.Matches(new Regex(@"if \(key == Key\.PageUp \|\| key == Key\.PageDown\)\s*\{\s*SayWherePanWent\(\);"), step);

            // Nothing on the slice row pans any more.
            Assert.DoesNotContain("SetVFOPan(", Handlers, StringComparison.Ordinal);
            Assert.DoesNotContain("AdjustPan(", Handlers, StringComparison.Ordinal);
        }

        /// <summary>
        /// The sentence names the route the map declares: the JJ key, then A
        /// (the audio layer's opener), then P (pan inside it).
        /// </summary>
        [Fact]
        public void The_relocation_sentence_names_the_route_the_map_declares()
        {
            var open = RuledHomeKeyMap.Declarations.Single(d => d.LayerEffect == LayerEffect.OpenLayer && d.OpensLayerId == "audio");
            Assert.Equal(GestureKey.A, open.Chord.Key);
            var pan = RuledHomeKeyMap.Declarations.Single(d => d.SelectsLayerTargetId == "pan");
            Assert.Equal(GestureKey.P, pan.Chord.Key);

            Lexicon.Load(Lexicon.Partitions);
            string said = Lexicon.Get("settings.pan.moved_to_audio_layer");
            Assert.Contains("audio layer", said, StringComparison.Ordinal);
            Assert.Contains("JJ key, A, then P", said, StringComparison.Ordinal);
        }

        /// <summary>
        /// FrequencyDisplay's Page Down jump to the Frequency field is gone in
        /// the same change (the 2026-09-21 ruling's condition): a key that used
        /// to pan must not quietly become a focus jump.
        /// </summary>
        [Fact]
        public void Page_Down_no_longer_jumps_to_the_Frequency_field()
        {
            string display = FieldKeyMapScan.ReadSource("JJFlexWpf/Controls/FrequencyDisplay.xaml.cs").Replace("\r\n", "\n");
            int at = display.IndexOf("if (e.Key == Key.PageDown || e.Key == Key.PageUp)", StringComparison.Ordinal);
            Assert.True(at > 0, "the Page Up and Page Down branch of RouteKey has moved");
            string branch = display.Substring(at, display.IndexOf("return;", at, StringComparison.Ordinal) - at);
            Assert.DoesNotContain("NavigateToField", branch, StringComparison.Ordinal);
            Assert.Contains("FieldKeyDown?.Invoke(pageField, e);", branch, StringComparison.Ordinal);
        }
    }
}
