using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using Radios;
using OffOnValues = Radios.FlexBase.OffOnValues;

namespace JJFlexWpf
{
    public enum DenoiseSide { Radio, Pc }
    public enum DenoiseFamily { Spectral, Neural }

    /// <summary>
    /// Which of the two noise subjects an engine belongs to: the reducers, or
    /// the blankers and notches (ruled by Noel 2026-11-07, question 2: "I'd add
    /// a noise blankers and notches menu"). Every surface groups by this, so the
    /// menu, the layer's H list and favourites cannot disagree about where an
    /// engine lives.
    /// </summary>
    public enum DenoiseGroup { NoiseReduction, BlankersAndNotches }

    /// <summary>
    /// A radio engine's level: its range, how it steps, whether the radio
    /// reports it back, and the two doors to it. Every radio level a surface
    /// shows is read from here, so the menu, Home and the noise layer cannot
    /// give one level three ranges.
    /// </summary>
    /// <param name="ReportedBack">
    /// False for the four levels FlexLib's status parser never reads
    /// (Legacy NR, Spectral NR, NR Filter, Legacy Auto-Notch): the value a
    /// surface shows for those is JJ Flexible's held intent, never a reading.
    /// </param>
    public sealed record RadioLevelSpec(int Min, int Max, bool ReportedBack,
        Func<FlexBase, int> Read, Action<FlexBase, int> Write)
    {
        /// <summary>
        /// One step from <paramref name="value"/>: 1 at a time up to 15 and 5
        /// at a time above it (the design's choice, 2026-11-07, so the fine end
        /// where Legacy NR's old range lived keeps its fine steps). Clamped to
        /// the range; returns the value unchanged at an end.
        /// </summary>
        public int Step(int value, int direction)
        {
            value = Math.Clamp(value, Min, Max);
            int next;
            if (direction > 0) next = value < FineStepCeiling ? value + 1 : value + CoarseStep - ((value - FineStepCeiling) % CoarseStep);
            else next = value <= FineStepCeiling ? value - 1 : value - (((value - FineStepCeiling) % CoarseStep) == 0 ? CoarseStep : (value - FineStepCeiling) % CoarseStep);
            return Math.Clamp(next, Min, Max);
        }

        /// <summary>Levels at or below this step by one.</summary>
        public const int FineStepCeiling = 15;

        /// <summary>The step above <see cref="FineStepCeiling"/>.</summary>
        public const int CoarseStep = 5;
    }

    public sealed record DenoiseEngineEntry(
        string Id, DenoiseSide Side, DenoiseFamily? Family, string LabelKey,
        IReadOnlyList<DenoiseDirection> Directions, Func<IReadOnlyDictionary<string, EngineSettingValue>, IDenoiseEngine>? Factory = null,
        Func<Unavailability>? Probe = null, RadioCapability? Capability = null,
        Func<FlexBase, OffOnValues>? ReadRadio = null, Action<FlexBase, OffOnValues>? WriteRadio = null,
        IReadOnlyList<EngineSettingDescriptor>? SettingDescriptors = null,
        DenoiseGroup Group = DenoiseGroup.NoiseReduction, RadioLevelSpec? Level = null, bool CwOnly = false)
    {
        public IReadOnlyList<EngineSettingDescriptor> Settings => SettingDescriptors ?? Array.Empty<EngineSettingDescriptor>();
        public bool Serves(DenoiseDirection direction) => Directions.Contains(direction);
        public Unavailability Availability() => Probe?.Invoke() ?? Unavailability.LibraryMissing;

        /// <summary>A radio engine that is only a switch (Neural NR, FFT Auto-Notch): no level, so no default to return to.</summary>
        public bool IsSwitchOnly => Side == DenoiseSide.Radio && Level == null;
    }

    public sealed class DenoiseEngineCatalog
    {
        private static readonly DenoiseDirection[] Receive = { DenoiseDirection.Receive };
        private static readonly DenoiseDirection[] Both = { DenoiseDirection.Receive, DenoiseDirection.Transmit };
        public IReadOnlyList<DenoiseEngineEntry> Entries { get; }
        public DenoiseEngineCatalog(IEnumerable<DenoiseEngineEntry>? entries = null)
        {
            Entries = (entries ?? DefaultEntries()).ToArray();
            if (Entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != Entries.Count)
                throw new ArgumentException("Duplicate engine id", nameof(entries));
        }
        public DenoiseEngineEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id);
        public static string UnavailabilityKey(Unavailability reason) => "audio.denoise.unavailable." + reason.ToString().ToLowerInvariant();

