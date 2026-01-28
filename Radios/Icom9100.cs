using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using HamBands;
using JJTrace;

namespace Radios
{
    public class Icom9100 : Icom
    {
        // default preamble address.
        internal const byte IC9100Address = 0x7c;

        // Region - capabilities
        #region capabilities
        private RigCaps.Caps[] capsList =
        {
            RigCaps.Caps.AFGet,
            RigCaps.Caps.AFSet,
            RigCaps.Caps.AGGet,
            RigCaps.Caps.AGSet,
            RigCaps.Caps.AGTimeGet,
            RigCaps.Caps.AGTimeSet,
            RigCaps.Caps.ALCGet,
            RigCaps.Caps.ANGet,
            RigCaps.Caps.ANSet,
            RigCaps.Caps.ATGet,
            RigCaps.Caps.ATSet,
            RigCaps.Caps.BCGet,
            RigCaps.Caps.BCSet,
            RigCaps.Caps.CLGet,
            RigCaps.Caps.CLSet,
            RigCaps.Caps.CTSSFreqGet,
            RigCaps.Caps.CTSSFreqSet,
            RigCaps.Caps.CTModeGet,
            RigCaps.Caps.CTModeSet,
            RigCaps.Caps.CWAutoTuneGet,
            RigCaps.Caps.CWAutoTuneSet,
            RigCaps.Caps.CWDelayGet,
            RigCaps.Caps.CWDelaySet,
            RigCaps.Caps.DMGet,
            RigCaps.Caps.DMSet,
            RigCaps.Caps.EQRGet,
            RigCaps.Caps.EQRSet,
            RigCaps.Caps.EQTGet,
            RigCaps.Caps.EQTSet,
            RigCaps.Caps.FrGet,
            RigCaps.Caps.FrSet,
            RigCaps.Caps.FSGet,
            RigCaps.Caps.FSSet,
            RigCaps.Caps.FWGet,
            RigCaps.Caps.FWSet,
            RigCaps.Caps.IDGet,
            RigCaps.Caps.KSGet,
            RigCaps.Caps.KSSet,
            RigCaps.Caps.LKGet,
            RigCaps.Caps.LKSet,
            RigCaps.Caps.MemGet,
            RigCaps.Caps.MemSet,
            RigCaps.Caps.MGGet,
            RigCaps.Caps.MGSet,
            RigCaps.Caps.ModeGet,
            RigCaps.Caps.ModeSet,
            RigCaps.Caps.NBGet,
            RigCaps.Caps.NBSet,
            RigCaps.Caps.NFGet,
            RigCaps.Caps.NFSet,
            RigCaps.Caps.NTGet,
            RigCaps.Caps.NTSet,
            RigCaps.Caps.PAGet,
            RigCaps.Caps.PASet,
            RigCaps.Caps.RAGet,
            RigCaps.Caps.RASet,
            RigCaps.Caps.RFGet,
            RigCaps.Caps.RFSet,
            RigCaps.Caps.SMGet,
            RigCaps.Caps.SPGet,
            RigCaps.Caps.SPSet,
            RigCaps.Caps.SQGet,
            RigCaps.Caps.SQSet,
            RigCaps.Caps.SWRGet,
            RigCaps.Caps.TOGet,
            RigCaps.Caps.TOSet,
            RigCaps.Caps.TXMonGet,
            RigCaps.Caps.TXMonSet,
            RigCaps.Caps.VDGet,
            RigCaps.Caps.VDSet,
            RigCaps.Caps.VFOGet,
            RigCaps.Caps.VFOSet,
            RigCaps.Caps.VGGet,
            RigCaps.Caps.VGSet,
            RigCaps.Caps.VSGet,
            RigCaps.Caps.VSSet,
            RigCaps.Caps.XFGet,
            RigCaps.Caps.XFSet,
            RigCaps.Caps.XPGet,
            RigCaps.Caps.XPSet,
        };
        #endregion

        // region - properties
        #region properties
        public override ulong RXFrequency
        {
            get
            {
                return base.RXFrequency;
            }
            set
            {
                base.RXFrequency = value;
                if (IC9100Memories != null)
                {
                    Memories = IC9100Memories[(int)getIC9100MemoryRange(value)];
                }
            }
        }

        internal override hfvhfValues hfvhf
        {
            get { return _hfvhf; }
        }

