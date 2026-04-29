# Cross-AT braille primitive — NVDA prototype

**Status:** prototype, Phase 5 of Track C
**Goal:** demonstrate the primitive design from `cross-at-primitive-design.md` against a real NVDA install on a real Focus 40 (or similar). NVDA-only; JAWS adapter is future work.

This prototype is **not the final library**. It's a minimal proof that the design holds when you actually wire it up. If something falls over here, Phase 4 needs revising before any production-track work begins.

## What it does

Adds an NVDA add-on `brailleElementDemo` that exposes one keystroke: **NVDA+Shift+B**. Pressing it toggles a demo session on the braille display showing three elements:

```
Play  Stop  Mute
```

When the user presses a cursor-routing key over any element, NVDA speaks "Element clicked: play" / "Element clicked: stop" / "Element clicked: mute". When the user presses NVDA+Shift+B again, the session dismisses and NVDA returns to its default focus rendering.

That's the entire prototype. It tests:

- `BrailleElementSession.open()` injecting a custom Region into `mainBuffer`.
- `_ElementRegion.routeTo(braillePos)` translating cell-position to element ID.
- `BrailleElementSession.dismiss()` cleanly restoring the prior state.
- Cooperative panning via NVDA's built-in pan keys (the demo string fits on most displays, so panning is more interesting if you bump the cell count down via NVDA's settings or with extra padding).

## Install

1. Build the `.nvda-addon` file:
   - This prototype follows the standard NVDA add-on layout. To package, run `scons` against the `addon` template (https://github.com/nvdaaddons/AddonTemplate).
   - Or manually: zip the contents of this directory (excluding `README.md`) and rename `.zip` to `.nvda-addon`.
2. In NVDA, Tools → Manage Add-ons → Install. Pick the `.nvda-addon`. Restart NVDA when prompted.

## Test

1. Verify a braille display is connected and recognized by NVDA (Preferences → Braille → Settings).
2. Press **NVDA+Shift+B**. The display should show `Play  Stop  Mute` (or wrapped if you have a small display).
3. Press a cursor-routing key over any element's text. NVDA should speak "Element clicked: <name>".
4. Press **NVDA+Shift+B** again. The display returns to whatever NVDA was rendering before.
5. **Negative test:** route over a separator (the spaces between elements). Expected: nothing happens (no speech, no action). This confirms the design choice from `cross-at-primitive-design.md` §5.4.

## What this prototype proves (and doesn't)

Proves:
- The Region-injection pattern (Pattern B from the NVDA survey) works without breaking NVDA's normal behavior.
- Routing keys dispatch correctly through the buffer to our region.
- Panning works without our code (NVDA does it).
- A globalPlugin is the right packaging unit for the primitive library.

Does not prove (deferred):
- JAWS adapter (no JAWS prototype here; the design has a `[verify in FSDN.chm]` gap).
- Multi-line display behavior (most testers will have single-line; handled by NVDA).
- High-frequency `update()` performance (the demo is static; production needs a 1Hz update loop).
- Coexistence with another add-on that touches braille (BrailleExtender). Worth a smoke test if a tester has it installed.
- Focus-loss dismissal (the demo doesn't have a host control with focus; NVDA+Shift+B opens the session globally).

## Files

- `manifest.ini` — NVDA add-on manifest, names, version.
- `globalPlugins/brailleElementDemo/__init__.py` — the demo plugin: keybinding, session lifecycle.
- `globalPlugins/brailleElementDemo/_session.py` — the actual primitive (BrailleElementSession + _ElementRegion).
- `addon/locale/en/LC_MESSAGES/nvda.po` — translations stub (not populated).

## Notes

- NVDA's logging (NVDA+1) shows debug output at the `info` level if anything misbehaves.
- If the routing keys don't reach our region, the most likely cause is that focus has moved to a different control after our `open()`, and NVDA's `_doNewObject` cleared `mainBuffer.regions`. The production primitive's design accounts for this by hooking focus events; the demo doesn't because there's no specific control to bind to.
- This is research code. It is NOT to be merged into the JJFlex production tree; per `TRACK-INSTRUCTIONS.md`, this branch contains only `docs/planning/track-c/` material.
