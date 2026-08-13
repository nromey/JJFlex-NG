using System;
using System.Diagnostics;
using Microsoft.Win32;
using JJTrace;

namespace JJPortaudio
{
    /// <summary>
    /// Windows microphone privacy gating, read honestly and reported in words.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mic Track, 2026-08-12. Since Windows 10 1903 the microphone privacy
    /// settings apply to ordinary desktop programs, not just Store apps — and
    /// when they block us, they do NOT hand back a tidy "permission denied".
    /// Depending on which host API PortAudio used, the capture either fails at
    /// open with an opaque host error, or opens perfectly and delivers an
    /// endless run of exact zeroes. The second shape is the dangerous one: it
    /// is indistinguishable, by ear and by meter, from a dead microphone. An
    /// operator who has been told "no audio" goes looking at cables.
    /// </para>
    /// <para>
    /// So we read the switches ourselves and say which one is off. Everything
    /// here is a plain registry read of the CapabilityAccessManager consent
    /// store — no COM, no WinRT, nothing that needs a packaged identity, all of
    /// it safe from any thread.
    /// </para>
    /// <para>
    /// <b>Verified on the development machine, 2026-08-12</b>, rather than
    /// assumed from documentation. The shapes below are what the keys actually
    /// hold here:
    /// </para>
    /// <list type="bullet">
    /// <item>HKLM ConsentStore\microphone has a REG_SZ <c>Value</c> = "Allow" —
    /// the machine-wide "Microphone access" master switch.</item>
    /// <item>HKCU ConsentStore\microphone has the same <c>Value</c> — the
    /// per-user "Let apps access your microphone".</item>
    /// <item>HKCU ConsentStore\microphone\NonPackaged has the same
    /// <c>Value</c> — "Let desktop apps access your microphone". <b>This is the
    /// one that gates us</b>: jjflexible.exe is a non-packaged desktop app.</item>
    /// <item>HKCU ConsentStore\microphone\NonPackaged\&lt;exe path&gt; exists per
    /// program, with backslashes written as '#'. On this machine those subkeys
    /// carry usage timestamps (LastUsedTimeStart/Stop) and NO <c>Value</c> —
    /// Windows lists desktop apps there for the "recently used" display without
    /// giving each one its own toggle. So a missing per-app Value means
    /// "inherit", never "denied", and we read it only when it is present.</item>
    /// <item>HKLM Policies\Microsoft\Windows\AppPrivacy was absent — this
    /// machine is unmanaged. On a managed machine
    /// <c>LetAppsAccessMicrophone</c> = 2 force-denies and the Settings app
    /// cannot override it, which is worth saying out loud because sending
    /// someone to a page that cannot help them is its own dead end.</item>
    /// </list>
    /// </remarks>
    public static class MicrophonePrivacy
    {
        /// <summary>What Windows says about our access to the microphone.</summary>
        public enum Access
        {
            /// <summary>Nothing is blocking us. Silence means silence.</summary>
            Allowed = 0,
            /// <summary>Group policy force-denies microphone access. Settings cannot override it.</summary>
            BlockedByPolicy,
            /// <summary>The machine-wide or per-user master switch is off.</summary>
            BlockedForAllApps,
            /// <summary>"Let desktop apps access your microphone" is off. The usual culprit.</summary>
            BlockedForDesktopApps,
            /// <summary>This program specifically is denied.</summary>
            BlockedForThisApp,
            /// <summary>The consent store is not there to read (older Windows, or a locked-down hive).</summary>
            Unknown
        }

        private const string ConsentSubKey =
            @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        private const string ConsentSubKeyHklm =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        private const string PolicySubKey =
            @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";

        /// <summary>The Windows Settings page for microphone privacy.</summary>
        public const string SettingsUri = "ms-settings:privacy-microphone";

        /// <summary>
        /// True when this result means the operator has something to go and fix.
        /// <see cref="Access.Unknown"/> is deliberately NOT a block — we did not
        /// find out, and claiming a block we cannot see would send someone
        /// chasing a setting that is already correct.
        /// </summary>
        public static bool IsBlocked(Access access) =>
            access == Access.BlockedByPolicy
            || access == Access.BlockedForAllApps
            || access == Access.BlockedForDesktopApps
            || access == Access.BlockedForThisApp;

