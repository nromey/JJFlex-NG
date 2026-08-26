using System;
using System.Linq;
using System.Reflection;
using Xunit;
using static Radios.Tests.IntegrationPass;

namespace Radios.Tests
{
    /// <summary>
    /// What the integration pass already found true at Sprint 35's base commit
    /// <c>22ac9926</c>, and who owns putting each one right.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a list to SHRINK, not a list to maintain.</b> Every entry is
    /// a defect somebody has already agreed exists. The gate fails when an
    /// entry stops being found, precisely so that fixing one forces a deletion
    /// here rather than leaving a claim nobody rechecks. CLAUDE.md carries the
    /// evidence for why that matters: a hand-maintained open-work register
    /// drifted from 34 items to 77 in nine days and nothing flagged it, because
    /// a drifted list looks exactly like a correct one.
    /// </para>
    /// <para>
    /// <b>Nothing may be added here to make a red build green.</b> An entry
    /// added by the person who caused the finding is a suppression, and the
    /// suppression outlives the memory of why. Add an entry only for damage
    /// that predates the sprint, and name the task that owns it — an entry with
    /// no owner is a defect with no owner.
    /// </para>
    /// <para>
    /// Task numbers resolve in the register the seal regenerates; they are not
    /// reproduced here, because this repository is public and the register
    /// names testers.
    /// </para>
    /// <para>
    /// Carries INTEGRATION_PASS_CORPUS_EXEMPT: entries here QUOTE the symbols
    /// they report as missing, so without the exemption the phantom-symbol
    /// sweep reads them back out of this file and concludes they exist. Every
    /// such finding would erase itself by being recorded, quietly, with a green
    /// test.
    /// </para>
    /// </remarks>
    internal static class IntegrationPassBaseline
    {
        internal static Known[] For(string rule)
            => Entries.Where(e => e.Rule == rule).ToArray();

