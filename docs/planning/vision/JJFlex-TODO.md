# JJ Flex — TODO / Rolling Backlog

Last updated: 2026-02-12

## Bug Reports

### BUG-001: Modern menu "coming soon" items silent in JAWS (reported by Don, 2026-02-09)
- **Steps:** Open Modern mode, navigate menus, press Enter on a stub/placeholder item
- **JAWS:** Says nothing on Enter
- **NVDA:** Says "coming soon"
- **Root cause:** `AddModernStubItem()` in Form1.vb creates enabled menu items that speak "coming soon" via Tolk on click. JAWS doesn't reliably surface Tolk speech in menu context. No `AccessibleDescription` is set.
- **Fix:** Set `AccessibleDescription = "coming soon"` on stub items, or disable them with `.Enabled = False` and set description to "Not yet available". Either way, both screen readers should announce the placeholder state.
- **Priority:** Medium — affects JAWS users navigating Modern menus
- **Status:** Superseded by BUG-011 — "coming soon" now in Text property directly

### BUG-002: Brief rig audio leak on remote connect (reported by Don, 2026-02-09)
- **Symptom:** When a remote user connects to Don's radio via SmartLink, Don hears 2-3 seconds of rig audio through his local speakers before the remote connection fully establishes and local audio goes silent. Remote audio works fine for the connecting user — this is a local-side issue at the radio owner's end.
- **Frequency:** Unknown — needs a study with ~10 connect attempts to determine how often it happens.
- **Hypothesis:** During SmartLink handshake, the radio may briefly route audio to local speakers before the remote client takes over the audio stream. The mute/routing switch may not happen until after the connection fully establishes.
- **Action:** Review SmartLink connect sequence in code to find where local audio muting occurs relative to remote stream start. Look for a race condition or ordering issue. Low urgency but worth understanding the root cause.
- **Priority:** Low — cosmetic annoyance, not a functional issue
- **Status:** Logged for future investigation

### BUG-004: Crash on shutdown — System.IO.Ports v9.0.0 missing (2026-02-09)
- **Symptom:** App crashes on exit with `FileNotFoundException: Could not load file or assembly 'System.IO.Ports, Version=9.0.0.0'`
- **Stack:** `JJFlexControl.Serial.Close() → FlexControl.Dispose() → FlexKnob.Dispose() → globals.knobThreadProc()`
- **Root cause:** JJFlexControl references `System.IO.Ports` v9.0.0 (.NET 9) but app targets .NET 8. The assembly isn't in the build output. The knob thread tries to dispose the serial port on shutdown even if no knob is connected.
- **Fix options:** (a) Downgrade System.IO.Ports to v8.x, (b) Add try/catch in FlexKnob.Dispose(), (c) Skip dispose if no knob was initialized
- **Priority:** Medium — causes crash dump on every shutdown, even when no FlexControl knob is connected
- **Status:** Pre-existing, logged for fix

### BUG-003: Modern mode tab stops need design (reported by Don, 2026-02-09)
- **Description:** Tab in Modern mode cycles through controls, but there's no intentional design for what should be in the tab order. Classic mode has its own tab behavior; Modern should be different and purpose-built.
- **Action:** Design Modern mode tab stops as part of a future Modern UI sprint. Consider: what controls should be reachable via Tab? What's the logical focus flow? Should RadioPane-like status be in the tab order?
- **Priority:** Low — Modern mode is still under construction
- **Status:** Logged for future sprint

