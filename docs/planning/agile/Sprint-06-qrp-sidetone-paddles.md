# Sprint 6: Bug Fixes, QRZ Logbook & Hotkey System

**Plan file:** `qrp-sidetone-paddles`
**Version target:** 4.1.13
**Predecessor:** Sprint 5 — QRZ/HamQTH Lookup & Full Log Access

## Context

Sprint 5 delivered callbook integration (QRZ/HamQTH lookup), credential management, Full Log Access, and logging mode refinements. Sprint 6 addresses outstanding bugs from Sprint 5 testing, adds QRZ Logbook upload/download integration, and overhauls the hotkey system with context-aware bindings and a Command Finder for discoverability.

---

## Branch Strategy

Sprint 6 uses feature branches for parallel development. Each track can be worked concurrently in separate CLI sessions and merged into main when stable.

```
main
 ├── sprint6/bug-fixes       (Track A: BUG-007, BUG-008, BUG-009)
 ├── sprint6/qrz-logbook     (Track B: QRZ Logbook API integration)
 └── sprint6/hotkeys          (Track C: Hotkey system overhaul)
```

**Merge order:** Track A (bug fixes) first, then Track B, then Track C. Bug fixes are smallest and least likely to conflict.

---

## Track A: Bug Fixes (branch: `sprint6/bug-fixes`)

### Phase A1: Callbook Graceful Degradation (BUG-007 + BUG-008)

**Goal:** Callbook lookups never silently fail. If the configured service fails, fall back gracefully.

#### A1a. HamQTH built-in account fallback (BUG-008)
- In LogPanel callbook initialization, when HamQTH is selected but no personal credentials exist, use the built-in "JJRadio" account (same as StationLookup already does)
- Check `CallbookService.cs` or wherever LogPanel creates its callbook client
- Match the pattern StationLookup uses for fallback

#### A1b. QRZ failure auto-fallback to HamQTH (BUG-007)
- On QRZ login failure (wrong password, expired subscription, network error):
  1. Attempt fallback to HamQTH (personal credentials first, then built-in "JJRadio")
  2. Track consecutive QRZ failures
  3. After 3 failed attempts, show one-time notification: "QRZ lookups are failing. Using HamQTH as fallback."
  4. Recommend user check QRZ subscription status
- Handle expired QRZ subscription as distinct case (QRZ returns specific error)

#### A1c. Test matrix
| # | Test | Expected |
|---|------|----------|
| 1 | QRZ selected, valid credentials | QRZ lookup works normally |
| 2 | QRZ selected, wrong password | Falls back to HamQTH, notification after 3 failures |
| 3 | QRZ selected, no credentials | Falls back to HamQTH immediately |
| 4 | HamQTH selected, no personal credentials | Uses built-in "JJRadio" account |
| 5 | HamQTH selected, valid personal credentials | Uses personal credentials |
| 6 | Both services unavailable (network down) | Graceful failure, no crash, user informed |

### Phase A2: Modern Menu Accessibility (BUG-009)

**Goal:** Modern mode menus speak properly with JAWS and NVDA.

#### A2a. Investigate Modern menu construction
- Find where Modern menus are built (likely `Form1.vb` or a dedicated Modern menu builder)
- Check how `AddModernStubItem()` creates items — this was the Sprint 5 "coming soon" fix
- Identify why submenus (not just stubs) are silent
- Check `AccessibleName`, `AccessibleRole`, `AccessibleDescription` on all Modern menu items and submenus

#### A2b. Fix Modern menu accessibility
- Set `AccessibleName` on all submenus and menu items
- Set `AccessibleDescription` where helpful (e.g., "coming soon" on stubs)
- Ensure parent menu items (Slice, Audio, etc.) announce themselves when opened
- Test with both JAWS and NVDA

#### A2c. Test matrix
| # | Test | Expected | JAWS | NVDA |
|---|------|----------|------|------|
| 1 | Open Radio menu, navigate items | Each item announced | | |
| 2 | Open Slice submenu | "Slice" announced, items listed | | |
| 3 | Open Audio submenu | "Audio" announced, items listed | | |
| 4 | Navigate to "coming soon" stub | "Coming soon" announced | | |
| 5 | Arrow through all Modern top-level menus | Each menu name announced | | |
| 6 | Compare with Classic mode menus | Similar announcement behavior | | |

