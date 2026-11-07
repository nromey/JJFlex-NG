namespace NrBench;
// Sprint 49 bench CW repair: the controls the Codex check asked for. Each was first written
// against the measure as it stood (660398b1) and shown failing there; the repair report has
// the red runs. Every one also carries a positive control, so a measure that simply calls
// everything harm cannot pass.
//   cw-fade          a keyed tone that fades 15 dB in its second half (QSB). The faded key-downs
//                    must be classified as key-down, and DELETING the faded half must lower the
//                    station's figures: destroying a weak element is harm, never improvement.
//                    Lowering the noise instead must read as kept. At a 20 dB fade (too faint
//                    to classify) deletion must still never read as improvement.
//   cw-weak-station  a second station 6 dB weaker beside the main one. The pitch finder must
//                    name it; deleting it must not improve the main station's figures, and its
//                    own figures must show the loss. Lowering the noise instead must read as kept.
//   cw-neighbour     a strong neighbour 65 Hz away keying while the main station is silent: named
//                    with --station, its leak must not become the main station's elements.
//   cw-fast          40 wpm, 30 ms dits in strict alternation: the dits must be measured, and a
//                    known 10 dB noise reduction must still read as 10 dB.
//   cw-fresh-noise   the station over NEW noise at the same level must read as no change (the
//                    control on the gap ceiling, which takes gaps from the quieter blocks).
//   cw-noise-only    noise alone: almost nothing may be classified as keying.
//   cw-pitch-step    a keyed station plus a noise band that switches on part way through (the
//                    operator widening the filter): the pitch finder must choose the station.
//   cw-pitch-agc     a fast AGC pumping the whole band with the keying: the station must still win,
//                    clearly ahead of the band's noise.
// Their mutations (selftest --mutation <name>), each of which must turn its check red:
//   cw-global-threshold        the old classifier, one Otsu split over the span: "cw-fade" fails
//   cw-rest-includes-stations  the rest of the passband keeps the other station: "cw-weak-station" fails
//   cw-edge-guard              the edge guard widened to 20 ms either side: "cw-fast" fails
//   cw-no-station-veto         a named neighbour's leak is not vetoed: "cw-neighbour" fails
//   cw-pitch-longspan          the old whole-span pitch score: "cw-pitch-step" fails
//   cw-pitch-common-mode       the band's common movement left in: "cw-pitch-agc" fails
partial class BenchApp
{
    /// <summary>The faded-QSB scene: Codex's reproduction, noise 10 dB down, the tone's second half faded.</summary>
    static (float[] Mix, float[] Erased, bool[] Keyed) FadeScene(double fadeDb, int seed = 53)
    {
        var s = MakeSynthCw(20, 700, 0, seed);
        double noiseGain = Math.Pow(10, -10 / 20.0), fade = Math.Pow(10, -fadeDb / 20);
        int half = s.Tone.Length / 2;
        var faded = s.Tone.Select((v, i) => (float)(i >= half ? v * fade : v)).ToArray();
        var mix = faded.Select((v, i) => (float)(v + s.Noise[i] * noiseGain)).ToArray();
        var erased = mix.Select((v, i) => i >= half ? v - faded[i] : v).ToArray();
        return (mix, erased, s.Keyed);
    }

    /// <summary>A strong station at 700 Hz and one weakBelowDb weaker, keyed independently, at 820 Hz. Erased is the scene without the weaker one.</summary>
    static (float[] Mix, float[] Erased, double WeakHz) WeakStationScene(double weakBelowDb)
    {
        var main = MakeSynthCw(20, 700, 0, 57);
        var weak = MakeSynthCw(20, 820, -weakBelowDb, 59);
        var mix = main.Mix().Select((v, i) => v + weak.Tone[i]).ToArray();
        return (mix, main.Mix(), 820);
    }

