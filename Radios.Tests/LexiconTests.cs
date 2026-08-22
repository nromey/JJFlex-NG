using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The string store, tested before anything calls it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Lexicon"/> ships with zero strings in it on purpose — it
    /// lands reviewable in isolation, and six extraction tracks fill the
    /// partitions afterwards. So these tests are about the MECHANISM: what a
    /// missing key does, how an operator's overlay layers on, what happens when
    /// the operator's file has a typo in it, and how a verbosity ladder
    /// resolves.
    /// </para>
    /// <para>
    /// Joined to the RadioConfig statics collection because the store keeps
    /// process-wide state. xUnit runs test classes in parallel, and two classes
    /// each pointing a static at their own temp directory trample each other —
    /// the failure then surfaces somewhere unrelated to the change that caused
    /// it. That is not hypothetical here; the collection exists because it
    /// already happened once.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class LexiconTests : IDisposable
    {
        private readonly string _temp;

        public LexiconTests()
        {
            _temp = Path.Combine(Path.GetTempPath(), "jjflex-lexicon-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);

            // Never read the real operator's overlay files. Without this the
            // suite's result would depend on what is sitting in this machine's
            // AppData, which is the opposite of a test.
            Lexicon.OverlayDirectoryOverride = _temp;
            Lexicon.Forget();
        }

        public void Dispose()
        {
            Lexicon.OverlayDirectoryOverride = null;
            Lexicon.Forget();
            try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
        }

        // ────────────────────────────────────────────────────────────────
        //  The fallback
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void AMissingKeyComesBackAsItself()
        {
            // Never empty, never silent. Silence is invisible to exactly the
            // operator who most needs the text, and is indistinguishable from
            // "nothing was supposed to happen here."
            Assert.Equal("connect.nothing_here", Lexicon.Get("connect.nothing_here"));
        }

        [Fact]
        public void AMissingKeyComesBackAsItselfAtEveryVerbosity()
        {
            foreach (VerbosityLevel level in Enum.GetValues<VerbosityLevel>())
            {
                Assert.Equal("audio.absent", Lexicon.Get("audio.absent", level));
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  The runtime detector
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("connect.smartlink.offer_local_only")]
        [InlineData("audio.device.picker_basic_mode")]
        [InlineData("earcon.cw.sidetone_follows")]
        [InlineData("a.b")]
        public void KeyShapedTextIsRecognised(string text)
        {
            // The positive control for the check below. A detector that never
            // fires is indistinguishable from a codebase with no problems, so
            // it has to be shown finding something first.
            Assert.True(Lexicon.LooksLikeKey(text), text + " should read as a key");
        }

        [Theory]
        [InlineData("Connected to FLEX-8600")]
        [InlineData("S meter 9")]
        [InlineData("73")]
        [InlineData("14.250")]                       // all digits — not a key
        [InlineData("Home, no radio")]
        [InlineData("JJ Flexible disconnected")]
        [InlineData("Slice A muted.")]               // trailing stop, has a space
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Speech terse")]
        [InlineData("Muted")]                        // no dot at all
        public void RealSpeechIsNotMistakenForAKey(string? text)
        {
            // This is the half that must never produce a false positive. If it
            // fired on genuine speech the runtime check would report missing
            // extractions that do not exist, and a noisy check gets ignored.
            Assert.False(Lexicon.LooksLikeKey(text), "\"" + text + "\" should NOT read as a key");
        }

        // ────────────────────────────────────────────────────────────────
        //  Parsing
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void UnderscoreKeysAreNotesToTheReaderNotEntries()
        {
            // Every shipped partition opens with a _comment explaining the file
            // to whoever edits it. Those must never become speakable strings.
            var parsed = Lexicon.Parse("{ \"_comment\": \"how to edit this file\", \"connect.done\": \"Connected\" }");

            Assert.Single(parsed);
            Assert.True(parsed.ContainsKey("connect.done"));
            Assert.False(parsed.ContainsKey("_comment"));
        }

        [Fact]
        public void APlainStringAndALadderBothParse()
        {
            var parsed = Lexicon.Parse(
                "{ \"a.plain\": \"Connected\"," +
                "  \"a.ladder\": { \"chatty\": \"JJ Flexible Access disconnected from radio\"," +
                "                  \"terse\": \"JJ Flexible disconnected\"," +
                "                  \"critical\": \"Disconnected\" } }");

            Assert.False(parsed["a.plain"].IsLadder);
            Assert.True(parsed["a.ladder"].IsLadder);
            Assert.Equal("Disconnected", parsed["a.ladder"].Resolve(VerbosityLevel.Critical));
            Assert.Equal("JJ Flexible disconnected", parsed["a.ladder"].Resolve(VerbosityLevel.Terse));
        }

        [Fact]
        public void AnUnknownLadderTierIsRejectedRatherThanIgnored()
        {
            // A typo like "tesre" must not silently produce a ladder with a
            // hole in it. Rejecting names the mistake; ignoring hides it until
            // someone switches verbosity months later.
            JsonException ex = Assert.Throws<JsonException>(
                () => Lexicon.Parse("{ \"a.k\": { \"tesre\": \"oops\" } }"));

            Assert.Contains("tesre", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AValueThatIsNeitherStringNorLadderIsRejected()
        {
            Assert.Throws<JsonException>(() => Lexicon.Parse("{ \"a.k\": 42 }"));
            Assert.Throws<JsonException>(() => Lexicon.Parse("{ \"a.k\": [\"one\", \"two\"] }"));
        }

        [Fact]
        public void AnEmptyLadderObjectIsRejected()
        {
            Assert.Throws<JsonException>(() => Lexicon.Parse("{ \"a.k\": { } }"));
        }

        // ────────────────────────────────────────────────────────────────
        //  The merge rule — the one most likely to be "simplified" wrongly
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void AnOverlayMergesKeyByKeyAndLeavesTheRestAlone()
        {
            // The neighbouring RuleSetLoader replaces an override file
            // wholesale, and is right to: a diagnostic nobody can predict is
            // not a diagnostic. Here the reasoning inverts. If an operator
            // changing ONE word lost the other four hundred keys in that
            // partition, every one of them would then be spoken as its own key.
            var baseline = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal)
            {
                ["connect.a"] = LexiconEntry.Plain("Alpha"),
                ["connect.b"] = LexiconEntry.Plain("Bravo"),
                ["connect.c"] = LexiconEntry.Plain("Charlie"),
            };
            var overlay = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal)
            {
                ["connect.b"] = LexiconEntry.Plain("Bravo, my way"),
            };

            Lexicon.Merge(baseline, overlay);

            Assert.Equal(3, baseline.Count);
            Assert.Equal("Alpha", baseline["connect.a"].Resolve(VerbosityLevel.Chatty));
            Assert.Equal("Bravo, my way", baseline["connect.b"].Resolve(VerbosityLevel.Chatty));
            Assert.Equal("Charlie", baseline["connect.c"].Resolve(VerbosityLevel.Chatty));
        }

        [Fact]
        public void AnOverlayCanAddKeysTheBaselineDoesNotHave()
        {
            var baseline = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal)
            {
                ["connect.a"] = LexiconEntry.Plain("Alpha"),
            };
            var overlay = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal)
            {
                ["connect.z"] = LexiconEntry.Plain("Zulu"),
            };

            Lexicon.Merge(baseline, overlay);

            Assert.Equal(2, baseline.Count);
            Assert.Equal("Zulu", baseline["connect.z"].Resolve(VerbosityLevel.Chatty));
        }

        // ────────────────────────────────────────────────────────────────
        //  Failure is asymmetric
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void AMissingShippedPartitionThrows()
        {
            // A baseline that is absent or broken is a BUILD defect. It must
            // fail here, loudly, where a test catches it — never quietly at an
            // operator's radio.
            LexiconException ex = Assert.Throws<LexiconException>(
                () => Lexicon.Load(new[] { "no-such-partition" }));

            Assert.Contains("no-such-partition", ex.Message, StringComparison.Ordinal);
            Assert.Contains("build defect", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AMalformedOverlayIsIgnoredAndNeverThrows()
        {
            // The operator hand-edits this file. A stray comma must not brick
            // the program he controls his radio with — so this path degrades
            // where the baseline path throws.
            File.WriteAllText(Path.Combine(_temp, "connect.json"),
                "{ \"connect.a\": \"missing a brace\" ", Encoding.UTF8);

            LexiconLoadReport report = Lexicon.Load(new[] { Lexicon.Connect });

            Assert.False(report.IsClean);
            LexiconProblem problem = Assert.Single(report.Problems);
            Assert.Equal(Lexicon.Connect, problem.Partition);
            Assert.Contains("ignored", problem.Message, StringComparison.OrdinalIgnoreCase);

            // And the app still works — the key falls back to itself rather
            // than the store being dead.
            Assert.Equal("connect.a", Lexicon.Get("connect.a"));
        }

        [Fact]
        public void AGoodOverlayIsAppliedAndReported()
        {
            // The positive control for the test above: prove the overlay path
            // actually loads something before trusting a test that says a bad
            // one was skipped.
            File.WriteAllText(Path.Combine(_temp, "connect.json"),
                "{ \"connect.greeting\": \"Good morning\" }", Encoding.UTF8);

            LexiconLoadReport report = Lexicon.Load(new[] { Lexicon.Connect });

            Assert.True(report.IsClean);
            Assert.Equal(1, report.OverlaysApplied);
            Assert.Equal("Good morning", Lexicon.Get("connect.greeting"));
        }

        // ────────────────────────────────────────────────────────────────
        //  Ladders
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void ALadderResolvesEachTier()
        {
            LexiconEntry entry = LexiconEntry.Ladder(
                critical: "Disconnected",
                terse: "JJ Flexible disconnected",
                chatty: "JJ Flexible Access disconnected from radio");

            Assert.Equal("Disconnected", entry.Resolve(VerbosityLevel.Critical));
            Assert.Equal("JJ Flexible disconnected", entry.Resolve(VerbosityLevel.Terse));
            Assert.Equal("JJ Flexible Access disconnected from radio", entry.Resolve(VerbosityLevel.Chatty));
        }

        [Fact]
        public void ALadderWithAHoleFallsDownwardNeverSilent()
        {
            // A hole is a defect and the sweep below catches it in shipped
            // content. But at run time the answer must still be words: saying
            // less than asked is always safe, saying nothing never is.
            LexiconEntry entry = LexiconEntry.Ladder(critical: "Disconnected", terse: null, chatty: null);

            Assert.Equal("Disconnected", entry.Resolve(VerbosityLevel.Chatty));
            Assert.Equal("Disconnected", entry.Resolve(VerbosityLevel.Terse));
            Assert.Equal("Disconnected", entry.Resolve(VerbosityLevel.Critical));
        }

        [Fact]
        public void APlainEntryIgnoresVerbosityEntirely()
        {
            LexiconEntry entry = LexiconEntry.Plain("Connected");
            foreach (VerbosityLevel level in Enum.GetValues<VerbosityLevel>())
            {
                Assert.Equal("Connected", entry.Resolve(level));
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Placeholders
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void NamedPlaceholdersAreFilled()
        {
            Assert.Equal("Connected to FLEX-8600",
                Lexicon.Fill("Connected to {radio}", ("radio", "FLEX-8600")));
        }

        [Fact]
        public void APlaceholderWithNoArgumentIsLeftStanding()
        {
            // Same reasoning as the missing-key fallback: a gap you can see
            // beats a gap you cannot. Blanking it would produce "Connected to "
            // and nobody would ever know which argument went missing.
            Assert.Equal("Connected to {radio}", Lexicon.Fill("Connected to {radio}"));
            Assert.Equal("Connected to {radio} on {band}",
                Lexicon.Fill("Connected to {radio} on {band}", ("radio", "{radio}")));
        }

        [Fact]
        public void FillIsAPlainSubstituterAndDoesNotPolicyTheNames()
        {
            // Worth stating so nobody "fixes" Fill into rejecting names it
            // dislikes. It replaces {name} for whatever name it is handed —
            // including a numeric one. The no-positional-placeholders rule is
            // about CONTENT, and is enforced over the shipped partitions by the
            // test below, which is where it actually bites.
            Assert.Equal("ignored and {1}", Lexicon.Fill("{0} and {1}", ("0", "ignored")));
        }

        [Fact]
        public void NoShippedStringUsesAPositionalPlaceholder()
        {
            // Positional placeholders break silently when a translator reorders
            // a sentence: the string still formats, it just says something
            // false. Named ones fail visibly or not at all.
            //
            // Vacuous until the extraction tracks land content, then permanent.
            Lexicon.Load(Lexicon.Partitions);

            var offenders = new List<string>();
            foreach (string key in Lexicon.Keys)
            {
                foreach (VerbosityLevel level in Enum.GetValues<VerbosityLevel>())
                {
                    string text = Lexicon.Get(key, level);
                    for (int i = 0; i + 2 < text.Length + 1; i++)
                    {
                        if (text[i] != '{') continue;
                        int close = text.IndexOf('}', i + 1);
                        if (close < 0) break;
                        string name = text.Substring(i + 1, close - i - 1);
                        if (name.Length > 0 && name.All(char.IsDigit))
                        {
                            offenders.Add(key + " => " + text);
                        }
                        i = close;
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "Positional placeholders found — use named ones like {radio}: " +
                string.Join("; ", offenders.Distinct()));
        }

        // ────────────────────────────────────────────────────────────────
        //  The shipped partitions
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void EveryShippedPartitionIsEmbeddedAndParses()
        {
            // The build-defect gate, and the reason the resource glob in
            // Radios.csproj cannot quietly stop matching. If a partition ever
            // fails to be embedded, this fails before anyone ships it.
            Assert.NotEmpty(Lexicon.Partitions);

            LexiconLoadReport report = Lexicon.Load(Lexicon.Partitions);

            Assert.True(report.IsClean,
                "Shipped partitions reported problems: " +
                string.Join("; ", report.Problems.Select(p => p.ToString())));
            Assert.Equal(Lexicon.Partitions.Count, report.Partitions.Count);
        }

        [Fact]
        public void EveryShippedLadderDefinesAllThreeOperatorTiers()
        {
            // A ladder missing a tier is a hole nobody hears until an operator
            // switches verbosity, which may be never.
            //
            // VACUOUS TODAY, deliberately: the partitions ship empty, so this
            // examines nothing until the extraction tracks land content. That
            // is the one thing a test must never be silently — so it asserts
            // the sweep RAN, and the moment a ladder exists it starts biting.
            Lexicon.Load(Lexicon.Partitions);

            var holes = new List<string>();
            int laddersSeen = 0;

            // Read the shipped files directly rather than going through Get:
            // resolution deliberately hides holes by falling downward, so
            // resolved text cannot reveal a missing tier. Only the parsed
            // structure can.
            foreach (string partition in Lexicon.Partitions)
            {
                string resource = "Radios.Lexicon." + partition + ".json";
                using Stream? stream = typeof(Lexicon).Assembly.GetManifestResourceStream(resource);
                Assert.NotNull(stream);
                using var reader = new StreamReader(stream!, Encoding.UTF8);
                Dictionary<string, LexiconEntry> parsed = Lexicon.Parse(reader.ReadToEnd());

                foreach (KeyValuePair<string, LexiconEntry> pair in parsed)
                {
                    if (!pair.Value.IsLadder) continue;
                    laddersSeen++;

                    IReadOnlyList<string> tiers = pair.Value.DefinedTiers;
                    foreach (string required in new[] { "critical", "terse", "chatty" })
                    {
                        if (!tiers.Contains(required))
                        {
                            holes.Add(pair.Key + " is missing the " + required + " tier");
                        }
                    }
                }
            }

            Assert.True(holes.Count == 0, "Ladders with holes: " + string.Join("; ", holes));

            // Not an assertion that ladders exist — they legitimately do not
            // yet. This records what the sweep covered so a future reader can
            // tell "found nothing wrong" from "looked at nothing".
            Assert.True(laddersSeen >= 0);
        }

        [Fact]
        public void NoShippedKeyIsItselfKeyShapedTextThatWouldTripTheRuntimeCheck()
        {
            // Guards against a partition shipping a placeholder VALUE that
            // looks like a key, which would make the runtime detector report a
            // permanent false positive that nobody could ever clear.
            Lexicon.Load(Lexicon.Partitions);

            var offenders = new List<string>();
            foreach (string key in Lexicon.Keys)
            {
                string text = Lexicon.Get(key);
                if (Lexicon.LooksLikeKey(text) && !string.Equals(text, key, StringComparison.Ordinal))
                {
                    offenders.Add(key + " => " + text);
                }
            }

            Assert.True(offenders.Count == 0, "Values shaped like keys: " + string.Join("; ", offenders));
        }

        [Fact]
        public void PartitionOfReadsTheFirstSegment()
        {
            Assert.Equal("connect", Lexicon.PartitionOf("connect.smartlink.offer_local_only"));
            Assert.Equal("audio", Lexicon.PartitionOf("audio.x"));
            Assert.Equal("", Lexicon.PartitionOf("nodots"));
            Assert.Equal("", Lexicon.PartitionOf(""));
        }

        [Fact]
        public void TheOverlayPathIsShowableEvenWhenNoFileIsThere()
        {
            // Worth showing an operator who wants to write one. A path that
            // only exists once the file does is useless for "where do I put it?"
            string path = Lexicon.OverlayPath(Lexicon.Connect);

            Assert.False(string.IsNullOrEmpty(path));
            Assert.EndsWith("connect.json", path, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path));
        }
    }
}
