using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Radios.Fixer
{
    /// <summary>
    /// What the operator said about hearing the radio (#243). The operator is
    /// an instrument, and a very good one: over a remote link, "I can hear
    /// it" proves PC audio is on, the transport is carrying audio, the output
    /// device works and the decode path works — four facts no probe in this
    /// stage can establish as reliably as the person listening.
    /// </summary>
    public enum HeardRadio
    {
        /// <summary>The question has not been answered this run.</summary>
        NotAsked = 0,
        /// <summary>"I can hear the radio."</summary>
        Hears,
        /// <summary>"I hear nothing from the radio."</summary>
        HearsNothing,
        /// <summary>"No radio is connected."</summary>
        NoRadio,
    }

    /// <summary>
    /// What the audio setup is actually doing, as facts the host read from the
    /// audio subsystem itself — not from configuration. The two can differ, and
    /// where they do that IS the finding.
    /// </summary>
    /// <remarks>
    /// Unknowables are nullable on purpose: "could not be read" and "read as
    /// false" are different facts, and collapsing them turns an unread Windows
    /// privacy setting into a clean bill of health.
    /// </remarks>
    public sealed class AudioSetupFacts
    {
        // What is actually open right now. Empty string: nothing open / not read.
        public string OpenHostApi { get; set; } = "";
        public string OpenInputDevice { get; set; } = "";
        public string OpenOutputDevice { get; set; } = "";
        public double OpenSampleRateHz { get; set; }   // 0 = not known
        public int OpenChannels { get; set; }          // 0 = not known

        // What the configuration says it should be.
        public string ConfiguredHostApi { get; set; } = "";
        public string ConfiguredInputDevice { get; set; } = "";

        // The environment around them.
        public bool WasapiAvailable { get; set; }

        /// <summary>Is any input device chosen at all?</summary>
        public bool InputDeviceSelected { get; set; }

        /// <summary>The host's nomination for an input when none is chosen —
        /// typically the Windows default capture device. Empty when there is
        /// nothing to nominate, in which case "choose one" cannot be offered
        /// as a button and the finding belongs to the operator.</summary>
        public string SuggestedInputDevice { get; set; } = "";

        /// <summary>Is the computer-audio path to the radio on?</summary>
        public bool PcAudioOn { get; set; }

        /// <summary>Is this a remote radio? PC audio only carries transmit
        /// audio when it is — a LAN radio takes audio regardless, so the
        /// PC-audio-off finding must not fire there.</summary>
        public bool RemoteRadio { get; set; }

        /// <summary>Is the selected microphone profile empty?</summary>
        public bool MicProfileEmpty { get; set; }

        // Windows-side facts we can observe but not change. Null: not knowable.
        public bool? WindowsInputMuted { get; set; }
        public bool? MicrophonePrivacyBlocked { get; set; }
        public bool? InputDeviceUnplugged { get; set; }

        /// <summary>What the operator said about hearing the radio (#243) —
        /// a reading taken from the best receive-path instrument in the room,
        /// recorded by the host from the stage's own declaration.</summary>
        public HeardRadio OperatorHearsRadio { get; set; } = HeardRadio.NotAsked;
    }

    /// <summary>
    /// Stage 0's decisions: what the facts mean, who can fix each problem,
    /// and the answer in a person's voice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure — the host gathers <see cref="AudioSetupFacts"/> from what is
    /// actually open, this decides. Same split as everything in ChainChecks:
    /// the part that decides what an operator is told is the part with the
    /// tests over it.
    /// </para>
    /// <para>
    /// <b>This stage detects and offers; it is not a device picker.</b>
    /// AudioDevicesDialog owns choosing devices. Every fix offered here is a
    /// specific detected repair — "you are on MME, switch to WASAPI" — and an
    /// operator who wants the full picker gets it from the host.
    /// </para>
    /// </remarks>
    public static class AudioSetupCheck
    {
        /// <summary>The host-API name PortAudio reports for MME. Compared
        /// case-insensitively and by prefix, because the full string is
        /// "MME" today and nobody has promised it stays that way.</summary>
        public const string MmeApiName = "MME";

        // Finding ids, stable so the page's fix buttons and the fix records
        // refer to the same thing across renders.
        public const string MmeInUse = "mme-in-use";
        public const string NoInputSelected = "no-input-selected";
        public const string NoInputAnywhere = "no-input-anywhere";
        public const string PcAudioOff = "pc-audio-off";
        public const string MicProfileEmptyFinding = "mic-profile-empty";
        public const string WindowsMuted = "windows-muted";
        public const string PrivacyBlocked = "privacy-blocked";
        public const string Unplugged = "unplugged";
        public const string ConfigOpenMismatch = "config-open-mismatch";
        public const string HearsNothingFinding = "hears-nothing";

        /// <summary>
        /// The receive rule that fires on the same state as
        /// <see cref="PcAudioOff"/>. Named here, not matched on prose, so an
        /// operator's own edit to the rule's WORDS cannot quietly reintroduce
        /// the duplicate. Renaming the rule itself would — which is the honest
        /// cost of rules being data, and it fails towards saying a true thing
        /// twice rather than towards saying nothing.
        /// </summary>
        public const string RxPcAudioOffRule = "rx-pc-audio-off-on-remote";

        // Fix action ids the transmit set binds host delegates to.
        public const string FixSwitchToWasapi = "switch-to-wasapi";
        public const string FixUseSuggestedInput = "use-suggested-input";
        public const string FixEnablePcAudio = "enable-pc-audio";
        public const string FixFillMicProfile = "fill-mic-profile";
        public const string FixReopenConfiguredAudio = "reopen-configured-audio";

        /// <summary>Decide what the facts mean.</summary>
        /// <param name="facts">What the audio system is actually doing.</param>
        /// <param name="receive">
        /// The receive walk, run through the same rules and phrased by the same
        /// code the Audio Workshop's receive door uses (#367). Null when the
        /// host wired nothing, and the stage then reports only the computer's
        /// half — honestly, and without inventing a receive answer.
        /// </param>
        /// <remarks>
        /// <b>The receive check is folded in HERE, not run as a stage of its
        /// own.</b> Noel's ruling, 2026-08-28: "as stage 0, it does rx audio as
        /// well, but you can just go to the other submenu option if you just
        /// wanted to test rx audio." One definition, two doors — so a rule
        /// added to <c>rx-chain-rules.txt</c> reaches the Fixer's report and
        /// the Workshop's button with no second edit. A second stage set would
        /// have been two homes for one idea, where one silently falls behind.
        /// </remarks>
        public static FixerOutcome Analyze(AudioSetupFacts facts,
                                           Radios.ChainChecks.ReceiveCheckResult receive = null)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));

            var findings = new List<FixerFinding>();

            // MME. Not merely worse-sounding: it MISREPORTS the device,
            // returning converted sample rates rather than what the hardware
            // runs, so a microphone measurement through it measures Windows'
            // resampler. It is also PortAudio's default nomination, so it is
            // what an operator gets by doing nothing (#61).
            //
            // MME IN USE IS A CAVEAT ON THE NUMBERS, NOT A FAULT (#239). It
            // records fine; nothing anywhere gates a stage on it, and an
            // MME-only setup runs all five stages with the caveat attached.
            // The finding stays FixOwner.Us only where WASAPI exists to move
            // to, and the fix is atomic around the microphone — an operator
            // whose only microphone is MME-only is never told to replace
            // working hardware.
            //
            // DIRECTSOUND'S STATUS, decided rather than left implicit (#239):
            // it is NOT offered as a target and is NOT treated as better than
            // MME. Devices.cs groups the two together — "MME and DirectSound
            // convert silently; WASAPI and WDM-KS do not" — so for the one
            // thing this stage cares about, measurement honesty, DirectSound
            // buys nothing: the same converter sits behind it wearing a
            // different name. A microphone with DirectSound but no WASAPI
            // gets the same answer as an MME-only one — keep using it, read
            // the levels as approximate.
            if (IsMme(facts.OpenHostApi))
            {
                if (facts.WasapiAvailable)
                {
                    findings.Add(new FixerFinding(MmeInUse, FixOwner.Us,
                        "Currently, you have selected the MME audio subsystem. It records "
                        + "perfectly well, but it will not tell you the truth about your "
                        + "hardware: Windows resamples behind it and reports its own "
                        + "converted format back, so the 44.1 kHz shown above may be "
                        + "48 kHz at the device itself. Every level measured in this run "
                        + "would belong to that converter rather than to your microphone.",
                        "Switch to WASAPI",
                        FixSwitchToWasapi));
                }
                else
                {
                    findings.Add(new FixerFinding(MmeInUse, FixOwner.NobodyHere,
                        "Currently, you have selected the MME audio subsystem, and this "
                        + "computer offers no WASAPI to move to. Recording works normally; "
                        + "the format MME reports simply does not have to match what the "
                        + "hardware is really doing.",
                        "Nothing here can change that. Read every level in this run as "
                        + "approximate — they describe Windows' resampling as much as your "
                        + "microphone."));
                }
            }

            // No microphone chosen.
            if (!facts.InputDeviceSelected)
            {
                if (facts.SuggestedInputDevice.Length > 0)
                {
                    findings.Add(new FixerFinding(NoInputSelected, FixOwner.Us,
                        "You have not selected an input device, so nothing you say can "
                        + "reach the radio.",
                        "Use " + facts.SuggestedInputDevice,
                        FixUseSuggestedInput));
                }
                else
                {
                    findings.Add(new FixerFinding(NoInputAnywhere, FixOwner.Operator,
                        "You have not selected an input device, and Windows is not "
                        + "offering one to choose.",
                        "Plug a microphone in, then run this stage again."));
                }
            }

            // The computer-audio path, remote radios only — on a LAN radio the
            // transmit audio does not ride this switch, and a finding there
            // would send the operator to fix something that is not in the path.
            if (facts.RemoteRadio && !facts.PcAudioOn)
            {
                findings.Add(new FixerFinding(PcAudioOff, FixOwner.Us,
                    "PC audio is currently switched off, so nothing at all leaves this "
                    + "computer for the radio — not your microphone, and not the test "
                    + "tone either.",
                    "Turn PC audio on",
                    FixEnablePcAudio));
            }

            if (facts.MicProfileEmpty)
            {
                findings.Add(new FixerFinding(MicProfileEmptyFinding, FixOwner.Us,
                    "No mic profile is loaded on the radio. It will key up and transmit "
                    + "silence. Receive is unaffected, and nothing you did caused this — a "
                    + "Flex arrives from the factory this way.",
                    "Load a working profile",
                    FixFillMicProfile));
            }

            // Windows-side facts: observed here, fixed there. One sentence,
            // no jargon, and only when actually observed — a null never
            // becomes a finding.
            if (facts.WindowsInputMuted == true)
                findings.Add(new FixerFinding(WindowsMuted, FixOwner.Operator,
                    "Windows itself has your microphone muted. This is not the radio and "
                    + "not this application: the mute is in Windows, and it has to be "
                    + "cleared there.",
                    "Unmute it in Sound settings, then run this stage again."));

            if (facts.MicrophonePrivacyBlocked == true)
                findings.Add(new FixerFinding(PrivacyBlocked, FixOwner.Operator,
                    "Windows privacy is blocking desktop apps from the microphone. The "
                    + "device is fine; Windows will not hand it over.",
                    "Settings, Privacy, Microphone — allow desktop apps, then run this "
                    + "stage again."));

            if (facts.InputDeviceUnplugged == true)
                findings.Add(new FixerFinding(Unplugged, FixOwner.Operator,
                    "The microphone you have selected is reporting itself as unplugged.",
                    "Check the cable and the connector, then run this stage again."));

            // The operator's own reading (#243). "I hear nothing" is a
            // finding-grade fact: over a remote link with PC audio ON, it
            // says the receive path is not delivering — and the transmit
            // stages ride part of the same road, so it should be settled
            // before their results are read. When PC audio is OFF on a
            // remote radio, silence is the EXPECTED consequence and the
            // pc-audio-off finding above already names the cause; a second
            // finding for the same cause is how a report starts feeling
            // long, so none is raised.
            if (facts.OperatorHearsRadio == HeardRadio.HearsNothing
                && !(facts.RemoteRadio && !facts.PcAudioOn))
            {
                findings.Add(facts.RemoteRadio
                    ? new FixerFinding(HearsNothingFinding, FixOwner.Operator,
                        "You hear nothing from the radio, even though PC audio is on — so "
                        + "the receive path is not delivering sound to your ears.",
                        "Check this computer's output device and its volume first. If the "
                        + "radio's audio genuinely is not arriving, the transmit tests "
                        + "will likely fail for the same reason, so settle this before "
                        + "reading them.")
                    : new FixerFinding(HearsNothingFinding, FixOwner.Operator,
                        "You hear nothing from the radio.",
                        "Check the volume on the radio and on this computer, and where "
                        + "your receive audio normally comes out. A silent receiver is "
                        + "worth settling before the transmit results are read."));
            }

            // Configuration and reality disagreeing is a finding in itself —
            // it is the exact reason this stage reads what is OPEN.
            string mismatch = DescribeMismatch(facts);
            if (mismatch.Length > 0)
            {
                findings.Add(new FixerFinding(ConfigOpenMismatch, FixOwner.Us,
                    mismatch,
                    "Reopen on the configured device",
                    FixReopenConfiguredAudio));
            }

            // THE RECEIVE WALK, in the rule file's own words (#367). Added
            // last so the computer's own half is read first: the evidence block
            // is a walk, and it starts at this end.
            AddReceiveFindings(findings, receive);

            return new FixerOutcome
            {
                Answer = Answer(facts) + ReceiveAnswer(receive),
                Findings = findings,
                Evidence = Evidence(facts) + ReceiveEvidence(receive),
                Payload = facts,
            };
        }

        /// <summary>
        /// Turn the receive walk's problems into findings, with the one
        /// exclusion that stops the same cause being reported twice.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every receive problem belongs to the OPERATOR or to nobody here.
        /// None of them gets a one-press button, and that is a decision rather
        /// than an omission: the remedies are a slice mute, output mutes and
        /// output levels — all of them choices about how somebody listens in
        /// their own room. Unmuting an output an operator muted on purpose has
        /// a consequence we would be making on their behalf, and picking a
        /// level for them is a judgement about a room we cannot hear. The
        /// Workshop is where a person adjusts audio deliberately; this stage
        /// says what it found and names the control.
        /// </para>
        /// <para>
        /// <b>The exclusion:</b> the receive rules' PC-audio rung and this
        /// stage's own <see cref="PcAudioOff"/> finding fire on exactly the same
        /// state, and this one carries a button that turns it on. Reporting
        /// both would put two entries and one action in front of an operator
        /// for one cause — "how a report starts feeling long", as the hearing
        /// finding above already puts it.
        /// </para>
        /// </remarks>
        private static void AddReceiveFindings(List<FixerFinding> findings,
                                               Radios.ChainChecks.ReceiveCheckResult receive)
        {
            if (receive == null) return;

            bool weAlreadyOfferPcAudio =
                findings.Exists(f => string.Equals(f.Id, PcAudioOff, StringComparison.OrdinalIgnoreCase));

            foreach (Radios.ChainChecks.ReceiveProblem p in receive.Problems)
            {
                if (weAlreadyOfferPcAudio
                    && string.Equals(p.Id, RxPcAudioOffRule, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Ids come from the rule file and cannot collide with this
                // stage's own, which are all written above; a duplicate would
                // still be refused rather than shadowing a fix button.
                if (findings.Exists(f => string.Equals(f.Id, p.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;

                bool nothingChecked = string.Equals(p.Id,
                    Radios.ChainChecks.ReceiveAudioCheck.NothingCheckedId,
                    StringComparison.OrdinalIgnoreCase);

                findings.Add(new FixerFinding(p.Id,
                    nothingChecked ? FixOwner.NobodyHere : FixOwner.Operator,
                    p.WhatIsWrong, p.WhatToDo));
            }
        }

        /// <summary>
        /// The receive half of the stage's spoken answer: what actually arrived
        /// from the radio, and — only when no finding already carries those
        /// words — the walk's own verdict.
        /// </summary>
        private static string ReceiveAnswer(Radios.ChainChecks.ReceiveCheckResult receive)
        {
            if (receive == null) return "";

            var sb = new StringBuilder();
            // The measurement first. It is the one fact in this stage that is
            // about the RADIO rather than about a switch we set, so it is the
            // half that survives a reader who distrusts our software (#350).
            // A BLANK LINE, not a space: this is the receive half of a stage
            // that has two, and both the page and the report render one
            // paragraph per block. Six sentences in a single run is readable
            // and unnavigable.
            if (receive.Arrival.Length != 0)
                sb.Append(Environment.NewLine).Append(Environment.NewLine).Append(receive.Arrival);
            string verdict = receive.VerdictNotCarriedByProblems;
            if (verdict.Length != 0)
            {
                sb.Append(sb.Length == 0
                    ? Environment.NewLine + Environment.NewLine : " ").Append(verdict);
            }
            return sb.ToString();
        }

        private static string ReceiveEvidence(Radios.ChainChecks.ReceiveCheckResult receive)
            => receive == null ? "" : Environment.NewLine + receive.Evidence;

        /// <summary>Is this host API name MME?</summary>
        public static bool IsMme(string hostApi)
            => (hostApi ?? "").TrimStart().StartsWith(MmeApiName,
                                                      StringComparison.OrdinalIgnoreCase);

        private static string DescribeMismatch(AudioSetupFacts f)
        {
            bool apiDiffers = f.ConfiguredHostApi.Length > 0 && f.OpenHostApi.Length > 0
                && !string.Equals(f.ConfiguredHostApi, f.OpenHostApi,
                                  StringComparison.OrdinalIgnoreCase);
            bool deviceDiffers = f.ConfiguredInputDevice.Length > 0 && f.OpenInputDevice.Length > 0
                && !string.Equals(f.ConfiguredInputDevice, f.OpenInputDevice,
                                  StringComparison.OrdinalIgnoreCase);

            if (!apiDiffers && !deviceDiffers) return "";

            var parts = new List<string>();
            if (deviceDiffers)
                parts.Add("you chose " + f.ConfiguredInputDevice
                        + ", but " + f.OpenInputDevice + " is what is open");
            if (apiDiffers)
                // Full phrase on first mention, short form second: naming the
                // category twice in one sentence reads like a form letter.
                parts.Add("you chose " + HostApiPhrase(f.ConfiguredHostApi)
                        + ", but the stream is actually running on " + f.OpenHostApi);

            return "Your settings and the open stream disagree — "
                 + string.Join("; and ", parts)
                 + ". Something overrode your choice, most often a device that "
                 + "disappeared and came back on a different index.";
        }

        /// <summary>
        /// A host API named the way an operator would say it out loud: "the MME
        /// audio subsystem", not a bare "MME".
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>One home for the phrasing, for the same reason the S-meter has
        /// one.</b> Noel had to give this correction twice on 2026-08-25,
        /// because the first pass fixed the finding and left the summary line
        /// saying "on MME" — the same defect, in the next paragraph.
        /// </para>
        /// <para>
        /// The bare acronym assumes the reader already knows what kind of thing
        /// MME is. Naming the category costs three words and assumes nothing,
        /// without explaining anything down to anyone. Use this on FIRST
        /// mention in a passage; the short form is fine afterwards, which is
        /// how a person writes.
        /// </para>
        /// <para>
        /// <b>"Audio subsystem" — Noel's word, chosen 2026-08-25.</b> Strictly
        /// these are APIs: MME is MultiMedia Extensions, WASAPI is the Windows
        /// Audio Session API, and both are interfaces INTO the Windows audio
        /// stack rather than subsystems of it. PortAudio calls them host APIs,
        /// which is why the fact names here are OpenHostApi and
        /// ConfiguredHostApi. The code keeps the accurate term; the operator
        /// gets the one Noel picked.
        /// </para>
        /// <para>
        /// <b>OUTSTANDING: AudioDevicesDialog still says "audio system".</b>
        /// That picker is where this stage's fix button sends the operator, and
        /// it has said "No audio system was found on this computer", "Each row
        /// names its own audio system", "choose MME as the audio system above"
        /// since Track E. Two words for one thing is the drift this project
        /// keeps paying for, so the picker should be brought onto "subsystem"
        /// too — flagged to Noel, awaiting his call, NOT changed unilaterally
        /// because that dialog's wording is his as well.
        /// </para>
        /// </remarks>
        internal static string HostApiPhrase(string hostApi)
        {
            if (string.IsNullOrWhiteSpace(hostApi)) return "an unnamed audio subsystem";
            return "the " + hostApi.Trim() + " audio subsystem";
        }

        private static string Answer(AudioSetupFacts f)
        {
            if (f.OpenInputDevice.Length == 0 && f.OpenHostApi.Length == 0)
                return "No stream is open. Nothing below was measured — it is all read "
                     + "back from your settings, which is exactly the thing this stage "
                     + "exists to distrust.";

            // A SENTENCE, not a label. This read "In: <device> on <api>, 44.1 kHz,
            // 2 channels. Out: <device>." until 2026-08-25, and Noel had it
            // exactly: "reads like an ingredient list." A spec line is fine on a
            // datasheet, where the reader's eye can jump between columns. Read
            // aloud, or read as the first thing a stage says to you, it is a
            // string of nouns with no grammar holding them together.
            //
            // The labelled form still exists and is better for scanning — it is
            // in Evidence, below, one fact per line. That belongs behind a
            // disclosure on the page rather than as the stage's opening words.
            // The device name is QUOTED because it is an opaque label from
            // Windows, not a phrase with grammar in it. Noel read
            // "Microphone (USB Audio Device)" and suggested "microphone, a USB
            // audio device" — which is how Windows means it, and is exactly
            // why re-punctuating is wrong: in "Line In (Realtek(R) Audio)" the
            // parenthesis is a real product, and in "Microphone (USB Audio
            // Device)" it is the placeholder Windows uses when the hardware
            // supplied no product string. Quoting says "this is a name" and
            // invents no structure. See task #240.
            var sb = new StringBuilder("You are recording from ");
            sb.Append(f.OpenInputDevice.Length > 0
                      ? "\"" + f.OpenInputDevice + "\""
                      : "an unnamed device");
            if (f.OpenHostApi.Length > 0) sb.Append(" using ").Append(HostApiPhrase(f.OpenHostApi));
            if (f.OpenSampleRateHz > 0)
                sb.Append(", at ")
                  .Append((f.OpenSampleRateHz / 1000.0).ToString("0.#", CultureInfo.InvariantCulture))
                  .Append(" kHz");
            if (f.OpenChannels == 1) sb.Append(" in mono");
            else if (f.OpenChannels == 2) sb.Append(" in stereo");
            else if (f.OpenChannels > 2)
                sb.Append(" across ").Append(f.OpenChannels).Append(" channels");
            sb.Append('.');
            if (f.OpenOutputDevice.Length > 0)
                sb.Append(" Playback is going to ").Append(f.OpenOutputDevice).Append('.');
            sb.Append(Hearing(f));
            return sb.ToString();
        }

        /// <summary>
        /// The operator's own reading, folded into the stage's answer (#243).
        /// Claims exactly as much as the answer proves: over a remote link,
        /// hearing the radio proves the whole receive path in one stroke; in
        /// the room, it may be the radio's own speaker and proves less about
        /// this computer.
        /// </summary>
        private static string Hearing(AudioSetupFacts f)
        {
            switch (f.OperatorHearsRadio)
            {
                case HeardRadio.Hears when f.RemoteRadio:
                    return " You can hear the radio, and over a remote connection that one "
                         + "fact proves the whole receive path at a stroke — the link is "
                         + "up, audio is flowing, and your output device is playing it. A "
                         + "silent transmit now points at the microphone side rather than "
                         + "at the connection.";
                case HeardRadio.Hears:
                    return " You can hear the radio. With the radio in the room that may "
                         + "be its own speaker rather than this computer, so it says less "
                         + "about the computer's audio path than it would over a remote "
                         + "connection.";
                case HeardRadio.NoRadio:
                    return " You said no radio is connected, so the tests that need one "
                         + "will wait until it is.";
                default:
                    return "";
            }
        }

        private static string Evidence(AudioSetupFacts f)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Audio setup, read from what is actually open");
            sb.AppendLine("--------------------------------------------");
            sb.AppendLine("Open host API: " + ValueOrNot(f.OpenHostApi));
            sb.AppendLine("Open input device: " + ValueOrNot(f.OpenInputDevice));
            sb.AppendLine("Open output device: " + ValueOrNot(f.OpenOutputDevice));
            sb.AppendLine("Open sample rate: " + (f.OpenSampleRateHz > 0
                ? f.OpenSampleRateHz.ToString("0", CultureInfo.InvariantCulture) + " Hz"
                : "not reported"));
            sb.AppendLine("Open channels: " + (f.OpenChannels > 0
                ? f.OpenChannels.ToString(CultureInfo.InvariantCulture) : "not reported"));
            sb.AppendLine("Configured host API: " + ValueOrNot(f.ConfiguredHostApi));
            sb.AppendLine("Configured input device: " + ValueOrNot(f.ConfiguredInputDevice));
            sb.AppendLine("WASAPI available: " + (f.WasapiAvailable ? "yes" : "no"));
            sb.AppendLine("PC audio: " + (f.PcAudioOn ? "on" : "off")
                + (f.RemoteRadio ? " (remote radio)" : " (local radio)"));
            sb.AppendLine("Microphone profile: " + (f.MicProfileEmpty ? "empty" : "has settings"));
            sb.AppendLine("Muted in Windows: " + Tristate(f.WindowsInputMuted));
            sb.AppendLine("Blocked by Windows privacy: " + Tristate(f.MicrophonePrivacyBlocked));
            sb.AppendLine("Device reports unplugged: " + Tristate(f.InputDeviceUnplugged));
            sb.AppendLine("Operator hears the radio: " + HearingEvidence(f.OperatorHearsRadio));
            return sb.ToString();
        }

        private static string ValueOrNot(string v)
            => string.IsNullOrEmpty(v) ? "none" : v;

        private static string Tristate(bool? v)
            => v == null ? "could not be read" : (v.Value ? "yes" : "no");

        private static string HearingEvidence(HeardRadio h)
        {
            switch (h)
            {
                case HeardRadio.Hears: return "yes, by their own account";
                case HeardRadio.HearsNothing: return "no — they hear nothing";
                case HeardRadio.NoRadio: return "no radio connected, by their own account";
                default: return "not asked, or not answered";
            }
        }
    }
}
