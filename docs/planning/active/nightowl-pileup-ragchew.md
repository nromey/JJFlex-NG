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

### Track G — Audio Workshop (deferred until plan lands)
Cut from `docs/planning/active/audio-workshop-plan.md` when Noel's parallel
Fable window delivers it. Own worktree `qb/track-g`; model decided from plan
crispness. Seam: Track B owns SettingsDialog audio; G owns the Audio
Workshop window.

## Execution order

All five spawnable tracks (B–F) are independent of each other. **Noel's
call (2026-08-07): no tracks launch until the audio workshop plan lands and
Track G's instructions are cut** — then B–G can start in any order,
simultaneously if desired. Track A runs in the orchestrator continuously.

## After the merge — guided testing run

When all tracks are merged and the build is clean, the orchestrator prepares
a step-by-step guided testing document (numbered steps, screen-reader-first,
per the directed-testing conventions) covering every track's user-visible
changes. Noel runs it with an Opus session driving. Blockers found mid-run
route back to the orchestrator.

## Merge plan

Target: `track/flexlib-4220`, merged by the orchestrator (Track A session).
Order: **B and E first** (as they complete, either order — smallest overlap),
then **F**, then **C**, then **D**. Rationale: F's RadioSetup touch should land
before C reworks the neighboring tabs; D's failure-path changes sit closest to
C's sendRemoteConnect consult order, so D rebases on C's merged result. Clean
build (`dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64`) after every
merge; exe timestamp verified per CLAUDE.md.

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
