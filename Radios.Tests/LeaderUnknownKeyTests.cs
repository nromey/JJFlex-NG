using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The message the leader layer speaks when it does not know the key you
    /// pressed, and the narrow stickiness that makes it true (#303).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> "Unknown command. Press H for help." was correct one
    /// keystroke earlier. By the time it finished speaking, the layer had
    /// already exited, so H did whatever H does in the scope the operator was
    /// now standing in. An operator hearing that sentence is already lost, and
    /// following its advice moves them again.
    /// </para>
    /// <para>
    /// <b>The two halves interlock.</b> Without stickiness the message would
    /// have to say "Ctrl+J then H"; with it, plain "H" is right again. So the
    /// fix makes the sentence SHORTER. That is why both halves are checked
    /// here, in one file: either alone is wrong.
    /// </para>
    /// <para>
    /// <b>Name the keystroke, not the glyph.</b> Noel, 2026-08-27: "sometimes
    /// that won't speak if punctuation is not high." A literal "?" may not be
    /// voiced AT ALL at a low punctuation level, so an instruction can silently
    /// lose the very key it names. It is the better wording at high punctuation
    /// too, because "Shift slash" is what the hands do while "question mark"
    /// names a character and leaves the operator to work out how to make it.
    /// This is a convention, not a one-string fix, and the sweep at the bottom
    /// is what keeps it one.
    /// </para>
    /// <para>
    /// The source-scanning checks read text rather than loading types, in the
    /// <see cref="LeaderLayerConsistencyTests"/> family: Radios.Tests cannot
    /// load the WPF assembly, and the thing being verified is literal code
    /// written by people.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class LeaderUnknownKeyTests
    {
        private const string KeyCommandsFile = "JJFlexWpf/KeyCommands.cs";
        private const string KeyInventoryFile = "JJFlexWpf/KeyInventory.cs";

        // ────────────────────────────────────────────────────────────────
        //  Prove the instruments before trusting their silence
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_method_scanner_returns_a_body_and_stops_at_its_end()
        {
            // Without this, every "does not contain" below could be passing on
            // an empty string — the silent-success shape this project keeps
            // finding.
            const string sample = @"
    private bool Wanted(Keys k)
    {
        Marker();
    }

    private bool Other(Keys k)
    {
        Elsewhere();
    }
";
            string body = MethodBody(sample, "private bool Wanted(Keys k)");
            Assert.Contains("Marker()", body);
            Assert.DoesNotContain("Elsewhere()", body);
        }

        [Fact]
        public void The_glyph_sweep_can_actually_see_a_violation()
        {
            // The sweep at the bottom reports nothing today. Prove it would
            // report something, or "nothing found" means nothing.
            Assert.True(NamesTheGlyph("Press question mark for the list."));
            Assert.True(NamesTheGlyph("press ? for help"));
            Assert.False(NamesTheGlyph("Delete it?"));
            Assert.False(NamesTheGlyph("Press Shift slash for the list."));
        }

        // ────────────────────────────────────────────────────────────────
        //  The wording, exactly as approved
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_unknown_key_message_is_a_ladder_at_both_tiers()
        {
            Lexicon.Forget();
            Lexicon.Load(Lexicon.Partitions);

            Assert.Equal(
                "Unknown key. H for the list, Escape to cancel.",
                Lexicon.Get("leader.unknown_key", VerbosityLevel.Terse));

            Assert.Equal(
                "Unknown key. Press H for the list of JJ key commands, or slash for the JJ key explorer. Escape to cancel.",
                Lexicon.Get("leader.unknown_key", VerbosityLevel.Chatty));
        }

        [Fact]
        public void The_message_says_unknown_key_and_not_unknown_command()
        {
            // Noel's word, and a deliberate distinction. A key was not
            // recognised; no command was involved. The near-miss (#206) owns
            // "is not a command" for the other case, where we DO know what the
            // operator probably meant. Two situations, two vocabularies.
            Lexicon.Forget();
            Lexicon.Load(Lexicon.Partitions);

            foreach (var level in new[] { VerbosityLevel.Critical, VerbosityLevel.Terse, VerbosityLevel.Chatty })
            {
                string text = Lexicon.Get("leader.unknown_key", level);
                Assert.StartsWith("Unknown key.", text, StringComparison.Ordinal);
                Assert.DoesNotContain("Unknown command", text, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void Every_tier_names_both_ways_out()
        {
            // When the sentence is spoken it is the only thing telling an
            // operator the layer is still listening. A tier that dropped
            // Escape would leave them in a mode with no stated exit — the
            // stuck-modal shape exactly. Since #528 the sentence is spoken
            // at Chatty, and below Chatty only when the thunk cannot sound
            // (earcons off) — so the lower tiers are not dead text, they are
            // the fallback, and they must still name both doors.
            Lexicon.Forget();
            Lexicon.Load(Lexicon.Partitions);

            foreach (var level in new[] { VerbosityLevel.Critical, VerbosityLevel.Terse, VerbosityLevel.Chatty })
            {
                string text = Lexicon.Get("leader.unknown_key", level);
                Assert.Contains("H", text, StringComparison.Ordinal);
                Assert.Contains("Escape", text, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void The_message_never_names_the_glyph()
        {
            Lexicon.Forget();
            Lexicon.Load(Lexicon.Partitions);

            foreach (var level in new[] { VerbosityLevel.Critical, VerbosityLevel.Terse, VerbosityLevel.Chatty })
            {
                string text = Lexicon.Get("leader.unknown_key", level);
                Assert.DoesNotContain("?", text, StringComparison.Ordinal);
                Assert.DoesNotContain("question mark", text, StringComparison.OrdinalIgnoreCase);
            }

            // And the verbose tier names the keystroke it replaced it with —
            // the slash key, which is the explorer's door now (#519).
            Assert.Contains("slash",
                Lexicon.Get("leader.unknown_key", VerbosityLevel.Chatty), StringComparison.Ordinal);
        }

        [Fact]
        public void The_leader_vocabulary_left_the_settings_partition()
        {
            // The leader is a way of driving the whole application from any
            // focus position. Filing its words under settings made it look like
            // a Settings feature to everybody who went looking for them.
            Lexicon.Forget();
            Lexicon.Load(Lexicon.Partitions);

            Assert.Contains(Lexicon.Leader, Lexicon.Partitions);
            Assert.Contains(Lexicon.Leader, Lexicon.EagerPartitions);

            foreach (string key in new[] { "leader.armed", "leader.cancelled", "leader.near_miss", "leader.unknown_key" })
                Assert.True(Lexicon.Contains(key), key + " is missing from the leader partition");

            var strays = Lexicon.Keys
                .Where(k => k.StartsWith("settings.leader.", StringComparison.Ordinal))
                .ToList();
            Assert.True(strays.Count == 0,
                "leader strings left behind in the settings partition: " + string.Join(", ", strays));
        }

        // ────────────────────────────────────────────────────────────────
        //  The stickiness the wording depends on
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_unknown_key_arm_leaves_the_layer_armed_and_the_near_miss_does_not()
        {
            // Stickiness follows the sentence that earns it. The near-miss
            // names a chord to retry and says nothing about help, so a layer
            // left armed there would be a mode the operator was never told
            // about — which is the trap the narrow design exists to avoid.
            string body = MethodBody(Source(KeyCommandsFile), "private bool DoLeaderCommand(Keys k)");

            int arms = Occurrences(body, "_leaderHelpArmed = true");
            Assert.True(arms == 1,
                $"expected exactly one arming site in DoLeaderCommand, found {arms}");

            int nearMissAt = body.IndexOf("leader.near_miss", StringComparison.Ordinal);
            int unknownAt = body.IndexOf("leader.unknown_key", StringComparison.Ordinal);
            int armAt = body.IndexOf("_leaderHelpArmed = true", StringComparison.Ordinal);

            Assert.True(nearMissAt >= 0 && unknownAt >= 0, "both arms must still be present");
            Assert.True(armAt > nearMissAt,
                "the layer is armed in the near-miss branch, which never mentions help");
            Assert.True(armAt < unknownAt,
                "the layer must be armed before the unknown-key message is spoken, in that branch");
        }

        // ────────────────────────────────────────────────────────────────
        //  #528 — below Chatty the thunk is the answer; the words are the
        //  fallback for when it cannot sound. Ruled by Noel 2026-09-02.
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_near_miss_is_a_ladder_and_only_chatty_names_the_alternative()
        {
            // Naming what to press instead is a hint. Terse is values and
            // transitions, not hints — so the fallback tiers say only that
            // the chord is not a command, and the recovery lives at Chatty.
            Lexicon.Forget();
            Lexicon.Load(Lexicon.Partitions);

            var args = new (string, object?)[] { ("pressed", "Ctrl+G"), ("alt", "G"), ("what", "Arm or disarm the TX test tone") };
            Assert.Equal("Ctrl+G is not a command. G: Arm or disarm the TX test tone",
                Lexicon.Get("leader.near_miss", VerbosityLevel.Chatty, args));
            Assert.Equal("Ctrl+G is not a command.",
                Lexicon.Get("leader.near_miss", VerbosityLevel.Terse, args));
            Assert.Equal(
                Lexicon.Get("leader.near_miss", VerbosityLevel.Terse, args),
                Lexicon.Get("leader.near_miss", VerbosityLevel.Critical, args));
        }

        [Fact]
        public void Below_chatty_the_thunk_gates_both_sentences_and_neither_gates_the_arm()
        {
            // Read the dispatcher rather than trusting the description of it.
            // Three things must be true of the default arm, and each is the
            // kind that a tidy-up could break without a compile error:
            //  - the gate is asked ONCE, from the shared rule, so the two
            //    branches cannot drift into two verbosity policies;
            //  - both sentences sit behind it;
            //  - the arming does NOT — verbosity changes what is said, never
            //    what happens, or H after a thunk would mean one thing at
            //    Chatty and another at Terse.
            string body = MethodBody(Source(KeyCommandsFile), "private bool DoLeaderCommand(Keys k)");
            string code = Code(body);

            Assert.Equal(1, Occurrences(code, "RefusalVoice.ToneStandsAlone("));
            Assert.Contains("EarconPlayer.IsOn(EarconPlayer.EarconCategory.CommandsAndConfirmations)", code);

            int gateAt = code.IndexOf("RefusalVoice.ToneStandsAlone(", StringComparison.Ordinal);
            int nearMissAt = code.IndexOf("TryFindLeaderNearMiss", StringComparison.Ordinal);
            Assert.True(gateAt < nearMissAt, "the gate must be computed before the branches that use it");

            // Two guarded Speak calls, one per branch, and the arm between
            // them is not inside either guard: it precedes the second guard
            // and follows the first branch's guard.
            int firstGuard = code.IndexOf("if (!toneStandsAlone)", gateAt, StringComparison.Ordinal);
            int armAt = code.IndexOf("_leaderHelpArmed = true", StringComparison.Ordinal);
            int secondGuard = code.IndexOf("if (!toneStandsAlone)", armAt, StringComparison.Ordinal);
            Assert.True(firstGuard > gateAt && firstGuard < armAt, "the near-miss sentence must be behind the gate");
            Assert.True(secondGuard > armAt, "the unknown-key sentence must be behind the gate, after the arm");
            Assert.Equal(2, Occurrences(code, "if (!toneStandsAlone)"));
        }

        [Fact]
        public void The_help_page_says_the_thunk_is_the_answer_below_chatty()
        {
            // The behaviour is only discoverable if the help says so — an
            // operator at Terse who hears a thunk and nothing else must be
            // able to read why. Drift here is the project's dominant defect.
            string page = Source("docs/help/md/leader-key.md");
            Assert.Contains("Terse", page);
            Assert.Contains("H for the list, Escape to cancel", page);
            Assert.Contains("earcons", page, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Only_H_the_slash_key_and_escape_keep_the_layer_armed()
        {
            string src = Source(KeyCommandsFile);
            string helpKeys = MethodBody(src, "private static bool IsLeaderHelpKey(Keys k)");

            // Both arrival forms of the glyph. "?" is Shift+/ on a US layout
            // and arrives as Oem2|Shift; a bare Oem2 case alone never fires,
            // which is exactly how the advertised "?" sat dead for months
            // (#183).
            Assert.Contains("Keys.Oem2 | Keys.Shift", helpKeys);

            // And NOTHING else. A letter creeping into this predicate is a key
            // the operator would lose to a mode they never asked to stay in, so
            // the set is asserted whole rather than one membership at a time.
            var named = System.Text.RegularExpressions.Regex
                .Matches(helpKeys, @"Keys\.(\w+)")
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(new[] { "H", "Oem2", "Shift" }, named);
        }

        [Fact]
        public void The_help_armed_state_never_dispatches_a_leader_command()
        {
            // The whole safety argument is that the three keys which keep the
            // layer alive all LEAD OUT of it. If this block could reach the
            // dispatcher, the layer would be fully sticky and an operator could
            // be held in it without being told.
            string block = Code(HelpArmedBlock(Source(KeyCommandsFile)));

            Assert.Contains("_leaderHelpArmed = false", block);
            Assert.Contains("LeaderKeyHelp()", block);
            Assert.Contains("OpenKeyExplorer()", block);   // the second door (#519)
            Assert.Contains("LeaderCancel()", block);
            Assert.DoesNotContain("DoLeaderCommand", block);
        }

        [Fact]
        public void The_state_is_dropped_wherever_the_leader_state_is_dropped()
        {
            // Any place that tears down _leaderKeyActive is a place that has
            // decided the layer must stop listening — transmit-time Escape, for
            // one. A sticky flag surviving that teardown would fire later, at a
            // moment nobody could predict.
            string src = Source(KeyCommandsFile);

            int leaderDrops = Occurrences(src, "_leaderKeyActive = false");
            int helpDrops = Occurrences(src, "_leaderHelpArmed = false");

            Assert.True(leaderDrops > 0, "the scan found no leader teardown at all — it is broken");
            Assert.True(helpDrops >= leaderDrops,
                $"{leaderDrops} places drop the leader but only {helpDrops} drop the help-armed state");
        }

        // ────────────────────────────────────────────────────────────────
        //  The convention: name the keystroke, not the glyph
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_help_row_is_spoken_as_a_keystroke_and_shown_as_a_glyph()
        {
            // The display form has to keep the glyph: LeaderChordParser reads
            // it, and the consistency test compares the parsed chords against
            // the dispatcher's switch in both directions. Rewriting the glyph
            // out of KeyDisplay would silently un-advertise Oem2 and disarm the
            // very check that caught the dead "?" (#183). Hence two strings.
            string src = Source(KeyInventoryFile);

            // The explorer row (#519) carries the glyph, so it carries the
            // spoken form. The H row no longer shares a key with it and needs
            // none of its own.
            Assert.Contains("\"Ctrl+J, /\"", src);
            Assert.Contains("KeySpoken = \"Ctrl+J, slash\"", src);
            Assert.Contains("\"Ctrl+J, H\"", src);
            Assert.Contains("KeySpoken = \"Shift slash\"", src);

            // And the spoken generators must actually reach for it.
            //
            // Presence is checked against the RAW body, because one of these
            // reads the property from inside an interpolated string and the
            // blanker cannot tell that hole from the words around it. Absence
            // is checked against the BLANKED body, or a comment explaining why
            // KeyDisplay is not used here would fail the check that it is not.
            foreach (string signature in new[]
            {
                "public static string LeaderHelpSpeech()",
                "public static string SpeakTextFor(string fieldKey, string fieldLabel, bool modern)",
            })
            {
                string body = MethodBody(src, signature);
                Assert.Contains("SpokenKey", body);
                Assert.DoesNotContain("KeyDisplay", Code(body));
            }
        }

        [Fact]
        public void No_shipped_string_tells_an_operator_to_press_a_glyph()
        {
            // The sweep this convention lives or dies by. It found one on the
            // day it was written — help.tuning.more_keys said "Press question
            // mark for every key on this field" — and a rule with no check
            // behind it decays into a comment.
            //
            // Deliberately narrow: an ordinary question mark ENDING a sentence
            // ("Delete it?") is punctuation, not an instruction, and the app is
            // full of them. Only an instruction to press one is a defect.
            var offenders = new List<string>();
            int examined = 0;

            foreach (string partition in Lexicon.Partitions)
            {
                string resource = "Radios.Lexicon." + partition + ".json";
                using Stream? stream = typeof(Lexicon).Assembly.GetManifestResourceStream(resource);
                Assert.NotNull(stream);
                using var reader = new StreamReader(stream!, Encoding.UTF8);

                foreach (var pair in Lexicon.Parse(reader.ReadToEnd()))
                {
                    foreach (var level in new[] { VerbosityLevel.Critical, VerbosityLevel.Terse, VerbosityLevel.Chatty })
                    {
                        string? text = pair.Value.Resolve(level);
                        if (text == null) continue;
                        examined++;
                        if (NamesTheGlyph(text)) offenders.Add(pair.Key + ": " + text);
                    }
                }
            }

            Assert.True(examined > 1000,
                $"only {examined} strings examined — the sweep is broken, so its silence proves nothing");

            Assert.True(offenders.Count == 0,
                "these tell an operator to press a punctuation key by naming the glyph rather than the "
                + "keystroke; a bare '?' may not be spoken at all at low punctuation: "
                + string.Join(" | ", offenders.Distinct()));
        }

        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Code only: comment and string-literal CONTENT blanked, length
        /// preserved. Shared with the leader consistency checks rather than
        /// re-implemented, which is the defect these tests exist to police.
        /// </summary>
        private static string Code(string source) =>
            LeaderSourceScan.BlankStringsAndComments(source);

        private static bool NamesTheGlyph(string text)
        {
            if (text.Contains("question mark", StringComparison.OrdinalIgnoreCase)) return true;

            // "press ?" in any spacing or quoting, and "the ? key".
            string squashed = text.Replace("\"", "").Replace("'", "");
            return squashed.Contains("press ?", StringComparison.OrdinalIgnoreCase)
                || squashed.Contains("the ? key", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The help-armed block in DoCommand, from its marker comment to the
        /// leader trigger that follows it.
        /// </summary>
        private static string HelpArmedBlock(string source)
        {
            const string start = "=== LEADER HELP-ARMED DISPATCH";
            const string end = "Check for leader key trigger";

            int from = source.IndexOf(start, StringComparison.Ordinal);
            Assert.True(from >= 0, "the help-armed dispatch block was not found in DoCommand");
            int to = source.IndexOf(end, from, StringComparison.Ordinal);
            Assert.True(to > from, "the help-armed block has no end marker after it");
            return source.Substring(from, to - from);
        }

        private static int Occurrences(string haystack, string needle)
        {
            int count = 0, at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }

        private static string MethodBody(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "signature not found: " + signature);

            int open = source.IndexOf('{', at);
            int arrow = source.IndexOf("=>", at, StringComparison.Ordinal);
            if (arrow >= 0 && (open < 0 || arrow < open))
            {
                // Expression-bodied member: everything to the terminating ';'.
                int semi = source.IndexOf(';', arrow);
                Assert.True(semi > arrow, "unterminated expression body: " + signature);
                return source.Substring(at, semi - at + 1);
            }

            Assert.True(open >= 0, "no body for: " + signature);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(at, i - at + 1);
                }
            }
            Assert.Fail("unbalanced braces after: " + signature);
            return "";
        }

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
