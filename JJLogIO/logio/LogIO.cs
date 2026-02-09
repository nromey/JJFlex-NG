using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.IO;
using System.Threading;

namespace JJLogIO
{
    /// <summary>
    /// log file of strings
    /// </summary>
    public class LogIO
    {
        /// <summary>
        /// JJLogio.dll version
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

        /// <summary>
        /// Name of the file in use
        /// </summary>
        public string fileName;
        protected FileStream fs;
        protected BinaryReader rfs;
        protected BinaryWriter wfs;
        /// <summary>
        /// True if file is open
        /// </summary>
        public bool IsOpen { get; set; }
        /// <summary>
        /// Invalid file pointer
        /// </summary>
        protected const long nullPTR = -1;
        // The cursor points to the current record.
        private long crsr;
        /// <summary>
        /// Current file position, set to nullPTR if invalid.
        /// </summary>
        private long cursor
        {
            get { return crsr; }
            set
            {
                if ((value >= fileHeaderData.Length) && (value < fs.Length))
                    crsr = value;
                else crsr = nullPTR;
            }
        }
        /// <summary>
        /// Current file position.
        /// </summary>
        public long Position
        {
            get { return cursor; }
        }
        /// <summary>
        /// True if end-of-file
        /// </summary>
        public bool EOF { get; set; }

