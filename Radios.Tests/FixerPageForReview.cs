using System;
using System.IO;
using Radios.ChainChecks;
using Radios.Fixer;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Renders the Fixer Tool's page to standalone HTML files so a person can
    /// read it in a browser — no application, no WebView, no radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written so Noel can review the one part of this tool nothing can
    /// verify but a person.</b> Everything structural has tests behind it now.
    /// The PROSE has none, and it is the surface an operator meets when
    /// something has already gone wrong.
    /// </para>
    /// <para>
    /// <b>It feeds real FACTS through the real analyzers.</b> Nothing here
    /// fabricates an outcome or a sentence — the findings, the wording and the
    /// ordering are produced by <c>AudioSetupCheck</c>, <c>TransmitStages</c>
    /// and <c>FixerPage</c> exactly as they would be at a radio. A mock-up of
    /// the page would review the mock-up.
    /// </para>
    /// <para>
    /// <b>What a browser CANNOT tell you:</b> the buttons will not work. The
    /// page talks to its host through <c>window.chrome.webview</c>, which does
    /// not exist outside the application. Pressing things will do nothing or
    /// error. Reading, heading navigation, button navigation, tab order and
    /// how it all sounds are exactly right; anything that requires the host to
    /// answer is not.
    /// </para>
    /// <para>
    /// Skipped by default so it never runs as part of the suite. Generate with:
    /// <c>dotnet test Radios.Tests/Radios.Tests.csproj -c Debug -p:Platform=x64
    /// --filter "FullyQualifiedName~FixerPageForReview" -e JJFLEX_WRITE_REVIEW_PAGES=1</c>
    /// </para>
    /// </remarks>
    public class FixerPageForReview
    {
        /// <summary>
        /// Where the pages land: <c>C:\temp\fixer</c>, by Noel.
        /// </summary>
        /// <remarks>
        /// NOT MyDocuments. That is the operator's own folder and this is
        /// throwaway output — generated review copies do not belong in a place
        /// somebody keeps things. It also resolved to
        /// <c>OneDrive\Documents</c> via folder redirection, so the files were
        /// being written somewhere different from where a check for them
        /// looked, and syncing to the cloud into the bargain.
        /// </remarks>
        private static string OutputDir
        {
            get
            {
                const string dir = @"C:\temp\fixer";
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        [Fact]
        public void Write_the_pages_a_person_can_read()
        {
            // NO GATE. The first version of this only wrote when an
            // environment variable was set, the variable never reached the
            // test host, and the test PASSED having done nothing — a silent
            // success in the very file written to catch silent successes.
            // Writing three small files costs nothing; a guard that can lie
            // costs an afternoon.
            string dir = OutputDir;

            File.WriteAllText(Path.Combine(dir, "1-nothing-run-yet.html"),
                              FreshRun());

            File.WriteAllText(Path.Combine(dir, "2-problems-found.html"),
                              ProblemsFound());

            File.WriteAllText(Path.Combine(dir, "3-nothing-wrong.html"),
                              NothingWrong());

            File.WriteAllText(Path.Combine(dir, "report-as-emailed.txt"),
                              ProblemsFoundReport());

            // Assert on all four, naming the directory, so a failure says
            // WHERE it looked rather than just that something was false.
            foreach (string name in new[] { "1-nothing-run-yet.html",
                                            "2-problems-found.html",
                                            "3-nothing-wrong.html",
                                            "report-as-emailed.txt" })
            {
                string full = Path.Combine(dir, name);
                Assert.True(File.Exists(full), "did not write " + full);
                Assert.True(new FileInfo(full).Length > 500, "wrote almost nothing to " + full);
            }
        }

        // ---------------- the three states worth reading ----------------

        /// <summary>
        /// As it opens. Nothing run, nothing declared — so this is where the
        /// load question and every stage's own question are read cold, which is
        /// how an operator meets them.
        /// </summary>
        private static string FreshRun()
        {
            var run = new FixerRun(TransmitStageSet.Build(NoHosts()));
            return FixerPage.Render(run, new FixerPageState
            {
                SelectedStageId = TransmitStageSet.AudioSetup,
            });
        }

        /// <summary>
        /// A station with several real problems, each of a different KIND, so
        /// every branch of the three-way ownership shows its words: one we can
        /// fix, one the operator must fix, one nobody here can.
        /// </summary>
        private static string ProblemsFound()
        {
            FixerRun run = BrokenStation();
            return FixerPage.Render(run, new FixerPageState
            {
                SelectedStageId = TransmitStageSet.TransmitterCheck,
                DeclarationAnswers = new System.Collections.Generic.Dictionary<string, string>
                {
                    [TransmitStageSet.LoadDeclaration] = "A dummy load",
                },
            });
        }

        private static string ProblemsFoundReport() => FixerReport.PlainText(BrokenStation());

        /// <summary>
        /// The healthy case, which is worth reading precisely because it is the
        /// one nobody designs carefully. An operator whose station is fine
        /// should be told so plainly and briefly.
        /// </summary>
        private static string NothingWrong()
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

            return FixerPage.Render(run, new FixerPageState
            {
                SelectedStageId = TransmitStageSet.TransmitterCheck,
                DeclarationAnswers = new System.Collections.Generic.Dictionary<string, string>
                {
                    [TransmitStageSet.LoadDeclaration] = "A dummy load",
                },
            });
        }

        /// <summary>
        /// The station this tool exists for: remote radio, MME in use, PC audio
        /// off, an empty microphone profile, and a transmitter making no power.
        /// Deliberately several faults at once — one of each ownership kind —
        /// because the interesting question is how they read TOGETHER.
        /// </summary>
        private static FixerRun BrokenStation()
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
            return run;
        }

        /// <summary>
        /// No delegates at all — every stage honestly reports it could not run.
        /// That is the state the page opens in before anything is measured.
        /// </summary>
        private static TransmitStageSet.Hosts NoHosts() => new TransmitStageSet.Hosts();
    }
}
