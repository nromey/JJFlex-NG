# Elmer, beacon, patch — the account-aware roster, executable

**What this is:** the implementation plan for the unified radio roster. The
*design* is already ratified and lives in
`docs/planning/active/qsl-roster-ragchew.md`; do not re-decide anything settled
there. This file is the build order, with the file and line references verified
against the tree on 2026-08-10, plus a new Phase 0 that the 2026-08-10 bug
report added in front of everything else.

**Launch prompt for the executing session:**
`Start the account-aware roster work from docs/planning/active/elmer-beacon-patch.md`

**Branch:** `track/flexlib-4220`. All line references below were checked against
`b5a3aa97`. If they have drifted, trust the symbol name over the number.

**Updated 2026-08-10 during the live guided run.** Phase 0.5 was added after
Noel hit three defects in the radio selector at the keyboard — two of them
blocking, one of them systemic across every dialog in the app. Phase 0.5 runs
**before** Phase 1, because Phase 1 adds content to the same dialog and one of
the defects is that the bottom of that dialog is already unreachable. Annotated
evidence lives under steps 1 and 2 of
`docs/planning/active/nightowl-guided-testing.md`.

**Read before starting, in this order:**

1. `docs/planning/active/qsl-roster-ragchew.md` — the ratified design and the
   non-negotiables. This plan assumes you have read it.
2. The "Corrections" section below — two of that document's stated premises are
   wrong, and one of its open questions is now closed.
3. `memory/project_smartlink_token_lineage.md` — why anything touching SmartLink
   auth gets handled carefully.

---

## Why Phase 0 exists — the live bug, 2026-08-10

Noel reported that every launch produces a registration advisory naming the
wrong account: not "this radio is not registered" but "this radio is not
registered to dbreda@mail.com" — Don's account, on Noel's machine, for Noel's
own 8600.

Root cause, confirmed end to end:

`TryLoadSavedAccount` (`Radios/FlexBase.cs:4015-4031`) resolves the current
SmartLink account as `accounts.OrderByDescending(a => a.LastUsed).First()`. It
never consults the saved default account. The saved default lives somewhere
else entirely — `AutoConnectConfig.SmartLinkAccountEmail`, reached from the VB
side through `GetDefaultSmartLinkEmail` (`globals.vb:1184` and again at
`globals.vb:2654`).

So the application holds **three disagreeing notions of "the current account"**:
the saved default, most-recently-used, and session-adopted-on-sign-in
(`AdoptSignedInAccount`, `FlexBase.cs:4043`). The 2026-08-09 selector
investigation found the AutoStartRemote check reading the default correctly;
this path reads a different one. Same question, same app, two answers.

