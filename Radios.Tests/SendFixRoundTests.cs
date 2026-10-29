#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The 2026-10-28 fix round of Track B (#835). Behaviour is driven where the
    /// state machine lives in this assembly; the parts that live in the UI
    /// assembly (the send host, push to talk) are pinned from source and stated
    /// as pins, the way <c>LockSafetySourcePinTests</c> does.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class SendFixRoundTests
    {
        private sealed class FakeRig : IVoiceMessageRig
        {
            public readonly List<string> Calls = new();
            public bool RadioSaysTransmitting;
            public bool Transmit { get => RadioSaysTransmitting; set => Calls.Add(value ? "Transmit=true" : "Transmit=false"); }
            public bool FilePlaying { get; set; }
            public bool FileReachedEnd { get; set; }
            public double FileSeconds { get; set; } = 8.0;
            public double Played;
            public double FilePlayedSeconds => Played;
            public void FileStart() { Calls.Add("FileStart"); FilePlaying = true; }
            public void FileStartForVox() { Calls.Add("FileStartForVox"); FilePlaying = true; }
            public void FileStop() { Calls.Add("FileStop"); FilePlaying = false; }
            public bool VoxOn { get; set; }
        }

        // ── Item 2: the takeover rides push to talk's first line ─────────────

        /// <summary>
        /// Push to talk's key-down takes a box send at once, with exactly the
        /// ending the tick would give it: the recording stops, the microphone
        /// comes back, and the carrier is NOT dropped under the operator's thumb.
        /// </summary>
        [Fact]
        public void A_box_send_taken_at_the_key_down_stops_and_leaves_the_key_alone()
        {
            var rig = new FakeRig();
            var send = VoiceMessageSend.BeginKeyed(rig, "cq", 0, new KeyedSendOptions());
            rig.RadioSaysTransmitting = true;
            send.Tick(100);

            Assert.True(send.TakeOverByController());

            Assert.Equal(VoiceMessageOutcome.TakenOver, send.Outcome);
            Assert.False(send.ReleasedToHardware);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop" }, rig.Calls);
            Assert.False(send.TakeOverByController(), "a send already ended is not taken twice");
        }

        /// <summary>
        /// Only a box send is taken over: a voice message riding the operator's
        /// key, or one VOX keyed, is not something push to talk takes from, and
        /// pressing it must leave it exactly as it was.
        /// </summary>
        [Fact]
        public void A_send_that_is_not_a_box_send_is_not_taken_at_the_key_down()
        {
            var keyed = new FakeRig();
            var plain = VoiceMessageSend.Begin(keyed, "cq", 0);
            Assert.False(plain.TakeOverByController());
            Assert.False(plain.Finished);

            var vox = new FakeRig { VoxOn = true };
            var viaVox = VoiceMessageSend.Begin(vox, "cq", 0);
            Assert.False(viaVox.TakeOverByController());
            Assert.False(viaVox.Finished);
        }

        /// <summary>
        /// Pins, because push to talk and the send host live in the UI assembly:
        /// both key-downs that START a transmission ask the send host first and
        /// speak ONE line, and the send host says nothing more when that line
        /// already told the operator.
        /// </summary>
        [Fact]
        public void Push_to_talks_first_line_carries_the_takeover_and_the_send_does_not_say_it_twice()
        {
            string ctl = ReadSource("JJFlexWpf/PttSafetyController.cs");
            // (The keys are matched by their tails: a quoted key in the source
            // of a test reads as a lexicon reference to the coverage scanner.)
            string down = Body(ctl, "public void PttDown()");
            Assert.Contains("SpeakKeyDownFirstLine(", down);
            Assert.Contains("announce_transmitting", down);
            string locked = Body(ctl, "private void EnterLocked()");
            Assert.Contains("bool fromIdle = State == PttState.Idle;", locked);
            Assert.Contains("if (fromIdle) SpeakKeyDownFirstLine(", locked);

            string first = Body(ctl, "private void SpeakKeyDownFirstLine(string keyingLine)");
            int asks = first.IndexOf("VoiceMessageSender.TakeOverAtKeyDown()", StringComparison.Ordinal);
            int speaks = first.IndexOf("ScreenReaderOutput.Speak(", StringComparison.Ordinal);
            Assert.True(asks >= 0 && speaks > asks, "the send must be asked before anything is spoken");
            Assert.Contains("takeover + \" \" + keyingLine", first);

            string host = ReadSource("JJFlexWpf/VoiceMessageSender.cs");
            string settle = Body(host, "private static void Settle()");
            Assert.Contains("if (send.Outcome == VoiceMessageOutcome.TakenOver && info.TakeoverSpoken) return;", settle);
            Assert.Contains("send.TakeOverByController()", Body(host, "internal static string? TakeOverAtKeyDown()"));
        }

        [Fact]
        public void The_first_line_reads_as_one_sentence_pair()
        {
            Assert.Equal("cq stopped. Transmitting.",
                Lexicon.Get("audio.send.message_taken_over_ptt", ("label", "cq")) + " "
                + Lexicon.Get("audio.ptt.announce_transmitting") + ".");
            Assert.Equal("Stopped sending the computer recording bench-2m50. Transmitting, locked.",
                Lexicon.Get("audio.send.taken_over_ptt",
                    ("what", Lexicon.Get("audio.send.what_computer", ("name", "bench-2m50")))) + " "
                + Lexicon.Get("audio.ptt.announce_locked") + ".");
        }

        // ── Item 1: the monitor, not a local copy ────────────────────────────

        [Fact]
        public void Every_send_piece_holds_the_monitor_and_the_ptt_watch_and_no_local_copy_is_played()
        {
            string host = ReadSource("JJFlexWpf/VoiceMessageSender.cs");
            string take = Body(host, "public static PieceHolds Take(FlexBase rig, string who)");
            Assert.Contains("rig.HoldMonitorForSend(who)", take);
            Assert.Contains("rig.WatchPttSource()", take);
            Assert.DoesNotContain("PlayAlongsideSend", host);
            Assert.DoesNotContain("PlayAlongsideSend", ReadSource("JJFlexWpf/RecordingPlayer.cs"));

            // Taken before Begin, which can key at once, so the monitor's
            // turn-on is queued ahead of the key.
            string begin = Body(host, "private static void BeginAndLaunch(");
            Assert.True(begin.IndexOf("PieceHolds.Take", StringComparison.Ordinal)
                        < begin.IndexOf("VoiceMessageSend.Begin(", StringComparison.Ordinal));

            // Given back on every settle, paused included.
            Assert.Contains("ReleaseHolds(info);", Body(host, "private static void Settle()"));

            // The slice recorder's own playback to the air holds it too, and
            // gives it back with its VOX lease on every road out.
            string slice = ReadSource("Radios/FlexBase.SliceRecorder.cs");
            Assert.Contains("if (intent.OnAir && state.PlaybackMonitor == null)", slice);
            Assert.Contains("Interlocked.Exchange(ref state.PlaybackMonitor, null)?.Dispose();",
                Body(slice, "private void ReleasePlaybackVox(SliceRecorderState state)"));
        }

        // ── Item 4: a voice message's own key pauses it ──────────────────────

        /// <summary>
        /// Noel, 2026-10-28 21:20: a voice message's own key PAUSES it and
        /// carries it on, the same as Play; Escape stops it. Pinned at the
        /// door, because the door is in the UI assembly; what Pause does to the
        /// send is driven in <c>SendPauseTests</c>.
        /// </summary>
        [Fact]
        public void A_voice_messages_own_key_pauses_it_and_carries_it_on()
        {
            string host = ReadSource("JJFlexWpf/VoiceMessageSender.cs");
            string door = Body(host, "public static void Send(FlexBase rig, CWMessageItem item)");
            Assert.Contains("SendSession.Answer(StateOf(active), same, pressPauses: true, canHoldPlace: true)", door);
            Assert.Contains("case SendPress.Pause:", door);
            Assert.Contains("PauseRunning();", door);
            Assert.Contains("ResumePaused();", door);

            Assert.Equal(SendPress.Pause, SendSession.Answer(SendSessionState.Running, true, true, true));
            Assert.Equal(SendPress.Resume, SendSession.Answer(SendSessionState.Paused, true, true, true));
            Assert.Equal(SendPress.Stop, SendSession.Answer(SendSessionState.Running, false, true, true));
        }

        // ── Item 6: carrying on does not ask again ───────────────────────────

        /// <summary>
        /// Noel, 2026-10-28 21:25 (Codex's request declined): the send was okayed
        /// once, so carrying on after a pause never asks the long-send question
        /// again, however long what is left is.
        /// </summary>
        [Fact]
        public void Carrying_on_after_a_pause_does_not_ask_again()
        {
            string host = ReadSource("JJFlexWpf/VoiceMessageSender.cs");
            string resume = Body(host, "private static void ResumePaused()");
            Assert.DoesNotContain("LengthAgreed", resume);
            Assert.DoesNotContain("ConfirmLongSend", resume);
            Assert.DoesNotContain("NeedsLengthWarning", resume);
            // And the pieces it goes to never ask either: only the doors do.
            foreach (string piece in new[]
                     {
                         "private static void StartVoiceMessagePiece(",
                         "private static void StartComputerPiece(",
                         "private static void BeginAndLaunch(",
                     })
                Assert.DoesNotContain("LengthAgreed", Body(host, piece));
        }

        // ── Item 7: no warning under the operator's own key ──────────────────

        /// <summary>
        /// Noel, 2026-10-28 21:25 (Codex's blocker declined): a send riding the
        /// operator's own key, push to talk or the lock, never puts the
        /// long-recording question up, because no dialog may take focus under
        /// his thumb. The route is read with the operator holding in every
        /// combination of the box, VOX and the radio's transmit report.
        /// </summary>
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public void A_send_riding_the_operators_key_never_asks_the_long_recording_question(
            bool ticked, bool voxOn, bool transmitting)
        {
            SendRoute route = VoxSendBox.Choose(ticked, voxOn, transmitting, operatorHolds: true);
            Assert.Equal(SendRoute.JoinTransmission, route);
            Assert.False(VoiceMessageSend.NeedsLengthWarning(20 * 60.0, route));
            // The positive control: the same length, not riding his key, asks.
            Assert.True(VoiceMessageSend.NeedsLengthWarning(20 * 60.0, SendRoute.Box));
        }

        // ── Item 3: Play from computer pauses on the speakers too ────────────

        /// <summary>
        /// Noel, 2026-10-28 21:20: one meaning for the key everywhere. Play
        /// pauses a recording playing on the speakers, Play again carries it
        /// on, Escape stops it. Pinned from source: the player is real audio,
        /// and driving it from a test would put sound on the operator's desk.
        /// </summary>
        [Fact]
        public void Play_from_computer_pauses_on_the_speakers_and_Escape_stops_it()
        {
            string controls = ReadSource("JJFlexWpf/RecorderControls.cs");
            string play = Body(controls, "public static async Task<RecorderOutcome> ToggleComputerPlayAsync(");
            int sendFirst = play.IndexOf("VoiceMessageSender.AnswerPlayFromComputer()", StringComparison.Ordinal);
            int speakers = play.IndexOf("PauseOrCarryOnSpeakers()", StringComparison.Ordinal);
            int engine = play.IndexOf("ComputerRecorder.TogglePlaybackAsync", StringComparison.Ordinal);
            Assert.True(sendFirst >= 0 && speakers > sendFirst && engine > speakers,
                "a send answers first, then a playing recording pauses, and only then may Play start one");

            string pause = Body(controls, "private static RecorderOutcome PauseOrCarryOnSpeakers()");
            Assert.Contains("RecordingPlayer.Resume()", pause);
            Assert.Contains("RecordingPlayer.Pause()", pause);
            Assert.DoesNotContain("Stop", pause);

            string main = ReadSource("JJFlexWpf/MainWindow.xaml.cs");
            int sendEscape = main.IndexOf("if (rawKey == Key.Escape && VoiceMessageSender.InFlight)", StringComparison.Ordinal);
            int speakerEscape = main.IndexOf("if (rawKey == Key.Escape && RecorderControls.StopSpeakersOnEscape())", StringComparison.Ordinal);
            Assert.True(sendEscape >= 0 && speakerEscape > sendEscape, "a send to the air is stopped first");
        }

        // ── helpers ─────────────────────────────────────────────────────────

        internal static string Body(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, "signature not found: " + signature);
            int open = src.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src.Substring(start, i - start + 1);
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
        }

        internal static string ReadSource(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative);
            Assert.True(File.Exists(path), "source not found: " + path);
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
