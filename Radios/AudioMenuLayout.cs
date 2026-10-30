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

        /// <summary>
        /// A row that opens a submenu of its own. The submenu's rows and their
        /// handlers live in its builder, not here.
        /// </summary>
        Submenu,
    }

    /// <summary>Which menu is being built from <see cref="AudioMenuLayout"/>.</summary>
    /// <remarks>
    /// Two menus build from the one layout: the top-level Audio menu and the
    /// Slice menu's Audio submenu. They hold the same rows in the same order,
    /// except the rows marked <see cref="AudioMenuEntry.TopLevelOnly"/>, which
    /// only the top-level menu shows (#836).
    /// </remarks>
    public enum AudioMenuHost
    {
        /// <summary>The top-level Audio menu on the menu bar.</summary>
        AudioMenu,

        /// <summary>The Audio submenu inside the Slice menu.</summary>
        SliceAudioSubmenu,
    }

    /// <summary>One row of the Audio menu.</summary>
    public sealed class AudioMenuEntry
    {
        public AudioMenuEntry(string id, string label, AudioMenuEntryKind kind,
                              bool needsRadio, string accelerator = "", bool topLevelOnly = false)
        {
            Id = id;
            Label = label;
            Kind = kind;
            NeedsRadio = needsRadio;
            Accelerator = accelerator ?? "";
            TopLevelOnly = topLevelOnly;
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

        /// <summary>
        /// True when only the top-level Audio menu shows this row, and the
        /// Slice menu's Audio submenu leaves it out. See
        /// <see cref="AudioMenuLayout.EntriesFor"/>.
        /// </summary>
        public bool TopLevelOnly { get; }
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
    /// that needs a radio is greyed and says "connect a radio" in its own label
    /// when there is no radio, which is the treatment this menu bar already gives commands
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
        /// In the same shape as the menu bar's existing " - not yet implemented"
        /// and " - coming soon", but it names the ACTION that makes the row
        /// work rather than only the lack (#839). It read " - needs a radio"
        /// until then; Noel on the bench, 2026-10-28: "it says 'needs a radio'
        /// which is technically true, but probably should say 'connect a
        /// radio'".
        /// </summary>
        public const string ConnectRadioSuffix = " - connect a radio";

        /// <summary>
        /// Every row of the top-level Audio menu, in order, in every state. The
        /// Slice menu's Audio submenu shows the same rows less the
        /// <see cref="AudioMenuEntry.TopLevelOnly"/> ones; build from
        /// <see cref="EntriesFor"/>.
        /// </summary>
        /// <remarks>
        /// Audio Workshop is part of this list even though it used to be
        /// appended by each caller after the shared builder ran. Two callers
        /// appending the same row in the same place is a second copy of the
        /// menu's shape living outside the menu's shape; with it here, the
        /// order below is the whole order and nothing can drift from it. The
        /// DSP submenu (#836) is here, flagged, for the same reason rather than
        /// appended by the one caller that shows it.
        /// </remarks>
        public static IReadOnlyList<AudioMenuEntry> Entries { get; } = new List<AudioMenuEntry>
        {
            new("mute-slice", "Mute/Unmute Slice", AudioMenuEntryKind.Toggle, true),
            new("mute-all-slices", "Mute/Unmute All Slices", AudioMenuEntryKind.Command, true),
            new("release-extra-slices", "Release All Extra Slices", AudioMenuEntryKind.Command, true),
            new("pc-audio", "PC Audio On/Off", AudioMenuEntryKind.Toggle, true),

            // #537. The radio's own stereo widening of what you hear. It was
            // wired the whole time and reachable only from Ctrl+B inside the
            // audio layer; Jim had a control for it and the WinForms-to-WPF
            // move lost it. Last in the toggle block because it is the only
            // radio-wide one here — the three above it act on a slice or on
            // this computer — and because its B is a first letter no other row
            // on this menu claims.
            //
            // NO ACCELERATOR, and that is not an oversight — Ctrl+J, V, Ctrl+B
            // does reach it. A checked row's text is rewritten as "{label}: On"
            // when the popup opens, and everything past a tab is the Windows key
            // column, so a key hint here would put ": On" INSIDE the keystroke
            // and read as part of it. Same reason AddRadioChecked exists. The
            // other two toggles on this menu carry no accelerator either.
            new("binaural", "Binaural", AudioMenuEntryKind.Toggle, true),

            // #787, Tape Deck track 2: the computer recorder. Computer
            // recording goes under Audio, slice recording under Slice (Noel,
            // 2026-10-21 11:01). Toggles, so no accelerator column, for the
            // reason Binaural gives above. Record to File needs a radio (it
            // records what the radio plays through this computer); Play from
            // Computer does not, which makes it the first row that works with
            // no radio. Its P is not an A, so #214's first-letter skip cannot
            // land on it in the disconnected case either; see
            // The_first_row_that_works_without_a_radio_does_not_start_with_A.
            new("sep-recorder", "", AudioMenuEntryKind.Separator, false),
            new("record-to-file", "Record to File", AudioMenuEntryKind.Toggle, false),
            new("play-from-computer", "Play from Computer", AudioMenuEntryKind.Toggle, false),
            // #787, Tape Deck track 4: the recordings dialog, beside the two
            // controls whose recordings it lists. A command, so it carries its
            // flat key in the accelerator column (Noel, 2026-10-24 15:49). It
            // works with no radio: the files are on this computer.
            new("recorded-qsos", "Recorded QSOs", AudioMenuEntryKind.Command, false, "Ctrl+Alt+Shift+F11"),

            new("sep-levels", "", AudioMenuEntryKind.Separator, false),

            new("pc-audio-levels", "PC Audio Levels (this computer)",
                AudioMenuEntryKind.Command, true, "Ctrl+J, V"),
            new("on-radio-levels", "On-Radio Levels (the radio's own jacks)",
                AudioMenuEntryKind.Command, true, "Ctrl+J, V"),

            // #836: the DSP submenu - noise reduction, noise blankers, PC noise
            // reduction, auto notch and the audio peak filter. It
            // sat only under the Slice menu from Sprint 23, when the Operations
            // menu that used to hold it was deleted, and nobody noticed for
            // seven months. Noel, 2026-10-27: "This should not be in slice ...
            // this needs to be moved to audio."
            //
            // TOP-LEVEL ONLY, the one place the two menus built from this
            // layout differ. He ruled it OFF the Slice menu, and the Slice
            // menu's Audio submenu is built from this same list, so without the
            // flag it would come straight back under Slice, Audio. Per-slice
            // DSP inside each slice's own submenu is #586's, later.
            //
            // Every item acts on the active slice, as it did under Slice. The
            // row itself needs no radio, because some of what is inside works
            // without one (PC noise reduction's switches and its folder); the
            // rows inside that do need one are greyed and say so in their own
            // labels, through MarkNeedsRadio, exactly as the rows above do.
            // D is a first letter no other row on this menu claims.
            new("sep-dsp", "", AudioMenuEntryKind.Separator, false, topLevelOnly: true),
            new("dsp", "DSP", AudioMenuEntryKind.Submenu, false, topLevelOnly: true),

            // Meter tones came along inside DSP in that move and get their own
            // row instead (Noel, 2026-10-27 20:41: "I think it gets its own
            // row"). They are not signal processing and do not act on a slice.
            // Top-level only for the same reason as DSP: before #836 their one
            // home was Slice, DSP, and a copy under Slice, Audio would be a
            // second. Its M is shared with the two Mute rows at the top.
            new("meter-tones", "Meter Tones", AudioMenuEntryKind.Submenu, false, topLevelOnly: true),

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

            // #835: Check your audio, beside the Workshop whose Hear Yourself
            // page it was (Noel, 2026-10-27 14:59: "I'm guessing we'd put check
            // audio in the audio menu as well?"). No other row here starts with
            // C, so its first letter reaches it in one press and no ampersand
            // is needed. It opens with no radio: the window says so itself.
            // The accelerator column names the JJ key chord, as the levels rows
            // above do for theirs.
            new("check-your-audio", "Check Your Audio", AudioMenuEntryKind.Command, false, "Ctrl+J, Alt+C"),
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
            if (entry.Accelerator.Length > 0) text += "\t" + entry.Accelerator;
            return entry.NeedsRadio ? MarkNeedsRadio(text, radioConnected) : text;
        }

        /// <summary>
        /// The rows <paramref name="host"/> shows, in order: every entry on the
        /// top-level Audio menu, and every entry except the
        /// <see cref="AudioMenuEntry.TopLevelOnly"/> ones on the Slice menu's
        /// Audio submenu.
        /// </summary>
        /// <remarks>
        /// Only WHICH MENU may change the set. Connection state never does,
        /// which is the rule this file exists to hold (#214).
        /// </remarks>
        public static IReadOnlyList<AudioMenuEntry> EntriesFor(AudioMenuHost host)
        {
            if (host == AudioMenuHost.AudioMenu) return Entries;

            var rows = new List<AudioMenuEntry>();
            foreach (var entry in Entries)
                if (!entry.TopLevelOnly) rows.Add(entry);
            return rows;
        }

        /// <summary>
        /// Menu text for a row that needs a radio: unchanged with one, and with
        /// <see cref="ConnectRadioSuffix"/> inserted before the accelerator column
        /// without one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Public so a submenu hung off this menu marks its own rows the same
        /// way, rather than growing a second wording or a second rule for where
        /// the reason goes. The DSP submenu (#836) is the first.
        /// </para>
        /// <para>
        /// The suffix goes BEFORE the tab, not after. Windows treats everything
        /// past a tab as the right-hand key column, so a reason appended to the
        /// end would be laid out as part of the keystroke and read as part of it
        /// too.
        /// </para>
        /// </remarks>
        public static string MarkNeedsRadio(string menuText, bool radioConnected)
        {
            menuText ??= "";
            if (radioConnected) return menuText;

            int tab = menuText.IndexOf('\t');
            return tab < 0
                ? menuText + ConnectRadioSuffix
                : menuText.Substring(0, tab) + ConnectRadioSuffix + menuText.Substring(tab);
        }
    }
}
