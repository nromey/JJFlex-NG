using System;
using System.Windows.Threading;

namespace JJFlexWpf;

/// <summary>
/// The operator's last coarse and fine tuning steps, kept (#346 J2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ruled by Noel 2026-11-09 20:38 (night question J2):</b> the step last
/// chosen, by any route (Alt or Shift with Left or Right mid-contact included),
/// is the step the operator starts with next time, the same on every radio
/// (the operator's settings), saved a moment after the steps stop changing and
/// again at close. <see cref="FreqOutHandlers.SaveStepSizes"/> is wired to
/// <see cref="Note"/>; the host loads the saved steps BEFORE it wires that, so
/// the load never saves.
/// </para>
/// <para>
/// <b>Why debounced.</b> A ladder key fires many times a second under key
/// repeat, and the operator record is written whole and synchronously. So the
/// record in memory changes at once (an Operators dialog opened in the quiet
/// window copies the new steps), and the file is written once, after
/// <see cref="Quiet"/> with no further change, or at <see cref="Flush"/>.
/// </para>
/// <para>
/// <b>One operator's step never lands in another's file.</b> The record a
/// pending write belongs to is captured when the step changes, not read when
/// the write fires. A step noted for a different record first writes the one
/// already pending. The host flushes at an operator switch and at close.
/// </para>
/// <para>
/// <b>UI thread only.</b> The timer is a <see cref="DispatcherTimer"/>, and the
/// steps only ever change from key handlers, the step picker and Settings, all
/// on the UI thread.
/// </para>
/// </remarks>
public sealed class StepSizeMemory
{
    /// <summary>How long the steps must stay still before the file is written.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(1500);

    private readonly Func<object?> _currentOperator;
    private readonly Action<object, int, int> _remember;
    private readonly Action<object> _write;
    private readonly Func<object, (int Coarse, int Fine)>? _read;
    private readonly Action _arm;
    private readonly Action _disarm;

    private object? _pendingRecord;

    /// <param name="currentOperator">The operator record in use now; null when none.</param>
    /// <param name="remember">Puts the two steps on a record, in memory only.</param>
    /// <param name="write">Writes a record to its own file.</param>
    /// <param name="read">Reads the two steps a record holds.</param>
    public StepSizeMemory(Func<object?> currentOperator, Action<object, int, int> remember, Action<object> write,
        Func<object, (int Coarse, int Fine)> read)
    {
        _currentOperator = currentOperator ?? throw new ArgumentNullException(nameof(currentOperator));
        _remember = remember ?? throw new ArgumentNullException(nameof(remember));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _read = read ?? throw new ArgumentNullException(nameof(read));
        var timer = new DispatcherTimer { Interval = Quiet };
        timer.Tick += (_, _) => Flush();
        _arm = () => { timer.Stop(); timer.Start(); };
        _disarm = timer.Stop;
    }

    /// <summary>For tests: the timer stood in for, so nothing needs a dispatcher.</summary>
    internal StepSizeMemory(Func<object?> currentOperator, Action<object, int, int> remember, Action<object> write,
        Action arm, Action disarm, Func<object, (int Coarse, int Fine)>? read = null)
    {
        _currentOperator = currentOperator;
        _remember = remember;
        _write = write;
        _arm = arm;
        _disarm = disarm;
        _read = read;
    }

    /// <summary>
    /// The steps the current operator's record holds now, null when there is
    /// no operator. Includes a step noted a moment ago and not yet written:
    /// the record changes at once.
    /// </summary>
    /// <remarks>
    /// #346 repair one (Codex B1): with no radio connected there is no
    /// <see cref="FreqOutHandlers"/> (it is built at power-on), so Settings
    /// shows, and the next connect loads, what the record holds.
    /// </remarks>
    public (int Coarse, int Fine)? CurrentSteps()
    {
        object? record = _currentOperator();
        if (record == null || _read == null) return null;
        return _read(record);
    }

    /// <summary>True while a write is waiting for the steps to stay still.</summary>
    public bool Pending => _pendingRecord != null;

    /// <summary>
    /// The steps changed: remember them on the current operator's record now,
    /// and write that record once they have stayed still for <see cref="Quiet"/>.
    /// </summary>
    public void Note(int coarseHz, int fineHz)
    {
        object? record = _currentOperator();
        if (record == null) return;
        if (_pendingRecord != null && !ReferenceEquals(_pendingRecord, record)) Flush();
        _remember(record, coarseHz, fineHz);
        _pendingRecord = record;
        _arm();
    }

    /// <summary>Write the pending record now, if there is one. Idempotent.</summary>
    public void Flush()
    {
        object? record = _pendingRecord;
        _pendingRecord = null;
        _disarm();
        if (record != null) _write(record);
    }
}
