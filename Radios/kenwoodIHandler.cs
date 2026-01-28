using System;
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
    /// Interrupt handler for kenwood-style commands.
    /// </summary>
    internal class KenwoodIhandler
    {
        private AllRadios theRig;
        private class bufData
        {
            private const int nBufs = 2;
            public string[] buf;
            public int inBufID, outBufID;
            private int outCC;
            public int outCharCount
            {
                get { return Thread.VolatileRead(ref outCC); }
                set { Thread.VolatileWrite(ref outCC, value); }
            }
            public Mutex bufLock;
            public bufData()
            {
                buf = new string[nBufs];
                for (int i = 0; i < nBufs; i++) buf[i] = "";
                inBufID = 0;
                outBufID = inBufID;
                outCharCount = 0;
                bufLock = new Mutex();
            }
            public int nextID(int id)
            {
                return (id + 1) % nBufs;
            }
        }
        private bufData pool;

        private Thread poolThread;

        /// <summary>
        /// Interrupt handler
        /// </summary>
        /// <param name="str">data from the rig</param>
        private void IHandler(string str)
        {
            try
            {
                Tracing.TraceLine("IHandler:" + Escapes.Escapes.Decode(str), TraceLevel.Info);
                // Ignore this if null data.
                if ((str == "") || (str[0] == (char)0)) return;
                pool.bufLock.WaitOne();
                // Wait until poolHandler has consumed the buffer.
                while (pool.outCharCount != 0)
                {
                    Tracing.TraceLine("IHandler:not consumed", TraceLevel.Verbose);
                    pool.bufLock.ReleaseMutex();
                    Thread.Sleep(10);
                    pool.bufLock.WaitOne();
                }
                // poolHandler is finished.
                int id = pool.inBufID;
                // Add the new data to the buffer.
                pool.buf[id] += str;
                // Find the end of the last response in the buffer.
                // No data is sent until there's at least one complete response in the buffer.
                for (int i = pool.buf[id].Length - 1; i >= 0; i--)
                {
                    if (pool.buf[id][i] == ';')
                    {
                        // Found it.  Get any ending unterminated chunk.
                        pool.outCharCount = i + 1;
                        // This buffer becomes the output buffer.
                        pool.outBufID = id;
                        pool.inBufID = pool.nextID(id); // new input buffer.
                        // Put any unterminated stuff in the now empty buffer.
                        if (pool.outCharCount < pool.buf[id].Length)
                        {
                            pool.buf[pool.inBufID] = pool.buf[id].Substring(pool.outCharCount);
                        }
                        // else the data ended witha complete entry.
                        // Let poolHandler run.
                        break;
                    }
                }
                pool.bufLock.ReleaseMutex();
                if (theRig.sendingOutput == AllRadios.CommandReporting.raw)
                {
                    // main program handles/receives output.
                    theRig.Callouts.safeReceiver(str);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("IHandler exception:" + ex.Message, TraceLevel.Error);
            }
        }

        private void poolHandler()
        {
            Tracing.TraceLine("poolHandler", TraceLevel.Info);
            string mybuf;
            try
            {
                // main (endless) loop.
                while (true)
                {
                    pool.bufLock.WaitOne();
                    if (pool.outCharCount > 0)
                    {
                        // There's data.  Copy it and release the lock.
                        mybuf = pool.buf[pool.outBufID].Substring(0, pool.outCharCount);
                        Tracing.TraceLine("PoolHandler:" + mybuf, TraceLevel.Verbose);
                        // Clear the buffer now.
                        pool.buf[pool.outBufID] = "";
                        // This tells iHandler() we got the data.
                        pool.outCharCount = 0;
                        pool.bufLock.ReleaseMutex(); // iHandler() can run now.
                        // Process each input item.
                        int startID = 0;
                        int len = 0;
                        foreach (char c in mybuf)
                        {
                            switch (c)
                            {
                                case '?':
                                    startID += len + 1;
                                    len = 0;
                                    Tracing.TraceLine("PoolHandler:? ignored", TraceLevel.Info);
                                    break; // ignore
                                case ';':
                                    if (len >= 2)
                                    {
                                        responseHandler(mybuf.Substring(startID, len));
                                    }
                                    startID += len + 1;
                                    len = 0;
                                    break;
                                default:
                                    // Add the char to the command.
                                    len += 1;
                                    break;
                            }
                        }
                    }
                    else
                    {
                        // iHandler() hasn't posted any data.
                        pool.bufLock.ReleaseMutex();
                        //Tracing.TraceLine("PoolHandler:no data",TraceLevel.Verbose);
                        Thread.Sleep(10);
                    }
                }
            }
            catch (ThreadAbortException) { Tracing.TraceLine("poolThread abort", TraceLevel.Error); }
            catch (Exception ex)
            {
                Tracing.TraceLine("poolThread exception:" + ex.Message, TraceLevel.Error);
            }
        }

        public delegate void handDel(string cmd);
        /// <summary>
        /// Used to provide a command and it's handler.
        /// </summary>
        public class ResponseItem : IComparable<ResponseItem>
        {
            public string hdr;
            public handDel handler;
            public int CompareTo(ResponseItem other)
            {
                return hdr.CompareTo(other.hdr);
            }
            public ResponseItem(string s)
            {
                hdr = s;
                handler = null;
            }
            public ResponseItem(string s, handDel h)
            {
                hdr = s;
                handler = h;
            }
        }
        /// <summary>
        /// Array of commands and handlers.
        /// </summary>
        public ResponseItem[] ResponseActions;

        private void responseHandler(string cmdBuf)
        {
            Tracing.TraceLine("responseHandler:" + cmdBuf, TraceLevel.Info);
            // We want to just ignore a "PS0", telling us that power is off.
            // We won't indicate power on.
            if (cmdBuf == "PS0") return;
            // We now know power's on and rig sending data.
            theRig.powerOn();
            ResponseItem r = new ResponseItem(cmdBuf.Substring(0, 2));
            int id;
            // See if reporting this command directly.
            try
            {
                if ((theRig.sendingOutput == AllRadios.CommandReporting.inputBased) &&
                    (cmdBuf.Substring(0, 2) == theRig.checkString.Substring(0, 2).ToUpper()))
                {
                    theRig.Callouts.safeReceiver(cmdBuf + ";");
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("command reporting failed:" + ex.Message, TraceLevel.Error);
            }
            // This can fail if interrupted.
            try { id = Array.BinarySearch<ResponseItem>(ResponseActions, r); }
            catch (Exception ex)
            {
                Tracing.TraceLine("responseHandler search exception:" + ex.Message, TraceLevel.Error);
                return;
            }
            if ((id >= 0) && (ResponseActions[id].handler != null))
            {
                try
                {
                    Tracing.TraceLine(ResponseActions[id].hdr + ':' + cmdBuf, TraceLevel.Info);
                    ResponseActions[id].handler(cmdBuf);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine(ResponseActions[id].hdr + " exception:" + ex.Message, TraceLevel.Error);
                }
            }
            else
            {
                Tracing.TraceLine("responseHandler:not found, id=" + id.ToString(), TraceLevel.Error);
#if zero
                // The PS response can start with junk.
                if ((cmdBuf.Length > 3) &&
                    (cmdBuf.Substring(cmdBuf.Length - 3, 2) == kcmdPS))
                {
                    contPS(cmdBuf.Substring(cmdBuf.Length - 3));
                }
#endif
            }
        }

        /// <summary>
        /// Start the command handler thread.
        /// </summary>
        public void Start()
        {
            Tracing.TraceLine("KenwoodIHandler start", TraceLevel.Info);
            pool = new bufData();
            poolThread = new Thread(new ThreadStart(poolHandler));
            poolThread.Name = "poolThread";
            try { poolThread.Start(); }
            catch (Exception ex)
            { Tracing.TraceLine("KenwoodIHandler start:" + ex.Message, TraceLevel.Error); }
            Thread.Sleep(0);
        }

        /// <summary>
        /// Stop the command processing thread.
        /// </summary>
        public void Stop()
        {
            Tracing.TraceLine("KenwoodIHandler stop", TraceLevel.Info);
            try { if (poolThread.IsAlive) poolThread.Abort(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("KenwoodIHandler stop exception:" + ex.Message, TraceLevel.Error);
            }
        }

        public KenwoodIhandler(AllRadios rig, ResponseItem[] items)
        {
            Tracing.TraceLine("KenwoodIHandler:" + items.Length.ToString() + " items", TraceLevel.Info);
            theRig = rig;
            ResponseActions = items;
            rig.InterruptHandler = IHandler;
        }
    }
}
