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
    }
}
