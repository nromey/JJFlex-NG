#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Task #402 — the station-name wait timed out and locked the application,
    /// and the field traces of 2026-08-29 put three separate app-side defects
    /// under it. These tests pin the three fixes that a build can prove; the
    /// radio-side half (why a quick reconnect's <c>client station</c> is never
    /// applied) needs a real connect and is pinned by trace instrumentation
    /// instead.
    ///
    /// <para><b>Defect 1 — the fabricated ghost.</b> The SmartLink server's
    /// list snapshot can carry OUR OWN just-disconnected client (station set,
    /// client_id empty, because discovery data carries no identity). The
    /// dedupe in <c>guiClientAdded</c> read that ghost as a foreign station
    /// holding our name and renamed us — <c>station now will be k5ner1</c> —
    /// after which the radio-side rename never completed and the wait ran its
    /// full 45-second budget, three times.</para>
    ///
    /// <para><b>Defect 2 — the rig-less gap fossilised the ghost.</b> The
    /// push that follows a disconnect — the one that would remove our dead
    /// session from the server's data — arrived while no rig held the
    /// presence intake and was dropped entirely, so the static WAN bank kept
    /// serving the stale snapshot to the next connect.</para>
    ///
    /// <para><b>Defect 3 (#401) — the words and the wire diverged.</b>
    /// <c>sendRemoteConnect</c> routed through the radio's OWNING account's
    /// held session while the spoken sentence named the account in play. One
    /// resolver now answers both.</para>
    ///
    /// <para>All offline: mock WAN servers behind real owners behind a real
    /// coordinator, and reflection-built <see cref="Radio"/> objects that
    /// never touch a network.</para>
    /// </summary>
    [Collection(SmartLinkSingletonCollection.Name)]
    public sealed class StationNameRegressionTests : IDisposable
    {
        private readonly SmartLinkSessionCoordinator _original;
        private readonly Func<IReadOnlyList<SmartLinkAccount>>? _originalAccountsHook;
        private readonly Func<SmartLinkAccount, bool, string?>? _originalJwtHook;
        private readonly List<FlexBase> _rigs = new();
        private readonly List<SmartLinkSessionCoordinator> _coordinators = new();

        public StationNameRegressionTests()
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
        }

        // ------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------

        private (SmartLinkSessionCoordinator coordinator, MockWanServer wan) NewCoordinator(string account)
        {
            MockWanServer? wan = null;
            var coordinator = new SmartLinkSessionCoordinator(accountId =>
            {
                wan = new MockWanServer();
                return new WanSessionOwner(
                    sessionId: Guid.NewGuid().ToString("N").Substring(0, 12),
                    accountId: accountId,
                    wanServer: wan,
                    audioSink: new DirectPassthroughSink());
            });
            _coordinators.Add(coordinator);
            SmartLinkServices.Override(coordinator);
            coordinator.GetOrCreateSession(account);
            Assert.NotNull(wan);
            return (coordinator, wan!);
        }

        private FlexBase NewRig(string stationName = "K5TEST")
        {
            var rig = new FlexBase(new FlexBase.OpenParms
            {
                ProgramName = "JJFlexTests",
                StationName = stationName
            });
            _rigs.Add(rig);
            return rig;
        }

        /// <summary>
        /// A WAN <see cref="Radio"/> with a serial, built the way the server's
        /// list parse builds them — no network, internal ctor via reflection.
        /// The assertion makes a moved ctor fail loudly instead of vacuously.
        /// </summary>
        private static Radio NewWanRadio(string serial)
        {
            var radio = (Radio?)Activator.CreateInstance(
                typeof(Radio),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { true },   // internal Radio(bool isWan)
                culture: null);
            Assert.True(radio != null,
                "Radio's internal ctor is not where this harness reaches it — every test using " +
                "NewWanRadio would be vacuous. Find its new shape before trusting a green run.");
            var setSerial = typeof(Radio).GetProperty(nameof(Radio.Serial))!.GetSetMethod(nonPublic: true);
            Assert.True(setSerial != null, "Radio.Serial's internal setter moved; this harness needs it.");
            setSerial!.Invoke(radio, new object[] { serial });
            return radio!;
        }

        private static void WaitUntil(Func<bool> condition, string complaint)
        {
            for (int i = 0; i < 200; i++)
            {
                if (condition()) return;
                System.Threading.Thread.Sleep(10);
            }
            Assert.Fail(complaint);
        }

        private static string UniqueSerial() =>
            "9999-" + Guid.NewGuid().ToString("N").Substring(0, 4) + "-TEST-" + Guid.NewGuid().ToString("N").Substring(0, 4);

        // ------------------------------------------------------------------
        // Defect 2 — a push with no intake still refreshes the WAN bank
        // ------------------------------------------------------------------

        /// <summary>
        /// The gap between a teardown and the next picker must not fossilise
        /// the server's snapshot. Before the fix, this push was dropped whole,
        /// and the bank kept handing the NEXT connect a radio object with our
        /// own dead session still in its client list.
        /// </summary>
        [Fact]
        public void APushWithNoIntakeStillRefreshesTheWanBank()
        {
            const string account = "bank-refresh@example.test";
            var (_, wan) = NewCoordinator(account);

            // Wire the static dispatcher by engaging once, then dispose so no
            // intake exists — the exact post-disconnect state from the traces.
            var rig = NewRig();
            rig.EngageSmartLinkPresence();
            rig.Dispose();
            Assert.Null(FlexBase.PresenceIntake);

            var serial = UniqueSerial();

            // Positive control: the bank genuinely does not know this serial
            // yet, so the assertion below measures the push and not a leftover.
            Assert.Equal("", FlexBase.WanAccountForSerial(serial));

            wan.RaiseWanRadioRadioListReceived(new[] { NewWanRadio(serial) });

            Assert.Equal(account, FlexBase.WanAccountForSerial(serial));
        }

        // ------------------------------------------------------------------
        // Defect 3 (#401) — one resolver for "which account will broker this"
        // ------------------------------------------------------------------

        [Fact]
        public void TheBrokerResolverNamesTheOwningAccountWhenItsSessionIsLive()
        {
            const string owner = "owner-live@example.test";
            var (_, wan) = NewCoordinator(owner);

            var rig = NewRig();
            rig.EngageSmartLinkPresence();
            rig.Dispose();

            var serial = UniqueSerial();
            wan.RaiseWanRadioRadioListReceived(new[] { NewWanRadio(serial) });
            Assert.Equal(owner, FlexBase.WanAccountForSerial(serial));

            // The owning account's session is up: the connect will route
            // through it, so the sentence must name it — not the account in
            // play. This divergence is the whole of #401. IsConnected is the
            // OWNER's state machine, so bring it up the way production does —
            // through Connect() — rather than poking the mock's flag.
            var session = SmartLinkServices.Coordinator.GetSessionForAccount(owner);
            Assert.NotNull(session);
            session!.Connect();
            WaitUntil(() => session.IsConnected,
                "the owning session never reached Connected, so this test is not measuring the resolver");

            Assert.Equal(owner, FlexBase.AccountThatWillBroker(serial, "in-play@example.test"));
        }

        [Fact]
        public void TheBrokerResolverFallsBackToTheAccountInPlayWhenTheOwningSessionIsDown()
        {
            const string owner = "owner-down@example.test";
            var (_, wan) = NewCoordinator(owner);

            var rig = NewRig();
            rig.EngageSmartLinkPresence();
            rig.Dispose();

            var serial = UniqueSerial();
            wan.RaiseWanRadioRadioListReceived(new[] { NewWanRadio(serial) });

            // Session exists but was never connected: sendRemoteConnect would
            // fall through to the active session, so the words must too.
            var session = SmartLinkServices.Coordinator.GetSessionForAccount(owner);
            Assert.NotNull(session);
            Assert.False(session!.IsConnected,
                "the never-connected session reports Connected, so the fallback below would be untested");
            Assert.Equal("in-play@example.test",
                FlexBase.AccountThatWillBroker(serial, "in-play@example.test"));
        }

        [Fact]
        public void TheBrokerResolverFallsBackForASerialNoSessionHasListed()
        {
            NewCoordinator("nobody-listed@example.test");
            Assert.Equal("in-play@example.test",
                FlexBase.AccountThatWillBroker(UniqueSerial(), "in-play@example.test"));
        }

        // ------------------------------------------------------------------
        // Defect 1 — the dedupe yields only to records that can prove a duplicate
        // ------------------------------------------------------------------

        private static readonly FieldInfo TheRadioField =
            typeof(FlexBase).GetField("theRadio", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo ClientHandleField =
            typeof(FlexBase).GetField("clientHandle", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo GuiClientAddedMethod =
            typeof(FlexBase).GetMethod("guiClientAdded", BindingFlags.NonPublic | BindingFlags.Instance)!;

        /// <summary>
        /// Drive our own client's add through the real handler with a chosen
        /// pre-existing client list on the radio object — exactly the state
        /// the fabricated ghost created in the field.
        /// </summary>
        private FlexBase RunAddWithExistingClient(GUIClient existing, out Radio radio)
        {
            Assert.True(TheRadioField != null && ClientHandleField != null && GuiClientAddedMethod != null,
                "FlexBase's theRadio/clientHandle/guiClientAdded are not where this test reaches them; " +
                "every dedupe assertion below would be vacuous.");

            var rig = NewRig("K5TEST");
            radio = NewWanRadio(UniqueSerial());
            TheRadioField!.SetValue(rig, radio);

            // Our own client, recognised by HANDLE (the symptom-5 rule): no
            // reliance on FlexLib's internal IsThisClient stamp.
            const uint myHandle = 42;
            ClientHandleField!.SetValue(rig, myHandle);
            var mine = new GUIClient(myHandle, "MY-CLIENT-ID", "JJ", "", is_local_ptt: true);

            lock (radio.GuiClientsLockObj)
            {
                radio.GuiClients.Add(existing);
                radio.GuiClients.Add(mine);
            }

            GuiClientAddedMethod!.Invoke(rig, new object[] { mine });

            // Unplant the fake radio before teardown: it was never FlexBase's
            // to disconnect, and a Dispose that throws over it leaves the
            // half-disposed object to the finalizer, which reruns the whole
            // sequence on the finalizer thread and aborts the test run —
            // #392's exact trap, demonstrated here on the first run.
            TheRadioField!.SetValue(rig, null);
            return rig;
        }

        /// <summary>
        /// The regression itself: a record with NO client_id — discovery/list
        /// data, which cannot carry identity — holding our station name must
        /// not push us off it. In the field that record WAS us, one connect
        /// ago, and yielding to it renamed the operator to "k5ner1" for a
        /// collision that did not exist.
        /// </summary>
        [Fact]
        public void AFabricatedRecordHoldingOurStationNameDoesNotRenameUs()
        {
            var ghost = new GUIClient(7, client_id: null!, "JJ", "K5TEST", is_local_ptt: false);
            var rig = RunAddWithExistingClient(ghost, out _);

            Assert.Equal("K5TEST", rig.Callouts.StationName);
        }

        /// <summary>
        /// The dedupe's legitimate core survives: a record WITH identity — the
        /// radio's own TCP status — holding our name is a real MultiFlex
        /// duplicate, and yielding is correct.
        /// </summary>
        [Fact]
        public void AnIdentityBearingClientHoldingOurStationNameStillRenamesUs()
        {
            var real = new GUIClient(7, "SOMEONE-ELSES-ID", "SmartSDR-Win", "K5TEST", is_local_ptt: false);
            var rig = RunAddWithExistingClient(real, out _);

            Assert.Equal("K5TEST1", rig.Callouts.StationName);
        }
    }
}
