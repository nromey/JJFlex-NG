using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The receive duck expires on its own and nothing can end it early, and
    /// this is what says so about a method added tomorrow (#116, #535).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The design this pins.</b> A duck with a start and a stop leaves the
    /// band permanently attenuated the first time the stop is missed — an
    /// exception on the earcon path, a crash mid-tone — with nothing visibly
    /// broken to point at and no way back short of a restart. So the duck has
    /// no stop. <c>RxDuck.RequestFor</c> writes a DEADLINE, the audio thread
    /// compares it against the clock on every buffer, and the gain glides home
    /// by itself when the clock passes it. A process that dies mid-duck simply
    /// stops processing audio; a request nobody follows up expires.
    /// </para>
    /// <para>
    /// <b>Why now.</b> #535 made the duck's timing adjustable, which is exactly
    /// the kind of change that invites a "let the operator cut it short" or a
    /// "reset on preset change" — each of them a stop call in a new coat. A
    /// stop added tomorrow compiles, passes every other test and works
    /// perfectly right up to the first time it is not reached. This is the
    /// only place that would notice.
    /// </para>
    /// <para>
    /// The check is mechanical on purpose: every write to the deadline field
    /// must sit inside <c>RequestFor</c>. Not "no method is called Stop" —
    /// a stop by any other name writes the same field.
    /// </para>
    /// </remarks>
    public sealed class WarningDuckDeadlineTests
    {
        private const string DuckDeclaration = @"JJFlexWpf\RxDuck.cs";
        private const string DeadlineField = "_activeUntilTicks";
        private const string OnlyWriter = "RequestFor";

        /// <summary>
        /// A mutation of the deadline: a plain or compound assignment (not a
        /// comparison), or any Interlocked call that changes what it is handed
        /// by reference. <c>Interlocked.Read</c> is deliberately absent — it is
        /// how the audio thread reads the deadline, and it changes nothing.
        /// </summary>
        private static readonly Regex DeadlineWrite = new(
            @"(?:\bInterlocked\s*\.\s*(?:Exchange|CompareExchange|Add|Increment|Decrement|And|Or)\s*\(\s*ref\s+"
            + DeadlineField + @"\b)"
            + @"|(?:\b" + DeadlineField + @"\s*(?:[-+*/%&|^]|<<|>>)?=(?!=))",
            RegexOptions.Compiled);

        /// <summary>
        /// A MEMBER declaration, capturing its name: access modifier, optional
        /// static and field modifiers, a type (generic commas allowed), a name.
        /// Members rather than methods, because a write inside a property
        /// setter has to be attributed to that property — the first version
        /// matched only methods, and attributed a setter's write to whatever
        /// method came before it, which in the real file is RequestFor.
        /// </summary>
        private static readonly Regex MemberDeclaration = new(
            @"\b(?:public|private|internal|protected)\s+(?:static\s+)?(?:(?:readonly|volatile|const)\s+)?"
            + @"[\w<>\[\]?.]+(?:\s*,\s*[\w<>\[\]?.]+)*\s+(\w+)\b",
            RegexOptions.Compiled);

        [Fact]
        public void TheDeadlineIsWrittenOnlyByRequestFor()
        {
            string source = IntegrationPassTree.Read(IntegrationPassTree.At(DuckDeclaration));
            var writers = new List<string>();
            var findings = WriteFindings(source, writers).ToList();

            Assert.True(findings.Count == 0,
                "Something other than RequestFor changes the duck's deadline in " + DuckDeclaration
                + ":\r\n  " + string.Join("\r\n  ", findings)
                + "\r\nThe duck has no stop by design: a request writes a deadline and the audio "
                + "thread glides home when the clock passes it, so a process dying mid-duck cannot "
                + "leave the band attenuated. A second writer is a stop call in a new coat. "
                + "See tasks #116 and #535.");

            // The positive control. A scan that finds no writers at all reads
            // identically whether the rule holds or the field was renamed.
            Assert.True(writers.Count >= 1 && writers.All(w => w == OnlyWriter),
                "Expected to find the deadline written inside " + OnlyWriter + " and found "
                + writers.Count + " writer(s): [" + string.Join(", ", writers)
                + "]. The pattern has stopped matching the field or the method, so this "
                + "rule is proving nothing.");
        }

        /// <summary>
        /// The rule, shown to discriminate: the analyser must accept the real
        /// shape and reject a stop, a reset, and a write from a property.
        /// </summary>
        [Fact]
        public void TheAnalyserAcceptsRequestForAndRejectsAnyOtherWriter()
        {
            const string honest =
                "public static void RequestFor(int earconMs)\r\n"
                + "{\r\n"
                + "    long until = 1;\r\n"
                + "    long current = Interlocked.Read(ref _activeUntilTicks);\r\n"
                + "    Interlocked.CompareExchange(ref _activeUntilTicks, until, current);\r\n"
                + "}\r\n"
                + "public static float TargetGain =>\r\n"
                + "    DateTime.UtcNow.Ticks < Interlocked.Read(ref _activeUntilTicks) ? 0.5f : 1f;\r\n";
            var writers = new List<string>();
            Assert.Empty(WriteFindings(honest, writers));
            Assert.Equal(new[] { OnlyWriter }, writers);

            const string stop =
                "public static void RequestFor(int earconMs)\r\n"
                + "{\r\n"
                + "    Interlocked.Exchange(ref _activeUntilTicks, 5);\r\n"
                + "}\r\n"
                + "public static void Stop()\r\n"
                + "{\r\n"
                + "    Interlocked.Exchange(ref _activeUntilTicks, 0);\r\n"
                + "}\r\n";
            Assert.NotEmpty(WriteFindings(stop, new List<string>()));

            // A plain assignment from something that is not RequestFor — a
            // preset setter that "helpfully" resets the duck, say.
            const string resetOnChange =
                "public static void RequestFor(int earconMs)\r\n"
                + "{\r\n"
                + "    _activeUntilTicks = 5;\r\n"
                + "}\r\n"
                + "public static RxDuckTimingPreset Timing\r\n"
                + "{\r\n"
                + "    set { _timing = (int)value; _activeUntilTicks = 0; }\r\n"
                + "}\r\n";
            Assert.NotEmpty(WriteFindings(resetOnChange, new List<string>()));

            // A comparison is not a write.
            const string comparison =
                "public static void RequestFor(int earconMs)\r\n"
                + "{\r\n"
                + "    _activeUntilTicks = 5;\r\n"
                + "}\r\n"
                + "private static bool Live()\r\n"
                + "{\r\n"
                + "    return _activeUntilTicks == 0;\r\n"
                + "}\r\n";
            Assert.Empty(WriteFindings(comparison, new List<string>()));
        }

        /// <summary>
        /// Every write to the deadline that is not inside <see cref="OnlyWriter"/>.
        /// The enclosing method is the nearest declaration above the write;
        /// accepted writes append their method to <paramref name="writers"/>.
        /// </summary>
        private static IEnumerable<string> WriteFindings(string source, List<string> writers)
        {
            var declarations = MemberDeclaration.Matches(source)
                .Select(m => (Index: m.Index, Name: m.Groups[1].Value))
                .ToList();

            foreach (Match write in DeadlineWrite.Matches(source))
            {
                if (IsComment(LineAt(source, write.Index))) continue;

                string enclosing = declarations
                    .Where(d => d.Index < write.Index)
                    .Select(d => d.Name)
                    .LastOrDefault() ?? "(no enclosing member)";

                // The field's own declaration initialiser is not a runtime
                // write; nothing can reach it after type initialisation.
                if (enclosing == DeadlineField) continue;

                if (enclosing == OnlyWriter)
                    writers.Add(enclosing);
                else
                    yield return "line " + LineNumber(source, write.Index) + " in " + enclosing;
            }
        }

        private static string LineAt(string source, int index)
        {
            int start = source.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
            int end = source.IndexOf('\n', index);
            if (end < 0) end = source.Length;
            return source[start..end];
        }

        private static int LineNumber(string source, int index)
            => source.Take(index).Count(c => c == '\n') + 1;

        private static bool IsComment(string line)
        {
            string t = line.TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal)
                || t.StartsWith("*", StringComparison.Ordinal);
        }
    }
}
