#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Every word the app says or shows, under a stable key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ships empty on purpose. This type lands with nothing calling it so it
    /// can be reviewed in isolation; the six extraction tracks fill the
    /// partitions afterwards. See
    /// <c>docs/planning/active/string-store-contract.md</c> for the rules every
    /// track follows, and why each one is the way it is.
    /// </para>
    /// <para>
    /// <b>Why this is called Lexicon and not Strings.</b> A
    /// <c>Radios.Strings</c> namespace would shadow
    /// <c>Microsoft.VisualBasic.Strings</c> for all 19 VB files that
    /// <c>Imports Radios</c>, so <c>Strings.Left</c> and friends would silently
    /// bind here. That is not hypothetical: a <c>Radios.Diagnostics</c>
    /// namespace once shadowed <c>System.Diagnostics</c> and broke
    /// <c>PersonalData.vb</c>, which is why the ruleset folder is called
    /// ChainChecks. Radios.csproj carries the standing rule. This type lives
    /// directly in <c>Radios</c> — no new namespace at all — so the whole
    /// category is sidestepped rather than re-entered.
    /// </para>
    /// <para>
    /// <b>Shipped baseline, operator overlay.</b> Each partition ships as an
    /// embedded resource, so it can never go missing from an install and needs
    /// no installer entry. A file of the same name under the settings folder
    /// layers on top, KEY BY KEY.
    /// </para>
    /// <para>
    /// That per-key merge is the one place this deliberately differs from
    /// <see cref="ChainChecks.RuleSetLoader"/>, which replaces a ruleset
    /// wholesale. The reason inverts: an operator editing one rule wants the
    /// built-ins gone, because a diagnostic nobody can predict is not a
    /// diagnostic. An operator editing one WORD does not want to lose the other
    /// four hundred in that file — they would each start rendering as their own
    /// key, and the app would speak <c>audio.device.picker_basic_mode</c> at
    /// him. Merging also means strings added by a later release still reach
    /// someone who has edited that partition.
    /// </para>
    /// <para>
    /// <b>Failure is asymmetric, for the same reason.</b> A malformed BASELINE
    /// throws: it is a build defect, a test catches it, and it must never reach
    /// anyone. A malformed OVERLAY is skipped with the baseline retained and the
    /// problem recorded — because that file is hand-edited by the operator, and
    /// a stray comma must not brick the program he controls his radio with.
    /// </para>
    /// </remarks>
    public static class Lexicon
    {
        /// <summary>Connect and session lifecycle.</summary>
        public const string Connect = "connect";

        /// <summary>Audio and DSP.</summary>
        public const string Audio = "audio";

        /// <summary>Settings and per-radio configuration.</summary>
        public const string Settings = "settings";

        /// <summary>Logging and cluster.</summary>
        public const string Logging = "logging";

        /// <summary>Earcon and CW vocabulary.</summary>
        public const string Earcon = "earcon";

        /// <summary>Help text — titles and labels, not the markdown bodies.</summary>
        public const string Help = "help";

        /// <summary>
        /// The Ctrl+J leader layer — what it says while it is armed, and what
        /// it says when it does not know the key you pressed.
        /// </summary>
        /// <remarks>
        /// Split out of <see cref="Settings"/> in Sprint 37 (#303). The leader
        /// is a way of driving the whole application from any focus position,
        /// not a Settings feature, and filing its vocabulary under settings
        /// made it look like one to everybody who went looking.
        /// </remarks>
        public const string Leader = "leader";

        /// <summary>
        /// The seven partitions, split for REVIEW rather than for speed. An
        /// in-memory dictionary is the same speed whichever file it loaded
        /// from; saying so here stops someone splitting a hot set across files
        /// chasing a gain that does not exist.
        /// </summary>
        public static IReadOnlyList<string> Partitions { get; } = new[]
        {
            Connect, Audio, Settings, Logging, Earcon, Help, Leader,
        };

        /// <summary>
        /// Partitions loaded eagerly at startup. Frequency and meter
        /// announcements fire many times a second and can never wait on a file.
        /// Help is the exception — it loads on first use.
        /// </summary>
        /// <remarks>
        /// Leader is eager on purpose even though it is small: it answers a
        /// KEYPRESS, and the first thing the layer ever says is the word it
        /// says while armed. A first-use file read there is a stall at exactly
        /// the moment the operator is waiting to hear whether the chord took.
        /// </remarks>
        public static IReadOnlyList<string> EagerPartitions { get; } = new[]
        {
            Connect, Audio, Settings, Logging, Earcon, Leader,
        };

        private static readonly object Gate = new object();

        private static readonly Dictionary<string, LexiconEntry> Entries =
            new Dictionary<string, LexiconEntry>(StringComparer.Ordinal);

        private static readonly HashSet<string> LoadedPartitions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static LexiconLoadReport _report = LexiconLoadReport.Empty;

        /// <summary>Starts at one so a pinned generation of zero reads as "never pinned".</summary>
        private static long _catalogGeneration = 1;

        /// <summary>
        /// What the last load found — how many keys, which partitions, and
        /// every problem. Never null.
        /// </summary>
        public static LexiconLoadReport LastReport
        {
            get { lock (Gate) return _report; }
        }

        /// <summary>How many keys are currently resolvable.</summary>
        public static int Count
        {
            get { lock (Gate) return Entries.Count; }
        }

        // ────────────────────────────────────────────────────────────────
        //  Lookup
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The text for a key. A key that is not there comes back AS ITSELF —
        /// never empty, never silent.
        /// </summary>
        /// <remarks>
        /// Silence is invisible to exactly the operator who most needs the
        /// text, and indistinguishable from "nothing was supposed to happen."
        /// A key in the ear is a bug report that writes itself.
        /// <para>
        /// That fallback also does a second job nobody planned: because a key
        /// has a shape no real utterance ever has, an unextracted string
        /// announces itself in the output transcript and is machine-detectable.
        /// See <see cref="LooksLikeKey"/>.
        /// </para>
        /// </remarks>
        public static string Get(string key)
        {
            return Get(key, VerbosityLevel.Chatty);
        }

        /// <summary>
        /// The text for a key at a verbosity tier. Where a key carries a
        /// ladder, this picks its tier; where it carries one plain string, the
        /// tier is ignored and that string comes back.
        /// </summary>
        public static string Get(string key, VerbosityLevel level)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;

            EnsureLoadedFor(key);

            LexiconEntry? entry;
            lock (Gate)
            {
                if (!Entries.TryGetValue(key, out entry)) return key;
            }

            return entry.Resolve(level) ?? key;
        }

        /// <summary>
        /// The text for a key, with named placeholders filled in.
        /// </summary>
        /// <remarks>
        /// Named, never positional. A translator reordering a sentence breaks
        /// <c>{0}</c> and <c>{1}</c> silently — the string still formats, it
        /// just says something false. A name either matches or is left standing
        /// in the output where it can be seen.
        /// </remarks>
        public static string Get(string key, params (string Name, object? Value)[] args)
        {
            return Fill(Get(key, VerbosityLevel.Chatty), args);
        }

        /// <summary>
        /// The text for a key at a verbosity tier, with named placeholders
        /// filled in.
        /// </summary>
        public static string Get(string key, VerbosityLevel level, params (string Name, object? Value)[] args)
        {
            return Fill(Get(key, level), args);
        }

        // ────────────────────────────────────────────────────────────────
        //  The typed lookup
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The key, its delivery contract and its unrendered arguments —
        /// everything <see cref="Get(string)"/> throws away.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Use this wherever a message will be published as a fact.</b> A
        /// string already returned by <see cref="Get(string)"/> has lost its
        /// key and its metadata, and there is no recovering them: matching the
        /// text back against the catalog is guessing, a second dictionary from
        /// sentence to descriptor is the same dictionary twice, and editing the
        /// JSON alone cannot change behaviour that never saw the key.
        /// </para>
        /// <para>
        /// <b>It never throws and never refuses to answer.</b> An unknown key
        /// comes back as an unclassified message whose text is the key itself —
        /// the same visible fallback the plain API has always given — so a
        /// caller that has already admitted a fact does not lose it because a
        /// lookup failed. What an unclassified message may NOT do is be
        /// presented automatically; that is
        /// <see cref="LexiconMessage.IsPresentableMessage"/>, and the refusal
        /// belongs to the delivery side rather than here.
        /// </para>
        /// </remarks>
        public static LexiconMessage Message(string key, params (string Name, object? Value)[] args)
        {
            IReadOnlyList<(string Name, object? Value)> arguments =
                args == null || args.Length == 0
                    ? Array.Empty<(string, object?)>()
                    : (IReadOnlyList<(string, object?)>)args;

            if (string.IsNullOrEmpty(key))
                return new LexiconMessage(string.Empty, null, arguments, CatalogGeneration);

            EnsureLoadedFor(key);

            lock (Gate)
            {
                Entries.TryGetValue(key, out LexiconEntry? entry);
                return new LexiconMessage(key, entry, arguments, _catalogGeneration);
            }
        }

        /// <summary>
        /// Which wording catalog is in force. Advanced by every load and every
        /// <see cref="Forget"/>.
        /// </summary>
        /// <remarks>
        /// A prepared presentation plan pins this. A wording reload therefore
        /// invalidates plans that have not started, so a sentence renders from
        /// one coherent catalog rather than half from each — and a
        /// classification changed by a later software version cannot
        /// retroactively reinterpret evidence stored under the old one.
        /// </remarks>
        public static long CatalogGeneration
        {
            get { lock (Gate) return _catalogGeneration; }
        }

        /// <summary>Is this key resolvable right now?</summary>
        public static bool Contains(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            EnsureLoadedFor(key);
            lock (Gate) return Entries.ContainsKey(key);
        }

        /// <summary>Every key currently loaded, for tests and for tooling.</summary>
        public static IReadOnlyCollection<string> Keys
        {
            get { lock (Gate) return new List<string>(Entries.Keys); }
        }

        /// <summary>
        /// Substitute <c>{name}</c> placeholders. A placeholder with no
        /// matching argument is LEFT STANDING rather than blanked — same
        /// reasoning as the missing-key fallback: a gap you can see beats a
        /// gap you cannot.
        /// </summary>
        public static string Fill(string text, params (string Name, object? Value)[] args)
        {
            if (string.IsNullOrEmpty(text) || args == null || args.Length == 0) return text;
            if (text.IndexOf('{') < 0) return text;

            var sb = new StringBuilder(text);
            foreach ((string name, object? value) in args)
            {
                if (string.IsNullOrEmpty(name)) continue;
                string rendered = value switch
                {
                    null => string.Empty,
                    IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
                    _ => value.ToString() ?? string.Empty,
                };
                sb.Replace("{" + name + "}", rendered);
            }
            return sb.ToString();
        }

        // ────────────────────────────────────────────────────────────────
        //  The runtime detector
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Does this text look like a key rather than something a person would
        /// say? The runtime half of the three checks: no speech event's text
        /// may look like a key, and any that does is a string somebody forgot
        /// to extract.
        /// </summary>
        /// <remarks>
        /// This lives here, beside the fallback that produces the shape, so the
        /// app and the test can never drift apart on what a key looks like.
        /// <para>
        /// Deliberately strict — it must not fire on real speech. Requires at
        /// least one dot, no whitespace, and every character lowercase
        /// alphanumeric, dot or underscore. "S meter" has a space. "73" has no
        /// dot. "14.250" is all digits, so the segment rule rejects it: every
        /// dotted segment must start with a letter.
        /// </para>
        /// </remarks>
        public static bool LooksLikeKey(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text!.IndexOf('.') < 0) return false;

            foreach (char c in text)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_';
                if (!ok) return false;
            }

            string[] segments = text.Split('.');
            if (segments.Length < 2) return false;

            foreach (string segment in segments)
            {
                if (segment.Length == 0) return false;
                char first = segment[0];
                if (first < 'a' || first > 'z') return false;
            }

            return true;
        }

        // ────────────────────────────────────────────────────────────────
        //  Loading
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Load the eager partitions. Call once at startup so problems surface
        /// then, rather than at the first thing the operator does.
        /// </summary>
        /// <exception cref="LexiconException">
        /// A shipped baseline is malformed or missing. That is a build defect,
        /// not an operator's problem, and it must fail here.
        /// </exception>
        public static LexiconLoadReport Load()
        {
            return Load(EagerPartitions);
        }

        /// <summary>Load specific partitions.</summary>
        public static LexiconLoadReport Load(IReadOnlyList<string> partitions)
        {
            if (partitions == null) throw new ArgumentNullException(nameof(partitions));

            var problems = new List<LexiconProblem>();
            int overlays = 0;

            foreach (string partition in partitions)
            {
                bool alreadyLoaded;
                lock (Gate) alreadyLoaded = LoadedPartitions.Contains(partition);
                if (alreadyLoaded) continue;

                Dictionary<string, LexiconEntry> baseline = ReadBaseline(partition);

                Dictionary<string, LexiconEntry>? overlay = ReadOverlay(partition, problems);
                if (overlay != null)
                {
                    overlays++;
                    Merge(baseline, overlay, problems, partition);
                }

                lock (Gate)
                {
                    foreach (KeyValuePair<string, LexiconEntry> pair in baseline)
                    {
                        Entries[pair.Key] = pair.Value;
                    }
                    LoadedPartitions.Add(partition);
                    _catalogGeneration++;
                }
            }

            LexiconLoadReport report;
            lock (Gate)
            {
                report = new LexiconLoadReport(
                    Entries.Count,
                    new List<string>(LoadedPartitions),
                    problems,
                    overlays);
                _report = report;
            }

            foreach (LexiconProblem problem in problems)
            {
                Tracing.TraceLine("Lexicon: " + problem, TraceLevel.Warning);
            }

            return report;
        }

        /// <summary>
        /// Forget everything, so the next lookup re-reads. For the operator who
        /// has just edited an overlay, and for tests.
        /// </summary>
        public static void Forget()
        {
            lock (Gate)
            {
                Entries.Clear();
                LoadedPartitions.Clear();
                _report = LexiconLoadReport.Empty;
                _catalogGeneration++;
            }
        }

        /// <summary>
        /// Where overlays are read from. Normally null, meaning the operator's
        /// settings folder. Set it to point the store somewhere else — tests do
        /// this so they never read the real operator's files, and it is the hook
        /// a portable install would use.
        /// </summary>
        public static string? OverlayDirectoryOverride { get; set; }

        /// <summary>
        /// Where an overlay for this partition would go, whether or not one is
        /// there. Worth showing an operator who wants to write one.
        /// </summary>
        public static string OverlayPath(string partition)
        {
            string? dir = OverlayDirectoryOverride;
            if (string.IsNullOrEmpty(dir))
            {
                string? baseDir = RadioConfig.ResolvedBaseDirectory;
                if (string.IsNullOrEmpty(baseDir)) return string.Empty;
                dir = Path.Combine(baseDir, "lexicon");
            }
            return Path.Combine(dir, FileNameFor(partition));
        }

        /// <summary>The partition a key belongs to — its first dotted segment.</summary>
        public static string PartitionOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            int dot = key.IndexOf('.');
            return dot <= 0 ? string.Empty : key.Substring(0, dot);
        }

        private static string FileNameFor(string partition)
        {
            return partition + ".json";
        }

        private static void EnsureLoadedFor(string key)
        {
            string partition = PartitionOf(key);
            if (partition.Length == 0) return;

            bool loaded;
            lock (Gate) loaded = LoadedPartitions.Contains(partition);
            if (loaded) return;

            // Only a real partition gets loaded on demand; an unknown first
            // segment is a malformed key, and the fallback will show it.
            foreach (string known in Partitions)
            {
                if (string.Equals(known, partition, StringComparison.OrdinalIgnoreCase))
                {
                    Load(new[] { known });
                    return;
                }
            }
        }

        private static Dictionary<string, LexiconEntry> ReadBaseline(string partition)
        {
            string resource = "Radios.Lexicon." + FileNameFor(partition);
            Assembly assembly = typeof(Lexicon).Assembly;

            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream == null)
            {
                throw new LexiconException(
                    "The shipped lexicon partition '" + partition + "' is missing from the " +
                    "assembly (expected embedded resource '" + resource + "'). This is a " +
                    "build defect, not an operator problem.");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            string json = reader.ReadToEnd();

            try
            {
                return Parse(json);
            }
            catch (JsonException ex)
            {
                throw new LexiconException(
                    "The shipped lexicon partition '" + partition + "' is not valid JSON: " +
                    ex.Message + ". This is a build defect, not an operator problem.", ex);
            }
        }

        private static Dictionary<string, LexiconEntry>? ReadOverlay(
            string partition, List<LexiconProblem> problems)
        {
            string path = OverlayPath(partition);
            if (string.IsNullOrEmpty(path)) return null;

            try
            {
                if (!File.Exists(path)) return null;

                var skipped = new List<string>();
                Dictionary<string, LexiconEntry> parsed =
                    Parse(File.ReadAllText(path, Encoding.UTF8), strict: false, skipped: skipped);

                if (skipped.Count != 0)
                {
                    // The file itself was readable; individual entries were not.
                    // Those keys keep the built-in wording and are named, so an
                    // edit that did not take is explainable rather than a
                    // mystery.
                    problems.Add(new LexiconProblem(partition, path,
                        skipped.Count + " entr" + (skipped.Count == 1 ? "y was" : "ies were") +
                        " skipped and left with the built-in wording, because the text was " +
                        "missing or the entry was malformed: " + string.Join(", ", skipped)));
                }

                return parsed;
            }
            catch (JsonException ex)
            {
                // The whole FILE could not be parsed — a stray comma or brace,
                // which breaks the structure rather than one entry. Skip the
                // overlay, keep the baseline, say what happened. A hand-edited
                // file must never brick the program he controls his radio with.
                problems.Add(new LexiconProblem(partition, path,
                    "Not valid JSON, so this file was ignored and the built-in wording " +
                    "is being used instead. " + ex.Message));
                return null;
            }
            catch (IOException ex)
            {
                problems.Add(new LexiconProblem(partition, path, "Could not be read. " + ex.Message));
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                problems.Add(new LexiconProblem(partition, path, "Could not be read. " + ex.Message));
                return null;
            }
        }

        /// <summary>
        /// Layer an overlay onto a baseline, KEY BY KEY. The overlay wins where
        /// it speaks and is silent everywhere else.
        /// </summary>
        /// <remarks>
        /// This is the whole design in four lines, and the one rule most likely
        /// to be "simplified" into a wholesale replace by someone copying
        /// <see cref="ChainChecks.RuleSetLoader"/> next door, which does replace
        /// wholesale and is right to. Replacing here would mean an operator who
        /// edits one word loses every other key in that partition, and each of
        /// those would then be spoken as its own key. It would also stop strings
        /// added by a later release from ever reaching anyone who had edited
        /// that file. Tested directly for exactly that reason.
        /// </remarks>
        internal static void Merge(
            Dictionary<string, LexiconEntry> baseline,
            Dictionary<string, LexiconEntry> overlay)
        {
            Merge(baseline, overlay, problems: null, partition: string.Empty);
        }

        /// <summary>
        /// Layer an overlay onto a baseline, KEY BY KEY, keeping the shipped
        /// DELIVERY contract on every key the operator rewords.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the migration's sharpest edge.</b> Before the envelope
        /// existed, this method replaced the whole entry for an overridden key,
        /// which was exactly right when an entry was only words. Under the new
        /// shape the same line would strip a safety message's shelf life, its
        /// validity contract, its history key and its receipt policy — and the
        /// result would still speak, so nothing would look broken. An operator
        /// who changes a sentence would silently turn a persistent warning into
        /// an unclassified one.
        /// </para>
        /// <para>
        /// <b>The split is: words are his, lifecycle is not.</b> An overlay
        /// that only gives text takes the shipped contract. An overlay that
        /// declares an IDENTICAL contract is accepted, because restating
        /// something true is not an error. An overlay that declares a DIFFERENT
        /// contract keeps its words, keeps the shipped contract, and is
        /// reported — refusing his words outright over a metadata mistake would
        /// punish the wrong half of the file.
        /// </para>
        /// <para>
        /// <b>A key the baseline does not have stays unclassified</b> whatever
        /// it declares. It remains readable, so nothing an operator added stops
        /// working, but it carries no authority for an automatic retained
        /// message: a wording file must not be able to mint a new safety
        /// message.
        /// </para>
        /// </remarks>
        internal static void Merge(
            Dictionary<string, LexiconEntry> baseline,
            Dictionary<string, LexiconEntry> overlay,
            List<LexiconProblem>? problems,
            string partition)
        {
            foreach (KeyValuePair<string, LexiconEntry> pair in overlay)
            {
                if (!baseline.TryGetValue(pair.Key, out LexiconEntry? shipped))
                {
                    LexiconEntry added = pair.Value;
                    if (added.Classification != DeliveryClassification.Unclassified)
                    {
                        added = added.WithDelivery(DeliveryClassification.Unclassified, null);
                        problems?.Add(new LexiconProblem(partition, OverlayPath(partition),
                            "'" + pair.Key + "' is not a key this release ships, so its wording is " +
                            "readable but its delivery classification was not applied. A wording " +
                            "file cannot introduce a new message the application will announce on " +
                            "its own account."));
                    }
                    baseline[pair.Key] = added;
                    continue;
                }

                if (pair.Value.Classification != DeliveryClassification.Unclassified
                    && !SameContract(shipped, pair.Value))
                {
                    problems?.Add(new LexiconProblem(partition, OverlayPath(partition),
                        "'" + pair.Key + "' tried to change its delivery classification, which is " +
                        "not wording — it decides how long the information stays worth saying and " +
                        "what makes it true. The new words were kept and the shipped classification " +
                        "was retained."));
                }

                // The shipped entry's contract, wearing the operator's words.
                baseline[pair.Key] = shipped.WithTextFrom(pair.Value);
            }
        }

        private static bool SameContract(LexiconEntry shipped, LexiconEntry overlay)
        {
            if (shipped.Classification != overlay.Classification) return false;
            if (shipped.Delivery == null) return overlay.Delivery == null;
            return shipped.Delivery.Equals(overlay.Delivery);
        }

        /// <summary>
        /// Parse one partition. Keys beginning with an underscore are notes to
        /// whoever opens the file, not entries.
        /// </summary>
        internal static Dictionary<string, LexiconEntry> Parse(string json)
        {
            return Parse(json, strict: true, skipped: null);
        }

        /// <summary>
        /// Parse one partition, optionally forgiving individual bad entries.
        /// </summary>
        /// <param name="strict">
        /// True for the shipped baseline: any bad entry throws, because it is a
        /// build defect. False for an operator's overlay: the offending key is
        /// skipped and named in <paramref name="skipped"/>, so one mistake does
        /// not discard four hundred good edits alongside it.
        /// </param>
        internal static Dictionary<string, LexiconEntry> Parse(
            string json, bool strict, List<string>? skipped)
        {
            var result = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json)) return result;

            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("A lexicon partition must be a JSON object at the top level.");
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Length == 0 || property.Name[0] == '_') continue;

                // JSON permits a duplicate key and every parser silently keeps
                // one of them. That must be an error here, because this is
                // exactly what a merge produces: six extraction tracks each
                // appending to the same partition, two of them independently
                // naming the same key with different words, and a union merge
                // that resolves cleanly while quietly deleting one wording.
                //
                // Nothing downstream could ever notice. The file parses, the
                // key resolves, the app speaks — just not what somebody wrote.
                if (result.ContainsKey(property.Name))
                {
                    throw new JsonException(
                        "Key '" + property.Name + "' appears more than once in this partition. " +
                        "A duplicate key is silently resolved by every JSON parser, so one of the " +
                        "two wordings would vanish with nothing to show for it. Merge them by hand.");
                }

                try
                {
                    switch (property.Value.ValueKind)
                    {
                        case JsonValueKind.String:
                            string text = property.Value.GetString() ?? string.Empty;

                            // An empty value is the one case the missing-key
                            // fallback cannot cover: the key IS there, so
                            // nothing looks absent, but Speak drops an empty
                            // string and the app says nothing at all. Silence
                            // is invisible to exactly the operator who most
                            // needs the words, and it is what clearing a line
                            // while editing produces. Never accept it.
                            if (string.IsNullOrWhiteSpace(text))
                            {
                                throw new JsonException(
                                    "Key '" + property.Name + "' has no text. An empty value would " +
                                    "make the app say nothing at all, which is never the right " +
                                    "answer — delete the key to fall back to the built-in wording, " +
                                    "or give it words.");
                            }

                            result[property.Name] = LexiconEntry.Plain(text);
                            break;

                        case JsonValueKind.Object:
                            result[property.Name] = IsEnvelope(property.Value)
                                ? ReadEnvelope(property.Name, property.Value)
                                : ReadLadder(property.Name, property.Value);
                            break;

                        default:
                            throw new JsonException(
                                "Key '" + property.Name + "' must be a string, a verbosity ladder " +
                                "object or a delivery envelope, but was " + property.Value.ValueKind + ".");
                    }
                }
                catch (JsonException) when (!strict)
                {
                    // An overlay is hand-edited. One bad entry loses that entry
                    // and nothing else — the key falls back to the built-in
                    // wording, and the operator is told which one.
                    skipped?.Add(property.Name);
                }
            }

            return result;
        }

        // ────────────────────────────────────────────────────────────────
        //  The version-2 envelope
        // ────────────────────────────────────────────────────────────────

        private const string TextField = "text";
        private const string DeliveryField = "delivery";
        private const string ShelfLifeField = "shelfLife";
        private const string ValidityField = "validity";
        private const string HistoryKeyField = "historyKey";
        private const string ReceiptField = "receipt";

        /// <summary>
        /// Is this object an envelope rather than a legacy ladder?
        /// </summary>
        /// <remarks>
        /// <para>
        /// The question has to be answered before either reader runs, because
        /// the two shapes are both JSON objects and the wrong reader gives a
        /// misleading error. An object naming <c>text</c> or <c>delivery</c> is
        /// an envelope; anything else is read as a ladder and is held to the
        /// ladder's own rules.
        /// </para>
        /// <para>
        /// <b>Naming <c>delivery</c> alone is enough</b>, deliberately, so that
        /// an envelope with a missing or misspelled <c>text</c> fails as a
        /// broken envelope rather than as an object full of unknown verbosity
        /// tiers. A misspelled metadata field must never be read as a tier —
        /// that is the reading that would silently discard a classification.
        /// </para>
        /// </remarks>
        private static bool IsEnvelope(JsonElement element)
        {
            foreach (JsonProperty field in element.EnumerateObject())
            {
                if (string.Equals(field.Name, TextField, StringComparison.Ordinal)) return true;
                if (string.Equals(field.Name, DeliveryField, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static LexiconEntry ReadEnvelope(string key, JsonElement element)
        {
            JsonElement? text = null;
            JsonElement? delivery = null;
            bool deliveryPresent = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (JsonProperty field in element.EnumerateObject())
            {
                // JSON permits a duplicate field and System.Text.Json hands us
                // both. Every parser resolves it silently to one of the two, so
                // an entry with two shelf lives would classify as whichever one
                // happened to win. That cannot be allowed to be quiet.
                if (!seen.Add(field.Name))
                {
                    throw new JsonException(
                        "Key '" + key + "' names '" + field.Name + "' more than once in its envelope. " +
                        "A duplicate field is silently resolved by every JSON parser, so one of the " +
                        "two values would take effect with nothing to show for it.");
                }

                if (string.Equals(field.Name, TextField, StringComparison.Ordinal))
                {
                    text = field.Value;
                }
                else if (string.Equals(field.Name, DeliveryField, StringComparison.Ordinal))
                {
                    deliveryPresent = true;
                    delivery = field.Value;
                }
                else
                {
                    throw new JsonException(
                        "Key '" + key + "' has an unknown envelope field '" + field.Name +
                        "'. An envelope holds 'text' and 'delivery' and nothing else — a misspelled " +
                        "field here would otherwise be discarded, taking a delivery classification " +
                        "with it.");
                }
            }

            if (text == null)
            {
                throw new JsonException(
                    "Key '" + key + "' has a delivery envelope with no 'text'. The words are the one " +
                    "part that is never optional.");
            }

            LexiconEntry entry = text.Value.ValueKind switch
            {
                JsonValueKind.String => LexiconEntry.Plain(RequireText(key, text.Value)),
                JsonValueKind.Object => ReadLadder(key, text.Value),
                _ => throw new JsonException(
                    "Key '" + key + "' has 'text' that is neither a string nor a verbosity ladder, " +
                    "but " + text.Value.ValueKind + "."),
            };

            if (!deliveryPresent)
            {
                // An envelope that carries text and says nothing about delivery
                // is still unclassified. Silence here is the same silence as a
                // bare legacy string, and gets the same answer: not a default,
                // an error state the gate refuses.
                return entry;
            }

            if (delivery!.Value.ValueKind == JsonValueKind.Null)
            {
                // "delivery": null is an ANSWER: this string is interface text
                // or a fragment, never something the application says on its
                // own account. It is not the same as saying nothing.
                return entry.WithDelivery(DeliveryClassification.TextOnly, null);
            }

            if (delivery.Value.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException(
                    "Key '" + key + "' has a 'delivery' that is neither null nor an object, but " +
                    delivery.Value.ValueKind + ". Null means 'not a standalone message'; an object " +
                    "is the delivery contract.");
            }

            return entry.WithDelivery(DeliveryClassification.Message, ReadDescriptor(key, delivery.Value));
        }

        private static DeliveryDescriptor ReadDescriptor(string key, JsonElement element)
        {
            string? shelfLife = null, validity = null, historyKey = null, receipt = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (JsonProperty field in element.EnumerateObject())
            {
                if (!seen.Add(field.Name))
                {
                    throw new JsonException(
                        "Key '" + key + "' names '" + field.Name + "' more than once in its delivery " +
                        "contract, so one of the two values would silently take effect.");
                }

                if (field.Value.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException(
                        "Key '" + key + "' has a delivery field '" + field.Name + "' that is not a " +
                        "string, but " + field.Value.ValueKind + ".");
                }

                string value = field.Value.GetString() ?? string.Empty;

                if (string.Equals(field.Name, ShelfLifeField, StringComparison.Ordinal)) shelfLife = value;
                else if (string.Equals(field.Name, ValidityField, StringComparison.Ordinal)) validity = value;
                else if (string.Equals(field.Name, HistoryKeyField, StringComparison.Ordinal)) historyKey = value;
                else if (string.Equals(field.Name, ReceiptField, StringComparison.Ordinal)) receipt = value;
                else
                {
                    throw new JsonException(
                        "Key '" + key + "' has an unknown delivery field '" + field.Name +
                        "'. A delivery contract holds shelfLife, validity, historyKey and receipt. " +
                        "In particular there is NO age or lifetime field: nothing decides staleness " +
                        "by counting seconds, and a TTL here would be exactly that.");
                }
            }

            if (shelfLife == null)
            {
                throw new JsonException(
                    "Key '" + key + "' has a delivery contract with no 'shelfLife'. There is no " +
                    "default: a message is perishable, persistent or forgettable, and guessing one " +
                    "of the three for a message nobody classified is the expensive mistake this " +
                    "field exists to prevent.");
            }

            ShelfLife life = shelfLife switch
            {
                "perishable" => ShelfLife.Perishable,
                "persistent" => ShelfLife.Persistent,
                "forgettable" => ShelfLife.Forgettable,
                _ => throw new JsonException(
                    "Key '" + key + "' has shelfLife '" + shelfLife + "'. The only legal values are " +
                    "perishable, persistent and forgettable, spelled in lower case. There is no " +
                    "fourth value and no default."),
            };

            if (string.IsNullOrWhiteSpace(validity))
            {
                throw new JsonException(
                    "Key '" + key + "' has a delivery contract with no 'validity'. Every classified " +
                    "message names the condition that makes it true — including a forgettable one, " +
                    "because no replay policy makes an obsolete FIRST presentation truthful. A " +
                    "message whose premise is its own asking names '" + ValidityContracts.RequestScoped +
                    "' explicitly.");
            }

            ReceiptPolicy policy;
            if (receipt == null)
            {
                throw new JsonException(
                    "Key '" + key + "' has a delivery contract with no 'receipt'. The earcon is the " +
                    "receipt, so whether this message has one is a decision somebody makes rather " +
                    "than an omission — say 'none' to decide it has none.");
            }

            switch (receipt)
            {
                case "none": policy = ReceiptPolicy.None; break;
                case "warning": policy = ReceiptPolicy.Warning; break;
                case "capture": policy = ReceiptPolicy.Capture; break;
                default:
                    throw new JsonException(
                        "Key '" + key + "' names receipt policy '" + receipt + "', which is not one " +
                        "of the established tone identities. The legal values are none, warning and " +
                        "capture — deliberately only the tones the operator already knows. Adding a " +
                        "new one is an accessibility decision, not a wording change.");
            }

            if (life != ShelfLife.Forgettable && string.IsNullOrWhiteSpace(historyKey))
            {
                throw new JsonException(
                    "Key '" + key + "' is " + shelfLife + ", so its information is retained after a " +
                    "failed attempt and has to be readable afterwards — it needs a 'historyKey' " +
                    "naming a past-tense rendering. A perishable event that missed its moment and a " +
                    "persistent condition that resolved unheard must both be readable without " +
                    "replaying a present-tense assurance under a history heading.");
            }

            return new DeliveryDescriptor(life, validity!, historyKey, policy);
        }

        /// <summary>
        /// A string value that is really there. Same rule as the bare legacy
        /// string: empty text is silence, and silence is invisible to exactly
        /// the operator who most needs the words.
        /// </summary>
        private static string RequireText(string key, JsonElement element)
        {
            string text = element.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new JsonException(
                    "Key '" + key + "' has no text. An empty value would make the app say nothing " +
                    "at all, which is never the right answer — delete the key to fall back to the " +
                    "built-in wording, or give it words.");
            }
            return text;
        }

        private static LexiconEntry ReadLadder(string key, JsonElement element)
        {
            string? critical = null, terse = null, chatty = null, diagnostic = null;
            var seenTiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (JsonProperty tier in element.EnumerateObject())
            {
                if (!seenTiers.Add(tier.Name))
                {
                    throw new JsonException(
                        "Key '" + key + "' names the '" + tier.Name + "' tier more than once, so one " +
                        "of the two wordings would vanish with nothing to show for it.");
                }

                if (tier.Value.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException(
                        "Key '" + key + "' has a ladder tier '" + tier.Name + "' that is not a string.");
                }

                string text = tier.Value.GetString() ?? string.Empty;

                // Same rule as a plain value: an empty tier is silence at that
                // verbosity, which the operator would experience as the app
                // going mute only when terse — nearly impossible to diagnose.
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new JsonException(
                        "Key '" + key + "' has an empty '" + tier.Name + "' tier. That would make " +
                        "the app say nothing at that verbosity — remove the tier to fall back, or " +
                        "give it words.");
                }

                if (string.Equals(tier.Name, "critical", StringComparison.OrdinalIgnoreCase)) critical = text;
                else if (string.Equals(tier.Name, "terse", StringComparison.OrdinalIgnoreCase)) terse = text;
                else if (string.Equals(tier.Name, "chatty", StringComparison.OrdinalIgnoreCase)) chatty = text;
                else if (string.Equals(tier.Name, "diagnostic", StringComparison.OrdinalIgnoreCase)) diagnostic = text;
                else
                {
                    throw new JsonException(
                        "Key '" + key + "' has an unknown ladder tier '" + tier.Name +
                        "'. Tiers are critical, terse, chatty and diagnostic.");
                }
            }

            if (critical == null && terse == null && chatty == null && diagnostic == null)
            {
                throw new JsonException("Key '" + key + "' is an empty object with no ladder tiers.");
            }

            return LexiconEntry.Ladder(critical, terse, chatty, diagnostic);
        }
    }

    /// <summary>
    /// One entry: text — either a plain string or a ladder across verbosity
    /// tiers — plus what the entry says about itself as a unit of delivery.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Text and delivery are separate halves on purpose.</b> The text is
    /// editable language; the delivery contract is an executable lifecycle
    /// policy. An operator overlay may replace the first and may never replace
    /// the second — see <see cref="Lexicon.Merge(Dictionary{string, LexiconEntry}, Dictionary{string, LexiconEntry}, List{LexiconProblem}?, string)"/>.
    /// </para>
    /// </remarks>
    public sealed class LexiconEntry
    {
        private readonly string? _plain;
        private readonly string? _critical;
        private readonly string? _terse;
        private readonly string? _chatty;
        private readonly string? _diagnostic;

        private LexiconEntry(string? plain, string? critical, string? terse, string? chatty, string? diagnostic,
                             DeliveryClassification classification, DeliveryDescriptor? delivery)
        {
            _plain = plain;
            _critical = critical;
            _terse = terse;
            _chatty = chatty;
            _diagnostic = diagnostic;
            Classification = classification;
            Delivery = delivery;
        }

        /// <summary>A single string, the same at every verbosity.</summary>
        public static LexiconEntry Plain(string text) =>
            new LexiconEntry(text, null, null, null, null, DeliveryClassification.Unclassified, null);

        /// <summary>A ladder. Any tier may be null; resolution falls downward.</summary>
        public static LexiconEntry Ladder(string? critical, string? terse, string? chatty, string? diagnostic = null)
            => new LexiconEntry(null, critical, terse, chatty, diagnostic, DeliveryClassification.Unclassified, null);

        /// <summary>
        /// How this key is classified as a unit of delivery. Every entry
        /// parsed from a legacy string or ladder is
        /// <see cref="DeliveryClassification.Unclassified"/>, which is an
        /// error state gated by tests — never a fourth shelf life and never a
        /// silent default.
        /// </summary>
        public DeliveryClassification Classification { get; }

        /// <summary>
        /// The delivery contract, non-null exactly when
        /// <see cref="Classification"/> is
        /// <see cref="DeliveryClassification.Message"/>.
        /// </summary>
        public DeliveryDescriptor? Delivery { get; }

        /// <summary>
        /// The same text, carrying a delivery classification. Used by the
        /// parser once it has read an envelope.
        /// </summary>
        public LexiconEntry WithDelivery(DeliveryClassification classification, DeliveryDescriptor? delivery)
            => new LexiconEntry(_plain, _critical, _terse, _chatty, _diagnostic, classification, delivery);

        /// <summary>
        /// THIS entry's delivery contract, wearing <paramref name="text"/>'s
        /// words.
        /// </summary>
        /// <remarks>
        /// The overlay merge in one method. An operator who rewrites a
        /// sentence keeps the shipped classification — its shelf life, its
        /// validity contract, its history key and its receipt policy — because
        /// those are not words and were never his to edit. Replacing the whole
        /// entry, which is what the merge did before the envelope existed,
        /// would silently strip a safety message's lifecycle policy and leave
        /// something that still speaks.
        /// </remarks>
        public LexiconEntry WithTextFrom(LexiconEntry text)
            => new LexiconEntry(text._plain, text._critical, text._terse, text._chatty, text._diagnostic,
                                Classification, Delivery);

        /// <summary>True when this entry varies by verbosity.</summary>
        public bool IsLadder => _plain == null;

        /// <summary>The tiers this ladder actually defines, for the hole check.</summary>
        public IReadOnlyList<string> DefinedTiers
        {
            get
            {
                var tiers = new List<string>(4);
                if (_critical != null) tiers.Add("critical");
                if (_terse != null) tiers.Add("terse");
                if (_chatty != null) tiers.Add("chatty");
                if (_diagnostic != null) tiers.Add("diagnostic");
                return tiers;
            }
        }

        /// <summary>
        /// The text for a tier. A ladder with a hole falls DOWNWARD to the
        /// next-terser tier that exists, because saying less than asked is
        /// always safe and saying nothing never is. A test enforces that
        /// shipped ladders have no holes; this is what happens anyway.
        /// </summary>
        public string? Resolve(VerbosityLevel level)
        {
            if (_plain != null) return _plain;

            return level switch
            {
                VerbosityLevel.Diagnostic => _diagnostic ?? _chatty ?? _terse ?? _critical,
                VerbosityLevel.Chatty => _chatty ?? _terse ?? _critical,
                VerbosityLevel.Terse => _terse ?? _critical ?? _chatty,
                _ => _critical ?? _terse ?? _chatty,
            };
        }
    }

    /// <summary>Something wrong with one partition, named so it can be fixed.</summary>
    public sealed class LexiconProblem
    {
        public LexiconProblem(string partition, string path, string message)
        {
            Partition = partition;
            Path = path;
            Message = message;
        }

        public string Partition { get; }
        public string Path { get; }
        public string Message { get; }

        public override string ToString()
        {
            return Partition + " (" + Path + "): " + Message;
        }
    }

    /// <summary>What a load found.</summary>
    public sealed class LexiconLoadReport
    {
        internal static readonly LexiconLoadReport Empty =
            new LexiconLoadReport(0, Array.Empty<string>(), Array.Empty<LexiconProblem>(), 0);

        public LexiconLoadReport(int keyCount, IReadOnlyList<string> partitions,
                                 IReadOnlyList<LexiconProblem> problems, int overlaysApplied)
        {
            KeyCount = keyCount;
            Partitions = partitions;
            Problems = problems;
            OverlaysApplied = overlaysApplied;
        }

        public int KeyCount { get; }
        public IReadOnlyList<string> Partitions { get; }
        public IReadOnlyList<LexiconProblem> Problems { get; }
        public int OverlaysApplied { get; }

        /// <summary>True when every partition asked for loaded without complaint.</summary>
        public bool IsClean => Problems.Count == 0;
    }

    /// <summary>
    /// A shipped baseline is broken. Deliberately fatal: it is a build defect,
    /// a test catches it, and it must never reach an operator.
    /// </summary>
    public sealed class LexiconException : Exception
    {
        public LexiconException() { }
        public LexiconException(string message) : base(message) { }
        public LexiconException(string message, Exception inner) : base(message, inner) { }
    }
}
