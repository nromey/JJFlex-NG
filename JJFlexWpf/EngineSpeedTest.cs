using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Radios;

namespace JJFlexWpf
{
    /// <summary>How one engine or pairing fared in the speed test.</summary>
    public enum EngineSpeedVerdict
    {
        /// <summary>Keeps up, with room to spare.</summary>
        KeepsUp,
        /// <summary>Keeps up on average, but its slow packets use half the time available or more: little room.</summary>
        Tight,
        /// <summary>Takes longer to process the audio than the audio lasts.</summary>
        FallsBehind,
        /// <summary>Not available on this computer (missing library, model, helper...).</summary>
        Unavailable,
        /// <summary>Could not be built or failed while it was measured.</summary>
        Failed,
        /// <summary>A helper engine whose cost the helper has not measured yet on this computer, or a pairing with one in it: unknown.</summary>
        NotMeasured,
    }

    /// <summary>Where a speed test line's figures came from.</summary>
    public enum EngineSpeedSource
    {
        /// <summary>Timed here, on this computer, by <see cref="EngineSpeedCore"/>.</summary>
        Timed,
        /// <summary>The helper's own measurement (<see cref="HelperSpeedFigures"/>), in stereo.</summary>
        Helper,
        /// <summary>A pairing with a helper half, composed from this computer's timing of the local half and the helper's own figures.</summary>
        Composed,
    }

    /// <summary>
    /// One line of the speed test: one engine, or a spectral engine followed by
    /// a neural one, as the receive pipeline runs them.
    /// </summary>
    /// <param name="EngineIds">One engine, or a spectral then a neural one.</param>
    /// <param name="Model">For a helper engine, the model the figure is for; null otherwise (and for the handshake's per-engine figure).</param>
    /// <param name="Figures">What it cost, for an engine or pairing timed here; null otherwise.</param>
    /// <param name="MedianMs">Typical cost per 10 ms stereo packet: the mean here, the helper's median for a helper engine; for a composed pairing, the busier half's (two helper halves add, on the helper's one worker).</param>
    /// <param name="P99Ms">The 99th percentile cost per 10 ms stereo packet, the same way.</param>
    /// <param name="Source">Where the figures came from.</param>
    public sealed record EngineSpeedResult(IReadOnlyList<string> EngineIds, string? Model, EngineSpeedVerdict Verdict,
        EngineSpeedFigures? Figures, double MedianMs, double P99Ms, Unavailability Reason = Unavailability.None, string? Detail = null,
        EngineSpeedSource Source = EngineSpeedSource.Timed)
    {
        /// <summary>How much of each packet's time the typical packet uses, in percent.</summary>
        public int PercentUsed => (int)Math.Round(100 * MedianMs / EngineSpeedFigures.PacketMs);
    }

    /// <summary>
    /// The Audio Workshop's speed test for the receive noise engines (Sprint 49
    /// Track L, #866: "Run each engine, and each spectral plus neural pairing
    /// ... on this computer, and report in plain words whether it keeps up and
    /// with how much headroom. Offline, no radio.").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One measuring core.</b> Every engine in this process is timed by
    /// <see cref="EngineSpeedCore"/>, the same code the console kit's
    /// <c>cpu</c> command runs, over the same generated noise.
    /// </para>
    /// <para>
    /// <b>The helper is never started for this</b> (the scoping report's
    /// recommendation, carried by the brief): NNR and NR2 are reported from what
    /// the helper itself measured on this computer (<see cref="HelperSpeedFigures"/>:
    /// per model where it measured its models, else its handshake's per-engine
    /// figure), and are "not measured yet" until it has. So there is never a
    /// second way of measuring the helper. Its figures are per channel; every
    /// line here is stereo, so a helper engine costs two channels' work on the
    /// helper's one worker.
    /// </para>
    /// <para>
    /// <b>Every spectral then neural pairing</b> (#866), including those with
    /// a helper half (repair 1, B2). A local half and a helper half run side by
    /// side, the local one on the receive thread and the helper's in its own
    /// process, so the pairing keeps up when both do and its margin is the
    /// busier half's. Two helper halves share the helper's one worker, so
    /// their work adds. A pairing whose helper half has no figure is "not
    /// measured", never guessed.
    /// </para>
    /// <para>
    /// <b>The verdict</b> is the self-benchmark's margin (design part four,
    /// ruled question 11: under half the 10 ms frame period is "runnable"): an
    /// engine keeps up when its 99th percentile packet takes under half the
    /// packet's time; it is tight when its typical packet keeps up but its slow
    /// ones do not leave that half; it falls behind when its typical packet
    /// takes the whole packet's time or more.
    /// </para>
    /// <para>
    /// Engines run at their default settings, in stereo, as the receive
    /// pipeline builds them. Never on the receive audio thread; it competes with
    /// a radio's receive audio for the processor when one is running.
    /// </para>
    /// </remarks>
    public static class EngineSpeedTest
    {
        /// <summary>Stereo, as the receive pipeline runs.</summary>
        public const int Channels = 2;

