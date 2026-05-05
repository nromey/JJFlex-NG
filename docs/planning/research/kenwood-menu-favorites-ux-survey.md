# Kenwood Menu Read/Write + Favorites — UX Pattern Survey

Research date: 2026-05-04. Audience: JJFlexRadio designer planning TS-590-line menu access and favorites UX. Method: paper survey of vendor pages, third-party product pages, user-guide excerpts, and forum descriptions. No installers were run; no screenshots were captured.

## 1. Summary

- The "kenwoodtalk" name the owner mentioned does not resolve to any specific identifiable product in public search results. The closest matches are Kenwood ARCP-590/590G (vendor), RT Systems KRS-590 (third-party programmer), Hamlib (open-source library), and the Quick Menu feature built into the radio itself. Treat "kenwoodtalk" as a probable mis-recall and investigate Quick Menu + ARCP-590G + KRS-590 as the actual prior art.
- The radio itself ships with a "Quick Menu" — a user-curated subset of EX-menu items, marked by pressing FINE on each item to add a checkmark. This is the canonical Kenwood "favorites" pattern and is what JJFlexRadio's favorites UX should mirror conceptually, while improving access.
- Vendor ARCP-590G offers two parallel views of the menu: a conventional flat list (numeric ordering, mirrors the radio) and a "category listing" (grouped by domain). This dual-view hint is the most useful cross-tool finding.
- Almost every other tool surveyed (HRD, N1MM, fldigi, DXLab, WSJT-X, JTDX) deliberately limits Kenwood menu surface to the handful of items they need (baud rate, audio routing, PTT source). They are not full-EX-menu editors. JJFlexRadio is filling a real gap here, not duplicating existing software.
- Third-party programmers (KRS-590) use a spreadsheet-style memory editor with menu items as a secondary tab. That pattern is mouse-centric and assumes sighted batch editing — directly hostile to the JJFlexRadio audience.

## 2. Software inventory

**Kenwood ARCP-590 / ARCP-590G** — Official vendor control program. Free download. Windows-only. ARCP-590 covers TS-590S; ARCP-590G covers TS-590SG (they are not interchangeable). Last meaningful release ARCP-590G v1.03 in late 2016, with a follow-on v1.2 published on third-party download mirrors. Companion ARHP-590 acts as the host side for KENWOOD Network Command System (KNS). Allows editing memory, the menu, and Auto Mode frequency, with read/write to the radio. Includes both a flat list and a category listing of menu items per Kenwood's own help text.

**Kenwood KCAT** — Older Kenwood control program, predates ARCP family, largely superseded. Not a meaningful target.

**RT Systems KRS-590 / KRS-590-USB** — Commercial third-party programmer (about USD 25 software-only, USD 50 with cable). Windows-only. Spreadsheet/grid layout; primary purpose is bulk memory channel management with menu items as a secondary surface. Updated through Windows 11 support era. Optimized for sighted batch edits via column-fill operations.

**Ham Radio Deluxe (HRD)** — Commercial multi-vendor rig control suite. Windows-only. Uses a Favorites tree (root folders, sub-folders, drag-and-drop, sort, marker overlay on bandscope) — but the Favorites are *frequencies and modes*, not radio menu settings. Does not expose the full TS-590 EX menu; it touches only the menu items needed to make CAT work (baud rate, USB audio routing).

**WSJT-X / JTDX / fldigi / flrig** — Open-source digital-mode tools. Cross-platform. CAT support via Hamlib. Touch a small set of menu items as documentation footnotes ("set menu 67 to USB, set menu 68 to 115200") rather than expose them in software. Hamlib's published Kenwood backend explicitly states control is limited to frequency, mode, and a small set of operating commands; it does not provide a generic EX-menu read/write surface.

**N1MM Logger+** — Free contest logger. Windows-only. Supports Kenwood CAT but only for log-relevant operations (frequency, mode, split, RIT/XIT). The Configurer is a multi-tab dialog for the *logger's* settings; it does not touch radio EX-menu items beyond CAT-prerequisite ones. N1MM has a public note that customizable colors/fonts are an explicit accessibility effort; relevant as prior-art on opt-in customization, not on menu access.

