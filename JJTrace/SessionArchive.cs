using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;

namespace JJTrace
{
    /// <summary>
    /// Per-session trace archive operations: compress one session's trace file
    /// into a date-organized archive directory, append to manifest, and prune
    /// older archives. Per project_trace_persistence_design.md.
    ///
    /// Storage layout: archiveRootDir / yyyy / MM / trace-yyyyMMdd-HHmmss-outcome.zip
    /// Manifest: archiveRootDir / manifest.json
    ///
    /// Compression: zip-format archive with LZMA-compressed entries via SharpCompress.
    /// Roughly 25% smaller archives than Deflate at SmallestSize on text traces, which
    /// matters for heavy debug sessions where trace files can run several MB. Pure-
    /// managed library, no native deps. The .zip extension is preserved so the file
    /// is universally recognized as an archive; users may need 7-Zip rather than
    /// Windows Explorer's built-in handler to extract the LZMA-compressed entry,
    /// but most ham tester audiences already have 7-Zip.
    /// </summary>
    public static class SessionArchive
    {
        public const string ManifestFileName = "manifest.json";
        public const int DefaultRetentionDays = 30;

        /// <summary>
        /// Largest source trace this will compress whole. Above it, only the
        /// tail is archived (see <see cref="OversizedTailBytes"/>).
        ///
        /// With rotation on, a live trace can't reach this — parts cap at 256 MB.
        /// It exists for the traces rotation can't help: files written by an
        /// older build, a run with rotation disabled, or a rotation that kept
        /// failing. The 2026-08-07 session left an 11.7 GB JJFlexRadioTrace.txt,
        /// and archiving threw on it, which meant the source was never renamed
        /// out of the way and the day's evidence stayed a single unusable blob.
        /// A truncated archive is worth immeasurably more than a failed one.
        /// </summary>
        public const long MaxWholeFileArchiveBytes = 1L * 1024 * 1024 * 1024;

        /// <summary>
        /// How much of an oversized trace's tail to keep. The tail is where the
        /// failure is; 64 MB is a deep scrollback that still compresses in
        /// seconds rather than minutes.
        /// </summary>
        public const long OversizedTailBytes = 64L * 1024 * 1024;

