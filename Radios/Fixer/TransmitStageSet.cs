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

        /// <summary>
        /// The wire kind for the frequency hand-off (#399), and the label the
        /// operator reads. Stated once here because three stages carry it: three
        /// literals would be three chances for one of them to say something
        /// slightly different, and a button whose wording moves between stages
        /// of one run is a button an operator has to re-read every time.
        /// </summary>
        public const string OpenFrequency = "open-frequency";

        /// <summary>
        /// "Change the frequency" — a verb and a noun, like the power button
        /// beside it. Not "Frequency…", which names a thing and leaves the
        /// operator to guess what pressing it does.
        /// </summary>
        public const string ChangeFrequencyLabel = "Change the frequency";

        /// <summary>
        /// The wire kind for the mode hand-off (#411), stated once for the
        /// same reason <see cref="OpenFrequency"/> is: the stages that carry
        /// it and the parser that reads it must agree on one string.
        /// </summary>
        public const string OpenMode = "open-mode";

        /// <summary>"Change the mode" — the same verb-and-noun shape as the
        /// power and frequency buttons it sits beside.</summary>
        public const string ChangeModeLabel = "Change the mode";

        /// <summary>
        /// The modes the mode hand-off offers, and it is EXACTLY these four.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>RULED BY NOEL 2026-08-30:</b> <i>"I think it needs a mode
        /// control, not all 12 modes but the basics."</i> He chose the
        /// shortest list on purpose: these are the only modes with a real
        /// transmit-audio path, which is the thing this tool tests. CW and FM
        /// were offered and declined — CW has no transmit audio path at all
        /// (the same fact that makes <c>TxToneLadder.PlanForMode</c> refuse
        /// it), and a transmit-audio test in FM measures a limiter, not a
        /// voice chain.
        /// </para>
        /// <para>
        /// <b>Do not add a fifth.</b> And do not add "the current mode" as a
        /// fake entry when the radio is in something outside this list — the
        /// hand-off says what the radio is on and lets the operator change it
        /// or not.
        /// </para>
        /// </remarks>
        public static readonly IReadOnlyList<string> TransmitAudioModes =
            new[] { "LSB", "USB", "DIGU", "DIGL" };

        /// <summary>True when <paramref name="mode"/> is one of the four the
        /// hand-off offers. Case-insensitive, because the radio's own report
        /// is the usual argument.</summary>
        public static bool IsTransmitAudioMode(string mode)
        {
            string m = (mode ?? "").Trim();
            foreach (string offered in TransmitAudioModes)
                if (string.Equals(m, offered, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }


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
        /// The remote session's honest fourth answer (#247): "I have not
        /// confirmed what is connected." Its own id, distinct from
        /// <see cref="LoadNothingUnsure"/>, so a trace or report reader can
        /// tell "nothing is on the socket" from "nobody I asked has looked" —
        /// they read the same to the gate and differently to a person.
        /// </summary>
        public const string LoadRemoteNotConfirmed = "remote-not-confirmed";

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
                // Explicit, not left to the default: the fallback below is
                // for FAULTS — ids nobody wrote — and a known answer must
                // never depend on it for its meaning.
                case LoadRemoteNotConfirmed: return FixerLoadKind.NothingOrUnsure;
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

            /// <summary>
            /// The receive walk, for stage 0 (#367). Typically
            /// <c>() =&gt; ReceiveAudioCheck.Run(rig)</c> — the SAME call the
            /// Audio Workshop's receive door makes, so a rule added to
            /// <c>rx-chain-rules.txt</c> reaches both with no second edit.
            /// Null leaves stage 0 reporting only the computer's half, and
            /// inventing no receive answer.
            /// </summary>
            public Func<ReceiveCheckResult> ReadReceiveChain;
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

            /// <summary>
            /// Where the radio will transmit ("14.250000 MHz"), or empty when it
            /// could not be read (#399).
            /// </summary>
            /// <remarks>
            /// On a dummy load the frequency is irrelevant, which is why the
            /// whole tool was designed, built and bench-tested without it. Into
            /// a real antenna it is the first thing any operator settles before
            /// keying, and until this existed the stage sentence named the power
            /// and the port and said nothing about where.
            /// </remarks>
            public string Frequency { get; set; } = "";

            /// <summary>The transmit mode ("USB"), or empty when not known.
            /// Reported, never changed: whether an operator needs to change mode
            /// mid-run is a question Noel raised and did not settle, and it is
            /// not built on a guess.</summary>
            public string Mode { get; set; } = "";

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

            // The receive walk, guarded the same way: a throw or a missing
            // delegate leaves stage 0 with only the computer's half, which is
            // honest, rather than a receive answer nobody measured.
            ReceiveCheckResult Receive()
            {
                try { return hosts.ReadReceiveChain?.Invoke(); }
                catch { return null; }
            }

            // " at 25 watts into ANT1 on 14.250000 MHz in USB", or as much of it
            // as is actually known. Assembled once so the transmitting stages
            // cannot drift apart in how they say it.
            //
            // WHERE comes last and it is not decoration (#399). Power and port
            // describe how hard and through what; the frequency is the only one
            // of the three that says who else is affected, and it is the one an
            // operator changes before keying into a real antenna.
            static string AtInto(TransmitStageSet.StationNow s, bool tuneCarrier)
            {
                int watts = tuneCarrier ? (s?.TunePowerWatts ?? -1) : (s?.RfPowerWatts ?? -1);
                string port = s?.AntennaPort ?? "";
                string t = "";
                if (watts >= 0)
                    t += " at " + watts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                       + (watts == 1 ? " watt" : " watts");
                if (!string.IsNullOrWhiteSpace(port)) t += " into " + port.Trim();
                // The MODE is omitted for a tune carrier, and that is not
                // tidiness: a tune carrier is the radio's own unmodulated
                // signal, so the slice's mode takes no part in it and naming
                // one would imply it did. Where audio is on the air, the mode
                // decides what the measurement means and belongs in the
                // sentence.
                t += ChainChecks.StationConditions.OnInPhrase(
                        s?.Frequency ?? "", tuneCarrier ? "" : (s?.Mode ?? ""));
                return t;
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
                    // NAMED FOR BOTH HALVES since 2026-08-29 (#367). It reads
                    // this computer's audio path AND walks the receive chain,
                    // and a stage called "Audio setup" would have hidden half
                    // of what it now does — including the only measurement in
                    // the run that is about the radio rather than about a
                    // setting of ours.
                    Title = "Audio setup and receive",
                    Question = "What is your audio doing right now, and is sound reaching you "
                             + "from the radio?",
                    Explanation =
                        "Two halves, and they answer different questions. The first reads "
                        + "the open audio stream directly: host API, input and "
                        + "output device, sample rate, channel count. Not your saved "
                        + "settings — the stream itself. Those two disagree more often "
                        + "than anyone expects, and when they do, the disagreement is "
                        + "usually the fault. You would never spot it on a settings page. "
                        + "The second walks the receive chain — the slice you are listening "
                        + "to, the radio's outputs and their levels, how the audio reaches "
                        + "you, and how much has actually been arriving over the network. "
                        + "Proving receive first is what makes the transmit tests after it "
                        + "readable, and the receive evidence belongs in the report whether "
                        + "or not you came here about receive. "
                        + "Nothing here keys the radio.",
                    Transmits = false,
                    HelpTopic = "fixer/transmit/audio-setup",
                    DescribeRunAction = () =>
                        "Running this takes a quick reading of the audio path on this "
                        + "computer, then walks the receive chain and reports how much audio "
                        + "has been arriving from the radio. Nothing transmits.",
                    // The operator as an instrument (#243). Over a remote
                    // link, "I can hear the radio" settles in one press what
                    // no probe in this stage can: PC audio is on, the
                    // transport is carrying audio, the output device works,
                    // and the decode path works.
                    Declarations = new[]
                    {
                        // Noel's own words, supplied 2026-08-28 (#374) and
                        // used verbatim — one doubled "to" dropped, and
                        // "transmit" corrected to "receive" on his explicit
                        // confirmation ("yes that is what I meant by proving
                        // the receive path"). The line it replaced — "You are
                        // the best instrument in the room" — was about US
                        // being clever; his version tells the operator what
                        // their answer accomplishes and why the run
                        // continues.
                        new FixerRunDeclaration(
                            HearingDeclaration,
                            "Can you hear the radio right now?",
                            "You're the best person to help prove this test. If you can "
                            + "hear the radio, it proves that the whole receive path is "
                            + "working. By proving the receive path, we can move to the "
                            + "next tests which will help us zero in on what's going "
                            + "right and what's going wrong with your radio's "
                            + "transmission paths.",
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
                        : ctx => AudioSetupCheck.Analyze(hosts.ReadAudioSetup(), Receive()),
                },

                new FixerStage
                {
                    Id = MicrophoneCheck,
                    Number = 1,
                    Title = "Microphone test",
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
                    Title = "Transmitter test",
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
                             + AtInto(s, tuneCarrier: true)
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
                        // #399, and the same hand-off exactly: one home for
                        // frequency, and it is not here either.
                        //
                        // NO MODE HAND-OFF HERE, deliberately (#411). The tune
                        // carrier is the radio's own unmodulated signal — the
                        // slice's mode takes no part in it, this stage's
                        // sentence omits the mode for exactly that reason, and
                        // a "Change the mode" button on a stage whose sentence
                        // never carries one would be a control whose effect
                        // the operator cannot see. The audio stages offer it.
                        new FixerHostAction(OpenFrequency, ChangeFrequencyLabel),
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
                        + "meter is watched to see what arrives. This test and stage 4 "
                        + "differ in exactly one thing, which is whether your microphone "
                        + "is involved. If this one works and stage 4 does not, your "
                        + "microphone is the problem. If neither works, your microphone is "
                        + "not the problem, and the fault lies between this computer and "
                        + "the radio. "
                        + "While the radio is keyed, your whole transmit path is also "
                        + "walked from end to end — thirteen steps, from the microphone "
                        + "this computer is set to use through to power leaving the "
                        + "radio — and the first dead one is named. Three of those steps "
                        + "exist only during a transmission, so this is the one moment "
                        + "they can be read at all, and the reading is taken with audio "
                        + "of a known loudness rather than whatever you happened to say.",
                    Transmits = true,
                    HelpTopic = "fixer/transmit/injected-transmit",
                    DescribeRunAction = () =>
                    {
                        StationNow s = Station();
                        return "Running this counts down with three tones, then keys "
                             + "the transmitter"
                             + AtInto(s, tuneCarrier: false)
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
                        // #399. Into a real antenna, finding a clear spot is
                        // the first thing any operator does before keying, and
                        // until this existed the only way to do it from here
                        // was to abandon the run.
                        new FixerHostAction(OpenFrequency, ChangeFrequencyLabel),
                        // #411 — the same argument one step along: a transmit
                        // audio test run in the wrong sideband is a valid
                        // measurement of the wrong thing, and on a real
                        // antenna it is also a transmission somebody else
                        // hears. This stage puts audio on the air, so the
                        // mode decides what the measurement means.
                        new FixerHostAction(OpenMode, ChangeModeLabel),
                    },
                    SkipChoices = new[] { operatorSkip },
                    // The stated load travels with THIS stage's evidence too:
                    // it keys the transmitter, and a transmission whose load
                    // is unrecorded cannot be read afterwards (#247).
                    Execute = hosts.RunInjectedTransmit == null ? (Func<FixerStageContext, FixerOutcome>)null
                        : ctx => TransmitStages.Injected(hosts.RunInjectedTransmit(),
                                                         hosts.ReadLoadDeclaration?.Invoke()),
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
                        + "points somewhere quite specific. "
                        + "Your whole transmit path is walked here too, while you are "
                        + "speaking, so the two walks differ in the same one thing the "
                        + "two stages do: a step that dies here and lives in stage 3 "
                        + "points at your microphone.",
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
                             + AtInto(s, tuneCarrier: false)
                             + " for about " + SecondsPhrase(TxAudioProbe.SpokenListenMs)
                             + " while you speak into your microphone.";
                    },
                    // Same hand-off as stage 2 (#250).
                    HostActions = new[]
                    {
                        new FixerHostAction("open-power-dialog",
                                            "Change the transmit power"),
                        // #399. Into a real antenna, finding a clear spot is
                        // the first thing any operator does before keying, and
                        // until this existed the only way to do it from here
                        // was to abandon the run.
                        new FixerHostAction(OpenFrequency, ChangeFrequencyLabel),
                        // #411, exactly as on stage 3: this stage puts the
                        // operator's own voice on the air, and the sideband it
                        // goes out in is part of what the measurement means.
                        new FixerHostAction(OpenMode, ChangeModeLabel),
                    },
                    SkipChoices = new[] { operatorSkip, remoteSkip, noMicSkip },
                    // Keys the transmitter, so the stated load rides with the
                    // evidence here as well (#247).
                    Execute = hosts.RunSpokenTransmit == null ? (Func<FixerStageContext, FixerOutcome>)null
                        : ctx => TransmitStages.Spoken(
                            hosts.RunSpokenTransmit(),
                            ctx.ResultFor(MicrophoneCheck)?.Payload as MicCheckFacts,
                            hosts.ReadLoadDeclaration?.Invoke()),
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
                     + "against that measurement rather than on its own. Stage 0 also walks "
                     + "your receive chain, so the report carries receive evidence whether "
                     + "or not receive is what brought you here, and stages 3 and 4 walk "
                     + "your transmit path while the radio is keyed, which is the only "
                     + "moment several of its steps can be read at all. Jump around if "
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
                        + "antenna, or through an amplifier, the tests that transmit "
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

                        // Remotely, the ANSWERS change too (#247), because the
                        // honest answers change. "A dummy load" is a claim
                        // about a socket the operator can see; a remote
                        // operator can only claim what someone at the station
                        // told them, and a required question whose answers
                        // nobody can give honestly gets answered anyway — so
                        // the answers are phrased as the claim a remote
                        // operator CAN make. "Someone at the station", not
                        // "the station owner", on purpose: the owner can be a
                        // thousand miles from their own radio too, and the
                        // word that counts belongs to whoever has eyes on the
                        // socket. The honest escape — "I have not confirmed" —
                        // costs only the transmitting stages, exactly like
                        // "Nothing, or I am not sure" does in the room, so
                        // honesty is never punished into a lie. Labels lead
                        // with the load itself: a screen reader arrowing
                        // through four answers must hear the difference
                        // first, not a shared preamble.
                        ChoicesNow = () =>
                        {
                            StationNow s = Station();
                            if (s?.RemoteRadio != true) return null;   // in the room: the static answers stand

                            return new[]
                            {
                                new FixerDeclarationChoice(LoadDummy,
                                    "A dummy load — someone at the station has confirmed "
                                    + "it is connected"),
                                new FixerDeclarationChoice(LoadAntenna,
                                    "An antenna — someone at the station has confirmed "
                                    + "it, and a short low-power test into it is fine"),
                                new FixerDeclarationChoice(LoadAmplifier,
                                    "An amplifier the radio feeds — someone at the "
                                    + "station has confirmed it"),
                                new FixerDeclarationChoice(LoadRemoteNotConfirmed,
                                    "I have not confirmed what is connected"),
                            };
                        },

                        // And the why-text tells the remote operator, before
                        // they answer, that the answer is recorded with its
                        // provenance and that everything runs cool (#247). A
                        // person who knows their word is going in the record
                        // weighs it before giving it.
                        WhyItMattersNow = () =>
                        {
                            StationNow s = Station();
                            if (s?.RemoteRadio != true) return null;   // in the room: the static text stands

                            return "Nothing transmits until you answer this question. You "
                                 + "are not at that station, so every answer here states "
                                 + "what someone there has confirmed with you — the report "
                                 + "will say your answer came over a remote session, on "
                                 + "someone else's word. Whatever is connected, the tests "
                                 + "that transmit keep the power at "
                                 + FixerTransmitGate.LowPowerCeilingWatts
                                   .ToString(System.Globalization.CultureInfo.InvariantCulture)
                                 + " watts or less, because a confirmation relayed from a "
                                 + "distance is not the same as seeing the socket. Answering "
                                 + "that you have not confirmed keeps them parked — "
                                 + "everything else still runs.";
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
