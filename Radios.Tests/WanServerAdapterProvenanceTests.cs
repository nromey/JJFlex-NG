#nullable enable

using System;
using System.Collections.Generic;
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
    /// <para><b>What this reaches by reflection, and why each is asserted
    /// first.</b> The vendor's <c>_sslClient</c> field and its private
    /// <c>OnRadioListReceivedForThisConnection</c> raiser — the Sprint 35
    /// Track K patch, MIGRATION.md item 13. If a FlexLib upgrade renames the
    /// field or drops the patch, this goes red on the lookup with a message
    /// that says so, rather than passing with nothing raised.</para>
    /// </remarks>
    public sealed class WanServerAdapterProvenanceTests
    {
        private static readonly FieldInfo? SslClientField =
            typeof(WanServer).GetField("_sslClient", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo? Raiser =
            typeof(WanServer).GetMethod("OnRadioListReceivedForThisConnection",
                BindingFlags.NonPublic | BindingFlags.Instance);

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

        /// <summary>Raise the vendored instance event exactly where the parse path raises it.</summary>
        private static void RaiseListOn(WanServer wan)
        {
            Assert.True(Raiser != null,
                "The Sprint 35 Track K patch (OnRadioListReceivedForThisConnection, MIGRATION.md item 13) " +
                "is not in the vendored WanServer, so no list could be attributed to a connection at all.");
            Raiser!.Invoke(wan, new object[] { new List<Radio>() });
        }

        [Fact]
        public void A_late_list_from_a_replaced_transport_carries_the_generation_it_was_born_under()
        {
            var made = new List<WanServer>();
            var adapter = new WanServerAdapter("[test]", () =>
            {
                var wan = UndialableWanServer();
                made.Add(wan);
                return wan;
            });
            var seen = new List<long>();
            adapter.WanRadioRadioListReceived += (_, e) => seen.Add(e.ConnectionGeneration);

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
                // list after the second dial has begun. It arrives, and it says
                // connection 1 — not the 2 that is current when it is handled.
                RaiseListOn(made[1]);
                Assert.Equal(new long[] { 1 }, seen);

                // Positive control: the live transport's list says 2.
                RaiseListOn(made[2]);
                Assert.Equal(new long[] { 1, 2 }, seen);
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
            var made = new List<WanServer>();
            var adapter = new WanServerAdapter("[test]", () =>
            {
                var wan = UndialableWanServer();
                made.Add(wan);
                return wan;
            });
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
