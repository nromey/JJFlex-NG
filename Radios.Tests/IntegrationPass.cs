using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The integration pass: the check that runs AFTER a sprint's tracks have
    /// merged, looking for the damage a merge cannot cause a conflict about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect class is DUPLICATION, and it produces no merge conflict
    /// and no build error.</b> Two agents implementing one idea in two files
    /// conflict about nothing: git is content, the compiler is content, both
    /// implementations work. On the evening of 2026-08-25 that happened five
    /// times — <c>HostApiPhrase</c>, the <c>speakNow</c>/<c>speakDone</c> pair,
    /// the <c>Sequence</c>/<c>RunId</c> resume semantics, tune power read but
    /// never displayed, and <c>JJTrace/SessionArchive</c>. Every pair sat in
    /// DISJOINT files, so an orchestrator watching file assignments would have
    /// passed all five.
    /// </para>
    /// <para>
    /// <b>Run the whole pass with one filter:</b>
    /// <c>dotnet test Radios.Tests/Radios.Tests.csproj -c Debug -p:Platform=x64
    /// --filter "FullyQualifiedName~IntegrationPass"</c>. Never the bare
    /// <c>dotnet test</c> — at solution scope it constructs real WPF dialogs on
    /// the operator's desktop.
    /// </para>
    /// <para>
    /// <b>Findings are gated against a frozen baseline, not against zero.</b>
    /// Several of the rules describe damage that is real today and belongs to a
    /// track that has not merged yet; a rule that fails on arrival gets deleted
    /// rather than read. So the baseline lists what was open at the sprint's
    /// base commit, each entry naming the task that owns it, and the gate fails
    /// in BOTH directions: a finding not in the baseline is new damage, and a
    /// baseline entry with no finding behind it has been fixed and must be
    /// struck out. The second half is the one that matters over time — a
    /// baseline nobody shrinks is the OPEN WORK REGISTER that drifted from 34
    /// to 77 in nine days without anything noticing.
    /// </para>
    /// <para>
    /// <b>Every rule also writes its findings to <c>C:\temp\integration-pass</c></b>
    /// so the pass has something a person can read, including the entries the
    /// gate is currently tolerating. A green test that silently holds twelve
    /// known defects would be its own kind of dishonest instrument.
    /// </para>
    /// </remarks>
    internal static class IntegrationPass
    {
        /// <summary>
        /// Rule names. Constants because the name appears in the detector, in
        /// the baseline and in the report, and three strings that must agree is
        /// exactly the shape <c>FixerWireContractTests</c> exists to catch.
        /// </summary>
        internal static class Rules
        {
            internal const string SkipAfterResult = "skip-control-on-a-completed-stage";
            internal const string ForwardAffordance = "no-way-on-from-this-stage";
            internal const string HeadingLevels = "heading-level-skipped";
            internal const string UnnamedControl = "operable-control-with-no-name";
            internal const string FocusableProse = "focusable-element-that-is-only-prose";
            internal const string DuplicateProse = "one-sentence-written-in-two-places";
            internal const string DuplicateBody = "one-method-implemented-in-two-places";
            internal const string CompetingVocabulary = "two-words-for-one-thing";
            internal const string SingleCallerHelper = "prose-helper-with-one-caller";
            internal const string ReflectedThreshold = "reflected-power-threshold-not-shared";
            internal const string ShadowedNamespace = "radios-namespace-shadows-a-system-one";
            internal const string ClickOnlyCheckBox = "checkbox-wired-to-click-only";
            internal const string SilentKeying = "keying-path-that-cannot-announce-itself";
            internal const string PhantomSymbol = "instruction-names-a-symbol-the-tree-lacks";
            internal const string PhantomPath = "instruction-names-a-file-the-tree-lacks";
        }

        /// <summary>One thing the pass noticed.</summary>
        /// <param name="Rule">Which detector produced it.</param>
        /// <param name="Where">The stable identity — a path, a symbol, a stage.
        /// The baseline is keyed on this, so it must NOT carry a measurement or
        /// a count that legitimate edits change.</param>
        /// <param name="What">The prose. Free to be reworded at any time.</param>
        internal sealed record Finding(string Rule, string Where, string What)
        {
            internal string Key => Rule + " :: " + Where;
        }

        /// <summary>A finding that was already true at the sprint's base
        /// commit, with the task that owns putting it right.</summary>
        internal sealed record Known(string Rule, string Where, string Task, string Why)
        {
            internal string Key => Rule + " :: " + Where;
        }

        /// <summary>
        /// Compare a rule's findings against the baseline, write the report,
        /// and fail on any movement in either direction.
        /// </summary>
        /// <param name="rule">One of <see cref="Rules"/>.</param>
        /// <param name="whatTheRuleMeans">One sentence, printed at the top of
        /// the report and in the failure. Whoever reads a red build here did
        /// not write the rule and may never have heard of the pass.</param>
        /// <param name="found">Everything the detector saw this run.</param>
        internal static void Gate(string rule, string whatTheRuleMeans, IEnumerable<Finding> found)
        {
            Finding[] all = found.OrderBy(f => f.Where, StringComparer.Ordinal).ToArray();

            foreach (Finding f in all)
                if (f.Rule != rule)
                    throw new InvalidOperationException(
                        "Rule \"" + rule + "\" produced a finding labelled \"" + f.Rule
                        + "\". The label is the baseline key, so a mismatch would silently "
                        + "make the gate unable to recognise its own findings.");

            Known[] known = IntegrationPassBaseline.For(rule);
            var knownByKey = known.ToDictionary(k => k.Key, StringComparer.Ordinal);
            var foundKeys = new HashSet<string>(all.Select(f => f.Key), StringComparer.Ordinal);

            Finding[] fresh = all.Where(f => !knownByKey.ContainsKey(f.Key)).ToArray();
            Known[] gone = known.Where(k => !foundKeys.Contains(k.Key)).ToArray();

            WriteReport(rule, whatTheRuleMeans, all, knownByKey, gone);

            if (fresh.Length == 0 && gone.Length == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("INTEGRATION PASS — " + rule);
            sb.AppendLine(whatTheRuleMeans);
            sb.AppendLine();

            if (fresh.Length > 0)
            {
                sb.AppendLine("NEW — " + fresh.Length + " finding(s) the baseline does not cover. "
                            + "Either something merged this sprint introduced them, or the "
                            + "detector has been sharpened and is seeing further than it did. "
                            + "Read them before deciding which:");
                foreach (Finding f in fresh)
                {
                    sb.AppendLine("  * " + f.Where);
                    sb.AppendLine("      " + f.What);
                }
                sb.AppendLine();
            }

            if (gone.Length > 0)
            {
                sb.AppendLine("FIXED — " + gone.Length + " baseline entr(y/ies) no longer found. "
                            + "This is good news and it is still a failure, because a baseline "
                            + "nobody shrinks stops being a record of anything. Delete these lines "
                            + "from IntegrationPassBaseline and the gate goes green:");
                foreach (Known k in gone)
                    sb.AppendLine("  * " + k.Rule + " :: " + k.Where + "   (" + k.Task + ")");
                sb.AppendLine();
            }

            sb.AppendLine("Full report: " + ReportPathFor(rule));
            Assert.Fail(sb.ToString());
        }

        /// <summary>Where the pass leaves something a person can read.</summary>
        private const string ReportDir = @"C:\temp\integration-pass";

        private static string ReportPathFor(string rule)
            => Path.Combine(ReportDir, rule + ".txt");

        private static void WriteReport(string rule, string meaning, IReadOnlyList<Finding> all,
                                        IReadOnlyDictionary<string, Known> known,
                                        IReadOnlyList<Known> gone)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Integration pass — " + rule);
            sb.AppendLine(new string('-', 70));
            sb.AppendLine(meaning);
            sb.AppendLine();
            sb.AppendLine("Written " + DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                        + " from " + IntegrationPassTree.Root);
            sb.AppendLine("Findings this run: " + all.Count
                        + "   Baseline entries: " + known.Count);
            sb.AppendLine();

            if (all.Count == 0) sb.AppendLine("Nothing found.");

            foreach (Finding f in all)
            {
                bool baselined = known.TryGetValue(f.Key, out Known? k);
                sb.AppendLine((baselined ? "[known " + k!.Task + "] " : "[NEW] ") + f.Where);
                sb.AppendLine("    " + f.What);
                if (baselined) sb.AppendLine("    baseline says: " + k!.Why);
                sb.AppendLine();
            }

            foreach (Known k in gone)
            {
                sb.AppendLine("[FIXED — delete from the baseline] " + k.Where);
                sb.AppendLine("    was: " + k.Why + "   (" + k.Task + ")");
                sb.AppendLine();
            }

            try
            {
                Directory.CreateDirectory(ReportDir);
                File.WriteAllText(ReportPathFor(rule), sb.ToString());
            }
            catch (IOException)
            {
                // The report is an aid; the gate is the check. Losing the file
                // must not turn a passing rule red, and must not turn a failing
                // one green either — the failure message carries the findings.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
