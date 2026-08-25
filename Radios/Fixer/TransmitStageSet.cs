using System;
using System.Collections.Generic;
using Radios.ChainChecks;

namespace Radios.Fixer
{
    /// <summary>
    /// The transmit domain: five stages, ordered so the thing under test sits
    /// between two independent positive controls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stage 1 proves the microphone with no radio involved; stage 2 proves
    /// the transmitter with no audio involved; stages 3 and 4 differ in
    /// exactly one thing — the microphone. Stage 1's result is a BASELINE the
    /// spoken stage is read against, not just a gate. Stages 0–2 do not
    /// transmit.
    /// </para>
    /// <para>
    /// <b>This class is data and wiring only.</b> Every decision lives in
    /// <see cref="AudioSetupCheck"/> and <see cref="TransmitStages"/>, pure
    /// and tested; everything that touches a radio, an audio device or a
    /// transmitter lives in the host's <see cref="Hosts"/> delegates. A host
    /// that supplies nothing gets a set whose stages honestly record that
    /// they could not run — and, for the transmitting stages, that nothing
    /// was transmitted.
    /// </para>
    /// </remarks>
    public static class TransmitStageSet
    {
        // Stage ids — stable, used by the page, the run and the help links.
        public const string AudioSetup = "audio-setup";
        public const string MicrophoneCheck = "microphone-check";
        public const string TransmitterCheck = "transmitter-check";
        public const string InjectedTransmit = "injected-transmit";
        public const string SpokenTransmit = "spoken-transmit";

        // Skip choice ids.
        public const string SkipOperatorChoice = "operator-skip";
        public const string SkipRemoteNoDirectSpeech = "remote-no-direct-speech";
        public const string SkipNoMicrophone = "no-microphone";

        // The run declaration and its answers. The ANSWER is a host event —
        // the page sends it on its own and FixerTransmitGate.DeclareLoad
        // records it once; it is never carried on a stage request, and every
        // new run asks afresh because the station may have been re-cabled.
        //
        // There is deliberately NO "I'm not sure" choice: any declared answer
        // opens the transmit gate, and "not sure" is precisely the state in
        // which it must stay shut. Not answering IS the not-sure answer, and
        // the gate's refusal text tells the operator so at the point they try
        // to run something that transmits.
        public const string LoadDeclaration = "antenna-load";
        public const string LoadDummy = "dummy-load";
        public const string LoadAntenna = "antenna";

        /// <summary>
        /// What the host supplies. Every field may be null; a null measurement
        /// leaves its stage recorded as unable to run, and a null fix leaves
        /// its button recorded as unable to act. Nothing in this assembly
        /// substitutes for a missing delegate.
        /// </summary>
        /// <remarks>
        /// <c>ProbeTransmitter</c> and the two transmit stages are THE
        /// transmit boundary: the host implements them (typically around
        /// <c>TxTuneProbeRunner.Run</c> and the injection pipeline, behind its
        /// own load-declaration and cancellation guards), and nothing on this
        /// side of the delegate ever keys a radio.
        /// </remarks>
        public sealed class Hosts
        {
            // Measurements, one per stage.
            public Func<AudioSetupFacts> ReadAudioSetup;
            public Func<MicCheckFacts> MeasureMicrophone;
            public Func<TxTuneProbe.Result> ProbeTransmitter;
            public Func<InjectedTransmitFacts> RunInjectedTransmit;
            public Func<SpokenTransmitFacts> RunSpokenTransmit;

            /// <summary>What the operator declared the antenna socket is
            /// connected to — typically the gate's LoadDeclaration. It travels
            /// with the transmitter check's evidence, because a power reading
            /// with no stated load cannot be read afterwards by anyone.</summary>
            public Func<string> ReadLoadDeclaration;

            // Fixes stage 0 can offer.
            public FixerFixAction SwitchToWasapi;
            public FixerFixAction UseSuggestedInput;
            public FixerFixAction EnablePcAudio;
            public FixerFixAction FillMicProfile;
            public FixerFixAction ReopenConfiguredAudio;
        }

