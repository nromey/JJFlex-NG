using System;
using System.Collections.Generic;
using System.Linq;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// Everything the noise layer can change, as it stood when the layer
    /// opened, so Escape can put it back (E7): every radio switch, the levels
    /// the layer touched, both computer slots and every engine's settings,
    /// favourites and per-mode memory.
    /// </summary>
    public sealed class NoiseSnapshot
    {
        internal Dictionary<string, bool> RadioSwitches { get; } = new();
        internal Dictionary<string, int> RadioLevels { get; } = new();
        internal (bool On, string Engine) Spectral { get; set; }
        internal (bool On, string Engine) Neural { get; set; }
        internal Radios.DenoiseEngineSettings[] SpectralSettings { get; set; } = Array.Empty<Radios.DenoiseEngineSettings>();
        internal Radios.DenoiseEngineSettings[] NeuralSettings { get; set; } = Array.Empty<Radios.DenoiseEngineSettings>();
        internal List<NoiseFavourite> Favourites { get; set; } = new();
        internal string MemoryXml { get; set; } = "";
    }

    public sealed partial class NoiseController
    {
        /// <summary>Take the picture Escape puts back.</summary>
        public NoiseSnapshot TakeSnapshot()
        {
            var snap = new NoiseSnapshot();
            if (_radio() is { } r)
                foreach (var e in Engines().Where(e => e.Side == DenoiseSide.Radio))
                {
                    snap.RadioSwitches[e.Id] = IsOn(e.Id);
                    if (e.Level != null && ReadRadioLevel(e) is int level) snap.RadioLevels[e.Id] = level;
                }
            if (_pipeline() is { } p)
            {
                snap.Spectral = (p.SpectralSlot.Enabled, p.SpectralSlot.ChosenEngineId);
                snap.Neural = (p.NeuralSlot.Enabled, p.NeuralSlot.ChosenEngineId);
                snap.SpectralSettings = p.SpectralSlot.ExportSettings();
                snap.NeuralSettings = p.NeuralSlot.ExportSettings();
            }
            if (_config() is { } c)
            {
                snap.Favourites = c.NoiseFavourites.Select(f => new NoiseFavourite { EngineId = f.EngineId, Model = f.Model, Name = f.Name }).ToList();
                snap.MemoryXml = SerializeMemory(c.NoiseModeMemory);
            }
            return snap;
        }

        /// <summary>
        /// Put back everything that differs from the snapshot. Radio switches
        /// are written only where they changed; radio levels only where the
        /// layer moved them (an unreported level cannot be compared with the
        /// radio, and writing every level back would be a write nobody asked
        /// for). Returns whether anything was put back.
        /// </summary>
        public bool RestoreSnapshot(NoiseSnapshot snap, string source)
        {
            bool changed = false;
            if (_radio() is { } r)
            {
                foreach (var (id, on) in snap.RadioSwitches)
                {
                    var e = _catalog.Find(id)!;
                    if (IsOn(id) == on) continue;
                    if (RadioRefusal(e, r, out _) != null) continue; // a refused write never happened
                    WriteRadioSwitch(e, r, on, source, byApp: false);
                    changed = true;
                }
                foreach (var (id, level) in snap.RadioLevels)
                {
                    var e = _catalog.Find(id)!;
                    if (ReadRadioLevel(e) == level) continue;
                    if (RadioRefusal(e, r, out _) != null) continue;
                    var lever = Levers(id, false).First();
                    Write(lever, e, new EngineSettingValue(level), source);
                    changed = true;
                }
            }
            if (_pipeline() is { } p)
            {
                changed |= RestoreSlot(p.SpectralSlot, snap.Spectral, snap.SpectralSettings);
                changed |= RestoreSlot(p.NeuralSlot, snap.Neural, snap.NeuralSettings);
            }
            if (_config() is { } c)
            {
                if (!c.NoiseFavourites.Select(f => (f.EngineId, f.Model, f.Name)).SequenceEqual(snap.Favourites.Select(f => (f.EngineId, f.Model, f.Name))))
                {
                    c.NoiseFavourites = snap.Favourites.ToList();
                    changed = true;
                }
                if (snap.MemoryXml.Length > 0 && SerializeMemory(c.NoiseModeMemory) != snap.MemoryXml)
                    c.NoiseModeMemory = DeserializeMemory(snap.MemoryXml) ?? c.NoiseModeMemory;
            }
            ClearListening();
            ForgetZero();
            if (changed) _persist();
            return changed;
        }

        private static bool RestoreSlot(PcDenoiseSlot slot, (bool On, string Engine) want, Radios.DenoiseEngineSettings[] settings)
        {
            bool changed = false;
            var now = slot.ExportSettings();
            if (Describe(now) != Describe(settings))
            {
                // An engine first touched inside the layer has no entry in the
                // snapshot: it goes back to its defaults (strength and no values).
                var back = settings.ToList();
                foreach (var extra in now.Where(n => settings.All(s => s.EngineId != n.EngineId)))
                    back.Add(new Radios.DenoiseEngineSettings { EngineId = extra.EngineId, Direction = extra.Direction, Strength = DefaultStrength(extra.EngineId) });
                slot.RestoreSettings(back);
                changed = true;
            }
            if (want.Engine.Length > 0 && slot.ChosenEngineId != want.Engine) { slot.Select(want.Engine, out _); changed = true; }
            if (slot.Enabled != want.On) { slot.Enabled = want.On; changed = true; }
            return changed;
        }

        // An entry with no values and its engine's default strength is the
        // same as no entry: reading a strength creates one, and reading is not
        // a change Escape should report.
        private static string Describe(Radios.DenoiseEngineSettings[] settings) =>
            string.Join("|", settings.Where(s => s.Values.Count > 0 || Math.Abs(s.Strength - DefaultStrength(s.EngineId)) > 1e-6).OrderBy(s => s.EngineId, StringComparer.Ordinal).Select(s => s.EngineId + ":" + s.Strength.ToString("R") + ":" +
                string.Join(",", s.Values.OrderBy(v => v.Name, StringComparer.Ordinal).Select(v => v.Name + "=" + (v.Choice ?? v.Number.ToString("R"))))));

        private static string SerializeMemory(NoiseModeMemoryConfig memory)
        {
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(NoiseModeMemoryConfig));
            using var writer = new System.IO.StringWriter();
            serializer.Serialize(writer, memory);
            return writer.ToString();
        }

        private static NoiseModeMemoryConfig? DeserializeMemory(string xml)
        {
            try
            {
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(NoiseModeMemoryConfig));
                using var reader = new System.IO.StringReader(xml);
                return serializer.Deserialize(reader) as NoiseModeMemoryConfig;
            }
            catch (InvalidOperationException) { return null; }
        }
    }
}
