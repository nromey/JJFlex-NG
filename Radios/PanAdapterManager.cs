using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Xml.Serialization;
using Flex.Smoothlake.FlexLib;
using Flex.Util;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Standalone pan adapter / braille waterfall manager.
    /// Sprint 11 Phase 11.1: Extracted from Flex6300Filters.
    /// All pan adapter logic lives here; no WinForms UI dependencies.
    /// </summary>
    public class PanAdapterManager
    {
        private FlexBase rig;
        private Radio theRadio { get { return rig.theRadio; } }
        private string operatorsDirectory;
        private int brailleWidth;
        private Action<string, int> panDisplayCallback;

        private Panadapter panadapter { get { return rig.Panadapter; } }
        private Waterfall waterfall { get { return rig.Waterfall; } }
        private PanRanges panRanges;

        internal bool PanReady = false;

        const int fps = 1;
        const int lowDBM = -121;
        const int highDBM = -21;
        const int brailleWidthDefault = 40;
        const int brailleScaleup = 50;
        const int brailleUpdateSeconds = 1;
        private ulong stepSize;

        private object segmentLock = new object();
        private PanRanges.PanRange segment = null;
        private uint generation = 0;
        private FlexWaterfall flexPan;

        private PanData currentPanData;
        private ushort[] oldPanData;
        private DateTime lastPanTime = DateTime.Now;
        private TimeSpan panInterval = new TimeSpan(10000 * 400 / fps); // 0.4 sec / fps
        private bool rapidReturn = false;
        private uint rapidStreamID;
        private int rapidFPS;

        private bool centerChangeSent = false;

        // ConfigInfo for per-operator settings (AutoProc)
        public class ConfigInfo
        {
            public string AutoProc = autoprocValues[0];
        }
        internal ConfigInfo OpsConfigInfo;
        internal static string[] autoprocValues = { "off", "ssb" };

        private string ConfigFilename { get { return (operatorsDirectory == null) ? null : operatorsDirectory + '\\' + "configinfo.xml"; } }

        /// <summary>
        /// Callback for segment low/high display updates.
        /// Parameters: (lowText, highText)
        /// </summary>
        public Action<string, string> SegmentDisplayCallback { get; set; }

        public PanAdapterManager(FlexBase rig, string operatorsDirectory, int brailleCells,
                                  Action<string, int> panDisplayCallback)
        {
            this.rig = rig;
            this.operatorsDirectory = operatorsDirectory;
            this.panDisplayCallback = panDisplayCallback;

            brailleWidth = brailleCells;
            if (brailleWidth <= 0)
            {
                brailleWidth = brailleWidthDefault;
                Tracing.TraceLine("PanAdapterManager:no braille cells given, using " + brailleWidth, TraceLevel.Error);
            }

            // Load per-operator config
            getConfig();

            panRanges = new PanRanges(rig, operatorsDirectory);
            currentPanData = new PanData(brailleWidth);
            PanReady = true;
        }

        // --- Config management ---

        internal void getConfig()
        {
            Stream configFile = null;
            try
            {
                if (!Directory.Exists(operatorsDirectory))
                {
                    Directory.CreateDirectory(operatorsDirectory);
                }
                if (!File.Exists(ConfigFilename))
                {
                    OpsConfigInfo = new ConfigInfo();
                }
                else
                {
                    configFile = File.Open(ConfigFilename, FileMode.Open);
                    XmlSerializer xs = new XmlSerializer(typeof(ConfigInfo));
                    OpsConfigInfo = (ConfigInfo)xs.Deserialize(configFile);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("PanAdapterManager getConfig error:" + ex.Message, TraceLevel.Error);
            }
            finally
            {
                if (configFile != null) configFile.Dispose();
            }
        }

        internal void writeConfig()
        {
            if (ConfigFilename == null)
            {
                Tracing.TraceLine("PanAdapterManager configWrite no file", TraceLevel.Error);
                return;
            }
            Tracing.TraceLine("configWrite:" + ConfigFilename, TraceLevel.Info);
            Stream configFile = null;
            try
            {
                configFile = File.Open(ConfigFilename, FileMode.Create);
                XmlSerializer xs = new XmlSerializer(typeof(ConfigInfo));
                xs.Serialize(configFile, OpsConfigInfo);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("configWrite error:" + ex.Message, TraceLevel.Error);
            }
            finally
            {
                if (configFile != null) configFile.Dispose();
            }
        }

        // --- Operator change ---

        public void OperatorChangeHandler()
        {
            getConfig();
            panRanges = new PanRanges(rig, operatorsDirectory);
            RXFreqChange(rig.VFOToSlice(rig.RXVFO));
        }

        // --- RX frequency / segment management ---

        public void RXFreqChange(object o)
        {
            if (!PanReady) return;

            if (o is List<Slice>)
            {
                List<Slice> sList = (List<Slice>)o;
                copyPan(sList[0].Panadapter, sList[1].Panadapter);
                return;
            }

            Slice s = (Slice)o;
            if ((s == null) || !s.Active) return;
            FreqChange(s.Freq);
        }

        public void FreqChange(double f)
        {
            ulong freq = rig.LibFreqtoLong(f);
            Tracing.TraceLine("FreqChange:" + freq.ToString(), TraceLevel.Info);
            FlexBase.DbgTrace("segmentLock1");
            lock (segmentLock)
            {
                if ((segment != null) && segment.User)
                {
                    Tracing.TraceLine("FreqChange:user segment", TraceLevel.Info);
                    return;
                }

                Tracing.TraceLine("FreqChange:current freq:" + rig.RXFrequency.ToString(), TraceLevel.Info);

                PanRanges.PanRange seg = panRanges.Query(freq);
                if (seg == null)
                {
                    Tracing.TraceLine("FreqChange:segment not found", TraceLevel.Info);
                    ulong low = freq - (((ulong)brailleWidth * 1000) / 2);
                    seg = new PanRanges.PanRange(low, low + ((ulong)brailleWidth * 1000), PanRanges.PanRangeStates.temp);
                }
                FlexBase.DbgTrace("dbg1:" + seg.ToString());

                if ((segment == null) || ((seg.Low != segment.Low) | (seg.High != segment.High)))
                {
                    segment = seg;
                    invalidateOldSegment();
                    Tracing.TraceLine("FreqChange:new segment:" + generation + ' ' + segment.ToString(), TraceLevel.Info);
                    panParameterSetup();
                }
            }
            FlexBase.DbgTrace("segmentLock1 done");
        }

        private void panParameterSetup()
        {
            Tracing.TraceLine("PanParameterSetup", TraceLevel.Info);
            try
            {
                if ((panadapter == null) || (waterfall == null)) return;

                // Remove all panadapter and waterfall handlers.
                for (int i = 0; i < rig.MyNumSlices; i++)
                {
                    if (rig.mySlices[i].Panadapter != null)
                    {
                        rig.mySlices[i].Panadapter.DataReady -= panDataHandler;
                    }
                }
                lock (rig.waterfallList)
                {
                    foreach (Waterfall w in rig.waterfallList)
                    {
                        w.DataReady -= waterfallDataHandler;
                    }
                }

                if (flexPan != null) flexPan.Stop();
                flexPan = null;

                // Display segment bounds via callback
                SegmentDisplayCallback?.Invoke(rig.Callouts.FormatFreq(segment.Low), rig.Callouts.FormatFreq(segment.High));

                lock (currentPanData)
                {
                    currentPanData.LowFreq = rig.LongFreqToLibFreq(segment.Low);
                    currentPanData.HighFreq = rig.LongFreqToLibFreq(segment.High);
                }

                stepSize = (ulong)((float)segment.Width / (float)brailleWidth);
                if (stepSize == 0) stepSize = 1;

                panadapter.Width = (brailleWidth * brailleScaleup) + brailleWidth;
                panadapter.Height = 700;
                panadapter.FPS = fps;
                panadapter.CenterFreq = segmentCenter(segment);
                waterfall.CenterFreq = panadapter.CenterFreq;
                FlexBase.DbgTrace("dbg setup:" + waterfall.CenterFreq.ToString());
                panadapter.Bandwidth = rig.LongFreqToLibFreq((ulong)segment.Width + stepSize);
                waterfall.Bandwidth = panadapter.Bandwidth;
                panadapter.LowDbm = lowDBM;
                panadapter.HighDbm = highDBM;
                flexPan = new FlexWaterfall(this, rig, segment.Low, segment.High, rig.Callouts.BrailleCells);
                panadapter.DataReady += panDataHandler;
                waterfall.DataReady += waterfallDataHandler;
            }
            catch (Exception ex)
            {
                // Can happen if active slice changes.
                Tracing.TraceLine("panParameterSetup exception" + ex.Message + ex.StackTrace, TraceLevel.Error);
            }
        }

        private void copyPan(Panadapter inPan, Panadapter outPan)
        {
            Tracing.TraceLine("copyPan", TraceLevel.Info);
            Waterfall inFall = rig.GetPanadaptersWaterfall(inPan);
            Waterfall outFall = rig.GetPanadaptersWaterfall(outPan);
            rig.q.Enqueue((FlexBase.FunctionDel)null, "copyPan start");
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.Width = inPan.Width; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.Height = inPan.Height; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.FPS = inPan.FPS; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.CenterFreq = inPan.CenterFreq; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outFall.CenterFreq = inFall.CenterFreq; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.Bandwidth = outPan.Bandwidth; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outFall.Bandwidth = inFall.Bandwidth; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.LowDbm = inPan.LowDbm; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.HighDbm = inPan.HighDbm; }));
            rig.q.Enqueue((FlexBase.FunctionDel)null, "copyPan done");
        }

        private void invalidateOldSegment()
        {
            segment.Generation = ++generation;
        }

        private double segmentCenter(PanRanges.PanRange segment)
        {
            return rig.LongFreqToLibFreq(segment.Low + (ulong)(segment.Width / 2));
        }

        // --- Pan data handler ---

        private void panDataHandler(Panadapter pan, ushort[] data)
        {
            if (rig.Disconnecting | (flexPan == null))
            {
                return;
            }

            TimeSpan delta = DateTime.Now - lastPanTime;
            if ((delta < panInterval) & (pan.FPS != fps))
            {
                rapidReturn = true;
                rapidFPS = pan.FPS;
                rapidStreamID = pan.StreamID;
                Tracing.TraceLine("panDataHandler:changing FPS:" + pan.FPS + ' ' + fps + ' ' + delta.ToString(), TraceLevel.Info);
                panadapter.FPS = fps;
                return;
            }
            else
            {
                bool diff = ((oldPanData == null) || (oldPanData.Length != data.Length));
                if (!diff)
                {
                    for (int i = 0; i < data.Length; i++)
                    {
                        if (data[i] != oldPanData[i])
                        {
                            oldPanData = data;
                            diff = true;
                            break;
                        }
                    }
                }
            }

            lastPanTime = DateTime.Now;
            if (rapidReturn)
            {
                rapidReturn = false;
                Tracing.TraceLine("panDataHandler:rapid calls:" + rapidStreamID + ' ' + rapidFPS, TraceLevel.Error);
            }

            try
            {
                PanData panOut = flexPan.Read();
                if ((panOut != null) && (panOut.Line.Length > 0))
                {
                    Tracing.TraceLine("panData:" + panOut.Line.Length.ToString() + ' ' + panOut.Line, TraceLevel.Verbose);
                    int pos = 0;
                    lock (currentPanData)
                    {
                        Array.Copy(panOut.frequencies, currentPanData.frequencies, panOut.frequencies.Length);
                        currentPanData.Line = string.Copy(panOut.Line);
                        pos = currentPanData.EntryPosition;
                    }
                    if (!rig.Disconnecting)
                    {
                        panDisplayCallback?.Invoke(panOut.Line, pos);
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("panDataHandler exception:" + ex.Message, TraceLevel.Error);
            }
        }

        // --- Waterfall data handler ---

        private void waterfallDataHandler(Waterfall w, WaterfallTile tile)
        {
            if (rig.Disconnecting | (flexPan == null))
            {
                FlexBase.DbgTrace("waterfallDataHandler:disconnecting or flexPan is null");
                return;
            }

            FlexBase.DbgTrace("segmentLock2");
            lock (segmentLock)
            {
                if (segment.Generation != generation)
                {
                    Tracing.TraceLine("waterfallDataHandler:returning:" + generation, TraceLevel.Info);
                    FlexBase.DbgTrace("segmentLock2 Done");
                    return;
                }

                double center = segmentCenter(segment);
                if (w.CenterFreq != center)
                {
                    Tracing.TraceLine("waterfallDataHandler:center not equal:" + w.CenterFreq.ToString() + ' ' + center.ToString() + ' ' + centerChangeSent.ToString(), TraceLevel.Info);
                    if (!centerChangeSent)
                    {
                        Tracing.TraceLine("waterfallDataHandler:center:" + center.ToString(), TraceLevel.Info);
                        rig.q.Enqueue((FlexBase.FunctionDel)(() =>
                        {
                            panParameterSetup();
                        }), "center change");
                        centerChangeSent = true;
                        FlexBase.DbgTrace("segmentLock2 Done");
                        return;
                    }
                }
                centerChangeSent = false;

                flexPan.Write(tile);
            }
            FlexBase.DbgTrace("segmentLock2 Done");
        }

        // --- Navigation ---

        public void gotoFreq(double freq)
        {
            Tracing.TraceLine("gotoFreq:" + freq.ToString() + ' ' + stepSize.ToString(), TraceLevel.Info);
            if (rig.ShowingXmitFrequency)
            {
                rig.q.Enqueue((FlexBase.FunctionDel)(() => { rig.mySlices[rig.TXVFO].Freq = freq; }), "[rig.TXVFO].Freq");
            }
            else
            {
                rig.q.Enqueue((FlexBase.FunctionDel)(() => { theRadio.ActiveSlice.Freq = freq; }), "ActiveSlice.Freq");
            }
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { rig.Callouts.GotoHome(); }), "goto home");
        }

        public bool checkForRangeJump(int keyCode)
        {
            // Keys.PageUp = 33, Keys.PageDown = 34
            bool rv = false;

            FlexBase.DbgTrace("segmentLock3");
            lock (segmentLock)
            {
                if (segment != null)
                {
                    PanRanges.PanRange newRange;
                    if (keyCode == 33) // PageUp
                    {
                        if ((newRange = panRanges.PriorRange(segment)) != null)
                        {
                            segment = newRange;
                            invalidateOldSegment();
                            panParameterSetup();
                            rv = true;
                        }
                    }
                    else if (keyCode == 34) // PageDown
                    {
                        if ((newRange = panRanges.NextRange(segment)) != null)
                        {
                            segment = newRange;
                            invalidateOldSegment();
                            rv = true;
                            panParameterSetup();
                        }
                    }
                }
            }
            FlexBase.DbgTrace("segmentLock3 done");
            return rv;
        }

        // --- Zero beat ---

        private Thread zeroBeatThread;
        private ulong zeroBeatValue;

        public ulong ZeroBeatFreq()
        {
            zeroBeatValue = 0;
            if (flexPan != null)
            {
                zeroBeatThread = new Thread(zeroBeatProc);
                zeroBeatThread.Name = "zeroBeatThread";
                zeroBeatThread.Start();
                FlexBase.await(() => { return !zeroBeatThread.IsAlive; }, 1100);
            }
            Tracing.TraceLine("PanAdapterManager ZeroBeatFreq:" + zeroBeatValue.ToString(), TraceLevel.Info);
            return zeroBeatValue;
        }

        private const int totalTime = 1000;
        private const int iterations = 10;

        class freqCount
        {
            public ulong Freq;
            public int Count;
            public freqCount(ulong f, int c)
            {
                Freq = f;
                Count = c;
            }
        }

        private void zeroBeatProc()
        {
            int sanity = iterations;
            Dictionary<ulong, freqCount> freqs = new Dictionary<ulong, freqCount>();
            while (sanity-- != 0)
            {
                freqCount freqCT = new freqCount(flexPan.ZeroBeatFreq(), 1);
                if (freqs.Keys.Contains(freqCT.Freq))
                {
                    freqs[freqCT.Freq].Count++;
                    if (freqCT.Count == (iterations / 2))
                    {
                        zeroBeatValue = freqCT.Freq;
                        break;
                    }
                }
                else freqs.Add(freqCT.Freq, freqCT);
                Thread.Sleep(totalTime / iterations);
            }
            if (zeroBeatValue == 0)
            {
                int maxCount = 0;
                foreach (freqCount fc in freqs.Values)
                {
                    if (fc.Count > maxCount)
                    {
                        maxCount = fc.Count;
                        zeroBeatValue = fc.Freq;
                    }
                }
            }
            Tracing.TraceLine("zeroBeatProc finished:" + zeroBeatValue.ToString(), TraceLevel.Info);
        }

        // --- Pan segment info (for UI consumers) ---

        /// <summary>
        /// Get the current pan data for cursor-based frequency navigation.
        /// </summary>
        public PanData CurrentPanData { get { return currentPanData; } }

        /// <summary>
        /// Get the current segment (for low/high display by UI).
        /// </summary>
        public PanRanges.PanRange CurrentSegment
        {
            get { lock (segmentLock) { return segment; } }
        }

        /// <summary>
        /// Set a user-defined segment range and reconfigure the pan adapter.
        /// </summary>
        public void SetUserSegment(ulong low, ulong high)
        {
            FlexBase.DbgTrace("segmentLock6");
            lock (segmentLock)
            {
                segment = new PanRanges.PanRange(low, high);
                invalidateOldSegment();
                panParameterSetup();
            }
        }

        /// <summary>
        /// Save the current segment to persistent pan ranges.
        /// </summary>
        public void SaveSegment()
        {
            FlexBase.DbgTrace("segmentLock7");
            lock (segmentLock)
            {
                if ((segment != null) && !segment.Saved)
                {
                    panRanges.Insert(segment);
                }
            }
        }

        /// <summary>
        /// Erase the current segment and re-query.
        /// </summary>
        public void EraseSegment()
        {
            FlexBase.DbgTrace("segmentLock8");
            lock (segmentLock)
            {
                if (segment.Saved & !segment.Permanent) panRanges.Remove(segment);
                segment = null;
                RXFreqChange(rig.VFOToSlice(rig.RXVFO));
            }
        }

        // --- Close ---

        public void Close()
        {
            Tracing.TraceLine("PanAdapterManager.Close", TraceLevel.Info);
            if ((rig != null) && (theRadio != null))
            {
                if (panadapter != null) panadapter.DataReady -= panDataHandler;
                if (waterfall != null) waterfall.DataReady -= waterfallDataHandler;
            }
        }

        // --- Inner types ---

        public class PanData
        {
            public int Cells;
            public string Line;
            public double[] frequencies;
            public double LowFreq, HighFreq;
            public double HZPerCell { get { return (HighFreq - LowFreq) / Cells; } }
            public int EntryPosition;
            public PanData(int cells)
            {
                Cells = cells;
                frequencies = new double[cells];
            }
            public int FreqToCell(double f)
            {
                if (f < LowFreq) f = LowFreq;
                else if (f > HighFreq) f = HighFreq;
                double relFreq = f - LowFreq;
                int rv = (int)(relFreq / HZPerCell);
                if (rv == Cells) rv--;
                return rv;
            }
            public double CellToFreq(int c)
            {
                if (c < 0) c = 0;
                else if (c > Cells - 1) c = Cells - 1;
                return LowFreq + (c * HZPerCell);
            }
        }
    }
}
