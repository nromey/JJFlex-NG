//#define TyleDetail
#define Average
#define highRes
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Flex.Util;
using JJTrace;

namespace Radios
{
    class FlexWaterfall
    {
        private PanAdapterManager parent;
        private FlexBase rig;
#if Average
        class avg
        {
            public uint Count;
            public uint[] Total;
            public avg(WaterfallTile tile)
            {
                Count = 1;
                Total = new uint[tile.Data.Length];
                for (int i = 0; i < Total.Length; i++)
                {
                    Total[i] = tile.Data[i];
                }
            }
            public ushort[] Averages
            {
                get
                {
                    int len = Total.Length;
                    ushort[] averages = new ushort[len];
                    for (int i = 0; i < len; i++)
                    {
                        averages[i] = (ushort)(Total[i] / Count);
                    }
                    return averages;
                }
            }
        }
#endif
        class accumulatorType
        {
            private WaterfallTile accumulator;
#if Average
            public avg Average;
#endif
            //public double FirstFreq { get { return accumulator.FirstPixelFreq; } }
            public double FirstFreq { get { return accumulator.FrameLowFreq; } }
            public double BinWidth { get { return accumulator.BinBandwidth; } }
            public ushort this[int id] { get { return accumulator.Data[id]; } }
            public int Length { get { return (accumulator == null) ? 0 : accumulator.Data.Length; } }
            private bool startOver = true;
            public bool Cleared { get { return startOver; } }
            public void Clear()
            {
                startOver = true;
            }
            public void Record(WaterfallTile tile)
            {
                if (startOver)
                {
                    accumulator = tile;
#if Average
                    Average = new avg(tile);
#endif
                }
                int len = Math.Min(Length, tile.Data.Length);
                if (startOver) startOver = false;
                else
                {
#if Average
                    Average.Count++;
                    for (int i = 0; i < len; i++)
                    {
                        Average.Total[i] += tile.Data[i];
                    }
#else
                    for (int i = 0; i < len; i++)
                    {
                        if (accumulator.Data[i] < tile.Data[i]) accumulator.Data[i] = tile.Data[i];
                    }
#endif
                }
            }
        }
        private accumulatorType accumulator = null;

        private double low, high, stepSize; // in MHZ
        private int cells;
#if highRes
        private static char[] brailleOut = { 'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j','k','l','m','n','o','p','q','r','s','t','u','v','w','x','y','z',
            'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I','J','K','L','M','N','O','P','Q','R','S','T','U','V','W','X','Y','Z' };
        //private static char[] brailleOut = { 'a', 'b', 'l', 'p', 'q', '=',
        //'A', 'B', 'L', 'P', 'Q'};
#else
        private static char[] brailleOut = { 'a', 'b', 'l', 'p', 'q', '=' };
#endif
#if zero
        private static char[] brailleOut =
        {
            'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j',
            'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't',
            '*', '<', '%', '?', ':', '$', '}', '|', '{', 'w',
            'u', 'v', 'x', 'y', 'z', '&', '=', '(', '!', ')',
        };
#endif

        // Signals below weakSignalThreshhold are considered background noise.
        private const int weakSignalThreshhold = 0;

        // swampThreshold is used to keep very strong signals from swamping the other signals.
        // It was set experimentally for about s9.
        private const int swampThreshold = 25000;

        public FlexWaterfall(PanAdapterManager p, FlexBase r, ulong l, ulong h, int c)
        {
            Tracing.TraceLine("FlexWaterfall:" + l.ToString() + ' ' + h.ToString() + ' ' + c.ToString(), TraceLevel.Info);
            parent = p;
            rig = r;
            low = (double)l / 1e6;
            high = (double)h / 1e6;
            cells = c;
            stepSize = (high - low) / cells;
            // Adjust to put the frequency of interest in the cell's center.
            low -= stepSize / 2;
            high += stepSize / 2;
            accumulator = new accumulatorType();
        }

        public void Stop()
        {
            Thread.Sleep(100);
            lock (accumulator) { }
            Tracing.TraceLine("FlexWaterfall stop");
        }