        protected const string recordMagic = "RCRD";
        protected const string DeletedRecordMagic = "RDEL";
        /// <summary>
        /// Data record
        /// </summary>
        protected class recordData
        {
            string magic;
            public uint Length;
            public long prev;
            public long next;
            char[] data;
            public recordData() { }
            /// <summary>
            /// Constructor - record to be added.
            /// </summary>
            /// <param name="prev">previous record pointer</param>
            /// <param name="str">the data</param>
            public recordData(long prev, string str)
            {
                magic = recordMagic;
                Length = (uint)str.Length;
                this.prev = prev;
                next = nullPTR;
                data = str.ToCharArray(0, str.Length);
            }
            /// <summary>
            /// Constructor - record to be inserted.
            /// </summary>
            /// <param name="prev">pointer to previous record</param>
            /// <param name="next">to next record</param>
            /// <param name="str">the data </param>
            public recordData(long prev, long next, string str)
            {
                magic = recordMagic;
                Length = (uint)str.Length;
                this.prev = prev;
                this.next = next;
                data = str.ToCharArray(0, str.Length);
            }
            /// <summary>
            /// true if record is ok
            /// </summary>
            /// <param name="fileSize">file size</param>
            /// <returns>true if good</returns>
            private bool goodRecordHeader(long fileSize)
            {
                if (magic != recordMagic) return false;
                if (!((prev == nullPTR) ||
                     ((prev >= fileHeaderData.Length) &&
                      (prev < fileSize)))) return false;
                if (!((next == nullPTR) ||
                     ((next >= fileHeaderData.Length) &&
                      (next < fileSize)))) return false;
                return true;
            }
            /// <summary>
            /// read a record
            /// </summary>
            /// <param name="rfs">binary file pointer</param>
            /// <param name="justHeader">read just the header if true</param>
            private void Read(BinaryReader rfs, bool justHeader)
            {
                // (overloaded) read just header or entire record.
                // Check the record.
                try
                {
                    magic = new string(rfs.ReadChars(recordMagic.Length));
                    Length = rfs.ReadUInt32();
                    prev = rfs.ReadInt64();
                    next = rfs.ReadInt64();
                    if (!justHeader)
                    {
                        data = rfs.ReadChars((Int32)Length);
                    }
                }
                catch { throw; }
                if (!goodRecordHeader(rfs.BaseStream.Length))
                {
                    throw new CorruptLog(rfs.BaseStream.Position);
                }
            }
            /// <summary>
            /// read a record
            /// </summary>
            /// <param name="rfs">binary file pointer</param>
            public void Read(BinaryReader rfs)
            {
                // (overloaded) read entire record.
                Read(rfs, false);
            }
            /// <summary>
            /// read just the record's header
            /// </summary>
            /// <param name="rfs">binary file pointer</param>
            public void ReadHeader(BinaryReader rfs)
            {
                Read(rfs, true);
            }
            /// <summary>
            /// Read the data
            /// </summary>
            /// <param name="rfs">binary file pointer</param>
            /// <returns>data as a string</returns>
            public string ReadString(BinaryReader rfs)
            {
                Read(rfs);
                return new string(data);
            }
            /// <summary>
            /// Write a record
            /// </summary>
            /// <param name="wfs">binary file</param>
            /// <param name="justHeader">write just the record header if true</param>
            private void Write(BinaryWriter wfs, bool justHeader)
            {
                // (overloaded) write just header or entire record.
                try
                {
                    wfs.Write(magic.ToCharArray(), 0, recordMagic.Length);
                    wfs.Write(Length);
                    wfs.Write(prev);
                    wfs.Write(next);
                    if (!justHeader)
                    {
                        wfs.Write(data, 0, (Int32)Length);
                    }
                }
                catch { throw; }
            }
            /// <summary>
            /// Write a record
            /// </summary>
            /// <param name="wfs">binary file</param>
            public void Write(BinaryWriter wfs)
            {
                // (overloaded) write entire record.
                Write(wfs, false);
            }
            /// <summary>
            /// write just the record's header
            /// </summary>
            /// <param name="wfs">binary file</param>
            public void WriteHeader(BinaryWriter wfs)
            {
                Write(wfs, true);
            }
            /// <summary>
            /// write the header for a deleted record
            /// </summary>
            /// <param name="wfs">binary file</param>
            public void writeDeletedHeader(BinaryWriter wfs)
            {
                magic = DeletedRecordMagic;
                Write(wfs, true);
            }
        }
        // File header
        protected const int Version1 = 1;
        protected const int logVersion = Version1;
        protected const string headerMagic = "JJLG";
        protected class fileHeaderData
        {
            string magic;
            public int version;
            public long first;
            public long last;
            public const int Length = 4 + sizeof(Int32) + 2 * sizeof(long);
            /// <summary>
            /// file header
            /// </summary>
            public fileHeaderData() { }
            /// <summary>
            /// file header
            /// </summary>
            /// <param name="f">first record pointer</param>
            /// <param name="l">last record pointer</param>
            public fileHeaderData(long f, long l)
            {
                magic = headerMagic;
                version = logVersion;
                first = f;
                last = l;
            }
            /// <summary>
            /// check the magic number
            /// </summary>
            /// <returns>true if good</returns>
            public bool GoodMagic()
            {
                return (magic == headerMagic) ? true : false;
            }
            /// <summary>
            /// position the file and read the file header.
            /// check the magic.
            /// </summary>
            /// <param name="rfs">binary file</param>
            public void Read(BinaryReader rfs)
            {
                rfs.BaseStream.Position = 0;
                try
                {
                    magic = new string(rfs.ReadChars(headerMagic.Length));
                    if (!GoodMagic())
                    {
                        throw new NotLog();
                    }
                    version = rfs.ReadInt32();
                    first = rfs.ReadInt64();
                    last = rfs.ReadInt64();
                }
                catch
                {
                    throw new NotLog();
                }
            }
            /// <summary>
            /// position the file and write the header
            /// </summary>
            /// <param name="wfs">binary file</param>
            public void Write(BinaryWriter wfs)
            {
                wfs.BaseStream.Position = 0;
                    // The log header must be in fileHeader.
                    try
                    {
                        wfs.Write(magic.ToCharArray(), 0, magic.Length);
                        wfs.Write(version);
                        wfs.Write(first);
                        wfs.Write(last);
                    }
                    catch
                    {
                        throw;
                    }
            }
        }
        /// <summary>
        /// active file's header
        /// </summary>
        protected fileHeaderData fileHeader;
        /// <summary>
        /// first record's position
        /// </summary>
        public long FirstRecord
        {
            get { return fileHeader.first; }
        }
        /// <summary>
        /// last record's position
        /// </summary>
        public long LastRecord
        {
            get { return fileHeader.last; }
        }
        /// <summary>
        /// true if empty
        /// </summary>
        public bool Empty
        {
            get { return (fileHeader.first == nullPTR); }
        }

