using System.Collections.Generic;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// How long a send may key the radio (#835, round 2): the warning over 180
    /// seconds, the pause at ten minutes, and Play pausing and carrying on.
    /// Driven by a fake radio and a number for the clock, as
    /// <see cref="BoxSendTests"/> drives the box send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rulings (Noel, 2026-10-27): 20:48, warn over 180 seconds and pause
    /// at 600; 20:50, <i>"it's got to unkey so you can ID, play keys it and stop
    /// stops it"</i>; 20:51, Play pauses and carries on the way it always does.
    /// Asserted here: the warning boundary; the pause unkeys only what the send
    /// keyed; a pause holds the place and the next piece starts from it; Stop
    /// while paused touches nothing; the radio keying and unkeying during a
    /// pause resumes nothing and keys nothing; and the run-long watchdog and
    /// the pause cannot fight.
    /// </para>
    /// <para>
    /// What is NOT proved here: that the host loads the next piece from the
    /// held place (<c>SendPieceLoaderTests</c> in JJFlexWpf.Tests proves the
    /// loader starts at the asked-for sample), the warning dialog itself, the
    /// VOX lease being given back at the pause and taken again at the resume
    /// (the host's, through the same calls a fresh box send makes), and
    /// anything the radio does. Those are the bench's.
    /// </para>
    /// </remarks>
    public sealed class SendPauseTests
    {
        private sealed class FakeRig : IVoiceMessageRig
        {
            public readonly List<string> Calls = new();
            public bool RadioSaysTransmitting;
            public bool Transmit
            {
                get => RadioSaysTransmitting;
                set => Calls.Add(value ? "Transmit=true" : "Transmit=false");
            }
            public bool FilePlaying { get; set; }
            public bool FileReachedEnd { get; set; }
            public double FileSeconds { get; set; } = 900.0;
            public double FilePlayedSeconds { get; set; }
            public void FileStart() { Calls.Add("FileStart"); FilePlaying = true; }
            public void FileStartForVox() { Calls.Add("FileStartForVox"); FilePlaying = true; }
            public void FileStop() { Calls.Add("FileStop"); FilePlaying = false; }
            public bool VoxOn { get; set; }
        }

        /// <summary>A rig that cannot say where its playback is: the interface's default.</summary>
        private sealed class PlacelessRig : IVoiceMessageRig
        {
            public readonly List<string> Calls = new();
            public bool RadioSaysTransmitting;
            public bool Transmit
            {
                get => RadioSaysTransmitting;
                set => Calls.Add(value ? "Transmit=true" : "Transmit=false");
            }
            public bool FilePlaying { get; set; }
            public bool FileReachedEnd { get; set; }
            public double FileSeconds { get; set; } = 900.0;
            public void FileStart() { Calls.Add("FileStart"); FilePlaying = true; }
            public void FileStartForVox() { Calls.Add("FileStartForVox"); FilePlaying = true; }
            public void FileStop() { Calls.Add("FileStop"); FilePlaying = false; }
            public bool VoxOn { get; set; }
        }

        /// <summary>A box send, keyed and confirmed at t = 100 ms.</summary>
        private static (FakeRig Rig, VoiceMessageSend Send) BoxKeyed(double fileSeconds = 900.0)
        {
            var rig = new FakeRig { FileSeconds = fileSeconds };
            var send = VoiceMessageSend.BeginKeyed(rig, "QSO", 0, new KeyedSendOptions());
            rig.RadioSaysTransmitting = true;
            send.Tick(100);
            return (rig, send);
        }

        /// <summary>Play the recording in step with the clock, from <paramref name="fromMs"/> to <paramref name="toMs"/>.</summary>
        private static void PlayAlong(FakeRig rig, VoiceMessageSend send, long fromMs, long toMs, long keyedAtMs = 100)
        {
            for (long t = fromMs; t <= toMs && !send.Finished; t += 1000)
            {
                rig.FilePlayedSeconds = (t - keyedAtMs) / 1000.0;
                send.Tick(t);
            }
        }

        // ── The warning, before anything keys ─────────────────────────────

        [Theory]
        [InlineData(180.0, false)]
        [InlineData(180.4, false)]   // announced as "3 minutes"
        [InlineData(180.5, true)]    // announced as "3 minutes 1 second"
        [InlineData(181.0, true)]
        [InlineData(15.0, false)]
        [InlineData(1200.0, true)]
        public void ARecordingLongerThanThreeMinutesWarnsBeforeKeying(double seconds, bool warns)
        {
            Assert.Equal(warns, VoiceMessageSend.NeedsLengthWarning(seconds, SendRoute.Box));
            Assert.Equal(warns, VoiceMessageSend.NeedsLengthWarning(seconds, SendRoute.AsBefore));
        }

        [Fact]
        public void ASendJoiningTheOperatorsOwnTransmissionKeysNothingSoDoesNotAsk()
        {
            Assert.False(VoiceMessageSend.NeedsLengthWarning(1200.0, SendRoute.JoinTransmission));
        }

        // ── The ten-minute pause ──────────────────────────────────────────

        [Fact]
        public void ABoxSendPausesAtTenMinutesAndUnkeysTheRadioItKeyed()
        {
            var (rig, send) = BoxKeyed();
            PlayAlong(rig, send, 1100, 599_100);
            Assert.False(send.Finished);

            PlayAlong(rig, send, 600_100, 601_100);

            Assert.Equal(VoiceMessageOutcome.Paused, send.Outcome);
            Assert.True(send.PausedForId);
            Assert.Equal(600.0, send.PausedAtSeconds, 3);
            // The recording is released before the key is touched, and the
            // send unkeys because it keyed.
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop", "Transmit=false" }, rig.Calls);
        }

        [Fact]
        public void ASendRidingTheOperatorsKeyPausesAtTenMinutesAndNeverUnkeysUnderThem()
        {
            // Push to talk or the lock: the radio is transmitting at the press,
            // so the send joins and never touches the key.
            var rig = new FakeRig { RadioSaysTransmitting = true };
            var send = VoiceMessageSend.Begin(rig, "QSO", 0);
            Assert.False(send.WeKeyed);
            PlayAlong(rig, send, 1000, 601_000, keyedAtMs: 0);

            Assert.Equal(VoiceMessageOutcome.Paused, send.Outcome);
            Assert.True(send.PausedForId);
            Assert.Equal(new[] { "FileStart", "FileStop" }, rig.Calls);
        }

        [Fact]
        public void AVoiceMessageTheSendKeyedItselfPausesAndUnkeys()
        {
            // VOX off, receiving: #151's keyed branch.
            var rig = new FakeRig { VoxOn = false };
            var send = VoiceMessageSend.Begin(rig, "Long call", 0);
            rig.RadioSaysTransmitting = true;
            send.Tick(100);
            PlayAlong(rig, send, 1100, 601_100);

            Assert.Equal(VoiceMessageOutcome.Paused, send.Outcome);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop", "Transmit=false" }, rig.Calls);
        }

        [Fact]
        public void AVoiceMessageVoxKeyedPausesWithoutTouchingTheKey()
        {
            // Box unticked, VOX on: VOX keys it (#151) and VOX drops it.
            var rig = new FakeRig { VoxOn = true };
            var send = VoiceMessageSend.Begin(rig, "Long call", 0);
            Assert.True(send.ViaVox);
            rig.RadioSaysTransmitting = true;
            send.Tick(100);
            PlayAlong(rig, send, 1100, 601_100);

            Assert.Equal(VoiceMessageOutcome.Paused, send.Outcome);
            Assert.Equal(new[] { "FileStartForVox", "FileStop" }, rig.Calls);
        }

        [Fact]
        public void AFewSecondsLeftAtTenMinutesStillPauses_UnderOneSecondFinishes()
        {
            var (longRig, longer) = BoxKeyed(fileSeconds: 605.0);
            PlayAlong(longRig, longer, 1100, 601_100);
            Assert.Equal(VoiceMessageOutcome.Paused, longer.Outcome);

            var (rig, send) = BoxKeyed(fileSeconds: 600.5);
            PlayAlong(rig, send, 1100, 600_100);
            Assert.False(send.Finished);
            rig.FileReachedEnd = true;
            send.Tick(600_700);
            Assert.Equal(VoiceMessageOutcome.Sent, send.Outcome);
        }

        [Fact]
        public void ARecordingUnderTenMinutesNeverPauses()
        {
            var (rig, send) = BoxKeyed(fileSeconds: 300.0);
            PlayAlong(rig, send, 1100, 300_100);
            Assert.False(send.Finished);
            rig.FileReachedEnd = true;
            send.Tick(300_200);
            Assert.Equal(VoiceMessageOutcome.Sent, send.Outcome);
        }

        // ── The pause and the run-long watchdog cannot fight ──────────────

        [Fact]
        public void ThePauseComesBeforeTheWatchdogCouldReadItAsRunningLong()
        {
            // The recording plays a touch slower than the wall clock, as an
            // audio clock can: the pause still lands, never RanLong.
            var (rig, send) = BoxKeyed();
            for (long t = 1100; !send.Finished && t < 700_000; t += 500)
            {
                rig.FilePlayedSeconds = (t - 100) / 1000.0 * 0.998;
                send.Tick(t);
            }
            Assert.Equal(VoiceMessageOutcome.Paused, send.Outcome);
        }

        [Fact]
        public void AStuckSendIsStillCaught_WithinTenMinutesNotTheWholeRecording()
        {
            // The player stopped advancing: nothing pacing it. Before round 2
            // this ran to the whole length plus grace (fifteen minutes for a
            // fifteen-minute piece); now the longest any piece can run is ten
            // minutes of recording, so the watchdog fires just past that.
            var (rig, send) = BoxKeyed(fileSeconds: 900.0);
            rig.FilePlayedSeconds = 12.0;
            send.Tick(600_000);
            Assert.False(send.Finished);
            long budgetMs = (long)((VoiceMessageSend.PauseAfterSeconds + VoiceMessageSend.PauseEndMarginSeconds) * 1000)
                            + VoiceMessageSend.RunLongGraceMs;
            send.Tick(100 + budgetMs + 50);
            Assert.Equal(VoiceMessageOutcome.RanLong, send.Outcome);
            Assert.Equal("Transmit=false", rig.Calls[^1]);
        }

        [Fact]
        public void ARigThatCannotSayItsPlaceNeverPausesAndIsStillBoundedByTheWatchdog()
        {
            // The radio's slice playback: at most two minutes, so it never
            // reaches ten, and a rig that reports no place gets no pause.
            var rig = new PlacelessRig { FileSeconds = 900.0 };
            var send = VoiceMessageSend.BeginKeyed(rig, "slice", 0, new KeyedSendOptions());
            rig.RadioSaysTransmitting = true;
            send.Tick(100);
            send.Tick(600_100);
            Assert.False(send.Finished);
            send.Tick(605_000);
            Assert.Equal(VoiceMessageOutcome.RanLong, send.Outcome);
        }

        // ── Play pauses ───────────────────────────────────────────────────

        [Fact]
        public void PlayPausesASendTheAppKeyed_HoldingTheExactPlace()
        {
            var (rig, send) = BoxKeyed();
            PlayAlong(rig, send, 1100, 42_100);
            send.Pause();

            Assert.Equal(VoiceMessageOutcome.Paused, send.Outcome);
            Assert.False(send.PausedForId);
            Assert.Equal(42.0, send.PausedAtSeconds, 3);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop", "Transmit=false" }, rig.Calls);
        }

        [Fact]
        public void PlayPausesASendRidingTheOperatorsKeyWithoutUnkeying()
        {
            var rig = new FakeRig { RadioSaysTransmitting = true };
            var send = VoiceMessageSend.Begin(rig, "QSO", 0);
            rig.FilePlayedSeconds = 30.0;
            send.Pause();

            Assert.Equal(VoiceMessageOutcome.Paused, send.Outcome);
            Assert.Equal(new[] { "FileStart", "FileStop" }, rig.Calls);
        }

        [Fact]
        public void PushToTalkDuringASendStillStopsIt_ItDoesNotPause()
        {
            // 15:26 is unchanged by round 2: PTT takes over, and it is a stop.
            var rig = new FakeRig();
            BoxTakeover by = BoxTakeover.None;
            var send = VoiceMessageSend.BeginKeyed(rig, "QSO", 0, new KeyedSendOptions { TakenBy = () => by });
            rig.RadioSaysTransmitting = true;
            send.Tick(100);
            by = BoxTakeover.Controller;
            send.Tick(200);
            Assert.Equal(VoiceMessageOutcome.TakenOver, send.Outcome);
        }

        // ── The session: the held place across pieces ─────────────────────

        /// <summary>
        /// The held place carries across pieces, and so does the ten-minute
        /// count: it runs across the operator's OWN pauses, and only the
        /// automatic ten-minute pause starts it again (Noel, 2026-10-28 21:25),
        /// so a quick pause of his cannot push the identification reminder out
        /// to nineteen minutes.
        /// </summary>
        [Fact]
        public void APausedSendCarriesOnFromTheHeldPlace_AndOnlyTheIdPauseRestartsTheTenMinutes()
        {
            var session = new SendSession(totalSeconds: 1500.0);
            var (rig, first) = BoxKeyed();
            session.Begin(first);
            PlayAlong(rig, first, 1100, 601_100);
            Assert.Equal(SendSessionState.Paused, session.Settle());
            Assert.Equal(600.0, session.HeldAtSeconds, 3);
            Assert.True(session.PausedForId);
            Assert.True(session.PausedPieceKeyed);
            Assert.Equal(0.0, session.SentSinceIdSeconds, 3);   // the ID pause starts the count again

            // Play: the host loads the next piece FROM 600 s and starts a fresh
            // box send on it, which keys exactly as the first did.
            var rig2 = new FakeRig { FileSeconds = 900.0 };
            var second = VoiceMessageSend.BeginKeyed(rig2, "QSO", 700_000, new KeyedSendOptions());
            session.Begin(second);
            Assert.Equal(SendSessionState.Running, session.State);
            Assert.Equal(new[] { "FileStart", "Transmit=true" }, rig2.Calls);
            rig2.RadioSaysTransmitting = true;
            second.Tick(700_100);

            // The operator pauses after four minutes of the second piece.
            PlayAlong(rig2, second, 701_100, 940_100, keyedAtMs: 700_100);
            second.Pause();
            session.Settle();
            Assert.Equal(840.0, session.HeldAtSeconds, 3);
            Assert.False(session.PausedForId);
            Assert.Equal(240.0, session.SentSinceIdSeconds, 3);   // his pause does not reset it

            // The third piece pauses for identification after SIX minutes, not
            // ten: four were already sent since the last identification.
            var rig3 = new FakeRig { FileSeconds = 660.0 };
            var third = VoiceMessageSend.BeginKeyed(rig3, "QSO", 1_000_000, new KeyedSendOptions());
            session.Begin(third);
            rig3.RadioSaysTransmitting = true;
            third.Tick(1_000_100);
            PlayAlong(rig3, third, 1_001_100, 1_359_100, keyedAtMs: 1_000_100);
            Assert.False(third.Finished);   // 359 s into this piece, 599 since the last identification
            PlayAlong(rig3, third, 1_360_100, 1_360_100, keyedAtMs: 1_000_100);
            Assert.Equal(VoiceMessageOutcome.Paused, third.Outcome);
            Assert.True(third.PausedForId);
            Assert.Equal(SendSessionState.Paused, session.Settle());
            Assert.Equal(1200.0, session.HeldAtSeconds, 3);
            Assert.Equal(0.0, session.SentSinceIdSeconds, 3);
            Assert.Equal(3, session.Pieces);
        }

        [Fact]
        public void TwoOperatorPausesInARowStillAddUp()
        {
            var session = new SendSession(totalSeconds: 1500.0);
            var (rig, first) = BoxKeyed();
            session.Begin(first);
            PlayAlong(rig, first, 1100, 300_100);
            first.Pause();
            session.Settle();
            Assert.Equal(300.0, session.SentSinceIdSeconds, 1);

            var rig2 = new FakeRig { FileSeconds = 1200.0 };
            var second = VoiceMessageSend.BeginKeyed(rig2, "QSO", 400_000, new KeyedSendOptions());
            session.Begin(second);
            rig2.RadioSaysTransmitting = true;
            second.Tick(400_100);
            PlayAlong(rig2, second, 401_100, 600_100, keyedAtMs: 400_100);
            second.Pause();
            session.Settle();
            Assert.Equal(500.0, session.SentSinceIdSeconds, 1);

            // Pausing before anything keyed sends nothing and changes nothing.
            session.PauseBeforeKeying();
            Assert.Equal(500.0, session.SentSinceIdSeconds, 1);
        }

        [Fact]
        public void StopWhilePausedEndsTheSendAndTouchesNothing()
        {
            var session = new SendSession(1200.0);
            var (rig, piece) = BoxKeyed();
            session.Begin(piece);
            PlayAlong(rig, piece, 1100, 601_100);
            session.Settle();
            int callsAtPause = rig.Calls.Count;

            session.Stop();   // Stop, or Escape: the host calls the same thing

            Assert.Equal(SendSessionState.Ended, session.State);
            Assert.Equal(callsAtPause, rig.Calls.Count);
            Assert.Null(session.Piece);
        }

        [Fact]
        public void PushToTalkDuringAPauseKeysNothingAndResumesNothing()
        {
            var session = new SendSession(1200.0);
            var (rig, piece) = BoxKeyed();
            session.Begin(piece);
            PlayAlong(rig, piece, 1100, 601_100);
            session.Settle();
            int callsAtPause = rig.Calls.Count;

            // The operator keys to identify, and lets go. Nothing of the send
            // is running to see it: the finished piece ignores ticks.
            rig.RadioSaysTransmitting = true;
            piece.Tick(610_000);
            rig.RadioSaysTransmitting = false;
            piece.Tick(620_000);

            Assert.Equal(SendSessionState.Paused, session.Settle());
            Assert.Equal(callsAtPause, rig.Calls.Count);
            Assert.DoesNotContain("Transmit=true", rig.Calls.GetRange(2, rig.Calls.Count - 2));
        }

        [Fact]
        public void EveryOtherEndingEndsTheWholeSend()
        {
            var session = new SendSession(300.0);
            var (rig, piece) = BoxKeyed(fileSeconds: 300.0);
            session.Begin(piece);
            rig.FileReachedEnd = true;
            piece.Tick(300_200);
            Assert.Equal(SendSessionState.Ended, session.Settle());
            Assert.Throws<System.InvalidOperationException>(() =>
                session.Begin(VoiceMessageSend.BeginKeyed(new FakeRig(), "QSO", 0, new KeyedSendOptions())));
        }

        [Fact]
        public void PausedBeforeKeyingHoldsTheSamePlace()
        {
            var session = new SendSession(1200.0);
            session.PauseBeforeKeying();
            Assert.Equal(SendSessionState.Paused, session.State);
            Assert.Equal(0.0, session.HeldAtSeconds);
            Assert.False(session.PausedPieceKeyed);
        }

        // ── What a press means ────────────────────────────────────────────

        [Theory]
        [InlineData(null, true, true, true, SendPress.StartNew)]
        [InlineData(SendSessionState.Running, true, true, true, SendPress.Pause)]
        [InlineData(SendSessionState.Running, false, true, true, SendPress.Stop)]     // another door stops it
        [InlineData(SendSessionState.Running, true, false, true, SendPress.Stop)]     // a door that does not pause stops
        [InlineData(SendSessionState.Running, true, true, false, SendPress.Stop)]     // slice playback cannot hold a place
        [InlineData(SendSessionState.Paused, true, true, true, SendPress.Resume)]
        [InlineData(SendSessionState.Paused, true, false, true, SendPress.Resume)]    // a paused message carries on on its key
        [InlineData(SendSessionState.Paused, false, true, true, SendPress.Stop)]
        [InlineData(SendSessionState.Ended, true, true, true, SendPress.StartNew)]
        public void APressMeansWhatTheRulingsSay(SendSessionState? state, bool sameDoor, bool pressPauses,
            bool canHoldPlace, SendPress expected)
        {
            Assert.Equal(expected, SendSession.Answer(state, sameDoor, pressPauses, canHoldPlace));
        }

        [Theory]
        [InlineData(true, SendRoute.Box, ResumeWay.Box)]
        [InlineData(false, SendRoute.Box, ResumeWay.Box)]
        [InlineData(true, SendRoute.JoinTransmission, ResumeWay.Join)]
        [InlineData(false, SendRoute.JoinTransmission, ResumeWay.Join)]
        [InlineData(true, SendRoute.AsBefore, ResumeWay.AsVoiceMessage)]
        [InlineData(false, SendRoute.AsBefore, ResumeWay.NeedsKey)]
        public void AResumeGoesTheWayAFreshPressWould_AndPlayNeverKeysOnItsOwn(bool voiceMessage, SendRoute route,
            ResumeWay expected)
        {
            Assert.Equal(expected, SendSession.ResumeAs(voiceMessage, route));
        }
    }
}
