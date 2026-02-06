# Test Plan: Sprint 1 & 2

Combined manual testing checklist for SmartLink Saved Accounts (Sprint 1) and Seamless Auto-Connect (Sprint 2).

**Tester:** Noel Romey
**Date:** 02/03/2026
**App Version:** 4.1.10
**Test Radio(s):** Flex 6300 Remote

---

## Prerequisites

- [x] NVDA or JAWS installed and running
- [x] At least one FlexRadio available (local or remote)
- [x] SmartLink account credentials available
- [x] Fresh install or cleared config (optional, for clean test)

---

## Sprint 1: SmartLink Saved Accounts

### Authentication Flow

| # | Test Case | Steps | Expected | Pass/Fail | Notes |
|---|-----------|-------|----------|-----------|-------|
| 1.1 | First-time login | Click Remote > Add Account > Log in | Auth0 login page appears in WebView2, login succeeds, account saved | | |
| 1.2 | Account appears in selector | After login, open Remote again | Saved account shown in list with email | | |
| 1.3 | Connect with saved account | Select saved account > Connect | Connects without re-entering credentials | | |
| 1.4 | Token refresh | Wait for token expiry (or restart app after ~1hr) | Silent refresh, no login prompt | | |
| 1.5 | Refresh failure | (Hard to test) Invalidate refresh token | Prompts for re-authentication | | |

### Account Management

| # | Test Case | Steps | Expected | Pass/Fail | Notes |
|---|-----------|-------|----------|-----------|-------|
| 1.6 | Rename account | Right-click account > Rename | Can change display name, saved | | |
| 1.7 | Delete account | Right-click account > Delete | Account removed from list | | |
| 1.8 | Multiple accounts | Add second SmartLink account | Both accounts shown, can switch | | |

### Accessibility (Sprint 1)

| # | Test Case | Steps | Expected | Pass/Fail | Notes |
|---|-----------|-------|----------|-----------|-------|
| 1.9 | Account selector with NVDA | Tab through account selector | All controls announced, no & characters read | | |
| 1.10 | Auth completing announcement | Complete login flow with NVDA | "Completing authentication" announced | | |

---

## Sprint 2: Seamless Auto-Connect

### Core Auto-Connect

| # | Test Case | Steps | Expected | Pass/Fail | Notes |
|---|-----------|-------|----------|-----------|-------|
| 2.1 | Set auto-connect (local) | RigSelector > Right-click local radio > Auto-Connect Settings > Enable > Save | Settings dialog shows, radio marked [AutoConnect] in list | | |
| 2.2 | Set auto-connect (remote) | Same as above for remote radio | Works same as local | | |
| 2.3 | Auto-connect on startup | Set auto-connect, restart app | App connects silently, no RigSelector shown | | |
| 2.4 | Single radio constraint | Try to set auto-connect on Radio B when A has it | Confirmation dialog: "Radio A currently has auto-connect. Switch to Radio B?" | | |
| 2.5 | Low bandwidth saved | Enable auto-connect + Low BW, restart | Connects with low bandwidth mode | | |

### Global Toggle

| # | Test Case | Steps | Expected | Pass/Fail | Notes |
|---|-----------|-------|----------|-----------|-------|
| 2.6 | Disable global auto-connect | Uncheck "Enable auto-connect on startup" | RigSelector shown on next startup even if radio has auto-connect | | |
| 2.7 | Re-enable global | Check the checkbox again, restart | Auto-connect works again | | |

### Radio Offline Handling

| # | Test Case | Steps | Expected | Pass/Fail | Notes |
|---|-----------|-------|----------|-----------|-------|
| 2.8 | Radio offline dialog | Set auto-connect, turn off radio, start app | Friendly dialog: "[Radio] is not available" with 4 options | | |
| 2.9 | Try Again | Click "Try Again" in offline dialog | Retries connection | | |
| 2.10 | Disable Auto-Connect | Click "Disable" in offline dialog | Auto-connect cleared, RigSelector shown | | |
| 2.11 | Choose Another Radio | Click "Choose Another" | RigSelector opens | | |
| 2.12 | Cancel | Click "Cancel" | Dialog closes, app stays disconnected | | |

### Switch Radio

| # | Test Case | Steps | Expected | Pass/Fail | Notes |
|---|-----------|-------|----------|-----------|-------|
| 2.13 | Switch while connected | Radio menu > Select Rig (while connected) | Disconnects current, opens RigSelector | | |
| 2.14 | Connect to different radio | After disconnect, select different radio | Connects to new radio without restart | | |

### UI Cleanup

| # | Test Case | Steps | Expected | Pass/Fail | Notes |
|---|-----------|-------|----------|-----------|-------|
| 2.15 | Login button hidden | Open RigSelector | Login button not visible | | |
| 2.16 | Login button not in tab order | Tab through RigSelector | Cannot tab to Login button | | |
| 2.17 | Auto-connect indicator | Set auto-connect on a radio | Radio shows [AutoConnect] prefix in list | | |
| 2.18 | Low BW indicator | Toggle Low BW for a radio | Radio shows [LowBW] prefix in list | | |

### Accessibility (Sprint 2)

| # | Test Case | Steps | Expected | Pass/Fail | Notes |
|---|-----------|-------|----------|-----------|-------|
| 2.19 | "Connecting to" announced | Connect to radio with NVDA | "Connecting to [RadioName]" spoken | | |
| 2.20 | "Connected to" announced | Connection completes | "Connected to [RadioName]" spoken | | |
| 2.21 | "Disconnecting" announced | Disconnect via Select Rig | "Disconnecting from [RadioName]" spoken | | |
| 2.22 | "Offline" announced | Trigger offline dialog | "[RadioName] is offline" spoken | | |
| 2.23 | Auto-connect set announced | Enable auto-connect | "Auto-connect set for [RadioName]" spoken | | |
| 2.24 | Global toggle announced | Check/uncheck global checkbox | "Auto-connect on startup enabled/disabled" spoken | | |
| 2.25 | Settings dialog accessible | Open Auto-Connect Settings dialog | Radio name, checkboxes, buttons all announced | | |

---

## Summary

| Sprint | Total Tests | Passed | Failed | Blocked |
|--------|-------------|--------|--------|---------|
| Sprint 1 | 10 | | | |
| Sprint 2 | 17 | | | |
| **Total** | **27** | | | |

---

## Issues Found

| # | Test | Issue Description | Severity | Status |
|---|------|-------------------|----------|--------|
| | | | | |
| | | | | |
| | | | | |

---

## Notes

_Any additional observations, edge cases discovered, or suggestions for improvement:_








---

*Test plan created 2026-02-01*
