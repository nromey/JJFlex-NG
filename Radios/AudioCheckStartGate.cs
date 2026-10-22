using System;

namespace Radios;

/// <summary>
/// One pending Audio Check start, across every door. Cancellation revokes its
/// right to key immediately, but admission stays closed until its rollback
/// finishes. A late completion cannot release a newer owner's admission.
/// </summary>
public sealed class AudioCheckStartGate
{
    private readonly object _sync = new();
    private Lease _pending;
    public bool IsBusy { get { lock (_sync) return _pending != null; } }
    public bool IsCancelled { get { lock (_sync) return _pending?.Cancelled == true; } }
    public Lease TryBegin()
    {
        lock (_sync)
        {
            if (_pending != null) return null;
            return _pending = new Lease(this);
        }
    }
    public void Cancel() { lock (_sync) { if (_pending != null) _pending.Cancelled = true; } }
    public sealed class Lease : IDisposable
    {
        private readonly AudioCheckStartGate _gate;
        internal bool Cancelled;
        internal Lease(AudioCheckStartGate gate) => _gate = gate;
        public bool IsCurrent { get { lock (_gate._sync) return !Cancelled && ReferenceEquals(_gate._pending, this); } }
        // Cancellation and the final ownership check/key action are serialized.
        public bool TryRun(Func<bool> action)
        {
            lock (_gate._sync) return IsCurrent && action();
        }
        public void Dispose()
        {
            lock (_gate._sync)
            {
                Cancelled = true;
                if (ReferenceEquals(_gate._pending, this)) _gate._pending = null;
            }
        }
    }
}
