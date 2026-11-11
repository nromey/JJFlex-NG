#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #398, repair one (Codex B1): a SmartLink list's advertised ports reach
    /// the radio picker's identity card on every path a list speaks for the
    /// radio, through the real intake. The path that lost them: local
    /// discovery finds the radio first, the SmartLink list then carries the
    /// same serial, and the intake folds the list into the LAN object and
    /// raises the sighting from it, so the sighting's radio is not a WAN one.
    /// The WAN bank's sighting, which the picker's reconcile hands a row, lost
    /// them too. Either way the card said SmartLink does not list a radio
    /// whose row said it was available on SmartLink.
    /// </summary>
    /// <remarks>
    /// <para>Nothing here reaches a network, a radio or a settings tree: a
    /// mock WAN server behind a real session owner and a real coordinator,
    /// the account key the connect flow's own fallback (no '@', so the
    /// intake's fast-paint cache write is skipped), as
    /// <see cref="SmartLinkListProvenanceTests"/> does.</para>
    /// <para>The ports are distinct (21443, 21444), so a swap or a default
    /// cannot pass.</para>
    /// </remarks>
    [Collection(SmartLinkSingletonCollection.Name)]
    public sealed class PickerAdvertisedPortsIntakeTests : IDisposable
    {
        private const string Account = "default-account";
        private const string Listed = "1234-5678-6400-0398";
        private const int Tls = 21443;
        private const int Udp = 21444;

        private readonly SmartLinkSessionCoordinator _original;
        private readonly Func<IReadOnlyList<SmartLinkAccount>>? _originalAccountsHook;
        private readonly Func<SmartLinkAccount, bool, string?>? _originalJwtHook;
        private readonly List<FlexBase> _rigs = new();
        private readonly List<SmartLinkSessionCoordinator> _coordinators = new();

        private static readonly FieldInfo? MyRadioListField =
            typeof(FlexBase).GetField("myRadioList", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo? ForgetWanRadiosForAccount =
            typeof(FlexBase).GetMethod("ForgetWanRadiosForAccount", BindingFlags.NonPublic | BindingFlags.Static);

        public PickerAdvertisedPortsIntakeTests()
        {
            _original = SmartLinkServices.Coordinator;
            _originalAccountsHook = SmartLinkPresenceService.AccountsHook;
            _originalJwtHook = SmartLinkPresenceService.SilentJwtHook;
            SmartLinkPresenceService.AccountsHook = () => Array.Empty<SmartLinkAccount>();
            SmartLinkPresenceService.SilentJwtHook = (_, __) => null;
        }

        public void Dispose()
        {
            foreach (var rig in _rigs)
            {
                try { rig.Dispose(); } catch { /* a test may have disposed it already */ }
            }
            SmartLinkServices.Override(_original);
            SmartLinkPresenceService.AccountsHook = _originalAccountsHook;
            SmartLinkPresenceService.SilentJwtHook = _originalJwtHook;
            foreach (var c in _coordinators)
            {
                try { c.Dispose(); } catch { /* nothing under test depends on the teardown */ }
            }
            ForgetWanRadiosForAccount?.Invoke(null, new object[] { Account, "test teardown" });
        }

        /// <summary>
        /// Local discovery found the radio first; the SmartLink list then
        /// carries it. The intake's merge sighting and the bank's sighting
        /// both carry the list's numbers, which is what the card reads.
        /// </summary>
        [Fact]
        public void A_radio_local_discovery_found_first_carries_the_lists_ports_on_its_SmartLink_sighting_and_in_the_bank()
        {
            var (owner, wan) = NewSession();
            using var sightings = new Sightings();
            try
            {
                var rig = NewRig();
                rig.EngageSmartLinkPresence();
                var local = SmartLinkRegistrationReplayTests.WanRadio(Listed);
                SetIsWan(local, false);
                RadioListOf(rig).Add(local);

                owner.Connect();
                Assert.True(SpinWait.SpinUntil(() => owner.IsConnected, 5000), "the mock session never reported connected");
                wan.RaiseWanRadioRadioListReceived(new[] { ListedWithPorts() });

                // The trap, stated: the merge branch ran. The list was folded
                // into the radio local discovery found, which is not a WAN one.
                var held = RadioListOf(rig).Where(r => r.Serial == Listed).ToList();
                Assert.Single(held);
                Assert.Same(local, held[0]);
                Assert.False(held[0].IsWan);

                var seen = sightings.Of(Listed);
                Assert.True(seen.FromWanList != null, "the merge sighting carried no list, so this is not the path under test");
                Assert.True(seen.WanAvailable);
                AssertTheListsNumbers(seen.Advertised, "the intake's merge sighting");

                var (_, wanLeg) = rig.RadioAvailability(Listed, out var bank);
                Assert.True(wanLeg);
                Assert.NotNull(bank);
                AssertTheListsNumbers(bank.Advertised, "the WAN bank's sighting");

                // The card's own words for these numbers are pinned in
                // PickerIdentityLinesTests, which owns the language store
                // (this class is in the SmartLink collection, not the
                // settings one). Here: the numbers reach both sightings, with
                // the row's SmartLink flag set, which is all the card reads.
                Assert.True(bank.WanAvailable);
            }
            finally
            {
                owner.Dispose();
            }
        }

        /// <summary>
        /// Control: the path that always worked, a radio only SmartLink lists,
        /// raised from the list's own object. And a local sighting of a radio
        /// no list describes carries no numbers, so the repair did not make
        /// local discovery claim SmartLink facts.
        /// </summary>
        [Fact]
        public void A_SmartLink_only_radio_carries_its_ports_and_a_local_sighting_carries_none()
        {
            var (owner, wan) = NewSession();
            using var sightings = new Sightings();
            try
            {
                var rig = NewRig();
                rig.EngageSmartLinkPresence();
                owner.Connect();
                Assert.True(SpinWait.SpinUntil(() => owner.IsConnected, 5000), "the mock session never reported connected");
                wan.RaiseWanRadioRadioListReceived(new[] { ListedWithPorts() });

                Assert.True(RadioListOf(rig).Single(r => r.Serial == Listed).IsWan);
                AssertTheListsNumbers(sightings.Of(Listed).Advertised, "a SmartLink-only radio's sighting");
            }
            finally
            {
                owner.Dispose();
            }

            var build = typeof(FlexBase).GetMethod("BuildRigData", BindingFlags.NonPublic | BindingFlags.Static)!;
            var lan = SmartLinkRegistrationReplayTests.WanRadio("1111-2222-6400-0398");
            SetIsWan(lan, false);
            lan.PublicTlsPort = Tls;
            lan.PublicUdpPort = Udp;
            var localSighting = (FlexBase.RigData)build.Invoke(null, new object?[] { lan, null, null })!;
            Assert.Null(localSighting.Advertised);
        }

        // ------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------

        private static Radio ListedWithPorts()
        {
            var r = SmartLinkRegistrationReplayTests.WanRadio(Listed);
            r.PublicTlsPort = Tls;
            r.PublicUdpPort = Udp;
            r.IsPortForwardOn = true;
            r.RequiresHolePunch = false;
            return r;
        }

        private static void AssertTheListsNumbers(SmartLinkAdvertised? advertised, string where)
        {
            Assert.True(advertised != null,
                where + " carried no advertised ports, so the picker's card would say SmartLink does not list a radio it lists (#398, Codex B1).");
            Assert.Equal(Tls, advertised!.TlsPort);
            Assert.Equal(Udp, advertised.UdpPort);
            Assert.True(advertised.PortForwardOn);
            Assert.False(advertised.RequiresHolePunch);
        }

        private static void SetIsWan(Radio r, bool value)
            => typeof(Radio).GetProperty(nameof(Radio.IsWan))!.GetSetMethod(nonPublic: true)!
                .Invoke(r, new object[] { value });

        private static List<Radio> RadioListOf(FlexBase rig)
        {
            Assert.True(MyRadioListField != null, "FlexBase.myRadioList is not where this test reads it.");
            return (List<Radio>)MyRadioListField!.GetValue(rig)!;
        }

        private (WanSessionOwner owner, MockWanServer wan) NewSession()
        {
            MockWanServer? wan = null;
            var coordinator = new SmartLinkSessionCoordinator(accountId =>
            {
                wan = new MockWanServer();
                return new WanSessionOwner(
                    sessionId: Guid.NewGuid().ToString("N").Substring(0, 12),
                    accountId: accountId,
                    wanServer: wan,
                    audioSink: new DirectPassthroughSink(),
                    backoffScheduleMs: new[] { 50, 50, 50 });
            });
            _coordinators.Add(coordinator);
            SmartLinkServices.Override(coordinator);
            var owner = (WanSessionOwner)coordinator.GetOrCreateSession(Account);
            Assert.NotNull(wan);
            return (owner, wan!);
        }

        private FlexBase NewRig()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            _rigs.Add(rig);
            return rig;
        }

        /// <summary>Every RadioFound raised while this is alive.</summary>
        private sealed class Sightings : IDisposable
        {
            private readonly List<FlexBase.RigData> _seen = new();
            private readonly FlexBase.RadioFoundDel _onFound;

            public Sightings()
            {
                _onFound = (_, r) => { lock (_seen) _seen.Add(r); };
                FlexBase.RadioFound += _onFound;
            }

            public FlexBase.RigData Of(string serial)
            {
                lock (_seen)
                {
                    var matching = _seen.Where(r => r.Serial == serial).ToList();
                    Assert.True(matching.Count == 1,
                        $"expected exactly one RadioFound for {serial}, saw {matching.Count}");
                    return matching[0];
                }
            }

            public void Dispose() => FlexBase.RadioFound -= _onFound;
        }
    }
}
