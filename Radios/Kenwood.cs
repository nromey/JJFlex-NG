using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Kenwood superclass
    /// </summary>
    public partial class Kenwood : AllRadios
    {
        // Region - Commands to send to Kenwood rigs.
        #region kenwood commands
        protected const string kcmdAC = "AC"; // Sets or reads the internal antenna tuner status.
        protected const string kcmdAG = "AG"; // Sets or reads the AF gain.
        protected const string kcmdAI = "AI"; // Sets or reads the Auto Information (AI) function ON/ OFF.
        protected const string kcmdAL = "AL"; // Sets or reads the Auto Notch level.
        protected const string kcmdAM = "AM"; // Sets or reads the Auto Mode ON/OFF.
        protected const string kcmdAN = "AN"; // Selects the antenna connector ANT1/ ANT2.
        protected const string kcmdAR = "AR"; // Sets or reads the ASC function ON/ OFF.
        protected const string kcmdAS = "AS"; // Sets or reads the Auto Mode function parameters.
        protected const string kcmdBC = "BC"; // Sets or reads the Beat Cancel function status.
        protected const string kcmdBD = "BD"; // Sets a frequency band.
        protected const string kcmdBP2 = "BP"; // (TS2000) Sets or reads the Manual Beat Canceller frequency settings.
        protected const string kcmdBP5 = "BP"; // (TS590) Adjusts the Notch Frequency of the Manual Notch Filter.
        protected const string kcmdBU = "BU"; // Sets a frequency band.
        protected const string kcmdBY = "BY"; // Reads the busy signal status.
        protected const string kcmdCA = "CA"; // Sets and reads the CW TUNE function status.
        protected const string kcmdCD = "CD"; // base CW decode command.
        protected const string kcmdCDOffOn = "CD0"; // CW decode off/on
        protected const string kcmdCDThreshold = "CD1"; // CW decode threshold
        protected const string kcmdCDData = "CD2"; // CW decode data
        protected const string kcmdCG = "CG"; // Sets and reads the Carrier Level.
        protected const string kcmdCH = "CH"; // Operate the MULTI/CH encoder.
        protected const string kcmdCI = "CI"; // Sets the current frequency to the CALL channel.
        protected const string kcmdCM = "CM"; // Sets or reads the PACKET CLUSTER TUNE function ON/OFF.
        protected const string kcmdCN = "CN"; // Sets and reads the CTCSS frequency.
        protected const string kcmdCT = "CT"; // Sets and reads the CTCSS function status.
        protected const string kcmdDA = "DA"; // Sets and reads the DATA mode.
        protected const string kcmdDC = "DC"; // Sets and reads the TX band status.
        protected const string kcmdDN = "DN"; // Emulates the microphone DWN and UP keys.
        protected const string kcmdDQ = "DQ"; // Sets and reads the DCS function status.
        protected const string kcmdEM = "EM"; // Sets the Emergency communication frequency mode.
        protected const string kcmdEQ = "EQ"; // equalizer
        protected const string kcmdEX = "EX"; // Sets or reads the Menu.
        protected const string kcmdFA = "FA"; // Sets or reads the VFO A frequency.
        protected const string kcmdFB = "FB"; // Sets or reads the VFO B frequency.
        protected const string kcmdFC = "FC"; // Reads and sets the sub-receiver's VFO frequency.
        protected const string kcmdFD = "FD"; // Reads the filter display dot pattern.
        protected const string kcmdFL = "FL"; // Sets and reads the IF filter.
        protected const string kcmdFR = "FR"; // Selects or reads the VFO or Memory channel.
        protected const string kcmdFS = "FS"; // Sets and reads the Fine Tuning function status.
        protected const string kcmdFT = "FT"; // Selects or reads the VFO or Memory channel.
        protected const string kcmdFV = "FV"; // Verifies the Firmware version.
        protected const string kcmdFW = "FW"; // Sets or reads the DSP filtering bandwidth.
        protected const string kcmdGC = "GC"; // Sets or reads the AGC.
        protected const string kcmdGT = "GT"; // Sets or reads the AGC time constant.
        protected const string kcmdID = "ID"; // Reads the transceiver ID number.
        protected const string kcmdIF = "IF"; // Reads the transceiver status. I
        protected const string kcmdIS = "IS"; // Sets and reads the DSP Filter Shift.
        protected const string kcmdKS = "KS"; // Sets and reads the Keying speed.
        protected const string kcmdKY = "KY"; // Converts the entered characters into morse code while keying.
        protected const string kcmdLK = "LK"; // Sets and reads the Lock status.
        protected const string kcmdLM = "LM"; // Sets and reads the VGS-1 electric keyer recording status.
        protected const string kcmdLT = "LT"; // Sets and reads the ALT function status.
        protected const string kcmdMC = "MC"; // Sets and reads the Memory Channel number.
        protected const string kcmdMD = "MD"; // Sets and reads the operating mode status.
        protected const string kcmdMF = "MF"; // Sets and reads Menu A or B.
        protected const string kcmdMG = "MG"; // Sets and reads the microphone gain.
        protected const string kcmdML = "ML"; // Sets and reads the TX Monitor function output level.
        protected const string kcmdMO = "MO"; // Sets the MONITOR function ON/ OFF in Sky Commander mode.
        protected const string kcmdMR = "MR"; // Reads the Memory channel data.
        protected const string kcmdMU = "MU"; // Sets or reads the Memory Group data.
        protected const string kcmdMW = "MW"; // Sets the Memory channel data.
        protected const string kcmdNB = "NB"; // Sets and reads the Noise Blanker function status.
        protected const string kcmdNL = "NL"; // Sets and reads the Noise Blanker level.
        protected const string kcmdNR = "NR"; // Sets and reads the Noise Reduction function status.
        protected const string kcmdNT = "NT"; // Sets and reads the Notch Filter status.
        protected const string kcmdOF = "OF"; // Sets or reads the Offset frequency information.
        protected const string kcmdOI = "OI"; // Reads the Memory channel data.
        protected const string kcmdOS = "OS"; // Sets or reads the offset function status.
        protected const string kcmdPA = "PA"; // Sets and reads the Pre-amplifier function status.
        protected const string kcmdPB = "PB"; // Sets and reads the voice and CW message playback status.
        protected const string kcmdPC = "PC"; // Sets and reads the output power.
        protected const string kcmdPI = "PI"; // Stores in the Programable Memory channel.
        protected const string kcmdPK = "PK"; // Reads the Packet Cluster data.
        protected const string kcmdPL = "PL"; // Sets and reads the Speech Processor input/output level.
        protected const string kcmdPM = "PM"; // Recalls the PM (Programmable Memory).
        protected const string kcmdPR = "PR"; // Sets and reads the Speech Processor function ON/ OFF.
        protected const string kcmdPS = "PS"; // Sets and reads the Power ON/ OFF status.
        protected const string kcmdQC = "QC"; // Sets or reads the DCS code.
        protected const string kcmdQD = "QD"; // Deletes the Quick Memory.
        protected const string kcmdQI = "QI"; // Stores the settings in the Quick Memory.
        protected const string kcmdQR = "QR"; // Sets and reads the Quick Memory channel data.
        protected const string kcmdRA = "RA"; // Sets and reads the RF Attenuator status.
        protected const string kcmdRC = "RC"; // Clears the RIT/XIT frequency.
        protected const string kcmdRD = "RD"; // Sets and reads the RIT/XIT frequency Up/ Down. Also sets and reads the scan speed in Scan mode.
        protected const string kcmdRG = "RG"; // Sets and reads the RF Gain status.
        protected const string kcmdRL = "RL"; // Sets and reads the Noise Reduction Level.
        protected const string kcmdRM = "RM"; // Sets and reads the Meter function.
        protected const string kcmdRS = "RS"; // enter/leave menu mode
        protected const string kcmdRT = "RT"; // Sets and reads the RIT function status.
        protected const string kcmdRU = "RU"; // Sets and reads the RIT/XIT frequency Up/ Down. Also sets and reads the scan speed in Scan mode.
        protected const string kcmdRX = "RX"; // Sets the receiver function status.
        protected const string kcmdSA = "SA"; // Sets or reads the Satellite mode status.
        protected const string kcmdSB = "SB"; // Sets or reads the SUB, TF-W status.
        protected const string kcmdSC = "SC"; // Sets and reads the Scan function status.
        protected const string kcmdSD = "SD"; // Sets and reads the CW break-in time delay.
        protected const string kcmdSH = "SH"; // Sets and reads the slope tune bandwidth high setting.
        protected const string kcmdSI = "SI"; // Enters the Satellite memory name.
        protected const string kcmdSL = "SL"; // Sets and reads the slope tune bandwidth low setting.
        protected const string kcmdSM = "SM"; // Reads the SMeter.
        protected const string kcmdSQ = "SQ"; // Sets and reads the squelch value.
        protected const string kcmdSR = "SR"; // Resets the transceiver.
        protected const string kcmdSS2 = "SS"; // (TS2000) Sets or reads the Program Scan pause frequency.
        protected const string kcmdSS5 = "SS"; // (TS590) Sets and reads the Program Slow Scan frequency.
        protected const string kcmdST = "ST"; // Sets or reads the MULTI/ CH control frequency steps.
        protected const string kcmdSU2 = "SU"; // (TS2000) Sets or reads the Program Scan pause frequency.
        protected const string kcmdSU5 = "SU"; // (TS590) Sets and reads the Scan group.
        protected const string kcmdSV = "SV"; // Performs the Memory Transfer function.
        protected const string kcmdTC = "TC"; // Sets or reads the internal TNC mode.
        protected const string kcmdTD = "TD"; // Sends the DTMF memory channel data.
        protected const string kcmdTI = "TI"; // Reads the TNC LED status.
        protected const string kcmdTN = "TN"; // Sets and reads the Tone frequency.
        protected const string kcmdTO = "TO"; // Sets and reads the Tone status.
        protected const string kcmdTS = "TS"; // Sets and reads the TF-Set status.
        protected const string kcmdTX = "TX"; // Sets the transmission mode.
        protected const string kcmdTY = "TY"; // reads the microprocessor firmware type. 
        protected const string kcmdUL = "UL"; // Detects the PLL unlock status.
        protected const string kcmdUP = "UP"; // Emulates the microphone DWN and UP keys.
        protected const string kcmdUR = "UR"; // Sets and reads the RX equalizer.
        protected const string kcmdUT = "UT"; // Sets and reads the TX equalizer.
        protected const string kcmdVD = "VD"; // Sets and reads the VOX Delay time.
        protected const string kcmdVG = "VG"; // Sets and reads the VOX Gain.
        protected const string kcmdVR = "VR"; // Sets the Voice synthesis generation function.
        protected const string kcmdVS0 = "VS0"; // Sets and reads the Visual Scan start/ stop/ pause status.
        protected const string kcmdVS1 = "VS1"; //  Sets the Visual Scan center frequency.
        protected const string kcmdVS2 = "VS2"; //  Sets the Visual Scan span.
        protected const string kcmdVS3 = "VS3"; //  Reads the Visual Scan upper/ lower/center frequency, and span.
        protected const string kcmdVS4 = "VS4"; //  Reads the Visual Scan sweep frequency and signal level.
        protected const string kcmdVV = "VV"; // Performs the VFO copy (A=B) function.
        protected const string kcmdVX = "VX"; // Sets and reads the VOX and Break-in function status.
        protected const string kcmdXI = "XI"; // Reads the transmit frequency and mode.
        protected const string kcmdXO = "XO"; // Sets and reads the offset direction and frequency for the transverter mode.
        protected const string kcmdXT = "XT"; // Sets and reads the XIT function status.
        #endregion

        internal static string BldCmd(string cmd)
        {
            return cmd + ";";
        }

        internal enum RadioIDs
        {
            none=0,
            TS2000=19,
            TS590=21,
            TS590SG=23
        }
        private RadioIDs _RadioID = RadioIDs.none;
        /// <summary>
        /// Kenwood radio ID
        /// </summary>
        internal RadioIDs RadioID
        {
            get { return _RadioID; }
            set { _RadioID = value; }
        }
        internal bool TS590S
        {
            get { return (RadioID == RadioIDs.TS590); }
        }
        internal bool TS590SG
        {
            get { return (RadioID == RadioIDs.TS590SG); }
        }

        protected string _FWVersion;
        internal float FWVersion
        {
            get
            {
                Single rv = 0;
                try { rv = System.Single.Parse(_FWVersion); }
                catch { Tracing.TraceLine("FWVersion:invalid", TraceLevel.Error); }
                return rv;
            }
        }

        /// <summary>
        /// Power status.
        /// If setting, you must subsequently test to see if it got set.
        /// </summary>
        public override bool Power
        {
            get { return base.Power; }
            set
            {
                // Attempt to set the power status; might not work.
                Tracing.TraceLine("Power set " + value.ToString(),TraceLevel.Info);
                if (value) Callouts.safeSend(BldCmd(kcmdPS+"1"));
                else Callouts.safeSend(BldCmd(kcmdPS+"0"));
            }
        }
        protected override void PowerCheck()
        {
            base.PowerCheck();
            Callouts.safeSend(BldCmd(kcmdPS));
        }

        public override RigCaps.VFOs RXVFO
        {
            get
            {
                return base.RXVFO;
            }
            set
            {
                string val;
                bool saveSplit = Split;
                if (IsMemoryMode(value)) val = "2";
                else val = ((int)value).ToString();
                Callouts.safeSend(BldCmd(kcmdFR + val));
                await(() => { return (_RXVFO == value); }, 500);
                if (saveSplit) Split = true;
                // Ensure VFOFreq array is setup.
                if (!IsMemoryMode(value) && (myVFOFreq[(int)value] == 0)) Callouts.safeSend(BldCmd((value == RigCaps.VFOs.VFOA) ? kcmdFA : kcmdFB));
            }
        }
        public override RigCaps.VFOs TXVFO
        {
            get
            {
                return base.TXVFO;
            }
            set
            {
                string val;
                if (IsMemoryMode(value)) val = "2";
                else val = ((int)value).ToString();
                Callouts.safeSend(BldCmd(kcmdFT + val));
                // Ensure VFOFreq array is setup.
                if (!IsMemoryMode(value) && (myVFOFreq[(int)value] == 0)) Callouts.safeSend(BldCmd((value == RigCaps.VFOs.VFOA) ? kcmdFA : kcmdFB));
            }
        }
        public override void CopyVFO(RigCaps.VFOs inv, RigCaps.VFOs outv)
        {
            if (inv == CurVFO)
            {
                Tracing.TraceLine("CopyVFO:" + inv.ToString() + " to " + outv.ToString(), TraceLevel.Info);
                Callouts.safeSend(BldCmd(kcmdVV));
            }
            else Tracing.TraceLine("CopyVFO:inv must be the current VFO",TraceLevel.Error);
        }
        public override bool SplitShowXmitFrequency
        {
            get
            {
                return base.SplitShowXmitFrequency;
            }
            set
            {
                if (!Transmit)
                {
                    //string val = (TFSetOn) ? "0" : "1";
                    //Callouts.safeSend(BldCmd(kcmdTS + val));
                    if (Split) Callouts.safeSend(BldCmd(kcmdTS + (string)((value) ? "1" : "0")));
                }
            }
        }
        public override ulong RXFrequency
        {
            get { return (TFSetOn) ? _TXFrequency : _RXFrequency; }
            set
            {
                string str = "F" + VFOToLetter(RXVFO) + UFreqToString(value);
                Callouts.safeSend(BldCmd(str));
            }
        }
        public override ulong TXFrequency
        {
            get { return _TXFrequency; }
            set
            {
                string str = "F" + VFOToLetter(TXVFO) + UFreqToString(value);
                Callouts.safeSend(BldCmd(str));
            }
        }
        public override bool Split
        {
            get
            {
                return base.Split;
            }
            set
            {
                if (!IsMemoryMode(RXVFO))
                {
                    // Using VFOs.
                    int v = (int)((value) ? nextVFO(RXVFO) : RXVFO);
                    Callouts.safeSend(BldCmd(kcmdFT + v.ToString()));
                }
                // Else using a memory, can't set it.
            }
        }

        public override int CurrentMemoryChannel
        {
            get
            {
                return base.CurrentMemoryChannel;
            }
            set
            {
                string val = value.ToString("d3");
                Callouts.safeSend(BldCmd(kcmdMC + val));
            }
        }
        public override bool MemoryMode
        {
            get
            {
                return base.MemoryMode;
            }
            set
            {
                if (value)
                {
                    if (!MemoryMode)
                    {
                        oldVFO = CurVFO;
                        Callouts.safeSend(BldCmd(kcmdFR + "2"));
                    }
                    // else already in memory mode.
                }
                else
                {
                    if (MemoryMode)
                    {
                        Callouts.safeSend(BldCmd(kcmdFR + ((int)oldVFO).ToString()));
                    }
                    // else already using a VFO.
                }
            }
        }

        public override ModeValue RXMode
        {
            get
            {
                return base.RXMode;
            }
            set
            {
                char c = KMode(value);
                Callouts.safeSend(BldCmd(kcmdMD + c.ToString()));
            }
        }
        public override ModeValue TXMode
        {
            get
            {
                return base.TXMode;
            }
            set
            {
                char c = KMode(value);
                Callouts.safeSend(BldCmd(kcmdMD + c.ToString()));
            }
        }

        public override DataModes RXDataMode
        {
            get
            {
                return base.RXDataMode;
            }
            set
            {
                char c = kDataMode(value);
                Callouts.safeSend(BldCmd(kcmdDA + c.ToString()));
            }
        }
        public override DataModes TXDataMode
        {
            get
            {
                return base.TXDataMode;
            }
            set
            {
                char c = kDataMode(value);
                Callouts.safeSend(BldCmd(kcmdDA + c.ToString()));
            }
        }

        /// <summary>
        /// Tone frequency
        /// </summary>
        public override float ToneFrequency
        {
            get
            {
                return base.ToneFrequency;
            }
            set
            {
                string s = kToneCTCSSFreq(value);
                Callouts.safeSend(BldCmd(kcmdTN + s));
            }
        }
        /// <summary>
        /// CTSS frequency
        /// </summary>
        public override float CTSSFrequency
        {
            get
            {
                return base.CTSSFrequency;
            }
            set
            {
                string s = kToneCTCSSFreq(value);
                Callouts.safeSend(BldCmd(kcmdCN + s));
            }
        }

        private int _KeyerSpeed;
        internal int KeyerSpeed
        {
            get
            {
                return _KeyerSpeed;
            }
            set
            {
                _KeyerSpeed = value;
                Callouts.safeSend(BldCmd(kcmdKS + value.ToString("d3")));
            }
        }

        public override AntTunerVals AntennaTuner
        {
            get
            {
                return base.AntennaTuner;
            }
            set
            {
                string val = "";
                bool sendCommand = true;
                // if tune is set, other values are "1".
                if ((value & AntTunerVals.tune) != 0)
                {
                    val = "111";
                    _AntennaTuner = (AntTunerVals.tx | AntTunerVals.rx);
                }
                else
                {
                    // Either both are on, or none are.
                    bool wasOn = ((int)_AntennaTuner != 0);
                    if (wasOn && (value != _AntennaTuner))
                    {
                        // turn it off.
                        _AntennaTuner = (AntTunerVals)0;
                        val = "000";
                    }
                    else if (!wasOn && ((int)value != 0))
                    {
                        // turn it on.
                        _AntennaTuner = (AntTunerVals.tx | AntTunerVals.rx);
                        val = "110";
                    }
                    else sendCommand = false; // do nothing.
                }
                if (sendCommand) Callouts.safeSend(BldCmd(kcmdAC + val));
                Tracing.TraceLine("AntennaTuner set:" + _AntennaTuner.ToString() + " "
                    + sendCommand.ToString(), TraceLevel.Verbose);
            }
        }

        protected OffOnValues preA;
        public virtual OffOnValues PreAmp
        {
            get
            {
                return preA;
            }
            set
            {
                // format is PAp1 (p2 only appears on a query)
                string val = (value == OffOnValues.on) ? "1" : "0";
                Callouts.safeSend(BldCmd(kcmdPA + val));
            }
        }

        /// <summary>
        /// Break-in delay maximum in milliseconds
        /// </summary>
        public virtual int BreakinDelayMax { get { return 1000; } }
        /// <summary>
        /// Break-in delay increment
        /// </summary>
        public int BreakinDelayIncrement { get { return 50; } }
        protected int _BreakinDelay;
        /// <summary>
        /// Break-in delay in ms
        /// </summary>
        public virtual int BreakinDelay
        {
            get
            {
                return _BreakinDelay;
            }
            set
            {
                string val = value.ToString("d4");
                Callouts.safeSend(BldCmd(kcmdSD + val));
            }
        }

        internal bool FullBreakin
        {
            get
            {
                return ((_Vox == OffOnValues.on) && (_BreakinDelay == 0));
            }
        }

        /// <summary>
        /// VOX on/off.
        /// </summary>
        public override OffOnValues Vox
        {
            get
            {
                return _Vox;
            }
            set
            {
                string val = (value == OffOnValues.on) ? "1" : "0";
                Callouts.safeSend(BldCmd(kcmdVX + val));
            }
        }

        /// <summary>
        /// Vox delay maximum in milliseconds
        /// </summary>
        public virtual int VoxDelayMax { get { return 3000; } }
        /// <summary>
        /// Vox delay increment in milliseconds.
        /// </summary>
        public virtual int VoxDelayIncrement { get { return 150; } }
        protected int _VoxDelay;
        /// <summary>
        /// VOX delay
        /// </summary>
        public virtual int VoxDelay
        {
            get
            {
                return _VoxDelay;
            }
            set
            {
                string val = value.ToString("d4");
                Callouts.safeSend(BldCmd(kcmdVD + val));
            }
        }

        /// <summary>
        /// Vox gain maximum value
        /// </summary>
        public virtual int VoxGainMax { get { return 9; } }
        /// <summary>
        /// vox gain increment
        /// </summary>
        public virtual int VoxGainIncrement { get { return 1; } }
        protected int _VoxGain;
        /// <summary>
        /// VOX gain
        /// </summary>
        public virtual int VoxGain
        {
            get
            {
                return _VoxGain;
            }
            set
            {
                string val = value.ToString("d3");
                Callouts.safeSend(BldCmd(kcmdVG + val));
            }
        }

        /// <summary>
        /// Mic gain maximum value
        /// </summary>
        public virtual int MicGainMax { get { return 100; } }
        /// <summary>
        /// Mic gain increment
        /// </summary>
        public virtual int MicGainIncrement { get { return 1; } }
        protected int mGain;
        /// <summary>
        /// Mic gain
        /// </summary>
        public virtual int MicGain
        {
            get
            {
                return mGain;
            }
            set
            {
                string val = value.ToString("d3");
                Callouts.safeSend(BldCmd(kcmdMG + val));
            }
        }

        /// <summary>
        /// Max carrier level
        /// </summary>
        public virtual int CarrierLevelMax { get { return 100; } }
        /// <summary>
        /// Carrier level increment
        /// </summary>
        public virtual int CarrierLevelIncrement { get { return 1; } }
        protected int cLevel;
        /// <summary>
        /// Carrier level
        /// </summary>
        public virtual int CarrierLevel
        {
            get
            {
                return cLevel;
            }
            set
            {
                string val = value.ToString("d3");
                Callouts.safeSend(BldCmd(kcmdCG + val));
            }
        }

        protected OffOnValues _ProcessorState;
        public virtual OffOnValues ProcessorState
        {
            get
            {
                return _ProcessorState;
            }
            set
            {
                Callouts.safeSend(BldCmd(kcmdPR + ((value == OffOnValues.on) ? "1" : "0")));
            }
        }

        /// <summary>
        /// Processor input level maximum
        /// </summary>
        public virtual int ProcessorInputLevelMax { get { return 100; } }
        /// <summary>
        /// speech processor input level increment
        /// </summary>
        public virtual int ProcessorInputLevelIncrement { get { return 1; } }
        protected int _ProcessorInputLevel;
        /// <summary>
        /// Processor input level
        /// </summary>
        public virtual int ProcessorInputLevel
        {
            get
            {
                return _ProcessorInputLevel;
            }
            set
            {
                string val = value.ToString("d3") + _ProcessorOutputLevel.ToString("d3");
                Callouts.safeSend(BldCmd(kcmdPL + val));
            }
        }

        /// <summary>
        /// speech processor output maximum
        /// </summary>
        public virtual int ProcessorOutputLevelMax { get { return 100; } }
        /// <summary>
        /// speech processor output level increment
        /// </summary>
        public virtual int ProcessorOutputLevelIncrement { get { return 1; } }
        protected int _ProcessorOutputLevel;
        /// <summary>
        /// Processor output level
        /// </summary>
        public virtual int ProcessorOutputLevel
        {
            get
            {
                return _ProcessorOutputLevel;
            }
            set
            {
                string val = _ProcessorInputLevel.ToString("d3") + value.ToString("d3");
                Callouts.safeSend(BldCmd(kcmdPL + val));
            }
        }

        internal const int TXMonitorMin = 0;
        internal const int TXMonitorMax = 9;
        internal const int TXMonitorIncrement = 1;
        protected int _TXMonitor;
        public int TXMonitor
        {
            get { return _TXMonitor; }
            set
            {
                _TXMonitor = value;
                string val = value.ToString("d3");
                Callouts.safeSend(BldCmd(kcmdML + val));
            }
        }

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
            }
        }

        // filter values.
        private int filtNum;
        internal int filterNum
        {
            get { return filtNum; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdFL + value.ToString("d1")));
            }
        }
        private int filtofst, filtwth;
        internal int filterOffset
        {
            get { return filtofst; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdIS + " " + value.ToString("d4")));
            }
        }
        internal int filterWidth
        {
            get { return filtwth; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdFW + value.ToString("d4")));
            }
        }
        // These are true if TS590SG, and menus 28 and 29 are shift/width, see customStatus() for the TS590.
        protected bool _SSBShiftWidth = false;
        protected bool _SSBDShiftWidth = false;
        /// <summary>
        /// TS590SG Shift/width setting regardless of data mode.
        /// </summary>
        internal bool SSBShiftWidth
        {
            get { return (DataMode == DataModes.off) ? _SSBShiftWidth : _SSBDShiftWidth; }
        }

        private int _filterLow, _filterHigh;
        /// <summary>
        /// Filter Low/width (SL)
        /// </summary>
        internal int filterLow
        {
            get { return _filterLow; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdSL + value.ToString("d2")));
            }
        }
        /// <summary>
        /// filter high/shift (SH)
        /// </summary>
        internal int filterHigh
        {
            get { return _filterHigh; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdSH + value.ToString("d2")));
            }
        }

        // SWR, comp, and ALC values (kcmdRM).
        internal int SWRRaw, compRaw, ALCRaw;

        public override int AGC
        {
            get { return base.AGC; }
            set
            {
                Callouts.safeSend(BldCmd(kcmdGT + value.ToString("d2")));
            }
        }

        public override ushort DCS
        {
            get
            {
                return base.DCS;
            }
            set
            {
                Callouts.safeSend(BldCmd(kcmdQC + kDCS(value)));
            }
        }

        public override RITData RIT
        {
            get
            {
                return base.RIT;
            }
            set
            {
                if (value.Active != _RIT.Active)
                {
                    char c = (value.Active) ? '1' : '0';
                    Callouts.safeSend(BldCmd(kcmdRT + c));
                }
                // Set value only if active.
                if (value.Active)
                {
                    if (value.Value == 0)
                    {
                        Callouts.safeSend(BldCmd(kcmdRC));
                    }
                    else if (value.Value != _RIT.Value)
                    {
                        string cmd = (value.Value > _RIT.Value) ? kcmdRU : kcmdRD;
                        int val = Math.Abs((value.Value - _RIT.Value));
                        Callouts.safeSend(BldCmd(cmd + val.ToString("d5")));
                    }
                }
                _RIT = new RITData(value); // set immediately
                //_XIT.Value = _RIT.Value;
            }
        }

        public override RITData XIT
        {
            get
            {
                return base.XIT;
            }
            set
            {
                if (value.Active != _XIT.Active)
                {
                    char c = (value.Active) ? '1' : '0';
                    Callouts.safeSend(BldCmd(kcmdXT + c));
                }
                if (value.Active)
                {
                    if (value.Value == 0)
                    {
                        Callouts.safeSend(BldCmd(kcmdRC));
                    }
                    else if (value.Value != _XIT.Value)
                    {
                        string cmd = (value.Value > _XIT.Value) ? kcmdRU : kcmdRD;
                        int val = Math.Abs((value.Value - _XIT.Value));
                        Callouts.safeSend(BldCmd(cmd + val.ToString("d5")));
                    }
                }
                _XIT = new RITData(value); // set immediately
            }
        }

        public override void UpdateMeter()
        {
            Callouts.safeSend(BldCmd(kcmdSM));
        }

        // Note that the 2000's RF gain is only for the main receiver, so this is same as for the 590.
        public override int AudioGain
        {
            get
            {
                return base.AudioGain;
            }
            set
            {
                if (value < 0) value = 0;
                if (value > 255) value = 255;
                base.AudioGain = value;
                Callouts.safeSend(BldCmd(kcmdRG + value.ToString("d3")));
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

        protected OffOnValues _CWDecode = OffOnValues.off;
        internal OffOnValues CWDecode
        {
            get { return _CWDecode; }
            set
            {
                if (TS590SG && (value != _CWDecode))
                {
                    Callouts.safeSend(BldCmd(kcmdCDOffOn + ((value == OffOnValues.on) ? '1' : '0')));
                }
            }
        }

        internal const int CWDecodeThresholdMin = 0;
        internal const int CWDecodeThresholdMax = 30;
        internal const int CWDecodeThresholdIncrement = 1;
        protected int _CWDecodeThreshold;
        internal int CWDecodeThreshold
        {
            get { return _CWDecodeThreshold; }
            set
            {
                if (TS590SG && (value != _CWDecodeThreshold))
                {
                    Callouts.safeSend(BldCmd(kcmdCDThreshold + value.ToString("d3")));
                }
            }
        }

        public override void CWZeroBeat()
        {
            Callouts.safeSend(BldCmd(kcmdCA + '1'));
        }

        private KenwoodIhandler rigHandler;
        private void setupResponseActions()
        {
            Tracing.TraceLine("setupResponseActions", TraceLevel.Info);
            Collection<KenwoodIhandler.ResponseItem> items = new Collection<KenwoodIhandler.ResponseItem>();
            items.Add(new KenwoodIhandler.ResponseItem(kcmdAC, contAC));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdAG, contAG));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdAL, contAL));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdAM, contAM));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdAN, contAN));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdBC, contBC));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdBP5, contBP));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdCA, contCA));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdCD, contCD));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdCG, contCG));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdCN, contCN));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdCT, contCT));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdDA, contDA));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdDC, contDC));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdDQ, contDQ));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdEQ, contEQ));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdEX, contEX));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdFA, contFreqA));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdFB, contFreqB));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdFC, contFreqC));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdFL, contFL));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdFR, contFR));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdFT, contFT));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdFV, contFV));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdFW, contFW));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdGC, contGC));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdGT, contGT));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdID, contID));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdIF, contIF));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdIS, contIS));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdKS, contKS));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdKY, contKY));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdMC, contMC));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdMD, contMD));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdMF, contMF));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdMG, contMG));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdML, contML));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdMR, contMR));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdNB, contNB));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdNL, contNL));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdNR, contNR));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdNT, contNT));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdOF, contOF));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdOS, contOS));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdPA, contPA));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdPC, contPC));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdPL, contPL));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdPR, contPR));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdPS, contPS));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdRA, contRA));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdRG, contRG));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdRL, contRL));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdRM, contRM));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdRT, contRIT));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdRX, contRXTX));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdSB, contSB));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdSD, contSD));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdSH, contSH));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdSL, contSL));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdSM, contSM));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdST, contST));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdTN, contTN));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdTO, contTO));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdTS, contTS));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdTX, contRXTX));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdUR, contUR));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdUT, contUT));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdVD, contVD));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdVG, contVG));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdVX, contVox));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdXI, contXI));
            items.Add(new KenwoodIhandler.ResponseItem(kcmdXT, contXIT));

            // Finally, setup the handler.
            rigHandler = new KenwoodIhandler((AllRadios)this, items.ToArray());
        }

        // region - cw sending
