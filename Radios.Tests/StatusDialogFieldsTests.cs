using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 43 Track E, #320. The Status Dialog reports the two fields its
    /// own specification named and never got.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>From Don's 2026-03-12 feedback.</b> The rebuild happened and is
    /// good; active preset and tuning mode fell off its field list and nothing
    /// recorded the gap, which is why this is a test rather than a note.
    /// </para>
    /// <para>
    /// <b>Tuning mode is the one worth arguing for.</b> Classic and Modern
    /// have different field sets and different key meanings, operators lose
    /// track of which one they are in, and this dialog is where somebody goes
    /// to ask what the state of things is. Until now the cheapest way to find
    /// out was to CHANGE mode and listen to the announcement — an answer that
    /// destroys the thing it was asked about.
    /// </para>
    /// <para>
    /// <b>Both values come from accessors that already existed</b>, the same
    /// two the Speak Status key reads. That is the assertion below that
    /// matters most: a second derivation of either value is how the meters
    /// ended up with two answers to one question.
    /// </para>
    /// </remarks>
    public sealed class StatusDialogFieldsTests
    {
        private const string DialogFile = "JJFlexWpf/Dialogs/StatusDialog.xaml.cs";

        [Fact]
        public void TheReadoutReportsTuningModeAndActivePreset()
        {
            string source = Read(DialogFile);

            // Positive control: this really is the status readout builder.
            Assert.Contains("private void RefreshStatus()", source, StringComparison.Ordinal);
            Assert.Contains("connect.status.section_radio", source, StringComparison.Ordinal);

            Assert.Contains("TuningModeStatus", source, StringComparison.Ordinal);
            Assert.Contains("FilterPresetStatus", source, StringComparison.Ordinal);
            Assert.Contains("connect.status.section_operating", source, StringComparison.Ordinal);
        }

        [Fact]
        public void NoPresetSaysSoRatherThanLeavingAHole()
        {
            // Silence is indistinguishable from a line that failed to render,
            // and that ambiguity is exactly what the roster's occupancy clause
            // was rewritten to remove (#394). A dialog whose job is answering
            // "what is the state of things" must not answer a question with
            // nothing.
            Assert.Contains("connect.status.no_filter_preset", Read(DialogFile),
                StringComparison.Ordinal);
        }

        [Fact]
        public void BothValuesAreSuppliedRatherThanDerivedHere()
        {
            string source = Read(DialogFile);

            // Suppliers, because the readout refreshes every five seconds and
            // both of these change while the dialog is open — the tuning mode
            // has a hotkey, and the preset changes the moment the operator
            // walks the filter. A value captured at construction goes stale in
            // the one surface whose entire job is being current.
            Assert.Contains("Func<string?>? TuningModeStatus", source, StringComparison.Ordinal);
            Assert.Contains("Func<string?>? FilterPresetStatus", source, StringComparison.Ordinal);

            // And nothing here re-derives either one.
            Assert.DoesNotContain("FindActivePreset", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ActiveUIMode", source, StringComparison.Ordinal);
        }

        [Fact]
        public void TheLaunchSiteHandsOverTheAccessorsThatAlreadyExist()
        {
            string commands = Read("JJFlexWpf/KeyCommands.cs");

            // The same two SpeakStatusHandler reads. If either is renamed, this
            // fails here rather than leaving the dialog quietly blank.
            Assert.Contains("TuningModeStatus = mw == null ? null : mw.GetTuningModeStatus",
                commands, StringComparison.Ordinal);
            Assert.Contains("FilterPresetStatus = mw == null ? null : mw.GetFilterPresetStatus",
                commands, StringComparison.Ordinal);
            Assert.Contains("mw?.GetTuningModeStatus()", commands, StringComparison.Ordinal);
            Assert.Contains("mw?.GetFilterPresetStatus()", commands, StringComparison.Ordinal);
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that cannot "
                + "find its subject passes every absence check it makes.");
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
