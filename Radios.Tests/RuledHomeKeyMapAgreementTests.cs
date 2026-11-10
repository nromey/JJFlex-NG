using System;
using System.Linq;
using System.Text.RegularExpressions;
using Radios.KeyOwnership;
using Xunit;
using static Radios.Tests.KeyOwn;

namespace Radios.Tests
{
    /// <summary>
    /// The live Home field handlers held to the ruled map, for the keys #517
    /// delivered in the handlers: Page Up and Page Down on the slice row's
    /// other fields say where pan went. Bare Up and Down, and Page Up, Page Down
    /// and Home, on the Slice and Slice operations fields now DIFFER from the
    /// map on purpose (Noel, 2026-11-09 20:50, 20:51 and 21:01), and the
    /// handlers are held to those rulings directly.
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
    /// <item><b>Added 2026-11-09 night (the 21:01 ruling).</b> The map still
    /// records the 2026-09-20 ruling, bare arrows tune on every Home field. On
    /// the Slice and Slice operations fields the arrows are now the
    /// operator's choice, two boxes in Settings (Slice: switch slices by
    /// default; Slice operations: tune by default), and Page Up, Page Down
    /// and, on the Slice field, Home pan again (20:51). The map is NOT made
    /// setting-dependent: it is the unwired arbiter's fixed declaration set,
    /// and the defaults live only in AccessibilityConfig's initialisers.</item>
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

        /// <summary>
        /// The map still says the bare arrows tune on these two fields (the
        /// 2026-09-20 ruling it records). Kept as the known difference's
        /// positive control: if the map ever changes, this says so.
        /// </summary>
        [Theory]
        [InlineData("home.slice", GestureKey.Up, "home.tune-up", 1)]
        [InlineData("home.slice-operations", GestureKey.Down, "home.tune-down", -1)]
        public void The_map_still_records_tuning_on_the_two_slice_fields(
            string field, GestureKey key, string command, int direction)
        {
            var fake = new FakeActions();
            var action = Assert.IsType<ExecuteAction>(fake.Press(OnHome(field, Bare(key))));
            Assert.Equal(command, action.CommandId);
            Assert.Equal(direction * 1000L, action.SignedStepHz);
        }

