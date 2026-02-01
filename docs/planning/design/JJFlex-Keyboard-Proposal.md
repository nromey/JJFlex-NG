# JJ Flex — Keyboard Shortcut Proposal

Last updated: 2026-01-31
Status: Proposal

## Goals
- Keep the “SDR cockpit” fast and learnable
- Avoid a 30-page key reference by building **in-app discovery**
- Support customization without chaos

## Core Rules
1. **No shortcut longer than 2 steps.**
   - Single-step for high-frequency operations
   - Leader + key for grouped/long-tail operations
2. **Discoverability is built-in**
   - Command Finder (F12) answers “what key does that?”
   - Group help (`H`/`?`) lists assigned keys only
3. **Explicit recording mode for remaps**
   - No accidental overwrites when pressing Tab/Enter/etc.
4. **Profiles**
   - Default / Classic / Contest / Minimal
   - User overrides stored separately

## Recommended Defaults
### Command Finder
- **F12**: open Command Finder
  - type keyword (“filter”, “tx”, “peak”, “slice”)
  - results speak command + shortcut
  - optional actions: Execute / Rebind / Help

### Leader key (optional, recommended for scale)
- **Ctrl+J**: JJ leader (Modern mode)
- After leader:
  - `?` or `H` speaks available next keys (assigned only)
  - `Esc` cancels
  - timeout cancels (quiet or tiny earcon)

### High-frequency single-step examples (illustrative)
- Tune up/down: arrow keys or configurable
- Filter narrow/widen: `[` and `]` (or configurable)
- Next/prev slice: configurable (e.g., `Alt+Left/Right`)
- Next/prev peak: `N` / `Shift+N` (example)
- Speak status: configurable (e.g., `Ctrl+J, S` or single key)

## Conflict Detection & Resolution
- Detect collisions within:
  - Global
  - App-focused
  - Mode-specific layers (e.g., Logger layer)
- Provide resolver UI:
  - show conflicts
  - suggest alternatives (unused combos)
  - allow “unassigned” where appropriate

## Global Hotkeys (future)
- Allow limited global hotkeys (opt-in) so users can trigger core actions from any app
- Prefer global leader + key to reduce collisions
- Provide an always-available “Bring JJ Flex to front” hotkey
