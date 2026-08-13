using System;
using JJPortaudio;

namespace LufsHarness
{
    /// <summary>
    /// Numerical harness for the BS.1770 / EBU R128 LUFS meter (Audio Arc
    /// Engine Track). There is no radio and no listener at this bench, so the
    /// meter is proven the same way the Track C tone generator was: drive the
    /// REAL code (the linked source file, not a copy) with synthetic signals
    /// whose loudness is known from the spec, and assert the measured values.
    ///
    /// Key references used:
    ///   * ITU-R BS.1770-4 publishes the 48 kHz K-weighting coefficients；
    ///     the meter derives its coefficients from the analog prototype so it
    ///     works at any rate — check 1 asserts the derivation reproduces the
    ///     published table.
    ///   * BS.1770 states a 997 Hz full-scale sine in ONE channel reads
    ///     -3.01 LKFS. Our TX stream carries the mono mic duplicated onto
    ///     both channels, which adds +3.01 dB — so a -10 dBFS stereo sine
    ///     must read -10.0 LUFS, directly comparable to the radio's SC_MIC
    ///     anchor (bench 2026-08-11: -10 dBFS injected, -11 read).
    ///   * EBU R128 gating: blocks under -70 LUFS absolute, or 10 LU under
    ///     the running level, are dropped — the property that natively
    ///     discards the silent gaps between words.
    /// </summary>
    internal static class Program
    {
        private static int failures;
        private static int checks;

        private static int Main()
        {
            Console.WriteLine("LUFS meter numerical harness — BS.1770-4 / EBU R128");
            Console.WriteLine();

            CheckCoefficients();
            CheckSineReference(-10.0f, 48000);
            CheckSineReference(-23.0f, 48000);   // the EBU R128 target level
            CheckSingleChannel();
            CheckSpectralTilt();
            CheckMomentaryResponse();
            CheckAbsoluteGate();
            CheckRelativeGate();
            CheckWordGapGating();
            CheckSilence();
            CheckSineReference(-10.0f, 24000);   // device fell back to 24 kHz
            CheckResetIntegrated();
            CheckToneGeneratorChain();
            CheckProfileNeedsEnoughAudio();
            CheckNoiseFloorQuietShack();
            CheckNoiseFloorNoisyRoom();
            CheckNoiseDoesNotMoveTheLevelVerdict();

            Console.WriteLine();
            Console.WriteLine($"{checks} checks, {failures} failed.");
            return failures;
        }

        // ---------------------------------------------------------------- //

        /// <summary>Coefficient derivation reproduces the ITU-published 48 kHz table.</summary>
        private static void CheckCoefficients()
        {
            double[] c = LufsMeter.CoefficientsForTest(48000.0);
            // ITU-R BS.1770-4, Tables 1 and 2 (stage 1 shelf, stage 2 high-pass).
            double[] expected =
            {
                1.53512485958697, -2.69169618940638, 1.19839281085285,
                -1.69065929318241, 0.73248077421585,
                1.0, -2.0, 1.0,
                -1.99004745483398, 0.99007225036621
            };
            string[] names =
            {
                "s1.b0", "s1.b1", "s1.b2", "s1.a1", "s1.a2",
                "s2.b0", "s2.b1", "s2.b2", "s2.a1", "s2.a2"
            };
            for (int i = 0; i < expected.Length; i++)
            {
                Assert(Math.Abs(c[i] - expected[i]) < 1e-6,
                    $"48 kHz K-weighting {names[i]} = {c[i]:F14} (ITU table {expected[i]:F14})");
            }
        }

        /// <summary>A 997 Hz stereo sine at a known dBFS reads that same number
        /// in LUFS — momentary, short-term and integrated.</summary>
        private static void CheckSineReference(float levelDb, uint fs)
        {
            var meter = new LufsMeter();
            FeedSine(meter, fs, 997.0, levelDb, seconds: 10.0);
            float m = meter.MomentaryLufs;
            float s = meter.ShortTermLufs;
            float i = meter.IntegratedLufs;
            Assert(Math.Abs(m - levelDb) < 0.1,
                $"997 Hz sine {levelDb} dBFS @ {fs} Hz: momentary {m:F2} LUFS");
            Assert(Math.Abs(s - levelDb) < 0.1,
                $"997 Hz sine {levelDb} dBFS @ {fs} Hz: short-term {s:F2} LUFS");
            Assert(Math.Abs(i - levelDb) < 0.1,
                $"997 Hz sine {levelDb} dBFS @ {fs} Hz: integrated {i:F2} LUFS");
        }

