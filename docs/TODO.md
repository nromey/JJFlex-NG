# JJFlexRadio - Feature Ideas & TODO

This document tracks feature ideas, improvements, and technical debt for future sessions.

## High Priority

### Screen Reader / Accessibility
- [ ] **Diversity status announcements** - Speak diversity on/off, antenna switching via Tolk. Options:
  - Hotkey (e.g., Ctrl+Shift+D) to announce current diversity status
  - Monitor mode: auto-announce when diversity state changes
  - Requires 2-SCU radio (6600/6700/8600) to test

- [ ] **Meter announcements** - Speak ALC peaks, SWR warnings, signal strength on demand. Use ScreenReaderOutput infrastructure already in place.

### SmartLink / Remote
- [ ] **Sprint 2: Seamless auto-connect** - See `docs/planning/agile/Sprint-02-Auto-Connect.md`
  - User story: "As a remote operator, I want to seamlessly auto-connect to my preferred radio when I launch the app, so that I don't have to click through login screens every time."
  - Integrate saved accounts (Sprint 1) with existing auto-connect feature
  - Silent authentication on startup if configured

## Medium Priority

### UI Improvements
- [ ] **Menu cleanup** - Reorganize menus for better accessibility flow
- [ ] **Keyboard shortcuts** - Add/document shortcuts for common actions

### Audio
- [ ] **Audio device hot-swap** - Handle device changes gracefully without restart

## Low Priority / Nice to Have

- [ ] **CW decode** - Investigate Fldigi integration or native decode
- [ ] **FT8/Digital modes** - WSJT-X CAT control improvements
- [ ] **Macro/scripting** - Contest automation, scheduled recordings

## Technical Debt

- [ ] **Suppress CA1416 warnings** - Windows-only APIs flagged by analyzer. Add SupportedOSPlatform attributes or suppress in project file.
- [ ] **System.Drawing.Common conflicts** - Version mismatch warnings in build

## Completed

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
