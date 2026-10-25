using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #753, ruled by Noel 2026-10-17 at the radio: Mode and Mute join the
    /// Modern Home slice row, readable by arrowing and changeable in place.
    /// Mute was readable in Modern only by CHANGING it - the M universal key
    /// toggles - so learning the state cost two changes. The key-by-key
    /// reconciliation of each field's handler against the inventory is
    /// <see cref="FieldKeyMapTests"/>; this holds the rest from source: the
    /// fields exist in both setups, the Modern dispatch routes them, the
    /// display writes Mode, and Mode changes go through the existing
    /// mode-change path and never a write of their own.
    /// </summary>
    public sealed class HomeModeAndMuteFieldTests
    {
        private static string Source(string file) =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), file));

        private static string Body(string source, string signature)
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

        [Fact]
        public void Modern_carries_Mode_and_Mute_and_Classic_carries_Mode()
        {
            string main = Source("JJFlexWpf/MainWindow.xaml.cs");
            string modern = Body(main, "private void SetupFreqoutModern()");
            string classic = Body(main, "private void SetupFreqoutClassic()");

            Assert.Contains("new FrequencyDisplay.DisplayField(\"Mode\"", modern);
            Assert.Contains("new FrequencyDisplay.DisplayField(\"Mute\"", modern);
            Assert.Contains("HelpItemsFor(\"Mode\", modern: true)", modern);
            Assert.Contains("HelpItemsFor(\"Mute\", modern: true)", modern);
            Assert.Contains("new FrequencyDisplay.DisplayField(\"Mode\"", classic);

            // After S Meter, so the Freq to S Meter path is uninterrupted.
            Assert.True(modern.IndexOf("DisplayField(\"SMeter\"", StringComparison.Ordinal)
                < modern.IndexOf("DisplayField(\"Mode\"", StringComparison.Ordinal));
        }

        [Fact]
        public void The_Modern_dispatch_routes_both_fields_to_their_handlers()
        {
            string dispatch = Body(Source("JJFlexWpf/MainWindow.xaml.cs"),
                "private void FreqOut_FieldKeyDown(FrequencyDisplay.DisplayField field, System.Windows.Input.KeyEventArgs e)");
            int modernAt = dispatch.IndexOf("if (ActiveUIMode == UIMode.Modern)", StringComparison.Ordinal);
            int classicAt = dispatch.IndexOf("// Classic mode: full field routing", StringComparison.Ordinal);
            Assert.True(modernAt >= 0 && classicAt > modernAt, "the Modern dispatch block was not found");
            string modern = dispatch.Substring(modernAt, classicAt - modernAt);

            Assert.Contains("case \"Mode\":", modern);
            Assert.Contains("_freqOutHandlers.AdjustMode(field, e);", modern);
            Assert.Contains("case \"Mute\":", modern);
            Assert.Contains("_freqOutHandlers.AdjustMute(field, e);", modern);
        }

        [Fact]
        public void The_Mode_field_shows_the_mode_and_changes_it_through_CycleMode()
        {
            string main = Source("JJFlexWpf/MainWindow.xaml.cs");
            Assert.Contains("FreqOut.Write(\"Mode\", RigControl.Mode", main);

            string handler = Body(Source("JJFlexWpf/FreqOutHandlers.cs"),
                "public void AdjustMode(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            Assert.Contains("_window.CycleMode(1);", handler);
            Assert.Contains("_window.CycleMode(-1);", handler);
            // Never a raw write: the existing path owns the mode list and the
            // spoken answer.
            Assert.DoesNotContain(".Mode =", handler);
            Assert.DoesNotContain("SendCommand", handler);

            string display = Source("JJFlexWpf/Controls/FrequencyDisplay.xaml.cs");
            Assert.Contains("\"Mode\" => Radios.Lexicon", display);
        }

        [Fact]
        public void The_inventory_no_longer_calls_Mute_Classic_only()
        {
            string inventory = Source("JJFlexWpf/KeyInventory.cs");
            Assert.DoesNotContain("Mute field (Classic only)", inventory);
            Assert.True(inventory.Split('\n').Count(l => l.Contains("new(\"Mode\", \"Mode field\"", StringComparison.Ordinal)) >= 2,
                "the Mode field declares fewer than two key rows");
        }
    }
}
