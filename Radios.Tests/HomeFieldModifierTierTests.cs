using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// A Home field handler's <c>ch</c> is never a letter or digit while a
    /// modifier is held (#546). So a <c>ch == 'M'</c> test ANDed with a
    /// Shift check is dead code, and the binding it names silently stops
    /// working — the Alt+L shape again: statically perfect, and dead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a source scan rather than a unit test.</b> The rule itself is
    /// asserted in <c>JJFlexWpf.Tests.HomeFieldChordTests</c>, over the whole
    /// key enum. What that cannot see is the next author writing the old
    /// idiom — <c>if (ch == 'M' &amp;&amp; Keyboard.Modifiers ==
    /// ModifierKeys.Shift)</c> was the house pattern for a Shift chord in
    /// five places until 2026-09-05, so it is exactly what someone will copy.
    /// Under the invariant that branch can never be taken. Nothing fails:
    /// the file compiles, the Roslyn field-key scan reads it as a Shift+M
    /// claim and finds it declared, and the chord is dead. Same reasoning and
    /// same idiom as <c>ValueLayerSelectKeyUniquenessTests</c>, which reads
    /// the real file because a test double models the intended design and
    /// cannot catch the shipped one diverging from it.
    /// </para>
    /// <para>
    /// <b>How a field's own Shift chord is written now:</b> by KEY —
    /// <c>key == Key.M &amp;&amp; Keyboard.Modifiers == ModifierKeys.Shift</c>
    /// — which is how the Shift+Comma guard beside it was always written.
    /// </para>
    /// </remarks>
    public sealed class HomeFieldModifierTierTests
    {
        private const string HandlersFile = "JJFlexWpf/FreqOutHandlers.cs";

        // `ch` compared to a character literal, any comparison operator.
        private static readonly Regex CharTest = new Regex(
            @"\bch\s*(==|!=|>=|<=|<|>)\s*'", RegexOptions.Compiled);

        private static readonly Regex ShiftTest = new Regex(
            @"ModifierKeys\s*\.\s*Shift\b", RegexOptions.Compiled);

        [Fact]
        public void No_handler_tests_a_character_together_with_Shift()
        {
            var dead = DeadShiftGuards(Source(HandlersFile));

            Assert.True(dead.Count == 0,
                HandlersFile + " tests `ch` against a character in the same condition as a Shift check. "
                + "KeyToChar returns no character while Shift is held, so these branches can never be "
                + "taken and the chords they name are dead. Test the raw key instead "
                + "(`key == Key.M && Keyboard.Modifiers == ModifierKeys.Shift`):"
                + Environment.NewLine + string.Join(Environment.NewLine, dead));
        }

        /// <summary>
        /// Positive control: the scanner must FIND the dead shape when it is
        /// there, in both orders and in the bit-test idiom, or an empty result
        /// above measures nothing.
        /// </summary>
        [Fact]
        public void The_scanner_finds_the_dead_shape_when_it_is_there()
        {
            const string sample = @"
class FreqOutHandlers
{
    void Alpha(KeyEventArgs e)
    {
        char ch = KeyToChar(e);
        if (ch == 'M' && Keyboard.Modifiers == ModifierKeys.Shift) { MuteAll(); }
        if (Keyboard.Modifiers == ModifierKeys.Shift
            && ch == 'S') { Speak(); }
        if (ch >= 'A' && ch <= 'H' && (Keyboard.Modifiers & ModifierKeys.Shift) != 0) { Jump(); }
        if (key == Key.M && Keyboard.Modifiers == ModifierKeys.Shift) { Fine(); }
        if (ch == 'P' && Keyboard.Modifiers == ModifierKeys.None) { AlsoFine(); }
    }
}";
            var dead = DeadShiftGuards(sample);

            Assert.Equal(3, dead.Count);
            Assert.Contains(dead, d => d.Contains("ch == 'M'", StringComparison.Ordinal));
            Assert.Contains(dead, d => d.Contains("ch == 'S'", StringComparison.Ordinal));
            Assert.Contains(dead, d => d.Contains("ch >= 'A'", StringComparison.Ordinal));
            Assert.DoesNotContain(dead, d => d.Contains("Fine()", StringComparison.Ordinal));
        }

        /// <summary>
        /// The rule the scan above relies on is still where it says it is. If
        /// somebody narrows <c>CharFor</c> back to blanking Alt alone, the
        /// Shift-guard scan becomes wrong in the other direction — it would
        /// forbid the only guard that still worked — so the two are pinned
        /// together.
        /// </summary>
        [Fact]
        public void The_general_rule_is_still_in_CharFor()
        {
            string source = Source(HandlersFile);
            int at = source.IndexOf("internal static char CharFor(", StringComparison.Ordinal);
            Assert.True(at >= 0, "CharFor not found in " + HandlersFile
                + " — KeyToChar's pure form has moved or been renamed; move this test with it");

            string body = BodyFrom(source, at);
            Assert.Contains("ModifierKeys.Control", body, StringComparison.Ordinal);
            Assert.Contains("ModifierKeys.Alt", body, StringComparison.Ordinal);
            Assert.Contains("ModifierKeys.Windows", body, StringComparison.Ordinal);
            Assert.Contains("ModifierKeys.Shift", body, StringComparison.Ordinal);
        }

        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every <c>if (...)</c> condition in the source whose text tests
        /// <c>ch</c> against a character AND names <c>ModifierKeys.Shift</c>.
        /// Conditions are read with balanced parentheses, so a condition
        /// wrapped across lines is one condition.
        /// </summary>
        internal static List<string> DeadShiftGuards(string source)
        {
            var found = new List<string>();
            var ifs = new Regex(@"\bif\s*\(", RegexOptions.Compiled);

            foreach (Match m in ifs.Matches(source))
            {
                int open = m.Index + m.Length - 1;
                string condition = Balanced(source, open);
                if (condition.Length == 0) continue;
                if (!CharTest.IsMatch(condition)) continue;
                if (!ShiftTest.IsMatch(condition)) continue;

                int line = source.Take(m.Index).Count(c => c == '\n') + 1;
                found.Add("line " + line + ": if (" + Flatten(condition) + ")");
            }
            return found;
        }

        private static string Balanced(string source, int open)
        {
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '(') depth++;
                else if (source[i] == ')')
                {
                    depth--;
                    if (depth == 0) return source.Substring(open + 1, i - open - 1);
                }
            }
            return "";
        }

        private static string BodyFrom(string source, int at)
        {
            int open = source.IndexOf('{', at);
            Assert.True(open >= 0, "no body found");
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(open, i - open + 1);
                }
            }
            Assert.Fail("unbalanced braces after CharFor");
            return "";
        }

        private static string Flatten(string s)
            => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        private static string Source(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "source not found: " + path);
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
