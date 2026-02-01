# JJ Flex — Product Plan & Architecture

Last updated: 2026-01-31
Status: Living document

## Vision
JJ Flex is an accessible, keyboard-first SDR control application that enables blind and sighted operators to:
- Operate FlexRadio systems efficiently (local + SmartLink remote)
- Explore spectrum via **audio** (sonification) and **braille** (braillified waterfall/peaks)
- Use slice-centric workflows that are discoverable and fun (earcons, status callouts)

Long-term, JJ Flex becomes a platform for accessible SDR beyond Flex (e.g., RTL-SDR/Airspy/SDRplay),
while preserving a “Classic JJ” mode for existing users.

## Core Product Principles
1. **Slice is the unit of operation.** Most controls act on the active slice.
2. **Action-first, not field-first.** Users should not need to jump focus to “fields” to operate.
3. **Discoverable controls.** Built-in help + command search reduce documentation burden.
4. **Brief speech.** Tolk is used for short confirmations/status—not continuous verbose spam.
5. **No surprise keystrokes.** Avoid accidental reassignment; use explicit recording mode.
6. **Modern by default, Classic preserved.** New installs default to Modern; Classic retains legacy behavior.
7. **Accessibility is definition-of-done.** NVDA + JAWS smoke tests and keyboard navigation required.

## Major Workstreams (Roadmap)
### A) Foundations
- Command model / routing (commands as first-class, bindings as data)
- Hotkeys v2 (profiles + conflict detection)
- UI Mode system: Modern vs Classic (Classic includes Fields menu + legacy status box)

### B) Modern Slice-Centric UI
- Slice List (select/next/prev; announce/earcon)
- Slice Menu (mode, TX, pan, AGC, filter entry points)
- Filter Menu (filter-only actions)
- Plain-English Status (Speak Status + Status Dialog)

### C) SmartLink / Remote UX
- Account management: save accounts, quick connect, auto-refresh tokens
- Radio discovery and “quick connect” defaults
- Optional: track/telemetry of login sessions if supported by API

### D) Waterfall & Spectrum Exploration
- Braille waterfall surface (peaks + energy bins)
- Sonification modes (scan, cursor, “peek list” navigation)
- Earcons for band edges/markers; optional “crossing beep/tink”

### E) Audio Management
- Per-slice audio pan/levels; multi-source mixing (future)
- Record/playback of rig audio; “parrot” playback for TX quality checks
- DAX integration research (respect Flex tools if best)

### F) Device Ecosystem Integration
- 4O3A network devices: amp/tuner/switch/rotator (SDK/protocol driven)
- “Genius” ecosystem integration plans

### G) Wider SDR Support (Ace in the hole)
- Add read-only SDR sources (RTL-SDR/Airspy/SDRplay) with the same waterfall UX
- Later: demod/decoding/transmit where legal/appropriate and technically feasible
- Multi-device simultaneous operation is a future/premium candidate

## Key UX Decisions
### Classic vs Modern
- Modern is default for new installs; installer/first-run allows selection.
- Classic retains legacy Fields menu (Alt+F focus jumps) and legacy status box for muscle memory.
- Modern hides legacy Fields/status; provides slice-centric menus and status dialog instead.

### Fields Menu (Alt+F)
- **Classic:** keep as-is
- **Modern:** Alt+F may be reclaimed for Filter or Slice menu; no “focus teleporter” dependence

### Discoverability
- One-key **Command Finder** (default: F12) for “what key does that?”
- Contextual help (`H`/`?`) to list assigned keys in a group

## Documentation & Quality
- Maintain CHANGELOG entries for user-visible changes
- Add/keep code comments where behavior is non-obvious (especially AT-related)
- Keep planning docs updated when architectural decisions change
