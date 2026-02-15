# Sprint 7: WPF Migration, Station Lookup, Status & Polish

**Plan file:** `pileup-ragchew-barefoot`
**Version target:** 4.1.13 (accumulating — ship when ready for Don)
**Predecessor:** Sprint 6 — Bug Fixes, QRZ Logbook & Hotkey System

## Context

Sprint 6 delivered the context-aware hotkey system, QRZ Logbook upload, and a wave of bug fixes (BUG-007 through BUG-014). Sprint 7 is our most ambitious yet: five parallel tracks covering WPF migration, new features, and polish. Each track runs in its own git worktree — we learned the hard way in Sprint 6 that shared working directories cause collisions.

---

## Branch Strategy

Five feature branches, each in its own worktree. Main repo stays on `main`.

```
main (primary worktree — C:\dev\JJFlex-NG)
 ├── sprint7/bug-fixes       (Track A: polish & bug fixes)
 ├── sprint7/wpf-migration   (Track B: LogPanel + Station Lookup → WPF)
 ├── sprint7/station-lookup   (Track C: Log Contact + distance/bearing)
 ├── sprint7/status           (Track D: Plain-English Status)
 └── sprint7/qso-grid-config  (Track E: configurable Recent QSOs size)
```

### Worktree Setup (run from main repo)

```batch
git branch sprint7/bug-fixes
git branch sprint7/wpf-migration
git branch sprint7/station-lookup
git branch sprint7/status
git branch sprint7/qso-grid-config

git worktree add ../jjflex-trackA sprint7/bug-fixes
git worktree add ../jjflex-trackB sprint7/wpf-migration
git worktree add ../jjflex-trackC sprint7/station-lookup
git worktree add ../jjflex-trackD sprint7/status
git worktree add ../jjflex-trackE sprint7/qso-grid-config
```

### Merge Order

```
Track A (bug fixes) → Track E (small) → Track B (WPF) → Track C (Station Lookup) → Track D (Status)
```

Track C depends on Track B (Station Lookup WPF conversion), so it merges after B. Track D is self-contained and merges last.

### Build Policy Per Track

| Track | Build in worktree? | Notes |
|-------|-------------------|-------|
| A | Yes | Small fixes, quick verify |
| B | Yes | Must compile clean — big structural changes |
| C | No — depends on Track B | Builds after merge onto B's WPF shell |
| D | Yes | Self-contained, no WPF dependency |
| E | Yes | Small, self-contained |

---

## Track A: Bug Fixes & Polish (branch: `sprint7/bug-fixes`)

**Goal:** Fix outstanding bugs, polish screen reader experience, update changelog.

### A1. BUG-015: F6 double-announces "Radio pane"
- **File:** `RadioPane.vb` line ~209
- **Fix:** Remove `Speak("Radio pane")` from FreqBox Enter handler. The `AccessibleName` ("Radio pane, Frequency...") already announces via screen reader naturally.
- **Test:** F6 in Logging Mode → should hear "Radio pane, Frequency..." exactly once.

### A2. BUG-013: Duplicate QSO beep not audible
- **Investigate:** Find where the dup beep is triggered in LogPanel. Check if `SystemSounds.Beep` or `Console.Beep` is used, whether it plays through the correct audio device.
- **Fix:** TBD after investigation.
- **Test:** Log a duplicate QSO → should hear both beep AND "worked x calls" speech.

### A3. CW hotkey feedback
- **Files:** `KeyCommands.vb` (CW message routines)
- **Fix:** When Ctrl+1-7 or F12 is pressed but no CW messages are configured, speak a short message: "No CW messages configured" or similar.
- **Test:** Press Ctrl+1 with no CW messages → hear feedback. Press F12 → hear feedback.

### A4. Modern menu "coming soon" stubs
- **Review:** Which "coming soon" stubs can now be wired to real functionality?
- **Discuss with user:** Which stubs to fill vs. leave as "coming soon"
- **Candidates:** Slice menu items may stay as stubs (Sprint 8). Filter/DSP items may be wirable now.

### A5. Changelog update
- **File:** `docs/CHANGELOG.md`
- **Content:** Sprint 6 changes (hotkey system, QRZ Logbook, BUG-007 through BUG-014, Command Finder, etc.)
- **Style:** First-person explanatory tone per CLAUDE.md conventions.

---

## Track B: WPF Migration (branch: `sprint7/wpf-migration`)

**Goal:** Convert LogPanel and Station Lookup from WinForms to WPF for better native UI Automation support.

### Why WPF?
- WinForms uses MSAA accessibility bridge — causes quirks (DataGridView row-0 indexing, manual AccessibleName needed everywhere)
- WPF has native UIA support — AutomationProperties work automatically
- JAWS/NVDA work more reliably with WPF controls

### B1. LogPanel → WPF
- Create WPF UserControl equivalent of `LogPanel.vb`
- Host in WinForms via `ElementHost` in the existing SplitContainer
- Fields: Call, RST Sent/Rcvd, Name, QTH, State, Grid, Comments
- Set `AutomationProperties.Name` on all fields
- WPF DataGrid for Recent QSOs (gets 1-based row indexing for free — but row-0 fix is Sprint 8 scope)
- Preserve all existing behavior: dup checking, callbook lookup, auto-fill, state save/restore
- Tab order must match current: Call(0) → RST Sent(1) → RST Rcvd(2) → Name(3) → QTH(4) → State(5) → Grid(6) → Comments(7)
- Focus management across WinForms↔WPF boundary (ElementHost quirks)

