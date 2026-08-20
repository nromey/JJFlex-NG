# Sprint 33 — barefoot harness pileup

**Goal, in Noel's words:** *"I'd like to build it all by end of day mainly so we
can exercise the whole app and be more comfortable sending a build to Don so he
can figure out where the tx chain is broken etc."* And: *"Right, and also run the
exercise of actual radio surfaces as well. Do the whole bit."*

**Base:** `honest-tx-audio` at `d179526f` (Sprint 32 merged, 0 errors, 211 tests).

**The premise.** Until now Noel has been the only tester, and every UI regression
had to be found by a human pressing keys. Sprint 32 proved that is not the only
way: a UI Automation probe walked the live tree and located the silent Audio
Workshop in minutes, after three static readings of the same code had all been
wrong. This sprint turns that one-off into a standing capability.

**The three tiers, and why the boundary is where it is.**

- **Tier 1 — in-process.** Construct a dialog, walk its automation tree, assert
  invariants. No desktop, no radio, runs in CI. Catches the silent-Workshop class.
- **Tier 2 — driven.** Launch the real executable, send real keystrokes, observe
  what the tree does. Needs a desktop. Catches the Alt+L class: a binding that
  compiles, reviews clean, and is never handled.
- **Tier 3 — radio in the loop.** Command the radio, read back its own state,
  assert it actually did the thing. Needs hardware. Splits into **3a
  non-transmitting** (runnable today) and **3b transmitting** (harness built now,
  tests parked until the dummy load arrives).

**The rule that keeps this from rotting: assert INVARIANTS, not specifics.**
"Every focusable control has a non-empty name" survives a redesign. "The third
tab stop is Load Preset" breaks the next time someone reorders a panel, and a
suite that cries wolf gets ignored, which is worse than no suite.

---

## Standing constraints — every track obeys these

**Noel is at the keyboard.** Anything that takes focus, sends keystrokes, or
opens windows FIGHTS HIM. Tracks A and B must not steal focus during a normal
build-and-test cycle, and any run that drives the live UI happens only under the
coordination handshake below.

**The four-beat handshake, verbatim from Noel:** *"Just simply, 'gonna run a UI
probe tool' then I say 'cool have at it' and then 'Done' then 'keep going' from
me."* Full stop and authorisation BEFORE. Full stop and notification AFTER. One
authorisation covers one run. If he reports interference, stop immediately.

**Radio state is the operator's, not the harness's.** Snapshot before, restore
after, restore on failure too. A harness that abandons a half-configured station
is worse than no harness.

**No transmitting beyond what is explicitly sanctioned.** No antenna is
connected. 1 watt is acceptable for a smoke test, used sparingly. The ATU is
rationed by RELAY WEAR, not RF — see Track C.

**No tables in any artifact.** Prose or bullets. This applies to test output,
reports and docs alike.

---

## Track A — Tier 1, the in-process invariant sweep

**Worktree:** `../jjflex-33a` · **Branch:** `sprint33/track-a` · **Merge target.**

Build `JJFlexWpf.Tests`, an STA xunit project referencing JJFlexWpf, that
constructs every dialog and walks its automation tree in process.

**The invariants to assert, in priority order:**

1. **Every focusable control exposes a non-empty automation Name.** This is the
   single highest-value assertion in the sprint. The Workshop failure was
   operators tabbing through controls that announced nothing.
2. **No category or tab renders an empty automation subtree while its controls
   remain focusable.** This is the exact silent-Workshop signature: a
   `ContentPresenter`-only template produced a peer with no children while the
   controls inside stayed reachable and correctly named. A tree walk sees it; no
   amount of reading the XAML did.
3. **Every control that declares HelpText has non-empty text.** Description
   drift is this project's dominant defect class.
4. **Focus cycles are conserved** — N moves produce N focus events, no silent
   swallowing.
5. **No duplicate automation ids within one window.** SmartSDR reusing
   `chkboxToggleTX` for two different controls cost a whole evening; we should
   not ship the same trap.

**The hazard to solve early, not at hour three.** WPF automation peers generally
need the element measured and arranged, which usually means the window has been
shown — and showing a window STEALS NOEL'S FOCUS. Solve this before writing the
bulk of the tests. Options worth trying in order: measure/arrange the window
content without showing; `ShowActivated = false` with the window positioned far
off-screen; a dedicated hidden desktop. **Report which one works** — the answer
determines whether Tier 1 can run while Noel is working, which is most of its
value.

**Report, do not fix.** Where a dialog fails an invariant, record it as a finding
with the control's identity. This track's job is the harness and the inventory of
what is broken, not the repairs — those get triaged afterwards, one at a time.

---

## Track B — Tier 2, promote the driver and prove every key routes

**Worktree:** `../jjflex-33b` · **Branch:** `sprint33/track-b`

**First, rescue the tooling.** The UIA probe that found the silent Workshop is
loose PowerShell in a session scratchpad under `%LOCALAPPDATA%\Temp` — a
directory that gets wiped. Promote it into the repository as a versioned tool
with a real name, real arguments and a documented contract. That is the
difference between a capability and an anecdote.

**Then the key-route test.** For every binding in `KeyInventory`, press it for
real and assert something observable happened. This catches the class of bug that
`Alt+L` shipped as: the handler tested `e.Key == Key.L`, which is never true
while Alt is held because WPF reports `Key.System` and puts the real key in
`e.SystemKey`. It compiled. It reviewed clean. The chord was simply never
handled, and the only way to know was to press it.

