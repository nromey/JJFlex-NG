using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using JJTrace;
using RadioBoxes;

namespace Radios
{
    public partial class ic9100filters : UserControl
    {
        protected Icom9100 rig;
        private Collection<Combo> combos;
        private Collection<NumberBox> numberBoxes;
        private Collection<InfoBox> infoBoxes;

        private delegate bool compDel();
        private class subDependentType
        {
            public Control target;
            public Control predicate;
            public compDel Comparer;
            public subDependentType(Control t, Control p, compDel c)
            {
                target = t;
                predicate = p;
                Comparer = c;
            }
        }
        private Collection<subDependentType> subDependents;

        internal class filterElement
        {
            private int val;
            public string Display { get { return val.ToString(); } }
            public int RigItem { get { return val; } }
            public filterElement(int v)
            {
                val = v;
            }
        }
        private filterElement[] filterValues =
        {
            new filterElement(1),
            new filterElement(2),
            new filterElement(3),
        };
        private ArrayList filterList;

        internal class widthElement
        {
            private int val;
            public string Display { get { return val.ToString(); } }
            public int RigItem { get { return val; } }
            public widthElement(int v)
            {
                val = v;
            }
        }
        private ArrayList CWSSBWidthList, AMWidthList;

        internal class filterTypeElement
        {
            private Icom.FilterTypes val;
            public string Display { get { return val.ToString(); } }
            public Icom.FilterTypes RigItem { get { return val; } }
            public filterTypeElement(Icom.FilterTypes v)
            {
                val = v;
            }
        }
        private ArrayList filterTypeList;

        internal class keyerElement
        {
            private Icom.KeyerValues val;
            public string Display { get { return val.ToString(); } }
            public int RigItem { get { return (int)val; } }
            public keyerElement(Icom.KeyerValues v)
            {
                val = v;
            }
        }
        internal static keyerElement[] keyerValues =
        {
            new keyerElement(Icom.KeyerValues.Key),
            new keyerElement(Icom.KeyerValues.Bug),
            new keyerElement(Icom.KeyerValues.Keyer),
        };
        private ArrayList keyerList;

        internal class firstIFElement
        {
            private string val;
            public string Display { get { return val; } }
            public string RigItem { get { return val; } }
            public firstIFElement(string v)
            {
                val = v;
            }
        }
        internal static firstIFElement[] firstIFValues =
        {
            new firstIFElement(Icom.firstIFSizes[0]),
            new firstIFElement(Icom.firstIFSizes[1]),
            new firstIFElement(Icom.firstIFSizes[2]),
        };
        private ArrayList firstIFList;

        internal class SSBTransmitBandwidthElement
        {
            Icom.SSBTransmitBandwidthValues val;
            public string Display { get { return val.ToString(); } }
            public Icom.SSBTransmitBandwidthValues RigItem { get { return val; } }
            public SSBTransmitBandwidthElement(Icom.SSBTransmitBandwidthValues v)
            {
                val = v;
            }
        }
        private ArrayList compList, NBList, SSBTransmitBandwidthList, nrList;

        // TX bandwidth - format 4-bits low, 4-bits high.
        internal static byte[] TXBandwidthValues =
        {
            0x01, 0x02, 0x03,
            0x10, 0x11, 0x12, 0x13,
            0x20, 0x21, 0x22, 0x23,
            0x30, 0x31, 0x32, 0x33
        };
        internal static string[] TXBLows =
        {
            "100",
            "200",
            "300",
            "500"
        };
        internal static string[] TXBHighs =
        {
            "2500",
            "2700",
            "2800",
            "2900"
        };
        internal class TXBandwidthElement
        {
            byte val;
            public string Display
            {
                get
                {
                    int l = val & 0xf;
                    int h = val / 16;
                    return TXBLows[l] + " - " + TXBHighs[h];
                }
            }
            public byte RigItem { get { return val; } }
            internal TXBandwidthElement(byte b)
            {
                val = b;
            }
        }
        private ArrayList TXBandwidthList, monitorList;

        internal class AGCElement
        {
            Icom.IcomAGCValues val;
            public string Display { get { return val.ToString(); } }
            public Icom.IcomAGCValues RigItem { get { return val; } }
            public AGCElement(Icom.IcomAGCValues v)
            {
                val = v;
            }
        }
        private ArrayList AGCList, FMAGCList;

        internal class NotchElement
        {
            Icom.NotchValues val;
            public string Display { get { return val.ToString(); } }
            public Icom.NotchValues RigItem { get { return val; } }
            public NotchElement(Icom.NotchValues v)
            {
                val = v;
            }
        }
        internal class NotchWidthElement
        {
            Icom.NotchWidthValues val;
            public string Display { get { return val.ToString(); } }
            public Icom.NotchWidthValues RigItem { get { return val; } }
            public NotchWidthElement(Icom.NotchWidthValues v)
            {
                val = v;
            }
        }
        private ArrayList SSBNotchList, manualNotchList, notchWidthList, FMNotchList;

        internal class VoiceDelayElement
        {
            Icom.VoiceDelayValues val;
            public string Display { get { return val.ToString(); } }
            public Icom.VoiceDelayValues RigItem { get { return val; } }
            public VoiceDelayElement(Icom.VoiceDelayValues v)
            {
                val = v;
            }
        }
        private ArrayList voiceDelayList;

        internal class ToneModeElement
        {
            Icom.ToneTypes val;
            public string Display { get { return val.ToString(); } }
            public Icom.ToneTypes RigItem { get { return val; } }
            public ToneModeElement(Icom.ToneTypes v)
            {
                val = v;
            }
        }
        private ArrayList toneModeList;

