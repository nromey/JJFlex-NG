# Sprint 4: Logging Mode

**Goal:** Add a Logging Mode UI state that provides a focused, keyboard-centric QSO logging experience. Logging Mode is a *layer* on top of Classic/Modern, not a third peer mode.

**Version target:** 4.1.12

---

## Key Design Decisions

### Logging Mode is a Layer, Not a Peer
- Classic and Modern are the two base modes (toggled by Ctrl+Shift+M)
- Logging Mode activates on top of whichever base mode is active
- Entering Logging Mode records `LastNonLogMode` (Classic or Modern)
- Exiting Logging Mode restores the saved base mode
- QSO-in-progress state is preserved when switching out and back

### Contest/SKCC Support
- Remove SKCC-specific log form (SKCCWESLog) — niche format, not worth maintaining
- Keep Field Day and NA Sprint forms — stable ARRL contests, already coded by JJ
- Log Characteristics dropdown shows: Standard, Field Day, NA Sprint

### Split Layout with Minimal RadioPane
- Logging Mode uses a horizontal SplitContainer: fixed 200px RadioPane (left) + LogPanel (right)
- RadioPane shows read-only Frequency, Mode, Band, and Tune Step displays
- RadioPane provides basic tuning: Up/Down arrows (1 step), Shift+Up/Down (10 steps), Left/Right (change step size), Ctrl+F (manual frequency entry)
- No mode switching, filter controls, power/ATU, or transmit controls in RadioPane
- F6 / Shift+F6 toggles focus between RadioPane and LogPanel
- For full radio controls, user exits to Classic/Modern — log state is preserved across mode switches

---

## Phases

### Phase 1: Menu & Mode Switching Infrastructure

**1a. Remove log menu items from Classic and Modern menus**
- Remove `LoggingMenuItem` submenu (Log Characteristics, Import, Export, LoTW Merge) from Classic menu bar
- Remove any log-related stubs from Modern menu's `BuildModernMenus()`
- These items move exclusively into Logging Mode's menu

**1b. Add "Enter Logging Mode" to Classic and Modern menus**
- Classic: Add "Enter Logging Mode" item to the Actions menu (near the mode switch item)
- Modern: Add "Enter Logging Mode" item to the Tools menu (near "Switch to Classic UI")

**1c. Add Ctrl+Shift+L hotkey**
- In `ProcessCmdKey`: Ctrl+Shift+L toggles Logging Mode on/off
- When entering: save `LastNonLogMode`, set `ActiveUIMode = UIMode.Logging`
- When exiting: restore `ActiveUIMode = LastNonLogMode`
- Screen reader announces "Entering Logging Mode" / "Returning to [Classic/Modern] mode"

**1d. Update `ApplyUIMode()` to handle Logging Mode**
- Add `ShowLoggingUI()` alongside `ShowClassicUI()` and `ShowModernUI()`
- `ShowLoggingUI()` hides both Classic and Modern menus, shows Logging Mode menu
- Logging Mode menu built programmatically like Modern menus

**1e. Build Logging Mode menu bar**
- **Log** menu: New Entry, Write Entry, Search, Log Characteristics, Import, Export, LoTW Merge
- **Navigate** menu: First Entry, Previous Entry, Next Entry, Last Entry
- **Mode** menu: Switch to Classic, Switch to Modern (both exit Logging Mode)
- **Help** menu: stays visible (shared across all modes)
- All items have AccessibleName and proper tab order

**Files touched:** `Form1.vb`, `Form1.Designer.vb`, `globals.vb` (ToggleLoggingMode helper)

---

### Phase 2: Quick-Entry Logging Panel

**2a. Split layout and `LogPanel` UserControl**
- Logging Mode replaces the main area with a horizontal SplitContainer
- Left panel (200px fixed): `RadioPane` — read-only freq/mode/band/step displays + arrow-key tuning
- Right panel (fills remaining width): `LogPanel` — QSO entry form + recent contacts grid
- `LogPanel` is a UserControl, visible only in Logging Mode
- Keyboard-centric: Tab moves between fields, Enter writes entry
- Fields (from DefaultLog, simplified):
  - Call sign (primary focus field)
  - RST Sent / RST Received
  - Name
  - QTH / State
  - Comments / Notes
  - Read-only display: Frequency, Mode, Band (auto-filled from radio)
  - Read-only display: Date/Time UTC (auto-filled)
  - Serial number (auto-incremented)

