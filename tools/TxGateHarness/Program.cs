using System;
using JJPortaudio;

namespace TxGateHarness
{
    /// <summary>
    /// Numerical harness for the TX noise gate and conditioning chain
    /// (Track I). There is no radio and no listener at this bench, so the
    /// gate is proven the way the tone generator and the LUFS meter were:
    /// drive the REAL code (linked source, not a copy) with synthetic
    /// signals and assert the properties the design brief calls load-bearing:
    ///
    ///  * a gate that wakes up OPEN and does not clip word onsets,
    ///  * hold that bridges the pauses inside a sentence,
    ///  * closure that attenuates by RangeDb and NEVER to silence,
    ///  * residual = input - output, exactly, and residual silence when
    ///    the chain is bypassed (the pathway-is-live diagnostic).
    /// </summary>
    internal static class Program
    {
        private const uint Fs = 48000;
        private const int FrameFloats = 960; // 480 stereo frames per buffer, like the Opus path

        private static int failures;
        private static int checks;

        private static int Main()
        {
            Console.WriteLine("TX gate and conditioner numerical harness — Track I");
            Console.WriteLine();

            CheckFreshGateIsOpen();
            CheckOnsetNotClipped();
            CheckHoldBridgesIntraSentencePause();
            CheckLongSilenceClosesToRangeNotSilence();
            CheckRangeIsClampedShortOfSilence();
            CheckDisabledGateTouchesNothing();
            CheckResidualIsExactlyInputMinusOutput();
            CheckBypassedChainGivesSilentResidual();
            CheckSplitMonitorChannels();
            CheckMonitorOffNeverCalls();

            Console.WriteLine();
            Console.WriteLine($"{checks} checks, {failures} failed.");
            return failures;
        }

        // ---------------------------------------------------------------- //
        // Gate
        // ---------------------------------------------------------------- //

        /// <summary>A fresh gate (key-down) passes the very first buffer at
        /// unity — it must never eat the first syllable of a transmission.</summary>
        private static void CheckFreshGateIsOpen()
        {
            var gate = NewGate(thresholdDb: -45f);
            float[] buf = SineBuffer(997.0, -15f, FrameFloats);
            float[] reference = (float[])buf.Clone();
            gate.Process(buf, buf.Length, Fs);
            double energy = Energy(buf);
            double refEnergy = Energy(reference);
            Assert(energy > refEnergy * 0.99,
                $"fresh gate passes first buffer at unity ({Ratio(energy, refEnergy):F3} of input energy)");
            Assert(gate.IsOpen, "fresh gate reports open");
        }

        /// <summary>
        /// The commonest complaint about every gate ever shipped: slow attack
        /// eats the front of words. Close the gate with a long silence, then
        /// hit it with speech-level signal and measure how much of the first
        /// 20 ms survives. A slow gate passes ~range squared (~0.3%); ours
        /// must pass most of it.
        /// </summary>
        private static void CheckOnsetNotClipped()
        {
            var gate = NewGate(thresholdDb: -45f);

            // 1.5 s of quiet floor: past detector release + hold + gain release.
            FeedSine(gate, 997.0, -60f, seconds: 1.5);
            Assert(!gate.IsOpen, "gate is closed after 1.5 s below threshold");

            // Onset: 40 ms of -15 dB "speech", measured in 20 ms halves.
            int onsetFrames = (int)(0.040 * Fs);
            float[] onset = SineMono(997.0, -15f, onsetFrames);
            float[] processed = ProcessMono(gate, onset);

            int half = (int)(0.020 * Fs);
            double firstIn = Energy(onset, 0, half);
            double firstOut = Energy(processed, 0, half);
            double secondIn = Energy(onset, half, half);
            double secondOut = Energy(processed, half, half);

            Assert(firstOut > firstIn * 0.5,
                $"first 20 ms of a word onset keeps most of its energy ({Ratio(firstOut, firstIn):F3})");
            Assert(secondOut > secondIn * 0.90,
                $"second 20 ms is essentially untouched ({Ratio(secondOut, secondIn):F3})");
        }

