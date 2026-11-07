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
    /// A filter preset's announcement says what the filter BECAME, and the
    /// active-preset status says the filter that IS (Codex's third re-verify
    /// of the 2026-11-06 junk box, B1 and B2; #747).
    /// </summary>
    /// <remarks>
    /// <para>B2: selecting a preset announced the edges it ASKED for, the
    /// moment the write was queued. FlexLib's <c>Slice.UpdateFilter</c>
    /// refuses FM and NFM outright and clamps other modes, so the operator
    /// heard numbers the radio never took. The announcement is now made on
    /// the command loop, after UpdateFilter has run, from the slice's own
    /// edges: FlexLib's rules, run, never copied.</para>
    /// <para>B1: <c>FindActivePreset</c> recognises a preset within 20 Hz an
    /// edge, and the status then spoke the SAVED edges as the current ones.
    /// It keeps the name and speaks the live edges.</para>
    /// <para>Real vendor Radio and Slice objects over the house fake
    /// transport; no radio, no window. Every expected sentence is a literal,
    /// assembled as heard, and DRAFT.</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class FilterPresetAppliedTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        private readonly VendorRadioFixture _vendor = new VendorRadioFixture("1111-2222-6300-4444");
        private readonly Slice _slice;

        public FilterPresetAppliedTests()
        {
            _slice = (Slice)Activator.CreateInstance(typeof(Slice), Private, null, new object[] { _vendor.Radio }, null)!;
            typeof(Slice).GetField("_active", Private)!.SetValue(_slice, true);
            _vendor.Radio.SliceList.Add(_slice);
            _rig.theRadio = _vendor.Radio;
            _rig.q.MainLoop = true;   // the queue holds writes for Drain, as the main loop would run them
        }

        public void Dispose()
        {
            _rig.theRadio = null;   // never FlexBase's to disconnect
            _rig.Dispose();
        }

        private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()));

        private void Drain()
        {
            while (_rig.q.Count > 0)
            {
                var item = _rig.q.Dequeue();
                if (item.Item is FlexBase.FunctionDel f) f();
            }
        }

        private void Slice(string mode, int low, int high, int rttyMark = 0, int rttyShift = 170)
        {
            typeof(Slice).GetField("_demodMode", Private)!.SetValue(_slice, mode);
            typeof(Slice).GetField("_filterLow", Private)!.SetValue(_slice, low);
            typeof(Slice).GetField("_filterHigh", Private)!.SetValue(_slice, high);
            typeof(Slice).GetField("_rttyMark", Private)!.SetValue(_slice, rttyMark);
            typeof(Slice).GetField("_rttyShift", Private)!.SetValue(_slice, rttyShift);
        }

        /// <summary>Select a preset as both routes do, run the loop, and return what was said.</summary>
        private List<string> Select(FilterPreset preset, string mode, VerbosityLevel level)
        {
            var said = new List<string>();
            var saved = ScreenReaderOutput.CurrentVerbosity;
            ScreenReaderOutput.CurrentVerbosity = level;
            try
            {
                _rig.ApplyFilterPreset(preset, mode, said.Add);
                Drain();
            }
            finally { ScreenReaderOutput.CurrentVerbosity = saved; }
            return said;
        }

        // ------------------------------------------------------------------
        // B2: what the filter became
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("FM")]
        [InlineData("NFM")]
        public void In_FM_a_preset_cannot_change_the_filter_and_says_so(string mode)
        {
            Slice(mode, -6000, 6000);
            var said = Select(new FilterPreset("Narrow", -4000, 4000), mode, VerbosityLevel.Chatty);
            Assert.Equal(new[] { "Narrow, filter unchanged at -6000 to 6000, width 12000 hertz" }, said);
            Assert.Equal((-6000, 6000), (_rig.FilterLow, _rig.FilterHigh));
        }

        [Fact]
        public void In_FM_at_terse()
        {
            Slice("FM", -6000, 6000);
            Assert.Equal(new[] { "Narrow, filter unchanged, 12 k" },
                Select(new FilterPreset("Narrow", -4000, 4000), "FM", VerbosityLevel.Terse));
        }

        [Fact]
        public void In_RTTY_a_preset_says_the_filter_the_mode_allowed()
        {
            Slice("RTTY", -500, 100, rttyMark: 0, rttyShift: 170);
            var said = Select(new FilterPreset("Normal", 100, 2500), "RTTY", VerbosityLevel.Chatty);
            Assert.Equal(new[] { "Normal, filter -220 to 150, width 370 hertz, at limit" }, said);
            Assert.Equal((-220, 150), (_rig.FilterLow, _rig.FilterHigh));
        }

        [Fact]
        public void In_USB_a_preset_past_the_top_says_where_it_stopped()
        {
            Slice("USB", 100, 2800);
            Assert.Equal(new[] { "Custom, filter 100 to 12000, width 11900 hertz, at limit" },
                Select(new FilterPreset("Custom", 100, 12500), "USB", VerbosityLevel.Chatty));
            Slice("USB", 100, 2800);
            Assert.Equal(new[] { "Custom, 11.9 k, at limit" },
                Select(new FilterPreset("Custom", 100, 12500), "USB", VerbosityLevel.Terse));
        }

        /// <summary>The controls: a preset the mode takes as asked says exactly what it asked for.</summary>
        [Theory]
        [InlineData("USB", 100, 2800, "Example", 100, 2850, "Example, filter 100 to 2850, width 2750 hertz")]
        [InlineData("LSB", -2800, -100, "Example", 100, 2850, "Example, filter -2850 to -100, width 2750 hertz")]
        [InlineData("CW", -250, 250, "Narrow", -125, 125, "Narrow, filter -125 to 125, width 250 hertz")]
        [InlineData("AM", -5000, 5000, "Normal", -3000, 3000, "Normal, filter -3000 to 3000, width 6000 hertz")]
        public void A_preset_the_mode_takes_says_what_it_set(string mode, int fromLow, int fromHigh, string name, int low, int high, string heard)
        {
            Slice(mode, fromLow, fromHigh);
            Assert.Equal(new[] { heard }, Select(new FilterPreset(name, low, high), mode, VerbosityLevel.Chatty));
        }

        /// <summary>Nothing is said before the loop has run the write: an enqueue is not proof.</summary>
        [Fact]
        public void Nothing_is_said_until_the_write_has_run()
        {
            Slice("USB", 100, 2800);
            var said = new List<string>();
            _rig.ApplyFilterPreset(new FilterPreset("Example", 100, 2850), "USB", said.Add);
            Assert.Empty(said);
            Drain();
            Assert.Single(said);
        }

        // ------------------------------------------------------------------
        // B1: the status says the filter that is
        // ------------------------------------------------------------------

        private static FilterPresets WithExample()
            => new FilterPresets { Modes = { new ModePresets { Mode = "SSB", Presets = { new FilterPreset("Example", 100, 2850) } } } };

        [Fact]
        public void A_near_match_is_named_but_the_numbers_are_the_live_ones()
        {
            Assert.Equal("preset Example, filter 110 to 2840, width 2730 hertz",
                WithExample().ActiveStatus("USB", 110, 2840, VerbosityLevel.Chatty));
            Assert.Equal("preset Example, 2.73 k", WithExample().ActiveStatus("USB", 110, 2840, VerbosityLevel.Terse));
        }

        [Fact]
        public void Twenty_one_hertz_off_is_no_preset()
            => Assert.Null(WithExample().ActiveStatus("USB", 121, 2850, VerbosityLevel.Chatty));

        /// <summary>
        /// The whole path: the saved preset is selected and lands exactly,
        /// then the radio reports its filter 10 hertz inside each edge (a
        /// near match, still recognised). The status names the preset and
        /// reads the radio's filter, never the saved one.
        /// </summary>
        [Fact]
        public void From_selection_to_status_the_numbers_are_the_radios()
        {
            Slice("USB", 100, 2800);
            var presets = WithExample();
            var example = presets.GetPresetsForMode("USB")[0];
            Assert.Equal(new[] { "Example, filter 100 to 2850, width 2750 hertz" }, Select(example, "USB", VerbosityLevel.Chatty));

            Slice("USB", 110, 2840);   // the radio's own report, 10 hertz in at each edge
            Assert.Equal(0, presets.FindActivePreset("USB", _rig.FilterLow, _rig.FilterHigh));
            Assert.Equal("preset Example, filter 110 to 2840, width 2730 hertz",
                presets.ActiveStatus("USB", _rig.FilterLow, _rig.FilterHigh, VerbosityLevel.Chatty));
        }

        // ------------------------------------------------------------------
        // F1 and the wiring
        // ------------------------------------------------------------------

        [Fact]
        public void The_menu_label_says_the_name_once()
            => Assert.Equal("Wide (2.7 k)", new FilterPreset("Wide", 100, 2800).FormatForDisplay());

        [Fact]
        public void Both_selection_routes_and_the_status_use_the_applied_path()
        {
            string handlers = Src("JJFlexWpf", "FreqOutHandlers.cs");
            Assert.Contains("Rig.ApplyFilterPreset(preset, mode,", handlers, StringComparison.Ordinal);
            Assert.Contains("FilterPresets.ActiveStatus(mode, low, high, Radios.ScreenReaderOutput.CurrentVerbosity)", handlers, StringComparison.Ordinal);
            string menu = Src("JJFlexWpf", "NativeMenuBar.cs");
            Assert.Contains("Rig.ApplyFilterPreset(preset, currentMode, (said, applied) => SpeakAfterMenuClose(said, stillTrue: FilterStillIs(applied)));", menu, StringComparison.Ordinal);
            Assert.Contains("string label = preset.FormatForDisplay();", menu, StringComparison.Ordinal);
            Assert.DoesNotContain("preset.Announcement(", handlers + menu, StringComparison.Ordinal);
        }
    }
}