**DXLab Suite (Commander)** — Free multi-tool suite. Windows-only. Covers Kenwood TS-590 CAT and references menu items 61/62 for baud rate config — again, only the menu items required to make the link work, not the full surface.

**wfview** — Open source, Windows/Linux/Mac. Originally Icom-focused, has added Kenwood TS-890S support. Settings dialog uses left-side category navigation. Does not currently expose a full Kenwood EX menu.

**CHIRP** — Open source, cross-platform. Tabbed UI (Memories tab, Settings tab). Does not officially support TS-590 line; mainly handheld/mobile programming focus.

**Yaesu SCU-LAN10 (reference, not Kenwood)** — Useful comparison: only 15 of 37 transceiver menu items were exposed remotely. Vendor remote-control software trends toward a *partial* menu surface even on the vendor's own product.

**Icom RS-BA1 (reference)** — Splits into Remote Utility (config) and Remote Controller (operation). Two-program model is heavy and not directly applicable but worth noting.

**The radio's own Quick Menu** — Built-in feature on TS-590 family. Operator marks frequently-used EX items by pressing FINE on each item to add a checkmark. Quick Menu is then accessed by pressing MENU then MHz. It is a *flat customizable subset*, not a hierarchy, and it lives on the radio (per-radio, not per-operator across radios). This is the closest thing to a vendor-blessed "favorites" pattern in the Kenwood ecosystem.

## 3. Menu UX patterns observed

Patterns surfaced across the survey, with names:

- **Numeric flat list (radio-mirroring)**. ARCP-590G's "general listing" view; the radio's own panel; most third-party reference docs. Items addressed by EX number (e.g. "EX 62 — USB baud rate"). Pro: predictable mapping to the printed manual; testers and net controllers can read out a number and have everyone find it. Con: 80 items is overwhelming as a single scroll target; numeric labels are accessibility-hostile when read aloud (no semantic content).
- **Category-grouped list (domain-clustered)**. ARCP-590G's "category listing" view, alongside the flat one. Items grouped by domain such as Display, Audio/USB, CW, DSP, Memory, Remote, etc. Pro: matches operator mental models; a screen-reader user navigating by domain hears semantically meaningful headings. Con: requires editorial decisions on what category an item belongs in; some items legitimately belong in two places.
- **Spreadsheet/grid (KRS-590 style)**. Rows and columns with one item or one channel per row; mouse-centric, copy/paste between cells, column fill operations. Pro: dense and powerful for bulk edits. Con: fundamentally a sighted-mouse pattern; screen-reader navigation through a grid is workable but never optimal, and the pattern's value (column fill) does not apply to one-of-a-kind menu items.
- **Tabbed-by-domain dialog (CHIRP, N1MM Configurer, wfview)**. Settings split into named tabs; one tab per category. Pro: discoverable, keyboard-navigable via Ctrl+Tab. Con: hides items two levels deep (find the right tab, then find the field); poor for "search by name" workflows.
- **Tree with expand/collapse (HRD Favorites)**. Hierarchical folders, drag-and-drop. Pro: scales to many items; user-managed organization. Con: tree controls are notoriously inconsistent for screen readers across NVDA/JAWS without explicit ARIA-equivalent labels in WPF/WinForms.
- **Search/filter overlay (CHIRP "Hide Unused Fields", N1MM-style filtering)**. A view-mode toggle that suppresses irrelevant items rather than reorganizing them. Pro: keeps a single canonical list while reducing scan distance. Con: hidden items can confuse a user who expected to see everything.

## 4. Favorites/preset UX patterns observed