        /// <summary>The verdict for a typical and a 99th percentile cost per packet.</summary>
        public static EngineSpeedVerdict Judge(double typicalMs, double p99Ms) =>
            typicalMs >= EngineSpeedFigures.PacketMs ? EngineSpeedVerdict.FallsBehind
            : p99Ms >= EngineSpeedFigures.PacketMs / 2 ? EngineSpeedVerdict.Tight
            : EngineSpeedVerdict.KeepsUp;

        /// <summary>Whether an engine runs in the helper (timed by the helper, never here).</summary>
        public static bool IsHelperEngine(string id) => Array.IndexOf(WdspHost.KnownEngines, id) >= 0;

        /// <summary>
        /// The receive engines this process can time, in the catalog's order:
        /// computer engines with a factory that serve receive and do not run in
        /// the helper.
        /// </summary>
        public static IReadOnlyList<DenoiseEngineEntry> InProcessEngines(DenoiseEngineCatalog catalog, DenoiseFamily family) =>
            catalog.Entries.Where(e => e.Side == DenoiseSide.Pc && e.Family == family && e.Factory != null
                && e.Serves(DenoiseDirection.Receive) && !IsHelperEngine(e.Id)).ToList();

        /// <summary>
        /// Every receive computer engine of one family in the catalog's order,
        /// local and helper alike: what a pairing may be made of.
        /// </summary>
        public static IReadOnlyList<DenoiseEngineEntry> ReceiveEngines(DenoiseEngineCatalog catalog, DenoiseFamily family) =>
            catalog.Entries.Where(e => e.Side == DenoiseSide.Pc && e.Family == family && e.Factory != null
                && e.Serves(DenoiseDirection.Receive)).ToList();

        /// <summary>
        /// Run the test: each local spectral engine alone, each local neural
        /// engine alone, then each local spectral then neural pairing (all
        /// timed); then the helper engines from the helper's own figures; then
        /// every pairing with a helper half, composed. <paramref name="onResult"/>
        /// is called as each line is ready, on this thread.
        /// </summary>
        /// <param name="helper">What the helper measured itself (<see cref="HelperSpeedFigures.Current"/> in the app), or null for nothing.</param>
        public static IReadOnlyList<EngineSpeedResult> Run(DenoiseEngineCatalog catalog, HelperSpeedFigures? helper,
            Action<EngineSpeedResult>? onResult = null, int seconds = EngineSpeedCore.DefaultSeconds, CancellationToken cancel = default)
        {
            helper ??= HelperSpeedFigures.None;
            var results = new List<EngineSpeedResult>();
            void Add(EngineSpeedResult r) { results.Add(r); onResult?.Invoke(r); }
            var mono = EngineSpeedCore.GeneratedNoise(seconds);
            double[][]? profile = null;
            double[][] Profile() => profile ??= GeneratedProfile(Channels);
            var spectral = InProcessEngines(catalog, DenoiseFamily.Spectral);
            var neural = InProcessEngines(catalog, DenoiseFamily.Neural);
            var alone = new Dictionary<string, EngineSpeedResult>(StringComparer.Ordinal);
            foreach (var e in spectral.Concat(neural))
            {
                var r = Time(new[] { e }, mono, Profile, cancel);
                alone[e.Id] = r;
                Add(r);
            }
            foreach (var s in spectral)
                foreach (var n in neural)
                    Add(Time(new[] { s, n }, mono, Profile, cancel));
            foreach (var e in catalog.Entries.Where(e => e.Side == DenoiseSide.Pc && IsHelperEngine(e.Id) && e.Serves(DenoiseDirection.Receive)))
                foreach (var r in HelperLines(e, helper)) Add(r);
            foreach (var s in ReceiveEngines(catalog, DenoiseFamily.Spectral))
                foreach (var n in ReceiveEngines(catalog, DenoiseFamily.Neural))
                    if (IsHelperEngine(s.Id) || IsHelperEngine(n.Id))
                        Add(Compose(s, n, alone, helper));
            return results;
        }

