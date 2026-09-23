using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// RULED by Noel 2026-09-23 04:44, in his words: <i>"if I'm coming into a
    /// radio that's not mine, the max number of slices it grabs on startup
    /// should be two."</i> A guest taking four of someone's slices can starve
    /// the radio's owner, so the first-time-guest default becomes the rule and
    /// applies every time, whatever this computer remembers for that radio.
    ///
    /// <para>An OWNER with company keeps their remembered layout, up to
    /// whatever is free: it is their radio and their saved station, and the
    /// limit there is the other operator's slices, not a number we chose.</para>
    ///
    /// <para>Neither case ever pads beyond free capacity — #587's rule,
    /// unchanged.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class GuestSliceCapTests
    {
        private static StationHarness Guest(RadioOwnership ownership = RadioOwnership.SomeoneElses)
        {
            var h = new StationHarness();
            h.Port.Facts.Ownership = ownership;
            h.OurClientAdded();
            h.RadioHonoursPanafallRequests();
            return h;
        }

        [Fact]
        public void TheGuestCapIsTwo()
        {
            // Pinned on its own line, and written out as a literal everywhere
            // else: an assertion against the constant moves with it and proves
            // nothing (the Track G4 mutation run caught exactly that).
            Assert.Equal(2, StationLayout.SlicesForAGuest);
        }

        [Fact]
        public void AGuestOnFourFreeSlots_AsksForTwo_NotTheStartupLatchsFour()
        {
            var h = Guest();
            h.Port.Capacity = 4;
            h.Port.LegacyTarget = 4;

            var r = h.Run();

            Assert.Equal(2, r.Allocation.Target);
            Assert.Equal(2, h.Port.PanafallRequests);
            Assert.Equal(2, r.Allocation.Obtained);
            Assert.Empty(h.Port.GlobalLoadsSent);
        }

        [Fact]
        public void AGuestWithAFourSliceLayoutRememberedOnThisComputer_StillAsksForTwo()
        {
            // The ruling's sharp edge: the cap is not the first-connect
            // default that a remembered layout later replaces. It is the rule.
            var h = Guest();
            h.Port.Capacity = 4;
            h.Port.LegacyTarget = 4;
            h.Port.OwnerLayout = new StationLayout
            {
                Slices =
                {
                    new SliceLayoutEntry(14_250_000, "USB"),
                    new SliceLayoutEntry(7_150_000, "LSB"),
                    new SliceLayoutEntry(21_300_000, "USB"),
                    new SliceLayoutEntry(3_900_000, "LSB"),
                },
            };

            var r = h.Run();

            Assert.Equal(2, r.Allocation.Target);
            Assert.Equal(2, h.Port.PanafallRequests);
        }

        [Fact]
        public void ARadioNobodyHasAnsweredFor_IsNotMine_SoTheCapApplies()
        {
            // Unset is the DEFAULT and it means guest behaviour, so the cap
            // must key on "not declared Mine", never on "declared someone
            // else's".
            var h = Guest(RadioOwnership.Unset);
            h.Port.Capacity = 4;
            h.Port.LegacyTarget = 4;

            var r = h.Run();

            Assert.Equal(2, r.Allocation.Target);
            Assert.Equal(2, h.Port.PanafallRequests);
        }

        [Fact]
        public void AGuestWithOneFreeSlot_AsksForOne_BecauseTheCapIsACeilingAndNotAQuota()
        {
            var h = Guest();
            h.Port.Capacity = 1;
            h.Port.LegacyTarget = 4;

            var r = h.Run();

            Assert.Equal(1, r.Allocation.Target);
            Assert.Equal(1, h.Port.PanafallRequests);
        }

        [Fact]
        public void AnOwnerWithCompany_AndAFourSliceRememberedLayout_AsksForAllFour()
        {
            // The other half of the ruling, and the reason the cap cannot
            // simply be applied to every allocation: this is the operator's
            // own radio and their own saved station.
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.OtherClientAdded();
            h.Port.Capacity = 4;
            h.Port.LegacyTarget = 4;
            h.Port.OwnerLayout = new StationLayout
            {
                Slices =
                {
                    new SliceLayoutEntry(14_250_000, "USB"),
                    new SliceLayoutEntry(7_150_000, "LSB"),
                    new SliceLayoutEntry(21_300_000, "USB"),
                    new SliceLayoutEntry(3_900_000, "LSB"),
                },
                ProfileName = "K5NER",
            };
            h.RadioHonoursPanafallRequests();

            var r = h.Run();

            Assert.True(r.OwnerRefusedForCompany);
            Assert.Equal(4, r.Allocation.Target);
            Assert.Equal(4, h.Port.PanafallRequests);
            Assert.Empty(h.Port.GlobalLoadsSent);
        }

        [Fact]
        public void AnOwnerWithCompany_AndOnlyTwoFreeSlots_GetsTwoOfTheirFour_AndIsNeverPadded()
        {
            // The limit on the owner's side is the other operator's slices,
            // not a number we chose — and it clips, it never tops up.
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.OtherClientAdded();
            h.Port.Capacity = 2;
            h.Port.LegacyTarget = 4;
            h.Port.OwnerLayout = new StationLayout
            {
                Slices =
                {
                    new SliceLayoutEntry(14_250_000, "USB"),
                    new SliceLayoutEntry(7_150_000, "LSB"),
                    new SliceLayoutEntry(21_300_000, "USB"),
                    new SliceLayoutEntry(3_900_000, "LSB"),
                },
                ProfileName = "K5NER",
            };
            h.RadioHonoursPanafallRequests();

            var r = h.Run();

            Assert.Equal(2, r.Allocation.Target);
            Assert.Equal(2, h.Port.PanafallRequests);
        }
    }
}
