---
type: design pull doc — TS-590-line menu read/write + user "favorites" UX surface
review needed: read, annotate with `**** ` per for-noel protocol; UX direction needs your input before implementation track opens
priority: high — influences IRadioBackend interface design from day one (multi-radio architecture predates this feature)
draft author: Claude
date: 2026-05-03
---

# TS-590-line menu read/write + favorites — pull doc

## Why this doc exists

You surfaced this in the Hamlib spike review (line 23 annotation):

> *"Mark has a 590SG, others have either a t90 or a 590s, but all should be supported by hamlib. We may need to add extra support for menus from the 590 line. The ability to change menu items on each unit is important, also, according to users, add a 'favorites' to allow users to save menu items as favorites so they can see menus that they would like to change, the 'greatest hits' as it were."*

This is significant feature scope that **affects the IRadioBackend interface design from day one** — menu read/write needs to be a first-class operation, not bolted on after the multi-radio architecture is locked. The favorites pattern is generalizable to any radio with a menu surface (Icom, Yaesu, AnyTone, etc.) — design once, apply per-backend.

This doc inventories the design space so we can lock the IRadioBackend contract and the JJF UX before the Hamlib track opens.

## Inline-context: relevant memory

`project_kenwood_590g_commitment.md` (updated 2026-05-02) captures the feature scope:

> Per Noel's annotation on the Hamlib spike — the TS-590 line (and Kenwood radios more broadly) have rich EX-menu surfaces. Mark and other testers want to:
>
> 1. Read and change EX-menu items from JJFlex — not just CAT-controllable parameters but the full menu hierarchy
> 2. Save menu items as user "favorites" — a curated subset of menus the user actually adjusts regularly (band-specific filter widths, AGC time constants, USB audio levels, etc.)
> 3. Surface the favorites prominently — so users don't have to navigate the full 80+-item EX menu every time they want to tweak the few items they actually care about

`project_jj_radio_folding.md` (ACK 2026-05-02) establishes that menu read/write was a first-class JJ Radio feature that JJFlex inherits. JJ Radio users coming over expect this.

`project_ts590_tester_pool.md` (created 2026-05-02): Mark + several BHN net regulars commit to TS-590 / TS-590SG testing. **Material tester capacity** for validating this feature.

## TS-590 menu surface inline reference

Brief inline summary so you don't have to context-switch into Kenwood docs:

The TS-590 line uses Kenwood's EX-menu paradigm — a numbered configuration menu accessed by holding MENU. The TS-590S has approximately 70 EX items; the TS-590SG has approximately 80 (added items reflect the SG's USB audio path, improved DSP, and additional filter modes). Items cover:

- **Display + interaction:** brightness, key beep volume, voice annunciation
- **TX behavior:** power presets, mic gain ranges, ALC, processor compression
- **DSP filter shapes:** filter widths per mode, AGC time constants, IF shift behavior
- **Memory management:** memory channel scan list, scan resume behavior
- **USB audio:** input/output levels, mode (USB-D, USB-PSK, etc.) — SG only
- **CAT/computer interface:** baud rates, command response delay, transmit-via-PTT
- **Data modes:** packet/digital mode behavior, USB-D filter shapes
- **Misc:** auto-power-off, antenna selection rules, key beep style

Each EX item has:
- A numeric ID (EX-00 through EX-7x or 8x)
- A short name (Kenwood docs: a 3-12 character abbreviation)
- A descriptive name (longer phrase from the manual)
- A value type (integer with range, enumerated set, on/off)
- A current value (radio-side persistent state)

Hamlib's TS-590 driver covers most CAT-controllable parameters via `RIG_LEVEL_*` and `RIG_FUNC_*` operations (verified at `C:\dev\Hamlib\rigs\kenwood\ts590.c`), but the EX menu specifically is a separate surface — accessed via Kenwood's `EX` CAT command (`EXxxx vv` to set EX item xxx to value vv, `EXxxx` to query). Hamlib's standard operations don't cover the full EX surface; we'll need pass-through `rig_send_raw` / `rig_get_raw` for items not exposed via standard levels.

