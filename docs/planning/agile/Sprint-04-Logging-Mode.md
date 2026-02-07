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

### No Contest/SKCC Support
- Remove SKCC-specific log form (SKCCWESLog)
- Remove contest forms (FieldDay, NASprint) from active use
- Standard contact logging only (DefaultLog)
- Contest formats change too often to maintain

### Minimal Radio Controls in Logging Mode
- No radio controls in the initial Logging Mode UI
- Easy to switch out to Classic/Modern for radio adjustments, then back
- Log state preserved across mode switches

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

**2a. Create `LogPanel` UserControl**
- Embedded in Form1's main area (not a separate dialog like current LogEntry)
- Visible only in Logging Mode
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

**2e. State preservation across mode switches**
- When exiting Logging Mode with a QSO in progress (fields populated):
  - Field values stored in memory (not written to file)
  - No "save before switching?" prompt unless user has entered a call sign
  - Re-entering Logging Mode restores the in-progress entry
- `LogSession` stays open across mode switches (only closes on operator change or app exit)

**Files touched:** New `LogPanel.vb` UserControl, `Form1.vb` (hosting), `globals.vb` (state)

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
4. Quick-entry panel allows keyboard-driven QSO entry (Tab, Enter to save)
5. Radio auto-fills frequency, mode, band on new entry
6. Dup checking works with audio + screen reader alert
7. Recent QSOs grid shows last entries, updates on save
8. Switching out of Logging Mode preserves QSO-in-progress
9. Switching back into Logging Mode restores QSO-in-progress
10. All transitions and actions have screen reader announcements
11. SKCC WES form removed from active use; Field Day and NA Sprint retained

---

## Resolved Decisions

1. **Ctrl+Shift+M in Logging Mode:** Ignored. User must Ctrl+Shift+L to exit first, then can toggle base mode.
2. **Log session auto-open:** Yes. Entering Logging Mode auto-opens the operator's last used log file if no session is active.
3. **Country/zone auto-lookup:** Keep it. HamQTH lookups remain active in Logging Mode.
