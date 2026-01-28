#define GetMemoriez
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Timers;
using HamBands;
using JJTrace;

namespace Radios
{
    public class Icom : AllRadios
    {
        // Region - Icom commands
        #region commands
        internal class IcomCommand
        {
            public byte[] Command;
            public IcomCommand(params byte[] b)
            {
                Command = b;
            }
            public IcomCommand(IcomCommand cmd, byte[] b)
            {
                Command = new byte[cmd.Command.Length + b.Length];
                int i;
                for (i = 0; i < cmd.Command.Length; i++) Command[i] = cmd.Command[i];
                for (int j = 0; j < b.Length; j++) Command[i + j] = b[j];
            }

            public override string ToString()
            {
                StringBuilder sb = new StringBuilder(Command.Length);
                foreach (byte b in Command)
                {
                    sb.Append((char)b);
                }
                return sb.ToString();
            }
        }

        // Commands
        internal static IcomCommand ICSendFreqXCV = new IcomCommand(0x00); // Send operating frequency for transceive  
        internal static IcomCommand ICSendOpModeXCV = new IcomCommand(0x01); // Send operating mode for transceive  
        internal static IcomCommand ICReadBandEdgeFreqs = new IcomCommand(0x02); // Read band edge frequencies  
        internal static IcomCommand ICReadOpFreq = new IcomCommand(0x03); // Read operating frequency  
        internal static IcomCommand ICReadOpMode = new IcomCommand(0x04); // Read operating mode  
        internal static IcomCommand ICSendOpFreq = new IcomCommand(0x05); // Send operating frequency  
        internal static IcomCommand ICSendOpMode = new IcomCommand(0x06); // Send operating mode  
        internal static IcomCommand ICVFO = new IcomCommand(0x07); // VFOs (no return data)
        internal static IcomCommand ICSelectVFOA = new IcomCommand(0x07, 0x00); // select vfo A
        internal static IcomCommand ICSelectVFOB = new IcomCommand(0x07, 0x01); // select vfo B
        internal static IcomCommand ICVFOEqual = new IcomCommand(0x07, 0xA0); // equalize A dand B
        internal static IcomCommand ICExchangeMainSub = new IcomCommand(0x07, 0xB0); // exchange main and sub
        internal static IcomCommand ICSelectMainBand = new IcomCommand(0x07, 0xD0); // select main band
        internal static IcomCommand ICSelectSubBand = new IcomCommand(0x07, 0xD1); // select sub-band 
        internal IcomCommand ICSelectMemMode = new IcomCommand(0x08); // select memory mode
        internal static IcomCommand ICMemWrite = new IcomCommand(0x09); // Memory write  
        internal static IcomCommand ICMemCopy2VFO = new IcomCommand(0x0A); // Memory copy to VFO  
        internal static IcomCommand ICMemClear = new IcomCommand(0x0B); // Memory clear  
        internal static IcomCommand ICReadOffsetFreq = new IcomCommand(0x0C); // Read offset frequency  
        internal static IcomCommand ICSendOffsetFreq = new IcomCommand(0x0D); // Send offset frequency  
        internal static IcomCommand ICReadSplit = new IcomCommand(0x0F); // read split
        internal static IcomCommand ICSplitOff = new IcomCommand(0x0F, 0x00); // split off
        internal static IcomCommand ICSplitOn = new IcomCommand(0x0F, 0x01); // split on
        internal static IcomCommand ICSymplex = new IcomCommand(0x0F, 0x10); // symplex
        internal static IcomCommand ICDup = new IcomCommand(0x0F, 0x11); // dup operation
        internal static IcomCommand ICDupPlus = new IcomCommand(0x0F, 0x12); // set dup+ operation
        internal static IcomCommand ICTuningStep = new IcomCommand(0x10); // Send/read the tuning step
        // 00=10 Hz (1 Hz), 01=100hz 02=1khz, 03=5khz, 04=6.25khz, 05=9khz,
        // 06=10khz, 07=12.5khz, 08=20khz, 09=25khz, 0x10=50khz,
        // 0x11=100khz, 12=1 MHz (except for HF/50 MHz band)  
        internal static IcomCommand ICReadAttenuator = new IcomCommand(0x11); // read attenuator
        internal static IcomCommand ICAttenuatorOff = new IcomCommand(0x11, 0x00); // Send/read attenuator OFF 
        internal static IcomCommand ICAttenuator = new IcomCommand(0x11, 0x01); // Send/read 20 dB attenuator  
        internal static IcomCommand ICAntSelection = new IcomCommand(0x12); // Send/read ANT selection, 0x00 or 01.
        internal static IcomCommand ICAnnounce = new IcomCommand(0x13); // Announce 
        // 0x00=operating frequency, operating mode and S-meter level
        // 01=operating frequency and S-meter level
        // 02=operating mode
        internal static IcomCommand ICAFGain = new IcomCommand(0x14, 0x01); // 0000 to 0255 Send/read [AF] position (0000=max.CCW,0255=max.CW)  
        internal static IcomCommand ICRFGain = new IcomCommand(0x14, 0x02); // 0000 to 0255 Send/read [RF/SQL] position (RF gain level) (0000=max.CCW,0255=11o’clock)  
        internal static IcomCommand ICSquelch = new IcomCommand(0x14, 0x03); // 0000 to 0255 Send/read [RF/SQL] position (squelch level) (0000=11o’clock,0255=max.CW)  
        internal static IcomCommand ICNRLevel = new IcomCommand(0x14, 0x06); // 0000 to 0255 Send/read [NR] position (0000=max.CCW,0255=max.CW)  
        internal static IcomCommand ICInnerPBT = new IcomCommand(0x14, 0x07); // 0000 to 0255 Send/read inner [TWIN PBT] position (0000=max.CCW,0128=center,0255=max.CW)  
        internal static IcomCommand ICOuterPBT = new IcomCommand(0x14, 0x08); // 0000 to 0255 Send/read outer [TWIN PBT] position (0000=max.CCW,0128=center,0255=max.CW)  
        internal static IcomCommand ICCWPitch = new IcomCommand(0x14, 0x09); // 0000 to 0255 Send/read [CW PITCH] position (0000=max.CCW,0128=center,0255=max.CW)  
        internal static IcomCommand ICRFPower = new IcomCommand(0x14, 0x0A); // 0000 to 0255 Send/read [RF POWER] position (0000=max.CCWto0255=max.CW)  
        internal static IcomCommand ICMicGain = new IcomCommand(0x14, 0x0B); // 0000 to 0255 Send/read [MIC GAIN] position (0000=max.CCWto0255=max.CW)  
        internal static IcomCommand ICKeyerSpeed = new IcomCommand(0x14, 0x0C); // 0000 to 0255 Send/read[KEYSPEED]position (0000=max.CCWto0255=max.CW)  
        internal static IcomCommand ICNotchPosition = new IcomCommand(0x14, 0x0D); // 0000 to 0255 Send/read [NOTCH] position (0000=max.CCW,0128=center,0255=max.CW)  
        internal static IcomCommand ICCompLevel = new IcomCommand(0x14, 0x0E); // 0000 to 0255 Send/read COMP level (0000=0to0255=10)  
        internal static IcomCommand ICBrkinDelay = new IcomCommand(0x14, 0x0F); // 0000 to 0255 Send/read Break-IN Delay setting (0000=2.0dto0255=13.0d)  
        internal static IcomCommand ICNBLevel = new IcomCommand(0x14, 0x12); // 0000 to 0255 Send/read NB level (0000=0%to0255=100%)  
        internal static IcomCommand ICMonLevel = new IcomCommand(0x14, 0x15); // 0000 to 0255 Send/read Monitor gain (0000=0%to0255=100%)  
        internal static IcomCommand ICVoxGain = new IcomCommand(0x14, 0x16); // 0000 to 0255 Send/read VOX gain (0000=0%to0255=100%)  
        internal static IcomCommand ICAntiVoxGain = new IcomCommand(0x14, 0x17); // 0000 to 0255 Send/read Anti VOX gain (0000=0%to0255=100%)  
        internal static IcomCommand ICContrast = new IcomCommand(0x14, 0x18); // 0000 to 0255 Send/read CONTRAST level (0000=0%to0255=100%)  
        internal static IcomCommand ICBrightLevel = new IcomCommand(0x14, 0x19); // 0000 to 0255 Send/read BRIGHT level (0000=0%to0255=100%) 
        internal static IcomCommand ICSquelchStatus = new IcomCommand(0x15, 0x01); // 00,01 Read squelch status (squelch close) Read squelch status (squelch open)  
        internal static IcomCommand ICSMeter = new IcomCommand(0x15, 0x02); // 0000 to 0255 Read S-meter level (0000=S0,0120=S9,0240=S9+60dB)  
        internal static IcomCommand ICRFMeter = new IcomCommand(0x15, 0x11); // 0000 to 0255 Read RF power meter (0000=0%,0141=50%,0215=100%)  
        internal static IcomCommand ICSWRMeter = new IcomCommand(0x15, 0x12); // 0000 to 0255 Read SWR meter (0000=SWR1.0, 0041=SWR1.5, 0081=SWR2.0, 0120=SWR3.0)  
        internal static IcomCommand ICALCMeter = new IcomCommand(0x15, 0x13); // 0000 to 0255 Read ALC meter (0000=Min.t0120=Max.)  
        internal static IcomCommand ICCompMeter = new IcomCommand(0x15, 0x14); // 0000 to 0255 Read COMP meter (0000=0dB, 0120=15dB, 0240=30dB)  
        internal static IcomCommand ICPreAmp = new IcomCommand(0x16, 0x02); // 00, 01, 02 Send/read Preamp OFF Send/read Preamp ON (144/430/1200 MHz) Send/read Preamp 1 ON (HF/50MHz) Send/read Preamp 2 ON (HF/50MHz)  
        internal static IcomCommand ICAGC = new IcomCommand(0x16, 0x12); // 01, 02, 03 Send/read AGC FAST Send/read AGC MID Send/read AGC SLOW  
        internal static IcomCommand ICNB = new IcomCommand(0x16, 0x22); // 00, 01 Send/read Noise Blanker OFF Send/read Noise Blanker ON  
        internal static IcomCommand ICNR = new IcomCommand(0x16, 0x40); // 00, 01 Send/read Noise Reduction OFF Send/read Noise Reduction ON  
        internal static IcomCommand ICAutoNotch = new IcomCommand(0x16, 0x41); // 00, 01 Send/read Auto Notch function OFF Send/read Auto Notch function ON  
        internal static IcomCommand ICTone = new IcomCommand(0x16, 0x42); // 00, 01 Send/read Repeater tone OFF Send/read Repeater tone ON  
        internal static IcomCommand ICToneSquelch = new IcomCommand(0x16, 0x43); // 00, 01 Send/read Tone squelch OFF Send/read Tone squelch ON  
        internal static IcomCommand ICComp = new IcomCommand(0x16, 0x44); // 00, 01 Send/read Speech compressor OFF Send/read Speech compressor ON  
        internal static IcomCommand ICMon = new IcomCommand(0x16, 0x45); // 00, 01 Send/read Monitor function OFF Send/read Monitor function ON  
        internal static IcomCommand ICVox = new IcomCommand(0x16, 0x46); // 00, 01 Send/read VOX OFF Send/read VOX On
        internal static IcomCommand ICBrkin = new IcomCommand(0x16, 0x47); // 00, 01, 02 Send/read BK-IN function OFF Send/read Semi BK-IN function ON Send/read Full BK-IN function ON  
        internal static IcomCommand ICManualNotch = new IcomCommand(0x16, 0x48); // 00, 01 Send/read Manual notch function OFF Send/read Manual notch function ON  
        internal static IcomCommand ICAFC = new IcomCommand(0x16, 0x4A); // 00, 01 Send/read AFC function OFF Send/read AFC function ON  
        internal static IcomCommand ICDTCS = new IcomCommand(0x16, 0x4B); // 00, 01 Send/read DTCS OFF Send/read DTCS ON  
        internal static IcomCommand ICVSC = new IcomCommand(0x16, 0x4C); // 00, 01 Send/read VSC function OFF Send/read VSC function ON  
        internal static IcomCommand ICTwinPeak = new IcomCommand(0x16, 0x4F); // 00, 01 Send/read Twin Peak Filter OFF Send/read Twin Peak Filter ON  
        internal static IcomCommand ICDialLock = new IcomCommand(0x16, 0x50); // 00, 01 Send/read Dial lock function OFF Send/read Dial lock function ON  
        internal static IcomCommand ICFirstIFFilter = new IcomCommand(0x16, 0x55); // 00, 01, 02 Send/read 1st IF filter 3 kHz Send/read 1st IF filter 6 kHz Send/read 1st IF filter 15 kHz  
        internal static IcomCommand ICDSPFilterType = new IcomCommand(0x16, 0x56); // 00, 01 Send/read DSP filter type SHARP Send/read DSP filter type SOFT  
        internal static IcomCommand ICNotchWidth = new IcomCommand(0x16, 0x57); // 00, 01, 02 Send/read manual notch width WIDE Send/read manual notch width MID Send/read manual notch width NAR  
        internal static IcomCommand ICSSBTransmitBandwidth = new IcomCommand(0x16, 0x58); // 00, 01, 02 Send/read SSB transmit bandwidth WIDE Send/read SSB transmit bandwidth MID Send/read SSB transmit bandwidth NAR 
        internal static IcomCommand ICSubBand = new IcomCommand(0x16, 0x59); // 00, 01 Send/read Sub band OFF Send/read Sub band ON  
        internal static IcomCommand ICSatellite = new IcomCommand(0x16, 0x5A); // 00, 01 Send/read Satellite mode OFF Send/read Satellite mode ON  
        internal static IcomCommand ICDSQL = new IcomCommand(0x16, 0x5B); // 00, 01, 02 Send/read DSQL/CSQL OFF (DV mode only) Send/read DSQL ON (DV mode only) Send/read CSQL ON (DV mode only)  
        internal static IcomCommand ICSendCWMsgs = new IcomCommand(0x17); // Send CW messages  
        internal static IcomCommand ICReadXCVRID = new IcomCommand(0x19, 0x00); // Read the transceiver ID  
        internal static IcomCommand ICMemContents = new IcomCommand(0x1A, 0x00); // (see p. 195) Send/read memory contents
        internal static IcomCommand ICBandStackingContents = new IcomCommand(0x1A, 0x01); // (see p. 191) Send/read band stacking register contents  
        internal static IcomCommand ICMemoryKeyerContents = new IcomCommand(0x1A, 0x02); // (see p. 191) Send/read memory keyer contents*1  
        internal static IcomCommand ICFilterWidth = new IcomCommand(0x1A, 0x03); // 00 to 49 Send/read the selected filter width (AM:00=200Hz to 49=10kHz; other than AM modes:00=50Hz to 40/31=3600Hz/2700Hz)
        internal static IcomCommand ICAGCtc = new IcomCommand(0x1A, 0x04); // 00 to 13 Send/read the selected AGC time constant (00=OFF,AM:01=0.3sec.to13=8.0sec.SSB/CW/RTTY:01=0.1sec.to13=6.0sec.)  
        internal static IcomCommand IC1A050001 = new IcomCommand(0x1A, 0x05, 0x00, 0x01); // 0000 to 0255 Send/read LCD contrast level (0000=0%(low)to0255=100%(high))  
        internal static IcomCommand IC1A050002 = new IcomCommand(0x1A, 0x05, 0x00, 0x02); // 0000 to 0255 Send/read LCD backlight brightness level (0000=0%(dark)to0255=100%(bright))  
        internal static IcomCommand IC1A050003 = new IcomCommand(0x1A, 0x05, 0x00, 0x03); // 0000 to 0255 Send/read beep level (0000=0%to0255=100%)  
        internal static IcomCommand IC1A050004 = new IcomCommand(0x1A, 0x05, 0x00, 0x04); // 00, 01 Send/read beep level limit OFF Send/read beep level limit ON  
        internal static IcomCommand IC1A050005 = new IcomCommand(0x1A, 0x05, 0x00, 0x05); // 00, 01 Send/read confirmation beep OFF Send/read confirmation beep ON  
        internal static IcomCommand IC1A050006 = new IcomCommand(0x1A, 0x05, 0x00, 0x06); // 00, 01, 02, 03 Send/read band edge beep OFF Send/read band edge beep ON (Beep sounds with a default band) Send/read band edge beep with user setting ON Send/read band edge beep with user set-ting/TX limit ON  
        internal static IcomCommand IC1A050007 = new IcomCommand(0x1A, 0x05, 0x00, 0x07); // 0050 to 0200 Send/readbeepaudiofrequencyforMAIN Band (0050=500Hzto0200=2000Hz)  
        internal static IcomCommand IC1A050008 = new IcomCommand(0x1A, 0x05, 0x00, 0x08); // 0050 to 0200 Send/read beep audio frequency for SUB Band (0050=500Hzto0200=2000Hz)  
        internal static IcomCommand IC1A050009 = new IcomCommand(0x1A, 0x05, 0x00, 0x09); // 00, 01, 02 Send/read Auto selection for [RF/SQL] Send/read SQL selection for [RF/SQL] Send/read RF+SQL selection for [RF/SQL]  
        internal static IcomCommand ICPeakHold = new IcomCommand(0x1A, 0x05, 0x00, 0x10); // 00, 01 Send/read Meter Peak Hold function OFF Send/read Meter Peak Hold function ON  
        internal static IcomCommand ICPeakHoldOff = new IcomCommand(0x1A, 0x05, 0x00, 0x10, 0x00); // off
        internal static IcomCommand ICPeakHoldOn = new IcomCommand(0x1A, 0x05, 0x00, 0x10, 0x01); // on
        internal static IcomCommand IC1A050011 = new IcomCommand(0x1A, 0x05, 0x00, 0x11); // 00, 01 Send/read FM/DV Center Error function OFF Send/read FM/DV Center Error function ON  
        internal static IcomCommand IC1A050012 = new IcomCommand(0x1A, 0x05, 0x00, 0x12); // 00, 01, 02, 03, 04, 05 Send/read Time-Out Timer OFF Send/read 3 min.Time-Out Timer Send/read 5 min.Time-Out Timer Send/read 10 min.Time-Out Timer Send/read 20 min.Time-Out Timer Send/read 30 min.Time-Out Timer  
        internal static IcomCommand IC1A050013 = new IcomCommand(0x1A, 0x05, 0x00, 0x13); // 00, 01 Send/read PTT Lock function OFF Send/read PTT Lock function ON  
        internal static IcomCommand IC1A050014 = new IcomCommand(0x1A, 0x05, 0x00, 0x14); // 00, 01 Send/read Quick Split function OFF Send/read Quick Split function ON  
        internal static IcomCommand IC1A050015 = new IcomCommand(0x1A, 0x05, 0x00, 0x15); // (see p. 192) Send/read Split offset frequency 
        internal static IcomCommand IC1A050016 = new IcomCommand(0x1A, 0x05, 0x00, 0x16); // 00, 01 Send/read Split Lock function OFF Send/read Split Lock function ON  
        internal static IcomCommand ICDupOffset = new IcomCommand(0x1A, 0x05, 0x00, 0x17); // (see p. 192) Send/read Duplex offset frequency  
        internal static IcomCommand IC1A050018 = new IcomCommand(0x1A, 0x05, 0x00, 0x18); // 00, 01 Send/read One Touch Repeater DUP– Send/read One Touch Repeater DUP+  
        internal static IcomCommand IC1A050019 = new IcomCommand(0x1A, 0x05, 0x00, 0x19); // 00, 01, 02 Send/read Auto Repeater OFF Send/read Auto Repeater ON-1 (for USA version) or ON (for Korea version) Send/read Auto Repeater ON-2 (for USA version)  
        internal static IcomCommand IC1A050020 = new IcomCommand(0x1A, 0x05, 0x00, 0x20); // 00, 01 Send/read Tuner Auto Start OFF Send/read Tuner Auto Start ON  
        internal static IcomCommand IC1A050021 = new IcomCommand(0x1A, 0x05, 0x00, 0x21); // 00, 01 Send/read PTT Tune OFF Send/read PTT Tune ON  
        internal static IcomCommand IC1A050022 = new IcomCommand(0x1A, 0x05, 0x00, 0x22); // 00, 01, 02 Send/read antenna selection OFF Send/read manual antenna selection Send/read auto antenna selection  
        internal static IcomCommand IC1A050023 = new IcomCommand(0x1A, 0x05, 0x00, 0x23); // 0000 to 0255 Send/read voice synthesizer level (0000=0%to0255=100%)  
        internal static IcomCommand IC1A050024 = new IcomCommand(0x1A, 0x05, 0x00, 0x24); // 00, 01 Send/read English selection for voice synthesizer speech language Send/read Japanese selection for voice synthesizer speech language
        internal static IcomCommand IC1A050025 = new IcomCommand(0x1A, 0x05, 0x00, 0x25); // 00, 01 Send/read speech speed slow Send/read speech speed fast  
        internal static IcomCommand IC1A050026 = new IcomCommand(0x1A, 0x05, 0x00, 0x26); // 00, 01 Send/read S-meter level announcement OFF Send/read S-meter level announcement ON  
        internal static IcomCommand IC1A050027 = new IcomCommand(0x1A, 0x05, 0x00, 0x27); // 00, 01 Send/read operating mode announcement (after pushing mode switch) OFF Send/read operating mode announcement (after pushing mode switch) ON  
        internal static IcomCommand IC1A050028 = new IcomCommand(0x1A, 0x05, 0x00, 0x28); // 00, 01 Send/read [SPEECH/LOCK] key function setting (Push=SPEECH,Holddown=LOCK) Send/read [SPEECH/LOCK] key function setting (Push=LOCK,Holddown=SPEECH)  
        internal static IcomCommand IC1A050029 = new IcomCommand(0x1A, 0x05, 0x00, 0x29); // 00, 01 Send/readnumberofmemopadchannels 5 Send/readnumberofmemopadchannels 10  
        internal static IcomCommand IC1A050030 = new IcomCommand(0x1A, 0x05, 0x00, 0x30); // 00, 01, 02 Send/read auto TS for [MAIN DIAL] OFF Send/read auto TS for [MAIN DIAL] Low Send/read auto TS for [MAIN DIAL] High  
        internal static IcomCommand IC1A050031 = new IcomCommand(0x1A, 0x05, 0x00, 0x31); // 00, 01 Send/read Low selection for microphone Up/Down speed Send/read High selection for microphone Up/Down speed  
        internal static IcomCommand IC1A050032 = new IcomCommand(0x1A, 0x05, 0x00, 0x32); // 00, 01 Send/read Quick RIT/?TX clear OFF Send/read Quick RIT/?TX clear ON  
        internal static IcomCommand ICAFCLimit = new IcomCommand(0x1A, 0x05, 0x00, 0x33); // 00, 01 Send/read AFC functioning range limit OFF Send/read AFC functioning range limit ON  
        internal static IcomCommand IC1A050034 = new IcomCommand(0x1A, 0x05, 0x00, 0x34); // 00, 01, 02 Send/read Auto Notch selection for SSB mode Send/read Manual notch selection for SSB mode Send/read Auto/Manual Notch selection for SSB mode  
        internal static IcomCommand IC1A050035 = new IcomCommand(0x1A, 0x05, 0x00, 0x35); // 00, 01, 02 Send/read Auto Notch selection for AM mode Send/read Manual Notch selection for AM mode Send/read Auto/Manual Notch selection for AM mode 
        internal static IcomCommand IC1A050036 = new IcomCommand(0x1A, 0x05, 0x00, 0x36); // 00, 01 Send/read Manual Notch filter width pop-up OFF Send/read Manual Notch filter width pop-up ON  
        internal static IcomCommand IC1A050037 = new IcomCommand(0x1A, 0x05, 0x00, 0x37); // 00, 01 Send/read BW Popup (PBT) setting OFF Send/read BW Popup (PBT) setting ON  
        internal static IcomCommand IC1A050038 = new IcomCommand(0x1A, 0x05, 0x00, 0x38); // 00, 01 Send/read BW Popup (FIL) setting OFF Send/read BW Popup (FIL) setting ON  
        internal static IcomCommand IC1A050039 = new IcomCommand(0x1A, 0x05, 0x00, 0x39); // 00, 01 Send/read SSB/CW Synchronous Tuning function OFF Send/read SSB/CW Synchronous Tuning function ON  
        internal static IcomCommand IC1A050040 = new IcomCommand(0x1A, 0x05, 0x00, 0x40); // 00, 01 Send/read LSB selection for CW normal side Send/read USB selection for CW normal side  
        internal static IcomCommand IC1A050041 = new IcomCommand(0x1A, 0x05, 0x00, 0x41); // 00, 01 Send/readKEYER-Rootselectionforkeyer 1st menu Send/readKEYER-SENDselectionforkeyer 1st menu  
        internal static IcomCommand IC1A050042 = new IcomCommand(0x1A, 0x05, 0x00, 0x42); // 00, 01 Send/read GPS-Root selection for GPS 1st Menu Send/read GPS-POS selection for GPS 1st Menu  
        internal static IcomCommand IC1A050043 = new IcomCommand(0x1A, 0x05, 0x00, 0x43); // 00, 01 Send/read external preamplifier (AG-25) control for 144 MHz band OFF Send/read external preamplifier (AG-25) control for 144 MHz band ON  
        internal static IcomCommand IC1A050044 = new IcomCommand(0x1A, 0x05, 0x00, 0x44); // 00, 01 Send/read external preamplifier (AG-35) control for 430 MHz band OFF Send/read external preamplifier (AG-35) control for 430 MHz band ON  
        internal static IcomCommand IC1A050045 = new IcomCommand(0x1A, 0x05, 0x00, 0x45); // 00, 01 Send/read external preamplifier (AG1200) control for 1200 MHz band OFF Send/read external preamplifier (AG1200) control for 1200 MHz band ON  
        internal static IcomCommand IC1A050046 = new IcomCommand(0x1A, 0x05, 0x00, 0x46); // 00, 01 Send/read Separate selection for the external speaker output method Send/read Mix selection for the external speaker output method  
        internal static IcomCommand IC1A050047 = new IcomCommand(0x1A, 0x05, 0x00, 0x47); // 00, 01, 02 Send/read Separate selection for the headphone audio output method Send/read Mix selection for the headphone audio output method Send/read Auto selection for the headphone audio output method  
        internal static IcomCommand IC1A050048 = new IcomCommand(0x1A, 0x05, 0x00, 0x48); // 00, 01 Send/read SUB Band audio mute during transmit on the main band OFF Send/read SUB Band audio mute during transmit on the main band ON  
        internal static IcomCommand IC1A050049 = new IcomCommand(0x1A, 0x05, 0x00, 0x49); // 00, 01 Send/read MAIN selection for the [ACC] AF/SQL line output Send/read SUB selection for the [ACC] AF/SQL line output  
        internal static IcomCommand IC1A050050 = new IcomCommand(0x1A, 0x05, 0x00, 0x50); // 00, 01 Send/read MAIN selection for the [DATA] AF/SQL line output Send/read SUB selection for the [DATA] AF/SQL line output  
        internal static IcomCommand IC1A050051 = new IcomCommand(0x1A, 0x05, 0x00, 0x51); // 00, 01, 02 Send/read VSEND select OFF Send/read UHF Only selection for VSEND Send/read VSEND select ON  
        internal static IcomCommand IC1A050052 = new IcomCommand(0x1A, 0x05, 0x00, 0x52); // 00, 01 Send/read external keypad OFF Send/readKEYERSENDselectionfortheexternal keypad  
        internal static IcomCommand IC1A050053 = new IcomCommand(0x1A, 0x05, 0x00, 0x53); // 00, 01 Send/read USB audio squelch OFF (OPEN) Send/read USB audio squelch ON 
        internal static IcomCommand IC1A050054 = new IcomCommand(0x1A, 0x05, 0x00, 0x54); // 0000 to 0255 Send/read USB modulation level (0000=0%to0255=100%)  
        internal static IcomCommand IC1A050055 = new IcomCommand(0x1A, 0x05, 0x00, 0x55); // 00, 01 Send/read 9600 bps mode OFF Send/read 9600 bps mode ON  
        internal static IcomCommand IC1A050056 = new IcomCommand(0x1A, 0x05, 0x00, 0x56); // 00, 01, 02, 03 Send/read MIC selection for the modulation input during DATA mode OFF Send/read ACC selection for the modulation input during DATA mode OFF Send/read MIC+ACC selection for the modulation input during DATA mode OFF Send/read USB selection for the modulation input during DATA mode OFF  
        internal static IcomCommand IC1A050057 = new IcomCommand(0x1A, 0x05, 0x00, 0x57); // 00, 01, 02, 03 Send/read MIC selection for the modulation input during DATA mode ON Send/read ACC selection for the modulation input during DATA mode ON Send/read MIC+ACC selection for the modulation input during DATA mode ON Send/read USB selection for the modulation input during DATA mode ON  
        internal static IcomCommand IC1A050058 = new IcomCommand(0x1A, 0x05, 0x00, 0x58); // 00, 01 Send/read CI-V transceive OFF Send/read CI-V transceive ON  
        internal static IcomCommand IC1A050059 = new IcomCommand(0x1A, 0x05, 0x00, 0x59); // 00, 01, 02 Send/read no function selection for “USB2” (COM port) function Send/readRTTYselectionfor“USB2”(COM port) function Send/read DVdat selection for “USB2”(COM port) function  
        internal static IcomCommand IC1A050060 = new IcomCommand(0x1A, 0x05, 0x00, 0x60); // 00, 01, 02 Send/read no function selection for [DATA1] function Send/readRTTYselectionfor[DATA1]function Send/read DVdat selection for [DATA1] function  
        internal static IcomCommand IC1A050061 = new IcomCommand(0x1A, 0x05, 0x00, 0x61); // 00, 01 Send/read OFF selection for GPS Out Send/read DATA->USB2 selection for GPS Out  
        internal static IcomCommand IC1A050062 = new IcomCommand(0x1A, 0x05, 0x00, 0x62); // 00, 01 Send/read 4800 bps selection for GPS position data transmission speed of [DATA1] Send/read 9600 bps selection for GPS position data transmission speed of [DATA1]  
        internal static IcomCommand IC1A050063 = new IcomCommand(0x1A, 0x05, 0x00, 0x63); // 00, 01, 02, 03, 04 Send/read300bpsselectionforRTTYDecode Baud rate Send/read1200bpsselectionforRTTYDecode Baud rate Send/read4800bpsselectionforRTTYDecode Baud rate Send/read9600bpsselectionforRTTYDecode Baud rate Send/read19200bpsselectionforRTTYDecode Baud rate  
        internal static IcomCommand IC1A050064 = new IcomCommand(0x1A, 0x05, 0x00, 0x64); // 00, 01 Send/read Calibration marker OFF Send/read Calibration marker ON  
        internal static IcomCommand IC1A050065 = new IcomCommand(0x1A, 0x05, 0x00, 0x65); // 0000 to 0255 Send/read reference frequency (0000=0%to0255=100%)  
        internal static IcomCommand IC1A050066 = new IcomCommand(0x1A, 0x05, 0x00, 0x66); // 00 to 10 Send/read COMP level (00=Minimumto10=Maximum)  
        internal static IcomCommand IC1A050067 = new IcomCommand(0x1A, 0x05, 0x00, 0x67); // (see p. 192) Send/read SSB RX HPF/LPF setting  
        internal static IcomCommand IC1A050068 = new IcomCommand(0x1A, 0x05, 0x00, 0x68); // 00 to 10 Send/read SSB RX Tone (Bass) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050069 = new IcomCommand(0x1A, 0x05, 0x00, 0x69); // 00 to 10 Send/read SSB RX Tone (Treble) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050070 = new IcomCommand(0x1A, 0x05, 0x00, 0x70); // 00 to 10 Send/read SSB TX Tone (Bass) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050071 = new IcomCommand(0x1A, 0x05, 0x00, 0x71); // 00 to 10 Send/read SSB TX Tone (Treble) level (00=–5to10=+5)  
        internal static IcomCommand ICTXBandwidthWide = new IcomCommand(0x1A, 0x05, 0x00, 0x72); // (see p. 192) Send/read SSB TX bandwidth for WIDE 
        internal static IcomCommand ICTXBandwidthMid = new IcomCommand(0x1A, 0x05, 0x00, 0x73); // (see p. 192) Send/read SSB TX bandwidth for MID  
        internal static IcomCommand ICTXBandwidthNarrow = new IcomCommand(0x1A, 0x05, 0x00, 0x74); // (see p. 192) Send/read SSB TX bandwidth for NARROW  
        internal static IcomCommand IC1A050075 = new IcomCommand(0x1A, 0x05, 0x00, 0x75); // (see p. 192) Send/read AM RX HPF/LPF setting  
        internal static IcomCommand IC1A050076 = new IcomCommand(0x1A, 0x05, 0x00, 0x76); // 00 to 10 Send/read AM RX tone (Bass) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050077 = new IcomCommand(0x1A, 0x05, 0x00, 0x77); // 00 to 10 Send/read AM RX Tone (Treble) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050078 = new IcomCommand(0x1A, 0x05, 0x00, 0x78); // 00 to 10 Send/read AM TX tone (Bass) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050079 = new IcomCommand(0x1A, 0x05, 0x00, 0x79); // 00 to 10 Send/read AM TX Tone (Treble) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050080 = new IcomCommand(0x1A, 0x05, 0x00, 0x80); // (see p. 192) Send/read FM RX HPF/LPF setting  
        internal static IcomCommand IC1A050081 = new IcomCommand(0x1A, 0x05, 0x00, 0x81); // 00 to 10 Send/read FM RX tone (Bass) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050082 = new IcomCommand(0x1A, 0x05, 0x00, 0x82); // 00 to 10 Send/read FM RX Tone (Treble) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050083 = new IcomCommand(0x1A, 0x05, 0x00, 0x83); // 00 to 10 Send/read FM TX tone (Bass) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050084 = new IcomCommand(0x1A, 0x05, 0x00, 0x84); // 00 to 10 Send/read FM TX Tone (Treble) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050085 = new IcomCommand(0x1A, 0x05, 0x00, 0x85); // (see p. 192) Send/read DV RX HPF/LPF setting  
        internal static IcomCommand IC1A050086 = new IcomCommand(0x1A, 0x05, 0x00, 0x86); // 00 to 10 Send/read DV RX tone (Bass) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050087 = new IcomCommand(0x1A, 0x05, 0x00, 0x87); // 00 to 10 Send/read DV RX Tone (Treble) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050088 = new IcomCommand(0x1A, 0x05, 0x00, 0x88); // 00 to 10 Send/read DV TX tone (Bass) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050089 = new IcomCommand(0x1A, 0x05, 0x00, 0x89); // 00 to 10 Send/read DV TX Tone (Treble) level (00=–5to10=+5)  
        internal static IcomCommand IC1A050090 = new IcomCommand(0x1A, 0x05, 0x00, 0x90); // (see p. 192) Send/read CW RX HPF/LPF setting  
        internal static IcomCommand IC1A050091 = new IcomCommand(0x1A, 0x05, 0x00, 0x91); // (see p. 192) Send/readRTTYRXHPF/LPFsetting  
        internal static IcomCommand IC1A050092 = new IcomCommand(0x1A, 0x05, 0x00, 0x92); // 00, 01, 02, 03, 04 Send/read Normal selection for contest number style Send/read “190 ? ANO” selection for contest number style Send/read “190 ? ANT” selection for contest number style Send/read “90 ? NO” selection for contest number style Send/read “90 ? NT” selection for contest number style  
        internal static IcomCommand IC1A050093 = new IcomCommand(0x1A, 0x05, 0x00, 0x93); // 01, 02, 03, 04 Send/read M1 selection for count up trigger channel Send/read M2 selection for count up trigger channel Send/read M3 selection for count up trigger channel Send/read M4 selection for count up trigger channel  
        internal static IcomCommand IC1A050094 = new IcomCommand(0x1A, 0x05, 0x00, 0x94); // 0001 to 9999 Send/read present number (0001=1to9999=9999)  
        internal static IcomCommand ICSidetoneGain = new IcomCommand(0x1A, 0x05, 0x00, 0x95); // 0000 to 0255 Send/read CW sidetone gain (0000=0%to0255=100%)  
        internal static IcomCommand IC1A050096 = new IcomCommand(0x1A, 0x05, 0x00, 0x96); // 00, 01 Send/read CW sidetone gain limit OFF Send/read CW sidetone gain limit ON  
        internal static IcomCommand IC1A050097 = new IcomCommand(0x1A, 0x05, 0x00, 0x97); // 01 to 60 Send/read CW keyer repeat time (01=1sec.to60=60sec.)  
        internal static IcomCommand IC1A050098 = new IcomCommand(0x1A, 0x05, 0x00, 0x98); // 00, 01 Send/read Normal selection for message display Send/read Message selection for message display  
        internal static IcomCommand IC1A050099 = new IcomCommand(0x1A, 0x05, 0x00, 0x99); // 28 to 45 Send/read CW keyer dot/dash ratio (28=1:1:2.8to45=1:1:4.5)  
        internal static IcomCommand IC1A050100 = new IcomCommand(0x1A, 0x05, 0x01, 0x00); // 00, 01, 02, 03 Send/read 2 msec. selection for CW Rise time Send/read 4 msec. selection for CW Rise time Send/read 6 msec. selection for CW Rise time Send/read 8 msec. selection for CW Rise time 
        internal static IcomCommand IC1A050101 = new IcomCommand(0x1A, 0x05, 0x01, 0x01); // 00, 01 Send/read Normal selection for paddle polarity Send/read Reverse selection for paddle polarity  
        internal static IcomCommand ICKeyer = new IcomCommand(0x1A, 0x05, 0x01, 0x02); // 00, 01, 02 Send/read Straight selection for keyer type Send/read BUG-Key selection for keyer type Send/read ELEC-Key selection for keyer type  
        internal static IcomCommand IC1A050103 = new IcomCommand(0x1A, 0x05, 0x01, 0x03); // 00, 01 Send/read Mic. up/down keyer OFF Send/read Mic. up/down keyer ON  
        internal static IcomCommand IC1A050104 = new IcomCommand(0x1A, 0x05, 0x01, 0x04); // 00, 01, 02 Send/read1275HzselectionforRTTYmark frequency Send/read1615HzselectionforRTTYmark frequency Send/read2125HzselectionforRTTYmark frequency  
        internal static IcomCommand IC1A050105 = new IcomCommand(0x1A, 0x05, 0x01, 0x05); // 00, 01, 02 Send/read170HzselectionforRTTYshiftwidth Send/read200HzselectionforRTTYshiftwidth Send/read425HzselectionforRTTYshiftwidth  
        internal static IcomCommand IC1A050106 = new IcomCommand(0x1A, 0x05, 0x01, 0x06); // 00, 01 Send/readNormalselectionforRTTYkeying polarity Send/readReverseselectionforRTTYkeying polarity  
        internal static IcomCommand IC1A050107 = new IcomCommand(0x1A, 0x05, 0x01, 0x07); // 00, 01 Send/readRTTYdecodeUSOSOFF Send/readRTTYdecodeUSOSON  
        internal static IcomCommand IC1A050108 = new IcomCommand(0x1A, 0x05, 0x01, 0x08); // 00, 01 Send/read “CR,LF,CR+LF” selection for RTTYdecodenewlinecode Send/read“CR+LF”selectionforRTTYdecode new line code  
        internal static IcomCommand IC1A050109 = new IcomCommand(0x1A, 0x05, 0x01, 0x09); // 00, 01 Send/read 2 lines selection for number of RTTYdecoderline Send/read 3 lines selection for number of RTTYdecoderline  
        internal static IcomCommand IC1A050110 = new IcomCommand(0x1A, 0x05, 0x01, 0x10); // 00, 01 Send/read Scan speed Low Send/read Scan speed High  
        internal static IcomCommand IC1A050111 = new IcomCommand(0x1A, 0x05, 0x01, 0x11); // 00, 01 Send/read Scan resume OFF Send/read Scan resume ON  
        internal static IcomCommand IC1A050112 = new IcomCommand(0x1A, 0x05, 0x01, 0x12); // 00, 01 Send/read OFF selection for MAIN DIAL function during a scan Send/read Up/Down selection for MAIN DIAL function during a scan  
        internal static IcomCommand IC1A050113 = new IcomCommand(0x1A, 0x05, 0x01, 0x13); // 0000 to 0255 Send/read NB level (HF/50 MHz) (0000=0%to0255=100%)  
        internal static IcomCommand ICNBDepthB80_6 = new IcomCommand(0x1A, 0x05, 0x01, 0x14); // 00 to 09 Send/read NB depth (HF/50 MHz) (00=1to09=10)  
        internal static IcomCommand ICNBWidthB80_6 = new IcomCommand(0x1A, 0x05, 0x01, 0x15); // 0000 to 0255 Send/read NB width (HF/50 MHz) (0000=1to0255=100)  
        internal static IcomCommand IC1A050116 = new IcomCommand(0x1A, 0x05, 0x01, 0x16); // 0000 to 0255 Send/read NB level (144 MHz) (0000=0%to0255=100%)  
        internal static IcomCommand ICNBDepthB2= new IcomCommand(0x1A, 0x05, 0x01, 0x17); // 00 to 09 Send/read NB depth (144 MHz) (00=1to09=10)  
        internal static IcomCommand ICNBWidthB2 = new IcomCommand(0x1A, 0x05, 0x01, 0x18); // 0000 to 0255 Send/read NB width (144 MHz) (0000=1to0255=100)  
        internal static IcomCommand IC1A050119 = new IcomCommand(0x1A, 0x05, 0x01, 0x19); // 0000 to 0255 Send/read NB level (430 MHz) (0000=0%to0255=100%)  
        internal static IcomCommand ICNBDepthB440 = new IcomCommand(0x1A, 0x05, 0x01, 0x20); // 00 to 09 Send/read NB depth (430 MHz) (00=1to09=10)  
        internal static IcomCommand ICNBWidthB440 = new IcomCommand(0x1A, 0x05, 0x01, 0x21); // 0000 to 0255 Send/read NB width (430 MHz) (0000=1to0255=100)  
        internal static IcomCommand IC1A050122 = new IcomCommand(0x1A, 0x05, 0x01, 0x22); // 0000 to 0255 Send/read NB level (1200 MHz) (0000=0%to0255=100%)  
        internal static IcomCommand ICNBDepthB1200 = new IcomCommand(0x1A, 0x05, 0x01, 0x23); // 00 to 09 Send/read NB depth (1200 MHz) (00=1to09=10)  
        internal static IcomCommand ICNBWidthB1200 = new IcomCommand(0x1A, 0x05, 0x01, 0x24); // 0000 to 0255 Send/read NB width (1200 MHz) (0000=1to0255=100) 
        internal static IcomCommand IC1A050125 = new IcomCommand(0x1A, 0x05, 0x01, 0x25); // 0000 to 0255 Send/read VOX gain (0000=0%to0255=100%)  
        internal static IcomCommand IC1A050126 = new IcomCommand(0x1A, 0x05, 0x01, 0x26); // 0000 to 0255 Send/read ANTI-VOX gain (0000=0%to0255=100%)  
        internal static IcomCommand ICVoxDelay = new IcomCommand(0x1A, 0x05, 0x01, 0x27); // 00 to 20 Send/read VOX delay time (00=0.0sec.to20=2.0sec.)  
        internal static IcomCommand ICVoiceDelay = new IcomCommand(0x1A, 0x05, 0x01, 0x28); // 00, 01, 02, 03 Send/read VOX voice delay OFF Send/read Short selection for VOX voice delay Send/read Mid selection for VOX voice delay Send/read Long selection for VOX voice delay  
        internal static IcomCommand IC1A050129 = new IcomCommand(0x1A, 0x05, 0x01, 0x29); // 0020 to 0130 Send/read BK-IN delay time (0020=2.0dto0130=13.0d)  
        internal static IcomCommand IC1A050130 = new IcomCommand(0x1A, 0x05, 0x01, 0x30); // 0000 to 0255 Send/read MONITOR gain (0000=0%to0255=100%)  
        internal static IcomCommand IC1A050131 = new IcomCommand(0x1A, 0x05, 0x01, 0x31); // 00, 01, 02 Send/read Standby Beep OFF Send/read ON-1 selection for Standby Beep Send/read ON-2 selection for Standby Beep  
        internal static IcomCommand IC1A050132 = new IcomCommand(0x1A, 0x05, 0x01, 0x32); // 00, 01 Send/read Auto Reply OFF Send/read Auto Reply ON  
        internal static IcomCommand IC1A050133 = new IcomCommand(0x1A, 0x05, 0x01, 0x33); // 00, 01 Send/read PTT selection for DV Data TX Send/read Auto selection for DV Data TX  
        internal static IcomCommand IC1A050134 = new IcomCommand(0x1A, 0x05, 0x01, 0x34); // 00, 01, 02 Send/read Auto selection for Digital Monitor Send/read Digital selection for Digital Monitor Send/read Analog selection for Digital Monitor  
        internal static IcomCommand IC1A050135 = new IcomCommand(0x1A, 0x05, 0x01, 0x35); // 00, 01 Send/read Digital RPT Set OFF Send/read Digital RPT Set ON  
        internal static IcomCommand IC1A050136 = new IcomCommand(0x1A, 0x05, 0x01, 0x36); // 00, 01 Send/read RX Call Sign Auto Write OFF Send/read Auto selection for RX Call Sign Auto Write  
        internal static IcomCommand IC1A050137 = new IcomCommand(0x1A, 0x05, 0x01, 0x37); // 00, 01 Send/read RX RPT Call Sign Auto Write OFF Send/read Auto selection for RX RPT Call Sign Auto Write  
        internal static IcomCommand IC1A050138 = new IcomCommand(0x1A, 0x05, 0x01, 0x38); // 00, 01 Send/read DV Auto Detect OFF Send/read DV Auto Detect ON  
        internal static IcomCommand IC1A050139 = new IcomCommand(0x1A, 0x05, 0x01, 0x39); // 00, 01, 02 Send/read Call Sign Edit Record OFF Send/read Select selection for Call Sign Edit Record Send/read Auto selection for Call Sign Edit Record  
        internal static IcomCommand IC1A050140 = new IcomCommand(0x1A, 0x05, 0x01, 0x40); // 00, 01 Send/read Gateway Auto Set OFF Send/read Auto selection for Gateway Auto Set  
        internal static IcomCommand IC1A050141 = new IcomCommand(0x1A, 0x05, 0x01, 0x41); // 00, 01 Send/read ALL selection for RX Record (RPT) Send/read Latest Only selection for RX Record (RPT)  
        internal static IcomCommand IC1A050142 = new IcomCommand(0x1A, 0x05, 0x01, 0x42); // 00, 01 Send/read RX Call Sign Auto Display OFF Send/read Auto selection for RX Call Sign Auto Display  
        internal static IcomCommand IC1A050143 = new IcomCommand(0x1A, 0x05, 0x01, 0x43); // 00, 01, 02 Send/read TX Call Sign Display OFF Send/read UR selection for TX Call Sign Display Send/readMYselectionforTXCallSignDisplay  
        internal static IcomCommand IC1A050144 = new IcomCommand(0x1A, 0x05, 0x01, 0x44); // 00, 01 Send/read RX Message Display OFF Send/read Auto selection for RX Message Display  
        internal static IcomCommand IC1A050145 = new IcomCommand(0x1A, 0x05, 0x01, 0x45); // 00, 01 Send/read Scrolling speed slow/fast
        internal static IcomCommand IC1A050146 = new IcomCommand(0x1A, 0x05, 0x01, 0x46); // 00, 01 Send/read DR Call Sign Popup OFF Send/read DR Call Sign Popup ON  
        internal static IcomCommand IC1A050147 = new IcomCommand(0x1A, 0x05, 0x01, 0x47); // 00, 01 Send/read Opening Call Sign OFF Send/read Opening Call Sign ON  
        internal static IcomCommand IC1A050148 = new IcomCommand(0x1A, 0x05, 0x01, 0x48); // 00, 01 Send/read BK function OFF Send/read BK function ON  
        internal static IcomCommand IC1A050149 = new IcomCommand(0x1A, 0x05, 0x01, 0x49); // 00, 01 Send/read EMR mode OFF Send/read EMR mode ON  
        internal static IcomCommand IC1A050150 = new IcomCommand(0x1A, 0x05, 0x01, 0x50); // 0000 to 0255 Send/read EMR AF Level (0000=0%to0255=100%)  
        internal static IcomCommand IC1A050151 = new IcomCommand(0x1A, 0x05, 0x01, 0x51); // 00, 01 Send/read 4800 bps selection for GPS Receiver Baud Send/read 9600 bps selection for GPS Receiver Baud  
        internal static IcomCommand IC1A050152 = new IcomCommand(0x1A, 0x05, 0x01, 0x52); // 00, 01 Send/read ddd°mm.mm’ selection for Position Format Send/read ddd°mm’ss” selection for Position Format  
        internal static IcomCommand IC1A050153 = new IcomCommand(0x1A, 0x05, 0x01, 0x53); // 00, 01 Send/read Meter selection for the displaying unit Send/read Feet/Mile selection for the displaying unit  
        internal static IcomCommand IC1A050154 = new IcomCommand(0x1A, 0x05, 0x01, 0x54); // 00, 01 Send/read North REF selection for compass direction Send/read South REF selection for compass direction  
        internal static IcomCommand IC1A050155 = new IcomCommand(0x1A, 0x05, 0x01, 0x55); // (see p. 192) Send/read UTC Offset  
        internal static IcomCommand IC1A050156 = new IcomCommand(0x1A, 0x05, 0x01, 0x56); // 00, 01 Send/read GPS Indicator OFF Send/read GPS Indicator ON  
        internal static IcomCommand IC1A050157 = new IcomCommand(0x1A, 0x05, 0x01, 0x57); // 00, 01 Send/readGPSselectionforMYPositioninput method Send/readManualselectionforMYPosition input method  
        internal static IcomCommand IC1A050158 = new IcomCommand(0x1A, 0x05, 0x01, 0x58); // (see p. 192) Send/read my position information  
        internal static IcomCommand IC1A050159 = new IcomCommand(0x1A, 0x05, 0x01, 0x59); // (see p. 193) Send/read Alarm Area1  
        internal static IcomCommand IC1A050160 = new IcomCommand(0x1A, 0x05, 0x01, 0x60); // 00, 01, 02 Send/read Limited selection for Alarm Area2 Send/read Extended selection for Alarm Area2 Send/read Both selection for Alarm Area2  
        internal static IcomCommand IC1A050161 = new IcomCommand(0x1A, 0x05, 0x01, 0x61); // 00, 01, 02, 03, 04, 05, 06, 07, 08 Send/read GPS Auto TX OFF Send/read 5 sec. selection for GPS Auto TX interval Send/read 10 sec. selection for GPS Auto TX interval Send/read 30 sec. selection for GPS Auto TX interval Send/read 1 min. selection for GPS Auto TX interval Send/read 3 min. selection for GPS Auto TX interval Send/read 5 min. selection for GPS Auto TX interval Send/read 10 min. selection for GPS Auto TX interval Send/read 30 min. selection for GPS Auto TX interval  
        internal static IcomCommand IC1A050162 = new IcomCommand(0x1A, 0x05, 0x01, 0x62); // 00, 01, 02 Send/read Disable selection for GPS TX Mode Send/read GPS selection for GPS TX Mode Send/read GPS-A selection for GPS TX Mode  
        internal static IcomCommand IC1A050163 = new IcomCommand(0x1A, 0x05, 0x01, 0x63); // 00, 01 Send/read GPS Sentence (RMC) OFF Send/read GPS Sentence (RMC) ON  
        internal static IcomCommand IC1A050164 = new IcomCommand(0x1A, 0x05, 0x01, 0x64); // 00, 01 Send/read GPS Sentence (GGA) OFF Send/read GPS Sentence (GGA) ON 
        internal static IcomCommand IC1A050165 = new IcomCommand(0x1A, 0x05, 0x01, 0x65); // 00, 01 Send/read GPS Sentence (GLL) OFF Send/read GPS Sentence (GLL) ON  
        internal static IcomCommand IC1A050166 = new IcomCommand(0x1A, 0x05, 0x01, 0x66); // 00, 01 Send/read GPS Sentence (GSA) OFF Send/read GPS Sentence (GSA) ON  
        internal static IcomCommand IC1A050167 = new IcomCommand(0x1A, 0x05, 0x01, 0x67); // 00, 01 Send/read GPS Sentence (VTG) OFF Send/read GPS Sentence (VTG) ON  
        internal static IcomCommand IC1A050168 = new IcomCommand(0x1A, 0x05, 0x01, 0x68); // 00, 01 Send/read GPS Sentence (GSV) OFF Send/read GPS Sentence (GSV) ON  
        internal static IcomCommand IC1A050169 = new IcomCommand(0x1A, 0x05, 0x01, 0x69); // (see p. 193) Send/read Unproto Address  
        internal static IcomCommand IC1A050170 = new IcomCommand(0x1A, 0x05, 0x01, 0x70); // 00, 01 Send/read position data extension OFF Send/read Course/Speed selection for position data extension  
        internal static IcomCommand IC1A050171 = new IcomCommand(0x1A, 0x05, 0x01, 0x71); // 00, 01, 02 Send/read Time Stamp OFF Send/read DHM selection for Time Stamp Send/read HMS selection for Time Stamp  
        internal static IcomCommand IC1A050172 = new IcomCommand(0x1A, 0x05, 0x01, 0x72); // 00 to 16 Send/read GPS-A Symbol (00=Ambulance,01=Bus,02=FireTruck,03=Bicycle,04=Yacht,05=Helicopter,06=SmallAircraft,07=Ship,08=Car,09=Motorcycle,10=Balloon,11=Jeep,12=RV,13=Truck,14=Van,15=HouseQTH(VHF),16=Other)  
        internal static IcomCommand IC1A050173 = new IcomCommand(0x1A, 0x05, 0x01, 0x73); // (see p. 193) Send/read GPS-A Symbol Other  
        internal static IcomCommand IC1A050174 = new IcomCommand(0x1A, 0x05, 0x01, 0x74); // 00 to 16 Send/read GPS-A SSID (00=--,01=(-0),02=-1to16,-15)  
        internal static IcomCommand IC1A050175 = new IcomCommand(0x1A, 0x05, 0x01, 0x75); // (see p. 193) Send/read Comment  
        internal static IcomCommand IC1A050176 = new IcomCommand(0x1A, 0x05, 0x01, 0x76); // (see p. 193) Send/read Comment (Extension)  
        internal static IcomCommand IC1A050177 = new IcomCommand(0x1A, 0x05, 0x01, 0x77); // (see p. 193) Send/read GPS TX Message  
        internal static IcomCommand ICDataMode = new IcomCommand(0x1A, 0x06); // (see p. 193) Send/read DATA mode selection  
        internal static IcomCommand ICSatelliteMemoryContents = new IcomCommand(0x1A, 0x07); // (see p. 196) Send/read Satellite memory contents  
        internal static IcomCommand ICReadToneFreq = new IcomCommand(0x1B, 0x00); // (see p. 193) read Repeater tone frequency  
        internal static IcomCommand ICSendToneFreq = new IcomCommand(0x1B, 0x00, 0x00); // (see p. 193) Send Repeater tone frequency  
        internal static IcomCommand ICReadToneSquelchFreq = new IcomCommand(0x1B, 0x01); // (see p. 193) read Tone squelch frequency  
        internal static IcomCommand ICSendToneSquelchFreq = new IcomCommand(0x1B, 0x01, 0x00); // (see p. 193) Send Tone squelch frequency  
        internal static IcomCommand ICDTCSCodeAndPolarity = new IcomCommand(0x1B, 0x02); // (see p. 193) Send/read DTCS code and polarity  
        internal static IcomCommand ICCSQL = new IcomCommand(0x1B, 0x07); // (see p. 193) Send/read CSQL code (DV mode)  
        internal static IcomCommand ICRXTX = new IcomCommand(0x1C, 0x00); // 00, 01 Send/read Transceiver’s status (RX) Send/read Transceiver’s status (TX)  
        internal static IcomCommand ICTuner = new IcomCommand(0x1C, 0x01); // 00, 01, 02 Send/read Antenna tuner OFF (through) Send/read Antenna tuner ON Send/read Manual tuning selection  
        internal static IcomCommand ICXmitMonitor = new IcomCommand(0x1C, 0x02); // 00, 01 Send/read Transmit frequency monitor check OFF Send/read Transmit frequency monitor check ON  
        internal static IcomCommand ICTXFreqBand = new IcomCommand(0x1E, 0x00); // Read number of available TX frequency band  
        internal static IcomCommand ICTXBandEdgeFreqs = new IcomCommand(0x1E, 0x01); // (see p. 191) Read TX band edge frequencies  
        internal static IcomCommand ICUsersetTXFreqBand = new IcomCommand(0x1E, 0x02); // Read number of user-set TX frequency band  
        internal static IcomCommand ICUsersetTXBandEdgeFreqs = new IcomCommand(0x1E, 0x03); // (see p. 191) Send/read User-set TX band edge frequencies  
        internal static IcomCommand ICDVMyCallsign = new IcomCommand(0x1F, 0x00); // (see p. 193) Send/read DVMYcallsign  
        internal static IcomCommand ICDVTXCallsigns = new IcomCommand(0x1F, 0x01); // (see p. 194) Send/read DV TX call signs  
        internal static IcomCommand ICDVTXMsg = new IcomCommand(0x1F, 0x02); // (see p. 194) Send/read DV TX message
        internal static IcomCommand ICOK = new IcomCommand(0xfb); // OK response

