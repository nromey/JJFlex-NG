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

        // ── Repair round 4 (Codex's third re-check) ──

        /// <summary>
        /// Codex's finding 1: FlexLib's Freq setter raises the slice's own
        /// Freq notification at once, after sending and before any reply, so
        /// it is our own send, not the radio confirming. Treating it as a
        /// confirmation disarmed the guard when the queue drained, and the
        /// radio's first delayed report then rewound a fully sent run of
        /// steps, losing the steps after it. Only a report that came from the
        /// radio settles the guard.
        /// </summary>
        [Fact]
        public void A_fully_sent_run_of_steps_is_not_rewound_by_the_radios_delayed_reports()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            SliceArrives(rig, a);

            for (int i = 0; i < 3; i++) rig.RXFrequency = rig.RXFrequency + 100;
            Drain(rig);                                      // every step sent; the radio has said nothing
            Assert.Equal(14_100_300UL, Hz(a));
            Assert.Equal(14_100_300UL, rig.RXFrequency);

            SliceReportsFreq(rig, a, 14.1001);               // the radio's report of the first step, late
            Assert.Equal(14_100_300UL, rig.RXFrequency);
            Assert.Equal(14_100_300UL, rig.TXFrequency);
            SliceRoles roles = SliceRoles.Of(rig);
            Assert.Equal(14_100_300UL, roles.SelectedHz);
            Assert.Equal(14_100_300UL, roles.TransmitHz);

            rig.RXFrequency = rig.RXFrequency + 100;         // the next step goes on from the last
            Assert.Equal(14_100_400UL, rig.RXFrequency);
            Drain(rig);
            SliceReportsFreq(rig, a, 14.1002);
            SliceReportsFreq(rig, a, 14.1003);
            Assert.Equal(14_100_400UL, rig.RXFrequency);
            SliceReportsFreq(rig, a, 14.1004);               // the radio confirms the last
            Assert.Equal(14_100_400UL, rig.RXFrequency);

            // Control: the RADIO's report settled the guard, so a later report
            // of an old value is believed again (another client, the knob).
            SliceReportsFreq(rig, a, 14.1001);
            Assert.Equal(14_100_100UL, rig.RXFrequency);
        }

        /// <summary>
        /// Keeping the guard armed until the RADIO confirms means a report the
        /// guard rejects as stale (a band-edge clamp back to the old number,
        /// say) is not re-sent by FlexLib, which raises nothing for a value it
        /// already holds. So once our last write has gone out and nothing
        /// confirmed it within the give-up window, readers believe the vendor's
        /// number. The control: with no report at all, that number is our send.
        /// </summary>
        [Fact]
        public void A_sent_tune_nothing_confirmed_reads_the_radios_truth_after_the_give_up_window()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            SliceArrives(rig, a);
            rig.RXFrequency = 14_100_100;
            Drain(rig);
            SliceReportsFreq(rig, a, 14.100);                // the radio clamps back to the old number
            Assert.Equal(14_100_100UL, rig.RXFrequency);     // inside the window: rejected as stale

            FlexBase quiet = Rig();
            Slice q = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            SliceArrives(quiet, q);
            quiet.RXFrequency = 14_100_100;
            Drain(quiet);

            System.Threading.Thread.Sleep(FrequencyEchoGuard.GiveUpMs + 150);
            Assert.Equal(14_100_000UL, rig.RXFrequency);
            Assert.Equal(14_100_000UL, rig.TXFrequency);
            Assert.Equal(14_100_000UL, SliceRoles.Of(rig).SelectedHz);
            Assert.Equal(14_100_100UL, quiet.RXFrequency);
        }

        /// <summary>
        /// The other refreshes read the slice's own number, which our own send
        /// has already written locally, so they cannot confirm a tune either
        /// (what else answers the same wrong way): an ActiveSlice refresh after
        /// a fully sent run keeps the guard armed, and the delayed report is
        /// still rejected.
        /// </summary>
        [Fact]
        public void A_refresh_from_the_slices_own_number_after_a_send_is_not_a_confirmation()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            SliceArrives(rig, a);

            for (int i = 0; i < 3; i++) rig.RXFrequency = rig.RXFrequency + 100;
            Drain(rig);
            typeof(FlexBase).GetMethod("NoteActiveSliceAsFound", Private)!.Invoke(rig, new object[] { a });
            SliceReportsFreq(rig, a, 14.1001);
            Assert.Equal(14_100_300UL, rig.RXFrequency);
        }

        /// <summary>
        /// Codex's finding 2: a slice's pending value is its value even when
        /// neither cache describes it any more. A selected with a step pending,
        /// B selected and stepped (both caches now B's), then A selected again
        /// before anything refreshes: A reads its pending step, and the next
        /// step goes on from there.
        /// </summary>
        [Fact]
        public void A_pending_step_on_a_slice_both_caches_have_left_is_still_that_slices_value()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: false, active: true, mhz: 14.100);
            Slice b = ReadySlice(1, tx: true, active: false, mhz: 7.150);
            SliceArrives(rig, a);
            SliceArrives(rig, b);
            Assert.Equal(0, rig.RXVFO);

            rig.RXFrequency = rig.RXFrequency + 100;         // A: 14.1001, queued
            rig.RXVFO = 1;                                   // B selected
            rig.RXFrequency = rig.RXFrequency + 100;         // B: 7.1501, queued; both caches now B's
            rig.RXVFO = 0;                                   // A again, before any refresh

            Assert.Equal(14_100_100UL, rig.RXFrequency);
            Assert.Equal(14_100_100UL, SliceRoles.Of(rig).SelectedHz);
            Assert.Equal(7_150_100UL, rig.TXFrequency);

            rig.RXFrequency = rig.RXFrequency + 100;
            Assert.Equal(14_100_200UL, rig.RXFrequency);
            Drain(rig);
            Assert.Equal(14_100_200UL, Hz(a));
            Assert.Equal(7_150_100UL, Hz(b));
        }

        private static PanAdapterManager Pan(FlexBase rig)
        {
            var pan = (PanAdapterManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PanAdapterManager));
            typeof(PanAdapterManager).GetField("rig", Private)!.SetValue(pan, rig);
            return pan;
        }

        /// <summary>
        /// Codex's finding 3: the panadapter's goto went to the radio's
        /// ActiveSlice, which lags the operator's selection (RXVFO moves at
        /// once, Active is queued). It now goes through the receive setter, so
        /// it lands on the slice selected when it was pressed, published and
        /// guarded like every other tune.
        /// </summary>
        [Fact]
        public void A_pan_goto_lands_on_the_slice_selected_when_it_was_pressed()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            Slice b = ReadySlice(1, tx: false, active: false, mhz: 7.150);
            SliceArrives(rig, a);
            SliceArrives(rig, b);

            rig.RXVFO = 1;                                   // B selected; A is still the radio's Active
            Pan(rig).gotoFreq(7.200);
            Assert.Equal(7_200_000UL, rig.RXFrequency);      // read back at once, like any tune
            rig.RXVFO = 0;                                   // the selection moves on before the queue runs
            Drain(rig);

            Assert.Equal(7_200_000UL, Hz(b));
            Assert.Equal(14_100_000UL, Hz(a));               // the transmit slice did not move
        }

        /// <summary>
        /// Codex's finding 4: a pan goto, transmit or receive, on a slice
        /// removed before the queue runs sends nothing to that slice's
        /// reusable index and nothing to any other slice. So does a slice copy.
        /// </summary>
        [Fact]
        public void A_pan_goto_or_a_slice_copy_onto_a_slice_removed_before_it_runs_tunes_nothing()
        {
            FlexBase rig = Rig();
            Slice a = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            Slice b = ReadySlice(1, tx: false, active: false, mhz: 7.150);
            SliceArrives(rig, a);
            SliceArrives(rig, b);

            rig.ShowingXmitFrequency = true;
            Pan(rig).gotoFreq(14.200);                       // the transmit goto, on A
            SliceGoes(rig, a);
            Drain(rig);
            Assert.Equal(14_100_000UL, Hz(a));
            Assert.Equal(7_150_000UL, Hz(b));

            FlexBase rig2 = Rig();
            Slice c = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            Slice d = ReadySlice(1, tx: false, active: false, mhz: 7.150);
            SliceArrives(rig2, c);
            SliceArrives(rig2, d);
            Pan(rig2).gotoFreq(14.250);                      // the receive goto, on C
            SliceGoes(rig2, c);
            Drain(rig2);
            Assert.Equal(14_100_000UL, Hz(c));
            Assert.Equal(7_150_000UL, Hz(d));

            FlexBase rig3 = Rig();
            Slice e = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            Slice f = ReadySlice(1, tx: false, active: false, mhz: 7.150);
            SliceArrives(rig3, e);
            SliceArrives(rig3, f);
            rig3.CopyVFO(0, 1);                              // copy E onto F
            SliceGoes(rig3, f);
            Drain(rig3);
            Assert.Equal(7_150_000UL, Hz(f));

            // Control: with nothing removed, the copy does tune.
            FlexBase rig4 = Rig();
            Slice g = ReadySlice(0, tx: true, active: true, mhz: 14.100);
            Slice h = ReadySlice(1, tx: false, active: false, mhz: 7.150);
            SliceArrives(rig4, g);
            SliceArrives(rig4, h);
            rig4.CopyVFO(0, 1);
            Drain(rig4);
            Assert.Equal(14_100_000UL, Hz(h));
        }

        /// <summary>
        /// The pan goto reads no ActiveSlice and writes no Freq of its own: it
        /// goes through the setters, which bind the slice at the press and
        /// check its lifetime at the write.
        /// </summary>
        [Fact]
        public void The_pan_goto_tunes_only_through_the_frequency_setters()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "Radios", "PanAdapterManager.cs"));
            int start = src.IndexOf("public void gotoFreq(", StringComparison.Ordinal);
            int end = src.IndexOf("public bool checkForRangeJump(", start, StringComparison.Ordinal);
            string body = src.Substring(start, end - start);
            Assert.DoesNotContain(".Freq =", body);
            Assert.DoesNotContain("ActiveSlice", body);
            Assert.Contains("rig.TXFrequency = ", body);
            Assert.Contains("rig.RXFrequency = ", body);
        }
    }
}
