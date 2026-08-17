# Track A — Roster and connect

**Worktree:** `C:\dev\jjflex-a` · **Branch:** `bsr/track-a` · **Model:** Opus

**Read first, in full:** `docs/planning/active/qsy-pileup-handshake.md`. It is
the whole specification and it has been revised repeatedly with Noel — do not
work from this summary alone.

## PHASE 1 IS NOT A CODE CHANGE. STOP AND REPORT.

**Your first deliverable is a written description of the roster state machine, in
that file. Not a diff.**

Cover `RigSelectorDialog.xaml.cs` (~2,035 lines), `KnownRadioRoster.cs`,
`RadioConnectionCache.cs`: how a radio gets into the list, what facts each source
contributes, who wins when they disagree, when a SmartLink session is opened and
by whom, and exactly what decides which branch a connect takes.

**Five symptoms over four suspected roots is precisely the case where a targeted
patch creates the sixth symptom.** If the roots below turn out to be wrong,
better to learn it in a document than after three fixes.

**Report when Phase 1 is done and WAIT.** Noel reviews the map before any roster
code changes.

## The four roots, for orientation — verify, do not assume

**Root A — the stated preference is not ignored, it is ERASED, three times.**
`IsRemote` consults `PreferRemotePath` only on the `DualHomed` branch; two sites
overwrite it to false for any radio not dual-homed; a third ANDs it with
`DualHomed` at the point of use. `WanAvailable` is never learned for a
locally-discovered radio, so all three suppressions fire for exactly the radios
where the preference matters.

**Root B — roster and discovery are merged by exclusion, not union.**
`PaintRoster` skips any roster entry already in the discovered list, so
roster-held facts (the operator's chosen name, preferred account, favourite) are
available only for radios that are NOT present.

**Root C — "my client" is resolved by clientId**, which the radio omits on
re-add, while ClientHandle survives. Resolve by ClientHandle.

**Root D — cached availability never expires.** Don's radio earned
`LanAvailable = true` across its whole life on his own LAN, then physically moved
to Tony's. The cached fact did not move with it, so a stale `LanAvailable`
short-circuits to local **permanently**. `LastSeenRemote` is honest about being
history; `LanAvailable` is not. **Not Don-specific** — Field Day, a club radio, a
house move all inherit it.

## Phase 3 — the connect flow Noel wants

- **The Remote button goes away.** One **Connect** button honouring the radio's
  stored preference; Enter activates it.
- **The context menu carries the explicit verbs** — connect locally, connect
  remotely, and set the default. That menu already exists on the radio list with
  a `ContextMenuOpening` handler; extend it.
- **Force-remote is TEST EQUIPMENT, not a convenience.** It is the rescue path
  and the instrument for re-running the hole-punch test. **It must never silently
  fall back to local** — a fallback would invalidate a punch test by succeeding
  over the wrong path.
- **`PreferRemotePath` must stop being a `bool`.** JJ Flexible Connect is a third
  path. **Model it as an ordered chain of paths to try:** Don's radio
  `[SmartLink, Local]`, the bench 8600 `[Local, SmartLink]`, force-remote
  `[SmartLink]` with no fallback. Automatic fallback then stops being special-case
  logic and becomes "walk the list, announcing each move". **Migrate the existing
  bool without losing anyone's setting.**
- **The auth ladder.** No token → native sign-in. Token → refresh (~250 ms,
  yields a fresh id_token since the 2026-08-06 lineage fix; id_tokens expire 60 s
  after issue so this nearly always fires). Attempt connect. Auth-shaped failure →
  one refresh-and-retry. **Still failing on auth → walk to the next path in the
  chain BEFORE prompting.** Only an exhausted chain reaches the native sign-in.
  **Any other failure: report the real error and stop.** A radio that is switched
  off is not an auth problem.
- **Re-auth means `SmartLinkLoginForm`, never the browser.** Any auth path ending
  in "then the browser opens" is a dead end for this user base.
- **The double Enter is authenticate-then-connect, not refresh-then-connect** —
  Noel's direct observation. **Three JIT-refresh call sites already exist in
  `FlexBase`; look there first** — one of them likely refreshes and then fails to
  continue. Establish whether the local-radio and remote-radio double-Enters are
  one defect or two.
- **Record per-radio connection history** — path attempted, outcome, duration,
  timestamp; a short ring, local JSON, never phoned home. **The offer UX and both
  policies are OUT OF SCOPE** — record only.
- **No silent path substitution.** Falling back says so.

## Keyboard audit — owed, not optional

Removing the Remote button removes **Alt+R**. That makes this a key-binding
change: update `docs/help/md/keyboard-reference.md`, update Command Finder
keywords, and give the changelog a line with heads-up language, since removals
are where someone has it in their fingers. **Press every changed chord on a real
build.**

## Rules

- **Reuse the symbols you find. If you conclude one should move or change
  signature, REPORT it rather than doing it.**
- `FlexBase.cs` is shared with B and C in disjoint regions; yours is the
  client-identity area.
- Build: `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
- Commit style: `Track A: <description>`.
- **Do not merge, do not push to main, do not touch other worktrees.**

## Done means

Phase 1's map is written and reviewed. Then: one Connect button walking an
ordered chain, force-remote with no fallback, presence by ClientHandle, roster
and discovery merged by union with the operator's name winning, availability that
expires, the auth ladder wired, connection history recorded, and the keyboard
audit complete.
