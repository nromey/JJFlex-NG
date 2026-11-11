using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The operator's last tuning steps are kept (#346 J2, ruled by Noel
/// 2026-11-09 20:38): saved a moment after they stop changing and again at
/// close, on the operator's own record.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> Windowless, asserted first: an MTA thread, and the
/// debouncer's timer stood in for by two counters, so no dispatcher and no
/// element. The records are plain objects; the host's real record and its
/// file write are pinned from source below and by
/// <c>Radios.Tests.OperatorStepCopyTests</c> against the built app.
/// </remarks>
public sealed class StepSizeMemoryTests
{
    private sealed class Record
    {
        public int Coarse = 5000, Fine = 100;
        public int[]? CoarseList, FineList;
    }

    private object? _current = new Record();
    private readonly List<(object Record, int Coarse, int Fine)> _written = new();
    private int _armed, _disarmed;

    private StepSizeMemory Memory() => new(
        () => _current,
        (r, c, f) => { var rec = (Record)r; rec.Coarse = c; rec.Fine = f; },
        r => _written.Add((r, ((Record)r).Coarse, ((Record)r).Fine)),
        () => _armed++,
        () => _disarmed++,
        r => (((Record)r).Coarse, ((Record)r).Fine),
        (r, c, f) => { var rec = (Record)r; rec.CoarseList = c; rec.FineList = f; });

    [Fact]
    public void A_key_repeat_walk_writes_the_record_once_with_where_it_landed()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var memory = Memory();
        for (int hz = 1000; hz <= 20000; hz += 1000) memory.Note(hz, 100);
        Assert.Empty(_written);                     // nothing written while the key repeats
        Assert.Equal(20, _armed);                   // each change restarts the quiet wait
        Assert.True(memory.Pending);

        memory.Flush();                             // the quiet wait ends (or the app closes)
        var only = Assert.Single(_written);
        Assert.Equal(20000, only.Coarse);
        Assert.Equal(100, only.Fine);
        Assert.False(memory.Pending);

