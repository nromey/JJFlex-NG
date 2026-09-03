using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace JJFlexWpf;

/// <summary>
/// QB Track H (2026-08-07) — the canonical inventory of every key that is NOT
/// a rebindable KeyCommands registry binding: home-field character keys,
/// universal Home keys, filter chords, leader-key commands, PTT keys,
/// navigation keys, and system-reserved chords.
///
/// This is DATA, deliberately in one place, because it drives six surfaces
/// that used to drift apart:
///   1. DisplayField.HelpItems (the per-field F1-style help dialog)
///   2. The '?' speak-keys-here handler on home fields
///   3. The Keys dialog's read-only "Home and field keys" view
///   4. Command Finder informational rows (formerly hand-built in
///      ApplicationEvents.vb)
///   5. The generated key manifest (KeyManifest) reconciled against
///      docs/help/md/keyboard-reference.md
///   6. Ctrl+F1 context help on the Home fields (#184) — FrequencyContextRows
///      and SpeakTextFor, composed live by MainWindow's help provider
///
/// If a field handler in FreqOutHandlers gains or loses a key, update the
/// tables here — the six surfaces above follow automatically.
/// </summary>
public static class KeyInventory
{
    /// <summary>
    /// One non-registry key behavior. Context is a machine key naming where
    /// the key applies; ContextLabel is the human name spoken/shown.
    /// </summary>
    public sealed class FixedKeyEntry
    {
        public string Context { get; init; } = "";
        public string ContextLabel { get; init; } = "";
        public string KeyDisplay { get; init; } = "";

        /// <summary>
        /// How this key is SPOKEN, when that cannot be the same string as how
        /// it is written. Empty means the two are the same, which is the case
        /// for all but a handful of rows.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why a second string exists at all (#303).</b> KeyDisplay is both
        /// shown and read aloud, and two rows carry a bare "?". A literal
        /// question mark may not be voiced AT ALL when a screen reader's
        /// punctuation level is set low — so the row that tells an operator
        /// which key to press is exactly the row that can lose the key.
        /// </para>
        /// <para>
        /// <b>The convention this serves, which outlives the two rows:</b>
        /// anywhere the app tells somebody to PRESS a punctuation key, name
        /// the KEYSTROKE, not the glyph. "Shift slash", never "?" and never
        /// "question mark". It is the better wording even at high punctuation,
        /// because it is what the hands do; "question mark" names a character
        /// and leaves the operator to work out how to produce it.
        /// </para>
        /// <para>
        /// <b>Why the display form cannot simply be replaced.</b> The leader
        /// row's KeyDisplay is PARSED — <see cref="Radios.LeaderChordParser"/>
        /// turns a form like "Ctrl+J, Shift+A through Shift+H" into the chords the
        /// dispatcher switches on,
        /// and the consistency test compares both directions against the
        /// switch. Rewriting the glyph out of it would silently un-advertise
        /// Oem2 and break the very check that caught the dead "?" (#183). So
        /// the display form stays machine-readable and this one is what an
        /// operator hears.
        /// </para>
        /// </remarks>
        public string KeySpoken { get; init; } = "";

        /// <summary>
        /// The form to put in front of a screen reader: <see cref="KeySpoken"/>
        /// where a row defines one, otherwise <see cref="KeyDisplay"/>.
        /// </summary>
        public string SpokenKey => KeySpoken.Length > 0 ? KeySpoken : KeyDisplay;

        public string Description { get; init; } = "";
        public string[] Keywords { get; init; } = Array.Empty<string>();
        public string Scope { get; init; } = "Radio";
        public string Group { get; init; } = "FieldKeys";

        /// <summary>
        /// Menu path for Command Finder door rows (see <see cref="FinderDoors"/>).
        /// Empty for ordinary key entries — keys are keys, not menu items.
        /// </summary>
        public string MenuText { get; init; } = "";

