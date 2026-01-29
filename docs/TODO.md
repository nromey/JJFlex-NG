# JJFlexRadio - Feature Ideas & TODO

This document tracks feature ideas, improvements, and technical debt for future sessions.

## High Priority

### Screen Reader / Accessibility
- [ ] **Fix CrossSpeak IsSpeaking()** - Currently always returns false on Windows. Clone Tolk source and investigate why. Would allow proper speech completion detection instead of timing estimates.
  - Source: https://github.com/dkager/tolk
  - CrossSpeak wraps Tolk for .NET
  - Test against NVDA first (best API support)

- [ ] **Meter announcements** - Speak ALC peaks, SWR warnings, signal strength on demand. Use ScreenReaderOutput infrastructure already in place.

### SmartLink / Remote
- [ ] **Save remote connection profiles** - Store email, refresh tokens (encrypted), radio nicknames in `%APPDATA%\JJFlexRadio\RemoteProfiles.json`. Allow quick reconnection without re-authenticating.
  - Dropdown to select saved profile
  - "Remember this connection" checkbox after successful auth
  - Nickname field (e.g., "Don's 6600", "Margaret's 8600")

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

- [x] **.NET 8 migration** - v4.1.9 (2026-01-29)
- [x] **x64/x86 dual architecture** - v4.1.9
- [x] **WebView2 for Auth0** - v4.1.9
- [x] **Screen reader output (CrossSpeak/Tolk)** - v4.1.9
- [x] **GitHub Actions release workflow** - v4.1.9
- [x] **WebView2 access denied fix** - v4.1.9

---

*Add new items at the top of the appropriate section. Move to Completed when done.*
