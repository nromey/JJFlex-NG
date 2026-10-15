using System.Collections.Generic;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The voice-message send's state machine (Sprint 48 Track A, #151),
    /// driven by a fake radio and a number for the clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is asserted is the contract the track report makes: a send from
    /// idle keys the radio and unkeys when the recording ends; a send while
    /// already transmitting leaves the key alone; every exit — played out,
    /// stopped, interrupted, radio never keyed, ran long — gives the
    /// microphone back; and an interrupt never re-keys.
    /// </para>
    /// <para>
    /// What is NOT proved: that the real player sets ReachedEnd, that the
    /// radio echoes Mox, or that the dispatcher timer ticks. Those are the
    /// engine's and the bench's. The fake records every call in order so a
    /// test can assert the SEQUENCE — FileStart before Transmit on the way
    /// up, FileStop before Transmit on the way down — because the order is
    /// the part a reader cannot see from the outcome alone.
    /// </para>
    /// </remarks>
    public sealed class VoiceMessageSendTests
    {
        private sealed class FakeRig : IVoiceMessageRig
        {
            public readonly List<string> Calls = new();
            private bool _transmit;

            /// <summary>What the radio reports. Set by the test to model the
            /// Mox echo; the setter below only records the ask.</summary>
            public bool RadioSaysTransmitting;

            public bool Transmit
            {
                get => RadioSaysTransmitting;
                set { _transmit = value; Calls.Add(value ? "Transmit=true" : "Transmit=false"); }
            }
            public bool LastTransmitAsk => _transmit;
            public bool FilePlaying { get; set; }
            public bool FileReachedEnd { get; set; }
            public double FileSeconds { get; set; } = 8.0;
            public void FileStart() { Calls.Add("FileStart"); FilePlaying = true; }
            public void FileStartForVox() { Calls.Add("FileStartForVox"); FilePlaying = true; }
            public void FileStop() { Calls.Add("FileStop"); FilePlaying = false; }
            public bool VoxOn { get; set; }
        }

        [Fact]
        public void FromIdleTheSendEngagesTheRecordingThenKeysTheRadio()
        {
            var rig = new FakeRig();
            var send = VoiceMessageSend.Begin(rig, "CQ", nowMs: 0);

            Assert.True(send.WeKeyed);
            Assert.Equal(new[] { "FileStart", "Transmit=true" }, rig.Calls);
            Assert.False(send.Finished);
        }

        [Fact]
        public void FromIdleWhenTheRecordingEndsTheRadioIsUnkeyedAndTheMicrophoneIsBack()
        {
            var rig = new FakeRig();
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            rig.RadioSaysTransmitting = true;           // the Mox echo
            send.Tick(100);
            Assert.False(send.Finished);

            rig.FileReachedEnd = true;                  // the audio thread ran off the end
            send.Tick(8200);

            Assert.True(send.Finished);
            Assert.Equal(VoiceMessageOutcome.Sent, send.Outcome);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop", "Transmit=false" }, rig.Calls);
        }

        [Fact]
        public void WhileAlreadyTransmittingTheSendNeverTouchesTheKey()
        {
            var rig = new FakeRig { RadioSaysTransmitting = true };   // operator holding PTT or VOX
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            Assert.False(send.WeKeyed);
            Assert.Equal(new[] { "FileStart" }, rig.Calls);

            rig.FileReachedEnd = true;
            send.Tick(8200);

            Assert.Equal(VoiceMessageOutcome.Sent, send.Outcome);
            Assert.Equal(new[] { "FileStart", "FileStop" }, rig.Calls);
            Assert.DoesNotContain("Transmit=false", rig.Calls);
        }

        [Fact]
        public void StoppingMidSendRestoresTheMicrophoneAndUnkeysWhenThisSendKeyed()
        {
            var rig = new FakeRig();
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            rig.RadioSaysTransmitting = true;
            send.Tick(100);

            send.Stop();

            Assert.Equal(VoiceMessageOutcome.Stopped, send.Outcome);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop", "Transmit=false" }, rig.Calls);
        }

        [Fact]
        public void StoppingMidSendLeavesTheKeyAloneWhenTheOperatorWasAlreadyTransmitting()
        {
            var rig = new FakeRig { RadioSaysTransmitting = true };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            send.Tick(100);

            send.Stop();

            Assert.Equal(VoiceMessageOutcome.Stopped, send.Outcome);
            Assert.Equal(new[] { "FileStart", "FileStop" }, rig.Calls);
        }

        /// <summary>
        /// PTT released, Escape through the controller, a kill, the radio's
        /// interlock: whoever unkeyed has decided the key. The send gives the
        /// microphone back and does NOT re-key or re-unkey.
        /// </summary>
        [Fact]
        public void WhenSomethingElseUnkeysTheSendRestoresTheMicrophoneAndDoesNotTouchTheKey()
        {
            var rig = new FakeRig();
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            rig.RadioSaysTransmitting = true;
            send.Tick(100);

            rig.RadioSaysTransmitting = false;          // Escape, PTT up, kill switch...
            send.Tick(3000);

            Assert.Equal(VoiceMessageOutcome.Interrupted, send.Outcome);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop" }, rig.Calls);
        }

        [Fact]
        public void IfTheRadioNeverKeysTheSendGivesUpAndRestoresTheMicrophone()
        {
            var rig = new FakeRig();
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);

            send.Tick(VoiceMessageSend.KeyUpWaitMs);        // still within patience
            Assert.False(send.Finished);
            send.Tick(VoiceMessageSend.KeyUpWaitMs + 1);    // one past it

            Assert.Equal(VoiceMessageOutcome.RadioDidNotKey, send.Outcome);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop", "Transmit=false" }, rig.Calls);
            Assert.False(rig.LastTransmitAsk, "the queued key-up must be withdrawn so a late echo does not leave the radio keyed");
        }

        /// <summary>
        /// The watchdog. Nothing pacing the player means ReachedEnd never
        /// comes; a keyed radio with a dead source must not stay keyed.
        /// </summary>
        [Fact]
        public void ARecordingThatNeverReportsItsEndIsStoppedPastItsLengthPlusGrace()
        {
            var rig = new FakeRig { FileSeconds = 8.0 };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            rig.RadioSaysTransmitting = true;

            long allowed = 8000 + VoiceMessageSend.KeyUpWaitMs + VoiceMessageSend.RunLongGraceMs;
            send.Tick(allowed);
            Assert.False(send.Finished);
            send.Tick(allowed + 1);

            Assert.Equal(VoiceMessageOutcome.RanLong, send.Outcome);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop", "Transmit=false" }, rig.Calls);
        }

        [Fact]
        public void FinishingIsIdempotent()
        {
            var rig = new FakeRig();
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            rig.RadioSaysTransmitting = true;
            send.Stop();
            int calls = rig.Calls.Count;

            send.Stop();
            send.Tick(99_999);

            Assert.Equal(calls, rig.Calls.Count);
            Assert.Equal(VoiceMessageOutcome.Stopped, send.Outcome);
        }

        // ── The VOX half (Sprint 48 Track E; #151, Noel 2026-10-05) ──────
        //
        // "If vox is on, which is how Don likes to do it, then it could use
        // that, otherwise it keys and unkeys." Every test above is the
        // "otherwise"; these are the VOX branch. The discriminator the fake
        // records is the CALL — FileStartForVox lets the recording reach a
        // receiving radio, FileStart arms it for the next key-down — because
        // that is the whole difference between a deliberate send and an
        // armed-and-idle source, and it is invisible in the outcome alone.

        [Fact]
        public void WithVoxOnTheSendStartsTheRecordingFlowingAndNeverTouchesTheKey()
        {
            var rig = new FakeRig { VoxOn = true };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);

            Assert.True(send.ViaVox);
            Assert.False(send.WeKeyed);
            Assert.Equal(new[] { "FileStartForVox" }, rig.Calls);
            Assert.DoesNotContain("Transmit=true", rig.Calls);
        }

        [Fact]
        public void WithVoxOffTheSendArmsAndKeysExactlyAsBefore()
        {
            // The control for the test above: the same rig with VOX off takes
            // the keyed road, call for call.
            var rig = new FakeRig { VoxOn = false };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            Assert.False(send.ViaVox);
            Assert.True(send.WeKeyed);
            Assert.Equal(new[] { "FileStart", "Transmit=true" }, rig.Calls);
        }

        [Fact]
        public void WithVoxOnButAlreadyTransmittingTheSendArmsAndLeavesTheKeyAlone()
        {
            // VOX on AND the operator is holding PTT, or VOX has already keyed
            // on their voice: the radio is transmitting, so the recording
            // simply replaces the microphone. The already-transmitting case
            // is unchanged by the ruling, by its own words.
            var rig = new FakeRig { VoxOn = true, RadioSaysTransmitting = true };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            Assert.False(send.ViaVox);
            Assert.False(send.WeKeyed);
            Assert.Equal(new[] { "FileStart" }, rig.Calls);
        }

        [Fact]
        public void AVoxSendEndsWhenTheRecordingEndsAndNeverUnkeys()
        {
            var rig = new FakeRig { VoxOn = true };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            rig.RadioSaysTransmitting = true;             // VOX tripped on the first word
            send.Tick(300);
            Assert.False(send.Finished);

            rig.FileReachedEnd = true;
            rig.FilePlaying = false;                      // the player ran off its end
            send.Tick(8200);

            Assert.Equal(VoiceMessageOutcome.Sent, send.Outcome);
            Assert.Equal(new[] { "FileStartForVox", "FileStop" }, rig.Calls);
            Assert.DoesNotContain("Transmit=false", rig.Calls);
        }

        [Fact]
        public void AVoxDropBetweenWordsIsNotAnInterrupt()
        {
            // VOX at a short delay drops between "CQ contest" and the call,
            // then keys again on the next word. The keyed send reads a dropped
            // transmit as somebody unkeying under it; the VOX send must not,
            // or every CQ would be cut at its first pause.
            var rig = new FakeRig { VoxOn = true };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            rig.RadioSaysTransmitting = true;
            send.Tick(300);
            rig.RadioSaysTransmitting = false;            // the pause
            send.Tick(1500);
            Assert.False(send.Finished, "a VOX drop between words ended the send");
            rig.RadioSaysTransmitting = true;             // the next word
            send.Tick(1800);
            Assert.False(send.Finished);

            rig.FileReachedEnd = true;
            rig.FilePlaying = false;
            send.Tick(8200);
            Assert.Equal(VoiceMessageOutcome.Sent, send.Outcome);
        }

        [Fact]
        public void IfVoxNeverKeysTheSendGivesUpSaysSoAndRestoresTheMicrophone()
        {
            var rig = new FakeRig { VoxOn = true };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);

            send.Tick(VoiceMessageSend.VoxKeyWaitMs);      // still within patience
            Assert.False(send.Finished);
            send.Tick(VoiceMessageSend.VoxKeyWaitMs + 1);

            Assert.Equal(VoiceMessageOutcome.VoxDidNotKey, send.Outcome);
            Assert.Equal(new[] { "FileStartForVox", "FileStop" }, rig.Calls);
            Assert.DoesNotContain("Transmit=false", rig.Calls);
        }

        [Fact]
        public void IfTheRecordingEndsBeforeVoxEverKeysThatIsAlsoVoxNotKeying()
        {
            // A recording shorter than the patience: no point waiting past it.
            var rig = new FakeRig { VoxOn = true, FileSeconds = 0.5 };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            rig.FileReachedEnd = true;
            rig.FilePlaying = false;
            send.Tick(600);
            Assert.Equal(VoiceMessageOutcome.VoxDidNotKey, send.Outcome);
        }

        [Fact]
        public void AKillThatReleasesThePlayerUnderAVoxSendIsAnInterruptAndNothingReKeys()
        {
            // #699: the hard kill drops the carrier AND disarms the source.
            // The VOX send sees the player go, not the carrier, and ends as
            // interrupted. Nothing here holds a key, so nothing re-keys.
            var rig = new FakeRig { VoxOn = true };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            rig.RadioSaysTransmitting = true;
            send.Tick(300);

            rig.RadioSaysTransmitting = false;            // the kill dropped the carrier...
            rig.FilePlaying = false;                      // ...and disarmed the player
            send.Tick(400);

            Assert.Equal(VoiceMessageOutcome.Interrupted, send.Outcome);
            Assert.Equal(new[] { "FileStartForVox", "FileStop" }, rig.Calls);
            Assert.DoesNotContain("Transmit=true", rig.Calls);
        }

        [Fact]
        public void AKillBeforeVoxEverKeysIsAlsoAnInterrupt()
        {
            var rig = new FakeRig { VoxOn = true };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            rig.FilePlaying = false;                      // disarmed before the radio keyed
            send.Tick(200);
            Assert.Equal(VoiceMessageOutcome.Interrupted, send.Outcome);
        }

        [Fact]
        public void AVoxSendThatNeverReportsItsEndIsStoppedPastItsLengthPlusGrace()
        {
            var rig = new FakeRig { VoxOn = true, FileSeconds = 8.0 };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            rig.RadioSaysTransmitting = true;

            long allowed = 8000 + VoiceMessageSend.VoxKeyWaitMs + VoiceMessageSend.RunLongGraceMs;
            send.Tick(allowed);
            Assert.False(send.Finished);
            send.Tick(allowed + 1);

            Assert.Equal(VoiceMessageOutcome.RanLong, send.Outcome);
            Assert.Equal(new[] { "FileStartForVox", "FileStop" }, rig.Calls);
        }

        [Fact]
        public void StoppingAVoxSendRestoresTheMicrophoneAndLeavesVoxToDropTheCarrier()
        {
            var rig = new FakeRig { VoxOn = true };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);
            rig.RadioSaysTransmitting = true;
            send.Tick(300);

            send.Stop();

            Assert.Equal(VoiceMessageOutcome.Stopped, send.Outcome);
            Assert.Equal(new[] { "FileStartForVox", "FileStop" }, rig.Calls);
        }
    }
}