        /// <summary>
        /// not a valid log file
        /// </summary>
        public class NotLog : Exception
        {
            /// <summary>
            /// 
            /// </summary>
            public NotLog() : base("Invalid log file.") { }
            /// <summary>
            /// 
            /// </summary>
            /// <param name="fn">file name</param>
            public NotLog(string fn) : base(fn + " is not a valid log file.") { }
        }
        /// <summary>
        /// log is corrupted
        /// </summary>
        public class CorruptLog : Exception
        {
            /// <summary>
            /// 
            /// </summary>
            public CorruptLog() : base("This log file is corrupted.") { }
            /// <summary>
            /// 
            /// </summary>
            /// <param name="pos">file position</param>
            public CorruptLog(long pos) : base("The log is corrupted at offset " + pos) { }
        }
        /// <summary>
        /// file position appears to be bad
        /// </summary>
        public class BadPosition : Exception
        {
            /// <summary>
            /// 
            /// </summary>
            public BadPosition() : base("Bad file position.") { }
            /// <summary>
            /// 
            /// </summary>
            /// <param name="pos">file position</param>
            public BadPosition(long pos) : base("Bad file position " + pos) { }
        }
        /// <summary>
        /// attempt to read past end-of-file
        /// </summary>
        public class PastEOF : Exception
        {
            /// <summary>
            /// 
            /// </summary>
            public PastEOF() : base("Attempt to read past EOF.") { }
        }
        /// <summary>
        /// file is already open
        /// </summary>
        public class AlreadyOpen : Exception
        {
            /// <summary>
            /// 
            /// </summary>
            public AlreadyOpen() : base("The file is already open.") { }
        }

        /// <summary>
        /// log i/o data
        /// </summary>
        public LogIO()
        {
            IsOpen = false;
        }
        /// <summary>
        /// log i/o data
        /// </summary>
        /// <param name="fn">file name</param>
        public LogIO(string fn)
        {
            IsOpen = false;
            try { Open(fn); }
            catch { throw; }
        }

        /// <summary>
        /// open the log. create a new log if doesn't exist.
        /// </summary>
        /// <param name="fn">file name</param>
        public void Open(string fn)
        {
            if (IsOpen) throw new AlreadyOpen();
            fileName = fn;
            EOF = false;
            try
            {
                fs = new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                rfs = new BinaryReader(fs);
                wfs = new BinaryWriter(fs);
            }
            catch
            {
                throw;
            }
            IsOpen = true;
            if (fs.Length>0)
            {
                // The file exists.
                fileHeader = new fileHeaderData();
                try { fileHeader.Read(rfs); }
                catch {
                    try { Close(); }
                    catch { }
                    throw new NotLog(fs.Name);
                }
            }
            else
            {
                // Create a new file.
                fileHeader = new fileHeaderData(nullPTR, nullPTR);
                try { fileHeader.Write(wfs); }
                catch {
                    try { Close(); }
                    catch { }
                    throw;
                }
            }
            cursor = fileHeader.first;
            if (cursor == nullPTR) EOF = true;
        }

        /// <summary>
        /// close the log file
        /// </summary>
        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            try {
                fs.Close();
                wfs.Dispose();
                rfs.Dispose();
                fs.Dispose();
            }
            catch { throw; }
        }

        /// <summary>
        /// seek to the first record
        /// </summary>
        /// <returns>file position</returns>
        public long SeekToFirst()
        {
            cursor = fileHeader.first;
            if (cursor != nullPTR)
            {
                fs.Position = cursor;
                EOF = false;
            }
            else EOF = true;
            return cursor;
        }

