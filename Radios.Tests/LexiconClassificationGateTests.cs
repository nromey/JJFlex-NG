#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The gate that makes "there is no default" real.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #617 named the expensive mistake outright: a wrong default applied to
    /// hundreds of strings at once. The answer is that there is no default at
    /// all — <see cref="DeliveryClassification.Unclassified"/> is an error
    /// state. But an error state nothing checks is just a value, so this is
    /// where it becomes an error.
    /// </para>
    /// <para>
    /// <b>The rule, in one line:</b> every shipped entry is either classified,
    /// or listed in the migration manifest with the fingerprint it had when it
    /// was frozen. Both halves are enforced in both directions, so the
    /// exception set can only shrink and a key cannot be edited while hiding
    /// inside it.
    /// </para>
    /// <para>
    /// <b>This is a test and release gate, and it says so.</b> An ordinary
    /// compile does not run it, and nothing here claims otherwise.
    /// </para>
    /// </remarks>
    // In the RadioConfig statics collection because it names Lexicon statics.
    // This class only ever READS the shipped baseline and never loads an
    // overlay, so it cannot itself disturb the process-wide catalogue — but the
    // isolation rule is deliberately one hop wider than that, and carving an
    // exemption for a class that "only reads" is how the rule stops working.
    [Collection(RadioConfigStaticsCollection.Name)]
    public class LexiconClassificationGateTests
    {
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

        private static string ManifestText()
        {
            string path = Path.Combine(RepoRoot(),
                LexiconSchema.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "The migration manifest is missing (looked at " + path + "). Without it, an " +
                "unclassified key has nothing to fail against, and the gate would pass by " +
                "checking nothing.");
            return File.ReadAllText(path);
        }

        private static IReadOnlyDictionary<string, LexiconEntry> Shipped()
        {
            var all = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal);
            foreach (string partition in Lexicon.Partitions)
                foreach (var pair in LexiconBaseline.FromShipped(partition))
                    all[pair.Key] = pair.Value;
            return all;
        }

        [Fact]
        public void EveryShippedEntryIsEitherClassifiedOrFrozenInTheManifest()
        {
            var shipped = Shipped();
            var manifest = LexiconSchema.ParseManifest(ManifestText());

            var unclassifiedAndUnfrozen = new List<string>();
            var manifestLines = new List<string>();
            foreach (var pair in shipped)
            {
                if (pair.Value.Classification != DeliveryClassification.Unclassified) continue;
                if (manifest.ContainsKey(pair.Key)) continue;
                unclassifiedAndUnfrozen.Add(pair.Key);
                manifestLines.Add(pair.Key + "\t" + LexiconSchema.Fingerprint(pair.Value));
            }
            manifestLines.Sort(StringComparer.Ordinal);

            Assert.True(unclassifiedAndUnfrozen.Count == 0,
                "These keys carry no delivery classification and are not in the migration " +
                "manifest, so nothing knows how long their information stays worth saying:\n  "
                + string.Join("\n  ", unclassifiedAndUnfrozen)
                + "\n\nA new key must be classified when it is written. There is no default "
                + "shelf life to fall back on, deliberately — guessing one for a message nobody "
                + "classified is the mistake this field exists to prevent."
                + "\n\nIf instead these are legacy strings that predate the store and are arriving "
                + "from a branch the sweep has not reached (#629), the lines to add to "
                + LexiconSchema.ManifestPath + ", in its alphabetical order, are exactly these — "
                + "computed by LexiconSchema.Fingerprint, never by hand:\n"
                + string.Join("\n", manifestLines));
        }

        [Fact]
        public void AClassifiedEntryHasLeftTheManifest()
        {
            // The other direction, and the one that makes the set one-way. A
            // key that has been classified but is still listed would keep its
            // exemption, so the manifest would stop shrinking while looking
            // like it had.
            var shipped = Shipped();
            var manifest = LexiconSchema.ParseManifest(ManifestText());

            var stillListed = new List<string>();
            foreach (var pair in shipped)
            {
                if (pair.Value.Classification == DeliveryClassification.Unclassified) continue;
                if (manifest.ContainsKey(pair.Key)) stillListed.Add(pair.Key);
            }

            Assert.True(stillListed.Count == 0,
                "These keys are classified but are still listed as migration exceptions. Remove "
                + "their lines from " + LexiconSchema.ManifestPath + ":\n  "
                + string.Join("\n  ", stillListed));
        }

        [Fact]
        public void TheManifestNamesNoKeyThatNoLongerExists()
        {
            var shipped = Shipped();
            var manifest = LexiconSchema.ParseManifest(ManifestText());

            var ghosts = new List<string>();
            foreach (string key in manifest.Keys)
                if (!shipped.ContainsKey(key)) ghosts.Add(key);

            Assert.True(ghosts.Count == 0,
                "The manifest exempts keys that are no longer in any partition, so it is "
                + "describing a corpus that has moved on:\n  " + string.Join("\n  ", ghosts));
        }

        [Fact]
        public void AFrozenEntryStillSaysWhatItSaidWhenItWasFrozen()
        {
            // This is what stops a key being reworded under cover of its
            // exemption. Changing the words changes the fingerprint, and the
            // gate then asks for the classification the entry should have
            // gained along the way.
            var shipped = Shipped();
            var manifest = LexiconSchema.ParseManifest(ManifestText());

            var changed = new List<string>();
            var manifestLines = new List<string>();
            foreach (var pair in manifest)
            {
                if (!shipped.TryGetValue(pair.Key, out LexiconEntry? entry)) continue;
                string now = LexiconSchema.Fingerprint(entry);
                if (string.Equals(now, pair.Value, StringComparison.Ordinal)) continue;
                changed.Add(pair.Key + " (frozen " + pair.Value + ", now " + now + ")");
                manifestLines.Add(pair.Key + "\t" + now);
            }
            manifestLines.Sort(StringComparer.Ordinal);

            Assert.True(changed.Count == 0,
                "These entries were edited while still listed as migration exceptions. An entry "
                + "somebody is rewriting is an entry somebody is looking at, which is the moment "
                + "to classify it — give it a delivery envelope and take its line out of "
                + LexiconSchema.ManifestPath + ":\n  " + string.Join("\n  ", changed)
                + "\n\nIf the rewording is being accepted under the exemption for now (#629 records "
                + "that tension, and that it wants a ruling), the replacement lines are exactly these — "
                + "computed by LexiconSchema.Fingerprint, never by hand:\n"
                + string.Join("\n", manifestLines));
        }

        [Fact]
        public void TheGateIsLookingAtTheWholeCorpusAndNotAtNothing()
        {
            // The positive control. Every assertion above is an ABSENCE check,
            // and an absence check over an empty set passes for the wrong
            // reason — a broken repository-root walk would make all four of
            // them green.
            var shipped = Shipped();
            var manifest = LexiconSchema.ParseManifest(ManifestText());

            // The floor was > 2900 until 2026-09-25, which is the corpus's own
            // size and therefore an INVENTORY dressed as a control. Quarantining
            // Track I's 222 manifest rows (#627) dropped the manifest to 2,757
            // and this failed — reporting an absent track as a broken read,
            // which is the one thing a positive control must never do.
            //
            // A control answers "did we read the corpus at all". A broken
            // repository-root walk returns nothing, so any floor well clear of
            // zero catches it. Pinning that floor to the corpus size buys no
            // extra detection and guarantees the control breaks every time the
            // corpus legitimately changes.
            Assert.True(shipped.Count > 2000,
                "only " + shipped.Count + " shipped entries were read");
            Assert.True(manifest.Count > 2000,
                "only " + manifest.Count + " manifest lines were read");

            // What the old number was reaching for, done in the way that
            // actually holds: every partition contributed. This catches a
            // partial read — one JSON failing to load — which no single total
            // ever could, and it needs no maintenance when the corpus grows or
            // a track's keys come and go.
            foreach (string partition in Lexicon.Partitions)
                Assert.True(LexiconBaseline.FromShipped(partition).Count > 0,
                    "partition '" + partition + "' contributed no entries, so the corpus was " +
                    "only partly read and every absence check above passed over a hole");
        }

        [Fact]
        public void TheFingerprintChangesWhenTheWordsDoAndNotWhenTheClassificationDoes()
        {
            LexiconEntry plain = LexiconEntry.Plain("Hello");
            LexiconEntry reworded = LexiconEntry.Plain("Hello there");
            LexiconEntry classified = plain.WithDelivery(
                DeliveryClassification.Message,
                new DeliveryDescriptor(ShelfLife.Forgettable, ValidityContracts.RequestScoped, null, ReceiptPolicy.None));

            Assert.NotEqual(LexiconSchema.Fingerprint(plain), LexiconSchema.Fingerprint(reworded));

            // The fingerprint is of the TEXT. Classifying an entry does not
            // change what it says, and the two checks above are what catch the
            // classification going missing.
            Assert.Equal(LexiconSchema.Fingerprint(plain), LexiconSchema.Fingerprint(classified));
        }

        [Fact]
        public void ALadderFingerprintDistinguishesItsTiers()
        {
            LexiconEntry a = LexiconEntry.Ladder("C", "T", "Ch");
            LexiconEntry b = LexiconEntry.Ladder("C", "T", "Chatty");
            LexiconEntry c = LexiconEntry.Ladder("C", "T", "Ch", "D");

            Assert.NotEqual(LexiconSchema.Fingerprint(a), LexiconSchema.Fingerprint(b));
            Assert.NotEqual(LexiconSchema.Fingerprint(a), LexiconSchema.Fingerprint(c));
        }

        [Fact]
        public void TheShippedCorpusPassesItsOwnCrossEntryChecks()
        {
            Assert.Empty(LexiconSchema.Validate(Shipped()));
        }
    }
}
