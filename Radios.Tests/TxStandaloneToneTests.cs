using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The test tone transmits in a session whose microphone could not be
    /// opened (#210): the session gets a transmit path of its own, the gate
    /// sends only the tone down it, and its teardown stops, clears and
    /// disposes in that order before the transmit stream closes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What only the bench can confirm</b>, and nothing here claims to:
    /// with an input device that cannot open, the tone armed and the radio
    /// keyed into the dummy load, SC_MIC on the radio reads the armed level.
    /// </para>
    /// <para>
    /// The lifecycle is tested on <see cref="TxStandaloneSource"/> with a fake
    /// encode, the way <see cref="TxFramePipeline"/>'s remarks invite, because
    /// a test host cannot build a real Opus encoder without libopus. The
    /// FlexBase wiring lives in <c>remoteAudioProc</c>, which needs a radio, so
    /// it is pinned by reading the source, as
    /// <c>TransmitGateArmedSourceTests</c> pins the loop.
    /// </para>
    /// </remarks>
    public sealed class TxStandaloneToneTests
    {
        private const uint Rate = 24000;
        private const int PerFrame = 240;

        private sealed class FakeEncoder : IDisposable
        {
            public int Disposals;
            public Action OnDispose;
            public void Dispose() { Disposals++; OnDispose?.Invoke(); }
        }

        private sealed class Rig
        {
            public int Sent;
            public readonly FakeEncoder Encoder = new FakeEncoder();
            public readonly TxToneGenerator Tone = new TxToneGenerator();
            public readonly LufsMeter Meter = new LufsMeter();
            public readonly TxAudioProcessorCallback Conditioner = (b, n, r) => { };
            public TxStandaloneSource Source;

            public Rig(uint rate = Rate, int perFrame = PerFrame)
            {
                Source = new TxStandaloneSource(b => new byte[] { 1 }, Encoder, rate, perFrame,
                    Tone, Conditioner, Meter, d => Interlocked.Increment(ref Sent));
            }
        }

        // ── The standalone path ─────────────────────────────────────────

        [Fact]
        public void The_pipeline_carries_exactly_the_parts_it_was_handed_and_no_others()
        {
            var rig = new Rig();
            TxFramePipeline p = rig.Source.Pipeline;
            Assert.Same(rig.Tone, p.Source);
            Assert.Same(rig.Conditioner, p.Conditioner);
            Assert.Same(rig.Meter, p.Meter);
            Assert.NotNull(p.Encode);
            Assert.NotNull(p.Handler);
            Assert.Equal(Rate, rig.Source.SampleRate);
            Assert.Equal(PerFrame, rig.Source.SamplesPerFrame);
            rig.Source.Dispose();
        }

        [Fact]
        public void It_sends_frames_on_its_own_clock_and_nothing_after_Stop()
        {
            var rig = new Rig();
            try
            {
                Assert.True(rig.Source.Start());
                Assert.True(rig.Source.Start(), "a second start while running is a yes, not a refusal");
                Thread.Sleep(150);
                rig.Source.Stop();
                Assert.False(rig.Source.Running);
                int atStop = Volatile.Read(ref rig.Sent);
                Thread.Sleep(60);
                Assert.True(atStop > 0, "the standalone path sent nothing in 150 ms");
                Assert.Equal(atStop, Volatile.Read(ref rig.Sent));
            }
            finally { rig.Source.Dispose(); }
        }

        [Fact]
        public void Disposal_stops_the_clock_then_clears_the_encode_step_then_disposes_the_encoder()
        {
            var rig = new Rig();
            bool runningAtEncoderDispose = true;
            bool encodeClearedAtEncoderDispose = false;
            rig.Encoder.OnDispose = () =>
            {
                runningAtEncoderDispose = rig.Source.Running;
                encodeClearedAtEncoderDispose = rig.Source.Pipeline.Encode == null;
            };

            Assert.True(rig.Source.Start());
            Thread.Sleep(50);
            rig.Source.Dispose();

            Assert.Equal(1, rig.Encoder.Disposals);
            Assert.False(runningAtEncoderDispose, "the encoder was disposed while the clock was still pumping into it");
            Assert.True(encodeClearedAtEncoderDispose, "the encoder was disposed while the pipeline could still reach it");

            int atDispose = Volatile.Read(ref rig.Sent);
            Thread.Sleep(60);
            Assert.Equal(atDispose, Volatile.Read(ref rig.Sent));
            Assert.True(rig.Source.Disposed);
        }

        [Fact]
        public void A_disposed_path_never_starts_again_and_disposes_its_encoder_once()
        {
            var rig = new Rig();
            rig.Source.Dispose();
            rig.Source.Dispose();
            Assert.Equal(1, rig.Encoder.Disposals);
            Assert.False(rig.Source.Start(), "a disposed standalone path started again");
            Assert.False(rig.Source.Running);
        }

        [Fact]
        public void Disposal_before_any_start_still_disposes_the_encoder()
        {
            var rig = new Rig();
            rig.Source.Dispose();
            Assert.Equal(1, rig.Encoder.Disposals);
            Assert.Null(rig.Source.Pipeline.Encode);
        }

        [Fact]
        public void Only_the_five_Opus_rates_map_and_a_rate_Opus_cannot_encode_is_refused_before_any_encoder_exists()
        {
            foreach (uint hz in new uint[] { 8000, 12000, 16000, 24000, 48000 })
                Assert.True(OpusEncodeProfile.TryGetSamplingRate(hz, out _), hz + " Hz should map");
            foreach (uint hz in new uint[] { 0, 800, 11025, 22050, 44100, 96000 })
                Assert.False(OpusEncodeProfile.TryGetSamplingRate(hz, out _), hz + " Hz must not map");

            // Refused at the rate check, so no libopus is needed to see it.
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TxStandaloneSource.Create(44100, OpusEncodeProfile.Shipped, null, null, null, d => { }));
        }

        [Fact]
        public void The_rate_table_is_the_one_Audio_Open_uses()
        {
            string audio = System.IO.File.ReadAllText(System.IO.Path.Combine(FlexBasePartials.RepoRoot(),
                "JJPortaudio", "JJPortaudio", "Audio.cs"));
            Assert.Contains("OpusEncodeProfile.TryGetSamplingRate(openRate, out", audio, StringComparison.Ordinal);
            Assert.DoesNotContain("case 24000: oRate", audio, StringComparison.Ordinal);
        }

        [Fact]
        public void A_nonsense_geometry_is_refused_at_construction()
        {
            Assert.ThrowsAny<ArgumentException>(() =>
                new TxStandaloneSource(b => new byte[1], null, Rate, 0, null, null, null, d => { }));
            Assert.Throws<ArgumentNullException>(() =>
                new TxStandaloneSource(null, null, Rate, PerFrame, null, null, null, d => { }));
            Assert.Throws<ArgumentNullException>(() =>
                new TxStandaloneSource(b => new byte[1], null, Rate, PerFrame, null, null, null, null));
        }

        // ── The gate's rule ─────────────────────────────────────────────

        [Fact]
        public void The_standalone_path_starts_only_for_the_tone()
        {
            // Not running: only an engaged tone starts it.
            Assert.True(FlexBase.StandaloneMayCarry(running: false, toneEngaged: true, toneIdle: false, otherSourceEngaged: false));
            Assert.True(FlexBase.StandaloneMayCarry(false, true, false, otherSourceEngaged: true)); // the tone wins the mux
            Assert.False(FlexBase.StandaloneMayCarry(false, false, true, otherSourceEngaged: true)); // a voice message alone: no
            Assert.False(FlexBase.StandaloneMayCarry(false, false, true, false));
            Assert.False(FlexBase.StandaloneMayCarry(false, false, false, false)); // a ramp is never a reason to start
        }

        [Fact]
        public void Once_running_it_finishes_the_tones_ramp_but_never_carries_another_source()
        {
            Assert.True(FlexBase.StandaloneMayCarry(running: true, toneEngaged: true, toneIdle: false, otherSourceEngaged: false));
            Assert.True(FlexBase.StandaloneMayCarry(true, toneEngaged: false, toneIdle: false, otherSourceEngaged: false)); // release ramp
            Assert.False(FlexBase.StandaloneMayCarry(true, false, false, otherSourceEngaged: true)); // ramp, but a message would be heard
            Assert.False(FlexBase.StandaloneMayCarry(true, false, toneIdle: true, otherSourceEngaged: false)); // done
        }

        [Fact]
        public void The_sentence_is_owed_once_per_connection()
        {
            var first = new object();
            var second = new object();
            Assert.False(FlexBase.ToneWithoutMicrophoneOwed(null, null), "no connection, nothing to say it about");
            Assert.True(FlexBase.ToneWithoutMicrophoneOwed(null, first));
            Assert.False(FlexBase.ToneWithoutMicrophoneOwed(first, first), "said twice on one connection");
            Assert.True(FlexBase.ToneWithoutMicrophoneOwed(first, second), "a new connection is owed it again");
        }

        // ── On a rig with no radio ──────────────────────────────────────

        private static T Field<T>(FlexBase rig, string name) =>
            (T)typeof(FlexBase).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rig)!;

        private static void SetField(FlexBase rig, string name, object value) =>
            typeof(FlexBase).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(rig, value);

        private static object Call(FlexBase rig, string name) =>
            typeof(FlexBase).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(rig, null)!;

        [Fact]
        public void With_no_transmit_stream_the_gate_refuses_the_tone_even_with_a_standalone_path_built()
        {
            FlexBase rig = null;
            var built = new Rig();
            try
            {
                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                Field<TxToneGenerator>(rig, "txToneGen").Start();
                SetField(rig, "_standaloneTx", built.Source);
                Assert.False(Field<bool>(rig, "opusInputAvailable"));
                Assert.Null(Field<object>(rig, "opusInputChannel"));

                Assert.False((bool)Call(rig, "startSelfClockedTxInput"),
                    "the gate let a tone out with no transmit stream");
                Assert.False(built.Source.Running);
                Assert.Equal(0, Volatile.Read(ref built.Sent));
            }
            finally
            {
                try { rig?.Dispose(); } catch { }
                built.Source.Dispose();
            }
        }

        [Fact]
        public void The_stop_every_branch_already_calls_stops_the_standalone_path_too()
        {
            FlexBase rig = null;
            var built = new Rig();
            try
            {
                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                SetField(rig, "_standaloneTx", built.Source);
                Assert.True(built.Source.Start());

                Call(rig, "stopSelfClockedTxInput");
                Assert.False(built.Source.Running, "stopSelfClockedTxInput left the no-microphone tone pumping");
            }
            finally
            {
                try { rig?.Dispose(); } catch { }
                built.Source.Dispose();
            }
        }

        [Fact]
        public void The_teardown_disposes_the_standalone_path_and_lets_it_go()
        {
            FlexBase rig = null;
            var built = new Rig();
            try
            {
                rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                SetField(rig, "_standaloneTx", built.Source);
                Assert.True(built.Source.Start());

                typeof(FlexBase).GetMethod("DisposeStandaloneTx", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(rig, new object[] { "a test" });

                Assert.Null(Field<object>(rig, "_standaloneTx"));
                Assert.True(built.Source.Disposed);
                Assert.False(built.Source.Running);
                Assert.Equal(1, built.Encoder.Disposals);
            }
            finally
            {
                try { rig?.Dispose(); } catch { }
                built.Source.Dispose();
            }
        }

        // ── The wiring, read as source ──────────────────────────────────

        private static string Src() => FlexBasePartials.JoinedText();

        private static string MethodBody(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "Could not find '" + signature + "'; re-anchor this rule rather than letting it pass on nothing.");
            int open = src.IndexOf('{', at + signature.Length);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0)
                    return src.Substring(at, i - at + 1);
            }
            Assert.Fail("Unbalanced braces after '" + signature + "'.");
            return string.Empty;
        }

        private static int Count(string text, string what)
        {
            int n = 0;
            for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + 1, StringComparison.Ordinal)) n++;
            return n;
        }

        [Fact]
        public void The_standalone_path_is_built_only_where_the_microphone_failed_to_open()
        {
            string src = Src();
            Assert.Equal(1, Count(src, "TxStandaloneSource.Create("));
            Assert.Equal(1, Count(src, "_standaloneTx = BuildStandaloneTx();"));

            string proc = MethodBody(src, "private void remoteAudioProc(long run)");
            int open = proc.IndexOf("opusInputAvailable = opusInputChannel.PortAudioStream.OpenOpus(", StringComparison.Ordinal);
            Assert.True(open > 0, "the microphone's open has moved; re-anchor this rule");
            int failed = proc.IndexOf("if (!opusInputAvailable)", open, StringComparison.Ordinal);
            // The success branch's first comment marks where the failure branch ends.
            int opened = proc.IndexOf("Always log what the transmit stream actually opened as", failed, StringComparison.Ordinal);
            int build = proc.IndexOf("_standaloneTx = BuildStandaloneTx();", StringComparison.Ordinal);
            Assert.True(failed > open && build > failed && build < opened,
                "the standalone path is built somewhere other than the microphone's failed open — "
                + "a session whose microphone opened must never have one");
        }

        [Fact]
        public void The_gate_sends_a_failed_microphone_to_the_standalone_path_and_never_to_the_failed_stream()
        {
            string src = Src();
            string start = MethodBody(src, "private bool startSelfClockedTxInput()");
            int noStream = start.IndexOf("if (opusInputChannel == null) return false;", StringComparison.Ordinal);
            int route = start.IndexOf("if (!opusInputAvailable) return startStandaloneToneTx();", StringComparison.Ordinal);
            int capture = start.IndexOf("stream.StartSelfClockedTx()", StringComparison.Ordinal);
            Assert.True(noStream > 0, "the gate no longer refuses with no transmit stream");
            Assert.True(route > noStream, "a failed microphone is not routed to the standalone path after the stream check");
            Assert.True(capture > route, "the failed stream's own self-clock can be reached before the route");

            string standalone = MethodBody(src, "private bool startStandaloneToneTx()");
            Assert.DoesNotContain("StartSelfClockedTx", standalone, StringComparison.Ordinal);
            Assert.DoesNotContain("PortAudioStream", standalone, StringComparison.Ordinal);
            Assert.Contains("opusInputChannel?.TXOpusChannel == null) return false;", standalone, StringComparison.Ordinal);
            Assert.Contains("StandaloneMayCarry(standalone.Running, txToneGen.Engaged, txToneGen.Idle, txFilePlayer.Engaged)",
                standalone, StringComparison.Ordinal);
        }

        [Fact]
        public void The_stop_reaches_the_standalone_path()
        {
            string stop = MethodBody(Src(), "private void stopSelfClockedTxInput()");
            Assert.Contains("_standaloneTx?.Stop();", stop, StringComparison.Ordinal);
        }

        [Fact]
        public void The_teardown_closes_the_standalone_path_before_the_transmit_stream_on_every_route()
        {
            string proc = MethodBody(Src(), "private void remoteAudioProc(long run)");
            int catchAt = proc.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
            int done = proc.IndexOf("remoteDone:", StringComparison.Ordinal);
            Assert.True(catchAt > 0 && done > catchAt, "the catch no longer falls through to remoteDone");

            int dispose = proc.IndexOf("DisposeStandaloneTx(", done, StringComparison.Ordinal);
            int captureClose = proc.IndexOf("opusInputChannel.PortAudioStream.Close();", done, StringComparison.Ordinal);
            int streamClose = proc.IndexOf("opusInputChannel.TXOpusChannel.Close();", done, StringComparison.Ordinal);
            Assert.True(dispose > done, "the teardown does not close the standalone path");
            Assert.True(captureClose > dispose && streamClose > dispose,
                "the transmit stream can close while the standalone path is still pumping into it");
        }

        [Fact]
        public void The_standalone_path_uses_the_rigs_own_source_conditioner_and_meter()
        {
            string build = MethodBody(Src(), "private JJPortaudio.TxStandaloneSource BuildStandaloneTx()");
            Assert.Contains("TxStandaloneSource.Create(OpusTxSampleRateSetting, OpusTxEncodeProfile,", build, StringComparison.Ordinal);
            Assert.Contains("TxInputSources, txConditioner.Process, txLufsMeter, sendOpusInput)", build, StringComparison.Ordinal);
            Assert.DoesNotContain("new JJPortaudio.TxAudioConditioner", build, StringComparison.Ordinal);
            Assert.DoesNotContain("new JJPortaudio.LufsMeter", build, StringComparison.Ordinal);
            Assert.DoesNotContain("TxInputSourceMux", build, StringComparison.Ordinal);
        }

        [Fact]
        public void The_sentence_is_queued_once_per_connection_and_keyed_with_its_own_subject()
        {
            string say = MethodBody(Src(), "private void SayToneWithoutMicrophoneOnce()");
            Assert.Contains("if (!ToneWithoutMicrophoneOwed(_toneWithoutMicSaidFor, connection)) return;", say, StringComparison.Ordinal);
            Assert.Contains("if (!SuppressSpeech)", say, StringComparison.Ordinal);
            Assert.Contains("tone_without_microphone\"), VerbosityLevel.Terse, false,", say, StringComparison.Ordinal);
            Assert.Contains("subject: Speech.SpeechSubject.ToneWithoutMicrophone", say, StringComparison.Ordinal);

            string standalone = MethodBody(Src(), "private bool startStandaloneToneTx()");
            int startOk = standalone.IndexOf("if (started)", StringComparison.Ordinal);
            int say1 = standalone.IndexOf("SayToneWithoutMicrophoneOnce();", StringComparison.Ordinal);
            int running = standalone.IndexOf("if (standalone.Running) return true;", StringComparison.Ordinal);
            Assert.True(running > 0 && running < startOk && say1 > startOk,
                "the sentence is reachable from the polling path rather than only from a start that succeeded");
        }
    }
}
