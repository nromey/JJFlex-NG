#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1092: under <c>$ErrorActionPreference = 'Stop'</c>, <c>Write-Error</c>
    /// throws, so a script's <c>exit 2</c> (or 3, 4 ...) on the next line never
    /// runs and the script exits 1. #872 fixed this in
    /// <c>backup-private-to-nas.ps1</c>; the same trap sat in the other backup
    /// scripts, the nightly publish script and the harness tools, where a seal
    /// reading the exit code could not tell "the NAS is offline" from any other
    /// failure. Every tracked script that sets Stop now writes its error with
    /// <c>[Console]::Error.WriteLine</c>.
    /// </summary>
    /// <remarks>
    /// The guard reads the TRACKED tree (<see cref="IntegrationPassTree.AllFiles"/>),
    /// not the disk. The run test never reaches the real NAS: both roots it passes
    /// sit in this class's own temp folder.
    /// </remarks>
    public sealed class ScriptErrorExitCodeTests : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "jjflex-1092-" + Guid.NewGuid().ToString("N"));

        public ScriptErrorExitCodeTests() => Directory.CreateDirectory(_base);

        public void Dispose()
        {
            try { Directory.Delete(_base, recursive: true); } catch { }
        }

        private static readonly Regex StopPreference =
            new(@"^\s*\$ErrorActionPreference\s*=\s*['""]Stop['""]", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        private static readonly Regex WriteErrorCall =
            new(@"(^|[;{\s])Write-Error\b", RegexOptions.IgnoreCase);

        /// <summary>The lines of <paramref name="script"/> that call Write-Error while
        /// the script sets Stop, comments left out. Empty when the script does not set Stop.</summary>
        internal static IReadOnlyList<int> TrappedLines(string script)
        {
            var hits = new List<int>();
            if (!StopPreference.IsMatch(script)) return hits;
            string[] lines = script.Replace("\r\n", "\n").Split('\n');
            bool inBlockComment = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (inBlockComment)
                {
                    int end = line.IndexOf("#>", StringComparison.Ordinal);
                    if (end < 0) continue;
                    line = line.Substring(end + 2);
                    inBlockComment = false;
                }
                int open = line.IndexOf("<#", StringComparison.Ordinal);
                if (open >= 0 && line.IndexOf("#>", open + 2, StringComparison.Ordinal) < 0)
                {
                    line = line.Substring(0, open);
                    inBlockComment = true;
                }
                int hash = line.IndexOf('#');
                string code = hash >= 0 ? line.Substring(0, hash) : line;
                if (WriteErrorCall.IsMatch(code)) hits.Add(i + 1);
            }
            return hits;
        }

        [Fact]
        public void No_tracked_script_that_stops_on_errors_calls_Write_Error()
        {
            var scripts = IntegrationPassTree.AllFiles
                .Where(f => f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.True(scripts.Count >= 10, "only " + scripts.Count + " tracked .ps1 files found; the scan looked at nothing");

            var offenders = new List<string>();
            foreach (string f in scripts)
                foreach (int line in TrappedLines(File.ReadAllText(f)))
                    offenders.Add(IntegrationPassTree.Relative(f) + ":" + line);

            Assert.True(offenders.Count == 0,
                "Write-Error under ErrorActionPreference Stop throws, so the exit code after it never runs. "
                + "Use [Console]::Error.WriteLine instead: " + string.Join(", ", offenders));
        }

        [Fact]
        public void The_guard_sees_the_trap_and_ignores_what_is_not_one()
        {
            // Positive controls: the shapes the scripts actually used.
            Assert.Equal(new[] { 3 }, TrappedLines("$ErrorActionPreference = 'Stop'\nif (-not $x) {\n    Write-Error \"missing\"\n    exit 2\n}"));
            Assert.Equal(new[] { 2 }, TrappedLines("$ErrorActionPreference = \"Stop\"\nif (-not (Test-Path $f)) { Write-Error \"Register not found: $f\"; exit 1 }"));
            // Negative controls: no Stop, a comment, a block comment, the fixed form.
            Assert.Empty(TrappedLines("Write-Error \"fine without Stop\"\nexit 2"));
            Assert.Empty(TrappedLines("$ErrorActionPreference = 'Stop'\n# Not Write-Error: it throws under Stop\n[Console]::Error.WriteLine(\"x\")\nexit 2"));
            Assert.Empty(TrappedLines("$ErrorActionPreference = 'Stop'\n<#\n Write-Error would throw here\n#>\nexit 0"));
        }

        [Fact]
        public void The_claude_state_backup_exits_2_when_its_root_is_missing()
        {
            string missing = Path.Combine(_base, "no-such-claude-root");
            string nas = Path.Combine(_base, "nas");
            // The guard this test rests on: nothing here may reach the real NAS.
            Assert.StartsWith(Path.GetFullPath(_base), Path.GetFullPath(nas), StringComparison.OrdinalIgnoreCase);

            string ps51 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            Assert.True(File.Exists(ps51), "no Windows PowerShell at " + ps51 + "; a broken test environment, not a broken script");

            var psi = new ProcessStartInfo
            {
                FileName = ps51,
                WorkingDirectory = IntegrationPassTree.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                         Path.Combine(IntegrationPassTree.Root, "backup-claude-state-to-nas.ps1"),
                         "-ClaudeRoot", missing, "-NasRoot", nas })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(60_000), "the script did not finish within a minute");

            Assert.True(p.ExitCode == 2, "expected exit 2 (Claude root not found), got " + p.ExitCode + ". stderr: " + stderr);
            Assert.Contains("Claude root not found", stderr, StringComparison.Ordinal);
            Assert.False(Directory.Exists(nas), "the script created a NAS folder before refusing");
        }
    }
}
