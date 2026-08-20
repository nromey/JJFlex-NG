using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace JJFlexWpf
{
    /// <summary>
    /// Marks a public <see cref="EarconPlayer"/> method as a playable earcon,
    /// giving it an operator-facing name, a family, and a sentence saying when
    /// it fires. <see cref="EarconCatalog"/> finds these by reflection.
    ///
    /// Sprint 32 Track E, #113. The Earcon Explorer used to be a hand-written
    /// list of buttons in a dialog, and it reached 18 of the 45 public methods.
    /// The gap was not neglect — it was the shape of the thing. Adding a sound
    /// meant remembering to go and edit a dialog in a different file, and the
    /// sounds that got forgotten were exactly the ones added in a hurry for
    /// some other reason. ConnectSuccessTone, the most recognisable sound in
    /// the whole application, could not be played on demand anywhere.
    ///
    /// So the fact lives on the method now. Writing a new earcon and naming it
    /// are one edit in one place, and no dialog anywhere has to be told.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class EarconAttribute : Attribute
    {
        /// <summary>Operator-facing name. Sentence case, no "tone" or "beep"
        /// suffix unless the word is doing real work — the explorer already
        /// says "Play".</summary>
        public string Label { get; }

        /// <summary>
        /// The family whose on/off switch governs this sound, or null for the
        /// handful deliberately outside the six switches: calibration and
        /// bench sounds, which answer to the master earcon gate only. The
        /// EarconCategory documentation names that exception; this mirrors it
        /// rather than inventing a seventh category for it.
        /// </summary>
        public EarconPlayer.EarconCategory? Category { get; }

        /// <summary>One sentence: when does an operator hear this?</summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// For continuous earcons, the name of the method that stops them.
        /// A sound that loops until told otherwise cannot be auditioned with a
        /// fire-and-forget Play button — the bench has to offer Start and Stop,
        /// and it can only do that if the pairing is written down.
        /// </summary>
        public string? StopMethod { get; set; }

        /// <summary>Optional name of a static bool property reporting whether a
        /// continuous earcon is currently running.</summary>
        public string? RunningProperty { get; set; }

        /// <summary>Sort position inside the family. Ties break alphabetically.
        /// Reflection does not promise declaration order, and a counting series
        /// listed as 1, 3, 2 is worse than useless.</summary>
        public int Order { get; set; }

        /// <summary>Declare an earcon belonging to one of the six families.</summary>
        public EarconAttribute(string label, EarconPlayer.EarconCategory category)
        {
            Label = label;
            Category = category;
        }

        /// <summary>Declare an earcon that sits outside the family switches.</summary>
        public EarconAttribute(string label)
        {
            Label = label;
            Category = null;
        }
    }

    /// <summary>
    /// One playable sound, as the bench sees it.
    /// </summary>
    public sealed class EarconEntry
    {
        /// <summary>Stable identifier — the method name for reflected entries,
        /// or method name plus a suffix for a parameterised variant.</summary>
        public string Id { get; init; } = "";

        /// <summary>Operator-facing name.</summary>
        public string Label { get; init; } = "";

        /// <summary>One sentence: when does an operator hear this?</summary>
        public string Description { get; init; } = "";

        /// <summary>The family switch governing it, or null if outside them.</summary>
        public EarconPlayer.EarconCategory? Category { get; init; }

        /// <summary>Sort position inside its family.</summary>
        public int Order { get; init; }

        /// <summary>Play it, or start it if it is continuous.</summary>
        public Action Play { get; init; } = () => { };

        /// <summary>Stop it. Null for everything that ends on its own.</summary>
        public Action? Stop { get; init; }

        /// <summary>Whether it is currently running, when that is knowable.</summary>
        public Func<bool>? IsRunning { get; init; }

        /// <summary>True when this sound loops until stopped.</summary>
        public bool IsContinuous => Stop != null;

        /// <summary>
        /// The family name as an operator reads it, or the out-of-family label.
        /// </summary>
        public string CategoryLabel => EarconCatalog.CategoryLabel(Category);
    }

    /// <summary>
    /// Every earcon the application can play, discovered from the methods
    /// themselves rather than transcribed into a dialog.
    ///
    /// Sections mirror the six <see cref="EarconPlayer.EarconCategory"/> values
    /// exactly, so the explorer and the Settings on/off switches speak one
    /// vocabulary. They did not before: the explorer's first heading was "Meter
    /// Tones" over a group of alert beeps that are not meter tones and are not
    /// governed by the meter switch, so an operator who turned off what the
    /// heading named would have found the sounds still playing.
    /// </summary>
    public static class EarconCatalog
    {
        private static IReadOnlyList<EarconEntry>? _all;
        private static IReadOnlyList<string>? _unregistered;

        /// <summary>
        /// Public no-argument methods on <see cref="EarconPlayer"/> that are not
        /// earcons and are not expected to carry the attribute. Lifecycle and
        /// bulk-teardown calls; the Stop half of every continuous earcon is
        /// excluded automatically, by being named in its Start's attribute.
        /// </summary>
        private static readonly HashSet<string> NotSounds = new(StringComparer.Ordinal)
        {
            nameof(EarconPlayer.Initialize),
            nameof(EarconPlayer.Dispose),
            nameof(EarconPlayer.UnregisterAllContinuousTones),
            // The Stop half of the transmit test-tone monitor. Its Start takes
            // a frequency, so the pair is registered as a variant below rather
            // than by an attribute, and the automatic exclusion cannot see it.
            nameof(EarconPlayer.StopTxToneMonitor),
        };

        /// <summary>Every playable earcon, families first in enum order, then
        /// the handful that sit outside the family switches.</summary>
        public static IReadOnlyList<EarconEntry> All => _all ??= Build();

        /// <summary>The entries in one family, in their declared order.</summary>
        public static IReadOnlyList<EarconEntry> InCategory(EarconPlayer.EarconCategory category) =>
            All.Where(e => e.Category == category).ToList();

        /// <summary>The entries that sit outside the six family switches.</summary>
        public static IReadOnlyList<EarconEntry> Uncategorised =>
            All.Where(e => e.Category == null).ToList();

        /// <summary>The six families, in enum order.</summary>
        public static IReadOnlyList<EarconPlayer.EarconCategory> Categories { get; } =
            Enum.GetValues<EarconPlayer.EarconCategory>().ToList();

        /// <summary>Find one entry by its stable id.</summary>
        public static EarconEntry? Find(string id) =>
            All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));

        /// <summary>
        /// Public no-argument methods on EarconPlayer that look like sounds but
        /// carry no <see cref="EarconAttribute"/>. The audit that keeps this
        /// registry honest: if someone adds an earcon and forgets to name it,
        /// this is where it shows up, and the bench says so out loud instead of
        /// silently reaching one sound fewer than the app can make.
        /// </summary>
        public static IReadOnlyList<string> UnregisteredMethods
        {
            get { if (_unregistered == null) Build(); return _unregistered!; }
        }

        /// <summary>The family name as an operator reads it.</summary>
        public static string CategoryLabel(EarconPlayer.EarconCategory? category) => category switch
        {
            EarconPlayer.EarconCategory.Connection => "Connection",
            EarconPlayer.EarconCategory.Transmit => "Transmit",
            EarconPlayer.EarconCategory.DialogsAndPanels => "Dialogs and panels",
            EarconPlayer.EarconCategory.TuningAndFilters => "Tuning and filters",
            EarconPlayer.EarconCategory.CommandsAndConfirmations => "Commands and confirmations",
            EarconPlayer.EarconCategory.Warnings => "Warnings",
            _ => "Outside the family switches",
        };

        /// <summary>One line saying what a family covers, for the section header.</summary>
        public static string CategoryDescription(EarconPlayer.EarconCategory? category) => category switch
        {
            EarconPlayer.EarconCategory.Connection =>
                "Connect-phase counting tones and the success double-beep.",
            EarconPlayer.EarconCategory.Transmit =>
                "Transmit start and stop, the PTT warning family, tune carrier and ATU.",
            EarconPlayer.EarconCategory.DialogsAndPanels =>
                "Dialog open and close, and panel expand and collapse.",
            EarconPlayer.EarconCategory.TuningAndFilters =>
                "Filter edges and boundaries, band edges, and frequency-entry confirmations.",
            EarconPlayer.EarconCategory.CommandsAndConfirmations =>
                "JJ-layer tones, feature on and off, mute all, and confirmations.",
            EarconPlayer.EarconCategory.Warnings =>
                "Something is wrong. The one family worth thinking twice about switching off.",
            _ =>
                "Calibration and bench sounds. These answer to the master earcon switch only, "
                + "on purpose — they are not part of any family an operator would turn off.",
        };

        private static IReadOnlyList<EarconEntry> Build()
        {
            var entries = new List<EarconEntry>();
            var missing = new List<string>();
            var stopMethodNames = new HashSet<string>(StringComparer.Ordinal);

            var type = typeof(EarconPlayer);
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);

            // First pass: collect the Stop halves so the audit does not flag
            // them as forgotten sounds.
            foreach (var m in methods)
            {
                var attr = m.GetCustomAttribute<EarconAttribute>();
                if (attr?.StopMethod != null) stopMethodNames.Add(attr.StopMethod);
            }

            foreach (var m in methods)
            {
                if (m.GetParameters().Length != 0) continue;
                if (m.ReturnType != typeof(void)) continue;

                var attr = m.GetCustomAttribute<EarconAttribute>();
                if (attr == null)
                {
                    if (!NotSounds.Contains(m.Name) && !stopMethodNames.Contains(m.Name))
                        missing.Add(m.Name);
                    continue;
                }

                var play = (Action)Delegate.CreateDelegate(typeof(Action), m);

                Action? stop = null;
                if (!string.IsNullOrEmpty(attr.StopMethod))
                {
                    var stopMethod = type.GetMethod(attr.StopMethod,
                        BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
                    if (stopMethod != null)
                        stop = (Action)Delegate.CreateDelegate(typeof(Action), stopMethod);
                }

                Func<bool>? running = null;
                if (!string.IsNullOrEmpty(attr.RunningProperty))
                {
                    var prop = type.GetProperty(attr.RunningProperty,
                        BindingFlags.Public | BindingFlags.Static);
                    if (prop?.GetMethod != null)
                        running = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), prop.GetMethod);
                }

                entries.Add(new EarconEntry
                {
                    Id = m.Name,
                    Label = attr.Label,
                    Description = attr.Description,
                    Category = attr.Category,
                    Order = attr.Order,
                    Play = play,
                    Stop = stop,
                    IsRunning = running,
                });
            }

            entries.AddRange(Variants());

            // Families in enum order, out-of-family last; then declared order,
            // then alphabetical so an unordered family is at least stable.
            entries.Sort((a, b) =>
            {
                int ca = a.Category.HasValue ? (int)a.Category.Value : int.MaxValue;
                int cb = b.Category.HasValue ? (int)b.Category.Value : int.MaxValue;
                if (ca != cb) return ca.CompareTo(cb);
                if (a.Order != b.Order) return a.Order.CompareTo(b.Order);
                return string.Compare(a.Label, b.Label, StringComparison.CurrentCulture);
            });

            _unregistered = missing;
            _all = entries;
            return entries;
        }

        /// <summary>
        /// Earcons whose method takes arguments, so the attribute cannot say
        /// everything on its own. Each one is a real sound an operator hears —
        /// the panned filter edges genuinely differ by ear, and the plain beep
        /// and chirps are the raw primitives worth having on the bench as a
        /// reference against the voiced set. They are written out here rather
        /// than left unreachable.
        ///
        /// Deliberately absent: PlayScratchpadTone and PlayScratchpadChirp
        /// (bench instruments, not earcons — they ARE the scratchpad),
        /// PlayTypingSound (per-keystroke, with its own mode setting and its
        /// own preview), and PlayStreamAsWav (plays whatever it is handed).
        /// </summary>
        private static IEnumerable<EarconEntry> Variants()
        {
            var cat = EarconPlayer.EarconCategory.TuningAndFilters;

            yield return new EarconEntry
            {
                Id = "FilterEdgeMoveTone.Low",
                Label = "Filter edge move, low edge",
                Description = "Each step while dragging the low filter edge. Panned left.",
                Category = cat,
                Order = 31,
                Play = () => EarconPlayer.FilterEdgeMoveTone(true),
            };
            yield return new EarconEntry
            {
                Id = "FilterEdgeMoveTone.High",
                Label = "Filter edge move, high edge",
                Description = "Each step while dragging the high filter edge. Panned right.",
                Category = cat,
                Order = 32,
                Play = () => EarconPlayer.FilterEdgeMoveTone(false),
            };
            yield return new EarconEntry
            {
                Id = "FilterBoundaryHitTone.Low",
                Label = "Filter boundary hit, low edge",
                Description = "The low filter edge has run out of room. Panned left, descending.",
                Category = cat,
                Order = 41,
                Play = () => EarconPlayer.FilterBoundaryHitTone(true),
            };
            yield return new EarconEntry
            {
                Id = "FilterBoundaryHitTone.High",
                Label = "Filter boundary hit, high edge",
                Description = "The high filter edge has run out of room. Panned right, ascending.",
                Category = cat,
                Order = 42,
                Play = () => EarconPlayer.FilterBoundaryHitTone(false),
            };

            yield return new EarconEntry
            {
                Id = "Beep",
                Label = "Plain beep",
                Description = "The general-purpose tone, 800 Hz for 150 ms. Not a member of the "
                            + "PTT warning family, though it used to be identical to the first one.",
                Category = null,
                Order = 10,
                Play = () => EarconPlayer.Beep(),
            };
            yield return new EarconEntry
            {
                Id = "Chirp.Up",
                Label = "Rising chirp",
                Description = "The raw sweep primitive, 400 to 800 Hz over 200 ms.",
                Category = null,
                Order = 11,
                Play = () => EarconPlayer.Chirp(400, 800, 200),
            };
            yield return new EarconEntry
            {
                Id = "Chirp.Down",
                Label = "Falling chirp",
                Description = "The raw sweep primitive, 800 to 400 Hz over 200 ms.",
                Category = null,
                Order = 12,
                Play = () => EarconPlayer.Chirp(800, 400, 200),
            };
            yield return new EarconEntry
            {
                Id = "TxToneMonitor",
                Label = "Transmit test-tone monitor",
                Description = "The local monitor for the transmit test tone, 700 Hz, continuous. "
                            + "Local only — it does not key the radio.",
                Category = null,
                Order = 20,
                Play = () => EarconPlayer.StartTxToneMonitor(700f),
                Stop = EarconPlayer.StopTxToneMonitor,
            };
        }
    }
}