        internal static byte[] CommandHDR;
        internal static int CommandHDRLen;
        internal const byte CommandTerm = 0xfd;
        internal class CommandItem
        {
            public byte[] Command;
            public int MS;
            internal CommandItem(byte[] cmd, int ms)
            {
                Command = cmd;
                MS = ms;
            }
        }
        private Queue commandQ;
        private Thread commandThread;
        internal const int defaultConfirmTime = 3000; // 3 second(s)
        
        // Send commands from a queue.
        private void commandProc()
        {
            Tracing.TraceLine("commandProc", TraceLevel.Info);
            try
            {
                while (true)
                {
                    while (commandQ.Count == 0) Thread.Sleep(10);
                    CommandItem cmd = (CommandItem)commandQ.Dequeue();
                    // Ignore a null item, probably a sync.
                    if (cmd == null) continue;
                    if (cmd.Command.Length <= CommandHDRLen)
                    {
                        Tracing.TraceLine("CommandProc:invalid command " + Escapes.Escapes.Decode(cmd.Command), TraceLevel.Error);
                        continue;
                    }
                    // get response command.
                    IcomIhandler.ResponseItem syncCmd = rigHandler.LookupCommand(cmd.Command,
                        CommandHDRLen, cmd.Command.Length - CommandHDRLen);
                    lock (rigHandler.SyncItem)
                    {
                        rigHandler.SyncItem.SyncCommand = syncCmd;
                    }
                    // send the command
                    // tracing is in the send routine
                    try { Callouts.SendBytesRoutine(cmd.Command); }
                    catch (Exception ex)
                    {
                        Tracing.ErrMessageTrace(ex, false, false);
                        continue;
                    }
                    // await response.
                    if ((syncCmd != null) && (cmd.MS != 0))
                    {
                        int period = (cmd.MS > 10) ? 10 : cmd.MS;
                        bool rv = await(checkSync, cmd.MS, period);
                        Tracing.TraceLine("commandProc await:" + Escapes.Escapes.Decode(cmd.Command) + ' ' + rv.ToString(), TraceLevel.Info);
                    }
                }
            }
            catch (ThreadAbortException) { Tracing.TraceLine("commandThread aborted", TraceLevel.Error); }
            catch (Exception ex)
            {
                Tracing.TraceLine("commandProc:" + ex.Message, TraceLevel.Error);
            }
        }
        // commandProc helper
        private bool checkSync()
        {
            bool rv;
            lock (rigHandler.SyncItem)
            {
                rv = rigHandler.SyncItem.ClearToSend;
                if (rv) rigHandler.SyncItem.SyncCommand = null;
                rigHandler.SyncItem.ClearToSend = false;
            }
            return rv;
        }

