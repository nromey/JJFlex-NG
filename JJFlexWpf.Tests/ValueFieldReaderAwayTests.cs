using System;
using System.IO;
using JJFlexWpf.Controls;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A Home value field's accessible name trails its number while it is focused
/// (renaming a focused control is spoken unprompted), and is brought up to date
/// on focus entry. A native menu never moves WPF keyboard focus, so focus
/// entry never came, and the reader read the old number when the menu closed.
/// The name is now brought up to date while the menu holds the reader.
/// </summary>
/// <remarks>
/// <b>Windowless and silent.</b> MTA thread throughout. The bookkeeping is run
/// with a stand-in field; the control and the shell's two calls are read from
/// source. No control is constructed.
/// </remarks>
[Collection(nameof(ValueFieldReaderAwayTests))]
public sealed class ValueFieldReaderAwayTests : IDisposable
{
    private sealed class Field : ValueFieldControl.ReaderAway.IStaleName
    {
        public int Refreshes;
        public void RefreshStaleName() => Refreshes++;
    }

    public ValueFieldReaderAwayTests() => ValueFieldControl.ReaderAway.Reset();

    public void Dispose() => ValueFieldControl.ReaderAway.Reset();

    [Fact]
    public void A_trailing_name_is_brought_up_to_date_once_the_menu_holds_the_reader()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var field = new Field();
        ValueFieldControl.ReaderAway.Stale(field);

        ValueFieldControl.ReaderAway.MenuStarted();
        // Not at the loop's first instant: the reader may still be on the field.
        Assert.Equal(0, field.Refreshes);
        Assert.False(ValueFieldControl.ReaderAway.RenameWhileFocused);

        ValueFieldControl.ReaderAway.Settled();
        Assert.Equal(1, field.Refreshes);
        Assert.True(ValueFieldControl.ReaderAway.RenameWhileFocused);

