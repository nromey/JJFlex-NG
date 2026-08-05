# JJ Flex Research & Work Queue

**Working dashboard.** Distinct from `docs/planning/vision/JJFlex-TODO.md` (long-lived strategic backlog) — this file tracks what's actually queued, in flight, blocked, or waiting for Noel's read **right now**.

**Last updated:** 2026-08-04 (orchestrator queue worked: auto-connect inversion fixed, rename field filed to Track C2). Claude updates this whenever items move between states. If the timestamp drifts more than a session, flag it.

**How to use:** Noel scans the sections below to pick what to fire off, or asks Claude to recommend based on what's available. Claude is expected to keep this current.

---

## In flight (running now)

- **Phase 0 Section F3-G — rarbox FastAPI receiver setup** — F1-F2 complete via SSH-from-orchestrator (nginx 1.26.3 + certbot 4.0.0 + Python 3.13.5 venv with FastAPI 0.136.1 + uvicorn 0.46.0 + pydantic 2.13.4 + python-multipart 0.0.27). F3-G handed off 2026-05-07 to rarbox-Claude (first trial of "Claude lives on rarbox" execution model) with briefing at `docs/planning/active/rarbox-claude-F3-G-briefing.md`. Storage design: zip on disk (forensic preservation) + SQLite index (triage queries) + JSON sidecar (rebuild source). → memory: `project_claude_as_rarbox_operator.md` (promoted from "SSH" to "lives on" model post-trial)

## Queued — orchestrator session, after Noel starts the B/C2 tracks (2026-08-04)

- **DONE (2026-08-04, `62466391` on track/flexlib-4220): auto-connect
  announcement inversion.** Root cause was not a stray utterance in globals.vb —
  the rig selector's constructor set `GlobalAutoConnectCheckbox.IsChecked` from
  the saved setting, which fired the Checked handler and spoke "Auto-connect on
  startup enabled" as if the user had toggled it. Fixed with a suppress-during-
  init flag (same pattern as the LowBW checkbox). The enabled path now speaks
  "Auto connect enabled, connecting to <radio name>" (Critical, interrupt) in
  `TryAutoConnectOnStartup` before the connecting window opens; OFF → silence.
  Awaiting Noel's on-radio verification.
- **FILED to Track C2 (2026-08-04): radio rename field.** Investigation done:
  JJ Flex exposes no rename anywhere; FlexLib `Radio.Nickname` setter works
  (sends `radio name <x>`, persists radio-side, flows back through discovery,
  works over SmartLink too). Fully scoped as work item 5 in
  `C:\dev\jjflex-dialogs\TRACK-INSTRUCTIONS.md` — Step 2 GroupBox on Radio
  Setup, FlexBase setter, auto-connect display-name refresh. C2 owns it
  because the item lands in the same Radio Setup territory as its Know Your
  Radio button; doing it in the orchestrator would invite a merge collision.
  **Noel: the C2 session started before item 5 existed — tell it to re-read
  TRACK-INSTRUCTIONS.md.**

## SmartLink registration findings — 2026-08-04 late-night live run (8600 now REGISTERED, via SmartSDR)

The radio got registered tonight through SmartSDR's chooser as a diagnostic;
JJ Flex's native flow has a structural bug plus a token-architecture gap.
Evidence and fixes, in priority order:

- **Registration must run chooser-style (no client connected) — JJ Flex
  registers while connected, and the radio refuses instantly.** Three
  identical failures while connected (mic+PTT plugged, bare jacks, after a
  radio reboot): ~24s in `wait_on_connection`, then `wait_on_ptt` →
  `failed_ptt` in the SAME millisecond — the radio never opened a keying
  window. SmartSDR (client closed, chooser path) succeeded. FlexLib's
  `WanRegisterRadio` has both paths built in (the `!already_connected` branch
  connects TCP itself with `_ignoreConnectedEvents`). Fix for JJ Flex: step 2
  Register should disconnect the session, register through the
  not-connected path, then reconnect — with clear speech through all three
  phases. Note `failed_ptt` was the radio's label for "client connected",
  NOT a PTT-line problem — worth reporting to Flex via the alpha channel
  (third-party clients will all hit this; SmartSDR never does).
- **SmartLink id_tokens live 60 SECONDS** (decoded live: iat 04:21:56, exp
  04:22:56). Any flow that uses a stored token is using a dead one; only
  just-in-time refresh works. SmartSDR's architecture confirms: it calls
  `RefreshIdToken()` immediately before EVERY `WanRegisterRadio`, refresh
  grant with scope `openid profile`, and USES the returned id_token.
  **Our codebase's belief that "frtest doesn't return id_token on refresh"
  (comments in SmartLinkAccountManager + FlexBase) is contradicted by
  vendor code** — likely scope-related (we send `openid offline_access
  email profile`; SmartSDR sends exactly `openid profile`). Fix: mirror
  SmartSDR — JIT refresh (their scope) before register/unregister and in
  the silent query path; interactive login only when refresh truly fails.
  This also makes the connect-time registration query answer instead of
  returning Unknown (tonight it went silent because the stored JWT was
  expired and silent mode declined to refresh it).
