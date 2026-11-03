using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Delete removes the selected item in every list that has a remove button
/// (#774; Noel, 2026-10-19: "Any other functions where we remove items should
/// also be removable with del").
/// </summary>
/// <remarks>
/// <b>Windowless.</b> The rule and the landing are pure statics on
/// <see cref="DeleteKeyRemoves"/>; the sweep reads source. Every test asserts
/// an MTA thread, where WPF refuses to build any element. What this does NOT
/// prove is a real Delete press on a real list: that is a harness step.
/// </remarks>
public sealed class DeleteKeyRemovesTests
{
    [Fact]
    public void Bare_Delete_on_a_selected_item_presses_an_enabled_visible_button()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(DeleteKeyRemoves.Presses(Key.Delete, ModifierKeys.None, fromTextEntry: false, selectedIndex: 2,
            buttonPresent: true, buttonEnabled: true, buttonVisible: true));
    }

    [Fact]
    public void Nothing_else_presses_it()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        bool P(Key k = Key.Delete, ModifierKeys m = ModifierKeys.None, bool text = false, int sel = 0,
               bool present = true, bool enabled = true, bool visible = true) =>
            DeleteKeyRemoves.Presses(k, m, text, sel, present, enabled, visible);

        Assert.False(P(k: Key.Back));
        Assert.False(P(m: ModifierKeys.Shift));          // Shift+Delete is not this
        Assert.False(P(m: ModifierKeys.Control));
        Assert.False(P(text: true));                    // a text box keeps Delete for text
        Assert.False(P(sel: -1));                       // nothing selected, nothing to remove
        Assert.False(P(present: false));
        Assert.False(P(enabled: false));                // never a quieter way round a refusal
        Assert.False(P(visible: false));
    }

    [Theory]
    [InlineData(2, 5, 4, 2)]    // the next item slides into the removed one's place
    [InlineData(4, 5, 4, 3)]    // the last was removed: the previous one
    [InlineData(0, 1, 0, -1)]   // emptied: the list itself keeps focus
    [InlineData(1, 5, 5, -1)]   // nothing went (No, or refused): leave it alone
    public void Focus_lands_on_the_next_item_or_the_previous_never_nowhere(int at, int before, int after, int landing)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(landing, DeleteKeyRemoves.LandingIndex(at, before, after));
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>The lists wired, by the file that wires them.</summary>
    private static readonly string[] Wired =
    {
        "CWMessageUpdateDialog", "FilterPresetEditorDialog", "ListerDialog", "ManageGroupsDialog",
        "MemoriesDialog", "ProfileDialog", "TNFDialog", "FixerPastRunsDialog", "SignalCapturesDialog",
    };

    /// <summary>Remove-style buttons that are not "remove the item I am on", or already answer Delete.</summary>
    private static readonly Dictionary<string, string> NotWired = new()
    {
        ["MetersPanel"] = "already handles Delete itself",
        ["SmartLinkAccountDialog"] = "already handles Delete itself",
        ["RemoveRadioDialog"] = "no list: it IS the confirmation for one radio",
        ["SettingsDialog"] = "bulk actions only (forget what has been learned, delete loose logs, delete sent crash reports)",
    };

    [Fact]
    public void Every_list_with_a_remove_button_is_wired_or_named_with_its_reason()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string dir = Path.Combine(Root(), "JJFlexWpf");

        // Positive control and the rule: every file wiring the behaviour is in the list...
        var wiring = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("DeleteKeyRemoves.SetButton(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f).Split('.')[0])
            .ToHashSet(StringComparer.Ordinal);
        foreach (string w in Wired) Assert.Contains(w, wiring);

        // ...and every XAML Remove, Delete or Forget button sits in a file that
        // is wired or says why it is not. A new one fails here until decided.
        var xamlRemove = new Regex("Content=\"_?(Remove|Delete|Forget)", RegexOptions.CultureInvariant);
        var found = Directory.GetFiles(dir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => xamlRemove.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToList();
        Assert.True(found.Count >= 10, "the sweep found only " + found.Count + " files; it is no longer seeing the tree");
        foreach (string f in found)
            Assert.True(wiring.Contains(f) || NotWired.ContainsKey(f),
                f + " has a Remove, Delete or Forget button and neither wires Delete to it nor says why not (#774)");
    }
}
