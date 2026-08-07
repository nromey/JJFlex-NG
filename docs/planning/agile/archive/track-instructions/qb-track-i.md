# QB Track I — Menu-parity audit + XVTR-aware power control

**Recommended model: Fable.** App-wide UI architecture routed from the
2026-08-07 audio session (plan section 4a). Document judgment calls in a
"Design decisions" section appended to this file.

## Context

One of the 2026-08-07 queue-burn tracks (plan:
`docs/planning/active/nightowl-pileup-ragchew.md`; queue: Track I
section). JJ Flex is a screen-reader-first FlexRadio client. Driver:
Noel uses ScreenFields expanders constantly but wants the ADDRESSABLE
path too — Alt+R → T (Transmit) → P (Power) should walk a menu with
accelerators into a Power dialog. Today power has no menu path anywhere,
and menu items that DO exist (TX/RX antenna submenus) have never been
found by the app's own author.

## Work items

1. **Menu-parity audit.** Inventory every actionable ScreenFields control
   (transmit, receive, antenna sections — `ScreenFieldsPanel.xaml.cs`)
   and map it against the menus (`NativeMenuBar.cs`). Three finding
   classes: (a) MISSING — add a menu item with accelerator (power is the
   flagship); (b) EXISTS-BUT-UNFINDABLE — TX/RX antenna submenus at
   ~685-707 are checkable, spoken, built from radio-reported lists, and
   unknown to the app's owner: fix placement/naming/accelerators so they
   are discoverable by exploration; (c) INCONSISTENT-ACROSS-MODES — the
   dispatch paths are not unified (four parallel paths, per project
   memory); verify every menu mode (Classic/Modern/Logging) builds the
   same radio-control items or document why not.
2. **The Power dialog.** Menu path with accelerators (Radio/Transmit
   territory), arrow-adjustable and type-in (via the shared
   ValueFieldControl once Track A's minus fix lands — coordinate at
   merge), spoken confirmation, honors radio min/max.
3. **XVTR-aware power entry.** When the selected TX antenna is a
   transverter port, the Power dialog AND the ScreenFields power field
   switch to dBm/decimal semantics (`Xvtr.MaxPower`, -10.0..+10.0 dBm in
   hundredths — Xvtr.cs:169-202); integer watts otherwise
   (`Radio.RFPower` is int — whole watts is a radio-side reality, not
   our choice). The unit is ANNOUNCED on entry ("power, in d B m").
   Rationale: mixer overdrive is the classic transverter killer; the
   radio's own design puts fine drive control only in the XVTR band.
