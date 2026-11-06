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
        public void A_report_nobody_asked_for_is_believed_at_once()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);

            b.Rig.RXFrequency = 14_100_100;
            Drain(b.Rig);
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
            Reports(a, 14.100400);                           // the last one, settled
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
        /// Codex's blocker 3: the slice is removed on another thread at the
        /// moment WriteTuneTo has checked it and is about to write (the guard's
        /// Sending boundary). The tune must not reach the transport for a slice
        /// that is no longer ours: either the removal waits for the write, or
        /// the write sees the removal. Here the removal is started on another
        /// thread and given time to finish before the write goes on.
        /// </summary>
        [Fact]
        public void A_slice_removed_between_the_check_and_the_write_gets_no_tune_after_its_removal()
        {
            Bench b = NewBench();
            Slice a = Add(b, 0, tx: true, active: true, mhz: 14.100);
            var mine = (List<Slice>)typeof(FlexBase).GetField("mySlices", Private)!.GetValue(b.Rig)!;
            var remove = typeof(FlexBase).GetMethod("sliceRemoved", Private)!;
            var binding = new ObservationBinding(new object(), new ConnectionAttempt("test-serial"));

            b.Rig.RXFrequency = 14_100_100;
            int armed = 0, fired = 0;
            Task? removal = null;
            Clock(b.Rig, a, () =>
            {
                if (Volatile.Read(ref armed) == 1 && Interlocked.Exchange(ref fired, 1) == 0)
                {
                    removal = Task.Run(() => remove.Invoke(b.Rig, new object[] { a, binding }));
                    removal.Wait(1000);                      // it finishes now unless the write holds it off
                }
                return 0;
            });
            bool? ownedAtWrite = null;
            b.V.Transport.CommandWritten = cmd =>
            {
                if (cmd.StartsWith("slice tune ", StringComparison.Ordinal))
                    lock (mine) ownedAtWrite = mine.Contains(a);
            };

            Volatile.Write(ref armed, 1);
            Drain(b.Rig);
            Assert.Equal(1, Volatile.Read(ref fired));       // the boundary was reached
            Assert.True(removal!.Wait(5000));                // and the removal did happen
            lock (mine) Assert.DoesNotContain(a, mine);
            // The write went ahead while the slice was still ours, and only then
            // did the removal finish: the removal waited for it.
            Assert.True(ownedAtWrite.HasValue, "no tune was written at all");
            Assert.True(ownedAtWrite!.Value, "a tune was written for a slice already removed");
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
            Assert.Contains("LongFreqToLibFreq(ValueOf(txSlice, _rxCache, _txCache))", xv);
            string lb = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "FlexBase.Listenback.cs"));
            Assert.DoesNotContain("tx.Freq", lb);
            Assert.Contains("double txMhz = LongFreqToLibFreq(ValueOf(tx, _rxCache, _txCache));", lb);
            string main = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "FlexBase.cs"));
            Assert.Contains("earsTransmit, earsAntenna, earsMhz))", main);
        }
    }
}