**2b. Radio auto-fill integration**
- On new entry: auto-fill RX freq, TX freq, mode, band from `RigControl`
- Same logic as existing `setModeIfNotSet()` / `setFreqsIfNotSet()` in LogEntry.vb
- Read-only fields update live if radio changes while entry is in progress

**2c. Dup checking**
- Call sign field triggers dup check on leave (same as existing `CallSign_Leave`)
- Screen reader announces "Duplicate - [N] previous contacts" with beep
- Dup count displayed in panel

**2d. Write flow**
- Enter key or menu item writes entry
- Appends to current log session via existing `LogSession.Append()`
- Clears fields, increments serial, returns focus to Call field
- Screen reader announces "Saved [callsign]"

**2e. F6 focus toggle between panes**
- F6 moves focus from LogPanel to RadioPane; Shift+F6 moves back
- Screen reader announces which pane received focus

**2f. Escape key clears form**
- Escape with data entered: TaskDialog asks "Clear the log entry for [CALL]?" with "Don't ask me again" checkbox
- Yes clears form, SR says "Entry cleared"; No keeps form intact
- "Don't ask me again" suppresses future confirmation (persisted as `SuppressClearConfirm` in operator settings)
- Escape with empty form: SR says "Entry is empty"

**2g. Close/exit protection for unsaved entries**
- Alt+F4 with a call sign entered: Dialog "Unsaved log entry for [CALL]. Save before closing?" with Yes/No/Cancel
- Yes saves (if valid) and closes; No discards and closes; Cancel keeps app open
- If save fails validation (e.g., missing RST), SR says "Save failed, close cancelled" and app stays open
- Alt+F4 with empty form: no prompt, app closes normally

**2h. State preservation across mode switches**
- When exiting Logging Mode with a QSO in progress (fields populated):
  - Field values stored in memory (not written to file)
  - No "save before switching?" prompt unless user has entered a call sign
  - Re-entering Logging Mode restores the in-progress entry
- `LogSession` stays open across mode switches (only closes on operator change or app exit)

**Files touched:** New `LogPanel.vb` UserControl, new `RadioPane.vb` UserControl, `Form1.vb` (hosting/split layout), `globals.vb` (state), `PersonalData.vb` (SuppressClearConfirm setting)

---

### Phase 3: Recent QSOs Grid

**3a. Add DataGridView to LogPanel**
- Shows last N QSOs (configurable, default 10-20)
- Columns: Time (UTC), Call, Mode, Freq, RST Sent, RST Rcvd, Name
- Auto-scrolls to most recent entry
- Read-only display (no inline editing)
- Populated from log session on Logging Mode entry

**3b. Grid updates on write**
- After each successful append, add new row to top of grid
- Screen reader: row count announced if user navigates to grid

**3c. Grid accessibility**
- Proper AccessibleName on grid and columns
- Arrow key navigation through rows
- Screen reader reads cell values on focus

**Files touched:** `LogPanel.vb`

---

### Phase 4: Cleanup & Polish

**4a. Remove SKCC WES form from active registration**
- Remove SKCCWESLog from `Logs.cs` registration (niche SKCC Weekend Sprint contest)
- Keep Field Day and NA Sprint (standard ARRL contests, well-known, stable)
- LogCharacteristics form type dropdown shows: Standard, Field Day, NA Sprint

**4b. Verify Classic/Modern mode switcher**
- Confirm "Switch to Modern UI" in Classic Actions menu works
- Confirm "Switch to Classic UI" in Modern Tools menu works
- Confirm Ctrl+Shift+M hotkey works from Classic and Modern
  - From Classic: switches to Modern
  - From Modern: switches to Classic
  - From Logging Mode: **ignored** (user exits with Ctrl+Shift+L first, then can toggle base mode)

**4c. Screen reader announcement audit**
- All mode transitions announced
- Log write confirmations announced
- Dup alerts announced
- Navigation (first/last/next/prev) announced
- Error conditions announced (no log open, write failed, etc.)

**4d. Version bump to 4.1.12**
- Update `JJFlexRadio.vbproj` AND `My Project\AssemblyInfo.vb`

