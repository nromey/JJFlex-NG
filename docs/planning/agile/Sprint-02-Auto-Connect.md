# Sprint 2: Seamless Auto-Connect

## User Story

> **As a remote operator, I want to seamlessly auto-connect to my preferred radio when I launch the app, so that I don't have to click through login screens every time.**

## Acceptance Criteria

1. **Silent startup connection**: If saved SmartLink account exists AND auto-connect radio is configured, app connects automatically on startup without showing any dialogs
2. **Disable option**: User can disable auto-connect from RigSelector or SmartLinkAccountSelector
3. **Graceful failure**: If auto-connect fails (token expired, radio offline, etc.), show RigSelector with informative error message
4. **Accessibility**: Screen reader announces connection status ("Connecting to W1ABC radio...", "Connected", or failure reason)
5. **Remove Login button**: Consolidate to single "Remote" button (Login is now implicit)

## Background

### What exists (from Sprint 1)
- `SmartLinkAccountManager` - Saved accounts with DPAPI encryption
- `SmartLinkAccountSelector` - UI for picking/managing accounts
- `AuthFormWebView2` - PKCE auth flow with refresh tokens
- Token refresh on expiration

### What exists (legacy)
- `RigSelector.AutoConnectData` - Stores radio serial, enabled flag, low-BW preference
- `RigSelector.AutoConnectTimer` - Fires ~100ms after RigSelector loads
- Per-operator config: `{OperatorName}_autoConnect.xml`

### The gap
Currently, even with saved accounts, the user must:
1. Click "Remote" button
2. See account selector (new in Sprint 1)
3. Pick account or wait for discovery
4. See RigSelector with radio list
5. Wait for auto-connect timer OR manually click Connect

**Sprint 2 goal**: Steps 1-4 happen automatically and silently on startup.

## Implementation Plan

### Task 1: Add "Auto-Connect on Startup" flag to SmartLinkAccountManager
- New property on `SmartLinkAccount`: `AutoConnectOnStartup` (bool)
- New property: `AutoConnectRadioSerial` (string) - which radio to connect to
- Integrate with existing `_autoConnect.xml` or create unified config

### Task 2: Modify Form1 startup sequence
- Before showing RigSelector, check:
  - Is there a saved SmartLink account?
  - Is AutoConnectOnStartup enabled?
  - Is there a configured auto-connect radio?
- If all yes: silently authenticate and connect

### Task 3: Create silent connection flow
- New method: `FlexBase.TryAutoConnect()`
- Uses saved account tokens (refreshes if needed)
- Connects to SmartLink server
- Waits for radio discovery
- Connects to saved radio serial
- Returns success/failure

### Task 4: Handle failures gracefully
- Token refresh failed → Show account selector with message
- Radio not found → Show RigSelector with "Radio offline" message
- Network error → Show error dialog with retry option

### Task 5: Update SmartLinkAccountSelector
- Add checkbox: "Auto-connect on startup"
- Show which radio is configured for auto-connect
- Option to clear auto-connect setting

### Task 6: Remove separate Login button
- Remove from main UI
- "Remote" button handles everything

### Task 7: Accessibility announcements
- On auto-connect start: "Connecting to [radio name]..."
- On success: "Connected to [radio name]"
- On failure: Announce the failure reason

## Files to Modify

| File | Action | Description |
|------|--------|-------------|
| `Radios/SmartLinkAccountManager.cs` | EDIT | Add auto-connect properties |
| `Radios/SmartLinkAccountSelector.cs` | EDIT | Add auto-connect UI |
| `Radios/FlexBase.cs` | EDIT | Add TryAutoConnect() method |
| `Form1.vb` | EDIT | Check auto-connect on startup |
| `RigSelector.vb` | EDIT | Integrate with SmartLink auto-connect |
| Main form (TBD) | EDIT | Remove Login button |

## Out of Scope

- Multi-radio auto-connect (only one radio at a time)
- Scheduled connections (connect at specific times)
- Auto-reconnect on disconnect (separate feature)

## Testing Plan

1. **Happy path**: Saved account + auto-connect enabled → App starts connected
2. **No saved account**: Falls back to showing RigSelector
3. **Expired token**: Refreshes silently, connects
4. **Refresh fails**: Shows account selector with "Please log in again"
5. **Radio offline**: Shows RigSelector with "Radio not found" message
6. **Auto-connect disabled**: Shows normal RigSelector flow
7. **Accessibility**: Verify NVDA announces all connection states

## Dependencies

- Sprint 1 complete (SmartLink saved accounts)
- Access to a remote radio for testing

---

*Sprint 2 planning document - created 2026-01-31*
