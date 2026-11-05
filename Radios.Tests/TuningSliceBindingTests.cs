using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The tuning core, through the production setters with the command queue
    /// parked (#642, repair round 3; ruled by Noel 2026-11-04 to be fixed in
    /// this round). Codex's second re-check reproduced three gaps in an
    /// unconnected fixture: a queued frequency write resolved its slice when it
    /// RAN, so a tune pressed on A landed on B selected meanwhile; the echo
    /// guard armed against the old selection's number, so A's own stale echo
    /// rewound a step taken while the selection settled; and the receive and
    /// transmit caches held different optimistic values for the same slice,
    /// so F and the title disagreed. These replay each, plus a fast run of
    /// steps drained part way.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TuningSliceBindingTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private readonly List<FlexBase> _rigs = new();

        public TuningSliceBindingTests() => Lexicon.Load(Lexicon.Partitions);

        public void Dispose()
        {
            foreach (FlexBase r in _rigs) { try { r.Dispose(); } catch { } }
        }

        private FlexBase Rig()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            rig.q.MainLoop = true;   // parked: nothing drains it but the test
            _rigs.Add(rig);
            return rig;
        }

        private static Slice ReadySlice(int index, bool tx, bool active, double mhz)
        {
            var radio = (Radio)Activator.CreateInstance(typeof(Radio), Private, null, new object[] { false }, null)!;
            var slice = (Slice)Activator.CreateInstance(typeof(Slice), Private, null, new object[] { radio }, null)!;
            Set(slice, "_index", index);
            Set(slice, "_letter", ((char)('A' + index)).ToString());
            Set(slice, "_isTransmitSlice", tx);
            Set(slice, "_active", active);
            Set(slice, "_demodMode", "USB");
            Set(slice, "_freq", mhz);
            Set(slice, "_lock", false);   // a locked slice refuses every tune
            return slice;
        }

        private static void Set(object o, string field, object value)
            => o.GetType().GetField(field, Private)!.SetValue(o, value);

        private static ObservationBinding Binding() => new(new object(), new ConnectionAttempt("test-serial"));

        private static void SliceArrives(FlexBase rig, Slice slice)
        {
            typeof(FlexBase).GetField("clientHandle", Private)!.SetValue(rig, slice.ClientHandle);
            typeof(FlexBase).GetMethod("sliceAdded", Private)!.Invoke(rig, new object[] { slice, Binding() });
        }

        private static void SliceGoes(FlexBase rig, Slice slice)
            => typeof(FlexBase).GetMethod("sliceRemoved", Private)!.Invoke(rig, new object[] { slice, Binding() });

        /// <summary>The radio's own report of a slice's frequency, as the Freq notification delivers it.</summary>
        private static void SliceReportsFreq(FlexBase rig, Slice slice, double mhz)
        {
            Set(slice, "_freq", mhz);
            try
            {
                typeof(FlexBase).GetMethod("slicePropertyChangedHandler", Private)!
                    .Invoke(rig, new object[] { slice, new PropertyChangedEventArgs("Freq") });
            }
            catch (TargetInvocationException) { /* the fixture has no filter adapter; the cache is written first */ }
        }

        private static ulong Hz(Slice s) => (ulong)Math.Round(s.Freq * 1_000_000d);

        /// <summary>Run every queued command, as the main loop does, item by item.</summary>
        private static void Drain(FlexBase rig) { while (rig.q.Count > 0) RunOne(rig); }

        private static string RunOne(FlexBase rig)
        {
            var item = rig.q.Dequeue();
            try { if (item.Item is FlexBase.FunctionDel f) f(); }
            catch (Exception) { /* the fixture has no filter adapter; the main loop traces and goes on */ }
            return item.Name;
        }

        private static string Mhz(ulong hz) => (hz / 1_000_000.0).ToString("0.000", CultureInfo.InvariantCulture);

        // ── A queued write lands on the slice it was pressed on ──

        [Fact]
        public void A_receive_tune_pressed_on_A_lands_on_A_when_B_is_selected_before_it_runs()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            Slice b = ReadySlice(1, tx: false, active: false, mhz: 7.150);
            SliceArrives(rig, a);
            SliceArrives(rig, b);
            Assert.Equal(0, rig.RXVFO);

            rig.RXFrequency = 14_100_100;   // the step, pressed on A
            rig.RXVFO = 1;                  // B selected before the queue runs
            Drain(rig);

            Assert.Equal(14_100_100UL, Hz(a));
            Assert.Equal(7_150_000UL, Hz(b));
        }

        [Fact]
        public void A_receive_tune_with_nothing_in_the_way_lands_on_the_selected_slice()
        {
            // Positive control: the fixture can see a write land.
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            Slice b = ReadySlice(1, tx: false, active: false, mhz: 7.150);
            SliceArrives(rig, a);
            SliceArrives(rig, b);

            rig.RXFrequency = 14_100_100;
            Drain(rig);

            Assert.Equal(14_100_100UL, Hz(a));
            Assert.Equal(7_150_000UL, Hz(b));
        }

        [Fact]
        public void A_transmit_tune_pressed_on_A_lands_on_A_when_the_transmit_slice_moves_before_it_runs()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            Slice b = ReadySlice(1, tx: false, active: false, mhz: 7.150);
            SliceArrives(rig, a);
            SliceArrives(rig, b);
            Assert.Equal(0, rig.TXVFO);

            rig.TXFrequency = 14_100_100;
            rig.TXVFO = 1;
            Drain(rig);

            Assert.Equal(14_100_100UL, Hz(a));
            Assert.Equal(7_150_000UL, Hz(b));
        }

        [Fact]
        public void A_tune_whose_slice_was_removed_before_it_ran_lands_on_no_other_slice()
        {
            // B selected and tuned, then B removed: the selection falls back to
            // A, and the old write, resolved when it ran, tuned A instead.
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: false, mhz: 14.100);
            Slice b = ReadySlice(1, tx: false, active: true, mhz: 7.150);
            SliceArrives(rig, a);
            SliceArrives(rig, b);
            Assert.Equal(1, rig.RXVFO);

            rig.RXFrequency = 7_150_100;
            SliceGoes(rig, b);
            Drain(rig);

            Assert.Equal(14_100_000UL, Hz(a));
        }

        // ── A stale echo during a selection does not rewind a step ──

        [Fact]
        public void A_step_taken_while_a_new_selection_settles_survives_that_slices_stale_echo()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: false, mhz: 14.100);
            Slice b = ReadySlice(1, tx: false, active: true, mhz: 7.150);
            SliceArrives(rig, a);
            SliceArrives(rig, b);
            Assert.Equal(7_150_000UL, rig.RXFrequency);

            rig.RXVFO = 0;                                   // A selected; its Active still queued
            Set(b, "_active", false);
            Set(a, "_active", true);
            rig.RXFrequency = rig.RXFrequency + 100;         // a step on A, from A's own 14.100
            Assert.Equal(14_100_100UL, rig.RXFrequency);

            SliceReportsFreq(rig, a, 14.100);                // A's in-flight report from before the step
            Assert.Equal(14_100_100UL, rig.RXFrequency);

            // The ActiveSlice refresh reads A's own (still old) frequency too.
            typeof(FlexBase).GetMethod("NoteActiveSliceAsFound", Private)!.Invoke(rig, new object[] { a });
            Assert.Equal(14_100_100UL, rig.RXFrequency);

            rig.RXFrequency = rig.RXFrequency + 100;         // the next step goes on from there
            Assert.Equal(14_100_200UL, rig.RXFrequency);
            Drain(rig);
            Assert.Equal(14_100_200UL, Hz(a));
        }

        [Fact]
        public void A_report_nobody_asked_for_is_still_believed_while_a_step_is_pending()
        {
            // Control: another client, the knob or a band-edge clamp moves A to
            // a number neither the step nor its predecessor was. The radio is
            // the authority.
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            SliceArrives(rig, a);

            rig.RXFrequency = 14_100_100;
            SliceReportsFreq(rig, a, 14.250);
            Assert.Equal(14_250_000UL, rig.RXFrequency);
        }

        // ── A fast run of steps ──

        [Fact]
        public void A_fast_run_of_steps_drained_part_way_loses_no_step()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            SliceArrives(rig, a);

            for (int i = 0; i < 3; i++) rig.RXFrequency = rig.RXFrequency + 100;
            Assert.Equal(14_100_300UL, rig.RXFrequency);

            Assert.Equal("RXFreq", RunOne(rig));             // the first step reaches the slice
            Assert.Equal(14_100_300UL, rig.RXFrequency);     // and its own echo rewinds nothing

            rig.RXFrequency = rig.RXFrequency + 100;
            Assert.Equal(14_100_400UL, rig.RXFrequency);
            Drain(rig);
            Assert.Equal(14_100_400UL, Hz(a));
            Assert.Equal(14_100_400UL, rig.RXFrequency);
        }

        [Fact]
        public void A_fast_run_of_steps_on_the_selected_slice_steps_from_each_result()
        {
            // The arithmetic on the selected slice, undrained: each step starts
            // from the last one's result.
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: false, active: false, mhz: 14.100);
            Slice b = ReadySlice(1, tx: true, active: true, mhz: 7.150);
            SliceArrives(rig, a);
            SliceArrives(rig, b);
            for (int i = 0; i < 5; i++) rig.RXFrequency = rig.RXFrequency + 1000;
            Assert.Equal(7_155_000UL, rig.RXFrequency);
            Drain(rig);
            Assert.Equal(7_155_000UL, Hz(b));
            Assert.Equal(14_100_000UL, Hz(a));
        }

        // ── One slice, one optimistic value ──

        [Fact]
        public void A_transmit_tune_on_the_selected_slice_is_the_one_number_F_and_the_frequencies_agree_on()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            SliceArrives(rig, a);

            rig.TXFrequency = 14_105_000;                    // still queued

            SliceRoles roles = SliceRoles.Of(rig);
            Assert.True(roles.Same);
            Assert.Equal(14_105_000UL, roles.SelectedHz);
            Assert.Equal(14_105_000UL, roles.TransmitHz);
            Assert.Equal(14_105_000UL, rig.TXFrequency);
            Assert.Equal(14_105_000UL, rig.RXFrequency);
            Assert.Equal(Lexicon.Get("connect.home.frequency", ("freqText", "14.105"), ("slice", "A")),
                         FrequencyKeyReadout.Sentence(FrequencyKeySlices.Transmit, roles, Mhz));
        }

        [Fact]
        public void A_receive_step_on_the_transmit_slice_moves_the_transmit_frequency_with_it()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            SliceArrives(rig, a);

            rig.RXFrequency = 14_100_100;

            SliceRoles roles = SliceRoles.Of(rig);
            Assert.Equal(14_100_100UL, roles.SelectedHz);
            Assert.Equal(14_100_100UL, roles.TransmitHz);
            Assert.Equal(14_100_100UL, rig.TXFrequency);
        }

        [Fact]
        public void On_a_split_each_slice_keeps_its_own_number()
        {
            // Control: one value per slice, not one value for both slices.
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: false, mhz: 14.100);
            Slice b = ReadySlice(1, tx: false, active: true, mhz: 7.150);
            SliceArrives(rig, a);
            SliceArrives(rig, b);

            rig.RXFrequency = 7_150_100;
            rig.TXFrequency = 14_100_500;

            SliceRoles roles = SliceRoles.Of(rig);
            Assert.False(roles.Same);
            Assert.Equal(7_150_100UL, roles.SelectedHz);
            Assert.Equal(14_100_500UL, roles.TransmitHz);
            Assert.Equal(7_150_100UL, rig.RXFrequency);
            Assert.Equal(14_100_500UL, rig.TXFrequency);
        }
    }
}
