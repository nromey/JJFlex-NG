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
    public partial class TS2000Filters : UserControl
    {
        protected KenwoodTS2000 rig;
        private Collection<Combo> combos;
        private Collection<NumberBox> numberBoxes;

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
        private ArrayList toneFreqList, CTCSSFreqList;

        private enum fmModes2000
        {
            Naro,
            Wide,
        }
        private class FMWidthElement
        {
            private fmModes2000 val;
            public string Display { get { return val.ToString(); } }
            public fmModes2000 RigItem { get { return val; } }
            public FMWidthElement(fmModes2000 v)
            {
                val = v;
            }
        }
        private ArrayList FMWidthList;

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

        internal class stepElement
        {
            private float val;
            public string Display { get { return (val).ToString(); } }
            public float RigItem { get { return val; } }
            public stepElement(float v)
            {
                val = v;
            }
        }
        private ArrayList StepSizeSSBList, StepSizeAMFMList;

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
        private ArrayList RXAntList, rfAttList, preAmpList, processorList, cwShiftList;

        internal class OnOffElement
        {
            private Radios.AllRadios.OffOnValues val;
            public string Display { get { return val.ToString(); } }
            public Radios.AllRadios.OffOnValues RigItem { get { return val; } }
            public OnOffElement(Radios.AllRadios.OffOnValues v)
            {
                val = v;
            }
        }
        private ArrayList ReverseList;

        internal class OffNumericElement
        {
            public int val;
            public string Display
            {
                get
                {
                    return (val == 0) ? "Off" : val.ToString();
                }
            }
            public int RigItem { get { return val; } }
            public OffNumericElement(int v)
            {
                val = v;
            }
        }
        private ArrayList TXMonitorList;

        private numericElement[] cwWidthValues =
        {new numericElement(50), new numericElement(80),
         new numericElement(100), new numericElement(150),
         new numericElement(200),
         new numericElement(300), new numericElement(400),
         new numericElement(500), new numericElement(600),
         new numericElement(1000),
         new numericElement(2000)};
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
        {1400,1600,1800,2000,2200,2400,2600,2800,
         3000,3400,4000,5000};
        private int[] AMLowValues = { 0, 100, 200, 500 };
        private int[] AMHighValues = { 2500, 3000, 4000, 5000 };
        // width uses SH, offset uses SL.
        private int[] SSBDOffsetValues = { 1000, 1100, 1200, 1300, 1400, 1500, 1600, 1700, 1800, 1900, 2000, 2100, 2210 };
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
            KenwoodTS2000.NRtype val;
            public string Display
            {
                get { return val.ToString(); }
            }
            public KenwoodTS2000.NRtype RigItem
            {
                get { return val; }
            }
            public NRElement(KenwoodTS2000.NRtype v)
            {
                val = v;
            }
        }
        // the NR Level1 value can be auto, or 1 - 9.
        private class NRLevel1Element
        {
            int val;
            public string Display
            {
                get 
                {
                    if (val == 0) return "auto";
                    else return val.ToString();
                }
            }
            public int RigItem
            {
                get { return val; }
            }
            public NRLevel1Element(int v)
            {
                val = v;
            }
        }
        private ArrayList NRList, NRLevel1List;

        private ArrayList agcList, nbList;

        private class bcElement
        {
            KenwoodTS2000.bcType val;
            public string Display
            {
                get
                {
                    return val.ToString();
                }
            }
            public KenwoodTS2000.bcType RigItem
            {
                get { return val; }
            }
            public bcElement(KenwoodTS2000.bcType v)
            {
                val = v;
            }
        }
        private ArrayList bcList;
        private ArrayList notchList;

        // TextBox setup data.
        // The ReadOnly TextBoxes are set using a table of values.
        private class textBoxValueClass
        {
            public bool Active; // true if in use.
            public char MeterID; // see Kenwood meter chars.
            public int PeakPeriod = 0; // in milliseconds, 0 for default.
            public TextBox box;
            private string[] tbl;
            public IndexedPeakType.ValueDel rigValue;
            private delegate void del(int val);
            private del dispFunc;
            private TS2000Filters parent;
            public textBoxValueClass(char id, TextBox tb, string[] t,
                IndexedPeakType.ValueDel rtn, TS2000Filters p)
            {
                MeterID = id;
                box = tb;
                tbl = t;
                rigValue = rtn;
                dispFunc = (int val) => { box.Text = ((val >= 0) && (val < tbl.Length)) ? tbl[val] : ""; };
                parent = p;
            }
            private int oldVal = -1;
            public void Value()
            {
                int val = parent.peakReader.Read();
                if (val != oldVal)
                {
                    oldVal = val;
                    if (box.InvokeRequired)
                    {
                        box.Invoke(dispFunc, new object[] { val });
                    }
                    else dispFunc(val);
                    Tracing.TraceLine(box.Name + ":" + tbl[val],TraceLevel.Info);
                }
            }
        }
        private Collection<textBoxValueClass> textBoxes;
        private textBoxValueClass SWRMeter, CompMeter, ALCMeter;
        private textBoxValueClass peakUser; // Only one user of the peak reader function.
        private IndexedPeakType peakReader;
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

        public TS2000Filters(KenwoodTS2000 r)
        {
            InitializeComponent();
            rig = r;
            Tracing.TraceLine("TS2000Filters constructor", TraceLevel.Info);

            // setup the boxes
            combos = new Collection<Combo>();
            numberBoxes = new Collection<NumberBox>();
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

            // repeater offset
            OffsetFreqControl.LowValue = rig.MinOffsetFrequency;
            OffsetFreqControl.HighValue = rig.MaxOffsetFrequency;
            OffsetFreqControl.Increment = rig.OffsetFrequencyStep;
            OffsetFreqControl.UpdateDisplayFunction =
                () => { return rig.OffsetFrequency/1000; };
            OffsetFreqControl.UpdateRigFunction =
                (int v) => { rig.OffsetFrequency = (v * 1000); };
            numberBoxes.Add(OffsetFreqControl);

            // Step sizes
            StepSizeSSBList = new ArrayList();
            foreach (float f in KenwoodTS2000.stepSizesSSB)
            {
                StepSizeSSBList.Add(new stepElement(f));
            }
            StepSizeSSBCWFSKControl.TheList = StepSizeSSBList;
            StepSizeSSBCWFSKControl.UpdateDisplayFunction =
                () => { return rig.StepSize; };
            StepSizeSSBCWFSKControl.UpdateRigFunction =
                (object v) => { rig.StepSize = (float)v; };
            combos.Add(StepSizeSSBCWFSKControl);

            StepSizeAMFMList = new ArrayList();
            foreach (float f in KenwoodTS2000.stepSizesAMFM)
            {
                StepSizeAMFMList.Add(new stepElement(f));
            }
            StepSizeAMFMControl.TheList = StepSizeAMFMList;
            StepSizeAMFMControl.UpdateDisplayFunction =
                () => { return rig.StepSize; };
            StepSizeAMFMControl.UpdateRigFunction =
                (object v) => { rig.StepSize = (float)v; };
            combos.Add(StepSizeAMFMControl);

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
            FMWidthList.Add(new FMWidthElement(fmModes2000.Naro));
            FMWidthList.Add(new FMWidthElement(fmModes2000.Wide));
            FMWidthControl.TheList = FMWidthList;
            FMWidthControl.UpdateDisplayFunction =
                () => { return (fmModes2000)rig.filterWidth; };
            FMWidthControl.UpdateRigFunction =
                (object v) => { rig.filterWidth = (int)v; };
            combos.Add(FMWidthControl);

            // FM reverse
            ReverseList = new ArrayList();
            ReverseList.Add(new offOnElement(Radios.AllRadios.OffOnValues.off));
            ReverseList.Add(new offOnElement(Radios.AllRadios.OffOnValues.on));
            ReverseControl.TheList = ReverseList;
            ReverseControl.UpdateDisplayFunction =
                () => { return rig.Reverse; };
            ReverseControl.UpdateRigFunction =
                (object v) => { rig.Reverse = (Radios.AllRadios.OffOnValues)v; };
            combos.Add(ReverseControl);
            subDependents.Add(new subDependentType(ReverseControl, ReverseControl,
                () => { return !rig.Split; }));

            // Antenna, 1 based
            TXAntList = new ArrayList();
            for (int i=1; i<rig.TXAnts + 1; i++)
            {
                TXAntList.Add(new numericElement(i)); // value is 1 based.
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

            // vox enabled is shown in main display.
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
                TXMonitorList.Add(new OffNumericElement(i));
            }
            TXMonitorControl.TheList = TXMonitorList;
            TXMonitorControl.UpdateDisplayFunction =
                () => { return rig.TXMonitor; };
            TXMonitorControl.UpdateRigFunction =
                (object v) => { rig.TXMonitor = (int)v; };
            combos.Add(TXMonitorControl);

            // transmit power
            XmitPowerControl.LowValue = rig.XmitPowerMin;
            XmitPowerControl.HighValue = rig.XmitPowerMax;
            XmitPowerControl.Increment = rig.XmitPowerIncrement;
            XmitPowerControl.UpdateDisplayFunction =
                () => { return rig.XmitPower; };
            XmitPowerControl.UpdateRigFunction =
                (int v) => { rig.XmitPower = v; };
            numberBoxes.Add(XmitPowerControl);

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

            SSBDOffsetList = new ArrayList();
            for (int i = 0; i < SSBDOffsetValues.Length; i++)
            {
                SSBDOffsetList.Add(new SHSL(i, SSBDOffsetValues));
            }
            SSBDOffsetControl.TheList = SSBDOffsetList;
            SSBDOffsetControl.UpdateDisplayFunction =
                () => { return safeTableValue(SSBDOffsetValues,rig.filterLow); };
            SSBDOffsetControl.UpdateRigFunction =
                (object v) => { rig.filterLow = (int)v; };
            combos.Add(SSBDOffsetControl);

            SSBDWidthList = new ArrayList();
            for (int i = 0; i < SSBDWidthValues.Length; i++)
            {
                SSBDWidthList.Add(new SHSL(i, SSBDWidthValues));
            }
            SSBDWidthControl.TheList = SSBDWidthList;
            SSBDWidthControl.UpdateDisplayFunction =
                () => { return safeTableValue(SSBDWidthValues,rig.filterHigh); };
            SSBDWidthControl.UpdateRigFunction =
                (object v) => { rig.filterHigh = (int)v; };
            combos.Add(SSBDWidthControl);

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
            NRList.Add(new NRElement(KenwoodTS2000.NRtype.off));
            NRList.Add(new NRElement(KenwoodTS2000.NRtype.NR1));
            NRList.Add(new NRElement(KenwoodTS2000.NRtype.NR2));
            NRControl.TheList = NRList;
            NRControl.UpdateDisplayFunction =
                () => { return rig.NRValue; };
            NRControl.UpdateRigFunction =
                (object v) => { rig.NRValue = (KenwoodTS2000.NRtype)v; };
            combos.Add(NRControl);

            NRLevel1List = new ArrayList();
            for (int i = KenwoodTS2000.NRLevel1Low; i < KenwoodTS2000.NRLevel1High; i += KenwoodTS2000.NRLevel1Increment)
            {
                NRLevel1List.Add(new NRLevel1Element(i));
            }
            NRLevel1Control.TheList = NRLevel1List;
            NRLevel1Control.UpdateDisplayFunction =
                () => { return rig.NRLevel1; };
            NRLevel1Control.UpdateRigFunction =
                (object v) => { rig.NRLevel1 = (int)v; };
            combos.Add(NRLevel1Control);
            subDependents.Add(new subDependentType(NRLevel1Control, NRControl,
                () => { return (rig.NRValue == KenwoodTS2000.NRtype.NR1); }));

            NRLevel2Control.LowValue = KenwoodTS2000.NRLevel2Low;
            NRLevel2Control.HighValue = KenwoodTS2000.NRLevel2High;
            NRLevel2Control.Increment = KenwoodTS2000.NRLevel2Increment;
            NRLevel2Control.UpdateDisplayFunction =
                () => { return rig.NRLevel2; };
            NRLevel2Control.UpdateRigFunction =
                (int v) => { rig.NRLevel2 = v; };
            numberBoxes.Add(NRLevel2Control);
            subDependents.Add(new subDependentType(NRLevel2Control, NRControl,
                () => { return (rig.NRValue == KenwoodTS2000.NRtype.NR2); }));

            agcList = new ArrayList();
            agcList.Add(new offOnElement(AllRadios.OffOnValues.off));
            agcList.Add(new offOnElement(AllRadios.OffOnValues.on));
            AGCControl.TheList = agcList;
            AGCControl.UpdateDisplayFunction =
                () => { return rig.agcOnOff; };
            AGCControl.UpdateRigFunction =
                (object v) => { rig.agcOnOff = (AllRadios.OffOnValues)v; };
            combos.Add(AGCControl);

            AGCLevelControl.LowValue = KenwoodTS2000.agcLevelLow;
            AGCLevelControl.HighValue = KenwoodTS2000.agcLevelHigh;
            AGCLevelControl.Increment = KenwoodTS2000.agcLevelIncrement;
            AGCLevelControl.UpdateDisplayFunction =
                () => { return rig.agcLevel; };
            AGCLevelControl.UpdateRigFunction =
                (int v) => { rig.agcLevel = v; };
            numberBoxes.Add(AGCLevelControl);
            subDependents.Add(new subDependentType(AGCLevelControl, AGCControl,
                () => { return (rig.agcOnOff == AllRadios.OffOnValues.on); }));

            nbList = new ArrayList();
            nbList.Add(new offOnElement(AllRadios.OffOnValues.off));
            nbList.Add(new offOnElement(AllRadios.OffOnValues.on));
            NBControl.TheList = nbList;
            NBControl.UpdateDisplayFunction =
                () => { return rig.NoiseBlanker; };
            NBControl.UpdateRigFunction =
                (object v) => { rig.NoiseBlanker = (AllRadios.OffOnValues)v; };
            combos.Add(NBControl);

            NBLevelControl.LowValue = KenwoodTS2000.nbLevelLow;
            NBLevelControl.HighValue = KenwoodTS2000.nbLevelHigh;
            NBLevelControl.Increment = KenwoodTS2000.nbLevelIncrement;
            NBLevelControl.UpdateDisplayFunction =
                () => { return rig.NBLevel; };
            NBLevelControl.UpdateRigFunction =
                (int v) => { rig.NBLevel = v; };
            numberBoxes.Add(NBLevelControl);
            subDependents.Add(new subDependentType(NBLevelControl, NBControl,
                () => { return (rig.NoiseBlanker == AllRadios.OffOnValues.on); }));

            bcList = new ArrayList();
            bcList.Add(new bcElement(KenwoodTS2000.bcType.off));
            bcList.Add(new bcElement(KenwoodTS2000.bcType.auto));
            bcList.Add(new bcElement(KenwoodTS2000.bcType.manual));
            BCConntrol.TheList = bcList;
            BCConntrol.UpdateDisplayFunction =
                () => { return rig.bc; };
            BCConntrol.UpdateRigFunction =
                (object v) => { rig.bc = (KenwoodTS2000.bcType)v; };
            combos.Add(BCConntrol);

            BCLevelControl.LowValue = KenwoodTS2000.bcLevelLow;
            BCLevelControl.HighValue = KenwoodTS2000.bcLevelHigh;
            BCLevelControl.Increment = KenwoodTS2000.bcLevelIncrement;
            BCLevelControl.UpdateDisplayFunction =
                () => { return rig.bcLevel; };
            BCLevelControl.UpdateRigFunction =
                (int v) => { rig.bcLevel = v; };
            numberBoxes.Add(BCLevelControl);
            subDependents.Add(new subDependentType(BCLevelControl, BCConntrol,
                () => { return (rig.bc == KenwoodTS2000.bcType.manual); }));

            notchList = new ArrayList();
            notchList.Add(new offOnElement(AllRadios.OffOnValues.off));
            notchList.Add(new offOnElement(AllRadios.OffOnValues.on));
            NotchControl.TheList = notchList;
            NotchControl.UpdateDisplayFunction =
                () => { return rig.AutoNotch; };
            NotchControl.UpdateRigFunction =
                (object v) => { rig.AutoNotch = (AllRadios.OffOnValues)v; };
            combos.Add(NotchControl);

            NotchLevelControl.LowValue = KenwoodTS2000.notchLevelLow;
            NotchLevelControl.HighValue = KenwoodTS2000.notchLevelHigh;
            NotchLevelControl.Increment = KenwoodTS2000.notchLevelIncrement;
            NotchLevelControl.UpdateDisplayFunction =
                () => { return rig.notchLevel; };
            NotchLevelControl.UpdateRigFunction =
                (int v) => { rig.notchLevel = v; };
            numberBoxes.Add(NotchLevelControl);
            subDependents.Add(new subDependentType(NotchLevelControl, NotchControl,
                () => { return (rig.AutoNotch == AllRadios.OffOnValues.on); }));

            // readOnly textboxes
            SWRMeter = new textBoxValueClass(Kenwood.RMSWR, SWRBox, SWRValues,
                () => { return rig.SWRRaw; }, this);
            CompMeter = new textBoxValueClass(Kenwood.RMComp, CompBox, CompValues,
                () => { return rig.compRaw; }, this);
            ALCMeter = new textBoxValueClass(Kenwood.RMALC, ALCBox, ALCValues,
                () => { return rig.ALCRaw; }, this);
            textBoxes=new Collection<textBoxValueClass>();
            textBoxes.Add(SWRMeter);
            textBoxes.Add(CompMeter);
            textBoxes.Add(ALCMeter);

            Control[] myControls = new Control[combos.Count + numberBoxes.Count + textBoxes.Count];

            // Sort ScreenFields control list by text.
            for (int i = 0; i < myControls.Length; i++)
            {
                int id;
                if (i < combos.Count)
                {
                    myControls[i] = (Control)combos[i];
                    myControls[i].Tag = combos[i].Header;
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
                    myControls[i] = (Control)textBoxes[id].box;
                    myControls[i].Tag = (string)textBoxes[id].box.Tag;
                }
            }
            IComparer mySort = new mySortClass();
            Array.Sort(myControls, mySort);

            // setup the mode change stuff.
            modeChange = new modeChangeClass(this);

            // setup the memory display.
            Form memDisp = new TS2000Memories(rig);
            // setup RigFields
            rig.RigFields = new AllRadios.RigInfo(this, updateBoxes, memDisp, null, myControls);
        }
        private int safeTableValue(int[] arr, int id)
        {
            return (id < arr.Length) ? arr[id] : 0;
        }

        private void Filters_Load(object sender, EventArgs e)
        {
            Tracing.TraceLine("TS2000Filters_load", TraceLevel.Info);
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
            try
            {
                // enable/disable boxes for this mode.
                modeChange.enableDisable(rig.Mode);

                // Check sub-dependencies
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

                if ((peakUser != null) && peakUser.Active)
                {
                    peakUser.Value();
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("updateBoxes:" + ex.Message + ex.StackTrace, TraceLevel.Error);
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
            private TS2000Filters parent;
            public modeChangeClass(TS2000Filters p)
            {
                parent = p;
                modeControls = new controlsClass[Kenwood.myModeTable.Length];

                // setup the mode to combobox mapping.
                modeControls[(int)Kenwood.modes.lsb] = new controlsClass(
                    new Control[] {
                        parent.ProcessorControl, parent.MicGainControl,
                        parent.VoxDelayControl, parent.VoxGainControl,
                        parent.ProcessorInLevelControl, parent.ProcessorOutLevelControl, parent.TXMonitorControl,
                        parent.SSBFMFMDLowControl, parent.SSBFMFMDHighControl,
                        parent.AGCControl, parent.NBControl,
                        parent.BCConntrol, parent.NotchControl,
                        parent.StepSizeSSBCWFSKControl,
                    });
                modeControls[(int)Kenwood.modes.usb] = new controlsClass(
                    new Control[] {
                        parent.ProcessorControl, parent.MicGainControl,
                        parent.VoxDelayControl, parent.VoxGainControl,
                        parent.ProcessorInLevelControl, parent.ProcessorOutLevelControl, parent.TXMonitorControl,
                        parent.SSBFMFMDLowControl, parent.SSBFMFMDHighControl,
                        parent.AGCControl, parent.NBControl,
                        parent.BCConntrol, parent.NotchControl,
                        parent.StepSizeSSBCWFSKControl,
                    });
                modeControls[(int)Kenwood.modes.cw] = new controlsClass(
                    new Control[] {
                        parent.SpeedControl, parent.BreakinDelayControl,
                        parent.CarrierLevelControl,
                        parent.CWOffsetControl, parent.CWWidthControl,
                        parent.AGCControl, parent.NBControl,
                        parent.BCConntrol, parent.StepSizeSSBCWFSKControl,
                    });
                modeControls[(int)Kenwood.modes.cwr] = new controlsClass(
                    new Control[] {
                        parent.SpeedControl, parent.BreakinDelayControl,
                        parent.CarrierLevelControl,
                        parent.CWOffsetControl, parent.CWWidthControl,
                        parent.AGCControl, parent.NBControl,
                        parent.BCConntrol, parent.StepSizeSSBCWFSKControl,
                    });
                modeControls[(int)Kenwood.modes.fsk] = new controlsClass(
                    new Control[] {
                        parent.CarrierLevelControl,
                        parent.VoxDelayControl, parent.TXMonitorControl,
                        parent.FSKWidthControl,
                        parent.AGCControl, parent.NBControl,
                        parent.BCConntrol, parent.StepSizeSSBCWFSKControl,
                    });
                modeControls[(int)Kenwood.modes.fskr] = new controlsClass(
                    new Control[] {
                        parent.CarrierLevelControl,
                        parent.VoxDelayControl, parent.TXMonitorControl,
                        parent.FSKWidthControl,
                        parent.AGCControl, parent.NBControl,
                        parent.BCConntrol, parent.StepSizeSSBCWFSKControl,
                    });
                modeControls[(int)Kenwood.modes.fm] = new controlsClass(
                    new Control[] {
                        parent.OffsetFreqControl,
                        parent.ToneCTCSSControl, parent.ReverseControl,
                        parent.ToneFreqControl, parent.CTCSSFreqControl,
                        parent.ProcessorControl, parent.MicGainControl,
                        parent.VoxDelayControl, parent.VoxGainControl,
                        parent.ProcessorInLevelControl, parent.ProcessorOutLevelControl, parent.TXMonitorControl,
                        parent.SSBFMFMDLowControl, parent.SSBFMFMDHighControl,
                        parent.StepSizeAMFMControl, parent.FMWidthControl,
                    });
                modeControls[(int)Kenwood.modes.am] = new controlsClass(
                    new Control[] { 
                        parent.MicGainControl, parent.AMLowControl, parent.AMHighControl, parent.TXMonitorControl,
                        parent.AGCControl, parent.NBControl,
                        parent.BCConntrol, parent.StepSizeAMFMControl,
                    });
            }

            private AllRadios.ModeValue oldMode = Kenwood.myModeTable[0];
            public void enableDisable(Kenwood.ModeValue mode)
            {
                // Just quit if the mode hasn't changed.
                if (mode == oldMode) return;
                oldMode = mode;
                int mod = mode.id;
                // quit if no controls for this mode.
                if (modeControls[mod] == null) return;
                // enables holds the controls to be enabled.
                Control[] enables = new Control[modeControls[mod].controls.Length];
                for (int i=0; i<enables.Length; i++)
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

        private void selectMeter(textBoxValueClass meter)
        {
            rig.Callouts.safeSend(Kenwood.BldCmd("RM" + meter.MeterID.ToString()));
            if (peakUser != null) peakUser.Active = false;
            if (peakReader != null) peakReader.Finished();
            peakReader = new IndexedPeakType(meter.rigValue, meter.PeakPeriod);
            meter.Active = true;
            peakUser = meter;
        }

        private void SWRBox_Enter(object sender, EventArgs e)
        {
            selectMeter(SWRMeter);
        }

        private void SWRBox_Click(object sender, EventArgs e)
        {
            selectMeter(SWRMeter);
        }

        private void CompBox_Enter(object sender, EventArgs e)
        {
            selectMeter(CompMeter);
        }

        private void CompBox_Click(object sender, EventArgs e)
        {
            selectMeter(CompMeter);
        }

        private void ALCBox_Enter(object sender, EventArgs e)
        {
            selectMeter(ALCMeter);
        }

        private void ALCBox_Click(object sender, EventArgs e)
        {
            selectMeter(ALCMeter);
        }

        private void TS2000Filters_ControlRemoved(object sender, ControlEventArgs e)
        {
            if (peakUser != null) peakUser.Active = false;
            if (peakReader != null) peakReader.Finished();
        }
    }
}
