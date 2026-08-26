using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The rule that keeps <see cref="RadioConfigStaticsScope"/> honest: a test
    /// class that touches the process-wide settings statics must be in the
    /// collection that serialises them.
    ///
    /// <para><b>Why a test and not a comment (task #232).</b>
    /// <c>KnownRadioRosterTests</c> carried this constraint as a comment —
    /// "one class, not several" — and a comment only holds while nobody adds a
    /// second class. Sprint 30 added one, hit exactly the predicted failure, and
    /// turned the comment into <see cref="RadioConfigStaticsCollection"/>. The
    /// collection is itself a convention: it works only while every future
    /// author remembers the attribute, and forgetting it produces no error at
    /// all — just a test somewhere else that fails one run in five. This closes
    /// that last hop.</para>
    ///
    /// <para>Source-scanning rather than reflection, because the thing being
    /// checked is a source-level declaration and the failure message has to name
    /// a file an author can open. <c>LexiconKeyCoverageTests</c> scans the tree
    /// the same way.</para>
    /// </summary>
    // In the collection itself, because the collision test below takes a real
    // scope: outside the collection it would race the classes it is policing
    // and report a violation that was its own.
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RadioConfigStaticsIsolationTests
    {
        /// <summary>
        /// Writing any of these is what makes a class a member of the shared
        /// state. Reading them is harmless.
        /// </summary>
        private static readonly string[] Assignments =
        {
            "RadioConfig.BaseDirectory =",
            "KnownRadioRoster.CacheDirectory =",
            "Lexicon.OverlayDirectoryOverride =",
        };

        /// <summary>
        /// Naming the type is enough. Matching <c>new RadioConfigStaticsScope(</c>
        /// was the first attempt and it found nothing, because every user writes
        /// the target-typed <c>= new(nameof(...))</c> — the positive control
        /// below is what caught that, on the first run.
        /// </summary>
        private const string ScopeUse = "RadioConfigStaticsScope";

        private const string Attribute = "[Collection(RadioConfigStaticsCollection.Name)]";

        /// <summary>
        /// The files allowed to write the statics without the attribute: the
        /// isolation machinery itself, which is not a test class.
        /// </summary>
        private static readonly string[] Exempt =
        {
            "RadioConfigStatics.cs",
            "RadioConfigStaticsCollection.cs",
            "RadioConfigStaticsIsolationTests.cs",
        };

        [Fact]
        public void EveryClassThatTouchesTheSettingsStaticsIsInTheCollection()
        {
            var offenders = new List<string>();
            var guarded = new List<string>();
            int scanned = 0;

            foreach (string file in TestSourceFiles())
            {
                scanned++;
                if (Exempt.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                string text = File.ReadAllText(file);
                bool touches = text.Contains(ScopeUse, StringComparison.Ordinal)
                               || Assignments.Any(a => text.Contains(a, StringComparison.Ordinal));
                if (!touches) continue;

                guarded.Add(Path.GetFileName(file));
                if (!text.Contains(Attribute, StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetFileName(file));
                }
            }

            // Positive control. A scan that finds nothing proves nothing — it
            // reads identically whether every class is compliant or the scanner
            // is looking at an empty directory. These two say the instrument
            // was pointed at something and can see what it is looking for.
            Assert.True(scanned > 20,
                "The scan saw only " + scanned + " test source files, so it was not looking at the test project.");
            Assert.True(guarded.Count >= 5,
                "The scan found only " + guarded.Count + " classes touching the settings statics; " +
                "there are at least six. The match strings are probably stale.");

            Assert.True(offenders.Count == 0,
                "These test classes touch the process-wide settings statics without " +
                Attribute + ", so xUnit will run them in parallel with the classes that " +
                "own the same state: " + string.Join(", ", offenders) +
                ". Add the attribute. See task #232.");
        }

        /// <summary>
        /// Nothing may hand-roll the save-and-restore any more. Six copies of
        /// one rule is six chances to omit a line, and an omitted line here
        /// fails in a different class on a later run.
        /// </summary>
        [Fact]
        public void NobodyHandRollsTheSaveAndRestore()
        {
            var offenders = new List<string>();

            foreach (string file in TestSourceFiles())
            {
                if (Exempt.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                string text = File.ReadAllText(file);
                if (Assignments.Any(a => text.Contains(a, StringComparison.Ordinal)))
                {
                    offenders.Add(Path.GetFileName(file));
                }
            }

            Assert.True(offenders.Count == 0,
                "These test classes set the settings statics directly instead of taking a " +
                "RadioConfigStaticsScope: " + string.Join(", ", offenders) +
                ". The scope takes every piece of that state together, gives it all back, " +
                "and reports a collision; a hand-rolled copy does whichever parts its author " +
                "remembered. See task #232.");
        }

        /// <summary>
        /// The collision guard, shown to fire. A guard nobody has watched trip
        /// is indistinguishable from a guard that cannot: this is the positive
        /// control for the one instrument in the scope that only speaks when
        /// something has already gone wrong.
        /// </summary>
        [Fact]
        public void TwoScopesAtOnceNameBothHolders()
        {
            using var first = new RadioConfigStaticsScope("FirstTaker");

            var collision = Assert.Throws<InvalidOperationException>(
                () => new RadioConfigStaticsScope("SecondTaker"));

            Assert.Contains("FirstTaker", collision.Message, StringComparison.Ordinal);
            Assert.Contains("SecondTaker", collision.Message, StringComparison.Ordinal);

            // The refusal must leave the first holder intact — a guard that
            // corrupts the state it protects is worse than none.
            Assert.Equal(first.Directory, Radios.RadioConfig.BaseDirectory);
        }

        private static IEnumerable<string> TestSourceFiles()
        {
            string dir = TestProjectDirectory();
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
        }

        private static string TestProjectDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "Radios.Tests", "Radios.Tests.csproj");
                if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
                dir = dir.Parent;
            }
            return "";
        }
    }
}
