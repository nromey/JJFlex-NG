# Sprint 15 Test Matrix

**Tested by:** Noel Romey (K5NER)
**Date:** 2026-02-25
**Build:** v4.1.115 Debug x64
**Screen Reader:** NVDA
**Radio:** Don's FLEX-6300 (WA2IWC) via SmartLink
**Claude Code version:** 2.55 → 2.56 during session

---

## Track A: WPF RigSelector + SmartLink

| # | Test | Result | Notes |
|---|------|--------|-------|
| A1 | SmartLink button connects using saved account | PASS | Auto-selects most recent account, connects to SmartLink server |
| A2 | Auth0 New Login flow | PASS | WebView2 PKCE flow works, token exchange succeeds |
| A3 | Radio discovery after SmartLink login | PASS | Don's 6300 appears in list |
| A4 | Connect to radio via SmartLink | PASS | Connected successfully to WA2IWC |
| A5 | Delete a SmartLink account | PASS | Account removed from saved list |
| A6 | Add multiple SmartLink accounts | PASS | Feature SmartSDR doesn't have |
| A7 | Per-radio auto-connect with specific SmartLink account | PASS | Connects to correct radio on correct account |
| A8 | Auto-connect on startup (both settings enabled) | PASS | Skips RigSelector, connects automatically |
| A9 | Auto-connect failed dialog | PASS | Try Again / Disable / Choose Another options work |
| A10 | SmartLink connection retry after failure | NOTE | Retry is instant (4ms), needs 2-3s delay — Sprint 16 |

### UX Issues (Sprint 16)

- Empty radio list gives no screen reader feedback — user doesn't know list is empty
- No guidance that SmartLink button needs to be clicked first for remote radios
- When no radios found, user should be told "No radios online" or similar

---

## Track B: Filter Overhaul

| # | Test | Result | Notes |
|---|------|--------|-------|
| B1 | `[` / `]` — independent edge adjust | PASS | Low edge down, high edge up |
| B2 | `Ctrl+[` / `Ctrl+]` — squeeze/pull | PASS | Both edges narrow/widen equally |
| B3 | `Shift+[` / `Shift+]` — passband shift | PASS | Slides entire passband |
| B4 | `Alt+[` / `Alt+]` — preset cycling | BUG | "No presets loaded" — FilterPresets never wired to FreqOutHandlers or NativeMenuBar |
| B5 | `Alt+Ctrl+F` — read filter values | PASS | Speaks "Filter [low] to [high]" |
| B6 | `Ctrl+Shift+F` — toggle freq readout | PASS | Global, works from anywhere |
| B7 | `F` — one-shot read frequency | BUG | Doesn't fire in Classic or Modern — key routing issue |
| B8 | `Shift+S` — announce step size | BUG | Doesn't fire — same key routing issue as F |
| B9 | Bracket keys work app-wide (not just VFO) | PASS | Handled in PreviewKeyDown, no focus check |

### Sprint 16 Filter Items

- **BUG: Wire FilterPresets** — `FreqOutHandlers.FilterPresets` and `NativeMenuBar.FilterPresets` are never assigned. Presets class has defaults built in, just never loaded/connected.
- **BUG: F and Shift+S key routing** — These keys are in `AdjustFreq`/`AdjustFreqModern` (FrequencyDisplay field handlers) but never reach them. Something in PreviewKeyDown or KeyCommands intercepts first.
- **Edge select mode** — Double-tap `[` selects left edge, double-tap `]` selects right edge. Then `[`/`]` nudge selected edge in either direction. Earcon feedback: two descending clicks (left edge), two ascending clicks (right edge), bonk-bonk on timeout exit. Speech + earcons by default, verbosity setting to suppress speech later.
- **Make F (read freq) work globally** — should speak frequency from anywhere, not just VFO focused.

---

## Track C: PTT Safety System

| # | Test | Result | Notes |
|---|------|--------|-------|
| C1 | Space = PTT hold | DEFERRED | Needs virtual dummy load before testing on live radio |
| C2 | Shift+Space = lock TX toggle | DEFERRED | Same |
| C3 | Earcon warning beeps | DEFERRED | Same |
| C4 | Configurable timeout | DEFERRED | Same |
| C5 | 15-min hard kill | DEFERRED | Same |
| C6 | ALC=0 auto-release | DEFERRED | Same |

**Deferred until Sprint 16** — Virtual dummy load needed to safely test PTT on Don's equipment. PTT infrastructure (state machine, earcons, config) was built in Sprint 15 but cannot be verified without TX safety.

---

## Track D: ScreenFields Menu Redesign

| # | Test | Result | Notes |
|---|------|--------|-------|
| D1 | `Ctrl+Shift+N` — Noise Reduction panel | PASS | Expands/collapses correctly |
| D2 | `Ctrl+Shift+A` — Audio panel | PASS | Expands/collapses correctly |
| D3 | `Ctrl+Shift+R` — Receiver panel | PASS | Expands/collapses correctly |
| D4 | `Ctrl+Shift+T` — Transmission panel | PASS | Expands/collapses correctly |
| D5 | `Ctrl+Shift+E` — Antenna panel | PASS | Expands/collapses correctly |
| D6 | Hotkey toggles expand/collapse | PASS | Same key opens and closes |
| D7 | Focus set on expand | MISSING | Expand opens panel but doesn't focus first control — user must Tab-hunt |
| D8 | Focus return on collapse | MISSING | Collapse closes panel but focus stays wherever it was |
| D9 | ScreenFields menu shows 5 categories | NOT TESTED | |

