using System.Collections.Generic;

namespace Radios
{
    /// <summary>What a row on the Audio menu is.</summary>
    public enum AudioMenuEntryKind
    {
        /// <summary>A divider. Present in every state, like everything else here.</summary>
        Separator,

        /// <summary>An ordinary command.</summary>
        Command,

        /// <summary>A command that also reports on or off.</summary>
        Toggle,
    }

    /// <summary>One row of the Audio menu.</summary>
    public sealed class AudioMenuEntry
    {
        public AudioMenuEntry(string id, string label, AudioMenuEntryKind kind,
                              bool needsRadio, string accelerator = "")
        {
            Id = id;
            Label = label;
            Kind = kind;
            NeedsRadio = needsRadio;
            Accelerator = accelerator ?? "";
        }

        /// <summary>Stable name the menu builder hangs a handler on. Never shown.</summary>
        public string Id { get; }

        /// <summary>The words on the row, with no accelerator column and no state suffix.</summary>
        public string Label { get; }

        public AudioMenuEntryKind Kind { get; }

        /// <summary>True when this command can do nothing without a connected radio.</summary>
        public bool NeedsRadio { get; }

        /// <summary>Key hint for the right-hand column, or empty.</summary>
        public string Accelerator { get; }
    }

    /// <summary>
    /// The Audio menu's shape, as data rather than as control flow (#214).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The complaint.</b> How many presses of a letter reaches a given item
    /// depended on whether a radio was connected. Noel, 2026-08-24: "when the
    /// radio is not connected, alt o then a goes to audio workshop, then audio
    /// devices. When I was connected to the radio ... a goes directly to audio
    /// devices." Muscle memory that works half the time is worse than none,
    /// because nothing tells you which half you are in.
    /// </para>
    /// <para>
    /// <b>The actual mechanism, which is not simply "items appear and
    /// disappear".</b> Windows menus start a first-letter search AFTER the
    /// currently highlighted item, and a popup opens with its first item
    /// highlighted. So an item that sits at the top of a menu is SKIPPED by its
    /// own first letter, and reached only on the second press. With no radio,
    /// every radio-gated command was omitted and Audio Devices became the first
    /// item — so pressing A stepped past it to Audio Workshop. With a radio, the
    /// gated commands pushed Audio Devices down the list, out of the highlighted
    /// position, and the same press landed on it. The item set never had to
    /// change much; it only had to change WHAT WAS FIRST.
    /// </para>
    /// <para>
    /// <b>The rule this file makes hold: the Audio menu's shape does not depend
    /// on connection state.</b> Every entry is present in every state. A command
    /// that needs a radio is greyed and says so in its own label when there is
    /// no radio, which is the treatment this menu bar already gives commands
    /// that are not available (see <c>AddNotImplemented</c> / <c>AddStub</c>,
    /// Sprint 32 Track H) and it was chosen there for this exact reason: a
    /// sighted operator reads "unavailable" from a greyed row in passing, while
    /// a keyboard operator who is given no row at all pays a full round trip to
    /// discover the same thing — or never learns the command exists.
    /// </para>
    /// <para>
    /// <b>What this did NOT fix, and what closed it afterwards.</b> Two rows
    /// here begin with A — Audio Devices and Audio Workshop — so under the
    /// skip-the-highlighted-item rule above, stable positions made the cycle
    /// learnable rather than single-press. The obvious answer was to rename one
    /// row, which meant changing a menu entry the help pages and years of
    /// muscle memory point at. Noel proposed a smaller one on 2026-08-27
    /// (#297): give Audio Workshop an explicit mnemonic of W instead. Nothing
    /// is renamed, W is the natural letter for Workshop, and Audio Devices
    /// keeps A. See the ampersand on that row below, and the class-wide
    /// carve-out at the top of <c>NativeMenuBar</c> that permits it.
    /// </para>
    /// <para>
    /// Held as data so the invariant is testable without constructing a window:
    /// the label sequence is a pure function of nothing at all, and the only
    /// thing connection state may change is a suffix.
    /// </para>
    /// </remarks>
    public static class AudioMenuLayout
    {
        /// <summary>
        /// Appended to a radio-gated entry's label when no radio is connected.
        /// Reads as a reason rather than a status, in the same shape as the
        /// menu bar's existing " - not yet implemented" and " - coming soon".
        /// </summary>
        public const string NeedsRadioSuffix = " - needs a radio";

