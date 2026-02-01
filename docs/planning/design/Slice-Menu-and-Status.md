# JJ Flex — Slice Menu & Plain-English Status

Last updated: 2026-01-31
Status: Proposal

## Problem
Legacy UI uses a compact “alphabet soup” status box combining:
- active slice indicators
- TX/RX state
- VOX / break-in flags
- slice enabled/disabled
This is hard to interpret without repeatedly checking a help file.

## Goal
In Modern mode, replace this with:
- Slice-centric menus and commands
- Plain-English status (spoken + dialog)

## Design
### Slice Menu (Modern)
Primary home for slice operations:
- Mode (CW/SSB/AM/FM/DIGI)
- TX slice selection / TX enable / MOX/PTT
- Pan / per-slice audio balance
- AGC and other receiver settings
- Jump to filter actions (without mixing concepts)

### Filter Menu (slice-scoped)
- Narrow / Widen
- Adjust low/high edges
- Presets and reset

### Status
Two features:
1. **Speak Status** (brief)
   Example: “Slice 1 active. 14.074 USB. Filter 2.8 kHz. TX on slice 1. VOX off.”
2. **Status Dialog** (full, structured)
   Groups:
   - Slice identity (name/number, freq, mode)
   - TX state (TX slice, power, MOX/PTT)
   - RX state (AGC, filter)
   - Audio (pan/levels)
   - Flags (VOX, break-in, lock, etc.)

## Speech Guidelines
- Use Tolk for short confirmations and on-demand summaries
- Avoid continuous verbose speech; throttle repeated changes (tuning/filter scrubs)

## Acceptance Criteria
- Modern mode is operable without legacy status box.
- Speak Status is concise and non-spammy.
- Status dialog is fully keyboard and screen-reader accessible.
