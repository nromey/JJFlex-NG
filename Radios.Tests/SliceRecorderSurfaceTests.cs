using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// The radio's own per-slice recorder, as ONE surface behind every door
/// (#787, Tape Deck track 2). What these hold that round 8's tests did not:
/// each slice has its own recorder, a slice that is not the active one can be
/// recorded, record and play on one slice exclude each other, a recording
/// started anywhere can be stopped, keying stops playback, the two-minute
/// warning belongs to the surface, and a door stops only what it started.
/// </summary>
/// <remarks>
/// What these cannot see: the radio itself. The vendor fixture echoes each
/// command as status and replies OK; whether a 6300 really keeps two slices'
/// recordings apart, or records two slices at once, is a bench question and is
/// in the track report's bench steps.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class SliceRecorderSurfaceTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(SliceRecorderSurfaceTests));
    public void Dispose() => _scope.Dispose();

    private static string Text(string key, string letter) => Lexicon.Get(key, ("letter", letter));

    [Fact]
    public async Task A_slice_you_are_not_listening_to_can_be_recorded_by_its_letter()
    {
        using var c = new RadioCase();
        c.Add(0, 7.1); // slice A, not active; the active slice is B
        var outcome = await c.Pump(c.F.Rig.ToggleSliceRecordAsync(SliceRecorderTarget.Letter('A')));
        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(Text("audio.recorder.recording", "A"), outcome.Message);
        Assert.Contains("slice set 0 record=1", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain("slice set 1 record=1", c.F.Vendor.Transport.Commands);
        Assert.True(c.F.Rig.IsSliceRecording(SliceRecorderTarget.Letter('A')));
        Assert.False(c.F.Rig.IsSliceRecording(SliceRecorderTarget.ActiveSlice));
        Assert.Equal(Speech.SpeechSubject.SliceRecorder("A"), outcome.Subject);
    }

    [Fact]
    public async Task Recording_one_slice_leaves_another_slice_playing()
    {
        using var c = new RadioCase();
        c.Add(0, 7.1);
        c.F.Vendor.Status("slice 0 play=0"); // slice A holds a recording
        Assert.True((await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.Letter('A')))).Success);
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.Contains("slice set 1 record=1", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain("slice set 0 play=0", c.F.Vendor.Transport.Commands);
        Assert.True(c.F.Rig.IsSlicePlaying(SliceRecorderTarget.Letter('A')));
    }

    [Fact]
    public async Task Play_on_a_recording_slice_stops_the_recording_first_and_then_plays_it()
    {
        using var c = new RadioCase();
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            c.Respond(command);
            // The radio reports the buffer playable once a recording stops.
            if (command == "slice set 1 record=0") c.F.Vendor.Status("slice 1 play=0");
        };
        c.F.Vendor.Status("slice 1 play=disabled"); // empty until the recording stops
        Assert.True((await c.Pump(c.F.Rig.ToggleSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        var played = await c.Pump(c.F.Rig.ToggleSlicePlayAsync(SliceRecorderTarget.ActiveSlice));
        Assert.True(played.Success, played.Message);
        Assert.Equal(Text("audio.recorder.playing", "B"), played.Message);
        var commands = c.F.Vendor.Transport.Commands.ToList();
        int stop = commands.IndexOf("slice set 1 record=0");
        int play = commands.IndexOf("slice set 1 play=1");
        Assert.True(stop >= 0 && play > stop, string.Join(" | ", commands));
        Assert.False(c.F.Rig.IsSliceRecording(SliceRecorderTarget.ActiveSlice));
    }

    [Fact]
    public async Task Record_on_a_playing_slice_stops_the_playback_first()
    {
        using var c = new RadioCase();
        c.F.Vendor.Status("slice 1 play=0");
        Assert.True((await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.True((await c.Pump(c.F.Rig.ToggleSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        var commands = c.F.Vendor.Transport.Commands.ToList();
        int stop = commands.IndexOf("slice set 1 play=0");
        int record = commands.IndexOf("slice set 1 record=1");
        Assert.True(stop >= 0 && record > stop, string.Join(" | ", commands));
        Assert.False(c.F.Rig.IsSlicePlaying(SliceRecorderTarget.ActiveSlice));
    }

    [Fact]
    public async Task A_recording_started_somewhere_else_can_still_be_stopped()
    {
        using var c = new RadioCase();
        c.F.Vendor.Status("slice 1 record=1"); // the front panel, an earlier session, a crash
        Assert.True(c.F.Rig.IsSliceRecording(SliceRecorderTarget.ActiveSlice));
        var outcome = await c.Pump(c.F.Rig.ToggleSliceRecordAsync(SliceRecorderTarget.ActiveSlice));
        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(Text("audio.recorder.recording_stopped", "B"), outcome.Message);
        Assert.Contains("slice set 1 record=0", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain("slice set 1 record=1", c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task Starting_a_recording_that_is_already_running_does_not_rearm_it()
    {
        using var c = new RadioCase();
        c.F.Vendor.Status("slice 1 record=1");
        var outcome = await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice));
        Assert.True(outcome.Success);
        Assert.True(outcome.AlreadyInState);
        Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.StartsWith("slice set 1 record=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Local_play_stops_on_a_transmit_edge()
    {
        using var c = new RadioCase();
        c.F.Vendor.Status("slice 1 play=0");
        Assert.True((await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.StartsWith("xmit", StringComparison.Ordinal));
        var handler = (Delegate?)typeof(FlexBase).GetField("TransmitChange", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.F.Rig);
        Assert.NotNull(handler);
        handler!.DynamicInvoke(c.F.Rig, true);
        await c.Pump(WaitUntil(() => c.F.Vendor.Transport.Commands.Contains("slice set 1 play=0")));
        Assert.Contains("slice set 1 play=0", c.F.Vendor.Transport.Commands);
        Assert.False(c.F.Rig.IsSlicePlaying(SliceRecorderTarget.ActiveSlice));
    }

    private static void Key(RadioCase c, bool keyed)
    {
        Field(c.F.Vendor.Radio, "_mox", keyed);
        typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(c.F.Rig, new object[] { keyed });
    }

    private static void VoxRadio(RadioCase c)
    {
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            c.Respond(command);
            if (command.EndsWith(" play=1") && c.F.Vendor.Radio.SimpleVOXEnable) Key(c, true);
        };
    }

    private static void OwnTransmission(RadioCase c)
    {
        c.F.Rig.CanTransmit = true;
        c.F.Rig.SlicePlaybackTransmitAllowed = () => true;
        Property(c.F.Vendor.Radio, "TXClientHandle", c.F.Vendor.Radio.ClientHandle);
        c.F.Vendor.Status("slice 1 play=0");
        Key(c, true);
    }

    [Fact]
    public async Task Cw_break_in_is_paused_for_local_play_and_restored_after_stop()
    {
        using var c = new RadioCase();
        c.F.Vendor.Status("slice 1 mode=CW play=0");
        Field(c.F.Vendor.Radio, "_cwBreakIn", true);
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            c.Respond(command);
            if (command == "cw break_in 0") c.F.Vendor.Status("transmit break_in=0");
        };
        Assert.True((await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.False(c.F.Vendor.Radio.CWBreakIn);
        Assert.True((await c.Pump(c.F.Rig.StopSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        await c.Pump(WaitUntil(() => c.F.Vendor.Radio.CWBreakIn));
        Assert.True(c.F.Vendor.Radio.CWBreakIn);
    }

    [Theory]
    [InlineData("safety")]
    [InlineData("owner")]
    [InlineData("unkey")]
    public async Task An_on_air_play_cannot_survive_a_stop_or_resume_in_the_next_transmission(string reason)
    {
        using var c = new RadioCase();
        OwnTransmission(c);
        Assert.True((await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        if (reason == "safety") TransmitKillSwitch.DisarmSources(c.F.Rig);
        else if (reason == "owner") Property(c.F.Vendor.Radio, "TXClientHandle", (uint)123);
        else Key(c, false);
        Assert.True(await c.Pump(WaitUntil(() => c.F.Vendor.Transport.Commands.Contains("slice set 1 play=0"))));
        Key(c, false);
        Key(c, true);
        Assert.False(c.F.Rig.IsSlicePlaying(SliceRecorderTarget.ActiveSlice));
        Assert.Single(c.F.Vendor.Transport.Commands.Where(x => x == "slice set 1 play=1"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_queued_play_keeps_its_press_intent_across_unkey_and_rekey(bool initiallyKeyed)
    {
        using var c = new RadioCase();
        if (initiallyKeyed) OwnTransmission(c);
        else c.F.Vendor.Status("slice 1 play=0");
        var request = c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice);
        Key(c, !initiallyKeyed);
        Key(c, initiallyKeyed);
        Assert.False((await c.Pump(request)).Success);
        Assert.DoesNotContain("slice set 1 play=1", c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task Losing_the_transmit_slice_stops_playback_on_a_different_slice()
    {
        using var c = new RadioCase();
        c.Add(0, 7.1);
        c.F.Vendor.Status("slice 0 play=0");
        OwnTransmission(c); // B transmits; A holds the recording.
        Assert.True((await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.Letter('A')))).Success);
        c.F.Vendor.Status("slice 1 in_use=0");
        Assert.True(await c.Pump(WaitUntil(() => c.F.Vendor.Transport.Commands.Contains("slice set 0 play=0"))));
        Assert.False(c.F.Rig.IsSlicePlaying(SliceRecorderTarget.Letter('A')));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("gate")]
    [InlineData("vox")]
    public async Task Dispatch_rechecks_admission_after_the_press(string changed)
    {
        using var c = new RadioCase();
        if (changed != "vox") OwnTransmission(c);
        else c.F.Vendor.Status("slice 1 play=0");
        var request = c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice);
        if (changed == "owner") Property(c.F.Vendor.Radio, "TXClientHandle", (uint)123);
        else if (changed == "gate") c.F.Rig.SlicePlaybackTransmitAllowed = () => false;
        else Field(c.F.Vendor.Radio, "_simpleVOXEnable", true);
        Assert.False((await c.Pump(request)).Success);
        Assert.DoesNotContain("slice set 1 play=1", c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task Lengths_are_per_slice_replace_the_previous_take_and_leave_old_takes_unknown()
    {
        using var c = new RadioCase();
        var a = c.Add(0, 7.1);
        c.F.Vendor.Status("slice 0 play=0 audio_mute=1");
        c.F.Vendor.Status("slice 1 play=0");
        double now = 100;
        c.F.Rig.SliceRecorderClock = () => now;
        Assert.Null(c.F.Rig.SliceRecordingSeconds(SliceRecorderTarget.ActiveSlice));
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.Letter('A')))).Success);
        now += 12;
        Assert.True((await c.Pump(c.F.Rig.StopSliceRecordAsync(SliceRecorderTarget.Letter('A')))).Success);
        Assert.Equal(12, c.F.Rig.SliceRecordingSeconds(SliceRecorderTarget.Letter('A')));
        Assert.Null(c.F.Rig.SliceRecordingSeconds(SliceRecorderTarget.ActiveSlice));
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.Letter('A')))).Success);
        now += 3;
        Assert.True((await c.Pump(c.F.Rig.StopSliceRecordAsync(SliceRecorderTarget.Letter('A')))).Success);
        Assert.Equal(3, c.F.Rig.SliceRecordingSeconds(SliceRecorderTarget.Letter('A')));
    }

    [Fact]
    public void Raw_radio_record_time_survives_a_late_slice_state_creation_and_updates()
    {
        using var c = new RadioCase();
        c.F.Rig.AttachSliceRecorderStatus(c.F.Vendor.Radio);
        c.F.Vendor.Status("slice 1 play=0");
        c.F.Rig.ObserveSliceRecorderStatus(c.F.Vendor.Radio, "S1|slice 1 record_time=12.5 play=0");
        Assert.Equal(12.5, c.F.Rig.SliceRecordingSeconds(SliceRecorderTarget.ActiveSlice));
        c.F.Rig.ObserveSliceRecorderStatus(c.F.Vendor.Radio, "S2|slice 1 record_time=120.0 play=0");
        Assert.Equal(120, c.F.Rig.SliceRecordingSeconds(SliceRecorderTarget.ActiveSlice));
        c.F.Rig.ObserveSliceRecorderStatus(c.F.Vendor.Radio, "S3|slice 10 record_time=3.0 play=0");
        Assert.Equal(120, c.F.Rig.SliceRecordingSeconds(SliceRecorderTarget.ActiveSlice));
    }

    [Fact]
    public void Recorder_attach_does_not_enable_the_telnet_trace_that_breaks_space_less_commands()
    {
        using var c = new RadioCase();
        int before = c.F.Vendor.Radio.SelectedTelnetTraceTopics.Count;
        c.F.Rig.AttachSliceRecorderStatus(c.F.Vendor.Radio);
        Assert.Equal(before, c.F.Vendor.Radio.SelectedTelnetTraceTopics.Count);
        Assert.Empty(c.F.Vendor.Radio.TelnetTraceOutput);
    }

    [Fact]
    public async Task Recorder_commands_confirm_without_enabling_telnet_tracing()
    {
        using var c = new RadioCase();
        int before = c.F.Vendor.Radio.SelectedTelnetTraceTopics.Count;
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.Equal(before, c.F.Vendor.Radio.SelectedTelnetTraceTopics.Count);
        Assert.True((await c.Pump(c.F.Rig.StopSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.Equal(before, c.F.Vendor.Radio.SelectedTelnetTraceTopics.Count);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("garbled")]
    public void Invalid_radio_record_time_does_not_invent_an_old_takes_length(string value)
    {
        using var c = new RadioCase();
        c.F.Rig.AttachSliceRecorderStatus(c.F.Vendor.Radio);
        c.F.Vendor.Status("slice 1 play=0");
        Assert.Null(c.F.Rig.SliceRecordingSeconds(SliceRecorderTarget.ActiveSlice));
        c.F.Rig.ObserveSliceRecorderStatus(c.F.Vendor.Radio, "S1|slice 1 record_time=" + value);
        Assert.Null(c.F.Rig.SliceRecordingSeconds(SliceRecorderTarget.ActiveSlice));
    }

    [Fact]
    public async Task Transmit_target_records_A_even_while_B_is_active()
    {
        using var c = new RadioCase();
        c.Add(0, 7.1);
        c.F.Vendor.Status("slice 1 tx=0");
        c.F.Vendor.Status("slice 0 tx=1");
        Assert.Equal("B", c.F.Rig.SliceRecorderLetter(SliceRecorderTarget.ActiveSlice));
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.TransmitSlice))).Success);
        Assert.Contains("slice set 0 record=1", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain("slice set 1 record=1", c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public void The_fake_radio_keys_on_play_with_VOX_enabled_positive_control()
    {
        using var c = new RadioCase();
        Field(c.F.Vendor.Radio, "_simpleVOXEnable", true);
        VoxRadio(c);
        c.F.Vendor.Radio.SendReplyCommand((_, _, _) => { }, "slice set 1 play=1");
        Assert.True(c.F.Rig.Transmit);
    }

    [Fact]
    public async Task Local_play_with_VOX_on_pauses_and_restores_without_keying_the_fake_radio()
    {
        using var c = new RadioCase();
        Field(c.F.Vendor.Radio, "_simpleVOXEnable", true);
        c.F.Vendor.Status("slice 1 play=0");
        VoxRadio(c);
        var result = await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice));
        Assert.True(result.Success, result.Message);
        Assert.False(c.F.Rig.Transmit);
        Assert.False(c.F.Vendor.Radio.SimpleVOXEnable);
        Assert.Contains("slice set 1 play=1", c.F.Vendor.Transport.Commands);
        Assert.True((await c.Pump(c.F.Rig.StopSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        await c.Pump(WaitUntil(() => c.F.Vendor.Radio.SimpleVOXEnable));
        Assert.True(c.F.Vendor.Radio.SimpleVOXEnable);
        Assert.False(c.F.Rig.Transmit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Slice_play_and_cut_hold_restore_vox_only_after_both_release(bool holdReleasesFirst)
    {
        using var c = new RadioCase();
        c.F.Vendor.Status("transmit vox_enable=1");
        c.F.Vendor.Status("slice 1 play=0");
        VoxRadio(c);
        var hold = new ReflectedCutHold(() => 0);
        void BeginHold() => hold.Begin(ReflectedCutNotice.WithoutReading("ANT1"),
            ReflectedCutHold.Snapshot(c.F.Rig), 0,
            ReflectedCutHold.LeversFor(c.F.Rig, null), voxNoteNow: false);
        if (holdReleasesFirst) BeginHold();
        Assert.True((await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        if (!holdReleasesFirst) BeginHold();
        Assert.False(c.F.Vendor.Radio.SimpleVOXEnable);
        if (holdReleasesFirst) hold.Abandon("test release", 0);
        else Assert.True((await c.Pump(c.F.Rig.StopSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.False(c.F.Vendor.Radio.SimpleVOXEnable);
        if (holdReleasesFirst)
            Assert.True((await c.Pump(c.F.Rig.StopSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        else hold.Abandon("test release", 0);
        Assert.True(await c.Pump(WaitUntil(() => c.F.Vendor.Radio.SimpleVOXEnable)));
        Assert.False(c.F.Rig.Transmit);
    }

    [Fact]
    public async Task Local_play_waits_for_a_radio_vox_off_reply_not_the_setters_local_cache()
    {
        using var c = new RadioCase();
        c.F.Vendor.Status("transmit vox_enable=1");
        c.F.Vendor.Status("slice 1 play=0");
        c.F.Rig.SliceVoxOffWait = TimeSpan.FromMilliseconds(100);
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command != "transmit set vox_enable=0") c.Respond(command);
        };
        var result = await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice));
        Assert.False(result.Success);
        Assert.Equal(Text("audio.recorder.play_vox_unconfirmed", "B"), result.Message);
        Assert.DoesNotContain("slice set 1 play=1", c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task Local_play_accepts_a_vox_off_reply_without_a_status_echo()
    {
        using var c = new RadioCase();
        c.F.Vendor.Status("transmit vox_enable=1");
        c.F.Vendor.Status("slice 1 play=0");
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("transmit set vox_enable="))
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            else c.Respond(command);
        };

        var result = await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice));
        Assert.True(result.Success, result.Message);
        Assert.False(c.F.Vendor.Radio.SimpleVOXEnable);
        Assert.Contains("slice set 1 play=1", c.F.Vendor.Transport.Commands);
    }

    [Theory]
    [InlineData("natural")]
    [InlineData("transmit")]
    [InlineData("removed")]
    public async Task A_local_play_releases_its_vox_lease_on_each_external_end(string end)
    {
        using var c = new RadioCase();
        c.F.Vendor.Status("transmit vox_enable=1");
        c.F.Vendor.Status("slice 1 play=0");
        VoxRadio(c);
        Assert.True((await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.False(c.F.Vendor.Radio.SimpleVOXEnable);
        if (end == "natural") c.F.Vendor.Status("slice 1 play=0");
        else if (end == "transmit")
        {
            var handler = (Delegate)typeof(FlexBase).GetField("TransmitChange", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.F.Rig)!;
            handler.DynamicInvoke(c.F.Rig, true);
        }
        else c.F.Vendor.Status("slice 1 in_use=0");
        Assert.True(await c.Pump(WaitUntil(() => c.F.Vendor.Radio.SimpleVOXEnable)));
        Assert.False(c.F.Rig.Transmit);
    }

    [Fact]
    public async Task Natural_play_end_reports_one_stop_and_manual_stop_does_not_duplicate_it()
    {
        using var c = new RadioCase();
        c.F.Vendor.Status("slice 1 play=0");
        var finished = new List<RecorderOutcome>();
        c.F.Rig.SlicePlaybackFinished += finished.Add;
        Assert.True((await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        c.F.Vendor.Status("slice 1 play=0");
        Assert.Single(finished);
        Assert.Equal(RecorderCue.PlayStop, finished[0].Cue);
        Assert.Equal(Text("audio.recorder.playing_stopped", "B"), finished[0].Message);
        Assert.True((await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.True((await c.Pump(c.F.Rig.StopSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.Single(finished);
    }

    [Fact]
    public async Task Audio_check_transmit_slice_replay_pauses_vox_and_never_keys()
    {
        using var c = new RadioCase();
        c.F.Vendor.Status("transmit vox_enable=1");
        c.F.Vendor.Status("slice 1 play=0");
        VoxRadio(c);
        var take = await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.TransmitSlice));
        Assert.True(take.Success, take.Message);
        Assert.False(c.F.Rig.Transmit);
        Assert.False(c.F.Vendor.Radio.SimpleVOXEnable);
        Assert.True((await c.Pump(c.F.Rig.StopSlicePlayAsync(SliceRecorderTarget.TransmitSlice))).Success);
        Assert.True(await c.Pump(WaitUntil(() => c.F.Vendor.Radio.SimpleVOXEnable)));
        Assert.False(c.F.Rig.Transmit);
    }

    [Fact]
    public async Task A_play_pressed_during_our_transmission_continues_until_unkey()
    {
        using var c = new RadioCase();
        Field(c.F.Vendor.Radio, "_simpleVOXEnable", true);
        VoxRadio(c);
        c.F.Rig.CanTransmit = true;
        c.F.Rig.SlicePlaybackTransmitAllowed = () => true;
        Property(c.F.Vendor.Radio, "TXClientHandle", c.F.Vendor.Radio.ClientHandle);
        c.F.Vendor.Status("slice 1 play=0");
        Key(c, true);
        var result = await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice));
        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("slice set 1 play=0", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.StartsWith("xmit") || x.Contains("tx=1"));
        Key(c, false);
        Assert.True(await c.Pump(WaitUntil(() => c.F.Vendor.Transport.Commands.Contains("slice set 1 play=0"))));
        Key(c, true);
        Assert.Single(c.F.Vendor.Transport.Commands.Where(x => x == "slice set 1 play=1"));
    }

    [Fact]
    public async Task Nothing_recorded_and_not_your_slice_are_refused_without_a_command()
    {
        using var c = new RadioCase();
        c.F.Vendor.Status("slice 1 play=disabled"); // how the radio says the buffer is empty
        var nothing = await c.Pump(c.F.Rig.ToggleSlicePlayAsync(SliceRecorderTarget.ActiveSlice));
        Assert.False(nothing.Success);
        Assert.Equal(Text("audio.recorder.nothing_to_play", "B"), nothing.Message);
        var notOurs = await c.Pump(c.F.Rig.ToggleSliceRecordAsync(SliceRecorderTarget.Letter('C')));
        Assert.False(notOurs.Success);
        Assert.Equal(Text("audio.recorder.not_your_slice", "C"), notOurs.Message);
        Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.Contains(" record=") || x.Contains(" play="));
    }

    [Fact]
    public async Task The_two_minute_warning_belongs_to_the_surface_and_a_stop_cancels_it()
    {
        using var c = new RadioCase();
        c.Add(0, 7.1);
        typeof(FlexBase).GetProperty("SliceRecordWarningAfter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, TimeSpan.FromMilliseconds(150));
        var warned = new List<string>();
        c.F.Rig.SliceRecordingNearlyFull += letter => { lock (warned) warned.Add(letter); };

        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.Letter('A')))).Success);
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.True((await c.Pump(c.F.Rig.StopSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        await c.Pump(WaitUntil(() => { lock (warned) return warned.Count > 0; }));
        await Task.Delay(300);
        lock (warned) Assert.Equal(new[] { "A" }, warned);
    }

    [Fact]
    public async Task A_door_stops_only_what_it_started()
    {
        using var c = new RadioCase();
        c.Add(0, 7.1);
        var workshop = new object();
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice, workshop))).Success);
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.Letter('A')))).Success); // from Home
        Assert.True(await c.Pump(c.F.Rig.StopSliceRecorderOwnedByAsync(workshop)));
        Assert.Contains("slice set 1 record=0", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain("slice set 0 record=0", c.F.Vendor.Transport.Commands);
        Assert.True(c.F.Rig.IsSliceRecording(SliceRecorderTarget.Letter('A')));
    }

    [Fact]
    public async Task A_success_reply_without_record_status_does_not_confirm_start()
    {
        using var c = new RadioCase();
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "slice set 1 record=1") c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            else c.Respond(command);
        };
        var result = await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice));
        Assert.False(result.Success);
        Assert.Contains("slice set 1 record=0", c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task Record_stopped_before_its_start_reply_is_not_resurrected()
    {
        using var c = new RadioCase();
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "slice set 1 record=1")
            {
                c.F.Vendor.Status("slice 1 record=1");
                c.F.Vendor.Status("slice 1 record=0");
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            }
            else c.Respond(command);
        };
        var result = await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice));
        Assert.False(result.Success);
        Assert.False(c.F.Rig.IsSliceRecording(SliceRecorderTarget.ActiveSlice));
    }

    [Fact]
    public async Task A_slow_other_slice_cannot_hold_up_the_transmit_playback_stop()
    {
        using var c = new RadioCase();
        c.Add(0, 7.1);
        c.F.Vendor.Status("slice 0 play=0");
        Assert.True((await c.Pump(c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.Letter('A')))).Success);
        int held = 0;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "slice set 1 record=1") held = c.F.Vendor.LastSequence;
            else c.Respond(command);
        };
        var record = c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice);
        Assert.True(await c.Pump(WaitUntil(() => held != 0)));
        var handler = (Delegate)typeof(FlexBase).GetField("TransmitChange", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.F.Rig)!;
        handler.DynamicInvoke(c.F.Rig, true);
        // Deadline shorter than the recorder reply timeout. The held reply is
        // released only AFTER we have observed whether A's stop was sent.
        var until = DateTime.UtcNow.AddMilliseconds(300);
        async Task<bool> Stopped()
        {
            while (DateTime.UtcNow < until && !c.F.Vendor.Transport.Commands.Contains("slice set 0 play=0")) await Task.Delay(5);
            return c.F.Vendor.Transport.Commands.Contains("slice set 0 play=0");
        }
        bool stopped = await c.Pump(Stopped());
        c.F.Vendor.Status("slice 1 record=1");
        c.F.Vendor.ReplyOk(held);
        await c.Pump(record);
        Assert.True(stopped, "A's transmit stop waited for B's held reply");
    }

    [Fact]
    public async Task Removing_a_recording_slice_retires_its_state_subscription_and_warning()
    {
        using var c = new RadioCase();
        typeof(FlexBase).GetProperty("SliceRecordWarningAfter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, TimeSpan.FromMilliseconds(150));
        int warned = 0;
        c.F.Rig.SliceRecordingNearlyFull += _ => System.Threading.Interlocked.Increment(ref warned);
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        var states = (System.Collections.IDictionary)typeof(FlexBase).GetField("_sliceRecorders", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.F.Rig)!;
        var state = states[c.Tx]!;
        c.F.Vendor.Status("slice 1 in_use=0");
        await Task.Delay(300);
        Assert.Equal(0, warned);
        Assert.True(await WaitUntil(() => !states.Keys.Cast<object>().Any()));
        Assert.Empty(states.Keys.Cast<object>());
        Assert.False(c.F.Rig.IsSliceRecording(SliceRecorderTarget.Letter('B')));
        Assert.False(c.F.Rig.IsSlicePlaying(SliceRecorderTarget.Letter('B')));
        Assert.Null(state.GetType().GetField("Status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state));
        Assert.Null(state.GetType().GetField("CapWarning", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state));
    }

    [Fact]
    public async Task Removing_a_slice_while_a_recorder_query_checks_liveness_does_not_deadlock()
    {
        using var c = new RadioCase();
        Assert.True((await c.Pump(c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        var radio = c.F.Vendor.Radio;
        var slices = typeof(Radio).GetField("_slices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(radio)!;
        var recorders = typeof(FlexBase).GetField("_sliceRecorders", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.F.Rig)!;
        var state = ((System.Collections.IDictionary)recorders)[c.Tx]!;
        var live = typeof(FlexBase).GetMethod("StateIsLive", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var removed = typeof(Radio).GetMethod("OnSliceRemoved", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var sliceHeld = new ManualResetEventSlim();
        using var recorderHeld = new ManualResetEventSlim();
        var disconnect = Task.Run(() =>
        {
            lock (slices)
            {
                sliceHeld.Set();
                Assert.True(recorderHeld.Wait(TimeSpan.FromSeconds(2)));
                removed.Invoke(radio, new object[] { c.Tx });
            }
        });
        var query = Task.Run(() =>
        {
            Assert.True(sliceHeld.Wait(TimeSpan.FromSeconds(2)));
            lock (recorders)
            {
                recorderHeld.Set();
                live.Invoke(c.F.Rig, new[] { state });
            }
        });
        await Task.WhenAll(disconnect, query).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("record", true)]
    [InlineData("record", false)]
    [InlineData("play", true)]
    [InlineData("play", false)]
    public async Task Reply_and_status_are_independent_requirements(string property, bool start)
    {
        using var c = new RadioCase();
        c.F.Rig.SliceRecorderConfirmationWait = TimeSpan.FromMilliseconds(250);
        c.F.Vendor.Status("slice 1 play=0");
        if (!start) c.F.Vendor.Status($"slice 1 {property}=1");
        int sequence = 0;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command == $"slice set 1 {property}={(start ? 1 : 0)}")
            { sequence = c.F.Vendor.LastSequence; c.F.Vendor.ReplyOk(sequence); }
            else c.Respond(command);
        };
        var request = property == "record"
            ? (start ? c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice) : c.F.Rig.StopSliceRecordAsync(SliceRecorderTarget.ActiveSlice))
            : (start ? c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice) : c.F.Rig.StopSlicePlayAsync(SliceRecorderTarget.ActiveSlice));
        Assert.True(await c.Pump(WaitUntil(() => sequence != 0)));
        Assert.False(request.IsCompleted); // OK alone cannot complete it
        c.F.Vendor.Status($"slice 1 {property}={(start ? 1 : 0)}");
        Assert.True((await c.Pump(request)).Success);
    }

    [Theory]
    [InlineData("transmit", "play")]
    [InlineData("close", "record")]
    [InlineData("close", "play")]
    [InlineData("rollback", "record")]
    [InlineData("rollback", "play")]
    public async Task Automatic_failed_stops_report_the_specific_failure(string reason, string property)
    {
        using var c = new RadioCase();
        c.F.Rig.SliceRecorderConfirmationWait = TimeSpan.FromMilliseconds(100);
        var heard = new System.Collections.Concurrent.ConcurrentQueue<RecorderOutcome>();
        c.F.Rig.RecorderStopFailureOutput = heard.Enqueue;
        var owner = new object();
        c.F.Vendor.Status("slice 1 play=0");
        if (reason != "rollback")
        {
            var started = property == "record" ? c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice, owner)
                : c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice, owner);
            Assert.True((await c.Pump(started)).Success);
        }
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            // A missing status after an OK is still an unconfirmed stop.
            if (command.Contains(property + "=")) c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            else c.Respond(command);
        };
        if (reason == "transmit")
        {
            var handler = (Delegate)typeof(FlexBase).GetField("TransmitChange", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.F.Rig)!;
            handler.DynamicInvoke(c.F.Rig, true);
            Assert.True(await c.Pump(WaitUntil(() => !heard.IsEmpty)));
        }
        else if (reason == "close") Assert.False(await c.Pump(c.F.Rig.StopSliceRecorderOwnedByAsync(owner)));
        else
        {
            var result = property == "record" ? c.F.Rig.StartSliceRecordAsync(SliceRecorderTarget.ActiveSlice, owner)
                : c.F.Rig.StartSlicePlayAsync(SliceRecorderTarget.ActiveSlice, owner);
            var outcome = await c.Pump(result);
            Assert.False(outcome.Success);
            // This path has a caller awaiting its outcome: return the failed
            // STOP, so a generic start refusal cannot supersede that warning.
            Assert.Equal(Text(property == "record" ? "audio.recorder.record_stop_unconfirmed" : "audio.recorder.play_stop_unconfirmed", "B")
                + (property == "play" ? " VOX remains off until playback stops." : ""), outcome.Message);
            Assert.Empty(heard);
            return;
        }
        var failure = Assert.Single(heard);
        Assert.False(failure.Success);
        Assert.Equal(Text(property == "record" ? "audio.recorder.record_stop_unconfirmed" : "audio.recorder.play_stop_unconfirmed", "B"), failure.Message);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        return condition();
    }
}

/// <summary>
/// ONE code path sets a slice's record or play (#787). Until Tape Deck track
/// 2 the Audio Check drove the radio's recorder through setters that read no
/// reply and did not refuse playback while transmitting, beside a second,
/// reply-checked path that did both. This reads the authored source and fails
/// if anything outside the recorder surface sets <c>RecordOn</c> or
/// <c>PlayOn</c>, sends <c>record=</c> or <c>play=</c> to a slice, or names
/// the retired setters.
/// </summary>
public sealed class SliceRecorderOneSurfaceTests
{
    private const string Surface = "Radios/FlexBase.SliceRecorder.cs";

    private static readonly Regex[] Forbidden =
    {
        new(@"\.\s*RecordOn\s*=(?!=)", RegexOptions.Compiled),
        new(@"\.\s*PlayOn\s*=(?!=)", RegexOptions.Compiled),
        new(@"\b(SliceRecordOn|SlicePlayOn|SlicePlayEnabled)\b", RegexOptions.Compiled),
        new(@"slice set [^""]*\b(record|play)=", RegexOptions.Compiled),
    };

    [Fact]
    public void Only_the_recorder_surface_drives_the_radios_slice_recorder()
    {
        var offenders = new List<string>();
        int scanned = 0;
        foreach (string file in IntegrationPassTree.AuthoredSource.Concat(IntegrationPassTree.UntrackedSource()))
        {
            string rel = IntegrationPassTree.Relative(file).Replace('\\', '/');
            if (IntegrationPassTree.IsTest(file) || rel == Surface) continue;
            scanned++;
            string[] lines = IntegrationPassTree.Read(file).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();
                if (line.StartsWith("//") || line.StartsWith("///") || line.StartsWith("'") || line.StartsWith("*")) continue;
                foreach (var pattern in Forbidden)
                    if (pattern.IsMatch(lines[i])) offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(scanned > 300, "only " + scanned + " source files were scanned; the tree was not found");
        Assert.True(offenders.Count == 0,
            "Something other than " + Surface + " drives the radio's slice recorder. Every door goes "
            + "through FlexBase.StartSliceRecordAsync and its siblings, which check the radio's reply "
            + "and refuse playback while transmitting (#787):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_scan_sees_each_shape_it_forbids()
    {
        // Positive controls: each pattern must catch the shape it exists for,
        // or a green run above means nothing.
        string[] shapes =
        {
            "theRadio.ActiveSlice.RecordOn = value;",
            "slice.PlayOn = true;",
            "if (rig.SlicePlayOn) rig.SlicePlayOn = false;",
            "bool radioHasTake = _rig?.SlicePlayEnabled == true;",
            "string command = $\"slice set {slice.Index} record=1\";",
        };
        foreach (string shape in shapes)
            Assert.True(Forbidden.Any(p => p.IsMatch(shape)), "No pattern catches: " + shape);
        Assert.False(Forbidden.Any(p => p.IsMatch("if (slice.PlayOn == true) return;")),
            "Reading PlayOn must not count as driving it");
    }
}

/// <summary>
/// Every recorder DOOR — Home, the Slice and Audio menus, the JJ key, the
/// registry commands, the Workshop's buttons — toggles through
/// <c>JJFlexWpf/RecorderControls.cs</c>, so a press is spoken the same way
/// from every door (#787). A door that called the rig's toggle directly would
/// have to word and speak the outcome itself, which is how a second
/// vocabulary for one press starts. The Audio Check is not a door in this
/// sense: it starts and stops the recorder as steps of its own flow, and
/// calls the Start and Stop methods, which this does not police.
/// </summary>
public sealed class RecorderDoorsTests
{
    private static readonly System.Text.RegularExpressions.Regex DirectToggle =
        new(@"(?<!RecorderControls)\.\s*ToggleSlice(Record|Play)Async\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly string[] Allowed =
    {
        "Radios/FlexBase.SliceRecorder.cs",
        "JJFlexWpf/RecorderControls.cs",
    };

    [Fact]
    public void Doors_toggle_the_recorder_through_RecorderControls()
    {
        var offenders = new List<string>();
        int scanned = 0;
        foreach (string file in IntegrationPassTree.AuthoredSource.Concat(IntegrationPassTree.UntrackedSource()))
        {
            string rel = IntegrationPassTree.Relative(file).Replace('\\', '/');
            if (IntegrationPassTree.IsTest(file) || Allowed.Contains(rel)) continue;
            scanned++;
            string[] lines = IntegrationPassTree.Read(file).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//")) continue;
                if (DirectToggle.IsMatch(lines[i])) offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(scanned > 300, "only " + scanned + " source files were scanned; the tree was not found");
        Assert.True(offenders.Count == 0,
            "A recorder door toggles the rig directly instead of through RecorderControls, so it words and "
            + "speaks the outcome on its own (#787):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_door_scan_tells_a_direct_toggle_from_a_RecorderControls_one()
    {
        Assert.Matches(DirectToggle, "var o = await _rig.ToggleSliceRecordAsync(target);");
        Assert.Matches(DirectToggle, "await rig.ToggleSlicePlayAsync(SliceRecorderTarget.ActiveSlice);");
        Assert.DoesNotMatch(DirectToggle, "await RecorderControls.ToggleSliceRecordAsync(_rig, target);");
    }
}
