using System;
using System.Collections.Generic;
using System.Linq;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// One entry for the Settings Radios-tab picker: which radio, what its
    /// combo row says, and whether it is the live connection.
    /// </summary>
    public sealed class RadioPickerEntry
    {
        public string Id { get; init; } = "";

        /// <summary>The row's text — what a screen reader announces for the
        /// item and what a sighted operator sees. Built by the naming ladder
        /// in <see cref="RadioProfilePickerModel.ComposeLabel"/>.</summary>
        public string Label { get; init; } = "";

        public bool IsConnected { get; init; }
    }

    /// <summary>
    /// Builds the Settings radio picker's entries: which radios appear, what
    /// each row is called, and what order they come in. Non-UI on purpose, so
    /// the whole surface is testable without constructing a dialog.
    ///
    /// <para><b>What appears (and what deliberately does not).</b> Radios that
    /// have a saved per-radio profile (<c>radios\{serial}\config.xml</c>),
    /// plus the connected radio even before its first save. A radio that has
    /// been SEEN but never configured does not appear — the editable combo
    /// accepts a typed serial for that case, and widening the list would
    /// change what the tab means. A profile flagged
    /// <see cref="RadioConfig.HiddenFromList"/> DOES appear here: that flag
    /// takes a radio off the CONNECT roster while keeping its settings (task
    /// #98), and this tab is where those surviving settings live — hiding it
    /// here would make them uneditable with no door anywhere.</para>
    ///
    /// <para><b>The naming ladder (#391's Settings half).</b> Until 2026-08-30
    /// every row without a display name was a bare sixteen-digit serial, and
    /// the operator was left disambiguating "1315-4176-6300-7236" from
    /// "2116-5319-6300-6334" by ear, mid-list, right before doing something
    /// consequential to somebody else's station. Every row now leads with
    /// something a person recognises — name, else model — and the serial rides
    /// along in parentheses where it is available without being the identity.
    /// A bare serial remains only when genuinely nothing else is known, which
    /// the serial's own model group makes rare.</para>
    /// </summary>
    public static class RadioProfilePickerModel
    {
        /// <summary>
        /// Model groups this project has actually held or documented — the
        /// third serial group of every Flex we have seen is the model number.
        /// DELIBERATELY not a guess for anything else: an Aurora's model reads
        /// "AU-510" and we have never seen what an Aurora puts in group three
        /// (see the shape-rule notes in <see cref="RadioConfig"/>), so an
        /// unrecognised group yields no model rather than a fabricated one.
        /// </summary>
        private static readonly HashSet<string> KnownModelGroups = new(StringComparer.Ordinal)
        {
            "6300", "6400", "6500", "6600", "6700", "8400", "8600",
        };

        /// <summary>
        /// The picker's entries, in the order a person looks for a radio: the
        /// connected one first, then favorites, then most recently seen,
        /// never-seen last, ties alphabetical. Serial order — the old order —
        /// was arbitrary: it put a never-seen test fixture ahead of the radio
        /// the operator uses every day.
        /// </summary>
        /// <param name="configDirectory">The app config root
        /// (<see cref="RadioConfig.BaseDirectory"/>).</param>
        /// <param name="connectedSerial">Serial of the live connection, or
        /// null/empty when nothing is connected.</param>
        /// <param name="connectedNickname">The connected radio's live
        /// broadcast name, for the rare row that has no saved profile yet.</param>
        /// <param name="connectedModel">The connected radio's live model
        /// string, same purpose.</param>
        public static List<RadioPickerEntry> Build(
            string? configDirectory,
            string? connectedSerial,
            string connectedNickname = "",
            string connectedModel = "")
        {
            var ids = string.IsNullOrEmpty(configDirectory)
                ? new List<string>()
                : RadioConfig.ListKnownRadioIds(configDirectory!);

            // Merged display metadata: the per-radio profile is authoritative;
            // the connection cache fills blanks for radios whose profile was
            // written before the roster fields existed. Hidden entries are
            // included on purpose — see the type remarks. A roster failure
            // costs only the cache fill, never the list.
            var merged = new Dictionary<string, KnownRadioEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var e in KnownRadioRoster.Load(includeHidden: true))
                {
                    if (!string.IsNullOrWhiteSpace(e.Serial) && !merged.ContainsKey(e.Serial))
                        merged[e.Serial] = e;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"RadioProfilePickerModel.Build: roster merge unavailable: {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
            }

            var working = new List<(string Id, string Label, bool Connected, bool Favorite, DateTime LastSeen)>();
            foreach (var id in ids)
            {
                var cfg = RadioConfig.Load(configDirectory!, id);
                merged.TryGetValue(id, out var fill);
                string name = FirstNonBlank(cfg.DisplayName, fill?.DisplayName);
                string model = FirstNonBlank(cfg.Model, fill?.Model, ModelFromSerial(id));
                bool connected = SameId(id, connectedSerial);
                string label = ComposeLabel(id, name, model);
                if (connected)
                    label = Lexicon.Get("settings.profile.picker_connected", ("connected", label));
                var lastSeen = cfg.LastSeenUtc;
                if (fill != null && fill.LastSeenUtc > lastSeen) lastSeen = fill.LastSeenUtc;
                working.Add((id, label, connected, cfg.IsFavorite, lastSeen));
            }

            // The connected radio belongs in the list even before its first
            // saved profile — its live name and model make the row readable.
            if (!string.IsNullOrEmpty(connectedSerial)
                && !working.Any(w => SameId(w.Id, connectedSerial)))
            {
                merged.TryGetValue(connectedSerial!, out var fill);
                string name = FirstNonBlank(connectedNickname, fill?.DisplayName);
                string model = FirstNonBlank(connectedModel, fill?.Model, ModelFromSerial(connectedSerial));
                string label = Lexicon.Get("settings.profile.picker_connected",
                    ("connected", ComposeLabel(connectedSerial!, name, model)));
                working.Add((connectedSerial!, label, true, false, DateTime.UtcNow));
            }

            return working
                .OrderByDescending(w => w.Connected)
                .ThenByDescending(w => w.Favorite)
                .ThenByDescending(w => w.LastSeen)
                .ThenBy(w => w.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(w => w.Id, StringComparer.OrdinalIgnoreCase)
                .Select(w => new RadioPickerEntry
                {
                    Id = w.Id,
                    Label = w.Label,
                    IsConnected = w.Connected,
                })
                .ToList();
        }

        /// <summary>
        /// The naming ladder for one profile, used wherever a commit note or
        /// status line names a radio. Same rungs as the picker rows, minus the
        /// connected marker — a receipt should not say "connected".
        /// </summary>
        public static string LabelFor(RadioConfig cfg)
        {
            string id = cfg.RadioId ?? "";
            return ComposeLabel(id, cfg.DisplayName,
                FirstNonBlank(cfg.Model, ModelFromSerial(id)));
        }

        /// <summary>
        /// The short spoken identity that LEADS every per-radio announcement:
        /// "K5NER, FLEX-8600", or "FLEX-8600 ending 0002" when unnamed. No
        /// full serial — this is the part a person recognises at arrow-key
        /// cadence, and sixteen digits at every press is the noise the ladder
        /// exists to remove. The full serial stays available in the row text.
        /// </summary>
        public static string SpokenNameFor(RadioConfig cfg)
        {
            string id = cfg.RadioId ?? "";
            return ComposeSpokenName(id, cfg.DisplayName,
                FirstNonBlank(cfg.Model, ModelFromSerial(id)));
        }

        /// <summary>The ladder itself: name and model, name, model, bare id —
        /// in that order, and a bare id only when nothing else is known.</summary>
        internal static string ComposeLabel(string id, string name, string model)
        {
            bool hasName = !string.IsNullOrWhiteSpace(name);
            bool hasModel = !string.IsNullOrWhiteSpace(model);
            if (hasName && hasModel)
                return Lexicon.Get("settings.profile.picker_named_model",
                    ("displayName", name.Trim()), ("model", model.Trim()), ("id", id));
            if (hasName)
                return Lexicon.Get("settings.profile.picker_named",
                    ("displayName", name.Trim()), ("id", id));
            if (hasModel)
                return Lexicon.Get("settings.profile.picker_model",
                    ("model", model.Trim()), ("id", id));
            return id;
        }

        /// <summary>Spoken rungs of the same ladder. Unnamed radios speak the
        /// model plus the serial's last group — enough to tell two unnamed
        /// radios of one model apart without reciting sixteen digits.</summary>
        internal static string ComposeSpokenName(string id, string name, string model)
        {
            bool hasName = !string.IsNullOrWhiteSpace(name);
            bool hasModel = !string.IsNullOrWhiteSpace(model);
            if (hasName && hasModel)
                return Lexicon.Get("settings.profile.spoken_name_model",
                    ("displayName", name.Trim()), ("model", model.Trim()));
            if (hasName) return name.Trim();
            if (hasModel)
                return Lexicon.Get("settings.profile.spoken_model_ending",
                    ("model", model.Trim()), ("tail", TailOf(id)));
            return id;
        }

        /// <summary>
        /// The model a serial carries in its own third group ("FLEX-6300" from
        /// 1315-4176-<b>6300</b>-7236), or empty when the group is not one we
        /// know. Every Flex serial this project has held follows the
        /// convention; refusing to guess for unknown groups is what keeps an
        /// Aurora — whose group three we have never seen — from being labelled
        /// with a model it does not have.
        /// </summary>
        public static string ModelFromSerial(string? radioId)
        {
            if (!RadioConfig.IsWellFormedRadioId(radioId)) return "";
            string group = radioId!.Trim().Substring(10, 4);
            return KnownModelGroups.Contains(group) ? "FLEX-" + group : "";
        }

        private static string TailOf(string id)
        {
            int dash = id.LastIndexOf('-');
            return dash >= 0 && dash < id.Length - 1 ? id.Substring(dash + 1) : id;
        }

        private static bool SameId(string a, string? b) =>
            !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string FirstNonBlank(params string?[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
            }
            return "";
        }
    }
}
