using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// TWO THINGS, TWO WORDS (#446).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Noel, 31 August 2026: <i>"audio workshop, it says no mic profiles are
    /// saved, there are actually 27 or so profiles on this radio."</i>
    /// <b>Both statements were true, which is the problem.</b> Two different
    /// things were called a microphone profile:
    /// </para>
    /// <para>
    /// OURS — <c>Radios/MicrophoneProfile.cs</c> — is one microphone on THIS
    /// COMPUTER: its name, its capture settings, and which of the radio's own
    /// mic profiles to load when it is used there. THE RADIO's is a stored set
    /// of transmit-audio settings that lives on the Flex and is shared with
    /// SmartSDR and every other client. Don's 6300 holds 27 of them.
    /// </para>
    /// <para>
    /// <b>The ruling:</b> the RADIO's keeps <i>mic profile</i> — it is
    /// FlexRadio's own term, it appears in SmartSDR, and hams already know it.
    /// Ours becomes <i>microphone setup</i>. This file is where that ruling
    /// lives, because a vocabulary has no single line of code to put it on and
    /// because every "please keep these in step" comment in this codebase has
    /// eventually been ignored by a future editor.
    /// </para>
    /// <para>
    /// <b>Drafted wording, pending Noel's review.</b> He rules user-facing
    /// prose; if he picks a different word than "setup", change the two
    /// constants below and the strings will be held to the new one.
    /// </para>
    /// </remarks>
    public sealed class MicProfileVocabularyTests
    {
        /// <summary>What OUR store is called in front of an operator.</summary>
        private const string Ours = "microphone setup";

        /// <summary>What THE RADIO's is called in front of an operator.</summary>
        private const string Theirs = "mic profile";

        /// <summary>
        /// Lexicon keys under <c>audio.micprofile.</c> and
        /// <c>audio.mic_profiles.</c> that talk about OUR store. Everything
        /// under those prefixes that is not listed here is about the radio's,
        /// and a key added to neither list fails the last test in this file
        /// rather than quietly picking a side.
        /// </summary>
        private static readonly HashSet<string> AboutOurStore = new(StringComparer.Ordinal)
        {
            "audio.mic_profiles.file_unreadable",
            "audio.micprofile.applied",
            "audio.micprofile.cleanup_needs_a_radio",
            "audio.micprofile.delete_body",
            "audio.micprofile.delete_question",
            "audio.micprofile.delete_title",
            "audio.micprofile.delete_yes_label",
            "audio.micprofile.deleted",
            "audio.micprofile.deleted_but_not_saved",
            "audio.micprofile.device_mismatch",
            "audio.micprofile.device_mismatch_with_cleanup",
            "audio.micprofile.level_not_set",
            "audio.micprofile.none_saved_yet",
            "audio.micprofile.none_selected",
            "audio.micprofile.none_selected_save_one",
            "audio.micprofile.save_failed",
            "audio.micprofile.save_receipt",
            "audio.micprofile.verb_saved",
            "audio.micprofile.verb_updated",
        };

        private static readonly HashSet<string> AboutTheRadios = new(StringComparer.Ordinal)
        {
            "audio.micprofile.create_reason",
            "audio.micprofile.created_on_radio",
            "audio.micprofile.load_reason",
            "audio.micprofile.loaded_on_radio",
            "audio.micprofile.no_longer_listed",
            "audio.micprofile.nothing_created_on_radio",
            "audio.micprofile.radio_left_alone",
            "audio.micprofile.radio_offers_none",
            "audio.micprofile.references_radio_profile",
            "audio.micprofile.snapshotted",
            "audio.micprofile.someone_elses_body",
            "audio.micprofile.someone_elses_deliberate",
            "audio.micprofile.someone_elses_no",
            "audio.micprofile.someone_elses_question",
            "audio.micprofile.someone_elses_shared",
            "audio.micprofile.someone_elses_title",
            "audio.micprofile.someone_elses_yes",
            "audio.micprofile.this_radio",
        };

        private static Dictionary<string, string> Lexicon()
        {
            string path = Path.Combine(RepoRoot(), "Radios", "Lexicon", "audio.json");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    map[p.Name] = p.Value.GetString() ?? "";
            return map;
        }

        [Fact]
        public void The_positive_control_is_that_these_keys_are_still_there()
        {
            // Every assertion below is of the form "this string does not say X".
            // If the keys were renamed away, all of them would pass while saying
            // nothing at all.
            Dictionary<string, string> lex = Lexicon();
            foreach (string key in AboutOurStore)
                Assert.True(lex.ContainsKey(key), key + " is gone from audio.json");
            foreach (string key in AboutTheRadios)
                Assert.True(lex.ContainsKey(key), key + " is gone from audio.json");
        }

        [Fact]
        public void Nothing_about_our_store_calls_itself_a_mic_profile()
        {
            Dictionary<string, string> lex = Lexicon();

            foreach (string key in AboutOurStore)
            {
                string text = lex[key];
                // "Mic profiles stored on a radio itself are not touched" is
                // allowed and wanted: naming the OTHER thing to say it is not
                // this one is the disambiguation working.
                foreach (string sentence in text.Split('.'))
                {
                    if (sentence.IndexOf("radio", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    Assert.False(
                        sentence.IndexOf("mic profile", StringComparison.OrdinalIgnoreCase) >= 0
                        || sentence.IndexOf("microphone profile", StringComparison.OrdinalIgnoreCase) >= 0,
                        key + " calls our store a mic profile: \"" + sentence.Trim()
                        + "\" — ours is a \"" + Ours + "\" (#446).");
                }
            }
        }

        [Fact]
        public void The_empty_store_message_says_which_store_it_means()
        {
            // The exact string Noel read while his radio held 27 of the other
            // kind. "(none saved yet)" was accurate about our store and read as
            // obviously false to an operator whose radio had just reported 27 in
            // a report from the same application, minutes earlier.
            string text = Lexicon()["audio.micprofile.none_saved_yet"];

            Assert.DoesNotContain("profile", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("this computer", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Everything_about_the_radios_still_says_mic_profile()
        {
            // The other half, and it is not decoration. If the rename ever runs
            // one file too far, the radio's own term goes with it — and that
            // term is the one shared with SmartSDR, the Flex manual and every
            // conversation an operator has ever had about it.
            Dictionary<string, string> lex = Lexicon();
            var speaks = new[]
            {
                "audio.micprofile.created_on_radio",
                "audio.micprofile.loaded_on_radio",
                "audio.micprofile.radio_offers_none",
                "audio.micprofile.someone_elses_shared",
            };

            foreach (string key in speaks)
                Assert.Contains(Theirs, lex[key], StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_silent_transmit_warning_is_about_the_radios_and_says_so()
        {
            Dictionary<string, string> lex = Lexicon();
            foreach (string key in new[] { "audio.silent_tx.advisory", "audio.silent_tx.repaired" })
            {
                Assert.Contains("mic profile", lex[key], StringComparison.OrdinalIgnoreCase);
                Assert.Contains("radio", lex[key], StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void A_new_mic_profile_string_has_to_pick_a_side()
        {
            // The test that notices the SECOND definition appearing. A key added
            // under either prefix and listed in neither set above lands here,
            // which forces whoever adds it to say which of the two things it is
            // about — the question nobody was asked for months.
            foreach (string key in Lexicon().Keys)
            {
                if (!key.StartsWith("audio.micprofile.", StringComparison.Ordinal)
                    && !key.StartsWith("audio.mic_profiles.", StringComparison.Ordinal))
                    continue;

                Assert.True(AboutOurStore.Contains(key) || AboutTheRadios.Contains(key),
                    key + " is new. Two different things are called a microphone profile in this "
                    + "app — ours on this computer, the radio's on the radio (#446). Add it to "
                    + "AboutOurStore or AboutTheRadios in MicProfileVocabularyTests, and word it "
                    + "as a \"" + Ours + "\" or a \"" + Theirs + "\" accordingly.");
            }
        }

        [Fact]
        public void The_Fixer_report_names_whose_profile_it_read()
        {
            // The Fixer's settings fingerprint reads the RADIO's selection and
            // was labelled "Microphone profile" — our words. One report
            // therefore said "Microphone profile: empty" a few lines from "Mic
            // profiles this radio offers: 27".
            string source = File.ReadAllText(Path.Combine(
                RepoRoot(), "Radios", "FixerEvidence", "FixerSettingsFingerprint.cs"));

            Assert.Contains("\"Mic profile on the radio\"", source);
            Assert.DoesNotContain("MicProfile, \"Microphone profile\"", source);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
