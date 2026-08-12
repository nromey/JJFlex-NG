# The unified radio roster (codename: qsl-roster-ragchew)

Design ratified with Noel in conversation, 2026-08-09 afternoon. Supersedes the
local-list / remote-list split in the radio selector.

**Execution plan:** `docs/planning/active/elmer-beacon-patch.md` — build order,
verified line references, and a Phase 0 (account resolution) added ahead of
Stage 1 after the 2026-08-10 bug report. This file stays the design; that file
is what an implementing session runs from. Three premises below were corrected
against the tree on 2026-08-10 and are marked inline.

## Why this exists: the bug that motivated it

On 2026-08-09 (~13:59, trace `JJFlexRadioTrace-20260809-135959.txt`) Noel opened
the app on his own account — `nromey@fastmail.com`, default, **AutoStartRemote
off** — and the picker listed both his 8600 and Don's 6300, looking exactly as
if Remote had been pressed. The Remote button, however, still read "Remote"
rather than "Refresh Remote List".

Diagnosis, all three legs confirmed against the trace and the on-disk config:

- **Remote did not auto-start.** No `wpfSelectorProc: remote-first startup`
  line (written only when the flag is true, `globals.vb:2544`), no SmartLink
  activity at all, and exactly one radio discovered live — the 8600 at
  192.168.50.100. The per-account `AutoStartRemote` machinery works; Don's flag
  did not leak.
- **The button was correct.** `_remoteListLive` is false because no remote pass
  ran, so "Remote" was the honest label. The remote view never loaded.
- **The list was the liar.** `PaintRoster()`
  (`RigSelectorDialog.xaml.cs:445`) calls `KnownRadioRoster.Load(accountEmail)`,
  which builds rows from `RadioConfig.LoadAllKnown()` and
  `cache.GetAllEntries()` — **neither filtered by account**. The `accountEmail`
  argument is used only at `KnownRadioRoster.cs:160-194` to stamp
  `InAccountCache` on the *matching* account's cached list.

The sting: `WhereText` (`RigSelectorDialog.xaml.cs:106`) needs **both**
`FromAccountCache` and a non-empty `LastSeenViaAccount` to name an owner. For
Don's 6300 under Noel's account both fail — the attribution lives only in the
`<AccountLists>` block that `Load()` just skipped, and the radio's own profile
(`Radios\1315-4176-6300-7236\config.xml`) carries nothing but a `<Nickname>`.
So it fell through to line 111 and read "offline, last seen remote via
SmartLink, 2 days ago": remote-flavored, unattributed.

**The label is inverted relative to need.** A radio from *your* account gets
named with the account; a radio from *someone else's* account — the only case
where the operator needs telling — gets nothing.

Second-order cost: Enter on that row (`HandleOfflineConnectAttempt:895`) sees
`LastSeenRemote` true and fires a full SmartLink auth round trip **on the wrong
account**, hunting a radio that account can never list. When it returns empty,
line 916 substitutes the literal string "this account", because
`LastSeenViaAccount` is empty — so even the failure message cannot name the
right account.

## The insight

The roster is **already unified**. It has always returned every radio the
install has met, across every account. What is split is only the interaction
model and the labels sitting on top of it. This is not a rewrite; it is the
presentation layer catching up to what the data layer already does.

## The model

One list of radios. No local mode, no remote mode. A row is a radio you might
connect to, and it carries everything needed to reach it.

- **Local rows** connect over the LAN, as today.
- **Remote rows** connect over SmartLink, using **that row's account** — not a
  globally selected one.
- **Stale remote rows** refresh on activation, not at picker open (see Lazy,
  below).

The Remote button stops being a mode switch and becomes plain **Refresh**. It
already half-became this — it morphs into "Refresh Remote List" after a
successful pass (`MorphRemoteToRefresh`, line 1161).

Rejected: a separate accounts-list navigation surface, and a setting to hoist
Remote to the top of the list. Radios are what the operator navigates; accounts
are metadata on the rows. Account *management* (add, remove, reset sign-in)
already has its own home in the SmartLink Account Manager. A second accounts
view would reintroduce the two-lists-to-reconcile problem this deletes, and if
the list is unified there is no remote section left to hoist.

## Per-radio account binding (Noel's proposal, and the core of the design)

