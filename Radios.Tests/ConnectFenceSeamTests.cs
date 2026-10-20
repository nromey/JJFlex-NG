using System;
using System.Reflection;
using Radios;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Shared drivers for the post-connect fence through the PRODUCTION
    /// wiring: the radio's own <c>slice</c> and <c>display pan</c> status
    /// lines parsed by the vendored Radio, the vendor's readiness chain
    /// (<c>Panadapter.CheckReady</c> → <c>Slice.CheckReady</c> →
    /// <c>SliceAdded</c>), and the rig's station handlers wired as Connect
    /// wires them. Used by <see cref="ConnectFenceSeamTests"/> and
    /// <see cref="ConnectFenceArrivalTests"/>.
    /// </summary>
    internal static class FenceSeam
    {
        public const uint PanA = 0x40000001;
        public const uint PanB = 0x40000002;

        public static readonly MethodInfo SendFence = typeof(FlexBase).GetMethod("sendConnectFence", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public static string Handle => "0x" + RigOnVendorRadio.OurHandle.ToString("X");

        /// <summary>A rig on a vendored radio, station handlers wired as Connect
        /// wires them (unless <paramref name="wire"/> is false), with a stored
        /// id under <paramref name="slot"/> so the connect is RETURNING and the
        /// fence is armed.</summary>
        public static RigOnVendorRadio ReturningRig(string serial, int slot, string id, bool wire = true)
        {
            Assert.True(SendFence != null, "sendConnectFence moved; every fence seam test would be vacuous");
            var rig = new RigOnVendorRadio(serial);
            if (wire) rig.Rig.WireStationHandlers(rig.Vendor.Radio);
            Assert.Null(GuiClientIdentityStore.Write(serial, slot, id));
            Assert.Equal(id, rig.Rig.prepareClientIdentityForConnect("returning"));
            return rig;
        }

        public static void SliceReported(RigOnVendorRadio rig, int index, uint pan, double mhz, string handle = null) =>
            rig.Vendor.Status("slice " + index + " in_use=1 client_handle=" + (handle ?? Handle) + " pan=0x" + pan.ToString("X")
                + " RF_frequency=" + mhz.ToString("f6", System.Globalization.CultureInfo.InvariantCulture) + " mode=USB index_letter=" + (char)('A' + index));

        public static void SliceRemoved(RigOnVendorRadio rig, int index) =>
            rig.Vendor.Status("slice " + index + " in_use=0");

        /// <summary>The panadapter's status with <c>waterfall=0x0</c>: the vendor
        /// takes that as "no waterfall", marks the pan ready at once, and
        /// re-checks every slice on it — which is what raises SliceAdded.</summary>
        public static void PanReported(RigOnVendorRadio rig, uint pan) =>
            rig.Vendor.Status("display pan 0x" + pan.ToString("X") + " client_handle=" + Handle + " waterfall=0x0 center=14.200000 bandwidth=0.200000");

        /// <summary>Send the fence and return the ping's own sequence number:
        /// the wired handlers send commands of their own (panadapterAdded sets
        /// the pan width), so "the last command" is not reliably the ping.</summary>
        public static int Ping(RigOnVendorRadio rig, string why = "returning")
        {
            SendFence.Invoke(rig.Rig, new object[] { why });
            Assert.Equal("ping", rig.Vendor.LastCommand);
            return rig.Vendor.LastSequence;
        }

        public static void Ack(RigOnVendorRadio rig, int pingSeq) => rig.Vendor.ReplyOk(pingSeq);

        public static long FenceAt(RigOnVendorRadio rig) => rig.Profiles.Snapshot().ConnectFenceAtSequence;
    }

    /// <summary>
    /// The readiness hold of the post-connect fence, through the production
    /// wiring (#782, round 3, Codex finding 3): the same slice object in the
    /// vendor's list and in ours, made ready by its panadapter's real status
    /// line, with the wired <c>sliceAdded</c> re-check doing the recording.
    /// Nothing here invokes the re-check by hand. The mutation control (the
    /// re-check line removed; the first two tests fail; restored, all pass)
    /// is recorded in the track report.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ConnectFenceSeamTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ConnectFenceSeamTests));
        private const string Serial = "0638-0000-8600-0003";
        private const string Id = "2B2B2B2B-0000-4000-8000-0000000000AA";

        private readonly GuiClientSlot _slot = GuiClientSlot.Acquire(@"Local\JJFlexRadio.Tests.ConnectFenceSeam." + Guid.NewGuid().ToString("N") + ".");
        private readonly IDisposable _slotScope;

        public ConnectFenceSeamTests()
        {
            GuiClientIdentityRefusals.ClearForTests();
            _slotScope = GuiClientSlot.OverrideForTests(_slot);
        }

        public void Dispose()
        {
            GuiClientIdentityRefusals.ClearForTests();
            _slotScope.Dispose();
            _slot.Dispose();
            _scope.Dispose();
        }

        private RigOnVendorRadio ReturningRig(bool wire = true) => FenceSeam.ReturningRig(Serial, _slot.Number, Id, wire);

        [Fact]
        public void AReportedSliceHoldsTheAcknowledgedFence_UntilItsPanadapterMakesItReady_ThroughTheWiredSliceAdded()
        {
            using var rig = ReturningRig();
            int ping = FenceSeam.Ping(rig);
            FenceSeam.SliceReported(rig, 0, FenceSeam.PanA, 14.250);   // in the vendor's list, not ready: its pan is unknown
            Assert.Single(rig.Vendor.Radio.SliceList);
            FenceSeam.Ack(rig, ping);
            Assert.Equal(0, FenceSeam.FenceAt(rig));                   // held: 1 reported, 0 ready

            // The pan's status makes the pan ready, the vendor re-checks the
            // slice, SliceAdded fires, the wired sliceAdded puts it in our list
            // and re-checks the fence — all inside this one status line.
            FenceSeam.PanReported(rig, FenceSeam.PanA);

            Assert.True(FenceSeam.FenceAt(rig) > 0, "the fence was not recorded when the vendor made the reported slice ready");
            Assert.Equal(1, rig.Station.Snapshot().OwnSliceCount);
        }

        [Fact]
        public void TwoReportedSlices_HoldTheFenceUntilBothAreReady()
        {
            using var rig = ReturningRig();
            int ping = FenceSeam.Ping(rig);
            FenceSeam.SliceReported(rig, 0, FenceSeam.PanA, 14.250);
            FenceSeam.SliceReported(rig, 1, FenceSeam.PanB, 7.150);
            FenceSeam.Ack(rig, ping);
            Assert.Equal(0, FenceSeam.FenceAt(rig));                   // 2 reported, 0 ready

            FenceSeam.PanReported(rig, FenceSeam.PanA);                // slice 0 ready
            Assert.Equal(1, rig.Station.Snapshot().OwnSliceCount);
            Assert.Equal(0, FenceSeam.FenceAt(rig));                   // still held: 2 reported, 1 ready

            FenceSeam.PanReported(rig, FenceSeam.PanB);                // slice 1 ready
            Assert.Equal(2, rig.Station.Snapshot().OwnSliceCount);
            Assert.True(FenceSeam.FenceAt(rig) > 0);
        }

        [Fact]
        public void AReportedSliceRemovedBeforeItIsReady_ReleasesTheFence()
        {
            using var rig = ReturningRig();
            int ping = FenceSeam.Ping(rig);
            FenceSeam.SliceReported(rig, 0, FenceSeam.PanA, 14.250);
            FenceSeam.Ack(rig, ping);
            Assert.Equal(0, FenceSeam.FenceAt(rig));

            FenceSeam.SliceRemoved(rig, 0);                            // the vendor drops it and raises SliceList

            Assert.Empty(rig.Vendor.Radio.SliceList);
            Assert.True(FenceSeam.FenceAt(rig) > 0, "a reported slice that went away before it was ready kept the fence held");
            Assert.Equal(0, rig.Station.Snapshot().OwnSliceCount);
        }

        [Fact]
        public void PositiveControl_WithoutTheWiredHandlers_ReadinessDoesNotRecordTheFence()
        {
            // Same lines, no station wiring: the vendor still makes the slice
            // ready, but nothing carries that into our list or re-checks the
            // fence, so it stays held. This is what the wired sliceAdded
            // re-check is for.
            using var rig = ReturningRig(wire: false);
            int ping = FenceSeam.Ping(rig);
            FenceSeam.SliceReported(rig, 0, FenceSeam.PanA, 14.250);
            FenceSeam.Ack(rig, ping);
            Assert.Equal(0, FenceSeam.FenceAt(rig));

            FenceSeam.PanReported(rig, FenceSeam.PanA);

            Assert.Equal(0, FenceSeam.FenceAt(rig));
            Assert.Equal(0, rig.Station.Snapshot().OwnSliceCount);
        }
    }
}