        internal static readonly Known[] Entries =
        {
            // ---------------------------------------------------------------
            //  The blind walk
            // ---------------------------------------------------------------

            // #249 — CLOSED in the Sprint 35 merge. SkipControls() is now reached
// only from the result == null branch, so a stage that has produced a
// measurement no longer renders the control that discards it. The six
// entries that stood here are deleted rather than commented out: a
// baseline is a list of what is STILL true.
//
// Was: SkipControls() in FixerPage.cs took the stage and never
            // the run, so a skip control renders under every stage regardless
            // of whether that stage has already produced a measurement.
            // Pressing it replaces the measurement with a skip record and says
            // nothing — and on a transmitting stage the measurement it discards
            // was paid for with RF. Track A owns the Fixer surface this sprint.

            // ---------------------------------------------------------------
            //  Concept dedup
            // ---------------------------------------------------------------

            // The Auth0 URL builder and its base64url helper exist twice, in
            // full, in two projects. Neither copy knows about the other.
            new Known(Rules.DuplicateBody, "BuildAuth0Url", "#256",
                      "the whole Auth0 authorize-URL builder is implemented twice, in "
                      + "JJFlexWpf/Dialogs/AuthDialog.xaml.cs and Radios/AuthFormWebView2.cs"),
            new Known(Rules.DuplicateBody, "Base64UrlEncode", "#256",
                      "the PKCE base64url encoder is implemented twice, beside the two "
                      + "copies of the URL builder that use it"),

            // Three field-builder helpers copied verbatim between the panel and
            // the dialog. The Audio Workshop grew beside ScreenFieldsPanel
            // rather than out of it.
            new Known(Rules.DuplicateBody, "MakeToggle", "#256",
                      "identical in JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs and "
                      + "JJFlexWpf/Dialogs/AudioWorkshopDialog.Controls.cs"),
            new Known(Rules.DuplicateBody, "MakeValue", "#256",
                      "identical in JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs and "
                      + "JJFlexWpf/Dialogs/AudioWorkshopDialog.Controls.cs"),
            new Known(Rules.DuplicateBody, "MakeCycle", "#256",
                      "identical in JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs and "
                      + "JJFlexWpf/Dialogs/AudioWorkshopDialog.Controls.cs"),

            // The guarded radio reads in ChainChecks. Written twice for the
            // tune probe and the differential capture, which take the same
            // readings for the same report.
            new Known(Rules.DuplicateBody, "SafeAntenna", "#256",
                      "identical in Radios/ChainChecks/TxDifferentialCapture.cs and "
                      + "Radios/ChainChecks/TxTuneProbeRunner.cs"),
            new Known(Rules.DuplicateBody, "SafeFrequency", "#256",
                      "identical in Radios/ChainChecks/TxDifferentialCapture.cs and "
                      + "Radios/ChainChecks/TxTuneProbeRunner.cs"),
            new Known(Rules.DuplicateBody, "SafeMode", "#256",
                      "identical in Radios/ChainChecks/TxDifferentialCapture.cs and "
                      + "Radios/ChainChecks/TxTuneProbeRunner.cs"),
            new Known(Rules.DuplicateBody, "SafeInventory", "#256",
                      "identical in three files: Radios/ChainChecks/TxChainFacts.cs, "
                      + "TxDifferentialCapture.cs and TxTuneProbeRunner.cs"),

            new Known(Rules.DuplicateBody, "IsNonVoiceMode", "#256",
                      "identical in JJFlexWpf/NoiseReductionProvider.cs and "
                      + "JJFlexWpf/TxAudioConditioning.cs — one question about the "
                      + "operating mode, answered twice"),
            new Known(Rules.DuplicateBody, "GetFilePath", "#256",
                      "identical in Radios/ConnectPathLearningConfig.cs and "
                      + "Radios/DiagnosticsConfig.cs — two settings files resolving "
                      + "their own path the same way"),
            new Known(Rules.DuplicateBody, "ToggleOffOn", "#256",
                      "identical in Radios/AllRadios.cs and Radios/FlexBase.cs"),
            new Known(Rules.DuplicateBody, "CloseButton_Click", "#256",
                      "identical in JJFlexWpf/Dialogs/PowerDialog.xaml.cs and "
                      + "ProblemsDialog.xaml.cs — trivial, and listed because the "
                      + "detector cannot tell trivial from load-bearing and must not "
                      + "be taught to guess"),

            // Operator-facing sentences assembled independently in two files.
            // Keys are the sentence, trimmed and cut to 56 characters, exactly as
            // the detector reports them.

            new Known(Rules.DuplicateProse, "No radio is connected, so nothing can be said about an", "#256",
                      "JJFlexWpf/Dialogs/AudioWorkshopDialog.Amplifier.cs and Radios/AmplifierInventory.cs"),
            new Known(Rules.DuplicateProse, "the reference recording could not be prepared:", "#256",
                      "JJFlexWpf/Dialogs/FixerDialog.cs and Radios/ChainChecks/FixerTransmitAudioBoundary.cs"),
            new Known(Rules.DuplicateProse, "no radio is connected, so the radio could not be asked", "#256",
                      "Radios/ChainChecks/RxChainFacts.cs and TxChainFacts.cs"),
            new Known(Rules.DuplicateProse, "This device follows your Windows default microphone. Rig", "#256",
                      "JJFlexWpf/Dialogs/AudioDevicesDialog.xaml.cs and AudioWorkshopDialog.TxAudio.cs"),
            new Known(Rules.DuplicateProse, "Re-send recent CW notifications, pressing again for earl", "#256",
                      "the command's own words live in JJFlexWpf/KeyCommands.cs and again in KeyInventory.cs"),
            new Known(Rules.DuplicateProse, "Mic Bias (low-voltage electret mic power — not 48-volt p", "#256",
                      "JJFlexWpf/Dialogs/AudioWorkshopDialog.TxAudio.cs and NativeMenuBar.cs"),
            new Known(Rules.DuplicateProse, "Could not reach QRZ.com. Check your internet connection.", "#256",
                      "QrzLookup/QrzCallbookLookup.cs and QrzLogbookClient.cs"),
            new Known(Rules.DuplicateProse, "does the same thing. This is here for when that is not t", "#256",
                      "JJFlexWpf/Dialogs/AudioWorkshopDialog.Diagnostics.cs and AudioWorkshopDialog.MeterInventory.cs"),
            new Known(Rules.DuplicateProse, "JJ Flexible Radio Access — install verification", "#256",
                      "DebugInfo.vb and InstallManifest.vb"),
            new Known(Rules.DuplicateProse, "Text files (*.txt)|*.txt|All files (*.*)|*.*", "#256",
                      "JJFlexWpf/Dialogs/AboutDialog.xaml.cs and ProblemsDialog.xaml.cs"),
            new Known(Rules.DuplicateProse, "ADIF file (*.ADI)|*.ADI|Text file (*.TXT)|*.TXT", "#256",
                      "ExportForm.vb and ImportForm.vb"),
            new Known(Rules.DuplicateProse, ", out var typeEl) && typeEl.GetString() ==", "#256",
                      "JJFlexWpf/Dialogs/AuthDialog.xaml.cs and Radios/AuthFormWebView2.cs, part of the duplicated Auth0 implementation above"),

            // Two words for one thing, both reaching the operator. Named in
            // AudioSetupCheck's own remarks on 2026-08-25 and still open.
            new Known(Rules.CompetingVocabulary, "audio subsystem / audio system", "#256",
                      "the Fixer says \"audio subsystem\" and the Audio Devices dialog says "
                      + "\"audio system\" for the PortAudio host API, both to the operator"),

            // ---------------------------------------------------------------
            //  Standing rules
            // ---------------------------------------------------------------

            // #237. Three reflected-power thresholds, all live. Two of them are
            // required by their own comments to agree and do; the third is a
            // factor of two away and nothing connects it to either.
            new Known(Rules.ReflectedThreshold, "TxTuneProbe.ReflectedSuspectPercent", "#237",
                      "20 percent, against 40 in TransmitSafety.ReflectedWarnFraction and 40 "
                      + "in the power-coming-back rule. Whether the tune probe SHOULD share "
                      + "the warning threshold is #237's judgement, not this rule's — what is "
                      + "certain is that nothing today would notice if one of them moved"),

            // FOUND BY THE SWEEP, not by the plan. #237 was written up as
            // "three reflected thresholds"; there are four. This one is the
            // abort bar, deliberately above the suspect bar the way SwrAbort
            // sits above SwrSuspect, so the tiering may well be right — but
            // 20 / 40 / 50 are three unrelated literals describing one
            // quantity, and nothing ties any of them to the others.
            new Known(Rules.ReflectedThreshold, "TxTuneProbe.ReflectedAbortPercent", "#237",
                      "50 percent, the bar for stopping a tune early. Reasonable as a tier "
                      + "above the others and still a fourth free-standing literal about "
                      + "reflected power"),

            // A CheckBox wired to Click alone never hears IsChecked change any
            // other way, so every programmatic path has to remember to call the
            // handler itself. RadioOutputMute_Click even carries a re-entrancy
            // guard, which is machinery for Checked/Unchecked and cannot fire
            // where it sits.
            new Known(Rules.ClickOnlyCheckBox, "SettingsDialog.xaml/HeadphoneMuteCheck", "#256", ClickOnly),
            new Known(Rules.ClickOnlyCheckBox, "SettingsDialog.xaml/LineOutMuteCheck", "#256", ClickOnly),
            new Known(Rules.ClickOnlyCheckBox, "SettingsDialog.xaml/FrontSpeakerMuteCheck", "#256", ClickOnly),
            new Known(Rules.ClickOnlyCheckBox, "SettingsDialog.xaml/PcAudioCheck", "#256", ClickOnly),
            new Known(Rules.ClickOnlyCheckBox, "SettingsDialog.xaml/PortForwardEnabledCheck", "#256", ClickOnly),
            new Known(Rules.ClickOnlyCheckBox, "SettingsDialog.xaml/EnforcePrivateIpCheck", "#256", ClickOnly),
            new Known(Rules.ClickOnlyCheckBox, "SettingsDialog.xaml/VerboseDiagnosticsCheck", "#256", ClickOnly),
            new Known(Rules.ClickOnlyCheckBox, "SettingsDialog.xaml/RadioProfileNoPhysicalAccessCheck", "#256", ClickOnly),

            // #255's shape at the layer that can be proved without the host.
            new Known(Rules.SilentKeying, "FixerTransmitBoundary.ProbeTransmitter", "#255",
                      "it consults the transmit gate and keys the radio for a tune, and takes "
                      + "no delegate with which to say so. Stage 2 blocks the UI thread while "
                      + "transmitting, so nothing else can speak for it either"),

            // The worked example. CLAUDE.md's keyboard audit names two grep
            // targets that have never existed in this tree.
            new Known(Rules.PhantomSymbol, "CLAUDE.md/RegisterScope", "#256",
                      "the keyboard audit says to grep the diff for RegisterScope. No source "
                      + "file has ever contained it — the scope enum is KeyScope. A grep for a "
                      + "name that does not exist returns nothing, and nothing reads exactly "
                      + "like \"no key bindings changed\", so the audit reports clean and stops"),
            new Known(Rules.PhantomSymbol, "CLAUDE.md/KeyBinding", "#256",
                      "the same sentence says to look for \"KeyBinding attributes\". This tree "
                      + "has none — no XAML InputBinding is declared anywhere. A substring grep "
                      + "does hit ValidateKeyBindings, so this one misleads rather than silently "
                      + "passing; a person should decide what the sentence meant to say"),
        };

