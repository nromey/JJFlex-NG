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
using Flex.Smoothlake.FlexLib;
using JJTrace;
using RadioBoxes;

namespace Radios
{
    public partial class FlexMemories : Form, IMemoryManager
    {
        private const string warning = "Warning";
        private const string dupName =
            "Warning:  Duplicate name\n\r" +
            "Do you want to change it?";
        private bool wasSetup;
        private bool wasActive;
        private FlexBase rig;
        private Radio theRadio { get { return rig.theRadio; } }

        public class MemoryElement : IMemoryElement
        {
            private Memory memory;
            public MemoryElement(Memory m)
            {
                memory = m;
            }
            public string FullName
            {
                // usually of the form group.name.
                get { return FlexBase.FullMemoryName(memory); }
            }
            // Used by the memory form.
            public string Display
            {
                get { return FullName; }
            }
            public Memory Value
            {
                get { return memory; }
            }
        }
        private List<MemoryElement> _SortedMemories;
        /// <summary>
        /// Memories sorted by name.
        /// </summary>
        public List<MemoryElement> SortedMemories
        {
            get { return _SortedMemories; }
            set { _SortedMemories = value; }
        }

        // Explicit interface implementation for IMemoryManager.SortedMemories.
        IReadOnlyList<IMemoryElement> IMemoryManager.SortedMemories
        {
            get { return _SortedMemories; }
        }
        private List<MemoryElement> sortMemories()
        {
            List<MemoryElement> sorted = new List<MemoryElement>();
            if (theRadio.MemoryList.Count > 0)
            {
                foreach (Memory m in theRadio.MemoryList)
                {
                    // Don't include a null memory.
                    if ((m.Freq == 0) | (m.Mode == null))
                    {
                        Tracing.TraceLine("SortElements:null element", TraceLevel.Error);
                        continue;
                    }
                    sorted.Add(new MemoryElement(m));
                }
                sorted.Sort(compareMemoryElements);
            }
            return sorted;
        }
        private static int compareMemoryElements(MemoryElement x, MemoryElement y)
        {
            // First, sort by group.
            if (x.Value.Group != y.Value.Group) return x.Value.Group.CompareTo(y.Value.Group);

            // Same group.
            // If unnamed, use the frequency.
            if (string.IsNullOrEmpty(x.Value.Name) && string.IsNullOrEmpty(y.Value.Name))
            {
                return x.Value.Freq.CompareTo(y.Value.Freq);
            }

            // Otherwise compare lexically.
            return x.Value.Name.CompareTo(y.Value.Name);
        }

        private int findMemoryInSorted(Memory mem)
        {
            for (int rv = 0; rv < _SortedMemories.Count; rv++)
            {
                if (_SortedMemories[rv].Value == mem)
                {
                    return rv;
                }
            }
            return -1;
        }

        private int _CurrentMemoryChannel = -1;
        public int CurrentMemoryChannel
        {
            get { return _CurrentMemoryChannel; }
            set
            {
                if (value < 0) _CurrentMemoryChannel = -1;
                else if (value >= _SortedMemories.Count) _CurrentMemoryChannel = 0; // wrap around
                else _CurrentMemoryChannel = value;
            }
        }

        public int NumberOfMemories
        {
            get { return _SortedMemories.Count; }
        }

        private Memory selectedMemory
        {
            get
            {
                return (_CurrentMemoryChannel == -1) ? null :
                    _SortedMemories[_CurrentMemoryChannel].Value;
            }
        }

        public bool SelectMemory()
        {
            bool rv = (selectedMemory != null);
            Tracing.TraceLine("SelectMemory:" + rv, TraceLevel.Info);
            if (rv)
            {
                rig.q.Enqueue((FlexBase.FunctionDel)(() =>
                {
                    selectedMemory.Select();
                }));
            }
            return rv;
        }