        /// <summary>The pauses INSIDE a sentence (up to ~250 ms) must not
        /// close the gate — that chattering is what makes operators switch
        /// gates off.</summary>
        private static void CheckHoldBridgesIntraSentencePause()
        {
            var gate = NewGate(thresholdDb: -45f);
            FeedSine(gate, 997.0, -15f, seconds: 0.5); // establish speech

            // 250 ms pause at the room floor, tracking the minimum gain.
            int pauseFrames = (int)(0.250 * Fs);
            float[] pause = SineMono(200.0, -60f, pauseFrames);
            float[] processed = ProcessMono(gate, pause);
            double minGainSq = double.MaxValue;
            for (int i = 0; i < pauseFrames; i++)
            {
                double inSq = (double)pause[i] * pause[i];
                double outSq = (double)processed[i] * processed[i];
                if (inSq > 1e-12)
                {
                    double g = outSq / inSq;
                    if (g < minGainSq) minGainSq = g;
                }
            }
            // Gain stayed within ~1 dB of unity through the whole pause.
            Assert(minGainSq > 0.8,
                $"gate holds through a 250 ms mid-sentence pause (min gain {10 * Math.Log10(minGainSq) / 2:F2} dB)");
        }

        /// <summary>
        /// After a LONG silence the gate closes — but closed means attenuated
        /// by RangeDb, never muted. On SSB a fully-muted signal reads as a
        /// dropped contact; the floor must still be audible.
        /// </summary>
        private static void CheckLongSilenceClosesToRangeNotSilence()
        {
            var gate = NewGate(thresholdDb: -45f);
            FeedSine(gate, 997.0, -15f, seconds: 0.5); // open it
            FeedSine(gate, 200.0, -60f, seconds: 1.4); // then a long pause

            // Measure the steady-state gain on the next quiet stretch.
            int frames = (int)(0.100 * Fs);
            float[] quiet = SineMono(200.0, -60f, frames);
            float[] processed = ProcessMono(gate, quiet);
            double gainDb = 10.0 * Math.Log10(Energy(processed) / Energy(quiet));

            Assert(!gate.IsOpen, "gate reports closed after a 1.4 s pause");
            Assert(gainDb < -20.0 && gainDb > -30.0,
                $"closed gate attenuates by the 25 dB range ({gainDb:F1} dB), within the 20-30 dB window");
            Assert(gainDb > -40.0,
                $"closed gate is NOT silence ({gainDb:F1} dB — still audibly there)");
        }

        /// <summary>The range knob itself refuses silence: it clamps at 40 dB.</summary>
        private static void CheckRangeIsClampedShortOfSilence()
        {
            var gate = new TxNoiseGate();
            gate.RangeDb = 90f; // ask for silence
            Assert(gate.RangeDb <= 40f,
                $"range clamps at 40 dB, never to silence (asked 90, got {gate.RangeDb:F0})");
        }

        /// <summary>Disabled gate must not touch a sample — bypassed and
        /// gentle must be distinguishable, and they are, via the residual.</summary>
        private static void CheckDisabledGateTouchesNothing()
        {
            var gate = new TxNoiseGate { Enabled = false, ThresholdDb = -20f };
            float[] buf = SineBuffer(997.0, -50f, FrameFloats);
            float[] reference = (float[])buf.Clone();
            gate.Process(buf, buf.Length, Fs);
            bool identical = true;
            for (int i = 0; i < buf.Length; i++)
                if (buf[i] != reference[i]) { identical = false; break; }
            Assert(identical, "disabled gate passes samples bit-for-bit untouched");
        }

        // ---------------------------------------------------------------- //
        // Conditioner: residual arithmetic and monitor plumbing
        // ---------------------------------------------------------------- //

