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
using JJTrace;
using MsgLib;
using RadioBoxes;

namespace Radios
{
    public partial class Flex6300Filters : UserControl, IFilterControl
    {
        private const string badLowFreq = "The low value must be a valid frequency";
        private const string badHighFreq = "The high value must be a valid frequency, and > the low value.";
        internal static FlexBase rig;
        private Radio theRadio
        {
            get { return (rig != null) ? rig.theRadio : null; }
        }

        internal string OperatorsDirectory
        {
            get {  return rig.ConfigDirectory + '\\' + rig.OperatorName; }
        }
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
        internal delegate void modeChangeFuncDel(FlexBase rig);
        private Collection<modeChangeFuncDel> modeChangeSpecials;
        private FlexTNF flexTNF;
        private delegate void specialDel();
        private Collection<specialDel> specials;
        private ToolTip licenseToolTip;
        private bool licenseGateInitialized;
        private bool lastHasSlice;
        private bool lastLicenseReported;
        private bool lastLicenseEnabled;
        private bool lastReadOnly;
        private string lastTooltip;
        private bool lastDiversityReady;
        private string lastDiversityGateMessage;

        // Noise Reduction / ANF menus
        private ContextMenuStrip nrMenu;
        private ToolStripMenuItem nrRoot;
        private ToolStripMenuItem rnnItem;
        private ToolStripMenuItem spectralItem;
        private ToolStripMenuItem legacyNRItem;
        private ToolStripMenuItem anfRoot;
        private ToolStripMenuItem anfFftItem;
        private ToolStripMenuItem anfLegacyItem;
        private MenuStrip filtersMenuStrip;
        private ToolStripMenuItem filtersRoot;
        private ToolStripMenuItem filtersNoiseRoot;
        private ToolStripMenuItem filtersNRRoot;
        private ToolStripMenuItem filtersANFRoot;
        private ToolStripMenuItem filtersCWRoot;
        private ToolStripMenuItem filtersCWAutotuneItem;
        private static void updateMenuAccessibility(ToolStripMenuItem item)
        {
            if (item == null) return;
            string label = item.Text ?? string.Empty;
            if (label.IndexOf('&') >= 0) label = label.Replace("&", "");

            if (item.CheckOnClick)
            {
                string state = item.Checked ? "checked" : "not checked";
                if (!item.Enabled) state = "unavailable";
                item.AccessibleRole = AccessibleRole.CheckButton;
                item.AccessibleName = label + " " + state;
                item.AccessibleDescription = state;
            }
            else
            {
                item.AccessibleRole = AccessibleRole.MenuItem;
                item.AccessibleName = label;
                item.AccessibleDescription = null;
            }
        }

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
            private Radios.FlexBase.OffOnValues val;
            public string Display { get { return val.ToString(); } }
            public Radios.FlexBase.OffOnValues RigItem { get { return val; } }
            public offOnElement(Radios.FlexBase.OffOnValues v)
            {
                val = v;
            }
        }

        internal class toneCTCSSElement
        {
            private FlexBase.ToneCTCSSValue val;
            public string Display { get { return val.ToString(); } }
            public FlexBase.ToneCTCSSValue RigItem { get { return val; } }
            public toneCTCSSElement(FlexBase.ToneCTCSSValue v)
            {
                val = v;
            }
        }

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
        private ArrayList noiseBlankerList;
        private ArrayList widebandNoiseBlankerList, noiseReductionList;
        private class KeyerElement
        {
            private FlexBase.IambicValues val;
            public string Display { get { return val.ToString(); } }
#if zero
            public string Display
            {
                get
                {
                    string str = "";
                    switch (val)
                    {
                        case FlexBase.IambicValues.off: str = "off"; break;
                        case FlexBase.IambicValues.iambicA: str = "iambA"; break;
                        case FlexBase.IambicValues.iambicB: str = "iambB"; break;
                    }
                    return str;
                }
            }
#endif
            public FlexBase.IambicValues RigItem { get { return val; } }
            public KeyerElement(FlexBase.IambicValues v)
            {
                val = v;
            }
        }
        private ArrayList keyerList, CWLList, CWReverseList;

        private class processorSettingElement
        {
            private FlexBase.ProcessorSettings val;
            public string Display { get { return val.ToString(); } }
            public FlexBase.ProcessorSettings RigItem { get { return val; } }
            public processorSettingElement(FlexBase.ProcessorSettings v)
            {
                val = v;
            }
        }
        private static processorSettingElement[] processorSettingItems =
        {
            new processorSettingElement(FlexBase.ProcessorSettings.NOR),
            new processorSettingElement(FlexBase.ProcessorSettings.DX),
            new processorSettingElement(FlexBase.ProcessorSettings.DXX),
        };
        private ArrayList processorOnList, processorSettingList, CompanderList;
        private ArrayList micBoostList, micBiasList, monitorList;

        private ArrayList toneModeList;
        private ArrayList toneFrequencyList;
        private ArrayList squelchList;

        internal class offsetDirectionElement
        {
            private FlexBase.OffsetDirections val;
            public string Display { get { return val.ToString(); } }
            public FlexBase.OffsetDirections RigItem { get { return val; } }
            public offsetDirectionElement(FlexBase.OffsetDirections v)
            {
                val = v;
            }
        }
        internal static offsetDirectionElement[] offsetDirectionValues =
        {
            new offsetDirectionElement(FlexBase.OffsetDirections.off),
            new offsetDirectionElement(FlexBase.OffsetDirections.minus),
            new offsetDirectionElement(FlexBase.OffsetDirections.plus)
        };
        private ArrayList offsetDirectionList;

        // Offset is in KHZ, and freq is only up to 6 meters.
        private ArrayList emphasisList, FM1750List;
        private ArrayList binauralList, playList, recordList, daxTXList;
        private ArrayList diversityList;

        private const int txAntennaDisplayOffset = 1;
        private ArrayList rxAntList, TxAntList;

