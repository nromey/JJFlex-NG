using System;
using System.Globalization;
using System.IO;
using System.Linq;
using SharpCompress.Common;
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
        /// Archive a single trace file: compress to per-session zip, append manifest
        /// entry, optionally delete the source trace file. Returns the relative
        /// archive filename (yyyy/MM/...) on success, null on failure.
        /// </summary>
        /// <param name="archiveRootDir">Root archive directory (typically %AppData%\JJFlexRadio\Traces).</param>
        /// <param name="traceFilePath">Source trace file to archive.</param>
        /// <param name="session">Session metadata; outcome and key events get folded into manifest.</param>
        /// <param name="deleteSourceAfter">If true, delete the source trace file after successful archive.</param>
        public static string ArchiveSession(string archiveRootDir, string traceFilePath, TraceSession session, bool deleteSourceAfter)
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

                string outcomeTag = SanitizeFileTag(session.Outcome);
                string baseName = string.Format(CultureInfo.InvariantCulture, "trace-{0:yyyyMMdd-HHmmss}-{1}.zip", stamp, outcomeTag);
                string fullPath = Path.Combine(monthDir, baseName);

                int suffix = 1;
                while (File.Exists(fullPath))
                {
                    baseName = string.Format(CultureInfo.InvariantCulture, "trace-{0:yyyyMMdd-HHmmss}-{1}-{2}.zip", stamp, outcomeTag, suffix);
                    fullPath = Path.Combine(monthDir, baseName);
                    suffix++;
                }

                long uncompressed = new FileInfo(traceFilePath).Length;
                string traceFileNameInZip = Path.GetFileName(traceFilePath);

                WriterOptions writerOptions = new WriterOptions(CompressionType.LZMA);
                using (FileStream fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (IWriter writer = WriterFactory.Open(fs, ArchiveType.Zip, writerOptions))
                {
                    writer.Write(traceFileNameInZip, traceFilePath);
                }

                long compressed = new FileInfo(fullPath).Length;

                string relativeFilename = Path.Combine(stamp.ToString("yyyy", CultureInfo.InvariantCulture), stamp.ToString("MM", CultureInfo.InvariantCulture), baseName)
                    .Replace(Path.DirectorySeparatorChar, '/');

                TraceSessionEntry entry = session.ToManifestEntry(relativeFilename, compressed, uncompressed);

                string manifestPath = Path.Combine(archiveRootDir, ManifestFileName);
                TraceManifest manifest = TraceManifest.Load(manifestPath);
                manifest.Entries.Add(entry);
                manifest.Save(manifestPath);

                if (deleteSourceAfter)
                {
                    try { File.Delete(traceFilePath); }
                    catch (Exception ex) { Tracing.ErrMessageTrace(ex); }
                }

                return relativeFilename;
            }
            catch (Exception ex)
            {
                Tracing.ErrMessageTrace(ex);
                return null;
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
                TraceManifest manifest = TraceManifest.Load(manifestPath);
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
                if (changed)
                {
                    manifest.Save(manifestPath);
                }
            }
            catch (Exception ex)
            {
                Tracing.ErrMessageTrace(ex);
            }
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
                TraceManifest manifest = TraceManifest.Load(manifestPath);
                DateTime cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);

                for (int i = manifest.Entries.Count - 1; i >= 0; i--)
                {
                    TraceSessionEntry entry = manifest.Entries[i];
                    if (entry.KeptForever) continue;
                    if (entry.BootTime > cutoffUtc) continue;

                    if (!string.IsNullOrEmpty(entry.Filename))
                    {
                        string fullPath = Path.Combine(archiveRootDir, entry.Filename.Replace('/', Path.DirectorySeparatorChar));
                        try { if (File.Exists(fullPath)) File.Delete(fullPath); }
                        catch (Exception ex) { Tracing.ErrMessageTrace(ex); }
                    }
                    manifest.Entries.RemoveAt(i);
                    pruned++;
                }

                if (pruned > 0)
                {
                    manifest.Save(manifestPath);
                    PruneEmptyDateDirs(archiveRootDir);
                }
            }
            catch (Exception ex)
            {
                Tracing.ErrMessageTrace(ex);
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
