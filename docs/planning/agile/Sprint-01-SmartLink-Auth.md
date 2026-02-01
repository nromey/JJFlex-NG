# Sprint 1 — SmartLink Authentication & Saved Accounts

Date created: 2026-01-31
Status: Planned

## Sprint Goal
Enable users to authenticate once with SmartLink and reconnect to remote radios without re-entering credentials.

Sprint duration: 1–2 weeks (solo Agile sprint)

---
## User Story

### AUTH-01 — Save and reuse SmartLink logins

As a JJ Flex user,  
I want to save my SmartLink login information,  
so that I can quickly connect to remote radios without re-entering credentials every time.

---
## Acceptance Criteria

### Functional
- User can authenticate to SmartLink via WebView2
- User is prompted to save login after successful authentication
- User can assign a friendly name to the saved login (default: callsign/email)
- User can select from multiple saved logins
- Saved logins are reused without re-entering credentials
- Token refresh handled automatically when possible
- User prompted to re-authenticate if token expires

### Security
- No plaintext passwords stored
- Tokens stored using Windows secure credential storage
- User can delete saved accounts

### Accessibility
- Fully keyboard accessible authentication flow
- NVDA and JAWS can enter credentials without hangs
- All dialogs and prompts are screen-reader friendly
- Errors presented in plain English

### UX
- First-time login is guided and clear
- Returning users can connect in two steps or fewer
- Error states are recoverable and understandable

---
## Tasks
- Create SmartLinkAccountManager
- Implement WebView2 SmartLink authentication flow
- Capture and persist refresh tokens securely
- Build saved account selection UI
- Implement token refresh and expiration handling
- Add delete/manage saved accounts functionality
- Test with NVDA and JAWS
- Update changelog and documentation

---
## Out of Scope (Not This Sprint)
- Multi-radio simultaneous connections
- Auto-connect on application startup
- Licensing or premium feature logic
- Radio preference profiles
- Audio routing or mixer changes

---
## Definition of Done
- User can log in once and reconnect without re-entering credentials
- Saved accounts persist across restarts
- Authentication works with NVDA and JAWS
- No regressions to local radio connections
- All acceptance criteria met

---
## Notes / Risks
- Validate SmartLink token lifetime and refresh behavior
- Carefully test WebView2 focus handling with screen readers