- **Hardware-side checkmark toggle (Kenwood Quick Menu)**. Operator stands on an item and presses FINE; checkmark appears; that item is now in the Quick Menu list. Single, reversible, in-place. No separate management screen. The Quick Menu does not modify the main menu — it is purely additive.
- **Hierarchical favorites tree (HRD)**. Root folders, sub-folders, drag-and-drop, alphabetical/numerical sort, marker overlay on bandscope. Tracks frequencies-with-mode, not menu settings; the structural pattern is still relevant.
- **Per-band / per-mode preset systems (multiple loggers)**. Implicit favorites tied to a context (e.g. "when on 20m USB, use these settings"). Not directly used for EX-menu editing in any tool surveyed but a common ham-software idiom.
- **Configuration profiles (RS-BA1, ARCP-590G save-to-file)**. Save-the-whole-state-to-file, load-the-whole-state-back. Different concept from "favorites"; closer to "snapshots." Worth offering as a separate feature alongside favorites — they solve different problems (favorites = quick recall of a few items; snapshots = full restore).
- **No favorites at all (most tools)**. WSJT-X, JTDX, fldigi, flrig, DXLab Commander, N1MM, KRS-590: none expose a user-curated favorites concept for EX-menu items. The Quick Menu *on the radio* is the only widely-known "favorites" pattern in this space.

## 5. Accessibility antipatterns identified

JJFlexRadio's design must NOT do the following, all observed in the surveyed tools:

- **Numeric-only labels.** "EX 62" with no descriptive name violates the no-silent-keystrokes spirit and the project's design rule that labels carry meaning. Always pair the number with a descriptive name; let the number be supporting metadata, not the primary label.
- **Spreadsheet/grid as the primary editor.** KRS-590's column-fill workflow is mouse-and-eyes design. Screen-reader navigation through a multi-column grid is workable, but it is never the right primary affordance for one-of-a-kind menu items.
- **Tabs that hide structure.** N1MM's Configurer and wfview's category sidebar both hide items behind a navigation step. Acceptable for grouping but must be paired with a flat, searchable view; never the only access path.
- **Drag-and-drop required for organization.** HRD's Favorites manager allows drag, copy, move buttons, and sort — keyboard alternatives exist, but drag is presented first. Provide explicit "Move up / Move down / Move to folder" keyboard commands, never lean on drag.
- **Bandscope-marker-only feedback.** HRD's "Show Markers" overlay communicates favorite location visually on the spectrum. Useless without audio/braille parallels. Any visual indicator must be paired with an audible/braille cue.
- **Modal "edit value" dialog with focus trap.** The TS-590S manual flow ("press MENU, scroll, press M.IN, change value") is itself stateful and modal; ARCP-590G presumably mirrors it. Any value-edit dialog must respect the project's Escape-closable rule and never trap focus.
- **No in-place announcement of state change.** When the user toggles an item to "favorite" in the Quick Menu pattern, the radio shows a checkmark visually. JJFlexRadio must announce the state change ("added to favorites" / "removed from favorites") audibly; silent visual indicators violate the no-silent-keystrokes rule.
- **Color-only state indication.** Implicit in most surveyed tools — favorited items glow, unsupported items grey out, dirty edits show a different background. JJFlexRadio must announce state in text/audio, not rely on color.
- **Mouse-only marker placement.** HRD's bandscope markers are dragged/clicked. Mark-as-favorite must be a single keyboard command on the focused item.
- **No search.** Most tools force linear scrolling or tab navigation. With 80 items, a name-or-keyword search is essential.
- **Two-program model (RS-BA1 split).** Forces the user to context-switch between tools. JJFlexRadio's existing single-app model is the right call; avoid surfacing a separate "menu editor" executable.

## 6. Borrowable patterns

Concrete patterns to consider, with justification:

