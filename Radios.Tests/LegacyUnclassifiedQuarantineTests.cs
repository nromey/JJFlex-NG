#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The guard that makes the quarantine safe to use. #627.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the quarantine exists.</b> The migration manifest names 222 alarm
    /// keys belonging to Track I, which is not merged. Those keys do not exist
    /// on this branch, so the classification gate fails on them. Noel ruled
    /// 2026-09-25 that they are <b>commented, not deleted</b> — *"I agree with
    /// you on quarantining"* — because a deletion that a later merge has to
    /// reverse is an absence, and absences are the failure this project does
    /// not notice. Nothing breaks when one is forgotten; the list is simply
    /// shorter, which reads as fine.
    /// </para>
    /// <para>
    /// <b>The quarantine on its own is just a tidier deletion.</b> It is worth
    /// something only if the suite notices when it should have ended. That is
    /// what this class is for, and it is the half of #627 that actually carries
    /// the ruling.
    /// </para>
    /// <para>
    /// <b>Two rules, and the second is not in #627 — it is here because the
    /// mechanism invites the abuse.</b> Commenting a line out makes a failing
    /// key stop failing. That is the point when Track I is the reason, and it is
    /// a silent hole in the gate for every other reason. So the file may only
    /// quarantine the thing it was opened for.
    /// </para>
    /// </remarks>
    // In the RadioConfig statics collection because it names Lexicon statics
    // (#605). Reading the shipped baseline only still takes the collection: the
    // isolation rule is deliberately one hop wider than strictly necessary, and
    // carving an exemption for a class that "only reads" is how the rule stops
    // working.
    [Collection(RadioConfigStaticsCollection.Name)]
    public class LegacyUnclassifiedQuarantineTests
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
                "The migration manifest is missing (looked at " + path + "). Without it this " +
                "guard would pass by checking nothing, which is the failure it exists to stop.");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// A commented-out ENTRY, as distinct from a note to the reader. Both
        /// begin with <c>#</c>; only an entry carries a tab, because the live
        /// format is key-tab-fingerprint. Header prose never does.
        /// </summary>
        private static IReadOnlyList<string> QuarantinedKeys() => QuarantinedKeysIn(ManifestText());

        private static IReadOnlyList<string> QuarantinedKeysIn(string manifestText)
        {
            var keys = new List<string>();
            foreach (string raw in manifestText.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] != '#') continue;

                string body = line.Substring(1).Trim();
                int tab = body.IndexOf('\t');
                if (tab <= 0) continue;          // a note, not an entry

                keys.Add(body.Substring(0, tab).Trim());
            }
            return keys;
        }

        private static IReadOnlyDictionary<string, LexiconEntry> Shipped()
        {
            var all = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal);
            foreach (string partition in Lexicon.Partitions)
                foreach (var pair in LexiconBaseline.FromShipped(partition))
                    all[pair.Key] = pair.Value;
            return all;
        }

        /// <summary>
        /// The rule that ends the quarantine. It passes today because Track I's
        /// keys are absent, and fails the day Track I merges without
        /// uncommenting them — which is the whole reason a comment was chosen
        /// over a deletion.
        /// </summary>
        [Fact]
        public void NoShippedKeyIsLeftQuarantined()
        {
            var shipped = Shipped();
            var stillShipping = new List<string>();

            foreach (string key in QuarantinedKeys())
                if (shipped.ContainsKey(key)) stillShipping.Add(key);

            Assert.True(stillShipping.Count == 0,
                "These keys now SHIP but are still commented out in " + LexiconSchema.ManifestPath +
                ", so the classification gate is not looking at them (#627):\n  " +
                string.Join("\n  ", stillShipping) +
                "\n\nThis is the expected failure when Track I merges. Uncomment those lines — " +
                "restore them exactly, fingerprints included — and delete the quarantine header " +
                "above them. Do NOT delete the lines to make this pass: that puts the keys back " +
                "outside the gate, which is what the quarantine was chosen to avoid.");
        }

        /// <summary>
        /// The rule that keeps the quarantine from becoming a way to silence any
        /// inconvenient key. Not in #627 — added because the mechanism invites
        /// it: commenting a line out makes a failing key stop failing, and that
        /// is only legitimate for the reason the file was opened for.
        /// </summary>
        [Fact]
        public void NothingIsQuarantinedExceptTrackIsAlarms()
        {
            var unexplained = new List<string>();

            foreach (string key in QuarantinedKeys())
                if (!key.StartsWith("alarms.", StringComparison.Ordinal)) unexplained.Add(key);

            Assert.True(unexplained.Count == 0,
                "Only Track I's alarm keys may be quarantined in " + LexiconSchema.ManifestPath +
                ", and these are not alarm keys (#627):\n  " + string.Join("\n  ", unexplained) +
                "\n\nCommenting a manifest line out removes that key from the classification " +
                "gate. For Track I that is a deliberate, documented, temporary exception. For " +
                "anything else it is a silent hole in the gate. Classify the key, or freeze it " +
                "live with its fingerprint, rather than hiding it here.");
        }

        /// <summary>
        /// The quarantine ENDED on 2026-10-01, when Track IJK brought Track I in
        /// and restored the 222 lines live. This used to be the positive control
        /// that the quarantine was non-empty; it is now the guard that it stays
        /// empty. A commented entry in the manifest is a key hidden from the
        /// classification gate, and with the one documented reason gone there
        /// is no legitimate reason left.
        /// </summary>
        /// <remarks>
        /// The class was kept rather than deleted when the quarantine closed:
        /// the two rules above still hold (nothing may be commented, and if
        /// something is, it had better be an alarm key), and a guard that
        /// outlives its first reason costs nothing while a deleted one is an
        /// absence nobody notices. The parser is exercised by
        /// <see cref="TheParserStillRecognisesACommentedEntry"/>, so an empty
        /// result here is a real zero, not a regex that stopped matching.
        /// </remarks>
        [Fact]
        public void TheQuarantineStaysClosed()
        {
            var quarantined = QuarantinedKeys();
            Assert.True(quarantined.Count == 0,
                "The manifest " + LexiconSchema.ManifestPath + " has commented-out ENTRIES again:\n  " +
                string.Join("\n  ", quarantined) +
                "\n\nThe #627 quarantine closed on 2026-10-01 when Track I merged. Commenting a " +
                "manifest line out hides that key from the classification gate. Classify the key, " +
                "or freeze it live with its fingerprint; do not quarantine it.");
        }

        /// <summary>
        /// The positive control for <see cref="TheQuarantineStaysClosed"/>: a
        /// guard over an empty set passes for the wrong reason if the parser
        /// stopped recognising a commented entry, which is one regex away. So
        /// feed the parser one synthetic commented entry and one note, and
        /// require it to tell them apart.
        /// </summary>
        [Fact]
        public void TheParserStillRecognisesACommentedEntry()
        {
            string sample = "# a note to the reader, no tab\n# alarms.sample\t0123456789abcdef\nlive.key\tfedcba9876543210\n";
            var keys = QuarantinedKeysIn(sample);
            Assert.Equal(new[] { "alarms.sample" }, keys);
        }
    }
}