### Sprint 16 ScreenFields Items

- **Focus on expand:** Hotkey should expand group AND set focus to first control inside it
- **Focus on collapse:** Should return focus to VFO (home position), always predictable
- **Ctrl+Tab / Ctrl+Shift+Tab:** Jump between expanded groups' first controls
- **Key remapping:** `Ctrl+Shift+A` = Antenna (first-letter), `Ctrl+Shift+U` = Audio

---

## Track E: UIA LiveRegion Pilot

| # | Test | Result | Notes |
|---|------|--------|-------|
| E1 | Menu toggle speaks result via LiveRegion | FAIL | NVDA reads "Status" (AutomationProperties.Name) instead of message content |
| E2 | Menu toggle speaks result via Tolk (after revert) | PASS | Reverted SpeakAfterMenuClose to 150ms Tolk delay — speech works |
| E3 | NR checkmark shows on/off state | PASS | Checked/unchecked announced correctly |
| E4 | RNN grayed when unlicensed | BUG | RNN menu item not grayed on Don's radio (no RNN license) |
| E5 | First-letter nav in menus | PASS | D jumps to DSP, N cycles through N items |

### Track E Conclusion

**LiveRegion does NOT work through WinForms → ElementHost → WPF interop chain.** UIA LiveRegionChanged events from the WPF TextBlock don't propagate to the Win32 UIA tree that NVDA reads. This was identified as a risk in the sprint plan. Reverted to Tolk with 150ms delay (proven pattern).

**Sprint 16:** Remove LiveRegion code (dead path), document that Tolk is the correct approach for this architecture. Consider LiveRegion only if/when the app moves to pure WPF.

---

## General Issues Found

| # | Issue | Priority | Sprint |
|---|-------|----------|--------|
| G1 | `Ctrl+/` Command Finder shows "Ctrl+M2" instead of "Ctrl+/" | Medium | 16 |
| G2 | Command Finder missing all WPF-handled hotkeys (filters, PTT, ScreenFields) | Medium | 16 |
| G3 | Need `Ctrl+Shift+/` for spoken context-sensitive hotkey help (like NVDA Insert+H) | Medium | 16 |
| G4 | Unify KeyCommands table + WPF PreviewKeyDown into single system | Medium | 16 |
| G5 | RNN menu item not gated by license | Low | 16 |
| G6 | SmartLink retry has no delay (4ms between attempts) | Medium | 16 |
| G7 | Need connection telemetry/profiler for SmartLink re-add investigation | Medium | 15.5 |
| G8 | Earcon + speech for filter edge mode, with future verbosity toggle | Low | 16 |
| G9 | Audio routing: earcons/speech to different device than radio audio | Low | 16+ |

---

## Sprint 15.5 Proposal: SmartLink Connection Profiler

**Goal:** Instrument SmartLink connection lifecycle to collect data on the guiClient remove/re-add timing issue.

**Scope:**
- Connection telemetry logger — timestamps each event (WAN connect, JWT, registration, radio list, GUI client add/remove/re-add, station name, success/failure)
- Output to JSON file per session
- Automated reconnect loop (connect → log → disconnect → repeat N times)
- Summary statistics: success rate, average re-add time, failure distribution, time-of-day correlation
- Add 2-3 second retry delay (simple fix while investigating)

**Not in scope:** Behavior changes, API re-registration attempts (need data first).

---

## Code Changes Made During Testing

1. **globals.vb** — `ShowAccountSelector` lambda: tested account dialog approach, reverted to auto-select (original behavior). Net change: added better comments.
2. **NativeMenuBar.cs** — `SpeakAfterMenuClose`: reverted from LiveRegion to Tolk with 150ms delay. LiveRegion confirmed broken through ElementHost.

---

## Sprint 16 Candidates (Consolidated)

### From Testing
- Wire FilterPresets to FreqOutHandlers and NativeMenuBar
- Fix F and Shift+S key routing
- Edge select mode with earcon feedback
- ScreenFields focus on expand/collapse + Ctrl+Tab between groups
- Key remapping: A=Antenna, U=Audio
- Ctrl+Shift+/ spoken hotkey help
- Fix Ctrl+/ key name display
- Unify KeyCommands + WPF hotkeys
- RNN license gating
- SmartLink retry delay
- Virtual dummy load for PTT testing
- Remove dead LiveRegion code

### Carried Forward from Agent.md
- S-meter read on bare S key
- Configurable tuning step lists
- Global tuning hotkeys (system-wide)
- Logging mode: F6 to flip to radio view
- Alt+letter menu accelerator investigation
- Earcon expansion (boundary bonk, field tick, tune tones)
- Virtual dummy load

### New Ideas
- Connection telemetry profiler (Sprint 15.5)
- NAudio integration for waterfall sonification
- Verbosity settings (speech vs earcons only)
- Audio device routing for earcons/speech separate from radio
