using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Noel, 2026-11-08 16:24-16:27 (#703, #877): "ctrl up and down does
    /// coarse, shift up and down does fine", everywhere including Frequency.
    /// On JJ Flexible Home's slice row that is now true on every field, in
    /// both tuning modes, through four real commands the Hotkey Editor can
    /// bind; and any other modifier on an arrow says it does nothing instead
    /// of acting as the bare arrow.
    /// </summary>
    /// <remarks>
    /// From source and the field-map scan, because the handlers need a WPF key
    /// event and a live keyboard. The pure decisions are held in
    /// <c>JJFlexWpf.Tests/SliceRowKeysTests</c>. Pressing the chords, held and
    /// repeated, under NVDA and JAWS, is the bench's.
    /// </remarks>
    public sealed class SliceRowTuningKeysTests
    {
        private static string Handlers => FieldKeyMapScan.ReadSource(FieldKeyMapScan.HandlersFile);

        internal static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature + " was not found");
            int open = source.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException(signature + " has no end");
        }

        /// <summary>
        /// Every field handler the Home dispatch reaches asks the slice-row
        /// keys FIRST. Asked after the field's own map, Shift+Up on VOX would
        /// still toggle VOX, which is the defect.
        /// </summary>
        [Fact]
        public void Every_dispatched_field_handler_asks_the_slice_row_keys_first()
        {
            var handlers = FieldKeyMapScan.ScanRepository().Fields
                .Select(f => f.Handler)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // Positive control: the scan really read the dispatch.
            Assert.Contains("AdjustSlice", handlers);
            Assert.Contains("AdjustVox", handlers);
            Assert.Contains("AdjustFreq", handlers);
            Assert.Contains("AdjustFreqModern", handlers);
            Assert.True(handlers.Count >= 15, "only " + handlers.Count + " handlers dispatched");

            string src = Handlers.Replace("\r\n", "\n");
            var late = new List<string>();
            foreach (string h in handlers)
            {
                string body = Body(src, "public void " + h + "(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
                string first = body.TrimStart('{').TrimStart();
                if (!first.StartsWith("if (TryHandleSliceRowKey(field, e)) return;", StringComparison.Ordinal))
                    late.Add(h);
            }
            Assert.True(late.Count == 0, "these handlers do not ask the slice-row keys first: " + string.Join(", ", late));
        }

        /// <summary>
        /// The scan sees Control and Shift with Up and Down on EVERY field,
        /// Classic and Modern, as the universal rows promise. This is the
        /// assertion the mutation of a single handler turns red.
        /// </summary>
        [Fact]
        public void The_scan_finds_the_four_tuning_chords_on_every_field()
        {
            var result = FieldKeyMapScan.ScanRepository();
            Assert.True(result.Fields.Count >= 12);
            var missing = new List<string>();
            foreach (var field in result.Fields)
            {
                foreach (string chord in new[] { "Ctrl+Up", "Ctrl+Down", "Shift+Up", "Shift+Down" })
                {
                    if (!field.Claims.Any(c => c.Binding.Display == chord && !c.StateGated && !c.ModsUnresolved))
                        missing.Add(field.Context + " " + chord);
                }
            }
            Assert.True(missing.Count == 0, "not claimed: " + string.Join(", ", missing));
        }

        [Fact]
        public void The_chords_tune_through_the_one_command_path_and_the_bare_arrows_are_not_claimed_there()
        {
            string body = Body(Handlers.Replace("\r\n", "\n"),
                "private bool TryHandleSliceRowKey(FrequencyDisplay.DisplayField field, KeyEventArgs e)");

            Assert.Contains("(key == Key.Up || key == Key.Down) && Keyboard.Modifiers == ModifierKeys.Control", body, StringComparison.Ordinal);
            Assert.Contains("TuneByStepFromField(coarse: true,", body, StringComparison.Ordinal);
            Assert.Contains("(key == Key.Up || key == Key.Down) && Keyboard.Modifiers == ModifierKeys.Shift", body, StringComparison.Ordinal);
            Assert.Contains("TuneByStepFromField(coarse: false,", body, StringComparison.Ordinal);
            // The class: every other modifier set says it does nothing.
            Assert.Contains("IsUnboundArrowChord(key, Keyboard.Modifiers) && !IsRegistryBound(e)", body, StringComparison.Ordinal);
            Assert.Contains("settings.home.key_unbound", body, StringComparison.Ordinal);
            // The bare arrows stay each field's own.
            Assert.DoesNotContain("ModifierKeys.None", body, StringComparison.Ordinal);

            string tune = Body(Handlers.Replace("\r\n", "\n"), "public void TuneByStep(bool coarse, int direction)");
            Assert.Contains("TuneFreq(", tune, StringComparison.Ordinal);
            Assert.Contains("_coarseStep", tune, StringComparison.Ordinal);
            Assert.Contains("_fineStep", tune, StringComparison.Ordinal);
        }

        /// <summary>
        /// The Modern Frequency field no longer keeps its own Shift test on the
        /// arrows; the fine step is the slice row's, and so the command's.
        /// </summary>
        [Fact]
        public void Modern_frequency_defers_its_fine_step_to_the_slice_row()
        {
            string body = Body(Handlers.Replace("\r\n", "\n"),
                "public void AdjustFreqModern(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            Assert.DoesNotContain("shiftHeld", body, StringComparison.Ordinal);
            Assert.Contains("TuneByStep(coarse: true, 1);", body, StringComparison.Ordinal);
            Assert.Contains("TuneByStep(coarse: true, -1);", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// Classic Frequency keeps its digit tuning on the bare arrows: only
        /// Control and Shift go to the command there.
        /// </summary>
        [Fact]
        public void Classic_frequency_keeps_the_digit_under_the_cursor_on_the_bare_arrows()
        {
            string body = Body(Handlers.Replace("\r\n", "\n"),
                "public void AdjustFreq(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            Assert.Matches(new Regex(@"case Key\.Up:\s*_inStepEntry = false;\s*TuneFreq\(step\);"), body);
        }

        /// <summary>
        /// The four tuning commands carry their default chords across the main
        /// window (#703 J4, ruled by Noel 2026-11-09 20:40), at Radio scope.
        /// This test said the opposite until then ("no default key, and say
        /// why"): J4 reversed it. Where they yield is TuningChordYield's, held
        /// by JJFlexWpf.Tests.TuningChordYieldTests.
        /// </summary>
        [Fact]
        public void The_four_tuning_commands_are_bound_across_the_main_window()
        {
            string keys = FieldKeyMapScan.ReadSource("JJFlexWpf/KeyCommands.cs").Replace("\r\n", "\n");
            var expected = new (string Id, string Call, string Chord)[]
            {
                ("TuneCoarseUp", "TuneFromCommand(coarse: true, direction: 1)", "Keys.Up | Keys.Control"),
                ("TuneCoarseDown", "TuneFromCommand(coarse: true, direction: -1)", "Keys.Down | Keys.Control"),
                ("TuneFineUp", "TuneFromCommand(coarse: false, direction: 1)", "Keys.Up | Keys.Shift"),
                ("TuneFineDown", "TuneFromCommand(coarse: false, direction: -1)", "Keys.Down | Keys.Shift"),
            };
            foreach (var (id, call, chord) in expected)
            {
                Assert.Contains("new(CommandValues." + id + ", KeyTypes.Command, () => " + call, keys, StringComparison.Ordinal);
                Assert.Contains("new(" + chord + ", CommandValues." + id + ", KeyScope.Radio),", keys, StringComparison.Ordinal);
                Assert.DoesNotContain("[CommandValues." + id + "] = new(UnboundReason", keys, StringComparison.Ordinal);
            }
            // The one tuning path.
            Assert.Contains("_context.GetMainWindow()?.FreqHandlers?.TuneByStep(coarse, direction);", keys, StringComparison.Ordinal);
        }

        [Fact]
        public void The_universal_rows_declare_the_four_chords()
        {
            string inventory = FieldKeyMapScan.ReadSource(FieldKeyMapScan.InventoryFile);
            Assert.Contains("new(\"Home\", \"Any Home field\", \"Ctrl+Up / Ctrl+Down\", \"Tune by your coarse step\"", inventory, StringComparison.Ordinal);
            Assert.Contains("new(\"Home\", \"Any Home field\", \"Shift+Up / Shift+Down\", \"Tune by your fine step\"", inventory, StringComparison.Ordinal);
        }

        /// <summary>
        /// Positive control for the scanner fix this change needed: the code
        /// writes <c>ModifierKeys.Control</c> and the inventory "Ctrl", and a
        /// Control claim must reconcile with a "Ctrl" row in both directions.
        /// Before the fix this synthetic tree reported one finding each way.
        /// </summary>
        [Fact]
        public void A_Control_claim_reconciles_with_a_Ctrl_row()
        {
            const string handlers = @"
class FreqOutHandlers
{
    public void AdjustAlpha(DisplayField field, KeyEventArgs e)
    {
        var key = RawKey(e);
        if ((key == Key.Up || key == Key.Down) && Keyboard.Modifiers == ModifierKeys.Control) { Tune(); }
    }
}";
            const string dispatch = @"
class MainWindow
{
    private void FreqOut_FieldKeyDown(DisplayField field, KeyEventArgs e)
    {
        switch (field.Key) { case ""Alpha"": _h.AdjustAlpha(field, e); break; }
    }
}";
            const string inventory = @"
public static class KeyInventory
{
    private static readonly FixedKeyEntry[] UniversalHome =
    {
        new(""Home"", ""Any Home field"", ""Ctrl+Up / Ctrl+Down"", ""Tune"", new[] { ""tune"" }),
    };
    private static readonly FixedKeyEntry[] HomeNavigation = { };
    private static readonly FixedKeyEntry[] FieldKeys = { };
}";
            var result = FieldKeyMapScan.Scan(handlers, dispatch, inventory);
            var alpha = result.Fields.Single(f => f.Context == "Alpha");
            Assert.Contains(alpha.Claims, c => c.Binding.Display == "Ctrl+Up");
            Assert.Empty(FieldKeyMapScan.DeclaredNotHandled(result));
            Assert.Empty(FieldKeyMapScan.HandledNotDeclared(result));
        }
    }
}
