using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #326, the latency half: the antenna-list wait and the station-name wait
    /// in Start() run side by side, each inside its own unchanged bound (20 s
    /// and 45 s), instead of the station name not even being watched until the
    /// antenna list arrived. The entry's standing rule: do not just raise the
    /// timeout.
    /// </summary>
    /// <remarks>
    /// From source: Start() needs a radio. A connect on the bench, LAN and
    /// SmartLink, reads the two events' order and the total time.
    /// </remarks>
    public sealed class ConnectWaitsSideBySideTests
    {
        private static string Flex =>
            File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "Radios", "FlexBase.cs")).Replace("\r\n", "\n");

        [Fact]
        public void The_antenna_wait_no_longer_blocks_before_the_station_name_wait()
        {
            string src = Flex;
            // The old blocking wait is gone...
            Assert.DoesNotMatch(new Regex(@"if \(!await\(\(\) =>\s*\{\s*return \(\(theRadio\.RXAntList != null\)"), src);
            // ...and its bound and deadline are set up before the station-name wait begins.
            int deadline = src.IndexOf("long antennaDeadline = Environment.TickCount64 + antennaWaitMs;", StringComparison.Ordinal);
            int stationBegin = src.IndexOf("RecordEvent(\"start_station_name_wait_begin\"", StringComparison.Ordinal);
            Assert.True(deadline > 0 && deadline < stationBegin);
            Assert.Contains("const int antennaWaitMs = 20000;", src, StringComparison.Ordinal);
            Assert.Contains("int maxWaitMs = 45000;", src, StringComparison.Ordinal);
        }

        [Fact]
        public void The_station_name_loop_watches_the_antenna_list_and_ends_on_its_bound()
        {
            string src = Flex;
            int loop = src.IndexOf("while (Environment.TickCount64 < stationWaitDeadline)", StringComparison.Ordinal);
            int loopEnd = src.IndexOf("Thread.Sleep(interval);", loop, StringComparison.Ordinal);
            Assert.True(loop > 0 && loopEnd > loop);
            string body = src.Substring(loop, loopEnd - loop);
            Assert.Contains("if (AntennasHere())", body, StringComparison.Ordinal);
            Assert.Contains("else if (Environment.TickCount64 >= antennaDeadline)", body, StringComparison.Ordinal);
            Assert.Contains("antennaWaitExpired = true;", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// After the loop, the antenna list is finished inside what is left of
        /// its own bound and judged ahead of the station name, with the same
        /// three verdicts the sequential wait gave.
        /// </summary>
        [Fact]
        public void The_antenna_verdicts_come_before_the_station_names_and_are_unchanged()
        {
            string src = Flex;
            int finish = src.IndexOf("long left = antennaDeadline - Environment.TickCount64;", StringComparison.Ordinal);
            int dropped = src.IndexOf("start: connection dropped during antenna-list wait", StringComparison.Ordinal);
            int stalled = src.IndexOf("start: antenna list never arrived", StringComparison.Ordinal);
            int empty = src.IndexOf("start: radio answered with an empty RX antenna list", StringComparison.Ordinal);
            int stationCancel = src.IndexOf("// User cancelled during station-name wait.", StringComparison.Ordinal);
            Assert.True(finish > 0 && finish < dropped && dropped < stalled && stalled < empty && empty < stationCancel);
        }
    }
}
