using System;
using System.IO;
using System.Windows.Controls;
using Keys = System.Windows.Forms.Keys;
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
        int yieldAt = keys.IndexOf("if (TuningChordYield.Yields(kt.KeyDef.Id, k, TuningFocusProbe))", dispatch, StringComparison.Ordinal);
        int guard = keys.IndexOf("if (_context.GetRigControl() == null &&", dispatch, StringComparison.Ordinal);
        int consumed = keys.IndexOf("rv = true;", dispatch, StringComparison.Ordinal);
        Assert.True(dispatch > 0 && yieldAt > dispatch && guard > yieldAt && consumed > guard,
            "the yield must come before the no-radio guard and before the key is marked handled");

        string say = keys.Substring(keys.IndexOf("private void TuneFromCommand(", StringComparison.Ordinal), 600);
        Assert.Contains("connect.command_needs_radio", say, StringComparison.Ordinal);
    }

    /// <summary>
    /// #703 repair one (Codex): the exception is for the arrow chords, not for
    /// the commands. Up or Down with Ctrl, Shift or both, and no Alt, is given
    /// back; anything else a tuning command is bound to is not.
    /// </summary>
    [Fact]
    public void Only_Up_and_Down_with_Ctrl_or_Shift_are_given_back()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(TuningChordYield.IsLocalArrowChord(Keys.Up | Keys.Control));
        Assert.True(TuningChordYield.IsLocalArrowChord(Keys.Down | Keys.Control));
        Assert.True(TuningChordYield.IsLocalArrowChord(Keys.Up | Keys.Shift));
        Assert.True(TuningChordYield.IsLocalArrowChord(Keys.Down | Keys.Shift));
        Assert.True(TuningChordYield.IsLocalArrowChord(Keys.Up | Keys.Control | Keys.Shift));
        Assert.False(TuningChordYield.IsLocalArrowChord(Keys.F8 | Keys.Control | Keys.Alt));   // a key of the operator's own
        Assert.False(TuningChordYield.IsLocalArrowChord(Keys.Up | Keys.Control | Keys.Alt));
        Assert.False(TuningChordYield.IsLocalArrowChord(Keys.Left | Keys.Control));
        Assert.False(TuningChordYield.IsLocalArrowChord(Keys.Up));
    }

    /// <summary>
    /// Codex's blocker, through the real <see cref="KeyCommands.DoCommand(Keys, bool)"/>:
    /// focus on a Home value field, Coarse tune up moved to Ctrl+Alt+F8 the
    /// way the Hotkey Editor leaves the table, no radio. The default Ctrl+Down
    /// (Coarse tune down) is left to the value field: not handled, nothing
    /// said. Ctrl+Alt+F8 at the same field runs its command, which with no
    /// radio says the slice row's sentence. The control: with focus on
    /// nothing that keeps the arrows, the same Ctrl+Down runs too, so the
    /// first press is a yield and not a chord that never reached the table.
    /// </summary>
    /// <remarks>
    /// <b>Desk safety:</b> an MTA thread (asserted), where no window can be
    /// built; the value field is an uninitialized object handed to the focus
    /// probe; speech is silenced (asserted) and read back from the recorder;
    /// the settings tree is the run's throwaway one (asserted). Whether a real
    /// key reaches DoCommand on Home is the harness's to press.
    /// </remarks>
    [Fact]
    public void At_one_value_field_the_default_arrow_chord_is_left_to_it_and_a_function_key_binding_tunes()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        var verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        try
        {
            var valueField = (System.Windows.DependencyObject)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(Controls.ValueFieldControl));
            System.Windows.DependencyObject? focused = valueField;
            var commands = new KeyCommands(new KeyCommandContext(), setDefault: true) { TuningFocusProbe = () => focused };

            // Coarse tune up on a key of the operator's own, as the Hotkey Editor leaves it.
            const Keys custom = Keys.F8 | Keys.Control | Keys.Alt;
            var entry = commands.Lookup(CommandValues.TuneCoarseUp)!;
            Assert.True(commands.KeyDictionary.TryGetValue(entry.KeyDef.Key, out var onDefault) && onDefault.Remove(entry));
            entry.KeyDef.Key = custom;
            Assert.True(commands.AddToKeyDictionary(entry));
            Assert.Same(entry, commands.Lookup(custom));
            Assert.Equal(CommandValues.TuneCoarseDown, commands.Lookup(Keys.Down | Keys.Control)!.KeyDef.Id);

            bool handled = true;
            string? said = SpokenAfter(() => handled = commands.DoCommand(Keys.Down | Keys.Control, fromDialog: false));
            Assert.False(handled);            // left to the value field
            Assert.Null(said);

            said = SpokenAfter(() => handled = commands.DoCommand(custom, fromDialog: false));
            Assert.True(handled);             // the operator's key runs its command
            Assert.Equal(CommandValues.TuneCoarseUp, commands.CommandId);
            Assert.Equal(Lexicon.Get("connect.command_needs_radio"), said);

            // Control: focus on nothing that keeps the arrows, and the default chord runs.
            focused = null;
            said = SpokenAfter(() => handled = commands.DoCommand(Keys.Down | Keys.Control, fromDialog: false));
            Assert.True(handled);
            Assert.Equal(CommandValues.TuneCoarseDown, commands.CommandId);
            Assert.Equal(Lexicon.Get("connect.command_needs_radio"), said);
        }
        finally
        {
            ScreenReaderOutput.CurrentVerbosity = verbosityBefore;
        }
    }

    private static string? SpokenAfter(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        press();
        string? first = ScreenReaderOutput.RecentMessages.FirstOrDefault();
        return first == marker ? null : first;
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
