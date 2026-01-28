#define GetMemoriez
//#define MemoryDebug
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using JJTrace;

namespace Radios
{
    public class KenwoodTS2000 : Kenwood
    {
        private const string nextValue1Desc = "Next noise reduction value";

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
            RigCaps.Caps.RITGet,
            RigCaps.Caps.RITSet,
            RigCaps.Caps.SMGet,
            RigCaps.Caps.SPGet,
            RigCaps.Caps.SPSet,
            RigCaps.Caps.SQGet,
            RigCaps.Caps.SQSet,
            RigCaps.Caps.SWRGet,
            RigCaps.Caps.TOGet,
            RigCaps.Caps.TOSet,
            RigCaps.Caps.TXITGet,
            RigCaps.Caps.TXITSet,
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
            RigCaps.Caps.Pan,
            RigCaps.Caps.ManualTransmit
        };
        #endregion

        // region - rig-specific properties
        #region RigSpecificProperties
        public override int RigID
        {
            get { return RadioSelection.RIGIDKenwoodTS2000; }
        }

        public override bool Transmit
        {
            get { return base.Transmit; }
            set
            {
                TransmitStatus = value;
#if zero
                char val = (transmittingSub) ? '1' : '0';
                if (value) Callouts.safeSend(BldCmd(kcmdTX + val));
                else Callouts.safeSend(BldCmd(kcmdRX + val));
#else
                if (value) Callouts.safeSend(BldCmd(kcmdTX));
                else Callouts.safeSend(BldCmd(kcmdRX));
#endif
            }
        }

        public override ulong RXFrequency
        {
            get { return _RXFrequency; }
            set
            {
                char v = (controlingSub) ? 'C' : VFOToLetter(RXVFO);
                string str = "F" + v + UFreqToString(value);
                Callouts.safeSend(BldCmd(str));
            }
        }

        public override ulong TXFrequency
        {
            get { return _TXFrequency; }
            set
            {
                char v = (controlingSub) ? 'C' : VFOToLetter(TXVFO);
                string str = "F" + v + UFreqToString(value);
                Callouts.safeSend(BldCmd(str));
            }
        }
        public override bool Split
        {
            get
            {
                if (controlingSub) return false;
                else return base.Split;
            }
            set
            {
                if (!controlingSub & !IsMemoryMode(RXVFO))
                {
                    // Using VFOs.
                    int v = (int)((value) ? nextVFO(RXVFO) : RXVFO);
                    Callouts.safeSend(BldCmd(kcmdFT + v.ToString()));
                }
                // Else using the subreceiver or a memory, can't set it.
            }
        }

        // Use SMeter dots as the index.
        private static int[] sMeterTable =
        {0,1,1,1,2,3,3,4,5,5,6,7,7,8,9,9,
         9+10,9+10,9+10,9+20,9+20,9+30,9+30,9+40,9+40,9+40,9+50,9+50,9+50,9+60,9+60};
        // Not sure of the power level values.
        private static int[] powerMeterTable =
        {0,5,5,5,10,10,10,25,25,25,25,25,25,50,50,50,50,50,50,
         75,75,75,75,75,100,100,100,100,100,100,100};
        /// <summary>
        /// Calibrated S-Meter/power
        /// </summary>
        public override int SMeter
        {
            get
            {
                if (Transmit)
                    return ((_SMeter >= 0) && (_SMeter < powerMeterTable.Length)) ?
                        powerMeterTable[_SMeter] : 0;
                else
                    return ((_SMeter >= 0) && (_SMeter < sMeterTable.Length)) ?
                        sMeterTable[_SMeter] : 0;
            }
        }

        /// <summary>
        /// Tone/CTSS setting
        /// </summary>
        public override ToneCTCSSValue ToneCTCSS
        {
            get
            {
                return base.ToneCTCSS;
            }
            set
            {
                char val = kToneCTCSS(value);
                switch (val)
                {
                    case '0':
                        Callouts.safeSend(BldCmd(kcmdTO + "0"));
                        Callouts.safeSend(BldCmd(kcmdCT + "0"));
                        Callouts.safeSend(BldCmd(kcmdDQ + "0"));
                        break;
                    case '1': Callouts.safeSend(BldCmd(kcmdTO + "1")); break;
                    case '2': Callouts.safeSend(BldCmd(kcmdCT + "1")); break;
                    case '3': Callouts.safeSend(BldCmd(kcmdDQ + "1")); break;
                }
                // toneCT is set by the contXX commands.
            }
        }

        public override int TXAntenna
        {
            get
            {
                return base.TXAntenna;
            }
            set
            {
                // Rig's value is 1 based.
                string val = (value == 0) ? "1" : "2";
                Callouts.safeSend(BldCmd(kcmdAN + val));
            }
        }
        public override bool RXAntenna
        {
            get
            {
                return base.RXAntenna;
            }
            set
            {
                // Use a menu command here.
                Callouts.safeSend(BldCmd(kcmdEX + "0180000" + (string)((value) ? "1" : "0")));
            }
        }

        public override OffOnValues RFAttenuator
        {
            get
            {
                return _RFAttenuator;
            }
            set
            {
                // Format for setting is RAp1p1 (no p2)
                string val = (value == OffOnValues.on) ? "01" : "00";
                Callouts.safeSend(BldCmd(kcmdRA + val));
            }
        }

        public override int XmitPower
        {
            get
            {
                return base.XmitPower;
            }
            set
            {
                string val = value.ToString("d3");
                Callouts.safeSend(BldCmd(kcmdPC + val));
            }
        }
        public int XmitPowerMin = 5;
        public int XmitPowerMax = 100;
        public int XmitPowerIncrement = 5;

        public override int AGC
        {
            get
            {
                return base.AGC;
            }
            set
            {
                Callouts.safeSend(BldCmd(kcmdGT + value.ToString("d3")));
            }
        }
        public override int AGCFast { get { return 20; } }
        public override int AGCSlow { get { return 1; } }

        public override OffOnValues NoiseBlanker
        {
            get
            {
                return base.NoiseBlanker;
            }
            set
            {
                string sval = (string)((value == OffOnValues.on) ? "1" : "0");
                Callouts.safeSend(BldCmd(kcmdNB + sval));
            }
        }

        public override OffOnValues AutoNotch
        {
            get
            {
                return base.AutoNotch;
            }
            set
            {
                string sval = (string)((value == OffOnValues.on) ? "1" : "0");
                Callouts.safeSend(BldCmd(kcmdNT + sval));
            }
        }

        public override OffsetDirections OffsetDirection
        {
            get
            {
                return base.OffsetDirection;
            }
            set
            {
                string s = ((byte)value).ToString("d1");
                Callouts.safeSend(BldCmd(kcmdOS + s));
                Tracing.TraceLine("OffsetDirection set:" + value.ToString() + " " + s, TraceLevel.Info);
            }
        }

        public override int OffsetFrequency
        {
            get
            {
                return base.OffsetFrequency;
            }
            set
            {
                string str = value.ToString("d9");
                Callouts.safeSend(BldCmd(kcmdOF + str));
            }
        }

        // Memory group min/max.
        internal const int minMemoryGroupID = 0;
        internal const int maxMemoryGroupID = 9;

        private const string rigSpeechCommand = kcmdEX + "0150000";
        public override bool RigSpeech
        {
            get
            {
                return base.RigSpeech;
            }
            set
            {
                if (_RigSpeech != value)
                {
                    string v = (value) ? _RigSpeechLevel.ToString("d1") : "0";
                    Callouts.safeSend(BldCmd(rigSpeechCommand + v));
                    _RigSpeech = value;
                    // Note that when starting speech, past stuff may be queued up and spoken.
                }
            }
        }

        public override bool AutoMode
        {
            get
            {
                return base.AutoMode;
            }
            set
            {
                if (_AutoMode != value)
                {
                    Callouts.safeSend(BldCmd(kcmdAM + (string)((value) ? "1" : "0")));
                    _AutoMode = value;
                }
            }
        }

        private const string beepLevelCommand = kcmdEX + "0120000";
        public override int RigBeepLevel
        {
            get { return base.RigBeepLevel; }
            set
            {
                string data = value.ToString("d1");
                string cmd = beepLevelCommand + data;
                Callouts.safeSend(BldCmd(cmd));
                _RigBeepLevel = value;
            }
        }

        internal override float StepSize
        {
            get
            {
                return base.StepSize;
            }
            set
            {
                string str = stepSizeToID(value, RXMode).ToString("d2");
                Callouts.safeSend(BldCmd(kcmdST + str));
            }
        }

        // Note that for the 2000, the audio gain applies to the subRX.
        private int _LineoutGainSub;
        public override int LineoutGain
        {
            get
            {
                return (controlingSub) ? _LineoutGainSub : _LineoutGain;
            }
            set
            {
                if (value < 0) value = 0;
                if (value > 255) value = 255;
                string cmd = kcmdAG;
                if (controlingSub)
                {
                    cmd += '1';
                    _LineoutGainSub = value;
                }
                else
                {
                    cmd += '0';
                    _LineoutGain = value;
                }
                Callouts.safeSend(BldCmd(cmd + value.ToString("d3")));
            }
        }

