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
/// This is DATA, deliberately in one place, because it drives five surfaces
/// that used to drift apart:
///   1. DisplayField.HelpItems (the per-field F1-style help dialog)
///   2. The '?' speak-keys-here handler on home fields
///   3. The Keys dialog's read-only "Home and field keys" view
///   4. Command Finder informational rows (formerly hand-built in
///      ApplicationEvents.vb)
///   5. The generated key manifest (KeyManifest) reconciled against
///      docs/help/md/keyboard-reference.md
///
/// If a field handler in FreqOutHandlers gains or loses a key, update the
/// tables here — the five surfaces above follow automatically.
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
        public string Description { get; init; } = "";
        public string[] Keywords { get; init; } = Array.Empty<string>();
        public string Scope { get; init; } = "Radio";
        public string Group { get; init; } = "FieldKeys";

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
        new("Slice", "Slice field", "0-7 or A-H", "Jump straight to that slice",
            new[] { "slice", "jump", "letter", "number" }),
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
        new("SliceOps", "Slice operations field", "S", "Sound — unmute (explicit)",
            new[] { "sound", "unmute" }),
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
        new("Freq.Classic", "Frequency field (Classic tuning)", "F", "Speak the current frequency",
            new[] { "frequency", "speak", "read" }),
        new("Freq.Classic", "Frequency field (Classic tuning)", "S", "Turn split on",
            new[] { "split", "on" }),
        new("Freq.Classic", "Frequency field (Classic tuning)", "T", "Toggle showing the transmit frequency",
            new[] { "transmit", "frequency", "show", "tx" }),

        // ── Frequency field, Modern tuning ──
        new("Freq.Modern", "Frequency field (Modern tuning)", "Up / Down", "Tune by your coarse step",
            new[] { "tune", "coarse", "step" }),
        new("Freq.Modern", "Frequency field (Modern tuning)", "Shift+Up / Shift+Down", "Tune by your fine step",
            new[] { "tune", "fine", "step" }),
        new("Freq.Modern", "Frequency field (Modern tuning)", "Digits", "Type a frequency, then Enter to apply",
            new[] { "frequency", "enter", "type", "digits" }),
        new("Freq.Modern", "Frequency field (Modern tuning)", "F", "Speak the current frequency",
            new[] { "frequency", "speak", "read" }),
        new("Freq.Modern", "Frequency field (Modern tuning)", "Shift+S", "Speak the coarse and fine step sizes",
            new[] { "step", "speak", "coarse", "fine" }),

        // ── S Meter field ──
        new("SMeter", "S Meter field", "Space", "Speak the current S meter reading",
            new[] { "s meter", "signal", "speak", "read" }),

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

        // ── VOX field ──
        new("VOX", "VOX field", "Space, Up, or Down", "Toggle VOX on or off",
            new[] { "vox", "toggle", "voice" }),

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
            new[] { "help", "keys", "question", "field", "speak" }),
        new("HomeNav", "JJ Flexible Home", "Shift+M", "Mute or unmute every slice at once",
            new[] { "mute", "all", "slices" }),
        new("HomeNav", "JJ Flexible Home", "Shift+Comma", "Release every slice except the first",
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
    //  Leader key commands (Ctrl+J, then one more key). Truth source:
    //  KeyCommands.DoLeaderCommand.
    // ────────────────────────────────────────────────────────────────
    private static readonly FixedKeyEntry[] LeaderCommands =
    {
        new("Leader", "Leader key", "Ctrl+J, N", "Toggle legacy Noise Reduction",
            new[] { "nr", "noise", "reduction", "leader", "toggle" }, "Radio", "DSP"),
        new("Leader", "Leader key", "Ctrl+J, B", "Toggle Noise Blanker",
            new[] { "nb", "noise", "blanker", "leader", "toggle" }, "Radio", "DSP"),
        new("Leader", "Leader key", "Ctrl+J, W", "Toggle Wideband Noise Blanker",
            new[] { "wnb", "wideband", "noise", "blanker", "leader" }, "Radio", "DSP"),
        new("Leader", "Leader key", "Ctrl+J, R", "Toggle Neural Noise Reduction",
            new[] { "rnn", "neural", "noise", "reduction", "leader" }, "Radio", "DSP"),
        new("Leader", "Leader key", "Ctrl+J, S", "Toggle Spectral Noise Reduction",
            new[] { "nrs", "spectral", "noise", "reduction", "leader" }, "Radio", "DSP"),
        new("Leader", "Leader key", "Ctrl+J, Shift+N", "Toggle NR Filter",
            new[] { "nr", "filter", "noise", "leader" }, "Radio", "DSP"),
        new("Leader", "Leader key", "Ctrl+J, Shift+R", "Toggle PC Neural Noise Reduction (runs on your computer)",
            new[] { "pc", "neural", "noise", "reduction", "leader" }, "Radio", "DSP"),
        new("Leader", "Leader key", "Ctrl+J, Shift+S", "Toggle PC Spectral Noise Reduction (runs on your computer)",
            new[] { "pc", "spectral", "noise", "reduction", "leader" }, "Radio", "DSP"),
        new("Leader", "Leader key", "Ctrl+J, A", "Toggle Auto Notch",
            new[] { "anf", "auto", "notch", "leader" }, "Radio", "DSP"),
        new("Leader", "Leader key", "Ctrl+J, P", "Toggle Audio Peak Filter (CW only)",
            new[] { "apf", "audio", "peak", "filter", "cw", "leader" }, "Radio", "DSP"),
        new("Leader", "Leader key", "Ctrl+J, F", "Speak the TX filter width",
            new[] { "tx", "filter", "width", "speak", "leader" }, "Radio", "audio"),
        new("Leader", "Leader key", "Ctrl+J, Shift+F", "Speak the RX filter width",
            new[] { "rx", "filter", "width", "speak", "leader" }, "Radio", "audio"),
        new("Leader", "Leader key", "Ctrl+J, Ctrl+F", "Enter a frequency",
            new[] { "frequency", "enter", "leader" }, "Radio", "General"),
        new("Leader", "Leader key", "Ctrl+J, D", "Toggle tuning speech debounce",
            new[] { "debounce", "tuning", "speech", "leader" }, "Global", "General"),
        new("Leader", "Leader key", "Ctrl+J, L", "Speak log statistics",
            new[] { "log", "statistics", "stats", "leader" }, "Global", "Logging"),
        new("Leader", "Leader key", "Ctrl+J, M", "Open the memories dialog",
            new[] { "memory", "memories", "leader" }, "Radio", "Dialog"),
        new("Leader", "Leader key", "Ctrl+J, T", "Toggle meter tones",
            new[] { "meter", "tones", "leader", "toggle" }, "Global", "Audio"),
        new("Leader", "Leader key", "Ctrl+J, Shift+T", "Toggle alert sounds (earcons)",
            new[] { "earcon", "alert", "sounds", "leader", "toggle" }, "Global", "Audio"),
        new("Leader", "Leader key", "Ctrl+J, Shift+A through Shift+H", "Jump to that slice from anywhere (Shift+F is reserved)",
            new[] { "slice", "jump", "leader", "letter" }, "Radio", "General"),
        new("Leader", "Leader key", "Ctrl+J, H or ?", "List the leader key commands",
            new[] { "leader", "help", "list" }, "Global", "help"),
        new("Leader", "Leader key", "Ctrl+J, Escape", "Cancel leader mode",
            new[] { "leader", "cancel", "escape" }, "Global", "help"),
    };

    // ────────────────────────────────────────────────────────────────
    //  PTT keys (Home focus), CW message keys, logging radio pane.
    // ────────────────────────────────────────────────────────────────
    private static readonly FixedKeyEntry[] OtherKeys =
    {
        new("PTT", "JJ Flexible Home", "Ctrl+Space", "Push to talk — transmit while held",
            new[] { "ptt", "transmit", "push", "talk", "space" }, "Radio", "Transmit"),
        new("PTT", "JJ Flexible Home", "Shift+Space", "Toggle transmit lock on or off",
            new[] { "ptt", "transmit", "lock", "toggle" }, "Radio", "Transmit"),
        new("PTT", "JJ Flexible Home", "Escape", "Stop transmitting (while a transmit lock is on)",
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
        foreach (var e in OtherKeys) yield return e;
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
        var items = FieldKeys
            .Where(e => e.Context == context)
            .Select(e => (e.KeyDisplay, e.Description))
            .ToList();
        if (items.Count == 0)
            items.Add(("This field is read-only", "no field-specific keys"));
        foreach (var u in UniversalHome)
            items.Add((u.KeyDisplay, u.Description));
        items.Add(("?", "speak this list"));
        return items;
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
            sb.Append(string.Join(", ", specific.Select(e => $"{e.KeyDisplay} {e.Description}")));
            sb.Append(". ");
        }
        sb.Append("Anywhere in Home: ");
        sb.Append(string.Join(", ", UniversalHome.Select(e => $"{e.KeyDisplay} {e.Description}")));
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
                KeyDisplay = e.KeyDisplay,
                Scope = e.Scope,
                Group = e.Group,
                Keywords = e.Keywords,
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