        /// <summary>
        /// Every engine in the order every surface lists them: by subject
        /// (reducers first), then the radio's before the computer's, each in
        /// catalog order. One ordering, so the menu, Home, the layer's
        /// brackets and Settings' favourites list read the same sequence.
        /// </summary>
        public IEnumerable<DenoiseEngineEntry> InSurfaceOrder() =>
            Entries.OrderBy(e => e.Group).ThenBy(e => e.Side == DenoiseSide.Radio ? 0 : 1)
                   .ThenBy(e => e.Side == DenoiseSide.Pc ? (e.Family == DenoiseFamily.Spectral ? 0 : 1) : 0);

        /// <summary>
        /// Legacy NR's level range, held in ONE constant (ruled 2026-11-07,
        /// question 18: "lean toward using it", 1 to 100, test first). The
        /// two doors limited it to 1 to 15 until this build. If the 8600
        /// reading (open fact 3) shows the radio ignores the top of the range,
        /// narrowing it back is this line.
        /// </summary>
        public const int LegacyNrLevelMax = 100;

        /// <summary>Legacy NR's lowest level, as the doors always had it.</summary>
        public const int LegacyNrLevelMin = 1;

        /// <summary>DeepFilterNet3's limit stops, in decibels: fine at the gentle end (Noel, 2026-10-17). 100 is shown as "unlimited".</summary>
        public static readonly IReadOnlyList<double> DeepFilterLimitStops = new double[] { 0, 3, 6, 9, 12, 15, 18, 24, 30, 40, 60, 100 };

        /// <summary>
        /// The NNR descriptors the helper offers, mirrored here so the slot can
        /// hold every lever as an intent before the helper has ever started
        /// (Sprint 49 Track E, E1; Track A's report, the descriptor list). The
        /// live handshake stays the truth for a running session: a helper that
        /// offers something different is traced by <see cref="WdspHelperEngine"/>
        /// and its own descriptors govern that engine.
        /// </summary>
        public static readonly IReadOnlyList<EngineSettingDescriptor> NnrMirror = new[]
        {
            new EngineSettingDescriptor("model", "audio.denoise.nnr.model", EngineSettingKind.Choice,
                new EngineSettingValue(0, "standard"), ChangesLatency: true, Options: new[]
                {
                    new EngineSettingOption("standard", "audio.denoise.nnr.model.standard"),
                    new EngineSettingOption("premium", "audio.denoise.nnr.model.premium"),
                    new EngineSettingOption("standard_cw", "audio.denoise.nnr.model.standard_cw"),
                    new EngineSettingOption("premium_cw", "audio.denoise.nnr.model.premium_cw"),
                }),
            new EngineSettingDescriptor("mask_floor_db", "audio.denoise.nnr.mask_floor_db", EngineSettingKind.Number,
                new EngineSettingValue(-25), -50, -10, 1, "audio.unit.decibels"),
        };

