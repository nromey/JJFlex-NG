# Nightowl Pileup Ragchew — overnight queue-burn session (2026-08-07)

Six parallel tracks burning the tactical queue while Noel reads the Connect
protocol documentation. Mixed-model experiment: Fable on judgment-heavy
tracks, Opus on spec-ready tracks — per-track observations feed the AAR as
real Opus-vs-Fable data on this codebase.

**Base branch:** `track/flexlib-4220` (the current dev line — do NOT merge
`main` into anything here; see `memory/project_flexlib42_branch_merge_trap.md`).
**Branch naming:** `qb/track-x`. **Worktrees:** `../jjflex-qb-x`.
**Queue of record:** `docs/planning/active/research-queue.md` (pruned 2026-08-07;
every track item is annotated there).

## Phase 0 — DONE (2026-08-07 overnight)

Six stale worktrees removed (braille, dialogs, flexlib-42, multi-radio,
rename, small-fixes). All branches preserved and pushed; `track/flexlib-42`
stays parked per memory. TRACK-INSTRUCTIONS archived to
`docs/planning/agile/archive/track-instructions/`. Build 4.1.16.536 published
to testers' `debug\` (Noel confirmed the LAN ghost sweep live).

## Tracks

### Track A — Orchestrator small-fixes batch (no separate worktree)
**Where:** main repo `C:\dev\JJFlex-NG`, worked by the orchestrator session
between coordination duties. **Model:** Fable (it's this session).
**Scope:** Radio menu maintenance section (Reboot second home + firmware
update entry); lineout-keys-gated-on-PCAudio bug; LocalAudioMute keep-or-kill;
vestigial PlayCwSK PowerOn re-wire removal; Remote re-click 10s timeout
(satisfy from cached list); "Start fresh with SmartLink" button; optional
NativeMenuBar teardown guard. Stretch: connect double-beep on every connect
path (signature sound).
**Also owns:** the rarbox WireGuard NAT lab (infra, approved 2026-08-07) and
all merges.

### Track B — Settings → Audio surface + device pickers
**Worktree:** `C:\dev\jjflex-qb-b`, branch `qb/track-b`. **Model: Opus** —
the spec is fully written (queue + radio-audio-settings research doc).
**Scope:** Radio Outputs group (headphone/lineout sliders + mutes,
live-apply); PC Audio checkbox honest with remote auto-enable; rebuilt audio
device picker (one surface: radio in/out, alert device, CW output); CW-enable
grouped with alert device (default stays FALSE — decided); device-missing
fallback speaks; "why is my radio silent" advisory ladder (rung 1 =
CONNECTED); audio-troubleshooting help topic.
**Owns:** SettingsDialog audio section, JJPortaudio picker UI, audio help md.

### Track C — Per-radio network settings, serial-keyed
**Worktree:** `C:\dev\jjflex-qb-c`, branch `qb/track-c`. **Model: Fable** —
config-model design with real judgment calls (mode semantics, precedence,
offline editing UX).
**Scope:** per-radio profile in `radios\<serial>\config.xml`: mode =
Auto | ForwardOnly | HolePunch + optional fixed punch port; offline editing
from known-radios list; kills both connect-first gates; resolves
ConfiguredListenPort double-duty; per-radio → account → radio-reported
precedence in sendRemoteConnect; ForwardOnly doubles as "disable hole punch"
fail-fast.
**Owns:** SettingsDialog Network + Radios tabs, per-radio store schema,
sendRemoteConnect consult path.

### Track D — Connectivity truth & guidance
**Worktree:** `C:\dev\jjflex-qb-d`, branch `qb/track-d`. **Model: Fable** —
failure-classification and messaging design across a non-unified pipeline.
**Scope:** surface test_connection results on failure; refused vs timed-out;
generated router-rule text from radio-reported values; fix misleading "no RX
antenna"; network identity card (read side); ConnectFailed auth-vs-not
classification; Test Network warn/defer on punched sessions.
**Owns:** connect failure paths, diagnostics surfaces, identity card UI.

### Track E — Selector, roster, dual-homing
**Worktree:** `C:\dev\jjflex-qb-e`, branch `qb/track-e`. **Model: Opus** —
well-specified UI work with clear patterns to follow.
**Scope:** favorite-radios roster; dual-homing with path CHOICE (connect via
SmartLink even when local — Noel 2026-08-07, testing + education value);
per-account cached-list fast paint; LAN/remote row labels; old C2 items
6 (empty-list announcement), 7 (state-driven account button), 14 (arrow
escape), 15 (announce active account).
**Owns:** RigSelectorDialog (ENTIRELY — no other track touches it),
radioConnectionCacheV1 extension.

### Track F — Dialog & SmartLink account sweep (C2 revival)
**Worktree:** `C:\dev\jjflex-qb-f`, branch `qb/track-f`. **Model: Fable** —
the MessageBox sweep is judgment-per-site, and the account flows carry
security-adjacent design calls.
**Scope:** C2 ledger carried forward: items 1 (MessageBox sweep), 3 (GPS
arrowability), 5 (rename field), 5b (ConfirmActionDialog warnings), 8 (native
signup/forgot-password), 8a (mid-session sign-in propagation), 13 (advisory
names account), 17 ("see the message" sweep), startup-speech ordering policy.
**Owns:** AdvisoryDialog/ConfirmActionDialog family, SmartLink account
manager dialogs, SettingsDialog.RadioSetup rename GroupBox (only RadioSetup
touch outside C's tabs).

### Track G — Audio Workshop: hear yourself (plan delivered 2026-08-07)
**Worktree:** `C:\dev\jjflex-qb-g`, branch `qb/track-g`. **Model: Fable** —
phase 1 integrates with keying safety (`PttSafetyController`), which is
judgment-tier work despite the crisp spec.
**Spec:** the LIVING plan file `docs/planning/active/audio-workshop-plan.md`
(mox-parrot-sidetone, Phil2's deliverable — he appends refinements and
verification results; read the main-repo copy at launch, not a snapshot).
**Shape decision (orchestrator):** ONE track, phased — all slices edit
AudioWorkshopDialog, so parallel tracks would collide. **ALL THREE PHASES
BUILDABLE — the 2026-08-07 marathon completed every gating verification**
(record-during-mute = real demodulated RF in the buffer; processing chain
carried; antenna-isolation test closed the three-model saga: genuine RF
through a massively overloaded receiver). Phase 1: Audio Check session
(MOX via PttSafetyController lock path, low-power default ON, two-stage
Escape, key-up announcement SAFETY-CRITICAL, safety line speaks
freq+power, remote-DAF advisory), phone-mode-only monitor work (**CW half
deferred behind the CW pipeline rewrite** — wave 2), TX-source awareness
aimed at the ACTIVE source + mic-source surfacing (source coherence is
the precondition), help rewrite, Command Finder registration,
Ctrl+Shift+W shadow fix, MicInput="PC" investigation. Phase 2: record/
play wrappers, auto-play-on-unkey default, recorder-state check before
re-arm, 120s-cap/two-take awareness, honest fidelity labels. Phase 3:
Loopback Check button — verified recipe + the new hard requirement:
**manage coupling level** (dBm XVTR drive into the receiver's linear
range; open question whether that upgrades the ratified "simulacrum"
framing to clean demodulation — no promises in UI copy); SDR-on-antenna
stays the stated ground-truth tier. Plus the crash pair from plan 4b:
TX-getter family null-guards (FlexBase ~7839 region) and `_meterTimer`
stops when the RIG dies, not just on dialog close.
**Launch gate: NONE — fully cleared 2026-08-07.**
**Owns:** AudioWorkshopDialog.xaml(.cs), FlexBase ~7700-7960 region
(record/play wrappers, public CW monitor pan), one unbound KeyCommands
registration, `docs/help/md/audio-workshop.md`. Does NOT touch
audio-troubleshooting.md (B) or Settings surfaces (B/C/F).

### Track H — Hotkey surface redesign + key coverage audit (added 2026-08-07)
**Worktree:** `C:\dev\jjflex-qb-h`, branch `qb/track-h` — cut at launch.
**Model: Fable.** Noel-directed after live-falsifying the legacy editor
(Help → Key Assignments → Update cannot change a key; SetupKeysDialog is
Jim's pre-v5 key-action system, orphaned from the unified KeyCommands
dispatch). One Keys surface backed by the KeyCommands v5 registry: scope /
alphabetical / function-group views; real editing with conflict
detection (names the collision, steal/cancel), live rebind, unbind,
reset-to-default; field-character keys as read-only rows; Tools and Help
doors both open it (edit vs view); duplicate Help variants collapse.
Deliverable: generated canonical key manifest reconciled against
keyboard-reference.md (the CLAUDE.md keyboard-audit automation seed).
Verify-then-delete the legacy dialog pair.
**Owns:** the new Keys dialog, ShowKeysDialog/SetupKeysDialog retirement,
menu wiring for Tools → Hotkey Editor + Help → Key Assignments,
keyboard-reference.md reconciliation. Reads KeyCommands.cs broadly; merges
AFTER A and G so their registrations are absorbed.

### Track I — Menu-parity audit + XVTR-aware power control (added 2026-08-07)
**Worktree:** `C:\dev\jjflex-qb-i`, branch `qb/track-i` — cut at launch.
**Model: Fable.** Routed from the audio session (plan section 4a) as
app-wide UI architecture. Menu-parity audit: every actionable ScreenFields
control gets an addressable menu path with accelerators (Alt+R → T → P →
Power dialog); part add-missing (power has no menu path), part
make-findable (TX/RX antenna submenus exist at NativeMenuBar ~685-707,
never met by the app's owner), part verify-across-dispatch-paths (four
un-unified paths). XVTR-aware power control: power surfaces switch to
dBm/decimal (`Xvtr.MaxPower`) when TX antenna is a transverter port,
integer watts otherwise; verify typed-digit entry.
**Owns:** NativeMenuBar menu additions, the new Power dialog, ScreenFields
power field behavior. Coordinates with A (Radio menu section) and H (any
new accelerators feed the key manifest) at merge.

### Track J — Slice identity: position vs letter (added 2026-08-07)
**Worktree:** `C:\dev\jjflex-qb-j`, branch `qb/track-j`. **Model: Fable.**
The night's deepest correctness find: `mySlices` position ≠ radio letter
after create/release churn — mode menu hits the wrong slice, pressing D
lands on C, JumpToSlice announces fabricated letters. Fix: the LETTER is
the identity (sort `mySlices` by radio index or map by letter), then
audit every positional consumer (`VFOToSlice`/`SliceToVFO`, direct-select
`ch-'A'`, `JumpToSlice`, RXVFO/TXVFO stale-position across removal,
`ReleaseAllExtraSlices`). Slice menu Selection labels are the regression
canary. **Owns:** FlexBase slice-mapping regions (~4700-5150, 6440-6550,
8828, 10280-10360), FreqOutHandlers slice-select paths, JumpToSlice.
No other track touches these. Merge early — it's correctness.

### Track K — Trace rotation + crash-bundle size policy (added 2026-08-07)
**Worktree:** `C:\dev\jjflex-qb-k`, branch `qb/track-k`. **Model: Opus** —
design ratified (plan 4b), spec-shaped. Driver: an 11.7 GB live trace and
a crash bundle that couldn't attach it. Size-based rotation into session
PARTS (~250-500 MB, zip to archive, start fresh); crash bundles attach
the CURRENT part; upload size policy with an honest "saved fine, too big
to auto-send" message. **Owns:** JJTrace/Tracing.cs, boot maintenance,
SaveCrash/bundle assembly, upload path. Zero overlap with other tracks.

## Execution order

**Final roster (2026-08-07 integration complete): ten spawnable tracks
B–K, plus Track A in the orchestrator.** All ten are mutually independent
— start any subset in any order, simultaneously if desired. Practical
suggestion at this scale: launch in two waves (e.g. B–G, then H–K as
sessions free up); H merges after A/G/I regardless, so its late start
costs nothing. The CW pipeline rewrite is deliberately NOT a track —
design round first (wave 2), fed by the three research memos landing in
`docs/planning/research/cw/`.

## After the merge — guided testing run

When all tracks are merged and the build is clean, the orchestrator prepares
a step-by-step guided testing document (numbered steps, screen-reader-first,
per the directed-testing conventions) covering every track's user-visible
changes. Noel runs it with an Opus session driving. Blockers found mid-run
route back to the orchestrator.

## Merge plan

Target: `track/flexlib-4220`, merged by the orchestrator (Track A session).
Order: **J first** (core slice-identity correctness — everything else
retests on top of it), then **B and E** (as they complete, either order),
then **K and G** (isolated territories, any time), then **F**, then **C**,
then **D**, then **A's own batch + I** (both touch NativeMenuBar — A lands
first, I rebases on it), then **H last** (its key manifest absorbs every
other track's registrations). Rationale for the middle: F's RadioSetup
touch lands before C reworks neighboring tabs; D's failure-path changes sit
closest to C's consult order, so D rebases on C's merged result. Clean
build (`dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64`) after
every merge; exe timestamp verified per CLAUDE.md.

If a track finishes out of order, the orchestrator may adjust and will tell
Noel before proceeding.

## Conflict ownership map

- `RigSelectorDialog.*` — E only.
- `SettingsDialog` audio section — B only. Network/Radios tabs — C only.
  RadioSetup partial — F (rename GroupBox) only; C stays out of RadioSetup.
- `FlexBase.cs` — shared by design (B: gain/mute wrappers exist; C: consult
  order; D: failure surfacing; F: Nickname setter). Different regions; merge
  order absorbs it.
- `KeyCommands.cs` — A only (lineout gate). Any track adding a command
  coordinates through the orchestrator (keyboard audit applies at merge).

## Noel's lane (while tracks run)

Connect protocol reading list + two for-noel research docs; audio workshop
conversation (parallel Fable window → plan file only, no commits); later
today: Don (busy-slices retest, transverter procedure). Radio-seat items when
he feels like it: Release All Extra Slices repro; optional port-forward on
the 8600 for WAN self-testing.

## AAR capture (mixed-model experiment)

At merge time the orchestrator records per track: model used, spec-adherence,
judgment quality where the spec was silent, rework required, and anything the
model refused/fumbled. Goes in tonight's AAR under a "model observations"
section. This is deliberate learning, not vibes.

## Keyboard audit note

Track A's Radio menu work and any track that binds keys triggers the
CLAUDE.md keyboard audit at merge (keyboard-reference.md, Command Finder
keywords, F1 help, changelog).