        /// <summary>The helper's figures for one helper engine, in stereo: a line per measured model, else its handshake figure, else one "not measured yet".</summary>
        public static IEnumerable<EngineSpeedResult> HelperLines(DenoiseEngineEntry engine, HelperSpeedFigures? helper)
        {
            var lines = (helper ?? HelperSpeedFigures.None).Lines(engine.Id);
            if (lines.Count == 0)
            {
                yield return new EngineSpeedResult(new[] { engine.Id }, null, EngineSpeedVerdict.NotMeasured, null, 0, 0,
                    Detail: "the helper has not measured it", Source: EngineSpeedSource.Helper);
                yield break;
            }
            foreach (var cost in lines)
            {
                double median = Channels * cost.MedianMs, p99 = Channels * cost.P99Ms;
                yield return new EngineSpeedResult(new[] { engine.Id }, cost.Model, Judge(median, p99), null, median, p99,
                    Detail: Describe(cost), Source: EngineSpeedSource.Helper);
            }
        }

        /// <summary>The model an engine runs at its default settings, from its own setting descriptor, or null when it has no models.</summary>
        internal static string? DefaultModel(DenoiseEngineEntry engine) =>
            engine.Settings.FirstOrDefault(d => d.Name == WdspHelperEngine.ModelSetting)?.Default.Choice;

        private static string Describe(HelperEngineCost c) => FormattableString.Invariant(
            $"{c.EngineId}{(c.Model != null ? " " + c.Model : "")} from the helper's {(c.Source == HelperCostSource.ModelCosts ? "model costs" : "handshake")}: median {c.MedianMs:F3} ms, 99th percentile {c.P99Ms:F3} ms per channel over {c.Frames} frames");

        /// <summary>
        /// A spectral then neural pairing with at least one helper half, from
        /// the figures there are: this computer's timing of a local half (from
        /// <paramref name="alone"/>) and the helper's own figure for a helper
        /// half at its default settings. Never timed and never guessed: a
        /// missing helper figure makes the pairing "not measured", and a local
        /// half that could not be timed gives the pairing its verdict.
        /// </summary>
        public static EngineSpeedResult Compose(DenoiseEngineEntry spectral, DenoiseEngineEntry neural,
            IReadOnlyDictionary<string, EngineSpeedResult> alone, HelperSpeedFigures? helper)
        {
            helper ??= HelperSpeedFigures.None;
            var ids = new[] { spectral.Id, neural.Id };
            double helperMedian = 0, helperP99 = 0;
            var detail = new List<string>();
            EngineSpeedResult? local = null;
            foreach (var stage in new[] { spectral, neural })
            {
                if (IsHelperEngine(stage.Id))
                {
                    var cost = helper.AtDefaults(stage.Id, DefaultModel(stage));
                    if (cost == null)
                        return new EngineSpeedResult(ids, null, EngineSpeedVerdict.NotMeasured, null, 0, 0,
                            Detail: stage.Id, Source: EngineSpeedSource.Composed);
                    // The helper's one worker does every helper channel in turn: two helper halves add.
                    helperMedian += Channels * cost.MedianMs;
                    helperP99 += Channels * cost.P99Ms;
                    detail.Add(Describe(cost));
                    continue;
                }
                if (!alone.TryGetValue(stage.Id, out local))
                    return new EngineSpeedResult(ids, null, EngineSpeedVerdict.Failed, null, 0, 0,
                        Detail: stage.Id + " was not timed", Source: EngineSpeedSource.Composed);
                if (local.Verdict is EngineSpeedVerdict.Unavailable or EngineSpeedVerdict.Failed)
                    return local with { EngineIds = ids, Model = null, Figures = null, Source = EngineSpeedSource.Composed };
                detail.Add(FormattableString.Invariant($"{stage.Id} timed here: mean {local.MedianMs:F3} ms, 99th percentile {local.P99Ms:F3} ms per stereo packet"));
            }
            var helperVerdict = Judge(helperMedian, helperP99);
            bool localBusier = local != null && (Severity(local.Verdict) > Severity(helperVerdict)
                || (Severity(local.Verdict) == Severity(helperVerdict) && local.MedianMs > helperMedian));
            var verdict = localBusier ? local!.Verdict : helperVerdict;
            double median = localBusier ? local!.MedianMs : helperMedian, p99 = localBusier ? local!.P99Ms : helperP99;
            detail.Add("the margin is the " + (localBusier ? "local" : "helper") + " half's");
            return new EngineSpeedResult(ids, null, verdict, null, median, p99, Detail: string.Join("; ", detail),
                Source: EngineSpeedSource.Composed);
        }