- **The SmartLink server itself is flaky:** SmartSDR's first registration
  attempt was refused; second worked. JJ Flex's flow should auto-retry
  once on a server-side failure before surfacing it.
- **Registration state speech verified working** through the failures:
  terminal states now speak at Critical (1657a8b6). The full success
  sequence has never been heard in JJ Flex — verify it when testing the
  chooser-style fix via an unregister/re-register cycle at the radio.

## Remote-connect field test — 2026-08-05 ~1am (laptop, first true WAN attempts)

- **Key artifact:** connection profile `20260805-053847-514` (NAS
  incoming\laptop-traces\connection-profiles\): remote connect SUCCEEDS
  (5.1s transport vs 130ms local), then the radio adds/drops our GUI
  client twice within 10s → early abort → clean 73. Classic UDP-return-
  path failure: TCP command channel fine, data stream can't get back
  through NAT. Exactly the disease hole punch treats; SmartLink reports
  RequiresHolePunch=True, PublicTlsPort=-1 for this radio.
- **Crash loop found (fartsnoodle-class):** laptop connected locally →
  Mullvad VPN came up underneath → never-ending crash loop. Network
  interface change under a live session must degrade to "connection
  lost" + reconnect offer, never crash. Not yet root-caused (laptop
  tracing was off).
- **Next session protocol (laptop, tracing ON via Operations → Tracing):**
  1) hotspot attempt, 2) Mullvad attempt, 3) crash-loop repro (connect
  local, flip Mullvad on). Full traces each. Watch NegotiatedHolePunchPort
  — the 2026-07-31 wiring fix has still never been observed choosing a
  port.
- **Crash loop ROOT-CAUSED and half-fixed:** all four 00:31-00:32 crash
  reports are one bug — `FlexBase.get_FilterLow` NRE (line 6302) via
  menu rebuild on slice-count change during network teardown. The
  dispatcher catches it, the next slice event re-fires it: report storm
  until killed. FilterLow/FilterHigh getters now null-guarded. **19 more
  getters share the identical unguarded `theRadio.ActiveSlice.X` pattern**
  (grep `return theRadio\.ActiveSlice\.` — Mute, AudioGain, AudioPan, NB/
  WNB/NR/ANF/APF levels, AGCMode, AGCThreshold, SquelchLevel, RFGain,
  PlayEnabled...). Each is the same teardown crash waiting on a different
  trigger. Sweep them with per-property sensible defaults — needs care
  (strings/enums/levels differ), not a mechanical regex. Also consider a
  NativeMenuBar guard: skip RebuildCurrentMenu entirely when the rig is
  tearing down.
- **Crash-dump retention gap:** %AppData%\JJFlexRadio\Errors has NO
  prune policy — Jan–Apr dumps at 200–700MB each, gigabytes total, on
  every machine. Trace archive prunes; error dumps don't. Add retention
  (age or size cap) to the crash reporter design. Safe to hand-delete
  pre-August dumps once tonight's copy lands.

## Queued — agent-ready (fire whenever)

These are bounded research tasks suitable for background agents. Each produces a memo and updates a memory entry.

- **AetherSDR CW implementation review** — Spelunk `c:\dev\aether*` for their CW path; identify FlexLib API usage; note borrowable patterns. ~30 min agent. → memory: `project_cw_keying_design.md`

- **WinKey protocol study** — Web research on WinKey hardware/protocol; assess JJF integration feasibility and value. ~30-60 min agent. → memory: `project_cw_keying_design.md`

- **CW decode roadmap survey** — Survey FLDIGI / MRP40 / open-source CW decoders; recommend integration approach (in-process / out-of-process / external tool). ~30 min agent. → memory: `project_cw_keying_design.md`

- **Computer-side keying-input device survey** — Beyond keyboard/gamepad/touchscreen: MIDI controllers, custom HID, foot pedals. Inform input-device abstraction design. ~30 min agent. → memory: `project_cw_keying_design.md`

- **Tier 2 GregorR `.rnnn` format compatibility test** (~30 min spike, can be human-loop with the v0.2 runtime + sample model files). Verify 2018-era GregorR models load against Xiph v0.2's changed binary weights format. → memory: `project_dsp_controls_design.md`

