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
