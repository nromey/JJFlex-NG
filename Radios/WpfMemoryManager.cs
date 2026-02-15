using System;
using System.Collections.Generic;
using System.Diagnostics;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Headless memory manager implementing IMemoryManager.
    /// Replaces FlexMemories (WinForms Form) for memory channel operations.
    /// Sprint 11 Phase 11.3.
    /// </summary>
    public class WpfMemoryManager : IMemoryManager
    {
        private FlexBase rig;
        private Radio theRadio { get { return rig.theRadio; } }

        public class MemoryElement : IMemoryElement
        {
            private Memory memory;
            public MemoryElement(Memory m) { memory = m; }
            public string FullName { get { return FlexBase.FullMemoryName(memory); } }
            public string Display { get { return FullName; } }
            public Memory Value { get { return memory; } }
        }

        private List<MemoryElement> _SortedMemories = new List<MemoryElement>();
        public List<MemoryElement> SortedMemories
        {
            get { return _SortedMemories; }
        }

        IReadOnlyList<IMemoryElement> IMemoryManager.SortedMemories
        {
            get { return _SortedMemories; }
        }

        private int _CurrentMemoryChannel = -1;
        public int CurrentMemoryChannel
        {
            get { return _CurrentMemoryChannel; }
            set
            {
                if (value < 0) _CurrentMemoryChannel = -1;
                else if (value >= _SortedMemories.Count) _CurrentMemoryChannel = 0;
                else _CurrentMemoryChannel = value;
            }
        }

        public int NumberOfMemories { get { return _SortedMemories.Count; } }

        private Memory SelectedMemory
        {
            get { return (_CurrentMemoryChannel == -1 || _CurrentMemoryChannel >= _SortedMemories.Count) ? null : _SortedMemories[_CurrentMemoryChannel].Value; }
        }

        public WpfMemoryManager(FlexBase rig)
        {
            this.rig = rig;
        }

        /// <summary>
        /// Initialize memory list. Call after radio is ready.
        /// Replaces FlexMemories.FlexMemories_Setup().
        /// </summary>
        public void Setup()
        {
            _SortedMemories = SortMemories();
            CurrentMemoryChannel = (_SortedMemories.Count > 0) ? 0 : -1;
        }

        public bool SelectMemory()
        {
            bool rv = (SelectedMemory != null);
            Tracing.TraceLine("WpfMemoryManager.SelectMemory:" + rv, TraceLevel.Info);
            if (rv)
            {
                rig.q.Enqueue((FlexBase.FunctionDel)(() =>
                {
                    SelectedMemory.Select();
                }));
            }
            return rv;
        }

        public bool SelectMemoryByName(string name)
        {
            for (int i = 0; i < _SortedMemories.Count; i++)
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

        public List<string> MemoryNames()
        {
            List<string> rv = new List<string>();
            foreach (MemoryElement el in _SortedMemories)
            {
                rv.Add(el.FullName);
            }
            return rv;
        }

        private List<MemoryElement> SortMemories()
        {
            List<MemoryElement> sorted = new List<MemoryElement>();
            if (theRadio.MemoryList.Count > 0)
            {
                foreach (Memory m in theRadio.MemoryList)
                {
                    if ((m.Freq == 0) | (m.Mode == null))
                    {
                        Tracing.TraceLine("WpfMemoryManager.SortMemories:null element", TraceLevel.Error);
                        continue;
                    }
                    sorted.Add(new MemoryElement(m));
                }
                sorted.Sort(CompareMemoryElements);
            }
            return sorted;
        }

        private static int CompareMemoryElements(MemoryElement x, MemoryElement y)
        {
            if (x.Value.Group != y.Value.Group) return x.Value.Group.CompareTo(y.Value.Group);
            if (string.IsNullOrEmpty(x.Value.Name) && string.IsNullOrEmpty(y.Value.Name))
                return x.Value.Freq.CompareTo(y.Value.Freq);
            return x.Value.Name.CompareTo(y.Value.Name);
        }
    }
}