#region CW sending
        private class cwSend
        {
            private Kenwood parent;
            private Queue cwq;
            public Thread cwSendThread;
            // See kcmdKY, a 24-char message.
            private char[] buf;
            const int bufferSize = 28;
            const int initialCursorPos = 3; // after "ky "
            const int messageMaxLen = 24;
            // buffer's cursor
            private int cursor = initialCursorPos;
            private float bufDelay;
            private int messageLen
            {
                get { return cursor - initialCursorPos; }
            }
            public cwSend(Kenwood p)
            {
                Tracing.TraceLine("cwSend", TraceLevel.Info);
                parent = p;
                // setup the buffer, see kcmdKY.
                buf = new char[bufferSize];
                buf[0] = 'K'; buf[1] = 'Y'; buf[2] = ' ';
                buf[bufferSize - 1] = ';';
                clearBuffer();
                cwq = Queue.Synchronized(new Queue());
                cwSendThread = new Thread(new ThreadStart(cwSender));
                cwSendThread.Name = "cwSendThread";
                try { cwSendThread.Start(); }
                catch (Exception ex)
                { Tracing.TraceLine("cwSend:" + ex.Message, TraceLevel.Error); }
            }
            public void close()
            {
                Tracing.TraceLine("CWSend close", TraceLevel.Info);
                try
                {
                    if (cwSendThread.IsAlive) cwSendThread.Abort();
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("CWSend close cwSendThread exception:" + ex.Message, TraceLevel.Error);
                }
            }
            // queue a char or string
            public void Send(object o)
            {
                cwq.Enqueue(o);
            }
            // Sending thread routine
            private void cwSender()
            {
                Tracing.TraceLine("CWSender", TraceLevel.Info);
                while (true)
                {
                    try
                    {
                        // await an item
                        while (cwq.Count == 0) Thread.Sleep(50);
                        object o = cwq.Dequeue();
                        if (o.GetType().Name == "Char")
                        {
                            sendChar((char)o);
                        }
                        else
                        {
                            // it's a string
                            // Terminate with a blank so it'll send.
                            bool blankSwitch = true;
                            foreach (char c in (string)o)
                            {
                                blankSwitch = treatAsWhite(c);
                                sendChar(c);
                            }
                            if (!blankSwitch) sendChar(' ');
                        }
                    }
                    catch (ThreadAbortException ab)
                    {
                        Tracing.TraceLine("CWSender aborting:" + ab.Message, TraceLevel.Error);
                    }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine("CWSender:" + ex.Message, TraceLevel.Error);
                        // immediate stop!
                        parent.Callouts.safeSend(BldCmd(kcmdKY + "0;"));
                        clearBuffer();
                        cwq.Clear();
                    }
                }
            }
            private void sendChar(char c)
            {
                // set if white space or treated as such.
                bool blankChar = treatAsWhite(c);
                // If white space, set to a blank.
                if (blankChar) c = ' ';
                bool alreadyAdded = false;
                // send if a blank or the buffer's full.
                if (blankChar || (messageLen == messageMaxLen))
                {
                    // Add the char.
                    if (messageLen < messageMaxLen)
                    {
                        buf[cursor++] = c;
                        bufDelay += cwMSPerChar(c);
                        alreadyAdded = true;
                    }
                    sendBuf(bufDelay);
                    bufDelay = 0;
                }
                // Add the char if not already added.
                if (!alreadyAdded)
                {
                    buf[cursor++] = c;
                    bufDelay += cwMSPerChar(c);
                }
            }
            private bool treatAsWhite(char c)
            {
                return ((char.IsWhiteSpace(c) |
                    (((int)c >= CharTimes.Length) || (CharTimes[(int)c] == 0))));
            }
            public bool CWBusyRcvd;
            public bool CWBusy;
            /// <summary>
            /// Return true if CW is busy.
            /// </summary>
            /// <remarks>
            /// This returns busy if we're using breakin and still in receive mode,
            /// or if "KY;" indicates busy.
            /// </remarks>
            private bool CWIsBusy()
            {
                bool rv = true;
                // See if full breakin and still in transmit.  If so, wait.
                while (parent.FullBreakin && parent.Transmit) { Thread.Sleep(1); }

                // See contKY().
                CWBusyRcvd = false;
                parent.Callouts.safeSend(BldCmd(kcmdKY));
                if (AllRadios.await(() => { return CWBusyRcvd; }, 25, 1))
                {
                    rv = CWBusy;
                }
                return rv;
            }

            private void sendBuf(float del)
            {
                Tracing.TraceLine("sendBuf:" + del, TraceLevel.Info);
                while (CWIsBusy()) { }
                parent.Callouts.safeSend(new string(buf));                
                // Delay for this buffer.
                int ms = (int)(del + .5);
                Thread.Sleep(ms);
                // clear the buffer
                while (cursor > initialCursorPos) buf[--cursor] = ' ';
            }
            private void clearBuffer()
            {
                cursor = initialCursorPos;
                for (int i = cursor; i < bufferSize - 1; i++) buf[i] = ' ';
                bufDelay = 0;
            }
            // Morse code timings.
            const float dotMS20 = 60; // 60 millisecs in a 20 wpm dot.
            const float dashMS20 = 3 * dotMS20;
            const float intraChar20 = dotMS20;
            const float interChar20 = dashMS20;
            const float interWord20 = 7 * dotMS20;
            // special chars:
            // quote .-..-. 0x22
            // # mistake ........ 0x23
            // % ...-. 0x25
            // apostrophe .----. 0x27
            // ( -.--. 0x28
            // ) -.--.- 0x29
            // * -..- 0x2A
            // + (ar) .-.-. 0x2B
            // , (comma) --..-- 0x2C
            // - (minus) -....- 0x2D
            // . (period) .-.-.- 0x2E
            // / -..-. 0x2F
            // : ---... 0x3A
            // < (as) .-... 0x3C
            // = (double dash) -...- 0x3D
            // > (sk) ...-.- 0x3E
            // ? ..--.. 0x3F
            // @ (at sign) .--.-. 0x40
            // [ -...- (double dash) 0x5B
            // \ bk -...-.- 0x5C
            // ] (kn) -.--. 0x5D
            // _ (ar) .-.-. 0x5F
            private static float[] CharTimes =
            {0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0, // 0 - 0F
                0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0, // 10 - 1F
                dotMS20, 0, 4*dotMS20+2*dashMS20+5*intraChar20, // space .-..-. 0x20-22
                8*dotMS20+7*intraChar20, 0, 4*dotMS20+dashMS20+4*intraChar20, // ........ ...-. 0x23-25
                0, 2*dotMS20+4*dashMS20+5*intraChar20, // .----. 0x26-27
                2*dotMS20+3*dashMS20+4*intraChar20, 2*dotMS20+4*dashMS20+5*intraChar20, // -.--. -.--.- 0x28-29
                2*dotMS20+2*dashMS20+3*intraChar20, 3*dotMS20+2*dashMS20+4*intraChar20, // -..- .-.-. 0x2A-2B
                2*dotMS20+4*dashMS20+5*intraChar20, 4*dotMS20+2*dashMS20+5*intraChar20, // --..-- -....- 0x2C-2D
                3*dotMS20+3*dashMS20+5*intraChar20, 3*dotMS20+2*dashMS20+4*intraChar20, // .-.-.- -..-. 0x2E-2F
                5*dashMS20+4*intraChar20, 1*dotMS20+4*dashMS20+4*intraChar20, // 0 1
                2*dotMS20+3*dashMS20+4*intraChar20, 3*dotMS20+2*dashMS20+4*intraChar20, // 2 3
                4*dotMS20+1*dashMS20+4*intraChar20, 5*dotMS20+4*intraChar20, // 4 5
                4*dotMS20+1*dashMS20+4*intraChar20, 3*dotMS20+2*dashMS20+4*intraChar20, // 6 7
                2*dotMS20+3*dashMS20+4*intraChar20, 1*dotMS20+4*dashMS20+4*intraChar20, // 8 9
                3*dotMS20+3*dashMS20+5*intraChar20, 0, 4*dotMS20+1*dashMS20+4*intraChar20, // ---... .-... 0x3A-3B
                3*dotMS20+2*dashMS20+4*intraChar20, 4*dotMS20+2*dashMS20+5*intraChar20, // -...- ...-.- 0x3D-3E
                4*dotMS20+2*dashMS20+5*intraChar20, 3*dotMS20+3*dashMS20+5*intraChar20, // ..--.. .--.-. 0x3F-40
                dotMS20+dashMS20+intraChar20, dashMS20+3*dotMS20+3*intraChar20, // A B
                2*dotMS20+2*dashMS20+3*intraChar20, dashMS20+2*dotMS20+2*intraChar20, // C D
                dotMS20, 3*dotMS20+dashMS20+3*intraChar20, // E F
                2*dashMS20+dotMS20+2*intraChar20, 4*dotMS20+3*intraChar20, // G H
                2*dotMS20+intraChar20, dotMS20+3*dashMS20+3*intraChar20, // I J
                2*dotMS20+dashMS20+2*intraChar20, 3*dotMS20+dashMS20+3*intraChar20, // K L
                2*dashMS20+intraChar20, dashMS20+dotMS20+intraChar20, // M N
                3*dotMS20+2*intraChar20, 2*dotMS20+2*dashMS20+3*intraChar20, // O P
                dotMS20+3*dashMS20+3*intraChar20, 2*dotMS20+dashMS20+2*intraChar20, // Q R
                3*dotMS20+2*intraChar20, dotMS20, // S T
                2*dotMS20+dashMS20+2*intraChar20, 3*dashMS20+dotMS20+3*intraChar20, // U V
                dotMS20+2*dashMS20+2*intraChar20, 2*dotMS20+2*dashMS20+3*intraChar20, // W X
                dotMS20+3*dashMS20+3*intraChar20, 2*dotMS20+2*dashMS20+3*intraChar20, // Y Z
                3*dotMS20+2*dashMS20+4*intraChar20, 4*dotMS20+3*dashMS20+6*intraChar20, // -...- -...-.- 0x5B-5C
                2*dotMS20+3*dashMS20+4*intraChar20, 0, // -.--. 0x5D-5E
                3*dotMS20+2*dashMS20+4*intraChar20, 0, // .-.-. 0x5F-60
                dotMS20+dashMS20+intraChar20, dashMS20+3*dotMS20+3*intraChar20, // a b
                2*dotMS20+2*dashMS20+3*intraChar20, dashMS20+2*dotMS20+2*intraChar20, // c d
                dotMS20, 3*dotMS20+dashMS20+3*intraChar20, // e f
                2*dashMS20+dotMS20+2*intraChar20, 4*dotMS20+3*intraChar20, // g h
                2*dotMS20+intraChar20, dotMS20+3*dashMS20+3*intraChar20, // i j
                2*dotMS20+dashMS20+2*intraChar20, 3*dotMS20+dashMS20+3*intraChar20, // k l
                2*dashMS20+intraChar20, dashMS20+dotMS20+intraChar20, // m n
                3*dotMS20+2*intraChar20, 2*dotMS20+2*dashMS20+3*intraChar20, // o p
                dotMS20+3*dashMS20+3*intraChar20, 2*dotMS20+dashMS20+2*intraChar20, // q r
                3*dotMS20+2*intraChar20, dotMS20, // s t
                2*dotMS20+dashMS20+2*intraChar20, 3*dashMS20+dotMS20+3*intraChar20, // u v
                dotMS20+2*dashMS20+2*intraChar20, 2*dotMS20+2*dashMS20+3*intraChar20, // w x
                dotMS20+3*dashMS20+3*intraChar20, 2*dotMS20+2*dashMS20+3*intraChar20}; // y z
            private float cwMSPerChar(char c)
            {
                float scale = ((float)20 / (float)parent.KeyerSpeed);
                //return (float)100 * scale;
                int id = (int)c;
                float rv = 0;
                if (id < CharTimes.Length)
                {
                    rv = scale * CharTimes[id];
                }
                return rv;
            }
        }
        private cwSend cwXmit;

        private void contKY(string str)
        {
            Tracing.TraceLine("contKY:" + str, TraceLevel.Info);
            cwXmit.CWBusy = (str[2] == '1');
            cwXmit.CWBusyRcvd = true;
        }

        public override bool SendCW(char c)
        {
            Tracing.TraceLine("SendCW:" + c.ToString(), TraceLevel.Info);
            bool rv = (Mode != null);
            if (rv) rv = ((Mode.ToString() == "cw") || (Mode.ToString() == "cwr"));
            if (rv)
            {
                cwXmit.Send(c);
            }
            else Tracing.TraceLine("SendCW:error", TraceLevel.Error);
            return rv;
        }
        public override bool SendCW(string str)
        {
            Tracing.TraceLine("SendCW:" + str, TraceLevel.Info);
            bool rv = (Mode != null);
            if (rv) rv = ((Mode.ToString() == "cw") || (Mode.ToString() == "cwr"));
            if (rv)
            {
                cwXmit.Send(str);
            }
            else Tracing.TraceLine("SendCW:error", TraceLevel.Error);
            return rv;
        }
        public override void StopCW()
        {
            Tracing.TraceLine("StopCW", TraceLevel.Info);
            bool rv = (Mode != null);
            if (rv) rv = ((Mode.ToString() == "cw") || (Mode.ToString() == "cwr"));
            if (rv)
            {
                try
                {
                    cwXmit.cwSendThread.Interrupt();
                }
                catch (Exception ex)
                { Tracing.TraceLine("StopCW:" + ex.Message, TraceLevel.Error); }
            }
            else Tracing.TraceLine("StopCW:error", TraceLevel.Error);
        }