        private OffOnValues _Reverse;
        /// <summary>
        /// FM reverse
        /// </summary>
        internal OffOnValues Reverse
        {
            get { return _Reverse; }
            set
            {
                if (value != _Reverse)
                {
                    _Reverse = value;
                    Callouts.safeSend(BldCmd(kcmdTS + '1')); // toggle
                }
            }
        }
        #endregion

        // Rig status commands, see rigstat() in kenwood.cs.
        private static string[] myStatCommands =
        {
            kcmdIF,
            kcmdVX,
            kcmdSB,
            kcmdFA,
            kcmdFB,
            kcmdFC,
            kcmdSM + "0",
            kcmdAC,
            kcmdAG + '0',
            kcmdAG + '1',
            kcmdAM,
            kcmdAN,
            kcmdBC,
            kcmdCG,
            kcmdCN,
            kcmdCT,
            kcmdDC,
            kcmdDQ,
            rigSpeechCommand, // speech status
            beepLevelCommand, // beep level
            kcmdFW,
            kcmdGT,
            kcmdIS,
            kcmdKS,
            kcmdMF,
            kcmdMG,
            kcmdML,
            kcmdNB,
            kcmdNR,
            kcmdNT,
            kcmdOF,
            kcmdPA,
            kcmdPC,
            kcmdPL,
            kcmdPR,
            kcmdRA,
            kcmdRG,
            kcmdSD,
            kcmdSH,
            kcmdSL,
            kcmdST,
            kcmdTN,
            kcmdTO,
            kcmdTS,
            kcmdVD,
            kcmdVG,
        };

        // FM tone modes
        private static ToneCTCSSValue[] myFMToneModes =
        {
            new ToneCTCSSValue('0', "Off"),
            new ToneCTCSSValue('1', "Tone"),
            new ToneCTCSSValue('2', "CTCSS"),
            new ToneCTCSSValue('3', "DCS")
        };
        // tone frequencies, 1 based.
        private static float[] myToneFrequencyTable =
        {
            -1, 67.0F, 71.9F, 74.4F, 77.0F, 79.7F, 82.5F, 85.4F, 88.5F, 91.5F,
            94.8F, 97.4F, 100.0F, 103.5F, 107.2F, 110.9F, 114.8F, 118.8F, 123.0F,
            127.3F, 131.8F, 136.5F, 141.3F, 146.2F, 151.4F, 156.7F, 162.2F, 167.9F,
            173.8F, 179.9F, 186.2F, 192.8F, 203.5F, 210.7F, 218.1F, 225.7F,
            233.6F, 241.8F, 250.3F, 1750F
        };

        public KenwoodTS2000()
        {
            Tracing.TraceLine("KenwoodTS2000 constructor", TraceLevel.Info);
            RadioID = RadioIDs.TS2000;
            myCaps = new RigCaps(capsList);
            getValueLock = new Mutex();

            memThreadLock = new Mutex();
            Memories = new MemoryGroup(totalMemories, this);

            // Setup the items for menu 51 (PFKeys) submenus.
            int len = PFChoices.Length + myMenus.Length;
            EnumAndValue[] pfStrings = new EnumAndValue[len];
            // Note that the pf choices are backward.
            int j = PFChoices.Length - 1;
            for (int i = 0; i < len; i++)
            {
                // Also, the values are 0 based.
                if (i >= myMenus.Length) pfStrings[i] = new EnumAndValue(PFChoices[j--], i);
                else pfStrings[i] = new EnumAndValue(myMenus[i].Description, i);
            }
            foreach (MenuDescriptor md in PFKeyMenus)
            {
                md.Enumerants = pfStrings;
                md.High = len - 1;
            }
            myMenus[51].subMenus = PFKeyMenus;

            // Setup 2 menu banks.
            Menus = new MenuDescriptor[2, myMenus.Length];
            for (int bank = 0; bank < Menus.GetLength(0); bank++)
            {
                // for each menu in this bank
                for (int m = 0; m < Menus.GetLength(1); m++)
                {
                    Menus[bank, m] = new MenuDescriptor(myMenus[m]);
                    setupMenu(Menus[bank, m], null, "");
                }
            }

            // Allow the 1 call channel to be activated.
            setCallChannelActive = mySetCallChannelActive;            

            // provide the rig status commands.
            statCommands = myStatCommands;

            // provide valid tone modes and frequencies
            FMToneModes = myFMToneModes;
            ToneFrequencyTable = myToneFrequencyTable;
        }
        /// <summary>
        /// Setup a TS-2000 menu; may have submenus.
        /// This is recursive.
        /// </summary>
        /// <param name="md">menu item being setup</param>
        /// <param name="p">parent, null for the toplevel menu (initial call)</param>
        /// <param name="cmdStr">command string so far</param>
        private void setupMenu(MenuDescriptor md, MenuDescriptor p, string cmdStr)
        {
            Tracing.TraceLine("setupMenu:" + cmdStr, TraceLevel.Verbose);
            MenuDescriptor parent;
            if (p == null)
            {
                // highest level menu.
                parent = md;
            }
            else
            {
                // a sub-menu
                parent = p;
                md.isSubMenu = true;                
            }
            if (md.HasSubMenus)
            {
                // There is no value to get or set.
                md.getRtn = null;
                md.setRtn = null;
                md.commandString = "";
                // Find the leaves and set their parents.
                foreach (MenuDescriptor sub in md.subMenus)
                {
                    // maintain the command string.
                    if (md.Type == MenuTypes.SubMenu1)
                    {
                        // initial cmdStr
                        cmdStr = md.Number.ToString("d3") + sub.Number.ToString("d2");
                    }
                    else
                    {
                        // add to the entry value.
                        cmdStr += sub.Number.ToString("d1");
                    }
                    // we recurse here.
                    setupMenu(sub, parent, cmdStr);
                }
            }
            else
            {
                // leaf - routines to get and set the value.
                md.getRtn = menuGet;
                md.setRtn = menuSet;
                if (cmdStr == "")
                {
                    // no submenus, cmdStr not setup yet.
                    cmdStr = md.Number.ToString("d3") + "0000";
                }
                else
                {
                    // Fill out the 7-digit command string.
                    for (int i = cmdStr.Length; i < 7; i++) cmdStr += "0";
                }
                md.commandString = cmdStr;
                Tracing.TraceLine("setupMenu leaf:" + cmdStr, TraceLevel.Info);
            }
        }

        private void mySetCallChannelActive(int id)
        {
            Callouts.safeSend(BldCmd(kcmdCI));
        }

#if zero
        internal override bool getTXMode()
        {
            if (TXMode != null) return true; // already got it.
            if (Transmit | MemoryMode) return false; // shouldn't have been called.
            bool rv;
            // Get the mode for the other VFO.
            RigCaps.VFOs saveVFO = rxV;
            Tracing.TraceLine("getTXMode:rxVFO=" + saveVFO.ToString(), TraceLevel.Info);
            OffsetDirections saveOffsetDirection = OffsetDirection;
            bool saveSplit = Split;
            if (saveSplit)
            {
                Split = false;
                await(() => { return (Split == false); }, 5000);
            }
            ModeValue saverxMd = rxMd;
            RigCaps.VFOs newVFO = nextVFO(rxV);
            RXVFO = newVFO;
            if (await(() => { return (rxV == newVFO); }, 5000))
            {
                // Explicitly query the mode, because we'll only get one automatically if RX and TX modes are different.
                rxMd = null;
                Callouts.safeSend(BldCmd(kcmdMD));
                rv = (await(() => { return (RXMode != null); }, 5000));
                if (rv)
                {
                    txMd = rxMd;
                    Tracing.TraceLine("getTXMode:" + txMd.ToString(),TraceLevel.Info);
                }
                else
                {
                    Tracing.TraceLine("getTXMode:no TXMode",TraceLevel.Error);
                }
                RXVFO = saveVFO;
                await(() => { return (rxV == saveVFO); }, 5000);
                rxMd = saverxMd;
            }
            else rv = false; // VFO change failed.
            if (saveSplit)
            {
                Split = true;
                await(() => { return (Split == true); }, 1000);
            }
            if (saveOffsetDirection != OffsetDirection) OffsetDirection = saveOffsetDirection;
            return rv;
        }
#endif

        // region - sub-receiver
        #region subRcvr
        private enum subRCVRvals { main, sub }
        private static char[] subRCVRChars = { 'm', 's' };
        private subRCVRvals subRCVRXVal, subRCVRCVal;
        private subRCVRvals subRCVRXmit
        {
            get { return subRCVRXVal; }
            set
            {
                if (value != subRCVRXVal)
                {
                    subRCVRvals save = subRCVRCVal;
                    string cmdVal = ((int)value).ToString() + ((int)save).ToString();
                    Callouts.safeSend(BldCmd(kcmdDC + cmdVal));
                    // To just change this, need to send again.
                }
            }
        }
        private subRCVRvals subRCVRCTRL
        {
            get { return subRCVRCVal; }
            set
            {
                if (value != subRCVRCVal)
                {
                    string cmdVal = ((int)subRCVRXVal).ToString() + ((int)value).ToString();
                    Callouts.safeSend(BldCmd(kcmdDC + cmdVal));
                }
            }
        }
        /// <summary>
        /// true if controling sub-receiver
        /// </summary>
        internal bool controlingSub
        {
            get { return subRCVRCVal == subRCVRvals.sub; }
        }
        /// <summary>
        /// true if transmitting on sub-receiver
        /// </summary>
        internal bool transmittingSub
        {
            get { return subRCVRXVal == subRCVRvals.sub; }
        }

