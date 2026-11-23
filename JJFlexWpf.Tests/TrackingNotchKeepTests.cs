using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using JJFlexWpf.Dialogs;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The tracking notch's keep-it choice is a checkbox: it shows the selected
/// notch's flag, and checking or unchecking it writes that flag for that
/// notch, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless.</b> The dialog is an uninitialized object
/// (<c>RuntimeHelpers.GetUninitializedObject</c>, as in
/// <c>CheckYourAudioCtrlF1Tests</c>): its constructor never runs, so no window
/// is built, shown or sourced. Its named controls are real WPF controls put
/// into its fields by reflection and never placed in a window. The thread is
/// STA because WPF elements require it. The toggle tone goes through the
/// earcon player, which this assembly's module initializer has silenced
/// (<c>Infrastructure/QuietRun</c>). No radio: the notch list is three fakes.
/// </para>
/// <para>
/// The checkbox's <c>Checked</c> and <c>Unchecked</c> are wired to the
/// dialog's own handler, as the XAML wires them. <b>What this cannot see:</b>
/// what NVDA says on Space, and the radio's reply. Those are the bench's.
/// </para>
/// </remarks>
public sealed class TrackingNotchKeepTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void TheCheckboxShowsTheSelectedNotchsFlagWithoutWritingIt()
    {
        Run(() =>
        {
            var (dialog, list, check, writes) = Dialog(new[] { false, true, false });

            list.SelectedIndex = 1;
            Invoke(dialog, "UpdateTNFProperties");
            Assert.True(check.IsChecked);

            list.SelectedIndex = 0;
            Invoke(dialog, "UpdateTNFProperties");
            Assert.False(check.IsChecked);

            // Showing a notch's flag is not the operator changing it.
            Assert.Empty(writes);
        });
    }

    [Fact]
    public void CheckingAndUncheckingWritesTheSelectedNotchsFlag()
    {
        Run(() =>
        {
            var (dialog, list, check, writes) = Dialog(new[] { false, false, false });
            list.SelectedIndex = 2;
            Invoke(dialog, "UpdateTNFProperties");

            check.IsChecked = true;
            check.IsChecked = false;

            Assert.Equal(new[] { (2, true), (2, false) }, writes.ToArray());
        });
    }

    [Fact]
    public void WithNothingSelectedTheCheckboxIsOutOfTheTabOrder()
    {
        Run(() =>
        {
            var (dialog, list, check, writes) = Dialog(Array.Empty<bool>());
            list.SelectedIndex = -1;
            Invoke(dialog, "UpdateTNFProperties");

            Assert.False(check.IsEnabled);
            Assert.False(check.IsChecked);
            Assert.Empty(writes);
        });
    }

    // ---- #773: moving the selected notch ----

    private static (TNFDialog Dialog, ListBox List, TextBox Box, List<(int, ulong)> Moves) MoveDialog()
    {
        var (dialog, list, _, _) = Dialog(new[] { false, false });
        var moves = new List<(int, ulong)>();
        // The radio's report lags the ask, as a queued write does.
        dialog.GetTNFFrequencyHz = i => 14_200_000UL + (ulong)i * 1000;
        dialog.SetTNFFrequencyHz = (i, hz) => { moves.Add((i, hz)); return hz; };
        // The app's own format, not a stand-in: a stand-in hid repair 3's B1.
        dialog.FormatTNFFrequency = ProductionFormat;
        list.SelectedIndex = 1;
        Invoke(dialog, "UpdateTNFProperties");
        var box = (TextBox)typeof(TNFDialog).GetField("FrequencyBox", Private | BindingFlags.Public)!.GetValue(dialog)!;
        return (dialog, list, box, moves);
    }

    [Fact]
    public void TheFrequencyBoxShowsTheSelectedNotchWithoutMovingIt()
    {
        Run(() =>
        {
            var (_, _, box, moves) = MoveDialog();
            Assert.Equal("14.201.000", box.Text);
            Assert.True(box.IsEnabled);
            Assert.Empty(moves);
        });
    }

    [Fact]
    public void CoarseAndFineStepsMoveTheSelectedNotchAndBuildOnTheAsk()
    {
        Run(() =>
        {
            var (dialog, _, box, moves) = MoveDialog();
            Assert.Equal(100, TNFDialog.FrequencyCoarseStepHz);
            Assert.Equal(10, TNFDialog.FrequencyFineStepHz);

            dialog.StepFrequency(1, TNFDialog.FrequencyCoarseStepHz);
            dialog.StepFrequency(1, TNFDialog.FrequencyCoarseStepHz);
            dialog.StepFrequency(1, -TNFDialog.FrequencyFineStepHz);

            Assert.Equal(new[] { (1, 14_201_100UL), (1, 14_201_200UL), (1, 14_201_190UL) }, moves.ToArray());
            Assert.Equal("14.201.190", box.Text);
        });
    }

    [Fact]
    public void ATypedFrequencyMovesTheNotchByTheTuningEntrysOwnParser()
    {
        Run(() =>
        {
            var (dialog, list, box, moves) = MoveDialog();
            box.Text = "14.2105";
            dialog.ApplyTypedFrequency(1);
            box.Text = "14,203.5";
            dialog.ApplyTypedFrequency(1);
            Assert.Equal(new[] { (1, 14_210_500UL), (1, 14_203_500UL) }, moves.ToArray());
            Assert.Equal("14.203.500", list.Items[1]);
        });
    }

    [Fact]
    public void TabbingThroughTheFrequencyBoxMovesNothing()
    {
        Run(() =>
        {
            var (dialog, _, box, moves) = MoveDialog();
            Handler(dialog, "FrequencyBox_LostFocus")(box, new RoutedEventArgs());
            Assert.Empty(moves);

            // Positive control: a typed change does go on leaving.
            box.Text = "14.2105";
            Handler(dialog, "FrequencyBox_LostFocus")(box, new RoutedEventArgs());
            Assert.Single(moves);
        });
    }

    [Fact]
    public void TextThatIsNotAFrequencyMovesNothingAndPutsTheFrequencyBack()
    {
        Run(() =>
        {
            var (dialog, _, box, moves) = MoveDialog();
            box.Text = "abc";
            dialog.ApplyTypedFrequency(1);
            Assert.Empty(moves);
            Assert.Equal("14.201.000", box.Text);
        });
    }

    [Fact]
    public void TheArrowsAreCoarseAndShiftWithThemFine()
    {
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "TNFDialog.xaml.cs"));
        int at = src.IndexOf("private void FrequencyBox_PreviewKeyDown(", StringComparison.Ordinal);
        string keys = src.Substring(at, src.IndexOf("private void FrequencyBox_LostFocus(", at, StringComparison.Ordinal) - at);
        Assert.Contains("bool fine = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;", keys);
        Assert.Contains("StepFrequency(idx, fine ? FrequencyFineStepHz : FrequencyCoarseStepHz);", keys);
        Assert.Contains("StepFrequency(idx, -(fine ? FrequencyFineStepHz : FrequencyCoarseStepHz));", keys);
        Assert.Contains("ApplyTypedFrequency(idx);", keys);
        string xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "TNFDialog.xaml"));
        Assert.Contains("PreviewKeyDown=\"FrequencyBox_PreviewKeyDown\" LostFocus=\"FrequencyBox_LostFocus\"", xaml);
    }

    /// <summary>
    /// A copy of <c>globals.FormatFreqUlong</c> (the VB app's formatter, which
    /// <c>OpenParms.FormatFreq</c> hands the radio layer), pinned to the
    /// source by <see cref="TheCopyOfTheAppsFormatterIsTheAppsFormatter"/>.
    /// </summary>
    private static string ProductionFormat(ulong freq)
    {
        string str = freq.ToString(System.Globalization.CultureInfo.InvariantCulture);
        while (str.Length < 7) str = "0" + str;
        int len = str.Length;
        return str.Substring(0, len - 6) + "." + str.Substring(len - 6, 3) + "." + str.Substring(len - 3);
    }

    [Fact]
    public void TheCopyOfTheAppsFormatterIsTheAppsFormatter()
    {
        string vb = System.IO.File.ReadAllText(System.IO.Path.Combine(Repo(), "globals.vb"));
        int at = vb.IndexOf("Friend Function FormatFreqUlong(ByVal freq As ULong) As String", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string body = vb.Substring(at, vb.IndexOf("End Function", at, StringComparison.Ordinal) - at);
        Assert.Contains("For i As Integer = 0 To 6 - str.Length", body);
        Assert.Contains("rv = str.Substring(0, len - 6) & \".\"c & str.Substring(len - 6, 3) &", body);
        Assert.Contains("\".\"c & str.Substring(len - 3)", body);
        Assert.Equal("14.201.000", ProductionFormat(14_201_000));
        Assert.Equal("0.475.000", ProductionFormat(475_000));
    }

    /// <summary>
    /// Batch 14 repair 3 (Codex's B1 on #773): every form the box shows reads
    /// back as the same frequency, so an edit of the shown text works.
    /// </summary>
    [Theory]
    [InlineData(30_000UL)]
    [InlineData(475_000UL)]
    [InlineData(1_800_000UL)]
    [InlineData(7_074_123UL)]
    [InlineData(14_201_000UL)]
    [InlineData(14_201_190UL)]
    [InlineData(54_000_000UL)]
    public void EveryFormTheBoxShowsReadsBackAsTheSameFrequency(ulong hz) =>
        Assert.Equal(hz, TNFDialog.ParseFrequencyHz(ProductionFormat(hz)));

    [Theory]
    [InlineData("14.2011", 14_201_100UL)]      // megahertz with a point
    [InlineData("14,201.1", 14_201_100UL)]     // grouped kilohertz
    [InlineData("14201", 14_201_000UL)]        // kilohertz
    [InlineData("14.201.1", 14_201_100UL)]     // the shown form, its hertz shortened: a decimal of a kilohertz
    public void TheOtherFormsReadAsTheTuningEntryReadsThem(string text, ulong hz)
    {
        Assert.Equal(hz, TNFDialog.ParseFrequencyHz(text));
    }

    [Theory]
    [InlineData("14.21.000")]
    [InlineData("14.201.0000")]
    [InlineData(".201.000")]
    [InlineData("14.2a1.000")]
    [InlineData("abc")]
    public void TextThatIsNotAFrequencyReadsAsNone(string text) =>
        Assert.Null(TNFDialog.ParseFrequencyHz(text));

    [Fact]
    public void AnInPlaceEditOfTheShownFrequencyMovesTheNotch()
    {
        Run(() =>
        {
            var (dialog, _, box, moves) = MoveDialog();
            Assert.Equal("14.201.000", box.Text);   // the shown form
            box.Text = "14.201.100";                // its last three digits replaced
            dialog.ApplyTypedFrequency(1);
            Assert.Equal(new[] { (1, 14_201_100UL) }, moves.ToArray());
            Assert.Equal("14.201.100", box.Text);
        });
    }

    // ---- #1198: Width writes only a change; #1189 item 2: a typed frequency out of range is refused ----

    private static (TNFDialog Dialog, TextBox Width, List<(int, int)> Writes) WidthDialog()
    {
        var (dialog, list, _, _) = Dialog(new[] { false, false });
        var writes = new List<(int, int)>();
        dialog.SetTNFWidth = (i, w) => writes.Add((i, w));
        list.SelectedIndex = 1;
        Invoke(dialog, "UpdateTNFProperties");
        var width = (TextBox)typeof(TNFDialog).GetField("WidthBox", Private | BindingFlags.Public)!.GetValue(dialog)!;
        return (dialog, width, writes);
    }

    [Fact]
    public void TabbingThroughAnUnchangedWidthWritesNothing()
    {
        Run(() =>
        {
            var (dialog, width, writes) = WidthDialog();
            Assert.Equal("100", width.Text);   // the radio's, shown on selection
            Handler(dialog, "WidthBox_LostFocus")(width, new RoutedEventArgs());
            Assert.Empty(writes);

            // Positive control: a changed width is written, once.
            width.Text = "150";
            Handler(dialog, "WidthBox_LostFocus")(width, new RoutedEventArgs());
            Handler(dialog, "WidthBox_LostFocus")(width, new RoutedEventArgs());
            Assert.Equal(new[] { (1, 150) }, writes.ToArray());
        });
    }

    [Fact]
    public void LeavingTheWidthAfterAnArrowWritesNothingMore()
    {
        Run(() =>
        {
            var (dialog, width, writes) = WidthDialog();
            typeof(TNFDialog).GetMethod("AdjustWidth", Private)!.Invoke(dialog, new object[] { 1, 50 });
            Assert.Equal(new[] { (1, 150) }, writes.ToArray());
            Assert.Equal("150", width.Text);
            // The radio's read-back still says 100 (a queued write lags); the
            // comparison is with what was shown, so nothing more goes out.
            Handler(dialog, "WidthBox_LostFocus")(width, new RoutedEventArgs());
            Assert.Single(writes);
        });
    }

    [Fact]
    public void WidthTextThatIsNotANumberPutsTheShownWidthBackAndWritesNothing()
    {
        Run(() =>
        {
            var (dialog, width, writes) = WidthDialog();
            width.Text = "wide";
            Handler(dialog, "WidthBox_LostFocus")(width, new RoutedEventArgs());
            Assert.Empty(writes);
            Assert.Equal("100", width.Text);
        });
    }

    // ---- #1198, the Depth half: Depth writes only a change, as Width does ----

    private static (TNFDialog Dialog, TextBox Depth, List<(int, int)> Writes) DepthDialog()
    {
        var (dialog, list, _, _) = Dialog(new[] { false, false });
        var writes = new List<(int, int)>();
        dialog.SetTNFDepth = (i, d) => writes.Add((i, d));
        list.SelectedIndex = 1;
        Invoke(dialog, "UpdateTNFProperties");
        var depth = (TextBox)typeof(TNFDialog).GetField("DepthBox", Private | BindingFlags.Public)!.GetValue(dialog)!;
        return (dialog, depth, writes);
    }

    [Fact]
    public void TabbingThroughAnUnchangedDepthWritesNothing()
    {
        Run(() =>
        {
            var (dialog, depth, writes) = DepthDialog();
            Assert.Equal("2", depth.Text);   // the radio's, shown on selection
            Handler(dialog, "DepthBox_LostFocus")(depth, new RoutedEventArgs());
            Assert.Empty(writes);

            // Positive control: a changed depth is written, once.
            depth.Text = "3";
            Handler(dialog, "DepthBox_LostFocus")(depth, new RoutedEventArgs());
            Handler(dialog, "DepthBox_LostFocus")(depth, new RoutedEventArgs());
            Assert.Equal(new[] { (1, 3) }, writes.ToArray());
        });
    }

    [Fact]
    public void EnterOnAnUnchangedDepthWritesNothing()
    {
        Run(() =>
        {
            var (dialog, depth, writes) = DepthDialog();
            typeof(TNFDialog).GetMethod("ApplyDepth", Private)!.Invoke(dialog, new object[] { 1 });
            Assert.Empty(writes);
            Assert.Equal("2", depth.Text);
        });
    }

    [Fact]
    public void LeavingTheDepthAfterAnArrowWritesNothingMore()
    {
        Run(() =>
        {
            var (dialog, depth, writes) = DepthDialog();
            typeof(TNFDialog).GetMethod("AdjustDepth", Private)!.Invoke(dialog, new object[] { 1, 1 });
            Assert.Equal(new[] { (1, 3) }, writes.ToArray());
            Assert.Equal("3", depth.Text);
            // The radio's read-back still says 2 (a queued write lags); the
            // comparison is with what was shown, so nothing more goes out.
            Handler(dialog, "DepthBox_LostFocus")(depth, new RoutedEventArgs());
            Assert.Single(writes);
        });
    }

    [Fact]
    public void DepthTextThatIsNotANumberPutsTheShownDepthBackAndWritesNothing()
    {
        Run(() =>
        {
            var (dialog, depth, writes) = DepthDialog();
            depth.Text = "deep";
            Handler(dialog, "DepthBox_LostFocus")(depth, new RoutedEventArgs());
            Assert.Empty(writes);
            Assert.Equal("2", depth.Text);
        });
    }

    [Fact]
    public void ADepthOutsideTheRangeIsClampedAndWrittenOnlyWhenTheClampChangesIt()
    {
        Run(() =>
        {
            var (dialog, depth, writes) = DepthDialog();
            depth.Text = "9";   // clamps to 3, a change from 2
            Handler(dialog, "DepthBox_LostFocus")(depth, new RoutedEventArgs());
            Assert.Equal(new[] { (1, 3) }, writes.ToArray());
            Assert.Equal("3", depth.Text);

            depth.Text = "7";   // clamps to 3 again: no change, no write
            Handler(dialog, "DepthBox_LostFocus")(depth, new RoutedEventArgs());
            Assert.Single(writes);
            Assert.Equal("3", depth.Text);
        });
    }

    [Theory]
    [InlineData("60.0")]        // 60 MHz, read by the tuning entry's parser
    [InlineData("0.020.000")]   // 20 kHz, in the box's own shown form
    [InlineData("54.000.001")]  // one hertz over the top
    public void ATypedFrequencyOutsideTheNotchRangeIsRefusedAndMovesNothing(string typed)
    {
        Run(() =>
        {
            var (dialog, _, box, moves) = MoveDialog();
            Assert.NotNull(TNFDialog.ParseFrequencyHz(typed));   // a number that was read, not unreadable text
            box.Text = typed;
            dialog.ApplyTypedFrequency(1);
            Assert.Empty(moves);
            Assert.Equal("14.201.000", box.Text);
        });
    }

    [Theory]
    [InlineData("0.030.000", 30_000UL)]
    [InlineData("54.000.000", 54_000_000UL)]
    [InlineData("7.074", 7_074_000UL)]
    public void ATypedFrequencyInsideTheNotchRangeMoves(string typed, ulong hz)
    {
        Run(() =>
        {
            var (dialog, _, box, moves) = MoveDialog();
            box.Text = typed;
            dialog.ApplyTypedFrequency(1);
            Assert.Equal(new[] { (1, hz) }, moves.ToArray());
        });
    }

    [Fact]
    public void TheRangeIsTheRadioLayersAndOnlyTheTypedPathRefuses()
    {
        Assert.Equal(30_000UL, Radios.FlexBase.TNFFrequencyMinHz);       // the refusal's sentence names these two
        Assert.Equal(54_000_000UL, Radios.FlexBase.TNFFrequencyMaxHz);
        Assert.True(TNFDialog.FrequencyInRange(30_000));
        Assert.False(TNFDialog.FrequencyInRange(29_999));
        Assert.True(TNFDialog.FrequencyInRange(54_000_000));
        Assert.False(TNFDialog.FrequencyInRange(54_000_001));

        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "TNFDialog.xaml.cs")).Replace("\r\n", "\n");
        int typed = src.IndexOf("internal void ApplyTypedFrequency(int idx)", StringComparison.Ordinal);
        string body = src.Substring(typed, src.IndexOf("\n    }\n", typed, StringComparison.Ordinal) - typed);
        int refuse = body.IndexOf("\"settings.tnf.frequency_out_of_range\"", StringComparison.Ordinal);
        Assert.True(refuse > 0 && refuse < body.IndexOf("MoveTo(idx, hz.Value);", StringComparison.Ordinal),
            "the refusal comes before the move");
        int step = src.IndexOf("internal void StepFrequency(int idx, int deltaHz)", StringComparison.Ordinal);
        string arrows = src.Substring(step, src.IndexOf("\n    }\n", step, StringComparison.Ordinal) - step);
        Assert.DoesNotContain("FrequencyInRange", arrows);   // the arrows stop at the edge, by design
    }

    [Fact]
    public void TheHelpTextCarriesNoSpokenMarker()
    {
        string xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(Repo(), "JJFlexWpf", "Dialogs", "TNFDialog.xaml"));
        Assert.DoesNotContain("=\"DRAFT", xaml);
    }

    private static RoutedEventHandler Handler(TNFDialog dialog, string name) =>
        (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), dialog,
            typeof(TNFDialog).GetMethod(name, Private)!);

    private static string Repo()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    private static (TNFDialog Dialog, ListBox List, CheckBox Check, List<(int, bool)> Writes) Dialog(bool[] kept)
    {
        var dialog = (TNFDialog)RuntimeHelpers.GetUninitializedObject(typeof(TNFDialog));
        var writes = new List<(int, bool)>();
        dialog.GetTNFCount = () => kept.Length;
        dialog.GetTNFWidth = _ => 100;
        dialog.GetTNFDepth = _ => 2;
        dialog.GetTNFPermanent = i => kept[i];
        dialog.SetTNFPermanent = (i, p) => writes.Add((i, p));

        var list = new ListBox();
        for (int i = 0; i < kept.Length; i++) list.Items.Add("14.10" + i);
        var check = new CheckBox();
        Put(dialog, "TNFList", list);
        Put(dialog, "FrequencyBox", new TextBox());
        Put(dialog, "WidthBox", new TextBox());
        Put(dialog, "DepthBox", new TextBox());
        Put(dialog, "PermanentCheck", check);
        Put(dialog, "RemoveButton", new Button());

        var handler = typeof(TNFDialog).GetMethod("PermanentCheck_Changed", Private)
            ?? throw new InvalidOperationException("TNFDialog has no PermanentCheck_Changed handler");
        var routed = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), dialog, handler);
        check.Checked += routed;
        check.Unchecked += routed;
        return (dialog, list, check, writes);
    }

    private static void Run(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (TargetInvocationException ex) { failure = ex.InnerException ?? ex; }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }

    private static void Put(object target, string name, object? value) =>
        (typeof(TNFDialog).GetField(name, Private | BindingFlags.Public)
            ?? throw new InvalidOperationException("TNFDialog has no field " + name)).SetValue(target, value);

    private static void Invoke(object target, string name) =>
        typeof(TNFDialog).GetMethod(name, Private | BindingFlags.DeclaredOnly)!.Invoke(target, null);
}
