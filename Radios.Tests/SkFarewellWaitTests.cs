using System;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 35 Track G, #143. The SK farewell's wait is derived from the
    /// farewell about to be sent, and bounded at both ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was wrong.</b> Both paths that play the exit farewell —
    /// <c>FlexBase.Disconnect</c> and the application's shutdown handler —
    /// waited a flat 5000 ms. The comment behind that number named exactly the
    /// right case, the richer "73 de JJF SK" farewell at 25 WPM and above, and
    /// the arithmetic undershot it by about a second.
    /// </para>
    /// <para>
    /// <b>Why one constant could never have worked.</b> The farewell's length
    /// depends on keying speed, which is settable 10 to 60 — and the STRING
    /// also changes at 25 WPM, from roughly 63 PARIS units to roughly 129.
    /// More than double, at one word per minute faster. So there are two
    /// danger bands with a safe one between them: about 10-15 WPM on the short
    /// string, and about 25-31 on the long one. Noel runs 20, which sits in
    /// the safe middle, and testing one speed in the middle passes cleanly.
    /// That is how a defect at both ends of the range survived.
    /// </para>
    /// <para>
    /// <b>This is NOT the drain race, which has the same symptom.</b> That one
    /// signalled completion on a computed duration, so the wait was satisfied
    /// EARLY and teardown cut the tail; a bigger timeout does nothing for it,
    /// and it was fixed in Sprint 32 Track H by observing the device instead
    /// of predicting it. This one genuinely EXPIRES. Both produce "the
    /// farewell is truncated" and only one responds to a bigger number.
    /// </para>
    /// <para>
    /// The end-to-end confirmation is still the two-run trace test — set 24
    /// WPM, close, read the trace; set 25 WPM, close, read it again — because
    /// only that exercises the real element builder and the real audio device.
    /// These tests cover the part that can go wrong silently: the bounds.
    /// </para>
    /// </remarks>
    public sealed class SkFarewellWaitTests : IDisposable
    {
        private readonly Func<int>? _saved = ScreenReaderOutput.CwFarewellBudgetMs;

        public void Dispose() => ScreenReaderOutput.CwFarewellBudgetMs = _saved;

        /// <summary>
        /// Nothing wired — the CW side has not started, or notifications are
        /// off. The old flat figure is the fallback, so an unwired build waits
        /// exactly as long as it always did.
        /// </summary>
        [Fact]
        public void WithNoBudgetReporterItFallsBackToTheOldFlatWait()
        {
            ScreenReaderOutput.CwFarewellBudgetMs = null;
            Assert.Equal(FlexBase.SkFarewellFallbackMs, FlexBase.SkFarewellWaitMs());
        }

        /// <summary>
        /// A reported budget longer than the old constant is honoured. This is
        /// the whole fix: at 10 WPM the short farewell takes about 7.6 seconds
        /// and used to be cut off at five.
        /// </summary>
        [Theory]
        [InlineData(6390)]  // ~15 WPM, short string — was truncated
        [InlineData(7542)]  // ~25 WPM, long string — was truncated
        [InlineData(8910)]  // ~10 WPM, short string — the worst honest case
        public void ARealBudgetIsHonoured(int budget)
        {
            ScreenReaderOutput.CwFarewellBudgetMs = () => budget;
            Assert.Equal(budget, FlexBase.SkFarewellWaitMs());
        }

        /// <summary>
        /// The floor. A fast operator's farewell is short, and shortening the
        /// window to match would be a change nobody asked for — this fix may
        /// only ever lengthen the wait. A speed that fits today keeps fitting.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(4999)]
        public void AShortBudgetIsRaisedToTheFloor(int budget)
        {
            ScreenReaderOutput.CwFarewellBudgetMs = () => budget;
            Assert.Equal(FlexBase.SkFarewellFallbackMs, FlexBase.SkFarewellWaitMs());
        }

        /// <summary>
        /// The ceiling. A farewell must never be able to hang a disconnect or
        /// an exit, whatever the speed setting says or the sound card does.
        /// </summary>
        [Fact]
        public void AnAbsurdBudgetIsCappedAtTheCeiling()
        {
            ScreenReaderOutput.CwFarewellBudgetMs = () => int.MaxValue;
            Assert.Equal(FlexBase.SkFarewellCeilingMs, FlexBase.SkFarewellWaitMs());
        }

        /// <summary>
        /// The ceiling has to clear the worst HONEST case or it would be
        /// re-creating the bug it was added to bound. The slowest allowed
        /// speed is 10 WPM and the short farewell is about 63 PARIS units, so
        /// roughly 7.6 seconds of sending before any device latency.
        /// </summary>
        [Fact]
        public void TheCeilingClearsTheSlowestRealFarewell()
        {
            const int SlowestWpm = 10;
            const int ShortFarewellParisUnits = 63;
            int worstSendingMs = (1200 / SlowestWpm) * ShortFarewellParisUnits;

            Assert.True(FlexBase.SkFarewellCeilingMs > worstSendingMs + 2000,
                "The ceiling (" + FlexBase.SkFarewellCeilingMs + " ms) does not clear the "
                + "slowest real farewell (" + worstSendingMs + " ms of sending, plus device "
                + "latency and drain). It would truncate the exact case this change exists "
                + "to stop truncating.");
        }

        /// <summary>
        /// A reporter that throws must not be able to fail a disconnect. This
        /// is the same rule the farewell itself already follows: working out
        /// how long to wait for a sound is never worth failing a teardown.
        /// </summary>
        [Fact]
        public void AThrowingReporterFallsBackRatherThanPropagating()
        {
            ScreenReaderOutput.CwFarewellBudgetMs = () => throw new InvalidOperationException("boom");
            Assert.Equal(FlexBase.SkFarewellFallbackMs, FlexBase.SkFarewellWaitMs());
        }

        /// <summary>
        /// The floor must not exceed the ceiling, or the clamp throws and the
        /// disconnect path takes an exception on the way out. Cheap, and it is
        /// the failure that would be found at exit time rather than here.
        /// </summary>
        [Fact]
        public void TheBoundsAreTheRightWayRound()
        {
            Assert.True(FlexBase.SkFarewellFallbackMs < FlexBase.SkFarewellCeilingMs,
                "Floor " + FlexBase.SkFarewellFallbackMs + " is not below ceiling "
                + FlexBase.SkFarewellCeilingMs + "; Math.Clamp throws when they cross.");
        }
    }
}
