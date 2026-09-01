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
    /// any hand-off fails toward an EMPTY list, which the row now renders as
    /// an affirmative "online with 0 connected clients" — a false claim of an
    /// empty radio, worse than the silence it used to be; that is why the
    /// hand-offs are pinned individually.</para>
    ///
    /// <para><b>The zero case SPEAKS — this reverses the first design.</b>
    /// The clause originally stayed silent when nobody was connected, and
    /// Noel hit the cost within hours, at his tester's radio, deciding
    /// whether to transmit on it: "I'm not seeing that Don's connected or no
    /// one's connected." Silence is indistinguishable from a feature that is
    /// not working. So every live row states its count, zero included, to his
    /// dictated spec of 2026-08-30: "online with 0 connected clients". The
    /// clause's absence now means exactly one thing — the row is not live, so
    /// there is no current knowledge to count (#394, #391).</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RadioOccupancyTests
    {
        // ------------------------------------------------------------------
        // The words — assembled clauses, because fragments read fine in a
        // diff and land badly in the ear
        // ------------------------------------------------------------------

        /// <summary>
        /// The case that regressed, pinned hardest. Zero is a REPORT — "I
        /// looked, and nobody is on it" — and it is the sentence Noel could
        /// not hear on 2026-08-30 while deciding whether to key his tester's
        /// transmitter. Null and empty both mean the same counted zero.
        /// </summary>
        [Fact]
        public void Nobody_connected_says_so_out_loud()
        {
            Assert.Equal(", online with 0 connected clients",
                OccupancyPhrase.RowSuffix(null));
            Assert.Equal(", online with 0 connected clients",
                OccupancyPhrase.RowSuffix(Array.Empty<string>()));
        }

        /// <summary>
        /// Count first, callsign in parentheses after — his wording, with his
        /// own correction applied: the client is identified by station name
        /// (the callsign), never by an email address. And "1 connected
        /// client", singular — "1 clients" must never ship.
        /// </summary>
        [Fact]
        public void One_named_client_is_a_count_then_the_callsign()
        {
            Assert.Equal(", online with 1 connected client (wa2iwc)",
                OccupancyPhrase.RowSuffix(new[] { "wa2iwc" }));
        }

        /// <summary>
        /// A fresh client's station name arrives in a LATER update (#402 saw
        /// the empty-station window in the field), and a nameless occupant
        /// holds the transmit slice exactly as firmly as a named one.
        /// Whitespace is the same absence in a different coat. There is no
        /// per-client account to fall back to, so the client goes unnamed
        /// rather than misnamed — the count still tells the truth.
        /// </summary>
        [Fact]
        public void One_client_with_no_station_name_still_counts()
        {
            Assert.Equal(", online with 1 connected client",
                OccupancyPhrase.RowSuffix(new[] { "" }));
            Assert.Equal(", online with 1 connected client",
                OccupancyPhrase.RowSuffix(new[] { "  " }));
        }

        [Fact]
        public void Two_named_clients_read_as_a_pair()
        {
            Assert.Equal(", online with 2 connected clients (wa2iwc and k5ner)",
                OccupancyPhrase.RowSuffix(new[] { "wa2iwc", "k5ner" }));
        }

        [Fact]
        public void A_partly_named_pair_names_who_it_can()
        {
            Assert.Equal(", online with 2 connected clients (wa2iwc)",
                OccupancyPhrase.RowSuffix(new[] { "wa2iwc", "" }));
        }

        [Fact]
        public void Two_nameless_clients_are_still_two_clients()
        {
            Assert.Equal(", online with 2 connected clients",
                OccupancyPhrase.RowSuffix(new[] { "", "" }));
        }

        /// <summary>
        /// "If there's more than two, list" — his words. A crowded radio is
        /// exactly where WHO matters most, so the names are listed, never
        /// truncated to a bare number.
        /// </summary>
        [Fact]
        public void A_crowd_is_listed_not_truncated()
        {
            Assert.Equal(", online with 3 connected clients (wa2iwc, k5ner, n5xyz)",
                OccupancyPhrase.RowSuffix(new[] { "wa2iwc", "k5ner", "n5xyz" }));
        }

        /// <summary>
        /// The honest third state (field trace 2026-08-30, build 4.1.16.1736):
        /// a live row whose client list NO source delivered admits it, and
        /// never claims zero. Presence pushes were being dropped with no
        /// intake while Don sat on his radio; with only two states, that row
        /// either said nothing (the ambiguity Noel called out) or — worse —
        /// "0 connected clients", a confident false claim in the sentence
        /// read before keying somebody else's transmitter.
        /// </summary>
        [Fact]
        public void An_undelivered_row_says_unknown_never_zero()
        {
            Assert.Equal(", online, client count unknown",
                OccupancyPhrase.UnknownSuffix());
            Assert.Equal(
                "6300inshack, FLEX-6300, on SmartLink, online, client count unknown",
                Row("6300inshack", "FLEX-6300",
                    Lexicon.Get("connect.row.remote"),
                    OccupancyPhrase.UnknownSuffix()));
        }

        /// <summary>
        /// The WAN bank's occupancy accessor answers false — with an empty,
        /// never-null list — for a serial no SmartLink list carries. The
        /// dialog turns that false into "client count unknown"; a true with
        /// invented stations here would defeat the whole third state.
        /// </summary>
        [Fact]
        public void The_wan_bank_answers_false_for_a_serial_no_list_carries()
        {
            Assert.False(FlexBase.TryGetWanGuiClientStations(
                "0000-0000-0000-0000", out var stations));
            Assert.NotNull(stations);
            Assert.Empty(stations);
            Assert.False(FlexBase.TryGetWanGuiClientStations("", out _));
            Assert.False(FlexBase.TryGetWanGuiClientStations(null, out _));
        }

        // ------------------------------------------------------------------
        // The row — the sentence a screen reader actually says, one per
        // reachability state, because these are read IN FULL on every arrow
        // keypress and the fragments above only matter assembled. The shape
        // is Noel's, dictated 2026-08-30: name, model, "on" plus the paths
        // (both, when both answer; "via" the brokering account only when it
        // is not the one in play), then "online with N connected clients".
        // ------------------------------------------------------------------

        private static string Row(string name, string model, string whereText, string occupancy) =>
            Lexicon.Get("connect.row.display",
                ("fav", ""), ("autoConn", ""), ("lbw", ""),
                ("namePart", name), ("modelPart", model),
                ("whereText", whereText),
                ("occupancy", occupancy));

        /// <summary>
        /// The zero case, on his own radio, on the local network — the row he
        /// arrows past most. One clause of where, one clause of state, done.
        /// </summary>
        [Fact]
        public void A_local_radio_with_nobody_on_it()
        {
            Assert.Equal(
                "k5ner, FLEX-8600, on the local network, online with 0 connected clients",
                Row("k5ner", "FLEX-8600",
                    Lexicon.Get("connect.row.local"),
                    OccupancyPhrase.RowSuffix(Array.Empty<string>())));
        }

        /// <summary>
        /// SmartLink-only, own account: the account is NOT named — his own
        /// radios arrive on his own account, and saying so answers a question
        /// nobody asked, on every keypress (#401's ruling, applied here).
        /// </summary>
        [Fact]
        public void A_smartlink_radio_on_the_operators_own_account_names_no_account()
        {
            Assert.Equal(
                "6300inshack, FLEX-6300, on SmartLink, online with 0 connected clients",
                Row("6300inshack", "FLEX-6300",
                    Lexicon.Get("connect.row.remote"),
                    OccupancyPhrase.RowSuffix(Array.Empty<string>())));
        }

        /// <summary>
        /// The sentence the whole rework exists for: a tester's radio,
        /// reached through his account, with him on it — everything the
        /// operator needs before deciding to key somebody else's
        /// transmitter, and nothing else.
        /// </summary>
        [Fact]
        public void A_foreign_radio_names_its_broker_account_and_who_is_on_it()
        {
            Assert.Equal(
                "6300inshack, FLEX-6300, on SmartLink via dbreda@example.com, "
                + "online with 1 connected client (wa2iwc)",
                Row("6300inshack", "FLEX-6300",
                    Lexicon.Get("connect.row.remote_via", ("account", "dbreda@example.com")),
                    OccupancyPhrase.RowSuffix(new[] { "wa2iwc" })));
        }

        /// <summary>
        /// Dual-homed names BOTH paths and no choice. The old wording added
        /// "using local network"; his spec sentence names the paths and stops,
        /// so which leg gets tried belongs to the path combo and the connect
        /// announcement, not to a clause paid for on every arrow keypress.
        /// </summary>
        [Fact]
        public void A_dual_homed_radio_names_both_paths_and_no_choice()
        {
            var row = Row("k5ner", "FLEX-8600",
                Lexicon.Get("connect.row.dual"),
                OccupancyPhrase.RowSuffix(Array.Empty<string>()));

            Assert.Equal(
                "k5ner, FLEX-8600, on the local network and SmartLink, "
                + "online with 0 connected clients",
                row);
            Assert.DoesNotContain("using", row, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_dual_homed_foreign_radio_with_a_crowd_reads_as_one_sentence()
        {
            Assert.Equal(
                "6300inshack, FLEX-6300, on the local network and SmartLink via dbreda@example.com, "
                + "online with 3 connected clients (wa2iwc, k5ner, n5xyz)",
                Row("6300inshack", "FLEX-6300",
                    Lexicon.Get("connect.row.dual_via", ("account", "dbreda@example.com")),
                    OccupancyPhrase.RowSuffix(new[] { "wa2iwc", "k5ner", "n5xyz" })));
        }

        /// <summary>
        /// The occupied row appends who is on it and nothing more — no
        /// MultiFlex tutorial, no explaining what a client is. The operator
        /// is a licensed ham with a MultiFlex radio; the row's job is who,
        /// not what.
        /// </summary>
        [Fact]
        public void No_row_explains_multiflex_or_defines_a_client()
        {
            var crowded = Row("6300inshack", "FLEX-6300",
                Lexicon.Get("connect.row.remote"),
                OccupancyPhrase.RowSuffix(new[] { "wa2iwc", "k5ner" }));

            Assert.DoesNotContain("MultiFlex", crowded, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("slice", crowded, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("available", crowded, StringComparison.OrdinalIgnoreCase);
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
            // The non-WAN add branch grew a second duty in #402 — seeding the
            // LAN-recency evidence — so the single-statement `else` became a
            // block. The watch call is what this test exists to pin.
            Assert.Contains("WatchDiscoveryGuiClients(r);", source, StringComparison.Ordinal);
            Assert.Contains("_lanLastSeenTicks[r.Serial] = Environment.TickCount64;", source, StringComparison.Ordinal);
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
        /// The via-account half of the sentence: the dialog stamps each WAN
        /// row with the account its connect would broker through, from the
        /// SAME resolver the wire and the connect announcement already use
        /// (#401). A second derivation here would be the two-answers-to-one-
        /// question defect that took a tester's radio off the air; this pin
        /// exists so removing the shared resolver is a decision, not a drift.
        /// </summary>
        [Fact]
        public void The_dialog_asks_the_one_broker_resolver_for_the_rows_account()
        {
            string dialog = Read(Dialog);

            Assert.Contains("FlexBase.AccountThatWillBroker(serial, currentAccount)",
                dialog, StringComparison.Ordinal);
            Assert.Contains("r.BrokerAccount =", dialog, StringComparison.Ordinal);
        }

        /// <summary>
        /// The chain the 2026-08-30 field trace exposed, pinned link by link.
        /// Presence pushes are consumed by exactly ONE rig (#386); a teardown
        /// can leave none, and every push is then dropped — "list from
        /// dbreda@example.com with no intake — dropped" repeated for a whole
        /// session while Don's occupied radio rendered with no occupancy
        /// clause. The dropped push still refreshes the static WAN bank,
        /// stations included, so the roster row now reads occupancy from the
        /// bank when no sighting fed it — and admits not knowing when even
        /// the bank cannot answer, rather than claiming zero.
        /// </summary>
        [Fact]
        public void A_row_the_pushes_never_reached_reads_the_bank_or_admits_not_knowing()
        {
            string dialog = Read(Dialog);
            string flex = Read(Flex);
            string globals = Read(Globals);

            // The dropped push still banks the freshly parsed objects.
            Assert.Contains("RememberWanRadio(r, e.AccountId);",
                flex, StringComparison.Ordinal);
            // The dialog reads that bank for rows nothing has fed.
            Assert.Contains("FlexBase.TryGetWanGuiClientStations(serial, out var banked)",
                dialog, StringComparison.Ordinal);
            // Delivery is an explicit fact, stamped by every source that speaks.
            Assert.Contains("row.OccupancyKnown = true;", dialog, StringComparison.Ordinal);
            Assert.Contains(".OccupancyKnown = True,", globals, StringComparison.Ordinal);
            // And an undelivered live row admits it instead of claiming zero.
            Assert.Contains(": Radios.OccupancyPhrase.UnknownSuffix();",
                dialog, StringComparison.Ordinal);
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
