using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using JJTrace;

namespace JJFlexControl
{
    class Serial
    {
        private SerialPort knobPort;
        internal bool IsOpen
        {
            get
            {
                return ((knobPort != null) && knobPort.IsOpen);
            }
        }

        private FlexControl parent;
        internal Serial(FlexControl p)
        {
            Tracing.TraceLine("Serial", TraceLevel.Info);
            parent = p;
        }

        /// <summary>
        /// Open the port.
        /// </summary>
        /// <param name="port">name of port to open</param>
        /// <returns></returns>
        internal bool Open(string port)
        {
            Tracing.TraceLine("Serial.Open:" + port, TraceLevel.Info);
            bool rv = true;
            knobPort = new SerialPort();
            try
            {
                knobPort.PortName = port;
                knobPort.BaudRate = 9600;
                knobPort.Parity = Parity.None;
                knobPort.Handshake = Handshake.RequestToSend;
                knobPort.DataBits = 8;
                knobPort.StopBits = StopBits.One;
                knobPort.Encoding = System.Text.Encoding.UTF8;
                knobPort.Open();
            }
            catch(Exception ex)
            {
                Tracing.TraceLine("Serial:Open exception:" + ex.Message, TraceLevel.Error);
                if (knobPort != null) knobPort.Dispose();
                rv = false;
            }

            // Create area to hold characters.
            if (rv)
            {
                buf = new bufType(knobPort);
                // A fresh filter at EVERY open: the device replays whatever was done to it while
                // the port was closed, and Noel's buffer ruling applies to each open (#520).
                reader = new Reader(parent);
                knobPort.DataReceived += dataReceived;
            }

            return rv;
        }

        /// <summary>
        /// Close the port.
        /// </summary>
        internal void Close()
        {
            Tracing.TraceLine("Serial.close",TraceLevel.Info);
            // Stop the release timer first, and without waiting on the dispatch lock: a key
            // handler can be mid-dispatch on another thread.
            if (reader != null)
            {
                reader.Stop();
                reader = null;
            }
            if (knobPort != null) knobPort.Dispose();
        }

        /// <summary>
        /// One open's worth of input: the #520 filter, its release timer, and the lock that keeps
        /// dispatch in order between the port's thread and the timer's.
        /// </summary>
        private sealed class Reader
        {
            private readonly FlexControl parent;
            private readonly object gate = new object();
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly KnobInputFilter filter;
            private readonly System.Threading.Timer releaseTimer;
            private volatile bool stopped;

            internal Reader(FlexControl p)
            {
                parent = p;
                filter = new KnobInputFilter(note => Tracing.TraceLine("Serial.filter:" + note, TraceLevel.Info));
                releaseTimer = new System.Threading.Timer(_ => onTimer(), null,
                    System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }

            /// <summary>Tokens decoded from one read. They arrived together, so they share a time.</summary>
            internal void Take(List<string> tokens)
            {
                lock (gate)
                {
                    if (stopped) return;
                    // Stamped inside the lock, so the timer can never have decided a later instant.
                    double now = clock.Elapsed.TotalMilliseconds;
                    foreach (string token in tokens)
                    {
                        if (KnobInputFilter.IsDeviceReport(token))
                        {
                            Tracing.TraceLine("Serial:device report kept:" + token, TraceLevel.Info);
                        }
                        dispatch(filter.Accept(token, now));
                    }
                    rearm(now);
                }
            }

            internal void Stop()
            {
                stopped = true;
                releaseTimer.Dispose();
            }

            private void onTimer()
            {
                lock (gate)
                {
                    if (stopped) return;
                    double now = clock.Elapsed.TotalMilliseconds;
                    dispatch(filter.Release(now));
                    rearm(now);
                }
            }

            private void rearm(double now)
            {
                double? due = filter.NextDueMs;
                long wait = (due == null)
                    ? System.Threading.Timeout.Infinite
                    : Math.Max(1L, (long)Math.Ceiling(due.Value - now));
                try { releaseTimer.Change(wait, System.Threading.Timeout.Infinite); }
                catch (ObjectDisposedException) { } // closed underneath us
            }

            private void dispatch(IReadOnlyList<string> tokens)
            {
                foreach (string token in tokens) parent.OnKnobOutput(token);
            }
        }
        private Reader reader;

        private class bufType
        {
            private StringBuilder buf;
            private const char delim = ';';
            private SerialPort dev;

            public bufType(SerialPort port)
            {
                dev = port;
                buf = new StringBuilder();
            }

            public void Add()
            {
                int len = dev.BytesToRead;
                char[] cBuf = new char[len];
                dev.Read(cBuf, 0, len);
                // Remove 0x00
                for(int i = 0; i < cBuf.Length; i++)
                {
                    if (cBuf[i] != 0x0) buf.Append(cBuf[i]);
                }
            }

            /// <summary>
            /// Read a command w/o the delimiter.
            /// </summary>
            /// <returns>a string containing the first command w/o delimiter, or the empty string.</returns>
            public string Read()
            {
                StringBuilder rv = new StringBuilder();
                for(int i = 0; i < buf.Length; i++)
                {
                    if (buf[i] == delim)
                    {
                        if (i == 0) buf.Remove(0, 1);
                        else
                        {
                            rv.Append(buf.ToString(0, i));
                            buf.Remove(0, i + 1);
                            break;
                        }
                    }
                }
                return rv.ToString();
            }
        }
        private bufType buf;

        private void dataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            // Every token goes through the #520 filter rather than straight to the handlers: a
            // backlog replayed at open or landed by a stalled read is discarded, a phantom step
            // from a press is dropped, and a device report such as F0304 is always passed on.
            Reader r = reader;
            if (r == null) return;
            buf.Add();
            var tokens = new List<string>();
            string data;
            while ((data = buf.Read()) != "")
            {
                tokens.Add(data);
            }
            if (tokens.Count > 0) r.Take(tokens);
        }
    }
}
