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
    /// With AGC off the radio uses a fixed gain, the off level, and nothing in
    /// JJ Flexible could reach it: every gain control wrote the threshold,
    /// which the radio ignores with AGC off, so the receiver went silent with
    /// no way back but turning AGC on (#888, heard 2026-11-05).
    /// </summary>
    /// <remarks>
    /// Noel's ruling, 2026-11-06 (night question J1): ONE field, its meaning
    /// following the mode, labelled for what it is. Real vendor Radio and
    /// Slice objects over the house fake transport, which records every
    /// command; no radio, no window.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class AgcOffGainFieldTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        private readonly VendorRadioFixture _vendor = new VendorRadioFixture("1111-2222-6300-4444");
        private readonly Slice _slice;

        public AgcOffGainFieldTests()
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

        private static string Body(string text, string signature)
        {
            int at = text.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature + " is not where this test reads it.");
            int open = text.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(at, i - at + 1);
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
        }

        /// <summary>Run what the rig queued, as its main loop would.</summary>
        private void Drain()
        {
            while (_rig.q.Count > 0)
            {
                var item = _rig.q.Dequeue();
                if (item.Item is FlexBase.FunctionDel f) f();
            }
        }

        private void SetSliceField(string field, object value) => typeof(Slice).GetField(field, Private)!.SetValue(_slice, value);

        private int Sent(string what) => _vendor.Transport.Commands.Count(c => c.Contains(" " + what + "="));

        [Theory]
        [InlineData(AGCMode.Off, true)]
        [InlineData(AGCMode.Slow, false)]
        [InlineData(AGCMode.Medium, false)]
        [InlineData(AGCMode.Fast, false)]
        [InlineData(AGCMode.None, false)]
        public void The_field_is_the_off_gain_only_while_AGC_is_off(AGCMode mode, bool offLevel)
            => Assert.Equal(offLevel, FlexBase.AgcFieldIsOffLevel(mode));

        [Fact]
        public void Its_label_names_what_it_is()
        {
            // DRAFT wording for the off gain; the threshold's label is unchanged.
            Assert.Equal("AGC Off Gain", FlexBase.AgcFieldLabel(true));
            Assert.Equal("AGC Threshold", FlexBase.AgcFieldLabel(false));
        }

        /// <summary>
        /// The defect: with AGC off, moving the field wrote the threshold. It
        /// writes the off level now, and the threshold is left alone.
        /// </summary>
        [Fact]
        public void With_AGC_off_the_field_writes_the_off_level_and_not_the_threshold()
        {
            SetSliceField("_agc_mode", AGCMode.Off);
            _rig.SetAgcFieldValue(FlexBase.AgcFieldIsOffLevel(_rig.AGCSpeed), 60);
            Drain();
            Assert.Equal(1, Sent("agc_off_level"));
            Assert.Contains(_vendor.Transport.Commands, c => c.Contains(" agc_off_level=60"));
            Assert.Equal(0, Sent("agc_threshold"));
            Assert.Equal(60, _rig.AGCOffLevel);
        }

        /// <summary>The control: with AGC on, the field is the threshold, exactly as before.</summary>
        [Fact]
        public void With_AGC_on_the_field_writes_the_threshold_as_before()
        {
            SetSliceField("_agc_mode", AGCMode.Medium);
            _rig.SetAgcFieldValue(FlexBase.AgcFieldIsOffLevel(_rig.AGCSpeed), 40);
            Drain();

            Assert.Equal(1, Sent("agc_threshold"));
            Assert.Equal(0, Sent("agc_off_level"));
        }

        [Fact]
        public void The_field_reads_the_value_its_label_names()
        {
            SetSliceField("_agc_threshold", 65);
            SetSliceField("_agc_off_level", 35);
            Assert.Equal(35, _rig.AgcFieldValue(true));
            Assert.Equal(65, _rig.AgcFieldValue(false));
        }

        /// <summary>
        /// Home and the Radio menu use the one field. Pinned at the source,
        /// because both are WPF or native menus and nothing here builds them.
        /// </summary>
        [Fact]
        public void Home_and_the_radio_menu_read_and_write_the_field_by_its_mode()
        {
            string panel = Src("JJFlexWpf", "Controls", "ScreenFieldsPanel.xaml.cs");
            Assert.Contains("_rig.SetAgcFieldValue(_agcFieldIsOffLevel, v)", panel, StringComparison.Ordinal);
            Assert.DoesNotContain("_rig.AGCThreshold = v", panel, StringComparison.Ordinal);
            string poll = Body(panel, "private void PollReceiver()");
            Assert.Contains("bool agcOffLevel = FlexBase.AgcFieldIsOffLevel(agcMode);", poll, StringComparison.Ordinal);
            Assert.Contains("_agcThresholdControl.Value = _rig.AgcFieldValue(agcOffLevel);", poll, StringComparison.Ordinal);
            Assert.Contains("_agcFieldIsOffLevel = agcOffLevel;", poll, StringComparison.Ordinal);
            Assert.Contains("_agcThresholdControl.Label = FlexBase.AgcFieldLabel(agcOffLevel);", poll, StringComparison.Ordinal);
            Assert.DoesNotContain("_agcThresholdControl.Value = _rig.AGCThreshold;", poll, StringComparison.Ordinal);

            string menu = Src("JJFlexWpf", "NativeMenuBar.cs");
            Assert.Contains("AddWired(parent, \"AGC Threshold Up\", () => AdjustAgcField(FlexBase.AGCThresholdIncrement));", menu, StringComparison.Ordinal);
            Assert.Contains("AddWired(parent, \"AGC Threshold Down\", () => AdjustAgcField(-FlexBase.AGCThresholdIncrement));", menu, StringComparison.Ordinal);
            string adjust = Body(menu, "private void AdjustAgcField(int step)");
            Assert.Contains("FlexBase.AgcFieldIsOffLevel(Rig.AGCSpeed)", adjust, StringComparison.Ordinal);
            Assert.Contains("Rig.SetAgcFieldValue(offLevel, v)", adjust, StringComparison.Ordinal);

            // The vendored Slice is not edited; the wrapper goes through it.
            string flexBase = FlexBasePartials.JoinedText();
            Assert.Contains("theRadio.ActiveSlice.AGCOffLevel = value;", flexBase, StringComparison.Ordinal);
        }
    }
}
