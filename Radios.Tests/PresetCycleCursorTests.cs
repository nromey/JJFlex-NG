#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Alt with a bracket moves through the filter presets by the operator's
    /// own position in the list, whether or not the radio took the last one
    /// (bench item 7 of the 2026-11-06 junk box, on the 8600 over SmartLink).
    /// </summary>
    /// <remarks>
    /// <para><b>The cause, found before the fix.</b> <c>FilterPresets.CyclePreset</c>
    /// works out where the operator is from the LIVE filter on every press.
    /// FM refuses every filter change, so the live filter stayed at -8000 to
    /// 8000, which is the Wide Open preset: Alt+] found itself already at the
    /// widest and said "Widest preset", and every Alt+[ stepped from Wide Open
    /// to Normal again. That is older than this batch: the cycle has always
    /// read the live filter; this batch only made the announcement say
    /// "unchanged" truthfully, which made the stuck cycle audible.</para>
    /// <para><b>The fix.</b> The cycle remembers the preset it last chose and
    /// the edges that choice left on the radio. While the live filter is
    /// still those edges, the next press steps from the remembered preset;
    /// once the filter has been changed some other way (brackets, the layer),
    /// the position comes from the live filter again, as before. Each
    /// refused preset still says "unchanged".</para>
    /// <para>Real vendor Radio and Slice objects over the fake transport, so
    /// UpdateFilter's FM refusal is the real one.</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class PresetCycleCursorTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        private readonly VendorRadioFixture _vendor = new VendorRadioFixture("1111-2222-6300-4444");
        private readonly Slice _slice;
        private readonly FilterPresets _presets = new FilterPresets();
        private readonly PresetCycleCursor _cursor = new PresetCycleCursor();
        private readonly List<string> _said = new List<string>();

        public PresetCycleCursorTests()
        {
            _slice = (Slice)Activator.CreateInstance(typeof(Slice), Private, null, new object[] { _vendor.Radio }, null)!;
            typeof(Slice).GetField("_active", Private)!.SetValue(_slice, true);
            _vendor.Radio.SliceList.Add(_slice);
            _rig.theRadio = _vendor.Radio;
            _rig.q.MainLoop = true;
        }

        public void Dispose()
        {
            _rig.theRadio = null;
            _rig.Dispose();
        }

        private void Rx(string mode, int low, int high)
        {
            typeof(Slice).GetField("_demodMode", Private)!.SetValue(_slice, mode);
            typeof(Slice).GetField("_filterLow", Private)!.SetValue(_slice, low);
            typeof(Slice).GetField("_filterHigh", Private)!.SetValue(_slice, high);
        }

        private void Drain()
        {
            while (_rig.q.Count > 0)
            {
                var item = _rig.q.Dequeue();
                if (item.Item is FlexBase.FunctionDel f) f();
            }
        }

        private string Press(string mode, int direction)
        {
            var saved = ScreenReaderOutput.CurrentVerbosity;
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
            try
            {
                _cursor.Cycle(_rig, _presets, mode, direction, _said.Add,
                    widest => _said.Add(widest ? "Widest preset" : "Narrowest preset"));
                Drain();
            }
            finally { ScreenReaderOutput.CurrentVerbosity = saved; }
            return _said.Last();
        }

        [Fact]
        public void In_FM_each_press_moves_on_to_the_next_name_and_says_it_was_not_applied()
        {
            Rx("FM", -8000, 8000);   // on Wide Open, as the bench found it
            Assert.Equal("Normal, filter unchanged at -8000 to 8000, width 16000 hertz", Press("FM", -1));
            Assert.Equal("Narrow, filter unchanged at -8000 to 8000, width 16000 hertz", Press("FM", -1));
            Assert.Equal("Narrowest preset", Press("FM", -1));
            Assert.Equal("Normal, filter unchanged at -8000 to 8000, width 16000 hertz", Press("FM", +1));
            Assert.Equal("Wide Open, filter -8000 to 8000, width 16000 hertz", Press("FM", +1));
            Assert.Equal((-8000, 8000), (_slice.FilterLow, _slice.FilterHigh));
        }

        /// <summary>The control: where the radio takes each preset, the cycle walks the list as it always did.</summary>
        [Fact]
        public void In_USB_the_cycle_walks_the_list_as_before()
        {
            Rx("USB", 100, 2500);   // Normal
            Assert.StartsWith("Wide, filter 100 to 2800", Press("USB", +1));
            Assert.StartsWith("Extra Wide, filter 100 to 3100", Press("USB", +1));
            Assert.StartsWith("Wide, filter 100 to 2800", Press("USB", -1));
        }

        /// <summary>
        /// Once the filter is changed some other way, the position comes from
        /// the live filter again: a bracket widening after an FM-style refusal
        /// does not leave the cycle stepping from a stale remembered preset.
        /// </summary>
        [Fact]
        public void A_filter_changed_some_other_way_puts_the_position_back_on_the_live_filter()
        {
            Rx("USB", 100, 2500);
            Press("USB", +1);                 // Wide, 100 to 2800
            Rx("USB", 100, 1900);             // the operator narrowed it with the brackets: Narrow
            Assert.StartsWith("Normal, filter 100 to 2500", Press("USB", +1));
        }

        [Fact]
        public void The_bracket_handler_cycles_through_the_cursor()
        {
            string handlers = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "FreqOutHandlers.cs"));
            Assert.Contains("_presetCursor.Cycle(Rig, FilterPresets, mode, direction,", handlers, StringComparison.Ordinal);
            Assert.DoesNotContain("FilterPresets.CyclePreset(mode, Rig.FilterLow, Rig.FilterHigh, direction)", handlers, StringComparison.Ordinal);
        }
    }
}
