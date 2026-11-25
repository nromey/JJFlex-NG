#nullable enable
using System;
using System.Net;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The manually forwarded port pair Radio Setup's step 5 and the Settings
    /// Network tab show (#1065). FlexLib's <c>PublicUdpPort</c> answers 4991 on
    /// any radio object that is not <c>IsWan</c>, so a radio found on the local
    /// network and then described by a SmartLink list read "UDP 4991" where the
    /// list said 25678. Windowless: no dialog, no radio, no network.
    /// </summary>
    public class ManualPortPairTests
    {
        private static string Serial() =>
            "1065-" + Random.Shared.Next(1000, 9999) + "-8600-" + Random.Shared.Next(1000, 9999);

        /// <summary>
        /// A radio local discovery found, then folded a SmartLink list into, as
        /// <c>FlexBase.Wan.cs</c>'s merge does: the listed ports are STORED in
        /// it, and the list's two pairs ride on <c>AdvertisedPorts</c>.
        /// </summary>
        private static Radio LocalRadioAListDescribed(string serial, AdvertisedPorts listed)
        {
            var r = Radio.CreateFromIp("FLEX-8600", serial, "Bench", IPAddress.Parse("192.0.2.10"), "4.1.5.0");
            r.PublicTlsPort = listed.ManualTlsPort;
            r.PublicUdpPort = listed.ManualUdpPort;
            r.IsPortForwardOn = true;
            r.AdvertisedPorts = listed;
            return r;
        }

        [Fact]
        public void A_local_radio_a_list_described_reads_the_listed_udp_port_not_4991()
        {
            var listed = new AdvertisedPorts(25678, 25678, false, -1, -1, false);
            var radio = LocalRadioAListDescribed(Serial(), listed);

            // Positive control: FlexLib's own getter still says 4991 on this
            // object, so the defect is reachable through it.
            Assert.False(radio.IsWan);
            Assert.Equal(4991, radio.PublicUdpPort);

            var pair = FlexBase.ManualPortPairFor(radio, bankedWan: null);
            Assert.Equal(25678, pair.TlsPort);
            Assert.Equal(25678, pair.UdpPort);
        }

        [Fact]
        public void Separate_listed_ports_stay_one_pair()
        {
            var listed = new AdvertisedPorts(25678, 25679, true, 30000, 30001, false);
            var pair = FlexBase.ManualPortPairFor(LocalRadioAListDescribed(Serial(), listed), null);
            Assert.Equal(25678, pair.TlsPort);
            Assert.Equal(25679, pair.UdpPort);
        }

        [Fact]
        public void A_local_radio_no_list_described_reads_not_known_never_4991()
        {
            var radio = Radio.CreateFromIp("FLEX-8600", Serial(), "Bench", IPAddress.Parse("192.0.2.11"), "4.1.5.0");
            Assert.Equal(4991, radio.PublicUdpPort);

            var pair = FlexBase.ManualPortPairFor(radio, null);
            Assert.Equal(-1, pair.TlsPort);
            Assert.Equal(-1, pair.UdpPort);
        }

        [Fact]
        public void A_local_radio_no_list_described_reads_the_banked_smartlink_object_when_one_is_held()
        {
            string serial = Serial();
            var radio = Radio.CreateFromIp("FLEX-8600", serial, "Bench", IPAddress.Parse("192.0.2.12"), "4.1.5.0");
            var banked = Radio.CreateFromIpWan("FLEX-8600", serial, "Bench", IPAddress.Parse("198.51.100.7"), "4.1.5.0",
                publicTlsPort: 25678, requiresHolePunch: false, isPortForwardOn: true);
            banked.PublicUdpPort = 25678;

            var pair = FlexBase.ManualPortPairFor(radio, banked);
            Assert.Equal(25678, pair.TlsPort);
            Assert.Equal(25678, pair.UdpPort);
        }

        [Fact]
        public void A_smartlink_object_with_no_list_reads_its_own_fields()
        {
            // Rebuilt from the connection cache: TCP and the forwarding flag are
            // kept, the UDP port is not, so it reads 0, which the readers treat
            // as not known.
            var wan = Radio.CreateFromIpWan("FLEX-8600", Serial(), "Bench", IPAddress.Parse("198.51.100.8"), "4.1.5.0",
                publicTlsPort: 25678, requiresHolePunch: false, isPortForwardOn: true);
            var pair = FlexBase.ManualPortPairFor(wan, null);
            Assert.Equal(25678, pair.TlsPort);
            Assert.True(pair.UdpPort <= 0);
        }

        [Fact]
        public void No_radio_reads_not_known()
        {
            var pair = FlexBase.ManualPortPairFor(null, null);
            Assert.Equal(-1, pair.TlsPort);
            Assert.Equal(-1, pair.UdpPort);
        }

        [Fact]
        public void The_rigs_own_getters_answer_the_listed_pair()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
            try
            {
                rig.theRadio = LocalRadioAListDescribed(Serial(), new AdvertisedPorts(25678, 25678, false, -1, -1, false));
                Assert.Equal(25678, rig.PortForwardingTcpPort);
                Assert.Equal(25678, rig.PortForwardingUdpPort);

                rig.theRadio = Radio.CreateFromIp("FLEX-8600", Serial(), "Bench", IPAddress.Parse("192.0.2.13"), "4.1.5.0");
                Assert.Equal(-1, rig.PortForwardingUdpPort);
            }
            finally
            {
                rig.theRadio = null;
                rig.Dispose();
            }
        }
    }
}
