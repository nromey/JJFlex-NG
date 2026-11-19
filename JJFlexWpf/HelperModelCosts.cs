using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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

    /// <summary>What <see cref="HelperModelCostStore.Save"/> did with a measurement.</summary>
    public enum HelperModelCostSave
    {
        /// <summary>Written: it is now the architecture's record.</summary>
        Saved,
        /// <summary>Not written: the file already holds a later measurement for that architecture.</summary>
        Older,
        /// <summary>Not written: the file could not be locked or written.</summary>
        Failed,
    }

    /// <summary>
    /// What each of the helper's model slots costs on this machine, remembered
    /// between runs (Sprint 49 Track K, #1002; ruled 2026-11-07, question 11:
    /// "remembered per machine and re-measured when the helper restarts"). One
    /// record per architecture, because an x86 helper and an x64 helper are
    /// different programs with different costs; a later measurement replaces
    /// the earlier for its architecture, and an earlier one never replaces a
    /// later one. A file that cannot be read reads as "not measured", and the
    /// next measurement overwrites it. The file lives under
    /// <see cref="RadioConfig.AppDataRoot"/>, so a run with a throwaway settings
    /// folder never touches the operator's.
    ///
    /// Nothing is cached: every read and every update reads the file as it is
    /// now, so an update changes only its own architecture's record and keeps
    /// whatever any other store, in this process or another, has written. Every
    /// read and update holds one named mutex per file, which every process of
    /// this user shares, and an update replaces the file whole by renaming a
    /// finished copy over it, so a reader never sees half a file. A named mutex
    /// rather than an exclusive open of the file itself: it waits without
    /// polling, Windows hands it on when a process dies holding it (abandoned,
    /// which is taken as acquired: the file is whole either way, because of the
    /// rename), and it leaves the file free to be replaced by a rename, which
    /// an exclusive open would prevent.
    /// </summary>
    public sealed class HelperModelCostStore
    {
        public const string FileName = "helperModelCosts.xml";
        /// <summary>How long a read or an update waits for another to finish before giving up.</summary>
        internal static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);
        /// <summary>
        /// A record stamped later than this beyond the present is taken to be from
        /// a clock that has since been set back, and is replaced rather than kept:
        /// otherwise one wrong clock would freeze the record until the present
        /// caught up with it.
        /// </summary>
        internal static readonly TimeSpan FutureSlack = TimeSpan.FromMinutes(1);
        private static readonly XmlSerializer Serializer = new(typeof(HelperModelCostFile));
        private readonly string _path;
        private readonly string _mutexName;

        public HelperModelCostStore(string root)
        {
            _path = Path.GetFullPath(Path.Combine(root ?? throw new ArgumentNullException(nameof(root)), FileName));
            _mutexName = MutexNameFor(_path);
        }

        /// <summary>
        /// The mutex for one file: the same for every process of this user that
        /// names the file, whatever case the path was given in.
        /// </summary>
        internal static string MutexNameFor(string fullPath)
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant()));
            return @"Local\JJFlexRadio.HelperModelCosts." + Convert.ToHexString(hash, 0, 16);
        }

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
            HelperModelCostFile? file = null;
            if (!Locked(() => file = Read())) return null;
            return file!.Measurements.LastOrDefault(m => string.Equals(m.Architecture, architecture, StringComparison.Ordinal));
        }

        /// <summary>Replace the measurement for the record's architecture. False when nothing was written.</summary>
        public bool Record(HelperModelCostRecord record) => Save(record) == HelperModelCostSave.Saved;

        /// <summary>
        /// Replace the measurement for the record's architecture, unless the file
        /// already holds a later one for it: a save left queued from an earlier
        /// helper run must not undo a later run's. Equal times replace (the later
        /// call wins).
        /// </summary>
        public HelperModelCostSave Save(HelperModelCostRecord record)
        {
            if (record == null) return HelperModelCostSave.Failed;
            var result = HelperModelCostSave.Failed;
            bool locked = Locked(() =>
            {
                var file = Read();
                var held = file.Measurements.LastOrDefault(m => string.Equals(m.Architecture, record.Architecture, StringComparison.Ordinal));
                if (held != null && IsLater(held.MeasuredUtc, record.MeasuredUtc, DateTime.UtcNow))
                {
                    System.Diagnostics.Trace.WriteLine($"HelperModelCostStore: kept the {record.Architecture} measurement of " +
                        $"{held.MeasuredUtc:O} over an earlier one of {record.MeasuredUtc:O}");
                    result = HelperModelCostSave.Older;
                    return;
                }
                file.Measurements.RemoveAll(m => string.Equals(m.Architecture, record.Architecture, StringComparison.Ordinal));
                file.Measurements.Add(record);
                result = Write(file) ? HelperModelCostSave.Saved : HelperModelCostSave.Failed;
            });
            return locked ? result : HelperModelCostSave.Failed;
        }

        /// <summary>
        /// Whether the held measurement is later than the offered one, and so is
        /// kept. A held time more than <see cref="FutureSlack"/> ahead of the
        /// present came from a clock since set back, and is not kept.
        /// </summary>
        internal static bool IsLater(DateTime held, DateTime offered, DateTime nowUtc) =>
            ToUtc(held) > ToUtc(offered) && ToUtc(held) <= nowUtc + FutureSlack;

        private static DateTime ToUtc(DateTime t) => t.Kind switch
        {
            DateTimeKind.Utc => t,
            DateTimeKind.Local => t.ToUniversalTime(),
            _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
        };

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

        /// <summary>Run an action holding this file's mutex. False when the mutex could not be had.</summary>
        private bool Locked(Action action)
        {
            Mutex mutex;
            try { mutex = new Mutex(false, _mutexName); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("HelperModelCostStore: no lock: " + ex.Message);
                return false;
            }
            using (mutex)
            {
                bool held;
                try { held = mutex.WaitOne(LockWait); }
                catch (AbandonedMutexException) { held = true; }   // its holder died; the file is whole, because it is renamed into place
                if (!held)
                {
                    System.Diagnostics.Trace.WriteLine("HelperModelCostStore: the file stayed locked; left alone");
                    return false;
                }
                try { action(); }
                finally { mutex.ReleaseMutex(); }
                return true;
            }
        }

        /// <summary>The file as it is now. Call holding the mutex.</summary>
        private HelperModelCostFile Read()
        {
            try
            {
                if (!File.Exists(_path)) return new();
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Serializer.Deserialize(fs) as HelperModelCostFile ?? new();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("HelperModelCostStore: unreadable, read as not measured: " + ex.Message);
                return new();
            }
        }

        /// <summary>Write a finished copy beside the file and rename it over the file. Call holding the mutex.</summary>
        private bool Write(HelperModelCostFile file)
        {
            string temp = _path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    Serializer.Serialize(fs, file);
                for (int attempt = 1; ; attempt++)
                {
                    try { File.Move(temp, _path, overwrite: true); return true; }
                    catch (IOException) when (attempt < 4) { Thread.Sleep(25 * attempt); }   // a scanner holding the file for a moment
                    catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(25 * attempt); }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("HelperModelCostStore: not saved: " + ex.Message);
                try { File.Delete(temp); } catch (Exception) { }
                return false;
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