## Two pillar features

### Pillar 1 — Full menu access ("Browse all menu items")

Operator can browse all 70-80 EX items in a list, see current values, and change any of them.

**UX sketch:**
- Settings → Radios → Kenwood TS-590[SG] → Menu Items
- A scrollable list. Each row shows: short name, descriptive name, current value.
- User navigates with arrow keys. Enter on a row opens an edit control appropriate to the item's value type (integer spinner, dropdown for enumerated values, on/off toggle).
- Confirming a value change: send the EX command, wait for confirmation, update the displayed value.
- Failed commands surface clearly (Kenwood's command response is acknowledge-only; failures are usually "command not understood" → log + show "Radio rejected the change").

**Accessibility:**
- Each row is a fully-readable element with name + descriptive name + current value spoken on focus.
- `AccessibleName` includes context: "EX 32, Filter A width USB, currently 2400 hertz."
- Edit controls use standard JJF patterns (spinners with announce-on-change, dropdowns with NVDA-driven option list, etc.).

**Implementation scope:**
- Static metadata catalog: 80+ items × {short name, descriptive name, value type, valid range}. Hand-curated from Kenwood manuals, lives in JJF source as a JSON/CSV file. Per-radio-model file (TS-590S has ~70 items, TS-590SG has ~80).
- Live state: query each item via Hamlib's `rig_get_raw("EX032;")` style on demand or at startup-cache-then-invalidate.
- Write: `rig_send_raw("EX032 03;")` style.

### Pillar 2 — Favorites ("Greatest Hits")

Operator marks a small handful of EX items as "favorites" — the menus they actually adjust regularly. Favorites get prominent surface.

**UX sketch:**
- In the full menu list (Pillar 1), each row has a "Favorite" toggle (e.g., F or Space key, or a context-menu action).
- A separate "Menu Favorites" view shows only favorited items, sorted by the operator's chosen order.
- The Favorites view is reachable via a top-level keystroke or menu item — much faster path than navigating into Settings → Radios → Menu.
- Each operator's favorites are stored per-radio (per `project_per_radio_config_serial_keyed.md`) so different operators on the same radio see their own favorites if they have separate config profiles.

**Why this is the right pattern:**
- **80+ items is too many to scan regularly.** An operator who tweaks AGC time constants per band doesn't want to navigate 30 rows past unrelated items each time.
- **Favorites externalize the operator's actual workflow.** Different operators have different "greatest hits" lists. The radio doesn't know; JJF can.
- **Generalizes across radios.** The pattern applies to any radio with a menu surface — Icom IC-7610 (similar set-and-forget config menus), Yaesu FTDX10, etc. Build the UX once, parameterize by radio.

### Bonus: workflow integration

Favorites can also feed Customize Home (`project_customize_home_vision.md`). A power user who lives in their EX-menu favorites might want one or two of them (e.g., AGC time constant) on their Home layout for instant access. That's Sprint 30+ scope — not blocking — but worth noting that the favorites system is the substrate.

## IRadioBackend interface implications

The multi-radio architecture (per worktree research at `C:\dev\jjflex-multi-radio\docs\research\iradio-backend-design.md`) needs menu operations as first-class. Sketch:

```
interface IRadioBackend {
    // ... existing operations: tune, ptt, slice queries, etc. ...

    // Menu surface
    bool SupportsMenu { get; }
    IList<MenuItemDescriptor> GetMenuCatalog();  // Static — from metadata file
    Task<MenuItemValue> GetMenuItemAsync(string itemId);
    Task SetMenuItemAsync(string itemId, MenuItemValue value);
    event EventHandler<MenuItemChangedEventArgs> MenuItemChanged;  // For live-tracking
}
```

`MenuItemDescriptor` carries the short name, descriptive name, value type, valid range. `MenuItemValue` is a discriminated union (integer / enum / boolean / string).

**Backend coverage:**
- `FlexLibBackend.SupportsMenu = false` initially. (Flex radios do have configuration surfaces but they're already exposed via FlexLib's structured properties; menu-ifying them is a future architectural question.)
- `HamlibBackend.SupportsMenu = true` for radios with EX-menu metadata files — TS-590, TS-590SG, IC-7610, etc. Backend implementation reads the static metadata + invokes raw commands via Hamlib.

**Why this shape:**
- **Static metadata + dynamic state.** Catalog comes from JSON; values come from radio. This separation lets the UX render before the radio responds (placeholder + live-update pattern).
- **Async by default.** Radio response time is variable; UI shouldn't block.
- **Event-driven update.** External changes (operator turns a radio knob, another connected app changes a value) propagate to JFF's UI without polling.

## Implementation phasing

**Phase 1: Metadata catalog**
- Build `radios/kenwood-ts590s.json` and `radios/kenwood-ts590sg.json`. Hand-curate 70-80 items each from Kenwood manuals. ~150 LOC of JSON per file.
- Verify against actual radios via Mark and BHN testers. Iterate.
- Schema: validated by JSON schema file in JJF source.

**Phase 2: Hamlib raw-command pass-through**
- HamlibBackend implements `GetMenuItemAsync` / `SetMenuItemAsync` via `rig_send_raw` / `rig_get_raw`. SWIG-bound (per `project_hamlib_swig_first_decision.md`) so we have access to all raw operations.
- Cache layer to avoid hammering the radio for every UI render.

**Phase 3: Browse-all UX**
- WPF list (pillar 1). Reuses the accessibility patterns from existing JJF dialogs.
- ~300-500 LOC across XAML + view model.

**Phase 4: Favorites UX**
- Per-radio config (serial-keyed) stores favorites list. Add favorite-toggle to Pillar 1 list.
- Standalone "Favorites" view (~150 LOC).
- Top-level keystroke wired into KeyCommands.vb with proper short action label per `project_short_action_labels_vocabulary.md`.

**Phase 5: Tester validation pass**
- Mark + BHN testers exercise the full surface. Catalog corrections, UX feedback, edge cases.

## Open questions for your read

1. **Favorites order — user-specified or fixed?** I lean user-specified (drag-to-reorder or up/down keys). But "fixed by frequency-of-use" auto-sorting is also legitimate. Your call.
2. **Per-band favorites?** Some operators might want different favorites for HF vs VHF. Adds complexity. My lean: v1 has one favorites list; per-band is Sprint 30+ if it surfaces.
3. **Cross-radio favorites portability?** If an operator has a TS-590S and a TS-590SG, do their favorites carry over? Most items overlap but EX numbering differs. My lean: per-radio-serial favorites are independent (simpler); add a "copy from another radio" action if testers ask for it.
4. **Where does this live in the menu hierarchy?** I sketched Settings → Radios → Kenwood TS-590[SG] → Menu Items. But JJF has a "JJ Flexible Home" model — should there be a top-level Home keybinding for "open menu favorites"? My lean: yes, since favorites is the high-frequency path.
5. **Phase 1 timing.** Should I start the metadata catalog now (build-now-ship-later) so it's ready when the Hamlib track opens? Or wait until the IRadioBackend interface is locked?

## Cross-references

- `project_kenwood_590g_commitment.md` — Mark's commitment + feature scope
- `project_ts590_tester_pool.md` — tester pool for validation
- `project_jj_radio_folding.md` — menu read/write inheritance from JJ Radio
- `project_hamlib_swig_first_decision.md` — full Hamlib API coverage justifies SWIG
- `project_per_radio_config_serial_keyed.md` — favorites storage location
- `project_short_action_labels_vocabulary.md` — labels for Favorites top-level keystroke
- `project_customize_home_vision.md` — favorites as substrate for Home
- `project_multi_radio_capability_discovery.md` — why menu support is per-radio (capability flag)
- Worktree: `C:\dev\jjflex-multi-radio\docs\research\iradio-backend-design.md` — interface design context
- Hamlib source: `C:\dev\Hamlib\rigs\kenwood\ts590.c` — driver entry points
