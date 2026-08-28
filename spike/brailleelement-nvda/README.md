# brailleElement NVDA spike — paint a line, get cursor routing back

Sprint 38 Track G research spike for task #347. **Research code, not a
product.** The production home for this mechanism is `tools/brailleElement/`
per the locked v1 decisions; this spike exists to answer one question on real
hardware: can we write a sample line to a braille display and, when a cursor
routing key is pressed, learn which element was clicked?

The design document for this spike lives in the private planning tree
(`planning/design/brailleelement-nvda-line-and-routing.md`), which also
records what was verified and what remains a human test.

## What is here

- `addon/` — an NVDA add-on, `brailleElementSpike`. A global plugin that
  paints four elements (`14.250  USB  fartsnoodle  Mute`) as a custom
  braille Region and speaks `clicked <name> (offset N)` when a routing key
  lands on one. Implements the locked v1 decisions: opt-in clickable
  indicator (dots 7 and 8), and dual focus dismissal (gain-focus event AND
  a 250 ms watchdog).
- `verify/verify_nvda_symbols_2026.py` — proves every NVDA name the spike
  touches exists in the installed NVDA, by scanning `library.zip` bytecode.
  Carries a positive control. Run after any NVDA upgrade.
- `verify/verify_region_logic.py` — runs the mapping logic outside NVDA
  against a fake `braille` module: cell-to-element resolution under identity
  AND synthetic contracted translation, separator no-ops, patch/update,
  indicator dots, and dismiss-after-clobber. Carries a positive control.

Run both from this directory:

    python verify/verify_nvda_symbols_2026.py
    python verify/verify_region_logic.py

Both were run 2026-08-28 against NVDA 2026.1 (2026.1.0.55743) and passed.

## What has NOT been done, on purpose

The add-on has never been loaded into NVDA. The one machine with NVDA on it
is the operator's live screen reader, and installing research code into a
live reader configuration is not a call an agent makes. Everything below is
the human's test.

## The hands-on test (a human with NVDA, ideally with a braille display)

Install to NVDA's developer scratchpad — nothing is registered, removal is
deleting a folder:

1. Copy `addon\globalPlugins\brailleElementSpike` to
   `%AppData%\nvda\scratchpad\globalPlugins\brailleElementSpike`.
2. In NVDA: Settings, Advanced, enable "Enable loading custom code from
   Developer Scratchpad directory" (skip if already on).
3. Press NVDA+Control+F3 to reload plugins. No restart needed.

Then, with a braille display connected:

- Press **NVDA+Shift+L**. Expect speech: "Braille element spike open, 40
  cells. Route to act." (cell count matches the display). Expect the display
  to show: `14.250  USB  fartsnoodle  Mute`.
- Press a **cursor routing key over any cell of "fartsnoodle"**. Expect
  speech: "clicked fartsnoodle (offset N)" — N is the cell's position within
  the word. This sentence is the acceptance test from the 2026-05-06 Track C
  handoff, verbatim.
- Press a routing key over `14.250`, `USB`, `Mute` — each must name itself.
- Press a routing key over a **separator gap** between elements. Expect
  nothing at all. (Deliberate: separator clicks are a no-op in v1.)
- **Pan** with the display's pan keys if the line overflows the display
  (set NVDA's braille cells lower, or use a shorter display, to force it).
  Panning must work with no code of ours involved, and routing after a pan
  must still name the right element — this is the load-bearing check that
  window-to-region position mapping holds.
- Press **NVDA+Control+Shift+L**. Expect "Clickable indicator on", and dots
  7 and 8 to appear under every cell of the four elements but not under the
  gaps. `14.250`, `USB`, `fartsnoodle`, `Mute` are all clickable in the
  demo, so all four get underlined. Toggle off, dots must vanish.
- With the line up, **switch to another window** (Alt+Tab). The session must
  dismiss itself — braille returns to normal NVDA rendering without any
  further keypress, within roughly a quarter second. Switch back; the line
  must NOT come back on its own (re-open is explicit).
- With the line up, run **contracted braille** (NVDA braille settings,
  output table) and repeat the fartsnoodle click. The element must still be
  named correctly even though it occupies fewer cells — this proves the
  liblouis position maps carry routing through contraction.
- **Without a display**: NVDA's Braille Viewer (Tools menu) shows the line
  visually and its "route to cell" hover can stand in for routing keys, but
  that is a sighted-assist path; the real test is routing keys on hardware.

Uninstall: delete
`%AppData%\nvda\scratchpad\globalPlugins\brailleElementSpike` and press
NVDA+Control+F3.

## Known limits of the spike

- Global demo only: it has no JJ Flexible appModule and no connection to the
  application. How element data travels from jjflexible.exe into the add-on
  is an open design question recorded in the design document.
- Gestures: the 2026-04-29 prototype used NVDA+Shift+B, which is a BUILT-IN
  binding in current NVDA (report battery status) — a global plugin would
  shadow it, silently stealing a working key. This spike uses NVDA+Shift+L
  and NVDA+Control+Shift+L, both verified absent from the installed 2026.1's
  built-in gestures. Other installed add-ons were not scanned — if a toggle
  does nothing, check Input Gestures for a conflict first. Both are
  rebindable there.
- The demo dismisses on ANY focus change. A production consumer scopes that
  to its own window's focus.