        memory.Flush();                             // nothing pending: nothing written again
        Assert.Single(_written);
    }

    /// <summary>
    /// The record in memory changes at once, so an Operators dialog opened in
    /// the quiet window copies the new steps rather than the old ones.
    /// </summary>
    [Fact]
    public void The_record_in_memory_changes_at_once()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var memory = Memory();
        memory.Note(10000, 50);
        var rec = (Record)_current!;
        Assert.Equal(10000, rec.Coarse);
        Assert.Equal(50, rec.Fine);
        Assert.Empty(_written);
    }

    /// <summary>
    /// One operator's step never lands in another's file: the record is the
    /// one current when the step changed, and a step for a different record
    /// writes the pending one first.
    /// </summary>
    [Fact]
    public void A_step_noted_for_another_operator_writes_the_first_operators_record_first()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var memory = Memory();
        var first = (Record)_current!;
        memory.Note(2500, 100);

        var second = new Record();
        _current = second;                          // the operator switched
        memory.Note(1000, 10);

        var written = Assert.Single(_written);
        Assert.Same(first, written.Record);
        Assert.Equal(2500, written.Coarse);
        Assert.Equal(1000, second.Coarse);          // the second is in memory, not yet written...
        memory.Flush();
        Assert.Same(second, _written[1].Record);    // ...and written to its own record
        Assert.Equal(1000, _written[1].Coarse);
    }

    [Fact]
    public void With_no_operator_nothing_is_remembered()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _current = null;
        var memory = Memory();
        memory.Note(1000, 10);
        memory.Flush();
        Assert.Empty(_written);
        Assert.Equal(0, _armed);
    }

    /// <summary>
    /// The host's wiring, from source: the saved steps load BEFORE
    /// SaveStepSizes is wired (ApplyStepSizes always invokes it, so a load
    /// after would save), an operator switch flushes and then loads with the
    /// hook unwired, close flushes, and a replaced record is not written.
    /// </summary>
    [Fact]
    public void The_host_loads_before_it_wires_and_flushes_at_a_switch_and_at_close()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string app = Read("ApplicationEvents.vb");
        int load = app.IndexOf("handlers.ApplyStepSizes(CurrentOp.CoarseTuneStep, CurrentOp.FineTuneStep, speak:=False)", StringComparison.Ordinal);
        int wire = app.IndexOf("handlers.SaveStepSizes = AddressOf _stepMemory.Note", StringComparison.Ordinal);
        Assert.True(load > 0 && wire > load, "the saved steps must load before SaveStepSizes is wired");

        int switchAt = app.IndexOf("AddHandler Operators.ConfigEvent,", wire, StringComparison.Ordinal);
        Assert.True(switchAt > wire);
        string onSwitch = app.Substring(switchAt, app.IndexOf("End Sub", switchAt, StringComparison.Ordinal) - switchAt);
        int flush = onSwitch.IndexOf("_stepMemory?.Flush()", StringComparison.Ordinal);
        int unwire = onSwitch.IndexOf("handlers.SaveStepSizes = Nothing", StringComparison.Ordinal);
        int reload = onSwitch.IndexOf("handlers.ApplyStepSizes(", StringComparison.Ordinal);
        Assert.True(flush >= 0 && unwire > flush && reload > unwire);

        Assert.Contains("Operators.ops.Contains(op) Then", app, StringComparison.Ordinal);
        int shutdown = app.IndexOf("Private Sub MyApplication_Shutdown(", StringComparison.Ordinal);
        Assert.True(app.IndexOf("_stepMemory?.Flush()", shutdown, StringComparison.Ordinal) > shutdown);
    }

    // ---- #346 repair one ----

    /// <summary>
    /// Codex B1: with no radio there are no FreqOutHandlers (they are built at
    /// power-on), so Settings showed 1000 Hz and 10 Hz, saved nothing, and the
    /// next connect loaded the old steps. Now Settings shows the operator's
    /// saved steps and keeps a change on the operator's record (in memory at
    /// once, written once after the quiet), which is what the connect loads.
    /// Driven through the window's own two methods, on a window made with no
    /// constructor (no element exists), with no handlers, which is the
    /// no-radio state.
    /// </summary>
    [Fact]
    public void With_no_radio_Settings_shows_the_operators_steps_and_keeps_a_change_on_the_record()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        Assert.Null(window.FreqHandlers);                        // the no-radio state
        var record = (Record)_current!;
        record.Coarse = 2500; record.Fine = 50;                  // what an earlier session saved
        window.StepMemory = Memory();

        Assert.Equal((2500, 50), window.StepSizesForSettings());

        window.ApplyStepSizesFromSettings(10000, 25);            // Settings, OK
        Assert.Equal((10000, 25), (record.Coarse, record.Fine)); // the record the connect loads
        Assert.Equal((10000, 25), window.StepSizesForSettings()); // reopened: what was chosen
        Assert.Empty(_written);
        Assert.True(window.StepMemory.Pending);
        window.StepMemory.Flush();                               // the quiet, or close
        Assert.Equal(new[] { ((object)record, 10000, 25) }, _written);

        window.ApplyStepSizesFromSettings(0, 100);               // a step that is not positive keeps its value
        Assert.Equal((10000, 100), (record.Coarse, record.Fine));
    }

    [Fact]
    public void With_no_radio_and_no_operator_Settings_shows_the_old_fallback_and_keeps_nothing()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        _current = null;
        window.StepMemory = Memory();
        Assert.Equal((MainWindow.SettingsFallbackCoarseStep, MainWindow.SettingsFallbackFineStep), window.StepSizesForSettings());
        window.ApplyStepSizesFromSettings(2500, 50);
        Assert.False(window.StepMemory.Pending);
        Assert.Equal(0, _armed);
    }

    /// <summary>
    /// Codex N1: the host sets the wire callback after InitializeApplication,
    /// which starts the first connect, so the handlers can be created first
    /// (the radio's power-on reaching the UI thread) and were then never wired.
    /// That state, a window holding handlers and no callback yet, is made
    /// directly; setting the callback then wires them, once. Set first, it
    /// does nothing until the handlers exist, which EnsureFreqOutHandlers
    /// still does at creation (pinned from source).
    /// </summary>
    [Fact]
    public void The_handlers_are_wired_once_whichever_comes_first()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var handlers = (FreqOutHandlers)RuntimeHelpers.GetUninitializedObject(typeof(FreqOutHandlers));
        var field = typeof(MainWindow).GetField("_freqOutHandlers", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Handlers first (the race), then the callback.
        var late = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        field.SetValue(late, handlers);
        var wired = new List<FreqOutHandlers>();
        late.FreqOutHandlersWireCallback = h => wired.Add(h);
        Assert.Equal(new[] { handlers }, wired);

        // Callback first: nothing to wire yet.
        var early = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        var none = new List<FreqOutHandlers>();
        early.FreqOutHandlersWireCallback = h => none.Add(h);
        Assert.Empty(none);

        string window = Read("JJFlexWpf", "MainWindow.xaml.cs");
        int ensure = window.IndexOf("private void EnsureFreqOutHandlers()", StringComparison.Ordinal);
        int create = window.IndexOf("_freqOutHandlers = new FreqOutHandlers(this);", ensure, StringComparison.Ordinal);
        int invoke = window.IndexOf("FreqOutHandlersWireCallback?.Invoke(_freqOutHandlers);", ensure, StringComparison.Ordinal);
        Assert.True(ensure > 0 && create > ensure && invoke > create, "creation no longer wires the handlers");

        // The order the host really uses: the memory before the first connect,
        // the callback after it (which is why the replay above is needed).
        string app = Read("ApplicationEvents.vb");
        int memory = app.IndexOf("WpfMainWindow.StepMemory = _stepMemory", StringComparison.Ordinal);
        int init = app.IndexOf("            InitializeApplication()", StringComparison.Ordinal);
        int callback = app.IndexOf("WpfMainWindow.FreqOutHandlersWireCallback = Sub(handlers)", StringComparison.Ordinal);
        Assert.True(memory > 0 && init > memory && callback > init);

        string menu = Read("JJFlexWpf", "NativeMenuBar.cs");
        Assert.Contains("var (coarseStep, fineStep) = _window.StepSizesForSettings();", menu, StringComparison.Ordinal);
        Assert.Contains("ApplyStepSizesFromSettings(coarseStep, fineStep);", window, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(parts))).Replace("\r\n", "\n");
    }

    /// <summary>
    /// #346 part two: the step lists are kept on the current operator's
    /// record at once (where the table reads them) and written with the
    /// steps' own debounce; with no operator they are not kept, and the
    /// caller is told so it can say it.
    /// </summary>
    [Fact]
    public void The_step_lists_are_kept_on_the_operators_record_with_the_same_debounce()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var memory = Memory();
        Assert.True(memory.NoteLists(new[] { 250, 3000 }, null));
        var rec = (Record)_current!;
        Assert.Equal(new[] { 250, 3000 }, rec.CoarseList);
        Assert.Null(rec.FineList);                      // the starting list
        Assert.Empty(_written);
        Assert.True(memory.Pending);
        Assert.Equal(1, _armed);
        memory.Flush();
        Assert.Same(rec, Assert.Single(_written).Record);

        _current = null;
        Assert.False(memory.NoteLists(new[] { 500 }, null));
        Assert.False(memory.Pending);
    }
}
