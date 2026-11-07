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
        private readonly List<string> _heard = new List<string>();
        private readonly SpeechArbiter _arbiter;
        private bool _open = true;
        private readonly TxFilterFields _fields;

        private const string SurfaceKey = "value-field:TX filter (test surface)";

        public TxFilterFieldsTests()
        {
            _rig.theRadio = _vendor.Radio;
            _rig.q.MainLoop = true;   // the queue holds writes for Drain, as the main loop would run them
            _arbiter = new SpeechArbiter(_clock, () => VerbosityLevel.Chatty,
                (text, interrupt, intent, level, origin, salvaged) => { _heard.Add(text); return true; },
                () => { }, (_, _, _, _) => { });
            _fields = new TxFilterFields(SurfaceKey, "TX Filter Low", "TX Filter High", () => _rig, () => _open,
                latest: (text, key) => _arbiter.Latest(key, text, VerbosityLevel.Terse, SpeechCoalesceKind.Value, "test"));
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

        private void Settle() => _clock.Advance(5000);

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
            Assert.Equal("TX Filter High 2850", _heard.Last());
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
            Assert.Equal("TX Filter High 2850", _heard.Last());
            Assert.DoesNotContain("TX filter 2750 to 2800, at limit", _heard);
            Assert.Equal((2750, 2850), Actual);
        }

        /// <summary>
        /// The same, with the low edge's result WAITING (a result was said just
        /// before, so the next one on the key waits its turn) when the high
        /// edge truly changes. Its "at limit" was true against the old high
        /// edge and would be false by the time it was heard; one key for both
        /// fields means the newer result replaces it, unheard.
        /// </summary>
        [Fact]
        public void A_waiting_result_is_replaced_by_a_newer_result_for_the_other_edge()
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
            Assert.DoesNotContain("TX Filter Low 2750, at limit", _heard);
            Assert.Equal("TX Filter High 2850", _heard.Last());
            Assert.Equal((2750, 2850), Actual);
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
            string workshop = System.IO.File.ReadAllText(System.IO.Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "AudioWorkshopDialog.TxAudio.cs"));
            Assert.Contains("_workshopTxFilter", workshop, StringComparison.Ordinal);
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
