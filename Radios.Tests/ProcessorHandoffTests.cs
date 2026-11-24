#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Sprint 49 Track CL (#1278): the handoff between our transmit compressor
/// and the radio's speech processor. The policy against a fake processor; the
/// store in a scratch folder; and the Flex side on the vendor-radio fixture
/// (no radio, no network: a recording transport). Nothing here keys anything.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ProcessorHandoffTests : IDisposable
{
    private const string Serial = "1234-5678-9012-3456";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jjf-track-cl-handoff-" + Guid.NewGuid().ToString("N"));
    private readonly RadioConfigStaticsScope _scope = new(nameof(ProcessorHandoffTests));

    public ProcessorHandoffTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _scope.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A radio's speech processor that does what it is told, at once, and remembers every write.</summary>
    private sealed class FakeProcessor : IRadioSpeechProcessor
    {
        public string Serial { get; set; } = ProcessorHandoffTests.Serial;
        public bool Connected { get; set; } = true;
        public bool? On { get; set; } = true;
        public bool? ProcessorOn => On;
        public int ProcessorLevel { get; set; } = 1;
        public bool Transmitting { get; set; }
        public bool AnotherClientMayBeConnected { get; set; }
        public bool RadioIsOperators { get; set; } = true;
        public bool RefuseWrites { get; set; }
        /// <summary>When set, a write waits in a queue until <see cref="Drain"/>, as it waits in a radio's command queue.</summary>
        public bool Queued { get; set; }
        public List<bool> Writes { get; } = new();
        private readonly List<(bool On, Func<bool>? StillWanted)> _queue = new();
        public int Waiting => _queue.Count;
        public bool WriteProcessorOn(bool on, Func<bool> stillWanted)
        {
            if (RefuseWrites) return false;
            if (Queued) { _queue.Add((on, stillWanted)); return true; }
            Make(on, stillWanted);
            return true;
        }
        public bool WriteProcessorOnNow(bool on) => Make(on, null);
        /// <summary>The queue runs: each write is made only if it is still right at that moment (the contract every implementation keeps).</summary>
        public void Drain()
        {
            var waiting = _queue.ToArray();
            _queue.Clear();
            foreach (var (on, stillWanted) in waiting) Make(on, stillWanted);
        }
        private bool Make(bool on, Func<bool>? stillWanted)
        {
            if (!Connected || Transmitting || (stillWanted != null && !stillWanted())) return false;
            Writes.Add(on);
            On = on;
            return true;
        }
    }

    private sealed class Bench
    {
        public readonly FakeProcessor Radio = new();
        public readonly List<ProcessorHandoffNotice> Said = new();
        public readonly List<string> Traced = new();
        public readonly ProcessorHandoffStore Store;
        public ProcessorHandoff Handoff;
        public bool Setting = true, Compressor, PcMic = true, Voice = true;
        public long Faults;

        public Bench(string root)
        {
            Store = new ProcessorHandoffStore(root);
            Handoff = Make();
        }

        public ProcessorHandoff Make() => new(Radio, Store, Said.Add, Traced.Add);

        /// <summary>A new radio object on the same radio and the same settings folder, as after a disconnect and a connect (or the app restarted).</summary>
        public void Reconnect()
        {
            Handoff = Make();
            Handoff.Connected();
        }

        public void Tick(int times = 1)
        {
            for (int i = 0; i < times; i++) Handoff.Tick(new ProcessorHandoffInputs(Setting, Compressor, PcMic, Voice, Faults));
        }

        /// <summary>Tick until a write just asked for has been seen in the radio's reading and said, with ticks to spare.</summary>
        public void Settle() => Tick(ProcessorHandoff.SettleTicks + 1);
    }

    private Bench NewBench()
    {
        var b = new Bench(_root);
        b.Handoff.Connected();
        b.Tick();
        return b;
    }

    // ── the edge that acts ──

    [Fact]
    public void The_compressor_coming_on_turns_the_processor_off_records_it_and_says_so_once()
    {
        var b = NewBench();
        Assert.Empty(b.Radio.Writes); // nothing yet: the compressor is off

        b.Compressor = true;
        b.Tick();
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.Empty(b.Said); // asked and, on this radio, written at once; said when the radio's reading shows it
        b.Tick();
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff }, b.Said);
        var record = b.Store.Find(Serial)!;
        Assert.Equal((true, 1, false), (record.PreviousOn, record.PreviousLevel, record.AppSetOn));

        b.Tick(20); // and never again, and never said again
        Assert.Single(b.Radio.Writes);
        Assert.Single(b.Said);
    }

    [Fact]
    public void With_another_client_possibly_connected_it_says_the_processor_is_shared_and_does_not_refuse()
    {
        var b = NewBench();
        b.Radio.AnotherClientMayBeConnected = true;
        b.Compressor = true;
        b.Tick(2);
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOffShared }, b.Said);
        b.Settle();
        b.Compressor = false;
        b.Tick(2);
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOffShared, ProcessorHandoffNotice.PutBackShared }, b.Said);
    }

    [Fact]
    public void A_processor_that_was_already_off_is_left_off_and_nothing_is_put_back_later()
    {
        var b = NewBench();
        b.Radio.On = false;
        b.Compressor = true;
        b.Tick(3);
        Assert.Empty(b.Radio.Writes);
        Assert.Empty(b.Said);
        Assert.Null(b.Store.Find(Serial));
        // The operator turns the processor on later, with our compressor still on: theirs, untouched.
        b.Radio.On = true;
        b.Tick(5);
        Assert.Empty(b.Radio.Writes);
        // And our compressor going off puts nothing "back".
        b.Compressor = false;
        b.Tick(3);
        Assert.Empty(b.Radio.Writes);
        Assert.True(b.Radio.On);
    }

    // ── putting it back ──

    [Fact]
    public void The_compressor_going_off_puts_the_processor_back_only_while_it_still_holds_our_value()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        b.Compressor = false;
        b.Tick();
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.NotNull(b.Store.Find(Serial)); // the record stays until the put-back is SEEN
        b.Tick();
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff, ProcessorHandoffNotice.PutBack }, b.Said);
        Assert.Null(b.Store.Find(Serial));
        Assert.False(b.Handoff.HoldsRecord);
    }

    [Fact]
    public void The_operators_turn_on_in_between_is_respected_and_never_fought()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        // The operator turns the radio's processor back on, by any door.
        b.Radio.On = true;
        b.Tick(10);
        Assert.Equal(new[] { false }, b.Radio.Writes);            // never turned off again in this session
        Assert.Null(b.Store.Find(Serial));                         // our record is dropped
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff }, b.Said);
        // Our compressor going off writes nothing: the processor is theirs.
        b.Compressor = false;
        b.Tick(3);
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.True(b.Radio.On);
        // The next time the compressor comes on is a new session, and acts again.
        b.Compressor = true;
        b.Tick();
        Assert.Equal(new[] { false, false }, b.Radio.Writes);
    }

    [Fact]
    public void A_turn_on_and_our_compressor_going_off_between_two_ticks_writes_nothing_and_says_nothing_more()
    {
        // Found by a mutation run: the put-back's own "does the radio still
        // hold our value" check had no test, because every other case is
        // settled earlier (in the session, or by the connect rule).
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        // Between two ticks the operator turns the processor on AND our compressor goes off.
        b.Radio.On = true;
        b.Compressor = false;
        b.Tick(3);
        Assert.Equal(new[] { false }, b.Radio.Writes);                    // nothing is put "back": it is already theirs
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff }, b.Said); // and no "back on" sentence, which would be false
        Assert.Null(b.Store.Find(Serial));
    }

    [Fact]
    public void The_radios_own_microphone_ends_the_session_and_the_processor_comes_back()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        b.PcMic = false; // the microphone becomes the radio's own
        b.Tick(2);
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.Equal(ProcessorHandoffNotice.PutBack, b.Said[^1]);
        // Back to the computer's microphone with the compressor still on: the edge acts again.
        b.Settle();
        b.PcMic = true;
        b.Tick();
        Assert.Equal(new[] { false, true, false }, b.Radio.Writes);
    }

    [Fact]
    public void Clearing_the_setting_puts_back_what_the_app_changed_and_setting_it_acts()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        b.Setting = false; // the operator wants both
        b.Tick();
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        b.Settle();
        b.Tick(5);
        Assert.Equal(2, b.Radio.Writes.Count); // and with the setting off it is never touched
        b.Setting = true;
        b.Tick();
        Assert.Equal(new[] { false, true, false }, b.Radio.Writes);
    }

    // ── never touched ──

    [Fact]
    public void The_limiter_alone_the_setting_off_and_the_radios_own_microphone_do_nothing()
    {
        // The limiter is not an input at all: the policy hears only "compressor on".
        Assert.DoesNotContain(typeof(ProcessorHandoffInputs).GetProperties(), p => p.Name.Contains("Limiter"));
        var b = NewBench();
        b.Tick(5); // compressor off (a limiter alone leaves it off)
        Assert.Empty(b.Radio.Writes);

        b.Setting = false; b.Compressor = true;
        b.Tick(5);
        Assert.Empty(b.Radio.Writes);

        b.Setting = true; b.PcMic = false;
        b.Tick(5);
        Assert.Empty(b.Radio.Writes);
        Assert.Empty(b.Said);

        // The positive control: with all three true it acts.
        b.PcMic = true;
        b.Tick();
        Assert.Equal(new[] { false }, b.Radio.Writes);
    }

    [Fact]
    public void In_CW_or_digital_the_processor_is_not_turned_off_until_a_voice_mode_returns()
    {
        var b = NewBench();
        b.Voice = false;
        b.Compressor = true;
        b.Tick(5);
        Assert.Empty(b.Radio.Writes);
        b.Voice = true;
        b.Tick();
        Assert.Equal(new[] { false }, b.Radio.Writes);
    }

    [Fact]
    public void On_a_radio_not_marked_as_the_operators_own_the_processor_is_left_alone_and_that_is_said_once()
    {
        var b = NewBench();
        b.Radio.RadioIsOperators = false;
        b.Compressor = true;
        b.Tick(8);
        Assert.Empty(b.Radio.Writes);
        Assert.Equal(new[] { ProcessorHandoffNotice.LeftAloneNotYours }, b.Said);
        Assert.Null(b.Store.Find(Serial));
        // A processor that is already off there calls for no sentence at all.
        var quiet = new Bench(Path.Combine(_root, "quiet"));
        quiet.Radio.RadioIsOperators = false; quiet.Radio.On = false;
        quiet.Handoff.Connected(); quiet.Compressor = true;
        quiet.Tick(5);
        Assert.Empty(quiet.Said);
    }

    // ── transmitting defers every write ──

    [Fact]
    public void A_transmitting_radio_defers_the_turn_off_and_the_put_back_to_the_unkey()
    {
        var b = NewBench();
        b.Radio.Transmitting = true;
        b.Compressor = true;
        b.Tick(6);
        Assert.Empty(b.Radio.Writes);
        Assert.Empty(b.Said);
        b.Radio.Transmitting = false;
        b.Tick();
        Assert.Equal(new[] { false }, b.Radio.Writes);

        b.Settle();
        b.Radio.Transmitting = true;
        b.Compressor = false;
        b.Tick(6);
        Assert.Equal(new[] { false }, b.Radio.Writes); // still waiting
        Assert.NotNull(b.Store.Find(Serial));
        b.Radio.Transmitting = false;
        b.Tick();
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
    }

    [Fact]
    public void A_write_the_radio_refuses_is_tried_again_and_leaves_no_record()
    {
        var b = NewBench();
        b.Radio.RefuseWrites = true;
        b.Compressor = true;
        b.Tick(3);
        Assert.Empty(b.Said);
        Assert.Null(b.Store.Find(Serial));
        b.Radio.RefuseWrites = false;
        b.Tick();
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.NotNull(b.Store.Find(Serial));
    }

    [Fact]
    public void The_radios_reading_is_not_judged_until_a_write_has_had_time_to_land()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        // The radio has not reported the change yet: it still reads on.
        b.Radio.On = true;
        b.Tick(ProcessorHandoff.SettleTicks);
        Assert.NotNull(b.Store.Find(Serial)); // not mistaken for the operator turning it on
        b.Radio.On = false;                    // the report arrives
        b.Tick(5);
        Assert.NotNull(b.Store.Find(Serial));
        Assert.Equal(new[] { false }, b.Radio.Writes);
    }

    // ── a fault in the chain mid-over ──

    [Fact]
    public void A_chain_fault_puts_the_processor_back_at_the_unkey_says_so_once_and_does_not_turn_it_off_again()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        b.Radio.Transmitting = true;
        b.Faults = 1; // the conditioner threw mid-over and is being skipped
        b.Tick(4);
        Assert.Equal(new[] { false }, b.Radio.Writes); // not while keyed
        b.Radio.Transmitting = false;
        b.Tick(2);
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.Equal(ProcessorHandoffNotice.PutBackAfterFault, b.Said[^1]);
        b.Tick(20);
        Assert.Equal(2, b.Radio.Writes.Count);
        Assert.Equal(2, b.Said.Count);
        // The compressor switched off and on again is a new session.
        b.Compressor = false; b.Tick();
        b.Compressor = true; b.Tick();
        Assert.Equal(new[] { false, true, false }, b.Radio.Writes);
    }

    [Fact]
    public void The_fault_count_at_the_first_tick_is_a_baseline_and_a_new_streams_count_starts_again()
    {
        var b = new Bench(_root);
        b.Faults = 7; // faults from before this connection's first tick are not a fault now
        b.Handoff.Connected();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        Assert.Equal(new[] { false }, b.Radio.Writes);
        b.Faults = 0; // a new stream: its counter starts at zero
        b.Tick(2);
        Assert.Equal(new[] { false }, b.Radio.Writes);
        b.Faults = 1;
        b.Tick();
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
    }

    // ── across a disconnect ──

    [Fact]
    public void An_orderly_disconnect_puts_the_processor_back_and_the_next_connect_with_the_compressor_on_acts_again()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        b.Handoff.Leaving();
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.NotNull(b.Store.Find(Serial)); // kept: the write cannot be confirmed from here

        b.Traced.Clear();
        b.Reconnect();
        b.Tick(2);
        // The processor is on (the put-back landed): the record is dropped, and
        // since our compressor is on this is a session's start like any other.
        Assert.Equal(new[] { false, true, false }, b.Radio.Writes);
        Assert.Equal(ProcessorHandoffNotice.TurnedOff, b.Said[^1]);
        // And the trace does not say the processor is "left as it is" in the same
        // breath as turning it off (repair 1): it says what happens next.
        Assert.DoesNotContain(b.Traced, line => line.Contains("left as it is"));
        Assert.Contains(b.Traced, line => line.Contains("at connect") && line.Contains("may turn it off again"));
    }

    [Fact]
    public void A_dropped_link_keeps_the_record_and_the_next_connect_puts_the_processor_back_if_the_compressor_is_off()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        // The link drops: nothing can be written.
        b.Radio.Connected = false;
        b.Handoff.Leaving();
        b.Tick(3);
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.NotNull(b.Store.Find(Serial));

        // Later, connected again with our compressor off: put back, and said.
        b.Radio.Connected = true;
        b.Compressor = false;
        b.Reconnect();
        b.Tick(2);
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.Equal(ProcessorHandoffNotice.PutBackAtConnect, b.Said[^1]);
        Assert.Null(b.Store.Find(Serial));
    }

    [Fact]
    public void The_next_connect_with_the_compressor_on_keeps_the_processor_off_keeps_the_record_and_says_so()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        b.Radio.Connected = false;
        b.Radio.Connected = true;
        b.Reconnect();
        b.Tick(3);
        Assert.Equal(new[] { false }, b.Radio.Writes); // nothing written: it is already off
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff, ProcessorHandoffNotice.StillOffAtConnect }, b.Said);
        Assert.NotNull(b.Store.Find(Serial));
        // And it is still ours to put back when the compressor goes off.
        b.Compressor = false;
        b.Tick(2);
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.Equal(ProcessorHandoffNotice.PutBack, b.Said[^1]);
    }

    [Fact]
    public void At_connect_a_processor_that_no_longer_holds_our_value_is_left_alone_and_the_record_dropped()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        b.Radio.Connected = false;
        // While we were away the reconnect's own profile load, or another client, turned it on.
        b.Radio.On = true;
        b.Radio.Connected = true;
        b.Compressor = false;
        b.Reconnect();
        b.Tick(3);
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.Null(b.Store.Find(Serial));
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff }, b.Said);
    }

    [Fact]
    public void A_disconnect_while_transmitting_writes_nothing_and_the_next_connect_puts_it_back()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        b.Radio.Transmitting = true;
        b.Handoff.Leaving();
        Assert.Equal(new[] { false }, b.Radio.Writes);
        b.Radio.Transmitting = false;
        b.Compressor = false;
        b.Reconnect();
        b.Tick();
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
    }

    [Fact]
    public void A_connect_that_cannot_read_the_processor_yet_waits_and_a_radio_with_no_record_is_untouched()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        b.Radio.Connected = false; b.Radio.Connected = true;
        b.Compressor = false;
        b.Radio.On = null; // not reported yet
        b.Reconnect();
        b.Tick(3);
        Assert.Equal(new[] { false }, b.Radio.Writes);
        b.Radio.On = false;
        b.Tick();
        Assert.Equal(new[] { false, true }, b.Radio.Writes);

        var other = new Bench(Path.Combine(_root, "other"));
        other.Radio.On = false;
        other.Handoff.Connected();
        other.Tick(5);
        Assert.Empty(other.Radio.Writes);
        Assert.Empty(other.Said);
    }

    // ── asked is not written (repair 1, B3): the policy against a queue ──

    [Fact]
    public void Asked_is_not_written_the_sentence_and_the_record_wait_for_the_radios_reading()
    {
        var b = NewBench();
        b.Radio.Queued = true;
        b.Compressor = true;
        b.Tick();
        Assert.Equal(1, b.Radio.Waiting);
        Assert.Empty(b.Radio.Writes);
        Assert.Empty(b.Said);                       // nothing written, so nothing said
        Assert.NotNull(b.Store.Find(Serial));       // the record is on disk before the write
        b.Tick(2);
        Assert.Empty(b.Said);
        b.Radio.Drain();
        b.Tick();
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff }, b.Said);

        // The put-back: queued is not put back. The debt stays on record and nothing is said.
        b.Compressor = false;
        b.Tick();
        Assert.Equal(1, b.Radio.Waiting);
        Assert.False(b.Radio.On);
        Assert.NotNull(b.Store.Find(Serial));
        Assert.Single(b.Said);
        b.Radio.Drain();
        b.Tick();
        Assert.Null(b.Store.Find(Serial));
        Assert.Equal(ProcessorHandoffNotice.PutBack, b.Said[^1]);
    }

    [Fact]
    public void A_radio_that_keys_between_the_asking_and_the_write_is_not_written_and_is_asked_again_at_the_unkey()
    {
        var b = NewBench();
        b.Radio.Queued = true;
        b.Compressor = true;
        b.Tick();
        Assert.Equal(1, b.Radio.Waiting);
        b.Radio.Transmitting = true;                // keyed before the queue reached the write
        b.Radio.Drain();
        Assert.Empty(b.Radio.Writes);
        b.Tick(ProcessorHandoff.SettleTicks + 4);
        Assert.Empty(b.Radio.Writes);
        Assert.Empty(b.Said);
        Assert.Equal(0, b.Radio.Waiting);           // and not asked for while it transmits
        // It was never off, so its reading "on" is not the operator turning it back on: asked again at the unkey.
        b.Radio.Transmitting = false;
        b.Tick();
        Assert.Equal(1, b.Radio.Waiting);
        b.Radio.Drain();
        b.Tick();
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff }, b.Said);
    }

    [Fact]
    public void A_write_the_handoff_no_longer_wants_is_not_made_when_the_queue_reaches_it()
    {
        var b = NewBench();
        b.Radio.Queued = true;
        b.Compressor = true;
        b.Tick();
        Assert.Equal(1, b.Radio.Waiting);
        b.Compressor = false;                       // the session ends before the queue has run
        b.Tick();
        b.Radio.Drain();
        Assert.Empty(b.Radio.Writes);               // withdrawn: the processor is never turned off late
        b.Tick(ProcessorHandoff.SettleTicks + 2);
        Assert.True(b.Radio.On);
        Assert.Null(b.Store.Find(Serial));          // nothing was changed, so nothing is owed
        Assert.Empty(b.Said);
        Assert.Empty(b.Radio.Writes);
    }

    [Fact]
    public void A_put_back_still_in_the_queue_when_the_link_ends_leaves_the_record_for_the_next_connect()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        b.Radio.Queued = true;
        b.Compressor = false;
        b.Tick();
        Assert.Equal(1, b.Radio.Waiting);
        b.Radio.Connected = false;                  // the link ends with the put-back still queued
        b.Radio.Drain();
        b.Tick(3);
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.NotNull(b.Store.Find(Serial));
        Assert.DoesNotContain(ProcessorHandoffNotice.PutBack, b.Said);

        b.Radio.Connected = true;
        b.Radio.Queued = false;
        b.Reconnect();
        b.Tick(2);
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.Equal(ProcessorHandoffNotice.PutBackAtConnect, b.Said[^1]);
        Assert.Null(b.Store.Find(Serial));
    }

    [Fact]
    public void A_put_back_that_never_shows_is_asked_for_again()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        b.Radio.Queued = true;
        b.Compressor = false;
        b.Tick();
        b.Radio.Transmitting = true;                // keyed when the queue reaches it: not written
        b.Radio.Drain();
        b.Radio.Transmitting = false;
        b.Tick(ProcessorHandoff.SettleTicks + 2);   // given its time, then asked again
        Assert.Equal(1, b.Radio.Waiting);
        Assert.NotNull(b.Store.Find(Serial));
        b.Radio.Drain();
        b.Tick();
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.Null(b.Store.Find(Serial));
    }

    [Fact]
    public void The_leave_writes_at_once_and_nothing_asked_earlier_goes_out_after_it()
    {
        // A put-back already in the queue: the leave writes its own at once, and the queued one is not made twice.
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        b.Radio.Queued = true;
        b.Compressor = false;
        b.Tick();
        Assert.Equal(1, b.Radio.Waiting);
        b.Handoff.Leaving();
        Assert.Equal(new[] { false, true }, b.Radio.Writes); // written now, not when a queue next runs
        b.Radio.Drain();
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.NotNull(b.Store.Find(Serial));

        // An OFF still in the queue when the app leaves: it must never go out afterwards.
        var c = new Bench(Path.Combine(_root, "leaving"));
        c.Handoff.Connected();
        c.Tick();
        c.Radio.Queued = true;
        c.Compressor = true;
        c.Tick();
        Assert.Equal(1, c.Radio.Waiting);
        c.Handoff.Leaving();
        c.Radio.Drain();
        Assert.Empty(c.Radio.Writes);
        Assert.True(c.Radio.On);
    }

    // ── the real queue (the vendor-radio fixture; a recording transport, no radio) ──

    private RigOnVendorRadio HandoffRig(out List<ProcessorHandoffNotice> said, out List<string> operatorChanges, string serial = "4321-8765-2109-6543")
    {
        var f = ConnectedRig(serial);
        f.Rig.ProcessorHandoffStoreOverride = new ProcessorHandoffStore(_root);
        RadioConfig.RecordOwnership(f.Vendor.Radio.Serial, RadioOwnership.Mine);
        var heard = new List<ProcessorHandoffNotice>();
        f.Rig.ProcessorHandoffNoticed += heard.Add;
        var changes = new List<string>();
        f.Rig.OperatorChangedRadioSetting += changes.Add;
        said = heard; operatorChanges = changes;
        f.Rig.ProcessorHandoff.Connected();
        return f;
    }

    private static readonly ProcessorHandoffInputs CompressorOn = new(true, true, true, true, 0);
    private static readonly ProcessorHandoffInputs CompressorOff = new(true, false, true, true, 0);

    [Fact]
    public void On_the_real_queue_a_radio_that_keys_before_the_queue_runs_is_never_written()
    {
        using var f = HandoffRig(out var said, out _);
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Assert.True(f.Rig.q.Count > 0);                         // the off is in the queue...
        Assert.Empty(f.Vendor.Transport.Commands);              // ...and has not gone out
        f.Vendor.Status("interlock state=TRANSMITTING source=RCA");
        Assert.True(f.Rig.SpeechProcessorForHandoff.Transmitting, "the fixture's radio does not read as transmitting: the test proves nothing");
        Drain(f.Rig);
        Assert.DoesNotContain(f.Vendor.Transport.Commands, c => c.Contains("speech_processor_enable"));
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);
        Assert.Empty(said);
    }

    [Fact]
    public void On_the_real_queue_a_write_the_handoff_withdrew_is_not_made()
    {
        using var f = HandoffRig(out var said, out _);
        f.Rig.ProcessorHandoff.Tick(CompressorOn);              // the off is asked for...
        Assert.True(f.Rig.q.Count > 0);
        f.Rig.ProcessorHandoff.Tick(CompressorOff);             // ...and the compressor goes off before the queue has run
        Drain(f.Rig);
        Assert.DoesNotContain(f.Vendor.Transport.Commands, c => c.Contains("speech_processor_enable"));
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);
        Assert.Empty(said);
    }

    [Fact]
    public void On_the_real_queue_a_put_back_not_yet_run_keeps_its_record_and_a_link_that_ends_first_writes_nothing()
    {
        using var f = HandoffRig(out var said, out _);
        string serial = f.Vendor.Radio.Serial;
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(f.Rig);
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Assert.Equal(new[] { "transmit set speech_processor_enable=0" }, f.Vendor.Transport.Commands);
        Assert.Single(said);

        f.Rig.ProcessorHandoff.Tick(CompressorOff);             // the put-back is asked for
        Assert.True(f.Rig.q.Count > 0);
        Assert.False(f.Vendor.Radio.SpeechProcessorEnable);     // the radio still reads off
        Assert.NotNull(f.Rig.ProcessorHandoffStoreOverride!.Find(serial)); // so the debt is still on record
        Assert.Single(said);                                    // and "back on" has not been said

        Set(f.Rig, "_IsConnected", false);                      // the link ends before the queue runs
        Drain(f.Rig);
        Assert.Equal(new[] { "transmit set speech_processor_enable=0" }, f.Vendor.Transport.Commands);
        Assert.NotNull(f.Rig.ProcessorHandoffStoreOverride.Find(serial));
    }

    [Fact]
    public void On_the_real_queue_the_put_back_is_forgotten_only_when_the_radio_reads_on_again()
    {
        using var f = HandoffRig(out var said, out var operatorChanges);
        string serial = f.Vendor.Radio.Serial;
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(f.Rig);
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        f.Rig.ProcessorHandoff.Tick(CompressorOff);
        Drain(f.Rig);
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);
        Assert.NotNull(f.Rig.ProcessorHandoffStoreOverride!.Find(serial)); // written, not yet read back
        f.Rig.ProcessorHandoff.Tick(CompressorOff);
        Assert.Null(f.Rig.ProcessorHandoffStoreOverride.Find(serial));
        Assert.Equal(2, said.Count);
        Assert.Equal(new[] { "transmit set speech_processor_enable=0", "transmit set speech_processor_enable=1" }, f.Vendor.Transport.Commands);
        Assert.Empty(operatorChanges); // neither write was read as the operator's
    }

    [Fact]
    public void The_leave_before_teardown_writes_the_put_back_at_once_without_the_queue()
    {
        using var f = HandoffRig(out _, out var operatorChanges);
        string serial = f.Vendor.Radio.Serial;
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(f.Rig);
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        f.Vendor.Transport.Written.Clear();

        f.Rig.ProcessorHandoff.Leaving();
        // No Drain: the command is already out, on the caller's thread, while the transport is up.
        Assert.Equal(new[] { "transmit set speech_processor_enable=1" }, f.Vendor.Transport.Commands);
        Assert.Equal(0, f.Rig.q.Count);
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);
        Assert.NotNull(f.Rig.ProcessorHandoffStoreOverride!.Find(serial)); // kept until the next connect confirms it
        Assert.Empty(operatorChanges);

        // A radio that is transmitting when the app leaves is not written; the record waits.
        using var g = HandoffRig(out _, out _, "5432-8765-2109-6543");
        g.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(g.Rig);
        g.Rig.ProcessorHandoff.Tick(CompressorOn);
        g.Vendor.Transport.Written.Clear();
        g.Vendor.Status("interlock state=TRANSMITTING source=RCA");
        g.Rig.ProcessorHandoff.Leaving();
        Assert.Empty(g.Vendor.Transport.Commands);
        Assert.False(g.Vendor.Radio.SpeechProcessorEnable);
    }

    // ── the teardown road (repair 1, B2): where the leave is called from ──

    private static string Between(string source, string from, string to)
    {
        int a = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, "not found: " + from);
        int b = source.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(b > a, "not found after it: " + to);
        return source.Substring(a, b - a);
    }

    [Fact]
    public void The_leave_is_on_the_road_Disconnect_and_Exit_take_while_the_rig_is_still_up()
    {
        string root = RepoRoot();
        // CloseTheRadio (Disconnect, and Exit through it) unwires the window's radio events and THEN disposes the rig.
        string close = Between(File.ReadAllText(Path.Combine(root, "globals.vb")), "Friend Sub CloseTheRadio()", "End Sub");
        int unwire = close.IndexOf("WpfMainWindow?.UnwireRadioEvents()", StringComparison.Ordinal);
        int dispose = close.IndexOf("RigControl.Dispose()", StringComparison.Ordinal);
        Assert.True(unwire >= 0 && dispose > unwire, "CloseTheRadio no longer unwires before it disposes the rig");

        // The unwire calls the leave among its first acts, before it stops anything.
        string main = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "MainWindow.xaml.cs"));
        string first = Between(main, "public void UnwireRadioEvents()", "PollTimerEnabled = false;");
        Assert.Contains("TxAudioConditioning.LeaveRadioBeforeTeardown(RigControl);", first);

        // And the leave is the handoff's, written at once; the transmit chain's own Detach, which that road never
        // reaches while the link is up (and which Attach calls for a rig that may be live), does not leave.
        string owner = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "TxAudioConditioning.cs"));
        Assert.Contains("rig.ProcessorHandoff.Leaving();", Between(owner, "public static void LeaveRadioBeforeTeardown(FlexBase? rig)", "/// <summary>The words"));
        Assert.DoesNotContain("Leaving()", Between(owner, "public static void Detach()", "// ------------------------------------------------------------------"));
    }

    // ── the store ──

    [Fact]
    public void The_store_keeps_one_record_a_radio_survives_a_new_instance_and_reads_a_damaged_file_as_nothing()
    {
        var store = new ProcessorHandoffStore(_root);
        Assert.Null(store.Find(Serial));
        Assert.True(store.Save(new ProcessorHandoffRecord(Serial, true, 2, false)));
        // A second save for the same radio keeps the FIRST: it holds the operator's own value.
        Assert.True(store.Save(new ProcessorHandoffRecord(Serial, false, 0, false)));
        Assert.True(store.Save(new ProcessorHandoffRecord("9999-0000-1111-2222", true, 1, false)));
        var again = new ProcessorHandoffStore(_root);
        Assert.Equal(new ProcessorHandoffRecord(Serial, true, 2, false), again.Find(Serial));
        Assert.Equal(1, again.Find("9999-0000-1111-2222")!.PreviousLevel);
        again.Remove(Serial);
        Assert.Null(store.Find(Serial));
        Assert.NotNull(store.Find("9999-0000-1111-2222"));
        store.Remove("9999-0000-1111-2222");
        Assert.False(File.Exists(Path.Combine(_root, ProcessorHandoffStore.FileName))); // nothing owed: no file

        File.WriteAllText(Path.Combine(_root, ProcessorHandoffStore.FileName), "not\ta record\n\n" + Serial + "\tTrue\t1\tFalse\n");
        Assert.NotNull(store.Find(Serial));
        Assert.Null(store.Find("not"));
    }

    // ── the Flex side ──

    private static void Set(FlexBase rig, string name, object value)
        => typeof(FlexBase).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig, value);

    private static void Drain(FlexBase rig)
    {
        while (rig.q.Count > 0)
            if (rig.q.Dequeue().Item is FlexBase.FunctionDel action) action();
    }

    private static RigOnVendorRadio ConnectedRig(string name)
    {
        var f = new RigOnVendorRadio(name);
        f.AddOwnSlice(0, 14.25, "USB");
        Set(f.Rig, "_TXVFO", 0);
        Set(f.Rig, "_IsConnected", true);
        f.Rig.q.MainLoop = true;
        f.Vendor.Status("transmit speech_processor_enable=1 speech_processor_level=1");
        f.Vendor.Transport.Written.Clear();
        return f;
    }

    [Fact]
    public void The_Flex_side_reads_the_processor_and_writes_it_as_the_applications_change_never_the_operators()
    {
        using var f = ConnectedRig("handoff-flex");
        var processor = f.Rig.SpeechProcessorForHandoff;
        Assert.True(processor.Connected);
        Assert.Equal(true, processor.ProcessorOn);
        Assert.Equal(1, processor.ProcessorLevel);

        var operatorChanges = new List<string>();
        f.Rig.OperatorChangedRadioSetting += operatorChanges.Add;

        Assert.True(processor.WriteProcessorOn(false, () => true));
        Assert.Empty(f.Vendor.Transport.Commands); // asked: nothing has gone out until the queue runs
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SpeechProcessorEnable);
        Assert.Equal(new[] { "transmit set speech_processor_enable=0" }, f.Vendor.Transport.Commands);
        Assert.Empty(operatorChanges); // the receipt and the save offer are NOT armed

        // The positive control: the operator's own setter, with no scope, IS the operator's change.
        f.Rig.ProcessorOn = FlexBase.OffOnValues.on;
        Drain(f.Rig);
        Assert.Equal(new[] { "ProcessorOn" }, operatorChanges);
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);
        // The level is never written by the handoff.
        Assert.DoesNotContain(f.Vendor.Transport.Commands, c => c.Contains("speech_processor_level"));
    }

    [Fact]
    public void Through_the_rigs_own_handoff_the_compressor_coming_on_writes_the_radio_and_the_notice_is_raised()
    {
        using var f = ConnectedRig("4321-8765-2109-6543");
        f.Rig.ProcessorHandoffStoreOverride = new ProcessorHandoffStore(_root);
        string serial = f.Vendor.Radio.Serial;
        if (string.IsNullOrWhiteSpace(serial)) return; // the fixture's radio has no serial: the policy has no key to record under
        RadioConfig.RecordOwnership(serial, RadioOwnership.Mine);
        var said = new List<ProcessorHandoffNotice>();
        f.Rig.ProcessorHandoffNoticed += said.Add;
        var operatorChanges = new List<string>();
        f.Rig.OperatorChangedRadioSetting += operatorChanges.Add;

        var processor = f.Rig.SpeechProcessorForHandoff;
        Assert.True(processor.RadioIsOperators, "the fixture's radio is not marked as the operator's: serial '" + serial + "'");
        Assert.False(processor.Transmitting, "the fixture's radio reads as transmitting");

        f.Rig.ProcessorHandoff.Connected();
        f.Rig.ProcessorHandoff.Tick(new ProcessorHandoffInputs(true, true, true, true, 0));
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SpeechProcessorEnable);
        Assert.Contains("transmit set speech_processor_enable=0", f.Vendor.Transport.Commands);
        Assert.Empty(said); // not until the radio's reading has been read back
        f.Rig.ProcessorHandoff.Tick(new ProcessorHandoffInputs(true, true, true, true, 0));
        Assert.Single(said);
        Assert.Contains(said[0], new[] { ProcessorHandoffNotice.TurnedOff, ProcessorHandoffNotice.TurnedOffShared });
        Assert.Empty(operatorChanges);
        Assert.NotNull(f.Rig.ProcessorHandoffStoreOverride.Find(serial));
    }

    [Fact]
    public void The_connect_path_calls_the_handoff_once_beside_the_audio_checks_put_back()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.MainThread.cs"));
        Assert.Equal(1, source.Split("ProcessorHandoffAtConnect();").Length - 1);
        Assert.True(source.IndexOf("AnnounceCheckRestoreReminder();", StringComparison.Ordinal)
            < source.IndexOf("ProcessorHandoffAtConnect();", StringComparison.Ordinal));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }
}
