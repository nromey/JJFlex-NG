using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #960: Windows' UI Automation for a native menu reads a label that begins
/// with a modifier word as a shortcut, so the row's Name came out EMPTY (the
/// desk read of 2026-11-18: "Shift Low Edge Up" gave an empty Name and an
/// AcceleratorKey of "Shift + Low Edge Up"). NVDA still read them; a reader
/// that reads menus through UI Automation would not. And the active filter
/// preset carried a literal check-mark character, spoken as a word, while
/// setting no checked state.
/// </summary>
public sealed class MenuLabelModifierTests
{
    // NativeMenuBar builds every native menu row. AudioMenuLayout (Radios) holds
    // the Audio menu's rows as data, where a Ctrl is a shortcut column, not a
    // label; Codex's words review checked it by hand (2026-11-19).
    private static readonly string[] Menus = { "NativeMenuBar.cs" };

    /// <summary>
    /// A literal label passed to a native-menu builder (the second argument
    /// of an <c>Add...(popup, "label", ...)</c> call), with a mnemonic
    /// ampersand allowed, that begins with a modifier word.
    /// </summary>
    private static readonly Regex ModifierLed = new(
        @"\bAdd\w*\(\s*[\w.\[\]]+\s*,\s*\$?""&?(?<word>Shift|Ctrl|Control|Alt|Win|Windows)\b",
        RegexOptions.CultureInvariant);

    private static string Source(string file)
    {
        string path = Directory.GetFiles(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf"), file, SearchOption.AllDirectories).Single();
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_scan_sees_a_modifier_led_label()
    {
        // Positive controls: the shape that hid four names, with and without a
        // mnemonic, and the neighbours that must not match.
        Assert.Matches(ModifierLed, "AddWired(parent, \"Shift Low Edge Up\", () =>");
        Assert.Matches(ModifierLed, "AddChecked(sub, \"&Ctrl tones\", () => x, () => y);");
        Assert.DoesNotMatch(ModifierLed, "AddWired(parent, \"Move Low Edge Up\", () =>");
        Assert.DoesNotMatch(ModifierLed, "AddWired(antSub, \"Start tune\\tCtrl+T\", () =>");
        Assert.DoesNotMatch(ModifierLed, "AddWired(parent, \"Shifty\", () =>");
    }

    [Fact]
    public void No_native_menu_label_begins_with_a_modifier_word()
    {
        foreach (string file in Menus)
        {
            var hits = ModifierLed.Matches(Source(file)).Select(m => m.Value).ToList();
            Assert.True(hits.Count == 0, file + ": " + string.Join(" | ", hits));
        }
    }

    [Fact]
    public void The_four_edge_items_are_named_by_what_they_do()
    {
        string menu = Source("NativeMenuBar.cs");
        foreach (string label in new[] { "Move Low Edge Up", "Move Low Edge Down", "Move High Edge Up", "Move High Edge Down" })
            Assert.Contains("AddWired(parent, \"" + label + "\", () =>", menu);
    }

    [Fact]
    public void The_active_filter_preset_is_checked_natively_and_carries_no_check_mark_in_its_label()
    {
        string menu = Source("NativeMenuBar.cs");
        int at = menu.IndexOf("private void BuildFilterItems(IntPtr parent)", StringComparison.Ordinal);
        int end = menu.IndexOf("\n    }\n", at, StringComparison.Ordinal);
        if (end < 0) end = menu.IndexOf("\r\n    }\r\n", at, StringComparison.Ordinal);
        string filter = menu.Substring(at, end - at);
        Assert.DoesNotContain("\\u2713", filter);
        Assert.DoesNotContain("✓", filter);
        Assert.Contains("AddRadioChecked(parent, preset.FormatForDisplay(), () =>", filter);
        Assert.Contains("FilterPresets.RowIsActive(mode, Rig.Mode, Rig.FilterLow, Rig.FilterHigh, index)", filter);   // repair round 2: the live mode
    }
}