        /// <summary>
        /// Select memory by name
        /// </summary>
        /// <param name="name">FullName of memory</param>
        public bool SelectMemoryByName(string name)
        {
            for (int i=0;i<_SortedMemories.Count;i++)
            {
                if (_SortedMemories[i].FullName == name)
                {
                    CurrentMemoryChannel = i;
                    SelectMemory();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Get sorted list of full memory names.
        /// </summary>
        public List<string> MemoryNames()
        {
            List<string> rv = new List<string>();
            foreach(MemoryElement el in _SortedMemories)
            {
                rv.Add(el.FullName);
            }
            return rv;
        }


        private Collection<Combo> combos;
        private Collection<NumberBox> numberBoxes;
        private Collection<TextBox> textBoxes;
        // tag field contains this function.
        private delegate void getBoxDel();

        internal class directionElement
        {
            private FlexBase.OffsetDirections val;
            public string Display
            {
                get { return val.ToString(); }
            }
            public FlexBase.OffsetDirections RigItem
            {
                get { return val; }
            }
            public directionElement(FlexBase.OffsetDirections v)
            {
                val = v;
            }
        }

        private class modeElement
        {
            private string val;
            public string Display
            {
                get { return val; }
            }
            public string RigItem
            {
                get { return val; }
            }
            public modeElement(string v)
            {
                val = v;
            }
        }
        private ArrayList modeList;
        private ArrayList toneModeList, toneFrequencyList;
        private ArrayList SquelchList, offsetDirectionList;

        public FlexMemories(FlexBase r)
        {
            InitializeComponent();  

            rig = r;
            combos = new Collection<Combo>();
            numberBoxes = new Collection<NumberBox>();
            textBoxes = new Collection<TextBox>();

            // Mode box.
            modeList = new ArrayList();
            foreach (string m in RigCaps.ModeTable)
            {
                if (m != "none") modeList.Add(new modeElement(m.ToLower()));
            }
            ModeControl.TheList = modeList;
            ModeControl.UpdateDisplayFunction =
                () =>
                {
                    modeChange.enableDisable(selectedMemory.Mode);
                    return selectedMemory.Mode;
                };
            ModeControl.UpdateRigFunction =
                (object v) =>
                {
                    string m = ((string)v);
                    modeChange.enableDisable(m);
                    selectedMemory.Mode = m;
                };
            combos.Add(ModeControl);

            // Filters
            FilterLowControl.LowValue = Flex6300Filters.filterLowMinimum;
            FilterLowControl.HighValue = Flex6300Filters.filterHighMinimum;
            FilterLowControl.Increment = Flex6300Filters.filterLowIncrement;
            FilterLowControl.UpdateDisplayFunction =
                () => { return selectedMemory.RXFilterLow; };
            FilterLowControl.UpdateRigFunction =
                (int v) => { selectedMemory.RXFilterLow = v; };
            numberBoxes.Add(FilterLowControl);

            FilterHighControl.LowValue = Flex6300Filters.filterLowMinimum;
            FilterHighControl.HighValue = Flex6300Filters.filterHighMinimum;
            FilterHighControl.Increment = Flex6300Filters.filterLowIncrement;
            FilterHighControl.UpdateDisplayFunction =
                () => { return selectedMemory.RXFilterHigh; };
            FilterHighControl.UpdateRigFunction =
                (int v) => { selectedMemory.RXFilterHigh= v; };
            numberBoxes.Add(FilterHighControl);

            // FM tone or CTCSS mode.
            toneModeList = new ArrayList();
            foreach (FlexBase.ToneCTCSSValue t in rig.FMToneModes)
            {
                toneModeList.Add(new Flex6300Filters.toneCTCSSElement(t));
            }
            ToneModeControl.TheList = toneModeList;
            ToneModeControl.UpdateDisplayFunction =
                () => { return rig.ToneModeToToneCTCSS(selectedMemory.ToneMode); };
            ToneModeControl.UpdateRigFunction =
                (object v) => { selectedMemory.ToneMode = rig.ToneCTCSSToToneMode((FlexBase.ToneCTCSSValue)v); };
            combos.Add(ToneModeControl);

            // FM tone frequency
            toneFrequencyList = new ArrayList();
            foreach (float f in rig.ToneFrequencyTable)
            {
                toneFrequencyList.Add(new Flex6300Filters.toneCTCSSFreqElement(f));
            }
            ToneFrequencyControl.TheList = toneFrequencyList;
            ToneFrequencyControl.UpdateDisplayFunction =
                () => { return rig.ToneValueToFloat(selectedMemory.ToneValue); };
            ToneFrequencyControl.UpdateRigFunction =
                (object v) => { selectedMemory.ToneValue = rig.FloatToToneValue((float)v); };
            combos.Add(ToneFrequencyControl);

            // Squelch controls
            SquelchList = new ArrayList();
            SquelchList.Add(new Flex6300Filters.offOnElement(FlexBase.OffOnValues.off));
            SquelchList.Add(new Flex6300Filters.offOnElement(FlexBase.OffOnValues.on));
            SquelchControl.TheList = SquelchList;
            SquelchControl.UpdateDisplayFunction =
                () => { return (selectedMemory.SquelchOn) ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off; };
            SquelchControl.UpdateRigFunction =
                (object v) =>
                { selectedMemory.SquelchOn = ((FlexBase.OffOnValues)v == FlexBase.OffOnValues.on) ? true : false; };
            combos.Add(SquelchControl);

            SquelchLevelControl.LowValue = FlexBase.SquelchLevelMin;
            SquelchLevelControl.HighValue = FlexBase.SquelchLevelMax;
            SquelchLevelControl.Increment = FlexBase.SquelchLevelIncrement;
            SquelchLevelControl.UpdateDisplayFunction =
                () => { return selectedMemory.SquelchLevel; };
            SquelchLevelControl.UpdateRigFunction =
                (int v) => { selectedMemory.SquelchLevel = v; };
            numberBoxes.Add(SquelchLevelControl);

            // Offset direction and offset
            offsetDirectionList = new ArrayList();
            foreach (Flex6300Filters.offsetDirectionElement e in Flex6300Filters.offsetDirectionValues)
            {
                offsetDirectionList.Add(e);
            }
            OffsetDirectionControl.TheList = offsetDirectionList;
            OffsetDirectionControl.UpdateDisplayFunction =
                () => { return rig.FlexOffsetDirectionToOffsetDirection(selectedMemory.OffsetDirection); };
            OffsetDirectionControl.UpdateRigFunction =
                (object v) => { selectedMemory.OffsetDirection = rig.OffsetDirectionToFlexOffsetDirection((FlexBase.OffsetDirections)v); };
            combos.Add(OffsetDirectionControl);

            OffsetControl.LowValue = FlexBase.offsetMin;
            OffsetControl.HighValue = FlexBase.offsetMax;
            OffsetControl.Increment = FlexBase.offsetIncrement;
            OffsetControl.UpdateDisplayFunction =
                () => { return (int)(selectedMemory.RepeaterOffset * 1e3); };
            OffsetControl.UpdateRigFunction =
                (int v) => { selectedMemory.RepeaterOffset = (v / 1e3); };
            numberBoxes.Add(OffsetControl);

            // Text boxes
            NameBox.Tag = (getBoxDel)(() => { NameBox.Text = selectedMemory.Name; });
            textBoxes.Add(NameBox);
            OwnerBox.Tag = (getBoxDel)(() => { OwnerBox.Text = selectedMemory.Owner; });
            textBoxes.Add(OwnerBox);
            GroupBox.Tag = (getBoxDel)(() => { GroupBox.Text = selectedMemory.Group; });
            textBoxes.Add(GroupBox);

            modeChange = new Flex6300Filters.modeChangeClass(this, buildModeChange(), null);
        }

        // Called from PanSetup.
        internal void FlexMemories_Setup()
        {
            // Used when dialogue isn't up.
            _SortedMemories = sortMemories();
            CurrentMemoryChannel = (_SortedMemories.Count >= 0) ? 0 : -1;
        }

        private void FlexMemories_Load(object sender, EventArgs e)
        {
            wasActive = false;
            DialogResult = DialogResult.None;
            ShowFreq = false;

            // One-time setup
            if (!wasSetup)
            {
                wasSetup = true;
                theRadio.MemoryAdded += new Radio.MemoryAddedEventHandler(memoryAdded);
                theRadio.MemoryRemoved += new Radio.MemoryRemovedEventHandler(memoryRemoved);
            }

            // See refreshMemoryList
            refreshMemoryList(null);
            if (selectedMemory != null) showMemory(selectedMemory);
        }

#if FlexGroups
        public List<ScanGroup> GetReservedGroups()
        {
            if ((theRadio.MemoryList == null) || (theRadio.MemoryList.Count == 0)) return null;
            Tracing.TraceLine("GetReservedGroups", TraceLevel.Info);
            Dictionary<string, List<MemoryData>> groups = new Dictionary<string, List<MemoryData>>();
            for (int i = 0; i < Memories.mems.Length; i++)
            {
                Memory mem = (Memory)Memories.mems[i].ExternalMemory;
                List<MemoryData> val = null;
                if (!groups.TryGetValue(mem.Group, out val))
                {
                    // New group
                    val = new List<MemoryData>();
                    groups.Add(mem.Group, val);
                }
                val.Add(Memories.mems[i]);
            }
            if (groups.Keys.Count == 0) return null;
            List<ScanGroup> rv = new List<ScanGroup>();
            foreach (string key in groups.Keys)
            {
                rv.Add(new ScanGroup(key, Memories.Bank, groups[key], true));
            }
            Tracing.TraceLine("GetReservedGroups:" + rv.Count, TraceLevel.Info);
            return rv;
        }
#endif

        private void FlexMemories_Activated(object sender, EventArgs e)
        {
            if (!wasActive)
            {
                wasActive = true;
                refreshMemoryList(null);
                MemoryList.Focus();
            }
        }

        private delegate void refreshMemoryListDel(Memory m);
        // May be called from an interrupt handler.
        private void refreshMemoryList(Memory m)
        {
            if (MemoryList.InvokeRequired)
            {
                MemoryList.Invoke((refreshMemoryListDel)refreshMemoryListProc, new object[] { m });
            }
            else refreshMemoryListProc(m);
        }
        private void refreshMemoryListProc(Memory m)
        {
            MemoryList.SuspendLayout();
            noSelectedAction = true; // don't run the SelectedIndexChange code.
            MemoryList.DataSource = null;
            MemoryList.DisplayMember = "Display";
            MemoryList.ValueMember = "Value";
            _SortedMemories = sortMemories();
            MemoryList.DataSource = _SortedMemories;
            // sync the CurrentMemoryChannel with the list.
            if (m == null)
            {
                if (_SortedMemories.Count == 0) CurrentMemoryChannel = -1;
                else if (_SortedMemories.Count <= CurrentMemoryChannel) CurrentMemoryChannel = _SortedMemories.Count - 1;
            }
            else
            {
                CurrentMemoryChannel = findMemoryInSorted(m);
            }
            MemoryList.SelectedIndex = CurrentMemoryChannel; // could be -1
            noSelectedAction = false;
            MemoryList.ResumeLayout();
        }

        private void showMemory(Memory m)
        {
            FreqBox.Text = rig.Callouts.FormatFreq(rig.LibFreqtoLong(m.Freq));
             foreach (Combo c in combos)
            {
                if (c.Enabled) c.UpdateDisplay(true);
            }

            foreach (NumberBox n in numberBoxes)
            {
                if (n.Enabled) n.UpdateDisplay(true);
            }

            foreach (TextBox tb in textBoxes)
            {
                ((getBoxDel)tb.Tag)();
            }
        }

        private bool addRemoveFlag;
        private void memoryRemoved(Memory mem)
        {
            Tracing.TraceLine("memoryRemoved:" + mem.ToString(), TraceLevel.Info);
            addRemoveFlag = true;
        }

        private Memory addedMem;
        private void memoryAdded(Memory mem)
        {
            Tracing.TraceLine("memoryAdded:" + mem.ToString(), TraceLevel.Info);
            addedMem = mem;
            addRemoveFlag = true;
        }

        private Flex6300Filters.modeChangeClass modeChange;

        private Dictionary<string, Flex6300Filters.modeChangeClass.controlsClass> buildModeChange()
        {
            Dictionary<string, Flex6300Filters.modeChangeClass.controlsClass> rv = new Dictionary<string, Flex6300Filters.modeChangeClass.controlsClass>();

            // setup the mode to combobox mapping.
            rv.Add("fm", new Flex6300Filters.modeChangeClass.controlsClass(
                new Control[] {
                    ToneModeControl, ToneFrequencyControl,
                    SquelchControl, SquelchLevelControl,
                    OffsetDirectionControl, OffsetControl,
                    }));
            rv.Add("digl", new Flex6300Filters.modeChangeClass.controlsClass(
                new Control[] {
                    }));
            rv.Add("digu", new Flex6300Filters.modeChangeClass.controlsClass(
                new Control[] {
                    }));
            rv.Add("nfm", new Flex6300Filters.modeChangeClass.controlsClass(
                new Control[] {
                    SquelchControl, SquelchLevelControl,
                    }));
            rv.Add("dfm", new Flex6300Filters.modeChangeClass.controlsClass(
                new Control[] {
                    SquelchControl, SquelchLevelControl,
                    }));

            return rv;
        }

        private bool noSelectedAction;
        private void MemoryList_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (noSelectedAction) return;
            if (MemoryList.SelectedValue != null)
            {
                _CurrentMemoryChannel = MemoryList.SelectedIndex;
                showMemory((Memory)MemoryList.SelectedValue);
            }
        }

        /// <summary>
        /// Show the frequency field when done.
        /// </summary>
        public bool ShowFreq;
        private void MemoryList_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r')
            {
                e.Handled = true;
                DialogResult = DialogResult.OK;
                SelectMemory();
                ShowFreq = true;
            }
        }

        private void AddButton_Click(object sender, EventArgs e)
        {
            addRemoveFlag = false;
            Memory m = new Memory(theRadio, theRadio.MemoryList.Count);
            rig.q.Enqueue((FlexBase.FunctionDel)(() => { theRadio.RequestMemory(); }));
            // Wait until memory added.
            FlexBase.await(() => { return addRemoveFlag; }, 1000);
            refreshMemoryList(addedMem); // sets CurrentMemoryChannel
            showMemory(addedMem);
            MemoryList.Focus();
            DialogResult = DialogResult.None;
        }

        private void DeleteButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.None;
            if (MemoryList.SelectedValue != null)
            {
                Tracing.TraceLine("DeleteButton_Click", TraceLevel.Info);
                addRemoveFlag = false;
                Memory mem = (Memory)MemoryList.SelectedValue;
                rig.q.Enqueue((FlexBase.FunctionDel)(() => { mem.Remove(); }));
                // await completion
                FlexBase.await(() => { return addRemoveFlag; }, 1000);
                refreshMemoryList(null); // might set CurrentMemoryChannel
            }
            MemoryList.Focus();
        }