The account is a property of the radio, not of the session. Store it per serial,
where per-radio config already lives.

**Two fields, deliberately not one:**

- `LastSeenViaAccount` — an **observation**. Auto-updated on every sighting,
  descriptive, used for labeling when nothing better exists. *This field already
  exists* on `RadioConfig` (`RadioConfig.cs:143`, read at
  `KnownRadioRoster.cs:119`). **Corrected 2026-08-10: it is NOT "never written."**
  `KnownRadioRoster.RecordSighting` writes it at `KnownRadioRoster.cs:222-223`,
  guarded by `isRemote && accountEmail` — deliberately, since a LAN sighting
  says nothing about which account reaches a radio remotely and must not erase a
  known answer. So the observation half of this split already works; only
  `PreferredAccount` is new. Profiles written before that code landed are still
  empty, which is why Don's 6300 read unattributed — plan for backfill, not for
  a broken write path.
- `PreferredAccount` — a **choice**. Set by the operator, sticky, and **never
  auto-overwritten by a sighting**.

Conflating them means an incidental listing silently destroys a deliberate
decision, with no event anyone could hear. That is the worst class of settings
bug: it has no symptom until it has a consequence.

**Resolution order for "which account reaches this radio":**

1. `PreferredAccount` if set.
2. Otherwise `LastSeenViaAccount`.
3. Otherwise the preferred-account-for-new-connections.

Zero configuration works; the override exists only to override.

**What justifies the override existing:** a radio reachable by *two* accounts —
a club rig both operators have on SmartLink. There, "which account" is a genuine
operator choice no heuristic can make, and last-seen-wins would flip-flop with
whoever listed it most recently. Most radios will never need this; the ones that
do cannot be automated.

**The setting surface — two doors, one store.** Both write the same per-serial
`PreferredAccount` field; there is no second store and therefore no sync
problem.

- **Per-row context menu in the radio list**, **keyboard-first**. Applications
  key and Shift+F10 are the primary route; right-click is the mouse alias.
  Registered in Command Finder, Escape-closable per the standing dialog rule.
  This is the door you use when thinking about *a radio*.
- **Radio associations in the SmartLink Account Manager** (Noel, 2026-08-09).
  View which radios an account covers and rebind them. This is the door you use
  when thinking about *an account*. Follows the multiple-doors principle already
  ratified for audio settings.

**Boundary that keeps the second door from reintroducing the problem this
design deletes: the Account Manager may view and rebind, but must never
initiate a connect.** Connecting lives in exactly one surface. Management and
navigation are different jobs; only the second one is allowed two homes.

**The job only the Account Manager can do — orphans.** Remove an account and
its radios remain in `<Entries>`, bound to an account that no longer exists.
The per-row menu structurally cannot reach them, because the radio may not
appear in the list at all once its account is gone.

Which raises a question the design had not asked: **what should happen to bound
radios when an account is deleted?** Options are forget them, orphan them and
show them as unreachable, or offer to rebind at delete time. Leaning
offer-to-rebind-at-delete-time — that is the one moment the operator has full
context about why they are removing the account. Not yet ratified.

## What survives of "default account"

Default dissolves entirely for **listing** — which radios you see — which is the
confusing part today. It survives, renamed to **preferred account for new
connections**, for three narrower jobs:

- which account a brand-new radio binds to on first connect,
- which account an auto-connect record stamps itself with,
- where a fresh sign-in lands.

A real simplification, not a deletion. Saying "you don't need a default at all"
overshoots; saying "the default no longer decides what you can see" is exact.

## Constraints that are not negotiable

**Lazy, not eager.** Refresh a stale account list on *activation of that row*,
never at picker open. Eager refresh across N accounts means N SmartLink sessions
every launch. The roster's instant paint is why the picker is usable the moment
it opens; do not trade that for freshness nobody asked for.

**Never authenticate as someone else without saying so first.** Any flow that
can end in a sign-in form must be something the operator chose out loud. A
cross-account row announces "Connecting as dbreda@mail.com" **before** starting
the session, not after it succeeds. Standing lesson from the 2026-08-06
token-lineage arc (`memory/project_smartlink_token_lineage.md`): the native
sign-in form makes cross-account refresh survivable in a way the WebView2 form
never was, but survivable is not the same as unannounced.