        /// <summary>
        /// Archive a single trace file: compress to per-session zip, append manifest
        /// entry, optionally delete the source trace file. Returns the relative
        /// archive filename (yyyy/MM/...) on success, null on failure.
        /// </summary>
        /// <param name="archiveRootDir">Root archive directory (typically %AppData%\JJFlexRadio\Traces).</param>
        /// <param name="traceFilePath">Source trace file to archive.</param>
        /// <param name="session">Session metadata; outcome and key events get folded into manifest.</param>
        /// <param name="deleteSourceAfter">If true, delete the source trace file after successful archive.</param>
        /// <param name="partNumber">
        /// 1-based part number when this is one part of a rotated session; 0 for a
        /// whole-session archive. Parts of one session share the session's boot
        /// stamp and a frozen outcome tag so they sort together as a sequence.
        /// </param>
        /// <param name="isFinalPart">True when this is the last part of a chain.</param>
        public static string ArchiveSession(string archiveRootDir, string traceFilePath, TraceSession session, bool deleteSourceAfter,
                                            int partNumber = 0, bool isFinalPart = false)
        {
            if (string.IsNullOrEmpty(traceFilePath) || !File.Exists(traceFilePath))
            {
                return null;
            }
            if (session == null)
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(archiveRootDir);

                DateTime stamp = session.BootTimeUtc.ToLocalTime();
                string yearDir = Path.Combine(archiveRootDir, stamp.ToString("yyyy", CultureInfo.InvariantCulture));
                string monthDir = Path.Combine(yearDir, stamp.ToString("MM", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(monthDir);

                // Parts use the tag frozen at the first rotation so every part of
                // one session shares a stem; whole-session archives use the live
                // outcome exactly as before.
                string outcomeTag = SanitizeFileTag(partNumber > 0 ? session.ResolvePartFileTag() : session.Outcome);
                string partSuffix = partNumber > 0
                    ? string.Format(CultureInfo.InvariantCulture, "-part-{0:D3}", partNumber)
                    : string.Empty;

                string baseName = string.Format(CultureInfo.InvariantCulture, "trace-{0:yyyyMMdd-HHmmss}-{1}{2}.zip", stamp, outcomeTag, partSuffix);
                string fullPath = Path.Combine(monthDir, baseName);

                int suffix = 1;
                while (File.Exists(fullPath))
                {
                    baseName = string.Format(CultureInfo.InvariantCulture, "trace-{0:yyyyMMdd-HHmmss}-{1}{2}-{3}.zip", stamp, outcomeTag, partSuffix, suffix);
                    fullPath = Path.Combine(monthDir, baseName);
                    suffix++;
                }

                long sourceBytes = new FileInfo(traceFilePath).Length;
                bool truncated = sourceBytes > MaxWholeFileArchiveBytes;
                long archivedBytes = truncated ? OversizedTailBytes : sourceBytes;
                string traceFileNameInZip = Path.GetFileName(traceFilePath);

                WriterOptions writerOptions = new WriterOptions(CompressionType.LZMA);
                using (FileStream fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
                // SharpCompress 0.50 renamed the factory entry points:
                // WriterFactory.Open -> OpenWriter, ReaderFactory.Open -> OpenReader.
                using (IWriter writer = WriterFactory.OpenWriter(fs, ArchiveType.Zip, writerOptions))
                {
                    if (truncated)
                    {
                        WriteTailEntry(writer, traceFilePath, traceFileNameInZip, sourceBytes, OversizedTailBytes);
                    }
                    else
                    {
                        // FileShare.ReadWrite: the live trace is open for writing
                        // by the rotating listener when a crash-path archive runs.
                        using (FileStream src = new FileStream(traceFilePath, FileMode.Open, FileAccess.Read,
                                                               FileShare.ReadWrite | FileShare.Delete))
                        {
                            writer.Write(traceFileNameInZip, src, null);
                        }
                    }
                }

                long compressed = new FileInfo(fullPath).Length;

                string relativeFilename = Path.Combine(stamp.ToString("yyyy", CultureInfo.InvariantCulture), stamp.ToString("MM", CultureInfo.InvariantCulture), baseName)
                    .Replace(Path.DirectorySeparatorChar, '/');

                TraceSessionEntry entry = session.ToManifestEntry(relativeFilename, compressed, archivedBytes);
                entry.SourceName = traceFileNameInZip;
                if (partNumber > 0)
                {
                    entry.PartNumber = partNumber;
                    if (isFinalPart) entry.PartFinal = true;
                    // A part's end_time/duration describe the whole session, not
                    // the part. Only the final part gets to claim the session
                    // ended; intermediate parts would otherwise each look like a
                    // complete session in duration queries.
                    if (!isFinalPart)
                    {
                        entry.EndTime = null;
                        entry.DurationMs = null;
                    }
                }
                if (truncated) entry.Truncated = true;

                string manifestPath = Path.Combine(archiveRootDir, ManifestFileName);
                TraceManifest.Mutate(manifestPath, m => { m.Entries.Add(entry); return true; });

                if (deleteSourceAfter)
                {
                    try { File.Delete(traceFilePath); }
                    catch (Exception ex) { Tracing.ErrTraceOnly(ex); }
                }

                return relativeFilename;
            }
            catch (Exception ex)
            {
                // Trace-only. A failure here used to raise a modal MessageBox
                // carrying the raw framework text — which is how "couldn't save
                // a stream of that size" reached the user with no explanation
                // and no action to take.
                Tracing.ErrTraceOnly(ex);
                return null;
            }
        }

        /// <summary>
        /// Compress a ticket's detached source, publish it, and commit its
        /// manifest entry — in that order, and each phase reported truthfully.
        ///
        /// <para><b>Nothing here runs under the trace boundary.</b> The ticket
        /// arrived because the boundary had already closed the file and moved it
        /// to a path no future writer knows, so this can take as long as LZMA
        /// takes without a single trace line in the process waiting on it.</para>
        ///
        /// <para><b>Write to a temporary name, publish, then commit.</b> If the
        /// compression fails half way, no partial zip appears under a name the
        /// manifest might later claim. If the manifest commit fails, the zip is
        /// left and the pending record is KEPT, so the next boot finishes the
        /// job rather than leaving an archive nothing indexes. The raw source is
        /// never deleted to make room, and a failure never rolls the live
        /// session backwards.</para>
        /// </summary>
        public static TraceArchiveCompletion ArchiveTicket(TraceArchiveTicket ticket)
        {
            var completion = new TraceArchiveCompletion
            {
                TicketId = ticket?.TicketId ?? Guid.Empty,
                RawPath = ticket?.SourcePath,
            };
            if (ticket == null) { completion.FailureStage = "ticket"; return completion; }

            completion.RawRetained = TraceArchiveWorker.SafeExists(ticket.SourcePath);

            if (string.IsNullOrEmpty(ticket.ArchiveRootDir))
            {
                // No archive root configured. The raw part stays as plain
                // text — bounded and readable — which is still far better than
                // nothing, and the pending record is released because no
                // amount of retrying will produce an archive.
                completion.FailureStage = "no_archive_root";
                TraceArchiveWorker.ReleasePendingRecord(ticket.SourcePath);
                return completion;
            }
            if (!completion.RawRetained)
            {
                completion.FailureStage = "source_missing";
                TraceArchiveWorker.ReleasePendingRecord(ticket.SourcePath);
                return completion;
            }

            string tempPath = null;
            try
            {
                DateTime stamp = ticket.StampLocal;
                string monthDir = Path.Combine(ticket.ArchiveRootDir,
                    stamp.ToString("yyyy", CultureInfo.InvariantCulture),
                    stamp.ToString("MM", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(monthDir);

                string outcomeTag = SanitizeFileTag(ticket.OutcomeFileTag);
                string partSuffix = ticket.PartNumber > 0
                    ? string.Format(CultureInfo.InvariantCulture, "-part-{0:D3}", ticket.PartNumber)
                    : string.Empty;

                string baseName = string.Format(CultureInfo.InvariantCulture,
                    "trace-{0:yyyyMMdd-HHmmss}-{1}{2}.zip", stamp, outcomeTag, partSuffix);
                string fullPath = Path.Combine(monthDir, baseName);
                int suffix = 1;
                while (File.Exists(fullPath))
                {
                    baseName = string.Format(CultureInfo.InvariantCulture,
                        "trace-{0:yyyyMMdd-HHmmss}-{1}{2}-{3}.zip", stamp, outcomeTag, partSuffix, suffix);
                    fullPath = Path.Combine(monthDir, baseName);
                    suffix++;
                }

                tempPath = fullPath + ".writing";
                long sourceBytes = new FileInfo(ticket.SourcePath).Length;
                bool truncated = sourceBytes > MaxWholeFileArchiveBytes;
                long archivedBytes = truncated ? OversizedTailBytes : sourceBytes;
                string nameInZip = Path.GetFileName(ticket.SourcePath);

                using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (IWriter writer = WriterFactory.OpenWriter(fs, ArchiveType.Zip,
                                                                 new WriterOptions(CompressionType.LZMA)))
                {
                    if (truncated)
                    {
                        WriteTailEntry(writer, ticket.SourcePath, nameInZip, sourceBytes, OversizedTailBytes);
                    }
                    else
                    {
                        using (FileStream src = new FileStream(ticket.SourcePath, FileMode.Open, FileAccess.Read,
                                                               FileShare.ReadWrite | FileShare.Delete))
                        {
                            writer.Write(nameInZip, src, null);
                        }
                    }
                }

                File.Move(tempPath, fullPath);
                tempPath = null;

                long compressed = new FileInfo(fullPath).Length;
                string relativeFilename = Path.Combine(
                        stamp.ToString("yyyy", CultureInfo.InvariantCulture),
                        stamp.ToString("MM", CultureInfo.InvariantCulture), baseName)
                    .Replace(Path.DirectorySeparatorChar, '/');

                TraceSessionEntry entry = ticket.Entry ?? new TraceSessionEntry();
                entry.Filename = relativeFilename;
                entry.SourceName = nameInZip;
                entry.TraceSizeCompressedBytes = compressed;
                entry.TraceSizeUncompressedBytes = archivedBytes;
                if (ticket.PartNumber > 0)
                {
                    entry.PartNumber = ticket.PartNumber;
                    if (ticket.IsFinalPart) entry.PartFinal = true;
                }
                if (truncated) entry.Truncated = true;

                string manifestPath = Path.Combine(ticket.ArchiveRootDir, ManifestFileName);
                bool committed = TraceManifest.Mutate(manifestPath, m =>
                {
                    // Deduplicate by session and part: recovery can re-queue a
                    // ticket whose manifest commit actually succeeded before the
                    // process ended.
                    foreach (TraceSessionEntry existing in m.Entries)
                    {
                        if (existing == null) continue;
                        if (string.Equals(existing.SourceName, entry.SourceName, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                    m.Entries.Add(entry);
                    return true;
                });

                completion.ArchiveFullPath = fullPath;
                completion.ArchiveRelativeName = relativeFilename;
                completion.ArchiveCommitted = committed;
                completion.RawRetained = TraceArchiveWorker.SafeExists(ticket.SourcePath);
                if (!committed)
                {
                    // Keep the pending record: the zip exists but nothing indexes
                    // it, and saying "committed" here is exactly the lie the
                    // manifest's silent Save made possible.
                    completion.FailureStage = "manifest";
                    return completion;
                }

                TraceArchiveWorker.ReleasePendingRecord(ticket.SourcePath);
                return completion;
            }
            catch (Exception ex)
            {
                Tracing.ErrTraceOnly(ex);
                completion.ArchiveCommitted = false;
                completion.FailureStage = completion.FailureStage ?? "compress";
                completion.FailureMessage = ex.Message;
                completion.RawRetained = TraceArchiveWorker.SafeExists(ticket.SourcePath);
                try { if (tempPath != null && File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                return completion;
            }
        }

        /// <summary>
        /// Write the last <paramref name="tailBytes"/> of an oversized trace into
        /// the archive, starting at a line boundary, plus a plain-text notice
        /// entry saying what was dropped. Screen-reader friendly: the notice is a
        /// separate readable file, not a banner buried in the trace text.
        /// </summary>
        private static void WriteTailEntry(IWriter writer, string traceFilePath, string entryName, long sourceBytes, long tailBytes)
        {
            using (FileStream src = new FileStream(traceFilePath, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, sourceBytes - tailBytes);
                src.Seek(start, SeekOrigin.Begin);
                if (start > 0) SkipToLineStart(src);

                long actualTail = sourceBytes - src.Position;
                string notice =
                    "This trace was too large to archive whole." + Environment.NewLine +
                    "Original size: " + FormatBytes(sourceBytes) + Environment.NewLine +
                    "Kept: the last " + FormatBytes(actualTail) + " of " + entryName + Environment.NewLine +
                    "Everything before that point was not archived." + Environment.NewLine +
                    "A trace this size means rotation was off or failing; see the" + Environment.NewLine +
                    "rotation lines in the trace itself." + Environment.NewLine;

                using (MemoryStream noticeStream = new MemoryStream(Encoding.UTF8.GetBytes(notice)))
                {
                    writer.Write("TRUNCATED-NOTICE.txt", noticeStream, null);
                }

                writer.Write(entryName, src, null);
            }
        }

        /// <summary>Advance past the remainder of a partial line.</summary>
        private static void SkipToLineStart(Stream s)
        {
            int b;
            long guard = 0;
            const long maxScan = 1024 * 1024; // one line should never be a megabyte
            while ((b = s.ReadByte()) >= 0)
            {
                if (b == '\n') return;
                if (++guard > maxScan) return;
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " bytes";
            if (bytes < 1024 * 1024) return (bytes / 1024) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024 * 1024)) + " MB";
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", bytes / (1024.0 * 1024 * 1024));
        }

        /// <summary>
        /// Extract the (single) trace text entry from a per-session archive zip
        /// to a destination directory and return the extracted file path.
        /// Returns null if the archive is missing or empty. The Archive Browser
        /// uses this for "View Trace" — Windows Explorer can't open LZMA-compressed
        /// zip entries natively, so we extract via SharpCompress and hand the
        /// resulting plain-text file to the OS default association.
        /// </summary>
        public static string ExtractTraceText(string archiveFullPath, string destDir)
        {
            if (string.IsNullOrEmpty(archiveFullPath) || !File.Exists(archiveFullPath)) return null;
            try
            {
                Directory.CreateDirectory(destDir);
                using (FileStream stream = File.OpenRead(archiveFullPath))
                // 0.50 requires the options argument that used to be optional.
                using (IReader reader = ReaderFactory.OpenReader(stream, new ReaderOptions()))
                {
                    while (reader.MoveToNextEntry())
                    {
                        if (reader.Entry.IsDirectory) continue;
                        string entryName = Path.GetFileName(reader.Entry.Key) ?? "trace.txt";
                        string outPath = Path.Combine(destDir, entryName);
                        using (FileStream fs = File.Create(outPath))
                        {
                            reader.WriteEntryTo(fs);
                        }
                        return outPath;
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.ErrTraceOnly(ex);
            }
            return null;
        }

        /// <summary>
        /// Delete a set of archive entries from disk and the manifest. Returns the
        /// number of entries successfully deleted. Safe to call with relative
        /// filenames that no longer exist — those rows are removed from the
        /// manifest regardless. Used by the Archive Browser's Delete Selected.
        /// </summary>
        public static int DeleteEntries(string archiveRootDir, IEnumerable<string> relativeFilenames)
        {
            if (relativeFilenames == null) return 0;
            int deleted = 0;
            try
            {
                HashSet<string> toDelete = new HashSet<string>(
                    relativeFilenames, StringComparer.OrdinalIgnoreCase);
                if (toDelete.Count == 0) return 0;

                string manifestPath = Path.Combine(archiveRootDir, ManifestFileName);
                TraceManifest.Mutate(manifestPath, manifest =>
                {
                    bool changed = false;
                    for (int i = manifest.Entries.Count - 1; i >= 0; i--)
                    {
                        TraceSessionEntry entry = manifest.Entries[i];
                        if (entry == null || string.IsNullOrEmpty(entry.Filename)) continue;
                        if (!toDelete.Contains(entry.Filename)) continue;

                        string fullPath = Path.Combine(archiveRootDir, entry.Filename.Replace('/', Path.DirectorySeparatorChar));
                        try { if (File.Exists(fullPath)) File.Delete(fullPath); }
                        catch (Exception ex) { Tracing.ErrTraceOnly(ex); }
                        DeleteLateEvidenceBeside(fullPath);

                        manifest.Entries.RemoveAt(i);
                        changed = true;
                        deleted++;
                    }
                    return changed;
                });
            }
            catch (Exception ex)
            {
                Tracing.ErrTraceOnly(ex);
            }
            return deleted;
        }

        /// <summary>
        /// True when the manifest already has an entry produced from a plain-text
        /// trace of this file name. Boot maintenance uses it to spot part files
        /// left behind by a run that died before its background compression
        /// finished — those get archived rather than pruned away unread.
        /// </summary>
        /// <summary>
        /// Every source file name the manifest archives, for a sweep that has
        /// to ask the question once per file without loading the manifest once
        /// per file. Null when the manifest exists but cannot be read — a
        /// caller treats that as "nothing is archived", which keeps evidence
        /// rather than deleting it.
        /// </summary>
        public static HashSet<string> ArchivedSourceNames(string archiveRootDir)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(archiveRootDir)) return names;
            try
            {
                string manifestPath = Path.Combine(archiveRootDir, ManifestFileName);
                if (!File.Exists(manifestPath)) return names;
                TraceManifest manifest = TraceManifest.Load(manifestPath);
                if (manifest?.Entries == null) return names;
                foreach (TraceSessionEntry e in manifest.Entries)
                {
                    if (e != null && !string.IsNullOrEmpty(e.SourceName)) names.Add(e.SourceName);
                }
                return names;
            }
            catch (Exception ex)
            {
                Tracing.ErrTraceOnly(ex);
                return null;
            }
        }

        public static bool IsSourceArchived(string archiveRootDir, string sourceName)
        {
            if (string.IsNullOrEmpty(sourceName)) return false;
            try
            {
                string manifestPath = Path.Combine(archiveRootDir, ManifestFileName);
                if (!File.Exists(manifestPath)) return false;
                TraceManifest manifest = TraceManifest.Load(manifestPath);
                if (manifest?.Entries == null) return false;
                return manifest.Entries.Any(e =>
                    e != null &&
                    string.Equals(e.SourceName, sourceName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Tracing.ErrTraceOnly(ex);
                return false;
            }
        }

        /// <summary>
        /// Reconcile manifest with disk: remove manifest entries whose archive file
        /// is missing, and (optionally) detect orphan archive files not referenced
        /// in the manifest. Idempotent — safe to call at every boot.
        /// </summary>
        public static void Reconcile(string archiveRootDir)
        {
            if (!Directory.Exists(archiveRootDir)) return;

            try
            {
                string manifestPath = Path.Combine(archiveRootDir, ManifestFileName);
                TraceManifest.Mutate(manifestPath, manifest =>
                {
                    bool changed = false;
                    for (int i = manifest.Entries.Count - 1; i >= 0; i--)
                    {
                        TraceSessionEntry entry = manifest.Entries[i];
                        if (string.IsNullOrEmpty(entry.Filename)) { manifest.Entries.RemoveAt(i); changed = true; continue; }
                        string fullPath = Path.Combine(archiveRootDir, entry.Filename.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(fullPath))
                        {
                            manifest.Entries.RemoveAt(i);
                            changed = true;
                        }
                    }
                    return changed;
                });
            }
            catch (Exception ex)
            {
                Tracing.ErrTraceOnly(ex);
            }
        }

        // ── Late evidence: lines about a sealed session, kept beside it ────

        /// <summary>
        /// The suffix of the plain-text file that holds lines about a session
        /// which arrived after another operation had sealed it — a refused
        /// drop's partial meter window above all (Sol's review of H9, blocker
        /// 3). It sits BESIDE the session's archive, named after it, and is
        /// never written into the sealed file or its zip.
        /// </summary>
        public const string LateEvidenceSuffix = ".late-evidence.txt";

        /// <summary>
        /// Where late evidence for the session archived at, or retained at,
        /// <paramref name="archiveOrRawPath"/> lives: the zip's name with
        /// <see cref="LateEvidenceSuffix"/> in place of <c>.zip</c>, or the
        /// raw file's name with the suffix appended. Neither matches
        /// <c>trace-*.zip</c>, so the problem-report bundle's walk copies it
        /// and the trace browser does not list it as a session.
        /// </summary>
        public static string LateEvidencePathFor(string archiveOrRawPath)
        {
            if (string.IsNullOrEmpty(archiveOrRawPath)) return null;
            return archiveOrRawPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? archiveOrRawPath.Substring(0, archiveOrRawPath.Length - 4) + LateEvidenceSuffix
                : archiveOrRawPath + LateEvidenceSuffix;
        }

        /// <summary>Delete the late-evidence file beside an archive that is
        /// being deleted, if there is one. Best effort; the archive's own
        /// deletion is the operation, this is tidying after it.</summary>
        private static void DeleteLateEvidenceBeside(string archiveFullPath)
        {
            try
            {
                string side = LateEvidencePathFor(archiveFullPath);
                if (side != null && File.Exists(side)) File.Delete(side);
            }
            catch (Exception ex) { Tracing.ErrTraceOnly(ex); }
        }

        /// <summary>
        /// Auto-prune: delete archive files older than <paramref name="retentionDays"/>
        /// (per their boot_time) and remove their manifest entries. KeptForever
        /// entries are exempt regardless of age.
        /// </summary>
        public static int PruneOlderThan(string archiveRootDir, int retentionDays)
        {
            if (retentionDays <= 0) return 0;
            if (!Directory.Exists(archiveRootDir)) return 0;

            int pruned = 0;
            try
            {
                string manifestPath = Path.Combine(archiveRootDir, ManifestFileName);
                DateTime cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);

                TraceManifest.Mutate(manifestPath, manifest =>
                {
                    for (int i = manifest.Entries.Count - 1; i >= 0; i--)
                    {
                        TraceSessionEntry entry = manifest.Entries[i];
                        if (entry.KeptForever) continue;
                        if (entry.BootTime > cutoffUtc) continue;

                        string fullPath = null;
                        if (!string.IsNullOrEmpty(entry.Filename))
                        {
                            fullPath = Path.Combine(archiveRootDir, entry.Filename.Replace('/', Path.DirectorySeparatorChar));
                            // Evidence somebody is in the middle of bundling is
                            // not evidence to age out from underneath them.
                            if (TraceEvidencePins.IsPinned(fullPath)) continue;
                            try { if (File.Exists(fullPath)) File.Delete(fullPath); }
                            catch (Exception ex) { Tracing.ErrTraceOnly(ex); }
                            DeleteLateEvidenceBeside(fullPath);
                        }
                        manifest.Entries.RemoveAt(i);
                        pruned++;
                    }
                    return pruned > 0;
                });

                if (pruned > 0)
                {
                    PruneEmptyDateDirs(archiveRootDir);
                }
            }
            catch (Exception ex)
            {
                Tracing.ErrTraceOnly(ex);
            }
            return pruned;
        }

        /// <summary>
        /// Remove empty year/month subdirectories left behind after prune. Best-effort
        /// — failures swallowed to avoid disturbing the user-facing paths.
        /// </summary>
        private static void PruneEmptyDateDirs(string archiveRootDir)
        {
            try
            {
                foreach (string yearDir in Directory.GetDirectories(archiveRootDir))
                {
                    foreach (string monthDir in Directory.GetDirectories(yearDir))
                    {
                        if (!Directory.EnumerateFileSystemEntries(monthDir).Any())
                        {
                            try { Directory.Delete(monthDir); } catch { }
                        }
                    }
                    if (!Directory.EnumerateFileSystemEntries(yearDir).Any())
                    {
                        try { Directory.Delete(yearDir); } catch { }
                    }
                }
            }
            catch { }
        }

        private static string SanitizeFileTag(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return TraceSessionOutcome.Unknown;
            char[] cleaned = new char[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                cleaned[i] = (char.IsLetterOrDigit(c) || c == '_' || c == '-') ? c : '_';
            }
            return new string(cleaned);
        }
    }
}
