namespace NrBench;
// Sprint 49 bench CW repair, round 5: Noel's ruling of 2026-11-06 (S19, option a). Engines are ranked
// on element survival and the noise in the rest of the passband; the at-pitch gap figures are shown,
// labelled, never ranked, measured on round 2's wider gap set. These controls hold the ruling.
//   cw-ranked-deletion  three fade constructions: Codex's round-3 dips inside dashes (a dash faded in
//                       its middle is the same signal there as dot-space-dot; 3 seeds, 15 to 35 dB),
//                       round 2's short fades (64 scenes) and round 1's deep half fades (18 scenes).
//                       Deleting the faded tone must never improve a RANKED figure; real noise reduction
//                       (the noise a further 10 dB down) must read about 10 dB on the ranked noise
//                       figure and keep the elements. The at-pitch figure is reported, not asserted:
//                       it is known to be fooled here, which is why it is not ranked.
//   cw-at-pitch-wide    the labelled at-pitch figure is read on round 2's wider gap set, not round 3's.
// Their mutations (selftest --mutation <name>), each of which must turn its check red:
//   cw-rank-gaps-fell        the at-pitch "gaps fell" back in the ranking as its noise figure: "cw-ranked-deletion" fails
//   cw-rank-weak-at-pitch    the at-pitch "weakest third above the gaps" back in the ranking: "cw-ranked-deletion" fails
//   cw-at-pitch-capped       the labelled figure left on round 3's capped gaps: "cw-at-pitch-wide" fails
partial class BenchApp
{
    /// <summary>
    /// Codex's round-3 construction: 20 s at 700 Hz, Morse T at 20 wpm (180 ms dash, 180 ms space, 5 ms
    /// ramps), every third dash with a 60 ms dip of depthDb in its middle; band noise at the seed, 10 dB
    /// down. Deleted: only the faded tone removed (each dipped dash becomes dot-space-dot). Cleaned: the
    /// tone untouched, the noise a further 10 dB down.
    /// </summary>
    static (float[] Mix, float[] Deleted, float[] Cleaned) DashDipScene(int seed, double depthDb)
    {
        var s = MakeSynthCw(20, 700, 0, seed);
        int n = s.Noise.Length;
        var mix = new float[n]; var deleted = new float[n]; var cleaned = new float[n];
        double env = 0, g = Math.Pow(10, -depthDb / 20), down = Math.Pow(10, -10 / 20.0), quieter = Math.Pow(10, -20 / 20.0);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate - .5;
            int ch = (int)Math.Floor(t / .36);
            double phase = t - ch * .36;
            bool on = t >= 0 && t < 19 && phase < .18;
            bool faded = on && ch % 3 == 1 && phase >= .06 && phase < .12;
            env = Math.Clamp(env + (on ? 1.0 / 240 : -1.0 / 240), 0, 1);
            double v = .02 * Math.Sqrt(2) * (.5 - .5 * Math.Cos(Math.PI * env)) * Math.Sin(2 * Math.PI * 700 * i / Rate) * (faded ? g : 1);
            mix[i] = (float)(v + s.Noise[i] * down);
            deleted[i] = faded ? (float)(s.Noise[i] * down) : mix[i];
            cleaned[i] = (float)(v + s.Noise[i] * quieter);
        }
        return (mix, deleted, cleaned);
    }

    static void AddCwRound5Checks(List<(string Name, Action Test)> tests)
    {
        tests.Add(("cw-ranked-deletion", () =>
        {
            var scenes = new List<(string Name, Func<(float[] Mix, float[] Deleted, float[] Cleaned)> Make)>();
            foreach (int seed in new[] { 83, 89, 97 })
                foreach (double depth in new[] { 15.0, 20, 25, 30, 35 })
                    scenes.Add(($"dash dip, seed {seed}, {depth:0} dB", () => DashDipScene(seed, depth)));
            foreach (int seed in ShortFadeSeeds)
                foreach (double depth in ShortFadeDepths)
                    foreach (double len in ShortFadeSeconds)
                        scenes.Add(($"short fade, seed {seed}, {depth:0} dB for {len:0.##} s", () => { var f = ShortFadeScene(seed, depth, len); return (f.Mix, f.Deleted, f.Cleaned); }));
            foreach (int seed in DeepFadeSeeds)
                foreach (double depth in new[] { 15.0, 20.0, 25.0 })
                    scenes.Add(($"half fade, seed {seed}, {depth:0} dB", () =>
                    {
                        var (mix, erased, _) = FadeScene(depth, seed);
                        var s = MakeSynthCw(20, 700, 0, seed);
                        double fade = Math.Pow(10, -depth / 20), quieter = Math.Pow(10, -20 / 20.0);
                        var cleaned = s.Tone.Select((v, i) => (float)((i >= s.Tone.Length / 2 ? v * fade : v) + s.Noise[i] * quieter)).ToArray();
                        return (mix, erased, cleaned);
                    }));
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            var info = new System.Collections.Concurrent.ConcurrentBag<(int Order, string Text)>();
            int atPitchFooled = 0;
            string sampleLine = "";
            Parallel.For(0, scenes.Count, k =>
            {
                var (name, make) = scenes[k];
                var (mix, deleted, cleaned) = make();
                var mask = CommandMasks(mix, 700, Array.Empty<CwStationSpec>()).Main;
                var r = MeasureBoth(mix, mask, 700, 200, 3000, null, 0);
                var e = MeasureBoth(deleted, mask, 700, 200, 3000, null, 0);
                var c = MeasureBoth(cleaned, mask, 700, 200, 3000, null, 0);
                var del = Ranked(e, r);
                var clean = Ranked(c, r);
                var bad = new List<string>();
                // Deletion: nothing ranked may improve. (Kept cannot exceed what the reference scores.)
                if (del.StationChangeDb > .05) bad.Add($"station change {del.StationChangeDb:+0.00;-0.00;0.00} dB");
                if (del.WeakAgainstStrongDb > .05) bad.Add($"weakest third {del.WeakAgainstStrongDb:+0.00;-0.00;0.00} dB");
                if (del.StationLevelDb > .05) bad.Add($"station level {del.StationLevelDb:+0.00;-0.00;0.00} dB");
                if (double.IsNaN(del.RestFellDb) || del.RestFellDb > .05) bad.Add($"rest of the passband {del.RestFellDb:+0.00;-0.00;0.00} dB");
                // Real noise reduction must read as improvement on the ranked noise figure, and keep.
                if (double.IsNaN(clean.RestFellDb) || Math.Abs(clean.RestFellDb - 10) > 1) bad.Add($"real noise reduction read {clean.RestFellDb:+0.0;-0.0;0.0} dB on the rest of the passband");
                if (clean.Kept < clean.Measured * .9) bad.Add($"real noise reduction kept {clean.Kept} of {clean.Measured}");
                if (bad.Count > 0) failures.Add($"{name}: deletion improved a ranked figure or the control failed: " + string.Join("; ", bad));
                double atPitch = e.AtPitch.Separation - r.AtPitch.Separation;
                if (atPitch > .05) Interlocked.Increment(ref atPitchFooled);
                info.Add((k, $"{name}: ranked on deletion rest {del.RestFellDb:+0.00;-0.00;0.00}, weakest third {del.WeakAgainstStrongDb:+0.00;-0.00;0.00}, station {del.StationChangeDb:+0.00;-0.00;0.00}, level {del.StationLevelDb:+0.00;-0.00;0.00}; real noise reduction rest {clean.RestFellDb:+0.0;-0.0;0.0}; not ranked, at the pitch, deletion {atPitch:+0.00;-0.00;0.00} dB"));
                if (k == 1) sampleLine = CwLine("deleted", e, r);
            });
            Console.WriteLine("INFO cw-ranked-deletion: " + string.Join("; ", info.OrderBy(i => i.Order).Select(i => i.Text)) + ".");
            Console.WriteLine($"INFO cw-ranked-deletion: the unranked at-pitch figure rewarded deletion by more than 0.05 dB in {atPitchFooled} of {scenes.Count} scenes.");
            // The line must say what is ranked and what is not, and must not claim gaps it cannot prove.
            if (!sampleLine.Contains("ranked:") || !sampleLine.Contains("Not ranked") || sampleLine.Contains("prove empty"))
                failures.Add("the line does not separate ranked from unranked figures plainly: " + sampleLine);
            Assert(failures.IsEmpty, $"{failures.Count} of {scenes.Count} scenes: " + string.Join(" | ", failures.Order()));
        }));
        tests.Add(("cw-at-pitch-wide", () =>
        {
            // Codex's dash scene at 20 dB: round 3's cap keeps only the dips (about 50 blocks); round 2's
            // set keeps the real character spaces too (about 1,500).
            var (mix, _, _) = DashDipScene(83, 20);
            var mask = CommandMasks(mix, 700, Array.Empty<CwStationSpec>()).Main;
            var r = MeasureBoth(mix, mask, 700, 200, 3000, null, 0);
            int capped = mask.Key.Count(k => k == false), wide = r.AtPitch.GapBlocks;
            Console.WriteLine($"INFO cw-at-pitch-wide: the labelled at-pitch figure reads {wide} gap blocks; round 3's capped set has {capped}.");
            Assert(wide >= 1000 && wide >= 10 * capped, $"The labelled at-pitch figure read {wide} gap blocks against the capped set's {capped}: it is not on round 2's wider set");
        }));
    }
}
