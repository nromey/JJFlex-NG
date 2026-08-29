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
    /// The one on-disk shape every evidence family shares: one JSON file per
    /// record, written whole and atomically, named so that name order IS date
    /// order, kept to a declared count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted from <see cref="FixerRunStore"/> when the QSO signal analyzer
    /// (#271) needed the identical mechanics for its captures. One mechanism,
    /// two record families — per #252's ruling that new evidence artifacts
    /// reuse the Fixer's evidence shape rather than growing a second store
    /// beside it. Everything record-specific — the id, the start stamp, the
    /// serialization — comes through the four abstract members; everything
    /// else (atomic writes, pruning, the unreadable-file census, the
    /// find-by-id filename prefilter) is shared and stays fixed here.
    /// </para>
    /// <para>
    /// <b>Each family gets its own folder.</b> The schema version inside a
    /// record file is per-family, so two families sharing a folder would
    /// misread each other's futures. The folder name is the family's identity
    /// on disk; the file prefix is a second guard inside it.
    /// </para>
    /// <para>
    /// Nothing here throws to a caller on I/O trouble. Evidence-keeping must
    /// never take the feature down; failures are traced and reported as
    /// booleans or nulls.
    /// </para>
    /// </remarks>
    public abstract class EvidenceFileStore<TRecord> where TRecord : class
    {
        private const string FileSuffix = ".json";

        private readonly string _root;
        private readonly string _filePrefix;
        private readonly int _maxKept;
        private readonly string _storeName;

        /// <param name="rootDir">The family's own folder. Resolve it from
        /// <see cref="RadioConfig.AppDataRoot"/> — never from
        /// SpecialFolder.ApplicationData plus "JJFlexRadio", which is invisible
        /// to JJFLEX_CONFIG_DIR relocation.</param>
        /// <param name="filePrefix">Leads every filename, e.g. "run-".</param>
        /// <param name="maxKept">Retention, decided up front (#252, on #92's
        /// precedent): the newest this-many records are kept, oldest deleted
        /// beyond that.</param>
        /// <param name="storeName">Names this store in trace lines.</param>
        protected EvidenceFileStore(string rootDir, string filePrefix, int maxKept, string storeName)
        {
            if (string.IsNullOrWhiteSpace(rootDir))
                throw new ArgumentException("the store needs a directory", nameof(rootDir));
            if (string.IsNullOrWhiteSpace(filePrefix))
                throw new ArgumentException("the store needs a file prefix", nameof(filePrefix));
            if (maxKept < 1)
                throw new ArgumentOutOfRangeException(nameof(maxKept));
            _root = rootDir;
            _filePrefix = filePrefix;
            _maxKept = maxKept;
            _storeName = string.IsNullOrWhiteSpace(storeName) ? GetType().Name : storeName;
        }

        public string Root => _root;

        /// <summary>The record's stable identifier — embedded in the filename
        /// so a person can find "capture A52-5T2" in Explorer.</summary>
        protected abstract string IdOf(TRecord record);

        /// <summary>When the record's window started, UTC — leads the filename
        /// so files sort chronologically by name alone.</summary>
        protected abstract DateTime StartedUtcOf(TRecord record);

        protected abstract string Serialize(TRecord record);

        /// <summary>Null when the text is not a readable record of this family
        /// — including a record from a future schema, which must be skipped,
        /// never guessed at.</summary>
        protected abstract TRecord Deserialize(string json);

        /// <summary>The file this record lives in. Derived from the start
        /// stamp and the id, never remembered — so relabelling a record and
        /// re-saving overwrites the same file in place.</summary>
        public string PathFor(TRecord record)
            => Path.Combine(_root, _filePrefix
                + StartedUtcOf(record).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-" + IdOf(record) + FileSuffix);

        /// <summary>
        /// Write the record, atomically, creating the folder on first use and
        /// pruning beyond the retention cap. False (traced) on failure.
        /// </summary>
        public bool Save(TRecord record)
        {
            if (record == null) return false;
            try
            {
                Directory.CreateDirectory(_root);

                string finalPath = PathFor(record);
                string tempPath = finalPath + ".tmp";
                File.WriteAllText(tempPath, Serialize(record));
                File.Move(tempPath, finalPath, overwrite: true);

                Prune(keep: finalPath);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(_storeName + ": could not save " + IdOf(record) + " — "
                                  + ex.Message, TraceLevel.Warning);
                return false;
            }
        }

        /// <summary>One file as a record, or null (traced) when it cannot be
        /// read — one corrupt file must never take the list down with it.</summary>
        public TRecord LoadFile(string path)
        {
            try
            {
                TRecord record = Deserialize(File.ReadAllText(path));
                if (record == null)
                    Tracing.TraceLine(_storeName + ": " + Path.GetFileName(path)
                        + " is not a readable record — skipped", TraceLevel.Warning);
                return record;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(_storeName + ": could not read " + Path.GetFileName(path)
                                  + " — " + ex.Message, TraceLevel.Warning);
                return null;
            }
        }

        /// <summary>
        /// Every readable record, newest first. <paramref name="unreadableFiles"/>
        /// counts the files that exist but could not be read — the honest
        /// census a list surface should state rather than silently shrink by.
        /// </summary>
        public IReadOnlyList<TRecord> LoadAll(out int unreadableFiles)
        {
            unreadableFiles = 0;
            var records = new List<TRecord>();
            foreach (string path in FilesNewestFirst())
            {
                TRecord record = LoadFile(path);
                if (record == null) unreadableFiles++;
                else records.Add(record);
            }
            return records;
        }

        /// <summary>The most recent record with this id, or null.</summary>
        public TRecord FindById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            foreach (string path in FilesNewestFirst())
            {
                // The id is the filename's tail; check it before parsing JSON.
                string stem = Path.GetFileNameWithoutExtension(path);
                if (!stem.EndsWith("-" + id, StringComparison.OrdinalIgnoreCase)) continue;

                TRecord record = LoadFile(path);
                if (record != null
                    && string.Equals(IdOf(record), id, StringComparison.OrdinalIgnoreCase))
                    return record;
            }
            return null;
        }

        /// <summary>True when at least one record file exists. Cheap — file
        /// names only, no JSON parsed — because it is asked at dialog open to
        /// decide first-run behaviour. False when the folder is missing or
        /// unreadable, which deliberately reads as "first run": the operator
        /// who gets the instructions unnecessarily loses seconds, the one who
        /// needed them and did not get them loses the feature.</summary>
        public bool HasAnyRecord()
        {
            foreach (string _ in FilesNewestFirst()) return true;
            return false;
        }

        /// <summary>Delete one record's file. False (traced) when it cannot be.</summary>
        public bool Delete(TRecord record)
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
                Tracing.TraceLine(_storeName + ": could not delete " + IdOf(record) + " — "
                                  + ex.Message, TraceLevel.Warning);
                return false;
            }
        }

        // -------- plumbing --------

        /// <summary>Record files, newest first. The start stamp leads the
        /// filename, so name order IS date order — no JSON gets parsed to
        /// decide what to prune.</summary>
        private IEnumerable<string> FilesNewestFirst()
        {
            if (!Directory.Exists(_root)) return Array.Empty<string>();
            try
            {
                return Directory.GetFiles(_root, _filePrefix + "*" + FileSuffix)
                    .OrderByDescending(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(_storeName + ": could not list " + _root + " — " + ex.Message,
                                  TraceLevel.Warning);
                return Array.Empty<string>();
            }
        }

        /// <summary>Enforce the retention cap. The file just written is never
        /// the one deleted, whatever its timestamp says.</summary>
        private void Prune(string keep)
        {
            try
            {
                List<string> files = FilesNewestFirst().ToList();
                if (files.Count <= _maxKept) return;

                foreach (string path in files.Skip(_maxKept))
                {
                    if (string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(path); }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine(_storeName + ": could not prune "
                            + Path.GetFileName(path) + " — " + ex.Message, TraceLevel.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(_storeName + ": prune failed — " + ex.Message,
                                  TraceLevel.Warning);
            }
        }
    }
}