        internal OffOnValues _UHFPreamp;
        internal OffOnValues UHFPreamp
        {
            get { return _UHFPreamp; }
            set
            {
                _UHFPreamp = value;
                byte b = (byte)((value == OffOnValues.on) ? 1 : 0);
                SendCommand(BldCmd(ICPreAmp, b));
            }
        }
        #endregion

        // FM tone frequencies
        internal static float[] myToneFrequencyTable =
        {
            67.0f, 69.3f, 71.9f, 74.4f, 77.0f, 79.7f, 82.5f, 85.4f,
            88.5f, 91.5f, 94.8f, 97.4f, 100.0f, 103.5f, 107.2f, 110.9f,
            114.8f, 118.8f, 123.0f, 127.3f, 131.8f, 136.5f, 141.3f, 146.2f,
            151.4f, 156.7f, 159.8f, 162.2f, 165.5f, 167.9f, 171.3f, 173.8f,
            177.3f, 179.9f, 183.5f, 186.2f, 189.9f, 192.8f, 196.6f, 199.5f,
            203.5f, 206.5f, 210.7f, 218.1f, 225.7f, 229.1f, 233.6f, 241.8f,
            250.3f, 254.1f
        };

        internal static int[] MyDTCSTable =
        {
            023, 025, 026, 031, 032, 036, 043, 047, 051, 053, 054, 065,
            071, 072, 073, 074,
            114, 115, 116, 122,
            125, 131, 132, 134,
            143, 145, 152, 155,
            156, 162, 165, 172,
            174, 205, 212, 223,
            225, 226, 243, 244,
            245, 246, 251, 252,
            255, 261, 263, 265,
            266, 271, 274, 306,
            311, 315, 325, 331,
            332, 343, 346, 351,
            356, 364, 365, 371,
            411, 412, 413, 423,
            431, 432, 445, 446,
            452, 454, 455, 462,
            464, 465, 466, 503,
            506, 516, 523, 526,
            532, 546, 565, 606,
            612, 624, 627, 631,
            632, 654, 662, 664,
            703, 712, 723, 731,
            732, 734, 743, 754
        };

        private static IcomCommand[] myStatCommands =
        {
            ICRFPower,
            ICBrkin,
            ICBrkinDelay, // breakin delay.
            ICVox,
            ICTuner,
            ICAFGain,
            ICRFGain,
            ICKeyer,
            ICKeyerSpeed,
            ICCWPitch,
            ICSidetoneGain,
            ICFirstIFFilter,
            ICInnerPBT,
            ICOuterPBT,
            ICVoxGain,
            ICVoxDelay,
            ICAntiVoxGain,
            ICComp,
            ICCompLevel,
            ICMicGain,
            ICSSBTransmitBandwidth,
            ICTXBandwidthWide,
            ICTXBandwidthMid,
            ICTXBandwidthNarrow,
            ICMon,
            ICMonLevel,
            ICNR,
            ICNRLevel,
            ICAGC,
            ICAGCtc,
            ICNB,
            ICNBLevel,
            ICNBDepthB80_6,
            ICNBDepthB2,
            ICNBDepthB440,
            ICNBDepthB1200, // Tells us if 1200 mhz is present
            ICNBWidthB80_6,
            ICNBWidthB2,
            ICNBWidthB440,
            ICNBWidthB1200,
            ICAutoNotch,
            ICManualNotch,
            ICNotchPosition,
            ICNotchWidth,
            ICVoiceDelay,
            ICDupOffset,
            ICTone,
            ICToneSquelch,
            ICReadToneFreq,
            ICReadToneSquelchFreq,
            ICReadAttenuator,
            ICPreAmp,
            ICAFC,
            ICAFCLimit,
            ICXmitMonitor,
            ICTuningStep,
            ICSubBand,
        };

        public Icom9100()
        {
            CommandHDR = new byte[] { 0xfe, 0xfe, IC9100Address, 0 };
            myCaps = new RigCaps(capsList);

            ToneFrequencyTable = myToneFrequencyTable;
        }

