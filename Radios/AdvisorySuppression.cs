#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// One message an operator is allowed to silence: the key that identifies
    /// it in the store, and the words that name it back to him afterwards.
    /// </summary>
    /// <remarks>
    /// <para><b>Both halves, always.</b> The constructor refuses a key without
    /// a label because the Settings list has no other way to describe what was
    /// silenced. Before Sprint 36 the keys were bare strings passed inline at
    /// the call site, so the only honest answer that surface could have given
    /// was <c>no-physical-access-cascade-v1</c> — a wall of identifiers read
    /// aloud, which is no answer at all.</para>
    /// <para><b>The label is never persisted.</b> See
    /// <see cref="AdvisorySuppressionStore"/>: the store keeps keys and dates,
    /// and asks <see cref="AdvisoryKeys.Describe"/> for the words each time it
    /// is read. A label written into the file would be a copy of prose living
    /// in code, and this project's most common defect by a wide margin is a
    /// description that no longer matches the thing it describes.</para>
    /// </remarks>
    public sealed class AdvisoryKey
    {
        public AdvisoryKey(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("An advisory key needs a stable identifier.", nameof(value));
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException(
                    "An advisory key needs a label — the operator has to be told what he silenced.",
                    nameof(label));
            Value = value;
            Label = label;
        }

        /// <summary>Stable, persisted, never spoken.</summary>
        public string Value { get; }

        /// <summary>What this message is called in the Settings list. Spoken.</summary>
        public string Label { get; }

        public override string ToString() => Value;
    }

    /// <summary>One silenced message, as the Settings list needs to read it.</summary>
    public sealed class SuppressedAdvisory
    {
        public SuppressedAdvisory(string key, string label, DateTimeOffset? silenced)
        {
            Key = key;
            Label = label;
            Silenced = silenced;
        }

        /// <summary>The stored key. Kept so it can be restored; not for reading out.</summary>
        public string Key { get; }

        /// <summary>What the message is, in words. Resolved fresh on every read.</summary>
        public string Label { get; }

        /// <summary>
        /// When it was silenced, or null for an entry written before this store
        /// recorded dates. Null is reported as such rather than guessed at.
        /// </summary>
        public DateTimeOffset? Silenced { get; }

        /// <summary>
        /// The whole item as one sentence pair, for a list a screen reader
        /// reads one arrow press at a time: what it is, then when it went
        /// quiet. Same reasoning as the problems list — a split design makes
        /// arrowing say only that something exists.
        /// </summary>
        public string Sentence()
        {
            string when = Silenced.HasValue
                ? Lexicon.Get("settings.silenced.item_when",
                    ("date", Silenced.Value.ToLocalTime().ToString("MMMM d, yyyy", CultureInfo.CurrentCulture)))
                : Lexicon.Get("settings.silenced.item_when_unknown");
            return Lexicon.Get("settings.silenced.item", ("label", Label), ("when", when));
        }
    }

    /// <summary>
    /// The "don't show this again" choices an operator has made, and — since
    /// Sprint 36 — the way back out of them.
    /// </summary>
    /// <remarks>
    /// <para><b>Task #267.</b> Until now this store could only ever grow.
    /// It offered <c>IsSuppressed</c> and <c>Suppress</c> and nothing else: no
    /// unsuppress, no clear, and no way to ask what had been silenced. One tick
    /// of a checkbox removed a message for the life of the install, and Settings
    /// could not even LIST the damage, because the store had no method to ask.
    /// That door was already open on three surfaces, one of them a CONFIRMATION
    /// dialog — so a destructive action could be made to stop asking, for good,
    /// by one keypress with no way back.</para>
    ///
    /// <para><b>An instance over a path, with the statics on top.</b> The app
    /// uses <see cref="AdvisorySuppression"/>, which is a facade over one
    /// default instance in the settings folder. Tests construct their own
    /// against a temp file and never touch process-wide state — no collection
    /// attribute, no save-and-restore dance, no chance of reading the
    /// operator's live folder. The old static-readonly file path was evaluated
    /// at type load, which is precisely the shape
    /// <see cref="RadioConfig.AppDataRoot"/>'s own remarks warn about.</para>
    ///
    /// <para><b>Stored as a plain JSON file</b> so it stays auditable and
    /// hand-fixable — deleting the file, or one entry from it, brings the
    /// advisories back, and support can ask "what is in
    /// suppressed-advisories.json" rather than spelunking the registry.</para>
    /// </remarks>
    public sealed class AdvisorySuppressionStore
    {
        private const int CurrentFormat = 2;

        private readonly string _filePath;
        private readonly Func<string, string> _describe;
        private readonly object _lock = new();

        /// <summary>Key to the moment it was silenced; null where unrecorded.</summary>
        private Dictionary<string, DateTimeOffset?>? _entries;

        /// <param name="filePath">The JSON file this store reads and writes.</param>
        /// <param name="describe">
        /// Turns a stored key into words. Defaults to
        /// <see cref="AdvisoryKeys.Describe"/>. Injected rather than fixed so a
        /// test can assert on labels without standing up the lexicon, and so
        /// the store never has to hold prose of its own.
        /// </param>
        public AdvisorySuppressionStore(string filePath, Func<string, string>? describe = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A suppression store needs a file to live in.", nameof(filePath));
            _filePath = filePath;
            _describe = describe ?? AdvisoryKeys.Describe;
        }

        /// <summary>Where this store keeps its file.</summary>
        public string FilePath => _filePath;

        public bool IsSuppressed(AdvisoryKey key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            return IsSuppressed(key.Value);
        }

        public bool IsSuppressed(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (_lock)
            {
                LoadIfNeeded();
                return _entries!.ContainsKey(key);
            }
        }

        /// <summary>
        /// Silence this message. Taking an <see cref="AdvisoryKey"/> rather
        /// than a string is the point: nothing can enter the store without a
        /// label, so the Settings list can always name what it holds.
        /// </summary>
        public void Suppress(AdvisoryKey key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            lock (_lock)
            {
                LoadIfNeeded();
                if (_entries!.ContainsKey(key.Value)) return;
                _entries[key.Value] = DateTimeOffset.UtcNow;
                Save();
                Tracing.TraceLine("AdvisorySuppression: silenced " + key.Value,
                    System.Diagnostics.TraceLevel.Info);
            }
        }

        /// <summary>
        /// Bring one message back. An unknown key is not an error — it is what
        /// a stale list, a second window, or a hand-edited file looks like, and
        /// the operator's intent ("show this again") is already satisfied.
        /// </summary>
        /// <returns>True when something was actually restored.</returns>
        public bool Unsuppress(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (_lock)
            {
                LoadIfNeeded();
                if (!_entries!.Remove(key)) return false;
                Save();
                Tracing.TraceLine("AdvisorySuppression: restored " + key,
                    System.Diagnostics.TraceLevel.Info);
                return true;
            }
        }

        /// <summary>Bring every silenced message back.</summary>
        /// <returns>How many were restored, so the caller can say so out loud.</returns>
        public int Clear()
        {
            lock (_lock)
            {
                LoadIfNeeded();
                int count = _entries!.Count;
                if (count == 0) return 0;
                _entries.Clear();
                Save();
                Tracing.TraceLine(
                    "AdvisorySuppression: restored all " + count.ToString(CultureInfo.InvariantCulture) + " silenced messages",
                    System.Diagnostics.TraceLevel.Info);
                return count;
            }
        }

        /// <summary>How many messages are silenced right now.</summary>
        public int Count
        {
            get { lock (_lock) { LoadIfNeeded(); return _entries!.Count; } }
        }

        /// <summary>
        /// Everything currently silenced, newest first — because the message
        /// somebody silenced by accident a minute ago is the one they came here
        /// to find. Entries with no recorded date sort last; they are the
        /// oldest by definition.
        /// </summary>
        public IReadOnlyList<SuppressedAdvisory> Snapshot()
        {
            lock (_lock)
            {
                LoadIfNeeded();
                return _entries!
                    .OrderByDescending(e => e.Value ?? DateTimeOffset.MinValue)
                    .ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new SuppressedAdvisory(e.Key, Describe(e.Key), e.Value))
                    .ToList();
            }
        }

        /// <summary>Drop the in-memory copy; the next read comes off disk.</summary>
        public void Forget()
        {
            lock (_lock) { _entries = null; }
        }

        private string Describe(string key)
        {
            try
            {
                string label = _describe(key);
                return string.IsNullOrWhiteSpace(label) ? key : label;
            }
            catch (Exception ex)
            {
                // A describer that throws must not make the recovery surface
                // unreachable — an unreadable list is still better than none.
                Tracing.TraceLine("AdvisorySuppression: could not describe " + key + ": " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
                return key;
            }
        }

        private void LoadIfNeeded()
        {
            if (_entries != null) return;
            _entries = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);

            try
            {
                if (!File.Exists(_filePath)) return;
                string text = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(text)) return;

                using var doc = JsonDocument.Parse(text);
                JsonElement root = doc.RootElement;

                // Format 1 was a bare array of key strings with no dates. It
                // shipped, so it is read rather than discarded: throwing an
                // operator's choices away on upgrade would restore advisories
                // he silenced on purpose, which is the same disrespect the
                // blanket reset commits.
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in root.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            AddKey(item.GetString(), null);
                    return;
                }

                if (root.ValueKind != JsonValueKind.Object) return;
                if (!root.TryGetProperty("silenced", out JsonElement list)) return;
                if (list.ValueKind != JsonValueKind.Array) return;

                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string? key = item.TryGetProperty("key", out JsonElement k) && k.ValueKind == JsonValueKind.String
                        ? k.GetString()
                        : null;
                    DateTimeOffset? when = null;
                    if (item.TryGetProperty("when", out JsonElement w) &&
                        w.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(w.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
                    {
                        when = parsed;
                    }
                    AddKey(key, when);
                }
            }
            catch (Exception ex)
            {
                // A damaged file means the advisories come back, which is the
                // safe direction to fail in. Say so; do not take the app down.
                Tracing.TraceLine("AdvisorySuppression: load failed: " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
                _entries = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
            }
        }

        private void AddKey(string? key, DateTimeOffset? when)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            _entries![key] = when;
        }

        private void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var payload = new
                {
                    version = CurrentFormat,
                    silenced = _entries!
                        .OrderBy(e => e.Key, StringComparer.Ordinal)
                        .Select(e => new
                        {
                            key = e.Key,
                            when = e.Value?.ToString("o", CultureInfo.InvariantCulture),
                        })
                        .ToArray(),
                };

                File.WriteAllText(_filePath, JsonSerializer.Serialize(
                    payload, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                // Losing a suppression preference is annoying, not dangerous —
                // worst case the advisory shows again next run. Losing an
                // UNsuppression is the same shape in reverse and no worse.
                Tracing.TraceLine("AdvisorySuppression: save failed: " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
            }
        }
    }

    /// <summary>
    /// The application's one suppression store, in the settings folder.
    /// </summary>
    /// <remarks>
    /// A facade rather than the implementation, so that everything the store
    /// does is reachable by a test that owns its own file. See
    /// <see cref="AdvisorySuppressionStore"/> for the whole story.
    /// </remarks>
    public static class AdvisorySuppression
    {
        private static readonly object _gate = new();
        private static AdvisorySuppressionStore? _default;

        /// <summary>
        /// The store in <c>%AppData%\JJFlexRadio</c> — or wherever
        /// <see cref="RadioConfig.AppDataRoot"/> points this run.
        /// </summary>
        /// <remarks>
        /// Resolved on first use rather than at type load. A
        /// <c>static readonly</c> path here would bind before startup had
        /// settled the settings root, which is exactly how a store ends up
        /// writing the operator's live folder during a run that believes it is
        /// isolated.
        /// </remarks>
        public static AdvisorySuppressionStore Default
        {
            get
            {
                lock (_gate)
                {
                    return _default ??= new AdvisorySuppressionStore(
                        Path.Combine(RadioConfig.AppDataRoot, "suppressed-advisories.json"));
                }
            }
        }

        public static bool IsSuppressed(AdvisoryKey key) => Default.IsSuppressed(key);

        public static bool IsSuppressed(string key) => Default.IsSuppressed(key);

        public static void Suppress(AdvisoryKey key) => Default.Suppress(key);

        public static bool Unsuppress(string key) => Default.Unsuppress(key);

        public static int Clear() => Default.Clear();

        public static int Count => Default.Count;

        public static IReadOnlyList<SuppressedAdvisory> Snapshot() => Default.Snapshot();
    }
}
