using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using Flex.Smoothlake.FlexLib;
using Flex.Util;
using HamBands;
using JJTrace;
using MsgLib;
using RadioBoxes;

namespace Radios
{
    public partial class Flex6300Filters : UserControl
    {
        private const string badLowFreq = "The low value must be a valid frequency";
        private const string badHighFreq = "The high value must be a valid frequency, and > the low value.";
        internal static Flex6300 rig;
        private Radio theRadio
        {
            get { return (rig != null) ? rig.theRadio : null; }
        }

        internal string OperatorsDirectory;
        private string ConfigFilename { get { return (OperatorsDirectory == null) ? null : OperatorsDirectory + '\\' + "configinfo.xml"; } }
        public class ConfigInfo
        {
            /// <summary>
            /// Auto processor setting, see AutoProcValues.
            /// </summary>
            public string AutoProc = autoprocValues[0];
        }
        internal ConfigInfo OpsConfigInfo;

        private Collection<Combo> combos;
        private Collection<ComboBox> comboBoxes;
        private Collection<NumberBox> numberBoxes;
        private Collection<InfoBox> infoBoxes;
        private Collection<Control> panControls;
        private Collection<Button> buttonControls;
        internal delegate void modeChangeFuncDel(Flex6300 rig);
        private Collection<modeChangeFuncDel> modeChangeSpecials;
        private FlexTNF flexTNF;
        private delegate void specialDel();
        private Collection<specialDel> specials;

        internal const int filterLowMinimum = -12000;
        internal const int filterLowMaximum = 12000;
        internal const int filterLowIncrement = 50;
        internal const int filterHighMinimum = -12000;
        internal const int filterHighMaximum = 12000;
        internal const int filterHighIncrement = 50;

        private class AGCSpeedElement
        {
            private AGCMode val;
            public string Display { get { return val.ToString(); } }
            public AGCMode RigItem { get { return val; } }
            public AGCSpeedElement(AGCMode v)
            {
                val = v;
            }
        }
        private AGCSpeedElement[] AGCSpeedItems =
        {
            new AGCSpeedElement(AGCMode.Off),
            new AGCSpeedElement(AGCMode.Slow),
            new AGCSpeedElement(AGCMode.Medium),
            new AGCSpeedElement(AGCMode.Fast),
        };
        private ArrayList AGCSpeedList;
        private ArrayList ANFList, APFList;
        private ArrayList noiseBlankerList, PreAmpControlList;
        private ArrayList widebandNoiseBlankerList, noiseReductionList;
        private class KeyerElement
        {
            private Flex.IambicValues val;
            public string Display { get { return val.ToString(); } }
#if zero
            public string Display
            {
                get
                {
                    string str = "";
                    switch (val)
                    {
                        case Flex.IambicValues.off: str = "off"; break;
                        case Flex.IambicValues.iambicA: str = "iambA"; break;
                        case Flex.IambicValues.iambicB: str = "iambB"; break;
                    }
                    return str;
                }
            }
#endif
            public Flex.IambicValues RigItem { get { return val; } }
            public KeyerElement(Flex.IambicValues v)
            {
                val = v;
            }
        }
        private ArrayList keyerList, CWLList, CWReverseList;

        private class processorSettingElement
        {
            private Flex.ProcessorSettings val;
            public string Display { get { return val.ToString(); } }
            public Flex.ProcessorSettings RigItem { get { return val; } }
            public processorSettingElement(Flex.ProcessorSettings v)
            {
                val = v;
            }
        }
        private static processorSettingElement[] processorSettingItems =
        {
            new processorSettingElement(Flex.ProcessorSettings.NOR),
            new processorSettingElement(Flex.ProcessorSettings.DX),
            new processorSettingElement(Flex.ProcessorSettings.DXX),
        };
        private ArrayList processorOnList, processorSettingList, CompanderList;
        private ArrayList micBoostList, micBiasList, monitorList;

        private ArrayList toneModeList;
        private ArrayList toneFrequencyList;
        private ArrayList squelchList;

        // Offset is in KHZ, and freq is only up to 6 meters.
        private ArrayList emphasisList, FM1750List;
        private ArrayList binauralList, playList, recordList, daxTXList;

        private const int txAntennaDisplayOffset = 1;
        private ArrayList rxAntList;

        // Used to automatically enable/disable the speech proc
        private static string[] autoprocValues = { "off", "ssb" };
        private void autoprocFunc(Flex6300 rig)
        {
            TextOut.PerformGenericFunction(AutoprocControl, () =>
                {
                    if ((string)AutoprocControl.Items[AutoprocControl.SelectedIndex] == autoprocValues[1]) // ssb
                    {
                        string modeString = rig.Mode.ToString();
                        if ((modeString == "lsb") | (modeString == "usb"))
                            rig.ProcessorOn = AllRadios.OffOnValues.on;
                        else rig.ProcessorOn = AllRadios.OffOnValues.off;
                    }
                    ;
                });
        }

        private class mySortClass : IComparer
        {
            int IComparer.Compare(object x, object y)
            {
                string s1 = ((string)((Control)x).Tag).ToLower();
                string s2 = ((string)((Control)y).Tag).ToLower();
                int len = Math.Min(s1.Length, s2.Length);
                int v = string.Compare(s1.Substring(0, len), s2.Substring(0, len));
                if (v == 0) v = (s1.Length <= s2.Length) ? -1 : 1;
                return v;
            }
        }

