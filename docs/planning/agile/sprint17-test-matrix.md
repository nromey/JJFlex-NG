# Sprint 17 Test Matrix — QRM Splatter Pileup

**Tested by:** Noel Romey (K5NER)
**Date:** 2026-03-01
**Build:** v4.1.115 Debug x64
**Screen Reader:** NVDA
**Radio:** _(fill in)_

---

## Pre-Test Setup

1. Build Debug x64: `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
2. Connect to radio via SmartLink or local
3. Verify you're on a known band (e.g., 20m USB) before starting

---

## Track A: PTT / TX Enhancements

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| A1 | TX start chirp tone | Ctrl+Space (hold) | Ascending chirp 400→800 Hz plays | | |
| A2 | TX stop chirp tone | Release Ctrl+Space | Descending chirp 800→400 Hz plays | | |
| A3 | "Transmitting" speech | Ctrl+Space hold | NVDA says "Transmitting" | | |
| A4 | "Receiving" speech | Release Ctrl+Space | NVDA says "Receiving" | | |
| A5 | TX lock chirp | Shift+Space | Ascending chirp plays, NVDA says "Transmitting, locked" | | |
| A6 | TX unlock (Escape) | Press Escape while locked | Descending chirp, NVDA says "Receiving" | | |
| A7 | Alt+Shift+S while transmitting | Lock TX, then Alt+Shift+S | NVDA speaks "transmitting, locked, N seconds remaining" | | |
| A8 | Alt+Shift+S while receiving | Not transmitting, Alt+Shift+S | NVDA says "Receiving" | | |
| A9 | SpeechEnabled = false | Settings > PTT > uncheck "Announce transmit/receive", then Ctrl+Space | Chirp tones still play, but NO "Transmitting"/"Receiving" speech | | |
| A10 | Safety warnings ignore SpeechEnabled | With SpeechEnabled off, let TX lock approach timeout | Warning beeps and speech ("timeout approaching") still fire | | |
| A11 | Dummy load — no ALC auto-release | Enable Dummy Load, lock TX | Should NOT auto-release (ALC is always 0 in dummy load) | | |
| A12 | TX health: silent mic warning | Lock TX with mic unplugged/muted, wait ~5s | NVDA says "Check microphone" | | |
| A13 | TX health: mic too hot | Lock TX with mic gain cranked, wait ~5s | NVDA says "Microphone level too high" (if ALC > 0.95) | | |

---

## Track B: Display Bug Fixes

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| B1 | Slice labels show letters | Connect, check slice references in UI | Shows A/B/C/D, NOT 0/1/2/3 | | |
| B2 | Filter presets USB | Switch to USB, cycle filter presets (Ctrl+[ / Ctrl+]) | Presets cycle correctly: Narrow/Normal/Wide/Extra Wide | | |
| B3 | Filter presets LSB | Switch to LSB, cycle filter presets | Same preset names, filter values are mirrored (negative low/high) | | |
| B4 | Filter presets CW | Switch to CW, cycle presets | CW presets: Tight/Narrow/Normal/Wide | | |
| B5 | Filter presets DIGL | Switch to DIGL, cycle presets | DIGI presets with mirrored values (like LSB) | | |
| B6 | Filter preset speech | Cycle presets in any mode | NVDA announces e.g. "2.4k Normal" or "250 hertz Narrow" | | |
| B7 | Filter preset wrap | At widest preset, try to go wider | Should announce "already at limit" or similar (not crash/silent) | | |

---

## Track C: Band Navigation (UPDATED — F-key reshuffle)

**New key layout:** F3–F9 = main bands, Shift+F3–F6 = WARC, F10–F11 = mode cycle, Alt+U/L/C = direct mode

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| C1 | F6 → 20m | Press F6 | Tunes to 20m, NVDA says "20 meter band, 14.200.000" (or band memory freq) | | |
| C2 | F3 → 160m | Press F3 | Tunes to 160m with speech | | |
| C3 | F9 → 6m | Press F9 | Tunes to 6m with speech | | |
| C4 | Shift+F3 → 60m | Press Shift+F3 | Tunes to 60m (WARC) with speech | | |
| C5 | Shift+F6 → 12m | Press Shift+F6 | Tunes to 12m with speech | | |
| C6 | Alt+Up (Band Up) | Press Alt+Up from 20m | Moves to next higher band (15m), announces it | | |
| C7 | Alt+Down (Band Down) | Press Alt+Down from 20m | Moves to next lower band (40m), announces it | | |
| C8 | Band Up at top (6m) | On 6m, press Alt+Up | NVDA says "Top of band list", stays on 6m | | |
| C9 | Band Down at bottom (160m) | On 160m, press Alt+Down | NVDA says "Bottom of band list", stays on 160m | | |
| C10 | Band boundary beep | Tune manually across a band edge | Double-beep (600 Hz), NVDA says "Entering N meter band" | | |
| C11 | Band memory save/recall | Go to 20m USB, tune to 14.225, jump to 40m, jump back to 20m | Returns to 14.225 (not band center) | | |
| C12 | Band memory per-mode | On 20m USB tune to 14.225, switch to LSB tune to 14.150, jump away, jump back to 20m USB | Returns to 14.225 (USB memory), switch to LSB returns to 14.150 | | |
| C13 | Band memory disabled | Settings > Tuning > uncheck "Enable band memory", jump between bands | Always goes to band center, not remembered freq | | |
| C14 | Band menu | Click Band menu in menu bar | Shows 11 bands (no 2m) with F-key hotkeys, Band Up, Band Down | | |
| C15 | CW windows moved | Press Ctrl+Shift+F3, Ctrl+Shift+F4, Ctrl+Shift+F5 | Opens CW Receive, CW Send, CW Send Direct respectively | | |
| C16 | Alt+Z = CW Zerobeat | Press Alt+Z | CW Zerobeat activates (was Alt+C) | | |

### Mode Hotkeys (NEW)

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| M1 | F10 = Next mode | Press F10 | Mode cycles forward (e.g., USB→LSB), NVDA announces new mode | | |
| M2 | F11 = Previous mode | Press F11 | Mode cycles backward, NVDA announces | | |
| M3 | Mode cycle wraps | Cycle past FM (last) | Wraps back to USB (first) | | |
| M4 | Alt+U = USB | Press Alt+U | Switches to USB, NVDA says "USB" | | |
| M5 | Alt+L = LSB | Press Alt+L | Switches to LSB, NVDA says "LSB" | | |
| M6 | Alt+C = CW | Press Alt+C | Switches to CW, NVDA says "CW" | | |
| M7 | Already in mode | Already in USB, press Alt+U | NVDA says "Already USB" | | |
| M8 | Mode on exotic mode | Set mode to SAM via menu, then F10 | Jumps to first common mode (USB), announces it | | |

### License-Aware Tuning

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| L1 | TX lockout — General on Extra segment | Settings > License: General + TX lockout on. Tune to Extra-only segment (e.g., 14.000–14.025 on 20m). Try Ctrl+Space | NVDA: "Cannot transmit here, outside licensed band segment" + warning tone. TX blocked | | |
| L2 | TX lockout — Extra (no restrictions) | Settings > License: Extra + TX lockout on. Same freq | TX proceeds normally | | |
| L3 | TX lockout off | Settings > License: any class, TX lockout OFF. Tune to out-of-privilege segment, Ctrl+Space | TX proceeds (no lockout) | | |
| L4 | Band boundary notifications | Settings > License: boundary notifications ON. Tune across a band edge | NVDA says "Entering N meter band" | | |
| L5 | Band boundary notifications off | Settings > License: boundary notifications OFF. Tune across band edge | Double-beep still plays, but NO speech announcement | | |

---

## Track D: ValueFieldControl Enhancements

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| D1 | PgUp in a value field | Focus a ValueFieldControl (e.g., AF gain), press PgUp | Value increases by 10x the normal step, NVDA announces new value | | |
| D2 | PgDn in a value field | Same field, press PgDn | Value decreases by 10x step, announced | | |
| D3 | PgUp at max | At maximum value, press PgUp | Value stays at max (clamped), no crash | | |
| D4 | PgDn at min | At minimum value, press PgDn | Value stays at min (clamped), no crash | | |
| D5 | Home → minimum | Press Home | Jumps to minimum value, announced | | |
| D6 | End → maximum | Press End | Jumps to maximum value, announced | | |
| D7 | Number entry: start | Press Enter on a value field | NVDA says "Enter [Label] value" | | |
| D8 | Number entry: type digits | Type digits (e.g., 7, 5) | NVDA speaks each digit ("7", "5"), display shows "Label: 75_" | | |
| D9 | Number entry: confirm | Press Enter again | Value set to 75 (if valid), NVDA announces "Label 75" | | |
| D10 | Number entry: out of range | Enter a number beyond max (e.g., 999) | Value clamped to max, announced | | |
| D11 | Number entry: cancel | Press Escape during entry | NVDA says "Cancelled", value unchanged | | |
| D12 | Number entry: backspace | Type digits, press Backspace | Last digit removed, NVDA says "delete" | | |
| D13 | Number entry: empty confirm | Press Enter, then Enter again without typing | NVDA says "Invalid, cancelled" | | |

---

## Track E: Settings Dialog & Command Finder

### Settings Dialog

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| E1 | Open Settings | Tools > Settings (or Actions > Settings) | Settings dialog opens, PTT tab focused | | |
| E2 | Tab navigation | Tab through all controls on PTT tab | NVDA reads each field with label and valid range | | |
| E3 | Switch tabs | Click/arrow to Tuning tab | NVDA announces "Tuning settings" | | |
| E4 | License tab | Switch to License tab | Shows License Class combo, boundary notifications, TX lockout checkboxes | | |
| E5 | Audio tab | Switch to Audio tab | Shows placeholder "coming in a future update" | | |
| E6 | Save PTT settings | Change timeout to 120, click OK | Dialog closes, setting persists (reopen to verify) | | |
| E7 | Invalid timeout | Enter "abc" in timeout, click OK | Error message, field refocused, dialog stays open | | |
| E8 | Cancel | Change values, click Cancel | Dialog closes, no changes applied (reopen to verify) | | |
| E9 | Tuning step sizes | Change coarse step to 5 kHz, fine step to 100 Hz, OK | Tuning step sizes change accordingly when tuning | | |

### Command Finder

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| E10 | Open Command Finder | Ctrl+/ | Command Finder opens, search box focused | | |
| E11 | Keyword search | Type "band" | Shows band-related commands (jump, up, down), NVDA announces result count | | |
| E12 | Keyword search: "ptt" | Type "ptt" | Shows PTT-related commands | | |
| E13 | Scope filter | Uncheck "Show All Scopes" | List filters to current-mode commands only, count updates | | |
| E14 | Execute from finder | Search, select a command, press Enter | Command executes, dialog closes | | |
| E15 | Empty search | Clear search box | Shows all commands (or all in-scope if filtered) | | |

---

## Sprint 16 Regression Checks

These items passed in Sprint 16 — verify they still work after Sprint 17 changes.

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| R1 | Ctrl+Space PTT | Hold Ctrl+Space | TX activates (basic PTT still works) | | |
| R2 | Shift+Space lock | Shift+Space from any field | TX locks | | |
| R3 | Escape unlock | Escape while locked | TX unlocks | | |
| R4 | Dummy Load toggle | Menu > Dummy Load | Toggles dummy load mode | | |
| R5 | Ctrl+Shift+S Speak Status | Press Ctrl+Shift+S | NVDA speaks enriched status | | |

---

## Screen Reader Matrix

| # | Feature | NVDA | JAWS | Notes |
|---|---------|------|------|-------|
| SR1 | Band jump announcements | | N/A | |
| SR2 | Band boundary "Entering N meter band" | | N/A | |
| SR3 | Settings dialog tab names | | N/A | |
| SR4 | Settings field labels with ranges | | N/A | |
| SR5 | PTT speech (Transmitting/Receiving) | | N/A | |
| SR6 | TX lockout block speech | | N/A | |
| SR7 | ValueFieldControl number entry digits | | N/A | |
| SR8 | Command Finder result count | | N/A | |
| SR9 | Earcon tones audible alongside speech | | N/A | |
| SR10 | SpeechEnabled=false mutes TX/RX but not warnings | | N/A | |
| SR11 | Mode cycle speech (F10/F11) | | N/A | |
| SR12 | Direct mode speech (Alt+U/L/C) | | N/A | |

---

## Test Order (Suggested)

For a guided walkthrough, I'd suggest this order:

1. **E1–E9** — Settings dialog first (so you can configure license/tuning for later tests)
2. **C1–C16** — Band navigation with new F-keys (F3–F9 bands, Shift+F WARC, CW moved to Ctrl+Shift)
3. **M1–M8** — Mode hotkeys (F10/F11 cycle, Alt+U/L/C direct)
4. **L1–L5** — License-aware tuning (uses license settings from E)
5. **B1–B7** — Filter presets (while moving between bands)
6. **D1–D13** — ValueFieldControl (test on AF gain or similar)
7. **A1–A13** — PTT/TX enhancements (save TX tests for last — less disruptive)
8. **E10–E15** — Command Finder
9. **R1–R5** — Regression checks
10. **SR1–SR12** — Screen reader specific (can be noted throughout)

Total: **~70 test items**
