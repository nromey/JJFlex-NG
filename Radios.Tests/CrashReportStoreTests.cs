using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #181: crash reports nobody has answered wait in a list, and retention
    /// follows the operator's setting, whose default (Noel, 2026-11-07 20:24)
    /// removes only the memory dump from an older unanswered report. Run in a
    /// temporary folder with made-up bundles: no crash, no network, never the
    /// operator's settings.
    /// </summary>
    // In the settings-root collection since repair one: its sentence tests
    // read the Lexicon, which resolves the process-wide settings root.
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class CrashReportStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjflex-crashstore-" + Guid.NewGuid().ToString("N"));

        public CrashReportStoreTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Bundle(string stamp, bool withDump, DateTime writtenUtc)
        {
            string path = Path.Combine(_dir, "JJFlexError-" + stamp + ".zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Add(zip, "JJFlexError-" + stamp + ".txt", "the report");
                if (withDump) Add(zip, "JJFlexError-" + stamp + ".dmp", new string('d', 200_000));
                Add(zip, "trace-current-part.txt", "the trace");
                Add(zip, "traces/older.zip", "archived");
                Add(zip, "bundle-manifest.txt", "manifest");
            }
            File.SetLastWriteTimeUtc(path, writtenUtc);
            return path;
        }

        private static void Add(ZipArchive zip, string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
            w.Write(text);
        }

        private static string[] Entries(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray();
        }

        // ------------------------------------------------------------------
        // The decision
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(UnresolvedCrashRetention.StripDumpAfterGrace, true, CrashRetentionAction.StripDump)]
        [InlineData(UnresolvedCrashRetention.StripDumpAfterGrace, false, CrashRetentionAction.Keep)]
        [InlineData(UnresolvedCrashRetention.KeepWhole, true, CrashRetentionAction.Keep)]
        [InlineData(UnresolvedCrashRetention.DeleteAfterGrace, true, CrashRetentionAction.Delete)]
        public void An_older_unanswered_report_past_its_grace_follows_the_setting(
            UnresolvedCrashRetention policy, bool hasDump, CrashRetentionAction expected)
        {
            Assert.Equal(expected, CrashReportStore.Decide(newestIndex: 4, keepNewest: 3, answered: false,
                tooOld: true, overCap: true, pastGrace: true, hasDump: hasDump, policy));
        }

        [Fact]
        public void The_newest_stay_whole_and_the_unanswered_are_kept_inside_their_grace()
        {
            foreach (UnresolvedCrashRetention policy in Enum.GetValues(typeof(UnresolvedCrashRetention)))
            {
                Assert.Equal(CrashRetentionAction.Keep, CrashReportStore.Decide(3, 3, false, true, true, true, true, policy));
                Assert.Equal(CrashRetentionAction.Keep, CrashReportStore.Decide(9, 3, false, true, true, false, true, policy));
            }
        }

        [Fact]
        public void An_answered_report_goes_past_the_age_window_or_the_cap_as_before()
        {
            Assert.Equal(CrashRetentionAction.Delete, CrashReportStore.Decide(4, 3, true, true, false, true, true, default));
            Assert.Equal(CrashRetentionAction.Delete, CrashReportStore.Decide(4, 3, true, false, true, false, true, default));
            Assert.Equal(CrashRetentionAction.Keep, CrashReportStore.Decide(4, 3, true, false, false, true, true, default));
        }

        [Fact]
        public void The_default_setting_is_the_ruled_one_and_an_unknown_value_falls_back_to_it()
        {
            Assert.Equal(UnresolvedCrashRetention.StripDumpAfterGrace, (UnresolvedCrashRetention)new DiagnosticsConfig().UnresolvedCrashRetention);
            Assert.Equal(UnresolvedCrashRetention.StripDumpAfterGrace, CrashReportStore.PolicyFrom(7));
            Assert.Equal(UnresolvedCrashRetention.KeepWhole, CrashReportStore.PolicyFrom(1));
        }

        // ------------------------------------------------------------------
        // Removing the dump, safely
        // ------------------------------------------------------------------

        [Fact]
        public void Stripping_removes_only_the_dump_marks_it_and_keeps_the_bundles_date()
        {
            var written = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            string path = Bundle("20261001-120000", withDump: true, written);
            long before = new FileInfo(path).Length;

            long freed = CrashReportStore.StripDump(path, new DateTime(2026, 11, 7, 20, 30, 0));

            Assert.True(freed > 0);
            Assert.True(new FileInfo(path).Length < before);
            var names = Entries(path);
            Assert.DoesNotContain(names, n => n.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("JJFlexError-20261001-120000.txt", names);
            Assert.Contains("trace-current-part.txt", names);
            Assert.Contains("traces/older.zip", names);
            Assert.Contains(CrashReportStore.DumpRemovedEntry, names);
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
            Assert.False(File.Exists(path + ".stripping"));

            var (hasDump, removed) = CrashReportStore.ReadDumpState(path);
            Assert.False(hasDump);
            Assert.Equal(new DateTime(2026, 11, 7), removed!.Value.Date);
        }

        [Fact]
        public void A_bundle_with_no_dump_is_left_exactly_as_it_was()
        {
            string path = Bundle("20261002-120000", withDump: false, new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc));
            byte[] before = File.ReadAllBytes(path);
            Assert.Equal(-1, CrashReportStore.StripDump(path, DateTime.Now));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".stripping"));
        }

        [Fact]
        public void A_bundle_that_cannot_be_read_is_never_left_half_written()
        {
            string path = Path.Combine(_dir, "JJFlexError-20261003-120000.zip");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("not a zip at all"));
            byte[] before = File.ReadAllBytes(path);
            Assert.Equal(-1, CrashReportStore.StripDump(path, DateTime.Now));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".stripping"));
        }

        // ------------------------------------------------------------------
        // The list
        // ------------------------------------------------------------------

        [Fact]
        public void The_list_holds_the_unanswered_reports_newest_first_and_no_upload_copies()
        {
            string older = Bundle("20261001-120000", true, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
            string newer = Bundle("20261005-120000", true, new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc));
            string answered = Bundle("20261006-120000", true, new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));
            Assert.True(CrashReportStore.Dismiss(answered));
            File.WriteAllText(Path.Combine(_dir, "JJFlexError-20261005-120000-upload.zip"), "leftover");
            CrashReportStore.StripDump(older, DateTime.Now);

            var rows = CrashReportStore.Pending(_dir);

            Assert.Equal(new[] { newer, older }, rows.Select(r => r.Path).ToArray());
            Assert.True(rows[0].HasDump);
            Assert.Null(rows[0].DumpRemoved);
            Assert.False(rows[1].HasDump);
            Assert.NotNull(rows[1].DumpRemoved);
            Assert.True(CrashReportStore.IsAnswered(answered));
        }

        [Fact]
        public void Each_row_is_one_sentence_that_says_whether_the_dump_is_still_there()
        {
            var when = new DateTime(2026, 10, 5, 12, 0, 0);
            string with = CrashReportStore.RowSentence(new CrashReportRow("p", when, 500, true, null), b => b + " bytes");
            string gone = CrashReportStore.RowSentence(new CrashReportRow("p", when, 500, false, new DateTime(2026, 11, 7)), b => b + " bytes");
            Assert.Contains("500 bytes", with);
            Assert.Contains("memory dump", with);
            Assert.Contains("removed", gone);
            Assert.NotEqual(with, gone);
            Assert.DoesNotContain("{", with + gone);   // every slot filled
        }

        [Fact]
        public void The_lists_name_says_how_many_and_never_as_a_position()
        {
            Assert.Contains("none", CrashReportStore.ListName(0), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2", CrashReportStore.ListName(2));
            Assert.DoesNotMatch(@"\b\d+\s+of\s+\d+\b", CrashReportStore.ListName(2));
        }

        [Fact]
        public void An_empty_or_missing_folder_lists_nothing()
        {
            Assert.Empty(CrashReportStore.Pending(Path.Combine(_dir, "nope")));
            Assert.Empty(CrashReportStore.Pending(null));
        }

        // ------------------------------------------------------------------
        // The crash reporter's side, from source (it is VB in the app)
        // ------------------------------------------------------------------

        private static string Reporter() =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "CrashReporter.vb"));

        [Fact]
        public void A_crash_that_ends_the_app_saves_its_report_and_asks_nothing()
        {
            string src = Reporter();
            int save = src.IndexOf("Private Sub SaveCrash(", StringComparison.Ordinal);
            int gate = src.IndexOf("If isTerminating Then", save, StringComparison.Ordinal);
            int prompt = src.IndexOf("PromptToUploadCrashBundle(zipPath, bundle)", save, StringComparison.Ordinal);
            Assert.True(gate > save && prompt > gate, "the terminating gate must come before the question");
            string between = src.Substring(gate, prompt - gate);
            Assert.Contains("\"logging.crash.saved_send_later\"", between);
            Assert.Contains("Return", between);
        }

        [Fact]
        public void Retention_uses_the_tested_decision_and_names_a_removed_dump_in_the_log()
        {
            string src = Reporter();
            int prune = src.IndexOf("Friend Sub PruneCrashReports()", StringComparison.Ordinal);
            string body = src.Substring(prune, src.IndexOf("End Sub", prune, StringComparison.Ordinal) - prune);
            Assert.Contains("Radios.CrashReportStore.Decide(", body);
            Assert.Contains("Radios.CrashReportStore.StripDump(fi.FullName", body);
            Assert.Contains("removed the memory dump from UNRESOLVED crash report {fi.Name}", body);
            Assert.Contains("removed UNRESOLVED crash report {fi.Name}", body);   // positive control: the old log line stays
        }

        [Fact]
        public void Sending_from_the_list_asks_first_and_a_no_there_answers_nothing()
        {
            string src = Reporter();
            int offer = src.IndexOf("Friend Sub OfferToSendCrashReport(", StringComparison.Ordinal);
            Assert.True(offer >= 0);
            string body = src.Substring(offer, src.IndexOf("End Sub", offer, StringComparison.Ordinal) - offer);
            Assert.Contains("PromptToUploadCrashBundle(zipPath, bundle, noMeansDismiss:=False)", body);
            Assert.DoesNotContain("UploadCrashBundleAsync", body);   // the upload only ever follows the Yes
            Assert.Contains("ElseIf Not noMeansDismiss Then", src);
        }

        // ------------------------------------------------------------------
        // Repair two: sending from the list does not announce a new error,
        // and the dump-removed note's time agrees with its marker
        // ------------------------------------------------------------------

        [Fact]
        public void Sending_from_the_list_opens_with_the_reports_own_date_and_never_says_a_new_error_happened()
        {
            var saved = new DateTime(2026, 11, 1, 9, 15, 0, DateTimeKind.Local);
            string[] included = { "JJFlexError-20261101-091500.txt (2 KB)", "trace-current-part.txt (40 KB)" };
            string fromList = CrashReportStore.SendQuestionText(@"C:\x\JJFlexError-20261101-091500.zip", "42 KB",
                included, Array.Empty<string>(), oversize: false, "https://example.invalid/crashes", saved);

            Assert.DoesNotContain(Lexicon.Get("logging.crash.prompt_intro"), fromList);
            Assert.DoesNotContain("unexpected error", fromList);
            Assert.StartsWith(Lexicon.Get("logging.crash.prompt_intro_existing",
                ("when", saved.ToString("f", System.Globalization.CultureInfo.CurrentCulture))), fromList);
            // Everything else the question says is still said.
            Assert.Contains(included[0], fromList);
            Assert.Contains(Lexicon.Get("logging.crash.prompt_send_question"), fromList);
            Assert.Contains("https://example.invalid/crashes", fromList);

            // The control: at the crash itself the window still opens with the error.
            string atCrash = CrashReportStore.SendQuestionText(@"C:\x\a.zip", "42 KB",
                included, new[] { "held back" }, oversize: true, "https://example.invalid/crashes", null);
            Assert.StartsWith(Lexicon.Get("logging.crash.prompt_intro"), atCrash);
            Assert.Contains(Lexicon.Get("logging.crash.prompt_not_included"), atCrash);
            Assert.Contains("held back", atCrash);
        }

        [Fact]
        public void The_send_window_takes_its_words_from_that_builder_with_the_reports_date_only_from_the_list()
        {
            string src = Reporter();
            int prompt = src.IndexOf("Private Sub PromptToUploadCrashBundle(", StringComparison.Ordinal);
            string body = src.Substring(prompt, src.IndexOf("End Sub", prompt, StringComparison.Ordinal) - prompt);
            Assert.Contains("Radios.CrashReportStore.SendQuestionText(", body);
            Assert.Contains("If(noMeansDismiss, CType(Nothing, DateTime?), File.GetLastWriteTime(zipPath))", body);
            Assert.DoesNotContain("\"logging.crash.prompt_intro\"", src);   // only the builder opens the window
            Assert.Contains("MessageBoxButtons.YesNo, MessageBoxIcon.Question))", body);   // the list asks; no error sound
        }

        private static string RemovedNote(string zipPath)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            using var r = new StreamReader(zip.GetEntry(CrashReportStore.DumpRemovedEntry)!.Open());
            return r.ReadToEnd();
        }

        [Fact]
        public void The_dump_removed_note_writes_local_time_with_its_offset_and_never_a_UTC_marker_on_it()
        {
            var local = new DateTime(2026, 11, 7, 20, 30, 0, DateTimeKind.Local);
            string path = Bundle("20261001-120000", withDump: true, DateTime.UtcNow.AddDays(-30));
            Assert.True(CrashReportStore.StripDump(path, local) > 0);

            string note = RemovedNote(path);
            TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(local);
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            string expected = "2026-11-07 20:30:00 " + sign + offset.Duration().ToString(@"hh\:mm");
            Assert.Contains(expected, note);
            Assert.DoesNotContain("20:30:00Z", note);

            // A UTC time says so with +00:00, at its own clock reading.
            var utc = new DateTime(2026, 11, 8, 2, 30, 0, DateTimeKind.Utc);
            Assert.Equal("2026-11-08 02:30:00 +00:00", CrashReportStore.RemovedStamp(utc));
        }

        // ------------------------------------------------------------------
        // Repair one: an answer leaves the list when it is recorded, and
        // every retention sentence follows the setting
        // ------------------------------------------------------------------

        [Fact]
        public void A_send_that_succeeds_later_takes_the_report_off_the_list_and_says_so_when_it_does()
        {
            var now = DateTime.UtcNow;
            string zip = Bundle("20261101-100000", withDump: true, now.AddDays(-1));
            Assert.Single(CrashReportStore.Pending(_dir));

            int heard = 0;
            int pendingWhenHeard = -1;
            void Listener() { heard++; pendingWhenHeard = CrashReportStore.Pending(_dir).Count; }
            CrashReportStore.VerdictRecorded += Listener;
            try
            {
                // The upload's own thread, some time after the press (the
                // tab's immediate refresh has long since run).
                var send = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(50);
                    return CrashReportStore.RecordVerdict(zip, "sent");
                });
                Assert.True(send.Wait(5000) && send.Result);
            }
            finally { CrashReportStore.VerdictRecorded -= Listener; }

            Assert.True(heard >= 1, "nothing told the Diagnostics tab the report was answered");
            Assert.Equal(0, pendingWhenHeard);                 // by the time it is told, the list has it gone
            Assert.StartsWith("sent ", File.ReadAllText(zip + CrashReportStore.VerdictSuffix));
        }

        [Fact]
        public void A_failed_send_or_a_not_now_records_nothing_and_the_report_stays_waiting()
        {
            string zip = Bundle("20261101-110000", withDump: true, DateTime.UtcNow.AddDays(-1));
            int heard = 0;
            void Listener() => heard++;
            CrashReportStore.VerdictRecorded += Listener;
            try
            {
                // No verdict is written on a failure or a "not now"; the one
                // writer is the only road to the event.
                Assert.False(CrashReportStore.RecordVerdict("", "sent"));
            }
            finally { CrashReportStore.VerdictRecorded -= Listener; }
            Assert.Equal(0, heard);
            Assert.Single(CrashReportStore.Pending(_dir));
            Assert.False(CrashReportStore.IsAnswered(zip));
        }

        [Fact]
        public void A_dismiss_is_heard_the_same_way()
        {
            string zip = Bundle("20261101-120000", withDump: false, DateTime.UtcNow.AddDays(-1));
            int heard = 0;
            void Listener() => heard++;
            CrashReportStore.VerdictRecorded += Listener;
            try { Assert.True(CrashReportStore.Dismiss(zip)); }
            finally { CrashReportStore.VerdictRecorded -= Listener; }
            Assert.True(heard >= 1);
            Assert.Empty(CrashReportStore.Pending(_dir));
        }

        private static string Describe(UnresolvedCrashRetention policy, int unresolved = 2) =>
            CrashReportStore.DescribeReports(bundles: 5, totalBytes: 1_500_000_000, unresolved: unresolved, keepNewest: 3,
                policy, b => b + " bytes");

        [Fact]
        public void Measure_now_says_what_retention_does_under_each_setting_and_never_contradicts_it()
        {
            string keep = Describe(UnresolvedCrashRetention.KeepWhole);
            string strip = Describe(UnresolvedCrashRetention.StripDumpAfterGrace);
            string delete = Describe(UnresolvedCrashRetention.DeleteAfterGrace);

            foreach (string s in new[] { keep, strip, delete })
            {
                Assert.StartsWith(Lexicon.Get("logging.crash.reports_head_many", ("count", 5), ("size", "1500000000 bytes")), s);
                Assert.Contains("2 have not been sent or dismissed", s);
                Assert.Contains("newest 3", s);
                Assert.DoesNotContain("{", s);
            }
            Assert.Contains("never removed automatically", keep);
            Assert.Contains("memory dump", strip);
            Assert.Contains(CrashReportStore.UnresolvedGraceDays + " days", strip);
            Assert.Contains("deleted", delete);
            Assert.Contains(CrashReportStore.UnresolvedGraceDays + " days", delete);
            // The contradiction Codex found: under the two settings that act
            // on an older unanswered report, nothing may say it is never removed.
            Assert.DoesNotContain("never removed", strip);
            Assert.DoesNotContain("never removed", delete);
            Assert.DoesNotContain("kept for you", delete);
            Assert.Equal(3, new[] { keep, strip, delete }.Distinct().Count());

            Assert.Contains("1 has not been sent or dismissed", Describe(UnresolvedCrashRetention.DeleteAfterGrace, unresolved: 1));
            Assert.Equal(Lexicon.Get("logging.crash.reports_none"),
                CrashReportStore.DescribeReports(0, 0, 0, 3, UnresolvedCrashRetention.DeleteAfterGrace, b => ""));
        }

        [Fact]
        public void The_disk_space_note_follows_the_setting_too()
        {
            string keep = CrashReportStore.DiskNote(UnresolvedCrashRetention.KeepWhole);
            string strip = CrashReportStore.DiskNote(UnresolvedCrashRetention.StripDumpAfterGrace);
            string delete = CrashReportStore.DiskNote(UnresolvedCrashRetention.DeleteAfterGrace);
            Assert.Contains("never deleted automatically", keep);
            Assert.Contains("memory dump", strip);
            Assert.DoesNotContain("never deleted", strip);
            Assert.Contains("deleted once it is " + CrashReportStore.UnresolvedGraceDays + " days old", delete);
            Assert.DoesNotContain("never deleted", delete);
            foreach (string s in new[] { keep, strip, delete })
                Assert.StartsWith("Deleting loose log text does not touch", s);
        }

        [Fact]
        public void The_crash_reporter_records_through_the_one_writer_and_describes_through_the_tested_sentence()
        {
            string src = Reporter();
            int rec = src.IndexOf("Private Sub RecordCrashVerdict(", StringComparison.Ordinal);
            string body = src.Substring(rec, src.IndexOf("End Sub", rec, StringComparison.Ordinal) - rec);
            Assert.Contains("Radios.CrashReportStore.RecordVerdict(zipPath, verdict)", body);
            Assert.DoesNotContain("File.WriteAllText", body);
            int describe = src.IndexOf("Friend Function DescribeCrashReports()", StringComparison.Ordinal);
            string d = src.Substring(describe, src.IndexOf("End Function", describe, StringComparison.Ordinal) - describe);
            Assert.Contains("Radios.CrashReportStore.PolicyFrom(DiagnosticsSettings.UnresolvedCrashRetention)", d);
            Assert.Contains("Radios.CrashReportStore.DescribeReports(", d);
            Assert.DoesNotContain("\"logging.crash.reports_tail\"", d);
            Assert.Contains("Private Const UnresolvedCrashGraceDays As Integer = Radios.CrashReportStore.UnresolvedGraceDays", src);
        }

        [Fact]
        public void The_bundle_carries_the_spoken_transcript_when_one_is_being_written()
        {
            string src = Reporter();
            Assert.Contains("Radios.OutputChannelRecorder.TranscriptPath", src);
            Assert.Contains("\"spoken-transcript.jsonl\"", src);
        }
    }
}
