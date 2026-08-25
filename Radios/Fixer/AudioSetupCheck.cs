using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Radios.Fixer
{
    /// <summary>
    /// What the audio setup is actually doing, as facts the host read from the
    /// audio system itself — not from configuration. The two can differ, and
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

        // Fix action ids the transmit set binds host delegates to.
        public const string FixSwitchToWasapi = "switch-to-wasapi";
        public const string FixUseSuggestedInput = "use-suggested-input";
        public const string FixEnablePcAudio = "enable-pc-audio";
        public const string FixFillMicProfile = "fill-mic-profile";
        public const string FixReopenConfiguredAudio = "reopen-configured-audio";

        /// <summary>Decide what the facts mean.</summary>
        public static FixerOutcome Analyze(AudioSetupFacts facts)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));

            var findings = new List<FixerFinding>();

            // MME. Not merely worse-sounding: it MISREPORTS the device,
            // returning converted sample rates rather than what the hardware
            // runs, so a microphone measurement through it measures Windows'
            // resampler. It is also PortAudio's default nomination, so it is
            // what an operator gets by doing nothing (#61).
            if (IsMme(facts.OpenHostApi))
            {
                if (facts.WasapiAvailable)
                {
                    findings.Add(new FixerFinding(MmeInUse, FixOwner.Us,
                        "Your audio is running through MME, which misreports the device — it "
                        + "hands over Windows' converted audio rather than what the hardware "
                        + "actually runs, so any microphone measurement taken through it "
                        + "measures the converter, not your microphone.",
                        "Switch to WASAPI",
                        FixSwitchToWasapi));
                }
                else
                {
                    findings.Add(new FixerFinding(MmeInUse, FixOwner.NobodyHere,
                        "Your audio is running through MME, which misreports the device, and "
                        + "WASAPI is not available on this computer to switch to.",
                        "Nothing here can change that. Treat the audio measurements in this "
                        + "run with caution — they describe Windows' conversion as much as "
                        + "your hardware."));
                }
            }

            // No microphone chosen.
            if (!facts.InputDeviceSelected)
            {
                if (facts.SuggestedInputDevice.Length > 0)
                {
                    findings.Add(new FixerFinding(NoInputSelected, FixOwner.Us,
                        "No microphone is chosen, so nothing you say can arrive.",
                        "Use " + facts.SuggestedInputDevice,
                        FixUseSuggestedInput));
                }
                else
                {
                    findings.Add(new FixerFinding(NoInputAnywhere, FixOwner.Operator,
                        "No microphone is chosen, and none was found on this computer to offer.",
                        "Connect a microphone, then run this stage again."));
                }
            }

            // The computer-audio path, remote radios only — on a LAN radio the
            // transmit audio does not ride this switch, and a finding there
            // would send the operator to fix something that is not in the path.
            if (facts.RemoteRadio && !facts.PcAudioOn)
            {
                findings.Add(new FixerFinding(PcAudioOff, FixOwner.Us,
                    "PC audio is off, so no audio from this computer reaches the radio over "
                    + "the network.",
                    "Turn PC audio on",
                    FixEnablePcAudio));
            }

            if (facts.MicProfileEmpty)
            {
                findings.Add(new FixerFinding(MicProfileEmptyFinding, FixOwner.Us,
                    "The selected microphone profile is empty, so nothing is set up to carry "
                    + "your voice.",
                    "Fill in the profile with working defaults",
                    FixFillMicProfile));
            }

            // Windows-side facts: observed here, fixed there. One sentence,
            // no jargon, and only when actually observed — a null never
            // becomes a finding.
            if (facts.WindowsInputMuted == true)
                findings.Add(new FixerFinding(WindowsMuted, FixOwner.Operator,
                    "Your microphone is muted in Windows itself.",
                    "Unmute it in the Windows sound settings, then run this stage again."));

            if (facts.MicrophonePrivacyBlocked == true)
                findings.Add(new FixerFinding(PrivacyBlocked, FixOwner.Operator,
                    "Windows privacy settings are blocking microphone access for apps.",
                    "In Windows Settings, under Privacy, allow desktop apps to use the "
                    + "microphone, then run this stage again."));

            if (facts.InputDeviceUnplugged == true)
                findings.Add(new FixerFinding(Unplugged, FixOwner.Operator,
                    "The chosen microphone reports as unplugged.",
                    "Check its cable and connector, then run this stage again."));

            // Configuration and reality disagreeing is a finding in itself —
            // it is the exact reason this stage reads what is OPEN.
            string mismatch = DescribeMismatch(facts);
            if (mismatch.Length > 0)
            {
                findings.Add(new FixerFinding(ConfigOpenMismatch, FixOwner.Us,
                    mismatch,
                    "Reopen audio with the configured device",
                    FixReopenConfiguredAudio));
            }

            return new FixerOutcome
            {
                Answer = Answer(facts),
                Findings = findings,
                Evidence = Evidence(facts),
                Payload = facts,
            };
        }

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
                parts.Add("the configuration says the input should be " + f.ConfiguredInputDevice
                        + ", but what is actually open is " + f.OpenInputDevice);
            if (apiDiffers)
                parts.Add("the configuration says " + f.ConfiguredHostApi
                        + ", but the audio is actually running on " + f.OpenHostApi);

            return "Your settings and your running audio disagree: "
                 + string.Join("; and ", parts)
                 + ". Whatever you set up is not what is in use right now.";
        }

        private static string Answer(AudioSetupFacts f)
        {
            if (f.OpenInputDevice.Length == 0 && f.OpenHostApi.Length == 0)
                return "Nothing is open right now — no audio device is actually running, so "
                     + "everything below was read from the configuration alone.";

            var sb = new StringBuilder("Your audio is actually running on ");
            sb.Append(f.OpenInputDevice.Length > 0 ? f.OpenInputDevice : "an unnamed input");
            if (f.OpenHostApi.Length > 0) sb.Append(", via ").Append(f.OpenHostApi);
            if (f.OpenSampleRateHz > 0)
                sb.Append(", at ")
                  .Append((f.OpenSampleRateHz / 1000.0).ToString("0.#", CultureInfo.InvariantCulture))
                  .Append(" kHz");
            if (f.OpenChannels > 0)
                sb.Append(f.OpenChannels == 1 ? ", mono" : ", " + f.OpenChannels + " channels");
            if (f.OpenOutputDevice.Length > 0)
                sb.Append(". Output goes to ").Append(f.OpenOutputDevice);
            sb.Append('.');
            return sb.ToString();
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
            return sb.ToString();
        }

        private static string ValueOrNot(string v)
            => string.IsNullOrEmpty(v) ? "none" : v;

        private static string Tristate(bool? v)
            => v == null ? "could not be read" : (v.Value ? "yes" : "no");
    }
}