    /// <summary>40 wpm dits in strict alternation (30 ms on, 30 ms off: the Codex case), the same 5 ms edges as MakeSynthCw, over the same noise.</summary>
    static SynthCw FastCw(double seconds, int seed)
    {
        var s = MakeSynthCw(seconds, 700, 0, seed);
        int n = s.Tone.Length, unit = (int)(.03 * Rate), at = Rate / 2;
        var keyed = new bool[n];
        while (at < n - Rate / 2)
        {
            for (int i = at; i < Math.Min(n, at + unit); i++) keyed[i] = true;
            at += 2 * unit;
        }
        double amp = .02 * Math.Sqrt(2);
        var tone = new float[n];
        int ramp = (int)(.005 * Rate);
        double env = 0;
        for (int i = 0; i < n; i++)
        {
            env = Math.Clamp(env + (keyed[i] ? 1.0 / ramp : -1.0 / ramp), 0, 1);
            tone[i] = (float)(amp * (.5 - .5 * Math.Cos(Math.PI * env)) * Math.Sin(2 * Math.PI * 700 * i / Rate));
        }
        return new(tone, s.Noise, keyed);
    }

    static void AddCwFadeChecks(List<(string Name, Action Test)> tests)
    {
        tests.Add(("cw-fade", () =>
        {
            var (mix, erased, keyed) = FadeScene(15);
            var mask = MakeMask(mix, 700);
            // Classification against the known keying, in the faded half, away from every edge.
            int truth = 0, asKey = 0, asGap = 0;
            int guard = (int)(.015 * Rate);
            for (int b = mask.Key.Length / 2 + 10; b < mask.Key.Length; b++)
            {
                int c = b * CwHop + CwWindow / 2;
                if (c - guard < 0 || c + guard >= keyed.Length || keyed[c - guard] != keyed[c] || keyed[c + guard] != keyed[c] || !keyed[c]) continue;
                truth++;
                if (mask.Key[b] == true) asKey++;
                if (mask.Key[b] == false) asGap++;
            }
            Assert(asKey >= truth * .8, $"Of {truth} faded key-down blocks, {asKey} were classified key-down and {asGap} as gap");
            var r = Measure(mix, mask, 700, 200, 3000, knownLag: 0);
            var e = Measure(erased, mask, 700, 200, 3000, knownLag: 0);
            Assert(e.WeakSeparation < r.WeakSeparation - 6, $"Deleting the faded half moved the weakest third from {r.WeakSeparation:0.0} to {e.WeakSeparation:0.0} dB: deletion must read as harm");
            Assert(e.Separation < r.Separation, $"Deleting the faded half moved key-down above the gaps from {r.Separation:0.0} to {e.Separation:0.0} dB: deletion must not read as improvement");
            var lost = Survival(e, r);
            Assert(lost.WeakAgainstStrongDb <= -10 && lost.Kept <= lost.Measured * .7,
                $"Deleting the faded half: weakest third {lost.WeakAgainstStrongDb:0.0} dB against the strongest half, {lost.Kept} of {lost.Measured} elements kept");
            // The positive control: real noise reduction (the noise a further 10 dB down, every
            // element untouched) must read as kept, and as 10 dB of separation, not as harm.
            var s = MakeSynthCw(20, 700, 0, 53);
            double fade = Math.Pow(10, -15 / 20.0), quieter = Math.Pow(10, -20 / 20.0);
            var cleaned = s.Tone.Select((v, i) => (float)((i >= s.Tone.Length / 2 ? v * fade : v) + s.Noise[i] * quieter)).ToArray();
            var c2 = Measure(cleaned, mask, 700, 200, 3000, knownLag: 0);
            var kept = Survival(c2, r);
            Assert(kept.Kept >= kept.Measured * .9 && Math.Abs(kept.WeakAgainstStrongDb) <= 3 && Math.Abs(c2.Separation - r.Separation - 10) <= 2,
                $"Noise 10 dB down under the faded station: {kept.Kept} of {kept.Measured} kept, weakest third {kept.WeakAgainstStrongDb:0.0} dB, separation {c2.Separation - r.Separation:+0.0;-0.0;0.0} dB");
            // A fade too deep to classify (20 dB): those elements may go unseen, but deleting them
            // must still never read as improvement.
            var (deep, deepErased, _) = FadeScene(20);
            var deepMask = MakeMask(deep, 700);
            var dr = Measure(deep, deepMask, 700, 200, 3000, knownLag: 0);
            var de = Measure(deepErased, deepMask, 700, 200, 3000, knownLag: 0);
            // Tightened in round 2 from +0.5 dB, which let a deletion pass: no figure may improve.
            Assert(de.Separation <= dr.Separation + .05 && de.WeakSeparation <= dr.WeakSeparation + .05,
                $"At a 20 dB fade, deleting the faded half moved separation {dr.Separation:0.0} to {de.Separation:0.0} and the weakest third {dr.WeakSeparation:0.0} to {de.WeakSeparation:0.0} dB");
            Console.WriteLine($"INFO cw-fade: 15 dB fade, {asKey} of {truth} faded key-down blocks seen, {asGap} called gap; deletion: weakest third {r.WeakSeparation:0.0} to {e.WeakSeparation:0.0} dB, " +
                              $"elements {lost.Kept} of {lost.Measured} kept. 20 dB fade: {deepMask.UncertainBlocks} of {deepMask.Key.Length} blocks too faint to classify; " +
                              $"deletion moved the weakest third {dr.WeakSeparation:0.0} to {de.WeakSeparation:0.0} dB. Noise alone spreads {mask.MinContrastDb - CertainMarginDb:0.0} dB.");
        }));
        tests.Add(("cw-weak-station", () =>
        {
            var (mix, erased, weakHz) = WeakStationScene(6);
            // The finder names the second keyed station among its candidates.
            var pitch = FindPitch(mix);
            Assert(Math.Abs(pitch.Hz - 700) <= 6 && pitch.Candidates.Any(c => Math.Abs(c.Hz - weakHz) <= 12),
                $"Pitch finder: {string.Join(", ", pitch.Candidates.Select(c => $"{c.Hz:0} Hz"))}; the stations are at 700 and {weakHz:0} Hz");
            // Through the commands' own mask path, the weaker station named as it would be.
            var masks = CommandMasks(mix, 700, new[] { new CwStationSpec(weakHz, 0, double.PositiveInfinity) });
            var mask = masks.Main;
            var r = Measure(mix, mask, 700, 200, 3000, new[] { weakHz }, 0);
            var e = Measure(erased, mask, 700, 200, 3000, new[] { weakHz }, 0);
            // Round 3: tightened from 0.2 dB. With the other station cut out of the rest of the passband
            // its deletion moves the rest by 0.00 dB; with it left in (cw-rest-includes-stations) the
            // move had shrunk to +0.19 dB on round 3's smaller gap set, inside the old tolerance.
            Assert(Math.Abs(e.RestBelowStation - r.RestBelowStation) <= .05 && e.Separation - r.Separation <= .5,
                $"Deleting the weaker station moved the main station's rest of the passband by {e.RestBelowStation - r.RestBelowStation:+0.00;-0.00;0.00} dB and its separation by {e.Separation - r.Separation:+0.0;-0.0;0.0} dB: deletion must not read as improvement");
            var weakMask = masks.Stations[0].Mask;
            var wr = Measure(mix, weakMask, weakHz, 200, 3000, new[] { 700.0 }, 0);
            var we = Measure(erased, weakMask, weakHz, 200, 3000, new[] { 700.0 }, 0);
            var main = Survival(e, r); var weak = Survival(we, wr, main.StationChangeDb);
            Assert(weak.StationChangeDb - main.StationChangeDb <= -20 && weak.Kept <= weak.Measured * .2,
                $"Deleting the weaker station: it changed {weak.StationChangeDb - main.StationChangeDb:+0.0;-0.0;0.0} dB against the main station, {weak.Kept} of {weak.Measured} elements kept");
            // The positive control: the noise 10 dB down, both stations untouched.
            var mainSynth = MakeSynthCw(20, 700, 0, 57); var weakSynth = MakeSynthCw(20, weakHz, -6, 59);
            var cleaned = mainSynth.Mix(Math.Pow(10, -10 / 20.0)).Select((v, i) => v + weakSynth.Tone[i]).ToArray();
            var cr = Survival(Measure(cleaned, mask, 700, 200, 3000, new[] { weakHz }, 0), r);
            var cw = Survival(Measure(cleaned, weakMask, weakHz, 200, 3000, new[] { 700.0 }, 0), wr, cr.StationChangeDb);
            Assert(Math.Abs(cw.StationChangeDb - cr.StationChangeDb) <= 2 && cw.Kept >= cw.Measured * .9,
                $"Noise 10 dB down, both stations kept: the weaker one measured {cw.StationChangeDb - cr.StationChangeDb:+0.0;-0.0;0.0} dB against the main, {cw.Kept} of {cw.Measured} kept");
            Console.WriteLine($"INFO cw-weak-station: deletion moved the weaker station {weak.StationChangeDb - main.StationChangeDb:+0.0;-0.0;0.0} dB against the main ({weak.Kept} of {weak.Measured} kept); " +
                              $"the main station's rest of the passband moved {e.RestBelowStation - r.RestBelowStation:+0.00;-0.00;0.00} dB.");
        }));
        tests.Add(("cw-neighbour", () =>
        {
            // A strong neighbour 65 Hz away, 15 dB louder, keying only while the main station is
            // silent (the main keys in the first half, the neighbour in the second). Named with its
            // pitch, its leak into the main station's tone band must not become the main's elements.
            var main = MakeSynthCw(20, 700, 0, 71);
            var near = MakeSynthCw(20, 765, 15, 73);
            int half = main.Tone.Length / 2;
            var mix = main.Noise.Select((v, i) => v + (i < half ? main.Tone[i] : 0) + (i >= half ? near.Tone[i] : 0)).ToArray();
            var mask = MakeMask(mix, 700, others: new[] { new CwStationSpec(765, 0, double.PositiveInfinity) });
            int late = mask.Elements.Count(e => (e.Start * CwHop + CwWindow / 2) > half + Rate / 2);
            Assert(late <= mask.Elements.Length * .05, $"With the neighbour named, {late} of the main station's {mask.Elements.Length} elements fell where only the neighbour was keying");
            Console.WriteLine($"INFO cw-neighbour: {late} of {mask.Elements.Length} main-station elements where only the neighbour keyed.");
        }));
        tests.Add(("cw-fast", () =>
        {
            var s = FastCw(12, 61);
            var reference = s.Mix();
            var cleaned = s.Mix(Math.Pow(10, -10 / 20.0));
            var mask = MakeMask(reference, 700);
            int dits = 0;
            for (int i = 1; i < s.Keyed.Length; i++) if (s.Keyed[i] && !s.Keyed[i - 1]) dits++;
            Assert(mask.Elements.Length >= dits * .8, $"At 40 wpm, {mask.Elements.Length} of {dits} dits were measured ({mask.ShortElements} too short)");
            var r = Measure(reference, mask, 700, 200, 3000, knownLag: 0);
            var c = Measure(cleaned, mask, 700, 200, 3000, knownLag: 0);
            Assert(Math.Abs(c.Separation - r.Separation - 10) <= 1.5, $"At 40 wpm, noise lowered 10 dB measured {c.Separation - r.Separation:0.0} dB");
            Console.WriteLine($"INFO cw-fast: {mask.Elements.Length} of {dits} dits measured, {mask.ShortElements} too short; {r.KeyBlocks} key-down and {r.GapBlocks} gap blocks.");
        }));
        tests.Add(("cw-fresh-noise", () =>
        {
            // An engine whose output noise is new noise at the SAME level (independent of the
            // input's) has changed nothing that matters: it must read as no change. This is the
            // control on the gap ceiling, which picks gaps from the reference's quieter blocks.
            var s = MakeSynthCw(20, 700, 0, 53);
            var other = MakeSynthCw(20, 700, 0, 67);
            var reference = s.Mix();
            var fresh = s.Tone.Select((v, i) => v + other.Noise[i]).ToArray();
            var mask = MakeMask(reference, 700);
            var r = Measure(reference, mask, 700, 200, 3000, knownLag: 0);
            var f = Measure(fresh, mask, 700, 200, 3000, knownLag: 0);
            Assert(Math.Abs(f.Separation - r.Separation) <= 1, $"Fresh noise at the same level moved separation by {f.Separation - r.Separation:+0.0;-0.0;0.0} dB");
            Console.WriteLine($"INFO cw-fresh-noise: separation moved {f.Separation - r.Separation:+0.00;-0.00;0.00} dB, the rest of the passband {f.RestBelowStation - r.RestBelowStation:+0.00;-0.00;0.00} dB.");
        }));
        tests.Add(("cw-noise-only", () =>
        {
            var s = MakeSynthCw(20, 700, -200, 65);
            var mask = MakeMask(s.Mix(), 700);
            int certain = mask.Key.Count(k => k != null);
            Assert(certain <= mask.Key.Length * .05, $"Noise alone: {certain} of {mask.Key.Length} blocks were classified as key-down or gap");
        }));
        tests.Add(("cw-pitch-agc", () =>
        {
            // A fast AGC: while the station at 650 Hz is keyed, the whole band (noise included)
            // drops 12 dB, so every bin alternates with the keying. The station must still win, and
            // the band's noise must not score near it.
            var s = MakeSynthCw(20, 650, 6, 69);
            var mix = new float[s.Tone.Length];
            double gain = 1, down = Math.Pow(10, -12 / 20.0), step = 1.0 / (.005 * Rate);
            for (int i = 0; i < mix.Length; i++)
            {
                gain = s.Keyed[i] ? Math.Max(down, gain - step) : Math.Min(1, gain + step / 20);
                mix[i] = (float)((s.Tone[i] + s.Noise[i]) * gain);
            }
            var pitch = FindPitch(mix);
            Assert(Math.Abs(pitch.Hz - 650) <= 6 && pitch.ScoreDb - pitch.RunnerUpScoreDb >= 3,
                $"Under a pumping AGC the pitch finder chose {pitch.Hz:0} Hz ({pitch.ScoreDb:0.0} dB) with {pitch.RunnerUpHz:0} Hz next ({pitch.RunnerUpScoreDb:0.0} dB); the station is at 650 Hz");
        }));
        tests.Add(("cw-pitch-step", () =>
        {
            // The station at 650 Hz; 8 s in, the operator widens the filter and 1750 to 3000 Hz goes
            // from nothing to noise 10 dB above the rest of the band.
            var s = MakeSynthCw(20, 650, -3, 63);
            var mix = s.Mix();
            var band = s.Noise.Select(v => v).ToArray();
            BandPass(band, 1750, 3000);
            double g = Math.Pow(10, 10 / 20.0) * Math.Sqrt(2800.0 / 1250);
            for (int i = 8 * Rate; i < mix.Length; i++) mix[i] += (float)(band[i] * g);
            var pitch = FindPitch(mix);
            Assert(Math.Abs(pitch.Hz - 650) <= 6, $"Pitch finder chose {pitch.Hz:0} Hz; the keyed station is at 650 Hz and a noise band switched on at 8 s");
        }));
    }
}
