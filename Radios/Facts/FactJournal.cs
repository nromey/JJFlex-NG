#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using JJTrace;

namespace Radios.Facts
{
    /// <summary>What became of one saved source when history was loaded.</summary>
    public enum SourceStatus
    {
        Loaded = 0,

        /// <summary>The primary would not read; its validated last-good generation did. The newer interval is a recovery gap.</summary>
        RecoveredFromLastGood = 1,

        /// <summary>Another running instance holds it. The inventory is partial — not corrupt, not complete.</summary>
        SkippedLive = 2,

        Inaccessible = 3,
        Corrupt = 4,
        Unsupported = 5,

        /// <summary>A schema-1 file: legacy evidence, loaded with its limitations named.</summary>
        Legacy = 6,
    }

    public sealed class SourceInventoryEntry
    {
        internal SourceInventoryEntry(string name, SourceStatus status, int records, string? note)
        {
            Name = name;
            Status = status;
            Records = records;
            Note = note;
        }

        public string Name { get; }
        public SourceStatus Status { get; }
        public int Records { get; }
        public string? Note { get; }
        public override string ToString() => Name + ": " + Status + " (" + Records + ")";
    }

    /// <summary>The load inventory: every source and what it contributed, counted separately.</summary>
    public sealed class LoadReport
    {
        internal LoadReport(IReadOnlyList<SourceInventoryEntry> sources, HydrationCounts counts)
        {
            Sources = sources;
            Counts = counts;
        }

        public IReadOnlyList<SourceInventoryEntry> Sources { get; }
        public HydrationCounts Counts { get; }

        /// <summary>Some source was not fully accounted for.</summary>
        public bool Partial => Sources.Any(s => s.Status != SourceStatus.Loaded && s.Status != SourceStatus.Legacy);

        public override string ToString() => string.Join("; ", Sources) + " — " + Counts;
    }

    /// <summary>
    /// The store's disk half: one versioned, readable JSON shard per writer
    /// incarnation, under the settings root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Schema 2 persists meaning, not just fields.</b> Origin identity,
    /// classification and descriptor as they were, typed material, the exact
    /// sets presented and reviewed, attempt evidence, the receipt's consumed
    /// allowance, and why anything was paused. Schema 1 wrote many of these and
    /// then read them back as defaults — process zero, unclassified, pending —
    /// so a reviewed item returned as owed and two shards aliased one another.
    /// </para>
    /// <para>
    /// <b>History, never authority.</b> A loaded record carries no publisher,
    /// no grant and no receipt permit, and it never speaks on its own. An
    /// attempt that was in flight when the file was written loads as
    /// interrupted, completion unconfirmed.
    /// </para>
    /// <para>
    /// <b>Durability is reported, never promised.</b> Persisted-through N is
    /// acknowledged only after the image containing N commits; a store already
    /// at N+1 stays unsaved through the newer mutation.
    /// </para>
    /// </remarks>
    public sealed class FactJournal : IDisposable
    {
        /// <summary>
        /// The on-disk format. Bumped when the shape changes, never reused.
        /// Schema 3 added the occurrence lineage and the per-assertion
        /// continuity evidence; a schema-2 file still loads, with its
        /// continuity marked unsupported.
        /// </summary>
        public const int SchemaVersion = 3;

        private readonly FactStore _store;
        private readonly string _directory;
        private readonly string _shardPath;
        private readonly object _gate = new object();
        private FileStream? _lease;
        private long _generation;
        private long _committedMutation = -1;
        private bool _disposed;

        /// <param name="directory">
        /// Normally <c>facts</c> under <see cref="RadioConfig.AppDataRoot"/>,
        /// resolved by the caller from that root so a relocated test root is
        /// really used.
        /// </param>
        public FactJournal(FactStore store, string directory)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            if (!Path.IsPathRooted(_directory))
                throw new ArgumentException("the journal directory must be absolute", nameof(directory));
            // The shard identity is the writer identity: one lifetime, one file.
            _shardPath = Path.Combine(_directory, "facts-" + store.WriterIncarnation.ToString("N") + ".json");
        }

        public string ShardPath => _shardPath;
        public string Directory => _directory;

