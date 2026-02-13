# JJ Flex Multi-Slice Decode and Auto Snap — Dev/Agent Model

## Goals

- Decode CW + Digital simultaneously per slice.
- Provide unified stacked decode feed (tagged by slice).
- Allow keyboard-first navigation across signals and decode lines.
- Implement configurable auto snap (focus/filter assist) with safety constraints.
- Preserve accessibility as a semantic/UIA layer separate from visuals.

## Core data types

### Slice
- sliceId: string
- mode: enum (CW, FT8, ...)
- frequencyHz: number
- filterWidthHz: number
- isPinned: boolean
- isPrimary: boolean
- snr: number
- signalStrength: number
- decoderEnabled: boolean
- lastHeardSummary: string

### DecodeEvent
- eventId: string
- sliceId: string
- mode: enum
- frequencyHz: number
- timestampUtc: string
- decodedText: string
- confidence: number
- tags: string[]

## Components

### Per-slice decoder
- Runs off UI thread.
- Emits DecodeEvent.
- Must not block UI or speech.

### DecodeAggregator
- Subscribes to all decoders.
- Normalizes/tags.
- Maintains feeds: cwFeed, digitalFeed, allFeed.
- Optional throttling/coalescing for announcements.

### UI (WPF)
- TabControl: CW, Digital, All.
- Two panes per tab:
  - SignalsList (per-slice summary)
  - DecodeLinesList (selected signal lines OR stacked feed)

Keyboard:
- Up/Down: move selection
- Enter: activate selection (may invoke snap)
- F6: toggle focus between panes
- Ctrl+Tab / Ctrl+Shift+Tab: change tab

## Accessibility / speech

- Use Tolk for confirmations and critical events.
- Use UIA live region only for passive updates (throttled).
- Avoid speaking character-by-character decode.

Speech modes:
- Off
- Active slice only
- Active + background prefixed
- Events only (CQ / MYCALL / WATCHLIST)

## Auto Snap Policy Engine

Model:
Trigger -> Eligibility -> ActionSet -> Confirmation

Eligibility:
- if txActive: block
- if manualTuneInProgress: block
- if targetSlice.isPinned: block
- if decodeEvent.confidence < threshold: block

Snap levels:
- 0: highlight only
- 1: focus only
- 2: focus + filter + zero-beat
- 3: full assist (optional visual follow)
- 4: contest mode (aggressive targeting; no auto TX)

Safety:
- NO_AUTO_TX
- NO_FOCUS_CHANGE_DURING_TX
- PIN override
- UNDO last snap
- Confirm large retunes (optional)

## QSOs paging (filtered view)

- VisibleQsos: collection bound to grid.
- Load initial page (e.g., 20).
- On DownArrow at last row: fetch next page (10–20) and append.
- Announce once per load: “Loaded N more. Showing Y of Z.”
