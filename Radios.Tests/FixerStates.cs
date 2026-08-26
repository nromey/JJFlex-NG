using System;
using System.Collections.Generic;
using Radios.ChainChecks;
using Radios.Fixer;

namespace Radios.Tests
{
    /// <summary>
    /// The states of the transmit-checks page worth looking at, built from real
    /// facts through the real analyzers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One home, because there are now two readers.</b>
    /// <see cref="FixerPageForReview"/> writes these to HTML files so a person
    /// can read them in a browser; <c>IntegrationPassWalkTests</c> walks the
    /// same markup looking for what a person cannot reliably notice — a
    /// destructive control on a finished step, a stage with no way onward, a
    /// heading level quietly skipped.
    /// </para>
    /// <para>
    /// <b>Keeping them together is the point.</b> If the automated walk checked
    /// one set of states and the human review read another, the two would drift
    /// and each would be confident about a page the other had never seen. That
    /// is precisely the defect the pass exists to catch, so the pass may not
    /// commit it.
    /// </para>
    /// <para>
    /// Nothing here fabricates an outcome or a sentence. The findings, the
    /// wording and the ordering are produced by <c>AudioSetupCheck</c>,
    /// <c>TransmitStages</c> and <c>FixerPage</c> exactly as they would be at a
    /// radio. A mock-up of the page would review the mock-up.
    /// </para>
    /// </remarks>
    internal sealed record FixerReviewState(string Name, string FileName,
                                            FixerRun Run, FixerPageState PageState)
    {
        /// <summary>The page as the operator would meet it in this state.</summary>
        internal string Html => FixerPage.Render(Run, PageState);
    }

    /// <summary>Builders for the review states. Each call builds a fresh run —
    /// <see cref="FixerRun"/> is stateful, and a shared instance would let one
    /// reader's walk change what the next one sees.</summary>
    internal static class FixerStates
    {
        /// <summary>Every state, in the order a reviewer should meet them.</summary>
        internal static IReadOnlyList<FixerReviewState> All() => new[]
        {
            Fresh(), ProblemsFound(), NothingWrong(),
        };

        /// <summary>
        /// As it opens. Nothing run, nothing declared — so this is where the
        /// load question and every stage's own question are read cold, which is
        /// how an operator meets them.
        /// </summary>
        internal static FixerReviewState Fresh()
        {
            var run = new FixerRun(TransmitStageSet.Build(new TransmitStageSet.Hosts()));
            return new FixerReviewState("nothing-run-yet", "1-nothing-run-yet.html", run,
                new FixerPageState { SelectedStageId = TransmitStageSet.AudioSetup });
        }

        /// <summary>
        /// The station this tool exists for: remote radio, MME in use, PC audio
        /// off, an empty microphone profile, and a transmitter making no power.
        /// Deliberately several faults at once — one of each ownership kind —
        /// because the interesting question is how they read TOGETHER.
        /// </summary>
        internal static FixerReviewState ProblemsFound()
        {
            var hosts = new TransmitStageSet.Hosts
            {
                ReadLoadDeclaration = () => "A dummy load",
                ReadAudioSetup = () => new AudioSetupFacts
                {
                    OpenHostApi = "MME",
                    OpenInputDevice = "Microphone (USB Audio Device)",
                    OpenOutputDevice = "Speakers (Realtek High Definition Audio)",
                    OpenSampleRateHz = 44100,
                    OpenChannels = 2,
                    ConfiguredHostApi = "Windows WASAPI",
                    ConfiguredInputDevice = "Microphone (USB Audio Device)",
                    WasapiAvailable = true,
                    InputDeviceSelected = true,
                    SuggestedInputDevice = "Microphone (Audient EVO8)",
                    InputDeviceUnplugged = false,
                    WindowsInputMuted = true,
                    MicrophonePrivacyBlocked = false,
                    PcAudioOn = false,
                    RemoteRadio = true,
                    MicProfileEmpty = true,
                },
                MeasureMicrophone = () => new MicCheckFacts
                {
                    Measured = true,
                    AudioArrived = false,
                    Device = "Microphone (USB Audio Device)",
                    HostApi = "MME",
                    PeakDb = -94.0,
                    Detail = "Listened for 4 seconds at 44100 Hz, 2 channels. "
                           + "Peak -94.0 dBFS. Every sample was exactly zero, "
                           + "which is Windows feeding silence rather than a quiet room.",
                },
                ProbeTransmitter = () => TxTuneProbe.Result.Ran(
                    TxTuneProbe.Verdict.NoPower, DateTime.UtcNow,
                    Array.Empty<TxTuneProbe.Reading>(), 10, double.NaN, false,
                    "14.200.000", "USB", "ANT1"),
            };

            var run = new FixerRun(TransmitStageSet.Build(hosts));
            run.RunStage(TransmitStageSet.AudioSetup);
            run.RunStage(TransmitStageSet.MicrophoneCheck);
            run.RunStage(TransmitStageSet.TransmitterCheck);
            run.SkipStage(TransmitStageSet.SpokenTransmit,
                          TransmitStageSet.SkipRemoteNoDirectSpeech);

            return new FixerReviewState("problems-found", "2-problems-found.html", run,
                new FixerPageState
                {
                    SelectedStageId = TransmitStageSet.TransmitterCheck,
                    DeclarationAnswers = new Dictionary<string, string>
                    {
                        [TransmitStageSet.LoadDeclaration] = "A dummy load",
                    },
                });
        }