- **HF SSB listening tests with Don and Justin** (human-loop, post-build). Validate Tier 2 candidates against real ham audio. → memory: `project_dsp_controls_design.md`

- **Training workflow design (REFRAMED 2026-05-04)** — split into two scoped questions: (a) capture-side UX in JJ Flex on Windows; (b) Mac training utility on Apple Silicon GPU. ~45-60 min total. → memory: `project_dsp_controls_design.md`

- **Cross-radio favorites format spec** — Brief design memo on portable favorites exchange format. ~20 min. → memory: `project_ts590_menu_favorites_design.md`

- **ntfy upstream-base-url verification** — Confirm ntfy.sh's iOS APNs relay mechanism is still `upstream-base-url` config (vs. anything new in 2025+). Web research against current ntfy docs. ~15-30 min agent. → memory: `project_ntfy_push_architecture.md`

- **ntfy server hosting decision (rarbox vs roarbox)** — Brief design memo on which box hosts ntfy. Roarbox is the dynamic-services fit; rarbox already has nginx + cert footprint as interim option. Sequencing implications. ~20 min. → memory: `project_ntfy_push_architecture.md`

- **ntfy v1 use-case scoping** — What pushes JJF actually sends in v1 (crash-receipt-to-Noel only, or also update-available, or more?). Determines topic schema and access-control model. ~20 min. → memory: `project_ntfy_push_architecture.md`

- **Three-tier docs split migration plan (Noel ask 2026-05-09)** — Draft a focused planning doc for splitting JJFlex-NG planning corpus into three tiers: (1) public source repo keeps code + design specs that map to build tracks; (2) new private `jjflex-planning` repo holds sprint plans, agendas, runbooks, tester-context-bearing docs, for-noel/for-claude round-trips; (3) `JJFlex-private` (laptop only, never GitHub) keeps AAR + easter-egg unlock codes. Output: migration plan naming what moves where, cross-reference rewrites, round-trip protocol in tier 2, on-box-claude access pattern. ~30-60 min planning pass. **Priority: deferred behind 4.2 release work per Noel 2026-05-09.** Revisit after Sprint 29 plan or post-4.2.0 release.

## Build-authorized — code work waiting

- **TS-590 metadata catalog Phase 1** — Hand-curate ~70-80 EX-menu items per model (TS-590S + TS-590SG) from Kenwood manuals into JSON files at `radios/kenwood-ts590s.json` and `radios/kenwood-ts590sg.json`. Build-now-ship-later authorized. ~2-4 hours agent time. → memory: `project_ts590_menu_favorites_design.md`

- **Stuck-modal escape implementation** — Worktree at `C:\dev\jjflex-stuck-modal` on branch `track/stuck-modal-escape` (branched from `sprint28/home-key-qsk` — pre-4.2.0 baseline, FlexLib 4.1.5). TRACK-INSTRUCTIONS.md in worktree root. **Merge target: sprint28/home-key-qsk → main as pre-4.2.0 foundation drop**. Design memo: `memory/project_stuck_modal_escape_design.md` (~275 LOC across 5 files). Includes the bonus 73-Morse-twice fix.

- **Sprint 28 bug bundle** — All 7 bugs shipped on `track/sprint28-bug-bundle` (commits `6752cafa` through `564e9333`). Awaiting orchestrator merge to `sprint28/home-key-qsk`. Triage doc: `docs/planning/active/sprint28-bug-bundle-triage.md`. **Merge target: sprint28/home-key-qsk → main as pre-4.2.0 foundation drop**, alongside stuck-modal.

- **Phase 0 — Cloudflare R2 + DNS + rarbox setup** — Section A (DNS for jjflexible.radio transferred to Cloudflare) DONE 2026-05-07. Section F (nginx + receiver on rarbox) IN FLIGHT via rarbox-Claude — see "In flight" section. Sections B-E (R2 bucket + custom domain `data.jjflexible.radio` + R2 API tokens + GitHub Action sync workflow) still queued — Noel-side Cloudflare UI work, ~30-60 min when Noel picks them up. Runbook: `docs/planning/active/phase-0-runbook.md`.

## Awaiting Noel input (read-and-respond)

- **Discovery-chain worktree cleanup** — `C:\dev\jjflex-discovery-chain` couldn't be force-removed tonight because the parallel CLI session that built R6 still has the directory locked. Close that CLI session and ping Claude — cleanup is one-line then.

- **R6 trace from Don** — R6 shipped to Don's Dropbox folder 2026-05-05 21:57. Trace lands when Don runs the build. Three possible outcomes per `project_flexlib_4218_discovery_investigation.md` resume-path section.

