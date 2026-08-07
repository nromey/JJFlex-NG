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

---

## Design decisions (Track E, 2026-08-07)

**One row per radio, always.** A radio that is local AND SmartLink-registered
gets one row plus a Connection path combo box, not two rows. Two rows carrying
the same nickname read as two radios to anyone arrowing the list, and the second
one is a trap: pick the wrong copy and you get the wrong transport with no way
to tell. The combo stays present and populated even when only one path exists —
disabled, naming that path — so "how will this connect?" always has a visible,
speakable answer rather than a blank.

**The SmartLink path is real or it fails; it never falls back silently.**
`FlexBase.Connect(serial, lowBW, preferWanPath: true)` resolves only from the
banked WAN radio objects. If the SmartLink identity is gone, the connect returns
false rather than quietly using the LAN object. A silent substitution would make
the selector's spoken "over SmartLink" false, and this is precisely the path
Noel uses to test WAN behaviour from inside his own shack — a fallback would
make the test meaningless while looking like success.

**Favorites sort above live radios, not below.** The instruction says favorites
sort first and that is what a favorites list means. The consequence is that an
offline favorite can sit above a live non-favorite; the row says "offline" and
refuses to connect, and auto-select-single only ever picks a LIVE radio, so the
cost is one extra arrow press. The alternative (live first, favorites within
live) was rejected as quietly redefining what the user asked for.

**Offline rows refuse to connect, but are never a dead end.** Enter on a radio
last seen over SmartLink starts a genuine remote look and says so. Enter on one
last seen locally says it is not on the network and may be powered off. Nothing
connects from remembered data. The rejected alternative was remembering the
intent and auto-connecting when the radio turned up — conservative option taken,
because an auto-connect the user did not press Enter for a second time is a
surprise, and this dialog's whole job is being predictable.

**Ghost sweep now marks rows offline instead of deleting them.** The sweep's
purpose was to stop offering radios that will only fail to connect; an offline
row that refuses to connect satisfies that, and deleting the row would make the
list forget a radio the operator still owns. A dual-homed radio losing one home
announces which door closed rather than "went offline", which would be false.

**Sightings are written once per radio per selector session.** A LAN radio
re-announces about once a second; writing `config.xml` at that rate to update a
timestamp read once per launch would be filesystem abuse. The favorite flag is
likewise never read from disk on the discovery thread — a radio with no roster
row has never been seen here, so it cannot be a favorite.

**Account list cache lives in `radioConnectionCacheV1.xml` as a NEW top-level
element**, not folded into `Entries`. Entries is schema-parity-locked with the
4.2 discovery cascade; `AccountLists` is additive and XmlSerializer ignores
unknown elements, so a 4.2-line build reading the file is unaffected.

**Account switch forces a session cycle.** SmartLink sends its radio list once
per TLS session, so reusing the previous account's live session would answer
with the previous account's radios. The switch takes the refresh path even when
no remote pass has succeeded yet.

**New accelerator: Alt+P** for the Connection path control (dialog accelerator,
not a KeyCommands binding). Documented in
`docs/help/md/keyboard-reference.md` under a new Radio Selector section. Flagged
here per the "no key bindings without flagging the orchestrator" rule.

**Bug fixed in shared code, deliberately:** the WAN radio-list loop in
`FlexBase.wanRadioListReceivedHandler` used `break` where it meant `continue`,
abandoning the rest of the list on the first already-known radio. With two
SmartLink radios the second never raised RadioFound, and a dual-homed radio
(always already known, because LAN finds it first) killed the loop on iteration
one — which would have made dual-homing detection work for exactly one radio.

**Row 4 of the selector grid is left empty on purpose** as the detail area for
Track D's network identity card. Nothing in this dialog claims it.