        /// <summary>
        /// seek to the last record
        /// </summary>
        /// <returns>file position</returns>
        public long SeekToLast()
        {
            // Seek to the last record.
            cursor = fileHeader.last;
            if (cursor != nullPTR)
            {
                fs.Position = cursor;
                EOF = false;
            }
            else EOF = true;
            return cursor;
        }

        /// <summary>
        /// seek to this position
        /// </summary>
        /// <param name="pos">file position</param>
        /// <returns>file position</returns>
        public long SeekToPosition(long pos)
        {
            recordData rec = new recordData();
            // Read the record's header.
            try {
                fs.Position = pos;
                rec.ReadHeader(rfs);
            }
            catch { throw new BadPosition(pos); }
            EOF = false;
            cursor = pos;
            fs.Position = pos;
            return cursor;
        }

        /// <summary>
        /// seek to the previous record
        /// </summary>
        /// <returns>file position</returns>
        public long SeekToPrevious()
        {
            // Seek to the record before the cursor.
            recordData rec = new recordData();
            try
            {
                fs.Position = cursor;
                rec.ReadHeader(rfs);
            }
            catch { throw; }
            return SeekToPosition(rec.prev);
        }

        /// <summary>
        /// seek to the next record
        /// </summary>
        /// <returns>file position</returns>
        public long SeekToNext()
        {
            // Seek to the record after the cursor.
            recordData rec = new recordData();
            try
            {
                fs.Position = cursor;
                rec.ReadHeader(rfs);
            }
            catch { throw; }
            return SeekToPosition(rec.next);
        }

        /// <summary>
        /// read a log record
        /// </summary>
        /// <returns>the data as a string</returns>
        public string Read()
        {
            if (EOF) throw new PastEOF();
            if (cursor == nullPTR) throw new BadPosition(cursor);
            fs.Position = cursor;
            recordData rec = new recordData();
            string str = null;
            try { str = rec.ReadString(rfs); }
            catch { throw; }
            cursor = rec.next;
            if (cursor == nullPTR) EOF = true;
            else EOF = false;
            return str;
        }

        /// <summary>
        /// append a new log record
        /// </summary>
        /// <param name="str">the data</param>
        public void Append(string str)
        {
            // Append a record.
            // We're positioned at the new record.
            fs.Position = fs.Length;
            crsr = fs.Position; // bypass cursor property.
            recordData rec = new recordData(fileHeader.last, str);
            try { rec.Write(wfs); }
            catch { throw; }
            // fileHeader.last is null means this is the first record, (i.e.) a new file.
            if (fileHeader.last != nullPTR)
            {
                // Update the previous last record's header.
                fs.Position = fileHeader.last;
                try { rec.ReadHeader(rfs); }
                catch { throw; }
                fs.Position = fileHeader.last;
                // Last record must have a null next pointer.
                if (rec.next != nullPTR)
                {
                    throw new CorruptLog();
                }
                rec.next = cursor;
                try { rec.WriteHeader(wfs); }
                catch { throw; }
            }
            fileHeader.last = cursor;
            if (fileHeader.first == nullPTR)
            {
                fileHeader.first = cursor;
            }
            try { fileHeader.Write(wfs); }
            catch { throw; }
            fs.Position = cursor;
            EOF = false;
        }

