# Context-Aware Hotkeys Design

**Status:** Design proposal (Sprint 4+)
**Author:** Jim / Claude
**Date:** 2026-02-06

---

## The Problem

Today every hotkey must be globally unique. A key like Alt+C can only do one thing regardless of what the operator is doing. This creates two painful trade-offs:

1. **Logging commands hog prime keys.** Alt+C (Log Call), Alt+S (Log State), Alt+G (Log Grid), Alt+N (Log Handle), etc. use up the best single-modifier combos even when the operator isn't logging.
2. **General commands get pushed to awkward chords.** Ctrl+Shift+R for Reverse Beacon, Ctrl+Shift+D for AR Cluster, Ctrl+Shift+T for Log Stats — hard to remember, hard to hit.

With the introduction of Logging Mode (Sprint 4), we can fix this. **Keys mean different things in different modes**, just like a well-designed application should work.

---

## The Idea

Each hotkey binding gets a **scope** — which mode(s) it's active in:

| Scope | Meaning |
|-------|---------|
| `Global` | Active in all modes (Classic, Modern, Logging) |
| `Radio` | Active in Classic and Modern only (radio operation) |
| `Logging` | Active in Logging Mode only |

This lets us **reuse the same key** for different functions depending on context:

### Example: What Alt+C Could Mean

| Mode | Alt+C does... | Today it does... |
|------|---------------|-----------------|
| Classic/Modern | CW Zero Beat (currently Ctrl+C) | Log Call (useless without logging) |
| Logging | Log Call | Log Call |

### Example: What Alt+S Could Mean

| Mode | Alt+S does... | Today it does... |
|------|---------------|-----------------|
| Classic/Modern | Start Scan (currently Ctrl+S) | Log State (useless without logging) |
| Logging | Log State | Log State |

---

## Proposed Default Key Map

### Global Keys (work everywhere)

| Key | Command | Group | Notes |
|-----|---------|-------|-------|
| F1 | Show Help | help | |
| F2 | Show Frequency | routingScan | |
| F3 | Show Received | routing | |
| F4 | Show Send | routing | |
| Ctrl+F4 | Show Send Direct | routing | |
| F12 | Stop CW | general | Safety — always available |
| Ctrl+1..9 | CW Message 1-9 | cwMessage | Moved from F5-F11 |
| Ctrl+Shift+L | Toggle Logging Mode | — | Hard-wired in ProcessCmdKey |
| Ctrl+Shift+M | Toggle Classic/Modern | — | Hard-wired in ProcessCmdKey |

### Radio-Only Keys (Classic + Modern)

| Key | Command | Today's Key | Freed From |
|-----|---------|-------------|------------|
| Alt+C | CW Zero Beat | Ctrl+C | Log Call moved to Logging scope |
| Alt+S | Start Scan | Ctrl+S | Log State moved to Logging scope |
| Alt+N | New Entry → enters Logging Mode | Ctrl+N | Log Handle moved to Logging scope |
| Alt+R | Reverse Beacon | Ctrl+Shift+R | Log Rig moved to Logging scope |
| Alt+D | AR Cluster (DX) | Ctrl+Shift+D | Log DateTime moved to Logging scope |
| Alt+G | Gather Debug | (none) | Log Grid moved to Logging scope |
| Alt+E | Export Setup | (none) | Log Comments moved to Logging scope |
| Alt+PgUp | Audio Gain Up | Alt+PgUp | (unchanged, now Radio-scoped) |
| Alt+PgDn | Audio Gain Down | Alt+PgDn | (unchanged, now Radio-scoped) |
| Alt+Shift+PgUp | Headphones Up | Alt+Shift+PgUp | (unchanged, now Radio-scoped) |
| Alt+Shift+PgDn | Headphones Down | Alt+Shift+PgDn | (unchanged, now Radio-scoped) |
| Shift+PgUp | Lineout Up | Shift+PgUp | (unchanged, now Radio-scoped) |
| Shift+PgDn | Lineout Down | Shift+PgDn | (unchanged, now Radio-scoped) |
| Ctrl+F | Set Frequency | Ctrl+F | (unchanged) |
| Ctrl+M | Show Memory | Ctrl+M | (unchanged) |
| Ctrl+P | Do Panning | Ctrl+P | (unchanged) |
| Ctrl+Z | Stop Scan | Ctrl+Z | (unchanged) |
| Shift+F2 | Resume Scan | Shift+F2 | (unchanged) |
| Ctrl+Shift+U | Saved Scan | Ctrl+Shift+U | (unchanged) |
| Ctrl+Shift+C | Clear RIT | Ctrl+Shift+C | (unchanged) |
| Ctrl+F9 | Toggle1 | Ctrl+F9 | (unchanged) |
| F5-F11 | (available for radio shortcuts) | CW Messages | Freed by moving CW to Ctrl+number |

