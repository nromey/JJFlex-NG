# Sprint 5: QRZ/HamQTH Lookup & Full Log Access

**Version target:** 4.1.13
**Predecessor:** Sprint 4 — Logging Mode (v4.1.12)

## Context

Sprint 4 delivered Logging Mode — a focused, keyboard-centric quick-entry overlay with LogPanel + RadioPane in a split layout. Sprint 5 extends it with callbook auto-fill (QRZ/HamQTH), access to JJ's full LogEntry form from within Logging Mode via Ctrl+Alt+L, and removes log entry hotkeys from Classic/Modern modes so all logging is done through Logging Mode.

---

## Pre-Sprint: Retest Recent QSOs Grid (before any code changes)

The Recent QSOs DataGridView changes from Sprint 4 were never tested with a fresh binary. Before starting Sprint 5 code, do a quick manual retest:

| # | Test | Expected |
|---|------|----------|
| R1 | Enter Logging Mode, log 2-3 QSOs, check grid shows them | Grid shows entries, most recent at bottom |
| R2 | Navigate grid with Down arrow — what does JAWS announce for first data row? | Note exact row number announced (Row 1? Row 2?) |
| R3 | Log a QSO when grid has 20 entries | Oldest entry removed, new entry at bottom, grid scrolls to show it |

**If R2 reveals row indexing issues**, add a fix to Phase 6 (grid headers or accessible name adjustment).

---

## Phase 1: Remove Classic/Modern Log Entry Hotkeys

**Rationale:** Logging belongs in Logging Mode. The 11 hotkeys that open the old LogEntry form from Classic/Modern mode (Ctrl+N, Alt+C, Alt+M, Alt+Q, Alt+S, Alt+G, Alt+N, Alt+R, Ctrl+A, Alt+E, Ctrl+H) are being removed. Operators enter Logging Mode via Ctrl+Shift+L and use Ctrl+Alt+L to access the full LogEntry form from there.

### 1a. Disable log hotkeys in Classic/Modern

In `KeyCommands.vb`, change the default key bindings for all log-field commands to `Keys.None`:

```
Line 372: LogCall        — Alt+C → Keys.None
Line 375: LogHisRST      — Ctrl+H → Keys.None
Line 376: LogMyRST       — Alt+M → Keys.None
Line 377: LogQTH         — Alt+Q → Keys.None
Line 378: LogState       — Alt+S → Keys.None
Line 379: LogGrid        — Alt+G → Keys.None
Line 380: LogHandle      — Alt+N → Keys.None
Line 381: LogRig         — Alt+R → Keys.None
Line 383: LogAnt         — Ctrl+A → Keys.None
Line 384: LogComments    — Alt+E → Keys.None
Line 385: NewLogEntry    — Ctrl+N → Keys.None
```

This frees up Alt+C, Alt+S, Alt+G, Alt+N, Alt+R, Alt+E, Alt+M for future radio-mode hotkeys (per context-aware-hotkeys.md design doc).

### 1b. Remove Modern mode "New Entry" menu item

In `Form1.vb` `BuildLoggingMenus()` (or Modern menu building), remove the "New Entry" menu item from the Tools → Log submenu.

### 1c. Keep Ctrl+Shift+F (SearchLog) and Ctrl+W (LogFinalize)

These should remain available — search is useful from any mode, and Ctrl+W write can stay for backwards compat.

**Files:**
- **Modify:** `KeyCommands.vb` (set 11 keys to `Keys.None`), `Form1.vb` (remove Modern "New Entry" menu)

---

## Phase 2: Callbook Lookup Library (QRZ + HamQTH)

### 2a. New `QrzLookup` project