        public override bool Open(OpenParms p)
        {
            Tracing.TraceLine("IC9100 open", TraceLevel.Info);
            p.RawIO = true;
            Escapes.Escapes.HexOnly = true;

            bool rv = base.Open(p);

            if (rv)
            {
                // setup subband field.
                // Note that the sub band is toggled with m or s, so no separate active status.
                p.RigField1 = new RigDependent(field1Chars,1,field1Get,field1Set,
                    () => true, (bool val) => { });
                p.RigField2 = new RigDependent(field2Chars, 2,field2Get,field2Set,
                    () => { return subBandActive; },
                    (bool v) => { if (!v) _mainSubControl = mainSubControls.main; }); // happens automatically
                // setup RigFields
                new ic9100filters(this);

                statCommands = myStatCommands;
                getRigStatus(true, true);
            }
            IsOpen = rv;
            return rv;
        }

        public override void close()
        {
            if (RigFields != null)
            {
                // The caller should have removed the user control from their form.
                RigFields.Close();
                RigFields = null;
            }

            base.close();
        }

        protected override void initialCustomStatus(bool initial)
        {
            Tracing.TraceLine("ic9100 initialCustomStatus:" + initial.ToString(), TraceLevel.Info);
#if zero
            if (initial)
            {
                // Save sub band status, on/off.
                SendCommand(BldCmd(ICSubBand)); // see contSubBand()
                if (!commandSync(defaultConfirmTime)) Tracing.TraceLine("initial status didn't get sub band", TraceLevel.Error);

                // See if primary is HF or VHF.
                SendCommand(BldCmd(ICSubBand, (byte)0)); // turn off sub band
                commandSync(defaultConfirmTime);
                ulong freq = 0;
                if (getTheFreq(out freq))
                {
                    _hfvhf = (freq < 100000000) ? hfvhfValues.hf : hfvhfValues.vhf;
                }
                else Tracing.TraceLine("initial status didn't get freq", TraceLevel.Error);

                // Restore sub band
                if (_subBandActive)
                {
                    SendCommand(BldCmd(ICSubBand, (byte)1));
                    if (!commandSync(defaultConfirmTime)) Tracing.TraceLine("initial status didn't restore sub band", TraceLevel.Error);
                }
            }
#endif

            // We need to reset the tone mode, see myStatCommands
            _ToneMode = ToneTypes.off;
        }

        private const int subOffID = 0;
        private const int subOnID = 1;
        private const int subExchID = 2;
        private static char[] field1Chars = { 'o', 'n', 'x' };
        private char field1Get(int id)
        {
            return (subBandActive) ? field1Chars[subOnID] : field1Chars[subOffID];
        }
        private void field1Set(char c, int fldID)
        {
            // Do nothing if getting status.
            if (rsHelperRunning != 0) return;

            // Turn sub on or off, or exchange.
            int id = Array.IndexOf(field1Chars, c);
            switch (id)
            {
                case subOffID:
                    mainSubControl = mainSubControls.main;
                    subBandActive = false;
                    break;
                case subOnID:
                    subBandActive = true;
                    break;
                case subExchID:
                    SendCommand(BldCmd(ICExchangeMainSub));
                    commandSync(defaultConfirmTime);
                    // Get the rig's status
                    getRigStatus(false, false);
                    break;
                default: return;
            }
        }

        private bool _subBandActive;
        private bool subBandActive
        {
            get { return _subBandActive; }
            set
            {
                _subBandActive = value;
                byte b = (byte)((value) ? 1 : 0);
                SendCommand(BldCmd(ICSubBand, b));
            }
        }

        private enum mainSubControls
        {
            main,
            sub
        }
        private mainSubControls _mainSubControl;
        private mainSubControls mainSubControl
        {
            get { return _mainSubControl; }
            set
            {
                if (value != _mainSubControl)
                {
                    _mainSubControl = value;
                    IcomCommand cmd = (value == mainSubControls.main) ? ICSelectMainBand : ICSelectSubBand;
                    SendCommand(BldCmd(cmd));
                    commandSync(defaultConfirmTime);
                    // Get the rig's status
                    getRigStatus(false, false);
                }
            }
        }

        private const int mainControlID = 0;
        private const int subControlID = 1;
        private static char[] field2Chars = { 'm', 's' };
        private char field2Get(int id)
        {
            return (mainSubControl == mainSubControls.main) ? field2Chars[mainControlID] : field2Chars[subControlID];
        }
        private void field2Set(char c, int id)
        {
            // Do nothing if getting status.
            if (rsHelperRunning != 0) return;
            if (c == field2Chars[mainControlID]) mainSubControl = mainSubControls.main;
            else mainSubControl = mainSubControls.sub;
        }

