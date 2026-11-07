#nullable enable

using System;
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
    /// The transmit filter says what FlexLib actually set, and FlexLib sends
    /// what it computed (#747, round seven of the 2026-11-06 junk box).
    /// </summary>
    /// <remarks>
    /// <para><b>The FlexLib defect (MIGRATION.md item 27).</b>
    /// <c>Radio.TXFilterLow</c> and <c>TXFilterHigh</c> worked out a clamped
    /// value, kept 50 Hz from the other edge, and then passed the UNCLAMPED
    /// value on to <c>_SetTXFilter</c>, which stored and sent it. So the 50 Hz
    /// minimum was never enforced, and a low edge at or above the high edge was
    /// dropped by <c>_SetTXFilter</c>'s own low-not-below-high check, silently,
    /// while the field that asked for it said the new value.</para>
    /// <para><b>The routes.</b> Every transmit filter write that speaks a value
    /// goes through <c>FlexBase.SetTXFilter</c> and speaks from what the radio
    /// object holds after the write: the Radio menu's TX filter items, Home's
    /// TX Filter Low and High fields, the Audio Workshop's TX Filter fields,
    /// the filter layer's transmit side, and the Ctrl+Shift and Ctrl+Alt
    /// bracket chords. A filter property write only: nothing here keys.</para>
    /// <para>Real vendor Radio over the house fake transport, which records
    /// every command; no radio, no window. Expected sentences are literals,
    /// assembled as heard, and DRAFT.</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TxFilterWriteReadoutTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        private readonly VendorRadioFixture _vendor = new VendorRadioFixture("1111-2222-6300-4444");

        public TxFilterWriteReadoutTests()
        {
            _rig.theRadio = _vendor.Radio;
            _rig.q.MainLoop = true;   // the queue holds writes for Drain, as the main loop would run them
        }

        public void Dispose()
        {
            _rig.theRadio = null;   // never FlexBase's to disconnect
            _rig.Dispose();
        }

        private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()));

        private void Tx(int low, int high)
        {
            typeof(Radio).GetField("_txFilterLow", Private)!.SetValue(_vendor.Radio, low);
            typeof(Radio).GetField("_txFilterHigh", Private)!.SetValue(_vendor.Radio, high);
        }

        private void Drain()
        {
            while (_rig.q.Count > 0)
            {
                var item = _rig.q.Dequeue();
                if (item.Item is FlexBase.FunctionDel f) f();
            }
        }

        private string[] TxCommands() => _vendor.Transport.Commands.Where(c => c.Contains("transmit set filter_")).ToArray();

        // ------------------------------------------------------------------
        // The FlexLib patch
        // ------------------------------------------------------------------

        [Fact]
        public void A_low_edge_under_50_hertz_from_the_high_is_sent_as_FlexLib_computed_it()
        {
            Tx(100, 2800);
            _vendor.Radio.TXFilterLow = 2780;
            Assert.Equal(new[] { "transmit set filter_low=2750 filter_high=2800" }, TxCommands().Select(Tail).ToArray());
            Assert.Equal(2750, _vendor.Radio.TXFilterLow);
        }

        [Fact]
        public void A_low_edge_at_or_above_the_high_is_clamped_and_sent_not_dropped()
        {
            Tx(100, 2800);
            _vendor.Radio.TXFilterLow = 3000;
            Assert.Equal(new[] { "transmit set filter_low=2750 filter_high=2800" }, TxCommands().Select(Tail).ToArray());
            Assert.Equal(2750, _vendor.Radio.TXFilterLow);
        }

        [Fact]
        public void A_high_edge_under_50_hertz_from_the_low_is_sent_as_FlexLib_computed_it()
        {
            Tx(100, 2800);
            _vendor.Radio.TXFilterHigh = 120;
            Assert.Equal(new[] { "transmit set filter_low=100 filter_high=150" }, TxCommands().Select(Tail).ToArray());
            Assert.Equal(150, _vendor.Radio.TXFilterHigh);
        }

        /// <summary>The control: inside the rules, FlexLib sends what was asked, as it always did.</summary>
        [Fact]
        public void Inside_the_rules_the_edge_is_sent_as_asked()
        {
            Tx(100, 2800);
            _vendor.Radio.TXFilterLow = 300;
            Assert.Equal(new[] { "transmit set filter_low=300 filter_high=2800" }, TxCommands().Select(Tail).ToArray());
        }

        private static string Tail(string command) => command.Substring(command.IndexOf("transmit set", StringComparison.Ordinal));

        [Fact]
        public void The_patch_is_marked_and_listed()
        {
            string radio = Src("FlexLib_API", "FlexLib", "Radio.cs");
            Assert.Contains("_SetTXFilter(new_cut, _txFilterHigh);", radio, StringComparison.Ordinal);
            Assert.Contains("_SetTXFilter(_txFilterLow, new_cut);", radio, StringComparison.Ordinal);
            Assert.DoesNotContain("_SetTXFilter(value, _txFilterHigh);", radio, StringComparison.Ordinal);
            Assert.DoesNotContain("_SetTXFilter(_txFilterLow, value);", radio, StringComparison.Ordinal);
            Assert.True(System.Text.RegularExpressions.Regex.Matches(radio, @"JJFlex patch \(MIGRATION\.md item 27\)").Count >= 2,
                "both transmit filter setters carry the JJFlex patch comment naming MIGRATION.md item 27");
            string migration = Src("MIGRATION.md");
            Assert.Contains("27. **Transmit filter setters send what they computed", migration, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // The routes: what is said comes from what FlexLib set
        // ------------------------------------------------------------------

        private FilterApplied Write(int? low, int? high)
        {
            FilterApplied? result = null;
            _rig.SetTXFilter(low, high, applied => result = applied);
            Assert.Null(result);   // nothing before the write has run
            Drain();
            Assert.NotNull(result);
            return result!.Value;
        }

        [Fact]
        public void A_low_edge_too_close_to_the_high_says_where_it_stopped()
        {
            Tx(100, 2800);
            var applied = Write(2780, null);
            Assert.Equal((2750, 2800), (applied.Low, applied.High));
            Assert.Equal("TX filter 2750 to 2800, at limit",
                FilterReadouts.Tx(applied, (l, h) => "asked", VerbosityLevel.Chatty));
        }

        [Fact]
        public void A_write_that_changes_nothing_says_the_filter_is_unchanged()
        {
            Tx(100, 150);
            var applied = Write(120, null);   // FlexLib keeps the low at 100: 50 below the high
            Assert.Equal((100, 150), (applied.Low, applied.High));
            Assert.Equal("TX filter unchanged at 100 to 150, width 50 hertz",
                FilterReadouts.Tx(applied, (l, h) => "asked", VerbosityLevel.Chatty));
        }

        [Fact]
        public void A_pair_opens_the_gap_first_and_lands_as_asked()
        {
            Tx(100, 2800);
            var applied = Write(3000, 3500);   // the high must move first, or FlexLib would clamp the low under the old high
            Assert.Equal((3000, 3500), (applied.Low, applied.High));
            Assert.Equal("asked 3000 3500", FilterReadouts.Tx(applied, (l, h) => $"asked {l} {h}", VerbosityLevel.Chatty));
        }

        /// <summary>Every speaking transmit write goes through the applied route.</summary>
        [Fact]
        public void Every_transmit_filter_write_that_speaks_goes_through_the_applied_route()
        {
            string panel = Src("JJFlexWpf", "Controls", "ScreenFieldsPanel.xaml.cs");
            Assert.Contains("_rig.SetTXFilter(v, null, OnHomeTxFilterApplied)", panel, StringComparison.Ordinal);
            Assert.Contains("_rig.SetTXFilter(null, v, OnHomeTxFilterApplied)", panel, StringComparison.Ordinal);
            Assert.DoesNotContain("_rig.TXFilterLow = v", panel, StringComparison.Ordinal);
            Assert.DoesNotContain("_rig.TXFilterHigh = v", panel, StringComparison.Ordinal);

            string workshop = Src("JJFlexWpf", "Dialogs", "AudioWorkshopDialog.TxAudio.cs");
            Assert.Contains("_rig.SetTXFilter(v, null,", workshop, StringComparison.Ordinal);
            Assert.Contains("_rig.SetTXFilter(null, v,", workshop, StringComparison.Ordinal);
            Assert.DoesNotContain("_rig.TXFilterLow = v", workshop, StringComparison.Ordinal);
            Assert.DoesNotContain("_rig.TXFilterHigh = v", workshop, StringComparison.Ordinal);

            string menu = Src("JJFlexWpf", "NativeMenuBar.cs");
            Assert.DoesNotContain("v => Rig.TXFilterLow = v", menu, StringComparison.Ordinal);
            Assert.DoesNotContain("v => Rig.TXFilterHigh = v", menu, StringComparison.Ordinal);
            Assert.Equal(5, CountOf(menu, "SayTxFilterWrite("));   // the four edge items and the helper

            string keys = Src("JJFlexWpf", "KeyCommands.cs");
            Assert.Contains("tx.ApplyThenSay = (l, h, readout, say) =>", keys, StringComparison.Ordinal);
            Assert.Contains("rig.SetTXFilter(l, h, applied =>", keys, StringComparison.Ordinal);
            Assert.Equal(2, CountOf(keys, "rig.SetTXFilter(newLow, null, SayTxLowMoved);"));    // Ctrl+Shift with either bracket
            Assert.Equal(2, CountOf(keys, "rig.SetTXFilter(null, newHigh, SayTxHighMoved);"));  // Ctrl+Alt with either bracket
            Assert.DoesNotContain("rig.TXFilterLow = newLow;", keys, StringComparison.Ordinal);
            Assert.DoesNotContain("rig.TXFilterHigh = newHigh;", keys, StringComparison.Ordinal);
        }

        private static int CountOf(string text, string what)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
            return n;
        }
    }
}