        /// <summary>
        /// Chords that fall inside this entry's written RANGE but do not belong
        /// to it, as exact KeyDisplay strings ("Ctrl+J, Shift+F"). Stated as
        /// data because a Description aside is invisible to every tool that
        /// expands ranges: on 2026-08-21 jjprobe expanded "Ctrl+J, Shift+A
        /// through Shift+H" into eight chords and pressed Ctrl+J, Shift+F
        /// believing it was a slice jump — "(Shift+F is reserved)" was right
        /// there in the Description, and no parser can be asked to read English
        /// prose. The aside stays in the Description for human ears; this field
        /// is the same fact for machines.
        /// </summary>
        public string[] ExcludedKeys { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The inventory Context this chord OPENS, when pressing it enters a
        /// layer of its own ("AudioLayer" on the Ctrl+J, V row). Empty for a
        /// chord that does one thing and returns.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Sprint 44 Track K (#158, #519). The JJ key tree explorer derives its
        /// structure from this table — a tier, then a chord, then the keys
        /// that only mean something after that chord — and it has to know
        /// which chords are doors. Two rows can say so without this field:
        /// volume mode's rows are written "Ctrl+J, V, H", so the chord prefix
        /// links them to "Ctrl+J, V" on its own (see
        /// <see cref="KeyTree"/>). Pan mode's rows are written "Left / Right",
        /// which no prefix can link to "Ctrl+J, Alt+P" — and the only other
        /// way to find the parent is to read "Enter pan mode" out of the
        /// Description, which is the prose-parsing this file already refused
        /// once (<see cref="ExcludedKeys"/>).
        /// </para>
        /// <para>
        /// So the link is data. A layer whose rows carry the full chord prefix
        /// needs nothing here; a layer whose rows are written as bare keys
        /// sets this on its opening chord. Either way the explorer follows.
        /// </para>
        /// </remarks>
        public string OpensLayer { get; init; } = "";

        public FixedKeyEntry() { }
        public FixedKeyEntry(string context, string contextLabel, string key,
            string description, string[] keywords, string scope = "Radio", string group = "FieldKeys")
        {
            Context = context;
            ContextLabel = contextLabel;
            KeyDisplay = key;
            Description = description;
            Keywords = keywords;
            Scope = scope;
            Group = group;
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Universal Home keys — work from ANY field in the JJ Flexible Home
    //  (TryHandleUniversalHomeKey + inline duplicates in the field handlers).
    // ────────────────────────────────────────────────────────────────
    private static readonly FixedKeyEntry[] UniversalHome =
    {
        new("Home", "Any Home field", "M", "Mute or unmute the active slice",
            new[] { "mute", "unmute", "audio", "slice" }),
        new("Home", "Any Home field", "V", "Cycle to the next slice",
            new[] { "slice", "cycle", "next", "vfo" }),
        new("Home", "Any Home field", "R", "Toggle RIT on or off",
            new[] { "rit", "toggle", "offset", "receive" }),
        new("Home", "Any Home field", "X", "Toggle XIT on or off",
            new[] { "xit", "toggle", "offset", "transmit" }),
        new("Home", "Any Home field", "Q", "Toggle squelch on or off",
            new[] { "squelch", "toggle" }),
        new("Home", "Any Home field", "=", "Transceive: receive and transmit on the current slice",
            new[] { "transceive", "equals", "rx", "tx", "slice" }),
    };

    // Per-field tables. Context keys match FrequencyDisplay.DisplayField.Key.
    // Freq differs between Classic and Modern tuning — two contexts.
    private static readonly FixedKeyEntry[] FieldKeys =
    {
        // ── Slice field ──
        new("Slice", "Slice field", "Space", "Next slice (wraps around)",
            new[] { "slice", "next", "cycle" }),
        new("Slice", "Slice field", "Up / Down", "Next or previous slice",
            new[] { "slice", "next", "previous", "cycle" }),
        // 0-9, not 0-7: the handler answers every digit, and a digit past
        // your last slice says "no such slice" instead of going quiet (#358).
        // No supported radio has more than 8 slices, so 8 and 9 only ever
        // answer back — but a key that answers is a key worth declaring.
        new("Slice", "Slice field", "0-9 or A-H", "Jump straight to that slice — a slice you don't have says so",
            new[] { "slice", "jump", "letter", "number", "digit" }),
        new("Slice", "Slice field", "T", "Make this slice the transmit slice",
            new[] { "transmit", "tx", "slice" }),
        new("Slice", "Slice field", "Period", "Create a new slice",
            new[] { "slice", "create", "new", "add" }),
        new("Slice", "Slice field", "Comma", "Release the current slice",
            new[] { "slice", "release", "remove", "close" }),
        new("Slice", "Slice field", "Page Up", "Pan hard right",
            new[] { "pan", "right", "stereo" }),
        new("Slice", "Slice field", "Home", "Pan center",
            new[] { "pan", "center", "stereo" }),
        new("Slice", "Slice field", "Page Down", "Pan hard left",
            new[] { "pan", "left", "stereo" }),

        // ── Slice operations field ──
        new("SliceOps", "Slice operations field", "Up / Down", "Adjust volume",
            new[] { "volume", "gain", "audio", "slice" }),
        new("SliceOps", "Slice operations field", "Page Up / Page Down", "Pan right or left",
            new[] { "pan", "stereo", "balance" }),
        new("SliceOps", "Slice operations field", "Space", "Toggle mute",
            new[] { "mute", "unmute", "toggle" }),
        new("SliceOps", "Slice operations field", "M", "Mute (explicit)",
            new[] { "mute", "silence" }),
        // No explicit-unmute row. Jim's "S — sound" leg was deleted on
        // Noel's ruling (#345): on an already-unmuted slice it did nothing
        // but announce, so it read as a dead key. Space toggles; the
        // asymmetry (fast mute, no explicit unmute) is deliberate.
        new("SliceOps", "Slice operations field", "A-H", "Jump straight to that slice",
            new[] { "slice", "jump", "letter", "active" }),
        new("SliceOps", "Slice operations field", "T", "Make this slice the transmit slice",
            new[] { "transmit", "tx", "slice" }),

        // ── Frequency field, Classic tuning ──
        new("Freq.Classic", "Frequency field (Classic tuning)", "Up / Down", "Tune by the digit under the cursor",
            new[] { "tune", "frequency", "cursor", "digit" }),
        new("Freq.Classic", "Frequency field (Classic tuning)", "U / D", "Tune up or down (same as Up and Down)",
            new[] { "tune", "frequency" }),
        new("Freq.Classic", "Frequency field (Classic tuning)", "Digits", "Type a frequency, then Enter to apply",
            new[] { "frequency", "enter", "type", "digits" }),
        new("Freq.Classic", "Frequency field (Classic tuning)", "K", "Round to the nearest kilohertz",
            new[] { "round", "kilohertz", "khz" }),
        new("Freq.Classic", "Frequency field (Classic tuning)", "Plus then digits", "Set a step multiplier for Up and Down",
            new[] { "step", "multiplier", "plus" }),
        // The key exists to answer, not to act: it speaks "Step entry uses
        // plus only". Declared (#358) because the operator who reaches for
        // Minus is exactly the one who needs to hear that sentence, and no
        // surface mentioned the key at all.
        new("Freq.Classic", "Frequency field (Classic tuning)", "Minus", "Reminds you that step entry uses Plus only",
            new[] { "step", "minus", "plus", "multiplier" }),
        new("Freq.Classic", "Frequency field (Classic tuning)", "F", "Speak the current frequency",
            new[] { "frequency", "speak", "read" }),
        // P carries split in BOTH tuning modes (#344) — the Jim-era S only
        // turned split on, and S means the step picker in Modern, so one
        // letter taught two lessons across a mode boundary.
        new("Freq.Classic", "Frequency field (Classic tuning)", "P", "Toggle split on or off",
            new[] { "split", "toggle", "on", "off" }),
        new("Freq.Classic", "Frequency field (Classic tuning)", "T", "Toggle showing the transmit frequency",
            new[] { "transmit", "frequency", "show", "tx" }),

        // ── Frequency field, Modern tuning ──
        //
        // One rule holds the four arrow rows together (#302): VERTICAL TUNES,
        // HORIZONTAL SIZES; PLAIN IS COARSE, MODIFIED IS FINE. Learning either
        // half teaches the other. The coarse SIZING pair carries Alt because
        // bare Left / Right is the HomeNav cursor row and belongs to the whole
        // Home surface in both tuning modes; the fine sizing pair keeps plain
        // Shift so it mirrors Shift+Up / Shift+Down exactly.
        //
        // Order matters here — these rows are read out in sequence by Ctrl+F1.
        // Tune first, then size, then the ways to type, hear and set.
        new("Freq.Modern", "Frequency field (Modern tuning)", "Up / Down", "Tune by your coarse step",
            new[] { "tune", "coarse", "step" }),
        new("Freq.Modern", "Frequency field (Modern tuning)", "Shift+Up / Shift+Down", "Tune by your fine step",
            new[] { "tune", "fine", "step" }),
        new("Freq.Modern", "Frequency field (Modern tuning)", "Alt+Left / Alt+Right", "Make your coarse step smaller or larger",
            new[] { "step", "coarse", "size", "smaller", "larger", "bigger", "change", "set",
                    "increment", "tuning", "alt" }),
        new("Freq.Modern", "Frequency field (Modern tuning)", "Shift+Left / Shift+Right", "Make your fine step smaller or larger",
            new[] { "step", "fine", "size", "smaller", "larger", "bigger", "change", "set",
                    "increment", "tuning", "shift" }),
        new("Freq.Modern", "Frequency field (Modern tuning)", "Digits", "Type a frequency, then Enter to apply",
            new[] { "frequency", "enter", "type", "digits" }),
        new("Freq.Modern", "Frequency field (Modern tuning)", "F", "Speak the current frequency",
            new[] { "frequency", "speak", "read" }),
        new("Freq.Modern", "Frequency field (Modern tuning)", "S", "Choose both step sizes from a list",
            new[] { "step", "steps", "size", "sizes", "coarse", "fine", "choose", "pick", "set",
                    "list", "picker", "tuning" }),
        new("Freq.Modern", "Frequency field (Modern tuning)", "Shift+S", "Speak the coarse and fine step sizes",
            new[] { "step", "speak", "coarse", "fine" }),
        // Same letter, same toggle, as Classic (#344) — split must not change
        // meaning across a mode boundary that is only about how you tune.
        new("Freq.Modern", "Frequency field (Modern tuning)", "P", "Toggle split on or off",
            new[] { "split", "toggle", "on", "off" }),

        // ── S Meter field ──
        new("SMeter", "S Meter field", "Space", "Speak the current S meter reading, or forward power while transmitting",
            new[] { "s meter", "signal", "speak", "read", "power", "watts" }),

        // ── Squelch field ──
        new("Squelch", "Squelch field", "Space, Up, Down, or Q", "Toggle squelch on or off",
            new[] { "squelch", "toggle" }),

        // ── Squelch Level field ──
        new("SquelchLevel", "Squelch Level field", "Up / Down", "Adjust the squelch level",
            new[] { "squelch", "level", "adjust" }),
        new("SquelchLevel", "Squelch Level field", "Q", "Toggle squelch on or off",
            new[] { "squelch", "toggle" }),

        // ── Split field ──
        new("Split", "Split field", "Space, Up, or Down", "Toggle split mode",
            new[] { "split", "toggle" }),
        new("Split", "Split field", "S", "Turn split on",
            new[] { "split", "on" }),
        new("Split", "Split field", "T", "Show the transmit frequency",
            new[] { "transmit", "frequency", "show" }),
        // Same letter, same toggle, as the frequency field in both tuning
        // modes (#344, #361) — the split letter must not fail on the field
        // named Split.
        new("Split", "Split field", "P", "Toggle split on or off",
            new[] { "split", "toggle", "on", "off" }),

        // ── VOX field ──
        // V toggles VOX here rather than cycling slices (#361): the field's
        // own mnemonic outranks the universal V on the one field that owns
        // the concept. Everywhere else in Home, V still cycles slices.
        new("VOX", "VOX field", "Space, Up, Down, or V", "Toggle VOX on or off",
            new[] { "vox", "toggle", "voice" }),

        // ── Transmit slice field (QB Track I; absorbed here by QB Track L) ──
        // T here matches the T the Slice and Slice Operations fields already
        // teach — the transmit letter works on the field that owns transmit
        // (#361).
        new("TXSlice", "Transmit slice field", "Space or T", "Set transmit to the active slice",
            new[] { "transmit", "tx", "slice", "set" }),
        new("TXSlice", "Transmit slice field", "Up / Down", "Move transmit to another slice",
            new[] { "transmit", "tx", "slice", "move" }),
        new("TXSlice", "Transmit slice field", "A-H", "Set the transmit slice by letter",
            new[] { "transmit", "tx", "slice", "letter", "jump" }),
        new("TXSlice", "Transmit slice field", "Delete or Backspace", "Clear the transmit slice",
            new[] { "transmit", "tx", "slice", "clear", "keying", "lockout" }),

        // ── Offset field ──
        new("Offset", "Offset field", "Space, Up, or Down", "Cycle offset direction: off, plus, minus",
            new[] { "offset", "repeater", "direction" }),
        new("Offset", "Offset field", "Plus / Minus", "Set offset direction directly",
            new[] { "offset", "plus", "minus" }),

        // ── RIT and XIT fields ──
        new("RIT", "RIT field", "Space", "Toggle RIT on or off",
            new[] { "rit", "toggle" }),
        new("RIT", "RIT field", "Up / Down or U / D", "Adjust by the digit under the cursor, or by the chosen scale in scale-adjust mode",
            new[] { "rit", "adjust", "offset" }),
        new("RIT", "RIT field", "1 2 3 4", "Enter scale-adjust mode at 1, 10, 100, or 1000 hertz",
            new[] { "rit", "scale", "adjust", "mode" }),
        new("RIT", "RIT field", "0 or Escape", "Exit scale-adjust mode",
            new[] { "rit", "scale", "exit" }),
        new("RIT", "RIT field", "5-9", "Type a digit at the cursor position",
            new[] { "rit", "digit", "type" }),
        new("RIT", "RIT field", "Plus / Minus", "Make the offset positive or negative",
            new[] { "rit", "positive", "negative", "sign" }),
        new("RIT", "RIT field", "=", "Copy RIT to XIT",
            new[] { "rit", "xit", "copy" }),
        new("XIT", "XIT field", "Space", "Toggle XIT on or off",
            new[] { "xit", "toggle" }),
        new("XIT", "XIT field", "Up / Down or U / D", "Adjust by the digit under the cursor, or by the chosen scale in scale-adjust mode",
            new[] { "xit", "adjust", "offset" }),
        new("XIT", "XIT field", "1 2 3 4", "Enter scale-adjust mode at 1, 10, 100, or 1000 hertz",
            new[] { "xit", "scale", "adjust", "mode" }),
        new("XIT", "XIT field", "0 or Escape", "Exit scale-adjust mode",
            new[] { "xit", "scale", "exit" }),
        new("XIT", "XIT field", "5-9", "Type a digit at the cursor position",
            new[] { "xit", "digit", "type" }),
        new("XIT", "XIT field", "Plus / Minus", "Make the offset positive or negative",
            new[] { "xit", "positive", "negative", "sign" }),

        // ── Mute field (Classic only) ──
        new("Mute", "Mute field", "Space or M", "Toggle mute",
            new[] { "mute", "toggle" }),

        // ── Volume field (Classic only) ──
        new("Volume", "Volume field", "Up / Down", "Adjust volume",
            new[] { "volume", "adjust", "gain" }),
    };

    // ────────────────────────────────────────────────────────────────
    //  Home navigation — FrequencyDisplay-level keys.
    // ────────────────────────────────────────────────────────────────
    private static readonly FixedKeyEntry[] HomeNavigation =
    {
        new("HomeNav", "JJ Flexible Home", "Left / Right", "Move one character at a time across the fields",
            new[] { "navigate", "cursor", "left", "right" }),
        new("HomeNav", "JJ Flexible Home", "Home", "Jump to the first field (on the Slice field: pan center)",
            new[] { "navigate", "first", "field", "home" }),
        new("HomeNav", "JJ Flexible Home", "End", "Jump to the last field",
            new[] { "navigate", "last", "field", "end" }),
        new("HomeNav", "JJ Flexible Home", "Page Down", "Jump to the Frequency field (where the field itself doesn't use Page Down)",
            new[] { "navigate", "frequency", "jump" }),
        new("HomeNav", "JJ Flexible Home", "?", "Speak the keys for the field you're on",
            new[] { "help", "keys", "question", "shift slash", "field", "speak" })
            { KeySpoken = "Shift slash" },
        new("HomeNav", "JJ Flexible Home", "Shift+M", "Mute or unmute every slice at once",
            new[] { "mute", "all", "slices" }),
        new("HomeNav", "JJ Flexible Home", "Shift+Comma", "Release every slice except the one you are on",
            new[] { "release", "slices", "extra" }),
    };

    // ────────────────────────────────────────────────────────────────
    //  Value fields (ScreenFields expanders).
    // ────────────────────────────────────────────────────────────────
    private static readonly FixedKeyEntry[] ValueField =
    {
        new("ValueField", "Value fields", "Up / Down", "Adjust the value",
            new[] { "value", "adjust" }, "Radio", "ValueField"),
        new("ValueField", "Value fields", "Page Up / Page Down", "Large step adjust",
            new[] { "value", "page", "large", "step" }, "Radio", "ValueField"),
        new("ValueField", "Value fields", "Home / End", "Set to minimum or maximum",
            new[] { "value", "minimum", "maximum" }, "Radio", "ValueField"),
        new("ValueField", "Value fields", "Enter", "Type an exact value",
            new[] { "value", "enter", "type", "exact", "number" }, "Radio", "ValueField"),
        new("ValueField", "Value fields", "Escape", "Collapse the group; press Escape twice quickly to collapse all groups",
            new[] { "escape", "collapse", "group" }, "Radio", "ValueField"),
        new("ValueField", "Value fields", "Ctrl+Tab / Ctrl+Shift+Tab", "Next or previous category",
            new[] { "category", "next", "previous", "tab" }, "Radio", "ValueField"),
    };

    // ────────────────────────────────────────────────────────────────
    //  Filter bracket chords (active in Classic and Modern tuning modes).
    // ────────────────────────────────────────────────────────────────
    private static readonly FixedKeyEntry[] FilterChords =
    {
        new("Filter", "Anywhere in radio modes", "[ ]", "Widen the filter: [ moves the lower edge down, ] moves the upper edge up",
            new[] { "filter", "widen", "edge" }, "Radio", "Filter"),
        new("Filter", "Anywhere in radio modes", "Shift+[ Shift+]", "Slide the passband left or right",
            new[] { "filter", "slide", "passband" }, "Radio", "Filter"),
        new("Filter", "Anywhere in radio modes", "Ctrl+[ Ctrl+]", "Squeeze or pull both filter edges",
            new[] { "filter", "squeeze", "pull", "narrow", "widen" }, "Radio", "Filter"),
        new("Filter", "Anywhere in radio modes", "Alt+[ Alt+]", "Cycle filter presets",
            new[] { "filter", "preset", "cycle" }, "Radio", "Filter"),
        new("Filter", "Anywhere in radio modes", "[[ or ]]", "Double-tap to adjust a single filter edge; Escape exits",
            new[] { "filter", "edge", "double", "tap" }, "Radio", "Filter"),
    };

    // ────────────────────────────────────────────────────────────────
    //  The JJ key (Ctrl+J, then one more key). Truth source:
    //  KeyCommands.DoLeaderCommand; the two are checked against each other
    //  both ways by Radios.Tests/LeaderLayerConsistencyTests, and against
    //  the four-tier grammar by Radios.Tests/JjKeyGrammarTests.
    //
    //  THE GRAMMAR (#515, Noel's ruling, 2026-09-02) — so a chord is derived
    //  rather than memorised:
    //    plain letter  — opens a LAYER (JJ key A audio, JJ key F filter)
    //    Shift+letter  — JUMPS TO THAT SLICE, from anywhere, in any layer
    //    Ctrl+letter   — TOGGLES the thing whose initial it is
    //    Alt+letter    — everything else (speak the version)
    //  Every layer answers H with its own list and the slash key with the
    //  explorer. The plain-letter one-shots below predate the grammar and
    //  keep working until Noel walks each letter; the grammar test carries
    //  that list as recorded debt, so it can only shrink.
    //
    //  Context stays "Leader" — a machine key other code matches on — and
    //  the label is "JJ key", which is what the thing is called (#512).
    // ────────────────────────────────────────────────────────────────
    private static readonly FixedKeyEntry[] LeaderCommands =
    {
        new("Leader", "JJ key", "Ctrl+J, Ctrl+C", "Copy the message the history walk is sitting on",
            new[] { "copy", "clipboard", "paste", "spoken", "speech", "message", "report",
                    "text", "share", "leader" }, "Global", "General"),
        new("Leader", "JJ key", "Ctrl+J, N", "Toggle legacy Noise Reduction",
            new[] { "nr", "noise", "reduction", "leader", "toggle" }, "Radio", "DSP"),
        new("Leader", "JJ key", "Ctrl+J, B", "Toggle Noise Blanker",
            new[] { "nb", "noise", "blanker", "leader", "toggle" }, "Radio", "DSP"),
        new("Leader", "JJ key", "Ctrl+J, W", "Toggle Wideband Noise Blanker",
            new[] { "wnb", "wideband", "noise", "blanker", "leader" }, "Radio", "DSP"),
        new("Leader", "JJ key", "Ctrl+J, R", "Toggle On-Radio Neural Noise Reduction (the radio's own DSP)",
            new[] { "rnn", "neural", "noise", "reduction", "on-radio", "leader" }, "Radio", "DSP"),
        new("Leader", "JJ key", "Ctrl+J, S", "Toggle On-Radio Spectral Noise Reduction (the radio's own DSP)",
            new[] { "nrs", "spectral", "noise", "reduction", "on-radio", "leader" }, "Radio", "DSP"),
        new("Leader", "JJ key", "Ctrl+J, Shift+N", "Toggle NR Filter",
            new[] { "nr", "filter", "noise", "leader" }, "Radio", "DSP"),
        new("Leader", "JJ key", "Ctrl+J, Shift+R", "Toggle PC Neural Noise Reduction (runs on your computer)",
            new[] { "pc", "neural", "noise", "reduction", "leader" }, "Radio", "DSP"),
        new("Leader", "JJ key", "Ctrl+J, Shift+S", "Toggle PC Spectral Noise Reduction (runs on your computer)",
            new[] { "pc", "spectral", "noise", "reduction", "leader" }, "Radio", "DSP"),
        // DSP controls track (2026-08-11) — Q for "quiet": capture what the
        // band sounds like with nobody talking, so Spectral NR can subtract it.
        new("Leader", "JJ key", "Ctrl+J, Q", "Capture a noise profile for PC Spectral NR (press Q again to cancel)",
            new[] { "noise", "profile", "capture", "quiet", "qrn", "sample", "spectral", "sub",
                    "subtraction", "baseline", "leader" }, "Radio", "DSP"),
        // Sprint 36 Track C (#271) — the QSO signal analyzer. Ctrl+Q because
        // plain Q is the noise capture and Q is the letter "QSO" reaches for;
        // Ctrl+F, Ctrl+D and Ctrl+R are the precedent for the Ctrl-modified
        // form when the letter you want is taken. The two capture chords sit
        // side by side on purpose.
        new("Leader", "JJ key", "Ctrl+J, Ctrl+Q",
            "Start or stop the QSO signal analyzer — watch the S-meter, then hear what the signal did, QSB and all (press it again to stop and hear the report)",
            new[] { "qso", "signal", "analyzer", "analyse", "analyze", "capture", "watch",
                    "qsb", "fade", "fading", "fades", "flutter", "swing", "strength",
                    "smeter", "meter", "report", "peak", "trough", "average", "trend",
                    "rising", "falling", "coming", "up", "down", "leader" }, "Radio", "General"),
        // Sprint 38 Track C (#337) — switch the S-meter's unit and keep it.
        // Ctrl+S because the chord echoes the flat key it CHANGES: Ctrl+S
        // reads the meter, this decides what it reads in. Sits beside the
        // S-family DSP toggles on purpose; the description names both units so
        // a search for either idea finds it.
        //
        // For one day (#306, Sprint 37 Track G) this chord took a one-shot dBm
        // reading instead. Noel ruled the second reading out of scope on
        // 2026-08-28 — one unit is live at a time.
        new("Leader", "JJ key", "Ctrl+J, Ctrl+S",
            "Switch the S-meter between S-units and dBm — remembered for this radio",
            new[] { "dbm", "db", "signal", "strength", "smeter", "meter", "s-meter", "level",
                    "units", "unit", "toggle", "switch", "change", "s", "reading",
                    "precise", "precision", "fine", "exact",
                    "antenna", "compare", "comparison", "weak", "strong", "leader" },
            "Radio", "General"),
        // Sprint 44 Track J: Ctrl+A, not plain A. Plain letters open layers
        // under the four-tier grammar and A is the audio layer (#514, #515);
        // Ctrl is the toggle tier and A is Auto Notch's own initial.
        new("Leader", "JJ key", "Ctrl+J, Ctrl+A", "Toggle Auto Notch",
            new[] { "anf", "auto", "notch", "carrier", "heterodyne", "tone", "leader", "toggle" }, "Radio", "DSP"),
        new("Leader", "JJ key", "Ctrl+J, P", "Toggle Audio Peak Filter (CW only)",
            new[] { "apf", "audio", "peak", "filter", "cw", "leader" }, "Radio", "DSP"),
        // JJ key A — the audio layer's door under the four-tier grammar (#515):
        // a plain letter opens a layer, and A is ruled for audio. Wired by the
        // Sprint 44 integration pass; Track I built the layer and Track J freed
        // the letter (Auto Notch moved to Ctrl+A), and neither could reach the
        // other's worktree to join them.
        new("Leader", "JJ key", "Ctrl+J, A", "Enter the audio layer: a letter picks what to adjust, arrows adjust, Enter keeps it, Escape puts it back",
            new[] { "audio", "layer", "volume", "level", "pc", "output", "headphone", "mic", "pan", "compander", "processor", "adjust", "leader" }, "Radio", "Audio")
            { OpensLayer = AudioLayerContext },
        // Audio Arc Track A (2026-08-11) — "adjust how I sound and what I hear".
        // Sprint 44 Track I (#514): volume mode and pan mode became ONE audio
        // layer on the value sub-layer engine. V still opens it; the letter
        // itself is Track J's under the four-tier allocation (#515).
        new("Leader", "JJ key", "Ctrl+J, V", "Enter the audio layer: a letter picks what to adjust, arrows adjust, Enter keeps it, Escape puts it back",
            new[] { "volume", "audio", "level", "pc", "output", "headphone", "mic", "pan", "adjust", "mode", "layer", "leader" }, "Radio", "Audio")
            { OpensLayer = AudioLayerContext },
        // Sprint 37 Track C (#304) — the fine stereo-pan control, as a value
        // sub-layer (#305). Alt+P because the other tiers are taken: plain P is
        // APF, Shift+P the Speech Processor, and Ctrl+P became PC audio in
        // Sprint 44 Track J (#513). Flat Ctrl+P, outside the layer, is a third
        // thing again — the FREQUENCY panning field, which shares a word with
        // stereo pan and nothing else. Since Track I this door opens the audio
        // layer with pan already picked.
        new("Leader", "JJ key", "Ctrl+J, Alt+P",
            "Enter the audio layer on pan: arrows place the slice in the stereo field, Enter keeps it, Escape puts it back",
            new[] { "pan", "stereo", "balance", "left", "right", "center", "centre", "place",
                    "placement", "position", "field", "audio", "slice", "separate", "separation",
                    "apart", "ear", "mode", "layer", "leader" }, "Radio", "Audio")
            { OpensLayer = AudioLayerContext },
        // Audio Arc Keys Track (2026-08-11) — the mic check and the tone generator.
        new("Leader", "JJ key", "Ctrl+J, K", "Mic check: speak your mic-audio verdict and level, nothing else",
            new[] { "mic", "check", "microphone", "audio", "level", "verdict", "gain", "query",
                    "how", "sound", "hot", "peak", "dbfs", "leader" }, "Radio", "Audio"),
        new("Leader", "JJ key", "Ctrl+J, G", "Arm or disarm the TX test tone (replaces your microphone while transmitting)",
            new[] { "tone", "test", "generator", "arm", "disarm", "440", "transmit", "tx",
                    "audio", "check", "calibrate", "leader" }, "Radio", "Audio"),
        // Ctrl+P — P for PC, ruled #513 (Noel, 2026-09-01: "p for pc makes
        // more sense"). Ctrl is the toggle tier of the four-tier grammar and
        // P is the toggle's own initial. It was Ctrl+A from Sprint 32 Track G
        // (#130), when Noel named it on the unbound-command survey: "No hotkey
        // for PC audio on and off available that I know of, you have to do it
        // in the menu." Ctrl+A belongs to Auto Notch now, the toggle whose
        // initial it is. Sits one keystroke from Ctrl+J, V, P, which rides the
        // PC output LEVEL; this is the on/off switch, and the pairing is
        // deliberate.
        new("Leader", "JJ key", "Ctrl+J, Ctrl+P", "Turn PC audio on or off — whether radio audio plays through this computer",
            new[] { "pc", "audio", "on", "off", "toggle", "remote", "sound", "mute", "unmute",
                    "computer", "playback", "hear", "silence", "quiet", "leader" }, "Radio", "Audio"),
        new("Leader", "JJ key", "Ctrl+J, C", "Toggle Compander",
            new[] { "compander", "compression", "tx", "transmit", "voice", "leader", "toggle" }, "Radio", "Transmit"),
        new("Leader", "JJ key", "Ctrl+J, Shift+P", "Toggle Speech Processor",
            new[] { "speech", "processor", "proc", "tx", "transmit", "voice", "leader", "toggle" }, "Radio", "Transmit"),
        // Plain F and Shift+F spoke the TX and RX filter widths until Sprint
        // 44. Plain F is the filter layer's door now (#512) and both readouts
        // live inside it; Shift+F is slice F, per the Shift tier (#504). The
        // RX readout keeps its flat Ctrl+Alt+F as well, and the TX one is also
        // on the Radio menu. Wired by the Sprint 44 integration pass.
        new("Leader", "JJ key", "Ctrl+J, F", "Enter the filter layer: hold Left Shift for the low edge or Right Shift for the high edge, arrows move it, S speaks it",
            new[] { "filter", "layer", "width", "bandwidth", "edge", "low", "high", "narrow", "wide", "passband", "transmit", "receive", "leader" }, "Radio", "DSP")
            { OpensLayer = FilterLayerContext },
        new("Leader", "JJ key", "Ctrl+J, Ctrl+F", "Enter a frequency",
            new[] { "frequency", "enter", "leader" }, "Radio", "General"),
        new("Leader", "JJ key", "Ctrl+J, D", "Toggle tuning speech debounce",
            new[] { "debounce", "tuning", "speech", "leader" }, "Global", "General"),
        // Sprint 30 Track D. Ctrl+D, not plain D — plain D has been debounce
        // since before the diagnostic-log design was written — and not Shift+D,
        // which sits inside the Shift+A-Shift+H slice-jump range. Ctrl+J, Ctrl+F
        // is the in-layer precedent for a Ctrl-modified follow-on key.
        new("Leader", "JJ key", "Ctrl+J, Ctrl+D",
            "Start or stop a detailed capture — everything the app is doing",
            new[] { "capture", "detailed", "diagnostic", "diagnostics", "trace", "tracing", "log",
                    "record", "bug", "problem", "report", "verbose", "leader" }, "Global", "General"),
        // Sprint 31 Track Q (#100). Ctrl+R for "Recorded problems", parked
        // beside Ctrl+D so the two diagnostics chords live together: Ctrl+D
        // starts recording evidence, Ctrl+R reads what has already gone wrong.
        // Plain R is On-Radio Neural NR and Shift+R is its PC namesake, so
        // Ctrl+R is the only free R in the layer — and Ctrl+D and Ctrl+F are
        // the precedent for a Ctrl-modified follow-on key.
        new("Leader", "JJ key", "Ctrl+J, Ctrl+R",
            "Read the problems recorded this session",
            new[] { "problem", "problems", "recorded", "failure", "failed", "error", "errors",
                    "wrong", "issue", "issues", "went", "missed", "notification", "diagnostic",
                    "diagnostics", "history", "leader" }, "Global", "General"),
        // Sprint 35 Track D (#253). O for "what is On". Parked beside Ctrl+D and
        // Ctrl+R so the three diagnostics chords live together: Ctrl+D starts
        // recording evidence, Ctrl+R reads what has already gone wrong, and this
        // one answers what is running and costing something right now. Plain O
        // rather than a Ctrl form because O was free in every variant, so there
        // was no taken letter to reach around.
        new("Leader", "JJ key", "Ctrl+J, O",
            "Say what is still running and what it is costing — recording, captures, meter tones",
            new[] { "running", "on", "still", "what", "recording", "record", "instrumentation",
                    "capture", "meter", "stream", "transcript", "tones", "cost", "costing",
                    "size", "megabytes", "disk", "left", "forgot", "diagnostic", "diagnostics",
                    "leader" }, "Global", "General"),
        // Sprint 36 Track F (#269). V for Version; Alt because bare V is volume
        // mode and has been since the Audio Arc. The first Alt chord in the
        // layer — WpfKeyConverter resolves Key.System before the switch sees
        // it, so the trap that killed the 2026-08-13 Alt+L binding does not
        // reach here.
        new("Leader", "JJ key", "Ctrl+J, Alt+V",
            "Speak the version and build date of this copy",
            new[] { "version", "build", "which", "number", "release", "debug", "nightly",
                    "date", "built", "tester", "report", "identify", "about", "copy",
                    "running", "installed", "update", "updated", "leader" }, "Global", "General"),
        new("Leader", "JJ key", "Ctrl+J, L", "Speak log statistics",
            new[] { "log", "statistics", "stats", "leader" }, "Global", "Logging"),
        new("Leader", "JJ key", "Ctrl+J, M", "Open the memories dialog",
            new[] { "memory", "memories", "leader" }, "Radio", "Dialog"),
        new("Leader", "JJ key", "Ctrl+J, T", "Toggle meter tones",
            new[] { "meter", "tones", "leader", "toggle" }, "Global", "Audio"),
        // Sprint 33 Track F (#153). E for echo, and E is a single dit — the
        // smallest character in Morse, for the one chord that only ever answers
        // in Morse. Plain E was the last obvious free letter; Shift+E belongs to
        // the slice-jump row.
        new("Leader", "JJ key", "Ctrl+J, E", "Re-send recent CW notifications — press again for earlier ones",
            new[] { "repeat", "cw", "morse", "echo", "again", "history", "recent", "earlier",
                    "back", "previous", "missed", "resend", "code", "leader" }, "Global", "Audio"),
        new("Leader", "JJ key", "Ctrl+J, Shift+T", "Toggle alert sounds (earcons)",
            new[] { "earcon", "alert", "sounds", "leader", "toggle" }, "Global", "Audio"),
        // The Shift tier (#515): Shift+letter is that slice, from anywhere,
        // inside any layer, and nothing else — all eight, F included (#504).
        new("Leader", "JJ key", "Ctrl+J, Shift+A through Shift+H", "Jump to that slice from anywhere — the letter is the slice",
            new[] { "slice", "jump", "leader", "letter", "go", "switch", "select" }, "Radio", "General"),
        // The two help doors every layer has (#514, #519). H lists this
        // layer's commands; the slash key opens the JJ key explorer, a map of
        // every layer you move through at your own pace. Two rows because they
        // are two surfaces — "?" was once a second door to the list and is the
        // explorer's now, so the physical key means one thing however it
        // arrives. KeyDisplay keeps the glyph because LeaderChordParser reads
        // it; the spoken form names the keystroke (#303).
        new("Leader", "JJ key", "Ctrl+J, H", "Open the list of JJ key commands — one per row, to arrow through at your own pace",
            new[] { "leader", "jj", "help", "list", "commands", "keys", "what" }, "Global", "help"),
        new("Leader", "JJ key", "Ctrl+J, /", "Open the JJ key explorer — every layer and its keys, at your own pace",
            new[] { "leader", "jj", "help", "explorer", "explore", "browse", "tree", "map",
                    "layer", "layers", "keys", "slash", "question", "shift slash" }, "Global", "help")
            { KeySpoken = "Ctrl+J, slash" },
        new("Leader", "JJ key", "Ctrl+J, Escape", "Close the JJ key layer",
            new[] { "leader", "cancel", "escape" }, "Global", "help"),
    };

    // ────────────────────────────────────────────────────────────────
    //  Leader near-miss lookup (#206, Sprint 35 Track E)
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Chord → (spoken key name, description) for every advertised leader
    /// follow-on, built once from <see cref="LeaderCommands"/> via
    /// <see cref="Radios.LeaderChordParser"/>.
    /// </summary>
    private static Dictionary<WinFormsKeys, (string KeyName, string Description)>? _leaderChords;

    private static Dictionary<WinFormsKeys, (string KeyName, string Description)> LeaderChords()
    {
        var table = _leaderChords;
        if (table != null) return table;

        table = new Dictionary<WinFormsKeys, (string, string)>();
        foreach (var e in LeaderCommands)
        {
            foreach (var chord in Radios.LeaderChordParser.ParseDisplay(e.KeyDisplay, e.ExcludedKeys))
            {
                // Brief, not the full description: this table exists only to
                // answer the near-miss, and the near-miss is a one-breath
                // recovery line, not a help entry (#206). See LeaderPhrase.
                if (!table.ContainsKey(chord))
                    table[chord] = (KeyManifest.FormatKey(chord), Radios.LeaderPhrase.Brief(e.Description));
            }
        }
        _leaderChords = table;
        return table;
    }

    /// <summary>
    /// When an unbound leader chord is one modifier away from a bound one,
    /// name the bound neighbour so "Unknown command" becomes a recovery.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #206: the JJ layer mixes bare, Shift and Ctrl tiers on the same
    /// letters, so the muscle memory built by Ctrl+A and Ctrl+D carries
    /// straight into Ctrl+G, where nothing is bound. Noel pressed exactly
    /// that on 2026-08-23 and got "Unknown command. Press H for help." — a
    /// dead end that asks a blind operator standing inside a modal layer to
    /// leave it and listen to thirty entries for the one letter they nearly
    /// pressed, when the registry already knows the answer.
    /// </para>
    /// <para>
    /// At most ONE alternative comes back, bare form first — that is the most
    /// likely intent (see <see cref="Radios.LeaderChordParser.NearMissCandidates"/>).
    /// Returns false when the pressed chord is actually bound (not this
    /// method's business) or when no neighbouring tier is bound either.
    /// </para>
    /// </remarks>
    public static bool TryFindLeaderNearMiss(WinFormsKeys pressed,
        out string altKeyName, out string altDescription)
    {
        altKeyName = "";
        altDescription = "";

        var table = LeaderChords();
        if (table.ContainsKey(pressed)) return false;

        foreach (var candidate in Radios.LeaderChordParser.NearMissCandidates(pressed))
        {
            if (table.TryGetValue(candidate, out var hit))
            {
                altKeyName = hit.KeyName;
                altDescription = hit.Description;
                return true;
            }
        }
        return false;
    }

    // ────────────────────────────────────────────────────────────────
    //  The audio layer (Sprint 44 Track I, #514) — pan and volume, one
    //  layer on the value sub-layer engine. Opened by Ctrl+J, V, and by
    //  Ctrl+J, Alt+P with pan already picked, until Track J's four-tier
    //  allocation (#515) gives it JJ key A. Truth source:
    //  Radios.ValueSubLayer.HandleKey plus the definition in
    //  KeyCommands.EnterAudioLayer; the engine is unit-tested in
    //  Radios.Tests and the H list is generated from THIS table, so a key
    //  missing here is missing from the layer's own help.
    //
    //  Letters pick the target and Up/Down adjust everything; Left/Right
    //  ALSO adjust pan, because for that one target direction means
    //  something real. Plain H is help in every layer (#514), so
    //  headphone wears Ctrl; plain P is PC output, so pan wears Ctrl too.
    //  Shift+letter jumps to a slice from inside the layer (#515) — the
    //  layer never spends A-F on slices.
    // ────────────────────────────────────────────────────────────────
    public const string AudioLayerContext = "AudioLayer";
    public const string FilterLayerContext = "FilterLayer";

    private static readonly FixedKeyEntry[] AudioLayerCommands =
    {
        new(AudioLayerContext, "Audio layer", "Ctrl+H", "On-radio headphone volume — the radio's own headphone jack; Up and Down adjust",
            new[] { "headphone", "headphones", "earphones", "volume", "on-radio", "jack", "level" }, "Radio", "Audio"),
        new(AudioLayerContext, "Audio layer", "P", "PC output volume in dB — how loud radio audio plays through this computer; Up and Down adjust",
            new[] { "pc", "output", "volume", "computer", "playback", "boost", "db", "remote", "audio", "level" }, "Radio", "Audio"),
        new(AudioLayerContext, "Audio layer", "M", "Mic level — your transmit audio level, PC audio included; Up and Down adjust",
            new[] { "mic", "microphone", "level", "gain", "transmit", "audio" }, "Radio", "Audio"),
        new(AudioLayerContext, "Audio layer", "L", "On-radio line out volume — the radio's own line out jack; Up and Down adjust",
            new[] { "line", "out", "lineout", "volume", "on-radio", "jack", "level" }, "Radio", "Audio"),
        new(AudioLayerContext, "Audio layer", "C", "Compander level; Up and Down adjust",
            new[] { "compander", "level", "compression", "transmit" }, "Radio", "Transmit"),
        new(AudioLayerContext, "Audio layer", "S", "Speech processor mode: Normal, DX, DX plus; Up and Down step",
            new[] { "speech", "processor", "proc", "mode", "dx", "transmit" }, "Radio", "Transmit"),
        new(AudioLayerContext, "Audio layer", "Ctrl+P", "Pan for the slice you're on — Left and Right, or Up and Down, place it in the stereo field; Shift moves by one, 0 centers",
            new[] { "pan", "stereo", "balance", "left", "right", "center", "centre", "place",
                    "placement", "position", "field", "slice", "separate", "separation", "apart", "ear" }, "Radio", "Audio"),
        new(AudioLayerContext, "Audio layer", "Up / Down", "Adjust the picked target; Shift moves by one",
            new[] { "adjust", "up", "down", "fine", "shift" }, "Radio", "Audio"),
        new(AudioLayerContext, "Audio layer", "Home / End / 0", "Place the picked target without hunting for it: Home is minimum, End is maximum, 0 is center — zero on a target with no center",
            new[] { "home", "end", "zero", "minimum", "maximum", "min", "max", "hard", "left", "right",
                    "center", "centre", "full", "off", "jump", "slam", "place" }, "Radio", "Audio")
            { KeySpoken = "Home, End or 0" },
        new(AudioLayerContext, "Audio layer", "Shift+A through Shift+H", "Jump to that slice without leaving the layer — pan follows you to it",
            new[] { "slice", "jump", "letter" }, "Radio", "Audio"),
        new(AudioLayerContext, "Audio layer", "Enter", "Keep every change and leave the audio layer",
            new[] { "keep", "confirm", "enter", "exit" }, "Radio", "Audio"),
        new(AudioLayerContext, "Audio layer", "Escape", "Put back everything you moved and leave the audio layer",
            new[] { "cancel", "restore", "undo", "back", "escape", "exit" }, "Radio", "Audio"),
        new(AudioLayerContext, "Audio layer", "H", "List the audio layer's keys",
            new[] { "help", "list", "keys" }, "Radio", "help"),
        new(AudioLayerContext, "Audio layer", "?", "Explore this layer's keys (the same list as H until the JJ key explorer lands)",
            new[] { "help", "explore", "explorer", "tree", "question", "shift slash" }, "Radio", "help")
            { KeySpoken = "Shift slash" },
    };

    // ────────────────────────────────────────────────────────────────
    //  The filter layer (Sprint 44 Track I, #516) — JJ key F once Track J
    //  wires the door. The modifier picks the TARGET and the key picks the
    //  VERB: Left Shift is the low edge, Right Shift the high edge, no
    //  modifier the whole filter. Truth source: Radios.ValueSubLayer with
    //  the definition in KeyCommands.EnterFilterLayer. The bracket chords
    //  above are NOT removed — Don has learned them; this is a second door.
    // ────────────────────────────────────────────────────────────────
    private static readonly FixedKeyEntry[] FilterLayerCommands =
    {
        new(FilterLayerContext, "Filter layer", "Left Shift + Left / Right", "Walk the low edge of the filter",
            new[] { "filter", "low", "edge", "lower", "walk", "left shift" }, "Radio", "Filter"),
        new(FilterLayerContext, "Filter layer", "Right Shift + Left / Right", "Walk the high edge of the filter",
            new[] { "filter", "high", "edge", "upper", "walk", "right shift" }, "Radio", "Filter"),
        new(FilterLayerContext, "Filter layer", "Left / Right", "Slide the whole filter down or up, width intact",
            new[] { "filter", "slide", "passband", "shift", "move", "whole" }, "Radio", "Filter"),
        new(FilterLayerContext, "Filter layer", "Up / Down", "Slide the whole filter up or down, width intact",
            new[] { "filter", "slide", "passband", "shift", "move", "whole" }, "Radio", "Filter"),
        new(FilterLayerContext, "Filter layer", "Ctrl+Up / Ctrl+Down", "Widen or narrow the filter about its center",
            new[] { "filter", "width", "widen", "narrow", "wider", "narrower", "bandwidth", "center" }, "Radio", "Filter"),
        new(FilterLayerContext, "Filter layer", "Home / End / 0", "Send whatever you're holding straight to a place: Home its lowest, End its highest, 0 zero hertz. The same Shift that walks an edge sends it, no Shift sends the whole filter, Ctrl sends the width",
            new[] { "home", "end", "zero", "limit", "rail", "minimum", "maximum", "min", "max", "hard",
                    "narrowest", "widest", "slam", "jump", "place", "carrier", "edge", "width" }, "Radio", "Filter")
            { KeySpoken = "Home, End or 0" },
        new(FilterLayerContext, "Filter layer", "S", "Speak the whole filter",
            new[] { "filter", "speak", "read", "width", "edges" }, "Radio", "Filter"),
        new(FilterLayerContext, "Filter layer", "Left Shift + S", "Speak the low edge",
            new[] { "filter", "speak", "read", "low", "edge" }, "Radio", "Filter"),
        new(FilterLayerContext, "Filter layer", "Right Shift + S", "Speak the high edge",
            new[] { "filter", "speak", "read", "high", "edge" }, "Radio", "Filter"),
        new(FilterLayerContext, "Filter layer", "T", "Work on the transmit filter — one for the whole radio",
            new[] { "filter", "transmit", "tx" }, "Radio", "Filter"),
        new(FilterLayerContext, "Filter layer", "R", "Work on the receive filter of the slice you're on — where the layer starts",
            new[] { "filter", "receive", "rx" }, "Radio", "Filter"),
        new(FilterLayerContext, "Filter layer", "Shift+A through Shift+H", "Jump to that slice without leaving the layer — the receive filter follows you to it",
            new[] { "slice", "jump", "letter" }, "Radio", "Filter"),
        new(FilterLayerContext, "Filter layer", "Enter", "Keep every change and leave the filter layer",
            new[] { "keep", "confirm", "enter", "exit" }, "Radio", "Filter"),
        new(FilterLayerContext, "Filter layer", "Escape", "Put back everything you moved and leave the filter layer",
            new[] { "cancel", "restore", "undo", "back", "escape", "exit" }, "Radio", "Filter"),
        new(FilterLayerContext, "Filter layer", "H", "List the filter layer's keys",
            new[] { "help", "list", "keys" }, "Radio", "help"),
        new(FilterLayerContext, "Filter layer", "?", "Explore this layer's keys (the same list as H until the JJ key explorer lands)",
            new[] { "help", "explore", "explorer", "tree", "question", "shift slash" }, "Radio", "help")
            { KeySpoken = "Shift slash" },
    };

    /// <summary>
    /// The rows of one layer, in presentation order — what H lists and what
    /// the tree explorer describes. A layer whose context is unknown here is
    /// invisible to both, so a new layer registers its table above and
    /// yields it from <see cref="All"/>.
    /// </summary>
    public static IReadOnlyList<FixedKeyEntry> LayerCommands(string context)
        => All().Where(e => e.Context == context).ToList();

    /// <summary>
    /// The spoken fallback for a layer's H, generated from its table so it
    /// cannot drift from the layer: the headline the host supplies (the
    /// layer's name and where it stands), then the COUNT, then the keys.
    /// Count first (#519) so the operator knows at once what they are
    /// getting and never waits through a recitation wondering when it
    /// ends. SpokenKey throughout — this goes straight to a screen reader.
    /// </summary>
    public static string LayerHelpSpeech(string context, string headline)
    {
        var rows = LayerCommands(context);
        string keys = string.Join("; ", rows.Select(r =>
            Radios.Lexicon.Get("leader.layer_help_item", ("key", r.SpokenKey), ("description", r.Description))));
        return Radios.Lexicon.Get("leader.layer_help",
            ("headline", headline), ("count", rows.Count), ("keys", keys));
    }

    // ────────────────────────────────────────────────────────────────
    //  Audio Workshop local keys (Threads Track, 2026-08-12). These are
    //  dialog-local accelerators handled in AudioWorkshopDialog's
    //  OnPreviewKeyDown — not registry bindings, not global chords. The
    //  keyboard-reference.md "Audio Workshop" section documents them; this
    //  registration closes the keyboard audit the Workshop track could not
    //  finish because it did not own this file.
    // ────────────────────────────────────────────────────────────────
    private static readonly FixedKeyEntry[] AudioWorkshopKeys =
    {
        new("AudioWorkshop", "Audio Workshop", "Ctrl+Enter", "Start the Audio Check, or stop the one that's running",
            new[] { "audio", "check", "start", "stop", "transmit", "mic", "microphone",
                    "hear", "test", "workshop", "levels" }, "Global", "Audio"),
        new("AudioWorkshop", "Audio Workshop", "Ctrl+S", "Save an audio preset (your TX audio chain settings)",
            new[] { "save", "preset", "profile", "audio", "chain", "settings", "workshop" }, "Global", "Audio"),
        new("AudioWorkshop", "Audio Workshop", "Ctrl+O", "Load an audio preset",
            new[] { "load", "open", "preset", "profile", "audio", "chain", "settings", "workshop" }, "Global", "Audio"),
        new("AudioWorkshop", "Audio Workshop", "Alt+E", "Export an audio preset to a file you can share",
            new[] { "export", "preset", "file", "share", "send", "save", "audio", "workshop" }, "Global", "Audio"),
        new("AudioWorkshop", "Audio Workshop", "Alt+I", "Import an audio preset from a file (added to your saved presets, not applied to the radio)",
            new[] { "import", "preset", "file", "share", "friend", "open", "read", "audio", "workshop" }, "Global", "Audio"),
        new("AudioWorkshop", "Audio Workshop", "Alt+R", "Reset the TX audio chain to default settings",
            new[] { "reset", "default", "audio", "chain", "transmit", "workshop" }, "Global", "Audio"),
        new("AudioWorkshop", "Load Audio Preset picker", "Delete", "Delete the selected preset (asks first)",
            new[] { "delete", "remove", "preset", "audio", "workshop" }, "Global", "Audio"),
        new("AudioWorkshop", "Audio Workshop", "Escape", "While a check is transmitting: first press unkeys and stays, second press closes. Escape never leaves you transmitting",
            new[] { "escape", "stop", "unkey", "transmit", "close", "workshop" }, "Global", "Audio"),
        // F6 has moved between the Workshop's SECTIONS since 2026-08-13 and was
        // never registered here, so it appeared in neither the Keys dialog nor
        // the exported key list — a working key nobody could discover. Closed
        // by Sprint 32 Track G's keyboard audit.
        //
        // Worth stating plainly, because it has been got wrong twice: sections
        // move on F6, not on a single letter. AutomationProperties.HeadingLevel
        // does NOT give a screen reader's H navigation inside a dialog — H and
        // friends live in browse mode, for web pages and documents, while a WPF
        // dialog runs in focus mode where H simply types the letter.
        new("AudioWorkshop", "Audio Workshop", "F6", "Move to the next section within this category, and say which one",
            new[] { "section", "next", "move", "navigate", "group", "jump", "workshop" }, "Global", "Audio"),
        new("AudioWorkshop", "Audio Workshop", "Shift+F6", "Move to the previous section within this category",
            new[] { "section", "previous", "back", "move", "navigate", "group", "workshop" }, "Global", "Audio"),
    };

    // ────────────────────────────────────────────────────────────────
    //  Category navigation — Settings and the Audio Workshop both
    //  (Sprint 32 Track G, task #134). One pattern, two surfaces, so it
    //  is registered once here rather than twice per dialog.
    // ────────────────────────────────────────────────────────────────
    private static readonly FixedKeyEntry[] CategoryNavigationKeys =
    {
        new("Categories", "Settings and the Audio Workshop", "Ctrl+Tab", "Move to the next category, from anywhere in the dialog",
            new[] { "category", "categories", "next", "tab", "section", "page", "move",
                    "navigate", "settings", "workshop" }, "Global", "General"),
        new("Categories", "Settings and the Audio Workshop", "Ctrl+Shift+Tab", "Move to the previous category, from anywhere in the dialog",
            new[] { "category", "categories", "previous", "back", "tab", "section", "page",
                    "move", "navigate", "settings", "workshop" }, "Global", "General"),
        new("Categories", "Settings and the Audio Workshop", "Up / Down", "In the category list: move between categories",
            new[] { "category", "categories", "list", "arrow", "up", "down", "move" }, "Global", "General"),
    };

    // ────────────────────────────────────────────────────────────────
    //  PTT keys (Home focus), CW message keys, logging radio pane.
    // ────────────────────────────────────────────────────────────────
    private static readonly FixedKeyEntry[] OtherKeys =
    {
        // Context widened 2026-08-11 (Audio Arc Keys Track): the PTT keys now
        // also work with focus in the Home field groups (expanders) — an
        // operator riding Mic Level in the Audio group can key without
        // first tabbing back to Home. Field-reported: Ctrl+Space died there.
        new("PTT", "JJ Flexible Home and its field groups", "Ctrl+Space", "Push to talk — transmit while held",
            new[] { "ptt", "transmit", "push", "talk", "space" }, "Radio", "Transmit"),
        new("PTT", "JJ Flexible Home and its field groups", "Shift+Space", "Toggle transmit lock on or off",
            new[] { "ptt", "transmit", "lock", "toggle" }, "Radio", "Transmit"),
        new("PTT", "JJ Flexible Home and its field groups", "Escape", "Stop transmitting (while a transmit lock is on)",
            new[] { "ptt", "transmit", "stop", "escape" }, "Radio", "Transmit"),
        new("CWMessages", "CW messages", "Ctrl+1 through Ctrl+7", "Send the CW message in that slot",
            new[] { "cw", "message", "send", "macro" }, "Radio", "CwMessage"),
        new("LoggingPane", "Logging radio pane", "Up / Down", "Tune by one step",
            new[] { "tune", "logging", "step" }, "Logging", "Logging"),
        new("LoggingPane", "Logging radio pane", "Shift+Up / Shift+Down", "Tune by ten steps",
            new[] { "tune", "logging", "step" }, "Logging", "Logging"),
        new("LoggingPane", "Logging radio pane", "Left / Right", "Change the tuning step size",
            new[] { "tune", "step", "size", "logging" }, "Logging", "Logging"),
        new("LoggingPane", "Logging radio pane", "Ctrl+F", "Enter a frequency",
            new[] { "frequency", "enter", "logging" }, "Logging", "Logging"),
    };

    // ────────────────────────────────────────────────────────────────
    //  Command Finder door rows — dialogs and menu paths users must be
    //  able to FIND even though no single fixed key opens them. Emitted
    //  VERBATIM by CommandFinderItems() (no "(on ...)" suffix — their
    //  descriptions already carry any context they need). Deliberately
    //  NOT part of All(): these are doors, not keys, so they stay out of
    //  the key manifest and the Keys dialog's built-in view.
    //  QB Track L (2026-08-07): absorbed from the merge-time inline adds
    //  in ApplicationEvents.vb (QB Track I's discoverability rows).
    // ────────────────────────────────────────────────────────────────
    private static readonly FixedKeyEntry[] FinderDoors =
    {
        // Sprint 30 Track D. Three doors, because the operator hunting these
        // may search for any of "trace", "log", "diagnostic" or "bug" — the
        // vocabularies of the old surface, the new one, and the problem.
        new FixedKeyEntry
        {
            Description = "Diagnostics settings - what JJ Flex records, how much detail, and where it goes",
            Scope = "Global", Group = "Diagnostics",
            MenuText = "Tools menu, Diagnostics (also Settings, Diagnostics tab)",
            Keywords = new[] { "diagnostic", "diagnostics", "log", "logging", "trace", "tracing",
                               "detail", "verbose", "record", "problem", "bug", "report", "privacy" },
        },
        new FixedKeyEntry
        {
            Description = "Saved diagnostic logs - find, read, export or delete a past session",
            Scope = "Global", Group = "Diagnostics",
            MenuText = "Settings, Diagnostics tab, Browse saved logs",
            Keywords = new[] { "saved", "diagnostic", "logs", "archive", "browse", "trace", "session",
                               "export", "history", "prune", "delete", "old" },
        },
        new FixedKeyEntry
        {
            Description = "Save a problem report bundle - everything the developer needs, in one file",
            Scope = "Global", Group = "Diagnostics",
            MenuText = "Settings, Diagnostics tab, Save a problem report bundle",
            Keywords = new[] { "problem", "report", "bundle", "debug", "gather", "support",
                               "send", "developer", "zip", "diagnostic" },
        },
        new FixedKeyEntry
        {
            Description = "Transmit slice: set, move, or clear (Transmit slice field)",
            KeyDisplay = "Space, Up/Down, A-H, Delete",
            Scope = "Radio", Group = "FreqOut",
            MenuText = "Slice menu, Transmit Slice submenu",
            Keywords = new[] { "transmit", "tx", "slice", "clear", "keying", "lockout" },
        },
        // Sprint 33 Track K (#117, #59). Two doors, and the keyword lists are
        // the point of them. The operator hunting this does not know the word
        // "profile" — that is the entire finding. They know what happened TO
        // them: the slice came back, the change did not stick, the radio
        // forgot. So the searchable vocabulary has to be the SYMPTOM's and not
        // the mechanism's, or the search surface reproduces the very
        // discoverability failure it exists to fix.
        new FixedKeyEntry
        {
            Description = "Save your station setup into the radio so slice changes survive disconnecting",
            Scope = "Radio", Group = "FreqOut",
            MenuText = "Slice menu, Selection submenu, Save Station Setup to Radio",
            Keywords = new[] { "save", "stick", "sticky", "keep", "persist", "permanent", "remember",
                               "forget", "forgot", "back", "returns", "restore", "revert", "lost",
                               "slice", "layout", "setup", "station", "profile", "global",
                               "disconnect", "reconnect" },
        },
        new FixedKeyEntry
        {
            Description = "Profiles stored in the radio - global, transmit and microphone; select, add, rename, delete or save",
            Scope = "Radio", Group = "FreqOut",
            MenuText = "Radio menu, Profiles",
            Keywords = new[] { "profile", "profiles", "global", "mic", "microphone", "tx", "transmit",
                               "save", "load", "select", "rename", "delete", "station", "setup",
                               "contest", "ragchew", "shared" },
        },
        new FixedKeyEntry
        {
            Description = "Power dialog - transmit and tune power (dBm drive on a transverter)",
            Scope = "Radio", Group = "Transmit",
            MenuText = "Radio menu, Transmit, Power (also Slice menu, Transmission)",
            Keywords = new[] { "power", "watts", "dbm", "drive", "rf", "tune", "xvtr", "transverter", "output" },
        },
        new FixedKeyEntry
        {
            Description = "TX antenna selection",
            Scope = "Radio", Group = "Antenna",
            MenuText = "Radio menu, Transmit, TX Antenna (also Slice menu, Antenna)",
            Keywords = new[] { "antenna", "tx", "transmit", "xvtr", "transverter", "ant1", "ant2" },
        },
        new FixedKeyEntry
        {
            Description = "RX antenna selection",
            Scope = "Radio", Group = "Antenna",
            MenuText = "Slice menu, Antenna, RX Antenna",
            Keywords = new[] { "antenna", "rx", "receive", "ant1", "ant2", "rxa", "rxb" },
        },
        // DSP controls track (2026-08-11) — the PC noise reduction room:
        // strengths, floor, capture duration, and the saved-profile shelf.
        new FixedKeyEntry
        {
            Description = "Noise Profiles dialog - PC noise reduction strengths, capture duration, save and load profiles",
            Scope = "Radio", Group = "DSP",
            MenuText = "Slice menu, DSP, PC Noise Reduction, Noise Profiles (also the DSP field group's Noise Profiles button)",
            Keywords = new[] { "noise", "profile", "profiles", "spectral", "neural", "rnn", "strength",
                               "floor", "capture", "duration", "save", "load", "pc", "reduction", "folder" },
        },
        // Threads Track (2026-08-12) — the two levels dialogs (Audio Arc
        // Track A-2's doors). Each stays open while you ride its levels
        // with Up/Down; Ctrl+J, V volume mode is the fast route to the
        // same knobs.
        new FixedKeyEntry
        {
            Description = "PC Audio Levels dialog - how loud radio audio plays through this computer (dB boost) and your mic level",
            KeyDisplay = "Ctrl+J, V is the fast route",
            Scope = "Radio", Group = "Audio",
            MenuText = "Audio menu, PC Audio Levels (also Slice menu, Audio)",
            Keywords = new[] { "pc", "audio", "levels", "level", "volume", "output", "boost", "db",
                               "computer", "playback", "mic", "microphone", "gain", "loud", "remote" },
        },
        new FixedKeyEntry
        {
            Description = "On-Radio Levels dialog - the radio's own headphone and line out volumes, and the headphone, line out, and front speaker mutes",
            KeyDisplay = "Ctrl+J, V is the fast route",
            Scope = "Radio", Group = "Audio",
            MenuText = "Audio menu, On-Radio Levels (also Slice menu, Audio)",
            Keywords = new[] { "on-radio", "radio", "levels", "level", "volume", "headphone", "headphones",
                               "line", "out", "lineout", "speaker", "jack", "mute", "boost", "audio" },
        },
    };

    /// <summary>
    /// Every fixed (non-rebindable) key entry, in presentation order.
    /// </summary>
    public static IEnumerable<FixedKeyEntry> All()
    {
        foreach (var e in UniversalHome) yield return e;
        foreach (var e in HomeNavigation) yield return e;
        foreach (var e in FieldKeys) yield return e;
        foreach (var e in ValueField) yield return e;
        foreach (var e in FilterChords) yield return e;
        foreach (var e in LeaderCommands) yield return e;
        foreach (var e in AudioLayerCommands) yield return e;
        foreach (var e in FilterLayerCommands) yield return e;
        foreach (var e in AudioWorkshopKeys) yield return e;
        foreach (var e in CategoryNavigationKeys) yield return e;
        foreach (var e in OtherKeys) yield return e;
    }

    /// <summary>
    /// The JJ key H spoken help, generated from the LeaderCommands table so
    /// it can never drift from the inventory again (the pre-2026-08-11
    /// hand-written string had quietly dropped six commands). Ends with the
    /// pointer to the two other help surfaces, per the 2026-05-11 JJ+H audit:
    /// users reaching for JJ+H often actually want F1 or the Command Finder.
    /// </summary>
    public static string LeaderHelpSpeech()
    {
        var sb = new StringBuilder("JJ key commands: ");
        bool first = true;
        foreach (var e in LeaderCommands)
        {
            // SpokenKey, not KeyDisplay: this string goes straight to a screen
            // reader, and the help row's display form carries a bare "?" that
            // low punctuation drops on the floor (#303).
            string key = e.SpokenKey.StartsWith("Ctrl+J, ", StringComparison.Ordinal)
                ? e.SpokenKey.Substring("Ctrl+J, ".Length)
                : e.SpokenKey;
            if (!first) sb.Append("; ");
            sb.Append(key).Append(", ").Append(e.Description);
            first = false;
        }
        sb.Append(". For help on the control you are focused on press F1. To search every command press Control slash.");
        return sb.ToString();
    }

    // ────────────────────────────────────────────────────────────────
    //  Surface 1: DisplayField.HelpItems
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Map a runtime field key + tuning mode to the inventory context.
    /// </summary>
    private static string ContextFor(string fieldKey, bool modern) =>
        fieldKey == "Freq" ? (modern ? "Freq.Modern" : "Freq.Classic") : fieldKey;

    /// <summary>
    /// Build the HelpItems list for a home field — field-specific keys first,
    /// then the universal Home keys. Feeds the ShowHelpDialog per-field help.
    /// </summary>
    public static List<(string key, string description)> HelpItemsFor(string fieldKey, bool modern)
    {
        var context = ContextFor(fieldKey, modern);
        // SpokenKey throughout: ShowHelpDialog renders each row as ONE string
        // that is both drawn and read aloud, so whatever goes in the key column
        // has to survive a screen reader with punctuation set low (#303).
        var items = FieldKeys
            .Where(e => e.Context == context)
            .Select(e => (e.SpokenKey, e.Description))
            .ToList();
        if (items.Count == 0)
            items.Add(("This field is read-only", "no field-specific keys"));
        foreach (var u in UniversalHome)
            items.Add((u.SpokenKey, u.Description));
        // Pulled from the row rather than retyped, so the key this list ends
        // with and the key the inventory advertises cannot drift apart.
        var speakList = HomeNavigation.FirstOrDefault(e => e.KeyDisplay == "?");
        items.Add((speakList?.SpokenKey ?? "Shift slash", "speak this list"));
        return items;
    }

    /// <summary>
    /// The rows Ctrl+F1 reads on the Frequency field (#184): the live map for
    /// the current tuning mode, verbatim from the same tables that drive
    /// every other key surface. In Classic the cursor-movement row is
    /// prepended, because in Classic the cursor position IS the tuning step
    /// and the movement keys are the thing that sets it — a Classic map that
    /// never mentions Left and Right teaches half the mode.
    /// </summary>
    public static List<(string key, string description)> FrequencyContextRows(bool modern)
    {
        var rows = FieldKeys
            .Where(e => e.Context == (modern ? "Freq.Modern" : "Freq.Classic"))
            .Select(e => (e.KeyDisplay, e.Description))
            .ToList();
        if (!modern)
        {
            // Pulled by key display rather than duplicated, so the wording has
            // exactly one home. TuningContextHelpTests holds the positive
            // control proving this lookup still finds its row.
            var leftRight = HomeNavigation.FirstOrDefault(e => e.KeyDisplay == "Left / Right");
            if (leftRight != null)
                rows.Insert(0, (leftRight.KeyDisplay, leftRight.Description));
        }
        return rows;
    }

    // ────────────────────────────────────────────────────────────────
    //  Surface 2: the '?' handler
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the '?' speech for a home field: field-specific keys, then the
    /// universal keys. One string so a screen reader user hears it as a
    /// single utterance they can interrupt.
    /// </summary>
    public static string SpeakTextFor(string fieldKey, string fieldLabel, bool modern)
    {
        var context = ContextFor(fieldKey, modern);
        var sb = new StringBuilder();
        var specific = FieldKeys.Where(e => e.Context == context).ToList();
        sb.Append("Keys on ").Append(fieldLabel).Append(": ");
        if (specific.Count == 0)
        {
            sb.Append("no field-specific keys. ");
        }
        else
        {
            sb.Append(string.Join(", ", specific.Select(e => $"{e.SpokenKey} {e.Description}")));
            sb.Append(". ");
        }
        sb.Append("Anywhere in Home: ");
        sb.Append(string.Join(", ", UniversalHome.Select(e => $"{e.SpokenKey} {e.Description}")));
        sb.Append(", Shift+M mute all slices, Shift+Comma release extra slices.");
        return sb.ToString();
    }

    // ────────────────────────────────────────────────────────────────
    //  Surface 4: Command Finder informational rows
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Informational (non-executable) Command Finder rows generated from the
    /// inventory. Replaces the hand-built list that lived in
    /// ApplicationEvents.vb and had drifted from the real handlers.
    /// </summary>
    public static List<Dialogs.CommandFinderItem> CommandFinderItems()
    {
        var result = new List<Dialogs.CommandFinderItem>();
        foreach (var e in All())
        {
            result.Add(new Dialogs.CommandFinderItem
            {
                Description = $"{e.Description} (on {e.ContextLabel})",
                // The Command Finder's key column is a rendered cell that a
                // screen reader reads verbatim — one string serving eyes and
                // ears, so it has to be the pronounceable one (#303).
                KeyDisplay = e.SpokenKey,
                Scope = e.Scope,
                Group = e.Group,
                Keywords = e.Keywords,
                Tag = null, // informational — not executable
            });
        }
        // Door rows go out verbatim — their descriptions already name their
        // context, and their MenuText is the road there.
        foreach (var d in FinderDoors)
        {
            result.Add(new Dialogs.CommandFinderItem
            {
                Description = d.Description,
                KeyDisplay = d.KeyDisplay,
                Scope = d.Scope,
                Group = d.Group,
                MenuText = d.MenuText,
                Keywords = d.Keywords,
                Tag = null, // informational — not executable
            });
        }
        return result;
    }

    // ────────────────────────────────────────────────────────────────
    //  Reserved chords — keys the rebind capture must refuse.
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// True if the key can never be assigned to a command, with a spoken
    /// reason. Covers system keys, the leader trigger, PTT chords, and all
    /// unmodified non-function keys (those belong to the Home fields and
    /// text entry — a window-level binding would shadow them).
    /// </summary>
    public static bool IsReservedForCapture(WinFormsKeys k, out string reason)
    {
        var code = k & WinFormsKeys.KeyCode;
        var mods = k & WinFormsKeys.Modifiers;

        if (k == (WinFormsKeys.J | WinFormsKeys.Control))
        {
            reason = "Ctrl+J is the leader key and cannot be reassigned";
            return true;
        }
        if (k == (WinFormsKeys.Space | WinFormsKeys.Control) ||
            k == (WinFormsKeys.Space | WinFormsKeys.Shift))
        {
            reason = "That key is reserved for push to talk";
            return true;
        }
        if (code == WinFormsKeys.Escape || code == WinFormsKeys.Tab ||
            code == WinFormsKeys.Return)
        {
            reason = "Escape, Tab, and Enter are reserved for navigation";
            return true;
        }
        if (k == (WinFormsKeys.F4 | WinFormsKeys.Alt))
        {
            reason = "Alt+F4 is reserved by Windows";
            return true;
        }

        bool isFunctionKey = code >= WinFormsKeys.F1 && code <= WinFormsKeys.F24;
        if (mods == WinFormsKeys.None && !isFunctionKey)
        {
            reason = "Plain keys belong to the Home fields and text entry. Add Ctrl, Alt, or Shift, or use a function key";
            return true;
        }

        reason = string.Empty;
        return false;
    }
}