        /// <summary>
        /// new FlexFilters object
        /// </summary>
        /// <param name="r">the Flex structure</param>
        /// <param name="openParms">open parameters, callouts isn't set up yet.</param>
        public Flex6300Filters(Flex6300 r, AllRadios.OpenParms openParms)
        {
            InitializeComponent();

            rig = r;

            // Get peroperator configuration items.
            OperatorsDirectory = openParms.ConfigDirectory + '\\' + openParms.OperatorName;
            getConfig();

            // Setup boxes.
            combos = new Collection<Combo>();
            comboBoxes = new Collection<ComboBox>();
            numberBoxes = new Collection<NumberBox>();
            infoBoxes = new Collection<InfoBox>();
            specials = new Collection<specialDel>();
            modeChangeSpecials = new Collection<modeChangeFuncDel>();

            // Filter low and high.
            FilterLowControl.LowValue = filterLowMinimum;
            FilterLowControl.HighValue = filterLowMaximum;
            FilterLowControl.Increment = filterLowIncrement;
            FilterLowControl.UpdateDisplayFunction =
                () => { return rig.FilterLow; };
            FilterLowControl.UpdateRigFunction =
                (int v) => { rig.FilterLow = v; };
            numberBoxes.Add(FilterLowControl);

            FilterHighControl.LowValue = filterHighMinimum;
            FilterHighControl.HighValue = filterHighMaximum;
            FilterHighControl.Increment = filterHighIncrement;
            FilterHighControl.UpdateDisplayFunction =
                () => { return rig.FilterHigh; };
            FilterHighControl.UpdateRigFunction =
                (int v) => { rig.FilterHigh = v; };
            numberBoxes.Add(FilterHighControl);

            // AGC
            AGCSpeedList = new ArrayList();
            foreach (AGCSpeedElement item in AGCSpeedItems)
            {
                AGCSpeedList.Add(item);
            }
            AGCSpeedControl.TheList = AGCSpeedList;
            AGCSpeedControl.UpdateDisplayFunction =
                () => { return rig.AGCSpeed; };
            AGCSpeedControl.UpdateRigFunction =
                (object v) => { rig.AGCSpeed = (AGCMode)v; };
            combos.Add(AGCSpeedControl);

            AGCThresholdControl.LowValue = Flex.AGCThresholdMin;
            AGCThresholdControl.HighValue = Flex.AGCThresholdMax;
            AGCThresholdControl.Increment = Flex.AGCThresholdIncrement;
            AGCThresholdControl.UpdateDisplayFunction =
                () => { return rig.AGCThreshold; };
            AGCThresholdControl.UpdateRigFunction =
                (int v) => { rig.AGCThreshold = v; };
            numberBoxes.Add(AGCThresholdControl);

            // Autonotch filter, ANF
            ANFList = new ArrayList();
            ANFList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            ANFList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            ANFControl.TheList = ANFList;
            ANFControl.UpdateDisplayFunction =
                () => { return rig.ANF; };
            ANFControl.UpdateRigFunction =
                (object v) => { rig.ANF = (AllRadios.OffOnValues)v; };
            combos.Add(ANFControl);

            // ANF level
            ANFLevelControl.LowValue = Flex.AutoNotchLevelMin;
            ANFLevelControl.HighValue = Flex.AutoNotchLevelMax;
            ANFLevelControl.Increment = Flex.AutoNotchLevelIncrement;
            ANFLevelControl.UpdateDisplayFunction =
                () => { return rig.AutoNotchLevel; };
            ANFLevelControl.UpdateRigFunction =
                (int v) => { rig.AutoNotchLevel = v; };
            numberBoxes.Add(ANFLevelControl);

            // Auto peaking filter, APF
            APFList = new ArrayList();
            APFList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            APFList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            APFControl.TheList = APFList;
            APFControl.UpdateDisplayFunction =
                () => { return rig.APF; };
            APFControl.UpdateRigFunction =
                (object v) => { rig.APF = (AllRadios.OffOnValues)v; };
            combos.Add(APFControl);

            // APF level
            APFLevelControl.LowValue = Flex.AutoPeakLevelMin;
            APFLevelControl.HighValue = Flex.AutoPeakLevelMax;
            APFLevelControl.Increment = Flex.AutoPeakLevelIncrement;
            APFLevelControl.UpdateDisplayFunction =
                () => { return rig.AutoPeakLevel; };
            APFLevelControl.UpdateRigFunction =
                (int v) => { rig.AutoPeakLevel = v; };
            numberBoxes.Add(APFLevelControl);

            // Noise blanker
            noiseBlankerList = new ArrayList();
            noiseBlankerList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            noiseBlankerList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            NoiseBlankerControl.TheList = noiseBlankerList;
            NoiseBlankerControl.UpdateDisplayFunction =
                () => { return rig.NoiseBlanker; };
            NoiseBlankerControl.UpdateRigFunction =
                (object v) => { rig.NoiseBlanker = (AllRadios.OffOnValues)v; };
            combos.Add(NoiseBlankerControl);

            NoiseBlankerLevelControl.LowValue = Flex.NoiseBlankerValueMin;
            NoiseBlankerLevelControl.HighValue = Flex.NoiseBlankerValueMax;
            NoiseBlankerLevelControl.Increment = Flex.NoiseBlankerValueIncrement;
            NoiseBlankerLevelControl.UpdateDisplayFunction =
                () => { return rig.NoiseBlankerLevel; };
            NoiseBlankerLevelControl.UpdateRigFunction =
                (int v) => { rig.NoiseBlankerLevel = v; };
            numberBoxes.Add(NoiseBlankerLevelControl);

            // pre-amp
            PreAmpControlList = new ArrayList();
            PreAmpControlList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            PreAmpControlList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            PreAmpControl.TheList = PreAmpControlList;
            PreAmpControl.UpdateDisplayFunction =
                () => { return rig.PreAmp; };
            PreAmpControl.UpdateRigFunction =
                (object v) => { rig.PreAmp = (AllRadios.OffOnValues)v; };
            combos.Add(PreAmpControl);

            // Wide band noise blanker
            widebandNoiseBlankerList = new ArrayList();
            widebandNoiseBlankerList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            widebandNoiseBlankerList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            WidebandNoiseBlankerControl.TheList = widebandNoiseBlankerList;
            WidebandNoiseBlankerControl.UpdateDisplayFunction =
                () => { return rig.WidebandNoiseBlanker; };
            WidebandNoiseBlankerControl.UpdateRigFunction =
                (object v) => { rig.WidebandNoiseBlanker = (AllRadios.OffOnValues)v; };
            combos.Add(WidebandNoiseBlankerControl);

            WidebandNoiseBlankerLevelControl.LowValue = Flex.NoiseBlankerValueMin;
            WidebandNoiseBlankerLevelControl.HighValue = Flex.NoiseBlankerValueMax;
            WidebandNoiseBlankerLevelControl.Increment = Flex.NoiseBlankerValueIncrement;
            WidebandNoiseBlankerLevelControl.UpdateDisplayFunction =
                () => { return rig.WidebandNoiseBlankerLevel; };
            WidebandNoiseBlankerLevelControl.UpdateRigFunction =
                (int v) => { rig.WidebandNoiseBlankerLevel = v; };
            numberBoxes.Add(WidebandNoiseBlankerLevelControl);

#if zero
            // RF Gain for the slice
            RFGainControl.Enabled = false;
            RFGainControl.LowValue = Flex.RFGainMin;
            RFGainControl.HighValue = Flex.RFGainMax;
            RFGainControl.Increment = Flex.RFGainIncrement;
            RFGainControl.UpdateDisplayFunction =
                () => { return rig.RFGain; };
            RFGainControl.UpdateRigFunction =
                (int v) => { rig.RFGain = v; };
            numberBoxes.Add(RFGainControl);

            // RF Gain for the pan adapter
            RFPanControl.LowValue = Flex.PanRFMin;
            RFPanControl.HighValue = Flex.PanRFMax;
            RFPanControl.Increment = Flex.PanRFIncrement;
            RFPanControl.UpdateDisplayFunction =
                () => { return rig.PanRF; };
            RFPanControl.UpdateRigFunction =
                (int v) => { rig.PanRF = v; };
            numberBoxes.Add(RFPanControl);
#endif

            // Noise reduction
            noiseReductionList = new ArrayList();
            noiseReductionList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            noiseReductionList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            NoiseReductionControl.TheList = noiseReductionList;
            NoiseReductionControl.UpdateDisplayFunction =
                () => { return rig.NoiseReduction; };
            NoiseReductionControl.UpdateRigFunction =
                (object v) => { rig.NoiseReduction = (AllRadios.OffOnValues)v; };
            combos.Add(NoiseReductionControl);

            NoiseReductionLevelControl.LowValue = Flex.NoiseReductionValueMin;
            NoiseReductionLevelControl.HighValue = Flex.NoiseReductionValueMax;
            NoiseReductionLevelControl.Increment = Flex.NoiseReductionValueIncrement;
            NoiseReductionLevelControl.UpdateDisplayFunction =
                () => { return rig.NoiseReductionLevel; };
            NoiseReductionLevelControl.UpdateRigFunction =
                (int v) => { rig.NoiseReductionLevel = v; };
            numberBoxes.Add(NoiseReductionLevelControl);

            // breakin
            BreakinDelayControl.LowValue = Flex.BreakinDelayMin;
            BreakinDelayControl.HighValue = Flex.BreakinDelayMax;
            BreakinDelayControl.Increment = Flex.BreakinDelayIncrement;
            BreakinDelayControl.UpdateDisplayFunction =
                () => { return rig.BreakinDelay; };
            BreakinDelayControl.UpdateRigFunction =
                (int v) => { rig.BreakinDelay = v; };
            numberBoxes.Add(BreakinDelayControl);

            // keyer
            keyerList = new ArrayList();
            keyerList.Add(new KeyerElement(Flex.IambicValues.off));
            keyerList.Add(new KeyerElement(Flex.IambicValues.iambicA));
            keyerList.Add(new KeyerElement(Flex.IambicValues.iambicB));
            KeyerControl.TheList = keyerList;
            KeyerControl.UpdateDisplayFunction =
                () => { return rig.Keyer; };
            KeyerControl.UpdateRigFunction =
                (object v) => { rig.Keyer = (Flex.IambicValues)v; };
            combos.Add(KeyerControl);

            // CW paddle reverse
            CWReverseList = new ArrayList();
            CWReverseList.Add(new TS590Filters.trueFalseElement(false));
            CWReverseList.Add(new TS590Filters.trueFalseElement(true));
            CWReverseControl.TheList = CWReverseList;
            CWReverseControl.UpdateDisplayFunction =
                () => { return rig.CWReverse; };
            CWReverseControl.UpdateRigFunction =
                (object v) => { rig.CWReverse = (bool)v; };
            combos.Add(CWReverseControl);

            // keyer speed
            KeyerSpeedControl.LowValue = Flex.KeyerSpeedMin;
            KeyerSpeedControl.HighValue = Flex.KeyerSpeedMax;
            KeyerSpeedControl.Increment = Flex.KeyerSpeedIncrement;
            KeyerSpeedControl.UpdateDisplayFunction =
                () => { return rig.KeyerSpeed; };
            KeyerSpeedControl.UpdateRigFunction =
                (int v) => { rig.KeyerSpeed = v; };
            numberBoxes.Add(KeyerSpeedControl);

            // Sidetone pitch
            SidetonePitchControl.LowValue = Flex.SidetonePitchMin;
            SidetonePitchControl.HighValue = Flex.SidetonePitchMax;
            SidetonePitchControl.Increment = Flex.SidetonePitchIncrement;
            SidetonePitchControl.UpdateDisplayFunction =
                () => { return rig.SidetonePitch; };
            SidetonePitchControl.UpdateRigFunction =
                (int v) => { rig.SidetonePitch= v; };
            numberBoxes.Add(SidetonePitchControl);

            // Sidetone volume
            SidetoneGainControl.LowValue = Flex.SidetoneGainMin;
            SidetoneGainControl.HighValue = Flex.SidetoneGainMax;
            SidetoneGainControl.Increment = Flex.SidetoneGainIncrement;
            SidetoneGainControl.UpdateDisplayFunction =
                () => { return rig.SidetoneGain; };
            SidetoneGainControl.UpdateRigFunction =
                (int v) => { rig.SidetoneGain = v; };
            numberBoxes.Add(SidetoneGainControl);

            // CWL
            CWLList = new ArrayList();
            CWLList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            CWLList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            CWLControl.TheList = CWLList;
            CWLControl.UpdateDisplayFunction =
                () => { return rig.CWL; };
            CWLControl.UpdateRigFunction =
                (object v) => { rig.CWL = (AllRadios.OffOnValues)v; };
            combos.Add(CWLControl);

            // Monitor pan
            MonitorPanControl.LowValue = Flex.MonitorPanMin;
            MonitorPanControl.HighValue = Flex.MonitorPanMax;
            MonitorPanControl.Increment = Flex.MonitorPanIncrement;
            MonitorPanControl.UpdateDisplayFunction =
                () => { return rig.MonitorPan; };
            MonitorPanControl.UpdateRigFunction =
                (int v) => { rig.MonitorPan= v; };
            numberBoxes.Add(MonitorPanControl);

            // Vox delay
            VoxDelayControl.LowValue = Flex.VoxDelayMin;
            VoxDelayControl.HighValue = Flex.VoxDelayMax;
            VoxDelayControl.Increment = Flex.VoxDelayIncrement;
            VoxDelayControl.UpdateDisplayFunction =
                () => { return rig.VoxDelay; };
            VoxDelayControl.UpdateRigFunction =
                (int v) => { rig.VoxDelay = v; };
            numberBoxes.Add(VoxDelayControl);

            // Vox gain
            VoxGainControl.LowValue = Flex.VoxGainMin;
            VoxGainControl.HighValue = Flex.VoxGainMax;
            VoxGainControl.Increment = Flex.VoxGainIncrement;
            VoxGainControl.UpdateDisplayFunction =
                () => { return rig.VoxGain; };
            VoxGainControl.UpdateRigFunction =
                (int v) => { rig.VoxGain = v; };
            numberBoxes.Add(VoxGainControl);

            MicGainControl.LowValue = Flex.MicGainMin;
            MicGainControl.HighValue = Flex.MicGainMax;
            MicGainControl.Increment = Flex.MicGainIncrement;
            MicGainControl.UpdateDisplayFunction =
                () => { return rig.MicGain; };
            MicGainControl.UpdateRigFunction =
                (int v) => { rig.MicGain = v; };
            numberBoxes.Add(MicGainControl);

            // speech processor
            processorOnList = new ArrayList();
            processorOnList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            processorOnList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            ProcessorOnControl.TheList = processorOnList;
            ProcessorOnControl.UpdateDisplayFunction =
                () => { return rig.ProcessorOn; };
            ProcessorOnControl.UpdateRigFunction =
                (object v) => { rig.ProcessorOn = (AllRadios.OffOnValues)v; };
            combos.Add(ProcessorOnControl);

            processorSettingList = new ArrayList();
            foreach (processorSettingElement item in processorSettingItems)
            {
                processorSettingList.Add(item);
            }
            ProcessorSettingControl.TheList = processorSettingList;
            ProcessorSettingControl.UpdateDisplayFunction =
                () => { return rig.ProcessorSetting; };
            ProcessorSettingControl.UpdateRigFunction =
                (object v) => { rig.ProcessorSetting = (Flex.ProcessorSettings)v; };
            combos.Add(ProcessorSettingControl);

            // Compander
            CompanderList = new ArrayList();
            CompanderList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            CompanderList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            CompanderControl.TheList = CompanderList;
            CompanderControl.UpdateDisplayFunction =
                () => { return rig.Compander; };
            CompanderControl.UpdateRigFunction =
                (object v) => { rig.Compander = (AllRadios.OffOnValues)v; };
            combos.Add(CompanderControl);

            CompanderLevelControl.LowValue = Flex.CompanderLevelMin;
            CompanderLevelControl.HighValue = Flex.CompanderLevelMax;
            CompanderLevelControl.Increment = Flex.CompanderLevelIncrement;
            CompanderLevelControl.UpdateDisplayFunction =
                () => { return rig.CompanderLevel; };
            CompanderLevelControl.UpdateRigFunction =
                (int v) => { rig.CompanderLevel = v; };
            numberBoxes.Add(CompanderLevelControl);

            // TX Filters
            TXFilterLowControl.LowValue = rig.TXFilterLowMin;
            TXFilterLowControl.HighValue = rig.TXFilterLowMax;
            TXFilterLowControl.Increment = rig.TXFilterLowIncrement;
            TXFilterLowControl.UpdateDisplayFunction =
                () => { return rig.TXFilterLow; };
            TXFilterLowControl.UpdateRigFunction =
                (int v) => { rig.TXFilterLow = v; };
            numberBoxes.Add(TXFilterLowControl);

            TXFilterHighControl.LowValue = rig.TXFilterHighMin;
            TXFilterHighControl.HighValue = rig.TXFilterHighMax;
            TXFilterHighControl.Increment = rig.TXFilterHighIncrement;
            TXFilterHighControl.UpdateDisplayFunction =
                () => { return rig.TXFilterHigh; };
            TXFilterHighControl.UpdateRigFunction =
                (int v) => { rig.TXFilterHigh = v; };
            numberBoxes.Add(TXFilterHighControl);

            // mic boost
            micBoostList = new ArrayList();
            micBoostList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            micBoostList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            MicBoostControl.TheList = micBoostList;
            MicBoostControl.UpdateDisplayFunction =
                () => { return rig.MicBoost; };
            MicBoostControl.UpdateRigFunction =
                (object v) => { rig.MicBoost = (AllRadios.OffOnValues)v; };
            combos.Add(MicBoostControl);

            // mic bias
            micBiasList = new ArrayList();
            micBiasList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            micBiasList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            MicBiasControl.TheList = micBiasList;
            MicBiasControl.UpdateDisplayFunction =
                () => { return rig.MicBias; };
            MicBiasControl.UpdateRigFunction =
                (object v) => { rig.MicBias = (AllRadios.OffOnValues)v; };
            combos.Add(MicBiasControl);

            // monitor
            monitorList = new ArrayList();
            monitorList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            monitorList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            MonitorControl.TheList = monitorList;
            MonitorControl.UpdateDisplayFunction =
                () => { return rig.Monitor; };
            MonitorControl.UpdateRigFunction =
                (object v) => { rig.Monitor = (AllRadios.OffOnValues)v; };
            combos.Add(MonitorControl);

            SBMonitorLevelControl.LowValue = Flex.SBMonitorLevelMin;
            SBMonitorLevelControl.HighValue = Flex.SBMonitorLevelMax;
            SBMonitorLevelControl.Increment = Flex.SBMonitorLevelIncrement;
            SBMonitorLevelControl.UpdateDisplayFunction =
                () => { return rig.SBMonitorLevel; };
            SBMonitorLevelControl.UpdateRigFunction =
                (int v) => { rig.SBMonitorLevel = v; };
            numberBoxes.Add(SBMonitorLevelControl);

            SBMonitorPanControl.LowValue = Flex.SBMonitorPanMin;
            SBMonitorPanControl.HighValue = Flex.SBMonitorPanMax;
            SBMonitorPanControl.Increment = Flex.SBMonitorPanIncrement;
            SBMonitorPanControl.UpdateDisplayFunction =
                () => { return rig.SBMonitorPan; };
            SBMonitorPanControl.UpdateRigFunction =
                (int v) => { rig.SBMonitorPan = v; };
            numberBoxes.Add(SBMonitorPanControl);

            // TX antenna, 0 or 1, displayed as 1 or 2.
            AntControl.LowValue = 1;
            AntControl.HighValue = 2;
            AntControl.Increment = 1;
            AntControl.UpdateDisplayFunction =
                () => { return rig.TXAntenna + txAntennaDisplayOffset; };
            AntControl.UpdateRigFunction =
                (int v) => { rig.TXAntenna = v - txAntennaDisplayOffset; };
            numberBoxes.Add(AntControl);

            // RX antenna, true/false.
            rxAntList = new ArrayList();
            rxAntList.Add(new TS590Filters.trueFalseElement(false));
            rxAntList.Add(new TS590Filters.trueFalseElement(true));
            RXAntControl.TheList = rxAntList;
            RXAntControl.UpdateDisplayFunction =
                () => { return rig.RXAntenna; };
            RXAntControl.UpdateRigFunction =
                (object v) => { rig.RXAntenna = (bool)v; };
            combos.Add(RXAntControl);

            // Transmit power
            XmitPowerControl.LowValue = Flex.XmitPowerMin;
            XmitPowerControl.HighValue = Flex.XmitPowerMax;
            XmitPowerControl.Increment = Flex.XmitPowerIncrement;
            XmitPowerControl.UpdateDisplayFunction =
                () => { return rig.XmitPower; };
            XmitPowerControl.UpdateRigFunction =
                (int v) => { rig.XmitPower = v; };
            numberBoxes.Add(XmitPowerControl);

            // Tune power
            TunePowerControl.LowValue = Flex.TunePowerMin;
            TunePowerControl.HighValue = Flex.TunePowerMax;
            TunePowerControl.Increment = Flex.TunePowerIncrement;
            TunePowerControl.UpdateDisplayFunction =
                () => { return rig.TunePower; };
            TunePowerControl.UpdateRigFunction =
                (int v) => { rig.TunePower = v; };
            numberBoxes.Add(TunePowerControl);

            // Info text boxes
            // MicPeakBox must initially be disabled, so the enabledChanged routine runs.
            infoBoxes.Add(MicPeakBox);

            SWRControl.UpdateDisplayFunction =
                () => { return rig.SWR.ToString("F1"); };
            infoBoxes.Add(SWRControl);

            // FM tone or CTCSS mode.
            toneModeList = new ArrayList();
            foreach (AllRadios.ToneCTCSSValue t in rig.FMToneModes)
            {
                toneModeList.Add(new TS2000Filters.toneCTCSSElement(t));
            }
            ToneModeControl.TheList = toneModeList;
            ToneModeControl.UpdateDisplayFunction =
                () => { return rig.ToneCTCSS; };
            ToneModeControl.UpdateRigFunction =
                (object v) => { rig.ToneCTCSS = (AllRadios.ToneCTCSSValue)v; };
            combos.Add(ToneModeControl);

            // FM tone frequency
            toneFrequencyList = new ArrayList();
            foreach (float f in rig.ToneFrequencyTable)
            {
                toneFrequencyList.Add(new TS2000Filters.toneCTCSSFreqElement(f));
            }
            ToneFrequencyControl.TheList = toneFrequencyList;
            ToneFrequencyControl.UpdateDisplayFunction =
                () => { return rig.ToneFrequency; };
            ToneFrequencyControl.UpdateRigFunction =
                (object v) => { rig.ToneFrequency = (float)v; };
            combos.Add(ToneFrequencyControl);

            // squelch
            squelchList = new ArrayList();
            squelchList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            squelchList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            SquelchControl.TheList = squelchList;
            SquelchControl.UpdateDisplayFunction =
                () => { return rig.Squelch; };
            SquelchControl.UpdateRigFunction =
                (object v) => { rig.Squelch = (AllRadios.OffOnValues)v; };
            combos.Add(SquelchControl);

            // squelch level
            SquelchLevelControl.LowValue = Flex.SquelchLevelMin;
            SquelchLevelControl.HighValue = Flex.SquelchLevelMax;
            SquelchLevelControl.Increment = Flex.SquelchLevelIncrement;
            SquelchLevelControl.UpdateDisplayFunction =
                () => { return rig.SquelchLevel; };
            SquelchLevelControl.UpdateRigFunction =
                (int v) => { rig.SquelchLevel = v; };
            numberBoxes.Add(SquelchLevelControl);

            // offset
            OffsetControl.LowValue = Flex.offsetMin;
            OffsetControl.HighValue = Flex.offsetMax;
            OffsetControl.Increment = Flex.offsetIncrement;
            OffsetControl.UpdateDisplayFunction =
                () => { return rig.OffsetFrequency; };
            OffsetControl.UpdateRigFunction =
                (int v) => { rig.OffsetFrequency = v; };
            numberBoxes.Add(OffsetControl);

            // emphasis
            emphasisList = new ArrayList();
            emphasisList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            emphasisList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            EmphasisControl.TheList = emphasisList;
            EmphasisControl.UpdateDisplayFunction =
                () => { return rig.FMEmphasis; };
            EmphasisControl.UpdateRigFunction =
                (object v) => { rig.FMEmphasis = (AllRadios.OffOnValues)v; };
            combos.Add(EmphasisControl);

            // 1750 offset
            FM1750List = new ArrayList();
            FM1750List.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            FM1750List.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            FM1750Control.TheList = FM1750List;
            FM1750Control.UpdateDisplayFunction =
                () => { return rig.FM1750; };
            FM1750Control.UpdateRigFunction =
                (object v) => { rig.FM1750 = (AllRadios.OffOnValues)v; };
            combos.Add(FM1750Control);

            // AM carrier level
            AMCarrierLevelControl.LowValue = Flex.AMCarrierLevelMin;
            AMCarrierLevelControl.HighValue = Flex.AMCarrierLevelMax;
            AMCarrierLevelControl.Increment= Flex.AMCarrierLevelIncrement;
            AMCarrierLevelControl.UpdateDisplayFunction =
                () => { return rig.AMCarrierLevel; };
            AMCarrierLevelControl.UpdateRigFunction =
                (int v) => { rig.AMCarrierLevel = v; };
            numberBoxes.Add(AMCarrierLevelControl);

            // binaural rx
            binauralList = new ArrayList();
            binauralList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            binauralList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            BinauralControl.TheList = binauralList;
            BinauralControl.UpdateDisplayFunction =
                () => { return rig.Binaural; };
            BinauralControl.UpdateRigFunction =
                (object v) => { rig.Binaural = (AllRadios.OffOnValues)v; };
            combos.Add(BinauralControl);

            // 1750 offset
            playList  = new ArrayList();
            playList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            playList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            PlayControl.TheList = playList;
            PlayControl.UpdateDisplayFunction =
                () => { return rig.Play; };
            PlayControl.UpdateRigFunction =
                (object v) => { rig.Play = (AllRadios.OffOnValues)v; };
            combos.Add(PlayControl);
            // Only enabled if play allowed.
            specials.Add(() =>
            {
                bool sw = rig.CanPlay;
                if (PlayControl.Enabled != sw) PlayControl.Enabled = sw;
            });

            // Record control
            recordList  = new ArrayList();
            recordList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            recordList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            RecordControl.TheList = recordList;
            RecordControl.UpdateDisplayFunction =
                () => { return rig.Record; };
            RecordControl.UpdateRigFunction =
                (object v) => { rig.Record = (AllRadios.OffOnValues)v; };
            combos.Add(RecordControl);

            // Setup panning
            // DAX transmit control
            daxTXList = new ArrayList();
            daxTXList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.off));
            daxTXList.Add(new TS590Filters.offOnElement(AllRadios.OffOnValues.on));
            DAXTXControl.TheList = daxTXList;
            DAXTXControl.UpdateDisplayFunction =
                () => { return rig.DAXOn; };
            DAXTXControl.UpdateRigFunction =
                (object v) => { rig.DAXOn = (AllRadios.OffOnValues)v; };
            combos.Add(DAXTXControl);