        /// <summary>The NR2 descriptors the helper offers, mirrored for the same reason as <see cref="NnrMirror"/>. All five are Advanced (ruled, question 15); NR2's normal lever is Strength.</summary>
        public static readonly IReadOnlyList<EngineSettingDescriptor> Nr2Mirror = new[]
        {
            new EngineSettingDescriptor("gain_method", "audio.denoise.nr2.gain_method", EngineSettingKind.Choice,
                new EngineSettingValue(0, "gamma"), Advanced: true, Options: new[]
                {
                    new EngineSettingOption("linear", "audio.denoise.nr2.gain_method.linear"),
                    new EngineSettingOption("log", "audio.denoise.nr2.gain_method.log"),
                    new EngineSettingOption("gamma", "audio.denoise.nr2.gain_method.gamma"),
                    new EngineSettingOption("trained", "audio.denoise.nr2.gain_method.trained"),
                }),
            new EngineSettingDescriptor("noise_estimator", "audio.denoise.nr2.noise_estimator", EngineSettingKind.Choice,
                new EngineSettingValue(0, "osms"), Advanced: true, Options: new[]
                {
                    new EngineSettingOption("osms", "audio.denoise.nr2.noise_estimator.osms"),
                    new EngineSettingOption("mmse", "audio.denoise.nr2.noise_estimator.mmse"),
                    new EngineSettingOption("nstat", "audio.denoise.nr2.noise_estimator.nstat"),
                }),
            new EngineSettingDescriptor("artefact_elimination", "audio.denoise.nr2.artefact_elimination", EngineSettingKind.Choice,
                new EngineSettingValue(0, "on"), Advanced: true, Options: new[]
                {
                    new EngineSettingOption("off", "audio.denoise.nr2.artefact_elimination.off"),
                    new EngineSettingOption("on", "audio.denoise.nr2.artefact_elimination.on"),
                }),
            new EngineSettingDescriptor("trained_t1", "audio.denoise.nr2.trained_t1", EngineSettingKind.Number,
                new EngineSettingValue(-2), -5, 5, 0.1, Advanced: true, ShownWhen: ("gain_method", "trained")),
            new EngineSettingDescriptor("trained_t2", "audio.denoise.nr2.trained_t2", EngineSettingKind.Number,
                new EngineSettingValue(0.2), 0.02, 0.3, 0.01, Advanced: true, ShownWhen: ("gain_method", "trained")),
        };

        /// <summary>
        /// About how long a helper engine delays the audio, in milliseconds, for
        /// the "Say delays" clause spoken when one is chosen, before its channel
        /// has opened and reported its own figure. From Track A's measured
        /// latencies (2026-11-05: NNR 2,454 samples, Premium CW 2,070, NR2
        /// 4,064, all at 48 kHz) plus the pipeline's frames in flight
        /// (<see cref="WdspPipelineDepth"/>), which the engine adds. Null for an
        /// engine that is not a helper engine.
        /// </summary>
        public static int? ExpectedHelperDelayMs(string engineId, string? model)
        {
            int samples = engineId switch
            {
                "nnr" => model == "premium_cw" ? 2070 : 2454,
                "nr2" => 4064,
                _ => -1,
            };
            if (samples < 0) return null;
            samples += WdspPipelineDepth.For(engineId, model) * 480;
            return (int)Math.Round(samples / 48.0);
        }

        /// <summary>The mirror for a helper engine id, or null.</summary>
        public static IReadOnlyList<EngineSettingDescriptor>? HelperMirror(string engineId) => engineId switch
        {
            "nnr" => NnrMirror,
            "nr2" => Nr2Mirror,
            _ => null,
        };

        /// <summary>
        /// Where a helper's live offer differs from <see cref="HelperMirror"/>:
        /// a short description for the trace, or null when they agree (or the
        /// engine has no mirror). Compares names, kinds, ranges, steps, defaults,
        /// options and whether each changes latency.
        /// </summary>
        public static string? MirrorDisagreement(string engineId, IReadOnlyList<EngineSettingDescriptor> offered)
        {
            var mirror = HelperMirror(engineId);
            if (mirror == null) return null;
            var problems = new List<string>();
            foreach (var m in mirror)
            {
                var o = offered.FirstOrDefault(x => x.Name == m.Name);
                if (o == null) { problems.Add(m.Name + " not offered"); continue; }
                if (o.Kind != m.Kind || o.Min != m.Min || o.Max != m.Max || o.Step != m.Step || o.ChangesLatency != m.ChangesLatency
                    || o.Default.Number != m.Default.Number || o.Default.Choice != m.Default.Choice
                    || !(o.Options ?? Array.Empty<EngineSettingOption>()).Select(x => x.Name)
                        .SequenceEqual((m.Options ?? Array.Empty<EngineSettingOption>()).Select(x => x.Name)))
                    problems.Add(m.Name + " differs");
            }
            foreach (var o in offered)
                if (mirror.All(m => m.Name != o.Name)) problems.Add(o.Name + " is new");
            return problems.Count == 0 ? null : string.Join(", ", problems);
        }

