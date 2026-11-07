namespace NrBench;
// Sprint 49 bench CW repair, round 2: the controls the Codex re-check asked for. Each was first
// written against d03deb33 and shown failing there; the repair report has the red runs.
//   cw-deep-fade     six seeds, fades of 15, 20 and 25 dB. Deleting the faded half must never
//                    improve any figure, and where the faded half is scorable the deletion must
//                    show as elements lost. Where it is not scorable, nothing may be claimed for it.
//   cw-coexist       a station 400 Hz away, 15 dB louder, keyed independently and named with
//                    --station, through the commands' own mask path. Deleting the main station
//                    only where the two key at once must read as harm.
//   cw-pitch-narrow  a keyed station behind a 350 Hz and a 135 Hz radio filter, the band inside
//                    pumping with an AGC and a floor 70 dB down outside it that rises with each
//                    key-down (as on 2026-11-04 takes 4 and 5): the pitch finder must choose the
//                    station, not the passband's edge.
// Their mutations (selftest --mutation <name>), each of which must turn its check red:
//   cw-gap-unanchored    gaps need no strong keying either side (round 3: and no cap on the quiet stretch either): "cw-deep-fade" fails
//   cw-veto-any-louder   d03deb33's veto (any keyed, louder named station): "cw-coexist" fails
//   cw-pitch-wholeband   the pitch searched over 200 to 3000 Hz with the band-wide median: "cw-pitch-narrow" fails
//   cw-passband-whole    the passband read from the whole span at once: "cw-pitch-step" fails
partial class BenchApp
{
    static readonly int[] DeepFadeSeeds = { 53, 54, 55, 56, 57, 58 };

    /// <summary>
    /// A station behind a radio filter, as the 2026-11-04 takes 4 and 5 are: the band and the station
    /// pumped by a fast AGC, and outside the passband a floor about 70 dB down that rises 15 dB with
    /// each key-down (measured there: the band-wide median swings 15 to 16 dB with the keying), so
    /// taking the band-wide median out as "common movement" removes the station's own keying.
    /// </summary>
    static float[] NarrowFilterScene(double stationHz, double passLo, double passHi, int seed)
    {
        var s = MakeSynthCw(30, stationHz, 6, seed);
        // The radio's filter: the bench's own steep band-pass, so outside it there is only the floor.
        var band = NarrowBand(s.Noise, (passLo + passHi) / 2, passHi - passLo);
        var tone = NarrowBand(s.Tone, (passLo + passHi) / 2, passHi - passLo);
        double inBand = Math.Sqrt(band.Average(v => (double)v * v));
        var rng = new Random(seed + 7);
        var mix = new float[band.Length];
        double gain = 1, down = Math.Pow(10, -10 / 20.0), step = 1.0 / (.005 * Rate);
        for (int i = 0; i < mix.Length; i++)
        {
            gain = s.Keyed[i] ? Math.Max(down, gain - step) : Math.Min(1, gain + step / 20);
            // The outside floor: 50 dB under the band's level (about 70 dB under it per hertz), and
            // 15 dB higher while keyed, on the AGC's own time course, as measured on takes 4 and 5.
            double up = (1 - gain) / (1 - down);
            double residue = inBand * Math.Pow(10, -50 / 20.0) * Math.Pow(10, 15 * up / 20);
            mix[i] = (float)((tone[i] + band[i]) * gain + Gaussian(rng) * residue);
        }
        return mix;
    }

