using System;
using System.IO;
using System.Windows.Controls;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Ctrl and Shift with Up and Down tune across the main window, except where
/// the arrows change the focused thing's own value (#703 J4, ruled by Noel
/// 2026-11-09 20:40).
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> The test thread asserts it is MTA (no window can be
/// built there); controls are built unhosted on a private STA thread with no
/// presentation source, as <c>StatusTextNameTests</c> does, and the dispatcher
/// is shut down after. The live key path (the registry running ahead of the
/// focused control, through BridgeForm or the main window's preview) is the
/// harness's to press.
/// </remarks>
public sealed class TuningChordYieldTests
{
    [Fact]
    public void Only_the_four_tuning_commands_yield()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(TuningChordYield.IsTuningChord(CommandValues.TuneCoarseUp));
        Assert.True(TuningChordYield.IsTuningChord(CommandValues.TuneCoarseDown));
        Assert.True(TuningChordYield.IsTuningChord(CommandValues.TuneFineUp));
        Assert.True(TuningChordYield.IsTuningChord(CommandValues.TuneFineDown));
        Assert.False(TuningChordYield.IsTuningChord(CommandValues.BandUp));     // Alt+Up stays as it is
    }

    /// <summary>
    /// Focus on a part of a list, a text box or a combo box: the chord is left
    /// to it. Focus on a button: the chord tunes. Focus sits on the part, so
    /// the walk up the parents is what makes a list item count as the list.
    /// </summary>
    [Fact]
    public void Lists_text_boxes_and_combo_boxes_keep_the_arrows_and_a_button_does_not()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        OnSta(() =>
        {
            var list = new ListBox();
            var item = new ListBoxItem { Content = "row" };
            list.Items.Add(item);
            Assert.True(TuningChordYield.FocusOwnsArrows(item));        // the part, through its parent

            var panel = new StackPanel();
            var box = new TextBox();
            var button = new Button();
            panel.Children.Add(box);
            panel.Children.Add(button);
            Assert.True(TuningChordYield.FocusOwnsArrows(box));
            Assert.False(TuningChordYield.FocusOwnsArrows(button));     // the control: tunes
            Assert.False(TuningChordYield.FocusOwnsArrows(panel));

            Assert.True(TuningChordYield.FocusOwnsArrows(new ComboBox()));
            Assert.True(TuningChordYield.FocusOwnsArrows(new DataGrid()));
            Assert.True(TuningChordYield.FocusOwnsArrows(new TreeView()));
            Assert.True(TuningChordYield.FocusOwnsArrows(new ListView()));
            Assert.True(TuningChordYield.FocusOwnsArrows(new PasswordBox()));
            Assert.False(TuningChordYield.FocusOwnsArrows(null));
        });
    }

    /// <summary>
    /// Home's own value controls, by type: a ValueFieldControl (Shift with the
    /// arrows is its fine value step) and a CycleFieldControl. Built as
    /// uninitialized objects, so no XAML and no element tree is made; the type
    /// test is what the walk applies to each element.
    /// </summary>
    [Fact]
    public void Homes_value_and_cycle_fields_keep_the_arrows()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var value = (System.Windows.DependencyObject)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(Controls.ValueFieldControl));
        var cycle = (System.Windows.DependencyObject)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(Controls.CycleFieldControl));
        Assert.True(TuningChordYield.OwnsArrows(value));
        Assert.True(TuningChordYield.OwnsArrows(cycle));
    }

    /// <summary>
    /// From source: the four commands carry their default chords at Radio
    /// scope, run without the generic no-radio guard so they say the slice
    /// row's sentence, and DoCommand asks the yield before that guard and
    /// before it marks the key handled.
    /// </summary>
    [Fact]
    public void The_chords_are_bound_and_DoCommand_asks_before_it_consumes_the_key()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string keys = Read("JJFlexWpf", "KeyCommands.cs");
        Assert.Contains("new(Keys.Up | Keys.Control, CommandValues.TuneCoarseUp, KeyScope.Radio),", keys, StringComparison.Ordinal);
        Assert.Contains("new(Keys.Down | Keys.Control, CommandValues.TuneCoarseDown, KeyScope.Radio),", keys, StringComparison.Ordinal);
        Assert.Contains("new(Keys.Up | Keys.Shift, CommandValues.TuneFineUp, KeyScope.Radio),", keys, StringComparison.Ordinal);
        Assert.Contains("new(Keys.Down | Keys.Shift, CommandValues.TuneFineDown, KeyScope.Radio),", keys, StringComparison.Ordinal);
        Assert.DoesNotContain("TuneByStepUnboundDetail", keys, StringComparison.Ordinal);

        int dispatch = keys.IndexOf("internal bool DoCommand(Keys k, bool fromDialog)", StringComparison.Ordinal);
        int yieldAt = keys.IndexOf("if (TuningChordYield.Yields(kt.KeyDef.Id))", dispatch, StringComparison.Ordinal);
        int guard = keys.IndexOf("if (_context.GetRigControl() == null &&", dispatch, StringComparison.Ordinal);
        int consumed = keys.IndexOf("rv = true;", dispatch, StringComparison.Ordinal);
        Assert.True(dispatch > 0 && yieldAt > dispatch && guard > yieldAt && consumed > guard,
            "the yield must come before the no-radio guard and before the key is marked handled");

        string say = keys.Substring(keys.IndexOf("private void TuneFromCommand(", StringComparison.Ordinal), 600);
        Assert.Contains("connect.command_needs_radio", say, StringComparison.Ordinal);
    }

    private static void OnSta(Action body)
    {
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The control test did not finish.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(parts))).Replace("\r\n", "\n");
    }
}