        /// <summary>BS.1770's own stated property: signal in one channel only
        /// reads 3.01 dB below the both-channels figure.</summary>
        private static void CheckSingleChannel()
        {
            var meter = new LufsMeter();
            FeedSine(meter, 48000, 997.0, -10.0f, seconds: 5.0, leftOnly: true);
            float i = meter.IntegratedLufs;
            Assert(Math.Abs(i - (-13.01)) < 0.15,
                $"997 Hz -10 dBFS in LEFT channel only: integrated {i:F2} LUFS (spec: -13.01)");
        }

        /// <summary>K-weighting shape: the ~38 Hz high-pass attenuates lows,
        /// the shelf boosts highs ~+4 dB.</summary>
        private static void CheckSpectralTilt()
        {
            var reference = new LufsMeter();
            FeedSine(reference, 48000, 997.0, -10.0f, seconds: 5.0);
            float at997 = reference.IntegratedLufs;

            // Deltas are relative to 997 Hz, which itself carries the ~+0.69 dB
            // K-gain that the spec's -0.691 offset cancels. So:
            //   60 Hz: high-pass attenuation ~2.9 dB, plus the 0.69 the
            //          reference enjoys => ~3.6 dB below 997 Hz.
            //   10 kHz: shelf ~+4.0 dB, minus that same 0.69 => ~+3.3 dB.
            // (First harness run asserted ~2.9 for 60 Hz and failed at 3.59 —
            // the meter was right, the window had forgotten the reference gain.)
            var low = new LufsMeter();
            FeedSine(low, 48000, 60.0, -10.0f, seconds: 5.0);
            float at60 = low.IntegratedLufs;
            float lowDelta = at997 - at60;
            Assert(lowDelta > 3.3 && lowDelta < 3.9,
                $"60 Hz reads {lowDelta:F2} dB below 997 Hz (expect ~3.6: 38 Hz high-pass + reference gain)");

            var high = new LufsMeter();
            FeedSine(high, 48000, 10000.0, -10.0f, seconds: 5.0);
            float at10k = high.IntegratedLufs;
            float highDelta = at10k - at997;
            Assert(highDelta > 3.0 && highDelta < 3.7,
                $"10 kHz reads {highDelta:F2} dB above 997 Hz (expect ~+3.3: K shelf - reference gain)");
        }

        /// <summary>Momentary follows the signal down within its 400 ms window;
        /// short-term (3 s) decays slower. This is the live-coaching contract.</summary>
        private static void CheckMomentaryResponse()
        {
            var meter = new LufsMeter();
            FeedSine(meter, 48000, 997.0, -10.0f, seconds: 3.0);
            float duringTone = meter.MomentaryLufs;
            FeedSilence(meter, 48000, seconds: 1.0);
            float afterSilence = meter.MomentaryLufs;
            float shortTerm = meter.ShortTermLufs;
            Assert(Math.Abs(duringTone - (-10.0)) < 0.1,
                $"momentary during tone {duringTone:F2} LUFS");
            Assert(afterSilence < -70.0,
                $"momentary after 1 s silence {afterSilence:F1} LUFS (window flushed)");
            Assert(shortTerm > -20.0 && shortTerm < -10.0,
                $"short-term after 1 s silence {shortTerm:F2} LUFS (3 s window still holds tone)");
        }

        /// <summary>The absolute gate (-70 LUFS) drops silence from the
        /// integrated average — the "gaps between words" property.</summary>
        private static void CheckAbsoluteGate()
        {
            var meter = new LufsMeter();
            FeedSine(meter, 48000, 997.0, -10.0f, seconds: 5.0);
            FeedSilence(meter, 48000, seconds: 5.0);
            float gated = meter.IntegratedLufs;
            float ungated = meter.IntegratedUngatedLufs;
            // 47 full-tone blocks + 3 straddlers over 50 counted blocks: -10.13.
            Assert(gated > -10.6 && gated < -9.7,
                $"5 s tone + 5 s silence: gated integrated {gated:F2} LUFS (tone level held)");
            Assert(gated - ungated >= 2.0,
                $"  ... vs ungated {ungated:F2} LUFS — gate recovered {gated - ungated:F2} dB");
        }

