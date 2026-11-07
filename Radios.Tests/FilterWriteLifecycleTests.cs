#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.Speech;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Round eight of the 2026-11-06 junk box (#747), Codex's fourth re-verify:
    /// a filter write's spoken result must be the LAST thing said and must
    /// still be true when it is said.
    /// </summary>
    /// <remarks>
    /// <para>B3: a value field says its own number on its own Latest key as it
    /// moves. The transmit filter correction was a separate interrupt, which
    /// did not retire the field's pending number, so the last thing said could
    /// be false: "TX Filter Low 2700", the correction, then "TX Filter Low
    /// 2800" while the edge was 2750. The correction now goes on the field's
    /// own key.</para>
    /// <para>F2: an older completion overwrote the layer's bank and rolled a
    /// newer held-key request back: three +50 presses from 100 ended at 200.
    /// <see cref="FilterRequestBank"/> adopts a completion only if it answers
    /// the newest request.</para>
    /// <para>F3: a completion after the layer closed still spoke; a write that
    /// could not run (no slice, or the command loop not running) ended in
    /// silence.</para>
    /// <para>Real vendor Radio and Slice objects over the house fake
    /// transport, the real value-layer engine, and the real speech arbiter on
    /// a manual clock; no radio, no window. Sentences are DRAFT literals.</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class FilterWriteLifecycleTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        private readonly VendorRadioFixture _vendor = new VendorRadioFixture("1111-2222-6300-4444");
        private readonly Slice _slice;

        public FilterWriteLifecycleTests()
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

        private void DrainOne()
        {
            var item = _rig.q.Dequeue();
            if (item.Item is FlexBase.FunctionDel f) f();
        }

        private void Drain()
        {
            while (_rig.q.Count > 0) DrainOne();
        }

        private void Rx(string mode, int low, int high)
        {
            typeof(Slice).GetField("_demodMode", Private)!.SetValue(_slice, mode);
            typeof(Slice).GetField("_filterLow", Private)!.SetValue(_slice, low);
            typeof(Slice).GetField("_filterHigh", Private)!.SetValue(_slice, high);
        }

        private void Tx(int low, int high)
        {
            typeof(Radio).GetField("_txFilterLow", Private)!.SetValue(_vendor.Radio, low);
            typeof(Radio).GetField("_txFilterHigh", Private)!.SetValue(_vendor.Radio, high);
        }

        // ------------------------------------------------------------------
        // B3: the field's correction is the last thing said
        // ------------------------------------------------------------------

        private static (SpeechArbiter arbiter, FakeSpeechClock clock, List<string> heard) Arbiter()
        {
            var clock = new FakeSpeechClock();
            var heard = new List<string>();
            var arbiter = new SpeechArbiter(clock, () => VerbosityLevel.Chatty,
                (text, interrupt, intent, level, origin, salvaged) => { heard.Add(text); return true; },
                () => { }, (_, _, _, _) => { });
            return (arbiter, clock, heard);
        }

        // The round-eight field-correction schedules moved to TxFilterFieldsTests
        // in round nine, where the field no longer speaks and the applied result
        // is the one speaker (Codex's fifth re-verify).

        // ------------------------------------------------------------------
        // F2: a completion never rolls a newer request back
        // ------------------------------------------------------------------

        [Fact]
        public void The_bank_adopts_only_the_newest_requests_completion()
        {
            var bank = new FilterRequestBank();
            bank.Seed(100, 2800);
            long first = bank.Request(150, 2800);
            long second = bank.Request(200, 2800);
            Assert.False(bank.Complete(first, 150, 2800));
            Assert.Equal(200, bank.Low);
            Assert.True(bank.Complete(second, 190, 2800));
            Assert.Equal(190, bank.Low);
        }

        /// <summary>
        /// A layer wired as KeyCommands wires the receive low edge: Linked, a
        /// Snapshot, the newest-request bank, and the write's completion.
        /// </summary>
        private (ValueSubLayer layer, FilterRequestBank bank, List<string> moves) LinkedLowEdgeLayer(int low, int high)
        {
            var bank = new FilterRequestBank();
            bank.Seed(low, high);
            var moves = new List<string>();
            var target = new ValueTarget
            {
                Id = "rx-low",
                Linked = true,
                Axes = ValueLayerAxes.UpDown,
                Min = 0,
                Max = 12000,
                Step = 50,
                Read = () => bank.Low,
                Apply = v => { bank.Set(v, bank.High); _rig.SetFilter(v, bank.High); },
                Number = v => Lexicon.Get("audio.filter.low_edge", ("low", v)),
                ApplyThenSay = (v, say) =>
                {
                    int h = bank.High;
                    long ticket = bank.Request(v, h);
                    _rig.SetFilter(v, h, applied =>
                    {
                        bank.Complete(ticket, applied.Low, applied.High);
                        say(FilterReadouts.Said(applied, (l, _) => Lexicon.Get("audio.filter.low_edge", ("low", l)),
                            FilterReadouts.RangeAtLimit, VerbosityLevel.Chatty));
                    });
                },
            };
            var def = new ValueSubLayerDefinition
            {
                Id = "filter-test",
                Targets = new List<ValueTarget> { target },
                Snapshot = () => () => { },
                DescribeClosed = () => "closed",
            };
            var layer = ValueSubLayer.EnterForTest(def, (text, _) => moves.Add(text), (_, _) => { }, _ => { }, () => VerbosityLevel.Chatty);
            return (layer, bank, moves);
        }

        /// <summary>
        /// Codex's schedule: two presses queued, the oldest write runs, a third
        /// press before the second write runs. Every press counts: 250.
        /// </summary>
        [Fact]
        public void Three_held_presses_with_two_writes_pending_all_count()
        {
            Rx("USB", 100, 2800);
            var (layer, bank, moves) = LinkedLowEdgeLayer(100, 2800);
            layer.HandleKey(Keys.Up);
            layer.HandleKey(Keys.Up);
            Assert.Equal(200, bank.Low);
            DrainOne();                     // the oldest write, 150, completes
            Assert.Equal(200, bank.Low);    // and does not pull the bank back
            layer.HandleKey(Keys.Up);       // steps from 200
            Drain();
            Assert.Equal(250, _slice.FilterLow);
            Assert.Equal(250, bank.Low);
            Assert.Equal("Low edge 250", moves.Last());
        }

        [Fact]
        public void Both_sides_of_the_filter_layer_use_the_newest_request_bank()
        {
            string keys = Src("JJFlexWpf", "KeyCommands.cs");
            Assert.Contains("public Radios.FilterRequestBank Bank = new Radios.FilterRequestBank();", keys, StringComparison.Ordinal);
            Assert.Contains("long ticket = rx.Bank.Request(l, h);", keys, StringComparison.Ordinal);
            Assert.Contains("rx.Bank.Complete(ticket, applied.Low, applied.High);", keys, StringComparison.Ordinal);
            Assert.Contains("long ticket = tx.Bank.Request(l, h);", keys, StringComparison.Ordinal);
            Assert.Contains("tx.Bank.Complete(ticket, applied.Low, applied.High);", keys, StringComparison.Ordinal);
            Assert.DoesNotContain("private sealed class FilterBank", keys, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // F3: lifecycle
        // ------------------------------------------------------------------

        [Fact]
        public void A_completion_after_the_layer_closed_says_nothing()
        {
            Rx("USB", 100, 2800);
            var (layer, _, moves) = LinkedLowEdgeLayer(100, 2800);
            layer.HandleKey(Keys.Up);
            layer.Drop();
            Drain();
            Assert.Empty(moves);
            Assert.Equal(150, _slice.FilterLow);   // the write itself still ran
        }

        [Fact]
        public void With_the_command_loop_not_running_a_write_says_nothing_was_changed()
        {
            Rx("USB", 100, 2800);
            Tx(100, 2800);
            _rig.q.MainLoop = false;
            var said = new List<string>();
            _rig.SetFilter(100, 2900, a => said.Add(FilterReadouts.Bracket(a, "", FilterEdge.None, VerbosityLevel.Chatty)));
            _rig.SetTXFilter(200, null, a => said.Add(FilterReadouts.Tx(a, (l, h) => "asked", VerbosityLevel.Chatty)));
            _rig.ApplyFilterPreset(new FilterPreset("Example", 100, 2850), "USB", said.Add);
            Assert.Equal(new[]
            {
                "Filter not changed. The radio is not taking changes right now.",
                "TX filter not changed. The radio is not taking changes right now.",
                "Example not applied. The radio is not taking changes right now.",
            }, said);
            Assert.Equal((100, 2800), (_slice.FilterLow, _slice.FilterHigh));
        }

        [Fact]
        public void A_slice_gone_by_the_write_says_nothing_was_changed()
        {
            Rx("USB", 100, 2800);
            var said = new List<string>();
            _rig.SetFilter(100, 2900, a => said.Add(FilterReadouts.Bracket(a, "", FilterEdge.None, VerbosityLevel.Chatty)));
            _vendor.Radio.SliceList.Clear();
            Drain();
            Assert.Equal(new[] { "Filter not changed. The radio is not taking changes right now." }, said);
        }

        [Fact]
        public void Home_and_the_workshop_answer_only_their_own_rig_and_newest_request()
        {
            // Round nine: the request identity (field, connection, epoch, open)
            // lives in Radios.TxFilterFields and is tested there behaviourally.
            foreach (string file in new[] { Src("JJFlexWpf", "Controls", "ScreenFieldsPanel.xaml.cs"), Src("JJFlexWpf", "Dialogs", "AudioWorkshopDialog.TxAudio.cs") })
                Assert.Contains("new TxFilterFields(", file, StringComparison.Ordinal);
            Assert.Contains("ReferenceEquals(radio?.CurrentCommandConnection, connection)", Src("Radios", "TxFilterFields.cs"), StringComparison.Ordinal);
        }

        [Fact]
        public void A_menu_sentence_that_is_no_longer_true_after_its_wait_is_not_said()
        {
            string menu = Src("JJFlexWpf", "NativeMenuBar.cs");
            Assert.Contains("if (stillTrue != null && !stillTrue())", menu, StringComparison.Ordinal);
            Assert.Contains("stillTrue: FilterStillIs(applied)", menu, StringComparison.Ordinal);
            Assert.Contains("stillTrue: TxFilterStillIs(applied)", menu, StringComparison.Ordinal);
            Assert.Contains("(said, applied) => SpeakAfterMenuClose(said, stillTrue: FilterStillIs(applied))", menu, StringComparison.Ordinal);
        }

        private static int CountOf(string text, string what)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
            return n;
        }
    }
}