            // auto mode change
            foreach (string s in autoprocValues)
            {
                AutoprocControl.Items.Add(s);
            }
            // Set configured value later.
            comboBoxes.Add(AutoprocControl);
            modeChangeSpecials.Add(autoprocFunc);

            panControlSetup();

            // Other controls
            int myControlsCount = combos.Count + comboBoxes.Count + numberBoxes.Count + infoBoxes.Count + panControls.Count;
            int loopCount = myControlsCount; // for the below loop.
            // loopCount is also the start of the buttons.

            // buttons
            buttonControls = new Collection<Button>();
            buttonControls.Add(TNFButton);
            buttonControls.Add(TNFEnableButton);
            buttonControls.Add(ExportButton);
            buttonControls.Add(ImportButton);
            buttonControls.Add(RXEqButton);
            buttonControls.Add(TXEqButton);
            buttonControls.Add(InfoButton);
            myControlsCount += buttonControls.Count;

            Control[] myControls = new Control[myControlsCount];

            // Sort ScreenFields control list by text.
            // Also add the BoxKeyDown interrupt.
            for (int i = 0; i < loopCount; i++)
            {
                if (i < combos.Count)
                {
                    RadioBoxes.Combo c = combos[i];
                    c.BoxKeydown += BoxKeydownDefault;
                    myControls[i] = (Control)c;
                    if ((string)myControls[i].Tag == "") myControls[i].Tag = c.Header;
                }
                else if (i < combos.Count + comboBoxes.Count)
                {
                    ComboBox c = comboBoxes[i - combos.Count];
                    c.KeyDown += BoxKeydownDefault;
                    myControls[i] = (Control)c;
                    // The tag must be set.
                }
                else if (i < combos.Count + comboBoxes.Count + numberBoxes.Count)
                {
                    RadioBoxes.NumberBox n = numberBoxes[i - combos.Count - comboBoxes.Count];
                    n.BoxKeydown += BoxKeydownDefault;
                    myControls[i] = (Control)n;
                    if ((string)myControls[i].Tag == "") myControls[i].Tag = n.Header;
                }
                else if (i < combos.Count + comboBoxes.Count + numberBoxes.Count + infoBoxes.Count)
                {
                    RadioBoxes.InfoBox ib = infoBoxes[i - combos.Count - comboBoxes.Count - numberBoxes.Count];
                    ib.BoxKeydown += BoxKeydownDefault;
                    myControls[i] = (Control)ib;
                    if ((string)myControls[i].Tag == "") myControls[i].Tag = ib.Header;
                }
                else
                {
                    Control pc = panControls[i - combos.Count - comboBoxes.Count - numberBoxes.Count - infoBoxes.Count];
                    pc.KeyDown += BoxKeydownDefault;
                    myControls[i] = pc;
                    // tag setup in panControlSetup().
                }
            }