        // region - memory stuff
        #region memories
        internal enum IC9100MemoryRanges
        {
            hf,
            m2,
            m70,
            m23
        }
        private IC9100MemoryRanges getIC9100MemoryRange(ulong freq)
        {
            IC9100MemoryRanges rv = IC9100MemoryRanges.hf;
            Bands.BandItem item = Bands.Query(freq);
            if (item != null)
            {
                switch (item.Band)
                {
                    case Bands.BandNames.m2: rv = IC9100MemoryRanges.m2; break;
                    case Bands.BandNames.m70: rv = IC9100MemoryRanges.m70; break;
                    case Bands.BandNames.m23: rv = IC9100MemoryRanges.m23; break;
                }
            }
            return rv;
        }
        internal IC9100MemoryRanges IC9100MemoryRange
        {
            get
            {
                return getIC9100MemoryRange(Frequency);
            }
        }

        internal MemoryGroup[] IC9100Memories;
        internal MemoryGroup IC9100MemoryGroup
        {
            get { return IC9100Memories[(int)IC9100MemoryRange]; }
        }

        internal int[] IC9100MemoryChannels;
        public override int CurrentMemoryChannel
        {
            get { return IC9100MemoryChannels[(int)IC9100MemoryRange]; }
            set
            {
                IC9100MemoryChannels[(int)IC9100MemoryRange] = value;
                if (MemoryMode)
                {
                    gotoMemory(CurrentMemoryNumber);
                }
            }
        }

        protected override void CollectMemories(object o)
        {
            bool initial = (bool)o;
            try
            {
                Tracing.TraceLine("collectMem:started " + initial.ToString() + ' ' + TotalMemories.ToString(), TraceLevel.Info);

                if (initial)
                {
                    MemoriesLoaded = false;
                    raiseComplete(CompleteEvents.memoriesStart);
                    int nGroups = Enum.GetValues(typeof(IC9100MemoryRanges)).Length;
                    if (!B1200) nGroups -= 1;
                    IC9100Memories = new MemoryGroup[nGroups];
                    IC9100MemoryChannels = new int[nGroups];

                    for (int i = 0; i < nGroups; i++)
                    {
                        string name = ((IC9100MemoryRanges)i).ToString();
                        Tracing.TraceLine("CollectMemories group:" + name, TraceLevel.Info);
                        IC9100Memories[i] = new MemoryGroup(TotalMemories, this, 1, name);
                        for (int j = 0; j < TotalMemories; j++)
                        {
                            // See if suspended.
                            while (SuspendMemCollection) Thread.Sleep(1000);

                            SendCommand(BldMemCmd(i, j + 1));
                            commandSync(defaultConfirmTime);
                        }
                    }

                    // Set the group for the current frequency.
                    Tracing.TraceLine("CollectMemories frequency:" + Frequency.ToString(), TraceLevel.Info);
                    Memories = IC9100MemoryGroup;

                    // Report loaded.  Sends complete event.
                    MemoriesLoaded = true;
                }

#if MemoryDebug
            foreach (MemoryData m in Memories)
            {
                debugMemoryData(m);
            }
#endif
            }
            catch (ThreadAbortException ab)
            {
                Tracing.TraceLine("CollectMemories abort:" + ab.Message, TraceLevel.Info);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("CollectMemories:" + ex.Message, TraceLevel.Error);
            }
        }
        #endregion

        // region - response handlers
        #region handlers
        protected override void contSubBand(byte[] cmd, int pos, int len)
        {
            _subBandActive = (cmd[len - 1] == 1) ? true : false;
        }

        protected override void contOpFreq(byte[] cmd, int pos, int len)
        {
            base.contOpFreq(cmd, pos, len);
            if (IC9100Memories != null)
            {
                Memories = IC9100MemoryGroup;
            }
            _hfvhf = (freqJustRetrieved < 100000000) ? hfvhfValues.hf : hfvhfValues.vhf;
        }

        protected override void contMemContents(byte[] cmd, int pos, int len)
        {
            MemoryGroup grp = IC9100Memories[cmd[memGroupPos]];
            SettMemContents(cmd, pos, len, grp.mems);
        }

        protected override void contPreamp(byte[] cmd, int pos, int len)
        {
            if (hfvhf == hfvhfValues.hf)
            {
                _HFPreamp = (HFPreampType)cmd[len - 1];
            }
            else
            {
                _UHFPreamp = (OffOnValues)cmd[len - 1];
            }
        }
        #endregion
    }
}
