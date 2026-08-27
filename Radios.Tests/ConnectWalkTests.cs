using System.Collections.Generic;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 36 Track E, task #284: the itinerary a connect attempt walks.
    ///
    /// <para>The walk used to be a <c>List</c> and a <c>For</c> index inside
    /// one VB function, which meant it could not be asserted without a radio,
    /// a SmartLink account and a working network. It is an object now for a
    /// blunter reason than testability — it has to OUTLIVE the loop, because
    /// the news that a leg did not work arrives up to a minute after the loop
    /// returned — but being able to pin the ordering here without any of that
    /// is what stops the ordering rotting again.</para>
    /// </summary>
    public sealed class ConnectWalkTests
    {
        private static ConnectWalk Chain(ConnectPathKind chosen, bool forced,
            params ConnectPathKind[] fallbacks) =>
            ConnectWalk.Build("1111-2222-8600-3333", lowBW: false, chosen, fallbacks, forced);

        [Fact]
        public void TheChosenPathIsTheFirstLeg()
        {
            var walk = Chain(ConnectPathKind.SmartLink, forced: false, ConnectPathKind.Local);

            Assert.Equal(ConnectPathKind.SmartLink, walk.Current);
            Assert.True(walk.IsFirstLeg);
            Assert.Equal(0, walk.LegIndex);
            Assert.Equal("SmartLink,Local", walk.Describe());
        }

        [Fact]
        public void AFallbackIsReachedByMovingOn()
        {
            var walk = Chain(ConnectPathKind.SmartLink, forced: false, ConnectPathKind.Local);

            Assert.True(walk.HasNextLeg);
            Assert.Equal(ConnectPathKind.Local, walk.PeekNext);
            Assert.True(walk.MoveNext());

            Assert.Equal(ConnectPathKind.Local, walk.Current);
            Assert.False(walk.IsFirstLeg);
            Assert.True(walk.IsLastLeg);
            Assert.False(walk.HasNextLeg);
            Assert.Null(walk.PeekNext);
        }

        [Fact]
        public void AnExhaustedWalkStillNamesTheLegItGaveUpOn()
        {
            // So a caller that has run out of paths can still say WHICH path
            // it ran out on, rather than reporting a bare failure.
            var walk = Chain(ConnectPathKind.SmartLink, forced: false, ConnectPathKind.Local);
            Assert.True(walk.MoveNext());

            Assert.False(walk.MoveNext());
            Assert.Equal(ConnectPathKind.Local, walk.Current);
            Assert.Equal(1, walk.LegIndex);
        }

        [Fact]
        public void AForcedWalkHasExactlyOneLegEvenWhenFallbacksAreOffered()
        {
            // Force-remote is the hole-punch test instrument. A fallback that
            // succeeded over the other path would invalidate the test while
            // reporting success, so a forced walk cannot have one — and it
            // cannot have one even if the caller passes some in.
            var walk = Chain(ConnectPathKind.SmartLink, forced: true, ConnectPathKind.Local);

            Assert.True(walk.Forced);
            Assert.Single(walk.Legs);
            Assert.False(walk.HasNextLeg);
            Assert.True(walk.IsLastLeg);
            Assert.False(walk.MoveNext());
        }

        [Fact]
        public void TheChosenPathIsNotRepeatedAsItsOwnFallback()
        {
            // A chain that named the chosen path again would make the walk
            // retry the path that just failed, which is a slow way of failing
            // twice.
            var walk = Chain(ConnectPathKind.Local, forced: false,
                ConnectPathKind.Local, ConnectPathKind.SmartLink);

            Assert.Equal(2, walk.Legs.Count);
            Assert.Equal(ConnectPathKind.Local, walk.Legs[0]);
            Assert.Equal(ConnectPathKind.SmartLink, walk.Legs[1]);
        }

        [Fact]
        public void AWalkWithNoFallbacksIsStillAValidOneLegWalk()
        {
            var walk = ConnectWalk.Build("1111-2222-8600-3333", lowBW: true,
                ConnectPathKind.Local, null, forced: false);

            Assert.Single(walk.Legs);
            Assert.Equal(ConnectPathKind.Local, walk.Current);
            Assert.True(walk.LowBW);
            Assert.False(walk.HasNextLeg);
        }

        [Fact]
        public void TheWalkCarriesWhatARESUMEDLegNeedsToAskForTheSameThing()
        {
            // A leg run a minute later, after an open failed, must ask for the
            // same radio at the same bandwidth. Nothing else survives that
            // long — not the dialog, not the local variables, not the radio.
            var walk = ConnectWalk.Build("4925-1213-8600-6245", lowBW: true,
                ConnectPathKind.SmartLink, new[] { ConnectPathKind.Local }, forced: false);

            Assert.Equal("4925-1213-8600-6245", walk.Serial);
            Assert.True(walk.LowBW);
            walk.MoveNext();
            Assert.Equal("4925-1213-8600-6245", walk.Serial);
            Assert.True(walk.LowBW);
        }

        [Fact]
        public void TheItineraryItselfIsNotRewritableThroughLegs()
        {
            var walk = Chain(ConnectPathKind.SmartLink, forced: false, ConnectPathKind.Local);
            Assert.IsNotType<List<ConnectPathKind>>(walk.Legs);
            Assert.Equal(new[] { ConnectPathKind.SmartLink, ConnectPathKind.Local },
                walk.Legs.ToArray());
        }
    }
}