        /// <summary>removed = input − output, exactly. A fake NR stage that
        /// halves the signal must produce a residual of exactly half.</summary>
        private static void CheckResidualIsExactlyInputMinusOutput()
        {
            var cond = new TxAudioConditioner();
            cond.NoiseReducer = (buf, count, rate) =>
            {
                for (int i = 0; i < count; i++) buf[i] *= 0.5f;
            };
            cond.MonitorMode = TxAudioConditioner.MonitorModes.Residual;

            float[] captured = null;
            cond.MonitorSink = (buf, count, rate) => captured = (float[])buf.Clone();

            float[] input = SineBuffer(997.0, -15f, FrameFloats);
            float[] original = (float[])input.Clone();
            cond.Process(input, input.Length, Fs);

            Assert(captured != null, "residual monitor sink was called");
            bool exact = captured != null;
            if (exact)
            {
                for (int i = 0; i < FrameFloats; i++)
                {
                    float expected = original[i] - input[i]; // input[] is now the output
                    if (Math.Abs(captured[i] - expected) > 1e-9f) { exact = false; break; }
                }
            }
            Assert(exact, "residual is exactly input minus output");
            // And with a halving stage, that is exactly half the input.
            bool half = exact;
            if (half)
            {
                for (int i = 0; i < FrameFloats; i++)
                    if (Math.Abs(captured[i] - original[i] * 0.5f) > 1e-6f) { half = false; break; }
            }
            Assert(half, "a stage that removes half leaves exactly half in the residual");
        }

        /// <summary>
        /// The pathway-is-live diagnostic: processing that is enabled but
        /// bypassed sounds exactly like processing that is on and gentle —
        /// on the OUTPUT. The residual tells them apart instantly: a
        /// bypassed chain produces actual silence there, not something quiet.
        /// </summary>
        private static void CheckBypassedChainGivesSilentResidual()
        {
            var cond = new TxAudioConditioner();
            // NR attached but internally bypassed (a do-nothing stage), gate off.
            cond.NoiseReducer = (buf, count, rate) => { };
            cond.MonitorMode = TxAudioConditioner.MonitorModes.Residual;

            float[] captured = null;
            cond.MonitorSink = (buf, count, rate) => captured = (float[])buf.Clone();

            float[] input = SineBuffer(997.0, -15f, FrameFloats);
            cond.Process(input, input.Length, Fs);

            Assert(captured != null, "monitor sink called for the bypassed chain");
            double residualEnergy = captured != null ? Energy(captured) : 1.0;
            Assert(residualEnergy == 0.0,
                $"bypassed chain gives EXACT silence in the residual (energy {residualEnergy:E1})");
        }

        /// <summary>Split mode: output in the left channel, residual in the
        /// right — the two-ears trade-off view.</summary>
        private static void CheckSplitMonitorChannels()
        {
            var cond = new TxAudioConditioner();
            cond.NoiseReducer = (buf, count, rate) =>
            {
                for (int i = 0; i < count; i++) buf[i] *= 0.25f;
            };
            cond.MonitorMode = TxAudioConditioner.MonitorModes.Split;

            float[] captured = null;
            cond.MonitorSink = (buf, count, rate) => captured = (float[])buf.Clone();

            float[] input = SineBuffer(997.0, -15f, FrameFloats);
            float[] original = (float[])input.Clone();
            cond.Process(input, input.Length, Fs);

            bool ok = captured != null;
            if (ok)
            {
                for (int i = 0; i + 1 < FrameFloats; i += 2)
                {
                    float expectedOut = original[i] * 0.25f;
                    float expectedResidual = original[i] - original[i] * 0.25f;
                    if (Math.Abs(captured[i] - expectedOut) > 1e-6f ||
                        Math.Abs(captured[i + 1] - expectedResidual) > 1e-6f)
                    { ok = false; break; }
                }
            }
            Assert(ok, "split monitor carries output left, residual right");
        }

