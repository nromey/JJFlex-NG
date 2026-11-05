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

    /// <summary>
    /// The list's own decision, fed a whole held key: one press under NVDA's
    /// auto-repeat, one under JAWS's down/up pairs, and a release then a later
    /// press presses again (Codex's check of #774, blocker 1: a held Delete
    /// removed item after item).
    /// </summary>
    [Fact]
    public void A_held_Delete_presses_once_under_either_reader_and_a_later_press_presses_again()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        bool D(Radios.DeleteKeyGate g, bool repeat, long t) =>
            DeleteKeyRemoves.Decide(g, Key.Delete, ModifierKeys.None, repeat, fromTextEntry: false,
                selectedIndex: 0, buttonPresent: true, buttonEnabled: true, buttonVisible: true, nowMs: t);

        var nvda = new Radios.DeleteKeyGate(750);
        int pressed = D(nvda, false, 0) ? 1 : 0;
        for (long t = 500; t < 3000; t += 30) if (D(nvda, true, t)) pressed++;
        nvda.Up(3000);
        Assert.Equal(1, pressed);
        Assert.True(D(nvda, false, 4000));

        var jaws = new Radios.DeleteKeyGate(750);
        pressed = D(jaws, false, 0) ? 1 : 0;
        jaws.Up(5);
        for (long t = 512; t < 3000; t += 250) { if (D(jaws, false, t)) pressed++; jaws.Up(t + 5); }
        Assert.Equal(1, pressed);
        Assert.True(D(jaws, false, 4500));
    }

    /// <summary>
    /// Repair round 2 (Codex's re-check, follow-up): a deliberate press on
    /// ANOTHER item, after moving to it with an arrow, is a new intent and
    /// presses, even inside the gap; Delete's own JAWS pairs still do not.
    /// </summary>
    [Fact]
    public void A_press_after_moving_to_another_item_presses_and_a_held_key_still_does_not()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        bool D(Radios.DeleteKeyGate g, Key key, bool repeat, long t) =>
            DeleteKeyRemoves.Decide(g, key, ModifierKeys.None, repeat, fromTextEntry: false,
                selectedIndex: 0, buttonPresent: true, buttonEnabled: true, buttonVisible: true, nowMs: t);

        var g = new Radios.DeleteKeyGate(750);
        Assert.True(D(g, Key.Delete, false, 0));
        g.Up(80);
        Assert.False(D(g, Key.Down, false, 150));       // to the next item
        Assert.True(D(g, Key.Delete, false, 300));      // a new press, inside the gap

        var jaws = new Radios.DeleteKeyGate(750);
        Assert.True(D(jaws, Key.Delete, false, 0));
        jaws.Up(5);
        Assert.False(D(jaws, Key.Delete, false, 512));  // the same hold
    }

    /// <summary>
    /// Repair round 3 (ruled by Noel 2026-11-04: a click counts as a new
    /// intent only once Delete has really been released). Codex's replay
    /// through the WPF list's own decisions: a press, the first JAWS pair's
    /// synthetic up, a click on the list while the key is still held, and the
    /// hold's next pair, which is the same hold. Controls: round two's press
    /// after an arrow still presses, and a click after a real release does.
    /// </summary>
    [Fact]
    public void A_click_during_a_jaws_hold_does_not_make_its_next_pair_a_press()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        bool D(Radios.DeleteKeyGate g, Key key, long t) =>
            DeleteKeyRemoves.Decide(g, key, ModifierKeys.None, false, fromTextEntry: false,
                selectedIndex: 0, buttonPresent: true, buttonEnabled: true, buttonVisible: true, nowMs: t);

        var jaws = new Radios.DeleteKeyGate(750);
        Assert.True(D(jaws, Key.Delete, 0));
        jaws.Up(5);
        DeleteKeyRemoves.Clicked(jaws, 200);
        Assert.False(D(jaws, Key.Delete, 512));
        jaws.Up(517);
        Assert.False(D(jaws, Key.Delete, 762));

        var arrow = new Radios.DeleteKeyGate(750);
        Assert.True(D(arrow, Key.Delete, 0));
        arrow.Up(80);
        Assert.False(D(arrow, Key.Down, 150));
        Assert.True(D(arrow, Key.Delete, 300));

        var released = new Radios.DeleteKeyGate(750);
        Assert.True(D(released, Key.Delete, 0));
        released.Up(80);
        DeleteKeyRemoves.Clicked(released, 900);
        Assert.True(D(released, Key.Delete, 950));
    }

    /// <summary>
    /// Shift+Delete, or Delete typed into a text box inside the list, is the
    /// same physical key: it never presses, and it never ends a hold the way
    /// another key does (Codex's round-two note on the adjacent class).
    /// </summary>
    [Fact]
    public void A_Delete_that_does_not_qualify_is_still_the_Delete_key_and_never_ends_a_hold()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var g = new Radios.DeleteKeyGate(750);
        Assert.True(DeleteKeyRemoves.Decide(g, Key.Delete, ModifierKeys.None, false, false, 0, true, true, true, 0));
        g.Up(5);
        Assert.False(DeleteKeyRemoves.Decide(g, Key.Delete, ModifierKeys.Shift, false, false, 0, true, true, true, 200));
        g.Up(205);
        Assert.False(DeleteKeyRemoves.Decide(g, Key.Delete, ModifierKeys.None, false, false, 0, true, true, true, 450));
    }

    /// <summary>
    /// Codex's third re-check (repair round 4), through the list's own
    /// decision with the key read the way the list reads it: a press, the
    /// first JAWS pair's synthetic up at 5, a modifier at 200 while Delete is
    /// still physically held, then the hold's next pair at 512. Alt arrives
    /// as Key.System with the real key in SystemKey. Controls: after a real
    /// release a modifier then a press presses, and an arrow after an up
    /// still makes a prompt press a press.
    /// </summary>
    [Theory]
    [InlineData(Key.LeftShift, Key.None, ModifierKeys.Shift)]
    [InlineData(Key.RightShift, Key.None, ModifierKeys.Shift)]
    [InlineData(Key.LeftCtrl, Key.None, ModifierKeys.Control)]
    [InlineData(Key.RightCtrl, Key.None, ModifierKeys.Control)]
    [InlineData(Key.System, Key.LeftAlt, ModifierKeys.Alt)]
    [InlineData(Key.System, Key.RightAlt, ModifierKeys.Alt)]
    [InlineData(Key.LWin, Key.None, ModifierKeys.Windows)]
    [InlineData(Key.Insert, Key.None, ModifierKeys.None)]
    [InlineData(Key.CapsLock, Key.None, ModifierKeys.None)]
    public void A_modifier_tapped_during_a_jaws_hold_does_not_make_its_next_pair_a_press(Key key, Key systemKey, ModifierKeys mods)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var g = new Radios.DeleteKeyGate(750);
        Assert.True(Event(g, Key.Delete, Key.None, ModifierKeys.None, 0));
        g.Up(5);
        Assert.False(Event(g, key, systemKey, mods, 200));
        Assert.False(Event(g, Key.Delete, Key.None, ModifierKeys.None, 512));
        g.Up(517);
        Assert.False(Event(g, Key.Delete, Key.None, ModifierKeys.None, 762));

        var released = new Radios.DeleteKeyGate(750);
        Assert.True(Event(released, Key.Delete, Key.None, ModifierKeys.None, 0));
        released.Up(80);
        Assert.False(Event(released, key, systemKey, mods, 900));
        Assert.True(Event(released, Key.Delete, Key.None, ModifierKeys.None, 950));

        var arrow = new Radios.DeleteKeyGate(750);
        Assert.True(Event(arrow, Key.Delete, Key.None, ModifierKeys.None, 0));
        arrow.Up(80);
        Assert.False(Event(arrow, Key.Down, Key.None, ModifierKeys.None, 150));
        Assert.True(Event(arrow, Key.Delete, Key.None, ModifierKeys.None, 300));
    }

    /// <summary>
    /// Alt+Delete is reported as Key.System with Delete in SystemKey (repair
    /// round 4; CLAUDE.md's keyboard audit, item 7). Read for its real key it
    /// is a Delete that does not qualify, so its pairs held past the gap
    /// continue the hold and the bare pair after them is the same hold. The
    /// control: the same history with nothing between is a release.
    /// </summary>
    [Fact]
    public void Alt_Delete_is_read_as_Delete_so_its_pairs_continue_a_hold_past_the_gap()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(Key.Delete, DeleteKeyRemoves.RealKey(Key.System, Key.Delete, Key.None, Key.None));
        Assert.Equal(Key.Delete, DeleteKeyRemoves.RealKey(Key.ImeProcessed, Key.None, Key.Delete, Key.None));
        Assert.Equal(Key.Delete, DeleteKeyRemoves.RealKey(Key.Delete, Key.None, Key.None, Key.None));

        var g = new Radios.DeleteKeyGate(750);
        Assert.True(Event(g, Key.Delete, Key.None, ModifierKeys.None, 0));
        g.Up(5);
        foreach (long t in new long[] { 512, 762, 1012, 1262 })
        {
            Assert.False(Event(g, Key.System, Key.Delete, ModifierKeys.Alt, t));
            g.Up(t + 5);
        }
        Assert.False(Event(g, Key.Delete, Key.None, ModifierKeys.None, 1512));

        var quiet = new Radios.DeleteKeyGate(750);
        Assert.True(Event(quiet, Key.Delete, Key.None, ModifierKeys.None, 0));
        quiet.Up(5);
        Assert.True(Event(quiet, Key.Delete, Key.None, ModifierKeys.None, 1512));
    }

    /// <summary>
    /// The list's handlers read the real key on the down AND the up (repair
    /// round 4), so Alt+Delete is Delete in both directions.
    /// </summary>
    [Fact]
    public void Both_key_handlers_read_the_real_key()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(Root(), "JJFlexWpf", "DeleteKeyRemoves.cs"));
        Assert.Equal(2, Regex.Matches(src, @"RealKey\(e\.Key, e\.SystemKey, e\.ImeProcessedKey, e\.DeadCharProcessedKey\)").Count);
        Assert.DoesNotContain("e.Key == Key.Delete", src);
    }

    /// <summary>A key event as the list's handler decides it: its real key first.</summary>
    private static bool Event(Radios.DeleteKeyGate g, Key key, Key systemKey, ModifierKeys mods, long t) =>
        DeleteKeyRemoves.Decide(g, DeleteKeyRemoves.RealKey(key, systemKey, Key.None, Key.None), mods, false,
            fromTextEntry: false, selectedIndex: 0, buttonPresent: true, buttonEnabled: true, buttonVisible: true, nowMs: t);

    [Fact]
    public void A_Delete_that_does_not_qualify_never_presses_however_fresh()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var g = new Radios.DeleteKeyGate(750);
        Assert.False(DeleteKeyRemoves.Decide(g, Key.Delete, ModifierKeys.None, false, fromTextEntry: true,
            0, true, true, true, 0));
        Assert.False(DeleteKeyRemoves.Decide(g, Key.Delete, ModifierKeys.Shift, false, false, 0, true, true, true, 0));
        Assert.False(DeleteKeyRemoves.Decide(g, Key.Delete, ModifierKeys.None, false, false, -1, true, true, true, 5000));
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
