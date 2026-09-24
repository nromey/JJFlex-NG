using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace JJTrace
{
    /// <summary>
    /// A small durable record written beside a detached raw trace, before any
    /// successor is published.
    ///
    /// <para>It exists because compression moved OUT of the teardown. That is
    /// the right trade — a lock held across LZMA while a radio is dying is
    /// worse than anything this file costs — but it means a process can end
    /// between "the bytes were detached" and "the archive was committed", and
    /// the raw file would then sit in AppData with no manifest entry, waiting
    /// for the plain-text sweep to delete unread evidence. This record is how
    /// the next boot recognises it, retries it without inventing a new session,
    /// and deduplicates by session and part.</para>
    /// </summary>
    internal sealed class TracePendingRecord
    {
        [JsonPropertyName("session_id")] public string SessionId { get; set; }
        [JsonPropertyName("part_number")] public int PartNumber { get; set; }
        [JsonPropertyName("part_final")] public bool IsFinalPart { get; set; }
        [JsonPropertyName("checkpoint")] public bool IsCheckpoint { get; set; }
        [JsonPropertyName("source_path")] public string SourcePath { get; set; }
        [JsonPropertyName("archive_root")] public string ArchiveRootDir { get; set; }
        [JsonPropertyName("outcome_file_tag")] public string OutcomeFileTag { get; set; }
        [JsonPropertyName("stamp_local")] public DateTime StampLocal { get; set; }
        [JsonPropertyName("entry")] public TraceSessionEntry Entry { get; set; }
    }

    /// <summary>
    /// One serialized worker for every archive this process makes — rotation
    /// parts and sealed sessions alike.
    ///
    /// <para><b>Why one, and why serial.</b> LZMA on a 256 MB text file is
    /// minutes of CPU; a marathon session can close several parts and then seal
    /// at exit. Running them concurrently would put several of those in flight
    /// at once during teardown. Running them on SEPARATE chains — which is what
    /// the code did until now, rotation on one and the final file inline on the
    /// caller's thread — let a part's manifest write and the final file's
    /// manifest write interleave and lose each other's entry.</para>
    /// </summary>
    public static class TraceArchiveWorker
    {
        private const string PendingSuffix = ".trace-pending.json";

        private static readonly object _chainLock = new object();
        private static Task _chain = Task.CompletedTask;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>Hand a detached file to the worker. Returns at once.</summary>
        internal static Task<TraceArchiveCompletion> Queue(TraceArchiveTicket ticket)
        {
            if (ticket == null) return Task.FromResult<TraceArchiveCompletion>(null);

            var tcs = ticket.CompletionSource;
            if (tcs == null)
            {
                tcs = new TaskCompletionSource<TraceArchiveCompletion>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                ticket.CompletionSource = tcs;
                ticket.Completion = tcs.Task;
            }

            lock (_chainLock)
            {
                _chain = _chain.ContinueWith(_ =>
                {
                    TraceArchiveCompletion completion;
                    try
                    {
                        completion = SessionArchive.ArchiveTicket(ticket);
                    }
                    catch (Exception ex)
                    {
                        Tracing.ErrTraceOnly(ex);
                        completion = new TraceArchiveCompletion
                        {
                            TicketId = ticket.TicketId,
                            ArchiveCommitted = false,
                            RawRetained = SafeExists(ticket.SourcePath),
                            RawPath = ticket.SourcePath,
                            FailureStage = "worker",
                            FailureMessage = ex.Message,
                        };
                    }
                    try { tcs.TrySetResult(completion); } catch { }
                }, TaskScheduler.Default);
            }
            return tcs.Task;
        }

        /// <summary>
        /// Block until the queue drains, up to <paramref name="budget"/>.
        /// Never throws. False means work is still pending — its raw files and
        /// records are intact and the next boot will finish them.
        /// </summary>
        internal static bool Drain(TimeSpan budget)
        {
            Task chain;
            lock (_chainLock) { chain = _chain; }
            try { return chain.Wait(budget); }
            catch { return false; }
        }

        /// <summary>Tests only.</summary>
        internal static void ResetChainForTests()
        {
            lock (_chainLock) { _chain = Task.CompletedTask; }
        }

        // ── Pending records ────────────────────────────────────────────────

        internal static string PendingPathFor(string sourcePath)
            => string.IsNullOrEmpty(sourcePath) ? null : sourcePath + PendingSuffix;

        /// <summary>
        /// Write the durable record. Failure is recorded as a fault rather than
        /// thrown: a missing record costs a retry at next boot, and refusing to
        /// seal because a sidecar would not write would cost the whole session.
        /// </summary>
        internal static void WritePendingRecord(TraceArchiveTicket ticket, List<string> faults)
        {
            if (ticket == null || string.IsNullOrEmpty(ticket.SourcePath)) return;
            try
            {
                var record = new TracePendingRecord
                {
                    SessionId = ticket.SessionId.ToString(),
                    PartNumber = ticket.PartNumber,
                    IsFinalPart = ticket.IsFinalPart,
                    IsCheckpoint = ticket.IsCheckpoint,
                    SourcePath = ticket.SourcePath,
                    ArchiveRootDir = ticket.ArchiveRootDir,
                    OutcomeFileTag = ticket.OutcomeFileTag,
                    StampLocal = ticket.StampLocal,
                    Entry = ticket.Entry,
                };
                File.WriteAllText(PendingPathFor(ticket.SourcePath),
                                  JsonSerializer.Serialize(record, JsonOptions));
            }
            catch (Exception ex)
            {
                faults?.Add("TraceCoordinator: could not write the pending archive record for "
                            + ticket.SourcePath + ": " + ex.Message);
            }
        }

        internal static void ReleasePendingRecord(string sourcePath)
        {
            string path = PendingPathFor(sourcePath);
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Tracing.ErrTraceOnly(ex); }
        }

        /// <summary>
        /// True when a plain-text file is somebody's pending archive work, so
        /// the retention sweep and the problem-report bundle both leave it
        /// alone. Recovery has to see it before pruning does.
        /// </summary>
        public static bool IsPendingWork(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            if (fullPath.EndsWith(PendingSuffix, StringComparison.OrdinalIgnoreCase)) return true;
            try { return File.Exists(PendingPathFor(fullPath)); }
            catch { return false; }
        }

        /// <summary>
        /// Finish the archives a previous run detached and never committed.
        /// Called at boot, BEFORE the plain-text pruning pass. Idempotent:
        /// a record whose session and part are already in the manifest is
        /// released rather than archived again.
        /// </summary>
        /// <returns>How many pending records were found.</returns>
        public static int RecoverPending(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return 0;
            int found = 0;
            try
            {
                if (!Directory.Exists(directory)) return 0;
                foreach (string recordPath in Directory.GetFiles(directory, "*" + PendingSuffix))
                {
                    TracePendingRecord record;
                    try
                    {
                        record = JsonSerializer.Deserialize<TracePendingRecord>(
                            File.ReadAllText(recordPath), JsonOptions);
                    }
                    catch (Exception ex)
                    {
                        Tracing.ErrTraceOnly(ex);
                        continue;
                    }
                    if (record == null || string.IsNullOrEmpty(record.SourcePath)) continue;
                    found++;

                    if (!File.Exists(record.SourcePath))
                    {
                        // The source is gone: either it was archived and the
                        // record outlived it, or somebody deleted it. Either
                        // way there is nothing left to compress.
                        try { File.Delete(recordPath); } catch { }
                        continue;
                    }

                    Guid.TryParse(record.SessionId, out Guid sessionId);
                    var ticket = new TraceArchiveTicket
                    {
                        SessionId = sessionId,
                        PartNumber = record.PartNumber,
                        IsFinalPart = record.IsFinalPart,
                        IsCheckpoint = record.IsCheckpoint,
                        SourcePath = record.SourcePath,
                        ArchiveRootDir = record.ArchiveRootDir,
                        Entry = record.Entry,
                        OutcomeFileTag = record.OutcomeFileTag,
                        StampLocal = record.StampLocal,
                    };
                    Queue(ticket);
                }
            }
            catch (Exception ex)
            {
                Tracing.ErrTraceOnly(ex);
            }
            return found;
        }

        /// <summary>
        /// Does this detached file still exist? Asked on the failure paths,
        /// where the answer is the difference between "the evidence is
        /// retained" and "the evidence is gone" — so it must never throw on the
        /// way to reporting.
        ///
        /// <para>One copy, shared with <see cref="SessionArchive"/>. It was
        /// written twice and the integration pass caught it: two identical
        /// private helpers do not conflict, do not fail to build and both work,
        /// right up until one of them is corrected.</para>
        /// </summary>
        internal static bool SafeExists(string path)
        {
            try { return !string.IsNullOrEmpty(path) && File.Exists(path); }
            catch { return false; }
        }

        internal static string FormatStamp(DateTime local, string format)
            => local.ToString(format, CultureInfo.InvariantCulture);
    }
}
