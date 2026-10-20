using System;
using System.Linq;
using System.Reflection;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Junkbox #686 and #649: <c>checkPcMicSelection</c>, the one healer that
    /// puts the radio's microphone input back to PC while computer transmit
    /// audio runs, driven through the REAL <see cref="FlexBase"/> method and
    /// the vendored Radio on its fake transport. What went out on the wire is
    /// read from the transport; the radio's own report of its mic input is
    /// replayed as a status line, the way another client moving it arrives.
    /// <para><b>What these tests cannot see.</b> The rig suppresses speech, so
    /// the report is read back through
    /// <c>FlexBase.LastPcMicNotOursReport</c>, which is set immediately before
    /// the <c>SuppressSpeech</c> check and the <c>ScreenReaderOutput.Speak</c>
    /// call. That call is at <c>VerbosityLevel.Critical</c>, the tier the
    /// verbosity filter never drops, so the filter is not a gate this
    /// sentence can be lost behind; the screen reader's own delivery is
    /// beyond any unit test. No socket, no window, no radio, and nothing is
    /// spoken.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class PcMicHealerOwnershipTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(PcMicHealerOwnershipTests));
        private const string Serial = "0686-0649-6300-0001";

        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo ExpectedField = typeof(FlexBase).GetField("_pcMicExpected", Private)!;
        private static readonly FieldInfo ClockField = typeof(FlexBase).GetField("_pcMicCheckClock", Private)!;
        private static readonly MethodInfo CheckMethod = typeof(FlexBase).GetMethod("checkPcMicSelection", Private)!;

        /// <summary>A start well past one second, so nothing here leans on
        /// the very first comparison.</summary>
        private const long Start = 5_000_000L;

        /// <summary>25 days in milliseconds: the low 32 bits, which is what
        /// <c>Environment.TickCount</c> would have read, are negative.</summary>
        private const long TwentyFiveDaysMs = 25L * 24 * 60 * 60 * 1000;

        public void Dispose() => _scope.Dispose();

        private static void Declare(RadioOwnership ownership) =>
            Assert.True(new RadioConfig { Ownership = ownership }.SaveForRadio(Serial));

        /// <summary>A rig with PC transmit audio up, as
        /// <c>startOpusOutputChannel</c> leaves it: it asked for PC and the
        /// radio reported PC.</summary>
        private sealed class PcAudioRig : IDisposable
        {
            public readonly RigOnVendorRadio Fixture;
            public long Clock;

            public PcAudioRig(long clock)
            {
                Assert.True(ExpectedField != null && ClockField != null && CheckMethod != null,
                    "checkPcMicSelection, _pcMicExpected or _pcMicCheckClock is not where this test reaches it; "
                    + "every assertion below would be vacuous");
                Clock = clock;
                Fixture = new RigOnVendorRadio(Serial);
                ClockField.SetValue(Fixture.Rig, (Func<long>)(() => Clock));
                ExpectedField.SetValue(Fixture.Rig, true);
                RadioReports("PC");
            }

            public FlexBase Rig => Fixture.Rig;

            /// <summary>The radio says where its mic input is — what another
            /// client moving it, or the owner picking up the hand mic, looks
            /// like from here.</summary>
            public void RadioReports(string input) =>
                Fixture.Vendor.Status("transmit mic_selection=" + input);

            public string MicInput => Fixture.Vendor.Radio.MicInput;

            /// <summary>One pass of the check, as the transmit branch of the
            /// remote-audio loop makes it.</summary>
            public void Check() => CheckMethod.Invoke(Rig, null);

            /// <summary>Every <c>mic input</c> command FlexLib sent.</summary>
            public string[] MicWrites =>
                Fixture.Vendor.Transport.Commands
                    .Where(c => c.StartsWith("mic input", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            public void Dispose() => Fixture.Dispose();
        }

        // ------------------------------------------------------------------
        // #649: the limiter's clock
        // ------------------------------------------------------------------

        [Fact]
        public void AMachineUpTwentyFiveDaysStillRunsTheCheck()
        {
            Assert.True(unchecked((int)TwentyFiveDaysMs) < 0,
                "premise: a 32-bit TickCount reads negative at 25 days of uptime");

            // On the operator's own radio the check's visible effect is the
            // heal, so a check that never runs is a write that never happens.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(TwentyFiveDaysMs);

            rig.RadioReports("MIC");
            rig.Check();
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);

            // A second later the check sees PC again, which ends the episode
            // (the heal is once per episode, and an episode ends only when a
            // check sees PC).
            rig.Clock += 1_000;
            rig.Check();

            // The limit still holds inside the second, and lets the next
            // second through: a revert 400 ms after a check is not looked at
            // until a second has passed.
            rig.RadioReports("MIC");
            rig.Clock += 400;
            rig.Check();
            Assert.Single(rig.MicWrites);

            rig.Clock += 700;
            rig.Check();
            Assert.Equal(2, rig.MicWrites.Length);
        }

        [Fact]
        public void TheLimiterIsSixtyFourBitAcrossTheThirtyTwoBitBoundary()
        {
            Assert.True(FlexBase.PcMicCheckDue((long)int.MaxValue + 1000L, int.MaxValue - 100L));
            Assert.False(FlexBase.PcMicCheckDue((long)int.MaxValue + 500L, int.MaxValue - 100L));
            // Never checked: due whatever the clock says, including a clock
            // whose low 32 bits are negative.
            Assert.True(FlexBase.PcMicCheckDue(TwentyFiveDaysMs, FlexBase.PcMicNeverChecked));
            Assert.True(FlexBase.PcMicCheckDue(0L, FlexBase.PcMicNeverChecked));
        }
    }
}
