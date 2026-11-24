using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// One SmartLink dial, through the real <c>FlexBase.DialSmartLink</c>, a
    /// real session owner and a mock WAN server: the policy's first method is
    /// carried out, the radio's own report survives, the method actually used
    /// is recorded, and every punched-session guard reads that method.
    /// </summary>
    /// <remarks>
    /// <para>Nothing reaches a network, a radio or the operator's settings:
    /// the session is handed to the dial directly (no process-wide coordinator
    /// is replaced), the per-radio profile is an object, and the stub the dial
    /// refreshes lands in this class's temporary settings tree. No test here
    /// saves SmartLink accounts: the account whose tier governs is the rig's
    /// own adopted account, set in memory (the tiers brief, settled item 3).</para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class SmartLinkDialMethodTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly RadioConfigStaticsScope _scope = new(nameof(SmartLinkDialMethodTests));
        private readonly List<IDisposable> _owned = new();
        private readonly string _account = "tiers-test-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@example.invalid";

        public void Dispose()
        {
            foreach (var d in _owned) { try { d.Dispose(); } catch { } }
            _scope.Dispose();
        }

        private static string Serial() => "4711-" + Random.Shared.Next(1000, 9999) + "-8600-" + Random.Shared.Next(1000, 9999);

        private static Radio WanRadio(string serial, AdvertisedPorts advertised)
        {
            var r = (Radio)Activator.CreateInstance(typeof(Radio), Private, null, new object[] { true }, null)!;
            typeof(Radio).GetField("_ip", Private)!.SetValue(r, IPAddress.Parse("192.0.2.1"));
            typeof(Radio).GetField("_serial", Private)!.SetValue(r, serial);
            r.AdvertisedPorts = advertised;
            // FlexLib's own single choice, as stock's parse would have made it.
            bool manual = advertised.ManualTlsPort != -1 && advertised.ManualUdpPort != -1;
            r.PublicTlsPort = manual ? advertised.ManualTlsPort : advertised.UpnpTlsPort;
            r.PublicUdpPort = manual ? advertised.ManualUdpPort : advertised.UpnpUdpPort;
            r.IsPortForwardOn = manual;
            r.RequiresHolePunch = advertised.RequiresHolePunch;
            r.NegotiatedHolePunchPort = -1;
            return r;
        }

        private (FlexBase rig, MockWanServer wan, WanSessionOwner owner) Setup(SmartLinkConnectionMode tier, bool chosen = true, int? savedListenPort = null)
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
            _owned.Add(new Releaser(rig));
            typeof(FlexBase).GetField("_currentAccount", Private)!.SetValue(rig, new SmartLinkAccount
            {
                Email = _account, FriendlyName = _account, ConnectionMode = tier, TierChosen = chosen,
                ConfiguredListenPort = savedListenPort,
            });
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

        /// <summary>The broker answers "ready" for whatever serial was asked for.</summary>
        private static Task AnswerReady(MockWanServer wan) => Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (wan.SendConnectMessageCallCount == 0 && sw.ElapsedMilliseconds < 5000) Thread.Sleep(5);
            if (wan.SendConnectMessageCallCount > 0) wan.RaiseWanRadioConnectReady("0xABCD1234", wan.LastSendConnectSerial!);
        });

        private sealed class Releaser : IDisposable
        {
            private readonly FlexBase _rig;
            public Releaser(FlexBase rig) => _rig = rig;
            public void Dispose() { _rig.theRadio = null; _rig.Dispose(); }
        }

        private static readonly AdvertisedPorts Forwarded = new(4994, 4993, false, -1, -1, false);

        [Fact]
        public void Hole_punch_always_to_a_forwarded_radio_punches_records_the_method_and_every_guard_sees_a_punched_session()
        {
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.AutomaticHolePunch, savedListenPort: 25678);
            string serial = Serial();
            var radio = WanRadio(serial, Forwarded);
            var ready = AnswerReady(wan);

            bool sent = rig.DialSmartLink(radio, owner, new RadioConfig { ConnectionPreference = RadioConnectionPreference.HolePunch });
            ready.Wait(5000);
            rig.theRadio = radio;

            Assert.True(sent);
            Assert.Equal(SmartLinkMethod.HolePunch, rig.SmartLinkMethodInUse);
            Assert.True(rig.ConnectedByHolePunch, "the guard must read the method used, not the radio's flag");
            // The radio's own report survives (it said: forwarded, no punch).
            Assert.False(rig.RadioRequiresHolePunch);
            Assert.False(radio.AdvertisedPorts!.RequiresHolePunch);
            // A punch port from the per-connect range, never the account's saved forwarded port (decision 11).
            Assert.InRange(wan.LastSendConnectHolePunchPort, 25000, 64999);
            var rec = SmartLinkConnectRecords.For(serial)!;
            Assert.Equal("random", rec.LastAttempt!.PunchPortSource);
            Assert.Equal(SmartLinkMethod.HolePunch, rec.MethodUsed);
            Assert.True(rec.ForcedMethod);
            Assert.True(rec.LastAttempt.HandleArrived);
            Assert.Equal(SmartLinkAttemptEnd.Dialled, rec.LastAttempt.End);

            // NetworkIdentityInfo describes this connection's path as a punch.
            var lines = new List<string>();
            typeof(NetworkIdentityInfo).GetMethod("BuildSmartLinkLines", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { rig, serial, lines });
            Assert.Contains(lines, l => l.StartsWith("Path: hole punch", StringComparison.Ordinal));
        }

        [Fact]
        public void A_per_radio_fixed_punch_port_wins()
        {
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.AutomaticHolePunch);
            string serial = Serial();
            var ready = AnswerReady(wan);
            Assert.True(rig.DialSmartLink(WanRadio(serial, new AdvertisedPorts(-1, -1, false, -1, -1, true)), owner,
                new RadioConfig { FixedHolePunchPort = 40420 }));
            ready.Wait(5000);
            Assert.Equal(40420, wan.LastSendConnectHolePunchPort);
            Assert.Equal("radioProfile", SmartLinkConnectRecords.For(serial)!.LastAttempt!.PunchPortSource);
        }

        [Fact]
        public void A_ports_dial_sends_no_punch_port_dials_the_UPnP_pair_and_is_not_a_punched_session()
        {
            // Tier 2, a manual pair that is not usable (a zero) beside a working UPnP pair.
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPlusUpnp);
            string serial = Serial();
            var radio = WanRadio(serial, new AdvertisedPorts(0, 0, true, 21000, 21001, false));
            var ready = AnswerReady(wan);

            Assert.True(rig.DialSmartLink(radio, owner, new RadioConfig()));
            ready.Wait(5000);
            rig.theRadio = radio;

            Assert.Equal(0, wan.LastSendConnectHolePunchPort);
            Assert.Equal(SmartLinkMethod.UpnpPorts, rig.SmartLinkMethodInUse);
            Assert.False(rig.ConnectedByHolePunch);
            Assert.Equal(21000, radio.PublicTlsPort);
            Assert.Equal(21001, radio.PublicUdpPort);
            Assert.False(radio.RequiresHolePunch);
        }

        [Fact]
        public void Tier_1_refuses_a_UPnP_only_radio_before_anything_is_sent_and_says_why()
        {
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPortForwardOnly, chosen: false);
            string serial = Serial();
            var radio = WanRadio(serial, new AdvertisedPorts(-1, -1, true, 21000, 21001, false));

            Assert.False(rig.DialSmartLink(radio, owner, new RadioConfig()));
            Assert.Equal(0, wan.SendConnectMessageCallCount);
            Assert.Null(rig.SmartLinkMethodInUse);
            Assert.Equal(ConnectFailureClass.PreflightRefused, rig.LastConnectFailureReport!.Class);
            Assert.Contains(_account, rig.LastConnectFailureAdvice);
            var rec = SmartLinkConnectRecords.For(serial)!;
            Assert.Equal(SmartLinkRefusal.TierForbidsWhatTheRadioOffers, rec.Refusal);
            Assert.Equal(SmartLinkConnectionMode.ManualPlusUpnp, rec.LowestAdmittingTier);
            Assert.Empty(rec.Attempts);
            // This account is not saved on this computer, so there is nowhere to keep an answer: no offer.
            Assert.Null(rig.PendingTierOffer);
        }

        [Fact]
        public void Hole_punch_always_under_Tier_1_is_refused_naming_the_radio_s_setting()
        {
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPortForwardOnly);
            string serial = Serial();
            Assert.False(rig.DialSmartLink(WanRadio(serial, Forwarded), owner,
                new RadioConfig { ConnectionPreference = RadioConnectionPreference.HolePunch }));
            Assert.Equal(0, wan.SendConnectMessageCallCount);
            Assert.Equal(SmartLinkRefusal.HolePunchAlwaysButTierForbidsPunch, SmartLinkConnectRecords.For(serial)!.Refusal);
            Assert.Equal(ConnectFailureClass.PreflightRefused, rig.LastConnectFailureReport!.Class);
        }

        [Fact]
        public void No_handle_ends_the_attempt_and_records_it()
        {
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.ManualPortForwardOnly);
            string serial = Serial();
            Assert.False(rig.DialSmartLink(WanRadio(serial, Forwarded), owner, new RadioConfig()));
            var rec = SmartLinkConnectRecords.For(serial)!;
            Assert.Equal(SmartLinkAttemptEnd.NoHandle, rec.LastAttempt!.End);
            Assert.False(rec.LastAttempt.HandleArrived);
            Assert.Null(rec.MethodUsed);
            Assert.Null(rig.SmartLinkMethodInUse);
        }

        // ---- The tier comes from the brokering account (#798), never the current one by habit ----

        [Fact]
        public void The_brokering_account_s_tier_governs_even_when_the_current_account_differs()
        {
            var brokering = new SmartLinkAccount { Email = "owner@example.invalid", ConnectionMode = SmartLinkConnectionMode.AutomaticHolePunch, TierChosen = true };
            var current = new SmartLinkAccount { Email = "default@example.invalid", ConnectionMode = SmartLinkConnectionMode.ManualPortForwardOnly };
            var governs = FlexBase.AccountWhoseTierGoverns("OWNER@example.invalid", new[] { current, brokering }, current);
            Assert.Same(brokering, governs);

            // The brokering account is not saved here and is not the current one:
            // nothing governs it but the default, never the current account's tier.
            var none = new SmartLinkAccount { Email = "default@example.invalid", ConnectionMode = SmartLinkConnectionMode.AutomaticHolePunch };
            Assert.Null(FlexBase.AccountWhoseTierGoverns("owner@example.invalid", Array.Empty<SmartLinkAccount>(), none));
            Assert.Same(none, FlexBase.AccountWhoseTierGoverns("default@example.invalid", Array.Empty<SmartLinkAccount>(), none));
        }

        [Fact]
        public void The_dial_reads_the_brokering_session_s_account_not_the_current_one()
        {
            // The rig's current account is Tier 3 under ANOTHER email; the session
            // brokering this connect belongs to an account nobody saved, so Tier 1
            // governs and the punch-only radio is refused.
            var (rig, wan, owner) = Setup(SmartLinkConnectionMode.AutomaticHolePunch);
            typeof(FlexBase).GetField("_currentAccount", Private)!.SetValue(rig, new SmartLinkAccount
            {
                Email = "someone-else@example.invalid", ConnectionMode = SmartLinkConnectionMode.AutomaticHolePunch, TierChosen = true,
            });
            string serial = Serial();
            Assert.False(rig.DialSmartLink(WanRadio(serial, new AdvertisedPorts(-1, -1, false, -1, -1, true)), owner, new RadioConfig()));
            var rec = SmartLinkConnectRecords.For(serial)!;
            Assert.Equal(_account, rec.BrokeringAccount);
            Assert.Equal(SmartLinkConnectionMode.ManualPortForwardOnly, rec.Tier);
            Assert.Equal(SmartLinkRefusal.TierForbidsWhatTheRadioOffers, rec.Refusal);
        }

        // ---- The guards outside Radios, pinned in their source ----

        private static string Src(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()));

        [Fact]
        public void Every_punched_session_guard_reads_the_method_actually_used()
        {
            string flexBase = Src("Radios", "FlexBase.cs");
            int kick = flexBase.IndexOf("KickPostConnectNetworkTest: skipped", StringComparison.Ordinal);
            Assert.True(kick > 0);
            string guard = flexBase.Substring(Math.Max(0, kick - 400), 400);
            Assert.Contains("if (ConnectedByHolePunch)", guard);

            Assert.Contains("!_rig.ConnectedByHolePunch)", Src("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"));
            Assert.Contains("if (_rig.ConnectedByHolePunch)", Src("JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs"));
            Assert.Contains("if (rig.ConnectedByHolePunch)", Src("Radios", "NetworkIdentityInfo.cs"));

            // ...and nothing outside the property itself asks the radio's report that question.
            foreach (var file in new[] { Src("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"), Src("JJFlexWpf", "Dialogs", "SettingsDialog.RadioSetup.cs"), Src("Radios", "NetworkIdentityInfo.cs") })
                Assert.DoesNotContain("RadioRequiresHolePunch", file);
        }

        [Fact]
        public void The_account_s_saved_listen_port_is_never_a_punch_port_or_advertised()
        {
            string wan = Src("Radios", "FlexBase.Wan.cs");
            int start = wan.IndexOf("internal bool DialSmartLink(", StringComparison.Ordinal);
            int end = wan.IndexOf("#endregion", start, StringComparison.Ordinal);
            string dial = wan.Substring(start, end - start);
            Assert.DoesNotContain("ConfiguredListenPort", dial);
            Assert.DoesNotContain("_currentAccount.ConnectionMode", dial);
        }
    }
}
