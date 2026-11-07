namespace NrBench;
// Sprint 49 bench CW repair, round 3: the controls the second Codex re-check asked for. Each was
// first written against f5ad7e4d and shown failing there; the repair report has the red runs.
//   cw-short-fade           Codex's construction class: four seeds, fades of 15, 20, 25 and 30 dB,
//                           lasting 0.18, 0.36, 0.6 and 1 s, one every 2 s (64 scenes). Deleting the
//                           tone inside the fades must never improve any figure; where faded
//                           elements are scored, their deletion must show as lost; faded key-down
//                           must not be scored as gap. The positive control: the noise 10 dB down
//                           in every scene must still read as about 10 dB and as kept.
//   cw-pitch-filter-change  a station behind a 135 or 350 Hz radio filter for part of the take and
//                           200 to 3000 Hz for the rest, pumped by an AGC, at 650 and 819 Hz: the
//                           commands' pitch decision must find the station or refuse, never choose
//                           another pitch. Each half alone must be found (the positive control).
//   cw-passband-degenerate  a pitch outside the receiver's passband (given, or chosen) must be
//                           refused, not measured in a passband of no width.
//   cw-truncated            cwmeasure on an external file that is only the first 4 s of the 20 s
//                           reference must refuse; and the measure itself must count every element
//                           the file does not cover as lost, and say how much it does not cover.
// Their mutations (selftest --mutation <name>), each of which must turn its check red:
//   cw-gap-run-uncapped            a gap may sit in any quiet stretch between anchors: "cw-short-fade" fails
//   cw-pitch-hull                  one passband (the hull) and one common-mode decision for the take: "cw-pitch-filter-change" fails
//   cw-passband-degenerate-accepted  no refusal of a passband the pitch is not in: "cw-passband-degenerate" fails
//   cw-length-unchecked            cwmeasure accepts a file shorter than the reference: "cw-truncated" fails
//   cw-uncovered-dropped           elements outside the file leave the count: "cw-truncated" fails
partial class BenchApp
{
    static readonly int[] ShortFadeSeeds = { 53, 55, 59, 71 };
    static readonly double[] ShortFadeDepths = { 15, 20, 25, 30 };
    static readonly double[] ShortFadeSeconds = { .18, .36, .6, 1.0 };

    /// <summary>
    /// Codex's short-fade scene: 20 s at 700 Hz, the tone 0 dB over the band's noise and the noise
    /// then 10 dB down; the tone alone faded by depthDb for the first `seconds` of every 2 s from 1 s
    /// to 19 s. Deleted: the tone removed inside those fades only, nothing else touched. Cleaned:
    /// the noise a further 10 dB down, the tone (fades and all) untouched.
    /// </summary>
    static (float[] Mix, float[] Deleted, float[] Cleaned, bool[] Keyed, Func<int, bool> Faded) ShortFadeScene(int seed, double depthDb, double seconds)
    {
        var s = MakeSynthCw(20, 700, 0, seed);
        double g = Math.Pow(10, -depthDb / 20), noise = Math.Pow(10, -10 / 20.0), quieter = Math.Pow(10, -20 / 20.0);
        bool Faded(int i) => i >= Rate && i < 19 * Rate && (i / (double)Rate - 1) % 2 < seconds;
        int n = s.Tone.Length;
        var mix = new float[n]; var deleted = new float[n]; var cleaned = new float[n];
        for (int i = 0; i < n; i++)
        {
            double tone = s.Tone[i] * (Faded(i) ? g : 1);
            mix[i] = (float)(tone + s.Noise[i] * noise);
            deleted[i] = Faded(i) ? (float)(s.Noise[i] * noise) : mix[i];
            cleaned[i] = (float)(tone + s.Noise[i] * quieter);
        }
        return (mix, deleted, cleaned, s.Keyed, Faded);
    }

