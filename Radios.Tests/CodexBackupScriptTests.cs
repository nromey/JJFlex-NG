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
    /// #575: <c>backup-codex-to-nas.ps1</c>, run for real against a FAKE Codex
    /// home folder this class builds and a scratch NAS root, both under the
    /// temp folder. It takes an allow-list (the configs, rules, skills,
    /// sessions, history and four SQLite databases), never the sign-in token or
    /// the sandbox's secrets, leaves the databases out while Codex runs and
    /// says so, leaves out and names a file it cannot read, and keeps 12.
    /// </summary>
    /// <remarks>
    /// <b>Never the real <c>.codex</c>, never the real NAS, and no file in the
    /// real <c>.codex</c> is ever opened.</b> <see cref="Run"/> refuses any root
    /// outside this class's own temp folder before it starts a process, and
    /// always passes <c>-CodexRunning</c>, so no test depends on whether Codex
    /// happens to be running. Every "secret" below is a placeholder string.
    /// </remarks>
    public sealed class CodexBackupScriptTests : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "jjflex-575-" + Guid.NewGuid().ToString("N"));
        private string Home => Path.Combine(_base, ".codex");
        private string Nas => Path.Combine(_base, "nas");
        private string History => Path.Combine(Nas, "historical", "codex");

        // Taken.
        private static readonly string[] Kept =
        {
            "config.toml", "astra.config.toml", "session_index.jsonl", "history.jsonl", "version.json", "installation_id",
            "rules/default.rules", "skills/review/SKILL.md", "sessions/2026/11/22/rollout-1.jsonl",
        };
        private static readonly string[] Databases =
        {
            "memories_1.sqlite", "goals_1.sqlite", "state_5.sqlite", "state_5.sqlite-wal", "state_5.sqlite-shm",
            "thread_history_1.sqlite",
        };

        // Never taken: the pick's list, the brief's list, and the items left
        // out because nobody judged them (the orchestrator's default-out rule).
        private static readonly string[] NeverTaken =
        {
            "auth.json", "auth.json.lock", "auth.lock", "cap_sid", "models_cache.json",
            "packages/standalone/codex.exe", "cache/models/a.bin", "tmp/x.txt", ".tmp/y.txt",
            ".sandbox/setup.log", ".sandbox-bin/helper.exe", ".sandbox-secrets/secret.bin",
            "sandbox.2026-09-19.log", "logs_2.sqlite", "logs_2.sqlite-wal", "logs_2.sqlite-shm",
            "queue_1.sqlite", ".codex-global-state.json", ".sqlite-maintenance.lock",
            "plugins/p/plugin.json", "app-server-daemon/d.json", "computer-use/c.json", "sqlite/s.db",
            "vendor_imports/v.json", "thread-writer-locks/l", "tui-thread-reference-capabilities/t.json",
            "chrome-native-hosts-v2.json",
        };

        public CodexBackupScriptTests()
        {
            Directory.CreateDirectory(Home);
            foreach (string f in Kept.Concat(Databases)) Write(f, "kept: " + f);
            foreach (string f in NeverTaken) Write(f, "placeholder, never to be taken: " + f);
        }

        public void Dispose()
        {
            try { Directory.Delete(_base, recursive: true); } catch { }
        }

        private void Write(string relative, string text)
        {
            string path = Path.Combine(Home, relative.Replace('/', Path.DirectorySeparatorChar));
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

        private (int Exit, string Output) Run(string source, string nas, bool codexRunning)
        {
            // The guard this whole class rests on: nothing here may ever reach
            // the real Codex home or the real NAS.
            string scratch = Path.GetFullPath(_base) + Path.DirectorySeparatorChar;
            Assert.StartsWith(scratch, Path.GetFullPath(nas) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(scratch, Path.GetFullPath(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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
                                         "-File", Path.Combine(RepoRoot(), "backup-codex-to-nas.ps1"),
                                         "-Source", source, "-Nas", nas, "-CodexRunning", codexRunning ? "Yes" : "No" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            Assert.True(p.WaitForExit(120_000), "the backup script did not finish in two minutes");
            return (p.ExitCode, stdout.Result + stderr.Result);
        }

        private string[] Snapshots() =>
            Directory.Exists(History)
                ? Directory.GetFiles(History, "codex-*.zip").Select(Path.GetFileName).OrderBy(n => n).ToArray()!
                : Array.Empty<string>();

        private static string[] Entries(string zip)
        {
            using var z = ZipFile.OpenRead(zip);
            return z.Entries.Where(e => !e.FullName.EndsWith("/") && !e.FullName.EndsWith("\\"))
                .Select(e => e.FullName.Replace('\\', '/')).ToArray();
        }

        private string Newest() => Path.Combine(History, Snapshots().Last());

        [Fact]
        public void With_Codex_stopped_it_takes_the_allow_list_and_the_databases_and_nothing_else()
        {
            var (exit, output) = Run(Home, Nas, codexRunning: false);
            Assert.True(exit == 0, "exit " + exit + ":\n" + output);
            var entries = Entries(Newest());
            foreach (string f in Kept.Concat(Databases)) Assert.Contains(f, entries);
            foreach (string f in NeverTaken) Assert.DoesNotContain(f, entries);
            Assert.Equal(Kept.Length + Databases.Length, entries.Length);   // and not one file more
        }

        [Fact]
        public void With_Codex_running_every_database_is_left_out_and_the_run_says_so()
        {
            var (exit, output) = Run(Home, Nas, codexRunning: true);
            Assert.True(exit == 5, "exit " + exit + ":\n" + output);
            Assert.Single(Snapshots());                     // the rest is saved all the same
            var entries = Entries(Newest());
            foreach (string f in Kept) Assert.Contains(f, entries);
            Assert.DoesNotContain(entries, e => e.Contains(".sqlite", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Codex is running", output);
            Assert.Contains("state_5.sqlite", output);      // named
        }

        [Fact]
        public void The_sign_in_token_is_never_taken_whether_or_not_Codex_runs()
        {
            foreach (bool running in new[] { false, true })
            {
                Run(Home, Nas, running);
                var entries = Entries(Newest());
                Assert.DoesNotContain(entries, e => e.StartsWith("auth", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(entries, e => e.StartsWith(".sandbox", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain("cap_sid", entries);
                System.Threading.Thread.Sleep(1100);        // the next run's name is a new second
            }
        }

        [Fact]
        public void One_file_another_process_holds_open_is_left_out_and_named_rather_than_failing_the_backup()
        {
            string held = Path.Combine(Home, "sessions", "2026", "11", "22", "rollout-1.jsonl");
            int exit; string output;
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
                (exit, output) = Run(Home, Nas, codexRunning: false);

            Assert.True(exit == 5, "exit " + exit + ":\n" + output);
            Assert.Single(Snapshots());
            var entries = Entries(Newest());
            Assert.Contains("config.toml", entries);
            Assert.DoesNotContain("sessions/2026/11/22/rollout-1.jsonl", entries);
            Assert.Contains("rollout-1.jsonl", output);     // and the run names what it left out
        }

        [Fact]
        public void The_newest_twelve_snapshots_are_kept_and_nothing_else_in_the_folder_is_touched()
        {
            Directory.CreateDirectory(History);
            for (int day = 1; day <= 14; day++)
                File.WriteAllText(Path.Combine(History, $"codex-202601{day:D2}-000000.zip"), "an older snapshot");
            File.WriteAllText(Path.Combine(History, "codex-notes.zip"), "not a snapshot this script named");
            File.WriteAllText(Path.Combine(History, "readme.txt"), "someone else's file");

            var (exit, output) = Run(Home, Nas, codexRunning: false);
            Assert.True(exit == 0, "exit " + exit + ":\n" + output);

            var kept = Snapshots().Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^codex-\d{8}-\d{6}\.zip$")).ToArray();
            Assert.Equal(12, kept.Length);
            Assert.DoesNotContain("codex-20260103-000000.zip", kept);
            Assert.Contains("codex-20260104-000000.zip", kept);
            Assert.True(File.Exists(Path.Combine(History, "codex-notes.zip")));
            Assert.True(File.Exists(Path.Combine(History, "readme.txt")));
        }

        [Fact]
        public void A_missing_Codex_home_exits_2_and_prunes_nothing()
        {
            Directory.CreateDirectory(History);
            for (int day = 1; day <= 14; day++)
                File.WriteAllText(Path.Combine(History, $"codex-202601{day:D2}-000000.zip"), "an older snapshot");
            var (exit, output) = Run(Path.Combine(_base, "no-such-folder"), Nas, codexRunning: false);
            Assert.True(exit == 2, "exit " + exit + ":\n" + output);
            Assert.Equal(14, Snapshots().Length);
        }

        [Fact]
        public void The_script_takes_an_allow_list_keeps_twelve_and_asks_whether_Codex_runs_by_default()
        {
            string script = File.ReadAllText(Path.Combine(RepoRoot(), "backup-codex-to-nas.ps1"));
            Assert.Contains("[int]    $KeepLast     = 12", script);
            Assert.Contains("[string] $CodexRunning = 'Auto'", script);
            Assert.Contains("NEVER TEST THIS AGAINST THE REAL .codex OR THE REAL NAS PATH", script);
            Assert.Contains("'^codex-\\d{8}-\\d{6}\\.zip$'", script);    // prunes only its own names
            // The allow-list lines name no token, secret or regenerable tree.
            foreach (string line in script.Split('\n').Where(l => l.TrimStart().StartsWith("$allow", StringComparison.Ordinal)
                                                                 || l.TrimStart().StartsWith("'version.json'", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain("auth", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("sandbox", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("packages", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("cap_sid", line, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
