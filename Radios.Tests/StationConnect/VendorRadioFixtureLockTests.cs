#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// #972: <see cref="VendorRadioFixture.Status"/> parses a status and
    /// delivers it under the transport's <c>ConnectionSync</c>, as production
    /// does (FlexLib's parser runs from <c>PublishData</c>'s DataReceived
    /// dispatch, inside that lock). Until then every fixture test ran the app's
    /// status handlers outside the reader's gate, so that lock order was never
    /// exercised.
    /// </summary>
    public sealed class VendorRadioFixtureLockTests
    {
        [Fact]
        public void A_handler_of_a_parsed_status_runs_holding_the_readers_lock()
        {
            var f = new VendorRadioFixture("1234-5678-9012-3456");
            var held = new List<bool>();
            f.Radio.PropertyChanged += (_, _) => held.Add(Monitor.IsEntered(f.Transport.ConnectionSync));

            f.Status("atu status=TUNE_IN_PROGRESS");

            Assert.NotEmpty(held);                           // positive control: the status reached a handler
            Assert.All(held, h => Assert.True(h, "a status handler ran outside ConnectionSync"));
            Assert.False(Monitor.IsEntered(f.Transport.ConnectionSync), "the fixture let go of the lock afterwards");
        }

        [Fact]
        public void Status_holds_the_lock_around_both_the_parse_and_the_delivery()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "Radios.Tests", "StationConnect", "VendorRadioFixture.cs"));
            string status = SourceMember.CSharp(src, "public void Status(string body)");
            int locked = status.IndexOf("lock (Transport.ConnectionSync)", StringComparison.Ordinal);
            int parse = status.IndexOf("ParseStatusMethod.Invoke(", StringComparison.Ordinal);
            int deliver = status.IndexOf("Transport.Deliver(", StringComparison.Ordinal);
            Assert.True(locked >= 0 && parse > locked && deliver > parse,
                "the parse and the delivery must both be inside the transport's ConnectionSync");
        }
    }
}
