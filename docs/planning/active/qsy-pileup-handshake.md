# QSY Pileup Handshake — the roster and connect batch

**Status:** planned, not started. Written 2026-08-14 at Noel's instruction:
*"We need to plan all the fixes in batches / a batch rather than fixing stuff
from time to time."*

**Why a batch and not five fixes.** Five reported symptoms, and they are almost
certainly **three roots**. Patching them individually is how the sixth symptom
gets created — every one of them lives in the same 2,668 lines of roster,
discovery-cache and selector code, and two of them are already suspected to
share a cause.

---

## The symptoms, as reported

1. **Don's radio tries to connect locally every time when it should go straight
   to SmartLink.** Noel, 2026-08-14. This is the headline.
2. **A known-local radio that is unreachable never falls back to SmartLink.**
   Trace-proven on the laptop, 12:07 session: discovery drained **zero** packets,
   both connect attempts took the LOCAL branch, hung 20-30 seconds, then "rig's
   open failed" — and the trace contains **zero SmartLink activity at all**. No
   account load, no session, no radio list. Meanwhile the same radio showed
   `status=Available` on SmartLink from another machine.
3. **Connecting through the roster takes two Enters** — the first appears to run
   a refresh, the second actually connects.
4. **A radio name set in per-radio settings is invisible whenever the radio is
   discovered** (task #75). `RigSelectorDialog.PaintRoster` skips the roster
   entirely for any radio already in the discovered list, so the operator's
   chosen name only appears while the radio is offline.
5. **Presence check fails after the client remove/re-add dance**, and the
   authority gate then denies a local operator. Trace-proven on the 8600 over
   LAN: the gui client is added with a full clientId, `isLocalPtt=True`,
   `myClient=True`, then removed at ~1.4 s during station-name binding and
   re-added with an **empty clientId**, `myClient=False`, `isLocalPtt=False` —
   same ClientHandle (1384667612). `IsCurrentClientLocalPtt` reads the impostor
   record and `RequirePortSettingsAuthority` denies.
6. **Account switching friction.** The ms-02 stays signed into SmartLink as Don;
   switching to Noel's own account to reach the 8600 is clumsy under the new
   roster code.

---

## The connect flow Noel wants (2026-08-15) — this reframes the whole batch

Captured from Noel directly. **Read this before Root A below**, which was written
from the discovery side and is correct but enters the problem from the wrong end.

### First, a correction to this document's framing

This file previously said the roster is "what stands between Don and using his
radio." **That is wrong and Noel corrected it.** Don can connect. The defect is
that his radio *tries local first when he has selected remote*, which costs him
a failed attempt and a wait every single time. It is an efficiency and
correctness problem, not a blocker. Noel hit the mirror image himself: he had to
press **Remote** explicitly to get his normally-local radio to connect at all.

### The vision

> *"Right now there's a Remote button. I'm thinking that needs to go away, with a
> connect locally or a connect remotely button. Another option would be to have a
> Connect button which would connect based on preference (locally if selected,
> remotely if selected). Enter clicks Connect and connects based on selection.
> Right click would allow you to connect locally or remotely, an option for both,
> and something that allows you to select connect locally as default or connect
> remotely by default."*

Concretely:

- **The separate Remote button goes away.** Today `RigSelectorDialog.xaml` has
  both `ConnectButton` (line 69) and `RemoteButton` (line 91). Two buttons for
  one act, where the second exists only because the first cannot be trusted to
  pick the right path.
- **One Connect button that honours the radio's stored preference.** Enter
  activates it. The preference is per-radio and persistent.
- **The context menu carries the explicit verbs** — connect locally, connect
  remotely, and the ability to set which is the default for this radio.
  **This surface already exists**: `RigSelectorDialog.xaml:47-63` has a real
  ContextMenu on the radio list with Connect / Add to Favorites / Auto-Connect
  Settings / Preferred Account and a `ContextMenuOpening` handler. Shift+F10
  already reaches it. The connect verbs extend a live menu rather than
  introducing a new one.

### Why the current code cannot do this — the preference is erased three times

Sharper than Root A, and verified in code 2026-08-15:

1. **`IsRemote` never consults the preference off the dual-homed path.**
   `RigSelectorDialog.xaml.cs:100` derives
   `DualHomed ? PreferRemotePath : WanAvailable ? true : LanAvailable ? false : LastSeenRemote`.
   A radio seen on the LAN hits `LanAvailable ? false` and goes local
   unconditionally. The operator's stated choice is not an input to the
   expression.
2. **Lines 705 and 780 overwrite the stored choice**:
   `if (!row.DualHomed) row.PreferRemotePath = false;`
3. **Line 1031 ANDs it away again at the point of use**:
   `SelectedPreferRemotePath = radio.DualHomed && radio.PreferRemotePath;`

And because `WanAvailable` is never learned for a radio discovered locally,
`DualHomed` is false — so all three suppressions fire for exactly the radios
where the preference matters most. **This is a settings-are-intents violation of
the purest kind** (`memory/project_settings_are_intents_not_commands.md`): the
app does not merely ignore the operator's choice, it destroys it.

### The consequence that unifies this batch

**"Honour the stated preference" and "learn WAN availability" are the same fix.**
If a preference of *remote* is authoritative even when the app believes the radio
is LAN-only, then the app must open a SmartLink session in order to satisfy it —
which is precisely the behaviour absent from the trace that drained zero
SmartLink activity. Entering from the operator's side is cleaner than entering
from discovery's side, and it makes the fallback logic a consequence rather than
a special case.

### Settled 2026-08-15 — the Remote button goes, force-remote moves to the menu

Noel: *"Take the Remote button away in favour of just a Connect button. I don't
see the need for Remote — it saved my bacon yesterday when I wanted to force
remote, so we need that, but that could be in the context menu."*

**Decided, not an open option.** One Connect button; the explicit verbs live in
the context menu.

**Force-remote is not a convenience, it is test equipment.** Noel: *"This will
let us be able to test hole punch etc."* It has two load-bearing jobs — the
rescue path that got him connected on 2026-08-14, and the instrument for
re-running the hole-punch test on the restored single-NAT topology (runbook item
13, `memory/project_hole_punch_wiring_gap.md`). Both mean it must be reliable,
explicitly named, and must **not** silently fall back to local — a fallback
would quietly invalidate a punch test by producing a successful connect over the
wrong path. Force-remote means *this path only*.

**Removing the button removes Alt+R.** `RemoteButton` is `Content="_Remote"`, so
the accelerator disappears with it. That makes this a key-binding change and
Track A owes the full keyboard audit from CLAUDE.md: update
`docs/help/md/keyboard-reference.md`, update Command Finder keywords, and give
the changelog a line with heads-up language, since a removal is the case where
someone somewhere has it in their fingers. **Press the key on a real build.**

### The preference must not be a boolean — JJ Flexible Connect is a third path

Noel, same conversation: *"Of course Connect will throw everything into
craziness, but we should be able to just add something to prefer Connect over
SmartLink etc., so that shouldn't be too disruptive."*

He is right that it need not be disruptive — **but only if the type changes
now.** `PreferRemotePath` is a `bool` (`RigSelectorDialog.xaml.cs:48`). A boolean
can express local-or-SmartLink and nothing else. The moment Connect
(`memory/project_jjflexible_connect.md`) is a real path, a bool cannot say
"prefer Connect, fall back to SmartLink, then local."

This batch already rewrites every site that reads or clobbers that field. **The
type change is nearly free right now and expensive later**, which is exactly the
case for doing it in this pass rather than deferring.

**Recommended model: an ordered chain of paths to try**, not an enum. One shape
then expresses everything this batch needs:

- Don's radio: `[SmartLink, Local]`
- The bench 8600: `[Local, SmartLink]`
- A future Connect radio: `[Connect, SmartLink, Local]`
- Force-remote from the menu: `[SmartLink]` — one entry, no fallback, which is
  what makes it a valid test instrument.

The automatic-fallback requirement then stops being special-case logic and
becomes "walk the list until one succeeds, announcing each move." An enum plus
separate fallback rules would work, but it re-creates the current situation where
the preference and the path logic are two things that can disagree.

Phase 1 owes: the persisted representation in `KnownRadioRoster.cs`, and a
migration from the existing bool that does not lose anyone's setting.

### The double Enter is authenticate-then-connect, not refresh-then-connect

Noel, 2026-08-15, from connecting to Don's radio on 2026-08-14: *"I had to
basically hit Enter twice — one time it'd authenticate with SmartLink, and the
second time actually connected."*

**This supersedes the symptom-3 reading elsewhere in this file**, which records
the double Enter as a refresh followed by a connect. Direct observation beats
inference. The defect is that the first Enter starts the SmartLink auth handshake
and **returns without proceeding**, instead of awaiting it and continuing into
the connect it was asked for.

Different fix, too: await the auth and carry on, rather than folding a refresh
into the first press.

**Phase 1 must establish whether this is one defect or two.** A local radio
plausibly double-Enters on the refresh path while a remote one double-Enters on
the auth path. If both exist, fixing one and declaring the symptom closed is how
the other survives to be rediscovered.

#### The auth ladder — Noel's design, 2026-08-15

> *"Make sure auth is needed, in case a quick refresh of the token would do it,
> then try to connect; and if that doesn't work, re-auth and connect."*

**Right shape, and the machinery already exists** — verified in code 2026-08-15,
so this is a wiring job rather than a build:

- `SmartLinkAccountManager.RefreshTokenAsync` (`SmartLinkAccountManager.cs:350`)
  yields a **fresh id_token**; the trace line at `:412` is the proof, and that
  capability was the 2026-08-06 fix for Don's lockout
  (`memory/project_smartlink_token_lineage.md`). Measured then at ~250 ms.
- `SmartLinkLoginForm` (`SmartLinkLoginForm.cs:21`) is the **native** sign-in
  form; `FlexBase.cs:4474` already uses it.

**The 60-second expiry is why the first rung nearly always fires.** The
frtest.auth0.com tenant issues id_tokens that expire sixty seconds after issue,
so at any realistic moment the stored token is stale. "Is auth needed?" is almost
always yes. The useful consequence: at ~250 ms a refresh is cheap enough to
simply *do*, rather than building elaborate logic to decide whether to.

**Three JIT-refresh call sites already exist** — `FlexBase.cs:1509`, `:4711`,
`:4749`, with `GetJwtFromSavedAccount` tracing the outcome and timing at `:4757`.
Three places doing one job is three chances to disagree. **The double Enter is
plausibly one of them refreshing successfully and then not continuing into the
connect**; Phase 1 should look there before looking anywhere else.

**The ladder, with the failure condition made explicit:**

1. No stored token at all → native sign-in.
2. Token present → refresh it. Silent, no UI, ~250 ms.
3. Attempt the connect.
4. Connect fails **with an auth-shaped error** (401/403/token rejected) → one
   refresh-and-retry.
5. Still failing on auth → **walk to the next path in the chain before prompting
   for anything.** Only when the chain is exhausted does the native sign-in
   appear, followed by the connect.
6. Connect fails with anything else → **report the actual error and stop.**

**Step 5 is Noel's refinement, 2026-08-15:** *"A refresh may not always work, but
it's not always necessary to enter the SmartLink password."* Correct, and the
chain model gives it for free — "refresh failed" and "type your password" are not
adjacent rungs, because another path may sit between them. Connecting locally is
strictly better than making the operator type anything.

The same rule produces three right answers:

- The bench 8600 on `[Local, SmartLink]` never prompts, because it never needed
  SmartLink in the first place.
- Don's remote-only radio does prompt, because there is nowhere else to go. That
  is correct, not a failure.
- Force-remote is a one-entry chain, so it escalates immediately — which is what
  a deliberate override should do.

**Step 6 is the guardrail and it is the whole point.** "If that doesn't work,
re-auth" must mean *if it fails because of auth*, never *if it fails*. A radio
that is switched off, a router dropping inbound UDP, a busy radio — none of those
are auth problems, and re-authing on them puts a sign-in form in front of an
operator whose actual problem is elsewhere. That is both confusing and, for a
blind operator, a disruptive surprise. It would also mask the real error, which
is the diagnosis we actually needed on 2026-08-14.

**Re-auth means the native form, never the browser.** Load-bearing accessibility
constraint with a shipped precedent: *"any auth design that ends in 'then the
browser form opens' is a dead end for this user base"*
(`memory/project_smartlink_token_lineage.md`). MFA is the sanctioned exception
that falls back to the browser, and it already does.

**All of this is one Enter, and it must speak.** The ladder can take seconds, and
a keystroke that produces silence while work happens violates
`memory/project_no_silent_keystrokes_rule.md`. Announce the stage — signing in,
connecting — not just the outcome.

### Per-radio connection history — one substrate, two offers

Noel's idea, 2026-08-15, and it generalises further than he pitched it:

> *"Keep track in JSON based on the radio. If connection's taken longer than
> normal, or the system determines that you've connected remotely three times,
> the user could be asked if they want to change to local or whatever. And once
> Connect is a thing, if SmartLink is taking too long, or longer than normal,
> then the operator could be invited to set Connect up, since that connection
> process will be lightning fast by design."*

**Both ideas are one mechanism.** They need exactly one thing the app does not
keep today: per-radio connection history — which path was attempted, what
happened, and how long it took. "You have connected remotely three times" and
"SmartLink is slow for you, Connect would be faster" are two *policies* reading
one substrate. Build the measurement once.

It pays off in a third place nobody asked for: on 2026-08-14, answering "how long
is Don's connect actually taking" required reading trace files. With this it is a
stored fact, which makes it a support tool as much as a UX one.

**What to record**, per radio, keyed by serial
(`memory/project_per_radio_config_serial_keyed.md` is the precedent):
attempted path, outcome, duration, timestamp. A short ring — the last ten
attempts, not unbounded history. **Local JSON only, never phoned home**
(`memory/project_no_silent_phone_home.md`); this is timing telemetry about the
operator's own network and it stays on their machine.

**Honest baselines.** "Longer than normal" needs a normal. The per-radio,
per-path median is the honest one, and it means **no offer until there is enough
history to have a median** — a first connect has no baseline and must not
generate advice. Likewise the "three times" trigger should count *the chain not
matching reality* three times, not three remote connects in general; a radio
correctly preferring remote is not evidence of anything.

**Always an offer, never an automatic change.** Settings are intents
(`memory/project_settings_are_intents_not_commands.md`) — the app noticing a
pattern does not license it to rewrite a choice the operator made. And offered
never nagged: one clear offer, dismissible for good, per the shared Elmer rules
in `docs/planning/vision/elmers.md`.

**Not during the connect.** The moment an operator is waiting for their radio is
the worst possible time for a dialog. The offer belongs after the connect
completes, or in the selector next time — an open design question, not a
decision.

**The Connect invitation needs one extra rule, because it is the one suggestion
that benefits us.** Offer it only from measured evidence that this operator's
SmartLink path is genuinely slow — never as a blanket promotion, never on a
first connect, once and dismissible forever. See
`memory/project_jjflexible_connect.md` and
`memory/project_friction_tax_principle.md`. Do not build the offer before Connect
exists; **do** build the measurement now, since it is the same measurement the
local/remote policy needs.

### Help text owes an explanation of the delay

Noel: *"We can put something in help to tell users that if there is an unwanted
delay, they need to designate what is preferred."* Docs ship with features
(`memory/feedback_docs_ship_with_features.md`). The keyboard-reference update
this track already owes for the Alt+R removal is the same pass.

### Scope call for Track A

**In scope:** record the history. The connect path is already open in this track,
already knows which path it took and whether it worked, and timing it is cheap.

**Out of scope for this batch:** the offer UX, both policies, and anything
Connect-shaped. Those have real open questions above and none of them block the
roster fixes.

### Decisions still open for Phase 1

- **What happens to `PathCombo`** (the "Remote via SmartLink" combo, referenced
  at line 1199). Under this model it either becomes the preference editor or
  becomes redundant. It must not survive as a third place the preference lives —
  three places is how the current bug happened.
- **What Connect announces** when preference and reality disagree. The
  no-silent-path-substitution rule below still governs: if the chain falls from
  SmartLink to local, or the reverse, it says so out loud.
- **Default chain for a radio with no preference recorded yet.** Local-first is
  the existing behaviour and the safe one; make it an explicit default rather
  than emergent from the derivation.

---

## Root D — cached availability never expires, so a radio that moves stays moved

**Noel, 2026-08-15, and this is the fact that explains Don completely:** *"Don's
radio, before it moved to Tony's, was always local — that's why he gets stuck
connecting locally."*

His radio earned `LanAvailable = true` across its entire life on his own LAN.
Then it physically moved to Tony's. **The cached fact did not move with it.**

Feed that into the derivation at `RigSelectorDialog.xaml.cs:100` —
`DualHomed ? PreferRemotePath : WanAvailable ? true : LanAvailable ? false : LastSeenRemote`
— and a stale `LanAvailable` short-circuits to **local, permanently**. The
preference that would override it is then erased by the three sites documented
above. Two independent defects compounding into one very stuck radio, which is
why it survived casual inspection.

**The category error:** conflating *"is on my LAN right now"* — a live discovery
fact with a shelf life of seconds — with *"was seen on a LAN once"*, which is a
historical hint and legitimately persisted. `LastSeenRemote` is honest about
being history; its name says so. `LanAvailable` is not, and is treated as
current when it is nothing of the kind.

**This is not Don-specific.** Any radio that changes networks inherits it: a rig
taken to Field Day, a club radio that rotates between shacks, a house move, a
subnet renumber. Don simply got there first by moving his radio to Tony's.

**The fix**, and Phase 1 should confirm the storage before choosing between
these:

- Make `LanAvailable` live-only — set by the current discovery sweep, never
  persisted, defaulting to false when a sweep has run and not seen the radio.
  Cleanest, because it makes the type honest.
- Or, if it must persist, **age it out** and invalidate it the moment a discovery
  sweep completes without the radio in it. A cached "yes" that survives a
  contradicting observation is not a cache, it is a lie.

**The reassuring cross-check:** if `LanAvailable` becomes live-only, Don's radio
falls through to `LastSeenRemote` and behaves correctly **even with no preference
set**. So the preference fix and the staleness fix each independently unstick
him. Two different corrections converging on the same right answer is good
evidence both are right.

**This also feeds the connection-history work below.** Ten consecutive failed
local attempts is exactly the signal that a cached LAN fact has gone stale — the
history substrate makes the invalidation evidence-based rather than purely
timer-based.

**Regression test this deliberately.** Take a radio that the roster knows as
local, make it unreachable on the LAN, and confirm the roster stops claiming it
is local. That test does not exist today and would have caught this.

## The three suspected roots

**Root A — a radio is classified before its WAN availability is ever learned.**
`RadioListItem.IsRemote` is derived as `DualHomed ? PreferRemotePath : …`, and
the whole path-selection apparatus (`PreferRemotePath`, the "Remote via
SmartLink" combo, the dual-homed display strings) **only engages for rows that
are already known to be dual-homed**. But `WanAvailable` is never learned for a
radio the roster has decided is local, because nothing opens the per-radio
SmartLink session before classifying it. A local-only classification is therefore
self-fulfilling: it cannot be revised by evidence it never goes looking for.

Explains symptoms 1 and 2 completely, and plausibly 6.

**Root B — the roster and the discovered list are merged by exclusion, not by
union.** `PaintRoster` skips any roster entry whose serial is already in
`_radiosList`. So roster-held facts — the operator's chosen name, last-seen
data, preferred account, favourite status — are available only for radios that
are NOT currently present. Every roster fact silently loses to a discovery fact,
including facts discovery does not carry at all.

Explains symptom 4, and is a strong candidate for part of 3 and 6.

**Root C — "my client" is resolved by clientId, which the radio omits on
re-add.** ClientHandle survives the remove/re-add; clientId does not. Any
identity check keyed on clientId reads the impostor record.

Explains symptom 5. Independent of A and B, but in the same connect path and
worth fixing in the same pass.

## Sequence — investigation first, deliberately

**Do not start with fixes.** Five symptoms over three suspected roots is exactly
the case where a targeted patch makes the next bug. The first pass produces a
map, not a diff.

**Phase 1 — read the state machine and write it down.**
Cover `RigSelectorDialog.xaml.cs` (2,035 lines), `KnownRadioRoster.cs` (349),
`RadioConnectionCache.cs` (284). Produce a plain-prose description of: how a
radio gets into the list, what facts each source contributes, who wins when they
disagree, when a SmartLink session is opened and by whom, and exactly what
decides which branch a connect takes. **The deliverable is that description**, and
it goes in this file. If the three roots above turn out to be wrong, better to
learn it here than after three fixes.

> **Phase 1 is DONE — the map is the "Phase 1 deliverable" section at the
> bottom of this file (Track A, 2026-08-16).** It confirms the shape of every
> root and corrects the mechanism of three of them. Read it before Phase 3 code
> review.

**Phase 2 — confirm each root against a trace**, not against reasoning. Traces
already exist for symptoms 2 and 5; get one for 4 by naming a radio and watching
the merge.

**Phase 3 — fix, as one batch, one merge.** Likely shape:
- Learn WAN availability before classifying, or make classification revisable
  when a local attempt fails. **Noel's stated design intent: local first, then
  automatically try SmartLink.** A failed local connect should fall through, not
  stop.
- Merge roster and discovery by union with an explicit precedence rule, and
  write that rule down where the next reader will find it. The operator's chosen
  name should win over the hardware's — they typed it deliberately and recently.
- Resolve "my client" by ClientHandle.
- Fold the refresh into the first Enter.

**Phase 4 — verify at a radio.** LAN to the 8600, SmartLink to the 8600, and —
if his radio is up — Don's 6300, which is the only one that reproduces symptom 1
naturally.

## What must not regress

- **Local-first stays local-first.** The fix is a fallback, not a preference
  flip. A radio on the LAN must not start travelling through SmartLink because
  the code got more willing to try it.
- **The dual-homed row and its explicit "Remote via SmartLink" choice must
  survive.** That machinery is correct; it is just unreachable for rows that
  never learn they are dual-homed.
- **No silent path substitution.** If a connect falls back from local to
  SmartLink, say so. An operator who thinks they are on the LAN and is actually
  on SmartLink has been misled about latency, bandwidth and who else can see the
  radio.
- **Do not slow the common case.** A radio that is present on the LAN should
  connect as fast as it does today; the SmartLink probe must not become a
  precondition for the happy path.

## Related, deliberately NOT in this batch

- **Mono capture support** (engine opens two channels, cannot upmix from one).
  Small and well understood — `deviceParams` opens at native count, the input
  callback duplicates, and `endPtr` walks `BufferSize / 2` for mono. `MicProbe`
  already does exactly this duplication. Noel, 2026-08-14: *"We can do it but
  there's lots more that we need to fix first."*
- **#74 REM ON**, **#76 GPS lock and PPB**, **#73 DSP explanations** — pre-lock,
  but separate subsystems.
- **The transverter bench (#27)** — still the blocker for its own arc, and needs
  radio time rather than code.
- **Updater manifest 404** — infrastructure, not roster.

## Context that frames the whole batch

The 2026-08-14 A/B verdict: laptop → exit node → SmartLink → forwarded ports →
the 8600 keyed with PC audio and produced the monitor echo. **Client, SmartLink
and the Opus upstream transport are proven good end to end over WAN**, and
SmartSDR fails on Don's radio too. So the TX-audio fault is at Don's end and is a
support matter.

**Corrected 2026-08-15 by Noel.** This section previously concluded that the
roster is "the thing standing between Don and using his radio at all." It is not
— **Don can connect.** What the roster costs him is a failed local attempt and a
wait on every connect, because it ignores the remote preference he selected.
Worth fixing properly, and it is the batch that most improves his daily
experience — but it is an efficiency and correctness defect, not a blocker, and
this batch should not crowd out other work on the strength of a blocker that
does not exist. See the connect-flow section at the top of this file.

---

# Phase 1 deliverable — the roster and connect state machine, as read 2026-08-16

Track A. Every claim below was read from code on branch `bsr/track-a`, with
file and line cited so Phase 3's diffs can be checked against it. Where this
map contradicts a root as written above, the contradiction is called out
explicitly — three of the four roots are right about the symptom and wrong (or
half-wrong) about the mechanism, which is exactly what Phase 1 existed to catch.

## 1. How a radio gets into the list

A row in the selector is one radio (`RadioListItem`, one per serial). Four
routes put one there, and they run in a fixed order:

**Route 1 — the roster paint.** The dialog constructor calls
`PaintRoster(CurrentAccountEmail())` BEFORE discovery starts
(`RigSelectorDialog.xaml.cs:396`). `KnownRadioRoster.Load` assembles entries
from two stores, merged by serial:

- The serial-keyed per-radio profile store, `radios\{serial}\config.xml`
  (`RadioConfig`). Authoritative for: nickname, model, favorite flag,
  last-seen stamp (`LastSeenUtc`), `LastSeenRemote`, `LastSeenViaAccount`,
  `PreferredAccount`.
- `radioConnectionCacheV1.xml` (`RadioConnectionCache`), two elements of it:
  the per-radio `Entries` (fills in model/nickname the profile lacks, and a
  newer last-seen stamp when connects outran sightings), and the per-account
  `AccountLists` (marks rows `InAccountCache` for the account in play, and
  attributes radios to whichever account's list last mentioned them).

Roster rows are born offline: `LanAvailable = false`, `WanAvailable = false`.
The paint happens synchronously in the constructor, so on a first open every
known radio is in the list, under its roster name, before any packet arrives.

**Route 2 — local VITA discovery.** `StartLocalDiscovery` →
`FlexBase.LocalRadios()` → `apiInit(force: true)` (`FlexBase.cs:372`). FlexLib
raises `API.RadioAdded` per announcement; `radioAddedHandler` adds to
`myRadioList` and raises `RadioFound` with a `RigData` built by
`BuildRigData` (`FlexBase.cs:260`): `LanAvailable = !r.IsWan`,
`WanAvailable = r.IsWan || WanKnows(serial)`. A LAN radio re-announces about
once a second, so this route fires continuously while the dialog is open.

**Route 3 — the SmartLink list.** One list per TLS session, ever. Arrives in
`wanRadioListReceivedHandler` (`FlexBase.cs:3870`), which: sweeps ghosts
(WAN radios absent from the fresh list get `RadioRemoved`), banks every WAN
`Radio` object in the static `_wanRadiosBySerial` dictionary (the only place a
dual-homed radio's WAN identity survives), writes the account's list to the
connection cache for next launch's fast paint, and then raises `RadioFound`
per radio — merging into the existing LAN object when one exists.

**Route 4 — the dialog-side merge.** `OnRadioFound`
(`RigSelectorDialog.xaml.cs:664`) updates an existing row **in place** (never
remove-and-append — that is the 2026-08-05 reorder lesson) or appends a new
row for a never-seen serial.

## 2. What facts each source contributes

- **The per-radio profile** contributes the operator-owned display metadata:
  name, favorite, preferred account, last-seen story. It contributes NOTHING
  to path selection except `LastSeenRemote`.
- **Local discovery** contributes liveness (`LanAvailable`), the radio's own
  broadcast nickname and model, and the `RigData` handle a local connect uses.
- **The SmartLink list** contributes `WanAvailable`, the WAN `Radio` object
  (banked by serial), public ports / hole-punch flags on that object, and the
  account attribution recorded to the cache.
- **The account cache** contributes a fast paint only: serial, nickname,
  model, `InAccountCache`, fetch age. By deliberate rule nothing connects
  from it (`RadioConnectionCache.cs:63-68`).
- **The auto-connect config** (`{op}_autoConnectV2.xml`) contributes a serial,
  a display name, `LowBandwidth` — and a **persisted `IsRemote` bool frozen at
  the moment auto-connect was configured** (`AutoConnectConfig.cs:33`). Keep
  that one in mind; it reappears under Root D below.

## 3. Who wins when they disagree

- **Discovery beats the roster while the radio is live, field by field.** The
  in-place update at `RigSelectorDialog.xaml.cs:693-705` overwrites `Name`,
  `ModelName`, `RigData`, both availability flags. It deliberately preserves
  what discovery does not carry: `IsFavorite`, `PreferredAccount`,
  `LastSeenText` — so the often-cited "roster loses wholesale" is not quite
  true. What it does lose is the NAME.
- **The name has no choice/observation split at all.** `RadioConfig.Nickname`
  is one field serving two masters: the Settings Radio Profile tab writes the
  operator's chosen name into it (`SettingsDialog.RadioProfile.cs:295`), and
  `KnownRadioRoster.RecordSighting` overwrites it with the radio's broadcast
  nickname at the first sighting of every selector session
  (`KnownRadioRoster.cs:252`). Compare `PreferredAccount` vs
  `LastSeenViaAccount`, where the code documents exactly why choice and
  observation must be separate fields — the name needs the same treatment and
  never got it.
- **On the repaint path** (account switch → `SwitchToAccount` →
  `ClearOfflineRows` + `PaintRoster`), the exclusion at
  `RigSelectorDialog.xaml.cs:556-558` skips any serial already live, so a live
  row never receives roster facts it missed. This is Root B's line, and it is
  real — but see the correction below for how much of symptom 4 it actually
  explains.
- **Within the roster load**, profile beats cache for every field the profile
  has; the cache only fills blanks and can only win the last-seen stamp by
  being newer (`KnownRadioRoster.cs:153-162`).

## 4. When a SmartLink session is opened, and by whom

Exactly five triggers, all explicit:

1. The **Remote button** (`StartRemoteFlow` → `RemoteRadios()` →
   `setupRemote()`).
2. **Remote-first startup** — the per-account `AutoStartRemote` flag, fired
   from the dialog's `Loaded` event.
3. **Enter on an offline row whose story is remote-ish**
   (`HandleOfflineConnectAttempt`, `RigSelectorDialog.xaml.cs:1074-1088`):
   `LastSeenRemote || FromAccountCache` starts a remote pass. Note what it
   does NOT do — see the double-Enter finding.
4. **An account switch** (`SwitchToAccount` forces a session cycle so the new
   account gets a fresh list).
5. **Auto-connect with `config.IsRemote == true`** (`TryAutoConnectRemote`).

The confirmed absence at the center of this batch: **nothing in the local
path ever opens a SmartLink session.** `WanKnows` is empty until a remote pass
runs, so a locally-discovered radio has `WanAvailable == false` not as an
observation but as an unasked question. Symptom 2's trace ("zero SmartLink
activity at all") is this absence, verbatim.

## 5. Exactly what decides which branch a connect takes

The **manual** path: `DoConnect` (`RigSelectorDialog.xaml.cs:1001`) refuses
offline rows, then copies `radio.IsRemote` — the derivation
`DualHomed ? PreferRemotePath : WanAvailable ? true : LanAvailable ? false :
LastSeenRemote` (`:100`) — into `SelectedIsRemote`, and
`radio.DualHomed && radio.PreferRemotePath` into `SelectedPreferRemotePath`
(`:1031`). The dialog closes. `globals.vb:2726-2732` then branches: remote →
`RigControl.ReconnectRemote(serial, lowBW, forceWanPath:=preferWan)`; local →
`RigControl.Connect(serial, lowBW)`. `findRadioForConnect`
(`FlexBase.cs:353`) resolves the object: `preferWan` true consults ONLY the
WAN dictionary and deliberately returns null rather than the LAN object —
force-remote's no-fallback rule already exists at this layer.

The **auto-connect** path: `TryAutoConnect` (`FlexBase.cs:1216`) branches on
the persisted `config.IsRemote` — remote authenticates then waits for the
radio in the account list; local runs `LocalRadios()` and polls
`findRadioInAPI` for up to 10 s. There is no crossover in either direction: a
radio that moved networks fails here every startup until the user reconfigures.

The **retry** path after a failed `Start()` (`globals.vb:2862-2943`) branches
on `CurrentRig.Remote` — the `RigData.Remote` flag stamped at discovery time
(`BuildRigData`, LAN wins for a dual-homed radio). Local failures have no
retry at all ("no retry path available").

So there are **three independent branch deciders** — row derivation,
persisted auto-connect bool, discovery-stamped `RigData.Remote` — and no
shared chain. That is the structural fact the Phase 3 chain type exists to fix.

## 6. The roots, verified and corrected

**Root A — confirmed in shape, corrected in framing.** The four sites are
real: the derivation (`:100`), the two overwrites (`:705` in `OnRadioFound`,
`:780` in `OnRadioRemoved`), the AND at point of use (`:1031`). But
`PreferRemotePath` is documented and implemented as **"per connect, never
persisted"** (`RadioListItem`, `:43-48`) — a session-scoped, dual-homed-only
choice set through `PathCombo`. The three "erasures" are faithful to that
field's contract. The sharper truth: **there is no persisted per-radio path
preference anywhere in the codebase to erase.** Don never had a stored
"prefer remote" to lose; he has no way to state one. The Phase 3 instruction
"migrate the existing bool without losing anyone's setting" is therefore
trivially satisfiable — the only persisted path-shaped facts are
`AutoConnectConfig.IsRemote` (one radio per operator) and the observational
`LastSeenRemote`. The chain is a new fact, not a migration of an old one.
One naming landmine for Phase 3: `RadioConfig.ConnectionPreference` already
exists and means something else entirely (SmartLink transport strategy —
Auto / ForwardOnly / HolePunch). The chain must not reuse that name.

**Root B — the line exists; the mechanism of symptom 4 is elsewhere.** The
exclusion (`:556-558`) only matters on a REPAINT, because on first open the
roster paints before discovery can claim anything. Symptom 4's actual
mechanism is two clobbers: `OnRadioFound` overwriting `row.Name` with the
broadcast nickname the moment the radio is discovered (`:693`), and —
worse — `RecordSighting` overwriting the profile's `Nickname` **on disk** at
first sighting (`KnownRadioRoster.cs:252`), so an offline-set name is not
merely hidden while the radio is live; it is destroyed for good the next time
the radio is seen. Fixing the exclusion alone would not fix symptom 4.

**Root C — the prescription is already the implementation; the defect is one
layer down.** Every FlexBase identity check already resolves by ClientHandle:
`myClient` compares handles (`FlexBase.cs:6239`), `TheGuiClient` uses
`FindGUIClientByClientHandle` (`:6294`), and `IsCurrentClientLocalPtt` reads
through it (`:6475`). The impostor record comes from FlexLib: the discovery
packet's client list is built with `client_id: null, is_local_ptt: false`
and `IsThisClient` never set (`Discovery.cs:679`), and
`Radio.UpdateGuiClientsList` (`Radio.cs:14604`) will **remove our own record**
when a stale packet omits us, then **add the fabricated one** when the next
packet carries our handle. The TCP correction path that would restore truth
drops any `client connected` status with an empty client_id on the floor
(`Radio.cs:14458`). FlexBase then compounds it: `guiClientAdded` gates all
self-recognition on `client.IsThisClient` (`:6170`), which the fabricated
record never has, so `_clientRemovedDuringStart` bookkeeping and
`_LocalPTT` go stale, and `IsCurrentClientLocalPtt` faithfully reads the
fabricated `IsLocalPtt == false` from the record its handle-keyed lookup
correctly found. The fix is not "resolve by ClientHandle" — it is **recognize
our own handle regardless of `IsThisClient`, and never let a record that
carries no client_id downgrade an authoritative local-PTT observation.**
FlexLib stays untouched (vendor); all of this is reachable from FlexBase's
handlers.

**Root D — right disease, wrong organ.** `RadioListItem.LanAvailable` is
live-only already: roster rows are born false, only discovery sets it, and
`OnRadioRemoved` clears it. Nothing persists it, and the connection cache's
`LanIp`/`IsRemote` fields are **write-only on this branch** (the 4.2
discovery-cascade reader does not exist here; `Lookup` has one consumer, the
display-only network identity card). The facts that actually never expire:

- **`AutoConnectConfig.IsRemote`** — frozen at configure time, drives a
  hard local-vs-remote branch every startup, and is the best code-level match
  for symptom 2's trace: two `TryAutoConnect` runs at 10 s timeout each on
  the local branch ≈ the observed 20-30 s hang and double attempt, with zero
  SmartLink activity because `config.IsRemote == false` never calls
  `setupRemote`. Phase 2 should confirm against the 12:07 trace
  (`TryAutoConnect: BEGIN ... remote=False` lines would clinch it).
- **`LastSeenRemote`** — honestly named, but it is the LAST rung of the
  `IsRemote` derivation, and for an offline row it decides the branch. A
  radio whose profile still says last-seen-local dead-ends in
  `HandleOfflineConnectAttempt`'s final else ("not on the local network right
  now", `:1101`) — the selector-side shape of "never falls back."

One successful remote sighting rewrites `LastSeenRemote` to true
(`RecordSighting` is called with `WanAvailable && !LanAvailable`), so Don's
roster row on his own machine has likely healed itself — which is evidence
his daily pain is the auto-connect record or the dead-end flow, not a stale
roster stamp. Phase 2: read his `autoConnectV2.xml`.

**The double Enter — it is two mechanisms, and neither is where the brief
pointed.** All three FlexBase JIT-refresh sites (`:1509`, `:4711`, `:4749`)
demonstrably continue into `ConnectToSmartLink` after refreshing; none
refreshes and stops. What actually eats the first Enter:

1. **By design**: Enter on an offline remote-ish row runs
   `HandleOfflineConnectAttempt` → "was last seen over SmartLink. Looking for
   it now." → `StartRemoteFlow()` → **return** (`:1076-1081`). The connect
   intention is deliberately dropped; when the pass completes, focus lands
   back on the list and the user must press Enter again. That IS
   authenticate-then-connect as two keystrokes — Noel's observation matches
   the code exactly.
2. **By focus accident**: after a remote pass, WPF focus restore can leave
   Enter targeting the Remote/Refresh button rather than the list (the
   2026-08-06 trace-164250 class, partially fixed by the list's Enter
   handler); an Enter that lands there starts ANOTHER pass instead of
   connecting.

The fix for (1) is a carried intention — remember which radio Enter was for,
and connect when the pass lands it. That also answers "one defect or two":
two, both dialog-layer, fixable in the same file.

## 7. Session-scoped state worth knowing before Phase 3 touches it

- `_remoteListLive` / `_remoteDiscoveryInFlight` gate the Remote button's
  morph into Refresh Remote List and the offline-row messaging.
- `PathCombo` writes only the session `PreferRemotePath` and only for
  dual-homed rows; it is one of exactly two writers (the other being the two
  clobber sites that clear it).
- `SessionSmartLinkEmail` (globals.vb) is the "Use Now" override — session
  only, resolved ahead of the saved default by `ResolveSmartLinkAccount`.
- The auth ladder's machinery already exists and is ordered correctly in
  `setupRemote` (`FlexBase.cs:4118`): silent JIT refresh first
  (`GetJwtFromSavedAccount`), cycle-and-silent-retry on failure, interactive
  native-first login (`PerformNewLogin` → `SmartLinkLoginForm`) strictly
  last, and non-auth failures never prompt. What is missing is only the
  chain rung: "walk to the next path before prompting" has no hook because
  there is no chain.