        private static IEnumerable<DenoiseEngineEntry> DefaultEntries()
        {
            // ── Noise Reduction: the radio's reducers ──
            yield return new("radio_nr", DenoiseSide.Radio, null, "audio.denoise.radio_nr", Receive,
                ReadRadio: r => r.NoiseReduction, WriteRadio: (r, v) => r.NoiseReduction = v,
                Level: new RadioLevelSpec(FlexBase.NoiseReductionValueMin, FlexBase.NoiseReductionValueMax, true,
                    r => r.NoiseReductionLevel, (r, v) => r.NoiseReductionLevel = v));
            yield return new("radio_nrl", DenoiseSide.Radio, null, "audio.denoise.radio_nrl", Receive,
                ReadRadio: r => r.NoiseReductionLegacy, WriteRadio: (r, v) => r.NoiseReductionLegacy = v,
                Level: new RadioLevelSpec(LegacyNrLevelMin, LegacyNrLevelMax, false,
                    r => r.NoiseReductionLegacyLevel, (r, v) => r.NoiseReductionLegacyLevel = v));
            yield return new("radio_rnn", DenoiseSide.Radio, null, "audio.denoise.radio_rnn", Receive,
                Capability: RadioCapability.NoiseReductionNeural,
                ReadRadio: r => r.NeuralNoiseReduction, WriteRadio: (r, v) => r.NeuralNoiseReduction = v);
            yield return new("radio_nrs", DenoiseSide.Radio, null, "audio.denoise.radio_nrs", Receive,
                Capability: RadioCapability.NoiseReductionSpectral,
                ReadRadio: r => r.SpectralNoiseReduction, WriteRadio: (r, v) => r.SpectralNoiseReduction = v,
                Level: new RadioLevelSpec(FlexBase.SpectralNoiseReductionValueMin, FlexBase.SpectralNoiseReductionValueMax, false,
                    r => r.SpectralNoiseReductionLevel, (r, v) => r.SpectralNoiseReductionLevel = v));
            yield return new("radio_nrf", DenoiseSide.Radio, null, "audio.denoise.radio_nrf", Receive,
                Capability: RadioCapability.NoiseReductionFilter,
                ReadRadio: r => r.NoiseReductionFilter, WriteRadio: (r, v) => r.NoiseReductionFilter = v,
                Level: new RadioLevelSpec(FlexBase.NoiseReductionFilterValueMin, FlexBase.NoiseReductionFilterValueMax, false,
                    r => r.NoiseReductionFilterLevel, (r, v) => r.NoiseReductionFilterLevel = v));

            // ── Noise Reduction: the computer's, spectral first, then neural ──
            yield return new("nr2", DenoiseSide.Pc, DenoiseFamily.Spectral, "audio.denoise.nr2", Both,
                initial => new WdspHelperEngine("nr2", DenoiseDirection.Receive, initial), () => WdspHelperEngine.Probe("nr2"),
                SettingDescriptors: Nr2Mirror);
            yield return new("nr4", DenoiseSide.Pc, DenoiseFamily.Spectral, "audio.denoise.nr4", Both,
                initial => new SpecbleachEngine(initial, estimator: SpecbleachEngine.EstimatorFrom(initial)),
                SpecbleachEngine.Probe, SettingDescriptors: SpecbleachEngine.SettingDescriptors);
            yield return new("spectral_capture", DenoiseSide.Pc, DenoiseFamily.Spectral, "audio.denoise.spectral_capture", Receive,
                _ => new SpectralCaptureEngine(), () => Unavailability.None, SettingDescriptors: SpectralCaptureEngine.SettingDescriptors);
            yield return new("rnnoise", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.rnnoise", Both,
                _ => new RnnoiseEngine(), ProbeRnnoise);
            yield return new("deepfilternet3", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.deepfilternet3", Both,
                initial => new DeepFilterNet3Engine((float)initial["attenuation_limit_db"].Number), ProbeDeepFilter, SettingDescriptors: DeepFilterNet3Engine.SettingDescriptors);
            yield return new("nnr", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.nnr", Both,
                initial => new WdspHelperEngine("nnr", DenoiseDirection.Receive, initial), () => WdspHelperEngine.Probe("nnr"),
                SettingDescriptors: NnrMirror);
            // Track G (#768) builds NVIDIA NR. Until then the row is listed,
            // marked and self-explaining, and has no factory.
            yield return new("nvidia_afx", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.nvidia_afx", Both,
                Probe: () => Unavailability.NotDownloaded);

            // ── Noise Blankers and Notches: the radio's ──
            yield return new("radio_nb", DenoiseSide.Radio, null, "audio.denoise.radio_nb", Receive,
                ReadRadio: r => r.NoiseBlanker, WriteRadio: (r, v) => r.NoiseBlanker = v, Group: DenoiseGroup.BlankersAndNotches,
                Level: new RadioLevelSpec(FlexBase.NoiseBlankerValueMin, FlexBase.NoiseBlankerValueMax, true,
                    r => r.NoiseBlankerLevel, (r, v) => r.NoiseBlankerLevel = v));
            yield return new("radio_wnb", DenoiseSide.Radio, null, "audio.denoise.radio_wnb", Receive,
                ReadRadio: r => r.WidebandNoiseBlanker, WriteRadio: (r, v) => r.WidebandNoiseBlanker = v, Group: DenoiseGroup.BlankersAndNotches,
                Level: new RadioLevelSpec(FlexBase.NoiseBlankerValueMin, FlexBase.NoiseBlankerValueMax, true,
                    r => r.WidebandNoiseBlankerLevel, (r, v) => r.WidebandNoiseBlankerLevel = v));
            yield return new("radio_anf", DenoiseSide.Radio, null, "audio.denoise.radio_anf", Receive,
                ReadRadio: r => r.ANF, WriteRadio: (r, v) => r.ANF = v, Group: DenoiseGroup.BlankersAndNotches,
                Level: new RadioLevelSpec(FlexBase.AutoNotchLevelMin, FlexBase.AutoNotchLevelMax, true,
                    r => r.AutoNotchLevel, (r, v) => r.AutoNotchLevel = v));
            yield return new("radio_anfl", DenoiseSide.Radio, null, "audio.denoise.radio_anfl", Receive,
                ReadRadio: r => r.AutoNotchLegacy, WriteRadio: (r, v) => r.AutoNotchLegacy = v, Group: DenoiseGroup.BlankersAndNotches,
                Level: new RadioLevelSpec(FlexBase.AutoNotchLegacyLevelMin, FlexBase.AutoNotchLegacyLevelMax, false,
                    r => r.AutoNotchLegacyLevel, (r, v) => r.AutoNotchLegacyLevel = v));
            yield return new("radio_anft", DenoiseSide.Radio, null, "audio.denoise.radio_anft", Receive,
                Capability: RadioCapability.AutoNotchFft,
                ReadRadio: r => r.AutoNotchFFT, WriteRadio: (r, v) => r.AutoNotchFFT = v, Group: DenoiseGroup.BlankersAndNotches);
            yield return new("radio_apf", DenoiseSide.Radio, null, "audio.denoise.radio_apf", Receive,
                ReadRadio: r => r.APF, WriteRadio: (r, v) => r.APF = v, Group: DenoiseGroup.BlankersAndNotches, CwOnly: true,
                Level: new RadioLevelSpec(FlexBase.AutoPeakLevelMin, FlexBase.AutoPeakLevelMax, true,
                    r => r.AutoPeakLevel, (r, v) => r.AutoPeakLevel = v));
        }

        private static Unavailability ProbeRnnoise()
        {
            try
            {
                string library = RnnoiseEngine.LocateLibrary();
                if (!File.Exists(library)) return Unavailability.LibraryMissing;
                using var stream = File.OpenRead(library);
                using var pe = new PEReader(stream);
                var expected = Environment.Is64BitProcess ? Machine.Amd64 : Machine.I386;
                return pe.PEHeaders.CoffHeader.Machine == expected ? Unavailability.None : Unavailability.InitFailed;
            }
            catch { return Unavailability.InitFailed; }
        }

        private static Unavailability ProbeDeepFilter()
        {
            string? library;
            try { library = DeepFilterNet3Engine.LibraryLocator?.Invoke(); }
            catch { return Unavailability.LibraryMissing; }
            library ??= DeepFilterNet3Engine.DefaultLibraryPath();
            if (!File.Exists(library)) return Unavailability.LibraryMissing;
            return File.Exists(Path.Combine(Path.GetDirectoryName(library) ?? "", DeepFilterNet3Engine.ModelFileName))
                ? Unavailability.None : Unavailability.ModelMissing;
        }
    }
}
