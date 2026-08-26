using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using JJTrace;

namespace Radios.Fixer.Evidence
{
    /// <summary>
    /// Where Fixer runs live on disk: one JSON file per run, written whole and
    /// atomically on every recording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Location:</b> <c>FixerRuns</c> under the settings root, resolved
    /// from <see cref="RadioConfig.AppDataRoot"/> — never from
    /// SpecialFolder.ApplicationData plus "JJFlexRadio", which is invisible to
    /// JJFLEX_CONFIG_DIR relocation (nineteen sites made that mistake; see
    /// CLAUDE.md). A test run against a throwaway tree keeps its run records
    /// in the throwaway tree.
    /// </para>
    /// <para>
    /// <b>Retention, decided up front (#252, on #92's precedent):</b> the
    /// newest <see cref="MaxRunsKept"/> runs are kept, oldest deleted beyond
    /// that. A count cap rather than an age cap, deliberately: a run record is
    /// evidence, and "it worked Tuesday" gains value with age — but "small and
    /// unbounded" is how AppData reached 2.2 GB, so the bound exists from day
    /// one. Two hundred runs at a few tens of KB each is a couple of MB.
    /// </para>
    /// <para>
    /// <b>Writes are atomic</b> (temp file, then replace), because the write
    /// path runs after every stage — including mid-run, including while a
    /// crash is exactly the thing being defended against. A torn write must
    /// cost at most one recording, never the file.
    /// </para>
    /// <para>
    /// Nothing here throws to a caller on I/O trouble. Evidence-keeping must
    /// never take the diagnosis down; failures are traced and reported as
    /// booleans or nulls.
    /// </para>
    /// </remarks>
    public sealed class FixerRunStore
    {
        /// <summary>See the class remarks for why a count, and why this count.</summary>
        public const int MaxRunsKept = 200;

        /// <summary>How far back the resume list reaches. A run stopped months
        /// ago is still viewable evidence, but offering it for resumption is
        /// noise — the station has moved on, and the fingerprint check would
        /// say little else.</summary>
        public const int ResumeWindowDays = 14;

        public const string FolderName = "FixerRuns";
        private const string FilePrefix = "run-";
        private const string FileSuffix = ".json";

        private readonly string _root;

