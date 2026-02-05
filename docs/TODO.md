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

### Classic Mode vs. JJFlex Next Gen
- [ ] **UI Mode toggle** - Allow users to choose between Classic and Next Gen interfaces:
  - **Classic Mode**: Keep existing UI for users with muscle memory
    - Big text box for status/tuning (type frequency directly)
    - Letter shortcuts for slice control (A/B/M/V etc.)
    - Logging fields in main form
  - **Next Gen Mode**: Modern, menu-driven interface for new users
    - Reorganized menus that make logical sense
    - Slice control via menus (no memorizing letter codes)
    - Slimmed down tuning interface
    - Speak Status feature
- [ ] **Logging options** - Make logging configurable:
  - Toggle logging on/off
  - Move logging to separate dialog (declutter main form)
  - Option to hide/remove logging entirely for non-contesters

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
- [ ] **FT8/Digital modes** - WSJT-X CAT control improvements
- [ ] **Macro/scripting** - Contest automation, scheduled recordings

## Technical Debt

- [ ] **Address 2500+ CA1416 warnings** - Windows-only APIs flagged by cross-platform analyzer. Options:
  - Add `[SupportedOSPlatform("windows")]` to assemblies (preferred - documents intent)
  - Suppress in project files with `<NoWarn>CA1416</NoWarn>`
  - Most warnings are in FlexLib vendor code and Radios project
  - App is Windows-only, so these are false positives, but noisy build output
- [ ] **System.Drawing.Common conflicts** - Version mismatch warnings in build

## Completed

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
