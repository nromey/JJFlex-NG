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
