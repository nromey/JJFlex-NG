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
    /// #1255: the automatic opens #1066 did not reach. After saving a file,
    /// the Profile comparison report, the connection test results and the key
    /// list export each opened it with the shell, so an isolated run
    /// (<c>JJFLEX_CONFIG_DIR</c>) put an editor on the real desktop. All three
    /// now open through <see cref="ProfileReporter.OpenExport(string)"/>,
    /// which writes the file and traces "not opened, isolated run" instead.
    /// And the log file's Open or Save dialog, for an operator with no log
    /// yet, starts inside the throwaway tree on an isolated run rather than in
    /// the real Documents.
    /// </summary>
    /// <remarks>
    /// The decision itself (relocated: no open; ordinary: open) is
    /// <see cref="ExportFolderIsolationTests"/>' to pin, including its positive
    /// control that an ordinary run still opens. Here, the wiring: each call
    /// site goes through that one helper and holds no shell open of its own.
    /// The explicit "open this folder" commands, web links and the updater are
    /// deliberately NOT routed: they are the operator asking for a window.
    /// </remarks>
    [Collection(BuiltAppAssemblyCollection.Name)]
    public sealed class AutomaticOpenIsolationTests
    {
        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { FieldKeyMapScan.RepoRoot() }.Concat(parts).ToArray())).Replace("\r\n", "\n");

        [Fact]
        public void The_comparison_report_opens_through_the_helper_then_waits_then_speaks()
        {
            string src = Read("JJFlexWpf", "NativeMenuBar.cs");
            int save = src.IndexOf("var path = ProfileReporter.SaveReport(report.Text);", StringComparison.Ordinal);
            Assert.True(save >= 0, "the comparison report's save is missing");
            int spoken = src.IndexOf("ProfileReporter.ComparisonFinishedSentence(report, path)", save, StringComparison.Ordinal);
            Assert.True(spoken > save, "the comparison report's closing sentence is missing");
            string block = src.Substring(save, spoken - save);

            Assert.DoesNotContain("Process.Start(", block, StringComparison.Ordinal);
            int tryAt = block.IndexOf("try", StringComparison.Ordinal);
            int open = block.IndexOf("ProfileReporter.OpenExport(path);", StringComparison.Ordinal);
            int caught = block.IndexOf("catch (Exception openEx)", StringComparison.Ordinal);
            int wait = block.IndexOf("System.Threading.Thread.Sleep(500);", StringComparison.Ordinal);
            Assert.True(tryAt >= 0 && open > tryAt && caught > open, "the open stays inside its own try");
            Assert.True(wait > caught, "open first, then the 500 ms wait, then the sentence: the order its comment explains");
        }

        [Fact]
        public void The_connection_test_results_open_through_the_helper()
        {
            string show = SourceMember.VisualBasic(Read("globals.vb"), "Friend Sub ShowTestResults()");
            Assert.Contains("Radios.ProfileReporter.OpenExport(reportPath)", show, StringComparison.Ordinal);
            Assert.DoesNotContain("Process.Start(", show, StringComparison.Ordinal);
            Assert.Contains("ConnectionTestReport.SaveReport(", show, StringComparison.Ordinal);   // control: the right member
        }

        [Fact]
        public void The_key_list_export_opens_through_the_helper()
        {
            string export = SourceMember.CSharp(Read("JJFlexWpf", "Dialogs", "KeysDialog.xaml.cs"),
                                                "private void ExportButton_Click(");
            Assert.Contains("Radios.ProfileReporter.OpenExport(path);", export, StringComparison.Ordinal);
            Assert.DoesNotContain("Process.Start(", export, StringComparison.Ordinal);
            Assert.Contains("KeyManifest.WriteToFile(", export, StringComparison.Ordinal);       // control: the right member
        }

        [Fact]
        public void The_log_file_dialog_asks_the_shared_rule_and_makes_the_isolated_folder_first()
        {
            string ask = SourceMember.VisualBasic(Read("LogClass.vb"),
                "Private Function AskForFile(operater As PersonalData.personal_v1, createNew As Boolean) As String");
            Assert.Contains("Dim relocated As Boolean = Radios.LocalDataRoot.RunIsRelocated", ask, StringComparison.Ordinal);
            Assert.Contains("LogDialogStartFolder(relocated, Radios.ProfileReporter.ExportFolder,", ask, StringComparison.Ordinal);
            int make = ask.IndexOf("Directory.CreateDirectory(folder)", StringComparison.Ordinal);
            int shown = ask.IndexOf("dialog.InitialDirectory = folder", StringComparison.Ordinal);
            Assert.True(make > 0 && shown > make, "the isolated folder is made before the dialog starts in it");
            Assert.DoesNotContain("Dim folder As String = Environment.GetFolderPath(", ask, StringComparison.Ordinal);
        }

        [Fact]
        public void The_log_file_dialog_starts_inside_the_tree_only_on_an_isolated_run()
        {
            const string isolated = @"D:\scratch\jjflex-run-7\Exports";
            const string documents = @"C:\Users\op\Documents";
            Assert.Equal(isolated, StartFolder(relocated: true, isolated, documents));
            // Positive control: an ordinary run starts in Documents, unchanged.
            Assert.Equal(documents, StartFolder(relocated: false, isolated, documents));
            // No isolated folder to offer: Documents, as before, rather than nothing.
            Assert.Equal(documents, StartFolder(relocated: true, "  ", documents));
        }

        private static string StartFolder(bool relocated, string isolated, string documents)
        {
            string? app = BuiltAppAssembly();
            Assert.True(app != null,
                "Build JJFlexRadio.vbproj (Debug, x64) first: this test calls the log dialog's start-folder rule "
                + "from the built app assembly.");
            string result = "";
            WeakReference unloaded = CallInIsolatedContext(app!, new object[] { relocated, isolated, documents }, r => result = r);
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CallInIsolatedContext(string app, object[] args, Action<string> report)
        {
            var context = new AssemblyLoadContext("log-start-folder", isCollectible: true);
            string folder = Path.GetDirectoryName(app)!;
            context.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(app);
                Type log = assembly.GetTypes().Single(t => t.Name == "LogClass");
                MethodInfo? rule = log.GetMethod("LogDialogStartFolder", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(rule != null, "LogClass.LogDialogStartFolder is missing");
                report((string)rule!.Invoke(null, args)!);
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
