using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track H, item E: every stage before the encoder is protected
    /// (#1074), so a throwing stage no longer aborts the microphone stream; the
    /// gate resets open at the real key-down rather than at a gap in the stream
    /// (#1075); and the unkey does not wait on the capture stream.
    /// </summary>
    public class TxChainSafetyTests
    {
        private const int Floats = 960;

        private sealed class Source : ITxInputSource
        {
            public bool Engaged { get; set; }
            public bool Idle { get; set; } = true;
            public bool BypassesConditioning { get; set; }
            public bool Throw;
            public int Calls;
            public void Process(float[] b, int n, uint rate)
            {
                Calls++;
                if (Throw) { b[0] = 99f; throw new InvalidOperationException("source broke"); }
            }
        }

        private static float[] Mic(float v = 0.25f) => Enumerable.Repeat(v, Floats).ToArray();

        [Fact]
        public void A_conditioner_that_throws_sends_the_audio_it_was_given_and_is_skipped_until_the_key_changes()
        {
            bool keyed = true;
            int conditionerCalls = 0;
            float lastSent = float.NaN;
            var p = new TxFramePipeline
            {
                KeySource = () => keyed,
                Conditioner = (b, n, r) =>
                {
                    conditionerCalls++;
                    for (int i = 0; i < n; i++) b[i] = 5f;   // half a rewrite...
                    throw new InvalidOperationException("chain broke"); // ...then a throw
                },
                Encode = b => { lastSent = b[0]; return new byte[1]; },
            };

            Assert.True(p.Emit(Mic(), Floats, 48000));      // the stream is NOT stopped
            Assert.Equal(0.25f, lastSent);                    // unprocessed, never the half-rewrite, never silence
            Assert.Equal(1, conditionerCalls);
            for (int i = 0; i < 10; i++) Assert.True(p.Emit(Mic(), Floats, 48000));
            Assert.Equal(1, conditionerCalls);                // skipped for the rest of the over
            Assert.Equal(1L, p.StageFailures);
            Assert.Equal(11L, p.FramesWithStageSkipped);
            Assert.Equal(11L, p.FramesSent);

            keyed = false;                                    // the unkey: try it again
            Assert.True(p.Emit(Mic(), Floats, 48000));
            Assert.Equal(2, conditionerCalls);
        }

        [Fact]
        public void A_source_that_throws_lets_the_microphone_through_and_the_conditioner_still_runs()
        {
            var src = new Source { Throw = true, Engaged = true };
            int conditioned = 0;
            float lastSent = float.NaN;
            var p = new TxFramePipeline
            {
                Source = src,
                Conditioner = (b, n, r) => conditioned++,
                Encode = b => { lastSent = b[0]; return new byte[1]; },
            };
            Assert.True(p.Emit(Mic(), Floats, 48000));
            Assert.Equal(0.25f, lastSent);
            Assert.Equal(1, conditioned);
            Assert.True(p.Emit(Mic(), Floats, 48000));
            Assert.Equal(1, src.Calls);
            Assert.Equal(2, conditioned);
            p.ResetStageFaults();
            p.Emit(Mic(), Floats, 48000);
            Assert.Equal(2, src.Calls);
        }

        [Fact]
        public void The_encoder_is_still_the_one_stage_that_stops_the_stream()
        {
            var p = new TxFramePipeline
            {
                Conditioner = (b, n, r) => throw new InvalidOperationException("chain broke"),
                Encode = b => throw new InvalidOperationException("encoder broke"),
            };
            // False is what makes the capture callback return paAbort.
            Assert.False(p.Emit(Mic(), Floats, 48000));
            Assert.Equal(1L, p.EncodeFailures);
            Assert.Equal(1L, p.StageFailures);
        }

        [Fact]
        public void A_throwing_key_source_reads_as_keyed()
        {
            int calls = 0;
            var p = new TxFramePipeline
            {
                KeySource = () => throw new InvalidOperationException("radio gone"),
                Conditioner = (b, n, r) => { calls++; throw new InvalidOperationException("x"); },
                Encode = b => new byte[1],
            };
            p.Emit(Mic(), Floats, 48000);
            p.Emit(Mic(), Floats, 48000);
            Assert.Equal(1, calls);
        }

        // ── The gate's reset (#1075) ──

        private static TxAudioConditioner GateClosedOnSilence(Func<bool>? keySource)
        {
            var cond = new TxAudioConditioner { KeySource = keySource };
            cond.Gate.Enabled = true;
            cond.Gate.ThresholdDb = -20f;
            cond.Gate.HoldMs = 50;
            cond.Gate.ReleaseMs = 50;
            // Half a second of near silence: the gate closes.
            for (int i = 0; i < 50; i++) cond.Process(Mic(0.0001f), Floats, 48000);
            Assert.True(cond.Gate.CurrentGainDb < -10, "the gate never closed, so this proves nothing");
            return cond;
        }

        [Fact]
        public void The_gate_resets_open_at_the_real_key_down()
        {
            bool keyed = false;
            var cond = GateClosedOnSilence(() => keyed);
            long before = cond.Gate.Resets;
            keyed = true;
            cond.Process(Mic(0.0001f), Floats, 48000);
            Assert.Equal(before + 1, cond.Gate.Resets);
            Assert.True(cond.Gate.CurrentGainDb > -1, "the first buffer of the over met a closed gate");
        }

        [Fact]
        public void With_a_key_state_a_gap_in_the_stream_no_longer_resets_the_gate()
        {
            var cond = GateClosedOnSilence(() => false);
            long before = cond.Gate.Resets;
            Thread.Sleep(150);
            cond.Process(Mic(0.0001f), Floats, 48000);
            Assert.Equal(before, cond.Gate.Resets);
            Assert.True(cond.Gate.CurrentGainDb < -10);
        }

        [Fact]
        public void With_no_key_state_a_gap_still_resets_it_as_before()
        {
            var cond = GateClosedOnSilence(null);
            long before = cond.Gate.Resets;
            Thread.Sleep(150);
            cond.Process(Mic(0.0001f), Floats, 48000);
            Assert.Equal(before + 1, cond.Gate.Resets);
        }

        // ── The unkey does not wait on the capture stream ──

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        [Fact]
        public void The_unkey_only_queues_the_radio_command_and_touches_no_capture_stream()
        {
            // Read, not run: the Transmit setter is the unkey. It must hand Mox
            // to the command queue and never stop, wait for or join anything on
            // the audio side, so a stalled capture callback cannot hold a radio
            // keyed. (Since #565 the capture is not even stopped at the unkey.)
            string text = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.cs"));
            int start = text.IndexOf("        public bool Transmit\r\n", StringComparison.Ordinal);
            if (start < 0) start = text.IndexOf("        public bool Transmit\n", StringComparison.Ordinal);
            Assert.True(start >= 0, "the Transmit property moved; find the unkey again");
            int end = text.IndexOf("/// <summary>", start, StringComparison.Ordinal);
            string setter = text.Substring(start, end - start);
            Assert.Contains("q.Enqueue", setter);
            Assert.Contains("theRadio.Mox = value", setter);
            foreach (var banned in new[] { "opusInputChannel", "StopAudio", "stopOpusInputChannel", ".Wait(", ".Join(", "PortAudio", "Thread.Sleep" })
                Assert.DoesNotContain(banned, setter);
        }
    }
}
