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

    /// <summary>
    /// A radio's speech processor as the handoff sees one. Its READING shows a
    /// write the moment the command is sent, with no radio in it (as a radio
    /// library's own cache does); what the radio itself holds changes only
    /// when it takes a command; and it answers at once, refuses, or never
    /// answers. By default it does what it is told and says so at once.
    /// </summary>
    private sealed class FakeProcessor : IRadioSpeechProcessor
    {
        public string Serial { get; set; } = ProcessorHandoffTests.Serial;
        public bool Connected { get; set; } = true;
        /// <summary>The reading.</summary>
        public bool? On { get; set; } = true;
        public bool? ProcessorOn => On;
        /// <summary>What the radio itself holds: changed only by a command it took.</summary>
        public bool RadioOn { get; set; } = true;
        public int ProcessorLevel { get; set; } = 1;
        public bool Transmitting { get; set; }
        public bool AnotherClientMayBeConnected { get; set; }
        public bool RadioIsOperators { get; set; } = true;
        /// <summary>The write cannot even be asked for.</summary>
        public bool RefuseWrites { get; set; }
        /// <summary>When set, a write waits in a queue until <see cref="Drain"/>, as it waits in a radio's command queue.</summary>
        public bool Queued { get; set; }
        /// <summary>The command link is down although the radio still reads as connected: a write that reaches it sends nothing.</summary>
        public bool LinkDown { get; set; }
        /// <summary>How the radio answers a command sent to it: true takes it at once, false refuses it at once, null never answers (until <see cref="Answer"/>).</summary>
        public bool? Answers { get; set; } = true;
        /// <summary>When set, the radio's answer is reported before the write is reported as sent (two threads, either order).</summary>
        public bool AnswerBeforeSent { get; set; }
        /// <summary>Every command that went to the radio.</summary>
        public List<bool> Writes { get; } = new();
        private readonly List<(bool On, Func<bool>? StillWanted, Action<ProcessorWriteStep>? Report)> _queue = new();
        private readonly List<(bool On, Action<ProcessorWriteStep>? Report)> _unanswered = new();
        public int Waiting => _queue.Count;
        public int Unanswered => _unanswered.Count;
        public bool WriteProcessorOn(bool on, Func<bool> stillWanted, Action<ProcessorWriteStep> report)
        {
            if (RefuseWrites) return false;
            if (Queued) { _queue.Add((on, stillWanted, report)); return true; }
            Make(on, stillWanted, report);
            return true;
        }
        public bool WriteProcessorOnNow(bool on) => Make(on, null, null);
        /// <summary>The queue runs: each write is made only if it is still right at that moment (the contract every implementation keeps).</summary>
        public void Drain()
        {
            var waiting = _queue.ToArray();
            _queue.Clear();
            foreach (var (on, stillWanted, report) in waiting) Make(on, stillWanted, report);
        }
        /// <summary>The radio answers everything it was sent and has not answered: it took it, or it refuses it.</summary>
        public void Answer(bool accept = true)
        {
            while (_unanswered.Count > 0) AnswerOldest(accept);
        }
        /// <summary>The radio answers the oldest command it has not answered.</summary>
        public void AnswerOldest(bool accept = true)
        {
            var (on, report) = _unanswered[0];
            _unanswered.RemoveAt(0);
            if (accept) RadioOn = on;
            // The contract: an answer that finds another value in the reading says the reading is unsettled.
            report?.Invoke(!accept ? ProcessorWriteStep.Refused : On == on ? ProcessorWriteStep.Accepted : ProcessorWriteStep.AcceptedUnsettled);
        }
        private bool Make(bool on, Func<bool>? stillWanted, Action<ProcessorWriteStep>? report)
        {
            if (!Connected || Transmitting || (stillWanted != null && !stillWanted()) || LinkDown)
            {
                report?.Invoke(ProcessorWriteStep.NotMade);
                return false;
            }
            Writes.Add(on);
            if (AnswerBeforeSent && Answers is bool early)
            {
                if (early) RadioOn = on;
                report?.Invoke(early ? ProcessorWriteStep.Accepted : ProcessorWriteStep.Refused);
            }
            report?.Invoke(ProcessorWriteStep.Sent); // before the reading shows it: the contract
            On = on;                                 // the reading shows the write at once, with no radio in it
            if (AnswerBeforeSent) return true;
            if (Answers == true) { RadioOn = on; report?.Invoke(ProcessorWriteStep.Accepted); }
            else if (Answers == false) report?.Invoke(ProcessorWriteStep.Refused);
            else _unanswered.Add((on, report));
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

        /// <summary>Tick until a write just asked for has been answered by the radio and said, with ticks to spare.</summary>
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
        Assert.Empty(b.Said); // asked and, on this radio, sent and answered at once; said on the tick that reads the answer
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
    public void A_reading_that_shows_on_before_the_radio_has_answered_is_not_taken_for_somebody_turning_it_on()
    {
        var b = NewBench();
        b.Radio.Answers = null;                // the radio has not answered yet
        b.Compressor = true;
        b.Tick();
        // An older report puts "on" back in the reading while the app's off is on its way.
        b.Radio.On = true;
        b.Tick(ProcessorHandoff.SettleTicks - 1);
        Assert.NotNull(b.Store.Find(Serial));  // not mistaken for somebody turning it on
        Assert.Empty(b.Said);
        b.Radio.On = false;                    // the radio reports off (its own report, by the time it answers)...
        b.Radio.Answer();                      // ...and the answer arrives
        b.Tick(5);
        Assert.NotNull(b.Store.Find(Serial));
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff }, b.Said);
        Assert.Equal(new[] { false }, b.Radio.Writes);
    }

    [Fact]
    public void The_reading_taken_in_the_tick_that_learns_of_a_write_is_not_judged_against_it()
    {
        // The tick reads the reading first and the news of the app's writes after: a reading from
        // that same tick may be older than the write the news tells of.
        var b = NewBench();
        b.Compressor = true;
        b.Tick();                              // the off: sent, and the radio answers at once
        b.Radio.On = true;                     // the reading this tick takes is from before the write
        b.Tick();                              // the tick that learns the radio took the off
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff }, b.Said);
        Assert.NotNull(b.Store.Find(Serial));  // "on" in that reading was not somebody's turn-on
        b.Radio.On = false;
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
    public void Asked_is_not_written_the_sentence_and_the_record_wait_for_the_radios_answer()
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
    public void A_put_back_that_was_never_made_is_asked_for_again()
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

    // ── only the radio's answer confirms (repair 2, B3a): the policy against a radio that does not answer, refuses, or cannot be reached ──

    [Fact]
    public void Sent_is_not_accepted_the_sentences_and_the_record_wait_for_the_radios_answer()
    {
        var b = NewBench();
        b.Radio.Answers = null;                     // the radio never answers, until told to
        b.Compressor = true;
        b.Tick();
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.False(b.Radio.On);                   // positive control: the reading shows the app's own write at once
        Assert.True(b.Radio.RadioOn);               // positive control: the radio itself has not taken anything
        b.Tick(12);
        Assert.Empty(b.Said);                       // the reading, read back twelve times, is not the radio's word
        Assert.NotNull(b.Store.Find(Serial));

        b.Radio.Answer();
        b.Tick();
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff }, b.Said);

        b.Compressor = false;
        b.Tick(12);
        Assert.Contains(true, b.Radio.Writes);      // positive control: the put-back went out
        Assert.True(b.Radio.On);                    // positive control: and the reading shows it
        Assert.Single(b.Said);                      // "back on" is not said
        Assert.NotNull(b.Store.Find(Serial));       // and the record is not forgotten

        b.Radio.Answer();
        b.Tick();
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff, ProcessorHandoffNotice.PutBack }, b.Said);
        Assert.Null(b.Store.Find(Serial));
    }

    [Fact]
    public void A_write_the_radio_does_not_answer_is_sent_again_at_growing_intervals_and_never_more_than_a_minute_apart()
    {
        var b = NewBench();
        b.Radio.Answers = null;
        b.Compressor = true;
        var sentAt = new List<int>();
        for (int tick = 0; tick < 3000; tick++)
        {
            int before = b.Radio.Writes.Count;
            b.Tick();
            if (b.Radio.Writes.Count > before) sentAt.Add(tick);
        }
        Assert.All(b.Radio.Writes, on => Assert.False(on)); // every asking again is the same write, on the wire
        Assert.Empty(b.Said);
        Assert.NotNull(b.Store.Find(Serial));
        var gaps = sentAt.Zip(sentAt.Skip(1), (a, next) => next - a).ToList();
        // The first asking again comes after the settle time; each wait is longer than the last, up to the longest.
        Assert.Equal(ProcessorHandoff.SettleTicks + 2, gaps[0]);
        Assert.Equal(2 * ProcessorHandoff.SettleTicks + 2, gaps[1]);
        Assert.Equal(3 * ProcessorHandoff.SettleTicks + 2, gaps[2]);
        Assert.Equal(ProcessorHandoff.LongestWaitTicks + 2, gaps.Max());
        Assert.Equal(ProcessorHandoff.LongestWaitTicks + 2, gaps[^1]);
    }

    [Fact]
    public void An_off_the_radio_refuses_is_undone_quietly_and_not_tried_again_until_the_compressor_is_next_switched_on()
    {
        var b = NewBench();
        b.Radio.Answers = false;                    // the radio refuses the off
        b.Compressor = true;
        b.Tick(2);
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.False(b.Radio.On);                   // positive control: the reading holds the refused write
        Assert.Empty(b.Said);

        b.Radio.Answers = true;                     // it takes the undo
        b.Tick(3);
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.True(b.Radio.On);                    // the reading no longer claims an off the radio refused
        Assert.Empty(b.Said);                       // nothing went off, so nothing is said to have come back
        Assert.Null(b.Store.Find(Serial));

        b.Tick(40);
        Assert.Equal(new[] { false, true }, b.Radio.Writes); // not tried again while the compressor stays on

        b.Compressor = false;
        b.Tick();
        b.Compressor = true;                        // the compressor next switched on: a new try
        b.Tick(2);
        Assert.Equal(new[] { false, true, false }, b.Radio.Writes);
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff }, b.Said);
    }

    [Fact]
    public void A_put_back_the_radio_refuses_is_not_asked_again_and_the_record_waits_for_the_next_connections_first_reading()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick(2);
        Assert.Single(b.Said);

        b.Radio.Answers = false;                    // the radio refuses the put-back
        b.Compressor = false;
        b.Tick(2);
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        b.Tick(300);
        Assert.Equal(new[] { false, true }, b.Radio.Writes); // a refusal is not hammered at
        Assert.Single(b.Said);
        Assert.NotNull(b.Store.Find(Serial));       // the debt is kept

        // The next connection reads the radio itself: still off, so it is put back and said.
        b.Radio.Answers = true;
        b.Radio.On = b.Radio.RadioOn;               // a new connection's first reading is the radio's own
        Assert.False(b.Radio.On);
        b.Reconnect();
        b.Tick(3);
        Assert.Equal(new[] { false, true, true }, b.Radio.Writes);
        Assert.Equal(ProcessorHandoffNotice.PutBackAtConnect, b.Said[^1]);
        Assert.Null(b.Store.Find(Serial));
    }

    [Fact]
    public void A_refused_put_back_does_not_stop_the_next_session_and_its_own_put_back_is_asked_afresh()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick(2);
        b.Radio.Answers = false;
        b.Compressor = false;
        b.Tick(4);                                  // the put-back: refused
        Assert.NotNull(b.Store.Find(Serial));

        b.Radio.Answers = true;
        b.Compressor = true;                        // the compressor comes on again: the off is asserted, and answered
        b.Tick(3);
        Assert.Equal(new[] { false, true, false }, b.Radio.Writes);
        Assert.Equal(2, b.Said.Count(n => n == ProcessorHandoffNotice.TurnedOff));
        b.Compressor = false;                       // and this session's end is a new occasion to put it back
        b.Tick(3);
        Assert.Equal(new[] { false, true, false, true }, b.Radio.Writes);
        Assert.Equal(ProcessorHandoffNotice.PutBack, b.Said[^1]);
        Assert.Null(b.Store.Find(Serial));
    }

    [Fact]
    public void A_command_link_that_is_down_sends_nothing_leaves_the_reading_alone_and_keeps_the_record()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick(2);
        Assert.Single(b.Said);

        b.Radio.LinkDown = true;                    // the link has dropped; the radio still reads as connected
        b.Compressor = false;
        b.Tick(12);
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.False(b.Radio.On);
        Assert.Single(b.Said);
        Assert.NotNull(b.Store.Find(Serial));

        b.Radio.LinkDown = false;                   // positive control: with the link up it is put back and said
        b.Tick(4);
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.Equal(ProcessorHandoffNotice.PutBack, b.Said[^1]);
        Assert.Null(b.Store.Find(Serial));
    }

    [Fact]
    public void The_radios_answer_arriving_before_the_write_is_reported_sent_is_still_the_confirmation()
    {
        var b = NewBench();
        b.Radio.AnswerBeforeSent = true;            // the reader's thread tells of the answer before the queue's thread tells of the sending
        b.Compressor = true;
        b.Tick(2);
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff }, b.Said);
        b.Compressor = false;
        b.Tick(2);
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff, ProcessorHandoffNotice.PutBack }, b.Said);
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.Null(b.Store.Find(Serial));
    }

    [Fact]
    public void An_answer_to_a_write_the_handoff_has_moved_on_from_changes_nothing()
    {
        var b = NewBench();
        b.Radio.Answers = null;
        b.Compressor = true;
        b.Tick(2);                                  // the off: sent, not answered
        b.Compressor = false;                       // the session ends before the radio answers
        b.Tick(2);                                  // so the put-back goes out, unanswered too
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.Empty(b.Said);
        Assert.NotNull(b.Store.Find(Serial));

        b.Radio.AnswerOldest();                     // the radio's answer to the OFF arrives, late
        b.Tick(3);
        Assert.Empty(b.Said);                       // it is not said as "turned off", and it does not pass for the put-back's answer
        Assert.NotNull(b.Store.Find(Serial));

        b.Radio.AnswerOldest();                     // then its answer to the put-back
        b.Tick(3);
        Assert.Equal(new[] { ProcessorHandoffNotice.PutBack }, b.Said);
        Assert.Null(b.Store.Find(Serial));
        Assert.True(b.Radio.RadioOn);
    }

    [Fact]
    public void An_unanswered_put_back_whose_asking_again_cannot_be_made_keeps_the_record_and_is_asked_once_more()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick(2);
        b.Radio.Answers = null;
        b.Compressor = false;
        b.Tick(2);                                  // the put-back: sent, not answered; the reading shows the app's own "on"
        Assert.True(b.Radio.On);
        b.Radio.LinkDown = true;                    // the asking again finds the command link down
        b.Tick(ProcessorHandoff.SettleTicks + 6);
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.NotNull(b.Store.Find(Serial));       // "on" in the reading is the app's own write, not somebody's: the debt stands
        Assert.Single(b.Said);

        b.Radio.LinkDown = false;
        b.Tick(3);
        Assert.Equal(new[] { false, true, true }, b.Radio.Writes); // asked once more, on the wire
        b.Radio.Answer();
        b.Tick();
        Assert.Equal(ProcessorHandoffNotice.PutBack, b.Said[^1]);
        Assert.Null(b.Store.Find(Serial));
    }

    [Fact]
    public void An_unanswered_put_back_and_the_compressor_coming_on_again_asserts_the_off_and_keeps_the_record()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick(2);
        b.Radio.Answers = null;
        b.Compressor = false;
        b.Tick(2);                                  // the put-back: sent, not answered
        Assert.True(b.Radio.On);                    // the reading shows the app's own put-back
        b.Compressor = true;
        b.Tick();                                   // the app's own "on" in the reading is not somebody's turn-on
        Assert.Equal(new[] { false, true, false }, b.Radio.Writes);
        Assert.NotNull(b.Store.Find(Serial));
        b.Radio.AnswerOldest();                     // the late answer to the put-back the handoff has moved on from
        b.Tick(2);
        Assert.Single(b.Said);                      // changes nothing: not "back on", and not this off's answer
        Assert.NotNull(b.Store.Find(Serial));
        b.Radio.Answer();
        b.Tick(3);
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff, ProcessorHandoffNotice.TurnedOff }, b.Said);
        Assert.NotNull(b.Store.Find(Serial));
        Assert.False(b.Radio.RadioOn);
    }

    [Fact]
    public void An_asking_again_that_is_not_made_does_not_lose_the_write_and_it_is_sent_at_the_unkey()
    {
        var b = NewBench();
        b.Radio.Answers = null;
        b.Compressor = true;
        b.Tick(ProcessorHandoff.SettleTicks + 2);   // sent, unanswered, and its settle time used up
        b.Radio.Queued = true;
        b.Tick();                                   // asked again: in the queue
        Assert.Equal(1, b.Radio.Waiting);
        b.Radio.Transmitting = true;                // keyed when the queue reaches it
        b.Radio.Drain();
        Assert.Equal(new[] { false }, b.Radio.Writes);
        b.Tick(6);
        Assert.Equal(0, b.Radio.Waiting);           // not asked for while it transmits
        b.Radio.Transmitting = false;
        b.Tick();
        Assert.Equal(1, b.Radio.Waiting);
        b.Radio.Drain();
        Assert.Equal(new[] { false, false }, b.Radio.Writes);
        b.Radio.Answer();
        b.Tick();
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff }, b.Said);
    }

    // ── a reading that disagrees at the radio's answer is unsettled (repair 3) ──

    /// <summary>A bench whose off was taken by the radio while its reading said on.</summary>
    private Bench Unsettled()
    {
        var b = NewBench();
        b.Radio.Answers = null;
        b.Compressor = true;
        b.Tick();
        b.Radio.On = true;                          // a report parsed since the write: older than it, or somebody's newer turn-on
        b.Radio.Answer();                           // the radio took the off
        b.Radio.Answers = true;
        b.Tick(2);
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff }, b.Said);
        return b;
    }

    [Fact]
    public void An_unsettled_reading_is_neither_fought_nor_forgotten_and_the_sessions_end_puts_the_processor_back_whatever_it_says()
    {
        var b = Unsettled();
        b.Tick(40);
        Assert.Equal(new[] { false }, b.Radio.Writes); // not turned off again
        Assert.NotNull(b.Store.Find(Serial));       // and the record is not dropped: if that "on" was an older report, the radio is off

        b.Compressor = false;
        b.Tick(3);
        Assert.Equal(new[] { false, true }, b.Radio.Writes); // put back although the reading already said on
        Assert.Equal(ProcessorHandoffNotice.PutBack, b.Said[^1]);
        Assert.Null(b.Store.Find(Serial));
        Assert.True(b.Radio.RadioOn);
    }

    [Fact]
    public void An_unsettled_reading_that_next_reads_off_is_settled_as_held_and_an_on_after_that_is_somebodys()
    {
        var b = Unsettled();
        b.Radio.On = false;                         // the radio reports off
        b.Tick(2);
        b.Radio.On = true;                          // and later on: somebody turned it on
        b.Tick(3);
        Assert.Null(b.Store.Find(Serial));
        Assert.Equal(new[] { false }, b.Radio.Writes);
        b.Compressor = false;
        b.Tick(3);
        Assert.Equal(new[] { false }, b.Radio.Writes); // nothing of the app's to put back
    }

    [Fact]
    public void Taken_over_with_the_owed_value_while_unsettled_the_apps_put_back_is_asked_quietly_and_the_off_is_not_tried_again()
    {
        var b = Unsettled();
        b.Handoff.TakenOver(true);                  // the operator says on; the reading already says on
        Assert.NotNull(b.Store.Find(Serial));       // not dropped: their "on" may never reach the radio by itself
        b.Tick(3);
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.Null(b.Store.Find(Serial));
        Assert.Single(b.Said);                      // nothing is said about it
        b.Tick(40);
        Assert.Equal(new[] { false, true }, b.Radio.Writes); // theirs for this session

        // The compressor next switched on is a new session.
        b.Compressor = false;
        b.Tick();
        b.Compressor = true;
        b.Tick(2);
        Assert.Equal(new[] { false, true, false }, b.Radio.Writes);
    }

    [Fact]
    public void Taken_over_with_the_other_value_while_unsettled_or_with_no_value_named_drops_the_record_as_ever()
    {
        var b = Unsettled();
        b.Handoff.TakenOver(false);                 // they say off: the radio's software sends that itself
        Assert.Null(b.Store.Find(Serial));
        b.Compressor = false;
        b.Tick(4);
        Assert.Equal(new[] { false }, b.Radio.Writes);

        var c = Unsettled();
        c.Handoff.TakenOver();                      // a caller that does not know the value
        Assert.Null(c.Store.Find(Serial));
    }

    [Fact]
    public void Taken_over_with_the_owed_value_when_the_reading_is_settled_drops_the_record_as_ever()
    {
        // Held off, the reading says off: their "on" differs from the reading, so the radio's software sends it.
        var b = NewBench();
        b.Compressor = true;
        b.Tick(2);
        b.Handoff.TakenOver(true);
        Assert.Null(b.Store.Find(Serial));
        b.Radio.On = true;
        b.Tick(5);
        Assert.Equal(new[] { false }, b.Radio.Writes);
    }

    [Fact]
    public void A_put_back_the_radio_took_while_its_reading_said_otherwise_is_paid_and_said()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick(2);
        b.Radio.Answers = null;
        b.Compressor = false;
        b.Tick(2);                                  // the put-back: sent
        b.Radio.On = false;                         // a report parsed since
        b.Radio.Answer();                           // the radio took the put-back
        b.Radio.Answers = true;
        b.Tick(2);
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOff, ProcessorHandoffNotice.PutBack }, b.Said);
        Assert.Null(b.Store.Find(Serial));
        b.Tick(20);
        Assert.Equal(new[] { false, true }, b.Radio.Writes); // nothing is sent to make the reading agree
    }

    [Fact]
    public void The_leave_puts_the_processor_back_while_the_reading_is_unsettled()
    {
        var b = Unsettled();
        b.Handoff.Leaving();
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        Assert.NotNull(b.Store.Find(Serial));
    }

    // ── the leave reads its news first (repair 3) ──

    [Fact]
    public void The_leave_reads_what_the_radios_side_reported_since_the_last_tick_before_it_decides()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();                                   // the off: sent and answered, and no tick has read that yet
        Assert.Equal(new[] { false }, b.Radio.Writes);
        b.Handoff.Leaving();
        Assert.Equal(new[] { false, true }, b.Radio.Writes); // put back on the way out
        Assert.NotNull(b.Store.Find(Serial));
        Assert.Empty(b.Said);                       // and nothing is said on the way out
        b.Tick();
        Assert.Empty(b.Said);                       // nor is a "turned off" from before the leave left over for a tick that comes after it

        // Sent and not yet answered: the same.
        var c = new Bench(Path.Combine(_root, "leave-news"));
        c.Handoff.Connected();
        c.Tick();
        c.Radio.Answers = null;
        c.Compressor = true;
        c.Tick();
        c.Handoff.Leaving();
        Assert.Equal(new[] { false, true }, c.Radio.Writes);

        // The positive control: an off still in the queue was never sent, and the leave writes nothing.
        var d = new Bench(Path.Combine(_root, "leave-queued"));
        d.Handoff.Connected();
        d.Tick();
        d.Radio.Queued = true;
        d.Compressor = true;
        d.Tick();
        d.Handoff.Leaving();
        d.Radio.Drain();
        Assert.Empty(d.Radio.Writes);
    }

    // ── the operator's own value, for whatever saves it (#1302) ──

    [Fact]
    public void The_operators_value_is_the_one_from_before_the_apps_change_for_as_long_as_the_app_holds_a_record()
    {
        var b = NewBench();
        Assert.Null(b.Handoff.OperatorsValue);      // no record: the reading is the operator's own
        b.Compressor = true;
        b.Tick(2);
        Assert.False(b.Radio.On);                   // positive control: the reading says off, by the app's hand
        Assert.True(b.Handoff.OperatorsValue);
        b.Radio.Connected = false;
        Assert.Null(b.Handoff.OperatorsValue);      // nothing is claimed about a radio that is not there
        b.Radio.Connected = true;
        b.Compressor = false;
        b.Tick(2);
        Assert.Null(b.Handoff.OperatorsValue);      // put back and answered: no record
        Assert.True(b.Radio.On);
    }

    // ── somebody else's change wins at once (repair 2, B3b) ──

    [Fact]
    public void Taken_over_while_the_apps_off_is_still_in_the_queue_the_off_is_never_made()
    {
        var b = NewBench();
        b.Radio.Queued = true;
        b.Compressor = true;
        b.Tick();
        Assert.Equal(1, b.Radio.Waiting);           // positive control
        b.Handoff.TakenOver();                      // the operator sets the processor through the rig's own setter
        b.Radio.Drain();
        b.Tick(10);
        b.Radio.Drain();
        Assert.Empty(b.Radio.Writes);
        Assert.True(b.Radio.On);
        Assert.Null(b.Store.Find(Serial));
        Assert.Empty(b.Said);
    }

    [Fact]
    public void Taken_over_after_the_apps_off_was_sent_and_before_any_answer_or_poll_it_is_never_turned_off_again()
    {
        var b = NewBench();
        b.Radio.Answers = null;
        b.Compressor = true;
        b.Tick();                                   // the off is sent; no answer, and no poll since
        Assert.Equal(new[] { false }, b.Radio.Writes);
        b.Handoff.TakenOver();                      // the operator turns it on
        b.Radio.On = true;
        b.Radio.Answer();                           // the radio's late answer to the app's off
        b.Tick(12);
        Assert.Equal(new[] { false }, b.Radio.Writes); // never asked off again
        Assert.True(b.Radio.On);
        Assert.Null(b.Store.Find(Serial));          // theirs: nothing of the app's to put back
        Assert.Empty(b.Said);                       // and the late answer is not said as "turned off"

        b.Compressor = false;                       // the session's end writes nothing
        b.Tick(3);
        Assert.Equal(new[] { false }, b.Radio.Writes);
    }

    [Fact]
    public void Taken_over_with_the_processor_left_off_the_app_does_not_put_it_back_on_afterwards()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick(2);
        Assert.Single(b.Said);
        b.Handoff.TakenOver();                      // the operator's own preset says off: it already reads off
        Assert.Null(b.Store.Find(Serial));
        b.Compressor = false;
        b.Tick(5);
        Assert.Equal(new[] { false }, b.Radio.Writes); // their off stands: the app writes no put-back
        Assert.False(b.Radio.On);
        Assert.Single(b.Said);
    }

    [Fact]
    public void Taken_over_while_the_put_back_is_in_the_queue_the_put_back_is_never_made_and_the_next_session_acts_again()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick(2);
        b.Radio.Queued = true;
        b.Compressor = false;
        b.Tick();
        Assert.Equal(1, b.Radio.Waiting);           // positive control: the put-back is waiting
        b.Handoff.TakenOver();
        b.Radio.Drain();
        b.Tick(6);
        b.Radio.Drain();
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.Null(b.Store.Find(Serial));
        Assert.Single(b.Said);

        // Their change does not switch the handoff off for good: the compressor next coming on is a new session.
        b.Radio.Queued = false;
        b.Radio.On = true;
        b.Compressor = true;
        b.Tick(2);
        Assert.Equal(new[] { false, false }, b.Radio.Writes);
        Assert.Equal(2, b.Said.Count);
    }

    [Fact]
    public void Taken_over_while_the_session_waits_for_the_unkey_the_processor_is_not_turned_off_at_the_unkey()
    {
        var b = NewBench();
        b.Radio.Transmitting = true;
        b.Compressor = true;
        b.Tick(3);
        Assert.Empty(b.Radio.Writes);               // positive control: the off is waiting for the unkey
        b.Handoff.TakenOver();                      // the operator sets the processor themselves, mid-over
        b.Radio.Transmitting = false;
        b.Tick(6);
        Assert.Empty(b.Radio.Writes);               // theirs for this session: not turned off at the unkey
        Assert.Empty(b.Said);

        // The positive control: without their change, the unkey is when it goes off.
        var c = new Bench(Path.Combine(_root, "unkey"));
        c.Handoff.Connected();
        c.Tick();
        c.Radio.Transmitting = true;
        c.Compressor = true;
        c.Tick(3);
        c.Radio.Transmitting = false;
        c.Tick(2);
        Assert.Equal(new[] { false }, c.Radio.Writes);
    }

    [Fact]
    public void Taken_over_on_a_radio_that_is_not_connected_changes_nothing()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick(2);
        b.Radio.Connected = false;                  // a setter used with no radio there cannot reach it
        b.Handoff.TakenOver();
        Assert.NotNull(b.Store.Find(Serial));       // the record still waits for the next connect
    }

    [Fact]
    public void What_the_handoff_says_is_said_with_its_lock_let_go()
    {
        // The app says a notice on the speech path; the operator's own setter, on another thread,
        // tells the handoff it was taken over. Said under the lock, the two can hold each other up.
        var radio = new FakeProcessor();
        var store = new ProcessorHandoffStore(Path.Combine(_root, "unlocked"));
        ProcessorHandoff? handoff = null;
        bool reachedFromAnotherThread = false;
        handoff = new ProcessorHandoff(radio, store, notice =>
        {
            var other = System.Threading.Tasks.Task.Run(() => handoff!.HoldsRecord);
            reachedFromAnotherThread = other.Wait(TimeSpan.FromSeconds(5));
        }, _ => { });
        handoff.Connected();
        var on = new ProcessorHandoffInputs(true, true, true, true, 0);
        handoff.Tick(on);
        handoff.Tick(on);
        Assert.True(reachedFromAnotherThread, "the notice was said while the handoff's lock was held");
    }

    // ── the leave, with a put-back the radio never answered ──

    [Fact]
    public void The_leave_sends_an_unanswered_put_back_again_and_leaves_somebody_elses_turn_on_alone()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick(2);
        b.Radio.Answers = null;
        b.Compressor = false;
        b.Tick(2);                                  // the put-back: sent, not answered; the reading shows it
        Assert.Equal(new[] { false, true }, b.Radio.Writes);
        b.Handoff.Leaving();
        Assert.Equal(new[] { false, true, true }, b.Radio.Writes); // sent again on the way out, although the reading already says on
        Assert.NotNull(b.Store.Find(Serial));

        // Somebody else's turn-on, with no put-back of the app's sent: the leave writes nothing.
        var c = new Bench(Path.Combine(_root, "leave-alone"));
        c.Handoff.Connected();
        c.Tick();
        c.Compressor = true;
        c.Tick(2);
        c.Radio.On = true;
        c.Handoff.Leaving();
        Assert.Equal(new[] { false }, c.Radio.Writes);
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
        Answer(f, new HashSet<int>());
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Assert.Equal(new[] { OffCommand, OffCommand }, f.Vendor.Transport.Commands); // with a reply handler, then FlexLib's own setter
        Assert.Single(said);

        f.Rig.ProcessorHandoff.Tick(CompressorOff);             // the put-back is asked for
        Assert.True(f.Rig.q.Count > 0);
        Assert.False(f.Vendor.Radio.SpeechProcessorEnable);     // the radio still reads off
        Assert.NotNull(f.Rig.ProcessorHandoffStoreOverride!.Find(serial)); // so the debt is still on record
        Assert.Single(said);                                    // and "back on" has not been said

        Set(f.Rig, "_IsConnected", false);                      // the link ends before the queue runs
        Drain(f.Rig);
        Assert.Equal(new[] { OffCommand, OffCommand }, f.Vendor.Transport.Commands);
        Assert.NotNull(f.Rig.ProcessorHandoffStoreOverride.Find(serial));
    }

    [Fact]
    public void On_the_real_queue_the_put_back_is_forgotten_only_when_the_radio_answers_that_it_took_it()
    {
        using var f = HandoffRig(out var said, out var operatorChanges);
        string serial = f.Vendor.Radio.Serial;
        var answered = new HashSet<int>();
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(f.Rig);
        Answer(f, answered);
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        f.Rig.ProcessorHandoff.Tick(CompressorOff);
        Drain(f.Rig);
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);      // FlexLib's reading shows the app's own write
        for (int i = 0; i < 3; i++) f.Rig.ProcessorHandoff.Tick(CompressorOff);
        Assert.NotNull(f.Rig.ProcessorHandoffStoreOverride!.Find(serial)); // sent, read back, and still owed: the radio has not answered
        Assert.Single(said);
        Answer(f, answered);
        f.Rig.ProcessorHandoff.Tick(CompressorOff);
        Assert.Null(f.Rig.ProcessorHandoffStoreOverride.Find(serial));
        Assert.Equal(2, said.Count);
        Assert.Equal(new[] { OffCommand, OffCommand, OnCommand, OnCommand }, f.Vendor.Transport.Commands);
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
        // No Drain: the command is already out, on the caller's thread, while the transport is up
        // (once with a reply handler nobody will wait for, once from FlexLib's own setter).
        Assert.Equal(new[] { OnCommand, OnCommand }, f.Vendor.Transport.Commands);
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

    // ── repair 2 (B3a, B3b): only the radio's own answer confirms a write, and the operator's own change wins at once ──

    /// <summary>The radio answers every speech-processor command written so far and not yet answered. Returns how many it answered.</summary>
    private static int Answer(RigOnVendorRadio f, HashSet<int> answered, uint code = 0)
    {
        int count, n = 0;
        lock (f.Vendor.Transport.Written) count = f.Vendor.Transport.Written.Count;
        var commands = f.Vendor.Transport.Commands;
        for (int i = 0; i < count; i++)
        {
            if (!commands[i].Contains("speech_processor_enable")) continue;
            int seq = f.Vendor.Transport.SequenceOf(i);
            if (!answered.Add(seq)) continue;
            f.Vendor.Reply(seq, code, "");
            n++;
        }
        return n;
    }

    /// <summary>Policy ticks with the radio's command queue running after each, as the app's half-second tick and the queue's own thread do.</summary>
    private static void Poll(RigOnVendorRadio f, ProcessorHandoffInputs inputs, int times, HashSet<int>? answering = null)
    {
        for (int i = 0; i < times; i++)
        {
            f.Rig.ProcessorHandoff.Tick(inputs);
            Drain(f.Rig);
            if (answering != null) Answer(f, answering);
        }
    }

    private static List<string> ProcessorCommands(RigOnVendorRadio f)
        => f.Vendor.Transport.Commands.Where(c => c.Contains("speech_processor_enable")).ToList();

    private const string OffCommand = "transmit set speech_processor_enable=0";
    private const string OnCommand = "transmit set speech_processor_enable=1";
    /// <summary>A refusal as the radio words one: any code but zero.</summary>
    private const uint RefusedCode = 0x50000043;

    [Fact]
    public void A_radio_that_never_answers_is_never_said_to_have_changed_and_the_record_is_kept()
    {
        using var f = HandoffRig(out var said, out _);
        string serial = f.Vendor.Radio.Serial;
        var store = f.Rig.ProcessorHandoffStoreOverride!;
        var answered = new HashSet<int>();

        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(f.Rig);
        Assert.Contains(OffCommand, ProcessorCommands(f));          // positive control: the off went out
        Assert.False(f.Vendor.Radio.SpeechProcessorEnable);         // positive control: FlexLib's own setter shows it at once, with no radio in it
        int offsAtFirst = ProcessorCommands(f).Count(c => c == OffCommand);

        Poll(f, CompressorOn, 12);
        Assert.Empty(said);                                         // FlexLib's own reading is not the radio's word
        Assert.NotNull(store.Find(serial));
        // Asked again, and the asking again reached the wire although FlexLib's reading already held the value.
        Assert.True(ProcessorCommands(f).Count(c => c == OffCommand) > offsAtFirst, "the off was not asked for again on the wire");

        // The radio's answer is what says it.
        Assert.True(Answer(f, answered) > 0);
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Assert.Single(said);
        Assert.Contains(said[0], new[] { ProcessorHandoffNotice.TurnedOff, ProcessorHandoffNotice.TurnedOffShared });

        // The put-back: sent, and never answered.
        Poll(f, CompressorOff, 12);
        Assert.Contains(OnCommand, ProcessorCommands(f));           // positive control: the put-back went out
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);          // positive control: FlexLib's reading shows our own write
        Assert.Single(said);                                        // "back on" is not said
        Assert.NotNull(store.Find(serial));                         // and the record is kept

        // A report from the radio that it is still off: the debt stands and the put-back is asked for again at once.
        int onsBefore = ProcessorCommands(f).Count(c => c == OnCommand);
        f.Vendor.Status("transmit speech_processor_enable=0");
        Poll(f, CompressorOff, 2);
        Assert.NotNull(store.Find(serial));
        Assert.True(ProcessorCommands(f).Count(c => c == OnCommand) > onsBefore, "the put-back was not asked for again after the radio reported off");
        Assert.Single(said);

        // And when the radio does answer, the debt is paid and said.
        Assert.True(Answer(f, answered) > 0);
        f.Rig.ProcessorHandoff.Tick(CompressorOff);
        Assert.Null(store.Find(serial));
        Assert.Equal(2, said.Count);
        Assert.Contains(said[1], new[] { ProcessorHandoffNotice.PutBack, ProcessorHandoffNotice.PutBackShared });
    }

    [Fact]
    public void An_off_the_radio_refuses_is_never_said_and_the_processors_reading_is_set_back()
    {
        using var f = HandoffRig(out var said, out _);
        string serial = f.Vendor.Radio.Serial;
        var store = f.Rig.ProcessorHandoffStoreOverride!;
        var answered = new HashSet<int>();

        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SpeechProcessorEnable);         // positive control: the off went out and FlexLib shows it
        Assert.True(Answer(f, answered, RefusedCode) > 0);          // the radio refuses it

        Poll(f, CompressorOn, 20, answering: answered);             // whatever is asked from here, the radio takes
        Assert.Empty(said);                                         // nothing was turned off, so nothing is said about it going off or coming back
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);          // FlexLib's reading no longer claims an off the radio refused
        Assert.Null(store.Find(serial));

        // Not asked again while the compressor stays on...
        int offs = ProcessorCommands(f).Count(c => c == OffCommand);
        Poll(f, CompressorOn, 20, answering: answered);
        Assert.Equal(offs, ProcessorCommands(f).Count(c => c == OffCommand));
        // ...and the compressor next switched on is a new try.
        Poll(f, CompressorOff, 2, answering: answered);
        Poll(f, CompressorOn, 3, answering: answered);
        Assert.True(ProcessorCommands(f).Count(c => c == OffCommand) > offs);
        Assert.Single(said);
    }

    [Fact]
    public void A_put_back_the_radio_refuses_is_never_said_and_the_record_waits_for_the_next_connection()
    {
        using var f = HandoffRig(out var said, out _);
        string serial = f.Vendor.Radio.Serial;
        var store = f.Rig.ProcessorHandoffStoreOverride!;
        var answered = new HashSet<int>();

        Poll(f, CompressorOn, 2, answering: answered);
        Assert.Single(said);                                        // positive control: the off was taken and said

        f.Rig.ProcessorHandoff.Tick(CompressorOff);
        Drain(f.Rig);
        Assert.Contains(OnCommand, ProcessorCommands(f));           // positive control: the put-back went out
        Assert.True(Answer(f, answered, RefusedCode) > 0);          // and the radio refuses it

        int ons = ProcessorCommands(f).Count(c => c == OnCommand);
        Poll(f, CompressorOff, 20);
        Assert.Single(said);                                        // "back on" is never said
        Assert.NotNull(store.Find(serial));                         // the debt is kept
        Assert.Equal(ons, ProcessorCommands(f).Count(c => c == OnCommand)); // and a refusal is not hammered at

        // The next connection's first reading settles it: still off, so it is put back and said.
        f.Vendor.Status("transmit speech_processor_enable=0");
        f.Rig.ProcessorHandoff.Connected();
        Poll(f, CompressorOff, 3, answering: answered);
        Assert.Null(store.Find(serial));
        Assert.Equal(ProcessorHandoffNotice.PutBackAtConnect, said.Last());
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);
    }

    [Fact]
    public void A_command_link_that_drops_before_the_app_knows_writes_nothing_says_nothing_and_keeps_the_record()
    {
        using var f = HandoffRig(out var said, out _);
        string serial = f.Vendor.Radio.Serial;
        var store = f.Rig.ProcessorHandoffStoreOverride!;
        var answered = new HashSet<int>();

        Poll(f, CompressorOn, 2, answering: answered);
        Assert.Single(said);                                        // positive control: the off was taken and said
        Assert.NotNull(store.Find(serial));

        f.Rig.ProcessorHandoff.Tick(CompressorOff);                 // the put-back is asked for...
        f.Vendor.Transport.Drop();                                  // ...and the command link drops before the queue runs
        Assert.False(f.Vendor.Transport.IsConnected);               // positive control: FlexLib will send nothing
        Assert.True(f.Rig.IsConnected);                             // positive control: the app has not heard yet

        int written;
        lock (f.Vendor.Transport.Written) written = f.Vendor.Transport.Written.Count;
        Poll(f, CompressorOff, 12);
        lock (f.Vendor.Transport.Written) Assert.Equal(written, f.Vendor.Transport.Written.Count); // nothing reached the wire
        Assert.Single(said);                                        // "back on" is not said
        Assert.NotNull(store.Find(serial));                         // the only record of the debt is kept
        Assert.False(f.Vendor.Radio.SpeechProcessorEnable);         // and FlexLib's reading was not made to say what the radio was never told
    }

    [Fact]
    public void The_operators_own_turn_on_wins_before_any_poll_has_seen_the_apps_off()
    {
        using var f = HandoffRig(out var said, out var receipts);
        string serial = f.Vendor.Radio.Serial;
        var store = f.Rig.ProcessorHandoffStoreOverride!;
        var answered = new HashSet<int>();

        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(f.Rig);                                               // the app's off has gone out; no poll has run since
        Assert.False(f.Vendor.Radio.SpeechProcessorEnable);         // positive control

        f.Rig.ProcessorOn = FlexBase.OffOnValues.on;                // the operator turns the processor on, through the rig's own setter
        Drain(f.Rig);
        f.Vendor.Status("transmit speech_processor_enable=1");
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);          // positive control
        Assert.Contains("ProcessorOn", receipts);                   // positive control: it was the operator's change

        Answer(f, answered);                                        // the radio's late answers, the one to the app's off among them
        Poll(f, CompressorOn, 7, answering: answered);
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);          // still on
        Assert.Equal(OnCommand, ProcessorCommands(f).Last());       // and nothing was sent after the operator's own command
        Assert.Null(store.Find(serial));                            // it is theirs now: nothing for the app to put back
        Assert.Empty(said);
    }

    [Fact]
    public void The_operators_own_change_while_the_apps_off_is_still_in_the_queue_withdraws_it()
    {
        using var f = HandoffRig(out var said, out var receipts);
        string serial = f.Vendor.Radio.Serial;
        var answered = new HashSet<int>();

        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Assert.True(f.Rig.q.Count > 0);                             // positive control: the app's off is in the queue
        f.Rig.ProcessorOn = FlexBase.OffOnValues.on;                // the operator says on (it already reads on, so FlexLib sends nothing)
        Assert.Contains("ProcessorOn", receipts);
        Poll(f, CompressorOn, 7, answering: answered);
        Assert.DoesNotContain(OffCommand, ProcessorCommands(f));    // the app's off never went out
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);
        Assert.Null(f.Rig.ProcessorHandoffStoreOverride!.Find(serial));
        Assert.Empty(said);
    }

    [Fact]
    public void Another_stations_turn_on_after_the_radio_took_the_apps_off_is_respected_with_no_operator_change_at_all()
    {
        using var f = HandoffRig(out var said, out var receipts);
        string serial = f.Vendor.Radio.Serial;
        var answered = new HashSet<int>();

        Poll(f, CompressorOn, 2, answering: answered);
        Assert.Single(said);                                        // positive control: the off was taken and said
        int offs = ProcessorCommands(f).Count(c => c == OffCommand);

        f.Vendor.Status("transmit speech_processor_enable=1");      // the radio reports it on again: another station's doing
        Poll(f, CompressorOn, 7, answering: answered);
        Assert.Empty(receipts);                                     // no operator's change was involved
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);          // left on
        Assert.Equal(offs, ProcessorCommands(f).Count(c => c == OffCommand)); // never fought
        Assert.Null(f.Rig.ProcessorHandoffStoreOverride!.Find(serial));
        Assert.Single(said);
    }

    [Fact]
    public void A_reading_left_unsettled_settles_as_held_when_the_radio_next_reports_off_and_a_later_on_is_somebodys()
    {
        using var f = HandoffRig(out var said, out _);
        string serial = f.Vendor.Radio.Serial;
        var store = f.Rig.ProcessorHandoffStoreOverride!;
        var answered = new HashSet<int>();

        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(f.Rig);
        f.Vendor.Status("transmit speech_processor_enable=1 speech_processor_level=1"); // a report parsed between the off and its answer
        Assert.True(Answer(f, answered) > 0);
        Poll(f, CompressorOn, 3, answering: answered);
        Assert.Single(said);
        Assert.NotNull(store.Find(serial));                         // unsettled: kept

        f.Vendor.Status("transmit speech_processor_enable=0");      // the radio reports off: settled, held
        Poll(f, CompressorOn, 2, answering: answered);
        Assert.NotNull(store.Find(serial));
        int offs = ProcessorCommands(f).Count(c => c == OffCommand);

        f.Vendor.Status("transmit speech_processor_enable=1");      // and an "on" after that is somebody's turn-on, as ever
        Poll(f, CompressorOn, 5, answering: answered);
        Assert.Null(store.Find(serial));
        Assert.Equal(offs, ProcessorCommands(f).Count(c => c == OffCommand)); // never fought
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);
        Assert.DoesNotContain(OnCommand, ProcessorCommands(f));     // and nothing of the app's to put back
    }

    [Fact]
    public void The_Flex_side_tells_an_answer_that_finds_another_value_in_the_reading_from_one_that_agrees()
    {
        using var f = ConnectedRig("handoff-unsettled");
        var processor = f.Rig.SpeechProcessorForHandoff;
        var steps = new List<ProcessorWriteStep>();
        Assert.True(processor.WriteProcessorOn(false, () => true, steps.Add));
        Drain(f.Rig);
        f.Vendor.Status("transmit speech_processor_enable=1");
        int written;
        lock (f.Vendor.Transport.Written) written = f.Vendor.Transport.Written.Count;
        f.Vendor.ReplyOk(f.Vendor.Transport.SequenceOf(0));
        Assert.Equal(new[] { ProcessorWriteStep.Sent, ProcessorWriteStep.AcceptedUnsettled }, steps);
        lock (f.Vendor.Transport.Written) Assert.Equal(written, f.Vendor.Transport.Written.Count);

        // The positive control: with the reading agreeing, the plain answer.
        steps.Clear();
        Assert.True(processor.WriteProcessorOn(false, () => true, steps.Add));
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SpeechProcessorEnable);
        f.Vendor.ReplyOk(f.Vendor.Transport.SequenceOf(f.Vendor.Transport.Written.Count - 2)); // the command with the handler goes first; FlexLib's own copy follows it
        Assert.Equal(new[] { ProcessorWriteStep.Sent, ProcessorWriteStep.Accepted }, steps);
    }

    [Fact]
    public void While_the_reading_is_unsettled_the_operators_own_on_reaches_the_radio_through_the_apps_put_back()
    {
        // FlexLib sends nothing for a value its reading already shows. If that "on" was an older
        // report, the radio is off, and the operator's own "on" would be swallowed.
        using var f = HandoffRig(out var said, out var receipts);
        string serial = f.Vendor.Radio.Serial;
        var store = f.Rig.ProcessorHandoffStoreOverride!;
        var answered = new HashSet<int>();
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(f.Rig);
        f.Vendor.Status("transmit speech_processor_enable=1");
        Answer(f, answered);
        Poll(f, CompressorOn, 2, answering: answered);
        Assert.Single(said);

        f.Rig.ProcessorOn = FlexBase.OffOnValues.on;                // the operator says on; the reading already says on
        Drain(f.Rig);
        Assert.Contains("ProcessorOn", receipts);
        Assert.DoesNotContain(OnCommand, ProcessorCommands(f));     // positive control: FlexLib itself sent nothing for it
        Assert.NotNull(store.Find(serial));                         // so the app's record is not dropped yet

        Poll(f, CompressorOn, 4, answering: answered);
        Assert.Equal(1, ProcessorCommands(f).Count(c => c == OnCommand)); // the app's put-back carried it to the radio
        Assert.Null(store.Find(serial));
        Assert.Single(said);                                        // nothing more is said: they did it themselves
        int offs = ProcessorCommands(f).Count(c => c == OffCommand);
        Poll(f, CompressorOn, 20, answering: answered);
        Assert.Equal(offs, ProcessorCommands(f).Count(c => c == OffCommand)); // and it is theirs for this session
    }

    [Fact]
    public void While_the_reading_is_unsettled_the_operators_own_off_is_theirs_and_nothing_is_put_back()
    {
        using var f = HandoffRig(out var said, out _);
        string serial = f.Vendor.Radio.Serial;
        var answered = new HashSet<int>();
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(f.Rig);
        f.Vendor.Status("transmit speech_processor_enable=1");
        Answer(f, answered);
        Poll(f, CompressorOn, 2, answering: answered);
        int offs = ProcessorCommands(f).Count(c => c == OffCommand);

        f.Rig.ProcessorOn = FlexBase.OffOnValues.off;               // the operator says off; the reading says on, so FlexLib sends it
        Drain(f.Rig);
        Assert.Equal(offs + 1, ProcessorCommands(f).Count(c => c == OffCommand));
        Assert.Null(f.Rig.ProcessorHandoffStoreOverride!.Find(serial));
        Poll(f, CompressorOff, 5, answering: answered);
        Assert.DoesNotContain(OnCommand, ProcessorCommands(f));     // their off stands
        Assert.Single(said);
    }

    // ── repair 3: nothing reaches the wire but behind the write's own gates; the leave reads its news; a preset holds the operator's value ──

    [Fact]
    public void The_radios_answer_sends_nothing_itself_keyed_or_not_whatever_the_reading_says()
    {
        // Codex's re-check 2: the answer's handler set FlexLib's reading right through FlexLib's
        // setter, which SENDS. With the radio transmitting, a third OFF went out from the reader's thread.
        using var f = HandoffRig(out _, out _);
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(f.Rig);
        Assert.Equal(new[] { OffCommand, OffCommand }, ProcessorCommands(f));   // the write itself, unkeyed
        f.Vendor.Status("interlock state=TRANSMITTING source=RCA");
        Assert.True(f.Rig.SpeechProcessorForHandoff.Transmitting, "the fixture's radio does not read as transmitting: the test proves nothing");
        f.Vendor.Status("transmit speech_processor_enable=1");                    // a report that disagrees with what was written
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);                        // positive control: the reading disagrees

        // Positive control: an ordinary write in this very state makes nothing.
        var steps = new List<ProcessorWriteStep>();
        f.Rig.SpeechProcessorForHandoff.WriteProcessorOn(false, () => true, steps.Add);
        Drain(f.Rig);
        Assert.Equal(new[] { ProcessorWriteStep.NotMade }, steps);
        int written;
        lock (f.Vendor.Transport.Written) written = f.Vendor.Transport.Written.Count;

        f.Vendor.ReplyOk(f.Vendor.Transport.SequenceOf(0));                       // the radio's answer to the app's off
        lock (f.Vendor.Transport.Written) Assert.Equal(written, f.Vendor.Transport.Written.Count); // nothing went out on it
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);                        // and the reading was not written over
    }

    [Fact]
    public void FlexLibs_setter_is_called_from_the_write_alone_behind_the_writes_own_gates()
    {
        // FlexLib's setter is a command whenever its cache differs. The Flex side may call it from
        // one place: the write itself, after it has asked connected, not transmitting, still wanted.
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.ProcessorHandoff.cs"));
        Assert.Equal(1, source.Split("radio.SpeechProcessorEnable = on;").Length - 1);
        Assert.Equal(1, source.Split("SetReading(radio!, on);").Length - 1);
        Assert.Equal(2, source.Split("SetReading(").Length - 1);    // the one call and the definition
        string write = Between(source, "private bool WriteIfStillRight(", "private void SetReading(");
        Assert.True(write.IndexOf(": Transmitting ? \"the radio is transmitting\"", StringComparison.Ordinal) >= 0
            && write.IndexOf(": Transmitting ? \"the radio is transmitting\"", StringComparison.Ordinal) < write.IndexOf("SetReading(radio!, on);", StringComparison.Ordinal));
        // And the answer's handler sends and sets nothing.
        string answered = Between(source, "private void Answered(", "private ProcessorHandoff? _processorHandoff;");
        Assert.DoesNotContain("SetReading", answered);
        Assert.DoesNotContain("SendReplyCommand", answered);
        Assert.DoesNotContain("SpeechProcessorEnable =", answered);
    }

    [Fact]
    public void A_reading_that_disagrees_when_the_radio_answers_is_neither_written_over_nor_taken_for_somebodys_turn_on()
    {
        // It may be a report older than our command or somebody's newer change: the answer does
        // not say which. So nothing is sent to find out, the record is kept, and the processor
        // is neither turned off again nor forgotten: the put-back at the session's end settles
        // it, and an "on" sent to a radio that is already on changes nothing.
        using var f = HandoffRig(out var said, out _);
        string serial = f.Vendor.Radio.Serial;
        var store = f.Rig.ProcessorHandoffStoreOverride!;
        var answered = new HashSet<int>();

        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(f.Rig);
        f.Vendor.Status("transmit speech_processor_enable=1 speech_processor_level=1");
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);          // positive control: the reading says on
        int before = ProcessorCommands(f).Count;
        Assert.True(Answer(f, answered) > 0);                       // the radio took the off
        Assert.Equal(before, ProcessorCommands(f).Count);           // nothing was sent on the answer
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);          // the reading is left as the radio's reports made it

        Poll(f, CompressorOn, 20, answering: answered);
        Assert.Single(said);                                        // the radio took the off: said, once
        Assert.NotNull(store.Find(serial));                         // the record is NOT dropped: the original hazard (the processor off, and nothing left to put it back)
        Assert.Equal(before, ProcessorCommands(f).Count);           // and nothing is fought

        // The session ends: the put-back goes out although the reading already says on.
        Poll(f, CompressorOff, 3, answering: answered);
        Assert.Equal(1, ProcessorCommands(f).Count(c => c == OnCommand)); // once, with its reply handler; FlexLib's setter has nothing to send
        Assert.Null(store.Find(serial));
        Assert.Equal(2, said.Count);
        Assert.Contains(said[1], new[] { ProcessorHandoffNotice.PutBack, ProcessorHandoffNotice.PutBackShared });
    }

    [Fact]
    public void A_leave_straight_after_the_radio_took_the_off_and_before_any_tick_still_puts_the_processor_back()
    {
        // Codex's re-check 2: the news of the write (sent, accepted) was still waiting to be read
        // when the leave came, so the leave took the off for never sent and wrote nothing.
        using var f = HandoffRig(out _, out _);
        string serial = f.Vendor.Radio.Serial;
        f.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(f.Rig);
        Answer(f, new HashSet<int>());
        Assert.False(f.Vendor.Radio.SpeechProcessorEnable);         // positive control: the off went out
        f.Vendor.Transport.Written.Clear();

        f.Rig.ProcessorHandoff.Leaving();                           // no tick in between
        Assert.Equal(new[] { OnCommand, OnCommand }, f.Vendor.Transport.Commands);
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);
        Assert.NotNull(f.Rig.ProcessorHandoffStoreOverride!.Find(serial)); // kept until the next connect confirms it

        // The same with the write sent and not yet answered.
        using var g = HandoffRig(out _, out _, "5432-8765-2109-6543");
        g.Rig.ProcessorHandoff.Tick(CompressorOn);
        Drain(g.Rig);
        g.Vendor.Transport.Written.Clear();
        g.Rig.ProcessorHandoff.Leaving();
        Assert.Equal(new[] { OnCommand, OnCommand }, g.Vendor.Transport.Commands);
    }

    [Fact]
    public void A_preset_saved_while_the_app_holds_the_processor_off_keeps_the_operators_own_value_and_applying_it_changes_nothing()
    {
        // #1302: the capture read the app's temporary off as the operator's choice, and applying
        // that preset then dropped the app's record, so the processor stayed off for good.
        using var f = HandoffRig(out var said, out _);
        string serial = f.Vendor.Radio.Serial;
        var store = f.Rig.ProcessorHandoffStoreOverride!;
        var answered = new HashSet<int>();
        Poll(f, CompressorOn, 2, answering: answered);
        Assert.Single(said);
        Assert.Equal(FlexBase.OffOnValues.off, f.Rig.ProcessorOn);  // positive control: the radio reads off, by the app's hand
        Assert.NotNull(store.Find(serial));

        var preset = AudioChainPreset.CaptureFrom(f.Rig, "saved while the compressor is on");
        Assert.True(preset.SpeechProcessorOn, "the preset recorded the app's temporary off as the operator's choice");

        int before = ProcessorCommands(f).Count;
        preset.ApplyTo(f.Rig);
        Drain(f.Rig);
        Assert.Equal(before, ProcessorCommands(f).Count);           // the same state, applied: nothing about the processor is sent
        Assert.NotNull(store.Find(serial));                         // and the app still knows what it owes

        Poll(f, CompressorOff, 4, answering: answered);             // the compressor goes off
        Assert.True(f.Vendor.Radio.SpeechProcessorEnable);          // the operator's processor is back
        Assert.Null(store.Find(serial));
        Assert.Contains(said.Last(), new[] { ProcessorHandoffNotice.PutBack, ProcessorHandoffNotice.PutBackShared });
    }

    [Fact]
    public void A_preset_that_really_says_processor_off_wins_and_the_app_does_not_put_it_back_on()
    {
        // The other half of #1302's judgement: a set through the rig's setter to the value the app
        // already holds is still somebody's choice. With the capture telling the truth, a preset
        // that says off was saved with the processor off by the operator's own hand.
        using var f = HandoffRig(out var said, out _);
        string serial = f.Vendor.Radio.Serial;
        var store = f.Rig.ProcessorHandoffStoreOverride!;
        var answered = new HashSet<int>();
        Poll(f, CompressorOn, 2, answering: answered);
        Assert.NotNull(store.Find(serial));

        var preset = AudioChainPreset.CaptureFrom(f.Rig, "the operator's quiet setup");
        preset.SpeechProcessorOn = false;                           // saved another day, with the processor off
        preset.ApplyTo(f.Rig);
        Drain(f.Rig);
        Assert.Null(store.Find(serial));                            // theirs now

        Poll(f, CompressorOff, 6, answering: answered);
        Assert.False(f.Vendor.Radio.SpeechProcessorEnable);         // their off stands
        Assert.DoesNotContain(OnCommand, ProcessorCommands(f));
        Assert.Single(said);
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

        var steps = new List<ProcessorWriteStep>();
        Assert.True(processor.WriteProcessorOn(false, () => true, steps.Add));
        Assert.Empty(f.Vendor.Transport.Commands); // asked: nothing has gone out until the queue runs
        Assert.Empty(steps);
        Drain(f.Rig);
        Assert.False(f.Vendor.Radio.SpeechProcessorEnable);
        // The command goes out twice: first with a reply handler (the radio's answer is the only
        // confirmation), then from FlexLib's own setter (its cache and its property event).
        Assert.Equal(new[] { OffCommand, OffCommand }, f.Vendor.Transport.Commands);
        Assert.Equal(new[] { ProcessorWriteStep.Sent }, steps); // sent is not accepted
        Assert.Empty(operatorChanges); // the receipt and the save offer are NOT armed

        // FlexLib's own copy carries no handler: an answer to it is nobody's.
        f.Vendor.ReplyOk(f.Vendor.Transport.SequenceOf(1));
        Assert.Single(steps);
        // The radio's answer to the first is the confirmation.
        f.Vendor.ReplyOk(f.Vendor.Transport.SequenceOf(0));
        Assert.Equal(new[] { ProcessorWriteStep.Sent, ProcessorWriteStep.Accepted }, steps);

        // An asking again reaches the wire although FlexLib's cache already holds the value, and a refusal is told.
        f.Vendor.Transport.Written.Clear();
        steps.Clear();
        Assert.True(processor.WriteProcessorOn(false, () => true, steps.Add));
        Drain(f.Rig);
        Assert.Equal(new[] { OffCommand }, f.Vendor.Transport.Commands);
        f.Vendor.Reply(f.Vendor.Transport.SequenceOf(0), RefusedCode, "");
        Assert.Equal(new[] { ProcessorWriteStep.Sent, ProcessorWriteStep.Refused }, steps);

        // A write no longer wanted when the queue reaches it is not made, and says so.
        f.Vendor.Transport.Written.Clear();
        steps.Clear();
        Assert.True(processor.WriteProcessorOn(true, () => false, steps.Add));
        Drain(f.Rig);
        Assert.Empty(f.Vendor.Transport.Commands);
        Assert.Equal(new[] { ProcessorWriteStep.NotMade }, steps);
        Assert.False(f.Vendor.Radio.SpeechProcessorEnable);
        Assert.Empty(operatorChanges);

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
        f.Rig.ProcessorHandoff.Tick(new ProcessorHandoffInputs(true, true, true, true, 0));
        Assert.Empty(said); // sent and read back, and still not said: the radio has not answered
        Assert.True(Answer(f, new HashSet<int>()) > 0);
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
