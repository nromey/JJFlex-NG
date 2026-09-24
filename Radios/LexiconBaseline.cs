#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Radios
{
    /// <summary>
    /// Read the SHIPPED wording, and only the shipped wording — no operator
    /// overlay, no global state, nothing cached and nothing mutated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a separate door.</b> <see cref="Lexicon.Load(IReadOnlyList{string})"/>
    /// is the run-time path: it layers the operator's files on top and fills a
    /// process-wide dictionary that everything else reads. Tests and tools that
    /// want to know what the PROJECT ships cannot use it — the answer would
    /// depend on whose machine ran it, and calling it would leave the app's own
    /// catalog loaded behind them.
    /// </para>
    /// <para>
    /// <b>And the readers that need this are the ones most likely to go quiet.</b>
    /// The accessibility and vocabulary scans read the partitions by hand today
    /// and skip anything that is not a bare string. A migration that turns
    /// entries into envelopes would leave them passing while inspecting fewer
    /// entries, which reads as success. A falling inspected-entry count is not a
    /// clean migration, so these callers get a projection that widens with the
    /// shape rather than narrowing against it.
    /// </para>
    /// </remarks>
    public static class LexiconBaseline
    {
        /// <summary>
        /// Parse partition text exactly as the shipped baseline is parsed:
        /// strictly, so a malformed entry is an error rather than a shrug.
        /// </summary>
        public static IReadOnlyDictionary<string, LexiconEntry> Parse(string json)
            => Lexicon.Parse(json);

        /// <summary>
        /// The shipped partition, straight from the embedded resource. No
        /// overlay is consulted and the process-wide catalog is untouched.
        /// </summary>
        public static IReadOnlyDictionary<string, LexiconEntry> FromShipped(string partition)
        {
            if (string.IsNullOrEmpty(partition)) throw new ArgumentNullException(nameof(partition));

            string resource = "Radios.Lexicon." + partition + ".json";
            Assembly assembly = typeof(Lexicon).Assembly;

            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream == null)
            {
                throw new LexiconException(
                    "The shipped lexicon partition '" + partition + "' is missing from the assembly " +
                    "(expected embedded resource '" + resource + "').");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Lexicon.Parse(reader.ReadToEnd());
        }

        /// <summary>Every shipped partition, merged into one map.</summary>
        public static IReadOnlyDictionary<string, LexiconEntry> AllShipped()
        {
            var all = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal);
            foreach (string partition in Lexicon.Partitions)
            {
                foreach (KeyValuePair<string, LexiconEntry> pair in FromShipped(partition))
                    all[pair.Key] = pair.Value;
            }
            return all;
        }

        /// <summary>
        /// Every piece of TEXT an entry holds, as (key, tier, text).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A plain string contributes one row, tier <c>"plain"</c>. A ladder
        /// contributes one row per tier it defines. An envelope contributes
        /// whichever of those its <c>text</c> holds. <b>Nothing is skipped for
        /// having the wrong shape</b>, which is the entire point: a scan that
        /// silently stops seeing part of the corpus looks exactly like a scan
        /// that found nothing wrong.
        /// </para>
        /// </remarks>
        public static IEnumerable<(string Key, string Tier, string Text)> TextProjection(
            IReadOnlyDictionary<string, LexiconEntry> entries)
        {
            if (entries == null) yield break;

            foreach (KeyValuePair<string, LexiconEntry> pair in entries)
            {
                LexiconEntry entry = pair.Value;
                if (!entry.IsLadder)
                {
                    string? plain = entry.Resolve(VerbosityLevel.Chatty);
                    if (!string.IsNullOrEmpty(plain)) yield return (pair.Key, "plain", plain!);
                    continue;
                }

                foreach (string tier in entry.DefinedTiers)
                {
                    VerbosityLevel level = tier switch
                    {
                        "critical" => VerbosityLevel.Critical,
                        "terse" => VerbosityLevel.Terse,
                        "diagnostic" => VerbosityLevel.Diagnostic,
                        _ => VerbosityLevel.Chatty,
                    };
                    string? text = entry.Resolve(level);
                    if (!string.IsNullOrEmpty(text)) yield return (pair.Key, tier, text!);
                }
            }
        }

        /// <summary>
        /// The one text a caller should show or speak for a key, at the chatty
        /// tier — the projection for callers that genuinely want one string per
        /// key rather than every tier.
        /// </summary>
        /// <remarks>
        /// Distinct from reading the raw JSON value, which is what these
        /// callers used to do: a ladder has no single raw value, so reading one
        /// either threw or silently dropped the key.
        /// </remarks>
        public static IReadOnlyDictionary<string, string> TextByKey(
            IReadOnlyDictionary<string, LexiconEntry> entries)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (entries == null) return map;

            foreach (KeyValuePair<string, LexiconEntry> pair in entries)
            {
                string? text = pair.Value.Resolve(VerbosityLevel.Chatty);
                if (!string.IsNullOrEmpty(text)) map[pair.Key] = text!;
            }
            return map;
        }
    }
}