**Special attention to the 29 commands bound to `Keys.None`** (#130). The harness
should distinguish "menu-only on purpose" from "nobody ever assigned a key,"
which today nothing does.

**This track needs the desktop, so it needs the handshake.** Nothing drives the
live UI without Noel's explicit go, every time.

---

## Track C — Tier 3, the radio surface: 3a runnable, 3b parked

**Worktree:** `../jjflex-33c` · **Branch:** `sprint33/track-c`

**3a — exercise the whole non-transmitting surface.** Command it, read the
radio's own state back, assert it changed. Mode, filters, slices, AGC, NB, NR,
ANF, preamp, attenuator, antenna selection, band, VFO, split, RIT and XIT.

**Guards, and these are not optional:**

- **Snapshot everything before; restore after, including on failure.**
- **Verify not-transmitting before every assertion**, not once at the start.
- **Refuse to run under MultiFlex with another operator connected.** It mutates
  shared station state. TX is a mutex; the rest is merely rude.
- **Slice changes do not persist** (#117) — the harness must not be surprised
  when a released slice returns, and must not "fix" it by writing a profile.

**3b — build the transmit harness, run nothing.** The consent gate, the power
ceiling, the duty-cycle budget with enforced cooling gaps, snapshot-and-restore
of every transmit-affecting setting. The DL-2000 is not here yet; when it
arrives the tests get written against a harness that already exists and has been
reviewed calmly rather than in a hurry with a hot load.

**The ATU is rationed by RELAY WEAR, not RF.** It tunes without an antenna. Give
it a hard budgeted count per run and enforce it in code. Also worth stating
plainly so nobody expects otherwise: **a dummy load cannot meaningfully test the
ATU** — into a matched 50 ohms it finds a match instantly, so only the command
path is exercised. Real tuning behaviour needs a real mismatch.

---

## Track D — the analyzer's fact layer, against a live radio

**Worktree:** `../jjflex-33d` · **Branch:** `sprint33/track-d`

**Why this earns its own track: the analyzer is what Don will actually use, and
a confidently wrong answer is worse than no answer at all.**

The rules engine is already built and genuinely well tested — 30 tests in
`Radios.Tests/ChainAnalyzerTests.cs` covering three-state honesty, unreadable
beating false, unobservable never reading as healthy. **None of that touches fact
collection.** `Radios/ChainChecks/TxChainFacts.cs` reads `rig.MicGain`,
`rig.ForwardPowerWatts`, `rig.SWR`, `rig.MicSource`, `rig.TXSliceLetter` and
friends off `FlexBase`. The engine is correct GIVEN those facts. Whether each
wrapper returns the truth on a live 8600 has never been checked.

**The job: for every fact the analyzer collects, verify it against the radio's
actual state.** Change the thing, confirm the fact moves with it. A fact that
reads plausibly but is wired to the wrong meter produces a confident, wrong
diagnosis — and #139 already suspects exactly that of the TX Peak Watcher's ALC
source.

**Also verify the three-state honesty survives contact with hardware.** Over a
local connection some facts are readable that are not readable over SmartLink.
The analyzer must report NOT OBSERVABLE rather than guessing, and the only way to
know it does is to look.

**Coordinate with Track C** — both want the radio. C owns the non-transmitting
surface; D owns the analyzer's reads. Where D needs a state changed, it should
use C's snapshot-and-restore rather than growing a second one.

---

## Track E — triage the master test list by tier

**Worktree:** `../jjflex-33e` · **Branch:** `sprint33/track-e` · **Docs only.**

Task #149. The master test list runs to roughly eighty items, most of which are
"press this, does it work." **That is not a job for a human, and it is certainly
not a job for the project's only blind tester.**

Go through it and assign every item a tier: 1 if an in-process tree walk settles
it, 2 if it needs real keystrokes, 3 if it needs the radio, and **HUMAN** for the
residue that genuinely requires ears and judgement — does this sound right, is
this wording clear, is this earcon audible against band noise.

**The residue is the deliverable.** The point is not the classification, it is
producing the short list of things only Noel can judge, so his testing time goes
there instead of into tab-order checks a machine does better.

No tables. Prose or bullets, screen-reader first.

---

## Execution order

**Start A, B, C and E immediately.** They are independent.

**Start D when Track C reports its snapshot-and-restore helper committed** —
D reuses it rather than growing a second one.

**And the Sprint 32 lesson applies to that instruction:** telling a track to
reuse a symbol creates an invisible dependency on that symbol staying put. So D's
instructions say **reuse C's helper; if you conclude it should move or change
shape, report it instead of doing it.** Sprint 32 lost a build to two tracks that
merged with zero textual conflict and would not compile.

## Merge order and the collision to expect

Merge into Track A as tracks complete: **E, then B, then C, then D.**

**The predictable collision is `JJFlexRadio.sln`.** Four tracks add a project.
That conflict is additive and visible, which makes it the easy kind — resolve by
keeping every project entry and confirming the platform mappings survived.

**Build after every merge before calling it clean.** A clean `git merge` is not
evidence that the result compiles.

## Definition of done

- Tier 1 runs green, and is documented as either safe-while-Noel-works or not.
- Tier 2 exists in the repo, not in a temp directory, and has pressed every key.
- Tier 3a has exercised the non-transmitting surface with state restored.
- Tier 3b harness reviewed and parked, with the ATU budget enforced in code.
- Every analyzer fact verified against the radio, or explicitly listed as unverified.
- The human-only test residue is written down.
- Findings are recorded as tasks, not fixed in place. Repairs get triaged one at
  a time afterwards.
