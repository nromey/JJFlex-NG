using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Radios;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The arrival log the bench reads to settle the firmware question (#782,
    /// round 3, Codex finding 2): on a returning connect, each slice's and
    /// panadapter's first appearance in the vendor's lists and the ping's
    /// reply are numbered in arrival order. FlexLib lists an object BEFORE it
    /// parses the line's handle, so an arrival is logged at once without a
    /// handle and attributed under the same number at the next trigger.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ConnectFenceArrivalTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ConnectFenceArrivalTests));
        private const string Serial = "0638-0000-8600-0004";
        private const string Id = "3C3C3C3C-0000-4000-8000-0000000000BB";

        private readonly GuiClientSlot _slot = GuiClientSlot.Acquire(@"Local\JJFlexRadio.Tests.ConnectFenceArrival." + Guid.NewGuid().ToString("N") + ".");
        private readonly IDisposable _slotScope;

        private static readonly PropertyInfo Arrivals = typeof(FlexBase).GetProperty("ConnectFenceArrivals", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public ConnectFenceArrivalTests()
        {
            GuiClientIdentityRefusals.ClearForTests();
            _slotScope = GuiClientSlot.OverrideForTests(_slot);
            Assert.True(Arrivals != null, "ConnectFenceArrivals moved; every test here would be vacuous");
        }

        public void Dispose()
        {
            GuiClientIdentityRefusals.ClearForTests();
            _slotScope.Dispose();
            _slot.Dispose();
            _scope.Dispose();
        }

        private RigOnVendorRadio ReturningRig() => FenceSeam.ReturningRig(Serial, _slot.Number, Id);

        private static IReadOnlyList<string> ArrivalsOf(RigOnVendorRadio rig) => (IReadOnlyList<string>)Arrivals.GetValue(rig.Rig)!;

        [Fact]
        public void TheArrivalLog_NumbersEachObjectsFirstReportAndThePingAck_InArrivalOrder()
        {
            // The bench reads: the ACK's number N, and every "attributed: ... is
            // OURS" with a number below N; an OURS number above N that we did
            // not request is the radio restoring after it answered.
            using var rig = ReturningRig();
            int ping = FenceSeam.Ping(rig);
            FenceSeam.SliceReported(rig, 0, FenceSeam.PanA, 14.250);
            FenceSeam.PanReported(rig, FenceSeam.PanA);                // also makes slice 0 ready (before the ACK: legitimate)
            FenceSeam.Ack(rig, ping);

            var log = ArrivalsOf(rig);
            Assert.Equal(new[]
            {
                "#1 slice 0 reported (handle and pan not parsed yet at arrival; not yet ready)",
                "#1 attributed: slice 0 is OURS (handle " + FenceSeam.Handle + ", pan 0x" + FenceSeam.PanA.ToString("X") + ")",
                "#2 panadapter 0x" + FenceSeam.PanA.ToString("X") + " reported (handle not parsed yet at arrival)",
                "#2 attributed: panadapter 0x" + FenceSeam.PanA.ToString("X") + " is OURS (handle " + FenceSeam.Handle + ")",
            }, log.Take(4).ToArray());
            Assert.Equal(5, log.Count);
            Assert.StartsWith("#3 ping ACK", log[4]);
            Assert.True(FenceSeam.FenceAt(rig) > 0);

            // What the bench must NOT see, shown to be visible: own objects
            // arriving AFTER the acknowledgement get higher numbers, and another
            // client's object is attributed as not ours.
            FenceSeam.SliceReported(rig, 1, FenceSeam.PanB, 7.150);
            FenceSeam.SliceReported(rig, 2, 0x40000003, 3.573, handle: "0x5E6F7A8B");
            FenceSeam.PanReported(rig, FenceSeam.PanB);                // the next trigger attributes both
            var ordered = ArrivalsOf(rig).ToList();
            Assert.Contains("#4 slice 1 reported (handle and pan not parsed yet at arrival; not yet ready)", ordered);
            Assert.Contains("#4 attributed: slice 1 is OURS (handle " + FenceSeam.Handle + ", pan 0x" + FenceSeam.PanB.ToString("X") + ")", ordered);
            Assert.Contains("#5 attributed: slice 2 is another client's (handle 0x5E6F7A8B, pan 0x40000003)", ordered);
            Assert.Contains("#6 attributed: panadapter 0x" + FenceSeam.PanB.ToString("X") + " is OURS (handle " + FenceSeam.Handle + ")", ordered);
            Assert.True(ordered.FindIndex(l => l.StartsWith("#3 ping ACK")) < ordered.FindIndex(l => l.StartsWith("#4 ")));
        }

        [Fact]
        public void TheArrivalLog_IsEmptyOnAFreshConnect_AndRecordsARejectedPing()
        {
            using var fresh = new RigOnVendorRadio(Serial);
            fresh.Rig.WireStationHandlers(fresh.Vendor.Radio);
            Assert.Null(fresh.Rig.prepareClientIdentityForConnect("fresh"));
            FenceSeam.SendFence.Invoke(fresh.Rig, new object[] { "fresh" });   // not sent
            FenceSeam.SliceReported(fresh, 0, FenceSeam.PanA, 14.250);
            Assert.Empty(ArrivalsOf(fresh));

            using var rig = ReturningRig();
            int ping = FenceSeam.Ping(rig);
            rig.Vendor.Reply(ping, 0x50000001, "no");
            Assert.Single(ArrivalsOf(rig));
            Assert.StartsWith("#1 ping REJECTED", ArrivalsOf(rig)[0]);
            Assert.Equal(0, FenceSeam.FenceAt(rig));
        }
    }
}
