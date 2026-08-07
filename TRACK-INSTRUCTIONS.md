# QB Track E — Selector, roster, dual-homing

**Recommended model: Opus.** Well-specified UI work with established
patterns. If a genuine design fork appears, pick the conservative option and
flag it in your completion report.

## Context

One of six parallel tracks in the 2026-08-07 queue-burn (plan:
`docs/planning/active/nightowl-pileup-ragchew.md`). JJ Flex is a
screen-reader-first FlexRadio client. This track owns `RigSelectorDialog`
ENTIRELY — no other track touches it. The selector just went through a major
verified slice (Enter-to-connect, remote-first startup, Use Now, Refresh
Remote List morph, ghost sweep — all confirmed live); you are building on
fresh, working code. Do not regress any of it.

Read first:
- `docs/planning/active/research-queue.md` — Track E section
- `docs/planning/agile/archive/track-instructions/track-dialog-sweep-C2.md`
  — items 6, 7, 14, 15 (full context for the four C2 items you inherit)
- `CLAUDE.md` — accessibility + build rules

## What exists today (verified)

- Serial-keyed per-radio store `radios\<serial>\config.xml` — profile stub
  written on every connect attempt; holds nickname + connection metadata.
- `FlexBase.RadioFound` / static `FlexBase.RadioRemoved` events — live
  presence, both LAN (17s expiry via FlexLib RadioListMaid, confirmed
  working 2026-08-07) and WAN ghost sweep (diff on refreshed list).
- `radioConnectionCacheV1.xml` — already holds serial/firmware/LAN-WAN per
  radio.
- Dual-homing today: one row per serial, LAN discovery re-announces every
  second, so a radio that is both local and SmartLink-registered always
  presents as "local"; its WAN identity never shows; connect prefers LAN.
- The remote list arrives once per TLS session; Refresh Remote List cycles
  the session (`FlexBase.RefreshRemoteRadios`).

## Work items

1. **Known-radios roster.** Enumerate the per-radio store and present every
   radio this install has ever seen — regardless of current discoverability.
   Add display metadata to the per-radio config (favorite flag, last-seen
   timestamp, last-seen-via account) — APPEND-ONLY field additions; Track C
   is adding network-mode fields to the same class, keep names orthogonal.
   Selector marks each row live/offline from RadioFound/RadioRemoved;
   favorites sort first; row accessible names carry state ("6300inshack,
   remote via SmartLink, offline").
2. **Dual-homing with path CHOICE** (Noel, 2026-08-07). Surface both homes
   for a dual-homed radio ("local network" and "remote via SmartLink") and
   let the user choose the connection path — "Connect via SmartLink even
   though it's local." Default stays LAN (it's the better path); the choice
   is explicit per connect. Three payoffs: users learn both paths exist,
   Noel can test WAN behavior from home, and the roster is honest. Design
   note: one row per radio with a path affordance beats two rows (screen
   reader users arrow the list; duplicate rows read as duplicate radios).
3. **Per-account cached radio list as fast paint, not authority.** On
   account switch, paint the cached list immediately (speakable at once),
   kick the live fetch in parallel, replace + announce "radio list updated."
   Provenance beats TTL: "last known radios for <account>, refreshing";
   age-announce entries older than a few minutes. NEVER connect from cache
   without a refresh in flight. Extend `radioConnectionCacheV1.xml` with
   account-keyed lists + timestamps — do not add a second store.
4. **LAN/remote in every row's accessible name** ("FLEX-8600, local network"
   vs "6300inshack, remote via SmartLink").
5. **C2 item 6 — empty-list announcement collision.** "No radios found yet"
   (500ms Loaded-handler announcement) gets stomped by discovery landing
   right after. Only announce if the list is still empty after discovery has
   had a real chance; skip if radios arrive within the settle window.
6. **C2 item 7 — state-driven SmartLink account button.** Zero saved
   accounts → "Sign in to SmartLink"; one → "SmartLink Account"; two+ →
   "Switch Account". Content AND AutomationProperties.Name. State source:
   extend `SmartLinkAccountManager.AnySavedAccounts()` to a count. Refresh
   the label after the account manager closes. Also fix:
   SwitchAccountButton_Click speaks "Account updated. Press Remote to
   connect." even when the user cancelled — only speak on actual change.
   Keep the label logic in one helper.
7. **C2 item 14 — arrow escape.** Arrowing off the top of the radio list
   lands on the auto-connect checkbox. Arrows must stay inside the list at
   both ends (`KeyboardNavigation.DirectionalNavigation="Contained"` on the
   right scope); Tab remains the way out; Shift+Tab returns to the selected
   row.
8. **C2 item 15 — say which account is active.** Speak it when Remote is
   pressed ("Connecting to SmartLink as dbreda@mail.com"); expose it as
   readable text near the account button; the button's accessible name
   carries it. Compose with item 6's state-driven label — one helper, not
   two.

## Ownership boundaries (do not cross)

- `RigSelectorDialog.*` is yours alone. SettingsDialog belongs to B/C/F.
  Connect failure messaging is Track D's. The per-radio store SCHEMA is
  shared with Track C — append-only, orthogonal names, no refactors of
  existing fields.
- Track D is building a network identity card control that may later land in
  the picker's detail area — leave a sane place for it, don't build one.
- No key bindings without flagging the orchestrator.

## Build & verify

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```
Timestamp must be fresh. Verify with the NVDA mental model: list navigation
announces position ("1 of 3"); every state change speaks; no regressions to
Enter-to-connect, remote-first startup, Use Now, Refresh morph, or ghost
sweep — retest each after your changes.

## Commit style

Commit after each work item: `QB Track E: <what changed>`. Push to `origin`
(never `upstream`). Report completion to Noel when done.
