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
    public void A_late_settle_after_the_menu_closed_renames_nothing()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var field = new Field();
        ValueFieldControl.ReaderAway.Stale(field);
        ValueFieldControl.ReaderAway.MenuStarted();
        ValueFieldControl.ReaderAway.MenuEnded();

        // The reader is back on the field: a rename now would be spoken.
        ValueFieldControl.ReaderAway.Settled();
        Assert.Equal(0, field.Refreshes);
        Assert.False(ValueFieldControl.ReaderAway.RenameWhileFocused);
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
        Assert.Contains("ValueFieldControl.NativeMenuLoopEnded()", shell.Substring(exit, exitEnd - exit));
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