### Logging-Only Keys (Logging Mode)

**Alt+letter keys — field entry:**

| Key | Command | Today's Key | Notes |
|-----|---------|-------------|-------|
| Alt+C | Log Call | Alt+C | Same key, now scoped |
| Alt+D | Log Date/Time | Alt+D | Same key, now scoped |
| Alt+S | Log State | Alt+S | Same key, now scoped |
| Alt+G | Log Grid | Alt+G | Same key, now scoped |
| Alt+N | Log Handle (Name) | Alt+N | Same key, now scoped |
| Alt+R | Log Rig | Alt+R | Same key, now scoped |
| Alt+E | Log Comments | Alt+E | Same key, now scoped |
| Alt+Q | Log QTH | Alt+Q | (unchanged) |
| Alt+M | Log My RST | Alt+M | (unchanged) |
| Alt+L | Station Lookup | Alt+L | (unchanged) |

**Ctrl+letter keys — log operations:**

| Key | Command | Today's Key | Notes |
|-----|---------|-------------|-------|
| Ctrl+H | Log His RST | Ctrl+H | (unchanged) |
| Ctrl+A | Log Antenna | Ctrl+A | (unchanged) |
| Ctrl+W | Write (Finalize) Entry | Ctrl+W | (unchanged) |
| Ctrl+N | New Log Entry | Ctrl+N | (unchanged) |
| Ctrl+S | Search Log | Ctrl+Shift+F | Simpler in context! |
| Ctrl+T | Log Statistics | Ctrl+Shift+T | Simpler in context! |
| Ctrl+F | Log File Name | (none) | Available since Set Frequency is Radio-only |

**F-keys — navigation (single press, no modifier):**

| Key | Command | Notes |
|-----|---------|-------|
| F5 | First Entry | |
| F6 | Previous Entry | |
| F7 | Next Entry | |
| F8 | Last Entry | |
| F9 | New Entry | Quick start a fresh QSO |
| F10 | Write (Finalize) Entry | Also available as Ctrl+W |
| F11 | Search Log | Also available as Ctrl+S |

**PgUp/PgDn — log scrolling (freed from audio in Logging Mode):**

| Key | Command | Notes |
|-----|---------|-------|
| PgUp | Scroll QSO grid up | Plain PgUp, no modifier needed |
| PgDn | Scroll QSO grid down | Plain PgDn, no modifier needed |
| Shift+PgUp | Page up (large jump) | |
| Shift+PgDn | Page down (large jump) | |

---

## What This Buys Us

### Keys Freed for Radio Use
With logging commands scoped to Logging Mode, **7 prime Alt+letter keys** become available for radio operations in Classic/Modern:

| Key | Was (logging) | Becomes (radio) |
|-----|---------------|-----------------|
| Alt+C | Log Call | CW Zero Beat |
| Alt+D | Log DateTime | DX Cluster |
| Alt+S | Log State | Start Scan |
| Alt+N | Log Handle | Quick-enter Logging Mode |
| Alt+R | Log Rig | Reverse Beacon |
| Alt+G | Log Grid | Gather Debug |
| Alt+E | Log Comments | Export Setup |

### Simpler Logging Shortcuts
Since radio commands aren't active in Logging Mode, we can use simpler chords:

| Was | Becomes | Command |
|-----|---------|---------|
| Ctrl+Shift+F | Ctrl+S | Search Log |
| Ctrl+Shift+T | Ctrl+T | Log Statistics |
| (none) | Ctrl+F | Log File Name |

### Mnemonic Consistency
Keys become **easier to remember** because they relate to what you're doing:

- In radio mode: Alt+S = **S**can, Alt+R = **R**everse Beacon, Alt+D = **D**X Cluster
- In logging mode: Alt+S = **S**tate, Alt+R = **R**ig, Alt+D = **D**ate/Time

---

## Implementation Plan

### Data Model Changes (KeyCommands.vb)

Add a `Scope` enum and field to `KeyDefType`:

```vb
Public Enum KeyScope
    Global    ' Active in all modes
    Radio     ' Classic + Modern only
    Logging   ' Logging Mode only
End Enum
```

Each `KeyDefType` gains a `.Scope` property. The default key table stores scope alongside the key and command.

### Key Resolution Changes

The `KeyDictionary` lookup (used at keypress time) becomes mode-aware:

```
1. Look up key in dictionary
2. If found, check command's scope vs ActiveUIMode:
   - Global → always fire
   - Radio → fire if Classic or Modern
   - Logging → fire if Logging
3. If scope doesn't match, check for a second binding with the same key
   but different scope → fire that one instead
```

