#nullable enable

using System;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The RF preamp switch and the RF gain field act on the panadapter whose
    /// report chose the value (#889, repair round 2). Real vendor Radio,
    /// Slice and Panadapter objects over the house fake transport, which
    /// records every command; no radio, no window.
    /// </summary>
    public sealed class RfGainDestinationTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        private readonly VendorRadioFixture _vendor = new VendorRadioFixture("1111-2222-6300-4444");
        private readonly Slice _slice;
        private readonly Panadapter _panA;
        private readonly Panadapter _panB;

        public RfGainDestinationTests()
        {
            _panA = NewPan(0x40000000);
            _panB = NewPan(0x40000001);
            _slice = (Slice)Activator.CreateInstance(typeof(Slice), Private, null, new object[] { _vendor.Radio }, null)!;
            typeof(Slice).GetField("_active", Private)!.SetValue(_slice, true);
            _vendor.Radio.SliceList.Add(_slice);
            PointSliceAt(_panA);
            _rig.theRadio = _vendor.Radio;
        }

        public void Dispose()
        {
            _rig.theRadio = null;   // never FlexBase's to disconnect
            _rig.Dispose();
        }

        private Panadapter NewPan(uint streamId)
        {
            var pan = (Panadapter)Activator.CreateInstance(typeof(Panadapter), Private, null, new object[] { _vendor.Radio }, null)!;
            typeof(Panadapter).GetField("_streamID", Private)!.SetValue(pan, streamId);
            _vendor.Radio.PanadapterList.Add(pan);
            return pan;
        }

        private void PointSliceAt(Panadapter pan) =>
            typeof(Slice).GetProperty(nameof(Slice.Panadapter))!.GetSetMethod(nonPublic: true)!.Invoke(_slice, new object[] { pan });

        /// <summary>The vendor's own reply parser, as the radio's rfgain_info answer reaches it.</summary>
        private static void Reply(Panadapter pan, string reply) =>
            typeof(Panadapter).GetMethod("UpdateRFGainInfo", Private)!.Invoke(pan, new object[] { 0, 0u, reply });

        /// <summary>Run what the rig queued, as its main loop would.</summary>
        private void Drain()
        {
            while (_rig.q.Count > 0)
            {
                var item = _rig.q.Dequeue();
                if (item.Item is FlexBase.FunctionDel f) f();
            }
        }

        private int CommandsFor(Panadapter pan) =>
            _vendor.Transport.Commands.Count(c => c.Contains("0x" + ((uint)typeof(Panadapter).GetField("_streamID", Private)!.GetValue(pan)!).ToString("X") + " rfgain="));

        /// <summary>
        /// Codex's blocker. The value is chosen from panadapter A; the active
        /// panadapter changes to B before the queue runs. The write must still
        /// go to A, the panadapter it was chosen for, never to B.
        /// </summary>
        [Fact]
        public void A_gain_chosen_for_one_panadapter_is_written_to_that_panadapter()
        {
            _rig.q.MainLoop = true;
            _rig.RFGain = 20;          // chosen while A is active
            PointSliceAt(_panB);       // the active panadapter changes before the queue runs
            Drain();

            Assert.Equal(20, _panA.RFGain);
            Assert.Equal(0, _panB.RFGain);
            Assert.Equal(1, CommandsFor(_panA));
            Assert.Equal(0, CommandsFor(_panB));
        }

        /// <summary>
        /// The control: with no change of panadapter the write lands where it
        /// always did, and the harness sees the command.
        /// </summary>
        [Fact]
        public void With_the_same_panadapter_active_the_write_lands_there()
        {
            _rig.q.MainLoop = true;
            _rig.RFGain = 10;
            Drain();
            Assert.Equal(10, _panA.RFGain);
            Assert.Equal(1, CommandsFor(_panA));
        }

        /// <summary>
        /// Before the ACTIVE panadapter has reported its RF gain settings, Home
        /// shows the plain number field, even if another panadapter would say
        /// "switch". (The rig-wide fields this also set were removed in the
        /// 2026-11-06 integration pass; nothing read them.)
        /// </summary>
        [Fact]
        public void Until_the_active_panadapter_reports_the_number_field_shows()
        {
            Reply(_panB, "0,20,20,0,20");           // another panadapter reported the switch shape

            Assert.False(FlexBase.IsPreampSwitch(_rig.ActiveRFGainReport, out _));
        }

        /// <summary>A completed reply for the panadapter, through the vendor's parser and the rig's recording, as production wires it.</summary>
        private void CompletedReply(Panadapter pan, string reply)
        {
            System.ComponentModel.PropertyChangedEventHandler h = (s, e) =>
            {
                if (e.PropertyName == "RFGainMarkers") _rig.RecordRFGainReport((Panadapter)s!);
            };
            pan.PropertyChanged += h;
            try { Reply(pan, reply); }
            finally { pan.PropertyChanged -= h; }
        }

        [Fact]
        public void The_active_panadapters_completed_report_decides_the_shape()
        {
            CompletedReply(_panA, "0,20,20,0,20");          // the 6300's answer
            Assert.True(FlexBase.IsPreampSwitch(_rig.ActiveRFGainReport, out int db));
            Assert.Equal(20, db);
            Assert.Same(_panA, _rig.ActiveRFGainReport.Pan);

            CompletedReply(_panA, "-8,32,8,0,16,32");       // a real range, later
            Assert.False(FlexBase.IsPreampSwitch(_rig.ActiveRFGainReport, out _));

            // Another panadapter's report does not speak for the active one.
            CompletedReply(_panB, "0,20,20,0,20");
            Assert.False(FlexBase.IsPreampSwitch(_rig.ActiveRFGainReport, out _));
        }

        [Fact]
        public void Two_markers_over_a_finer_range_keep_the_number_field()
        {
            CompletedReply(_panA, "0,20,5,0,20");
            Assert.False(FlexBase.IsPreampSwitch(_rig.ActiveRFGainReport, out _));
            CompletedReply(_panA, "0,20,20,0,20");
            Assert.True(FlexBase.IsPreampSwitch(_rig.ActiveRFGainReport, out _));
        }

        /// <summary>
        /// The chosen panadapter has closed by the time the queue runs:
        /// nothing is sent, to it or to whatever is active now.
        /// </summary>
        [Fact]
        public void A_gain_for_a_panadapter_that_has_gone_is_not_sent_anywhere()
        {
            _rig.q.MainLoop = true;
            _rig.SetRFGain(_panA, 20);
            PointSliceAt(_panB);
            _vendor.Radio.PanadapterList.Remove(_panA);
            Drain();

            Assert.Equal(0, CommandsFor(_panA));
            Assert.Equal(0, CommandsFor(_panB));
            Assert.Equal(0, _panB.RFGain);
        }

        /// <summary>
        /// Vendor fields that no completed report has delivered are not a
        /// report: the parser publishes its marker array before filling it, so
        /// a raw read can see a half-made answer.
        /// </summary>
        [Fact]
        public void Raw_vendor_fields_without_a_completed_report_are_not_read()
        {
            typeof(Panadapter).GetField("_rf_gain_low", Private)!.SetValue(_panA, 0);
            typeof(Panadapter).GetField("_rf_gain_high", Private)!.SetValue(_panA, 20);
            typeof(Panadapter).GetField("_rf_gain_step", Private)!.SetValue(_panA, 20);
            typeof(Panadapter).GetField("_rf_gain_markers", Private)!.SetValue(_panA, new[] { 0, 20 });

            Assert.False(FlexBase.IsPreampSwitch(_rig.ActiveRFGainReport, out _));
        }

        // ------------------------------------------------------------------
        // #1291: a panadapter that has not reported is known as unreported,
        // its range is asked for, and the Radio menu steps nothing on it.
        // ------------------------------------------------------------------

        private int RangeRequestsFor(Panadapter pan) =>
            _vendor.Transport.Commands.Count(c => c.Contains("display pan rfgain_info 0x"
                + ((uint)typeof(Panadapter).GetField("_streamID", Private)!.GetValue(pan)!).ToString("X")));

        [Fact]
        public void A_panadapter_is_unreported_until_its_completed_report_and_then_reported()
        {
            Assert.False(_rig.HasRFGainReport(_panA));
            Assert.False(_rig.HasRFGainReport(null!));
            CompletedReply(_panA, "-8,32,8,0,16,32");
            Assert.True(_rig.HasRFGainReport(_panA));
            Assert.False(_rig.HasRFGainReport(_panB), "another panadapter's report is not this one's");
        }

        [Fact]
        public void Asking_for_the_range_sends_the_request_for_that_panadapter()
        {
            int before = RangeRequestsFor(_panA);
            _rig.RequestRFGainRange(_panA, "test");
            Assert.Equal(before + 1, RangeRequestsFor(_panA));
            Assert.Equal(0, RangeRequestsFor(_panB));
            _rig.RequestRFGainRange(null!, "test");   // nothing to ask, nothing thrown
        }

        /// <summary>
        /// The Radio menu's RF Gain Up and Down (SOURCE PIN: NativeMenuBar needs
        /// a window). Before any step or switch, an unreported panadapter is
        /// asked for its range and the refusal is said; the step comes after.
        /// </summary>
        [Fact]
        public void The_menu_steps_nothing_on_an_unreported_panadapter()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                FlexBasePartials.RepoRoot(), "JJFlexWpf", "NativeMenuBar.cs"));
            int body = src.IndexOf("private void AdjustRfGain(bool up)", StringComparison.Ordinal);
            Assert.True(body > 0);
            int guard = src.IndexOf("if (report.Pan != null && !Rig.HasRFGainReport(report.Pan))", body, StringComparison.Ordinal);
            int ask = src.IndexOf("Rig.RequestRFGainRange(report.Pan,", body, StringComparison.Ordinal);
            int say = src.IndexOf("Radios.Lexicon" + ".Get(\"audio.fields.rf_gain_range_asked\")", body, StringComparison.Ordinal);
            int ret = src.IndexOf("return;", say, StringComparison.Ordinal);
            int preamp = src.IndexOf("FlexBase.IsPreampSwitch(report, out int onDb)", body, StringComparison.Ordinal);
            int step = src.IndexOf("AdjustValue(\"RF Gain\"", body, StringComparison.Ordinal);
            Assert.True(guard > body && ask > guard && say > ask && ret > say, "the guard asks, says, and returns");
            Assert.True(ret < preamp && preamp < step, "before the switch and the step");
        }
    }
}