**It is a latch, not merely a bad default.** `SmartLinkAccountManager.cs:437`
sets `account.LastUsed = DateTime.UtcNow` inside the *token refresh success
path*. Each launch picks the most-recently-used account, refreshes its token,
and thereby re-stamps it as most-recently-used. Signing in as yourself corrects
it for exactly one launch. Noel's on-disk state on 2026-08-10 showed
`dbreda@mail.com` at LastUsed 08/10 00:48:25 (that night's launch) against
`nromey@fastmail.com` at 08/07 02:25:31 — three days cold and structurally
unable to win.

**The part that matters more than the wording.** The trace shows the resolution
does not stop at a label:

- `TryLoadSavedAccount: loaded saved account 'dbreda@mail.com'`
- `GetJwtFromSavedAccount: BEGIN email=dbreda@mail.com, interactive=False`
- `Coordinator: created session id=05f67fb4078c account=dbreda@mail.com`
- `SuggestRegistration: 4925-1213-8600-6245 not registered to dbreda@mail.com`

The third line is an authenticated SmartLink session on another operator's
account, opened from Noel's machine, unannounced, on every launch. That
directly violates the constraint `qsl-roster-ragchew.md` calls non-negotiable:
*never authenticate as someone else without saying so first*. Fixing only the
advisory text would have left this running.

Traces: `%AppData%\JJFlexRadio\JJFlexRadioTrace-20260809-194811.txt` (line 227
onward) and `-20260809-152732.txt` (line 213 onward).

---

## Corrections to the ratified design, verified 2026-08-10

Carry these into the work; do not follow the design doc blindly on these three
points.

- **`LastSeenViaAccount` is NOT "simply never written."** The design says the
  field exists and is never populated. It is written — `KnownRadioRoster.cs:222-223`,
  inside `RecordSighting`, guarded by `if (isRemote && !string.IsNullOrWhiteSpace(accountEmail))`.
  The guard is deliberate and correct (a LAN sighting says nothing about which
  account reaches a radio remotely, so it must not erase a known answer). What
  this means for Phase 2: the *observation* half of the two-field split already
  exists and works. Only `PreferredAccount` is new. Profiles written before
  this code landed are still empty, which is why Don's 6300 read unattributed —
  so plan for empty-field backfill rather than assuming the write path is
  broken.
- **The line number for `LastSeenViaAccount` on `RadioConfig` is 143, not 139.**
- **The radio list already has a context menu.** The design doc's open question
  ("Does the radio list already have a context menu to extend, or does one need
  building? Not yet checked.") is closed: yes. It is declared at
  `JJFlexWpf/Dialogs/RigSelectorDialog.xaml:47-57`, opened via
  `RadiosBox_ContextMenuOpening` (`RigSelectorDialog.xaml.cs:1093`), and already
  carries Connect, favorite toggle, and Auto-Connect Settings items. Phase 2
  adds an item to an existing menu; it does not build a menu.

---

## Phase 0 — one account resolution, consulted everywhere

The goal is not "prefer the default." The goal is **one resolver**, so the app
stops being able to disagree with itself.

**The canonical resolver already exists** and already implements the right
precedence: `ResolveSmartLinkAccount()` at `globals.vb:2402-2418`. It returns
the sole account when only one is saved, then the session "Use Now" override,
then the saved default, and deliberately returns `Nothing` when several
accounts exist and none has been chosen. Its own comment explains why that last
case must not guess: pretending otherwise lets the selector name an account the
connect never uses.

**The dependency points the wrong way.** `ResolveSmartLinkAccount` is `Friend`
in the VB main assembly; `FlexBase` lives in `Radios.dll`, which the main app
references. `FlexBase` cannot call it directly. Follow the pattern already used
for exactly this problem: a static hook on `FlexBase` that `globals.vb` wires at
startup, mirroring how `WpfMainWindow.GetDefaultSmartLinkEmail` is wired.

**Wire it in both places.** There appear to be two wiring blocks that set the
same callbacks — around `globals.vb:1177-1188` and around `globals.vb:2647-2658`.
Determine which is live (or whether both run on different paths) and wire the
new hook consistently. A hook wired in only one of them produces a bug that
reproduces on one startup path and not the other, which is expensive to chase.

**Change `TryLoadSavedAccount` to ask the hook first**, falling back to the
existing `LastUsed` ordering only when the hook is unwired — that fallback
exists solely so `Radios.dll` stays usable standalone, and should trace loudly
when taken.

**Handle the ambiguous case honestly — this is the subtle part.** When the
resolver returns nothing (two or more accounts, no default, no session
override), do **not** fall back to most-recently-used. That is the exact
behaviour being removed. Return false and let each caller respond correctly.
There are two callers and they need different treatment:

- `QuerySmartLinkRegistrationAsync` (`FlexBase.cs:2558`). With no account,
  `anySaved` is true, so it returns `Unknown`, and `Unknown` means the advisory
  stays silent. That is already the correct outcome and needs no change — the
  method's own documentation says a suggestion built on a guess is worse than
  no suggestion. Confirm the path, do not modify it.
- `PreflightSmartLinkRegistration` (`FlexBase.cs:2443`). This one **is** wrong
  today in the ambiguous case: its `BlockReason` reads "No SmartLink account is
  signed in. Sign in to SmartLink first." With several accounts saved and none
  chosen, that sentence is false and sends the operator to re-sign-in when the
  actual fix is choosing a default. Split it into two messages — genuinely no
  accounts saved, versus several saved and none chosen — and in the second
  case point at the account picker rather than at sign-in.

**Confirmed live 2026-08-10, and the display makes it worse.** Noel opened the
SmartLink account manager: Don's account listed **first**, his own **second**,
and his own correctly **marked as default**. So the dialog renders the right
answer and orders the list by a different field — while the resolver a few files
away prefers that same ordering field over the default just displayed. Two
answers on one screen, inches apart. **Fixing the resolver alone is not enough:**
the operator would still be looking at a list whose order implies the wrong
account is in play. Order the account manager by something the operator chose —
default first, then alphabetical or friendly name — or show the default marker
prominently enough that ordering stops carrying implied meaning.

**Then remove the latch at its source.** `SmartLinkAccountManager.cs:437`
stamping `LastUsed` inside the token-refresh success path conflates "the
operator chose this account" with "the program touched this account." Only the
first meaning is useful. Stamp `LastUsed` on deliberate user action — sign-in,
explicit switch, `MarkAccountUsed` (`SmartLinkAccountManager.cs:695`) — and not
on background refresh. Do this even though Phase 0 stops reading `LastUsed` for
resolution: the field is still shown in the account manager ordering
(`MainWindow.xaml.cs:3519`, `globals.vb:2479`), where "last used" currently
means "last silently refreshed" and misinforms the operator.

**Flag for Noel, do not act on it unasked.** `SmartLinkAccountManager.cs:432-434`
overwrites the stored refresh token when the server returns a rotated one. If
that tenant has refresh-token rotation enabled, one machine renewing another
operator's token could invalidate that operator's own copy. There is no
evidence rotation is on, and Don's 2026-08-06 lockout was root-caused to
id_token lineage and closed — so this is a question to answer, not a diagnosis
to repeat. Answering it needs a tenant setting Noel can check.

**Acceptance for Phase 0.** Launch with two accounts saved and Noel's set as
default: the trace resolves to `nromey@fastmail.com`, no SmartLink session is
opened on any other account, and if the advisory fires at all it names the
default. Launch with two accounts and no default: no session is opened, the
advisory is silent, and the Radio Setup preflight explains that a default needs
choosing rather than claiming nobody is signed in.

---

## Phase 0.5 — the selector dialog is not fully navigable

Found live at the keyboard on 2026-08-10, radio powered off, by the operator
this application exists for. Two of these are blocking: there is content in the
radio selector that a keyboard user simply cannot reach. Do this phase before
Phase 1, which adds more content to the same dialog.

### 0.5a — Shift+Tab does not wrap, in every dialog

`JJFlexDialog` (`JJFlexWpf/JJFlexDialog.cs:15`) derives from `Window` and never
sets `KeyboardNavigation.TabNavigation`. WPF's default for that property is
`Continue`, which does not cycle at either end of the tab order; a dialog wants
`Cycle`. Nothing in the base class constructor sets it, and
`RigSelectorDialog.xaml` does not set it either.

Noel's report: "I can tab forward but I can't shift tab back through all
options."

**This is not a radio-selector bug.** Every dialog deriving from `JJFlexDialog`
inherits it, which is most of the application's dialogs. Fix it in the base
class constructor beside the other accessibility defaults already set there
(`ResizeMode`, `WindowStartupLocation`, the Escape handler). One line, applied
once, repaid on every surface.

Verify on at least three unrelated dialogs, not just the selector — the point of
a base-class fix is that it lands everywhere, and the point of checking three is
that a dialog overriding the property locally would silently opt out.

### 0.5b — the identity card and account line are unreachable

Noel tabbed the selector with no radio connected and reached the Test button and
the auto-connect control, but never the network identity card and never the
read-only SmartLink account line.

**Rule out the obvious causes first — they are already ruled out.** All three of
these were checked on 2026-08-10 and are healthy:

- The card is present and positioned: `RigSelectorDialog.xaml:135`, Grid.Row 5,
  with its bold heading at row 4. The Grid declares six rows, so the rows exist.
- The account line is present and is deliberately a real tab stop, not a
  TextBlock: `RigSelectorDialog.xaml:118`, Grid.Row 3, a read-only `TextBox`.
- The card populates itself even with no rig. `NetworkIdentityInfo.BuildLines`
  (`Radios/NetworkIdentityInfo.cs:34-39`) returns "No radio connected." plus a
  follow-on line for a null or disconnected rig, and `NetworkIdentityCard`'s
  constructor calls `Refresh()` directly. So this is **not** the empty-ListBox
  tab trap that bit this dialog before, and **not** an unwired `GetCurrentRig`
  callback.

**A layout-overflow hypothesis was raised and then DISPROVED — do not chase
it.** The theory was that the fixed `Height="560"` plus the base class's forced
`ResizeMode = NoResize` (`JJFlexDialog.cs:35`) squeezed the trailing Auto rows
to zero. Noel retested on 2026-08-10 and reached both the account line and the
card by tabbing **forward** far enough. Nothing is clipped. Recorded here so
nobody re-derives and re-tests a dead theory.

**The actual defect is discoverability, and it is 0.5a wearing a disguise.**
The card sits late in the tab order, after the nine-control button column and
the auto-connect checkbox. Forward-tabbing reaches it; the natural motion —
Shift+Tab back from the radio list to the detail area directly above the
buttons — does not, because the dialog does not cycle. So the content is
present, announced, and effectively invisible to anyone who does not tab the
long way round by accident.

**This means 0.5a is the whole fix for reachability**, and 0.5b reduces to a
judgement call worth making deliberately: whether the identity card belongs
that late in the tab order at all. Do not reorder on impulse — the buttons are
the dialog's primary actions and should keep their position. Once cycling
works, one Shift+Tab from the list reaches the card, which is likely
sufficient. Retest before changing tab order.

**One piece of the dead hypothesis is still worth keeping as a check.** The
overflow arithmetic was wrong about today but not absurd: a fixed-height,
non-resizable dialog carrying nine stacked buttons plus five content rows has
little headroom. **Verify the selector at 125% Windows text scaling** and
confirm nothing drops off the bottom. If it does, fix it by removing the
fixed-height constraint rather than by picking a larger number — a magic
height fails silently again the next time a control is added.

### 0.5c — the roster row says "last seen" twice

Heard: "last seen on the local network, last seen 4 hours ago". Noel's wording:
"last seen on the local network 4 hours ago".

`RigSelectorDialog.xaml.cs:104-111` builds `lastPath` as "last seen on the local
network" (or "last seen remote via SmartLink") and then appends `", " +
LastSeenText`, where `LastSeenText` contains "last seen" a second time. Reduce
`lastPath` to the bare path — "on the local network", "remote via SmartLink" —
and compose one sentence. Check what `LastSeenText` actually returns before
deciding which half to strip; the duplication could be removed from either side
and only one choice keeps the other call sites correct.

This lands in the same method Phase 1 rewrites, so do it as part of that edit
rather than twice.

### 0.5e — Shift+F10 does not open the row context menu

Found 2026-08-10. In the radio selector, on a radio row, **Shift+F10 does not
raise the row's context menu** — Noel got what he described as "a system tree"
instead. He has no Applications key on his current keyboard, so Shift+F10 is his
only route, and the menu is therefore unreachable.

**This is a blocker for Phase 2, not a minor annoyance.** The ratified design
makes the row context menu the keyboard-first door for setting a radio's
preferred account, with Applications key and Shift+F10 as the *primary* route
and right-click as the mouse alias. Building that feature onto a door that does
not open from the keyboard would ship an inaccessible setting.

The menu itself is correctly declared (`RigSelectorDialog.xaml:47-57`) with
`ContextMenuOpening="RadiosBox_ContextMenuOpening"`
(`RigSelectorDialog.xaml.cs:1093`), so this is a key-routing problem, not a
missing menu.

**Investigate the interop path first.** `BridgeForm.vb:16` documents that
"Windows handles Alt/F10 → menu activation automatically via DefWindowProc", and
`BridgeForm.vb:81` defines `SC_KEYMENU`. The WPF dialogs are hosted from a
WinForms shell (`JJFlexDialog.cs:37-44` notes MainWindow is a UserControl in an
ElementHost). A plausible cause is F10 being consumed by native menu activation
before WPF sees Shift+F10 — but **confirm the actual route before fixing**,
including whether the system menu or the app menu bar is what appears, since
those implicate different owners.

**Verify the fix with a real context menu, not just a keystroke that stops
misbehaving.** Shift+F10 on a radio row must open the same menu the Applications
key opens, announce as a menu, and be Escape-closable per the standing rule.
Check other list surfaces too — if the routing is interop-level, every context
menu in the app is affected and this belongs next to 0.5a as a systemic fix.

### 0.5d — read-only readouts are ListBoxes and should be read-only edits

Noel, on reaching the identity card: *"this is one of these wonky listbox boxes
we replaced with readonly edits."* This is an existing convention in this
codebase being applied inconsistently, not a new preference. The selector's own
SmartLink account line already follows it — `RigSelectorDialog.xaml:118` is a
read-only `TextBox` with a comment explaining the choice.

**Why the control type matters here.** A ListBox announces itself as a list,
reports "item 1 of 2", and treats each arrow press as changing a *selection*.
That is correct for a list of things you might act on and wrong for a block of
text you want to read. A read-only `TextBox` gives line, word and character
navigation, text selection and copy, and reads as prose. When the content is
sentences, list semantics are a lie the screen reader faithfully repeats.

**Convert these two:**

- `NetworkIdentityCard.IdentityList` (`JJFlexWpf/Controls/NetworkIdentityCard.xaml:15`).
  One control, two hosts — the selector and the Status dialog both benefit from
  a single change. Keep `AutomationProperties.Name`, keep it a tab stop, and
  preserve the `IsKeyboardFocusWithin` guard in `Refresh()`
  (`NetworkIdentityCard.xaml.cs:55`) that stops a rebuild stealing the reading
  position — that guard is the reason arrowing through the card survives a
  refresh, and its equivalent must survive the conversion.
- `StatusDialog.StatusList` (`JJFlexWpf/Dialogs/StatusDialog.xaml:18`). Noel
  flagged this one in the same breath: *"it's a listbox which could be a read
  only."*

**Do NOT convert selection lists.** Many dialogs use `ListBox` correctly — the
radio list itself, memories, groups, filter presets, log entries, audio devices.
The test is whether the user picks an item to act on it (leave it a ListBox) or
merely reads it (convert). Converting a genuine picker would break it.

**Multi-line behaviour matters.** These readouts are several lines. The
replacement needs `IsReadOnly`, `IsReadOnlyCaretVisible`, `AcceptsReturn` or
`TextWrapping` as appropriate, and must remain arrow-navigable line by line. Set
the text as one string with newlines rather than as items.

**Check whether other readouts share this shape** while you are in here, and
report what you find rather than converting everything on sight — the two above
are confirmed by the operator, anything else is your judgement and should come
back as a list for Noel.

## Phase 1 — Stage 1 of the design: attribution and honest refusal

No schema change. This phase makes the roster tell the truth about rows it
already loads.

**Scan every account list for attribution.** `KnownRadioRoster.Load()`
(`Radios/KnownRadioRoster.cs:102`) merges radio profiles and the connection
cache, then at lines 160-194 looks up **only** the passed account's cached radio
list. Attribution for every other account is sitting in the same cache and is
skipped. Extend the pass to walk all cached account lists for attribution
purposes, while keeping `InAccountCache` (line 177) true only for the matching
account — that flag means "this account can see it now," and widening it would
break the live/offline logic downstream.

First check whether the cache class exposes enumeration of all account lists;
`LookupAccountRadioList(accountEmail)` is a single-account lookup and an
all-accounts accessor may need adding. Extend `radioConnectionCacheV1.xml`;
do not introduce a second store.

**Give `WhereText` a foreign-account branch.** `RigSelectorDialog.xaml.cs:90-113`.
Line 106 requires **both** `FromAccountCache` and a non-empty
`LastSeenViaAccount` before it will name an owner, so a radio belonging to
someone else — the only case where the operator actually needs telling — falls
through to line 111 and reads "offline, last seen remote via SmartLink":
remote-flavored and anonymous. Add a branch for a row attributed to an account
that is not the current one, reading in the shape of "offline, registered to
dbreda@mail.com, last seen 2 days ago". `DescribeAge()`
(`KnownRadioRoster.cs:269`) and `AccountListFetchedUtc` (line 43) already
produce those ages; they are gated behind the same matching-account check that
causes the bug, so fixing the gate hands you the staleness wording for free.

**Stop the doomed SmartLink pass.** `HandleOfflineConnectAttempt`
(`RigSelectorDialog.xaml.cs:895-926`). Line 897 sets `remoteish` from
`LastSeenRemote || FromAccountCache`, and line 899 then fires `StartRemoteFlow()`
— a full authentication round trip **on the current account**, hunting a radio
that account can never list. Add an earlier branch: a row attributed to a
different account does not start a pass at all. It names the owning account and
offers to switch. The literal `"this account"` substitution at lines 916-917
becomes unreachable once attribution is populated, but leave the guard in place
for rows that genuinely have no attribution.

**Loaded-state announcements, with the trap.** Local and remote are different
kinds of event and must not claim to be the same. Remote is discrete — a
session opens, the server sends its list once, done — so "Remote loaded" is
true. Local is continuous, because VITA discovery keeps arriving the whole time
the picker is open, so any "local loaded" wording must say it is still
listening. Terse and Chatty wordings are specified in the design doc; both sit
at `VerbosityLevel.Terse`.

The trap, stated in the design and worth repeating because it fails silently:
**the announcement must not live inside `MorphRemoteToRefresh()`**. That method
(`RigSelectorDialog.xaml.cs:1161`) opens with `if (_remoteListLive) return;` at
line 1163, so anything inside it fires once per session and then stops forever —
it would pass a first-launch test and fail every time after. Put it in the
success branch near line 1215-1217, outside the guard.

**Add a query key as well as the announcement.** An announcement is a one-shot;
if the screen reader is mid-sentence or the operator alt-tabbed, it is gone and
they are back to inferring state from list contents. A key that reports "local
loaded, remote not loaded" on demand answers the question at the moment the
operator has it, and demotes the announcement to a convenience.

---

## Phase 2 — Stage 2 of the design: the account is a property of the radio

**Add `PreferredAccount` to `RadioConfig`**, beside `LastSeenViaAccount` at
`Radios/RadioConfig.cs:143`. Two fields, deliberately not one:
`LastSeenViaAccount` is an observation, auto-updated on remote sightings, and
already works. `PreferredAccount` is a choice — operator-set, sticky, and
**never auto-overwritten by a sighting**. Conflating them lets an incidental
listing destroy a deliberate decision with no event anyone could hear, which is
the worst class of settings bug: no symptom until it has a consequence.

**Resolution order for "which account reaches this radio":** `PreferredAccount`
if set, otherwise `LastSeenViaAccount`, otherwise the preferred-account-for-new-connections
(what Phase 0 wired up). Zero configuration works; the override exists only to
override. What justifies it existing at all is a radio reachable by two accounts
— a club rig both operators have on SmartLink — where no heuristic can choose
and last-seen-wins would flip-flop with whoever listed it most recently.

**Row activation connects on the row's own account**, announcing the account
**before** starting the session, not after it succeeds. Name the account on
every connect, not only cross-account ones — symmetry means the operator learns
the pattern instead of having to notice an exception at the moment it matters.
This is a transmit-safety line, not a politeness one: Don's 6300 is his
production station, and a unified list puts it one arrow key from Noel's 8600.

**Refresh lazily, on activation of a stale row, never at picker open.** Eager
refresh across N accounts means N SmartLink sessions every launch. The roster's
instant paint is what makes the picker usable the moment it opens; do not trade
that for freshness nobody asked for.

**Two doors, one store.** Both write the same per-serial `PreferredAccount`;
there is no second store and therefore no synchronisation problem.

- The existing per-row context menu (`RigSelectorDialog.xaml:47-57`) gains an
  item. Keyboard-first: Applications key and Shift+F10 are the primary route,
  right-click is the mouse alias. Register it in Command Finder and keep it
  Escape-closable per the standing dialog rule. This is the door for thinking
  about *a radio*.
- A radio-associations view in the SmartLink Account Manager — view which radios
  an account covers and rebind them. This is the door for thinking about *an
  account*. **It may view and rebind but must never initiate a connect.**
  Connecting lives in exactly one surface; management and navigation are
  different jobs and only the second gets two homes.

**The job only the Account Manager can do is orphans.** Delete an account and
its radios remain bound to an account that no longer exists; the per-row menu
structurally cannot reach them, because the radio may not appear in the list at
all once its account is gone. **What should happen on account deletion is not
ratified** — the design leans toward offering to rebind at delete time, that
being the one moment the operator has full context. Do not implement a rule
here without Noel's call.

**Keyboard audit applies to this phase** — it adds bindings. That means
`docs/help/md/keyboard-reference.md`, Command Finder keywords, F1 context help
for the affected controls, a changelog line, and a CHM rebuild. The full
checklist is in `CLAUDE.md` under "Keyboard Audit — Definition of Done."

---

## Out of scope

- Stage 3 of the design (renaming "default account" to "preferred account for
  new connections", and turning the Remote button into plain Refresh). It waits
  until Phases 1 and 2 are proven in use.
- Anything in the JIT id_token refresh arc. Related, separately queued.
- The FlexLib `Slice.cs:213` malformed-command bug. Unrelated, separately
  recorded.

## Build, verify, commit

Build the project, not the solution, and verify the timestamp every time:

`dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`

then confirm the exe timestamp is current at
`bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe`. A bare `dotnet build`
stamps FileVersion as `4.1.16.0` rather than the four-part number; that is
expected and not a sign of a stale binary — the timestamp is the check that
matters.

Commit per phase, not per file. Phase 0 is independently shippable and should
land on its own so it can be reverted without disturbing roster work.

## Testing

Written for a screen-reader operator; no visual-only checks.

- **Phase 0, the reported bug.** With Noel's account set as default and Don's
  also saved, launch and connect locally to the 8600. The registration advisory
  must either not fire or must name `nromey@fastmail.com`. Grep the trace for
  `Coordinator: created session` and confirm no session was opened on
  `dbreda@mail.com`.
- **Phase 0, ambiguity.** Temporarily clear the saved default with two accounts
  present. Confirm silence rather than a guess, and confirm Radio Setup's
  preflight says a default needs choosing rather than claiming nobody is signed
  in.
- **Phase 0.5a, wrapping.** In the radio selector, Tab to the last control and
  Tab once more — focus returns to the first. Shift+Tab from the first control
  reaches the last. Repeat on two dialogs that have nothing to do with the
  selector, to prove the base-class fix landed everywhere.
- **Phase 0.5b, discoverability.** With **no radio connected**, focus the radio
  list and press Shift+Tab. You should reach the identity card and the account
  line going backward, in a few keystrokes — not by tabbing forward past every
  button. The card reads "No radio connected."
- **Phase 0.5b, scaling.** Repeat at 125% Windows text scaling and confirm
  nothing drops off the bottom of the selector. This is a headroom check on a
  fixed-height dialog, not a retest of the disproved clipping theory.
- **Phase 0.5c, wording.** The offline row says "last seen" exactly once.
- **Phase 0.5e, context menu.** On a radio row, Shift+F10 opens the row context
  menu — Connect, favorite toggle, Auto-Connect Settings — announced as a menu
  and closed by Escape. Test on a keyboard with no Applications key, since that
  is the configuration the bug was found on. Then try Shift+F10 on another list
  surface: if it fails there too, the fix is interop-level and systemic.
- **Phase 0.5d, control type.** The identity card and the Status dialog's status
  readout announce as text, not as "list, item 1 of N". Arrow through by line,
  and confirm you can select and copy text. Then trigger a refresh while your
  cursor is parked mid-readout and confirm your position survives it — that
  guard exists today and must not be lost in the conversion.
- **Phase 1, attribution.** Open the selector on Noel's account with Don's 6300
  in the roster. The row must name Don's account and its age, not read as an
  anonymous remote row.
- **Phase 1, refusal.** Press Enter on that row. It must refuse immediately,
  naming the owning account and offering the switch — no authentication round
  trip, no thirty-second grind.
- **Phase 1, the guard trap.** Open the picker, refresh remote, close it, and
  open it again in the same app session. The loaded-state announcement must
  still fire the second time. This is the specific failure that
  `MorphRemoteToRefresh` would cause and that a single-launch test cannot see.
- **Phase 2, binding.** Set a preferred account on a row via the Applications
  key, then confirm a later sighting on a different account does not overwrite
  it.
- **Phase 2, announcement order.** The account name must be spoken before the
  session starts, not after it succeeds.

## Open questions for Noel

- Refresh-token rotation on the tenant: enabled or not? Determines whether one
  machine refreshing another operator's token is harmless or harmful.
- The account-deletion rule for bound radios — forget, orphan-and-show, or
  offer-to-rebind-at-delete-time. The design leans to the third.
- Whether a cross-account connect deserves a confirmation step on top of the
  spoken account name. The design leans no, on the grounds that a spoken name
  plus a deliberate Enter is already two signals, but flags it for revisit if a
  near-miss ever happens.
