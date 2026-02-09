# JJ Flex — TODO / Rolling Backlog

Last updated: 2026-02-09

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

## Near-term (next 1–3 sprints)
- [ ] Hotkeys v2: profiles + conflict detection + explicit key recording UX
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
- [ ] Command Finder (F12) search by keywords/synonyms, returns key + can execute
- [ ] Contextual help: group help via H/?
- [ ] Prevent accidental reassignment (explicit record mode; safe defaults)
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

## Sprint 5 — QRZ + Logging Graduation
- [ ] QRZ.com integration (callsign lookup, auto-fill from QRZ database)
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
