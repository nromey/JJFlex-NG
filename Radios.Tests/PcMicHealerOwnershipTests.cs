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
        private static readonly MethodInfo StartMethod = typeof(FlexBase).GetMethod("startOpusOutputChannel", Private)!;
        private static readonly MethodInfo StopMethod = typeof(FlexBase).GetMethod("stopOpusOutputChannel", Private)!;
        private static readonly FieldInfo OutputChannelField = typeof(FlexBase).GetField("opusOutputChannel", Private)!;

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

            public PcAudioRig(long clock, string? beforePcAudio = null)
            {
                Assert.True(ExpectedField != null && ClockField != null && CheckMethod != null
                    && StartMethod != null && StopMethod != null && OutputChannelField != null,
                    "checkPcMicSelection, _pcMicExpected or _pcMicCheckClock is not where this test reaches it; "
                    + "every assertion below would be vacuous");
                Clock = clock;
                Fixture = new RigOnVendorRadio(Serial);
                ClockField.SetValue(Fixture.Rig, (Func<long>)(() => Clock));
                if (beforePcAudio != null)
                {
                    // PC audio not started yet: the radio is wherever its
                    // owner left it, and nothing has asked for PC.
                    RadioReports(beforePcAudio);
                    return;
                }
                ExpectedField.SetValue(Fixture.Rig, true);
                RadioReports("PC");
            }

            /// <summary>
            /// The real <c>startOpusOutputChannel</c>, as <c>remoteAudioProc</c>
            /// calls it when receive audio comes up. The channel carries a real
            /// FlexLib receive stream on the vendored Radio and a PortAudio
            /// stream that was never constructed, so it holds no audio device
            /// and makes no sound: its <c>StartAudio</c> throws a
            /// NullReferenceException on its first line, which is AFTER every
            /// mic-input decision the start path makes. The test then marks the
            /// channel started, which is what a successful StartAudio would
            /// have done.
            /// </summary>
            public void StartPcAudio()
            {
                var channel = new FlexBase.audioChannelData(
                    new Flex.Smoothlake.FlexLib.RXRemoteAudioStream(Fixture.Vendor.Radio), "test.OpusOutputChan");
                channel.PortAudioStream = (JJPortaudio.JJAudioStream)
                    System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(JJPortaudio.JJAudioStream));
                OutputChannelField.SetValue(Rig, channel);
                var thrown = Assert.Throws<TargetInvocationException>(() => StartMethod.Invoke(Rig, null));
                Assert.IsType<NullReferenceException>(thrown.InnerException);
                Assert.Contains("StartAudio", thrown.InnerException!.StackTrace);
                channel.Started = true;
            }

            /// <summary>The real <c>stopOpusOutputChannel</c>. Its
            /// <c>StopAudio</c> throws the same way, after the restore and
            /// after the channel is marked stopped.</summary>
            public void StopPcAudio()
            {
                var thrown = Assert.Throws<TargetInvocationException>(() => StopMethod.Invoke(Rig, null));
                Assert.IsType<NullReferenceException>(thrown.InnerException);
                Assert.Contains("StopAudio", thrown.InnerException!.StackTrace);
            }

            public FlexBase Rig => Fixture.Rig;

            /// <summary>The connection travels SmartLink: what
            /// <c>FlexBase.RemoteRig</c> reads, <c>Radio.IsWan</c>, set the
            /// way FlexLib's discovery sets it.</summary>
            public void OverSmartLink() =>
                typeof(Flex.Smoothlake.FlexLib.Radio).GetProperty(nameof(Flex.Smoothlake.FlexLib.Radio.IsWan))!
                    .GetSetMethod(nonPublic: true)!.Invoke(Fixture.Vendor.Radio, new object[] { true });

            public bool PcMicExpected => (bool)ExpectedField.GetValue(Rig)!;

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

        private static string NotOursSentence(string current) =>
            Lexicon.Get("audio.mic.diverged_not_ours", ("current", current));

        // ------------------------------------------------------------------
        // #686: on a radio that is not the operator's, report and never write
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(null)]                         // never answered: no config at all
        [InlineData(RadioOwnership.Unset)]         // answered nothing, explicitly
        [InlineData(RadioOwnership.SomeoneElses)]
        public void OnARadioNotMarkedMineTheMicInputIsLeftAloneAndTheOperatorIsTold(RadioOwnership? ownership)
        {
            if (ownership.HasValue) Declare(ownership.Value);
            using var rig = new PcAudioRig(Start);

            rig.RadioReports("MIC");
            rig.Check();

            // The owner's choice stands: nothing went out, and the radio still
            // says MIC.
            Assert.Empty(rig.MicWrites);
            Assert.Equal("MIC", rig.MicInput);

            // And the operator is told why their transmit audio is going
            // nowhere, assembled as NVDA would speak it.
            string heard = rig.Rig.LastPcMicNotOursReport;
            Assert.Equal(NotOursSentence("MIC"), heard);
            Assert.Contains("is not reaching the transmitter", heard);
            Assert.Contains("was changed to MIC", heard);
            Assert.DoesNotContain("Setting it back to PC", heard);
            Assert.DoesNotContain("{current}", heard);
            Assert.Equal(1, rig.Rig.PcMicNotOursReports);
        }

        [Fact]
        public void OnTheOperatorsOwnRadioTheHealStands()
        {
            // The positive control for the theory above: the same divergence
            // on a radio declared Mine IS written back, so "no write" there is
            // the ownership answer and not a check that never ran.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start);

            rig.RadioReports("MIC");
            rig.Check();

            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);
            Assert.Null(rig.Rig.LastPcMicNotOursReport);
            Assert.Equal(0, rig.Rig.PcMicNotOursReports);
        }

        [Fact]
        public void TheReportFiresOncePerEpisodeAndNotOncePerCheck()
        {
            Declare(RadioOwnership.SomeoneElses);
            using var rig = new PcAudioRig(Start);

            rig.RadioReports("MIC");
            for (int i = 0; i < 5; i++)
            {
                rig.Check();
                rig.Clock += 1_000;
            }
            Assert.Equal(1, rig.Rig.PcMicNotOursReports);

            // The owner puts it back to PC: the episode ends.
            rig.RadioReports("PC");
            rig.Check();
            rig.Clock += 1_000;
            Assert.Equal(1, rig.Rig.PcMicNotOursReports);

            // And moves it away again: a new episode, a new report.
            rig.RadioReports("BAL");
            rig.Check();
            rig.Clock += 1_000;
            rig.Check();
            Assert.Equal(2, rig.Rig.PcMicNotOursReports);
            Assert.Equal(NotOursSentence("BAL"), rig.Rig.LastPcMicNotOursReport);

            // Through all of it, nothing was ever written to the owner's radio.
            Assert.Empty(rig.MicWrites);
        }

        // ------------------------------------------------------------------
        // #686 repair round one: the put-back at stop
        // ------------------------------------------------------------------

        [Fact]
        public void StopPutsTheSavedInputBackWhileTheInputIsStillThePcWeSet()
        {
            // The control for the theory below: nobody touched the input, so
            // the start's PC goes and the owner's MIC comes back.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");

            rig.StartPcAudio();
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);

            rig.StopPcAudio();
            Assert.Equal(new[] { "mic input PC", "mic input MIC" }, rig.MicWrites);
            Assert.Equal("MIC", rig.MicInput);
        }

        [Theory]
        [InlineData(RadioOwnership.Mine)]
        [InlineData(RadioOwnership.Unset)]
        [InlineData(RadioOwnership.SomeoneElses)]
        public void StopLeavesAMidSessionChangeAloneInsteadOfRestoringOverIt(RadioOwnership ownership)
        {
            // Started with the input on MIC; somebody - the owner at the
            // radio, another client - moved it to BAL while PC audio ran.
            // Putting MIC back at stop would overrule that choice with a value
            // nobody has asked for since the session began.
            Declare(ownership);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            int writesAtStart = rig.MicWrites.Length;

            rig.RadioReports("BAL");
            rig.StopPcAudio();

            Assert.Equal(writesAtStart, rig.MicWrites.Length);
            Assert.Equal("BAL", rig.MicInput);
        }

        // ------------------------------------------------------------------
        // #686 repair round one, part 1: the switch to PC at start
        // ------------------------------------------------------------------

        /// <summary>How this operator is connected, for the start matrix.</summary>
        public enum Where { Local, SmartLink, DeclaredNoPhysicalAccess }

        private static void Arrange(RadioOwnership? ownership, Where where)
        {
            var cfg = new RadioConfig();
            if (ownership.HasValue) cfg.Ownership = ownership.Value;
            if (where == Where.DeclaredNoPhysicalAccess)
            {
                cfg.NoPhysicalAccess = true;
                cfg.NoPhysicalAccessDecided = true;
            }
            if (ownership.HasValue || where == Where.DeclaredNoPhysicalAccess)
                Assert.True(cfg.SaveForRadio(Serial));
        }

        [Theory]
        // A remote operator has no microphone at the radio, so starting PC
        // audio switches to PC whoever owns it (Noel, 2026-10-20 05:25).
        [InlineData(RadioOwnership.Mine, Where.SmartLink)]
        [InlineData(RadioOwnership.Unset, Where.SmartLink)]
        [InlineData(RadioOwnership.SomeoneElses, Where.SmartLink)]
        [InlineData(null, Where.SmartLink)]
        [InlineData(RadioOwnership.Unset, Where.DeclaredNoPhysicalAccess)]
        [InlineData(RadioOwnership.SomeoneElses, Where.DeclaredNoPhysicalAccess)]
        // The operator's own radio, local: unchanged.
        [InlineData(RadioOwnership.Mine, Where.Local)]
        public void StartingPcAudioSwitchesToPcWhenTheOperatorIsRemoteOrTheRadioIsTheirs(RadioOwnership? ownership, Where where)
        {
            Arrange(ownership, where);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            if (where == Where.SmartLink) rig.OverSmartLink();

            rig.StartPcAudio();

            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);
            Assert.True(rig.PcMicExpected);
            Assert.Null(rig.Rig.LastPcMicNotOursReport);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(RadioOwnership.Unset)]
        [InlineData(RadioOwnership.SomeoneElses)]
        public void StartingPcAudioOnSomeoneElsesRadioFromItsLocalNetworkLeavesTheirMicAloneAndSaysSo(RadioOwnership? ownership)
        {
            Arrange(ownership, Where.Local);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");

            rig.StartPcAudio();

            Assert.Empty(rig.MicWrites);
            Assert.Equal("MIC", rig.MicInput);
            Assert.False(rig.PcMicExpected);   // we asked for nothing, so nothing to heal or report later

            string heard = rig.Rig.LastPcMicNotOursReport;
            Assert.Equal(Lexicon.Get("audio.mic.not_switched_not_ours", ("current", "MIC")), heard);
            Assert.Contains("will not reach the transmitter", heard);
            Assert.DoesNotContain("{current}", heard);
            Assert.Equal(1, rig.Rig.PcMicNotOursReports);

            // And stopping puts nothing back, because nothing was swapped.
            rig.StopPcAudio();
            Assert.Empty(rig.MicWrites);
            Assert.Equal("MIC", rig.MicInput);
        }

        [Fact]
        public void OnSomeoneElsesRadioAlreadyOnPcTheLocalStartHasNothingToSay()
        {
            Arrange(RadioOwnership.SomeoneElses, Where.Local);
            using var rig = new PcAudioRig(Start, beforePcAudio: "PC");

            rig.StartPcAudio();

            Assert.Empty(rig.MicWrites);
            Assert.Null(rig.Rig.LastPcMicNotOursReport);
        }

        [Fact]
        public void ARemoteGuestIsSwitchedOnceAtStartAndNeverHealedAfter()
        {
            // The start's switch is a one-time act; the healer is an ongoing
            // override, and #686 forbids it on someone else's radio. If the
            // owner picks up the hand mic, it stays picked up, and the remote
            // guest is told why their audio has stopped reaching the
            // transmitter.
            Arrange(RadioOwnership.SomeoneElses, Where.SmartLink);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.OverSmartLink();
            rig.StartPcAudio();
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);

            rig.RadioReports("MIC");
            rig.Check();
            rig.Clock += 1_000;
            rig.Check();

            Assert.Single(rig.MicWrites);
            Assert.Equal("MIC", rig.MicInput);
            Assert.Equal(NotOursSentence("MIC"), rig.Rig.LastPcMicNotOursReport);
            Assert.Equal(1, rig.Rig.PcMicNotOursReports);

            // And the put-back at stop respects the owner's choice too.
            rig.StopPcAudio();
            Assert.Single(rig.MicWrites);
            Assert.Equal("MIC", rig.MicInput);
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
