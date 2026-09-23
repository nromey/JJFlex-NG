using System;
using System.Threading;

namespace Radios.StationConnect
{
    /// <summary>
    /// The window in which ONE dispatched action may still be sent. The
    /// caller opens it when it queues the delegate and closes it the moment
    /// it stops waiting for that action's result; the delegate refuses to
    /// send once the window is closed.
    /// </summary>
    /// <remarks>
    /// Until Track G3 a queued delegate checked the OPERATION and the
    /// twenty-second PHASE, while its caller gave up after the action's own
    /// two-second bound: a panafall request or a slice tune released by the
    /// command loop after its caller had already returned Timeout would
    /// still go out (Track G2 re-review, sections 1.7 and 5). A result that
    /// has been returned is terminal for its action; this is the token that
    /// makes it so. The phase and operation checks stay; this is the third,
    /// per-action, one.
    /// </remarks>
    public sealed class ActionWindow
    {
        private int _closed;
        private string _whyClosed;

        public ActionWindow(string action)
        {
            Action = action ?? "";
        }

        public string Action { get; }

        /// <summary>True until <see cref="Close"/> has been called.</summary>
        public bool IsOpen => Volatile.Read(ref _closed) == 0;

        /// <summary>Why the window closed, or null while open.</summary>
        public string WhyClosed => Volatile.Read(ref _whyClosed);

        /// <summary>Close the window. Idempotent; the first reason wins.</summary>
        public void Close(string why)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Volatile.Write(ref _whyClosed, why ?? "closed");
            }
        }

        /// <summary>Null while the action may still be sent, else the refusal.</summary>
        public string Refusal =>
            IsOpen ? null : "the " + Action + " ran after its own result had been returned (" + WhyClosed + ")";

        public override string ToString() => Action + (IsOpen ? " (open)" : " (closed: " + WhyClosed + ")");
    }
}
