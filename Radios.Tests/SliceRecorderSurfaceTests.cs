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
    public async Task Keying_stops_a_playback_and_play_never_keys()
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
            Assert.Equal(Text(property == "record" ? "audio.recorder.record_stop_unconfirmed" : "audio.recorder.play_stop_unconfirmed", "B"), outcome.Message);
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
