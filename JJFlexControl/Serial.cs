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
            KnobInputPump openPump = null;
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

                // A fresh pump, so a fresh #520 wait for quiet, at EVERY open: the device replays
                // whatever was done to it while the port was closed, and Noel's ruling throws that
                // away at each open. Made, and its handler attached, BEFORE Open: the wait counts
                // from the open call, so nothing can arrive before its clock starts, and a handler
                // attached after Open could miss the first callback, leaving the start of the
                // replay unread in the buffer until the knob's next byte, after the wait. The
                // handler is bound to THIS open's pump and port.
                Stopwatch openClock = Stopwatch.StartNew();
                openPump = new KnobInputPump(
                    token => parent.OnKnobOutput(token),
                    () => openClock.Elapsed.TotalMilliseconds,
                    note => Tracing.TraceLine("Serial.filter:" + note, TraceLevel.Info));
                SerialPort openPort = knobPort;
                KnobInputPump boundPump = openPump;
                knobPort.DataReceived += (sender, e) => dataReceived(openPort, boundPump);
                knobPort.Open();
                Tracing.TraceLine("Serial.Open: the port opened " + openClock.Elapsed.TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + " ms after the open call; the knob's arrival times count from the open call", TraceLevel.Info);
            }
            catch(Exception ex)
            {
                Tracing.TraceLine("Serial:Open exception:" + ex.Message, TraceLevel.Error);
                if (knobPort != null) knobPort.Dispose();
                rv = false;
            }

            if (rv) pump = openPump;

            return rv;
        }

        /// <summary>
        /// Close the port.
        /// </summary>
        internal void Close()
        {
            Tracing.TraceLine("Serial.close",TraceLevel.Info);
            // Stop first: it waits for any dispatch already in progress, and nothing from this open
            // is dispatched after it returns (#520). Nothing is held, so nothing is lost.
            KnobInputPump closing = pump;
            pump = null;
            if (closing != null) closing.Stop();
            if (knobPort != null) knobPort.Dispose();
        }

        private KnobInputPump pump;

        private static void dataReceived(SerialPort port, KnobInputPump openPump)
        {
            // Every token goes through the #520 wait for quiet rather than straight to the
            // handlers: what the knob replays when the port opens is ignored until the knob has
            // gone quiet, every input code after that is acted on at once, and a device report
            // such as F0304 is always passed on.
            string text;
            try
            {
                text = port.ReadExisting();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IOException)
            {
                return; // the port closed underneath this callback
            }
            openPump.TakeText(text);
        }
    }
}
