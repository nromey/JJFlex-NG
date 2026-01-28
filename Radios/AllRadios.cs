using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Timers;
using System.Windows.Forms;
using Flex.Smoothlake.FlexLib;
using HamBands;
using JJPortaudio;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Provide template functions for radio queries and actions.
    /// </summary>
    public partial class AllRadios
    {
        // string constants
        internal const string Empty = "empty";

        /// <summary>
        /// Radios.dll version
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
        /// This rig's capabilities
        /// </summary>
        public RigCaps myCaps; // See RigCaps.cs

        /// <summary>
        /// Argument for CapsChangeEvent
        /// </summary>
        public class CapsChangeArg
        {
            public RigCaps NewCaps;
            internal CapsChangeArg(RigCaps caps)
            {
                NewCaps = caps;
            }
        }
        public delegate void CapsChangeDel(CapsChangeArg arg);
        /// <summary>
        /// Raised when rig's capabilities change.
        /// </summary>
        public event CapsChangeDel CapsChangeEvent;
        internal void RaiseCapsChange(CapsChangeArg arg)
        {
            Tracing.TraceLine("RaiseCapsChange arg:" + +' '+ ((ulong)arg.NewCaps.setCaps).ToString("x"), TraceLevel.Error);
            if (CapsChangeEvent != null)
            {
                Tracing.TraceLine("RaiseCapsChange raised", TraceLevel.Info);
                CapsChangeEvent(arg);
            }
            else Tracing.TraceLine("RaiseCapsChange not raised", TraceLevel.Error);
        }

        public virtual int RigID
        {
            get { return 0; }
        }

        // region - general properties and methods
#region general properties
        /// <summary>
        /// Off/On values for use by the rigs
        /// </summary>
        public enum OffOnValues
        {
            off,
            on
        }
#if zero
        /// <summary>
        /// On/Off (reversed Off/On) values for use by the rigs
        /// </summary>
        public enum OnOffValues
        {
            on,
            off
        }
#endif

        /// <summary>
        /// return the toggle of the OffOnValue
        /// </summary>
        /// <param name="value"></param>
        /// <returns>Toggled OffOnValue</returns>
        public OffOnValues ToggleOffOn(OffOnValues value)
        {
            return (value == OffOnValues.on) ? OffOnValues.off : OffOnValues.on;
        }

        /// <summary>
        /// Data passed when a radio is discovered (network rigs only).
        /// </summary>
        public class RadioDiscoveredEventArgs
        {
            public string Name;
            public string Model;
            public string Serial;
            public RadioDiscoveredEventArgs() { }
            internal RadioDiscoveredEventArgs(string name, string model, string serial)
            {
                Name = name;
                Model = model;
                Serial = serial;
            }
        }
        public delegate void RadioDiscoveredEventHandler(RadioDiscoveredEventArgs arg);
        /// <summary>
        /// Raised when a radio is discovered (network rigs only).
        /// </summary>
        public static event RadioDiscoveredEventHandler RadioDiscoveredEvent;
        internal static void RaiseRadioDiscovered(RadioDiscoveredEventArgs arg)
        {
            if (RadioDiscoveredEvent != null)
            {
                RadioDiscoveredEvent(arg);
            }
        }
        /// <summary>
        /// Discover radios on the network.
        /// Data returned using the RadioDiscoveredEvent.
        /// </summary>
        public static void DiscoverRadios()
        {
            FlexDiscovery.DiscoverFlexRadios(null, false); // ignore the return value.
        }

        /// <summary>
        /// Allow user to enter network radio info.
        /// </summary>
        /// <returns>null - flex only supported now</returns>
        /// <param name="existingInfo">(optional) existing info</param>
        public static RadioDiscoveredEventArgs ManualNetworkRadioInfo(RadioDiscoveredEventArgs existingInfo=null)
        {
            return FlexDiscovery.FlexManualNetworkRadioInfo(existingInfo);
        }

        private bool _NetworkRadio = false;
        /// <summary>
        /// True for a network radio.
        /// </summary>
        public bool NetworkRadio
        {
            get { return _NetworkRadio; }
            set
            {
                Tracing.TraceLine("NetworkRadio:" + value.ToString(), TraceLevel.Info);
                _NetworkRadio = value;
            }
        }

        /// <summary>
        /// Internal vfos
        /// </summary>
        protected RigCaps.VFOs _RXVFO, _TXVFO;
        /// <summary>
        /// receive VFO in use
        /// </summary>
        public virtual RigCaps.VFOs RXVFO
        {
            get { return _RXVFO; }
            set { _RXVFO = value; }
        }
        /// <summary>
        /// transmit VFO
        /// </summary>
        public virtual RigCaps.VFOs TXVFO
        {
            get { return _TXVFO; }
            set { _TXVFO = value; }
        }
        /// <summary>
        /// return the next VFO in the list.
        /// If it's a memory, VFOs.None, just return VFOs.None.
        /// </summary>
        /// <param name="v">Starting VFO</param>
        /// <returns>Next VFO</returns>
        public virtual RigCaps.VFOs NextVFO(RigCaps.VFOs v)
        {
            RigCaps.VFOs rv;
            if (IsMemoryMode(v)) rv = v;
            else
            {
                int n = myCaps.MaxVFO;
                int i = (int)v;
                // Note there are n+1 real VFOs.
                rv = (RigCaps.VFOs)((i + 1) % (n+1));
            }
            return rv;
        }

        /// <summary>
        /// copy a VFO.
        /// </summary>
        /// <param name="inv">VFO to copy</param>
        /// <param name="outv">Target VFO</param>
        public virtual void CopyVFO(RigCaps.VFOs inv, RigCaps.VFOs outv)
        {
            // Provided by the rigs.
        }

        public delegate void TransmitChangeDel(object sender, bool value);
        public event TransmitChangeDel TransmitChange;
        private bool _Transmit;
        /// <summary>
        /// True if transmitting
        /// </summary>
        public virtual bool Transmit
        {
            get { return _Transmit; }
            set
            {
                TransmitStatus = value;
            }
        }
        /// <summary>
        /// set transmit status but don't go into transmit.
        /// </summary>
        internal bool TransmitStatus
        {
            set
            {
                if ((_Transmit != value) & (TransmitChange != null))
                {
                    TransmitChange(this, value);
                }
                _Transmit = value;
            }
        }
        /// <summary>
        /// True if split operation
        /// </summary>
        public virtual bool Split
        {
            get
            {
                bool rv = false;
                if (IsMemoryMode(_RXVFO))
                {
                    // Using a memory
                    try
                    {
                        MemoryData m = Memories.mems[CurrentMemoryChannel];
                        rv = ((m != null) && m.complete && m.Present && m.Split);
                    }
                    catch (Exception ex) { Tracing.TraceLine("split:" + ex.Message, TraceLevel.Error); }
                }
                else
                {
                    // using VFOs.
                    rv = ((_RXVFO != _TXVFO) || TFSetOn);
                }
                return rv;
            }
            // The "set" must be provided by the rigs.
            set { }
        }
        /// <summary>
        /// Get the current VFO in use.
        /// </summary>
        public RigCaps.VFOs CurVFO
        {
            get
            {
                return (Transmit) ? TXVFO : RXVFO;
            }
            set
            {
                if (Transmit)
                {
                    TXVFO = value;
                }
                else
                {
                    RXVFO = value;
                }
            }
        }
        internal RigCaps.VFOs nextVFO(RigCaps.VFOs cur)
        {
            RigCaps.VFOs rv = (RigCaps.VFOs)(((int)cur + 1) % (Enum.GetValues(typeof(RigCaps.VFOs)).Length - 1));
            Tracing.TraceLine("nextVFO:" + cur.ToString() + " " + rv.ToString(), TraceLevel.Verbose);
            return rv;
        }
        // TFSetOn is set if showing it frequency when split.
        protected bool TFSetOn;
        /// <summary>
        /// Show the transmit frequency while split.
        /// </summary>
        public virtual bool SplitShowXmitFrequency
        {
            get { return TFSetOn; }
            // Set provided by the rigs if function exists.
            set { }
        }
        /// <summary>
        /// RX frequency to log.
        /// May not be the same as the RXFrequency.
        /// </summary>
        public ulong RXFrequency4Logging;
        /// <summary>
        /// Internal frequencies
        /// </summary>
        protected ulong __RXFrequency;
        protected ulong _RXFrequency
        {
            get { return __RXFrequency; }
            set
            {
                __RXFrequency = value;
                if (!TFSetOn) RXFrequency4Logging = value;
            }
        }
        protected ulong _TXFrequency;
        /// <summary>
        /// receive frequency
        /// </summary>
        public virtual ulong RXFrequency
        {
            get { return _RXFrequency; }
            set { 
                _RXFrequency = value;
            }
        }
        /// <summary>
        /// transmit frequency
        /// </summary>
        public virtual ulong TXFrequency
        {
            get { return _TXFrequency; }
            set { _TXFrequency = value; }
        }
        /// <summary>
        /// current frequency
        /// </summary>
        public ulong Frequency
        {
            get
            {
                if (Transmit) return TXFrequency;
                else return RXFrequency;
            }
            set
            {
                if (Transmit) TXFrequency = value;
                else RXFrequency = value;
            }
        }
        protected int _SMeter;
        /// <summary>
        /// (readOnly) Calibrated SMeter/power value
        /// </summary>
        /// <remarks>
        /// Rigs override to provide calibrated value.
        /// Value is added to 9 to indicate values over S9, (e.g.) 14 means S9+5.
        /// </remarks>
        public virtual int SMeter
        {
            get { return _SMeter; }
        }
        /// <summary>
        /// (readOnly) Raw SMeter value.
        /// </summary>
        public int RawSMeter { get { return _SMeter; } }

        public class RITData
        {
            public bool Active;
            public int Value; // may be negative
            public RITData()
            {
                Active = false;
                Value = 0;
            }
            public RITData(RITData r)
            {
                Active = r.Active;
                Value = r.Value;
            }
        }
        protected RITData _RIT = new RITData();
        /// <summary>
        /// RIT, Active and Value.
        /// </summary>
        public virtual RITData RIT
        {
            get { return _RIT; }
            set { _RIT = value; }
        }

        protected RITData _XIT = new RITData();
        /// <summary>
        /// XIT, Active and Value.
        /// </summary>
        public virtual RITData XIT
        {
            get { return _XIT; }
            set { _XIT = value; }
        }

        protected int _AGC;
        /// <summary>
        /// Minimum AGC response time in milliseconds.
        /// </summary>
        /// <remarks>Rigs can override</remarks>
        public virtual int AGCMinTime { get { return 30; } }
        /// <summary>
        /// AGC fast level.
        /// </summary>
        public virtual int AGCFast { get { return 1; } }
        /// <summary>
        /// AGC Slow level.
        /// </summary>
        public virtual int AGCSlow { get { return 20; } }
        /// <summary>
        /// AGC value, 0 through 20.
        /// 0 turns the AGC off.
        /// </summary>
        public virtual int AGC
        {
            get { return _AGC; }
            set { _AGC = value; }
        }

        /// <summary>
        /// power on/off
        /// </summary>
        public virtual bool Power
        {
            get
            {
                return powerWasOn; // See below
            }
            set
            {
                // handled by the rigs.  Should turn power on/off.
            }
        }

        // Region - power checking
        #region powerCheck
        /// <summary>
        /// Method to initiate a power on check, defined by the rigs
        /// </summary>
        protected virtual void PowerCheck()
        {
            // handled by the rigs; command to check power status.
        }
        private byte poByte = 0;
        private bool powerWasOn
        {
            get { return (Thread.VolatileRead(ref poByte) == 1); }
            set { Thread.VolatileWrite(ref poByte, (byte)((value) ? 1 : 0)); }
        }
        private int lastPCheck = 0;
        private bool sinceLastCheck
        {
            get { return (Thread.VolatileRead(ref lastPCheck) == 1) ? true : false; }
            set { Thread.VolatileWrite(ref lastPCheck, (value) ? 1 : 0); }
        }
        /// <summary>
        /// Power status interrupt argument.
        /// </summary>
        public class PowerStatusArg
        {
            /// <summary>
            /// true if power is on.
            /// </summary>
            public bool On;
            internal PowerStatusArg(bool p)
            {
                On = p;
            }
        }
        /// <summary>
        /// power status event handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e">event argument</param>
        public delegate void PowerStatusHandler(object sender, PowerStatusArg e);
        /// <summary>
        /// power status event
        /// </summary>
        public event PowerStatusHandler PowerStatus;
        private void raisePowerEvent(bool on)
        {
            bool raise = (PowerStatus != null);
            Tracing.TraceLine("raisePowerEvent:" + on.ToString() + ' ' + raise.ToString(), TraceLevel.Info);
            if (raise)
            {
                PowerStatus(this, new PowerStatusArg(on));
            }
        }
        /// <summary>
        /// if power was off, raise power on event and call getRigStatus.
        /// </summary>
        internal void raisePowerOn()
        {
            Tracing.TraceLine("raisePowerOn", TraceLevel.Info);
            if (!powerWasOn)
            {
                raisePowerEvent(true);
                powerWasOn = true;
                // Get status w/o checking power.
                getRigStatus(false, true);
            }
        }
        /// <summary>
        /// if power was on, raise power off event.
        /// </summary>
        internal void raisePowerOff()
        {
            Tracing.TraceLine("raisePowerOff", TraceLevel.Info);
            if (powerWasOn)
            {
                raisePowerEvent(false);
                powerWasOn = false;
            }
        }
        /// <summary>
        /// Get rig's status
        /// </summary>
        /// <param name="ckPower">check power status if true.</param>
        /// <param name="initial">true for initial call at device startup</param>
        protected virtual void getRigStatus(bool ckPower, bool initial)
        {
            // implemented by the rigs.
        }
        /// <summary>
        /// return true if power is on.
        /// Wait up to a second if not already on.
        /// When anything arrives from the rig, powerOn() must be called.
        /// </summary>
        internal bool powerOnCheck()
        {
            if (!powerWasOn)
            {
                PowerCheck();
                // wait up to a second.
                await(() => { return powerWasOn; }, 1000);
            }
            Tracing.TraceLine("powerOnCheck returning " + powerWasOn.ToString(), TraceLevel.Info);
            return powerWasOn;
        }
        private System.Threading.Timer heartbeatTimer = null;
        protected int _HeartbeatInterval = 2000; // default 2 seconds.
        /// <summary>
        /// Get/set the heartbeat interval in milliseconds, default is 2000.
        /// </summary>
        public int HeartbeatInterval
        {
            get { return _HeartbeatInterval; }
            set
            {
                Tracing.TraceLine("HeartbeatInterval:" + value.ToString(), TraceLevel.Info);
                _HeartbeatInterval = value;
                if (value == 0)
                {
                    Heartbeat = false;
                }
            }
        }

        private bool _Heartbeat;
        /// <summary>
        /// Turn the heartbeat power check timer on/off.
        /// Not set for a network radio.
        /// </summary>
        public bool Heartbeat
        {
            get { return _Heartbeat; }
            set
            {
                value = (value & !NetworkRadio);
                Tracing.TraceLine("Heartbeat:" + value.ToString(), TraceLevel.Info);
                // note that heartbeatFlag may be set even if timer not enabled.
                _Heartbeat = value;
                //powerWasOn = false;
                sinceLastCheck = false;
                if (value)
                {
                    heartbeatTimer = new System.Threading.Timer(new TimerCallback(heartbeatHandler), null, 0, HeartbeatInterval);
                }
                else
                {
                    // careful!  May already be disabled.
                    if (heartbeatTimer != null)
                    {
                        WaitHandle hand = new AutoResetEvent(false);
                        heartbeatTimer.Dispose(hand);
                        hand.WaitOne(HeartbeatInterval + (HeartbeatInterval / 2));
                        hand.Dispose();
                        heartbeatTimer = null;
                    }
                }
            }
        }
        private void heartbeatHandler(object s)
        {
            Tracing.TraceLine("heartbeatHandler:" + Heartbeat.ToString() + " " + sinceLastCheck.ToString(), TraceLevel.Info);
            if (!Heartbeat) return; // has been disabled.
            if (sinceLastCheck)
            {
                // something has been received.
                sinceLastCheck = false;
            }
            else
            {
                // nothing received, interrupt raised only if power had been on.
                raisePowerOff();
            }
            // send heartbeat power check.
            PowerCheck();
        }
        /// <summary>
        /// Indicate power is on.
        /// Rigs using async communications call this whenever anything received from the rig.
        /// </summary>
        internal void powerOn()
        {
            Tracing.TraceLine("powerOn", TraceLevel.Info);
            if (!sinceLastCheck)
            {
                sinceLastCheck = true;
                // only raises event if power was off.
                raisePowerOn();
            }
        }
        #endregion

        /// <summary>
        /// Operating mode value type
        /// </summary>
        public class ModeValue
        {
            internal char value;
            private string name;
            public int id;
            internal ModeValue() { }
            internal ModeValue(int i, char val, string nam)
            {
                value = val;
                name = nam;
                id = i;
            }
            internal ModeValue(char c)
            {
                value = c;
                name = "";
                id = 0;
            }
            public static bool operator ==(ModeValue val1,ModeValue val2)
            {
                if (((object)val1 == null) && ((object)val2 == null)) return true;
                if (((object)val1 == null) || ((object)val2 == null)) return false;
                return (val1.value == val2.value);
            }
            public static bool operator !=(ModeValue val1, ModeValue val2)
            {
                if (((object)val1 == null) && ((object)val2 == null)) return false;
                if (((object)val1 == null) || ((object)val2 == null)) return true;
                return (val1.value != val2.value);
            }
            public override bool Equals(object obj)
            {
                bool rv;
                try { rv = (value == ((ModeValue)obj).value); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("ModeValue exception:" + ex.Message, TraceLevel.Error);
                    rv = false;
                }
                return rv;
            }
            public override int GetHashCode()
            {
                return (int)value;
            }
            public override string ToString()
            {
                return name;
            }
        }
        /// <summary>
        /// Valid modes for this rig.
        /// </summary>
        public ModeValue[] ModeTable;

        protected ModeValue rxM, txM;
        protected virtual ModeValue _RXMode
        {
            get { return rxM; }
            set { rxM = value; }
        }
        protected virtual ModeValue _TXMode
        {
            get { return txM; }
            set { txM = value; }
        }
        /// <summary>
        /// Receive operating mode
        /// </summary>
        public virtual ModeValue RXMode
        {
            get { return _RXMode; }
            set { _RXMode = value; }
        }
        /// <summary>
        ///  Transmit operating mode.
        /// </summary>
        public virtual ModeValue TXMode
        {
            get { return _TXMode; }
            set { _TXMode = value; }
        }
        /// <summary>
        /// Used if we need to get the TX mode by hand.
        /// </summary>
        /// <returns></returns>
        internal virtual bool getTXMode() { return true; }
        /// <summary>
        /// Current mode
        /// </summary>
        public ModeValue Mode
        {
            get
            {
                if (Transmit) return TXMode;
                else return RXMode;
            }
            set
            {
                if (Transmit) TXMode = value;
                else RXMode = value;
            }
        }

        /// <summary>
        /// Data mode values
        /// </summary>
        public enum DataModes
        {
            unset = -1, // for internal use
            off = 0,
            on = 1
        }
        protected DataModes _RXDataMode, _TXDataMode;
        /// <summary>
        /// Receive Data mode
        /// </summary>
        public virtual DataModes RXDataMode
        {
            get { return _RXDataMode; }
            set { _RXDataMode = value; }            
        }
        /// <summary>
        /// Transmit data mode
        /// </summary>
        public virtual DataModes TXDataMode
        {
            get { return _TXDataMode; }
            set { _TXDataMode = value; }
        }
        /// <summary>
        /// Current Data mode
        /// </summary>
        public DataModes DataMode
        {
            get
            {
                if (Transmit) return TXDataMode;
                else return RXDataMode;
            }
            set
            {
                if (Transmit) TXDataMode = value;
                else RXDataMode = value;
            }
        }

        /// <summary>
        /// Tone/CTSS value type
        /// </summary>
        public class ToneCTCSSValue
        {
            internal char value;
            private string name;
            internal ToneCTCSSValue(char val, string nam)
            {
                value = val;
                name = nam;
            }
            internal ToneCTCSSValue(char c)
            {
                value = c;
                name = "";
            }
            public static bool operator ==(ToneCTCSSValue val1,ToneCTCSSValue val2)
            {
                if (((object)val1 == null) && ((object)val2 == null)) return true;
                if (((object)val1 == null) || ((object)val2 == null)) return false;
                return (val1.value == val2.value);
            }
            public static bool operator !=(ToneCTCSSValue val1, ToneCTCSSValue val2)
            {
                if (((object)val1 == null) && ((object)val2 == null)) return false;
                if (((object)val1 == null) || ((object)val2 == null)) return true;
                return (val1.value != val2.value);
            }
            public override bool Equals(object obj)
            {
                bool rv;
                try { rv = (value == ((ToneCTCSSValue)obj).value); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("ToneCTCSSValue exception:" + ex.Message, TraceLevel.Error);
                    rv = false;
                }
                return rv;
            }
            public override int GetHashCode()
            {
                return (int)value;
            }
            public override string ToString()
            {
                return name;
            }
        }
        /// <summary>
        /// FM tone modes
        /// </summary>
        public ToneCTCSSValue[] FMToneModes;
        /// <summary>
        /// Internal tone/CTSS value
        /// </summary>
        protected ToneCTCSSValue _ToneCTCSS;
        /// <summary>
        /// Tone/CTSS
        /// </summary>
        public virtual ToneCTCSSValue ToneCTCSS
        {
            get { return _ToneCTCSS; }
            set { _ToneCTCSS = value; }
        }
        /// <summary>
        /// Array of tone frequencies
        /// </summary>
        public float[] ToneFrequencyTable;
        /// <summary>
        /// Internal tone frequency
        /// </summary>
        protected float _ToneFrequency;
        /// <summary>
        /// Tone frequency
        /// </summary>
        public virtual float ToneFrequency
        {
            get { return _ToneFrequency; }
            set { _ToneFrequency = value; }
        }
        /// <summary>
        /// Internal CTSS frequency.
        /// </summary>
        protected float _CTSSFrequency;
        /// <summary>
        /// CTSS frequency
        /// </summary>
        public virtual float CTSSFrequency
        {
            get { return _CTSSFrequency; }
            set { _CTSSFrequency = value; }
        }

        protected static ushort[] DCSTable =
        {
            023, 025, 026,031, 032, 036, 043, 047, 051, 053, 054, 065, 071, 
            072,073, 074, 114,115,
            116, 122, 125, 131, 132, 134, 143, 145, 152, 155,156, 162, 165,
            172, 174,205, 212, 223,
            225, 226, 243,244, 245, 246, 251, 252, 255, 261, 263, 265, 266, 
            271, 274, 306, 311, 315, 325, 331, 332, 343, 346,  351, 356, 364,
            365, 371, 411, 412, 413, 423, 431, 432, 445, 446, 452, 454, 
            455, 462, 464, 465, 466, 503, 506, 516, 523, 526, 532, 546, 565, 
            606, 612, 624, 627, 631, 632, 654, 662, 664, 703, 712, 723, 731, 
            732, 734, 743, 754, 
        };
        protected ushort _DCS;
        public virtual ushort DCS
        {
            get { return _DCS; }
            set { _DCS = value; }
        }

        /// <summary>
        /// Offset direction values
        /// </summary>
        public enum OffsetDirections : byte
        {
            off, plus, minus, allTypes
        }
        protected OffsetDirections _OffsetDirection;
        /// <summary>
        /// offset direction
        /// </summary>
        public virtual OffsetDirections OffsetDirection
        {
            get { return _OffsetDirection; }
            set { _OffsetDirection = value; }
        }

        protected int _OffsetFrequency;
        /// <summary>
        /// Offset frequency in HZ.
        /// </summary>
        public virtual int OffsetFrequency
        {
            get { return _OffsetFrequency; }
            set { _OffsetFrequency = value; }
        }
        /// <summary>
        /// Min offset (KHZ)
        /// </summary>
        public virtual int MinOffsetFrequency { get { return 0; } }
        /// <summary>
        /// Max offset (KHZ)
        /// </summary>
        public virtual int MaxOffsetFrequency { get { return 990000; } }
        /// <summary>
        /// Offset frequency step size (khz)
        /// </summary>
        public virtual int OffsetFrequencyStep { get { return 50; } }

        protected float _StepSize;
        /// <summary>
        /// Step size
        /// </summary>
        internal virtual float StepSize
        {
            get { return _StepSize; }
            set { _StepSize = value; }
        }

        /// <summary>
        /// How to directly report radio output
        /// Default is none.
        /// </summary>
        public enum CommandReporting
        {
            none, raw, inputBased
        }
        internal CommandReporting sendingOutput = CommandReporting.none;
        internal string checkString = null;
        /// <summary>
        /// set rig output reporting or query the state.
        /// </summary>
        /// <returns>CommandReporting value</returns>
        public CommandReporting ReportRigOutput()
        {
            return sendingOutput;
        }
        /// <summary>
        /// set rig output reporting or query the state.
        /// </summary>
        /// <param name="rpt">new CommandReporting state</param>
        /// <param name="str">string sent to the rig</param>
        /// <returns>CommandReporting state</returns>
        public CommandReporting ReportRigOutput(CommandReporting rpt, string str)
        {
            CommandReporting rv = sendingOutput;
            checkString = str;
            sendingOutput = rpt;
            return rv;
        }

        /// <summary>
        /// default minimum keyer speed
        /// </summary>
        internal const int MinSpeed = 4;
        /// <summary>
        /// default maximum keyer speed.
        /// </summary>
        internal const int MaxSpeed = 60;

        /// <summary>
        /// Transmit antennas
        /// </summary>
        public virtual int TXAnts { get { return 2; } }
        /// <summary>
        /// Receive antennas
        /// </summary>
        public virtual int RXAnts { get { return 2; } }
        protected int _TXAntenna;
        /// <summary>
        /// Which main antenna (0 through n)
        /// </summary>
        public virtual int TXAntenna
        {
            get { return _TXAntenna; }
            set { _TXAntenna = value; }
        }
        protected bool _RXAntenna;
        /// <summary>
        /// Auxiliary receive antenna
        /// </summary>
        public virtual bool RXAntenna
        {
            get { return _RXAntenna; }
            set { _RXAntenna = value; }
        }
        protected bool _DriveAmp;
        /// <summary>
        /// true if set to drive an amp.
        /// </summary>
        public virtual bool DriveAmp
        {
            get { return _DriveAmp; }
            set { _DriveAmp = value; }
        }

        /// <summary>
        /// bitwise antenna tuner values
        /// </summary>
        [Flags]
        public enum AntTunerVals
        {
            none=0x0,
            rx=0x01,
            tx=0x02,
            tune=0x04
        }
        protected AntTunerVals _AntennaTuner;
        /// <summary>
        /// Antenna tuner value
        /// </summary>
        public virtual AntTunerVals AntennaTuner
        {
            get { return _AntennaTuner; }
            set { _AntennaTuner = value; }
        }

        /// <summary>
        /// Antenna tuner memory management
        /// </summary>
        public virtual void AntennaTunerMemories() { }

        /// <summary>
        /// Flex Antenna tuner start/stop interrupt argument
        /// </summary>
        public class FlexAntTunerArg
        {
            public string Type;
            public string Status;
            public string SWR; // Good when stopped
            public FlexAntTunerArg(FlexTunerTypes type,ATUTuneStatus status, float swr)
            {
                Type = type.ToString();
                Status = status.ToString();
                SWR = swr.ToString("f1");
            }
            // Used to send a message
            public FlexAntTunerArg(string status)
            {
                Status = status;
                Type = null;
                SWR = null;
            }
        }
        public delegate void FlexAntTunerStartStopDel(FlexAntTunerArg arg);
        /// <summary>
        /// Antenna tuner start/stop event
        /// </summary>
        public event FlexAntTunerStartStopDel FlexAntTunerStartStop;
        internal void RaiseFlexAntTuneStartStop(FlexAntTunerArg arg)
        {
            if (FlexAntTunerStartStop != null)
            {
                Tracing.TraceLine("FlexAntTunerStartStop raised" + arg.Type + ' ' + arg.Status + ' ' + arg.SWR, TraceLevel.Info);
                FlexAntTunerStartStop(arg);
            }
            else Tracing.TraceLine("FlexAntTunerStartStop not raised", TraceLevel.Verbose);
        }

        protected OffOnValues _ManualTuner;
        /// <summary>
        /// Manual antenna tuning on/off
        /// </summary>
        public virtual OffOnValues ManualTuner
        {
            get { return _ManualTuner; }
            set { _ManualTuner = value; }
        }

        /// <summary>
        /// Type of the Flex tuner in use
        /// </summary>
        public enum FlexTunerTypes
        {
            none,
            manual,
            auto,
        }
        internal FlexTunerTypes _FlexTunerType;
        public virtual FlexTunerTypes FlexTunerType
        {
            get { return _FlexTunerType; }
            set
            {
                // Provided by the rig.
            }
        }

        internal bool _FlexTunerOn;
        public virtual bool FlexTunerOn
        {
            get { return _FlexTunerOn; }
            set
            {
                // Provided by the rig.
            }
        }

        public virtual bool FlexTunerUsingMemoryNow
        {
            get { return false; }
        }

        protected int _AudioGain;
        /// <summary>
        /// Flex audio gain, RF gain for other rigs.
        /// </summary>
        public virtual int AudioGain
        {
            get { return _AudioGain; }
            set { _AudioGain = value; }
        }

        protected int _LineoutGain;
        /// <summary>
        /// Flex lineout gain, audio gain for other rigs.
        /// </summary>
        public virtual int LineoutGain
        {
            get { return _LineoutGain; }
            set { _LineoutGain = value; }
        }

        protected int _HeadphoneGain;
        /// <summary>
        /// Receiver headphone gain.
        /// </summary>
        public virtual int HeadphoneGain
        {
            get { return _HeadphoneGain; }
            set { _HeadphoneGain = value; }
        }

        protected OffOnValues _Vox;
        /// <summary>
        /// Vox on/off
        /// </summary>
        public virtual OffOnValues Vox
        {
            get { return _Vox; }
            set { _Vox = value; }
        }

        protected OffOnValues _RFAttenuator;
        /// <summary>
        /// binary RF attenuator
        /// </summary>
        public virtual OffOnValues RFAttenuator
        {
            get { return _RFAttenuator; }
            set { _RFAttenuator = value; }
        }
        protected int _AttenuatorValue;
        /// <summary>
        /// RF attenuator value
        /// </summary>
        public virtual int AttenuatorValue
        {
            get { return _AttenuatorValue; }
            set { _AttenuatorValue = value; }
        }

        protected OffOnValues _NoiseBlanker;
        /// <summary>
        /// Noise blanker on/off
        /// </summary>
        public virtual OffOnValues NoiseBlanker
        {
            get { return _NoiseBlanker; }
            set { _NoiseBlanker = value; }
        }

        protected OffOnValues _NoiseReduction;
        /// <summary>
        /// Noise reduction on/off.
        /// </summary>
        public virtual OffOnValues NoiseReduction
        {
            get { return _NoiseReduction; }
            set { _NoiseReduction = value; }
        }

        protected OffOnValues _AutoNotch;
        /// <summary>
        /// Autonotch on/off
        /// </summary>
        public virtual OffOnValues AutoNotch
        {
            get { return _AutoNotch; }
            set { _AutoNotch = value; }
        }

        protected OffOnValues _ManualNotch;
        /// <summary>
        /// Manual notch on/off
        /// </summary>
        public virtual OffOnValues ManualNotch
        {
            get { return _ManualNotch; }
            set { _ManualNotch = value; }
        }

        protected int _XmitPower;
        /// <summary>
        /// transmit power in watts
        /// </summary>
        /// <remarks>a rig with fixed power should set xmitPwr.</remarks>
        public virtual int XmitPower
        {
            get { return _XmitPower; }
            set
            {
                // provided by the rigs 
            }
        }

        protected int _TunePower;
        /// <summary>
        /// Tune power in watts
        /// </summary>
        /// <remarks>a rig with fixed power should set _TunePower.</remarks>
        public virtual int TunePower
        {
            get { return _TunePower; }
            set
            {
                // provided by the rigs 
            }
        }

        /// <summary>
        /// Query the rig for the meter.
        /// </summary>
        public virtual void UpdateMeter()
        {
            // Implemented by the rigs.
        }

        // region - cw stuff
        #region "cw"
        /// <summary>
        /// (overloaded) Send a CW character.
        /// </summary>
        /// <param name="c">character to send</param>
        /// <returns>true if sent</returns>
        public virtual bool SendCW(char c)
        {
            // Rigs must override.
            return false;
        }
        /// <summary>
        /// (overloaded) Send a CW string
        /// </summary>
        /// <param name="str">string</param>
        /// <returns>true on success</returns>
        public virtual bool SendCW(string str)
        {
            if (str == null) return false;
            // May be overridden by the rigs.
            foreach (char c in str)
            {
                if (!SendCW(c)) return false;
            }
            return true;
        }
        /// <summary>
        /// Halt the sending of CW.
        /// </summary>
        public virtual void StopCW()
        {
            // provided by the rigs.
        }

        // Data/CW receive
        /// <summary>
        /// (readOnly) True if can receive data.
        /// </summary>
        public bool CanReceiveData { get; internal set; }

        internal const int RXCharBufferSize = 40;
        internal string RXCharBuffer;
        /// <summary>
        /// (readOnly) return received data if any.
        /// </summary>
        public virtual string DataReceived
        {
            get
            {
                string rv;
                if (CanReceiveData) rv = RXCharBuffer;
                else rv = "";
                return rv;
            }
        }
        #endregion

        // Used for rig-specific functions.
        public delegate void updateDel();
        /// <summary>
        /// Type of RigFields.
        /// </summary>
        public class RigInfo
        {
            /// <summary>
            /// RigFields form control
            /// </summary>
            public Control RigControl;
            /// <summary>
            /// RigFields update function.
            /// </summary>
            public updateDel RigUpdate;
            /// <summary>
            /// Memory display form.
            /// </summary>
            public Form Memories;
            /// <summary>
            /// Menu display form
            /// </summary>
            public Form Menus;
            /// <summary>
            /// Screen fields list.
            /// </summary>
            public Control[] ScreenFields;
            internal RigInfo(Control c, updateDel rtn)
            {
                setup(c, rtn, null, null, null);
            }
            internal RigInfo(Control c, updateDel rtn, Form f)
            {
                setup(c, rtn, f, null, null);
            }
            internal RigInfo(Control c, updateDel rtn, Form mem, Form mnu)
            {
                setup(c, rtn, mem, mnu, null);
            }
            internal RigInfo(Control c, updateDel rtn, Form mem, Form mnu,
                Control[] s)
            {
                setup(c, rtn, mem, mnu, s);
            }
            private void setup(Control c, updateDel rtn, 
                Form mem, Form mnu, Control[] s)
            {
                RigControl = c;
                RigUpdate = rtn;
                Memories = mem;
                Menus = mnu;
                ScreenFields = s;
            }
            /// <summary>
            /// Close down the forms.
            /// </summary>
            internal void Close()
            {
                if (RigControl != null)
                {
                    RigControl.Dispose();
                    RigControl = null;
                }
                if (Memories != null)
                {
                    Memories.Dispose();
                    Memories = null;
                }
                if (Menus != null)
                {
                    Menus.Dispose();
                    Menus = null;
                }
            }
        }
        /// <summary>
        /// Gets the RigInfo data for the active rig.
        /// </summary>
        public RigInfo RigFields
        {
            get;
            // value provided by the rigs.
            internal set;
        }

        protected bool _RigSpeech;
        /// <summary>
        /// Control the rig's speech true/false
        /// </summary>
        public virtual bool RigSpeech
        {
            get { return _RigSpeech; }
            set { _RigSpeech = value; }
        }

        protected int _RigSpeechLevel;
        /// <summary>
        /// Control the rig's speech level
        /// </summary>
        /// <remarks>not completely implemented</remarks>
        public virtual int RigSpeechLevel
        {
            get { return _RigSpeechLevel; }
            set { _RigSpeechLevel = value; }
        }

        protected bool _AutoMode;
        /// <summary>
        /// Auto mode set true/false.
        /// </summary>
        public virtual bool AutoMode
        {
            get { return _AutoMode; }
            set { _AutoMode = value; }
        }

        /// <summary>
        /// Turn off key beeps.
        /// </summary>
        public virtual int RigBeepOff
        {
            get { return 0; }
        }
        protected int _RigBeepLevel;
        /// <summary>
        /// Control the rig's beep level, 0 = off.
        /// </summary>
        public virtual int RigBeepLevel
        {
            get { return _RigBeepLevel; }
            set { _RigBeepLevel = value; }
        }

        /// <summary>
        /// Zero beat, currently only supports CW.
        /// </summary>
        public virtual void CWZeroBeat()
        {
            // provided by the rigs.
        }

        private bool _RemoteRig = false;
        /// <summary>
        /// True if rig is on the WAN, not local.
        /// </summary>
        public bool RemoteRig
        {
            get { return _RemoteRig; }
            internal set
            {
                _RemoteRig = value;
            }
        }

        protected bool _LANAudio;
        /// <summary>
        /// True if remote audio is on for a local rig.
        /// </summary>
        public virtual bool LANAudio
        {
            get { return _LANAudio; }
            set { _LANAudio = value; }
        }
#endregion

        // region - memory stuff.
#region memory stuff
        internal enum memoryStates
        { none, inProcess, complete }
        /// <summary>
        /// Memory type enumeration
        /// </summary>
        public enum MemoryTypes
        {
            Normal,
            Range,
            MemorySwitch,
            PowerOn,
            CallChannel,
            Special
        }
        /// <summary>
        /// Get the data into the specified memory.
        /// </summary>
        /// <param name="m">memory object in the memories group, the number must be set.</param>
        /// <returns>true if gotten successfully.</returns>
        internal virtual bool getMem(MemoryData m) { return false; }
        /// <summary>
        /// Set the radio's memory.
        /// </summary>
        /// <param name="m">a memoryData object</param>
        internal virtual void setMem(MemoryData m) { }
        /// <summary>
        /// Do any rig-specific memory setup
        /// </summary>
        /// <param name="m">the memoryData</param>
        internal virtual void rigSpecificSetup(MemoryData m) { }
        /// <summary>
        /// Memory display routine
        /// </summary>
        /// <param name="mem">MemoryData object</param>
        /// <returns>string to display</returns>
        /// <remarks>This is the default, used by Kenwood rigs.</remarks>
        internal virtual string DisplayMemName(MemoryData mem)
        {
            string str = mem.Number.ToString("d3") + " ";
            if (mem.Present)
            {
                if (string.IsNullOrEmpty(mem.Name) || (mem.Name == " "))
                {
                    str += Callouts.FormatFreq(mem.Frequency[0]);
                }
                else
                {
                    str += mem.Name;
                }
            }
            else
            {
                str += Empty;
            }
            return str;
        }
        /// <summary>
        /// a memory data item.
        /// </summary>
        public class MemoryData
        {
            private AllRadios parent;
            internal Mutex myLock;
            internal byte[] RawMem; // currently used by Icom
            internal object ExternalMemory; // used by Flex.
            private byte _State;
            internal memoryStates State
            {
                get { return (memoryStates)Thread.VolatileRead(ref _State); }
                set { Thread.VolatileWrite(ref _State, (byte)value); }
            }
            internal bool complete
            {
                get { return ((memoryStates)Thread.VolatileRead(ref _State) == memoryStates.complete) ? true : false; }
            }
            //internal object tag;
            public int Number;
            public string Name;
            public string DisplayName
            {
                get { return parent.DisplayMemName(this); }
            }
            public bool Present;
            public MemoryTypes Type;
            public bool Split;
            public ulong[] Frequency;
            public ModeValue[] Mode;
            public DataModes[] DataMode;
            public object Filter; // Only used by Icom.
            public ToneCTCSSValue ToneCTCSS;
            public float ToneFrequency;
            public float CTSSFrequency;
            public ushort DCS;
            public bool Reverse;
            public OffsetDirections OffsetDirection;
            public int OffsetFrequency;
            public int StepSize;
            public int GroupID;
            public string RigSpecific;
            public int FMMode; // these are rig-specific
            public bool Lockout;
            internal MemoryData(AllRadios p)
            {
                parent = p;
                Present = false;
                State = memoryStates.none;
                myLock = new Mutex();
                Frequency = new ulong[2];
                Mode = new ModeValue[2];
                Mode[0] = new ModeValue('0');
                Mode[1] = new ModeValue('0');
                DataMode = new DataModes[2];
            }
            /// <summary>
            /// Make a copy
            /// </summary>
            /// <returns>a copy of this object</returns>
            public MemoryData Copy()
            {
                MemoryData m = new MemoryData(parent);
                Tracing.TraceLine("memory copy:" + Number.ToString(),TraceLevel.Info);
                for (int i = 0; i < Frequency.Length; i++)
                {
                    m.Frequency[i] = Frequency[i];
                    m.Mode[i] = Mode[i];
                    m.DataMode[i] = DataMode[i];
                }
                m.State = State;
                m.CTSSFrequency = CTSSFrequency;
                m.FMMode = FMMode;
                m.Lockout = Lockout;
                m.Name = Name;
                m.Number = Number;
                m.Present = Present;
                m.Split = Split;
                m.ToneCTCSS = ToneCTCSS;
                m.ToneFrequency = ToneFrequency;
                m.Reverse = Reverse;
                m.OffsetDirection = OffsetDirection;
                m.OffsetFrequency = OffsetFrequency;
                m.DCS = DCS;
                m.StepSize = StepSize;
                m.GroupID = GroupID;
                m.RigSpecific = RigSpecific;
                m.Type = Type;
                m.RawMem = RawMem;
                m.ExternalMemory = ExternalMemory;
                return m;
            }
        }
        /// <summary>
        /// The group of memories for this rig.
        /// </summary>
        public class MemoryGroup
        {
            internal MemoryData[] mems;
            private AllRadios parent;
            /// <summary>
            /// The memory bank
            /// </summary>
            public string Bank;
            private void setupGroup(int totalMemories, object p, int numOffset, string bank)
            {
                Tracing.TraceLine("MemoryGroup:" + totalMemories + ' ' + bank, TraceLevel.Info);
                parent = (AllRadios)p;
                Bank = bank;
                mems = new MemoryData[totalMemories];
                for (int i = 0; i < totalMemories; i++)
                {
                    mems[i] = new MemoryData(parent);
                    mems[i].Number = i+ numOffset;
                    // Setup fields from the parent.
                    parent.rigSpecificSetup(mems[i]);                    
                }
            }
            internal MemoryGroup(int totalMemories, object p)
            {
                setupGroup(totalMemories, p, 0, "only");
            }
            internal MemoryGroup(int totalMemories, object p, int numOffset)
            {
                setupGroup(totalMemories, p, numOffset, "only");
            }
            internal MemoryGroup(int totalMemories, object p, int numOffset, String bank)
            {
                setupGroup(totalMemories, p, numOffset, bank);
            }
            /// <summary>
            /// Get the memory for this id
            /// </summary>
            /// <param name="id">index</param>
            /// <returns>corresponding MemoryData object</returns>
            public MemoryData this[int id]
            {
                get
                {
                    // Get the memory from the radio if necessary.
                    // Return a copy of the memory.
                    Tracing.TraceLine("MemoryData get:" + id.ToString(), TraceLevel.Info);
                    MemoryData rv = null;
                    MemoryData m = mems[id];
                    if (parent.getMem(m))
                    {
                        m.myLock.WaitOne();
                        rv = m.Copy();
                        m.myLock.ReleaseMutex();
                    }
                    return rv;
                }
                set
                {
                    Tracing.TraceLine("MemoryData set:" + id.ToString(), TraceLevel.Info);
                    // We don't need to lock this now.
                    parent.setMem(value);
                }
            }
        }
        public MemoryGroup Memories;
        private bool _MemoriesLoaded;
        /// <summary>
        /// True if memories are loaded.
        /// </summary>
        public bool MemoriesLoaded
        {
            get { return _MemoriesLoaded; }
            set
            {
                _MemoriesLoaded = value;
                if (value) raiseComplete(CompleteEvents.memories);
            }
        }
        /// <summary>
        /// (readonly) number of memories, provided by the rigs.
        /// </summary>
        public virtual int NumberOfMemories
        {
            get { return Memories.mems.Length; }
        }
        protected int _CurrentMemoryChannel;
        /// <summary>
        /// Current memory channel number (0 based)
        /// </summary>
        public virtual int CurrentMemoryChannel
        {
            get { return _CurrentMemoryChannel; }
            set { _CurrentMemoryChannel = value; }
        }
        /// <summary>
        /// (ReadOnly) Memory number to display
        /// </summary>
        public virtual int CurrentMemoryNumber
        {
            get { return _CurrentMemoryChannel; }
        }
        // oldVFO is the VFO to return to when memory mode is turned off.
        protected RigCaps.VFOs oldVFO = RigCaps.VFOs.VFOA;
        /// <summary>
        /// Memory mode, true or false.
        /// </summary>
        public virtual bool MemoryMode
        {
            get { return IsMemoryMode(CurVFO); }
            // Set provided by the rigs.
            set { }
        }
        /// <summary>
        /// True if in memory mode according to specified VFO.
        /// </summary>
        /// <param name="v">the VFO</param>
        public virtual bool IsMemoryMode(RigCaps.VFOs v)
        {
            return (v == RigCaps.VFOs.None);
        }
        /// <summary>
        /// Set the specified vfo from the specified memory.
        /// </summary>
        /// <param name="n">memory number</param>
        /// <param name="vfo">vfo to set
        /// If in memory mode, the last vfo is used.
        /// </param>
        /// <returns>true if it will be set.</returns>
        public virtual bool MemoryToVFO(int n, RigCaps.VFOs vfo)
        {
            // implemented by the rigs.
            return false;
        }

        /// <summary>
        /// Memory has been selected, keep us up to date.
        /// </summary>
        /// <param name="m">memoryData object</param>
        protected void reflectMemoryData(MemoryData m)
        {
            Tracing.TraceLine("reflectMemoryData:" + m.Number.ToString(), TraceLevel.Info);
            m.myLock.WaitOne();
            _RXFrequency = m.Frequency[0];
            _TXFrequency = m.Frequency[1];
            _RXMode = m.Mode[0];
            _TXMode = m.Mode[1];
            _RXDataMode = m.DataMode[0];
            _TXDataMode = m.DataMode[1];
            _ToneCTCSS = m.ToneCTCSS;
            _ToneFrequency = m.ToneFrequency;
            _CTSSFrequency = m.CTSSFrequency;
            //fmMd = m.FMMode;
            _OffsetDirection = m.OffsetDirection;
            _OffsetFrequency = m.OffsetFrequency;
            _DCS = m.DCS;
            //reverseVal = m.Reverse;
            actuateRigSpecific(m);
            m.myLock.ReleaseMutex();
        }
        /// <summary>
        /// Set any rig-specific data from a memory.
        /// </summary>
        /// <param name="m">memoryData</param>
        protected virtual void actuateRigSpecific(MemoryData m)
        {
            // provided by the rigs.
        }

        // Call channel.
        protected delegate void setCallDel(int id);
        protected setCallDel setCallChannelActive;
        public class CallChannelType
        {
            public int ID;
            private AllRadios parent;
            internal bool _Active;
            public bool Active
            {
                get { return _Active; }
                set { if (parent.setCallChannelActive != null) parent.setCallChannelActive(ID); }
            }
            public MemoryData Item;
            internal CallChannelType(AllRadios p, int id)
            {
                parent = p;
                ID = id;
                Item = new MemoryData(parent);
            }
        }
        public CallChannelType[] CallChannels;
        public CallChannelType CallChannel;

        /// <summary>
        /// Memory scan group
        /// </summary>
        public class ScanGroup
        {
            public string Name { get; set; }
            public string Bank { get; set; }
            public List<MemoryData> Members;
            public bool Readonly; // false for a user-group
            public ScanGroup(string name, String bank, List<MemoryData> members)
            {
                Name = name;
                Bank = bank;
                Members = members;
            }
            internal ScanGroup(string name, String bank, List<MemoryData> members, bool rdonly)
            {
                Name = name;
                Bank = bank;
                Members = members;
                Readonly = rdonly;
            }
            public ScanGroup(ExternalScanGroup group, AllRadios parent)
            {
                Name = group.Name;
                Bank = group.Bank;
                Readonly = false; // a user group.
                Dictionary<string, MemoryData> name2mem = new Dictionary<string, MemoryData>();
                foreach (MemoryData m in parent.Memories.mems)
                {
                    if (!name2mem.ContainsKey(m.DisplayName))
                    {
                        name2mem.Add(m.DisplayName, m);
                    }
                }
                Members = new List<MemoryData>();
                foreach (string name in group.Members)
                {
                    if (name2mem.ContainsKey(name))
                    {
                        Members.Add(name2mem[name]);
                    }
                }
            }
        }
        /// <summary>
        /// Externalized scan group.
        /// </summary>
        public class ExternalScanGroup
        {
            public string Name { get; set; }
            public string Bank { get; set; }
            public List<string> Members;
            public ExternalScanGroup() { }
            public ExternalScanGroup(ScanGroup group)
            {
                Name = group.Name;
                Bank = group.Bank;
                Members = new List<string>();
                foreach (MemoryData m in group.Members)
                {
                    Members.Add(m.DisplayName);
                }
            }
        }
        /// <summary>
        /// Get reserved scan groups, default is none.
        /// </summary>
        /// <returns>Array of ScanGroup or null.</returns>
        public virtual List<ScanGroup> GetReservedGroups()
        {
            return null;
        }
#endregion

        // region - menu stuff
        #region menu stuff
        /// <summary>
        /// menu item type
        /// </summary>
        public enum MenuTypes
        {
            /// <summary>
            /// 0=Off,1=On, no descriptor
            /// </summary>
            OnOff,
            /// <summary>
            /// number range, takes a NumberRangeDescriptor
            /// </summary>
            NumberRange,
            /// <summary>
            /// number range where 0 is off, takes an int for highest value.
            /// </summary>
            NumberRangeOff0,
            /// <summary>
            /// Enumerated items, takes an EnumeratedDescriptor.
            /// </summary>
            Enumerated,
            /// <summary>
            /// string of text, takes a string.
            /// </summary>
            Text,
            /// <summary>
            /// submenu type 1
            /// </summary>
            SubMenu1,
            /// <summary>
            /// submenu type 2
            /// </summary>
            SubMenu2
        }
        /// <summary>
        /// Description and associated value.
        /// If Value is null, use relative position between Low and High.
        /// </summary>
        public class EnumAndValue
        {
            public string Description;
            public MenuTypes Type;
            public object Value;
            public EnumAndValue() { }
            /// <summary>
            /// EnumAndValue object with a numeric value and type of Enumerated.
            /// </summary>
            /// <param name="desc">description</param>
            /// <param name="v">integral value</param>
            public EnumAndValue(string desc, int v)
            {
                Description = desc;
                Value = v;
                Type = MenuTypes.Enumerated;
            }
            /// <summary>
            /// EnumAndValue object with a specified type and value.
            /// </summary>
            /// <param name="desc">description</param>
            /// <param name="t">value type</param>
            /// <param name="v">value</param>
            public EnumAndValue(string desc, MenuTypes t, object v)
            {
                Description = desc;
                Type = t;
                Value = v;
            }
        }
        internal delegate object getDel(MenuDescriptor md);
        internal delegate void setDel(MenuDescriptor md, object v);
        internal class menuStatics
        {
            public int Number;
            public MenuTypes Type;
            public string Description;
            public int Low;
            public int High;
            public int Base;
            public EnumAndValue[] Enumerants;
            public MenuDescriptor[] subMenus;
            /// <summary>
            /// statics
            /// </summary>
            /// <param name="n">number</param>
            /// <param name="desc">description</param>
            /// <param name="typ">type</param>
            /// <param name="l">low</param>
            /// <param name="h">high</param>
            /// <param name="b">base</param>
            /// <param name="e">string array</param>
            /// <param name="ev">enumAndValue array</param>
            /// <param name="subs">submenu array</param>
            private void menuStaticsSetup(int n, string desc,
                MenuTypes typ, int l, int h, int b, string[] e,
                EnumAndValue[] ev, MenuDescriptor[] subs)
            {
                Tracing.TraceLine("menuStaticsSetup:" + n.ToString(), TraceLevel.Verbose);
                Number = n;
                Description = desc;
                Type = typ;
                Low = l;
                High = h;
                Base = b;
                if (e != null)
                {
                    // enumerants passed as strings, values are zero-based.
                    Enumerants = new EnumAndValue[e.Length];
                    for (int i = 0; i < e.Length; i++)
                    {
                        Enumerants[i] = new EnumAndValue(e[i], i + Low);
                    }
                }
                else Enumerants = ev; // might be null.
                subMenus = subs;
            }
            internal menuStatics(int n, string desc,
                MenuTypes typ, int l, int h)
            {
                menuStaticsSetup(n, desc, typ, l, h, 0, null, null, null);
            }
            internal menuStatics(int n, string desc,
                MenuTypes typ, int l, int h, int b)
            {
                menuStaticsSetup(n, desc, typ, l, h, b, null, null, null);
            }
            internal menuStatics(int n, string desc,
                MenuTypes typ, int l, int h, string[] e)
            {
                menuStaticsSetup(n, desc, typ, l, h, 0, e, null, null);
            }
            internal menuStatics(int n, string desc,
                MenuTypes typ, int l, int h, int b, string[] e)
            {
                menuStaticsSetup(n, desc, typ, l, h, b, e, null, null);
            }
            internal menuStatics(int n, string desc,
                MenuTypes typ, int l, int h, EnumAndValue[] e)
            {
                menuStaticsSetup(n, desc, typ, l, h, 0, null, e, null);
            }
            internal menuStatics(int n, string desc,
                MenuTypes typ, int subL, int subH, MenuDescriptor[] md)
            {
                menuStaticsSetup(n, desc, typ, subL, subH, 0, null, null, md);
            }
        }
        /// <summary>
        /// menu descriptor class
        /// </summary>
        public class MenuDescriptor
        {
            private byte cmplt;
            /// <summary>
            /// Internal:  true if the menu has been read in.
            /// </summary>
            internal bool Complete
            {
                get { return (Thread.VolatileRead(ref cmplt) != 0); }
                set { Thread.VolatileWrite(ref cmplt, (byte)((value)?1:0)); }
            }
            /// <summary>
            /// String to use for rig's command.
            /// </summary>
            internal string commandString;
            private menuStatics fixedStuff;
            /// <summary>
            /// menu number
            /// </summary>
            public int Number { get { return fixedStuff.Number; } }
            /// <summary>
            /// type enumeration, see MenuTypes
            /// </summary>
            public MenuTypes Type { get { return fixedStuff.Type; } }
            /// <summary>
            /// True if this has submenus.
            /// </summary>
            public bool HasSubMenus
            {
                get { return ((Type == MenuTypes.SubMenu1) || (Type == MenuTypes.SubMenu2)); }
            }
            private bool isSub = false;
            /// <summary>
            /// True if this is a submenu.
            /// </summary>
            public bool isSubMenu
            {
                get { return isSub; }
                internal set { isSub = value; }
            }
            /// <summary>
            /// text description
            /// </summary>
            public string Description { get { return fixedStuff.Description; } }
            /// <summary>
            /// low value
            /// </summary>
            public int Low { get { return fixedStuff.Low; } }
            /// <summary>
            /// high value
            /// </summary>
            public int High
            { 
                get { return fixedStuff.High; }
                internal set { fixedStuff.High = value; }
            }
            internal int Base { get { return fixedStuff.Base; } }
            /// <summary>
            /// enumerated descriptors
            /// </summary>
            public EnumAndValue[] Enumerants
            { 
                get { return fixedStuff.Enumerants; }
                internal set { fixedStuff.Enumerants = value; }
            }
            /// <summary>
            /// submenus
            /// </summary>
            public MenuDescriptor[] subMenus
            {
                get { return fixedStuff.subMenus; }
                internal set { fixedStuff.subMenus = value; }
            }
            /// <summary>
            /// Routine to get the value.
            /// This field must be dynamically initialized.
            /// </summary>
            internal getDel getRtn;
            /// <summary>
            /// routine to set the value.
            /// This field must be dynamically initialized.
            /// </summary>
            internal setDel setRtn;
            internal object val;
            /// <summary>
            /// menu's current value.
            /// This may need to await a command response.
            /// </summary>
            public object Value
            {
                get { return getRtn(this); }
                set
                {
                    // setRtn should return when the menu is really set by the rig.
                    if (setRtn != null) setRtn(this, value);
                }
            }
            /// <summary>
            /// constructor for a first-level menu without children.
            /// </summary>
            /// <param name="ms">fixed data</param>
            internal MenuDescriptor(menuStatics ms)
            {
                Tracing.TraceLine("MenuDescriptor:" + ms.Number.ToString(), TraceLevel.Verbose);
                Complete = false;
                fixedStuff = ms;
            }
        }
        /// <summary>
        /// Menu arrays (bank, menu).
        /// </summary>
        public MenuDescriptor[,] Menus;
        /// <summary>
        /// (ReadOnly) number of menus
        /// </summary>
        public virtual int NumberOfMenus
        {
            get { return ((Menus != null) ? Menus.GetLength(1) : 0); }
        }
        public int MenuBanks
        {
            get { return ((Menus != null) ? Menus.GetLength(0) : 0); }
        }
        public const int MenuBankNotSetup = -1;
        protected int _MenuBank = MenuBankNotSetup;
        public virtual int MenuBank
        {
            get { return _MenuBank; }
            set { _MenuBank = value; }
        }
#endregion

        // region - events
#region events
        /// <summary>
        /// Events that report "Complete".
        /// </summary>
        public enum CompleteEvents
        {
            memoriesStart,
            memories,
            menus
        }
        /// <summary>
        /// Complete event arguments
        /// </summary>
        public class CompleteEventArgs
        {
            public CompleteEvents TheEvent;
            public DateTime TheTime;
            internal CompleteEventArgs(CompleteEvents ev)
            {
                TheEvent = ev;
                TheTime = DateTime.Now;
            }
        }
        public delegate void CompleteHandler(object sender, CompleteEventArgs e);
        /// <summary>
        /// Complete event
        /// </summary>
        public event CompleteHandler CompleteEvent;
        /// <summary>
        /// raise the complete event for this event.
        /// </summary>
        /// <param name="ev">event from CompleteEvents</param>
        protected void raiseComplete(CompleteEvents ev)
        {
            if (CompleteEvent != null)
            {
                Tracing.TraceLine("raiseEvent:" + ev.ToString(), TraceLevel.Info);
                CompleteEvent(this, new CompleteEventArgs(ev));
            }
            else
            {
                Tracing.TraceLine("raiseEvent:" + ev.ToString() + " no handler", TraceLevel.Error);
            }
        }
#endregion

        public AllRadios()
        {
            Tracing.TraceLine("AllRadios constructor", TraceLevel.Info);
            IsOpen = false;
            Callouts = new OpenParms();

            // Setup for 1 call channel, which can't be activated.
            CallChannels = new CallChannelType[1];
            CallChannels[0] = new CallChannelType(this, 0);
            CallChannel = CallChannels[0];
        }

        /// <summary>
        /// Handler for string data from the rig.
        /// </summary>
        public HandRtn InterruptHandler;
        public HandBytesRtn IBytesHandler;

        /// <summary>
        /// Internal routine to get radio's memories.
        /// </summary>
        protected virtual void GetMemories()
        {
            // provided by the rigs.
        }

        /// <summary>
        /// routine to send string data to the rig.
        /// </summary>
        /// <param name="str">data to send</param>
        /// <returns>True if sent</returns>
        public delegate bool SendRtn(string str);
        public delegate bool SendBytesRtn(byte[] bytes);
        /// <summary>
        /// Receives direct rig output
        /// </summary>
        /// <param name="o">data from the rig</param>
        public delegate void DDRcvr(object o);
        /// <summary>
        /// Receive decoded CW.
        /// </summary>
        /// <param name="txt">the text string</param>
        public delegate void DCWText(string txt);
        /// <summary>
        /// Process returned string data
        /// </summary>
        /// <param name="str">the data</param>
        public delegate void HandRtn(string str);
        public delegate void HandBytesRtn(byte[] bytes, int len);
        /// <summary>
        /// Format the frequency for display
        /// </summary>
        /// <param name="freq">a ulong</param>
        /// <returns>string to display</returns>
        public delegate string FormatFreqDel(ulong freq);
        /// <summary>
        /// format a frequency string for the radio.
        /// </summary>
        /// <param name="str"></param>
        /// <returns>a ulong frequency</returns>
        public delegate ulong FormatFreqForRadioDel(string str);
        /// <summary>
        /// reackquire the rig's values
        /// </summary>
        public delegate void ReackquireRigDel();
        /// <summary>
        /// Get the displayable SWR.
        /// </summary>
        /// <returns>SWR string</returns>
        public delegate string GetSWRTextDel();
        /// <summary>
        /// rig-dependent next value of this field.
        /// </summary>
        public delegate void NextValue1Del();
        /// <summary>
        /// rig dependent data type
        /// Usually used for rigs with subreceivers and the like.
        /// </summary>
        public class RigDependent
        {
            internal delegate bool GetActiveDel();
            internal delegate void SetActiveDel(bool val);
            /// <summary>
            /// get active status.
            /// </summary>
            internal GetActiveDel GetActive;
            /// <summary>
            /// Routine to set activve on/off.
            /// </summary>
            internal SetActiveDel SetActive;
            public bool Active
            {
                get { return GetActive(); }
                set { SetActive(value); }
            }
            public class entries
            {
                // First two are the up/down arrow commands, (e.g.) sub or main.
                // If more than two are used, the set routine must set value to one of the first two.
                private char[] items;
                public char this[int id]
                {
                    get { return (id < items.Length)? items[id] : ' '; }
                }
                internal entries(char[] vals)
                {
                    items = vals;
                }
                public int Length { get { return items.Length; } }
            }
            /// <summary>
            /// readOnly available members array
            /// </summary>
            public entries Members;
            private int id; // tell rig which field to use.
            internal delegate char getDel(int id);
            internal delegate void setDel(char c, int i);
            internal getDel getRtn;
            internal setDel setRtn;
            /// <summary>
            /// single character value
            /// </summary>
            public char value
            {
                get { return getRtn(id); }
                set { setRtn(value, id); }
            }
            internal RigDependent(char[] items, int i,
                getDel get, setDel set, 
                GetActiveDel ga, SetActiveDel sa)
            {
                Members = new entries(items);
                id = i;
                getRtn = get;
                setRtn = set;
                GetActive = ga;
                SetActive = sa;
            }
        }

        /// <summary>
        /// Callout vector
        /// </summary>
        public class OpenParms
        {
            /// <summary>
            /// Send commands to the radio
            /// </summary>
            public SendRtn SendRoutine { get; set; }
            public SendBytesRtn SendBytesRoutine { get; set; }
            public bool RawIO = false;
            /// <summary>
            /// Optional description for the "next value 1" prompt (used by Kenwood rigs).
            /// </summary>
            public string NextValue1Description { get; set; }
            /// <summary>
            /// routine to receive raw data
            /// </summary>
            public DDRcvr DirectDataReceiver { get; set; }
            public DCWText CWTextReceiver { get; set; }
            internal void safeSend(string str)
            {
                // tracing is in the send routine
                try { SendRoutine(str); }
                catch (Exception ex)
                { Tracing.ErrMessageTrace(ex, false, false); }
            }
            internal void safeSendBytes(byte[] bytes)
            {
                // tracing is in the send routine
                try { SendBytesRoutine(bytes); }
                catch (Exception ex)
                { Tracing.ErrMessageTrace(ex, false, false); }
            }
            internal void safeReceiver(object o)
            {
                try { DirectDataReceiver(o); }
                catch (Exception ex)
                { Tracing.ErrMessageTrace(ex, false, false); }
            }
            internal void safeCWTextReceiver(string txt)
            {
                try { CWTextReceiver(txt); }
                catch (Exception ex)
                { Tracing.ErrMessageTrace(ex, false, false); }
            }
            /// <summary>
            /// Format a frequency for display
            /// </summary>
            public FormatFreqDel FormatFreq;
            /// <summary>
            /// format a string frequency for the radio
            /// </summary>
            public FormatFreqForRadioDel FormatFreqForRadio;
            /// <summary>
            /// Go to the home field.
            /// </summary>
            public delegate void GotoHomeDel();
            /// <summary>
            /// Go to the home field.
            /// </summary>
            public GotoHomeDel GotoHome;
            /// <summary>
            /// Configuration directory
            /// </summary>
            public string ConfigDirectory;
            /// <summary>
            /// Name of audio device selection file.
            /// </summary>
            public string AudioDevicesFile;
            public delegate string GetOperatorNameDel();
            /// <summary>
            /// Function to retrieve the current operator's name.
            /// </summary>
            public GetOperatorNameDel GetOperatorName;
            internal string OperatorName { get { return GetOperatorName(); } }
            /// <summary>
            /// Braille display cells
            /// </summary>
            public int BrailleCells;
            /// <summary>
            /// Operator's license class.
            /// </summary>
            public Bands.Licenses License;
            /// <summary>
            /// Send CW with no preprocessing.
            /// </summary>
            public bool DirectSend;
            /// <summary>
            /// Rig dependent field 1
            /// </summary>
            public RigDependent RigField1 = null;
            /// <summary>
            /// Rig dependent field 2
            /// </summary>
            public RigDependent RigField2 = null;
            /// <summary>
            /// For rigs discovered on the network, local or WAN.
            /// </summary>
            public RadioDiscoveredEventArgs NetworkRadio;
            /// <summary>
            /// True if the rig handles it's own panning.
            /// </summary>
            public bool RigDoesPanning;
            /// <summary>
            /// Set if the main program can go directly to the panning field.
            /// </summary>
            public Control PanField;
            /// <summary>
            /// Get the displayable SWR.
            /// </summary>
            public GetSWRTextDel GetSWRText = null;
            /// <summary>
            /// Reackquire the rig's status
            /// </summary>
            public ReackquireRigDel ReackquireRig;
            /// <summary>
            /// rig-dependent next value.
            /// </summary>
            public NextValue1Del NextValue1;
            /// <summary>
            /// True to allow access via internet.
            /// </summary>
            public bool AllowRemote;
        }
        /// <summary>
        /// Callout vector provided at open().
        /// </summary>
        public OpenParms Callouts;
        internal string ConfigDirectory { get { return Callouts.ConfigDirectory; } }
        internal string OperatorName { get { return Callouts.OperatorName; } }
        /// <summary>
        /// Operator's directory for rig-specific stuff.
        /// </summary>
        internal string OperatorsDirectory { get { return ConfigDirectory + "\\" + OperatorName; } }

        // Formatters from callouts.
        internal static FormatFreqDel FormatFreq;

        /// <summary>
        /// Open the radio.
        /// The serial communication port must be open.
        /// </summary>
        /// <param name="p">Callout vector, see the OpenParms class.</param>
        /// <returns>True on success</returns>
        public virtual bool Open(OpenParms p)
        {
            Tracing.TraceLine("AllRadios Open", TraceLevel.Info);
            Callouts = p;
            FormatFreq = p.FormatFreq;
            _RIT = new RITData();
            _XIT = new RITData();
            IsOpen = true;
            return true;
        }

        /// <summary>
        /// Close.
        /// Call after inherritor's close has finished.
        /// </summary>
        public virtual void close()
        {
            Tracing.TraceLine("AllRadios close", TraceLevel.Info);
            raisePowerOff();

            try { PanStop(); }
            catch (Exception ex)
            {
                Tracing.TraceLine("AllRadios close PanStop exception:" + ex.Message, TraceLevel.Error);
            }

            IsOpen = false;
        }

        /// <summary>
        /// True if open.
        /// </summary>
        public bool IsOpen
        {
            get;
            protected set;
        }

        internal delegate bool awaitExp();
        /// <summary>
        /// Await the specified condition.
        /// </summary>
        /// <param name="exp">function that returns the condition</param>
        /// <param name="ms">milliseconds to wait.</param>
        /// <param name="interval">optional interval to check</param>
        /// <returns>true if condition met.</returns>
        internal static bool await(awaitExp exp, int ms, int interval)
        {
            int sanity = ms / interval;
            bool rv = false;
            while (sanity-- > 0)
            {
                rv = exp();
                if (rv) break;
                Thread.Sleep(interval);
            }
            return rv;
        }
        internal static bool await(awaitExp exp, int ms)
        {
            return await(exp, ms, 25);
        }

        internal int RoundInt(int val, int step)
        {
            int rounder = (val >= 0) ? step / 2 : -step / 2;
            int rv = ((val + rounder) / step) * step;
            return rv;
        }
    }

    // region - radio selection
    #region RadioSelection
    /// <summary>
    /// Used to select the radio by model number
    /// </summary>
    public static class RadioSelection
    {
        // supported radio ids (Flex-only)
        public const int RIGIDNone = 0;
        public const int RIGIDFlex = 900;
        public const int RIGIDFlex6300 = RIGIDFlex + 7;
        public const int RIGIDFlex6400 = RIGIDFlex + 8;
        public const int RIGIDFlex6500 = RIGIDFlex + 9;
        public const int RIGIDFlex6600 = RIGIDFlex + 10;
        public const int RIGIDFlex6700 = RIGIDFlex + 11;
        public const int RIGIDFlex8600 = RIGIDFlex + 12;
        public const int RIGIDAurora = RIGIDFlex + 13;

        public enum ComType
        {
            serial,
            network
        }

        /// <summary>
        /// Communication defaults for a rig
        /// </summary>
        public class ComDefaults
        {
            public ComType ComType;
            public int Baud { get; private set; }
            public Parity Parity { get; private set; }
            public int DataBits { get; private set; }
            public int StopBits { get; private set; }
            public Handshake Handshake { get; private set; }
            public bool ExposeBaud { get; private set; }
            public bool ExposeCom { get; private set; }
            internal ComDefaults(int b, Parity p, int db, int sb, Handshake h,
                bool eb, bool ec)
            {
                ComType = ComType.serial;
                Baud = b;
                Parity = p;
                DataBits = db;
                StopBits = sb;
                Handshake = h;
                ExposeBaud = eb;
                ExposeCom = ec;
            }
            internal ComDefaults()
            {
                ComType = ComType.network;
            }
        }

        /// <summary>
        /// The rig names and ids.
        /// </summary>
        public class RigElement
        {
            public int id { get; private set; }
            public string name { get; private set; }
            public ComDefaults ComDefaults { get; private set; }
            internal System.Type RadioCode;
            public RigElement(int num, string nam, System.Type r,
                ComDefaults dflt)
            {
                id = num;
                name = nam;
                RadioCode = r;
                ComDefaults = dflt;
            }
        }

        private static ComDefaults FlexComDefaults = new ComDefaults();

        /// <summary>
        /// Array of supported rigs.
        /// </summary>
        public static RigElement[] RigTable =
            {
                // Flex-only for JJFlex modernization
                new RigElement(RIGIDFlex6300, "Flex6300", typeof(FlexRadio), FlexComDefaults),
                new RigElement(RIGIDFlex6400, "Flex6400/6400M", typeof(FlexRadio), FlexComDefaults),
                new RigElement(RIGIDFlex6500, "Flex6500", typeof(FlexRadio), FlexComDefaults),
                new RigElement(RIGIDFlex6600, "Flex6600/6600M", typeof(FlexRadio), FlexComDefaults),
                new RigElement(RIGIDFlex6700, "Flex6700/6700R", typeof(FlexRadio), FlexComDefaults),
                new RigElement(RIGIDFlex8600, "Flex8600/8600M", typeof(FlexRadio), FlexComDefaults),
                new RigElement(RIGIDAurora, "Flex Aurora (A520/A520M)", typeof(FlexRadio), FlexComDefaults),
            };

        /// <summary>
        /// Get an object for the specified rig.
        /// </summary>
        /// <param name="id">model number</param>
        /// <returns>The AllRadios object for the rig.</returns>
        public static AllRadios GetRig(int id)
        {
            foreach (RigElement t in RigTable)
            {
                if (id == t.id)
                {
                    return (AllRadios)Activator.CreateInstance(t.RadioCode, new object[] {});
                }
            }
            return null;
        }
    }
    #endregion
}