        /// <summary>Monitor Off means the sink is never called — no zombie
        /// audio path.</summary>
        private static void CheckMonitorOffNeverCalls()
        {
            var cond = new TxAudioConditioner();
            cond.NoiseReducer = (buf, count, rate) => { };
            cond.MonitorMode = TxAudioConditioner.MonitorModes.Off;
            bool called = false;
            cond.MonitorSink = (buf, count, rate) => called = true;

            float[] input = SineBuffer(997.0, -15f, FrameFloats);
            cond.Process(input, input.Length, Fs);
            Assert(!called, "monitor sink is never called while Off");
        }

        // ---------------------------------------------------------------- //
        // Plumbing
        // ---------------------------------------------------------------- //

        private static TxNoiseGate NewGate(float thresholdDb)
        {
            return new TxNoiseGate
            {
                Enabled = true,
                ThresholdDb = thresholdDb
                // attack/hold/release/range stay at shipped defaults —
                // the harness proves the defaults, not a tuned special case.
            };
        }

        private static double _phase;

        /// <summary>One buffer of interleaved stereo sine (mono duplicated),
        /// phase-continuous across calls via the shared accumulator.</summary>
        private static float[] SineBuffer(double freq, float levelDb, int floats)
        {
            float amp = (float)Math.Pow(10.0, levelDb / 20.0);
            var buf = new float[floats];
            for (int i = 0; i < floats; i += 2)
            {
                float s = (float)(Math.Sin(_phase) * amp);
                _phase += 2.0 * Math.PI * freq / Fs;
                if (_phase >= 2.0 * Math.PI) _phase -= 2.0 * Math.PI;
                buf[i] = s;
                buf[i + 1] = s;
            }
            return buf;
        }

        /// <summary>Mono frame array (one float per frame) for measurement
        /// convenience.</summary>
        private static float[] SineMono(double freq, float levelDb, int frames)
        {
            float amp = (float)Math.Pow(10.0, levelDb / 20.0);
            var buf = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                buf[i] = (float)(Math.Sin(_phase) * amp);
                _phase += 2.0 * Math.PI * freq / Fs;
                if (_phase >= 2.0 * Math.PI) _phase -= 2.0 * Math.PI;
            }
            return buf;
        }

        /// <summary>Run mono frames through the gate in Opus-sized stereo
        /// buffers; returns the processed mono track.</summary>
        private static float[] ProcessMono(TxNoiseGate gate, float[] mono)
        {
            var result = new float[mono.Length];
            int pos = 0;
            var stereo = new float[FrameFloats];
            while (pos < mono.Length)
            {
                int frames = Math.Min(FrameFloats / 2, mono.Length - pos);
                for (int f = 0; f < frames; f++)
                {
                    stereo[f * 2] = mono[pos + f];
                    stereo[f * 2 + 1] = mono[pos + f];
                }
                gate.Process(stereo, frames * 2, Fs);
                for (int f = 0; f < frames; f++)
                    result[pos + f] = stereo[f * 2];
                pos += frames;
            }
            return result;
        }

        private static void FeedSine(TxNoiseGate gate, double freq, float levelDb, double seconds)
        {
            int total = (int)(seconds * Fs);
            int fed = 0;
            while (fed < total)
            {
                int frames = Math.Min(FrameFloats / 2, total - fed);
                float[] buf = SineBuffer(freq, levelDb, frames * 2);
                gate.Process(buf, frames * 2, Fs);
                fed += frames;
            }
        }

        private static double Energy(float[] buf) => Energy(buf, 0, buf.Length);

        private static double Energy(float[] buf, int offset, int count)
        {
            double sum = 0.0;
            int end = Math.Min(offset + count, buf.Length);
            for (int i = offset; i < end; i++) sum += (double)buf[i] * buf[i];
            return sum;
        }

        private static double Ratio(double a, double b) => b > 0 ? a / b : 0;

        private static void Assert(bool ok, string what)
        {
            checks++;
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }
    }
}