### BUG-005: UTC timestamp stuck at first QSO time (found during Sprint 5 testing, 2026-02-10)
- **Symptom:** In Logging Mode, after logging the first QSO, all subsequent QSOs logged in the same session showed the same UTC timestamp as the first entry (time didn't update).
- **Root cause:** `NewEntry()` cleared text fields but didn't clear the session's DateOn/TimeOn ADIF fields. `AutoFillFromRadio()` only set timestamps "if empty", so the first timestamp persisted across all entries.
- **Fix:** `NewEntry()` now explicitly clears session DateOn/TimeOn fields before calling `AutoFillFromRadio()`, ensuring each QSO gets a fresh timestamp.
- **Priority:** High — affects log accuracy
- **Status:** FIXED (commit 9591a835, 2026-02-10)

### BUG-006: Callbook announcement interrupts field announcements (found during Sprint 5 testing, 2026-02-10)
- **Symptom:** When operator tabs out of Call Sign field and quickly tabs to next field (RST Sent), the screen reader field announcement ("RST Sent edit") gets cut off by the async callbook result announcement ("Bob, Seattle").
- **Root cause:** `ApplyCallbookResult()` used `ScreenReaderOutput.Speak(..., interrupt=True)`, which interrupted the screen reader's field announcement in progress.
- **Fix:** Changed interrupt parameter to `False` in `ApplyCallbookResult()`. Screen reader now queues callbook data after field announcement, so operator hears both: "RST Sent edit" followed by "Bob, Seattle".
- **Priority:** Medium — affects screen reader UX during fast tabbing
- **Status:** FIXED (commit 9591a835, 2026-02-10)

### BUG-007: QRZ failure should auto-fallback to HamQTH (found during Sprint 5 testing, 2026-02-11)
- **Symptom:** If QRZ login fails (wrong password, expired subscription), callbook lookups silently stop working. User gets no feedback during logging — just no auto-fill.
- **Desired behavior:** On QRZ login failure, automatically fall back to built-in HamQTH ("JJRadio" account) so lookups keep working. After 3 failed QRZ logins, show a one-time dialog telling user HamQTH is being used as fallback. Also handle expired QRZ subscription as a distinct case with appropriate messaging. Recommend auto-switching the saved callbook source to HamQTH.
- **Scope:** LogPanel callbook initialization + QRZ login failure handler + HamQTH fallback logic + user notification dialog
- **Priority:** Medium — affects users whose QRZ subscriptions lapse
- **Status:** FIXED in Sprint 6 (Track A)

### BUG-008: HamQTH LogPanel lookup requires personal account (found during Sprint 5 testing, 2026-02-11)
- **Symptom:** When operator selects HamQTH as callbook source but has no personal HamQTH credentials, LogPanel lookups don't work. The built-in "JJRadio" HamQTH account is only used by StationLookup dialog, not by LogPanel.
- **Fix:** LogPanel now falls back to the built-in "JJRadio" account automatically when HamQTH is selected but no personal credentials are configured.
- **Priority:** Medium — blocks HamQTH users without personal accounts from getting auto-fill
- **Status:** FIXED in Sprint 6 (Track A)

### BUG-009: Modern menu submenus silent in JAWS/NVDA (Sprint 5 testing, 2026-02-11)
- **Symptom:** In Modern mode, submenus don't speak at all with JAWS or NVDA. Top-level menus and "coming soon" stubs were also not announcing.
- **Root cause:** Modern menus were missing `AccessibleName` on top-level menus and submenus.
- **Fix:** Added `AccessibleName` to all Modern mode top-level menus and submenus in Form1.vb.
- **Priority:** Medium — affects all screen reader users in Modern mode
- **Status:** FIXED in Sprint 6 (Track A)

### BUG-010: Hotkeys not firing — ProcessCmdKey bypassed by MenuStrip (Sprint 6 testing, 2026-02-12)
- **Symptom:** Multiple hotkey failures across all modes:
  - F6 doesn't switch panes in Logging Mode (test D1/F3)
  - Alt+C opens Radio menu instead of CW Zero Beat / Log Call (test D1)
  - Alt+S opens Radio menu instead of Start Scan / Log State (test D2)
  - F1 doesn't work in Logging Mode (test D3)
  - Ctrl+/ (Command Finder) intermittent — works only when focus is on certain controls (test D3b/F1)
- **Root cause:** `ProcessCmdKey` in Form1.vb delegated ALL keys to `MyBase.ProcessCmdKey()` which let the MenuStrip eat Alt+letter combinations before `DoCommand` could see them. Additionally, `DoCommand` was only wired to specific controls via `doCommand_KeyDown` — if focus was on LogPanel, SplitContainer, or any other control, hotkeys never reached the scope-aware registry.
- **Fix:** Route ALL keys through `Commands.DoCommand(keyData)` in `ProcessCmdKey` BEFORE falling through to `MyBase.ProcessCmdKey`. This ensures scope-aware hotkeys always fire regardless of focus and take priority over menu accelerators.
- **Priority:** High — blocked most hotkeys in Logging Mode and some in Modern Mode
- **Status:** FIXED in Sprint 6 (2026-02-12)

### BUG-011: "Coming soon" stubs don't announce in JAWS or NVDA (Sprint 6 testing, 2026-02-12)
- **Symptom:** In Modern mode, stub menu items announce their label ("Select Slice") but NOT the "coming soon" suffix. Both JAWS and NVDA affected. (test B3)
- **Root cause:** Screen readers read the `.Text` property of disabled ToolStripMenuItems, not `.AccessibleName`. The " - coming soon" suffix was only in `AccessibleName` and `AccessibleDescription`, not in `Text`.
- **Fix:** Put "coming soon" directly in the `.Text` property: `"Select Slice - coming soon"`. Also set matching `AccessibleName`. All screen readers now announce the full label.
- **Priority:** Medium — affects Modern mode navigation for all screen reader users
- **Status:** FIXED in Sprint 6 (2026-02-12)
- **Note:** Supersedes BUG-001 (same menu items, different symptom)

### BUG-012: Hotkey conflict detection allows saving duplicates (Sprint 6 testing, 2026-02-12)
- **Symptom:** In Hotkey Editor, assigning the same key to two functions in the same scope shows a conflict warning but still allows saving via the OK button. User can save conflicting bindings. Also, after declining to save, the conflict remains in the UI with no way to undo. (test E3)
- **Root cause:** (a) `CheckConflict()` skipped same-scope entries (line 61: `If other.Scope = scope Then Continue For`), so same-scope duplicates weren't caught during assignment. (b) `OKButton_Click` only warned with Yes/No dialog — didn't block save.
- **Fix:** (a) Auto-clear conflicting bindings on assignment (industry standard — like VS Code). When you assign a key that's already used, the OLD binding is cleared to `None` and the user is told. (b) Block save entirely if conflicts somehow still exist (belt and suspenders). (c) Fixed `CheckConflict` to use `excludeIndex` parameter instead of scope comparison for self-exclusion.
- **Priority:** Medium — prevents confusing key binding states
- **Status:** FIXED in Sprint 6 (2026-02-12)

### BUG-013: Duplicate QSO beep not audible (Sprint 6 testing, 2026-02-12)
- **Symptom:** When logging a duplicate QSO, "worked x calls" is announced but the duplicate warning beep is not heard. (test G1)
- **Priority:** Low — speech announcement works, just the audio beep is missing
- **Status:** Logged for investigation

### BUG-014: XmlSerializer corrupts combined Keys values in KeyDefs.xml (Sprint 6, 2026-02-12)
- **Symptom:** Hotkey bindings with modifier keys (e.g., `Keys.C Or Keys.Alt`) get serialized as space-separated flag names ("LButton ShiftKey ...") that can't be parsed back, causing bindings to silently revert to `Keys.None` on next load.
- **Root cause:** `XmlSerializer` treats `Keys` as a `[Flags]` enum and decomposes combined values. The decomposed names don't round-trip through `Enum.Parse`.
- **Fix:** Store `Keys` as integer via XML proxy property (`keyAsString`). Backward-compatible: tries `Integer.TryParse` first, falls back to `Enum.Parse` for legacy files. Added contamination guard: if loaded key is `Keys.None` but built-in default has a real binding, preserve the default.
- **Priority:** High — caused progressive hotkey data loss across app restarts
- **Status:** FIXED in Sprint 6 (2026-02-12)

### BUG-015: F6 double-announces "Radio pane" in Logging Mode (Sprint 6 testing, 2026-02-12)
- **Symptom:** Pressing F6 to switch to RadioPane in Logging Mode announces "Radio pane" twice.
- **Root cause:** `FreqBox.Enter` handler in `RadioPane.vb` explicitly calls `Speak("Radio pane")`, and the screen reader also reads `FreqBox.AccessibleName` which starts with "Radio pane". Both fire when focus lands on FreqBox.
- **Fix:** Remove the explicit `Speak("Radio pane")` from the FreqBox Enter handler — the `AccessibleName` already provides the announcement naturally.
- **Priority:** Low — cosmetic double-announcement
- **Status:** Logged for Sprint 7

### BUG-016: RNN toggles on radios that don't support it — no feature gating in Modern menu (Sprint 7 testing, 2026-02-14)
- **Symptom:** Neural NR (RNN) can be toggled via Modern menu on a FLEX-6300, which doesn't support RNN. Should be grayed out or blocked based on radio model/subscription.
- **Root cause:** Modern menu DSP toggles don't check `Radio.FeatureLicense` or radio capabilities before toggling. The Classic Filters form does gate these features.
- **Fix:** Add feature gating checks before each DSP toggle in Modern menu. If feature unavailable, speak "Neural NR not available on this radio" or similar. Check both hardware capability and license subscription.
- **Priority:** Medium — allows toggling unsupported features, confusing behavior
- **Status:** Open

### BUG-017: APF toggle always says "on" — doesn't actually toggle off (Sprint 7 testing, 2026-02-14)
- **Symptom:** Audio Peak Filter in Modern menu says "Audio Peak Filter on" every time it's pressed — never says "off". May not actually be toggling the radio state. Tested in LSB mode on Don's 6300.
- **Root cause:** Needs investigation — may be mode-dependent (APF is CW-only on some radios?) or the FlexLib property may not toggle correctly.
- **Priority:** Medium — broken toggle
- **Status:** Open

### BUG-018: Dup count inconsistent between tab and shift-tab (Sprint 7 testing, 2026-02-14)
- **Symptom:** When entering a duplicate callsign (WA2IWC), pressing Tab says "worked 6 times" but Shift-Tab says "3 contacts". The two announcements give different counts for the same call.
- **Root cause:** Needs investigation — may be reading from different data sources (previous contact lookup vs dup check).
- **Priority:** Medium — confusing for operator
- **Status:** Open

### BUG-019: Log Contact doesn't announce pre-fill (Sprint 7 testing, 2026-02-14)
- **Symptom:** Clicking "Log Contact" in Station Lookup enters Logging Mode with fields pre-filled, but no screen reader announcement like "Entering Logging Mode with [call] pre-filled".
- **Root cause:** The speech announcement was expected but never implemented.
- **Fix:** Add `Speak("Entering Logging Mode with [call] pre-filled")` when Log Contact triggers mode entry.
- **Priority:** Low — functional, just missing feedback
- **Status:** Open

### BUG-020: Status Dialog not accessible — can't tab, no close button, appears outside app (Sprint 7 testing, 2026-02-14)
- **Symptom:** Status Dialog (Ctrl+Alt+S) opens but: (a) can't tab through fields, (b) no OK button or Enter-to-close, (c) window appears outside the main JJFlex window. NVDA can force-read the dialog content but keyboard navigation is broken.
- **Root cause:** WPF Window likely missing tab stops, focusable elements, and window ownership/placement.
- **Fix:** Set `Owner` to Form1's WPF interop handle, add OK/Close button with Enter as default, ensure all TextBlocks/labels are in tab order or use a single selectable TextBox with the full status text.
- **Priority:** High — completely inaccessible dialog
- **Status:** Open

### BUG-021: QSO grid count setting doesn't take effect (Sprint 7 testing, 2026-02-14)
- **Symptom:** Changed operator's Recent QSOs setting to 10, but grid still shows 20. Setting doesn't apply until some unknown trigger (possibly app restart or new session).
- **Root cause:** Needs investigation — LogPanel may read the count only at session initialization, not on settings change. Or the setting may not be saved/loaded correctly.
- **Priority:** Medium — setting appears broken
- **Status:** Open

### BUG-022: QSO grid rows announce WPF type name instead of English (Sprint 7 testing, 2026-02-14)
- **Symptom:** When arrowing through QSO grid rows, screen reader announces "JJFlexWPF.RecentQsoRowDataItem" instead of a meaningful label like "QSO 1" or just the callsign.
- **Root cause:** WPF DataGrid row automation name defaults to the `ToString()` of the data item, which returns the type name. Need to override `ToString()` on `RecentQsoRowDataItem` or set `AutomationProperties.Name` on DataGrid rows.
- **Fix:** Override `ToString()` on `RecentQsoRowDataItem` to return the callsign (e.g., "W1AW") or implement an `AutomationPeer` for the row.
- **Priority:** Medium — bad screen reader experience in QSO grid
- **Status:** Open

### BUG-023: Connect to Radio while already connected has messy flow (Sprint 7 testing, 2026-02-14)
- **Symptom:** Clicking Radio → Connect to Radio while already connected disconnects without asking, opens rig selector, then SmartLink reconnect flow shows "session invalid" repeatedly. User has to click "No" multiple times to get back to a connected state.
- **Root cause:** No guard to ask "You're already connected — disconnect first?" before proceeding. The disconnect + reconnect sequence has error handling issues with stale SmartLink sessions.
- **Fix:** Add a confirmation dialog: "You are currently connected to [radio]. Disconnect and choose a new radio?" If No, cancel. If Yes, clean disconnect then open selector.
- **Priority:** Medium — confusing and potentially destructive flow
- **Status:** Open

## Near-term (next 1–3 sprints)

### Completed in Sprint 7
- [x] Callbook graceful degradation: QRZ→HamQTH auto-fallback, built-in HamQTH for LogPanel (BUG-007, BUG-008) — Sprint 6
- [x] Hotkeys v2: scope-aware registry, conflict detection, Command Finder, tabbed Settings UI — Sprint 6
- [x] Station Lookup → Log Contact button + distance/bearing — Sprint 7 Track C
- [x] WPF migration: LogPanel + Station Lookup — Sprint 7 Track B
- [x] Plain-English Status: Speak Status + Status Dialog — Sprint 7 Track D
- [x] Configurable QSO grid size — Sprint 7 Track E
- [x] CW hotkey feedback — Sprint 7 Track A
- [x] Modern UI Mode toggle (Modern default; Classic preserved)
- [x] DSP toggle state inversion fix — Sprint 7 Track A
- [x] Ctrl+Shift+L/M key forwarding through WPF — Sprint 7 Track A

### Sprint 8 — Form1 WPF Conversion (boring but necessary)
- [ ] Convert Form1 from WinForms to WPF Window (kills all interop issues)
- [ ] Eliminate ElementHost — all WPF controls native in WPF Window
- [ ] Fix R1 "unknown" interop artifact (goes away with pure WPF)
- [ ] Remove the FreqOut radio box from Modern mode. A charming relic of JJ's circa-1947 UI philosophy and Don's personal favorite control, this thing freqks the current developer out. It will be replaced by proper keyboard tuning commands once Modern mode has its own tuning keystrokes. Don will survive — he can switch to Classic mode whenever he wishes to get nostalgic.

### Sprint 9 — Remaining Forms WPF Conversion (boring but necessary)
- [ ] Convert all remaining dialog forms to WPF (in stages)
- [ ] WPF migration: Command Finder + DefineCommands
- [ ] WPF migration: PersonalInfo + LogCharacteristics
- [ ] WPF migration: remaining dialogs

### Sprint 10 (was Sprint 8)
- [ ] Slice Menu + Filter Menu (slice-centric operating model)
- [ ] QSO Grid: full filtering (band, mode, country, date range, callsign) + paging
- [ ] QSO Grid: row-0→1 indexing fix (comes free with WPF DataGrid)

### Sprint 11 (was Sprint 9)
- [ ] QRZ per-QSO upload checkbox in LogPanel tab order (Alt+Q)
- [ ] QRZ confirmation status check/request from QSO Grid
- [ ] QRZ data import (bulk sync)

### Sprint 12 (was Sprint 10)
- [ ] Modern menus → WPF Menu control (eliminates AccessibleName workarounds)
- [ ] StationLookup full redesign (distance/bearing, richer layout)

### Backlog — New Items (Sprint 7 testing, 2026-02-14)
- [ ] "Update Current Operator" menu item in Modern and Logging — opens PersonalInfo directly for current operator (skip select step)
- [ ] Rename "Operators" to "Edit or Change Operators" in Modern and Logging menus (leave Classic as-is)
- [ ] GPS grid from FlexLib — query radio GPS for operator grid square (works mobile!). Add Speak GPS and View GPS hotkeys/dialog. Fallback: lookup operator call via QRZ/HamQTH for grid.
- [ ] Grid refresh button in Station Lookup or PersonalInfo — re-lookup operator grid from callbook or GPS
- [ ] Include slice number in mute/unmute speech ("Muted slice A" / "Unmuted slice A")
- [ ] Audio management baseline: device selection + rig audio adjust entry point
- [ ] Filter presets / filter presets by mode
- [ ] Earcon/tone beeps — replace SystemSounds with configurable tone-based audio cues
- [ ] DSP current state display — show on/off state when arrowing through DSP menu items
- [ ] Hotkeys for Modern menu DSP/filter items
- [ ] "No stations connected" message in Connected Stations listbox when empty
- [ ] BUG-015: Fix F6 double-announce "Radio pane" in Logging Mode

## Slice UI & Status
- [ ] Define ActiveSlice rules and announce/earcon behaviors
- [ ] Implement Slice List (next/prev, select)
- [ ] Implement Slice Menu entry point
- [ ] Implement Speak Status (concise) + Status Dialog (full)

## Keyboard & Discoverability
- [x] Command Finder (Ctrl+/) search by keywords/synonyms, returns key + can execute — Sprint 6
- [x] Scope-aware conflict detection in Settings UI — Sprint 6
- [ ] Contextual help: group help via H/?
- [ ] Optional leader key design (Ctrl+J) + 2-step max

## Audio
- [ ] Audio menu / Adjust Rig Audio workflow (TX/RX levels, ALC guidance)
- [ ] Recording/playback and “parrot” concept (design + feasibility)
- [ ] DAX integration research and decision (use Flex manager vs internal config)

## Operator Profile & Band Plans (future sprint)
- [ ] QRZ self-lookup: if operator has a QRZ subscription and is logged in, auto-populate operator data (name, QTH, grid, etc.) from QRZ. Trigger from settings or on first callbook login.
- [ ] License class selection: add license class field to operator profile. Provide a dropdown of license classes based on operator's country (US: Technician/General/Amateur Extra; UK: Foundation/Intermediate/Full; etc.). For countries without a built-in list, allow free-text entry.
- [ ] Band plan enforcement/guidance: look up band plans by ITU region + license class. Show operator which bands/modes they're authorized for. Could highlight out-of-privilege operation. Research: ARRL band plan data, IARU region plans.
- [ ] Internationalization of license classes for popular countries (US, UK, Canada, Germany, Japan, Australia, etc.)

## Ecosystem
- [ ] 4O3A device integration plan (SDK/protocol + device matrix)
- [ ] Tuner Genius support planning

## QSO Grid — Full-Featured Log Viewer (Sprint 8)

Rename "Recent QSOs grid" → "QSO Grid". Default view: filter to recent (preserves current behavior).

- [ ] Filter bar: band, mode, country, date range, callsign, free-text search
- [ ] Apply filter → paged results with accessible announcement: "Loaded N more, showing X of Y"
- [ ] Down arrow at bottom of grid → load next page (10-20 rows)
- [ ] Row-0→1 indexing (WPF DataGrid gives this for free)
- [ ] Configurable default page size (from Sprint 7 Track E setting)
- [ ] Screen reader: announce filter results count, page loads, selected row details
- [ ] Clear filter → return to "recent" default view

## QRZ Deep Integration (Sprint 9)

Per-QSO upload with operator control, plus data import and confirmation features.

### Per-QSO Upload Checkbox
- Add "Upload to QRZ" checkbox as **last field in LogPanel tab order** (after Comments)
- Hotkey: **Alt+Q** jumps directly to checkbox (quick toggle for ragchew QSOs)
- Default state driven by operator settings: "Upload QSOs to QRZ by default" checkbox in PersonalInfo
- If settings checked → checkbox pre-checked on every new QSO
- Operator can uncheck per-QSO (e.g., another QSO with Don — he'll never confirm anyway)
- On Ctrl+W (save): if checked → save locally AND push to QRZ API; if unchecked → save locally only
- Screen reader: "Upload to QRZ, checked" / "Upload to QRZ, unchecked"
- Tab order: Call(Alt+C) → RST Sent → RST Rcvd → Name → QTH → State → Grid → Comments → Upload to QRZ(Alt+Q) → Ctrl+W saves

### QRZ Confirmation
- Check confirmation status from QSO Grid (hotkey on selected QSO)
- Request confirmation if QRZ API supports it
- Bulk confirmation status check (filter unconfirmed QSOs)

### Data Import
- Bulk upload existing log to QRZ Logbook
- Import QSOs from QRZ into local log (sync)
- Settings-driven: enable/disable auto-upload globally

## Multi-Slice Decode & Auto Snap (future — multiple sprints)

Full design docs in `docs/planning/design/multislice-decode/`. Key features:
- Per-slice CW + digital decoding with structured DecodeEvent pipeline
- DecodeAggregator: unified stacked feed (CW / Digital / All tabs)
- Auto Slice Snap policy engine: Trigger → Eligibility → ActionSet → Confirmation
- Five snap levels (0: highlight → 4: contest mode) — never auto-TX
- Keyboard-first navigation: Up/Down signals, Enter to snap, F6 toggle panes, J/Shift+J jump CQs
- Accessibility-first: Tolk for confirmations, UIA live regions for passive, speech modes (off/active/background/events-only)
- Safety: no auto-TX, no focus change during TX, pin override, undo last snap

## Logging Mode Customization (Sprint 9+)

### Customizable Split Logger Field Layout
JJ's vision: logging should be **blazing fast** with keyboard shortcuts. The split logger (Logging Mode) is the speed machine — minimal fields, every one reachable by hotkey. JJ's big logger (Ctrl+Alt+L) has everything.

**Design:**
- Operator selects which fields appear in the split logger from a curated list
- **No "select all"** — the split logger must stay minimal and fast. Cap the max field count
- Each field in JJ's big logger already has a default hotkey (Alt+letter). When a field is pulled into the split logger, **its hotkey comes with it** — inherited, not reassigned
- Hotkeys are Logging-scope (already supported by Sprint 6 KeyScope system)
- Tab order follows selected fields in display order
- Operators can Tab sequentially OR jump directly with hotkeys — whichever is faster

**Speed workflow example:**
Alt+C → W1ABC → Alt+N → Bob → Alt+Q → Space (uncheck QRZ) → Ctrl+W → logged in 5 seconds

**Field picker UI:**
- Accessible list with add/remove/reorder (screen-reader-friendly)
- Shows field name + its hotkey for each available field
- Preview of tab order after changes
- Reset to defaults button

**Implementation notes:**
- Research JJ's existing field hotkey assignments in the big logger (LogEntry form)
- Map each LogEntry field to a Logging-scope hotkey
- Split logger dynamically builds its UI from the operator's field selection
- Store field selection in PersonalData (per-operator)

### Contest Mode
- [ ] Contest designer / contest mode: review JJ's existing contest C# files in JJLogLib (FieldDay.cs, NASprint.cs, SKCCWESLog.cs), assess whether they work and are useful. Consider building a contest designer that lets operators define required exchange fields and contest rules. May be complex — research scope before committing. At minimum, validate existing contest exchange definitions work with LogPanel.

## Sprint 5 — QRZ + Logging Graduation (COMPLETED 2026-02-09)
- [x] QRZ.com + HamQTH integration (callsign lookup, auto-fill from callbook)
- [x] Credential validation on save (QRZ subscription check, HamQTH test login)
- [x] Station Lookup upgraded (Ctrl+L, QRZ/HamQTH support, DX country SR)
- [x] Full Log Access from Logging Mode (Ctrl+Alt+L)
- [x] Removed 11 Classic/Modern log entry hotkeys
- [ ] Graduate LogPanel to full-featured: add record navigation (PageUp/Down browse)
- [ ] Graduate LogPanel: edit existing records (load, modify, update)
- [ ] Graduate LogPanel: search log (find QSOs by criteria)
- [ ] Graduate LogPanel: load more records in Recent QSOs grid (batch/paging)
- [ ] Modern Mode gets embedded LogPanel (split view like Logging Mode)
- [ ] Classic Mode keeps full-screen LogEntry form (JJ's original design)
- [ ] Settings tab for managing Logging Mode preferences (suppress confirmations, etc.)
- [ ] Screen reader verbosity levels (concise vs. verbose announcements for pileup vs. ragchew)
- [ ] Contest-specific exchange validation (Field Day, NA Sprint, etc. — each has different required fields)

## WPF Migration (incremental, ongoing)

**Goal:** Replace WinForms controls with WPF for better native UI Automation, screen reader support, and modern UI capabilities. Migrate incrementally — 2 forms per sprint, new forms always WPF. WinForms shell (Form1) stays until most content is WPF.

**Why:** WinForms uses MSAA accessibility bridge which causes quirks (DataGridView row-0 indexing, menu items needing manual `AccessibleName`, etc.). WPF has native UIA support — controls expose automation properties automatically. JAWS/NVDA work more reliably with WPF.

**Rules going forward:**
- All NEW forms/dialogs → WPF from day one
- Existing forms → convert 2 per sprint alongside feature work
- WPF controls hosted in WinForms via `ElementHost` during transition
- Test each conversion with JAWS + NVDA before marking done

**Migration roadmap (REVISED — Form1 first to kill interop issues):**

| Priority | Form | Why | Sprint |
|----------|------|-----|--------|
| 1 | LogPanel (QSO grid + entry fields) | Biggest a11y win — fixes row-0, field nav | 7 ✅ |
| 2 | Station Lookup (+ Log Contact button) | Small form, DX workflow, validates approach | 7 ✅ |
| **3** | **Form1 shell → WPF Window** | **Kills all WPF-in-WinForms interop issues** | **8** |
| 4 | Command Finder | Already new, natural WPF fit | 9 |
| 5 | DefineCommands (hotkey settings) | Tabbed UI, benefits from WPF TabControl | 9 |
| 6 | PersonalInfo (settings) | Straightforward form conversion | 9 |
| 7 | LogCharacteristics | Small dialog | 9 |
| 8 | All remaining dialogs | Finish the job | 9 |
| 9 | Modern menus → WPF Menu control | Eliminates AccessibleName workarounds | 12 |

**Acceptance criteria per conversion:**
- [ ] WPF control/window created with proper AutomationProperties
- [ ] Hosted via ElementHost (or standalone WPF Window for dialogs)
- [ ] JAWS announces all controls, grids show 1-based rows
- [ ] NVDA announces all controls, same behavior
- [ ] Tab order logical, focus management correct across WinForms↔WPF boundary
- [ ] No regression in existing functionality

## Long-term
- [ ] RTL-SDR/Airspy/SDRplay source support planning
- [ ] Braille + sonified waterfall "famous interface" implementation
- [ ] Multi-radio simultaneous operation (possible premium)
- [ ] Auto-update / update notifications (needs server or web page for version checking; design update delivery mechanism)