        /// <summary>
        /// receiver prefix for rig commands
        /// </summary>
        internal char RCVRPrefix
        { get { return (controlingSub) ? '1' : '0'; } }

        private static string[] ctrlChangeCmds = { kcmdSM, kcmdPA, kcmdRA };
        protected override void contDC(string str)
        {
            subRCVRvals savectl = subRCVRCVal;
            subRCVRXVal = (str[2] == '1') ? subRCVRvals.sub : subRCVRvals.main;
            subRCVRCVal = (str[3] == '1') ? subRCVRvals.sub : subRCVRvals.main;
            if (savectl != subRCVRCVal)
            {
                // Send some status commands
                foreach (string cmd in ctrlChangeCmds)
                {
                    Callouts.safeSend(BldCmd(cmd));
                }
            }
        }

        protected override void contFreqC(string str)
        {
            if (controlingSub)
            {
                ulong f = System.UInt64.Parse(str.Substring(2));
                _RXFrequency = _TXFrequency = f;
            }
        }

        protected override void contSM(string str)
        {
            if (str[2] == RCVRPrefix)
            {
                int mult = (controlingSub) ? 2 : 1;
                _SMeter = mult * System.Int32.Parse(str.Substring(3));
                // For panning.
                raiseRigOutput(RigCaps.Caps.SMGet, (ulong)_SMeter);
            }
        }

        protected override void contPA(string str)
        {
            int pos = (controlingSub) ? 3 : 2;
            // format is PAp1p2
            preA = (str[pos] == '1') ? OffOnValues.on : OffOnValues.off;
        }

        protected override void contRA(string str)
        {
            int pos = (controlingSub) ? 4 : 2;
            // format is RAp1p1p2p2
            _RFAttenuator = (str.Substring(pos, 2) == "00") ? OffOnValues.off : OffOnValues.on;
        }

        /// <summary>
        /// Used for panning
        /// </summary>
        public override void UpdateMeter()
        {
            Callouts.safeSend(BldCmd(kcmdSM + RCVRPrefix.ToString()));
        }

        // Called from the application to get the letter.
        private char subGet(int id)
        {
            char rv;
            if (_subActive)
            {
                rv = subRCVRChars[(int)((id == 1) ? subRCVRXVal : subRCVRCVal)];
            }
            else rv = ' ';
            return rv;
        }

