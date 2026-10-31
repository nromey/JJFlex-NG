#nullable enable
using System;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A recording paused on the speakers carries on the way a press now would
    /// go (#835; Noel, 2026-10-30 05:56: <i>"Shouldn't it track if send is on?
    /// If you've paused and you turn send on, say you didn't realize you did,
    /// it should send"</i>). On the bench a speaker playback paused before the
    /// box was ticked carried on through the speakers and the radio never keyed.
    /// The session's place is driven here; the doors live in the UI assembly
    /// and play real audio, so they are pinned from source, as
    /// <see cref="SendFixRoundTests"/> does.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class SendFromPauseTests
    {
        [Fact]
        public void A_send_from_a_paused_place_holds_that_place_and_counts_what_is_left()
        {
            var session = new SendSession(170.0, 42.5);
            Assert.Equal(42.5, session.HeldAtSeconds, 3);
            Assert.Equal(127.5, session.RemainingSeconds, 3);
            Assert.Equal(SendSessionState.Running, session.State);
        }

        [Theory]
        [InlineData(-3.0, 0.0)]
        [InlineData(500.0, 170.0)]
        public void A_start_outside_the_recording_is_held_inside_it(double start, double held)
        {
            Assert.Equal(held, new SendSession(170.0, start).HeldAtSeconds, 3);
        }

        [Fact]
        public void Play_from_computer_sends_a_paused_speaker_playback_before_it_would_carry_it_on()
        {
            string controls = SendFixRoundTests.ReadSource("JJFlexWpf/RecorderControls.cs");
            string play = SendFixRoundTests.Body(controls, "public static async Task<RecorderOutcome> ToggleComputerPlayAsync(");
            int sendFirst = play.IndexOf("VoiceMessageSender.AnswerPlayFromComputer()", StringComparison.Ordinal);
            int pausedSend = play.IndexOf("VoiceMessageSender.SendPausedSpeakerPlayback(rig, VoiceMessageSender.RouteFor(rig))", StringComparison.Ordinal);
            int speakers = play.IndexOf("PauseOrCarryOnSpeakersAsync()", StringComparison.Ordinal);
            Assert.True(sendFirst >= 0 && pausedSend > sendFirst && speakers > pausedSend,
                "a send in hand answers first, then a paused playback goes to the air if a press now would send, and only then does it carry on through the speakers");
        }

        [Fact]
        public void Only_a_recorded_QSO_is_sent_from_a_pause_and_the_speakers_stop_only_once_it_will_send()
        {
            string sender = SendFixRoundTests.ReadSource("JJFlexWpf/VoiceMessageSender.cs");
            string body = SendFixRoundTests.Body(sender, "internal static bool SendPausedSpeakerPlayback(");
            int asBefore = body.IndexOf("route == SendRoute.AsBefore", StringComparison.Ordinal);
            int qso = body.IndexOf("IsRecordedQso(path)", StringComparison.Ordinal);
            int send = body.IndexOf("SendComputerRecording(rig, path, route, at, beforeSending: RecordingPlayer.StopWithoutSpeaking)", StringComparison.Ordinal);
            Assert.True(asBefore >= 0 && qso > asBefore && send > qso,
                "the route and the folder are asked first, and the send starts from the paused place, handed the speakers' stop");
            Assert.DoesNotContain("RecordingPlayer.StopWithoutSpeaking();", body);
        }

        /// <summary>
        /// Noel, 2026-10-31: saying No to the long-send warning "shouldn't lose
        /// the pause for sure". The speakers stop after the warning and every
        /// refusal, just before the piece starts, so anything that turns the
        /// send away leaves the recording paused where it was.
        /// </summary>
        [Fact]
        public void A_declined_warning_or_a_refusal_leaves_the_speakers_paused()
        {
            string sender = SendFixRoundTests.ReadSource("JJFlexWpf/VoiceMessageSender.cs");
            string body = SendFixRoundTests.Body(sender, "private static void SendComputerRecording(FlexBase rig, string path, SendRoute route, double fromSeconds,");
            int admitted = body.IndexOf("if (!Admitted(rig, ptt, info, route)) return;", StringComparison.Ordinal);
            int length = body.IndexOf("if (!LengthAgreed(", StringComparison.Ordinal);
            int load = body.IndexOf("TxAudioFile.TryLoadPieceInto(", StringComparison.Ordinal);
            int stop = body.IndexOf("beforeSending?.Invoke();", StringComparison.Ordinal);
            int start = body.IndexOf("StartComputerPiece(rig, info, loaded, route);", StringComparison.Ordinal);
            Assert.True(admitted >= 0 && length > admitted && load > length && stop > load && start > stop,
                "admission, the warning and the load all come before the speakers stop, and the piece starts right after");
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(body, @"beforeSending\?\.Invoke\(\)").Count);
        }

        [Fact]
        public void Recorded_QSOs_sends_a_paused_preview_through_the_same_door()
        {
            string dialog = SendFixRoundTests.ReadSource("JJFlexWpf/Dialogs/RecordedQsosDialog.cs");
            Assert.Contains("VoiceMessageSender.SendPausedSpeakerPlayback(rig, route)", dialog);
            string press = SendFixRoundTests.Body(dialog, "private async Task PlayOrPauseAsync()");
            Assert.Contains("case EnterAct.SendFromPause:", press);
            Assert.Contains("doors.SendPausedPlayback(_rig!, route!.Value)", press);
        }
    }
}
