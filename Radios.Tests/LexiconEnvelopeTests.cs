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
            Assert.Equal(18, ladders);
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
                // THE PASS REACHES IT, DECIDE BOTH TOGETHER. The wording is a
                // DRAFT awaiting Noel.
                "audio.value_layer.unknown_key",
                // Same track, same reasoning: the line for a bare digit, which
                // a value layer now refuses before asking the registry because
                // forwarded it would start frequency entry. DRAFT awaiting Noel.
                "audio.value_layer.typed_number",

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
                // TextOnly is the only honest classification; global keeps
                // the approved legacy fragments settings.guard.action.profile_save
                // / profile_delete, which stay frozen and are shared with the
                // hold's refusal. These four are DRAFTS awaiting Noel; the two
                // sentences above were APPROVED the same day.
                "settings.guard.action.tx_profile_create",
                "settings.guard.action.tx_profile_delete",
                "settings.guard.action.mic_profile_create",
                "settings.guard.action.mic_profile_delete",
                // Same track, round two (Codex B3): the honest receipt for a
                // save whose command did not go out within its bound, which
                // until then was announced as saved. Spoken only in answer to
                // the press that asked for the save; TextOnly on the same
                // reasoning; the existing receipts settings.profile.saved and
                // settings.station_layout.saved beside them are legacy and
                // still frozen - DECIDE EACH PAIR TOGETHER. DRAFTS awaiting
                // Noel.
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
                "audio.iq.aligned",
                "audio.iq.not_aligned",
                "audio.iq.sent_unavailable",
                "audio.iq.tone_identified",
                "audio.iq.tone_not_identified",
                "audio.iq.playing_sent",
                "audio.iq.difference",
                "audio.iq.louder",
                "audio.iq.quieter",
                "audio.iq.more_compressed",
                "audio.iq.more_peaky",
                "audio.iq.need_two",
                "audio.iq.previous_comparison",
                "audio.iq.unlike_or_unaligned",
                "audio.iq.reason.sent-checksum",
                "audio.iq.reason.arrangement-not-confirmed",
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

                // Junkbox #469, 2026-10-20: the profile comparison report now
                // refuses while keyed and runs in the background, so it needs
                // its own start sentence (one for a run that loads profiles,
                // one for a radio where the comparison is left out), its own
                // transmit refusal, and its own failure sentence: the restore
                // capture's settings.restore_export.start names a "restore"
                // capture and its not_while_transmitting says "this export".
                // Classified rather than frozen because they were written here
                // (#720). delivery: null - TextOnly - on the #726 reasoning:
                // each is spoken ONLY in answer to the operator's own press of
                // the Tools menu item, the start and refusal at once and the
                // failure as the delayed answer to that press, and the
                // application never says any of them on its own account. Their
                // siblings settings.profile.report_saved and the
                // settings.restore_export.* family are legacy and still frozen;
                // WHEN THE PASS REACHES THEM, DECIDE THE TWO REPORTS' SENTENCES
                // TOGETHER. All four wordings are DRAFTS awaiting Noel.
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
                // frozen; DECIDE THEM WITH THESE. All DRAFTS awaiting Noel.
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
                // settings.restore_export.start is no longer spoken and stays
                // frozen; DECIDE IT (retire or reword) WITH THESE. DRAFTS.
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
                // and reasoning as above. All DRAFTS awaiting Noel.
                // Round four (Noel, 2026-10-20: stop naming profiles after a
                // transmit stop and claim nothing we cannot prove): every
                // fragment that named a profile after a stop is gone. The stop
                // line (settings.profile_walk.stopped_keyed, classified above)
                // and the two finished sentences now carry one fixed piece of
                // advice instead. DRAFTS.
                "settings.profile.report_saved_stopped",
                "settings.restore_export.saved_stopped",
                "settings.profile.report_start_list_missing",
                "settings.profile.report_start_lists_missing",
                "settings.restore_export.not_while_transmitting_kinds",
            };

            Assert.Equal(3152, unclassified);
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