        // Called from the application to set the receiver.
        private void subSet(char c, int id)
        {
            if (_subActive)
            {
                subRCVRvals val = (c == 's') ? subRCVRvals.sub : subRCVRvals.main;
                // note that subRCVR Xmit and CTRL send the command to the rig.
                if (id == 1) subRCVRXmit = val;
                else subRCVRCTRL = val;
            }
        }
        private bool _subActive;
        private bool subActive
        {
            get { return _subActive; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdSB + (string)((value) ? "1" : "0")));
                _subActive = value;
            }
        }

        protected override void contSB(string str)
        {
            _subActive = (str[2] == '1');
        }
        #endregion

        public override bool Open(OpenParms p)
        {
            Tracing.TraceLine("KenwoodTS2000 Open", TraceLevel.Info);
            bool rv = base.Open(p);
            IsOpen = rv;
            if (rv)
            {
                // setup sub-receiver fields.
                p.RigField1 = new RigDependent(subRCVRChars, 1, subGet, subSet,
                    () => subActive,
                    (bool val) => { subActive = val; });
                p.RigField2 = new RigDependent(subRCVRChars, 2, subGet, subSet,
                    () => subActive,
                    (bool val) => { subActive = val; });
                p.NextValue1 = nextNRValue;
                p.NextValue1Description = nextValue1Desc;
                // setup RigFields - other 2000-specific data.
                new TS2000Filters(this);
            }
            return rv;
        }

        public override void close()
        {
            Tracing.TraceLine("KenwoodTS2000 close", TraceLevel.Info);

            try
            {
                // Careful if we're acquiring memories!
                if ((memThread != null) && memThread.IsAlive)
                {
                    memThread.Abort();
                }
                memThread = null;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("KenwoodTS2000 close memThread exception:" + ex.Message, TraceLevel.Error);
            }

            try
            {
                if ((MRThread != null) && MRThread.IsAlive)
                {
                    MRThread.Abort();
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("KenwoodTS2000 close MRThread exception:" + ex.Message, TraceLevel.Error);
            }

            if (RigFields != null)
            {
                // The caller should have removed the user control from their form.
                RigFields.Close();
                RigFields = null;
            }

            base.close();
        }

        // region - rig output handlers
        #region output handlers
        // Can't handle "IF;" if the call channel is on.
        private class IFCallChannel : Exception
        {
            public IFCallChannel() : base("IF received with call channel.") { }
        }

        protected override void contIF(string str)
        {
            int ofst = 2; // frequency offset
            ulong freq;
            int i;
            RigCaps.VFOs ovfo; // other VFO.
            bool mem; // memory mode
            string freqStr = str.Substring(ofst, 11);
            freq = System.UInt64.Parse(freqStr);
            ofst += 11 + 4;
            string rs = str.Substring(ofst, 6);
            RIT.Value = stringToRIT(rs);
            XIT.Value = stringToRIT(rs);
            ofst += 6;
            RIT.Active = (str[ofst++] == '1');
            XIT.Active = (str[ofst++] == '1');
            int chan = getMemoryChannel(str.Substring(ofst, 3));
            if (chan == 300) throw new IFCallChannel();
            _CurrentMemoryChannel = chan;
            ofst += 3; // mem channel
            TransmitStatus = (str[ofst++] == '1');
            if (Transmit) _TXFrequency = freq;
            else _RXFrequency = freq;
            ModeValue md = getMode(str[ofst++]);
            if (Transmit) _TXMode = md;
            else _RXMode = md;
            // VFO/memory.
            i = str[ofst++] - '0';
            if ((i >= 0) && (i <= 2))
            {
                // For a VFO:
                if (i < 2)
                {
                    mem = false;
                    // set current VFO
                    ovfo = (RigCaps.VFOs)((i + 1) % 2); // the other VFO.
                    if (Transmit) _TXVFO = (RigCaps.VFOs)i;
                    else _RXVFO = (RigCaps.VFOs)i;
                    freqVFO((RigCaps.VFOs)i, freqStr);
                }
                else
                {
                    // Memory mode.
                    mem = true;
                    ovfo = _RXVFO = _TXVFO = RigCaps.VFOs.None;
                }
            }
            else throw new IndexOutOfRangeException();
            ofst += 1; // Scan status.
            bool Splt = (str[ofst++] == '1');
            // Set the other VFO/freq.
            if (Splt)
            {
                if (!mem)
                {
                    // Split VFOs.
                    if (Transmit) _RXVFO = ovfo;
                    else _TXVFO = ovfo;
                } // else other vfo already set to None.
            }
            else
            {
                // symplex
                if (Transmit)
                {
                    _RXVFO = _TXVFO;
                    _RXFrequency = _TXFrequency;
                    _RXMode = _TXMode;
                }
                else
                {
                    _TXVFO = _RXVFO;
                    _TXFrequency = _RXFrequency;
                    _TXMode = _RXMode;
                }
            }
            _ToneCTCSS = getToneCTCSS(str[ofst++]);
            float tcf = ToneCTCSSToFreq(str.Substring(ofst, 2));
            // see if in tone or ctcss mode
            if ((_ToneCTCSS == myFMToneModes[0]) || (_ToneCTCSS == myFMToneModes[1]))
                _ToneFrequency = tcf;
            else if (_ToneCTCSS == myFMToneModes[2])
                _CTSSFrequency = tcf;
            else
            {
                // cross tone
                if (Transmit) _ToneFrequency = tcf;
                else _CTSSFrequency = tcf;
            }
            ofst += 2;
            OffsetDirections o = (OffsetDirections)(str[ofst++] - '0');
            if (!Splt) _OffsetDirection = o;
        }
        private int stringToRIT(string str)
        {
            Tracing.TraceLine("stringToRIT:" + str, TraceLevel.Info);
            int i;
            if (System.Int32.TryParse(str.Substring(1), out i))
            {
                return (str[0] == '-') ? -i : i;
            }
            else
            {
                Tracing.TraceLine("stringToRIT:error" + str, TraceLevel.Error);
                return 0;
            }
        }

        public override void CopyVFO(RigCaps.VFOs inv, RigCaps.VFOs outv)
        {
            if ((inv != CurVFO) & (outv != nextVFO(inv)))
            {
                Tracing.TraceLine("CopyVFO:VFOs are set wrong.", TraceLevel.Error);
                return;
            }
            if (Transmit)
            {
                Tracing.TraceLine("CopyVFO:can't be transmitting", TraceLevel.Error);
                return;
            }
            OffsetDirections offset;
            Tracing.TraceLine("CopyVFO:" + inv.ToString() + " " + outv.ToString(), TraceLevel.Info);
            ulong freqI;
            bool saveSplit = Split;
            ModeValue modeI;
            freqI = RXFrequency;
            modeI = RXMode;
            offset = OffsetDirection;
            // We have to switch VFOs in order to set the mode.
            RXVFO = outv; // Only two VFOs.
            await(() => { return (_RXVFO == outv); }, 1000);
            RXFrequency = freqI;
            await(() => { return (_RXFrequency == freqI); }, 1000);
            RXMode = modeI;
            await(() => { return (RXMode == modeI); }, 1000);
            // Now switch back.
            RXVFO = inv;
            await(() => { return (_RXVFO == inv); }, 1000);
            if (!saveSplit & (RXMode.ToString() == "fm"))
            {
                OffsetDirection = offset;
                await(() => { return (OffsetDirection == offset); }, 1000);
            }
        }

        // These are mutually exclusive
        [Flags]
        private enum toneFlags
        {
            off=0,
            tone=1,
            ctcss=2,
            dcs=4
        }
        private toneFlags iTone;
        private void setTone(toneFlags f, bool set)
        {
            if (set) iTone = f;
            else iTone &= ((toneFlags)(-1) ^ f);
            // set toneCT if no bits set or just one bit set.
            if ((int)iTone == 0) _ToneCTCSS = myFMToneModes[0];
            else
            {
                double d = Math.Log((int)iTone, 2);
                if (d == (double)((int)d)) _ToneCTCSS = myFMToneModes[(int)d + 1];
            }
        }

        protected override void contTO(string str)
        {
            setTone(toneFlags.tone, (str[2] == '1'));
        }

        protected override void contCT(string str)
        {
            setTone(toneFlags.ctcss, (str[2] == '1'));
        }

        protected override void contDQ(string str)
        {
            setTone(toneFlags.dcs, (str[2] == '1'));
        }

        protected override void contAG(string str)
        {
            if (str[2] == '1')
            {
                _LineoutGainSub = System.Int32.Parse(str.Substring(3));
            }
            else
            {
                _LineoutGain = System.Int32.Parse(str.Substring(3));
            }
        }

        protected override void contAN(string str)
        {
            _TXAntenna = (str[2] == '2') ? 1 : 0; // value is 1-based.
        }

        internal enum NRtype
        { off, NR1, NR2 }
        private NRtype NRValueVal;
        /// <summary>
        /// TS2000 noise reduction
        /// </summary>
        internal NRtype NRValue
        {
            get { return NRValueVal; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdNR + ((int)value).ToString()));
            }
        }
        private void nextNRValue()
        {
            NRValue = (NRtype)(((int)NRValue + 1) % Enum.GetValues(typeof(NRtype)).Length);
        }
        protected override void contNR(string str)
        {
            NRValueVal = (NRtype)(str[2] - '0');
            // Get the level.
            if (NRValue != NRtype.off) Callouts.safeSend(BldCmd(kcmdRL));
        }

        internal const int NRLevel1Low = 0;
        internal const int NRLevel1High = 10;
        internal const int NRLevel1Increment = 1;
        // NRLevel1Val is sent to/received from the radio.
        private int NRLevel1Val;
        /// <summary>
        /// NR level 1
        /// </summary>
        internal int NRLevel1
        {
            get { return NRLevel1Val; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdRL + value.ToString("d2")));
            }
        }
        internal const int NRLevel2Low = 2;
        internal const int NRLevel2High = 20;
        internal const int NRLevel2Increment = 2;
        // NRLevel2Val is sent to/received from the radio.
        private int NRLevel2Val;
        /// <summary>
        /// SPAC time in ms.
        /// </summary>
        internal int NRLevel2
        {
            get { return (NRLevel2Val + 1) * 2; }
            set
            {
                NRLevel2Val = (value / 2) - 1;
                Callouts.safeSend(BldCmd(kcmdRL + NRLevel2Val.ToString("d2")));
            }
        }
        protected override void contRL(string str)
        {
            int val = System.Int32.Parse(str.Substring(2));
            if (NRValue == NRtype.NR1) NRLevel1Val = val;
            else if (NRValue == NRtype.NR2) NRLevel2Val = val;
        }

        // AGC level constants, not including 0.
        internal const int agcLevelLow = 1;
        internal const int agcLevelHigh = 20;
        internal const int agcLevelIncrement = 1;
        // agcSave holds the non-zero agc value.
        // If off at power-on, we'll use the midpoint.
        private int agcSave = (agcLevelLow + agcLevelHigh)/2;
        /// <summary>
        /// AGC on/off.  Off is GT000.
        /// </summary>
        internal OffOnValues agcOnOff
        {
            get { return (_AGC == 0) ? OffOnValues.off : OffOnValues.on; }
            set
            {
                string sval;
                if (value == OffOnValues.off) sval = "000";
                else sval = agcSave.ToString("d3");
                Callouts.safeSend(BldCmd(kcmdGT + sval));
            }
        }
        /// <summary>
        /// AGC level, starts with 1.
        /// </summary>
        internal int agcLevel
        {
            get { return agcSave; }
            set { Callouts.safeSend(BldCmd(kcmdGT + value.ToString("d3"))); }
        }

        protected override void contGT(string str)
        {
            if (str[2] != ' ') _AGC = System.Int32.Parse(str.Substring(2));
            if (_AGC != 0) agcSave = _AGC;
        }

        internal const int nbLevelLow = 1;
        internal const int nbLevelHigh = 10;
        internal const int nbLevelIncrement = 1;
        private int nbLevelVal;
        /// <summary>
        /// noise blanker level.
        /// Use NoiseBlanker to get on/off status.
        /// </summary>
        internal int NBLevel
        {
            get { return nbLevelVal; }
            set { Callouts.safeSend(BldCmd(kcmdNL + value.ToString("d3"))); }
        }

        protected override void contNB(string str)
        {
            _NoiseBlanker = (str[2] == '1') ? OffOnValues.on : OffOnValues.off;
            if (_NoiseBlanker == OffOnValues.on)
            {
                // turning on, get the level.
                Callouts.safeSend(BldCmd(kcmdNL));
            }
        }

        protected override void contNL(string str)
        {
            nbLevelVal = System.Int32.Parse(str.Substring(2));
        }

        internal enum bcType
        { off, auto, manual }
        internal bcType bcVal;
        /// <summary>
        /// beat cancel off, auto or manual.
        /// </summary>
        internal bcType bc
        {
            get { return bcVal; }
            set
            {
                string sval = ((int)value).ToString();
                Callouts.safeSend(BldCmd(kcmdBC + sval));
            }
        }
        internal const int bcLevelLow = 0;
        internal const int bcLevelHigh = 63;
        internal const int bcLevelIncrement = 1;
        internal int bcLevelVal;
        internal int bcLevel
        {
            get { return bcLevelVal; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdBP2 + value.ToString("d3")));
            }
        }

        protected override void contBC(string str)
        {
            bcVal = (bcType)(str[2] - '0');
            // Get the value
            Callouts.safeSend(BldCmd(kcmdBP2));
        }

        protected override void contBP(string str)
        {
            bcLevelVal = System.Int32.Parse(str.Substring(2));
        }

        internal const int notchLevelLow = 0;
        internal const int notchLevelHigh = 4;
        internal const int notchLevelIncrement = 1;
        internal int notchLevelVal;
        /// <summary>
        /// Notch level (really an autonotch)
        /// </summary>
        internal int notchLevel
        {
            get { return notchLevelVal; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdAL + value.ToString("d3")));
            }
        }

        protected override void contNT(string str)
        {
            _AutoNotch = (str[2] == '1') ? OffOnValues.on : OffOnValues.off;
        }

        protected override void contAL(string str)
        {
            notchLevelVal = System.Int32.Parse(str.Substring(2));
        }

        protected override void contAM(string str)
        {
            _AutoMode = (str[2] == '1') ? true : false;
        }

        private const int myCallChannelMemory = 300;
        private const string FTForCallChannel = "FT3";
        private const string MCForCallChannel = "MC300";
        protected override void contFR(string cmd)
        {
            int i = System.Int32.Parse(cmd.Substring(2, 1));
            if (i < 3)
            {
                CallChannel._Active = false;
                base.contFR(cmd);
            }
            else if (i == 3)
            {
                // Get the memory.
                Callouts.safeSend(BldCmd(kcmdMR + "0" + myCallChannelMemory.ToString()));
                CallChannel._Active = true;
            }
            else throw new IndexOutOfRangeException();
        }

        protected override void contFT(string cmd)
        {
            // Ignore FT3.
            if (cmd != FTForCallChannel) base.contFT(cmd);
        }

        protected override void contMC(string str)
        {
            // We'll ignore MC300.
            if (str != MCForCallChannel) base.contMC(str);
        }

        protected override void contST(string str)
        {
            int id = System.Int32.Parse(str.Substring(2, 2));
            _StepSize = stepIDToSize(id, RXMode);
        }
        internal static float[] stepSizesSSB =
        { 1f, 2.5f, 5f, 10f };
        internal static float[] stepSizesAMFM = { 5f, 6.25f, 10f, 12.5f, 15f, 20f, 25f, 30f, 50f, 100f };
        internal static int stepSizeToID(float s, ModeValue mode)
        {
            int rv = 0;
            string modeStr = mode.ToString();
            float[] steps = ((modeStr == "am") | (modeStr == "fm")) ? stepSizesAMFM : stepSizesSSB;
            int id = 0;
            foreach (float sz in steps)
            {
                if (s == sz)
                {
                    rv = id;
                    break;
                }
                id++;
            }
            return rv;
        }
        internal static float stepIDToSize(int id, ModeValue mode)
        {
            string modeStr = mode.ToString();
            float[] steps = ((modeStr == "am") | (modeStr == "fm")) ? stepSizesAMFM : stepSizesSSB;
            float rv;
            try
            {
                rv = steps[id];
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("stepIDToSize:" + ex.Message, TraceLevel.Error);
                rv = steps[0];
            }
            return rv;
        }

        protected override void contTS(string str)
        {
            if (Split)
            {
                // We won't set this if the split flag isn't on.
                // This can happen if we're using the XIT.
                TFSetOn = (str[2] == '1') ? true : false;
            }
            else
            {
                // This is likely FM reverse mode.
                if (Mode.ToString() == "fm") _Reverse = (OffOnValues)(str[2] - '0');
            }
        }
        #endregion

        // Region - memory stuff
        #region memoryStuff
        private const int totalMemories = 300;
        internal override void rigSpecificSetup(MemoryData m)
        {
            base.rigSpecificSetup(m);
            m.ToneCTCSS = myFMToneModes[0];
        }
        /// <summary>
        /// Load the specified memory from the radio.
        /// </summary>
        /// <param name="m">the memory object.  The number must be set.</param>
        protected virtual void loadMem(MemoryData m)
        {
            Tracing.TraceLine("loadMem:" + m.Number.ToString(), TraceLevel.Verbose);
            m.State = memoryStates.inProcess;
            Callouts.safeSend(BldCmd(kcmdMR + "0" + m.Number.ToString("d3")));
        }
        // memory get wait constants
        private const int getWait = 25; // 25 ms
        private const int getSanity = (5000 / getWait); // 5 second sanity count
        /// <summary>
        /// Get the data into the specified memory.
        /// </summary>
        /// <param name="m">memory object in the memories group, the number must be set.</param>
        /// <returns>true if gotten successfully.</returns>
        internal override bool getMem(MemoryData m)
        {
            Tracing.TraceLine("getMem:" + m.Number.ToString(), TraceLevel.Info);
            int sanity = getSanity;
            bool rv;
            m.myLock.WaitOne();
            while (!m.complete && (sanity-- > 0))
            {
                // load once only.
                if (m.State == memoryStates.none)
                {
                    loadMem(m); // Sets m.State.
                }
                // await response, see counts above.
                m.myLock.ReleaseMutex();
                Thread.Sleep(getWait);
                m.myLock.WaitOne();
            }
            rv = m.complete;
            m.myLock.ReleaseMutex();
            Tracing.TraceLine("getMem:returning " + rv.ToString(), TraceLevel.Info);
            return rv;
        }
        /// <summary>
        /// Set the radio's memory.
        /// </summary>
        /// <param name="m">a memoryData object</param>
        internal override void setMem(MemoryData m)
        {
            Tracing.TraceLine("SetMem:" + m.Number.ToString(), TraceLevel.Info);
            // Fix the name if too long, up to 7 characters.
            if ((m.Name != null) && (m.Name.Length > 7))
                m.Name = m.Name.Substring(0, 7);
            // Get the memory object to lock.
            MemoryData mg = Memories.mems[m.Number];
            string memnoStr = m.Number.ToString("d3");
            mg.myLock.WaitOne();
            // if the present flag isn't set, it's a deletion.
            if (!m.Present)
            {
                m.Frequency[0] = m.Frequency[1] = 0;
                m.Mode[0] = m.Mode[1] = ModeTable[0]; // mode none
                m.DataMode[0] = m.DataMode[1] = DataModes.off;
                m.ToneCTCSS = myFMToneModes[0];
                m.ToneFrequency = m.CTSSFrequency = ToneFrequencyTable[0];
                m.Reverse = false;
                m.OffsetDirection = OffsetDirections.off;
                m.OffsetFrequency = 0;
                m.StepSize = 0;
                m.GroupID = 0;
                m.FMMode = 0;
                m.Lockout = false;
                m.Name = "";
                m.Split = false;
                m.Type = MemoryTypes.Normal;
            }
            // Send the MW0.
            string mem0str = kcmdMW + "0" + memnoStr + 
                setFreqMode(m, 0) +
                ((m.Lockout) ? '1' : '0') +
                m.ToneCTCSS.value +
                kToneCTCSSFreq(m.ToneFrequency) +
                kToneCTCSSFreq(m.CTSSFrequency) +
                kDCS(m.DCS) +
                ((m.Reverse)? "1" : "0") +
                ((byte)m.OffsetDirection).ToString("d1") +
                m.OffsetFrequency.ToString("d9") +
                m.StepSize.ToString("d2") +
                m.GroupID.ToString("d1") +
                m.Name;
            Tracing.TraceLine("smw0:sending " + mem0str, TraceLevel.Info);
            Callouts.safeSend(BldCmd(mem0str));
            // See if need to send an MW1.
            if (m.Split || (m.Type == MemoryTypes.Range))
            {
                string mem1str = kcmdMW + "1" + memnoStr + 
                    setFreqMode(m, 1) +
                    ((m.Lockout) ? '1' : '0') +
                    m.ToneCTCSS.value +
                    kToneCTCSSFreq(m.ToneFrequency) +
                    kToneCTCSSFreq(m.CTSSFrequency) +
                    kDCS(m.DCS) +
                    ((m.Reverse)? "1" : "0") +
                    ((byte)m.OffsetDirection).ToString("d1") +
                    m.OffsetFrequency.ToString("d9") +
                    m.StepSize.ToString("d2") +
                    m.GroupID.ToString("d1") +
                    m.Name;
                Tracing.TraceLine("smw1:sending " + mem1str, TraceLevel.Info);
                Callouts.safeSend(BldCmd(mem1str));
            }
            mg.State = memoryStates.none; // until read back in.
            mg.myLock.ReleaseMutex();
            // Finally, get the updated memory from the rig.
            //Callouts.safeSend(BldCmd(kcmdMR + "0" + memnoStr));
        }
        private string setFreqMode(MemoryData m, int id)
        {
            Tracing.TraceLine("setFreqMode:" + m.Number.ToString() + "," + id.ToString(), TraceLevel.Verbose);
            string rv = m.Frequency[id].ToString("d11") +
                KMode(m.Mode[id]);
            return rv;
        }

        private Thread memThread;
        private Mutex memThreadLock;
        protected override void GetMemories()
        {
            Tracing.TraceLine("KenwoodTS2000 GetMemories", TraceLevel.Info);
            base.GetMemories();
#if GetMemoriez
            memThread = new Thread(new ThreadStart(CollectMemories));
            try { memThread.Start(); }
            catch (Exception ex)
            { Tracing.TraceLine("KenwoodTS2000 GetMemories:" + ex.Message, TraceLevel.Error); }
            Thread.Sleep(0);
#endif
        }
        protected void CollectMemories()
        {
            try
            {
                Tracing.TraceLine("collectMem:started", TraceLevel.Info);
                memThreadLock.WaitOne();
                raiseComplete(CompleteEvents.memoriesStart);
                for (int i = 0; i < totalMemories; i++)
                {
                    MemoryData m = Memories.mems[i];
                    // It's possible that the radio might lose the command if swamped.
                    while (!m.complete)
                    {
                        // Get the memory's first part.
                        if (m.State == memoryStates.none) loadMem(m);
                        // Wait up to .5 seconds or until it's complete.
                        int sanity = 20;
                        while (!m.complete && (sanity-- > 0))
                        {
                            Thread.Sleep(25);
                        }
                        // Finally, make sure it's loaded.
                        m.myLock.WaitOne();
                        if (m.State != memoryStates.complete)
                        {
                            Tracing.TraceLine("CollectMemories:retrying " + m.Number.ToString(), TraceLevel.Error);
                            m.State = memoryStates.none;
                        }
                        m.myLock.ReleaseMutex();
                    }
                }
                // Report complete.
                MemoriesLoaded = true;

#if MemoryDebug
                for (int i = 0; i < totalMemories; i++)
                {
                    debugMemoryData(Memories.mems[i]);
                }
#endif
            }
            catch (ThreadAbortException ab)
            {
                Tracing.TraceLine("CollectMemories:" + ab.Message, TraceLevel.Info);
            }
            finally
            {
                // Indicate thread complete.
                memThreadLock.ReleaseMutex();
            }
        }
        public override List<ScanGroup> GetReservedGroups()
        {
            if (!MemoriesLoaded) return null;
            Tracing.TraceLine("GetReservedGroups", TraceLevel.Info);
            Dictionary<string, ScanGroup> groups = new Dictionary<string,ScanGroup>();
            ScanGroup group = null;
            foreach (MemoryData m in Memories.mems)
            {
                if (!m.Present) continue;
                string name = "group" + m.GroupID.ToString();
                if (!groups.TryGetValue(name,out group))
                {
                    // new group.
                    group = new ScanGroup(name, Memories.Bank, new List<MemoryData>(), true);
                    groups.Add(name, group);
                }
                group.Members.Add(m);
            }
            List<ScanGroup> rv = null;
            if (groups.Keys.Count > 0)
            {
                rv = new List<ScanGroup>();
                foreach (string name in groups.Keys)
                {
                    rv.Add(groups[name]);
                }
            }
            Tracing.TraceLine("GetReservedGroups:" + rv.Count.ToString(),TraceLevel.Info);
            return rv;
        }
        private void debugMemoryData(MemoryData m)
        {
#if MemoryDebug
            Tracing.TraceLine("memory " + m.Number.ToString() + " " +
                m.Complete.ToString() + " " + m.Present.ToString() + " " + m.Name);
#endif
        }

        /// <summary>
        /// Handle MR from the rig.
        /// </summary>
        /// <param name="o">the MR command including MR</param>
        protected override void MRThreadProc(object o)
        {
            string str = o.ToString();
            Tracing.TraceLine("MRThreadProc:" + str, TraceLevel.Info);
            // if rxtx and frequency are 0, all done, not present.
            // frequency and mode come from rxtx 0 and 1.
            // If rxtx is 0, send out the MR1 command.
            // otherwise indicate complete.

            MemoryData m = null;
            try
            {
                int ofst = 2;
                int rxtx;
                string memNoString;
                rxtx = str[ofst++] - '0';
                char c = str[ofst++];
                if (c == ' ') c = '0';
                memNoString = c.ToString() + str.Substring(ofst, 2);
                ofst += 2;
                int memNo;
                memNo = System.Int32.Parse(memNoString);
                if (memNo == myCallChannelMemory) m = CallChannel.Item;
                else m = Memories.mems[memNo];
                m.myLock.WaitOne();
                m.State = memoryStates.inProcess;
                if (rxtx == 0)
                {
                    m.Type = (memNo > 289) ? MemoryTypes.Range : MemoryTypes.Normal;
                    m.Number = memNo;
                }
                string wkstr;
                wkstr = str.Substring(ofst, 11);
                m.Frequency[rxtx] = System.UInt64.Parse(wkstr);
                ofst += 11;
                if (rxtx == 0)
                {
                    m.Present = (m.Frequency[0] != 0);
                }
                if (m.Present)
                {
                    m.Mode[rxtx] = getMode(str[ofst++]);
                    m.Lockout = (str[ofst++] == '1'); // p6
                    if (rxtx == 0)
                    {
                        m.ToneCTCSS = getToneCTCSS(str[ofst++]); // p7
                        m.ToneFrequency = ToneCTCSSToFreq(str.Substring(ofst, 2));
                        ofst += 2;
                        m.CTSSFrequency = ToneCTCSSToFreq(str.Substring(ofst, 2));
                        ofst += 2;
                        m.DCS = stringToDCS(str.Substring(ofst, 3));
                        ofst += 3; // p10 DCS
                        m.Reverse = (str[ofst++] == '1') ? true : false; // p11
                        m.OffsetDirection = (OffsetDirections)(str[ofst++] - '0');
                        m.OffsetFrequency = System.Int32.Parse(str.Substring(ofst, 9));
                        ofst += 9;
                        m.StepSize = System.Int32.Parse(str.Substring(ofst, 2));
                        ofst += 2;
                        m.GroupID = System.Int32.Parse(str[ofst++].ToString());
                        if (ofst < str.Length)
                        {
                            m.Name = str.Substring(ofst);
                        }

                        // Get the other part of this memory.
                        Callouts.safeSend(BldCmd(kcmdMR + "1" + memNoString));
                    }
                    else
                    {
                        // This memory is complete now.
                        m.Split = ((m.OffsetDirection == OffsetDirections.off) &&
                                   ((m.Frequency[0] != m.Frequency[1]) ||
                                    (m.Mode[0] != m.Mode[1])));
                        m.State = memoryStates.complete;
                        // We need to reflect the memory if it's the call channel.
                        if (memNo == myCallChannelMemory) reflectMemoryData(m);
                    }
                }
                else
                {
                    // memory is empty but complete.
                    m.State = memoryStates.complete;
                }
            }
            catch (ThreadAbortException) { Tracing.TraceLine("MRThreadProc abort", TraceLevel.Error); }
            catch (Exception ex)
            {
                Tracing.TraceLine("MRThread:" + ex.Message, TraceLevel.Error);
                if (m != null) m.State = memoryStates.none;
            }
            finally
            {
                if (m != null)
                {
                    m.myLock.ReleaseMutex();
                }
            }
        }

        // rig-specific memory reflection.
        protected override void actuateRigSpecific(MemoryData m)
        {
            _StepSize = stepIDToSize(m.StepSize, m.Mode[0]);
        }
        #endregion

        // region - menu stuff
        #region menu stuff
        public override int MenuBank
        {
            get
            {
                return base.MenuBank;
            }
            set
            {
                string val = value.ToString();
                Callouts.safeSend(BldCmd(kcmdMF + val));
                // This rig doesn't echo the result back.
                _MenuBank = value;
            }
        }

        Mutex getValueLock;
        private object menuGet(MenuDescriptor md)
        {
            Tracing.TraceLine("getMenuDescriptor:" + md.Number.ToString(), TraceLevel.Info);
            if (md.HasSubMenus)
            {
                Tracing.TraceLine("menuGet:" + md.Number.ToString() + " " + md.Type.ToString(), TraceLevel.Error);
                return null;
            }
            getValueLock.WaitOne(); // one at a time!
            md.Complete = false;
            Callouts.safeSend(BldCmd(kcmdEX + md.commandString));
            // await the result.  contEX() zeros queryComplete.
            int sanity = 20;
            while ((sanity-- > 0) && !md.Complete)
            {
                Thread.Sleep(25);
            }
            getValueLock.ReleaseMutex();
            if (md.Complete)
            {
                // Subtract the Base from a non-text item.
                if (md.Type != MenuTypes.Text)
                {
                    md.val = (int)md.val - md.Base;
                }
            }
            return md.val;
        }
        private void menuSet(MenuDescriptor md, object val)
        {
            Tracing.TraceLine("menuSet:" + md.Number.ToString() + " " + val.ToString(), TraceLevel.Info);
            if (md.HasSubMenus)
            {
                Tracing.TraceLine("menuset:" +md.Number.ToString() + " " + md.Type.ToString(), TraceLevel.Error);
                return;
            }
            string strVal = null;
            switch (md.Type)
            {
                case MenuTypes.Text:
                    strVal = val.ToString();
                    break;
                default:
                    // Add in the Base.
                    val = (int)val + md.Base;
                    // Make sure we format this correctly.
                    int i = (int)Math.Log((double)md.High, 10);
                    string fmt = "d" + (++i).ToString();
                    strVal = ((int)val).ToString(fmt);
                    break;
            }
            Callouts.safeSend(BldCmd(Kenwood.kcmdEX + md.commandString + strVal));
            // Wait til it's really set.
            menuGet(md);
        }

        // Region - menu statics
        #region menuStatics
