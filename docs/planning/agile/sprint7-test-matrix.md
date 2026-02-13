# Sprint 7 Test Matrix

**Sprint:** 7 — WPF Migration, Station Lookup, Status & Polish
**Build:** `bin\x64\Release\net8.0-windows\win-x64\JJFlexRadio.exe`
**Date:** 2026-02-13

---

## Track A: Bug Fixes & Polish

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| A1 | BUG-015: F6 no double-announce | F6 in Logging Mode | Hear "Radio pane, Frequency..." exactly once, no extra "Radio pane" | | |
| A2 | BUG-013: Dup beep audible | Log a duplicate QSO | Hear both beep (SystemSounds.Exclamation) AND "worked x calls" speech | | |
| A3 | CW hotkey feedback (no messages) | Press Ctrl+1 with no CW messages configured | Hear "No CW messages configured" or similar | | |
| A4 | CW hotkey feedback (F12) | Press F12 with no CW messages | Hear feedback message | | |
| A5 | Menu stubs wired | Open Modern menu, check previously "coming soon" items | Wired items work; remaining stubs still say "coming soon" | | |
| A6 | Changelog updated | Open `docs/CHANGELOG.md` | Sprint 6 changes documented in first-person tone | | |

---

## Track B: WPF Migration

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| B1 | WPF LogPanel loads | Enter Logging Mode | LogPanel displays with all fields (Call, RST S/R, Name, QTH, State, Grid, Comments) | | |
| B2 | Tab order correct | Tab through LogPanel fields | Order: Call → RST Sent → RST Rcvd → Name → QTH → State → Grid → Comments | | |
| B3 | QSO logging works | Enter a QSO and press Ctrl+Enter | QSO saved, appears in Recent QSOs grid, serial increments | | |
| B4 | Previous Contact lookup | Enter a known callsign, tab out of Call field | Previous contact info displayed | | |
| B5 | Dup checking | Enter a duplicate callsign | "Dup: N" label updates, beep plays, speech announces | | |
| B6 | Auto-fill from radio | Enter Logging Mode with radio connected | Freq, Mode, Band, UTC auto-filled | | |
| B7 | WPF Station Lookup opens | Press Ctrl+L | Station Lookup dialog opens | | |
| B8 | Station Lookup callbook | Enter a callsign, click Lookup | Name, QTH, State, Country, zones populated | | |
| B9 | F6 pane switching | Press F6 in Logging Mode | Switches between Radio pane and LogPanel | | |
| B10 | Mode switching | Switch Classic → Modern → Logging → Classic | All modes render correctly, no crashes | | |
| B11 | Hotkeys after WPF | Test various hotkeys in Logging Mode | All hotkeys still work (ProcessCmdKey + ElementHost) | | |

---

## Track C: Station Lookup Enhancements

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| C1 | Grid Square in Settings | Open PersonalInfo, enter grid (e.g., "FN31pr"), save, reopen | Grid square persisted and shown | | |
| C2 | Distance/bearing displayed | Set grid in Settings, Ctrl+L, look up a station with grid | Distance/bearing row shows (e.g., "245 mi (394 km), bearing 035 degrees") | | |
| C3 | Distance/bearing announced | Same as C2, listen to screen reader | Announcement includes distance and bearing | | |
| C4 | No operator grid message | Clear grid in Settings, look up a station | Distance row shows "Set your grid square in Settings" | | |
| C5 | Log Contact button | Look up a station, click "Log Contact" | Dialog closes, Logging Mode opens, fields pre-filled (Call, Name, QTH, State, Grid) | | |
| C6 | Log Contact Ctrl+Enter | Look up a station, press Ctrl+Enter | Same as C5 | | |
| C7 | Log Contact announcement | Click Log Contact | Screen reader announces "Entering Logging Mode with [call] pre-filled" | | |
| C8 | Log Contact from non-Logging | Open Station Lookup from Classic/Modern mode, click Log Contact | Switches to Logging Mode, fields pre-filled | | |
| C9 | Done button still works | Look up a station, click Done | Dialog closes, no Logging Mode change | | |
| C10 | Pre-fill triggers dup check | Log Contact with a previously worked station | Dup count and previous contact displayed after pre-fill | | |

---

## Track D: Plain-English Status

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| D1 | Speak Status (radio connected) | Press Speak Status hotkey with radio connected | Hear concise summary: freq, mode, band, slice | | |
| D2 | Speak Status (no radio) | Press Speak Status hotkey with no radio | Hear "No radio connected" or similar | | |
| D3 | Status Dialog opens | Press Status Dialog hotkey | WPF Status Dialog opens with full radio status | | |
| D4 | Status Dialog accessible | Tab through Status Dialog fields | All fields announced by screen reader | | |
| D5 | Status Dialog (no radio) | Open Status Dialog with no radio | Shows appropriate "no radio" state | | |

---

## Track E: Configurable Recent QSOs

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| E1 | Default value | New operator, enter Logging Mode | Grid shows 20 recent QSOs (or fewer if < 20 exist) | | |
| E2 | Change to 10 | Set Recent QSOs to 10 in Settings, enter Logging Mode | Grid shows max 10 QSOs | | |
| E3 | Change to 50 | Set Recent QSOs to 50 in Settings | Grid shows up to 50 QSOs (if enough exist) | | |
| E4 | Persists | Change setting, close and reopen Settings | Value persisted | | |

---

## Integration Tests (post-merge)

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| I1 | Full QSO workflow | Station Lookup → Log Contact → fill RST → Ctrl+Enter → check grid | QSO logged, appears in grid with correct data | | |
| I2 | Speak Status after QSO | Log a QSO, then Speak Status | Status reflects current frequency/mode | | |
| I3 | All modes work | Cycle through Classic → Modern → Logging → back | No crashes, all features available in each mode | | |
| I4 | Configurable grid + WPF | Set QSO count to 10, log contacts, verify grid | WPF DataGrid respects the configurable count | | |

---

## Screen Reader Matrix

| Feature | JAWS | NVDA |
|---------|------|------|
| WPF LogPanel fields (AutomationProperties) | | |
| WPF LogPanel DataGrid (row/col headers) | | |
| WPF Station Lookup (all fields) | | |
| Grid Square field in Station Lookup | | |
| Distance/bearing announcement | | |
| Log Contact button + workflow | | |
| Speak Status hotkey | | |
| Status Dialog (all fields, tab order) | | |
| F6 pane switching (no double-announce) | | |
| CW feedback messages | | |
| Recent QSOs count setting (PersonalInfo) | | |
| Grid Square setting (PersonalInfo) | | |

---

## Sign-off

| Tester | Date | Result | Notes |
|--------|------|--------|-------|
| | | | |
