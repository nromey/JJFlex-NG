using JJFlexWpf;

namespace NrBench;
// Sprint 49 bench CW: the self-checks for the new engines and for the CW measure.
// Each is a control that fails when the thing it guards is broken, and each has a
// mutation (selftest --mutation <name>) that breaks exactly that and must turn it red:
//   helper-missing   the helper is not where the bench looks: every helper check fails, never skips
//   engine-bypass    the engine passes audio through unchanged: "processes" and "quieter gaps" fail
//   engine-latency   the output arrives a frame later than the engine says: "aligned" fails
//   nnr-same-model   the bench builds every NNR model as standard: "nnr-models-distinct" fails
//   cw-pitch         the pitch finder takes the loudest bin: a steady carrier wins, "cw-pitch" fails
//   cw-separation    the measure ignores key-down against gap: "cw-separation" fails
//   cw-align         the measure does not look for a file's delay: "cw-align" fails
//   narrow-filter    the narrow filter's delay is not removed: "narrow-filter" fails
partial class BenchApp
{
    static void AddCwChecks(List<(string Name, Action Test)> tests, string library)
    {
        // Eight seconds of a keyed 700 Hz tone, its RMS equal to the noise's across 200 to 3000 Hz,
        // which puts it about 15 dB above the noise within 50 Hz of its pitch.
        var synth = MakeSynthCw(8, 700, 0, 49);
        var input = synth.Mix();
        int n = input.Length;
        var outputs = new Dictionary<string, float[]>();
        var engines = NnrModels.Select(m => ("nnr-" + m, Nnr(m), (string?)m)).Concat(new[] { ("nr2", Nr2(), (string?)null), ("nr4", Nr4(), (string?)null) });
        foreach (var (name, stage, model) in engines)
        {
            tests.Add((name, () =>
            {
                string id = stage.Settings.EngineId;
                var output = ProcessChain(input, 1, new() { stage.Copy() }, library, out int latency);
                int expected = DocumentedAlgorithmicDelay(id, model) + (IsHelperEngine(id) ? WdspHelperEngine.DefaultPipelineDepth * Frame : 0);
                Assert(latency == expected, $"{name} reports a delay of {latency} samples; its documentation says {expected}");
                var wet = output.AsSpan(latency, n).ToArray();
                // engine-latency: the output a frame later than the engine says.
                if (Mutation == "engine-latency") wet = output.AsSpan(latency - Frame, n).ToArray();
                outputs[name] = wet;
                int lag = EnvelopeLagMs(input, wet, 700);
                Assert(Math.Abs(lag) <= 3, $"{name}'s output is {lag} ms from where its reported delay puts it");
                double residual = 0, energy = 0;
                for (int i = Rate; i < n - Rate / 2; i++) { double d = wet[i] - input[i]; residual += d * d; energy += (double)input[i] * input[i]; }
                double residualDb = 10 * Math.Log10(residual / energy + 1e-30);
                Assert(residualDb > -20, $"{name} changed the audio by only {residualDb:0.0} dB: it is passing audio through");
                double gapIn = GapPower(input, synth.Keyed), gapOut = GapPower(wet, synth.Keyed);
                double fell = 10 * Math.Log10(gapIn / Math.Max(1e-30, gapOut));
                Assert(fell >= 3, $"{name} lowered the noise between elements by only {fell:0.0} dB");
                double keyIn = KeyPower(NarrowBand(input, 700, 250), synth.Keyed), keyOut = KeyPower(NarrowBand(wet, 700, 250), synth.Keyed);
                Console.WriteLine($"INFO {name}: delay {latency} samples, aligned within {Math.Abs(lag)} ms, gaps {fell:0.0} dB quieter, " +
                                  $"the tone near its pitch {10 * Math.Log10(keyOut / Math.Max(1e-30, keyIn)):+0.0;-0.0;0.0} dB.");
            }));
        }
        tests.Add(("nnr-models-distinct", () =>
        {
            // Every pair that rendered; a model that failed its own check is already red there.
            var rendered = NnrModels.Where(m => outputs.ContainsKey("nnr-" + m)).ToArray();
            Assert(rendered.Length >= 2, "Fewer than two NNR models rendered, so the models cannot be compared");
            Assert(rendered.Length == NnrModels.Length || Mutation != null, "An NNR model did not render; see its own check");
            for (int i = 0; i < NnrModels.Length; i++)
                for (int j = i + 1; j < NnrModels.Length; j++)
                {
                    if (!outputs.ContainsKey("nnr-" + NnrModels[i]) || !outputs.ContainsKey("nnr-" + NnrModels[j])) continue;
                    var x = outputs["nnr-" + NnrModels[i]]; var y = outputs["nnr-" + NnrModels[j]];
                    double d = 0, e = 0;
                    for (int k = Rate; k < n - Rate / 2; k++) { d += (x[k] - y[k]) * (double)(x[k] - y[k]); e += (double)x[k] * x[k]; }
                    double db = 10 * Math.Log10(d / Math.Max(1e-30, e) + 1e-30);
                    Assert(db > -40, $"NNR {NnrModels[i]} and {NnrModels[j]} differ by only {db:0.0} dB: the model choice is not reaching the helper");
                }
        }));

        tests.Add(("cw-pitch", () =>
        {
            // A keyed station at 650 Hz and a steady carrier at 1500 Hz, 10 dB louder than the station.
            var s = MakeSynthCw(20, 650, -6, 51, carrierHz: 1500, carrierOverNoiseDb: 4);
            var pitch = FindPitch(s.Mix());
            Assert(Math.Abs(pitch.Hz - 650) <= 6, $"Pitch finder chose {pitch.Hz:0} Hz; the keyed station is at 650 Hz and the carrier at 1500 Hz");
        }));
        var cw = MakeSynthCw(20, 700, 0, 53);
        var reference = cw.Mix();
        var cleaned = cw.Mix(Math.Pow(10, -10 / 20.0));
        tests.Add(("cw-separation", () =>
        {
            var mask = MakeMask(reference, 700);
            var r = Measure(reference, mask, 700, 200, 3000);
            var c = Measure(cleaned, mask, 700, 200, 3000);
            double fell = c.Separation - r.Separation;
            Assert(Math.Abs(fell - 10) <= 1.5, $"Noise lowered 10 dB under an unchanged tone measured {fell:0.0} dB");
            Assert(Math.Abs(c.RestBelowStation - r.RestBelowStation - 10) <= 1.5, $"The rest of the passband, lowered 10 dB, measured {c.RestBelowStation - r.RestBelowStation:0.0} dB");
        }));
        tests.Add(("cw-align", () =>
        {
            var mask = MakeMask(reference, 700);
            var late = new float[cleaned.Length + 3000];
            cleaned.CopyTo(late, 3000);
            var r = Measure(reference, mask, 700, 200, 3000);
            var c = Measure(late, mask, 700, 200, 3000);
            Assert(Math.Abs(c.Lag - 6) <= 1, $"A file 62.5 ms late was aligned {c.Lag * 10} ms late");
            Assert(Math.Abs(c.Separation - r.Separation - 10) <= 1.5, $"The late file's 10 dB measured {c.Separation - r.Separation:0.0} dB");
        }));
        tests.Add(("narrow-filter", () =>
        {
            var impulse = new float[40000]; impulse[20000] = 1;
            var y = NarrowBand(impulse, 700, 250);
            int peak = Array.IndexOf(y, y.Max());
            Assert(peak == 20000, $"The narrow filter's impulse peaks at {peak}, not 20000: its delay is not removed");
            double Gain(double hz)
            {
                var tone = Enumerable.Range(0, Rate * 2).Select(i => (float)Math.Sin(2 * Math.PI * hz * i / Rate)).ToArray();
                var o = NarrowBand(tone, 700, 250);
                double a = 0, b = 0;
                for (int i = Rate / 2; i < Rate * 3 / 2; i++) { a += (double)tone[i] * tone[i]; b += (double)o[i] * o[i]; }
                return 10 * Math.Log10(b / a + 1e-30);
            }
            double centre = Gain(700), edge = Gain(825), outside = Gain(950);
            Assert(Math.Abs(centre) <= .05, $"Centre gain {centre:0.00} dB");
            Assert(edge <= -3 && edge >= -9, $"Gain at the 125 Hz edge {edge:0.0} dB");
            Assert(outside <= -60, $"Gain 250 Hz from the centre {outside:0.0} dB");
        }));
    }