- **Dual-view: flat list + category list, switchable.** Mirrors ARCP-590G's "general listing" + "category listing" idea. Lets net controllers reference EX numbers while letting domain-oriented operators browse by category. Switch via a single keystroke (e.g. F6 or Ctrl+1/Ctrl+2). Both views read the same underlying list.
- **In-place mark-as-favorite single-key toggle.** Lift the Quick Menu pattern: stand on an item, press a single key (proposed: F or Space or a chord like Ctrl+D for "default favorite"), state announced ("added to favorites" / "removed from favorites"). The radio's own Quick Menu uses FINE; we do not need to copy that exact key, but we should copy the *idea* of single-key in-place marking with audible confirmation.
- **Favorites view as a filtered projection of the same list.** Not a separate window or dialog. Toggle "favorites only" (proposed: F2-region command) and the same list shrinks to favorited items. Same labels, same value-edit affordances, same key bindings — just fewer rows. Removes the cognitive cost of "where am I now?"
- **Search/filter-as-you-type.** Type any letters and the list narrows. Standard incremental search. Critical for a 70-80 item list. Paired with a "clear search" Escape behavior.
- **Descriptive-name-first labels with EX number as suffix.** "USB audio output level (EX 65)" rather than "EX 65". Screen reader hears the meaning first; the number remains for cross-reference with manuals and net traffic.
- **Per-radio config keyed by serial.** Already a project principle (`radios\<serial>\config.xml`). Favorites should live there, not in user-scope; an operator with two TS-590s and a TS-2000 will not want the same favorites list across all three. This also matches the radio-side Quick Menu being radio-local.
- **Snapshot save/load as a separate feature.** Borrow from ARCP-590G save-to-file and RS-BA1 profile model. Implement as "save current radio state" / "restore from snapshot" — distinct from favorites. Solves the "I broke a setting and want everything back" problem that favorites do not solve.
- **Read-current-from-radio + Show-deltas.** Borrowed from KRS-590's read-from-radio flow plus a dirty-edit indicator: when a value differs from the radio's current state, announce "modified" alongside the value. Keyboard command to write changes back; never auto-write on focus change.
- **"Hide unused fields" mode** (CHIRP). Apply to JJFlexRadio as "hide license-gated, model-gated, or firmware-gated items that don't apply to this radio." A 6300 user does not need to scroll past 6700-only items.

## 7. Recommendation for JJFlexRadio

Adopt a **single linear list with three orthogonal view filters** plus an **in-place favorites toggle**.

The list:
- One canonical flat list of all menu items applicable to the connected radio.
- Each row is one item, with a descriptive-name-first label, optional EX number suffix, current value read from the radio, and a favorited-or-not state.
- Items are addressed by descriptive name in announcements; the EX number is supporting metadata read on demand.

The view filters (any combination, each toggleable by a single key):
- **Category filter** — restrict to one domain (Display, Audio/USB, CW, DSP, Memory, Remote, etc.). Cycle categories with a single key.
- **Favorites-only filter** — restrict to favorited items. This *is* the favorites view; no separate window.
- **Search filter** — incremental text match against names; clear with Escape.

The favorites toggle:
- Single key on the focused row marks/unmarks favorite. Audible state change announcement. No drag, no folder management, no hierarchy in v1.
- Favorites stored in `radios\<serial>\config.xml`, per the project's per-radio config principle.

Editing values:
- Press Enter on a row to enter edit mode (still inside the list, not a popup dialog where possible). Up/Down adjusts numeric or enumerated values; Enter commits; Escape cancels. Audible confirmation on commit.
- Values are pulled live from the radio on display, written to the radio on commit, and the row announces "modified" while uncommitted.

What to defer to v2:
- Folder/hierarchy organization of favorites. Quick Menu shows the radio's own designers concluded a flat list is enough; HRD's tree exists for *frequencies* (thousands of them), not for menu items (dozens).
- Save/restore snapshots. Useful but distinct feature; build it later as a separate command, not entangled with favorites.
- Cross-radio shared favorites. Per-radio config is correct in v1; if operators ask for a "copy favorites from another radio" import command later, add it then.

Where this lands in the JJFlexRadio design rules:
- Satisfies the flexibility principle (user-curated subset, conservative default of empty favorites, per-radio config).
- Satisfies no-silent-keystrokes (every toggle and edit announces).
- Satisfies dialog-Escape-closable (no popup dialogs in the primary flow).
- Satisfies the project's accessibility anti-pattern checklist (all items reachable, in-app discoverable via Command Finder + keyboard reference, no closed-source secret menus).

## 8. Open questions

