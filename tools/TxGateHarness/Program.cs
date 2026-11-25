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
            CheckDelayedResidualAndBypass(480);
            CheckDelayedResidualAndBypass(2448);

            // Sprint 49 Track I: the gate converted to a downward expander,
            // and the stages after it.
            CheckExpanderLawAtTwoToOne();
            CheckSideChainIgnoresRumble();
            CheckLookAheadOpensBeforeTheWord();
            CheckEqualiserOffAndFlatAreExact();
            CheckDeEsserIdleIsExact();
            CheckResidualLeavesTheEqualiserOut();

            // Sprint 49 Track CL: the compressor stage and the limiter.
            CheckCompressorAtRatioOneIsExact();
            CheckCompressorSettlesToThePeaksReduction();
            CheckRotatorKeepsTheLevel();
            CheckLimiterLeavesAudioUnderTheCeilingAlone();
            CheckLimiterHoldsTheCeiling();
            CheckResidualLeavesTheDynamicsOut();

            // Sprint 49 Track J: JJ Clarity, between the de-esser and the compressor.
            CheckClarityOffAndZeroMixAreExact();
            CheckClarityAloneShapesAndLeavesTheResidualSilent();
            CheckClarityEdgeChangeReachesTheWaitingRecord();

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
            Assert(gate.AttenuationDb < 0.1f, $"fresh gate applies no attenuation ({gate.AttenuationDb:F2} dB)");
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
            Assert(gate.AttenuationDb > 20f, $"gate attenuates after 1.5 s below threshold ({gate.AttenuationDb:F1} dB)");

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

            Assert(gate.AttenuationDb > 20f, $"gate reports its attenuation after a 1.4 s pause ({gate.AttenuationDb:F1} dB)");
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

        /// <summary>A pure delay removes nothing: residual must be exact
        /// silence, including a monitor enabled midstream and NR bypasses.</summary>
        private static void CheckDelayedResidualAndBypass(int latency)
        {
            var cond = new TxAudioConditioner();
            var line = new float[latency * 2];
            int at = 0, applied = 0;
            bool bypass = false;
            cond.NoiseReducer = (buffer, count, rate) => {
                applied = 0;
                if (bypass || rate != Fs) return;
                for (int i = 0; i < count; i++) {
                    float raw = buffer[i]; buffer[i] = line[at]; line[at] = raw;
                    if (++at == line.Length) at = 0;
                }
                applied = latency;
            };
            cond.NoiseReducerAppliedLatencySamples = () => applied;
            float[] captured = null;
            cond.MonitorSink = (buffer, count, rate) => captured = (float[])buffer.Clone();
            bool exact = true, split = true, bypassSilent = true, output = true;
            for (int frame = 0; frame < 45; frame++) {
                bypass = frame >= 20 && frame < 24;
                cond.MonitorMode = frame < 8 ? TxAudioConditioner.MonitorModes.Off
                    : frame < 14 ? TxAudioConditioner.MonitorModes.Residual
                    : frame < 35 ? TxAudioConditioner.MonitorModes.Split : TxAudioConditioner.MonitorModes.Output;
                float[] buffer = SineBuffer(997, -15, FrameFloats);
                captured = null;
                uint rate = frame >= 28 && frame < 31 ? 44100u : Fs;
                cond.Process(buffer, buffer.Length, rate);
                if (frame < 8) continue;
                if (captured == null) { exact = false; continue; }
                if (frame < 14) {
                    for (int i = 0; i < FrameFloats; i++) exact &= captured[i] == 0;
                }
                else if (frame < 35) {
                    for (int i = 0; i < FrameFloats; i += 2) {
                        split &= captured[i] == buffer[i] && captured[i + 1] == 0;
                        if (bypass || rate != Fs) bypassSilent &= captured[i + 1] == 0;
                    }
                }
                else for (int i = 0; i < FrameFloats; i++) output &= captured[i] == buffer[i];
            }
            Assert(exact, $"{latency}-sample reducer has exact zero residual, including enabling monitor midstream");
            Assert(split, $"{latency}-sample delay: split output left, exact zero residual right");
            Assert(bypassSilent, $"{latency}-sample reducer reports zero on bypass and wrong rate, residual stays silent");
            Assert(output, $"{latency}-sample reducer leaves Output monitor bit-identical");
        }

        // ---------------------------------------------------------------- //
        // Sprint 49 Track I: expander, equaliser, de-esser
        // ---------------------------------------------------------------- //

        /// <summary>At 2:1 a steady tone 13 dB below the threshold (10 dB below the close point) is attenuated by about 10 dB, not by the full depth.</summary>
        private static void CheckExpanderLawAtTwoToOne()
        {
            // A quadrature pair (sine left, cosine right) has a constant
            // channel-power sum, so the detector reads its level with no
            // ripple; a plain sine reads near its peak power, about 3 dB high,
            // because the detector rises fast and falls slowly.
            var gate = NewGate(thresholdDb: -40f);
            gate.GateSetting = false;
            gate.RatioToOne = 2f;
            FeedQuadrature(gate, 997.0, -53f, seconds: 2.0);
            Assert(Math.Abs(gate.AttenuationDb - 10f) < 0.3f, $"2:1 attenuates a level 10 dB below the close point by 10 dB ({gate.AttenuationDb:F2} dB)");
            gate.RatioToOne = 1f;
            FeedQuadrature(gate, 997.0, -53f, seconds: 1.0);
            Assert(gate.AttenuationDb < 0.05f, $"1:1 attenuates nothing ({gate.AttenuationDb:F2} dB)");
            var gateSet = NewGate(thresholdDb: -40f);
            FeedSine(gateSet, 997.0, -53f, seconds: 2.0);
            Assert(gateSet.AttenuationDb > 24f, $"the gate setting still takes the full depth ({gateSet.AttenuationDb:F2} dB)");
        }

        /// <summary>A loud 60 Hz rumble holds the stage open when the detector hears everything, and does not when it hears the voice band only.</summary>
        private static void CheckSideChainIgnoresRumble()
        {
            var open = NewGate(thresholdDb: -40f);
            FeedSine(open, 60.0, -30f, seconds: 2.0);
            var filtered = NewGate(thresholdDb: -40f);
            filtered.SideChainEnabled = true;
            FeedSine(filtered, 60.0, -30f, seconds: 2.0);
            Assert(open.AttenuationDb < 1f, $"without the side-chain filter, rumble holds the stage open ({open.AttenuationDb:F1} dB)");
            Assert(filtered.AttenuationDb > 20f, $"with it, the same rumble does not ({filtered.AttenuationDb:F1} dB)");
        }

        /// <summary>After a long quiet, a word arriving with 10 ms look-ahead (1 ms attack) meets the stage already open; with none it meets it attenuated.</summary>
        private static void CheckLookAheadOpensBeforeTheWord()
        {
            float with = FirstCycleGainDb(10f), without = FirstCycleGainDb(0f);
            Assert(with > -0.5f, $"with 10 ms look-ahead the word's first cycle is at full gain ({with:F2} dB)");
            Assert(without < -3f, $"with none it is still attenuated ({without:F2} dB): the control");
        }

        private static float FirstCycleGainDb(float lookAheadMs)
        {
            var gate = NewGate(thresholdDb: -45f);
            gate.AttackMs = 1f;
            gate.LookAheadMs = lookAheadMs;
            FeedSilence(gate, 1.5);
            int la = TxNoiseGate.LookAheadFramesFor(lookAheadMs, Fs);
            int frames = (int)(0.040 * Fs);
            var word = SineMono(997.0, -15f, frames);
            var padded = new float[frames + la];
            Array.Copy(word, padded, frames);
            var outp = ProcessMono(gate, padded);
            // The word's first sample comes out la frames later; compare the
            // peaks of its first cycle in and out.
            double inPeak = 0, outPeak = 0;
            int cycle = (int)(Fs / 997.0);
            for (int i = 0; i < cycle; i++)
            {
                inPeak = Math.Max(inPeak, Math.Abs(word[i]));
                outPeak = Math.Max(outPeak, Math.Abs(outp[la + i]));
            }
            return (float)(20 * Math.Log10(outPeak / inPeak));
        }

        /// <summary>The equaliser off, and on with no bands at 0 dB, both pass audio bit for bit.</summary>
        private static void CheckEqualiserOffAndFlatAreExact()
        {
            var eq = new ParametricEq();
            float[] a = SineBuffer(997.0, -15f, FrameFloats), refA = (float[])a.Clone();
            eq.Process(a, a.Length, Fs);
            eq.Use(new ParametricEqDesign(Array.Empty<ParametricEqBand>()));
            float[] b = SineBuffer(997.0, -15f, FrameFloats), refB = (float[])b.Clone();
            eq.Process(b, b.Length, Fs);
            bool same = true;
            for (int i = 0; i < a.Length; i++) same &= a[i] == refA[i] && b[i] == refB[i];
            Assert(same, "the equaliser off, or flat, passes samples bit-for-bit");
        }

        /// <summary>A de-esser hearing nothing in its band changes nothing.</summary>
        private static void CheckDeEsserIdleIsExact()
        {
            var de = new DeEsser { Enabled = true, FrequencyHz = 6000f, ThresholdDb = -30f };
            bool same = true;
            for (int k = 0; k < 20; k++)
            {
                float[] buf = SineBuffer(300.0, -40f, FrameFloats), r = (float[])buf.Clone();
                de.Process(buf, buf.Length, Fs);
                for (int i = 0; i < buf.Length; i++) same &= buf[i] == r[i];
            }
            Assert(same, "an idle de-esser passes samples bit-for-bit");
        }

        /// <summary>A 6 dB boost after the cleanup stages leaves "what was removed" silent (S8).</summary>
        private static void CheckResidualLeavesTheEqualiserOut()
        {
            var cond = new TxAudioConditioner();
            cond.NoiseReducer = (buf, count, rate) => { };
            var staged = cond.GateInForce.Copy();
            staged.Eq = new ParametricEqDesign(new[] { new ParametricEqBand(BiquadKind.Peaking, 997, 6, 1) });
            cond.SetGateInForce(staged);
            cond.MonitorMode = TxAudioConditioner.MonitorModes.Residual;
            float[] captured = null;
            cond.MonitorSink = (buf, count, rate) => captured = (float[])buf.Clone();
            double residual = 0;
            bool changed = false;
            for (int k = 0; k < 10; k++)
            {
                float[] buf = SineBuffer(997.0, -20f, FrameFloats), r = (float[])buf.Clone();
                cond.Process(buf, buf.Length, Fs);
                residual += Energy(captured);
                for (int i = 0; i < buf.Length; i++) changed |= buf[i] != r[i];
            }
            Assert(changed, "the boost did change what goes out (the control)");
            Assert(residual == 0.0, $"and the residual stays silent ({residual:E1})");
        }

        private static void FeedQuadrature(TxNoiseGate gate, double freq, float levelDb, double seconds)
        {
            int total = (int)(seconds * Fs), fed = 0;
            double amp = Math.Pow(10.0, levelDb / 20.0), ph = 0;
            var buf = new float[FrameFloats];
            while (fed < total)
            {
                int frames = Math.Min(FrameFloats / 2, total - fed);
                for (int f = 0; f < frames; f++)
                {
                    buf[f * 2] = (float)(amp * Math.Sin(ph));
                    buf[f * 2 + 1] = (float)(amp * Math.Cos(ph));
                    ph += 2.0 * Math.PI * freq / Fs;
                }
                gate.Process(buf, frames * 2, Fs);
                fed += frames;
            }
        }

        private static void FeedSilence(TxNoiseGate gate, double seconds)
        {
            int total = (int)(seconds * Fs), fed = 0;
            var buf = new float[FrameFloats];
            while (fed < total)
            {
                int frames = Math.Min(FrameFloats / 2, total - fed);
                Array.Clear(buf, 0, buf.Length);
                gate.Process(buf, frames * 2, Fs);
                fed += frames;
            }
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

        // ---------------------------------------------------------------- //
        // The compressor stage and the limiter (Sprint 49 Track CL)
        // ---------------------------------------------------------------- //

        /// <summary>Ratio 1, no drive, no makeup, no rotator: on, and still the input bit for bit.</summary>
        private static void CheckCompressorAtRatioOneIsExact()
        {
            var comp = new TxCompressor { Settings = new TxCompressorSettings { Enabled = true, RatioToOne = 1f, MakeupDb = 0f } };
            bool same = true;
            for (int k = 0; k < 20; k++)
            {
                float[] buf = SineBuffer(700.0, -3f, FrameFloats), r = (float[])buf.Clone();
                comp.Process(buf, buf.Length, Fs);
                for (int i = 0; i < buf.Length; i++) same &= buf[i] == r[i];
            }
            Assert(same, "a compressor at ratio 1 with no drive, makeup or rotator passes samples bit-for-bit");
        }

        /// <summary>A steady -6 dBFS tone, 12 dB over a -18 dBFS threshold at 3 to 1, settles to 8 dB of reduction: the PEAK's, not several dB short.</summary>
        private static void CheckCompressorSettlesToThePeaksReduction()
        {
            var comp = new TxCompressor { Settings = new TxCompressorSettings { Enabled = true, ThresholdDb = -18f, RatioToOne = 3f, KneeDb = 6f, MakeupDb = 0f } };
            for (int k = 0; k < 200; k++)
            {
                float[] buf = SineBuffer(1000.0, -6f, FrameFloats);
                comp.Process(buf, buf.Length, Fs);
            }
            Assert(comp.ReductionDb > 7.5f && comp.ReductionDb < 8.05f,
                $"a steady tone settles to the reduction its peak calls for (8 dB; read {comp.ReductionDb:F2})");
        }

        /// <summary>Six all-pass sections change the phase and not the level.</summary>
        private static void CheckRotatorKeepsTheLevel()
        {
            var comp = new TxCompressor { Settings = new TxCompressorSettings { Enabled = true, RatioToOne = 1f, MakeupDb = 0f, RotatorStages = PhaseRotator.MaxStages } };
            double input = 0, output = 0;
            bool changed = false;
            for (int k = 0; k < 100; k++)
            {
                float[] buf = SineBuffer(700.0, -12f, FrameFloats), r = (float[])buf.Clone();
                comp.Process(buf, buf.Length, Fs);
                if (k < 50) continue; // settled
                input += Energy(r); output += Energy(buf);
                for (int i = 0; i < buf.Length; i++) changed |= buf[i] != r[i];
            }
            double db = 10.0 * Math.Log10(output / input);
            Assert(changed && Math.Abs(db) < 0.01, $"the phase rotator moves the samples and keeps the level (off by {db:F4} dB)");
        }

        /// <summary>The #1278 property: with the limiter on at its default ceiling, audio within full scale is not touched.</summary>
        private static void CheckLimiterLeavesAudioUnderTheCeilingAlone()
        {
            var lim = new TxLimiter { Settings = new TxLimiterSettings { Enabled = true } };
            bool same = true;
            for (int k = 0; k < 20; k++)
            {
                float[] buf = SineBuffer(700.0, 0f, FrameFloats), r = (float[])buf.Clone(); // peaks AT full scale
                lim.Process(buf, buf.Length, Fs);
                for (int i = 0; i < buf.Length; i++) same &= buf[i] == r[i];
            }
            Assert(same, "a limiter at full scale passes audio that never exceeds full scale bit-for-bit (it cannot lower peak drive)");
        }

        /// <summary>A tone 6 dB over full scale leaves with no sample over the ceiling, with and without look-ahead.</summary>
        private static void CheckLimiterHoldsTheCeiling()
        {
            foreach (float lookAhead in new[] { 0f, 5f })
            {
                var lim = new TxLimiter { Settings = new TxLimiterSettings { Enabled = true, LookAheadMs = lookAhead } };
                float worst = 0;
                for (int k = 0; k < 50; k++)
                {
                    float[] buf = SineBuffer(700.0, 6f, FrameFloats);
                    lim.Process(buf, buf.Length, Fs);
                    for (int i = 0; i < buf.Length; i++) worst = Math.Max(worst, Math.Abs(buf[i]));
                }
                var (caught, over, _, _) = lim.TakeOverStats();
                Assert(worst <= 1f && caught > 0 && over == 0,
                    $"a limiter with {lookAhead:F0} ms look-ahead holds a +6 dBFS tone at the ceiling (worst sample {worst:F4}, caught {caught})");
            }
        }

        /// <summary>The compressor and the limiter after the cleanup stages leave "what was removed" silent (S8, by extension).</summary>
        private static void CheckResidualLeavesTheDynamicsOut()
        {
            var cond = new TxAudioConditioner { MonitorMode = TxAudioConditioner.MonitorModes.Residual };
            double residual = 0;
            cond.MonitorSink = (b, n, r) => residual += Energy(b, 0, n);
            var record = new TxAudioConditioner.StagedGate
            {
                Comp = new TxCompressorSettings { Enabled = true, ThresholdDb = -30f, RatioToOne = 4f, MakeupDb = 12f, DriveDb = 6f, RotatorStages = 4 },
                Limiter = new TxLimiterSettings { Enabled = true, CeilingDb = -6f, LookAheadMs = 5f },
            };
            cond.SetGateInForce(record);
            double sent = 0, input = 0;
            for (int k = 0; k < 50; k++)
            {
                float[] buf = SineBuffer(700.0, -6f, FrameFloats);
                input += Energy(buf);
                cond.Process(buf, buf.Length, Fs);
                sent += Energy(buf);
            }
            Assert(residual == 0.0 && Math.Abs(10.0 * Math.Log10(sent / input)) > 1.0,
                "the compressor and the limiter change what is sent and leave \"what was removed\" silent");
        }

        // ------------------------------------------------------------------
        // JJ Clarity (Sprint 49 Track J)
        // ------------------------------------------------------------------

        private static readonly ClaritySettings BrightClarity = new ClaritySettings(
            new ClarityBand(true, 2000f, 18f, 60f, ClarityHarmonics.Even),
            new ClarityBand(false, 150f, 6f, 25f, ClarityHarmonics.Both));

        /// <summary>JJ Clarity off, and on with both mixes at zero, leave the chain's audio bit-for-bit, and the chain reads as doing nothing.</summary>
        private static void CheckClarityOffAndZeroMixAreExact()
        {
            bool exact = true, idle = true;
            foreach (bool enabled in new[] { false, true })
            {
                var cond = new TxAudioConditioner();
                var zero = new ClaritySettings(BrightClarity.High.WithMix(0f), BrightClarity.Low.WithMix(0f));
                cond.SetGateInForce(new TxAudioConditioner.StagedGate { ClarityEnabled = enabled, Clarity = enabled ? zero : BrightClarity });
                for (int k = 0; k < 20; k++)
                {
                    float[] buf = SineBuffer(1000.0, -12f, FrameFloats);
                    float[] reference = (float[])buf.Clone();
                    cond.Process(buf, buf.Length, Fs);
                    for (int i = 0; i < buf.Length; i++)
                        if (BitConverter.SingleToInt32Bits(buf[i]) != BitConverter.SingleToInt32Bits(reference[i])) exact = false;
                }
                idle &= !cond.AnyStageActive;
            }
            Assert(exact && idle, "JJ Clarity off, and on with both mixes at zero, leaves the audio bit-for-bit and the chain reads as doing nothing");
        }

        /// <summary>JJ Clarity alone on is a chain that is doing something: it shapes what is sent, and "what was removed" stays silent.</summary>
        private static void CheckClarityAloneShapesAndLeavesTheResidualSilent()
        {
            var cond = new TxAudioConditioner { MonitorMode = TxAudioConditioner.MonitorModes.Residual };
            double residual = 0;
            cond.MonitorSink = (b, n, r) => residual += Energy(b, 0, n);
            cond.SetGateInForce(new TxAudioConditioner.StagedGate { ClarityEnabled = true, Clarity = BrightClarity });
            double difference = 0;
            for (int k = 0; k < 50; k++)
            {
                float[] buf = SineBuffer(1000.0, -12f, FrameFloats);
                float[] reference = (float[])buf.Clone();
                cond.Process(buf, buf.Length, Fs);
                for (int i = 0; i < buf.Length; i++) difference += (buf[i] - reference[i]) * (double)(buf[i] - reference[i]);
            }
            Assert(cond.AnyStageActive && difference > 1e-4 && residual == 0.0,
                $"JJ Clarity alone makes the chain active, adds to what is sent (difference energy {difference:E2}) and leaves \"what was removed\" silent");
        }

        /// <summary>A setup staged while keyed, then a transmit filter change, then the unkey: the setup runs with the NEW edges.</summary>
        private static void CheckClarityEdgeChangeReachesTheWaitingRecord()
        {
            bool keyed = true;
            var cond = new TxAudioConditioner { KeySource = () => keyed };
            cond.SetClarityPassband(100f, 2900f);
            float[] buf = SineBuffer(1000.0, -12f, FrameFloats);
            cond.Process(buf, buf.Length, Fs);
            cond.StageGate(new TxAudioConditioner.StagedGate { ClarityEnabled = true, Clarity = BrightClarity });
            cond.SetClarityPassband(100f, 2000f);
            cond.Process(buf, buf.Length, Fs);
            bool waited = !cond.Clarity.Enabled;
            keyed = false;
            cond.Process(buf, buf.Length, Fs);
            var running = cond.Clarity.Wanted;
            Assert(waited && cond.Clarity.Enabled && running != null && running.PassbandHighEdgeHz == 2000f
                    && running.Settings.Equals(BrightClarity) && cond.Clarity.HighTuneClamped,
                $"a JJ Clarity setup staged while keyed runs, at the unkey, with the passband edges set after it was staged (high edge {running?.PassbandHighEdgeHz:F0} Hz)");
        }

        private static void Assert(bool ok, string what)
        {
            checks++;
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
        }
    }
}
