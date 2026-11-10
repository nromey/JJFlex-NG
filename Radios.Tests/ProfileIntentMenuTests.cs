#nullable enable

using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The three profile intents under Radio, Profiles on This Radio, are one
    /// answer out of three, so they are a radio group: the checkmark is the
    /// state and the row text is never rewritten (#680, defect 1). They went in
    /// through <c>AddChecked</c>, whose state-suffix rewrite made the reader say
    /// "Use My Transmit Audio Here: On" — a toggle, in words, that would not
    /// toggle off. <c>AddRadioChecked</c> registers the row in the group the
    /// rewrite skips.
    /// </summary>
    /// <remarks>
    /// A source pin: the menu is native Win32 built at run time and nothing
    /// here can open it. What it pins is the one line that decides the words.
    /// </remarks>
    public sealed class ProfileIntentMenuTests
    {
        private static readonly string[] Intents =
        {
            "Use My Transmit Audio Here",
            "Load All My Profiles Here",
            "Leave This Radio's Profiles Alone",
            // The fourth, back to unanswered (#680, defect 2).
            "Ask Me at the Next Connect",
        };

        private static string MenuSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
                dir = dir.Parent;
            Assert.True(dir != null, "Could not find the repository root above " + AppContext.BaseDirectory);
            string path = Path.Combine(dir!.FullName, "JJFlexWpf", "NativeMenuBar.cs");
            Assert.True(File.Exists(path), "NativeMenuBar.cs is not where this test reads it.");
            return File.ReadAllText(path);
        }

        [Fact]
        public void The_three_profile_intents_are_a_radio_group_and_never_get_an_on_or_off_suffix()
        {
            string src = MenuSource();
            foreach (string intent in Intents)
            {
                string label = Regex.Escape("\"" + intent + "\"");
                var calls = Regex.Matches(src, @"(\w+)\(\s*profilesSub\s*,\s*" + label);
                Assert.True(calls.Count == 1,
                    $"Expected the intent \"{intent}\" to be added to the profiles submenu exactly once; found {calls.Count}. "
                    + "The pin is not reading the menu it was written for.");
                Assert.Equal("AddRadioChecked", calls[0].Groups[1].Value);
            }
        }

        /// <summary>
        /// The profile names under Which Transmit Audio are one choice out of
        /// the list, so they are a radio group too: through AddChecked each
        /// was spoken with ": On" or ": Off".
        /// </summary>
        [Fact]
        public void The_transmit_audio_profile_names_are_a_radio_group()
        {
            string src = MenuSource();
            var calls = Regex.Matches(src, @"(\w+)\(\s*whichSub\s*,\s*n\s*,\s*\(\)\s*=>\s*SetGuestTransmitAudioProfile\(n\)");
            Assert.True(calls.Count == 1,
                $"Expected one row per profile name under Which Transmit Audio; found {calls.Count} calls. "
                + "The pin is not reading the menu it was written for.");
            Assert.Equal("AddRadioChecked", calls[0].Groups[1].Value);
        }

        /// <summary>
        /// #680, defect 3: "Use My Transmit Audio Here" could be armed with no
        /// transmit-audio profile chosen. It is greyed whenever none is chosen,
        /// including when none exists, with the reason in its label, and stays
        /// in the radio group.
        /// </summary>
        [Fact]
        public void Use_my_transmit_audio_is_greyed_with_its_reason_until_a_profile_is_chosen()
        {
            string src = MenuSource();
            int at = src.IndexOf("AddRadioChecked(profilesSub, \"Use My Transmit Audio Here\"", StringComparison.Ordinal);
            Assert.True(at >= 0, "the Use My Transmit Audio row has moved");
            int end = src.IndexOf(");", at, StringComparison.Ordinal);
            string call = src.Substring(at, end - at);
            Assert.Contains("enabled: useMineNotYet == null", call);
            Assert.Contains("(useMineNotYet ?? \"\")", call);   // the reason is in the label

            int decl = src.LastIndexOf("string? useMineNotYet", at, StringComparison.Ordinal);
            Assert.True(decl >= 0, "the reason is no longer worked out before the row");
            string why = src.Substring(decl, at - decl);
            // Repair one: the choice is checked against the profiles still
            // saved, through one decision the WPF tests drive with a real
            // store (JunkboxRepairOneTests); its three reasons are pinned there.
            Assert.Contains("UseMyTransmitAudioNotYet(Rig.LocalTransmitAudioProfileChoice, presetNames)", why);
        }

        /// <summary>
        /// A greyed member of a radio group is still marked when it is the
        /// answer: the popup refresh skips greyed rows, so the mark is set when
        /// the row is built.
        /// </summary>
        [Fact]
        public void A_greyed_group_member_still_carries_its_mark()
        {
            string src = MenuSource();
            int at = src.IndexOf("private void AddRadioChecked(", StringComparison.Ordinal);
            Assert.True(at >= 0);
            int end = src.IndexOf("// ── Verbs that are not there yet", at, StringComparison.Ordinal);
            Assert.True(end > at, "the code after AddRadioChecked has moved; re-point this read");
            string body = src.Substring(at, end - at);
            Assert.Contains("if (!enabled)", body);
            Assert.Contains("if (stateGetter()) CheckMenuItem(popup, (uint)id, MF_BYCOMMAND | MF_CHECKED);", body);
            Assert.Matches(@"if \(!enabled\) continue;", src);   // positive control: the refresh does skip greyed rows
        }

        /// <summary>
        /// The control: the helper the intents must use really is the one
        /// whose rows the suffix rewrite skips, so the pin above is pinning
        /// the behaviour and not just a name.
        /// </summary>
        [Fact]
        public void AddRadioChecked_registers_the_row_the_suffix_rewrite_skips()
        {
            string src = MenuSource();
            Assert.Matches(@"private void AddRadioChecked\([^)]*\)\s*\{[^}]*_radioGroupItems\.Add\(", src);
            Assert.Matches(@"if \(_radioGroupItems\.Contains\(id\)\) continue;", src);
        }
    }
}
