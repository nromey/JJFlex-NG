# JJ Flex — TODO / Rolling Backlog

Last updated: 2026-02-11

## Bug Reports

### BUG-001: Modern menu "coming soon" items silent in JAWS (reported by Don, 2026-02-09)
- **Steps:** Open Modern mode, navigate menus, press Enter on a stub/placeholder item
- **JAWS:** Says nothing on Enter
- **NVDA:** Says "coming soon"
- **Root cause:** `AddModernStubItem()` in Form1.vb creates enabled menu items that speak "coming soon" via Tolk on click. JAWS doesn't reliably surface Tolk speech in menu context. No `AccessibleDescription` is set.
- **Fix:** Set `AccessibleDescription = "coming soon"` on stub items, or disable them with `.Enabled = False` and set description to "Not yet available". Either way, both screen readers should announce the placeholder state.
- **Priority:** Medium — affects JAWS users navigating Modern menus

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

## Near-term (next 1–3 sprints)
- [x] Callbook graceful degradation: QRZ→HamQTH auto-fallback, built-in HamQTH for LogPanel (BUG-007, BUG-008) — Sprint 6
- [x] Hotkeys v2: scope-aware registry, conflict detection, Command Finder, tabbed Settings UI — Sprint 6
- [ ] Station Lookup → Log Contact: "Log a contact with station" button on Station Lookup dialog. Enters Logging Mode with callsign/name/QTH/grid pre-filled from lookup. DX hunting workflow: lookup → call → log.
- [ ] Station Lookup: add distance and bearing to station (for beam/rotator directionality)
- [ ] Configurable Recent QSOs grid size (currently hardcoded at 20)
- [ ] Modern UI Mode toggle (Modern default; Classic preserved)
- [ ] Slice Menu + Filter Menu (slice-centric operating model)
- [ ] Plain-English Status: Speak Status + Status Dialog (replace alphabet soup in Modern)
- [ ] Audio management baseline: device selection + rig audio adjust entry point
- [ ] Update CHANGELOG discipline + code commenting guideline doc

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

## Logging Mode Customization (future sprint)
- [ ] Customizable LogPanel field layout: let operators choose which fields appear in the quick-entry logger. JJ's full LogEntry form remains available via Ctrl+Alt+L for all fields. The field picker must be fully accessible (screen-reader-friendly list with add/remove/reorder).
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

## Long-term
- [ ] RTL-SDR/Airspy/SDRplay source support planning
- [ ] Braille + sonified waterfall "famous interface" implementation
- [ ] Multi-radio simultaneous operation (possible premium)
- [ ] Auto-update / update notifications (needs server or web page for version checking; design update delivery mechanism)
