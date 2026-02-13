# JJ Flex — Multi-Slice Decode & Auto Snap Architecture

## Overview

This document defines a model for:

- Multi-slice simultaneous decoding (CW + digital modes)
- A stacked decode feed (combined view across slices)
- Keyboard-first navigation (signals and decode lines)
- Configurable Auto Slice Snap behaviors (focus/filter assistance)
- Accessibility-first operation (semantic layer separate from visuals)

Key principles:

- **Accessibility is semantic, not visual.**
- **Automation must be operator-controlled and reversible.**
- **Never transmit automatically.**


## 1. Multi-Slice Decode Model

### 1.1 Slice data

Each slice maintains:

- Audio stream reference
- Mode (CW, FT8/FT4, RTTY/PSK, etc.)
- Frequency
- Filter width
- Decoder instance
- Output buffer
- Signal metadata (SNR, confidence, strength)

### 1.2 Decoder isolation

- Each slice runs its decoder off the UI thread.
- Decoders publish structured events.

Example event shape:

```text
DecodeEvent
{
  SliceId
  Frequency
  Mode
  TimestampUtc
  DecodedText
  Confidence
  Tags[]
}
```


## 2. Decode Aggregator

A Decode Aggregator:

- Subscribes to all slice decoder outputs
- Tags and normalizes events by slice/mode
- Maintains feeds:
  - CW feed
  - Digital feed
  - All (stacked) feed
- Applies optional coalescing/throttling for speech


## 3. UI Structure (Tabbed)

Tabs:

- **CW**
- **Digital**
- **All** (stacked across everything)

Each tab contains two primary panes:

1. **Signals list**
   - One row per active signal/slice (or per decode stream)
   - Summary fields: slice, frequency, mode, strength/SNR, last-heard

2. **Decode lines list**
   - For the selected signal (CW/Digital tabs), or stacked feed (All tab)
   - One row per decoded line/chunk, tagged with slice

Keyboard model:

- Up/Down: navigate signals or lines
- Enter: activate/focus behavior (see Snap Policy)
- F6: toggle focus between signals list and decode lines list
- Ctrl+Tab / Ctrl+Shift+Tab: switch tabs


## 4. Accessibility Model

### 4.1 Speech strategy

- **Tolk**: high-reliability speech for confirmations, errors, snap results.
- **UIA live region (optional)**: passive status updates (throttled).

Avoid “character-by-character” speaking for live decode. Use chunking (words/lines).

### 4.2 Speech modes (user-configurable)

- Off
- Active slice only
- Active + background (prefixed: “Slice B: …”)
- Events only (CQ / MyCall / Watchlist)


## 5. Auto Slice Snap (Policy Engine)

Auto Snap is a policy, not a single feature:

**Trigger → Eligibility → Action Set → Confirmation**

Triggers may include:

- Enter on a decode line
- Enter on a signal
- Detect CQ
- Detect my callsign
- Watchlist match
- SNR threshold crossed

Eligibility rules (examples):

- Never snap during TX
- Never snap during manual tuning
- Never modify pinned slices
- Require minimum confidence

Snap levels (summary):

- **0: Highlight only**
- **1: Focus only**
- **2: Focus + Filter + Zero-beat**
- **3: Full assist (optional panadapter/waterfall follow)**
- **4: Contest mode (aggressive targeting, still no auto TX)**


## 6. Safety Constraints

- No auto transmit (ever)
- No focus changes during TX
- Pin/unpin slice (pin blocks snap actions)
- Undo last snap (one key)
- Optional confirmation for large retune deltas


## 7. QSOs Filtering + Paging (not follow-tail)

Use paging for large filtered result sets:

- Filter returns N results (example: 73)
- Initially load 20
- When user hits DownArrow at the last loaded row, load the next page (10–20)
- Announce once per load: “Loaded 10 more. Showing 30 of 73.”

Do not steal focus; let navigation proceed naturally.


## 8. Visual Layer Independence

The visual UI can be as “dashboardy” as desired (ticker, animations, highlights),
while the accessible semantic structure remains stable and navigable.

This enables a “CNBC on steroids” look without sacrificing screen reader usability.


## 9. SmartSDR comparison (high level)

SmartSDR supports multiple slices and multiple waterfalls visually.
This model proposes a structured stacked decode aggregator plus a configurable snap policy engine.
Verify against the current SmartSDR version for any newly added features.
