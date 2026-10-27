using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #506: the lines that record speech DELIVERED and speech DROPPED are
    /// traced at one level, so any capture that holds one holds the other.
    /// </summary>
    /// <remarks>
    /// Deliveries were Verbose and drops Warning, so an ordinary capture showed
    /// what the application failed to say and not what it said - 89 drops on
    /// 2026-09-01 with no denominator. A trace line carries no level tag; the
    /// level only decides which captures include it, which is why parity is the
    /// property worth holding rather than any particular level. Read from the
    /// source because the lines fire only through a live screen reader.
    /// </remarks>
    public sealed class SpeechDeliveryTraceLevelTests
    {
        [Fact]
        public void Delivered_and_dropped_speech_are_traced_at_one_level()
        {
            var levels = new[]
            {
                LevelOf("Radios/ScreenReaderOutput.cs", "ScreenReaderOutput: Spoke '"),
                LevelOf("Radios/ScreenReaderOutput.cs", "ScreenReaderOutput: Output '"),
                LevelOf("Radios/Speech/SpeechArbiter.cs", "SpeechArbiter: dropped a salvage ({refusal})"),
                LevelOf("Radios/Speech/SpeechArbiter.cs", "SpeechArbiter: dropped a salvage (held set full"),
            };

            Assert.Single(levels.Distinct());
            Assert.NotEqual("TraceLevel.Verbose", levels[0]);
        }

        /// <summary>
        /// The TraceLevel argument of the one Tracing.TraceLine call whose
        /// message starts with <paramref name="marker"/>.
        /// </summary>
        private static string LevelOf(string relative, string marker)
        {
            string source = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), relative));
            var call = CSharpSyntaxTree.ParseText(source).GetRoot()
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
