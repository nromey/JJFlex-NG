using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

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
        /// True if tracing is on.
        /// </summary>
        public static bool On
        {
            get { return _on; }
            set
            {
                if (_on != value)
                {
                    _on = value;
                    if (value)
                    {
                        Trace.AutoFlush = true;
                    }
                    else
                    {
                        Trace.Flush();
                        Trace.Close();
                        TraceFile = null;
                        ToConsole = false;
                    }
                }
            }
        }

        /// <summary>
        /// the trace switch
        /// </summary>
        public static TraceSwitch TheSwitch { get; set; }

        private static TextWriterTraceListener listener = null;
        private static string _TraceFile = null;
        /// <summary>
        /// the trace file
        /// </summary>
        public static string TraceFile
        {
            get { return _TraceFile; }
            set
            {
                // Can't change file if on.
                if (_on) return;
                if (value == "") value = null;
                if (value != _TraceFile)
                {
                    if (value == null)
                    {
                        if (listener != null)
                        {
                            Trace.Listeners.Remove(listener);
                            listener.Dispose();
                            listener = null;
                        }
                    }
                    else
                    {
                        listener = new TextWriterTraceListener(File.Create(value));
                        Trace.Listeners.Add(listener);
                    }
                    _TraceFile = value;
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
        }

        /// <summary>
        /// Builds the trace prefix: "{ticks} [T{id}:{name}] " or "{ticks} [T{id}] ".
        /// </summary>
        private static string TracePrefix()
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
            Trace.WriteLine(TracePrefix() + str);
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
                string line = TracePrefix() + str;
                if (Debugger.IsAttached) Debug.WriteLine(line);
                else Trace.WriteLine(line);
            }
        }
    }
}