        /// <summary>The relative gate (-10 LU under the running level) drops a
        /// quiet passage that is well above the absolute floor.</summary>
        private static void CheckRelativeGate()
        {
            var meter = new LufsMeter();
            FeedSine(meter, 48000, 997.0, -10.0f, seconds: 5.0);
            FeedSine(meter, 48000, 997.0, -45.0f, seconds: 5.0);
            float gated = meter.IntegratedLufs;
            Assert(gated > -10.7 && gated < -9.6,
                $"5 s at -10 + 5 s at -45: integrated {gated:F2} LUFS (relative gate drops the quiet half)");
        }

        /// <summary>Speech-shaped duty cycle: 300 ms bursts with 700 ms gaps —
        /// an extreme 30% duty cycle. The gate keeps the reading near the burst
        /// level instead of the duty-cycle-diluted level, with no custom
        /// peak-hold logic. This is what supersedes ScMicRecentDb.</summary>
        private static void CheckWordGapGating()
        {
            var meter = new LufsMeter();
            for (int k = 0; k < 10; k++)
            {
                FeedSine(meter, 48000, 997.0, -10.0f, seconds: 0.3);
                FeedSilence(meter, 48000, seconds: 0.7);
            }
            float gated = meter.IntegratedLufs;
            float ungated = meter.IntegratedUngatedLufs;
            // No 400 ms block ever holds more than 300 ms of tone, so ~-13.0
            // is the arithmetic best case here; real speech (shorter gaps,
            // longer phrases) does better still.
            Assert(gated > -13.6,
                $"300 ms bursts / 700 ms gaps: gated integrated {gated:F2} LUFS (burst level ~-13 best case)");
            Assert(gated - ungated >= 1.5,
                $"  ... vs ungated {ungated:F2} LUFS — gate recovered {gated - ungated:F2} dB of word-gap dilution");
        }

        /// <summary>Pure silence reads the floor everywhere, never a number.</summary>
        private static void CheckSilence()
        {
            var meter = new LufsMeter();
            FeedSilence(meter, 48000, seconds: 2.0);
            Assert(meter.MomentaryLufs == LufsMeter.Floor,
                $"silence: momentary {meter.MomentaryLufs:F0}");
            Assert(meter.ShortTermLufs == LufsMeter.Floor,
                $"silence: short-term {meter.ShortTermLufs:F0}");
            Assert(meter.IntegratedLufs == LufsMeter.Floor,
                $"silence: integrated {meter.IntegratedLufs:F0}");
        }

        /// <summary>ResetIntegrated starts a fresh calibration sample.</summary>
        private static void CheckResetIntegrated()
        {
            var meter = new LufsMeter();
            FeedSine(meter, 48000, 997.0, -10.0f, seconds: 2.0);
            meter.ResetIntegrated();
            Assert(meter.IntegratedLufs == LufsMeter.Floor,
                "ResetIntegrated: integrated back to floor");
            FeedSine(meter, 48000, 997.0, -20.0f, seconds: 5.0);
            float i = meter.IntegratedLufs;
            Assert(Math.Abs(i - (-20.0)) < 0.1,
                $"post-reset sample at -20 dBFS: integrated {i:F2} LUFS (no memory of the -10 run)");

            // The profile is cached against block count, and a reset lands that
            // count back where a previous cache entry may already sit. Read it
            // once to prime the cache, reset, and it must go invalid rather
            // than answering from the run that just ended.
            Assert(meter.Profile.IsValid, "pre-reset: profile valid");
            meter.ResetIntegrated();
            Assert(!meter.Profile.IsValid,
                "post-reset: profile invalid immediately (cache did not survive the reset)");
        }

        /// <summary>Integration check mirroring the real input callback: the
        /// REAL TxToneGenerator feeds the REAL meter, chained exactly as
        /// Audio.inputCallback chains them (tone first, meter second). The
        /// meter must read the tone's level — this is the PC side of the
        /// bench anchor where -10 dBFS injected read -11 on SC_MIC.</summary>
        private static void CheckToneGeneratorChain()
        {
            var tone = new TxToneGenerator();
            tone.Frequency = 440f;
            tone.LevelDb = -10f;
            tone.Start();
            var meter = new LufsMeter();
            const uint fs = 48000;
            const int chunk = 960; // one 10 ms Opus frame, stereo interleaved
            var buf = new float[chunk];
            int calls = (int)(10.0 * fs * 2 / chunk);
            for (int k = 0; k < calls; k++)
            {
                Array.Clear(buf); // silent "mic" — the tone replaces it
                tone.Process(buf, chunk, fs);
                meter.Process(buf, chunk, fs);
            }
            float i = meter.IntegratedLufs;
            // K-weighting is referenced at 997 Hz, and 440 Hz sits ~0.6 dB
            // down the curve (mild shelf rolloff plus the reference gain), so
            // a -10 dBFS 440 Hz tone properly reads about -10.7 LUFS — worth
            // knowing when comparing LUFS to the frequency-flat SC_MIC meter.
            // The generator's fade-in and ~20 ms amplitude settle add a hair.
            Assert(i > -11.1 && i < -10.3,
                $"real TxToneGenerator at -10 dBFS through the real meter: integrated {i:F2} LUFS (expect ~-10.7, K curve at 440 Hz)");
            Assert(meter.HasRecentData,
                "HasRecentData true while samples flow");
        }