        internal void SendCommand(byte[] bytes)
        {
            SendCommand(bytes, true);
        }
        internal void SendCommand(byte[] bytes, bool await)
        {
            int time = (await) ? defaultConfirmTime : 0;
            SendCommand(bytes, time);
        }
        internal void SendCommand(byte[] bytes, int time)
        {
            if (commandThread.IsAlive)
            {
                CommandItem item = new CommandItem(bytes, time);
                commandQ.Enqueue(item); // See commandProc()
            }
            else Tracing.TraceLine("SendCommand:command thread not running:" + Escapes.Escapes.Decode(bytes), TraceLevel.Error);
        }
        // Sync with the command queue; all commands sent.
        internal bool commandSync(int ms)
        {
            commandQ.Enqueue(null);
            return await(() => { return (commandQ.Count == 0); }, ms);
        }

        // Command builders.
        private const byte b100 = 100;
        private const byte b10 = 10;
        // build a command from a byte array.
        internal static byte[] BldCmd(byte[] cmd)
        {
            byte[] rv = new byte[CommandHDRLen + cmd.Length + 1];
            Array.Copy(CommandHDR, rv, CommandHDRLen);
            Array.ConstrainedCopy(cmd, 0, rv, CommandHDRLen, cmd.Length);
            rv[rv.Length - 1] = CommandTerm;
            return rv;
        }
        // build a command from an Icom command.
        internal static byte[] BldCmd(IcomCommand cmd)
        {
            return BldCmd(cmd.Command);
        }
        // build a command with a byte array parameter.
        internal static byte[] BldCmd(IcomCommand cmd, byte[] b)
        {
            byte[] bytes = new byte[cmd.Command.Length + b.Length];
            Array.Copy(cmd.Command, bytes, cmd.Command.Length);
            Array.ConstrainedCopy(b, 0, bytes, cmd.Command.Length, b.Length);
            return BldCmd(bytes);
        }
        // build a command containing a one-byte number.
        internal static byte[] BldCmd(IcomCommand cmd, byte b)
        {
            byte[] bytes = new byte[cmd.Command.Length + 1];
            Array.Copy(cmd.Command, bytes, cmd.Command.Length);
            int d2 = b / 10;
            int d1 = b - (d2 * 10);
            bytes[cmd.Command.Length] = (byte)((d1 & 0xf) + ((d2 & 0xf) * 16));
            return BldCmd(bytes);
        }
        // build a command For 0000 - 0255.
        internal static byte[] BldCmd(IcomCommand cmd, int i)
        {
            int len = cmd.Command.Length;
            byte[] bytes = new byte[len + 2];
            Array.Copy(cmd.Command, bytes, len);
            Array.ConstrainedCopy(IntToBCD(i), 0, bytes, len, 2);
            return BldCmd(bytes);
        }

