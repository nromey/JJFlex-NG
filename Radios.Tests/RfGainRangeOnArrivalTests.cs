using System;
using System.Linq;
using Radios;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1291: the RF gain range is asked for when one of OUR panadapters first
    /// becomes known, through the production wiring (the radio's own
    /// <c>display pan</c> status, parsed by the vendored Radio and raised
    /// through PanadapterAdded into <c>panadapterAdded</c>). Before this the
    /// only request sat in the ActiveSlice handler, which skips it when it
    /// refuses a slice: measured on the 8600, no range was ever asked and RF
    /// Gain Up stepped by the defaults.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RfGainRangeOnArrivalTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(RfGainRangeOnArrivalTests));
        private const string Serial = "0638-0000-8600-0291";
        private const string Id = "2B2B2B2B-0000-4000-8000-000000001291";
        private const uint OtherPan = 0x40000003;

        private readonly GuiClientSlot _slot = GuiClientSlot.Acquire(@"Local\JJFlexRadio.Tests.RfGainRangeOnArrival." + Guid.NewGuid().ToString("N") + ".");
        private readonly IDisposable _slotScope;

        public RfGainRangeOnArrivalTests()
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

        private static int RangeRequests(RigOnVendorRadio rig, uint pan) =>
            rig.Vendor.Transport.Commands.Count(c => c.Contains("display pan rfgain_info 0x" + pan.ToString("X"), StringComparison.Ordinal));

        [Fact]
        public void Our_panadapter_asks_for_its_range_when_it_arrives_and_another_clients_does_not()
        {
            using var rig = FenceSeam.ReturningRig(Serial, _slot.Number, Id);
            FenceSeam.PanReported(rig, FenceSeam.PanA);
            Assert.Equal(1, RangeRequests(rig, FenceSeam.PanA));

            rig.Vendor.Status("display pan 0x" + OtherPan.ToString("X")
                + " client_handle=0x5E6F7A8B waterfall=0x0 center=14.200000 bandwidth=0.200000");
            Assert.Equal(0, RangeRequests(rig, OtherPan));
        }

        /// <summary>The control: with the station handlers not wired, the same
        /// status asks for nothing, so the request above is the handler's.</summary>
        [Fact]
        public void Unwired_the_same_status_asks_for_nothing()
        {
            using var rig = FenceSeam.ReturningRig(Serial, _slot.Number, Id, wire: false);
            FenceSeam.PanReported(rig, FenceSeam.PanA);
            Assert.Equal(0, RangeRequests(rig, FenceSeam.PanA));
        }
    }
}