        /// <summary>Build the set around the host's delegates.</summary>
        public static FixerStageSet Build(Hosts hosts)
        {
            hosts = hosts ?? new Hosts();

            var operatorSkip = new FixerSkipChoice(
                SkipOperatorChoice,
                "I want to skip this stage for now.",
                FixerSkipEffect.OperatorChoice,
                "The report will say this stage was not run, and the overall answer is "
                + "weaker for it.");

            // The two microphone skip reasons stay distinct because they do
            // different things to the conclusion — one narrows the fault
            // domain, the other leaves the question open. Collapsing them
            // would make two very different reports read the same.
            var remoteSkip = new FixerSkipChoice(
                SkipRemoteNoDirectSpeech,
                "I can't speak directly into my radio — it is somewhere else.",
                FixerSkipEffect.NarrowsFaultDomain,
                "The radio being remote rules out speaking into it directly, but a "
                + "microphone on this computer can still be measured, so a comparison is "
                + "still possible. This narrows where the fault can be.");

            var noMicSkip = new FixerSkipChoice(
                SkipNoMicrophone,
                "I don't have access to a microphone at all.",
                FixerSkipEffect.LeavesQuestionOpen,
                "With no microphone there is nothing to compare the injected audio "
                + "against, so whether your own voice would get through is left open.");

            var stages = new List<FixerStage>
            {
                new FixerStage
                {
                    Id = AudioSetup,
                    Number = 0,
                    Title = "Audio setup",
                    Question = "What is your audio actually doing right now?",
                    Explanation =
                        "Reads the open audio stream directly: host API, input and "
                        + "output device, sample rate, channel count. Not your saved "
                        + "settings — the stream itself. Those two disagree more often "
                        + "than anyone expects, and when they do, the disagreement is "
                        + "usually the fault. You would never spot it on a settings page. "
                        + "Nothing here keys the radio.",
                    Transmits = false,
                    HelpTopic = "fixer/transmit/audio-setup",
                    SkipChoices = new[] { operatorSkip },
                    // This stage offers only the specific fixes it detected.
                    // The full picker is AudioDevicesDialog's job, so the one
                    // extra control is a hand-off to the host, never a picker
                    // of our own.
                    HostActions = new[]
                    {
                        new FixerHostAction("open-device-picker",
                                            "Open the full audio device picker"),
                    },
                    Execute = hosts.ReadAudioSetup == null ? (Func<FixerStageContext, FixerOutcome>)null
                        : ctx => AudioSetupCheck.Analyze(hosts.ReadAudioSetup()),
                },

                new FixerStage
                {
                    Id = MicrophoneCheck,
                    Number = 1,
                    Title = "Microphone check",
                    Question = "Is sound from your microphone arriving in this computer?",
                    Explanation =
                        "This listens to your microphone with the radio out of the "
                        + "picture entirely, and reports the peak in dBFS along with the "
                        + "integrated loudness in LUFS. It settles the first link in the "
                        + "chain before anything downstream is blamed for it. The reading "
                        + "is also kept as a baseline, because stage 4 is judged against "
                        + "it: a quiet result there means something quite different "
                        + "depending on whether your microphone measured well here.",
                    Transmits = false,
                    HelpTopic = "fixer/transmit/microphone-check",
                    SkipChoices = new[] { operatorSkip, remoteSkip, noMicSkip },
                    Execute = hosts.MeasureMicrophone == null ? (Func<FixerStageContext, FixerOutcome>)null
                        : ctx => TransmitStages.Microphone(hosts.MeasureMicrophone()),
                },

                new FixerStage
                {
                    Id = TransmitterCheck,
                    Number = 2,
                    Title = "Transmitter check",
                    Question = "Does the radio produce RF when it keys a tune carrier?",
                    Explanation =
                        "The radio keys a tune carrier — a steady unmodulated signal it "
                        + "generates itself — so no microphone, computer audio or streaming "
                        + "takes any part. Forward "
                        + "power and SWR are read while it is keyed. If RF appears, the "
                        + "transmitter is working and whatever is wrong lies somewhere in "
                        + "the audio path. If no RF appears, you never had an audio problem "
                        + "at all, and no amount of microphone testing would have found it. "
                        + "Nothing is transmitted until you have said what is connected to "
                        + "the antenna port.",
                    Transmits = true,
                    HelpTopic = "fixer/transmit/transmitter-check",
                    SkipChoices = new[] { operatorSkip },
                    Execute = hosts.ProbeTransmitter == null ? (Func<FixerStageContext, FixerOutcome>)null
                        : ctx => TransmitStages.Transmitter(hosts.ProbeTransmitter(),
                                                            hosts.ReadLoadDeclaration?.Invoke()),
                },

                new FixerStage
                {
                    Id = InjectedTransmit,
                    Number = 3,
                    Title = "Injected transmit",
                    Question = "Does audio reach the radio when your microphone is bypassed?",
                    Explanation =
                        "Tones and a generated voice are sent to the radio with your "
                        + "microphone taken out of the path, and the radio's own SC_MIC "
                        + "meter is watched to see what arrives. This check and stage 4 "
                        + "differ in exactly one thing, which is whether your microphone "
                        + "is involved. If this one works and stage 4 does not, your "
                        + "microphone is the problem. If neither works, your microphone is "
                        + "not the problem, and the fault lies between this computer and "
                        + "the radio.",
                    Transmits = true,
                    HelpTopic = "fixer/transmit/injected-transmit",
                    SkipChoices = new[] { operatorSkip },
                    Execute = hosts.RunInjectedTransmit == null ? (Func<FixerStageContext, FixerOutcome>)null
                        : ctx => TransmitStages.Injected(hosts.RunInjectedTransmit()),
                },

                new FixerStage
                {
                    Id = SpokenTransmit,
                    Number = 4,
                    Title = "Spoken transmit",
                    Question = "Does your voice reach the radio through your microphone?",
                    Explanation =
                        "You speak, and the radio's SC_MIC meter is watched to see what "
                        + "arrives. This is the same measurement stage 3 made, with your "
                        + "microphone put back into the path — that one difference is what "
                        + "makes the pair worth running. The result is read against your "
                        + "stage 1 microphone reading rather than judged on its own, so a "
                        + "quiet result here on a microphone that measured well earlier "
                        + "points somewhere quite specific.",
                    Transmits = true,
                    HelpTopic = "fixer/transmit/spoken-transmit",
                    SkipChoices = new[] { operatorSkip, remoteSkip, noMicSkip },
                    Execute = hosts.RunSpokenTransmit == null ? (Func<FixerStageContext, FixerOutcome>)null
                        : ctx => TransmitStages.Spoken(
                            hosts.RunSpokenTransmit(),
                            ctx.ResultFor(MicrophoneCheck)?.Payload as MicCheckFacts),
                },
            };

            var fixes = new Dictionary<string, FixerFixAction>(StringComparer.OrdinalIgnoreCase);
            AddFix(fixes, AudioSetupCheck.FixSwitchToWasapi, hosts.SwitchToWasapi);
            AddFix(fixes, AudioSetupCheck.FixUseSuggestedInput, hosts.UseSuggestedInput);
            AddFix(fixes, AudioSetupCheck.FixEnablePcAudio, hosts.EnablePcAudio);
            AddFix(fixes, AudioSetupCheck.FixFillMicProfile, hosts.FillMicProfile);
            AddFix(fixes, AudioSetupCheck.FixReopenConfiguredAudio, hosts.ReopenConfiguredAudio);

            return new FixerStageSet(
                id: "transmit",
                name: "Transmit",
                intro: "Work forward from stage 0. What each stage finds feeds the ones "
                     + "after it — stage 1 measures your microphone, and stage 4 is judged "
                     + "against that measurement rather than on its own. Jump around if "
                     + "you want; the report records what was skipped. Stages 0 and 1 do "
                     + "not key the radio.",
                stages: stages,
                fixActions: fixes,
                runDeclarations: new[]
                {
                    new FixerRunDeclaration(
                        LoadDeclaration,
                        "What is the antenna socket connected to right now?",
                        "Nothing transmits until you answer this question. If you are "
                        + "not sure, leave it unanswered: the checks that transmit will "
                        + "wait, and everything else still runs.",
                        new[]
                        {
                            new FixerDeclarationChoice(LoadDummy, "A dummy load"),
                            new FixerDeclarationChoice(LoadAntenna,
                                "An antenna, and transmitting a short test into it is fine"),
                        }),
                });
        }

        private static void AddFix(Dictionary<string, FixerFixAction> fixes, string id,
                                   FixerFixAction action)
        {
            if (action != null) fixes[id] = action;
        }
    }
}
