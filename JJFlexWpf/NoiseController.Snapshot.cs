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
        /// <summary>
        /// The slice the radio part was actually read from (B1): the radio's
        /// own slice object, as <see cref="INoiseRadio.SliceIdentity"/> gives
        /// it, never the requested VFO number. Repair round 2: a slice jump
        /// changes the VFO number at once but makes the new slice active only
        /// when the queued command runs, so a number would label the OLD
        /// slice's values with the new slice's name. The radio part belongs
        /// to this slice and is never written to another one.
        /// </summary>
        internal object? SliceIdentity { get; set; }
        /// <summary>
        /// The radio switches and levels the layer itself wrote on
        /// <see cref="SliceIdentity"/> since the radio part was read. Escape puts back
        /// only these: a value that merely differs from the picture (another
        /// slice, another program, the radio itself) is not this layer's to
        /// undo.
        /// </summary>
        internal HashSet<string> TouchedSwitches { get; } = new();
        internal HashSet<string> TouchedLevels { get; } = new();
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
            ReadRadioPart(snap);
            _live = snap;
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
        /// The snapshot the open noise layer will put back on Escape, if any:
        /// the controller marks on it every radio write made while it is live.
        /// </summary>
        private NoiseSnapshot? _live;

        /// <summary>
        /// Read the radio part of a snapshot afresh, from the slice the radio
        /// is reading now, with nothing touched yet: at the layer's entry, and
        /// once the radio's active slice really has changed under it (B1:
        /// leaving a slice confirms that slice's work, LayerSliceJump's
        /// documented rule, so the new slice starts clean).
        /// </summary>
        private void ReadRadioPart(NoiseSnapshot snap)
        {
            snap.RadioSwitches.Clear();
            snap.RadioLevels.Clear();
            snap.TouchedSwitches.Clear();
            snap.TouchedLevels.Clear();
            var r = _radio();
            snap.SliceIdentity = r?.SliceIdentity;
            _readingSlice = snap.SliceIdentity;
            if (r is null) return;
            foreach (var e in Engines().Where(e => e.Side == DenoiseSide.Radio))
            {
                snap.RadioSwitches[e.Id] = IsOn(e.Id);
                if (e.Level != null && ReadRadioLevel(e) is int level) snap.RadioLevels[e.Id] = level;
            }
        }

        /// <summary>A radio write the controller made: marked on the live snapshot, so Escape knows the layer made it.</summary>
        private void NoteRadioTouched(string id, bool level)
        {
            if (_live is not { } snap || _radio() is not { } r || !ReferenceEquals(r.SliceIdentity, snap.SliceIdentity)) return;
            (level ? snap.TouchedLevels : snap.TouchedSwitches).Add(id);
        }

        /// <summary>The slice the controller's shadows and the live snapshot belong to.</summary>
        private object? _readingSlice;

        /// <summary>
        /// Notice that the slice the radio is reading has changed (repair
        /// round 2, B1). Called on every report of the active slice, and
        /// BEFORE every radio write, so an edit made before the next poll
        /// still finds a picture of the slice it is about to change. On a
        /// change: the write shadows belong to the slice they were written on
        /// and are forgotten, and the open layer's picture is read again from
        /// the new slice, nothing touched.
        /// </summary>
        private void FollowActiveSlice()
        {
            if (_radio() is not { } r) return;
            object? now = r.SliceIdentity;
            if (ReferenceEquals(now, _readingSlice)) return;
            _readingSlice = now;
            _switchShadow.Clear();
            _levelShadow.Clear();
            if (_live is { } snap && !ReferenceEquals(now, snap.SliceIdentity)) ReadRadioPart(snap);
        }

        /// <summary>The layer closed: nothing is live to put back any more.</summary>
        public void EndSnapshot() => _live = null;

        /// <summary>
        /// Put back what the layer changed. Radio switches and levels only
        /// where the layer itself wrote them on the slice the picture was read
        /// on, and only where they now differ from it: nothing is written to
        /// a slice the operator jumped to without touching, and an unreported
        /// level is never written back unasked. Computer slots, favourites and
        /// per-mode memory are app-wide and put back wherever they differ.
        /// Returns whether anything was put back.
        /// </summary>
        public bool RestoreSnapshot(NoiseSnapshot snap, string source)
        {
            bool changed = false;
            if (ReferenceEquals(_live, snap)) _live = null;
            if (_radio() is { } r && ReferenceEquals(r.SliceIdentity, snap.SliceIdentity))
            {
                foreach (var (id, on) in snap.RadioSwitches)
                {
                    var e = _catalog.Find(id)!;
                    if (!snap.TouchedSwitches.Contains(id) || IsOn(id) == on) continue;
                    if (RadioRefusal(e, r, out _) != null) continue; // a refused write never happened
                    WriteRadioSwitch(e, r, on, source, byApp: false);
                    changed = true;
                }
                foreach (var (id, level) in snap.RadioLevels)
                {
                    var e = _catalog.Find(id)!;
                    if (!snap.TouchedLevels.Contains(id) || ReadRadioLevel(e) == level) continue;
                    if (RadioRefusal(e, r, out _) != null) continue;
                    var lever = Levers(id, false).First();
                    Write(lever, e, new EngineSettingValue(level), source);
                    changed = true;
                }
            }
            // Memory first, so a model the mode's rule swaps while the slots
            // are put back is owed back on leaving CW (B4) and not undone.
            if (_config() is { } c)
            {
                if (!c.NoiseFavourites.Select(f => (f.EngineId, f.Model, f.Name)).SequenceEqual(snap.Favourites.Select(f => (f.EngineId, f.Model, f.Name))))
                {
                    c.NoiseFavourites = snap.Favourites.ToList();
                    changed = true;
                }
                if (snap.MemoryXml.Length > 0 && SerializeMemory(c.NoiseModeMemory) != snap.MemoryXml
                    && DeserializeMemory(snap.MemoryXml) is { } memory)
                {
                    // The group the live settings are in is where the radio is
                    // now, not where it was when the layer opened (B6).
                    memory.LastGroup = c.NoiseModeMemory.LastGroup;
                    c.NoiseModeMemory = memory;
                }
            }
            if (_pipeline() is { } p)
            {
                changed |= RestoreSlot(p.SpectralSlot, snap.Spectral, snap.SpectralSettings);
                changed |= RestoreSlot(p.NeuralSlot, snap.Neural, snap.NeuralSettings);
            }
            ClearListening();
            ForgetZero();
            if (changed) _persist();
            return changed;
        }

        private bool RestoreSlot(PcDenoiseSlot slot, (bool On, string Engine) want, Radios.DenoiseEngineSettings[] settings)
        {
            bool changed = false;
            var now = slot.ExportSettings();
            var back = new List<Radios.DenoiseEngineSettings>();
            if (Describe(now) != Describe(settings))
            {
                // An engine first touched inside the layer has no entry in the
                // snapshot: it goes back to its defaults (strength and no values).
                back.AddRange(settings);
                foreach (var extra in now.Where(n => settings.All(s => s.EngineId != n.EngineId)))
                    back.Add(new Radios.DenoiseEngineSettings { EngineId = extra.EngineId, Direction = extra.Direction, Strength = DefaultStrength(extra.EngineId) });
                changed = true;
            }
            string target = want.Engine.Length > 0 ? want.Engine : slot.ChosenEngineId;
            // The mode's rule (B4), then the settings and the choice; an engine
            // still chosen rebuilds when a model or estimator went back (B3).
            if (slot.RestoreAndSelect(AdmitForMode(slot, back, target), target)) changed = true;
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
