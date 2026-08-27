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
        /// state.
        /// </summary>
        private static readonly string[] Assignments =
        {
            "RadioConfig.BaseDirectory =",
            "KnownRadioRoster.CacheDirectory =",
            "Lexicon.OverlayDirectoryOverride =",
        };

        /// <summary>
        /// Stores that resolve the shared settings root FOR you, so a class can
        /// join the shared state without ever naming a static or a scope.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the hop the first two rules cannot see.</b> They
        /// look for an assignment or for the word
        /// <c>RadioConfigStaticsScope</c>. A class that writes neither, and
        /// simply calls <c>ConnectionHistory.Record(...)</c>, lands its file in
        /// whichever directory the statics happen to point at when the call
        /// runs — which, while another class holds a scope, is that class's
        /// private tree. Nothing conflicts, nothing throws, and the damage
        /// surfaces as an unrelated count being one too high in a class that
        /// did nothing wrong.</para>
        /// <para>Every one of these was already inside the collection when this
        /// rule was written. That is the point: the rule is what stops the
        /// eleventh from being outside it, and forgetting produces no error at
        /// all — just a test somewhere else that fails one run in five.</para>
        /// </remarks>
        private static readonly string[] ImplicitConsumers =
        {
            "ConnectionHistory.",
            "ConnectPathLearningConfig.",
            "KnownRadioRoster.",
            "Lexicon.",
            "RadioConfig.BaseDirectory",
            "RadioConfig.ResolvedBaseDirectory",
            "RadioConfig.AppDataRoot",
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

        /// <summary>
        /// The same rule as <see cref="EveryClassThatTouchesTheSettingsStaticsIsInTheCollection"/>,
        /// one hop out: a class that never names a static or a scope but reads
        /// a store which resolves the settings root on its behalf is in the
        /// shared state just the same.
        /// </summary>
        [Fact]
        public void EveryClassThatUsesAStoreUnderTheSettingsRootIsInTheCollection()
        {
            var offenders = new List<string>();
            var guarded = new List<string>();
            int scanned = 0;

            foreach (string file in TestSourceFiles())
            {
                scanned++;
                if (Exempt.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                string code = CodeOnly(File.ReadAllText(file));
                var used = ImplicitConsumers
                    .Where(c => code.Contains(c, StringComparison.Ordinal))
                    .ToList();
                if (used.Count == 0) continue;

                guarded.Add(Path.GetFileName(file));
                if (!code.Contains(Attribute, StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetFileName(file) + " (" + string.Join(", ", used) + ")");
                }
            }

            Assert.True(scanned > 20,
                "The scan saw only " + scanned + " test source files, so it was not looking at the test project.");
            Assert.True(guarded.Count >= 6,
                "The scan found only " + guarded.Count + " classes using a store under the settings root; " +
                "there are at least seven. The match strings are probably stale.");

            Assert.True(offenders.Count == 0,
                "These test classes use a store that resolves the process-wide settings root for them, " +
                "without " + Attribute + ". They will run in parallel with the classes that own that " +
                "state, and write into whichever private tree is in force at the time: " +
                string.Join("; ", offenders) +
                ". Add the attribute, or pass an explicit directory. See task #232.");
        }

        /// <summary>
        /// The scanner reads CODE. A doc comment or a message string that
        /// happens to name one of these types is not a use of it.
        /// </summary>
        /// <remarks>
        /// The positive control below is not decoration. Without the stripping
        /// this rule reports <c>IntegrationPassBaseline.cs</c> — a table of
        /// findings whose only mention of the type is inside a quoted
        /// explanation — and the fix an author would reach for is to exempt
        /// that file by name, which would silently exempt any real use added to
        /// it later.
        /// </remarks>
        [Fact]
        public void TheScannerReadsCodeAndNotCommentsOrMessageStrings()
        {
            const string sample =
                "// ConnectionHistory.Clear(serial) is what the old version did\n" +
                "/// <summary>Lexicon.Forget resets it.</summary>\n" +
                "var note = \"identical in Radios/ConnectPathLearningConfig.cs and elsewhere\";\n" +
                "var actual = ConnectionHistory.Load(serial);\n";

            string code = CodeOnly(sample);

            Assert.DoesNotContain("Lexicon.", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ConnectPathLearningConfig.", code, StringComparison.Ordinal);
            Assert.Contains("ConnectionHistory.Load", code, StringComparison.Ordinal);

            // And on the real file that motivated it, both directions: the raw
            // text matches and the stripped text does not. Asserting only the
            // second would pass just as well if the file had gone missing.
            string baselinePath = Path.Combine(TestProjectDirectory(), "IntegrationPassBaseline.cs");
            Assert.True(File.Exists(baselinePath),
                "IntegrationPassBaseline.cs is not where this test expects it, so the control below " +
                "proves nothing about the stripping.");

            string raw = File.ReadAllText(baselinePath);
            Assert.Contains("ConnectPathLearningConfig.", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("ConnectPathLearningConfig.", CodeOnly(raw), StringComparison.Ordinal);
        }

        /// <summary>
        /// Blanks line comments, doc comments, block comments and the contents
        /// of string literals, leaving the executable text. Crude on purpose —
        /// it only has to be right about whether a TYPE NAME is being used, and
        /// a parser here would be a second thing to be wrong.
        /// </summary>
        private static string CodeOnly(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            bool inString = false, inChar = false, inLine = false, inBlock = false, verbatim = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';

                if (inLine)
                {
                    if (c == '\n') { inLine = false; sb.Append(c); }
                    continue;
                }
                if (inBlock)
                {
                    if (c == '*' && next == '/') { inBlock = false; i++; }
                    continue;
                }
                if (inString)
                {
                    if (verbatim && c == '"' && next == '"') { i++; continue; }
                    if (!verbatim && c == '\\') { i++; continue; }
                    if (c == '"') { inString = false; verbatim = false; sb.Append(c); }
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') { inChar = false; sb.Append(c); }
                    continue;
                }

                if (c == '/' && next == '/') { inLine = true; i++; continue; }
                if (c == '/' && next == '*') { inBlock = true; i++; continue; }
                if (c == '"')
                {
                    inString = true;
                    verbatim = i > 0 && (text[i - 1] == '@' || (i > 1 && text[i - 2] == '@'));
                    sb.Append(c);
                    continue;
                }
                if (c == '\'') { inChar = true; sb.Append(c); continue; }

                sb.Append(c);
            }

            return sb.ToString();
        }

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
            // AllDirectories, not TopDirectoryOnly. A test class put in a
            // subfolder is not a different kind of test class, and a scanner
            // that cannot see it reports the same clean result either way.
            string dir = TestProjectDirectory();
            if (!Directory.Exists(dir)) return Array.Empty<string>();

            return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                                        StringComparison.OrdinalIgnoreCase)
                            && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                                           StringComparison.OrdinalIgnoreCase));
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
