using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using JJTrace;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Task #293: a declared wait budget is a DURATION, not a count of sleeps.
    ///
    /// <para><b>The defect.</b> Four separate loops in this codebase computed
    /// <c>iterations = ms / interval</c> and then slept <c>interval</c> per turn.
    /// Each turn therefore cost the sleep PLUS whatever the work in it cost, so
    /// the loop ran the right NUMBER of times and took the wrong amount of TIME.
    /// The station-name wait declared 45,000 ms and was measured in the
    /// 2026-08-26 field trace at 55.7 seconds — 24% over, and the error is not a
    /// constant: it scales with how busy the machine is, so it grows exactly
    /// when a connect is already struggling.</para>
    ///
    /// <para><b>It had already misled a caller.</b> The #212 connect heartbeat
    /// needed a ceiling, read the declared 45,000 as a duration, and would have
    /// gone silent with ten seconds of the wait still running — reproducing the
    /// silence it exists to remove. It was covered with a 1.5 margin, a fudge
    /// factor over an arithmetic error that the next caller had no way to know
    /// about. That margin is now a small additive allowance, because the budget
    /// is real.</para>
    /// </summary>
    public sealed class WaitBudgetIsADurationTests
    {
        // ------------------------------------------------------------------
        // Behaviour: the shared await helper honours a deadline
        // ------------------------------------------------------------------

        /// <summary>
        /// A condition that never comes true, evaluated by work that costs more
        /// than the poll interval, must still give up at roughly the declared
        /// budget.
        /// </summary>
        /// <remarks>
        /// The discriminator, and it is wide on purpose so a loaded machine does
        /// not make it flaky. Budget 400 ms, interval 20 ms, 40 ms of work per
        /// turn. Under the old arithmetic: 400/20 = 20 turns at 60 ms each =
        /// about 1,200 ms. Under a deadline: about 400 ms plus one final turn.
        /// The ceiling below sits between the two with 300 ms of headroom on
        /// each side.
        /// </remarks>
        [Fact]
        public void The_budget_is_wall_clock_not_a_count_of_sleeps()
        {
            const int budgetMs = 400;
            const int intervalMs = 20;
            const int workMs = 40;

            var sw = Stopwatch.StartNew();
            bool met = Tracing.await(() => { Thread.Sleep(workMs); return false; },
                                     budgetMs, intervalMs);
            sw.Stop();

            Assert.False(met);

            // It really waited: a deadline loop must not exit early either.
            Assert.True(sw.ElapsedMilliseconds >= budgetMs - 60,
                $"gave up after {sw.ElapsedMilliseconds} ms against a {budgetMs} ms budget");

            // And it did not spend the budget on sleeps alone. 900 ms is well
            // clear of the ~460 ms a deadline loop takes and well clear of the
            // ~1,200 ms the sleep-counting version took.
            Assert.True(sw.ElapsedMilliseconds < 900,
                $"a {budgetMs} ms budget took {sw.ElapsedMilliseconds} ms — the loop is "
                + "counting sleeps rather than reading the clock (task #293)");
        }

        /// <summary>
        /// A budget shorter than one poll interval still asks the question once.
        /// </summary>
        /// <remarks>
        /// Under <c>sanity = ms / interval</c> this computed zero turns and
        /// returned false having never evaluated the condition at all — so "wait
        /// up to 10 ms for X" reported that X was not true without ever looking.
        /// No caller passes a budget that small today; the loop should not be
        /// wrong for the one that eventually does.
        /// </remarks>
        [Fact]
        public void A_budget_smaller_than_the_interval_still_asks_once()
        {
            int asked = 0;
            bool met = Tracing.await(() => { asked++; return true; }, ms: 10, interval: 25);

            Assert.True(met);
            Assert.Equal(1, asked);
        }

        /// <summary>A condition already true returns immediately.</summary>
        [Fact]
        public void An_already_true_condition_does_not_sleep()
        {
            var sw = Stopwatch.StartNew();
            Assert.True(Tracing.await(() => true, 5_000, 25));
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 500,
                $"returned after {sw.ElapsedMilliseconds} ms for a condition that was already true");
        }

        // ------------------------------------------------------------------
        // The sweep, and its positive control
        // ------------------------------------------------------------------

        /// <summary>
        /// The historical text of the defect, kept verbatim so the scanner below
        /// can be made to find something before it is trusted to find nothing.
        /// </summary>
        /// <remarks>
        /// Three of these are the four real sites as they stood before this
        /// task; the fourth is the VB copy. A scanner that reports a clean tree
        /// is indistinguishable from a scanner that is looking for the wrong
        /// thing, and "I looked and found nothing" also claims the instrument
        /// would have SEEN it.
        /// </remarks>
        private static readonly string[] KnownSpecimens =
        {
            "            int sanity = ms / interval;",
            "                int iterations = maxWaitMs / interval;",
            "        Dim iterations As Integer = ms / waitMS",
            "            int turns = timeoutMs / pollMs;",
        };

        [Fact]
        public void The_sweep_finds_the_defect_it_is_looking_for()
        {
            foreach (var specimen in KnownSpecimens)
            {
                Assert.True(SleepCountingLoop.IsMatch(specimen),
                    "the sweep did not recognise a known sleep-counting loop, so a clean "
                    + "result from it would mean nothing: " + specimen);
            }
        }

        /// <summary>
        /// Lines that divide a duration by something that is not a poll interval
        /// must NOT be flagged, or the sweep becomes noise and stops being read.
        /// </summary>
        [Fact]
        public void The_sweep_does_not_flag_ordinary_arithmetic()
        {
            string[] innocent =
            {
                "int seconds = elapsedMs / 1000;",
                "double rate = bytesMs / totalMs;",
                "var perSample = windowMs / sampleCount;",
                "Tracing.TraceLine($\"waiting up to {listWaitMs / 1000}s\");",
            };

            foreach (var line in innocent)
            {
                Assert.False(SleepCountingLoop.IsMatch(line),
                    "the sweep flagged ordinary arithmetic, which is how a check gets "
                    + "ignored: " + line);
            }
        }

        /// <summary>
        /// No authored source computes a loop count from a time budget any more.
        /// </summary>
        /// <remarks>
        /// <para>Vendored FlexLib is excluded — it is not ours to correct, and a
        /// permanent failure teaches people to skip the test.</para>
        /// <para>This file is excluded because it deliberately holds the
        /// specimens above.</para>
        /// </remarks>
        [Fact]
        public void No_authored_source_counts_sleeps_instead_of_reading_the_clock()
        {
            var hits = new List<string>();

            foreach (var file in AuthoredSourceFiles())
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (IsWhollyComment(lines[i])) continue;
                    if (SleepCountingLoop.IsMatch(lines[i]))
                        hits.Add($"{Relative(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }

            Assert.True(hits.Count == 0,
                "a wait budget is being turned into a loop count again (task #293) — "
                + "honour a deadline instead:" + Environment.NewLine
                + string.Join(Environment.NewLine, hits));
        }

        /// <summary>
        /// An identifier assigned from <c>&lt;a time budget&gt; / &lt;a poll
        /// interval&gt;</c>. Both halves have to look like what they are, so
        /// ordinary division by a count or a unit conversion does not trip it.
        /// </summary>
        private static readonly Regex SleepCountingLoop = new(
            @"=\s*\(?\s*[A-Za-z_]*(?:[Mm]s|MS|[Tt]imeout|[Bb]udget|[Mm]axWait)\b\w*\s*/\s*"
            + @"[A-Za-z_]*(?:[Ii]nterval|[Pp]ollMs|[Pp]oll|[Ss]tepMs|[Tt]ickMs|[Ww]aitMS)\b\w*",
            RegexOptions.Compiled);

        /// <summary>
        /// A line that is nothing but a comment. Skipped, because the fix for
        /// this defect is documented AT each site by quoting the old code, and a
        /// sweep that flagged its own postmortems would be permanently red — at
        /// which point nobody reads it.
        /// </summary>
        /// <remarks>
        /// A line with real code and a trailing comment is still scanned; only
        /// wholly-comment lines are exempt, and no loop has ever lived in one.
        /// </remarks>
        private static bool IsWhollyComment(string line)
        {
            var t = line.TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal)
                || t.StartsWith("'", StringComparison.Ordinal)
                || t.StartsWith("*", StringComparison.Ordinal)
                || t.StartsWith("/*", StringComparison.Ordinal)
                || t.StartsWith("<", StringComparison.Ordinal);
        }

        private static IEnumerable<string> AuthoredSourceFiles()
        {
            string root = RepoRoot();
            string[] skipDirs =
            {
                Path.Combine(root, "FlexLib_API"),
                Path.Combine(root, ".git"),
            };

            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".vb", StringComparison.OrdinalIgnoreCase)) continue;

                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;

                if (skipDirs.Any(d => file.StartsWith(d, StringComparison.OrdinalIgnoreCase))) continue;

                // This file carries the specimens on purpose.
                if (Path.GetFileName(file).Equals("WaitBudgetIsADurationTests.cs",
                        StringComparison.OrdinalIgnoreCase)) continue;

                yield return file;
            }
        }

        private static string Relative(string file)
        {
            string root = RepoRoot();
            return file.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)
                : file;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
