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
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjf-opsave-" + Guid.NewGuid().ToString("N"));

        public OperatorSaveKeepsOldFileTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

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
            Assert.Contains("SaveThenRetireOld(oldPath, theOp.pathName, Function() TryWrite(theOp))", update, StringComparison.Ordinal);
            int tryWrite = src.IndexOf("Friend Function TryWrite(ByVal op As personal_v1) As Boolean", StringComparison.Ordinal);
            Assert.True(tryWrite > 0);
            string body = src.Substring(tryWrite, src.IndexOf("End Function", tryWrite, StringComparison.Ordinal) - tryWrite);
            int caught = body.IndexOf("Catch ex As Exception", StringComparison.Ordinal);
            Assert.True(caught > 0 && body.IndexOf("Return False", caught, StringComparison.Ordinal) > caught,
                "a write that fails must say so");
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
