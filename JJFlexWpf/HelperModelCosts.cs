using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Serialization;
using Radios;

namespace JJFlexWpf
{
    /// <summary>One model slot's cost as the helper measured it on this machine.</summary>
    public sealed class HelperModelCostEntry
    {
        [XmlAttribute] public string Engine { get; set; } = "";
        [XmlAttribute] public int Slot { get; set; }
        [XmlAttribute] public string Option { get; set; } = "";
        /// <summary>Frames measured; 0 when the slot was not measured.</summary>
        [XmlAttribute] public int Frames { get; set; }
        [XmlAttribute] public long MedianNanos { get; set; }
        [XmlAttribute] public long P99Nanos { get; set; }
    }

    /// <summary>The helper's last measurement of its model slots, for one architecture.</summary>
    public sealed class HelperModelCostRecord
    {
        /// <summary>"x64" or "x86": the helper that measured, which is the app's own architecture.</summary>
        [XmlAttribute] public string Architecture { get; set; } = "";
        [XmlAttribute] public string HelperVersion { get; set; } = "";
        [XmlAttribute] public DateTime MeasuredUtc { get; set; }
        [XmlElement("Slot")] public List<HelperModelCostEntry> Slots { get; set; } = new();

        /// <summary>The slot an engine's option selects, when it was measured; otherwise null.</summary>
        public HelperModelCostEntry? Measured(string engine, string option) =>
            Slots.LastOrDefault(s => s.Frames > 0 && string.Equals(s.Engine, engine, StringComparison.Ordinal)
                && string.Equals(s.Option, option, StringComparison.Ordinal));
    }

    [XmlRoot("HelperModelCosts")]
    public sealed class HelperModelCostFile
    {
        [XmlElement("Measurement")] public List<HelperModelCostRecord> Measurements { get; set; } = new();
    }

    /// <summary>
    /// What each of the helper's model slots costs on this machine, remembered
    /// between runs (Sprint 49 Track K, #1002; ruled 2026-11-07, question 11:
    /// "remembered per machine and re-measured when the helper restarts"). One
    /// record per architecture, because an x86 helper and an x64 helper are
    /// different programs with different costs; each new measurement replaces
    /// the last for its architecture. A file that cannot be read reads as "not
    /// measured", and the next measurement overwrites it. The file lives under
    /// <see cref="RadioConfig.AppDataRoot"/>, so a run with a throwaway settings
    /// folder never touches the operator's.
    /// </summary>
    public sealed class HelperModelCostStore
    {
        public const string FileName = "helperModelCosts.xml";
        private static readonly object Gate = new();
        private static readonly XmlSerializer Serializer = new(typeof(HelperModelCostFile));
        private readonly string _path;
        private HelperModelCostFile? _cached;

        public HelperModelCostStore(string root) => _path = Path.Combine(root ?? throw new ArgumentNullException(nameof(root)), FileName);

        /// <summary>The store under the settings root, or null when there is none.</summary>
        public static HelperModelCostStore? Default()
        {
            string root = RadioConfig.AppDataRoot;
            return string.IsNullOrEmpty(root) ? null : new HelperModelCostStore(root);
        }

        /// <summary>This process's architecture as a record names it.</summary>
        public static string ThisArchitecture => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x64",
            var other => other.ToString().ToLowerInvariant(),
        };

        public string FilePath => _path;

        /// <summary>The last measurement for an architecture (this process's by default), or null.</summary>
        public HelperModelCostRecord? Find(string? architecture = null)
        {
            architecture ??= ThisArchitecture;
            lock (Gate) return Read().Measurements.LastOrDefault(m => string.Equals(m.Architecture, architecture, StringComparison.Ordinal));
        }

        /// <summary>Replace the measurement for the record's architecture. False when the file could not be written.</summary>
        public bool Record(HelperModelCostRecord record)
        {
            if (record == null) return false;
            lock (Gate)
            {
                var file = Read();
                file.Measurements.RemoveAll(m => string.Equals(m.Architecture, record.Architecture, StringComparison.Ordinal));
                file.Measurements.Add(record);
                _cached = file;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    using var fs = File.Create(_path);
                    Serializer.Serialize(fs, file);
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("HelperModelCostStore: not saved: " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>A record of a connection's usable model costs, for this process's architecture.</summary>
        internal static HelperModelCostRecord From(string helperVersion, IEnumerable<WdspModelCost> costs, DateTime measuredUtc) => new()
        {
            Architecture = ThisArchitecture,
            HelperVersion = helperVersion,
            MeasuredUtc = measuredUtc,
            Slots = costs.Select(c => new HelperModelCostEntry
            {
                Engine = c.EngineId, Slot = c.Slot, Option = c.Option, Frames = c.CalibrationFrames,
                MedianNanos = c.MedianNanos, P99Nanos = c.P99Nanos,
            }).ToList(),
        };

        private HelperModelCostFile Read()
        {
            if (_cached != null) return _cached;
            try
            {
                if (!File.Exists(_path)) return _cached = new();
                using var fs = File.OpenRead(_path);
                return _cached = Serializer.Deserialize(fs) as HelperModelCostFile ?? new();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("HelperModelCostStore: unreadable, read as not measured: " + ex.Message);
                return _cached = new();
            }
        }
    }

    /// <summary>
    /// Premium CW's cost as the helper last measured it on this machine, for
    /// the CW starting point's rule (<see cref="NoiseModeRules.ChooseCwModel"/>).
    /// No measurement, or one in which Premium CW was not measured, is null,
    /// which keeps the ruled unmeasured default.
    /// </summary>
    public sealed class MeasuredNnrCwCost : INnrCwCostSource
    {
        private readonly Func<HelperModelCostStore?> _store;

        /// <summary>The app's store under the settings root.</summary>
        public MeasuredNnrCwCost() : this(HelperModelCostStore.Default) { }

        internal MeasuredNnrCwCost(Func<HelperModelCostStore?> store) => _store = store;

        public double? PremiumCw99thPercentileMs
        {
            get
            {
                var slot = _store()?.Find()?.Measured("nnr", NoiseModeRules.NnrPremiumCw);
                return slot == null ? null : slot.P99Nanos / 1e6;
            }
        }
    }
}
