using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #346, ruled by Noel 2026-11-11 19:40 (J15, option b) and 19:44: in the list
/// S opens, leaving with a changed step list ASKS before dropping it, from
/// every way out; Yes drops and closes, No keeps the window open. Enter in an
/// EMPTY new-step box presses OK; with something typed it adds the step.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> No window is constructed: the dialog is
/// <see cref="RuntimeHelpers.GetUninitializedObject"/> with its two lists
/// planted, the question is answered by a stub, and every test asserts the
/// thread is MTA, where WPF refuses to build any element. The OnClosing
/// override that calls <c>KeepOpenOnClose</c> is pinned from the source.
/// </remarks>
public sealed class StepListCloseAskTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static TuningStepsDialog Dialog(bool coarseChanged, bool fineChanged, Func<bool> answer, out List<string> asked)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var d = (TuningStepsDialog)RuntimeHelpers.GetUninitializedObject(typeof(TuningStepsDialog));
        var listType = typeof(TuningStepsDialog).GetNestedType("EditedList", BindingFlags.NonPublic);
        Assert.True(listType != null, "production type TuningStepsDialog.EditedList is missing");
        foreach (var (field, changed) in new[] { ("_coarse", coarseChanged), ("_fine", fineChanged) })
        {
            object list = Activator.CreateInstance(listType!)!;
            listType!.GetField("Changed")!.SetValue(list, changed);
            var f = typeof(TuningStepsDialog).GetField(field, Any);
            Assert.True(f != null, "production field TuningStepsDialog." + field + " is missing");
            f!.SetValue(d, list);
        }
        var log = new List<string>();
        asked = log;
        d.AskToDropChanges = () => { log.Add("asked"); return answer(); };
        return d;
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Closing_with_a_changed_list_asks_and_No_keeps_the_window_open(bool coarse, bool fine)
    {
        var d = Dialog(coarse, fine, () => false, out var asked);
        Assert.True(d.KeepOpenOnClose(false));         // Cancel or Escape: DialogResult false
        Assert.Single(asked);
    }

    [Fact]
    public void Yes_drops_the_changes_and_lets_the_window_close_without_asking_again()
    {
        var d = Dialog(true, false, () => true, out var asked);
        Assert.False(d.KeepOpenOnClose(false));
        Assert.False(d.KeepOpenOnClose(false));        // the close that follows the answer
        Assert.Single(asked);
    }

    [Fact]
    public void The_title_bar_close_asks_too()
    {
        var d = Dialog(true, true, () => false, out var asked);
        Assert.True(d.KeepOpenOnClose(null));          // Alt+F4 or the title bar: no DialogResult
        Assert.Single(asked);
    }

    [Fact]
    public void One_press_asks_once_though_the_close_arrives_twice()
    {
        // CloseWithResult assigns DialogResult (one close) and then calls
        // Close (a second): the operator answered No once.
        var d = Dialog(true, false, () => false, out var asked);
        Assert.True(d.KeepOpenOnClose(false));
        Assert.True(d.KeepOpenOnClose(false));
        Assert.Single(asked);
    }

    [Fact]
    public void An_unchanged_list_closes_at_once_and_OK_never_asks()
    {
        var d = Dialog(false, false, () => false, out var asked);
        Assert.False(d.KeepOpenOnClose(false));
        var ok = Dialog(true, true, () => false, out var askedOk);
        Assert.False(ok.KeepOpenOnClose(true));        // OK keeps the edits: nothing to drop
        Assert.Empty(asked);
        Assert.Empty(askedOk);
    }

    [Fact]
    public void The_rule_on_its_own()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(TuningStepsDialog.AsksBeforeClosing(false, true, false, false));
        Assert.True(TuningStepsDialog.AsksBeforeClosing(null, false, true, false));
        Assert.False(TuningStepsDialog.AsksBeforeClosing(true, true, true, false));
        Assert.False(TuningStepsDialog.AsksBeforeClosing(false, false, false, false));
        Assert.False(TuningStepsDialog.AsksBeforeClosing(false, true, true, true));
    }

    [Fact]
    public void Every_way_out_goes_through_the_one_ask()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string src = File.ReadAllText(Path.Combine(Root(), "JJFlexWpf", "Dialogs", "TuningStepsDialog.xaml.cs")).Replace("\r\n", "\n");
        Assert.Contains("protected override void OnClosing(CancelEventArgs e)\n        {\n            if (!e.Cancel && KeepOpenOnClose(DialogResult))", src);
        Assert.Contains("AskToDropChanges = AskToDrop;", src);
        // Cancel still closes through CloseWithResult, which reaches OnClosing.
        Assert.Contains("private void CancelButton_Click(object sender, RoutedEventArgs e)\n        {\n            CloseWithResult(false);", src);
    }

    // ── Enter in the new-step box ──

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("250 Hz", false)]
    [InlineData("x", false)]
    public void Enter_in_an_empty_box_presses_OK_and_with_text_adds(string? typed, bool pressesOk)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal(pressesOk, TuningStepsDialog.EnterPressesOk(typed));
    }

    // ── Remove names the step ──

    [Fact]
    public void Remove_names_the_selected_step_and_says_the_plain_caption_with_none()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Lexicon.Load(Lexicon.Partitions);
        string named = TuningStepsDialog.RemoveCaption(true, "250 Hz");
        Assert.Contains("250 Hz", named);
        Assert.NotEqual(Lexicon.Get("settings.tuning.steps_list_remove_coarse"), named);
        Assert.Contains("2 kHz", TuningStepsDialog.RemoveCaption(false, "2 kHz"));
        Assert.Equal(Lexicon.Get("settings.tuning.steps_list_remove_fine"), TuningStepsDialog.RemoveCaption(false, null));
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "repository root not found above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
