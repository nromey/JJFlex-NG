#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JJTrace;

namespace Radios.Alarms
{
    /// <summary>An operator's own preset: their name for it, and the definition to start from.</summary>
    public sealed class OperatorPreset
    {
        public string Id { get; set; } = "";

        /// <summary>The operator's name. Plain data.</summary>
        public string Name { get; set; } = "";

        public DateTime SavedUtc { get; set; }

        /// <summary>
        /// Everything the editor carries, including the selector — which is
        /// re-resolved on the radio it is applied to, never trusted as-is.
        /// The radio serial and the id are replaced on apply.
        /// </summary>
        public AlarmDefinition Definition { get; set; } = new AlarmDefinition();
    }

    public sealed class OperatorPresetFile
    {
        public const int CurrentSchemaVersion = 1;
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<OperatorPreset> Presets { get; set; } = new List<OperatorPreset>();
    }

    /// <summary>
    /// The operator's own presets, kept on their machine and offered for any
    /// radio (#566, ruled by Noel 2026-09-22: "users should be able to add a
    /// preset").
    /// </summary>
    /// <remarks>
    /// <para>
    /// Machine-wide rather than per radio on purpose: the whole point is to
    /// apply the same definition on another radio, or after a reset, without
    /// rebuilding it field by field. Applying re-discovers the meter through
    /// the selector exactly as a shipped preset does; if the new radio does
    /// not publish that meter the editor says so and asks for a choice,
    /// rather than binding to anything else.
    /// </para>
    /// <para>
    /// Same file discipline as <see cref="AlarmDefinitionStore"/>: under the
    /// settings root, atomic, versioned, unreadable means unavailable.
    /// </para>
    /// </remarks>
    public sealed class OperatorPresetStore
    {
        public const string FileName = "alarm-presets.json";
        public const int MaxPresets = 64;

        private readonly string _path;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public OperatorPresetStore(string root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            _path = Path.Combine(root, FileName);
        }

        public static OperatorPresetStore? Default()
        {
            string root = RadioConfig.AppDataRoot;
            return string.IsNullOrEmpty(root) ? null : new OperatorPresetStore(root);
        }

        public string Path_ => _path;

        /// <summary>Every preset, oldest first, or an empty list with <paramref name="problem"/> set when the file is unreadable.</summary>
        public IReadOnlyList<OperatorPreset> Load(out string problem)
        {
            problem = "";
            if (!File.Exists(_path)) return Array.Empty<OperatorPreset>();
            try
            {
                OperatorPresetFile? file = JsonSerializer.Deserialize<OperatorPresetFile>(File.ReadAllText(_path, Encoding.UTF8), JsonOptions);
                if (file == null) { problem = "the file is empty"; return Array.Empty<OperatorPreset>(); }
                if (file.SchemaVersion > OperatorPresetFile.CurrentSchemaVersion)
                {
                    problem = "written by a newer version";
                    return Array.Empty<OperatorPreset>();
                }
                return file.Presets ?? new List<OperatorPreset>();
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                problem = ex.Message;
                Tracing.TraceLine("OperatorPresetStore: " + _path + " is unavailable — " + ex.Message, TraceLevel.Error);
                return Array.Empty<OperatorPreset>();
            }
        }

        /// <summary>Add or replace by name (case-insensitive), and save. False when refused or the write failed.</summary>
        public bool Save(string name, AlarmDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(name) || definition == null) return false;
            name = name.Trim();
            if (name.Length > AlarmDefinition.MaxNameLength) return false;

            var presets = new List<OperatorPreset>(Load(out string problem));
            if (problem.Length != 0) return false;   // never overwrite a file we could not read

            int existing = presets.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            var preset = new OperatorPreset
            {
                Id = existing >= 0 ? presets[existing].Id : Guid.NewGuid().ToString("N"),
                Name = name,
                SavedUtc = DateTime.UtcNow,
                // The template carries no radio, no enabled intent and no id of its own.
                Definition = definition with { Id = "", RadioSerial = "", Enabled = false, Revision = 1, IsTest = false, PresetKey = "" },
            };
            if (existing >= 0) presets[existing] = preset;
            else
            {
                if (presets.Count >= MaxPresets) return false;
                presets.Add(preset);
            }
            return Write(presets);
        }

        public bool Delete(string id)
        {
            var presets = new List<OperatorPreset>(Load(out string problem));
            if (problem.Length != 0) return false;
            int removed = presets.RemoveAll(p => p.Id == id);
            return removed != 0 && Write(presets);
        }

        private bool Write(List<OperatorPreset> presets)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new OperatorPresetFile { Presets = presets }, JsonOptions), Encoding.UTF8);
                File.Move(temp, _path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("OperatorPresetStore: could not save " + _path + " — " + ex.Message, TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// A definition for THIS radio from a saved preset: new id, this serial,
        /// the selector re-resolved against this connection's census. When the
        /// meter is not published here the definition comes back with its
        /// selector intact and <paramref name="resolution"/> says why, so the
        /// editor can ask for a meter instead of guessing one.
        /// </summary>
        public static AlarmDefinition Apply(OperatorPreset preset, string radioSerial,
            IReadOnlyList<MeterDescriptor> inventory, out MeterSelectorResolution resolution)
        {
            resolution = preset.Definition.Selector.Resolve(inventory);
            MeterSelector selector = resolution.IsResolved
                ? MeterSelector.From(resolution.Match!)
                : preset.Definition.Selector;
            return preset.Definition with
            {
                Id = Guid.NewGuid().ToString("N"),
                RadioSerial = radioSerial,
                Selector = selector,
                Enabled = false,
                Revision = 1,
                PresetKey = "",
            };
        }
    }
}