#endregion

        public Kenwood()
        {
            Tracing.TraceLine("Kenwood constructor", TraceLevel.Info);
            ModeTable = myModeTable;
            setupResponseActions();
        }

        /// <summary>
        /// Open the radio
        /// </summary>
        /// <returns>True on success </returns>
        public override bool Open(OpenParms p)
        {
            Tracing.TraceLine("Kenwood Open", TraceLevel.Info);
            // the com port should be open.
            bool rv = base.Open(p);
            if (rv)
            {
                // Start the radio output processor.
                try { rigHandler.Start(); }
                catch (Exception ex)
                { Tracing.TraceLine("Kenwood Open:" + ex.Message, TraceLevel.Error); }

                // Initialize CW sending.
                cwXmit = new cwSend(this);
            }
            IsOpen = rv;
            return rv;
        }

        public override void close()
        {
            Tracing.TraceLine("Kenwood close", TraceLevel.Info);
            try { if ((statThread != null) && statThread.IsAlive) statThread.Abort(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("kenwood close statThread exception:" + ex.Message, TraceLevel.Error);
            }

            // Turn off continuous rig output.
            Callouts.safeSend(BldCmd(kcmdAI + '0'));

            try { if ((memGetThread != null) && memGetThread.IsAlive) memGetThread.Abort(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("kenwood close memGetThread exception:" + ex.Message, TraceLevel.Error);
            }

            try { if (rigHandler != null) rigHandler.Stop(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("kenwood close rigHandler exception:" + ex.Message, TraceLevel.Error);
            }

            try { if (cwXmit != null) cwXmit.close(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("kenwood close cwXmit exception:" + ex.Message, TraceLevel.Error);
            }

            base.close(); // resets IsOpen.
        }

        protected static string[] statCommands;
        private Thread statThread;
        /// <summary>
        /// Get rig status.
        /// This only runs if power is on.
        /// The rig operations are run in a separate thread.
        /// </summary>
        /// <param name="ckPower">true to check power here</param>
        /// <param name="initial">true for initial call at device startup</param>
        protected override void getRigStatus(bool ckPower, bool initial)
        {
            Tracing.TraceLine("getRigStatus:" + ckPower.ToString() + ' ' + initial.ToString(), TraceLevel.Info);
            // Get the radio's status.
            if (ckPower)
            {
                if (!powerOnCheck())
                {
                    // Quit here.  Note that we're called again if power comes back on.
                    Tracing.TraceLine("getRigstat:power not on",TraceLevel.Info);
                    return;
                }
            }
            statThread = new Thread(new ThreadStart(rigStatHelper));
            statThread.Name = "statThread";
            try { statThread.Start(); }
            catch (Exception ex)
            { Tracing.TraceLine("getRigStatus:" + ex.Message, TraceLevel.Error); }
            Thread.Sleep(0);
        }
        private int rsHelperRunning = 0;
        /// <summary>
        /// separate thread to get the rig's status at power-on or program startup.
        /// </summary>
        private void rigStatHelper()
        {
            Tracing.TraceLine("rigStatHelper", TraceLevel.Info);
            try
            {
                // Quit if already running.
                if (Interlocked.CompareExchange(ref rsHelperRunning, 1, 0) == 1)
                {
                    Tracing.TraceLine("rigStatHelper:already running", TraceLevel.Info);
                    return;
                }
                // rsHelperRunning is now 1.

                foreach (string cmd in statCommands)
                {
                    Callouts.safeSend(BldCmd(cmd));
                    // wait a bit
                    Thread.Sleep(50);
                }

                customStatus();

                // Turn on continuous rig output.
                Callouts.safeSend(BldCmd(kcmdAI + "2"));

                GetMemories();
            }
            catch (ThreadAbortException) { Tracing.TraceLine("statThread abort", TraceLevel.Error); }
            catch (Exception ex)
            {
                Tracing.TraceLine("rigStatHelper:" + ex.Message, TraceLevel.Error);
            }
            finally { rsHelperRunning = 0; }
        }
        /// <summary>
        /// Customized status for this rig.
        /// </summary>
        protected virtual void customStatus()
        {
            // Rigs override to provide
        }

        protected const string ZPad = "00000000000";
        protected string UFreqToString(ulong u)
        {
            string str = u.ToString();
            if (str.Length >= 11) return str;
            return ZPad.Substring(0, 11 - str.Length) + str;
        }

        protected char VFOToLetter(RigCaps.VFOs v)
        {
            switch (v)
            {
                case RigCaps.VFOs.VFOA: return 'A';
                case RigCaps.VFOs.VFOB: return 'B';
                default: return ' ';
            }
        }
        protected RigCaps.VFOs letterToVFO(char c)
        {
            RigCaps.VFOs rv;
            switch (c)
            {
                case 'A': rv = RigCaps.VFOs.VFOA; break;
                case 'B': rv = RigCaps.VFOs.VFOB; break;
                default: rv = RigCaps.VFOs.None; break;
            }
            return rv;
        }

        // Also see KenwoodTS2000
        protected virtual void contFR(string cmd)
        {
            // Receive VFO.
            int i = System.Int32.Parse(cmd.Substring(2, 1));
            // set vfo, and frequency if not from memory.
            if (i < 2)
            {
                _RXVFO = (RigCaps.VFOs)i;
                _RXFrequency = myVFOFreq[i];
            }
            else
            {
                _RXVFO = RigCaps.VFOs.None;
                // This memory might not be in our collection yet!
                // Use a thread to collect it, and get off the interrupt level.
                memGetThread = new Thread(new ParameterizedThreadStart(getThisMemory));
                memGetThread.Name = "memGetThread";
                memGetThread.Start(_CurrentMemoryChannel);
            }
        }
        protected virtual void contFT(string cmd)
        {
            int i = System.Int32.Parse(cmd.Substring(2));
            // Get vfo, and frequency if not from memory.
            if (i < 2)
            {
                _TXVFO = (RigCaps.VFOs)i;
                _TXFrequency = myVFOFreq[i];
            }
            else if (i == 2)
            {
                _TXVFO = RigCaps.VFOs.None;
                // We'll get an "FR" from the rig that'll get the memory.
            }
            else throw new IndexOutOfRangeException();
        }

        protected virtual void contTS(string str)
        {
            // We won't set this if the split flag isn't on.
            // This can happen if we're using the XIT.
            TFSetOn = (Split && (str[2] == '1')) ? true : false;
        }

        public override bool MemoryToVFO(int n, RigCaps.VFOs vfo)
        {
            Tracing.TraceLine("MemoryToVFO:" + n.ToString() + " " + vfo.ToString(), TraceLevel.Info);
            if (Transmit | !MemoryMode)
            {
                Tracing.TraceLine("MemoryToVFO:invalid", TraceLevel.Error);
                return false;
            }
            // See if we need to go to another VFO.
            if (!IsMemoryMode(vfo) & (vfo != oldVFO))
            {
                RXVFO = vfo;
                if (await(() => { return (RXVFO == vfo); }, 1000)) oldVFO = vfo;
            }
            Callouts.safeSend(BldCmd(kcmdSV));
            Callouts.safeSend(BldCmd(kcmdIF));
            return true;
        }

        protected virtual void contFreqA(string str)
        {
            freqVFO(RigCaps.VFOs.VFOA, str.Substring(2));
        }
        protected virtual void contFreqB(string str)
        {
            freqVFO(RigCaps.VFOs.VFOB, str.Substring(2));
        }
        protected virtual void contFreqC(string str) { }
        protected virtual void contXI(string str)
        {
            int ofst = 2;
            _TXFrequency = System.UInt64.Parse(str.Substring(ofst, 11));
            ofst += 11;
            _TXMode = getMode(str[ofst++]);
            _TXDataMode = getDataMode(str[ofst++]);
        }
        protected ulong[] myVFOFreq = new ulong[2];
        protected void freqVFO(RigCaps.VFOs v, string f)
        {
            Tracing.TraceLine("freqVFO:v=" + v.ToString() + " freq=" + f +
                " txvfo=" + TXVFO.ToString() + " rxvfo=" + RXVFO.ToString(),
                TraceLevel.Info);
            ulong u;
            if (System.UInt64.TryParse(f, out u))
            {
                myVFOFreq[(int)v] = u;
                if (v == TXVFO) _TXFrequency = u;
                if (v == RXVFO) _RXFrequency = u;
                // For panning
                raiseRigOutput(RigCaps.Caps.FrGet, u);
            }
            else Tracing.TraceLine("freqVFO:error" + f, TraceLevel.Error);
        }
        protected virtual void contSM(string str)
        {
            _SMeter = System.Int32.Parse(str.Substring(2));
            // For panning.
            raiseRigOutput(RigCaps.Caps.SMGet, (ulong)_SMeter);
        }

        // See KenwoodTS2000.
        protected virtual void contST(string str) { }

        protected void contPS(string cmd)
        {
        }

        protected virtual void contRXTX(string str)
        {
            TransmitStatus = (str[0] == 'T');
        }

        Thread memGetThread;
        protected virtual void contMC(string str)
        {
            _CurrentMemoryChannel = getMemoryChannel(str.Substring(2, 3));
            if (IsMemoryMode(RXVFO))
            {
                // We're in memory mode.
                // However, the memory might not be in our collection yet!
                // Use a thread to collect it, and get off the interrupt level.
                memGetThread = new Thread(new ParameterizedThreadStart(getThisMemory));
                memGetThread.Start(_CurrentMemoryChannel);
            }
        }
        /// <summary>
        /// Get a memory number from this string.
        /// </summary>
        /// <param name="str">memory string</param>
        /// <returns>memory number</returns>
        protected virtual int getMemoryChannel(string str)
        {
            Tracing.TraceLine("getMemoryChannel:" + str, TraceLevel.Info);
            // The memory channel might be " nn".
            int i = 0;
            try
            {
                char c = (str[0] == ' ') ? '0' : str[0];
                string s = c.ToString() + str.Substring(1);
                i = System.Int32.Parse(s);
            }
            catch (Exception ex)
            { Tracing.TraceLine("getMemoryChannel:" + ex.Message, TraceLevel.Error); }
            return i;
        }
        /// <summary>
        /// Request the current memory from the rig.
        /// </summary>
        private void getThisMemory(object o)
        {
            Tracing.TraceLine("getThisMemory:" + o.ToString(), TraceLevel.Info);
            try
            {
                MemoryData m = Memories.mems[(int)o];
                if (getMem(m)) reflectMemoryData(m);
            }
            catch (ThreadAbortException) { Tracing.TraceLine("memGetThread abort", TraceLevel.Error); }
        }
        protected virtual void contRIT(string str)
        {
            RIT.Active = (str.Substring(2, 1) == "1") ? true : false;
        }
        protected virtual void contXIT(string str)
        {
            XIT.Active = (str.Substring(2, 1) == "1") ? true : false;
        }

        protected virtual void contMD(string str)
        {
            ModeValue m = getMode(str[2]);
            if (!Split) _RXMode = _TXMode = m;
            else
            {
                if (Transmit) _TXMode = m;
                else _RXMode = m;
            }
        }

        /// <summary>
        /// mode indecies which must match ModeTable
        /// </summary>
        internal enum modes
        { none, lsb, usb, cw, cwr, fm, am, fsk, fskr, none2}
        /// <summary>
        /// my mode table
        /// </summary>
        internal static ModeValue[] myModeTable =
            {
                new ModeValue(0, '0',"none"),
                new ModeValue(1, '1', "lsb"),
                new ModeValue(2, '2', "usb"),
                new ModeValue(3, '3', "cw"),
                new ModeValue(4, '7', "cwr"),
                new ModeValue(5, '4', "fm"),
                new ModeValue(6, '5', "am"),
                new ModeValue(7, '6', "fsk"),
                new ModeValue(8, '9', "fskr"),
                new ModeValue(9, '8', "none"),
            };
        /// <summary>
        /// Kenwood mode character to internal mode.
        /// </summary>
        /// <param name="c">ASCII character</param>
        /// <returns>modes value</returns>
        protected virtual ModeValue getMode(char c)
        {
            Tracing.TraceLine("getMode:" + c.ToString(), TraceLevel.Info);
            ModeValue rv = ModeTable[0]; // Use none if invalid
            ModeValue cm = new ModeValue(c);
            try
            {
                foreach (ModeValue m in myModeTable)
                {
                    if (m == cm)
                    {
                        rv = m; // must return m, not cm.
                        break;
                    }
                }
            }
            catch (Exception ex)
            { Tracing.TraceLine("getMode:" + ex.Message, TraceLevel.Error); }
            return rv;
        }
        /// <summary>
        /// Get mode character to send to the rig.
        /// </summary>
        /// <param name="m">modeValue item</param>
        /// <returns>mode character</returns>
        protected virtual char KMode(ModeValue m)
        {
            Tracing.TraceLine("KMode:" + m.ToString(), TraceLevel.Info);
            char rv = '0';
            foreach (ModeValue mv in myModeTable)
            {
                if (m == mv)
                {
                    rv = mv.value;
                    break;
                }
            }
            return rv;
        }

        protected virtual void contDA(string str)
        {
            DataModes dm = getDataMode(str[2]);
            if (Transmit) _TXDataMode = dm;
            else _RXDataMode = dm;
        }
        protected virtual DataModes getDataMode(char c)
        {
            Tracing.TraceLine("getDataMode:" + c.ToString(), TraceLevel.Info);
            return (c == '1') ? DataModes.on : DataModes.off;
        }
        protected char kDataMode(DataModes d)
        {
            Tracing.TraceLine("kDataMode:" + d.ToString(), TraceLevel.Info);
            return (char)((int)d + '0');
        }

        protected virtual ToneCTCSSValue getToneCTCSS(char c)
        {
            Tracing.TraceLine("getToneCTCSS:" + c.ToString(), TraceLevel.Info);
            ToneCTCSSValue t = new ToneCTCSSValue(c);
            foreach (ToneCTCSSValue tt in FMToneModes)
            {
                if (t == tt) { t = tt; break; }
            }
            return t;
        }
        protected virtual char kToneCTCSS(ToneCTCSSValue t)
        {
            Tracing.TraceLine("kToneCTCSS:" + t.ToString(), TraceLevel.Info);
            return t.value;
        }

        protected Thread MRThread;
        protected virtual void contMR(string str)
        {
            Tracing.TraceLine("contMR:" + str, TraceLevel.Info);
            // We have to perform this under a thread because of the locking.
            MRThread = new Thread(new ParameterizedThreadStart(MRThreadProc));
            MRThread.Name = "MRThread";
            MRThread.Priority = ThreadPriority.Highest;
            MRThread.Start(str);
        }
        /// <summary>
        /// Handle MR from the rig.
        /// Handled by the rigs.
        /// </summary>
        /// <param name="o">the MR command including MR</param>
        protected virtual void MRThreadProc(object o) { }

        protected virtual void contVox(string str)
        {
            _Vox = ((str[2] == '1') ? OffOnValues.on : OffOnValues.off);
        }
        // handled by the rigs
        protected virtual void contCT(string str) { }
        protected virtual void contDQ(string str) { }
        protected virtual void contIF(string str) { }
        protected virtual void contTO(string str) { }
        protected virtual void contAN(string str) { }

        protected virtual void contCN(string str)
        {
            _CTSSFrequency = ToneCTCSSToFreq(str.Substring(2, 2));
        }

        protected virtual void contTN(string str)
        {
            _ToneFrequency = ToneCTCSSToFreq(str.Substring(2, 2));
        }
        /// <summary>
        /// Get corresponding Tone/CTSS frequency
        /// </summary>
        /// <param name="str">2-character string</param>
        /// <returns>frequency as a float</returns>
        protected virtual float ToneCTCSSToFreq(string str)
        {
            Tracing.TraceLine("ToneCTCSSFreq:" + str, TraceLevel.Info);
            try
            {
                int t = System.Int32.Parse(str);
                return ToneFrequencyTable[t];
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("ToneCTCSSToFreq:" + ex.Message, TraceLevel.Error);
                return 0;
            }
        }
        /// <summary>
        /// Convert the frequency to a 2-digit string for a command.
        /// </summary>
        /// <param name="f">tone frequency as a float</param>
        /// <returns>2-character string</returns>
        protected virtual string kToneCTCSSFreq(float f)
        {
            Tracing.TraceLine("kToneCTCSSFreq:" + f.ToString(), TraceLevel.Info);
            int i;
            for (i = 0; (i < ToneFrequencyTable.Length) && (f != ToneFrequencyTable[i]); i++) { }
            if (i == ToneFrequencyTable.Length)
            {
                Tracing.TraceLine("ToneCTCss error:" + f.ToString(), TraceLevel.Error);
                i = 0;
            }
            return i.ToString("d2");
        }

        /// <summary>
        /// Get the DCS code for the kenwood string
        /// </summary>
        /// <param name="str">string from radio</param>
        /// <returns>DCS code</returns>
        protected ushort stringToDCS(string str)
        {
            Tracing.TraceLine("stringToDCS:" + str, TraceLevel.Info);
            ushort rv = 0;
            try
            {
                ushort id = System.UInt16.Parse(str.Substring(2));
                rv = DCSTable[id];
            }
            catch (Exception ex)
            { Tracing.TraceLine("stringToDCS:" + ex.Message, TraceLevel.Error); }
            return rv;
        }
        /// <summary>
        /// Get the command string for this DCS value
        /// </summary>
        /// <param name="code">DCS value</param>
        /// <returns>3-digit string</returns>
        protected string kDCS(ushort code)
        {
            Tracing.TraceLine("kDCS:" + code.ToString(), TraceLevel.Info);
            int id;
            for (id = 0; (id < DCSTable.Length) && (code != DCSTable[id]); id++) { }
            if (id == DCSTable.Length)
            {
                Tracing.TraceLine("DCS value error:" + code.ToString(), TraceLevel.Error);
                id = 0;
            }
            return id.ToString("d3");
        }

        protected virtual void contKS(string str)
        {
            _KeyerSpeed = System.Int32.Parse(str.Substring(2));
        }

        protected virtual void contEX(string str)
        {
            // implemented by specific models for now.
        }

        protected virtual void contAC(string str)
        {
            // if TX tune is on, they both are.
            if (str[3] == '1') _AntennaTuner = (AntTunerVals.rx | AntTunerVals.tx);
            else _AntennaTuner = (AntTunerVals)0;
            Tracing.TraceLine("contAC:" + _AntennaTuner.ToString(), TraceLevel.Verbose);
        }

        protected virtual void contAG(string str)
        {
            _LineoutGain = System.Int32.Parse(str.Substring(3));
        }

        protected virtual void contRG(string str)
        {
            _AudioGain = System.Int32.Parse(str.Substring(2));
        }

        protected virtual void contRA(string str)
        {
            // format is RAp1p1p2p2 where p2 is 00.
            _RFAttenuator = (str[3] == '1') ? OffOnValues.on : OffOnValues.off;
        }

        protected virtual void contPA(string str) { }

        protected virtual void contSD(string str)
        {
            _BreakinDelay = System.Int32.Parse(str.Substring(2));
        }

        protected virtual void contVD(string str)
        {
            _VoxDelay = System.Int32.Parse(str.Substring(2));
        }

        protected virtual void contVG(string str)
        {
            _VoxGain = System.Int32.Parse(str.Substring(2));
        }

        protected virtual void contMG(string str)
        {
            mGain = System.Int32.Parse(str.Substring(2));
        }

        protected virtual void contCG(string str)
        {
            cLevel = System.Int32.Parse(str.Substring(2));
        }

        protected virtual void contPL(string str)
        {
            _ProcessorInputLevel = System.Int32.Parse(str.Substring(2, 3));
            _ProcessorOutputLevel = System.Int32.Parse(str.Substring(5, 3));
        }

        protected virtual void contPR(string str)
        {
            _ProcessorState = (str[2] == '1') ? OffOnValues.on : OffOnValues.off;
        }

        protected virtual void contML(string str)
        {
            _TXMonitor = System.Int32.Parse(str.Substring(2));
        }

        protected virtual void contFL(string str)
        {
            filtNum = str[2] - '0';
        }

        protected virtual void contFW(string str)
        {
            filtwth = System.Int32.Parse(str.Substring(2));
        }

        protected virtual void contIS(string str)
        {
            // p1 is always a space.
            filtofst = System.Int32.Parse(str.Substring(3));
        }

        protected virtual void contSH(string str)
        {
            _filterHigh = System.Int32.Parse(str.Substring(2));
        }

        protected virtual void contSL(string str)
        {
            _filterLow = System.Int32.Parse(str.Substring(2));
        }

        internal const char RMSWR = '1';
        internal const char RMComp = '2';
        internal const char RMALC = '3';
        protected virtual void contRM(string str)
        {
            int val;
            val = System.Int32.Parse(str.Substring(3));
            switch (str[2])
            {
                case RMSWR: SWRRaw = val; break;
                case RMComp: compRaw = val; break;
                case RMALC: ALCRaw = val; break;
            }
        }

        protected virtual void contMF(string str)
        {
            _MenuBank = ((str[2] == '1') ? 1 : 0);
        }

        protected virtual void contPC(string str)
        {
            _XmitPower = System.Int32.Parse(str.Substring(2));
        }

        // handled by the rigs.
        protected virtual void contAL(string str) { }
        protected virtual void contAM(string str) { }
        protected virtual void contBC(string str) { }
        protected virtual void contBP(string str) { }
        protected virtual void contDC(string str) { }
        protected virtual void contGC(string str) { }
        protected virtual void contNB(string str) { }
        protected virtual void contNL(string str) { }
        protected virtual void contNR(string str) { }
        protected virtual void contNT(string str) { }
        protected virtual void contRL(string str) { }
        protected virtual void contSB(string str) { }

        protected virtual void contGT(string str)
        {
            if (str[2] != ' ')
            {
                _AGC = System.Int32.Parse(str.Substring(2));
            }
        }

        protected virtual void contOF(string str)
        {
            _OffsetFrequency = System.Int32.Parse(str.Substring(2));
        }

        protected virtual void contOS(string str)
        {
            _OffsetDirection = (OffsetDirections)(str[2] - '0');
        }

        protected virtual void contQC(string str)
        {
            _DCS = stringToDCS(str.Substring(2));
        }

        protected void contFV(string str)
        {
            _FWVersion = str.Substring(2);
        }

        protected virtual void contID(string str)
        {
            // Currently user must select the correct radio.
            //RadioID = (RadioIDs)System.Int32.Parse(str.Substring(2));
        }

        protected void contCD(string cmd)
        {
            // CW decode breakout.
            switch (cmd[2])
            {
                case '0': contCDOffOn(cmd); break;
                case '1': contCDThreshold(cmd); break;
                case '2': contCDData(cmd); break;
            }
        }

        protected virtual void contCDOffOn(string cmd) { }
        protected virtual void contCA(string cmd) { }
        protected virtual void contCDThreshold(string cmd) { }
        protected virtual void contCDData(string cmd) { }
        protected virtual void contEQ(string cmd) { }
        protected virtual void contUR(string cmd) { }
        protected virtual void contUT(string cmd) { }
    }
}
