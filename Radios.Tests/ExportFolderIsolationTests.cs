using System;
using System.Collections.Generic;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1066: the Quick settings export and the Full restore capture stay
    /// inside a throwaway settings tree, and are not opened, when
    /// JJFLEX_CONFIG_DIR moves the settings root. An ordinary run is
    /// unchanged: Documents\JJFlexRadio, opened as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> Both exports wrote to Documents\JJFlexRadio
    /// unconditionally, and both menu commands then opened the file with the
    /// shell. An isolated bench run wrote a real <c>.ini</c> into the
    /// operator's Documents and put Notepad on the real desktop.
    /// </para>
    /// <para>
    /// <b>One rule.</b> Whether a run is isolated is
    /// <see cref="LocalDataRoot.IsRelocated"/>, the same exact-folder test the
    /// browser cache uses (#937); the export folder asks it and never decides
    /// again. No test here sets the process environment.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ExportFolderIsolationTests
    {
        private const string Roaming = @"C:\Users\op\AppData\Roaming";
        private const string Documents = @"C:\Users\op\Documents";

        // ---- The shared rule ----

        [Fact]
        public void The_standard_folder_is_not_relocated()
        {
            Assert.False(LocalDataRoot.IsRelocated(Path.Combine(Roaming, "JJFlexRadio"), Roaming));
        }

        [Fact]
        public void The_standard_folder_is_not_relocated_whatever_its_case_or_trailing_separator()
        {
            Assert.False(LocalDataRoot.IsRelocated(Path.Combine(Roaming, "jjflexradio"), Roaming));
            Assert.False(LocalDataRoot.IsRelocated(Path.Combine(Roaming, "JJFlexRadio") + @"\", Roaming + @"\"));
        }

        [Theory]
        [InlineData(@"D:\scratch\jjflex-run-7")]
        [InlineData(@"C:\Users\op\AppData\Roaming\Other")]                // #937: directly inside roaming
        [InlineData(@"C:\Users\op\AppData\Roaming\scratch\JJFlexRadio")]  // the standard name, nested deeper
        public void Any_other_root_is_relocated(string root)
        {
            Assert.True(LocalDataRoot.IsRelocated(root, Roaming));
        }

        [Fact]
        public void An_unresolved_root_is_the_ordinary_one_as_Resolve_treats_it()
        {
            Assert.False(LocalDataRoot.IsRelocated(null, Roaming));
            Assert.False(LocalDataRoot.IsRelocated("  ", Roaming));
            Assert.True(LocalDataRoot.IsRelocated(@"D:\scratch\x", null));
        }

        [Fact]
        public void Positive_control_LocalDataRoot_still_resolves_through_the_shared_rule()
        {
            const string local = @"C:\Users\op\AppData\Local";
            Assert.Equal(Path.Combine(local, "JJFlexRadio"),
                         LocalDataRoot.Resolve(Path.Combine(Roaming, "JJFlexRadio"), Roaming, local));
            Assert.Equal(Path.Combine(Roaming, "Other", LocalDataRoot.RelocatedFolderName),
                         LocalDataRoot.Resolve(Path.Combine(Roaming, "Other"), Roaming, local));
        }

        // ---- The export folder ----

        [Fact]
        public void An_ordinary_run_exports_to_Documents_as_it_always_has()
        {
            string root = Path.Combine(Roaming, "JJFlexRadio");
            Assert.Equal(Path.Combine(Documents, "JJFlexRadio"),
                         ProfileReporter.ResolveExportFolder(root, LocalDataRoot.IsRelocated(root, Roaming), Documents));
        }

        [Fact]
        public void A_case_differing_standard_root_still_exports_to_Documents()
        {
            string root = Path.Combine(Roaming, "jjflexradio");
            Assert.Equal(Path.Combine(Documents, "JJFlexRadio"),
                         ProfileReporter.ResolveExportFolder(root, LocalDataRoot.IsRelocated(root, Roaming), Documents));
        }

        [Fact]
        public void A_relocated_run_exports_inside_its_own_tree_never_to_Documents()
        {
            const string temp = @"D:\scratch\jjflex-run-7";
            string folder = ProfileReporter.ResolveExportFolder(temp, LocalDataRoot.IsRelocated(temp, Roaming), Documents);

            Assert.Equal(Path.Combine(temp, ProfileReporter.RelocatedExportFolderName), folder);
            Assert.DoesNotContain(Documents, folder, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void This_process_exports_under_its_own_roots()
        {
            // The live property, against the live settings root.
            string folder = ProfileReporter.ExportFolder;
            if (LocalDataRoot.RunIsRelocated)
                Assert.StartsWith(RadioConfig.AppDataRoot, folder, StringComparison.OrdinalIgnoreCase);
            else
                Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JJFlexRadio"),
                             folder, StringComparer.OrdinalIgnoreCase);
        }

        // ---- Opening ----

        [Fact]
        public void A_relocated_run_writes_the_file_but_does_not_open_it()
        {
            var opened = new List<string>();
            bool did = ProfileReporter.OpenExport(@"D:\scratch\x\Exports\radio-settings.txt", relocated: true, opened.Add);
            Assert.False(did);
            Assert.Empty(opened);
        }

        [Fact]
        public void Positive_control_an_ordinary_run_opens_the_export_as_before()
        {
            var opened = new List<string>();
            const string path = @"C:\Users\op\Documents\JJFlexRadio\radio-settings.txt";
            bool did = ProfileReporter.OpenExport(path, relocated: false, opened.Add);
            Assert.True(did);
            Assert.Equal(new[] { path }, opened.ToArray());
        }

        // ---- The wiring, read from source ----

        [Fact]
        public void Both_exports_save_through_the_resolved_folder()
        {
            string src = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "Radios", "ProfileReporter.cs"))
                .Replace("\r\n", "\n");
            foreach (string signature in new[]
            {
                "public static string SaveStationSettingsExport(string report, string radioSerial)",
                "public static string SaveRestoreGradeExport(string text, string radioSerial)",
            })
            {
                int start = src.IndexOf(signature, StringComparison.Ordinal);
                Assert.True(start >= 0, signature + " is missing");
                string body = src.Substring(start, src.IndexOf("\n        }\n", start, StringComparison.Ordinal) - start);
                Assert.Contains("var dir = ExportFolder;", body);
                Assert.DoesNotContain("SpecialFolder.MyDocuments", body);
            }
        }

        [Fact]
        public void Both_menu_commands_open_through_the_isolation_aware_helper()
        {
            string src = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "JJFlexWpf", "NativeMenuBar.cs"))
                .Replace("\r\n", "\n");

            // The Quick settings export's command, to its closing "});".
            int quick = src.IndexOf("AddWired(tools, \"Quick settings export, read only\"", StringComparison.Ordinal);
            Assert.True(quick >= 0, "the Quick settings export command is missing");
            string quickBlock = src.Substring(quick, src.IndexOf("\n        });\n", quick, StringComparison.Ordinal) - quick);
            Assert.Contains("ProfileReporter.SaveStationSettingsExport(", quickBlock);   // control: the right block
            Assert.Contains("ProfileReporter.OpenExport(path);", quickBlock);
            Assert.DoesNotContain("Process.Start(", quickBlock);

            // The Full restore capture: from its save to the end of its try.
            int save = src.IndexOf("var savedPath = ProfileReporter.SaveRestoreGradeExport(", StringComparison.Ordinal);
            Assert.True(save >= 0, "the Full restore capture's save is missing");
            string after = src.Substring(save, src.IndexOf("catch (Exception ex)", save, StringComparison.Ordinal) - save);
            Assert.Contains("ProfileReporter.OpenExport(savedPath);", after);
            Assert.DoesNotContain("Process.Start(", after);
        }
    }
}