        private static int Severity(EngineSpeedVerdict v) => v switch
        {
            EngineSpeedVerdict.KeepsUp => 0,
            EngineSpeedVerdict.Tight => 1,
            _ => 2,
        };

        /// <summary>Time one engine, or a spectral then neural pair, built as the receive slots build them.</summary>
        public static EngineSpeedResult Time(IReadOnlyList<DenoiseEngineEntry> stages, float[] mono, Func<double[][]> profile,
            CancellationToken cancel = default)
        {
            var ids = stages.Select(s => s.Id).ToArray();
            foreach (var stage in stages)
            {
                var reason = stage.Availability();
                if (reason != Unavailability.None)
                    return new EngineSpeedResult(ids, null, EngineSpeedVerdict.Unavailable, null, 0, 0, reason, stage.Id);
            }
            var banks = new List<(PerChannelDenoiserBank Bank, float Strength)>();
            try
            {
                foreach (var stage in stages)
                {
                    var initial = new ReadOnlyDictionary<string, EngineSettingValue>(stage.Settings.ToDictionary(s => s.Name, s => s.Default));
                    var bank = new PerChannelDenoiserBank(() => stage.Factory!(initial, DenoiseDirection.Receive), Channels);
                    banks.Add((bank, NoiseFloor.DefaultStrength(stage.Id, DenoiseDirection.Receive)));
                    if (!bank.IsAvailable)
                        return new EngineSpeedResult(ids, null, EngineSpeedVerdict.Unavailable, null, 0, 0,
                            bank.EngineFor(0).Unavailability, bank.EngineFor(0).UnavailabilityDetail);
                    for (int ch = 0; ch < bank.Channels; ch++)
                        if (bank.EngineFor(ch) is SpectralCaptureEngine capture) capture.SetProfile(profile()[ch]);
                }
                var figures = EngineSpeedCore.Time(mono, Channels, buffer =>
                {
                    foreach (var (bank, strength) in banks) bank.Process(buffer, strength);
                }, cancel);
                foreach (var (bank, _) in banks)
                    if (bank.FallbackFrames != 0 || bank.LateFrames != 0)
                        return new EngineSpeedResult(ids, null, EngineSpeedVerdict.Failed, figures, figures.MeanMs, figures.P99Ms,
                            Detail: $"{bank.EngineFor(0).Id}: {bank.FallbackFrames} frames failed, {bank.LateFrames} late");
                return new EngineSpeedResult(ids, null, Judge(figures.MeanMs, figures.P99Ms), figures, figures.MeanMs, figures.P99Ms);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Trace.WriteLine($"EngineSpeedTest: {string.Join(" then ", ids)} failed: {ex.GetType().Name}: {ex.Message}");
                return new EngineSpeedResult(ids, null, EngineSpeedVerdict.Failed, null, 0, 0, Detail: ex.Message);
            }
            finally
            {
                foreach (var (bank, _) in banks) bank.Dispose();
            }
        }

