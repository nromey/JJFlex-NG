using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// The check nobody was doing (#363): whether a control's accessible name
    /// is a NAME.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A name is an ACTION or a THING. An explanation is help text.</b> The
    /// operator found two violations inside one hour on 2026-08-28 — a button
    /// showing "Release All Extra Slices" and announcing "Release every slice
    /// except the one you are on, back to one slice", and a combo showing "Read
    /// the S-meter in:" and announcing "Unit the S-meter is read in for this
    /// radio". Two strings for one control, and only the long one is ever
    /// heard, on every single landing.
    /// </para>
    /// <para>
    /// <b>Nothing we had could see this class.</b> The invariant checks in
    /// <c>JJFlexWpf.Tests</c> walk every focusable control and assert a name
    /// EXISTS — and both of these existed, and both were carefully written. A
    /// sighted review cannot catch it either, because only one of the two
    /// strings is on screen at a time. The single detector was a screen-reader
    /// user landing on the control.
    /// </para>
    /// <para>
    /// <b>Why this lives in Radios.Tests and not beside the invariants.</b> The
    /// invariant suite constructs real dialogs and is gated behind
    /// <c>DeskGuard</c>, so it only runs when a human has stepped away from the
    /// machine. This defect is legible in SOURCE — both strings are literals,
    /// or lexicon keys — so it can be checked on every ordinary run instead of
    /// on the rare one. Nothing here creates a window, reads a setting or
    /// touches a radio.
    /// </para>
    /// <para>
    /// <b>WHAT IT PROVES AND WHAT IT CANNOT.</b> It proves a name is SHAPED
    /// like an explanation. It cannot prove the name is wrong, and it cannot
    /// hear what a reader actually says — announcements are assembled from more
    /// than the name. A green run here is not a substitute for landing on the
    /// control with a reader running.
    /// </para>
    /// </remarks>
    // LEXICON_SCANNER_EXEMPT — the synthetic source below contains call sites
    // written for the scanner to read, including one naming a key that has
    // deliberately been retired from the store. They are fixtures, not calls.
    public sealed class AccessibleNameTests
    {
        private readonly ITestOutputHelper _output;

        public AccessibleNameTests(ITestOutputHelper output) => _output = output;

        // ────────────────────────────────────────────────────────────────
        //  Positive controls, before anything else
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A scanner that finds nothing and a codebase with nothing wrong
        /// produce the same output. So the scanner is made to find, in
        /// synthetic source, both of the shapes the operator actually met —
        /// including the one a text scan gets wrong, where the "label" a naive
        /// pairing sees is really the control's own Ctrl+F1 help text.
        /// </summary>
        [Fact]
        public void The_scanner_finds_the_two_shapes_the_operator_found()
        {
            const string xaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:local='clr-namespace:JJFlexWpf'>
  <StackPanel>
    <!-- The Settings combo: one control, two different sentences. -->
    <TextBlock Text='Read the S-meter in:' />
    <ComboBox AutomationProperties.Name='Unit the S-meter is read in for this radio' />

    <!-- The shape a regex scan gets wrong: the long string is the ON-DEMAND
         explanation, and the name beside it is already correct. -->
    <ComboBox AutomationProperties.Name='Meter to configure'
              local:JJFlexHelp.Text='Pick which of your meter tones to work on. Everything below this changes the meter named here.' />

    <!-- A second sentence in the name, spoken on every landing. -->
    <Button Content='Use Now'
            AutomationProperties.Name='Use now. Uses this account for the rest of this session.' />

    <!-- A prose block that names itself. Not a defect: the sentence is on
         screen, and the reader reads it either way — even though the two
         copies were typed with different punctuation. -->
    <TextBlock Text='Enter any two values &#8212; the third will be computed.'
               AutomationProperties.Name='Enter any two values, the third will be computed' />

    <!-- A heading is not a label for whatever happens to follow it. Without
         the colon convention this pairs the two and invents a finding. -->
    <TextBlock Text='Network' />
    <CheckBox Content='Use SmartLink' AutomationProperties.Name='Use SmartLink' />

    <!-- Expanding an abbreviation is right and normal. -->
    <Button Content='TNF' AutomationProperties.Name='Tracking Notch Filter' />

    <!-- A TextBox's Text is its VALUE. Reading it as a label invents a
         finding out of nothing, so the name is judged on its length alone. -->
    <TextBox Text='4992' AutomationProperties.Name='Port number' />
  </StackPanel>
</Window>";

            const string code = @"
class Panel
{
    void Build()
    {
        var releaseAllButton = new Button
        {
            Content = Lexicon.Get(""audio.fields.release_all_button""),
        };
        AutomationProperties.SetName(releaseAllButton, Lexicon.Get(""audio.fields.release_all_name""));

        var okButton = new Button { Content = ""OK"" };
        AutomationProperties.SetName(okButton, ""OK"");

        var mystery = new Button();
        AutomationProperties.SetName(mystery, someVariable);
    }
}";

            var lexicon = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["audio.fields.release_all_button"] = "Release All Extra Slices",
                ["audio.fields.release_all_name"] =
                    "Release every slice except the one you are on, back to one slice",
            };

            var result = AccessibleNameScan.Scan(
                new[] { ("Synthetic.xaml", xaml) },
                new[] { ("Synthetic.xaml.cs", code) },
                lexicon);
            _output.WriteLine(AccessibleNameScan.Report(result));

            Assert.Empty(result.Notes);

            var findings = AccessibleNameScan.Findings(result);
            string all = string.Join(Environment.NewLine, findings.Select(f => f.Line));

            // BOTH of the operator's instances, caught by two different rules.
            // The S-meter combo is the one a rule reading only a control's own
            // Content cannot see: its label is the TextBlock beside it, which
            // is the more common of the two layouts.
            var smeter = findings.Single(f => f.Control.Name.StartsWith("Unit the S-meter", StringComparison.Ordinal));
            Assert.Equal("Read the S-meter in", smeter.Control.Label);
            Assert.Equal("the label before it", smeter.Control.LabelSource);

            Assert.Contains(findings, f => f.Control.Name.StartsWith("Release every slice", StringComparison.Ordinal));

            // Resolved THROUGH the lexicon, on both sides. Without this the
            // release-all finding would be a coincidence of two literals.
            var releaseAll = findings.Single(f => f.Control.Name.StartsWith("Release every slice", StringComparison.Ordinal));
            Assert.Equal("Release All Extra Slices", releaseAll.Control.Label);
            Assert.Equal("audio.fields.release_all_name", releaseAll.Control.NameKey);
            Assert.Equal(AccessibleNameScan.ExceedsLabel, releaseAll.Direction);

            // The second sentence is found, and quoted back.
            var useNow = findings.Single(f => f.Control.Name.StartsWith("Use now.", StringComparison.Ordinal));
            Assert.Equal(AccessibleNameScan.Prose, useNow.Direction);
            Assert.Contains("Uses this account", useNow.Detail, StringComparison.Ordinal);

            // And the five shapes that must NOT be reported.
            Assert.DoesNotContain("Meter to configure", all, StringComparison.Ordinal);
            Assert.DoesNotContain("Enter any two values", all, StringComparison.Ordinal);
            Assert.DoesNotContain("Tracking Notch Filter", all, StringComparison.Ordinal);
            Assert.DoesNotContain("Port number", all, StringComparison.Ordinal);
            Assert.DoesNotContain("Use SmartLink", all, StringComparison.Ordinal);
            Assert.DoesNotContain("\"OK\"", all, StringComparison.Ordinal);

            // A name it could not resolve is COUNTED, never assumed clean.
            Assert.Equal(1, result.UnresolvedCodeNames);
        }

        /// <summary>
        /// The rule reduced to the sentence it is: a name is an action or a
        /// thing. These are the boundary cases the regex has to get right, and
        /// getting one wrong is a hole that reads exactly like a clean tree.
        /// </summary>
        [Theory]
        // A real second sentence.
        [InlineData("Cancel. Discards unapplied changes.", true)]
        [InlineData("OK. Applies the auto-connect settings and closes.", true)]
        // An ellipsis is not a sentence break — it is how a button says it
        // opens a dialog.
        [InlineData("Audio Devices... choose which sound devices JJ Flexible uses", false)]
        // Neither is a decimal, a version or an abbreviation.
        [InlineData("Set the threshold to 1.5 decibels", false)]
        [InlineData("Firmware 3.2.19 is available", false)]
        [InlineData("A tone burst, e.g. the one the tuner makes", false)]
        // One sentence, however long, is not this rule's finding.
        [InlineData("Release every slice except the one you are on, back to one slice", false)]
        public void A_sentence_break_is_a_terminator_between_two_words(string name, bool expected)
            => Assert.Equal(expected, AccessibleNameScan.SentenceBreak(name) != null);

        /// <summary>
        /// The access-key underscore is invisible on screen and absent from
        /// speech, so "App_ly to radio" and "Apply to radio" are one label
        /// written twice. A comparison that misses this reports every mnemonic
        /// button in the application.
        /// </summary>
        [Theory]
        [InlineData("App_ly to radio", "Apply to radio")]
        [InlineData("Audio Devices...", "Audio Devices")]
        [InlineData("Read the S-meter in:", "Read the S-meter in")]
        [InlineData("  spaced   out  ", "spaced out")]
        public void An_access_key_and_a_trailing_colon_are_not_part_of_the_label(string raw, string expected)
            => Assert.Equal(expected, AccessibleNameScan.Normalise(raw));

        // ────────────────────────────────────────────────────────────────
        //  The real tree
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The scan reached the real UI. Without this, every assertion below
        /// could pass on an empty read — which is the failure mode a checker
        /// has that a person does not, because nothing about an empty result
        /// looks wrong.
        /// </summary>
        [Fact]
        public void The_scan_reaches_the_real_user_interface()
        {
            var result = Real;

            Assert.True(result.XamlFilesRead >= 60,
                "only " + result.XamlFilesRead + " XAML files read — the UI has moved and this "
                + "check is no longer reading the application it thinks it is");
            Assert.True(result.LexiconEntries >= 1000,
                "only " + result.LexiconEntries + " lexicon entries read — accessible names built "
                + "from lexicon keys would resolve to nothing and silently pass");
            Assert.True(result.Controls.Count >= 400,
                "only " + result.Controls.Count + " accessible names found");
            Assert.Contains(result.Controls, c => c.Label != null);
            Assert.Contains(result.Controls, c => c.NameKey.Length > 0);
        }

        /// <summary>
        /// Every file was read. A file that failed to parse is a whole dialog's
        /// worth of names this check never saw, and an unread dialog reports
        /// exactly what a clean one reports.
        /// </summary>
        [Fact]
        public void The_scan_met_no_file_it_could_not_read()
        {
            var notes = Real.Notes.Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.True(notes.Count == 0,
                "the accessible-name scan could not read part of the UI, so its findings are "
                + "incomplete:" + Environment.NewLine + string.Join(Environment.NewLine, notes));
        }

        /// <summary>
        /// THE CHECK. No control's accessible name may be an explanation.
        /// </summary>
        [Fact]
        public void No_accessible_name_is_an_explanation()
        {
            var findings = AccessibleNameScan.Findings(Real);
            _output.WriteLine(AccessibleNameScan.Report(Real));

            var complaint = Reconcile(findings, Baseline);
            Assert.True(complaint.Count == 0,
                string.Join(Environment.NewLine, complaint)
                + Environment.NewLine
                + "A name is an ACTION or a THING; the explanation belongs in JJFlexHelp.Text, "
                + "where Ctrl+F1 finds it on demand and silence finds it otherwise. NOT in "
                + "AutomationProperties.HelpText — a reader speaks that on every focus too, "
                + "which is how the 2026-08-18 sweep changed nothing an operator could hear.");
        }

        // ────────────────────────────────────────────────────────────────
        //  Baseline
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// What was already true when this checker was written, and therefore
        /// what it is not this check's job to make green tonight.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A list to SHRINK, not to maintain</b>, in the shape
        /// <see cref="FieldKeyMapTests"/> and <see cref="IntegrationPassBaseline"/>
        /// already use. The gate fails when an entry stops being found, so
        /// putting a name right forces a deletion here rather than leaving a
        /// claim nobody rechecks.
        /// </para>
        /// <para>
        /// <b>Nothing may be added here to make a red build green.</b> An entry
        /// added by whoever caused the finding is a suppression, and a
        /// suppression outlives the memory of why. Every entry below carries
        /// the reason it is still here, and several of them are arguments for
        /// keeping the name as it is — this rule has real exceptions and they
        /// belong in writing, not in a threshold.
        /// </para>
        /// <para>
        /// <b>Each entry is the offending SENTENCE, on purpose.</b> Rewording
        /// the name is exactly what closes the finding, so the entry stops
        /// matching the moment somebody fixes it.
        /// </para>
        /// </remarks>
        private static readonly string[] Baseline =
        {
            // ── Settings, the network and firmware panel ──────────────────
            // Nine buttons and a tab, written to one house style by one hand:
            // a short label on screen and a full description in the ear.
            // "Help" is the worst of them — the shortest button in the panel
            // carries the longest name. Every one is a straight lift into
            // JJFlexHelp.Text and none of them is contentious.
            //
            // NOT DONE TONIGHT AND THE REASON IS NOT DOUBT: SettingsDialog.xaml
            // is the largest shared surface in the application and Sprint 39 has
            // seven tracks running against it. This is twenty lines of edit in a
            // file three other tasks may be standing in. Worth doing on a quiet
            // tree, and worth doing all at once so the panel keeps one voice.
            "NAME-EXCEEDS-LABEL SettingsDialog.xaml · Audio Devices, choose which sound devices JJ Flex uses",
            "NAME-EXCEEDS-LABEL SettingsDialog.xaml · Copy the last network diagnostic report to the clipboard as…",
            "NAME-EXCEEDS-LABEL SettingsDialog.xaml · Diagnostics, diagnostic log and problem reporting settings",
            "NAME-EXCEEDS-LABEL SettingsDialog.xaml · Go to the Network tab to set the port",
            "NAME-EXCEEDS-LABEL SettingsDialog.xaml · Look for firmware for this radio on the JJ Flexible servers…",
            "NAME-EXCEEDS-LABEL SettingsDialog.xaml · Open the help document relevant to the current network state",
            "NAME-EXCEEDS-LABEL SettingsDialog.xaml · Re-read the radio and update the status of every step",
            "NAME-EXCEEDS-LABEL SettingsDialog.xaml · Run a fresh SmartLink network diagnostic probe against the c…",
            "NAME-EXCEEDS-LABEL SettingsDialog.xaml · Save the last network diagnostic report to a file",
            "NAME-EXCEEDS-LABEL SettingsDialog.xaml · Test the port number locally for validity and common conflic…",

            // ── A range or a unit in the name of a field ──────────────────
            // "Port number, 1024 to 65535, applied to TCP and UDP" is a name
            // followed by a specification. By the rule the specification is
            // help text, and there is a real argument the other way: an
            // operator typing into a box wants the range at the moment of
            // typing, not after pressing a key to ask for it. That argument is
            // WEAKER than it sounds — the range is heard on every landing
            // forever, and is wanted once — but it is Noel's call, not a
            // checker's, and it is the same call for all nine.
            "NAME-EXCEEDS-LABEL MetersPanel.xaml · Highest pitch in hertz, 100 to 4000",
            "NAME-EXCEEDS-LABEL MetersPanel.xaml · Lowest pitch in hertz, 100 to 2000",
            "NAME-EXCEEDS-LABEL MetersPanel.xaml · Speech interval in seconds, 1 to 10",
            "NAME-IS-LONG MetersPanel.xaml · Pan, minus 100 full left, 0 centre, 100 full right",
            "NAME-IS-LONG SettingsDialog.xaml · ALC auto-release seconds, 0 to disable or 10 to 300",
            "NAME-IS-LONG SettingsDialog.xaml · Headphone level, 0 to 100, sent to the radio as you change i…",
            "NAME-IS-LONG SettingsDialog.xaml · Line out level, 0 to 100, sent to the radio as you change it",
            "NAME-IS-LONG SettingsDialog.xaml · Port number, 1024 to 65535, applied to TCP and UDP",
            "NAME-EXCEEDS-LABEL ConnectionTesterDialog.xaml · Simulated user delay in seconds for manual simulation",

            // ── A name that also says what the setting is FOR ─────────────
            // Same shape, without a range: the name states the thing and then
            // states its consequence. Each is a sentence a person will want
            // once and hear for years.
            "NAME-IS-LONG SettingsDialog.xaml · Connects in a row before JJ Flexible takes the hint",
            "NAME-IS-LONG SettingsDialog.xaml · PC audio when this radio connects, saved for this radio",
            "NAME-IS-LONG SettingsDialog.xaml · REM ON remote power jack when connecting to this radio",
            "NAME-IS-LONG SettingsDialog.xaml · Tier 1 plus 2, manual plus UPnP automatic port mapping",
            "NAME-IS-PROSE SettingsDialog.xaml · Tier 1, manual port forwarding only. Sovereign default.",
            "NAME-IS-PROSE SettingsDialog.xaml · Automatic, follow what the radio reports. Recommended.",
            "NAME-IS-PROSE StaticIpControl.xaml · Automatic, the router assigns the address. Default.",
            "NAME-IS-PROSE SettingsDialog.xaml · Radio to configure. Choose from known radios or type a seria…",
            "NAME-IS-PROSE SettingsDialog.xaml · This radio is operated remotely. I cannot reach its front pa…",
            "NAME-EXCEEDS-LABEL StaticIpControl.xaml · Apply the address mode and settings to the radio",
            "NAME-EXCEEDS-LABEL StaticIpControl.xaml · Fill in the fields using the address the radio is using righ…",
            "NAME-EXCEEDS-LABEL AudioDevicesDialog.xaml · Microphone input device sent to the radio",
            "NAME-EXCEEDS-LABEL MetersPanel.xaml · Play a two second preview of this meter's tone",
            "NAME-EXCEEDS-LABEL RadioAssociationsDialog.xaml · Bind the selected radio to the chosen account",
            "NAME-EXCEEDS-LABEL ScreenFieldsPanel.xaml.cs · Noise profiles: capture settings, save, load, and manage",
            "NAME-EXCEEDS-LABEL SmartLinkSignUpDialog.cs · Send a password reset email to the address above, for the ac…",
            "NAME-IS-LONG AudioWorkshopDialog.MicProfiles.cs · Why creating a mic profile on the radio is not offered",

            // ── Three menu items that differ ONLY in the tail ─────────────
            // "Connect", "Connect Locally" and "Connect over SmartLink" sit
            // next to each other, and the distinguishing words — preferred
            // path, local only, SmartLink only never falling back — are the
            // whole point of having three items. Trim these to their labels
            // and the operator hears three names that barely differ. This one
            // needs the words rewritten, not moved, and that is prose work
            // rather than a sweep.
            "NAME-EXCEEDS-LABEL RigSelectorDialog.xaml · Connect to selected radio over SmartLink only, never falling…",
            "NAME-EXCEEDS-LABEL RigSelectorDialog.xaml · Connect to selected radio over the local network only",
            "NAME-EXCEEDS-LABEL RigSelectorDialog.xaml · Connect to selected radio using its preferred path",

            // ── Names whose second sentence reports STATE ─────────────────
            // These are rebuilt as the dialog changes, and the tail is a live
            // fact rather than an explanation: whether an account is saved,
            // which account Automatic would pick. Help text cannot carry it,
            // because help text is written once and this changes. The right
            // answer is probably a status line the operator can read on
            // demand, which is a design question, not a rename.
            "NAME-IS-PROSE RigSelectorDialog.xaml.cs · Automatic. Use the account that last listed this radio, or t…",
            "NAME-IS-PROSE RigSelectorDialog.xaml.cs · Sign in to SmartLink. No SmartLink account is saved on this…",

            // ── The two the rule should probably NOT win ──────────────────
            // Remove Radio's options. The second sentence says whether the
            // thing you are about to do can be undone — "everything you have
            // set up for this radio is deleted and cannot be recovered" —
            // and this is the dialog whose destructive option was already
            // found to be hard to encounter at all. Moving a consequence
            // behind a key the operator has to know to press is the opposite
            // of what a destructive choice needs. Deliberately unfixed, and
            // deliberately not deleted from this list either: it is the
            // clearest example that the rule has an edge, and the edge is
            // "does the sentence say what this will cost you".
            "NAME-IS-PROSE RemoveRadioDialog.xaml · Remove from the list only. Everything you have set up for th…",
            "NAME-IS-PROSE RemoveRadioDialog.xaml · Remove the radio and its settings. Everything you have set u…",

            // Settings' Restart button. Same shape, much milder: "Asks for
            // confirmation first" is reassurance before a disruptive action
            // rather than a consequence. Probably should be trimmed; grouped
            // with the two above because it is the same judgement.
            "NAME-IS-PROSE SettingsDialog.xaml · Restart the radio. Asks for confirmation first.",

            // ── A keystroke inside a name ─────────────────────────────────
            // "Log Contact with this station. Ctrl+Enter" is not an
            // explanation, it is a key hint, and hiding key hints is BlindCat
            // anti-pattern number one — the thing this project exists not to
            // do. AutomationProperties.HelpText is the right slot for a hint
            // this short, by JJFlexHelp's own rule, but moving it should
            // happen alongside the keyboard reference rather than on its own.
            "NAME-IS-PROSE StationLookupWindow.xaml · Log Contact with this station. Ctrl+Enter",
        };

        /// <summary>
        /// Compare today's findings with the baseline, in BOTH directions: a
        /// new finding fails, and so does a baseline entry that has stopped
        /// being found. Pure, so the gate itself can be proved to fire.
        /// </summary>
        internal static List<string> Reconcile(
            List<AccessibleNameScan.Finding> findings, IEnumerable<string> baseline)
        {
            var known = new HashSet<string>(baseline, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var fresh = new List<string>();
            foreach (var finding in findings)
            {
                seen.Add(finding.Id);
                if (!known.Contains(finding.Id)) fresh.Add(finding.Line);
            }

            var repaired = known.Where(k => !seen.Contains(k))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();

            var complaint = new List<string>();
            if (fresh.Count > 0)
            {
                complaint.Add("NEW accessible names shaped like explanations (" + fresh.Count + "):");
                complaint.AddRange(fresh.Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal).Select(s => "  " + s));
            }
            if (repaired.Count > 0)
            {
                complaint.Add("These baseline entries are no longer found. Good — delete them from "
                    + "AccessibleNameTests.Baseline, because a baseline is a list of what is STILL "
                    + "true (" + repaired.Count + "):");
                complaint.AddRange(repaired.Select(s => "  " + s));
            }
            return complaint;
        }

        /// <summary>
        /// The gate itself, proved to fire — in both directions, because a
        /// baseline that only catches new findings quietly becomes a list of
        /// claims nobody rechecks.
        /// </summary>
        [Fact]
        public void The_baseline_fails_on_a_new_finding_and_on_a_repaired_one()
        {
            var known = new AccessibleNameScan.Named
            {
                File = "JJFlexWpf/Dialogs/Known.xaml",
                Element = "<Button>",
                Name = "Cancel. Discards unapplied changes.",
            };
            var brandNew = new AccessibleNameScan.Named
            {
                File = "JJFlexWpf/Dialogs/Known.xaml",
                Element = "<Button>",
                Name = "Apply. Something nobody has looked at.",
            };

            var findings = new List<AccessibleNameScan.Finding>
            {
                new(AccessibleNameScan.Prose, known, "known"),
                new(AccessibleNameScan.Prose, brandNew, "never seen before"),
            };
            string[] baseline =
            {
                "NAME-IS-PROSE Known.xaml · Cancel. Discards unapplied changes.",
                "NAME-IS-PROSE Known.xaml · A name somebody already put right.",
            };

            string all = string.Join(Environment.NewLine, Reconcile(findings, baseline));

            Assert.Contains("never seen before", all, StringComparison.Ordinal);
            Assert.Contains("A name somebody already put right.", all, StringComparison.Ordinal);
            Assert.DoesNotContain("known", all, StringComparison.Ordinal);

            // And a tree that matches its baseline exactly says nothing at all.
            Assert.Empty(Reconcile(
                new List<AccessibleNameScan.Finding> { new(AccessibleNameScan.Prose, known, "known") },
                new[] { "NAME-IS-PROSE Known.xaml · Cancel. Discards unapplied changes." }));
        }

        // Read once per test class instance. Pure source parsing: no window, no
        // settings, no radio.
        private static readonly AccessibleNameScan.Result RealScan = AccessibleNameScan.ScanRepository();

        private static AccessibleNameScan.Result Real => RealScan;
    }
}
