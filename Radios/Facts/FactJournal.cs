#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using JJTrace;

namespace Radios.Facts
{
    /// <summary>
    /// The store's disk half: a versioned, readable JSON shard under the
    /// settings root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is persisted and what is deliberately not.</b> Typed facts,
    /// provenance, material coverage, quiet and review state and delivery
    /// evidence are written. Executable refresh delegates, capabilities that
    /// could be restored as live authority, and a cached current sentence
    /// treated as the truth are NOT — a restored file must be readable as
    /// history and must never be able to assert a current condition or speak on
    /// its own.
    /// </para>
    /// <para>
    /// <b>One shard per process, with a lease.</b> The settings root is not
    /// owned by one application instance, and two processes writing one file is
    /// how a store loses both halves. Each process writes only its own shard
    /// and holds the file open for its lifetime; on restart, shards nobody
    /// holds load as historical records.
    /// </para>
    /// <para>
    /// <b>Durability is reported, never promised.</b> A crash before an
    /// asynchronous write may lose recent information, and no amount of
    /// wording changes that — so the status says pending, failed or up to date,
    /// and a failed flush leaves an explicit unsaved state rather than a claim.
    /// </para>
    /// </remarks>
    public sealed class FactJournal : IDisposable
    {
        /// <summary>The on-disk format. Bumped when the shape changes, never reused.</summary>
        public const int SchemaVersion = 1;

        private readonly FactStore _store;
        private readonly string _directory;
        private readonly string _shardPath;
        private readonly object _gate = new object();
        private FileStream? _lease;
        private bool _disposed;

        /// <summary>
        /// Open (or create) this process's shard.
        /// </summary>
        /// <param name="directory">
        /// Normally <c>facts</c> under <see cref="RadioConfig.AppDataRoot"/>.
        /// Resolved by the caller from <c>AppDataRoot</c> rather than from the
        /// ApplicationData folder directly, so a throwaway settings tree really
        /// is throwaway.
        /// </param>
        public FactJournal(FactStore store, string directory, string shardName)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _shardPath = Path.Combine(directory, "facts-" + shardName + ".json");
        }

        /// <summary>The file this process writes. Nothing else may write it.</summary>
        public string ShardPath => _shardPath;

        /// <summary>True when this process holds the lease on its shard.</summary>
        public bool HoldsLease
        {
            get { lock (_gate) return _lease != null; }
        }

