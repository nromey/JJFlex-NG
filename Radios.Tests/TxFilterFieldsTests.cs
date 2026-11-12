#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.Speech;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The transmit filter fields on Home and in the Audio Workshop have ONE
    /// speaker (round nine of the 2026-11-06 junk box, #747). The fields never
    /// announce the number asked for; the only speech is the applied result,
    /// on one Latest key shared by both fields of a surface, for that field's
    /// newest request on the same connection.
    /// </summary>
    /// <remarks>
    /// <para>Codex's fifth re-verify found four schedules that still ended on
    /// a false edge while the field and the correction were two speech
    /// streams; each round's patch had opened a new gap, so this round
    /// removes the second stream. These are Codex's four schedules and two
    /// positive controls, on the real speech arbiter with a manual clock, the
    /// real <c>SetTXFilter</c>, and the vendor setters over the fake
    /// transport. The field's own silence is the WPF half, pinned at the
    /// source in <see cref="The_fields_say_nothing_of_their_own_and_the_hosts_use_the_one_speaker"/>.</para>
    /// <para>Sentences are DRAFT literals.</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TxFilterFieldsTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        private readonly VendorRadioFixture _vendor = new VendorRadioFixture("1111-2222-6300-4444");
        private readonly FakeSpeechClock _clock = new FakeSpeechClock();
        /// <summary>Every text handed to the reader, in order (not proof it was heard).</summary>
        private readonly List<string> _heard = new List<string>();
        private readonly SpeechArbiter _arbiter;
        private bool _open = true;
        private readonly TxFilterFields _fields;
        private readonly List<Func<bool>> _refreshes = new();

        // The reader: every hand-over is tracked, an interrupt cuts what is
        // still outstanding (Cancelled, by us, no words reached), and the
        // test says when the reader finished the rest (Completed).
        private long _nextTicket = 1;
        private readonly List<(long Ticket, string Text)> _outstanding = new();
        private readonly List<(long Ticket, string Text, SpeechOutcome Outcome)> _toReport = new();
        private bool _refuse;
        /// <summary>A reader that never reports an outcome (JAWS, or any reader but NVDA).</summary>
        private bool _untracked;

        private const string SurfaceKey = "value-field:TX filter (test surface)";

        public TxFilterFieldsTests()
        {
            _rig.theRadio = _vendor.Radio;
            _rig.q.MainLoop = true;   // the queue holds writes for Drain, as the main loop would run them
            _arbiter = new SpeechArbiter(_clock, () => VerbosityLevel.Chatty, Reader, () => { }, (_, _, _, _) => { });
            _fields = new TxFilterFields(SurfaceKey, "TX Filter Low", "TX Filter High", () => _rig, () => _open,
                speak: (key, compose) => _arbiter.LatestComposed(key, compose, VerbosityLevel.Terse, "test"),
                refresh: stillCurrent => _refreshes.Add(stillCurrent),
                retire: key => _arbiter.RetireLatest(key));
        }

        private SpeechHandoff Reader(string text, bool interrupt, SpeechIntent? intent, VerbosityLevel? level, string? origin, bool salvaged)
        {
            _heard.Add(text);
            if (_refuse) return SpeechHandoff.NotReached;
            if (_untracked) return SpeechHandoff.Untracked;
            if (interrupt)
            {
                foreach (var (ticket, cut) in _outstanding)
                    _toReport.Add((ticket, cut, SpeechOutcome.Cancelled(0, Words(cut), byUs: true, elapsedMs: 10)));
                _outstanding.Clear();
            }
            long t = _nextTicket++;
            _outstanding.Add((t, text));
            return SpeechHandoff.TrackedAs(t);
        }

        private static int Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        /// <summary>Report the cuts the reader has made.</summary>
        private void Pump()
        {
            var due = _toReport.ToList();
            _toReport.Clear();
            foreach (var (ticket, text, outcome) in due) _arbiter.OnOutcome(ticket, text, outcome);
        }

        /// <summary>The reader finishes everything it still holds.</summary>
        private void Finish()
        {
            Pump();
            var done = _outstanding.ToList();
            _outstanding.Clear();
            foreach (var (ticket, text) in done)
                _arbiter.OnOutcome(ticket, text, SpeechOutcome.Completed(Words(text), Words(text), 100));
        }

        public void Dispose()
        {
            _rig.theRadio = null;   // never FlexBase's to disconnect
            _rig.Dispose();
        }

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

        /// <summary>Let everything waiting be handed over, and the reader finish it.</summary>
        private void Settle()
        {
            Pump();
            _clock.Advance(5000);
            Finish();
        }

        private (int, int) Actual => (_vendor.Radio.TXFilterLow, _vendor.Radio.TXFilterHigh);

        // ------------------------------------------------------------------
        // Codex's four schedules
        // ------------------------------------------------------------------

        /// <summary>
        /// 1. The write cannot run (the command loop is not running), so its
        /// refusal arrives at once, before the field would have announced. The
        /// field used to say its number after the refusal, false.
        /// </summary>
        [Fact]
        public void A_refusal_is_the_last_word_and_the_asked_number_is_never_said()
        {
            Tx(2700, 2800);
            _rig.q.MainLoop = false;
            _fields.Write(lowField: true, 2800);
            Settle();
            Assert.Equal("TX filter not changed. The radio is not taking changes right now.", _heard.Last());
            Assert.DoesNotContain("TX Filter Low 2800", _heard);
            Assert.Equal((2700, 2800), Actual);
        }

        /// <summary>
        /// 2. A held-back write, then a true later value for the same field
        /// before the first result is heard. The later, true result is last.
        /// </summary>
        [Fact]
        public void A_later_true_value_replaces_an_older_held_back_result()
        {
            Tx(2700, 2800);
            _fields.Write(lowField: true, 2800);
            Drain();
            _fields.Write(lowField: true, 2700);
            Drain();
            Settle();
            Assert.Equal("TX Filter Low 2700", _heard.Last());
            Assert.Equal((2700, 2800), Actual);
        }

        /// <summary>
        /// 3. The low edge is held back while a newer request for the HIGH edge
        /// is queued. Each field has its own newest request, so the low's
        /// result is not discarded for the high's, and the asked-for low is
        /// never said.
        /// </summary>
        [Fact]
        public void A_newer_request_for_the_other_edge_does_not_leave_a_false_number()
        {
            Tx(2700, 2800);
            _fields.Write(lowField: true, 2800);
            _fields.Write(lowField: false, 2850);
            Drain();
            Settle();
            Assert.DoesNotContain("TX Filter Low 2800", _heard);
            // Round eleven: the high result replaced the unheard low result, so
            // it covers it (Codex's sixth re-verify: the shared key must not
            // erase the other edge's news).
            Assert.Equal("TX Filter Low 2750, at limit. TX Filter High 2850", _heard.Last());
            Assert.Equal((2750, 2850), Actual);
        }

        /// <summary>
        /// 4. The low edge's held-back result is waiting when the high edge
        /// truly changes. One key for both fields: the newer, true result
        /// replaces the older one, and no whole-pair sentence about the old
        /// high edge is left to be said.
        /// </summary>
        [Fact]
        public void An_older_result_cannot_outlive_a_true_change_to_the_other_edge()
        {
            Tx(2700, 2800);
            _fields.Write(lowField: true, 2800);
            Drain();
            _fields.Write(lowField: false, 2850);
            Drain();
            Settle();
            // Round eleven: the low result may not have been heard yet, so the
            // newer result covers both edges.
            Assert.Equal("TX Filter Low 2750, at limit. TX Filter High 2850", _heard.Last());
            Assert.DoesNotContain("TX filter 2750 to 2800, at limit", _heard);
            Assert.Equal((2750, 2850), Actual);
        }

        /// <summary>
        /// Codex's sixth re-verify, the erasure schedule. The low edge's result
        /// is WAITING (a result was said just before, so the next one on the
        /// key waits its turn) when the high edge changes. The high result
        /// replaces it on the shared key, so it must carry the low news too:
        /// round nine's version of this test asserted the low 2750 was never
        /// heard, which pinned the erasure.
        /// </summary>
        [Fact]
        public void A_result_that_replaces_a_waiting_one_covers_it()
        {
            Tx(2600, 2800);
            _fields.Write(lowField: true, 2700);
            Drain();                                   // "TX Filter Low 2700", said at once
            _clock.Advance(50);
            _fields.Write(lowField: true, 2800);
            Drain();                                   // held back to 2750: waits its turn
            _fields.Write(lowField: false, 2850);
            Drain();
            Settle();
            Assert.Equal("TX Filter Low 2750, at limit. TX Filter High 2850", _heard.Last());
            Assert.Equal((2750, 2850), Actual);
        }

        /// <summary>Codex's positive control: let the low result be heard first, and the high result says only the high.</summary>
        [Fact]
        public void Control_a_result_after_the_others_was_heard_says_only_its_own_edge()
        {
            Tx(2600, 2800);
            _fields.Write(lowField: true, 2700);
            Drain();
            _clock.Advance(50);
            _fields.Write(lowField: true, 2800);
            Drain();
            Settle();                                  // the low result is heard
            Assert.Equal("TX Filter Low 2750, at limit", _heard.Last());
            _fields.Write(lowField: false, 2850);
            Drain();
            Settle();
            Assert.Equal("TX Filter High 2850", _heard.Last());
        }

        // ------------------------------------------------------------------
        // Round eleven: end stops, retiring, refreshes, stale writes, menu
        // ------------------------------------------------------------------

        /// <summary>
        /// Codex's end-stop schedule: the field's display ran ahead to 9950
        /// (End, or typed), FlexLib held the write to 2750, and a press against
        /// the end arrives before the window refreshed. The end stop says the
        /// APPLIED value, never the field's optimistic one.
        /// </summary>
        [Fact]
        public void An_end_stop_says_the_applied_value_not_the_fields_own()
        {
            Tx(2700, 2800);
            _fields.Write(lowField: true, 9950);
            Drain();
            Settle();
            _fields.SayEndStop(lowField: true, atMinimum: false, shownValue: 9950);
            Settle();
            Assert.Equal("TX Filter Low 2750, maximum", _heard.Last());
            Assert.DoesNotContain("TX Filter Low 9950, maximum", _heard);
        }

        private void SayLowThenLeaveOneWaiting()
        {
            Tx(100, 2800);
            _fields.Write(lowField: true, 150);
            Drain();                                   // said at once
            _clock.Advance(50);
            _fields.Write(lowField: true, 200);
            Drain();                                   // waits its turn
        }

        [Fact]
        public void Closing_retires_a_result_still_waiting()
        {
            SayLowThenLeaveOneWaiting();
            _open = false;
            _fields.Invalidate();                      // what the Workshop's Closed handler does
            Settle();
            Assert.DoesNotContain("TX Filter Low 200", _heard);
        }

        [Fact]
        public void Invalidating_retires_a_result_still_waiting()
        {
            SayLowThenLeaveOneWaiting();
            _fields.Invalidate();                      // Initialize, Detach, SetRig
            Settle();
            Assert.DoesNotContain("TX Filter Low 200", _heard);
        }

        [Fact]
        public void A_connection_change_retires_a_result_still_waiting()
        {
            SayLowThenLeaveOneWaiting();
            RaiseCommandConnectionChanged(CommandConnectionState.Disconnected);
            Settle();
            Assert.DoesNotContain("TX Filter Low 200", _heard);
        }

        /// <summary>Control: with nothing ended, the waiting result is heard.</summary>
        [Fact]
        public void Control_with_nothing_ended_the_waiting_result_is_heard()
        {
            SayLowThenLeaveOneWaiting();
            Settle();
            Assert.Equal("TX Filter Low 200", _heard.Last());
        }

        private void RaiseCommandConnectionChanged(CommandConnectionState state)
        {
            var report = (CommandConnectionChanged)Activator.CreateInstance(typeof(CommandConnectionChanged), Private, null,
                new object[] { _vendor.Radio.CurrentCommandConnection!, state }, null)!;
            var field = typeof(Radio).GetField("CommandConnectionChanged", Private)!;
            ((Action<CommandConnectionChanged>?)field.GetValue(_vendor.Radio))?.Invoke(report);
        }

        /// <summary>
        /// A write queued on one command connection does not run on its
        /// replacement: it reports not written, and nothing is sent on the new
        /// connection (Codex's reconnect schedule).
        /// </summary>
        [Fact]
        public void A_write_queued_before_a_reconnect_does_not_run_on_the_new_connection()
        {
            Tx(100, 2800);
            FilterApplied? got = null;
            _rig.SetTXFilter(300, null, a => got = a);
            var fresh = new FakeCommandTransport();
            typeof(Radio).GetField("_commandCommunication", Private)!.SetValue(_vendor.Radio, fresh);
            Drain();
            Assert.True(got!.Value.NotWritten);
            Assert.DoesNotContain(fresh.Commands, c => c.Contains("transmit set filter_"));
            Assert.Equal(100, _vendor.Radio.TXFilterLow);
        }

        /// <summary>A window refresh queued before the surface was invalidated does nothing when it runs.</summary>
        [Fact]
        public void A_refresh_rechecks_that_it_still_answers_when_it_runs()
        {
            Tx(100, 2800);
            _fields.Write(lowField: true, 300);
            Drain();
            var stillCurrent = Assert.Single(_refreshes);
            Assert.True(stillCurrent());
            _fields.Invalidate();
            Assert.False(stillCurrent());
        }

        /// <summary>The menu's delayed sentence is not said once the window holds another rig.</summary>
        [Fact]
        public void A_menu_sentence_needs_the_window_to_still_hold_the_originating_rig()
        {
            Tx(100, 2800);
            var applied = WriteTx(300);
            FlexBase? window = _rig;
            var stillTrue = FilterWriteFreshness.StillTrue(_rig, applied, transmit: true, windowRig: () => window);
            Assert.True(stillTrue());
            using var other = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            window = other;
            Assert.False(stillTrue());
        }

        // ------------------------------------------------------------------
        // Codex's two positive controls
        // ------------------------------------------------------------------

        [Fact]
        public void Control_a_held_back_write_says_where_it_stopped_last()
        {
            Tx(2700, 2800);
            _fields.Write(lowField: true, 2800);
            Drain();
            Settle();
            Assert.Equal("TX Filter Low 2750, at limit", _heard.Last());
            Assert.Equal((2750, 2800), Actual);
        }

        [Fact]
        public void Control_a_later_success_on_the_same_key_replaces_the_waiting_result()
        {
            Tx(2700, 2800);
            _fields.Write(lowField: true, 2800);
            Drain();
            _fields.Write(lowField: true, 2600);
            Drain();
            Settle();
            Assert.Equal("TX Filter Low 2600", _heard.Last());
        }

        // ------------------------------------------------------------------
        // Request identity: per field, per connection, while open
        // ------------------------------------------------------------------

        /// <summary>
        /// Each field answers its OWN newest request: a newer request for the
        /// high edge does not silence the low edge's result (Codex's third
        /// schedule, as it would sound with time between the two results).
        /// </summary>
        [Fact]
        public void Each_field_answers_its_own_newest_request()
        {
            Tx(2700, 2800);
            _fields.Write(lowField: true, 2800);
            _fields.Write(lowField: false, 2850);
            var item = _rig.q.Dequeue();
            ((FlexBase.FunctionDel)item.Item)();   // the low edge's write runs, held back to 2750
            Settle();
            Assert.Equal("TX Filter Low 2750, at limit", _heard.Last());
            Drain();
            Settle();
            Assert.Equal("TX Filter High 2850", _heard.Last());
        }

        [Fact]
        public void A_completion_on_a_different_connection_says_nothing()
        {
            Tx(100, 2800);
            _fields.Write(lowField: true, 300);
            // A reconnect: the same rig and Radio, a new command connection.
            typeof(Radio).GetField("_commandCommunication", Private)!.SetValue(_vendor.Radio, new FakeCommandTransport());
            Drain();
            Settle();
            Assert.Empty(_heard);
        }

        [Fact]
        public void A_completion_after_the_surface_was_invalidated_says_nothing()
        {
            Tx(100, 2800);
            _fields.Write(lowField: true, 300);
            _fields.Invalidate();   // Initialize, Detach or SetRig
            Drain();
            Settle();
            Assert.Empty(_heard);
        }

        [Fact]
        public void A_completion_after_the_surface_closed_says_nothing()
        {
            Tx(100, 2800);
            _fields.Write(lowField: true, 300);
            _open = false;
            Drain();
            Settle();
            Assert.Empty(_heard);
        }

        [Fact]
        public void The_fields_say_nothing_of_their_own_and_the_hosts_use_the_one_speaker()
        {
            string field = System.IO.File.ReadAllText(System.IO.Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Controls", "ValueFieldControl.xaml.cs"));
            Assert.Contains("public bool SpeaksOnlyTheAppliedResult { get; set; }", field, StringComparison.Ordinal);
            Assert.True(CountOf(field, "if (!SpeaksOnlyTheAppliedResult) AnnounceValue(atLimit: false);") == 2, "single steps and held keys");
            Assert.Contains("if (!SpeaksOnlyTheAppliedResult)", field, StringComparison.Ordinal);

            foreach (var rel in new[] { new[] { "JJFlexWpf", "Controls", "ScreenFieldsPanel.xaml.cs" }, new[] { "JJFlexWpf", "Dialogs", "AudioWorkshopDialog.TxAudio.cs" } })
            {
                string host = System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { IntegrationPassTree.Root }.Concat(rel).ToArray()));
                Assert.True(CountOf(host, ".SpeaksOnlyTheAppliedResult = true;") == 2, string.Join("/", rel) + ": both TX filter fields");
                Assert.Contains("new TxFilterFields(", host, StringComparison.Ordinal);
                Assert.Contains(".Write(lowField: true, v)", host, StringComparison.Ordinal);
                Assert.Contains(".Write(lowField: false, v)", host, StringComparison.Ordinal);
                Assert.DoesNotContain("SayFieldCorrection", host, StringComparison.Ordinal);
            }
            string panel = System.IO.File.ReadAllText(System.IO.Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Controls", "ScreenFieldsPanel.xaml.cs"));
            Assert.True(CountOf(panel, "_homeTxFilter.Invalidate();") >= 2, "Initialize and Detach invalidate Home's requests");

            // Round eleven: the end stop goes to the one speaker, which says the
            // applied value; the refresh re-checks when it runs; the bounds are
            // the radio's own limits for the applied pair.
            Assert.Contains("if (SpeaksOnlyTheAppliedResult) AppliedEndStop?.Invoke(_value <= LowestForOperator);", field, StringComparison.Ordinal);
            foreach (var rel in new[] { new[] { "JJFlexWpf", "Controls", "ScreenFieldsPanel.xaml.cs" }, new[] { "JJFlexWpf", "Dialogs", "AudioWorkshopDialog.TxAudio.cs" } })
            {
                string host = System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { IntegrationPassTree.Root }.Concat(rel).ToArray()));
                Assert.True(CountOf(host, ".AppliedEndStop = atMinimum =>") == 2, string.Join("/", rel) + ": both fields route their end stop");
                Assert.Contains("refresh: stillCurrent => Dispatcher.BeginInvoke(() =>", host, StringComparison.Ordinal);
                // The Workshop reads its LIVE radio (#1039): after a Disconnect a
                // filter answer finds none and writes nothing to its fields.
                string r = rel[^1].StartsWith("AudioWorkshop", StringComparison.Ordinal) ? "LiveRig" : "_rig";
                Assert.Contains("if (!stillCurrent() || " + r + " == null) return;", host, StringComparison.Ordinal);
                // Round twelve: the pair is read when the closure runs, never captured at completion.
                Assert.Contains("TxFilterFields(" + r + ".TXFilterLow, " + r + ".TXFilterHigh); }", host, StringComparison.Ordinal);
                Assert.DoesNotContain("applied.Low, applied.High", host, StringComparison.Ordinal);
                Assert.Contains("Control.Max = Math.Max(0, high - 50);", host, StringComparison.Ordinal);
                Assert.Contains("Control.Min = Math.Min(10000, low + 50);", host, StringComparison.Ordinal);
            }
            string workshop = System.IO.File.ReadAllText(System.IO.Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "AudioWorkshopDialog.TxAudio.cs"));
            Assert.Contains("_workshopTxFilter", workshop, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // Round twelve: composed when it is spoken; heard only when Completed
        // (Codex's seventh re-verify: B4, B3 end stop, B3/F3 other door, F4)
        // ------------------------------------------------------------------

        /// <summary>
        /// B4. The field's Low 150 was heard; another door then set Low 300;
        /// the field asks for High 3000 and the write is refused. The refusal
        /// used to rebuild the pair from what was last said, and claimed Low
        /// 150 as current. The values come from the radio.
        /// </summary>
        [Fact]
        public void A_refusal_states_the_edges_as_the_radio_holds_them_not_as_last_said()
        {
            Tx(100, 2800);
            _fields.Write(lowField: true, 150);
            Drain();
            Finish();                                  // "TX Filter Low 150", heard
            _clock.Advance(50);
            WriteTx(300);                              // another door
            _rig.q.MainLoop = false;
            _fields.Write(lowField: false, 3000);      // refused at once
            Settle();
            Assert.Equal("TX Filter Low 300. TX filter not changed. The radio is not taking changes right now.", _heard.Last());
            Assert.Equal((300, 2800), Actual);
        }

        /// <summary>Codex's control: a refusal with no history says only that nothing changed.</summary>
        [Fact]
        public void Control_a_refusal_with_nothing_owed_says_only_that_nothing_changed()
        {
            Tx(100, 2800);
            _rig.q.MainLoop = false;
            _fields.Write(lowField: false, 3000);
            Settle();
            Assert.Equal("TX filter not changed. The radio is not taking changes right now.", Assert.Single(_heard));
        }

        private void HearLow150ThenLeaveLow200Waiting(int high)
        {
            Tx(100, high);
            _fields.Write(lowField: true, 150);
            Drain();
            Finish();                                  // "TX Filter Low 150", heard
            _clock.Advance(50);
            _fields.Write(lowField: true, 200);
            Drain();                                   // waits its turn on the key
        }

        /// <summary>
        /// B3, the end stop. An end stop on the high edge replaces the low
        /// edge's waiting result on the shared key, so it states the low edge
        /// too, and what it did not state is not credited: a later high result
        /// does not need to repeat the low, because the low WAS stated and heard.
        /// </summary>
        [Fact]
        public void An_end_stop_covers_an_edge_still_owed_and_credits_only_what_it_said()
        {
            HearLow150ThenLeaveLow200Waiting(high: 10000);
            _fields.SayEndStop(lowField: false, atMinimum: false, shownValue: 10000);
            Settle();
            Assert.Equal("TX Filter Low 200. TX Filter High 10000, maximum", _heard.Last());
            _fields.Write(lowField: false, 9950);
            Drain();
            Settle();
            Assert.Equal("TX Filter High 9950", _heard.Last());
        }

        /// <summary>Codex's control: with the low edge heard first, the end stop says only its own edge.</summary>
        [Fact]
        public void Control_an_end_stop_with_nothing_else_owed_says_only_its_edge()
        {
            HearLow150ThenLeaveLow200Waiting(high: 10000);
            Settle();
            Assert.Equal("TX Filter Low 200", _heard.Last());
            _fields.SayEndStop(lowField: false, atMinimum: false, shownValue: 10000);
            Settle();
            Assert.Equal("TX Filter High 10000, maximum", _heard.Last());
        }

        /// <summary>
        /// B3/F3, another door. The field's Low 200 is waiting when another
        /// door sets Low 300. The sentence is composed when it is handed over,
        /// so it says the 300 the radio holds, and never the 200.
        /// </summary>
        [Fact]
        public void A_waiting_result_says_the_value_another_door_set_since()
        {
            HearLow150ThenLeaveLow200Waiting(high: 2800);
            WriteTx(300);                              // another door
            Settle();
            Assert.Equal("TX Filter Low 300", _heard.Last());
            Assert.DoesNotContain("TX Filter Low 200", _heard);
        }

        /// <summary>Codex's control: with no other write, the waiting result is said as it was.</summary>
        [Fact]
        public void Control_with_no_other_door_the_waiting_result_is_said()
        {
            HearLow150ThenLeaveLow200Waiting(high: 2800);
            Settle();
            Assert.Equal("TX Filter Low 200", _heard.Last());
        }

        /// <summary>
        /// F4, refused. The reader took nothing of Low 150. Five seconds later
        /// is no evidence it was heard, so High 2850 still covers it.
        /// </summary>
        [Fact]
        public void A_sentence_the_reader_refused_is_still_owed()
        {
            Tx(100, 2800);
            _refuse = true;
            _fields.Write(lowField: true, 150);
            Drain();
            _refuse = false;
            _clock.Advance(5000);
            _fields.Write(lowField: false, 2850);
            Drain();
            Settle();
            Assert.Equal("TX Filter Low 150. TX Filter High 2850", _heard.Last());
        }

        /// <summary>
        /// F4, cancelled. Other speech cut Low 150 before a word of it was
        /// said, and the reader reported Cancelled. Time passing does not make
        /// it heard: High 2850 covers it.
        /// </summary>
        [Fact]
        public void A_sentence_the_reader_cancelled_is_still_owed()
        {
            Tx(100, 2800);
            _fields.Write(lowField: true, 150);
            Drain();
            _clock.Advance(50);
            _arbiter.Emit("Other focus speech", interrupt: true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "test", subject: "other");
            Finish();                                  // Low 150 Cancelled; the other speech Completed
            _clock.Advance(5000);
            _fields.Write(lowField: false, 2850);
            Drain();
            Settle();
            Assert.Equal("TX Filter Low 150. TX Filter High 2850", _heard.Last());
        }

        /// <summary>
        /// A refusal waiting to be said is withdrawn by a later success on the
        /// surface: "not changed" would no longer be true when spoken.
        /// </summary>
        [Fact]
        public void A_later_success_withdraws_a_refusal_not_yet_said()
        {
            Tx(100, 2800);
            _fields.Write(lowField: true, 150);
            Drain();                                   // handed over, not finished
            _clock.Advance(50);
            _rig.q.MainLoop = false;
            _fields.Write(lowField: false, 3000);      // refused; waits its turn
            _rig.q.MainLoop = true;
            _fields.Write(lowField: false, 2900);
            Drain();
            Settle();
            Assert.Equal("TX Filter Low 150. TX Filter High 2900", _heard.Last());
            Assert.DoesNotContain(_heard, h => h.Contains("not changed"));
        }

        /// <summary>
        /// Completed credits only what the text STATED. Another door moves the
        /// low edge while a high-only sentence is being spoken; that sentence
        /// finishing is no evidence the new low was heard, so the next
        /// sentence states it.
        /// </summary>
        [Fact]
        public void A_completed_sentence_credits_only_the_edges_it_stated()
        {
            Tx(100, 2800);
            _fields.Write(lowField: true, 150);
            Drain();
            Finish();                                  // "TX Filter Low 150", heard
            _clock.Advance(5000);
            _fields.Write(lowField: false, 2850);
            Drain();                                   // "TX Filter High 2850", being spoken
            WriteTx(300);                              // another door, meanwhile
            Finish();                                  // the high-only sentence finishes
            _clock.Advance(5000);
            _fields.Write(lowField: false, 2900);
            Drain();
            Settle();
            Assert.Equal("TX Filter Low 300. TX Filter High 2900", _heard.Last());
        }

        /// <summary>
        /// A sentence cut before it was heard is said again even when the words
        /// are the same: a composed sentence is never skipped as a repeat.
        /// </summary>
        [Fact]
        public void A_cut_sentence_is_said_again_even_in_the_same_words()
        {
            Tx(100, 2800);
            _fields.Write(lowField: true, 150);
            Drain();
            _arbiter.Emit("Other focus speech", interrupt: true, SpeechIntent.Interrupt, VerbosityLevel.Terse, "test", subject: "other");
            Pump();                                    // Low 150 Cancelled
            _clock.Advance(50);
            _fields.Write(lowField: true, 150);        // the same value again
            Drain();
            Settle();
            Assert.Equal(2, _heard.Count(h => h == "TX Filter Low 150"));
        }

        /// <summary>
        /// A Completed outcome pays only the obligations its text carried. A
        /// second press held back to the same value, raised while the first
        /// sentence was being spoken, still gets its own "at limit".
        /// </summary>
        [Fact]
        public void A_completed_sentence_does_not_pay_a_newer_request()
        {
            Tx(2700, 2800);
            _fields.Write(lowField: true, 2800);
            Drain();                                   // "TX Filter Low 2750, at limit", being spoken
            _clock.Advance(50);
            _fields.Write(lowField: true, 2900);
            Drain();                                   // held back to 2750 again; waits
            Finish();                                  // the first finishes
            Settle();
            Assert.Equal(2, _heard.Count(h => h == "TX Filter Low 2750, at limit"));
        }

        // ------------------------------------------------------------------
        // J35 (b), ruled by Noel 2026-11-07: a sentence counts as heard once
        // the next sentence on these fields is handed over
        // ------------------------------------------------------------------

        /// <summary>
        /// A reader that never reports (JAWS). Low 150 may have been cut, so
        /// the next sentence says it again, once; after that it counts as
        /// heard and High 2900 is said alone. Before, every later sentence
        /// stated both edges until the surface closed.
        /// </summary>
        [Fact]
        public void With_a_reader_that_never_reports_a_sentence_counts_once_the_next_is_handed_over()
        {
            _untracked = true;
            Tx(100, 2800);
            _fields.Write(lowField: true, 150);
            Drain();
            _clock.Advance(5000);
            _fields.Write(lowField: false, 2850);
            Drain();
            Assert.Equal("TX Filter Low 150. TX Filter High 2850", _heard.Last());
            _clock.Advance(5000);
            _fields.Write(lowField: false, 2900);
            Drain();
            _clock.Advance(5000);
            Assert.Equal("TX Filter High 2900", _heard.Last());
        }

        /// <summary>
        /// The outcome callback can arrive late and out of order (#916). Low
        /// 150 was counted heard when the next sentence was handed over, and
        /// that sentence said Low 300; Low 150's own report arriving after it
        /// must not put 150 back as the last low heard.
        /// </summary>
        [Fact]
        public void A_late_completion_for_a_sentence_already_counted_changes_nothing()
        {
            Tx(100, 2800);
            _fields.Write(lowField: true, 150);
            Drain();
            var (t1, s1) = _outstanding.Last();
            _outstanding.Clear();                      // its report is held back
            _clock.Advance(5000);
            WriteTx(300);                              // another door moves the low edge
            _fields.Write(lowField: false, 2850);
            Drain();
            Assert.Equal("TX Filter Low 300. TX Filter High 2850", _heard.Last());
            Finish();                                  // that sentence is heard
            _arbiter.OnOutcome(t1, s1, SpeechOutcome.Completed(Words(s1), Words(s1), 100));
            _clock.Advance(5000);
            _fields.Write(lowField: false, 2900);
            Drain();
            Settle();
            Assert.Equal("TX Filter High 2900", _heard.Last());
        }

        // ------------------------------------------------------------------
        // Round thirteen: the words are as fresh as the numbers (Codex's
        // eighth re-verify, the two wording follow-ups)
        // ------------------------------------------------------------------

        private void HearLow150ThenWait(int high)
        {
            Tx(100, high);
            _fields.Write(lowField: true, 150);
            Drain();
            Finish();                                  // "TX Filter Low 150", heard
            _clock.Advance(50);                        // the next sentence waits its turn
        }

        /// <summary>
        /// The boundary word is decided when the sentence is composed: High's
        /// maximum end stop is waiting when another door sets High 9000, so the
        /// edge is no longer at its maximum and the word is not said.
        /// </summary>
        [Fact]
        public void An_end_stop_says_its_boundary_only_if_the_edge_is_still_there()
        {
            HearLow150ThenWait(high: 10000);
            _fields.SayEndStop(lowField: false, atMinimum: false, shownValue: 10000);
            _rig.SetTXFilter(null, 9000, _ => { });   // another door
            Drain();
            Settle();
            Assert.Equal("TX Filter High 9000", _heard.Last());
        }

        /// <summary>Codex's control: with no other door, the end stop keeps its word.</summary>
        [Fact]
        public void Control_with_no_other_door_the_end_stop_keeps_its_boundary_word()
        {
            HearLow150ThenWait(high: 10000);
            _fields.SayEndStop(lowField: false, atMinimum: false, shownValue: 10000);
            Settle();
            Assert.Equal("TX Filter High 10000, maximum", _heard.Last());
        }

        private void HearHigh2850ThenPressLowMinimum()
        {
            Tx(0, 2800);
            _fields.Write(lowField: false, 2850);
            Drain();
            Finish();                                  // "TX Filter High 2850", heard
            _clock.Advance(50);
            _fields.SayEndStop(lowField: true, atMinimum: true, shownValue: 0);   // waits its turn
        }

        /// <summary>The same for the low edge's minimum: another door sets Low 300 first.</summary>
        [Fact]
        public void A_low_end_stop_says_minimum_only_if_the_edge_is_still_there()
        {
            HearHigh2850ThenPressLowMinimum();
            WriteTx(300);                              // another door
            Settle();
            Assert.Equal("TX Filter Low 300", _heard.Last());
        }

        [Fact]
        public void Control_with_no_other_door_the_low_end_stop_keeps_its_boundary_word()
        {
            HearHigh2850ThenPressLowMinimum();
            Settle();
            Assert.Equal("TX Filter Low 0, minimum", _heard.Last());
        }

        /// <summary>
        /// A refusal waiting to be said is dropped once another door has
        /// changed the filter: "not taking changes right now" is no longer true.
        /// </summary>
        [Fact]
        public void A_waiting_refusal_is_dropped_once_another_door_changed_the_filter()
        {
            HearLow150ThenWait(high: 2800);
            _rig.q.MainLoop = false;
            _fields.Write(lowField: false, 3000);      // refused; waits its turn
            _rig.q.MainLoop = true;
            _rig.SetTXFilter(null, 2900, _ => { });   // another door succeeds
            Drain();
            Settle();
            Assert.Equal("TX Filter High 2900", _heard.Last());
            Assert.DoesNotContain(_heard, h => h.Contains("not changed"));
        }

        /// <summary>Codex's control: with no other door, the waiting refusal is said.</summary>
        [Fact]
        public void Control_with_no_other_door_the_waiting_refusal_is_said()
        {
            HearLow150ThenWait(high: 2800);
            _rig.q.MainLoop = false;
            _fields.Write(lowField: false, 3000);
            Settle();
            Assert.Equal("TX filter not changed. The radio is not taking changes right now.", _heard.Last());
        }

        /// <summary>Codex's control: Low 150 Completed, and High 2850 says only the high.</summary>
        [Fact]
        public void Control_a_completed_sentence_is_not_repeated()
        {
            Tx(100, 2800);
            _fields.Write(lowField: true, 150);
            Drain();
            Finish();
            _clock.Advance(5000);
            _fields.Write(lowField: false, 2850);
            Drain();
            Settle();
            Assert.Equal("TX Filter High 2850", _heard.Last());
        }

        // ------------------------------------------------------------------
        // The menu's delayed sentence
        // ------------------------------------------------------------------

        private FilterApplied WriteTx(int low)
        {
            FilterApplied? got = null;
            _rig.SetTXFilter(low, null, a => got = a);
            Drain();
            return got!.Value;
        }

        [Fact]
        public void A_menu_sentence_is_checked_against_the_rig_and_connection_it_came_from()
        {
            Tx(100, 2800);
            var applied = WriteTx(300);
            var stillTrue = FilterWriteFreshness.StillTrue(_rig, applied, transmit: true);
            Assert.NotNull(stillTrue);
            Assert.True(stillTrue!());
            // A reconnect with the same edges: no longer the write this sentence is about.
            typeof(Radio).GetField("_commandCommunication", Private)!.SetValue(_vendor.Radio, new FakeCommandTransport());
            Assert.False(stillTrue());
        }

        [Fact]
        public void A_menu_refusal_is_retired_by_a_later_successful_write()
        {
            Tx(100, 2800);
            _rig.q.MainLoop = false;
            FilterApplied? refused = null;
            _rig.SetTXFilter(300, null, a => refused = a);
            Assert.True(refused!.Value.NotWritten);
            var stillTrue = FilterWriteFreshness.StillTrue(_rig, refused.Value, transmit: true);
            Assert.NotNull(stillTrue);
            Assert.True(stillTrue!());
            _rig.q.MainLoop = true;
            WriteTx(400);   // a later write to the same filter succeeds within the wait
            Assert.False(stillTrue());
        }

        private static int CountOf(string text, string what)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
            return n;
        }
    }
}
