using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A Record yourself take the radio has been asked to play, and has not yet
/// answered, keeps the audition's microphone shut until it has been answered
/// and stopped again; and it says nothing (#262, Sprint 49 Track Q2 repair 4;
/// Noel 2026-11-19 12:22).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect, from Codex's second re-check of <c>b9afa71f</c>, whose held-start
/// reproduction and tracked-target positive control this adopts.</b>
/// <c>PlayRadioTakeAsync</c> put its target on the window's playback list only
/// AFTER the radio confirmed the start, and both audition guards read only that
/// list. So Play last take, then the audition's Start inside the radio's answer
/// (countdown off), opened capture with no stop sent; when the answer arrived
/// the take played and "Playing your take" was asked for, inside the capture.
/// </para>
/// <para>
/// <b>Real seams.</b> The window's own <c>PlayRadioTakeAsync</c>, its own gate
/// (<c>CountedThenRadioPlaysStoppedAsync</c>, countdown off in the isolated
/// settings) and its own last act before opening (<c>QuietBeforeTake</c>), on a
/// dialog whose constructor never runs; the REAL <see cref="FlexBase"/> slice
/// recorder over a vendor <see cref="Radio"/> and <see cref="Slice"/>, with the
/// vendor's own reply and status parsers; the REAL
/// <see cref="ChainAuditionTake.StartAfterAsync"/>. The radio's answer is held
/// by a command capture and released by hand.
/// </para>
/// <para>
/// <b>Desk safety.</b> No window and no WPF control: every test asserts
/// <see cref="NoiseReductionPerChannelTests.AssertWindowless"/> (an MTA
/// thread). Nothing audible (<see cref="QuietRun"/>, render off); settings
/// isolated (<see cref="TestSettingsRoot"/>); no socket, and no command leaves
/// the process. The microphone is a fake that opens nothing. What the app asks
/// to say or sound is read from the output transcript, never from a reader.
/// </para>
/// </remarks>
public sealed class AuditionPendingRadioPlayTests : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string PlayOn = "slice set 0 play=1";
    private const string PlayOff = "slice set 0 play=0";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jjflex-audition-radio-" + Guid.NewGuid().ToString("N"));
    private readonly string _transcript = Path.Combine(Path.GetTempPath(), "jjflex-audition-radio-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public AuditionPendingRadioPlayTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Assert.False(OutputChannelRecorder.RenderEnabled);
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        OutputChannelRecorder.Configure(render: false, record: false);
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { File.Delete(_transcript); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── What the window does ─────────────────────────────────────────

    [Fact]
    public async Task A_radio_playback_still_waiting_for_its_answer_keeps_the_take_shut_until_it_is_stopped_and_says_nothing()
    {
        // Codex's held-start reproduction: the target is NOT on the window's
        // list, which is the state the window was in while the radio answered.
        await using var b = new Bench(_transcript);
        var play = b.PressPlay();
        await b.PumpUntil(() => b.Held != 0);
        Assert.False(play.IsCompleted);
        Assert.True(b.Rig.IsSlicePlaying(b.Target), "the recorder itself knows a start is pending");
        Assert.Empty(b.Targets);
        int before = b.Transcript().Length;

        var starting = b.StartTake(_folder);
        Assert.False(starting.IsCompleted, "the take opened while the radio had not yet answered the playback");
        Assert.False(b.Microphone.IsRunning);
        Assert.DoesNotContain(PlayOff, b.Wire.Commands);

        b.AnswerHeldStart(yes: true);
        await b.PumpUntil(() => starting.IsCompleted && play.IsCompleted);

        Assert.True(await starting, "the take opens once the playback is answered and stopped");
        Assert.True(b.Microphone.IsRunning);
        Assert.True(b.Microphone.OpenedWithPlaybackStopped, "the microphone opened before the radio's playback was stopped");
        Assert.Contains(PlayOff, b.Wire.Commands);
        Assert.False(b.Rig.IsSlicePlaying(b.Target));
        Assert.False(b.Slice.PlayOn);
        Assert.Empty(b.Targets);
        string after = b.Transcript().Substring(before);
        Assert.DoesNotContain(Lexicon.Get("audio.check.playing_take"), after);
        Assert.DoesNotContain("\"event\":\"speech\"", after);
        Assert.DoesNotContain("\"event\":\"earcon\"", after);
    }

    [Fact]
    public async Task A_waiting_radio_playback_the_radio_refuses_says_nothing_and_the_take_then_opens()
    {
        await using var b = new Bench(_transcript);
        var play = b.PressPlay();
        await b.PumpUntil(() => b.Held != 0);
        int before = b.Transcript().Length;

        var starting = b.StartTake(_folder);
        Assert.False(starting.IsCompleted);

        b.AnswerHeldStart(yes: false);
        await b.PumpUntil(() => starting.IsCompleted && play.IsCompleted);

        Assert.True(await starting);
        Assert.True(b.Microphone.OpenedWithPlaybackStopped);
        Assert.False(b.Rig.IsSlicePlaying(b.Target));
        string after = b.Transcript().Substring(before);
        Assert.DoesNotContain("\"event\":\"speech\"", after);
        Assert.DoesNotContain("\"event\":\"earcon\"", after);
    }

    [Fact]
    public async Task A_waiting_radio_playback_that_will_not_stop_keeps_the_take_shut_and_says_so_before_anything_opens()
    {
        // A playback that cannot be stopped refuses the take, as one that was
        // already playing does (repair 2): the take never opens.
        await using var b = new Bench(_transcript);
        b.RefuseStops = true;
        var play = b.PressPlay();
        await b.PumpUntil(() => b.Held != 0);
        int before = b.Transcript().Length;

        var starting = b.StartTake(_folder);
        b.AnswerHeldStart(yes: true);
        await b.PumpUntil(() => starting.IsCompleted && play.IsCompleted);

        Assert.False(await starting, "the take opened over a playback the radio would not stop");
        Assert.False(b.Microphone.EverOpened);
        Assert.True(b.Rig.IsSlicePlaying(b.Target));
        Assert.Contains(b.Target, b.Targets);
        Assert.Contains(Lexicon.Get("audio.audition.not_quiet"), b.Transcript().Substring(before));
        Assert.DoesNotContain(Lexicon.Get("audio.check.playing_take"), b.Transcript().Substring(before));
    }

    [Fact]
    public async Task The_last_act_before_opening_refuses_while_a_radio_playback_still_waits_and_the_request_is_then_void()
    {
        // The gate waits for every request it can see; one pressed after it
        // waited, still unanswered when the microphone is about to open, is
        // caught by the window's last act instead, and voided by it.
        await using var b = new Bench(_transcript);
        var play = b.PressPlay();
        await b.PumpUntil(() => b.Held != 0);
        int before = b.Transcript().Length;

        Assert.False(Call<bool>(b.Dialog, "QuietBeforeTake"), "the microphone may open with a radio playback unanswered");
        Assert.Contains(Lexicon.Get("audio.audition.not_quiet"), b.Transcript().Substring(before));

        int afterRefusal = b.Transcript().Length;
        b.AnswerHeldStart(yes: true);
        await b.PumpUntil(() => play.IsCompleted);
        await play;

        Assert.Contains(PlayOff, b.Wire.Commands);
        Assert.False(b.Rig.IsSlicePlaying(b.Target));
        Assert.DoesNotContain("\"event\":\"speech\"", b.Transcript().Substring(afterRefusal));
    }

    [Fact]
    public async Task A_radio_playback_answered_with_no_take_starting_is_announced_as_before()
    {
        // The other side of the fix: a request no take start has made void is
        // the ordinary Play last take, and still says so.
        await using var b = new Bench(_transcript);
        var play = b.PressPlay();
        await b.PumpUntil(() => b.Held != 0);
        int before = b.Transcript().Length;

        b.AnswerHeldStart(yes: true);
        await b.PumpUntil(() => play.IsCompleted);
        await play;

        Assert.True(b.Rig.IsSlicePlaying(b.Target));
        Assert.Contains(b.Target, b.Targets);
        Assert.Contains(Lexicon.Get("audio.check.playing_take"), b.Transcript().Substring(before));
        Assert.DoesNotContain(PlayOff, b.Wire.Commands);
    }

    // ── Positive control: the instrument sees a stop when the window knows ──

    [Fact]
    public async Task Positive_control_a_target_already_on_the_list_is_stopped_before_the_take_opens()
    {
        // Codex's tracked-target control, unchanged in what it asserts: with the
        // pending target on the window's list the gate waits behind the start,
        // stops it with play=0, and only then opens. It passed before repair 4
        // as well, so the reproduction above is failing on the missing
        // accounting and not on the harness.
        await using var b = new Bench(_transcript);
        b.Targets.Add(b.Target);
        var play = b.PressPlay();
        await b.PumpUntil(() => b.Held != 0);
        Assert.False(play.IsCompleted);

        var starting = b.StartTake(_folder);
        Assert.False(starting.IsCompleted, "the tracked control waits for the outstanding stop");
        Assert.False(b.Microphone.IsRunning);

        b.AnswerHeldStart(yes: true);
        await b.PumpUntil(() => starting.IsCompleted && play.IsCompleted);

        Assert.True(await starting);
        Assert.True(b.Microphone.IsRunning);
        Assert.Contains(PlayOff, b.Wire.Commands);
        Assert.False(b.Rig.IsSlicePlaying(b.Target));
        Assert.False(b.Slice.PlayOn);
    }

    // ── The arrangement ──────────────────────────────────────────────

    private static void Set(object o, string name, object? value) => o.GetType().GetField(name, Hidden)!.SetValue(o, value);
    private static T Call<T>(object o, string name) => (T)o.GetType().GetMethod(name, Hidden)!.Invoke(o, null)!;

    /// <summary>
    /// A real FlexBase over a vendor radio with slice A (index 0) holding a
    /// playable recording, a window with that slice as its radio take, an
    /// output transcript, and a fake microphone. Put back in DisposeAsync.
    /// </summary>
    private sealed class Bench : IAsyncDisposable
    {
        public const uint OurHandle = 0x1A2B3C4D;
        private readonly FlexBaseGlobals _globals;
        private readonly object _queue;
        private readonly VerbosityLevel _oldVerbosity;
        private readonly string _transcript;
        private Task? _play;

        public readonly FlexBase Rig;
        public readonly Radio Radio;
        public readonly Slice Slice;
        public readonly WireCapture Wire = new();
        public readonly CheckYourAudioDialog Dialog;
        public readonly HashSet<SliceRecorderTarget> Targets = new();
        public readonly SliceRecorderTarget Target = SliceRecorderTarget.Letter('A');
        public readonly Microphone Microphone;

        /// <summary>The sequence number of the held play=1, or 0 before it is sent.</summary>
        public int Held;
        /// <summary>When set, the radio refuses every play=0.</summary>
        public bool RefuseStops;

        public Bench(string transcript)
        {
            _transcript = transcript;
            _globals = FlexBaseGlobals.Capture();
            Rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            Radio = (Radio)Activator.CreateInstance(typeof(Radio), Hidden, null, new object[] { false }, null)!;
            typeof(Radio).GetProperty(nameof(Radio.Serial))!.GetSetMethod(nonPublic: true)!
                .Invoke(Radio, new object[] { "audition-radio-" + Guid.NewGuid().ToString("N") });
            Set(Radio, "_commandCommunication", Wire);
            typeof(Radio).GetProperty(nameof(Radio.Connected))!.GetSetMethod(nonPublic: true)!.Invoke(Radio, new object[] { true });
            Set(Radio, "_clientHandle", OurHandle);
            Set(Radio, "_guiClientID", "persistent-gui");
            Set(Rig, "theRadio", Radio);
            Set(Rig, "clientHandle", OurHandle);

            Slice = (Slice)Activator.CreateInstance(typeof(Slice), Hidden, null, new object[] { Radio }, null)!;
            typeof(Slice).GetProperty(nameof(Slice.Index))!.GetSetMethod(nonPublic: true)!.Invoke(Slice, new object[] { 0 });
            typeof(Slice).GetProperty(nameof(Slice.ClientHandle))!.GetSetMethod(nonPublic: true)!.Invoke(Slice, new object[] { OurHandle });
            Set(Slice, "_freq", 14.2);
            Set(Slice, "_demodMode", "USB");
            Set(Slice, "_lock", false);
            Set(Slice, "_active", true);
            Set(Slice, "_isTransmitSlice", true);
            Set(Slice, "_playEnabled", true);
            Set(Slice, "_playOn", false);
            ((List<Slice>)typeof(FlexBase).GetField("mySlices", Hidden)!.GetValue(Rig)!).Add(Slice);
            Radio.SliceList.Add(Slice);

            _queue = typeof(FlexBase).GetField("q", Hidden)!.GetValue(Rig)!;
            _queue.GetType().GetProperty("MainLoop")!.SetValue(_queue, true);
            typeof(FlexBase).GetProperty("SliceRecorderConfirmationWait", Hidden)!.SetValue(Rig, TimeSpan.FromSeconds(12));

            Dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
            Set(Dialog, "_rig", Rig);
            Set(Dialog, "_radioTakeTarget", Target);
            Set(Dialog, "_checkPlayTargets", Targets);

            // The radio answers everything at once, except play=1, which it holds.
            Wire.CommandWritten = command =>
            {
                if (command == PlayOn && Held == 0) { Held = Wire.LastSequence; return; }
                if (command == PlayOff && RefuseStops) { Reply(Wire.LastSequence, 0x50000001); return; }
                if (command.StartsWith("slice set ", StringComparison.Ordinal)) Status("slice " + command[10..]);
                if (command.StartsWith("transmit set ", StringComparison.Ordinal)) Status("transmit " + command[13..]);
                Reply(Wire.LastSequence, 0);
            };

            Microphone = new Microphone(() => !Rig.IsSlicePlaying(Target) && Wire.Commands.Contains(PlayOff));
            _oldVerbosity = ScreenReaderOutput.CurrentVerbosity;
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: transcript);
        }

        /// <summary>The window's own Play last take, for the radio's take.</summary>
        public Task PressPlay() => _play = Call<Task>(Dialog, "PlayRadioTakeAsync");

        /// <summary>
        /// The audition's start, from its gate on: the window's own gate with the
        /// countdown off, and its own last act before the microphone opens.
        /// </summary>
        public Task<bool> StartTake(string folder)
        {
            var take = new ChainAuditionTake(folder, Microphone, ChainNotConnectedYet.Instance, _ => null);
            return take.StartAfterAsync(
                () => Call<Task<bool>>(Dialog, "CountedThenRadioPlaysStoppedAsync"),
                () => true, () => DateTime.Now,
                beforeOpening: () => Call<bool>(Dialog, "QuietBeforeTake"));
        }

        /// <summary>The radio answers the held play=1, and reports play on for a yes.</summary>
        public void AnswerHeldStart(bool yes)
        {
            Assert.NotEqual(0, Held);
            if (yes) Status("slice 0 play=1");
            Reply(Held, yes ? 0u : 0x50000001u);
        }

        public string Transcript()
        {
            if (!File.Exists(_transcript)) return "";
            using var file = new FileStream(_transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var text = new StreamReader(file);
            return text.ReadToEnd();
        }

        private void Reply(int seq, uint code) =>
            typeof(Radio).GetMethod("ParseReply", Hidden)!.Invoke(Radio, new object[] { "R" + seq + "|" + code.ToString("X") + "|" });

        private void Status(string body) =>
            typeof(Radio).GetMethod("ParseStatus", Hidden)!.Invoke(Radio, new object[] { "S0|" + body });

        /// <summary>Run FlexBase's command queue by hand until <paramref name="done"/>.</summary>
        public async Task PumpUntil(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(8);
            while (!done() && DateTime.UtcNow < until)
            {
                while ((int)_queue.GetType().GetProperty("Count")!.GetValue(_queue)! > 0)
                {
                    var item = _queue.GetType().GetMethod("Dequeue")!.Invoke(_queue, null)!;
                    ((Delegate)item.GetType().GetField("Item")!.GetValue(item)!).DynamicInvoke();
                }
                await Task.Delay(5);
            }
            Assert.True(done(), "the command pump did not reach its condition");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                Microphone.Stop();
                RefuseStops = false;
                if (Held != 0 && _play is { IsCompleted: false }) AnswerHeldStart(yes: true);
                if (_play is { IsCompleted: false }) { try { await PumpUntil(() => _play.IsCompleted); } catch { } }
                var stop = Rig.StopSlicePlayAsync(Target);
                try { await PumpUntil(() => stop.IsCompleted); } catch { }
            }
            finally
            {
                OutputChannelRecorder.Configure(render: false, record: false);
                ScreenReaderOutput.CurrentVerbosity = _oldVerbosity;
                try { Set(Rig, "theRadio", null); Rig.Dispose(); } catch { }
                _globals.Dispose();
            }
        }
    }

    /// <summary>A microphone that opens nothing, and notes whether the radio's playback had been stopped when it opened.</summary>
    private sealed class Microphone(Func<bool> playbackStopped) : IAuditionRecorder
    {
        public bool IsRunning { get; private set; }
        public bool EverOpened { get; private set; }
        public bool OpenedWithPlaybackStopped { get; private set; }
        public bool IsRecordingTo(string path) => IsRunning;
        public bool Start(string path, TimeSpan fileStartsAfter)
        {
            OpenedWithPlaybackStopped = playbackStopped();
            IsRunning = EverOpened = true;
            return true;
        }
        public void Stop() => IsRunning = false;
    }

    /// <summary>
    /// The radio's command channel, captured: connected as far as FlexLib can
    /// tell, and never to anything. Each command is handed to
    /// <see cref="CommandWritten"/> so the test can answer it, by its sequence
    /// number, through the vendor's own parsers.
    /// </summary>
    private sealed class WireCapture : CommandCommunicationBase
    {
        private readonly List<string> _written = new();
        public Action<string>? CommandWritten;
        public WireCapture() => PublishConnected(BeginConnection());
        public override IPAddress? LocalIp { get; set; } = IPAddress.Loopback;
        public override bool Connect(IPAddress ip, int port, int source, out CommandConnection? connection) =>
            throw new InvalidOperationException("No connection is made in this test.");
        public override void Disconnect() { }
        public override void Write(string value)
        {
            lock (_written) _written.Add(value ?? "");
            CommandWritten?.Invoke(Text(value ?? ""));
        }

        public List<string> Commands { get { lock (_written) return _written.Select(Text).ToList(); } }

        public int LastSequence
        {
            get
            {
                string wire;
                lock (_written) wire = _written[^1];
                return int.Parse(wire[..wire.IndexOf('|')].TrimStart('C', 'D'), CultureInfo.InvariantCulture);
            }
        }

        private static string Text(string wire)
        {
            int bar = wire.IndexOf('|');
            return (bar < 0 ? wire : wire[(bar + 1)..]).TrimEnd('\n', '\r');
        }
    }
}
