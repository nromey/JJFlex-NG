using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios;
using OffOnValues = Radios.FlexBase.OffOnValues;

namespace JJFlexWpf
{
    public enum DenoiseSide { Radio, Pc }
    public enum DenoiseFamily { Spectral, Neural }

    public sealed record DenoiseEngineEntry(
        string Id, DenoiseSide Side, DenoiseFamily? Family, string LabelKey,
        IReadOnlyList<DenoiseDirection> Directions, Func<IReadOnlyDictionary<string, EngineSettingValue>, IDenoiseEngine>? Factory = null,
        Func<Unavailability>? Probe = null, RadioCapability? Capability = null,
        Func<FlexBase, OffOnValues>? ReadRadio = null, Action<FlexBase, OffOnValues>? WriteRadio = null,
        IReadOnlyList<EngineSettingDescriptor>? SettingDescriptors = null)
    {
        public IReadOnlyList<EngineSettingDescriptor> Settings => SettingDescriptors ?? Array.Empty<EngineSettingDescriptor>();
        public bool Serves(DenoiseDirection direction) => Directions.Contains(direction);
        public Unavailability Availability() => Probe?.Invoke() ?? Unavailability.LibraryMissing;
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

        private static IEnumerable<DenoiseEngineEntry> DefaultEntries()
        {
            yield return new("rnnoise", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.rnnoise", Both,
                _ => new RnnoiseEngine(), () => File.Exists(Path.Combine(AppContext.BaseDirectory, "rnnoise.dll"))
                    ? Unavailability.None : Unavailability.LibraryMissing);
            yield return new("deepfilternet3", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.deepfilternet3", Both,
                initial => new DeepFilterNet3Engine((float)initial["attenuation_limit_db"].Number), ProbeDeepFilter, SettingDescriptors: DeepFilterNet3Engine.SettingDescriptors);
            yield return new("nnr", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.nnr", Both);
            yield return new("nr2", DenoiseSide.Pc, DenoiseFamily.Spectral, "audio.denoise.nr2", Both);
            yield return new("nr4", DenoiseSide.Pc, DenoiseFamily.Spectral, "audio.denoise.nr4", Both,
                initial => new SpecbleachEngine(initial), SpecbleachEngine.Probe, SettingDescriptors: SpecbleachEngine.SettingDescriptors);
            yield return new("spectral_capture", DenoiseSide.Pc, DenoiseFamily.Spectral, "audio.denoise.spectral_capture", Receive,
                _ => new SpectralCaptureEngine(), () => Unavailability.None, SettingDescriptors: SpectralCaptureEngine.SettingDescriptors);
            yield return new("nvidia_afx", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.nvidia_afx", Both);
            yield return new("radio_nr", DenoiseSide.Radio, null, "audio.denoise.radio_nr", Receive,
                ReadRadio: r => r.NoiseReduction, WriteRadio: (r, v) => r.NoiseReduction = v);
            yield return new("radio_nrl", DenoiseSide.Radio, null, "audio.denoise.radio_nrl", Receive,
                ReadRadio: r => r.NoiseReductionLegacy, WriteRadio: (r, v) => r.NoiseReductionLegacy = v);
            yield return new("radio_nrs", DenoiseSide.Radio, null, "audio.denoise.radio_nrs", Receive,
                Capability: RadioCapability.NoiseReductionSpectral,
                ReadRadio: r => r.SpectralNoiseReduction, WriteRadio: (r, v) => r.SpectralNoiseReduction = v);
            yield return new("radio_nrf", DenoiseSide.Radio, null, "audio.denoise.radio_nrf", Receive,
                Capability: RadioCapability.NoiseReductionFilter,
                ReadRadio: r => r.NoiseReductionFilter, WriteRadio: (r, v) => r.NoiseReductionFilter = v);
            yield return new("radio_rnn", DenoiseSide.Radio, null, "audio.denoise.radio_rnn", Receive,
                Capability: RadioCapability.NoiseReductionNeural,
                ReadRadio: r => r.NeuralNoiseReduction, WriteRadio: (r, v) => r.NeuralNoiseReduction = v);
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
