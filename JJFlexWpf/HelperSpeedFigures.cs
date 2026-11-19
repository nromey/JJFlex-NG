using System;
using System.Collections.Generic;
using System.Linq;

namespace JJFlexWpf
{
    /// <summary>Where a helper engine's cost figure came from.</summary>
    public enum HelperCostSource
    {
        /// <summary>The helper's measurement of one model slot (Track K's model costs, remembered per computer).</summary>
        ModelCosts,
        /// <summary>The helper's measurement of the engine at its default settings, sent with every handshake.</summary>
        Handshake,
    }

    /// <summary>
    /// One figure the helper measured for one of its engines on this computer:
    /// one channel, so per channel. <paramref name="Model"/> is the model slot's
    /// option for a model-cost figure, null for the handshake's per-engine one.
    /// </summary>
    public sealed record HelperEngineCost(string EngineId, string? Model, int Frames, double MedianMs, double P99Ms, HelperCostSource Source);

    /// <summary>
    /// The helper's own figures for the speed test (Sprint 49 Track L repair 1,
    /// B2): what it measured itself, so there is never a second way of
    /// measuring it and the helper is never started for the test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two sources, model costs first.</b> A helper that measures its model
    /// slots (Track K) sends one figure per NNR model, remembered per computer
    /// in <see cref="HelperModelCostStore"/>. Every helper also sends one figure
    /// per engine with its handshake, at the engine's default settings: that
    /// is the only figure NR2 ever has, and the only one NNR has from a helper
    /// built before model costs.
    /// </para>
    /// <para>
    /// <b>The handshake figures are the running helper's.</b> They are read from
    /// the live connection (<see cref="WdspHost.CurrentConnection"/>), never
    /// remembered across a helper's exit, and never asked for: when no helper is
    /// running at the time of the test, only the remembered model costs count,
    /// and an engine with none is "not measured yet". The helper measures again
    /// every time it starts, so a remembered handshake figure would only be an
    /// older reading of what the next start measures anyway.
    /// </para>
    /// </remarks>
    public sealed class HelperSpeedFigures
    {
        /// <summary>No figures at all.</summary>
        public static readonly HelperSpeedFigures None = new(Array.Empty<HelperEngineCost>());

        /// <summary>Every measured figure (zero-frame ones are dropped).</summary>
        public IReadOnlyList<HelperEngineCost> Costs { get; }

        public HelperSpeedFigures(IEnumerable<HelperEngineCost> costs) =>
            Costs = costs.Where(c => c.Frames > 0).ToList();

        /// <summary>The remembered model costs alone.</summary>
        public static HelperSpeedFigures From(HelperModelCostRecord? record) => From(record, null);

        /// <summary>The remembered model costs and a handshake's per-engine figures for receive.</summary>
        internal static HelperSpeedFigures From(HelperModelCostRecord? record, WdspHelloAck? hello)
        {
            var list = new List<HelperEngineCost>();
            if (record != null)
                foreach (var slot in record.Slots)
                    if (slot.Frames > 0 && Array.IndexOf(WdspHost.KnownEngines, slot.Engine) >= 0)
                        list.Add(new(slot.Engine, string.IsNullOrEmpty(slot.Option) ? null : slot.Option, slot.Frames,
                            slot.MedianNanos / 1e6, slot.P99Nanos / 1e6, HelperCostSource.ModelCosts));
            if (hello != null)
                foreach (var offer in hello.Engines)
                    if (offer.CalibrationFrames > 0 && (offer.Directions & (1 << WdspProtocol.Receive)) != 0
                        && Array.IndexOf(WdspHost.KnownEngines, offer.Id) >= 0)
                        list.Add(new(offer.Id, null, offer.CalibrationFrames, offer.MedianNanos / 1e6, offer.P99Nanos / 1e6,
                            HelperCostSource.Handshake));
            return new(list);
        }

        /// <summary>
        /// What there is now: the remembered model costs and, when the app's
        /// helper is running, its handshake. Starts nothing; may wait briefly on
        /// the host's lock and the store's, so never call it on the UI thread or
        /// the audio thread.
        /// </summary>
        internal static HelperSpeedFigures Current() =>
            From(HelperModelCostStore.Default()?.Find(), WdspHost.DefaultIfCreated?.CurrentConnection?.Hello);

        /// <summary>
        /// The lines for one engine on its own: each measured model when the
        /// helper measured its models, otherwise its handshake figure, otherwise
        /// nothing (not measured yet).
        /// </summary>
        public IReadOnlyList<HelperEngineCost> Lines(string engineId)
        {
            var models = Costs.Where(c => c.EngineId == engineId && c.Source == HelperCostSource.ModelCosts).ToList();
            if (models.Count > 0) return models;
            return Costs.Where(c => c.EngineId == engineId && c.Source == HelperCostSource.Handshake).Take(1).ToList();
        }

        /// <summary>
        /// The figure for the engine at its default settings, as a pairing runs
        /// it: the default model's own measurement, else the handshake's (which
        /// measures the defaults), else null.
        /// </summary>
        public HelperEngineCost? AtDefaults(string engineId, string? defaultModel)
        {
            if (defaultModel != null)
            {
                var model = Costs.FirstOrDefault(c => c.EngineId == engineId && c.Source == HelperCostSource.ModelCosts
                    && string.Equals(c.Model, defaultModel, StringComparison.Ordinal));
                if (model != null) return model;
            }
            return Costs.FirstOrDefault(c => c.EngineId == engineId && c.Source == HelperCostSource.Handshake);
        }
    }
}
