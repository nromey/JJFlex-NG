#nullable enable
using System;

namespace Radios
{
    /// <summary>
    /// Every message an operator is allowed to silence — each one declared
    /// once, with the words that name it back to him written in the same
    /// expression as the key that identifies it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why key and label share an expression (task #267).</b> The
    /// obvious shape for a "what have I silenced" list is a second table
    /// mapping keys to labels. That table drifts: somebody adds a key at a call
    /// site, the table is a different file, and the Settings list quietly reads
    /// out an identifier — or worse, the wrong sentence. A description that no
    /// longer matches the thing it describes is this project's most common
    /// defect by a wide margin, and a second table is a machine for producing
    /// them. Here there is one table, and <see cref="Describe"/> is that same
    /// table read backwards: it hands the key back to the factory that built
    /// it, so the label expression exists exactly once either way.</para>
    ///
    /// <para><b>Version the key when the message changes.</b> A key ending
    /// <c>-v1</c> is a promise that the words behind it have not changed since
    /// the operator agreed to stop seeing them. Bump it when the contents
    /// change, or "don't show again" quietly becomes "never tell me about new
    /// things you do to my radio."</para>
    ///
    /// <para><b>Parameterised keys carry their subject.</b> Registration and
    /// firmware are per radio, so a serial goes in the key and the label says
    /// which radio it was. "The offer to register a radio" would be useless on
    /// a bench with three of them.</para>
    /// </remarks>
    public static class AdvisoryKeys
    {
        private const string SmartLinkSetupValue = "smartlink-setup";
        private const string NoPhysicalAccessCascadeValue = "no-physical-access-cascade-v1";
        private const string StillRunningAtExitValue = "still-running-at-exit-v1";
        private const string RegisterPrefix = "register|";
        private const string FirmwarePrefix = "firmware|";

        /// <summary>
        /// The suggestion that this computer has no SmartLink account on it.
        /// Per computer, not per radio.
        /// </summary>
        public static AdvisoryKey SmartLinkSetup => new(
            SmartLinkSetupValue,
            Lexicon.Get("settings.silenced.smartlink_setup"));

        /// <summary>
        /// The suggestion that a particular radio is not registered to the
        /// signed-in SmartLink account.
        /// </summary>
        public static AdvisoryKey RegisterRadio(string serial) => new(
            RegisterPrefix + serial,
            Lexicon.Get("settings.silenced.register", ("serial", serial)));

        /// <summary>
        /// The notice that a routine firmware release is available for one
        /// radio. Keyed by version as well as serial so the next release
        /// announces itself again on its own. Breaking releases never take a
        /// key at all — re-prompting is the point.
        /// </summary>
        public static AdvisoryKey FirmwareUpdate(string serial, string version) => new(
            FirmwarePrefix + serial + "|" + version,
            Lexicon.Get("settings.silenced.firmware", ("serial", serial), ("version", version)));

        /// <summary>
        /// The explanation shown before the no-physical-access bundle is turned
        /// on: remote power-on at connect, remote port changes, remote firmware
        /// updates. The receipt that says what changed is never suppressed —
        /// only this explanation is.
        /// </summary>
        public static AdvisoryKey NoPhysicalAccessCascade => new(
            NoPhysicalAccessCascadeValue,
            Lexicon.Get("settings.silenced.no_physical_access"));

        /// <summary>
        /// The prompt at exit that names what is still running and offers to
        /// turn it off.
        /// </summary>
        /// <remarks>
        /// Added in Sprint 36 and deliberately AFTER the way back existed.
        /// This is the message somebody silences in a hurry on the way out of
        /// the shack and wants back a month later, when a meter capture has
        /// been quietly filling the disk since. Adding its checkbox while the
        /// store was still one-way would have widened a door nobody could
        /// close.
        /// </remarks>
        public static AdvisoryKey StillRunningAtExit => new(
            StillRunningAtExitValue,
            Lexicon.Get("settings.silenced.still_running"));

        /// <summary>
        /// The words for a key that is already in the store — the same table
        /// read backwards.
        /// </summary>
        /// <remarks>
        /// Every branch delegates to the factory above it rather than repeating
        /// its wording, so there is still exactly one label expression per
        /// message. A key this version does not recognise is named as such
        /// rather than dropped: it is what an entry written by a newer build,
        /// or a retired advisory, or a hand-edited file looks like, and an
        /// operator who cannot see it cannot restore it.
        /// </remarks>
        public static string Describe(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;

            switch (key)
            {
                case SmartLinkSetupValue: return SmartLinkSetup.Label;
                case NoPhysicalAccessCascadeValue: return NoPhysicalAccessCascade.Label;
                case StillRunningAtExitValue: return StillRunningAtExit.Label;
            }

            if (key.StartsWith(RegisterPrefix, StringComparison.Ordinal))
            {
                string serial = key.Substring(RegisterPrefix.Length);
                if (serial.Length > 0) return RegisterRadio(serial).Label;
            }

            if (key.StartsWith(FirmwarePrefix, StringComparison.Ordinal))
            {
                string[] parts = key.Split('|');
                if (parts.Length == 3 && parts[1].Length > 0 && parts[2].Length > 0)
                    return FirmwareUpdate(parts[1], parts[2]).Label;
            }

            return Lexicon.Get("settings.silenced.unrecognised", ("key", key));
        }
    }
}
