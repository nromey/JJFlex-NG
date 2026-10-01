#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Radios
{
    /// <summary>One thing wrong with a classification, named so it can be fixed.</summary>
    public sealed class LexiconSchemaFinding
    {
        public LexiconSchemaFinding(string key, string message)
        {
            Key = key;
            Message = message;
        }

        public string Key { get; }
        public string Message { get; }

        public override string ToString() => Key + ": " + Message;
    }

    /// <summary>
    /// The checks that cannot be made while parsing one entry, and the
    /// migration manifest that says which keys are allowed to be unclassified
    /// for now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these are separate from the parser.</b> A history key can name an
    /// entry in another partition, and a validity contract is registered by a
    /// domain owner at run time. Neither question can be answered from inside
    /// the parse of one entry, and asking it there would make the answer depend
    /// on which file happened to load first.
    /// </para>
    /// <para>
    /// <b>This is a gate, not a load-time behaviour.</b> A shipped baseline
    /// that fails these checks is a build defect that a test refuses; it is not
    /// something an ordinary compile runs today and this type does not pretend
    /// otherwise.
    /// </para>
    /// </remarks>
    public static class LexiconSchema
    {
        /// <summary>
        /// Where the migration manifest lives, relative to the repository root.
        /// </summary>
        /// <remarks>
        /// Beside the strings it describes, so the diff that classifies a
        /// partition and the diff that shrinks the exception set sit next to
        /// each other. The leading underscore follows the partitions' own
        /// note convention, and the extension keeps it out of the
        /// <c>Lexicon\*.json</c> embedding glob — a manifest that shipped as a
        /// partition would be parsed as one.
        /// </remarks>
        public const string ManifestPath = "Radios/Lexicon/_legacy-unclassified.tsv";

        /// <summary>
        /// Cross-entry checks over a whole loaded catalog.
        /// </summary>
        /// <param name="entries">Every entry, across every partition.</param>
        /// <param name="registeredContracts">
        /// Validity contract names some domain owner has registered, or null to
        /// skip that check. Null is honest rather than lenient: in a test
        /// process no domain has registered anything, and reporting every cited
        /// contract as unknown would be noise, not evidence.
        /// </param>
        public static IReadOnlyList<LexiconSchemaFinding> Validate(
            IReadOnlyDictionary<string, LexiconEntry> entries,
            IReadOnlyCollection<string>? registeredContracts = null)
        {
            var findings = new List<LexiconSchemaFinding>();
            if (entries == null) return findings;

            foreach (KeyValuePair<string, LexiconEntry> pair in entries)
            {
                DeliveryDescriptor? delivery = pair.Value.Delivery;
                if (delivery == null) continue;

                if (delivery.HistoryKey != null)
                {
                    if (!entries.TryGetValue(delivery.HistoryKey, out LexiconEntry? history))
                    {
                        findings.Add(new LexiconSchemaFinding(pair.Key,
                            "names history key '" + delivery.HistoryKey + "', which is not in the " +
                            "catalog. The past-tense rendering has to exist before the present-tense " +
                            "one can be withdrawn in favour of it."));
                    }
                    else if (history.Classification != DeliveryClassification.TextOnly)
                    {
                        findings.Add(new LexiconSchemaFinding(pair.Key,
                            "names history key '" + delivery.HistoryKey + "', which is classified " +
                            history.Classification.ToString().ToLowerInvariant() + " rather than " +
                            "text-only. History is read through the explicit fact-read path and must " +
                            "never be reachable by an automatic current-message route."));
                    }
                }

                if (registeredContracts != null
                    && !ValidityContracts.IsBuiltIn(delivery.Validity)
                    && !Contains(registeredContracts, delivery.Validity))
                {
                    findings.Add(new LexiconSchemaFinding(pair.Key,
                        "names validity contract '" + delivery.Validity + "', which no domain owner " +
                        "has registered. A contract nobody can evaluate cannot say whether the " +
                        "message is still justified."));
                }
            }

            return findings;
        }

        private static bool Contains(IReadOnlyCollection<string> names, string wanted)
        {
            foreach (string name in names)
                if (string.Equals(name, wanted, StringComparison.Ordinal)) return true;
            return false;
        }

        // ────────────────────────────────────────────────────────────────
        //  The migration manifest
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A fingerprint of an entry's TEXT — what it says, at every tier it
        /// defines, and nothing about its classification.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is what makes the exception set one-way. The manifest records
        /// the shape and content an entry had when it was frozen, so a key
        /// cannot be edited and left unclassified, cannot be reworded under
        /// cover of its exemption, and cannot be quietly re-added after being
        /// converted. Changing the words changes the fingerprint, and the gate
        /// then asks for the classification the entry should have gained.
        /// </para>
        /// <para>
        /// Sixteen hex characters of SHA-256. Not a security boundary — an
        /// accident detector, sized to stay readable in a file a person
        /// reviews.
        /// </para>
        /// </remarks>
        public static string Fingerprint(LexiconEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            var sb = new StringBuilder();
            if (!entry.IsLadder)
            {
                sb.Append("plain\u0000").Append(entry.Resolve(VerbosityLevel.Chatty) ?? string.Empty);
            }
            else
            {
                sb.Append("ladder");
                foreach (string tier in entry.DefinedTiers)
                {
                    VerbosityLevel level = tier switch
                    {
                        "critical" => VerbosityLevel.Critical,
                        "terse" => VerbosityLevel.Terse,
                        "diagnostic" => VerbosityLevel.Diagnostic,
                        _ => VerbosityLevel.Chatty,
                    };
                    sb.Append('\u0000').Append(tier).Append('\u0001').Append(entry.Resolve(level) ?? string.Empty);
                }
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            var hex = new StringBuilder(16);
            for (int i = 0; i < 8; i++) hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            return hex.ToString();
        }

        /// <summary>
        /// Read a manifest: key to fingerprint. Blank lines and lines beginning
        /// with <c>#</c> are notes to whoever opens the file.
        /// </summary>
        public static IReadOnlyDictionary<string, string> ParseManifest(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return map;

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;

                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                map[line.Substring(0, tab)] = line.Substring(tab + 1).Trim();
            }
            return map;
        }

        /// <summary>
        /// Render a manifest. Sorted by key so a diff shows exactly which
        /// entries left the exception set.
        /// </summary>
        public static string RenderManifest(IReadOnlyDictionary<string, string> entries, string header)
        {
            var keys = new List<string>(entries.Keys);
            keys.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            foreach (string line in header.Split('\n'))
                sb.Append('#').Append(line.Length == 0 ? "" : " ").Append(line.TrimEnd('\r')).Append('\n');
            sb.Append('\n');
            foreach (string key in keys)
                sb.Append(key).Append('\t').Append(entries[key]).Append('\n');
            return sb.ToString();
        }
    }
}