        internal const string ClickOnly =
            "wired to Click and neither Checked nor Unchecked, so it never hears the state "
            + "change by any route but a press — a binding, a settings reload or a screen "
            + "reader's toggle all move the box without reaching the handler";
    }

    /// <summary>The baseline's own integrity.</summary>
    public class IntegrationPassBaselineTests
    {
        private static IntegrationPass.Known[] Entries => IntegrationPassBaseline.Entries;

        /// <summary>
        /// A duplicate key would let one entry mask another, and an entry
        /// naming a rule no detector produces can never be struck out, so it
        /// would sit here for ever claiming something nobody checks.
        /// </summary>
        [Fact]
        public void The_baseline_is_well_formed()
        {
            string[] dupes = Entries.GroupBy(e => e.Key, StringComparer.Ordinal)
                                    .Where(g => g.Count() > 1)
                                    .Select(g => g.Key).ToArray();
            Assert.True(dupes.Length == 0,
                "duplicate baseline keys, so one entry hides another: " + string.Join("; ", dupes));

            // NonPublic is load-bearing: Rules' members are internal, and
            // GetFields() without flags returns only public ones. The first
            // version omitted it, found nothing, and reported every entry in
            // the baseline as an orphan — a check that looked at nothing and
            // said so loudly, which is the good failure mode of the two.
            string[] rules = typeof(IntegrationPass.Rules)
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(f => f.IsLiteral)
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToArray();

            Assert.True(rules.Length >= 10,
                "only " + rules.Length + " rule name(s) were read out of IntegrationPass.Rules, "
                + "so the orphan check below is comparing against an empty set and cannot fail "
                + "for the right reason.");

            string[] orphans = Entries.Select(e => e.Rule).Distinct()
                                      .Where(r => !rules.Contains(r)).ToArray();
            Assert.True(orphans.Length == 0,
                "baseline entries name rules that no detector produces, so nothing can ever "
                + "strike them out: " + string.Join("; ", orphans));

            Assert.All(Entries, e =>
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Task),
                             "a baseline entry with no owning task is a defect with no owner: " + e.Key);
                Assert.False(string.IsNullOrWhiteSpace(e.Why),
                             "a baseline entry with no reason cannot be judged later: " + e.Key);
            });
        }
    }
}
