using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// This project may not reach the dialogs, because it has nothing that would
    /// stop them appearing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only one test project can construct a window safely.</b>
    /// <c>JJFlexWpf.Tests</c> carries <c>DeskGuard</c>, <c>PrivateDesktop</c>,
    /// <c>QuietRun</c>, <c>TestSettingsRoot</c> and <c>ModalWatchdog</c> — a
    /// refusal to build anything unless the desktop is private, the process is
    /// silent and the settings are redirected. <c>Radios.Tests</c> has none of
    /// that, and is the project everyone runs constantly because it is the one
    /// that is safe to run. Give it a path to <c>JJFlexWpf</c> and every dialog
    /// type is one careless <c>new</c> away from the operator's screen, from a
    /// project whose whole reputation is that it cannot do that.
    /// </para>
    /// <para>
    /// <b>Written 2026-08-27 (Track G, #233) because the rule already had to be
    /// decided once and survived only as a judgement.</b> Sprint 36 Track B
    /// needed a type testable from here, and the quick route was exactly this
    /// reference; it moved the type into <c>Radios</c> instead. That was the
    /// right call and it left nothing behind — the next author faces the same
    /// choice with no reason to make the same decision, and adding a
    /// <c>ProjectReference</c> produces no error at all. The cost of being
    /// wrong is windows on Noel's desk while he is working, which has happened
    /// twice.
    /// </para>
    /// <para>
    /// A project-file assertion rather than a type-level one, because the
    /// reference is the thing that makes the mistake POSSIBLE. Catching a
    /// <c>new SomeDialog()</c> after the fact would mean the guard rail already
    /// failed.
    /// </para>
    /// </remarks>
    public sealed class DialogsStayBehindTheDeskGuardTests
    {
        /// <summary>
        /// Projects this one must not reference: the ones that define, or
        /// transitively drag in, WPF windows the app shows to the operator.
        /// </summary>
        private static readonly string[] Forbidden = { "JJFlexWpf", "JJFlexRadio" };

        [Fact]
        public void RadiosTestsCannotReachTheDialogs()
        {
            string projectPath = Path.Combine(
                IntegrationPassTree.Root, "Radios.Tests", "Radios.Tests.csproj");

            Assert.True(File.Exists(projectPath),
                "Radios.Tests.csproj is not at " + projectPath + ", so this rule is reading nothing.");

            string text = File.ReadAllText(projectPath);

            // Positive control. The file has to actually be the project file,
            // with references in it, or "no forbidden reference" is a statement
            // about an empty string.
            Assert.Contains(@"..\Radios\Radios.csproj", text, StringComparison.OrdinalIgnoreCase);

            var found = Forbidden
                .Where(p => text.Contains(@"\" + p + @"\", StringComparison.OrdinalIgnoreCase)
                            || text.Contains(p + ".csproj", StringComparison.OrdinalIgnoreCase)
                            || text.Contains(p + ".vbproj", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(found.Count == 0,
                "Radios.Tests now references " + string.Join(", ", found) + ". This project has no " +
                "DeskGuard, no private desktop, no audio suppression and no settings redirection, " +
                "and it is the project everybody runs by reflex because it is the safe one. A " +
                "reference to the dialogs puts every window in the app one `new` away from the " +
                "operator's screen. Put the type under test in Radios instead, as Sprint 36 Track B " +
                "did. See task #233.");
        }

        [Fact]
        public void TheProjectThatCanBuildWindowsStillHasTheGuard()
        {
            // The other half. The rule above is only worth having while the
            // guard it is protecting still exists — if DeskGuard were deleted
            // tomorrow, this file would go on cheerfully asserting that dialogs
            // stay behind a gate that had gone.
            string guard = Path.Combine(
                IntegrationPassTree.Root, "JJFlexWpf.Tests", "Infrastructure", "DeskGuard.cs");

            Assert.True(File.Exists(guard),
                "JJFlexWpf.Tests/Infrastructure/DeskGuard.cs is gone, so the project that CAN build " +
                "windows may no longer refuse to. This rule assumes it refuses.");

            string text = File.ReadAllText(guard);
            foreach (string condition in new[]
                     {
                         "RefusedIsolationFailed",
                         "RefusedAudioNotSuppressed",
                         "RefusedSettingsNotIsolated",
                     })
            {
                Assert.Contains(condition, text, StringComparison.Ordinal);
            }
        }
    }
}
