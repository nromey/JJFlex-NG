using System.Collections.Generic;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The box send (#835, the VOX send box): with the box ticked and VOX on, a send
    /// keys the radio itself, sends the whole recording, unkeys, and hands back
    /// to VOX. Driven by a fake radio and a number for the clock, as
    /// <see cref="VoiceMessageSendTests"/> drives the voice send it extends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is asserted is the contract the rulings make (Noel, 2026-10-27):
    /// the box keys even when the radio reads transmitting from VOX's own tail
    /// (15:28); push to talk during the send takes over, so the recording
    /// stops and the send does NOT unkey (15:26); every other exit unkeys
    /// because the send keyed; a recording that can only start once keyed
    /// (the radio's slice playback) starts after the key, never before; and
    /// the run-long watchdog applies to every box send.
    /// </para>
    /// <para>
    /// What is NOT proved: the VOX lease (held by the caller and tested in
    /// <c>VoxPauseOwnerTests</c>), that the radio drops a VOX-held carrier when
    /// VOX is switched off, or what the air hears. Those are the bench's.
    /// </para>
    /// </remarks>
    public sealed class BoxSendTests
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
            public double FileSeconds { get; set; } = 8.0;
            public void FileStart() { Calls.Add("FileStart"); FilePlaying = true; }
            public void FileStartForVox() { Calls.Add("FileStartForVox"); FilePlaying = true; }
            public void FileStop() { Calls.Add("FileStop"); FilePlaying = false; }
            public bool VoxOn { get; set; } = true;
        }

        private static (FakeRig Rig, VoiceMessageSend Send) Keyed(KeyedSendOptions? options = null)
        {
            var rig = new FakeRig();
            var send = VoiceMessageSend.BeginKeyed(rig, "CQ", 0, options ?? new KeyedSendOptions());
            rig.RadioSaysTransmitting = true;   // the Mox echo
            send.Tick(100);
            return (rig, send);
        }

        [Fact]
        public void WithVoxOnTheBoxKeysTheRadioRatherThanLettingVoxDoIt()
        {
            var rig = new FakeRig { VoxOn = true };
            var send = VoiceMessageSend.BeginKeyed(rig, "CQ", 0, new KeyedSendOptions());

            Assert.True(send.WeKeyed);
            Assert.True(send.Boxed);
            Assert.False(send.ViaVox);
            Assert.Equal(new[] { "FileStart", "Transmit=true" }, rig.Calls);
            Assert.DoesNotContain("FileStartForVox", rig.Calls);
        }

        /// <summary>
        /// 15:28: a radio held up only by VOX counts as unkeyed for the box, so
        /// the send takes the key itself rather than riding on VOX, which would
        /// drop in the recording's first pause.
        /// </summary>
        [Fact]
        public void ARadioReadingTransmittingFromVoxIsStillKeyedByTheBox()
        {
            var rig = new FakeRig { RadioSaysTransmitting = true };
            var send = VoiceMessageSend.BeginKeyed(rig, "CQ", 0, new KeyedSendOptions());

            Assert.True(send.WeKeyed);
            Assert.Contains("Transmit=true", rig.Calls);
        }

        [Fact]
        public void PlayedToTheEndTheBoxUnkeysAfterGivingTheMicrophoneBack()
        {
            var (rig, send) = Keyed();
            rig.FileReachedEnd = true;
            send.Tick(8200);

            Assert.Equal(VoiceMessageOutcome.Sent, send.Outcome);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop", "Transmit=false" }, rig.Calls);
        }

        /// <summary>
        /// The whole point of the box: VOX dropping out between words is not
        /// possible because nothing is waiting on VOX. A send keeps going while
        /// the radio stays keyed, however long the pause in the recording.
        /// </summary>
        [Fact]
        public void APauseInTheRecordingDoesNotEndTheSend()
        {
            var (rig, send) = Keyed();
            for (long t = 150; t < 7000; t += 50) send.Tick(t);

            Assert.False(send.Finished);
            Assert.DoesNotContain("Transmit=false", rig.Calls);
        }

        /// <summary>
        /// 15:26, PTT takes over: the recording stops, the microphone comes
        /// back, and the carrier stays up under the operator's thumb. The send
        /// must never drop it.
        /// </summary>
        [Fact]
        public void PushToTalkDuringTheSendStopsTheRecordingAndLeavesTheKeyToTheOperator()
        {
            bool operatorKeyed = false;
            var (rig, send) = Keyed(new KeyedSendOptions { TakenBy = () => operatorKeyed ? BoxTakeover.Controller : BoxTakeover.None });

            operatorKeyed = true;
            send.Tick(2000);

            Assert.Equal(VoiceMessageOutcome.TakenOver, send.Outcome);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop" }, rig.Calls);
            Assert.DoesNotContain("Transmit=false", rig.Calls);
        }

        /// <summary>
        /// The takeover is checked before anything else, including the
        /// recording's end: a press that lands on the last tick still leaves
        /// the operator holding a carrier rather than having it dropped.
        /// </summary>
        [Fact]
        public void TheOperatorKeyingOnTheLastTickStillTakesOver()
        {
            bool operatorKeyed = false;
            var (rig, send) = Keyed(new KeyedSendOptions { TakenBy = () => operatorKeyed ? BoxTakeover.Controller : BoxTakeover.None });
            operatorKeyed = true;
            rig.FileReachedEnd = true;
            send.Tick(8200);

            Assert.Equal(VoiceMessageOutcome.TakenOver, send.Outcome);
            Assert.DoesNotContain("Transmit=false", rig.Calls);
        }

        [Fact]
        public void EscapeStopsTheBoxSendAndUnkeysBecauseTheSendKeyed()
        {
            var (rig, send) = Keyed();
            send.Stop();

            Assert.Equal(VoiceMessageOutcome.Stopped, send.Outcome);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop", "Transmit=false" }, rig.Calls);
        }

        /// <summary>The hard kill or a reflected-power cut dropped the carrier. Nothing re-keys.</summary>
        [Fact]
        public void WhenSomethingElseUnkeysTheBoxSendDoesNotTouchTheKey()
        {
            var (rig, send) = Keyed();
            rig.RadioSaysTransmitting = false;
            send.Tick(3000);

            Assert.Equal(VoiceMessageOutcome.Interrupted, send.Outcome);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop" }, rig.Calls);
        }

        /// <summary>
        /// The kill disarms the player before the radio's unkey echo arrives
        /// (TransmitKillSwitch.DisarmSources). Seen with the carrier still up,
        /// the send unkeys too: it keyed, and nothing must be left keyed with
        /// no source.
        /// </summary>
        [Fact]
        public void APlayerReleasedUnderTheSendWithTheCarrierUpUnkeys()
        {
            var (rig, send) = Keyed();
            rig.FilePlaying = false;
            send.Tick(3000);

            Assert.Equal(VoiceMessageOutcome.SourceStopped, send.Outcome);
            Assert.Equal("Transmit=false", rig.Calls[^1]);
        }

        [Fact]
        public void ARadioThatNeverKeysEndsTheBoxSendAndWithdrawsTheAsk()
        {
            var rig = new FakeRig();
            var send = VoiceMessageSend.BeginKeyed(rig, "CQ", 0, new KeyedSendOptions());
            send.Tick(VoiceMessageSend.KeyUpWaitMs + 50);

            Assert.Equal(VoiceMessageOutcome.RadioDidNotKey, send.Outcome);
            Assert.Equal(new[] { "FileStart", "Transmit=true", "FileStop", "Transmit=false" }, rig.Calls);
        }

        /// <summary>
        /// The brief's length limit: a recording that never reports its end is
        /// a keyed radio with a dead source. The box gets the same watchdog as
        /// every voice message, measured from the key.
        /// </summary>
        [Fact]
        public void ABoxSendThatRunsPastItsLengthIsStoppedAndUnkeyed()
        {
            var (rig, send) = Keyed();
            long limit = 100 + (long)(rig.FileSeconds * 1000) + VoiceMessageSend.RunLongGraceMs;
            send.Tick(limit);
            Assert.False(send.Finished);
            send.Tick(limit + 50);

            Assert.Equal(VoiceMessageOutcome.RanLong, send.Outcome);
            Assert.Equal("Transmit=false", rig.Calls[^1]);
        }

        // ── A recording that starts only once keyed: the radio's slice playback ──

        /// <summary>
        /// Started unkeyed, the radio's playback goes to the speakers, so the
        /// box must key FIRST and start it only after the key is confirmed and
        /// the key-down's own edge has been handled.
        /// </summary>
        [Fact]
        public void ASourceThatStartsOnTheKeyIsNotStartedUntilTheRadioIsTransmitting()
        {
            var rig = new FakeRig();
            var send = VoiceMessageSend.BeginKeyed(rig, "Slice A", 0,
                new KeyedSendOptions { StartSourceWhenKeyed = true });
            Assert.Equal(new[] { "Transmit=true" }, rig.Calls);

            send.Tick(50);
            Assert.DoesNotContain("FileStart", rig.Calls);

            rig.RadioSaysTransmitting = true;
            send.Tick(100);
            Assert.DoesNotContain("FileStart", rig.Calls);   // the settle

            send.Tick(100 + VoiceMessageSend.SourceStartSettleMs);
            Assert.Equal(new[] { "Transmit=true", "FileStart" }, rig.Calls);
        }

        [Fact]
        public void ASourceThatFailsToStartUnkeysAtOnce()
        {
            bool failed = false;
            var rig = new FakeRig();
            var send = VoiceMessageSend.BeginKeyed(rig, "Slice A", 0,
                new KeyedSendOptions { StartSourceWhenKeyed = true, SourceFailed = () => failed });
            rig.RadioSaysTransmitting = true;
            send.Tick(100);
            send.Tick(250);                 // started
            rig.FilePlaying = false;        // the radio has not confirmed
            send.Tick(300);
            Assert.False(send.Finished);

            failed = true;
            send.Tick(350);

            Assert.Equal(VoiceMessageOutcome.SourceStopped, send.Outcome);
            Assert.Equal("Transmit=false", rig.Calls[^1]);
        }

        [Fact]
        public void ASourceThatNeverReportsPlayingUnkeysAfterTheStartWait()
        {
            var rig = new FakeRig();
            var send = VoiceMessageSend.BeginKeyed(rig, "Slice A", 0,
                new KeyedSendOptions { StartSourceWhenKeyed = true });
            rig.RadioSaysTransmitting = true;
            send.Tick(100);
            send.Tick(250);
            rig.FilePlaying = false;
            send.Tick(250 + VoiceMessageSend.SourceStartWaitMs);
            Assert.False(send.Finished);
            send.Tick(250 + VoiceMessageSend.SourceStartWaitMs + 50);

            Assert.Equal(VoiceMessageOutcome.SourceStopped, send.Outcome);
            Assert.Equal("Transmit=false", rig.Calls[^1]);
        }

        [Fact]
        public void ASlicePlaybackThatEndsOnItsOwnIsSentAndUnkeyed()
        {
            var rig = new FakeRig();
            var send = VoiceMessageSend.BeginKeyed(rig, "Slice A", 0,
                new KeyedSendOptions { StartSourceWhenKeyed = true });
            rig.RadioSaysTransmitting = true;
            send.Tick(100);
            send.Tick(250);
            send.Tick(300);                 // reports playing
            rig.FileReachedEnd = true;
            send.Tick(5000);

            Assert.Equal(VoiceMessageOutcome.Sent, send.Outcome);
            Assert.Equal(new[] { "Transmit=true", "FileStart", "FileStop", "Transmit=false" }, rig.Calls);
        }

        /// <summary>
        /// The run-long clock for a source that starts on the key runs from
        /// when it reported playing, not from the press, so the slice
        /// recorder's confirmation round trip is not charged to the recording.
        /// </summary>
        [Fact]
        public void TheRunLongClockForASliceStartsWhenItReportsPlaying()
        {
            var rig = new FakeRig { FileSeconds = 4.0 };
            var send = VoiceMessageSend.BeginKeyed(rig, "Slice A", 0,
                new KeyedSendOptions { StartSourceWhenKeyed = true });
            rig.RadioSaysTransmitting = true;
            send.Tick(100);
            send.Tick(250);                 // FileStart; the fake reports playing at once
            rig.FilePlaying = false;
            send.Tick(2000);                // still confirming
            rig.FilePlaying = true;
            send.Tick(3000);                // reports playing here
            send.Tick(3000 + 4000 + VoiceMessageSend.RunLongGraceMs);
            Assert.False(send.Finished);
            send.Tick(3000 + 4000 + VoiceMessageSend.RunLongGraceMs + 50);
            Assert.Equal(VoiceMessageOutcome.RanLong, send.Outcome);
        }

        /// <summary>
        /// The existing sends are untouched by the box: unticked, a voice
        /// message with VOX on still lets VOX key it (#151, 2026-10-05).
        /// </summary>
        [Fact]
        public void UntickedTheVoiceMessageWithVoxOnStillLetsVoxKeyIt()
        {
            var rig = new FakeRig { VoxOn = true };
            var send = VoiceMessageSend.Begin(rig, "CQ", 0);

            Assert.True(send.ViaVox);
            Assert.False(send.Boxed);
            Assert.Equal(new[] { "FileStartForVox" }, rig.Calls);
        }
    }
}