---

## Track B: QRZ Logbook Integration (branch: `sprint6/qrz-logbook`)

### Phase B1: QRZ Logbook API Research & Settings

**Goal:** Add QRZ Logbook API key to Settings, understand API capabilities.

#### B1a. Research findings (pre-sprint — DONE)
- QRZ Logbook API endpoint: `https://logbook.qrz.com/api`
- Authentication: per-logbook API key (format `ABCD-0A0B-1C1D-2E2F`), separate from XML lookup username/password
- Requires `User-Agent` header (e.g., `JJFlexRadio/4.1.14`)
- Requires XML Logbook Data subscription ($35.95/yr) — same subscription needed for XML lookup
- Supported actions: INSERT (upload QSO), FETCH (download QSOs), DELETE, STATUS (logbook stats)
- INSERT accepts ADIF format, real-time single-QSO upload supported
- FETCH supports filtering by date range, band, mode, callsign, DXCC, confirmed status
- Response format: URL-encoded name=value pairs (not XML)

#### B1b. Settings UI additions
- Add to Settings → Callbook/Logging tab (or new "QRZ Logbook" tab):
  - **QRZ Logbook API Key** text field (masked, DPAPI-encrypted like existing credentials)
  - **"Log QSOs to QRZ"** checkbox (default unchecked)
  - **Validate** button — calls STATUS action to verify key works, displays logbook stats (total QSOs, confirmed, DXCC entities)
- Store API key in settings with DPAPI encryption (same pattern as QRZ/HamQTH passwords)

### Phase B2: QRZ Logbook Upload (Real-Time)

**Goal:** When "Log QSOs to QRZ" is enabled, each QSO logged in LogPanel is also posted to QRZ.

#### B2a. QRZ Logbook client class
- Create `QrzLogbookClient` (or add to existing QRZ service class)
- Methods: `UploadQsoAsync(adifRecord)`, `FetchQsosAsync(filters)`, `GetStatusAsync()`, `ValidateKeyAsync()`
- HTTP POST to `https://logbook.qrz.com/api` with `KEY=...&ACTION=...`
- Parse URL-encoded response, check for `RESULT=OK` vs `RESULT=FAIL&REASON=...`
- Handle: duplicate QSO, invalid key, expired subscription, network errors
- Set `User-Agent: JJFlexRadio/{version}`

