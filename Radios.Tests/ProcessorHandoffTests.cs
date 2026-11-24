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
        public List<bool> Writes { get; } = new();
        public bool WriteProcessorOn(bool on)
        {
            if (RefuseWrites) return false;
            Writes.Add(on);
            On = on;
            return true;
        }
    }

    private sealed class Bench
    {
        public readonly FakeProcessor Radio = new();
        public readonly List<ProcessorHandoffNotice> Said = new();
        public readonly ProcessorHandoffStore Store;
        public ProcessorHandoff Handoff;
        public bool Setting = true, Compressor, PcMic = true, Voice = true;
        public long Faults;

        public Bench(string root)
        {
            Store = new ProcessorHandoffStore(root);
            Handoff = Make();
        }

        public ProcessorHandoff Make() => new(Radio, Store, Said.Add, _ => { });

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

        /// <summary>Tick past the settle after a write, so the next tick judges the radio again.</summary>
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
        b.Tick();
        Assert.Equal(new[] { false }, b.Radio.Writes);
        Assert.Equal(new[] { ProcessorHandoffNotice.TurnedOffShared }, b.Said);
        b.Settle();
        b.Compressor = false;
        b.Tick();
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
    public void The_radios_own_microphone_ends_the_session_and_the_processor_comes_back()
    {
        var b = NewBench();
        b.Compressor = true;
        b.Tick();
        b.Settle();
        b.PcMic = false; // the microphone becomes the radio's own
        b.Tick();
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
        b.Tick();
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

        b.Reconnect();
        b.Tick();
        // The processor is on (the put-back landed): the record is dropped, and
        // since our compressor is on this is a session's start like any other.
        Assert.Equal(new[] { false, true, false }, b.Radio.Writes);
        Assert.Equal(ProcessorHandoffNotice.TurnedOff, b.Said[^1]);
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
        b.Tick();
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
        b.Tick();
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

        Assert.True(processor.WriteProcessorOn(false));
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