#if !MenuTest
        // base-level menu static data.
        // The submenus must be setup first due to initialization order.
        private static MenuDescriptor[] memoryChannelSubs =
        {
            new MenuDescriptor(
                new menuStatics(1, "memory-VFO split operation",
                    MenuTypes.OnOff,0,1)),
            new MenuDescriptor(
                new menuStatics(2,"tunable (ON) or fixed (OFF) memory channel frequencies",
                    MenuTypes.OnOff,0,1)),
        };
        private static MenuDescriptor[] DTMFMenus =
        {   new MenuDescriptor(
                new menuStatics(1,"DTMF number memory",
                    MenuTypes.NumberRange,0,9)),
            new MenuDescriptor(
                new menuStatics(2,"TX speed for stored DTMF number",
                    MenuTypes.Enumerated,0,1,
                    new string[] {"slow","fast"})),
            new MenuDescriptor(
                new menuStatics(3,"pause duration for stored DTMF number",
                    MenuTypes.Enumerated,0,5,
                    new string[] {"100ms","250ms","500ms","750ms","1000ms","1500ms","2000ms"})),
            new MenuDescriptor(
                new menuStatics(4,"enable Mic remote control",
                    MenuTypes.OnOff,0,1))                            
        };
        private static MenuDescriptor[] playbackMenus =
        {   new MenuDescriptor(
                new menuStatics(1,"repeat the playback",
                    MenuTypes.OnOff,0,1)),
            new MenuDescriptor(
                new menuStatics(2,"interval time for repeating",
                    MenuTypes.NumberRange,0,60))
        };
        private static MenuDescriptor[] linearMenus =
        {   new MenuDescriptor(
                new menuStatics(1, "linear amplifier control delay for HF band",
                    MenuTypes.NumberRangeOff0,0,2)),
            new MenuDescriptor(
                new menuStatics(2, "linear amplifier control delay for 50 MHz band",
                    MenuTypes.NumberRangeOff0,0,2)),
            new MenuDescriptor(
                new menuStatics(3, "linear amplifier control delay for 144 MHz band",
                    MenuTypes.NumberRangeOff0,0,2)),
            new MenuDescriptor(
                new menuStatics(4, "linear amplifier control delay for 430 (440) MHz band",
                    MenuTypes.NumberRangeOff0,0,2)),
            new MenuDescriptor(
                new menuStatics(5, "linear amplifier control delay for 1.2 GHz band",
                    MenuTypes.NumberRangeOff0,0,2))
        };
        private static MenuDescriptor[] squelchMenus =
        {   new MenuDescriptor(
                new menuStatics(1,"enable S-meter squelch",
                    MenuTypes.OnOff,0,1)),
            new MenuDescriptor(
                new menuStatics(2,"hang time for S-meter squelch",
                    MenuTypes.NumberRange,0,3,
                    new string[] {"off","125ms","250ms","500ms"}))
        };
        private static MenuDescriptor[] PCTMenus =
        {   new MenuDescriptor(
                new menuStatics(1,"packet cluster tune mode",
                    MenuTypes.Enumerated,0,1,
                    new string[] {"manual","auto"})),
            new MenuDescriptor(
                new menuStatics(2,"packet cluster RX confirmation tone",
                    MenuTypes.Enumerated,0,2,
                    new string[] {"off","morse","voice"}))
        };
        private static MenuDescriptor[] packetMenus =
        {   new MenuDescriptor(
                new menuStatics(1,"packet filter bandwidth",
                    MenuTypes.OnOff,0,1)),
            new MenuDescriptor(
                new menuStatics(2,"AF input level for packet",
                    MenuTypes.NumberRange,0,9)),
            new MenuDescriptor(
                new menuStatics(3,"main band AF output level for packet operation",
                    MenuTypes.NumberRange,0,9)),
            new MenuDescriptor(
                new menuStatics(4,"sub band AF output level for packet operation",
                    MenuTypes.NumberRange,0,9)),
            new MenuDescriptor(
                new menuStatics(5,"main/sub band:  external TNC",
                    MenuTypes.Enumerated,0,1,
                    new string[] {"main","sub"})),
            new MenuDescriptor(
                new menuStatics(6,"data transfer speed:  external TNC",
                    MenuTypes.Enumerated,0,1,
                    new string[] {"1200","9600"}))
        };
        // add menu descriptions for 0-62
        // These are actually backwards, so Voice1 is 63.
        private static string[] PFChoices =
        {   "off","A.N.","B.C.","N.R.","NB","ANT 1/2","1 MHz","CTRL","CALL",
            "CLR","FINE","CH3","CH2","CH1","CW TUNE","M.IN","M>VFO","SCAN",
            "A=B","VFO/M","A/B","TF-SET","SPLIT","Q.M.IN","Q MR","DSP MONI",
            "RX MONI","VOICE2","VOICE1"}; 
        // the PF key menus are setup at instantiation time.
        private static MenuDescriptor[] PFKeyMenus =
        {   new MenuDescriptor(
                new menuStatics(1,"front panel PF key",
                    MenuTypes.Enumerated,0,0,(string[])null)),
            new MenuDescriptor(
                new menuStatics(2,"microphone PF1 (PF) key",
                    MenuTypes.Enumerated,0,0,(string[])null)),
            new MenuDescriptor(
                new menuStatics(3,"microphone PF2 (MR) key",
                    MenuTypes.Enumerated,0,0,(string[])null)),
            new MenuDescriptor(
                new menuStatics(4,"microphone PF3 (VFO) key",
                    MenuTypes.Enumerated,0,0,(string[])null)),
            new MenuDescriptor(
                new menuStatics(5,"microphone PF4 (call) key",
                    MenuTypes.Enumerated,0,0,(string[])null)),
        };
        private static MenuDescriptor[] repeaterMenus =
        {   new MenuDescriptor(
                new menuStatics(1,"Repeater mode select",
                    MenuTypes.Enumerated,0,2,
                    new string[] {"off","locked","cross"})),
            new MenuDescriptor(
                new menuStatics(2,"repeater TX hold",
                    MenuTypes.OnOff,0,1)),
            new MenuDescriptor(
                new menuStatics(3,"remote control ID code",
                    MenuTypes.NumberRange,0,999)),
            new MenuDescriptor(
                new menuStatics(4,"acknowledgement signal in external remote control mode",
                    MenuTypes.OnOff,0,1)),
            new MenuDescriptor(
                new menuStatics(5,"external remote control",
                    MenuTypes.OnOff,0,1))
        };
        private static string[] CTSSTones =
        {   "67.0", "71.9", "74.4", "77.0", "79.7", "82.5", "85.4", "88.5",
            "91.5", "94.8", "97.4", "100.0", "103.5", "107.2", "110.9",
            "114.8", "118.8", "123.0", "127.3", "131.8", "136.5", "141.3",
            "146.2", "151.4", "156.7", "162.2", "167.9", "173.8", "179.9",
            "186.2", "192.8", "203.5", "210.7", "218.1", "225.7", "233.6",
            "241.8", "1750"
        };
        private static MenuDescriptor[] skyMenus =
        {   new MenuDescriptor(
                new menuStatics(1,"commander callsign for Sky Command II +",
                    MenuTypes.Text,0,0)),
            new MenuDescriptor(
                new menuStatics(2,"transporter callsign for Sky Command II +",
                    MenuTypes.Text,0,0)),
            new MenuDescriptor(
                new menuStatics(3,"Sky Command II + tone frequency",
                    MenuTypes.Enumerated,0,37,1,CTSSTones)),
            new MenuDescriptor(
                new menuStatics(4,"Sky Command II + communication speed",
                    MenuTypes.Enumerated,0,1,
                    new string[] {"1200","9600"})),
            new MenuDescriptor(
                new menuStatics(5,"Sky Command II + mode",
                    MenuTypes.Enumerated,0,3,
                    new string[] {"Off","client","command","T-porter"}))
        };

        // base level menu statics
        private static menuStatics[] myMenus =
        {
            new menuStatics(00, "Display brightness",
                MenuTypes.NumberRangeOff0,0,4),
            new menuStatics(01, "key illumination",
                MenuTypes.OnOff,0,1),
            new menuStatics(02, "tuning control change per revolution",
                MenuTypes.Enumerated,0,1,
                new string[] {"500","1000"}),
            new menuStatics(03, "tuning with MULTI/CH control",
                MenuTypes.OnOff,0,1),
            new menuStatics(04,"Rounds off VFO frequencies changed by using the MULTI/CH control",
                MenuTypes.OnOff,0,1),
            new menuStatics(05, "9kHz frequency step size for the MULTI/CH control",
                MenuTypes.OnOff,0,1),
            new menuStatics(06, "memory channel",
                MenuTypes.SubMenu1,0,1, memoryChannelSubs),
            new menuStatics(07, "program scan partially slowed",
                MenuTypes.OnOff,0,1),
            new menuStatics(08, "slow down frequency range for the Program scan",
                MenuTypes.Enumerated,0,4,
                new string[] {"100","200","300","400","500"}),
            new menuStatics(09, "program scan hold",
                MenuTypes.OnOff,0,1),
            new menuStatics(10, "scan resume method",
                MenuTypes.Enumerated,0,1,
                new string[] {"tone operated","carrier operated"}),
            new menuStatics(11, "visual scan range",
                MenuTypes.Enumerated,0,2,
                new string[] {"31","61","91","181"}),
            new menuStatics(12, "beep output level",
                MenuTypes.NumberRangeOff0,0,9),
            new menuStatics(13, "TX sidetone volume",
                MenuTypes.NumberRangeOff0,0,9),
            new menuStatics(14, "DRU-3A playback volume",
                MenuTypes.NumberRangeOff0,0,9),
            new menuStatics(15, "VS-3 playback volume",
                MenuTypes.NumberRangeOff0,0,9),
            new menuStatics(16, "audio output configuration for EXT.SP2 or headphone",
                MenuTypes.Enumerated,0,2,
                new string[] {"SP1 (L):main/sub mix, SP2 (R):main/sub mix",
                    "SP1 (L): main, SP2 (R): sub.",
                    "SP1 (L): main+1/4 sub mix, SP2 (R): sub+1/4 sub mix."}),
            new menuStatics(17, "reverses the EXT.SP1 and EXT.SP2 (the headphone jack L/R channels) audio outputs",
                MenuTypes.OnOff,0,1),
            new menuStatics(18, "enable an input from the HF RX ANT connector",
                MenuTypes.OnOff,0,1),
            new menuStatics(19, "S-meter squelch",
                MenuTypes.SubMenu1,0,1,squelchMenus),
            new menuStatics(20, "function=DSP Equalizer",
                MenuTypes.Enumerated,0,5,
                new string[] {"Off","H boost","F boost","B boost",
                    "Conventional","User"}),
            new menuStatics(21, "DSP TX equalizer",
                MenuTypes.Enumerated,0,5,
                new string[] {"Off","H boost","F boost","B boost",
                    "Conventional","User"}),
            new menuStatics(22, "DSP TX filter bandwidth for SSB or AM",
                MenuTypes.Enumerated,0,5,
                new string[] {"2.0khz","2.2khz","2.4khz","2.6khz","2.8khz","3.0khz"}),
            new menuStatics(23, "fine transmit power tuning",
                MenuTypes.OnOff,0,1),
            new menuStatics(24, "time-out timer",
                MenuTypes.Enumerated,0,5,
                new string[] {"off","3min","5min","10min","20min","30min"}),
            new menuStatics(25, "transverter frequency display",
                MenuTypes.OnOff,0,1),
            new menuStatics(26, "TX hold when AT completes the tuning",
                MenuTypes.OnOff,0,1),
            new menuStatics(27, "in-line AT while receiving",
                MenuTypes.OnOff,0,1),
            new menuStatics(28, "linear amplifier control",
                MenuTypes.SubMenu1,0,4,linearMenus),
            new menuStatics(29, "voice & CW message playback",
                MenuTypes.SubMenu1,0,1,playbackMenus),
            new menuStatics(30, "keying priority over playback",
                MenuTypes.OnOff,0,1),
            new menuStatics(31, "CW RX pitch/TX sidetone frequency",
                MenuTypes.Enumerated,0,12,
                new string[] {"400hz","450hz","500hz","550hz","600hz","650hz",
                    "700hz","750hz","800hz","850hz","900hz","950hz","1000hz"}),
            new menuStatics(32, "CW rise time",
                MenuTypes.Enumerated,0,3,
                new string[] {"1ms","2ms","4ms","6ms"}),
            new menuStatics(33, "CW keying dot, dash weight ratio",
                MenuTypes.Enumerated,0,15,
                new string[] {"2.5","2.6","2.7","2.8","2.9","3.0",
                    "3.1","3.2","3.3","3.4","3.5","3.6","3.7","3.8","3.9","4.0"}),
            new menuStatics(34, "reverse CW keying auto weight ratio",
                MenuTypes.OnOff,0,1),
            new menuStatics(35, "bug key mode",
                MenuTypes.OnOff,0,1),
            new menuStatics(36, "auto CW TX in SSB mode",
                MenuTypes.OnOff,0,1),
            new menuStatics(37, "frequency correction in changing SSB to CW",
                MenuTypes.OnOff,0,1),
            new menuStatics(38, "FSK shift",
                MenuTypes.Enumerated,0,3,
                new string[] {"170","200","425","850"}),
            new menuStatics(39, "FSK keying polarity",
                MenuTypes.Enumerated,0,1,
                new string[] {"Normal","Reversed"}),
            new menuStatics(40, "FSK tone frequency",
                MenuTypes.Enumerated,0,1,
                new string[] {"1275","2125"}),
            new menuStatics(41, "MIC gain for FM",
                MenuTypes.Enumerated,0,2,
                new string[] {"Low","Mid","High"}),
            new menuStatics(42, "sub-tone mode for FM",
                MenuTypes.Enumerated,0,1,
                new string[] {"burst","cont."}),
            new menuStatics(43, "auto repeater offset",
                MenuTypes.OnOff,0,1),
            new menuStatics(44, "TX hold:  1750 Hz tone",
                MenuTypes.OnOff,0,1),
            new menuStatics(45, "DTMF functions",
                MenuTypes.SubMenu1,0,3,DTMFMenus),
            new menuStatics(46, "main/sub band:  internal TNC",
                MenuTypes.Enumerated,0,1,
                new string[] {"main","sub"}),
            new menuStatics(47, "data transfer speed:  internal TNC",
                MenuTypes.Enumerated,0,1,
                new string[] {"1200","9600"}),
            new menuStatics(48, "DCD sensing band",
                MenuTypes.Enumerated,0,1,
                new string[] {"TNC band","main and sub"}),
            new menuStatics(49, "P.C.T. (Packet Cluster Tune) mode",
                MenuTypes.SubMenu1,0,1,PCTMenus),
            new menuStatics(50, "packet configuration",
                MenuTypes.SubMenu1,0,5,packetMenus),
            new menuStatics(51, "PF key assignment",
                MenuTypes.SubMenu1,0,4,PFKeyMenus),
            new menuStatics(52, "split frequency transfer in master/slave operation",
                MenuTypes.OnOff,0,1),
            new menuStatics(53, "permit to write the transferred split frequencies to the target VFOs",
                MenuTypes.OnOff,0,1),
            new menuStatics(54, "TX inhibit",
                MenuTypes.OnOff,0,1),
            new menuStatics(55, "packet communication mode",
                MenuTypes.OnOff,0,1),
            new menuStatics(56, "COM port communication speed",
                MenuTypes.Enumerated,0,4,
                new string[] {"4800","9600","19200","38400","57600"}),
            new menuStatics(57, "APO (auto power on) function",
                MenuTypes.Enumerated,0,3,
                new string[] {"off","60min","120min","180min"}),
            new menuStatics(58, "RC-2000 font in easy operation mode",
                MenuTypes.Enumerated,0,1,
                new string[] {"font1","font2"}),
            new menuStatics(59, "RC-2000 panel/TS-2000(x) dot-matrix display contrast",
                MenuTypes.NumberRange,1,16,1),
            new menuStatics(60, "display mode for RC-2000",
                MenuTypes.Enumerated,0,1,
                new string[] {"negative","positive"}),
            new menuStatics(61, "repeater function",
                MenuTypes.SubMenu1,0,4,repeaterMenus),
            new menuStatics(62, "sky command + configuration",
                MenuTypes.SubMenu1,0,skyMenus.Length-1,skyMenus)
        };
