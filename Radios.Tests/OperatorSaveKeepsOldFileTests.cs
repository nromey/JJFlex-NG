#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Editing an operator so their file name changes (a new callsign or name)
    /// used to delete the old file BEFORE writing the new one, and the write
    /// swallows its own failure, so a failed write lost the operator's record
    /// (#1086). The old file now goes only after the save reports success.
    /// </summary>
    /// <remarks>
    /// Calls the factored-out save step, <c>PersonalData.SaveThenRetireOld</c>,
    /// from the built app assembly in a collectible context, as
    /// <see cref="OperatorStepCopyTests"/> does. <b>Never <c>PersonalData.Update</c></b>:
    /// it opens a WinForms dialog first. Files live in a temporary folder.
    /// Needs <c>JJFlexRadio.vbproj</c> built first and says so if it is not.
    /// </remarks>
    [Collection(BuiltAppAssemblyCollection.Name)]
    public sealed class OperatorSaveKeepsOldFileTests : IDisposable
    {
        // The settings root and its Operators folder, laid out as the app's
        // own, so a test can also load the folder as PersonalData (#1254).
        private readonly string _base = Path.Combine(Path.GetTempPath(), "jjf-opsave-" + Guid.NewGuid().ToString("N"));
        private string _dir => Path.Combine(_base, "Operators");

        public OperatorSaveKeepsOldFileTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try
            {
                foreach (string f in Directory.GetFiles(_base, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_base, recursive: true);
            }
            catch { }
        }

        /// <summary>A well-formed operator file's text, its inner name <paramref name="fileName"/>.</summary>
        private static string OperatorXml(string fileName, bool isDefault = false) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<personal_v1 xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\r\n"
            + "  <fileName>" + fileName + "</fileName>\r\n"
            + "  <DefaultFlag>" + (isDefault ? "true" : "false") + "</DefaultFlag>\r\n"
            + "  <callSign>TEST" + fileName.Length + "</callSign>\r\n"
            + "  <handl>" + Path.GetFileNameWithoutExtension(fileName) + "</handl>\r\n"
            + "  <fullName>" + Path.GetFileNameWithoutExtension(fileName) + " Operator</fullName>\r\n"
            + "</personal_v1>\r\n";

        /// <summary>Write a readable operator into <paramref name="path"/>, its inner name its own.</summary>
        private static void WriteOperator(string path, bool isDefault = false) =>
            File.WriteAllText(path, OperatorXml(Path.GetFileName(path), isDefault));

        private string FileIn(string name, string? text = null)
        {
            string path = Path.Combine(_dir, name);
            if (text != null) File.WriteAllText(path, text);
            return path;
        }

        [Fact]
        public void A_save_that_fails_keeps_the_old_file()
        {
            string old = FileIn("OLD_Operator.xml", "the operator's record");
            string renamed = FileIn("NEW_Operator.xml");
            bool saved = Run(old, renamed, () => false);
            Assert.False(saved);
            Assert.True(File.Exists(old), "a failed save deleted the operator's only record");
            Assert.Equal("the operator's record", File.ReadAllText(old));
        }

        [Fact]
        public void A_save_that_works_moves_the_operator_to_the_new_file()
        {
            string old = FileIn("OLD_Operator.xml", "the operator's record");
            string renamed = FileIn("NEW_Operator.xml");
            bool saved = Run(old, renamed, () => { File.WriteAllText(renamed, "edited"); return true; });
            Assert.True(saved);
            Assert.False(File.Exists(old), "the old file is left behind after a good save");
            Assert.Equal("edited", File.ReadAllText(renamed));
        }

        [Fact]
        public void The_old_file_is_still_there_while_the_new_one_is_written()
        {
            string old = FileIn("OLD_Operator.xml", "the operator's record");
            string renamed = FileIn("NEW_Operator.xml");
            bool oldPresentDuringSave = false;
            Run(old, renamed, () => { oldPresentDuringSave = File.Exists(old); File.WriteAllText(renamed, "edited"); return true; });
            Assert.True(oldPresentDuringSave, "the old file was removed before the new one was written");
        }

        [Fact]
        public void A_name_that_only_changed_case_keeps_the_file_just_written()
        {
            // On Windows these are one file: removing the "old" one would
            // remove the operator just saved.
            string old = FileIn("lower_Operator.xml", "the operator's record");
            string renamed = Path.Combine(_dir, "LOWER_Operator.xml");
            bool saved = Run(old, renamed, () => { File.WriteAllText(renamed, "edited"); return true; });
            Assert.True(saved);
            Assert.True(File.Exists(renamed));
            Assert.Equal("edited", File.ReadAllText(renamed));
        }

        [Fact]
        public void An_unchanged_name_is_saved_in_place()
        {
            string same = FileIn("SAME_Operator.xml", "before");
            bool saved = Run(same, same, () => { File.WriteAllText(same, "after"); return true; });
            Assert.True(saved);
            Assert.Equal("after", File.ReadAllText(same));
        }

        [Fact]
        public void Update_retires_the_old_file_through_the_save_step_and_the_write_reports_failure()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "PersonalData.vb")).Replace("\r\n", "\n");
            int start = src.IndexOf("Public Function Update(ByVal id As Integer) As Boolean", StringComparison.Ordinal);
            int end = src.IndexOf("End Function", start, StringComparison.Ordinal);
            string update = src.Substring(start, end - start);
            Assert.DoesNotContain("File.Delete", update, StringComparison.Ordinal);
            // #1175: the save goes through SaveThenReplace, which swaps the
            // entry only after the save succeeds. Changed from a direct
            // SaveThenRetireOld call that followed an in-memory swap.
            Assert.Contains("If SaveThenReplace(ops, id, theOp, oldPath, theOp.pathName, Function() TryWrite(theOp)) Then", update, StringComparison.Ordinal);
            Assert.DoesNotContain("ops.RemoveAt(id)", update, StringComparison.Ordinal);
            Assert.DoesNotContain("ops.Insert(id, theOp)", update, StringComparison.Ordinal);
            int save = update.IndexOf("If SaveThenReplace(", StringComparison.Ordinal);
            Assert.True(update.IndexOf("DefaultID = id", StringComparison.Ordinal) > save, "the default moves only after the save succeeded");
            Assert.True(update.IndexOf("reportEvent()", StringComparison.Ordinal) > save, "the change is reported only after the save succeeded");
            int tryWrite = src.IndexOf("Friend Function TryWrite(ByVal op As personal_v1) As Boolean", StringComparison.Ordinal);
            Assert.True(tryWrite > 0);
            string body = src.Substring(tryWrite, src.IndexOf("End Function", tryWrite, StringComparison.Ordinal) - tryWrite);
            int caught = body.IndexOf("Catch ex As Exception", StringComparison.Ordinal);
            Assert.True(caught > 0 && body.IndexOf("Return False", caught, StringComparison.Ordinal) > caught,
                "a write that fails must say so");
        }

        // ---- #1175: the session keeps the old entry when the save fails ----

        [Fact]
        public void A_save_that_fails_keeps_the_old_entry_in_memory_and_its_file_on_disk()
        {
            string old = FileIn("OLD_Operator.xml", "the operator's record");
            string renamed = FileIn("NEW_Operator.xml");
            var entries = new System.Collections.Generic.List<string> { "someone else", "OLD", "a third" };
            bool saved = Replace(entries, 1, "NEW", old, renamed, () => false);
            Assert.False(saved);
            Assert.Equal(new[] { "someone else", "OLD", "a third" }, entries);
            Assert.True(File.Exists(old), "a failed save deleted the operator's only record");
        }

        [Fact]
        public void A_save_that_works_replaces_the_entry_and_retires_the_old_file()
        {
            string old = FileIn("OLD_Operator.xml", "the operator's record");
            string renamed = FileIn("NEW_Operator.xml");
            var entries = new System.Collections.Generic.List<string> { "someone else", "OLD", "a third" };
            bool saved = Replace(entries, 1, "NEW", old, renamed, () => { File.WriteAllText(renamed, "edited"); return true; });
            Assert.True(saved);
            Assert.Equal(new[] { "someone else", "NEW", "a third" }, entries);
            Assert.False(File.Exists(old));
        }

        /// <summary>
        /// The route Codex described: a rename whose save fails, then a good
        /// save. With the entry swapped before the failed save, the good save
        /// took the new path as the old one and left the old file behind, so
        /// both files loaded at the next start.
        /// </summary>
        [Fact]
        public void A_failed_rename_then_a_good_save_leaves_one_operator_file()
        {
            string old = FileIn("OLD_Operator.xml", "the operator's record");
            string renamed = FileIn("NEW_Operator.xml");
            var paths = new System.Collections.Generic.List<string> { old };   // the session's idea of where the operator lives

            Assert.False(Replace(paths, 0, renamed, paths[0], renamed, () => false));
            Assert.True(Replace(paths, 0, renamed, paths[0], renamed, () => { File.WriteAllText(renamed, "edited"); return true; }));

            Assert.Equal(new[] { renamed }, paths);
            Assert.False(File.Exists(old), "two operator files would load at the next start");
            Assert.True(File.Exists(renamed));
        }

        // ---- #1241: an old file a rename could not remove ----

        private const string Record = "retired-operator-files.txt";

        private string RecordPath => Path.Combine(_dir, Record);

        /// <summary>A rename whose save works and whose delete fails: the old
        /// file is read-only, so File.Delete throws.</summary>
        private (string Old, string Renamed) RenameWithStuckOldFile()
        {
            string old = FileIn("OLD_Operator.xml", OperatorXml("OLD_Operator.xml"));
            string renamed = Path.Combine(_dir, "NEW_Operator.xml");
            File.SetAttributes(old, FileAttributes.ReadOnly);
            // A readable operator: since #1254 a stuck old file is removed only
            // when the renamed copy reads.
            Assert.True(Run(old, renamed, () => { WriteOperator(renamed); return true; }));
            Assert.True(File.Exists(old), "positive control: the read-only old file really could not be deleted");
            return (old, renamed);
        }

        private string[] Load() =>
            ((string[])CallStatic("OperatorFilesToLoad", _dir)!).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

        [Fact]
        public void A_rename_whose_old_file_cannot_be_removed_records_it()
        {
            RenameWithStuckOldFile();
            Assert.True(File.Exists(RecordPath), "the stuck old file must be recorded");
            string[] lines = File.ReadAllLines(RecordPath);
            Assert.Single(lines);
            string[] parts = lines[0].Split('\t');
            Assert.Equal(new[] { "OLD_Operator.xml", "NEW_Operator.xml" }, parts.Take(2).ToArray());
            // #1254: the stuck file's OWN write time and length, never the clock.
            var info = new FileInfo(Path.Combine(_dir, "OLD_Operator.xml"));
            Assert.Equal(new[] { info.LastWriteTimeUtc.Ticks.ToString(), info.Length.ToString(), "renamed" }, parts.Skip(2).ToArray());
            File.SetAttributes(info.FullName, FileAttributes.Normal);
        }

        [Fact]
        public void The_next_start_skips_a_stuck_old_file_and_keeps_it_on_record()
        {
            var (old, _) = RenameWithStuckOldFile();
            Assert.Equal(new[] { "NEW_Operator.xml" }, Load());
            Assert.True(File.Exists(old), "nothing deletes it while it still cannot be deleted");
            Assert.True(File.Exists(RecordPath), "still stuck, so still on record");
            File.SetAttributes(old, FileAttributes.Normal);
        }

        [Fact]
        public void The_next_start_removes_the_old_file_once_it_can_and_clears_the_record()
        {
            var (old, renamed) = RenameWithStuckOldFile();
            File.SetAttributes(old, FileAttributes.Normal);

            Assert.Equal(new[] { "NEW_Operator.xml" }, Load());
            Assert.False(File.Exists(old), "the retired file is removed at the next start");
            Assert.False(File.Exists(RecordPath), "an empty record is removed");
            Assert.Contains("<handl>NEW_Operator</handl>", File.ReadAllText(renamed));
        }

        [Fact]
        public void A_newer_operator_file_at_the_old_name_is_kept_and_loaded()
        {
            var (old, _) = RenameWithStuckOldFile();
            File.SetAttributes(old, FileAttributes.Normal);
            // A later operator saved under the old name: written after the record.
            File.WriteAllText(old, "a different operator");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(1));

            Assert.Equal(new[] { "NEW_Operator.xml", "OLD_Operator.xml" }, Load());
            Assert.Equal("a different operator", File.ReadAllText(old));
            Assert.False(File.Exists(RecordPath), "the entry is settled");
        }

        [Fact]
        public void With_the_renamed_file_gone_the_old_file_is_the_only_copy_and_is_loaded()
        {
            var (old, renamed) = RenameWithStuckOldFile();
            File.SetAttributes(old, FileAttributes.Normal);
            File.Delete(renamed);

            Assert.Equal(new[] { "OLD_Operator.xml" }, Load());
            Assert.Equal(OperatorXml("OLD_Operator.xml"), File.ReadAllText(old));
        }

        [Fact]
        public void A_later_good_rename_carries_the_record_to_the_newest_file()
        {
            var (old, renamed) = RenameWithStuckOldFile();
            string third = Path.Combine(_dir, "THIRD_Operator.xml");
            Assert.True(Run(renamed, third, () => { WriteOperator(third); return true; }));
            Assert.False(File.Exists(renamed));

            File.SetAttributes(old, FileAttributes.Normal);
            Assert.Equal(new[] { "THIRD_Operator.xml" }, Load());
            Assert.False(File.Exists(old), "the first old file is still known to be stale, and goes");
        }

        [Fact]
        public void A_record_never_names_a_file_outside_the_folder_or_a_non_operator_file()
        {
            string outside = Path.Combine(Path.GetTempPath(), "jjf-outside-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(outside, "not ours");
            try
            {
                FileIn("NEW_Operator.xml", "edited");
                FileIn("notes.txt", "not an operator");
                long ticks = DateTime.UtcNow.AddDays(1).Ticks;
                File.WriteAllLines(RecordPath, new[]
                {
                    outside + "\tNEW_Operator.xml\t" + ticks,
                    "..\\" + Path.GetFileName(outside) + "\tNEW_Operator.xml\t" + ticks,
                    "notes.txt\tNEW_Operator.xml\t" + ticks,
                    "garbled line",
                    "",
                });
                Assert.Equal(new[] { "NEW_Operator.xml" }, Load());
                Assert.True(File.Exists(outside), "a file outside the operators folder is never touched");
                Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")), "a file that is not an operator is never touched");
                Assert.False(File.Exists(RecordPath), "unreadable entries are dropped");
            }
            finally
            {
                File.Delete(outside);
            }
        }

        [Fact]
        public void The_record_is_never_read_as_an_operator_so_startup_survives_it()
        {
            RenameWithStuckOldFile();
            Assert.True(File.Exists(RecordPath));
            Assert.DoesNotContain(Record, Load());
            Assert.False(Record.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Record, (string)ConstantOf("RetiredFilesRecordName"));
            File.SetAttributes(Path.Combine(_dir, "OLD_Operator.xml"), FileAttributes.Normal);
        }

        [Fact]
        public void Positive_control_with_no_record_every_operator_file_loads()
        {
            FileIn("A_Operator.xml", "a");
            FileIn("B_Operator.xml", "b");
            Assert.Equal(new[] { "A_Operator.xml", "B_Operator.xml" }, Load());
        }

        [Fact]
        public void Startup_reads_its_operators_through_the_settled_list()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "PersonalData.vb")).Replace("\r\n", "\n");
            int start = src.IndexOf("Public Sub New(ByVal baseDir As String)", StringComparison.Ordinal);
            Assert.True(start >= 0);
            string ctor = src.Substring(start, src.IndexOf("\n    End Sub\n", start, StringComparison.Ordinal) - start);
            Assert.Contains("Dim opFiles As String() = OperatorFilesToLoad(opsDir)", ctor);
            Assert.DoesNotContain("GetFiles(opsDir", ctor);
        }

        // ---- #1254: the record's three leftovers ----

        /// <summary>The record's single entry, split into its fields.</summary>
        private string[] RecordFields()
        {
            string[] lines = File.ReadAllLines(RecordPath).Where(l => l.Length > 0).ToArray();
            Assert.Single(lines);
            return lines[0].Split('\t');
        }

        /// <summary>
        /// A clock set back: a DIFFERENT operator, the same size, is saved
        /// under the old name, and its write time is EARLIER than when the
        /// stuck file was recorded.
        /// The old rule (delete when the file is no newer than the record)
        /// deleted that operator's only copy.
        /// </summary>
        [Fact]
        public void A_different_operator_saved_under_the_old_name_with_an_earlier_time_is_kept_and_loaded()
        {
            var (old, _) = RenameWithStuckOldFile();
            long recorded = long.Parse(RecordFields()[2]);
            File.SetAttributes(old, FileAttributes.Normal);
            // The SAME length as the recorded file, so only the time tells
            // them apart: the case the old clock rule deleted.
            string other = OperatorXml("OLD_Operator.xml").Replace("<handl>OLD_Operator</handl>", "<handl>OLD_Operatus</handl>");
            Assert.Equal(new FileInfo(old).Length, (long)System.Text.Encoding.UTF8.GetByteCount(other));
            File.WriteAllText(old, other);
            File.SetLastWriteTimeUtc(old, new DateTime(recorded, DateTimeKind.Utc).AddHours(-1));

            Assert.Equal(new[] { "NEW_Operator.xml", "OLD_Operator.xml" }, Load());
            Assert.Contains("OLD_Operatus", File.ReadAllText(old));
            Assert.False(File.Exists(RecordPath), "the entry is settled: the file it named is gone");
        }

        /// <summary>
        /// The length is half of the match: a different file at exactly the
        /// recorded write time is still not the file that was recorded.
        /// </summary>
        [Fact]
        public void A_file_at_the_recorded_time_but_a_different_length_is_kept_and_loaded()
        {
            var (old, _) = RenameWithStuckOldFile();
            long recorded = long.Parse(RecordFields()[2]);
            File.SetAttributes(old, FileAttributes.Normal);
            File.WriteAllText(old, OperatorXml("OLD_Operator.xml") + "<!-- longer -->");
            File.SetLastWriteTimeUtc(old, new DateTime(recorded, DateTimeKind.Utc));

            Assert.Equal(new[] { "NEW_Operator.xml", "OLD_Operator.xml" }, Load());
            Assert.True(File.Exists(old));
        }

        /// <summary>
        /// Positive control for the two above: the very file recorded, back to
        /// writable, still goes, however far the clock has moved since.
        /// </summary>
        [Fact]
        public void The_recorded_file_itself_still_goes_whatever_the_clock_says()
        {
            var (old, _) = RenameWithStuckOldFile();
            File.SetAttributes(old, FileAttributes.Normal);
            Assert.Equal(new[] { "NEW_Operator.xml" }, Load());
            Assert.False(File.Exists(old));
        }

        /// <summary>
        /// A deleted operator came back under its old name: rename X from OLD
        /// to NEW while OLD cannot be removed, then delete X in the Operators
        /// list. NEW is gone, so the "only copy" rule loaded OLD as X again.
        /// Driven through the real <c>RemoveAt</c>.
        /// </summary>
        [Fact]
        public void A_deleted_operator_does_not_come_back_under_its_old_name()
        {
            var (old, renamed) = RenameWithStuckOldFile();
            WriteOperator(Path.Combine(_dir, "A_Default.xml"), isDefault: true);

            var seen = LoadOperators(app =>
            {
                Assert.Equal(new[] { "A_Default", "NEW_Operator" }, app.Handles());
                Assert.True(app.RemoveAt(1));
            });
            Assert.Empty(seen.NotDeleted);
            Assert.False(File.Exists(renamed), "positive control: the operator's file was deleted");
            Assert.Equal("deleted", RecordFields()[4]);

            // Still stuck: never loaded, still on record.
            Assert.Equal(new[] { "A_Default.xml" }, Load());
            Assert.True(File.Exists(old));
            Assert.True(File.Exists(RecordPath));

            // Free again: removed, and the record cleared.
            File.SetAttributes(old, FileAttributes.Normal);
            Assert.Equal(new[] { "A_Default.xml" }, Load());
            Assert.False(File.Exists(old), "the deleted operator's old copy is removed, not loaded");
            Assert.False(File.Exists(RecordPath));
        }

        /// <summary>
        /// After the delete, a NEW operator saved under the old name is not the
        /// recorded file, so it is kept and loaded: the delete marker never
        /// reaches past the file that was recorded.
        /// </summary>
        [Fact]
        public void After_a_delete_a_new_operator_under_the_old_name_is_kept()
        {
            var (old, renamed) = RenameWithStuckOldFile();
            File.Delete(renamed);
            CallStatic("MarkRetiredFilesDeleted", renamed);
            Assert.Equal("deleted", RecordFields()[4]);

            File.SetAttributes(old, FileAttributes.Normal);
            WriteOperator(old);   // a new operator who reuses the name
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(new[] { "OLD_Operator.xml" }, Load());
            Assert.True(File.Exists(old));
        }

        /// <summary>
        /// The renamed copy has become unreadable before the next start: the
        /// old file is the operator's last good copy, so it is kept AND loaded
        /// as the operator, and stays on record until the renamed copy reads.
        /// It used to be deleted because the renamed file merely existed.
        /// </summary>
        [Fact]
        public void An_unreadable_renamed_copy_keeps_the_old_file_and_loads_it_as_the_operator()
        {
            var (old, renamed) = RenameWithStuckOldFile();
            File.SetAttributes(old, FileAttributes.Normal);
            File.WriteAllText(renamed, "<personal_v1><fileName>NEW");   // cut short
            WriteOperator(Path.Combine(_dir, "A_Default.xml"), isDefault: true);

            var seen = LoadOperators(app =>
                Assert.Equal(new[] { "A_Default", "OLD_Operator" }, app.Handles()));
            Assert.Equal(new[] { "NEW_Operator.xml" }, seen.NotLoaded);
            Assert.True(File.Exists(old), "the last good copy was deleted");
            Assert.True(File.Exists(RecordPath), "kept on record for a start at which the renamed copy reads");

            // The renamed copy reads again: the stale old file goes.
            WriteOperator(renamed);
            Assert.Equal(new[] { "A_Default.xml", "NEW_Operator.xml" }, Load());
            Assert.False(File.Exists(old));
        }

        /// <summary>
        /// Batch 19 wrote three-field lines (old, new, the failure time), and
        /// they are on main, so one may exist on a real machine. Nothing in
        /// such a line shows the file there now is the stale copy, so it is
        /// kept and loaded, never deleted, and the entry dropped. Under the old
        /// rule this line (a failure time later than the file) deleted it.
        /// </summary>
        [Fact]
        public void An_earlier_format_entry_keeps_the_old_file_and_loads_it()
        {
            string old = FileIn("OLD_Operator.xml", OperatorXml("OLD_Operator.xml"));
            WriteOperator(Path.Combine(_dir, "NEW_Operator.xml"));
            File.WriteAllText(RecordPath, "OLD_Operator.xml\tNEW_Operator.xml\t" + DateTime.UtcNow.AddDays(1).Ticks + "\r\n");

            Assert.Equal(new[] { "NEW_Operator.xml", "OLD_Operator.xml" }, Load());
            Assert.True(File.Exists(old));
            Assert.False(File.Exists(RecordPath), "the entry is dropped");
        }

        /// <summary>
        /// The record has one reader: a later rename retargets an entry of
        /// either shape, keeping its shape, and a delete marks only the
        /// five-field one.
        /// </summary>
        [Fact]
        public void Retargeting_and_the_delete_marker_read_the_record_through_one_parser()
        {
            string a = FileIn("A_Operator.xml", "a");
            string b = FileIn("B_Operator.xml", "b");
            string c = Path.Combine(_dir, "C_Operator.xml");
            File.WriteAllText(RecordPath,
                "A_Operator.xml\tB_Operator.xml\t123\t1\trenamed\r\n" +
                "X_Operator.xml\tB_Operator.xml\t456\r\n");
            Assert.True(Run(b, c, () => { WriteOperator(c); return true; }));
            Assert.Equal(new[]
            {
                "A_Operator.xml\tC_Operator.xml\t123\t1\trenamed",
                "X_Operator.xml\tC_Operator.xml\t456",
            }, File.ReadAllLines(RecordPath));

            CallStatic("MarkRetiredFilesDeleted", c);
            Assert.Equal(new[]
            {
                "A_Operator.xml\tC_Operator.xml\t123\t1\tdeleted",
                "X_Operator.xml\tC_Operator.xml\t456",
            }, File.ReadAllLines(RecordPath));
            Assert.True(File.Exists(a));
        }

        [Fact]
        public void RemoveAt_marks_the_record_only_after_the_operator_file_is_deleted()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "PersonalData.vb")).Replace("\r\n", "\n");
            string remove = SourceMember.VisualBasic(src, "Public Function RemoveAt(ByVal id As Integer) As Boolean");
            int delete = remove.IndexOf("File.Delete(fileName)", StringComparison.Ordinal);
            int mark = remove.IndexOf("MarkRetiredFilesDeleted(fileName)", StringComparison.Ordinal);
            int caught = remove.IndexOf("Catch ex As Exception", delete, StringComparison.Ordinal);
            Assert.True(delete > 0 && mark > delete && caught > mark,
                "the record is marked inside the Try, after a delete that succeeded");
        }

        // ---- #1276: a delete keeps the current operator current ----

        /// <summary>
        /// Five operators, E the default (so none of these deletes the default, which
        /// writes through Write and its box), and the current one at
        /// <paramref name="current"/>. Deletes <paramref name="delete"/> through
        /// the real <c>RemoveAt</c>, then reports who is current.
        /// </summary>
        private (string Before, string After, int AfterId) DeleteWithCurrent(int current, int delete)
        {
            foreach (string n in new[] { "A_First", "B_Second", "C_Third", "D_Fourth" })
                WriteOperator(Path.Combine(_dir, n + ".xml"));
            WriteOperator(Path.Combine(_dir, "E_Default.xml"), isDefault: true);
            string before = "", after = "";
            int afterId = -2;
            LoadOperators(app =>
            {
                Assert.Equal(new[] { "A_First", "B_Second", "C_Third", "D_Fourth", "E_Default" }, app.Handles());
                app.CurrentID = current;
                object was = app.CurrentItem!;
                before = app.HandleOf(was);
                Assert.True(app.RemoveAt(delete));
                after = app.HandleOf(app.CurrentItem);
                afterId = app.CurrentID;
                if (delete != current)
                    Assert.Same(was, app.CurrentItem);   // the SAME operator, by identity
            });
            return (before, after, afterId);
        }

        [Fact]
        public void Deleting_the_operator_just_above_the_current_one_keeps_it_current()
        {
            var r = DeleteWithCurrent(current: 1, delete: 0);
            Assert.Equal(("B_Second", "B_Second", 0), r);
        }

        /// <summary>
        /// The current operator THIRD or lower: lowered twice, the id landed on
        /// the operator above it, so a different operator became current with
        /// nothing said and every per-operator store still the old one's.
        /// </summary>
        [Fact]
        public void Deleting_an_operator_two_above_the_current_one_keeps_it_current()
        {
            var r = DeleteWithCurrent(current: 3, delete: 0);
            Assert.Equal(("D_Fourth", "D_Fourth", 2), r);
        }

        [Fact]
        public void Deleting_the_operator_just_above_a_third_current_one_keeps_it_current()
        {
            var r = DeleteWithCurrent(current: 2, delete: 1);
            Assert.Equal(("C_Third", "C_Third", 1), r);
        }

        [Fact]
        public void Deleting_an_operator_below_the_current_one_changes_nothing()
        {
            var r = DeleteWithCurrent(current: 2, delete: 3);
            Assert.Equal(("C_Third", "C_Third", 2), r);
        }

        /// <summary>
        /// Deleting the current operator itself: the Operators list refuses it,
        /// and <c>RemoveAt</c> makes the default current, as it always has.
        /// </summary>
        [Fact]
        public void Deleting_the_current_operator_through_the_store_makes_the_default_current()
        {
            var r = DeleteWithCurrent(current: 2, delete: 2);
            Assert.Equal(("C_Third", "E_Default", 3), r);
        }

        /// <summary>
        /// Through the Operators list's own delete path, <c>Lister.RemoveFromStore</c>,
        /// not the store alone: the five tests above call <c>RemoveAt</c> directly
        /// and stay green when the list lowers the current id a second time, which
        /// is the defect #1276 fixed. These go red on that. Operators are written
        /// as <paramref name="names"/> (the one ending "_Default" is the default),
        /// the current one at <paramref name="current"/>; deletes
        /// <paramref name="delete"/> and reports who is current and who is the
        /// default afterwards.
        /// </summary>
        private (string Before, string After, int AfterId, string Default) ListDelete(string[] names, int current, int delete)
        {
            foreach (string n in names)
                WriteOperator(Path.Combine(_dir, n + ".xml"), isDefault: n.EndsWith("_Default", StringComparison.Ordinal));
            string before = "", after = "", dflt = "";
            int afterId = -2;
            LoadOperators(app =>
            {
                Assert.Equal(names, app.Handles());
                app.CurrentID = current;
                object was = app.CurrentItem!;
                before = app.HandleOf(was);
                app.ListRemove(delete);
                after = app.HandleOf(app.CurrentItem);
                afterId = app.CurrentID;
                dflt = app.HandleOf(app.DefaultItem);
                Assert.Same(was, app.CurrentItem);   // the SAME operator, by identity
            });
            return (before, after, afterId, dflt);
        }

        [Fact]
        public void Two_operators_deleting_the_one_above_the_current_one_through_the_list_keeps_it_current()
        {
            // Lowered twice, the id went to -1: nobody was current.
            var r = ListDelete(new[] { "A_First", "B_Default" }, current: 1, delete: 0);
            Assert.Equal(("B_Default", "B_Default", 0, "B_Default"), r);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void Three_operators_deleting_either_one_above_the_third_through_the_list_keeps_it_current(int delete)
        {
            // Lowered twice, the operator above the current one became current
            // with nothing said.
            var r = ListDelete(new[] { "A_First", "B_Second", "C_Default" }, current: 2, delete: delete);
            Assert.Equal(("C_Default", "C_Default", 1, "C_Default"), r);
        }

        [Fact]
        public void Deleting_the_default_above_the_current_one_through_the_list_keeps_it_current_and_moves_the_default()
        {
            // The list asks before deleting the default; this is the path once
            // the operator says yes. RemoveAt makes the first operator the
            // default and writes it, so the file it writes must be writable here
            // (a failed write would put up the store's error box).
            string first = Path.Combine(_dir, "B_Second.xml");
            var r = ListDelete(new[] { "A_Default", "B_Second", "C_Third" }, current: 2, delete: 0);
            Assert.Equal(("C_Third", "C_Third", 1, "B_Second"), r);
            Assert.True(File.Exists(first));
            Assert.False(File.Exists(Path.Combine(_dir, "A_Default.xml")), "the deleted operator's file is gone");
        }

        /// <summary>
        /// One place lowers the current id: <c>RemoveAt</c>. The Operators
        /// list's Delete removes through <c>RemoveFromStore</c> and refreshes, and
        /// neither touches the current id itself.
        /// </summary>
        [Fact]
        public void The_operator_list_leaves_the_current_id_to_the_store()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "Lister.vb")).Replace("\r\n", "\n");
            string delete = SourceMember.VisualBasic(src, "Private Sub DeleteButton_Click(");
            int remove = delete.IndexOf("RemoveFromStore(listObj, id)", StringComparison.Ordinal);
            Assert.True(remove > 0, "positive control: the Delete button still removes through the store");
            string after = delete.Substring(remove);
            Assert.DoesNotContain("currentID -=", after, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("currentID =", after, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CurrentOpID", after, StringComparison.Ordinal);
            Assert.DoesNotContain("RemoveAT(", delete, StringComparison.OrdinalIgnoreCase);

            string seam = SourceMember.VisualBasic(src, "Friend Shared Sub RemoveFromStore(");
            Assert.Contains("store.RemoveAt(id)", seam, StringComparison.Ordinal);
            Assert.DoesNotContain("CurrentID", seam.Substring(seam.IndexOf("store.RemoveAt(id)", StringComparison.Ordinal)), StringComparison.OrdinalIgnoreCase);

            string store = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "PersonalData.vb")).Replace("\r\n", "\n");
            string removeAt = SourceMember.VisualBasic(store, "Public Function RemoveAt(ByVal id As Integer) As Boolean");
            Assert.Contains("ElseIf id < CurrentID Then", removeAt, StringComparison.Ordinal);
        }

        // ---- loading the folder as PersonalData ----

        private sealed class Seen
        {
            public System.Collections.Generic.List<string> NotLoaded { get; } = new();
            public System.Collections.Generic.List<string> NotDeleted { get; } = new();
            public int OtherNotices { get; set; }
        }

        private sealed class Ops
        {
            private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            private readonly object _pd;
            public Ops(object pd) => _pd = pd;
            private System.Collections.IList List => (System.Collections.IList)_pd.GetType().GetField("ops", Any)!.GetValue(_pd)!;
            public string[] Handles() => List.Cast<object>().Select(o => (string)o.GetType().GetField("handl")!.GetValue(o)!).ToArray();
            public bool RemoveAt(int id) => (bool)_pd.GetType().GetMethod("RemoveAt", Any)!.Invoke(_pd, new object[] { id })!;
            public int CurrentID
            {
                get => (int)_pd.GetType().GetProperty("CurrentID", Any)!.GetValue(_pd)!;
                set => _pd.GetType().GetProperty("CurrentID", Any)!.SetValue(_pd, value);
            }
            /// <summary>The current operator itself, the object, not its index.</summary>
            public object? CurrentItem => _pd.GetType().GetProperty("CurrentItem", Any)!.GetValue(_pd);
            public object Item(int id) => List[id]!;
            /// <summary>The default operator itself, by the store's default id.</summary>
            public object? DefaultItem
            {
                get
                {
                    int id = (int)_pd.GetType().GetProperty("DefaultID", Any)!.GetValue(_pd)!;
                    return id < 0 ? null : List[id];
                }
            }
            /// <summary>
            /// The Operators list's own delete, <c>Lister.RemoveFromStore</c>, called
            /// as a shared method: no list is constructed and nothing is shown.
            /// </summary>
            public void ListRemove(int id)
            {
                Type lister = _pd.GetType().Assembly.GetTypes().Single(t => t.Name == "Lister");
                MethodInfo? remove = lister.GetMethod("RemoveFromStore", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(remove != null, "Lister.RemoveFromStore is gone; the list's delete path is no longer tested");
                remove!.Invoke(null, new object[] { _pd, id });
            }
            public string HandleOf(object? op) => op == null ? "(nobody)" : (string)op.GetType().GetField("handl")!.GetValue(op)!;
        }

        /// <summary>
        /// Construct the built app's <c>PersonalData</c> over this test's
        /// folder, every notice replaced by a recorder first, then run
        /// <paramref name="then"/>. The rules of
        /// <see cref="OperatorUnreadableFileTests"/>: the folder always holds an
        /// operator file (an empty one opens the add-operator dialog), and a
        /// test that deletes the default operator (which writes the new default
        /// through <c>Write</c>, whose failure puts up a box) does it only in this
        /// test's own writable folder.
        /// </summary>
        private Seen LoadOperators(Action<Ops> then)
        {
            Assert.True(Directory.GetFiles(_dir, "*.xml").Length > 0,
                "every test folder must hold an operator file, or the constructor opens the add-operator dialog");
            var seen = new Seen();
            string? app = BuiltAppAssembly();
            Assert.True(app != null, "Build JJFlexRadio.vbproj (Debug, x64) first.");
            WeakReference unloaded = LoadInIsolatedContext(app!, _base, seen, then);
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.Equal(0, seen.OtherNotices);
            return seen;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference LoadInIsolatedContext(string app, string baseDir, Seen seen, Action<Ops> then)
        {
            var context = new AssemblyLoadContext("operator-load", isCollectible: true);
            string folder = Path.GetDirectoryName(app)!;
            context.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(app);
                Type pd = assembly.GetTypes().Single(t => t.Name == "PersonalData");
                const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                pd.GetField("OperatorFilesNotLoadedNotice", statics)!.SetValue(null,
                    new Action<System.Collections.Generic.IReadOnlyList<string>>(names => seen.NotLoaded.AddRange(names)));
                pd.GetField("OperatorFileReadOnlyNotice", statics)!.SetValue(null, new Action<string>(_ => seen.OtherNotices++));
                pd.GetField("OperatorFileNotDeletedNotice", statics)!.SetValue(null, new Action<string>(n => seen.NotDeleted.Add(n)));
                pd.GetField("OperatorNotSavedNotice", statics)!.SetValue(null, new Action(() => seen.OtherNotices++));
                try
                {
                    object instance = pd.GetConstructor(new[] { typeof(string) })!.Invoke(new object[] { baseDir });
                    then(new Ops(instance));
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                }
            }
            finally
            {
                context.Unload();
            }
            return new WeakReference(context);
        }

        private object? CallStatic(string method, params object[] args)
        {
            string? app = BuiltAppAssembly();
            Assert.True(app != null,
                "Build JJFlexRadio.vbproj (Debug, x64) first: this test calls the operator store "
                + "from the built app assembly.");
            object? result = null;
            WeakReference unloaded = CallInIsolatedContext(app!, method, args, r => result = r);
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return result;
        }

        private static object ConstantOf(string name)
        {
            object? value = null;
            WeakReference unloaded = ConstantInIsolatedContext(BuiltAppAssembly()!, name, v => value = v);
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return value!;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference ConstantInIsolatedContext(string app, string name, Action<object?> report)
        {
            var context = new AssemblyLoadContext("operator-constant", isCollectible: true);
            string folder = Path.GetDirectoryName(app)!;
            context.Resolving += (ctx, assemblyName) =>
            {
                string candidate = Path.Combine(folder, assemblyName.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(app);
                Type personal = assembly.GetTypes().Single(t => t.Name == "PersonalData");
                FieldInfo? field = personal.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(field != null, "PersonalData." + name + " is missing");
                report(field!.GetValue(null));
            }
            finally
            {
                context.Unload();
            }
            return new WeakReference(context);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CallInIsolatedContext(string app, string method, object[] args, Action<object?> report)
        {
            var context = new AssemblyLoadContext("operator-store", isCollectible: true);
            string folder = Path.GetDirectoryName(app)!;
            context.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(app);
                Type personal = assembly.GetTypes().Single(t => t.Name == "PersonalData");
                MethodInfo? step = personal.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(step != null, "PersonalData." + method + " is missing");
                report(step!.Invoke(null, args));
            }
            finally
            {
                context.Unload();
            }
            return new WeakReference(context);
        }

        private static bool Replace(System.Collections.Generic.List<string> entries, int id, string edited,
                                    string oldPath, string newPath, Func<bool> save)
        {
            string? app = BuiltAppAssembly();
            Assert.True(app != null,
                "Build JJFlexRadio.vbproj (Debug, x64) first: this test calls the operator save step "
                + "from the built app assembly.");
            bool result = false;
            WeakReference unloaded = ReplaceInIsolatedContext(app!, entries, id, edited, oldPath, newPath, save, r => result = r);
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference ReplaceInIsolatedContext(string app, System.Collections.Generic.List<string> entries, int id, string edited,
                                                              string oldPath, string newPath, Func<bool> save, Action<bool> report)
        {
            var context = new AssemblyLoadContext("operator-replace", isCollectible: true);
            string folder = Path.GetDirectoryName(app)!;
            context.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(app);
                Type personal = assembly.GetTypes().Single(t => t.Name == "PersonalData");
                MethodInfo? step = personal.GetMethod("SaveThenReplace", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(step != null, "PersonalData.SaveThenReplace is missing");
                report((bool)step!.MakeGenericMethod(typeof(string))
                    .Invoke(null, new object[] { entries, id, edited, oldPath, newPath, save })!);
            }
            finally
            {
                context.Unload();
            }
            return new WeakReference(context);
        }

        private static bool Run(string oldPath, string newPath, Func<bool> save)
        {
            string? app = BuiltAppAssembly();
            Assert.True(app != null,
                "Build JJFlexRadio.vbproj (Debug, x64) first: this test calls the operator save step "
                + "from the built app assembly.");
            bool result = false;
            WeakReference unloaded = RunInIsolatedContext(app!, oldPath, newPath, save, r => result = r);
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference RunInIsolatedContext(string app, string oldPath, string newPath, Func<bool> save, Action<bool> report)
        {
            var context = new AssemblyLoadContext("operator-save", isCollectible: true);
            string folder = Path.GetDirectoryName(app)!;
            context.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(app);
                Type personal = assembly.GetTypes().Single(t => t.Name == "PersonalData");
                MethodInfo step = personal.GetMethod("SaveThenRetireOld", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
                Assert.NotNull(step);
                report((bool)step.Invoke(null, new object[] { oldPath, newPath, save })!);
            }
            finally
            {
                context.Unload();
            }
            return new WeakReference(context);
        }

        private static string? BuiltAppAssembly()
        {
            string root = FieldKeyMapScan.RepoRoot();
            foreach (string config in new[] { "Debug", "Release" })
            {
                string path = Path.Combine(root, "bin", "x64", config, "net10.0-windows", "win-x64", "jjflexible.dll");
                if (File.Exists(path)) return path;
            }
            return null;
        }
    }
}
