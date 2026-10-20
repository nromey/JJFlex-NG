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
            Assert.Contains("The Radio menu has Switch Mic Input Back to PC.", heard);
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
        // ------------------------------------------------------------------

        /// <summary>The radio answers "mic input PC" with the status a real
        /// radio sends, or with nothing at all, or refuses it.</summary>
        private static void RadioAnswersSwitch(PcAudioRig rig, bool reportsPc, uint replyCode = 0)
        {
            var vendor = rig.Vendor;
            vendor.Transport.CommandWritten = cmd =>
            {
                if (cmd != "mic input PC") return;
                vendor.Reply(vendor.LastSequence, replyCode, replyCode == 0 ? "" : "refused");
                if (reportsPc) vendor.Status("transmit mic_selection=PC");
            };
        }

        /// <summary>PC audio running on someone else's radio, then the owner
        /// moved the input to MIC.</summary>
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

        [Fact]
        public void SwitchBackIsSaidOnlyWhenTheRadioReportsPcAfterwards()
        {
            using var rig = GuestRadioMovedToMic();
            RadioAnswersSwitch(rig, reportsPc: true);

            var result = rig.Rig.SwitchMicInputBackToPc();

            Assert.Equal(FlexBase.MicSwitchBackOutcome.Switched, result.Outcome);
            Assert.Equal("Mic input switched back to PC.", result.Sentence);
            Assert.Equal("PC", rig.MicInput);
            Assert.Equal(2, rig.MicWrites.Count(c => c == "mic input PC"));
        }

        [Fact]
        public void ASwitchBackTheRadioNeverReportsIsNotAnnouncedAsDone()
        {
            // The reply came back OK but no status followed. FlexLib's own
            // setter would have made MicInput read PC regardless; the
            // reply-bearing send does not, so the radio's answer is the only
            // thing that can.
            using var rig = GuestRadioMovedToMic();
            RadioAnswersSwitch(rig, reportsPc: false);

            var result = rig.Rig.SwitchMicInputBackToPc();

            Assert.Equal(FlexBase.MicSwitchBackOutcome.NotConfirmed, result.Outcome);
            // Without a status the input is unknown: the sentence says only
            // that the radio did not confirm, and what it last reported
            // (Codex follow-up finding 1).
            Assert.Equal("The radio didn't confirm the switch to PC. The last mic input it reported is MIC.", result.Sentence);
            Assert.DoesNotContain("didn't switch", result.Sentence);
            Assert.DoesNotContain("still", result.Sentence);
            Assert.Equal("MIC", rig.MicInput);
        }

        [Fact]
        public void SwitchBackWithNoRadioRefusesAndSaysSo()
        {
            using var rig = GuestRadioMovedToMic();
            TheRadioField.SetValue(rig.Rig, null);

            var result = rig.Rig.SwitchMicInputBackToPc();

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

            var result = rig.Rig.SwitchMicInputBackToPc();

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

            var result = rig.Rig.SwitchMicInputBackToPc();

            Assert.Equal(FlexBase.MicSwitchBackOutcome.AlreadyPc, result.Outcome);
            Assert.Equal("The radio's mic input is already PC.", result.Sentence);
            Assert.Single(rig.MicWrites);
        }

        [Fact]
        public void SwitchBackUnderTheHoldRefusesAndTheHoldWins()
        {
            using var rig = GuestRadioMovedToMic();
            rig.Rig.SetChangeNothingActive(true);

            var result = rig.Rig.SwitchMicInputBackToPc();

            Assert.Equal(FlexBase.MicSwitchBackOutcome.Held, result.Outcome);
            Assert.Null(result.Sentence);   // GuardRefuses has already spoken
            Assert.Single(rig.MicWrites);
            Assert.Equal("MIC", rig.MicInput);
        }

        [Fact]
        public void AfterASwitchBackTheStopPutsBackWhatTheSwitchReplaced()
        {
            // Started on BAL; the owner moved it to MIC; the operator switched
            // it back to PC. The stop owes back the owner's latest choice, the
            // one the switch overrode: MIC, not the BAL from before the
            // session began.
            Arrange(RadioOwnership.SomeoneElses, Where.SmartLink);
            using var rig = new PcAudioRig(Start, beforePcAudio: "BAL");
            rig.OverSmartLink();
            rig.StartPcAudio();
            rig.RadioReports("MIC");
            rig.MarkConnected(true);
            RadioAnswersSwitch(rig, reportsPc: true);
            Assert.Equal(FlexBase.MicSwitchBackOutcome.Switched, rig.Rig.SwitchMicInputBackToPc().Outcome);

            rig.StopPcAudio();

            Assert.Equal("mic input MIC", rig.MicWrites.Last());
            Assert.Equal("MIC", rig.MicInput);
        }

        [Fact]
        public void AfterASwitchBackTheHealerReportsTheNextMoveAsANewEpisode()
        {
            using var rig = GuestRadioMovedToMic();
            rig.Check();
            Assert.Equal(1, rig.Rig.PcMicNotOursReports);
            RadioAnswersSwitch(rig, reportsPc: true);
            rig.Rig.SwitchMicInputBackToPc();

            rig.RadioReports("MIC");
            rig.Clock += 1_000;
            rig.Check();

            Assert.Equal(2, rig.Rig.PcMicNotOursReports);
            Assert.Equal("MIC", rig.MicInput);   // reported, never healed
        }

        // ------------------------------------------------------------------
        // #686 follow-up, repair round: an unconfirmed switch, and the overlap
        // ------------------------------------------------------------------

        [Fact]
        public void AnUnconfirmedSwitchBackIsStillPutBackAtStop()
        {
            // OK reply, no status: the radio may be on PC without saying so,
            // while the cache still reads MIC. The stop must not lean on "the
            // cache reads PC", or the owner's radio is left on PC.
            using var rig = GuestRadioMovedToMic();
            RadioAnswersSwitch(rig, reportsPc: false);
            Assert.Equal(FlexBase.MicSwitchBackOutcome.NotConfirmed, rig.Rig.SwitchMicInputBackToPc().Outcome);
            int before = rig.MicWrites.Length;

            rig.StopPcAudio();

            Assert.Equal(before + 1, rig.MicWrites.Length);
            Assert.Equal("mic input MIC", rig.MicWrites.Last());
        }

        [Fact]
        public void ASwitchBackTheRadioRefusedIsNotPutBack()
        {
            // An error reply means the radio did not switch and the cache is
            // right, so there is nothing to put back.
            using var rig = GuestRadioMovedToMic();
            RadioAnswersSwitch(rig, reportsPc: false, replyCode: 0x50000016);
            Assert.Equal(FlexBase.MicSwitchBackOutcome.NotConfirmed, rig.Rig.SwitchMicInputBackToPc().Outcome);
            int before = rig.MicWrites.Length;

            rig.StopPcAudio();

            Assert.Equal(before, rig.MicWrites.Length);
        }

        [Fact]
        public void AnUnconfirmedSwitchBackThenANewerReportIsLeftAlone()
        {
            using var rig = GuestRadioMovedToMic();
            RadioAnswersSwitch(rig, reportsPc: false);
            rig.Rig.SwitchMicInputBackToPc();
            rig.RadioReports("BAL");   // a newer choice, reported by the radio
            int before = rig.MicWrites.Length;

            rig.StopPcAudio();

            Assert.Equal(before, rig.MicWrites.Length);
            Assert.Equal("BAL", rig.MicInput);
        }

        [Fact]
        public void AnUnconfirmedSwitchBackIsNotReannouncedByTheHealerAsNews()
        {
            using var rig = GuestRadioMovedToMic();
            rig.Check();
            Assert.Equal(1, rig.Rig.PcMicNotOursReports);
            RadioAnswersSwitch(rig, reportsPc: false);
            rig.Rig.SwitchMicInputBackToPc();

            rig.Clock += 1_000;
            rig.Check();

            // The operator has just heard the last reported input; the same
            // divergence is not a new episode.
            Assert.Equal(1, rig.Rig.PcMicNotOursReports);
        }

        [Fact]
        public void AStopDuringTheSwitchBacksWaitWaitsForItAndPutsBackTheRightValue()
        {
            // Codex follow-up finding 3: the stop arrives while the
            // switch-back is waiting for the radio. It must wait, then decide
            // against what the radio actually reported, not consume the saved
            // value against a cache about to change.
            using var rig = GuestRadioMovedToMic();
            var vendor = rig.Vendor;
            System.Threading.Tasks.Task? stop = null;
            bool stopFinishedDuringTheWait = true;
            vendor.Transport.CommandWritten = cmd =>
            {
                if (cmd != "mic input PC") return;
                vendor.ReplyOk(vendor.LastSequence);
                stop = System.Threading.Tasks.Task.Run(() => rig.StopPcAudio());
                stopFinishedDuringTheWait = stop.Wait(300);
                vendor.Status("transmit mic_selection=PC");   // the radio answers, late
            };

            var result = rig.Rig.SwitchMicInputBackToPc();
            Assert.NotNull(stop);
            Assert.True(stop!.Wait(5_000), "the stop never finished");

            Assert.False(stopFinishedDuringTheWait, "the stop ran inside the switch-back's wait");
            Assert.Equal(FlexBase.MicSwitchBackOutcome.Switched, result.Outcome);
            Assert.Equal("mic input MIC", rig.MicWrites.Last());
            Assert.Equal("MIC", rig.MicInput);
        }

        [Fact]
        public void ASwitchBackArrivingDuringAStopsPutBackDoesNotSwitchAfterTheSessionEnds()
        {
            // The other order: the stop is already putting the owner's value
            // back when the switch-back is pressed. The channel still reads
            // started at that instant (the stop clears it after the put-back),
            // so the switch-back passes its first look, then waits for the
            // lock - and must find the session closed, not switch the radio
            // to PC with no session left to put it back.
            Arrange(RadioOwnership.SomeoneElses, Where.SmartLink);
            using var rig = new PcAudioRig(Start, beforePcAudio: "MIC");
            rig.OverSmartLink();
            rig.StartPcAudio();
            rig.MarkConnected(true);
            var vendor = rig.Vendor;
            System.Threading.Tasks.Task<FlexBase.MicSwitchBackResult>? press = null;
            vendor.Transport.CommandWritten = cmd =>
            {
                if (cmd != "mic input MIC" || press != null) return;
                press = System.Threading.Tasks.Task.Run(() => rig.Rig.SwitchMicInputBackToPc());
                press.Wait(300);   // it gets as far as the lock, and waits
            };

            rig.StopPcAudio();
            Assert.NotNull(press);
            Assert.True(press!.Wait(5_000), "the switch-back never finished");

            Assert.Equal(FlexBase.MicSwitchBackOutcome.PcAudioNotRunning, press.Result.Outcome);
            Assert.Equal(new[] { "mic input PC", "mic input MIC" }, rig.MicWrites);
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