**Name the account on every connect, not just cross-account ones.** Symmetry
means the operator learns the pattern instead of having to notice an exception
at the moment it matters. This is a TX-safety requirement, not a politeness one:
Don's 6300 is his production station, and a unified list puts it one arrow key
from Noel's own rig.

**Staleness is spoken, not silent.** `AccountListFetchedUtc` and `DescribeAge()`
already exist and already produce ages like "2 days ago". They are gated behind
the same matching-account check that causes the bug; fixing the gate hands us
the staleness metadata for free.

## Loaded-state announcements

Local and remote are not the same kind of event and should not claim to be.

- **Remote is discrete.** A SmartLink session opens, the server sends its radio
  list once per TLS session, done. "Remote connections loaded" describes a real
  completion.
- **Local is continuous.** VITA discovery keeps arriving the whole time the
  picker is open; a radio powered on thirty seconds from now simply appears.
  "Local connections loaded" is true for about five seconds and then quietly
  becomes a lie.

Wording:

- Terse "Local loaded" / Chatty "Local connection list loaded, still listening"
- Terse "Remote loaded" / Chatty "Remote connection list loaded"

Both at `VerbosityLevel.Terse`. Critical means "spoken even with speech off",
and a picker that speaks twice at startup for someone who deliberately turned
speech off is too much. Noel's non-clobberable requirement is about **ordering**
— it must not be stepped on by `AnnounceListDelta` firing a few lines later —
and is handled by sequencing, not by escalating the level.

**Implementation trap:** the announcement must **not** live inside
`MorphRemoteToRefresh()`. That method opens with `if (_remoteListLive) return;`,
so it fires once per session — the announcement would work the first time and
then silently stop forever. It belongs in the success branch at line 1215,
outside the guard.

**Also add a query, not just an announcement.** An announcement is a one-shot;
if the screen reader is mid-sentence or the operator alt-tabbed, it is gone and
they are back to inferring state from list contents. A key that reports "Local
loaded, remote not loaded" on demand answers the question at the moment the
operator has it. The announcement then becomes a convenience rather than
something that must be caught.

## Staging

**Stage 1 — the bug fix (implementable now).**

- `KnownRadioRoster.Load()`: scan **all** account lists for attribution; keep
  `InAccountCache` true only for the matching account. No schema change.
- `WhereText`: new branch for a row attributed to an account that is not the
  current one — "offline, registered to dbreda@mail.com, last seen 2 days ago".
- `HandleOfflineConnectAttempt`: a row owned by another account does not fire a
  doomed SmartLink pass; it names the owner and offers the switch.
- The loaded-state announcements plus the query key.

**Stage 2 — unified activation.**

- Write `LastSeenViaAccount` on sighting; add `PreferredAccount` to
  `RadioConfig`.
- Row activation resolves its account and connects, announcing the account
  first. Lazy refresh on activation.
- Context menu (keyboard-first) to set the preferred account per radio.
- Radio-associations view in the SmartLink Account Manager: same field, second
  door, view-and-rebind only — no connect from that surface.
- Decide the account-deletion rule for bound radios (leaning
  offer-to-rebind-at-delete-time).

**Stage 3 — cleanup.**

- Rename default to preferred-account-for-new-connections; narrow it to the
  three jobs above.
- Remote button becomes Refresh.

## Relationship to Connect

Not throwaway pre-Connect work. Connect needs "radios I have access to, across
grants", which is structurally identical to "radios I have access to, across
accounts". The account-aware row built here is the same abstraction arriving
early, and Connect inherits it rather than replacing it.

## Open

- ~~Does the radio list already have a context menu to extend, or does one need
  building?~~ **Closed 2026-08-10: it exists.** Declared at
  `RigSelectorDialog.xaml:47-57`, opened via `RadiosBox_ContextMenuOpening`
  (`RigSelectorDialog.xaml.cs:1093`), already carrying Connect, favorite toggle,
  and Auto-Connect Settings. Stage 2 adds an item to an existing menu.
- Keyboard audit applies at Stage 2 (new bindings: context menu, loaded-state
  query). `docs/help/md/keyboard-reference.md` plus Command Finder keywords.
- Whether a cross-account connect deserves a confirmation step on top of the
  spoken account name, given Don's radio is production. Leaning no — the spoken
  name plus a deliberate Enter is already two signals — but revisit if a
  near-miss ever happens.
