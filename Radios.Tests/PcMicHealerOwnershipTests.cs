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
        private static readonly FieldInfo StartAudioSeam = typeof(FlexBase).GetField("_startOutputAudio", Private)!;
        private static readonly FieldInfo StopAudioSeam = typeof(FlexBase).GetField("_stopOutputAudio", Private)!;
        private static readonly FieldInfo TheRadioField = typeof(FlexBase).GetField("theRadio", Private)!;
        private static readonly FieldInfo RunField = typeof(FlexBase).GetField("_remoteAudioRun", Private)!;
        private static readonly FieldInfo ConnectedField = typeof(FlexBase).GetField("_IsConnected", Private)!;
        private static readonly MethodInfo BeginRunMethod = typeof(FlexBase).GetMethod("BeginRemoteAudioRunMicState", Private)!;

        /// <summary>A start well past one second, so nothing here leans on
        /// the very first comparison.</summary>
        private const long Start = 5_000_000L;

        /// <summary>25 days in milliseconds: the low 32 bits, which is what
        /// <c>Environment.TickCount</c> would have read, are negative.</summary>
        private const long TwentyFiveDaysMs = 25L * 24 * 60 * 60 * 1000;

        public void Dispose() => _scope.Dispose();

        private static void Declare(RadioOwnership ownership, string serial = Serial) =>
            Assert.True(new RadioConfig { Ownership = ownership }.SaveForRadio(serial));

        private const string SecondSerial = "0686-0649-8600-0002";

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
                    && StartMethod != null && StopMethod != null && OutputChannelField != null
                    && StartAudioSeam != null && StopAudioSeam != null && TheRadioField != null && RunField != null
                    && ConnectedField != null,
                    "checkPcMicSelection, _pcMicExpected or _pcMicCheckClock is not where this test reaches it; "
                    + "every assertion below would be vacuous");
                Clock = clock;
                Fixture = new RigOnVendorRadio(Serial);
                Vendor = Fixture.Vendor;
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
            /// FlexLib receive stream on the radio in use. The PortAudio start
            /// and stop are the production seams <c>_startOutputAudio</c> and
            /// <c>_stopOutputAudio</c>, standing in for the device: nothing is
            /// opened and nothing is heard. <paramref name="deviceStarts"/>
            /// false is <c>Audio.Start</c> returning false on its one-second
            /// timeout, so the channel is left not started, as in production.
            /// </summary>
            public void StartPcAudio(bool deviceStarts = true)
            {
                if (OutputChannelField.GetValue(Rig) == null) PlantChannel(started: false);
                StartAudioSeam.SetValue(Rig, (Func<FlexBase.audioChannelData, bool>)(_ => deviceStarts));
                StopAudioSeam.SetValue(Rig, (Action<FlexBase.audioChannelData>)(_ => { }));
                bool started = (bool)StartMethod.Invoke(Rig, null)!;
                Assert.Equal(deviceStarts, started);
            }

            /// <summary>A receive channel on the radio in use.</summary>
            public FlexBase.audioChannelData PlantChannel(bool started)
            {
                var channel = new FlexBase.audioChannelData(
                    new Flex.Smoothlake.FlexLib.RXRemoteAudioStream(Vendor.Radio), "test.OpusOutputChan")
                { Started = started };
                OutputChannelField.SetValue(Rig, channel);
                StopAudioSeam.SetValue(Rig, (Action<FlexBase.audioChannelData>)(_ => { }));
                return channel;
            }

            /// <summary>The real <c>stopOpusOutputChannel</c>, as
            /// <c>remoteDone</c> calls it, whether or not the start
            /// succeeded.</summary>
            public void StopPcAudio() => StopMethod.Invoke(Rig, null);

            /// <summary>
            /// The same FlexBase is now talking to a different radio: what a
            /// reconnect does to <c>theRadio</c>, and nothing else, so the
            /// stale-state guards are tested without the clears that connect
            /// and run start also make. The receive channel goes, as
            /// <c>remoteAudioProc</c> drops it at the start of every run.
            /// </summary>
            public void ReconnectTo(string serial, string micInput)
            {
                Vendor = new VendorRadioFixture(serial);
                TheRadioField.SetValue(Rig, Vendor.Radio);
                // A connect: the radio reports its interlock, and the app reads
                // that report itself (repair round 3, #861 R3).
                Assert.True(Radios.InterlockReports.Watch(Vendor.Radio));
                Vendor.Status("interlock state=READY");
                OutputChannelField.SetValue(Rig, null);
                RadioReports(micInput);
            }

            /// <summary>A new remote-audio run on the same radio, without the
            /// clear the run's start makes.</summary>
            public void NextRunWithoutClearing() =>
                RunField.SetValue(Rig, (long)RunField.GetValue(Rig)! + 1);

            /// <summary>A new remote-audio run begins, through the exact call
            /// <c>remoteAudioProc</c> makes first, after an earlier run whose
            /// stop never ran (its thread was abandoned).</summary>
            public void NextRunBegins()
            {
                Assert.True(BeginRunMethod != null, "BeginRemoteAudioRunMicState is not where this test reaches it");
                long run = (long)RunField.GetValue(Rig)! + 1;
                RunField.SetValue(Rig, run);
                BeginRunMethod.Invoke(Rig, new object[] { run });
            }

            /// <summary>FlexBase's own connected flag, which the menu verb
            /// asks first, as the Radio menu's other verbs do. Cleared again
            /// on dispose so teardown runs as it does for every other rig.</summary>
            public void MarkConnected(bool connected) => ConnectedField.SetValue(Rig, connected);

            /// <summary>The radio this FlexBase is talking to now.</summary>
            public VendorRadioFixture Vendor;

            public FlexBase Rig => Fixture.Rig;

            /// <summary>The connection travels SmartLink: what
            /// <c>FlexBase.RemoteRig</c> reads, <c>Radio.IsWan</c>, set the
            /// way FlexLib's discovery sets it.</summary>
            public void OverSmartLink() =>
                typeof(Flex.Smoothlake.FlexLib.Radio).GetProperty(nameof(Flex.Smoothlake.FlexLib.Radio.IsWan))!
                    .GetSetMethod(nonPublic: true)!.Invoke(Vendor.Radio, new object[] { true });

            public bool PcMicExpected => (bool)ExpectedField.GetValue(Rig)!;

            /// <summary>The radio says where its mic input is — what another
            /// client moving it, or the owner picking up the hand mic, looks
            /// like from here.</summary>
            public void RadioReports(string input) =>
                Vendor.Status("transmit mic_selection=" + input);

            public string MicInput => Vendor.Radio.MicInput;

            /// <summary>One pass of the check, as the transmit branch of the
            /// remote-audio loop makes it.</summary>
            public void Check() => CheckMethod.Invoke(Rig, null);

            /// <summary>Every <c>mic input</c> command FlexLib sent.</summary>
            public string[] MicWrites => MicWritesOn(Vendor);

            public static string[] MicWritesOn(VendorRadioFixture radio) =>
                radio.Transport.Commands
                    .Where(c => c.StartsWith("mic input", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            public void Dispose()
            {
                MarkConnected(false);
                Fixture.Dispose();   // clears theRadio, whichever radio it is
            }
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
            // The radio reported a different input; WHO changed it - someone
            // at the radio, another client, a profile load - is not known, so
            // the sentence must not say (Codex follow-up finding 2).
            Assert.StartsWith("The radio's microphone input changed to MIC,", heard);
            Assert.DoesNotContain("Someone", heard);
            Assert.Contains("will not hear your transmitted audio", heard);
            Assert.Contains("Use Switch Mic Input Back to PC, found in the Radio menu, to put it back.", heard);
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
        // #686: the switch to PC at start, on every radio (Noel, 08:10)
        // ------------------------------------------------------------------

        /// <summary>How this operator is connected. Since Noel's 08:10
        /// revision none of these changes what the start does; the matrix is
        /// kept so that stays visibly true.</summary>
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
        // "If you don't own it and you connect to PC, it's got to switch it
        // to pc for you to be able to transmit, it just needs to put it back
        // if you don't own it." (Noel, 2026-10-20 08:10)
        [InlineData(RadioOwnership.Mine, Where.Local)]
        [InlineData(RadioOwnership.Mine, Where.SmartLink)]
        [InlineData(null, Where.Local)]
        [InlineData(RadioOwnership.Unset, Where.Local)]
        [InlineData(RadioOwnership.SomeoneElses, Where.Local)]
        [InlineData(null, Where.SmartLink)]
        [InlineData(RadioOwnership.Unset, Where.SmartLink)]
        [InlineData(RadioOwnership.SomeoneElses, Where.SmartLink)]
        [InlineData(RadioOwnership.Unset, Where.DeclaredNoPhysicalAccess)]
        [InlineData(RadioOwnership.SomeoneElses, Where.DeclaredNoPhysicalAccess)]
        public void StartingPcAudioAlwaysSwitchesToPcAndStoppingPutsItBack(RadioOwnership? ownership, Where where)
        {
            Arrange(ownership, where);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            if (where == Where.SmartLink) rig.OverSmartLink();

            rig.StartPcAudio();

            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);
            Assert.True(rig.PcMicExpected);
            Assert.Null(rig.Rig.LastPcMicNotOursReport);

            rig.StopPcAudio();

            Assert.Equal(new[] { "mic input PC", "mic input MIC" }, rig.MicWrites);
            Assert.Equal("MIC", rig.MicInput);
        }

        [Fact]
        public void OnSomeoneElsesRadioAlreadyOnPcTheStartHasNothingToSay()
        {
            Arrange(RadioOwnership.SomeoneElses, Where.Local);
            using var rig = new PcAudioRig(Start, beforePcAudio: "PC");

            rig.StartPcAudio();
            rig.StopPcAudio();

            Assert.Empty(rig.MicWrites);
            Assert.Null(rig.Rig.LastPcMicNotOursReport);
        }

        [Fact]
        public void ARemoteGuestIsSwitchedOnceAtStartAndNeverHealedAfter()
        {
            // The start's switch is a one-time act with a put-back; the
            // healer is an ongoing override, and #686 forbids it on someone
            // else's radio. If the owner picks up the hand mic, it stays
            // picked up, and the guest is told, with the Radio menu offer.
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
        // #686 repair round two: mic state that outlives its session
        // ------------------------------------------------------------------

        [Fact]
        public void AStartWhoseDeviceNeverStartsPutsTheOwnersInputBack()
        {
            // The start switches the owner's radio to PC. If PortAudio then
            // fails to start (Audio.Start's one-second
            // timeout), there is no PC audio session, so the owner's radio must
            // not be left on PC.
            Arrange(RadioOwnership.SomeoneElses, Where.SmartLink);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.OverSmartLink();

            rig.StartPcAudio(deviceStarts: false);
            rig.StopPcAudio();   // remoteDone runs the stop either way

            Assert.Equal(new[] { "mic input PC", "mic input MIC" }, rig.MicWrites);
            Assert.Equal("MIC", rig.MicInput);
            Assert.False(rig.PcMicExpected);
        }

        [Fact]
        public void AFailedStartPutsBackOnlyWhileTheInputIsStillThePcWeSet()
        {
            // Same terms as a stop: if somebody moved it while the device was
            // trying to start, their choice stands.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.PlantChannel(started: false);
            bool deviceAsked = false;
            StartAudioSeam.SetValue(rig.Rig, (Func<FlexBase.audioChannelData, bool>)(_ =>
            {
                deviceAsked = true;
                rig.RadioReports("BAL");
                return false;
            }));

            Assert.False((bool)StartMethod.Invoke(rig.Rig, null)!);
            rig.StopPcAudio();

            Assert.True(deviceAsked);
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
            Assert.Equal("BAL", rig.MicInput);
        }

        [Fact]
        public void AfterAFailedStartAReconnectToAnotherRadioGetsNothingFromTheFirst()
        {
            Arrange(RadioOwnership.SomeoneElses, Where.SmartLink);
            Declare(RadioOwnership.SomeoneElses, SecondSerial);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.OverSmartLink();
            rig.StartPcAudio(deviceStarts: false);
            rig.StopPcAudio();

            // The second radio is local and someone else's, already on PC:
            // its own start saves nothing and writes nothing there.
            rig.ReconnectTo(SecondSerial, "PC");
            rig.StartPcAudio();
            rig.StopPcAudio();

            Assert.Empty(rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);
        }

        [Fact]
        public void AValueSavedFromOneRadioIsNeverWrittenToAnother()
        {
            // The binding itself, with no failed start to clear things first:
            // a session saved MIC on the first radio and never stopped, and
            // the same FlexBase is now on a second radio that reads PC.
            Declare(RadioOwnership.Mine);
            Declare(RadioOwnership.SomeoneElses, SecondSerial);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);

            rig.ReconnectTo(SecondSerial, "PC");
            rig.PlantChannel(started: true);
            rig.StopPcAudio();

            Assert.Empty(rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);
        }

        [Fact]
        public void AValueSavedInAnEarlierRunIsNeverWrittenInALaterOne()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();

            rig.NextRunWithoutClearing();
            rig.StopPcAudio();

            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);
        }

        [Fact]
        public void ANewPcAudioSessionReportsItsOwnDivergence()
        {
            // A remote guest is told the owner moved the input, stops PC audio
            // while it is still on MIC, starts again (which switches to PC),
            // and the owner moves it again before any check has seen the PC
            // in between: the check runs only while transmitting. That is a
            // new session's divergence and it is reported. Within a session
            // it is still once per episode.
            Arrange(RadioOwnership.SomeoneElses, Where.SmartLink);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.OverSmartLink();

            rig.StartPcAudio();
            rig.RadioReports("MIC");
            rig.Check();
            rig.Clock += 1_000;
            rig.Check();
            Assert.Equal(1, rig.Rig.PcMicNotOursReports);

            rig.StopPcAudio();
            rig.StartPcAudio();
            Assert.Equal("PC", rig.MicInput);
            rig.RadioReports("MIC");
            rig.Clock += 1_000;
            rig.Check();
            rig.Clock += 1_000;
            rig.Check();

            Assert.Equal(2, rig.Rig.PcMicNotOursReports);
            Assert.Equal(new[] { "mic input PC", "mic input PC" }, rig.MicWrites);
        }

        [Fact]
        public void ARunStartAfterAnAbandonedStopPutsTheSameRadioBack()
        {
            // The earlier run's thread was abandoned before its stop ran, so
            // the owner's radio is still on the PC we set. The next run's start
            // writes exactly what that stop would have written: discarding it
            // would leave the owner's radio on PC, the harm #686 exists for.
            Arrange(RadioOwnership.SomeoneElses, Where.SmartLink);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.OverSmartLink();
            rig.StartPcAudio();

            rig.NextRunBegins();

            Assert.Equal(new[] { "mic input PC", "mic input MIC" }, rig.MicWrites);
            Assert.Equal("MIC", rig.MicInput);
            Assert.False(rig.PcMicExpected);
        }

        /// <summary>
        /// Repair round 9 (the coordinator's direction, the smaller machine): a
        /// put-back withheld under a carrier is OWED only in its own run and
        /// connection. A new run begun while the carrier is still up makes the
        /// old run's last attempt, is refused again, and ABANDONS it: never
        /// carried into the new run, whose start saves the PC it finds. What the
        /// operator notices: the mic input stays on PC. (Until round 9 the new
        /// run adopted the owner's MIC and its stop wrote it.)
        /// </summary>
        [Fact]
        public void A_put_back_withheld_under_a_carrier_is_abandoned_at_the_next_run_and_never_carried_into_it()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.Vendor.Status("interlock state=TRANSMITTING source=RCA tx_client_handle=0x0BADF00D");

            rig.StopPcAudio();                       // withheld: the room mic must not go on the air
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
            Assert.NotNull(SwapField.GetValue(rig.Rig));   // OWED, in its own run

            rig.NextRunBegins();                     // still keyed: the last attempt is refused, ABANDONED
            Assert.Null(SwapField.GetValue(rig.Rig));
            rig.StartPcAudio();                      // saves the PC it finds; adopts nothing
            Assert.Equal("PC", rig.MicInput);

            rig.Vendor.Status("interlock state=READY source= tx_client_handle=0x00000000");
            rig.StopPcAudio();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);        // what the operator notices
        }

        /// <summary>
        /// The other half of the same cell (round 9): owed in its own run and
        /// connection, the put-back is written when the radio next reports
        /// receive there, with no new run and no stop.
        /// </summary>
        [Fact]
        public void A_put_back_withheld_under_a_carrier_is_written_when_its_own_connection_reports_receive()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.Vendor.Status("interlock state=TRANSMITTING source=RCA tx_client_handle=0x0BADF00D");
            rig.StopPcAudio();                       // OWED
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);

            rig.Vendor.Status("interlock state=READY source= tx_client_handle=0x00000000");
            WaitFor(() => rig.MicWrites.Contains("mic input MIC"), "the owed put-back to be retried on the receive report");
            Assert.Equal("MIC", rig.MicInput);
            Assert.Null(SwapField.GetValue(rig.Rig));   // PAID
        }

        /// <summary>
        /// And a report of something other than receive does not retry it: a
        /// fault, or the carrier again, leaves it owed (round 9).
        /// </summary>
        [Fact]
        public void An_owed_put_back_is_not_retried_by_a_report_that_is_not_receive()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.Vendor.Status("interlock state=TRANSMITTING source=RCA tx_client_handle=0x0BADF00D");
            rig.StopPcAudio();
            rig.Vendor.Status("interlock state=TX_FAULT source=RCA tx_client_handle=0x0BADF00D");
            System.Threading.Thread.Sleep(150);
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
            Assert.NotNull(SwapField.GetValue(rig.Rig));
        }

        private static void WaitFor(Func<bool> condition, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < until, "timed out waiting for " + what);
                System.Threading.Thread.Sleep(10);
            }
        }

        /// <summary>
        /// Repair round 4 (Codex's F3): the owner chose BAL while the withheld
        /// debt for MIC was kept. Their newer choice wins: the next run saves
        /// BAL as the original, and its stop puts BAL back, never the older MIC.
        /// The round-3 test above, with no newer choice, is the control.
        /// </summary>
        [Fact]
        public void A_newer_input_chosen_while_a_withheld_debt_is_kept_wins_over_the_debt()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.Vendor.Status("interlock state=TRANSMITTING source=RCA tx_client_handle=0x0BADF00D");

            rig.StopPcAudio();                       // withheld: MIC kept as owed
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
            rig.RadioReports("BAL");                 // the owner's newer choice while PC audio is off

            rig.NextRunBegins();                     // still keyed
            rig.StartPcAudio();
            Assert.Equal("PC", rig.MicInput);

            rig.Vendor.Status("interlock state=READY source= tx_client_handle=0x00000000");
            rig.StopPcAudio();
            Assert.Equal("mic input BAL", rig.MicWrites.Last());
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
            Assert.Equal("BAL", rig.MicInput);
        }

        /// <summary>
        /// The same newer choice reported while the debt is kept, with the
        /// radio receiving again and no new run: the stop that owed MIC finds
        /// BAL, not the PC we set, and writes nothing (match only).
        /// </summary>
        [Fact]
        public void A_withheld_debt_meets_a_newer_choice_and_writes_nothing()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.Vendor.Status("interlock state=TRANSMITTING source=RCA tx_client_handle=0x0BADF00D");
            rig.StopPcAudio();
            rig.RadioReports("BAL");
            rig.Vendor.Status("interlock state=READY source= tx_client_handle=0x00000000");
            rig.NextRunBegins();                     // the start's own put-back pass meets BAL

            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
            Assert.Equal("BAL", rig.MicInput);
        }

        /// <summary>
        /// The adoption itself (repair round 4, F3): a new channel start on the
        /// same run, with the withheld MIC debt kept and the owner's BAL
        /// reported since, saves BAL as the original. No run-start pass runs
        /// first here, so this is the start's own check of the debt.
        /// </summary>
        [Fact]
        public void A_channel_start_does_not_adopt_a_debt_a_newer_choice_has_voided()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.Vendor.Status("interlock state=TRANSMITTING source=RCA tx_client_handle=0x0BADF00D");
            rig.StopPcAudio();                       // withheld: MIC kept as owed
            rig.RadioReports("BAL");

            rig.StartPcAudio();                      // the same run, a fresh channel start
            rig.Vendor.Status("interlock state=READY source= tx_client_handle=0x00000000");
            rig.StopPcAudio();
            Assert.Equal("mic input BAL", rig.MicWrites.Last());
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
        }

        /// <summary>
        /// A stop that finds a newer choice than the PC we set keeps no debt
        /// for it, carrier or not (repair round 4, F3): there is nothing of
        /// ours to put back over another operator's choice.
        /// </summary>
        [Fact]
        public void A_stop_that_finds_a_newer_choice_keeps_no_debt_even_under_a_carrier()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("BAL");                 // the owner chose BAL while PC audio was on
            rig.Vendor.Status("interlock state=TRANSMITTING source=RCA tx_client_handle=0x0BADF00D");
            rig.StopPcAudio();

            Assert.Null(SwapField.GetValue(rig.Rig));
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
        }

        [Fact]
        public void ARunStartLeavesALaterChoiceAlone()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("BAL");

            rig.NextRunBegins();

            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
            Assert.Equal("BAL", rig.MicInput);
        }

        // ── Repair round 5 (Codex's fourth re-check of #861, B1): the direct
        //    put-back checks its saved radio, connection and run WITH the
        //    write, and asks receive of that target ──

        private static readonly MethodInfo DecideMethod = typeof(FlexBase).GetMethod("PutBackSavedMicInputLocked", Private)!;

        /// <summary>The stop's decision, taken as production takes it (under the mic lock), and returned unrun.</summary>
        private static Action DecideUnderTheLock(PcAudioRig rig, string when)
        {
            Assert.True(DecideMethod != null, "PutBackSavedMicInputLocked is not where this test reaches it");
            lock (SwitchLockField.GetValue(rig.Rig)!)
                return (Action)DecideMethod.Invoke(rig.Rig, new object[] { when, true })!;
        }

        [Fact]
        public void A_mic_input_saved_on_one_connection_is_not_put_back_on_the_next_one_of_the_same_radio()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.Vendor.Transport.Reconnect();          // the same Radio, a new command connection
            rig.Vendor.Status("interlock state=READY");

            rig.StopPcAudio();
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
        }

        [Fact]
        public void A_next_runs_cleanup_does_not_pay_an_old_connections_mic_input_on_the_new_one()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.Vendor.Transport.Reconnect();
            rig.Vendor.Status("interlock state=READY");

            rig.NextRunBegins();                       // an earlier run's stop never ran
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
        }

        [Fact]
        public void A_reset_made_before_the_next_transport_exists_does_not_keep_an_ended_connections_debt()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.Vendor.Status("interlock state=TRANSMITTING source=RCA tx_client_handle=0x0BADF00D");
            rig.StopPcAudio();                         // withheld under the carrier, kept owed
            Assert.NotNull(SwapField.GetValue(rig.Rig));

            rig.Vendor.Transport.Fall();               // the link ends; FlexLib keeps it as the current one
            rig.Rig.ResetPcMicSession("connect (test)");   // Connect's reset, before the next transport
            Assert.Null(SwapField.GetValue(rig.Rig));

            rig.Vendor.Transport.Reconnect();
            rig.Vendor.Status("interlock state=READY");
            rig.RadioReports("PC");
            rig.NextRunBegins();
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
        }

        [Fact]
        public void A_put_back_decided_before_a_radio_switch_does_not_write_the_old_radio_while_it_transmits()
        {
            Declare(RadioOwnership.Mine);
            Declare(RadioOwnership.Mine, SecondSerial);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            var old = rig.Vendor;
            var write = DecideUnderTheLock(rig, "PC audio stopped (test)");
            Assert.NotNull(write);

            // Between the decision and the write: the old radio keys, and the
            // rig is now talking to another radio, which reports receive.
            old.Status("interlock state=TRANSMITTING source=RCA tx_client_handle=0x0BADF00D");
            rig.ReconnectTo(SecondSerial, "PC");
            write();

            Assert.Equal(new[] { "mic input PC" }, PcAudioRig.MicWritesOn(old));
            Assert.Empty(rig.MicWrites);
        }

        [Fact]
        public void A_put_back_refused_because_a_newer_run_began_is_abandoned_not_handed_to_that_run()
        {
            // Round 9 (the smaller machine): the old Action, refused for its run,
            // ABANDONS the owner's MIC with a MicPutBackWithheld line. The newer
            // run saved the PC this app left and owes nothing. Until round 9 the
            // debt was handed to that run (Codex's #861 N4 in round 6), which is
            // the hand-over five rounds kept finding new cells in.
            Declare(RadioOwnership.SomeoneElses);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            var old = DecideUnderTheLock(rig, "PC audio stopped (test)");

            rig.NextRunBegins();                       // the old stop is suspended; a new run starts
            rig.PlantChannel(started: false);          // remoteAudioProc's fresh channel
            rig.StartPcAudio();                        // saves the PC this app left as its original
            old();                                     // refused for its run: ABANDONED
            rig.StopPcAudio();

            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);
        }

        [Fact]
        public void A_put_back_decided_and_then_run_with_nothing_changed_writes_its_radio()
        {
            // The control for the two above: the same returned write, run
            // after the lock with nothing changed, puts the saved input back.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            var write = DecideUnderTheLock(rig, "PC audio stopped (test)");
            write();
            Assert.Equal(new[] { "mic input PC", "mic input MIC" }, rig.MicWrites);
        }

        [Fact]
        public void ARunStartOnADifferentRadioDiscardsTheValueUnwritten()
        {
            Declare(RadioOwnership.Mine);
            Declare(RadioOwnership.SomeoneElses, SecondSerial);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();

            rig.ReconnectTo(SecondSerial, "PC");
            rig.NextRunBegins();
            rig.PlantChannel(started: true);
            rig.StopPcAudio();

            Assert.Empty(rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);
        }

        // ------------------------------------------------------------------
        // #686 follow-up: Radio menu, Switch Mic Input Back to PC
        // (round five, Noel 12:28: verified the way full duplex is - make
        // the change, the radio answers a second or so later, and only a
        // real refusal is spoken)
        // ------------------------------------------------------------------

        private static readonly FieldInfo SwapField = typeof(FlexBase).GetField("_micInputSwap", Private)!;
        private static readonly FieldInfo SwitchLockField = typeof(FlexBase).GetField("_micSwitchLock", Private)!;

        /// <summary>A refusal code, as the radio sends one: any non-zero
        /// value is a refusal to <c>SendReplyCommand</c>'s callers.</summary>
        private const uint Refused = 0x50000016;

        /// <summary>PC audio running on someone else's radio, then the radio
        /// reported its input moved to MIC.</summary>
        private static PcAudioRig GuestRadioMovedToMic()
        {
            Arrange(RadioOwnership.SomeoneElses, Where.SmartLink);
            var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.OverSmartLink();
            rig.StartPcAudio();
            rig.RadioReports("MIC");
            rig.MarkConnected(true);
            return rig;
        }

        /// <summary>The press, as the Radio menu makes it: the session read on
        /// the UI thread, then the call. Its queued write is then run, as the
        /// main loop would run it at once.</summary>
        private static FlexBase.MicSwitchBackResult Press(PcAudioRig rig)
        {
            using var held = new HeldQueue(rig.Rig);
            var result = PressOnly(rig);
            held.Drain();
            return result;
        }

        /// <summary>The press alone; the caller holds the queue and decides
        /// when the write runs.</summary>
        private static FlexBase.MicSwitchBackResult PressOnly(PcAudioRig rig) =>
            rig.Rig.SwitchMicInputBackToPc(rig.Rig.PcMicSessionToken);

        /// <summary>The sequence number of the switch's own "mic input PC",
        /// the last one written, so the test can answer exactly it.</summary>
        private static int SwitchSequence(PcAudioRig rig)
        {
            var t = rig.Vendor.Transport;
            for (int i = t.Written.Count - 1; i >= 0; i--)
                if (FakeCommandTransport.CommandText(t.Written[i]) == "mic input PC") return t.SequenceOf(i);
            throw new InvalidOperationException("no mic input PC was written");
        }

        /// <summary>Every sentence the switch-back speaks on its own account,
        /// and whether the mic lock was held by the speaking thread when it
        /// did. Read through the production seam immediately before the
        /// speech gate; the sentence is Critical, a tier the verbosity filter
        /// never drops, and the screen reader's delivery is beyond a unit
        /// test.</summary>
        private sealed class SpeechLog
        {
            public readonly System.Collections.Generic.List<string> Spoken = new();
            public bool AnySpokenUnderLock;
            public SpeechLog(FlexBase rig)
            {
                object gate = SwitchLockField.GetValue(rig)!;
                rig.MicSwitchSpeechProbe = s =>
                {
                    Spoken.Add(s);
                    AnySpokenUnderLock |= System.Threading.Monitor.IsEntered(gate);
                };
            }
        }

        /// <summary>
        /// FlexBase's command queue, held undrained by telling it the main loop
        /// owns it, so a queued write runs only when the test says. The same
        /// helper as <c>StationProfileOwnershipExecutionTests.HeldQueue</c>,
        /// which is private there. Restores on dispose.
        /// </summary>
        private sealed class HeldQueue : IDisposable
        {
            private readonly object _queue;
            private readonly PropertyInfo _mainLoop;
            private readonly PropertyInfo _count;

            public HeldQueue(FlexBase rig)
            {
                _queue = typeof(FlexBase).GetField("q", Private)!.GetValue(rig)!;
                _mainLoop = _queue.GetType().GetProperty("MainLoop")!;
                _count = _queue.GetType().GetProperty("Count")!;
                _mainLoop.SetValue(_queue, true);
            }

            public int Count => (int)_count.GetValue(_queue)!;

            public void RunOne()
            {
                object item = _queue.GetType().GetMethod("Dequeue")!.Invoke(_queue, null)!;
                ((Delegate)item.GetType().GetField("Item")!.GetValue(item)!).DynamicInvoke();
            }

            public void Drain()
            {
                while (Count > 0) RunOne();
            }

            public void Dispose() => _mainLoop.SetValue(_queue, false);
        }

        [Fact]
        public void APressSendsTheSwitchWithAReplyAndClaimsNothingMore()
        {
            // The radio answers nothing at all yet: no reply, no status. The
            // press still returns at once, because nothing waits for the radio,
            // and its sentence claims only that the switch is under way. The
            // switch goes out on the reply-bearing path, so FlexLib's cache is
            // NOT written ahead of the radio: it still reads MIC until the
            // radio's own status says otherwise. Absence is traced, not spoken.
            using var rig = GuestRadioMovedToMic();
            var speech = new SpeechLog(rig.Rig);

            var result = Press(rig);

            Assert.Equal(FlexBase.MicSwitchBackOutcome.Switching, result.Outcome);
            Assert.Equal("Switching the mic input to PC.", result.Sentence);
            Assert.Equal(FlexBase.MicSwitchBackOutcome.Switching, rig.Rig.LastQueuedMicSwitchBack);
            Assert.Equal(new[] { "mic input PC", "mic input PC" }, rig.MicWrites);
            Assert.Equal("MIC", rig.MicInput);          // the setter's optimistic echo would read PC
            Assert.Null(rig.Rig.LastMicSwitchBackReply);
            Assert.Empty(speech.Spoken);
            Assert.True(rig.PcMicExpected);
        }

        [Fact]
        public void AnAcceptedSwitchSaysNothingMoreAndKeepsTheRestoreValue()
        {
            // Started on BAL; the owner moved it to MIC; the operator switched
            // it back to PC and the radio accepted. Nothing more is said, and
            // the stop owes back the owner's latest choice, the one the switch
            // overrode: MIC, not the BAL from before the session began.
            Arrange(RadioOwnership.SomeoneElses, Where.SmartLink);
            using var rig = new PcAudioRig(Start, beforePcAudio: "BAL");
            rig.OverSmartLink();
            rig.StartPcAudio();
            rig.RadioReports("MIC");
            rig.MarkConnected(true);
            var speech = new SpeechLog(rig.Rig);
            using var held = new HeldQueue(rig.Rig);

            Assert.Equal(FlexBase.MicSwitchBackOutcome.Switching, PressOnly(rig).Outcome);
            held.Drain();
            rig.RadioReports("PC");                         // the radio's own status
            rig.Vendor.ReplyOk(SwitchSequence(rig));

            Assert.Empty(speech.Spoken);
            Assert.StartsWith("accepted", rig.Rig.LastMicSwitchBackReply);
            Assert.Equal(FlexBase.MicSwitchBackOutcome.Switching, rig.Rig.LastQueuedMicSwitchBack);

            rig.StopPcAudio();
            held.Drain();

            Assert.Equal("mic input MIC", rig.MicWrites.Last());
            Assert.Equal("MIC", rig.MicInput);
        }

        [Fact]
        public void ARefusedSwitchIsSpokenOnceAndTheStopPutsNothingBack()
        {
            using var rig = GuestRadioMovedToMic();
            var speech = new SpeechLog(rig.Rig);
            using var held = new HeldQueue(rig.Rig);
            object swapBefore = SwapField.GetValue(rig.Rig)!;

            PressOnly(rig);
            held.Drain();
            rig.Vendor.Reply(SwitchSequence(rig), Refused, "mic input not allowed");

            Assert.Equal(new[] { "The radio did not switch the mic input to PC." }, speech.Spoken);
            Assert.Equal(FlexBase.MicSwitchBackOutcome.RefusedByRadio, rig.Rig.LastQueuedMicSwitchBack);
            Assert.StartsWith("refused", rig.Rig.LastMicSwitchBackReply);
            // The switch's restore value is gone: the bookkeeping is what it
            // was before the press.
            Assert.Same(swapBefore, SwapField.GetValue(rig.Rig));
            Assert.Equal("MIC", rig.MicInput);

            // The healer has had its report for this episode: the refusal.
            rig.Check();
            Assert.Equal(0, rig.Rig.PcMicNotOursReports);

            int writesBeforeStop = rig.MicWrites.Length;
            rig.StopPcAudio();
            held.Drain();

            // Nothing is put back that was never changed.
            Assert.Equal(writesBeforeStop, rig.MicWrites.Length);
            Assert.Equal("MIC", rig.MicInput);
            Assert.Single(speech.Spoken);
        }

        [Fact]
        public void ARefusalAfterTheSessionClosedDoesNothingAndTheStopIsNotOvertaken()
        {
            // The stop comes while the switch's answer is outstanding: the
            // radio has said nothing, so the cache still reads MIC while the
            // radio may be on PC. The put-back goes on the queue behind the
            // switch and sends MIC regardless. Then a late refusal arrives for
            // a session that has gone: traced, nothing spoken, nothing written.
            using var rig = GuestRadioMovedToMic();
            var speech = new SpeechLog(rig.Rig);
            using var held = new HeldQueue(rig.Rig);

            PressOnly(rig);
            held.Drain();
            int seq = SwitchSequence(rig);
            rig.StopPcAudio();                               // returns at once
            held.Drain();

            Assert.Equal(new[] { "mic input PC", "mic input PC", "mic input MIC" }, rig.MicWrites);

            rig.Vendor.Reply(seq, Refused, "");

            Assert.Empty(speech.Spoken);
            Assert.StartsWith("ignored", rig.Rig.LastMicSwitchBackReply);
            Assert.Contains("session", rig.Rig.LastMicSwitchBackReply);
            Assert.Equal(FlexBase.MicSwitchBackOutcome.Switching, rig.Rig.LastQueuedMicSwitchBack);
            Assert.Equal(3, rig.MicWrites.Length);
        }

        [Fact]
        public void ARefusalAfterTheRadioChangedDoesNothing()
        {
            using var rig = GuestRadioMovedToMic();
            var speech = new SpeechLog(rig.Rig);
            using var held = new HeldQueue(rig.Rig);
            var first = rig.Vendor;

            PressOnly(rig);
            held.Drain();
            int seq = SwitchSequence(rig);
            rig.ReconnectTo(SecondSerial, "MIC");
            object swapAtReconnect = SwapField.GetValue(rig.Rig)!;

            first.Reply(seq, Refused, "");

            Assert.Empty(speech.Spoken);
            Assert.StartsWith("ignored", rig.Rig.LastMicSwitchBackReply);
            Assert.Contains("radio", rig.Rig.LastMicSwitchBackReply);
            Assert.Same(swapAtReconnect, SwapField.GetValue(rig.Rig));
            Assert.Empty(rig.MicWrites);                     // the second radio: untouched
        }

        [Fact]
        public void TheHoldArmedWhileTheSwitchWaitsInTheQueueWritesNothing()
        {
            // Codex re-verify 3, blocker 1: the hold is checked at the press
            // and found lifted, then armed while the switch waits in the queue.
            // The queued half asks again and writes nothing, changes no
            // restore value, and leaves the speaking to the hold's own refusal
            // (GuardRefuses, which this rig traces rather than speaks): the
            // switch-back says nothing of its own.
            using var rig = GuestRadioMovedToMic();
            var speech = new SpeechLog(rig.Rig);
            using var held = new HeldQueue(rig.Rig);
            object swapBefore = SwapField.GetValue(rig.Rig)!;

            Assert.Equal(FlexBase.MicSwitchBackOutcome.Switching, PressOnly(rig).Outcome);
            rig.Rig.SetChangeNothingActive(true);
            held.Drain();

            Assert.Equal(FlexBase.MicSwitchBackOutcome.Held, rig.Rig.LastQueuedMicSwitchBack);
            Assert.Single(rig.MicWrites);                    // the start's PC, nothing more
            Assert.Same(swapBefore, SwapField.GetValue(rig.Rig));
            Assert.Equal("MIC", rig.MicInput);
            Assert.Empty(speech.Spoken);
        }

        [Fact]
        public void TheSwitchIsSentAndItsRefusalSpokenWithTheMicLockReleased()
        {
            // Round five, Codex's follow-up: the lock covers bookkeeping only.
            // Whenever a mic command is written, the writing thread must not
            // hold the lock, so a stop on any other thread takes it at once;
            // and the refusal is spoken the same way.
            using var rig = GuestRadioMovedToMic();
            var speech = new SpeechLog(rig.Rig);
            using var held = new HeldQueue(rig.Rig);
            object gate = SwitchLockField.GetValue(rig.Rig)!;
            var writtenUnderLock = new System.Collections.Generic.List<string>();
            rig.Vendor.Transport.CommandWritten = c =>
            {
                if (c.StartsWith("mic input", StringComparison.Ordinal) && System.Threading.Monitor.IsEntered(gate))
                    writtenUnderLock.Add(c);
            };

            PressOnly(rig);
            held.Drain();
            rig.Vendor.Reply(SwitchSequence(rig), Refused, "");

            Assert.Contains("mic input PC", rig.MicWrites.Skip(1));
            Assert.Empty(writtenUnderLock);
            Assert.Single(speech.Spoken);
            Assert.False(speech.AnySpokenUnderLock);
        }

        [Fact]
        public void AStopDuringTheSwitchsSendIsNotOvertakenAndNotKeptWaiting()
        {
            // The stop arrives WHILE the switch's command is being written,
            // with the radio's answer still to come. It runs to completion
            // there - the lock is free, because the send is outside it - and
            // its put-back goes on the queue behind the switch, so the radio
            // sees PC and then the owner's MIC, never the other way round.
            using var rig = GuestRadioMovedToMic();
            using var held = new HeldQueue(rig.Rig);
            object gate = SwitchLockField.GetValue(rig.Rig)!;
            bool stopped = false, lockFreeAtStop = false;
            rig.Vendor.Transport.CommandWritten = c =>
            {
                if (stopped || c != "mic input PC") return;
                stopped = true;
                lockFreeAtStop = !System.Threading.Monitor.IsEntered(gate);
                rig.StopPcAudio();
            };
            int before = rig.MicWrites.Length;

            PressOnly(rig);
            held.Drain();

            Assert.True(stopped);
            Assert.True(lockFreeAtStop);
            Assert.Equal(new[] { "mic input PC", "mic input MIC" }, rig.MicWrites.Skip(before).ToArray());
        }

        [Fact]
        public void AQueuedPutBackNeverLandsInTheNextSession()
        {
            // Codex re-verify 4, blocker: the stop comes while the switch's
            // answer is outstanding, so its put-back is queued; computer audio
            // starts again on the same radio before the queue runs, and the
            // new start writes PC. The old put-back belongs to the session
            // that ended: it must write nothing, or it silences the new
            // session's transmit audio.
            using var rig = GuestRadioMovedToMic();
            using var held = new HeldQueue(rig.Rig);

            PressOnly(rig);
            held.Drain();                                    // the switch is sent; no answer yet
            rig.StopPcAudio();                               // its put-back is queued
            Assert.Equal(1, held.Count);
            rig.StartPcAudio();                              // a new session writes PC
            held.Drain();                                    // the old put-back runs now

            Assert.Equal("mic input PC", rig.MicWrites.Last());
            Assert.Equal("PC", rig.MicInput);
        }

        [Fact]
        public void AnAcceptedSwitchEndsTheQuietSoTheNextMoveIsReported()
        {
            // Codex re-verify 4, follow-up: the radio accepts but never sends
            // mic_selection=PC, then reports a different input. The healer
            // must judge that report normally, not stay muted.
            using var rig = GuestRadioMovedToMic();

            Press(rig);
            rig.Vendor.ReplyOk(SwitchSequence(rig));         // accepted; no PC status follows
            rig.RadioReports("BAL");
            rig.Clock += 500;                                // still inside the switch's own quiet
            rig.Check();

            Assert.Equal(1, rig.Rig.PcMicNotOursReports);
            Assert.Equal(NotOursSentence("BAL"), rig.Rig.LastPcMicNotOursReport);
        }

        [Fact]
        public void ASwitchTheRadioNeverAnswersCannotMuteTheHealer()
        {
            // No reply and no status at all. The quiet holds for one check
            // interval and then ends by itself: nothing waits on it, and the
            // healer reports what the radio last said.
            using var rig = GuestRadioMovedToMic();

            Press(rig);
            rig.Clock += 500;
            rig.Check();
            Assert.Equal(0, rig.Rig.PcMicNotOursReports);    // in flight: quiet

            rig.Clock += 1_000;
            rig.Check();
            Assert.Equal(1, rig.Rig.PcMicNotOursReports);
            Assert.Equal(NotOursSentence("MIC"), rig.Rig.LastPcMicNotOursReport);
        }

        [Fact]
        public void A_restart_does_not_take_over_a_put_back_still_waiting_which_is_abandoned()
        {
            // Codex re-verify 5's sequence, under round 9's smaller machine. The
            // stop queues the owner's MIC; computer audio restarts before it
            // runs. The restart no longer takes the debt over (the round-seven
            // adoption is gone): it saves the PC it finds, the queued put-back
            // finds a newer session and is ABANDONED, and the mic stays on PC.
            using var rig = GuestRadioMovedToMic();
            using var held = new HeldQueue(rig.Rig);

            PressOnly(rig);
            held.Drain();
            rig.RadioReports("PC");
            rig.Vendor.ReplyOk(SwitchSequence(rig));
            rig.StopPcAudio();                               // queues the owner's MIC
            Assert.Equal(1, held.Count);
            int micBefore = rig.MicWrites.Count(c => c == "mic input MIC");
            rig.StartPcAudio();                              // restart before the queue runs
            held.Drain();                                    // the old put-back: abandoned, writes nothing
            Assert.Equal("PC", rig.MicInput);

            rig.StopPcAudio();
            held.Drain();

            Assert.Equal("PC", rig.MicInput);
            Assert.Equal(micBefore, rig.MicWrites.Count(c => c == "mic input MIC"));
        }

        [Fact]
        public void APutBackThatRunsBeforeTheRestartStillEndsOnTheOwnersInput()
        {
            using var rig = GuestRadioMovedToMic();
            using var held = new HeldQueue(rig.Rig);

            PressOnly(rig);
            held.Drain();
            rig.RadioReports("PC");
            rig.Vendor.ReplyOk(SwitchSequence(rig));
            rig.StopPcAudio();
            held.Drain();                                    // the put-back runs first
            Assert.Equal("MIC", rig.MicInput);

            rig.StartPcAudio();                              // saves MIC, writes PC
            Assert.Equal("PC", rig.MicInput);
            rig.StopPcAudio();
            held.Drain();

            Assert.Equal("MIC", rig.MicInput);
            Assert.Equal("mic input MIC", rig.MicWrites.Last());
        }

        /// <summary>Hold the radio transport's dispatch gate on another thread for <paramref name="ms"/>.</summary>
        private static System.Threading.Thread HoldDispatchFor(PcAudioRig rig, int ms)
        {
            using var held = new System.Threading.ManualResetEventSlim();
            var holder = new System.Threading.Thread(() =>
            {
                lock (rig.Vendor.Transport.ConnectionSync) { held.Set(); System.Threading.Thread.Sleep(ms); }
            }) { IsBackground = true };
            holder.Start();
            Assert.True(held.Wait(2000), "the dispatch gate was never held");
            return holder;
        }

        [Fact]
        public void A_switch_back_restore_refused_for_want_of_the_report_gate_stays_owed_and_the_next_receive_pays_it()
        {
            // Codex's #861 B4 (repair round 7): the raw put-back after a
            // switch-back, refused because the reader held its gate past the
            // bound, cleared its only debt. A restore fails closed; it is kept,
            // in its own run (round 9), and the radio's next receive report on
            // that connection writes it.
            using var rig = GuestRadioMovedToMic();
            using var held = new HeldQueue(rig.Rig);
            PressOnly(rig);
            held.Drain();                                    // the switch's PC, raw; the radio says nothing yet
            int micBefore = rig.MicWrites.Count(c => c == "mic input MIC");
            rig.StopPcAudio();                               // queues the put-back behind it
            var holder = HoldDispatchFor(rig, 350);
            held.Drain();                                    // refused at its write: the gate was not had in time
            Assert.True(holder.Join(2000));
            Assert.Equal(micBefore, rig.MicWrites.Count(c => c == "mic input MIC"));
            Assert.NotNull(SwapField.GetValue(rig.Rig));     // OWED

            rig.Vendor.Status("interlock state=READY source= tx_client_handle=0x00000000");
            WaitFor(() => held.Count > 0, "the owed switch-back put-back to be queued again on the receive report");
            held.Drain();
            Assert.Equal(micBefore + 1, rig.MicWrites.Count(c => c == "mic input MIC"));
            Assert.Equal("mic input MIC", rig.MicWrites.Last());
        }

        [Fact]
        public void An_old_put_back_resumed_after_a_newer_choice_was_already_restored_owes_nothing()
        {
            // Codex's #861 B5 (repair round 7): the handover kept an old MIC in
            // an empty slot although a newer BAL had been chosen and put back,
            // and a later run then wrote the stale MIC.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            var old = DecideUnderTheLock(rig, "PC audio stopped (test)");
            rig.RadioReports("BAL");                         // a newer choice
            rig.NextRunBegins();
            rig.PlantChannel(started: false);
            rig.StartPcAudio();                              // saves BAL, sets PC
            rig.StopPcAudio();                               // puts BAL back: that run is done
            old();                                           // refused for its run; must not revive MIC
            Assert.Null(SwapField.GetValue(rig.Rig));
            rig.RadioReports("PC");
            rig.NextRunBegins();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
        }

        // ── Repair round 8: the mic debt's state machine, cell by cell ──
        //
        // States: NONE; SAVED (this run set PC, the owner's input kept in
        // _micInputSwap); IN FLIGHT (a put-back decided, its write pending);
        // OWED (a refused put-back kept on this connection). Ends: PAID, VOID.
        // The machine and every cell's test are listed in the round-8 report.

        [Fact]
        public void Cell_in_flight_refused_for_want_of_the_report_gate_becomes_owed_and_the_next_run_pays_it()
        {
            // Codex's #861 B2: the round-7 rollback could not take the gate, the
            // cache kept the unsent MIC, and the next cleanup took MIC for the
            // owner's own choice and dropped the debt.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            var holder = HoldDispatchFor(rig, 450);
            rig.StopPcAudio();                               // refused: the gate was not had in time
            Assert.True(holder.Join(2000));
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);                // nothing unsent was cached
            Assert.NotNull(SwapField.GetValue(rig.Rig));     // OWED

            rig.NextRunBegins();                             // the cleanup pays it
            Assert.Equal("mic input MIC", rig.MicWrites.Last());
            Assert.Null(SwapField.GetValue(rig.Rig));
        }

        [Fact]
        public void Cell_in_flight_handed_over_after_a_choice_then_a_later_PC_is_void()
        {
            // Codex's #861 B3: BAL chosen and put back by a completed successor,
            // then a later PC, all before the old Action resumes. The later PC
            // does not make the old MIC obligation valid again.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            var old = DecideUnderTheLock(rig, "PC audio stopped (test)");
            rig.RadioReports("BAL");
            rig.NextRunBegins();
            rig.PlantChannel(started: false);
            rig.StartPcAudio();
            rig.StopPcAudio();                               // BAL is put back: that run is done
            rig.RadioReports("PC");                          // a later, independent PC
            old();                                           // refused for its run: VOID, not handed over
            Assert.Null(SwapField.GetValue(rig.Rig));
            rig.NextRunBegins();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
        }

        [Fact]
        public void Cell_in_flight_meeting_a_new_run_at_its_write_is_abandoned_and_never_written_into_it()
        {
            // Codex's #861 B4: the next run began inside the old write's vendor
            // setter; the final decision did not ask the run, and MIC went into
            // the new session.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            bool armed = true;
            rig.Vendor.Radio.AddTelnetTraceTopic("mic");
            ((System.ComponentModel.INotifyPropertyChanged)rig.Vendor.Radio).PropertyChanged += (_, e) =>
            {
                if (!armed || e.PropertyName != nameof(Flex.Smoothlake.FlexLib.Radio.TelnetTraceOutput)) return;
                string? last;
                var output = rig.Vendor.Radio.TelnetTraceOutput;
                lock (output) last = output.LastOrDefault();
                if (last != "mic input MIC") return;
                armed = false;
                rig.NextRunBegins();                         // the next run starts inside the old write
                rig.PlantChannel(started: false);
                rig.StartPcAudio();
            };
            rig.StopPcAudio();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
            // Round 9: abandoned. The new run holds only the PC it saved.
            Assert.Equal("PC", (string)SwapField.GetValue(rig.Rig)!.GetType().GetProperty("Saved")!.GetValue(SwapField.GetValue(rig.Rig))!);

            rig.NextRunBegins();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);
        }

        [Fact]
        public void Cell_owed_meeting_an_equal_PC_report_stays_owed_and_is_paid()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.Vendor.Status("interlock state=TRANSMITTING source=RCA tx_client_handle=0x0BADF00D");
            rig.StopPcAudio();                               // OWED: refused under a carrier
            Assert.NotNull(SwapField.GetValue(rig.Rig));
            rig.RadioReports("PC");                          // the radio repeats the PC we left
            rig.Vendor.Status("interlock state=READY source= tx_client_handle=0x00000000");
            rig.NextRunBegins();
            Assert.Equal("mic input MIC", rig.MicWrites.Last());
        }

        // ── Repair round 9: the smaller machine's cells that Codex's eighth
        //    re-check reached (#861 B2, B3, B4) ──

        /// <summary>Run <paramref name="act"/> once, on the writing thread, just before FlexLib sends <paramref name="command"/>.</summary>
        private static void BeforeTheSendOf(PcAudioRig rig, string command, Action act)
        {
            bool armed = true;
            rig.Vendor.Radio.AddTelnetTraceTopic("mic");
            ((System.ComponentModel.INotifyPropertyChanged)rig.Vendor.Radio).PropertyChanged += (_, e) =>
            {
                if (!armed || e.PropertyName != nameof(Flex.Smoothlake.FlexLib.Radio.TelnetTraceOutput)) return;
                string? last;
                var output = rig.Vendor.Radio.TelnetTraceOutput;
                lock (output) last = output.LastOrDefault();
                if (last != command) return;
                armed = false;
                act();
            };
        }

        [Fact]
        public void Cell_in_flight_after_a_choice_then_a_later_PC_in_the_same_run_is_void()
        {
            // Codex's #861 B2: the history check lived only in the hand-over, so
            // the ordinary payment, deciding from the cache alone, wrote MIC over
            // a BAL-then-PC history. Now every decision asks the choices reported
            // since this app set PC.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            var old = DecideUnderTheLock(rig, "PC audio stopped (test)");
            rig.RadioReports("BAL");
            rig.RadioReports("PC");                          // a later, independent PC
            old();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
            Assert.Null(SwapField.GetValue(rig.Rig));        // VOID
        }

        [Fact]
        public void Cell_in_flight_with_only_repeated_PC_reports_is_still_paid()
        {
            // The control: the same decision and the same later PC, with no
            // choice between, pays the owner's MIC.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            var old = DecideUnderTheLock(rig, "PC audio stopped (test)");
            rig.RadioReports("PC");
            old();
            Assert.Equal("mic input MIC", rig.MicWrites.Last());
        }

        [Fact]
        public void Cell_in_flight_meeting_a_choice_then_PC_at_the_vendors_pre_send_point_is_not_written()
        {
            // Codex's #861 B2, delivered at FlexLib's own pre-send notification:
            // both reports land before the write's final decision, which refuses.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            BeforeTheSendOf(rig, "mic input MIC", () => { rig.RadioReports("BAL"); rig.RadioReports("PC"); });
            rig.StopPcAudio();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
            Assert.Null(SwapField.GetValue(rig.Rig));
        }

        [Fact]
        public void Cell_owed_after_a_choice_then_PC_is_void_at_the_receive_and_at_the_next_run()
        {
            // Codex's #861 B2, the OWED half: a put-back withheld under a carrier,
            // then BAL and PC while it waits. The receive report's retry and the
            // next run's start both find the choice, and write nothing.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.Vendor.Status("interlock state=TRANSMITTING source=RCA tx_client_handle=0x0BADF00D");
            rig.StopPcAudio();                               // OWED
            rig.RadioReports("BAL");
            rig.RadioReports("PC");
            rig.Vendor.Status("interlock state=READY source= tx_client_handle=0x00000000");
            WaitFor(() => SwapField.GetValue(rig.Rig) == null, "the retry to find the debt void");
            rig.NextRunBegins();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
        }

        [Fact]
        public void Cell_in_flight_after_the_saved_input_was_reported_then_PC_is_void()
        {
            // Codex's #861 B2: a real report of the saved MIC settled the debt; a
            // later PC does not make it payable again.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            var old = DecideUnderTheLock(rig, "PC audio stopped (test)");
            rig.RadioReports("MIC");
            rig.RadioReports("PC");
            old();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
        }

        [Fact]
        public void Cell_paid_while_the_radio_reports_a_newer_choice_during_the_write_saves_that_choice_for_the_next_run()
        {
            // Codex's #861 B3: BAL reported after "mic input MIC" was captured,
            // before SendMicInput returned. FlexLib's cache then followed the
            // write and erased BAL, and the next run saved the false MIC and put
            // MIC back. The cache now keeps the radio's newer word (MIGRATION.md
            // item 22), so the next run saves BAL and puts BAL back.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            bool once = true;
            rig.Vendor.Transport.CommandWritten = command =>
            {
                if (!once || command != "mic input MIC") return;
                once = false;
                rig.RadioReports("BAL");
            };
            rig.StopPcAudio();                               // PAID: MIC written
            Assert.Equal("BAL", rig.MicInput);               // the radio's newer word stands

            rig.NextRunBegins();
            rig.PlantChannel(started: false);
            rig.StartPcAudio();                              // saves BAL
            rig.RadioReports("PC");
            rig.StopPcAudio();
            Assert.Equal(new[] { "mic input PC", "mic input MIC", "mic input PC", "mic input BAL" }, rig.MicWrites);
        }

        [Fact]
        public void Cell_paid_with_nothing_reported_during_the_write_saves_the_written_input_for_the_next_run()
        {
            // The control: no report during the write, so the cache follows it.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.StopPcAudio();
            Assert.Equal("MIC", rig.MicInput);
            rig.NextRunBegins();
            rig.PlantChannel(started: false);
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.StopPcAudio();
            Assert.Equal(new[] { "mic input PC", "mic input MIC", "mic input PC", "mic input MIC" }, rig.MicWrites);
        }

        [Fact]
        public void Cell_in_flight_refused_by_the_gate_after_a_new_run_began_is_abandoned_and_never_written()
        {
            // Codex's #861 B4: at MIC's pre-send point the dispatch gate is held
            // past its bound and a new run begins. The final predicate never runs,
            // so the round-8 run flag stayed false and the debt was lost to a
            // keep that refused the successor's PC. Round 9 re-reads the run at
            // the refusal: the debt's own run is over, so it is ABANDONED, on
            // purpose and with its line, and nothing writes MIC later.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            System.Threading.Thread? holder = null;
            BeforeTheSendOf(rig, "mic input MIC", () =>
            {
                holder = HoldDispatchFor(rig, 450);
                var successor = new System.Threading.Thread(() =>
                {
                    rig.NextRunBegins();
                    rig.PlantChannel(started: false);
                    rig.StartPcAudio();
                }) { IsBackground = true };
                successor.Start();
                Assert.True(successor.Join(2000), "the successor run never started");
            });
            rig.StopPcAudio();
            Assert.True(holder != null && holder.Join(2000));
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
            Assert.Equal("PC", (string)SwapField.GetValue(rig.Rig)!.GetType().GetProperty("Saved")!.GetValue(SwapField.GetValue(rig.Rig))!);

            rig.NextRunBegins();                             // the next run's cleanup: the successor saved PC, nothing owed
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);                // what the operator notices
        }

        [Fact]
        public void Cell_in_flight_refused_by_the_gate_in_its_own_run_stays_owed_and_the_next_receive_pays_it()
        {
            // The same refusal with the run unchanged is OWED, not abandoned.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            var holder = HoldDispatchFor(rig, 450);
            rig.StopPcAudio();
            Assert.True(holder.Join(2000));
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
            Assert.NotNull(SwapField.GetValue(rig.Rig));     // OWED
            rig.Vendor.Status("interlock state=READY source= tx_client_handle=0x00000000");
            WaitFor(() => rig.MicWrites.Contains("mic input MIC"), "the owed put-back to be paid on the receive report");
        }

        [Fact]
        public void Cell_owed_whose_connection_ends_is_abandoned_at_the_next_connect()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.Vendor.Status("interlock state=TRANSMITTING source=RCA tx_client_handle=0x0BADF00D");
            rig.StopPcAudio();                               // OWED
            rig.Vendor.Transport.Fall();
            rig.Rig.ResetPcMicSession("connect (test)");
            Assert.Null(SwapField.GetValue(rig.Rig));        // ABANDONED
            rig.Vendor.Transport.Reconnect();
            rig.Vendor.Status("interlock state=READY source= tx_client_handle=0x00000000");
            rig.NextRunBegins();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
        }

        // ── Repair round 10 (Codex's #861 N1): a choice reported WHILE our PC
        //    write is going out is a newer choice. The baseline is taken
        //    before the write, never rebased after it. ──

        /// <summary>Deliver <paramref name="reports"/> once, after the transport captured "mic input PC" and before its writer's caller returns.</summary>
        private static void DuringOurPcWrite(PcAudioRig rig, params string[] reports)
        {
            bool once = true;
            rig.Vendor.Transport.CommandWritten = command =>
            {
                if (!once || command != "mic input PC") return;
                once = false;
                foreach (var input in reports) rig.RadioReports(input);
            };
        }

        [Fact]
        public void Cell_saved_with_a_choice_then_PC_reported_during_the_starts_own_PC_write_is_void_at_the_stop()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            DuringOurPcWrite(rig, "BAL", "PC");
            rig.StartPcAudio();
            rig.StopPcAudio();
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);   // round 9 wrote MIC here
            Assert.Equal("PC", rig.MicInput);
        }

        [Fact]
        public void Cell_saved_with_only_our_PC_echoed_during_the_starts_write_is_paid_at_the_stop()
        {
            // The control: the radio echoes only our PC during the write.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            DuringOurPcWrite(rig, "PC");
            rig.StartPcAudio();
            rig.StopPcAudio();
            Assert.Equal(new[] { "mic input PC", "mic input MIC" }, rig.MicWrites);
        }

        [Fact]
        public void Cell_saved_with_a_choice_then_PC_during_the_PC_write_of_a_failed_start_is_void_at_its_cleanup()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            DuringOurPcWrite(rig, "BAL", "PC");
            rig.StartPcAudio(deviceStarts: false);    // the failed start's own cleanup decides
            Assert.Equal(new[] { "mic input PC" }, rig.MicWrites);
        }

        [Fact]
        public void Cell_switched_back_with_a_choice_then_PC_during_the_switchs_raw_write_is_void_at_the_stop()
        {
            using var rig = GuestRadioMovedToMic();
            using var held = new HeldQueue(rig.Rig);
            int micBefore = rig.MicWrites.Count(c => c == "mic input MIC");
            DuringOurPcWrite(rig, "BAL", "PC");
            PressOnly(rig);
            held.Drain();                                    // the switch's raw PC, with BAL then PC during it
            rig.Vendor.ReplyOk(SwitchSequence(rig));
            rig.StopPcAudio();
            held.Drain();
            Assert.Equal(micBefore, rig.MicWrites.Count(c => c == "mic input MIC"));
            Assert.Equal("PC", rig.MicInput);
        }

        [Fact]
        public void Cell_switched_back_with_only_our_PC_reported_is_paid_at_the_stop()
        {
            // The control for the switch-back route.
            using var rig = GuestRadioMovedToMic();
            using var held = new HeldQueue(rig.Rig);
            int micBefore = rig.MicWrites.Count(c => c == "mic input MIC");
            DuringOurPcWrite(rig, "PC");
            PressOnly(rig);
            held.Drain();
            rig.Vendor.ReplyOk(SwitchSequence(rig));
            rig.StopPcAudio();
            held.Drain();
            Assert.Equal(micBefore + 1, rig.MicWrites.Count(c => c == "mic input MIC"));
        }

        [Fact]
        public void Cell_saved_and_healed_after_a_newer_choice_is_void_at_the_stop()
        {
            // The healer re-asserts PC on the operator's own radio after another
            // input was reported. That report was a newer choice: it settled the
            // saved original, and round 10 no longer rebases after the heal, so
            // the stop writes nothing back.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.RadioReports("BAL");
            rig.Clock += 1_000;
            rig.Check();                                     // the heal writes PC
            Assert.Equal(new[] { "mic input PC", "mic input PC" }, rig.MicWrites);
            rig.RadioReports("PC");
            rig.StopPcAudio();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
        }

        // ── Repair round 11 (Codex's #861 N1): the saved input and its history
        //    are ONE snapshot, so a report while the start waits for the mic
        //    lock settles the value it saved ──

        /// <summary>
        /// Start PC audio on another thread while this one holds the mic lock;
        /// once the start is waiting for it, deliver <paramref name="reports"/>,
        /// then let it finish.
        /// </summary>
        private static void StartBlockedOnTheMicLockWhile(PcAudioRig rig, bool deviceStarts, params string[] reports)
        {
            object micLock = SwitchLockField.GetValue(rig.Rig)!;
            System.Threading.Thread start;
            System.Threading.Monitor.Enter(micLock);
            try
            {
                start = new System.Threading.Thread(() => rig.StartPcAudio(deviceStarts)) { IsBackground = true };
                start.Start();
                WaitFor(() => (start.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, "the start to wait for the mic lock");
                foreach (var input in reports) rig.RadioReports(input);
            }
            finally { System.Threading.Monitor.Exit(micLock); }
            Assert.True(start.Join(5000), "the start never finished");
        }

        [Fact]
        public void Cell_saved_while_a_choice_is_reported_during_the_starts_wait_for_the_mic_lock_is_void()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            StartBlockedOnTheMicLockWhile(rig, true, "BAL");
            rig.StopPcAudio();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);   // round 10 wrote MIC over BAL here
        }

        [Fact]
        public void Cell_saved_while_a_choice_then_PC_is_reported_during_the_starts_wait_is_void()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            StartBlockedOnTheMicLockWhile(rig, true, "BAL", "PC");
            rig.StopPcAudio();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
            Assert.Equal("PC", rig.MicInput);
        }

        [Fact]
        public void Cell_saved_while_a_choice_is_reported_during_a_failed_starts_wait_is_void_at_its_cleanup()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            StartBlockedOnTheMicLockWhile(rig, false, "BAL", "PC");
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
        }

        [Fact]
        public void Cell_saved_with_nothing_reported_during_the_starts_wait_is_paid()
        {
            // The control: the same held lock, no report.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            StartBlockedOnTheMicLockWhile(rig, true);
            rig.RadioReports("PC");
            rig.StopPcAudio();
            Assert.Equal(new[] { "mic input PC", "mic input MIC" }, rig.MicWrites);
        }

        [Fact]
        public void Cell_saved_after_a_choice_reported_before_the_start_restores_that_choice()
        {
            // The other control: BAL before the start is the original.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.RadioReports("BAL");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            rig.StopPcAudio();
            Assert.Equal(new[] { "mic input PC", "mic input BAL" }, rig.MicWrites);
        }

        // ── Repair round 12 (Codex's #861 B1 and B2): record first, act from
        //    the record, never change what could not be recorded ──

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_start_whose_mic_input_cannot_be_recorded_does_not_switch_it(bool deviceStarts)
        {
            // Codex's B2: round 11 still wrote PC while it had disabled the
            // put-back, a change with no way back. Now nothing is changed.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            var holder = HoldDispatchFor(rig, 1400);
            rig.StartPcAudio(deviceStarts);
            Assert.True(holder.Join(3000));
            Assert.Empty(rig.MicWrites);                     // no PC: what could not be recorded is not changed
            Assert.Null(SwapField.GetValue(rig.Rig));
            Assert.False(rig.PcMicExpected);
            rig.StopPcAudio();
            Assert.Empty(rig.MicWrites);
            Assert.Equal("MIC", rig.MicInput);
        }

        [Fact]
        public void A_switch_back_whose_mic_input_cannot_be_recorded_does_not_switch_and_says_so()
        {
            using var rig = GuestRadioMovedToMic();
            using var held = new HeldQueue(rig.Rig);
            var speech = new SpeechLog(rig.Rig);
            int pcBefore = rig.MicWrites.Count(c => c == "mic input PC");
            PressOnly(rig);
            var holder = HoldDispatchFor(rig, 1400);
            held.Drain();                                    // the queued half cannot record the input
            Assert.True(holder.Join(3000));
            Assert.Equal(pcBefore, rig.MicWrites.Count(c => c == "mic input PC"));
            Assert.Equal(FlexBase.MicSwitchBackOutcome.NotRecorded, rig.Rig.LastQueuedMicSwitchBack);
            Assert.Contains(Lexicon.Get("audio.mic.switch_back_refused"), speech.Spoken);
        }

        /// <summary>Start PC audio on another thread while this one holds the mic lock; once it waits, run <paramref name="during"/>, then let it finish.</summary>
        private static void StartBlockedOnTheMicLockDuring(PcAudioRig rig, bool deviceStarts, Action during)
        {
            object micLock = SwitchLockField.GetValue(rig.Rig)!;
            System.Threading.Thread start;
            System.Threading.Monitor.Enter(micLock);
            try
            {
                start = new System.Threading.Thread(() => rig.StartPcAudio(deviceStarts)) { IsBackground = true };
                start.Start();
                WaitFor(() => (start.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, "the start to wait for the mic lock");
                during();
            }
            finally { System.Threading.Monitor.Exit(micLock); }
            Assert.True(start.Join(5000), "the start never finished");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_start_whose_connection_is_replaced_after_its_capture_changes_nothing_on_the_new_one(bool deviceStarts)
        {
            // Codex's B1: the capture's input and count were bound to whatever
            // connection was current at installation, and MIC from the old one was
            // written over BAL on the new one.
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            StartBlockedOnTheMicLockDuring(rig, deviceStarts, () =>
            {
                rig.Rig.ResetPcMicSession("reconnect (test)");
                rig.Vendor.Transport.Reconnect();
                rig.Vendor.Status("interlock state=READY");
                rig.RadioReports("BAL");                     // the replacement's own original
            });
            rig.StopPcAudio();
            Assert.Empty(rig.MicWrites);                     // neither the PC nor the old MIC
            Assert.Equal("BAL", rig.MicInput);
        }

        [Fact]
        public void A_start_whose_radio_is_replaced_after_its_capture_writes_nothing_to_either()
        {
            Declare(RadioOwnership.Mine);
            Declare(RadioOwnership.Mine, SecondSerial);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            var first = rig.Vendor;
            var second = new VendorRadioFixture(SecondSerial);
            StartBlockedOnTheMicLockDuring(rig, true, () =>
            {
                TheRadioField.SetValue(rig.Rig, second.Radio);
                Assert.True(Radios.InterlockReports.Watch(second.Radio));
                second.Status("interlock state=READY");
                second.Status("transmit mic_selection=BAL");
            });
            rig.StopPcAudio();
            Assert.Empty(PcAudioRig.MicWritesOn(first));
            Assert.Empty(PcAudioRig.MicWritesOn(second));
            Assert.Equal("BAL", second.Radio.MicInput);
        }

        [Fact]
        public void The_abandoned_line_says_only_what_is_known()
        {
            // Codex's T1 (round 10): it no longer claims the input stays on PC.
            string text = FlexBasePartials.JoinedText();
            int at = text.IndexOf("private static void MicDebtAbandoned(MicInputSwap swap, string when, string why)", StringComparison.Ordinal);
            Assert.True(at > 0, "MicDebtAbandoned moved");
            string body = text.Substring(at, 1800);
            Assert.Contains("no write was made", body, StringComparison.Ordinal);
            Assert.Contains("The last mic input known for that radio is '", body, StringComparison.Ordinal);
            Assert.DoesNotContain("stays on PC", body, StringComparison.Ordinal);
            Assert.DoesNotContain("RebaseChoices", text, StringComparison.Ordinal);
            Assert.DoesNotContain("RebaseMicDebt", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Cell_in_flight_whose_radio_already_reports_the_saved_input_is_void()
        {
            Declare(RadioOwnership.Mine);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.StartPcAudio();
            rig.RadioReports("PC");
            var write = DecideUnderTheLock(rig, "PC audio stopped (test)");
            rig.RadioReports("MIC");                         // someone put it back already
            write();
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
            Assert.Null(SwapField.GetValue(rig.Rig));
        }

        [Fact]
        public void APutBackOwedToOneRadioIsNeverTakenOverOnAnother()
        {
            using var rig = GuestRadioMovedToMic();
            using var held = new HeldQueue(rig.Rig);

            PressOnly(rig);
            held.Drain();
            rig.RadioReports("PC");
            rig.StopPcAudio();                               // queued for the first radio
            rig.ReconnectTo(SecondSerial, "BAL");
            rig.StartPcAudio();                              // the second radio: saves its own BAL
            held.Drain();                                    // the first radio's put-back: dropped
            rig.StopPcAudio();
            held.Drain();

            Assert.Equal("BAL", rig.MicInput);
            Assert.DoesNotContain("mic input MIC", rig.MicWrites);
            Assert.Equal("mic input BAL", rig.MicWrites.Last());
        }

        [Fact]
        public void TheQuietDoesNotUseUpTheCheckInterval()
        {
            // Codex re-verify 5, follow-up: a check held by the quiet just
            // before it ends must not push the first real check nearly a
            // second later. The report comes as soon as the quiet ends.
            using var rig = GuestRadioMovedToMic();

            Press(rig);
            rig.Clock += FlexBase.PcMicSwitchQuietMs - 1;
            rig.Check();
            Assert.Equal(0, rig.Rig.PcMicNotOursReports);    // still quiet

            rig.Clock += 1;
            rig.Check();
            Assert.Equal(1, rig.Rig.PcMicNotOursReports);
        }

        [Fact]
        public void SwitchBackWithNoRadioRefusesAndSaysSo()
        {
            using var rig = GuestRadioMovedToMic();
            TheRadioField.SetValue(rig.Rig, null);

            var result = Press(rig);

            Assert.Equal(FlexBase.MicSwitchBackOutcome.NoRadio, result.Outcome);
            Assert.Equal(Lexicon.Get("connect.command_needs_radio"), result.Sentence);
            TheRadioField.SetValue(rig.Rig, rig.Vendor.Radio);
            Assert.Single(rig.MicWrites);
        }

        [Fact]
        public void SwitchBackWithoutPcAudioRefusesAndSaysSo()
        {
            Arrange(RadioOwnership.SomeoneElses, Where.SmartLink);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.MarkConnected(true);

            var result = Press(rig);

            Assert.Equal(FlexBase.MicSwitchBackOutcome.PcAudioNotRunning, result.Outcome);
            Assert.Equal("Computer audio isn't running, so the mic input was not switched to PC.", result.Sentence);
            Assert.Empty(rig.MicWrites);
        }

        [Fact]
        public void SwitchBackWhenAlreadyPcRefusesAndSaysSo()
        {
            Arrange(RadioOwnership.SomeoneElses, Where.SmartLink);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.OverSmartLink();
            rig.StartPcAudio();
            rig.MarkConnected(true);

            var result = Press(rig);

            Assert.Equal(FlexBase.MicSwitchBackOutcome.AlreadyPc, result.Outcome);
            Assert.Equal("The radio's mic input is already PC.", result.Sentence);
            Assert.Single(rig.MicWrites);
        }

        [Fact]
        public void SwitchBackUnderTheHoldRefusesAndTheHoldWins()
        {
            using var rig = GuestRadioMovedToMic();
            rig.Rig.SetChangeNothingActive(true);

            var result = Press(rig);

            Assert.Equal(FlexBase.MicSwitchBackOutcome.Held, result.Outcome);
            Assert.Null(result.Sentence);   // GuardRefuses has already spoken
            Assert.Single(rig.MicWrites);
            Assert.Equal("MIC", rig.MicInput);
        }

        [Fact]
        public void AfterAnAcceptedSwitchTheHealerReportsTheNextMoveAsANewEpisode()
        {
            using var rig = GuestRadioMovedToMic();
            rig.Check();
            Assert.Equal(1, rig.Rig.PcMicNotOursReports);
            rig.Clock += 400;
            Press(rig);

            // In flight, inside the switch's quiet: the radio has not
            // answered or reported PC yet, and the healer does not report the
            // same MIC again as news.
            rig.Clock += 600;
            rig.Check();
            Assert.Equal(1, rig.Rig.PcMicNotOursReports);

            rig.RadioReports("PC");
            rig.Vendor.ReplyOk(SwitchSequence(rig));
            rig.Clock += 1_000;
            rig.Check();                                     // sees PC: the episode is over

            rig.RadioReports("MIC");
            rig.Clock += 1_000;
            rig.Check();

            Assert.Equal(2, rig.Rig.PcMicNotOursReports);
            Assert.Equal("MIC", rig.MicInput);   // reported, never healed
        }

        [Fact]
        public void APressWhoseWriteRunsAfterAStopAndRestartWritesNothing()
        {
            // Codex re-verify 2, the queued-worker sequence: the press is made
            // in one session; before its write runs, computer audio stops and
            // starts again, and the owner moves the NEW session's input off PC.
            // The write belongs to the old session, so it must write nothing
            // and must not touch the new session's saved value.
            using var rig = GuestRadioMovedToMic();
            var speech = new SpeechLog(rig.Rig);
            using (var held = new HeldQueue(rig.Rig))
            {
                Assert.Equal(FlexBase.MicSwitchBackOutcome.Switching, PressOnly(rig).Outcome);
                Assert.Equal(1, held.Count);                     // queued, not yet run

                rig.StopPcAudio();
                rig.RadioReports("BAL");
                rig.StartPcAudio();                              // saves BAL, writes PC
                rig.RadioReports("MIC");                         // the owner moves it again

                held.RunOne();
            }

            Assert.Equal(FlexBase.MicSwitchBackOutcome.SessionChanged, rig.Rig.LastQueuedMicSwitchBack);
            Assert.Equal(2, rig.MicWrites.Count(c => c == "mic input PC"));   // the two starts only
            Assert.Equal("MIC", rig.MicInput);
            Assert.Equal(new[] { Lexicon.Get("audio.mic.switch_back_session_changed") }, speech.Spoken);
            Assert.False(speech.AnySpokenUnderLock);

            rig.StopPcAudio();   // the new session's stop: the owner's MIC stands
            Assert.Equal("MIC", rig.MicInput);
            Assert.Equal(2, rig.MicWrites.Count(c => c == "mic input PC"));
        }

        [Fact]
        public void AStopNeverWaitsOnASwitchBack()
        {
            // A press is in flight: queued, not yet run, and the radio has
            // answered nothing. The stop - which the UI waits for through
            // PCAudio's join - runs to completion on this thread at once, and
            // the queued write that runs afterwards finds the session closed.
            using var rig = GuestRadioMovedToMic();
            using var held = new HeldQueue(rig.Rig);
            PressOnly(rig);
            Assert.Equal(1, held.Count);

            rig.StopPcAudio();                                   // returns: nothing to wait on
            held.RunOne();

            Assert.Equal(FlexBase.MicSwitchBackOutcome.SessionChanged, rig.Rig.LastQueuedMicSwitchBack);
            Assert.Single(rig.MicWrites);                         // the start's PC, nothing after
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