This means the dictionary needs to support **multiple commands per key** (one per scope). Change from `Dictionary(Of Keys, keyTbl)` to `Dictionary(Of Keys, List(Of keyTbl))` or a lookup keyed on `(Keys, KeyScope)`.

### Hotkey Editor Changes

The key customization dialog (ChangeKeys form) needs:

1. **Scope column** — show which mode(s) each binding applies to
2. **Conflict detection** — only flag conflicts within the same scope (Alt+C in Radio and Alt+C in Logging are fine; two Alt+C in Radio is a conflict)
3. **Scope selector** — when assigning a key, pick Global/Radio/Logging
4. **Filter/group by scope** — so the list isn't overwhelming

### Menu Filtering (Already Started)

The `SetupOperationsMenu` filter in Form1.vb already hides logging commands in Classic/Modern. This extends naturally: commands with `Scope = Radio` hidden in Logging Mode, `Scope = Logging` hidden in Classic/Modern.

### Saved Key Files

Operator key files (`.key` files) need to include scope. Backward compatibility: keys loaded without a scope field default to `Global` (preserves existing behavior for operators who haven't customized).

---

## Migration / Backward Compatibility

1. **Existing operators** keep their current key bindings — all imported as `Global` scope
2. **New operators** get the new context-aware defaults
3. **"Reset to defaults"** offers the context-aware map
4. **No breaking changes** — Global scope works exactly like today's system

---

## Complexity vs. Payoff

**More complex:** The hotkey editor needs a scope column and scope-aware conflict detection. The key dictionary needs to support multiple bindings per physical key.

**Much simpler for operators:** Instead of memorizing 40+ unique key combos across one flat namespace, operators learn ~15 radio keys and ~15 logging keys, with natural mnemonics in each context. The mode indicator (already on screen) tells them which keys are active.

This is the same pattern used by every modern IDE (Vim modes, VS Code contexts, Emacs major modes). It works because **the operator already knows what they're doing** — they know if they're tuning the radio or logging a QSO. The keys should match that mental context.

---

## Resolved Questions

### 1. CW/Voice Message Keys → Ctrl+Number (Global), F-keys freed

**Decision:** Move CW and voice messages from F5-F11 to **Ctrl+1 through Ctrl+9** (CW) and a parallel scheme for voice memories. These are Global — you need to send CW/voice exchanges while logging. This **frees up all F-keys** for mode-specific shortcuts.

**Rationale:** F-keys are the most ergonomic single-press keys on the keyboard. Locking 7 of them to CW messages (which many operators don't use) wastes prime real estate. Ctrl+number is still fast, memorable ("message 1, message 2..."), and consistent across CW/voice.

**What F-keys become available for:**

| Key | Radio scope | Logging scope |
|-----|-------------|---------------|
| F5 | (available) | First Entry |
| F6 | (available) | Previous Entry |
| F7 | (available) | Next Entry |
| F8 | (available) | Last Entry |
| F9 | (available) | New Entry |
| F10 | (available) | Write (Finalize) Entry |
| F11 | (available) | Search Log |

Radio-scope F-key assignments TBD — candidates: quick band change, quick mode change, quick filter select, spot to cluster, etc.

### 2. Audio Keys → Radio-scoped

**Decision:** Audio gain keys (Alt+PgUp/PgDn, Alt+Shift+PgUp/PgDn, Shift+PgUp/PgDn) are **Radio-scoped**. In Logging Mode, PgUp/PgDn (with various modifiers) become available for log navigation — scrolling through entries, paging the QSO grid, etc.

**Rationale:** Operators rarely adjust audio mid-QSO. If they need to, Ctrl+Shift+L flips back to radio mode instantly. The PgUp/PgDn keys are natural for scrolling/navigation, which is exactly what Logging Mode needs.

### 3. No Global Checkbox — Per-Key Scope Only

**Decision:** No master "flat keys" toggle. Operators set scope **per key** in the hotkey editor. If someone wants a key to work everywhere, they set it to Global.

**Rationale:** A master checkbox would require cross-checking all scopes for conflicts when toggled, adding complexity for an edge case. Per-key Global scope gives the same result with no special code path. The hotkey editor's conflict detection already handles it — if you set Alt+C to Global, it'll warn you if there's already a Radio-scoped or Logging-scoped Alt+C.

**Conflict rules for the editor:**
- Global key conflicts with any other binding on the same physical key (any scope)
- Radio key only conflicts with other Radio or Global bindings on the same key
- Logging key only conflicts with other Logging or Global bindings on the same key
- Radio + Logging on the same key = **no conflict** (they never run at the same time)