        internal static byte[] BldMemCmd(int grp, int num)
        {
            int len = ICMemContents.Command.Length;
            byte[] cmd = new byte[len + 1 + 2];
            Array.Copy(ICMemContents.Command, cmd, len);
            cmd[len] = (byte)grp;
            Array.ConstrainedCopy(IntToBCD(num), 0, cmd, len + 1, 2);
            return BldCmd(cmd);
        }

        /// <summary>
        /// Convert into the output range.
        /// </summary>
        /// <param name="inVal">input value</param>
        /// <param name="inLow">low input value</param>
        /// <param name="inHigh">high input value</param>
        /// <param name="outLow">low output value (default 0)</param>
        /// <param name="outHigh">high output value (default 255)</param>
        /// <returns>converted integer value</returns>
        internal int RangeConvert(int inVal, int inLow, int inHigh, int outLow, int outHigh)
        {
            int outVal = outLow;
            float inRange = inHigh - inLow;
            float outRange = outHigh - outLow;
            // Ensure the value is in range.
            if (inVal < inLow) inVal = inLow;
            else if (inVal > inHigh) inVal = inHigh;
            // get relative pos in in range.
            inVal = inVal - inLow;
            // Convert to out range.
            float rangePos = (float)inVal * (outRange / inRange);
            // Get out val rounding the range pos.
            outVal += (int)(rangePos + 0.5f);
            return outVal;
        }
        internal int RangeConvert(int inVal, int inLow, int inHigh)
        {
            return RangeConvert(inVal, inLow, inHigh, 0, 255);
        }
        /// <summary>
        /// convert a value from the rig, 0-255.
        /// </summary>
        /// <param name="inVal">value from rig</param>
        /// <param name="inLow">low output</param>
        /// <param name="inHigh">high output</param>
        /// <returns>value between inlow and inhigh</returns>
        internal int RigRangeConvert(int inVal, int inLow, int inHigh)
        {
            return RangeConvert(inVal, 0, 255, inLow, inHigh);
        }

        internal IcomCommand VFOCommand(RigCaps.VFOs vfo)
        {
            IcomCommand cmd = null;
            switch (vfo)
            {
                case RigCaps.VFOs.VFOA: cmd = ICSelectVFOA; break;
                case RigCaps.VFOs.VFOB: cmd = ICSelectVFOB; break;
            }
            return cmd;
        }

        internal enum BandRanges
        {
            b80_6,
            b2,
            b440,
            b1200
        }
        internal BandRanges BandRange(ulong freq)
        {
            BandRanges rv = BandRanges.b80_6; // default
            Bands.BandItem item = Bands.Query(freq);
            if (item != null)
            {
                switch (item.Band)
                {
                    case Bands.BandNames.m2: rv = BandRanges.b2; break;
                    case Bands.BandNames.m70: rv = BandRanges.b440; break;
                    case Bands.BandNames.m23: rv = BandRanges.b1200; break;
                }
            }
            return rv;
        }

        internal static int BCDToInt(params byte[] bytes)
        {
            int rv;
            if (bytes.Length < 2)
            {
                rv = (bytes[0] & 0xf) + 10 * (bytes[0] / 16);
            }
            else
            {
                rv = (bytes[1] & 0xf) + 10 * (bytes[1] / 16);
                rv += 100 * (bytes[0] & 0xf) + 1000 * (bytes[0] / 16);
            }
            return rv;
        }

        internal static byte[] IntToBCD(int i)
        {
            byte[] rv = new byte[2];
            string str = i.ToString("d4");
            rv[0] = (byte)((str[0] & 0xf) * 16 + (str[1] & 0xf));
            rv[1] = (byte)((str[2] & 0xf) * 16 + (str[3] & 0xf));
            return rv;
        }
        #endregion

        // region - rig properties
        #region properties
        /// <summary>
        /// Power status.
        /// Don't allow set now.
        /// </summary>
        public override bool Power
        {
            get { return base.Power; }
            set { }
        }

        protected override void PowerCheck()
        {
            SendCommand(BldCmd(ICReadXCVRID));
        }

        private System.Threading.Timer rigWatchTimer;
        internal const int rigWatchInterval = 250;
        protected bool RXTXRcvd;
        protected bool RXTXRslt;
        /// <summary>
        /// This timer handler periodically watches rig values
        /// </summary>
        private void rigWatcher(object s)
        {
            if (sendingOutput != AllRadios.CommandReporting.none) return;
            SendCommand(BldCmd(ICRXTX));
            bool complete = await(() => { return RXTXRcvd; }, rigWatchInterval / 5);
            if (complete & RXTXRslt)
            {
                SendCommand(BldCmd(ICRFMeter));
            }
            else
            {
                SendCommand(BldCmd(ICSMeter));
            }
        }

        protected void RigWatchTimerStart()
        {
            Tracing.TraceLine("RigWatchTimerStart", TraceLevel.Info);
            rigWatchTimer = new System.Threading.Timer(new TimerCallback(rigWatcher), null, rigWatchInterval, rigWatchInterval);
        }

        protected void RigWatchTimerStop()
        {
            Tracing.TraceLine("RigWatchTimerStop:" + (rigWatchTimer != null).ToString(), TraceLevel.Info);
            if (rigWatchTimer == null) return;
            WaitHandle hand = new AutoResetEvent(false);
            rigWatchTimer.Dispose(hand);
            hand.WaitOne(rigWatchInterval + (rigWatchInterval / 2));
            hand.Dispose();
        }

        public override bool Transmit
        {
            get { return base.Transmit; }
            set
            {
                byte v = (byte)((value) ? 1 : 0);
                TransmitStatus = value;
                SendCommand(BldCmd(ICRXTX, v));
            }
        }

        public override RigCaps.VFOs RXVFO
        {
            get
            {
                return base.RXVFO;
            }
            set
            {
                if (!Transmit)
                {
                    bool saveSplit = Split;
                    IcomCommand cmd = VFOCommand(value);
                    _RXVFO = value; // might be VFOs.None, see MemoryMode.
                    if (cmd == null) return;
                    Split = saveSplit; // Sets TX vfo.
                    SendCommand(BldCmd(cmd));
                    getFilterInfo();
                }
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
                _TXVFO = value;
            }
        }

        public override void CopyVFO(RigCaps.VFOs inv, RigCaps.VFOs outv)
        {
            RigCaps.VFOs saveVfo = RXVFO;
            IcomCommand cmd = VFOCommand(inv);
            IcomCommand cmd2 = null;
            if (cmd == null) return;
            if (inv != saveVfo)
            {
                SendCommand(BldCmd(cmd));
                cmd2 = VFOCommand(saveVfo); // restore command
            }
            vfoItems[GetVfoID(outv)] = new vfoData(vfoItems[GetVfoID(inv)]);
            SendCommand(BldCmd(ICVFOEqual));
            if (inv != saveVfo) SendCommand(BldCmd(cmd2));
        }

#if zero
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
                    // set TFSet
                    TFSetOn = value;
                }
            }
        }
