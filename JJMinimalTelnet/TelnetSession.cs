using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using JJTrace;

namespace JJMinimalTelnet
{
    public class TelnetSession
    {
        private TelnetConnection tc;
        private Thread readThread;
        private char lastCharOfNewline;

        /// <summary>
        /// (overloaded) new connection
        /// </summary>
        /// <param name="hostName">string hostname</param>
        /// <param name="port">int port</param>
        public TelnetSession(string hostName, int port)
        {
            Tracing.TraceLine("TelnetSession" + hostName + ' ' + port.ToString(), TraceLevel.Info);
            setupSession(hostName, port);
        }
        public TelnetSession(string hostName)
        {
            Tracing.TraceLine("TelnetSession" + hostName, TraceLevel.Info);
            setupSession(hostName, 23);
        }
        private void setupSession(string hostName, int port)
        {
            lastCharOfNewline = TelnetConnection.TCNewline[TelnetConnection.TCNewline.Length - 1];
            tc = new TelnetConnection(hostName, port);
        }

        public void Close()
        {
            Tracing.TraceLine("TelnetSession close", TraceLevel.Info);
            closing = true;

            if (readThread != null)
            {
                // Give it up to a second to close.
                int sanity = 40;
                while ((sanity-- > 0) & readThread.IsAlive) { Thread.Sleep(25); }
                if (sanity == 0) Tracing.TraceLine("TelnetSession close time exceeded", TraceLevel.Error);
            }

            if (tc != null) tc.Close();
            Tracing.TraceLine("TelnetSession close done", TraceLevel.Info);
        }

        public string LoginString
        {
            get { return (tc != null) ? tc.LoginString : ""; }
        }
        /// <summary>
        /// Login
        /// </summary>
        /// <param name="Username">optional user name string</param>
        /// <param name="Password">optional password string</param>
        /// <param name="LoginTimeOutMs">int timeout in ms, 0 means use default.</param>
        /// <param name="loginError">login error message returned</param>
        /// <returns>true on success</returns>
        /// <remarks>
        /// A successful return doesn't mean you necessarily got logged in.
        /// </remarks>
        public bool Login(string Username, string Password, int LoginTimeOutMs,
            out string loginError)
        {
            Tracing.TraceLine("TelnetSession Login:" + Username + ' ' + Password + ' ' + LoginTimeOutMs.ToString(), TraceLevel.Info);
            closing = false;
            bool rv = tc.Login(Username, Password, LoginTimeOutMs, out loginError);

            if (rv)
            {
                readThread = new Thread(readProc);
                readThread.Name = "Reader";
                readThread.Start();
            }
            return rv;
        }

        /// <summary>
        /// See if connected.
        /// </summary>
        public bool IsConnected
        {
            get { return (tc != null) ? tc.IsConnected : false; }
        }

        /// <summary>
        /// Write a string appending a newline.
        /// </summary>
        /// <param name="cmd">string to write</param>
        public void WriteLine(string cmd)
        {
            Tracing.TraceLine("TelnetSession WriteLine:" + cmd, TraceLevel.Info);
            tc.WriteLine(cmd);
        }

        /// <summary>
        /// Write a string
        /// </summary>
        /// <param name="cmd">string to write</param>
        public void Write(string cmd)
        {
            Tracing.TraceLine("TelnetSession Write:" + cmd, TraceLevel.Info);
            tc.Write(cmd);
        }

        private class queueStuff
        {
            public Mutex Lock = new Mutex();
            public bool Handled = true; // interrupt handled.
            public Queue Q = new Queue();
        }
        private queueStuff intQueue;
        private string buffer = "";
        private bool closing;

        public delegate void StringDel();
        public event StringDel StringEvent;
        private void onStringEvent()
        {
            if (StringEvent != null)
            {
                Tracing.TraceLine("TelnetSession StringEvent", TraceLevel.Info);
                StringEvent();
            }
            else Tracing.TraceLine("TelnetSession StringEvent not setup", TraceLevel.Info);
        }

        /// <summary>
        /// Read characters only when a StringEvent has occurred.
        /// </summary>
        /// <returns>the string read, including the newline.</returns>
        public List<string> Read()
        {
            Tracing.TraceLine("TelnetSession Read", TraceLevel.Info);
            List<string> rv = new List<string>();
            string buf = buffer;
            intQueue.Lock.WaitOne();
            // Get all queued items
            while (intQueue.Q.Count > 0)
            {
                buf += intQueue.Q.Dequeue();
            }
            intQueue.Handled = true;
            intQueue.Lock.ReleaseMutex();
            // parse out the strings
            int len = buf.IndexOf(TelnetConnection.TCNewline) + TelnetConnection.TCNewline.Length;
            if (len >= TelnetConnection.TCNewline.Length)
            {
                // at least one string
                int startID = 0;
                do
                {
                    string str = buf.Substring(startID, len);
                    rv.Add(str);
                    Tracing.TraceLine("telnetSession read adding:" + Escapes.EscapeHelper.Decode(str), TraceLevel.Verbose);
                    startID += len;
                } while ((len = buf.Substring(startID).IndexOf(TelnetConnection.TCNewline) +
                    TelnetConnection.TCNewline.Length) >= TelnetConnection.TCNewline.Length);
                // Save rest of the data
                if (startID < buf.Length) buffer = buf.Substring(startID);
                else buffer = "";
                Tracing.TraceLine("telnetSession Read buffer:" + buffer, TraceLevel.Verbose);
            }
            else
            {
                Tracing.TraceLine("TelnetSession Read:no EOL in read", TraceLevel.Error);
            }
            return rv;
        }

        private void readProc()
        {
            Tracing.TraceLine("TelnetSession readProc started", TraceLevel.Info);

            intQueue = new queueStuff();
            buffer = "";

            // Show the login message
            if (!string.IsNullOrEmpty(tc.LoginString))
            {
                intQueue.Lock.WaitOne();
                intQueue.Q.Enqueue(tc.LoginString + Environment.NewLine);
                intQueue.Lock.ReleaseMutex();
                onStringEvent();
            }

            while (tc.IsConnected && !closing)
            {
                string str = tc.Read();
                if (!string.IsNullOrEmpty(str))
                {
                    Tracing.TraceLine("readProc read:" + Escapes.EscapeHelper.Decode(str), TraceLevel.Verbose);
                    intQueue.Lock.WaitOne();
                    intQueue.Q.Enqueue(str);
                    if (str.IndexOf(lastCharOfNewline) != -1)
                    {
                        // wait on any prior interrupt
                        while (!intQueue.Handled)
                        {
                            intQueue.Lock.ReleaseMutex();
                            Thread.Sleep(25);
                            intQueue.Lock.WaitOne();
                        }
                        intQueue.Handled = false;
                        onStringEvent();
                    }
                    intQueue.Lock.ReleaseMutex();
                }
            }
            Tracing.TraceLine("TelnetSession readProc finished", TraceLevel.Info);
        }
    }
}
