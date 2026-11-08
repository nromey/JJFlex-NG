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

    /// <summary>
    /// One lever an engine offers, as data every surface reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sprint 49 Track E added three members, each optional so every older
    /// construction still compiles and means what it meant:
    /// </para>
    /// <para>
    /// <see cref="Rebuilds"/>: changing the value needs a NEW engine instance
    /// (NR4's noise estimator throws away the learned noise when it changes)
    /// although the delay does not change. The slot treats it exactly like
    /// <see cref="ChangesLatency"/> — a rebuild under the warm-up cross-fade —
    /// and nobody has to mislabel a setting as changing the delay to get one.
    /// </para>
    /// <para>
    /// <see cref="Advanced"/>: the lever is shown only when the operator asks
    /// for an engine's Advanced levers (Home's Advanced checkbox, the noise
    /// layer's Tab after the normal levers). Never a menu row (ruled
    /// 2026-11-07, question 15).
    /// </para>
    /// <para>
    /// <see cref="ShownWhen"/>: the lever only means something while another
    /// setting of the same engine holds one choice (NR2's two trained values
    /// do nothing unless the gain curve is Trained).
    /// </para>
    /// </remarks>
    public sealed record EngineSettingDescriptor(
        string Name, string LabelKey, EngineSettingKind Kind, EngineSettingValue Default,
        double Min = 0, double Max = 1, double Step = 0.01,
        string? UnitKey = null, IReadOnlyList<double>? Stops = null,
        IReadOnlyList<EngineSettingOption>? Options = null, bool ChangesLatency = false,
        bool Rebuilds = false, bool Advanced = false, (string Setting, string Choice)? ShownWhen = null)
    {
        /// <summary>Whether a change to this lever needs a new engine instance.</summary>
        public bool NeedsRebuild => ChangesLatency || Rebuilds;

        public bool Accepts(EngineSettingValue value)
        {
            if (Kind == EngineSettingKind.Choice)
            {
                if (value.Choice == null || Options == null) return false;
                for (int i = 0; i < Options.Count; i++)
                    if (Options[i].Name == value.Choice) return true;
                return false;
            }
            if (value.Choice != null || !double.IsFinite(value.Number) || value.Number < Min || value.Number > Max)
                return false;
            if (Stops != null)
            {
                for (int i = 0; i < Stops.Count; i++)
                    if (Stops[i] == value.Number) return true;
                return false;
            }
            return Step > 0 && Math.Abs((value.Number - Min) / Step - Math.Round((value.Number - Min) / Step)) < 0.0001;
        }

        /// <summary>
        /// The nearest value this lever accepts, for a stored value from an
        /// older build or a hand-edited file: a number snaps to the nearest
        /// stop (or step) inside the range, never silently to the default.
        /// A choice the lever does not offer falls back to the default, since
        /// there is no "nearest" choice. A non-finite number is the default.
        /// </summary>
        public EngineSettingValue Snap(EngineSettingValue value)
        {
            if (Accepts(value)) return value;
            if (Kind == EngineSettingKind.Choice) return Default;
            if (value.Choice != null || !double.IsFinite(value.Number)) return Default;
            double n = Math.Clamp(value.Number, Min, Max);
            if (Stops != null && Stops.Count > 0)
            {
                double best = Stops[0];
                foreach (double stop in Stops)
                    if (Math.Abs(stop - n) < Math.Abs(best - n)) best = stop;
                return new EngineSettingValue(best);
            }
            if (Step <= 0) return Default;
            double snapped = Min + Math.Round((n - Min) / Step) * Step;
            snapped = Math.Round(Math.Clamp(snapped, Min, Max), 6);
            return new EngineSettingValue(snapped);
        }

        /// <summary>
        /// The next accepted value one step up (+1) or down (-1), or null at
        /// the end. Stops step stop to stop; a choice steps through its
        /// options in order.
        /// </summary>
        public EngineSettingValue? StepFrom(EngineSettingValue current, int direction)
        {
            current = Snap(current);
            if (Kind == EngineSettingKind.Choice)
            {
                if (Options == null || Options.Count == 0) return null;
                int at = -1;
                for (int i = 0; i < Options.Count; i++) if (Options[i].Name == current.Choice) at = i;
                int next = at + Math.Sign(direction);
                if (next < 0 || next >= Options.Count) return null;
                return new EngineSettingValue(0, Options[next].Name);
            }
            if (Stops != null && Stops.Count > 0)
            {
                var ordered = Stops.OrderBy(s => s).ToList();
                int at = ordered.IndexOf(current.Number);
                int next = at + Math.Sign(direction);
                if (next < 0 || next >= ordered.Count) return null;
                return new EngineSettingValue(ordered[next]);
            }
            double value = Math.Round(current.Number + Math.Sign(direction) * Step, 6);
            if (value < Min - 1e-9 || value > Max + 1e-9) return null;
            return new EngineSettingValue(Math.Clamp(value, Min, Max));
        }

        /// <summary>The least (-1) or most (+1) value this lever accepts.</summary>
        public EngineSettingValue Edge(int direction)
        {
            if (Kind == EngineSettingKind.Choice)
                return Options == null || Options.Count == 0 ? Default
                    : new EngineSettingValue(0, (direction < 0 ? Options[0] : Options[Options.Count - 1]).Name);
            if (Stops != null && Stops.Count > 0)
                return new EngineSettingValue(direction < 0 ? Stops.Min() : Stops.Max());
            return new EngineSettingValue(direction < 0 ? Min : Max);
        }
    }
}