#### B2b. Integration with LogPanel save
- After successful local QSO save, if QRZ logging enabled:
  1. Format QSO as ADIF string (required fields: `station_callsign`, `call`, `qso_date`, `time_on`, `band`, `mode`)
  2. Include optional fields: `freq`, `rst_sent`, `rst_rcvd`, `comment`, `name`, `gridsquare`, `qth`
  3. POST to QRZ asynchronously (don't block the UI)
  4. On success: brief screen reader announcement "Logged to QRZ"
  5. On failure: announce error, don't lose the local log entry
- Fire-and-forget with error notification — local log is always the source of truth

#### B2c. Test matrix
| # | Test | Expected |
|---|------|----------|
| 1 | Log QSO with QRZ logging enabled, valid key | QSO uploaded, "Logged to QRZ" announced |
| 2 | Log QSO with QRZ logging enabled, invalid key | Local log saved, error announced |
| 3 | Log QSO with QRZ logging disabled | Only local log, no QRZ call |
| 4 | Log QSO with no API key configured | Only local log, no error |
| 5 | Network failure during upload | Local log saved, error announced gracefully |
| 6 | Validate button with valid key | Shows logbook stats |
| 7 | Validate button with invalid key | Shows error message |

### Phase B3: QRZ Logbook Fetch & "Worked Before?" (Future — backlog)

**Note:** This phase is scoped but may be deferred to Sprint 7 depending on Sprint 6 velocity.

#### B3a. "Worked Before?" check
- On callsign lookup (tab out of Call Sign field), if QRZ logbook key is configured:
  - Query QRZ logbook: `ACTION=FETCH&OPTION=CALL:{callsign}`
  - If results found, announce "Worked before on {band} {date}" or "Worked {count} times"
  - Display in LogPanel info area alongside callbook data
- This is a nice-to-have that leverages FETCH capability

#### B3b. Logbook stats display
- STATUS action returns: total QSOs, confirmed QSOs, DXCC entities worked
- Show in Settings validation result
- Possibly show in a status area when entering Logging Mode

---

## Track C: Hotkey System Overhaul (branch: `sprint6/hotkeys`)

### Phase C1: Hotkey Registry with Context Scoping

**Goal:** Replace flat global key dictionary with a context-aware registry.

#### C1a. Define KeyScope enum
```vb
Public Enum KeyScope
    Global = 0      ' Active everywhere (e.g., Ctrl+Shift+L for Logging Mode)
    Radio = 1       ' Active in Classic + Modern modes (radio operations)
    Logging = 2     ' Active in Logging Mode only
End Enum
```

#### C1b. Extend key data model
- Add `Scope As KeyScope` property to `keyTbl` class (or its replacement)
- Add `Context As String` property for display grouping (e.g., "Slice", "Audio", "CW", "Logging", "General")
- Add `Description As String` — human-readable description for Command Finder search
- Add `Keywords As String()` — searchable keywords/synonyms

#### C1c. Refactor KeyDictionary
- Change from `Dictionary(Of Keys, keyTbl)` to support multi-scope resolution
- Key lookup: given a physical key + current UI mode, resolve to the correct command
  - If current mode is Logging → check Logging scope first, then Global
  - If current mode is Classic/Modern → check Radio scope first, then Global
- Backward compatibility: existing operator key configs (`KeyDefs.xml`) default all bindings to `Global` scope

#### C1d. Assign default scopes
- Review all ~75 commands and assign appropriate default scope:
  - `Global`: Ctrl+Shift+L (toggle Logging Mode), F1 (help), Escape, etc.
  - `Radio`: frequency adjustment, slice operations, filter changes, CW, audio
  - `Logging`: log field navigation (Alt+C for Call, Alt+S for State, etc.)
- This frees prime Alt+letter keys for radio use when not in Logging Mode

#### C1e. Update key persistence
- Extend `KeyConfigType_V1` (or create V2) to include scope per binding
- Migration: load V1 format, assign Global scope to all existing bindings, save as V2
- Store context/group metadata for Settings UI

### Phase C2: Hotkey Settings UI Redesign

**Goal:** Settings UI with tabs per context, showing grouped hotkeys that can be rebound.

#### C2a. Redesign DefineCommands dialog
- Replace flat list with tabbed layout:
  - **Global** tab — keys active everywhere
  - **Radio** tab — keys active in Classic/Modern (sub-grouped: Slice, Audio, CW, General)
  - **Logging** tab — keys active in Logging Mode
- Each tab shows: Command name | Current key | Description
- Select a command → press new key to rebind
- Conflict detection is now scope-aware:
  - Same key in Radio + Logging = OK (different contexts)
  - Same key in Radio + Global = CONFLICT (global overrides)
  - Same key in same scope = CONFLICT

#### C2b. Key recording UX
- "Press key to assign" modal — captures next keypress
- Shows current binding if key already assigned in this scope
- "Clear" button to unbind a command
- "Reset to Default" per command or per tab

#### C2c. Accessibility
- All tabs, lists, and dialogs fully screen-reader accessible
- `AccessibleName` on all controls
- Announce scope context when switching tabs
- Key recording dialog announces what was pressed

### Phase C3: Command Finder (Runtime Discovery)

**Goal:** An operator can press a hotkey to search all available commands by keyword.

#### C3a. Command Finder dialog
- Hotkey: F12 (or configurable) opens Command Finder
- Search box: type keywords to filter commands (searches name, description, keywords)
- Results list: shows matching commands with their current key binding and scope
- Press Enter on a result: executes the command immediately
- Fully accessible (screen reader announces results as you type)

#### C3b. Context-aware help
- New hotkey (e.g., Ctrl+/) shows all hotkeys for the current context
- In Radio mode: shows Radio + Global hotkeys
- In Logging mode: shows Logging + Global hotkeys
- Grouped by function group (Slice, Audio, CW, etc.)
- Could be a simplified Command Finder filtered to current scope

#### C3c. Test matrix
| # | Test | Expected |
|---|------|----------|
| 1 | Open Settings → Hotkeys → Radio tab | Shows radio commands grouped by function |
| 2 | Rebind a Radio command to Alt+C | Saves, works in Classic/Modern, doesn't interfere with Logging |
| 3 | Same key (Alt+C) used for Logging command | Both coexist, correct one fires per mode |
| 4 | Try to assign Global key that conflicts with Radio key | Conflict warning shown |
| 5 | F12 → type "slice" | Shows all slice-related commands |
| 6 | F12 → select "Next Slice" → Enter | Executes the command |
| 7 | Ctrl+/ in Radio mode | Shows Radio + Global hotkeys |
| 8 | Ctrl+/ in Logging mode | Shows Logging + Global hotkeys |
| 9 | Screen reader: navigate Command Finder | All elements announced properly |
| 10 | Reset to Default on a single command | Restores default key for that command |
| 11 | Legacy KeyDefs.xml loads correctly | All bindings import as Global scope |

---

## Radio-Independent Logging (backlog — not in Sprint 6 scope)

Deferred to Sprint 7. Concept: checkbox in LogPanel or Settings that says "Log for external radio" — skips `AutoFillFromRadio()`, makes freq/mode/band manually editable. Allows logging AllStar, 2m, or other non-Flex QSOs through the same LogPanel interface.

---

## Sprint 6 Summary

| Track | Branch | Phases | Dependencies |
|-------|--------|--------|--------------|
| A: Bug Fixes | `sprint6/bug-fixes` | A1 (callbook fallback), A2 (Modern menu a11y) | None — merge first |
| B: QRZ Logbook | `sprint6/qrz-logbook` | B1 (settings), B2 (upload) | None — merge second |
| C: Hotkeys | `sprint6/hotkeys` | C1 (registry), C2 (settings UI), C3 (command finder) | None — merge last (largest) |

**Phase B3** (QRZ logbook fetch / "worked before?") is scoped but may slip to Sprint 7.
**Radio-independent logging** deferred to Sprint 7.

---

## Version Bump

When all tracks merged and tested:
- Bump to **4.1.13** in both `JJFlexRadio.vbproj` and `My Project\AssemblyInfo.vb`
- Update `docs/CHANGELOG.md`
- Tag and release

---

## What Shipped — Change Summary

All three tracks have been merged to main and build clean (0 errors).

### Track A: Bug Fixes

**BUG-007 — QRZ callbook failure → HamQTH fallback**
- QRZ lookup now distinguishes three outcomes: (1) call found, (2) call not in QRZ database but session OK, (3) real auth/network failure.
- Only real failures count toward the 3-strike threshold that triggers fallback.
- After 3 consecutive real failures, announces: "QRZ lookups are failing. Using HamQTH as fallback."
- Files: `QrzLookup/QrzCallbookLookup.cs`, `LogPanel.vb`

**BUG-008 — HamQTH built-in account**
- When HamQTH is selected but operator has no personal credentials, falls back to built-in "JJRadio" account automatically.
- File: `LogPanel.vb`

**BUG-009 — Modern menu accessibility**
- `AccessibleName` added to all Modern mode top-level menus and submenus so JAWS/NVDA announce them.
- File: `Form1.vb`

### Track B: QRZ Logbook Upload

**New settings in Personal Information dialog:**
- "Log QSOs to QRZ Logbook" checkbox
- "QRZ Logbook API key" text field (masked with password dots, DPAPI-encrypted)
- "Validate" button — calls QRZ Logbook STATUS API, shows logbook stats or error

**Real-time upload:**
- When enabled, each QSO saved in Logging Mode is immediately uploaded to QRZ Logbook (fire-and-forget).
- Screen reader announces "Logged to QRZ" on success, error message on failure.
- Local log is always saved first — QRZ upload failure never loses a QSO.
- Circuit breaker: pauses uploads after 5 consecutive errors.
- Files: `QrzLookup/QrzLogbookClient.cs` (new), `LogPanel.vb`, `PersonalData.vb`, `PersonalInfo.vb`, `PersonalInfo.Designer.vb`

### Track C: Hotkey System Overhaul

**Scope-aware hotkeys:**
- New `KeyScope` enum: Global, Radio, Logging.
- Same physical key can have different commands per UI mode:
  - Alt+C = CW Zero Beat in Radio mode, Call Sign field in Logging mode
  - Alt+S = Start Scan in Radio mode, State field in Logging mode
  - Alt+R = Reverse Beacon in Radio, My RST in Logging mode
  - Alt+D = DX Cluster in Radio, DateTime in Logging mode
  - etc.
- Global keys (F1 help, F12 stop CW, Ctrl+L station lookup, Ctrl+/ command finder) work everywhere.

**CW message key migration:**
- F5-F11 CW messages automatically remapped to Ctrl+1 through Ctrl+7 on first load.
- Frees F-keys for future use. Migration is one-time and saved.

**New Settings UI (Tools → Configure Hotkeys):**
- Tabbed layout: Global, Radio, Logging tabs
- Each tab shows commands grouped by function (General, Audio, CW, Logging, etc.)
- Select a command → press a key to rebind
- Scope-aware conflict detection (same key in Radio + Logging = OK; same key in same scope = conflict)
- "Reset Selected" and "Reset All in Tab" buttons

**New Command Finder dialog:**
- Opened via **Ctrl+/** (slash on the forward-slash key)
- Searches all commands by name, description, keywords, group, and menu text
- Pre-filtered to current scope (Radio mode shows Radio+Global; Logging shows Logging+Global)
- Select a result and press Enter (or double-click) to execute that command immediately
- Fully accessible: announces result count as you type

---

## Hotkey Quick Reference

### Global Hotkeys (work everywhere)

| Key | Command |
|-----|---------|
| F1 | Show help |
| F12 | Stop sending CW |
| Ctrl+L | Station lookup |
| Ctrl+/ | Open Command Finder |

### Radio Hotkeys (Classic + Modern modes)

| Key | Command |
|-----|---------|
| F2 | Show frequency / pause scan |
| Shift+F2 | Resume scan |
| F3 | Show received |
| F4 | Show send |
| Ctrl+F4 | Show send direct |
| Ctrl+F | Enter frequency |
| Ctrl+M | Show memory |
| Ctrl+Shift+M | Memory scan |
| Alt+C | CW zero beat |
| Ctrl+Shift+C | Clear RIT |
| Alt+S | Start scan |
| Alt+D | DX Cluster |
| Alt+R | Reverse Beacon |
| Ctrl+P | Panning |
| Ctrl+Shift+U | Saved scan |
| Ctrl+Z | Stop scan |
| Alt+PageUp | Audio gain up |
| Alt+PageDown | Audio gain down |
| Alt+Shift+PageUp | Headphones up |
| Alt+Shift+PageDown | Headphones down |
| Shift+PageUp | Line out up |
| Shift+PageDown | Line out down |
| Ctrl+F9 | Toggle 1 |
| Ctrl+1 through Ctrl+7 | CW messages 1-7 (migrated from F5-F11) |

### Logging Hotkeys (Logging Mode only)

| Key | Command |
|-----|---------|
| Alt+C | Call sign field |
| Alt+T | His RST |
| Alt+R | My RST |
| Alt+N | Name |
| Alt+Q | QTH |
| Alt+S | State |
| Alt+G | Grid |
| Alt+E | Comments |
| Alt+D | Date/Time |
| Ctrl+W | Write (save) entry |
| Ctrl+N | New entry |
| Ctrl+Shift+F | Search log |
| Ctrl+Shift+T | Log stats |
| F6 | Switch between radio and log panes |
| Ctrl+Shift+N | Log characteristics dialog |
| Ctrl+Alt+L | Open full log entry form |

---

## Step-by-Step Test Procedures

### Test Setup

1. Build: `dotnet clean JJFlexRadio.sln -c Release -p:Platform=x64 && dotnet build JJFlexRadio.sln -c Release -p:Platform=x64`
2. Run the exe from `bin\x64\Release\net8.0-windows\win-x64\JJFlexRadio.exe`
3. Connect to a radio (or use without radio for some tests)
4. Have JAWS or NVDA running for accessibility tests

### A. Callbook Fallback Tests

**A1. QRZ with valid credentials**
1. Settings → Personal Info → Callbook: QRZ, enter valid username/password → OK
2. Enter Logging Mode (Ctrl+Shift+L)
3. Type a known callsign (e.g. W1AW) and Tab out of Call field
4. **Expected:** Name, QTH, Grid populate from QRZ. Screen reader says the callsign data.

**A2. QRZ "not found" — supplemental HamQTH (no failure counted)**
1. Same QRZ setup as A1
2. Type a callsign that's NOT in QRZ (e.g. a very new or vanity call)
3. Tab out of Call field
4. **Expected:** No crash. If HamQTH has it, those fields fill in. No "QRZ failing" warning — this is not a failure.

**A3. QRZ auth failure → HamQTH fallback**
1. Settings → Personal Info → Callbook: QRZ, enter **wrong** password → OK
2. Enter Logging Mode
3. Type a callsign and Tab out — repeat 3+ times
4. **Expected:** After 3 failures, hear "QRZ lookups are failing. Using HamQTH as fallback." Lookups continue via HamQTH.

**A4. HamQTH with no personal credentials**
1. Settings → Personal Info → Callbook: HamQTH, leave username/password blank → OK
2. Enter Logging Mode, type a callsign, Tab out
3. **Expected:** Lookup works using built-in "JJRadio" account. Fields populate.

### B. Modern Menu Accessibility

**B1. Modern menus speak with screen reader**
1. Switch to Modern mode if not already
2. Press Alt to activate menu bar
3. Arrow through top-level menus (Radio, Slice, Audio, etc.)
4. **Expected:** Each menu name is announced by JAWS/NVDA
5. Open a submenu (e.g. Radio → arrow down)
6. **Expected:** Items within the submenu are announced
7. Navigate to any "coming soon" stub items
8. **Expected:** "Coming soon" is announced

### C. QRZ Logbook Upload

**C1. Settings — enter and validate API key**
1. Settings → Personal Info
2. Check "Log QSOs to QRZ Logbook"
3. Enter your QRZ Logbook API key (from qrz.com → Logbook → Settings → API)
4. Press "Validate" button
5. **Expected:** Message box shows logbook stats (total QSOs, confirmed, DXCC entities) — or error if key is bad.

**C2. Real-time upload on QSO save**
1. With QRZ Logbook enabled and valid key from C1
2. Enter Logging Mode, fill in a QSO (call, RST, etc.)
3. Press Ctrl+W to save
4. **Expected:** Hear "Saved {callsign}" then shortly after "Logged to QRZ"
5. Verify on qrz.com → Logbook that the QSO appeared

**C3. QRZ upload with invalid key**
1. Enter an invalid API key, save a QSO
2. **Expected:** Local log saves normally ("Saved {callsign}"). Then hear "QRZ upload failed: {error}". QSO is NOT lost locally.

**C4. QRZ logging disabled**
1. Uncheck "Log QSOs to QRZ Logbook"
2. Save a QSO
3. **Expected:** Normal local save only, no QRZ announcement at all

### D. Hotkey Scoping

**D1. Same key, different modes**
1. In Radio mode (Classic or Modern), press Alt+C
2. **Expected:** CW Zero Beat command fires
3. Enter Logging Mode (Ctrl+Shift+L), press Alt+C
4. **Expected:** Focus jumps to Call Sign field (not CW zero beat)

**D2. Alt+S scope test**
1. Radio mode: Alt+S → **Expected:** Start Scan
2. Logging mode: Alt+S → **Expected:** Focus to State field

**D3. Global keys work in both modes**
1. Radio mode: press F1 → **Expected:** Help
2. Logging mode: press F1 → **Expected:** Help (same)
3. Radio mode: Ctrl+/ → **Expected:** Command Finder opens
4. Logging mode: Ctrl+/ → **Expected:** Command Finder opens

**D4. CW messages on Ctrl+number**
1. Verify Ctrl+1 sends first CW message (was F5 before migration)
2. F5 should now be unbound (unless re-assigned)

### E. Hotkey Settings UI

**E1. Open and navigate**
1. Tools → Configure Hotkeys (or however it's wired in the menu)
2. **Expected:** Dialog opens with three tabs: Global, Radio, Logging
3. Tab through — screen reader announces tab names and list contents

**E2. Rebind a key**
1. Select a command in the Radio tab
2. Press a new key combination in the key-capture field
3. **Expected:** New key shown, saved when OK pressed
4. Test the rebound key in Radio mode — **Expected:** fires the newly assigned command

**E3. Scope-aware conflict detection**
1. Try assigning Alt+C in the Radio tab (already assigned to CW Zero Beat)
2. **Expected:** Conflict warning shown
3. Try assigning Alt+C in the Logging tab (already assigned to Call Sign)
4. **Expected:** Conflict warning shown
5. Assign a key used in Radio tab to a Logging tab command
6. **Expected:** NO conflict — different scopes

**E4. Reset buttons**
1. Change a key binding, then press "Reset Selected"
2. **Expected:** That single key reverts to default
3. Press "Reset All in Tab"
4. **Expected:** All keys in that tab revert to defaults

### F. Command Finder

**F1. Open and search**
1. Press Ctrl+/ (forward slash)
2. **Expected:** Command Finder dialog opens, focus in search box
3. Type "slice" — **Expected:** results filter to slice-related commands, hear "{N} results"
4. Type "freq" — **Expected:** results show frequency-related commands

**F2. Execute from finder**
1. Open Command Finder (Ctrl+/)
2. Type "help", arrow down to "Show keys help", press Enter
3. **Expected:** Command Finder closes, help command executes

**F3. Scope filtering**
1. In Radio mode, open Ctrl+/ → **Expected:** Shows Radio + Global commands (no Logging-only commands)
2. In Logging mode, open Ctrl+/ → **Expected:** Shows Logging + Global commands (no Radio-only commands)

**F4. Accessibility**
1. Open Command Finder with screen reader running
2. **Expected:** "Command Finder" dialog announced
3. Type in search box → **Expected:** result count announced after each keystroke
4. Arrow through results → **Expected:** Command name, key, scope announced per row

### G. Regression Tests

**G1. Logging Mode end-to-end**
1. Enter Logging Mode, fill in a full QSO, save with Ctrl+W
2. **Expected:** All fields work, QSO saved to log, "Saved" announced

**G2. Classic/Modern mode basic operation**
1. Check that existing radio hotkeys (F2, F3, F4, Ctrl+F, etc.) still work as before

**G3. CW operation**
1. Verify CW messages send on Ctrl+1 through Ctrl+7
2. Verify F12 stops CW sending

**G4. Legacy KeyDefs.xml**
1. If upgrading from a previous version, existing custom key bindings should load and work
2. All imported keys default to Global scope (backward compatible)

---

## Test Results

| # | Test | Result | Notes |
|---|------|--------|-------|
| A1 | QRZ valid creds | | |
| A2 | QRZ not-found (no failure) | | |
| A3 | QRZ auth fail → HamQTH fallback | | |
| A4 | HamQTH no creds → built-in | | |
| B1 | Modern menus speak (JAWS) | | |
| B1 | Modern menus speak (NVDA) | | |
| C1 | QRZ Logbook validate key | | |
| C2 | QRZ real-time upload | | |
| C3 | QRZ upload invalid key | | |
| C4 | QRZ logging disabled | | |
| D1 | Alt+C scope (Radio vs Logging) | | |
| D2 | Alt+S scope (Radio vs Logging) | | |
| D3 | Global keys in both modes | | |
| D4 | CW messages on Ctrl+number | | |
| E1 | Hotkey Settings UI opens/tabs | | |
| E2 | Rebind a key | | |
| E3 | Scope conflict detection | | |
| E4 | Reset buttons | | |
| F1 | Command Finder search | | |
| F2 | Execute from finder | | |
| F3 | Scope filtering | | |
| F4 | Command Finder accessibility | | |
| G1 | Logging end-to-end | | |
| G2 | Classic/Modern hotkeys | | |
| G3 | CW operation | | |
| G4 | Legacy KeyDefs.xml | | |

## Test Plan (Post-Merge Integration)

After all three tracks merge into main:

| # | Area | Test |
|---|------|------|
| 1 | Callbook fallback | QRZ failure → HamQTH fallback works |
| 2 | Callbook fallback | HamQTH no-credentials → built-in account works |
| 3 | Modern menus | All menus speak in JAWS and NVDA |
| 4 | QRZ upload | QSO logged → uploaded to QRZ in real-time |
| 5 | QRZ settings | API key validation shows logbook stats |
| 6 | Hotkeys | Context scoping — same key, different modes |
| 7 | Hotkeys | Settings UI — rebind, conflict detection, reset |
| 8 | Command Finder | Search by keyword, execute from results |
| 9 | Regression | Logging Mode still works end-to-end |
| 10 | Regression | Classic/Modern mode hotkeys unchanged |
| 11 | Regression | CW messages still work |
| 12 | Build | Clean build produces fresh installer |