        // ---------------- noise floor (Levels Track, 2026-08-12) ---------- //

        /// <summary>A two-word radio check does not carry the evidence for a
        /// floor estimate, and the profile must say so rather than guess.</summary>
        private static void CheckProfileNeedsEnoughAudio()
        {
            var meter = new LufsMeter();
            FeedSpeechOverNoise(meter, 48000, seconds: 1.5, speechDb: -13.0f, noiseRmsDb: -50.0f);
            var shortSample = meter.Profile;
            Assert(!shortSample.IsValid,
                $"1.5 s sample: profile invalid ({shortSample.BlockCount} blocks, need {LufsMeter.MinProfileBlocks})");

            FeedSpeechOverNoise(meter, 48000, seconds: 8.0, speechDb: -13.0f, noiseRmsDb: -50.0f);
            Assert(meter.Profile.IsValid,
                $"9.5 s sample: profile valid ({meter.Profile.BlockCount} blocks)");
        }

        /// <summary>Quiet shack: speech bursts over a -75 dBFS floor. The gap
        /// between voice and room is wide, which is what "nothing to say about
        /// your noise" has to look like numerically.</summary>
        private static void CheckNoiseFloorQuietShack()
        {
            var meter = new LufsMeter();
            FeedSpeechOverNoise(meter, 48000, seconds: 10.0, speechDb: -13.0f, noiseRmsDb: -75.0f);
            var p = meter.Profile;
            Assert(p.IsValid, $"quiet shack: profile valid ({p.BlockCount} blocks)");
            // K-weighting lifts broadband noise ~3.8 dB and the duplicated
            // channels add 3.01, so a -75 dBFS RMS hiss reads near -66 LUFS.
            Assert(p.NoiseFloorLufs < -60.0f,
                $"quiet shack: noise floor {p.NoiseFloorLufs:F1} LUFS");
            Assert(p.SpeechToNoiseLu > 35.0f,
                $"quiet shack: voice stands {p.SpeechToNoiseLu:F1} LU clear of the room");
        }

        /// <summary>Don's apartment: the same speech over a -37 dBFS fan. The
        /// level is unchanged but the daylight underneath it collapses — this
        /// is the whole signature the spoken observation fires on.</summary>
        private static void CheckNoiseFloorNoisyRoom()
        {
            var meter = new LufsMeter();
            FeedSpeechOverNoise(meter, 48000, seconds: 10.0, speechDb: -13.0f, noiseRmsDb: -37.0f);
            var p = meter.Profile;
            Assert(p.IsValid, $"noisy room: profile valid ({p.BlockCount} blocks)");
            Assert(p.NoiseFloorLufs > -35.0f,
                $"noisy room: noise floor {p.NoiseFloorLufs:F1} LUFS (above the -55 audibility threshold)");
            Assert(p.SpeechToNoiseLu < 20.0f,
                $"noisy room: voice only {p.SpeechToNoiseLu:F1} LU clear of the room (under the 20 LU threshold)");
        }

        /// <summary>The load-bearing promise: a noisy room must NOT drag the
        /// level verdict down. The gated speech figure has to read the same in
        /// both rooms, or the noise observation would be replacing information
        /// instead of adding it.</summary>
        private static void CheckNoiseDoesNotMoveTheLevelVerdict()
        {
            var quiet = new LufsMeter();
            FeedSpeechOverNoise(quiet, 48000, seconds: 10.0, speechDb: -13.0f, noiseRmsDb: -75.0f);
            var noisy = new LufsMeter();
            FeedSpeechOverNoise(noisy, 48000, seconds: 10.0, speechDb: -13.0f, noiseRmsDb: -37.0f);
            float delta = Math.Abs(quiet.Profile.SpeechLufs - noisy.Profile.SpeechLufs);
            Assert(delta < 0.5,
                $"speech level moves {delta:F2} dB between quiet and noisy rooms "
                + $"({quiet.Profile.SpeechLufs:F2} vs {noisy.Profile.SpeechLufs:F2} LUFS)");
        }

