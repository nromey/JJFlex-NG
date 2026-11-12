using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using HamBands;
using JJTrace;

namespace Radios
{
    public class PanRanges
    {
        [Flags]
        public enum PanRangeStates
        {
            user = 0x1,
            saved = 0x2,
            permanent = 0x4,
            temp = 0x8,
        }
        public class PanRange
        {
            public ulong Low;
            public ulong High;
            public ulong Width { get { return High - Low; } }
            public uint Generation; // checked by interrupt handler
            public PanRangeStates State;
            public bool Permanent
            {
                get { return ((State & PanRangeStates.permanent) == PanRangeStates.permanent); }
            }
            public bool Saved
            {
                get { return (Permanent || ((State & PanRangeStates.saved) == PanRangeStates.saved)); }
            }
            public bool User
            {
                get { return (State == PanRangeStates.user); }
            }
            public PanRange() { }
            public PanRange(ulong l, ulong h)
            {
                Low = l;
                High = h;
                State = PanRangeStates.user;
            }
            public PanRange(ulong l, ulong h, PanRangeStates s)
            {
                Low = l;
                High = h;
                State = s;
            }
            public override string ToString()
            {
                return Generation.ToString() + ' ' + FlexBase.FormatFreq(Low) + ' ' + FlexBase.FormatFreq(High) + ' ' + State.ToString();
            }
        }
        private Collection<PanRange> ranges;
        public class RangeData
        {
            public PanRange[] Ranges;
        }
        public RangeData Persistent;

        private string directoryName;
        private string fileName { get { return (directoryName == null)? null: directoryName + "\\PanRanges.xml"; } }
        public PanRanges(FlexBase rig, string dir)
        {
            Tracing.TraceLine("PanRanges:" + rig.OperatorName, TraceLevel.Info);
            directoryName = dir;
            ranges = new Collection<PanRange>();
            Persistent = new RangeData();
            if ((fileName == null) || !File.Exists(fileName))
            {
                // provide default ranges and return.
                Tracing.TraceLine("PanRanges:new range data", TraceLevel.Info);
                foreach (Bands.BandItem item in Bands.TheBands)
                {
                    PanRange range = new PanRange(item.Low, item.High);
                    // Make it permanent.
                    range.State = PanRangeStates.permanent;
                    ranges.Add(range);
                }
                write();

                return;
            }

            // Get operator's ranges.
            Stream rangeFile = null;
            try
            {
                rangeFile = File.Open(fileName, FileMode.Open);
                XmlSerializer xs = new XmlSerializer(typeof(RangeData));
                Persistent = (RangeData)xs.Deserialize(rangeFile);
                // Add to the collection.
                foreach (PanRange r in Persistent.Ranges)
                {
                    ranges.Add(r);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("PanRanges exception:" + ex.Message, TraceLevel.Error);
            }
            finally
            {
                if (rangeFile != null) rangeFile.Dispose();
            }
        }

        private void write()
        {
            if (fileName == null) return;
            try
            {
                XmlSerializer xs = new XmlSerializer(typeof(RangeData));
                Persistent.Ranges = ranges.ToArray(); // convert to saved format.
                AtomicFile.Write(fileName, rangeFile => xs.Serialize(rangeFile, Persistent));
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("PanRanges write exception:" + ex.Message, TraceLevel.Error);
            }
        }

        /// <summary>
        /// Insert the range at the beginning.
        /// Nonpermanent ranges entirely contained in the range are removed.
        /// </summary>
        /// <param name="range">a pan range</param>
        public void Insert(PanRange range)
        {
            Tracing.TraceLine("PanRanges.Insert:" + range.ToString(), TraceLevel.Info);
            // Remove any old nonpermanent ranges entirely contained in the new range.
            PanRange rmv;
            do
            {
                rmv = null;
                foreach (PanRange r in ranges)
                {
                    if ((r.Low >= range.Low) && (r.High <= range.High) &
                        !range.Permanent)
                    {
                        rmv = r;
                        break;
                    }
                }
                if (rmv != null)
                {
                    ranges.Remove(rmv);
                    Tracing.TraceLine("PanRanges.Add:removing " + rmv.ToString(), TraceLevel.Info);
                }
            } while (rmv != null);

            // Indicate a Saved range.
            range.State = PanRangeStates.saved;

            // Insert the range.
            ranges.Insert(0, range);
            write();
        }

        /// <summary>
        /// Remove the specified range.
        /// </summary>
        /// <param name="range">a pan range</param>
        public void Remove(PanRange range)
        {
            Tracing.TraceLine("PanRanges.Remove:" + range.ToString(), TraceLevel.Info);
            bool removed = false;
            foreach (PanRange r in ranges)
            {
                if ((r.Low == range.Low) & (r.High == range.High) & 
                    !range.Permanent)
                {
                    ranges.Remove(r);
                    removed = true;
                    break; // should only be one.
                }
            }
            if (removed) write();
            else Tracing.TraceLine("PanRanges.Remove:item not found or permanent", TraceLevel.Error);
        }

        /// <summary>
        /// Find the range for this frequency.
        /// </summary>
        /// <param name="freq">ulong frequency</param>
        /// <returns>a pan range</returns>
        public PanRange Query(ulong freq)
        {
            PanRange rv = null;
            foreach (PanRange range in ranges)
            {
                if ((range.Low <= freq) & (range.High >= freq))
                {
                    rv = range;
                    break;
                }
            }
            return rv;
        }

        /// <summary>
        /// Get the ranges pertaining to this frequency.
        /// </summary>
        /// <param name="freq">ulong frequency</param>
        /// <returns>collection of PanRange</returns>
        public Collection<PanRange> QueryPertinentRanges(ulong freq)
        {
            Collection<PanRange> rv = new Collection<PanRange>();
            foreach (PanRange r in ranges)
            {
                if ((freq >= r.Low) && (freq <= r.High))
                {
                    rv.Add(r);
                }
            }
            return rv;
        }

        /// <summary>
        /// Find the newest, next range.
        /// This will be an entire range, not part of a large range.
        /// </summary>
        /// <param name="range">a pan range</param>
        /// <returns>next range or null.</returns>
        public PanRange NextRange(PanRange range)
        {
            PanRange rv = null;
            ulong low = ulong.MaxValue;
            foreach (PanRange r in ranges)
            {
                // If current high <= candidate's low, and
                // candidate's low < prior candidate's low.
                if ((range.High <= r.Low) &&
                    (r.Low < low))
                {
                    rv = r;
                    low = r.Low;
                }
            }
            return rv;
        }

        /// <summary>
        /// Return the newest prior range.
        /// This is an entire range, not part of a larger range.
        /// </summary>
        /// <param name="range">a pan range</param>
        /// <returns>prior range or null</returns>
        public PanRange PriorRange(PanRange range)
        {
            PanRange rv = null;
            ulong high = ulong.MinValue;
            foreach (PanRange r in ranges)
            {
                // If current low >= candidate's high, and
                // candidate's high > prior candidate's high.
                if ((range.Low >= r.High) &&
                    (r.High > high))
                {
                    rv = r;
                    high = r.High;
                }
            }
            return rv;
        }
    }
}