#else
        // just for testing...
        private static MenuDescriptor[] subSubSub1 =
        {
            new MenuDescriptor(
                new menuStatics(0,"Sub sub sub 1 and 2 leaf 1",
                    MenuTypes.NumberRange,1,9)),
            new MenuDescriptor(
                new menuStatics(1,"Sub sub sub 1 and 2 leaf 2",
                    MenuTypes.OnOff,0,1))
        };
        // Same as subSubSub1, but need a distinct object.
        private static MenuDescriptor[] subSubSub2 =
        {
            new MenuDescriptor(
                new menuStatics(0,"Sub sub sub 1 and 2 leaf 1",
                    MenuTypes.NumberRange,1,9)),
            new MenuDescriptor(
                new menuStatics(1,"Sub sub sub 1 and 2 leaf 2",
                    MenuTypes.OnOff,0,1))
        };
        private static MenuDescriptor[] subSubs =
        {
            new MenuDescriptor(
                new menuStatics(0,"Sub sub 1",
                    MenuTypes.SubMenu2,0,0,subSubSub1)),
            new MenuDescriptor(
                new menuStatics(1,"Sub sub 2",
                    MenuTypes.SubMenu2,0,0,subSubSub2))
        };
        private static MenuDescriptor[] memoryChannelSubsWithSub =
        {
            new MenuDescriptor(
                new menuStatics(0, "Sub with subs",
                    MenuTypes.SubMenu2,0,1,subSubs)),
            new MenuDescriptor(
                new menuStatics(1,"sub with enumerated data",
                    MenuTypes.Enumerated,0,1,
                    new String[] {"s1","s2",}))
        };
        // base level
        private static menuStatics[] myMenus =
        {
            new menuStatics(00, "Simple menu",
                MenuTypes.NumberRangeOff0,0,4),
            new menuStatics(01, "One submenu",
                MenuTypes.SubMenu1,0,1, memoryChannelSubs),
            new menuStatics(2,"one sub with 2 subs",
                MenuTypes.SubMenu1,0,1,memoryChannelSubsWithSub)
        };
