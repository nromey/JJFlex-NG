#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Radios;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A remote radio's advertised ports, readable before connecting (#398).
    /// The radio picker's Network identity card describes the HIGHLIGHTED row:
    /// a SmartLink-listed radio shows the path its SmartLink list advertises,
    /// carried on the row's SmartLink sighting; with no row highlighted, or the
    /// connected radio highlighted, the card shows the connected radio's own
    /// lines, as the Status dialog does.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class PickerIdentityLinesTests : IDisposable
    {
        private readonly FlexBase _current = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        public PickerIdentityLinesTests() => Lexicon.Load(Lexicon.Partitions);

        public void Dispose() => _current.Dispose();

        private const string Serial = "1234-5678-6300-0398";

        [Fact]
        public void With_no_row_highlighted_the_card_describes_the_current_radio_as_before()
        {
            Assert.Equal(NetworkIdentityInfo.BuildLines(_current),
                NetworkIdentityInfo.BuildPickerLines(_current, null, null, null, null));
            Assert.Equal(NetworkIdentityInfo.BuildLines(null),
                NetworkIdentityInfo.BuildPickerLines(null, "", null, null, null));
        }

        [Fact]
        public void A_SmartLink_listed_radio_shows_its_advertised_ports_without_a_connect()
        {
            var lines = NetworkIdentityInfo.BuildPickerLines(_current, Serial, "Shack", "FLEX-6300",
                new SmartLinkAdvertised(4993, 4994, portForwardOn: true, requiresHolePunch: false));
            Assert.Equal(new[]
            {
                "Radio: FLEX-6300, name Shack",
                "Serial: " + Serial,
                Lexicon.Get("connect.identity.listed_not_connected"),
                // The connected radio's own sentence, word for word.
                "Path: forwarded ports — external TCP 4993, external UDP 4994. Radio reports port forwarding on.",
            }, lines);
        }

        [Fact]
        public void A_listed_radio_that_needs_a_hole_punch_says_so_as_the_connected_card_does()
        {
            var lines = NetworkIdentityInfo.BuildPickerLines(null, Serial, "", "FLEX-6300",
                new SmartLinkAdvertised(0, 0, portForwardOn: false, requiresHolePunch: true));
            Assert.Equal("Radio: FLEX-6300", lines[0]);
            Assert.Equal("Path: hole punch. No forwarded ports are configured at the radio's site.", lines[^1]);
        }

        [Fact]
        public void A_radio_SmartLink_does_not_list_says_there_is_nothing_advertised()
        {
            var lines = NetworkIdentityInfo.BuildPickerLines(_current, Serial, "Bench", "FLEX-8600", null);
            Assert.Equal(new[]
            {
                "Radio: FLEX-8600, name Bench",
                "Serial: " + Serial,
                Lexicon.Get("connect.identity.not_listed"),
            }, lines);
        }

        /// <summary>
        /// The sighting carries the list's numbers: built from a SmartLink
        /// list's radio object, the row's sighting has them; from a local
        /// radio, none.
        /// </summary>
        [Fact]
        public void A_SmartLink_sighting_carries_the_lists_advertised_path_and_a_local_one_none()
        {
            using var f = new RigOnVendorRadio("picker-identity-398");
            var radio = f.Vendor.Radio;
            var build = typeof(FlexBase).GetMethod("BuildRigData", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.NotNull(build);
            var isWan = typeof(Flex.Smoothlake.FlexLib.Radio).GetProperty(nameof(Flex.Smoothlake.FlexLib.Radio.IsWan))!;

            isWan.SetValue(radio, false);
            var local = (FlexBase.RigData)build.Invoke(null, new object?[] { radio, null })!;
            Assert.Null(local.Advertised);

            isWan.SetValue(radio, true);
            radio.PublicTlsPort = 21443;
            radio.PublicUdpPort = 21444;
            radio.IsPortForwardOn = true;
            radio.RequiresHolePunch = false;
            var wan = (FlexBase.RigData)build.Invoke(null, new object?[] { radio, null })!;
            Assert.NotNull(wan.Advertised);
            Assert.Equal(21443, wan.Advertised!.TlsPort);
            Assert.Equal(21444, wan.Advertised.UdpPort);
            Assert.True(wan.Advertised.PortForwardOn);
            Assert.False(wan.Advertised.RequiresHolePunch);
        }

        /// <summary>
        /// The picker reads the row's SmartLink sighting, not its RigData: a
        /// local sighting of a dual-homed radio replaces RigData with one that
        /// carries no SmartLink facts, while the SmartLink half's sighting is
        /// kept across it.
        /// </summary>
        [Fact]
        public void The_picker_reads_the_rows_SmartLink_sighting()
        {
            string src = Source("JJFlexWpf", "Dialogs", "RigSelectorDialog.xaml.cs");
            int at = src.IndexOf("private void RefreshIdentityCard()", StringComparison.Ordinal);
            string body = src.Substring(at, src.IndexOf("catch (Exception ex)", at, StringComparison.Ordinal) - at);
            Assert.Contains("(row.WanSighting as FlexBase.RigData)?.Advertised", body, StringComparison.Ordinal);
            Assert.Contains("NetworkIdentityInfo.BuildPickerLines(current, row.Serial,", body, StringComparison.Ordinal);
            Assert.Contains("if (row == null) { IdentityCard.Rig = current; return; }", body, StringComparison.Ordinal);
            // The Status dialog's copy of the card still describes its radio.
            Assert.DoesNotContain("ShowLines", Source("JJFlexWpf", "Dialogs", "StatusDialog.xaml.cs"), StringComparison.Ordinal);
        }

        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
        }
    }
}
