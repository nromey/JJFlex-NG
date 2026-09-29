using System.Diagnostics;

namespace JJTrace
{
    /// <summary>
    /// The one listener that stays in <c>Trace.Listeners</c> for the life of
    /// the process. It writes nothing itself; it passes every line through the
    /// coordinator's gate to whichever sink owns the current session.
    ///
    /// <para><b>Why the listener set stops changing.</b> The old design added a
    /// file listener when tracing came on and removed it when tracing went off,
    /// and closing a session called process-wide <c>Trace.Close</c>. Two things
    /// follow from that and both are defects. Between the remove and the add
    /// there are zero listeners, so lines written by anything that calls
    /// <c>System.Diagnostics.Trace.WriteLine</c> directly — which JJFlexWpf does
    /// in dozens of places, and FlexLib's panadapter does at frame rate —
    /// evaporate. And <c>Trace.Close</c> is process-wide: a caller closing ITS
    /// session closed everyone's, including a session that had replaced it
    /// moments earlier.</para>
    ///
    /// <para><b>Close is deliberately a no-op.</b> A process-lifetime router is
    /// never closed. Sessions are closed by the coordinator, on the sink it
    /// owns, under the gate. If anything in the process calls
    /// <c>Trace.Close()</c> it now costs nothing rather than taking the live
    /// trace with it.</para>
    ///
    /// <para><b>Lock order:</b> framework trace dispatch, then this gate, then
    /// the sink's own synchronisation. A lifecycle transition starts at the
    /// gate and calls only the owned sink's internal methods — never
    /// <c>Trace.WriteLine</c>, <c>Trace.Flush</c>, <c>Trace.Close</c>, never a
    /// change to <c>Trace.Listeners</c>, never a user callback and never a wait
    /// on a task. That is what keeps the two locks in one order.</para>
    /// </summary>
    internal sealed class TraceRouterListener : TraceListener
    {
        public override void Write(string message)
        {
            TraceCoordinator.RouteWrite(message);
        }

        public override void WriteLine(string message)
        {
            TraceCoordinator.RouteWriteLine(message);
        }

        public override void Flush()
        {
            TraceCoordinator.RouteFlush();
        }

        public override void Close()
        {
            // Nothing. See the class remarks: a process-wide Close must never
            // be able to take a session's sink down.
        }

        protected override void Dispose(bool disposing)
        {
            // Same reasoning as Close.
            base.Dispose(disposing);
        }
    }
}