#endif
        #endregion

        protected override void contEX(string str)
        {
            //base.contEX(str);
            MenuDescriptor md;
            int num = System.Int32.Parse(str.Substring(2, 3));
            // Beep level, menu 12.
            if (num == 12)
            {
                _RigBeepLevel = System.Int32.Parse(str.Substring(9));
            }
            // speech check, menu 15.
            if (num == 15)
            {
                // Don't set the internal level to 0, that's speech off.
                int lvl = System.Int32.Parse(str.Substring(9));
                if (lvl == 0) _RigSpeech = false;
                else
                {
                    _RigSpeech = true;
                    _RigSpeechLevel = lvl;
                }
            }
            // Special case for menu 18, RX ant.
            if (num == 18)
            {
                _RXAntenna = (str[9] == '1');
            }
            if (MenuBank != MenuBankNotSetup)
            {
                md = Menus[MenuBank, num];
                switch (md.Type)
                {
                    case MenuTypes.SubMenu1:
                        getSubmenuValue(str, md);
                        break;
                    case MenuTypes.SubMenu2:
                        Tracing.TraceLine("contEX:bad type" + str, TraceLevel.Error);
                        break;
                    default:
                        // not a submenu
                        getMenuValue(str, md);
                        break;
                }
                md.Complete = true;
            }
        }
        private void getSubmenuValue(string cmd, MenuDescriptor md)
        {
            Tracing.TraceLine("getSubmenuValue:" + md.Number.ToString() + " " + cmd, TraceLevel.Info);
            int pos = 5; // just past the base menu number.
            int id;
            // Get the submenu1 descriptor.
            try
            {
                id = System.Int32.Parse(cmd.Substring(pos, 2)) - 1; // value is 1 based.
                md = md.subMenus[id];
                pos += 2;
                while (md.Type == MenuTypes.SubMenu2)
                {
                    id = cmd[pos++] - '1';
                    md = md.subMenus[id];
                }
                getMenuValue(cmd, md);
            }
            catch (Exception ex)
            { Tracing.TraceLine("getSubmenuValue:" + ex.Message, TraceLevel.Error); }
        }
        private void getMenuValue(string cmd, MenuDescriptor md)
        {
            Tracing.TraceLine("getMenuValue:" + md.Number.ToString() + " " + cmd, TraceLevel.Info);
            try
            {
                if (md.Type == MenuTypes.Text)
                {
                    md.val = cmd.Substring(9);
                }
                else
                {
                    int v;
                    v = System.Int32.Parse(cmd.Substring(9));
                    md.val = v - md.Base;
                }
            }
            catch (Exception ex)
            { Tracing.TraceLine("getMenuValue:" + ex.Message, TraceLevel.Error); }
        }
        #endregion
    }
}