**Files touched:** `JJLogLib/Logs.cs`, `Form1.vb`, version files

---

## Acceptance Criteria

1. Ctrl+Shift+L enters and exits Logging Mode from any base mode
2. Classic and Modern menus no longer show log-related items (except "Enter Logging Mode")
3. Logging Mode shows its own menu bar with log operations and mode switching
4. Split layout: RadioPane (left, 200px) with freq/mode/band/step + LogPanel (right)
5. RadioPane supports arrow-key tuning and Ctrl+F manual frequency entry
6. F6 / Shift+F6 toggles focus between RadioPane and LogPanel
7. Quick-entry panel allows keyboard-driven QSO entry (Tab, Enter to save)
8. Radio auto-fills frequency, mode, band on new entry
9. Dup checking works with audio + screen reader alert
10. Previous contact lookup auto-fills Name/QTH from prior QSOs
11. Recent QSOs grid shows last entries, updates on save
12. Escape clears form with optional confirmation (suppressible via "Don't ask me again")
13. Alt+F4 with unsaved entry prompts to save before closing
14. Switching out of Logging Mode preserves QSO-in-progress
15. Switching back into Logging Mode restores QSO-in-progress
16. All transitions and actions have screen reader announcements
17. SKCC WES form removed; Field Day and NA Sprint retained (JJ's original code)

---

## Resolved Decisions

1. **Ctrl+Shift+M in Logging Mode:** Ignored. User must Ctrl+Shift+L to exit first, then can toggle base mode.
2. **Log session auto-open:** Yes. Entering Logging Mode auto-opens the operator's last used log file if no session is active.
3. **Country/zone auto-lookup:** Keep it. HamQTH lookups remain active in Logging Mode.

---

## Test Matrix — Sprint 4 (Build 2026-02-08 11:00 AM)

### Mode Switching & Persistence

| # | Test | Expected | Pass? |
|---|------|----------|-------|
| 1 | Start app in Modern mode | App opens in Modern, not Logging | pass |
| 2 | Start app in Classic mode | App opens in Classic, not Logging | pass |
| 3 | Ctrl+Shift+L from Classic | Enters Logging Mode, SR says "Entering Logging Mode" | pass |
| 4 | Ctrl+Shift+L from Logging | Returns to Classic, SR says "Returning to Classic mode" | pass |
| 5 | Ctrl+Shift+L from Modern | Enters Logging Mode, SR says "Entering Logging Mode" | pass |
| 6 | Ctrl+Shift+L again | Returns to Modern (not Classic), SR confirms | pass |
| 7 | Close app while in Modern, reopen | Starts in Modern (mode persists, Logging doesn't) | pass |
| 8 | Close app while in Classic, reopen | Starts in Classic (mode persists) | pass |

### LogPanel Entry & Validation

| # | Test | Expected | Pass? |
|---|------|----------|-------|
| 9 | Tab through all Logging Mode fields | Call → RST Sent → RST Rcvd → Name → QTH → State → Grid → Comments → Previous → Recent Grid → wraps to Call. Tab does NOT enter RadioPane. | pass |
| 10 | Tab does NOT wrap into Classic controls | Tab stays within LogPanel only | pass |
| 11 | Type call sign + Enter (no RST) | SR says "Missing RST Sent, RST Received" — not saved | pass |
| 12 | Fill call + RST Sent + RST Rcvd + Enter | SR says "Saved [CALL]", fields clear, focus returns to Call | pass |
| 13 | Enter with empty call sign | Nothing happens (no action, no error) | pass |
| 14 | Previous contact: enter a call you've worked before | SR announces previous contact info, auto-fills Name/QTH } pass |
| 15 | Dup check: (1) Log menu → Log Characteristics, set dup checking to "Call and Band" or "Just Call". (2) Log a contact (e.g., W1AW). (3) Start new entry, type same call, Tab out of Call field. | Beep + SR says "Duplicate, N previous contacts". Saving is still allowed (warn-only, not blocked). | pass |

### Keyboard Shortcuts (Logging Mode Only)

| # | Hotkey | Expected | Pass? |
|---|--------|----------|-------|
| 16 | Alt+C | Focus jumps to Call Sign field | passed in log mode, alt+c activates some kind of DXCC thing in classic mode. is this expected? |
| 17 | Alt+T | Focus jumps to RST Sent field (senT) | pass |
| 18 | Alt+R | Focus jumps to RST Received field | pass |
| 19 | Alt+N | Focus jumps to Name field | pass that shortcut makes sense |
| 20 | Alt+Q | Focus jumps to QTH field | pass |
| 21 | Alt+S | Focus jumps to State field | pass |
| 22 | Alt+G | Focus jumps to Grid field | pass |
| 23 | Alt+E | Focus jumps to Comments field | pass |
| 24 | Ctrl+N | Clears form, SR says "New entry", focus to Call | pass |
| 25 | Ctrl+W | Saves entry (same as Enter — validates first) | pass |
| 26 | Ctrl+Shift+N | Opens Log Characteristics dialog | pass (FileShare.ReadWrite fix) |
| 27 | F6 / Shift+F6 | Toggles focus between Radio pane and Log pane  | pass |

### Escape Key (Clear Form)

| # | Test | Expected | Pass? |
|---|------|----------|-------|
| 28 | Escape with data entered | TaskDialog: "Clear the log entry for [CALL]?" with "Don't ask me again" checkbox | pass |
| 29 | Click Yes | Form clears, SR says "Entry cleared" | pass |
| 30 | Click No | Form stays, nothing changes | pass |
| 31 | Check "Don't ask" + Yes | Future Escape clears without dialog, SR says "Entry cleared" | pass. How do you reverse this if you've checked it bey accident? Guessing that'll be in the verbosity type field |
| 32 | Escape with empty form | SR says "Entry is empty" | pass |

### Close/Exit with Unsaved Entry

| # | Test | Expected | Pass? |
|---|------|----------|-------|
| 33 | Alt+F4 with call sign entered | Dialog: "Unsaved log entry for [CALL]. Save before closing?" Yes/No/Cancel | pass |
| 34 | Click Yes (fields valid) | Entry saved, app closes | pass |
| 35 | Click Yes (missing RST) | Dialog includes "Note: RST Sent, RST Received must be filled before save." Save blocked, app stays open. | pass (missing fields now shown in dialog prompt) |
| 36 | Click No | Entry discarded, app closes | pass |
| 37 | Click Cancel | App stays open, entry preserved | pass |
| 38 | Alt+F4 with empty form | No prompt, app closes normally | pass |

### Radio Integration

| # | Test | Expected | Pass? |
|---|------|----------|-------|
| 39 | Enter Logging Mode with radio connected | Freq/Mode/Band labels auto-filled | pass |
| 40 | Change radio frequency while in Logging Mode | Freq label updates (on next field exit or new entry) | pass |
| 41 | Tuning mechanic (from radio pane) | Works in Logging Mode | pass |

### Hotkeys in Classic/Modern (Regression)

| # | Test | Expected | Pass? |
|---|------|----------|-------|
| 42 | Alt+C in Classic mode | Opens full LogEntry form (NOT LogPanel) | pass |
| 43 | Ctrl+N in Classic mode | Opens full LogEntry form with new entry | pass |
| 44 | Ctrl+W in Classic mode | Writes entry in full LogEntry form | pass |
| 45 | Ctrl+Shift+M in Classic | Switches to Modern | fail, I was in classic, hitting ctrl+m does not leave log mode |
| 46 | Ctrl+Shift+M in Modern | Switches to Classic | fail if you're in full log panel. |

---

## Parking Lot (Future Sprints)

- **Expanded/full-screen logger view**: Ctrl+Alt+L toggles between standard split layout and expanded full-window logger (only available while in Logging Mode). Hotkey family: Ctrl+Shift+L = enter/exit Logging Mode, Ctrl+Alt+L = toggle expanded view.
- **Reset "Don't ask me again"**: Add option in settings/verbosity area to reset the Escape-clear confirmation dialog preference.
- **NVDA "Radio pane" announcement**: NVDA does not speak the AccessibleName prefix on FreqBox due to UIA processing differences. Note in help docs; investigate UIA workaround in future sprint.
- **Tests 45-46**: Ctrl+Shift+M blocked while Classic/Modern LogEntry form is open — pre-existing behavior, not Sprint 4 regression.