        /// <summary>
        /// A noise profile per channel from three seconds of the kit's generated
        /// noise (seed 49), captured as the live capture does, so Captured
        /// noise has something to subtract.
        /// </summary>
        public static double[][] GeneratedProfile(int channels)
        {
            var mono = EngineSpeedCore.GeneratedNoise(3);
            var capture = new SpectralSubtractionProvider(EngineSpeedCore.SampleRate, channels);
            capture.StartSampling(3);
            var packet = new float[EngineSpeedCore.FrameLength * channels];
            for (int pos = 0; pos + EngineSpeedCore.FrameLength <= mono.Length; pos += EngineSpeedCore.FrameLength)
            {
                for (int i = 0; i < EngineSpeedCore.FrameLength; i++)
                    for (int c = 0; c < channels; c++) packet[i * channels + c] = mono[pos + i];
                capture.ProcessInPlace(packet, 0, packet.Length, channels);
            }
            var profiles = new double[channels][];
            for (int c = 0; c < channels; c++)
                profiles[c] = capture.GetChannelProfile(c) ?? throw new InvalidOperationException("The generated noise did not give a profile for channel " + c);
            return profiles;
        }

        /// <summary>A model's label from the engine's own setting descriptor (NNR's mirror); the option's name when nothing names it.</summary>
        private static string ModelLabel(string engineId, string model)
        {
            var models = DenoiseEngineCatalog.HelperMirror(engineId)?.FirstOrDefault(d => d.Name == "model");
            var option = models?.Options?.FirstOrDefault(o => o.Name == model);
            return option != null ? Lexicon.Get(option.LabelKey) : model;
        }

        /// <summary>
        /// The words for one line (lexicon placeholders until the words round).
        /// <paramref name="name"/> turns an engine id into its label.
        /// </summary>
        public static string Words(EngineSpeedResult r, Func<string, string> name)
        {
            string engine = r.EngineIds.Count >= 2
                ? Lexicon.Get("audio.diagnostics.speed.pair", ("first", name(r.EngineIds[0])), ("second", name(r.EngineIds[1])))
                : name(r.EngineIds[0]);
            if (r.Model != null)
                engine = Lexicon.Get("audio.diagnostics.speed.with_model", ("engine", engine),
                    ("model", ModelLabel(r.EngineIds[0], r.Model)));
            string ms(double v) => v.ToString(v < 1 ? "0.00" : "0.0", System.Globalization.CultureInfo.InvariantCulture);
            var args = new (string, object?)[] { ("engine", engine), ("percent", r.PercentUsed), ("p99_ms", ms(r.P99Ms)) };
            return (r.Verdict, r.Source) switch
            {
                (EngineSpeedVerdict.KeepsUp, EngineSpeedSource.Helper) => Lexicon.Get("audio.diagnostics.speed.helper.keeps_up", args),
                (EngineSpeedVerdict.KeepsUp, EngineSpeedSource.Composed) => Lexicon.Get("audio.diagnostics.speed.composed.keeps_up", args),
                (EngineSpeedVerdict.KeepsUp, _) => Lexicon.Get("audio.diagnostics.speed.keeps_up", args),
                (EngineSpeedVerdict.Tight, EngineSpeedSource.Helper) => Lexicon.Get("audio.diagnostics.speed.helper.tight", args),
                (EngineSpeedVerdict.Tight, EngineSpeedSource.Composed) => Lexicon.Get("audio.diagnostics.speed.composed.tight", args),
                (EngineSpeedVerdict.Tight, _) => Lexicon.Get("audio.diagnostics.speed.tight", args),
                (EngineSpeedVerdict.FallsBehind, EngineSpeedSource.Helper) => Lexicon.Get("audio.diagnostics.speed.helper.falls_behind", args),
                (EngineSpeedVerdict.FallsBehind, EngineSpeedSource.Composed) => Lexicon.Get("audio.diagnostics.speed.composed.falls_behind", args),
                (EngineSpeedVerdict.FallsBehind, _) => Lexicon.Get("audio.diagnostics.speed.falls_behind", args),
                (EngineSpeedVerdict.Unavailable, _) => Lexicon.Get("audio.diagnostics.speed.unavailable", ("engine", engine),
                    ("reason", Lexicon.Get(DenoiseEngineCatalog.UnavailabilityKey(r.Reason)))),
                (EngineSpeedVerdict.NotMeasured, EngineSpeedSource.Composed) => Lexicon.Get("audio.diagnostics.speed.pair_not_measured",
                    ("engine", engine), ("missing", name(r.Detail ?? ""))),
                (EngineSpeedVerdict.NotMeasured, _) => Lexicon.Get("audio.diagnostics.speed.helper.not_measured", ("engine", engine)),
                _ => Lexicon.Get("audio.diagnostics.speed.failed", ("engine", engine)),
            };
        }
    }
}