        internal class ToneFrequencyElement
        {
            float val;
            public float Display { get { return val; } }
            public float RigItem { get { return val; } }
            public ToneFrequencyElement(float v)
            {
                val = v;
            }
        }
        private ArrayList ToneFrequencyList;

        internal class HFPreampElement
        {
            Icom.HFPreampType val;
            public string Display { get { return val.ToString(); } }
            public Icom.HFPreampType RigItem { get { return val; } }
            public HFPreampElement(Icom.HFPreampType v)
            {
                val = v;
            }
        }
        private ArrayList attenuatorList, HFPreampList, UHFPreampList;

        private ArrayList AFCList, AFCLimitList, XmitMonitorList;

        private class tuningStepElement
        {
            private int val;
            public string Display { get { return val.ToString(); } }
            public int RigItem { get { return val; } }
            public tuningStepElement(int v)
            {
                val = v;
            }
        }
        private ArrayList tuningStepList;

        // Used to sort Controls in order of their .Text.
        private class mySortClass : IComparer
        {
            int IComparer.Compare(object x, object y)
            {
                Control c1 = (Control)x;
                Control c2 = (Control)y;
                return string.Compare((string)c1.Tag, (string)c2.Tag);
            }
        }

        public ic9100filters(Icom9100 r)
        {
            InitializeComponent();
            rig = r;
            Tracing.TraceLine("IC9100Filters constructor", TraceLevel.Info);

            // setup the boxes
            combos = new Collection<Combo>();
            numberBoxes = new Collection<NumberBox>();
            infoBoxes = new Collection<InfoBox>();
            subDependents = new Collection<subDependentType>();

            // breakin delay (we use ms, but values are in ds)
            BkinDelayControl.LowValue = Icom.BreakinDelayMin;
            BkinDelayControl.HighValue = Icom.BreakinDelayMax;
            BkinDelayControl.Increment = Icom.BreakinDelayIncrement;
            BkinDelayControl.UpdateDisplayFunction =
                () => { return rig.BreakinDelay; };
            BkinDelayControl.UpdateRigFunction =
                (int v) => { rig.BreakinDelay = v; };
            numberBoxes.Add(BkinDelayControl);

            // Keyer
            keyerList=new ArrayList();
            keyerList.AddRange(keyerValues);
            KeyerControl.TheList = keyerList;
            KeyerControl.UpdateDisplayFunction =
                () => { return rig.Keyer; };
            KeyerControl.UpdateRigFunction =
                (object v) => { rig.Keyer = (Icom.KeyerValues)v; };
            combos.Add(KeyerControl);

            // keyer speed
            KeyerSpeedControl.LowValue = Icom.KeyerSpeedMin;
            KeyerSpeedControl.HighValue = Icom.KeyerSpeedMax;
            KeyerSpeedControl.Increment = 1;
            KeyerSpeedControl.UpdateDisplayFunction =
                () => { return rig.KeyerSpeed; };
            KeyerSpeedControl.UpdateRigFunction =
                (int v) => { rig.KeyerSpeed = v; };
            numberBoxes.Add(KeyerSpeedControl);

            // CW pitch
            CWPitchControl.LowValue = Icom.CWPitchMin;
            CWPitchControl.HighValue = Icom.CWPitchMax;
            CWPitchControl.Increment = Icom.CWPitchIncrement;
            CWPitchControl.UpdateDisplayFunction =
                () => { return rig.CWPitch; };
            CWPitchControl.UpdateRigFunction =
                (int v) => { rig.CWPitch = v; };
            numberBoxes.Add(CWPitchControl);

            // sidetone volume
            SidetoneGainControl.LowValue = Icom.SidetoneGainMin;
            SidetoneGainControl.HighValue = Icom.SidetoneGainMax;
            SidetoneGainControl.Increment = Icom.SidetoneGainIncrement;
            SidetoneGainControl.UpdateDisplayFunction =
                () => { return rig.SidetoneGain; };
            SidetoneGainControl.UpdateRigFunction =
                (int v) => { rig.SidetoneGain = v; };
            numberBoxes.Add(SidetoneGainControl);

            // Filter
            filterList = new ArrayList();
            filterList.AddRange(filterValues);
            FilterControl.TheList = filterList;
            FilterControl.UpdateDisplayFunction =
                () => { return rig.Filter; };
            FilterControl.UpdateRigFunction =
                (object v) => { rig.Filter = (int)v; };
            combos.Add(FilterControl);

            // CW/SSB filter width
            CWSSBWidthList = new ArrayList();
            for (int i = 0; ; i++)
            {
                int val = rig.getFilterWidth(i, Icom.myModeTable[(int)Icom.modes.usb]);
                if (val == -1) break;
                CWSSBWidthList.Add(new widthElement(val));
            }
            CWSSBWidthControl.TheList = CWSSBWidthList;
            CWSSBWidthControl.UpdateDisplayFunction =
                () => { return rig.FilterWidth; };
            CWSSBWidthControl.UpdateRigFunction =
                (object v) => { rig.FilterWidth = (int)v; };
            combos.Add(CWSSBWidthControl);

            // AM filter width
            AMWidthList = new ArrayList();
            for (int i = 0; ; i++)
            {
                int val = rig.getFilterWidth(i, Icom.myModeTable[(int)Icom.modes.am]);
                if (val == -1) break;
                AMWidthList.Add(new widthElement(val));
            }
            AMWidthControl.TheList = AMWidthList;
            AMWidthControl.UpdateDisplayFunction =
                () => { return rig.FilterWidth; };
            AMWidthControl.UpdateRigFunction =
                (object v) => { rig.FilterWidth = (int)v; };
            combos.Add(AMWidthControl);

            // filter type
            filterTypeList = new ArrayList();
            for (int i = 0; i < Enum.GetValues(typeof(Icom.FilterTypes)).Length; i++)
            {
                filterTypeList.Add(new filterTypeElement((Icom.FilterTypes)i));
            }
            FilterTypeControl.TheList=filterTypeList;
            FilterTypeControl.UpdateDisplayFunction =
                () => { return rig.FilterType; };
            FilterTypeControl.UpdateRigFunction =
                (object v) => { rig.FilterType = (Icom.FilterTypes)v; };
            combos.Add(FilterTypeControl);

            // transmit power
            XmitPowerControl.LowValue = 0;
            XmitPowerControl.HighValue = rig.MyPower;
            XmitPowerControl.Increment = 1;
            XmitPowerControl.UpdateDisplayFunction =
                () => { return rig.XmitPower; };
            XmitPowerControl.UpdateRigFunction =
                (int v) => { rig.XmitPower = v; };
            numberBoxes.Add(XmitPowerControl);

            // 1st IF filter
            firstIFList = new ArrayList();
            firstIFList.AddRange(firstIFValues);
            FirstIFControl.TheList = firstIFList;
            FirstIFControl.UpdateDisplayFunction =
                () => { return rig.FirstIF; };
            FirstIFControl.UpdateRigFunction =
                (object v) => { rig.FirstIF = (string)v; };
            combos.Add(FirstIFControl);

            // Inner PBT
            InnerPBTControl.LowValue = Icom.InnerPBTMin;
            InnerPBTControl.HighValue = Icom.InnerPBTMax;
            InnerPBTControl.Increment = Icom.InnerPBTIncrement;
            InnerPBTControl.UpdateDisplayFunction =
                () => { return rig.InnerPBT; };
            InnerPBTControl.UpdateRigFunction =
                (int v) => { rig.InnerPBT = v; };
            numberBoxes.Add(InnerPBTControl);

            // Outer PBT
            OuterPBtControl.LowValue = Icom.InnerPBTMin;
            OuterPBtControl.HighValue = Icom.InnerPBTMax;
            OuterPBtControl.Increment = Icom.InnerPBTIncrement;
            OuterPBtControl.UpdateDisplayFunction =
                () => { return rig.OuterPBT; };
            OuterPBtControl.UpdateRigFunction =
                (int v) => { rig.OuterPBT = v; };
            numberBoxes.Add(OuterPBtControl);

            // Vox gain
            VoxGainControl.LowValue = Icom.pcMin;
            VoxGainControl.HighValue = Icom.pcMax;
            VoxGainControl.Increment = Icom.pcIncrement;
            VoxGainControl.UpdateDisplayFunction =
                () => { return rig.VoxGain; };
            VoxGainControl.UpdateRigFunction =
                (int v) => { rig.VoxGain = v; };
            numberBoxes.Add(VoxGainControl);

            // Vox delay
            VoxDelayControl.LowValue = Icom.pcMin;
            VoxDelayControl.HighValue = Icom.pcMax;
            VoxDelayControl.Increment = Icom.pcIncrement;
            VoxDelayControl.UpdateDisplayFunction =
                () => { return rig.VoxDelay; };
            VoxDelayControl.UpdateRigFunction =
                (int v) => { rig.VoxDelay = v; };
            numberBoxes.Add(VoxDelayControl);

            // antivox
            AntivoxControl.LowValue = Icom.pcMin;
            AntivoxControl.HighValue = Icom.pcMax;
            AntivoxControl.Increment = Icom.pcIncrement;
            AntivoxControl.UpdateDisplayFunction =
                () => { return rig.AntiVox; };
            AntivoxControl.UpdateRigFunction =
                (int v) => { rig.AntiVox= v; };
            numberBoxes.Add(AntivoxControl);

            // mic gain
            MicGainControl.LowValue = Icom.pcMin;
            MicGainControl.HighValue = Icom.pcMax;
            MicGainControl.Increment = Icom.pcIncrement;
            MicGainControl.UpdateDisplayFunction =
                () => { return rig.MicGain; };
            MicGainControl.UpdateRigFunction =
                (int v) => { rig.MicGain = v; };
            numberBoxes.Add(MicGainControl);

            // compression off/on.
            compList = new ArrayList();
            compList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.off));
            compList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.on));
            CompControl.TheList = compList;
            CompControl.UpdateDisplayFunction =
                () => { return rig.Comp; };
            CompControl.UpdateRigFunction =
                (object v) => { rig.Comp = (AllRadios.OffOnValues)v; };
            combos.Add(CompControl);

            // compression level
            CompLevelControl.LowValue = Icom.CompLevelMin;
            CompLevelControl.HighValue = Icom.CompLevelMax;
            CompLevelControl.Increment = Icom.CompLevelIncrement;
            CompLevelControl.UpdateDisplayFunction =
                () => { return rig.CompLevel; };
            CompLevelControl.UpdateRigFunction =
                (int v) => { rig.CompLevel = v; };
            numberBoxes.Add(CompLevelControl);

            // AFC
            AFCList = new ArrayList();
            AFCList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.off));
            AFCList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.on));
            AFCControl.TheList = AFCList;
            AFCControl.UpdateDisplayFunction =
                () => { return rig.AFC; };
            AFCControl.UpdateRigFunction =
                (object v) => { rig.AFC = (AllRadios.OffOnValues)v; };
            combos.Add(AFCControl);

            // AFC limit
            AFCLimitList = new ArrayList();
            AFCLimitList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.off));
            AFCLimitList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.on));
            AFCLimitControl.TheList = AFCLimitList;
            AFCLimitControl.UpdateDisplayFunction =
                () => { return rig.AFCLimit; };
            AFCLimitControl.UpdateRigFunction =
                (object v) => { rig.AFCLimit = (AllRadios.OffOnValues)v; };
            combos.Add(AFCLimitControl);

            // transmit width
            SSBTransmitBandwidthList = new ArrayList();
            foreach (int v in Enum.GetValues(typeof(Icom.SSBTransmitBandwidthValues)))
            {
                SSBTransmitBandwidthList.Add(new SSBTransmitBandwidthElement((Icom.SSBTransmitBandwidthValues)v));
            }
            SSBTransmitBandwidthControl.TheList = SSBTransmitBandwidthList;
            SSBTransmitBandwidthControl.UpdateDisplayFunction =
                () => { return rig.SSBTransmitBandwidth; };
            SSBTransmitBandwidthControl.UpdateRigFunction =
                (object v) => { rig.SSBTransmitBandwidth = (Icom.SSBTransmitBandwidthValues)v; };
            combos.Add(SSBTransmitBandwidthControl);

            // transmit bandwidth width
            TXBandwidthList = new ArrayList();
            foreach (byte b in TXBandwidthValues)
            {
                TXBandwidthList.Add(new TXBandwidthElement(b));
            }
            TXBandwidthControl.TheList = TXBandwidthList;
            TXBandwidthControl.UpdateDisplayFunction =
                () => { return rig.BWWidth; };
            // Update the display by setting SelectedIndex.
            TXBandwidthControl.BoxIndexFunction =
                (object o) => { return Array.IndexOf(TXBandwidthValues, (byte)o); };
            // Update the rig using the table indexed by SelectedIndex.
            TXBandwidthControl.UpdateRigByIndexFunction =
                (int id) => { rig.BWWidth = TXBandwidthValues[id]; };
            combos.Add(TXBandwidthControl);

            // monitor off/on.
            monitorList = new ArrayList();
            monitorList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.off));
            monitorList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.on));
            MonitorControl.TheList = monitorList;
            MonitorControl.UpdateDisplayFunction =
                () => { return rig.Monitor; };
            MonitorControl.UpdateRigFunction =
                (object v) => { rig.Monitor = (AllRadios.OffOnValues)v; };
            combos.Add(MonitorControl);

            // monitor level
            MonitorLevelControl.LowValue = Icom.pcMin;
            MonitorLevelControl.HighValue = Icom.pcMax;
            MonitorLevelControl.Increment = Icom.pcIncrement;
            MonitorLevelControl.UpdateDisplayFunction =
                () => { return rig.MonitorLevel; };
            MonitorLevelControl.UpdateRigFunction =
                (int v) => { rig.MonitorLevel = v; };
            numberBoxes.Add(MonitorLevelControl);

            // noise reduction off/on.
            nrList = new ArrayList();
            nrList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.off));
            nrList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.on));
            NRControl.TheList = nrList;
            NRControl.UpdateDisplayFunction =
                () => { return rig.NR; };
            NRControl.UpdateRigFunction =
                (object v) => { rig.NR = (AllRadios.OffOnValues)v; };
            combos.Add(NRControl);

            // noise reduction level
            NRLevelControl.LowValue = Icom.pcMin;
            NRLevelControl.HighValue = Icom.pcMax;
            NRLevelControl.Increment = Icom.pcIncrement;
            NRLevelControl.UpdateDisplayFunction =
                () => { return rig.NRLevel; };
            NRLevelControl.UpdateRigFunction =
                (int v) => { rig.NRLevel= v; };
            numberBoxes.Add(NRLevelControl);

            // AGC for all but FM.
            AGCList = new ArrayList();
            foreach (int v in Enum.GetValues(typeof(Icom.IcomAGCValues)))
            {
                AGCList.Add(new AGCElement((Icom.IcomAGCValues)v));
            }
            AGCControl.TheList = AGCList;
            AGCControl.UpdateDisplayFunction =
                () => { return rig.IcomAGC; };
            AGCControl.UpdateRigFunction =
                (object v) => { rig.IcomAGC = (Icom.IcomAGCValues)v; };
            combos.Add(AGCControl);

            // AGC level
            AGCtcControl.LowValue = Icom.AGCtcMin;
            AGCtcControl.HighValue = Icom.AGCtcMax;
            AGCtcControl.Increment = Icom.AGCtcIncrement;
            AGCtcControl.UpdateDisplayFunction =
                () => { return rig.AGCtc; };
            AGCtcControl.UpdateRigFunction =
                (int v) => { rig.AGCtc = v; };
            numberBoxes.Add(AGCtcControl);

            // noise blanker
            NBList = new ArrayList();
            NBList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.off));
            NBList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.on));
            NBControl.TheList = NBList;
            NBControl.UpdateDisplayFunction =
                () => { return rig.NoiseBlanker; };
            NBControl.UpdateRigFunction =
                (object v) => { rig.NoiseBlanker = (AllRadios.OffOnValues)v; };
            combos.Add(NBControl);

            // NB level
            NBLevelControl.LowValue = Icom.pcMin;
            NBLevelControl.HighValue = Icom.pcMax;
            NBLevelControl.Increment = Icom.pcIncrement;
            NBLevelControl.UpdateDisplayFunction =
                () => { return rig.NBLevel; };
            NBLevelControl.UpdateRigFunction =
                (int v) => { rig.NBLevel = v; };
            numberBoxes.Add(NBLevelControl);

            // NB depth
            NBDepthControl.LowValue = Icom.NBDepthMin;
            NBDepthControl.HighValue = Icom.NBDepthMax;
            NBDepthControl.Increment = Icom.NBDepthIncrement;
            NBDepthControl.UpdateDisplayFunction =
                () => { return rig.NBDepth; };
            NBDepthControl.UpdateRigFunction =
                (int v) => { rig.NBDepth = v; };
            numberBoxes.Add(NBDepthControl);

            // NB width
            NBWidthControl.LowValue = Icom.NBWidthMin;
            NBWidthControl.HighValue = Icom.NBWidthMax;
            NBWidthControl.Increment = Icom.NBWidthIncrement;
            NBWidthControl.UpdateDisplayFunction =
                () => { return rig.NBWidth; };
            NBWidthControl.UpdateRigFunction =
                (int v) => { rig.NBWidth = v; };
            numberBoxes.Add(NBWidthControl);

            // SSB/AM notch values
            SSBNotchList = new ArrayList();
            foreach (int v in Enum.GetValues(typeof(Icom.NotchValues)))
            {
                SSBNotchList.Add(new NotchElement((Icom.NotchValues)v));
            }
            SSBNotchControl.TheList = SSBNotchList; ;
            SSBNotchControl.UpdateDisplayFunction =
                () => { return rig.Notch; };
            SSBNotchControl.UpdateRigFunction =
                (object v) => { rig.Notch = (Icom.NotchValues)v; };
            combos.Add(SSBNotchControl);

            // SSB/AM notch values
            manualNotchList = new ArrayList();
            manualNotchList.Add(new NotchElement(Icom.NotchValues.off));
            manualNotchList.Add(new NotchElement(Icom.NotchValues.manual));
            ManualNotchControl.TheList = manualNotchList;
            ManualNotchControl.UpdateDisplayFunction =
                () => { return rig.Notch; };
            ManualNotchControl.UpdateRigFunction =
                (object v) => { rig.Notch = (Icom.NotchValues)v; };
            combos.Add(ManualNotchControl);

            // notch position
            NotchPositionControl.LowValue = Icom.NotchPositionMin;
            NotchPositionControl.HighValue = Icom.NotchPositionMax;
            NotchPositionControl.Increment = Icom.NotchPositionIncrement;
            NotchPositionControl.UpdateDisplayFunction =
                () => { return rig.NotchPosition; };
            NotchPositionControl.UpdateRigFunction =
                (int v) => { rig.NotchPosition = v; };
            numberBoxes.Add(NotchPositionControl);

            // notch width
            notchWidthList = new ArrayList();
            foreach (int v in Enum.GetValues(typeof(Icom.NotchWidthValues)))
            {
                notchWidthList.Add(new NotchWidthElement((Icom.NotchWidthValues)v));
            }
            NotchWidthControl.TheList = notchWidthList;
            NotchWidthControl.UpdateDisplayFunction =
                () => { return rig.NotchWidth; };
            NotchWidthControl.UpdateRigFunction =
                (object v) => { rig.NotchWidth = (Icom.NotchWidthValues)v; };
            combos.Add(NotchWidthControl);

            // FM notch
            FMNotchList = new ArrayList();
            FMNotchList.Add(new NotchElement(Icom.NotchValues.off));
            FMNotchList.Add(new NotchElement(Icom.NotchValues.auto));
            FMNotchControl.TheList = FMNotchList;
            FMNotchControl.UpdateDisplayFunction =
                () => { return rig.Notch; };
            FMNotchControl.UpdateRigFunction =
                (object v) => { rig.Notch = (Icom.NotchValues)v; };
            combos.Add(FMNotchControl);

            // vox voice delay
            voiceDelayList = new ArrayList();
            foreach (int v in Enum.GetValues(typeof(Icom.VoiceDelayValues)))
            {
                voiceDelayList.Add(new VoiceDelayElement((Icom.VoiceDelayValues)v));
            }
            VoiceDelayControl.TheList = voiceDelayList;
            VoiceDelayControl.UpdateDisplayFunction =
                () => { return rig.VoiceDelay; };
            VoiceDelayControl.UpdateRigFunction =
                (object v) => { rig.VoiceDelay = (Icom.VoiceDelayValues)v; };
            combos.Add(VoiceDelayControl);

            // Offset
            OffsetControl.LowValue = Icom.OffsetFrequencyMin;
            OffsetControl.HighValue = Icom.OffsetFrequencyMax;
            OffsetControl.Increment = Icom.OffsetFrequencyIncrement;
            OffsetControl.UpdateDisplayFunction =
                () => { return rig.OffsetFrequency; };
            OffsetControl.UpdateRigFunction =
                (int v) => { rig.OffsetFrequency = v; };
            numberBoxes.Add(OffsetControl);

            // tone mode
            toneModeList = new ArrayList();
            foreach (Icom.ToneTypes t in Enum.GetValues(typeof(Icom.ToneTypes)))
            {
                toneModeList.Add(new ToneModeElement(t));
            }
            ToneModeControl.TheList = toneModeList;
            ToneModeControl.UpdateDisplayFunction =
                () => { return rig.ToneMode; };
            ToneModeControl.UpdateRigFunction =
                (object v) => { rig.ToneMode = (Icom.ToneTypes)v; };
            combos.Add(ToneModeControl);

            // tone frequency
            ToneFrequencyList = new ArrayList();
            foreach (float f in rig.ToneFrequencyTable)
            {
                ToneFrequencyList.Add(new ToneFrequencyElement(f));
            }
            ToneFrequencyControl.TheList = ToneFrequencyList;
            ToneFrequencyControl.UpdateDisplayFunction =
                () => { return rig.ToneFrequency; };
            // Update the display by setting SelectedIndex.
            ToneFrequencyControl.BoxIndexFunction =
                (object o) => { return Array.IndexOf(rig.ToneFrequencyTable, (float)o); };
            // Update the rig using the table indexed by SelectedIndex.
            ToneFrequencyControl.UpdateRigByIndexFunction =
                (int id) => { rig.ToneFrequency = rig.ToneFrequencyTable[id]; };
            combos.Add(ToneFrequencyControl);
            subDependents.Add(new subDependentType(ToneFrequencyControl, ToneModeControl,
                () => { return ((rig.ToneMode == Icom.ToneTypes.tone) || (rig.ToneMode == Icom.ToneTypes.tcs)); }));

            // Xmit monitor
            XmitMonitorList = new ArrayList();
            XmitMonitorList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.off));
            XmitMonitorList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.on));
            XmitMonitorControl.TheList = XmitMonitorList;
            XmitMonitorControl.UpdateDisplayFunction =
                () => { return rig.AFCLimit; };
            XmitMonitorControl.UpdateRigFunction =
                (object v) => { rig.AFCLimit = (AllRadios.OffOnValues)v; };
            combos.Add(XmitMonitorControl);

            // AGC for FM.
            FMAGCList = new ArrayList();
            FMAGCList.Add(new AGCElement(Icom.IcomAGCValues.off));
            FMAGCList.Add(new AGCElement(Icom.IcomAGCValues.fast));
            FMAGCControl.TheList = FMAGCList;
            FMAGCControl.UpdateDisplayFunction =
                () => { return rig.IcomAGC; };
            FMAGCControl.UpdateRigFunction =
                (object v) => { rig.IcomAGC = (Icom.IcomAGCValues)v; };
            combos.Add(FMAGCControl);

            // RF attenuator
            attenuatorList = new ArrayList();
            attenuatorList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.off));
            attenuatorList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.on));
            AttenuatorControl.TheList = attenuatorList;
            AttenuatorControl.UpdateDisplayFunction =
                () => { return rig.NoiseBlanker; };
            AttenuatorControl.UpdateRigFunction =
                (object v) => { rig.NoiseBlanker = (AllRadios.OffOnValues)v; };
            combos.Add(AttenuatorControl);

            // HF preamp
            HFPreampList = new ArrayList();
            foreach (Icom.HFPreampType p in Enum.GetValues(typeof(Icom.HFPreampType)))
            {
                HFPreampList.Add(new HFPreampElement(p));
            }
            HFPreampControl.TheList = HFPreampList;
            HFPreampControl.UpdateDisplayFunction =
                () => { return rig.HFPreamp; };
            HFPreampControl.UpdateRigFunction =
                (object v) => { rig.HFPreamp = (Icom.HFPreampType)v; };
            combos.Add(HFPreampControl);
            subDependents.Add(new subDependentType(HFPreampControl, this,
                () => { return (rig.hfvhf == Icom.hfvhfValues.hf); }));

            // UHF preamp
            UHFPreampList = new ArrayList();
            UHFPreampList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.off));
            UHFPreampList.Add(new TS2000Filters.offOnElement(AllRadios.OffOnValues.on));
            UHFPreampControl.TheList = UHFPreampList;
            UHFPreampControl.UpdateDisplayFunction =
                () => { return rig.UHFPreamp; };
            UHFPreampControl.UpdateRigFunction =
                (object v) => { rig.UHFPreamp = (AllRadios.OffOnValues)v; };
            combos.Add(UHFPreampControl);
            subDependents.Add(new subDependentType(UHFPreampControl,this,
                () => { return (rig.hfvhf == Icom.hfvhfValues.vhf); }));

            // SWR
            infoBoxes.Add(SWRControl);

            // ALC
            infoBoxes.Add(ALCControl);

            // comp meter
            infoBoxes.Add(CompMeterControl);

            // Tuning step
            tuningStepList = new ArrayList();
            foreach (int i in Icom.TuningStepValues)
            {
                tuningStepList.Add(new tuningStepElement(i));
            }
            TuningStepControl.TheList = tuningStepList;
            TuningStepControl.UpdateDisplayFunction =
                () => { return rig.TuningStep; };
            TuningStepControl.UpdateRigFunction =
                (object v) => { rig.TuningStep = (int)v; };
            combos.Add(TuningStepControl);

            Control[] myControls = new Control[combos.Count + numberBoxes.Count + infoBoxes.Count]; // + textBoxes.Count];
            // Sort ScreenFields control list by text.
            for (int i = 0; i < myControls.Length; i++)
            {
                int id;
                if (i < combos.Count)
                {
                    myControls[i] = (Control)combos[i];
                    myControls[i].Tag = combos[i].Header;
                    combos[i].BoxKeydown += BoxKeydownDefault;
                }
                else if (i < combos.Count + numberBoxes.Count)
                {
                    id = i - combos.Count;
                    myControls[i] = (Control)numberBoxes[id];
                    myControls[i].Tag = numberBoxes[id].Header;
                    numberBoxes[id].BoxKeydown += BoxKeydownDefault;
                }
                else
                {
                    id = i - combos.Count - numberBoxes.Count;
                    myControls[i] = (Control)infoBoxes[id];
                    myControls[i].Tag = (string)infoBoxes[id].Header;
                    infoBoxes[id].BoxKeydown += BoxKeydownDefault;
                }
            }
            IComparer mySort = new mySortClass();
            Array.Sort(myControls, mySort);

            // setup the mode change stuff.
            modeChange = new modeChangeClass(this);

            // setup the memory display.
            Form memDisp = new ic9100memories(rig);
            // setup RigFields
            rig.RigFields = new AllRadios.RigInfo(this, updateBoxes, memDisp, null, myControls);
        }

        private delegate void rtn(Control c);
        private rtn enab = enable;
        private rtn disab = disable;
        private static void enable(Control c)
        {
            if (!c.Enabled)
            {
                c.Enabled = true;
                c.Visible = true;
                c.BringToFront();
            }
        }
        private static void disable(Control c)
        {
            if (c.Enabled)
            {
                c.Enabled = false;
                c.Visible = false;
                c.SendToBack();
            }
        }

        private void updateBoxes()
        {
            Tracing.TraceLine("updateBoxes", TraceLevel.Verbose);
            string errIdent = "";
            try
            {
                // enable/disable boxes for this mode.
                errIdent = "modeChange";
                modeChange.enableDisable(rig.Mode);

                // Check sub-dependencies
                foreach (subDependentType s in subDependents)
                {
                    errIdent = s.target.Name;
                    bool enabSw;
                    enabSw = ((s.predicate == null) || s.predicate.Enabled);
                    enabSw = (enabSw && ((s.Comparer == null) || s.Comparer()));
                    rtn enabDisab = (enabSw) ? enab : disab;
                    if (InvokeRequired)
                    {
                        Invoke(enabDisab, new object[] { s.target });
                    }
                    else
                    {
                        enabDisab(s.target);
                    }
                }

                foreach (Combo c in combos)
                {
                    errIdent = c.Name;
                    if (c.Enabled)
                    {
                        c.UpdateDisplay();
                    }
                }

                foreach (NumberBox c in numberBoxes)
                {
                    errIdent = c.Name;
                    if (c.Enabled)
                    {
                        c.UpdateDisplay();
                    }
                }

                foreach (InfoBox c in infoBoxes)
                {
                    errIdent = c.Name;
                    if (c.Enabled && (c.UpdateDisplayFunction != null))
                    {
                        c.UpdateDisplay();
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("updateBoxes exception:" + errIdent + ':' + ex.Message + ex.StackTrace, TraceLevel.Error);
            }
        }

        #region ModeChange
        private class modeChangeClass
        {
            // A mode's filter controls are enabled when that mode is active.
            // First, the specified controls for the other modes are disabled,
            // unless they're just going to be enabled again.
            private class controlsClass
            {
                public Control[] controls;
                public controlsClass(Control[] controlArray)
                {
                    int controlDim = controlArray.Length;
                    controls = new Control[controlDim];
                    for (int i = 0; i < controlDim; i++)
                    {
                        controls[i] = controlArray[i];
                    }
                }
            }
            private controlsClass[] modeControls;
            private ic9100filters parent;
            public modeChangeClass(ic9100filters p)
            {
                parent = p;
                modeControls = new controlsClass[Icom.myModeTable.Length];

                // setup the mode to combobox mapping.
                modeControls[(int)Icom.modes.lsb] = new controlsClass(
                    new Control[] {
                        parent.FilterControl, parent.CWSSBWidthControl,
                        parent.FilterTypeControl,
                        parent.InnerPBTControl, parent.OuterPBtControl,
                        parent.MicGainControl, parent.CompControl,
                        parent.CompLevelControl,parent.SSBTransmitBandwidthControl,
                        parent.TXBandwidthControl,
                        parent.VoxDelayControl, parent.VoxGainControl,
                        parent.AntivoxControl, parent.VoiceDelayControl,
                        parent.AGCControl, parent.AGCtcControl,
                        parent.NBControl, parent.NBLevelControl,
                        parent.NBDepthControl, parent.NBWidthControl,
                        parent.SSBNotchControl, parent.NotchPositionControl, parent.NotchWidthControl,
                        parent.CompMeterControl,
                    });
                modeControls[(int)Icom.modes.usb] = new controlsClass(
                    new Control[] {
                        parent.FilterControl, parent.CWSSBWidthControl,
                        parent.FilterTypeControl,
                        parent.InnerPBTControl, parent.OuterPBtControl,
                        parent.MicGainControl, parent.CompControl,
                        parent.CompLevelControl,parent.SSBTransmitBandwidthControl,
                        parent.TXBandwidthControl,
                        parent.VoxDelayControl, parent.VoxGainControl,
                        parent.AntivoxControl, parent.VoiceDelayControl,
                        parent.AGCControl, parent.AGCtcControl,
                        parent.NBControl, parent.NBLevelControl,
                        parent.NBDepthControl, parent.NBWidthControl,
                        parent.SSBNotchControl, parent.NotchPositionControl, parent.NotchWidthControl,
                        parent.CompMeterControl,
                    });
                modeControls[(int)Icom.modes.cw] = new controlsClass(
                    new Control[] {
                        parent.BkinDelayControl, parent.KeyerSpeedControl,
                        parent.FilterControl, parent.CWSSBWidthControl,
                        parent.FilterTypeControl,
                        parent.CWPitchControl, parent.KeyerControl,
                        parent.SidetoneGainControl,
                        parent.InnerPBTControl, parent.OuterPBtControl,
                        parent.AGCControl, parent.AGCtcControl,
                        parent.NBControl, parent.NBLevelControl,
                        parent.NBDepthControl, parent.NBWidthControl,
                        parent.ManualNotchControl, parent.NotchPositionControl, parent.NotchWidthControl,
                    });
                modeControls[(int)Icom.modes.cwr] = new controlsClass(
                    new Control[] {
                        parent.BkinDelayControl, parent.KeyerSpeedControl,
                        parent.FilterControl, parent.CWSSBWidthControl,
                        parent.FilterTypeControl,
                        parent.CWPitchControl, parent.KeyerControl,
                        parent.SidetoneGainControl,
                        parent.InnerPBTControl, parent.OuterPBtControl,
                        parent.AGCControl, parent.AGCtcControl,
                        parent.NBControl, parent.NBLevelControl,
                        parent.NBDepthControl, parent.NBWidthControl,
                        parent.ManualNotchControl, parent.NotchPositionControl, parent.NotchWidthControl,
                    });
                modeControls[(int)Icom.modes.fsk] = new controlsClass(
                    new Control[] {
                        parent.FilterControl, parent.CWSSBWidthControl,
                        parent.InnerPBTControl, parent.OuterPBtControl,
                        parent.AGCControl, parent.AGCtcControl,
                        parent.NBControl, parent.NBLevelControl,
                        parent.NBDepthControl, parent.NBWidthControl,
                    });
                modeControls[(int)Icom.modes.fm] = new controlsClass(
                    new Control[] {
                        parent.VoxDelayControl, parent.VoxGainControl,
                        parent.AntivoxControl, parent.VoiceDelayControl,
                        parent.MicGainControl,
                        parent.AFCControl,parent.AFCLimitControl,
                        parent.MonitorControl, parent.MonitorLevelControl,
                        parent.FMAGCControl, parent.FMNotchControl,
                        parent.OffsetControl,
                        parent.ToneModeControl, parent.XmitMonitorControl,
                        parent.CompMeterControl,
                    });
                modeControls[(int)Icom.modes.am] = new controlsClass(
                    new Control[] { 
                        parent.FilterControl, parent.AMWidthControl,
                        parent.FilterTypeControl,
                        parent.InnerPBTControl, parent.OuterPBtControl,
                        parent.MicGainControl,
                        parent.VoxDelayControl, parent.VoxGainControl,
                        parent.AntivoxControl, parent.VoiceDelayControl,
                        parent.AGCControl, parent.AGCtcControl,
                        parent.NBControl, parent.NBLevelControl,
                        parent.NBDepthControl, parent.NBWidthControl,
                        parent.SSBNotchControl, parent.NotchPositionControl, parent.NotchWidthControl,
                    });
            }

            private AllRadios.ModeValue oldMode = Icom.myModeTable[0];
            public void enableDisable(AllRadios.ModeValue mode)
            {
                // Just quit if the mode hasn't changed.
                if (mode == oldMode) return;
                oldMode = mode;
                int mod = mode.id;
                // quit if no controls for this mode.
                if (modeControls[mod] == null) return;
                // enables holds the controls to be enabled.
                Control[] enables = new Control[modeControls[mod].controls.Length];
                for (int i = 0; i < enables.Length; i++)
                {
                    // We need to quit if no more controls for this mode.
                    if (modeControls[mod].controls[i] == null) break;
                    enables[i] = modeControls[mod].controls[i];
                }
                parent.SuspendLayout();
                for (int i = 0; i < modeControls.Length; i++)
                {
                    if (modeControls[i] == null) continue;
                    for (int j = 0; j < modeControls[i].controls.Length; j++)
                    {
                        Control c = modeControls[i].controls[j];
                        if (c == null) break;
                        if (Array.IndexOf(enables, c) >= 0)
                        {
                            // enable
                            if (parent.InvokeRequired)
                            {
                                parent.Invoke(parent.enab, new object[] { c });
                            }
                            else
                            {
                                parent.enab(c);
                            }
                        }
                        else
                        {
                            // disable
                            if (parent.InvokeRequired)
                            {
                                parent.Invoke(parent.disab, new object[] { c });
                            }
                            else
                            {
                                parent.disab(c);
                            }
                        }
                    }
                }
                parent.ResumeLayout();
            }
        }
        private modeChangeClass modeChange;
        #endregion

        private void BoxKeydownDefault(object sender, KeyEventArgs e)
        {
            OnKeyDown(e);
        }

        private FloatPeakType SWRPeak;
        private IndexedPeakType ALCPeak;
        private IndexedPeakType CompPeak;
        private void ic9100filters_ControlRemoved(object sender, ControlEventArgs e)
        {
            SWRControl.UpdateDisplayFunction = null;
            if (SWRPeak != null) SWRPeak.Finished();
            ALCControl.UpdateDisplayFunction = null;
            if (ALCPeak != null) ALCPeak.Finished();
            CompMeterControl.UpdateDisplayFunction = null;
            if (CompPeak != null) CompPeak.Finished();
        }

        private void SWRControl_Enter(object sender, EventArgs e)
        {
            Tracing.TraceLine("SWR entered", TraceLevel.Info);
            SWRPeak = new FloatPeakType(() =>
            {
                rig.SendCommand(Icom.BldCmd(Icom.ICSWRMeter));
                return rig.SWR;
            },
            1000, 1.0f);
            SWRControl.UpdateDisplayFunction =
                () => { return SWRPeak.Read().ToString("f1"); };
        }

        private void SWRControl_Leave(object sender, EventArgs e)
        {
            SWRControl.UpdateDisplayFunction = null;
            if (SWRPeak != null) SWRPeak.Finished();
            Tracing.TraceLine("SWR left", TraceLevel.Info);
        }

        private void ALCControl_Enter(object sender, EventArgs e)
        {
            Tracing.TraceLine("ALC entered", TraceLevel.Info);
            ALCPeak = new IndexedPeakType(() =>
            {
                rig.SendCommand(Icom.BldCmd(Icom.ICALCMeter));
                return rig.ALC;
            },
            1000);
            ALCControl.UpdateDisplayFunction =
                () => { return ALCPeak.Read().ToString(); };
        }

        private void ALCControl_Leave(object sender, EventArgs e)
        {
            ALCControl.UpdateDisplayFunction = null;
            if (ALCPeak != null) ALCPeak.Finished();
            Tracing.TraceLine("ALC left", TraceLevel.Info);
        }

        private void CompMeterControl_Enter(object sender, EventArgs e)
        {
            Tracing.TraceLine("CompMeter entered", TraceLevel.Info);
            CompPeak = new IndexedPeakType(() =>
            {
                rig.SendCommand(Icom.BldCmd(Icom.ICCompMeter));
                return rig.CompMeter;
            },
            1000);
            CompMeterControl.UpdateDisplayFunction =
                () => { return CompPeak.Read().ToString(); };
        }

        private void CompMeterControl_Leave(object sender, EventArgs e)
        {
            CompMeterControl.UpdateDisplayFunction = null;
            if (CompPeak != null) CompPeak.Finished();
            Tracing.TraceLine("CompMeter left", TraceLevel.Info);
        }
    }
}
