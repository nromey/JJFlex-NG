using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #634, Track L6: the SmartLink client list informs the display and the
    /// roster, and nothing else. The radio's own client reports drive the
    /// connect's retry flags and the arrival and departure announcements; a
    /// client only a list has mentioned is shown, labelled as reported, and
    /// cannot be disconnected from that row; and the station-name rescue
    /// survives, narrowed to this connection's handle, the account this
    /// connection was brokered through, and a radio that has reported neither
    /// a different name nor our departure.
    /// </summary>
    /// <remarks>
    /// <para>A real vendored <see cref="Radio"/> with a real
    /// <see cref="FlexBase"/> wired through both production seams, driven the
    /// way Track H's tests drive it: the radio's own lines go through the
    /// qualified command producer into the real parser, and a SmartLink list
    /// goes through this rig's own merge, <c>UpdateRadioDiscoveryFields</c>,
    /// exactly as the intake calls it. No socket, no radio, no window.</para>
    ///
    /// <para>The announcements are observed through the earcon delegates,
    /// which fire beside the speech on both the old and the new path; the
    /// retry flags are read by reflection, as the station-name wait reads
    /// them. Every "does not" below has a "does" beside it, from the radio's
    /// own line, so a silent harness cannot pass.</para>
    /// </remarks>
    // In the RadioConfig statics collection because it reads Lexicon statics,
    // as RadioOccupancyTests does; the isolation rule is one hop wider than
    // "only reads" on purpose.
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class SmartLinkClientListAuthorityTests
    {
        private const string Account = "executed@example.test";
        private const string OtherAccount = "someone-else@example.test";
        private const string Requested = "K5TEST";

        private static readonly FieldInfo AddedFlag =
            typeof(FlexBase).GetField("_clientAddedDuringStart", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo RemovedFlag =
            typeof(FlexBase).GetField("_clientRemovedDuringStart", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>A connected rig on a WAN radio, with the executed broker
        /// route known — or, with <paramref name="provenRoute"/> false, a
        /// route whose handle does not match, so the binding's account is
        /// null as Track H leaves it for an unproven association.</summary>
        private sealed class Bench : IDisposable
        {
            public readonly Radio Radio;
            public readonly FlexBase Rig;
            public readonly CommandConnection Connection;
            public int ConnectedEarcons, DisconnectedEarcons, Refreshes;
            private readonly Action _savedConnected, _savedDisconnected;

            /// <param name="lan">A LAN radio on a TCP connection instead —
            /// the ordinary local operator, who has never touched SmartLink
            /// (Track L7).</param>
            /// <param name="alreadyOnTheObject">Clients the vendor object
            /// already lists when this attempt attaches to it — put there
            /// before the rig wires a single handler, the way a client the
            /// radio's own status added on a PREVIOUS connection survives on
            /// a reused WAN object (Track L8, Sol's review of L7). Seeded
            /// into the roster with no recorded adder.</param>
            public Bench(bool provenRoute = true, bool lan = false, GUIClient[] alreadyOnTheObject = null)
            {
                Assert.True(AddedFlag != null && RemovedFlag != null,
                    "FlexBase's start flags are not where this test reads them, so every retry assertion would be vacuous.");
                Radio = OfflineCommandProducer.Radio(wan: !lan);
                if (alreadyOnTheObject != null && alreadyOnTheObject.Length > 0)
                    Radio.UpdateGuiClientsList(alreadyOnTheObject.ToList());
                Rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests", StationName = Requested });
                Rig.SuppressSpeech = true;
                Rig.theRadio = Radio;
                if (!lan)
                {
                    Radio.WANConnectionHandle = "broker-result";
                    OfflineCommandProducer.Call(Rig, "rememberCommandRoute", Radio, provenRoute ? "broker-result" : "other-handle", Account);
                }
                Rig.BeginStationAttempt(Radio, "test");
                OfflineCommandProducer.Wire(Rig, Radio);
                _savedConnected = ScreenReaderOutput.PlayClientConnectedEarcon;
                _savedDisconnected = ScreenReaderOutput.PlayClientDisconnectedEarcon;
                ScreenReaderOutput.PlayClientConnectedEarcon = () => ConnectedEarcons++;
                ScreenReaderOutput.PlayClientDisconnectedEarcon = () => DisconnectedEarcons++;
                Rig.GuiClientChanged += () => Refreshes++;
                if (lan)
                {
                    // The TCP transport, as ProducerIdentityTests drives it:
                    // an attempt whose writer is memory, published connected.
                    var transport = OfflineCommandProducer.Transport(Radio);
                    var attempt = OfflineCommandProducer.Call(transport, "BeginAttempt",
                        System.Net.IPAddress.Parse("192.0.2.1"), 4992, 0);
                    _lanWriter = new System.IO.StreamWriter(new System.IO.MemoryStream()) { AutoFlush = true };
                    attempt.GetType().GetField("Writer").SetValue(attempt, _lanWriter);
                    Assert.True((bool)OfflineCommandProducer.Call(transport, "PublishConnected", transport.CurrentConnection));
                }
                else
                {
                    OfflineCommandProducer.StartTls(Radio);
                }
                Connection = Radio.CurrentCommandConnection;
                Assert.NotNull(Rig.CurrentConnectionBinding);
                Assert.Equal(!lan, Rig.CurrentConnectionBinding.IsWan);
                Assert.Equal(!lan && provenRoute ? Account : null, Rig.CurrentConnectionBinding.AccountId);
            }

            private readonly System.IO.StreamWriter _lanWriter;

            /// <summary>A LAN discovery broadcast for this radio, applied the
            /// way the vendor's discovery applies it (API.RefreshRadio): the
            /// radio object's own list merge, on a thread that is not a
            /// SmartLink merge.</summary>
            public void BroadcastSays(params GUIClient[] clients) =>
                Radio.UpdateGuiClientsList(clients.ToList());

            /// <summary>A status line from the radio, on this connection.</summary>
            public void RadioSays(string line) => OfflineCommandProducer.Data(Radio, Connection, line);

            /// <summary>A SmartLink list for this radio from <paramref name="account"/>,
            /// merged the way the intake merges it.</summary>
            public void ListSays(string account, params GUIClient[] clients)
            {
                var listed = SmartLinkRegistrationReplayTests.WanRadio(Radio.Serial);
                listed.UpdateGuiClientsList(clients.ToList());
                OfflineCommandProducer.Call(Rig, "UpdateRadioDiscoveryFields", listed, Radio, account);
            }

            public bool Added => (bool)AddedFlag.GetValue(Rig);
            public bool Removed => (bool)RemovedFlag.GetValue(Rig);
            public ClientRow? Row(uint handle) =>
                Rig.GetGuiClients().Where(r => r.Handle == handle).Select(r => (ClientRow?)r).FirstOrDefault();

            public void Dispose()
            {
                ScreenReaderOutput.PlayClientConnectedEarcon = _savedConnected;
                ScreenReaderOutput.PlayClientDisconnectedEarcon = _savedDisconnected;
                OfflineCommandProducer.Release(Rig, Radio);
                _lanWriter?.Dispose();
            }
        }

        private static GUIClient Own(string station = Requested) => new GUIClient(7, null, "JJFlex", station, false);
        private static GUIClient Don() => new GUIClient(9, null, "SmartSDR", "WA2IWC", false);

        private static void OurClientIsEstablished(Bench b)
        {
            b.RadioSays("H7");
            b.RadioSays("S0|client 7 connected client_id=me program=JJFlex station=" + Requested + " local_ptt=1");
            Assert.True(b.Added, "the radio's own Connected report for our handle did not arm the added flag, so nothing below measures the flags");
            Assert.False(b.Removed);
        }

        // ------------------------------------------------------------------
        // Ruling TWO: the list informs the display and the roster, and nothing else
        // ------------------------------------------------------------------

        [Fact]
        public void A_list_merge_shows_a_client_but_neither_announces_it_nor_arms_the_retry()
        {
            using var b = new Bench();
            OurClientIsEstablished(b);
            b.Refreshes = 0;

            // SmartLink's list says Don is on the radio. Displayed and
            // labelled; not spoken; the retry flags untouched.
            b.ListSays(Account, Own(), Don());
            Assert.Equal(0, b.ConnectedEarcons);
            Assert.True(b.Added);
            Assert.False(b.Removed);
            var don = b.Row(9);
            Assert.NotNull(don);
            Assert.False(don.Value.ConfirmedByRadio);
            Assert.False(don.Value.MayHaveLeft);
            Assert.Contains("Reported by SmartLink", ClientRowPhrase.Line(don.Value), StringComparison.Ordinal);
            Assert.Equal(1, b.Refreshes);

            // The same list again: the answer did not change, so the display
            // is not told again (the signature gate, #634's second caller).
            b.ListSays(Account, Own(), Don());
            Assert.Equal(1, b.Refreshes);

            // The radio's own word: announced, and the row is confirmed.
            b.RadioSays("S0|client 9 connected client_id=don program=SmartSDR station=WA2IWC local_ptt=0");
            Assert.Equal(1, b.ConnectedEarcons);
            Assert.True(b.Row(9).Value.ConfirmedByRadio);
            Assert.DoesNotContain("Reported by SmartLink", ClientRowPhrase.Line(b.Row(9).Value), StringComparison.Ordinal);

            // The list stops mentioning Don. Not a departure: kept, marked,
            // not spoken, and not a reason to retry.
            b.ListSays(Account, Own());
            Assert.Equal(0, b.DisconnectedEarcons);
            Assert.False(b.Removed);
            var maybeGone = b.Row(9);
            Assert.NotNull(maybeGone);
            Assert.True(maybeGone.Value.MayHaveLeft);
            Assert.Contains("was reported earlier", ClientRowPhrase.Line(maybeGone.Value), StringComparison.Ordinal);

            // The radio says he left: announced, and the row is gone.
            b.RadioSays("S0|client 9 disconnected forced=0");
            Assert.Equal(1, b.DisconnectedEarcons);
            Assert.Null(b.Row(9));
        }

        [Fact]
        public void The_retry_flags_follow_the_radios_own_report_for_our_handle_and_never_a_list()
        {
            using var b = new Bench();
            OurClientIsEstablished(b);

            // The list omits our own record — the stale-list shape that used
            // to abort a live connect for a retry.
            b.ListSays(Account, Don());
            Assert.False(b.Removed, "a SmartLink list omitting our record armed the retry (#634)");
            Assert.True(b.Added);

            // The radio's own word does arm it.
            b.RadioSays("S0|client 7 disconnected forced=0");
            Assert.True(b.Removed);

            // And a list re-adding our record does not clear it; the radio's
            // own report does.
            b.ListSays(Account, Own(), Don());
            Assert.True(b.Removed, "a SmartLink list re-adding our record read as the radio saying we were back (#634)");
            b.RadioSays("S0|client 7 connected client_id=me program=JJFlex station=" + Requested + " local_ptt=1");
            Assert.False(b.Removed);
            Assert.True(b.Added);
        }

        [Fact]
        public void Disconnect_is_refused_for_a_client_only_a_list_reported_and_the_caveat_names_it()
        {
            using var b = new Bench();
            OurClientIsEstablished(b);
            b.ListSays(Account, Own(), Don());

            Assert.False(b.Rig.RadioHasConfirmedClient(9));
            Assert.False(b.Rig.DisconnectGuiClient(9),
                "a client only SmartLink's list reported could be disconnected from its row before the radio identified it (#634)");
            // Named as company that may be affected — and NOT in the definite
            // claim. L6 asserted the opposite here, pinning the false claim
            // Sol's review of L6 found (#634, Track L7).
            Assert.DoesNotContain("WA2IWC", b.Rig.OtherConnectedStations);
            Assert.Equal(new[] { "WA2IWC" }, b.Rig.UnconfirmedOtherStations);
            Assert.Equal(Lexicon.Get("connect.client.unconfirmed_affected", ("clients", "WA2IWC")), b.Rig.UnconfirmedCompanyCaveat);

            // The radio identifies him: named as connected, no caveat, and
            // the row is his to disconnect.
            b.RadioSays("S0|client 9 connected client_id=don program=SmartSDR station=WA2IWC local_ptt=0");
            Assert.True(b.Rig.RadioHasConfirmedClient(9));
            Assert.Equal(new[] { "WA2IWC" }, b.Rig.OtherConnectedStations);
            Assert.Empty(b.Rig.UnconfirmedOtherStations);
            Assert.Null(b.Rig.UnconfirmedCompanyCaveat);
            Assert.True(ClientRowPhrase.MayDisconnect(b.Row(9)));
        }

        // ------------------------------------------------------------------
        // Ruling ONE: the station-name rescue survives, narrowed
        // ------------------------------------------------------------------

        [Fact]
        public void The_rescue_finishes_the_wait_from_the_brokered_accounts_list_and_names_its_source()
        {
            using var b = new Bench();
            b.RadioSays("H7");

            // Nothing has written our record: the wait goes on.
            Assert.False(b.Rig.StationNameWaitDecision(null, Requested).Finished);

            // The brokered account's list writes our record with the name we
            // asked for, before the radio's own report: the rescue finishes
            // the wait, and says which source did.
            b.ListSays(Account, Own());
            var overlay = b.Radio.FindGUIClientByClientHandle(7);
            Assert.NotNull(overlay);
            Assert.Equal(Requested, overlay.Station);
            var decision = b.Rig.StationNameWaitDecision(overlay.Station, Requested);
            Assert.True(decision.Finished);
            Assert.Equal(FlexBase.StationNameEvidence.SmartLinkList, decision.Source);
            Assert.Equal(Lexicon.Get("connect.start.station_from_smartlink"), decision.RescueSentence);
        }

        [Fact]
        public void A_name_only_discovery_wrote_finishes_the_wait_and_is_named_as_the_local_network()
        {
            using var b = new Bench();
            b.RadioSays("H7");
            // No list has written our record; a broadcast has (the overlay
            // carries the name, and nothing recorded a SmartLink list for it).
            var decision = b.Rig.StationNameWaitDecision(Requested, Requested);
            Assert.True(decision.Finished);
            Assert.Equal(FlexBase.StationNameEvidence.LocalDiscovery, decision.Source);
            Assert.Equal(Lexicon.Get("connect.start.station_from_local_network"), decision.RescueSentence);
        }

        [Fact]
        public void The_radios_own_report_outranks_every_overlay()
        {
            using var b = new Bench();
            b.RadioSays("H7");

            // The radio enumerates us with no name yet: not a different
            // name, so an overlay may still finish the wait.
            b.RadioSays("S0|client 7 connected client_id=me program=JJFlex station= local_ptt=1");
            Assert.True(b.Rig.StationNameWaitDecision(Requested, Requested).Finished);

            // The radio reports us gone: nothing finishes the wait.
            b.RadioSays("S0|client 7 disconnected forced=0");
            Assert.False(b.Rig.StationNameWaitDecision(Requested, Requested).Finished);

            // The radio reports a DIFFERENT name: the wait can never succeed
            // (#402's rename), whatever a list says.
            b.RadioSays("S0|client 7 connected client_id=me program=JJFlex station=" + Requested + "1 local_ptt=1");
            Assert.False(b.Rig.StationNameWaitDecision(Requested, Requested).Finished);

            // The radio reports the name we asked for: finished, by the
            // radio, with no caveat sentence.
            b.RadioSays("S0|client 7 connected client_id=me program=JJFlex station=" + Requested + " local_ptt=1");
            var decision = b.Rig.StationNameWaitDecision(null, Requested);
            Assert.True(decision.Finished);
            Assert.Equal(FlexBase.StationNameEvidence.RadioReport, decision.Source);
            Assert.Null(decision.RescueSentence);
        }

        [Fact]
        public void A_list_from_another_account_does_not_write_the_connected_radios_roster()
        {
            using var b = new Bench();
            b.RadioSays("H7");

            // Another account's list names the same radio. It was not the
            // path this connection took, so its roster stays out.
            b.ListSays(OtherAccount, Own(), Don());
            Assert.Null(b.Radio.FindGUIClientByClientHandle(7));
            Assert.Null(b.Radio.FindGUIClientByClientHandle(9));
            Assert.Empty(b.Rig.GetGuiClients());
            Assert.False(b.Rig.StationNameWaitDecision(b.Radio.FindGUIClientByClientHandle(7)?.Station, Requested).Finished);

            // Positive control: the brokered account's list writes it.
            b.ListSays(Account, Own(), Don());
            Assert.NotNull(b.Radio.FindGUIClientByClientHandle(7));
            Assert.NotNull(b.Row(9));
        }

        [Fact]
        public void A_connection_whose_account_is_unproven_takes_no_list_into_its_roster()
        {
            using var b = new Bench(provenRoute: false);
            b.RadioSays("H7");
            Assert.Null(b.Rig.CurrentConnectionBinding.AccountId);

            // Null is not an account, and is not inferred to be one.
            b.ListSays(Account, Own(), Don());
            Assert.Null(b.Radio.FindGUIClientByClientHandle(7));
            Assert.Empty(b.Rig.GetGuiClients());
        }

        // ------------------------------------------------------------------
        // A row names the source that reported it (Track L7, Sol's review of L6)
        // ------------------------------------------------------------------

        /// <summary>
        /// The ordinary LAN operator. A local discovery broadcast lists a
        /// client the radio has not confirmed; until Track L7 the row said
        /// SmartLink had reported it, to someone who never used SmartLink.
        /// It names the local network now, with a station and without one,
        /// and the radio's own report replaces both with its word.
        /// </summary>
        /// <remarks>
        /// Routing every unconfirmed row to the SmartLink sentence, as L6
        /// did, turns the first line assertion red.
        /// </remarks>
        [Fact]
        public void A_client_only_a_local_broadcast_reported_is_labelled_as_the_local_network()
        {
            using var b = new Bench(lan: true);
            OurClientIsEstablished(b);

            b.BroadcastSays(Own(), Don(), new GUIClient(10, null, "SmartSDR", "", false));

            var don = b.Row(9);
            Assert.NotNull(don);
            Assert.Equal(ClientRowSource.LocalDiscovery, don.Value.Source);
            Assert.False(don.Value.ConfirmedByRadio);
            string line = ClientRowPhrase.Line(don.Value);
            Assert.Equal("WA2IWC. Reported on the local network; not yet confirmed.", line);
            Assert.DoesNotContain("SmartLink", line, StringComparison.Ordinal);
            Assert.Equal("A client with no station name was reported on the local network; the radio has not confirmed it.",
                ClientRowPhrase.Line(b.Row(10).Value));
            Assert.False(ClientRowPhrase.MayDisconnect(b.Row(9)));

            // The radio's own word outranks the broadcast.
            b.RadioSays("S0|client 9 connected client_id=don program=SmartSDR station=WA2IWC local_ptt=0");
            Assert.Equal(ClientRowSource.Radio, b.Row(9).Value.Source);
            Assert.Equal("SmartSDR on WA2IWC", ClientRowPhrase.Line(b.Row(9).Value));
        }

        /// <summary>
        /// Each row is named by the source that added it, not by what kind
        /// of radio object it sits on. A LAN radio's object is filled by
        /// broadcasts, yet a SmartLink list merged into it (as display, for a
        /// LAN connection) adds a client that is SmartLink's; a WAN object is
        /// filled by lists, yet a record a broadcast built — no client_id,
        /// outside a merge — is the local network's. Both are cases the
        /// object's own channel, the fallback for an unrecorded client, would
        /// name wrongly, so both pin the recorded adder.
        /// </summary>
        /// <remarks>
        /// Dropping the adder record, so every unrecorded row names no source
        /// (Track L8) as the object's channel named it wrongly before, turns
        /// both source assertions red.
        /// </remarks>
        [Fact]
        public void Each_row_names_the_source_that_added_it_not_the_radio_objects_channel()
        {
            using (var lan = new Bench(lan: true))
            {
                OurClientIsEstablished(lan);
                lan.BroadcastSays(Own());
                lan.ListSays(Account, Own(), Don());
                Assert.Equal(ClientRowSource.SmartLinkList, lan.Row(9).Value.Source);
                Assert.Contains("Reported by SmartLink", ClientRowPhrase.Line(lan.Row(9).Value), StringComparison.Ordinal);
            }

            using (var wan = new Bench())
            {
                OurClientIsEstablished(wan);
                wan.BroadcastSays(Own(), Don());
                Assert.Equal(ClientRowSource.LocalDiscovery, wan.Row(9).Value.Source);
                Assert.Contains("Reported on the local network", ClientRowPhrase.Line(wan.Row(9).Value), StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Sol's review of L7: a client already on the WAN object when the
        /// attempt attaches has no recorded adder, and L7 named it by the
        /// object's channel — "reported by SmartLink" — although SmartLink
        /// may never have listed it. The radio's own TCP status adds such a
        /// client after the session's one list, deliberately unrecorded
        /// (it carries a client_id), and the vendor keeps it on the reused
        /// object across a command connection. It is seeded into the next
        /// attempt's roster, and the row now says only that the radio has not
        /// confirmed it, naming no source. The radio's own report is the
        /// control that the row then names the radio.
        /// </summary>
        /// <remarks>
        /// Falling back to the object's channel for an unrecorded client, as
        /// L7 did, turns the source and both sentence assertions red.
        /// </remarks>
        [Fact]
        public void A_client_already_on_the_WAN_object_at_attachment_names_no_source()
        {
            using var b = new Bench(alreadyOnTheObject: new[]
            {
                new GUIClient(9, "don-id", "SmartSDR", "WA2IWC", false),
                new GUIClient(10, "quiet-id", "SmartSDR", "", false),
            });
            OurClientIsEstablished(b);

            var don = b.Row(9);
            Assert.NotNull(don);
            Assert.Equal(ClientRowSource.Unknown, don.Value.Source);
            Assert.False(don.Value.ConfirmedByRadio);
            Assert.True(don.Value.Unconfirmed);
            string line = ClientRowPhrase.Line(don.Value);
            Assert.Equal("WA2IWC. Not yet confirmed.", line);
            Assert.DoesNotContain("SmartLink", line, StringComparison.Ordinal);
            Assert.DoesNotContain("local network", line, StringComparison.Ordinal);
            Assert.Equal("A client with no station name has not yet been confirmed by the radio.",
                ClientRowPhrase.Line(b.Row(10).Value));
            Assert.False(ClientRowPhrase.MayDisconnect(don));

            // The caveat, not the claim: no source is still not the radio.
            var company = ClientRowPhrase.Company(b.Rig.GetGuiClients());
            Assert.Contains("WA2IWC", company.Reported);
            Assert.DoesNotContain("WA2IWC", company.Confirmed);

            // The radio's own word replaces the unknown with the radio.
            b.RadioSays("S0|client 9 connected client_id=don-id program=SmartSDR station=WA2IWC local_ptt=0");
            Assert.Equal(ClientRowSource.Radio, b.Row(9).Value.Source);
            Assert.Equal("SmartSDR on WA2IWC", ClientRowPhrase.Line(b.Row(9).Value));
        }

        // ------------------------------------------------------------------
        // The outcome of a disconnect request stays readable (#643, Track L7)
        // ------------------------------------------------------------------

        /// <summary>
        /// Sol's review of L6: the dialog wrote "the disconnect request was
        /// sent" into the line under the list and refreshed half a second
        /// later, and the refresh rewrote the line from the selected row —
        /// for an ordinary client still on the radio, it cleared it. The
        /// outcome now stays until the operator selects another row or the
        /// radio reports the client gone, whatever refreshes in between, and
        /// a failed request is held the same way. This is the dialog's rule,
        /// read without a window; every refresh and every operator selection
        /// asks it for the line.
        /// </summary>
        /// <remarks>
        /// The first assertion is the control that the line is otherwise the
        /// row's reason. Letting the line fall back to the row's reason on
        /// every read, which is what L6's refresh did, turns the held
        /// assertions red.
        /// </remarks>
        [Fact]
        public void A_disconnect_outcome_stays_on_the_line_until_the_operator_moves_or_the_radio_reports_the_client_gone()
        {
            var don = new ClientRow("SmartSDR", "WA2IWC", 9, false, "", ClientRowSource.Radio, false);
            var justin = new ClientRow("SmartSDR", "KD2XYZ", 10, false, "", ClientRowSource.SmartLinkList, false);
            string sent = Lexicon.Get("connect.multiflex.disconnect_requested", ("station", don.NameForSentence));
            string unavailable = Lexicon.Get("connect.multiflex.disconnect_unavailable");

            var line = new DisconnectOutcomeLine();
            Assert.Null(line.TextFor(don));
            Assert.Equal(unavailable, line.TextFor(justin));

            line.Record(9, sent);
            // The timed refresh half a second later: same rows, same row
            // selected. L6's refresh cleared the line here.
            Assert.Equal(sent, line.TextFor(don));
            // Another client leaves; Don is still selected.
            Assert.Equal(sent, line.TextFor(don));
            // A list stops mentioning Don. The radio has not spoken, so the
            // row stays — marked — and so does the outcome, instead of the
            // row's "unavailable" reason.
            var donMaybeGone = don with { MayHaveLeft = true };
            Assert.Equal(sent, line.TextFor(donMaybeGone));

            // The operator moves to another row: that row's reason, and the
            // outcome is finished — coming back does not bring it back.
            Assert.Equal(unavailable, line.TextFor(justin));
            Assert.Null(line.TextFor(don));

            // The radio reports the client gone: the roster drops the row, so
            // the list selects another row or none, and the outcome goes.
            line.Record(9, sent);
            Assert.Equal(sent, line.TextFor(don));
            Assert.Equal(unavailable, line.TextFor(justin));
            line.Record(9, sent);
            Assert.Null(line.TextFor(null));

            // A failed request is held the same way.
            string failed = Lexicon.Get("connect.multiflex.disconnect_failed");
            line.Record(9, failed);
            Assert.Equal(failed, line.TextFor(don));
            Assert.Equal(failed, line.TextFor(don));
            Assert.Equal(unavailable, line.TextFor(justin));
        }

        // ------------------------------------------------------------------
        // Radio-wide confirmations claim only what the radio confirmed (Track L7)
        // ------------------------------------------------------------------

        private static readonly FieldInfo IsConnectedField =
            typeof(FlexBase).GetField("_IsConnected", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Sol's review of L6: the static-IP, registration and firmware
        /// preflights and the reboot confirmation named every client in the
        /// vendor's merged list as connected — including one only a stale
        /// SmartLink list mentioned — and L6's caveat after the claim could
        /// not make the claim true. A mixed company now splits: the client
        /// the radio confirmed is named as connected, the one only a list
        /// reported is named as possibly affected, never both; and a company
        /// the radio has confirmed none of makes no definite claim at all.
        /// Driven through the rig's two properties, which all four sites
        /// read, and through the static-IP preflight's assembled warnings.
        /// </summary>
        /// <remarks>
        /// Reading the definite list from the vendor's merged list again, as
        /// L6 did, turns the "no definite claim" and "never both"
        /// assertions red.
        /// </remarks>
        [Fact]
        public void A_radio_wide_confirmation_names_as_connected_only_the_clients_the_radio_confirmed()
        {
            using var b = new Bench();
            OurClientIsEstablished(b);
            Assert.True(IsConnectedField != null, "FlexBase._IsConnected is not where this test sets it, so the preflight would refuse before the company lines.");
            IsConnectedField.SetValue(b.Rig, true);

            // All unconfirmed: a list names Don and Justin, the radio neither.
            var justin = new GUIClient(10, null, "SmartSDR", "KD2XYZ", false);
            b.ListSays(Account, Own(), Don(), justin);
            Assert.Empty(b.Rig.OtherConnectedStations);
            Assert.Equal(new[] { "WA2IWC", "KD2XYZ" }, b.Rig.UnconfirmedOtherStations);
            var allReported = b.Rig.PreflightStaticIp("192.0.2.1", "192.0.2.254", "255.255.255.0");
            Assert.True(allReported.CanProceed, allReported.BlockReason);
            Assert.DoesNotContain(allReported.Warnings, w => w.Contains("are connected", StringComparison.Ordinal));
            Assert.Equal(new[] { Lexicon.Get("connect.client.unconfirmed_affected", ("clients", "WA2IWC, KD2XYZ")) }, allReported.Warnings);

            // Mixed: the radio confirms Don. He moves to the definite claim
            // and out of the caveat; Justin stays reported.
            b.RadioSays("S0|client 9 connected client_id=don program=SmartSDR station=WA2IWC local_ptt=0");
            Assert.Equal(new[] { "WA2IWC" }, b.Rig.OtherConnectedStations);
            Assert.Equal(new[] { "KD2XYZ" }, b.Rig.UnconfirmedOtherStations);
            var mixed = b.Rig.PreflightStaticIp("192.0.2.1", "192.0.2.254", "255.255.255.0");
            Assert.Equal(new[]
            {
                "Other stations are connected and will need to reconnect: WA2IWC",
                Lexicon.Get("connect.client.unconfirmed_affected", ("clients", "KD2XYZ")),
            }, mixed.Warnings);
        }

        /// <summary>
        /// The split itself, read without a rig: our own client is in
        /// neither list; a confirmed client is definite; a reported one, from
        /// either source, and a confirmed one something has since stopped
        /// listing are caveat material; and a client with no station is named
        /// by its program, or by the unknown-client word when it has neither.
        /// </summary>
        [Fact]
        public void The_company_split_puts_each_client_in_exactly_one_list()
        {
            var ours = new ClientRow("JJFlex", "K5TEST", 7, true, "", ClientRowSource.Radio, false);
            var don = new ClientRow("SmartSDR", "WA2IWC", 9, false, "", ClientRowSource.Radio, false);
            var nameless = new ClientRow("Maestro", "", 11, false, "", ClientRowSource.Radio, false);
            var justin = new ClientRow("SmartSDR", "KD2XYZ", 10, false, "", ClientRowSource.SmartLinkList, false);
            var lan = new ClientRow("SmartSDR", "N0LAN", 12, false, "", ClientRowSource.LocalDiscovery, false);
            var maybeGone = new ClientRow("SmartSDR", "W1GONE", 13, false, "", ClientRowSource.Radio, true);
            var anonymous = new ClientRow("Unknown", "", 14, false, "", ClientRowSource.SmartLinkList, false);

            var mixed = ClientRowPhrase.Company(new[] { ours, don, nameless, justin, lan, maybeGone, anonymous });
            Assert.Equal(new[] { "WA2IWC", "Maestro" }, mixed.Confirmed);
            Assert.Equal(new[] { "KD2XYZ", "N0LAN", "W1GONE", Lexicon.Get("connect.client.unknown_added") }, mixed.Reported);

            var noneConfirmed = ClientRowPhrase.Company(new[] { ours, justin, lan });
            Assert.Empty(noneConfirmed.Confirmed);
            Assert.Equal(new[] { "KD2XYZ", "N0LAN" }, noneConfirmed.Reported);
        }

        // ------------------------------------------------------------------
        // The sentences an operator reads, assembled
        // ------------------------------------------------------------------

        /// <summary>
        /// Sol's scoped review of L9 and L10: a client the radio identified,
        /// that a list or a broadcast has since stopped mentioning, was given
        /// the Disconnect reason "Unable to disconnect this client until the
        /// radio identifies it" — false for it, because the radio did. Its
        /// uncertainty is that it may have left, and the reason now says so,
        /// in the terms of sentence 3, named as sentence 3 names it: station,
        /// else program. A client the radio never identified keeps the
        /// approved sentence, whether or not a list has since dropped it too.
        /// </summary>
        /// <remarks>
        /// Routing every refused row to the approved sentence, as L10 did,
        /// turns the first three assertions red. The readable line under the
        /// list reads the same reason when no outcome is pinned.
        /// </remarks>
        [Fact]
        public void A_client_the_radio_identified_that_may_have_left_is_never_said_to_be_waiting_for_the_radio()
        {
            var radioMaybeGone = new ClientRow("SmartSDR", "WA2IWC", 9, false, "", ClientRowSource.Radio, true);
            var namelessRadioMaybeGone = new ClientRow("Maestro", "", 11, false, "", ClientRowSource.Radio, true);
            var listMaybeGone = new ClientRow("SmartSDR", "KD2XYZ", 10, false, "", ClientRowSource.SmartLinkList, true);
            var listed = new ClientRow("SmartSDR", "KD2XYZ", 10, false, "", ClientRowSource.SmartLinkList, false);
            var broadcast = new ClientRow("SmartSDR", "KD2XYZ", 12, false, "", ClientRowSource.LocalDiscovery, false);
            var unknown = new ClientRow("SmartSDR", "KD2XYZ", 13, false, "", ClientRowSource.Unknown, false);
            const string awaitingRadio = "Unable to disconnect this client until the radio identifies it.";

            Assert.False(ClientRowPhrase.MayDisconnect(radioMaybeGone));
            Assert.Equal("Unable to disconnect WA2IWC; it may have disconnected already.",
                ClientRowPhrase.DisconnectReason(radioMaybeGone));
            Assert.Equal("Unable to disconnect Maestro; it may have disconnected already.",
                ClientRowPhrase.DisconnectReason(namelessRadioMaybeGone));
            Assert.Equal(
                Lexicon.Get("connect.multiflex.disconnect_unavailable_may_have_left", ("station", radioMaybeGone.NameForSentence)),
                new DisconnectOutcomeLine().TextFor(radioMaybeGone));
            Assert.DoesNotContain("identifies", ClientRowPhrase.DisconnectReason(radioMaybeGone)!, StringComparison.Ordinal);

            // Never identified by the radio: the approved sentence, for every
            // source, and still when a list has since dropped it.
            Assert.Equal(awaitingRadio, ClientRowPhrase.DisconnectReason(listMaybeGone));
            Assert.Equal(awaitingRadio, ClientRowPhrase.DisconnectReason(listed));
            Assert.Equal(awaitingRadio, ClientRowPhrase.DisconnectReason(broadcast));
            Assert.Equal(awaitingRadio, ClientRowPhrase.DisconnectReason(unknown));

            // One text at both tiers.
            Assert.Equal(
                Lexicon.Get("connect.multiflex.disconnect_unavailable_may_have_left", VerbosityLevel.Terse, ("station", "WA2IWC")),
                Lexicon.Get("connect.multiflex.disconnect_unavailable_may_have_left", VerbosityLevel.Chatty, ("station", "WA2IWC")));
        }

        /// <summary>
        /// The words are Noel's, approved 2026-09-30 (Track L10), and this
        /// test pins them exactly: the chatty and terse forms of each
        /// not-yet-confirmed row, one text at both tiers for everything else.
        /// The two no-station companions for the local network and an
        /// unknown source are still drafts; the approval gave them no
        /// wording, so they read the same at both tiers.
        /// </summary>
        [Fact]
        public void The_rows_say_how_we_know_and_the_summary_says_when_some_are_only_reported()
        {
            var ours = new ClientRow("JJFlex", "K5TEST", 7, true, "A", ClientRowSource.Radio, false);
            var confirmed = new ClientRow("SmartSDR", "WA2IWC", 9, false, "B", ClientRowSource.Radio, false);
            var reported = new ClientRow("SmartSDR", "WA2IWC", 9, false, "", ClientRowSource.SmartLinkList, false);
            var nameless = new ClientRow("SmartSDR", "", 10, false, "", ClientRowSource.SmartLinkList, false);
            var broadcast = new ClientRow("SmartSDR", "WA2IWC", 9, false, "", ClientRowSource.LocalDiscovery, false);
            var namelessBroadcast = new ClientRow("SmartSDR", "", 10, false, "", ClientRowSource.LocalDiscovery, false);
            var unknown = new ClientRow("SmartSDR", "WA2IWC", 9, false, "", ClientRowSource.Unknown, false);
            var namelessUnknown = new ClientRow("SmartSDR", "", 10, false, "", ClientRowSource.Unknown, false);
            var maybeGone = new ClientRow("SmartSDR", "WA2IWC", 9, false, "", ClientRowSource.Radio, true);

            Assert.Equal("JJFlex on K5TEST — Slices: A (This client)", ClientRowPhrase.Line(ours));
            Assert.Equal("SmartSDR on WA2IWC — Slices: B", ClientRowPhrase.Line(confirmed));
            // Chatty, which is also what the level-less overload reads.
            Assert.Equal("WA2IWC. Reported by SmartLink; not yet confirmed.", ClientRowPhrase.Line(reported));
            Assert.Equal("WA2IWC. Reported by SmartLink; not yet confirmed.", ClientRowPhrase.Line(reported, VerbosityLevel.Chatty));
            Assert.Equal("A client with no station name was reported by SmartLink and it's currently not confirmed.", ClientRowPhrase.Line(nameless));
            Assert.Equal("WA2IWC. Reported on the local network; not yet confirmed.", ClientRowPhrase.Line(broadcast));
            Assert.Equal("A client with no station name was reported on the local network; the radio has not confirmed it.", ClientRowPhrase.Line(namelessBroadcast));
            Assert.Equal("WA2IWC. Not yet confirmed.", ClientRowPhrase.Line(unknown));
            Assert.Equal("A client with no station name has not yet been confirmed by the radio.", ClientRowPhrase.Line(namelessUnknown));
            Assert.Equal("Heads up: WA2IWC was reported earlier, but may have disconnected.", ClientRowPhrase.Line(maybeGone));

            // Terse. Noel's own capitalisation and punctuation, kept.
            Assert.Equal("WA2IWC SmartLink Responded; unconfirmed.", ClientRowPhrase.Line(reported, VerbosityLevel.Terse));
            Assert.Equal("station reporting via SmartLink, unconfirmed", ClientRowPhrase.Line(nameless, VerbosityLevel.Terse));
            Assert.Equal("WA2IWC local network; unconfirmed.", ClientRowPhrase.Line(broadcast, VerbosityLevel.Terse));
            Assert.Equal("WA2IWC unconfirmed.", ClientRowPhrase.Line(unknown, VerbosityLevel.Terse));
            Assert.Equal("Heads up: WA2IWC was reported earlier, but may have disconnected.", ClientRowPhrase.Line(maybeGone, VerbosityLevel.Terse));
            Assert.Equal(ClientRowPhrase.Line(namelessBroadcast), ClientRowPhrase.Line(namelessBroadcast, VerbosityLevel.Terse));
            Assert.Equal(ClientRowPhrase.Line(namelessUnknown), ClientRowPhrase.Line(namelessUnknown, VerbosityLevel.Terse));
            Assert.Equal("SmartSDR on WA2IWC — Slices: B", ClientRowPhrase.Line(confirmed, VerbosityLevel.Terse));

            // The approved row sentences name the station only; the drafts'
            // program lead-in is gone. A nameless client that may have left
            // is still named by its program (NameForSentence).
            Assert.DoesNotContain("SmartSDR", ClientRowPhrase.Line(reported), StringComparison.Ordinal);
            Assert.DoesNotContain("SmartSDR", ClientRowPhrase.Line(reported, VerbosityLevel.Terse), StringComparison.Ordinal);
            Assert.Equal("Heads up: Maestro was reported earlier, but may have disconnected.",
                ClientRowPhrase.Line(new ClientRow("Maestro", "", 11, false, "", ClientRowSource.Radio, true)));

            Assert.Equal("1 client connected:", ClientRowPhrase.Summary(new[] { ours }, informationUnavailable: false));
            Assert.Equal("2 clients connected:", ClientRowPhrase.Summary(new[] { ours, confirmed }, informationUnavailable: false));
            Assert.Equal("These are the clients reported to JJ Flexible Radio Access. Some entries have not been confirmed by the radio.",
                ClientRowPhrase.Summary(new[] { ours, reported }, informationUnavailable: false));
            Assert.Equal("Client information isn't available yet. Other operators may still be connected using SmartSDR, JJ Flexible Radio Access, or another Flex client.",
                ClientRowPhrase.Summary(Array.Empty<ClientRow>(), informationUnavailable: true));

            Assert.True(ClientRowPhrase.MayDisconnect(confirmed));
            Assert.False(ClientRowPhrase.MayDisconnect(ours));
            Assert.False(ClientRowPhrase.MayDisconnect(reported));
            Assert.False(ClientRowPhrase.MayDisconnect(unknown));
            Assert.False(ClientRowPhrase.MayDisconnect(maybeGone));
            Assert.False(ClientRowPhrase.MayDisconnect(null));
            Assert.Null(ClientRowPhrase.DisconnectReason(confirmed));
            Assert.Null(ClientRowPhrase.DisconnectReason(ours));
            Assert.Null(ClientRowPhrase.DisconnectReason(null));
            Assert.Equal("Unable to disconnect this client until the radio identifies it.", ClientRowPhrase.DisconnectReason(reported));
            // The radio identified this one; its reason is that it may have
            // left, never that the radio has not identified it (Track L11).
            Assert.Equal("Unable to disconnect WA2IWC; it may have disconnected already.", ClientRowPhrase.DisconnectReason(maybeGone));

            // Sentences 7 and 8, which the dialog reads straight from the
            // lexicon; one text at both tiers.
            Assert.Equal("Info about the client changed while you were using this dialog. Please select the client again to see currently available status about this client.",
                Lexicon.Get("connect.multiflex.changed_while_confirming"));
            Assert.Equal("Disconnect requested for WA2IWC.",
                Lexicon.Get("connect.multiflex.disconnect_requested", ("station", confirmed.NameForSentence)));
        }
    }
}