        /// <summary>
        /// Take the lease. Returns false when it could not be taken, which is
        /// reported rather than worked around.
        /// </summary>
        public bool TakeLease()
        {
            lock (_gate)
            {
                if (_lease != null) return true;
                try
                {
                    Directory.CreateDirectory(_directory);
                    // The lease is a separate, empty file held open for the
                    // process's lifetime. Holding the DATA file open would stop
                    // the atomic replace below from ever running.
                    _lease = new FileStream(
                        _shardPath + ".lease", FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None);
                    return true;
                }
                catch (IOException ex)
                {
                    _store.NotePersistence(PersistenceStatus.Failed,
                        "another instance is using this settings folder's fact store: " + ex.Message);
                    return false;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _store.NotePersistence(PersistenceStatus.Failed,
                        "the fact store folder could not be opened: " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// Write the newest complete snapshot.
        /// </summary>
        /// <remarks>
        /// <b>Coalesced by construction.</b> This writes the whole bounded
        /// snapshot rather than an event, so a caller that fires it on every
        /// dirty signal writes the newest state and never a queue of samples.
        /// Temporary file, flush, atomic replace, last-good generation kept.
        /// </remarks>
        public bool Write()
        {
            lock (_gate)
            {
                if (_disposed) return false;
                string temp = _shardPath + ".tmp";
                try
                {
                    string json = Render(_store);
                    if (Encoding.UTF8.GetByteCount(json) > FactStoreCapacity.MaxJournalBytes)
                    {
                        // Pressure is REPORTED, not resolved by overwriting
                        // something we cannot safely compact.
                        _store.NotePersistence(PersistenceStatus.Failed,
                            "the fact store is larger than its disk allowance, so it was not written");
                        return false;
                    }

                    Directory.CreateDirectory(_directory);
                    File.WriteAllText(temp, json, new UTF8Encoding(false));

                    if (File.Exists(_shardPath))
                        File.Replace(temp, _shardPath, _shardPath + ".last-good", ignoreMetadataErrors: true);
                    else
                        File.Move(temp, _shardPath);

                    _store.NotePersistence(PersistenceStatus.UpToDate);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _store.NotePersistence(PersistenceStatus.Failed,
                        "the fact store could not be written: " + ex.Message);
                    Tracing.TraceLine("FactJournal: write failed — " + ex.Message, TraceLevel.Warning);
                    return false;
                }
            }
        }

        /// <summary>
        /// Load every shard nobody holds, as history.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A shard a live process holds is skipped rather than read, because a
        /// half-written snapshot is not history. Records that will not parse
        /// produce a <b>reachable recovery-gap item</b> rather than an empty
        /// healthy-looking list — an empty list after a corrupt file is the
        /// worst possible answer, because it looks exactly like nothing having
        /// gone wrong.
        /// </para>
        /// </remarks>
        public int LoadHistory()
        {
            if (!Directory.Exists(_directory)) return 0;

            int restored = 0, gaps = 0;
            foreach (string path in Directory.EnumerateFiles(_directory, "facts-*.json"))
            {
                if (string.Equals(path, _shardPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(path + ".lease") && IsHeld(path + ".lease")) continue;

                try
                {
                    foreach (Fact fact in ReadShard(File.ReadAllText(path)))
                    {
                        _store.Restore(fact);
                        restored++;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    gaps++;
                    Tracing.TraceLine(
                        "FactJournal: '" + Path.GetFileName(path) + "' could not be read — " + ex.Message,
                        TraceLevel.Warning);
                }
            }

            if (gaps > 0)
            {
                // Operator-facing, so the words live in the lexicon.
                _store.NotePersistence(PersistenceStatus.RecoveryGap,
                    Lexicon.Get("facts.storage.recovery_gap", ("count", gaps)));
            }
            return restored;
        }

        private static bool IsHeld(string leasePath)
        {
            try
            {
                using var probe = new FileStream(leasePath, FileMode.Open, FileAccess.Read, FileShare.None);
                return false;   // we got it, so nobody holds it
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
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

        // ────────────────────────────────────────────────────────────────
        //  The format
        // ────────────────────────────────────────────────────────────────

        internal static string Render(FactStore store)
        {
            var options = new JsonWriterOptions { Indented = true };
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, options))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schema", SchemaVersion);
                writer.WriteNumber("processIncarnation", store.ProcessIncarnation);
                writer.WriteStartArray("facts");

                foreach (Fact fact in store.All)
                {
                    writer.WriteStartObject();
                    writer.WriteString("episode", fact.Identity.EpisodeId);
                    writer.WriteNumber("connection", fact.Identity.ConnectionIncarnation);
                    writer.WriteString("radio", fact.Identity.RadioIdentity);
                    writer.WriteString("slot", fact.Identity.ConditionSlot);
                    writer.WriteString("occurrence", fact.Identity.OccurrenceId);
                    writer.WriteString("key", fact.MessageKey);
                    writer.WriteString("classification", fact.Classification.ToString());
                    if (fact.Delivery != null)
                    {
                        writer.WriteString("shelfLife", fact.Delivery.ShelfLife.ToString());
                        writer.WriteString("validityContract", fact.Delivery.Validity);
                        writer.WriteString("historyKey", fact.Delivery.HistoryKey);
                        writer.WriteString("receiptPolicy", fact.Delivery.Receipt.ToString());
                    }
                    writer.WriteString("validity", fact.Validity.State.ToString());
                    writer.WriteString("validityDetail", fact.Validity.ToString());
                    writer.WriteString("observedUtc",
                        fact.Provenance.ObservedUtc.ToString("o", CultureInfo.InvariantCulture));
                    writer.WriteNumber("revision", fact.Revision);
                    writer.WriteNumber("materialRevision", fact.MaterialRevision);
                    writer.WriteNumber("reviewedMaterialRevision", fact.ReviewedMaterialRevision);
                    writer.WriteString("receipt", fact.Receipt.ToString());
                    writer.WriteBoolean("paused", fact.AutomaticPaused);
                    writer.WriteBoolean("undelivered", fact.HasUndeliveredDetail);
                    writer.WriteNumber("attempts", fact.Attempts.Count + fact.CompactedAttempts);
                    writer.WriteString("detail", fact.Detail);
                    writer.WriteBoolean("detailTruncated", fact.DetailTruncated);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                writer.WriteStartObject("overflow");
                writer.WriteNumber("lost", store.Overflow.LostCount);
                writer.WriteBoolean("saturated", store.Overflow.Saturated);
                writer.WriteEndObject();

                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        internal static IReadOnlyList<Fact> ReadShard(string json)
        {
            var result = new List<Fact>();
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("not an object");
            if (!root.TryGetProperty("schema", out JsonElement schema) || schema.GetInt32() != SchemaVersion)
            {
                throw new JsonException(
                    "this file was written by a different version of the fact store, so it is not " +
                    "read rather than guessed at");
            }

            if (!root.TryGetProperty("facts", out JsonElement facts)) return result;

            foreach (JsonElement element in facts.EnumerateArray())
            {
                string slot = Text(element, "slot");
                string occurrence = Text(element, "occurrence");
                if (slot.Length == 0 || occurrence.Length == 0) continue;

                var identity = new FactIdentity(
                    0,                                    // a restored record belongs to no live process
                    Number(element, "connection"),
                    element.TryGetProperty("radio", out JsonElement radio)
                        && radio.ValueKind == JsonValueKind.String ? radio.GetString() : null,
                    slot, occurrence);

                // Restored records are HISTORY and UNKNOWN. A loaded file can
                // never assert that a condition is current, whatever it said
                // when it was written.
                var validity = ValiditySnapshot.NotKnown(
                    UnknownReason.DataUnavailable,
                    DateTime.UtcNow,
                    "this was saved before the application last closed; whether it is still true now "
                    + "is not known");

                var provenance = new FactProvenance(
                    Number(element, "connection"), 0, ParseTime(Text(element, "observedUtc")),
                    "restored from an earlier session", observationComplete: false);

                var fact = new Fact(
                    identity, Text(element, "key"), null,
                    DeliveryClassification.Unclassified, validity, provenance,
                    admittedAtQuietGeneration: 0);
                fact.ApplyDetail(Text(element, "detail"));
                fact.RestoredFromDisk = true;
                result.Add(fact);
            }

            return result;
        }

        private static string Text(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static long Number(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt64()
                : 0;

        private static DateTime ParseTime(string text)
            => DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime when)
                ? when
                : DateTime.MinValue;
    }
}
