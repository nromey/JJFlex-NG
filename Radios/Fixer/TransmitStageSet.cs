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
        // FOUR answers since Sprint 35 (#244) — the operator answering "what
        // is on the socket" is stating what, which port, and what power is
        // acceptable, and two of the three used to be thrown away. "Nothing,
        // or I am not sure" REPLACED the earlier design's deliberate absence
        // of a not-sure choice: picking it keeps the gate shut exactly as not
        // answering did, but the operator who picks it has TOLD us something
        // and now gets an explicit refusal instead of a silent wait. The
        // gate, not this file, owns what each kind permits.
        public const string LoadDeclaration = "antenna-load";
        public const string LoadDummy = "dummy-load";
        public const string LoadAntenna = "antenna";
        public const string LoadAmplifier = "amplifier";
        public const string LoadNothingUnsure = "nothing-unsure";

        /// <summary>
        /// Map a load choice id from the wire to what it means to the gate.
        /// An unknown or missing id maps to
        /// <see cref="FixerLoadKind.NothingOrUnsure"/> — an answer the gate
        /// cannot classify fails CLOSED, never open.
        /// </summary>
        public static FixerLoadKind LoadKindFromChoice(string choiceId)
        {
            switch ((choiceId ?? "").Trim().ToLowerInvariant())
            {
                case LoadDummy: return FixerLoadKind.DummyLoad;
                case LoadAntenna: return FixerLoadKind.Antenna;
                case LoadAmplifier: return FixerLoadKind.Amplifier;
                default: return FixerLoadKind.NothingOrUnsure;
            }
        }

        // The hearing affirmation (#243) — asked INSIDE stage 0, because the
        // operator is an instrument and this is where the reading is taken.
        // Travels as its own wire kind, recorded by the host, and fed back
        // into stage 0's facts for the next run of the stage.
        public const string HearingDeclaration = "radio-hearing";
        public const string HearingYes = "hears";
        public const string HearingNo = "hears-nothing";
        public const string HearingNoRadio = "no-radio";

        /// <summary>Map a hearing choice id from the wire to the fact it
        /// asserts. An unknown id reads as not-asked, never as a guess.</summary>
        public static HeardRadio HearingFromChoice(string choiceId)
        {
            switch ((choiceId ?? "").Trim().ToLowerInvariant())
            {
                case HearingYes: return HeardRadio.Hears;
                case HearingNo: return HeardRadio.HearsNothing;
                case HearingNoRadio: return HeardRadio.NoRadio;
                default: return HeardRadio.NotAsked;
            }
        }

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

            /// <summary>
            /// The live facts a stage's "what Run will do" sentence carries —
            /// tune power, RF power, the TX antenna port, remoteness. Read at
            /// RENDER time, because the page re-renders on every action and
            /// these move under it. The tool already read every one of these
            /// for its own record and showed none of them (#250); this is the
            /// road they take to the operator. Null, or a null return, and
            /// the sentences simply omit the live half.
            /// </summary>
            public Func<StationNow> ReadStation;

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

        /// <summary>
        /// The station as it stands right now, for the sentences that tell an
        /// operator what pressing Run will actually do.
        /// </summary>
        public sealed class StationNow
        {
            /// <summary>Tune power in watts, or -1 when it could not be read.</summary>
            public int TunePowerWatts { get; set; } = -1;

            /// <summary>RF power in watts, or -1 when it could not be read.</summary>
            public int RfPowerWatts { get; set; } = -1;

            /// <summary>The TX antenna port ("ANT1"), or empty when not known.</summary>
            public string AntennaPort { get; set; } = "";

            /// <summary>True when this is a remote session — the operator is
            /// not in the room with the antenna socket (#247).</summary>
            public bool RemoteRadio { get; set; }
        }

        /// <summary>Build the set around the host's delegates.</summary>
        public static FixerStageSet Build(Hosts hosts)
        {
            hosts = hosts ?? new Hosts();

            // Live station facts, guarded once here: a throw or a missing
            // delegate reads as "nothing known" and the sentences carry on
            // without the live half.
            StationNow Station()
            {
                try { return hosts.ReadStation?.Invoke(); }
                catch { return null; }
            }

            // " at 25 watts into ANT1", or as much of it as is actually known.
            // Assembled once so the transmitting stages cannot drift apart in
            // how they say it.
            static string AtInto(int watts, string port)
            {
                string s = "";
                if (watts >= 0)
                    s += " at " + watts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                       + (watts == 1 ? " watt" : " watts");
                if (!string.IsNullOrWhiteSpace(port)) s += " into " + port.Trim();
                return s;
            }

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
                    DescribeRunAction = () =>
                        "Running this takes a quick reading of the audio path. "
                        + "Nothing transmits.",
                    // The operator as an instrument (#243). Over a remote
                    // link, "I can hear the radio" settles in one press what
                    // no probe in this stage can: PC audio is on, the
                    // transport is carrying audio, the output device works,
                    // and the decode path works.
                    Declarations = new[]
                    {
                        new FixerRunDeclaration(
                            HearingDeclaration,
                            "Can you hear the radio right now?",
                            "You are the best instrument in the room for this question. "
                            + "Hearing the radio proves the whole receive path in one "
                            + "stroke, which narrows where a transmit problem can be.",
                            new[]
                            {
                                new FixerDeclarationChoice(HearingYes,
                                    "I can hear the radio"),
                                new FixerDeclarationChoice(HearingNo,
                                    "I hear nothing from the radio"),
                                new FixerDeclarationChoice(HearingNoRadio,
                                    "No radio is connected"),
                            },
                            messageKind: "declare-hearing"),
                    },
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
                    // Off the UI thread (Sprint 35 ruling): this stage keys
                    // nothing, and it froze the page for its whole listen —
                    // Noel's first live run read as a hang (#255).
                    OffUiThread = true,
                    DescribeRunAction = () =>
                        "Running this measures your room's noise in a quiet moment, then "
                        + "counts you in with three tones and listens while you talk. "
                        + "Nothing transmits.",
                    SkipChoices = new[] { operatorSkip, remoteSkip, noMicSkip },
                    // Stage 0's facts ride along (#241): stage 1's advice must
                    // not send the operator to check a Windows mute that stage
                    // 0 has already measured and reported one card up.
                    Execute = hosts.MeasureMicrophone == null ? (Func<FixerStageContext, FixerOutcome>)null
                        : ctx => TransmitStages.Microphone(
                            hosts.MeasureMicrophone(),
                            ctx.ResultFor(AudioSetup)?.Payload as AudioSetupFacts),
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
                    // "This will key your radio for two seconds at 25 watts
                    // into ANT1" — both live facts were already read for the
                    // record and shown to nobody (#250). The duration derives
                    // from the probe's own constant so it cannot drift.
                    // The count-in is part of what pressing Run does (#255), so
                    // the sentence says so — stage 1's already did, and a cue
                    // the operator was not told about reads as a malfunction.
                    DescribeRunAction = () =>
                    {
                        StationNow s = Station();
                        return "Running this counts down with three tones, then keys "
                             + "the radio's own tune carrier"
                             + AtInto(s?.TunePowerWatts ?? -1, s?.AntennaPort ?? "")
                             + " for about " + SecondsPhrase(TxTuneProbe.TuneMs) + ".";
                    },
                    // The other half of #250: once the stage names the power
                    // it will use, the next thing an operator wants is to
                    // change it — and the Fixer is modal, so "go to the main
                    // window" used to cost the whole run. The page hands off
                    // to the host's own power surface rather than growing a
                    // number box of its own, the same rule as the device
                    // picker: one home for power, and it is not here.
                    HostActions = new[]
                    {
                        new FixerHostAction("open-power-dialog",
                                            "Change the tune power"),
                    },
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
                    DescribeRunAction = () =>
                    {
                        StationNow s = Station();
                        return "Running this counts down with three tones, then keys "
                             + "the transmitter"
                             + AtInto(s?.RfPowerWatts ?? -1, s?.AntennaPort ?? "")
                             + " for several seconds and sends tones and a recorded voice "
                             + "through it. Your microphone stays out of the path.";
                    },
                    // Same hand-off as stage 2 (#250); this stage transmits at
                    // the RF power, so that is the number an operator here
                    // wants to move.
                    HostActions = new[]
                    {
                        new FixerHostAction("open-power-dialog",
                                            "Change the transmit power"),
                    },
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
                    // "Counts you in", not "counts down" — this is the one
                    // keying stage where the operator performs, and the count
                    // is their cue as well as the RF warning.
                    DescribeRunAction = () =>
                    {
                        StationNow s = Station();
                        return "Running this counts you in with three tones, then keys "
                             + "the transmitter"
                             + AtInto(s?.RfPowerWatts ?? -1, s?.AntennaPort ?? "")
                             + " for about " + SecondsPhrase(TxAudioProbe.SpokenListenMs)
                             + " while you speak into your microphone.";
                    },
                    // Same hand-off as stage 2 (#250).
                    HostActions = new[]
                    {
                        new FixerHostAction("open-power-dialog",
                                            "Change the transmit power"),
                    },
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
                        "Nothing transmits until you answer this question. Into a real "
                        + "antenna, or through an amplifier, the checks that transmit "
                        + "keep the power at "
                        + FixerTransmitGate.LowPowerCeilingWatts
                          .ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " watts or less. Answering that nothing is connected, or that "
                        + "you are not sure, keeps them parked — everything else still "
                        + "runs.",
                        new[]
                        {
                            new FixerDeclarationChoice(LoadDummy, "A dummy load"),
                            new FixerDeclarationChoice(LoadAntenna,
                                "An antenna, and transmitting a short low-power test "
                                + "into it is fine"),
                            // Not a nicety: an amplifier in the path means every
                            // reading describes the amplifier's INPUT (#223), and
                            // a measurement labelled "antenna" through one is
                            // wrong in the report Flex eventually reads.
                            new FixerDeclarationChoice(LoadAmplifier,
                                "An amplifier — the radio feeds it before anything "
                                + "reaches an antenna or a load"),
                            new FixerDeclarationChoice(LoadNothingUnsure,
                                "Nothing, or I am not sure"),
                        })
                    {
                        // The question names the PORT from the radio rather
                        // than asking the operator to know it (#244) — which
                        // also surfaces a wrong TX antenna before it costs
                        // anything (#205's failure mode). And for a remote
                        // radio it says out loud that it is asking about a
                        // station the operator is not at (#247): naming the
                        // distance is what makes a person stop and think.
                        QuestionNow = () =>
                        {
                            StationNow s = Station();
                            string port = s?.AntennaPort ?? "";
                            bool remote = s?.RemoteRadio == true;

                            if (remote)
                                return port.Length > 0
                                    ? "You are connected remotely. The radio will transmit "
                                      + "on " + port + " — what is connected to " + port
                                      + " at that station right now?"
                                    : "You are connected remotely. What is connected to "
                                      + "the antenna port at that station right now?";

                            return port.Length > 0
                                ? "The radio will transmit on " + port
                                  + ". What is connected to " + port + " right now?"
                                : "";   // fall back to the static question
                        },
                    },
                });
        }

        private static void AddFix(Dictionary<string, FixerFixAction> fixes, string id,
                                   FixerFixAction action)
        {
            if (action != null) fixes[id] = action;
        }

        /// <summary>
        /// "two seconds", derived from the probe constant it describes so the
        /// sentence cannot drift from the behaviour. Words for the small
        /// counts a person says as words; numerals past twelve.
        /// </summary>
        private static string SecondsPhrase(int ms)
        {
            string[] small = { "zero", "one", "two", "three", "four", "five", "six",
                               "seven", "eight", "nine", "ten", "eleven", "twelve" };
            int whole = ms / 1000;
            string n = ms % 1000 == 0 && whole < small.Length
                ? small[whole]
                : (ms / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            return n + (ms == 1000 ? " second" : " seconds");
        }
    }
}
