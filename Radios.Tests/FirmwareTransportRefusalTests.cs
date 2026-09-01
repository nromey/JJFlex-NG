using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Firmware cannot travel over SmartLink, and the code has to be the thing
    /// that says so — not a disabled button.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Task #404.</b> Until 2026-09-01 the ONLY thing keeping a firmware
    /// update off a SmartLink connection was three lines of button enablement
    /// in <c>SettingsDialog.RadioSetup.cs</c>. Neither
    /// <c>PreflightFirmwareUpdate</c> nor <c>BeginFirmwareUpdate</c> contained
    /// any connection-path test at all.
    /// </para>
    /// <para>
    /// <b>Why UI state was not enough, and why this is a test rather than a
    /// comment.</b> <c>RefreshFirmwareStatus</c> samples the connection once,
    /// on tab setup, and is not re-run when the connection changes. Paint the
    /// Setup tab while local, reconnect over SmartLink, and three enabled
    /// buttons sit in front of a click path with nothing in it. The Sprint 42
    /// write-path audit put it plainly: "This is a UI-state protection standing
    /// in for a policy decision… <b>Nothing fails when it goes.</b>" A comment
    /// asking a future editor to keep the check has the same failure mode as
    /// the button did.
    /// </para>
    /// <para>
    /// <b>The refusal has to carry a reason, not just refuse.</b> A disabled
    /// button announces "unavailable" and no more; the explanation lived in a
    /// separate polite live region that never fires for an operator who tabs
    /// onto the button later in the session. So the preflight sets
    /// <c>BlockReason</c> — which its callers already speak — and the writer
    /// speaks for itself.
    /// </para>
    /// <para>
    /// Scoped deliberately to the TRANSPORT fact. Whether a firmware update
    /// may proceed with nobody at the radio is a different question, gated on
    /// a presence challenge that does not exist yet, and
    /// <c>AllowRemoteFirmwareUpdates</c> is its answer. Do not grow this file
    /// into that; see the report for #404.
    /// </para>
    /// </remarks>
    public sealed class FirmwareTransportRefusalTests
    {
        private const string FlexBase = "Radios/FlexBase.cs";
        private const string SettingsJson = "Radios/Lexicon/settings.json";
        private const string ReasonKey = "settings.radio.firmware.smartlink_cannot_carry";

        /// <summary>
        /// The advisory check refuses and hands back the reason, so the dialog
        /// speaks it rather than greying a control and saying nothing.
        /// </summary>
        [Fact]
        public void ThePreflightRefusesOverSmartLinkAndSaysWhy()
        {
            string slice = Slice(Read(FlexBase),
                "public FirmwareUpdateCheck PreflightFirmwareUpdate(", 2600);

            Assert.True(slice.Contains("IsWanConnection", StringComparison.Ordinal),
                "PreflightFirmwareUpdate no longer tests the connection path. Firmware " +
                "cannot travel over SmartLink, and without this the only thing saying so " +
                "is button enablement that is sampled once and never refreshed on a " +
                "connection change. See task #404.");

            Assert.True(slice.Contains(ReasonKey, StringComparison.Ordinal),
                "PreflightFirmwareUpdate refuses over SmartLink without setting a reason " +
                "the operator can hear. A refusal with no reason is indistinguishable from " +
                "a click that did not register.");
        }

        /// <summary>
        /// The write itself refuses too. The preflight is advisory and both
        /// methods are public, so the transport fact is asserted where the
        /// bytes would actually move.
        /// </summary>
        [Fact]
        public void TheWriteRefusesOverSmartLinkEvenIfThePreflightWasSkipped()
        {
            string slice = Slice(Read(FlexBase), "public bool BeginFirmwareUpdate(", 1400);

            Assert.True(slice.Contains("IsWanConnection", StringComparison.Ordinal),
                "BeginFirmwareUpdate no longer tests the connection path. It is public and " +
                "the preflight can be skipped, so this is the last thing standing in front " +
                "of the highest-consequence write in the application. See task #404.");

            Assert.True(slice.Contains("ScreenReaderOutput.Speak", StringComparison.Ordinal),
                "BeginFirmwareUpdate refuses silently. An operator who pressed a button and " +
                "heard nothing cannot tell a refusal from a dead key.");
        }

        /// <summary>
        /// The reason is a real key. A missing one is spoken as the key itself,
        /// so the operator would hear the words "settings dot radio dot
        /// firmware" read out and learn nothing.
        /// </summary>
        [Fact]
        public void TheRefusalReasonExistsInTheStringStore()
        {
            using var doc = JsonDocument.Parse(Read(SettingsJson));
            bool found = false;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, ReasonKey, StringComparison.Ordinal)) { found = true; break; }

            Assert.True(found,
                ReasonKey + " is not in " + SettingsJson + ". The firmware refusal would be " +
                "spoken as its own key name.");
        }

        // ------------------------------------------------------------------

        private static string Slice(string source, string signature, int window)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0,
                "could not find '" + signature + "' — it was renamed or removed, and " +
                "whatever replaced it needs the SmartLink refusal too");
            return source.Substring(at, Math.Min(window, source.Length - at));
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), path + " is missing");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
