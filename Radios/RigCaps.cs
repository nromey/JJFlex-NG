using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Radio's capabilities
    /// </summary>
    public class RigCaps
    {
        // (collapsable) list of possible capabilities
        #region Capabilities
        private const ulong SetFlag = (ulong)Caps.SetBit;
        private const ulong AllSet = 0xffffffffffffffff;
        /// <summary>
        /// capabilities enumeration
        /// </summary>
        public enum Caps : ulong
        {
            None, // place holder (value 0).
            AFGet = 0x1UL, // AF gain
            AFSet = SetFlag+(ulong)AFGet,
            AGGet = 0x2UL, // AGC
            AGSet = SetFlag+AGGet,
            AGTimeGet = 0x4UL, // AGC time constant
            AGTimeSet = SetFlag+AGTimeGet,
            ALCGet = 0x8UL, // ALC (read only)
            ANGet = 0x10UL, // Antenna
            ANSet = SetFlag+ANGet,
            ATGet = 0x20UL, // Antenna tuner (also see ManualATGet)
            ATSet = SetFlag+ATGet,
            BCGet = 0x40UL, // Beat cancellation
            BCSet = SetFlag+BCGet,
            CLGet = 0x80UL, // Carier level
            CLSet = SetFlag+CLGet,
            CTSSFreqGet = 0x100UL, // CTSS frequency
            CTSSFreqSet = SetFlag+CTSSFreqGet,
            CTModeGet = 0x200UL, // CTSS mode
            CTModeSet = SetFlag+CTModeGet,
            CWAutoTuneGet = 0x400UL, // CW auto tune
            CWAutoTuneSet = SetFlag+CWAutoTuneGet,
            CWDelayGet = 0x800UL, // CW delay
            CWDelaySet = SetFlag+CWDelayGet,
            DMGet = 0x1000UL, // data mode
            DMSet = SetFlag+DMGet,
            EQRGet = 0x2000UL, // EQ receive
            EQRSet = SetFlag+EQRGet,
            EQTGet = 0x4000UL, // EQ transmit
            EQTSet = SetFlag+EQTGet,
            FrGet = 0x8000UL, // frequency
            FrSet = SetFlag+FrGet,
            FSGet = 0x10000UL, // filter shift
            FSSet = SetFlag+FSGet,
            FWGet = 0x20000UL, // filter width
            FWSet = SetFlag+FWGet,
            IDGet = 0x40000UL, // rig id (read only)
            KSGet = 0x80000UL, // Keying speed
            KSSet = SetFlag+KSGet,
            LKGet = 0x100000UL, // lock status
            LKSet = SetFlag+LKGet,
            MemGet = 0x200000UL, // Memory
            MemSet = SetFlag+MemGet,
            MGGet = 0x400000UL, // mic gain
            MGSet = SetFlag+MGGet,
            ModeGet = 0x800000UL, // mode
            ModeSet = SetFlag+ModeGet,
            NBGet = 0x1000000UL, // noise blanker (has a level)
            NBSet = SetFlag+NBGet,
            NFGet = 0x2000000UL, // Notch frequency
            NFSet = SetFlag+NFGet,
            NTGet = 0x4000000UL, // notch status
            NTSet = SetFlag+NTGet,
            PAGet = 0x8000000UL, // pre-amp
            PASet = SetFlag+PAGet,
            RAGet = 0x10000000UL, // RF attinuator
            RASet = SetFlag+RAGet,
            RFGet = 0x20000000UL, // RF gain
            RFSet = SetFlag+RFGet,
            RITGet = 0x40000000UL, // RIT
            RITSet = SetFlag+RITGet,
            SMGet = 0x80000000UL, // SMeter (read only)
            SPGet = 0x100000000UL, // speech processor status and level
            SPSet = SetFlag+SPGet,
            SQGet = 0x200000000UL, // squeltch
            SQSet = SetFlag+SQGet,
            SWRGet = 0x400000000UL, // SWR (read only)
            TOGet = 0x800000000UL, // Tone
            TOSet = SetFlag+TOGet,
            TXITGet = 0x1000000000UL, // XIT
            TXITSet = SetFlag+TXITGet,
            TXMonGet = 0x2000000000UL, // TX monitor
            TXMonSet = SetFlag+TXMonGet,
            VDGet = 0x4000000000UL, // VOX delay
            VDSet = SetFlag+VDGet,
            VFOGet = 0x8000000000UL, // current VFO
            VFOSet = SetFlag+VFOGet,
            VGGet = 0x10000000000UL, // VOX gain
            VGSet = SetFlag+VGGet,
            VSGet = 0x20000000000UL, // VOX status
            VSSet = SetFlag+VSGet,
            XFGet = 0x40000000000UL, // XMIT frequency
            XFSet = SetFlag+XFGet,
            XPGet = 0x80000000000UL, // transmit power
            XPSet = SetFlag+XPGet,
            Pan = 0x100000000000UL, // Panning support
            ManualATGet = 0x200000000000UL, // Manual tune (also see ATGet for ATU)
            ManualATSet = SetFlag+ManualATGet,
            ManualTransmit = 0x400000000000UL, // Manual transmit on/off.
            CWDecode = 0x800000000000UL, // CW decode.
            RemoteAudio = 0x1000000000000UL, // remote audio capable
            ATMems = 0x2000000000000UL, // Antenna tuner memory mgmt.
            SPGuideGet = 0x4000000000000UL, // speech guidance
            SPGuideSet = SetFlag + SPGuideGet,
            AutoModeGet = 0x8000000000000UL, // automatic mode change
            AutoModeSet = SetFlag + AutoModeGet,
            SetBit = 0x8000000000000000 // the "set" flag.
        }
        public Caps getCaps, setCaps;
        #endregion

        public enum VFOs
        {
            None = -1,
            VFOA = 0,
            VFOB,
            VFOC,
            VFOD,
        };

        public int MaxVFO = (int)VFOs.VFOB; // default is VFO B

        /// <summary>
        /// Provide capabilities
        /// </summary>
        /// <param name="c">arbitrary number of capabilities</param>
        public RigCaps(Caps[] c)
        {
            getCaps = setCaps = 0;
            foreach (Caps cap in c)
            {
                if (((ulong)cap & SetFlag) == 0) getCaps |= cap;
                else setCaps |= cap;
            }
        }

        /// <summary>
        /// Check for specified capability
        /// </summary>
        /// <param name="c">capability</param>
        /// <returns>True if yes</returns>
        public bool HasCap(Caps c)
        {
            return (((getCaps & c) == c) || ((setCaps & c) == c));
        }

        /// <summary>
        /// Sets a flag
        /// </summary>
        /// <param name="caps">current flags</param>
        /// <param name="flag">new flag</param>
        public Caps SetCap(Caps caps, Caps flag)
        {
            return (caps | flag);
        }

        /// <summary>
        ///  Reset a flag.
        /// </summary>
        /// <param name="caps">current caps</param>
        /// <param name="flag">cap to reset</param>
        /// <remarks>Don't reset the set flag if set.</remarks>
        public Caps ResetCap(Caps caps, Caps flag)
        {
            Caps setFlag = (flag & Caps.SetBit);
            Caps rv = (Caps)((ulong)caps & (AllSet ^ (ulong)flag)); // Also turns off the setFlag.
            return (rv | setFlag); // perhaps turn setFlag back on.
        }

        /// <summary>
        /// Return hex value of capabilities.
        /// </summary>
        public override string ToString()
        {
            return "getcaps:" + ((ulong)getCaps).ToString("x") + "\r\n" + "setcaps:" + ((ulong)setCaps).ToString("x");
        }

        /// <summary>
        /// Valid modes
        /// </summary>
        public static string[] ModeTable = new string[]
            { "LSB", "USB", "CW", "AM", "FM", "DIGU", "DIGL", "NFM", "DFM", "SAM" };

        // Region - default capabilities
            #region capabilities
        internal static RigCaps.Caps[] DefaultCapsList =
        {
            RigCaps.Caps.AFGet,
            RigCaps.Caps.AFSet,
            RigCaps.Caps.AGGet,
            RigCaps.Caps.AGSet,
            RigCaps.Caps.FrGet,
            RigCaps.Caps.FrSet,
            RigCaps.Caps.IDGet,
            RigCaps.Caps.KSGet,
            RigCaps.Caps.KSSet,
            RigCaps.Caps.ModeGet,
            RigCaps.Caps.ModeSet,
            RigCaps.Caps.RITGet,
            RigCaps.Caps.RITSet,
            RigCaps.Caps.SMGet,
            RigCaps.Caps.TXITGet,
            RigCaps.Caps.TXITSet,
            RigCaps.Caps.VFOGet,
            RigCaps.Caps.VFOSet,
            //RigCaps.Caps.ATGet,
            //RigCaps.Caps.ATSet,
            RigCaps.Caps.ManualATGet,
            RigCaps.Caps.ManualATSet,
            RigCaps.Caps.AGTimeGet,
            RigCaps.Caps.AGTimeSet,
            RigCaps.Caps.ALCGet,
            RigCaps.Caps.ANGet,
            RigCaps.Caps.ANSet,
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
            RigCaps.Caps.FSGet,
            RigCaps.Caps.FSSet,
            RigCaps.Caps.FWGet,
            RigCaps.Caps.FWSet,
            RigCaps.Caps.LKGet,
            RigCaps.Caps.LKSet,
            RigCaps.Caps.MemGet,
            RigCaps.Caps.MemSet,
            RigCaps.Caps.MGGet,
            RigCaps.Caps.MGSet,
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
            // Note that RemoteAudio is not supported on the Flex6300Remote, since it must be assumed.
            RigCaps.Caps.RemoteAudio,
            //RigCaps.Caps.ATMems,
        };
        #endregion
    }
}
