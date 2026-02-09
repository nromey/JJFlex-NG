# JJFlexRadio - Feature Ideas & TODO

This document tracks feature ideas, improvements, and technical debt for future sessions.

## High Priority

### Screen Reader / Accessibility
- [ ] **Speech verbosity settings** - Let users control announcement detail level:
  - Verbosity levels: Minimal (errors only), Normal (key states), Verbose (all changes)
  - Per-category settings: connection events, meter readings, state changes
  - Store in operator config
  - Extend ScreenReaderOutput with verbosity check before speaking

- [ ] **Diversity status announcements** - Speak diversity on/off, antenna switching via Tolk. Options:
  - Hotkey (e.g., Ctrl+Shift+D) to announce current diversity status
  - Monitor mode: auto-announce when diversity state changes
  - Requires 2-SCU radio (6600/6700/8600) to test

- [ ] **Meter announcements** - Speak ALC peaks, SWR warnings, signal strength on demand. Use ScreenReaderOutput infrastructure already in place.

- [ ] **Auth0 login error announcements** - Known limitation: Auth0 renders login errors (bad password) client-side as plain text without accessible markup. NVDA doesn't announce them. Attempted MutationObserver/JS injection but Auth0's DOM structure doesn't cooperate. Revisit if FlexRadio updates their Auth0 tenant or if we find a reliable selector.

### SmartLink / Remote
- [ ] **Auto-reconnect on disconnect** - Automatically reconnect when connection drops unexpectedly
  - Configurable retry attempts and delay
  - User notification when reconnecting

## Medium Priority

### Logging Mode Enhancements (Post-Sprint 4)
- [ ] **Configurable Recent QSOs grid size** - Add a setting for "number of QSOs to display in the Recent QSO list"
  - Currently hardcoded as `MaxRecentQSOs = 20` in LogPanel.vb
  - Store in operator config (PersonalData)
  - Settings UI: text field or dropdown with common values (10, 20, 50, 100)
  - Apply on Logging Mode entry / settings change

### Logging Mode Enhancements (Post-Sprint 5)
- [ ] **Modernize FindLogEntry** - Replace ListBox with DataGridView for screen reader UIA support

### UI Improvements
- [ ] **Menu cleanup** - Reorganize menus for better accessibility flow
- [ ] **Keyboard shortcuts** - Add/document shortcuts for common actions

### Audio - Sprint 3 Candidate
- [ ] **Audio Boost settings (PC Audio path)** - User-adjustable gain for remote/WAN audio:
  - Menu under Settings or Actions with preset levels: 50%, 75%, 100%, 125%, 150%, Custom
  - Hotkey for quick adjustment (Vol Up/Down in steps)
  - Future: Global hotkey support when that feature ships
  - Live update without restarting audio stream
  - Persist setting in operator config
  - Current default: 8x (+18dB) based on calibration testing
  - Technical: `JJAudioStream.OutputGain` property already implemented

- [ ] **Local Flex Audio volume control** - When using radio's built-in speakers/headphone:
  - Verify Slice.AudioGain (0-100) works for local output
  - Add hotkey for Flex speaker volume up/down
  - May need different gain path than PC Audio (no Opus decode involved)
  - Test with Don's local setup

- [ ] **Audio routing clarity** - Two distinct paths need separate controls:
  - **PC Audio**: WAN → Opus decode → OutputGain → PortAudio → PC speakers
  - **Local Flex Audio**: Radio DSP → Slice.AudioGain → Flex speakers/headphones
  - Document which controls affect which path
  - Consider unified "Audio Settings" dialog showing both

- [x] **~~x64 radio audio routing issue~~** - RESOLVED: Raw Opus peaks ~0.16, needed 8x gain boost. Rebuilt Opus 1.6.1 and PortAudio 19.7.0 from source with full SIMD. Added `OutputGain` property to `JJAudioStream.WriteOpus()`.

- [ ] **Audio device hot-swap** - Handle device changes gracefully without restart

## Low Priority / Nice to Have

- [ ] **CW decode** - Investigate Fldigi integration or native decode
- [ ] **FT8/Digital modes** - WSJT-X integration with logging-aware station selection:
  - WSJT-X decodes signals → JJFlex checks each call against log
  - Highlight new DXCC countries, new bands, new modes (band/mode slots)
  - Accessible station picker: screen reader announces "new country" / "new band" etc.
  - Auto-log completed FT8 QSOs using Jim's logging infrastructure
  - CAT control improvements for frequency/mode coordination