        // ------------------------- signal plumbing ------------------------ //

        /// <summary>Speech-shaped bursts (500 ms on, 500 ms off) riding a
        /// continuous noise bed — the one signal shape that separates a quiet
        /// shack from a noisy one. Deterministic RNG so runs are comparable.
        /// Sine level is peak dBFS (matching the tone generator); noise level
        /// is RMS dBFS. Mono duplicated to both channels, as the real TX
        /// stream carries it.
        ///
        /// The 10 ms raised-cosine edges are load-bearing, not polish. The
        /// first cut of this harness switched the sine on and off in one
        /// sample, and the resulting clicks splattered enough broadband energy
        /// into the "quiet" stretches to read a -75 dBFS room as -55 LUFS —
        /// the synthetic signal was 13 dB dirtier than the room it was meant
        /// to represent. Mouths do not open in one sample.</summary>
        private static void FeedSpeechOverNoise(LufsMeter meter, uint fs, double seconds,
            float speechDb, float noiseRmsDb)
        {
            var rng = new Random(20260812);
            double speechAmp = Math.Pow(10.0, speechDb / 20.0);
            double noiseAmp = Math.Pow(10.0, noiseRmsDb / 20.0) * Math.Sqrt(3.0); // uniform => RMS = a/sqrt(3)
            int frames = (int)(fs * seconds);
            int chunkFrames = (int)fs / 100; // 10 ms
            int cycle = (int)fs;             // 1 s: half talking, half quiet
            int on = cycle / 2;
            int ramp = (int)fs / 100;        // 10 ms attack and release
            var buf = new float[chunkFrames * 2];
            double phase = 0.0;
            double step = 2.0 * Math.PI * 997.0 / fs;
            int fed = 0;
            while (fed < frames)
            {
                int n = Math.Min(chunkFrames, frames - fed);
                for (int f = 0; f < n; f++)
                {
                    int pos = (fed + f) % cycle;
                    double env;
                    if (pos >= on) env = 0.0;
                    else if (pos < ramp) env = 0.5 * (1.0 - Math.Cos(Math.PI * pos / ramp));
                    else if (pos > on - ramp) env = 0.5 * (1.0 - Math.Cos(Math.PI * (on - pos) / ramp));
                    else env = 1.0;

                    double s = noiseAmp * (rng.NextDouble() * 2.0 - 1.0)
                             + Math.Sin(phase) * speechAmp * env;
                    phase += step;
                    if (phase >= 2.0 * Math.PI) phase -= 2.0 * Math.PI;
                    buf[f * 2] = (float)s;
                    buf[f * 2 + 1] = (float)s;
                }
                meter.Process(buf, n * 2, fs);
                fed += n;
            }
        }


        /// <summary>Feed a stereo interleaved sine in real-callback-sized
        /// chunks (10 ms). dBFS is peak-amplitude convention, matching the
        /// tone generator and the radio's meters.</summary>
        private static void FeedSine(LufsMeter meter, uint fs, double freq,
            float levelDb, double seconds, bool leftOnly = false)
        {
            double amp = Math.Pow(10.0, levelDb / 20.0);
            int frames = (int)(fs * seconds);
            int chunkFrames = (int)fs / 100; // 10 ms
            var buf = new float[chunkFrames * 2];
            double phase = 0.0;
            double step = 2.0 * Math.PI * freq / fs;
            int fed = 0;
            while (fed < frames)
            {
                int n = Math.Min(chunkFrames, frames - fed);
                for (int f = 0; f < n; f++)
                {
                    float s = (float)(Math.Sin(phase) * amp);
                    phase += step;
                    if (phase >= 2.0 * Math.PI) phase -= 2.0 * Math.PI;
                    buf[f * 2] = s;
                    buf[f * 2 + 1] = leftOnly ? 0f : s;
                }
                meter.Process(buf, n * 2, fs);
                fed += n;
            }
        }

        private static void FeedSilence(LufsMeter meter, uint fs, double seconds)
        {
            int frames = (int)(fs * seconds);
            int chunkFrames = (int)fs / 100;
            var buf = new float[chunkFrames * 2];
            int fed = 0;
            while (fed < frames)
            {
                int n = Math.Min(chunkFrames, frames - fed);
                Array.Clear(buf);
                meter.Process(buf, n * 2, fs);
                fed += n;
            }
        }

        private static void Assert(bool ok, string what)
        {
            checks++;
            if (!ok) failures++;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
        }
    }
}
