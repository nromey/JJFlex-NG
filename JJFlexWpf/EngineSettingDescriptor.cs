using System;
using System.Collections.Generic;
using System.Linq;

namespace JJFlexWpf
{
    public enum Unavailability
    {
        None, LibraryMissing, ModelMissing, InitFailed, HelperNotRunning,
        HelperMissing, HelperVersionMismatch, NoSupportedGpu, GpuTooOld,
        NotDownloaded, LicenceNotAccepted, NotOn32Bit
    }

    public enum EngineSettingKind { Number, Choice }
    public readonly record struct EngineSettingValue(double Number, string? Choice = null);
    public sealed record EngineSettingOption(string Name, string LabelKey);

    public sealed record EngineSettingDescriptor(
        string Name, string LabelKey, EngineSettingKind Kind, EngineSettingValue Default,
        double Min = 0, double Max = 1, double Step = 0.01,
        string? UnitKey = null, IReadOnlyList<double>? Stops = null,
        IReadOnlyList<EngineSettingOption>? Options = null, bool ChangesLatency = false)
    {
        public bool Accepts(EngineSettingValue value)
        {
            if (Kind == EngineSettingKind.Choice)
                return value.Choice != null && Options != null && Options.Any(x => x.Name == value.Choice);
            if (value.Choice != null || !double.IsFinite(value.Number) || value.Number < Min || value.Number > Max)
                return false;
            if (Stops != null) return Stops.Contains(value.Number);
            return Step > 0 && Math.Abs((value.Number - Min) / Step - Math.Round((value.Number - Min) / Step)) < 0.0001;
        }
    }
}