        /// <summary>
        /// Every row of the Audio menu, in order, in every state.
        /// </summary>
        /// <remarks>
        /// Audio Workshop is part of this list even though it used to be
        /// appended by each caller after the shared builder ran. Two callers
        /// appending the same row in the same place is a second copy of the
        /// menu's shape living outside the menu's shape; with it here, the
        /// order below is the whole order and nothing can drift from it.
        /// </remarks>
        public static IReadOnlyList<AudioMenuEntry> Entries { get; } = new List<AudioMenuEntry>
        {
            new("mute-slice", "Mute/Unmute Slice", AudioMenuEntryKind.Toggle, true),
            new("mute-all-slices", "Mute/Unmute All Slices", AudioMenuEntryKind.Command, true),
            new("release-extra-slices", "Release All Extra Slices", AudioMenuEntryKind.Command, true),
            new("pc-audio", "PC Audio On/Off", AudioMenuEntryKind.Toggle, true),

            new("sep-levels", "", AudioMenuEntryKind.Separator, false),

            new("pc-audio-levels", "PC Audio Levels (this computer)",
                AudioMenuEntryKind.Command, true, "Ctrl+J, V"),
            new("on-radio-levels", "On-Radio Levels (the radio's own jacks)",
                AudioMenuEntryKind.Command, true, "Ctrl+J, V"),

            new("sep-setup", "", AudioMenuEntryKind.Separator, false),

            // Everything from here needs no radio, and the scratchpad leads it
            // ON PURPOSE. Two reasons, and the second is the load-bearing one.
            //
            // It groups better: device setup and the workshop are the two
            // destinations an operator actually comes here for, and the
            // scratchpad used to sit BETWEEN them.
            //
            // And it survives a Windows detail nobody can verify by reading
            // code. Greyed items are reachable by arrow key on Windows menus,
            // but whether one is HIGHLIGHTED when the popup opens — rather than
            // the first enabled item — is behaviour we would have to press a key
            // to learn. If Windows skips to the first enabled row, then with no
            // radio that row is whatever leads this block; if that row began
            // with A, it would be skipped by its own first letter and the whole
            // #214 defect would come straight back in the disconnected case.
            // A leading row that starts with E cannot do that. So the gesture
            // lands on Audio Devices either way, and the fix does not rest on an
            // assumption about Windows.
            new("earcon-scratchpad", "Earcon Scratchpad", AudioMenuEntryKind.Command, false),
            new("audio-devices", "Audio Devices", AudioMenuEntryKind.Command, false),

            // THE AMPERSAND IS DELIBERATE — DO NOT REMOVE IT ON AN
            // ACCESSIBILITY SWEEP (#297, 2026-08-27).
            //
            // Two rows here begin with "Audio", so first-letter exploration
            // cannot reach both in one press. An explicit mnemonic of W gives
            // the workshop its own letter and leaves Audio Devices holding A.
            // Nothing is renamed; W is simply the natural letter for Workshop.
            //
            // It is allowed under the CLASS-WIDE AMPERSAND CARVE-OUT documented
            // at the top of JJFlexWpf/NativeMenuBar.cs (#40): Win32 renders "&"
            // as an underlined access key rather than exposing the character,
            // so a screen reader reads the label cleanly. CLAUDE.md's general
            // "remove & from menu labels" rule is about WinForms labels, where
            // the character IS read aloud. Read that carve-out before touching
            // this line.
            //
            // Safe because this label is NOT SHARED: the only runtime consumer
            // of AudioMenuLayout is NativeMenuBar.BuildAudioItems, which hands
            // the string straight to AppendMenuW. No speech path, no Command
            // Finder row, no exported key list is built from it — which is
            // exactly what would turn this ampersand into "audio ampersand
            // workshop" in someone's ear. AudioMenuStabilityTests pins that:
            // see The_layout_label_is_read_by_nothing_but_the_native_menu.
            //
            // The in-file precedent is "A&ntenna" (NativeMenuBar.cs), same
            // collision, same fix, already shipping.
            new("audio-workshop", "Audio &Workshop", AudioMenuEntryKind.Command, false, "Ctrl+Shift+W"),
        };

        /// <summary>
        /// The text to put on the menu for <paramref name="entry"/>: its label,
        /// the reason it is unavailable when it is, then the accelerator column.
        /// </summary>
        /// <remarks>
        /// The suffix goes BEFORE the tab, not after. Windows treats everything
        /// past a tab as the right-hand key column, so a reason appended to the
        /// end would be laid out as part of the keystroke and read as part of it
        /// too.
        /// </remarks>
        public static string LabelFor(AudioMenuEntry entry, bool radioConnected)
        {
            if (entry == null) return "";
            if (entry.Kind == AudioMenuEntryKind.Separator) return "";

            string text = entry.Label;
            if (entry.NeedsRadio && !radioConnected) text += NeedsRadioSuffix;
            if (entry.Accelerator.Length > 0) text += "\t" + entry.Accelerator;
            return text;
        }
    }
}