        /// <summary>
        /// The healthy case, which is worth reading precisely because it is the
        /// one nobody designs carefully. An operator whose station is fine
        /// should be told so plainly and briefly.
        /// </summary>
        internal static FixerReviewState NothingWrong()
        {
            var hosts = new TransmitStageSet.Hosts
            {
                ReadLoadDeclaration = () => "A dummy load",
                ReadAudioSetup = () => new AudioSetupFacts
                {
                    OpenHostApi = "Windows WASAPI",
                    OpenInputDevice = "Microphone (Audient EVO8)",
                    OpenOutputDevice = "Speakers (Audient EVO8)",
                    OpenSampleRateHz = 48000,
                    OpenChannels = 1,
                    ConfiguredHostApi = "Windows WASAPI",
                    ConfiguredInputDevice = "Microphone (Audient EVO8)",
                    WasapiAvailable = true,
                    InputDeviceSelected = true,
                    InputDeviceUnplugged = false,
                    WindowsInputMuted = false,
                    MicrophonePrivacyBlocked = false,
                    PcAudioOn = true,
                    RemoteRadio = false,
                    MicProfileEmpty = false,
                },
                MeasureMicrophone = () => new MicCheckFacts
                {
                    Measured = true,
                    AudioArrived = true,
                    Device = "Microphone (Audient EVO8)",
                    HostApi = "Windows WASAPI",
                    PeakDb = -14.2,
                    Detail = "Listened for 4 seconds at 48000 Hz, 1 channel. "
                           + "Peak -14.2 dBFS, integrated loudness -21.6 LUFS.",
                },
                ProbeTransmitter = () => TxTuneProbe.Result.Ran(
                    TxTuneProbe.Verdict.MakesPower, DateTime.UtcNow,
                    Array.Empty<TxTuneProbe.Reading>(), 10, 1.2, false,
                    "14.200.000", "USB", "ANT1"),
            };

            var run = new FixerRun(TransmitStageSet.Build(hosts));
            run.RunStage(TransmitStageSet.AudioSetup);
            run.RunStage(TransmitStageSet.MicrophoneCheck);
            run.RunStage(TransmitStageSet.TransmitterCheck);
            run.SkipStage(TransmitStageSet.InjectedTransmit,
                          TransmitStageSet.SkipOperatorChoice);
            run.SkipStage(TransmitStageSet.SpokenTransmit,
                          TransmitStageSet.SkipOperatorChoice);

            return new FixerReviewState("nothing-wrong", "3-nothing-wrong.html", run,
                new FixerPageState
                {
                    SelectedStageId = TransmitStageSet.TransmitterCheck,
                    DeclarationAnswers = new Dictionary<string, string>
                    {
                        [TransmitStageSet.LoadDeclaration] = "A dummy load",
                    },
                });
        }
    }
}
