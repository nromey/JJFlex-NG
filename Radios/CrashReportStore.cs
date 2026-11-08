#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Radios
{
    /// <summary>
    /// What happens to an OLDER crash report (past the newest
    /// <see cref="DiagnosticsConfig.KeepCrashReports"/>) that has never been
    /// sent or dismissed, once it is past the grace window. Stored as an int in
    /// <see cref="DiagnosticsConfig.UnresolvedCrashRetention"/>.
    /// </summary>
    /// <remarks>
    /// Ruled by Noel 2026-11-07: J36 (06:44), <i>"Make it configurable with the
    /// default to keep everything"</i>, and at 20:24, once he knew that each
    /// bundle zip holds the report, the traces AND the 200-700 MB memory dump:
    /// the default strips the dump from an older unanswered report and keeps
    /// the small report and traces until it is sent or dismissed.
    /// </remarks>
    public enum UnresolvedCrashRetention
    {
        /// <summary>The default: the memory dump is removed from the zip; the report and traces stay until answered.</summary>
        StripDumpAfterGrace = 0,

        /// <summary>The whole bundle stays until it is sent or dismissed.</summary>
        KeepWhole = 1,

        /// <summary>The rule before the ruling: the whole bundle is deleted.</summary>
        DeleteAfterGrace = 2,
    }

    /// <summary>What retention does to one crash report.</summary>
    public enum CrashRetentionAction
    {
        Keep,
        Delete,
        StripDump,
    }

    /// <summary>One crash report nobody has sent or dismissed, as the Diagnostics tab lists it.</summary>
    public sealed record CrashReportRow(string Path, DateTime Written, long Bytes, bool HasDump, DateTime? DumpRemoved);

    /// <summary>
    /// The crash reports in the Errors folder, apart from the VB crash
    /// reporter that writes them, so the decisions can be tested with no
    /// crash and no network: which reports wait for an answer, how each is
    /// described, the retention decision, and removing a memory dump safely.
    /// </summary>
    /// <remarks>
    /// <para>A report is "answered" when its <see cref="VerdictSuffix"/> sidecar
    /// exists ("sent" or "dismissed"). The crash reporter writes it; so does
    /// <see cref="Dismiss"/>.</para>
    /// <para>Sending lives in the crash reporter, behind the same question the
    /// crash itself asks, and only on the operator's press.</para>
    /// </remarks>
    public static class CrashReportStore
    {
        /// <summary>The sidecar that records the operator's answer. Same suffix as the crash reporter's.</summary>
        public const string VerdictSuffix = ".verdict";

        /// <summary>The entry a stripped bundle carries, saying when its dump was removed.</summary>
        public const string DumpRemovedEntry = "dump-removed.txt";

        /// <summary>The bundles the crash reporter writes.</summary>
        public const string BundlePattern = "JJFlexError-*.zip";

        /// <summary>
        /// How long an older report nobody has sent or dismissed waits before
        /// the retention setting acts on it. One number for the pruner and
        /// every sentence that describes it.
        /// </summary>
        public const int UnresolvedGraceDays = 7;

        /// <summary>
        /// Raised, on whatever thread recorded it, when a report is answered:
        /// sent (when the receiver took it) or dismissed. The Diagnostics tab
        /// re-reads its list on it, so a report sent from the list leaves the
        /// list when the send succeeds, not at the next visit (#181 repair
        /// one, Codex B1). A failed send or a "not now" records nothing and
        /// raises nothing: the report stays waiting.
        /// </summary>
        public static event Action? VerdictRecorded;

        /// <summary>
        /// Write the operator's answer beside the bundle ("sent" or
        /// "dismissed"), then raise <see cref="VerdictRecorded"/>. False when
        /// it could not be written; then nothing is raised.
        /// </summary>
        public static bool RecordVerdict(string zipPath, string verdict)
        {
            try
            {
                if (string.IsNullOrEmpty(zipPath)) return false;
                File.WriteAllText(zipPath + VerdictSuffix,
                    verdict + " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + Environment.NewLine);
            }
            catch { return false; }   // not recording a verdict costs disk space, never evidence
            try { VerdictRecorded?.Invoke(); }
            catch { /* a listener's failure is not the verdict's */ }
            return true;
        }

        /// <summary>
        /// The retention decision for one bundle, newest first.
        /// </summary>
        /// <param name="newestIndex">1 for the newest bundle.</param>
        /// <param name="keepNewest">How many of the newest stay whole whatever else is true.</param>
        /// <param name="answered">Sent or dismissed.</param>
        /// <param name="tooOld">Past the age window.</param>
        /// <param name="overCap">Over the folder cap, counting the newer ones kept.</param>
        /// <param name="pastGrace">Older than the unresolved grace window.</param>
        /// <param name="hasDump">Still holds its memory dump.</param>
        /// <param name="policy">The operator's setting.</param>
        public static CrashRetentionAction Decide(int newestIndex, int keepNewest, bool answered, bool tooOld,
            bool overCap, bool pastGrace, bool hasDump, UnresolvedCrashRetention policy)
        {
            if (newestIndex <= Math.Max(1, keepNewest)) return CrashRetentionAction.Keep;
            if (answered) return tooOld || overCap ? CrashRetentionAction.Delete : CrashRetentionAction.Keep;
            if (!pastGrace) return CrashRetentionAction.Keep;
            return policy switch
            {
                UnresolvedCrashRetention.KeepWhole => CrashRetentionAction.Keep,
                UnresolvedCrashRetention.DeleteAfterGrace => CrashRetentionAction.Delete,
                _ => hasDump ? CrashRetentionAction.StripDump : CrashRetentionAction.Keep,
            };
        }

        /// <summary>A stored setting as the enum; anything unknown is the default.</summary>
        public static UnresolvedCrashRetention PolicyFrom(int stored) =>
            Enum.IsDefined(typeof(UnresolvedCrashRetention), stored)
                ? (UnresolvedCrashRetention)stored
                : UnresolvedCrashRetention.StripDumpAfterGrace;

        /// <summary>True when the operator has sent or dismissed this bundle.</summary>
        public static bool IsAnswered(string zipPath)
        {
            try { return File.Exists(zipPath + VerdictSuffix); }
            catch { return false; }
        }

        /// <summary>Record the operator's "dismiss": the report stays, and retention may reclaim it later.</summary>
        public static bool Dismiss(string zipPath)
        {
            try
            {
                if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath)) return false;
            }
            catch { return false; }
            return RecordVerdict(zipPath, "dismissed");
        }

        /// <summary>
        /// The reports waiting for an answer, newest first. Upload copies
        /// (<c>-upload.zip</c>) are a send's leftovers, not reports, and are
        /// left out. An unreadable bundle is still listed, by its date and
        /// size, because the operator can still send or dismiss it.
        /// </summary>
        public static IReadOnlyList<CrashReportRow> Pending(string? errorsDir)
        {
            var rows = new List<CrashReportRow>();
            try
            {
                if (string.IsNullOrEmpty(errorsDir) || !Directory.Exists(errorsDir)) return rows;
                foreach (var fi in new DirectoryInfo(errorsDir).GetFiles(BundlePattern)
                    .Where(f => !f.Name.EndsWith("-upload.zip", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    if (IsAnswered(fi.FullName)) continue;
                    var (hasDump, removed) = ReadDumpState(fi.FullName);
                    rows.Add(new CrashReportRow(fi.FullName, fi.LastWriteTime, fi.Length, hasDump, removed));
                }
            }
            catch { /* a folder that cannot be read lists nothing; the tab says so */ }
            return rows;
        }

        /// <summary>Whether a bundle holds a memory dump, and when one was removed from it.</summary>
        public static (bool HasDump, DateTime? Removed) ReadDumpState(string zipPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                bool hasDump = zip.Entries.Any(IsDump);
                var marker = zip.GetEntry(DumpRemovedEntry);
                return (hasDump, marker?.LastWriteTime.LocalDateTime);
            }
            catch { return (false, null); }
        }

        private static bool IsDump(ZipArchiveEntry e) =>
            e.FullName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/');

        /// <summary>
        /// One row as a sentence, the way the list reads it: when, how big,
        /// and whether the memory dump is still in it. DRAFT wording.
        /// </summary>
        public static string RowSentence(CrashReportRow row, Func<long, string> describeBytes)
        {
            string when = row.Written.ToString("f", CultureInfo.CurrentCulture);
            string size = describeBytes(row.Bytes);
            if (row.DumpRemoved is DateTime removed)
                return Lexicon.Get("logging.crash.row_dump_removed",
                    ("when", when), ("size", size), ("removed", removed.ToString("D", CultureInfo.CurrentCulture)));
            return Lexicon.Get(row.HasDump ? "logging.crash.row_with_dump" : "logging.crash.row_no_dump",
                ("when", when), ("size", size));
        }

        /// <summary>
        /// What the saved crash reports come to, as Measure now says it: how
        /// many and how big, how many wait for an answer, and what retention
        /// does with them under the operator's setting. Every policy has its
        /// own closing sentence (#181 repair one, Codex B2): the one sentence
        /// there was promised that an unanswered report is never removed,
        /// which "Delete the whole report after 7 days" made untrue. DRAFT
        /// wording for the two new policies and the reworded counts.
        /// </summary>
        public static string DescribeReports(int bundles, long totalBytes, int unresolved, int keepNewest,
            UnresolvedCrashRetention policy, Func<long, string> describeBytes)
        {
            if (bundles <= 0) return Lexicon.Get("logging.crash.reports_none");
            string size = describeBytes(totalBytes);
            string head = Lexicon.Get(bundles == 1 ? "logging.crash.reports_head_one" : "logging.crash.reports_head_many",
                ("count", bundles), ("size", size));
            string tail = policy switch
            {
                UnresolvedCrashRetention.KeepWhole => Lexicon.Get("logging.crash.reports_tail", ("keepNewest", keepNewest)),
                UnresolvedCrashRetention.DeleteAfterGrace => Lexicon.Get("logging.crash.reports_tail_delete",
                    ("keepNewest", keepNewest), ("graceDays", UnresolvedGraceDays)),
                _ => Lexicon.Get("logging.crash.reports_tail_strip_dump",
                    ("keepNewest", keepNewest), ("graceDays", UnresolvedGraceDays)),
            };
            if (unresolved > 0)
                tail = Lexicon.Get(unresolved == 1 ? "logging.crash.reports_unresolved_one" : "logging.crash.reports_unresolved_many",
                    ("count", unresolved), ("tail", tail));
            return head + tail;
        }

        /// <summary>
        /// The Disk space group's standing note, under the operator's setting:
        /// what deleting loose logs leaves alone, and what happens to an older
        /// report nobody has answered. DRAFT for the two new policies.
        /// </summary>
        public static string DiskNote(UnresolvedCrashRetention policy) => policy switch
        {
            UnresolvedCrashRetention.KeepWhole => Lexicon.Get("settings.diagnostics.disk_note_keep"),
            UnresolvedCrashRetention.DeleteAfterGrace => Lexicon.Get("settings.diagnostics.disk_note_delete", ("graceDays", UnresolvedGraceDays)),
            _ => Lexicon.Get("settings.diagnostics.disk_note_strip_dump", ("graceDays", UnresolvedGraceDays)),
        };

        /// <summary>The list's own name, with the count said so it cannot be heard as a position. DRAFT.</summary>
        public static string ListName(int count) => count <= 0
            ? Lexicon.Get("logging.crash.pending_list_none")
            : Lexicon.Get("logging.crash.pending_list_count", ("count", count));

        /// <summary>
        /// When a memory dump was removed, as the note inside the bundle says
        /// it: the operator's own clock with its offset from UTC, for example
        /// "2026-11-07 20:30:00 -06:00" (#181 repair two). The note used the
        /// "u" format, which appends a literal Z, the UTC marker, to whatever
        /// clock it is given; retention passes local time, so every note was
        /// a UTC claim on a local time, off by the whole offset. Local and not
        /// UTC because the note sits beside the crash report's own "When:"
        /// line, which is local with an offset for the same reason, and the
        /// person reading either compares it with their own clock; the offset
        /// keeps it exact for support. A UTC time is written with +00:00.
        /// </summary>
        public static string RemovedStamp(DateTime when) =>
            new DateTimeOffset(when).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

        /// <summary>
        /// The text of the window that asks whether to send a crash report:
        /// what it holds, what will be left out, and where it goes (#181
        /// repair two). <paramref name="savedOn"/> is null at the crash
        /// itself, which opens with "hit an unexpected error"; from the
        /// Diagnostics list it is the report's own date, and the window opens
        /// with that instead. An old report sent on purpose is not a new
        /// error, and saying it was told the operator something had just gone
        /// wrong. DRAFT wording for the list's opening sentence.
        /// </summary>
        public static string SendQuestionText(string zipPath, string sizeText,
            IEnumerable<string> included, IReadOnlyCollection<string> withheld,
            bool oversize, string endpoint, DateTime? savedOn)
        {
            var sb = new StringBuilder();
            sb.AppendLine(savedOn is DateTime saved
                ? Lexicon.Get("logging.crash.prompt_intro_existing",
                    ("when", saved.ToString("f", CultureInfo.CurrentCulture)))
                : Lexicon.Get("logging.crash.prompt_intro"));
            sb.AppendLine();
            sb.AppendLine(Lexicon.Get("logging.crash.prompt_saved_to"));
            sb.AppendLine(zipPath);
            sb.AppendLine();
            sb.AppendLine(Lexicon.Get("logging.crash.prompt_contains", ("size", sizeText)));
            foreach (string item in included)
                sb.AppendLine(Lexicon.Get("logging.crash.manifest_bullet", ("item", item)));
            if (withheld.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(Lexicon.Get("logging.crash.prompt_not_included"));
                foreach (string item in withheld)
                    sb.AppendLine(Lexicon.Get("logging.crash.manifest_bullet", ("item", item)));
            }
            sb.AppendLine();
            if (oversize)
            {
                // Honest, and stated before the user chooses, not after a
                // failed POST comes back with a raw server error.
                sb.AppendLine(Lexicon.Get("logging.crash.prompt_oversize", ("newline", Environment.NewLine)));
                sb.AppendLine();
            }
            sb.AppendLine(Lexicon.Get("logging.crash.prompt_send_question"));
            sb.AppendLine(Lexicon.Get("logging.crash.prompt_endpoint", ("endpoint", endpoint)));
            return sb.ToString();
        }

        /// <summary>
        /// Remove the memory dump from a bundle, keeping every other entry,
        /// and leave <see cref="DumpRemovedEntry"/> in its place. Safe: the new
        /// zip is written beside the old one and then swapped in, so a failure
        /// at any point leaves the original bundle exactly as it was, never a
        /// half zip. The bundle keeps its own date, so its place in retention
        /// order does not move.
        /// </summary>
        /// <returns>The bytes reclaimed, or -1 when nothing was changed.</returns>
        public static long StripDump(string zipPath, DateTime now)
        {
            string temp = zipPath + ".stripping";
            try
            {
                if (!File.Exists(zipPath)) return -1;
                var original = new FileInfo(zipPath);
                DateTime written = original.LastWriteTimeUtc;
                long before = original.Length;
                bool hadDump = false;
                if (File.Exists(temp)) File.Delete(temp);

                using (var src = ZipFile.OpenRead(zipPath))
                using (var destStream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var dest = new ZipArchive(destStream, ZipArchiveMode.Create))
                {
                    foreach (var entry in src.Entries)
                    {
                        if (IsDump(entry)) { hadDump = true; continue; }
                        if (string.Equals(entry.FullName, DumpRemovedEntry, StringComparison.OrdinalIgnoreCase)) continue;
                        var copy = dest.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                        copy.LastWriteTime = entry.LastWriteTime;
                        using var input = entry.Open();
                        using var output = copy.Open();
                        input.CopyTo(output);
                    }
                    if (!hadDump) return -1;   // nothing to remove; the temp is deleted below
                    var marker = dest.CreateEntry(DumpRemovedEntry, CompressionLevel.Optimal);
                    marker.LastWriteTime = new DateTimeOffset(now);
                    using var w = new StreamWriter(marker.Open(), new UTF8Encoding(false));
                    w.WriteLine(Lexicon.Get("logging.crash.dump_removed_note",
                        ("date", RemovedStamp(now))));
                }

                File.Replace(temp, zipPath, null);
                File.SetLastWriteTimeUtc(zipPath, written);
                return Math.Max(0, before - new FileInfo(zipPath).Length);
            }
            catch
            {
                return -1;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
