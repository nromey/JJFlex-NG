#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Every readout that follows a receive filter write says what the filter
    /// BECAME (#747, round six of the 2026-11-06 junk box): the bracket keys,
    /// edge-mode steps, the filter layer's moves and the Filter menu. They
    /// each clamped to their own bounds and spoke their own numbers, while
    /// FlexLib's <c>Slice.UpdateFilter</c> refuses FM outright and clamps
    /// RTTY and the rest to its own, so they could say a filter the radio
    /// never took. Codex found the class on presets (round five, B2).
    /// </summary>
    /// <remarks>
    /// Real vendor Radio and Slice objects over the house fake transport, so
    /// UpdateFilter's own rules run; no radio, no window. Every expected
    /// sentence is a literal, assembled as heard, and DRAFT.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class FilterWriteReadoutTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        private readonly VendorRadioFixture _vendor = new VendorRadioFixture("1111-2222-6300-4444");
        private readonly Slice _slice;

        public FilterWriteReadoutTests()
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

        /// <summary>A bracket write as the handler makes it, run through the loop.</summary>
        private string Bracket(int low, int high, FilterEdge held, VerbosityLevel level, string ownLimit = "")
        {
            string? said = null;
            _rig.SetFilter(low, high, applied => said = FilterReadouts.Bracket(applied, ownLimit, held, level));
            Assert.Null(said);   // nothing before the write has run
            Drain();
            Assert.NotNull(said);
            return said!;
        }

        // ------------------------------------------------------------------
        // The bracket keys and edge mode
        // ------------------------------------------------------------------

        [Fact]
        public void In_FM_the_brackets_say_the_filter_is_unchanged()
        {
            Slice("FM", -6000, 6000);
            Assert.Equal("Filter unchanged at -6000 to 6000, width 12000 hertz", Bracket(-6000, 6100, FilterEdge.None, VerbosityLevel.Chatty));
            Slice("FM", -6000, 6000);
            Assert.Equal("Filter unchanged at -6000 to 6000, 12 k", Bracket(-6000, 6100, FilterEdge.None, VerbosityLevel.Terse));
        }

        [Fact]
        public void In_FM_an_edge_mode_step_says_so_with_the_held_edge()
        {
            Slice("FM", -6000, 6000);
            Assert.Equal("High edge held. Filter unchanged at -6000 to 6000, width 12000 hertz",
                Bracket(-6000, 6100, FilterEdge.High, VerbosityLevel.Chatty));
        }

        /// <summary>
        /// RTTY, mark 0, shift 170: widening the high edge to 200 is pulled to
        /// 0 by the mark and then up to the mode's own minimum of 50. The
        /// filter really became -220 to 50, so that is what is said.
        /// </summary>
        [Fact]
        public void In_RTTY_the_brackets_say_the_filter_the_mode_allowed()
        {
            Slice("RTTY", -220, 150);
            Assert.Equal("Filter -220 to 50, width 270 hertz, at limit", Bracket(-220, 200, FilterEdge.None, VerbosityLevel.Chatty));
            Assert.Equal((-220, 50), (_rig.FilterLow, _rig.FilterHigh));
        }

        /// <summary>The control: taken as asked, the readout is as before, own limit suffix and all.</summary>
        [Fact]
        public void In_USB_the_brackets_say_what_was_asked_because_it_was_taken()
        {
            Slice("USB", 100, 2800);
            Assert.Equal("Filter 100 to 2900, width 2800 hertz", Bracket(100, 2900, FilterEdge.None, VerbosityLevel.Chatty));
            Slice("USB", 100, 2800);
            Assert.Equal("Filter 0 to 2800, 2.8 k, lower limit",
                Bracket(0, 2800, FilterEdge.None, VerbosityLevel.Terse, ownLimit: Lexicon.Get("settings.filter.suffix_lower_limit")));
        }

        // ------------------------------------------------------------------
        // The filter layer, through the engine's after-the-write route
        // ------------------------------------------------------------------

        /// <summary>
        /// A one-target layer wired the way KeyCommands wires the receive
        /// filter's low edge: the bank takes the request, the write runs on the
        /// loop, and the sentence comes from what it produced.
        /// </summary>
        private (ValueSubLayer layer, List<string> moves) LowEdgeLayer(int low, int high, VerbosityLevel level)
        {
            int bankLow = low, bankHigh = high;
            var moves = new List<string>();
            var target = new ValueTarget
            {
                Id = "rx-low",
                Axes = ValueLayerAxes.UpDown,
                Min = -24000,
                Max = 24000,
                Step = 50,
                Read = () => bankLow,
                Apply = v => { bankLow = v; _rig.SetFilter(v, bankHigh); },
                Number = v => Lexicon.Get("audio.filter.low_edge", ("low", v)),
                ApplyThenSay = (v, say) =>
                {
                    bankLow = v;
                    _rig.SetFilter(v, bankHigh, applied =>
                    {
                        bankLow = applied.Low; bankHigh = applied.High;
                        say(FilterReadouts.Said(applied,
                            (l, _) => Lexicon.Get("audio.filter.low_edge", ("low", l)),
                            (l, h) => Lexicon.Get("audio.filter_layer.at_limit", ("what", Lexicon.Get("audio.filter.range", ("low", l), ("high", h)))),
                            level));
                    });
                },
            };
            var def = new ValueSubLayerDefinition
            {
                Id = "filter-test",
                Targets = new List<ValueTarget> { target },
                DescribeClosed = () => "closed",
            };
            var layer = ValueSubLayer.EnterForTest(def, (text, _) => moves.Add(text), (_, _) => { }, _ => { }, () => level);
            return (layer, moves);
        }

        [Fact]
        public void In_FM_the_layer_says_the_filter_is_unchanged_once_the_write_has_run()
        {
            Slice("FM", -6000, 6000);
            var (layer, moves) = LowEdgeLayer(-6000, 6000, VerbosityLevel.Chatty);
            layer.HandleKey(Keys.Down);
            Assert.Empty(moves);   // the engine does not speak the request
            Drain();
            Assert.Equal(new[] { "Filter unchanged at -6000 to 6000, width 12000 hertz" }, moves);
        }

        [Fact]
        public void In_RTTY_the_layer_says_the_whole_filter_the_mode_allowed()
        {
            Slice("RTTY", -220, 150);
            var (layer, moves) = LowEdgeLayer(-220, 150, VerbosityLevel.Chatty);
            layer.HandleKey(Keys.Down);   // the low edge asks for -270
            Drain();
            Assert.Equal(new[] { "Filter -270 to 50, at the limit" }, moves);
        }

        [Fact]
        public void In_USB_the_layer_says_the_edge_it_moved()
        {
            Slice("USB", 300, 2800);
            var (layer, moves) = LowEdgeLayer(300, 2800, VerbosityLevel.Chatty);
            layer.HandleKey(Keys.Down);
            Drain();
            Assert.Equal(new[] { "Low edge 250" }, moves);
        }

        // ------------------------------------------------------------------
        // The wiring, pinned at the source
        // ------------------------------------------------------------------

        [Fact]
        public void Every_receive_filter_write_that_speaks_goes_through_the_applied_route()
        {
            string handlers = Src("JJFlexWpf", "FreqOutHandlers.cs");
            Assert.Contains("Rig.SetFilter(low, high, applied => Radios.ScreenReaderOutput.Speak(", handlers, StringComparison.Ordinal);
            Assert.Contains("Radios.FilterReadouts.Bracket(applied, atLimit, held,", handlers, StringComparison.Ordinal);
            Assert.DoesNotContain("Rig.SetFilter(low, high);", handlers, StringComparison.Ordinal);

            string keys = Src("JJFlexWpf", "KeyCommands.cs");
            Assert.Contains("rx.ApplyThenSay = (l, h, readout, say) =>", keys, StringComparison.Ordinal);
            Assert.Contains("rig.SetFilter(l, h, applied =>", keys, StringComparison.Ordinal);
            foreach (string coordinate in new[] { "(l, _) => s.LowEdge(l)", "(_, h) => s.HighEdge(h)", "s.RangeAt, say", "s.WidthAt, say" })   // the edges the write produced (round eight)
                Assert.Contains(coordinate, keys, StringComparison.Ordinal);

            string menu = Src("JJFlexWpf", "NativeMenuBar.cs");
            Assert.Equal(7, CountOf(menu, "SayFilterWrite("));   // the six write items and the helper itself
            Assert.DoesNotContain("Rig.SetFilter(newLow", menu, StringComparison.Ordinal);
            Assert.DoesNotContain("Rig.SetFilter(Rig.FilterLow, newHigh)", menu, StringComparison.Ordinal);
            Assert.DoesNotContain("Rig.SetFilter(low, newHigh)", menu, StringComparison.Ordinal);
        }

        private static int CountOf(string text, string what)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
            return n;
        }
    }
}