        /// <summary>
        /// update the log record we're positioned at.
        /// </summary>
        /// <param name="str">the data</param>
        public void Update(string str)
        {
            // Update the record at the cursor.
            // If successful, position at the updated record.
            recordData rec = new recordData();
            try {
                fs.Position = cursor;
                rec.Read(rfs);
            }
            catch { throw new BadPosition(fs.Position); }
            if (str.Length <= rec.Length)
            {
                // We can update in place.
                fs.Position = cursor;
                recordData rec2 = new recordData(rec.prev, rec.next, str);
                try { rec2.Write(wfs); }
                catch { throw; }
            }
            else
            {
                // Put the updated record at the end.
                // May need to update the header too.
                bool updateHeader = false;
                long newcursor = fs.Length;
                fs.Position = newcursor;
                recordData rec2 = new recordData(rec.prev, rec.next, str);
                try { rec2.Write(wfs); }
                catch { throw; }
                // Update the previous record's next pointer if there is one.
                if (rec.prev != nullPTR)
                {
                    try
                    {
                        fs.Position = rec.prev;
                        // Be sure to just read the header!
                        rec2.ReadHeader(rfs);
                        rec2.next = newcursor;
                        fs.Position = rec.prev;
                        rec2.WriteHeader(wfs);
                    }
                    catch { throw; }
                }
                else {
                    // This must be the first record.
                    if (cursor != fileHeader.first) {
                        throw new CorruptLog(cursor);
                    }
                    else {
                        fileHeader.first = newcursor;
                        updateHeader = true;
                    }
                }
                // Fix the previous position of the next record if there is one.
                if (rec.next != nullPTR)
                {
                    try
                    {
                        fs.Position = rec.next;
                        // Be sure to just read the header!
                        rec2.ReadHeader(rfs);
                        rec2.prev = newcursor;
                        fs.Position = rec.next;
                        rec2.WriteHeader(wfs);
                    }
                    catch { throw; }
                }
                else {
                    // This must be the last record.
                    if (cursor != fileHeader.last) {
                        throw new CorruptLog(cursor);
                    }
                    else {
                        fileHeader.last = newcursor;
                        updateHeader = true;
                    }
                }
                if (updateHeader)
                {
                    try { fileHeader.Write(wfs); }
                    catch { throw; }
                }
                // Finally, mark the updated record as deleted.
                try
                {
                    fs.Position = cursor;
                    rec.writeDeletedHeader(wfs);
                }
                catch { throw; }
                cursor = newcursor;
            }
            fs.Position = cursor;
            EOF = false;
        }

        /// <summary>
        /// Delete the record we're positioned at.
        /// </summary>
        public void Delete()
        {
            recordData rec = new recordData();
            bool updateHeader = false;
            try
            {
                fs.Position = cursor;
                rec.Read(rfs);
            }
            catch { throw new BadPosition(fs.Position); }
            recordData rec2 = new recordData();
            // Update the prior record.
            if (rec.prev != nullPTR)
            {
                try
                {
                    fs.Position = rec.prev;
                    rec2.ReadHeader(rfs);
                    fs.Position = rec.prev;
                    rec2.next = rec.next;
                    rec2.WriteHeader(wfs);
                }
                catch { throw; }
            }
            else
            {
                // Must be the first record.
                if (cursor != fileHeader.first)
                {
                    throw new CorruptLog();
                }
                fileHeader.first = rec.next;
                updateHeader = true;
            }
            // Update the next record's previous pointer.
            if (rec.next != nullPTR)
            {
                try
                {
                    fs.Position = rec.next;
                    rec2.ReadHeader(rfs);
                    fs.Position = rec.next;
                    rec2.prev = rec.prev;
                    rec2.WriteHeader(wfs);
                }
                catch { throw; }
            }
            else
            {
                // This must be the last record.
                if (cursor != fileHeader.last)
                {
                    throw new CorruptLog();
                }
                else
                {
                    fileHeader.last = rec.prev;
                    updateHeader = true;
                }
            }
            // Update the header if necessary.
            if (updateHeader)
            {
                try { fileHeader.Write(wfs); }
                catch { throw; }
            }
            // Finally, mark the record as deleted.
            try
            {
                fs.Position = cursor;
                rec.writeDeletedHeader(wfs);
            }
            catch { throw; }
            // Position to the next record.
            EOF = (rec.next == nullPTR) ? true : false;
            cursor = rec.next;
            if (!EOF) {
                try { fs.Position = rec.next; }
                catch { throw; }
            }
            else if (!Empty)
            {
                try { fs.Position = rec.prev; }
                catch { throw; }
            }
        }
    }
}
