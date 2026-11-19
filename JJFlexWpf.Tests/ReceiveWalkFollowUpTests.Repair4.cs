using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track N2 repair 4: Codex's third re-check of 6f407b1a, F2. A memory
/// recall's ownership read the receive slice's POSITION and counted only the
/// app's setter, so a radio-reported move away and back revived a stale recall,
/// the selected slice removed with its replacement at the same position kept
/// the old recall, and a lower slice removed (the same slice reindexed) retired
/// a good one. Ownership now reads the slice itself
/// (<see cref="FlexBase.RXSliceIdentity"/>) and a count of every effective
/// change of it (<see cref="FlexBase.RXSliceChoices"/>), from every writer.
///
/// Codex's four probes (for-claude/2026-11-19-codex-n2-recheck-3-results/
/// ReceiveWalkFollowUpTests.CodexReportedSlice.cs) are adopted with their
/// stimuli, controls and ownership assertions unchanged. Run verbatim at
/// 6f407b1a they gave Codex's result, 3 failures and 1 pass. Three edits were
/// needed and each is marked: the marks read the slice's identity rather than
/// its position (the mark's type changed), absolute counts of the slice
/// changes became counts relative to the start (two of them asserted the
/// count's old blindness, "the count did not move", as a precondition), and
/// speech is suppressed on the rig, because a roster change starts the slice
/// census timer. All windowless on the MTA test thread, asserted by the class
/// constructor; the bench is repair 1's.
/// </summary>
public sealed partial class ReceiveWalkFollowUpTests
{
    private static object BenchField(object owner, string field) => owner.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void SetVendor(object owner, string field, object value) => owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, value);
    private static void InvokeRig(FlexBase rig, string method, params object[] args) => typeof(FlexBase).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(rig, args);

    // Adapted: the slice itself, not its position (was rig.RXVFO).
    private static RecallMarks MarksOf(FlexBase rig) => new(rig, rig.RXSliceIdentity, 4, rig.RXFrequencyRequests, rig.RXSliceChoices);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Codex_reported_slice_round_trip_retires_recall(bool checkWhileAway)
    {
        using var bench = NewStatusBench(out var rig);
        rig.SuppressSpeech = true;   // adapted: no census sentence from a test
        var radio = (Radio)BenchField(bench, "Radio");
        var a = (Slice)BenchField(bench, "A");
        var b = (Slice)BenchField(bench, "B");
        var pan = (Panadapter)Activator.CreateInstance(typeof(Panadapter), BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { radio }, null)!;
        SetVendor(a, "_panadapter", pan); SetVendor(b, "_panadapter", pan);
        SetVendor(a, "_freq", 14.1); SetVendor(b, "_freq", 14.1);
        Put(rig, "clientHandle", a.ClientHandle);
        Put(rig, "FilterObj", new WpfFilterAdapter(rig));
        Put(rig, "_RXFrequency", 14_100_000UL); Put(rig, "_RXMode", "USB");
        var binding = new ObservationBinding(radio, new ConnectionAttempt("probe"));
        RecallMarks MarksNow() => MarksOf(rig);
        var recall = new PendingRecall(7_030_000, "CW", RadioStatusBuilder.BuildDetailedStatus(rig), 1000, MarksNow());
        Assert.Contains("7.030", RadioStatusBuilder.CaptureSpokenStatus(rig, recall.AskIfStillOwned(MarksNow, () => 1000)).Text);
        int count = rig.RXSliceChoices;
        radio.PropertyChanged += (sender, args) => InvokeRig(rig, "radioPropertyChangedHandler", radio, args, binding);
        void Report(Slice active, Slice inactive)
        {
            // The exact vendor update used by the parser for active=, no write command.
            var update = typeof(Slice).GetMethod("_UpdateActive", BindingFlags.NonPublic | BindingFlags.Instance)!;
            update.Invoke(inactive, new object[] { false, false });
            update.Invoke(active, new object[] { true, false });
        }
        Report(b, a);
        Assert.Equal(1, rig.RXVFO);
        if (checkWhileAway) Assert.Null(recall.AskIfStillOwned(MarksNow, () => 1001));
        Report(a, b);
        Assert.Equal(0, rig.RXVFO);
        // Adapted: was Assert.Equal(count, ...), the count's blindness to the
        // radio's report. The round trip is two changes of the slice.
        Assert.Equal(count + 2, rig.RXSliceChoices);
        var ask = recall.AskIfStillOwned(MarksNow, () => 1002);
        var landing = RadioStatusBuilder.CaptureSpokenStatus(rig, ask);
        Assert.DoesNotContain("7.030", landing.Text);
        Assert.Null(ask);
    }

    [Fact]
    public void Codex_removing_the_selected_slice_retires_the_recall_even_when_its_position_stays_zero()
    {
        using var bench = NewStatusBench(out var rig);
        rig.SuppressSpeech = true;   // adapted
        var radio = (Radio)BenchField(bench, "Radio");
        var a = (Slice)BenchField(bench, "A");
        var b = (Slice)BenchField(bench, "B");
        Put(rig, "clientHandle", a.ClientHandle);
        Put(rig, "_RXFrequency", 14_100_000UL); Put(rig, "_RXMode", "USB");
        RecallMarks MarksNow() => MarksOf(rig);
        var recall = new PendingRecall(7_030_000, "CW", RadioStatusBuilder.BuildDetailedStatus(rig), 1000, MarksNow());
        Assert.NotNull(recall.AskIfStillOwned(MarksNow, () => 1000));
        int count = rig.RXSliceChoices;
        var binding = new ObservationBinding(radio, new ConnectionAttempt("probe"));
        radio.SliceRemoved += gone => InvokeRig(rig, "sliceRemoved", gone, binding);
        typeof(Radio).GetMethod("RemoveSlice", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(radio, new object[] { a });
        Assert.DoesNotContain(a, radio.SliceList);
        Assert.Equal(1, rig.MyNumSlices);
        Assert.Same(b, ((List<Slice>)BenchField(rig, "mySlices"))[rig.RXVFO]);
        Assert.Equal(0, rig.RXVFO);
        // Adapted: was Assert.Equal(0, ...), "no mark moved". A to B is one change.
        Assert.Equal(count + 1, rig.RXSliceChoices);
        Assert.Null(recall.AskIfStillOwned(MarksNow, () => 1001));
    }

    [Fact]
    public void Codex_repeated_report_of_the_current_slice_does_not_retire_a_legitimate_recall()
    {
        using var bench = NewStatusBench(out var rig);
        rig.SuppressSpeech = true;   // adapted
        var radio = (Radio)BenchField(bench, "Radio");
        var a = (Slice)BenchField(bench, "A");
        var pan = (Panadapter)Activator.CreateInstance(typeof(Panadapter), BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { radio }, null)!;
        SetVendor(a, "_panadapter", pan); SetVendor(a, "_freq", 14.1);
        Put(rig, "clientHandle", a.ClientHandle); Put(rig, "FilterObj", new WpfFilterAdapter(rig));
        Put(rig, "_RXFrequency", 14_100_000UL); Put(rig, "_RXMode", "USB");
        var binding = new ObservationBinding(radio, new ConnectionAttempt("probe"));
        RecallMarks MarksNow() => MarksOf(rig);
        var recall = new PendingRecall(7_030_000, "CW", RadioStatusBuilder.BuildDetailedStatus(rig), 1000, MarksNow());
        int count = rig.RXSliceChoices;
        for (int i = 0; i < 3; ++i)
        {
            InvokeRig(rig, "radioPropertyChangedHandler", radio, new PropertyChangedEventArgs("ActiveSlice"), binding);
            Assert.Equal(count, rig.RXSliceChoices);   // adapted: was 0
            Assert.NotNull(recall.AskIfStillOwned(MarksNow, () => 1000 + i));
        }
    }

    [Fact]
    public void Codex_removing_an_inactive_lower_slice_preserves_the_same_selected_slices_recall()
    {
        using var bench = NewStatusBench(out var rig);
        rig.SuppressSpeech = true;   // adapted
        var radio = (Radio)BenchField(bench, "Radio");
        var a = (Slice)BenchField(bench, "A");
        var b = (Slice)BenchField(bench, "B");
        SetVendor(a, "_active", false); SetVendor(b, "_active", true);
        Put(rig, "_RXVFO", 1); Put(rig, "clientHandle", a.ClientHandle);
        Put(rig, "_RXFrequency", 14_100_000UL); Put(rig, "_RXMode", "USB");
        RecallMarks MarksNow() => MarksOf(rig);
        var recall = new PendingRecall(7_030_000, "CW", RadioStatusBuilder.BuildDetailedStatus(rig), 1000, MarksNow());
        Assert.NotNull(recall.AskIfStillOwned(MarksNow, () => 1000));
        int count = rig.RXSliceChoices;
        var binding = new ObservationBinding(radio, new ConnectionAttempt("probe"));
        radio.SliceRemoved += gone => InvokeRig(rig, "sliceRemoved", gone, binding);
        typeof(Radio).GetMethod("RemoveSlice", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(radio, new object[] { a });
        Assert.Same(b, radio.ActiveSlice);
        Assert.Same(b, ((List<Slice>)BenchField(rig, "mySlices"))[rig.RXVFO]);
        Assert.Equal(0, rig.RXVFO);
        Assert.Equal(count, rig.RXSliceChoices);   // adapted: was 0
        Assert.NotNull(recall.AskIfStillOwned(MarksNow, () => 1001));
    }

    // ── Repair 4's own: the arrival path, and every writer saying so ──

    /// <summary>
    /// A slice arriving below the selected one shifts the selected slice's
    /// position and is not a change of it; a slice arriving active is. Through
    /// FlexBase's own sliceAdded, as FlexLib's SliceAdded event calls it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_slice_arriving_moves_the_recall_only_when_it_arrives_selected(bool arrivesActive)
    {
        using var bench = NewStatusBench(out var rig);
        rig.SuppressSpeech = true;
        var radio = (Radio)BenchField(bench, "Radio");
        var a = (Slice)BenchField(bench, "A");
        var b = (Slice)BenchField(bench, "B");
        var mine = (List<Slice>)BenchField(rig, "mySlices");
        // On B with A gone, so B sits at position 0 and an arrival at index 0 lands below it.
        SetVendor(a, "_active", false); SetVendor(b, "_active", true);
        mine.Remove(a); radio.SliceList.Remove(a);
        Put(rig, "_RXVFO", 0); Put(rig, "clientHandle", b.ClientHandle);
        Put(rig, "_RXFrequency", 14_100_000UL); Put(rig, "_RXMode", "USB");
        RecallMarks MarksNow() => MarksOf(rig);
        var recall = new PendingRecall(7_030_000, "CW", RadioStatusBuilder.BuildDetailedStatus(rig), 1000, MarksNow());
        Assert.NotNull(recall.AskIfStillOwned(MarksNow, () => 1000));
        int count = rig.RXSliceChoices;

        var z = (Slice)Activator.CreateInstance(typeof(Slice), BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { radio }, null)!;
        SetVendor(z, "_index", 0); SetVendor(z, "_active", arrivesActive); SetVendor(z, "_freq", 7.03);
        if (arrivesActive) SetVendor(b, "_active", false);
        radio.SliceList.Add(z);
        InvokeRig(rig, "sliceAdded", z, new ObservationBinding(radio, new ConnectionAttempt("probe")));

        Assert.Equal(2, rig.MyNumSlices);
        Assert.Same(z, mine[0]);
        if (arrivesActive)
        {
            Assert.Same(z, rig.RXSliceIdentity);
            Assert.Equal(count + 1, rig.RXSliceChoices);
            Assert.Null(recall.AskIfStillOwned(MarksNow, () => 1001));
        }
        else
        {
            Assert.Equal(1, rig.RXVFO);                 // B reindexed
            Assert.Same(b, rig.RXSliceIdentity);        // and still B
            Assert.Equal(count, rig.RXSliceChoices);
            Assert.NotNull(recall.AskIfStillOwned(MarksNow, () => 1001));
        }
    }

    /// <summary>
    /// A recall's marks read the slice itself: a different slice object at the
    /// same position is another slice, and the same slice at another position
    /// is the same one. Through the app's own marks (MainWindow).
    /// </summary>
    [Fact]
    public void The_app_marks_read_the_slice_itself_not_its_position()
    {
        string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
        string marks = Body(main, "private Radios.RecallMarks RecallMarksNow()", "private Radios.StatusAsk? TakeRecallAskedFor()");
        Assert.Contains("rig?.RXSliceIdentity", marks);
        Assert.DoesNotContain("RXVFO", marks);
    }

    /// <summary>
    /// Every write of the receive slice's position, in every Radios file, is
    /// followed by the note that counts a change of the selected slice. The
    /// class of the defect: a writer that moved the selection without saying
    /// so (the radio's report, the roster changes, the identity restore).
    /// </summary>
    [Fact]
    public void Every_writer_of_the_receive_slice_position_says_so()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        var files = Directory.GetFiles(Path.Combine(dir!.FullName, "Radios"), "FlexBase*.cs");
        Assert.NotEmpty(files);
        int writers = 0;
        var silent = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string code = lines[i].Split("//")[0];
                int at = code.IndexOf("_RXVFO =", StringComparison.Ordinal);
                if (at < 0 || code.Contains("int _RXVFO") || code.Contains("_RXVFO ==")) continue;
                writers++;
                bool said = lines.Skip(i + 1).Take(16).Any(l => l.Contains("NoteReceiveSliceSelection("));
                if (!said) silent.Add(Path.GetFileName(file) + ":" + (i + 1) + ": " + lines[i].Trim());
            }
        }
        Assert.True(writers >= 7, "positive control: the known writers are seen (" + writers + ")");
        Assert.Empty(silent);
    }
}
