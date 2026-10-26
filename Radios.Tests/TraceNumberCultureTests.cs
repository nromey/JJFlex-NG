using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// #650: the <c>tuneStart:</c> and <c>tuneResult:</c> lines write their
    /// numbers the invariant way, whatever the machine's culture, and a reader
    /// that parses them invariantly gets back the numbers that were written.
    /// The same contract <c>txMeters:</c> already holds
    /// (<see cref="TxMetersTraceParserTests"/>); the supply-voltage and
    /// PA-efficiency lines are held to it in
    /// <see cref="TraceSelfDescriptionTests"/>.
    /// </summary>
    /// <remarks>
    /// Driven through the REAL tune-cycle handlers on a radioless rig, with the
    /// radio's meter readings fed through the real meter handlers, so the line
    /// read back is the line production writes. On a comma-decimal machine the
    /// old lines read <c>swrNow=1,09</c>, which no invariant reader parses as
    /// 1.09, and <c>reflW=0,112</c>, which a lenient one reads as 112.
    /// </remarks>
    public sealed class TraceNumberCultureTests
    {
        private readonly ITestOutputHelper _out;

        public TraceNumberCultureTests(ITestOutputHelper output) => _out = output;

        [Theory]
        [InlineData("de-DE")]
        [InlineData("fi-FI")]
        [InlineData("ar-SA")]
        public void The_tune_lines_are_invariant_and_read_back(string cultureName)
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            List<string> start, result;
            float expectedFwdW, expectedReflW;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                // Premise: this culture really does write the number differently.
                Assert.NotEqual(1.09f.ToString("F2", CultureInfo.InvariantCulture), 1.09f.ToString("F2"));

                using var rig = new TxMetersEvidenceTests.Rig();
                rig.Forward(47.5f);
                rig.Reflected(20.5f);
                rig.Swr(1.09f);
                int mark = rig.Mark;
                rig.Call("noteTuneCarrier", true);
                rig.Swr(1.09f);   // a reading inside the cycle, which the settled value latches
                rig.Call("noteTuneCarrier", false);
                start = rig.LinesSince(mark, "tuneStart:");
                result = rig.LinesSince(mark, "tuneResult:");
                expectedFwdW = rig.Flex.ForwardPowerWatts;
                expectedReflW = rig.Flex.ReflectedPowerWatts;
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }

            string startLine = Assert.Single(start);
            string resultLine = Assert.Single(result);
            _out.WriteLine(startLine);
            _out.WriteLine(resultLine);

            foreach (string line in new[] { startLine, resultLine })
            {
                string body = line.Substring(line.IndexOf("tune", StringComparison.Ordinal));
                Assert.DoesNotContain(",", body, StringComparison.Ordinal);
                Assert.DoesNotContain("٫", body, StringComparison.Ordinal);   // Arabic decimal separator
                Assert.DoesNotContain("−", body, StringComparison.Ordinal);   // Unicode minus
            }

            Dictionary<string, string> s = Fields(startLine);
            Assert.Equal(1.09, Number(s, "swrAtStart"), 2);
            Assert.Equal(Math.Round(expectedFwdW, 2), Number(s, "fwdW"), 2);

            Dictionary<string, string> r = Fields(resultLine);
            Assert.Equal(1.09, Number(r, "swrSettled"), 2);
            Assert.Equal(1.09, Number(r, "swrNow"), 2);
            Assert.Equal(Math.Round(expectedFwdW, 2), Number(r, "fwdW"), 2);
            Assert.Equal(Math.Round(expectedReflW, 3), Number(r, "reflW"), 3);
            Assert.True(r["elapsed"].EndsWith("ms", StringComparison.Ordinal), "elapsed has no unit: " + r["elapsed"]);
            Assert.True(long.TryParse(r["elapsed"][..^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                "elapsed is not an invariant integer: " + r["elapsed"]);
        }

        /// <summary>The <c>key=value</c> pairs of a tune line, split on spaces.</summary>
        private static Dictionary<string, string> Fields(string line)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in line.Split(' ').Where(p => p.Contains('=')))
            {
                int eq = part.IndexOf('=');
                fields[part[..eq]] = part[(eq + 1)..];
            }
            return fields;
        }

        /// <summary>A field read the way a tool reads it: invariantly, and
        /// strictly, so a comma-decimal number fails rather than misreads.</summary>
        private static double Number(Dictionary<string, string> fields, string key)
        {
            Assert.True(fields.TryGetValue(key, out string text), "no " + key + " field");
            Assert.True(double.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out double value), key + " is not an invariant number: " + text);
            return value;
        }
    }
}
