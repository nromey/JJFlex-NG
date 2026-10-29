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
    /// Ctrl+F1 and F1 explain the method chosen in Check your audio (#835,
    /// Noel 2026-10-28 00:15, ruled 00:20): Ctrl+F1 on the method says what the
    /// control is and what the method shown right now does, and F1 opens the
    /// help at that method's own section.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One account per method.</b> The status line under the method, the
    /// Ctrl+F1 answer and the help page's section all carry the same
    /// description, read from one lexicon key, so they cannot drift into two
    /// accounts. The help section is held to it word for word.
    /// </para>
    /// <para>
    /// <b>What these cannot see:</b> that the compiled help's viewer honours the
    /// <c>#anchor</c> in a topic it is handed, which is the bench's question;
    /// what NVDA actually says on Ctrl+F1 and on focus; and the dialog's F1
    /// handler running, which only a source pin reaches here (pressing it would
    /// open the help on the operator's desktop). The method control following
    /// the selection is tested windowless in JJFlexWpf.Tests,
    /// <c>CheckYourAudioCtrlF1Tests</c>.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class CheckYourAudioMethodHelpTests
    {
        private static IEnumerable<AudioCheckListenMethods> Methods => AudioCheckMethods.DisplayOrder;

        // ────────────────────────────────────────────────────────────────
        //  The descriptions
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Every_method_has_a_description_of_its_own()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in Methods)
            {
                string key = AudioCheckMethods.DescriptionKey(m);
                string text = AudioCheckMethods.Description(m);
                Assert.False(string.IsNullOrWhiteSpace(text), m + " has an empty description");
                Assert.NotEqual(key, text); // Lexicon.Get returns the key itself when it is missing
                Assert.EndsWith(".", text);
                Assert.True(seen.Add(text), m + " says the same as another method");
            }
            Assert.Equal(6, seen.Count);
        }

        [Fact]
        public void Every_description_says_where_the_microphone_verdict_appears()
        {
            // Every method gives the mic verdict, Off the air included (13:50).
            foreach (var m in Methods)
                Assert.Contains("reading box", AudioCheckMethods.Description(m), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(AudioCheckListenMethods.OffAir, "local or VPN connection")]
        [InlineData(AudioCheckListenMethods.Loopback, "local connection")]
        public void The_methods_with_needs_say_what_they_need(AudioCheckListenMethods method, string connection)
        {
            string text = AudioCheckMethods.Description(method);
            Assert.Contains(connection, text, StringComparison.Ordinal);
            Assert.Contains("two receivers", text, StringComparison.Ordinal);
            Assert.Contains("transverter port", text, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(AudioCheckListenMethods.Monitor)]
        [InlineData(AudioCheckListenMethods.RecordPlayback)]
        public void The_methods_that_obey_check_power_say_so(AudioCheckListenMethods method)
        {
            Assert.True(AudioCheckMethods.UsesCheckPower(method));
            Assert.Contains("check power", AudioCheckMethods.Description(method), StringComparison.Ordinal);
        }

        // ────────────────────────────────────────────────────────────────
        //  Ctrl+F1
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_control_sentence_is_one_sentence()
        {
            string control = Lexicon.Get("audio.check.method_help").Trim();
            Assert.EndsWith(".", control);
            Assert.DoesNotContain(". ", control, StringComparison.Ordinal);
        }

        [Fact]
        public void Ctrl_F1_says_the_control_and_then_the_method_shown()
        {
            string control = Lexicon.Get("audio.check.method_help");
            var answers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in Methods)
            {
                string answer = AudioCheckMethods.ContextHelp(m);
                Assert.Equal(control + " " + AudioCheckMethods.Description(m), answer);
                Assert.True(answers.Add(answer), "Ctrl+F1 says the same for " + m + " as for another method");
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  F1
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Every_method_has_a_help_target_of_its_own()
        {
            var targets = Methods.Select(AudioCheckMethods.HelpContext).ToList();
            Assert.Equal(targets.Count, targets.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.DoesNotContain(AudioCheckMethods.HelpPageContext, targets);
        }

        [Fact]
        public void A_method_with_no_section_opens_the_page()
        {
            Assert.Equal(AudioCheckMethods.HelpPageContext,
                AudioCheckMethods.HelpContext((AudioCheckListenMethods)99));
            var map = ContextMap();
            Assert.True(map.TryGetValue(AudioCheckMethods.HelpPageContext, out var page),
                "HelpLauncher.ContextMap has no entry for the page itself");
            Assert.Equal("AudioWorkshop/AudioWorkshop.md", page.Page);
            Assert.Equal("", page.Anchor);
        }

        [Fact]
        public void Every_help_target_names_a_page_that_exists_and_an_anchor_it_defines()
        {
            var map = ContextMap();
            // Positive control: the reader sees the entries the rest of the map
            // is made of, so an anchor entry it failed to read cannot pass here
            // by being absent.
            Assert.True(map.Count >= 20, "only " + map.Count + " ContextMap entries were read");

            foreach (var m in Methods)
            {
                string key = AudioCheckMethods.HelpContext(m);
                Assert.True(map.TryGetValue(key, out var target),
                    "HelpLauncher.ContextMap has no entry for " + key + ", so F1 on " + m
                    + " would open the help's front page");
                Assert.False(string.IsNullOrEmpty(target.Anchor), key + " names no section");

                string md = Path.Combine(HelpPages.MdRoot, target.Page.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(md), key + " opens " + target.Page + ", which does not exist");

                string section = Section(File.ReadAllText(md), target.Anchor);
                Assert.True(section != null,
                    target.Page + " has no heading with the id #" + target.Anchor + " for " + m);
                // The help says the same thing as Ctrl+F1, word for word.
                Assert.Contains(AudioCheckMethods.Description(m), section!, StringComparison.Ordinal);
                // And its heading is the method's name.
                Assert.StartsWith(Lexicon.Get(MethodNameKey(m)) + " {#", section!.TrimStart('#', ' '),
                    StringComparison.Ordinal);
            }
        }

        [Fact]
        public void The_dialog_F1_asks_for_the_selected_method()
        {
            string src = File.ReadAllText(Path.Combine(LeaderSourceScan.RepoRoot(),
                "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.cs"));
            Assert.Contains("HelpLauncher.ShowHelp(AudioCheckMethods.HelpContext(SelectedMethod));", src,
                StringComparison.Ordinal);
            Assert.DoesNotContain("HelpLauncher.ShowHelp(\"CheckYourAudio\")", src, StringComparison.Ordinal);
        }

        // ────────────────────────────────────────────────────────────────
        //  Helpers
        // ────────────────────────────────────────────────────────────────

        private static string MethodNameKey(AudioCheckListenMethods m) => m switch
        {
            AudioCheckListenMethods.ComputerMic => "audio.check.method.computer_mic",
            AudioCheckListenMethods.MicTest => "audio.check.method.mic_test",
            AudioCheckListenMethods.Monitor => "audio.check.method.monitor",
            AudioCheckListenMethods.RecordPlayback => "audio.check.method.record",
            AudioCheckListenMethods.OffAir => "audio.check.method.off_air",
            AudioCheckListenMethods.Loopback => "audio.check.method.loopback",
            _ => throw new ArgumentOutOfRangeException(nameof(m))
        };

        private static readonly Regex MapEntry = new(
            @"\{\s*""(?<key>\w+)""\s*,\s*""pages/(?<page>[^""#]+)\.htm(?:#(?<anchor>[^""]+))?""\s*\}",
            RegexOptions.Compiled);

        private static Dictionary<string, (string Page, string Anchor)> ContextMap()
        {
            string src = File.ReadAllText(Path.Combine(LeaderSourceScan.RepoRoot(), "JJFlexWpf", "HelpLauncher.cs"));
            var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in MapEntry.Matches(src))
                map[m.Groups["key"].Value] = (m.Groups["page"].Value + ".md", m.Groups["anchor"].Value);
            return map;
        }

        /// <summary>
        /// The heading carrying <c>{#anchor}</c> and the text under it, up to the
        /// next heading of any level; null when no heading carries the id.
        /// </summary>
        private static string? Section(string markdown, string anchor)
        {
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            int start = Array.FindIndex(lines, l => l.StartsWith("#", StringComparison.Ordinal)
                && l.TrimEnd().EndsWith("{#" + anchor + "}", StringComparison.Ordinal));
            if (start < 0) return null;
            var body = lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("#", StringComparison.Ordinal));
            return lines[start] + "\n" + string.Join("\n", body);
        }
    }
}
