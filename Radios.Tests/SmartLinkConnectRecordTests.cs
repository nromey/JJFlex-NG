using System;
using System.Linq;
using System.Reflection;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The attempt and outcome record that Track B files and Track C reads,
    /// frozen by Track A. These tests pin its shape as well as its behaviour:
    /// a field added, removed or renamed fails here first, so the change is a
    /// conversation rather than a surprise in another track.
    /// </summary>
    public sealed class SmartLinkConnectRecordTests
    {
        private static readonly RadioPortReport Both = new(
            new AdvertisedPortPair(PortPairSource.Manual, 4994, 4993, true),
            new AdvertisedPortPair(PortPairSource.Upnp, 21000, 21001, true), false, true);

        private static SmartLinkConnectRecord Begin(string serial)
        {
            var input = new SmartLinkPolicyInput(SmartLinkConnectionMode.ManualPlusUpnp, true, RadioConnectionPreference.Auto, true, Both);
            return SmartLinkConnectRecord.Begin(serial, "owner@example.com", input, SmartLinkMethodPolicy.Decide(input), new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc));
        }

        [Fact]
        public void Begin_copies_the_policy_s_input_and_answer()
        {
            var rec = Begin("A-1");
            Assert.Equal("A-1", rec.Serial);
            Assert.Equal("owner@example.com", rec.BrokeringAccount);
            Assert.Equal(SmartLinkConnectionMode.ManualPlusUpnp, rec.Tier);
            Assert.True(rec.TierChosen);
            Assert.True(rec.ForcedPath);
            Assert.False(rec.ForcedMethod);
            Assert.Equal(new[] { SmartLinkMethod.ManualPorts, SmartLinkMethod.UpnpPorts }, rec.MethodOrder);
            Assert.False(rec.IsRefused);
            Assert.Empty(rec.Attempts);
            Assert.Null(rec.MethodUsed);
        }

        [Fact]
        public void Attempts_append_and_the_newest_can_be_finished_without_touching_the_original()
        {
            var rec = Begin("A-2");
            var one = rec.WithAttempt(SmartLinkAttemptRecord.ForPorts(SmartLinkMethod.ManualPorts, Both.Manual));
            var two = one.WithLastAttempt(a => a with { HandleArrived = true, OpenSucceeded = false, End = SmartLinkAttemptEnd.ConnectFailed, EndDetail = "TCP refused" })
                         .WithAttempt(SmartLinkAttemptRecord.ForPorts(SmartLinkMethod.UpnpPorts, Both.Upnp))
                         .WithLastAttempt(a => a with { HandleArrived = true, OpenSucceeded = true, QualifiedUdpAfter = TimeSpan.FromMilliseconds(118), End = SmartLinkAttemptEnd.Proven })
                         .WithMethodUsed(SmartLinkMethod.UpnpPorts);

            Assert.Empty(rec.Attempts);
            Assert.Equal(SmartLinkAttemptEnd.Pending, one.LastAttempt!.End);
            Assert.Equal(2, two.Attempts.Count);
            Assert.Equal(4994, two.Attempts[0].TlsPortSent);
            Assert.Equal(0, two.Attempts[0].PunchPortSent);
            Assert.Equal(SmartLinkAttemptEnd.ConnectFailed, two.Attempts[0].End);
            Assert.Equal(21000, two.Attempts[1].TlsPortSent);
            Assert.Equal(SmartLinkMethod.UpnpPorts, two.MethodUsed);
            string line = two.Describe();
            Assert.Contains("tier=2 chosen", line);
            Assert.Contains("order=ManualPorts,UpnpPorts", line);
            Assert.Contains("qualifiedUdp=118ms end=Proven", line);
            Assert.Contains("used=UpnpPorts", line);
        }

        [Fact]
        public void A_punch_attempt_sends_no_ports_and_names_its_port_source()
        {
            var a = SmartLinkAttemptRecord.ForPunch(40420, "random");
            Assert.Equal(SmartLinkMethod.HolePunch, a.Method);
            Assert.Equal(-1, a.TlsPortSent);
            Assert.Equal(40420, a.PunchPortSent);
            Assert.Contains("punchPort=40420(random)", a.Describe());
        }

        [Fact]
        public void A_refusal_is_recorded_with_its_reason_and_lowest_admitting_tier()
        {
            var report = new RadioPortReport(new AdvertisedPortPair(PortPairSource.Manual, -1, -1, true),
                new AdvertisedPortPair(PortPairSource.Upnp, 21000, 21001, true), false, true);
            var input = new SmartLinkPolicyInput(SmartLinkConnectionMode.ManualPortForwardOnly, false, RadioConnectionPreference.Auto, false, report);
            var rec = SmartLinkConnectRecord.Begin("A-3", "owner@example.com", input, SmartLinkMethodPolicy.Decide(input), DateTime.UtcNow);
            Assert.True(rec.IsRefused);
            Assert.Equal(SmartLinkRefusal.TierForbidsWhatTheRadioOffers, rec.Refusal);
            Assert.Equal(SmartLinkConnectionMode.ManualPlusUpnp, rec.LowestAdmittingTier);
            Assert.Contains("refused=TierForbidsWhatTheRadioOffers lowestAdmittingTier=2", rec.Describe());
        }

        [Fact]
        public void The_store_keeps_the_newest_record_per_radio()
        {
            string serial = "store-" + Guid.NewGuid().ToString("N");
            Assert.Null(SmartLinkConnectRecords.For(serial));
            var first = Begin(serial);
            SmartLinkConnectRecords.Store(first);
            var second = first.WithMethodUsed(SmartLinkMethod.ManualPorts);
            SmartLinkConnectRecords.Store(second);
            Assert.Same(second, SmartLinkConnectRecords.For(serial.ToUpperInvariant()));
        }

        /// <summary>The frozen shape. Changing it is an agreement between tracks, not an edit.</summary>
        [Fact]
        public void The_frozen_shape_is_unchanged()
        {
            string Shape(Type t) => string.Join(",", t.GetConstructors().Single(c => c.GetParameters().Length > 2)
                .GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));

            Assert.Equal(
                "String Serial,String BrokeringAccount,SmartLinkConnectionMode Tier,Boolean TierChosen,RadioConnectionPreference PerRadioChoice," +
                "Boolean ForcedPath,Boolean ForcedMethod,RadioPortReport Report,IReadOnlyList`1 MethodOrder,SmartLinkRefusal Refusal," +
                "Nullable`1 LowestAdmittingTier,Boolean PunchWithheldUntilGate,IReadOnlyList`1 Attempts,Nullable`1 MethodUsed,DateTime StartedUtc",
                Shape(typeof(SmartLinkConnectRecord)));
            Assert.Equal(
                "SmartLinkMethod Method,Int32 TlsPortSent,Int32 UdpPortSent,Int32 PunchPortSent,String PunchPortSource,Boolean HandleArrived," +
                "Nullable`1 OpenSucceeded,Nullable`1 QualifiedUdpAfter,SmartLinkAttemptEnd End,String EndDetail",
                Shape(typeof(SmartLinkAttemptRecord)));
            Assert.Equal(
                "Pending,Proven,Dialled,NoHandle,NullHandle,ConnectFailed,OpenFailed,CommandConnectionLost,NoQualifiedUdp,Cancelled,NotSent",
                string.Join(",", Enum.GetNames(typeof(SmartLinkAttemptEnd))));
            Assert.Equal("ManualPorts,UpnpPorts,HolePunch", string.Join(",", Enum.GetNames(typeof(SmartLinkMethod))));
            Assert.Equal("None,HolePunchAlwaysButTierForbidsPunch,TierForbidsWhatTheRadioOffers,ForwardedOnlyButNoUsablePortPair",
                string.Join(",", Enum.GetNames(typeof(SmartLinkRefusal))));
        }
    }
}
