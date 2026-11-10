using System;
using System.Collections.Generic;
using System.IO;
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
    }

    private object? _current = new Record();
    private readonly List<(object Record, int Coarse, int Fine)> _written = new();
    private int _armed, _disarmed;

    private StepSizeMemory Memory() => new(
        () => _current,
        (r, c, f) => { var rec = (Record)r; rec.Coarse = c; rec.Fine = f; },
        r => _written.Add((r, ((Record)r).Coarse, ((Record)r).Fine)),
        () => _armed++,
        () => _disarmed++);

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

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(parts))).Replace("\r\n", "\n");
    }
}
