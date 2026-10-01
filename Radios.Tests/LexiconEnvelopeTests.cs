#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The version-2 entry envelope: what it accepts, what it refuses, and
    /// every promise it makes to the readers that were already here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shape of this file follows the risk.</b> The dangerous failure is
    /// not a parser that rejects something valid — that fails loudly on the
    /// first run. It is a parser that quietly reads a misspelled classification
    /// as a verbosity tier, or a merge that silently drops a safety message's
    /// shelf life, and then ships something that still speaks. Most of what is
    /// below is refusals.
    /// </para>
    /// </remarks>
    // LEXICON_SCANNER_EXEMPT — this file names keys that deliberately do NOT
    // exist, to prove the missing-key fallback still answers rather than
    // throwing. Sweeping it for unresolvable keys would report its own subject
    // as a defect.
    //
    // In the RadioConfig statics collection because it calls Lexicon.Forget()
    // and Lexicon.Load(), which are process-wide. xUnit runs test classes in
    // parallel, so without this it would clear the catalogue underneath another
    // class's sweep and the failure would surface far from its cause.
    [Collection(RadioConfigStaticsCollection.Name)]
    public class LexiconEnvelopeTests
    {
        private static IReadOnlyDictionary<string, LexiconEntry> Parse(string json)
            => LexiconBaseline.Parse(json);

        // ────────────────────────────────────────────────────────────────
        //  Nothing that worked before stops working
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void APlainStringStillParsesAndIsUnclassified()
        {
            var entries = Parse("""{ "a.b": "Hello" }""");

            Assert.Equal("Hello", entries["a.b"].Resolve(VerbosityLevel.Chatty));
            Assert.False(entries["a.b"].IsLadder);

            // Not "defaulted to forgettable", not "assumed safe". Nobody has
            // said, and the entry says so.
            Assert.Equal(DeliveryClassification.Unclassified, entries["a.b"].Classification);
            Assert.Null(entries["a.b"].Delivery);
        }

        [Fact]
        public void ALegacyLadderStillParsesWithEveryTier()
        {
            var entries = Parse("""
                { "a.b": { "critical": "Cut", "terse": "Transmit cut", "chatty": "Transmit was cut" } }
                """);

            LexiconEntry entry = entries["a.b"];
            Assert.True(entry.IsLadder);
            Assert.Equal("Cut", entry.Resolve(VerbosityLevel.Critical));
            Assert.Equal("Transmit cut", entry.Resolve(VerbosityLevel.Terse));
            Assert.Equal("Transmit was cut", entry.Resolve(VerbosityLevel.Chatty));
            Assert.Equal(DeliveryClassification.Unclassified, entry.Classification);
        }

        [Fact]
        public void TheNineShippedLaddersStillResolveAtEveryTierTheyDefine()
        {
            // The positive control for the whole migration. An envelope format
            // that handled only strings would damage behaviour that works
            // today, and this is the behaviour.
            int ladders = 0, tiers = 0;
            foreach (string partition in Lexicon.Partitions)
            {
                foreach (var pair in LexiconBaseline.FromShipped(partition))
                {
                    if (!pair.Value.IsLadder) continue;
                    ladders++;
                    foreach (string tier in pair.Value.DefinedTiers)
                    {
                        tiers++;
                        VerbosityLevel level = tier switch
                        {
                            "critical" => VerbosityLevel.Critical,
                            "terse" => VerbosityLevel.Terse,
                            "diagnostic" => VerbosityLevel.Diagnostic,
                            _ => VerbosityLevel.Chatty,
                        };
                        Assert.False(string.IsNullOrWhiteSpace(pair.Value.Resolve(level)),
                            pair.Key + " resolves to nothing at " + tier);
                    }
                }
            }

            Assert.Equal(9, ladders);
            Assert.True(tiers >= 27, "only " + tiers + " ladder tiers were seen");
        }

        // ────────────────────────────────────────────────────────────────
        //  The envelope
        // ────────────────────────────────────────────────────────────────

        private const string Warning = """
            {
              "a.warning": {
                "text": "Reflected power is high, stopping transmit.",
                "delivery": {
                  "shelfLife": "perishable",
                  "validity": "transmit.active",
                  "historyKey": "a.warning.history",
                  "receipt": "warning"
                }
              },
              "a.warning.history": { "text": "A moment ago, transmit was cut.", "delivery": null }
            }
            """;

        [Fact]
        public void AnEnvelopeCarriesItsWordsAndItsContract()
        {
            var entries = Parse(Warning);
            LexiconEntry entry = entries["a.warning"];

            Assert.Equal("Reflected power is high, stopping transmit.", entry.Resolve(VerbosityLevel.Chatty));
            Assert.Equal(DeliveryClassification.Message, entry.Classification);
            Assert.Equal(ShelfLife.Perishable, entry.Delivery!.ShelfLife);
            Assert.Equal("transmit.active", entry.Delivery.Validity);
            Assert.Equal("a.warning.history", entry.Delivery.HistoryKey);
            Assert.Equal(ReceiptPolicy.Warning, entry.Delivery.Receipt);
        }

        [Fact]
        public void AnExplicitNullDeliveryIsAnAnswerAndNotAnAbsence()
        {
            var entries = Parse(Warning);
            LexiconEntry history = entries["a.warning.history"];

            // The difference this test exists for: somebody LOOKED at this
            // string and said it is not a standalone message. That is not the
            // same as nobody having said anything.
            Assert.Equal(DeliveryClassification.TextOnly, history.Classification);
            Assert.Null(history.Delivery);
            Assert.NotEqual(DeliveryClassification.Unclassified, history.Classification);
        }

        [Fact]
        public void AnEnvelopeMayWrapALadder()
        {
            var entries = Parse("""
                {
                  "a.b": {
                    "text": { "critical": "Hot", "terse": "PA hot", "chatty": "The PA is at 70 degrees" },
                    "delivery": {
                      "shelfLife": "persistent",
                      "validity": "pa.temperature",
                      "historyKey": "a.b.history",
                      "receipt": "warning"
                    }
                  }
                }
                """);

            LexiconEntry entry = entries["a.b"];
            Assert.True(entry.IsLadder);
            Assert.Equal("Hot", entry.Resolve(VerbosityLevel.Critical));
            Assert.Equal(ShelfLife.Persistent, entry.Delivery!.ShelfLife);
        }

        [Fact]
        public void AnEnvelopeWithTextAndNoDeliveryFieldIsStillUnclassified()
        {
            var entries = Parse("""{ "a.b": { "text": "Hello" } }""");

            // Saying nothing in an envelope is the same silence as saying
            // nothing in a bare string, and gets the same answer.
            Assert.Equal(DeliveryClassification.Unclassified, entries["a.b"].Classification);
        }

        // ────────────────────────────────────────────────────────────────
        //  Refusals — the half that matters
        // ────────────────────────────────────────────────────────────────

        private static string Refusal(string json)
        {
            JsonException ex = Assert.Throws<JsonException>(() => Parse(json));
            return ex.Message;
        }

        [Fact]
        public void AMisspelledMetadataFieldIsNeverReadAsAVerbosityTier()
        {
            // The specific quiet failure this detection exists to prevent: an
            // object naming 'delivary' would otherwise fall into the ladder
            // reader, which would report an unknown TIER and send whoever is
            // reading the error looking in the wrong place entirely.
            string message = Refusal("""
                { "a.b": { "text": "Hi", "delivary": { "shelfLife": "forgettable" } } }
                """);

            Assert.Contains("unknown envelope field", message, StringComparison.Ordinal);
            Assert.Contains("delivary", message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnEnvelopeWithoutTextIsRefused()
        {
            string message = Refusal("""{ "a.b": { "delivery": null } }""");
            Assert.Contains("no 'text'", message, StringComparison.Ordinal);
        }

        [Fact]
        public void ADuplicateEnvelopeFieldIsRefusedRatherThanResolvedSilently()
        {
            string message = Refusal("""{ "a.b": { "text": "One", "text": "Two" } }""");
            Assert.Contains("more than once", message, StringComparison.Ordinal);
        }

        [Fact]
        public void ADuplicateLadderTierIsRefusedToo()
        {
            string message = Refusal("""{ "a.b": { "chatty": "One", "chatty": "Two" } }""");
            Assert.Contains("more than once", message, StringComparison.Ordinal);
        }

        [Fact]
        public void AMissingShelfLifeIsRefusedAndTheMessageSaysThereIsNoDefault()
        {
            string message = Refusal("""
                { "a.b": { "text": "Hi", "delivery": { "validity": "request-scoped", "receipt": "none" } } }
                """);

            Assert.Contains("no 'shelfLife'", message, StringComparison.Ordinal);
            Assert.Contains("no default", message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("Perishable")]      // capitalised
        [InlineData("urgent")]          // an importance word, which is the wrong axis
        [InlineData("permanent")]       // a plausible near-miss
        [InlineData("")]
        public void AnythingButTheThreeWordsIsRefused(string value)
        {
            string message = Refusal("""
                { "a.b": { "text": "Hi", "delivery": { "shelfLife": "@", "validity": "request-scoped", "receipt": "none" } } }
                """.Replace("@", value, StringComparison.Ordinal));

            Assert.Contains("perishable, persistent and forgettable", message, StringComparison.Ordinal);
        }

        [Fact]
        public void AForgettableMessageStillHasToNameItsValidityContract()
        {
            // No replay policy makes an obsolete FIRST presentation truthful,
            // so even the class that is never retried names its premise.
            string message = Refusal("""
                { "a.b": { "text": "PC audio on", "delivery": { "shelfLife": "forgettable", "receipt": "none" } } }
                """);

            Assert.Contains("no 'validity'", message, StringComparison.Ordinal);
            Assert.Contains("request-scoped", message, StringComparison.Ordinal);
        }

        [Fact]
        public void ARetainedMessageWithoutAHistoryKeyIsRefused()
        {
            string message = Refusal("""
                { "a.b": { "text": "Hot", "delivery": { "shelfLife": "persistent", "validity": "pa.temperature", "receipt": "warning" } } }
                """);

            Assert.Contains("historyKey", message, StringComparison.Ordinal);
        }

        [Fact]
        public void AForgettableMessageNeedsNoHistoryKey()
        {
            var entries = Parse("""
                { "a.b": { "text": "PC audio on", "delivery": { "shelfLife": "forgettable", "validity": "request-scoped", "receipt": "none" } } }
                """);

            Assert.Equal(ShelfLife.Forgettable, entries["a.b"].Delivery!.ShelfLife);
            Assert.Null(entries["a.b"].Delivery.HistoryKey);
        }

        [Fact]
        public void AMissingReceiptIsRefusedBecauseHavingNoneIsADecision()
        {
            string message = Refusal("""
                { "a.b": { "text": "Hi", "delivery": { "shelfLife": "forgettable", "validity": "request-scoped" } } }
                """);

            Assert.Contains("no 'receipt'", message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnInventedEarconNameIsRefused()
        {
            // The vocabulary is Noel's accessibility decision, so a wording
            // file cannot add to it by naming a sound that does not exist.
            string message = Refusal("""
                { "a.b": { "text": "Hi", "delivery": { "shelfLife": "forgettable", "validity": "request-scoped", "receipt": "klaxon" } } }
                """);

            Assert.Contains("none, warning and capture", message, StringComparison.Ordinal);
            Assert.Contains("accessibility decision", message, StringComparison.Ordinal);
        }

        [Fact]
        public void ATtlFieldIsRefusedAndTheMessageSaysWhy()
        {
            // The ruling in a test. Nothing decides staleness by counting
            // seconds, so a lifetime in a delivery descriptor is not an
            // unsupported field to be ignored — it is the wrong idea, and the
            // refusal says so where somebody will read it.
            string message = Refusal("""
                { "a.b": { "text": "Hi", "delivery": { "shelfLife": "forgettable", "validity": "request-scoped", "receipt": "none", "ttlSeconds": "20" } } }
                """);

            Assert.Contains("unknown delivery field", message, StringComparison.Ordinal);
            Assert.Contains("counting seconds", message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnEmptyStringIsStillRefusedInsideAnEnvelope()
        {
            string message = Refusal("""{ "a.b": { "text": "   " } }""");
            Assert.Contains("no text", message, StringComparison.Ordinal);
        }

        // ────────────────────────────────────────────────────────────────
        //  Cross-entry checks
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void AHistoryKeyThatNamesNothingIsReported()
        {
            var entries = Parse("""
                { "a.b": { "text": "Cut", "delivery": { "shelfLife": "perishable", "validity": "transmit.active", "historyKey": "a.nowhere", "receipt": "warning" } } }
                """);

            var findings = LexiconSchema.Validate(entries);
            Assert.Single(findings);
            Assert.Contains("not in the catalog", findings[0].Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AHistoryKeyThatIsItselfAMessageIsReported()
        {
            // History is read through the explicit fact-read path. A history
            // key that is a standalone message would be reachable by an
            // automatic current-message route, which is how an old present-
            // tense assurance gets replayed under a history heading.
            var entries = Parse("""
                {
                  "a.b": { "text": "Cut", "delivery": { "shelfLife": "perishable", "validity": "transmit.active", "historyKey": "a.h", "receipt": "warning" } },
                  "a.h": { "text": "It was cut", "delivery": { "shelfLife": "forgettable", "validity": "request-scoped", "receipt": "none" } }
                }
                """);

            var findings = LexiconSchema.Validate(entries);
            Assert.Single(findings);
            Assert.Contains("text-only", findings[0].Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AValidHistoryReferenceReportsNothing()
        {
            // The positive control. A validator that finds a problem in
            // everything has not established that it can see a correct one.
            Assert.Empty(LexiconSchema.Validate(Parse(Warning)));
        }

        [Fact]
        public void AnUnregisteredValidityContractIsReportedOnlyWhenSomethingIsRegistered()
        {
            var entries = Parse(Warning);

            // No registry supplied: no opinion, because in a process where no
            // domain has started, reporting every contract as unknown is noise
            // rather than evidence.
            Assert.Empty(LexiconSchema.Validate(entries, registeredContracts: null));

            var findings = LexiconSchema.Validate(entries, new[] { "something.else" });
            Assert.Single(findings);
            Assert.Contains("no domain owner has registered", findings[0].Message, StringComparison.Ordinal);

            Assert.Empty(LexiconSchema.Validate(entries, new[] { "transmit.active" }));
        }

        [Fact]
        public void TheBuiltInRequestScopedContractNeedsNoRegistration()
        {
            var entries = Parse("""
                { "a.b": { "text": "Hi", "delivery": { "shelfLife": "forgettable", "validity": "request-scoped", "receipt": "none" } } }
                """);

            Assert.Empty(LexiconSchema.Validate(entries, Array.Empty<string>()));
        }

        // ────────────────────────────────────────────────────────────────
        //  The overlay merge — the migration's sharpest edge
        // ────────────────────────────────────────────────────────────────

        private static (Dictionary<string, LexiconEntry> Baseline, List<LexiconProblem> Problems) MergeOverlay(
            string baselineJson, string overlayJson)
        {
            var baseline = new Dictionary<string, LexiconEntry>(Parse(baselineJson), StringComparer.Ordinal);
            var overlay = new Dictionary<string, LexiconEntry>(Parse(overlayJson), StringComparer.Ordinal);
            var problems = new List<LexiconProblem>();
            Lexicon.Merge(baseline, overlay, problems, "test");
            return (baseline, problems);
        }

        [Fact]
        public void AnOverlayThatChangesWordsInheritsTheShippedDeliveryContract()
        {
            // Before the envelope, Merge replaced the whole entry — correct
            // when an entry was only words. Under the new shape that same line
            // would strip a safety message's shelf life, its validity contract,
            // its history key and its receipt, and the result would still
            // speak, so nothing would look broken.
            var (baseline, problems) = MergeOverlay(Warning, """
                { "a.warning": "Too much power is coming back. I stopped transmitting." }
                """);

            LexiconEntry merged = baseline["a.warning"];
            Assert.Equal("Too much power is coming back. I stopped transmitting.",
                merged.Resolve(VerbosityLevel.Chatty));

            Assert.Equal(DeliveryClassification.Message, merged.Classification);
            Assert.Equal(ShelfLife.Perishable, merged.Delivery!.ShelfLife);
            Assert.Equal("transmit.active", merged.Delivery.Validity);
            Assert.Equal("a.warning.history", merged.Delivery.HistoryKey);
            Assert.Equal(ReceiptPolicy.Warning, merged.Delivery.Receipt);
            Assert.Empty(problems);
        }

        [Fact]
        public void AnOverlayLadderAlsoInheritsTheShippedContract()
        {
            var (baseline, _) = MergeOverlay(Warning, """
                { "a.warning": { "critical": "Cut", "terse": "Transmit cut", "chatty": "Transmit was cut" } }
                """);

            LexiconEntry merged = baseline["a.warning"];
            Assert.True(merged.IsLadder);
            Assert.Equal("Cut", merged.Resolve(VerbosityLevel.Critical));
            Assert.Equal(ShelfLife.Perishable, merged.Delivery!.ShelfLife);
        }

        [Fact]
        public void AnOverlayThatTriesToChangeTheContractKeepsItsWordsAndLosesTheChange()
        {
            var (baseline, problems) = MergeOverlay(Warning, """
                { "a.warning": { "text": "Stopped.", "delivery": { "shelfLife": "forgettable", "validity": "request-scoped", "receipt": "none" } } }
                """);

            LexiconEntry merged = baseline["a.warning"];
            Assert.Equal("Stopped.", merged.Resolve(VerbosityLevel.Chatty));

            // Words are his. Lifecycle is not.
            Assert.Equal(ShelfLife.Perishable, merged.Delivery!.ShelfLife);
            Assert.Equal(ReceiptPolicy.Warning, merged.Delivery.Receipt);

            Assert.Single(problems);
            Assert.Contains("delivery classification", problems[0].Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnOverlayRestatingTheSameContractIsNotAnError()
        {
            var (_, problems) = MergeOverlay(Warning, """
                { "a.warning": { "text": "Stopped.", "delivery": { "shelfLife": "perishable", "validity": "transmit.active", "historyKey": "a.warning.history", "receipt": "warning" } } }
                """);

            Assert.Empty(problems);
        }

        [Fact]
        public void AnOverlayOnlyKeyStaysReadableButCarriesNoAuthority()
        {
            var (baseline, problems) = MergeOverlay(Warning, """
                { "a.invented": { "text": "Something I made up", "delivery": { "shelfLife": "persistent", "validity": "transmit.active", "historyKey": "a.warning.history", "receipt": "warning" } } }
                """);

            // Readable, so nothing an operator added stops working.
            Assert.Equal("Something I made up", baseline["a.invented"].Resolve(VerbosityLevel.Chatty));

            // But a wording file cannot mint a message the application
            // announces on its own account.
            Assert.Equal(DeliveryClassification.Unclassified, baseline["a.invented"].Classification);
            Assert.Null(baseline["a.invented"].Delivery);
            Assert.Single(problems);
        }

        [Fact]
        public void TheOldTwoArgumentMergeStillMergesKeyByKey()
        {
            // The rule this file must not break while changing everything
            // around it: an operator who edits one word does not lose the other
            // four hundred keys in that partition.
            var baseline = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal)
            {
                ["a.one"] = LexiconEntry.Plain("One"),
                ["a.two"] = LexiconEntry.Plain("Two"),
            };
            var overlay = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal)
            {
                ["a.one"] = LexiconEntry.Plain("Uno"),
            };

            Lexicon.Merge(baseline, overlay);

            Assert.Equal("Uno", baseline["a.one"].Resolve(VerbosityLevel.Chatty));
            Assert.Equal("Two", baseline["a.two"].Resolve(VerbosityLevel.Chatty));
        }

        // ────────────────────────────────────────────────────────────────
        //  The typed lookup
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void TheTypedLookupKeepsTheKeyAndTheArgumentsUnrendered()
        {
            LexiconMessage message = Lexicon.Message("connect.status.not_connected");

            Assert.Equal("connect.status.not_connected", message.Key);
            Assert.True(message.CatalogGeneration > 0);
            Assert.Equal(Lexicon.Get("connect.status.not_connected"), message.Text());
        }

        [Fact]
        public void TheTypedLookupAndThePlainOneNeverDisagreeAboutWords()
        {
            foreach (string key in new[]
                     {
                         "connect.status.not_connected",
                         "audio.audio_layer.entered",          // a shipped ladder
                         "connect.disconnect.announcement",    // a shipped four-tier ladder
                     })
            {
                foreach (VerbosityLevel level in new[]
                         { VerbosityLevel.Critical, VerbosityLevel.Terse, VerbosityLevel.Chatty })
                {
                    Assert.Equal(Lexicon.Get(key, level), Lexicon.Message(key).Text(level));
                }
            }
        }

        [Fact]
        public void AMissingKeyComesBackHandledRatherThanThrowing()
        {
            // A caller that has already admitted a fact must not lose it
            // because a lookup failed. What it may not do is present it.
            LexiconMessage message = Lexicon.Message("nothing.like.this.exists");

            Assert.Equal("nothing.like.this.exists", message.Key);
            Assert.Equal("nothing.like.this.exists", message.Text());
            Assert.Equal(DeliveryClassification.Unclassified, message.Classification);
            Assert.False(message.IsPresentableMessage);
        }

        [Fact]
        public void TheMigrationHasClassifiedExactlyTheFactsSurfaceSoFar()
        {
            // The state of the migration, pinned. Legacy entries carry no
            // classification and are frozen in the manifest; the facts surface
            // is classified, and is the shipped corpus's positive control that
            // an envelope really loads.
            //
            // 2,757, NOT the 2,979 this said until 2026-09-25. The full legacy
            // set is 2,979 — 2,970 strings and nine ladders — and 222 of them
            // are Track I's alarm keys, which are QUARANTINED in the manifest
            // (#627) because Track I is not merged and those keys do not exist
            // in this tree. 2,979 minus 222 is 2,757.
            //
            // SO THIS TEST FAILING WITH "Expected 2757, Actual 2979" IS THE
            // EXPECTED RESULT WHEN TRACK I MERGES, and it is a third deliberate
            // tripwire for that event alongside
            // LegacyUnclassifiedQuarantineTests. Read it as "the quarantine has
            // ended, put the number back", NOT as the migration going
            // backwards. Restore 2,979 then.
            //
            // The number is pinned so the migration can only shrink. That is
            // still true; the baseline it shrinks from is simply this tree's.
            //
            // 2,809 since the Track H merge (2026-09-29), NOT 2,757: Track H's
            // 52 keys — the fifteen logging.capture.dropped.* strings of the
            // drop window and the thirty-seven logging.recording.health.*
            // strings — were written on a branch that predates the store and
            // arrived unclassified, so they were frozen in the manifest at the
            // merge (#629, the same move as the heartbeat re-freeze) rather
            // than classified there, because classification is a #617 speech
            // decision. So this went UP by 52 for the same reason it will go
            // up by 222 when Track I merges: a branch older than the store
            // landed. When Track I merges, this reads 3,031.
            //
            // 3,031 since Track IJK brought Track I in (2026-10-01): the 222
            // alarm keys left quarantine and are frozen live in the manifest,
            // exactly as the paragraph above predicted. Still unclassified,
            // still frozen by fingerprint, because classifying them is a #617
            // speech decision that this merge does not take.
            //
            // 3,037 after Track IJK's fixes to Track I's review findings: six
            // new alarms.validation.* sentences for a number the editor could
            // not read (finding 6), frozen by the gate's own printed lines.
            //
            // 3,039 with two more from the same review's prose qualifications:
            // alarms.summary.not_ready (an enabled alarm that cannot judge its
            // condition yet is said, not hidden under "none active") and
            // alarms.summary.item_last_known (a stopped meter's last value is
            // history, and the sentence says so).
            //
            // 3,043 with the four alarms.point.*.terse keys of Noel's
            // 2026-09-23 ruling (#566: no commas, and the place phrase gets two
            // lengths). The thirteen keys that ruling REWORDED were re-frozen
            // with the gate's printed fingerprints rather than classified,
            // which is the #629 tension taken the same way Tracks H and L took
            // it: classification is a #617 speech decision and not this
            // merge's to make.
            int unclassified = 0;
            var classified = new List<string>();

            foreach (string partition in Lexicon.Partitions)
            {
                foreach (var pair in LexiconBaseline.FromShipped(partition))
                {
                    if (pair.Value.Classification == DeliveryClassification.Unclassified) unclassified++;
                    else classified.Add(pair.Key);
                }
            }

            Assert.Equal(3043, unclassified);
            Assert.All(classified, key =>
                Assert.StartsWith("facts.", key, StringComparison.Ordinal));
            Assert.True(classified.Count > 30,
                "only " + classified.Count + " classified entries were found");
        }

        [Fact]
        public void TheFactsSurfaceIsAffirmativelyTextAndNotAccidentallySilent()
        {
            // Every one of these is "delivery": null — somebody looked and said
            // a window's own labels are text. The distinction that matters is
            // that none of them is UNCLASSIFIED, which would mean nobody had
            // looked at all.
            foreach (var pair in LexiconBaseline.FromShipped(Lexicon.FactsSurface))
            {
                Assert.Equal(DeliveryClassification.TextOnly, pair.Value.Classification);
                Assert.Null(pair.Value.Delivery);
            }
        }

        [Fact]
        public void ATextOnlyEntryIsNotAPresentableMessage()
        {
            // "An explicit text-only entry passed to the new standalone message
            // API is a classification error, not implicit forgettable speech."
            var entries = Parse(Warning);
            LexiconEntry history = entries["a.warning.history"];

            var message = new LexiconMessage("a.warning.history", history,
                Array.Empty<(string, object?)>(), 1);

            Assert.False(message.IsPresentableMessage);
            Assert.Equal(DeliveryClassification.TextOnly, message.Classification);
        }

        // ────────────────────────────────────────────────────────────────
        //  The baseline reader
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void TheTextProjectionSeesEveryTierRatherThanSkippingLadders()
        {
            var entries = Parse("""
                {
                  "a.plain": "One",
                  "a.ladder": { "critical": "C", "terse": "T", "chatty": "Ch" },
                  "a.envelope": { "text": "E", "delivery": null }
                }
                """);

            var rows = LexiconBaseline.TextProjection(entries).ToList();

            // One plain, three tiers, one envelope. The old readers saw two of
            // these five and reported success.
            Assert.Equal(5, rows.Count);
            Assert.Contains(rows, r => r.Key == "a.ladder" && r.Tier == "critical" && r.Text == "C");
            Assert.Contains(rows, r => r.Key == "a.envelope" && r.Text == "E");
        }

        [Fact]
        public void TheBaselineReaderNeverTouchesTheProcessWideCatalog()
        {
            long before = Lexicon.CatalogGeneration;
            int count = Lexicon.Count;

            foreach (string partition in Lexicon.Partitions) LexiconBaseline.FromShipped(partition);

            Assert.Equal(before, Lexicon.CatalogGeneration);
            Assert.Equal(count, Lexicon.Count);
        }

        [Fact]
        public void TheCatalogGenerationAdvancesWhenTheWordingReloads()
        {
            long before = Lexicon.CatalogGeneration;
            Lexicon.Forget();
            Lexicon.Load(Lexicon.Partitions);

            // A plan prepared under an older generation renders from one
            // coherent catalogue or not at all.
            Assert.True(Lexicon.CatalogGeneration > before);
        }
    }
}
