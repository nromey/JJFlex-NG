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

            if (rv)
            {
                // A fresh pump, so a fresh #520 filter, at EVERY open: the device replays
                // whatever was done to it while the port was closed, and Noel's buffer ruling
                // applies to each open. The handler is bound to THIS open's pump and port.
                KnobInputPump openPump = new KnobInputPump(
                    token => parent.OnKnobOutput(token),
                    note: note => Tracing.TraceLine("Serial.filter:" + note, TraceLevel.Info));
                SerialPort openPort = knobPort;
                pump = openPump;
                knobPort.DataReceived += (sender, e) => dataReceived(openPort, openPump);
            }

            return rv;
        }

        /// <summary>
        /// Close the port.
        /// </summary>
        internal void Close()
        {
            Tracing.TraceLine("Serial.close",TraceLevel.Info);
            // Stop first: it waits for any dispatch already in progress, acts on a real step still
            // held, and nothing from this open is dispatched after it returns (#520).
            KnobInputPump closing = pump;
            pump = null;
            if (closing != null) closing.Stop();
            if (knobPort != null) knobPort.Dispose();
        }

        private KnobInputPump pump;

        private static void dataReceived(SerialPort port, KnobInputPump openPump)
        {
            // Every token goes through the #520 filter rather than straight to the handlers: a
            // burst of three or more replayed at open or landed by a stalled read is discarded,
            // and a device report such as F0304 is always passed on.
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