    /// <summary>
    /// Codex's changing-filter scene: the same station, keying and AGC, behind a narrow radio filter
    /// (hz - 0.65 width to hz + 0.35 width) up to `narrowShare` of the take, and 200 to 3000 Hz after.
    /// </summary>
    static float[] FilterChangeScene(double hz, double width, double narrowShare, int seed = 83)
    {
        var a = NarrowFilterScene(hz, hz - width * .65, hz + width * .35, seed);
        var b = NarrowFilterScene(hz, 200, 3000, seed);
        int split = (int)(a.Length * narrowShare);
        Array.Copy(b, split, a, split, a.Length - split);
        return a;
    }

    static void AddCwRound3Checks(List<(string Name, Action Test)> tests)
    {
        tests.Add(("cw-short-fade", () =>
        {
            var cases = (from seed in ShortFadeSeeds from depth in ShortFadeDepths from len in ShortFadeSeconds select (seed, depth, len)).ToArray();
            int totalFadedScored = 0, totalFadedLost = 0;
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            var info = new System.Collections.Concurrent.ConcurrentBag<(int Order, string Text)>();
            Parallel.For(0, cases.Length, k =>
            {
                var (seed, depth, len) = cases[k];
                var (mix, deleted, cleaned, keyed, faded) = ShortFadeScene(seed, depth, len);
                var mask = CommandMasks(mix, 700, Array.Empty<CwStationSpec>()).Main;
                var r = Measure(mix, mask, 700, 200, 3000, null, 0);
                var e = Measure(deleted, mask, 700, 200, 3000, null, 0);
                var c = Measure(cleaned, mask, 700, 200, 3000, null, 0);
                double dSep = e.Separation - r.Separation, dWeak = e.WeakSeparation - r.WeakSeparation, dRest = e.RestBelowStation - r.RestBelowStation;
                var lost = Survival(e, r);
                var kept = Survival(c, r);
                // Truly keyed blocks inside a fade, clear of every key edge by 15 ms, scored as gap.
                int guard = (int)(.015 * Rate), fadedKeyAsGap = 0, gaps = mask.Key.Count(x => x == false);
                for (int b = 0; b < mask.Key.Length; b++)
                {
                    int at = b * CwHop + CwWindow / 2;
                    if (at - guard < 0 || at + guard >= keyed.Length || !faded(at) || !keyed[at] || !keyed[at - guard] || !keyed[at + guard]) continue;
                    if (mask.Key[b] == false) fadedKeyAsGap++;
                }
                // Scored elements that are the station's faded key-down: their band power at least
                // halves when the faded tone is deleted (as cw-deep-fade counts them).
                var mixTone = ToneBlocks(mix, 700); var delTone = ToneBlocks(deleted, 700);
                double Power(double[] t, CwElement el) => Enumerable.Range(el.Start, el.End - el.Start).Average(b => Math.Pow(10, t[b] / 10));
                var fadedIdx = Enumerable.Range(0, mask.Elements.Length).Where(i => Power(delTone, mask.Elements[i]) <= .5 * Power(mixTone, mask.Elements[i])).ToList();
                int fadedScored = fadedIdx.Count(i => r.ElementSignal[i] > 0);
                // Lost as Survival counts it: not detected, or more than 10 dB under the station's own change.
                int fadedLost = fadedIdx.Count(i => r.ElementSignal[i] > 0 &&
                    10 * Math.Log10((Math.Max(0, e.ElementSignal[i]) + r.ElementSignal[i] * 1e-3) / r.ElementSignal[i]) - lost.StationChangeDb < -10 ||
                    r.ElementSignal[i] > 0 && !(e.ElementSignal[i] > 0));
                Interlocked.Add(ref totalFadedScored, fadedScored); Interlocked.Add(ref totalFadedLost, fadedLost);
                string where = $"seed {seed}, {depth:0} dB for {len:0.##} s";
                var bad = new List<string>();
                if (dSep > .05 || dWeak > .05 || dRest > .05)
                    bad.Add($"deleting the faded tone improved separation {dSep:+0.00;-0.00;0.00}, weakest third {dWeak:+0.00;-0.00;0.00}, rest {dRest:+0.00;-0.00;0.00} dB ({lost.Kept} of {lost.Measured} kept)");
                // A detector at twice the gaps' scatter passes noise alone a few times in a hundred, so one
                // marginal faded element can read as kept after its deletion. Three or more cannot all.
                if (fadedScored >= 3 && lost.Kept >= lost.Measured)
                    bad.Add($"{fadedScored} faded elements were scored, yet deleting them left {lost.Kept} of {lost.Measured} kept");
                if (fadedKeyAsGap > Math.Max(2, gaps / 200))
                    bad.Add($"{fadedKeyAsGap} faded key-down blocks scored as gap, of {gaps} gap blocks");
                // The positive control: real noise reduction must still be seen, and kept.
                if (Math.Abs(c.Separation - r.Separation - 10) > 2 || kept.Kept < kept.Measured * .9)
                    bad.Add($"the noise 10 dB down read {c.Separation - r.Separation:+0.0;-0.0;0.0} dB, {kept.Kept} of {kept.Measured} kept");
                if (bad.Count > 0) failures.Add($"{where}: " + string.Join("; ", bad));
                info.Add((k, $"{seed}/{depth:0}/{len:0.##}: {dSep:+0.00;-0.00;0.00} dB, {lost.Kept}/{lost.Measured} kept, {fadedLost}/{fadedScored} faded elements lost, {fadedKeyAsGap} faded key-down as gap, {100 * r.UnscoredShare:0}% unscored"));
            });
            // Reported, not asserted: a scored faded element only about 9 dB above the noise cannot fall
            // past the 10 dB kept line when deleted, because the noise peaks that helped it be seen
            // stay. That is the kept line's reach, not a reward for deleting (round 3 report, S19).
            Console.WriteLine($"INFO cw-short-fade: across the 64 scenes, {totalFadedLost} of {totalFadedScored} scored faded elements read as lost when deleted.");
            Console.WriteLine("INFO cw-short-fade (seed/depth/length: separation change on deletion, kept, faded key-down blocks scored as gap, unscored): " +
                              string.Join("; ", info.OrderBy(i => i.Order).Select(i => i.Text)) + ".");
            Assert(failures.IsEmpty, $"{failures.Count} of {cases.Length} scenes: " + string.Join(" | ", failures.Order()));
        }));
        tests.Add(("cw-pitch-filter-change", () =>
        {
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            var info = new System.Collections.Concurrent.ConcurrentBag<string>();
            var cases = (from hz in new[] { 650.0, 819.0 } from width in new[] { 135.0, 350.0 } from share in new[] { 0.0, .25, .5, .75, 1.0 } select (hz, width, share)).ToArray();
            Parallel.ForEach(cases, cs =>
            {
                var (hz, width, share) = cs;
                // Share 0 is the wide filter alone and 1 the narrow alone: both must be found, not refused.
                var x = FilterChangeScene(hz, width, share);
                string where = $"{hz:0} Hz, {width:0} Hz filter for {100 * share:0} percent";
                try
                {
                    var c = ChoosePitch(x, null);
                    bool right = Math.Abs(c.Pitch - hz) <= 6;
                    if (!right) failures.Add($"{where}: chose {c.Pitch:0.0} Hz, passband {c.Lo:0} to {c.Hi:0} Hz");
                    else if (!(c.Lo <= hz && hz <= c.Hi && c.Hi - c.Lo >= 40)) failures.Add($"{where}: found the station but read the passband as {c.Lo:0} to {c.Hi:0} Hz");
                    info.Add($"{where}: {c.Pitch:0.0} Hz");
                }
                catch (InvalidOperationException ex)
                {
                    if (share == 0 || share == 1) failures.Add($"{where} (one filter throughout): refused, {ex.Message}");
                    info.Add($"{where}: refused");
                }
            });
            Console.WriteLine("INFO cw-pitch-filter-change: " + string.Join("; ", info.Order()) + ".");
            Assert(failures.IsEmpty, string.Join(" | ", failures.Order()));
        }));
        tests.Add(("cw-passband-degenerate", () =>
        {
            // Behind 422 to 772 Hz, a pitch of 1500 Hz is outside anything the receiver let through.
            var x = NarrowFilterScene(650, 650 - 350 * .65, 650 + 350 * .35, 83);
            bool refused = false; string said = "";
            try { var c = ChoosePitch(x, 1500); said = $"measured at {c.Pitch:0} Hz in a passband of {c.Lo:0} to {c.Hi:0} Hz"; }
            catch (InvalidOperationException) { refused = true; }
            Assert(refused, $"A given pitch outside the receiver's passband was accepted: {said}");
            // The positive control: the station's own pitch, given, is accepted.
            var ok = ChoosePitch(x, 650);
            Assert(ok.Lo <= 650 && 650 <= ok.Hi, $"The station's own pitch read a passband of {ok.Lo:0} to {ok.Hi:0} Hz");
        }));
        tests.Add(("cw-truncated", () =>
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "selftest");
            Directory.CreateDirectory(dir);
            var s = MakeSynthCw(20, 700, 0, 53);
            var reference = s.Mix(Math.Pow(10, -10 / 20.0));
            var prefix = reference[..(4 * Rate)];
            string refPath = Path.Combine(dir, "round3-reference.wav"), cutPath = Path.Combine(dir, "round3-first-4s.wav");
            WriteWav(refPath, reference, 1, 1);
            WriteWav(cutPath, prefix, 1, 1);
            // The command: a file shorter than the reference must be refused; the reference itself accepted.
            var console = Console.Out;
            bool refused = false; int same;
            try
            {
                Console.SetOut(TextWriter.Null);
                // Refused: an exception, or the command's own failure exit.
                try { refused = CwMeasureCommand(new[] { refPath, cutPath, "--pitch", "700", "--aligned" }) != 0; }
                catch (InvalidOperationException) { refused = true; }
                same = CwMeasureCommand(new[] { refPath, refPath, "--pitch", "700", "--aligned" });
            }
            finally { Console.SetOut(console); }
            Assert(same == 0, "cwmeasure refused the reference measured against itself");
            // The measure: every element the file does not reach counts as lost, and the line says so.
            var mask = CommandMasks(reference, 700, Array.Empty<CwStationSpec>()).Main;
            var r = Measure(reference, mask, 700, 200, 3000, null, 0);
            var f = Measure(prefix, mask, 700, 200, 3000, null, 0);
            var sv = Survival(f, r);
            int scorable = r.ElementSignal.Count(v => v > 0);
            int beyond = Enumerable.Range(0, mask.Elements.Length).Count(e => r.ElementSignal[e] > 0 && mask.Elements[e].Start * CwHop + CwWindow >= prefix.Length);
            string line = CwLine("first 4 s", f, r);
            Console.WriteLine($"INFO cw-truncated: the 4 s prefix {(refused ? "refused" : "ACCEPTED")} by cwmeasure; measured directly, {sv.Kept} of {sv.Measured} kept, {beyond} of the {scorable} scorable elements beyond its end.");
            Assert(refused, "cwmeasure accepted a file 16 s shorter than its reference");
            Assert(sv.Measured == scorable && sv.Kept <= scorable - beyond,
                $"Measured directly, the 4 s prefix kept {sv.Kept} of {sv.Measured}; the reference scores {scorable} elements, {beyond} of them past the prefix's end");
            Assert(line.Contains("not in this file"), "The prefix's line does not say how much of the span the file does not cover: " + line);
        }));
    }
}
