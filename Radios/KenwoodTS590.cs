//#define MemoryDebug
#define GetMemoriez
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using JJTrace;
using MsgLib;

namespace Radios
{
    /// <summary>
    /// Handle the TS-590 and TS-590SG.
    /// Inherited by KenwoodTS590SG.
    /// </summary>
    public class KenwoodTS590 : Kenwood
    {
        private const string nextValue1Desc = "Next noise reduction value";
        private const string radioIsSG = "This is a TS-590SG, but a TS-590S was selected.";
        private const string radioIsS = "This is a TS-590S, but a TS-590SG was selected.";
        private const string warning = "Warning";

        // Region - capabilities
        #region capabilities
        private static RigCaps.Caps[] TS590capsList =
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
            RigCaps.Caps.ManualTransmit,
        };
        #endregion

        // region - rig-specific properties
        #region RigSpecificProperties
        public override int RigID
        {
            get { return RadioSelection.RIGIDKenwoodTS590; }
        }

        // Use SMeter dots as the index.
        private static int[] sMeterTable =
        {0,1,1,1,2,2,3,4,4,5,6,6,7,8,8,9,
         9+10,9+10,9+10,9+10,9+20,9+30,9+30,9+30,9+30,9+40,9+50,9+50,9+50,9+50,9+60};
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
                // Use the TO or CT command.
                if ((value == myFMToneModes[0]) || (value == myFMToneModes[1]))
                    Callouts.safeSend(BldCmd(kcmdTO + kToneCTCSS(value)));
                else if (value == myFMToneModes[2])
                    Callouts.safeSend(BldCmd(kcmdCT + "1"));
                else Callouts.safeSend(BldCmd(kcmdCT + "2"));
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
                // format is ANx99
                string val = (value + 1).ToString("d1") + "99";
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
                // format is AN9x9
                string val = (value) ? "919" : "909";
                Callouts.safeSend(BldCmd(kcmdAN + val));
            }
        }
        public override bool DriveAmp
        {
            get
            {
                return base.DriveAmp;
            }
            set
            {
                // format is AN99x
                string val = (value) ? "991" : "990";
                Callouts.safeSend(BldCmd(kcmdAN + val));
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
                // Format for setting is RAp1p1 (no p2).
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
                // 0 turns it off with "GC".
                if (value == 0) Callouts.safeSend(BldCmd(kcmdGC + "0"));
                else
                {
                    // If it's off, turn it on first.
                    if (_AGC == 0) Callouts.safeSend(BldCmd(kcmdGC + "3"));
                    Callouts.safeSend(BldCmd(kcmdGT + value.ToString("d2")));
                }
            }
        }

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
                    string cmd = kcmdEX + speechOnOffMenu.ToString("d3") + "0000" +
                        ((value) ? _RigSpeechLevel.ToString("d1") : "0");
                    Callouts.safeSend(BldCmd(cmd));
                    _RigSpeech = value; // in case we don't get a response
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
                    string cmd = kcmdEX + automodeMenu.ToString("d3") + "0000" +
                        ((value) ? '1' : '0');
                    Callouts.safeSend(BldCmd(cmd));
                    _AutoMode = value; // in case we don't get a response
                }
            }
        }

        public override int RigBeepLevel
        {
            get { return base.RigBeepLevel; }
            set
            {
                string data = value.ToString(beepDataLengthFmt);
                string cmd = kcmdEX + beepLevelMenu.ToString("d3") + "0000" + data;
                Callouts.safeSend(BldCmd(cmd));
                _RigBeepLevel = value;
            }
        }

        public override int LineoutGain
        {
            get
            {
                return base.LineoutGain;
            }
            set
            {
                if (value < 0) value = 0;
                if (value > 255) value = 255;
                base.LineoutGain = value;
                Callouts.safeSend(BldCmd(kcmdAG + '0' + value.ToString("d3")));
            }
        }

        // EQ values
        // These are only valid for the TS590SG and 590 with FW vers 2.0 or higher.
        // EQ modes must correspond to parameter p2 of the EQ command.
        private enum eqModes
        {
            none=-1,
            ssb=0,
            ssbData,
            cw,
            fm,
            fmData,
            am,
            amData,
            fsk
        }
        private bool[] _RXEQActive;
        private bool[] _TXEQActive;
        // Get the EQ mode given the Mode and dataMode.
        private eqModes modeToEqmode(ModeValue m, DataModes dm)
        {
            eqModes rv = eqModes.none;
            switch (m.ToString())
            {
                case "lsb":
                case "usb":
                    rv = (dm == DataModes.on) ? eqModes.ssbData : eqModes.ssb;
                    break;
                case "cw":
                case "cwr":
                    rv = eqModes.cw;
                    break;
                case "fm":
                    rv = (dm == DataModes.on) ? eqModes.fmData : eqModes.fm;
                    break;
                case "am":
                    rv = (dm == DataModes.on) ? eqModes.amData : eqModes.am;
                    break;
                case "fsk":
                    rv = eqModes.fsk;
                    break;
            }
            return rv;
        }
        // See if RX EQ active given the Modes.
        internal bool RXEQActive
        {
            get
            {
                return ((TS590SG || (FWVersion >= 2)) &&
                        _RXEQActive[(int)modeToEqmode(Mode, DataMode)]);
            }
        }
        // See if TX EQ active given the Modes.
        internal bool TXEQActive
        {
            get
            {
                string m = Mode.ToString();
                return ((TS590SG || (FWVersion >= 2)) &&
                        ((m != "cw") & (m != "fsk")) &&
                        _TXEQActive[(int)modeToEqmode(Mode, DataMode)]);
            }
        }

        internal int GetRXEQ(int num)
        {
            int pos = 2 + (num * 2);
            int rv = 0;
            try { rv = System.Int32.Parse(URValue.Substring(pos, 2)); }
            catch (Exception ex)
            { Tracing.TraceLine("GetRXEQ exception:" + ex.Message, TraceLevel.Error); }
            return rv;
        }

        internal int GetTXEQ(int num)
        {
            int pos = 2 + (num * 2);
            int rv = 0;
            try { rv = System.Int32.Parse(UTValue.Substring(pos, 2)); }
            catch (Exception ex)
            { Tracing.TraceLine("GetTXEQ exception:" + ex.Message, TraceLevel.Error); }
            return rv;
        }

        internal void SetTXEQ(int num, int value)
        {
            int pos = 2 + (num * 2);
            try
            {
                StringBuilder sb = new StringBuilder(URUTSize);
                if (pos > 0) sb.Append(UTValue.Substring(0, pos));
                sb.Append(value.ToString("d2"));
                if (pos + 2 < URUTSize) sb.Append(UTValue.Substring(pos + 2));
                Callouts.safeSend(BldCmd(kcmdUT + sb.ToString()));
            }
            catch (Exception ex)
            { Tracing.TraceLine("SetRXEQ exception:" + ex.Message, TraceLevel.Error); }
        }

        internal void SetRXEQ(int num, int value)
        {
            int pos = 2 + (num * 2);
            try
            {
                StringBuilder sb = new StringBuilder(URUTSize);
                if (pos > 0) sb.Append(URValue.Substring(0, pos));
                sb.Append(value.ToString("d2"));
                if (pos + 2 < URUTSize) sb.Append(URValue.Substring(pos + 2));
                Callouts.safeSend(BldCmd(kcmdUR + sb.ToString()));
            }
            catch (Exception ex)
            { Tracing.TraceLine("SetRXEQ exception:" + ex.Message, TraceLevel.Error); }
        }

        internal enum TXSources
        {
            normal = 0,
            data = 1,
            tune = 2
        }
        private TXSources _TXSource = TXSources.normal;
        internal TXSources TXSource
        {
            get { return _TXSource; }
            set
            {
                _TXSource = value;
            }
        }

        public override bool Transmit
        {
            get { return base.Transmit; }
            set
            {
                TransmitStatus = value;
                if (value) Callouts.safeSend(BldCmd(kcmdTX +
                    ((int)_TXSource).ToString()));
                else Callouts.safeSend(BldCmd(kcmdRX));
            }
        }
        #endregion

        // Rig status commands, see rigstat() in kenwood.cs.
        private static string[] myStatCommands =
        {
            kcmdVX,
            kcmdFA,
            kcmdFB,
            kcmdSM + "0",
            kcmdAC,
            kcmdAG + '0',
            kcmdAN,
            kcmdBC,
            kcmdCDOffOn,
            kcmdCDThreshold,
            kcmdCG,
            kcmdCN,
            kcmdCT,
            kcmdDA,
            kcmdFL,
            kcmdFR,
            kcmdFT,
            kcmdFW,
            kcmdGC,
            kcmdGT,
            kcmdIF,
            kcmdIS,
            kcmdKS,
            kcmdMF,
            kcmdMG,
            kcmdML,
            kcmdNR,
            kcmdNT,
            kcmdPA,
            kcmdPC,
            kcmdPL,
            kcmdPR,
            kcmdRA,
            kcmdRG,
            kcmdSD,
            kcmdSH,
            kcmdSL,
            kcmdTN,
            kcmdTO,
            kcmdVD,
            kcmdVG,
            kcmdXI
        };

        protected override void customStatus()
        {
            Tracing.TraceLine("TS590 CustomStatus", TraceLevel.Info);

            // Get the radio ID from the rig.
            Callouts.safeSend(BldCmd(kcmdID));
            if (await(() => { return (receivedRadioID != RadioIDs.none); }, 1000))
            {
                Tracing.TraceLine("customStatus:radio ID " + receivedRadioID.ToString(), TraceLevel.Info);
                // For a 590S or SG, check against the user's selection,
                // otherwise nothing is output.
                if ((RadioID != receivedRadioID) &&
                    ((receivedRadioID == RadioIDs.TS590SG) ||
                     (receivedRadioID == RadioIDs.TS590)))
                {
                    string msg = (receivedRadioID == RadioIDs.TS590SG) ?
                        radioIsSG : radioIsS;
                    string tag = (receivedRadioID == RadioIDs.TS590SG) ?
                        "SSel" : "SGSel";
                    OptionalMessage.Show(msg, warning, tag);
                }
            }
            else
            {
                Tracing.TraceLine("customStatus:radio ID not received", TraceLevel.Error);
            }

            // Get the firmware version.
            Callouts.safeSend(BldCmd(kcmdFV));
            if (await(() => { return (!string.IsNullOrEmpty(_FWVersion)); }, 1000))
            {
                Tracing.TraceLine("open:FW version " + _FWVersion, TraceLevel.Info);
            }
            else
            {
                Tracing.TraceLine("open:didn't get FW version", TraceLevel.Error);
            }

            // Get the beep level, speech and automode settings.
            Callouts.safeSend(BldCmd(kcmdEX + beepLevelMenu.ToString("d3") + "0000"));
            Callouts.safeSend(BldCmd(kcmdEX + speechOnOffMenu.ToString("d3") + "0000"));
            Callouts.safeSend(BldCmd(kcmdEX + automodeMenu.ToString("d3") + "0000"));
            if (TS590SG)
            {
                // Get the low/high-shift/width settings.
                Thread.Sleep(50);
                Callouts.safeSend(BldCmd(kcmdEX + "0280000"));
                Callouts.safeSend(BldCmd(kcmdEX + "0290000"));
            }
            if (TS590SG || (FWVersion >= 2))
            {
                // Get eq status.
                foreach (eqModes e in Enum.GetValues(typeof(eqModes)))
                {
                    if (e == eqModes.none) continue;
                    Thread.Sleep(50);
                    Callouts.safeSend(BldCmd(kcmdEQ + '0' + ((int)e).ToString("d1"))); // TX
                    Callouts.safeSend(BldCmd(kcmdEQ + '1' + ((int)e).ToString("d1"))); // RX
                }
                Thread.Sleep(50);
                Callouts.safeSend(BldCmd(kcmdUR));
                Callouts.safeSend(BldCmd(kcmdUT));
            }
        }

        // Valid FM tone modes for this rig.
        private static ToneCTCSSValue[] myFMToneModes =
        {
            new ToneCTCSSValue('0', "Off"),
            new ToneCTCSSValue('1', "Tone"),
            new ToneCTCSSValue('2', "CTCSS"),
            new ToneCTCSSValue('3', "CTCSSX")
        };
        // Valid tone/CTSS frequencies
        internal static float[] myToneFrequencyTable =
        {
            67.0F, 69.3F, 71.9F, 74.4F, 77.0F, 79.7F, 82.5F, 85.4F, 88.5F, 91.5F,
            94.8F, 97.4F, 100.0F, 103.5F, 107.2F, 110.9F, 114.8F, 118.8F, 123.0F,
            127.3F, 131.8F, 136.5F, 141.3F, 146.2F, 151.4F, 156.7F, 162.2F, 167.9F,
            173.8F, 179.9F, 186.2F, 192.8F, 203.5F, 206.5F, 210.7F, 218.1F, 225.7F,
            229.1F, 233.6F, 241.8F, 250.3F, 254.1F, 1750F
        };

        public KenwoodTS590()
        {
            Tracing.TraceLine("KenwoodTS590 constructor", TraceLevel.Info);
            RadioID = RadioIDs.TS590;

            // myCaps is replaced if on the SG.
            myCaps = new RigCaps(TS590capsList);
        }

        public override bool Open(OpenParms p)
        {
            Tracing.TraceLine("KenwoodTS590 open:" + RadioID.ToString(), TraceLevel.Info);
            bool rv = base.Open(p);
            IsOpen = rv;

            if (rv)
            {
                p.NextValue1 = nextNRValue;
                p.NextValue1Description = nextValue1Desc;
                int EQNumModes = Enum.GetValues(typeof(eqModes)).Length;
                _RXEQActive = new bool[EQNumModes];
                _TXEQActive = new bool[EQNumModes];

                Memories = new MemoryGroup(totalMemories, this);

                // provide rig status commands.
                statCommands = myStatCommands;

                // Provide valid FM tone modes and tone frequencies for this rig.
                CTTOState = CTTOStates.none;
                FMToneModes = myFMToneModes;
                ToneFrequencyTable = myToneFrequencyTable;

                // Setup 2 menu banks.
                // myMenus returns the appropriate bank for this model.
                Menus = new MenuDescriptor[2, myMenus.Length];
                for (int bank = 0; bank < Menus.GetLength(0); bank++)
                {
                    for (int m = 0; m < Menus.GetLength(1); m++)
                    {
                        Menus[bank, m] = new MenuDescriptor(myMenus[m]);
                        Menus[bank, m].commandString = m.ToString("d3");
                        Menus[bank, m].getRtn = menuGet;
                        Menus[bank, m].setRtn = menuSet;
                    }
                }

                // Setup the rig-dependent user control.
                new TS590Filters(this);
            }
            return rv;
        }

        public override void close()
        {
            Tracing.TraceLine("KenwoodTS590 close", TraceLevel.Info);

            // Careful if we're acquiring memories!
            try
            {
                if ((memThread != null) && memThread.IsAlive)
                {
                    memThread.Abort();
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TS590 close memThread exception:" + ex.Message, TraceLevel.Error);
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
                Tracing.TraceLine("TS590 close MRThread exception:" + ex.Message, TraceLevel.Error);
            }

            try
            {
                if ((CWDecodeThread != null) && CWDecodeThread.IsAlive)
                {
                    CWDecodeThread.Abort();
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("TS590 close CWDecodeThread exception:" + ex.Message, TraceLevel.Error);
            }

            // The user should have removed the control.
            RigFields = null;

            base.close();
        }

        // region - rig output handlers
        #region output handlers
        protected override void contIF(string str)
        {
            int ofst = 2; // frequency offset
            ulong freq;
            int i;
            RigCaps.VFOs ovfo; // other VFO.
            bool mem; // memory mode
            string freqStr = str.Substring(ofst, 11);
            freq = System.UInt64.Parse(freqStr);
            ofst += 11 + 5;
            string rs = str.Substring(ofst, 5);
            RIT.Value = stringToRIT(rs);
            XIT.Value = stringToRIT(rs);
            ofst += 5;
            RIT.Active = (str.Substring(ofst++, 1) == "1");
            XIT.Active = (str.Substring(ofst++, 1) == "1");
            _CurrentMemoryChannel = getMemoryChannel(str.Substring(ofst, 3));
            ofst += 3; // mem channel
            TransmitStatus = (str.Substring(ofst++, 1) == "1");
            if (Transmit) _TXFrequency = freq;
            else _RXFrequency = freq;
            ModeValue md = getMode(str[ofst++]);
            if (Transmit) _TXMode = md;
            else _RXMode = md;
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
            else return;
            ofst += 1; // Scan status.
            bool Splt = (str.Substring(ofst++, 1) == "1");
            // Set the other VFO/freq.
            if (Splt)
            {
                if (!mem)
                {
                    // Split VFOs.
                    if (Transmit) _RXVFO = (RigCaps.VFOs)ovfo;
                    else _TXVFO = (RigCaps.VFOs)ovfo;
                } // else other vfo already set to None.
                // Get xmit frequency.
            }
            else
            {
                // symplex
                if (Transmit)
                {
                    _RXVFO = TXVFO;
                    _RXFrequency = _TXFrequency;
                    _RXMode = _TXMode;
                }
                else
                {
                    _TXVFO = RXVFO;
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

        private ToneCTCSSValue itoneCT; // Internal setting, see contCT and contTO
        private enum CTTOStates
        {
            none,
            CTRcvd,
            TORcvd
        }
        private CTTOStates CTTOState;

        protected override void contTO(string str)
        {
            Tracing.TraceLine("contTO:" + CTTOState.ToString(), TraceLevel.Info);
            switch (str[2])
            {
                case '0':
                    // Only set if no CT.
                    if (CTTOState == CTTOStates.none) itoneCT = myFMToneModes[0];
                    break;
                case '1':
                    itoneCT = myFMToneModes[1];
                    break;
            }
            if (CTTOState == CTTOStates.CTRcvd)
            {
                // The pair is complete.
                _ToneCTCSS = itoneCT;
                CTTOState = CTTOStates.none;
            }
            else CTTOState = CTTOStates.TORcvd;
        }

        protected override void contCT(string str)
        {
            Tracing.TraceLine("contCT:" + CTTOState.ToString(), TraceLevel.Info);
            switch (str[2])
            {
                case '0':
                    // only set if TO not received.
                    if (CTTOState == CTTOStates.none) itoneCT = myFMToneModes[0];
                    break;
                case '1': itoneCT = myFMToneModes[2]; break;
                case '2': itoneCT = myFMToneModes[3]; break;
            }
            if (CTTOState == CTTOStates.TORcvd)
            {
                // The pair is complete.
                _ToneCTCSS = itoneCT; // make the change.
                CTTOState = CTTOStates.none;
            }
            else CTTOState = CTTOStates.CTRcvd; // preceeded the TO.
        }

        protected override void contAN(string str)
        {
            _TXAntenna = (str[2] == '2') ? 1 : 0; // value is 1-based.
            _RXAntenna = (str[3] == '1') ? true : false;
            _DriveAmp = (str[4] == '1') ? true : false;
        }

        internal enum AGCGroupType
        { off, slow, fast }
        private AGCGroupType GCVal;
        internal AGCGroupType AGCGroup
        {
            get { return GCVal; }
            set
            {
                // if turned off, first turn on.
                if ((GCVal == AGCGroupType.off) && (value != AGCGroupType.off))
                    Callouts.safeSend(BldCmd(kcmdGC + "3"));
                Callouts.safeSend(BldCmd(kcmdGC + ((int)value).ToString()));
            }
        }
        protected override void contGC(string str)
        {
            // If turned off, set agcVal to 0.
            // If turning on, get the current agcVal.
            GCVal = (AGCGroupType)(str[2] - '0');
            if (GCVal == AGCGroupType.off) _AGC = 0;
            else Callouts.safeSend(BldCmd(kcmdGT)); // get the level
        }
        // Value low/high/incr for display routine.
        internal const int AGCLow = 1;
        internal const int AGCHigh = 20;
        internal const int AGCIncrement = 1;

        protected override void contPA(string str)
        {
            // format is PAp1p2 (p2 always 0)
            preA = (str[2] == '1') ? OffOnValues.on : OffOnValues.off;
        }

        protected override void contSM(string str)
        {
            _SMeter = System.Int32.Parse(str.Substring(3));
            // For panning.
            raiseRigOutput(RigCaps.Caps.SMGet, (ulong)_SMeter);
        }
        /// <summary>
        /// Used by panning
        /// </summary>
        public override void UpdateMeter()
        {
            Callouts.safeSend(BldCmd(kcmdSM + "0"));
        }
        #endregion

        // Region - memory stuff
        #region memoryStuff
        private const int total590Memories = 110;
        private const int total590SGMemories = 120;
        private int totalMemories
        {
            get { return (TS590S) ? total590Memories : total590SGMemories; }
        }
        private bool extendedMemory(int n)
        {
            return (n >= total590Memories);
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
        /// <summary>
        /// Get the data into the specified memory.
        /// </summary>
        /// <param name="m">memory object in the memories group, the number must be set.</param>
        /// <returns>true if gotten successfully.</returns>
        internal override bool getMem(MemoryData m)
        {
            Tracing.TraceLine("getMem:" + m.Number.ToString(), TraceLevel.Info);
            int sanity = 10;
            bool rv;
            m.myLock.WaitOne();
            while (!m.complete && (sanity-- > 0))
            {
                // load once only.
                if (m.State == memoryStates.none)
                {
                    loadMem(m); // Sets m.State.
                }
                // await response, up to .5 seconds, see sanity.
                m.myLock.ReleaseMutex();
                Thread.Sleep(50);
                m.myLock.WaitOne();
            }
            rv = m.complete;
            m.myLock.ReleaseMutex();
            Tracing.TraceLine("getMem:returning " + rv.ToString(),TraceLevel.Info);
            return rv;
        }
        /// <summary>
        /// Set the radio's memory.
        /// </summary>
        /// <param name="m">a memoryData object</param>
        internal override void setMem(MemoryData m)
        {
            Tracing.TraceLine("SetMem:" + m.Number.ToString(), TraceLevel.Info);
            // Fix the name if too long.
            if ((m.Name != null) && (m.Name.Length > 8))
                m.Name = m.Name.Substring(0, 8);
            // Get the memory object to lock.
            MemoryData mg = Memories.mems[m.Number];
            string memnoStr = m.Number.ToString("d3");
            mg.myLock.WaitOne();
            mg.State = memoryStates.none;
            // if the present flag isn't set, it's a deletion.
            if (!m.Present)
            {
                m.Frequency[0] = m.Frequency[1] = 0;
                m.Mode[0] = m.Mode[1] = ModeTable[0]; // mode none
                m.DataMode[0] = m.DataMode[1] = DataModes.off;
                m.ToneCTCSS = myFMToneModes[0];
                m.ToneFrequency = m.CTSSFrequency = ToneFrequencyTable[0];
                m.FMMode = 0;
                m.Lockout = false;
                m.Name = "";
                m.Split = false;
                m.Type = MemoryTypes.Normal;
            }
            // Send the MW0.
            if (m.ToneCTCSS == null) m.ToneCTCSS = myFMToneModes[0];
            string mem0str = kcmdMW + "0" + memnoStr + setFreqToneData(m, 0) +
                m.ToneCTCSS.value +
                kToneCTCSSFreq(m.ToneFrequency) +
                kToneCTCSSFreq(m.CTSSFrequency) +
                "000" + "0" + "0" + "000000000" +
                ((int)m.FMMode).ToString("d2") +
                ((m.Lockout) ? '1' : '0') +
                m.Name;
            // safesend traces commands.
            Callouts.safeSend(BldCmd(mem0str));
            // See if need to send an MW1.
            if (m.Split || (m.Type == MemoryTypes.Range))
            {
                string mem1str = kcmdMW + "1" + memnoStr + setFreqToneData(m, 1) +
                    m.ToneCTCSS.value +
                    kToneCTCSSFreq(m.ToneFrequency) +
                    kToneCTCSSFreq(m.CTSSFrequency) +
                    "000" + "0" + "0" + "000000000" +
                    ((int)m.FMMode).ToString("d2") +
                    ((m.Lockout) ? '1' : '0') +
                    m.Name;
                Callouts.safeSend(BldCmd(mem1str));
            }
            mg.State = memoryStates.none; // Next request reads it in.
            mg.myLock.ReleaseMutex();
            // Finally, get the updated memory from the rig.
            //Callouts.safeSend(BldCmd(kcmdMR + "0" + memnoStr));
        }
        private string setFreqToneData(MemoryData m, int id)
        {
            Tracing.TraceLine("setFreqToneData:" + m.Number.ToString() + "," + id.ToString(), TraceLevel.Info);
            string rv = m.Frequency[id].ToString("d11") +
                KMode(m.Mode[id]) + ((int)m.DataMode[id]).ToString("d1");
            return rv;
        }

        private Thread memThread;
        private Mutex memThreadLock = new Mutex();
        protected override void GetMemories()
        {
            Tracing.TraceLine("KenwoodTS590 GetMemories", TraceLevel.Info);
            base.GetMemories();
#if GetMemoriez
            memThread = new Thread(new ThreadStart(CollectMemories));
            memThread.Name = "CollectMemories";
            try { memThread.Start(); }
            catch (Exception ex)
            { Tracing.TraceLine("KenwoodTS590 GetMemories:" + ex.Message, TraceLevel.Error); }
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
                    // Keep trying until complete.
                    while (m.State != memoryStates.complete)
                    {
                        if (m.State == memoryStates.none) loadMem(m); // Sets m.State.
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
                Tracing.TraceLine("CollectMemories exception:" + ex.Message, TraceLevel.Error);
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
            int nGroups = totalMemories / 10;
            bool atLeastOne = false;
            List<ScanGroup> groups = new List<ScanGroup>();
            List<MemoryData> list;
            for (int i = 0; i < nGroups; i++)
            {
                list = new List<MemoryData>();
                for (int j = 0; j < 10; j++)
                {
                    if (Memories.mems[i * 10 + j].Present) list.Add(Memories.mems[i * 10 + j]);
                }
                if (list.Count > 0)
                {
                    atLeastOne = true;
                    groups.Add(new ScanGroup("reserved" + i.ToString(), Memories.Bank, list, true));
                }
            }
            if (!atLeastOne) groups = null;
            Tracing.TraceLine("GetReservedGroups:" + groups.Count, TraceLevel.Info);
            return groups;
        }
        private void debugMemoryData(MemoryData m)
        {
#if MemoryDebug
          //Tracing.TraceLine("memory " + m.Number.ToString());
            if (!m.Present)
            {
              //Tracing.TraceLine(" empty");
            }
            else
            {
              //DBGW(" type=" + m.Type.ToString() + " split=" + m.Split.ToString());
              //DBGW(" freq=" + m.Frequency[0].ToString() + " " + m.Frequency[1].ToString());
              //DBGW(" mode=" + m.Mode[0].ToString() + " " + m.Mode[1].ToString());
              //DBGW(" DataMode=" + m.DataMode[0].ToString() + " " + m.DataMode[1].ToString());
              //DBGW(" ToneCTCSS=" + m.ToneCTCSS.ToString());
              //DBGW(" name=" + m.Name);
            }
#endif
        }

        /// <summary>
        /// Handle MR from the rig.
        /// </summary>
        /// <param name="o">the MR command including MR</param>
        protected override void MRThreadProc(object o)
        {
            Tracing.TraceLine("MRThreadProc:" + o.ToString(), TraceLevel.Info);
            string str = o.ToString();
            // if rxtx and frequency are 0, all done, not present.
            // frequency, mode, and dataMode come from rxtx 0 and 1.
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
                m = Memories.mems[memNo];
                m.myLock.WaitOne();
                m.State = memoryStates.inProcess;
                if (rxtx == 0)
                {
                    m.Type = ((c == '0') || extendedMemory(memNo)) ? MemoryTypes.Normal : MemoryTypes.Range;
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
                    m.DataMode[rxtx] = getDataMode(str[ofst++]);
                    if (rxtx == 0)
                    {
                        m.ToneCTCSS = getToneCTCSS(str[ofst++]);
                        m.ToneFrequency = ToneCTCSSToFreq(str.Substring(ofst, 2));
                        ofst += 2;
                        m.CTSSFrequency = ToneCTCSSToFreq(str.Substring(ofst, 2));
                        ofst += 2;
                        ofst += 3; // p10
                        ofst += 1; // p11
                        ofst += 1; // p12
                        ofst += 9; // p13
                        m.FMMode = System.Int32.Parse(str.Substring(ofst, 2));
                        ofst += 2;
                        m.Lockout = (str[ofst++] == '1');
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
                        m.Split = ((m.Frequency[0] != m.Frequency[1]) ||
                            (m.Mode[0] != m.Mode[1]) ||
                            (m.DataMode[0] != m.DataMode[1]));
                        m.State = memoryStates.complete;
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
        #endregion

        // region - menu stuff
        #region menu stuff
        Mutex getValueLock = new Mutex();
        private object menuGet(MenuDescriptor md)
        {
            Tracing.TraceLine("menuGet:" + md.Number.ToString(), TraceLevel.Info);
            try
            {
                getValueLock.WaitOne(); // one at a time!
                md.Complete = false;
                Callouts.safeSend(BldCmd(kcmdEX + md.commandString + "0000"));
                // await the result.  contEX() zeros queryComplete.
                await(() => { return md.Complete; }, 500);
                // Subtract the Base from a non-text item.
                if (md.Type != MenuTypes.Text)
                {
                    md.val = (int)md.val - md.Base;
                }
                getValueLock.ReleaseMutex();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("menuget:" + ex.Message, TraceLevel.Error);
                return null;
            }
            return md.val;
        }
        private void menuSet(MenuDescriptor md, object val)
        {
            Tracing.TraceLine("menuSet:" + md.Number.ToString() + " " + val.ToString(), TraceLevel.Info);
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
            Callouts.safeSend(BldCmd(Kenwood.kcmdEX + md.commandString + "0000" + strVal));
            // Wait til it's really set.
            menuGet(md);
        }
        #region menus
        private static EnumAndValue[] TS590pfKeys =
        {
            new EnumAndValue("000 Display brightness",(int)000),
            new EnumAndValue("001 Display backlight color",(int)001),
            new EnumAndValue("002 Panel key response for double function",(int)002),
            new EnumAndValue("003 Beep output level",(int)003),
            new EnumAndValue("004 Sidetone volume",(int)004),
            new EnumAndValue("005 VGS-1 message playback volume",(int)005),
            new EnumAndValue("006 VGS-1 Voice Guide announcement volume",(int)006),
            new EnumAndValue("007 VGS-1 announcement speed",(int)007),
            new EnumAndValue("008 VGS-1 announcement language",(int)008),
            new EnumAndValue("009 VGS-1 auto announcement",(int)009),
            new EnumAndValue("010 MHz step",(int)010),
            new EnumAndValue("011 Tuning control adjustment rate",(int)011),
            new EnumAndValue("012 Rounds off VFO frequencies changed by using the MULTI/CH control",(int)012),
            new EnumAndValue("013 9 kHz frequency step size for the MULTI/CH control in AM mode",(int)013),
            new EnumAndValue("014 Frequency step size for the MULTI/CH control in SSB/ CW/ FSK mode",(int)014),
            new EnumAndValue("015 Frequency step size for the MULTI/CH control in AM mode",(int)015),
            new EnumAndValue("016 Frequency step size for the MULTI/CH control in FM mode",(int)016),
            new EnumAndValue("017 Number of quick memory channels",(int)017),
            new EnumAndValue("018 Tunable memory recall frequencies",(int)018),
            new EnumAndValue("019 Program scan partially slowed",(int)019),
            new EnumAndValue("020 Slow down frequency range for the program scan",(int)020),
            new EnumAndValue("021 Program scan hold",(int)021),
            new EnumAndValue("022 Scan resume method",(int)022),
            new EnumAndValue("023 Auto mode operation",(int)023),
            new EnumAndValue("024 Auto notch tracking speed",(int)024),
            new EnumAndValue("025 TX filter for SSB/AM low cut",(int)025),
            new EnumAndValue("026 TX filter for SSB/AM high cut",(int)026),
            new EnumAndValue("027 TX filter for SSB-DATA low cut",(int)027),
            new EnumAndValue("028 TX filter for SSB-DATA high cut",(int)028),
            new EnumAndValue("029 Speech processor effect",(int)029),
            new EnumAndValue("030 DSP TX equalizer",(int)030),
            new EnumAndValue("031 DSP RX equalizer",(int)031),
            new EnumAndValue("032 Electric keyer mode",(int)032),
            new EnumAndValue("033 Keying priority over playback",(int)033),
            new EnumAndValue("034 CW RX pitch/ TX sidetone frequency",(int)034),
            new EnumAndValue("035 CW rise time",(int)035),
            new EnumAndValue("036 CW keying dot, dash weight ratio",(int)036),
            new EnumAndValue("037 Reverse CW keying auto weight ratio",(int)037),
            new EnumAndValue("038 Bug key function",(int)038),
            new EnumAndValue("039 Reversed dot and dash keying",(int)039),
            new EnumAndValue("040 MIC UP/DWN key paddle function",(int)040),
            new EnumAndValue("041 Auto CW TX when keying in SSB",(int)041),
            new EnumAndValue("042 Frequency correction for changing SSB to CW",(int)042),
            new EnumAndValue("043 No Break-in operation while adjusting keying speed",(int)043),
            new EnumAndValue("044 FSK shift",(int)044),
            new EnumAndValue("045 FSK keying polarity",(int)045),
            new EnumAndValue("046 FSK tone frequency",(int)046),
            new EnumAndValue("047 MIC gain for FM",(int)047),
            new EnumAndValue("048 Fine transmission power tuning",(int)048),
            new EnumAndValue("049 Time-out timer",(int)049),
            new EnumAndValue("050 Xverter/ power down of Xverter",(int)050),
            new EnumAndValue("051 TX hold when AT completes the tuning",(int)051),
            new EnumAndValue("052 In-line AT while receiving",(int)052),
            new EnumAndValue("053 Linear amplifier control relay for HF band",(int)053),
            new EnumAndValue("054 Linear amplifier control relay for 50 MHz band",(int)054),
            new EnumAndValue("055 Constant recording",(int)055),
            new EnumAndValue("056 Repeat the playback",(int)056),
            new EnumAndValue("057 Interval time for repeating the playback",(int)057),
            new EnumAndValue("058 Split frequency transfer in master/ slave operation",(int)058),
            new EnumAndValue("059 Permit to write the transferred Split frequencies to the target VFOs",(int)059),
            new EnumAndValue("060 TX inhibit",(int)060),
            new EnumAndValue("061 COM port communication speed",(int)061),
            new EnumAndValue("062 USB port communication speed",(int)062),
            new EnumAndValue("063 Audio input line selection for data communications",(int)063),
            new EnumAndValue("064 Audio level of USB input for DATA communications",(int)064),
            new EnumAndValue("065 Audio level of USB output for DATA communications",(int)065),
            new EnumAndValue("066 Audio level of ACC2 input for data communications",(int)066),
            new EnumAndValue("067 AUDIO level of ACC2 output for data communications",(int)067),
            new EnumAndValue("068 Mixing beep tones for ACC2/USB audio output",(int)068),
            new EnumAndValue("069 Data VOX",(int)069),
            new EnumAndValue("070 Data VOX delay time",(int)070),
            new EnumAndValue("071 Data VOX gain for the USB audio input",(int)071),
            new EnumAndValue("072 Data VOX gain for the ACC2 terminal",(int)072),
            new EnumAndValue("073 PKS polarity",(int)073),
            new EnumAndValue("074 Busy lockout (TX)",(int)074),
            new EnumAndValue("075 CTCSS mute control",(int)075),
            new EnumAndValue("076 PSQ control signal logic",(int)076),
            new EnumAndValue("077 PSQ source output condition",(int)077),
            new EnumAndValue("078 APO (Auto Power Off) function",(int)078),
            new EnumAndValue("079 Front panel PF A key assignment",(int)079),
            new EnumAndValue("080 Front panel PF-key assignment",(int)080),
            new EnumAndValue("081 Microphone PF 1 key assignmen Permit to write the transferred Split frequencies to the target VFOs",(int)081),
            new EnumAndValue("082 Microphone PF 2 key assignment",(int)082),
            new EnumAndValue("083 Microphone PF 3 key assignment",(int)083),
            new EnumAndValue("084 Microphone PF 4 key assignment",(int)084),
            new EnumAndValue("085 Microphone DWN key assignment",(int)085),
            new EnumAndValue("086 Microphone UP key assignment",(int)086),
            new EnumAndValue("087 Power on message",(int)087),
            new EnumAndValue("100 RX ANT ",(int)100),
            new EnumAndValue("101 ANT 1/2 ",(int)101),
            new EnumAndValue("102 VOX LEVEL ",(int)102),
            new EnumAndValue("103 PROC LEVEL ",(int)103),
            new EnumAndValue("104 AT/TUNE Possible press and hold",(int)104),
            new EnumAndValue("105 CAR ",(int)105),
            new EnumAndValue("106 TX-MONI ",(int)106),
            new EnumAndValue("107 KEY DELAY ",(int)107),
            new EnumAndValue("108 DRV ",(int)108),
            new EnumAndValue("109 REV ",(int)109),
            new EnumAndValue("110 FM-N ",(int)110),
            new EnumAndValue("111 F.LOCK ",(int)111),
            new EnumAndValue("112 NB LEV ",(int)112),
            new EnumAndValue("113 NR LEV ",(int)113),
            new EnumAndValue("114 AUTO NOTCH ",(int)114),
            new EnumAndValue("115 NOTCH WIDE ",(int)115),
            new EnumAndValue("116 CH1 Possible press and hold",(int)116),
            new EnumAndValue("117 CH2 Possible press and hold",(int)117),
            new EnumAndValue("118 CH3 Possible press and hold",(int)118),
            new EnumAndValue("119 CH4 Possible press and hold",(int)119),
            new EnumAndValue("120 RX Possible press and hold",(int)120),
            new EnumAndValue("121 A=B ",(int)121),
            new EnumAndValue("122 AGC SEL ",(int)122),
            new EnumAndValue("123 TONE SEL ",(int)123),
            new EnumAndValue("124 AGC OFF ",(int)124),
            new EnumAndValue("125 Q-MR ",(int)125),
            new EnumAndValue("126 Q-M.IN ",(int)126),
            new EnumAndValue("127 DRV ",(int)127),
            new EnumAndValue("128 SPLIT Mic [PF2] default",(int)128),
            new EnumAndValue("129 TF-SET ",(int)129),
            new EnumAndValue("130 A/B Mic [PF1] default",(int)130),
            new EnumAndValue("131 SCAN Possible press and hold",(int)131),
            new EnumAndValue("132 M>V Mic [PF3] default",(int)132),
            new EnumAndValue("133 M.IN ",(int)133),
            new EnumAndValue("134 CW T. ",(int)134),
            new EnumAndValue("200 VOICE1 [PF A] default",(int)200),
            new EnumAndValue("201 VOICE2 [PF B] default",(int)201),
            new EnumAndValue("202 VOICE3 The lower meter when transmitting",(int)202),
            new EnumAndValue("203 MONITOR Mic [PF4] default",(int)203),
            new EnumAndValue("204 TX TUNE ",(int)204),
            new EnumAndValue("205 DATA SEND The input voice from the data terminal is transmitted",(int)205),
            new EnumAndValue("206 DOWN Mic [UP] default",(int)206),
            new EnumAndValue("207 UP Mic [DWN]default",(int)207),
            new EnumAndValue("208 EMERGENCY frequency call (K type only)",(int)208),
            new EnumAndValue("255 No function",(int)255)
        };

        private static menuStatics[] TS590Menus =
        {
            new menuStatics(00, "Display brightness",
                MenuTypes.NumberRangeOff0,0,6),
            new menuStatics(01, "Display backlight color",
                MenuTypes.Enumerated,0,1,
                new string[] {"amber","green"}),
            new menuStatics(02, "Panel key response for double function",
                MenuTypes.Enumerated,0,2,
                new string[] {".2",".5","1"}),
            new menuStatics(03, "Beep output level",
                MenuTypes.NumberRangeOff0,0,9),
            new menuStatics(04, "Sidetone volume",
                MenuTypes.NumberRangeOff0,0,9),
            new menuStatics(05, "VGS-1 message playback volume",
                MenuTypes.NumberRangeOff0,0,9),
            new menuStatics(06, "VGS-1 Voice Guide announcement volume",
                MenuTypes.NumberRangeOff0,0,7),
            new menuStatics(07, "VGS-1 announcement speed",
                MenuTypes.NumberRange,0,4),
            new menuStatics(08, "VGS-1 announcement language",
                MenuTypes.Enumerated,0,1,
                new string[] {"EN","JP"}),
            new menuStatics(09, "VGS-1 auto announcement",
                MenuTypes.OnOff,0,1),
            new menuStatics(10, "MHz step",
                MenuTypes.Enumerated,0,2,
                new string[] {".1MHZ",".5MHZ","1MHZ"}),
            new menuStatics(11, "Tuning control adjustment rate",
                MenuTypes.Enumerated,0,2,
                new string[] {"250HZ","500HZ","1000HZ"}),
            new menuStatics(12, "Rounds off VFO frequencies changed by using the MULTI/CH control",
                MenuTypes.OnOff,0,1),
            new menuStatics(13, "9 kHz frequency step size for the MULTI/CH control in AM mode",
                MenuTypes.OnOff,0,1),
            new menuStatics(14, "Frequency step size for the MULTI/CH control in SSB/ CW/ FSK mode",
                MenuTypes.Enumerated,0,4,
                new string[] {".5KHZ","1KHZ","2.5KHZ","5KHZ","10KHZ"}),
            new menuStatics(15, "Frequency step size for the MULTI/CH control in AM mode",
                MenuTypes.Enumerated,0,9,
                new string[] {"5KHZ","6.25KHZ","10KHZ","12.5KHZ","15KHZ",
                    "20KHZ","25KHZ","30KHZ","50KHZ","100KHZ"}),
            new menuStatics(16, "Frequency step size for the MULTI/CH control in FM mode",
                MenuTypes.Enumerated,0,9,
                new string[] {"5KHZ","6.25KHZ","10KHZ","12.5KHZ","15KHZ",
                    "20KHZ","25KHZ","30KHZ","50KHZ","100KHZ"}),
            new menuStatics(17, "Number of quick memory channels",
                MenuTypes.Enumerated,0,2,
                new string[] {"3","5","10"}),
            new menuStatics(18, "Tunable memory recall frequencies",
                MenuTypes.OnOff,0,1),
            new menuStatics(19, "Program scan partially slowed",
                MenuTypes.OnOff,0,1),
            new menuStatics(20, "Slow down frequency range for the program scan",
                MenuTypes.Enumerated,0,4,
                new string[] {"100HZ","200HZ","300HZ","400HZ","500HZ"}),
            new menuStatics(21, "Program scan hold",
                MenuTypes.OnOff,0,1),
            new menuStatics(22, "Scan resume method",
                MenuTypes.Enumerated,0,1,
                new string[] {"TO","CO"}),
            new menuStatics(23, "Auto mode operation",
                MenuTypes.OnOff,0,1),
            new menuStatics(24, "Auto notch tracking speed",
                MenuTypes.NumberRange,0,4),
            new menuStatics(25, "TX filter for SSB/AM low cut",
                MenuTypes.Enumerated,0,5,
                new string[] {"10HZ","100HZ","200HZ","300HZ","400HZ","500HZ"}),
            new menuStatics(26, "TX filter for SSB/AM high cut",
                MenuTypes.Enumerated,0,5,
                new string[] {"2500HZ","2600HZ","2700HZ","2800HZ","2900HZ","3000HZ"}),
            new menuStatics(27, "TX filter for SSB-DATA low cut",
                MenuTypes.Enumerated,0,5,
                new string[] {"10HZ","100HZ","200HZ","300HZ","400HZ","500HZ"}),
            new menuStatics(28, "TX filter for SSB-DATA high cut",
                MenuTypes.Enumerated,0,5,
                new string[] {"2500HZ","2600HZ","2700HZ","2800HZ","2900HZ","3000HZ"}),
            new menuStatics(29, "Speech processor effect",
                MenuTypes.Enumerated,0,1,
                new string[] {"Soft","Hard"}),
            new menuStatics(30, "DSP TX equalizer",
                MenuTypes.Enumerated,0,7,
                new string[] {"Off","High boost1","High boost2",
                    "Formant pass","Bass boost1","Bass boost2","Conventional",
                    "User (Reserved)"}),
            new menuStatics(31, "DSP RX equalizer",
                MenuTypes.Enumerated,0,7,
                new string[] {"Off","High boost1","High boost2",
                    "Formant pass","Bass boost1","Bass boost2","Flat",
                    "User (Reserved)"}),
            new menuStatics(32, "Electric keyer mode",
                MenuTypes.Enumerated,0,1,
                new string[] {"A","B"}),
            new menuStatics(33, "Keying priority over playback",
                MenuTypes.OnOff,0,1),
            new menuStatics(34, "CW RX pitch/ TX sidetone frequency",
                MenuTypes.Enumerated,0,15,
                new string[] {"300","350","400","450","500","550","600",
                    "650","700","750","800","850","900","950","1000"}),
            new menuStatics(35, "CW rise time",
                MenuTypes.Enumerated,0,3,
                new string[] {"1","2","4","6"}),
            new menuStatics(36, "CW keying dot, dash weight ratio",
                MenuTypes.Enumerated,0,16,
                new string[] {"Auto","2.5","2.6","2.7","2.8","2.9",
                    "3.0","3.1","3.2","3.3","3.4","3.5",
                    "3.6","3.7","3.8","3.9","4.0"}),
            new menuStatics(37, "Reverse CW keying auto weight ratio",
                MenuTypes.OnOff,0,1),
            new menuStatics(38, "Bug key function",
                MenuTypes.OnOff,0,1),
            new menuStatics(39, "Reversed dot and dash keying",
                MenuTypes.OnOff,0,1),
            new menuStatics(40, "MIC UP/DWN key paddle function",
                MenuTypes.Enumerated,0,1,
                new string[] {"PF","Paddle"}),
            new menuStatics(41, "Auto CW TX when keying in SSB",
                MenuTypes.OnOff,0,1),
            new menuStatics(42, "Frequency correction for changing SSB to CW",
                MenuTypes.OnOff,0,1),
            new menuStatics(43, "No Break-in operation while adjusting keying speed",
                MenuTypes.OnOff,0,1),
            new menuStatics(44, "FSK shift",
                MenuTypes.Enumerated,0,3,
                new string[] {"170","200","425","850"}),
            new menuStatics(45, "FSK keying polarity",
                MenuTypes.Enumerated,0,1,
                new string[] {"Normal","Reversed"}),
            new menuStatics(46, "FSK tone frequency",
                MenuTypes.Enumerated,0,1,
                new string[] {"1275","2125"}),
            new menuStatics(47, "MIC gain for FM",
                MenuTypes.Enumerated,0,2,
                new string[] {"Low","Mid","High"}),
            new menuStatics(48, "Fine transmission power tuning",
                MenuTypes.OnOff,0,1),
            new menuStatics(49, "Time-out timer in minutes",
                MenuTypes.Enumerated,0,5,
                new string[] {"Off","3","5","10","20","30"}),
            new menuStatics(50, "Xverter/ power down of Xverter",
                MenuTypes.Enumerated,0,2,
                new string[] {"Off","5 watts","full power"}),
            new menuStatics(51, "TX hold when AT completes the tuning",
                MenuTypes.OnOff,0,1),
            new menuStatics(52, "In-line AT while receiving",
                MenuTypes.OnOff,0,1),
            new menuStatics(53, "Linear amplifier control relay for HF band",
                MenuTypes.Enumerated,0,3,
                new string[] {"Off","10ms, no relay","10ms, relay","25ms, relay"}),
            new menuStatics(54, "Linear amplifier control relay for 50 MHz band",
                MenuTypes.Enumerated,0,3,
                new string[] {"Off","10ms, no relay","10ms, relay","25ms, relay"}),
            new menuStatics(55, "Constant recording",
                MenuTypes.OnOff,0,1),
            new menuStatics(56, "Repeat the playback",
                MenuTypes.OnOff,0,1),
            new menuStatics(57, "Interval time for repeating the playback (in seconds)",
                MenuTypes.NumberRange,0,60),
            new menuStatics(58, "Split frequency transfer in master/ slave operation",
                MenuTypes.OnOff,0,1),
            new menuStatics(59, "Permit to write the transferred Split frequencies to the target VFOs",
                MenuTypes.OnOff,0,1),
            new menuStatics(60, "TX inhibit",
                MenuTypes.OnOff,0,1),
            new menuStatics(61, "COM port communication speed",
                MenuTypes.Enumerated,0,5,
                new string[] {"4800","9600","19200","38400","57600","115200"}),
            new menuStatics(62, "USB port communication speed",
                MenuTypes.Enumerated,0,5,
                new string[] {"4800","9600","19200","38400","57600","115200"}),
            new menuStatics(63, "Audio input line selection for data communications",
                MenuTypes.Enumerated,0,1,
                new string[] {"ACC2","USB"}),
            new menuStatics(64, "Audio level of USB input for DATA communications",
                MenuTypes.NumberRange,0,9),
            new menuStatics(65, "Audio level of USB output for DATA communications",
                MenuTypes.NumberRange,0,9),
            new menuStatics(66, "Audio level of ACC2 input for data communications",
                MenuTypes.NumberRange,0,9),
            new menuStatics(67, "AUDIO level of ACC2 output for data communications",
                MenuTypes.NumberRange,0,9),
            new menuStatics(68, "Mixing beep tones for ACC2/USB audio output",
                MenuTypes.OnOff,0,1),
            new menuStatics(69, "Data VOX",
                MenuTypes.OnOff,0,1),
            new menuStatics(70, "Data VOX delay time",
                MenuTypes.Enumerated,0,20,
                new string[] {"0","5","10","15","20","25","30","35","40","45","50",
                    "55","60","65","70","75","80","85","90","95","100"}),
            new menuStatics(71, "Data VOX gain for the USB audio input",
                MenuTypes.NumberRange,0,9),
            new menuStatics(72, "Data VOX gain for the ACC2 terminal",
                MenuTypes.NumberRange,0,9),
            new menuStatics(73, "PKS polarity",
                MenuTypes.Enumerated,0,1,
                new string[] {"Normal","Reversed"}),
            new menuStatics(74, "Busy lockout (TX)",
                MenuTypes.OnOff,0,1),
            new menuStatics(75, "CTCSS mute control",
                MenuTypes.Enumerated,0,1,
                new string[] {"1","2"}),
            new menuStatics(76, "PSQ control signal logic",
                MenuTypes.Enumerated,0,1,
                new string[] {"Lo","Open"}),
            new menuStatics(77, "PSQ source output condition",
                MenuTypes.Enumerated,0,5,
                new string[] {"OFF","BSY","SQL","SND","BSY-SND","SQL-SND"}),
            new menuStatics(78, "Auto Power Off (in minutes)",
                MenuTypes.Enumerated,0,3,
                new string[] {"Off","60","120","180"}),
            new menuStatics(79, "Front panel PF A key assignment",
                MenuTypes.Enumerated,0,255,TS590pfKeys),
            new menuStatics(80, "Front panel PF-key assignment",
                MenuTypes.Enumerated,0,255,TS590pfKeys),
            new menuStatics(81, "Microphone PF 1 key assignment",
                MenuTypes.Enumerated,0,255,TS590pfKeys),
            new menuStatics(82, "Microphone PF 2 key assignment",
                MenuTypes.Enumerated,0,255,TS590pfKeys),
            new menuStatics(83, "Microphone PF 3 key assignment",
                MenuTypes.Enumerated,0,255,TS590pfKeys),
            new menuStatics(84, "Microphone PF 4 key assignment",
                MenuTypes.Enumerated,0,255,TS590pfKeys),
            new menuStatics(85, "Microphone DWN key assignment",
                MenuTypes.Enumerated,0,255,TS590pfKeys),
            new menuStatics(86, "Microphone UP key assignment",
                MenuTypes.Enumerated,0,255,TS590pfKeys),
            new menuStatics(87, "Power on message",
                MenuTypes.Text,0,0)
        };

        private static EnumAndValue[] TS590SGpfKeys =
        {
            new EnumAndValue("000 Version information", (int)000),
            new EnumAndValue("001 Power on message", (int)001),
            new EnumAndValue("002 Display brightness", (int)002),
            new EnumAndValue("003 Display backlight color", (int)002),
            new EnumAndValue("004 Panel key response for double function", (int)003),
            new EnumAndValue("005 Beep output level", (int)005),
            new EnumAndValue("006 Sidetone volume", (int)006),
            new EnumAndValue("007 VGS-1 message playback volume", (int)007),
            new EnumAndValue("008 VGS-1 Voice Guide announcement volume", (int)008),
            new EnumAndValue("009 VGS-1 announcement speed", (int)009),
            new EnumAndValue("010 VGS-1 announcement language", (int)010),
            new EnumAndValue("011 VGS-1 auto announcement", (int)011),
            new EnumAndValue("012 MHz step", (int)012),
            new EnumAndValue("013 Tuning control adjustment rate", (int)013),
            new EnumAndValue("014 Rounds off VFO frequencies changed by using the MULTI/CH control", (int)014),
            new EnumAndValue("015 9 kHz frequency step size for the MULTI/CH control in AM mode", (int)015),
            new EnumAndValue("016 Frequency step size for the MULTI/CH control in SSB mode", (int)016),
            new EnumAndValue("017 Frequency step size for the MULTI/CH control in CW/ FSK mode", (int)017),
            new EnumAndValue("018 Frequency step size for the MULTI/CH control in AM mode", (int)018),
            new EnumAndValue("019 Frequency step size for the MULTI/CH control in FM mode", (int)019),
            new EnumAndValue("020 RX frequency during split transmission", (int)020),
            new EnumAndValue("021 Number of quick memory channels", (int)021),
            new EnumAndValue("022 Temporary variable of the standard/ Extention memory frequency", (int)022),
            //new EnumAndValue("018 Tunable memory recall frequencies", (int)018),
            new EnumAndValue("023 Program scan slow down", (int)023),
            new EnumAndValue("024 Slow down frequency range for the program scan", (int)024),
            new EnumAndValue("025 Program scan hold", (int)025),
            new EnumAndValue("026 Scan resume method", (int)026),
            new EnumAndValue("027 Auto mode change", (int)027),
            new EnumAndValue("028 Low Cut/ Low Cut and Width/ Shift change (SSB)", (int)028),
            new EnumAndValue("029 Low Cut/ Low Cut and Width/ Shift change (SSB Data)", (int)029),
            new EnumAndValue("030 Auto notch tracking speed", (int)030),
            new EnumAndValue("031 TX filter for SSB/AM low cut", (int)031),
            new EnumAndValue("032 TX filter for SSB/AM high cut", (int)032),
            new EnumAndValue("033 TX filter for SSB-DATA low cut", (int)033),
            new EnumAndValue("034 TX filter for SSB-DATA high cut", (int)034),
            new EnumAndValue("035 Speech processor effect", (int)035),
            new EnumAndValue("036 transmit equalizer", (int)036),
            new EnumAndValue("037 Receive equalizer", (int)037),
            new EnumAndValue("038 Electric keyer mode", (int)038),
            new EnumAndValue("039 Insert keying", (int)039),
            new EnumAndValue("040 CW RX pitch/ TX sidetone frequency", (int)040),
            new EnumAndValue("041 CW clipping (ms)", (int)041),
            new EnumAndValue("042 CW keying dot, dash weight ratio", (int)042),
            new EnumAndValue("043 Reverse CW keying auto weight ratio", (int)043),
            new EnumAndValue("044 Bug key function", (int)044),
            new EnumAndValue("045 Paddle dot/dash replacement setting", (int)045),
            new EnumAndValue("046 MIC paddle function", (int)046),
            new EnumAndValue("047 Auto CW TX when keying in SSB", (int)047),
            new EnumAndValue("048 Frequency correction for changing SSB to CW", (int)048),
            new EnumAndValue("049 No Break-in operation while adjusting keying speed", (int)049),
            new EnumAndValue("050 FSK shift", (int)050),
            new EnumAndValue("051 FSK keying polarity", (int)051),
            new EnumAndValue("052 FSK tone frequency", (int)052),
            new EnumAndValue("053 MIC gain for FM", (int)053),
            new EnumAndValue("054 Fine transmission power tuning", (int)054),
            new EnumAndValue("055 Time-out timer in minutes", (int)055),
            new EnumAndValue("056 Xverter/ power down of Xverter", (int)056), // ?
            new EnumAndValue("057 TX hold when AT completes the tuning", (int)057),
            new EnumAndValue("058 AT operation while receiving", (int)058),
            new EnumAndValue("059 Linear amplifier control relay for HF band", (int)059), // ?
            new EnumAndValue("060 Linear amplifier control relay for 50 MHz band", (int)060), // ?
            new EnumAndValue("061 Constant recording", (int)061),
            new EnumAndValue("062 Voice/message playback repeat", (int)062),
            new EnumAndValue("063 Interval time for repeating the playback (in seconds)", (int)063),
            new EnumAndValue("064 Split frequency transfer in master/ slave operation", (int)064),
            new EnumAndValue("065 write the transferred Split frequencies to the target VFOs", (int)065),
            new EnumAndValue("066 Transmit inhibit", (int)066),
            new EnumAndValue("067 COM port communication speed", (int)067),
            new EnumAndValue("068 USB port communication speed", (int)068),
            new EnumAndValue("069 DATA modulation line", (int)069),
            new EnumAndValue("070 Audio source of SEND/PTT transmission for data mode", (int)070),
            new EnumAndValue("071 Audio level of USB input for DATA communications", (int)071),
            new EnumAndValue("072 Audio level of USB output for DATA communications", (int)072),
            new EnumAndValue("073 Audio level of ACC2 input for data communications", (int)073),
            new EnumAndValue("074 AUDIO level of ACC2 output for data communications", (int)074),
            new EnumAndValue("075 Mixing beep tones for ACC2/USB audio output", (int)068),
            new EnumAndValue("076 Data VOX", (int)076),
            new EnumAndValue("077 Data VOX delay time", (int)077),
            new EnumAndValue("078 Data VOX gain for the USB audio input", (int)078),
            new EnumAndValue("079 Data VOX gain for the ACC2 terminal", (int)079),
            new EnumAndValue("080 PKS polarity change", (int)080),
            new EnumAndValue("081 Busy transmit inhibit", (int)081),
            new EnumAndValue("082 CTCSS mute control", (int)082),
            new EnumAndValue("083 PSQ control signal logic", (int)083),
            new EnumAndValue("084 PSQ source output condition", (int)084),
            new EnumAndValue("085 DRV connector output function", (int)085),
            new EnumAndValue("086 APO function (minutes)", (int)086),
            new EnumAndValue("087 Front panel PF A key assignment", (int)087),
            new EnumAndValue("088 Front panel PF B key assignment", (int)088),
            new EnumAndValue("089 RIT key assignment", (int)089),
            new EnumAndValue("090 XIT key assignment", (int)090),
            new EnumAndValue("091 CL key assignment", (int)091),
            new EnumAndValue("092 Front panel MULTI/CH key assignment (exclude CW mode)", (int)092),
            new EnumAndValue("093 Front panel MULTI/CH key assignment (CW mode)", (int)093),
            new EnumAndValue("094 Microphone PF 1 key assignment", (int)094),
            new EnumAndValue("095 Microphone PF 2 key assignment", (int)095),
            new EnumAndValue("096 Microphone PF 3 key assignment", (int)096),
            new EnumAndValue("097 Microphone PF 4 key assignment", (int)097),
            new EnumAndValue("098 Microphone PF DWN key assignment", (int)098),
            new EnumAndValue("099 Microphone PF UP key assignment", (int)099),
            new EnumAndValue("120 RX ANT", (int)120),
            new EnumAndValue("121 ATT", (int)121),
            new EnumAndValue("122 ANT1/2", (int)122),
            new EnumAndValue("123 PRE", (int)123),
            new EnumAndValue("124 VOX (Press and hold: enter the level setup mode.)", (int)124),
            new EnumAndValue("125 PROC (Press and hold: enter the level setup mode.  )", (int)125),
            new EnumAndValue("126 SEND", (int)126),
            new EnumAndValue("127 AT (Press and hold: start the antenna tuning.  )", (int)127),
            new EnumAndValue("128 CAR", (int)128),
            new EnumAndValue("129 MIC", (int)129),
            new EnumAndValue("130 TX-MONI", (int)130),
            new EnumAndValue("131 PWR ([MULT/CH] default (except CW mode))", (int)131),
            new EnumAndValue("132 DELAY", (int)132),
            new EnumAndValue("133 KEY ([MULT/CH] default (CW mode))", (int)133),
            new EnumAndValue("134 DRV (Selected ANT: ANT OUT on/off)", (int)134),
            new EnumAndValue("135 METER", (int)135),
            new EnumAndValue("136 LSB/USB", (int)136),
            new EnumAndValue("137 CW/FSK (Press and hold: REV)", (int)137),
            new EnumAndValue("138 FM/AM (Press and hold: NAR)", (int)138),
            new EnumAndValue("139 DATA (When the CW Morse decoder is ON, press and hold: enter the threshold level adjustment mode.)", (int)139),
            new EnumAndValue("140 F.LOCK", (int)140),
            new EnumAndValue("141 FINE", (int)141),
            new EnumAndValue("142 IF FIL (Press and hold: enter the bandwidth display.)", (int)142),
            new EnumAndValue("143 NB (Press and hold: enter the level setup mode.)", (int)143),
            new EnumAndValue("144 NR (Press and hold: enter the level setup mode.)", (int)144),
            new EnumAndValue("145 AUTO NOTCH", (int)145),
            new EnumAndValue("146 BC", (int)146),
            new EnumAndValue("147 NOTCH (Press and hold: NOTCH WIDE.)", (int)147),
            new EnumAndValue("148 SPLIT (Mic [PF2] default)", (int)148),
            new EnumAndValue("149 TF-SET", (int)149),
            new EnumAndValue("150 A=B", (int)150),
            new EnumAndValue("151 A/B (Mic [PF1] default)", (int)151),
            new EnumAndValue("152 M/V", (int)152),
            new EnumAndValue("153 M.IN", (int)153),
            new EnumAndValue("154 M>V (Mic [PF3] default)", (int)154),
            new EnumAndValue("155 Q-M.IN", (int)155),
            new EnumAndValue("156 Q-MR", (int)156),
            new EnumAndValue("157 MHz", (int)157),
            new EnumAndValue("158 SCAN", (int)158),
            new EnumAndValue("159 MENU", (int)159),
            new EnumAndValue("160 CH1", (int)160),
            new EnumAndValue("161 CH2", (int)161),
            new EnumAndValue("162 CH3", (int)162),
            new EnumAndValue("163 CH4", (int)163),
            new EnumAndValue("164 RX", (int)164),
            new EnumAndValue("165 RIT ([RIT] default)", (int)165),
            new EnumAndValue("166 XIT ([XIT] default)", (int)166),
            new EnumAndValue("167 CL ([CL] default)", (int)167),
            new EnumAndValue("168 AGC/T (Press and hold: enter the tone setup mode.)", (int)168),
            new EnumAndValue("169 AGC OFF", (int)169),
            new EnumAndValue("170 CW T.", (int)170),
            new EnumAndValue("200 VOICE1 ([PF A] default)", (int)200),
            new EnumAndValue("201 VOICE2 ([PF B] default)", (int)201),
            new EnumAndValue("202 VOICE3 (The lower meter when transmitting)", (int)202),
            new EnumAndValue("203 MONITOR (Mic [PF4] default)", (int)203),
            new EnumAndValue("204 TX TUNE 1", (int)204),
            new EnumAndValue("205 TX TUNE 2", (int)205),
            new EnumAndValue("206 DATA SEND (The input voice from the data terminal is transmitted)", (int)206),
            new EnumAndValue("207 DWN (Mic [DWN] default)", (int)207),
            new EnumAndValue("208 UP (Mic [UP] default)", (int)208),
            new EnumAndValue("209 EMERGENCY (Emergency frequency call (K type only))", (int)209),
            new EnumAndValue("255 OFF (No function)", (int)255),
        };

        private static menuStatics[] TS590SGMenus =
        {
            new menuStatics(00, "Version information",
                MenuTypes.Text,0,0),
            new menuStatics(01, "Power on message",
                MenuTypes.Text,0,0),
            new menuStatics(02, "Display brightness",
                MenuTypes.NumberRangeOff0,0,6),
            new menuStatics(03, "Display backlight color",
                MenuTypes.Enumerated,0,10,
                new string[] {"amber","2","3","4","5","6","7","8","9","green"}),
            new menuStatics(04, "Panel key response for double function",
                MenuTypes.Enumerated,0,2,
                new string[] {".2",".5","1"}),
            new menuStatics(05, "Beep output level",
                MenuTypes.NumberRangeOff0,0,20),
            new menuStatics(06, "Sidetone volume",
                MenuTypes.NumberRangeOff0,0,20),
            new menuStatics(07, "VGS-1 message playback volume",
                MenuTypes.NumberRangeOff0,0,20),
            new menuStatics(08, "VGS-1 Voice Guide announcement volume",
                MenuTypes.NumberRangeOff0,0,20),
            new menuStatics(09, "VGS-1 announcement speed",
                MenuTypes.NumberRange,0,4),
            new menuStatics(10, "VGS-1 announcement language",
                MenuTypes.Enumerated,0,1,
                new string[] {"EN","JP"}),
            new menuStatics(11, "VGS-1 auto announcement",
                MenuTypes.NumberRangeOff0,0,2),
            new menuStatics(12, "MHz step",
                MenuTypes.Enumerated,0,2,
                new string[] {".1MHZ",".5MHZ","1MHZ"}),
            new menuStatics(13, "Tuning control adjustment rate",
                MenuTypes.Enumerated,0,2,
                new string[] {"250HZ","500HZ","1000HZ"}),
            new menuStatics(14, "Rounds off VFO frequencies changed by using the MULTI/CH control",
                MenuTypes.OnOff,0,1),
            new menuStatics(15, "9 kHz frequency step size for the MULTI/CH control in AM mode",
                MenuTypes.OnOff,0,1),
            new menuStatics(16, "Frequency step size for the MULTI/CH control in SSB mode",
                MenuTypes.Enumerated,0,5,
                new string[] {"off",".5KHZ","1KHZ","2.5KHZ","5KHZ","10KHZ"}),
            new menuStatics(17, "Frequency step size for the MULTI/CH control in CW/ FSK mode",
                MenuTypes.Enumerated,0,5,
                new string[] {"off",".5KHZ","1KHZ","2.5KHZ","5KHZ","10KHZ"}),
            new menuStatics(18, "Frequency step size for the MULTI/CH control in AM mode",
                MenuTypes.Enumerated,0,10,
                new string[] {"off","5KHZ","6.25KHZ","10KHZ","12.5KHZ","15KHZ",
                    "20KHZ","25KHZ","30KHZ","50KHZ","100KHZ"}),
            new menuStatics(19, "Frequency step size for the MULTI/CH control in FM mode",
                MenuTypes.Enumerated,0,10,
                new string[] {"off","5KHZ","6.25KHZ","10KHZ","12.5KHZ","15KHZ",
                    "20KHZ","25KHZ","30KHZ","50KHZ","100KHZ"}),
            new menuStatics(20, "RX frequency during split transmission",
                MenuTypes.OnOff,0,1),
            new menuStatics(21, "Number of quick memory channels",
                MenuTypes.Enumerated,0,2,
                new string[] {"3","5","10"}),
            new menuStatics(22, "TUNABLE MEMORY RECALL FREQUENCIES",
                MenuTypes.OnOff,0,1),
            //new menuStatics(18, "Tunable memory recall frequencies",
            //    MenuTypes.OnOff,0,1),
            new menuStatics(23, "Program scan slow down",
                MenuTypes.OnOff,0,1),
            new menuStatics(24, "Slow down frequency range for the program scan",
                MenuTypes.Enumerated,0,4,
                new string[] {"100HZ","200HZ","300HZ","400HZ","500HZ"}),
            new menuStatics(25, "Program scan hold",
                MenuTypes.OnOff,0,1),
            new menuStatics(26, "Scan resume method",
                MenuTypes.Enumerated,0,1,
                new string[] {"TO","CO"}),
            new menuStatics(27, "Auto mode change",
                MenuTypes.OnOff,0,1),
            new menuStatics(28, "High Cut/Low Cut and Width/Shift change (SSB)",
                MenuTypes.Enumerated,0,1,
                new string[] {"high/low","width/shift"}),
            new menuStatics(29, "High Cut/Low Cut and Width/Shift change (SSB Data)",
                MenuTypes.Enumerated,0,1,
                new string[] {"high/low","width/shift"}),
            new menuStatics(30, "Auto notch tracking speed",
                MenuTypes.NumberRange,0,4),
            new menuStatics(31, "TX filter for SSB/AM low cut",
                MenuTypes.Enumerated,0,5,
                new string[] {"10HZ","100HZ","200HZ","300HZ","400HZ","500HZ"}),
            new menuStatics(32, "TX filter for SSB/AM high cut",
                MenuTypes.Enumerated,0,5,
                new string[] {"2500HZ","2600HZ","2700HZ","2800HZ","2900HZ","3000HZ"}),
            new menuStatics(33, "TX filter for SSB-DATA low cut",
                MenuTypes.Enumerated,0,5,
                new string[] {"10HZ","100HZ","200HZ","300HZ","400HZ","500HZ"}),
            new menuStatics(34, "TX filter for SSB-DATA high cut",
                MenuTypes.Enumerated,0,5,
                new string[] {"2500HZ","2600HZ","2700HZ","2800HZ","2900HZ","3000HZ"}),
            new menuStatics(35, "Speech processor effect",
                MenuTypes.Enumerated,0,1,
                new string[] {"Soft","Hard"}),
            new menuStatics(36, "transmit equalizer",
                MenuTypes.Enumerated,0,7,
                new string[] {"Off","High boost1","High boost2",
                    "Formant pass","Bass boost1","Bass boost2","Conventional",
                    "User (Reserved)"}),
            new menuStatics(37, "Receive equalizer",
                MenuTypes.Enumerated,0,7,
                new string[] {"Off","High boost1","High boost2",
                    "Formant pass","Bass boost1","Bass boost2","Flat",
                    "User (Reserved)"}),
            new menuStatics(38, "Electric keyer mode",
                MenuTypes.Enumerated,0,1,
                new string[] {"A","B"}),
            new menuStatics(39, "KEYING PRIORITY OVER PLAYBACK",
                MenuTypes.OnOff,0,1),
            new menuStatics(40, "CW RX pitch/ TX sidetone frequency",
                MenuTypes.Enumerated,0,15,
                new string[] {"300","350","400","450","500","550","600",
                    "650","700","750","800","850","900","950","1000"}),
            new menuStatics(41, "CW rise time (ms)",
                MenuTypes.Enumerated,0,3,
                new string[] {"1","2","4","6"}),
            new menuStatics(42, "CW keying dot, dash weight ratio",
                MenuTypes.Enumerated,0,16,
                new string[] {"Auto","2.5","2.6","2.7","2.8","2.9",
                    "3.0","3.1","3.2","3.3","3.4","3.5",
                    "3.6","3.7","3.8","3.9","4.0"}),
            new menuStatics(43, "Reverse CW keying auto weight ratio",
                MenuTypes.OnOff,0,1),
            new menuStatics(44, "Bug key function",
                MenuTypes.OnOff,0,1),
            new menuStatics(45, "Reversed dot and dash keying",
                MenuTypes.OnOff,0,1),
            new menuStatics(46, "MIC paddle function",
                MenuTypes.Enumerated,0,1,
                new string[] {"PF","Paddle"}),
            new menuStatics(47, "Auto CW TX when keying in SSB",
                MenuTypes.OnOff,0,1),
            new menuStatics(48, "Frequency correction for changing SSB to CW",
                MenuTypes.OnOff,0,1),
            new menuStatics(49, "No Break-in operation while adjusting keying speed",
                MenuTypes.OnOff,0,1),
            new menuStatics(50, "FSK shift",
                MenuTypes.Enumerated,0,3,
                new string[] {"170","200","425","850"}),
            new menuStatics(51, "FSK keying polarity",
                MenuTypes.Enumerated,0,1,
                new string[] {"Normal","Reversed"}),
            new menuStatics(52, "FSK tone frequency",
                MenuTypes.Enumerated,0,1,
                new string[] {"1275","2125"}),
            new menuStatics(53, "MIC gain for FM",
                MenuTypes.Enumerated,0,2,
                new string[] {"Low","Mid","High"}),
            new menuStatics(54, "Fine transmission power tuning",
                MenuTypes.OnOff,0,1),
            new menuStatics(55, "Time-out timer in minutes",
                MenuTypes.Enumerated,0,5,
                new string[] {"Off","3","5","10","20","30"}),
            new menuStatics(56, "Xverter/ power down of Xverter", // ?
                MenuTypes.Enumerated,0,2,
                new string[] {"Off","1","2"}),
            new menuStatics(57, "TX hold when AT completes the tuning",
                MenuTypes.OnOff,0,1),
            new menuStatics(58, "AT operation while receiving",
                MenuTypes.OnOff,0,1),
            new menuStatics(59, "Linear amplifier control relay for HF band", // ?
                MenuTypes.NumberRangeOff0,0,5),
            new menuStatics(60, "Linear amplifier control relay for 50 MHz band", // ?
                MenuTypes.NumberRangeOff0,0,5),
            new menuStatics(61, "Constant recording",
                MenuTypes.OnOff,0,1),
            new menuStatics(62, "Voice/message playback repeat",
                MenuTypes.OnOff,0,1),
            new menuStatics(63, "Interval time for repeating the playback (in seconds)",
                MenuTypes.NumberRange,0,60),
            new menuStatics(64, "Split frequency transfer in master/ slave operation",
                MenuTypes.OnOff,0,1),
            new menuStatics(65, "write the transferred Split frequencies to the target VFOs",
                MenuTypes.OnOff,0,1),
            new menuStatics(66, "Transmit inhibit",
                MenuTypes.OnOff,0,1),
            new menuStatics(67, "COM port communication speed",
                MenuTypes.Enumerated,0,5,
                new string[] {"4800","9600","19200","38400","57600","115200"}),
            new menuStatics(68, "USB port communication speed",
                MenuTypes.Enumerated,0,5,
                new string[] {"4800","9600","19200","38400","57600","115200"}),
            new menuStatics(69, "DATA modulation line",
                MenuTypes.Enumerated,0,1,
                new string[] {"ACC2","USB"}),
            new menuStatics(70, "Audio source of SEND/PTT transmission for data mode",
                MenuTypes.Enumerated,0,1,
                new string[] {"front","rear"}),
            new menuStatics(71, "Audio level of USB input for DATA communications",
                MenuTypes.NumberRange,0,9),
            new menuStatics(72, "Audio level of USB output for DATA communications",
                MenuTypes.NumberRange,0,9),
            new menuStatics(73, "Audio level of ACC2 input for data communications",
                MenuTypes.NumberRange,0,9),
            new menuStatics(74, "AUDIO level of ACC2 output for data communications",
                MenuTypes.NumberRange,0,9),
            new menuStatics(75, "Mixing beep tones for ACC2/USB audio output",
                MenuTypes.OnOff,0,1),
            new menuStatics(76, "Data VOX",
                MenuTypes.OnOff,0,1),
            new menuStatics(77, "Data VOX delay time",
                MenuTypes.Enumerated,0,20,
                new string[] {"0","5","10","15","20","25","30","35","40","45","50",
                    "55","60","65","70","75","80","85","90","95","100"}),
            new menuStatics(78, "Data VOX gain for the USB audio input",
                MenuTypes.NumberRange,0,9),
            new menuStatics(79, "Data VOX gain for the ACC2 terminal",
                MenuTypes.NumberRange,0,9),
            new menuStatics(80, "PKS polarity change",
                MenuTypes.OnOff,0,1),
            new menuStatics(81, "Busy transmit inhibit",
                MenuTypes.OnOff,0,1),
            new menuStatics(82, "CTCSS mute control",
                MenuTypes.Enumerated,0,1,
                new string[] {"1","2"}),
            new menuStatics(83, "PSQ control signal logic",
                MenuTypes.Enumerated,0,1,
                new string[] {"Lo","Open"}),
            new menuStatics(84, "PSQ source output condition",
                MenuTypes.Enumerated,0,5,
                new string[] {"OFF","BSY","SQL","SND","BSY-SND","SQL-SND"}),
            new menuStatics(85, "DRV connector output function",
                MenuTypes.Enumerated,0,1,
                new string[] {"DRO","ANT"}),
            new menuStatics(86, "APO function (minutes)",
                MenuTypes.Enumerated,0,3,
                new string[] {"Off","60","120","180"}),
            new menuStatics(87, "Front panel PF A key assignment",
                MenuTypes.Enumerated,0,255,TS590SGpfKeys),
            new menuStatics(88, "Front panel PF-key assignment",
                MenuTypes.Enumerated,0,255,TS590SGpfKeys),
            new menuStatics(89, "RIT key assignment",
                MenuTypes.Enumerated,0,255,TS590SGpfKeys),
            new menuStatics(90, "XIT key assignment",
                MenuTypes.Enumerated,0,255,TS590SGpfKeys),
            new menuStatics(91, "CL key assignment",
                MenuTypes.Enumerated,0,255,TS590SGpfKeys),
            new menuStatics(92, "Front panel MULTI/CH key assignment (exclude CW mode)",
                MenuTypes.Enumerated,0,255,TS590SGpfKeys),
            new menuStatics(93, "Front panel MULTI/CH key assignment (CW mode)",
                MenuTypes.Enumerated,0,255,TS590SGpfKeys),
            new menuStatics(94, "Microphone PF 1 key assignment",
                MenuTypes.Enumerated,0,255,TS590SGpfKeys),
            new menuStatics(95, "Microphone PF 2 key assignment",
                MenuTypes.Enumerated,0,255,TS590SGpfKeys),
            new menuStatics(96, "Microphone PF 3 key assignment",
                MenuTypes.Enumerated,0,255,TS590SGpfKeys),
            new menuStatics(97, "Microphone PF 4 key assignment",
                MenuTypes.Enumerated,0,255,TS590SGpfKeys),
            new menuStatics(98, "Microphone DWN key assignment",
                MenuTypes.Enumerated,0,255,TS590SGpfKeys),
            new menuStatics(99, "Microphone UP key assignment",
                MenuTypes.Enumerated,0,255,TS590SGpfKeys),
        };

        /// <summary>
        /// (Readonly) Get the menu bank for this model.
        /// The default is the TS590S menus.
        /// </summary>
        private menuStatics[] myMenus
        {
            get { return (TS590SG) ? TS590SGMenus : TS590Menus; }
        }

        // Menu numbers for beep level, speech and automode
        private int beepLevelMenu
        {
            get { return (TS590S) ? 3 : 5; }
        }
        private String beepDataLengthFmt
        {
            get { return (TS590S) ? "d1" : "d2"; }
        }
        private int speechOnOffMenu
        {
            get { return (TS590S) ? 9 : 11; }
        }
        private int automodeMenu
        {
            get { return (TS590S) ? 23 : 27; }
        }
        #endregion

        protected override void contEX(string str)
        {
            base.contEX(str);
            MenuDescriptor md;
            int num = System.Int32.Parse(str.Substring(2, 3));
            // Check beep level, speech and auto mode
            if (num == beepLevelMenu)
            {
                _RigBeepLevel = System.Int32.Parse(str.Substring(9));
            }
            if (num == speechOnOffMenu)
            {
                int level = System.Int32.Parse(str.Substring(9));
                _RigSpeech = (level != 0);
                if (level > 0) _RigSpeechLevel = level;
            }
            if (num == automodeMenu) _AutoMode = (str[9] == '1');
            if (TS590SG)
            {
                // Check shift/width substitution.
                if (num == 28) _SSBShiftWidth = (str[9] == '1');
                if (num == 29) _SSBDShiftWidth = (str[9] == '1');
            }

            if (MenuBank != MenuBankNotSetup)
            {
                md = Menus[MenuBank, num];
                switch (md.Type)
                {
                    case MenuTypes.Text:
                        md.val = str.Substring(9);
                        break;
                    default:
                        int v = System.Int32.Parse(str.Substring(9));
                        md.val = v;
                        break;
                }
                md.Complete = true;
            }
        }

        internal enum NRtype
        { off, NR1, NR2 }
        private NRtype NRValueVal;
        /// <summary>
        /// TS590 noise reduction
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

        internal const int NRLevel1Low = 1;
        internal const int NRLevel1High = 10;
        internal const int NRLevel1Increment = 1;
        // NRLevel1Val is sent to/received from the radio.
        private int _NRLevel1;
        /// <summary>
        /// NR level 1
        /// </summary>
        internal int NRLevel1
        {
            get { return _NRLevel1; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdRL + value.ToString("d2")));
            }
        }
        internal const int NRLevel2Low = 0;
        internal const int NRLevel2High = 9;
        internal const int NRLevel2Increment = 1;
        // NRLevel2Val is in steps of 2 ms.
        private int _NRLevel2;
        /// <summary>
        /// SPAC time 0 to 9, 2 to 20 ms.
        /// </summary>
        internal int NRLevel2
        {
            get { return _NRLevel2; }
            set
            {
                _NRLevel2 = value;
                Callouts.safeSend(BldCmd(kcmdRL + _NRLevel2.ToString("d2")));
            }
        }
        protected override void contRL(string str)
        {
            int val = System.Int32.Parse(str.Substring(2));
            if (NRValue == NRtype.NR1) _NRLevel1 = val;
            else if (NRValue == NRtype.NR2) _NRLevel2 = val;
        }

        internal enum NBtype
        { off, NB1, NB2 }
        private NBtype NBValueVal;
        /// <summary>
        /// TS590 noise blankers
        /// </summary>
        internal NBtype NBValue
        {
            get { return NBValueVal; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdNB + ((int)value).ToString()));
            }
        }
        protected override void contNB(string str)
        {
            NBValueVal = (NBtype)(str[2] - '0');
            // Get the level.
            if (NBValue != NBtype.off) Callouts.safeSend(BldCmd(kcmdNL));
        }

        internal const int NBLevelLow = 1;
        internal const int NBLevelHigh = 10;
        internal const int NBLevelIncrement = 1;
        private int _NBLevel;
        /// <summary>
        /// TS590 NB Level.
        /// </summary>
        internal int NBLevel
        {
            get { return _NBLevel; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdNL + value.ToString("d3")));
            }
        }
        protected override void contNL(string str)
        {
            if (NBValue != NBtype.off) _NBLevel = System.Int32.Parse(str.Substring(2));
        }

        internal enum notchType
        { off, auto, manual }
        private notchType notchValueVal;
        /// <summary>
        /// TS590 notch
        /// </summary>
        internal notchType notchValue
        {
            get { return notchValueVal; }
            set
            {
                string val = ((int)value).ToString() + ((int)notchWidthVal).ToString();
                Callouts.safeSend(BldCmd(kcmdNT + val));
            }
        }
        internal enum notchWidthType
        { normal, wide }
        private notchWidthType notchWidthVal = notchWidthType.normal;
        internal notchWidthType notchWidth
        {
            get { return notchWidthVal; }
            set
            {
                if (notchValueVal == notchType.manual)
                {
                    string val = ((int)notchType.manual).ToString() + ((int)value).ToString();
                    Callouts.safeSend(BldCmd(kcmdNT + val));
                }
            }
        }
        protected override void contNT(string str)
        {
            notchValueVal = (notchType)(str[2] - '0');
            // Get the width and frequency if manual.
            if (notchValueVal == notchType.manual)
            {
                notchWidthVal = (notchWidthType)(str[3] - '0');
                Callouts.safeSend(BldCmd(kcmdBP5));
            }
        }

        internal const int notchFreqLow = 0;
        internal const int notchFreqHigh = 127;
        internal const int notchFreqIncrement = 1;
        private int notchFreqVal;
        /// <summary>
        /// TS590 manual notch frequency.
        /// </summary>
        internal int notchFreq
        {
            get { return notchFreqVal; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdBP5 + value.ToString("d3")));
            }
        }
        protected override void contBP(string str)
        {
            notchFreqVal = System.Int32.Parse(str.Substring(2));
        }

        internal enum beatCancelType
        { off, bc1, bc2 }
        private beatCancelType beatCancelVal;
        internal beatCancelType beatCancel
        {
            get { return beatCancelVal; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdBC + ((int)value).ToString()));
            }
        }
        protected override void contBC(string str)
        {
            beatCancelVal = (beatCancelType)(str[2] - '0');
        }

        protected override void contCDOffOn(string cmd)
        {
            if (cmd[3] == '1')
            {
                if (CWDecodeQ == null)
                {
                    CWDecodeQ = Queue.Synchronized(new Queue());
                }
                CWDecodeThread = new Thread(CWDecodeProc);
                CWDecodeThread.Name = "CWDecode";
                CWDecodeThread.Start();
                _CWDecode = OffOnValues.on;
            }
            else
            {
                _CWDecode = OffOnValues.off;
                if (CWDecodeQ != null) CWDecodeQ.Clear();
                try
                {
                    if (CWDecodeThread != null)
                    {
                        CWDecodeThread.Abort();
                    }
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("contCDOffOn exception:" + ex.Message, TraceLevel.Error);
                }
                CWDecodeThread = null;
            }
        }

        protected override void contCDThreshold(string cmd)
        {
            _CWDecodeThreshold = System.Int32.Parse(cmd.Substring(3));
        }

        protected override void contCDData(string cmd)
        {
            // If user wants raw radio output, just return.
            if ((sendingOutput != CommandReporting.none) |
                (_CWDecode == OffOnValues.off))
            {
                return;
            }
            for (int i = 3; i < cmd.Length; i++)
            {
                CWDecodeQ.Enqueue(cmd[i]);
            }
        }
        private Queue CWDecodeQ = null;
        private Thread CWDecodeThread = null;
        private void CWDecodeProc()
        {
            Tracing.TraceLine("CWDecodeProc", TraceLevel.Info);
            try
            {
                while (true)
                {
                    while (CWDecodeQ.Count > 0)
                    {
                        Callouts.safeCWTextReceiver(((char)CWDecodeQ.Dequeue()).ToString());
                    }
                    Thread.Sleep(25);
                }
            }
            catch (ThreadAbortException)
            {
                Tracing.TraceLine("CWDecodeProc aborted", TraceLevel.Info);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("CWDecodeProc exception:" + ex.Message, TraceLevel.Error);
            }
        }

        protected override void contEQ(string cmd)
        {
            bool tx = (cmd[2] == '0');
            int id = cmd[3] - '0';
            if (tx) _TXEQActive[id] = (cmd[4] != '0');
            else _RXEQActive[id] = (cmd[4] != '0');
        }

        protected string URValue = null;
        protected string UTValue = null;
        private const int URUTSize = 18 * 2;
        protected override void contUR(string cmd)
        {
            if (cmd.Length == 2 + URUTSize) URValue = cmd.Substring(2);
            else Tracing.TraceLine("contUR:bad length " + cmd.Length.ToString(), TraceLevel.Error);
        }
        protected override void contUT(string cmd)
        {
            if (cmd.Length == 2 + URUTSize) UTValue = cmd.Substring(2);
            else Tracing.TraceLine("contUT:bad length " + cmd.Length.ToString(), TraceLevel.Error);
        }

        // We'll compare this to the radio the user selected.
        private RadioIDs receivedRadioID = RadioIDs.none;
        protected override void contID(string str)
        {
            receivedRadioID = (RadioIDs)System.Int32.Parse(str.Substring(2));
        }
        #endregion
    }
}
