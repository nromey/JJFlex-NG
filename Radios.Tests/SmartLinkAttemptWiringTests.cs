#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Flex.Smoothlake.Vita;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The SmartLink method walk where it meets the real radio layer: the
    /// request through a real session owner and a mock WAN server, the proof's
    /// two inputs through a real FlexLib Radio made live without a socket, and
    /// the wiring that cannot be run (FlexLib's connect, the open, the app's
    /// connect driver) pinned at the source.
    /// </summary>
    /// <remarks>
    /// Nothing reaches a network, a radio or the operator's settings, nothing
    /// is spoken (the walk's speech seam is replaced for the tests that say
    /// anything), and no test saves SmartLink accounts: the account whose tier
    /// governs is the rig's own adopted account, set in memory.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class SmartLinkAttemptWiringTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly RadioConfigStaticsScope _scope = new(nameof(SmartLinkAttemptWiringTests));
        private readonly List<IDisposable> _owned = new();
        private readonly string _account = "tiers-b-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@example.invalid";
        private static readonly IPAddress RadioAddress = IPAddress.Parse("192.0.2.1");

        // The process's client-id slot, taken under a TEST prefix and planted.
        // The tests here that run FlexLib's connect reach the rig's client
        // identity, which reads the process slot; unplanted, that takes a
        // real session slot under the production name and holds it until the
        // test host exits, so a copy of the program started meanwhile would be
        // handed a different slot and a different stored id.
        private readonly GuiClientSlot _slot = GuiClientSlot.Acquire(@"Local\JJFlexRadio.Tests.SmartLinkAttemptWiring." + Guid.NewGuid().ToString("N") + ".");
        private readonly IDisposable _slotScope;

        public SmartLinkAttemptWiringTests()
        {
            _slotScope = GuiClientSlot.OverrideForTests(_slot);
        }

        public void Dispose()
        {
            foreach (var d in _owned) { try { d.Dispose(); } catch { } }
            _slotScope.Dispose();
            _slot.Dispose();
            _scope.Dispose();
        }

        private static string Serial() => "4711-" + Random.Shared.Next(1000, 9999) + "-8600-" + Random.Shared.Next(1000, 9999);

        private static readonly AdvertisedPorts ManualOnly = new(4994, 4993, false, -1, -1, false);
        private static readonly AdvertisedPorts BothPairs = new(4994, 4993, true, 21000, 21001, false);
        private static readonly AdvertisedPorts NoPorts = new(-1, -1, false, -1, -1, true);

        private static Radio WanRadio(string serial, AdvertisedPorts advertised)
        {
            var r = (Radio)Activator.CreateInstance(typeof(Radio), Private, null, new object[] { true }, null)!;
            typeof(Radio).GetField("_ip", Private)!.SetValue(r, RadioAddress);
            typeof(Radio).GetField("_serial", Private)!.SetValue(r, serial);
            r.AdvertisedPorts = advertised;
            bool manual = advertised.ManualTlsPort != -1 && advertised.ManualUdpPort != -1;
            r.PublicTlsPort = manual ? advertised.ManualTlsPort : advertised.UpnpTlsPort;
            r.PublicUdpPort = manual ? advertised.ManualUdpPort : advertised.UpnpUdpPort;
            r.IsPortForwardOn = manual;
            r.RequiresHolePunch = advertised.RequiresHolePunch;
            r.NegotiatedHolePunchPort = -1;
            return r;
        }

        private (FlexBase rig, MockWanServer wan, WanSessionOwner owner) Setup(SmartLinkConnectionMode tier)
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
            _owned.Add(new Releaser(rig));
            SetTier(rig, tier);
            var wan = new MockWanServer();
            var owner = new WanSessionOwner(Guid.NewGuid().ToString("N").Substring(0, 12), _account, wan,
                new DirectPassthroughSink(), new[] { 50, 50, 50 });
            _owned.Add(owner);
            owner.Connect();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!owner.IsConnected && sw.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            Assert.True(owner.IsConnected, "the mock session never connected");
            return (rig, wan, owner);
        }

        private void SetTier(FlexBase rig, SmartLinkConnectionMode tier) =>
            typeof(FlexBase).GetField("_currentAccount", Private)!.SetValue(rig, new SmartLinkAccount
            {
                Email = _account, FriendlyName = _account, ConnectionMode = tier, TierChosen = true,
            });

        /// <summary>The broker answers "ready" for each request as it is sent, up to <paramref name="times"/>.</summary>
        private static Task AnswerReady(MockWanServer wan, int times = 1) => Task.Run(() =>
        {
            int answered = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (answered < times && sw.ElapsedMilliseconds < 8000)
            {
                if (wan.SendConnectMessageCallCount > answered)
                {
                    answered++;
                    wan.RaiseWanRadioConnectReady("0xABCD000" + answered, wan.LastSendConnectSerial!);
                }
                else Thread.Sleep(5);
            }
        });

        private sealed class Releaser : IDisposable
        {
            private readonly FlexBase _rig;
            public Releaser(FlexBase rig) => _rig = rig;
            public void Dispose() { _rig.theRadio = null; _rig.Dispose(); }
        }

        private static object? PendingRequest(WanSessionOwner owner) =>
            typeof(WanSessionOwner).GetField("_pendingRadioConnect", Private)!.GetValue(owner);

        private static T Call<T>(FlexBase rig, string method, params object?[] args) =>
            (T)typeof(FlexBase).GetMethod(method, Private)!.Invoke(rig, args)!;

        // ── The request: given up means no longer listened for ──────────────

        [Fact]
        public void A_ready_that_arrives_after_the_request_was_given_up_decides_nothing()
        {
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPortForwardOnly);
            rig.SmartLinkHandleWaitMsInUse = 150;
            string serial = Serial();
            var radio = WanRadio(serial, ManualOnly);

            bool sent = rig.DialSmartLink(radio, owner, new RadioConfig());

            Assert.False(sent);
            Assert.Equal(1, wan.SendConnectMessageCallCount);
            var leg = rig.SmartLinkLegInFlight!;
            var attempt = leg.Current!;
            Assert.Equal(SmartLinkAttemptEnd.NoHandle, attempt.End);
            Assert.True(leg.Ended);
            // The request's token was cancelled, and the session stopped listening for it.
            Assert.True(attempt.RequestToken.IsCancellationRequested);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (PendingRequest(owner) != null && sw.ElapsedMilliseconds < 2000) Thread.Sleep(10);
            Assert.Null(PendingRequest(owner));
            Assert.False(SmartLinkRequestLedger.IsOutstanding(_account, serial));

            // The late "ready".
            wan.RaiseWanRadioConnectReady("0xLATE0001", serial);
            Thread.Sleep(100);

            Assert.Equal(SmartLinkAttemptEnd.NoHandle, attempt.End);
            Assert.Null(rig.SmartLinkMethodInUse);
            Assert.True(string.IsNullOrEmpty(radio.WANConnectionHandle), "a late ready gave the radio a handle");
            var record = SmartLinkConnectRecords.For(serial)!;
            Assert.Single(record.Attempts);
            Assert.Equal(SmartLinkAttemptEnd.NoHandle, record.LastAttempt!.End);
            Assert.False(record.LastAttempt.HandleArrived);
            Assert.Null(record.MethodUsed);
        }

        [Fact]
        public void A_request_that_is_not_answered_ends_the_leg_even_when_another_method_was_planned()
        {
            // The plan's ordering rule: a fallback only follows an attempt that
            // received its handle, so no request of ours is unanswered when
            // the next one is sent. With no handle, nothing more is sent.
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPlusUpnp);
            rig.SmartLinkHandleWaitMsInUse = 150;
            string serial = Serial();
            var radio = WanRadio(serial, BothPairs);

            Assert.False(rig.DialSmartLink(radio, owner, new RadioConfig()));
            var leg = rig.SmartLinkLegInFlight!;
            Assert.Equal(new[] { SmartLinkMethod.ManualPorts, SmartLinkMethod.UpnpPorts }, leg.Methods);
            Assert.True(leg.Ended);
            Assert.False(leg.RunConnectStage());                      // the leg is over; asking again sends nothing
            Assert.Equal(1, wan.SendConnectMessageCallCount);

            // "After the next attempt has started": the walk has moved on (to
            // another path). A late ready finds no request of ours pending.
            wan.RaiseWanRadioConnectReady("0xLATE0002", serial);
            Thread.Sleep(100);
            Assert.Single(SmartLinkConnectRecords.For(serial)!.Attempts);
            Assert.Null(rig.SmartLinkMethodInUse);
        }

        [Fact]
        public void After_an_unanswered_request_a_later_answer_for_the_same_radio_is_marked_as_possibly_the_earlier_ones()
        {
            // WHAT IS NOT GUARANTEED, pinned so it stays said. "Ready" names a
            // radio, not a request, and nothing cancels a request on the wire.
            // A retry for the same radio soon after an unanswered request may
            // be answered by the first request's late ready. It cannot be told
            // apart; it is marked, in the record and the trace.
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPortForwardOnly);
            rig.SmartLinkHandleWaitMsInUse = 150;
            string serial = Serial();
            var radio = WanRadio(serial, ManualOnly);
            Assert.False(rig.DialSmartLink(radio, owner, new RadioConfig()));

            rig.SmartLinkHandleWaitMsInUse = 5000;
            var ready = AnswerReady(wan, times: 2);                   // answers the retry's request (the count is already 1)
            wan.RaiseWanRadioConnectReady("0xIGNORED", serial);       // nothing pending yet: dropped
            Assert.True(rig.DialSmartLink(radio, owner, new RadioConfig()));
            ready.Wait(1000);

            var record = SmartLinkConnectRecords.For(serial)!;
            Assert.True(record.LastAttempt!.HandleArrived);
            Assert.Contains("may have been meant for it", record.LastAttempt.EndDetail);

            // The mark outlives the attempt's interim state: whatever the
            // attempt ends as, the record still says its answer was in doubt.
            Assert.False(rig.SmartLinkLegInFlight!.ContinueAfterTransport(false, out _));
            var ended = SmartLinkConnectRecords.For(serial)!.LastAttempt!;
            Assert.Equal(SmartLinkAttemptEnd.ConnectFailed, ended.End);
            Assert.Contains("may have been meant for it", ended.EndDetail);

            // Control: with no unanswered request before it, the answer carries no such mark.
            string other = Serial();
            var ready2 = AnswerReady(wan, times: 3);
            Assert.True(rig.DialSmartLink(WanRadio(other, ManualOnly), owner, new RadioConfig()));
            ready2.Wait(1000);
            Assert.DoesNotContain("may have been meant", SmartLinkConnectRecords.For(other)!.LastAttempt!.EndDetail);
        }

        [Fact]
        public void A_new_dial_on_the_rig_ends_an_undecided_attempt_first()
        {
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPortForwardOnly);
            string serial = Serial();
            var radio = WanRadio(serial, ManualOnly);
            var ready = AnswerReady(wan, times: 2);
            Assert.True(rig.DialSmartLink(radio, owner, new RadioConfig()));
            var first = rig.SmartLinkLegInFlight!.Current!;
            Assert.False(first.IsOver);
            long firstConnect = SmartLinkConnectRecords.For(serial)!.ConnectId;

            Assert.True(rig.DialSmartLink(radio, owner, new RadioConfig()));
            ready.Wait(1000);

            Assert.True(first.IsOver);                                 // the retry settled the earlier attempt before sending
            Assert.NotSame(first, rig.SmartLinkLegInFlight!.Current);
            var record = SmartLinkConnectRecords.For(serial)!;
            Assert.True(record.ConnectId > firstConnect);
            Assert.Equal(SmartLinkAttemptEnd.Dialled, record.LastAttempt!.End);
        }

        [Fact]
        public void Escape_during_the_wait_for_the_handle_is_a_cancel_and_the_request_is_let_go()
        {
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPlusUpnp);
            string serial = Serial();
            var radio = WanRadio(serial, BothPairs);
            var escape = Task.Run(() => { Thread.Sleep(150); rig.RequestCancel(); });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(rig.DialSmartLink(radio, owner, new RadioConfig()));
            sw.Stop();
            escape.Wait(1000);

            Assert.True(sw.ElapsedMilliseconds < 3000, "the wait did not end at the cancel");
            Assert.True(rig.SmartLinkLegCancelled);
            Assert.Equal(SmartLinkAttemptEnd.Cancelled, SmartLinkConnectRecords.For(serial)!.LastAttempt!.End);
            Assert.Equal(1, wan.SendConnectMessageCallCount);
            Assert.True(rig.SmartLinkLegInFlight!.Current!.RequestToken.IsCancellationRequested);
        }

        [Fact]
        public void A_brokering_session_that_has_gone_sends_nothing_and_no_other_session_is_used()
        {
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPortForwardOnly);
            string serial = Serial();
            owner.Disconnect();                                          // the account signed out, or its session was closed
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (owner.IsConnected && sw.ElapsedMilliseconds < 3000) Thread.Sleep(10);
            Assert.False(owner.IsConnected);
            int sentBefore = wan.SendConnectMessageCallCount;

            Assert.False(rig.DialSmartLink(WanRadio(serial, ManualOnly), owner, new RadioConfig()));

            Assert.Equal(sentBefore, wan.SendConnectMessageCallCount);
            var attempt = SmartLinkConnectRecords.For(serial)!.LastAttempt!;
            Assert.Equal(SmartLinkAttemptEnd.NotSent, attempt.End);
            Assert.Contains("no other account", attempt.EndDetail);
        }

        // ── Two methods, through the real radio layer ───────────────────────

        [Fact]
        public void A_failed_first_method_is_followed_by_the_next_with_its_own_request_and_its_own_ports_and_the_move_is_said_once()
        {
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPlusUpnp);
            string serial = Serial();
            var radio = WanRadio(serial, BothPairs);
            var said = new List<string>();
            var seam = ConnectWalkAnnouncer.Speak;
            ConnectWalkAnnouncer.Speak = said.Add;
            rig.SuppressSpeech = false;
            try
            {
                var ready = AnswerReady(wan, times: 2);
                Assert.True(rig.DialSmartLink(radio, owner, new RadioConfig()));
                Assert.Equal(SmartLinkMethod.ManualPorts, rig.SmartLinkMethodInUse);
                Assert.Equal(0, wan.LastSendConnectHolePunchPort);
                Assert.Equal(4994, radio.PublicTlsPort);

                // FlexLib's connect for the first method failed. The rig holds
                // no radio object here, so the second method's own connect
                // fails at once too: what is under test is everything around it.
                var leg = rig.SmartLinkLegInFlight!;
                Assert.False(leg.ContinueAfterTransport(false, out bool movedOn));
                ready.Wait(1000);

                Assert.Equal(2, wan.SendConnectMessageCallCount);
                Assert.Equal(21000, radio.PublicTlsPort);              // the UPnP pair was dialled, not the manual one again
                Assert.Equal(21001, radio.PublicUdpPort);
                Assert.Equal(0, wan.LastSendConnectHolePunchPort);     // a ports method never sends a punch port
                Assert.False(radio.RequiresHolePunch);

                var record = SmartLinkConnectRecords.For(serial)!;
                Assert.Equal(2, record.Attempts.Count);
                Assert.Equal(SmartLinkMethod.ManualPorts, record.Attempts[0].Method);
                Assert.Equal(SmartLinkAttemptEnd.ConnectFailed, record.Attempts[0].End);
                Assert.Equal(4994, record.Attempts[0].TlsPortSent);
                Assert.Equal(SmartLinkMethod.UpnpPorts, record.Attempts[1].Method);
                Assert.Equal(21000, record.Attempts[1].TlsPortSent);
                Assert.True(record.Attempts[1].HandleArrived);
                Assert.Equal(SmartLinkAttemptEnd.ConnectFailed, record.Attempts[1].End);
                Assert.Null(record.MethodUsed);                         // nothing is in use
                Assert.Null(rig.SmartLinkMethodInUse);                  // cleared with the attempt that failed
                Assert.True(leg.Ended);

                // Said once, in the walk's own sentence, with the two methods' names.
                string sentence = Assert.Single(said);
                Assert.Equal(
                    ConnectWalkAnnouncer.MoveSentence(
                        ConnectWalkAnnouncer.MethodLegName(SmartLinkMethod.ManualPorts),
                        ConnectWalkAnnouncer.MethodLegName(SmartLinkMethod.UpnpPorts)),
                    sentence);
            }
            finally
            {
                ConnectWalkAnnouncer.Speak = seam;
                rig.SuppressSpeech = true;
            }
        }

        [Fact]
        public void A_tier_lowered_mid_walk_stops_the_next_method_from_being_sent()
        {
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPlusUpnp);
            string serial = Serial();
            var radio = WanRadio(serial, BothPairs);
            var ready = AnswerReady(wan);
            Assert.True(rig.DialSmartLink(radio, owner, new RadioConfig()));
            ready.Wait(1000);

            SetTier(rig, SmartLinkConnectionMode.ManualPortForwardOnly);     // the operator lowered the tier while the first method was failing
            Assert.False(rig.SmartLinkLegInFlight!.ContinueAfterTransport(false, out _));

            Assert.Equal(1, wan.SendConnectMessageCallCount);
            var record = SmartLinkConnectRecords.For(serial)!;
            Assert.Equal(SmartLinkAttemptEnd.NotSent, record.Attempts[1].End);
            Assert.Equal(SmartLinkMethod.UpnpPorts, record.Attempts[1].Method);
        }

        [Fact]
        public void A_connection_choice_narrowed_mid_walk_is_read_again_for_the_next_method()
        {
            // The profile is read afresh for each later method. Here it turns
            // to "Hole punch always", which a Tier 2 account does not allow.
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPlusUpnp);
            string serial = Serial();
            var radio = WanRadio(serial, BothPairs);
            var profile = new RadioConfig();
            var ready = AnswerReady(wan);
            Assert.True(rig.DialSmartLink(radio, owner, profile, () => profile, reconnect: false));
            ready.Wait(1000);

            profile.ConnectionPreference = RadioConnectionPreference.HolePunch;
            Assert.False(rig.SmartLinkLegInFlight!.ContinueAfterTransport(false, out _));
            Assert.Equal(1, wan.SendConnectMessageCallCount);
            Assert.Equal(SmartLinkAttemptEnd.NotSent, SmartLinkConnectRecords.For(serial)!.Attempts[1].End);
        }

        // ── Reconnects ──────────────────────────────────────────────────────

        [Fact]
        public void A_reconnect_dials_the_method_that_last_proved_itself_first_and_only_if_the_policy_still_allows_it()
        {
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPlusUpnp);
            string serial = Serial();
            FlexBase.RememberProvenSmartLinkMethod(serial, SmartLinkMethod.UpnpPorts, forcedPath: false);
            try
            {
                var radio = WanRadio(serial, BothPairs);
                var profile = new RadioConfig();
                var ready = AnswerReady(wan, times: 3);

                // An ordinary connect keeps the policy's order.
                Assert.True(rig.DialSmartLink(radio, owner, profile));
                Assert.Equal(SmartLinkMethod.ManualPorts, rig.SmartLinkMethodInUse);

                // A reconnect tries the proven method first, and the record says so.
                Assert.True(rig.DialSmartLink(radio, owner, profile, () => profile, reconnect: true));
                Assert.Equal(SmartLinkMethod.UpnpPorts, rig.SmartLinkMethodInUse);
                Assert.Equal(21000, radio.PublicTlsPort);
                var record = SmartLinkConnectRecords.For(serial)!;
                Assert.Equal(new[] { SmartLinkMethod.UpnpPorts, SmartLinkMethod.ManualPorts }, record.MethodOrder);

                // The tier is lowered: the policy change takes effect on this
                // next attempt, and the remembered method is not tried.
                SetTier(rig, SmartLinkConnectionMode.ManualPortForwardOnly);
                Assert.True(rig.DialSmartLink(radio, owner, profile, () => profile, reconnect: true));
                ready.Wait(1000);
                Assert.Equal(SmartLinkMethod.ManualPorts, rig.SmartLinkMethodInUse);
                Assert.Equal(new[] { SmartLinkMethod.ManualPorts }, SmartLinkConnectRecords.For(serial)!.MethodOrder);
            }
            finally { FlexBase.ForgetProvenSmartLinkMethodsForTests(); }
        }

        [Fact]
        public void A_forced_path_teaches_nothing_and_another_radio_learns_nothing_from_this_one()
        {
            string serial = Serial(), other = Serial();
            try
            {
                FlexBase.RememberProvenSmartLinkMethod(serial, SmartLinkMethod.UpnpPorts, forcedPath: true);
                Assert.Null(FlexBase.LastProvenSmartLinkMethod(serial));

                FlexBase.RememberProvenSmartLinkMethod(serial, SmartLinkMethod.UpnpPorts, forcedPath: false);
                Assert.Equal(SmartLinkMethod.UpnpPorts, FlexBase.LastProvenSmartLinkMethod(serial));
                Assert.Null(FlexBase.LastProvenSmartLinkMethod(other));

                // A later forced connect does not overwrite what an ordinary one taught.
                FlexBase.RememberProvenSmartLinkMethod(serial, SmartLinkMethod.ManualPorts, forcedPath: true);
                Assert.Equal(SmartLinkMethod.UpnpPorts, FlexBase.LastProvenSmartLinkMethod(serial));
            }
            finally { FlexBase.ForgetProvenSmartLinkMethodsForTests(); }
        }

        // ── The proof's two inputs, through a real FlexLib Radio ────────────

        private static VitaDataReceivedCallback NewSocketWatch(Radio radio)
        {
            var callback = (VitaDataReceivedCallback)typeof(Radio).GetMethod("WatchedUdpCallback", Private)!.Invoke(radio, null)!;
            // A socket object that exists, so the generation counts as live. Never opened.
            radio.VitaSock = (global::Vita.VitaSocket)RuntimeHelpers.GetUninitializedObject(typeof(global::Vita.VitaSocket));
            return callback;
        }

        /// <summary>A dialled attempt whose transport has come up on a live offline radio.</summary>
        private (FlexBase rig, Radio radio, SmartLinkAttempt attempt, VitaDataReceivedCallback socket) AttemptWithTransportUp(
            SmartLinkConnectionMode tier, AdvertisedPorts ports, RadioConfig? profile = null)
        {
            var (rig, wan, owner) = Setup(tier);
            var radio = WanRadio(Serial(), ports);
            var ready = AnswerReady(wan);
            Assert.True(rig.DialSmartLink(radio, owner, profile ?? new RadioConfig()));
            ready.Wait(1000);
            rig.theRadio = radio;
            _owned.Add(new Detach(radio));

            var attempt = Call<SmartLinkAttempt>(rig, "WatchSmartLinkAttemptBeforeTransport", new object?[] { null });
            Assert.NotNull(attempt);
            OfflineCommandProducer.StartTls(radio);                      // a current command connection, no socket
            var socket = NewSocketWatch(radio);
            object?[] args = { attempt, true, true, false };
            Assert.True((bool)typeof(FlexBase).GetMethod("SmartLinkTransportResult", Private)!.Invoke(rig, args)!);
            Assert.True(attempt.TransportUp);
            Assert.Same(radio.CurrentCommandConnection, attempt.Connection);
            Assert.Equal(radio.VitaSocketGeneration, attempt.SocketGeneration);
            return (rig, radio, attempt, socket);
        }

        /// <summary>Takes the never-opened socket object off the radio before anything disposes it.</summary>
        private sealed class Detach : IDisposable
        {
            private readonly Radio _radio;
            public Detach(Radio radio) => _radio = radio;
            public void Dispose() => _radio.VitaSock = null;
        }

        private static bool WaitFor(Func<bool> condition, int ms = 5000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < ms) Thread.Sleep(10);
            return condition();
        }

        [Fact]
        public void The_radios_own_data_on_this_attempts_socket_and_connection_plus_the_open_is_the_proof()
        {
            var (_, radio, attempt, socket) = AttemptWithTransportUp(SmartLinkConnectionMode.ManualPortForwardOnly, ManualOnly);

            // Data first (it arrived before the open completed in the October trace)...
            socket(new IPEndPoint(RadioAddress, 7604), QualifiedUdpObservationTests.FlexVita(), 16);
            Assert.True(WaitFor(() => attempt.QualifiedUdpAfter != null), "the radio's own packet never reached the attempt");
            Assert.False(attempt.IsOver);
            // ...then the open.
            attempt.NoteOpenCompleted();
            Assert.True(attempt.IsProven);
        }

        [Fact]
        public void A_packet_from_another_address_never_reaches_the_attempt()
        {
            var (_, _, attempt, socket) = AttemptWithTransportUp(SmartLinkConnectionMode.ManualPortForwardOnly, ManualOnly);
            attempt.NoteOpenCompleted();

            socket(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 4993), QualifiedUdpObservationTests.FlexVita(), 16);
            Thread.Sleep(200);

            Assert.Null(attempt.QualifiedUdpAfter);
            Assert.False(attempt.IsOver);
        }

        [Fact]
        public void The_radios_data_on_a_socket_that_has_been_replaced_never_counts()
        {
            var (_, radio, attempt, oldSocket) = AttemptWithTransportUp(SmartLinkConnectionMode.ManualPortForwardOnly, ManualOnly);
            attempt.NoteOpenCompleted();

            NewSocketWatch(radio);                                        // a newer socket is the live one now
            oldSocket(new IPEndPoint(RadioAddress, 4993), QualifiedUdpObservationTests.FlexVita(), 16);
            Thread.Sleep(200);

            Assert.Null(attempt.QualifiedUdpAfter);
            Assert.False(attempt.IsOver);
        }

        [Fact]
        public void This_attempts_command_connection_falling_before_proof_ends_it_as_a_method_failure()
        {
            var (_, radio, attempt, _) = AttemptWithTransportUp(SmartLinkConnectionMode.ManualPortForwardOnly, ManualOnly);
            radio.VitaSock = null;                                        // nothing for FlexLib's own teardown to dispose

            var transport = OfflineCommandProducer.Transport(radio);
            transport.GetType().GetMethod("Disconnect", BindingFlags.Instance | BindingFlags.Public)!.Invoke(transport, null);

            Assert.True(WaitFor(() => attempt.IsOver), "the fall never reached the attempt");
            Assert.Equal(SmartLinkAttemptEnd.CommandConnectionLost, attempt.End);
            Assert.True(SmartLinkAttemptRules.IsMethodFailure(attempt.End));
        }

        [Fact]
        public void A_hole_punch_attempt_is_watched_and_proven_the_same_way()
        {
            // Settled item 2: a punch dialled as the only method is an attempt
            // like any other.
            var (rig, radio, attempt, socket) = AttemptWithTransportUp(SmartLinkConnectionMode.AutomaticHolePunch, NoPorts);
            Assert.Equal(SmartLinkMethod.HolePunch, attempt.Method);
            Assert.True(radio.RequiresHolePunch);
            Assert.True(rig.ConnectedByHolePunch);

            socket(new IPEndPoint(RadioAddress, 31007), QualifiedUdpObservationTests.FlexVita(), 16);   // the port a NAT rewrote
            Assert.True(WaitFor(() => attempt.QualifiedUdpAfter != null));
            attempt.NoteOpenCompleted();
            Assert.True(attempt.IsProven);
        }

        [Fact]
        public void A_proof_that_fails_during_the_open_stops_the_setup_wait_without_being_a_cancel()
        {
            // The open's shared wait, on its own clock: the attempt's failure
            // ends it as Aborted, and an operator cancel still wins over it.
            long now = 0;
            bool failed = false, cancelled = false;
            ConnectSetupWait.World World() => new ConnectSetupWait.World
            {
                Now = () => now,
                Sleep = ms => { now += ms; if (now >= 2_000) failed = true; },
                Cancelled = () => cancelled,
                Aborted = () => failed,
            };

            var stopped = ConnectSetupWait.Run(World(), 0);
            Assert.Equal(ConnectSetupWait.End.Aborted, stopped.Ended);
            Assert.InRange(stopped.EndedAt, 2_000, 2_000 + ConnectSetupWait.IntervalMs);

            now = 0; failed = true; cancelled = true;
            Assert.Equal(ConnectSetupWait.End.Cancelled, ConnectSetupWait.Run(World(), 0).Ended);

            // And a wait nobody stops is not stopped by the new input's default.
            now = 0;
            var untouched = ConnectSetupWait.Run(new ConnectSetupWait.World
            {
                Now = () => now, Sleep = ms => now += ms, AntennasHere = () => true, StationNamed = () => true,
            }, 0);
            Assert.Equal(ConnectSetupWait.End.Ready, untouched.Ended);
        }

        // ── Escape during the TCP and TLS stage ─────────────────────────────

        /// <summary>
        /// A peer on the loopback address that accepts a TCP connection and
        /// then says nothing: a TLS handshake against it waits for a ServerHello
        /// that never comes, which is a real blocked establishment with no
        /// radio, no network and nothing leaving this machine.
        /// </summary>
        private sealed class SilentTlsPeer : IDisposable
        {
            private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly List<System.Net.Sockets.TcpClient> _clients = new();
            private readonly ManualResetEventSlim _accepted = new(false);
            private readonly ManualResetEventSlim _closed = new(false);
            private int _acceptedCount;

            public SilentTlsPeer()
            {
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = AcceptLoop();
            }

            public int Port { get; }
            public int Accepted => Volatile.Read(ref _acceptedCount);
            public bool WaitForClient(int ms) => _accepted.Wait(ms);
            /// <summary>True once a client that connected has closed its side.</summary>
            public bool WaitForClientClosed(int ms) => _closed.Wait(ms);

            private async Task AcceptLoop()
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        lock (_clients) _clients.Add(client);
                        Interlocked.Increment(ref _acceptedCount);
                        _accepted.Set();
                        _ = Drain(client);
                    }
                }
                catch (Exception) { /* the listener was stopped */ }
            }

            private async Task Drain(System.Net.Sockets.TcpClient client)
            {
                var buffer = new byte[4096];
                try { while (await client.GetStream().ReadAsync(buffer) > 0) { } }
                catch (Exception) { }
                _closed.Set();
            }

            public void Dispose()
            {
                try { _listener.Stop(); } catch { }
                lock (_clients) foreach (var c in _clients) { try { c.Dispose(); } catch { } }
            }
        }

        /// <summary>A dialled attempt whose radio's forwarded TCP port is the silent peer.</summary>
        private (FlexBase rig, Radio radio, SmartLinkLeg leg, SmartLinkAttempt attempt, List<CommandConnectionState> states) DialledAt(SilentTlsPeer peer)
        {
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPortForwardOnly);
            var radio = WanRadio(Serial(), new AdvertisedPorts(peer.Port, 4993, false, -1, -1, false));
            typeof(Radio).GetField("_ip", Private)!.SetValue(radio, IPAddress.Loopback);
            var ready = AnswerReady(wan);
            Assert.True(rig.DialSmartLink(radio, owner, new RadioConfig()));
            ready.Wait(1000);
            rig.theRadio = radio;
            var states = new List<CommandConnectionState>();
            radio.CommandConnectionChanged += report => { lock (states) states.Add(report.State); };
            var leg = rig.SmartLinkLegInFlight!;
            return (rig, radio, leg, leg.Current!, states);
        }

        /// <summary>The bound an Escape must be answered within. The stage's own timeout is 15 seconds, twice over.</summary>
        private const int CancelToReturnBoundMs = 3000;

        [Fact]
        public void Escape_during_a_blocked_TLS_handshake_ends_the_connect_promptly_and_nothing_completes_later()
        {
            using var peer = new SilentTlsPeer();
            var (rig, radio, leg, attempt, states) = DialledAt(peer);

            // The production call for a method's transport: FlexLib's own
            // connect, through the real TLS wrapper, on this thread's task.
            var connect = Task.Run(() => leg.RunConnectStage());
            Assert.True(peer.WaitForClient(5000), "the TCP connect never reached the peer");
            // POSITIVE CONTROL: it really is blocked in the handshake.
            Assert.False(connect.Wait(800), "the establishment was not blocked, so this test proves nothing");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            rig.RequestCancel();                                    // Escape
            Assert.True(connect.Wait(CancelToReturnBoundMs),
                "Escape did not end the TLS wait: the connect was still blocked " + CancelToReturnBoundMs + " ms later");
            sw.Stop();

            Assert.False(connect.Result);
            Assert.True(leg.Cancelled);                             // recorded as a cancel, and nothing is walked on
            Assert.Equal(SmartLinkAttemptEnd.Cancelled, attempt.End);
            Assert.False(attempt.TransportUp);
            Assert.False(radio.Connected);
            Assert.InRange(sw.ElapsedMilliseconds, 0, CancelToReturnBoundMs);

            // Nothing completes in the background: our socket is closed, the
            // TLS 1.2 fallback did not dial a second one, and no "connected"
            // is ever reported for this radio object.
            Assert.True(peer.WaitForClientClosed(3000), "the cancelled attempt's socket was left open");
            Thread.Sleep(400);
            Assert.Equal(1, peer.Accepted);
            lock (states) Assert.DoesNotContain(CommandConnectionState.Connected, states);
            Assert.False(radio.Connected);
            Assert.Equal(SmartLinkAttemptEnd.Cancelled, SmartLinkConnectRecords.For(radio.Serial)!.LastAttempt!.End);

            // This test ran FlexLib's connect for real, which reads the
            // process's client-id slot. It read the planted one.
            Assert.False(GuiClientSlot.ProductionSlotTaken,
                "a test here took the production client-id slot; it must run under the planted test slot");
        }

        [Fact]
        public void An_attempt_ended_from_outside_during_its_TLS_wait_ends_that_wait_too()
        {
            // The attempt's own cancellation, not the operator's: a new dial on
            // the rig, or the rig being disconnected, while FlexLib's connect
            // is still waiting. The wait must not outlive its attempt.
            using var peer = new SilentTlsPeer();
            var (rig, radio, leg, attempt, states) = DialledAt(peer);

            var connect = Task.Run(() => leg.RunConnectStage());
            Assert.True(peer.WaitForClient(5000));
            Assert.False(connect.Wait(800));

            leg.Abandon(operatorCancelled: false, "the rig was disconnected");
            Assert.True(connect.Wait(CancelToReturnBoundMs), "the connect outlived its attempt");

            Assert.False(connect.Result);
            Assert.False(rig.CancelRequested);                      // not an operator cancel
            Assert.False(attempt.TransportUp);
            Assert.False(radio.Connected);
            Assert.True(peer.WaitForClientClosed(3000));
            Thread.Sleep(300);
            Assert.Equal(1, peer.Accepted);
            lock (states) Assert.DoesNotContain(CommandConnectionState.Connected, states);
        }

        [Fact]
        public void An_abort_that_arrives_after_the_connect_returned_touches_nothing()
        {
            // The guard against a late abort closing the NEXT attempt's
            // transport: an establishment that has finished cannot be ended.
            var (rig, radio, attempt, _) = AttemptWithTransportUp(SmartLinkConnectionMode.ManualPortForwardOnly, ManualOnly);
            radio.VitaSock = null;
            Assert.True(radio.Connected);

            // No establishment is in flight: Escape now is only the flag.
            rig.RequestCancel();
            Thread.Sleep(300);
            Assert.True(radio.Connected, "Escape with no establishment in flight closed a live connection");

            // And an establishment object that has finished refuses to be ended.
            var begin = typeof(FlexBase).GetMethod("BeginTransportEstablishment", Private)!;
            var end = typeof(FlexBase).GetMethod("EndTransportEstablishment", Private)!;
            var abort = typeof(FlexBase).GetMethod("AbortTransportEstablishment", Private)!;
            var field = typeof(FlexBase).GetField("_establishing", Private)!;
            typeof(FlexBase).GetField("_cancelRequested", Private)!.SetValue(rig, false);

            var second = new SmartLinkAttempt(1, 2, SmartLinkMethod.UpnpPorts, 0, () => Environment.TickCount64);
            begin.Invoke(rig, new object[] { second, radio });
            object establishment = field.GetValue(rig)!;
            Assert.False((bool)end.Invoke(rig, new object[] { second })!);        // finished, and it was not aborted
            Assert.Null(field.GetValue(rig));
            Assert.False((bool)abort.Invoke(rig, new object[] { establishment, "a late abort" })!);
            Assert.True(radio.Connected, "a late abort closed a connection that was no longer its own");

            // Control: the same call on an establishment still waiting does end it.
            var third = new SmartLinkAttempt(1, 3, SmartLinkMethod.UpnpPorts, 0, () => Environment.TickCount64);
            begin.Invoke(rig, new object[] { third, radio });
            object waiting = field.GetValue(rig)!;
            Assert.True((bool)abort.Invoke(rig, new object[] { waiting, "ended while waiting" })!);
            Assert.False(radio.Connected);
            Assert.True((bool)end.Invoke(rig, new object[] { third })!);          // and the connect is told it was ended
        }

        [Fact]
        public void Both_vendor_connects_are_inside_an_establishment_that_Escape_can_end()
        {
            string attempts = Src("Radios", "FlexBase.SmartLinkAttempts.cs");
            // Begun where the proof watch is subscribed, which both vendor
            // connects call just before (pinned in the test above this one)...
            string watch = Body(attempts, "private SmartLinkAttempt? WatchSmartLinkAttemptBeforeTransport(", "private bool SmartLinkTransportResult(");
            Assert.Contains("BeginTransportEstablishment(attempt, theRadio!);", watch);
            // ...and ended first thing where both hand their result over.
            string result = Body(attempts, "private bool SmartLinkTransportResult(", "private bool SmartLinkLegEndedAtTheRequest()");
            Assert.True(result.IndexOf("EndTransportEstablishment(attempt)", StringComparison.Ordinal)
                        < result.IndexOf("attempt.NoteTransportUp(", StringComparison.Ordinal),
                "an establishment that was ended must be known before the transport is called up");
            // Escape reaches it, off the caller's thread.
            string cancel = Body(Src("Radios", "FlexBase.cs"), "public void RequestCancel()", "public event Action? GuiClientChanged;");
            Assert.Contains("AbortTransportEstablishmentOnCancel();", cancel);
            string abort = Body(attempts, "private bool AbortTransportEstablishment(", "private void AbortTransportEstablishmentOnCancel()");
            Assert.Contains("if (establishment.Finished || establishment.Aborted) return false;", abort);
            Assert.Contains("establishment.Radio.Disconnect();", abort);      // its own radio object, never "the current one"
            Assert.DoesNotContain("theRadio", abort);
            // The TLS wrapper's two waits both end with its client's cancellation.
            string tls = Src("FlexLib_API", "FlexLib", "SslClientTls12.cs");
            Assert.Contains("CreateLinkedTokenSource(connectTimeoutCts.Token, _cts.Token)", tls);
            Assert.Contains("CreateLinkedTokenSource(authenticationTimeoutCts.Token, _cts.Token)", tls);
            Assert.Contains("_cts.Token.ThrowIfCancellationRequested();", tls);
        }

        // ── Source pins: the wiring that cannot be run from here ────────────

        private static string Src(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()));

        private static string Body(string source, string header, string until)
        {
            int at = source.IndexOf(header, StringComparison.Ordinal);
            Assert.True(at >= 0, "not found: " + header);
            int end = source.IndexOf(until, at + header.Length, StringComparison.Ordinal);
            Assert.True(end > at, "no end found after: " + header);
            return source.Substring(at, end - at);
        }

        [Fact]
        public void Start_runs_a_SmartLink_open_under_the_method_walk_and_commits_only_after_proof()
        {
            string flexBase = Src("Radios", "FlexBase.cs");
            string start = Body(flexBase, "public bool Start()", "private bool startOpenPhase(");
            Assert.Contains("var leg = SmartLinkLegOwingAnOpen();", start);
            Assert.Contains("return startOpenPhase(null, out _) && startCommit();", start);
            Assert.Contains("return StartUnderSmartLinkLeg(leg);", start);
            // The operator's cancel is cleared once per Start, not at each open inside it.
            Assert.Single(start.Split(new[] { "_cancelRequested = false;" }, StringSplitOptions.None).Skip(1));
            string open = Body(flexBase, "private bool startOpenPhase(", "private bool startCommit()");
            Assert.DoesNotContain("_cancelRequested = false;", open);
            // The rig's main thread starts in the commit, and nowhere in the open.
            Assert.DoesNotContain("mainThread = new Thread(mainThreadProc)", open);
            Assert.Contains("mainThread = new Thread(mainThreadProc)", Body(flexBase, "private bool startCommit()", "internal bool Disconnecting"));

            // The attempt's own stop is read at both of the open's waits, and
            // is told apart from the operator's cancel.
            Assert.Contains("return initialFreeSlices != -1 || _cancelRequested || AttemptFailed();", open);
            Assert.Contains("Aborted = AttemptFailed,", open);
            Assert.Contains("if (!_cancelRequested && waited.Ended == ConnectSetupWait.End.Aborted)", open);
            Assert.True(open.IndexOf("if (_cancelRequested)", StringComparison.Ordinal)
                        < open.IndexOf("if (AttemptFailed())", StringComparison.Ordinal),
                "the operator's cancel must be read before the attempt's stop");
            Assert.Equal(2, open.Split(new[] { "StopOpenForFailedAttempt(attempt," }, StringSplitOptions.None).Length - 1);

            string attempts = Src("Radios", "FlexBase.SmartLinkAttempts.cs");
            string under = Body(attempts, "private bool StartUnderSmartLinkLeg(", "private bool StopOpenForFailedAttempt(");
            Assert.Contains("bool proven = leg.RunOpenStage();", under);
            string stop = Body(attempts, "private bool StopOpenForFailedAttempt(", "private bool SmartLinkLegWillMoveOnAfterLoss(");
            Assert.DoesNotContain("_cancelRequested", stop);
            Assert.DoesNotContain("RequestCancel", stop);
            Assert.Contains("releaseGuiClientRegistration(why);", stop);
            Assert.Contains("teardownDisconnect(why);", stop);
        }

        [Fact]
        public void Both_vendor_connects_are_watched_for_the_attempts_proof_before_and_handed_its_result_after()
        {
            string flexBase = Src("Radios", "FlexBase.cs");
            foreach (string label in new[] { "Connect", "RetryConnect" })
            {
                string call = "rv = theRadio.Connect(prepareClientIdentityForConnect(\"" + label + "\"));";
                int at = flexBase.IndexOf(call, StringComparison.Ordinal);
                Assert.True(at > 0, "moved: " + call);
                string before = flexBase.Substring(at - 300, 300);
                string after = flexBase.Substring(at + call.Length, 400);
                Assert.Contains("WatchSmartLinkAttemptBeforeTransport(", before);
                Assert.Contains("rv = SmartLinkTransportResult(smartLinkAttempt, rv, continuation: ", after);
            }
            // The teardown and the dial never go through the operator's
            // Disconnect, and never touch another client or a radio setting.
            string attempts = Src("Radios", "FlexBase.SmartLinkAttempts.cs");
            string teardown = Body(attempts, "public void Teardown(SmartLinkAttempt attempt, string why)", "public void AnnounceMove(");
            Assert.Contains("_rig.releaseGuiClientRegistration(reason);", teardown);
            Assert.Contains("_rig.teardownDisconnect(reason);", teardown);
            foreach (string never in new[]
            {
                "_rig.Disconnect(", "DisconnectAllGuiClients", "DisconnectClientByHandle", "SendDisconnectSingleUser",
                "disconnect_users", "SetSmartLinkPortForwarding", "wan set", "RunNetworkDiagnosticAsync", "SendTestConnection",
            })
                Assert.DoesNotContain(never, attempts);
        }

        [Fact]
        public void An_auto_connect_holds_its_Connected_until_the_SmartLink_connection_is_proven()
        {
            string flexBase = Src("Radios", "FlexBase.cs");
            string said = Body(flexBase, "string connectedSentence = Lexicon.Get(\"connect.auto.connected\"", "return true;");
            Assert.Contains("if (SmartLinkLegOwingAnOpen() != null) _connectedSentenceOwedAtProof = connectedSentence;", said);
            // Exactly one place still says it at once, and that is the local leg's.
            Assert.Single(flexBase.Split(new[] { "Lexicon.Get(\"connect.auto.connected\"" }, StringSplitOptions.None).Skip(1));

            string attempts = Src("Radios", "FlexBase.SmartLinkAttempts.cs");
            string under = Body(attempts, "private bool StartUnderSmartLinkLeg(", "private bool StopOpenForFailedAttempt(");
            // Said after the proof, queued, never interrupting the walk's own sentence.
            Assert.True(under.IndexOf("leg.RunOpenStage()", StringComparison.Ordinal)
                        < under.IndexOf("ScreenReaderOutput.Speak(owed, VerbosityLevel.Critical, false);", StringComparison.Ordinal));
        }

        [Fact]
        public void An_auto_connects_owed_Connected_survives_a_retry_and_is_forgotten_only_when_the_connect_is_over()
        {
            // The Opus check's N3: the sentence was dropped at every new dial,
            // so an auto-connect that was proven on its second pass never said it.
            var owedField = typeof(FlexBase).GetField("_connectedSentenceOwedAtProof", Private)!;
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPortForwardOnly);
            string serial = Serial();
            var radio = WanRadio(serial, ManualOnly);
            var ready = AnswerReady(wan, times: 2);

            Assert.True(rig.DialSmartLink(radio, owner, new RadioConfig()));
            owedField.SetValue(rig, "Connected to FLEX-8600");

            // The retry: a new dial on the same rig.
            Assert.True(rig.DialSmartLink(radio, owner, new RadioConfig(), () => new RadioConfig(), reconnect: true));
            ready.Wait(1000);
            Assert.Equal("Connected to FLEX-8600", owedField.GetValue(rig));

            // The connect is over: forgotten.
            rig.Disconnect();
            Assert.Null(owedField.GetValue(rig));

            // And it is cleared only where it is said.
            string attempts = Src("Radios", "FlexBase.SmartLinkAttempts.cs");
            string under = Body(attempts, "private bool StartUnderSmartLinkLeg(", "private bool StopOpenForFailedAttempt(");
            int provenBranch = under.IndexOf("if (proven)", StringComparison.Ordinal);
            int cleared = under.IndexOf("_connectedSentenceOwedAtProof = null;", StringComparison.Ordinal);
            Assert.True(provenBranch > 0 && cleared > provenBranch, "the owed sentence must be forgotten only once it has been said");
            Assert.Single(under.Split(new[] { "_connectedSentenceOwedAtProof = null;" }, StringSplitOptions.None).Skip(1));
        }

        [Fact]
        public void A_loss_the_walk_will_move_on_from_is_not_narrated_over_the_move()
        {
            string flexBase = Src("Radios", "FlexBase.cs");
            string open = Body(flexBase, "private bool startOpenPhase(", "private bool startCommit()");
            Assert.Contains(
                "SmartLinkLegWillMoveOnAfterLoss(attempt) ? \"start_connection_lost_moving_on\" : \"start_connection_lost\"", open);
            // The one remaining unconditional record of the loss would be the defect.
            Assert.DoesNotContain("RecordAndSave(\"start_connection_lost\")", open);
        }

        [Fact]
        public void The_apps_connect_driver_never_walks_on_after_a_cancel_and_speaks_through_the_one_announcer()
        {
            string globals = Src("globals.vb");
            // Both loops ask the one rule.
            Assert.Equal(2, globals.Split(new[] { "walk.MayMoveOn(WalkCancelled())" }, StringSplitOptions.None).Length - 1);
            string cancelled = Body(globals, "Private Function WalkCancelled() As Boolean", "End Function");
            Assert.Contains("rig.CancelRequested OrElse rig.SmartLinkLegCancelled", cancelled);
            // The suppressed sign-in is not earned by a cancelled connect.
            Assert.Contains("If Not connectOk AndAlso suppressedAuthFailure AndAlso Not WalkCancelled() Then", globals);

            // One voice: the driver assembles no move sentence of its own.
            Assert.DoesNotContain("connect.walk.falling_back", globals);
            string announce = Body(globals, "Private Function AnnounceWalkOn(", "End Function");
            Assert.Single(announce.Split(new[] { "Radios.ConnectWalkAnnouncer.SayMove(walk.Current, walk.PeekNext.Value)" }, StringSplitOptions.None).Skip(1));
            Assert.DoesNotContain("ScreenReaderOutput.Speak", announce);

            // The path being left is retired before the next one connects.
            string resumed = Body(globals, "Private Function WalkOnAfterFailedOpen(", "End Function");
            int retire = resumed.IndexOf("RigControl.RetireSessionBeforeNextLeg(", StringComparison.Ordinal);
            int move = resumed.IndexOf("walk.MoveNext()", StringComparison.Ordinal);
            int run = resumed.IndexOf("RunWalkLeg(walk, suppressedAuthFailure)", StringComparison.Ordinal);
            Assert.True(retire > 0 && retire < move && move < run, "the old session must be retired before the next leg runs");

            // The SmartLink leg's method reaches the walk.
            Assert.Contains("walk.NoteSmartLinkMethod(RigControl.SmartLinkLegMethod)", Body(globals, "Private Function RunWalkLeg(", "End Function"));
        }

        [Fact]
        public void The_auto_connects_own_walk_never_goes_on_after_a_cancel_and_a_cancelled_connect_gathers_no_evidence()
        {
            string flexBase = Src("Radios", "FlexBase.cs");
            string auto = Body(flexBase, "public bool TryAutoConnect(AutoConnectConfig config", "public bool ReconnectRemote(");
            string legs = auto.Substring(auto.IndexOf("for (int leg = 0; leg < chain.Count; leg++)", StringComparison.Ordinal));
            // Checked before each leg is tried...
            int top = legs.IndexOf("if (_cancelRequested)", StringComparison.Ordinal);
            int firstWork = legs.IndexOf("if (path == ConnectPathKind.SmartLink)", StringComparison.Ordinal);
            Assert.True(top > 0 && top < firstWork, "the cancel must be read before a leg is tried");
            // ...and again after a leg's connect fails, before the next path is announced.
            int failed = legs.IndexOf("Connect() FAILED", StringComparison.Ordinal);
            int afterFailed = legs.IndexOf("if (_cancelRequested)", failed, StringComparison.Ordinal);
            int announce = legs.IndexOf("Lexicon.Get(\"connect.auto.leg_failed\"", failed, StringComparison.Ordinal);
            Assert.True(afterFailed > failed && afterFailed < announce, "a cancelled leg must not be followed by 'Trying'");
            // Every sentence that names a next path is withheld after a cancel.
            Assert.Contains("if (!lastLeg && !SuppressSpeech && !_cancelRequested)", legs);
            Assert.Contains("if (!SuppressSpeech && !_cancelRequested)", legs);

            string connect = Body(flexBase, "public bool Connect(string serial, bool lowBW", "public bool TryAutoConnect(");
            int cancelled = connect.IndexOf("if (RemoteRig && _cancelRequested)", StringComparison.Ordinal);
            int evidence = connect.IndexOf("RecordConnectFailure(BuildRemoteConnectFailureReport(", StringComparison.Ordinal);
            Assert.True(cancelled > 0 && cancelled < evidence, "a cancelled connect must not gather failure evidence");
        }

        [Fact]
        public void The_radio_layers_move_is_said_through_the_same_announcer_once_and_never_under_suppressed_speech()
        {
            string attempts = Src("Radios", "FlexBase.SmartLinkAttempts.cs");
            string announce = Body(attempts, "public void AnnounceMove(SmartLinkMethod from, SmartLinkMethod to)", "public void Proven(");
            Assert.Single(announce.Split(new[] { "ConnectWalkAnnouncer.SayMethodMove(from, to);" }, StringSplitOptions.None).Skip(1));
            Assert.True(announce.IndexOf("if (_rig.SuppressSpeech) return;", StringComparison.Ordinal)
                        < announce.IndexOf("ConnectWalkAnnouncer.SayMethodMove", StringComparison.Ordinal));
            Assert.DoesNotContain("ScreenReaderOutput.Speak", announce);

            string announcer = Src("Radios", "ConnectWalkAnnouncer.cs");
            Assert.Contains("ScreenReaderOutput.Speak(sentence, VerbosityLevel.Critical, true);", announcer);
            Assert.Single(announcer.Split(new[] { "ScreenReaderOutput.Speak(" }, StringSplitOptions.None).Skip(1));
        }

        [Fact]
        public void A_failed_connects_report_reads_this_radios_own_test_and_sends_no_test_of_its_own_while_the_switch_is_off()
        {
            string smartLink = Src("Radios", "FlexBase.SmartLink.cs");
            string report = Body(smartLink, "private ConnectFailureReport BuildRemoteConnectFailureReport(", "private static string YesNo(");
            Assert.Contains("probe = NetworkReportFor(r.Serial);", report);
            Assert.DoesNotContain("ActiveSession", report);
            Assert.DoesNotContain("LastNetworkReportFor", report);
            // Gated on the attempt's own method, not the radio's flag alone.
            Assert.Contains("LastSmartLinkAttemptMethod is Radios.SmartLink.SmartLinkMethod attempted", report);
            // A fresh probe only past the same switch as the test with no radio connected.
            int gate = report.IndexOf("Radios.SmartLink.NetworkTestGate.Decide(radioConnected: false, r.Serial)", StringComparison.Ordinal);
            int probe = report.IndexOf("RunNetworkDiagnosticAsync(", StringComparison.Ordinal);
            Assert.True(gate > 0 && probe > gate, "the fresh probe must sit behind the gate");
            Assert.Contains("PairedSession(NetworkDiagnosticPairingFor(r.Serial))", report);
            Assert.False(NetworkTestGate.OfferedWithoutAConnectedRadio);
            Assert.Equal(NetworkTestGateAnswer.NeedsAConnectedRadio, NetworkTestGate.Decide(radioConnected: false, "4711-0000-8600-0000"));

            // And the active-session reader is gone from the whole radio layer.
            foreach (string file in Directory.GetFiles(Path.Combine(IntegrationPassTree.Root, "Radios"), "*.cs", SearchOption.AllDirectories))
                Assert.DoesNotContain("LastNetworkReportFor", File.ReadAllText(file));
        }

        [Fact]
        public void The_ports_to_punch_fallback_stays_withheld()
        {
            // Not Track B's to build: the hardware gate has not run.
            Assert.False(SmartLinkMethodPolicy.HolePunchFallbackGatePassed);
            var both = new RadioPortReport(
                new AdvertisedPortPair(PortPairSource.Manual, 4994, 4993, true),
                new AdvertisedPortPair(PortPairSource.Upnp, 21000, 21001, true), false, true);
            var decision = SmartLinkMethodPolicy.Decide(new SmartLinkPolicyInput(
                SmartLinkConnectionMode.AutomaticHolePunch, true, RadioConnectionPreference.Auto, false, both));
            Assert.Equal(new[] { SmartLinkMethod.ManualPorts, SmartLinkMethod.UpnpPorts }, decision.Methods);
            Assert.True(decision.PunchWithheldUntilGate);
        }
    }
}
