#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The change-nothing hold's sentence is built in one place (#1086 F5,
    /// from the batch 5 integration pass). The Fix tool's two refusals and the
    /// re-readable lines in Settings and the static address control each built
    /// it by hand, and would drift from the hold the day it grows a second gate.
    /// </summary>
    /// <remarks>
    /// Two different needs, so two members. The spoken refusals take
    /// <see cref="FlexBase.HoldRefusalToSay"/>, which traces and goes empty while
    /// the radio's speech is suppressed. A line the operator re-reads takes
    /// <see cref="FlexBase.HoldRefusalSentence"/>, which never goes blank and
    /// does not trace a refusal the guard already traced. A status line built
    /// from the first would blank itself under suppression, and that is checked
    /// here as well as the hand-built copies.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class HoldSentenceOneSourceTests : IDisposable
    {
        private readonly FlexBase _rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        public void Dispose() => _rig.Dispose();

        // ── The sentence itself ─────────────────────────────────────────

        [Fact]
        public void The_plain_sentence_is_the_holds_sentence()
        {
            _rig.SetChangeNothingActive(true);
            Assert.Equal(_rig.HoldRefusalToSay("settings.guard.action.ports"),
                FlexBase.HoldRefusalSentence("settings.guard.action.ports"));
        }

        [Fact]
        public void The_plain_sentence_never_goes_blank_while_speech_is_suppressed()
        {
            _rig.SetChangeNothingActive(true);
            _rig.SuppressSpeech = true;
            // The spoken route has nothing to say: right for speech ...
            Assert.Equal("", _rig.HoldRefusalToSay("settings.guard.action.ports"));
            // ... and wrong for a line on screen, which keeps its words.
            string line = FlexBase.HoldRefusalSentence("settings.guard.action.ports");
            Assert.False(string.IsNullOrWhiteSpace(line));
            Assert.Contains(Lexicon.Get("settings.guard.action.ports"), line, StringComparison.Ordinal);
        }

        [Fact]
        public void The_plain_sentence_traces_nothing_speaks_nothing_and_is_the_holds_only_builder()
        {
            string src = Source("Radios", "FlexBase.cs");
            string plain = Between(src, "public static string HoldRefusalSentence(string actionKey)", ";");
            Assert.DoesNotContain("Trac", plain, StringComparison.Ordinal);
            Assert.DoesNotContain("Speak", plain, StringComparison.Ordinal);
            Assert.DoesNotContain("SuppressSpeech", plain, StringComparison.Ordinal);
            string traced = Between(src, "private string TheHoldRefused(string actionKey)", "\n        }");
            Assert.Contains("HoldRefusalSentence(actionKey)", traced, StringComparison.Ordinal);
            Assert.DoesNotContain("settings.guard.refused", traced, StringComparison.Ordinal);
        }

        // ── Hand-built copies ───────────────────────────────────────────

        /// <summary>
        /// The lexicon call's opening text, split in two, so the source quoted in this file's
        /// planted controls is not read as a lexicon call by LexiconKeyCoverageTests.
        /// </summary>
        private const string Get = "Lexicon" + ".Get(";

        /// <summary>Lines outside the hold's own builder that build its sentence.</summary>
        internal static List<string> HandBuiltCopies(string file, string text)
        {
            var hits = new List<string>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("'", StringComparison.Ordinal)
                    || line.StartsWith("///", StringComparison.Ordinal)) continue;
                if (!line.Contains("\"settings.guard.refused\"", StringComparison.Ordinal)) continue;
                bool theBuilder = file.EndsWith("FlexBase.cs", StringComparison.OrdinalIgnoreCase) && i > 0
                    && lines[i - 1].Contains("public static string HoldRefusalSentence(string actionKey)", StringComparison.Ordinal);
                if (!theBuilder) hits.Add(file + ":" + (i + 1) + ": " + line);
            }
            return hits;
        }

        /// <summary>
        /// Lines that put the hold's refusal on screen (a <c>.Text =</c> or a
        /// list a status line is built from) through the spoken route, which
        /// goes blank while the radio's speech is suppressed.
        /// </summary>
        internal static List<string> StatusLinesThatCanGoBlank(string file, string text)
        {
            var hits = new List<string>();
            var blankable = new Regex(@"(\.Text\s*=|\.Add\()[^;]*HoldRefusalToSay\(", RegexOptions.Singleline);
            foreach (Match m in blankable.Matches(text.Replace("\r\n", "\n")))
                hits.Add(file + ": " + m.Value.Trim());
            return hits;
        }

        private static IEnumerable<string> ProductSources()
        {
            string root = IntegrationPassTree.Root;
            return IntegrationPassTree.AuthoredSource.Concat(IntegrationPassTree.UntrackedSource())
                .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar)[0]
                    .EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void No_product_file_builds_the_holds_sentence_by_hand()
        {
            var hits = ProductSources().SelectMany(f => HandBuiltCopies(f, File.ReadAllText(f))).ToList();
            Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
        }

        [Fact]
        public void No_status_line_shows_the_refusal_through_the_route_that_goes_blank()
        {
            var hits = ProductSources().SelectMany(f => StatusLinesThatCanGoBlank(f, File.ReadAllText(f))).ToList();
            Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
        }

        [Theory]
        [InlineData("JJFlexWpf/Dialogs/SettingsDialog.xaml.cs", "refused.Add(FlexBase.HoldRefusalSentence(\"settings.guard.action.ports\"))")]
        [InlineData("JJFlexWpf/Dialogs/SettingsDialog.xaml.cs", "NetworkCurrentStateText.Text = FlexBase.HoldRefusalSentence(\"settings.guard.action.ports\")")]
        [InlineData("JJFlexWpf/Dialogs/SettingsDialog.RadioSetup.cs", "SetupRegisterStatus.Text = FlexBase.HoldRefusalSentence(\"settings.guard.action.registration\")")]
        [InlineData("JJFlexWpf/Dialogs/SettingsDialog.RadioSetup.cs", "SetupFirmwareFileText.Text = FlexBase.HoldRefusalSentence(\"settings.guard.action.firmware\")")]
        [InlineData("JJFlexWpf/Controls/StaticIpControl.xaml.cs", "ResultText.Text = FlexBase.HoldRefusalSentence(\"settings.guard.action.network\")")]
        public void Each_re_readable_line_takes_the_plain_sentence(string file, string expected) =>
            Assert.Contains(expected, Source(file.Split('/')), StringComparison.Ordinal);

        [Fact]
        public void The_fix_tools_refusals_take_the_holds_sentence_and_the_stale_comment_is_gone()
        {
            string src = Source("JJFlexWpf", "Dialogs", "FixerFixActions.cs");
            Assert.Contains("FixerFixOutcome.RefusedByHold(held)", src, StringComparison.Ordinal);
            Assert.Contains("FixerFixOutcome.RefusedByHold(FlexBase.HoldRefusalSentence(\"settings.guard.action.profile_load\"))", src, StringComparison.Ordinal);
            Assert.DoesNotContain("says nothing more", src, StringComparison.Ordinal);
        }

        // ── The controls: each check flags what it exists to flag ────────

        [Fact]
        public void A_planted_hand_built_copy_is_caught()
        {
            string planted = "x.Text = " + Get + "\"settings.guard.refused\",\n    (\"action\", " + Get + "\"settings.guard.action.ports\")));";
            Assert.Single(HandBuiltCopies("Planted.cs", planted));
            string builder = "        public static string HoldRefusalSentence(string actionKey) =>\n            " + Get + "\"settings.guard.refused\", (\"action\", " + Get + "actionKey)));";
            Assert.Empty(HandBuiltCopies("Radios" + Path.DirectorySeparatorChar + "FlexBase.cs", builder));
        }

        [Fact]
        public void A_planted_status_line_through_the_spoken_route_is_caught()
        {
            const string planted = "SetupRegisterStatus.Text = _rig.HoldRefusalToSay(\"settings.guard.action.registration\");\n"
                + "refused.Add(_rig.HoldRefusalToSay(\"settings.guard.action.ports\") ?? \"\");";
            Assert.Equal(2, StatusLinesThatCanGoBlank("Planted.cs", planted).Count);
            const string spoken = "string? held = Rig?.HoldRefusalToSay(\"settings.guard.action.full_duplex\");";
            Assert.Empty(StatusLinesThatCanGoBlank("Planted.cs", spoken));
        }

        // ── F8: a choice that did not save is heard at every verbosity ───

        internal static string? NotSavedCall(string menu)
        {
            int at = menu.IndexOf("Radios." + Get + "\"settings.profile_guest.not_saved\")", StringComparison.Ordinal);
            if (at < 0) return null;
            int start = menu.LastIndexOf("SpeakAfterMenuClose(", at, StringComparison.Ordinal);
            int end = menu.IndexOf(";", at, StringComparison.Ordinal);
            return start < 0 || end < 0 ? null : menu.Substring(start, end - start);
        }

        [Fact]
        public void A_guest_profile_choice_that_did_not_save_is_spoken_at_critical()
        {
            string? call = NotSavedCall(Source("JJFlexWpf", "NativeMenuBar.cs"));
            Assert.NotNull(call);
            Assert.Contains("VerbosityLevel.Critical", call!, StringComparison.Ordinal);
            // Off is Critical: only Critical is heard there.
            Assert.Equal(0, (int)VerbosityLevel.Critical);
        }

        [Fact]
        public void The_not_saved_check_catches_a_call_at_the_default_level()
        {
            string planted = "SpeakAfterMenuClose(Radios." + Get + "\"settings.profile_guest.not_saved\"),\n    subject: Radios.Speech.SpeechSubject.ProfileGuestOutcome);";
            Assert.DoesNotContain("VerbosityLevel.Critical", NotSavedCall(planted)!, StringComparison.Ordinal);
        }

        private static string Between(string text, string start, string end)
        {
            int at = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(at >= 0, "not found: " + start);
            int stop = text.IndexOf(end, at + start.Length, StringComparison.Ordinal);
            Assert.True(stop > at, "no end after: " + start);
            return text.Substring(at, stop - at);
        }

        private static string Source(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }
}