4. **TX-slice doors** (moved here from Track A — it IS menu parity):
   a "Transmit" submenu in the Slice menu mirroring Selection (checkmark
   on the current TX slice), a Transmit-slice field on the slice page,
   and a Command Finder registration so "transmit slice" finds it.
   IMPORTANT: letters come from `Slice.Letter` (the radio's truth), never
   from positional arithmetic — Track J is fixing the position-vs-letter
   divergence; build against the letter as identity.
5. **Typed-digit check:** verify the ScreenFields power field accepts
   typed entry at all (flagged in the plan); if it routes through
   ValueFieldControl, decimal entry for dBm needs that control extended
   (coordinate with Track A's minus-sign fix — same control, same
   entry-mode code; agree the seam via the orchestrator if you both need
   to touch it).

## Ownership boundaries (do not cross)

- Yours: NativeMenuBar menu ADDITIONS for parity, the new Power dialog,
  ScreenFields power-field behavior, the Slice menu Transmit submenu.
- NOT yours: Track A's Radio menu maintenance section and menu stub
  audit (same file — A merges FIRST, you rebase on it; keep your diff in
  separate regions and coordinate through the orchestrator);
  `RigSelectorDialog` (E); the slice-identity mapping internals (J);
  the Keys surface (H — but every accelerator you add feeds H's
  manifest; list them in your completion report).
- New accelerators/hotkeys: menu accelerators are fine (that's the
  point); NEW global hotkeys need orchestrator sign-off (keyboard audit).

## Build & verify

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```
Timestamp must be fresh. Verify by ear-model: every new menu item speaks,
every accelerator chain reachable, checkmarks reflect radio truth.

## Commit style

Commit after each work item: `QB Track I: <what changed>`. Push to
`origin` (never `upstream`). Report completion to Noel when done.

## Design decisions (QB Track I, 2026-08-07)

### Menu-parity audit — full inventory

Every actionable ScreenFields control, mapped against the menus as found and
as left. Finding classes: (a) MISSING, (b) EXISTS-BUT-UNFINDABLE,
(c) INCONSISTENT-ACROSS-MODES.

**DSP expander.** Neural NR / Spectral NR / NRF / Legacy NR / NB / WNB /
FFT + Legacy notch / APF / Meter Tones / Peak Watcher: already had menu items
(Slice > DSP submenus) — no change. NR Level, NB Level, WNB Level: class (a),
FIXED — Up/Down pairs added inside their toggles' submenus. PC Neural NR and
PC Spectral NR: class (a), FIXED — new "PC Noise Reduction" submenu in the
DSP menu (routes through ScreenFieldsPanel.AudioPipeline; speaks "pipeline
not available" when no radio).

**Audio expander.** Mute, volume, pan, headphone, lineout, create/release
slice, mute-all, release-all: all already had menu paths — no change.

**Receiver expander.** AGC mode/threshold, squelch + level, RF gain: already
present (Slice > Receiver). RX filter width display is read-only; Filter menu
"Read Filter" covers it. No change.

**Transmission expander.** TX Power: class (a) FLAGSHIP, FIXED — the new
Power dialog, reachable from Radio > Transmit > Power and Slice >
Transmission > Power. Tune Power: class (a), FIXED — second field in the
Power dialog (deliberately NOT a menu Up/Down pair; set-and-confirm value,
the dialog is its home). VOX: existed. Mic Gain / Mic Boost / Mic Bias /
Compander + level / Speech Processor + mode / TX Monitor + level / TX filter
edges: all class (a), FIXED — items in the shared Transmit submenu (levels as
Up/Down pairs, mode as a cycle item, TX filter as its own submenu with a
Read entry).

**Antenna expander.** RX/TX antenna submenus: class (b), FIXED four ways —
(1) TX Antenna also hangs under Transmit, next to Power, where the
XVTR/power relationship is taught; (2) selecting the XVTR port speaks the
dBm semantics change at the moment it happens; (3) Command Finder now finds
"TX antenna" / "RX antenna" by name with the menu path named; (4) mnemonics
(Antenna takes N, since Audio owns A; RX / TX Antenna submenus take R / T).
ATU + ATU mode: existed. Diversity: existed with gating.

**Class (c) verdict.** Classic and Modern share ONE menu build path
(NativeMenuBar.BuildMenuBar — the mode switch only selects Logging vs
everything else), so Classic/Modern menu drift is structurally impossible
today. Logging mode intentionally builds a radio-less menu bar (Log /
Navigate / Mode / Help): it is a focused data-entry mode and the Mode menu
exits back to the full bar. Documented rather than "fixed" — parity across
Logging would put 60+ radio items in a mode built to keep them out of the
way. The "four un-unified dispatch paths" memory is about COMMAND dispatch
(menu handlers / KeyCommands / field handlers / Command Finder), which is why
every new door here was registered on each relevant path: menu item + Home
field + Command Finder entry (no new KeyCommands — no new global hotkeys).

### Judgment calls

1. **Transmit submenu: one builder, two doors.** Noel asked for Alt+R, T, P.
   Rather than duplicating item lists, BuildTransmitItems feeds both
   Radio > Transmit and Slice > Transmission, so the doors can never drift.
   The Slice-side submenu keeps its established "Transmission" name; the
   Radio-side door is "Transmit" (Noel's word, and T is unique in the Radio
   menu so first-letter navigation also works).

2. **Ampersand mnemonics on new items.** The old "remove ampersands from
   menu labels" guideline dates from the WinForms MenuStrip, where they
   leaked into accessible names. Native Win32 menus render them as the
   standard underlined access key and NVDA reads them cleanly — the
   top-level popups (Radio, Slice...) have used them all along. New Track I
   items get explicit mnemonics; existing items were left untouched
   (surgical diff, Track A rebase). Flag for Noel's live NVDA pass: if any
   mnemonic announces badly, the fix is dropping that one label's mnemonic,
   not restructuring.

3. **Power dialog is live-apply, no OK/Cancel.** Power is a continuous
   control (like the ScreenFields field it mirrors), not a form: each arrow
   step or confirmed typed value goes to the radio immediately and speaks.
   An OK/Cancel would imply a staged commit the radio API doesn't have.
   Escape closes (JJFlexDialog base). Tune power rides along in the same
   dialog — same watts domain, same operator moment (setting up drive
   levels).

4. **XVTR drive carried as centi-dBm integers.** ValueFieldControl is
   integer-based; drive needs hundredths of a dB. Value is scaled integer
   (550 = 5.50 dBm) with DecimalPlaces=2 formatting at the display/speech
   boundary, rather than converting the control to floating point — smaller
   diff, no float-comparison hazards, and the radio's own resolution IS
   hundredths (f2 wire format).

5. **Which transverter is "active".** Xvtr has an RF start frequency but no
   reported band width. Resolution rule: valid XVTRs at-or-below the slice
   frequency, highest start wins; if nothing matches and exactly one XVTR is
   defined, use it; else none (power surfaces stay in watts and honest).
   Radio._xvtrs is private, so FlexBase mirrors the list via the public
   XvtrAdded/XvtrRemoved events (sub xvtr all is already in the FlexLib
   connect sequence).

6. **Drive bounds mirror the vendor clamp.** Xvtr.MaxPower clamps to
   [-10, +10] on 6400/6600 with IF below 80 MHz, [-10, +15] on others,
   [-10, +8] when IF is 80 MHz or above. FlexBase.XvtrDriveMaxCentiDbm
   reproduces that (spec said -10..+10; the vendor rule is the truer
   "honors radio min/max"). The vendor setter still clamps — ours is a UI
   bound.

7. **"No Transmit Slice" is a real state, so it got a door.** slice set N
   tx=0 with no successor is legitimate; FlexBase.ClearTransmitSlice sends
   it and drops TXVFO to noVFO. Surfaced as the checkable "No Transmit
   Slice" menu entry and Delete/Backspace on the TXSlice field, spoken as
   "Transmit slice cleared. No slice will key the radio." — doubles as a
   soft TX lockout.

8. **TXSlice Home field placement and speech.** After VOX (the orchestrator's
   "near VOX/RIT/XIT"), one character: the TX slice letter, "-" when none,
   with "-" translated to "none" in speech (dash is meaningless). Letters
   always resolve through VFOToLetter (Slice.Letter — Track J's identity
   rule); the A-H set-by-letter handler matches reported letters, never
   positional arithmetic.

9. **Watts speech in ScreenFields vs the dialog.** The ScreenFields TX Power
   field in watts mode keeps its historical unlabeled announcements ("TX
   Power 50") to avoid regressing muscle-memory speech; in dBm mode the unit
   ALWAYS rides the value ("TX Power 5.50 dBm") because ambiguity there is
   dangerous. The new Power dialog, having no legacy speech, labels both
   units.

10. **Menu step sizes.** Menu Up/Down actions use coarser steps than the
    ScreenFields fields (mic gain 5 vs 1, levels 5) — a menu walk per step
    is expensive; fine-grained work belongs to the field or dialog. Matches
    the existing Audio menu convention (gain step 10 vs field step 5).

11. **Level items without mnemonics.** Up/Down pairs (Compander Level,
    Monitor Level, NR/NB/WNB levels) rely on arrow navigation and
    first-letter cycling; the mnemonic budget went to the single-action
    items an operator addresses by name.

### J-audit fixes folded in (orchestrator relay)

- Selection submenu: iterates MyNumSlices (own slices only); other stations'
  slices are one speaking summary entry ("N in use by other stations...")
  instead of numeric rows that silently no-opped. Transmit Slice submenu
  built the same way from the start.
- "Release Slice X" build-time-letter drift: label is now "Release Active
  Slice"; the click-time announcement speaks the letter actually released.
  Gate moved from TotalNumSlices to MyNumSlices.

### Integration points for the merge (orchestrator)

- **ValueFieldControl vs Track A's minus fix.** Track A landed
  ToggleBufferSign()/BeginNumberEntry() as the designated seam. Track I's
  versions use the SAME names and shape (aligned in commit be220732, marked
  INTEGRATION POINT in-code). At rebase keep one copy of each and fold I's
  decimal/unit additions (DecimalPlaces gate, point handling, scaled
  confirm, Setup decimalPlaces/unit parameters) on top. I's version also
  rejects minus audibly on non-negative fields — keep whichever refusal
  wording A chose if they differ.
- **Track A's Radio menu maintenance section.** The "Transmit" submenu was
  inserted between "Local PTT On" and the Logging submenu — away from the
  connection cluster A touches, but same file region; trivial rebase
  expected.
- **Track J identity doors.** All Track I code resolves letters via
  VFOToLetter and indexes via ValidVFO/MyNumSlices — no positional letter
  arithmetic anywhere — so J's sorted-mySlices world needs no changes here.
  If J's LetterToVFO door exists post-merge, AdjustTxSlice's set-by-letter
  loop is a natural (optional) simplification.
- **Track H key manifest.** New MENU mnemonics only, no new global hotkeys.
  Radio menu: Transmit (T). Within Transmit: Power (P), Tune Carrier (C),
  TX Antenna (T), VOX On/Off (V), Mic Gain Up (U), Mic Gain Down (D),
  Mic Boost (B), Mic Bias (I), Compander (M), Speech Processor (S),
  TX Monitor (O), TX Filter (F), Dummy Load (L). Slice menu: Transmit
  Slice (L), Antenna (N), RX Antenna (R), TX Antenna (T). New FIELD keys on
  the TXSlice Home field: Space, Up/Down, A-H, Delete/Backspace (all in the
  field's F1 HelpItems). New Home field means keyboard-reference.md gains a
  TXSlice section — left for H's reconciliation pass per ownership
  boundaries.
- **Orphan note.** FiltersDspControl and RadioNumberBox (Track A finding:
  never instantiated) were NOT touched or built upon — the queue's
  RF-gain-hardcode sibling bug in FiltersDspControl is moot until something
  instantiates it.
