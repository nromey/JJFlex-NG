using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The release build's NAS publish publishes what it was asked to, and
    /// fails out loud when it does not (#323).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> <c>build-installers.bat</c> passed
    /// <c>-Archs x64,x86</c> through <c>powershell -File</c>, which binds that
    /// as ONE string, so the script looked for an installer named
    /// <c>..._x64,x86.exe</c>, warned, and printed "Publish complete". The
    /// batch never checked an exit code. Every "both" release published
    /// nothing, silently.
    /// </para>
    /// <para>
    /// These read the two scripts as text: running either here would publish
    /// to the NAS. The behaviour was exercised by hand against scratch roots
    /// (<c>-NasRoot</c> and <c>-ProjectRoot</c>) in the x64, x86 and both
    /// modes, with an installer missing, with none, and with the NAS
    /// unreachable; see the commit that added this file.
    /// </para>
    /// </remarks>
    public sealed class PublishToNasScriptTests
    {
        private static string Read(string name) =>
            File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), name));

        [Fact]
        public void The_architecture_list_is_split_inside_the_script()
        {
            string ps1 = Read("publish-to-nas.ps1");
            int split = ps1.IndexOf("$Archs = @($Archs | ForEach-Object { $_ -split ',' }", StringComparison.Ordinal);
            int loop = ps1.IndexOf("foreach ($arch in $Archs)", StringComparison.Ordinal);
            Assert.True(split > 0, "publish-to-nas.ps1 no longer splits -Archs; \"x64,x86\" from the batch file binds as one name again");
            Assert.True(loop > split, "the split must happen before the publish loop reads the list");
        }

        [Fact]
        public void A_missing_architecture_and_an_unreachable_NAS_both_exit_non_zero()
        {
            string ps1 = Read("publish-to-nas.ps1");
            Assert.DoesNotContain("exit 0", ps1, StringComparison.Ordinal);
            Assert.Contains("NOTHING WAS PUBLISHED.", ps1, StringComparison.Ordinal);
            Assert.Contains("    exit 2", ps1, StringComparison.Ordinal);
            Assert.Contains("$missing += $arch", ps1, StringComparison.Ordinal);
            int incomplete = ps1.IndexOf("if ($missing.Count -gt 0) {", StringComparison.Ordinal);
            int complete = ps1.IndexOf("Write-Host \"Publish complete (", StringComparison.Ordinal);
            Assert.True(incomplete > 0 && complete > incomplete,
                "the missing-architecture failure must be decided before anything says the publish is complete");
            Assert.Contains("    exit 1", ps1.Substring(incomplete, complete - incomplete), StringComparison.Ordinal);
        }

        [Fact]
        public void Every_write_goes_under_the_NasRoot_parameter()
        {
            string ps1 = Read("publish-to-nas.ps1");
            Assert.Contains("[string] $NasRoot = '\\\\nas.macaw-jazz.ts.net\\jjflex'", ps1, StringComparison.Ordinal);
            Assert.Contains("$nas         = $NasRoot", ps1, StringComparison.Ordinal);
            // The NAS path appears once, as the parameter's default, and
            // nowhere a write could reach it without going through $nas.
            int first = ps1.IndexOf("nas.macaw-jazz.ts.net", StringComparison.Ordinal);
            Assert.Equal(-1, ps1.IndexOf("nas.macaw-jazz.ts.net", first + 1, StringComparison.Ordinal));
        }

        [Fact]
        public void The_batch_file_fails_when_the_publish_does_and_catches_a_negative_exit_code()
        {
            string bat = Read("build-installers.bat");
            int call = bat.IndexOf("publish-to-nas.ps1\" -ProjectRoot", StringComparison.Ordinal);
            Assert.True(call > 0, "the publish call has moved; re-anchor this rule");
            int capture = bat.IndexOf("set \"PUBRC=!ERRORLEVEL!\"", call, StringComparison.Ordinal);
            Assert.True(capture > call, "the batch file does not capture the publish's exit code");
            string after = bat.Substring(capture);
            Assert.Contains("if not \"!PUBRC!\"==\"0\" (", after, StringComparison.Ordinal);
            Assert.Contains("exit /b 4", after, StringComparison.Ordinal);
            // "if errorlevel 1" is a greater-or-equal test, so it would pass a
            // powershell -File that failed before the script ran (a negative code).
            foreach (string line in bat.Substring(call).Split('\n'))
                Assert.False(line.TrimStart().StartsWith("if errorlevel", StringComparison.OrdinalIgnoreCase),
                    "the publish is checked with \"if errorlevel\" again: " + line.Trim());
        }
    }
}