    static double GapPower(float[] x, bool[] keyed)
    {
        int guard = (int)(.03 * Rate); double s = 0; long c = 0;
        for (int i = Rate; i < x.Length - Rate / 2; i++)
        {
            if (keyed[i]) continue;
            bool near = false;
            for (int j = Math.Max(0, i - guard); j <= Math.Min(keyed.Length - 1, i + guard) && !near; j += 48) near = keyed[j];
            if (near) continue;
            s += (double)x[i] * x[i]; c++;
        }
        return s / Math.Max(1, c);
    }

    static double KeyPower(float[] x, bool[] keyed)
    {
        double s = 0; long c = 0;
        for (int i = Rate; i < x.Length - Rate / 2; i++) if (keyed[i]) { s += (double)x[i] * x[i]; c++; }
        return s / Math.Max(1, c);
    }

    /// <summary>The lag, in ms, between two keyed envelopes near a pitch, at 1 ms resolution over plus or minus 30 ms.</summary>
    static int EnvelopeLagMs(float[] reference, float[] file, double pitch)
    {
        double[] Env(float[] x)
        {
            var b = NarrowBand(x, pitch, 250);
            var e = new double[b.Length / 48];
            for (int i = 0; i < e.Length; i++) { double s = 0; for (int j = 0; j < 48; j++) s += (double)b[i * 48 + j] * b[i * 48 + j]; e[i] = s; }
            double m = e.Average();
            return e.Select(v => v - m).ToArray();
        }
        var a = Env(reference); var f = Env(file);
        int best = 0; double bestV = double.MinValue;
        for (int lag = -30; lag <= 30; lag++)
        {
            double s = 0;
            for (int i = 1000; i < a.Length - 1000; i++) s += a[i] * f[i + lag];
            if (s > bestV) { bestV = s; best = lag; }
        }
        return best;
    }
}
