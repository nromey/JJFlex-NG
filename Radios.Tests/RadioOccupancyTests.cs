using System;
using System.IO;
using Flex.Smoothlake.FlexLib;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #394: the roster row tells the operator whether anyone is already on a
    /// radio, BEFORE they connect — and before they key a transmitter at
    /// somebody else's site. MultiFlex admits a second client, but transmit is
    /// a mutex, so "will I know if Don's connected before I try?" must be
    /// answerable from the picker.
    /// </summary>
    /// <remarks>
    /// <para><b>What the data turned out to be.</b> Both pre-connect sighting
    /// channels carry the answer: the LAN VITA discovery broadcast and the
    /// SmartLink radio list each deliver gui_client_stations, handles and
    /// programs, which FlexLib parses into <c>Radio.GuiClients</c>. This file
    /// pins the vendor parse we rely on, the assembled words, and — by source
    /// scan, because the plumbing crosses a VB file and a WPF dialog no unit
    /// test constructs — each hand-off the fact travels through. A break in
    /// any hand-off fails toward SILENCE, which reads as "nobody is on it";
    /// that is why the hand-offs are pinned individually.</para>
    ///
    /// <para><b>The empty case must stay COMPLETELY silent.</b> The row is
    /// read in full on every arrow keypress, on every radio the operator owns,
    /// and most radios have nobody on them. Any wording for "unoccupied" is a
    /// tax on every keypress forever.</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RadioOccupancyTests
    {
        // ------------------------------------------------------------------
        // The words — assembled clauses, because fragments read fine in a
        // diff and land badly in the ear
        // ------------------------------------------------------------------

        [Fact]
        public void Nobody_connected_is_silence()
        {
            Assert.Equal("", OccupancyPhrase.RowSuffix(null));
            Assert.Equal("", OccupancyPhrase.RowSuffix(Array.Empty<string>()));
        }

        [Fact]
        public void One_named_client_is_a_count_and_a_name()
        {
            Assert.Equal(", one other client, k5ner",
                OccupancyPhrase.RowSuffix(new[] { "k5ner" }));
        }

        /// <summary>
        /// A fresh client's station name arrives in a LATER update (#402 saw
        /// the empty-station window in the field), and a nameless occupant
        /// holds the transmit slice exactly as firmly as a named one.
        /// Whitespace is the same absence in a different coat.
        /// </summary>
        [Fact]
        public void One_client_with_no_station_name_still_counts()
        {
            Assert.Equal(", one other client", OccupancyPhrase.RowSuffix(new[] { "" }));
            Assert.Equal(", one other client", OccupancyPhrase.RowSuffix(new[] { "  " }));
        }

        [Fact]
        public void Two_named_clients_read_as_a_pair()
        {
            Assert.Equal(", 2 other clients, k5ner and don",
                OccupancyPhrase.RowSuffix(new[] { "k5ner", "don" }));
        }

        [Fact]
        public void A_partly_named_pair_names_who_it_can()
        {
            Assert.Equal(", 2 other clients, k5ner",
                OccupancyPhrase.RowSuffix(new[] { "k5ner", "" }));
        }

        [Fact]
        public void Two_nameless_clients_are_still_two_clients()
        {
            Assert.Equal(", 2 other clients",
                OccupancyPhrase.RowSuffix(new[] { "", "" }));
        }

        // ------------------------------------------------------------------
        // The row — the sentence a screen reader actually says
        // ------------------------------------------------------------------

        private static string Row(string occupancy) =>
            Lexicon.Get("connect.row.display",
                ("fav", ""), ("autoConn", ""), ("lbw", ""),
                ("namePart", "6300inshack"), ("modelPart", "FLEX-6300"),
                ("whereText", "remote via SmartLink"),
                ("occupancy", occupancy));

        /// <summary>
        /// Adding the occupancy token changed NOTHING for the common case.
        /// This is the sentence the row spoke before #394 existed, verbatim.
        /// </summary>
        [Fact]
        public void The_unoccupied_row_reads_exactly_as_it_always_has()
        {
            Assert.Equal("6300inshack, FLEX-6300, remote via SmartLink", Row(""));
        }

        /// <summary>
        /// The occupied row appends who is on it and nothing more — no
        /// MultiFlex tutorial, no "available". The operator is a licensed ham
        /// with a MultiFlex radio; the row's job is who, not what.
        /// </summary>
        [Fact]
        public void The_occupied_row_appends_who_is_on_it_and_nothing_more()
        {
            Assert.Equal(
                "6300inshack, FLEX-6300, remote via SmartLink, one other client, k5ner",
                Row(OccupancyPhrase.RowSuffix(new[] { "k5ner" })));
        }

        // ------------------------------------------------------------------
        // The vendor parse — the pre-connect fact this whole feature stands
        // on. Both sighting channels funnel through this one method
        // (Discovery for the LAN broadcast, WanServer.ParseRadioListMessage
        // for the SmartLink list), so its behaviour IS the data contract.
        //
        // Reached by reflection, because the Discovery class is internal to
        // vendored FlexLib and this repo does not edit vendor code to make a
        // test compile. If a FlexLib upgrade renames or moves the method, the
        // helper below fails with its own words instead of silently proving
        // nothing.
        // ------------------------------------------------------------------

        private static System.Collections.IList ParseClients(
            string programsCsv, string stationsCsv, string handlesCsv)
        {
            var discovery = typeof(GUIClient).Assembly
                .GetType("Flex.Smoothlake.FlexLib.Discovery");
            Assert.True(discovery != null,
                "Flex.Smoothlake.FlexLib.Discovery is gone. The pre-connect occupancy fact "
                + "is parsed there; find where ParseGuiClientsFromDiscovery moved before "
                + "trusting the roster row again.");

            var method = discovery.GetMethod("ParseGuiClientsFromDiscovery",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.True(method != null,
                "Discovery.ParseGuiClientsFromDiscovery is gone. Both sighting channels "
                + "funnelled the GUI-client CSVs through it; the roster row's occupancy "
                + "clause stands on whatever replaced it.");

            return (System.Collections.IList)method.Invoke(
                null, new object[] { programsCsv, stationsCsv, handlesCsv });
        }

        [Fact]
        public void The_discovery_payload_yields_one_client_per_csv_entry()
        {
            var clients = ParseClients(
                "SmartSDR-Windows,SmartSDR-iOS", "K5NER,DON", "0x40000001,0x40000002");

            Assert.Equal(2, clients.Count);
            Assert.Equal("K5NER", ((GUIClient)clients[0]).Station);
            Assert.Equal("DON", ((GUIClient)clients[1]).Station);
            Assert.Equal(0x40000001u, ((GUIClient)clients[0]).ClientHandle);
            Assert.Equal("SmartSDR-iOS", ((GUIClient)clients[1]).Program);
        }

        /// <summary>
        /// The radio transmits spaces in station names as DEL (U+007F); the
        /// parse turns them back. A station named "K5NER MOBILE" must not be
        /// spoken as one glued word with a control character in it.
        /// </summary>
        [Fact]
        public void The_discovery_payload_restores_spaces_in_station_names()
        {
            var clients = ParseClients(
                "SmartSDR-Windows", "K5NER\u007fMOBILE", "0x40000001");

            Assert.Equal(1, clients.Count);
            Assert.Equal("K5NER MOBILE", ((GUIClient)clients[0]).Station);
        }

        /// <summary>
        /// Empty CSVs — the fields a radio with no clients broadcasts — parse
        /// to an empty list, which the row renders as silence. This is the
        /// common case for every radio the operator owns and nobody visits.
        /// </summary>
        [Fact]
        public void An_empty_payload_parses_to_nobody()
        {
            Assert.Equal(0, ParseClients("", "", "").Count);
            Assert.Equal(0, ParseClients(null, null, null).Count);
        }

        /// <summary>
        /// A torn payload (CSV lengths disagree) parses to an empty list —
        /// the vendor fails toward silence, not toward an invented roster.
        /// Silence never claims "nobody is on it" out loud; it just declines
        /// to claim anyone is. That is the acceptable direction, and this pin
        /// is here so an upstream change in that direction gets noticed.
        /// </summary>
        [Fact]
        public void A_torn_payload_parses_to_nobody_rather_than_to_guesses()
        {
            Assert.Equal(0,
                ParseClients("SmartSDR-Windows", "K5NER,DON", "0x40000001").Count);
        }

        // ------------------------------------------------------------------
        // The hand-offs — scanned in source, because the chain crosses a VB
        // entry point and a WPF dialog no unit test constructs, and every
        // dropped baton in this chain reads as "nobody is on it"
        // ------------------------------------------------------------------

        private const string Globals = "globals.vb";
        private const string Dialog = "JJFlexWpf/Dialogs/RigSelectorDialog.xaml.cs";
        private const string Flex = "Radios/FlexBase.cs";

        /// <summary>
        /// FlexBase snapshots the client list into every RigData it raises,
        /// under the vendor's own lock, and watches LAN radios for changes —
        /// a LAN radio raises RadioFound once at first sighting, so without
        /// the watch an owner connecting later never reaches the row.
        /// </summary>
        [Fact]
        public void FlexBase_carries_the_client_list_and_watches_lan_radios()
        {
            string source = Read(Flex);

            Assert.Contains(
                "rd.GuiClientStations = r.GuiClients.Select(c => c.Station ?? \"\").ToList();",
                source, StringComparison.Ordinal);
            Assert.Contains("else WatchDiscoveryGuiClients(r);", source, StringComparison.Ordinal);
        }

        [Fact]
        public void The_vb_entry_point_hands_the_client_list_to_the_row()
        {
            Assert.Contains(".GuiClientStations = e.GuiClientStations,",
                Read(Globals), StringComparison.Ordinal);
        }

        /// <summary>
        /// The dialog updates rows IN PLACE (never remove-then-append), so a
        /// field the in-place copy skips is a field that goes stale the moment
        /// the radio is seen twice — which for a LAN radio is one second in.
        /// And the sentence must actually be handed the clause: an unpassed
        /// token would leave a literal "{occupancy}" in the operator's ear.
        /// </summary>
        [Fact]
        public void The_dialog_copies_the_fact_and_speaks_it()
        {
            string dialog = Read(Dialog);

            Assert.Contains("row.GuiClientStations = radio.GuiClientStations;",
                dialog, StringComparison.Ordinal);
            Assert.Contains("(\"occupancy\", OccupancyText)", dialog, StringComparison.Ordinal);
        }

        /// <summary>
        /// The positive control. Every hand-off assertion above is a source
        /// scan, and a scan that reads the wrong file — or looks for a line
        /// that was never the convention — passes for the wrong reason. Prove
        /// each reader finds a neighbouring line known to be present, and
        /// discriminates against one known not to be.
        /// </summary>
        [Fact]
        public void The_source_reader_finds_what_is_there_and_not_what_is_not()
        {
            string globals = Read(Globals);
            string dialog = Read(Dialog);
            string flex = Read(Flex);

            Assert.Contains(".WanAvailable = e.WanAvailable,", globals, StringComparison.Ordinal);
            Assert.Contains("row.WanAvailable = radio.WanAvailable;", dialog, StringComparison.Ordinal);
            Assert.Contains("if (r.IsWan) RememberWanRadio(r);", flex, StringComparison.Ordinal);

            Assert.DoesNotContain("NoSuchOccupancySymbol", globals, StringComparison.Ordinal);
            Assert.DoesNotContain("NoSuchOccupancySymbol", dialog, StringComparison.Ordinal);
            Assert.DoesNotContain("NoSuchOccupancySymbol", flex, StringComparison.Ordinal);
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that cannot "
                + "find its subject proves nothing about it.");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