            // TNF and other Buttons
            for (int i = loopCount; i < myControlsCount; i++)
            {
                Button b = buttonControls[i - loopCount];
                b.Tag = b.Text;
                b.KeyDown += BoxKeydownDefault;
                myControls[i] = b;
            }
            TNFEnableButton.Tag = "TNFO"; // special case - Gets it in the sort order

            IComparer mySort = new mySortClass();
            Array.Sort(myControls, mySort);

            // setup the mode change stuff.
            modeChange = new modeChangeClass(this, buildModeChange(), modeChangeSpecials);

            openParms.PanField = PanBox;
            Form memdisp = new FlexMemories(rig);
            rig.RigFields = new AllRadios.RigInfo(this, updateBoxes, memdisp, null, myControls);

            // Set routine to get SWR text.
            openParms.GetSWRText = SWRText;
        }

        private void Filters_Load(object sender, EventArgs e)
        {
            Tracing.TraceLine("Flex6300Filters_load", TraceLevel.Info);
            setupNoiseReductionMenu();
            setupFiltersMenuStrip();
        }

        /// <summary>
        /// Get configuration info.
        /// </summary>
        /// <remarks>
        /// OperatorsDirectory must be set.
        /// OperatorsDirectory is null on failure.
        /// </remarks>
        private void getConfig()
        {
            Stream configFile = null;
            try
            {
                if (!Directory.Exists(OperatorsDirectory))
                {
                    Directory.CreateDirectory(OperatorsDirectory);
                }
                if (!File.Exists(ConfigFilename))
                {
                    // Use defaults
                    OpsConfigInfo = new ConfigInfo();
                }
                else
                {
                    configFile = File.Open(ConfigFilename, FileMode.Open);
                    XmlSerializer xs = new XmlSerializer(typeof(ConfigInfo));
                    OpsConfigInfo = (ConfigInfo)xs.Deserialize(configFile);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK);
                OperatorsDirectory = null;
            }
            finally
            {
                if (configFile != null) configFile.Dispose();
            }
        }

        private void writeConfig()
        {
            if (ConfigFilename == null)
            {
                Tracing.TraceLine("configWrite no file", TraceLevel.Error);
                return;
            }
            Tracing.TraceLine("configWrite:" + ConfigFilename, TraceLevel.Info);
            Stream configFile = null;
            try
            {
                configFile = File.Open(ConfigFilename, FileMode.Create);
                XmlSerializer xs = new XmlSerializer(typeof(ConfigInfo));
                xs.Serialize(configFile, OpsConfigInfo);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("configWrite error:" + ex.Message, TraceLevel.Error);
            }
            finally
            {
                if (configFile != null) configFile.Dispose();
            }
        }

        private void updateBoxes()
        {
            Tracing.TraceLine("updateBoxes", TraceLevel.Verbose);
            if (rig.Mode == null)
            {
                Tracing.TraceLine("updateBoxes:no mode", TraceLevel.Verbose);
                return;
            }

            try
            {
                // enable/disable boxes for this mode.
                Tracing.TraceLine("UpdateBoxes:enableDisable", TraceLevel.Verbose);
                modeChange.enableDisable(rig.Mode);

#if subdependencies
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
#endif

                Tracing.TraceLine("UpdateBoxes:combos",TraceLevel.Verbose);
                foreach (Combo c in combos)
                {
                    if (c.Enabled)
                    {
                        c.UpdateDisplay();
                    }
                }

                Tracing.TraceLine("UpdateBoxes:numberBoxes", TraceLevel.Verbose);
                foreach (NumberBox c in numberBoxes)
                {
                    if (c.Enabled)
                    {
                        c.UpdateDisplay();
                    }
                }

                Tracing.TraceLine("UpdateBoxes:infoBoxes", TraceLevel.Verbose);
                foreach (InfoBox c in infoBoxes)
                {
                    if (c.Enabled)
                    {
                        c.UpdateDisplay();
                    }
                }

                Tracing.TraceLine("UpdateBoxes:specials", TraceLevel.Verbose);
                foreach (specialDel rtn in specials)
                {
                    if (InvokeRequired) Invoke(rtn);
                    else rtn();
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("updateBoxes exception:" + ex.Message + ex.StackTrace, TraceLevel.Error);
            }
        }

        #region ModeChange
        internal class modeChangeClass
        {
            // A mode's filter controls are enabled when that mode is active.
            public class controlsClass
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
            private Dictionary<string, controlsClass> modeControls;
            private Collection<modeChangeFuncDel> modeChangeSpecials;
            private Control parent;
            internal modeChangeClass(Control p,
                Dictionary<string, controlsClass> controls,
                Collection<modeChangeFuncDel> specials)
            {
                parent = p;
                modeControls = controls;
                modeChangeSpecials = specials;
            }

            private delegate void rtn(Control c);
            private static rtn enab = enable;
            private static rtn disab = disable;
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

            private AllRadios.ModeValue oldMode = null;
            public void enableDisable(Flex6300.ModeValue mode)
            {
                // Just quit if the mode hasn't changed.
                if ((oldMode != null) && (mode == oldMode)) return;
                oldMode = mode;
                string modeString = mode.ToString();
                // enables holds the controls to be enabled.  It may be null.
                controlsClass enables = (modeControls.Keys.Contains(modeString)) ? modeControls[modeString] : null;
                parent.SuspendLayout();
                if (enables != null)
                {
                    foreach (Control c in enables.controls)
                    {
                        // enable
                        if (parent.InvokeRequired)
                        {
                            parent.Invoke(enab, new object[] { c });
                        }
                        else
                        {
                            enab(c);
                        }
                    }
                }
                // Now disable the others.
                foreach (controlsClass controlArray in modeControls.Values)
                {
                    if ((controlArray == null) || (controlArray == enables)) continue;
                    foreach (Control c in controlArray.controls)
                    {
                        if ((enables != null) && (Array.IndexOf(enables.controls, c) >= 0)) continue; // already enabled.
                        // disable
                        if (parent.InvokeRequired)
                        {
                            parent.Invoke(disab, new object[] { c });
                        }
                        else
                        {
                            disab(c);
                        }
                    }
                }

                if (modeChangeSpecials != null)
                {
                    foreach (modeChangeFuncDel func in modeChangeSpecials)
                    {
                        func(Flex6300Filters.rig);
                    }
                }
                parent.ResumeLayout();
            }
        }
        private modeChangeClass modeChange;

        #region enable controls
        private Dictionary<string, modeChangeClass.controlsClass> buildModeChange()
        {
            Dictionary<string, modeChangeClass.controlsClass> rv = new Dictionary<string, modeChangeClass.controlsClass>();

            // setup the mode to combobox mapping for mode-dependent controls.
            // Controls for all modes need not appear.
            rv.Add("lsb", new modeChangeClass.controlsClass(
                new Control[] {
                        ANFControl, ANFLevelControl, // not for CW
                        MicGainControl, MicPeakBox,
                        ProcessorOnControl, ProcessorSettingControl,
                        CompanderControl, CompanderLevelControl,
                        VoxDelayControl, VoxGainControl,
                        TXFilterHighControl,TXFilterLowControl,
                        MicBiasControl, MicBoostControl,
                        MonitorControl, SBMonitorLevelControl,SBMonitorPanControl,
                        TNFButton, TNFEnableButton, // not for FM
                        RXEqButton, TXEqButton, // not for CW and digital
                        DAXTXControl,
                    }));
            rv.Add("usb", new modeChangeClass.controlsClass(
                new Control[] {
                        ANFControl, ANFLevelControl,
                        MicGainControl, MicPeakBox,
                        ProcessorOnControl, ProcessorSettingControl,
                        CompanderControl, CompanderLevelControl,
                        VoxDelayControl, VoxGainControl,
                        TXFilterHighControl,TXFilterLowControl,
                        MicBiasControl, MicBoostControl,
                        MonitorControl, SBMonitorLevelControl,SBMonitorPanControl,
                        TNFButton, TNFEnableButton,
                        RXEqButton, TXEqButton,
                        DAXTXControl,
                    }));
            rv.Add("cw", new modeChangeClass.controlsClass(
                new Control[] {
                        APFControl, APFLevelControl, // CW only
                        BreakinDelayControl, KeyerControl, CWReverseControl,
                        KeyerSpeedControl, SidetonePitchControl,
                        SidetoneGainControl, MonitorPanControl,
                        CWLControl,
                        TNFButton, TNFEnableButton,
                    }));
            rv.Add("fm", new modeChangeClass.controlsClass(
                new Control[] {
                        ANFControl, ANFLevelControl,
                        MicGainControl, MicPeakBox,
                        ProcessorOnControl, ProcessorSettingControl,
                        CompanderControl, CompanderLevelControl,
                        VoxDelayControl, VoxGainControl,
                        TXFilterHighControl,TXFilterLowControl,
                        MicBiasControl, MicBoostControl,
                        MonitorControl, SBMonitorLevelControl,SBMonitorPanControl,
                        ToneModeControl, ToneFrequencyControl,
                        SquelchControl, SquelchLevelControl,
                        OffsetControl, EmphasisControl,
                        FM1750Control,
                        RXEqButton, TXEqButton,
                        DAXTXControl,
                    }));
            rv.Add("am", new modeChangeClass.controlsClass(
                new Control[] {
                        ANFControl, ANFLevelControl,
                        MicGainControl, MicPeakBox,
                        ProcessorOnControl, ProcessorSettingControl,
                        CompanderControl, CompanderLevelControl,
                        VoxDelayControl, VoxGainControl,
                        TXFilterHighControl,TXFilterLowControl,
                        MicBiasControl, MicBoostControl,
                        MonitorControl, SBMonitorLevelControl,SBMonitorPanControl,
                        TNFButton, TNFEnableButton,
                        RXEqButton, TXEqButton,
                        AMCarrierLevelControl,
                    }));
            rv.Add("digl", new modeChangeClass.controlsClass(
                new Control[] {
                        ANFControl, ANFLevelControl,
                        TNFButton, TNFEnableButton,
                        ProcessorOnControl, MicGainControl,
                        MicBiasControl, MicBoostControl,
                        VoxDelayControl, VoxGainControl, MicPeakBox,
                        MonitorControl, SBMonitorLevelControl,SBMonitorPanControl,
                        //ProcessorInLevelControl, ProcessorOutLevelControl,
                        DAXTXControl,
                    }));
            rv.Add("digu", new modeChangeClass.controlsClass(
                new Control[] {
                        ANFControl, ANFLevelControl,
                        ProcessorOnControl, MicGainControl,
                        MicBiasControl, MicBoostControl,
                        VoxDelayControl, VoxGainControl,
                        TNFButton, TNFEnableButton, MicPeakBox,
                        MonitorControl, SBMonitorLevelControl,SBMonitorPanControl,
                        //ProcessorInLevelControl, ProcessorOutLevelControl,
                        DAXTXControl,
                    }));
            rv.Add("nfm", new modeChangeClass.controlsClass(
                new Control[] {
                        ANFControl, ANFLevelControl,
                        MicGainControl, MicPeakBox,
                        ProcessorOnControl, ProcessorSettingControl,
                        CompanderControl, CompanderLevelControl,
                        VoxDelayControl, VoxGainControl,
                        TXFilterHighControl,TXFilterLowControl,
                        MicBiasControl, MicBoostControl,
                        MonitorControl, SBMonitorLevelControl,SBMonitorPanControl,
                        ToneModeControl, ToneFrequencyControl,
                        SquelchControl, SquelchLevelControl,
                        OffsetControl, EmphasisControl,
                        FM1750Control,
                        RXEqButton, TXEqButton,
                        DAXTXControl,
                    }));
            rv.Add("dfm", new modeChangeClass.controlsClass(
                new Control[] {
                        ANFControl, ANFLevelControl,
                        MicGainControl, MicPeakBox,
                        //ProcessorControl,
                        VoxDelayControl, VoxGainControl,
                        //ProcessorInLevelControl, ProcessorOutLevelControl,
                        TXFilterHighControl,TXFilterLowControl,
                        MicBiasControl, MicBoostControl,
                        MonitorControl, SBMonitorLevelControl,SBMonitorPanControl,
                        ToneModeControl, ToneFrequencyControl,
                        SquelchControl, SquelchLevelControl,
                        OffsetControl, EmphasisControl,
                        FM1750Control,
                        RXEqButton, TXEqButton,
                        DAXTXControl,
                    }));
            rv.Add("sam", new modeChangeClass.controlsClass(
                new Control[] {
                        ANFControl, ANFLevelControl,
                        MicGainControl, MicPeakBox,
                        ProcessorOnControl, ProcessorSettingControl,
                        CompanderControl, CompanderLevelControl,
                        VoxDelayControl, VoxGainControl,
                        TXFilterHighControl,TXFilterLowControl,
                        MicBiasControl, MicBoostControl,
                        MonitorControl, SBMonitorLevelControl,SBMonitorPanControl,
                        TNFButton, TNFEnableButton,
                        RXEqButton, TXEqButton,
                        AMCarrierLevelControl,
                    }));

            return rv;
        }
        #endregion
        #endregion

        private void BoxKeydownDefault(object sender, KeyEventArgs e)
        {
            OnKeyDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            // Alt+F: Noise Reduction quick selector (menu-like)
            if (e.Alt && (e.KeyCode == Keys.F))
            {
                try
                {
                    string mode = rig.Mode.ToLowerInvariant();
                    // Gate by mode: skip CW and FM variants
                    if (mode.StartsWith("cw") || mode.Contains("fm")) return;

                    // Cycle through NR algorithms: RNN -> Spectral -> Legacy -> Off
                    bool rnn = rig.NeuralNoiseReduction;
                    bool spec = rig.SpectralNoiseReduction;
                    bool leg = rig.NoiseReductionLegacy;

                    if (!rnn && !spec && !leg)
                    {
                        rig.NeuralNoiseReduction = true;
                        rig.SpectralNoiseReduction = false;
                        rig.NoiseReductionLegacy = false;
                        JJTrace.Tracing.TraceLine("Alt+F: RNN enabled", JJTrace.TraceLevel.Info);
                    }
                    else if (rnn)
                    {
                        rig.NeuralNoiseReduction = false;
                        rig.SpectralNoiseReduction = true;
                        rig.NoiseReductionLegacy = false;
                        JJTrace.Tracing.TraceLine("Alt+F: Spectral NR enabled", JJTrace.TraceLevel.Info);
                    }
                    else if (spec)
                    {
                        rig.NeuralNoiseReduction = false;
                        rig.SpectralNoiseReduction = false;
                        rig.NoiseReductionLegacy = true;
                        JJTrace.Tracing.TraceLine("Alt+F: Legacy NR enabled", JJTrace.TraceLevel.Info);
                    }
                    else
                    {
                        rig.NeuralNoiseReduction = false;
                        rig.SpectralNoiseReduction = false;
                        rig.NoiseReductionLegacy = false;
                        JJTrace.Tracing.TraceLine("Alt+F: Noise Reduction off", JJTrace.TraceLevel.Info);
                    }

                    // Refresh UI bindings
                    updateBoxes();
                    e.Handled = true;
                }
                catch (Exception ex)
                {
                    JJTrace.Tracing.TraceLine("Alt+F NR toggle error: " + ex.Message, JJTrace.TraceLevel.Error);
                }
            }
        }

        private FloatPeakType micPeak;
        private void MicPeakBox_EnabledChanged(object sender, EventArgs e)
        {
            if (MicPeakBox.Enabled)
            {
                // Use a peak period of 1 second.
                micPeak = new FloatPeakType(() => { return rig._MicPeakData; }, 1000, -1000);
                //MicPeakBox.UpdateDisplayFunction = () => { return micPeak.Read().ToString("F1"); };
                MicPeakBox.UpdateDisplayFunction = updateMicPeak;
            }
            else
            {
                MicPeakBox.UpdateDisplayFunction = null;
                if (micPeak != null) micPeak.Finished();
            }
        }
        private string updateMicPeak()
        {
            return micPeak.Read().ToString("F1");
        }

        private void Flex6300Filters_ControlRemoved(object sender, ControlEventArgs e)
        {
            Cleanup();
        }
        public void Cleanup()
        {
            flexTNF.Dispose();
        }

        private void TNFButton_Click(object sender, EventArgs e)
        {
            flexTNF.ShowDialog();
        }

        // Noise Reduction context menu (checkable items, multiple selection)
        private ContextMenuStrip nrMenu;
        private ToolStripMenuItem nrRoot;
        private ToolStripMenuItem rnnItem;
        private ToolStripMenuItem spectralItem;
        private ToolStripMenuItem legacyNRItem;
        private ToolStripMenuItem anfRoot;
        private ToolStripMenuItem anfFftItem;
        private ToolStripMenuItem anfLegacyItem;
        private static void updateMenuAccessibility(ToolStripMenuItem item)
        {
            if (item == null) return;
            string label = item.Text ?? string.Empty;
            if (label.IndexOf('&') >= 0) label = label.Replace("&", "");
            string state = (!item.Enabled) ? "unavailable" : (item.Checked ? "checked" : "not checked");
            item.AccessibleRole = AccessibleRole.CheckButton;
            item.AccessibleName = label + " " + state;
            item.AccessibleDescription = state;
        }
        private void setupNoiseReductionMenu()
        {
            nrMenu = new ContextMenuStrip();
            nrRoot = new ToolStripMenuItem("Noise Reduction");
            rnnItem = new ToolStripMenuItem("Neural (RNN)") { CheckOnClick = true };
            spectralItem = new ToolStripMenuItem("Spectral (NRF/NRS)") { CheckOnClick = true };
            legacyNRItem = new ToolStripMenuItem("Legacy (NRL)") { CheckOnClick = true };
            nrRoot.DropDownItems.Add(rnnItem);
            nrRoot.DropDownItems.Add(spectralItem);
            nrRoot.DropDownItems.Add(legacyNRItem);

            anfRoot = new ToolStripMenuItem("Auto Notch");
            anfFftItem = new ToolStripMenuItem("FFT (ANFT)") { CheckOnClick = true };
            anfLegacyItem = new ToolStripMenuItem("Legacy (ANFL)") { CheckOnClick = true };
            anfRoot.DropDownItems.Add(anfFftItem);
            anfRoot.DropDownItems.Add(anfLegacyItem);

            nrMenu.Items.Add(nrRoot);
            nrMenu.Items.Add(anfRoot);

            nrMenu.Opening += nrMenu_Opening;
            rnnItem.CheckedChanged += (s, e) => { try { rig.NeuralNoiseReduction = rnnItem.Checked; updateBoxes(); } catch { } updateMenuAccessibility(rnnItem); };
            spectralItem.CheckedChanged += (s, e) => { try { rig.SpectralNoiseReduction = spectralItem.Checked; updateBoxes(); } catch { } updateMenuAccessibility(spectralItem); };
            legacyNRItem.CheckedChanged += (s, e) => { try { rig.NoiseReductionLegacy = legacyNRItem.Checked; updateBoxes(); } catch { } updateMenuAccessibility(legacyNRItem); };
            anfFftItem.CheckedChanged += (s, e) => { try { rig.AutoNotchFFT = anfFftItem.Checked; updateBoxes(); } catch { } updateMenuAccessibility(anfFftItem); };
            anfLegacyItem.CheckedChanged += (s, e) => { try { rig.AutoNotchLegacy = anfLegacyItem.Checked; updateBoxes(); } catch { } updateMenuAccessibility(anfLegacyItem); };

            // Attach to the Filters user control (right-click)
            this.ContextMenuStrip = nrMenu;
        }
        private void nrMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                // Reflect current state
                rnnItem.Checked = rig.NeuralNoiseReduction;
                spectralItem.Checked = rig.SpectralNoiseReduction;
                legacyNRItem.Checked = rig.NoiseReductionLegacy;
                anfFftItem.Checked = rig.AutoNotchFFT;
                anfLegacyItem.Checked = rig.AutoNotchLegacy;

                // Mode-aware enablement: allow NR in SSB/AM/digital, disable in CW and FM variants
                string mode = rig.Mode.ToLowerInvariant();
                bool cwOrFm = mode.StartsWith("cw") || mode.Contains("fm");
                rnnItem.Enabled = !cwOrFm;
                spectralItem.Enabled = !cwOrFm;
                legacyNRItem.Enabled = !cwOrFm;
                // ANF gating: allow ANF in non-FM; disable on FM
                bool fmMode = mode.Contains("fm");
                anfFftItem.Enabled = !fmMode;
                anfLegacyItem.Enabled = !fmMode;

                // License-aware hints: if FeatureLicense unknown/disabled, keep enabled but annotate tooltip
                var fl = rig.theRadio.FeatureLicense;
                string tip = null;
                if (fl == null)
                {
                    tip = "License info unavailable; actions will attempt and log.";
                }
                rnnItem.ToolTipText = tip;
                spectralItem.ToolTipText = tip;
                legacyNRItem.ToolTipText = tip;
                anfFftItem.ToolTipText = tip;
                anfLegacyItem.ToolTipText = tip;

                updateMenuAccessibility(rnnItem);
                updateMenuAccessibility(spectralItem);
                updateMenuAccessibility(legacyNRItem);
                updateMenuAccessibility(anfFftItem);
                updateMenuAccessibility(anfLegacyItem);
            }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("NR menu opening error: " + ex.Message, JJTrace.TraceLevel.Error);
            }
        }

        // Top-level Filters menu with Noise Reduction submenu (mirrors context menu)
        private MenuStrip filtersMenuStrip;
        private ToolStripMenuItem filtersRoot;
        private ToolStripMenuItem filtersNRRoot;
        private ToolStripMenuItem filtersANFRoot;
        private void setupFiltersMenuStrip()
        {
            filtersMenuStrip = new MenuStrip();
            filtersRoot = new ToolStripMenuItem("Filters");

            // Noise Reduction submenu
            filtersNRRoot = new ToolStripMenuItem("Noise Reduction");
            var rnn = new ToolStripMenuItem("Neural (RNN)") { CheckOnClick = true };
            var spectral = new ToolStripMenuItem("Spectral (NRF/NRS)") { CheckOnClick = true };
            var legacy = new ToolStripMenuItem("Legacy (NRL)") { CheckOnClick = true };
            filtersNRRoot.DropDownItems.AddRange(new ToolStripItem[] { rnn, spectral, legacy });

            // Auto Notch submenu
            filtersANFRoot = new ToolStripMenuItem("Auto Notch");
            var anfFft = new ToolStripMenuItem("FFT (ANFT)") { CheckOnClick = true };
            var anfLegacy = new ToolStripMenuItem("Legacy (ANFL)") { CheckOnClick = true };
            filtersANFRoot.DropDownItems.AddRange(new ToolStripItem[] { anfFft, anfLegacy });

            // Bind actions
            rnn.CheckedChanged += (s, e) => { try { rig.NeuralNoiseReduction = rnn.Checked; updateBoxes(); } catch { } updateMenuAccessibility(rnn); };
            spectral.CheckedChanged += (s, e) => { try { rig.SpectralNoiseReduction = spectral.Checked; updateBoxes(); } catch { } updateMenuAccessibility(spectral); };
            legacy.CheckedChanged += (s, e) => { try { rig.NoiseReductionLegacy = legacy.Checked; updateBoxes(); } catch { } updateMenuAccessibility(legacy); };
            anfFft.CheckedChanged += (s, e) => { try { rig.AutoNotchFFT = anfFft.Checked; updateBoxes(); } catch { } updateMenuAccessibility(anfFft); };
            anfLegacy.CheckedChanged += (s, e) => { try { rig.AutoNotchLegacy = anfLegacy.Checked; updateBoxes(); } catch { } updateMenuAccessibility(anfLegacy); };

            // Opening sync and gating
            filtersNRRoot.DropDownOpening += (s, e) => syncAndGateNRItems(rnn, spectral, legacy);
            filtersANFRoot.DropDownOpening += (s, e) => syncAndGateANFItems(anfFft, anfLegacy);

            filtersRoot.DropDownItems.Add(filtersNRRoot);
            filtersRoot.DropDownItems.Add(filtersANFRoot);
            filtersMenuStrip.Items.Add(filtersRoot);

            // Add to control
            this.Controls.Add(filtersMenuStrip);
            filtersMenuStrip.Dock = DockStyle.Top;
        }

        private void syncAndGateNRItems(ToolStripMenuItem rnn, ToolStripMenuItem spectral, ToolStripMenuItem legacy)
        {
            try
            {
                rnn.Checked = rig.NeuralNoiseReduction;
                spectral.Checked = rig.SpectralNoiseReduction;
                legacy.Checked = rig.NoiseReductionLegacy;
                string mode = rig.Mode.ToLowerInvariant();
                bool cwOrFm = mode.StartsWith("cw") || mode.Contains("fm");
                rnn.Enabled = !cwOrFm;
                spectral.Enabled = !cwOrFm;
                legacy.Enabled = !cwOrFm;
                var fl = rig.theRadio.FeatureLicense;
                string tip = (fl == null) ? "License info unavailable; actions will attempt and log." : null;
                rnn.ToolTipText = tip; spectral.ToolTipText = tip; legacy.ToolTipText = tip;

                updateMenuAccessibility(rnn);
                updateMenuAccessibility(spectral);
                updateMenuAccessibility(legacy);
            }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("Filters NR menu sync error: " + ex.Message, JJTrace.TraceLevel.Error);
            }
        }

        private void syncAndGateANFItems(ToolStripMenuItem anfFft, ToolStripMenuItem anfLegacy)
        {
            try
            {
                anfFft.Checked = rig.AutoNotchFFT;
                anfLegacy.Checked = rig.AutoNotchLegacy;
                string mode = rig.Mode.ToLowerInvariant();
                bool fmMode = mode.Contains("fm");
                anfFft.Enabled = !fmMode;
                anfLegacy.Enabled = !fmMode;
                var fl = rig.theRadio.FeatureLicense;
                string tip = (fl == null) ? "License info unavailable; actions will attempt and log." : null;
                anfFft.ToolTipText = tip; anfLegacy.ToolTipText = tip;

                updateMenuAccessibility(anfFft);
                updateMenuAccessibility(anfLegacy);
            }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine("Filters ANF menu sync error: " + ex.Message, JJTrace.TraceLevel.Error);
            }
        }

        private void TNFEnableButton_Click(object sender, EventArgs e)
        {
            bool newState = !rig.TNF;
            rig.TNF = newState;
            TNFEnableText(newState);
        }

        private void TNFEnableText()
        {
            TNFEnableText(rig.TNF);
        }
        private delegate void textDel(Control tb, string text);
        private static textDel textboxText =
        (Control tb, string text) =>
        {
            tb.Text = text;
            tb.Tag = text;
        };
        private void TNFEnableText(bool state)
        {
            string text = (state) ? "TNFOff" : "TNFOn"; // opposit from the current state.
            if (TNFEnableButton.InvokeRequired) TNFEnableButton.Invoke(textboxText, new object[] { TNFEnableButton, text });
            else textboxText(TNFEnableButton, text);
        }

        private FlexDB flexDB;
        private void ExportButton_Click(object sender, EventArgs e)
        {
            if (flexDB == null) flexDB = new FlexDB((Flex)rig);
            flexDB.Export();
        }

        private void ImportButton_Click(object sender, EventArgs e)
        {
            if (flexDB == null) flexDB = new FlexDB((Flex)rig);
            flexDB.Import();
        }

        internal void Close()
        {
            Tracing.TraceLine("Flex6300Filters.Close", TraceLevel.Info);
            if ((rig != null) && (theRadio != null))
            {
                if (panadapter != null) panadapter.DataReady -= panDataHandler;
                if (waterfall != null) waterfall.DataReady -= waterfallDataHandler;
            }
        }

        private void RXEqButton_Click(object sender, EventArgs e)
        {
            Tracing.TraceLine("RXEq button", TraceLevel.Info);
            Equalizer eq = getEq(EqualizerSelect.RX);
            if (eq == null) return;

            // Bring up the form.
            Form eqForm = new FlexEq((Flex)rig, eq);
            eqForm.ShowDialog();
            eqForm.Dispose();
        }

        private void TXEqButton_Click(object sender, EventArgs e)
        {
            Tracing.TraceLine("TXEq button", TraceLevel.Info);
            Equalizer eq = getEq(EqualizerSelect.TX);
            if (eq == null) return;

            // Bring up the form.
            Form eqForm = new FlexEq((Flex)rig, eq);
            eqForm.ShowDialog();
            eqForm.Dispose();
        }

        private Equalizer getEq(EqualizerSelect typ)
        {
            Equalizer rv = theRadio.FindEqualizerByEQSelect(typ);
            if (rv == null)
            {
                rv = theRadio.CreateEqualizer(typ);
                if (!rv.RequestEqualizerFromRadio())
                {
                    Tracing.TraceLine("equalizer RequestFromRadio failed", TraceLevel.Error);
                    rv = null;
                }
            }
            if (rv != null) rv.EQ_enabled = true;
            return rv;
        }

        private void InfoButton_Click(object sender, EventArgs e)
        {
            Form theForm = new FlexInfo((Flex)rig);
            theForm.ShowDialog();
            theForm.Dispose();
        }

        delegate string d1();
        private string infoBoxText(InfoBox box)
        {
            string rv;
            if (box.InvokeRequired)
            {
                d1 d = () => { return box.Text; };
                rv = (string)box.Invoke(d);
            }
            else rv = box.Text;
            return rv;
        }

        private string SWRText()
        {
            return rig.SWR.ToString("F1");
        }

        private void AutoprocControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (AutoprocControl.SelectedIndex == -1) return;
            OpsConfigInfo.AutoProc = (string)AutoprocControl.Items[AutoprocControl.SelectedIndex];
            writeConfig();
            autoprocFunc(Flex6300Filters.rig);
        }

        // Panning region
        #region panning
        private Panadapter panadapter { get { return rig.Panadapter; } }
        private Waterfall waterfall { get { return rig.Waterfall; } }
        private Panadapter oldPan = null;
        private Waterfall oldFall = null;
        private PanRanges panRanges;