- **Bug-bundle DESIGN follow-up — Q2 of for-noel/2026-05-04-sprint28-bug-bundle-questions-pull.md.** Two DESIGN entries deferred from the just-finished bundle CLI session: (a) `RunsWithoutRadio` per-command opt-out flag on `KeyTableEntry` (lets SetFreq dialog open with no radio for easter-egg input), and (b) action-aware no-radio announcement using `KeyTableEntry.ShortActionLabel`. Awaiting Noel's yes/no/partial answer.

- **docs/principles.md** — Created 2026-05-04, uncommitted. Noel can commit when convenient (or as part of end-of-day seal).

- **SmartLink login silent-validation test** — Confirm whether bad-credentials feedback now reads automatically under NVDA 2026.1 (browse-mode + WebView2 path), or whether JJF still needs to bridge an announcement in `AuthFormWebView2.cs`. Quick manual test, no agent. → memory: `project_smartlink_login_silent_validation_bug.md`

## Blocked

- **Phase D firmware update implementation** — Per Q5 (2026-05-05): no longer blocking on R5 outcome. The discovery cascade R6 dissolves the firmware-install dependency. Phase D becomes regular Sprint 29 work, not 4.2.0-critical. → memory: `project_firmware_install_dependency_strategy.md` (now archived/decided)

- **Verbosity channels track** — HOLD per Noel's 2026-05-04 decision. Unblocks when Sprint 30+ formally opens.

- **CW live-paddle work (Phase 1)** — Sprint 30+ scope per 2026-05-04 decision. Not currently blocking anything.

- **8600 unbox** — Trigger condition (firmware drop) is met, but personal capacity blocked by surgery. Unblocks post-recovery week. → memory: `project_8600_unbox_firmware_trigger.md`

## Done today (2026-05-05, clears at end-of-day seal)

- **Surgery (8:30 AM Central)** — Procedure successful, anesthesia team handled the osteopetrosis caution, Noel home and recovering.

- **R5 trace from Don analyzed** — MMCSS exonerated. Build marker hygiene gap noted (R5 binary printed "R4 active"; bumped on R6). Investigation memory updated to reflect Outcome B confirmed; resume-path now points at packet-capture / SmartSDR-ILSpy paths if future investigation resumes. → memory: `project_flexlib_4218_discovery_investigation.md`

- **Discovery-fallback-chain track completed and merged** — Three commits on `track/discovery-fallback-chain` (vendor patch + Phase 1 + 1.5 + 1.6) merged into `track/flexlib-42` for R6 assembly. ~960 LOC across 8 files. wpfSelectorProc integration uses additive belt-and-suspenders dedupe (not first-wins) per orchestrator direction note.

- **R6 build assembled and shipped to Don** — Combined R5 MMCSS patch + discovery cascade. Build clean Debug x64, exe timestamp 21:54:23, marker bumped to "R6 active (chain+MMCSS-bypass)". Zip + NOTES at `C:\Users\nrome\Dropbox\JJFlexRadio\don\` (overwriting R5). Historical archive at `docs/planning/active/don-flexlib-4218-discovery/JJFlex_4218-discovery-diagnostic-R6_x64_debug.zip`.

- **for-claude/2026-05-04-42-release-execution-plan-pull.md processed** — All 5 questions answered. Phase 0 runbook extracted as standalone. Memory entries: `project_firmware_install_dependency_strategy.md` marked DECIDED, two new entries `project_crash_triage_bundle_flow.md` and `project_claude_as_rarbox_operator.md`. for-claude copy deleted.

- **MEMORY.md index updated** — Two new entries added (crash triage flow, Claude as rarbox operator); firmware-install-dependency entry rewritten as DECIDED; FlexLib silent-discovery entry updated to reflect R6 shipped.

- **NVDA 2026.1 release noted** — 0-size element invisibility fixed in browse mode (covers WebView2 surfaces like SmartLink Auth0 login + future jjflexible.radio / data.jjflexible.radio web UIs; does NOT cover JJF's native WinForms/WPF UI). Help doc updated; durable record in memory. → memory: `project_nvda_2026_1_zero_size_fix.md`

---

## Conventions for maintenance

- **Item shape:** `**Title** — Status (one phrase). What it produces. Scope. → cross-ref.` Keep terse — single bullet per item, no nested structure.
- **Move items between sections** as state changes. "Queued" → "In flight" when started. "In flight" → "Done today" when complete.
- **Done-today section** clears each end-of-day seal — items that landed today, useful for the seal commit message and the AAR.
- **Blocked items** include WHAT they're blocked on. If the blocker resolves, move them to Queued or Build-authorized.
- **Awaiting Noel input** is the highest-attention section — items here cost wall-clock time per day they wait.
- **Cross-references** are mandatory. Every item points at a memory entry, a for-noel doc, or a research output path.