- The "kenwoodtalk" name is unresolved. Worth one direct ask back to the project owner: was that a placeholder, a personal nickname for ARCP-590G, or a niche tool not in public search results? If it's a real product, its UX may have informed the owner's mental model and we should look at it before locking the design.
- Does the TS-590 line expose every EX-menu item over CAT, or are some hardware-only? The Hamlib backend's stated limitation ("frequency, mode, basic ops") suggests Hamlib does not implement full EX coverage, but the underlying serial protocol may. Spike test recommended: enumerate every EX item via the documented EX command and see which ones round-trip.
- Should JJFlexRadio expose the radio's own Quick Menu state as a separate concept ("Quick Menu mirror") or fold it into JJFlexRadio's favorites? Probably fold — operators want one favorites list, not two. But this needs tester input from Mark (TS-590SG) once the feature is in alpha.
- Verbosity ladder interaction: how much does favorites-list-navigation announce per row? Likely "name + value" terse, "name + value + EX number + category" chatty. Slot into the Sprint 30+ verbosity architecture rather than designing in isolation.
- Braille rendering: how does favorites-only view present on a 1-line linear braille display vs Dot Pad? Worth designing the row's braille cell layout in the same pass as the list view, since BrailleElement v1 is in motion.
- Tester pool: Mark is the committed TS-590SG tester. The expanded BHN net pool gives multiple TS-590-family testers. Surface the favorites/menu UX in front of all of them simultaneously rather than serially — different operators will pick different subsets and the diversity is the point.

## Sources

- [KENWOOD Radio Control Program ARCP-590G](https://www.kenwood.com/i/products/info/amateur/ts_590g/arcp590g_e.html)
- [KENWOOD Radio Control Program ARCP-590](https://www.kenwood.com/i/products/info/amateur/ts_590/arcp590_e.html)
- [ARCP-590G download mirror with version notes](https://arcp-590g.software.informer.com/)
- [Kenwood TS-590S Instruction Manual — Menu Setup chapter (ManualsLib)](https://www.manualslib.com/manual/523344/Kenwood-Ts-590s.html?page=22)
- [Kenwood TS-590SG Instruction Manual — Menu Setup chapter (ManualsLib)](https://www.manualslib.com/manual/1324817/Kenwood-Ts-590sg.html?page=22)
- [TS-590SG In-Depth Manual (Kenwood)](https://www.kenwood.com/i/products/info/amateur/pdf/TS-590SG_IDM.pdf)
- [Kenwood TS-590SG remote operating walkthrough — M0PZT blog](https://www.m0pzt.com/blog/ts-590sg-remote-operating/)
- [RT Systems KRS-590 product page](https://www.rtsystemsinc.com/TS-590-Programming-Software-and-USB-cable-p/krs-590-usb.htm)
- [KRS-590 at GigaParts](https://www.gigaparts.com/krs-590-software-and-rt-42-for-the-kenwood-ts-590s-and-ts-590sg.html)
- [Ham Radio Deluxe Favorites manual](https://support.hamradiodeluxe.com/support/solutions/articles/51000052480-favorites)
- [Ham Radio Deluxe TS-590 USB configuration](https://support.hamradiodeluxe.com/support/solutions/articles/51000054701-ts-590-590s-590sg-usb-configuration)
- [DXLab Suite — Connecting a Kenwood TS-590](https://www.dxlabsuite.com/dxlabwiki/ConnectingKenwoodUSB)
- [N1MM Logger Plus — The Configurer](https://n1mmwp.hamdocs.com/setup/the-configurer/)
- [N1MM Logger Plus — Supported Radios](https://n1mmwp.hamdocs.com/manual-supported/supported-radios/)
- [Hamlib Kenwood backend FAQ](https://github.com/Hamlib/Hamlib/wiki/FAQ)
- [Hamlib issue 1625 — TS-590 NR2 mode limitation](https://github.com/Hamlib/Hamlib/issues/1625)
- [wfview Settings manual](https://wfview.org/wfview-user-manual/settings/)
- [wfview Kenwood TS-890S Setup](https://wfview.org/wfview-user-manual/kenwood-ts-890s-setup/)
- [CHIRP Beginners Guide](https://chirpmyradio.com/projects/chirp/wiki/Beginners_Guide)
- [Yaesu SCU-LAN10 Operation Manual](https://www2.randl.com/man2/yaesu/sculan10.pdf)
- [Icom RS-BA1 Version 2 Instruction Manual](https://icomuk.co.uk/files/icom/PDF/productManual/RS-BA1_manual_ENG_3.pdf)