        /// <summary>
        /// One arm per handler for both arrows, which reads its box exactly
        /// once (Noel, 2026-11-09 20:59: flipping it later must stay a
        /// one-place change), and does the ruled thing each way: checked, tune
        /// by the coarse step; unchecked, what the field did before the
        /// 2026-11-09 batch (Slice: switch slices in the slice arrow order;
        /// Slice operations: the slice's volume). The behaviour in the running
        /// app is the harness's to confirm.
        /// </summary>
        [Theory]
        [InlineData("AdjustSlice", "SliceFieldArrowsTune",
            "CycleVFO(key == Key.Up ? SliceStepForUpArrow : SliceStepForDownArrow);")]
        [InlineData("AdjustSliceOps", "SliceOpsFieldArrowsTune",
            "AdjustGain(vfo, key == Key.Up ? FlexBase.GainIncrement : -FlexBase.GainIncrement);")]
        public void Each_slice_fields_arrows_follow_their_box_both_ways(string handler, string box, string unchecked_)
        {
            string body = SliceRowTuningKeysTests.Body(Handlers,
                "public void " + handler + "(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            var arm = Regex.Match(body, @"case Key\.Up:\s*case Key\.Down:(?<arm>.*?)break;", RegexOptions.Singleline);
            Assert.True(arm.Success, handler + " has no single arm for Up and Down");
            string code = Regex.Replace(arm.Groups["arm"].Value, @"//[^\n]*", "");
            int ifAt = code.IndexOf("if (Radios.AccessibilityConfig.Current." + box + ")", StringComparison.Ordinal);
            int tune = code.IndexOf("TuneByStep(coarse: true, key == Key.Up ? 1 : -1);", StringComparison.Ordinal);
            int elseAt = code.IndexOf("else", StringComparison.Ordinal);
            int other = code.IndexOf(unchecked_, StringComparison.Ordinal);
            Assert.True(ifAt >= 0 && tune > ifAt && elseAt > tune && other > elseAt,
                handler + ": checked must tune, unchecked must " + unchecked_);
            // The box is read once in all the handlers, and its default is set
            // only in the initialiser.
            Assert.Single(Regex.Matches(Handlers, @"\b" + box + @"\b"));
            string config = FieldKeyMapScan.ReadSource("Radios/AccessibilityConfig.cs");
            Assert.Single(Regex.Matches(config, @"public bool " + box + @" \{ get; set; \} = (true|false);"));
        }

        [Fact]
        public void The_defaults_are_Slice_switches_and_Slice_operations_tunes()
        {
            var config = new AccessibilityConfig();
            Assert.False(config.SliceFieldArrowsTune);
            Assert.True(config.SliceOpsFieldArrowsTune);
        }

        /// <summary>
        /// Pan is back on the Slice and Slice operations fields whatever the
        /// boxes say (Noel, 2026-11-09 20:51), exactly as before the
        /// 2026-11-09 batch: Slice, Page Down hard left, Home centre, Page Up
        /// hard right; Slice operations, Page Up and Page Down by the pan step.
        /// No Home on Slice operations: it never had one.
        /// </summary>
        [Fact]
        public void Page_keys_pan_on_the_Slice_and_Slice_operations_fields()
        {
            string slice = SliceRowTuningKeysTests.Body(Handlers,
                "public void AdjustSlice(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            Assert.Matches(new Regex(@"case Key\.PageDown:\s*if \(Rig\.ValidVFO\(vfo\)\)\s*\{\s*Rig\.SetVFOPan\(vfo, FlexBase\.MinPan\);"), slice);
            Assert.Matches(new Regex(@"case Key\.Home:\s*if \(Rig\.ValidVFO\(vfo\)\)\s*\{\s*Rig\.SetVFOPan\(vfo, \(FlexBase\.MaxPan - FlexBase\.MinPan\) / 2\);"), slice);
            Assert.Matches(new Regex(@"case Key\.PageUp:\s*if \(Rig\.ValidVFO\(vfo\)\)\s*\{\s*Rig\.SetVFOPan\(vfo, FlexBase\.MaxPan\);"), slice);

            string ops = SliceRowTuningKeysTests.Body(Handlers,
                "public void AdjustSliceOps(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            Assert.Matches(new Regex(@"case Key\.PageUp:\s*(?://[^\n]*\n\s*)*if \(Rig\.ValidVFO\(vfo\)\)\s*AdjustPan\(vfo, FlexBase\.PanIncrement\);"), ops);
            Assert.Matches(new Regex(@"case Key\.PageDown:\s*if \(Rig\.ValidVFO\(vfo\)\)\s*AdjustPan\(vfo, -FlexBase\.PanIncrement\);"), ops);
            Assert.DoesNotContain("case Key.Home:", ops, StringComparison.Ordinal);

            // The routing trap: the slice-row step answers Page Up and Page
            // Down first on every field, so it must let these two through.
            string step = SliceRowTuningKeysTests.Body(Handlers,
                "private bool TryHandleSliceRowKey(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            Assert.Contains("if ((key == Key.PageUp || key == Key.PageDown) && !PageKeysPan(field.Key))", step, StringComparison.Ordinal);
            Assert.Contains("internal static bool PageKeysPan(string? fieldKey) => fieldKey == \"Slice\" || fieldKey == \"SliceOps\";",
                Handlers, StringComparison.Ordinal);
        }

        [Theory]
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
        /// The handler side of the same rule, on every OTHER field of the slice
        /// row: the scan finds Page Up and Page Down claimed on each by the one
        /// slice-row step, which says the relocation sentence and pans nothing.
        /// Slice and Slice operations pan instead (the test above).
        /// </summary>
        [Fact]
        public void Page_Up_and_Page_Down_say_where_pan_went_on_every_other_slice_row_field()
        {
            var result = FieldKeyMapScan.ScanRepository();
            Assert.True(result.Fields.Count >= 12);
            var missing = result.Fields
                .Where(f => f.Context != "Slice" && f.Context != "SliceOps")
                .SelectMany(f => new[] { "Page Up", "Page Down" }
                    .Where(k => !f.Claims.Any(c => c.Binding.Display == k && c.Method == "TryHandleSliceRowKey"))
                    .Select(k => f.Context + " " + k))
                .ToList();
            Assert.True(missing.Count == 0, "not answered by the slice-row step: " + string.Join(", ", missing));

            string step = SliceRowTuningKeysTests.Body(Handlers,
                "private bool TryHandleSliceRowKey(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            Assert.Matches(new Regex(@"if \(\(key == Key\.PageUp \|\| key == Key\.PageDown\) && !PageKeysPan\(field\.Key\)\)\s*\{\s*SayWherePanWent\(\);"), step);

            // Pan happens on the two slice fields only: SetVFOPan in the Slice
            // field's handler and the restored AdjustPan, which only Slice
            // operations calls.
            Assert.Equal(4, Regex.Matches(Handlers, @"Rig\.SetVFOPan\(").Count);   // three in AdjustSlice, one in AdjustPan
            Assert.Equal(2, Regex.Matches(SliceRowTuningKeysTests.Body(Handlers,
                "public void AdjustSliceOps(FrequencyDisplay.DisplayField field, KeyEventArgs e)"), @"AdjustPan\(").Count);
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
