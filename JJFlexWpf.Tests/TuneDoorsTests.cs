using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

// LEXICON_SCANNER_EXEMPT: the pins below quote source lines, escaped quotes and
// all, so the scanner would read each as a call with a mangled key.

/// <summary>
/// What the recorder and send doors do while a tune carrier is up (#860).
/// Recording is refused aloud; playing still plays and says it will not go
/// out; nothing starts a send.
/// </summary>
/// <remarks>
/// Windowless, proved: an MTA thread, an unconnected <see cref="FlexBase"/>
/// whose writes go nowhere, the radio's report stood in for by
/// <see cref="TuneDoors.UpOverride"/>, and a stand-in computer recorder. What
/// these cannot see is the radio: a bench step.
/// </remarks>
[Collection(RadioConfigStatics)]
public sealed class TuneDoorsTests : IDisposable
{
    private const string RadioConfigStatics = "TuneDoors";

    private const string Refused = "Tune is on. Turn it off first.";
    private const string WontGoOut = "Tune is on, so this recording won't go out on the air.";

    private readonly FlexBase _rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
    private readonly FakeRecorder _engine = new();
    private bool _boxWas;

    public TuneDoorsTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        _boxWas = VoxSendBox.Ticked;
        TuneDoors.UpOverride = _ => true;
        ComputerRecorder.Install(_engine);
    }

    public void Dispose()
    {
        TuneDoors.UpOverride = null;
        VoxSendBox.Ticked = _boxWas;
        ComputerRecorder.Install(null);
        ComputerRecorder.ChosenRecording = null;
        try { _rig.Dispose(); } catch { }
    }

    private sealed class FakeRecorder : IComputerRecorder
    {
        public bool IsRecording { get; set; }
        public bool IsPlaying { get; set; }
        public string LastRecordingPath => null!;
        public int Starts, Plays;
        public Task<RecorderOutcome> StartRecordingAsync(FlexBase rig) { Starts++; return Task.FromResult(new RecorderOutcome(true, "Recording", "x")); }
        public Task<RecorderOutcome> StopRecordingAsync() { IsRecording = false; return Task.FromResult(new RecorderOutcome(true, "Stopped", "x")); }
        public Task<RecorderOutcome> PlayAsync(string path) { Plays++; return Task.FromResult(new RecorderOutcome(true, "", "x")); }
        public Task<RecorderOutcome> StopPlaybackAsync() => Task.FromResult(new RecorderOutcome(true, "", "x"));
    }

    private static string Spoken(Func<Task> press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        press().GetAwaiter().GetResult();
        string newest = ScreenReaderOutput.RecentMessages.FirstOrDefault() ?? "";
        return newest == marker ? "" : newest;
    }

    // ── Recording is refused ────────────────────────────────────────────

    [Fact]
    public void Record_to_file_is_refused_aloud_and_nothing_starts()
    {
        string said = Spoken(() => RecorderControls.ToggleComputerRecordAsync(_rig));
        Assert.Equal(Refused, said);
        Assert.Equal(0, _engine.Starts);
    }

    [Fact]
    public void A_refusal_under_tune_is_told_the_usual_way_so_the_operators_Off_silences_it()
    {
        // Ruled by Noel 2026-11-01 13:08. The tone is the earcon's own gate; its
        // pin is in ZeroPowerDoorsTests, the same earcon.
        VerbosityLevel before = ScreenReaderOutput.CurrentVerbosity;
        try
        {
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;   // what Off is
            Assert.Equal("", Spoken(() => RecorderControls.ToggleComputerRecordAsync(_rig)));
            Assert.Equal(0, _engine.Starts);                                 // silenced, and still refused

            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;      // the positive control
            Assert.Equal(Refused, Spoken(() => RecorderControls.ToggleComputerRecordAsync(_rig)));
        }
        finally { ScreenReaderOutput.CurrentVerbosity = before; }
    }

    [Fact]
    public void Record_to_file_starts_when_tune_is_off()
    {
        TuneDoors.UpOverride = _ => false;     // positive control: the door is reachable
        RecorderControls.ToggleComputerRecordAsync(_rig).GetAwaiter().GetResult();
        Assert.Equal(1, _engine.Starts);
    }

    [Fact]
    public void A_recording_already_running_can_always_be_stopped()
    {
        _engine.IsRecording = true;
        var stopped = RecorderControls.ToggleComputerRecordAsync(_rig).GetAwaiter().GetResult();
        Assert.True(stopped.Success);
        Assert.False(_engine.IsRecording);
    }

    [Fact]
    public void Record_active_slice_is_refused_aloud()
    {
        string said = Spoken(() => RecorderControls.ToggleSliceRecordAsync(_rig, SliceRecorderTarget.ActiveSlice));
        Assert.Equal(Refused, said);
    }

    // ── Playing plays, and says it will not go out ──────────────────────

    [Fact]
    public void Play_from_computer_plays_on_the_speakers_says_so_and_starts_no_send()
    {
        string path = Path.Combine(Path.GetTempPath(), "jjflex-tune-" + Guid.NewGuid().ToString("N") + ".flac");
        File.WriteAllBytes(path, new byte[8]);
        try
        {
            ComputerRecorder.ChosenRecording = path;
            string said = Spoken(() => RecorderControls.ToggleComputerPlayAsync(_rig));
            Assert.Equal(WontGoOut, said);
            Assert.Equal(1, _engine.Plays);
            Assert.False(VoiceMessageSender.InFlight);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_playback_that_did_not_start_is_not_told_it_wont_go_out()
    {
        var refused = new RecorderOutcome(false, "No recording.", "x");
        Assert.Same(refused, TuneDoors.ForPlay(refused, true));
        var already = new RecorderOutcome(true, "Playing", "x", AlreadyInState: true);
        Assert.Same(already, TuneDoors.ForPlay(already, true));
    }

    [Fact]
    public void A_playback_that_started_says_it_only_under_tune()
    {
        var started = new RecorderOutcome(true, "Playing slice A", "x");
        Assert.Equal(WontGoOut, TuneDoors.ForPlay(started, true).Message);
        Assert.Equal("Playing slice A", TuneDoors.ForPlay(started, false).Message);
    }

    // ── The routes that ask OperatorHolds ───────────────────────────────

    [Fact]
    public void Under_tune_no_route_is_a_send()
    {
        // An unconnected radio reads AsBefore anyway, so this is read from the
        // source: the question has to be asked first, ahead of OperatorHolds
        // counting the tune as the operator holding the key.
        string src = Source("JJFlexWpf", "VoiceMessageSender.cs");
        int route = src.IndexOf("internal static SendRoute RouteFor(FlexBase rig)", StringComparison.Ordinal);
        int gate = src.IndexOf("if (TuneDoors.Up(rig)) return SendRoute.AsBefore;", route, StringComparison.Ordinal);
        int holds = src.IndexOf("OperatorHolds(rig, ptt)", route, StringComparison.Ordinal);
        Assert.True(route > 0 && gate > route && gate < holds);
    }

    [Fact]
    public void A_voice_message_key_under_tune_starts_nothing_and_says_so()
    {
        var item = new CWMessageItem { Label = "CQ", Audio = "none" };
        string said = Spoken(() => { VoiceMessageSender.Send(_rig, item); return Task.CompletedTask; });
        Assert.Equal(WontGoOut, said);
        Assert.False(VoiceMessageSender.InFlight);
    }

    [Fact]
    public void A_computer_send_that_reaches_the_door_under_tune_starts_nothing()
    {
        string said = Spoken(() => { VoiceMessageSender.SendComputerRecording(_rig, "x.flac", SendRoute.JoinTransmission); return Task.CompletedTask; });
        Assert.Equal(WontGoOut, said);
        Assert.False(VoiceMessageSender.InFlight);
    }

    [Fact]
    public void A_slice_send_that_reaches_the_door_under_tune_starts_nothing()
    {
        string said = Spoken(() => { VoiceMessageSender.SendSliceRecording(_rig, SliceRecorderTarget.ActiveSlice); return Task.CompletedTask; });
        Assert.Equal(WontGoOut, said);
        Assert.False(VoiceMessageSender.InFlight);
    }

    [Fact]
    public void Enter_in_Recorded_QSOs_previews_under_tune()
    {
        // AsBefore is what RouteFor answers under tune.
        Assert.Equal(Dialogs.RecordedQsosDialog.EnterAct.Preview,
            Dialogs.RecordedQsosDialog.ChooseEnter(false, false, SendRoute.AsBefore));
        Assert.Equal(Dialogs.RecordedQsosDialog.EnterAct.PreviewTransport,
            Dialogs.RecordedQsosDialog.ChooseEnter(true, false, SendRoute.AsBefore, previewPaused: true));
    }

    [Fact]
    public async Task A_paused_recording_carries_on_through_the_speakers_under_tune_with_VOX_paused()
    {
        // The real live pause, past the override: an unconnected radio
        // answers NotNeeded, and the tune answers before OperatorHolds can.
        var result = await LocalPlaybackVoxLive(_rig);
        Assert.Equal(LocalPlaybackVox.CarryOn.Resumed, result.Refusal);
        Assert.NotNull(result.Pause);
    }

    private static Task<(IDisposable? Pause, LocalPlaybackVox.CarryOn Refusal)> LocalPlaybackVoxLive(FlexBase rig)
    {
        var method = typeof(LocalPlaybackVox).GetMethod("PauseForCarryOnLiveAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (Task<(IDisposable?, LocalPlaybackVox.CarryOn)>)method.Invoke(null, new object?[] { rig })!;
    }

    // ── A tune that comes up after a door said yes ──────────────────────

    private const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;

    private object Info(bool paused)
    {
        var sender = typeof(VoiceMessageSender);
        var type = sender.GetNestedType("SendInfo", Any)!;
        object info = Activator.CreateInstance(type, true)!;
        type.GetField("Kind", Any)!.SetValue(info, Enum.Parse(sender.GetNestedType("SendKind", Any)!, "ComputerRecording"));
        type.GetField("What", Any)!.SetValue(info, "the computer recording test");
        type.GetField("Rig", Any)!.SetValue(info, _rig);
        var session = new SendSession(1);
        if (paused) session.PauseBeforeKeying();
        type.GetField("Session", Any)!.SetValue(info, session);
        return info;
    }

    /// <summary>The send's last step, as a door that said yes reaches it after the file has loaded.</summary>
    private string AtTheSink(string method, object info) => Spoken(() =>
    {
        typeof(VoiceMessageSender).GetMethod(method, Any)!.Invoke(null,
            method == "JoinWithFile" ? new object?[] { _rig, info, null } : new object?[] { _rig, info, "test", null });
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("BeginAndLaunch")]
    [InlineData("JoinWithFile")]
    public void A_tune_that_came_up_after_the_door_stops_the_send_at_its_last_step(string method)
    {
        Assert.Equal(WontGoOut, AtTheSink(method, Info(paused: false)));
        Assert.False(VoiceMessageSender.InFlight);
    }

    [Fact]
    public void With_no_tune_the_last_step_goes_on()
    {
        TuneDoors.UpOverride = _ => false;
        // The positive control: past the tune, an idle radio's own refusal.
        Assert.Contains(Lexicon.Get("audio.send.not_yet_transmitting"), AtTheSink("JoinWithFile", Info(paused: false)));
    }

    [Theory]
    [InlineData("BeginAndLaunch")]
    [InlineData("JoinWithFile")]
    public void A_send_carrying_on_goes_back_to_its_place_and_says_so(string method)
    {
        var paused = typeof(VoiceMessageSender).GetField("_paused", Any)!;
        try
        {
            object info = Info(paused: true);
            Assert.Equal(WontGoOut, AtTheSink(method, info));
            Assert.True(VoiceMessageSender.Paused);
            Assert.Same(info, paused.GetValue(null));
        }
        finally { paused.SetValue(null, null); }
    }

    // ── A recording paused on the speakers keeps its place ──────────────

    private object InfoHandingOff(Action handOff)
    {
        object info = Info(paused: false);
        info.GetType().GetField("HandOff", Any)!.SetValue(info, handOff);
        return info;
    }

    [Theory]
    [InlineData("BeginAndLaunch")]
    [InlineData("JoinWithFile")]
    public void A_late_refusal_leaves_the_paused_speakers_alone(string method)
    {
        bool handedOff = false;
        Assert.Equal(WontGoOut, AtTheSink(method, InfoHandingOff(() => handedOff = true)));
        Assert.False(handedOff);
    }

    [Fact]
    public void A_recording_paused_on_the_speakers_keeps_its_place_when_tune_comes_up_during_the_load()
    {
        // The whole road from the speakers: a real paused player, a real file
        // in the recordings folder, and a radio whose audio path says yes.
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var radioType = typeof(FlexBase).GetField("theRadio", flags)!.FieldType;
        object radio = Activator.CreateInstance(radioType, flags, null, new object[] { false }, null)!;
        radioType.GetField("_micInput", flags)!.SetValue(radio, "PC");
        typeof(FlexBase).GetField("theRadio", flags)!.SetValue(_rig, radio);
        typeof(FlexBase).GetField("_PCAudio", flags)!.SetValue(_rig, true);
        Assert.Equal("", _rig.TxTonePathTrouble);    // or the send never reaches the load

        Directory.CreateDirectory(RecordedQsoCatalog.FolderPath);
        string path = Path.Combine(RecordedQsoCatalog.FolderPath, "tune-" + Guid.NewGuid().ToString("N") + ".wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(24000, 16, 1)))
        {
            var samples = Enumerable.Repeat(0.5f, 72000).ToArray();
            writer.WriteSamples(samples, 0, samples.Length);
        }
        try
        {
            Assert.True(RecordingPlayer.PlayWithMixer(path, "A test recording.", null, false, _ => true));
            Assert.True(RecordingPlayer.Pause());
            double? at = RecordingPlayer.PositionSeconds;

            int asked = 0;
            TuneDoors.UpOverride = _ => ++asked >= 2;   // clear at the door, up at the last step
            string said = Spoken(() =>
            {
                Assert.True(VoiceMessageSender.SendPausedSpeakerPlayback(_rig, SendRoute.JoinTransmission));
                return Task.CompletedTask;
            });

            Assert.True(asked >= 2);                    // the send did reach the last step
            Assert.Equal(WontGoOut, said);
            Assert.True(RecordingPlayer.IsPaused);
            Assert.Equal(at, RecordingPlayer.PositionSeconds);
            Assert.False(VoiceMessageSender.InFlight);
        }
        finally
        {
            RecordingPlayer.StopWithoutSpeaking();
            typeof(FlexBase).GetField("theRadio", flags)!.SetValue(_rig, null);
            File.Delete(path);
        }
    }

    [Fact]
    public void Any_late_refusal_leaves_them_alone_not_just_a_tunes()
    {
        TuneDoors.UpOverride = _ => false;
        bool handedOff = false;
        AtTheSink("JoinWithFile", InfoHandingOff(() => handedOff = true));   // an idle radio turns it away
        Assert.False(handedOff);
    }

    [Fact]
    public void The_hand_off_runs_once_and_only_at_the_two_last_steps()
    {
        string src = Source("JJFlexWpf", "VoiceMessageSender.cs");
        int send = src.IndexOf("private static void SendComputerRecording(FlexBase rig, string path, SendRoute route, double fromSeconds,", StringComparison.Ordinal);
        string body = src.Substring(send, src.IndexOf("StartComputerPiece(rig, info, loaded, route);", send, StringComparison.Ordinal) - send);
        Assert.DoesNotContain("beforeSending?.Invoke", body);
        Assert.Contains("info.HandOff = beforeSending;", body);

        int plain = src.IndexOf("private static void BeginAndLaunch(", StringComparison.Ordinal);
        int gate = src.IndexOf("if (TuneDoors.Up(rig))", plain, StringComparison.Ordinal);
        int handOff = src.IndexOf("HandOffNow(info);", plain, StringComparison.Ordinal);
        int holds = src.IndexOf("PieceHolds holds = PieceHolds.Take", plain, StringComparison.Ordinal);
        Assert.True(gate > plain && handOff > gate && holds > handOff);

        int box = src.IndexOf("VoiceMessageSend.BeginKeyed(info.Owned", StringComparison.Ordinal);
        int boxHandOff = src.LastIndexOf("HandOffNow(info);", box, StringComparison.Ordinal);
        int boxGate = src.LastIndexOf("RefuseIfHeldAfterCut(\"a send\")", box, StringComparison.Ordinal);
        int owned = src.LastIndexOf("return false;", box, StringComparison.Ordinal);
        Assert.True(boxHandOff > boxGate && boxHandOff > owned);
    }

    [Fact]
    public void Every_send_reaches_the_last_step_after_its_file_has_loaded()
    {
        string src = Source("JJFlexWpf", "VoiceMessageSender.cs");
        int resume = src.IndexOf("private static void ResumePaused()", StringComparison.Ordinal);
        int load = src.IndexOf("TxAudioFile.TryLoadPieceInto(", resume, StringComparison.Ordinal);
        int piece = src.IndexOf("StartVoiceMessagePiece(rig, info, loaded, route);", load, StringComparison.Ordinal);
        Assert.True(resume > 0 && load > resume && piece > load);
        Assert.Contains("BeginAndLaunch(rig, info, info.Label, loaded);", src);
        Assert.Contains("BeginAndLaunch(rig, info, info.What, loaded);", src);
    }

    [Fact]
    public void The_reference_arm_is_refused_aloud_and_unticks()
    {
        Exception? failure = null;
        string said = "";
        bool? ticked = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var dialog = (Dialogs.CheckYourAudioDialog)System.Runtime.CompilerServices.RuntimeHelpers
                    .GetUninitializedObject(typeof(Dialogs.CheckYourAudioDialog));
                var box = new System.Windows.Controls.CheckBox { IsChecked = true };
                typeof(Dialogs.CheckYourAudioDialog).GetField("_rig", Flags)!.SetValue(dialog, _rig);
                typeof(Dialogs.CheckYourAudioDialog).GetField("_refArmCheck", Flags)!.SetValue(dialog, box);
                var arm = typeof(Dialogs.CheckYourAudioDialog).GetMethod("ReferenceArmChanged", Flags)!;
                said = Spoken(() => { arm.Invoke(dialog, new object[] { true }); return Task.CompletedTask; });
                ticked = box.IsChecked;
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.Equal(WontGoOut, said);
        Assert.False(ticked);
    }

    // ── Read from source: these need a keyed radio or a window ──────────

    private static string Source(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(path)));
    }

    [Fact]
    public void A_carry_on_under_tune_passes_both_transmit_questions()
    {
        // An unconnected radio holds nothing, so the two questions are read
        // from the source: OperatorHolds counts a tune, and rig.Transmit reads
        // true under one, and either would keep the recording paused.
        string src = Source("JJFlexWpf", "LocalPlaybackVox.cs");
        int live = src.IndexOf("PauseForCarryOnLiveAsync(FlexBase? rig)", StringComparison.Ordinal);
        Assert.Contains("if (!underTune && VoiceMessageSender.OperatorHolds(rig,", src.Substring(live));
        int early = src.IndexOf("if (underTune) return (pause, CarryOn.Resumed);", live, StringComparison.Ordinal);
        int wait = src.IndexOf("while (rig.Transmit", live, StringComparison.Ordinal);
        Assert.True(early > live && early < wait, "a tune must not wait for the carrier to drop");
    }

    [Fact]
    public void Play_from_computer_is_not_refused_as_transmitting_under_tune()
    {
        string src = Source("JJFlexWpf", "RecorderControls.cs");
        Assert.Contains("bool transmitting = rig?.Transmit == true && !underTune;", src);
    }

    [Fact]
    public void A_preview_is_not_refused_as_transmitting_under_tune()
    {
        Assert.Contains("if (_rig?.Transmit == true && !underTune) { Refuse(Lexicon.Get(\"audio.qsos.play_refused_transmitting\")); return; }",
            Source("JJFlexWpf", "Dialogs", "RecordedQsosDialog.cs"));
    }

    [Fact]
    public void A_check_is_refused_before_its_countdown_and_a_take_says_it_wont_go_out()
    {
        string check = Source("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs");
        int toggle = check.IndexOf("public void ToggleAudioCheck()", StringComparison.Ordinal);
        int gate = check.IndexOf("if (TuneDoors.Up(_rig))", toggle, StringComparison.Ordinal);
        int start = check.IndexOf("_ = StartAudioCheckSessionAsync(", toggle, StringComparison.Ordinal);
        int offAir = check.IndexOf("_ = StartOffAirAsync(ptt)", toggle, StringComparison.Ordinal);
        Assert.True(gate > toggle && gate < start && gate < offAir, "the gate must come before every keyed method");
        Assert.Contains("TuneDoors.ForPlay(Lexicon.Get(\"audio.check.playing_take\"), underTune)", check);
    }

    [Fact]
    public void A_box_send_that_met_a_tune_while_it_waited_refuses_instead_of_joining()
    {
        string src = Source("JJFlexWpf", "VoiceMessageSender.cs");
        int gate = src.IndexOf("// A tune came up while we waited", StringComparison.Ordinal);
        int join = src.IndexOf("joinInstead();", gate, StringComparison.Ordinal);
        Assert.True(gate > 0 && join > gate);
    }

    [Fact]
    public void A_paused_send_stays_paused_under_tune()
    {
        string src = Source("JJFlexWpf", "VoiceMessageSender.cs");
        int resume = src.IndexOf("private static void ResumePaused()", StringComparison.Ordinal);
        int gate = src.IndexOf("if (TuneDoors.Up(rig))", resume, StringComparison.Ordinal);
        int key = src.IndexOf("_paused = null;", gate, StringComparison.Ordinal);
        Assert.True(gate > resume && key > gate);
    }

    [Fact]
    public void No_door_touches_the_carrier()
    {
        string src = Source("JJFlexWpf", "TuneDoors.cs");
        Assert.DoesNotContain("TxTune =", src);
        Assert.DoesNotContain("DropCarrier", src);
    }
}

[CollectionDefinition("TuneDoors", DisableParallelization = true)]
public sealed class TuneDoorsCollection { }
