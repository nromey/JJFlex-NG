using System;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Noel's ruling, 2026-10-25 20:51: "I'd allow it to work anywhere if it
    /// applies i.e. if it's in the correct tuning mode. In classic mode, it'd
    /// say shift s does nothing." So Shift+S speaks both step sizes from any
    /// Home field in Modern tuning, and in Classic falls to the existing
    /// dead-key answer. MenuShortcutAgreementTests holds the menu's
    /// "Speak Current Step, Shift+S" to this same branch.
    /// </summary>
    /// <remarks>
    /// From source and the field-map scan, because the handlers need a WPF key
    /// event. Pressing Shift+S on S Meter in both tuning modes is the bench's.
    /// </remarks>
    public sealed class HomeShiftSStepsTests
    {
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
        public void Shift_S_is_a_universal_Home_key_in_Modern_tuning_only()
        {
            string src = FieldKeyMapScan.ReadSource(FieldKeyMapScan.HandlersFile);
            string universal = Body(src, "private bool TryHandleUniversalHomeKey(KeyEventArgs e, bool numberEntryActive = false)");
            const string branch = "if (key == Key.S && Keyboard.Modifiers == ModifierKeys.Shift && _window.FreqOut.IsModernMode)";
            int at = universal.IndexOf(branch, StringComparison.Ordinal);
            Assert.True(at >= 0, "Shift+S is no longer a universal Home key gated on Modern tuning");
            Assert.Contains("SpeakCurrentStepFromMenu();", universal.Substring(at, 200));

            // One home: the Modern Frequency field keeps no copy of its own.
            string modern = Body(src, "public void AdjustFreqModern(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            Assert.DoesNotContain("key == Key.S && Keyboard.Modifiers == ModifierKeys.Shift", modern);
            Assert.Contains("TryHandleUniversalHomeKey(e, numberEntryActive: _inQuickType);", modern);
        }

        [Fact]
        public void In_Classic_tuning_Shift_S_reaches_the_dead_key_answer()
        {
            // Classic is not claimed by the branch above, so every field handler
            // goes on to AnnounceDeadHomeKey, whose answer names the chord and
            // the field ("Shift S does nothing on ..."). The scan sees the claim
            // gated, by the window's tuning mode, on every field.
            var fields = FieldKeyMapScan.ScanRepository().Fields;
            Assert.True(fields.Count >= 15);
            foreach (var f in fields)
                Assert.Contains(f.Claims, c => c.Binding.Display == "Shift+S" && c.StateGated);

            string inventory = FieldKeyMapScan.ReadSource(FieldKeyMapScan.InventoryFile);
            Assert.Contains("new(\"Home\", \"Any Home field\", \"Shift+S\"", inventory);
            // The Frequency field keeps its own row too (orchestrator, 2026-10-25): its help reads the step values beside it.
            Assert.Contains("new(\"Freq.Modern\", \"Frequency field (Modern tuning)\", \"Shift+S\"", inventory);
        }
    }
}