    static void AddCwRound2Checks(List<(string Name, Action Test)> tests)
    {
        tests.Add(("cw-deep-fade", () =>
        {
            var failures = new List<string>();
            var info = new List<string>();
            foreach (int seed in DeepFadeSeeds)
                foreach (double fadeDb in new[] { 15.0, 20.0, 25.0 })
                {
                    var (mix, erased, _) = FadeScene(fadeDb, seed);
                    var mask = MakeMask(mix, 700);
                    var r = Measure(mix, mask, 700, 200, 3000, knownLag: 0);
                    var e = Measure(erased, mask, 700, 200, 3000, knownLag: 0);
                    double dSep = e.Separation - r.Separation, dWeak = e.WeakSeparation - r.WeakSeparation, dRest = e.RestBelowStation - r.RestBelowStation;
                    var lost = Survival(e, r);
                    int half = mix.Length / 2;
                    // Scored elements in the faded half that are the STATION's: those whose band power at
                    // least halves when the station is deleted. A noise peak that happens to fall during
                    // a faded key-down is scored as an element, but the station is not what made it, so
                    // deleting the station rightly leaves it.
                    var mixTone = ToneBlocks(mix, 700); var erasedTone = ToneBlocks(erased, 700);
                    double Power(double[] t, CwElement el) => Enumerable.Range(el.Start, el.End - el.Start).Average(b => Math.Pow(10, t[b] / 10));
                    // Round 3: only elements Survival scores (detected in the reference, ElementSignal > 0).
                    // A faded element in the mask that the reference cannot detect against its certain
                    // gaps is not scored, so nothing is claimed for it, which is this check's own rule.
                    int scorableFaded = Enumerable.Range(0, mask.Elements.Length).Count(i => r.ElementSignal[i] > 0 &&
                        mask.Elements[i].Start * CwHop + CwWindow / 2 >= half + Rate / 4 &&
                        Power(erasedTone, mask.Elements[i]) <= .5 * Power(mixTone, mask.Elements[i]));
                    string where = $"seed {seed}, {fadeDb:0} dB";
                    if (dSep > .05 || dWeak > .05 || dRest > .05)
                        failures.Add($"{where}: deleting the faded half improved separation {dSep:+0.00;-0.00;0.00}, weakest third {dWeak:+0.00;-0.00;0.00}, rest {dRest:+0.00;-0.00;0.00} dB");
                    if (scorableFaded > 0 && lost.Kept >= lost.Measured)
                        failures.Add($"{where}: {scorableFaded} faded elements were scored, yet deleting them left {lost.Kept} of {lost.Measured} kept");
                    info.Add($"{seed}/{fadeDb:0}: {dSep:+0.00;-0.00;0.00} dB, {lost.Kept}/{lost.Measured} kept, {scorableFaded} faded scored");
                }
            Console.WriteLine("INFO cw-deep-fade (seed/fade: separation change on deletion, kept, faded elements scored): " + string.Join("; ", info) + ".");
            Assert(failures.Count == 0, string.Join(" | ", failures));
        }));
        tests.Add(("cw-coexist", () =>
        {
            // Codex's construction: the main station at 700 Hz, an independently keyed one at 1100 Hz,
            // 15 dB louder, over the first scene's noise. Delete the main station's tone only inside
            // the other's key-downs, 15 ms clear of its edges.
            var main = MakeSynthCw(20, 700, 0, 71);
            var far = MakeSynthCw(20, 1100, 15, 73);
            var mix = main.Mix().Select((v, i) => v + far.Tone[i]).ToArray();
            int clear = (int)(.015 * Rate);
            var deleted = (float[])mix.Clone();
            long removed = 0, mainKeyed = 0;
            for (int i = 0; i < mix.Length; i++)
            {
                if (main.Keyed[i]) mainKeyed++;
                bool inside = true;
                for (int j = Math.Max(0, i - clear); j <= Math.Min(mix.Length - 1, i + clear) && inside; j += 48) inside = far.Keyed[j];
                if (inside) { deleted[i] -= main.Tone[i]; if (main.Keyed[i]) removed++; }
            }
            var stations = new[] { new CwStationSpec(1100, 0, double.PositiveInfinity) };
            var masks = CommandMasks(mix, 700, stations);
            var others = new[] { 1100.0 };
            var r = Measure(mix, masks.Main, 700, 200, 3000, others, 0);
            var e = Measure(deleted, masks.Main, 700, 200, 3000, others, 0);
            var lost = Survival(e, r);
            Console.WriteLine($"INFO cw-coexist: {100.0 * removed / mainKeyed:0} percent of the main station's keyed samples deleted; separation {e.Separation - r.Separation:+0.0;-0.0;0.0} dB, elements {lost.StationChangeDb:+0.0;-0.0;0.0} dB, {lost.Kept} of {lost.Measured} kept; {100.0 * masks.Main.UncertainBlocks / masks.Main.Key.Length:0} percent unscored.");
            // The deletion takes parts of elements (wherever the other station keyed), so it shows as
            // lost level, not as whole elements gone: both the separation and the station's own
            // element-by-element level must fall.
            Assert(e.Separation - r.Separation <= -3 && lost.StationChangeDb <= -1,
                $"Deleting the main station wherever a station 400 Hz away keyed ({100.0 * removed / mainKeyed:0} percent of its keyed samples) moved separation {e.Separation - r.Separation:+0.0;-0.0;0.0} dB and its elements {lost.StationChangeDb:+0.0;-0.0;0.0} dB ({lost.Kept} of {lost.Measured} kept): the deletion was hidden");
        }));
        tests.Add(("cw-pitch-narrow", () =>
        {
            var failures = new List<string>();
            foreach (var (hz, lo, hi, seed) in new[] { (803.0, 586.0, 926.0, 75), (819.0, 715.0, 850.0, 77) })
            {
                var pitch = FindPitch(NarrowFilterScene(hz, lo, hi, seed));
                if (Math.Abs(pitch.Hz - hz) > 6) failures.Add($"behind {lo:0} to {hi:0} Hz the finder chose {pitch.Hz:0} Hz for the station at {hz:0} Hz");
            }
            Assert(failures.Count == 0, string.Join("; ", failures));
        }));
    }
}
