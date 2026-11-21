#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A failed operator save no longer opens an "Exception" box that reads
    /// the .NET message and stack trace aloud, and an added operator joins the
    /// list only once it is saved (#1195, junk box batch 16). Add used to put
    /// the operator in the list, and make it the default, before the save, and
    /// ignore the save's result, so the session held an operator the disk did
    /// not.
    /// </summary>
    /// <remarks>
    /// Calls the factored-out step, <c>PersonalData.SaveThenAdd</c>, from the
    /// built app assembly in a collectible context, as
    /// <see cref="OperatorSaveKeepsOldFileTests"/> calls <c>SaveThenReplace</c>.
    /// <b>Never <c>PersonalData.Add</c>, <c>Update</c> or the constructor</b>:
    /// Add and Update open the WinForms operator dialog first. The plain notice
    /// is the step's own argument here, a recorder, so no box is ever opened.
    /// Needs <c>JJFlexRadio.vbproj</c> built first and says so if it is not.
    /// </remarks>
    [Collection(BuiltAppAssemblyCollection.Name)]
    public sealed class OperatorAddSaveFirstTests
    {
        [Fact]
        public void An_add_whose_save_fails_leaves_the_list_unchanged_and_says_so_once()
        {
            var entries = new List<string> { "first", "second" };
            int notices = 0;
            int id = Add(entries, "NEW", () => false, () => notices++);
            Assert.Equal(-1, id);
            Assert.Equal(new[] { "first", "second" }, entries);
            Assert.Equal(1, notices);
        }

        [Fact]
        public void An_add_whose_save_works_joins_the_list_and_says_nothing()
        {
            var entries = new List<string> { "first", "second" };
            int notices = 0;
            int id = Add(entries, "NEW", () => true, () => notices++);
            Assert.Equal(2, id);
            Assert.Equal(new[] { "first", "second", "NEW" }, entries);
            Assert.Equal(0, notices);
        }

        [Fact]
        public void The_new_operator_is_not_in_the_list_while_it_is_being_saved()
        {
            var entries = new List<string> { "first" };
            bool inListDuringSave = true;
            Add(entries, "NEW", () => { inListDuringSave = entries.Contains("NEW"); return true; }, () => { });
            Assert.False(inListDuringSave, "the operator joined the list before its save");
        }

        private static string Src() =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "PersonalData.vb")).Replace("\r\n", "\n");

        private static string Member(string src, string signature, string end)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " is missing");
            return src.Substring(start, src.IndexOf(end, start, StringComparison.Ordinal) - start);
        }

        [Fact]
        public void Add_saves_first_and_sets_the_default_only_after_a_good_save()
        {
            string add = Member(Src(), "Public Function Add() As Integer", "End Function");
            Assert.DoesNotContain("ops.Add(", add, StringComparison.Ordinal);
            int save = add.IndexOf("rv = SaveThenAdd(ops, theOp, Function() TryWrite(theOp), OperatorNotSavedNotice)", StringComparison.Ordinal);
            Assert.True(save > 0, "Add saves through SaveThenAdd");
            int dflt = add.IndexOf("DefaultID = rv", StringComparison.Ordinal);
            Assert.True(dflt > save, "the default moves only after the save");
            Assert.Contains("If rv >= 0 AndAlso theOp.DefaultFlag Then", add, StringComparison.Ordinal);
        }

        [Fact]
        public void An_edit_whose_save_fails_says_the_plain_sentence_once()
        {
            string update = Member(Src(), "Public Function Update(ByVal id As Integer) As Boolean", "End Function");
            int save = update.IndexOf("If SaveThenReplace(", StringComparison.Ordinal);
            int otherwise = update.IndexOf("Else", save, StringComparison.Ordinal);
            int notice = update.IndexOf("OperatorNotSavedNotice()", StringComparison.Ordinal);
            Assert.True(save > 0 && otherwise > save && notice > otherwise, "the notice is in the failed save's branch");
            Assert.Equal(1, update.Split("OperatorNotSavedNotice()").Length - 1);
        }

        [Fact]
        public void TryWrite_and_the_old_files_removal_trace_without_the_box_and_Write_keeps_it_for_its_callers()
        {
            string src = Src();
            string tryWrite = Member(src, "Friend Function TryWrite(ByVal op As personal_v1) As Boolean", "End Function");
            Assert.DoesNotContain("ErrMessageTrace", tryWrite, StringComparison.Ordinal);
            Assert.Contains("LastWriteFailure = ex", tryWrite, StringComparison.Ordinal);

            string retire = Member(src, "Friend Shared Function SaveThenRetireOld(", "End Function");
            Assert.DoesNotContain("ErrMessageTrace", retire, StringComparison.Ordinal);

            // Positive control, and the boundary: the other Write callers
            // (UpdateCurrentOp, the step memory, the profiles) keep today's box.
            string write = Member(src, "Friend Sub Write(ByVal op As personal_v1)", "End Sub");
            Assert.Contains("If Not TryWrite(op) Then", write, StringComparison.Ordinal);
            Assert.Contains("Tracing.ErrMessageTrace(LastWriteFailure)", write, StringComparison.Ordinal);

            // The plain box carries the one sentence and no exception text.
            string show = Member(src, "Private Shared Sub ShowOperatorNotSaved()", "End Sub");
            Assert.Contains("\"settings.operator.not_saved\"", show, StringComparison.Ordinal);
            Assert.DoesNotContain("ex.", show, StringComparison.Ordinal);
            Assert.DoesNotContain("StackTrace", show, StringComparison.Ordinal);
        }

        private static int Add(List<string> entries, string added, Func<bool> save, Action notSaved)
        {
            string? app = BuiltAppAssembly();
            Assert.True(app != null,
                "Build JJFlexRadio.vbproj (Debug, x64) first: this test calls the operator save step "
                + "from the built app assembly.");
            int result = int.MinValue;
            WeakReference unloaded = AddInIsolatedContext(app!, entries, added, save, notSaved, r => result = r);
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference AddInIsolatedContext(string app, List<string> entries, string added,
                                                          Func<bool> save, Action notSaved, Action<int> report)
        {
            var context = new AssemblyLoadContext("operator-add", isCollectible: true);
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
                MethodInfo? step = personal.GetMethod("SaveThenAdd", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(step != null, "PersonalData.SaveThenAdd is missing");
                report((int)step!.MakeGenericMethod(typeof(string))
                    .Invoke(null, new object[] { entries, added, save, notSaved })!);
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
