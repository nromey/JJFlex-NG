# JJ Flex — Auto Slice Snap Policy

## Purpose

Auto Slice Snap helps the operator quickly “lock onto” a decoded signal
(CW or digital) while still monitoring other slices. The system must
remain predictable, reversible, and never transmit automatically.

## Core model

**Trigger → Eligibility → Action Set → Confirmation**

- Trigger: something happens (user action or decode event).
- Eligibility: determine if snap is allowed right now.
- Action Set: apply a defined level of assist.
- Confirmation: announce what changed (Tolk preferred).

## Triggers

User-driven:
- Activate a decode line (Enter on DecodeLinesList)
- Activate a signal (Enter on SignalsList)

Event-driven (optional / configurable):
- CQ detected
- My callsign detected
- Watchlist match (DXCC/callsign/prefix/etc.)
- SNR/confidence threshold crossed
- Spot/cluster match

## Eligibility rules (recommended defaults)

Block snap if:
- TX is active (never change focus during TX)
- Manual tuning is in progress
- Target slice is pinned
- Confidence is below threshold

Additional optional rules:
- Require confirmation if retune delta > X Hz
- Rate limit event-driven snaps (cooldown)

## Snap Levels

### Level 0 — Highlight Only
Actions:
- Visual highlight candidate slice/line
- Optional speech: “Candidate: Slice B …”

### Level 1 — Focus Only
Actions:
- Set the selected slice as primary
- Optional speech: “Slice B active …”

### Level 2 — Focus + Filter + Zero-beat (recommended MVP)
Actions:
- Set slice as primary
- Narrow filter to mode default (e.g., CW 250–500 Hz)
- Center/zero-beat to the preferred sidetone frequency
- Speech summary: “Slice B active. Filter 250 Hz. Zero-beat set.”

### Level 3 — Full Assist
Actions:
- Level 2 actions
- Optional panadapter follows focus
- Optional waterfall follows focus
- Mode-specific assist tweaks (as needed)

### Level 4 — Contest Mode
Actions:
- Prioritize and queue candidates based on patterns
- Still requires explicit user action to focus or arm TX
- Speech defaults to events-only

## Non-negotiable safety constraints

- **Never auto transmit**
- **Never change focus during TX**
- **Pinned slices are not modified**
- Provide **Undo last snap** (single hotkey)
- Provide **Snap enable/disable** toggle

## Operator controls (suggested)

- Toggle snap: Ctrl+Alt+S
- Pin/unpin slice: Ctrl+Alt+P
- Undo last snap: Ctrl+Alt+Z

## Announcements (speech)

Prefer short, consistent confirmations:

- “Slice B active. 14.030 CW. Filter 250 Hz.”
- “Pinned Slice C. Snap will ignore it.”
- “Loaded 10 more. Showing 30 of 73.” (paging)

Avoid speaking raw character streams. Use chunking (words/lines) with throttling.
