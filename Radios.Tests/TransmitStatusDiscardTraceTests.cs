using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A transmit-status power value FlexLib cannot parse leaves a line in our
    /// diagnostic log in a Release build too (MIGRATION.md item 28).
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>Radio.ParseTransmitStatus</c> reads
    /// <c>max_power_level</c>, <c>max_internal_pa_power</c>, <c>rfpower</c>
    /// and <c>tunepower</c> with <c>int.TryParse</c>, and on a failure wrote
    /// only <c>Debug.WriteLine</c>. <c>Debug.WriteLine</c> is compiled out of
    /// a Release build, so the installed app dropped a fractional reading
    /// with no trace. In a Debug build it already reached the log: .NET hands
    /// <c>Debug</c> output to <c>Trace.Listeners</c>, where our
    /// <c>TraceRouterListener</c> sits (shown with a listener before the
    /// patch: the unpatched FlexLib's rfpower and tunepower lines arrived).
    /// So the behaviour test below cannot tell the patch from the stock code
    /// in this Debug test build; the source pin is what holds it.</para>
    /// <para><b>Scope.</b> These four fields only, out of about three hundred
    /// <c>Debug.WriteLine</c> discards in <c>Radio.cs</c>: a vendor patch
    /// stays minimal. Every occurrence is logged; a transmit status line
    /// arrives on a change, not at frame rate.</para>
    /// </remarks>
    public sealed class TransmitStatusDiscardTraceTests
    {
        private static readonly string[] Fields = { "max_power_level", "max_internal_pa_power", "rfpower", "tunepower" };

        private static string Src(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()));

        /// <summary>The text of one <c>case "field":</c> block of ParseTransmitStatus.</summary>
        private static string CaseBlock(string radio, string field)
        {
            int method = radio.IndexOf("private void ParseTransmitStatus(string s)", StringComparison.Ordinal);
            Assert.True(method >= 0, "ParseTransmitStatus is missing from Radio.cs");
            string marker = "case \"" + field + "\":";
            int start = radio.IndexOf(marker, method, StringComparison.Ordinal);
            Assert.True(start >= 0, "ParseTransmitStatus has no case for " + field);
            int end = radio.IndexOf("case \"", start + marker.Length, StringComparison.Ordinal);
            return radio.Substring(start, (end < 0 ? radio.Length : end) - start);
        }

        [Fact]
        public void Each_of_the_four_discards_is_written_with_Trace_and_marked_as_our_patch()
        {
            string radio = Src("FlexLib_API", "FlexLib", "Radio.cs");
            foreach (string field in Fields)
            {
                string block = CaseBlock(radio, field);
                Assert.Contains("Trace.WriteLine(\"Radio::ParseTransmitStatus - " + field + ": Invalid value (\" + kv + \")\");", block);
                Assert.DoesNotContain("Debug.WriteLine(", block);
                Assert.Contains("JJFlex patch", block);
                Assert.Contains("MIGRATION.md item 28", block);
            }
        }

        [Fact]
        public void Migration_lists_the_patch_as_item_28()
        {
            string migration = Src("MIGRATION.md");
            Assert.Contains("28. **A transmit power value FlexLib cannot parse is logged", migration);
        }

        private sealed class Grab : TraceListener
        {
            public readonly List<string> Lines = new();
            public override void Write(string? message) { lock (Lines) Lines.Add(message ?? ""); }
            public override void WriteLine(string? message) { lock (Lines) Lines.Add(message ?? ""); }
        }

        /// <summary>
        /// The line reaches <c>Trace.Listeners</c> for each field, and the
        /// three fields that skip a bad value keep the previous one. An
        /// unreadable <c>max_power_level</c> is not pinned here: stock FlexLib
        /// carries on with zero and sets the ceiling to zero, which this patch
        /// leaves as it was and the batch report names.
        /// </summary>
        [Fact]
        public void A_fractional_power_reading_is_logged_and_parsing_is_unchanged()
        {
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            var radio = (Flex.Smoothlake.FlexLib.Radio)Activator.CreateInstance(
                typeof(Flex.Smoothlake.FlexLib.Radio), Private, binder: null, args: new object[] { false }, culture: null)!;
            MethodInfo parse = typeof(Flex.Smoothlake.FlexLib.Radio).GetMethod("ParseTransmitStatus", Private)!;

            parse.Invoke(radio, new object[] { "rfpower=40 tunepower=10 max_internal_pa_power=100" });
            Assert.Equal(40, radio.RFPower);
            Assert.Equal(10, radio.TunePower);
            Assert.Equal(100, radio.MaxInternalPaPowerWatts);

            var grab = new Grab();
            Trace.Listeners.Add(grab);
            try
            {
                parse.Invoke(radio, new object[] { "rfpower=12.5 tunepower=7.5 max_internal_pa_power=99.9 max_power_level=55.5" });
            }
            finally { Trace.Listeners.Remove(grab); }

            List<string> lines;
            lock (grab.Lines) lines = grab.Lines.ToList();
            foreach (string field in Fields)
                Assert.Contains(lines, l => l.StartsWith("Radio::ParseTransmitStatus - " + field + ": Invalid value (", StringComparison.Ordinal));

            Assert.Equal(40, radio.RFPower);
            Assert.Equal(10, radio.TunePower);
            Assert.Equal(100, radio.MaxInternalPaPowerWatts);
        }
    }
}
