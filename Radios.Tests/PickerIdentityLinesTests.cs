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
                NetworkIdentityInfo.BuildPickerLines(_current, null, null, null, PickerListing.NotListed, null));
            Assert.Equal(NetworkIdentityInfo.BuildLines(null),
                NetworkIdentityInfo.BuildPickerLines(null, "", null, null, PickerListing.NotListed, null));
        }

        [Fact]
        public void A_SmartLink_listed_radio_shows_its_advertised_ports_without_a_connect()
        {
            var lines = NetworkIdentityInfo.BuildPickerLines(_current, Serial, "Shack", "FLEX-6300", PickerListing.Listed,
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
            var lines = NetworkIdentityInfo.BuildPickerLines(null, Serial, "", "FLEX-6300", PickerListing.Listed,
                new SmartLinkAdvertised(0, 0, portForwardOn: false, requiresHolePunch: true));
            Assert.Equal("Radio: FLEX-6300", lines[0]);
            Assert.Equal("Path: hole punch. No forwarded ports are configured at the radio's site.", lines[^1]);
        }

        [Fact]
        public void A_radio_SmartLink_does_not_list_says_there_is_nothing_advertised()
        {
            var lines = NetworkIdentityInfo.BuildPickerLines(_current, Serial, "Bench", "FLEX-8600", PickerListing.NotListed, null);
            Assert.Equal(new[]
            {
                "Radio: FLEX-8600, name Bench",
                "Serial: " + Serial,
                Lexicon.Get("connect.identity.not_listed"),
            }, lines);
        }

        /// <summary>
        /// Repair one, Codex B1: missing numbers are not evidence that
        /// SmartLink does not list the radio. A row a list carries, whose
        /// numbers have not reached the picker, says it is listed and that
        /// they have not arrived; and a row no list carries any more says it
        /// is not listed, even though its last SmartLink sighting still holds
        /// the old numbers.
        /// </summary>
        [Fact]
        public void Whether_SmartLink_lists_the_radio_is_the_rows_flag_not_the_presence_of_numbers()
        {
            var listedNoNumbers = NetworkIdentityInfo.BuildPickerLines(_current, Serial, "Bench", "FLEX-8600", PickerListing.Listed, null);
            Assert.Equal(Lexicon.Get("connect.identity.listed_ports_not_arrived"), listedNoNumbers[^1]);
            Assert.DoesNotContain(Lexicon.Get("connect.identity.not_listed"), listedNoNumbers);

            var leftTheList = NetworkIdentityInfo.BuildPickerLines(_current, Serial, "Bench", "FLEX-8600", PickerListing.NotListed,
                new SmartLinkAdvertised(4993, 4994, portForwardOn: true, requiresHolePunch: false));
            Assert.Equal(Lexicon.Get("connect.identity.not_listed"), leftTheList[^1]);
            Assert.DoesNotContain(leftTheList, l => l.Contains("4993", StringComparison.Ordinal));
        }

        /// <summary>
        /// Repair two (the integration pass's F1): a row nothing has asked
        /// SmartLink about yet gets neither the denial nor any numbers, even
        /// when its last SmartLink sighting still holds them.
        /// </summary>
        [Fact]
        public void A_row_nothing_has_asked_SmartLink_about_says_so_and_denies_nothing()
        {
            var lines = NetworkIdentityInfo.BuildPickerLines(_current, Serial, "Bench", "FLEX-8600", PickerListing.NotAsked,
                new SmartLinkAdvertised(4993, 4994, portForwardOn: true, requiresHolePunch: false));
            Assert.Equal(new[]
            {
                "Radio: FLEX-8600, name Bench",
                "Serial: " + Serial,
                Lexicon.Get("connect.identity.not_asked"),
            }, lines);
            Assert.DoesNotContain(Lexicon.Get("connect.identity.not_listed"), lines);
        }

        /// <summary>
        /// Repair two (the integration pass's F2): a row last seen on
        /// SmartLink gives the numbers as SmartLink last listed them, in the
        /// past tense, and never says SmartLink lists the radio now.
        /// </summary>
        [Fact]
        public void A_radio_last_seen_on_SmartLink_is_described_in_the_past_tense()
        {
            var lines = NetworkIdentityInfo.BuildPickerLines(_current, Serial, "Shack", "FLEX-6300", PickerListing.LastListed,
                new SmartLinkAdvertised(4993, 4994, portForwardOn: true, requiresHolePunch: false));
            Assert.Equal(new[]
            {
                "Radio: FLEX-6300, name Shack",
                "Serial: " + Serial,
                Lexicon.Get("connect.identity.last_listed"),
                "Path: forwarded ports — external TCP 4993, external UDP 4994. Radio reports port forwarding on.",
            }, lines);

            var noNumbers = NetworkIdentityInfo.BuildPickerLines(_current, Serial, "Shack", "FLEX-6300", PickerListing.LastListed, null);
            Assert.Equal(Lexicon.Get("connect.identity.last_listed_ports_not_arrived"), noNumbers[^1]);

            foreach (var said in lines.Concat(noNumbers))
            {
                Assert.NotEqual(Lexicon.Get("connect.identity.listed_not_connected"), said);
                Assert.NotEqual(Lexicon.Get("connect.identity.listed_ports_not_arrived"), said);
                Assert.DoesNotContain("SmartLink lists", said, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Repairs two (F1) and three (B1): the card's verdict reads the row's
        /// own passes and whether SmartLink has ANSWERED without the radio,
        /// each on its own. With both passes settled and an answer in, a row
        /// no list carries is not listed; flip any one of them and nothing
        /// may be denied. A row a list carries is listed whatever the rest
        /// say, because the list itself is the answer. Whether a list has
        /// answered is decided from the sessions' own snapshots, held by
        /// <see cref="PickerAnsweredListTests"/>.
        /// </summary>
        [Fact]
        public void The_card_denies_a_listing_only_once_SmartLink_has_been_asked()
        {
            // Everything answered: discovery settled, no pass in flight, and
            // the lists that bear on the row answered without it.
            Assert.Equal(PickerListing.NotListed, NetworkIdentityInfo.PickerListingFor(
                wanAvailable: false, wanUnconfirmed: false, discoverySettled: true, refreshInFlight: false,
                smartLinkAnsweredWithoutIt: true));

            // The row says "checking".
            Assert.Equal(PickerListing.NotAsked, NetworkIdentityInfo.PickerListingFor(
                false, false, discoverySettled: false, false, true));
            // A pass is in flight: "rechecking now" or "refreshing".
            Assert.Equal(PickerListing.NotAsked, NetworkIdentityInfo.PickerListingFor(
                false, false, true, refreshInFlight: true, true));
            // No list has answered: no session, a session signing in, or a
            // session connected whose list has not arrived (repair three).
            Assert.Equal(PickerListing.NotAsked, NetworkIdentityInfo.PickerListingFor(
                false, false, true, false, smartLinkAnsweredWithoutIt: false));

            // The list that carried it is no longer current: last listed,
            // whatever else is still settling (repair two, F2).
            Assert.Equal(PickerListing.LastListed, NetworkIdentityInfo.PickerListingFor(
                true, wanUnconfirmed: true, true, false, true));

            // A list carries it: listed, whatever else is still settling.
            Assert.Equal(PickerListing.Listed, NetworkIdentityInfo.PickerListingFor(
                true, false, false, true, false));
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
            var local = (FlexBase.RigData)build.Invoke(null, new object?[] { radio, null, null })!;
            Assert.Null(local.Advertised);

            isWan.SetValue(radio, true);
            radio.PublicTlsPort = 21443;
            radio.PublicUdpPort = 21444;
            radio.IsPortForwardOn = true;
            radio.RequiresHolePunch = false;
            var wan = (FlexBase.RigData)build.Invoke(null, new object?[] { radio, null, null })!;
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
            Assert.Contains("row.SmartLinkListing, (row.WanSighting as FlexBase.RigData)?.Advertised", body, StringComparison.Ordinal);
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
