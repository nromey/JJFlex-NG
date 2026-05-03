# Braille Display Verbosity Design

**Status:** Design phase. Original draft 2026-03-16 (Sprint 25 implementation start). **Reconciled 2026-05-02** with the verbosity-channel-split decision (`memory/project_verbosity_architecture_proposal.md`). The "verbosity" framing in the original doc is partially superseded — see "Verbosity Tie-in (UPDATED)" section below.

## Overview

Persistent braille status line showing real-time radio info. Unlike speech (sequential, transient), braille sits on the display and can be read anytime. User-configurable fields, display-size aware, priority-based packing.

## Display Ownership Model

NVDA gets priority. We fill the gaps.

- **Focus on main frequency field (home position):** We push meter status to braille via `Tolk.Braille()`
- **Focus anywhere else (menus, dialogs, fields):** NVDA owns the display, we stop pushing
- **Any focus change:** Immediately yield to NVDA
- **Return to home position:** Resume meter status after 2-3 second idle

## API

Already available in Tolk:
- `Tolk.Braille(string)` — push to braille display only (no speech)
- `Tolk.HasBraille()` — check if display connected
- `Tolk.Output(string)` — push to both speech and braille (not used for status line)

## Display Sizes

User configures cell count in Settings. Common sizes:
- 20 cells (compact)
- 32 cells
- 40 cells (Noel's Focus 40 / Mantis)
- 80 cells (full-size)

## Field Abbreviations (2-char prefix)

- SM = S-meter (e.g., SM7, SM9+20)
- SW = SWR (e.g., SW1.3)
- PW = Power in watts (e.g., PW50)
- AL = ALC (e.g., AL2)
- MC = Mic level
- VT = Voltage
- PA = PA temperature
- CM = Compression
- NR = Noise reduction on/off
- NB = Noise blanker on/off

## Priority Order (user configurable, sensible defaults)

1. Frequency (always shown, 6-8 cells) — `14.250`
2. Mode (always shown, 2-4 cells) — `USB`
3. S-meter (high priority, 3-5 cells) — `SM7`
4. SWR (high priority during TX/tune, 5-6 cells) — `SW1.3`
5. Power (TX relevant, 4-5 cells) — `PW50`
6. ALC (TX relevant, 3-4 cells) — `AL2`
7. DSP flags (2-3 cells each) — `NR`, `NB`
8. Slice letter (multislice, 2 cells) — `A`

## Layout Examples

### 20 cells (compact)
```
14.250 USB SM7 SW1.3
```

### 32 cells
```
14.250 USB SM7 SW1.3 PW50 AL2
```

### 40 cells
```
14.250 USB SM7 SW1.3 PW50 AL2 NR NB
```

### 80 cells (verbose — room for full words)
```
14.250 USB A SM7 SW1.3 PW50W AL2 NR on NB off Comp3
```

## Implementation

### Core Method

```csharp
string BuildBrailleStatus(int cellCount, List<BrailleField> enabledFields)
{
    // Start with frequency + mode (mandatory)
    // Fill remaining cells with fields in priority order
    // Stop when next field won't fit
    // Return padded/trimmed to cellCount
}
```

### Update Mechanism

- 1-second timer when braille display connected and focus is home
- Rebuilds status string, pushes via `Tolk.Braille()`
- Timer pauses when focus leaves home position
- Timer resumes when focus returns to home after idle delay

### Settings UI

New section in Settings dialog (or dedicated Braille tab):
- Display cell count (dropdown: 20, 32, 40, 80, or custom)
- Field checkboxes (which meters to show)
- Priority reorder (drag or up/down buttons)
- Preview of current layout at configured cell count

### Cursor Routing (future)

Tap a braille cell position to interact with that field:
- Tap frequency → start quick-type frequency entry
- Tap meter → speak full meter value
- Tap mode → cycle mode

This requires NVDA add-on or cursor routing callback — research needed.

## Verbosity Tie-in (UPDATED 2026-05-02 to reconcile with channel-split)

**Original framing (2026-03-16) is superseded.** The original suggested braille could have its own verbosity ladder ("Terse, Normal, Chatty for braille separately from speech"). The verbosity-channel-split decision (`memory/project_verbosity_architecture_proposal.md`) explicitly **rejected per-channel verbosity ladders** — keeping configuration surface small.

**Updated model:**

- **Braille is a CHANNEL** (on/off toggle), not a verbosity level. The toggle is `Ctrl+Shift+D` (per the channel-toggle key proposal) and persists in `audioConfig.xml`. Default off until braille support matures (per the locked default in the verbosity proposal).
- **The braille STATUS LINE shows persistent state** regardless of speech verbosity setting. The amount of detail in the status line is controlled by the user's **field-selection in Customize Home** (per `memory/project_customize_home_vision.md`), not by a verbosity slider.
- **Speech verbosity (renamed from just "Verbosity") applies only to the speech channel.** It does NOT affect braille rendering. A user at speech-Off-(critical-only) still gets full braille status if braille is on.
- **Smart-warn safeguard applies:** if a screen reader is detected AND user attempts to disable speech AND CW (per current state) AND braille is also off, JJ Flex warns *"You need at least one accessibility output channel..."* See verbosity proposal for full safeguard logic.

**Implication for the cell-count + field-selection UI:** the Settings UI for braille (originally proposed as "Display cell count + field checkboxes + priority reorder") fits naturally into the Customize Home framework. **Recommended consolidation:** the braille field-selection becomes a "Customize Braille" subsection of the Customize Home dialog (or a parallel dialog with the same patterns). Avoids inventing new UI patterns; reuses the Customize Home pattern that's already shipping in 4.2.1.

The original "1-second push timer with focus-aware start/stop" behavior stays exactly as designed — it's about WHEN braille gets pushed, not about HOW MUCH detail.

## Meter Speech Configuration (related)

Same Settings section configures which meters speak:
- On-demand (Ctrl+Alt+V): which meters included
- Periodic timer: which meters, interval
- ATU/tune complete: auto-speak SWR
- Verbosity level for meter speech

## Phase 1 (Sprint 25)

- BuildBrailleStatus method with priority packing
- Settings UI for cell count and field selection
- 1-second push timer with focus-aware start/stop
- Basic home position detection

## Phase 2 (future)

- Cursor routing
- Independent braille verbosity level
- Context-sensitive fields (TX meters during TX, RX meters during RX)
- Dot Pad integration (tactile graphics — separate design)