        private double zeroBeatFreq;
        private uint zeroBeatValue = 0;
        private uint zbLowVal = uint.MaxValue;
        private uint zbHighVal = 0;
        private void zbInvalidate()
        {
            zeroBeatValue = 0;
            zbLowVal = uint.MaxValue;
            zbHighVal = 0;
        }
        // This is done while the accumulator is locked.
        private void zeroBeatCalc(WaterfallTile tile)
        {
            if (rig.Mode.ToString() != "CW") return;
            double lowFreq = rig.LongFreqToLibFreq(rig.RXFrequency - (ulong)Math.Abs(rig.FilterLow));
            double highFreq = rig.LongFreqToLibFreq(rig.RXFrequency + (ulong)Math.Abs(rig.FilterHigh));
            if (!((lowFreq < tile.FrameLowFreq) ||
                (highFreq > (double)tile.FrameLowFreq + ((double)tile.BinBandwidth * tile.Data.Length))))
            {
                // Invalidate the current zerobeat data if necessary.
                if ((zeroBeatFreq < lowFreq) | (zeroBeatFreq > highFreq)) zbInvalidate();
                int lowID = (int)((lowFreq - tile.FrameLowFreq) / (double)tile.BinBandwidth);
                int highID = (int)((highFreq - tile.FrameLowFreq) / (double)tile.BinBandwidth);
                uint lowVal = uint.MaxValue;
                uint highVal = 0;
                int highValID = 0;
                for (int id = lowID; id < highID; id++)
                {
                    if (tile.Data[id] < lowVal) lowVal = tile.Data[id];
                    else if (tile.Data[id] > highVal)
                    {
                        highVal = tile.Data[id];
                        highValID = id;
                    }
                }
                if (lowVal < zbLowVal) zbLowVal = lowVal;
                if (highVal > zbHighVal) zbHighVal = highVal;
                // If we've got a low and high value, and a peak was found:
                if ((zbLowVal < zbHighVal) & (highValID != 0))
                {
                    double zbFreq = (double)tile.FrameLowFreq + highValID * (double)tile.BinBandwidth;
                    if ((zbFreq != zeroBeatFreq) | (highVal > zeroBeatValue))
                    {
                        zeroBeatFreq = zbFreq;
                        zeroBeatValue = highVal;
                    }
                }
            }
        }

        public void Write(WaterfallTile tile)
        {
            lock (accumulator)
            {
#if TyleDetail
                Tracing.TraceLine("FlexWaterfall Write:" + accumulator.Cleared.ToString() + ' ' +
                    low.ToString() + ' ' + high.ToString() + ' ' +
                    ((double)tile.FrameLowFreq).ToString() + ' ' +
                    ((double)tile.BinBandwidth).ToString() + ' ' +
                    tile.LineDurationMS.ToString() + ' ' +
                    tile.Width.ToString() + ' ' +
                    tile.Height.ToString() + ' ' +
                    tile.Timecode.ToString() + ' ' +
                    //((double)tile.FirstPixelFreq + ((double)tile.BinBandwidth * tile.Data.Length)).ToString() + ' ' +
                    tile.Data.Length.ToString(), TraceLevel.Verbose);
#else
                Tracing.TraceLine("FlexWaterfall Write:" + ((double)tile.FrameLowFreq).ToString() + ' ' + ((double)tile.BinBandwidth).ToString(), TraceLevel.Verbose);
#endif
                // Ignore this if not in range.
                if (!((low < tile.FrameLowFreq) ||
                    (high > (double)tile.FrameLowFreq + ((double)tile.BinBandwidth * tile.Data.Length))))
                {
                    accumulator.Record(tile);
                    zeroBeatCalc(tile);
                    FlexBase.DbgTrace("dbg4:" + low.ToString() + ' ' + ((double)tile.FrameLowFreq).ToString() + ' ' +
                        high.ToString() + ' ' + ((double)tile.FrameLowFreq + ((double)tile.BinBandwidth * tile.Data.Length)).ToString());
                }
                else
                {
                    FlexBase.DbgTrace("dbg3:" + low.ToString() + ' ' + ((double)tile.FrameLowFreq).ToString() + ' ' +
                        high.ToString() + ' ' + ((double)tile.FrameLowFreq + ((double)tile.BinBandwidth * tile.Data.Length)).ToString());
#if zero
                    rig.q.Enqueue((FlexBase.FunctionDel)(() =>
                    {
                        parent.iRXFreqChange(low + (high - low) / 2);
                    }), "retry rxFreqChange");
#endif
                }
            }
        }

