using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The radio-list parse keeps both port pairs, each by source (MIGRATION.md
    /// item 29), and leaves FlexLib's own single choice exactly as stock made it.
    /// </summary>
    /// <remarks>
    /// Noel's ruling on the tiers plan, decision 3: "both". The connect policy
    /// needs the manual pair and the radio's own UPnP pair separately, so a
    /// dead manual forward can still fall back to a working UPnP mapping, and
    /// Tier 1 can refuse a radio that offers only UPnP ports. Every message here
    /// is parsed by the real <c>WanServer.ParseRadioListMessage</c>, offline.
    /// </remarks>
    public sealed class RadioListBothPairsTests
    {
        private static Radio Parse(string fields)
        {
            var server = new WanServer();
            List<Radio>? got = null;
            server.RadioListReceivedForThisConnection += list => got = list;
            typeof(WanServer).GetMethod("ParseRadioListMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server, new object[] { "radio list serial=1234-5678-9012-3456 public_ip=192.0.2.1 model=FLEX-8600 version=4.2.20.41343 radio_name=Bench callsign=N0CALL status=Available gui_client_ips= gui_client_hosts= gui_client_programs= gui_client_stations= gui_client_handles= " + fields });
            Assert.NotNull(got);
            return Assert.Single(got!);
        }

        [Fact]
        public void Both_pairs_are_kept_by_source_and_stock_still_prefers_the_manual_pair()
        {
            var r = Parse("public_tls_port=4994 public_udp_port=4993 upnp_supported=1 public_upnp_tls_port=21000 public_upnp_udp_port=21001");
            var a = r.AdvertisedPorts!;
            Assert.Equal(4994, a.ManualTlsPort);
            Assert.Equal(4993, a.ManualUdpPort);
            Assert.True(a.UpnpSupported);
            Assert.Equal(21000, a.UpnpTlsPort);
            Assert.Equal(21001, a.UpnpUdpPort);
            Assert.False(a.RequiresHolePunch);

            // FlexLib's own choice, unchanged.
            Assert.Equal(4994, r.PublicTlsPort);
            Assert.Equal(4993, r.PublicUdpPort);
            Assert.True(r.IsPortForwardOn);
            Assert.False(r.RequiresHolePunch);
        }

        [Fact]
        public void A_radio_with_only_UPnP_ports_keeps_them_and_says_the_manual_pair_is_absent()
        {
            var r = Parse("public_tls_port=-1 public_udp_port=-1 upnp_supported=1 public_upnp_tls_port=21000 public_upnp_udp_port=21001");
            Assert.Equal(-1, r.AdvertisedPorts!.ManualTlsPort);
            Assert.Equal(-1, r.AdvertisedPorts.ManualUdpPort);
            Assert.Equal(21000, r.AdvertisedPorts.UpnpTlsPort);
            // Stock picks the UPnP pair, as before.
            Assert.Equal(21000, r.PublicTlsPort);
            Assert.False(r.IsPortForwardOn);
            Assert.False(r.RequiresHolePunch);
        }

        [Fact]
        public void A_radio_with_no_ports_is_a_punch_in_both_records()
        {
            var r = Parse("public_tls_port=-1 public_udp_port=-1 upnp_supported=0");
            Assert.True(r.AdvertisedPorts!.RequiresHolePunch);
            Assert.True(r.RequiresHolePunch);
            Assert.Equal(-1, r.AdvertisedPorts.ManualTlsPort);
            Assert.Equal(-1, r.AdvertisedPorts.UpnpTlsPort);
        }

        [Fact]
        public void A_malformed_manual_port_is_absent_in_the_record_while_stock_keeps_its_zero()
        {
            var r = Parse("public_tls_port=abc public_udp_port=4993 upnp_supported=0");
            Assert.Equal(-1, r.AdvertisedPorts!.ManualTlsPort);
            Assert.Equal(4993, r.AdvertisedPorts.ManualUdpPort);
            // Stock's int.TryParse wrote 0 and stock then treats 0 as a forwarded port:
            // the flag reads false beside nothing usable. Pinned, because the patch
            // must not change it; the policy reads the record instead.
            Assert.Equal(0, r.PublicTlsPort);
            Assert.True(r.IsPortForwardOn);
            Assert.False(r.RequiresHolePunch);
        }

        [Fact]
        public void A_missing_manual_pair_reads_as_absent_in_the_record_while_stock_calls_it_forwarded()
        {
            // The plan's trap, pinned on stock: int.TryParse(null) writes 0, 0 is
            // not -1, so stock "forwards" to port 0 and says no punch is needed.
            var r = Parse("upnp_supported=0");
            Assert.Equal(-1, r.AdvertisedPorts!.ManualTlsPort);
            Assert.Equal(-1, r.AdvertisedPorts.ManualUdpPort);
            Assert.False(r.AdvertisedPorts.RequiresHolePunch); // the parse's own flag, kept as computed
            Assert.Equal(0, r.PublicTlsPort);
            Assert.True(r.IsPortForwardOn);
            Assert.False(r.RequiresHolePunch);
        }

        [Fact]
        public void A_zero_port_is_kept_as_reported()
        {
            var r = Parse("public_tls_port=0 public_udp_port=0 upnp_supported=0");
            Assert.Equal(0, r.AdvertisedPorts!.ManualTlsPort);
            Assert.Equal(0, r.AdvertisedPorts.ManualUdpPort);
        }

        [Fact]
        public void UPnP_ports_are_read_even_when_UPnP_is_not_supported_and_the_flag_says_so()
        {
            var r = Parse("public_tls_port=4994 public_udp_port=4993 upnp_supported=0 public_upnp_tls_port=21000 public_upnp_udp_port=21001");
            Assert.False(r.AdvertisedPorts!.UpnpSupported);
            Assert.Equal(21000, r.AdvertisedPorts.UpnpTlsPort);
            Assert.Equal(4994, r.PublicTlsPort); // stock ignores UPnP ports here, as before
        }

        [Fact]
        public void ParsePort_reads_absent_and_malformed_as_minus_one()
        {
            Assert.Equal(-1, AdvertisedPorts.ParsePort(null));
            Assert.Equal(-1, AdvertisedPorts.ParsePort(""));
            Assert.Equal(-1, AdvertisedPorts.ParsePort("4994x"));
            Assert.Equal(4994, AdvertisedPorts.ParsePort("4994"));
            Assert.Equal(-5, AdvertisedPorts.ParsePort("-5"));
        }

        private static string Src(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()));

        [Fact]
        public void The_patch_is_marked_and_listed_as_item_29_and_the_list_merge_carries_it()
        {
            Assert.Contains("MIGRATION.md item 29", Src("FlexLib_API", "FlexLib", "WanServer.cs"));
            Assert.Contains("MIGRATION.md item 29", Src("FlexLib_API", "FlexLib", "Radio.cs"));
            Assert.Contains("29. **Both advertised port pairs", Src("MIGRATION.md"));
            Assert.Contains("oldRadio.AdvertisedPorts = newRadio.AdvertisedPorts;", Src("Radios", "FlexBase.Wan.cs"));
        }
    }
}
