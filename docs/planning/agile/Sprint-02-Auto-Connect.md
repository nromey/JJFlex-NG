# Sprint 2: Seamless Auto-Connect

## User Stories

### Primary Story
> **As an operator (local or remote), I want to seamlessly auto-connect to my preferred radio when I launch the app, so that I don't have to click through selection screens every time.**

### Supporting Stories

**Story 2a - Switch Radios**:
> "As an operator, I want to disconnect from my current radio and connect to a different one, so that I can switch between radios without restarting the app."

**Story 2b - Graceful Offline Handling**:
> "As an operator, when my auto-connect radio is offline, I want a friendly message with clear options, so that I'm not stuck or confused."

---

## Acceptance Criteria

### Core Auto-Connect
1. **Works for local AND remote radios** - Not just SmartLink
2. **Silent startup**: If auto-connect is configured, app connects automatically without dialogs
3. **Single radio only**: Only ONE radio can have auto-connect enabled at a time
   - Setting auto-connect on Radio B automatically disables it on Radio A
   - UI prevents enabling auto-connect on multiple radios

### Failure Handling (Radio Offline)
4. **Friendly offline dialog** when auto-connect radio is unavailable:
   - Message: "[Radio Name] is not available. Would you like to:"
   - Option A: "Try Again" - Retry connection
   - Option B: "Disable Auto-Connect" - Turn off auto-connect for this radio
   - Option C: "Choose Another Radio" - Open RigSelector
   - Option D: "Cancel" - Close dialog, stay disconnected
5. **No crashes or error states** - Always graceful degradation

### Radio Switching
6. **Disconnect and switch**: User can disconnect current radio and open RigSelector
7. **Switch doesn't require restart** - Can go from Don's radio to Margaret's radio in one session

### Auto-Connect Management
8. **Disable option per-radio**: Can turn off auto-connect for a specific radio
9. **Global disable**: Button/option to disable ALL auto-connect (master switch)
10. **Clear auto-connect**: Can remove auto-connect setting entirely

### Accessibility
11. **Screen reader announces** all connection states:
    - "Connecting to [Radio Name]..."
    - "Connected to [Radio Name]"
    - "[Radio Name] is offline"
    - "Disconnected from [Radio Name]"

### UI Cleanup
12. **Remove separate Login button** - Remote button handles everything

---

## Constraints (MUST be true)

| Constraint | Rationale |
|------------|-----------|
| Only ONE radio can have auto-connect enabled | Prevents confusion about which radio will connect |
| Setting auto-connect on new radio disables previous | Automatic mutual exclusion |
| Auto-connect setting stored per-operator | Different operators may have different preferences |
| Local radios supported, not just remote | Feature parity for all users |

---

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
- Right-click menu on radio list: "Connect" and "AutoLogin"

### The gap
Currently:
- Auto-connect only works after RigSelector is shown
- No graceful handling when radio is offline
- No way to switch radios without closing/reopening RigSelector
- Local and remote auto-connect are separate code paths

---

## Implementation Plan

### Task 1: Unify Auto-Connect Configuration
- Merge local and remote auto-connect into single config
- Store in `{OperatorName}_autoConnect.xml`:
  ```xml
  <AutoConnectConfig>
    <Enabled>true</Enabled>
    <RadioSerial>12345678</RadioSerial>
    <RadioName>Don's 6600</RadioName>
    <IsRemote>true</IsRemote>
    <SmartLinkAccountEmail>don@example.com</SmartLinkAccountEmail>
    <LowBandwidth>false</LowBandwidth>
  </AutoConnectConfig>
  ```
- Enforce single-radio constraint in save logic

### Task 2: Create "Radio Offline" Dialog
- New dialog: `AutoConnectFailedDialog`
- Options: Try Again, Disable Auto-Connect, Choose Another, Cancel
- Accessible with screen reader
- Remembers "Don't ask again" preference (optional)

### Task 3: Modify Startup Sequence (Form1)
- Before showing any UI, check auto-connect config
- If enabled:
  - For remote: silently authenticate with saved account
  - For local: check if radio is discovered
  - If radio found: connect silently
  - If radio not found: show AutoConnectFailedDialog

### Task 4: Add "Switch Radio" Functionality
- New menu item or button: "Switch Radio" / "Change Radio"
- Disconnects current radio
- Opens RigSelector to pick new radio
- Works for both local and remote

### Task 5: Add Global Auto-Connect Disable
- Checkbox in settings or RigSelector: "Enable auto-connect on startup"
- When unchecked, always shows RigSelector regardless of saved config
- Quick way to temporarily disable without clearing per-radio settings

### Task 6: Update RigSelector UI
- Show which radio has auto-connect enabled (icon or text)
- Context menu: "Set as Auto-Connect" / "Clear Auto-Connect"
- Prevent setting auto-connect on multiple radios (disable option if one already set, or auto-clear previous)

### Task 7: Remove Login Button
- Remove from main form
- "Remote" button handles account selection and connection

### Task 8: Accessibility Announcements
- Add Tolk.Speak() calls at all connection state changes
- Test with NVDA

---

## Files to Modify

| File | Action | Description |
|------|--------|-------------|
| `Radios/AutoConnectConfig.cs` | CREATE | Unified auto-connect configuration class |
| `Radios/AutoConnectFailedDialog.cs` | CREATE | Friendly offline handling dialog |
| `Radios/FlexBase.cs` | EDIT | Add TryAutoConnect(), SwitchRadio() methods |
| `Form1.vb` | EDIT | Check auto-connect on startup before showing UI |
| `RigSelector.vb` | EDIT | Update context menu, show auto-connect status |
| `RigSelector.Designer.vb` | EDIT | UI changes for auto-connect indicators |
| Main form | EDIT | Add "Switch Radio" menu/button, remove Login |

---

## Out of Scope (future sprints)

- Multi-radio simultaneous connections
- Scheduled connections (connect at specific times)
- Auto-reconnect on unexpected disconnect
- Per-radio audio device preferences

---

## Testing Plan

| Test Case | Expected Result |
|-----------|-----------------|
| Auto-connect enabled, radio online | App starts connected, no dialogs |
| Auto-connect enabled, radio offline | Friendly dialog with options |
| Auto-connect disabled | RigSelector shown on startup |
| Set auto-connect on Radio B when A is set | Radio A auto-connect cleared automatically |
| Try to enable auto-connect on two radios | UI prevents this (only one allowed) |
| Click "Switch Radio" while connected | Disconnects, shows RigSelector |
| Disable global auto-connect | Always shows RigSelector |
| Remote radio, token expired | Silent refresh, then connect (or re-auth if refresh fails) |
| NVDA running | All states announced |

---

## Agile Notes

### How we handled complexity
1. **Started with one user story**, then identified edge cases through discussion
2. **Split into supporting stories** when scope expanded (switching, offline handling)
3. **Added acceptance criteria** to capture specific behaviors
4. **Documented constraints** to prevent invalid states (only one auto-connect)
5. **Kept "out of scope"** list to defer features for future sprints

### Definition of Done
- [ ] All acceptance criteria met
- [ ] All test cases pass
- [ ] Code reviewed and committed
- [ ] NVDA tested for accessibility
- [ ] Documentation updated (CLAUDE.md if needed)

---

*Sprint 2 planning document - Updated 2026-01-31*
