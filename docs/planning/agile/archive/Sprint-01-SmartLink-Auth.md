# Sprint 1 — SmartLink Authentication & Saved Accounts

**Status:** ✅ COMPLETED
**Date Created:** 2026-01-31
**Date Completed:** 2026-01-31
**Version Released:** v4.1.10

---

## Sprint Goal
Enable users to authenticate once with SmartLink and reconnect to remote radios without re-entering credentials.

---

## User Story

### AUTH-01 — Save and reuse SmartLink logins

As a JJ Flex user,
I want to save my SmartLink login information,
so that I can quickly connect to remote radios without re-entering credentials every time.

---

## Acceptance Criteria

### Functional ✅
- [x] User can authenticate to SmartLink via WebView2
- [x] User is prompted to save login after successful authentication
- [x] User can assign a friendly name to the saved login (default: email)
- [x] User can select from multiple saved logins
- [x] Saved logins are reused without re-entering credentials
- [x] Token refresh handled automatically when possible
- [x] User prompted to re-authenticate if token expires

### Security ✅
- [x] No plaintext passwords stored
- [x] Tokens stored using Windows DPAPI (user-scoped encryption)
- [x] User can delete saved accounts

### Accessibility ✅
- [x] Fully keyboard accessible authentication flow
- [x] Screen reader announces login states
- [x] All dialogs have AccessibleName and proper tab order
- [x] Errors presented in plain English

### UX ✅
- [x] First-time login guided and clear
- [x] Returning users can connect in two steps or fewer
- [x] Error states are recoverable

---

## Deliverables

| File | Type | Description |
|------|------|-------------|
| `Radios/SmartLinkAccountManager.cs` | NEW | Account storage with DPAPI encryption, token refresh |
| `Radios/SmartLinkAccountSelector.cs` | NEW | Account selection dialog with rename/delete |
| `Radios/AuthFormWebView2.cs` | MODIFIED | PKCE auth flow, refresh token extraction, email parsing |
| `Radios/FlexBase.cs` | MODIFIED | Integrated saved accounts into setupRemote() |
| `Radios/Radios.csproj` | MODIFIED | Added System.Security.Cryptography.ProtectedData |

---

## Technical Decisions

### PKCE over Implicit Flow
**Decision:** Switched from OAuth implicit flow to Authorization Code + PKCE
**Reason:** Implicit flow cannot return refresh tokens (Auth0 security limitation)
**Outcome:** More secure authentication AND persistent sessions

### DPAPI over Custom Encryption
**Decision:** Used Windows DPAPI instead of existing StringCipher utility
**Reason:** DPAPI ties encryption to Windows user account - tokens can't be decrypted if file is copied to another machine
**Outcome:** Better security with less code

### JSON File Storage
**Decision:** Store accounts in `%APPDATA%\JJFlexRadio\SmartLinkAccounts.json`
**Reason:** Standard Windows convention, human-readable for debugging, per-user isolation
**Outcome:** Simple, reliable storage

---

## Retrospective

### What Went Well ✅

1. **PKCE was the right call** - When we discovered implicit flow couldn't return refresh tokens, switching to Authorization Code + PKCE was the correct architectural decision. More secure AND enables the feature we needed.

2. **DPAPI over custom encryption** - Using Windows built-in credential protection was simpler and more secure than rolling our own.

3. **Existing infrastructure helped** - WebView2 migration and Tolk integration were already done, giving us building blocks.

4. **Planning docs first** - Committing the plan before coding gave us a clear target.

### What Could Be Improved 📝

1. **Scope discovery during planning** - Login vs Remote button question emerged mid-sprint. Better upfront analysis would have caught this.

2. **Auth0 research** - Should have researched Auth0 flow capabilities before starting implementation.

3. **More explanation during coding** - Developer moved fast without pausing to explain decisions. For learning/collaboration, more pause points would help.

### What We Learned 🎓

1. **Auth0 PKCE flow** - How to implement secure OAuth with code_verifier/code_challenge
2. **JWT parsing** - Extracting claims (email) from id_token payload
3. **Agile workflow** - Planning → implement → commit → retrospective cycle
4. **Stakeholder feedback is valuable** - Mid-sprint questions improved the design

### Process Improvements for Next Sprint

- **Pause and explain** before writing significant code
- **Summarize** after completing each task
- **Ask "Ready to continue?"** before moving to next task
- **Update CLAUDE.md** as we go for beta testers

---

## Out of Scope (Deferred to Sprint 2)

- Auto-connect on application startup → Sprint 2
- Multi-radio simultaneous connections → Future
- Radio preference profiles → Future

---

## Related Commits

1. `70a136c3` - Cleanup: remove completed plans, add Sprint 1 planning docs
2. `8809fbec` - feat: SmartLink saved accounts with PKCE authentication (Sprint 1)
3. `2785d33d` - docs: add Sprint 2 planning for seamless auto-connect
4. `82ef7a1b` - docs: expand Sprint 2 with edge cases and constraints

---

*Archived: 2026-01-31*