### B2. Station Lookup → WPF
- Convert `StationLookup.vb` to WPF Window (standalone, not ElementHost)
- Preserve: callbook lookup, DX detection, screen reader announcements
- Add AutomationProperties to all controls
- This becomes the shell that Track C adds features to

### B3. Testing
- JAWS: all controls announce, grid rows navigable, tab order correct
- NVDA: same verification
- Focus: F6 pane switching still works with WPF LogPanel in ElementHost
- Regression: existing hotkeys, mode switching, QSO logging all still work

### Acceptance Criteria
- [ ] WPF LogPanel with AutomationProperties on all controls
- [ ] WPF Station Lookup with AutomationProperties
- [ ] JAWS announces all controls correctly
- [ ] NVDA announces all controls correctly
- [ ] Tab order logical, focus management correct across WinForms↔WPF boundary
- [ ] No regression in existing functionality
- [ ] Builds clean with no new warnings

---

## Track C: Station Lookup Enhancements (branch: `sprint7/station-lookup`)

**Goal:** Add DX workflow features to Station Lookup.

**Dependency:** Track B must merge first (provides the WPF Station Lookup shell).

### C1. "Log Contact" button
- Add button to Station Lookup dialog: "Log Contact" (or "Log a QSO with [callsign]")
- On click:
  1. Close Station Lookup dialog
  2. Enter Logging Mode (call `EnterLoggingMode()`)
  3. Pre-fill LogPanel fields: callsign, name, QTH, grid from lookup result
- Screen reader: announce "Entering Logging Mode with [callsign] pre-filled"
- Hotkey: consider Ctrl+Enter or a dedicated key

### C2. Distance and bearing
- Calculate great-circle distance and bearing from operator's grid square to station's grid square
- Display in Station Lookup results
- Screen reader: include in announcement ("245 miles, bearing 035 degrees")
- Need operator's grid square from `PersonalData` or `CurrentOp`
- Maidenhead grid → lat/lon conversion needed (standard formula)
- Haversine formula for distance, initial bearing calculation

### C3. Testing
- Look up a known station → verify distance/bearing are reasonable
- Click "Log Contact" → verify Logging Mode opens with fields pre-filled
- Screen reader announces all new information

---

## Track D: Plain-English Status (branch: `sprint7/status`)

**Goal:** Replace the cryptic radio status with human-readable spoken and visual summaries.

**Design decision needed:** Discuss ActiveSlice rules with user before coding.

### D1. Speak Status (hotkey: TBD, suggest Ctrl+S or dedicated key)
- Concise spoken summary of current radio state
- Example: "Listening on 14.250 megahertz, USB, 20 meter band, slice A"
- Include: frequency, mode, band, active slice, TX state, signal strength if available
- Should be short enough to speak in ~3 seconds

### D2. Status Dialog (hotkey: TBD)
- Accessible dialog showing full radio status
- Organized sections: Frequency/Mode, Slice info, TX status, Signal levels, Audio routing
- All fields as read-only text with AutomationProperties
- Tab-navigable for screen reader users
- Consider making this a WPF Window (new forms should be WPF per CLAUDE.md rules)

### D3. ActiveSlice awareness
- **Needs design discussion:** What is the "active slice"? How does it change?
- Current FlexLib: `Radio.ActiveSlice` property exists
- Need to define: when does JJFlexRadio track slice changes? What triggers announcements?
- This affects Speak Status content and future Slice Menu (Sprint 8)

### D4. Testing
- Speak Status with radio connected → hear correct frequency/mode/band
- Speak Status with no radio → hear "No radio connected" or similar
- Status Dialog opens, all fields readable by screen reader
- Tab through dialog fields → all announced correctly

---

## Track E: Configurable Recent QSOs Grid Size (branch: `sprint7/qso-grid-config`)

**Goal:** Let operators choose how many recent QSOs appear in the Logging Mode grid.

### E1. Setting
- Add `RecentQsoCount` field to `PersonalData.personal_v1` (default: 20, range: 5-100)
- Add UI control in PersonalInfo settings (numeric up/down or text field with validation)
- Accessible: label + screen reader announcement

### E2. Apply to LogPanel
- `LogPanel.vb` currently hardcodes `20` — replace with `CurrentOp.RecentQsoCount`
- Grid reload on setting change (or on next Logging Mode entry)

### E3. Testing
- Change setting to 10 → enter Logging Mode → grid shows 10 QSOs
- Change setting to 50 → grid shows 50 (if enough QSOs exist)
- Default (20) still works for new operators

---

## Testing Strategy

Each track has its own test section above. After all tracks merge:

### Integration Tests
- Full QSO logging workflow with WPF LogPanel
- Station Lookup → Log Contact → QSO logged → appears in grid
- Speak Status after QSO logged → reflects updated state
- Mode switching (Classic ↔ Modern ↔ Logging) with all new features
- Hotkeys still work after WPF migration (ProcessCmdKey + ElementHost)

### Screen Reader Matrix
| Feature | JAWS | NVDA |
|---------|------|------|
| WPF LogPanel fields | | |
| WPF LogPanel grid | | |
| WPF Station Lookup | | |
| Log Contact workflow | | |
| Distance/bearing announcement | | |
| Speak Status | | |
| Status Dialog | | |
| F6 pane switching (no double-announce) | | |
| CW feedback messages | | |

---

## Sprint 8 Preview (not committed)

- WPF: Command Finder + DefineCommands
- Row-0 → row-1 indexing fix (WPF DataGrid)
- Slice Menu implementation (big feature — filling Modern menu stubs)
- Configurable Recent QSOs grid size (if not completed in Sprint 7)
