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
        public void TheShippedLaddersStillResolveAtEveryTierTheyDefine()
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

            // Nine until Track L9, which added the two forms of the picker's
            // last-seen SmartLink row (#619): Noel asked for a terse form
            // that is short, and a ladder is how a key carries one. The test
            // was named for the count; it is named for the rule now.
            // Fifteen from Track L10: Noel's approved terse and chatty pairs
            // for four MultiFlex rows a radio has not confirmed (#634), the
            // SmartLink row with and without a station name, and the local
            // network and unknown-source rows in their pattern.
            // Seventeen from Track L13: Noel's approved pairs for the two
            // no-station companions, local network and unknown source (#634).
            // Eighteen from Sprint 48 Track F: the noise layer's entry
            // sentence, a ladder like the audio and filter layers' (#518).
            // Nineteen from junkbox #693: the JJ key near miss, Noel's ruled
            // Chatty and Terse sentences (2026-10-06).
            // Twenty from the Check your audio Track B fix round (#835): the
            // near miss that names two chords, ruled 2026-10-28 21:25.
            Assert.Equal(20, ladders);
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
            // in this tree. 2,979 minus 222, plus the three ownership-question strings
            // added under #638, is 2,760.
            //
            // SO THIS TEST FAILING WITH "Expected 2760, Actual 2982" IS THE
            // EXPECTED RESULT WHEN TRACK I MERGES, and it is a third deliberate
            // tripwire for that event alongside
            // LegacyUnclassifiedQuarantineTests. Read it as "the quarantine has
            // ended, put the number back", NOT as the migration going
            // backwards. Restore 2,982 then.
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
            // Then 2,810 on Track L, and it moves by ONE, not four: bringing
            // integration into Track L added four SmartLink keys L had written
            // and removed three L had renamed away (#629, frozen for the same
            // reason). Whichever of L and I integrates second adds its own
            // delta on top of the other's figure.
            //
            // Then 2,821 on Track L6 (#634): the eleven DRAFT sentences of the
            // MultiFlex client list — reported-by-SmartLink rows, may-have-left,
            // the summary and disconnect-reason lines, the blast-radius caveat,
            // and the two station-name rescue details — are frozen, not
            // classified, because their wording is Noel's and unruled and
            // their classification is a #617 speech decision nobody has made.
            // When Track I merges on top of this, it reads 3,043.
            //
            // Then 2,823 on Track L7 (#634, Sol's review of L6): the two
            // local-network forms of the reported-client row, so a client
            // only a LAN broadcast listed stops saying SmartLink reported it.
            // Frozen as DRAFTS for the same reason as L6's. When Track I
            // merges on top of this, it reads 3,045.
            //
            // Then 2,825 on Track L8 (#634, Sol's review of L7): the two
            // forms of the reported-client row for a client no source was
            // observed reporting — already on the vendor object when the
            // attempt attached — so such a row names no source instead of
            // inferring SmartLink from the object's channel. Frozen as
            // DRAFTS for the same reason. When Track I merges on top of
            // this, it reads 3,047.
            //
            // Then 2,827 on Track L9 (#619, Noel's ruling of 2026-09-30):
            // the two forms of the picker row for a radio last seen on
            // SmartLink and not currently confirmed, each a terse and chatty
            // ladder. Frozen as DRAFTS for the same reason. When Track I
            // merges on top of this, it reads 3,049.
            //
            // Then 2,828 on Track L11 (#634, Sol's scoped review of L9 and
            // L10): the Disconnect reason for a client the radio identified
            // that may have left since, which the approved "until the radio
            // identifies it" states falsely. Frozen as a DRAFT for the same
            // reason. When Track I merges on top of this, it reads 3,050.
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
            //
            // 3,062 when Track IJK (I, J and K carried forward) merged on top of
            // Track L, 2026-10-01: L's 19 over the 2,809 base (2,828) plus IJK's
            // 234 (the 222 restored alarm keys and twelve new ones, 3,043 on its
            // own branch). Each side's paragraphs above are kept so the figure can
            // be re-derived; the number is the sum, confirmed by running this test
            // on the merged tree rather than by arithmetic alone.
            //
            // 3,063 with alarms.event.silenced (Track IJK2, 2026-10-01): the
            // history row for the operator's Ctrl standing an alarm's warning
            // down, which #617's 2026-09-24 ruling makes a state of its own —
            // not an acknowledgement — lasting until the reading worsens. Frozen
            // with the gate's printed line, not classified (#629 stands).
            //
            // 3,068 after Track IJK3's alarm prose pass (2026-10-01): Noel's
            // 07:17 and 07:18 rulings give the place phrase ONE length, so the
            // four alarms.point.*.terse keys leave (-4), and the terse WARNING
            // gets its own six frames instead, alarms.voltage.*_tx.terse and
            // *_rx.terse (+6). alarms.summary.item_last_known leaves (-1) for
            // alarms.summary.last_known and last_known_no_value (+2), because a
            // last-known alarm is no longer counted active and a meter lost
            // outright has no value to quote; alarms.summary.not_ready_one
            // (+1) is the singular of the new not-ready sentence; and
            // alarms.point.meter (+1) names a meter we have not met. Net +5.
            // Twenty-eight reworded keys were re-frozen with the gate's
            // printed fingerprints, not classified (#629 stands).
            //
            // 3,070 with alarms.meter.still_active_above and _below (Track
            // IJK4, 2026-10-01, Astra's round-two blocker 3): the frame for a
            // re-read level warning whose current reading is inside the clear
            // margin, where "at or above the line" would be false of the
            // number stated. Frozen with the gate's printed lines, not
            // classified (#629 stands).
            //
            // 3,072 with alarms.meter.still_active_above_no_band and
            // _below_no_band (Track IJK5, 2026-10-01, Astra's Track IJK4
            // review, blocker 1): the same frame for an alarm with no clear
            // margin the meter can represent, which clears only strictly past
            // the line, so "at or below" would promise a clearance the monitor
            // never grants. Frozen with the gate's printed lines, not
            // classified (#629 stands).
            //
            // Then 2,831 when integration merged into Track N (#638): the
            // three keys the connect-time ownership question brought with it
            // -- connect.ownership.reason, connect.ownership.silencing, and
            // settings.silenced.ownership, the label the suppression store
            // reads back. Frozen as DRAFTS for the same reason. The
            // arithmetic, because the merge got it wrong once: merge base
            // 2,757 + Track N's 3 + integration's 71. Only one of the three
            // conflicted, so resolving the conflict alone undercounts by two.
            // Whoever merges Track I on top of this adds the same 3.
            // 3,075 when Track N merged into integration (2026-10-01): the three
            // ownership keys named in the paragraph above, on top of Track IJK's
            // 3,072. Base 2,828 + IJK's 244 + Track N's 3. **Confirmed by running
            // this test on the merged tree, not by the arithmetic alone** — the
            // arithmetic was wrong once today, when resolving the single
            // CONFLICTING key was mistaken for counting the merge, and two keys
            // that merged cleanly went uncounted.
            //
            // 3,131 at the Sprint 48 merge (2026-10-05): all three tracks
            // landed, each having recorded the count it would have produced
            // alone. 3,075 + 17 - 1 + 39 + 1. Each track's own note is kept
            // below rather than collapsed, because each says WHICH strings it
            // added and why they were frozen.
            //
            // NOEL RULED 2026-10-05 THAT THESE GET CLASSIFIED RATHER THAN
            // FROZEN — "I'd classify teh keys" — so this number is now a debt
            // with a decision behind it, not a standing state. The ruling and
            // the measured size of the backlog (3,091 before this sprint,
            // across eight lexicon files, with facts.json the only one ever
            // classified) are in #151.
            //
            // Track A (#151, 2026-10-05): seventeen new
            // message-key sentences — what the key said when it sent, stopped,
            // was cut short, ran long, found nothing for the mode, or could not
            // key the radio, plus the editor's payload-kind words — and one key
            // deleted (needs_key_label_and_text, replaced by _payload). Frozen
            // as DRAFTS by the gate's own printed lines, not classified, for
            // the same reason as L6 through L11: the wording is Noel's to rule
            // and the classification is a #617 speech decision this track does
            // not take. Six reworded CW-to-Messages entries re-frozen under the
            // #629 exemption. 3,075 + 17 - 1. Confirmed by running this test.
            //
            // Track B (#307, 2026-10-05): the thirty-nine
            // strings of the system-wide keys — thirty-seven
            // settings.systemwide_keys.* for the dialog and its four-layer
            // conflict report, leader.systemwide.layer_needs_window for a layer
            // asked for from another program, and
            // audio.ptt.systemwide_watchdog_unkeyed for the fail-safe ending a
            // held transmit on its own. Frozen with the gate's printed lines,
            // not classified, for the reason every track above gives: the
            // classification is a #617 speech decision and this track's brief
            // did not carry it. The watchdog sentence is the one that most
            // wants classifying — a safety outcome the operator has no other
            // way of learning — and it is named in the track report for exactly
            // that reason.
            //
            // Track C: one key, leader.explorer.tier.digit,
            // the explorer's label for the JJ key's fifth tier. Frozen in the
            // manifest beside its six sibling tier labels rather than
            // classified alone, because the only classified surface so far is
            // facts.* and this test says so below; the seven tier labels want
            // classifying together, under #629, as the window text they are.
            //
            // Sprint 48 Track F: fourteen keys for the noise layer
            // (audio.noise_layer.*) — the layer's name, its entry ladder,
            // closed, the two restore sentences, the switches-only hint and
            // the eight switch names — frozen in the manifest rather than
            // classified, for the reason Track C gave for its tier label:
            // the only classified surface is facts.*. Two existing hints
            // (audio.noise.capture.turn_spectral_on, spectral_is_off_hint)
            // were reworded and re-frozen, not added. +14.
            //
            // THAT FREEZE IS AGAINST THE RULING ABOVE, and so is Track D's
            // ten (logging.radio_pane.*, settings.atu.memories_cleared,
            // settings.memory.*). Both tracks gave the every-track-so-far
            // precedent as their reason; the ruling names that precedent and
            // overrules it. D's brief never cited #151 and had no route to
            // the ruling; F's cited it, where it sits deep inside an entry
            // about a different subject. The twenty-four are a known debt
            // with a decision already against them, to be classified key by
            // key — never batch-defaulted, which is the expensive mistake
            // #617 names — with the perishable sentences going to Noel,
            // because operator-facing prose is his.
            //
            // Sprint 48 Track E (2026-10-14) FOLLOWED THE RULING, froze
            // nothing, AND CLASSIFIED THE FIRST KEYS OUTSIDE facts.*. Noel's
            // 2026-10-05 ruling above — classify, do not freeze — is applied
            // from that track on: settings.cw.vox_did_not_key, the reason a
            // VOX voice send gives when the radio never keyed (#151's VOX
            // half), ships with a delivery contract (forgettable,
            // request-scoped, a warning receipt) and NOT a manifest line. The
            // classified set below is now ENUMERATED rather than
            // prefix-matched: every classified key is either on the facts
            // surface or named here, with the track that classified it. A key
            // that reaches this list has been looked at; a key that reaches
            // the manifest has been postponed. The wording itself is still
            // Noel's to review.
            //
            // Also that track: the system-wide keys' one master switch became
            // two (Item 4 of Track E, recommended under #689), so
            // settings.systemwide_keys.enabled and enabled_help are retired
            // from the lexicon AND the manifest (-2), and the four strings of
            // the two switches ship classified as window text (delivery null,
            // the facts surface's own classification for labels and help),
            // named below.
            //
            // Sprint 48 Track D (#591, #700, 2026-10-14): ten keys, frozen for
            // the same reason as every track above. Eight for the Memories and
            // ATU Memories dialogs whose mutators were never wired -
            // settings.memory.tone_mode_off / tone_mode_ctcss, offset_minus /
            // offset_simplex / offset_plus (combo labels), add_failed and
            // frequency_rejected (the two outcomes the dialog's own controls
            // cannot convey), and settings.atu.memories_cleared (the Clear
            // button changes nothing visible). Two for the logging layout's
            // radio pane, whose ten hooks were never assigned:
            // logging.radio_pane.frequency_set and tune_step, which until now
            // were literals in RadioPaneControl that nothing had ever spoken.
            // The spoken ones carry SpeechSubject keys (MemoryEdit, AtuMemories,
            // LoggingRadioPane) so supersession works; their shelf life is the
            // #617 decision this brief did not carry.
            //
            // SO THE MERGED NUMBER IS NO TRACK'S OWN. From the same base of
            // 3,131: F alone would have said 3,145, E alone 3,129, D alone
            // 3,141. The value below was recomputed by running this test on
            // the merged tree and pasting what it printed, and the assertion
            // was shown reachable by mutating it one higher and watching it go
            // red — not by adding the three deltas, which would be a
            // prediction wearing a measurement's clothes.
            //
            // 3,144 at the junkbox fold (2026-10-21), three fewer: Noel's Fold 6
            // ruling took the "on it" / "from it" tail off every profile
            // refusal piece, the three shipped legacy fragments
            // settings.guard.action.profile_load, profile_save and
            // profile_delete among them. Rewording a frozen key is exactly what
            // the manifest refuses, so they leave it and are classified below
            // beside their four transmit and microphone siblings, as the
            // fragments they are.
            //
            // One fewer at junkbox #693 (2026-10-25): leader.near_miss left
            // the manifest when Noel's ruled sentences replaced it, and is
            // classified below.
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

            var classifiedOutsideFacts = new[]
            {
                "audio.reference.armed", // Round 8: direct answer to the operator's arm request.
                "settings.cw.vox_did_not_key",                        // Sprint 48 Track E, #151
                "settings.systemwide_keys.leader_enabled",            // Sprint 48 Track E, #307 Item 4
                "settings.systemwide_keys.leader_enabled_help",
                "settings.systemwide_keys.transmit_enabled",
                "settings.systemwide_keys.transmit_enabled_help",
                "settings.keys.editor.systemwide_route",             // Sprint 48 Track E, #307 Item 6
                "settings.systemwide_keys.reset_help",               // Sprint 48 Track E, #307 Item 6

                // #726, the capability gate, 2026-10-17. Classified rather
                // than frozen because they were written here, not inherited
                // from a branch older than the store.
                //
                // delivery: null - TextOnly - is an affirmative judgement, not
                // the easy answer. Each of these is spoken ONLY in answer to a
                // press: the three refusals when the operator presses R, S or
                // Ctrl+N on a radio that cannot run them, and the two closing
                // sentences as part of the H list they asked for. The
                // application never says any of them on its own account, so
                // none has a shelf life.
                //
                // Their three older siblings - audio.nr.neural_unsupported,
                // .spectral_unsupported and .filter_unsupported, the hardware
                // half of the same refusals - are legacy bare strings still in
                // the manifest. WHEN THE PASS REACHES THOSE, DECIDE ALL SIX
                // TOGETHER: splitting one family's classification across two
                // judgements is how a surface ends up half-presentable.
                "audio.nr.neural_unsubscribed",
                "audio.nr.spectral_unsubscribed",
                "audio.nr.filter_unsubscribed",
                "audio.noise_layer.gated_keys_unavailable",
                "audio.noise_layer.gated_keys_unsubscribed",

                // #726 item 6, the same day: the textual markers that replaced
                // a collapsed checkbox and a stand-in menu item. These two are
                // LABELS rather than announcements - they are the control's own
                // name, read when focus lands on it - so delivery: null is the
                // plainest case of TextOnly in this group.
                "audio.nr.marker_needs_subscription",
                "audio.nr.marker_not_on_this_radio",

                // The connect-time ownership question's reason, classified
                // 2026-10-18 when Noel reviewed its wording. It had been
                // FROZEN AS A DRAFT since the Track N merge precisely because
                // the review had not happened, and the call site said so in a
                // comment. The review is what makes it classifiable: the
                // migration manifest's own header says a key "cannot be edited
                // and left unclassified", so rewording it and re-fingerprinting
                // it in place was not an option.
                //
                // delivery: null - TextOnly - because this is the dialog's BODY
                // PROSE, read when focus lands in it, and the application never
                // says it on its own account. A sentence this long having a
                // shelf life would mean it could be shortened or dropped by the
                // verbosity machinery, which for a consent question is the one
                // thing that must not happen.
                //
                // Its two siblings from the same merge - connect.ownership.silencing
                // and settings.silenced.ownership - ARE STILL FROZEN. That is
                // deliberate and not an oversight: only the reason text was
                // reviewed. WHEN THE PASS REACHES THEM, DECIDE BOTH WITH THIS
                // ONE IN VIEW, for the reason the audio.nr family's note gives
                // two blocks above.
                "connect.ownership.reason",

                // Junkbox #778, 2026-10-19: the value layers' answer to a key
                // that has nowhere to go and is no near miss, now that such a
                // key stays in the layer instead of leaving it. Classified
                // rather than frozen because it was written here (Noel's
                // 2026-10-05 ruling; #720). delivery: null - TextOnly - on the
                // #726 reasoning two blocks above: it is spoken ONLY in answer
                // to a press, through the engine's own refusal path where #528
                // already decides whether words or the tone are heard, and the
                // application never says it on its own account. Its sibling
                // audio.value_layer.near_miss is legacy and still frozen; WHEN
                // THE PASS REACHES IT, DECIDE BOTH TOGETHER. The wording was
                // RULED by Noel 2026-10-19 14:33 (#778 queue question 2).
                "audio.value_layer.unknown_key",
                // Same track, same reasoning: the line for a bare digit, which
                // a value layer refuses because forwarded it would start
                // frequency entry. RULED by Noel 14:33-14:34 ("just say
                // selected number").
                "audio.value_layer.typed_number",
                // Junkbox #693, 2026-10-25: the JJ key layer's near miss, now
                // Noel's ruled ladder (2026-10-06, written into #695's body)
                // with the {pressed} placeholder it never had. Classified, not
                // re-frozen, because rewording a frozen key is what the
                // manifest refuses (#720). delivery: null - TextOnly - on the
                // #726 reasoning: spoken ONLY in answer to the press that
                // missed, through the leader's refusal path where
                // RefusalVoice decides whether words or the tone are heard.
                // Its sibling leader.unknown_key is legacy and still frozen;
                // WHEN THE PASS REACHES IT, DECIDE IT WITH THIS ONE IN VIEW.
                "leader.near_miss",
                // Junkbox #717, 2026-10-25: Manage Messages' empty-state row
                // and the list's help text, which names Tab and Escape.
                // Classified rather than frozen because they were written
                // here (#720). delivery: null - TextOnly - because both are
                // the control's own text, read when focus lands on the list
                // (the #726 item 6 reasoning for labels); the application
                // never says either on its own account. DRAFTS awaiting Noel.
                "settings.cw_message.list_empty",
                "settings.cw_message.list_help",
                // Junkbox #705, 2026-10-25: the Hotkey Editor's reason for a
                // key that can never be assigned (Enter, and Tab or Escape
                // with a modifier). A FRAGMENT that settings.keys.editor.key_reserved
                // wraps, never spoken alone, so TextOnly (the #777
                // reasoning). DRAFT awaiting Noel.
                "settings.keys.editor.navigation_key_reserved",
                "settings.keys.editor.description",
                // Junkbox #753, 2026-10-25: the Home Mode field's spoken
                // label. A LABEL, read when focus lands on the field, so
                // delivery: null - TextOnly (the #726 item 6 reasoning). Its
                // siblings settings.home.field_label_* are legacy and still
                // frozen; WHEN THE PASS REACHES THEM, DECIDE THEM WITH THIS ONE.
                "settings.home.field_label_mode",
                // #778b, same reasoning: the fine-step sentence the engine
                // appends to the digit line where Shift with the arrows really
                // is the fine step. Noel asked for one at 14:33; this wording
                // is a DRAFT awaiting him.
                "audio.value_layer.fine_step",
                // #778b, same reasoning: the three very short sentences for push
                // to talk, the transmit lock and a bracket key pressed while a
                // layer is open where nothing would act on them. Spoken only in
                // answer to that press, and only at Chatty (Noel, 2026-10-19
                // 20:55; below Chatty the refusal tone alone). DRAFTS awaiting Noel.
                "audio.value_layer.ptt_not_here",
                "audio.value_layer.lock_not_here",
                "audio.value_layer.bracket_not_here",

                // Junkbox #571 and #800, 2026-10-22: the hold on keying for
                // five seconds after a reflected-power cut, and the one note
                // that VOX was switched off for it. Classified rather than
                // frozen because they were written here (#720).
                // DRAFT PROSE (#571): awaiting Noel
                //
                // The refusal (and its singular last-second form) is
                // forgettable, request-scoped, receipt warning: it is spoken
                // ONLY in answer to a press that would have keyed, through
                // SpeechIntent.Urgent at Critical so it is heard at Off like
                // the cut sentence it repeats; the warning alarm tone is the
                // cut's own and sounds beside it. Forgettable because the
                // next press inside the window gets a fresh copy with the
                // seconds counted down, and a copy replayed later would claim
                // a hold that may have ended. Its shelf life against a later
                // LEGITIMATE transmission is carried at the call site, not
                // here: the same validity answer the cut sentence carries.
                "audio.ptt.cut_hold_refused",
                "audio.ptt.cut_hold_refused_last_second",
                // The reason clauses and "the antenna" are FRAGMENTS of that
                // sentence, never spoken alone: TextOnly, the only honest
                // classification for a fragment (the #777 reasoning).
                "audio.ptt.cut_hold_reason_share",
                "audio.ptt.cut_hold_reason_watts",
                // Repair round 1 (Astra B1): the reason when a transmit
                // check's cut arrives with no reading in hand. Same fragment
                // classification as its two siblings.
                "audio.ptt.cut_hold_reason_unknown",
                "audio.ptt.cut_hold_the_antenna",

                // Junkbox #777, 2026-10-19: a global-profile write (Save, or
                // Delete since round two) refused because the radio is
                // declared someone else's, or its ownership was never
                // answered; the verb comes from settings.guard.action.* the
                // way settings.guard.refused does it. Classified rather than
                // frozen (#720). delivery: null - TextOnly - on the #726
                // reasoning: each is spoken ONLY in answer to the operator's
                // own press (the menu's Save Station Setup to Radio, or the
                // Profiles dialog's Save or Delete), the application never
                // says either on its own account, and a refusal whose sentence
                // names the remedy must not be shortened by the verbosity
                // machinery. Their siblings settings.guard.layout_save_blocked
                // and .layout_save_roster_unknown are legacy and still frozen;
                // WHEN THE PASS REACHES THEM, DECIDE ALL FOUR TOGETHER. Both
                // wordings were APPROVED by Noel 2026-10-19 (#777 Q4, Q5).
                "settings.guard.profile_write_not_our_radio",
                "settings.guard.profile_write_ownership_unknown",
                // Round three, Noel's 2026-10-19 ruling that transmit and
                // microphone profiles are refused like global ones: the verb
                // and kind the two sentences above take as {action} for those
                // kinds. FRAGMENTS of a sentence, never spoken alone, so
                // TextOnly is the only honest classification. The two
                // sentences above were APPROVED the same day; these four
                // were APPROVED by Noel at the 2026-10-21 fold (Fold 6), with
                // the "on it" / "from it" tail removed.
                "settings.guard.action.tx_profile_create",
                "settings.guard.action.tx_profile_delete",
                "settings.guard.action.mic_profile_create",
                "settings.guard.action.mic_profile_delete",
                // The global fragments, shared with the change-nothing hold's
                // settings.guard.refused. Legacy and frozen until the same
                // fold, when Noel's Fold 6 ruling took the same tail off them
                // so every refusal reads alike ("load the profile", "save the
                // profile", "delete the profile"); a reworded key cannot stay
                // in the manifest, so they are classified here on the
                // reasoning of the four above: fragments, never spoken alone.
                "settings.guard.action.profile_load",
                "settings.guard.action.profile_save",
                "settings.guard.action.profile_delete",
                // Same track, round two (Codex B3): the honest receipt for a
                // save whose command did not go out within its bound, which
                // until then was announced as saved. Spoken only in answer to
                // the press that asked for the save; TextOnly on the same
                // reasoning; the existing receipts settings.profile.saved and
                // settings.station_layout.saved beside them are legacy and
                // still frozen - DECIDE EACH PAIR TOGETHER. APPROVED as
                // written by Noel at the 2026-10-21 fold (Fold 6).
                "settings.profile.save_not_sent",
                "settings.station_layout.save_not_sent",
                // #456: new listenback control labels, dialog body/status prose,
                // and fragments. TextOnly describes these persistent UI values;
                // the dialog echoes its status value through its existing speech
                // path. None is submitted as a standalone fact-delivery message.
                // Draft wording still awaits Noel; the legacy manifest is unchanged.
                "audio.iq.idle",
                "audio.iq.status_label",
                "audio.iq.rate_label",
                "audio.iq.rate_help",
                "audio.iq.separate_pan",
                "audio.iq.full_duplex",
                "audio.iq.antenna_label",
                "audio.iq.antenna_help",
                "audio.iq.antenna_unchanged",
                "audio.iq.diagnostic_start",
                "audio.iq.diagnostic_stop",
                "audio.iq.cancel_start",
                "audio.iq.preflight_button",
                "audio.iq.recovery_button",
                "audio.iq.one_scu",
                "audio.iq.preparing",
                "audio.iq.recording_diagnostic",
                "audio.iq.stop_other",
                "audio.iq.finishing",
                "audio.iq.failed",
                "audio.iq.corrupt",
                "audio.iq.diagnostic_result",
                "audio.iq.unknown",
                "audio.iq.state.clean",
                "audio.iq.state.incomplete",
                "audio.iq.state.corrupt",
                "audio.iq.preflight_running",
                "audio.iq.preflight_clean",
                "audio.iq.preflight_loss",
                "audio.iq.recovery_title",
                "audio.iq.recovery_offer",
                "audio.iq.saved_setting",
                "audio.iq.creation_uncertain",
                "audio.iq.restore_now",
                "audio.iq.restored",
                "audio.iq.recovery_pending",
                "audio.iq.no_recovery",
                "audio.iq.reason.cancelled",
                "audio.iq.reason.unexpected",
                "audio.iq.reason.no-radio",
                "audio.iq.reason.connection-ended",
                "audio.iq.reason.radio-busy",
                "audio.iq.reason.take-already-running",
                "audio.iq.reason.recovery-pending",
                "audio.iq.reason.one-scu-not-confirmed",
                "audio.iq.reason.no-transverter-port",
                "audio.iq.reason.smartlink-iq-unavailable",
                "audio.iq.reason.ssb-only",
                "audio.iq.reason.unsupported-rate",
                "audio.iq.reason.no-free-slice",
                "audio.iq.reason.antenna-change-requires-separate-pan",
                "audio.iq.reason.no-free-iq-channel",
                "audio.iq.reason.pan-iq-in-use",
                "audio.iq.reason.no-transmit-pan",
                "audio.iq.reason.arrangement-refused",
                "audio.iq.reason.listening-pan-not-created",
                "audio.iq.reason.no-other-transverter-input",
                "audio.iq.reason.receive-antenna-unavailable",
                "audio.iq.reason.no-stream-status-within-five-seconds",
                "audio.iq.reason.no-active-stream-at-confirmed-rate",
                "audio.iq.reason.classifier-not-ready",
                "audio.iq.reason.repeating-pattern",
                "audio.iq.reason.constant",
                "audio.iq.reason.non-finite-payload",
                "audio.iq.reason.rate-request-refused",
                "audio.iq.reason.unexpected-payload-endian",
                "audio.iq.reason.stream-state-or-rate-changed",
                "audio.iq.reason.change-nothing",
                "audio.iq.reason.preflight-required",
                "audio.iq.reason.dummy-load-active",
                "audio.iq.setting.full-duplex",
                "audio.iq.setting.slice-mute",
                "audio.iq.setting.ears-antenna",
                "audio.iq.setting.ears-rf-gain",
                "audio.iq.setting.ears-frequency",
                "audio.iq.setting.ears-mode",
                "audio.iq.setting.ears-pan-center",
                "audio.iq.setting.tx-antenna",
                "audio.iq.setting.rf-power",
                "audio.iq.setting.monitor",
                "audio.iq.setting.xvtr-drive",
                "audio.iq.setting.created-pan",
                "audio.iq.setting.created-slice",
                "audio.iq.setting.created-stream",
                "audio.iq.setting.pan-iq-channel",
                "audio.iq.listen_method",
                "audio.iq.listen_method_help",
                "audio.iq.detune_plus",
                "audio.iq.detune_minus",
                "audio.iq.keyed_line",
                "audio.iq.recording_offair",
                "audio.iq.take_result",
                // #456 Round 12: short spoken take result and post-playback comparison.
                "audio.iq.take_summary_short",
                "audio.iq.take_summary_gaps",
                "audio.iq.startup_signature",
                "audio.iq.adding_listener",
                "audio.iq.removing_listener",
                "audio.check.loopback_adding_ears",
                "audio.check.loopback_removing_ears",
                "audio.iq.never_keyed",
                "audio.iq.playing",
                "audio.iq.playing_incomplete",
                "audio.iq.playing_detuned",
                "audio.iq.no_take",
                "audio.iq.reason.keying-refused",
                "audio.iq.reason.unsupported-render",
                "audio.iq.rate24",
                "audio.iq.rate48",
                "audio.iq.rate96",
                "audio.iq.play_sent",
                "audio.iq.compare_previous",
                // Round 11 hear-yourself comparison and copyable measurements.
                "audio.iq.auto_compare",
                "audio.iq.auto_unavailable",
                "audio.iq.auto_result_intro",
                // #456 Round 13: the 0.5 dB audible-change comparison sentences.
                "audio.iq.same_level_tone",
                "audio.iq.level_changed_tone_same",
                "audio.iq.level_same_tone_changed",
                "audio.iq.level_and_tone_changed",
                "audio.iq.all_measures_same",
                "audio.iq.reference_same_level_tone",
                "audio.iq.reference_level_changed_tone_same",
                "audio.iq.reference_level_same_tone_changed",
                "audio.iq.reference_level_and_tone_changed",
                "audio.iq.reference_all_measures_same",
                "audio.iq.punch_same",
                "audio.iq.punch_changed",
                "audio.iq.stopband_same",
                "audio.iq.stopband_changed",
                "audio.iq.rose",
                "audio.iq.fell",
                "audio.iq.statistics_label",
                "audio.iq.statistics_help",
                "audio.iq.statistics_empty",
                "audio.iq.aligned",
                "audio.iq.not_aligned",
                "audio.iq.sent_unavailable",
                "audio.iq.tone_identified",
                "audio.iq.tone_not_identified",
                "audio.iq.playing_sent",
                "audio.iq.louder",
                "audio.iq.quieter",
                "audio.iq.brighter",
                "audio.iq.darker",
                "audio.iq.stronger",
                "audio.iq.weaker",
                "audio.iq.more_compressed",
                "audio.iq.more_peaky",
                "audio.iq.need_two",
                "audio.iq.previous_comparison",
                "audio.iq.unlike_or_unaligned",
                "audio.iq.reason.sent-checksum",
                "audio.iq.reason.arrangement-not-confirmed",
                "audio.iq.reason.tx-antenna-setup-unconfirmed",
                "audio.iq.reason.rf-power-setup-unconfirmed",
                "audio.iq.reason.monitor-setup-unconfirmed",
                "audio.iq.reason.slice-mute-setup-unconfirmed",
                "audio.iq.reason.full-duplex-setup-unconfirmed",
                "audio.iq.reason.xvtr-drive-setup-unconfirmed",
                "audio.iq.reason.arrangement-connection-ended",
                "settings.guard.action.iq_take",
                "audio.iq.reason.command-queue-stalled",
                "audio.iq.unreadable_offer",
                "audio.iq.unreadable_acknowledge",
                "audio.iq.unreadable_archived",
                "audio.iq.reason.listening-state-not-confirmed",
                "audio.iq.recovery_state_unknown",
                "audio.iq.ack.title",
                "audio.iq.ack.offer",
                "audio.iq.ack.clear",
                "audio.iq.ack.cleared",
                "audio.iq.ack.changed",
                "audio.iq.ack.creation_unknown",
                "audio.iq.ack.slice",
                "audio.iq.ack.pan",
                "audio.iq.ack.stream",
                "audio.iq.ack.radio",
                "audio.iq.ack.created",
                "audio.iq.ack.setting",
                "audio.iq.ack.not_recorded",
                "audio.iq.ack.listener_request",
                "audio.iq.ack.antenna_unchanged",
                "audio.iq.heading",
                "audio.iq.no_transmission",
                "audio.iq.ack.present",
                "audio.iq.ack.absent",
                "audio.iq.review_absent",
                "audio.iq.review_present",
                "audio.iq.review_unknown",
                "settings.guard.action.full_duplex",
                "audio.full_duplex.label",
                "audio.full_duplex.command",
                "audio.full_duplex.on",
                "audio.full_duplex.off",
                "audio.full_duplex.not_reported",
                "audio.full_duplex.help",
                "audio.full_duplex.blocked",
                "audio.full_duplex.pending",
                "audio.full_duplex.setting",
                "audio.check.loopback_setup_unconfirmed",
                "audio.check.loopback_no_other_port",
                "audio.check.loopback_restore_unconfirmed",
                "audio.iq.reason.listener-command-refused",
                "audio.iq.reason.listener-command-timeout",
                "audio.iq.reason.full-duplex-refused",
                "audio.full_duplex.disagreed_on",
                "audio.full_duplex.disagreed_off",
                "audio.full_duplex.refused",
                "audio.full_duplex.unconfirmed",

                // #456 Round 8/9: playback fact/limiter, optional local capacity,
                // reply failure, purpose wording, and #787 slice control labels.
                "audio.take.fixed_gain",
                "audio.take.limiter_engaged",
                "audio.iq.stop_slice_record",
                "audio.iq.stop_slice_play",
                "audio.iq.capacity_optional",
                "audio.iq.reason.no-stream-create-reply",
                "audio.iq.reason.stream-create-refused",
                "audio.iq.reason.smartlink-no-iq",
                "audio.iq.listener_added",
                "audio.iq.offair_removing_listener",
                "audio.check.loopback_ears_added",

                // #456 Round 10: off-air live monitoring and the two comparison methods.
                "audio.iq.hear_during_take",
                "audio.iq.setting.ears-mute",
                "audio.iq.compared_aligned",
                "audio.iq.compared_average",

                // #456 Round 14 (#794's header-and-gate half): the three source
                // words, the one-sentence refusal that asks for another take of
                // the same kind, and the setup changes named in front of the
                // numbers (transmit filter, RF power, mic profile, mic input,
                // computer microphone). All DRAFT PROSE awaiting Noel. TextOnly
                // because the dialog speaks them through its existing status
                // path, which already carries the comparison's own subject.
                "audio.iq.source_tone",
                "audio.iq.source_voice",
                "audio.iq.source_reference",
                "audio.iq.unlike_source",
                "audio.iq.setup_filter",
                "audio.iq.setup_power",
                "audio.iq.setup_profile",
                "audio.iq.setup_mic_input",
                "audio.iq.setup_pc_microphone",
                "audio.iq.no_mic_profile",
                // Round 14 bench (Noel, 2026-10-22 07:10): the automatic comparison
                // stays inside this session, and a manual one reaching an older take
                // says which session it came from. DRAFT PROSE, TextOnly as above.
                "audio.iq.first_of_session",
                "audio.iq.earlier_session",
                "audio.iq.when_today",
                "audio.iq.when_yesterday",
                "audio.iq.when_date",

                // #787 Tape Deck track 2: the ONE slice recorder surface's
                // sentences, spoken by every door (Home, the Slice menu, the
                // JJ key, the Workshop's buttons, the Audio Check). All DRAFT
                // PROSE awaiting Noel. TextOnly because each is spoken through
                // RecorderControls.Speak under SpeechSubject.SliceRecorder.
                "audio.recorder.recording",
                "audio.recorder.recording_loopback",
                "audio.recorder.recording_stopped",
                "audio.recorder.already_recording",
                "audio.recorder.playing",
                "audio.recorder.playing_stopped",
                "audio.recorder.play_stopped_by_transmit",
                "audio.recorder.nothing_to_play",
                "audio.recorder.no_slice",
                "audio.recorder.not_your_slice",
                "audio.recorder.record_unconfirmed",
                "audio.recorder.record_stop_unconfirmed",
                "audio.recorder.play_unconfirmed",
                "audio.recorder.play_stop_unconfirmed",
                "audio.recorder.nearly_full",
                // Home's recorder controls and the Slice menu's Recorder rows.
                "audio.recorder.record_button",
                "audio.recorder.stop_record_button",
                "audio.recorder.play_button",
                "audio.recorder.stop_play_button",
                "audio.fields.recorder_slice",
                "audio.fields.recorder_slice_option",
                "audio.fields.recorder_help",
                // The computer recorder's controls (record to file, play
                // from computer) and the stub's refusals until Tape Deck
                // track 3's engine is installed.
                "audio.recorder.failed",
                "audio.recorder.computer_record_unavailable",
                "audio.recorder.computer_play_unavailable",
                "audio.recorder.computer_play_refused_transmitting",
                "audio.recorder.record_to_file_button",
                "audio.recorder.stop_record_to_file_button",
                "audio.recorder.play_from_computer_button",
                "audio.fields.computer_recorder_help",

                // DRAFT PROSE (#787): pending-start cancellation confirmation.
                "audio.check.start_cancelled",
                "audio.check.start_cancelled_escape",

                // DRAFT PROSE (#787): recorder intent, lengths, and capture options.
                "audio.recorder.play_vox_unconfirmed",
                "audio.recorder.play_intent_changed",
                "audio.recorder.seconds",
                "audio.recorder.length_recorded",
                "audio.recorder.no_recording",
                "audio.recorder.has_recording",
                "audio.recorder.recording_length",
                "audio.recorder.play_length",
                "audio.recorder.play_strip",
                "audio.recorder.stop_strip",
                "audio.recorder.record_heard",
                "audio.recorder.stereo",
                "audio.recorder.options_help",



                // Junkbox #686, 2026-10-20: the microphone healer's report on
                // a radio not declared Mine (Unset or SomeoneElses). There it
                // no longer puts the mic input back to PC, so the legacy
                // audio.mic.diverged ("Setting it back to PC.") would be
                // false; this says the radio will not hear the operator and
                // points at the Radio menu's Switch Mic Input Back to PC, in
                // Noel's own phrase (07:58). Classified rather than frozen
                // (#720). It is a MESSAGE, not TextOnly: nobody asked for it -
                // the application says it on its own account when a
                // divergence starts. Forgettable because that is what its
                // producer actually does: one presentation per divergence
                // episode, no retry, no retained debt. Receipt warning because
                // it reports a transmit that will carry nothing.
                // request-scoped is the one built-in contract and is an
                // APPROXIMATION here: the premise can stop being true, and no
                // domain owner has registered a contract that evaluates it. By
                // #617's own test the honest shelf life is persistent, which
                // needs that contract and a past-tense historyKey in Noel's
                // words - a question for Noel, not a default. Its sibling
                // audio.mic.diverged is legacy and still frozen; DECIDE BOTH
                // TOGETHER. APPROVED by Noel at the 2026-10-21 fold (Fold 4),
                // its second sentence reworded in his words.
                "audio.mic.diverged_not_ours",
                // Same track, repair round one: the start-time sibling. On a
                // radio not declared Mine, from its local network with no
                // NoPhysicalAccess declaration, starting PC audio no longer
                // switches the owner's mic input to PC, and when that input is
                // not PC this says computer audio will not reach the
                // transmitter and names both remedies. Said once per PC-audio
                // start, on the application's own account; classified on the
                // reasoning of the line above, with the same persistent-versus-
                // forgettable question for Noel. DECIDE ALL THREE TOGETHER.
                // DRAFT awaiting Noel.

                // Junkbox #312, repair rounds one and two, 2026-10-20: what a
                // 60 m arrival says about the mode it asked for, replacing
                // "mode set to ..." which could be false. Round two (ruled by
                // Noel): the request went out ("switching", claiming no
                // confirmation, because nothing on the path hears the radio
                // agree), or it was refused while transmitting, or there was
                // no slice of ours or no running command loop. The refusals
                // speak about the MODE only, so they cannot contradict the
                // arrival's frequency in the same sentence. FRAGMENTS appended
                // to an arrival (the band jump's, the rotation's, or typed
                // entry's), never spoken alone, so TextOnly is the only honest
                // classification. Their sibling settings.band.mode_advisory
                // ("mode set to ...") had no caller since round two and was
                // RETIRED, deleted from the lexicon, on Noel's 2026-10-21 07:39
                // ruling ("yes, retire the sentences"). APPROVED as written
                // by Noel at the 2026-10-21 fold (Fold 2).
                "settings.band.mode_not_set",
                "settings.band.mode_not_set_transmitting",
                "settings.band.mode_switching",

                // Junkbox #639, 2026-10-20: the brief clause the connect
                // briefing speaks when the station phase ends Unconfirmed.
                // REWORDED, which is what forced it out of the manifest: it
                // read "Station restore not confirmed; nothing added on top."
                // and Noel said on 2026-10-19 that "'nothing was restored on
                // top' makes no sense to me." It now says what happened and
                // what it cost the operator (the transmit and microphone setup
                // that runs only on a confirmed station was skipped), in a
                // whole sentence. Until the same day it was spoken on EVERY
                // owner connect, because the completion default was the bench
                // B placeholder; it is now reachable only when the evidence
                // genuinely did not arrive. The wording was APPROVED as
                // written by Noel at the 2026-10-21 fold (Fold 3).
                //
                // delivery: forgettable, request-scoped, no receipt. It is a
                // standalone thing the application says on its own account, so
                // not TextOnly. Its premise is what THIS connect's station
                // phase concluded, which cannot stop being true, so
                // request-scoped is the honest contract; and the connect
                // briefing retains it by SpeechSubject.ProfileStationOutcome
                // through its own owed-utterance mechanics, not through this
                // descriptor, so a retained shelf life here would be a second
                // record of one obligation. No receipt: the long form carries
                // the remedy and nothing is alarming about an unconfirmed
                // restore that kept the slices. Its siblings -- the long form
                // settings.profile_station.unconfirmed, and failed_brief and
                // not_restored_brief, which say "nothing else changed" and
                // "nothing changed on top" in the same family -- are legacy and
                // still frozen. WHEN THE PASS REACHES THEM, DECIDE ALL FOUR
                // TOGETHER.
                "settings.profile_station.unconfirmed_brief",

                // Junkbox #469, 2026-10-20: the profile comparison report now
                // refuses while keyed and runs in the background, so it needs
                // its own start sentence (one for a run that loads profiles,
                // one for a radio where the comparison is left out), its own
                // transmit refusal, and its own failure sentence: the restore
                // capture's start sentence named a "restore" capture (since
                // retired, below), and its not_while_transmitting said "this
                // export" and was RETIRED on Noel's 2026-10-21 11:03 ruling.
                // Classified rather than frozen because they were written here
                // (#720). delivery: null - TextOnly - on the #726 reasoning:
                // each is spoken ONLY in answer to the operator's own press of
                // the Tools menu item, the start and refusal at once and the
                // failure as the delayed answer to that press, and the
                // application never says any of them on its own account. Their
                // siblings settings.profile.report_saved and the
                // settings.restore_export.* family are legacy and still frozen;
                // WHEN THE PASS REACHES THEM, DECIDE THE TWO REPORTS' SENTENCES
                // TOGETHER. APPROVED by Noel at the 2026-10-21 fold (Fold 7). The start and
                // refusal sentences were reworded in his words; the failure
                // sentence is as written.
                "settings.profile.report_start",
                "settings.profile.report_start_no_walk",
                "settings.profile.report_not_while_transmitting",
                "settings.profile.report_failed",
                // Repair round one (Codex's four blockers, 2026-10-20), same
                // reasoning and classification as the block above. The start
                // sentence now names what the walk loads, through the three
                // FRAGMENTS settings.profile.report_kinds.*, which are never
                // spoken alone; a fourth start sentence covers a run that has
                // nothing to compare; the transmit refusal names what the run
                // would load. The walker's three lines for a key-up mid-walk
                // are spoken as progress, in answer to the press that started
                // the walk, and the two reports' finished sentences for a walk
                // stopped by a key-up are the delayed answer to that press.
                // Siblings settings.restore_export.saved_* are legacy and
                // frozen; DECIDE THEM WITH THESE. APPROVED by Noel at the 2026-10-21 fold (Fold 7).
                "settings.profile.report_start_nothing_to_compare",
                "settings.profile.report_kinds.global",
                "settings.profile.report_kinds.tx",
                "settings.profile.report_kinds.mic",
                "settings.profile.report_kinds.and",
                "settings.profile_walk.stopped_keyed",
                // Same round, the restore capture's start sentence, which had
                // the comparison's defect one method over: one sentence,
                // "every stored profile is loaded", whatever would load. Three
                // sentences now, chosen from its plan. The legacy
                // settings.restore_export.start was RETIRED, deleted from the
                // lexicon, on Noel's 2026-10-21 07:39 ruling. APPROVED by Noel at the 2026-10-21 fold (Fold 7).
                // The start sentences were reworded in his words.
                "settings.restore_export.start_walk",
                "settings.restore_export.start_nothing_to_walk",
                "settings.restore_export.start_no_walk",
                // Round two, Noel's ruling 2026-10-20 ("I'd say what profile
                // radio's on now"): a walk stopped by a key-up puts NOTHING
                // back, so the wait-and-put-back sentences of round one are
                // gone. What replaces them says which profile the radio is on
                // and that the original was not put back, built from the
                // fragments below, which are never spoken alone; the stop
                // line, both finished sentences and both files use the same
                // words. Also: the list-missing start sentences (an unreadable
                // list is not "nothing to compare") and the restore refusal
                // that names only the kinds it would load. Same classification
                // and reasoning as above. APPROVED by Noel at the 2026-10-21 fold (Fold 7).
                // The list-missing start sentences and the restore refusal
                // were reworded in his words.
                // Round four (Noel, 2026-10-20: stop naming profiles after a
                // transmit stop and claim nothing we cannot prove): every
                // fragment that named a profile after a stop is gone. The stop
                // line (settings.profile_walk.stopped_keyed, classified above)
                // and the two finished sentences now carry one fixed piece of
                // advice instead. APPROVED by Noel at the 2026-10-21 fold (Fold 7).
                // As written.
                "settings.profile.report_saved_stopped",
                "settings.restore_export.saved_stopped",
                "settings.profile.report_start_list_missing",
                "settings.profile.report_start_lists_missing",
                "settings.restore_export.not_while_transmitting_kinds",
                // Round six (Codex reverify5: "sent back" is unprovable too,
                // because Done means queued and FlexLib's setter can skip the
                // send): a run that requested any load ends with the stop
                // line's manual route and claims nothing about what reached the
                // radio; one that requested none says only that it was not
                // moved, which is provable. The legacy
                // settings.restore_export.saved_put_back / saved_not_put_back
                // ("back on", which nothing can prove) were RETIRED, deleted
                // from the lexicon, on Noel's 2026-10-21 07:39 ruling.
                // Same classification and reasoning as above. APPROVED by Noel at the 2026-10-21 fold (Fold 7).
                // As written.
                "settings.profile.report_saved_loaded",
                "settings.restore_export.saved_loaded",
                "settings.restore_export.saved_nothing_moved",

                // Same track, follow-up (Noel 07:55, 08:09): the answers to the
                // Radio menu's Switch Mic Input Back to PC, and the action
                // fragment the change-nothing refusal takes as {action}.
                // TextOnly on the #726 and #777 reasoning: each is spoken ONLY
                // in answer to the operator's own press, never on the
                // application's account, and an answer that says whether the
                // radio did what was asked must not be shortened by the
                // verbosity machinery. The repair round's start-time sentence
                // audio.mic.not_switched_not_ours was RETIRED the same morning,
                // when Noel ruled (08:10) that the start always switches to PC;
                // it never reached main. APPROVED as written by Noel at the
                // 2026-10-21 fold (Fold 4).
                // The confirmed and not-confirmed answers (switch_back_done,
                // switch_back_not_confirmed) were RETIRED the same day, when
                // Noel ruled (09:41) the item should do what a manual change
                // does: queue the write and claim nothing ("switching").
                "audio.mic.switch_back_switching",
                "audio.mic.switch_back_no_pc_audio",
                "audio.mic.switch_back_already_pc",
                // Same reasoning, same day: the answer when the press was made in
                // a computer-audio session that ended before it could act, so a
                // later session's saved value is never touched by it.
                // APPROVED as written by Noel at the 2026-10-21 fold (Fold 4).
                "audio.mic.switch_back_session_changed",
                "settings.guard.action.mic_input_pc",
                // Same track, round five (Noel 12:28: "do it the way we do the
                // full duplex check ... verify later then automatically speak
                // it"): the one late answer to the same press, spoken once
                // when the radio refuses the switch. TextOnly for the same
                // reason as its siblings: it exists only because the operator
                // pressed Switch Mic Input Back to PC, and it says whether the
                // radio did what was asked. APPROVED as written by Noel at
                // the 2026-10-21 fold (Fold 4).
                "audio.mic.switch_back_refused",

                // #787 recorder round 12: Audio Workshop button labels name
                // the active or Loopback Check listening slice. TextOnly because
                // the button's visible text and accessible name carry the words.
                "audio.iq.record_active_slice",
                "audio.iq.record_listening_slice",
                "audio.iq.play_active_slice",
                "audio.iq.play_active_slice_length",
                "audio.iq.play_listening_slice",
                "audio.iq.play_listening_slice_length",

                // #787 Tape Deck track 4: the Recorded QSOs window, its Save As
                // and Prune windows, and the words for rows, positions, trim
                // marks, sizes and refusals. All DRAFT PROSE awaiting Noel.
                // TextOnly because each is a control's own label or is spoken
                // through ScreenReaderOutput under SpeechSubject.RecordedQsos,
                // as track 2's recorder entries are.
                "audio.qsos.title",
                "audio.qsos.description",
                "audio.qsos.list",
                "audio.qsos.list_help",
                "audio.qsos.empty",
                "audio.qsos.unreadable_one",
                "audio.qsos.unreadable_many",
                "audio.qsos.step",
                "audio.qsos.step_help",
                "audio.qsos.step_one_second",
                "audio.qsos.step_seconds",
                "audio.qsos.step_one_minute",
                "audio.qsos.play",
                "audio.qsos.pause",
                "audio.qsos.resume",
                "audio.qsos.stop",
                "audio.qsos.position",
                "audio.qsos.position_value",
                "audio.qsos.set_start",
                "audio.qsos.set_end",
                "audio.qsos.selection_label",
                "audio.qsos.save_as",
                "audio.qsos.delete",
                "audio.qsos.prune",
                "audio.qsos.close",
                "audio.qsos.when_today",
                "audio.qsos.when_yesterday",
                "audio.qsos.when_date",
                "audio.qsos.row",
                "audio.qsos.row_named",
                "audio.qsos.length_unknown",
                "audio.qsos.recording_now",
                "audio.qsos.the_start",
                "audio.qsos.the_end",
                "audio.qsos.at",
                "audio.qsos.at_start",
                "audio.qsos.at_end",
                "audio.qsos.no_position",
                "audio.qsos.playing_from_start",
                "audio.qsos.playing_from",
                "audio.qsos.playing_unknown",
                "audio.qsos.paused",
                "audio.qsos.resumed",
                "audio.qsos.nothing_selected",
                "audio.qsos.move_failed",
                "audio.qsos.marks_cleared",
                "audio.qsos.type_flac",
                "audio.qsos.file_name",
                "audio.qsos.file_name_help",
                "settings.recorder.playback_level_unchanged",
                "settings.recorder.playback_level_lowered6",
                "settings.recorder.playback_level_lowered12",
                "settings.recorder.playback_level_lowered18",
                "settings.recorder.playback_level_lowered24",
                "settings.recorder.playback_level_muted",
                "audio.qsos.still_recording",
                "audio.qsos.play_refused_transmitting",
                "audio.qsos.vox_unconfirmed",
                "audio.qsos.trim_start_set",
                "audio.qsos.trim_end_set",
                "audio.qsos.trim_start_refused",
                "audio.qsos.trim_end_refused",
                "audio.qsos.whole",
                "audio.qsos.selection",
                "audio.qsos.delete_title",
                "audio.qsos.delete_message",
                "audio.qsos.delete_question",
                "audio.qsos.delete_yes",
                "audio.qsos.delete_no",
                "audio.qsos.deleted_next",
                "audio.qsos.deleted_last",
                "audio.qsos.refused_missing",
                "audio.qsos.refused_no_bin",
                "audio.qsos.refused_bin_off",
                "audio.qsos.refused_too_large",
                "audio.qsos.refused_outside",
                "audio.qsos.refused_failed",
                "audio.qsos.prune_title",
                "audio.qsos.prune_by_count",
                "audio.qsos.prune_by_age",
                "audio.qsos.prune_count",
                "audio.qsos.prune_days",
                "audio.qsos.prune_by_keep",
                "audio.qsos.prune_keep",
                "audio.qsos.prune_preview_label",
                "audio.qsos.prune_list",
                "audio.qsos.prune_preview_one",
                "audio.qsos.prune_preview_many",
                "audio.qsos.prune_preview_none",
                "audio.qsos.prune_in_use",
                "audio.qsos.prune_bad_number",
                "audio.qsos.prune_move_one",
                "audio.qsos.prune_move_many",
                "audio.qsos.prune_cancel",
                "audio.qsos.prune_confirm_title",
                "audio.qsos.prune_confirm_one",
                "audio.qsos.prune_confirm_many",
                "audio.qsos.prune_confirm_question",
                "audio.qsos.pruned",
                "audio.qsos.pruned_one",
                "audio.qsos.pruned_partly",
                "audio.qsos.saveas_title",
                "audio.qsos.saveas_name",
                "audio.qsos.saveas_folder",
                "audio.qsos.saveas_browse",
                "audio.qsos.saveas_type",
                "audio.qsos.saveas_range",
                "audio.qsos.saveas_channels",
                "audio.qsos.saveas_mono",
                "audio.qsos.saveas_stereo",
                "audio.qsos.saveas_bitrate",
                "audio.qsos.saveas_kbps",
                "audio.qsos.saveas_save",
                "audio.qsos.saveas_cancel",
                "audio.qsos.saveas_type_flac",
                "audio.qsos.saveas_type_wav",
                "audio.qsos.saveas_type_mp3",
                "audio.qsos.saveas_type_opus",
                "audio.qsos.saveas_type_m4a",
                "audio.qsos.saveas_range_whole",
                "audio.qsos.saveas_saving",
                "audio.qsos.saveas_saved",
                "audio.qsos.saveas_replace_title",
                "audio.qsos.saveas_replace",
                "audio.qsos.saveas_replace_question",
                "audio.qsos.saveas_failed",
                "audio.qsos.saveas_no_name",
                "audio.qsos.saveas_bad_name",
                "audio.qsos.saveas_same_file",
                "audio.qsos.saveas_no_folder",
                "audio.qsos.saveas_mp3_unavailable",
                "audio.qsos.saveas_m4a_unavailable",
                "audio.qsos.saveas_type_help",
                "audio.qsos.size_kilobytes",
                "audio.qsos.size_megabytes",
                "audio.qsos.size_gigabytes",
                "audio.qsos.being_saved",
                // 2026-10-25 bench rulings (Noel): tenths, clear marks, button help.
                "audio.qsos.clear_selection",
                "audio.qsos.one_second",
                "audio.qsos.n_seconds",
                "audio.qsos.one_minute",
                "audio.qsos.n_minutes",
                "audio.qsos.stopped_to_start",
                "audio.qsos.play_head_start",
                "audio.qsos.whole_length_unknown",
                "audio.qsos.recording_selection",
                "audio.qsos.no_start_mark",
                "audio.qsos.no_end_mark",
                "audio.qsos.start_cleared",
                "audio.qsos.end_cleared",
                "audio.qsos.row_named_saved",
                "audio.qsos.row_saved",
                "audio.qsos.type_wav",
                "audio.qsos.type_mp3",
                "audio.qsos.type_opus",
                "audio.qsos.type_m4a",
                "audio.qsos.saveas_saved_month",
                "audio.qsos.flac_only",
                "audio.qsos.play_help",
                "audio.qsos.stop_help",
                "audio.qsos.position_help",
                "audio.qsos.set_start_help",
                "audio.qsos.set_end_help",
                "audio.qsos.selection_help",
                "audio.qsos.clear_selection_help",
                "audio.qsos.save_as_help",
                "audio.qsos.delete_help",
                "audio.qsos.prune_help",
                "audio.qsos.close_help",
                // #835, Check your audio (Track A): the five ruled method
                // names, the method's help and status line, the one-sentence
                // reasons a method is missing, Off the air's readings in the
                // reading box, the tone's refusal while the reference is armed,
                // and the Workshop's bench-tools lines. TextOnly on the #456
                // reasoning above: control labels and status values the dialog
                // shows, echoed through its existing speech path where it
                // speaks at all. Draft wording awaits Noel.
                "audio.check.method.mic_test",
                "audio.check.method.monitor",
                "audio.check.method.record",
                "audio.check.method.off_air",
                "audio.check.method.loopback",
                "audio.check.method_help",
                "audio.check.method_status_label",
                "audio.check.status.mic_test",
                "audio.check.status.monitor",
                "audio.check.status.record",
                "audio.check.status.off_air",
                "audio.check.status.loopback",
                "audio.check.missing.smartlink",
                "audio.check.missing.one_receiver",
                "audio.check.missing.no_transverter",
                "audio.check.offair_this_take",
                "audio.check.offair_last_take",
                "audio.check.offair_measures",
                "audio.check.offair_no_take",
                "audio.check.offair_unavailable",
                "audio.check.offair_sent_heard",
                "audio.tone.reference_already_armed",
                "audio.diagnostics.bench_idle",
                "audio.diagnostics.bench_smartlink",
                // Track A fix round 2: the sentence for Escape when the radio
                // is keyed by something other than this app.
                "audio.check.escape_radio_still_transmitting",
                // Track A fix round 2: a teardown that could not be confirmed
                // before the radio went.
                "audio.check.teardown_unconfirmed",
                // Track A fix round 3: restores withheld because the radio kept
                // transmitting, and the refusal while a teardown holds admission.
                "audio.check.teardown_withheld",
                "audio.check.teardown_refuses",
                // Track A fix round 4: an IQ take refused while a teardown holds admission.
                "audio.iq.reason.teardown-in-progress",
                // Check your audio Track B (#835): the VOX send box and the sends that
                // key the radio with VOX on, including a computer recording to
                // the air (Tape Deck phase 2). Draft prose, awaiting Noel.
                "audio.send.box_label",
                "audio.send.box_help",
                "audio.send.box_on",
                "audio.send.box_off",
                "audio.send.what_computer",
                "audio.send.what_slice",
                "audio.send.sending",
                "audio.send.sent",
                "audio.send.stopped",
                "audio.send.interrupted",
                "audio.send.ran_long",
                "audio.send.taken_over",
                "audio.send.source_stopped",
                "audio.send.not_sent",
                "audio.send.message_taken_over",
                "audio.send.message_source_stopped",
                "audio.send.vox_unconfirmed",
                "audio.send.no_computer_recording",
                "audio.send.not_yet_transmitting",
                "audio.send.command_help",
                // Check your audio Track B, round 2 (#835): the long-send warning,
                // the ten-minute pause, and Play pausing and carrying on a send;
                // and the loader refusing a file rather than shortening it.
                // Draft prose, awaiting Noel.
                "audio.send.file_too_long",
                "audio.send.long_title",
                "audio.send.long_message",
                "audio.send.long_pause_play",
                "audio.send.long_pause_message",
                "audio.send.long_id",
                "audio.send.long_question",
                "audio.send.long_yes",
                "audio.send.long_no",
                "audio.send.cancelled",
                "audio.send.nothing_keys_now",
                "audio.send.resume_play",
                "audio.send.resume_message",
                "audio.send.paused_unkeyed",
                "audio.send.paused_mic",
                "audio.send.paused_id_unkeyed",
                "audio.send.paused_id_mic",
                "audio.send.resuming",
                "audio.send.resume_needs_key",
                "audio.send.resume_refused",
                "audio.send.resume_refused_ended",
                "audio.send.stopped_paused",
                "audio.send.paused_disconnected",
                // Track A, fix round 2026-10-28: the one short sentence the
                // "About this method" line shows per method (ruled 21:20), and
                // how its accessible name joins the label to it.
                "audio.check.about.mic_test",
                "audio.check.about.monitor",
                "audio.check.about.record",
                "audio.check.about.off_air",
                "audio.check.about.loopback",
                "audio.check.about_named",
                // Track A, fix round 2026-10-28: the new first method, checking
                // this computer's microphone (ruled 20:30): its name, its
                // description and short sentence, and the reading box before it runs.
                "audio.check.method.computer_mic",
                "audio.check.status.computer_mic",
                "audio.check.about.computer_mic",
                "audio.check.computer_mic.idle",
                // Track A, fix round 2026-10-28: Ctrl+F1 for every control on
                // the Workshop's Diagnostics page that had none (ruled 20:48:
                // short labels, and Ctrl+F1 explains them).
                "audio.diagnostics.help.diagnostic_take",
                "audio.diagnostics.help.capacity",
                "audio.diagnostics.help.detune_plus",
                "audio.diagnostics.help.detune_minus",
                "audio.diagnostics.help.recovery",
                "audio.diagnostics.help.separate_pan",
                "audio.diagnostics.help.full_duplex",
                "audio.diagnostics.help.bench_status",
                "audio.diagnostics.help.rx_result",
                "audio.diagnostics.help.tx_result",
                "audio.diagnostics.help.evidence",
                "audio.diagnostics.help.bench_smartlink",
                // Track A, fix round 2026-10-28: the Fix tool button in both
                // windows (ruled 21:41 and 21:46), and the three failure
                // sentences that now name it, classified as they changed.
                "audio.fix_tool.label",
                "audio.fix_tool.help",
                "audio.fix_tool.pointer",
                "audio.fix_tool.wait_for_check",
                "audio.check.could_not_start",
                "audio.check.no_recording_available",
                "audio.check.radio_did_not_key",
                // Track A, fix round 2026-10-28: the Check your audio button in
                // Home's Audio and Slice expander (Noel's request, 19:52).
                "audio.fields.check_your_audio",
                "audio.fields.check_your_audio_help",
                // Check your audio Track C, fix round 2026-10-28 (#835): the one
                // countdown, its spoken count and lead-in, and the Countdown group
                // in Settings. Draft prose, awaiting Noel.
                "audio.countdown.count",
                "audio.countdown.lead_in",
                "audio.countdown.method.mic_test",
                "audio.countdown.method.monitor",
                "audio.countdown.method.record",
                "audio.countdown.method.off_air",
                "audio.countdown.method.loopback",
                "audio.countdown.method.other",
                "audio.countdown.method.computer_mic",
                "audio.countdown.group",
                "audio.countdown.enabled_label",
                "audio.countdown.enabled_help",
                "audio.countdown.seconds_label",
                "audio.countdown.seconds_help",
                "audio.countdown.pitch_label",
                "audio.countdown.pitch_preset",
                "audio.countdown.pitch_preset_default",
                "audio.countdown.pitch_custom",
                "audio.countdown.custom_pitch_label",
                "audio.countdown.pitch_help",
                "audio.countdown.pip_ms_label",
                "audio.countdown.pip_ms_help",
                "audio.countdown.lead_in_label",
                "audio.countdown.lead_in_help",
                "audio.countdown.sample_label",
                "audio.countdown.sample_help",
                // Codex round 1: a count whose words could not be delivered stops.
                "audio.countdown.not_heard",
                // Check your audio Track B, fix round 2026-10-28 (#835): push to
                // talk's first line says a box send it took over has stopped.
                // Draft prose, awaiting Noel.
                "audio.send.message_taken_over_ptt",
                "audio.send.taken_over_ptt",
                // The same round: Play from computer pauses on the speakers too,
                // so its Home button says pause or carry on. Draft prose.
                "audio.recorder.pause_play_from_computer_button",
                "audio.recorder.carry_on_play_from_computer_button",
                // The same round: a slipped bare letter with both a Ctrl and an
                // Alt chord names both (ruled 2026-10-28 21:25). TextOnly on
                // leader.near_miss's reasoning, beside which it sits. Draft prose.
                "leader.near_miss_two",
                // Check your audio Track C, fix round 2026-10-29 (#835): the
                // countdown's pip sound, ruled 00:51. Draft prose, awaiting Noel.
                "audio.countdown.pip_sound_label",
                "audio.countdown.pip_sound_default",
                "audio.countdown.pip_sound_help",
            };

            Assert.Equal(3135, unclassified);   // Track A 2026-10-28: the Workshop's removed Check Microphone button took its two sentences with it, and three failure sentences that now name the Fix tool were classified
            Assert.All(classified, key =>
                Assert.True(key.StartsWith("facts.", StringComparison.Ordinal)
                            || Array.IndexOf(classifiedOutsideFacts, key) >= 0,
                    key + " is classified but is neither on the facts surface nor named in this test. "
                    + "Classifying is the ruled direction (Noel, 2026-10-05); add it to the list with "
                    + "the track that classified it, so the set stays enumerated."));
            foreach (string key in classifiedOutsideFacts)
                Assert.Contains(key, classified);
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
