using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Escapes;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Interrupt handler for Icom-style commands.
    /// </summary>
    internal class IcomIhandler
    {
        public class SyncData
        {
            public bool ClearToSend = false;
            public ResponseItem SyncCommand = null; // command to sync with
        }
        public SyncData SyncItem;

        public ResponseItem LookupCommand(byte[] bytes, int pos, int len)
        {
            ResponseItem r = new ResponseItem(bytes, pos, len);
            int id;
            try
            {
                // This can fail if interrupted.
                id = Array.BinarySearch(ResponseActions, r, mySort);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(ex.Message, TraceLevel.Error);
                return null;
            }
            return (id >= 0) ? ResponseActions[id] : null;
        }

        private AllRadios theRig;
        private byte[] commandHdr;
        private int commandHdrLen;
        private byte commandTerm;
        private class bufData
        {
            private const int BufSize = 1024;
            public byte[] Buf;
            public int BufLen;
            public bufData()
            {
                Buf = new byte[BufSize];
                BufLen = 0;
            }

            public void Append(byte[] bytes, int id, int len)
            {
                int copyLen = Math.Min((BufSize - BufLen), len);
                if (copyLen > 0)
                {
                    Array.ConstrainedCopy(bytes, id, Buf, BufLen, copyLen);
                    BufLen += copyLen;
                }
            }
        }

        private bufData currentBuf = null;
        private Queue q;
        private Thread qThread;

        /// <summary>
        /// Interrupt handler
        /// </summary>
        /// <param name="bytes">data from the rig</param>
        /// <param name="len">data length</param>
        private void IHandler(byte[] bytes, int len)
        {
            if (Tracing.TheSwitch.Level == TraceLevel.Verbose) Tracing.TraceLine("IHandler:" + Escapes.Escapes.Decode(bytes, len));
            //else Tracing.TraceLine("IHandler len:" + len.ToString());
            // Ignore this if null data.
            if (len == 0) return;

            // Add data to the buffer.
            currentBuf.Append(bytes, 0, len);

            // See if there's an end character.
            int rspLen = Array.LastIndexOf<byte>(currentBuf.Buf, commandTerm) + 1;
            // Note rspLen is the length of the complete responses.
            bool queueIt = (rspLen > 0);

            // Enqueue the data if at least one command.
            if (queueIt)
            {
                int oldBufLen = currentBuf.BufLen;
                currentBuf.BufLen = rspLen;
                bufData qData = currentBuf;
                q.Enqueue(qData);
                currentBuf = new bufData();
                // Copy any remaining data to the new buffer.
                if (rspLen < oldBufLen) currentBuf.Append(qData.Buf, rspLen, (oldBufLen - rspLen));
            }

            if (theRig.sendingOutput != AllRadios.CommandReporting.none)
            {
                // main program handles/receives output.
                StringBuilder sb = new StringBuilder(len);
                for (int i = 0; i < len; i++)
                {
                    sb.Append((char)bytes[i]);
                }
                theRig.Callouts.safeReceiver(sb.ToString());
            }
        }

        private void qHandler()
        {
            Tracing.TraceLine("qHandler", TraceLevel.Info);
            try
            {
                // main (endless) loop.
                while (true)
                {
                    bufData qData;
                    while (q.Count == 0)
                    {
                        Thread.Sleep(10);
                    }
                    qData = (bufData)q.Dequeue();

                    int id = 0;
                    // Get complete commands.
                    for (int endID = 0; endID < qData.BufLen; endID++)
                    {
                        if (qData.Buf[endID] == commandTerm)
                        {
                            // Note the terminator isn't passed to the handlers.
                            responseHandler(qData.Buf, id, (endID - id));
                            id = endID + 1;
                        }
                    }
                }
            }
            catch (ThreadAbortException) { Tracing.TraceLine("qHandler abort", TraceLevel.Error); }
            catch (Exception ex)
            {
                Tracing.TraceLine("qHandler exception:" + ex.Message, TraceLevel.Error);
            }
        }

        public delegate void handDel(byte[] bytes, int id, int len);
        /// <summary>
        /// Used to provide a command and it's handler.
        /// </summary>
        public class ResponseItem
        {
            public string Name;
            public byte[] hdr;
            /// <summary>
            /// command's response from the radio.
            /// If the OK command, the default, use either OK or the command itself.
            /// </summary>
            public byte[] Confirmation;
            public handDel handler;
            // Create responseItem for a search
            public ResponseItem(byte[] cmd, int id, int len)
            {
                hdr = new byte[len];
                Array.ConstrainedCopy(cmd, id, hdr, 0, len);
                handler = null;
                Confirmation = null;
            }
            public ResponseItem(Icom.IcomCommand cmd, string nam, handDel h)
            {
                Name = nam;
                hdr = cmd.Command;
                Confirmation = Icom.ICOK.Command; // OK response
                handler = h;
            }
            /// <summary>
            /// (overloaded) response from the rig
            /// </summary>
            /// <param name="cmd">byte array to identify the command</param>
            /// <param name="nam">command name</param>
            /// <param name="h">the handler</param>
            /// <param name="cfm">confirmation command (default is OK command)</param>
            public ResponseItem(Icom.IcomCommand cmd, string nam, handDel h, Icom.IcomCommand cfm)
            {
                hdr = cmd.Command;
                Name = nam;
                Confirmation = (cfm == null) ? null : cfm.Command;
                handler = h;
            }
        }
        /// <summary>
        /// Array of commands and handlers.
        /// </summary>
        private ResponseItem[] ResponseActions;

        private class mySortClass : IComparer
        {
            int IComparer.Compare(object x, object y)
            {
                return CompareBytes(((ResponseItem)x).hdr, ((ResponseItem)y).hdr);
            }

            public int CompareBytes(byte[] x, byte[] y)
            {
                int len = Math.Min(x.Length, y.Length);
                for (int i = 0; i < len; i++)
                {
                    if (x[i] > y[i]) return 1;
                    if (x[i] < y[i]) return -1;
                }
                return 0;
            }
        }
        private IComparer mySort;

        /// <summary>
        /// response character validation table.
        /// 1 means allowed.
        /// </summary>
        private static byte[] validationTable =
        {
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // 00-0f
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // 10-1f
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // 20-2f
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // 30-3f
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // 40-4f
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // 50-5f
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // 60-6f
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // 70-7f
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // 80-8f
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // 90-9f
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // a0-af
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // b0-bf
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // c0-cf
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // d0-df
            1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1, // e0-ef
            0,0,0,0,0,0,0,0,0,0,0,1,0,1,1,1 // allow fb, fd, fe, and ff.
        };
        /// <summary>
        /// Handle a rig's response.
        /// Runs under the qThread.
        /// </summary>
        /// <param name="bytes">buffer containing command</param>
        /// <param name="pos">start of command</param>
        /// <param name="len">command length</param>
        private void responseHandler(byte[] bytes, int pos, int len)
        {
            Tracing.TraceLine("responseHandler:" + Escapes.Escapes.Decode(bytes, pos, len), TraceLevel.Info);
            // We now know power's on and rig sending data.
            theRig.powerOn();
            if (len < (commandHdrLen + 1))
            {
                Tracing.TraceLine("responseHandler short item:" + len.ToString(), TraceLevel.Error);
                return;
            }

            // strip any crud from the response.
            byte[] stripped = new byte[len];
            int strippedLen = 0;
            for (int i = 0; i < len; i++)
            {
                if (validationTable[bytes[pos + i]] == 1)
                {
                    stripped[strippedLen++] = bytes[pos + i];
                }
            }
            bytes = stripped;
            len = strippedLen;
            pos = 0;

            // Get the actual response position and length.
            int respPos = pos + commandHdrLen;
            int respLen = len - commandHdrLen;

            // quit if echoed from the rig.
            if (commandHdr[commandHdrLen - 2] == bytes[pos + commandHdrLen - 2])
            {
                return;
            }

            // Check for a possible synchronous command, awaited response.
            bool isResponse = false;
            bool OKResponseAllowed = false;
            ResponseItem r = null;
            lock (SyncItem)
            {
                r = SyncItem.SyncCommand;
                if (r != null)
                {
                    OKResponseAllowed = (r.Confirmation[0] == 0xfb);
                    // If OK allowed and response is OK command, we're done.
                    if (OKResponseAllowed && (bytes[respPos] == 0xfb))
                    {
                        SyncItem.ClearToSend = true;
                        Tracing.TraceLine("responseHandler:OK Response = true", TraceLevel.Info);
                        return;
                    }

                    byte[] test = new byte[respLen];
                    Array.ConstrainedCopy(bytes, respPos, test, 0, test.Length);
                    // indicate clear-to-send if awaited.
                    if (OKResponseAllowed)
                    {
                        // OK allowed, but not received. allow the command itself.
                        isResponse = (((mySortClass)mySort).CompareBytes(test, r.hdr) == 0);
                        if (!isResponse)
                        {
                            // We need to get the responseItem for the command received.
                            r = LookupCommand(bytes, respPos, respLen);
                        }
                    }
                    else
                    {
                        // OK wasn't a valid response.
                        isResponse = (((mySortClass)mySort).CompareBytes(test, r.Confirmation) == 0);
                        r = LookupCommand(bytes, respPos, respLen);
                    }
                    // We only want to set ClearToSend to true here.
                    if (isResponse) SyncItem.ClearToSend = true;
                    Tracing.TraceLine("responseHandler:isResponse = " + isResponse.ToString(), TraceLevel.Info);
                }
                else
                {
                    Tracing.TraceLine("responseHandler:no sync command", TraceLevel.Info);
                    r = LookupCommand(bytes, respPos, respLen);
                }
            } // unlock SyncItem on exit.

            // if found, execute routine
            if ((r != null) && (r.handler != null))
            {
                try
                {
                    // This includes the header, and excludes the terminator byte.
                    Tracing.TraceLine("response " + r.Name + ' ' +
                        len.ToString() + ' ' + Escapes.Escapes.Decode(bytes, pos, len), TraceLevel.Info);
                    r.handler(bytes, pos, len);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine(r.Name + ':' + ex.Message, TraceLevel.Error);
                }
            }
        }

        /// <summary>
        /// Start the command handler thread.
        /// </summary>
        public void Start()
        {
            Tracing.TraceLine("IcomIHandler start", TraceLevel.Info);
            qThread = new Thread(new ThreadStart(qHandler));
            qThread.Name = "qHandler";
            try
            {
                qThread.Start();
                Thread.Sleep(0);
            }
            catch (Exception ex)
            { Tracing.TraceLine("IcomIHandler start:" + ex.Message, TraceLevel.Error); }
        }

        /// <summary>
        /// Stop the command processing thread.
        /// </summary>
        public void Stop()
        {
            Tracing.TraceLine("IcomIHandler stop", TraceLevel.Info);
            try { if (qThread.IsAlive) qThread.Abort(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("IcomIHandler stop qThread exception:" + ex.Message, TraceLevel.Error);
            }
        }

        public IcomIhandler(AllRadios rig, ResponseItem[] items, byte[] hdr, byte term)
        {
            Tracing.TraceLine("IcomIHandler:" + items.Length.ToString() + " items", TraceLevel.Info);
            theRig = rig;
            mySort = new mySortClass();
            ResponseActions = items;
            Array.Sort(ResponseActions, mySort);
            commandHdr = hdr;
            commandHdrLen = hdr.Length;
            commandTerm = term;
            rig.IBytesHandler = IHandler;
            q = Queue.Synchronized(new Queue());
            currentBuf = new bufData();
            SyncItem = new SyncData();
        }
    }
}