- [ ] **Macro/scripting** - Contest automation, scheduled recordings

## Technical Debt

- [ ] **Address 2500+ CA1416 warnings** - Windows-only APIs flagged by cross-platform analyzer. Options:
  - Add `[SupportedOSPlatform("windows")]` to assemblies (preferred - documents intent)
  - Suppress in project files with `<NoWarn>CA1416</NoWarn>`
  - Most warnings are in FlexLib vendor code and Radios project
  - App is Windows-only, so these are false positives, but noisy build output
- [ ] **System.Drawing.Common conflicts** - Version mismatch warnings in build

## Completed

- [x] **Sprint 5: QRZ/HamQTH Lookup & Full Log Access** (2026-02-09, no version bump)
  - Phase 1: Removed 11 Classic/Modern log entry hotkeys (freed Alt+C/S/G/N/R/E/M for future radio use)
  - Phase 2: QRZ XML lookup library (`QrzLookup/QrzCallbookLookup.cs`) + unified `CallbookResult.vb`
  - Phase 3: Callbook credentials UI in operator profile (source dropdown + username/password with DPAPI encryption)
  - Phase 4: Callbook auto-fill in LogPanel (async, fills empty fields only, local data takes priority)
  - Phase 5: Full Log Access from Logging Mode (Ctrl+Alt+L opens JJ's LogEntry form)
  - Phase 6: Parking lot — Reset Confirmations, BUG-001 Modern menu stubs fix
  - Phase 7: Sprint 4 archived, changelog updated
  - **Post-sprint:** Credential validation on save (QRZ subscription check + HamQTH test login)
  - **Post-sprint:** Station Lookup upgraded — QRZ/HamQTH support, Ctrl+L hotkey, DX country SR announcements
  - See `docs/planning/agile/Sprint-05-QRZ-Lookup.md`

- [x] **Sprint 4: Logging Mode** - v4.1.12 (2026-02-07)
  - Phase 1: Menu & Mode Switching (Ctrl+Shift+L toggle, Logging Mode menu bar)
  - Phase 2: Quick-Entry LogPanel + RadioPane (SplitContainer, F6 pane switching, arrow-key tuning, dup checking)
  - Phase 3: Recent QSOs Grid + Previous Contact Lookup (DataGridView with UIA, call sign index, auto-fill Name/QTH)
  - Phase 4: Cleanup & Polish (SKCC WES removal, screen reader audit, version bump)
  - See `docs/planning/agile/Sprint-04-Logging-Mode.md`

- [x] **Sprint 3: Classic/Modern UI Mode** - v4.1.11 (2026-02-06)
  - UIMode enum (Classic/Modern/Logging), persisted per operator
  - Modern menu skeleton with programmatic menus
  - Ctrl+Shift+M toggle, one-time upgrade prompt for existing operators
  - New operators default to Modern mode
  - See `docs/planning/agile/archive/Sprint-03-Classic-Modern-Mode.md`

- [x] **Sprint 2: Seamless auto-connect** - v4.1.11 (2026-02-01)
  - Unified auto-connect for local and remote radios
  - Friendly "radio offline" dialog with options
  - Auto-connect settings dialog with confirmation
  - Global auto-connect toggle
  - Full screen reader announcements for all connection states
  - See `docs/planning/agile/Sprint-02-Auto-Connect.md`

- [x] **Sprint 1: SmartLink saved accounts** - v4.1.10 (2026-01-31)
  - PKCE authentication flow (more secure)
  - DPAPI-encrypted token storage
  - Account selector dialog
  - Automatic token refresh
- [x] **.NET 8 migration** - v4.1.9 (2026-01-29)
- [x] **x64/x86 dual architecture** - v4.1.9
- [x] **WebView2 for Auth0** - v4.1.9
- [x] **Screen reader output (CrossSpeak/Tolk)** - v4.1.9
- [x] **GitHub Actions release workflow** - v4.1.9
- [x] **WebView2 access denied fix** - v4.1.9
- [x] **Advanced DSP features** - v4.1.8
  - Neural NR (RNN), Spectral NR, Legacy NR
  - FFT Auto-Notch, Legacy Auto-Notch

---

*Add new items at the top of the appropriate section. Move to Completed when done.*