        internal string ShardKey => "shard:" + Path.GetFileName(_shardPath);

        public bool HoldsLease
        {
            get { lock (_gate) return _lease != null; }
        }

        /// <summary>Take this writer's exclusive lease for its whole lifetime.</summary>
        public bool TakeLease()
        {
            lock (_gate)
            {
                if (_disposed) return false;
                if (_lease != null) return true;
                try
                {
                    System.IO.Directory.CreateDirectory(_directory);
                    // Deleted when released — by Dispose, or by Windows when the
                    // process ends however it ends — so launches do not leave a
                    // trail of empty lease files. A missing lease file means
                    // "released", which is exactly what the loader needs.
                    _lease = new FileStream(_shardPath + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                            FileShare.None, 1, FileOptions.DeleteOnClose);
                    _store.NoteJournalAttached();
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _store.NotePersistFailure(ShardKey, Path.GetFileName(_shardPath),
                        "the fact store's file could not be reserved: " + ex.Message);
                    return false;
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Writing
        // ────────────────────────────────────────────────────────────────

        /// <summary>Capture the newest image and write it.</summary>
        public bool Write() => Write(_store.CaptureImage());

        /// <summary>
        /// Write one captured image. Temporary file, flush to disk, atomic
        /// replace keeping the previous generation as last-good, and only
        /// then acknowledge the image's mutation sequence.
        /// </summary>
        internal bool Write(StoreImage image)
        {
            lock (_gate)
            {
                if (_disposed) return false;
                if (_lease == null)
                {
                    // Never a false success: without the lease this writer does
                    // not own the file it would replace.
                    _store.NotePersistFailure(ShardKey, Path.GetFileName(_shardPath),
                        "a write was attempted without holding the file's lease, so it was refused");
                    return false;
                }

                // Coalescing: an older image than one already committed has
                // nothing to add.
                if (image.Mutation <= _committedMutation) return true;

                if (image.IsEmpty && !File.Exists(_shardPath))
                {
                    // Nothing to persist and nothing previously persisted.
                    _committedMutation = image.Mutation;
                    _store.NotePersisted(image.Mutation, ShardKey);
                    return true;
                }

                string temp = _shardPath + ".tmp";
                try
                {
                    long generation = _generation + 1;
                    byte[] bytes = new UTF8Encoding(false).GetBytes(FactJournalFormat.Render(image, generation));
                    if (bytes.Length > FactStoreCapacity.MaxJournalBytes)
                    {
                        _store.NotePersistFailure(ShardKey, Path.GetFileName(_shardPath),
                            "the fact store is larger than its disk allowance, so it was not written");
                        return false;
                    }

                    System.IO.Directory.CreateDirectory(_directory);
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(flushToDisk: true);
                    }

                    if (File.Exists(_shardPath))
                        File.Replace(temp, _shardPath, _shardPath + ".last-good", ignoreMetadataErrors: true);
                    else
                        File.Move(temp, _shardPath);

                    _generation = generation;
                    _committedMutation = image.Mutation;
                    _store.NotePersisted(image.Mutation, ShardKey);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _store.NotePersistFailure(ShardKey, Path.GetFileName(_shardPath),
                        "the fact store could not be written: " + ex.Message);
                    Tracing.TraceLine("FactJournal: write failed — " + ex.Message, TraceLevel.Warning);
                    return false;
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Loading
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Load every released source as history, keeping an inventory of what
        /// each contributed. Nothing that fails to load becomes an empty,
        /// healthy-looking list: each failure is a row.
        /// </summary>
        public LoadReport LoadHistory()
        {
            var inventory = new List<SourceInventoryEntry>();
            var sources = new List<LoadedSource>();
            _store.SetLoadState(HistoryLoadState.Loading);
            try
            {
                if (!System.IO.Directory.Exists(_directory))
                    return new LoadReport(inventory, _store.Hydrate(sources));

                foreach (string path in System.IO.Directory.EnumerateFiles(_directory, "facts-*.json")
                                                           .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                                           .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.Equals(path, _shardPath, StringComparison.OrdinalIgnoreCase)) continue;
                    inventory.Add(LoadOne(path, sources));
                }

                HydrationCounts counts = _store.Hydrate(sources);
                return new LoadReport(inventory, counts);
            }
            finally
            {
                _store.SetLoadState(HistoryLoadState.Loaded);
            }
        }

        private SourceInventoryEntry LoadOne(string path, List<LoadedSource> sources)
        {
            string name = Path.GetFileName(path);
            FileStream? lease = null;
            try
            {
                // Hold the source's lease WHILE reading it, rather than probing
                // and releasing first — a probe-then-read lets a writer start in
                // between.
                string leasePath = path + ".lease";
                if (File.Exists(leasePath))
                {
                    try
                    {
                        lease = new FileStream(leasePath, FileMode.Open, FileAccess.Read, FileShare.None);
                    }
                    catch (IOException)
                    {
                        _store.NoteIssue(IssueKind.IncompleteInventory, "live:" + name, name,
                            "another running instance is writing this history, so it was not read; this list is partial, not damaged",
                            1, ExtentCertainty.Unknown, name, "live:" + name);
                        return new SourceInventoryEntry(name, SourceStatus.SkippedLive, 0, "held by a live writer");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        return Inaccessible(name, ex.Message);
                    }
                }

                string text;
                try
                {
                    text = File.ReadAllText(path, Encoding.UTF8);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Inaccessible(name, ex.Message);
                }

                string contentHash = FactHash.Of(text);
                try
                {
                    LoadedSource source = FactJournalFormat.Parse(text, name, out List<string> rejected);
                    sources.Add(source);
                    NoteRejected(name, rejected);
                    if (source.Legacy)
                    {
                        // A standing limitation of the record, not a fault
                        // that is happening: it leaves the default view once
                        // read, stays in history, and keeps qualifying every
                        // claim about the saved record.
                        NoteMigrationGap("legacy:", name, contentHash, source.Facts.Count,
                            "this history was saved by an older format; its delivery evidence is unverified and incomplete");
                        return new SourceInventoryEntry(name, SourceStatus.Legacy, source.Facts.Count, null);
                    }
                    if (source.DeliveryContinuityUnsupported)
                    {
                        NoteMigrationGap("schema2:", name, contentHash, source.Continuity.Count,
                            "this history was saved by an earlier build; its records are usable, but a reconnect cannot continue a condition from it");
                        return new SourceInventoryEntry(name, SourceStatus.Loaded, source.Facts.Count,
                                                        "schema 2: delivery continuity unsupported");
                    }
                    return new SourceInventoryEntry(name, SourceStatus.Loaded, source.Facts.Count, null);
                }
                catch (UnsupportedSchemaException ex)
                {
                    _store.NoteIssue(IssueKind.IncompleteInventory, "unsupported:" + name, name,
                        "this history was saved in a format this version cannot read; the file is kept",
                        1, ExtentCertainty.Unknown, name + ": " + ex.Message, "unsupported:" + contentHash);
                    return new SourceInventoryEntry(name, SourceStatus.Unsupported, 0, ex.Message);
                }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
                {
                    return RecoverFromLastGood(path, name, contentHash, ex.Message, sources);
                }
            }
            finally
            {
                lease?.Dispose();
            }
        }

        private SourceInventoryEntry RecoverFromLastGood(string path, string name, string contentHash, string why,
                                                         List<LoadedSource> sources)
        {
            string lastGood = path + ".last-good";
            if (File.Exists(lastGood))
            {
                try
                {
                    LoadedSource recovered = FactJournalFormat.Parse(File.ReadAllText(lastGood, Encoding.UTF8),
                                                                    name + " (last good)", out List<string> rejected);
                    sources.Add(recovered);
                    NoteRejected(name, rejected);
                    // Usable history — and the newer interval it does not
                    // contain is still missing, of unknown extent.
                    _store.NoteIssue(IssueKind.RecoveryGap, "recovery:" + name, name,
                        "the newest saved copy could not be read; an earlier copy was used, and whatever happened after it is missing",
                        1, ExtentCertainty.Unknown, name + ": " + why, "gap:" + contentHash);
                    return new SourceInventoryEntry(name, SourceStatus.RecoveredFromLastGood, recovered.Facts.Count, why);
                }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException
                                            or UnsupportedSchemaException or IOException or UnauthorizedAccessException)
                {
                    why += "; the earlier copy could not be read either (" + ex.Message + ")";
                }
            }

            _store.NoteIssue(IssueKind.RecoveryGap, "recovery:" + name, name,
                "saved history could not be read, so some earlier events are missing from this list; nothing was deleted",
                1, ExtentCertainty.Unknown, name + ": " + why, "gap:" + contentHash);
            return new SourceInventoryEntry(name, SourceStatus.Corrupt, 0, why);
        }

        private SourceInventoryEntry Inaccessible(string name, string why)
        {
            _store.NoteIssue(IssueKind.IncompleteInventory, "inaccessible:" + name, name,
                "saved history could not be opened, so this list may be missing some of it",
                1, ExtentCertainty.Unknown, name + ": " + why, "inaccessible:" + name);
            return new SourceInventoryEntry(name, SourceStatus.Inaccessible, 0, why);
        }

        private void NoteRejected(string name, List<string> rejected)
        {
            if (rejected.Count == 0) return;
            // Record-level salvage: each recovered record passed full
            // validation, and the remaining gap is explicit — one key per
            // rejected record, so the count is provably what the keys say.
            for (int i = 0; i < rejected.Count; i++)
                _store.NoteIssue(IssueKind.RecoveryGap, "records:" + name, name,
                    "some saved records failed validation and were not loaded; the file is kept",
                    1, ExtentCertainty.Exact, rejected[i],
                    "records:" + name + ":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + FactHash.Of(rejected[i]));
        }

        /// <summary>
        /// Note a migration limitation with one deduplication key per thing it
        /// counts. A row that counted more than it keyed could never be proved
        /// the same information as its own saved copy — equal partial keys
        /// prove nothing — so a review of it was lost at every restart.
        /// Nothing to count is nothing to be limited by: no row.
        /// </summary>
        private void NoteMigrationGap(string prefix, string name, string contentHash, int count, string reason)
        {
            for (int i = 0; i < count; i++)
                _store.NoteIssue(IssueKind.MigrationGap, prefix + name, name, reason, 1, ExtentCertainty.Exact,
                                 i == 0 ? name : null,
                                 prefix + contentHash + ":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                 IssueState.Limitation);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try { _lease?.Dispose(); } catch (IOException) { /* on the way out */ }
                _lease = null;
            }
        }
    }

    /// <summary>A source written in a schema this version does not read.</summary>
    public sealed class UnsupportedSchemaException : Exception
    {
        public UnsupportedSchemaException(string message) : base(message) { }
    }

    /// <summary>
    /// Coalesces persistence to the newest pending image on a background
    /// thread. One write in flight at a time; a dirty signal during a write
    /// schedules exactly one more.
    /// </summary>
    public sealed class FactJournalWriter : IDisposable
    {
        private readonly FactStore _store;
        private readonly FactJournal _journal;
        private int _dirty;
        private int _running;
        private bool _disposed;

        public FactJournalWriter(FactStore store, FactJournal journal)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _store.PersistenceNeeded += OnNeeded;
        }

        private void OnNeeded()
        {
            if (_disposed) return;
            Interlocked.Exchange(ref _dirty, 1);
            if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
                ThreadPool.QueueUserWorkItem(_ => Drain());
        }

        private void Drain()
        {
            try
            {
                while (Interlocked.Exchange(ref _dirty, 0) == 1 && !_disposed)
                    _journal.Write();
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                if (Volatile.Read(ref _dirty) == 1 && !_disposed) OnNeeded();
            }
        }

        /// <summary>
        /// The bounded final flush: one write attempt, no retries. It neither
        /// holds up protection nor invents durable success — a failure leaves
        /// the store's explicit unsaved state.
        /// </summary>
        public bool FlushOnce()
        {
            if (_store.PersistedThrough >= _store.MutationSequence) return true;
            return _journal.Write();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _store.PersistenceNeeded -= OnNeeded;
        }
    }
}
