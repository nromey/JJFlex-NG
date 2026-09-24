using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace JJTrace
{
    public static partial class Tracing
    {
        /// <summary>
        /// JJTrace.dll version
        /// </summary>
        public static Version Version
        {
            get
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                AssemblyName asmName = asm.GetName();
                return asmName.Version;
            }
        }

        private static long beginTicks;

        private static bool _on = false;
        /// <summary>
        /// True if tracing is on — the EMISSION gate, and now only that.
        ///
        /// <para><b>Turning it off no longer closes anything.</b> It used to
        /// call process-wide <c>Trace.Flush</c> and <c>Trace.Close</c>, null the
        /// trace file and drop the console listener, which made "stop emitting"
        /// and "end this session's file" the same act performed by whoever got
        /// there first. That is half of #612: a caller closing ITS session
        /// closed everyone's, including a session that had replaced it
        /// moments earlier. Files are opened and closed by
        /// <see cref="TraceCoordinator"/>, on the sink it owns, under its
        /// gate.</para>
        /// </summary>
        public static bool On
        {
            get { return _on; }
            set
            {
                if (_on != value)
                {
                    _on = value;
                    if (value) Trace.AutoFlush = true;
                }
            }
        }

        /// <summary>
        /// the trace switch
        /// </summary>
        public static TraceSwitch TheSwitch { get; set; }

        /// <summary>
        /// The live trace listener, or null when nothing is recording. The
        /// coordinator owns it; this is a read.
        /// </summary>
        internal static RotatingTraceListener LiveListener
        {
            get { return TraceCoordinator.Sink; }
        }

        private static string _unmanagedPath;

        /// <summary>
        /// Where lines are landing right now.
        ///
        /// <para>After a rotation the live file keeps the same path, but if a
        /// rotation had to fall back to the part path (rename failed) the sink
        /// is the authority on where lines are actually landing. Anything that
        /// attaches "the current trace" — the crash bundler above all — must get
        /// the real path.</para>
        ///
        /// <para><b>The setter is for the console tools only.</b> Inside the
        /// application every open and close goes through
        /// <see cref="TraceCoordinator"/>, because a file and a session and a
        /// capture have to change together. <c>RadioInTheLoop</c> and the
        /// SmartLink harness have no session lifecycle at all, so they get a
        /// plain sink and are refused if a real session is live.</para>
        /// </summary>
        public static string TraceFile
        {
            get
            {
                string live = TraceCoordinator.LivePath;
                return live ?? _unmanagedPath;
            }
            set
            {
                if (value == "") value = null;
                if (value == _unmanagedPath && value == null) return;
                if (TraceCoordinator.CurrentSession != null)
                {
                    // A managed session owns the sink. Silently swapping the
                    // file under it is exactly the bypass this design removes.
                    return;
                }
                _unmanagedPath = value;
                if (value == null)
                {
                    TraceCoordinator.CloseUnmanagedSink();
                }
                else
                {
                    TraceCoordinator.OpenUnmanagedSink(value);
                }
            }
        }

        private static ConsoleTraceListener consoleListener = null;
        private static bool _ToConsole;
        /// <summary>
        /// Send output to the console.
        /// </summary>
        public static bool ToConsole
        {
            get { return _ToConsole; }
            set
            {
                // Can't change if on.
                if (_on) return;
                if (_ToConsole != value)
                {
                    if (value)
                    {
                        consoleListener = new ConsoleTraceListener();
                        Trace.Listeners.Add(consoleListener);
                    }
                    else
                    {
                        if (consoleListener != null)
                        {
                            Trace.Listeners.Remove(consoleListener);
                            consoleListener.Dispose();
                            consoleListener = null;
                        }
                    }
                    _ToConsole = value;
                }
            }
        }

        static Tracing()
        {
            TheSwitch = new TraceSwitch("TraceSwitch", "from .config file");
            beginTicks = DateTime.Now.Ticks;
            DetachDefaultListener();
            RegisterRouter();
        }

        private static TraceRouterListener router;

        /// <summary>
        /// Put the process-lifetime router in <c>Trace.Listeners</c>, once.
        /// Everything written through <see cref="TraceLine(string)"/> AND
        /// everything written by a direct <c>System.Diagnostics.Trace</c> or
        /// <c>Debug</c> call passes its gate before it reaches a sink — which
        /// is what lets the owner switch sinks atomically with respect to every
        /// writer, instead of leaving a window where the listener set is empty
        /// and lines evaporate.
        /// </summary>
        private static void RegisterRouter()
        {
            try
            {
                if (router != null) return;
                router = new TraceRouterListener();
                Trace.Listeners.Add(router);
            }
            catch
            {
                // Tracing must never be the thing that takes the app down.
            }
        }

        /// <summary>
        /// True once <see cref="DetachDefaultListener"/> has taken the
        /// framework's <c>DefaultTraceListener</c> out of
        /// <c>Trace.Listeners</c>. Reported at boot so a future stall trace
        /// says outright whether the app was immune.
        /// </summary>
        public static bool DefaultListenerDetached { get; private set; }

        /// <summary>
        /// Remove the framework's <c>DefaultTraceListener</c> from
        /// <c>Trace.Listeners</c>. Idempotent, and safe to call from anywhere.
        ///
        /// **This is the fix for the ten-second-per-trace-line startup stall
        /// (#434), and it is worth stating why a listener nobody reads was
        /// costing ten seconds a line.**
        ///
        /// With an empty <c>LogFileName</c> — which is the shipped state, since
        /// nothing configures one — <c>DefaultTraceListener</c>'s only output is
        /// <c>OutputDebugStringW</c>. That call opens <c>DBWinMutex</c> and, if
        /// the debug-monitor objects exist, waits on <c>DBWIN_BUFFER_READY</c>
        /// with a **fixed 10,000 ms timeout**. A debug monitor that registered
        /// and then stopped servicing the buffer therefore makes every
        /// <c>OutputDebugString</c> call in *every* process on the machine cost
        /// exactly ten seconds and then give up.
        ///
        /// Proven by deliberate reproduction 2026-09-01, measured from
        /// PowerShell (which writes none of our trace files, killing the
        /// filesystem explanation outright): 0.020 ms per call at baseline,
        /// **10,003.542 ms** with the event created and unserviced, 0.024 ms
        /// after release. The stalled trace's own deltas were 10,001 to 10,033.
        ///
        /// <c>Trace.UseGlobalLock</c> is <c>true</c>, so every listener write
        /// serializes on one critical section — which is why one blocked
        /// listener stalls the whole process rather than one thread, and why a
        /// background thread's interposed line cost the main thread an extra
        /// full timeout.
        ///
        /// Removing it costs nothing. No debugger is ever attached when the
        /// boot trace runs, so that output goes nowhere anyone reads, and this
        /// also takes a syscall off a path that runs tens of thousands of times
        /// a session. Everything we actually keep — the rotating file listener,
        /// and the console listener when asked for — is untouched, and
        /// <c>Debug.WriteLine</c> callers (FlexLib's panadapter above all) still
        /// reach the trace file, because in .NET the Debug provider is routed
        /// into the same <c>Trace.Listeners</c> collection.
        /// </summary>
        /// <returns>True if a listener was removed by this call.</returns>
        public static bool DetachDefaultListener()
        {
            bool removed = false;
            try
            {
                // Copy first: removing while enumerating the live collection is
                // exactly the kind of thing that must never throw out of a
                // trace path.
                var doomed = new List<TraceListener>();
                foreach (TraceListener l in Trace.Listeners)
                {
                    if (l is DefaultTraceListener) doomed.Add(l);
                }
                foreach (TraceListener l in doomed)
                {
                    Trace.Listeners.Remove(l);
                    removed = true;
                }
                DefaultListenerDetached = true;
            }
            catch
            {
                // Tracing must never be the thing that takes the app down. If
                // the collection cannot be touched we are no worse off than
                // before this method existed.
            }
            return removed;
        }

        /// <summary>
        /// Builds the trace prefix: "{ticks} [T{id}:{name}] " or "{ticks} [T{id}] ".
        /// Internal because the coordinator writes terminal records straight
        /// into a sink and they must carry the same prefix as every other line.
        /// </summary>
        internal static string TracePrefix()
        {
            long tks = (DateTime.Now.Ticks - beginTicks) / 10000;
            var t = System.Threading.Thread.CurrentThread;
            string threadTag = string.IsNullOrEmpty(t.Name)
                ? $"[T{t.ManagedThreadId}]"
                : $"[T{t.ManagedThreadId}:{t.Name}]";
            return $"{tks} {threadTag} ";
        }

        /// <summary>
        /// Unconditionally trace a line.
        /// </summary>
        /// <param name="str">string to trace</param>
        public static void TraceLine(string str)
        {
            if (!On) return;
            Emit(str, preferDebugWhenAttached: false);
        }
        /// <summary>
        /// Conditionally trace a line for this level.
        /// </summary>
        /// <param name="str">string to trace</param>
        /// <param name="lvl">level at which to trace.</param>
        public static void TraceLine(string str, TraceLevel lvl)
        {
            if (!On) return;
            if (TheSwitch.Level >= lvl)
            {
                Emit(str, preferDebugWhenAttached: true);
            }
        }

        // ── The split stopwatch (#434) ──────────────────────────────────────
        //
        // The original #434 entry asked for "a trace line naming which call
        // took the ten seconds", and it took a reconstruction from timestamps
        // to answer it after the fact. This is that instrument, standing.
        //
        // Three costs are separated: building the prefix, our own rotating file
        // listener's write, and everything else in the listener dispatch — which
        // is where a blocked OutputDebugString would land. When the whole write
        // exceeds the threshold, one marker names the split and lists the
        // listener set, so the next stall trace carries its own diagnosis.
        //
        // Cost when nothing is wrong: three QPC reads per line. Against the file
        // write already happening, and against the OutputDebugString syscall
        // this change removes, that is not measurable.

        /// <summary>A write slower than this earns a marker line.</summary>
        private const long SlowWriteMarkerMs = 1000;

        /// <summary>
        /// Minimum spacing between markers. The marker goes out through the
        /// same listeners, so during a real stall it costs a full timeout of
        /// its own — one is the diagnosis, a stream of them is just more stall.
        /// </summary>
        private const long SlowWriteMarkerIntervalMs = 60000;

        private static long lastSlowMarkerStamp;

        [ThreadStatic] private static bool inSlowMarker;

        private static void Emit(string str, bool preferDebugWhenAttached)
        {
            long tStart = Stopwatch.GetTimestamp();
            string line = TracePrefix() + str;
            long tPrefixed = Stopwatch.GetTimestamp();

            RotatingTraceListener.BeginThreadCostWindow();
            if (preferDebugWhenAttached && Debugger.IsAttached) Debug.WriteLine(line);
            else Trace.WriteLine(line);
            long tWritten = Stopwatch.GetTimestamp();
            long ourTicks = RotatingTraceListener.EndThreadCostWindow();

            if (ElapsedMs(tStart, tWritten) >= SlowWriteMarkerMs)
            {
                MarkSlowWrite(tStart, tPrefixed, tWritten, ourTicks);
            }
        }

        private static long ElapsedMs(long fromStamp, long toStamp)
        {
            return (toStamp - fromStamp) * 1000L / Stopwatch.Frequency;
        }

        private static void MarkSlowWrite(long tStart, long tPrefixed, long tWritten, long ourTicks)
        {
            // Never let the marker's own write recurse into another marker.
            if (inSlowMarker) return;

            long now = tWritten;
            long last = Interlocked.Read(ref lastSlowMarkerStamp);
            if (last != 0 && ElapsedMs(last, now) < SlowWriteMarkerIntervalMs) return;
            if (Interlocked.CompareExchange(ref lastSlowMarkerStamp, now, last) != last) return;

            inSlowMarker = true;
            try
            {
                long prefixMs = ElapsedMs(tStart, tPrefixed);
                long dispatchMs = ElapsedMs(tPrefixed, tWritten);
                long ourMs = ourTicks * 1000L / Stopwatch.Frequency;
                long otherMs = dispatchMs - ourMs;
                if (otherMs < 0) otherMs = 0;

                string marker =
                    "Tracing: SLOW TRACE WRITE " + (prefixMs + dispatchMs) + " ms"
                    + " - prefix " + prefixMs + " ms"
                    + ", rotating file listener " + ourMs + " ms"
                    + ", other listeners " + otherMs + " ms"
                    + "; listeners=" + DescribeListeners()
                    + "; defaultListenerDetached=" + DefaultListenerDetached
                    + ". A large 'other listeners' figure at about 10,000 ms is"
                    + " OutputDebugString waiting on an unserviced debug monitor (#434).";

                Trace.WriteLine(TracePrefix() + marker);
            }
            catch
            {
                // A diagnostic must not be able to break the thing it measures.
            }
            finally
            {
                inSlowMarker = false;
            }
        }

        /// <summary>
        /// Comma-separated listener type names, for the slow-write marker.
        /// Best effort: the collection can be mutated by another thread and
        /// this runs on a path that must never throw.
        /// </summary>
        private static string DescribeListeners()
        {
            try
            {
                var names = new List<string>();
                foreach (TraceListener l in Trace.Listeners)
                {
                    names.Add(l == null ? "null" : l.GetType().Name);
                }
                return names.Count == 0 ? "(none)" : string.Join(",", names);
            }
            catch (Exception ex)
            {
                return "(unreadable: " + ex.GetType().Name + ")";
            }
        }
    }
}
