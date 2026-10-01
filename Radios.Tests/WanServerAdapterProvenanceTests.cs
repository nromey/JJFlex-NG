#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The adapter stamps a radio list with the generation of the transport
    /// it was born on, at the moment that transport is created — so a
    /// callback from a transport that has since been replaced says the OLD
    /// generation however late it arrives (#619, Sol's review of Track L3).
    /// </summary>
    /// <remarks>
    /// <para><b>No network.</b> Stock <c>WanServer.Connect</c> returns at once
    /// when it already holds an <c>SslClient</c>, and an <c>SslClient</c>
    /// constructed without its ping thread touches nothing until
    /// <c>Connect</c> is called on it — which nothing here does. Each
    /// WanServer the adapter's factory hands out is pre-loaded that way, so
    /// the adapter's real dial-retire-dial path runs against instances that
    /// never reach SmartLink.</para>
    ///
    /// <para><b>Where a list enters, and why.</b> The vendor's
    /// <c>SslClient</c> read loop invokes <c>WanServer</c>'s private
    /// <c>_sslClient_MessageReceivedReady(string)</c> with each line it
    /// receives; that method parses the line, and the radio-list parser ends
    /// by raising the Sprint 35 Track K instance event (MIGRATION.md item 13)
    /// the adapter subscribes. The list tests here deliver wire text to that
    /// handler, so the list travels the vendor's own parse path to the raiser:
    /// a vendor change that kept the raiser but stopped the parser calling it
    /// goes red here, which Track L4's tests — which invoked the raiser
    /// directly — could not see (Sol's review of L4). What is NOT driven is the
    /// read loop itself and the subscription <c>Connect</c> makes to it; both
    /// are stock vendor plumbing that every message type in the product
    /// depends on.</para>
    ///
    /// <para><b>What this reaches by reflection, and why each is asserted
    /// first.</b> The vendor's <c>_sslClient</c> field, its message handler,
    /// and the private <c>OnRadioListReceivedForThisConnection</c> raiser. If
    /// a FlexLib upgrade renames any of them or drops the patch, this goes red
    /// on the lookup with a message that says so, rather than passing with
    /// nothing raised.</para>
    /// </remarks>
    public sealed class WanServerAdapterProvenanceTests
    {
        private static readonly FieldInfo? SslClientField =
            typeof(WanServer).GetField("_sslClient", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo? MessageHandler =
            typeof(WanServer).GetMethod("_sslClient_MessageReceivedReady",
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null);

        private static readonly MethodInfo? Raiser =
            typeof(WanServer).GetMethod("OnRadioListReceivedForThisConnection",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private const string Serial = "4925-1213-8600-6245";

        /// <summary>
        /// One radio, as the server's <c>radio list</c> line carries it. Every
        /// word must be <c>key=value</c>, which is the parser's own rule.
        /// </summary>
        private static readonly string RadioListLine =
            $"radio list radio_name=Bench callsign=K5NER serial={Serial} version=4.2.20.0 model=FLEX-8600 status=Available public_ip=203.0.113.9 max_licensed_version=v3";

        /// <summary>A WanServer whose Connect() returns without dialing.</summary>
        private static WanServer UndialableWanServer()
        {
            Assert.True(SslClientField != null,
                "FlexLib's WanServer no longer has a private _sslClient field, so this test cannot " +
                "keep Connect() off the network and would dial SmartLink if it went on.");
            var wan = new WanServer();
            SslClientField!.SetValue(wan, new SslClient("127.0.0.1", "1", srcPort: 0, startPingThread: false));
            return wan;
        }

        /// <summary>
        /// Deliver one line of wire text to the method the vendor's transport
        /// read loop invokes, so the list travels the parser to the raiser.
        /// </summary>
        private static void DeliverLineOn(WanServer wan, string line)
        {
            Assert.True(MessageHandler != null,
                "FlexLib's WanServer no longer has _sslClient_MessageReceivedReady(string), the method its " +
                "transport's read loop invokes for every line received, so the parse path this test drives " +
                "is not where it looks.");
            Assert.True(Raiser != null,
                "The Sprint 35 Track K patch (OnRadioListReceivedForThisConnection, MIGRATION.md item 13) " +
                "is not in the vendored WanServer, so no list could be attributed to a connection at all.");
            MessageHandler!.Invoke(wan, new object[] { line });
        }

        private static (WanServerAdapter adapter, List<WanServer> made) AdapterOverUndialableInstances()
        {
            var made = new List<WanServer>();
            var adapter = new WanServerAdapter("[test]", () =>
            {
                var wan = UndialableWanServer();
                made.Add(wan);
                return wan;
            });
            return (adapter, made);
        }

        [Fact]
        public void A_late_list_from_a_replaced_transport_carries_the_generation_it_was_born_under()
        {
            var (adapter, made) = AdapterOverUndialableInstances();
            var seen = new List<(long generation, string serials)>();
            adapter.WanRadioRadioListReceived += (_, e) =>
                seen.Add((e.ConnectionGeneration, string.Join(",", e.Radios.Select(r => r.Serial))));

            try
            {
                Assert.Equal(0, adapter.ConnectionGeneration);

                adapter.Connect();
                Assert.Equal(1, adapter.ConnectionGeneration);
                adapter.Connect();
                Assert.Equal(2, adapter.ConnectionGeneration);

                // Generation 0 from the constructor, then one instance per dial:
                // a WanServer is never reused across dials, which is what makes
                // its identity the same thing as its transport's.
                Assert.Equal(3, made.Count);

                // Sol's interleaving: the FIRST dial's transport delivers its
                // line after the second dial has begun. The vendor parses it,
                // raises the instance event, and the adapter's closure says
                // connection 1 — not the 2 that is current when it is handled.
                DeliverLineOn(made[1], RadioListLine);
                Assert.True(seen.Count == 1,
                    "The vendor's parser did not raise the per-connection list event for a radio list line, " +
                    "so no list can reach the adapter from the wire. The message handler swallows parse " +
                    "exceptions, so this is the only place that failure is visible.");
                Assert.Equal((1L, Serial), seen[0]);

                // Positive control: the live transport's line says 2.
                DeliverLineOn(made[2], RadioListLine);
                Assert.Equal(new[] { (1L, Serial), (2L, Serial) }, seen);

                // Instrument control: the same delivery path with a line the
                // parser does not route to the list raiser produces nothing,
                // so an empty `seen` above would have been a real negative
                // and not a handler that raises on any input. The vendor-side
                // negative — a parser that stops calling the raiser — cannot
                // be run here, because the vendor tree is closed to edits.
                DeliverLineOn(made[2], "radio connect_ready handle=abc serial=" + Serial);
                Assert.Equal(2, seen.Count);
            }
            finally
            {
                adapter.Dispose();
            }
        }

        /// <summary>
        /// The order that makes the owner's decision atomic with retirement:
        /// <see cref="IWanServer.ConnectionDialing"/> is raised with the new
        /// generation, and <see cref="IWanServer.ConnectionGeneration"/>
        /// already reports it, BEFORE the WanServer that will carry it is
        /// created — so by the time any list can say the new number, every
        /// subscriber has heard it (Sol's review of Track L4, #619). A
        /// redundant Connect over a connected instance dials nothing and
        /// announces nothing, as stock WanServer.Connect returns for one.
        /// </summary>
        [Fact]
        public void A_dial_is_announced_before_the_transport_that_will_carry_it_exists()
        {
            var (adapter, made) = AdapterOverUndialableInstances();
            var announced = new List<(long announced, long reported, int instancesMade)>();
            adapter.ConnectionDialing += (_, generation) =>
                announced.Add((generation, adapter.ConnectionGeneration, made.Count));

            try
            {
                adapter.Connect();
                adapter.Connect();

                // At the first announcement only the constructor's generation-0
                // instance exists; at the second, only that and the first
                // dial's. The instance for the announced generation is made
                // after.
                Assert.Equal(new[] { (1L, 1L, 1), (2L, 2L, 2) }, announced);
                Assert.Equal(3, made.Count);

                made[2].IsConnected = true;
                adapter.Connect();
                Assert.Equal(2, announced.Count);
                Assert.Equal(3, made.Count);
                Assert.Equal(2, adapter.ConnectionGeneration);
            }
            finally
            {
                adapter.Dispose();
            }
        }

        /// <summary>
        /// A retired instance's transport-state event is unhooked, so a late
        /// IsConnected edge from a dead transport cannot wake anything; the
        /// live instance's still forwards. The list event is the one that
        /// stays hooked, by design, and the test above is its evidence.
        /// </summary>
        [Fact]
        public void A_retired_instance_no_longer_forwards_its_connection_state()
        {
            var (adapter, made) = AdapterOverUndialableInstances();
            var edges = new List<string>();
            adapter.PropertyChanged += (_, e) => edges.Add(e.PropertyName ?? "");

            try
            {
                adapter.Connect();
                adapter.Connect();

                // The first dial's instance raises IsConnected — late, dead.
                made[1].IsConnected = false;
                Assert.Empty(edges);

                // The live instance's edge is forwarded.
                made[2].IsConnected = false;
                Assert.Equal(new[] { "IsConnected" }, edges);
            }
            finally
            {
                adapter.Dispose();
            }
        }
    }
}
