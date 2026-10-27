using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #506: the ordinary log gets a COUNT of speech said beside the speech
    /// dropped, and detailed capture keeps every sentence.
    /// </summary>
    /// <remarks>
    /// Deliveries were Verbose and drops Warning, so an ordinary capture showed
    /// what the application failed to say and not how much it said - 89 drops
    /// on 2026-09-01 with no denominator. Raising every "Spoke" line to the
    /// ordinary level was rejected by Noel on 2026-10-27, because a log that
    /// writes every sentence all day grows fast. So the sentences stay Verbose
    /// and <see cref="SpeechTally"/> writes counts at the drop lines' level.
    /// </remarks>
    public sealed class SpeechDeliveryTraceLevelTests
    {
        [Fact]
        public void Every_spoken_sentence_stays_in_detailed_capture_only()
        {
            Assert.Equal("TraceLevel.Verbose", LevelOf("Radios/ScreenReaderOutput.cs", "ScreenReaderOutput: Spoke '"));
            Assert.Equal("TraceLevel.Verbose", LevelOf("Radios/ScreenReaderOutput.cs", "ScreenReaderOutput: Output '"));
        }

        [Fact]
        public void The_tally_is_written_at_the_drop_lines_level()
        {
            string drop = LevelOf("Radios/Speech/SpeechArbiter.cs", "SpeechArbiter: dropped a salvage ({refusal})");
            Assert.Equal(drop, LevelOf("Radios/Speech/SpeechArbiter.cs", "SpeechArbiter: dropped a salvage (held set full"));
            Assert.Equal(drop, LevelOf("Radios/Speech/SpeechTally.cs", "line"));
        }

        [Fact]
        public void Each_spoken_sentence_and_each_drop_is_counted()
        {
            // The counting calls sit beside the lines they count, so a new
            // speak or drop path that copies one copies the other.
            string output = Read("Radios/ScreenReaderOutput.cs");
            Assert.Equal(2, Count(output, "SpeechTally.Shared.NoteSaid()"));
            Assert.Equal(1, Count(output, "SpeechTally.Shared.NoteRefused()"));
            Assert.Equal(2, Count(Read("Radios/Speech/SpeechArbiter.cs"), "SpeechTally.Shared.NoteDropped()"));
            Assert.Contains("SpeechTally.Shared.Write(\"shutdown\")", output);
            Assert.Contains("SpeechTally.Shared.Write(\"disconnect\")", Read("Radios/FlexBase.cs"));
        }

        [Fact]
        public void The_tally_writes_every_ten_minutes_only_when_something_changed_and_counts_since_the_last_line()
        {
            long ms = 0;
            var lines = new List<string>();
            var tally = new SpeechTally(() => ms, () => new DateTime(2026, 10, 27, 12, 0, 0).AddMilliseconds(ms), lines.Add);

            tally.NoteSaid();
            tally.NoteDropped();
            Assert.Empty(lines);                       // inside the window: nothing yet

            ms = (long)SpeechTally.Interval.TotalMilliseconds;
            tally.NoteSaid();
            Assert.Single(lines);
            Assert.Equal(SpeechTally.Format(new DateTime(2026, 10, 27, 12, 0, 0), 2, 1, 0, "every 10 minutes"), lines[0]);

            tally.NoteRefused();
            tally.Write("disconnect");                 // a quiet tail is never lost
            Assert.Equal(2, lines.Count);
            Assert.EndsWith("0 said, 0 dropped by the arbiter, 1 refused by the reader (disconnect)", lines[1]);

            tally.Write("shutdown");                   // nothing counted since: no line
            Assert.Equal(2, lines.Count);
        }

        private static string Read(string relative) =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), relative));

        private static int Count(string text, string needle)
        {
            int n = 0;
            for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>
        /// The TraceLevel argument of the one Tracing.TraceLine call whose
        /// message argument contains <paramref name="marker"/>.
        /// </summary>
        private static string LevelOf(string relative, string marker)
        {
            var call = CSharpSyntaxTree.ParseText(Read(relative)).GetRoot()
                .DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression.ToString() == "Tracing.TraceLine"
                            && i.ArgumentList.Arguments.Count == 2
                            && i.ArgumentList.Arguments[0].ToString().Contains(marker, StringComparison.Ordinal))
                .ToList();
            Assert.True(call.Count == 1, $"expected one TraceLine for '{marker}' in {relative}, found {call.Count}");
            return call[0].ArgumentList.Arguments[1].ToString();
        }
    }
}
