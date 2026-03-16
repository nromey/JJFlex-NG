# Connection Picker UX Design

**Status:** Proposed for Sprint 25 or backlog
**Date:** 2026-03-14

## Problem

Currently, clicking Remote always auto-connects with the last saved SmartLink account. There's no way to:
- Choose between multiple SmartLink accounts
- Pick a specific radio when an account has multiple radios
- Connect to a new account without going through Manage SmartLink
- Recover from a failed connection (menu stuck on "Disconnect", must restart)

## Proposed Flow

### Menu (simple)
- Radio menu has Connect and Disconnect
- Connect opens the RigSelector dialog
- Disconnect disconnects and resets menu state (even if connection already failed)

### RigSelector Dialog (the picker)
The connect dialog shows a list of known radios the user has connected to before:

- Each entry is an account+radio pair (e.g., "6300 inshack — Don's SmartLink", "K5NER — Noel's SmartLink")
- Entries are stored persistently so they survive app restarts
- "New Connection..." option at the bottom opens the SmartLink auth flow
- Select a radio and press Connect (or double-click/Enter)
- Local radios (discovered via VITA-49) also appear in the list when available

### Data Storage
- Persist known radios as a list of (account name, radio nickname, SmartLink account ID, last connected date)
- Store in existing config directory (e.g., `known-radios.xml` or extend `SmartLinkAccounts.json`)
- Prune entries that haven't connected in 90+ days (or let user remove manually)

### Connection Failure Recovery
- If connection fails or times out, menu MUST revert to "Connect" state
- Show spoken error: "Connection failed" or "Connection timed out"
- User can immediately try again without restarting

### Accessibility
- List must be screen-reader navigable (arrow keys, announce radio name + account)
- Access keys on Connect and Cancel buttons
- Spoken confirmation on connect: "Connecting to 6300 inshack..."

## Not In Scope
- MultiFlex multi-client management (connecting to a radio already in use by another client)
- Local radio discovery refinements
- Connection profiles (different settings per radio)