        /// <summary>
        /// Read the consent store and report whether Windows is blocking us.
        /// </summary>
        /// <param name="explanation">
        /// A sentence written to be spoken as-is, naming the switch that is off
        /// and where it lives. Empty when access is allowed.
        /// </param>
        /// <remarks>
        /// Checked outermost-first, because the outer switch is the one the
        /// operator has to turn on first: a per-app allow underneath a global
        /// deny changes nothing, and telling someone about the inner switch
        /// while the outer one is off wastes a trip to Settings.
        /// </remarks>
        public static Access Check(out string explanation)
        {
            explanation = "";
            try
            {
                // 1. Group policy. Nothing below matters if this says no, and
                //    the Settings page is powerless against it — say so.
                int? policy = ReadPolicyDword();
                if (policy == 2)
                {
                    explanation = "Microphone access is blocked by a policy set on this computer. "
                        + "The Windows privacy settings cannot override it — whoever manages this "
                        + "computer has to change the policy.";
                    return Access.BlockedByPolicy;
                }

                // 2. The machine-wide master switch.
                string machine = ReadValue(Registry.LocalMachine, ConsentSubKeyHklm);
                if (IsDeny(machine))
                {
                    explanation = "Microphone access is turned off for this whole computer in "
                        + "Windows privacy settings. Nothing on this computer can hear a microphone "
                        + "until that is turned back on.";
                    return Access.BlockedForAllApps;
                }

                // 3. The per-user master switch.
                string user = ReadValue(Registry.CurrentUser, ConsentSubKey);
                if (IsDeny(user))
                {
                    explanation = "Microphone access is turned off for your Windows account in "
                        + "Windows privacy settings.";
                    return Access.BlockedForAllApps;
                }

                // 4. The desktop-app switch — the one that actually gates us.
                string desktop = ReadValue(Registry.CurrentUser, ConsentSubKey + @"\NonPackaged");
                if (IsDeny(desktop))
                {
                    explanation = "Windows is blocking desktop programs from using the microphone. "
                        + "JJ Flexible is a desktop program, so it cannot hear yours. Turn on "
                        + "\"Let desktop apps access your microphone\" in Windows privacy settings.";
                    return Access.BlockedForDesktopApps;
                }

                // 5. This program specifically. Usually absent; read when present.
                string mine = ReadValue(Registry.CurrentUser,
                    ConsentSubKey + @"\NonPackaged\" + EncodeExePath(CurrentExePath()));
                if (IsDeny(mine))
                {
                    explanation = "Windows is blocking JJ Flexible specifically from using the "
                        + "microphone. Allow it in Windows privacy settings.";
                    return Access.BlockedForThisApp;
                }

                // Nothing readable anywhere: do not claim either answer.
                if (machine == null && user == null && desktop == null)
                {
                    Tracing.TraceLine("MicrophonePrivacy: consent store not readable; access unknown",
                        TraceLevel.Info);
                    return Access.Unknown;
                }

                return Access.Allowed;
            }
            catch (Exception ex)
            {
                // A registry read that throws tells us nothing about the
                // microphone, so it must not turn into a claim about it.
                Tracing.TraceLine("MicrophonePrivacy.Check failed: " + ex.Message, TraceLevel.Warning);
                return Access.Unknown;
            }
        }

        /// <summary>
        /// Open the Windows microphone privacy page.
        /// </summary>
        /// <param name="failure">Why it did not open, written to be spoken.</param>
        /// <returns>true when Windows accepted the request.</returns>
        public static bool OpenSettings(out string failure)
        {
            failure = "";
            try
            {
                Process.Start(new ProcessStartInfo(SettingsUri) { UseShellExecute = true });
                Tracing.TraceLine("MicrophonePrivacy: opened " + SettingsUri, TraceLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("MicrophonePrivacy: could not open " + SettingsUri + " — " + ex.Message,
                    TraceLevel.Error);
                failure = "Windows would not open the microphone privacy page. "
                    + "Open Windows Settings, then Privacy and security, then Microphone.";
                return false;
            }
        }

        // ------------------------------------------------------------ reading

        /// <summary>
        /// Read the REG_SZ "Value" from a consent key. Null when the key or the
        /// value is absent — which is not the same answer as "Deny" and must
        /// never be collapsed into one.
        /// </summary>
        private static string ReadValue(RegistryKey hive, string subKey)
        {
            try
            {
                using RegistryKey key = hive.OpenSubKey(subKey, writable: false);
                return key?.GetValue("Value") as string;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("MicrophonePrivacy: could not read " + subKey + " — " + ex.Message,
                    TraceLevel.Info);
                return null;
            }
        }

        private static int? ReadPolicyDword()
        {
            try
            {
                using RegistryKey key = Registry.LocalMachine.OpenSubKey(PolicySubKey, writable: false);
                object v = key?.GetValue("LetAppsAccessMicrophone");
                return (v is int i) ? i : (int?)null;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("MicrophonePrivacy: could not read AppPrivacy policy — " + ex.Message,
                    TraceLevel.Info);
                return null;
            }
        }

        private static bool IsDeny(string value) =>
            value != null && string.Equals(value.Trim(), "Deny", StringComparison.OrdinalIgnoreCase);

        private static string CurrentExePath()
        {
            try { return Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
            catch { return ""; }
        }

        /// <summary>
        /// Windows writes a desktop program's consent subkey as its full path
        /// with every backslash replaced by '#'. Confirmed by reading the live
        /// key: "C:#dev#JJFlex-NG#bin#...#jjflexible.exe".
        /// </summary>
        private static string EncodeExePath(string path) =>
            string.IsNullOrEmpty(path) ? "" : path.Replace('\\', '#');
    }
}