Create `QrzLookup/QrzLookup.csproj` (SDK-style C# class library, `net8.0-windows`) modeled after existing `HamQTHLookup/HamQTHLookup.csproj`.

**New file:** `QrzLookup/QrzCallbookLookup.cs`
**Reference pattern:** `HamQTHLookup/lookup.cs` (326 lines — session auth, background thread, event callback, in-memory cache)

Key implementation details:
- Base URL: `https://xmldata.qrz.com/xml/current/`
- Login: `?username=XX&password=YY&agent=JJFlexRadio`
- Lookup: `?s=SESSIONKEY&callsign=XX1XXX`
- Use `HttpClient` (not deprecated `WebClient`)
- XML response models: `QRZDatabase` root → `Session` (Key, Error) + `Callsign` (call, fname, name, addr2, state, country, grid, lat, lon, cqzone, ituzone, lotw, eqsl)
- Session key caching, auto-re-login on "Session Timeout" / "Invalid session key"
- In-memory callsign cache (`Dictionary<string, QRZDatabase>`) for repeat lookups
- Background `Thread` for network calls (same pattern as HamQTH)
- Event callback: `CallsignSearchEvent` delegate
- Max 3 login failures before giving up (silent — no error dialogs)
- `CanLookup` property checks credentials + login state

**Files:**
- **Create:** `QrzLookup/QrzLookup.csproj`, `QrzLookup/QrzCallbookLookup.cs`
- **Modify:** `JJFlexRadio.sln` (add project), `JJFlexRadio.vbproj` (add `<ProjectReference>`)

### 2b. Unified callbook result class

Both QRZ and HamQTH return similar data. Create a simple shared result class so LogPanel doesn't care which source provided the data:

```vb
Public Class CallbookResult
    Public Property Source As String       ' "QRZ" or "HamQTH"
    Public Property Name As String         ' First name / handle (ham standard)
    Public Property QTH As String          ' City
    Public Property State As String
    Public Property Grid As String
    Public Property Country As String
End Class
```

**IMPORTANT — Ham standard for Name field:** Use `fname` (first name) only, NOT full name. Hams exchange first names (handles) on the air. QRZ's `fname` field is what goes in the Name box. The `name` field (last name) is NOT used for the log Name field.

---

## Phase 3: Credentials & Settings UI

### 3a. PersonalData fields

Add to `PersonalData.vb` class `personal_v1`:

```vb
Public QrzUsername As String = ""
Public QrzPassword As String = ""
Public CallbookLookupSource As String = "None"   ' "QRZ", "HamQTH", or "None"
```

Update the copy constructor (`New(p As personal_v1)`) to include these fields.

### 3b. PersonalInfo form UI

Add to `PersonalInfo.vb` / `PersonalInfo.Designer.vb`:
- **ComboBox** `CallbookSourceCombo` — choices: "None", "QRZ", "HamQTH" (AccessibleName = "Callbook lookup source")
- **TextBox** `CallbookUsernameBox` (AccessibleName = "Callbook username")
- **TextBox** `CallbookPasswordBox` (AccessibleName = "Callbook password", `UseSystemPasswordChar = True`)
- Show/hide or enable/disable username/password based on selected source ("None" = disabled)
- Load/save in `PersonalInfo_Load` / `OKButton_Click`

One set of credentials for whichever service the operator chooses. Switching source in settings takes effect next time Logging Mode is entered.

**Files:**
- **Modify:** `PersonalData.vb`, `PersonalInfo.vb`, `PersonalInfo.Designer.vb`

---

## Phase 4: Callbook Integration into LogPanel

### 4a. Lookup trigger in `CallSignBox_Leave`

In `LogPanel.vb`, after existing `CheckDup()` and `ShowPreviousContact()` calls:

```vb
' Callbook lookup (async — fills empty fields when results arrive)
If callText <> lastLookedUpCall AndAlso callbookLookup IsNot Nothing Then
    callbookLookup.LookupCall(callText)
End If
lastLookedUpCall = callText
```

**Priority chain:**
1. Local call index (synchronous, instant) — fills Name, QTH from previous contacts
2. Callbook lookup (async network) — fills Name, QTH, State, Grid into **empty fields only**
3. User input always wins (never overwrite what operator typed)

### 4b. Result handler with UI-thread marshaling

```vb
Private Sub CallbookLookupHandler(result As CallbookResult)
    If Me.InvokeRequired Then
        Me.BeginInvoke(Sub() CallbookLookupHandler(result))
        Return
    End If

    Dim filled As New List(Of String)
    ' Fill only empty fields (local data + user input takes priority)
    If FillIfEmpty(NameBox, result.Name) Then filled.Add("Name")
    If FillIfEmpty(QTHBox, result.QTH) Then filled.Add("QTH")
    If FillIfEmpty(StateBox, result.State) Then filled.Add("State")
    If FillIfEmpty(GridBox, result.Grid) Then filled.Add("Grid")

    ' Brief SR announcement: "QRZ: Name, QTH, Grid"
    If filled.Count > 0 Then
        ScreenReaderOutput.Speak(result.Source & ": " & String.Join(", ", filled), True)
    End If
End Sub
```

### 4c. Initialize from Form1

In `EnterLoggingMode()` (Form1.vb), after `LoggingLogPanel.Initialize(...)`:
- Check `CurrentOp.CallbookLookupSource`
- If "QRZ": create `QrzCallbookLookup` with QRZ credentials, wire event
- If "HamQTH": use existing `CallbookLookup` class with HamQTH credentials, wire event
- If "None": skip (no lookup)

Cleanup in `ExitLoggingMode()`: call `FinishCallbookLookup()`.

**Files:**
- **Modify:** `LogPanel.vb`, `Form1.vb`

---

## Phase 5: Full Log Access from Logging Mode (Ctrl+Alt+L)

### 5a. Open JJ's LogEntry form from Logging Mode

Ctrl+Alt+L opens the existing `LogEntry` form as a modal dialog while in Logging Mode. This gives operators access to all fields, searching, navigation — everything the full form offers.

**In Form1.vb `ProcessCmdKey`:**

```vb
' Ctrl+Alt+L — open full LogEntry form (only in Logging Mode)
If keyData = (Keys.Control Or Keys.Alt Or Keys.L) Then
    If ActiveUIMode = UIMode.Logging Then
        OpenFullLogFromLoggingMode()
    End If
    Return True
End If
```

**`OpenFullLogFromLoggingMode()` implementation:**

```vb
Private Sub OpenFullLogFromLoggingMode()
    ' 1. Save quick-entry state
    If LoggingLogPanel IsNot Nothing Then LoggingLogPanel.SaveState()

    ' 2. Announce
    ScreenReaderOutput.Speak("Opening full log entry", True)

    ' 3. Open LogEntry form (modal — blocks until closed)
    '    LogEntry is already wired to the active ContactLog session
    LogEntry.ShowDialog()

    ' 4. On return, restore quick-entry state and refresh grid
    If LoggingLogPanel IsNot Nothing Then
        LoggingLogPanel.RestoreState()
        LoggingLogPanel.LoadRecentQSOs()   ' Refresh in case entries were added/modified
        LoggingLogPanel.FocusCallSign()
    End If

    ScreenReaderOutput.Speak("Returned to logging mode", True)
End Sub
```

### 5b. Add menu item

In `BuildLoggingMenus()`, add to Log menu:
- "Full Log Entry    Ctrl+Alt+L" — calls `OpenFullLogFromLoggingMode()`

### 5c. State preservation

- LogPanel.SaveState() already captures all field values (Call, RST, Name, QTH, State, Grid, Comments)
- LogPanel.RestoreState() restores them on return
- RecentGrid refreshed via LoadRecentQSOs() in case operator added entries in the full form

**Files:**
- **Modify:** `Form1.vb` (ProcessCmdKey, new method, BuildLoggingMenus)

---

## Phase 6: Parking Lot Cleanup

### 6a. Reset "Don't ask me again" menu item

In `BuildLoggingMenus()`, add to Log menu:
- "Reset Confirmations" — resets `CurrentOp.SuppressClearConfirm = False`, persists, SR announces "Confirmation dialogs restored"

### 6b. NVDA Radio pane limitation

Document as known limitation:
- JAWS reads "Radio pane, Frequency..." from AccessibleName — works correctly
- NVDA doesn't announce the prefix due to UIA processing differences
- Existing `FreqBox.Enter` handler speaks "Radio pane" via Tolk as workaround
- Note in changelog and future help docs

### 6c. Tests 45-46 documentation

Document as pre-existing behavior: Ctrl+Shift+M blocked while Classic/Modern LogEntry form is open. Not a Sprint 4/5 regression.

### 6d. Recent QSOs grid fix (if needed from pre-sprint retest)

If retest R2 reveals row indexing issues (e.g., JAWS says "Row 2" for first data row because column headers count as Row 1), fix by adjusting the DataGridView accessibility or column header behavior.

### 6e. Retest R3: 20-entry grid auto-scroll

Test the Recent QSOs grid with 20 entries, then log a 21st QSO. Verify:
- Oldest entry is removed (grid stays at MaxRecentQSOs)
- New entry appears at bottom
- Grid auto-scrolls to show the new entry

This was deferred from pre-sprint retest because we only had 5 entries at the time.

### 6f. Fix Modern menu "coming soon" stubs (BUG-001)

In `Form1.vb` `AddModernStubItem()`, fix the stub items so both JAWS and NVDA properly announce placeholder state:

- Disable the item: `.Enabled = False` (grayed out, can't click)
- Set accessible description: `.AccessibleDescription = "Coming soon. Use Classic mode for full features."`
- On click (if we keep them enabled): play a system beep + speak a friendly message like "This feature is coming soon. For now, try Classic mode — it has everything you need!"
- Remove the existing silent Tolk-only speech that JAWS misses

This is a quick fix — just modifying the `AddModernStubItem()` function (~5 lines changed).

**Files:**
- **Modify:** `Form1.vb` (AddModernStubItem, menu item, Reset Confirmations), `docs/CHANGELOG.md`, possibly `LogPanel.vb` (grid fix)

---

## Phase 7: Polish, Testing & Release

### 7a. Version bump to 4.1.13
**DEFERRED** — accumulating more features before a version bump.

### 7b. Archive Sprint 4 ✅
Moved `docs/planning/agile/Sprint-04-Logging-Mode.md` → `docs/planning/agile/archive/`

### 7c. Changelog ✅
Updated `docs/CHANGELOG.md` — added "Unreleased" section for Sprint 5 work.

---

## Test Matrix

### A. Operator Profile — Callbook Settings & Credential Validation

| # | Test | Expected | Pass/Fail | Comments |
|---|------|----------|-----------|----------|
| A1 | Open PersonalInfo, tab to "Callbook lookup source" combo | Combo accessible as "Callbook lookup source", choices: None, QRZ, HamQTH | pass | |
| A2 | Select "None" — check username/password fields | Username and password fields disabled | pass | |
| A3 | Select "QRZ" — check username/password fields | Username and password fields enabled | pass | |
| A4 | Select "HamQTH" — check username/password fields | Username and password fields enabled | pass| |
| A5 | Tab to password field | Accessible as "Callbook password", characters masked (dots) | pass | |
| A6 | Enter valid QRZ credentials, click Update | Message confirms login, shows subscription expiry date | pass| |
| A7 | Enter wrong QRZ password, click Update | Error message mentions subscription requirement + QRZ URL, asks "Save anyway?" | pass | |
| A8 | Click "No" on "Save anyway?" | Returns to form, username field focused | pass | |
| A9 | Click "Yes" on "Save anyway?" | Saves despite failed validation, form closes | pass | |
| A10 | Enter valid HamQTH credentials, click Update | Message confirms successful login | pass | |
| A11 | Enter wrong HamQTH password, click Update | Error message from HamQTH, asks "Save anyway?" | pass | |
| A12 | Source=None, click Update (no credentials needed) | No validation attempt, saves normally | pass | |
| A13 | Reopen PersonalInfo after saving QRZ credentials | Source = QRZ, username preserved, password field shows masked text | pass | |
| A14 | Close and reopen app, check saved credentials | Credentials persisted (DPAPI encrypted in XML) | pass | |

### B. Callbook Lookup in Logging Mode (QRZ)

| # | Test | Expected | Pass/Fail | Comments |
|---|------|----------|-----------|----------|
| B1 | Configure QRZ in settings, enter Logging Mode | QRZ initialized (no visible indicator, but lookup works) | pass | |
| B2 | Type valid US callsign (W1AW), Tab out of call field | Empty fields fill with **first name** (not full name), QTH, State, Grid. SR speaks: "name, QTH" (actual values, no "QRZ:" prefix) | pass | |
| B3 | Type a DX callsign (e.g., G3XYZ), Tab out | Fields fill. SR speaks: "name, QTH, country" (country included because DX) | pass | |	
| B4 | Type a US callsign (domestic), Tab out | SR speaks: "name, QTH" (country NOT spoken — same country as operator) | pass| |
| B5 | Type callsign you've worked before, Tab out | Local fills Name/QTH first. SR announces previous contact info. Then QRZ fills remaining empty fields (State, Grid). SR speaks actual values | pass | |
| B6 | Type callsign, manually type Name first, then Tab out | QRZ does NOT overwrite Name. Fills remaining empty fields only | fail | I can't enter a name without tabbing which will ultimately use qrz to get a name. I think I can put in a name after the QRZ search though, not sure. |
| B7 | Invalid callsign (XXXXXX), Tab out | No error. No callbook announcement. Logging continues normally | pass | |
| B8 | Same callsign Tab-out twice | Second time uses cache (instant, no network call) | pass | |
| B9 | Network offline, type callsign, Tab out | Lookup silently fails. No crash. No error dialog. Previous contact still works	| pass | |
| B10 | Wrong QRZ password in settings, enter Logging Mode, Tab out of callsign | After 3 failed logins, stops trying. No error dialogs. Logging continues | pass, I think| I can't really test this because even with a wrong password, it works so that if it's an issue nothing changes i.e. no call is logged. Recommend that if qrz is unable to work but and QRZ fails, system should start using hamqth and the JJ provided login for searches. This should happen if qrz fails once, client should just automatically try hamqth. If system has to fall back to hamqth after three logins, stop trying qrz and show a dialog that says that hamqth is being used and that the user should fix a qrz password or set lookups to hamqth. I'm guessing that this would happen if sub expires. Check edge case for expired sub. If expired, switch to hamqth and let user know again but state qrz sub has expired. Recommend automatically setting and saving the call lookup setting to hamqth. Uesr notification could tell user that if they want qrz, they'll need to switch back to qrz with a fresh logon. |

### C. Callbook Lookup in Logging Mode (HamQTH)

| # | Test | Expected | Pass/Fail | Comments |
|---|------|----------|-----------|----------|
| C1 | Configure HamQTH in settings, enter Logging Mode | HamQTH initialized | I don't have a hamqth logon of my own. I know that JJ has a hardcoded hamqth, if no call and password is entered, hamqth should be used with jj's hard coded password | |
| C2 | Type valid callsign, Tab out | Fields fill. SR speaks: "name, QTH" (actual values, no "HamQTH:" prefix) | fail | see c1 |
| C3 | Type a DX callsign, Tab out | SR speaks: "name, QTH, country" (country because DX) | fail | see c1|
| C4 | Switch source from QRZ to HamQTH in settings, re-enter Logging Mode | New session uses HamQTH | pass |  since I don't have a hamqth logon I know it's free, I think it works correctly but since I can't use JJ's call, I can only say that when I switched from qrz to hamqth it didn't give a call. |
| C5 | Source=None in settings, Tab out of callsign | No lookup attempted. Previous contact still works | pass | |

### D. Station Lookup Dialog (Ctrl+L)

| # | Test | Expected | Pass/Fail | Comments |
|---|------|----------|-----------|----------|
| D1 | Press Ctrl+L in Logging Mode | Station Lookup dialog opens | pass | |
| D2 | Press Ctrl+L in Classic mode | Station Lookup dialog opens | fail | it should fail because ctrl+l and all login stuff has been removed from the interfaces. Since ctrl+l is a radio function, recommend removing ctrl+l into classic and modern and removing from log. |
| D3 | Press Ctrl+L in Modern mode | Station Lookup dialog opens | fail | see d2 |
| D4 | Type a valid US callsign, press Lookup (or Enter) | Name, QTH, State, Country, lat/long, CQ, ITU, GMT fields populate. SR speaks: "name, QTH" | pass | recommend adding distance to station and baring for beam or rotater directionality |
| D5 | Look up a DX callsign | SR speaks: "name, QTH, country" (country because DX) | pass | |
| D6 | Look up a domestic callsign | SR speaks: "name, QTH" (country NOT spoken) | pass | |
| D7 | Tab through result fields after lookup | Read-only fields accessible: name, QTH, state, country, lat/long, CQ zone, ITU zone, GMT offset | | pass | |
| D8 | Press Done (or Escape) to close dialog | Dialog closes, returns to previous context | pass | |
| D9 | Look up same callsign twice | Second lookup uses cache (instant) | pass | |
| D10 | With QRZ configured: lookup uses QRZ data | Results match QRZ data | pass | |
| D11 | With HamQTH configured: lookup uses HamQTH data | Results match HamQTH data | unknown | I don't have a hamqth logon, see previous hamqth fallback to using old JJ logon |
| D12 | With no callbook configured (Source=None): lookup uses built-in HamQTH | Still works using "JJRadio" built-in account | pass | |
| D13 | Look up invalid callsign | No crash. Country database may still return country from prefix. No callbook data | pass | |
| D14 | Station Lookup from Logging Mode Log menu → "Station Lookup" | Same as Ctrl+L | pass | |

### E. Full Log Access (Ctrl+Alt+L)

| # | Test | Expected | Pass/Fail | Comments |
|---|------|----------|-----------|----------|
| E1 | Ctrl+Alt+L in Logging Mode | SR: "Opening full log entry". LogEntry form opens | pass | |
| E2 | Close LogEntry form | SR: "Returned to logging mode". Focus on CallSign. Quick-entry fields preserved | fail | SR announces nothing |
| E3 | Add entry in LogEntry, close, check RecentGrid | New entry appears in Recent QSOs grid | pass | |
| E4 | Ctrl+Alt+L outside Logging Mode | Nothing happens (ignored in Classic/Modern) | pass | |
| E5 | Log menu → "Full Log Entry" | Same as Ctrl+Alt+L | pass | |
| E6 | Type partial data in LogPanel, Ctrl+Alt+L, close LogEntry | Partial data preserved in LogPanel fields | pass | |

### F. Classic/Modern Log Hotkey Removal

| # | Test | Expected | Pass/Fail | Comments |
|---|------|----------|-----------|----------|
| F1 | Ctrl+N in Classic mode | Does NOT open LogEntry form (hotkey removed) | pass | |
| F2 | Alt+C in Classic mode | Does NOT open LogEntry form (freed for future radio use) | pass |  pulls down the actions menu in classic |
| F3 | Alt+M in Classic mode | Does NOT open LogEntry form (freed) | pass | pulls up actions menu as well in classic |
| F4 | Alt+Q in Classic mode | Does NOT open LogEntry form (freed) | pass | pulls up actions menu again in classic. pulls up radio menu in modern. Seems to be effected by alt with letter pulling up first menu. |
| F5 | Alt+S in Classic mode | Does NOT open LogEntry form (freed) | pass | same |
| F6 | Alt+G in Classic mode | Does NOT open LogEntry form (freed) | pass | same |
| F7 | Alt+N in Classic mode | Does NOT open LogEntry form (freed) | pass | same |
| F8 | Alt+R in Classic mode | Does NOT open LogEntry form (freed) | pass | same |
| F9 | Alt+E in Classic mode | Does NOT open LogEntry form (freed) | pass | same |
| F10 | Ctrl+A in Classic mode | Does NOT open LogEntry form (freed) | pass | no extraneous menus or fields brought up for that one |
| F11 | Ctrl+H in Classic mode | Does NOT open LogEntry form (freed) | pass | same as above |
| F12 | Ctrl+Shift+F in Classic/Modern | Search log still works (kept) | pass | same as above |
| F13 | Ctrl+Shift+L from Classic/Modern | Enters Logging Mode (unchanged) | pass | |

### G. Parking Lot & BUG-001

| # | Test | Expected | Pass/Fail | Comments |
|---|------|----------|-----------|----------|
| G1 | In Logging Mode, check "Don't ask" on Escape-clear, then Log menu → Reset Confirmations | SR: "Confirmation dialogs restored". Next Escape shows dialog again | unknown | still not able to get a dialog when escape/clear either that or unable to re-set the ask checkbox. |
| G2 | Reset persists across restart | After reset + restart, Escape dialog appears | unknown | |
| G3 | Navigate Modern menu, land on a "coming soon" item with JAWS | JAWS announces item as disabled / "coming soon" or "unavailable" | unknown | Don turned his radio off so wasn't able to test tihs. Right now, I get no output though. Even on radio disconnect if a menu expands, everything should be grayed out / spoken. Disconnected, I get sr accessibility in radio menu, but no where else. |
| G4 | Same test with NVDA | NVDA announces same info | unknown | still reads blank accessibility for all menus but radio when disconnected |
| G5 | Press Enter on a disabled "coming soon" item | Nothing happens (item is disabled) | unknown | |

### H. Regression

| # | Test | Expected | Pass/Fail | Comments |
|---|------|----------|-----------|----------|
| H1 | Tab through LogPanel fields | Same order as Sprint 4: Call → RST Sent → RST Rcvd → Name → QTH → State → Grid → Comments | pass | |
| H2 | F6 pane switching | RadioPane ↔ LogPanel (unchanged, 2-stop) | pass | |
| H3 | Enter saves QSO with callbook-filled fields | All fields saved including auto-filled data | pass | |
| H4 | Dup checking still works | Dup beep on duplicate callsign | pass | |
| H5 | Ctrl+Shift+L enter/exit round-trip | State preserved, callbook-filled fields survive | pass | |
| H6 | Alt+F4 with unsaved entry | Save/discard/cancel dialog works | pass | |
| H7 | Arrow-key tuning in RadioPane | Up/Down step, Shift+Up/Down 10x, Left/Right change step size | pass | |
| H8 | Previous contact lookup (no callbook) | Tab out of call sign shows "Worked X times" if known | pass | |
| H9 | Recent QSOs grid shows logged entries | Grid updates after logging a QSO | pass | |
| H10 | Log a QSO when grid has 20 entries | Oldest removed, new at bottom, grid scrolls | PASS | R3 passed - grid auto-scrolls correctly  consider making items in recent qo to be configurable |

---

## Key Files Summary

| File | Changes |
|------|---------|
| `QrzLookup/QrzCallbookLookup.cs` | **NEW** — QRZ XML API client |
| `QrzLookup/QrzLookup.csproj` | **NEW** — Project file |
| `HamQTHLookup/lookup.cs` | Reference pattern (may need minor event signature updates for unified result) |
| `KeyCommands.vb` | Set 11 log-entry hotkeys to `Keys.None` |
| `LogPanel.vb` | Callbook trigger in CallSignBox_Leave, result handler, InitializeCallbook/FinishCallbook |
| `Form1.vb` | Ctrl+Alt+L handler, OpenFullLogFromLoggingMode, callbook init in EnterLoggingMode, menu items, remove Modern "New Entry" |
| `PersonalData.vb` | QrzUsername, QrzPassword, CallbookLookupSource fields |
| `PersonalInfo.vb` + `.Designer.vb` | Callbook source combo, username/password fields |
| `JJFlexRadio.sln` | Add QrzLookup project |
| `JJFlexRadio.vbproj` | Add QrzLookup ProjectReference, version bump |
| `My Project/AssemblyInfo.vb` | Version bump |
| `docs/CHANGELOG.md` | 4.1.13 entry |
| `docs/planning/agile/Sprint-04-Logging-Mode.md` | Move to archive |
