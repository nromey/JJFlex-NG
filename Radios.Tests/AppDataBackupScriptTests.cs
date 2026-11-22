#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #728: <c>backup-appdata-to-nas.ps1</c>, run for real against a scratch
    /// settings tree and a scratch NAS root under the temp folder. It takes
    /// every folder except <c>WebView2</c>, never a raw <c>.dmp</c> (the crash
    /// zips go), keeps the newest 8 (ruled by Noel 2026-11-19 and 2026-11-20),
    /// and one file another process holds open no longer stops it.
    /// </summary>
    /// <remarks>
    /// <b>Never the real AppData, never the real NAS.</b> The script's defaults
    /// are the operator's live settings and the real NAS, and its pruning
    /// deletes history, so <see cref="Run"/> refuses any root outside this
    /// class's own temp folder before it starts a process. Modelled on
    /// <see cref="PrivateBackupScriptTests"/>; the helpers are this class's own.
    /// </remarks>
    public sealed class AppDataBackupScriptTests : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "jjflex-728-" + Guid.NewGuid().ToString("N"));
        private string AppData => Path.Combine(_base, "JJFlexRadio");
        private string Nas => Path.Combine(_base, "nas");
        private string History => Path.Combine(Nas, "historical", "appdata");

        public AppDataBackupScriptTests()
        {
            Directory.CreateDirectory(AppData);
            Write("KeyDefs.xml", "<keys/>");
            Write("JJFlexRadioTrace-20261120-101500.txt", "a finished run's trace");
            Write(Path.Combine("Operators", "Op.xml"), "<personal_v1/>");
            Write(Path.Combine("alarm-journal", "2026-10-02.journal"), "dated measurement data");
            Write(Path.Combine("Recordings", "take.wav"), "audio");
            Write(Path.Combine("Errors", "crash-1.zip"), "the bundle a support conversation reads");
            Write(Path.Combine("Errors", "crash-1.dmp"), "a raw dump, hundreds of megabytes in life");
            Write(Path.Combine("WebView2", "EBWebView", "Default", "Cookies"), "sign-in cookies");
            Write(Path.Combine("WebView2", "EBWebView", "Local State.json"), "browser cache");
        }

        public void Dispose()
        {
            try { Directory.Delete(_base, recursive: true); } catch { }
        }

        private void Write(string relative, string text)
        {
            string path = Path.Combine(AppData, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        private static string RepoRoot() => CaptureMeterSetTests.RepoRoot();

        private static string Shell()
        {
            string ps51 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            Assert.True(File.Exists(ps51), "no Windows PowerShell at " + ps51 + "; a broken test environment, not a broken script");
            return ps51;
        }

        private (int Exit, string Output) Run(string appData, string nas, params string[] extra)
        {
            // The guard this whole class rests on: nothing here may ever reach
            // the operator's settings or the real NAS.
            string scratch = Path.GetFullPath(_base) + Path.DirectorySeparatorChar;
            Assert.StartsWith(scratch, Path.GetFullPath(nas) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(scratch, Path.GetFullPath(appData) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = Shell(),
                WorkingDirectory = RepoRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                                         "-File", Path.Combine(RepoRoot(), "backup-appdata-to-nas.ps1"),
                                         "-AppData", appData, "-Nas", nas }.Concat(extra))
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            Assert.True(p.WaitForExit(120_000), "the backup script did not finish in two minutes");
            return (p.ExitCode, stdout.Result + stderr.Result);
        }

        private string[] Snapshots() =>
            Directory.Exists(History)
                ? Directory.GetFiles(History, "appdata-*.zip").Select(Path.GetFileName).OrderBy(n => n).ToArray()!
                : Array.Empty<string>();

        private static string[] Entries(string zip)
        {
            using var z = ZipFile.OpenRead(zip);
            return z.Entries.Where(e => !e.FullName.EndsWith("/") && !e.FullName.EndsWith("\\"))
                .Select(e => e.FullName.Replace('\\', '/')).ToArray();
        }

        private string Newest() => Path.Combine(History, Snapshots().Last());

        [Fact]
        public void A_clean_run_takes_every_folder_and_the_crash_zips_but_not_WebView2_or_a_raw_dump()
        {
            var (exit, output) = Run(AppData, Nas);
            Assert.True(exit == 0, "exit " + exit + ":\n" + output);
            var entries = Entries(Newest());
            Assert.Contains("KeyDefs.xml", entries);
            Assert.Contains("JJFlexRadioTrace-20261120-101500.txt", entries);   // loose root files too
            Assert.Contains("Operators/Op.xml", entries);
            Assert.Contains("alarm-journal/2026-10-02.journal", entries);
            Assert.Contains("Recordings/take.wav", entries);
            Assert.Contains("Errors/crash-1.zip", entries);
            Assert.DoesNotContain(entries, e => e.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(entries, e => e.StartsWith("WebView2/", StringComparison.OrdinalIgnoreCase));
            // The operator's tree itself is never touched: the dump is still there.
            Assert.True(File.Exists(Path.Combine(AppData, "Errors", "crash-1.dmp")));
        }

        [Fact]
        public void A_folder_named_WebView2_deeper_in_the_tree_is_still_taken()
        {
            // Only the settings root's own browser cache is left out.
            Write(Path.Combine("profile-reports", "WebView2", "notes.txt"), "not the browser cache");
            var (exit, output) = Run(AppData, Nas);
            Assert.True(exit == 0, "exit " + exit + ":\n" + output);
            Assert.Contains("profile-reports/WebView2/notes.txt", Entries(Newest()));
        }

        [Fact]
        public void One_file_another_process_holds_open_is_left_out_and_named_rather_than_failing_the_backup()
        {
            Write("JJFlexRadioTrace.txt", "the live run's trace");
            string held = Path.Combine(AppData, "JJFlexRadioTrace.txt");
            int exit; string output;
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
                (exit, output) = Run(AppData, Nas);

            Assert.True(exit == 5, "exit " + exit + ":\n" + output);
            Assert.Single(Snapshots());                     // the snapshot was made all the same
            var entries = Entries(Newest());
            Assert.Contains("KeyDefs.xml", entries);
            Assert.DoesNotContain("JJFlexRadioTrace.txt", entries);
            Assert.Contains(held, output);                  // and the run names what it left out
        }

        [Fact]
        public void The_newest_eight_snapshots_are_kept_and_nothing_else_in_the_folder_is_touched()
        {
            Directory.CreateDirectory(History);
            for (int day = 1; day <= 10; day++)
                File.WriteAllText(Path.Combine(History, $"appdata-202601{day:D2}-000000.zip"), "an older snapshot");
            File.WriteAllText(Path.Combine(History, "appdata-notes.zip"), "not a snapshot this script named");
            File.WriteAllText(Path.Combine(History, "readme.txt"), "someone else's file");

            var (exit, output) = Run(AppData, Nas);
            Assert.True(exit == 0, "exit " + exit + ":\n" + output);

            var kept = Snapshots().Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^appdata-\d{8}-\d{6}\.zip$")).ToArray();
            Assert.Equal(8, kept.Length);
            Assert.DoesNotContain("appdata-20260103-000000.zip", kept);
            Assert.Contains("appdata-20260104-000000.zip", kept);
            Assert.True(File.Exists(Path.Combine(History, "appdata-notes.zip")));
            Assert.True(File.Exists(Path.Combine(History, "readme.txt")));
        }

        [Fact]
        public void A_missing_settings_folder_exits_2_and_prunes_nothing()
        {
            Directory.CreateDirectory(History);
            for (int day = 1; day <= 10; day++)
                File.WriteAllText(Path.Combine(History, $"appdata-202601{day:D2}-000000.zip"), "an older snapshot");
            var (exit, output) = Run(Path.Combine(_base, "no-such-folder"), Nas);
            Assert.True(exit == 2, "exit " + exit + ":\n" + output);
            Assert.Equal(10, Snapshots().Length);
        }

        [Fact]
        public void The_old_diagnostics_switch_is_still_accepted_and_changes_nothing()
        {
            var (exit, output) = Run(AppData, Nas, "-IncludeDiagnostics");
            Assert.True(exit == 0, "exit " + exit + ":\n" + output);
            Assert.Contains("no longer needed", output);
            var entries = Entries(Newest());
            Assert.Contains("Errors/crash-1.zip", entries);
            Assert.DoesNotContain(entries, e => e.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void The_default_keeps_eight_and_the_script_says_never_to_test_against_the_real_NAS()
        {
            string script = File.ReadAllText(Path.Combine(RepoRoot(), "backup-appdata-to-nas.ps1"));
            Assert.Contains("[int]    $Keep               = 8", script);
            Assert.Contains("NEVER TEST THE PRUNING AGAINST THE REAL NAS PATH", script);
            Assert.Contains("'^appdata-\\d{8}-\\d{6}\\.zip$'", script);                  // prunes only its own names
            Assert.DoesNotContain("Compress-Archive -Path (Join-Path $AppData", script);   // the live tree is never zipped
        }
    }
}