        /// <summary>
        /// Get the CW zero beat frequency if applicable, 0 if not.
        /// </summary>
        /// <returns>ulong frequency or 0</returns>
        public ulong ZeroBeatFreq()
        {
            ulong rv = 0;
            lock (accumulator)
            {
                rv = rig.LibFreqtoLong(zeroBeatFreq);
                Tracing.TraceLine("FlexWaterfall ZeroBeatFreq:" + rv.ToString(), TraceLevel.Info);
                if ((rv < (rig.RXFrequency - (uint)Math.Abs(rig.FilterLow))) |
                    (rv > (rig.RXFrequency + (uint)Math.Abs(rig.FilterHigh))))
                {
                    rv = 0;
                }
            }
            Tracing.TraceLine("FlexWaterfall ZeroBeatFreq:" + rv.ToString(), TraceLevel.Info);
            return rv;
        }

        public PanAdapterManager.PanData Read()
        {
            //string logTxt = "";
            PanAdapterManager.PanData rv = new PanAdapterManager.PanData(cells);
            ushort[] brlArray = new ushort[cells];
            double lowFreq = low;
            ushort floor = ushort.MaxValue;
#if highRes
            ushort max = swampThreshold;
#else
            ushort max = 0;
#endif
            lock (accumulator)
            {
                if (accumulator.Cleared)
                {
                    Tracing.TraceLine("FlexWaterfall.Read:ac cleared", TraceLevel.Error);
                    return null;
                }
                // Get id of first pertinent bin.
                int lowID = (int)((low - accumulator.FirstFreq) / accumulator.BinWidth);
#if Average
                ushort[] averages = accumulator.Average.Averages;
#endif
                // For each braille cell, get the max value from the waterfall.
                // Also set floor, max, and frequencies values.
                for (int i = 0; i < cells; i++)
                {
                    // Get the max value in this range.
                    double highFreq = lowFreq + stepSize;
                    ushort val = 0;
                    while (lowFreq < highFreq)
                    {
#if Average
                        if (val < averages[lowID])
                        {
                            val = averages[lowID];
                            rv.frequencies[i] = lowFreq + (accumulator.BinWidth / 2);
                        }
#else
                        if (val < accumulator[lowID])
                        {
                            val = accumulator[lowID];
                            rv.frequencies[i] = lowFreq + (accumulator.BinWidth / 2);
                        }
#endif
                        lowID++;
                        if (lowID == accumulator.Length)
                        {
                            throw new Exception("FlexWaterfall:accumulator length exceeded:" + lowID.ToString());
                        }
                        lowFreq += accumulator.BinWidth;
                    }
                    if (val > swampThreshold)
                    {
                        //Tracing.TraceLine("FlexWaterfall swamp:" + val.ToString(), TraceLevel.Info);
                        val = swampThreshold;
                    }
                    brlArray[i] = val;
                    if (floor > val) floor = val;
#if !highRes
                    if (max < val) max = val;
#endif
                }
                accumulator.Clear();
            }
            floor += weakSignalThreshhold;
            if (max > swampThreshold) max = swampThreshold;
            bool noVariance = (floor > max);
            //Tracing.TraceLine("Flexwaterfall read logs:" + logTxt + ' ' + ((float)Math.Log(swampThreshold)).ToString(), TraceLevel.Info);
            // Get the characters.
            char[] rvArray = new char[cells + 1];
            rvArray[cells] = '\0';
            // Get # values per cell to use.
            int usedBrailleLength = brailleOut.Length;
            // cellRange is the with of each character's bin.
            int cellRange;
            cellRange = (max - floor) / usedBrailleLength;
            Tracing.TraceLine("Flexwaterfall read:" + floor.ToString() + ' ' + max.ToString() + ' ' + cellRange.ToString(), TraceLevel.Verbose);
            if (cellRange == 0) cellRange = 1;
            // Get the braille characters.
            for (int i = 0; i < cells; i++)
            {
                if (noVariance) rvArray[i] = brailleOut[0];
                else
                {
                    int id = (brlArray[i] - floor) / cellRange;
                    if (id < 0) id = 0;
                    if (id == usedBrailleLength) id--;
                    rvArray[i] = brailleOut[id];
                }
            }
            rv.Line = new string(rvArray);
            return rv;
        }
    }
}
