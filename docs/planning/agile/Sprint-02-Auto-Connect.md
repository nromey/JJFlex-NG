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
   - **Confirm before switching**: When user sets auto-connect on Radio B while Radio A has it, show confirmation: "Radio A currently has auto-connect enabled. Switch to Radio B?"
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
- Keep existing Low Bandwidth toggle in context menu (quick access)
- Add "Auto-Connect Settings" dialog showing current config before save:
  - Radio name, Auto-connect enabled, Low Bandwidth checkbox
  - Confirms what user is saving ("here's what we're saving, hit OK to confirm")
- Prevent setting auto-connect on multiple radios (show confirmation per acceptance criteria)

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
| `Radios/AutoConnectSettingsDialog.cs` | CREATE | Settings review dialog before saving auto-connect |
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
| Set auto-connect on Radio B when A is set | Confirmation dialog, then Radio A auto-connect cleared |
| Try to enable auto-connect on two radios | UI prevents this (only one allowed) |
| Click "Select Rig" while connected | Disconnects, shows RigSelector |
| Disable global auto-connect | Always shows RigSelector |
| Remote radio, token expired | Silent refresh, then connect (or re-auth if refresh fails) |
| NVDA running | All states announced |
| Tab through RigSelector with NVDA | Login button NOT reachable (hidden and TabStop=False) |
| Auto-connect settings dialog | Screen reader announces radio name, checkboxes, buttons |

---

## Agile Notes

### How we handled complexity
1. **Started with one user story**, then identified edge cases through discussion
2. **Split into supporting stories** when scope expanded (switching, offline handling)
3. **Added acceptance criteria** to capture specific behaviors
4. **Documented constraints** to prevent invalid states (only one auto-connect)
5. **Kept "out of scope"** list to defer features for future sprints

### Definition of Done
- [x] All acceptance criteria met
- [x] All test cases pass
- [x] Code reviewed and committed
- [x] NVDA tested for accessibility
- [x] Documentation updated (CLAUDE.md if needed)

---

## Retrospective

**Status:** ✅ COMPLETED
**Date Completed:** 2026-02-04
**Version Released:** v4.1.11

### What Went Well ✅

1. **Unified local/remote handling** - The `AutoConnectConfig` class cleanly abstracts both local and SmartLink radios, making the auto-connect code much simpler than the legacy split-path approach.

2. **Friendly offline dialog** - The `AutoConnectFailedDialog` provides clear options instead of cryptic errors. Users can try again, pick another radio, or disable auto-connect without confusion.

3. **Screen reader announcements throughout** - All connection states are spoken: connecting, connected, offline, disconnected. No silent failures.

4. **Settings confirmation dialog** - The `AutoConnectSettingsDialog` shows users exactly what's being saved before they commit, preventing "wait, what did I just do?" moments.

5. **Audio volume fix (bonus)** - Discovered and fixed the low WAN audio volume issue (raw Opus peaks at ~16%, needed 8x boost). Not planned but critical for usability.

6. **Help page crash fix (bonus)** - .NET 8 changed `UseShellExecute` default; fixed with explicit `ProcessStartInfo`.

7. **Native DLL rebuild** - Fresh Opus 1.6.1 and PortAudio 19.7.0 builds with proper SIMD optimizations for both x64 and x86.

### What Could Be Improved 📝

1. **Version bump process still bites** - Even with documentation, the dual-file version bump (`JJFlexRadio.vbproj` + `AssemblyInfo.vb`) caught us again. Need to consider automating this.

2. **Installer pipeline complexity** - The Rube Goldberg machine of `install.bat` → template substitution → `install.nsi` generation → NSIS → rename dance is fragile. Works, but has lots of potential failure points.

3. **Testing without physical radio** - Jim doesn't have his Flex antenna'd up, so all remote testing was against Don's 6600. Should coordinate test sessions better.

### What We Learned 🎓

1. **Opus decode bypasses FlexLib gain** - The `_rxGainScalar` only applies to uncompressed IF data. Opus packets go straight from decoder to PortAudio with no gain adjustment. Had to add `OutputGain` property in `JJAudioStream.WriteOpus()`.

2. **.NET 8 Process.Start breaking change** - `UseShellExecute` now defaults to `false`, breaking file association launches.

3. **Peak level diagnostics** - Adding dBFS logging to the audio pipeline made calibration possible. Raw peaks were measurable (~0.16), leading to data-driven gain selection.

4. **Two distinct audio paths** - PC Audio (WAN→Opus→OutputGain→PortAudio) vs Local Flex Audio (Radio DSP→Slice.AudioGain→Flex speakers). Documented for Sprint 3.

### Delivered Files

| File | Type | Description |
|------|------|-------------|
| `Radios/AutoConnectConfig.cs` | NEW | Unified auto-connect configuration (local + remote) |
| `Radios/AutoConnectFailedDialog.cs` | NEW | Friendly "radio offline" dialog |
| `Radios/AutoConnectSettingsDialog.cs` | NEW | Settings review before save |
| `Radios/FlexBase.cs` | MODIFIED | TryAutoConnect(), OutputGain, connection state announcements |
| `Form1.vb` | MODIFIED | Startup auto-connect check, help page fix |
| `RigSelector.vb` | MODIFIED | Auto-connect context menu, UI cleanup |
| `JJPortaudio/JJPortaudio/AudioStream.cs` | MODIFIED | OutputGain property, peak level logging |
| `docs/TODO.md` | MODIFIED | Added Audio Sprint 3 planning |
| `build-native.bat` | NEW | Opus + PortAudio build script |
| `runtimes/win-*/native/*.dll` | REBUILT | Fresh Opus 1.6.1, PortAudio 19.7.0 |

---

*Sprint 2 completed - 2026-02-04*
