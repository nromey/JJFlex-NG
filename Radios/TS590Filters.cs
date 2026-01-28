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
    public partial class TS590Filters : UserControl
    {
        protected KenwoodTS590 rig;
        private Collection<Combo> combos;
        private Collection<NumberBox> numberBoxes;
        private Collection<Button> buttons;

        private Form RXForm = null;
        private bool RXActive = false;
        private Form TXForm = null;
        private bool TXActive = false;

        internal class dataModeElement
        {
            private AllRadios.DataModes val;
            public string Display { get { return val.ToString(); } }
            public AllRadios.DataModes RigItem { get { return val; } }
            public dataModeElement(AllRadios.DataModes v)
            {
                val = v;
            }
        }
        private static dataModeElement[] dataModeItems =
        {
            new dataModeElement(AllRadios.DataModes.off),
            new dataModeElement(AllRadios.DataModes.on)
        };
        private ArrayList dataModeList, tDataModeList;

        internal class toneCTCSSElement
        {
            private AllRadios.ToneCTCSSValue val;
            public string Display { get { return val.ToString(); } }
            public AllRadios.ToneCTCSSValue RigItem { get { return val; } }
            public toneCTCSSElement(AllRadios.ToneCTCSSValue v)
            {
                val = v;
            }
        }
        private ArrayList toneCTCSSList;

        internal class toneCTCSSFreqElement
        {
            private float val;
            public string Display { get { return val.ToString(); } }
            public float RigItem { get { return val; } }
            public toneCTCSSFreqElement(float v)
            {
                val = v;
            }
        }

        internal enum fmModes590
        {
            Wide,
            Naro
        }
        internal class FMWidthElement
        {
            private fmModes590 val;
            public string Display { get { return val.ToString(); } }
            public fmModes590 RigItem { get { return val; } }
            public FMWidthElement(fmModes590 v)
            {
                val = v;
            }
        }
        private ArrayList FMWidthList;

        private ArrayList toneFreqList, CTCSSFreqList;

        internal class numericElement
        {
            private int val;
            public string Display { get { return (val).ToString(); } }
            public int RigItem { get { return val; } }
            public numericElement(int v)
            {
                val = v;
            }
        }
        private ArrayList TXAntList;

        internal class trueFalseElement
        {
            private bool val;
            public string Display { get { return val.ToString(); } }
            public bool RigItem { get { return val; } }
            public trueFalseElement(bool v)
            {
                val = v;
            }
        }
        private ArrayList RXAntList, DriveAmpList;

        internal class offOnElement
        {
            private Radios.AllRadios.OffOnValues val;
            public string Display { get { return val.ToString(); } }
            public Radios.AllRadios.OffOnValues RigItem { get { return val; } }
            public offOnElement(Radios.AllRadios.OffOnValues v)
            {
                val = v;
            }
        }
        private ArrayList rfAttList, preAmpList, processorList, decodeList, TXMonitorList;

        private enum filterValues
        {
            a = 1,
            b
        }
        private class filterElement
        {
            private filterValues val;
            public string Display { get { return val.ToString(); } }
            public filterValues RigItem { get { return val; } }
            public filterElement(filterValues v)
            {
                val = v;
            }
        }
        private ArrayList filterList;
        private filterElement[] filterItems =
        {new filterElement(filterValues.a),
         new filterElement(filterValues.b)};

        private ArrayList cwShiftList;

        private numericElement[] cwWidthValues =
        {new numericElement(50), new numericElement(80),
         new numericElement(100), new numericElement(150),
         new numericElement(200), new numericElement(250),
         new numericElement(300), new numericElement(400),
         new numericElement(500), new numericElement(600),
         new numericElement(1000), new numericElement(1500),
         new numericElement(2000), new numericElement(2500)};
        private ArrayList cwWidthList;

        private numericElement[] fskWidthValues =
        {new numericElement(250), new numericElement(500),
         new numericElement(1000), new numericElement(1500)};
        private ArrayList fskWidthList;

        // Map SH/SL control values to HZ
        private int[] SSBFMFMDLowValues = 
        { 0, 50, 100, 200, 300, 400, 500,
          600, 700, 800, 900, 1000 };
        private int[] SSBFMFMDHighValues =
        {1000,1200,1400,1600,1800,2000,2200,2400,2600,2800,
         3000,3400,4000,5000};
        private int[] AMLowValues = { 0, 100, 200, 300 };
        private int[] AMHighValues = { 2500, 3000, 4000, 5000 };
        // width uses SH, offset uses SL.
        private int[] SSBDOffsetValues =
        { 1000, 1100, 1200, 1300, 1400, 1500, 1600, 1700, 1800, 1900, 2000, 2100, 2210 };
        private int[] SSBDSGOffsetValues =
        { 1000, 1100, 1200, 1300, 1400, 1500, 1600, 1700, 1750, 1800, 1900, 2000, 2100, 2210 };
        private int[] SSBDWidthValues = { 50, 80, 100, 150, 200, 250, 300, 400, 500, 600, 1000, 1500, 2000, 2500 };
        private class SHSL
        {
            int id;
            int[] disp;
            public SHSL(int i, int[] arr)
            {
                id = i;
                disp = arr;
            }
            public string Display
            {
                get { return (id<disp.Length)?disp[id].ToString():""; }
            }
            public int RigItem { get { return id; } }
        }
        private ArrayList SSBFMFMDLowList, SSBFMFMDHighList;
        private ArrayList SSBDOffsetList, SSBDWidthList;
        private ArrayList AMLowList, AMHighList;

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

        private class NRElement
        {
            KenwoodTS590.NRtype val;
            public string Display
            {
                get { return val.ToString(); }
            }
            public KenwoodTS590.NRtype RigItem
            {
                get { return val; }
            }
            public NRElement(KenwoodTS590.NRtype v)
            {
                val = v;
            }
        }
        private ArrayList NRList;

        private class AGCGroupElement
        {
            KenwoodTS590.AGCGroupType val;
            public string Display
            {
                get { return val.ToString(); }
            }
            public KenwoodTS590.AGCGroupType RigItem
            {
                get { return val; }
            }
            public AGCGroupElement(KenwoodTS590.AGCGroupType v)
            {
                val = v;
            }
        }
        private ArrayList AGCGroupList;

        private class NBElement
        {
            KenwoodTS590.NBtype val;
            public string Display
            {
                get { return val.ToString(); }
            }
            public KenwoodTS590.NBtype RigItem
            {
                get { return val; }
            }
            public NBElement(KenwoodTS590.NBtype v)
            {
                val = v;
            }
        }
        private ArrayList NBList;

        private class NotchElement
        {
            KenwoodTS590.notchType val;
            public string Display
            {
                get { return val.ToString(); }
            }
            public KenwoodTS590.notchType RigItem
            {
                get { return val; }
            }
            public NotchElement(KenwoodTS590.notchType v)
            {
                val = v;
            }
        }
        private class notchWidthElement
        {
            KenwoodTS590.notchWidthType val;
            public string Display
            {
                get { return val.ToString(); }
            }
            public KenwoodTS590.notchWidthType RigItem
            {
                get { return val; }
            }
            public notchWidthElement(KenwoodTS590.notchWidthType v)
            {
                val = v;
            }
        }
        private ArrayList notchList, notchWidthList;

        private class beatCancelElement
        {
            KenwoodTS590.beatCancelType val;
            public string Display
            {
                get { return val.ToString(); }
            }
            public KenwoodTS590.beatCancelType RigItem
            {
                get { return val; }
            }
            public beatCancelElement(KenwoodTS590.beatCancelType v)
            {
                val = v;
            }
        }
        private ArrayList beatCancelList;

        private class txSourceElement
        {
            KenwoodTS590.TXSources val;
            public string Display
            {
                get { return val.ToString(); }
            }
            public KenwoodTS590.TXSources RigItem
            {
                get { return val; }
            }
            public txSourceElement(KenwoodTS590.TXSources v)
            {
                val = v;
            }
        }
        private ArrayList txSourceList;

        // TextBox setup data.
        // The ReadOnly TextBoxes are set using a table of values.
        private class textBoxValueClass
        {
            public TextBox box;
            private string[] tbl;
            public delegate int RigValueDel();
            private RigValueDel rigValue;
            private delegate void del(int val);
            private del dispFunc;
            private KenwoodTS590 radio;
            public textBoxValueClass(TextBox tb, string[] t, RigValueDel rtn, KenwoodTS590 rad)
            {
                box = tb;
                tbl = t;
                rigValue = rtn;
                dispFunc = (int val) => { box.Text = ((val >= 0) && (val < tbl.Length)) ? tbl[val] : ""; };
                radio = rad;
            }
            private int oldVal = -1;
            public void Value()
            {
                int val = rigValue();
                if (val != oldVal)
                {
                    oldVal = val;
                    if (box.InvokeRequired)
                    {
                        box.Invoke(dispFunc, new object[] { val });
                    }
                    else dispFunc(val);
                    //Tracing.TraceLine(box.Name + ":" + tbl[val]);
                }
            }
        }
        private Collection<textBoxValueClass> textBoxes;
        private string[] SWRValues =
        {"1.0","1.0","1.1","1.2","1.3","1.4","1.5",
         "1.6","1.7","1.8","1.9","2.0","2.2","2.4","2.6","2.8","3.0",
         "3.5","4.0","4.5","5.0","6.0","7.0","8.0","9.0",
         "Over","Over","Over","Over","Over","Over"};
        private string[] ALCValues =
        {"0","1","2","3","4","5","6","7","8","9",
         "10","11","12","13","14","over","over","over","over","over",
         "over","over","over","over","over","over",
         "over","over","over","over","over"};
        private string[] CompValues =
        {"0","1","2","3","4","5","6","7","8","9","10",
            "11","12","13","14","15","16","17","18","19","20",
            "over","over","over","over","over","over","over","over","over","over"};

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

        public TS590Filters(KenwoodTS590 r)
        {
            InitializeComponent();
            rig = r;
            Tracing.TraceLine("TS590Filters constructor", TraceLevel.Info);

            // setup the boxes
            combos = new Collection<Combo>();
            numberBoxes = new Collection<NumberBox>();
            buttons = new Collection<Button>();
            subDependents = new Collection<subDependentType>();

            // CW keyer speed
            SpeedControl.LowValue = AllRadios.MinSpeed;
            SpeedControl.HighValue = AllRadios.MaxSpeed;
            SpeedControl.Increment = 1;
            SpeedControl.UpdateDisplayFunction =
                () => { return rig.KeyerSpeed; };
            SpeedControl.UpdateRigFunction =
                (int v) => { rig.KeyerSpeed = v; };
            numberBoxes.Add(SpeedControl);

            // SSB/FM data mode
            dataModeList = new ArrayList();
            tDataModeList = new ArrayList();
            foreach (dataModeElement e in dataModeItems)
            {
                dataModeList.Add(e);
                tDataModeList.Add(e);
            }
            DataModeControl.TheList = dataModeList;
            DataModeControl.UpdateDisplayFunction =
                () => { return (AllRadios.DataModes)rig.DataMode; };
            DataModeControl.UpdateRigFunction =
                (object v) => { rig.DataMode = (AllRadios.DataModes)v; };
            combos.Add(DataModeControl);

            // FM tone or CTCSS mode.
            toneCTCSSList=new ArrayList();
            foreach (AllRadios.ToneCTCSSValue t in rig.FMToneModes)
            {
                toneCTCSSList.Add(new toneCTCSSElement(t));
            }
            ToneCTCSSControl.TheList=toneCTCSSList;
            ToneCTCSSControl.UpdateDisplayFunction =
                () => { return rig.ToneCTCSS;};
            ToneCTCSSControl.UpdateRigFunction =
                (object v) => { rig.ToneCTCSS = (AllRadios.ToneCTCSSValue)v;};
            combos.Add(ToneCTCSSControl);

            // FM tone frequency
            toneFreqList = new ArrayList();
            foreach (float f in rig.ToneFrequencyTable)
            {
                toneFreqList.Add(new toneCTCSSFreqElement(f));
            }
            ToneFreqControl.TheList = toneFreqList;
            ToneFreqControl.UpdateDisplayFunction =
                () => { return rig.ToneFrequency; };
            ToneFreqControl.UpdateRigFunction =
                (object v) => { rig.ToneFrequency = (float)v; };
            combos.Add(ToneFreqControl);

            // FM CTCSS frequency
            CTCSSFreqList = new ArrayList();
            foreach (float f in rig.ToneFrequencyTable)
            {
                CTCSSFreqList.Add(new toneCTCSSFreqElement(f));
            }
            CTCSSFreqControl.TheList = CTCSSFreqList;
            CTCSSFreqControl.UpdateDisplayFunction =
                () => { return rig.CTSSFrequency; };
            CTCSSFreqControl.UpdateRigFunction =
                (object v) => { rig.CTSSFrequency = (float)v; };
            combos.Add(CTCSSFreqControl);

            // FM width
            FMWidthList = new ArrayList();
            FMWidthList.Add(new FMWidthElement(fmModes590.Wide));
            FMWidthList.Add(new FMWidthElement(fmModes590.Naro));
            FMWidthControl.TheList = FMWidthList;
            FMWidthControl.UpdateDisplayFunction =
                () => { return (fmModes590)rig.filterWidth; };
            FMWidthControl.UpdateRigFunction =
                (object v) => { rig.filterWidth = (int)v; };
            combos.Add(FMWidthControl);

            // Antenna, 1 based
            TXAntList = new ArrayList();
            for (int i=1; i<rig.TXAnts+1; i++)
            {
                TXAntList.Add(new numericElement(i));
            }
            TXAntControl.TheList = TXAntList;
            TXAntControl.UpdateDisplayFunction =
                () => { return rig.TXAntenna + 1; };
            TXAntControl.UpdateRigFunction =
                (object v) => { rig.TXAntenna = (int)v - 1; };
            combos.Add(TXAntControl);

            // RX Antenna
            RXAntList = new ArrayList();
            RXAntList.Add(new trueFalseElement(false));
            RXAntList.Add(new trueFalseElement(true));
            RXAntControl.TheList = RXAntList;
            RXAntControl.UpdateDisplayFunction =
                () => { return rig.RXAntenna; };
            RXAntControl.UpdateRigFunction =
                (object v) => { rig.RXAntenna = (bool)v; };
            combos.Add(RXAntControl);

            // Driving amp?
            DriveAmpList = new ArrayList();
            DriveAmpList.Add(new trueFalseElement(false));
            DriveAmpList.Add(new trueFalseElement(true));
            DriveAmpControl.TheList = DriveAmpList;
            DriveAmpControl.UpdateDisplayFunction =
                () => { return rig.DriveAmp; };
            DriveAmpControl.UpdateRigFunction =
                (object v) => { rig.DriveAmp = (bool)v; };
            combos.Add(DriveAmpControl);

            // RF Attenuator
            rfAttList = new ArrayList();
            rfAttList.Add(new offOnElement(Radios.AllRadios.OffOnValues.off));
            rfAttList.Add(new offOnElement(Radios.AllRadios.OffOnValues.on));
            RFAttControl.TheList = rfAttList;
            RFAttControl.UpdateDisplayFunction =
                () => { return rig.RFAttenuator;};
            RFAttControl.UpdateRigFunction =
                (object v) => { rig.RFAttenuator = (Radios.AllRadios.OffOnValues)v;};
            combos.Add(RFAttControl);

            // PreAmp
            preAmpList = new ArrayList();
            preAmpList.Add(new offOnElement(Radios.AllRadios.OffOnValues.off));
            preAmpList.Add(new offOnElement(Radios.AllRadios.OffOnValues.on));
            PreAmpControl.TheList = preAmpList;
            PreAmpControl.UpdateDisplayFunction =
                () => { return rig.PreAmp; };
            PreAmpControl.UpdateRigFunction =
                (object v) => { rig.PreAmp = (Radios.AllRadios.OffOnValues)v; };
            combos.Add(PreAmpControl);

            // vox enabled shown in main display
            // processor
            processorList = new ArrayList();
            processorList.Add(new offOnElement(Radios.AllRadios.OffOnValues.off));
            processorList.Add(new offOnElement(Radios.AllRadios.OffOnValues.on));
            ProcessorControl.TheList = processorList;
            ProcessorControl.UpdateDisplayFunction =
                () => { return rig.ProcessorState; };
            ProcessorControl.UpdateRigFunction =
                (object v) => { rig.ProcessorState = (Radios.AllRadios.OffOnValues)v; };
            combos.Add(ProcessorControl);

            // breakin delay
            BreakinDelayControl.LowValue = 0;
            BreakinDelayControl.HighValue = rig.BreakinDelayMax;
            BreakinDelayControl.Increment = rig.BreakinDelayIncrement;
            BreakinDelayControl.UpdateDisplayFunction =
                () => { return rig.BreakinDelay; };
            BreakinDelayControl.UpdateRigFunction =
                (int v) => { rig.BreakinDelay = v; };
            numberBoxes.Add(BreakinDelayControl);

            // vox delay
            VoxDelayControl.LowValue = 0;
            VoxDelayControl.HighValue = rig.VoxDelayMax;
            VoxDelayControl.Increment = rig.VoxDelayIncrement;
            VoxDelayControl.UpdateDisplayFunction =
                () => { return rig.VoxDelay; };
            VoxDelayControl.UpdateRigFunction =
                (int v) => { rig.VoxDelay = v; };
            numberBoxes.Add(VoxDelayControl);

            // vox gain
            VoxGainControl.LowValue = 0;
            VoxGainControl.HighValue = rig.VoxGainMax;
            VoxGainControl.Increment = rig.VoxGainIncrement;
            VoxGainControl.UpdateDisplayFunction =
                () => { return rig.VoxGain; };
            VoxGainControl.UpdateRigFunction =
                (int v) => { rig.VoxGain = v; };
            numberBoxes.Add(VoxGainControl);

            // mic gain
            MicGainControl.LowValue = 0;
            MicGainControl.HighValue = rig.MicGainMax;
            MicGainControl.Increment = rig.MicGainIncrement;
            MicGainControl.UpdateDisplayFunction =
                () => { return rig.MicGain; };
            MicGainControl.UpdateRigFunction =
                (int v) => { rig.MicGain = v; };
            numberBoxes.Add(MicGainControl);

            // carrier level
            CarrierLevelControl.LowValue = 0;
            CarrierLevelControl.HighValue = rig.CarrierLevelMax;
            CarrierLevelControl.Increment = rig.CarrierLevelIncrement;
            CarrierLevelControl.UpdateDisplayFunction =
                () => { return rig.CarrierLevel; };
            CarrierLevelControl.UpdateRigFunction =
                (int v) => { rig.CarrierLevel = v; };
            numberBoxes.Add(CarrierLevelControl);

            // processor input level
            ProcessorInLevelControl.LowValue = 0;
            ProcessorInLevelControl.HighValue = rig.ProcessorInputLevelMax;
            ProcessorInLevelControl.Increment = rig.ProcessorInputLevelIncrement;
            ProcessorInLevelControl.UpdateDisplayFunction =
                () => { return rig.ProcessorInputLevel; };
            ProcessorInLevelControl.UpdateRigFunction =
                (int v) => { rig.ProcessorInputLevel = v; };
            numberBoxes.Add(ProcessorInLevelControl);

            // processor output level
            ProcessorOutLevelControl.LowValue = 0;
            ProcessorOutLevelControl.HighValue = rig.ProcessorOutputLevelMax;
            ProcessorOutLevelControl.Increment = rig.ProcessorOutputLevelIncrement;
            ProcessorOutLevelControl.UpdateDisplayFunction =
                () => { return rig.ProcessorOutputLevel; };
            ProcessorOutLevelControl.UpdateRigFunction =
                (int v) => { rig.ProcessorOutputLevel = v; };
            numberBoxes.Add(ProcessorOutLevelControl);

            // TX monitor control, off if 0.
            TXMonitorList = new ArrayList();
            for (int i = Kenwood.TXMonitorMin; i <= Kenwood.TXMonitorMax; i += Kenwood.TXMonitorIncrement)
            {
                TXMonitorList.Add(new TS2000Filters.OffNumericElement(i));
            }
            TXMonitorControl.TheList = TXMonitorList;
            TXMonitorControl.UpdateDisplayFunction =
                () => { return rig.TXMonitor; };
            TXMonitorControl.UpdateRigFunction =
                (object v) => { rig.TXMonitor = (int)v; };
            combos.Add(TXMonitorControl);

            // transmit source
            txSourceList = new ArrayList();
            foreach(KenwoodTS590.TXSources s in Enum.GetValues(typeof(KenwoodTS590.TXSources)))
            {
                txSourceList.Add(new txSourceElement(s));
            }
            TXSourceControl.TheList = txSourceList;
            TXSourceControl.UpdateDisplayFunction =
                () => { return rig.TXSource; };
            TXSourceControl.UpdateRigFunction =
                (object v) => { rig.TXSource = (KenwoodTS590.TXSources)v; };
            combos.Add(TXSourceControl);

            // transmit power
            XmitPowerControl.LowValue = rig.XmitPowerMin;
            XmitPowerControl.HighValue = rig.XmitPowerMax;
            XmitPowerControl.Increment = rig.XmitPowerIncrement;
            XmitPowerControl.UpdateDisplayFunction =
                () => { return rig.XmitPower; };
            XmitPowerControl.UpdateRigFunction =
                (int v) => { rig.XmitPower = v; };
            numberBoxes.Add(XmitPowerControl);

            // filter in use
            filterList = new ArrayList();
            foreach (filterElement f in filterItems)
            {
                filterList.Add(f);
            }
            FilterControl.TheList = filterList;
            FilterControl.UpdateDisplayFunction =
                () => { return (filterValues)rig.filterNum; };
            FilterControl.UpdateRigFunction =
                (object v) => { rig.filterNum = (int)v; };
            combos.Add(FilterControl);

            // cw shift
            cwShiftList = new ArrayList();
            // Values are from 300 to 1000 step 50.
            for (int i = 300; i <= 1000; i += 50)
            {
                cwShiftList.Add(new numericElement(i));
            }
            CWOffsetControl.TheList = cwShiftList;
            CWOffsetControl.UpdateDisplayFunction =
                () => { return rig.filterOffset; };
            CWOffsetControl.UpdateRigFunction =
                (object v) => { rig.filterOffset = (int)v; };
            combos.Add(CWOffsetControl);

            // cw width
            cwWidthList = new ArrayList();
            foreach (numericElement e in cwWidthValues)
            {
                cwWidthList.Add(e);
            }
            CWWidthControl.TheList = cwWidthList;
            CWWidthControl.UpdateDisplayFunction =
                () => { return rig.filterWidth; };
            CWWidthControl.UpdateRigFunction =
                (object v) => { rig.filterWidth = (int)v; };
            combos.Add(CWWidthControl);

            // FSK width
            fskWidthList = new ArrayList();
            foreach (numericElement e in fskWidthValues)
            {
                fskWidthList.Add(e);
            }
            FSKWidthControl.TheList = fskWidthList;
            FSKWidthControl.UpdateDisplayFunction =
                () => { return rig.filterWidth; };
            FSKWidthControl.UpdateRigFunction =
                (object v) => { rig.filterWidth = (int)v; };
            combos.Add(FSKWidthControl);

            SSBFMFMDLowList = new ArrayList();
            for (int i=0; i<SSBFMFMDLowValues.Length; i++)
            {
                SSBFMFMDLowList.Add(new SHSL(i, SSBFMFMDLowValues));
            }
            SSBFMFMDLowControl.TheList = SSBFMFMDLowList;
            SSBFMFMDLowControl.UpdateDisplayFunction =
                () => { return safeTableValue(SSBFMFMDLowValues,rig.filterLow); };
            SSBFMFMDLowControl.UpdateRigFunction =
                (object v) => { rig.filterLow = (int)v; };
            combos.Add(SSBFMFMDLowControl);
            // Control is valid for FM, and SSB where the high/low menu item is set.
            subDependents.Add(new subDependentType(SSBFMFMDLowControl, SSBFMFMDLowControl,
                () =>
                {
                    return ((rig.Mode.ToString() == "fm") || !rig.SSBShiftWidth);
                }));

            SSBFMFMDHighList = new ArrayList();
            for (int i=0; i<SSBFMFMDHighValues.Length; i++)
            {
                SSBFMFMDHighList.Add(new SHSL(i, SSBFMFMDHighValues));
            }
            SSBFMFMDHighControl.TheList = SSBFMFMDHighList;
            SSBFMFMDHighControl.UpdateDisplayFunction =
                () => { return safeTableValue(SSBFMFMDHighValues,rig.filterHigh); };
            SSBFMFMDHighControl.UpdateRigFunction =
                (object v) => { rig.filterHigh = (int)v; };
            combos.Add(SSBFMFMDHighControl);
            // Control is valid for FM, and SSB where the high/low menu item is set.
            subDependents.Add(new subDependentType(SSBFMFMDHighControl, SSBFMFMDHighControl,
                () =>
                {
                    return ((rig.Mode.ToString() == "fm") || !rig.SSBShiftWidth);
                }));

            SSBDOffsetList = new ArrayList();
            int[] offsetArray = (rig.TS590S) ? SSBDOffsetValues : SSBDSGOffsetValues;
            for (int i = 0; i < offsetArray.Length; i++)
            {
                SSBDOffsetList.Add(new SHSL(i, offsetArray));
            }
            SSBDOffsetControl.TheList = SSBDOffsetList;
            SSBDOffsetControl.UpdateDisplayFunction =
                () => { return safeTableValue(offsetArray, rig.filterHigh); };
            SSBDOffsetControl.UpdateRigFunction =
                (object v) => { rig.filterHigh= (int)v; };
            combos.Add(SSBDOffsetControl);
            // Control is valid for SSB where the shift/offset menu item is set.
            // Note that SSBShiftWidth is only set for the TS590SG.
            subDependents.Add(new subDependentType(SSBDOffsetControl, null,
                () =>
                {
                    return (rig.SSBShiftWidth);
                }));

            SSBDWidthList = new ArrayList();
            for (int i = 0; i < SSBDWidthValues.Length; i++)
            {
                SSBDWidthList.Add(new SHSL(i, SSBDWidthValues));
            }
            SSBDWidthControl.TheList = SSBDWidthList;
            SSBDWidthControl.UpdateDisplayFunction =
                () => { return safeTableValue(SSBDWidthValues,rig.filterLow); };
            SSBDWidthControl.UpdateRigFunction =
                (object v) => { rig.filterLow = (int)v; };
            combos.Add(SSBDWidthControl);
            // Control is valid for SSB where the shift/offset menu item is set.
            subDependents.Add(new subDependentType(SSBDWidthControl, null,
                () =>
                {
                    return (rig.SSBShiftWidth);
                }));

            AMLowList = new ArrayList();
            for (int i = 0; i < AMLowValues.Length; i++)
            {
                AMLowList.Add(new SHSL(i, AMLowValues));
            }
            AMLowControl.TheList = AMLowList;
            AMLowControl.UpdateDisplayFunction =
                () => { return safeTableValue(AMLowValues,rig.filterLow); };
            AMLowControl.UpdateRigFunction =
                (object v) => { rig.filterLow = (int)v; };
            combos.Add(AMLowControl);

            AMHighList = new ArrayList();
            for (int i = 0; i < AMHighValues.Length; i++)
            {
                AMHighList.Add(new SHSL(i, AMHighValues));
            }
            AMHighControl.TheList = AMHighList;
            AMHighControl.UpdateDisplayFunction =
                () => { return safeTableValue(AMHighValues, rig.filterHigh); };
            AMHighControl.UpdateRigFunction =
                (object v) => { rig.filterHigh = (int)v; };
            combos.Add(AMHighControl);

            NRList = new ArrayList();
            NRList.Add(new NRElement(KenwoodTS590.NRtype.off));
            NRList.Add(new NRElement(KenwoodTS590.NRtype.NR1));
            NRList.Add(new NRElement(KenwoodTS590.NRtype.NR2));
            NRControl.TheList=NRList;
            NRControl.UpdateDisplayFunction =
                () => { return rig.NRValue; };
            NRControl.UpdateRigFunction =
                (object v) => { rig.NRValue = (KenwoodTS590.NRtype)v; };
            combos.Add(NRControl);

            NRLevel1Control.LowValue = KenwoodTS590.NRLevel1Low;
            NRLevel1Control.HighValue = KenwoodTS590.NRLevel1High;
            NRLevel1Control.Increment = KenwoodTS590.NRLevel1Increment;
            NRLevel1Control.UpdateDisplayFunction =
                () => { return rig.NRLevel1; };
            NRLevel1Control.UpdateRigFunction =
                (int v) => { rig.NRLevel1 = v; };
            numberBoxes.Add(NRLevel1Control);
            subDependents.Add(new subDependentType(NRLevel1Control, NRControl,
                () => { return (rig.NRValue == KenwoodTS590.NRtype.NR1); }));

            NRLevel2Control.LowValue = KenwoodTS590.NRLevel2Low;
            NRLevel2Control.HighValue = KenwoodTS590.NRLevel2High;
            NRLevel2Control.Increment = KenwoodTS590.NRLevel2Increment;
            NRLevel2Control.UpdateDisplayFunction =
                () => { return rig.NRLevel2; };
            NRLevel2Control.UpdateRigFunction =
                (int v) => { rig.NRLevel2 = v; };
            numberBoxes.Add(NRLevel2Control);
            subDependents.Add(new subDependentType(NRLevel2Control, NRControl,
                () => { return (rig.NRValue == KenwoodTS590.NRtype.NR2); }));

            AGCGroupList = new ArrayList();
            AGCGroupList.Add(new AGCGroupElement(KenwoodTS590.AGCGroupType.off));
            AGCGroupList.Add(new AGCGroupElement(KenwoodTS590.AGCGroupType.slow));
            AGCGroupList.Add(new AGCGroupElement(KenwoodTS590.AGCGroupType.fast));
            AGCControl.TheList=AGCGroupList;
            AGCControl.UpdateDisplayFunction =
                () => { return rig.AGCGroup; };
            AGCControl.UpdateRigFunction =
                (object v) => { rig.AGCGroup = (KenwoodTS590.AGCGroupType)v; };
            combos.Add(AGCControl);

            AGCLevelControl.LowValue = KenwoodTS590.AGCLow;
            AGCLevelControl.HighValue = KenwoodTS590.AGCHigh;
            AGCLevelControl.Increment = KenwoodTS590.AGCIncrement;
            AGCLevelControl.UpdateDisplayFunction =
                () => { return rig.AGC; };
            AGCLevelControl.UpdateRigFunction =
                (int v) => { rig.AGC = v; };
            numberBoxes.Add(AGCLevelControl);
            subDependents.Add(new subDependentType(AGCLevelControl, AGCControl,
                () => { return (rig.AGCGroup != KenwoodTS590.AGCGroupType.off); }));

            NBList = new ArrayList();
            NBList.Add(new NBElement(KenwoodTS590.NBtype.off));
            NBList.Add(new NBElement(KenwoodTS590.NBtype.NB1));
            NBList.Add(new NBElement(KenwoodTS590.NBtype.NB2));
            NBControl.TheList = NBList;
            NBControl.UpdateDisplayFunction =
                () => { return rig.NBValue; };
            NBControl.UpdateRigFunction =
                (object v) => { rig.NBValue = (KenwoodTS590.NBtype)v; };
            combos.Add(NBControl);

            NBLevelControl.LowValue = KenwoodTS590.NBLevelLow;
            NBLevelControl.HighValue = KenwoodTS590.NBLevelHigh;
            NBLevelControl.Increment = KenwoodTS590.NBLevelIncrement;
            NBLevelControl.UpdateDisplayFunction =
                () => { return rig.NBLevel; };
            NBLevelControl.UpdateRigFunction =
                (int v) => { rig.NBLevel = v; };
            numberBoxes.Add(NBLevelControl);
            subDependents.Add(new subDependentType(NBLevelControl, NBControl,
                () => { return (rig.NBValue != KenwoodTS590.NBtype.off); }));

            notchList = new ArrayList();
            notchList.Add(new NotchElement(KenwoodTS590.notchType.off));
            notchList.Add(new NotchElement(KenwoodTS590.notchType.auto));
            notchList.Add(new NotchElement(KenwoodTS590.notchType.manual));
            NotchControl.TheList = notchList;
            NotchControl.UpdateDisplayFunction =
                () => { return rig.notchValue; };
            NotchControl.UpdateRigFunction =
                (object v) => { rig.notchValue = (KenwoodTS590.notchType)v; };
            combos.Add(NotchControl);

            NotchFreqControl.LowValue = KenwoodTS590.notchFreqLow;
            NotchFreqControl.HighValue = KenwoodTS590.notchFreqHigh;
            NotchFreqControl.Increment = KenwoodTS590.notchFreqIncrement;
            NotchFreqControl.UpdateDisplayFunction =
                () => { return rig.notchFreq; };
            NotchFreqControl.UpdateRigFunction =
                (int v) => { rig.notchFreq = v; };
            numberBoxes.Add(NotchFreqControl);
            subDependents.Add(new subDependentType(NotchFreqControl, NotchControl,
                () => { return (rig.notchValue == KenwoodTS590.notchType.manual);}));

            notchWidthList = new ArrayList();
            notchWidthList.Add(new notchWidthElement(KenwoodTS590.notchWidthType.normal));
            notchWidthList.Add(new notchWidthElement(KenwoodTS590.notchWidthType.wide));
            notchWidthList.Add(KenwoodTS590.notchWidthType.wide);
            NotchWidthControl.TheList = notchWidthList;
            NotchWidthControl.UpdateDisplayFunction =
                () => { return rig.notchWidth; };
            NotchWidthControl.UpdateRigFunction =
                (object v) => { rig.notchWidth = (KenwoodTS590.notchWidthType)v; };
            combos.Add(NotchWidthControl);
            subDependents.Add(new subDependentType(NotchWidthControl, NotchControl,
                () => { return (rig.notchValue == KenwoodTS590.notchType.manual); }));

            beatCancelList = new ArrayList();
            beatCancelList.Add(new beatCancelElement(KenwoodTS590.beatCancelType.off));
            beatCancelList.Add(new beatCancelElement(KenwoodTS590.beatCancelType.bc1));
            beatCancelList.Add(new beatCancelElement(KenwoodTS590.beatCancelType.bc2));
            BeatCancelControl.TheList = beatCancelList;
            BeatCancelControl.UpdateDisplayFunction =
                () => { return rig.beatCancel; };
            BeatCancelControl.UpdateRigFunction =
                (object v) => { rig.beatCancel = (KenwoodTS590.beatCancelType)v; };
            combos.Add(BeatCancelControl);

            // CW decode
            decodeList = new ArrayList();
            decodeList.Add(new offOnElement(Radios.AllRadios.OffOnValues.off));
            decodeList.Add(new offOnElement(Radios.AllRadios.OffOnValues.on));
            DecodeControl.TheList = decodeList;
            DecodeControl.UpdateDisplayFunction =
                () => { return rig.CWDecode; };
            DecodeControl.UpdateRigFunction =
                (object v) => { rig.CWDecode = (Radios.AllRadios.OffOnValues)v; };
            combos.Add(DecodeControl);
            subDependents.Add(new subDependentType(DecodeControl, DecodeControl,
                () => { return rig.TS590SG; }));

            // CW decode threshold
            DecodeThresholdControl.LowValue = Kenwood.CWDecodeThresholdMin;
            DecodeThresholdControl.HighValue = Kenwood.CWDecodeThresholdMax;
            DecodeThresholdControl.Increment = Kenwood.CWDecodeThresholdIncrement;
            DecodeThresholdControl.UpdateDisplayFunction =
                () => { return rig.CWDecodeThreshold; };
            DecodeThresholdControl.UpdateRigFunction =
                (int v) => { rig.CWDecodeThreshold = v; };
            numberBoxes.Add(DecodeThresholdControl);
            subDependents.Add(new subDependentType(DecodeThresholdControl, DecodeControl,
                () => { return (rig.CWDecode == AllRadios.OffOnValues.on); }));

            // readOnly textboxes
            textBoxes = new Collection<textBoxValueClass>();
            textBoxes.Add(new textBoxValueClass(SWRBox, SWRValues,
                () => { return rig.SWRRaw; }, rig));
            textBoxes.Add(new textBoxValueClass(CompBox, CompValues,
                () => { return rig.compRaw; }, rig));
            textBoxes.Add(new textBoxValueClass(ALCBox, ALCValues,
                () => { return rig.ALCRaw; }, rig));

            // buttons
            RXEQButton.Tag = RXEQButton.Text;
            buttons.Add(RXEQButton);
            subDependents.Add(new subDependentType(RXEQButton, null,
                () => { return rig.RXEQActive; }));
            TXEQButton.Tag = TXEQButton.Text;
            buttons.Add(TXEQButton);
            subDependents.Add(new subDependentType(TXEQButton, null,
                () => { return rig.TXEQActive; }));

            Control[] myControls = new Control[combos.Count + numberBoxes.Count + textBoxes.Count + buttons.Count];
            int id = 0;
            foreach (Combo c in combos) myControls[id++] = (Control)c;
            foreach (NumberBox n in numberBoxes)
            {
                myControls[id++] = (Control)n;
                n.BoxKeydown += BoxKeydownDefault;
            }
            foreach (textBoxValueClass t in textBoxes) myControls[id++] = (Control)t.box;
            foreach (Control b in buttons) myControls[id++] = (Control)b;
            IComparer mySort = new mySortClass();
            Array.Sort(myControls, mySort);

            // setup the mode change stuff.
            modeChange = new modeChangeClass(this);

            // setup the memory display.
            Form memDisp = new TS590Memories(rig);
            // setup RigFields
            rig.RigFields = new AllRadios.RigInfo(this, updateBoxes, memDisp, null, myControls);
        }

        private int safeTableValue(int[] arr, int id)
        {
            return (id < arr.Length) ? arr[id] : 0;
        }

        private void Filters_Load(object sender, EventArgs e)
        {
            Tracing.TraceLine("TS590Filters_load", TraceLevel.Info);
        }

        private delegate void rtn(Control c);
        private rtn enab = (Control c) =>
        {
            if (!c.Enabled)
            {
                c.Enabled = true;
                c.Visible = true;
                c.BringToFront();
            }
        };
        private rtn disab = (Control c) =>
        {
            if (c.Enabled)
            {
                c.Enabled = false;
                c.Visible = false;
                c.SendToBack();
            }
        };

        private void updateBoxes()
        {
            Tracing.TraceLine("updateBoxes", TraceLevel.Verbose);
            if (rig.Mode == null)
            {
                Tracing.TraceLine("updateBoxes:no mode", TraceLevel.Info);
                return;
            }

            try
            {
                // enable/disable boxes for this mode.
                modeChange.enableDisable(rig.Mode, rig.DataMode);

                foreach (Combo c in combos)
                {
                    if (c.Enabled)
                    {
                        c.UpdateDisplay();
                    }
                }

                foreach (NumberBox c in numberBoxes)
                {
                    if (c.Enabled)
                    {
                        c.UpdateDisplay();
                    }
                }

                // Currently the text boxes are always enabled.
                foreach (textBoxValueClass t in textBoxes)
                {
                    t.Value();
                }

                // Check sub-dependencies (must follow other checks).
                foreach (subDependentType s in subDependents)
                {
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

                // EQ forms
                if ((RXForm != null) && RXActive) ((ts590eq)RXForm).UpdateBoxes();
                if ((TXForm != null) && TXActive) ((ts590eq)TXForm).UpdateBoxes();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("updateBoxes:" + ex.Message + ex.StackTrace, TraceLevel.Error);
            }
        }

        private class modeChangeClass
        {
            // A mode's filter controls are enabled when that mode is active.
            // First, the specified controls for the other modes are disabled,
            // unless they're just going to be enabled again.
            // Note each mode has two arrays, without and with data mode.
            private class controlsClass
            {
                public Control[,] controls;
                public controlsClass(Control[] wo,Control[] w)
                {
                    int controlDim = Math.Max(wo.Length, w.Length);
                    controls = new Control[2, controlDim];
                    for (int i = 0; i < wo.Length; i++)
                    {
                        controls[0, i] = wo[i];
                    }
                    for (int i = 0; i < w.Length; i++)
                    {
                        controls[1, i] = w[i];
                    }
                }
            }
            private controlsClass[] modeControls;
            private TS590Filters parent;
            public modeChangeClass(TS590Filters p)
            {
                parent = p;
                modeControls = new controlsClass[Kenwood.myModeTable.Length];

                // setup the mode to combobox mapping, without and with datamode.
                modeControls[(int)Kenwood.modes.lsb] = new controlsClass(
                    new Control[] {
                        parent.DataModeControl,
                        parent.ProcessorControl, parent.MicGainControl,
                        parent.VoxDelayControl, parent.VoxGainControl,
                        parent.ProcessorInLevelControl, parent.ProcessorOutLevelControl, parent.TXMonitorControl,
                        parent.SSBFMFMDLowControl, parent.SSBFMFMDHighControl,
                        parent.SSBDOffsetControl, parent.SSBDWidthControl,
                        parent.NBControl, parent.NotchControl, parent.BeatCancelControl,
                        parent.AGCControl,
                    },
                    new Control[] {
                        parent.DataModeControl,
                        parent.ProcessorControl, parent.MicGainControl,
                        parent.VoxDelayControl, parent.VoxGainControl,
                        parent.ProcessorInLevelControl, parent.ProcessorOutLevelControl, parent.TXMonitorControl,
                        parent.SSBFMFMDLowControl, parent.SSBFMFMDHighControl,
                        parent.SSBDOffsetControl, parent.SSBDWidthControl,
                        parent.NBControl, parent.NotchControl, parent.BeatCancelControl,
                        parent.AGCControl,
                    });
                modeControls[(int)Kenwood.modes.usb] = new controlsClass(
                    new Control[] {
                        parent.DataModeControl,
                        parent.ProcessorControl, parent.MicGainControl,
                        parent.VoxDelayControl, parent.VoxGainControl,
                        parent.ProcessorInLevelControl, parent.ProcessorOutLevelControl, parent.TXMonitorControl,
                        parent.SSBFMFMDLowControl, parent.SSBFMFMDHighControl,
                        parent.SSBDOffsetControl, parent.SSBDWidthControl,
                        parent.NBControl, parent.NotchControl, parent.BeatCancelControl,
                        parent.AGCControl,
                    },
                    new Control[] {
                        parent.DataModeControl,
                        parent.ProcessorControl, parent.MicGainControl,
                        parent.VoxDelayControl, parent.VoxGainControl,
                        parent.ProcessorInLevelControl, parent.ProcessorOutLevelControl, parent.TXMonitorControl,
                        parent.SSBFMFMDLowControl, parent.SSBFMFMDHighControl,
                        parent.SSBDOffsetControl, parent.SSBDWidthControl,
                        parent.NBControl, parent.NotchControl, parent.BeatCancelControl,
                        parent.AGCControl,
                    });
                modeControls[(int)Kenwood.modes.cw] = new controlsClass(
                    new Control[] {
                        parent.SpeedControl, parent.BreakinDelayControl,
                        parent.CarrierLevelControl,
                        parent.DecodeControl, parent.DecodeThresholdControl,
                        parent.CWOffsetControl, parent.CWWidthControl,
                        parent.NBControl, parent.NotchControl,
                        parent.AGCControl,
                    },
                    new Control[] {});
                modeControls[(int)Kenwood.modes.cwr] = new controlsClass(
                    new Control[] {
                        parent.SpeedControl, parent.BreakinDelayControl,
                        parent.CarrierLevelControl,
                        parent.DecodeControl, parent.DecodeThresholdControl,
                        parent.CWOffsetControl, parent.CWWidthControl,
                        parent.NBControl, parent.NotchControl,
                        parent.AGCControl,
                    },
                    new Control[] {});
                modeControls[(int)Kenwood.modes.fsk] = new controlsClass(
                    new Control[] {
                        parent.CarrierLevelControl,
                        parent.VoxDelayControl, parent.TXMonitorControl,
                        parent.FSKWidthControl, parent.NBControl, parent.NotchControl,
                        parent.AGCControl,
                    },
                    new Control[] {});
                modeControls[(int)Kenwood.modes.fskr] = new controlsClass(
                    new Control[] {
                        parent.CarrierLevelControl,
                        parent.VoxDelayControl, parent.TXMonitorControl,
                        parent.FSKWidthControl, parent.NBControl, parent.NotchControl,
                        parent.AGCControl,
                    },
                    new Control[] {});
                modeControls[(int)Kenwood.modes.fm] = new controlsClass(
                    new Control[] {
                        parent.DataModeControl, parent.ToneCTCSSControl,
                        parent.ToneFreqControl, parent.CTCSSFreqControl,
                        parent.ProcessorControl, parent.MicGainControl,
                        parent.VoxDelayControl, parent.VoxGainControl,
                        parent.ProcessorInLevelControl, parent.ProcessorOutLevelControl, parent.TXMonitorControl,
                        parent.SSBFMFMDLowControl, parent.SSBFMFMDHighControl,
                        parent.FMWidthControl,
                    },
                    new Control[] {
                        parent.DataModeControl, parent.ToneCTCSSControl,
                        parent.ToneFreqControl, parent.CTCSSFreqControl,
                        parent.ProcessorControl, parent.MicGainControl,
                        parent.VoxDelayControl, parent.VoxGainControl,
                        parent.ProcessorInLevelControl, parent.ProcessorOutLevelControl, parent.TXMonitorControl,
                        parent.SSBFMFMDLowControl, parent.SSBFMFMDHighControl,
                        parent.FMWidthControl,
                    });
                modeControls[(int)Kenwood.modes.am] = new controlsClass(
                    new Control[] { parent.MicGainControl, parent.AMLowControl, parent.AMHighControl,
                        parent.NBControl, parent.BeatCancelControl,
                        parent.AGCControl, parent.TXMonitorControl,
                    },
                    new Control[] { });
            }
            private AllRadios.ModeValue oldMode = Kenwood.myModeTable[0];
            private AllRadios.DataModes oldDataMode = AllRadios.DataModes.unset;
            public void enableDisable(AllRadios.ModeValue mode, AllRadios.DataModes dataMode)
            {
                // Just quit if the mode hasn't changed.
                if ((mode == oldMode) && (dataMode == oldDataMode)) return;
                oldMode = mode;
                oldDataMode = dataMode;
                int mod = mode.id;
                // quit if no controls for this mode.
                if (modeControls[mod] == null) return;
                // dat is the datamode index.
                int dat = (dataMode == AllRadios.DataModes.off) ? 0 : 1;
                // enables holds the controls to be enabled.
                Control[] enables = new Control[modeControls[mod].controls.GetLength(1)];
                for (int i=0; i<enables.Length; i++)
                {
                    // We need to quit if no more controls for this mode/datamode.
                    if (modeControls[mod].controls[dat, i] == null) break;
                    enables[i] = modeControls[mod].controls[dat, i];
                }
                parent.SuspendLayout();
                for (int i = 0; i < modeControls.Length; i++)
                {
                    if (modeControls[i] == null) continue;
                    for (int j = 0; j < modeControls[i].controls.GetLength(0); j++)
                    {
                        for (int k = 0; k < modeControls[i].controls.GetLength(1); k++)
                        {
                            Control c = modeControls[i].controls[j, k];
                            if (c == null) break;
                            if (Array.IndexOf(enables,c) >= 0)
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
                }
                parent.ResumeLayout();
            }
        }
        private modeChangeClass modeChange;

        private void BoxKeydownDefault(object sender, KeyEventArgs e)
        {
            OnKeyDown(e);
        }

        private void RXEQButton_Click(object sender, EventArgs e)
        {
            if (RXForm == null) RXForm = new ts590eq(rig, false);
            RXActive = true;
            RXForm.ShowDialog();
            RXActive = false;
        }

        private void TXEQButton_Click(object sender, EventArgs e)
        {
            if (TXForm == null) TXForm = new ts590eq(rig, true);
            TXActive = true;
            TXForm.ShowDialog();
            TXActive = false;
        }
    }
}