        /// <summary>The store at an explicit root. Tests use this; the app
        /// uses <see cref="Default"/>.</summary>
        public FixerRunStore(string rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir))
                throw new ArgumentException("the store needs a directory", nameof(rootDir));
            _root = rootDir;
        }

        /// <summary>The store under the settings root.</summary>
        public static FixerRunStore Default()
            => new FixerRunStore(Path.Combine(RadioConfig.AppDataRoot, FolderName));

        public string Root => _root;

        /// <summary>The file this record lives in. The name embeds the start
        /// stamp so files sort chronologically by name alone, and the run id
        /// so a person can find "run A52-5T2" in Explorer.</summary>
        public string PathFor(FixerRunRecord record)
            => Path.Combine(_root, FilePrefix
                + record.StartedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-" + record.RunId + FileSuffix);

        /// <summary>
        /// Write the record, atomically, creating the folder on first use and
        /// pruning beyond the retention cap. False (traced) on failure.
        /// </summary>
        public bool Save(FixerRunRecord record)
        {
            if (record == null) return false;
            try
            {
                Directory.CreateDirectory(_root);

                string finalPath = PathFor(record);
                string tempPath = finalPath + ".tmp";
                File.WriteAllText(tempPath, record.ToJson());
                File.Move(tempPath, finalPath, overwrite: true);

                Prune(keep: finalPath);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerRunStore: could not save run " + record.RunId + " — "
                                  + ex.Message, TraceLevel.Warning);
                return false;
            }
        }

        /// <summary>One file as a record, or null (traced) when it cannot be
        /// read — one corrupt file must never take the list down with it.</summary>
        public FixerRunRecord LoadFile(string path)
        {
            try
            {
                FixerRunRecord record = FixerRunRecord.FromJson(File.ReadAllText(path));
                if (record == null)
                    Tracing.TraceLine("FixerRunStore: " + Path.GetFileName(path)
                        + " is not a readable run record — skipped", TraceLevel.Warning);
                return record;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerRunStore: could not read " + Path.GetFileName(path)
                                  + " — " + ex.Message, TraceLevel.Warning);
                return null;
            }
        }

        /// <summary>
        /// Every readable run, newest first. <paramref name="unreadableFiles"/>
        /// counts the files that exist but could not be read — the honest
        /// census a list surface should state rather than silently shrink by.
        /// </summary>
        public IReadOnlyList<FixerRunRecord> LoadAll(out int unreadableFiles)
        {
            unreadableFiles = 0;
            var records = new List<FixerRunRecord>();
            foreach (string path in RunFilesNewestFirst())
            {
                FixerRunRecord record = LoadFile(path);
                if (record == null) unreadableFiles++;
                else records.Add(record);
            }
            return records;
        }

        /// <summary>The most recent record with this run id, or null.</summary>
        public FixerRunRecord FindById(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return null;
            foreach (string path in RunFilesNewestFirst())
            {
                // The id is the filename's tail; check it before parsing JSON.
                string stem = Path.GetFileNameWithoutExtension(path);
                if (!stem.EndsWith("-" + runId, StringComparison.OrdinalIgnoreCase)) continue;

                FixerRunRecord record = LoadFile(path);
                if (record != null
                    && string.Equals(record.RunId, runId, StringComparison.OrdinalIgnoreCase))
                    return record;
            }
            return null;
        }

        /// <summary>
        /// Runs that stopped part-way and are recent enough to offer for
        /// resumption (#252 part 2): incomplete, and started within
        /// <see cref="ResumeWindowDays"/> of <paramref name="nowUtc"/>.
        /// Newest first, so the most likely candidate leads the list.
        /// </summary>
        public IReadOnlyList<FixerRunRecord> StoppedRuns(DateTime nowUtc)
        {
            DateTime cutoff = nowUtc.AddDays(-ResumeWindowDays);
            return LoadAll(out _)
                .Where(r => !r.IsComplete() && r.StartedUtc >= cutoff)
                .ToList();
        }

        /// <summary>Delete one run's file. False (traced) when it cannot be.</summary>
        public bool Delete(FixerRunRecord record)
        {
            if (record == null) return false;
            try
            {
                string path = PathFor(record);
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerRunStore: could not delete run " + record.RunId + " — "
                                  + ex.Message, TraceLevel.Warning);
                return false;
            }
        }

        // -------- plumbing --------

        /// <summary>Run files, newest first. The start stamp leads the
        /// filename, so name order IS date order — no JSON gets parsed to
        /// decide what to prune.</summary>
        private IEnumerable<string> RunFilesNewestFirst()
        {
            if (!Directory.Exists(_root)) return Array.Empty<string>();
            try
            {
                return Directory.GetFiles(_root, FilePrefix + "*" + FileSuffix)
                    .OrderByDescending(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerRunStore: could not list " + _root + " — " + ex.Message,
                                  TraceLevel.Warning);
                return Array.Empty<string>();
            }
        }

        /// <summary>Enforce <see cref="MaxRunsKept"/>. The file just written
        /// is never the one deleted, whatever its timestamp says.</summary>
        private void Prune(string keep)
        {
            try
            {
                List<string> files = RunFilesNewestFirst().ToList();
                if (files.Count <= MaxRunsKept) return;

                foreach (string path in files.Skip(MaxRunsKept))
                {
                    if (string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(path); }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine("FixerRunStore: could not prune "
                            + Path.GetFileName(path) + " — " + ex.Message, TraceLevel.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerRunStore: prune failed — " + ex.Message,
                                  TraceLevel.Warning);
            }
        }
    }
}