        private void DoneButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
        }

        private void FreqBox_Leave(object sender, EventArgs e)
        {
            if (selectedMemory == null) return;
            // Set the memory's frequency.
            double freq = rig.LongFreqToLibFreq(rig.Callouts.FormatFreqForRadio(FreqBox.Text));
            if ((freq != 0) && (selectedMemory.Freq != freq))
            {
                selectedMemory.Freq = freq;
                refreshMemoryList(selectedMemory); // might change sort order, hence CurrentMemoryChannel
            }
        }

        private void FreqBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r')
            {
                e.Handled = true;
                SendKeys.Send("{TAB}");
            }
        }

        // Called on leave
        private void GroupBox_Change(object sender, EventArgs e)
        {
            if (selectedMemory != null)
            {
                if (selectedMemory.Group != GroupBox.Text)
                {
                    selectedMemory.Group = GroupBox.Text;
                    refreshMemoryList(selectedMemory); // might change sort order, hence CurrentMemoryChannel
                }
            }
        }

        private void GroupBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r')
            {
                e.Handled = true;
                SendKeys.Send("{TAB}");
            }
        }

        private void OwnerBox_Change(object sender, EventArgs e)
        {
            if (selectedMemory != null)
            {
                if (selectedMemory.Owner != OwnerBox.Text)
                {
                    selectedMemory.Owner = OwnerBox.Text;
                    //refreshMemoryList(selectedMemory);
                }
            }
        }

        private void OwnerBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r')
            {
                e.Handled = true;
                SendKeys.Send("{TAB}");
            }
        }

        private void NameBox_Change(object sender, EventArgs e)
        {
            if (selectedMemory != null)
            {
                if (selectedMemory.Name != NameBox.Text)
                {
                    foreach (Memory m in theRadio.MemoryList)
                    {
                        if (m.Name == NameBox.Text)
                        {
                            if (MessageBox.Show(dupName, warning, MessageBoxButtons.YesNo) == DialogResult.Yes)
                            {
                                NameBox.Focus();
                                return;
                            }
                        }
                    }
                    selectedMemory.Name = NameBox.Text;
                    refreshMemoryList(selectedMemory); // might change sort order, hence CurrentMemoryChannel
                }
            }
        }

        private void NameBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r')
            {
                e.Handled = true;
                SendKeys.Send("{TAB}");
            }
        }
    }
}
