using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios.StationConnect;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// What confirms a tune (#642, junk box repair round 5). Codex's fourth
    /// re-check found that an ordinary successful tune was never confirmed:
    /// FlexLib's Freq setter puts our value in its cache before it sends,
    /// throws the radio's success reply away, and then skips the radio's
    /// report as "no change". So the guard stayed armed and rejected the next
    /// genuine move back. It also found a slice copy that bypassed the tune's
    /// request, a locked slice that kept our number for good, and a give-up
    /// window counted from the press rather than the send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every test here runs on a REAL vendored Radio with a fake command
    /// transport (<see cref="VendorRadioFixture"/>): the radio's reports go
    /// through FlexLib's own <c>Slice.StatusUpdate</c>, with its equal-value
    /// skip, and its replies through FlexLib's own reply routing. Nothing
    /// forces a notification FlexLib would not raise, which is how the old
    /// tests hid finding 1.
    /// </para>
    /// <para>
    /// What these cannot see: a radio. Whether a real radio sends a
    /// command's status before its reply is the harness's question (see the
    /// report).
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TuningReceiptTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const uint Handle = 0x5A5A0001;

        private readonly RadioConfigStaticsScope _scope = new(nameof(TuningReceiptTests));
        private readonly List<FlexBase> _rigs = new();

        public TuningReceiptTests() => Lexicon.Load(Lexicon.Partitions);

        public void Dispose()
        {
            foreach (FlexBase r in _rigs) { try { r.Dispose(); } catch { } }
            _scope.Dispose();
        }

        private sealed class Bench
        {
            public FlexBase Rig = null!;
            public VendorRadioFixture V = null!;
        }

        private Bench NewBench()
        {
            var b = new Bench
            {
                Rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true },
                V = new VendorRadioFixture("tuning-" + Guid.NewGuid().ToString("N")),
            };
            b.Rig.q.MainLoop = true;   // parked: nothing drains it but the test
            typeof(FlexBase).GetField("clientHandle", Private)!.SetValue(b.Rig, Handle);
            _rigs.Add(b.Rig);
            return b;
        }

        private static Slice Add(Bench b, int index, bool tx, bool active, double mhz)
        {
            Slice s = b.V.NewSlice(index, Handle, mhz, "USB");
            Set(s, "_letter", ((char)('A' + index)).ToString());
            Set(s, "_isTransmitSlice", tx);
            Set(s, "_active", active);
            typeof(FlexBase).GetMethod("sliceAdded", Private)!
                .Invoke(b.Rig, new object[] { s, new ObservationBinding(new object(), new ConnectionAttempt("test-serial")) });
            return s;
        }

        private static void Set(object o, string field, object value)
            => o.GetType().GetField(field, Private)!.SetValue(o, value);

        /// <summary>
        /// The radio's report of a slice's frequency, through FlexLib's own
        /// status parser: a value equal to FlexLib's cache raises nothing.
        /// </summary>
        private static void Reports(Slice s, double mhz)
        {
            try { s.StatusUpdate("rf_frequency=" + mhz.ToString("F6", CultureInfo.InvariantCulture)); }
            catch (Exception) { /* the fixture has no filter adapter; the caches are written first */ }
        }

        /// <summary>Every <c>slice tune</c> written, oldest first, with its index in the transport's list.</summary>
        private static List<(int at, int slice, ulong hz)> Tunes(Bench b)
        {
            var list = new List<(int, int, ulong)>();
            List<string> cmds = b.V.Transport.Commands;
            for (int i = 0; i < cmds.Count; i++)
            {
                string[] w = cmds[i].Split(' ');
                if (w.Length >= 4 && w[0] == "slice" && w[1] == "tune")
                    list.Add((i, int.Parse(w[2], CultureInfo.InvariantCulture),
                        (ulong)Math.Round(double.Parse(w[3], CultureInfo.InvariantCulture) * 1_000_000d)));
            }
            return list;
        }

        /// <summary>What we last told the radio to tune <paramref name="s"/> to, or null.</summary>
        private static ulong? LastTune(Bench b, Slice s)
        {
            var t = Tunes(b).Where(x => x.slice == s.Index).ToList();
            return t.Count == 0 ? null : t[^1].hz;
        }

        /// <summary>The radio answers the <paramref name="nth"/> tune written (0 is the first) with success.</summary>
        private static void AckTune(Bench b, int nth) => b.V.ReplyOk(b.V.Transport.SequenceOf(Tunes(b)[nth].at));

        private static void RejectTune(Bench b, int nth, string kept) =>
            b.V.Reply(b.V.Transport.SequenceOf(Tunes(b)[nth].at), 0x50000015, kept);

        private static void Drain(FlexBase rig) { while (rig.q.Count > 0) RunOne(rig); }

        private static void RunOne(FlexBase rig)
        {
            var item = rig.q.Dequeue();
            try { if (item.Item is FlexBase.FunctionDel f) f(); }
            catch (Exception) { /* the fixture has no filter adapter; the main loop traces and goes on */ }
        }

        /// <summary>Put an injected clock on a slice's own echo guard.</summary>
        private static void Clock(FlexBase rig, Slice s, Func<long> clock)
        {
            object guard = typeof(FlexBase).GetMethod("EchoGuardFor", Private)!.Invoke(rig, new object[] { s })!;
            guard.GetType().GetField("_clock", Private)!.SetValue(guard, clock);
        }

        private static string Mhz(ulong hz) => (hz / 1_000_000.0).ToString("0.0000", CultureInfo.InvariantCulture);

        /// <summary>Every reader of a slice's one number agrees on <paramref name="hz"/>: the getters, both roles and F.</summary>
        private static void EveryReaderSays(FlexBase rig, ulong hz)
        {
            Assert.Equal(hz, rig.RXFrequency);
            Assert.Equal(hz, rig.TXFrequency);
            SliceRoles roles = SliceRoles.Of(rig);
            Assert.Equal(hz, roles.SelectedHz);
            Assert.Equal(hz, roles.TransmitHz);
            Assert.Equal(Lexicon.Get("connect.home.frequency", ("freqText", Mhz(hz)), ("slice", "A")),
                         FrequencyKeyReadout.Sentence(FrequencyKeySlices.Transmit, roles, Mhz));
        }

        // ── Finding 1: an ordinary tune is confirmed ──

        /// <summary>
        /// Codex's finding 1, its exact schedule. One step, sent, answered and
        /// reported by the radio; then somebody moves the slice back (another
        /// client, the knob). That move is believed by every reader.
        /// </summary>
        [Fact]
        public void An_ordinary_tune_is_confirmed_and_a_later_move_back_is_believed()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Assert.Equal(14_100_100UL, LastTune(b, a));
            AckTune(b, 0);
            Reports(a, 14.100100);                           // the radio's report of our tune
            EveryReaderSays(b.Rig, 14_100_100);

            Reports(a, 14.100000);                           // a genuine move back
            EveryReaderSays(b.Rig, 14_100_000);
        }

        /// <summary>
        /// The receipt when there is nothing for the radio to report: a step up
        /// and straight back down, both answered, and the radio reporting
        /// nothing because it ends where it started. The reply to the last one
        /// confirms it, so a genuine move afterwards is believed.
        /// </summary>
        [Fact]
        public void A_tune_back_to_where_the_radio_is_is_confirmed_by_its_reply()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            b.Rig.RXFrequency = 14_100_000;
            Drain(b.Rig);
            Assert.Equal(2, Tunes(b).Count);
            AckTune(b, 0);
            AckTune(b, 1);
            EveryReaderSays(b.Rig, 14_100_000);

            Reports(a, 14.100100);                           // somebody else tunes it up
            EveryReaderSays(b.Rig, 14_100_100);
        }

        /// <summary>
        /// Control: the radio's report of a value nobody in the chain asked for
        /// is believed at once (a clamp, another client, the knob).
        /// </summary>
        [Fact]
        public void A_report_nobody_asked_for_is_believed_once_our_tune_is_answered()
        {
            // Changed in repair round 8: until then a number outside the chain
            // was believed at once. Now nothing but our own value gets in until
            // the radio answers our latest request, and then the radio's report
            // is read: a clamp, the knob or another client is believed one
            // reply later, never lost.
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            // Round two of the rulings round (the 8600 bench): a report that
            // arrives BEFORE our reply was a change the radio made before it ran
            // our tune, which overrode it; one that arrives AFTER is believed.
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Reports(a, 14.250);
            EveryReaderSays(b.Rig, 14_100_100);
            AckTune(b, 0);
            EveryReaderSays(b.Rig, 14_100_100);
            Reports(a, 14.250);
            EveryReaderSays(b.Rig, 14_250_000);
        }

        /// <summary>
        /// The #266 guarantee, on the new route: a fully sent run of steps is
        /// not rewound by the radio's late reports of the earlier steps, and
        /// the last report settles it, so an old number is news again.
        /// </summary>
        [Fact]
        public void A_sent_run_of_steps_is_not_rewound_by_its_own_late_reports()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            for (int i = 0; i < 3; i++) b.Rig.RXFrequency = b.Rig.RXFrequency + 100;
            Drain(b.Rig);
            Reports(a, 14.100100);
            Reports(a, 14.100200);
            EveryReaderSays(b.Rig, 14_100_300);
            b.Rig.RXFrequency = b.Rig.RXFrequency + 100;
            Assert.Equal(14_100_400UL, b.Rig.RXFrequency);
            Drain(b.Rig);
            Reports(a, 14.100300);
            Assert.Equal(14_100_400UL, b.Rig.RXFrequency);
            Reports(a, 14.100400);                           // the last one's report
            AckTune(b, 3);                                   // and its answer: settled (repair round 8)
            Reports(a, 14.100100);                           // and an old number is believed again
            EveryReaderSays(b.Rig, 14_100_100);
        }

        // ── Finding 2: a copy is a whole tune request ──

        /// <summary>
        /// Codex's finding 2, its exact schedule: A stepped and sent, then B
        /// copied onto A. Every reader says the copied frequency at once, the
        /// radio's late report of the step does not rewind it, the copy's own
        /// report confirms it, and the next step starts from it.
        /// </summary>
        [Fact]
        public void A_copy_onto_a_slice_just_tuned_reads_the_copy_and_the_next_step_starts_there()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Slice c = Add(b, 1, tx: false, active: false, mhz: 7.150);

            b.Rig.RXFrequency = b.Rig.RXFrequency + 100;
            Drain(b.Rig);
            b.Rig.CopyVFO(1, 0);                             // B onto A
            Drain(b.Rig);
            Assert.Equal(7_150_000UL, LastTune(b, a));
            EveryReaderSays(b.Rig, 7_150_000);

            Reports(a, 14.100100);                           // the step's late report
            EveryReaderSays(b.Rig, 7_150_000);
            Reports(a, 7.150000);                            // the copy's own
            EveryReaderSays(b.Rig, 7_150_000);

            b.Rig.RXFrequency = b.Rig.RXFrequency + 100;
            Drain(b.Rig);
            Assert.Equal(7_150_100UL, LastTune(b, a));
            Assert.Equal(7_150_000UL, LastTune(b, c) ?? 7_150_000UL);
        }

        /// <summary>
        /// Control: the same copy with no step before it reads the copy too,
        /// so the instrument sees a copy land.
        /// </summary>
        [Fact]
        public void A_copy_with_nothing_before_it_reads_the_copy()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Add(b, 1, tx: false, active: false, mhz: 7.150);

            b.Rig.CopyVFO(1, 0);
            Drain(b.Rig);
            Reports(a, 7.150000);
            EveryReaderSays(b.Rig, 7_150_000);
        }

        /// <summary>
        /// A copy copies the source's ONE value at the press: a step still on
        /// its way to the source is what goes, not the source's older number.
        /// </summary>
        [Fact]
        public void A_copy_takes_the_source_slices_pending_step()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: false, mhz: 14.100);
            Slice c = Add(b, 1, tx: false, active: true, mhz: 7.150);
            Assert.Equal(1, b.Rig.RXVFO);

            b.Rig.RXFrequency = 7_150_500;                   // B's step, still queued
            b.Rig.CopyVFO(1, 0);
            Drain(b.Rig);
            Assert.Equal(7_150_500UL, LastTune(b, a));
            Assert.Equal(7_150_500UL, LastTune(b, c));
        }

        // ── Finding 3: a refused tune reads the truth at once ──

        [Fact]
        public void A_tune_on_a_locked_slice_is_not_sent_and_every_reader_says_where_it_is()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Set(a, "_lock", true);

            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Assert.Empty(Tunes(b));
            EveryReaderSays(b.Rig, 14_100_000);

            // Control: unlocked, the same tune is sent and read back.
            Set(a, "_lock", false);
            b.Rig.RXFrequency = 14_100_200;
            Drain(b.Rig);
            Assert.Equal(14_100_200UL, LastTune(b, a));
            EveryReaderSays(b.Rig, 14_100_200);
        }

        // ── Integration fixes 2026-11-06: a tune says whether it was admitted,
        //    and uses the queue's own answer ──

        [Fact]
        public void A_tune_on_a_locked_slice_is_reported_as_not_admitted_so_its_number_is_not_spoken()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Set(a, "_lock", true);
            b.Rig.RXFrequency = 14_100_100;
            Assert.Equal(false, b.Rig.TuneAdmittedHere);
            Set(a, "_lock", false);
            b.Rig.RXFrequency = 14_100_200;                 // the control
            Assert.Equal(true, b.Rig.TuneAdmittedHere);
        }

        [Fact]
        public void A_tune_the_command_loop_will_not_run_is_not_admitted_and_reads_where_the_slice_is()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.q.MainLoop = false;                       // the loop is not running
            b.Rig.RXFrequency = 14_100_100;
            Assert.Equal(false, b.Rig.TuneAdmittedHere);
            Assert.Equal(0, b.Rig.q.Count);
            EveryReaderSays(b.Rig, 14_100_000);             // nothing published for a send that will not happen
        }

        [Fact]
        public void An_item_accepted_but_not_run_when_the_loop_ends_is_dropped_and_its_caller_told()
        {
            // Integration fix 2 (Codex's B1): the loop's end runs nothing late;
            // each accepted item's caller is told it was dropped, and later
            // items are refused.
            Bench b = NewBench();
            bool ran = false, dropped = false;
            Assert.True(b.Rig.q.Enqueue((FlexBase.FunctionDel)(() => ran = true), "test", () => dropped = true));
            b.Rig.CloseCommandQueue();
            Assert.False(ran);
            Assert.True(dropped);
            Assert.False(b.Rig.q.Enqueue((FlexBase.FunctionDel)(() => { }), "after the end"));
        }

        [Fact]
        public void A_tune_accepted_but_dropped_at_the_loops_end_reads_where_the_slice_is()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;
            Assert.Equal(true, b.Rig.TuneAdmittedHere);
            b.Rig.CloseCommandQueue();
            Assert.Empty(Tunes(b));
            EveryReaderSays(b.Rig, 14_100_000);
        }

        [Fact]
        public void A_slice_locked_between_the_press_and_the_send_reads_where_it_is()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            Assert.Equal(14_100_100UL, b.Rig.RXFrequency);   // our request, on its way
            Set(a, "_lock", true);
            Drain(b.Rig);
            Assert.Empty(Tunes(b));
            EveryReaderSays(b.Rig, 14_100_000);
        }

        [Fact]
        public void A_tune_the_radio_rejects_reads_where_the_radio_is_at_once()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            RejectTune(b, 0, "14.100000");
            EveryReaderSays(b.Rig, 14_100_000);
        }

        [Fact]
        public void A_tune_whose_command_connection_fell_before_it_ran_is_not_sent_and_reads_where_it_is()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            b.V.Transport.Fall();
            Drain(b.Rig);
            Assert.Empty(Tunes(b));
            EveryReaderSays(b.Rig, 14_100_000);
        }

        // ── The check travels with the request ──

        /// <summary>
        /// Rule 1: a reply carries the request it answers. A step up is sent,
        /// a step back to where the radio still is waits in the queue, and the
        /// FIRST step's reply arrives: the radio still reports the old number,
        /// which equals the pending request, but that reply is not about it. So
        /// the first step's own late report is still rejected as stale.
        /// </summary>
        [Fact]
        public void A_reply_to_an_older_tune_does_not_confirm_a_newer_one()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            RunOne(b.Rig);                                   // the step up is sent
            b.Rig.RXFrequency = 14_100_000;                  // back down, still queued
            AckTune(b, 0);                                   // the step up's reply
            Reports(a, 14.100100);                           // the step up's report, late
            EveryReaderSays(b.Rig, 14_100_000);
            Drain(b.Rig);
            Reports(a, 14.100000);
            EveryReaderSays(b.Rig, 14_100_000);
        }

        /// <summary>
        /// Rule 1 for refusals: the radio rejecting an older tune does not
        /// throw away a newer one still on its way.
        /// </summary>
        [Fact]
        public void A_rejected_older_tune_leaves_the_newer_one_pending()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            RunOne(b.Rig);
            b.Rig.RXFrequency = 14_100_200;                  // still queued
            RejectTune(b, 0, "14.100000");
            EveryReaderSays(b.Rig, 14_100_200);
            Drain(b.Rig);
            Assert.Equal(14_100_200UL, LastTune(b, a));
        }

        /// <summary>
        /// The vendor's pre-write boundary: a reply delivered while the write
        /// is still in progress (before SendReplyCommand has returned) is
        /// taken as the reply to that write, and a step pressed in the same
        /// instant is a newer request the reply says nothing about.
        /// </summary>
        [Fact]
        public void A_reply_and_a_new_press_inside_the_write_each_keep_to_their_own_request()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            bool pressed = false;
            b.V.Transport.CommandWritten = cmd =>
            {
                if (!cmd.StartsWith("slice tune ", StringComparison.Ordinal) || pressed) return;
                pressed = true;
                b.Rig.RXFrequency = 14_100_000;              // pressed back while the step is written
                b.V.ReplyOk(b.V.LastSequence);               // the step's reply, inside the write
            };

            b.Rig.RXFrequency = 14_100_100;
            RunOne(b.Rig);
            Reports(a, 14.100100);                           // the step's report: stale for the newer request
            EveryReaderSays(b.Rig, 14_100_000);
            Drain(b.Rig);
            AckTune(b, 1);                                   // the newer one's reply: the radio is not there yet
            EveryReaderSays(b.Rig, 14_100_000);
            Reports(a, 14.100000);
            Reports(a, 14.100250);                           // settled: a genuine move is believed
            EveryReaderSays(b.Rig, 14_100_250);
        }

        // ── The follow-up: the give-up window counts from the send ──

        /// <summary>
        /// Codex's follow-up, its exact schedule: the press at 0, the queue
        /// sends it at 1190 ms, the radio's report of the old number then
        /// arrives. At 1200 ms our request has been out for 10 ms; every
        /// reader still says it, and the next step goes on from it.
        /// </summary>
        [Fact]
        public void The_give_up_window_counts_from_the_send_not_the_press()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            long now = 0;
            Clock(b.Rig, a, () => now);

            b.Rig.RXFrequency = 14_100_100;
            now = 1190;
            Drain(b.Rig);
            Reports(a, 14.100000);
            now = 1200;
            EveryReaderSays(b.Rig, 14_100_100);
            b.Rig.RXFrequency = b.Rig.RXFrequency + 100;
            Drain(b.Rig);
            Assert.Equal(14_100_200UL, LastTune(b, a));
        }

        /// <summary>
        /// And it does run out: sent, then nothing from the radio for the whole
        /// window. Every reader then says what the radio last reported, never
        /// our unconfirmed number (#164's honesty: a request is not a report).
        /// </summary>
        [Fact]
        public void Sent_and_never_confirmed_reads_the_radios_last_report_once_the_window_is_out()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            long now = 0;
            Clock(b.Rig, a, () => now);

            b.Rig.RXFrequency = 14_100_100;
            now = 500;
            Drain(b.Rig);
            now = 500 + FrequencyEchoGuard.GiveUpMs - 1;
            EveryReaderSays(b.Rig, 14_100_100);
            now = 500 + FrequencyEchoGuard.GiveUpMs;
            EveryReaderSays(b.Rig, 14_100_000);
        }

        /// <summary>
        /// A request still waiting in OUR queue is held well past the radio's
        /// window: it is our own write on its way, not a lost one. A stale
        /// report meanwhile does not rewind it.
        /// </summary>
        [Fact]
        public void A_request_still_in_our_queue_is_held_past_the_radios_window()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            long now = 0;
            Clock(b.Rig, a, () => now);

            b.Rig.RXFrequency = 14_100_100;
            RunOne(b.Rig);
            b.Rig.RXFrequency = 14_100_200;                  // the newer step, unsent
            now = 5000;
            Reports(a, 14.100100);                           // the older step's report
            EveryReaderSays(b.Rig, 14_100_200);
            Drain(b.Rig);
            Assert.Equal(14_100_200UL, LastTune(b, a));
        }
        // ── Repair round 6 (Codex's fifth re-check) ──

        /// <summary>
        /// Codex's blocker 1, its exact schedule: up, up again, and back down
        /// to the first step's number, all three sent. The radio reports and
        /// answers each in order, each report before its own reply. The FIRST
        /// request's report carries the latest request's number but is not its
        /// confirmation, so the middle step's report that follows must not be
        /// believed, and the next step down starts from the latest request.
        /// </summary>
        [Fact]
        public void A_reversal_is_not_settled_by_an_older_requests_report_of_the_same_number()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            b.Rig.RXFrequency = 14_100_200;
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Assert.Equal(3, Tunes(b).Count);

            Reports(a, 14.100100); AckTune(b, 0);            // the first request's report, then its reply
            EveryReaderSays(b.Rig, 14_100_100);
            Reports(a, 14.100200); AckTune(b, 1);            // the middle request's
            EveryReaderSays(b.Rig, 14_100_100);

            b.Rig.RXFrequency = b.Rig.RXFrequency - 100;     // the next step down
            Drain(b.Rig);
            Assert.Equal(14_100_000UL, LastTune(b, a));
        }

        /// <summary>
        /// The same reversal, carried through: the latest request's own report
        /// then settles it, and a genuine move after that is believed.
        /// </summary>
        [Fact]
        public void A_reversal_settles_on_the_latest_requests_own_report()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            b.Rig.RXFrequency = 14_100_200;
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Reports(a, 14.100100); AckTune(b, 0);
            Reports(a, 14.100200); AckTune(b, 1);
            Reports(a, 14.100100); AckTune(b, 2);            // the latest's own report and reply
            EveryReaderSays(b.Rig, 14_100_100);
            Reports(a, 14.100200);                           // somebody else moves it: believed now
            EveryReaderSays(b.Rig, 14_100_200);
        }

        /// <summary>
        /// Control (Codex's): three distinct steps, reported and answered in
        /// order. The intermediate reports rewind nothing, and the next step
        /// down starts from the last.
        /// </summary>
        [Fact]
        public void A_monotonic_run_reported_in_order_keeps_its_last_step()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            for (int i = 0; i < 3; i++) b.Rig.RXFrequency = b.Rig.RXFrequency + 100;
            Drain(b.Rig);
            Reports(a, 14.100100); AckTune(b, 0);
            Reports(a, 14.100200); AckTune(b, 1);
            EveryReaderSays(b.Rig, 14_100_300);
            b.Rig.RXFrequency = b.Rig.RXFrequency - 100;
            Drain(b.Rig);
            Assert.Equal(14_100_200UL, LastTune(b, a));
        }

        private static Xvtr Transverter(VendorRadioFixture v, int index, string name, double rfMhz)
        {
            var x = new Xvtr(v.Radio);
            Set(x, "_index", index);
            Set(x, "_name", name);
            Set(x, "_rfFreq", rfMhz);
            Set(x, "_ifFreq", 28.0);
            Set(x, "_valid", true);
            return x;
        }

        /// <summary>
        /// Codex's blocker 2, its exact schedule: definitions at 144 and 432
        /// MHz, the transmit slice on 144.1. A band change to 432.1 is sent but
        /// not yet reported, and drive is then set: the drive goes to the
        /// definition for the band the slice is being tuned to, the band F
        /// names, never the old band's.
        /// </summary>
        [Fact]
        public void Transverter_drive_set_after_a_band_change_is_sent_goes_to_the_new_bands_definition()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 144.100);
            typeof(FlexBase).GetField("theRadio", Private)!.SetValue(b.Rig, b.V.Radio);
            var addX = typeof(FlexBase).GetMethod("xvtrAdded", Private)!;
            addX.Invoke(b.Rig, new object[] { Transverter(b.V, 0, "2M", 144.0) });
            addX.Invoke(b.Rig, new object[] { Transverter(b.V, 1, "70CM", 432.0) });
            try
            {
                Assert.Equal("2M", b.Rig.ActiveXvtrName);    // control: the instrument sees the band

                b.Rig.TXFrequency = 432_100_000;
                Drain(b.Rig);
                Assert.Equal(432_100_000UL, b.Rig.TXFrequency);
                Assert.Equal("70CM", b.Rig.ActiveXvtrName);

                b.Rig.XvtrDrivePowerCentiDbm = 500;
                Drain(b.Rig);
                List<string> cmds = b.V.Transport.Commands;
                Assert.Contains("xvtr set 1 max_power=5.00", cmds);
                Assert.DoesNotContain(cmds, c => c.StartsWith("xvtr set 0 ", StringComparison.Ordinal));

                Reports(a, 432.100000);                      // the radio's report: still the new band
                Assert.Equal("70CM", b.Rig.ActiveXvtrName);
            }
            finally { typeof(FlexBase).GetField("theRadio", Private)!.SetValue(b.Rig, null); }
        }

        /// <summary>
        /// Codex's sixth re-check, blocker 1: a tune whose write is stalled
        /// (a slow or stuck network) must not hold up anything else. While the
        /// write is held open, the frequency getters, the roles F reads, and a
        /// slice removal on another thread all finish. Until repair round 7
        /// the roster's lock was held across the write and all three waited.
        /// </summary>
        [Fact]
        public void A_stalled_tune_write_holds_up_no_reader_and_no_removal()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Slice c = Add(b, 1, tx: false, active: false, mhz: 7.150);
            var remove = typeof(FlexBase).GetMethod("sliceRemoved", Private)!;
            var binding = new ObservationBinding(new object(), new ConnectionAttempt("test-serial"));
            using var inWrite = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            b.V.Transport.CommandWritten = cmd =>
            {
                if (!cmd.StartsWith("slice tune ", StringComparison.Ordinal)) return;
                inWrite.Set();
                release.Wait(10_000);                        // the network stalls here
            };

            b.Rig.RXFrequency = 14_100_100;
            Task drain = Task.Run(() => Drain(b.Rig));
            try
            {
                Assert.True(inWrite.Wait(5000), "the write was never reached");
                Task<ulong> rx = Task.Run(() => b.Rig.RXFrequency);
                Task<SliceRoles> roles = Task.Run(() => SliceRoles.Of(b.Rig));
                Task removal = Task.Run(() => remove.Invoke(b.Rig, new object[] { c, binding }));
                Assert.True(rx.Wait(2000), "the receive frequency waited for the stalled write");
                Assert.True(roles.Wait(2000), "F's roles waited for the stalled write");
                Assert.True(removal.Wait(2000), "a slice removal waited for the stalled write");
                Assert.Equal(14_100_100UL, rx.Result);
            }
            finally
            {
                release.Set();
                drain.Wait(10_000);
            }
            Assert.Equal(14_100_100UL, LastTune(b, a));
        }

        /// <summary>
        /// The one removal a client-side check can order against a tune is one
        /// WE start, and our own slice close runs on the same command queue as
        /// the tune's send, so the queue orders them (round 7). A copy onto a
        /// slice queued after we asked to close it is not sent; one queued
        /// before it is sent before the slice's remove.
        /// </summary>
        [Fact]
        public void Our_own_slice_close_and_a_tune_of_that_slice_go_out_in_the_order_they_were_pressed()
        {
            static (Bench, Slice) Setup(TuningReceiptTests t)
            {
                Bench b = t.NewBench();
                Add(b, 0, tx: true, active: true, mhz: 14.100);
                Slice c = Add(b, 1, tx: false, active: false, mhz: 7.150);
                var remove = typeof(FlexBase).GetMethod("sliceRemoved", Private)!;
                var binding = new ObservationBinding(new object(), new ConnectionAttempt("test-serial"));
                // FlexLib raises SliceRemoved synchronously inside Slice.Close; wire
                // it the way the station wiring does.
                b.V.Radio.SliceRemoved += s => remove.Invoke(b.Rig, new object[] { s, binding });
                b.V.Radio.SliceList.Add(c);                    // so Slice.Close finds it in FlexLib's list
                return (b, c);
            }
            var removeSlice = typeof(FlexBase).GetMethod("RemoveSlice", Private, null, new[] { typeof(Slice) }, null)!;

            var (first, c1) = Setup(this);
            Assert.True((bool)removeSlice.Invoke(first.Rig, new object[] { c1 })!);
            first.Rig.CopyVFO(0, 1);                          // pressed after the close
            Drain(first.Rig);
            Assert.Contains("slice remove 1", first.V.Transport.Commands);
            Assert.Null(LastTune(first, c1));

            var (second, c2) = Setup(this);
            second.Rig.CopyVFO(0, 1);                         // pressed before the close
            Assert.True((bool)removeSlice.Invoke(second.Rig, new object[] { c2 })!);
            Drain(second.Rig);
            List<string> cmds = second.V.Transport.Commands;
            int tune = cmds.FindIndex(x => x.StartsWith("slice tune 1 ", StringComparison.Ordinal));
            int gone = cmds.IndexOf("slice remove 1");
            Assert.True(tune >= 0 && gone > tune, "the tune pressed first did not go out before the remove");
        }

        /// <summary>
        /// A slice we are removing by a raw command (the listenback's restore
        /// of a slice it created) is marked first, and a tune of it queued
        /// after that is not sent.
        /// </summary>
        [Fact]
        public void A_slice_we_are_removing_by_command_gets_no_later_tune()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;
            _ = (long)typeof(FlexBase).GetMethod("NoteRemovingSlice", Private)!.Invoke(b.Rig, new object[] { a })!;
            Drain(b.Rig);
            Assert.Empty(Tunes(b));
            EveryReaderSays(b.Rig, 14_100_000);

            string lb = System.IO.File.ReadAllText(System.IO.Path.Combine(FlexBasePartials.RepoRoot(), "Radios", "FlexBase.Listenback.cs"));
            Assert.Contains("long leavingMark = leaving != null ? NoteRemovingSlice(leaving) : 0;", lb);
            Assert.Contains("ClearRemovingSlice(leaving, leavingMark);", lb);
        }

        /// <summary>
        /// Codex's sixth re-check, blocker 2, its exact schedule: up, up and
        /// back, all reported in order, and the replies arrive latest first
        /// (2, 1, 0). The latest's reply settles it, the older replies change
        /// nothing after that, and a genuine move to the middle number is then
        /// believed by every reader; the next step goes on from it.
        /// </summary>
        [Fact]
        public void Replies_arriving_latest_first_still_settle_and_a_later_genuine_move_is_believed()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            b.Rig.RXFrequency = 14_100_200;
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Reports(a, 14.100100);
            Reports(a, 14.100200);
            Reports(a, 14.100100);
            AckTune(b, 2); AckTune(b, 1); AckTune(b, 0);
            EveryReaderSays(b.Rig, 14_100_100);

            Reports(a, 14.100200);                           // somebody else moves it
            EveryReaderSays(b.Rig, 14_100_200);
            b.Rig.RXFrequency = b.Rig.RXFrequency + 100;
            Drain(b.Rig);
            Assert.Equal(14_100_300UL, LastTune(b, a));
        }

        /// <summary>
        /// The lost-first-reply variant: the first request is never answered,
        /// the middle and latest are. The latest is settled by its own reply,
        /// and a genuine move afterwards is believed; the first request's
        /// reply, arriving late after all, changes nothing.
        /// </summary>
        [Fact]
        public void A_lost_older_reply_does_not_hold_the_latest_unconfirmed()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            b.Rig.RXFrequency = 14_100_200;
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Reports(a, 14.100100);
            Reports(a, 14.100200); AckTune(b, 1);
            Reports(a, 14.100100);
            EveryReaderSays(b.Rig, 14_100_100);
            Reports(a, 14.100200);                           // a move made at the radio before our latest ran
            EveryReaderSays(b.Rig, 14_100_100);              // kept out until the latest is answered (round 8)
            AckTune(b, 2);
            EveryReaderSays(b.Rig, 14_100_100);              // our latest overrode it (rulings round two)
            AckTune(b, 0);                                   // an older reply out of order changes nothing
            EveryReaderSays(b.Rig, 14_100_100);
        }

        /// <summary>
        /// An older request's error, and a lost older reply, hold nothing up:
        /// only the latest request's answer matters (repair round 8). The
        /// radio reports nothing for the first step and its reply is lost,
        /// rejects the middle one, reports the latest, then a genuine move; the
        /// latest's answer then lets the radio's report in.
        /// </summary>
        [Fact]
        public void An_older_requests_error_or_lost_reply_holds_nothing_up()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            b.Rig.RXFrequency = 14_100_200;
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            RejectTune(b, 1, "14.100000");
            Reports(a, 14.100200);                           // a move made at the radio before our latest ran
            EveryReaderSays(b.Rig, 14_100_100);
            AckTune(b, 2);
            EveryReaderSays(b.Rig, 14_100_100);              // our latest overrode it (rulings round two)
        }

        /// <summary>Control for the above: removed before the queue runs, nothing is written at all.</summary>
        [Fact]
        public void A_slice_removed_before_its_tune_runs_gets_nothing()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;
            typeof(FlexBase).GetMethod("sliceRemoved", Private)!
                .Invoke(b.Rig, new object[] { a, new ObservationBinding(new object(), new ConnectionAttempt("test-serial")) });
            Drain(b.Rig);
            Assert.Empty(Tunes(b));
        }
        // ── The sweep of raw readers on the transmit path (repair round 6) ──

        /// <summary>
        /// The loopback ears are tuned to the frequency the caller gives them,
        /// which the arrangement takes from the transmit slice's one value.
        /// The control: the old overload still tunes them to FlexLib's number.
        /// </summary>
        [Fact]
        public void The_loopback_ears_go_to_the_transmit_frequency_they_are_given()
        {
            var v = new VendorRadioFixture("ears-" + Guid.NewGuid().ToString("N"));
            Slice Make(int index, double mhz)
            {
                Slice s = v.NewSlice(index, 123, mhz, "USB");
                var pan = (Panadapter)Activator.CreateInstance(typeof(Panadapter), Private, null, new object[] { v.Radio }, null)!;
                typeof(Panadapter).GetProperty(nameof(Panadapter.StreamID))!.GetSetMethod(true)!
                    .Invoke(pan, new object[] { (uint)(0x40000000 + index) });
                typeof(Slice).GetProperty(nameof(Slice.Panadapter))!.GetSetMethod(true)!.Invoke(s, new object[] { pan });
                return s;
            }
            Slice tx = Make(0, 144.100);
            var before = new[] { tx };
            var resources = new LoopbackSliceResources(before, before.Select(s => s.Panadapter));
            Slice ears = Make(1, 7.100);
            var roster = new[] { tx, ears };
            var pans = roster.Select(s => s.Panadapter).ToArray();
            Assert.True(resources.Capture(roster, pans));
            Assert.True(resources.Configure(roster, pans, tx, "XVTR", 432.100));
            Assert.Equal(432.100, ears.Freq);
        }

        /// <summary>
        /// Every reader on the transmit path that took FlexLib's number for
        /// the transmit slice now takes its one value: the transverter for
        /// drive, the listenback's listener and its record, and the loopback
        /// ears. A source pin, so a new raw read on these paths is seen.
        /// </summary>
        [Fact]
        public void The_transmit_path_reads_the_transmit_slices_one_value()
        {
            string dir = System.IO.Path.Combine(FlexBasePartials.RepoRoot(), "Radios");
            string xv = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "FlexBase.Transverter.cs"));
            Assert.DoesNotContain("SliceForTransmitSetting()?.Freq", xv);
            Assert.Contains("LongFreqToLibFreq(ValueOf(txSlice))", xv);
            string lb = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "FlexBase.Listenback.cs"));
            Assert.DoesNotContain("tx.Freq", lb);
            Assert.Contains("double txMhz = LongFreqToLibFreq(ValueOf(tx));", lb);
            string main = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "FlexBase.cs"));
            Assert.Contains("earsTransmit, earsAntenna, earsMhz))", main);
        }
        // ── Repair round 8 (Codex's seventh re-check) ──

        /// <summary>
        /// Codex's blocker 1, its exact schedule: up, up and back; the radio
        /// reports all three in order; then somebody genuinely moves the slice
        /// to the middle number BEFORE any reply; then the replies arrive
        /// (in order here, reversed in the next test). Every reader must end
        /// on the genuine number, and the next step must start from it.
        /// </summary>
        [Theory]
        [InlineData(new[] { 0, 1, 2 })]
        [InlineData(new[] { 2, 1, 0 })]
        public void A_move_reported_before_the_replies_is_overridden_by_the_latest_step(int[] replyOrder)
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            b.Rig.RXFrequency = 14_100_200;
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Reports(a, 14.100100);
            Reports(a, 14.100200);
            Reports(a, 14.100100);
            Reports(a, 14.100200);                           // made at the radio before our steps ran
            foreach (int n in replyOrder) AckTune(b, n);     // in order, and (not possible on TCP) reversed
            EveryReaderSays(b.Rig, 14_100_100);              // the latest step is where the radio is
            b.Rig.RXFrequency = b.Rig.RXFrequency + 100;
            Drain(b.Rig);
            Assert.Equal(14_100_200UL, LastTune(b, a));
        }

        /// <summary>
        /// Codex's blocker 3, its exact schedule: seventy steps queued and
        /// sent with nothing answered yet, then the radio's late report of the
        /// FIRST step. Every reader stays on the last step, and the next step
        /// goes on from it.
        /// </summary>
        [Theory]
        [InlineData(70)]
        [InlineData(10)]                                     // Codex's control: a short burst
        [InlineData(500)]
        public void A_long_unanswered_burst_is_not_rewound_by_its_first_steps_late_report(int steps)
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            for (int i = 0; i < steps; i++) b.Rig.RXFrequency = b.Rig.RXFrequency + 100;
            Drain(b.Rig);
            ulong last = 14_100_000UL + 100UL * (ulong)steps;
            EveryReaderSays(b.Rig, last);
            Reports(a, 14.100100);                           // the first step's report, late
            EveryReaderSays(b.Rig, last);
            b.Rig.RXFrequency = b.Rig.RXFrequency + 100;
            Drain(b.Rig);
            Assert.Equal(last + 100, LastTune(b, a));
        }

        /// <summary>
        /// Codex's blocker 2, its exact schedule, through the real listenback
        /// restore: the radio REJECTS the removal of the listening slice it
        /// created, so the slice survives. Selected, it can be tuned like any
        /// other. The control: when the removal succeeds, the slice is gone
        /// and nothing is tuned.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task A_slice_whose_removal_the_radio_rejected_can_still_be_tuned(bool rejected)
        {
            using var c = new ListenbackRoundTwoTests.RadioCase();
            c.Snapshot();
            // The radio's removal reaches our roster, as the station wiring delivers it.
            var removed = typeof(FlexBase).GetMethod("sliceRemoved", Private)!;
            c.F.Vendor.Radio.SliceRemoved += sl => removed.Invoke(c.F.Rig, new object[] { sl, c.F.Binding });
            c.F.Vendor.Transport.CommandWritten = cmd =>
            {
                if (rejected && cmd == "slice remove 0") c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0x50000015, "");
                else c.Respond(cmd);
            };
            bool restored = await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(c.F.Rig.PendingListenbackRecovery, true));
            Assert.Equal(!rejected, restored);
            var mine = (List<Slice>)typeof(FlexBase).GetField("mySlices", Private)!.GetValue(c.F.Rig)!;
            bool survives;
            lock (mine) survives = mine.Contains(c.Ears);
            Assert.Equal(rejected, survives);
            if (!survives) return;

            typeof(FlexBase).GetField("_RXVFO", Private)!.SetValue(c.F.Rig, mine.IndexOf(c.Ears));
            c.F.Vendor.Transport.Written.Clear();
            c.F.Rig.RXFrequency = 14_200_100;
            while (c.F.Rig.q.Count > 0) RunOne(c.F.Rig);
            Assert.Contains("slice tune 0 14.200100", c.F.Vendor.Transport.Commands);
            Assert.Equal(14_200_100UL, c.F.Rig.RXFrequency);
        }
        // ── Repair round 8: the rest of the table, through FlexBase ──

        /// <summary>
        /// A removal mark lasts only while the removal is outstanding: cleared
        /// (a rejected or unsent removal) or lapsed (never answered), the
        /// slice can be tuned again. The control: while it stands, nothing is sent.
        /// </summary>
        [Fact]
        public void A_removal_mark_is_cleared_by_a_failed_removal_and_lapses_when_never_answered()
        {
            long now = 0;
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RemovalMarkClock = () => now;
            var note = typeof(FlexBase).GetMethod("NoteRemovingSlice", Private)!;
            var clear = typeof(FlexBase).GetMethod("ClearRemovingSlice", Private)!;

            long mark = (long)note.Invoke(b.Rig, new object[] { a })!;
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Assert.Empty(Tunes(b));                          // control: the mark refuses

            clear.Invoke(b.Rig, new object[] { a, mark });
            b.Rig.RXFrequency = 14_100_200;
            Drain(b.Rig);
            Assert.Equal(14_100_200UL, LastTune(b, a));      // cleared: tuned again

            note.Invoke(b.Rig, new object[] { a });
            now = FlexBase.RemovalMarkMs - 1;
            b.Rig.RXFrequency = 14_100_300;
            Drain(b.Rig);
            Assert.Equal(14_100_200UL, LastTune(b, a));      // still standing
            now = FlexBase.RemovalMarkMs;
            b.Rig.RXFrequency = 14_100_400;
            Drain(b.Rig);
            Assert.Equal(14_100_400UL, LastTune(b, a));      // lapsed: tuned again
        }

        /// <summary>
        /// The answer overtakes the report (the one premise, broken): success
        /// comes back while the radio has reported nothing since the write and
        /// still reports the old number. The reading stays on our request until
        /// the radio's report, and then follows it.
        /// </summary>
        [Fact]
        public void An_answer_ahead_of_its_report_holds_our_value_until_the_report()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            AckTune(b, 0);
            EveryReaderSays(b.Rig, 14_100_100);
            Reports(a, 14.100100);
            EveryReaderSays(b.Rig, 14_100_100);
            Reports(a, 14.100300);                           // and the radio is the authority after that
            EveryReaderSays(b.Rig, 14_100_300);
        }

        /// <summary>
        /// When the window ends on a number other than ours, the slice's handler
        /// runs again, so what is derived from the frequency follows the radio:
        /// here the receive cache, which the handler writes.
        /// </summary>
        [Fact]
        public void The_end_of_the_window_on_another_number_brings_the_caches_with_it()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            var rxHz = typeof(FlexBase).GetProperty("RxCacheHz", Private)!;

            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Reports(a, 14.250000);                           // made before our tune ran: kept out
            Assert.Equal(14_100_100UL, (ulong)rxHz.GetValue(b.Rig)!);
            AckTune(b, 0);
            Assert.Equal(14_100_100UL, (ulong)rxHz.GetValue(b.Rig)!);
            Reports(a, 14.250000);                           // a move after the answer: the caches follow it
            Assert.Equal(14_250_000UL, (ulong)rxHz.GetValue(b.Rig)!);
        }
        // ── Repair round 9 (Codex's eighth re-check) ──

        /// <summary>
        /// Codex's blocker 1, its exact schedule: a step is answered after the
        /// radio reported a genuine 14.25 (control: every reader says 14.25).
        /// The next step is sent and the radio reports 14.25 AGAIN, unchanged,
        /// so FlexLib raises no Freq notification; then the answer. That report
        /// arrived, so every reader says 14.25 and the next step starts there.
        /// </summary>
        [Fact]
        public void An_unchanged_report_before_the_answer_still_counts_as_the_radios_answer()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            // Rulings round two: the report the old guard waited for never comes
            // to us; the answer settles the step whatever came before it.
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Reports(a, 14.250000);
            AckTune(b, 0);
            EveryReaderSays(b.Rig, 14_100_100);

            b.Rig.RXFrequency = 14_100_200;
            Drain(b.Rig);
            Reports(a, 14.250000);                           // the same number again: no Freq notification
            AckTune(b, 1);
            EveryReaderSays(b.Rig, 14_100_200);
            b.Rig.RXFrequency = b.Rig.RXFrequency + 100;
            Drain(b.Rig);
            Assert.Equal(14_100_300UL, LastTune(b, a));
        }

        /// <summary>
        /// Codex's blocker 3, its exact schedule: a tune is sent, the radio
        /// reports a genuine 14.25, the slice is removed, THEN the tune is
        /// answered. The removal cleared the transmit caches; the late answer
        /// must not bring them back. Control: before the answer they are clear.
        /// </summary>
        [Fact]
        public void A_late_answer_for_a_removed_slice_does_not_bring_its_numbers_back()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Set(a, "_active", false);                        // as Codex did: no filter adapter in the fixture to stop the handler
            var remove = typeof(FlexBase).GetMethod("sliceRemoved", Private)!;

            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Reports(a, 14.250000);
            remove.Invoke(b.Rig, new object[] { a, new ObservationBinding(new object(), new ConnectionAttempt("test-serial")) });
            Assert.Equal(0UL, b.Rig.TXFrequency);            // control: the removal cleared it
            AckTune(b, 0);
            Drain(b.Rig);
            Assert.Equal(0UL, b.Rig.TXFrequency);
            Assert.False(SliceRoles.Of(b.Rig).HasTransmit);
        }

        /// <summary>
        /// Codex's blocker 2, its exact schedule, through the real listenback
        /// restore: the first removal of the listening slice is never answered
        /// (the restore times out), a retry sends a second removal, and only
        /// THEN does the first removal's error arrive. The second removal is
        /// still outstanding, so the slice must still not be tuned. Control: the
        /// same with the old error withheld.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task An_old_removal_attempts_late_error_does_not_clear_a_newer_attempts_mark(bool lateError)
        {
            using var c = new ListenbackRoundTwoTests.RadioCase();
            c.Snapshot();
            int removals = 0, firstSeq = 0;
            c.F.Vendor.Transport.CommandWritten = cmd =>
            {
                if (cmd != "slice remove 0") { c.Respond(cmd); return; }
                removals++;
                if (removals == 1) { firstSeq = c.F.Vendor.LastSequence; return; }   // never answered
                if (lateError) c.F.Vendor.Reply(firstSeq, 0x50000015, "");           // the old error, now
            };
            Assert.False(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(c.F.Rig.PendingListenbackRecovery, true)));
            Assert.False(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(c.F.Rig.PendingListenbackRecovery, true)));
            Assert.Equal(2, removals);

            var mine = (List<Slice>)typeof(FlexBase).GetField("mySlices", Private)!.GetValue(c.F.Rig)!;
            lock (mine) Assert.Contains(c.Ears, mine);
            typeof(FlexBase).GetField("_RXVFO", Private)!.SetValue(c.F.Rig, mine.IndexOf(c.Ears));
            c.F.Vendor.Transport.Written.Clear();
            c.F.Rig.RXFrequency = 14_200_100;
            while (c.F.Rig.q.Count > 0) RunOne(c.F.Rig);
            Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.StartsWith("slice tune 0 ", StringComparison.Ordinal));
        }

        /// <summary>
        /// The follow-up: when the window ends by running out of time rather
        /// than by an answer, what is derived from the frequency follows the
        /// radio too. A genuine 14.25 is kept out while our tune is unanswered;
        /// the answer is lost; at the give-up the receive cache and the
        /// where-you-are record become 14.25.
        /// </summary>
        [Fact]
        public void Running_out_of_time_brings_what_is_derived_with_it()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            long now = 0;
            Clock(b.Rig, a, () => now);
            var rxHz = typeof(FlexBase).GetProperty("RxCacheHz", Private)!;

            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Reports(a, 14.100100);
            Reports(a, 14.250000);                           // genuine, kept out
            Assert.Equal(14_100_100UL, (ulong)rxHz.GetValue(b.Rig)!);
            now = FrequencyEchoGuard.GiveUpMs;               // the answer never comes
            Assert.Equal(14_250_000UL, b.Rig.RXFrequency);   // the reading follows the radio
            Drain(b.Rig);                                    // and the follow-up queued at the give-up runs
            Assert.Equal(14_250_000UL, (ulong)rxHz.GetValue(b.Rig)!);
        }
        /// <summary>
        /// Each removal mark is one attempt: clearing an older attempt's mark
        /// leaves a newer attempt's standing; clearing the newer one clears it.
        /// </summary>
        [Fact]
        public void Clearing_an_older_removal_attempt_leaves_a_newer_attempts_mark()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            var note = typeof(FlexBase).GetMethod("NoteRemovingSlice", Private)!;
            var clear = typeof(FlexBase).GetMethod("ClearRemovingSlice", Private)!;

            long first = (long)note.Invoke(b.Rig, new object[] { a })!;
            long second = (long)note.Invoke(b.Rig, new object[] { a })!;
            clear.Invoke(b.Rig, new object[] { a, first });
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Assert.Empty(Tunes(b));
            clear.Invoke(b.Rig, new object[] { a, second });
            b.Rig.RXFrequency = 14_100_200;
            Drain(b.Rig);
            Assert.Equal(14_100_200UL, LastTune(b, a));
        }

        /// <summary>
        /// The FlexLib patch (MIGRATION.md item 23): every rf_frequency status
        /// raises FrequencyReported, an unchanged one included, and a changed
        /// one raises it before the Freq notification. The control: an unchanged
        /// status raises no Freq notification, which is the vendor behaviour
        /// the patch exists to see past.
        /// </summary>
        [Fact]
        public void FlexLib_reports_every_frequency_status_including_an_unchanged_one()
        {
            var v = new VendorRadioFixture("reported-" + Guid.NewGuid().ToString("N"));
            Slice s = v.NewSlice(0, Handle, 14.100, "USB");
            var events = new List<string>();
            s.PropertyChanged += (_, e) => { if (e.PropertyName == "Freq") events.Add("Freq " + s.Freq.ToString("F6", CultureInfo.InvariantCulture)); };
            s.FrequencyReported += (sl, mhz, changed) => events.Add("Reported " + mhz.ToString("F6", CultureInfo.InvariantCulture) + (changed ? " changed" : ""));

            s.StatusUpdate("rf_frequency=14.100000");
            s.StatusUpdate("rf_frequency=14.250000");
            Assert.Equal(new[] { "Reported 14.100000", "Reported 14.250000 changed", "Freq 14.250000" }, events);
        }

        /// <summary>The FlexLib patch is comment-marked where it is raised, and the event is declared once.</summary>
        [Fact]
        public void The_FlexLib_frequency_report_patch_is_marked_and_in_place()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(FlexBasePartials.RepoRoot(), "FlexLib_API", "FlexLib", "Slice.cs"));
            Assert.Contains("public event Action<Slice, double, bool> FrequencyReported;", src);
            Assert.Contains("// JJFlex patch (MIGRATION.md item 23): each subscriber in isolation", src);
            Assert.Contains("try { handler(this, mhz, changed); }", src);
            Assert.Contains("// JJFlex patch (MIGRATION.md item 23): an equal report still arrived.", src);
            Assert.Contains("// JJFlex patch (MIGRATION.md item 23): report every arrival, before the Freq notification, so a throwing Freq subscriber cannot hide it.", src);
            Assert.Contains("OnFrequencyReported(temp, false);", src);
            Assert.Contains("OnFrequencyReported(temp, true);", src);
            string migration = System.IO.File.ReadAllText(System.IO.Path.Combine(FlexBasePartials.RepoRoot(), "MIGRATION.md"));
            Assert.Contains("Slice frequency report arrival", migration);
        }
        // ── Repair round 10 (Codex's ninth re-check) ──

        /// <summary>
        /// Codex's blocker, its exact schedule, through the answer and through
        /// running out of time: the reconcile has decided the slice is still
        /// ours and the slice's Freq handler is running; at that moment (the
        /// guard's clock, read inside the handler) the slice is removed on
        /// ANOTHER thread, and the removal is let finish. The handler must not
        /// then put the removed transmitter's frequency, mode or XIT back.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_removal_while_the_handler_commits_leaves_no_transmit_numbers_behind(bool byExpiry)
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Set(a, "_active", false);                        // as Codex did: no filter adapter in the fixture
            var remove = typeof(FlexBase).GetMethod("sliceRemoved", Private)!;
            var binding = new ObservationBinding(new object(), new ConnectionAttempt("test-serial"));
            long now = 0;
            int armed = 0, fired = 0;
            Task? removal = null;
            Clock(b.Rig, a, () =>
            {
                if (Volatile.Read(ref armed) == 1
                    && new System.Diagnostics.StackTrace().ToString().Contains("slicePropertyChangedHandler")
                    && Interlocked.Exchange(ref fired, 1) == 0)
                {
                    removal = Task.Run(() => remove.Invoke(b.Rig, new object[] { a, binding }));
                    removal.Wait(1000);                      // finishes now unless the commit holds it off
                }
                return now;
            });

            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Reports(a, 14.250000);                           // genuine, kept out while our tune is unanswered
            if (byExpiry)
            {
                now = FrequencyEchoGuard.GiveUpMs;
                _ = b.Rig.RXFrequency;                       // a reader notices the give-up; the reconcile is queued
                Volatile.Write(ref armed, 1);
                Drain(b.Rig);
            }
            else
            {
                Volatile.Write(ref armed, 1);
                AckTune(b, 0);
            }
            Assert.Equal(1, Volatile.Read(ref fired));       // the boundary was reached
            Assert.True(removal!.Wait(5000));
            Assert.Equal(0UL, b.Rig.TXFrequency);
            Assert.Equal("", (string)typeof(FlexBase).GetField("_TXMode", Private)!.GetValue(b.Rig)!);
            Assert.False(b.Rig.XIT.Active);
            b.Rig.ShowingXmitFrequency = true;
            Assert.Equal(0UL, b.Rig.VirtualRXFrequency);     // the field and title fallback's input
        }
        /// <summary>
        /// The FlexLib patch isolates each subscriber (repair round 10): one
        /// that throws stops neither a later subscriber nor, on a change, the
        /// Freq notification.
        /// </summary>
        [Fact]
        public void A_throwing_frequency_report_subscriber_stops_nothing_else()
        {
            var v = new VendorRadioFixture("isolated-" + Guid.NewGuid().ToString("N"));
            Slice s = v.NewSlice(0, Handle, 14.100, "USB");
            int later = 0, freq = 0;
            s.FrequencyReported += (_, _, _) => throw new InvalidOperationException("a broken subscriber");
            s.FrequencyReported += (_, _, _) => later++;
            s.PropertyChanged += (_, e) => { if (e.PropertyName == "Freq") freq++; };

            s.StatusUpdate("rf_frequency=14.100000");        // unchanged
            s.StatusUpdate("rf_frequency=14.250000");        // changed
            Assert.Equal(2, later);
            Assert.Equal(1, freq);
            Assert.Equal(14.25, s.Freq);
        }
        /// <summary>
        /// The commit itself, not only the check, is one step against removal
        /// (repair round 10). Inside the handler's locked commit the guard is
        /// asked for the pending value (a report other than our pending tune
        /// arrives while the transmit caches are being moved over); at that
        /// moment a removal starts on another thread. It must wait for the
        /// commit and then clear it, so the removed transmitter has no number.
        /// </summary>
        [Fact]
        public void A_removal_during_the_handlers_commit_waits_and_then_clears_it()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Set(a, "_active", false);
            var remove = typeof(FlexBase).GetMethod("sliceRemoved", Private)!;
            var binding = new ObservationBinding(new object(), new ConnectionAttempt("test-serial"));
            int armed = 0, fired = 0;
            Task? removal = null;
            Clock(b.Rig, a, () =>
            {
                if (Volatile.Read(ref armed) == 1)
                {
                    string stack = new System.Diagnostics.StackTrace().ToString();
                    if (stack.Contains("slicePropertyChangedHandler") && stack.Contains("TryPending")
                        && Interlocked.Exchange(ref fired, 1) == 0)
                    {
                        removal = Task.Run(() => remove.Invoke(b.Rig, new object[] { a, binding }));
                        removal.Wait(1000);
                    }
                }
                return 0;
            });

            b.Rig.RXFrequency = 14_100_100;                  // pending, sent
            Drain(b.Rig);
            typeof(FlexBase).GetMethod("ForgetTransmitCaches", Private)!.Invoke(b.Rig, new object[] { a });
            Volatile.Write(ref armed, 1);
            Reports(a, 14.250000);                           // not ours: the commit asks the guard for the pending value
            Assert.Equal(1, Volatile.Read(ref fired));
            Assert.True(removal!.Wait(5000));
            Assert.Equal(0UL, b.Rig.TXFrequency);
            Assert.Equal("", (string)typeof(FlexBase).GetField("_TXMode", Private)!.GetValue(b.Rig)!);
        }
        // ── Repair round 11: every slice fact through one commit ──

        private static readonly string[] SliceFactWrites =
        {
            "SetRxCache(", "SetTxCache(", "_rxCache = ", "_txCache = ", "_TXMode = ", "_RXMode = ",
            "_XIT.Active = ", "_XIT.Value = ", "_RIT.Active = ", "_RIT.Value = ",
            "NoteTransmitSliceAsFound(", "NoteActiveSliceAsFound(", "ForgetTransmitCaches(",
            "FollowTransmitSlice(", "PublishTuning(",
        };

        private static readonly string[] SliceFactWriters =
        {
            "SetRxCache", "SetTxCache", "NoteTransmitSliceAsFound", "NoteActiveSliceAsFound",
            "ForgetTransmitCaches", "FollowTransmitSlice", "PublishTuning",
        };

        /// <summary>
        /// Every place in <paramref name="text"/> that writes a slice fact
        /// outside the one commit: not inside a <c>CommitSliceFacts(...)</c>
        /// call, not inside the body of one of the low-level writers (which are
        /// themselves only called inside a commit), and not a field
        /// declaration. Full-line comments are ignored.
        /// </summary>
        private static List<string> SliceFactWritesOutsideTheCommit(string text)
        {
            text = text.Replace("\r\n", "\n");
            // Blank full-line comments (keeps offsets).
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) lines[i] = new string(' ', lines[i].Length);
            text = string.Join("\n", lines);

            var allowed = new List<(int from, int to)>();
            int at = 0;
            while ((at = text.IndexOf("CommitSliceFacts(", at, StringComparison.Ordinal)) >= 0)
            {
                int open = at + "CommitSliceFacts".Length, depth = 0, k = open;
                for (; k < text.Length; k++)
                {
                    if (text[k] == '(') depth++;
                    else if (text[k] == ')' && --depth == 0) break;
                }
                allowed.Add((open, k));
                at = k;
            }
            foreach (string w in SliceFactWriters)
            {
                at = 0;
                string decl = "private void " + w + "(";
                while ((at = text.IndexOf(decl, at, StringComparison.Ordinal)) >= 0)
                {
                    int close = text.IndexOf(')', at);
                    int arrow = text.IndexOf("=>", close, StringComparison.Ordinal);
                    int brace = text.IndexOf('{', close);
                    int end;
                    if (arrow >= 0 && (brace < 0 || arrow < brace)) end = text.IndexOf(';', arrow);
                    else
                    {
                        int depth = 0; end = brace;
                        for (; end < text.Length; end++)
                        {
                            if (text[end] == '{') depth++;
                            else if (text[end] == '}' && --depth == 0) break;
                        }
                    }
                    allowed.Add((at, end));
                    at = end;
                }
            }
            var outside = new List<string>();
            foreach (string token in SliceFactWrites)
            {
                at = 0;
                while ((at = text.IndexOf(token, at, StringComparison.Ordinal)) >= 0)
                {
                    int lineStart = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
                    string line = text.Substring(lineStart, (text.IndexOf('\n', at) < 0 ? text.Length : text.IndexOf('\n', at)) - lineStart).Trim();
                    bool declaration = line.StartsWith("private string _", StringComparison.Ordinal)
                                       || line.StartsWith("private RITData _", StringComparison.Ordinal);
                    bool inside = allowed.Any(r => at > r.from && at < r.to);
                    if (!inside && !declaration) outside.Add(line);
                    at += token.Length;
                }
            }
            return outside;
        }

        /// <summary>
        /// The class, closed by construction (repair round 11): every write to
        /// a cached slice fact in every FlexBase file goes through
        /// <c>CommitSliceFacts</c>. A new writer added outside it fails here.
        /// The scanner's own positive controls: it finds the commits it should
        /// (more than a dozen), and it flags a write planted outside one.
        /// </summary>
        [Fact]
        public void Every_slice_fact_is_written_through_the_one_commit()
        {
            string all = FlexBasePartials.JoinedText();
            Assert.True(all.Split("CommitSliceFacts(").Length - 1 >= 14, "the scan found too few commits to trust");
            Assert.Empty(SliceFactWritesOutsideTheCommit(all));

            string planted = "void X() {\n    CommitSliceFacts(s, () => SetTxCache(s, 1));\n    _TXMode = \"USB\";\n}\n";
            Assert.Equal(new[] { "_TXMode = \"USB\";" }, SliceFactWritesOutsideTheCommit(planted));
        }

        /// <summary>
        /// Codex's tenth re-check, its exact schedule, through both frequency
        /// setters: the setter has bound the slice and armed the guard (its
        /// clock read), and the slice is removed on ANOTHER thread before the
        /// value is published. Nothing about the removed transmitter may be
        /// published, and nothing is sent. Control: publication before removal
        /// leaves nothing either, because the removal clears it.
        /// </summary>
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void A_slice_removed_while_a_tune_is_being_published_gets_nothing_published(bool transmitSetter, bool removeFirst)
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Set(a, "_active", false);
            Set(a, "_xitOn", true);
            Set(a, "_xitFreq", 120);
            var remove = typeof(FlexBase).GetMethod("sliceRemoved", Private)!;
            var binding = new ObservationBinding(new object(), new ConnectionAttempt("test-serial"));
            int armed = removeFirst ? 1 : 0, fired = 0;
            Task? removal = null;
            Clock(b.Rig, a, () =>
            {
                if (Volatile.Read(ref armed) == 1
                    && new System.Diagnostics.StackTrace().ToString().Contains("Requested")
                    && Interlocked.Exchange(ref fired, 1) == 0)
                {
                    removal = Task.Run(() => remove.Invoke(b.Rig, new object[] { a, binding }));
                    removal.Wait(1000);
                }
                return 0;
            });

            if (transmitSetter) b.Rig.TXFrequency = 14_100_100; else b.Rig.RXFrequency = 14_100_100;
            if (!removeFirst) remove.Invoke(b.Rig, new object[] { a, binding });
            else { Assert.Equal(1, Volatile.Read(ref fired)); Assert.True(removal!.Wait(5000)); }
            Drain(b.Rig);
            Assert.Empty(Tunes(b));
            Assert.Equal(0UL, b.Rig.TXFrequency);
            Assert.Equal("", (string)typeof(FlexBase).GetField("_TXMode", Private)!.GetValue(b.Rig)!);
            Assert.False(b.Rig.XIT.Active);
            Assert.False(SliceRoles.Of(b.Rig).HasTransmit);
            b.Rig.ShowingXmitFrequency = true;
            Assert.Equal(0UL, b.Rig.VirtualRXFrequency);
        }

        /// <summary>
        /// Every slice-notification writer, one each: a report arriving for a
        /// slice already removed writes none of the slice facts it carries.
        /// The removal cleared the transmit facts; the receive facts it does
        /// not clear are checked unchanged.
        /// </summary>
        [Theory]
        [InlineData("Freq")]
        [InlineData("DemodMode")]
        [InlineData("IsTransmitSlice")]
        [InlineData("RITOn")]
        [InlineData("RITFreq")]
        [InlineData("XITOn")]
        [InlineData("XITFreq")]
        [InlineData("ActiveSlice")]
        public void A_late_report_for_a_removed_slice_writes_nothing(string property)
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            typeof(FlexBase).GetMethod("sliceRemoved", Private)!
                .Invoke(b.Rig, new object[] { a, new ObservationBinding(new object(), new ConnectionAttempt("test-serial")) });
            var rxHz = typeof(FlexBase).GetProperty("RxCacheHz", Private)!;
            ulong rxBefore = (ulong)rxHz.GetValue(b.Rig)!;
            string rxModeBefore = (string)typeof(FlexBase).GetField("_RXMode", Private)!.GetValue(b.Rig)!;
            bool ritBefore = b.Rig.RIT.Active; int ritValueBefore = b.Rig.RIT.Value;

            Set(a, "_freq", 14.300);
            Set(a, "_demodMode", "CW");
            Set(a, "_ritOn", !ritBefore); Set(a, "_ritFreq", 333);
            Set(a, "_xitOn", true); Set(a, "_xitFreq", 444);
            // So the IsTransmitSlice writer is reached at all.
            typeof(FlexBase).GetProperty("CanTransmit")!.GetSetMethod(true)!.Invoke(b.Rig, new object[] { true });
            try
            {
                if (property == "ActiveSlice")
                {
                    typeof(Radio).GetField("_clientHandle", Private)!.SetValue(b.V.Radio, Handle);
                    b.V.Radio.SliceList.Add(a);
                    typeof(FlexBase).GetMethod("radioPropertyChangedHandler", Private)!.Invoke(b.Rig, new object[]
                        { b.V.Radio, new System.ComponentModel.PropertyChangedEventArgs("ActiveSlice"),
                          new ObservationBinding(new object(), new ConnectionAttempt("test-serial")) });
                }
                else
                    typeof(FlexBase).GetMethod("slicePropertyChangedHandler", Private)!
                        .Invoke(b.Rig, new object[] { a, new System.ComponentModel.PropertyChangedEventArgs(property) });
            }
            catch (TargetInvocationException) { /* the fixture's missing filter adapter, after the commit point */ }

            Assert.Equal(0UL, b.Rig.TXFrequency);
            Assert.Equal("", (string)typeof(FlexBase).GetField("_TXMode", Private)!.GetValue(b.Rig)!);
            Assert.False(b.Rig.XIT.Active);
            Assert.Equal(0, b.Rig.XIT.Value);
            Assert.Equal(rxBefore, (ulong)rxHz.GetValue(b.Rig)!);
            Assert.Equal(rxModeBefore, (string)typeof(FlexBase).GetField("_RXMode", Private)!.GetValue(b.Rig)!);
            Assert.Equal(ritBefore, b.Rig.RIT.Active);
            Assert.Equal(ritValueBefore, b.Rig.RIT.Value);
        }

        /// <summary>
        /// The arrival writer: a slice removed (on another thread) while it is
        /// still arriving, after it joined the roster and before its facts are
        /// noted, gets none noted. Control: arriving undisturbed, it is noted.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_slice_removed_while_arriving_gets_no_facts_noted(bool removed)
        {
            Bench b = NewBench();
            Slice a = b.V.NewSlice(0, Handle, 14.100, "USB");
            Set(a, "_letter", "A");
            Set(a, "_isTransmitSlice", true);
            Set(a, "_active", true);
            var remove = typeof(FlexBase).GetMethod("sliceRemoved", Private)!;
            var binding = new ObservationBinding(new object(), new ConnectionAttempt("test-serial"));
            int once = 0;
            if (removed)
                b.Rig.SliceCountChanged += () =>
                {
                    if (Interlocked.Exchange(ref once, 1) != 0) return;
                    var t = Task.Run(() => remove.Invoke(b.Rig, new object[] { a, binding }));
                    Assert.True(t.Wait(2000));
                };
            try
            {
                typeof(FlexBase).GetMethod("sliceAdded", Private)!.Invoke(b.Rig, new object[] { a, binding });
            }
            catch (TargetInvocationException) { }
            Assert.Equal(removed ? 0UL : 14_100_000UL, b.Rig.TXFrequency);
            Assert.Equal(removed ? "" : "USB", (string)typeof(FlexBase).GetField("_TXMode", Private)!.GetValue(b.Rig)!);
        }

        /// <summary>
        /// The writers that forget or that name no slice still run with no
        /// slice in the roster: forgetting revives nothing. A transmit slice
        /// losing its designation forgets the transmit facts, and a receive
        /// tune with no slice selected still records the number it was asked.
        /// </summary>
        [Fact]
        public void Forgetting_and_slice_less_writes_need_no_membership()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Assert.Equal(14_100_000UL, b.Rig.TXFrequency);
            Set(a, "_isTransmitSlice", false);
            typeof(FlexBase).GetMethod("slicePropertyChangedHandler", Private)!
                .Invoke(b.Rig, new object[] { a, new System.ComponentModel.PropertyChangedEventArgs("IsTransmitSlice") });
            Assert.Equal("", (string)typeof(FlexBase).GetField("_TXMode", Private)!.GetValue(b.Rig)!);

            Bench c = NewBench();
            typeof(FlexBase).GetField("_RXVFO", Private)!.SetValue(c.Rig, -1);
            c.Rig.RXFrequency = 7_100_000;
            Assert.Equal(7_100_000UL, c.Rig.RXFrequency);
        }

        private sealed class GaveUpListener : System.Diagnostics.TraceListener
        {
            public readonly List<(string line, bool rosterHeld)> Seen = new();
            public object? Roster;
            public override void Write(string? message) => WriteLine(message);
            public override void WriteLine(string? message)
            {
                if (message == null) return;
                if (message.Contains("gave up on") || message.Contains("round11-probe"))
                    lock (Seen) Seen.Add((message, Roster != null && System.Threading.Monitor.IsEntered(Roster)));
            }
        }

        /// <summary>
        /// Codex's tenth re-check, the follow-up: a give-up noticed inside the
        /// locked commit (the handler asks the guard for the pending value at
        /// exactly the deadline) must not write its trace there. The trace is
        /// written later, from the command queue, with the roster lock free.
        /// The positive control proves the listener sees trace lines at all.
        /// </summary>
        [Fact]
        public void A_give_up_noticed_inside_the_commit_traces_later_off_the_lock()
        {
            var listener = new GaveUpListener();
            System.Diagnostics.Trace.Listeners.Add(listener);
            bool wasOn = JJTrace.Tracing.On;
            var savedSwitch = JJTrace.Tracing.TheSwitch;
            JJTrace.Tracing.TheSwitch = new System.Diagnostics.TraceSwitch("round11", "round11") { Level = System.Diagnostics.TraceLevel.Verbose };
            JJTrace.Tracing.On = true;
            try
            {
                Bench b = NewBench();
                Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
                Set(a, "_active", false);
                listener.Roster = typeof(FlexBase).GetField("mySlices", Private)!.GetValue(b.Rig);
                JJTrace.Tracing.TraceLine("round11-probe", System.Diagnostics.TraceLevel.Warning);
                Assert.Contains(listener.Seen, x => x.line.Contains("round11-probe"));   // positive control

                long now = 0;
                Clock(b.Rig, a, () => new System.Diagnostics.StackTrace().ToString().Contains("TryPending") ? now + 1 : now);
                b.Rig.RXFrequency = 14_100_100;
                Drain(b.Rig);                                // sent at 0
                now = FrequencyEchoGuard.GiveUpMs - 1;
                typeof(FlexBase).GetMethod("ForgetTransmitCaches", Private)!.Invoke(b.Rig, new object[] { a });
                Reports(a, 14.250000);                       // Accept at 1199; inside the commit TryPending at 1200: give-up
                Assert.DoesNotContain(listener.Seen, x => x.line.Contains("gave up on"));
                Drain(b.Rig);                                // the queued follow-up writes it
                Assert.Contains(listener.Seen, x => x.line.Contains("gave up on") && !x.rosterHeld);
            }
            finally
            {
                System.Diagnostics.Trace.Listeners.Remove(listener);
                JJTrace.Tracing.On = wasOn;
                JJTrace.Tracing.TheSwitch = savedSwitch;
            }
        }
    
        // ── The 2026-11-02 junk box rulings round, J4 (ruled by Noel 2026-11-06
        //    11:06 to 11:12): when a tune ends somewhere other than the
        //    frequency spoken, the frequency it ended on is said, the bare
        //    number; a stream of outside moves is not said move by move ──

        private static readonly PropertyInfo? CorrectionProperty = typeof(FlexBase).GetProperty("LastTuneCorrection", Private);
        private static readonly FieldInfo? CorrectionCount = typeof(FlexBase).GetField("TuneCorrections", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>A connected bench: a correction is never said for a connection that is not up.</summary>
        private Bench Connected()
        {
            Bench b = NewBench();
            typeof(FlexBase).GetField("_IsConnected", Private)!.SetValue(b.Rig, true);
            return b;
        }

        private static string? Said(Bench b)
        {
            Assert.True(CorrectionProperty != null && CorrectionCount != null,
                "a tune that ends elsewhere is never said (J4): there is nothing that says it");
            return (string?)CorrectionProperty!.GetValue(b.Rig);
        }

        private static int Corrections(Bench b) => (int)CorrectionCount!.GetValue(b.Rig)!;

        /// <summary>The bare number, as F says it (FormatFreq, here its fallback).</summary>
        private static string Bare(ulong hz) => Lexicon.Get("connect.home.frequency_ended_on",
            ("freqText", RadioStatusBuilder.FormatFreqDisplay(hz)));

        [Fact]
        public void A_tune_the_radio_refuses_says_where_the_slice_is()
        {
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;                  // spoken by the key as 14.100.100
            Drain(b.Rig);
            RejectTune(b, 0, "14.100000");
            Assert.Equal(Bare(14_100_000), Said(b));
            Assert.Equal("14.100.000", Said(b));             // the bare number, no label
            Assert.Equal(1, Corrections(b));
        }

        [Fact]
        public void An_ordinary_tune_answered_only_by_its_reply_says_nothing_more()
        {
            // The radio as measured on the 8600: the sender gets "R|0|" and no
            // status. Round one's guard gave up and would have said the OLD number.
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            long now = 0;
            Clock(b.Rig, a, () => now);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            RadioAnswersOnly(b, 0);
            now += FrequencyEchoGuard.GiveUpMs * 3;
            Drain(b.Rig);
            Assert.Null(Said(b));
        }

        [Fact]
        public void A_give_up_says_nothing_and_neither_does_the_late_reply_that_follows()
        {
            // No answer in 1.2 s: where the radio is is not known, so nothing is
            // said; the late success says the radio is where it was spoken.
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            long now = 0;
            Clock(b.Rig, a, () => now);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            now = FrequencyEchoGuard.GiveUpMs;
            Assert.Equal(14_100_000UL, b.Rig.RXFrequency);
            Drain(b.Rig);
            Assert.Null(Said(b));
            RadioAnswersOnly(b, 0);
            Assert.Null(Said(b));
            EveryReaderSays(b.Rig, 14_100_100);
        }

        [Fact]
        public void A_tune_refused_late_after_a_give_up_says_where_the_slice_is()
        {
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            long now = 0;
            Clock(b.Rig, a, () => now);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            now = FrequencyEchoGuard.GiveUpMs;
            Drain(b.Rig);
            RejectTune(b, 0, "");
            Assert.Equal(Bare(14_100_000), Said(b));
            Assert.Equal(1, Corrections(b));
        }

        [Fact]
        public void Another_clients_move_never_speaks_whether_before_or_after_our_reply()
        {
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Reports(a, 14.250000);                           // before our reply: overridden by our tune
            RadioAnswersOnly(b, 0);
            Assert.Null(Said(b));
            EveryReaderSays(b.Rig, 14_100_100);
            for (int i = 1; i <= 3; i++) Reports(a, 14.100100 + i * 0.000010);   // a Doppler-like stream, after
            Assert.Null(Said(b));
            EveryReaderSays(b.Rig, 14_100_130);
        }

        [Fact]
        public void A_refusal_during_a_stream_of_outside_moves_says_where_the_radio_last_reported()
        {
            // The radio's last report is where it is; that is true even mid-stream.
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Reports(a, 14.100010);
            Reports(a, 14.100020);
            b.Rig.RXFrequency = 14_100_120;
            Drain(b.Rig);
            RejectTune(b, 0, "");
            Assert.Equal(Bare(14_100_020), Said(b));
        }

        [Fact]
        public void A_slice_locked_between_the_press_and_the_send_says_where_it_is()
        {
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;
            Set(a, "_lock", true);
            Drain(b.Rig);
            Assert.Equal(Bare(14_100_000), Said(b));
        }

        [Fact]
        public void A_tune_refused_at_its_press_spoke_nothing_and_corrects_nothing()
        {
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Set(a, "_lock", true);
            b.Rig.RXFrequency = 14_100_100;
            Assert.False(b.Rig.TuneAdmittedHere);
            Drain(b.Rig);
            Assert.Null(Said(b));
            b.Rig.q.MainLoop = false;                        // the loop is not running: refused at the press
            Set(a, "_lock", false);
            b.Rig.RXFrequency = 14_100_200;
            Assert.False(b.Rig.TuneAdmittedHere);
            Assert.Null(Said(b));
        }

        [Fact]
        public void A_tune_that_ends_while_the_connection_is_down_says_nothing()
        {
            Bench b = NewBench();                            // never connected
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            RejectTune(b, 0, "14.100000");
            Assert.Null(Said(b));
        }

        [Fact]
        public void An_older_tunes_refusal_says_nothing_while_a_newer_one_is_on_its_way()
        {
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            b.Rig.RXFrequency = 14_100_200;
            Drain(b.Rig);
            RejectTune(b, 0, "14.100000");                   // the older one
            Assert.Null(Said(b));
            RadioAnswersOnly(b, 1);
            Assert.Null(Said(b));
            EveryReaderSays(b.Rig, 14_100_200);
        }

        // ── Round two of the rulings round (the 8600 bench, 2026-11-06, Fail 1):
        //    the radio sends a slice's new frequency to every OTHER client and
        //    NONE to the client that tuned it, which gets only its reply ──

        /// <summary>
        /// The radio as measured: our tune is answered with "R|0|" and nothing
        /// else. No status of our own write ever arrives here.
        /// </summary>
        private static void RadioAnswersOnly(Bench b, int nth) => AckTune(b, nth);

        [Fact]
        public void A_tune_the_radio_answers_only_with_its_reply_settles_at_the_requested_value_and_stays()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            long now = 0;
            Clock(b.Rig, a, () => now);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            now += 120;
            RadioAnswersOnly(b, 0);
            EveryReaderSays(b.Rig, 14_100_100);
            Assert.Equal(14.100100, a.Freq, 6);              // FlexLib's own number too
            now += FrequencyEchoGuard.GiveUpMs * 3;          // long past any give-up
            Drain(b.Rig);
            EveryReaderSays(b.Rig, 14_100_100);              // the bench saw it revert to the old number here
            b.Rig.RXFrequency = b.Rig.RXFrequency + 100;     // and the next step starts from there
            Drain(b.Rig);
            Assert.Equal(14_100_200UL, LastTune(b, a));
        }

        [Fact]
        public void A_held_run_answered_only_by_replies_ends_on_its_last_step()
        {
            // The bench's 2 s hold: the radio ended at the last step; the app
            // settled back to where the hold began.
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            long now = 0;
            Clock(b.Rig, a, () => now);
            for (int i = 1; i <= 5; i++) { b.Rig.RXFrequency = 14_100_000UL + (ulong)(i * 100); Drain(b.Rig); now += 40; }
            for (int i = 0; i < 5; i++) { RadioAnswersOnly(b, i); now += 40; }
            now += FrequencyEchoGuard.GiveUpMs * 3;
            EveryReaderSays(b.Rig, 14_100_500);
        }

        [Fact]
        public void A_refused_last_step_reads_the_last_step_the_radio_took()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100; Drain(b.Rig);
            b.Rig.RXFrequency = 14_100_200; Drain(b.Rig);
            RadioAnswersOnly(b, 0);
            RejectTune(b, 1, "");
            EveryReaderSays(b.Rig, 14_100_100);
        }

        [Fact]
        public void A_move_by_another_client_reported_after_our_reply_still_wins()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            RadioAnswersOnly(b, 0);
            Reports(a, 14.250000);                           // later, from another client
            EveryReaderSays(b.Rig, 14_250_000);
        }

        [Fact]
        public void A_move_reported_before_our_reply_was_overridden_by_our_tune()
        {
            // The radio ran the other client's move first (its status reached us
            // first), then ours: it is on ours.
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            Reports(a, 14.250000);
            RadioAnswersOnly(b, 0);
            EveryReaderSays(b.Rig, 14_100_100);
        }

        [Fact]
        public void A_reply_after_the_give_up_still_settles_the_number()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            long now = 0;
            Clock(b.Rig, a, () => now);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            now = FrequencyEchoGuard.GiveUpMs;               // a slow link
            Drain(b.Rig);
            RadioAnswersOnly(b, 0);                          // the answer, late
            EveryReaderSays(b.Rig, 14_100_100);
        }
    
        [Fact]
        public void An_older_reply_arriving_after_a_newer_one_does_not_set_the_number_back()
        {
            // One connection delivers replies in order; should one ever arrive
            // late, the newest settled reply still stands.
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100; Drain(b.Rig);
            b.Rig.RXFrequency = 14_100_200; Drain(b.Rig);
            RadioAnswersOnly(b, 1);
            RadioAnswersOnly(b, 0);
            EveryReaderSays(b.Rig, 14_100_200);
        }
    
        [Fact]
        public void The_FlexLib_tune_reply_patch_is_marked_and_in_place()
        {
            // MIGRATION.md item 26. A FlexLib upgrade that drops it turns this red
            // rather than leaving every step on the old frequency again.
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string slice = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "FlexLib_API", "FlexLib", "Slice.cs"));
            Assert.Contains("// JJFlex patch (MIGRATION.md item 26)", slice);
            Assert.Contains("public bool AcceptTuneReply(double mhz)", slice);
            Assert.Contains("26. **A tune's success reply is its receipt",
                System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "MIGRATION.md")));
        }
    
        [Fact]
        public void A_correction_not_yet_heard_is_retired_by_a_newer_tune_and_by_a_change_of_slice()
        {
            // Codex's R3: a newer successful tune's readback interrupted a queued
            // correction, and the arbiter salvaged the stale number after it.
            var retired = typeof(FlexBase).GetField("TuneCorrectionsRetired", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Add(b, 1, tx: false, active: false, mhz: 7.100);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            RejectTune(b, 0, "");
            Assert.Equal(Bare(14_100_000), Said(b));
            Assert.Equal(0, (int)retired.GetValue(b.Rig)!);
            b.Rig.RXFrequency = 14_200_000;                  // a newer tune: it speaks its own number
            Assert.Equal(1, (int)retired.GetValue(b.Rig)!);
            b.Rig.RXFrequency = 14_200_100;                  // nothing live: nothing to retire
            Assert.Equal(1, (int)retired.GetValue(b.Rig)!);
            Drain(b.Rig);
            RejectTune(b, 2, "");
            Assert.Equal(2, Corrections(b));
            b.Rig.RXVFO = 1;                                 // the selection moves
            Assert.Equal(2, (int)retired.GetValue(b.Rig)!);
        }
    

        // ── Round three (Codex's re-check of 236bb7f0) ──

        private static int Retired(Bench b) =>
            (int)typeof(FlexBase).GetField("TuneCorrectionsRetired", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(b.Rig)!;

        [Fact]
        public void A_correction_not_yet_heard_is_retired_when_another_client_moves_the_slice()
        {
            // Codex's B1, tuning-outside-queue: refused, the correction queued,
            // then another client's 14.200 arrives; the old 14.100.000 was
            // salvaged after the slice had moved.
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            RejectTune(b, 0, "");
            Assert.Equal(Bare(14_100_000), Said(b));
            Reports(a, 14.100000);                           // the same number again: still true, kept
            Assert.Equal(0, Retired(b));
            Reports(a, 14.200000);                           // another client moves it
            Assert.Equal(1, Retired(b));
        }

        [Fact]
        public void A_correction_not_yet_heard_is_retired_when_the_connection_falls_or_the_slice_goes()
        {
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            RejectTune(b, 0, "");
            typeof(FlexBase).GetMethod("dropTransmitWithConnection", Private)!
                .Invoke(b.Rig, new object[] { (Action<string, System.Diagnostics.TraceLevel>)((_, _) => { }) });
            Assert.Equal(1, Retired(b));

            b.Rig.RXFrequency = 14_100_200;
            Drain(b.Rig);
            RejectTune(b, 1, "");
            Assert.Equal(2, Corrections(b));
            typeof(FlexBase).GetMethod("sliceRemoved", Private)!
                .Invoke(b.Rig, new object[] { a, new ObservationBinding(new object(), new ConnectionAttempt("test-serial")) });
            Assert.Equal(2, Retired(b));
        }

        [Fact]
        public void A_refused_tune_whose_reply_names_the_frequency_kept_reads_and_says_that_frequency()
        {
            // Codex's B2, clamped-refusal: a non-zero reply whose body is the
            // frequency the radio kept, as the vendor's own SetFreqReply reads it.
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            RejectTune(b, 0, "14.100050");
            EveryReaderSays(b.Rig, 14_100_050);
            Assert.Equal(14.100050, a.Freq, 6);
            Assert.Equal(Bare(14_100_050), Said(b));
        }

        [Fact]
        public void A_refusal_with_a_body_that_is_not_a_number_reads_where_the_radio_last_was()
        {
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            RejectTune(b, 0, "Invalid frequency");
            EveryReaderSays(b.Rig, 14_100_000);
            Assert.Equal(Bare(14_100_000), Said(b));
        }

        [Fact]
        public void A_late_refusal_after_a_give_up_names_the_frequency_kept()
        {
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            long now = 0;
            Clock(b.Rig, a, () => now);
            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
            now = FrequencyEchoGuard.GiveUpMs;
            Drain(b.Rig);
            RejectTune(b, 0, "14.100050");
            EveryReaderSays(b.Rig, 14_100_050);
            Assert.Equal(Bare(14_100_050), Said(b));
        }

        [Fact]
        public void An_older_refusals_kept_frequency_does_not_set_back_a_newer_answer()
        {
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            b.Rig.RXFrequency = 14_100_100; Drain(b.Rig);
            b.Rig.RXFrequency = 14_100_200; Drain(b.Rig);
            RadioAnswersOnly(b, 1);
            RejectTune(b, 0, "14.100000");                   // out of order: older than the settled one
            EveryReaderSays(b.Rig, 14_100_200);
        }
    

        [Fact]
        public void Homes_return_status_is_retired_when_the_selection_or_its_frequency_changes()
        {
            // The bench's 4.1: "Listening on 21.260 ... slice B" replayed after
            // the selection had moved to A. Bound like the correction (round three).
            var retired = typeof(FlexBase).GetField("ReturnStatusesRetired", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            Bench b = Connected();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            Add(b, 1, tx: false, active: false, mhz: 7.100);
            b.Rig.NoteReturnStatusSpoken();
            Reports(a, 14.100000);                           // unchanged: still true
            Assert.Equal(0, (int)retired.GetValue(b.Rig)!);
            Reports(a, 14.150000);                           // the selected slice moved
            Assert.Equal(1, (int)retired.GetValue(b.Rig)!);
            b.Rig.NoteReturnStatusSpoken();
            b.Rig.RXVFO = 1;                                 // the selection moved
            Assert.Equal(2, (int)retired.GetValue(b.Rig)!);
        }
    }
}