#endif

        public override ulong RXFrequency
        {
            get { return (TFSetOn) ? vfoItems[GetVfoID(TXVFO)].Frequency : vfoItems[GetVfoID(RXVFO)].Frequency; }
            set
            {
                // Can't do this if TFSet is on.
                if (TFSetOn) return;
                byte[] iFreq = FreqToIFreq(value);
                vfoItems[GetVfoID(RXVFO)].Frequency = value;
                SendCommand(BldCmd(ICSendOpFreq, iFreq));
                _RXFrequency = value;
            }
        }

        public override ulong TXFrequency
        {
            get { return vfoItems[GetVfoID(TXVFO)].Frequency; }
            set
            {
                byte[] iFreq = FreqToIFreq(value);
                vfoItems[GetVfoID(TXVFO)].Frequency = value;
                SendCommand(BldCmd(ICSendOpFreq, iFreq));
            }
        }

        private bool _Split;
        public override bool Split
        {
            get
            {
                if (IsMemoryMode(_RXVFO)) return base.Split;
                return _Split;
            }
            set
            {
                // not set if using a memory
                if (!IsMemoryMode(RXVFO))
                {
                    // Using VFOs.
                    _Split = value;
                    _TXVFO = (value) ? nextVFO(RXVFO) : RXVFO;
                    SendCommand(BldCmd((value) ? ICSplitOn : ICSplitOff));
                }
            }
        }

        // Filters
        internal class FilterData
        {
            public int Filter;
            public int Width;
            internal FilterData(int f, int w)
            {
                Filter = f;
                Width = w;
            }
        }
        internal int Filter
        {
            get { return vfoItems[GetVfoID(CurVFO)].FilterItem.Filter; }
            set
            {
                vfoItems[GetVfoID(CurVFO)].FilterItem.Filter = value;
                // The filter is set with the Mode.
                Mode = getMode((byte)Mode.value);
                SendCommand(BldCmd(ICFilterWidth));
            }
        }

        internal virtual int FilterWidth
        {
            get
            {
                return vfoItems[GetVfoID(CurVFO)].FilterItem.Width;
            }
            set
            {
                // See ICFilterWidth
                int val;
                if (Mode == myModeTable[(int)modes.am])
                {
                    if (value < 200) value = 200;
                    else if (value > 10000) value = 10000;
                    value = ((value + 199) / 200) * 200;
                    val = (value - 200) / 200;
                }
                else
                {
                    int highLimit = (Mode == myModeTable[(int)modes.fsk]) ? 2700 : 3600;
                    if (value < 50) value = 50;
                    else if (value > highLimit) value = highLimit;
                    else if ((value > 500) && (value < 600)) value = 600;
                    value = (value <= 500) ? ((value + 49) / 50) * 50 : ((value + 99) / 100) * 100;
                    val = (value < 600) ? (value - 50) / 50 : (value / 100) + 4;
                }
                vfoItems[GetVfoID(CurVFO)].FilterItem.Width = value;
                SendCommand(BldCmd(ICFilterWidth, (byte)val));
            }
        }

        internal enum FilterTypes
        {
            sharp,
            soft
        }
        internal FilterTypes _FilterType;
        internal FilterTypes FilterType
        {
            get { return _FilterType; }
            set
            {
                _FilterType = value;
                byte b = (byte)((value == FilterTypes.soft) ? 1 : 0);
                SendCommand(BldCmd(ICDSPFilterType, b));
            }
        }

        internal void getFilterInfo()
        {
            if (Mode != myModeTable[(int)modes.fm])
            {
                SendCommand(BldCmd(ICFilterWidth));
                SendCommand(BldCmd(ICDSPFilterType));
                SendCommand(BldCmd(ICInnerPBT));
                SendCommand(BldCmd(ICOuterPBT));
                SendCommand(BldCmd(ICManualNotch));
                SendCommand(BldCmd(ICNotchPosition));
                SendCommand(BldCmd(ICNotchWidth));
            }
            else
            {
                // We need to reset the tone mode, since there are two commands for it.
                _ToneMode = ToneTypes.off;
                SendCommand(BldCmd(ICTone));
                SendCommand(BldCmd(ICToneSquelch));
                SendCommand(BldCmd(ICReadToneFreq));
                SendCommand(BldCmd(ICReadToneSquelchFreq));
                SendCommand(BldCmd(ICDupOffset));
            }
            SendCommand(BldCmd(ICAGC));
            SendCommand(BldCmd(ICAGCtc));
            SendCommand(BldCmd(ICAutoNotch));
            SendCommand(BldCmd(ICReadSplit));
            SendCommand(BldCmd(ICTuningStep));
        }

        public override ModeValue RXMode
        {
            get
            {
                return vfoItems[GetVfoID(RXVFO)].Mode;
            }
            set
            {
                // Don't set if transmitting
                if (Transmit) return;
                int id = GetVfoID(RXVFO);
                vfoItems[id].Mode = value;
                byte[] b = new byte[2] { IMode(value), (byte)vfoItems[id].FilterItem.Filter };
                SendCommand(BldCmd(ICSendOpMode, b));
                getFilterInfo();
            }
        }

        public override ModeValue TXMode
        {
            get
            {
                return vfoItems[GetVfoID(TXVFO)].Mode;
            }
            set
            {
                // Don't set if transmitting
                if (Transmit) return;
                vfoItems[GetVfoID(TXVFO)].Mode = value;
            }
        }

        public override int SMeter
        {
            get
            {
                return (Transmit) ? _ICPower: _SMeter;
            }
        }

        /// <summary>
        /// Default max power
        /// </summary>
        internal virtual int MyPower
        {
            get { return 100; }
        }

        public override int XmitPower
        {
            get
            {
                return base.XmitPower;
            }
            set
            {
                _XmitPower = value;
                int v = RangeConvert(value, 0, MyPower);
                SendCommand(BldCmd(ICRFPower, v));
            }
        }

        public override AntTunerVals AntennaTuner
        {
            get { return base.AntennaTuner; }
            set
            {
                // If tuning, turn on the tuner.
                if ((value & AntTunerVals.tune) != 0)
                {
                    // Tune
                    _AntennaTuner = (AntTunerVals.rx | AntTunerVals.tx | AntTunerVals.tune);
                    SendCommand(BldCmd(ICTuner, (byte)2));
                    return;
                }
                // Set both rx and tx to the changed value.
                bool tunerOn = false;
                if ((_AntennaTuner & AntTunerVals.rx) != (value & AntTunerVals.rx))
                {
                    // rx changed
                    tunerOn = ((value & AntTunerVals.rx) != 0);
                }
                else if ((_AntennaTuner & AntTunerVals.tx) != (value & AntTunerVals.tx))
                {
                    // tx changed
                    tunerOn = ((value & AntTunerVals.tx) != 0);
                }
                _AntennaTuner = (tunerOn) ? AntTunerVals.rx | AntTunerVals.tx : 0;
                SendCommand(BldCmd(ICTuner, (byte)((tunerOn) ? 1 : 0)));
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
                if (value > 255) value = 255;
                else if (value < 0) value = 0;
                _LineoutGain = value;
                SendCommand(BldCmd(ICAFGain, value));
            }
        }

        // Actually this is RF gain
        public override int AudioGain
        {
            get
            {
                return base.AudioGain;
            }
            set
            {
                if (value > 255) value = 255;
                else if (value < 0) value = 0;
                _AudioGain = value;
                SendCommand(BldCmd(ICRFGain, value));
            }
        }

        private bool _myVox, _myCWVox;
        internal bool cwMode()
        {
            return ((Mode == myModeTable[(int)modes.cw]) || (Mode == myModeTable[(int)modes.cwr])) ? true : false;
        }

        public override OffOnValues Vox
        {
            get
            {
                bool rv = (cwMode()) ? _myCWVox : _myVox;
                return (rv) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                byte b = (byte)((value == OffOnValues.on) ? 1 : 0);
                if (cwMode())
                {
                    // full or semi-brkin depends upon the delay.
                    _myCWVox = (b != 0);
                    if (_myCWVox)
                    {
                        b = (byte)((_BreakinDelay == 0) ? 2 : 1);
                    }
                    else b = 0;
                    SendCommand(BldCmd(ICBrkin, b));
                }
                else
                {
                    _myVox = (b != 0);
                    SendCommand(BldCmd(ICVox, b));
                }
            }
        }

        // these are in miliseconds
        internal const int BreakinDelayMin = 0;
        internal const int BreakinDelayMax = 1300;
        internal const int BreakinDelayIncrement = 100;
        // 0 means full breakin.
        internal int __BreakinDelay;
        internal int _BreakinDelay
        {
            get { return __BreakinDelay; }
            set { __BreakinDelay = RoundInt(value, BreakinDelayIncrement); }
        }
        internal int BreakinDelay
        {
            get { return _BreakinDelay; }
            set
            {
                // Note:  value is in milliseconds.
                if (value > 0)
                {
                    if (value > 1300) value = 1300;
                    else if (value < 200) value = 200;
                    _BreakinDelay = value;
                    // 0000=20ds, 0255=130ds
                    int val = RangeConvert(_BreakinDelay, 200, 1300);
                    SendCommand(BldCmd(ICBrkinDelay, val));
                }
                else
                {
                    _BreakinDelay = 0;
                }
                // This ensures that the CW Vox is correct.
                if ((Vox == OffOnValues.on) && cwMode()) Vox = OffOnValues.on;
            }
        }

        internal const int KeyerSpeedMin = 6;
        internal const int KeyerSpeedMax = 48;
        private int _KeyerSpeed;
        internal int KeyerSpeed
        {
            get { return _KeyerSpeed; }
            set
            {
                _KeyerSpeed = value;
                int val = RangeConvert(value, KeyerSpeedMin, KeyerSpeedMax);
                SendCommand(BldCmd(ICKeyerSpeed, val));
            }
        }

        internal const int CWPitchMin = 300;
        internal const int CWPitchMax = 900;
        internal const int CWPitchIncrement = 50;
        internal int __CWPitch;
        internal int _CWPitch
        {
            get { return __CWPitch; }
            set { __CWPitch = RoundInt(value, CWPitchIncrement); }
        }
        internal int CWPitch
        {
            get { return _CWPitch; }
            set
            {
                _CWPitch = value;
                int val = RangeConvert(_CWPitch, CWPitchMin, CWPitchMax);
                SendCommand(BldCmd(ICCWPitch, val));
            }
        }

        internal enum KeyerValues
        {
            Key = 0,
            Bug = 1,
            Keyer = 2
        }
        private KeyerValues _Keyer;
        internal KeyerValues Keyer
        {
            get { return _Keyer; }
            set
            {
                try { _Keyer = (KeyerValues)value; }
                catch (Exception ex)
                {
                    Tracing.TraceLine("keyer exception:" + ex.Message, TraceLevel.Error);
                    return;
                }
                SendCommand(BldCmd(ICKeyer, (byte)value));
            }
        }

        internal const int SidetoneGainMin = 0;
        internal const int SidetoneGainMax = 100;
        internal const int SidetoneGainIncrement = 5;
        internal int __SidetoneGain;
        internal int _SidetoneGain
        {
            get { return __SidetoneGain; }
            set { __SidetoneGain = RoundInt(value, SidetoneGainIncrement); }
        }
        internal int SidetoneGain
        {
            get { return _SidetoneGain; }
            set
            {
                _SidetoneGain = value;
                int val = RangeConvert(_SidetoneGain, SidetoneGainMin, SidetoneGainMax);
                SendCommand(BldCmd(ICSidetoneGain, val));
            }
        }

        internal static string[] firstIFSizes =
        {
            "3khz",
            "6khz",
            "15khz",
        };
        internal string _FirstIF;
        internal string FirstIF
        {
            get { return _FirstIF; }
            set
            {
                _FirstIF = value;
                int val = Array.IndexOf(firstIFSizes, value);
                if (val != -1) SendCommand(BldCmd(ICFirstIFFilter, (byte)val));
            }
        }

        internal const int InnerPBTMin = -(16 * 50);
        internal const int InnerPBTMax = (16 * 50);
        internal const int InnerPBTIncrement = 50;
        internal int __InnerPBT;
        internal int _InnerPBT
        {
            get { return __InnerPBT; }
            set { __InnerPBT = RoundInt(value, InnerPBTIncrement); }
        }
        internal int InnerPBT
        {
            get { return _InnerPBT; }
            set
            {
                _InnerPBT = value;
                int val = RangeConvert(_InnerPBT, InnerPBTMin, InnerPBTMax);
                SendCommand(BldCmd(ICInnerPBT, val));
            }
        }

        internal int __OuterPBT;
        internal int _OuterPBT
        {
            get { return __OuterPBT; }
            set { __OuterPBT = RoundInt(value, InnerPBTIncrement); }
        }
        internal int OuterPBT
        {
            get { return _OuterPBT; }
            set
            {
                _OuterPBT = value;
                int val = RangeConvert(_OuterPBT, InnerPBTMin, InnerPBTMax);
                SendCommand(BldCmd(ICOuterPBT, val));
            }
        }

        internal const int pcMin = 0;
        internal const int pcMax = 100;
        internal const int pcIncrement = 5;
        internal int __VoxGain;
        internal int _VoxGain
        {
            get { return __VoxGain; }
            set { __VoxGain = RoundInt(value, pcIncrement); }
        }
        internal int VoxGain
        {
            get { return _VoxGain; }
            set
            {
                _VoxGain = value;
                int val = RangeConvert(_VoxGain, pcMin, pcMax);
                SendCommand(BldCmd(ICVoxGain, val));
            }
        }

        internal int __AntiVox;
        internal int _AntiVox
        {
            get { return __AntiVox; }
            set { __AntiVox = RoundInt(value, pcIncrement); }
        }
        internal int AntiVox
        {
            get { return _AntiVox; }
            set
            {
                _AntiVox = value;
                int val = RangeConvert(_AntiVox, pcMin, pcMax);
                SendCommand(BldCmd(ICAntiVoxGain, val));
            }
        }

        internal int __VoxDelay;
        internal int _VoxDelay
        {
            get { return __VoxDelay; }
            set { __VoxDelay = RoundInt(value, pcIncrement); }
        }
        internal int VoxDelay
        {
            get { return _VoxDelay; }
            set
            {
                _VoxDelay = value;
                int val = RangeConvert(_VoxDelay, pcMin, pcMax, 0, 20);
                SendCommand(BldCmd(ICVoxDelay, (byte)val));
            }
        }

        internal int __MicGain;
        internal int _MicGain
        {
            get { return __MicGain; }
            set { __MicGain = RoundInt(value, pcIncrement); }
        }
        internal int MicGain
        {
            get { return _MicGain; }
            set
            {
                _MicGain = value;
                int val = RangeConvert(_MicGain, pcMin, pcMax);
                SendCommand(BldCmd(ICMicGain, val));
            }
        }

        internal OffOnValues _Comp;
        internal OffOnValues Comp
        {
            get { return _Comp; }
            set
            {
                _Comp = value;
                byte b = (value == OffOnValues.on) ? (byte)1 : (byte)0;
                SendCommand(BldCmd(ICComp, b));
            }
        }

        internal const int CompLevelMin = 0;
        internal const int CompLevelMax = 10;
        internal const int CompLevelIncrement = 1;
        internal int _CompLevel;
        internal int CompLevel
        {
            get { return _CompLevel; }
            set
            {
                _CompLevel = value;
                int val = RangeConvert(value, CompLevelMin, CompLevelMax);
                SendCommand(BldCmd(ICCompLevel, val));
            }
        }

        internal enum SSBTransmitBandwidthValues
        {
            wide = 0,
            mid,
            narrow,
        }
        internal SSBTransmitBandwidthValues _SSBTransmitBandwidth;
        internal SSBTransmitBandwidthValues SSBTransmitBandwidth
        {
            get { return _SSBTransmitBandwidth; }
            set
            {
                _SSBTransmitBandwidth = value;
                SendCommand(BldCmd(ICSSBTransmitBandwidth, (byte)value));
            }
        }
        // The internal width is the raw value from the rig.
        // the value is 0xHL, High/Low, see ic9100filters.cs.
        internal byte[] _BWWidth = new byte[Enum.GetValues(typeof(SSBTransmitBandwidthValues)).Length];
        internal byte BWWidth
        {
            get { return _BWWidth[(int)SSBTransmitBandwidth]; }
            set
            {
                _BWWidth[(int)SSBTransmitBandwidth] = value;
                IcomCommand cmd = ICTXBandwidthWide;
                if (SSBTransmitBandwidth == SSBTransmitBandwidthValues.mid) cmd = ICTXBandwidthMid;
                else if (SSBTransmitBandwidth == SSBTransmitBandwidthValues.narrow) cmd = ICTXBandwidthNarrow;
                else return;
                SendCommand(BldCmd(cmd, new byte[1] { value }));
            }
        }

        internal OffOnValues _NR;
        internal OffOnValues NR
        {
            get { return _NR; }
            set
            {
                _NR = value;
                byte b = (byte)((value == OffOnValues.on) ? 1 : 0);
                SendCommand(BldCmd(ICNR, b));
            }
        }

        internal int __NRLevel;
        internal int _NRLevel
        {
            get { return __NRLevel; }
            set { __NRLevel = RoundInt(value, pcIncrement); }
        }
        internal int NRLevel
        {
            get { return _NRLevel; }
            set
            {
                _NRLevel = value;
                int val = RangeConvert(_NRLevel, pcMin, pcMax);
                SendCommand(BldCmd(ICNRLevel, val));
            }
        }

        internal enum IcomAGCValues
        {
            off,
            fast,
            medium,
            slow
        }
        internal bool IcomAGCOff;
        internal IcomAGCValues _IcomAGC;
        internal IcomAGCValues IcomAGC
        {
            get { return (IcomAGCOff) ? IcomAGCValues.off : _IcomAGC; }
            set
            {
                bool wasOff = IcomAGCOff;
                IcomAGCOff = (value == IcomAGCValues.off);
                if (IcomAGCOff)
                {
                    // turn off with time constant = 0.
                    SendCommand(BldCmd(ICAGCtc, (byte)0));
                }
                else
                {
                    if (wasOff)
                    {
                        // Reset to prior values.
                        SendCommand(BldCmd(ICAGC, (byte)_IcomAGC));
                        SendCommand(BldCmd(ICAGCtc, (byte)_AGCtc));
                    }
                    else
                    {
                        _IcomAGC = value;
                        SendCommand(BldCmd(ICAGC, (byte)value));
                        SendCommand(BldCmd(ICAGCtc)); // tc for this setting.
                    }
                }
            }
        }

        internal const int AGCtcMin = 1; // 0 means off
        internal const int AGCtcMax = 13;
        internal const int AGCtcIncrement = 1;
        internal int _AGCtc;
        internal int AGCtc
        {
            get { return _AGCtc; }
            set
            {
                if (value == 0) return; // don't allow to turn off here.
                _AGCtc = value;
                SendCommand(BldCmd(ICAGCtc, (byte)value));
            }
        }

        public override OffOnValues NoiseBlanker
        {
            get
            {
                return base.NoiseBlanker;
            }
            set
            {
                base.NoiseBlanker = value;
                byte b = (byte)((value == OffOnValues.on) ? 1 : 0);
                SendCommand(BldCmd(ICNB, b));
            }
        }

        internal int __NBLevel;
        internal int _NBLevel
        {
            get { return __NBLevel; }
            set { __NBLevel = RoundInt(value, pcIncrement); }
        }
        internal int NBLevel
        {
            get { return _NBLevel; }
            set
            {
                _NBLevel = value;
                int val = RangeConvert(_NBLevel, pcMin, pcMax);
                SendCommand(BldCmd(ICNBLevel, val));
            }
        }

        internal const int NBDepthMin = 1;
        internal const int NBDepthMax = 10;
        internal const int NBDepthIncrement = 1;
        internal int[] _NBDepth = new int[Enum.GetValues(typeof(BandRanges)).Length];
        internal int NBDepth
        {
            get { return _NBDepth[(int)BandRange(RXFrequency)]; }
            set
            {
                _NBDepth[(int)BandRange(RXFrequency)] = value;
                int val = value - 1;
                IcomCommand cmd = ICNBDepthB80_6;
                switch (BandRange(RXFrequency))
                {
                    case BandRanges.b2: cmd = ICNBDepthB2; break;
                    case BandRanges.b440: cmd = ICNBDepthB440; break;
                    case BandRanges.b1200: cmd = ICNBDepthB1200; break;
                    default: return;
                }
                SendCommand(BldCmd(cmd, (byte)val));
            }
        }

        internal const int NBWidthMin = 1;
        internal const int NBWidthMax = 100;
        internal const int NBWidthIncrement = 5;
        internal int[] _NBWidth = new int[Enum.GetValues(typeof(BandRanges)).Length];
        internal int NBWidth
        {
            get { return _NBWidth[(int)BandRange(RXFrequency)]; }
            set
            {
                _NBWidth[(int)BandRange(RXFrequency)] = RoundInt(value, NBWidthIncrement);
                int val = RangeConvert(value - 1, NBWidthMin, NBWidthMax);
                IcomCommand cmd = ICNBWidthB80_6;
                switch (BandRange(RXFrequency))
                {
                    case BandRanges.b2: cmd = ICNBWidthB2; break;
                    case BandRanges.b440: cmd = ICNBWidthB440; break;
                    case BandRanges.b1200: cmd = ICNBWidthB1200; break;
                    default: return;
                }
                SendCommand(BldCmd(cmd, val));
            }
        }

        internal enum NotchValues
        {
            off,
            auto,
            manual
        }
        internal NotchValues _Notch;
        internal NotchValues Notch
        {
            get { return _Notch; }
            set
            {
                NotchValues oldNotch = _Notch;
                _Notch = value;
                IcomCommand cmd = null;
                byte b;
                if (value == NotchValues.off)
                {
                    b = (byte)0;
                    if (oldNotch == NotchValues.auto) cmd = ICAutoNotch;
                    else if (oldNotch == NotchValues.manual) cmd = ICManualNotch;
                }
                else
                {
                    b = (byte)1;
                    if (value == NotchValues.auto) cmd = ICAutoNotch;
                    else if (value == NotchValues.manual) cmd = ICManualNotch;
                }
                if (cmd != null) SendCommand(BldCmd(cmd, b));
            }
        }

        internal const int NotchPositionMin = 0;
        internal const int NotchPositionMax = 100;
        internal const int NotchPositionIncrement = 1;
        internal int _NotchPosition;
        internal int NotchPosition
        {
            get { return _NotchPosition; }
            set
            {
                _NotchPosition = value;
                int val = RangeConvert(value, NotchPositionMin, NotchPositionMax);
                SendCommand(BldCmd(ICNotchPosition, val));
            }
        }

        internal enum NotchWidthValues
        {
            wide=0,
            mid,
            narrow
        }
        internal NotchWidthValues _NotchWidth;
        internal NotchWidthValues NotchWidth
        {
            get { return _NotchWidth; }
            set
            {
                _NotchWidth = value;
                byte b = (byte)value;
                SendCommand(BldCmd(ICNotchWidth, b));
            }
        }

        public override OffOnValues AutoNotch
        {
            get
            {
                return (_Notch == NotchValues.auto) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                NotchValues val = (value == OffOnValues.on) ? NotchValues.auto : NotchValues.off;
                Notch = val;
            }
        }

        public override OffOnValues ManualNotch
        {
            get
            {
                return (_Notch == NotchValues.manual) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                NotchValues val = (value == OffOnValues.on) ? NotchValues.manual : NotchValues.off;
                Notch = val;
            }
        }

        internal enum VoiceDelayValues
        {
            off,
            small,
            medium,
            large
        }
        internal VoiceDelayValues _VoiceDelay;
        internal VoiceDelayValues VoiceDelay
        {
            get { return _VoiceDelay; }
            set
            {
                _VoiceDelay = value;
                byte b = (byte)value;
                SendCommand(BldCmd(ICVoiceDelay, b));
            }
        }

        internal OffOnValues _Monitor;
        internal OffOnValues Monitor
        {
            get { return _Monitor; }
            set
            {
                _Monitor = value;
                byte b = (byte)((value == OffOnValues.on) ? 1 : 0);
                SendCommand(BldCmd(ICMon, b));
            }
        }

        internal int _MonitorLevel;
        internal int MonitorLevel
        {
            get { return _MonitorLevel; }
            set
            {
                _MonitorLevel = value;
                int val = RangeConvert(value, pcMin, pcMax);
                SendCommand(BldCmd(ICMonLevel, val));
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
                _OffsetDirection = value;
                IcomCommand cmd = ICSymplex;
                if (value == OffsetDirections.minus) cmd = ICDup;
                else if (value == OffsetDirections.plus) cmd = ICDupPlus;
                SendCommand(BldCmd(cmd));
            }
        }

        // See AllRadios
        internal const int OffsetFrequencyMin = 0;
        internal const int OffsetFrequencyIncrement = 100;
        internal const int OffsetFrequencyMax = 99999900;
        public override int OffsetFrequency
        {
            get { return _OffsetFrequency; }
            set
            {
                _OffsetFrequency = value;
                SendCommand(BldCmd(ICDupOffset, OffsetToIOffset(value)));
            }
        }

        internal enum ToneTypes
        {
            off,
            tone,
            tcs,
            // tbd dtcs,
        }
        internal ToneTypes _ToneMode;
        internal ToneTypes ToneMode
        {
            get { return _ToneMode; }
            set
            {
                // Get the command and value to issue.
                ToneTypes cmdVal = value;
                byte b = 1;
                if (value == ToneTypes.off)
                {
                    // Turn off current setting.
                    cmdVal = _ToneMode;
                    b = 0;
                }
                IcomCommand cmd = ICTone;
                IcomCommand freqCmd = ICReadToneFreq;
                if (cmdVal == ToneTypes.tcs)
                {
                    cmd = ICToneSquelch;
                    freqCmd = ICReadToneSquelchFreq;
                }
#if zero
                else if (cmdVal == ToneTypes.dtcs)
                {
                    cmd = ICDTCS;
                    // get DTCS code and polarity.
                }
#endif
                _ToneMode = value;
                SendCommand(BldCmd(cmd, b));
                SendCommand(BldCmd(freqCmd));
            }
        }

        public override float ToneFrequency
        {
            get { return _ToneFrequency; }
            set
            {
                _ToneFrequency = value;
                IcomCommand cmd = (_ToneMode == ToneTypes.tone) ? ICSendToneFreq : ICSendToneSquelchFreq;
                // format is (e.g.) 107.2 = 1072.
                int i = (int)(value * 10);
                SendCommand(BldCmd(cmd, i));
            }
        }

        public override OffOnValues RFAttenuator
        {
            get { return base.RFAttenuator; }
            set
            {
                _RFAttenuator = value;
                IcomCommand cmd = (value == OffOnValues.on) ? ICAttenuator : ICAttenuatorOff;
                SendCommand(BldCmd(cmd));
            }
        }

        internal enum hfvhfValues
        {
            hf,
            vhf,
        }
        // _hfvhf must be set initially by the rig.  Only used by the 9100.
        internal hfvhfValues _hfvhf;
        internal virtual hfvhfValues hfvhf
        {
            get { return hfvhfValues.hf; }
        }

        internal enum HFPreampType
        {
            off,
            one,
            two
        }
        internal HFPreampType _HFPreamp;
        internal HFPreampType HFPreamp
        {
            get { return _HFPreamp; }
            set
            {
                _HFPreamp = value;
                byte b = (byte)value;
                SendCommand(BldCmd(ICPreAmp, b));
            }
        }

        internal float _SWR;
        internal float SWR
        {
            get { return _SWR; }
        }

        internal int _ALC;
        internal int ALC
        {
            get { return _ALC; }
        }

        internal int _CompMeter;
        internal int CompMeter
        {
            get { return _CompMeter; }
        }

        internal OffOnValues _AFC;
        internal OffOnValues AFC
        {
            get { return _AFC; }
            set
            {
                _AFC = value;
                byte b = (byte)((value == OffOnValues.on) ? 1 : 0);
                SendCommand(BldCmd(ICAFC, b));
            }
        }

        internal OffOnValues _AFCLimit;
        internal OffOnValues AFCLimit
        {
            get { return _AFCLimit; }
            set
            {
                _AFCLimit = value;
                byte b = (byte)((value == OffOnValues.on) ? 1 : 0);
                SendCommand(BldCmd(ICAFCLimit, b));
            }
        }

        internal OffOnValues _XmitMonitor;
        internal OffOnValues XmitMonitor
        {
            get { return _XmitMonitor; }
            set
            {
                _XmitMonitor = value;
                byte b = (byte)((value == OffOnValues.on) ? 1 : 0);
                SendCommand(BldCmd(ICXmitMonitor, b));
            }
        }

        // Tuning step values
        internal static int[] TuningStepValues =
        {   10, 100, 1000, 5000, 6250, 9000, 10000,
            12500, 20000, 25000, 50000, 100000, 1000000,
        };
        internal int _TuningStep;
        internal int TuningStep
        {
            get { return _TuningStep; }
            set
            {
                byte b = (byte)Array.IndexOf(TuningStepValues, value);
                if (b >= 0)
                {
                    _TuningStep = value;
                    SendCommand(BldCmd(ICTuningStep, b));
                }
                else Tracing.TraceLine("TuningStep:invalid value " + value.ToString(), TraceLevel.Error);
            }
        }
        #endregion

        // region - rig status
        #region RigStatus
        internal IcomCommand[] statCommands;
        private Thread statThread;
        /// <summary>
        /// Start the rig's continuous output and get rig status.
        /// This only runs if both power is on and continuous monitoring is on.
        /// The rig operations are run in a separate thread.
        /// </summary>
        /// <param name="ckPower">true to check power here</param>
        /// <param name="initial">true for initial call at device startup</param>
        protected override void getRigStatus(bool ckPower, bool initial)
        {
            Tracing.TraceLine("getRigStatus, ckPower:" + ckPower.ToString() + ' ' + initial.ToString(), TraceLevel.Info);
            // Get the radio's status.
            if (ckPower)
            {
                if (!powerOnCheck())
                {
                    // Quit here.  Note that we're called again if power comes back on.
                    Tracing.TraceLine("getRigstat:power not on", TraceLevel.Info);
                    return;
                }
            }
            statThread = new Thread(rigStatHelper);
            statThread.Name = "statThread";
            try { statThread.Start(initial); }
            catch (Exception ex)
            { Tracing.TraceLine("getRigStatus:" + ex.Message, TraceLevel.Error); }
            //Thread.Sleep(0);
        }
        protected int rsHelperRunning = 0;
        /// <summary>
        /// separate thread to get the rig's status at power-on or program startup.
        /// </summary>
        /// <param name="o">a bool object, true if initial call</param>
        private void rigStatHelper(object o)
        {
            bool initial = (bool)o;
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

                // Stop the rigWatch timer and suspend memory collection..
                RigWatchTimerStop();
                SuspendMemCollection = true;

                // Initial custom setup.
                initialCustomStatus(initial);

                // some default Icom setup.
                defaultStatus(initial);

                Tracing.TraceLine("rig status:" + statCommands.Length.ToString() + " commands", TraceLevel.Info);
                foreach (IcomCommand cmd in statCommands)
                {
                    SendCommand(BldCmd(cmd));
                    commandSync(defaultConfirmTime);
                }

                customStatus(initial);

                // Get memory info.
                if (initial) ICGetMemories(initial);

                // Allow memory collection and start the rig watcher timer for continuous status.
                SuspendMemCollection = false;
                RigWatchTimerStart();
            }
            catch (ThreadAbortException) { Tracing.TraceLine("rigStatHelper abort", TraceLevel.Error); }
            catch (Exception ex)
            {
                Tracing.TraceLine("rigStatHelper:" + initial.ToString() + ' ' + ex.Message, TraceLevel.Error);
            }
            finally { rsHelperRunning = 0; }
        }

        private void defaultStatus(bool initial)
        {
            Tracing.TraceLine("defaultStatus", TraceLevel.Info);
            // Turn transmit off.
            SendCommand(BldCmd(ICRXTX, (byte)0));
            // Get split/dup status.
            SendCommand(BldCmd(ICReadSplit));
            if (!commandSync(defaultConfirmTime)) Tracing.TraceLine("default status didn't get split", TraceLevel.Error);
            // Get current operating frequency.
            ulong curOpFreq;
            if (!getTheFreq(out curOpFreq)) Tracing.TraceLine("default status didn't get op freq", TraceLevel.Error);
            _RXFrequency = curOpFreq;

            // Get VFO-dependent info for VFO A.
            ulong dummy;
            _RXVFO = RigCaps.VFOs.VFOA; // so we set the right VFOItem
            SendCommand(BldCmd(ICSelectVFOA));
            if (!getTheFreq(out dummy)) Tracing.TraceLine("defaultStatus:didn't get VFO A frequency", TraceLevel.Error);
            SendCommand(BldCmd(ICReadOpMode));
            SendCommand(BldCmd(ICFilterWidth));
            SendCommand(BldCmd(ICDSPFilterType));
            // Await the filter stuff.
            if (!commandSync(3 * defaultConfirmTime)) Tracing.TraceLine("defaultStatus:didn't get VFO A mode and filter", TraceLevel.Error);

            // Get VFO-dependent info for VFO B.
            _RXVFO = RigCaps.VFOs.VFOB; // so we set the right VFOItem
            SendCommand(BldCmd(ICSelectVFOB));
            if (!getTheFreq(out dummy)) Tracing.TraceLine("defaultStatus:didn't get VFO B frequency", TraceLevel.Error);
            SendCommand(BldCmd(ICReadOpMode));
            SendCommand(BldCmd(ICFilterWidth));
            SendCommand(BldCmd(ICDSPFilterType));
            // Await the filter stuff.
            if (!commandSync(3 * defaultConfirmTime)) Tracing.TraceLine("defaultStatus:didn't get VFO B mode and filter", TraceLevel.Error);

            // Set to correct VFO.
            if (curOpFreq == vfoItems[(int)RigCaps.VFOs.VFOA].Frequency)
            {
                _RXVFO = RigCaps.VFOs.VFOA;
            }
            else if (curOpFreq == vfoItems[(int)RigCaps.VFOs.VFOB].Frequency)
            {
                _RXVFO = RigCaps.VFOs.VFOB;
            }
            else
            {
                // It's likely a memory, use VFO A.
                _RXVFO = RigCaps.VFOs.VFOA;
                _Split = false;
            }
            if (_RXVFO == RigCaps.VFOs.VFOA) SendCommand(BldCmd(ICSelectVFOA));
            if (!commandSync(2 * defaultConfirmTime)) Tracing.TraceLine("defaultStatus:didn't set VFO A at end", TraceLevel.Error);
            // Set the tx VFO and rig's split.
            // Only allow duplex plus/minus for fm.
            if ((RXMode.ToString() != "fm") && (OffsetDirection != OffsetDirections.off))
            {
                Tracing.TraceLine("defaultStatus:turning off duplex", TraceLevel.Info);
                OffsetDirection = OffsetDirections.off;
            }
            Split = _Split;  // sets TXVFO.
        }

        internal bool getTheFreq(out ulong freq)
        {
            freq = 0;
            freqJustRetrieved = 0;
            SendCommand(BldCmd(ICReadOpFreq));
            if (!await(() => { return (freqJustRetrieved != 0); }, defaultConfirmTime * 2)) return false;
            freq = freqJustRetrieved;
            return true;
        }

        /// <summary>
        /// Initial rig-specific setup.
        /// </summary>
        protected virtual void initialCustomStatus(bool initial) { }

        /// <summary>
        /// Customized status for this rig.
        /// </summary>
        protected virtual void customStatus(bool initial)
        {
            // Rigs override to provide
        }

        internal virtual void Reackquire()
        {
            Tracing.TraceLine("Reackquire", TraceLevel.Info);
            getRigStatus(false, true);
        }
        #endregion

        /// <summary>
        /// mode indecies which must match ModeTable
        /// </summary>
        internal enum modes
        { lsb, usb, cw, cwr, fm, am, fsk, fskr, dv, none }
        /// <summary>
        /// my mode table
        /// </summary>
        internal static ModeValue[] myModeTable =
            {
                new ModeValue(0, (char)0x00, "lsb"),
                new ModeValue(1, (char)0x01, "usb"),
                new ModeValue(2, (char)0x03, "cw"),
                new ModeValue(3, (char)0x07, "cwr"),
                new ModeValue(4, (char)0x05, "fm"),
                new ModeValue(5, (char)0x02, "am"),
                new ModeValue(6, (char)0x04, "fsk"),
                new ModeValue(7, (char)0x08, "fskr"),
                new ModeValue(8, (char)0x17, "dv"),
                new ModeValue(9, (char)0xff, "none"),
            };
        /// <summary>
        /// Icom mode character to internal mode.
        /// </summary>
        /// <param name="b">mode from rig</param>
        /// <returns>modes value</returns>
        protected virtual ModeValue getMode(byte b)
        {
            Tracing.TraceLine("getMode:" + b.ToString("x2"), TraceLevel.Info);
            ModeValue rv = ModeTable[(int)modes.none]; // Use none if invalid
            ModeValue cm = new ModeValue((char)b);
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
        /// Get mode to send to the rig.
        /// </summary>
        /// <param name="m">modeValue item</param>
        /// <returns>mode byte</returns>
        protected virtual byte IMode(ModeValue m)
        {
            Tracing.TraceLine("IMode:" + m.ToString(), TraceLevel.Info);
            byte rv = 0xff;
            foreach (ModeValue mv in myModeTable)
            {
                if (m == mv)
                {
                    rv = (byte)mv.value;
                    break;
                }
            }
            return rv;
        }

        /// <summary>
        /// Rig's 5-digit frequency to internal format.
        /// </summary>
        /// <param name="bytes">byte array from radio</param>
        /// <param name="id">offset of left-most digit</param>
        /// <returns>ulong frequency in HZ</returns>
        internal static ulong IFreqToFreq(byte[] bytes, int id)
        {
            ulong rv = 0;
            for (int i = 0; i < 5; i++)
            {
                int c = (bytes[id] & 0xf) + (bytes[id] / 16) * 10;
                rv += (ulong)(c * Math.Pow(10, i * 2));
                id++;
            }
            return rv;
        }

        /// <summary>
        /// Rig's 3-byte offset to internal format.
        /// </summary>
        /// <param name="bytes">byte array from radio</param>
        /// <param name="id">offset of left-most digit</param>
        /// <returns>int offset in HZ</returns>
        internal static int IOffsetToOffset(byte[] bytes, int id)
        {
            int rv = 0;
            for (int i = 0; i < 3; i++)
            {
                int c = (bytes[id] & 0xf) + (bytes[id] / 16) * 10;
                rv += (int)(c * Math.Pow(10, (i + 1) * 2));
                id++;
            }
            return rv;
        }

        internal static byte[] FreqToIFreq(ulong freq)
        {
            byte[] rv = new byte[5];
            string str = freq.ToString("d10");
            int k = 0;
            for (int i = 4; i >= 0; i--)
            {
                int j = i * 2;
                rv[k++] = (byte)((str[j + 1] & 0xf) + ((str[j] & 0xf) * 16));
            }
            return rv;
        }

        internal static byte[] OffsetToIOffset(int freq)
        {
            freq /= 100; // offset is in 100 hz increments
            byte[] rv = new byte[3];
            string str = freq.ToString("d6");
            int k = 0;
            for (int i = 2; i >= 0; i--)
            {
                int j = i * 2;
                rv[k++] = (byte)((str[j + 1] & 0xf) + ((str[j] & 0xf) * 16));
            }
            return rv;
        }

        private IcomIhandler rigHandler;
        private void setupResponseActions()
        {
            Tracing.TraceLine("setupResponseActions", TraceLevel.Info);
            Collection<IcomIhandler.ResponseItem> items = new Collection<IcomIhandler.ResponseItem>();
            items.Add(new IcomIhandler.ResponseItem(ICReadXCVRID, "ICReadXCVRID", null));
            items.Add(new IcomIhandler.ResponseItem(ICRXTX, "ICRXTX", contRXTX));
            items.Add(new IcomIhandler.ResponseItem(ICReadOpMode, "ICReadOpMode", contOpMode));
            items.Add(new IcomIhandler.ResponseItem(ICSendOpMode, "ICSendOpMode", contOpMode));
            items.Add(new IcomIhandler.ResponseItem(ICSendOpModeXCV, "ICSendOpModeXCV", contOpMode));
            items.Add(new IcomIhandler.ResponseItem(ICReadSplit, "ICReadSplit", contSplitDup));
            items.Add(new IcomIhandler.ResponseItem(ICReadOpFreq, "ICReadOpFreq", contOpFreq));
            items.Add(new IcomIhandler.ResponseItem(ICSendOpFreq, "ICSendOpFreq", contOpFreq));
            items.Add(new IcomIhandler.ResponseItem(ICSendFreqXCV, "ICSendFreqXCV", contOpFreq));
            items.Add(new IcomIhandler.ResponseItem(ICSMeter, "ICSMeter", contSMeter));
            items.Add(new IcomIhandler.ResponseItem(ICRFPower, "ICRFPower", contPower));
            items.Add(new IcomIhandler.ResponseItem(ICRFMeter, "ICRFMeter", contRFMeter));
            items.Add(new IcomIhandler.ResponseItem(ICVFO, "ICVFO", null));
            items.Add(new IcomIhandler.ResponseItem(ICTuner, "ICTuner", contTuner));
            items.Add(new IcomIhandler.ResponseItem(ICAFGain, "ICAFGain", contLineout));
            items.Add(new IcomIhandler.ResponseItem(ICRFGain, "ICRFGain", contAudio));
            items.Add(new IcomIhandler.ResponseItem(ICBrkin, "ICBrkin", contBreakin));
            items.Add(new IcomIhandler.ResponseItem(ICBrkinDelay, "ICBrkinDelay", contBreakinDelay));
            items.Add(new IcomIhandler.ResponseItem(ICVox, "ICVox", contVox));
            items.Add(new IcomIhandler.ResponseItem(ICFilterWidth, "ICFilterWidth", contFilterWidth));
            items.Add(new IcomIhandler.ResponseItem(ICDSPFilterType, "ICDSPFilterType", contDSPFilterType));
            items.Add(new IcomIhandler.ResponseItem(ICKeyerSpeed, "ICKeyerSpeed", contKeyerSpeed));
            items.Add(new IcomIhandler.ResponseItem(ICCWPitch, "ICCWPitch", contCWPitch));
            items.Add(new IcomIhandler.ResponseItem(ICKeyer, "ICKeyer", contKeyer));
            items.Add(new IcomIhandler.ResponseItem(ICSidetoneGain, "ICSidetoneGain", contSidetoneGain));
            items.Add(new IcomIhandler.ResponseItem(ICFirstIFFilter, "ICFirstIFFilter", contFirstIF));
            items.Add(new IcomIhandler.ResponseItem(ICInnerPBT, "ICInnerPBT", contInnerPBT));
            items.Add(new IcomIhandler.ResponseItem(ICOuterPBT, "ICOuterPBT", contOuterPBT));
            items.Add(new IcomIhandler.ResponseItem(ICVoxGain, "ICVoxGain", contVoxGain));
            items.Add(new IcomIhandler.ResponseItem(ICAntiVoxGain, "ICAntiVoxGain", contAntiVox));
            items.Add(new IcomIhandler.ResponseItem(ICVoxDelay, "ICVoxDelay", contVoxDelay));
            items.Add(new IcomIhandler.ResponseItem(ICMicGain, "ICMicGain", contMicGain));
            items.Add(new IcomIhandler.ResponseItem(ICComp, "ICComp", contComp));
            items.Add(new IcomIhandler.ResponseItem(ICCompLevel, "ICCompLevel", contCompLevel));
            items.Add(new IcomIhandler.ResponseItem(ICSSBTransmitBandwidth, "ICSSBTransmitBandwidth", contSSBTransmitBandwidth));
            items.Add(new IcomIhandler.ResponseItem(ICTXBandwidthWide, "ICTXBandwidthWide", contTXBandwidthWide));
            items.Add(new IcomIhandler.ResponseItem(ICTXBandwidthMid, "ICTXBandwidthMid", contTXBandwidthMid));
            items.Add(new IcomIhandler.ResponseItem(ICTXBandwidthNarrow, "ICTXBandwidthNarrow", contTXBandwidthNarrow));
            items.Add(new IcomIhandler.ResponseItem(ICNR, "ICNR", contNR));
            items.Add(new IcomIhandler.ResponseItem(ICNRLevel, "ICNRLevel", contNRLevel));
            items.Add(new IcomIhandler.ResponseItem(ICAGC, "ICAGC", contAGC));
            items.Add(new IcomIhandler.ResponseItem(ICAGCtc, "ICAGCtc", contAGCtc));
            items.Add(new IcomIhandler.ResponseItem(ICNB, "ICNB", contNB));
            items.Add(new IcomIhandler.ResponseItem(ICNBLevel, "ICNBLevel", contNBLevel));
            items.Add(new IcomIhandler.ResponseItem(ICNBDepthB80_6, "ICNBDepthB80_6", contNBDepthB80_6));
            items.Add(new IcomIhandler.ResponseItem(ICNBDepthB2, "ICNBDepthB2", contNBDepthB2));
            items.Add(new IcomIhandler.ResponseItem(ICNBDepthB440, "ICNBDepthB440", contNBDepthB440));
            items.Add(new IcomIhandler.ResponseItem(ICNBDepthB1200, "ICNBDepthB1200", contNBDepthB1200));
            items.Add(new IcomIhandler.ResponseItem(ICNBWidthB80_6, "ICNBWidthB80_6", contNBWidthB80_6));
            items.Add(new IcomIhandler.ResponseItem(ICNBWidthB2, "ICNBWidthB2", contNBWidthB2));
            items.Add(new IcomIhandler.ResponseItem(ICNBWidthB440, "ICNBWidthB440", contNBWidthB440));
            items.Add(new IcomIhandler.ResponseItem(ICNBWidthB1200, "ICNBWidthB1200", contNBWidthB1200));
            items.Add(new IcomIhandler.ResponseItem(ICAutoNotch, "ICAutoNotch", contAutoNotch));
            items.Add(new IcomIhandler.ResponseItem(ICManualNotch, "ICManualNotch", contManualNotch));
            items.Add(new IcomIhandler.ResponseItem(ICNotchPosition, "ICNotchPosition", contNotchPosition));
            items.Add(new IcomIhandler.ResponseItem(ICNotchWidth, "ICNotchWidth", contNotchWidth));
            items.Add(new IcomIhandler.ResponseItem(ICSubBand, "ICSubBand", contSubBand));
            items.Add(new IcomIhandler.ResponseItem(ICVoiceDelay, "ICVoiceDelay", contVoiceDelay));
            items.Add(new IcomIhandler.ResponseItem(ICMon, "ICMon", contMon));
            items.Add(new IcomIhandler.ResponseItem(ICMonLevel, "ICMonLevel", contMonLevel));
            items.Add(new IcomIhandler.ResponseItem(ICDupOffset, "ICDupOffset", contDupOffset));
            items.Add(new IcomIhandler.ResponseItem(ICTone, "ICTone", contTone));
            items.Add(new IcomIhandler.ResponseItem(ICToneSquelch, "ICToneSquelch", contToneSquelch));
            items.Add(new IcomIhandler.ResponseItem(ICReadToneFreq, "ICReadToneFreq", contReadToneFreq));
            items.Add(new IcomIhandler.ResponseItem(ICReadToneSquelchFreq, "ICReadToneSquelchFreq", contReadToneSquelchFreq));
            items.Add(new IcomIhandler.ResponseItem(ICMemContents, "ICMemContents", contMemContents));
            items.Add(new IcomIhandler.ResponseItem(ICSelectMemMode, "ICSelectMemMode", null));
            items.Add(new IcomIhandler.ResponseItem(ICMemWrite, "ICMemWrite", null));
            items.Add(new IcomIhandler.ResponseItem(ICMemCopy2VFO, "ICMemCopy2VFO", null));
            items.Add(new IcomIhandler.ResponseItem(ICReadAttenuator, "ICReadAttenuator", contReadAttenuator));
            items.Add(new IcomIhandler.ResponseItem(ICPreAmp, "ICPreamp", contPreamp));
            items.Add(new IcomIhandler.ResponseItem(ICSWRMeter, "ICSWRMeter", contSWRMeter));
            items.Add(new IcomIhandler.ResponseItem(ICALCMeter, "ICALCMeter", contALCMeter));
            items.Add(new IcomIhandler.ResponseItem(ICCompMeter, "ICCompMeter", contCompMeter));
            items.Add(new IcomIhandler.ResponseItem(ICAFC, "ICAFC", contAFC));
            items.Add(new IcomIhandler.ResponseItem(ICAFCLimit, "ICAFCLimit", contAFCLimit));
            items.Add(new IcomIhandler.ResponseItem(ICXmitMonitor, "ICXmitMonitor", contXmitMonitor));
            items.Add(new IcomIhandler.ResponseItem(ICTuningStep, "ICTuningStep", contTuningStep));

            // Finally, setup the handler.
            CommandHDRLen = CommandHDR.Length;
            rigHandler = new IcomIhandler((AllRadios)this, items.ToArray(),
                CommandHDR, CommandTerm);
        }

        public Icom()
        {
            Tracing.TraceLine("Icom constructor", TraceLevel.Info);
            ModeTable = myModeTable;
            vfoItems = new vfoData[3];
            vfoItems[GetVfoID(RigCaps.VFOs.VFOA)] = new vfoData(0, myModeTable[(int)modes.none], new FilterData(0, 0));
            vfoItems[GetVfoID(RigCaps.VFOs.VFOB)] = new vfoData(0, myModeTable[(int)modes.none], new FilterData(0, 0));
            vfoItems[GetVfoID(RigCaps.VFOs.None)] = new vfoData(0, myModeTable[(int)modes.none], new FilterData(0, 0));
        }

        private int saveHeartbeat;
        public override bool Open(OpenParms p)
        {
            Tracing.TraceLine("Icom Open", TraceLevel.Info);
            // the com port should be open.
            bool rv = base.Open(p);
            if (rv)
            {
                p.ReackquireRig = Reackquire;
                // Readjust the heartbeat time, restore on close.
                saveHeartbeat = _HeartbeatInterval;
                _HeartbeatInterval = defaultConfirmTime + 1000;
                // Start the radio output processor.
                try
                {
                    setupResponseActions();
                    rigHandler.Start();
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("Icom open:" + ex.Message, TraceLevel.Error);
                    return false;
                }

                // Start the command thread.
                commandQ = Queue.Synchronized(new Queue());
                commandThread = new Thread(new ThreadStart(commandProc));
                commandThread.Name = "commandThread";
                commandThread.Start();
            }
            return rv;
        }

        public override void close()
        {
            Tracing.TraceLine("Icom close", TraceLevel.Info);
            try
            {
                if ((commandThread != null) && commandThread.IsAlive) commandThread.Abort();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Icom close commandThread" + ex.Message, TraceLevel.Error);
            }

            RigWatchTimerStop();

            try
            {
                if ((memThread != null) && memThread.IsAlive) memThread.Abort();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Icom close memThread exception:" + ex.Message, TraceLevel.Error);
            }
            try
            {
                if ((statThread != null) && statThread.IsAlive) statThread.Abort();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Icom close statThread exception:" + ex.Message, TraceLevel.Error);
            }
            try
            {
                rigHandler.Stop();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Icom close rigHandler exception:" + ex.Message, TraceLevel.Error);
            }

            _HeartbeatInterval = saveHeartbeat;
            base.close(); // resets IsOpen.
        }

        // Region - memory stuff
        #region memoryStuff
        // Note Icom rigs currently have no reserved (default) memory groups.
        private const int MemLength = 107;
        /// <summary>
        /// Internal memory type
        /// This is used to insert or extract fields from an Icom memory.
        /// </summary>
        internal class MemoryType
        {
            private Icom parent;
            public byte[] Mem;
            public int Group
            {
                get { return (int)Mem[0]; }
                set
                {
                    Mem[0] = (byte)value;
                }
            }
            public int Number
            {
                get { return BCDToInt(Mem[1], Mem[2]); }
                set
                {
                    byte[] b = IntToBCD(value);
                    Array.ConstrainedCopy(b, 0, Mem, 1, 2);
                }
            }
            public MemoryTypes Type
            {
                get
                {
                    MemoryTypes t;
                    if (Number <= UserMemories) t = MemoryTypes.Normal;
                    else if (Number == 106) t = MemoryTypes.CallChannel;
                    else t = MemoryTypes.Special;
                    return t;
                }
            }
            public bool Present
            {
                get { return (Mem[3] != 0xff); }
                set
                {
                    Mem[3] = (byte)((value) ? 0 : 0xff);
                }
            }
            public bool Split
            {
                get { return Present && ((Mem[3] & 0x10) == 0x10); }
                set
                {
                    if (Present) Mem[3] = (byte)((value) ? 1 : 0);
                }
            }
            public ulong RXFrequency
            {
                get { return IFreqToFreq(Mem, 4); }
                set
                {
                    byte[] b = FreqToIFreq(value);
                    Array.ConstrainedCopy(b, 0, Mem, 4, 5);
                }
            }
            // The set also includes the filter.
            public ModeValue RXMode
            {
                get { return parent.getMode(Mem[9]); }
                set
                {
                    Mem[9] = parent.IMode(value);
                }
            }
            public int Filter
            {
                get { return (int)Mem[10]; }
                set
                {
                    Mem[10] = (byte)value;
                }
            }
            public OffsetDirections OffsetDirection
            {
                get
                {
                    OffsetDirections rv = OffsetDirections.off;
                    switch (Mem[12] & 0xf0)
                    {
                        case 0x10: rv = OffsetDirections.minus; break;
                        case 0x20: rv = OffsetDirections.plus; break;
                    }
                    return rv;
                }
            }
            public ToneCTCSSValue ToneCTCSS
            {
                get
                {
                    char t = (char)(Mem[12] & 0xf);
                    return new ToneCTCSSValue(t, ((ToneTypes)t).ToString());
                }
            }
            internal void setOffsetDirAndToneMode(OffsetDirections d, ToneTypes t)
            {
                byte bd = 0x00; // symplex
                if (d == OffsetDirections.minus) bd = 0x10;
                else if (d == OffsetDirections.plus) bd = 0x20;
                Mem[12] = (byte)(bd + (int)t);
            }
            public float ToneFrequency
            {
                get { return BCDToInt(Mem[15], Mem[16]) / 10; }
            }
            public float TCSFrequency
            {
                get { return BCDToInt(Mem[18], Mem[19]) / 10; }
            }
            // We'll set both Tone and CTCSS to the same value.
            internal void SetToneOrTCSFrequency(float freq)
            {
                byte[] b = IntToBCD((int)(freq * 10));
                Array.ConstrainedCopy(b, 0, Mem, 15, 2);
                Array.ConstrainedCopy(b, 0, Mem, 18, 2);
            }
            internal ushort DTCS
            {
                get { return (ushort)BCDToInt(Mem[21], Mem[22]); }
                set
                {
                    byte[] b = IntToBCD((int)value);
                    Array.ConstrainedCopy(b, 0, Mem, 21, 2);
                }
            }
            public int Offset
            {
                get { return IOffsetToOffset(Mem, 24); }
                set
                {
                    byte[] b = OffsetToIOffset(value);
                    Array.ConstrainedCopy(b, 0, Mem, 24, 3);
                }
            }
            // Unused text, 3 * 8 bytes
            private static byte[] unusedText =
            {
                0x43, 0x51, 0x43, 0x51, 0x43, 0x51, 0x20, 0x20,
                0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20,
                0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20,
            };
            public void setUnusedText()
            {
                Array.ConstrainedCopy(unusedText, 0, Mem, 27, unusedText.Length);
            }
            // Second half of memory.
            public ulong TXFrequency
            {
                get { return IFreqToFreq(Mem, 51); }
                set
                {
                    byte[] b = FreqToIFreq(value);
                    Array.ConstrainedCopy(b, 0, Mem, 51, 5);
                }
            }
            public ModeValue TXMode
            {
                get { return parent.getMode(Mem[56]); }
                set
                {
                    byte[] b = new byte[2] { parent.IMode(value), (byte)parent.Filter };
                    Array.ConstrainedCopy(b, 0, Mem, 56, 2);
                }
            }

            // Copy first half to second half of memory.
            // Do this before setting the TX values.
            internal void FirstHalfToSecondHalf()
            {
                Array.ConstrainedCopy(Mem, 4, Mem, 51, 47);
            }

            public string Name
            {
                get
                {
                    StringBuilder sb = new StringBuilder(9);
                    for (int i = MemLength - 9; i < MemLength; i++) sb.Append((char)Mem[i]);
                    return sb.ToString().Trim();
                }
                set
                {
                    int len = Math.Min(9, value.Length);
                    int j = 0;
                    for (int i = MemLength - 9; i < MemLength; i++)
                    {
                        Mem[i] = (byte)((j < len) ? value[j++] : ' ');
                    }
                }
            }

            // This doesn't make a copy of the bytes.
            public MemoryType(byte[] m, Icom p)
            {
                Mem = m;
                parent = p;
            }
            // This copies the bytes.
            public MemoryType(byte[] m, int pos, int len, Icom p) 
            {
                Mem = new byte[MemLength];
                len = Math.Min(MemLength, len);
                Array.ConstrainedCopy(m, pos, Mem, 0, len);
                parent = p;
            }
        }
        internal const int TotalMemories = 106;
        internal const int UserMemories = 99;

        internal override string DisplayMemName(MemoryData mem)
        {
            string rv = "";
            if (mem.Present)
            {
                if (!string.IsNullOrEmpty(mem.Name) && (mem.Name != " "))
                {
                    rv = mem.Name + ' ';
                }
                rv += Callouts.FormatFreq(mem.Frequency[0]);
            }
            else
            {
                rv = AllRadios.Empty;
            }
            return rv + ' ' + mem.Number.ToString("d3");
        }

        private Thread memThread = null;
        protected void ICGetMemories(bool initial)
        {
            Tracing.TraceLine("ICGetMemories:" + initial.ToString(), TraceLevel.Info);
#if GetMemoriez
            if (!initial) return;

            try
            {
                if ((memThread != null) && memThread.IsAlive)
                {
                    Tracing.TraceLine("ICGetMemories aborting memThread",TraceLevel.Info);
                    memThread.Abort();
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("ICGetMemories memThread exception:"+ex.Message,TraceLevel.Error);
            }

            memThread = new Thread(CollectMemories);
            memThread.Name = "memThread";
            
            try { memThread.Start(initial); }
            catch (Exception ex)
            { Tracing.TraceLine("ICGetMemories:" + ex.Message, TraceLevel.Error); }
            Thread.Sleep(0);
#endif
        }
        protected bool SuspendMemCollection = false;
        protected virtual void CollectMemories(object o)
        {
            bool initial = (bool)o;
            try
            {
                Tracing.TraceLine("collectMem:started:" + initial.ToString() + ' ' + TotalMemories.ToString(), TraceLevel.Info);

                if (initial)
                {
                    MemoriesLoaded = false;
                    raiseComplete(CompleteEvents.memoriesStart);

                    Memories = new MemoryGroup(TotalMemories, this, 1);
                    for (int i = 0; i < TotalMemories; i++)
                    {
                        // See if suspended.
                        while (SuspendMemCollection) Thread.Sleep(1000);

                        SendCommand(BldMemCmd(0, i + 1));
                        commandSync(defaultConfirmTime);
                    }
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

        internal override bool getMem(MemoryData m) { return true; }

        /// <summary>
        /// Setup and write the current MemoryData object.
        /// It is set from the rig's current settings.
        /// To delete a memory, use MemDelete().
        /// If named, the name must be set.
        /// </summary>
        /// <param name="md">the MemoryData object</param>
        internal void VFOToMem(MemoryData md)
        {
            MemoryType m = new MemoryType(md.RawMem, this);
            m.Present = true;
            md.Present = true;
            m.Split = Split;
            md.Split = Split;
            m.RXFrequency = RXFrequency;
            md.Frequency[0] = RXFrequency;
            m.RXMode = RXMode;
            md.Mode[0] = RXMode;
            m.Filter = Filter;
            md.Filter = Filter;
            m.setOffsetDirAndToneMode(OffsetDirection, ToneMode);
            md.OffsetDirection = OffsetDirection;
            md.ToneCTCSS = new ToneCTCSSValue((char)ToneMode, ToneMode.ToString());
            // We need to default the tone frequency if not set.
            // We also set both tone and CTCSS frequencies to the same value.
            if (ToneFrequency == 0) ToneFrequency = ToneFrequencyTable[0];
            m.SetToneOrTCSFrequency(ToneFrequency);
            md.ToneFrequency = ToneFrequency;
            md.CTSSFrequency = ToneFrequency;
            md.ToneFrequency = m.ToneFrequency;
            md.CTSSFrequency = m.TCSFrequency;
            m.DTCS = DCSTable[0]; // default this
            md.DCS = m.DTCS;
            m.Offset = OffsetFrequency;
            md.OffsetFrequency = OffsetFrequency;
            m.setUnusedText();

            m.FirstHalfToSecondHalf();
            if (md.Split)
            {
                m.TXFrequency = TXFrequency;
                md.Frequency[1] = TXFrequency;
                m.TXMode = TXMode;
                md.Mode[1] = TXMode;
            }
            m.Name = md.Name;
            SendCommand(BldCmd(ICMemContents, m.Mem));
        }

        public override int CurrentMemoryChannel
        {
            get { return base.CurrentMemoryChannel; }
            set
            {
                CurrentMemoryChannel = value;
                if (MemoryMode)
                {
                    gotoMemory(CurrentMemoryNumber);
                }
            }
        }

        public override int CurrentMemoryNumber
        {
            get { return CurrentMemoryChannel + 1; }
        }

        // Called if 'V' pressed from memory field in main box.
        // We must be in memory mode when called.
        public override bool MemoryToVFO(int n, RigCaps.VFOs vfo)
        {
            Tracing.TraceLine("MemoryToVFO:" + n.ToString() + " " + vfo.ToString(), TraceLevel.Info);
            if (Transmit | !MemoryMode)
            {
                Tracing.TraceLine("MemoryToVFO:invalid", TraceLevel.Error);
                return false;
            }
            vfo = oldVFO;
            IcomCommand cmd = VFOCommand(vfo);
            if (cmd == null) return false;
            _RXVFO = vfo;
            SendCommand(BldCmd(cmd));
            Split = Memories.mems[n].Split;
            SendCommand(BldCmd(ICMemCopy2VFO));
            SendCommand(BldCmd(ICReadOpFreq));
            SendCommand(BldCmd(ICReadOpMode));
            getFilterInfo();
            return true;
        }

        public override bool MemoryMode
        {
            get { return base.MemoryMode; }
            set
            {
                if (value)
                {
                    if (!MemoriesLoaded)
                    {
                        Tracing.TraceLine("Memories not loaded", TraceLevel.Error);
                        return;
                    }
                    if (!MemoryMode)
                    {
                        oldVFO = CurVFO;
                        CurVFO = RigCaps.VFOs.None;
                        gotoMemory(CurrentMemoryNumber);
                    }
                    // else already in memory mode.
                }
                else
                {
                    if (MemoryMode)
                    {
                        RXVFO = oldVFO;
                    }
                    // else already using a VFO.
                }
            }
        }

        /// <summary>
        /// Goto a memory
        /// </summary>
        /// <param name="num">memory number, not the index</param>
        protected void gotoMemory(int num)
        {
            byte[] b = IntToBCD(num);
            SendCommand(BldCmd(ICSelectMemMode, b));
            SendCommand(BldCmd(ICSelectMemMode));
            SendCommand(BldCmd(ICReadOpFreq));
            SendCommand(BldCmd(ICReadOpMode));
            commandSync(defaultConfirmTime);
            reflectMemoryData(Memories.mems[CurrentMemoryChannel]);
        }

        protected override void actuateRigSpecific(MemoryData m)
        {
            _ToneMode = (ToneTypes)m.ToneCTCSS.value;
            _ToneFrequency = (_ToneMode == ToneTypes.tcs) ? m.CTSSFrequency : m.ToneFrequency;
            Split = m.Split;
        }

        /// <summary>
        /// go to the memory selected in the memories dialogue, see ic9100Memories.cs.
        /// </summary>
        /// <param name="m">the MemoryData</param>
        internal bool MemToVFO(MemoryData m)
        {
            if (!m.Present) return false;
            RigWatchTimerStop();
            RigCaps.VFOs theVFO = (MemoryMode) ? oldVFO : CurVFO;
            byte[] b = IntToBCD(m.Number);
            _RXVFO = theVFO; // put into VFO mode
            SendCommand(BldCmd(VFOCommand(_RXVFO)));
            RXFrequency = m.Frequency[0]; // Ensure we're in the right range.
            SendCommand(BldCmd(ICSelectMemMode, b)); // Selects the memory.
            SendCommand(BldCmd(ICMemCopy2VFO));
            commandSync(defaultConfirmTime);
            _hfvhf = (m.Frequency[0] < 100000000) ? hfvhfValues.hf : hfvhfValues.vhf;
            Split = m.Split;
            CurrentMemoryChannel = m.Number - 1;
            SendCommand(BldCmd(ICReadOpFreq));
            SendCommand(BldCmd(ICReadOpMode));
            getFilterInfo();
            RigWatchTimerStart();
            return true;
        }

        internal void MemDelete(MemoryData m)
        {
            m.Present = false;
            m.RawMem[3] = 0xff;
            int len = ICMemContents.Command.Length;
            byte[] b = new byte[len + 4];
            Array.Copy(ICMemContents.Command, b, len);
            Array.ConstrainedCopy(m.RawMem, 0, b, len, 4);
            SendCommand(BldCmd(b));
        }
        #endregion

        // region - CW sending
        #region
        private static IcomCommand ICSendCWMsgChar = new IcomCommand(0x17, 0x00); // Send 1 cw character
        public override bool SendCW(char c)
        {
            int len = ICSendCWMsgs.Command.Length;
            ICSendCWMsgChar.Command[len] = (byte)c;
            SendCommand(BldCmd(ICSendCWMsgChar));
            return true;
        }
        #endregion

        // region - output handlers
        #region output
        protected virtual void contRXTX(byte[] cmd, int pos, int len)
        {
            RXTXRslt = (cmd[pos + len - 1] == 1) ? true : false;
            RXTXRcvd = true;
        }

        // For stuff needed on a per VFO basis
        private class vfoData
        {
            public ulong Frequency;
            public ModeValue Mode;
            public FilterData FilterItem;
            public vfoData() { }
            public vfoData(ulong freq, ModeValue m, FilterData f)
            {
                Frequency = freq;
                Mode = m;
                FilterItem = f;
            }
            // This makes a copy
            public vfoData(vfoData v)
            {
                Frequency = v.Frequency;
                Mode = v.Mode;
                FilterItem = new FilterData(v.FilterItem.Filter, v.FilterItem.Width);
            }
        }
        private vfoData[] vfoItems;
        internal int GetVfoID(RigCaps.VFOs vfo)
        {
            return (IsMemoryMode(vfo)) ? 2 : (int)vfo;
        }

        protected virtual void contSplitDup(byte[] cmd, int pos, int len)
        {
            bool s = false;
            OffsetDirections o = OffsetDirections.off;
            switch (cmd[len - 1])
            {
                case 0x01: s = true; break;
                case 0x11: o = OffsetDirections.minus; break;
                case 0x12: o = OffsetDirections.plus; break;
            }
            _Split = s;
            _OffsetDirection = o;
        }

        protected virtual void contOpMode(byte[] cmd, int pos, int len)
        {
            vfoItems[GetVfoID(CurVFO)].FilterItem.Filter = cmd[len - 1]; // last byte is the filter #
            ModeValue m = getMode(cmd[len - 2]);
            if ((m != myModeTable[(int)modes.fm]) &&
                (OffsetDirection != OffsetDirections.off))
            {
                SendCommand(BldCmd(ICSymplex));
                if (_Split) Split = true;
            }
            vfoItems[GetVfoID(CurVFO)].Mode = m;
        }

        protected ulong freqJustRetrieved;
        protected virtual void contOpFreq(byte[] cmd, int pos, int len)
        {
            freqJustRetrieved = IFreqToFreq(cmd, pos + CommandHDRLen + 1);
            vfoItems[GetVfoID(CurVFO)].Frequency = freqJustRetrieved;
            if (CurVFO == TXVFO) _TXFrequency = freqJustRetrieved;
            if (CurVFO == RXVFO) _RXFrequency = freqJustRetrieved;
        }

        protected virtual void contDupOffset(byte[] cmd, int pos, int len)
        {
            _OffsetFrequency = IOffsetToOffset(cmd, len - 3);
        }

        protected virtual void contSMeter(byte[] cmd, int pos, int len)
        {
            // Only called if not transmitting.
            // 0000 to 0255 S-meter level (0000=S0,0120=S9,0240=S9+60dB)  
            int val = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            if (val > 240) val = 240;
            val /= 2;
            // now 0-120
            if (val < 60) val /= 6; // s0-9
            else if (val >= 60)
            {
                val -= 60;
                val += 9; // S9 +
            }
            _SMeter = val;
            TransmitStatus = false;
        }

            protected virtual void contPower(byte[] cmd, int pos, int len)
        {
            // 0000 to 0255 RF POWER position (0000=max.CCWto0255=max.CW)
            int v = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            _XmitPower = RigRangeConvert(v, 0, MyPower);
        }

        private class RFMeterData
        {
            public int PC;
            public int RigOutput;
            public RFMeterData(int p, int r)
            {
                PC = p;
                RigOutput = r;
            }
        }
        private static RFMeterData[] RFMeterTable =
        {
            new RFMeterData(05, 0028),
            new RFMeterData(10, 0055),
            new RFMeterData(20, 0076),
            new RFMeterData(30, 0098),
            new RFMeterData(40, 0121),
            new RFMeterData(50, 0141),
            new RFMeterData(60, 0157),
            new RFMeterData(70, 0171),
            new RFMeterData(80, 0192),
            new RFMeterData(90, 0217),
            new RFMeterData(100, 0237),
        };
        private int _ICPower;
        protected virtual void contRFMeter(byte[] cmd, int pos, int len)
        {
            // Only called if transmitting.
            // 0000 to 0255 RF power meter (0000=0%,0141=50%,0215=100%)  
            int v = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            // get lowest >= v.
            int id = -1;
            for (int i = 0; i < RFMeterTable.Length; i++)
            {
                if (RFMeterTable[i].RigOutput >= v)
                {
                    id = i;
                    break;
                }
            }
            if (id == -1) id = RFMeterTable.Length - 1; // use last one.
            // Get the low values from the previous entry.
            int lowPC = (id == 0) ? 0 : RFMeterTable[id - 1].PC;
            int lowRig = (id == -1)?0:RFMeterTable[id - 1].RigOutput;
            // Get the interpolated percentage.
            int pc = RangeConvert(v, lowRig, RFMeterTable[id].RigOutput, lowPC, RFMeterTable[id].PC);
            // Set the power value.
            _ICPower = (int)(((float)MyPower / 100) * pc);
            // indicate transmitting
            TransmitStatus = true;
        }

        protected virtual void contTuner(byte[] cmd, int pos, int len)
        {
            switch (cmd[len - 1])
            {
                case 0: _AntennaTuner = (AntTunerVals)0; break;
                case 1: _AntennaTuner = (AntTunerVals.rx | AntTunerVals.tx); break;
                case 2: _AntennaTuner = (AntTunerVals.rx | AntTunerVals.tx | AntTunerVals.tune); break;
            }
        }

        // Really the audio gain
        protected virtual void contLineout(byte[] cmd, int pos, int len)
        {
            _LineoutGain = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
        }

        // Really the RF gain
        protected virtual void contAudio(byte[] cmd, int pos, int len)
        {
            _AudioGain = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
        }

        protected virtual void contVox(byte[] cmd, int pos, int len)
        {
            _myVox = (cmd[len - 1] != 0) ? true : false;
        }

        protected virtual void contBreakin(byte[] cmd, int pos, int len)
        {
            _myCWVox = (cmd[len - 1] != 0) ? true : false;
        }

        protected virtual void contBreakinDelay(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            // Convert to MS, see ICBreakinDelay.
            _BreakinDelay = RigRangeConvert(val, 200, 1300);
        }

        protected virtual void contKeyer(byte[] cmd, int pos, int len)
        {
            _Keyer = (KeyerValues)cmd[len - 1];
        }

        protected virtual void contFilterWidth(byte[] cmd, int pos, int len)
        {
            // See ICFilterWidth
            int val = (int)cmd[len - 1];
            // First, convert from hexadufus.
            val = (val & 0xf) + ((val / 16) * 10);
            int val2 = getFilterWidth(val);
            if (val2 == -1) Tracing.TraceLine("contFilterWidth:bad value " + val.ToString(), TraceLevel.Error);
            else vfoItems[GetVfoID(CurVFO)].FilterItem.Width = val2;
        }

        internal int getFilterWidth(int val)
        {
            return getFilterWidth(val, Mode);
        }
        /// <summary>
        /// Get filter width values.
        /// </summary>
        /// <param name="val">integer value from 00 to 49.</param>
        /// <param name="mode">the mode</param>
        /// <returns>filter width</returns>
        /// <remarks>Pass integers starting at 0, and go until the value = -1.</remarks>
        internal int getFilterWidth(int val, ModeValue mode)
        {
            int rv = 0;
            if (mode == myModeTable[(int)modes.am])
            {
                if (val > 49) rv = -1;
                else rv = (val * 200) + 200;
            }
            else
            {
                if ((mode == myModeTable[(int)modes.fsk]) && (val > 31)) rv = -1;
                else if (val > 40) rv = -1;
                if (rv != -1) rv = (val < 10) ? (val + 1) * 50 : (val * 100) - 400;
            }
            return rv;
        }

        protected virtual void contDSPFilterType(byte[] cmd, int pos, int len)
        {
            _FilterType = (cmd[len - 1] == 1) ? FilterTypes.soft : FilterTypes.sharp;
        }

        protected virtual void contKeyerSpeed(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            _KeyerSpeed = RigRangeConvert(val, KeyerSpeedMin, KeyerSpeedMax);
        }

        protected virtual void contCWPitch(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            _CWPitch = RigRangeConvert(val, CWPitchMin, CWPitchMax);
        }

        protected virtual void contSidetoneGain(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            _SidetoneGain = RigRangeConvert(val, SidetoneGainMin, SidetoneGainMax);
        }

        protected virtual void contFirstIF(byte[] cmd, int pos, int len)
        {
            _FirstIF = firstIFSizes[(int)cmd[len - 1]];
        }

        protected virtual void contInnerPBT(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            _InnerPBT = RigRangeConvert(val, InnerPBTMin, InnerPBTMax);
        }

        protected virtual void contOuterPBT(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            _OuterPBT = RigRangeConvert(val, InnerPBTMin, InnerPBTMax);
        }

        protected virtual void contVoxGain(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            _VoxGain = RigRangeConvert(val, pcMin, pcMax);
        }

        protected virtual void contAntiVox(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            _AntiVox = RigRangeConvert(val, pcMin, pcMax);
        }

        protected virtual void contVoxDelay(byte[] cmd, int pos, int len)
        {
            int val = (int)cmd[len - 1];
            _VoxDelay = RangeConvert(val, 0, 20, pcMin, pcMax);
        }

        protected virtual void contMicGain(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            _MicGain = RigRangeConvert(val, pcMin, pcMax);
        }

        protected virtual void contComp(byte[] cmd, int pos, int len)
        {
            _Comp = (cmd[len - 1] == 0) ? OffOnValues.off : OffOnValues.on;
        }

        protected virtual void contCompLevel(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            _CompLevel = RigRangeConvert(val, CompLevelMin, CompLevelMax);
        }

        protected virtual void contSSBTransmitBandwidth(byte[] cmd, int pos, int len)
        {
            _SSBTransmitBandwidth = (SSBTransmitBandwidthValues)cmd[len - 1];
        }

        protected virtual void contNR(byte[] cmd, int pos, int len)
        {
            _NR = (cmd[len - 1] == 0) ? OffOnValues.off : OffOnValues.on;
        }

        protected virtual void contNRLevel(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            _NRLevel = RigRangeConvert(val, pcMin, pcMax);
        }

        protected virtual void contAGC(byte[] cmd, int pos, int len)
        {
            IcomAGCValues val = (IcomAGCValues)cmd[len - 1];
            // If setting is off, default to medium.
            // Note:  We won't get called if turned off programatically.
            if (val == IcomAGCValues.off) _IcomAGC = IcomAGCValues.medium;
            else _IcomAGC = val;
        }

        protected virtual void contAGCtc(byte[] cmd, int pos, int len)
        {
            _AGCtc = BCDToInt(cmd[len - 1]);
        }

        protected virtual void contNB(byte[] cmd, int pos, int len)
        {
            _NoiseBlanker = (cmd[len - 1] == 0) ? OffOnValues.off : OffOnValues.on;
        }

        protected virtual void contNBLevel(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(new byte[2] { cmd[len - 2], cmd[len - 1] });
            _NBLevel = RigRangeConvert(val, pcMin, pcMax);
        }

        private int getDepth(byte val)
        {
            return BCDToInt(val) + 1;
        }
        protected virtual void contNBDepthB80_6(byte[] cmd, int pos, int len)
        {
            _NBDepth[(int)BandRanges.b80_6] = getDepth(cmd[len - 1]);
        }
        protected virtual void contNBDepthB2(byte[] cmd, int pos, int len)
        {
            _NBDepth[(int)BandRanges.b2] = getDepth(cmd[len - 1]);
        }
        protected virtual void contNBDepthB440(byte[] cmd, int pos, int len)
        {
            _NBDepth[(int)BandRanges.b440] = getDepth(cmd[len - 1]);
        }

        /// <summary>
        /// B1200 is true if 1200 MHZ is present.
        /// </summary>
        internal bool B1200 = true;

        // This is used to see if 1200 mhz is present.
        protected bool ICNBDepthB1200Rcvd = false;
        protected virtual void contNBDepthB1200(byte[] cmd, int pos, int len)
        {
            if (len > CommandHDRLen) _NBDepth[(int)BandRanges.b1200] = getDepth(cmd[len - 1]);
            else B1200 = false;
            ICNBDepthB1200Rcvd = true;
        }

        private int getWidth(byte[] cmd, int len)
        {
            int val = BCDToInt(cmd[len - 2], cmd[len - 1]);
            return RoundInt(RigRangeConvert(val, NBWidthMin, NBWidthMax), NBWidthIncrement);
        }
        protected virtual void contNBWidthB80_6(byte[] cmd, int pos, int len)
        {
            _NBWidth[(int)BandRanges.b80_6] = getWidth(cmd, len);
        }
        protected virtual void contNBWidthB2(byte[] cmd, int pos, int len)
        {
            _NBWidth[(int)BandRanges.b2] = getWidth(cmd, len);
        }
        protected virtual void contNBWidthB440(byte[] cmd, int pos, int len)
        {
            _NBWidth[(int)BandRanges.b440] = getWidth(cmd, len);
        }
        protected virtual void contNBWidthB1200(byte[] cmd, int pos, int len)
        {
            _NBWidth[(int)BandRanges.b1200] = getWidth(cmd, len);
        }

        protected virtual void contAutoNotch(byte[] cmd, int pos, int len)
        {
            // Set the notch enumeration if auto is on, or this turns it on.
            bool val = (cmd[len - 1] == 1);
            if (val | (_Notch == NotchValues.auto))
            {
                _Notch = (val) ? NotchValues.auto : NotchValues.off;
            }
        }

        protected virtual void contManualNotch(byte[] cmd, int pos, int len)
        {
            // Set the notch enumeration if manual is on, or this turns it on.
            bool val = (cmd[len - 1] == 1);
            if (val | (_Notch == NotchValues.manual))
            {
                _Notch = (val) ? NotchValues.manual : NotchValues.off;
            }
        }

        protected virtual void contNotchPosition(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(cmd[len - 2], cmd[len - 1]);
            _NotchPosition = RigRangeConvert(val, NotchPositionMin, NotchPositionMax);
        }

        protected virtual void contNotchWidth(byte[] cmd, int pos, int len)
        {
            _NotchWidth = (NotchWidthValues)cmd[len - 1];
        }

        // See IC9100.
        protected virtual void contSubBand(byte[] cmd, int pos, int len) { }

        protected virtual void contVoiceDelay(byte[] cmd, int pos, int len)
        {
            _VoiceDelay = (VoiceDelayValues)cmd[len - 1];
        }

        protected virtual void contTXBandwidthWide(byte[] cmd, int pos, int len)
        {
            _BWWidth[(int)SSBTransmitBandwidthValues.wide] = cmd[len - 1];
        }

        protected virtual void contTXBandwidthMid(byte[] cmd, int pos, int len)
        {
            _BWWidth[(int)SSBTransmitBandwidthValues.mid] = cmd[len - 1];
        }

        protected virtual void contTXBandwidthNarrow(byte[] cmd, int pos, int len)
        {
            _BWWidth[(int)SSBTransmitBandwidthValues.narrow] = cmd[len - 1];
        }

        protected virtual void contMon(byte[] cmd, int pos, int len)
        {
            _Monitor = (cmd[len - 1] == 1) ? OffOnValues.on : OffOnValues.off;
        }

        protected virtual void contMonLevel(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(cmd[len - 2], cmd[len - 1]);
            _MonitorLevel = RigRangeConvert(val, pcMin, pcMax);
        }

        protected virtual void contTone(byte[] cmd, int pos, int len)
        {
            if (cmd[len - 1] == 1) _ToneMode = ToneTypes.tone;
        }
        protected virtual void contToneSquelch(byte[] cmd, int pos, int len)
        {
            if (cmd[len - 1] == 1) _ToneMode = ToneTypes.tcs;
        }

        protected virtual void contReadToneFreq(byte[] cmd, int pos, int len)
        {
            // Only set for ToneTypes.tone
            if (_ToneMode != ToneTypes.tone) return;
            float val = (float)BCDToInt(cmd[len - 2], cmd[len - 1]);
            _ToneFrequency = val / 10;
        }
        protected virtual void contReadToneSquelchFreq(byte[] cmd, int pos, int len)
        {
            // Only set for ToneTypes.tcs
            if (_ToneMode != ToneTypes.tcs) return;
            float val = (float)BCDToInt(cmd[len - 2], cmd[len - 1]);
            _ToneFrequency = val / 10;
        }

        protected int memGroupPos { get { return CommandHDRLen + 2; } } // hdr + 0x1a00.
        protected int memNumberPos { get { return memGroupPos + 1; } } // hdr + 0x1a00 + grp.
        protected virtual void contMemContents(byte[] cmd, int pos, int len)
        {
            SettMemContents(cmd, pos, len, Memories.mems);
        }
        /// <summary>
        /// Set memory contents
        /// </summary>
        /// <param name="cmd">from the rig</param>
        /// <param name="pos">character position of response</param>
        /// <param name="len">response length</param>
        /// <param name="mg">MemoryData array to use.</param>
        protected void SettMemContents(byte[] cmd, int pos, int len, MemoryData[] mg)
        {
            int ID = BCDToInt(cmd[memNumberPos], cmd[memNumberPos + 1]) - 1; // mem no. is 1 based
            MemoryType m = new MemoryType(cmd, memGroupPos, len - memGroupPos, this);

            mg[ID].RawMem = new byte[MemLength];
            for (int i = 0; i < MemLength; i++) mg[ID].RawMem[i] = 0;
            Array.ConstrainedCopy(cmd, memGroupPos, mg[ID].RawMem, 0, len - memGroupPos);

            mg[ID].Number = m.Number;
            mg[ID].State = memoryStates.complete;
            mg[ID].Present = m.Present;
            if (m.Present)
            {
                mg[ID].Type = m.Type;
                mg[ID].Name = m.Name;
                mg[ID].Split = m.Split;
                mg[ID].Frequency[0] = m.RXFrequency;
                mg[ID].Mode[0] = m.RXMode;
                mg[ID].Filter = m.Filter;
                mg[ID].Frequency[1] = m.TXFrequency;
                mg[ID].Mode[1] = m.TXMode;
                mg[ID].OffsetDirection = m.OffsetDirection;
                mg[ID].OffsetFrequency = m.Offset;
                mg[ID].ToneCTCSS = m.ToneCTCSS;
                mg[ID].ToneFrequency = m.ToneFrequency;
                mg[ID].CTSSFrequency = m.TCSFrequency;
            }
        }

        protected virtual void contReadAttenuator(byte[] cmd, int pos, int len)
        {
            _RFAttenuator = (cmd[len - 1] == 1) ? OffOnValues.on : OffOnValues.off;
        }

        protected virtual void contPreamp(byte[] cmd, int pos, int len)
        {
            _HFPreamp = (HFPreampType)cmd[len - 1];
        }

        protected virtual void contSWRMeter(byte[] cmd, int pos, int len)
        {
            float val = (float)BCDToInt(cmd[len - 2], cmd[len - 1]);
            // See ICSWR
            val = (val / 80) + 1;
            _SWR = (float)Math.Round(val, 1);
        }

        protected virtual void contALCMeter(byte[] cmd, int pos, int len)
        {
            _ALC = BCDToInt(cmd[len - 2], cmd[len - 1]);
        }

        protected virtual void contCompMeter(byte[] cmd, int pos, int len)
        {
            int val = BCDToInt(cmd[len - 2], cmd[len - 1]);
            // see ICCompMeter.
            _CompMeter = val / 8;
        }

        protected virtual void contAFC(byte[] cmd, int pos, int len)
        {
            _AFC = (cmd[len - 1] == 1) ? OffOnValues.on : OffOnValues.off;
        }

        protected virtual void contAFCLimit(byte[] cmd, int pos, int len)
        {
            _AFCLimit = (cmd[len - 1] == 1) ? OffOnValues.on : OffOnValues.off;
        }

        protected virtual void contXmitMonitor(byte[] cmd, int pos, int len)
        {
            _XmitMonitor = (cmd[len - 1] == 1) ? OffOnValues.on : OffOnValues.off;
        }

        protected virtual void contTuningStep(byte[] cmd, int pos, int len)
        {
            int id = BCDToInt(cmd[len - 1]);
            _TuningStep = TuningStepValues[id];
        }
        #endregion
    }
}