#if zero
        private delegate void panTextDel(TextBox tb, string txt);
        panTextDel panText = (TextBox tb, string txt) =>
        { tb.Text = txt; };
        private void showPanText(TextBox tb, string txt)
        {
            if (tb.InvokeRequired) tb.Invoke(panText, new object[] { tb, txt });
            else panText(tb, txt);
        }
#endif

        private void panControlSetup()
        {
            // Pan controls
            panControls = new Collection<Control>();
            PanBox.Tag = "Pan";
            PanLowBox.Tag = PanLowLabel.Text;
            PanHighBox.Tag = PanHighLabel.Text;
            panControls.Add(PanBox);
            panControls.Add(PanLowBox);
            panControls.Add(PanHighBox);

            // Routine called to configure pan adapter controls.
            rig.PanSetup = panSetup;
        }

        // Called when ready to handle the pan adapter.
        private void panSetup()
        {
            Tracing.TraceLine("panSetup", TraceLevel.Info);

            // Other setup
            flexTNF = new FlexTNF(rig);
            TNFEnableText(); // Sets the button text
            TextOut.PerformGenericFunction(AutoprocControl, () => { AutoprocControl.Text = OpsConfigInfo.AutoProc; });

            brailleWidth = rig.Callouts.BrailleCells;
            // If no cells, there's no pan adapter.
            if (brailleWidth <= 0)
            {
                brailleWidth = brailleWidthDefault;
                Tracing.TraceLine("panSetup:no braille cells given, using " + brailleWidth, TraceLevel.Error);
            }

            panRanges = new PanRanges((AllRadios)rig, OperatorsDirectory);
            currentPanData = new PanData(brailleWidth);
            rig.RXFreqChange = rxFreqChange;
        }

        const int fps = 1;
        const int lowDBM = -121;
        const int highDBM = -21;
        const int brailleWidthDefault = 40;
        private int brailleWidth;
        const int brailleScaleup = 50; // How far to scale up the pan width
        const int brailleUpdateSeconds = 1;
        //const ulong stepSizeScalerDefault = 1000; // KHZ
        //private ulong stepSizeScaler = stepSizeScalerDefault;
        private ulong width;
        private ulong stepSize;

        private PanRanges.PanRange segment = null;
        private FlexWaterfall flexPan;

        // Called when the active slice, or active slice's mode or freq changes.
        // Also called to copy a panadapter/waterfall.
        private void rxFreqChange(object o)
        {
            // See if it's a copy.
            if (o is List<Slice>)
            {
                // First parm is the input.
                List<Slice> sList = (List<Slice>)o;
                copyPan(sList[0].Panadapter, sList[1].Panadapter);
                return;
            }

            Slice s = (Slice)o;
            ulong freq = rig.LibFreqtoLong(s.Freq);

#if zero
            // bandsMode = conversion from flex modes.
            Bands.Modes myMode;
            switch (s.DemodMode.ToUpper())
            {
                case "LSB":
                case "USB":
                case "FM":
                case "NFM":
                case "DFM":
                case "AM":
                case "SAM":
                    myMode = Bands.Modes.PhoneCW;
                    break;
                case "CW":
                    myMode = Bands.Modes.CW;
                    break;
                case"DIGU":
                case "DIGL":
                    myMode = Bands.Modes.RTTYData;
                    break;
                default:
                    Tracing.TraceLine("RXFreqChange:bad mode " + rig.Mode.ToString(), TraceLevel.Error);
                    return;
            }
#endif

            // Return if a user segment.
            if ((segment != null) && segment.User) return;

            Tracing.TraceLine("rxFreqChange:" + rig.RXFrequency.ToString(), TraceLevel.Info);
            PanRanges.PanRange oldSegment = segment;
            segment = panRanges.Query(freq);
            if (segment == null)
            {
                // make this brailleWidth KHZ wide.
                ulong low = freq - (((ulong)brailleWidth * 1000) / 2);
                segment = new PanRanges.PanRange(low, low + ((ulong)brailleWidth * 1000), PanRanges.PanRangeStates.temp);
            }

            if (segment != oldSegment)
            {
                // start panning.
                panParameterSetup();
            }
        }

        private void panParameterSetup()
        {
            Tracing.TraceLine("PanParameterSetup", TraceLevel.Info);
            try
            {
                if ((panadapter == null) || (waterfall == null)) return;
                //int rf = panadapter.RFGain;
                //int rl = panadapter.RFGainLow;
                //int rh = panadapter.RFGainHigh;
                if ((oldPan != null) && (oldPan != panadapter))
                {
                    oldPan.DataReady -= panDataHandler;
                }
                oldPan = panadapter;
                if ((oldFall != null) && (oldFall != waterfall))
                {
                    oldFall.DataReady -= waterfallDataHandler;
                }
                oldFall = waterfall;
                panadapter.DataReady -= panDataHandler;
                waterfall.DataReady -= waterfallDataHandler;
                if (flexPan != null) flexPan.Stop();
                flexPan = null;
                // Display the low and high.
                TextOut.DisplayText(PanLowBox, rig.Callouts.FormatFreq(segment.Low), false, true);
                TextOut.DisplayText(PanHighBox, rig.Callouts.FormatFreq(segment.High), false, true);
                lock (currentPanData)
                {
                    currentPanData.LowFreq = rig.LongFreqToLibFreq(segment.Low);
                    currentPanData.HighFreq = rig.LongFreqToLibFreq(segment.High);
                }

                width = segment.Width; // in hz
                stepSize = (ulong)((float)width / (float)brailleWidth); // hz / cell
                if (stepSize == 0) stepSize = 1;
                //rig.q.Enqueue((Flex.FunctionDel)(() => { panadapter.Size = new System.Windows.Size(width, highDBM - lowDBM); }));
                //rig.q.Enqueue((Flex.FunctionDel)(() => { panadapter.Size = new System.Windows.Size(brailleWidth * brailleScaleup, 700); }));
                rig.q.Enqueue((Flex.FunctionDel)(() => { panadapter.Width = (brailleWidth * brailleScaleup) + brailleWidth; }));
                rig.q.Enqueue((Flex.FunctionDel)(() => { panadapter.Height = 700; }));
                rig.q.Enqueue((Flex.FunctionDel)(() => { panadapter.FPS = fps; }));
                rig.q.Enqueue((Flex.FunctionDel)(() => { panadapter.CenterFreq = rig.LongFreqToLibFreq(segment.Low + (ulong)(width / 2)); }));
                rig.q.Enqueue((Flex.FunctionDel)(() => { waterfall.CenterFreq = rig.LongFreqToLibFreq(segment.Low + (ulong)(width / 2)); }));
                //rig.q.Enqueue((Flex.FunctionDel)(() => { panadapter.Bandwidth = rig.LongFreqToLibFreq((ulong)width); }));
                rig.q.Enqueue((Flex.FunctionDel)(() => { panadapter.Bandwidth = rig.LongFreqToLibFreq((ulong)width + stepSize); }));
                rig.q.Enqueue((Flex.FunctionDel)(() => { waterfall.Bandwidth = rig.LongFreqToLibFreq((ulong)width + stepSize); }));
                rig.q.Enqueue((Flex.FunctionDel)(() => { panadapter.LowDbm = lowDBM; }));
                rig.q.Enqueue((Flex.FunctionDel)(() => { panadapter.HighDbm = highDBM; }));

                flexPan = new FlexWaterfall(rig, segment.Low, segment.High, rig.Callouts.BrailleCells);
                panadapter.DataReady += panDataHandler;
                waterfall.DataReady += waterfallDataHandler;
            }
            catch(Exception ex)
            {
                Tracing.TraceLine("panParameterSetup exception" + ex.Message + ex.StackTrace, TraceLevel.Error);
            }
        }

        private void copyPan(Panadapter inPan, Panadapter outPan)
        {
            Waterfall inFall = rig.GetPanadaptersWaterfall(inPan);
            Waterfall outFall = rig.GetPanadaptersWaterfall(outPan);
            rig.q.Enqueue((Flex.FunctionDel)(() => { outPan.Width = inPan.Width; }));
            rig.q.Enqueue((Flex.FunctionDel)(() => { outPan.Height = inPan.Height; }));
            rig.q.Enqueue((Flex.FunctionDel)(() => { outPan.FPS = inPan.FPS; }));
            rig.q.Enqueue((Flex.FunctionDel)(() => { outPan.CenterFreq = inPan.CenterFreq; }));
            rig.q.Enqueue((Flex.FunctionDel)(() => { outFall.CenterFreq = inFall.CenterFreq; }));
            rig.q.Enqueue((Flex.FunctionDel)(() => { outPan.Bandwidth = outPan.Bandwidth; }));
            rig.q.Enqueue((Flex.FunctionDel)(() => { outFall.Bandwidth = inFall.Bandwidth; }));
            rig.q.Enqueue((Flex.FunctionDel)(() => { outPan.LowDbm = inPan.LowDbm; }));
            rig.q.Enqueue((Flex.FunctionDel)(() => { outPan.HighDbm = inPan.HighDbm; }));
        }

        internal class PanData
        {
            public int Cells;
            public string Line; // braille line
            public double[] frequencies;
            public double LowFreq, HighFreq;
            public double HZPerCell { get { return (HighFreq - LowFreq) / Cells; } }
            public int EntryPosition;
            public PanData(int cells)
            {
                Cells = cells;
                frequencies = new double[cells];
            }
            public int FreqToCell(double f)
            {
                if (f < LowFreq) f = LowFreq;
                else if (f > HighFreq) f = HighFreq;
                double relFreq = f - LowFreq;
                return (int)(relFreq / HZPerCell);
            }
            public double CellToFreq(int c)
            {
                if (c < 0) c = 0;
                else if (c > Cells - 1) c = Cells - 1;
                return LowFreq + (c * HZPerCell);
            }
        }
        private PanData currentPanData;

        private void panDataHandler(Panadapter pan, ushort[] data)
        {
            if (flexPan == null)
            {
                return;
            }
            try
            {
                PanData panOut = flexPan.Read();
                if ((panOut != null) && (panOut.Line.Length > 0))
                {
                    Tracing.TraceLine("panData:" + panOut.Line.Length.ToString() + ' ' + panOut.Line, TraceLevel.Verbose);
                    int pos = 0;
                    lock (currentPanData)
                    {
                        // Preserve the pan data for gotoFreq.
                        Array.Copy(panOut.frequencies, currentPanData.frequencies, panOut.frequencies.Length);
                        currentPanData.Line = string.Copy(panOut.Line);
                        pos = currentPanData.EntryPosition;
                    }
                    TextOut.PerformGenericFunction(PanBox,
                         () => {
                             PanBox.Text = panOut.Line;
                             PanBox.SelectionStart = pos;
                         });
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("panDataHandler exception:" + ex.Message, TraceLevel.Error);
            }
        }

        private void waterfallDataHandler(Waterfall w, WaterfallTile tile)
        {
            if (flexPan == null)
            {
                return;
            }
            flexPan.Write(tile);
        }

        private void gotoFreq(double freq)
        {
            Tracing.TraceLine("gotoFreq:" + freq.ToString() + ' ' + stepSize.ToString() + ' ' + segment.Low.ToString(), TraceLevel.Info);
            freq = Math.Round(freq, 4); // round to the nearest 100 hz.
            rig.q.Enqueue((Flex.FunctionDel)(() => { theRadio.ActiveSlice.Freq = freq; }));
            rig.Callouts.GotoHome();
        }

        private bool checkForRangeJump(Keys key)
        {
            bool rv = false;

            if (segment != null)
            {
                PanRanges.PanRange newRange;
                switch (key)
                {
                    case Keys.PageUp:
                        if ((newRange = panRanges.PriorRange(segment)) != null)
                        {
                            segment = newRange;
                            panParameterSetup();
                            rv = true;
                        }
                        break;
                    case Keys.PageDown:
                        if ((newRange = panRanges.NextRange(segment)) != null)
                        {
                            segment = newRange;
                            rv = true;
                            panParameterSetup();
                        }
                        break;
                    case Keys.L:
                        rv = true; // just means we handled the key.
                        // List
                        Collection<PanRanges.PanRange> r = panRanges.QueryPertinentRanges(rig.LibFreqtoLong(theRadio.ActiveSlice.Freq));
                        if (r.Count > 0)
                        {
                            PanListForm f = new PanListForm(r);
                            if (f.ShowDialog() == DialogResult.OK)
                            {
                                segment = f.SelectedRange;
                                panParameterSetup();
                            }
                            f.Dispose();
                        }
                        break;
                }
            }
            return rv;
        }

        private void PanBox_KeyDown(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = checkForRangeJump(e.KeyData);
        }

        private void PanBox_MouseClick(object sender, MouseEventArgs e)
        {
            int pos = PanBox.GetCharIndexFromPosition(e.Location);
            double freq;
            lock (currentPanData)
            {
                freq = currentPanData.frequencies[pos];
            }
            gotoFreq(freq);
        }

        private const int panTimerInterval = 100;
        private System.Threading.Timer panTimer;

        private void PanBox_Enter(object sender, EventArgs e)
        {
            int pos = 0;
            lock (currentPanData)
            {
                pos = currentPanData.FreqToCell(theRadio.ActiveSlice.Freq);
                currentPanData.EntryPosition = pos;
            }
            if (pos < PanBox.Text.Length)
            {
                PanBox.SelectionStart = pos;
                PanBox.SelectionLength = 0;
            }
            else Tracing.TraceLine("Flex6300Filters.PanBox_enter text length:" + pos + ' ' + PanBox.Text.Length, TraceLevel.Error);

            panTimer = new System.Threading.Timer(panTimerHandler, null, panTimerInterval, panTimerInterval);
        }

        private void PanBox_Leave(object sender, EventArgs e)
        {
            if (panTimer != null)
            {
                panTimer.Dispose();
            }
        }

        private void panTimerHandler(object state)
        {
            bool go = false;
            // Get cursor's current position.
            int pos = 0;
            TextOut.PerformGenericFunction(PanBox, () => { pos = PanBox.SelectionStart; });
            double freq = 0;
            lock (currentPanData)
            {
                // Set go switch if position changed.
                go = (pos != currentPanData.EntryPosition);
                if (go)
                {
                    currentPanData.EntryPosition = pos;
                    freq = currentPanData.frequencies[pos];
                }
            }
            if (go) gotoFreq(freq);
        }

        private void PanLowBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Control && !e.Alt && !e.Shift &&
                !((e.KeyCode >= Keys.D0) && (e.KeyCode <= Keys.D9)))
            {
                e.SuppressKeyPress = checkForRangeJump(e.KeyData);
                if (e.SuppressKeyPress)
                {
                    TextOut.DisplayText(PanLowBox, rig.Callouts.FormatFreq(segment.Low), false, true);
                }
            }
        }

        private void PanHighBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Control && !e.Alt && !e.Shift &&
                !((e.KeyCode >= Keys.D0) && (e.KeyCode <= Keys.D9)))
            {
                e.SuppressKeyPress = checkForRangeJump(e.KeyData);
                if (e.SuppressKeyPress)
                {
                    TextOut.DisplayText(PanHighBox, rig.Callouts.FormatFreq(segment.High), false, true);
                }
            }
        }

        private void ChangeButton_Click(object sender, EventArgs e)
        {
            ulong low, high;
            if ((low = rig.Callouts.FormatFreqForRadio(PanLowBox.Text)) == 0)
            {
                MessageBox.Show(badLowFreq, "error", MessageBoxButtons.OK);
                return;
            }
            if (((high = rig.Callouts.FormatFreqForRadio(PanHighBox.Text)) == 0) ||
                (high <= low))
            {
                MessageBox.Show(badHighFreq, "error", MessageBoxButtons.OK);
                return;
            }
            segment = new PanRanges.PanRange(low, high);
            panParameterSetup();
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            if ((segment != null) && !segment.Saved)
            {
                panRanges.Insert(segment);
            }
        }

        private void EraseButton_Click(object sender, EventArgs e)
        {
            if (segment.Saved & !segment.Permanent) panRanges.Remove(segment);

            // Try a new segment.
            segment = null;
            rxFreqChange(rig.VFOToSlice(rig.RXVFO));
        }

        private Thread zeroBeatThread;
        private ulong zeroBeatValue;
        public ulong ZeroBeatFreq()
        {
            zeroBeatValue = 0;
            if (flexPan != null)
            {
                zeroBeatThread = new Thread(zeroBeatProc);
                zeroBeatThread.Name = "zeroBeatThread";
                zeroBeatThread.Start();
                AllRadios.await(() => { return !zeroBeatThread.IsAlive; }, 1100);
            }
            Tracing.TraceLine("FlexFilter ZeroBeatFreq:" + zeroBeatValue.ToString(), TraceLevel.Info);
            return zeroBeatValue;
        }
        private const int totalTime = 1000;
        private const int iterations = 10;
        class freqCount
        {
            public ulong Freq;
            public int Count;
            public freqCount(ulong f, int c)
            {
                Freq = f;
                Count = c;
            }
        }
        private void zeroBeatProc()
        {
            int sanity = iterations;
            Dictionary<ulong, freqCount> freqs = new Dictionary<ulong, freqCount>();
            // Find freq with the most high points.
            while (sanity-- != 0)
            {
                freqCount freqCT = new freqCount(flexPan.ZeroBeatFreq(), 1);
                if (freqs.Keys.Contains(freqCT.Freq))
                {
                    freqs[freqCT.Freq].Count++;
                    // If won't find a bigger one...
                    if (freqCT.Count == (iterations / 2))
                    {
                        zeroBeatValue = freqCT.Freq;
                        break;
                    }
                }
                else freqs.Add(freqCT.Freq, freqCT);
                Thread.Sleep(totalTime / iterations);
            }
            // Note that zeroBeatValue is initially 0.
            // If set above, we don't need to do this,
            // otherwise use the highest count.
            if (zeroBeatValue == 0)
            {
                int maxCount = 0;
                foreach (freqCount fc in freqs.Values)
                {
                    if (fc.Count > maxCount)
                    {
                        maxCount = fc.Count;
                        zeroBeatValue = fc.Freq;
                    }
                }
            }
            Tracing.TraceLine("zeroBeatProc finished:" + zeroBeatValue.ToString(), TraceLevel.Info);
        }
#endregion
    }
}