        // Once is enough; a second settle has nothing trailing to refresh.
        ValueFieldControl.ReaderAway.Settled();
        Assert.Equal(1, field.Refreshes);
    }

    [Fact]
    public void After_the_menu_closes_a_focused_field_goes_back_to_visual_only()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        ValueFieldControl.ReaderAway.MenuStarted();
        ValueFieldControl.ReaderAway.Settled();
        Assert.True(ValueFieldControl.ReaderAway.RenameWhileFocused);

        ValueFieldControl.ReaderAway.MenuEnded();
        Assert.False(ValueFieldControl.ReaderAway.RenameWhileFocused);
    }

    [Fact]
    public void A_name_already_brought_up_to_date_is_not_renamed_again()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var field = new Field();
        ValueFieldControl.ReaderAway.Stale(field);
        ValueFieldControl.ReaderAway.Fresh(field);

        ValueFieldControl.ReaderAway.MenuStarted();
        ValueFieldControl.ReaderAway.Settled();
        Assert.Equal(0, field.Refreshes);
    }

    [Fact]
    public void A_short_menu_brings_the_name_up_to_date_as_it_ends_and_a_late_settle_renames_nothing_more()
    {
        // Until repair one this asserted no refresh at all after a short menu,
        // which pinned the stale name Codex found as the desired behaviour.
        NoiseReductionPerChannelTests.AssertWindowless();
        var field = new Field();
        ValueFieldControl.ReaderAway.Stale(field);
        ValueFieldControl.ReaderAway.MenuStarted();
        ValueFieldControl.ReaderAway.MenuEnded();
        Assert.Equal(1, field.Refreshes);   // at the loop's end, while the reader is still in the menu

        // The reader is back on the field: a rename now would be spoken.
        ValueFieldControl.ReaderAway.Settled();
        Assert.Equal(1, field.Refreshes);
        Assert.False(ValueFieldControl.ReaderAway.RenameWhileFocused);
    }

    [Fact]
    public void With_no_menu_open_a_menus_end_renames_nothing()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var field = new Field();
        ValueFieldControl.ReaderAway.Stale(field);
        ValueFieldControl.ReaderAway.MenuEnded();
        Assert.Equal(0, field.Refreshes);
    }

    /// <summary>
    /// A focused Home field as the control runs it: visual only while focused
    /// unless a menu holds the reader; the name otherwise; and a trailing
    /// name refreshed when ReaderAway says so.
    /// </summary>
    private sealed class FocusedField : ValueFieldControl.ReaderAway.IStaleName
    {
        public int Value;
        public string Name;
        public FocusedField(int value) { Value = value; Name = "Volume " + value; }
        public void Set(int value)
        {
            Value = value;
            if (!ValueFieldControl.ReaderAway.RenameWhileFocused) ValueFieldControl.ReaderAway.Stale(this);   // UpdateVisual
            else { Name = "Volume " + value; ValueFieldControl.ReaderAway.Fresh(this); }                     // UpdateDisplay
        }
        public void RefreshStaleName() { Name = "Volume " + Value; ValueFieldControl.ReaderAway.Fresh(this); }
    }

    [Fact]
    public void After_a_quick_open_and_cancel_the_name_carries_the_current_number()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var field = new FocusedField(50);
        field.Set(55);                                       // an arrow step, or the radio's echo
        Assert.Equal("Volume 50", field.Name);               // trailing, as designed
        ValueFieldControl.ReaderAway.MenuStarted();
        ValueFieldControl.ReaderAway.MenuEnded();            // cancelled before the settle
        Assert.Equal("Volume " + field.Value, field.Name);
    }

    [Fact]
    public void After_a_menu_held_past_the_settle_the_name_carries_the_current_number()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var field = new FocusedField(50);
        field.Set(55);
        ValueFieldControl.ReaderAway.MenuStarted();
        ValueFieldControl.ReaderAway.Settled();
        field.Set(60);                                       // a value arriving while the menu is open
        ValueFieldControl.ReaderAway.MenuEnded();
        Assert.Equal("Volume " + field.Value, field.Name);
    }

    [Fact]
    public void The_control_renames_while_a_menu_holds_the_reader_and_marks_every_visual_only_update()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "Controls", "ValueFieldControl.xaml.cs"));
        Assert.Contains("if (IsKeyboardFocusWithin && !ReaderAway.RenameWhileFocused) UpdateVisual();", src);
        Assert.Contains("ReaderAway.Stale(this);", MethodBody(src, "private void UpdateVisual("));
        Assert.Contains("ReaderAway.Fresh(this);", MethodBody(src, "private void UpdateDisplay("));
    }

    [Fact]
    public void The_shell_reports_both_ends_of_a_native_menu_loop()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string shell = File.ReadAllText(Path.Combine(Repo(), "BridgeForm.vb"));
        int enter = shell.IndexOf("If m.Msg = WM_ENTERMENULOOP Then", StringComparison.Ordinal);
        int exit = shell.IndexOf("If m.Msg = WM_EXITMENULOOP AndAlso _inNativeMenuLoop Then", StringComparison.Ordinal);
        Assert.True(enter >= 0 && exit > enter, "the shell's menu-loop handling was not found");
        Assert.Contains("ValueFieldControl.NativeMenuLoopStarted()", shell.Substring(enter, exit - enter));
        int exitEnd = shell.IndexOf("End If", exit, StringComparison.Ordinal);
        string exitBlock = shell.Substring(exit, exitEnd - exit);
        Assert.Contains("ValueFieldControl.NativeMenuLoopEnded()", exitBlock);
        // The short menu's refresh must land before the shell hands the exit
        // on and gives focus back, while the reader is still in the menu.
        int ended = exitBlock.IndexOf("NativeMenuLoopEnded()", StringComparison.Ordinal);
        Assert.True(ended < exitBlock.IndexOf("MyBase.WndProc(m)", StringComparison.Ordinal)
            && ended < exitBlock.IndexOf("_elementHost?.Focus()", StringComparison.Ordinal),
            "the menu's end must be reported before the shell passes it on and restores focus");
    }

    [Fact]
    public void The_settle_timer_is_started_by_the_menus_start_and_its_tick_settles()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "Controls", "ValueFieldControl.xaml.cs"));
        Assert.Contains("s_settle.Start();", MethodBody(src, "public static void NativeMenuLoopStarted("));
        Assert.Contains("ReaderAway.Settled();", MethodBody(src, "private static System.Windows.Threading.DispatcherTimer CreateSettleTimer("));
    }

    private static string MethodBody(string source, string signature)
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
        throw new InvalidOperationException("unbalanced braces after " + signature);
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
