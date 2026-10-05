using System;
using System.IO;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// An armed generated source must wait for key-down, and since #565 that
    /// is only true because the transmit gate parks the microphone capture
    /// while a source is engaged and the radio is receiving (Sprint 48
    /// Track A, #151).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The hazard, measured rather than argued.</b> <c>TxFileStart</c>'s
    /// comment says the recording goes out "immediately if transmitting,
    /// otherwise at the next key-down". The brief asked whether that survived
    /// #565, which keeps the capture running while receiving. It had not: the
    /// capture callback hands every buffer to <see cref="TxFramePipeline.Emit"/>,
    /// which hands it to the source mux, and an engaged
    /// <see cref="TxFilePlayer"/> replaces the microphone in it — on the
    /// capture's clock, into a radio that is not transmitting, consuming the
    /// recording before any key-down. The first test below is that mechanism
    /// in isolation: a pipeline fed as the capture callback feeds it drains
    /// an armed player with no transmit anywhere in sight. That is the
    /// positive control for the rule the second test pins.
    /// </para>
    /// <para>
    /// <b>The rule is in FlexBase and is read as source</b>, because the
    /// gate lives inside <c>remoteAudioProc</c>'s main loop, which needs a
    /// radio. What is pinned is narrow: the receive branch of the gate stops
    /// the capture channel when the source mux is engaged and starts it
    /// otherwise. If the gate is rewritten so that this test cannot find it,
    /// the test says so rather than passing on nothing.
    /// </para>
    /// </remarks>
    public sealed class TransmitGateArmedSourceTests
    {
        private const int Rate = 48000;
        private const int Frames = 480;          // one 10 ms frame
        private const int Floats = Frames * 2;   // interleaved stereo

        /// <summary>
        /// A pipeline fed the way the CAPTURE callback feeds it drains an
        /// engaged player. Nothing here asked the radio to transmit.
        /// </summary>
        [Fact]
        public void TheCapturePathConsumesAnArmedRecordingWithNoTransmitInvolved()
        {
            var player = new TxFilePlayer();
            var content = new float[Rate * 2];           // two seconds
            for (int i = 0; i < content.Length; i++) content[i] = 0.5f;
            player.Load(content, Rate, "cq");

            var tone = new TxToneGenerator();
            var mux = new TxInputSourceMux(tone, player);
            var pipeline = new TxFramePipeline
            {
                Source = mux,
                Encode = _ => new byte[1],       // a two-line fake, as TxFramePipeline's remarks invite
                Handler = _ => { },
            };

            player.Start();                                   // TxFileStart, radio receiving
            var micBuffer = new float[Floats];
            for (int frame = 0; frame < 250; frame++)        // 2.5 s of capture callbacks
            {
                Array.Fill(micBuffer, 0.1f);                 // room noise
                pipeline.Emit(micBuffer, Floats, Rate);
            }

            Assert.True(player.PlayedFrames > 0,
                "the capture path did not touch the armed player — if that is now true by design, the gate "
                + "rule this file pins is no longer needed and both tests should go together");
            Assert.True(player.ReachedEnd,
                "two seconds of recording survived 2.5 seconds of capture callbacks; the hazard this "
                + "test demonstrates would then be smaller than described");
            Assert.False(player.Engaged, "a drained player hands the microphone back on its own");
        }

        /// <summary>
        /// And once the player is drained there is nothing left for the next
        /// key-down: Start on a player that reached its end restarts from the
        /// top only if something CALLS Start again. The message key does not;
        /// it already did. This is the user-visible shape of the hazard — the
        /// CQ would have gone out to nobody and the key-down would carry the
        /// microphone.
        /// </summary>
        [Fact]
        public void ADrainedPlayerDoesNotPlayAgainOnItsOwn()
        {
            var player = new TxFilePlayer();
            player.Load(new float[Rate], Rate, "cq");
            player.Start();
            var buf = new float[Floats];
            for (int f = 0; f < 150; f++) player.Process(buf, Floats, Rate);   // 1.5 s, drained
            Assert.True(player.ReachedEnd);
            Assert.True(player.Idle);

            // Later: a stream restarts (key-down). Cold start on an idle
            // player is a pass-through; nothing plays.
            for (int f = 0; f < 10; f++) { Array.Fill(buf, 0.25f); player.Process(buf, Floats, Rate); }
            Assert.All(buf, s => Assert.Equal(0.25f, s, 5));
        }

        /// <summary>
        /// The gate's receive branch: engaged parks the capture, otherwise it
        /// starts it. Read from the source because the loop needs a radio.
        /// </summary>
        [Fact]
        public void TheReceiveBranchParksTheCaptureWhileASourceIsEngaged()
        {
            string path = Path.Combine(IntegrationPassTree.Root, "Radios", "FlexBase.cs");
            Assert.True(File.Exists(path), "Radios/FlexBase.cs has moved; this rule is scanning nothing.");
            string src = File.ReadAllText(path);

            // Anchor on the inner gate's distinctive comment, then look at
            // the else branch that follows it.
            int inner = src.IndexOf("INNER GATE: which producer feeds the encoder", StringComparison.Ordinal);
            Assert.True(inner > 0,
                "The transmit gate's INNER GATE comment is gone. The gate has been rewritten; re-anchor "
                + "this rule on whatever now decides what feeds the encoder, or it proves nothing.");
            int outer = src.IndexOf("OUTER GATE", inner, StringComparison.Ordinal);
            Assert.True(outer > inner, "No OUTER GATE after the inner one; the receive branch has moved.");

            // The receive branch runs until the opus receive polling comment.
            int end = src.IndexOf("opus receive polling", outer, StringComparison.Ordinal);
            Assert.True(end > outer, "The receive branch's end marker has moved.");
            string branch = src.Substring(outer, end - outer);

            Assert.Contains("if (TxInputSources.Engaged) stopOpusInputChannel();", branch);
            Assert.Contains("else startOpusInputChannel();", branch);
            Assert.DoesNotContain("if (TxInputSources.Idle) startOpusInputChannel();", branch);
        }
    }
}
