#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #872: <c>backup-private-to-nas.ps1</c>, run for real against a scratch
    /// private tree and a scratch NAS root under the temp folder. One file held
    /// open by another process no longer fails the whole backup; build output
    /// is left out and Codex's logs are kept; the newest 30 snapshots are kept
    /// (ruled by Noel 2026-11-11 11:59).
    /// </summary>
    /// <remarks>
    /// <b>Never the real NAS.</b> The script's default <c>-NasRoot</c> is the
    /// real one and its pruning deletes history, so <see cref="Run"/> refuses
    /// any root outside this class's own temp folder before it starts a
    /// process. The helpers are this class's own: the script-test helper on
    /// another branch is not shared from here.
    /// </remarks>
    public sealed class PrivateBackupScriptTests : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "jjflex-872-" + Guid.NewGuid().ToString("N"));
        private string Private => Path.Combine(_base, "private");
        private string Nas => Path.Combine(_base, "nas");
        private string History => Path.Combine(Nas, "historical", "private");

        public PrivateBackupScriptTests()
        {
            Directory.CreateDirectory(Private);
            Write("tasks.md", "the register");
            Write(Path.Combine("planning", "for-claude", "codex-logs-2026-11-03", "lane-1.log"), "a codex log");
            Write(Path.Combine("planning", "for-claude", "check", "TestResults", "run.trx"), "<TestRun/>");
            Write(Path.Combine("planning", "for-claude", "check", "bin", "Debug", "tool.dll"), "build output");
            Write(Path.Combine("planning", "for-claude", "check", "obj", "project.assets.json"), "build output");
        }

        public void Dispose()
        {
            try { Directory.Delete(_base, recursive: true); } catch { }
        }

        private void Write(string relative, string text)
        {
            string path = Path.Combine(Private, relative);
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

        private (int Exit, string Output) Run(string privateRoot, string nasRoot)
        {
            // The guard this whole class rests on: nothing here may ever reach the real NAS.
            string scratch = Path.GetFullPath(_base) + Path.DirectorySeparatorChar;
            Assert.StartsWith(scratch, Path.GetFullPath(nasRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(scratch, Path.GetFullPath(privateRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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
                                         "-File", Path.Combine(RepoRoot(), "backup-private-to-nas.ps1"),
                                         "-PrivateRoot", privateRoot, "-NasRoot", nasRoot })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            Assert.True(p.WaitForExit(120_000), "the backup script did not finish in two minutes");
            return (p.ExitCode, stdout.Result + stderr.Result);
        }

        private string[] Snapshots() =>
            Directory.Exists(History)
                ? Directory.GetFiles(History, "private-*.zip").Select(Path.GetFileName).OrderBy(n => n).ToArray()!
                : Array.Empty<string>();

        private static string[] Entries(string zip)
        {
            using var z = ZipFile.OpenRead(zip);
            return z.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith("/") && !e.FullName.EndsWith("\\"))
                .Select(e => e.FullName.Replace('\\', '/')).ToArray();
        }

        private string Newest() => Path.Combine(History, Snapshots().Last());

        [Fact]
        public void A_clean_run_makes_a_snapshot_with_codex_logs_and_test_results_and_no_build_output()
        {
            var (exit, output) = Run(Private, Nas);
            Assert.True(exit == 0, "exit " + exit + ":\n" + output);
            var entries = Entries(Newest());
            Assert.Contains("tasks.md", entries);
            Assert.Contains("planning/for-claude/codex-logs-2026-11-03/lane-1.log", entries);
            Assert.Contains("planning/for-claude/check/TestResults/run.trx", entries);
            Assert.DoesNotContain(entries, e => e.Contains("/bin/") || e.Contains("/obj/"));
        }

        [Fact]
        public void One_file_another_process_holds_open_is_left_out_and_named_rather_than_failing_the_backup()
        {
            Write("held-by-codex.log", "being written");
            string held = Path.Combine(Private, "held-by-codex.log");
            int exit; string output;
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
                (exit, output) = Run(Private, Nas);

            Assert.True(exit == 5, "exit " + exit + ":\n" + output);
            Assert.Single(Snapshots());                     // the snapshot was made all the same
            var entries = Entries(Newest());
            Assert.Contains("tasks.md", entries);
            Assert.DoesNotContain("held-by-codex.log", entries);
            Assert.Contains(held, output);                  // and the run names what it left out
        }

        [Fact]
        public void The_newest_thirty_snapshots_are_kept_and_nothing_else_in_the_folder_is_touched()
        {
            Directory.CreateDirectory(History);
            for (int day = 1; day <= 32; day++)
                File.WriteAllText(Path.Combine(History, $"private-202601{day:D2}-000000.zip"), "an older snapshot");
            File.WriteAllText(Path.Combine(History, "private-notes.zip"), "not a snapshot this script named");
            File.WriteAllText(Path.Combine(History, "readme.txt"), "someone else's file");

            var (exit, output) = Run(Private, Nas);
            Assert.True(exit == 0, "exit " + exit + ":\n" + output);

            var kept = Snapshots().Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^private-\d{8}-\d{6}\.zip$")).ToArray();
            Assert.Equal(30, kept.Length);
            Assert.DoesNotContain("private-20260101-000000.zip", kept);
            Assert.DoesNotContain("private-20260103-000000.zip", kept);
            Assert.Contains("private-20260104-000000.zip", kept);
            Assert.True(File.Exists(Path.Combine(History, "private-notes.zip")));
            Assert.True(File.Exists(Path.Combine(History, "readme.txt")));
        }

        [Fact]
        public void A_missing_private_folder_exits_2_and_prunes_nothing()
        {
            Directory.CreateDirectory(History);
            for (int day = 1; day <= 32; day++)
                File.WriteAllText(Path.Combine(History, $"private-202601{day:D2}-000000.zip"), "an older snapshot");
            var (exit, output) = Run(Path.Combine(_base, "no-such-folder"), Nas);
            Assert.True(exit == 2, "exit " + exit + ":\n" + output);
            Assert.Equal(32, Snapshots().Length);
        }

        [Fact]
        public void The_default_keeps_thirty_and_the_seal_is_told_to_check_the_exit_code()
        {
            string script = File.ReadAllText(Path.Combine(RepoRoot(), "backup-private-to-nas.ps1"));
            Assert.Contains("[int]    $KeepLast    = 30", script);
            Assert.Contains("NEVER TEST THE PRUNING AGAINST THE REAL NAS PATH", script);
            Assert.DoesNotContain("Compress-Archive -Path (Join-Path $PrivateRoot", script);   // the live tree is never zipped
        }
    }
}
