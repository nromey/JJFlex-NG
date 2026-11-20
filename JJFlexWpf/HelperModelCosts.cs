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
    /// "remembered per machine and re-measured when the helper restarts").
    /// Kept per architecture, because an x86 helper and an x64 helper are
    /// different programs with different costs. Since Sprint 49 Track K2 (ruled
    /// 2026-11-19, X11) the file keeps the last <see cref="Kept"/> measurements
    /// of each architecture, oldest first, so the CW rule can read the middle
    /// of them rather than whichever one came last. A file written with one
    /// measurement per architecture has exactly this shape with fewer records,
    /// and reads as it is. A later measurement joins its architecture's list,
    /// pushing the oldest out beyond <see cref="Kept"/>; one earlier than the
    /// newest held is never saved at all, so it can neither replace a later
    /// one nor sit after it. A file that cannot be read reads as "not
    /// measured", and the next measurement overwrites it. The file lives under
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

        /// <summary>
        /// How many measurements each architecture keeps (Sprint 49 Track K2,
        /// X11). Five, the ruling's own example, because the rule reads their
        /// median: the middle of five stays on a quiet measurement while up to
        /// two of the five were taken on a busy machine (Track K measured
        /// Premium CW's 99th percentile here at about 4 ms quiet, 5.2 to 8.2 ms
        /// while compiling, and 80 to 788 ms under heavy load). The helper
        /// measures once per start, so five is the last five helper starts:
        /// recent enough that a changed machine moves the median within three
        /// starts. More would hold an old machine's figures longer for little
        /// more protection.
        /// </summary>
        public const int Kept = 5;
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
        public HelperModelCostRecord? Find(string? architecture = null) => FindRecent(architecture).FirstOrDefault();

        /// <summary>
        /// The measurements kept for an architecture (this process's by default),
        /// newest first, at most <see cref="Kept"/>; empty when there are none or
        /// the file cannot be read. Read once, holding the lock, so the list is
        /// the file at one moment.
        /// </summary>
        public IReadOnlyList<HelperModelCostRecord> FindRecent(string? architecture = null)
        {
            architecture ??= ThisArchitecture;
            HelperModelCostFile? file = null;
            if (!Locked(() => file = Read())) return Array.Empty<HelperModelCostRecord>();
            return Newest(Of(file!, architecture)).Take(Kept).ToList();
        }

        /// <summary>Add a measurement to its architecture's list. False when nothing was written.</summary>
        public bool Record(HelperModelCostRecord record) => Save(record) == HelperModelCostSave.Saved;

        /// <summary>
        /// Add a measurement to its architecture's list, unless the file already
        /// holds a later one for it: a save left queued from an earlier helper
        /// run must not undo a later run's, nor sit after it in the list. Equal
        /// times replace (the later call wins). The list keeps the newest
        /// <see cref="Kept"/>. A record stamped more than <see cref="FutureSlack"/>
        /// beyond the present came from a clock since set back and is dropped, so
        /// it cannot freeze the list. The other architecture's records are written
        /// back exactly as they were read.
        /// </summary>
        public HelperModelCostSave Save(HelperModelCostRecord record)
        {
            if (record == null) return HelperModelCostSave.Failed;
            var result = HelperModelCostSave.Failed;
            bool locked = Locked(() =>
            {
                var file = Read();
                var now = DateTime.UtcNow;
                var mine = Of(file, record.Architecture);
                var held = Newest(mine).FirstOrDefault(m => ToUtc(m.MeasuredUtc) <= now + FutureSlack);
                if (held != null && IsLater(held.MeasuredUtc, record.MeasuredUtc, now))
                {
                    System.Diagnostics.Trace.WriteLine($"HelperModelCostStore: kept the {record.Architecture} measurement of " +
                        $"{held.MeasuredUtc:O} over an earlier one of {record.MeasuredUtc:O}");
                    result = HelperModelCostSave.Older;
                    return;
                }
                var candidates = mine
                    .Where(m => ToUtc(m.MeasuredUtc) <= now + FutureSlack && ToUtc(m.MeasuredUtc) != ToUtc(record.MeasuredUtc))
                    .Append(record);
                // Oldest first in the file, newest last: an app from before Track
                // K2 reads an architecture's LAST record as its measurement, so it
                // still reads the newest.
                var kept = Newest(candidates).Take(Kept).Reverse().ToList();
                file.Measurements.RemoveAll(m => string.Equals(m.Architecture, record.Architecture, StringComparison.Ordinal));
                file.Measurements.AddRange(kept);
                result = Write(file) ? HelperModelCostSave.Saved : HelperModelCostSave.Failed;
            });
            return locked ? result : HelperModelCostSave.Failed;
        }

        private static List<HelperModelCostRecord> Of(HelperModelCostFile file, string architecture) =>
            file.Measurements.Where(m => string.Equals(m.Architecture, architecture, StringComparison.Ordinal)).ToList();

        /// <summary>
        /// Newest first, by the measurement's own time. Of two stamped alike, the
        /// one later in the file counts as newer, as the last record did when the
        /// file held one per architecture.
        /// </summary>
        private static IEnumerable<HelperModelCostRecord> Newest(IEnumerable<HelperModelCostRecord> records) =>
            records.Select((r, i) => (Record: r, Index: i))
                .OrderByDescending(x => ToUtc(x.Record.MeasuredUtc)).ThenByDescending(x => x.Index)
                .Select(x => x.Record);

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
    /// Premium CW's cost on this machine, for the CW model rule
    /// (<see cref="NoiseModeRules.ChooseCwModel(INnrCwCostSource)"/>): the
    /// median of its 99th percentile over the measurements the store keeps
    /// for this architecture (Sprint 49 Track K2, ruled 2026-11-19, X11: "the
    /// middle of the last few measurements", so one busy moment does not
    /// decide it; with an even count, the lower of the middle two, Sprint 49
    /// Track K3, see <see cref="Median"/>). A measurement in which Premium CW was not measured gives no
    /// figure and is not counted. None at all is null, which keeps the ruled
    /// unmeasured default.
    /// </summary>
    public sealed class MeasuredNnrCwCost : INnrCwCostSource
    {
        private readonly Func<HelperModelCostStore?> _store;

        /// <summary>The app's store under the settings root.</summary>
        public MeasuredNnrCwCost() : this(HelperModelCostStore.Default) { }

        internal MeasuredNnrCwCost(Func<HelperModelCostStore?> store) => _store = store;

        public double? PremiumCw99thPercentileMs => ReadPremiumCw().Ms;

        /// <summary>The median and how many measurements it was taken from, from one read of the store.</summary>
        public NnrCwCost ReadPremiumCw()
        {
            var records = _store()?.FindRecent() ?? Array.Empty<HelperModelCostRecord>();
            var figures = records.Select(r => r.Measured("nnr", NoiseModeRules.NnrPremiumCw))
                .Where(s => s != null).Select(s => s!.P99Nanos / 1e6).ToList();
            return figures.Count == 0 ? new NnrCwCost(null, 0) : new NnrCwCost(Median(figures), figures.Count);
        }

        /// <summary>
        /// The middle figure; with an even count, the LOWER of the two middle
        /// ones, not their average (Sprint 49 Track K3, Noel 2026-11-19, Q6:
        /// "agreed"). A busy moment reads high, so taking the lower one keeps
        /// one busy start from deciding on a machine's second measurement (4 ms
        /// and 788 ms give 4 ms, not 396 ms) and lets a two-versus-two split
        /// keep the ruled default, Premium CW. This leans optimistic and can
        /// delay recognising a real slowdown by one measurement; the live
        /// "can't keep up" warning is the safety net. Odd counts are unchanged.
        /// </summary>
        internal static double Median(IReadOnlyList<double> figures)
        {
            var sorted = figures.OrderBy(f => f).ToList();
            return sorted[(sorted.Count - 1) / 2];
        }
    }
}
