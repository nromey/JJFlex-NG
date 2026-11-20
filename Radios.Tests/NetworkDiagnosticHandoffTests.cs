using System;
using System.Collections.Generic;
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
    /// The network diagnostic on the schedule a real connect reaches (Codex's
    /// check of the tiers work's Track C, B1): SmartLink's handle arrives, the
    /// record is filed by the real <c>FlexBase.DialSmartLink</c>, and the open
    /// that follows fails. Nothing after the handle updates the record today
    /// (<c>FlexBase.Connect</c> files nothing when <c>Radio.Connect</c>
    /// returns false), so the record the diagnostic reads after a failed open
    /// is exactly the one filed when the handle arrived. The diagnostic must
    /// then claim neither an opened radio nor a connection in use.
    /// </summary>
    /// <remarks>
    /// Nothing reaches a network, a radio or the operator's settings: the
    /// session is a <see cref="MockWanServer"/> handed to the dial directly, the
    /// radio is an object, its stub lands in this class's temporary settings
    /// tree, and no open is attempted (attempting one would dial an address).
    /// No test here saves SmartLink accounts.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class NetworkDiagnosticHandoffTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly RadioConfigStaticsScope _scope = new(nameof(NetworkDiagnosticHandoffTests));
        private readonly List<IDisposable> _owned = new();
        private readonly string _account = "tiers-c-test-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@example.invalid";

        public void Dispose()
        {
            foreach (var d in _owned) { try { d.Dispose(); } catch { } }
            _scope.Dispose();
        }

        private static string Serial() => "4711-" + Random.Shared.Next(1000, 9999) + "-8600-" + Random.Shared.Next(1000, 9999);

        private static string D(string key, params (string, object)[] args) =>
            Lexicon.Get("settings.network.diag." + key, args);

        private static Radio WanRadio(string serial, AdvertisedPorts advertised)
        {
            var r = (Radio)Activator.CreateInstance(typeof(Radio), Private, null, new object[] { true }, null)!;
            typeof(Radio).GetField("_ip", Private)!.SetValue(r, IPAddress.Parse("192.0.2.1"));
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
            typeof(FlexBase).GetField("_currentAccount", Private)!.SetValue(rig, new SmartLinkAccount
            {
                Email = _account, FriendlyName = _account, ConnectionMode = tier, TierChosen = true,
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

        public static IEnumerable<object[]> Dials() => new[]
        {
            // A forwarded radio under Tier 1: the forwarded ports.
            new object[] { SmartLinkConnectionMode.ManualPortForwardOnly, RadioConnectionPreference.Auto,
                new AdvertisedPorts(4994, 4993, false, -1, -1, false), SmartLinkMethod.ManualPorts },
            // Hole punch always under Tier 3: a punch.
            new object[] { SmartLinkConnectionMode.AutomaticHolePunch, RadioConnectionPreference.HolePunch,
                new AdvertisedPorts(4994, 4993, false, -1, -1, false), SmartLinkMethod.HolePunch },
        };

        [Theory]
        [MemberData(nameof(Dials))]
        public void A_handle_followed_by_a_failed_open_is_never_described_as_opened_or_in_use(
            SmartLinkConnectionMode tier, RadioConnectionPreference choice, AdvertisedPorts advertised, SmartLinkMethod expected)
        {
            var (rig, wan, owner) = Setup(tier);
            string serial = Serial();
            var radio = WanRadio(serial, advertised);
            var ready = AnswerReady(wan);

            Assert.True(rig.DialSmartLink(radio, owner, new RadioConfig { ConnectionPreference = choice }));
            ready.Wait(5000);
            // The open fails here: no radio is opened, and nothing is filed.

            // The record as the producer leaves it: a handoff, its open unknown.
            var rec = SmartLinkConnectRecords.For(serial)!;
            Assert.Equal(expected, rec.MethodUsed);
            Assert.True(rec.LastAttempt!.HandleArrived);
            Assert.Null(rec.LastAttempt.OpenSucceeded);
            Assert.Equal(SmartLinkAttemptEnd.Dialled, rec.LastAttempt.End);

            // What the diagnostic says, through the same call the Network tab,
            // Radio Setup step 6 and the export make.
            var diag = rig.NetworkDiagnosticFor(serial);
            var outcome = diag.Outcome.Lines;
            string method = NetworkDiagnosticComposer.MethodName(expected);

            Assert.Equal(D("outcome_handed_unconfirmed", ("method", method)), outcome[^1]);
            Assert.Contains(D("end_dialled"), outcome[0]);

            // No opened or live claim, whatever the wording becomes.
            foreach (SmartLinkMethod m in Enum.GetValues(typeof(SmartLinkMethod)))
                Assert.DoesNotContain(D("outcome_used", ("method", NetworkDiagnosticComposer.MethodName(m))), outcome);
            Assert.DoesNotContain(outcome, l => l.Contains(D("end_dialled_opened"), StringComparison.Ordinal));
            Assert.DoesNotContain(outcome, l => l.Contains(D("end_proven"), StringComparison.Ordinal));
            foreach (string text in new[] { diag.ToText(), diag.ToMarkdown(serial, DateTime.UtcNow) })
            {
                Assert.DoesNotContain("was opened", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("in use", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
