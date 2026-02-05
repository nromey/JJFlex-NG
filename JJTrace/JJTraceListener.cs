using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace JJTrace
{
    class JJTraceListener : TraceListener
    {
        private FileStream theFile;
        private StreamWriter theWriter;
        private bool _pause;
        private Mutex theLock;
        private string fileName;

        public JJTraceListener(string fName)
        {
            theLock = new Mutex();
            fileName = fName;
            theFile = new FileStream(fName, FileMode.Create, FileAccess.Write, FileShare.Read);
            theWriter = new StreamWriter(theFile);
            _pause = false;
        }

        public override void Write(string message)
        {
            theLock.WaitOne();
            if (!_pause)
            {
                try
                {
                    if (NeedIndent) WriteIndent();
                    theWriter.Write(message);
                }
                catch { Close(); }
            }
            theLock.ReleaseMutex();
        }

        public override void WriteLine(string message)
        {
            Write(message + "\r\n");
        }

        public override void Flush()
        {
            theLock.WaitOne();
            if (!_pause)
            {
                try { theWriter.Flush(); }
                catch { Close(); }
            }
            theLock.ReleaseMutex();
        }

        public override void Close()
        {
            theLock.WaitOne();
            if (!_pause)
            {
                try
                {
                    theWriter.Dispose();
                    theFile.Dispose();
                }
                catch { }
            }
            theLock.ReleaseMutex();
        }

        public bool Pause()
        {
            theLock.WaitOne();
            bool rv = _pause;
            if (!_pause)
            {
                Close();
                _pause = true;
            }
            theLock.ReleaseMutex();
            return rv;
        }

        public void Resume()
        {
            theLock.WaitOne();
            if (_pause)
            {
                try
                {
                    theFile = new FileStream(fileName, FileMode.Append, FileAccess.Write, FileShare.Read);
                    theWriter = new StreamWriter(theFile);
                    _pause = false;
                }
                catch { }
            }
            theLock.ReleaseMutex();
        }
    }
}