        // Used to automatically enable/disable the speech proc
        private static string[] autoprocValues = { "off", "ssb" };
        private void autoprocFunc(FlexBase rig)
        {
            TextOut.PerformGenericFunction(AutoprocControl, () =>
                {
                    if ((string)AutoprocControl.Items[AutoprocControl.SelectedIndex] == autoprocValues[1]) // ssb
                    {
                        if ((rig.TXMode == "lsb") | (rig.TXMode == "usb"))
                            rig.ProcessorOn = FlexBase.OffOnValues.on;
                        else rig.ProcessorOn = FlexBase.OffOnValues.off;
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
        /// <remarks>
        /// Radio is started now.
        /// </remarks>
        public Flex6300Filters(FlexBase r)
        {
            InitializeComponent();

            rig = r;
            licenseToolTip = new ToolTip();

            if (rig != null)
            {
                rig.FeatureLicenseChanged += Rig_FeatureLicenseChanged;
            }

            applyLicenseGating();

            // Get peroperator configuration items.
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

            AGCThresholdControl.LowValue = FlexBase.AGCThresholdMin;
            AGCThresholdControl.HighValue = FlexBase.AGCThresholdMax;
            AGCThresholdControl.Increment = FlexBase.AGCThresholdIncrement;
            AGCThresholdControl.UpdateDisplayFunction =
                () => { return rig.AGCThreshold; };
            AGCThresholdControl.UpdateRigFunction =
                (int v) => { rig.AGCThreshold = v; };
            numberBoxes.Add(AGCThresholdControl);

            // Autonotch filter, ANF
            ANFList = new ArrayList();
            ANFList.Add(new offOnElement(FlexBase.OffOnValues.off));
            ANFList.Add(new offOnElement(FlexBase.OffOnValues.on));
            ANFControl.TheList = ANFList;
            ANFControl.UpdateDisplayFunction =
                () => { return rig.ANF; };
            ANFControl.UpdateRigFunction =
                (object v) => { rig.ANF = (FlexBase.OffOnValues)v; };
            combos.Add(ANFControl);

            // ANF level
            ANFLevelControl.LowValue = FlexBase.AutoNotchLevelMin;
            ANFLevelControl.HighValue = FlexBase.AutoNotchLevelMax;
            ANFLevelControl.Increment = FlexBase.AutoNotchLevelIncrement;
            ANFLevelControl.UpdateDisplayFunction =
                () => { return rig.AutoNotchLevel; };
            ANFLevelControl.UpdateRigFunction =
                (int v) => { rig.AutoNotchLevel = v; };
            numberBoxes.Add(ANFLevelControl);

            // Auto peaking filter, APF
            APFList = new ArrayList();
            APFList.Add(new offOnElement(FlexBase.OffOnValues.off));
            APFList.Add(new offOnElement(FlexBase.OffOnValues.on));
            APFControl.TheList = APFList;
            APFControl.UpdateDisplayFunction =
                () => { return rig.APF; };
            APFControl.UpdateRigFunction =
                (object v) => { rig.APF = (FlexBase.OffOnValues)v; };
            combos.Add(APFControl);

            // APF level
            APFLevelControl.LowValue = FlexBase.AutoPeakLevelMin;
            APFLevelControl.HighValue = FlexBase.AutoPeakLevelMax;
            APFLevelControl.Increment = FlexBase.AutoPeakLevelIncrement;
            APFLevelControl.UpdateDisplayFunction =
                () => { return rig.AutoPeakLevel; };
            APFLevelControl.UpdateRigFunction =
                (int v) => { rig.AutoPeakLevel = v; };
            numberBoxes.Add(APFLevelControl);

            // Noise blanker
            noiseBlankerList = new ArrayList();
            noiseBlankerList.Add(new offOnElement(FlexBase.OffOnValues.off));
            noiseBlankerList.Add(new offOnElement(FlexBase.OffOnValues.on));
            NoiseBlankerControl.TheList = noiseBlankerList;
            NoiseBlankerControl.UpdateDisplayFunction =
                () => { return rig.NoiseBlanker; };
            NoiseBlankerControl.UpdateRigFunction =
                (object v) => { rig.NoiseBlanker = (FlexBase.OffOnValues)v; };
            combos.Add(NoiseBlankerControl);

            NoiseBlankerLevelControl.LowValue = FlexBase.NoiseBlankerValueMin;
            NoiseBlankerLevelControl.HighValue = FlexBase.NoiseBlankerValueMax;
            NoiseBlankerLevelControl.Increment = FlexBase.NoiseBlankerValueIncrement;
            NoiseBlankerLevelControl.UpdateDisplayFunction =
                () => { return rig.NoiseBlankerLevel; };
            NoiseBlankerLevelControl.UpdateRigFunction =
                (int v) => { rig.NoiseBlankerLevel = v; };
            numberBoxes.Add(NoiseBlankerLevelControl);

            // Wide band noise blanker
            widebandNoiseBlankerList = new ArrayList();
            widebandNoiseBlankerList.Add(new offOnElement(FlexBase.OffOnValues.off));
            widebandNoiseBlankerList.Add(new offOnElement(FlexBase.OffOnValues.on));
            WidebandNoiseBlankerControl.TheList = widebandNoiseBlankerList;
            WidebandNoiseBlankerControl.UpdateDisplayFunction =
                () => { return rig.WidebandNoiseBlanker; };
            WidebandNoiseBlankerControl.UpdateRigFunction =
                (object v) => { rig.WidebandNoiseBlanker = (FlexBase.OffOnValues)v; };
            combos.Add(WidebandNoiseBlankerControl);

            WidebandNoiseBlankerLevelControl.LowValue = FlexBase.NoiseBlankerValueMin;
            WidebandNoiseBlankerLevelControl.HighValue = FlexBase.NoiseBlankerValueMax;
            WidebandNoiseBlankerLevelControl.Increment = FlexBase.NoiseBlankerValueIncrement;
            WidebandNoiseBlankerLevelControl.UpdateDisplayFunction =
                () => { return rig.WidebandNoiseBlankerLevel; };
            WidebandNoiseBlankerLevelControl.UpdateRigFunction =
                (int v) => { rig.WidebandNoiseBlankerLevel = v; };
            numberBoxes.Add(WidebandNoiseBlankerLevelControl);

#if zero
            // pre-amp
            PreAmpControlList = new ArrayList();
            PreAmpControlList.Add(new offOnElement(FlexBase.OffOnValues.off));
            PreAmpControlList.Add(new offOnElement(FlexBase.OffOnValues.on));
            PreAmpControl.TheList = PreAmpControlList;
            PreAmpControl.UpdateDisplayFunction =
                () => { return rig.PreAmp; };
            PreAmpControl.UpdateRigFunction =
                (object v) => { rig.PreAmp = (FlexBase.OffOnValues)v; };
            combos.Add(PreAmpControl);
#endif

            // RF Gain for the pan adapter
            RFGainControl.LowValue = rig.RFGainMin;
            RFGainControl.HighValue = rig.RFGainMax;
            RFGainControl.Increment = rig.RFGainIncrement;
            RFGainControl.UpdateDisplayFunction =
                () => { return rig.RFGain; };
            RFGainControl.UpdateRigFunction =
                (int v) => { rig.RFGain = v; };
            numberBoxes.Add(RFGainControl);

            // Noise reduction
            noiseReductionList = new ArrayList();
            noiseReductionList.Add(new offOnElement(FlexBase.OffOnValues.off));
            noiseReductionList.Add(new offOnElement(FlexBase.OffOnValues.on));
            NoiseReductionControl.TheList = noiseReductionList;
            NoiseReductionControl.UpdateDisplayFunction =
                () => { return rig.NoiseReduction; };
            NoiseReductionControl.UpdateRigFunction =
                (object v) => { rig.NoiseReduction = (FlexBase.OffOnValues)v; };
            combos.Add(NoiseReductionControl);

            NoiseReductionLevelControl.LowValue = FlexBase.NoiseReductionValueMin;
            NoiseReductionLevelControl.HighValue = FlexBase.NoiseReductionValueMax;
            NoiseReductionLevelControl.Increment = FlexBase.NoiseReductionValueIncrement;
            NoiseReductionLevelControl.UpdateDisplayFunction =
                () => { return rig.NoiseReductionLevel; };
            NoiseReductionLevelControl.UpdateRigFunction =
                (int v) => { rig.NoiseReductionLevel = v; };
            numberBoxes.Add(NoiseReductionLevelControl);

            // breakin
            BreakinDelayControl.LowValue = FlexBase.BreakinDelayMin;
            BreakinDelayControl.HighValue = FlexBase.BreakinDelayMax;
            BreakinDelayControl.Increment = FlexBase.BreakinDelayIncrement;
            BreakinDelayControl.UpdateDisplayFunction =
                () => { return rig.BreakinDelay; };
            BreakinDelayControl.UpdateRigFunction =
                (int v) => { rig.BreakinDelay = v; };
            numberBoxes.Add(BreakinDelayControl);

            // keyer
            keyerList = new ArrayList();
            keyerList.Add(new KeyerElement(FlexBase.IambicValues.off));
            keyerList.Add(new KeyerElement(FlexBase.IambicValues.iambicA));
            keyerList.Add(new KeyerElement(FlexBase.IambicValues.iambicB));
            KeyerControl.TheList = keyerList;
            KeyerControl.UpdateDisplayFunction =
                () => { return rig.Keyer; };
            KeyerControl.UpdateRigFunction =
                (object v) => { rig.Keyer = (FlexBase.IambicValues)v; };
            combos.Add(KeyerControl);

            // CW paddle reverse
            CWReverseList = new ArrayList();
            CWReverseList.Add(new trueFalseElement(false));
            CWReverseList.Add(new trueFalseElement(true));
            CWReverseControl.TheList = CWReverseList;
            CWReverseControl.UpdateDisplayFunction =
                () => { return rig.CWReverse; };
            CWReverseControl.UpdateRigFunction =
                (object v) => { rig.CWReverse = (bool)v; };
            combos.Add(CWReverseControl);

            // keyer speed
            KeyerSpeedControl.LowValue = FlexBase.KeyerSpeedMin;
            KeyerSpeedControl.HighValue = FlexBase.KeyerSpeedMax;
            KeyerSpeedControl.Increment = FlexBase.KeyerSpeedIncrement;
            KeyerSpeedControl.UpdateDisplayFunction =
                () => { return rig.KeyerSpeed; };
            KeyerSpeedControl.UpdateRigFunction =
                (int v) => { rig.KeyerSpeed = v; };
            numberBoxes.Add(KeyerSpeedControl);

            // Sidetone pitch
            SidetonePitchControl.LowValue = FlexBase.SidetonePitchMin;
            SidetonePitchControl.HighValue = FlexBase.SidetonePitchMax;
            SidetonePitchControl.Increment = FlexBase.SidetonePitchIncrement;
            SidetonePitchControl.UpdateDisplayFunction =
                () => { return rig.SidetonePitch; };
            SidetonePitchControl.UpdateRigFunction =
                (int v) => { rig.SidetonePitch = v; };
            numberBoxes.Add(SidetonePitchControl);

            // Sidetone volume
            SidetoneGainControl.LowValue = FlexBase.SidetoneGainMin;
            SidetoneGainControl.HighValue = FlexBase.SidetoneGainMax;
            SidetoneGainControl.Increment = FlexBase.SidetoneGainIncrement;
            SidetoneGainControl.UpdateDisplayFunction =
                () => { return rig.SidetoneGain; };
            SidetoneGainControl.UpdateRigFunction =
                (int v) => { rig.SidetoneGain = v; };
            numberBoxes.Add(SidetoneGainControl);

            // CWL
            CWLList = new ArrayList();
            CWLList.Add(new offOnElement(FlexBase.OffOnValues.off));
            CWLList.Add(new offOnElement(FlexBase.OffOnValues.on));
            CWLControl.TheList = CWLList;
            CWLControl.UpdateDisplayFunction =
                () => { return rig.CWL; };
            CWLControl.UpdateRigFunction =
                (object v) => { rig.CWL = (FlexBase.OffOnValues)v; };
            combos.Add(CWLControl);

            // Monitor pan
            MonitorPanControl.LowValue = FlexBase.MonitorPanMin;
            MonitorPanControl.HighValue = FlexBase.MonitorPanMax;
            MonitorPanControl.Increment = FlexBase.MonitorPanIncrement;
            MonitorPanControl.UpdateDisplayFunction =
                () => { return rig.MonitorPan; };
            MonitorPanControl.UpdateRigFunction =
                (int v) => { rig.MonitorPan = v; };
            numberBoxes.Add(MonitorPanControl);

            // Vox delay
            VoxDelayControl.LowValue = FlexBase.VoxDelayMin;
            VoxDelayControl.HighValue = FlexBase.VoxDelayMax;
            VoxDelayControl.Increment = FlexBase.VoxDelayIncrement;
            VoxDelayControl.UpdateDisplayFunction =
                () => { return rig.VoxDelay; };
            VoxDelayControl.UpdateRigFunction =
                (int v) => { rig.VoxDelay = v; };
            numberBoxes.Add(VoxDelayControl);

            // Vox gain
            VoxGainControl.LowValue = FlexBase.VoxGainMin;
            VoxGainControl.HighValue = FlexBase.VoxGainMax;
            VoxGainControl.Increment = FlexBase.VoxGainIncrement;
            VoxGainControl.UpdateDisplayFunction =
                () => { return rig.VoxGain; };
            VoxGainControl.UpdateRigFunction =
                (int v) => { rig.VoxGain = v; };
            numberBoxes.Add(VoxGainControl);

            MicGainControl.LowValue = FlexBase.MicGainMin;
            MicGainControl.HighValue = FlexBase.MicGainMax;
            MicGainControl.Increment = FlexBase.MicGainIncrement;
            MicGainControl.UpdateDisplayFunction =
                () => { return rig.MicGain; };
            MicGainControl.UpdateRigFunction =
                (int v) => { rig.MicGain = v; };
            numberBoxes.Add(MicGainControl);

            // speech processor
            processorOnList = new ArrayList();
            processorOnList.Add(new offOnElement(FlexBase.OffOnValues.off));
            processorOnList.Add(new offOnElement(FlexBase.OffOnValues.on));
            ProcessorOnControl.TheList = processorOnList;
            ProcessorOnControl.UpdateDisplayFunction =
                () => { return rig.ProcessorOn; };
            ProcessorOnControl.UpdateRigFunction =
                (object v) => { rig.ProcessorOn = (FlexBase.OffOnValues)v; };
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
                (object v) => { rig.ProcessorSetting = (FlexBase.ProcessorSettings)v; };
            combos.Add(ProcessorSettingControl);

            // Compander
            CompanderList = new ArrayList();
            CompanderList.Add(new offOnElement(FlexBase.OffOnValues.off));
            CompanderList.Add(new offOnElement(FlexBase.OffOnValues.on));
            CompanderControl.TheList = CompanderList;
            CompanderControl.UpdateDisplayFunction =
                () => { return rig.Compander; };
            CompanderControl.UpdateRigFunction =
                (object v) => { rig.Compander = (FlexBase.OffOnValues)v; };
            combos.Add(CompanderControl);

            CompanderLevelControl.LowValue = FlexBase.CompanderLevelMin;
            CompanderLevelControl.HighValue = FlexBase.CompanderLevelMax;
            CompanderLevelControl.Increment = FlexBase.CompanderLevelIncrement;
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
            micBoostList.Add(new offOnElement(FlexBase.OffOnValues.off));
            micBoostList.Add(new offOnElement(FlexBase.OffOnValues.on));
            MicBoostControl.TheList = micBoostList;
            MicBoostControl.UpdateDisplayFunction =
                () => { return rig.MicBoost; };
            MicBoostControl.UpdateRigFunction =
                (object v) => { rig.MicBoost = (FlexBase.OffOnValues)v; };
            combos.Add(MicBoostControl);

            // mic bias
            micBiasList = new ArrayList();
            micBiasList.Add(new offOnElement(FlexBase.OffOnValues.off));
            micBiasList.Add(new offOnElement(FlexBase.OffOnValues.on));
            MicBiasControl.TheList = micBiasList;
            MicBiasControl.UpdateDisplayFunction =
                () => { return rig.MicBias; };
            MicBiasControl.UpdateRigFunction =
                (object v) => { rig.MicBias = (FlexBase.OffOnValues)v; };
            combos.Add(MicBiasControl);

            // monitor
            monitorList = new ArrayList();
            monitorList.Add(new offOnElement(FlexBase.OffOnValues.off));
            monitorList.Add(new offOnElement(FlexBase.OffOnValues.on));
            MonitorControl.TheList = monitorList;
            MonitorControl.UpdateDisplayFunction =
                () => { return rig.Monitor; };
            MonitorControl.UpdateRigFunction =
                (object v) => { rig.Monitor = (FlexBase.OffOnValues)v; };
            combos.Add(MonitorControl);

            SBMonitorLevelControl.LowValue = FlexBase.SBMonitorLevelMin;
            SBMonitorLevelControl.HighValue = FlexBase.SBMonitorLevelMax;
            SBMonitorLevelControl.Increment = FlexBase.SBMonitorLevelIncrement;
            SBMonitorLevelControl.UpdateDisplayFunction =
                () => { return rig.SBMonitorLevel; };
            SBMonitorLevelControl.UpdateRigFunction =
                (int v) => { rig.SBMonitorLevel = v; };
            numberBoxes.Add(SBMonitorLevelControl);

            SBMonitorPanControl.LowValue = FlexBase.SBMonitorPanMin;
            SBMonitorPanControl.HighValue = FlexBase.SBMonitorPanMax;
            SBMonitorPanControl.Increment = FlexBase.SBMonitorPanIncrement;
            SBMonitorPanControl.UpdateDisplayFunction =
                () => { return rig.SBMonitorPan; };
            SBMonitorPanControl.UpdateRigFunction =
                (int v) => { rig.SBMonitorPan = v; };
            numberBoxes.Add(SBMonitorPanControl);

#if zero
            // TX antenna, 0 or 1, displayed as 1 or 2.
            AntControl.LowValue = 1;
            AntControl.HighValue = 2;
            AntControl.Increment = 1;
            AntControl.UpdateDisplayFunction =
                () => { return rig.TXAntenna + txAntennaDisplayOffset; };
            AntControl.UpdateRigFunction =
                (int v) => { rig.TXAntenna = v - txAntennaDisplayOffset; };
            numberBoxes.Add(AntControl);
#endif

            // RX antenna
            rxAntList = new ArrayList();
            if (!string.IsNullOrEmpty(FlexBase.RxAntDefault)) rxAntList.Add(FlexBase.RxAntDefault);
            if (FlexBase.await(() =>
                {
                    return (theRadio.RXAntList != null);
                }, 1000))
            {
                foreach (string s in theRadio.RXAntList)
                {
                    rxAntList.Add(s);
                }
            }
            else
            {
                Tracing.TraceLine("Flex6300Filters:no RXAntList", TraceLevel.Error);
            }
            RXAntControl.TheList = rxAntList;
            RXAntControl.UpdateDisplayFunction =
                () => { return rig.RXAntenna; };
            RXAntControl.UpdateRigFunction =
                (object v) => { rig.RXAntenna = (string)v; };
            combos.Add(RXAntControl);

            // Diversity
            diversityList = new ArrayList();
            diversityList.Add(new offOnElement(FlexBase.OffOnValues.off));
            diversityList.Add(new offOnElement(FlexBase.OffOnValues.on));
            DiversityControl.TheList = diversityList;
            DiversityControl.UpdateDisplayFunction =
                () => { return rig.DiversityOn ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off; };
            DiversityControl.UpdateRigFunction =
                (object v) => { rig.DiversityOn = ((FlexBase.OffOnValues)v) == FlexBase.OffOnValues.on; };
            combos.Add(DiversityControl);

            // TX antenna
            TxAntList = new ArrayList();
            if ((theRadio != null) && (theRadio.ActiveSlice != null))
            {
                foreach (string s in theRadio.ActiveSlice.TXAntList)
                {
                    TxAntList.Add(s);
                }
            }
            else
            {
                TxAntList.Add("ANT1");
                TxAntList.Add("ANT2");
                TxAntList.Add("XVTR");
            }
            TXAntControl.TheList = TxAntList;
            TXAntControl.UpdateDisplayFunction =
                () => { return rig.TXAntenna; };
            TXAntControl.UpdateRigFunction =
                (object v) => { rig.TXAntenna = (string)v; };
            combos.Add(TXAntControl);

            // Transmit power
            XmitPowerControl.LowValue = FlexBase.XmitPowerMin;
            XmitPowerControl.HighValue = FlexBase.XmitPowerMax;
            XmitPowerControl.Increment = FlexBase.XmitPowerIncrement;
            XmitPowerControl.UpdateDisplayFunction =
                () => { return rig.XmitPower; };
            XmitPowerControl.UpdateRigFunction =
                (int v) => { rig.XmitPower = v; };
            numberBoxes.Add(XmitPowerControl);

            // Tune power
            TunePowerControl.LowValue = FlexBase.TunePowerMin;
            TunePowerControl.HighValue = FlexBase.TunePowerMax;
            TunePowerControl.Increment = FlexBase.TunePowerIncrement;
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

            PATempBox.UpdateDisplayFunction =
                () => { return rig.PATemp.ToString("F1") + 'C'; };
            infoBoxes.Add(PATempBox);

            VoltsBox.UpdateDisplayFunction =
                () => { return rig.Volts.ToString("F1"); };
            infoBoxes.Add(VoltsBox);

            DiversityStatusControl.UpdateDisplayFunction =
                () => { return rig.DiversityStatus; };
            infoBoxes.Add(DiversityStatusControl);

            // FM tone or CTCSS mode.
            toneModeList = new ArrayList();
            foreach (FlexBase.ToneCTCSSValue t in rig.FMToneModes)
            {
                toneModeList.Add(new toneCTCSSElement(t));
            }
            ToneModeControl.TheList = toneModeList;
            ToneModeControl.UpdateDisplayFunction =
                () => { return rig.ToneCTCSS; };
            ToneModeControl.UpdateRigFunction =
                (object v) => { rig.ToneCTCSS = (FlexBase.ToneCTCSSValue)v; };
            combos.Add(ToneModeControl);

            // FM tone frequency
            toneFrequencyList = new ArrayList();
            foreach (float f in rig.ToneFrequencyTable)
            {
                toneFrequencyList.Add(new toneCTCSSFreqElement(f));
            }
            ToneFrequencyControl.TheList = toneFrequencyList;
            ToneFrequencyControl.UpdateDisplayFunction =
                () => { return rig.ToneFrequency; };
            ToneFrequencyControl.UpdateRigFunction =
                (object v) => { rig.ToneFrequency = (float)v; };
            combos.Add(ToneFrequencyControl);

            // squelch
            squelchList = new ArrayList();
            squelchList.Add(new offOnElement(FlexBase.OffOnValues.off));
            squelchList.Add(new offOnElement(FlexBase.OffOnValues.on));
            SquelchControl.TheList = squelchList;
            SquelchControl.UpdateDisplayFunction =
                () => { return rig.Squelch; };
            SquelchControl.UpdateRigFunction =
                (object v) => { rig.Squelch = (FlexBase.OffOnValues)v; };
            combos.Add(SquelchControl);

            // squelch level
            SquelchLevelControl.LowValue = FlexBase.SquelchLevelMin;
            SquelchLevelControl.HighValue = FlexBase.SquelchLevelMax;
            SquelchLevelControl.Increment = FlexBase.SquelchLevelIncrement;
            SquelchLevelControl.UpdateDisplayFunction =
                () => { return rig.SquelchLevel; };
            SquelchLevelControl.UpdateRigFunction =
                (int v) => { rig.SquelchLevel = v; };
            numberBoxes.Add(SquelchLevelControl);

            // offset
            offsetDirectionList = new ArrayList();
            foreach (offsetDirectionElement e in offsetDirectionValues)
            {
                offsetDirectionList.Add(e);
            }
            OffsetDirectionControl.TheList = offsetDirectionList;
            OffsetDirectionControl.UpdateDisplayFunction =
                () => { return rig.OffsetDirection; };
            OffsetDirectionControl.UpdateRigFunction =
                (object v) => { rig.OffsetDirection = (FlexBase.OffsetDirections)v; };
            combos.Add(OffsetDirectionControl);

            OffsetControl.LowValue = FlexBase.offsetMin;
            OffsetControl.HighValue = FlexBase.offsetMax;
            OffsetControl.Increment = FlexBase.offsetIncrement;
            OffsetControl.UpdateDisplayFunction =
                () => { return rig.OffsetFrequency; };
            OffsetControl.UpdateRigFunction =
                (int v) => { rig.OffsetFrequency = v; };
            numberBoxes.Add(OffsetControl);

            // emphasis
            emphasisList = new ArrayList();
            emphasisList.Add(new offOnElement(FlexBase.OffOnValues.off));
            emphasisList.Add(new offOnElement(FlexBase.OffOnValues.on));
            EmphasisControl.TheList = emphasisList;
            EmphasisControl.UpdateDisplayFunction =
                () => { return rig.FMEmphasis; };
            EmphasisControl.UpdateRigFunction =
                (object v) => { rig.FMEmphasis = (FlexBase.OffOnValues)v; };
            combos.Add(EmphasisControl);

            // 1750 offset
            FM1750List = new ArrayList();
            FM1750List.Add(new offOnElement(FlexBase.OffOnValues.off));
            FM1750List.Add(new offOnElement(FlexBase.OffOnValues.on));
            FM1750Control.TheList = FM1750List;
            FM1750Control.UpdateDisplayFunction =
                () => { return rig.FM1750; };
            FM1750Control.UpdateRigFunction =
                (object v) => { rig.FM1750 = (FlexBase.OffOnValues)v; };
            combos.Add(FM1750Control);

            // AM carrier level
            AMCarrierLevelControl.LowValue = FlexBase.AMCarrierLevelMin;
            AMCarrierLevelControl.HighValue = FlexBase.AMCarrierLevelMax;
            AMCarrierLevelControl.Increment = FlexBase.AMCarrierLevelIncrement;
            AMCarrierLevelControl.UpdateDisplayFunction =
                () => { return rig.AMCarrierLevel; };
            AMCarrierLevelControl.UpdateRigFunction =
                (int v) => { rig.AMCarrierLevel = v; };
            numberBoxes.Add(AMCarrierLevelControl);

            // binaural rx
            binauralList = new ArrayList();
            binauralList.Add(new offOnElement(FlexBase.OffOnValues.off));
            binauralList.Add(new offOnElement(FlexBase.OffOnValues.on));
            BinauralControl.TheList = binauralList;
            BinauralControl.UpdateDisplayFunction =
                () => { return rig.Binaural; };
            BinauralControl.UpdateRigFunction =
                (object v) => { rig.Binaural = (FlexBase.OffOnValues)v; };
            combos.Add(BinauralControl);

            // 1750 offset
            playList = new ArrayList();
            playList.Add(new offOnElement(FlexBase.OffOnValues.off));
            playList.Add(new offOnElement(FlexBase.OffOnValues.on));
            PlayControl.TheList = playList;
            PlayControl.UpdateDisplayFunction =
                () => { return rig.Play; };
            PlayControl.UpdateRigFunction =
                (object v) => { rig.Play = (FlexBase.OffOnValues)v; };
            combos.Add(PlayControl);
            // Only enabled if play allowed.
            specials.Add(() =>
            {
                bool sw = rig.CanPlay;
                if (PlayControl.Enabled != sw) PlayControl.Enabled = sw;
            });

            // Record control
            recordList = new ArrayList();
            recordList.Add(new offOnElement(FlexBase.OffOnValues.off));
            recordList.Add(new offOnElement(FlexBase.OffOnValues.on));
            RecordControl.TheList = recordList;
            RecordControl.UpdateDisplayFunction =
                () => { return rig.Record; };
            RecordControl.UpdateRigFunction =
                (object v) => { rig.Record = (FlexBase.OffOnValues)v; };
            combos.Add(RecordControl);

            // DAX transmit control
            daxTXList = new ArrayList();
            daxTXList.Add(new offOnElement(FlexBase.OffOnValues.off));
            daxTXList.Add(new offOnElement(FlexBase.OffOnValues.on));
            DAXTXControl.TheList = daxTXList;
            DAXTXControl.UpdateDisplayFunction =
                () => { return rig.DAXOnOff; };
            DAXTXControl.UpdateRigFunction =
                (object v) => { rig.DAXOnOff = (FlexBase.OffOnValues)v; };
            combos.Add(DAXTXControl);

            // auto mode change
            foreach (string s in autoprocValues)
            {
                AutoprocControl.Items.Add(s);
            }
            // Set configured value later.
            comboBoxes.Add(AutoprocControl);
            modeChangeSpecials.Add(autoprocFunc);

            // Setup panning
            panControlSetup();

            // Other controls
            int myControlsCount = combos.Count + comboBoxes.Count + numberBoxes.Count + infoBoxes.Count + panControls.Count;
            int loopCount = myControlsCount; // for the below loop.
            // loopCount is also the start of the buttons.

            // buttons
            buttonControls = new Collection<Button>();
            buttonControls.Add(TNFButton);
            buttonControls.Add(TNFEnableButton);
            if (ExportButton.Enabled)
            {
                buttonControls.Add(ExportButton);
            }
            if (ImportButton.Enabled)
            {
                buttonControls.Add(ImportButton);
            }
            buttonControls.Add(RXEqButton);
            buttonControls.Add(TXEqButton);
            buttonControls.Add(InfoButton);
            buttonControls.Add(EscButton);
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

            rig.Callouts.PanField = PanBox;
            Form memdisp = new FlexMemories(rig);
            rig.RigFields = new FlexBase.RigFields_t(this, updateBoxes, memdisp, null, myControls);

            // Set routine to get SWR text.
            rig.Callouts.GetSWRText = SWRText;
        }

        private void Filters_Load(object sender, EventArgs e)
        {
            Tracing.TraceLine("Flex6300Filters_load", TraceLevel.Info);
            setupNoiseReductionMenu();
            setupFiltersMenuStrip();
            applyLicenseGating();
            applyDiversityGating();
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

        // Operator change
        public void OperatorChangeHandler()
        {
            getConfig();
            panRanges = new PanRanges(rig, OperatorsDirectory);
            RXFreqChange(rig.VFOToSlice(rig.RXVFO));
        }

        private void updateBoxes()
        {
            Tracing.TraceLine("updateBoxes", TraceLevel.Verbose);
            if (string.IsNullOrEmpty(rig.Mode))
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

                applyLicenseGating();
                applyDiversityGating();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("updateBoxes exception:" + ex.Message + ex.StackTrace, TraceLevel.Error);
            }
        }

        private void setGate(Control c, bool enabled, bool readOnly, string tooltip)
        {
            if (c == null) return;

            if (c.Enabled != enabled) c.Enabled = enabled;

            if (c is RadioBoxes.Combo combo)
            {
                if (combo.ReadOnly != readOnly) combo.ReadOnly = readOnly;
            }
            else if (c is RadioBoxes.NumberBox numberBox)
            {
                if (numberBox.ReadOnly != readOnly) numberBox.ReadOnly = readOnly;
            }

            if (licenseToolTip != null)
            {
                var normalizedTooltip = string.IsNullOrEmpty(tooltip) ? string.Empty : tooltip;
                var currentTip = licenseToolTip.GetToolTip(c) ?? string.Empty;
                if (currentTip != normalizedTooltip)
                {
                    licenseToolTip.SetToolTip(c, normalizedTooltip);
                }
            }

            if (!string.IsNullOrEmpty(tooltip))
            {
                c.Tag = string.IsNullOrEmpty(c.Tag as string) ? tooltip : (string)c.Tag;
            }
        }

        private void setTooltip(Control c, string tooltip)
        {
            if (c == null || licenseToolTip == null) return;
            var normalizedTooltip = string.IsNullOrEmpty(tooltip) ? string.Empty : tooltip;
            var currentTip = licenseToolTip.GetToolTip(c) ?? string.Empty;
            if (currentTip != normalizedTooltip)
            {
                licenseToolTip.SetToolTip(c, normalizedTooltip);
            }
        }

        private class NrLicenseGate
        {
            public bool HasRadio;
            public bool HasSlice;
            public bool LicenseReported;
            public bool LicenseEnabled;
            public bool ReadOnly;
            public bool Enabled;
            public string Tooltip;
        }

        private NrLicenseGate GetNoiseReductionGate()
        {
            var gate = new NrLicenseGate();
            var radio = theRadio;
            gate.HasRadio = (rig != null) && (radio != null);
            gate.HasSlice = gate.HasRadio && (radio.ActiveSlice != null);
            var licenseFeature = radio?.FeatureLicense?.LicenseFeatNoiseReduction;
            gate.LicenseReported = (licenseFeature != null);
            gate.LicenseEnabled = licenseFeature?.FeatureEnabled == true;
            gate.ReadOnly = gate.LicenseReported && !gate.LicenseEnabled;
            gate.Enabled = gate.HasRadio && gate.HasSlice && gate.LicenseEnabled;

            if (gate.LicenseReported && !gate.LicenseEnabled)
            {
                gate.Tooltip = licenseFeature.FeatureGatedMessage ?? "Purchase a noise reduction license to enable this control.";
            }
            else if (gate.HasRadio && !gate.LicenseReported)
            {
                gate.ReadOnly = true;
                gate.Enabled = false;
            }

            return gate;
        }

        private void applyLicenseGating()
        {
            if (rig == null) return;

            var gate = GetNoiseReductionGate();

            bool noiseChanged = !licenseGateInitialized ||
                (gate.HasSlice != lastHasSlice) ||
                (gate.LicenseReported != lastLicenseReported) ||
                (gate.LicenseEnabled != lastLicenseEnabled) ||
                (gate.ReadOnly != lastReadOnly) ||
                !string.Equals(gate.Tooltip, lastTooltip, StringComparison.Ordinal);

            if (!noiseChanged)
            {
                return;
            }

            Control[] gateControls = {
                ANFControl,
                ANFLevelControl,
                NoiseReductionControl,
                NoiseReductionLevelControl,
            };

            foreach (Control ctrl in gateControls)
            {
                setGate(ctrl, gate.Enabled, gate.ReadOnly, gate.Tooltip);
            }

            gateNoiseMenus(gate);

            lastHasSlice = gate.HasSlice;
            lastLicenseReported = gate.LicenseReported;
            lastLicenseEnabled = gate.LicenseEnabled;
            lastReadOnly = gate.ReadOnly;
            lastTooltip = gate.Tooltip;
            licenseGateInitialized = true;

            gateCwMenus();
        }

        private void applyDiversityGating()
        {
            if (DiversityControl == null && DiversityStatusControl == null && EscButton == null) return;

            string gateMessage = "Radio not ready";
            bool ready = false;

            if (rig != null)
            {
                ready = rig.DiversityReady;
                gateMessage = rig.DiversityGateMessage ?? string.Empty;
            }

            bool stateChanged = (ready != lastDiversityReady) ||
                !string.Equals(gateMessage, lastDiversityGateMessage, StringComparison.Ordinal);

            if (!stateChanged && !ready)
            {
                // Still update status text for stale display.
                if (DiversityStatusControl != null)
                {
                    DiversityStatusControl.Text = rig != null ? rig.DiversityStatus : gateMessage;
                }
                return;
            }

            bool enableControls = ready;

            setGate(DiversityControl, enableControls, !enableControls, gateMessage);
            if (EscButton != null)
            {
                if (EscButton.Enabled != enableControls) EscButton.Enabled = enableControls;
                setTooltip(EscButton, gateMessage);
            }

            if (DiversityStatusControl != null)
            {
                if (DiversityStatusControl.Enabled != enableControls) DiversityStatusControl.Enabled = enableControls;
                setTooltip(DiversityStatusControl, gateMessage);
                if (rig != null)
                {
                    DiversityStatusControl.UpdateDisplay(true);
                }
                else
                {
                    DiversityStatusControl.Text = gateMessage;
                }
            }

            lastDiversityReady = ready;
            lastDiversityGateMessage = gateMessage;
        }

        private void gateNoiseMenus(NrLicenseGate gate)
        {
            // Ensure menu items reflect license gating even if the drop-down isn't opened.
            if (nrRoot != null)
            {
                gateMenuItem(rnnItem, gate);
                gateMenuItem(spectralItem, gate);
                gateMenuItem(legacyNRItem, gate);
                gateMenuItem(anfFftItem, gate);
                gateMenuItem(anfLegacyItem, gate);
            }

            if (filtersRoot != null)
            {
                gateMenuItem(filtersNoiseRoot, gate);
                gateMenuItem(filtersNRRoot, gate);
                gateMenuItem(filtersANFRoot, gate);
            }
        }

        private void gateMenuItem(ToolStripMenuItem item, NrLicenseGate gate)
        {
            if (item == null) return;

            bool newEnabled = gate.Enabled;
            if (item.Enabled != newEnabled) item.Enabled = newEnabled;

            string tip = gate.Tooltip ?? string.Empty;
            if (!string.Equals(item.ToolTipText ?? string.Empty, tip, StringComparison.Ordinal))
            {
                item.ToolTipText = tip;
            }
        }

        private void Rig_FeatureLicenseChanged(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() =>
                {
                    applyLicenseGating();
                    applyDiversityGating();
                }));
                return;
            }

            applyLicenseGating();
            applyDiversityGating();
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

            private string oldMode = null;
            public void enableDisable(string mode)
            {
                // Just quit if the mode hasn't changed.
                if ((!string.IsNullOrEmpty(oldMode)) && (mode == oldMode)) return;
                oldMode = mode;
                // enables holds the controls to be enabled.  It may be null.
                controlsClass enables = (modeControls.Keys.Contains(mode)) ? modeControls[mode] : null;
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
            rv.Add("LSB", new modeChangeClass.controlsClass(
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
            rv.Add("USB", new modeChangeClass.controlsClass(
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
            rv.Add("CW", new modeChangeClass.controlsClass(
                new Control[] {
                        APFControl, APFLevelControl, // CW only
                        BreakinDelayControl, KeyerControl, CWReverseControl,
                        KeyerSpeedControl, SidetonePitchControl,
                        SidetoneGainControl, MonitorPanControl,
                        CWLControl,
                        TNFButton, TNFEnableButton,
                    }));
            rv.Add("FM", new modeChangeClass.controlsClass(
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
                        OffsetDirectionControl, OffsetControl, EmphasisControl,
                        FM1750Control,
                        RXEqButton, TXEqButton,
                        DAXTXControl,
                    }));
            rv.Add("AM", new modeChangeClass.controlsClass(
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
            rv.Add("DIGL", new modeChangeClass.controlsClass(
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
            rv.Add("DIGU", new modeChangeClass.controlsClass(
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
            rv.Add("NFM", new modeChangeClass.controlsClass(
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
                        OffsetDirectionControl, OffsetControl, EmphasisControl,
                        FM1750Control,
                        RXEqButton, TXEqButton,
                        DAXTXControl,
                    }));
            rv.Add("DFM", new modeChangeClass.controlsClass(
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
                        OffsetDirectionControl, OffsetControl, EmphasisControl,
                        FM1750Control,
                        RXEqButton, TXEqButton,
                        DAXTXControl,
                    }));
            rv.Add("SAM", new modeChangeClass.controlsClass(
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
            if (rig != null)
            {
                rig.FeatureLicenseChanged -= Rig_FeatureLicenseChanged;
                licenseGateInitialized = false;
            }
            flexTNF.Dispose();
        }

        private void TNFButton_Click(object sender, EventArgs e)
        {
            flexTNF.ShowDialog();
        }

        // Noise Reduction context menu (checkable items, multiple selection)
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
            rnnItem.CheckedChanged += (s, e) => { try { rig.NeuralNoiseReduction = rnnItem.Checked ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off; updateBoxes(); } catch { } };
            spectralItem.CheckedChanged += (s, e) => { try { rig.SpectralNoiseReduction = spectralItem.Checked ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off; updateBoxes(); } catch { } };
            legacyNRItem.CheckedChanged += (s, e) => { try { rig.NoiseReductionLegacy = legacyNRItem.Checked ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off; updateBoxes(); } catch { } };
            anfFftItem.CheckedChanged += (s, e) => { try { rig.AutoNotchFFT = anfFftItem.Checked ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off; updateBoxes(); } catch { } };
            anfLegacyItem.CheckedChanged += (s, e) => { try { rig.AutoNotchLegacy = anfLegacyItem.Checked ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off; updateBoxes(); } catch { } };
            rnnItem.CheckedChanged += (s, e) => updateMenuAccessibility(rnnItem);
            spectralItem.CheckedChanged += (s, e) => updateMenuAccessibility(spectralItem);
            legacyNRItem.CheckedChanged += (s, e) => updateMenuAccessibility(legacyNRItem);
            anfFftItem.CheckedChanged += (s, e) => updateMenuAccessibility(anfFftItem);
            anfLegacyItem.CheckedChanged += (s, e) => updateMenuAccessibility(anfLegacyItem);

            // Attach to the Filters user control (right-click)
            this.ContextMenuStrip = nrMenu;
        }

        private void nrMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                var gate = GetNoiseReductionGate();

                // Reflect current state
                rnnItem.Checked = rig.NeuralNoiseReduction == FlexBase.OffOnValues.on;
                spectralItem.Checked = rig.SpectralNoiseReduction == FlexBase.OffOnValues.on;
                legacyNRItem.Checked = rig.NoiseReductionLegacy == FlexBase.OffOnValues.on;
                anfFftItem.Checked = rig.AutoNotchFFT == FlexBase.OffOnValues.on;
                anfLegacyItem.Checked = rig.AutoNotchLegacy == FlexBase.OffOnValues.on;

                string mode = (rig.Mode ?? string.Empty).ToLowerInvariant();
                bool cwOrFm = mode.StartsWith("cw") || mode.Contains("fm");
                bool fmMode = mode.Contains("fm");

                applyMenuGate(rnnItem, !cwOrFm, gate);
                applyMenuGate(spectralItem, !cwOrFm, gate);
                applyMenuGate(legacyNRItem, !cwOrFm, gate);
                applyMenuGate(anfFftItem, !fmMode, gate);
                applyMenuGate(anfLegacyItem, !fmMode, gate);

                updateMenuAccessibility(rnnItem);
                updateMenuAccessibility(spectralItem);
                updateMenuAccessibility(legacyNRItem);
                updateMenuAccessibility(anfFftItem);
                updateMenuAccessibility(anfLegacyItem);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("NR menu opening error: " + ex.Message, TraceLevel.Error);
            }
        }

            // Top-level Filters menu with Noise Reduction submenu (mirrors context menu)
        private void setupFiltersMenuStrip()
        {
            filtersMenuStrip = new MenuStrip();
            filtersRoot = new ToolStripMenuItem("Filters");
            filtersNoiseRoot = new ToolStripMenuItem("Noise and Mitigation");

            filtersNRRoot = new ToolStripMenuItem("Noise Reduction");
            var rnn = new ToolStripMenuItem("Neural (RNN)") { CheckOnClick = true };
            var spectral = new ToolStripMenuItem("Spectral (NRF/NRS)") { CheckOnClick = true };
            var legacy = new ToolStripMenuItem("Legacy (NRL)") { CheckOnClick = true };
            filtersNRRoot.DropDownItems.AddRange(new ToolStripItem[] { rnn, spectral, legacy });

            filtersANFRoot = new ToolStripMenuItem("Auto Notch");
            var anfFft = new ToolStripMenuItem("FFT (ANFT)") { CheckOnClick = true };
            var anfLegacy = new ToolStripMenuItem("Legacy (ANFL)") { CheckOnClick = true };
            filtersANFRoot.DropDownItems.AddRange(new ToolStripItem[] { anfFft, anfLegacy });

            rnn.CheckedChanged += (s, e) => { try { rig.NeuralNoiseReduction = rnn.Checked ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off; updateBoxes(); } catch { } };
            spectral.CheckedChanged += (s, e) => { try { rig.SpectralNoiseReduction = spectral.Checked ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off; updateBoxes(); } catch { } };
            legacy.CheckedChanged += (s, e) => { try { rig.NoiseReductionLegacy = legacy.Checked ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off; updateBoxes(); } catch { } };
            anfFft.CheckedChanged += (s, e) => { try { rig.AutoNotchFFT = anfFft.Checked ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off; updateBoxes(); } catch { } };
            anfLegacy.CheckedChanged += (s, e) => { try { rig.AutoNotchLegacy = anfLegacy.Checked ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off; updateBoxes(); } catch { } };
            rnn.CheckedChanged += (s, e) => updateMenuAccessibility(rnn);
            spectral.CheckedChanged += (s, e) => updateMenuAccessibility(spectral);
            legacy.CheckedChanged += (s, e) => updateMenuAccessibility(legacy);
            anfFft.CheckedChanged += (s, e) => updateMenuAccessibility(anfFft);
            anfLegacy.CheckedChanged += (s, e) => updateMenuAccessibility(anfLegacy);

            filtersNRRoot.DropDownOpening += (s, e) => syncAndGateNRItems(rnn, spectral, legacy);
            filtersANFRoot.DropDownOpening += (s, e) => syncAndGateANFItems(anfFft, anfLegacy);

            filtersNoiseRoot.DropDownItems.Add(filtersNRRoot);
            filtersNoiseRoot.DropDownItems.Add(filtersANFRoot);

            // CW submenu
            filtersCWRoot = new ToolStripMenuItem("CW");
            filtersCWAutotuneItem = new ToolStripMenuItem("Autotune");
            filtersCWAutotuneItem.Click += (s, e) =>
            {
                try
                {
                    rig.CWAutotune();
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("CW Autotune error: " + ex.Message, TraceLevel.Error);
                }
            };
            filtersCWRoot.DropDownItems.Add(filtersCWAutotuneItem);

            filtersRoot.DropDownItems.Add(filtersNoiseRoot);
            filtersRoot.DropDownItems.Add(filtersCWRoot);
            filtersMenuStrip.Items.Add(filtersRoot);

            updateMenuAccessibility(filtersRoot);
            updateMenuAccessibility(filtersNoiseRoot);
            updateMenuAccessibility(filtersNRRoot);
            updateMenuAccessibility(filtersANFRoot);
            updateMenuAccessibility(filtersCWRoot);

            // Add to control
            this.Controls.Add(filtersMenuStrip);
            filtersMenuStrip.Dock = DockStyle.Top;
        }

        private void syncAndGateNRItems(ToolStripMenuItem rnn, ToolStripMenuItem spectral, ToolStripMenuItem legacy)
        {
            try
            {
                var gate = GetNoiseReductionGate();

                rnn.Checked = rig.NeuralNoiseReduction == FlexBase.OffOnValues.on;
                spectral.Checked = rig.SpectralNoiseReduction == FlexBase.OffOnValues.on;
                legacy.Checked = rig.NoiseReductionLegacy == FlexBase.OffOnValues.on;

                string mode = (rig.Mode ?? string.Empty).ToLowerInvariant();
                bool cwOrFm = mode.StartsWith("cw") || mode.Contains("fm");

                applyMenuGate(rnn, !cwOrFm, gate);
                applyMenuGate(spectral, !cwOrFm, gate);
                applyMenuGate(legacy, !cwOrFm, gate);

                updateMenuAccessibility(rnn);
                updateMenuAccessibility(spectral);
                updateMenuAccessibility(legacy);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Filters NR menu sync error: " + ex.Message, TraceLevel.Error);
            }
        }

        private void syncAndGateANFItems(ToolStripMenuItem anfFft, ToolStripMenuItem anfLegacy)
        {
            try
            {
                var gate = GetNoiseReductionGate();

                anfFft.Checked = rig.AutoNotchFFT == FlexBase.OffOnValues.on;
                anfLegacy.Checked = rig.AutoNotchLegacy == FlexBase.OffOnValues.on;

                string mode = (rig.Mode ?? string.Empty).ToLowerInvariant();
                bool fmMode = mode.Contains("fm");

                applyMenuGate(anfFft, !fmMode, gate);
                applyMenuGate(anfLegacy, !fmMode, gate);

                updateMenuAccessibility(anfFft);
                updateMenuAccessibility(anfLegacy);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Filters ANF menu sync error: " + ex.Message, TraceLevel.Error);
            }
        }

        private void applyMenuGate(ToolStripMenuItem item, bool modeAllows, NrLicenseGate gate)
        {
            if (item == null) return;

            bool enabled = gate.Enabled && modeAllows;
            if (item.Enabled != enabled) item.Enabled = enabled;

            string tip = gate.Tooltip ?? string.Empty;
            if (!string.Equals(item.ToolTipText ?? string.Empty, tip, StringComparison.Ordinal))
            {
                item.ToolTipText = tip;
            }
            updateMenuAccessibility(item);
        }

        private void gateCwMenus()
        {
            if (filtersCWRoot == null) return;

            bool hasSlice = rig?.HasActiveSlice == true;
            string mode = (rig?.Mode ?? string.Empty).ToLowerInvariant();
            bool cwMode = mode.StartsWith("cw");
            bool cap = rig?.SupportsCwAutotune == true;

            bool enabled = hasSlice && cwMode && cap;
            string tip;
            if (!cap)
            {
                tip = "CW autotune not supported on this radio.";
            }
            else if (!cwMode)
            {
                tip = "Switch to CW to use autotune.";
            }
            else if (!hasSlice)
            {
                tip = "No active slice.";
            }
            else tip = string.Empty;

            setMenuState(filtersCWRoot, enabled, tip);
            setMenuState(filtersCWAutotuneItem, enabled, tip);
        }

        private void setMenuState(ToolStripMenuItem item, bool enabled, string tooltip)
        {
            if (item == null) return;
            if (item.Enabled != enabled) item.Enabled = enabled;
            tooltip = tooltip ?? string.Empty;
            if (!string.Equals(item.ToolTipText ?? string.Empty, tooltip, StringComparison.Ordinal))
            {
                item.ToolTipText = tooltip;
            }
            updateMenuAccessibility(item);
        }

        private void TNFEnableButton_Click(object sender, EventArgs e)
        {
            bool newState = !rig.TNF;
            rig.TNF = newState;
            TNFEnableText(newState);
        }

        private void EscButton_Click(object sender, EventArgs e)
        {
            ShowEscDialog();
        }

        public void ShowEscDialog()
        {
            if (rig == null) return;
            using (var esc = new EscDialog(rig))
            {
                esc.ShowDialog();
            }
        }

        public void ToggleDiversity()
        {
            if (rig == null) return;
            rig.DiversityOn = !rig.DiversityOn;
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
            if (flexDB == null) flexDB = new FlexDB(rig);
            flexDB.Export();
        }

        private void ImportButton_Click(object sender, EventArgs e)
        {
            if (flexDB == null) flexDB = new FlexDB((FlexBase)rig);
            flexDB.Import();
        }

        public new void Close()
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
            Form eqForm = new FlexEq((FlexBase)rig, eq);
            eqForm.ShowDialog();
            eqForm.Dispose();
        }

        private void TXEqButton_Click(object sender, EventArgs e)
        {
            Tracing.TraceLine("TXEq button", TraceLevel.Info);
            Equalizer eq = getEq(EqualizerSelect.TX);
            if (eq == null) return;

            // Bring up the form.
            Form eqForm = new FlexEq(rig, eq);
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
            Form theForm = new FlexInfo(rig);
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
        }

        // Called when ready to handle the pan adapter.
        internal bool PanReady = false;
        public void PanSetup()
        {
            Tracing.TraceLine("panSetup", TraceLevel.Info);

            // Other setup
            flexTNF = new FlexTNF(rig);
            TNFEnableText(); // Sets the button text
            TextOut.PerformGenericFunction(AutoprocControl, () => { AutoprocControl.Text = OpsConfigInfo.AutoProc; });

            ((FlexMemories)rig.RigFields.Memories).FlexMemories_Setup();

            brailleWidth = rig.Callouts.BrailleCells;
            // If no cells, there's no pan adapter.
            if (brailleWidth <= 0)
            {
                brailleWidth = brailleWidthDefault;
                Tracing.TraceLine("panSetup:no braille cells given, using " + brailleWidth, TraceLevel.Error);
            }

            panRanges = new PanRanges(rig, OperatorsDirectory);
            currentPanData = new PanData(brailleWidth);
            PanReady = true;
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
        private ulong stepSize;

        private object segmentLock = new object();
        private PanRanges.PanRange segment = null;
        private uint generation = 0;
        private FlexWaterfall flexPan;

        // Called when the active slice, or active slice's mode or freq changes.
        // Also called to copy a panadapter/waterfall.
        public void RXFreqChange(object o)
        {
            // get out if pan adapter isn't ready.
            if (!PanReady) return;

            // See if it's a copy.
            if (o is List<Slice>)
            {
                // First parm is the input.
                List<Slice> sList = (List<Slice>)o;
                copyPan(sList[0].Panadapter, sList[1].Panadapter);
                return;
            }

            Slice s = (Slice)o;
            // This should always be the active slice.
            if ((s == null) || !s.Active) return;

            //Tracing.TraceLine("RXFreqChange:" + s.Freq.ToString(), TraceLevel.Info);
            iRXFreqChange(s.Freq);
        }

        internal void iRXFreqChange(double f)
        {
            ulong freq = rig.LibFreqtoLong(f);
            Tracing.TraceLine("iRXFreqChange:" + freq.ToString(), TraceLevel.Info);
            FlexBase.DbgTrace("segmentLock1");
            lock (segmentLock)
            {
                // Return if a user segment.
                if ((segment != null) && segment.User)
                {
                    Tracing.TraceLine("iRXFreqChange:user segment", TraceLevel.Info);
                    return;
                }

                Tracing.TraceLine("iRXFreqChange:current freq:" + rig.RXFrequency.ToString(), TraceLevel.Info);

                PanRanges.PanRange seg = panRanges.Query(freq);
                if (seg == null)
                {
                    Tracing.TraceLine("iRXFreqChange:segment not found", TraceLevel.Info);
                    // make this brailleWidth KHZ wide.
                    ulong low = freq - (((ulong)brailleWidth * 1000) / 2);
                    seg = new PanRanges.PanRange(low, low + ((ulong)brailleWidth * 1000), PanRanges.PanRangeStates.temp);
                }
                FlexBase.DbgTrace("dbg1:" + seg.ToString());

                //if (seg != segment)
                if ((segment == null) || ((seg.Low != segment.Low) | (seg.High != segment.High)))
                {
                    segment = seg;
                    invalidateOldSegment();
                    Tracing.TraceLine("iRXFreqChange:new segment:" + generation + ' ' + segment.ToString(), TraceLevel.Info);
                    // start panning.
                    panParameterSetup();
                }
            }
            FlexBase.DbgTrace("segmentLock1 done");
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

                // Remove all panadapter and waterfall handlers.
                for (int i = 0; i < rig.MyNumSlices; i++)
                {
                    if (rig.mySlices[i].Panadapter != null)
                    {
                        rig.mySlices[i].Panadapter.DataReady -= panDataHandler;
                    }
                }
                lock (rig.waterfallList)
                {
                    foreach (Waterfall w in rig.waterfallList)
                    {
                        w.DataReady -= waterfallDataHandler;
                    }
                }

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

                stepSize = (ulong)((float)segment.Width / (float)brailleWidth); // hz / cell
                if (stepSize == 0) stepSize = 1;

                panadapter.Width = (brailleWidth * brailleScaleup) + brailleWidth;
                //waterfall.Width = (brailleWidth * brailleScaleup) + brailleWidth;
                panadapter.Height = 700;
                panadapter.FPS = fps;
                panadapter.CenterFreq = segmentCenter(segment);
                waterfall.CenterFreq = panadapter.CenterFreq;
                FlexBase.DbgTrace("dbg setup:" + waterfall.CenterFreq.ToString());
                panadapter.Bandwidth = rig.LongFreqToLibFreq((ulong)segment.Width + stepSize);
                waterfall.Bandwidth = panadapter.Bandwidth;
                panadapter.LowDbm = lowDBM;
                panadapter.HighDbm = highDBM;
                flexPan = new FlexWaterfall(this, segment.Low, segment.High, rig.Callouts.BrailleCells);
                panadapter.DataReady += panDataHandler;
                waterfall.DataReady += waterfallDataHandler;
            }
            catch (Exception ex)
            {
                // Can happen if active slice changes.
                Tracing.TraceLine("panParameterSetup exception" + ex.Message + ex.StackTrace, TraceLevel.Error);
            }
        }

        private void copyPan(Panadapter inPan, Panadapter outPan)
        {
            Tracing.TraceLine("copyPan", TraceLevel.Info);
            Waterfall inFall = rig.GetPanadaptersWaterfall(inPan);
            Waterfall outFall = rig.GetPanadaptersWaterfall(outPan);
            rig.q.Enqueue((FlexBase.FunctionDel)null, "copyPan start");
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.Width = inPan.Width; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.Height = inPan.Height; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.FPS = inPan.FPS; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.CenterFreq = inPan.CenterFreq; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outFall.CenterFreq = inFall.CenterFreq; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.Bandwidth = outPan.Bandwidth; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outFall.Bandwidth = inFall.Bandwidth; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.LowDbm = inPan.LowDbm; }));
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { outPan.HighDbm = inPan.HighDbm; }));
            rig.q.Enqueue((FlexBase.FunctionDel)null, "copyPan done");
        }

        private void invalidateOldSegment()
        {
            segment.Generation = ++generation;
        }

        private double segmentCenter(PanRanges.PanRange segment)
        {
            return rig.LongFreqToLibFreq(segment.Low + (ulong)(segment.Width / 2));
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
                int rv = (int)(relFreq / HZPerCell);
                if (rv == Cells) rv--;
                return rv;
            }
            public double CellToFreq(int c)
            {
                if (c < 0) c = 0;
                else if (c > Cells - 1) c = Cells - 1;
                return LowFreq + (c * HZPerCell);
            }
        }
        private PanData currentPanData;

        private ushort[] oldPanData;
        private DateTime lastPanTime = DateTime.Now;
        private TimeSpan panInterval = new TimeSpan(10000 * 400 / fps); // 0.4 sec / fps
        private bool rapidReturn = false;
        private uint rapidStreamID;
        private int rapidFPS;
        private void panDataHandler(Panadapter pan, ushort[] data)
        {
            if (rig.Disconnecting | (flexPan == null))
            {
                return;
            }

            // Prevent rapid calls.
            TimeSpan delta = DateTime.Now - lastPanTime;
            if ((delta < panInterval) & (pan.FPS != fps))
            {
                rapidReturn = true;
                rapidFPS = pan.FPS;
                rapidStreamID = pan.StreamID;
                // FPS change.
                Tracing.TraceLine("panDataHandler:changing FPS:" + pan.FPS + ' ' + fps + ' ' + delta.ToString(), TraceLevel.Info);
                //rig.q.Enqueue((FlexBase.FunctionDel)(() => { panadapter.FPS = fps; }), "panadapter.FPS");
                panadapter.FPS = fps;
                return; // too rapid
            }
            else
            {
                bool diff = ((oldPanData == null) || (oldPanData.Length != data.Length));
                if (!diff)
                {
                    for (int i = 0; i < data.Length; i++)
                    {
                        if (data[i] != oldPanData[i])
                        {
                            oldPanData = data;
                            diff = true;
                            break;
                        }
                    }
                }
            }

            lastPanTime = DateTime.Now;
            if (rapidReturn)
            {
                rapidReturn = false;
                Tracing.TraceLine("panDataHandler:rapid calls:" +rapidStreamID + ' ' + rapidFPS, TraceLevel.Error);
            }

            try
            {
                //Tracing.TraceLine("panDataHandler:" + pan.StreamID + ' ' + pan.FPS, TraceLevel.Info);
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
                             if (!rig.Disconnecting)
                             {
                                 PanBox.Text = panOut.Line;
                                 PanBox.SelectionStart = pos;
                             }
                         });
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("panDataHandler exception:" + ex.Message, TraceLevel.Error);
            }
        }
        private bool centerChangeSent = false;
        private void waterfallDataHandler(Waterfall w, WaterfallTile tile)
        {
            if (rig.Disconnecting | (flexPan == null))
            {
                FlexBase.DbgTrace("waterfallDataHandler:disconnecting or flexPan is null");
                return;
            }

            FlexBase.DbgTrace("segmentLock2");
            lock (segmentLock)
            {
                // Check for a stale segment.
                // This may ignore the first call after a new segment.
                if (segment.Generation != generation)
                {
                    Tracing.TraceLine("waterfallDataHandler:returning:" + generation, TraceLevel.Info);
                    FlexBase.DbgTrace("segmentLock2 Done");
                    return;
                }

                // Check center freq
                double center = segmentCenter(segment);
                if (w.CenterFreq != center)
                {
                    Tracing.TraceLine("waterfallDataHandler:center not equal:" + w.CenterFreq.ToString() + ' ' + center.ToString() + ' ' + centerChangeSent.ToString(), TraceLevel.Info);
                    // only do this once.
                    if (!centerChangeSent)
                    {
                        Tracing.TraceLine("waterfallDataHandler:center:" + center.ToString(), TraceLevel.Info);
                        rig.q.Enqueue((FlexBase.FunctionDel)(() =>
                        {
                            panParameterSetup();
                        }), "center change");
                        centerChangeSent = true;
                        FlexBase.DbgTrace("segmentLock2 Done");
                        return;
                    }
                }
                    centerChangeSent = false;

                flexPan.Write(tile);
            }
            FlexBase.DbgTrace("segmentLock2 Done");
        }

        private void gotoFreq(double freq)
        {
            //Tracing.TraceLine("gotoFreq:" + freq.ToString() + ' ' + stepSize.ToString() + ' ' + segment.Low.ToString(), TraceLevel.Info);
            Tracing.TraceLine("gotoFreq:" + freq.ToString() + ' ' + stepSize.ToString(), TraceLevel.Info);
            //freq = Math.Round(freq, 4); // round to the nearest 100 hz.
            // may want to change the transmit freq if split and showing xmit
            if (rig.ShowingXmitFrequency)
            {
                rig.q.Enqueue((FlexBase.FunctionDel)(() => { rig.mySlices[rig.TXVFO].Freq = freq; }), "[rig.TXVFO].Freq");
            }
            else
            {
                rig.q.Enqueue((FlexBase.FunctionDel)(() => { theRadio.ActiveSlice.Freq = freq; }), "ActiveSlice.Freq");
            }
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { rig.Callouts.GotoHome(); }), "goto home");
        }

        private bool checkForRangeJump(Keys key)
        {
            bool rv = false;

            FlexBase.DbgTrace("segmentLock3");
            lock (segmentLock)
            {
                if (segment != null)
                {
                    PanRanges.PanRange newRange;
                    switch (key)
                    {
                        case Keys.PageUp:
                            if ((newRange = panRanges.PriorRange(segment)) != null)
                            {
                                segment = newRange;
                                invalidateOldSegment();
                                panParameterSetup();
                                rv = true;
                            }
                            break;
                        case Keys.PageDown:
                            if ((newRange = panRanges.NextRange(segment)) != null)
                            {
                                segment = newRange;
                                invalidateOldSegment();
                                rv = true;
                                panParameterSetup();
                            }
                            break;
#if zero
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
                                    invalidateOldSegment();
                                    panParameterSetup();
                                }
                                f.Dispose();
                            }
                            break;
#endif
                    }
                }
            }
            FlexBase.DbgTrace("segmentLock3 done");
            return rv;
        }

        private void PanBox_KeyDown(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = checkForRangeJump(e.KeyData);
        }

        private void PanBox_MouseClick(object sender, MouseEventArgs e)
        {
            int pos = PanBox.GetCharIndexFromPosition(e.Location);
            Tracing.TraceLine("PanBox_MouseClick:" + pos, TraceLevel.Info);
            double freq;
            lock (currentPanData)
            {
                freq = currentPanData.frequencies[pos];
            }
            gotoFreq(freq);
        }

        private const int panTimerInterval = 20;
        private const int panTimerInitialDelay = panTimerInterval * 50;
        private System.Threading.Timer panTimer;

        private void PanBox_Enter(object sender, EventArgs e)
        {
            int pos = 0;
            lock (currentPanData)
            {
                pos = currentPanData.FreqToCell(theRadio.ActiveSlice.Freq);
                Tracing.TraceLine("PanBox_Enter:" + pos, TraceLevel.Info);
                // save this position.
                currentPanData.EntryPosition = pos;
            }
            if (pos < PanBox.Text.Length)
            {
                PanBox.SelectionStart = pos;
                PanBox.SelectionLength = 0;
            }
            else Tracing.TraceLine("Flex6300Filters.PanBox_enter text length:" + pos + ' ' + PanBox.Text.Length, TraceLevel.Error);

            // Start looking for a cursor position change.
            panTimer = new System.Threading.Timer(panTimerHandler, null, panTimerInitialDelay, panTimerInterval);
        }

        private void PanBox_Leave(object sender, EventArgs e)
        {
            if (panTimer != null)
            {
                panTimer.Dispose();
                panTimer = null;
            }
        }

        // look for a cursor position change.
        private void panTimerHandler(object state)
        {
            if (panTimer == null) return; // timer inactive
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
            if (go)
            {
                Tracing.TraceLine("panTimerHandler:" + pos + ' ' + freq.ToString(), TraceLevel.Info);
                gotoFreq(freq);
            }
        }

        private void PanLowBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Control && !e.Alt && !e.Shift &&
                !((e.KeyCode >= Keys.D0) && (e.KeyCode <= Keys.D9)))
            {
                e.SuppressKeyPress = checkForRangeJump(e.KeyData);
                if (e.SuppressKeyPress)
                {
                    ulong low;
                    FlexBase.DbgTrace("segmentLock4");
                    lock (segmentLock)
                    {
                        low = segment.Low;
                    }
                    TextOut.DisplayText(PanLowBox, rig.Callouts.FormatFreq(low), false, true);
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
                    ulong high;
                    FlexBase.DbgTrace("segmentLock5");
                    lock (segmentLock)
                    {
                        high = segment.High;
                    }
                    TextOut.DisplayText(PanHighBox, rig.Callouts.FormatFreq(high), false, true);
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

            FlexBase.DbgTrace("segmentLock6");
            lock (segmentLock)
            {
                segment = new PanRanges.PanRange(low, high);
                invalidateOldSegment();
                panParameterSetup();
            }
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            FlexBase.DbgTrace("segmentLock7");
            lock (segmentLock)
            {
                if ((segment != null) && !segment.Saved)
                {
                    panRanges.Insert(segment);
                }
            }
        }

        private void EraseButton_Click(object sender, EventArgs e)
        {
            FlexBase.DbgTrace("segmentLock8");
            lock (segmentLock)
            {
                if (segment.Saved & !segment.Permanent) panRanges.Remove(segment);

                // Try a new segment.
                segment = null;
                RXFreqChange(rig.VFOToSlice(rig.RXVFO));
            }
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
                FlexBase.await(() => { return !zeroBeatThread.IsAlive; }, 1100);
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
