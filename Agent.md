# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main` (post-merge of `track/flexlib-42` on 2026-05-14 — main is now the integration branch. FlexLib 4.2.18 + full discovery cascade live on main. Three ship gates (firmware safety / crash reporter / updater) now gate the **public 4.2.0 release**, not the merge. See `memory/project_main_branch_41_posture.md` (superseded entry) and `memory/project_soft_launch_strategy.md` (2026-05-14 strategic frame).)

## RESUME HERE — 2026-05-14 end-of-day seal: track/flexlib-42 merged home + soft-launch strategic frame + Sprint 29 plan synthesis

**The headline.** The day's structural move: `track/flexlib-42` (FlexLib 4.2.18 + discovery cascade v3 Phases 1-3 + NetworkChangeWatchdog) merged to main as a single 30-commit absorption. 4 real conflicts resolved deliberately — most interestingly the `RadioConnectionCache.cs` add/add, where Track A's trace-tagging and Track B's LRU history compose into a richer single class than either branch had alone. Memory captured a new strategic frame: 4.2.0 is a soft launch BECAUSE Sprint 30's TS-590/Kenwood audience is the scaling event we're building support infrastructure for. Sprint 29 plan synthesis ran on top of all this. Civ VI accessibility work happened in parallel on Noel's side (separate session, separate repo).

### What shipped (2 commits to main + 4 memory updates outside repo)

- **`6d90033e`** — Merge `track/flexlib-42` into main: FlexLib 4.2.18 + discovery cascade. Brings 30 commits onto main including the vendor lib swap (v4.0.1 → v4.2.18 with TLS wrapper reapplied per MIGRATION.md), full discovery cascade v3 (Rungs 1a / 1b / 1c / 1.5a ARP / 1.7 SmartSDR config / Rung 4 SmartLink-as-LAN), NetworkChangeWatchdog wiring via `CascadeNetworkChangeHandler`, Phase 5 firmware-version gating, and the cache-version-string fix for round-trip-safe writes.
- **`046ae9f4`** — Sprint 29 plan synthesis + Don's Track-A test doc. Plan doc gains a "Status update 2026-05-14" header (items table, bundle-vs-release distinction, remaining tracks C/E/O, resolved open questions, pre-spawn infrastructure prep for Track E). Don's test doc establishes the new `for-don/` folder convention with 7 numbered radio tests for the trace archive surface, friendly tone, Results section at bottom.
- **Memory updates** (outside repo, backed up to NAS): `project_verbosity_architecture_proposal.md` HOLD lifted (scaffold authorized as Sprint 29 Track O); `project_main_branch_41_posture.md` superseded (4.1-only rule lifted, three gates now gate public release not merge); `project_soft_launch_strategy.md` new entry (audience-scale framing for 4.2.0 → Sprint 30 transition); `MEMORY.md` index updated to match.

### Conflict resolutions worth remembering

The merge had 4 real conflicts. The instructive ones:

- **`Radios/DiscoveryChain/RadioConnectionCache.cs` (add/add):** both branches added this file independently — main's was the 4.1-line write-only backport plus Sprint 29 Track A trace-tagging, track-42's was the full cascade-canonical version with LRU history (Rung 1c). Resolution COMPOSED both: kept track-42's LRU history + `GetLanIpHistory` + `HistoricalLanIp` class + `AppendLanIpHistory` private method, ADDED main's `using JJTrace;` and `TraceSessionContext.SetConnectionTarget(...)` call after `Save()`. Neither feature replaces the other.
- **`Radios/FlexBase.cs`:** two methods doing the same thing with different names (`RecordConnectedRadioForCache` on main, `RecordConnectedRadioForChain` on track-42, plus duplicate `_radioConnectionCache` field + `GetRadioConnectionCache` accessor). Resolution: keep `RecordConnectedRadioForChain` as canonical (doc comment + more accurate post-cascade name), remove the duplicate method/field/accessor, rename the retry-success call site.
- **`docs/CHANGELOG.md`:** track-42 still had stale 4.1.17 framing; main's Foundation Phase framing is canonical per 2026-05-09 decision to skip 4.1.17 → 4.2.0. Kept Foundation Phase heading; preserved track-42's "Under the hood" network-change-watchdog bullet as a Foundation Phase subsection (real shipping content, not stale).
- **`.gitignore`:** orthogonal additions, just concatenated.

Build clean post-merge (0 errors, 1222 warnings, all pre-existing). Not pushed; local-only until Noel eyeballs the merge.

### Cross-surface activity (per pre-seal sweep)

- **Memory:** 4 files modified — `project_verbosity_architecture_proposal.md` (HOLD lifted), `project_main_branch_41_posture.md` (superseded with preserved-history block + new rule), `project_soft_launch_strategy.md` (new, captures audience-scale framing), `MEMORY.md` (index updated for both — verbosity entry, posture entry, plus new soft launch entry near foundation-phase).
- **Worktrees:** All three (jjflex-braille, jjflex-flexlib-42, jjflex-multi-radio) idle today. `jjflex-flexlib-42` worktree's branch is now merged to main, so the worktree itself can be torn down whenever Noel's ready — but no rush; nothing breaks if it sits.
- **Sibling repos:** `Civ-V-Access` got real work according to Noel ("good work on this and the Civ Vi access mod / helper today"), but no commits visible in `git log --since=midnight` — work was either uncommitted or in a different branch / clone. JJFlex rigmeter doesn't count this work since it's a separate repo.
- **External infra:** NAS backups for memory (×2 — once by publish-nightly script, once by my explicit call; harmless redundancy), private (×2 same reason), and dev-mirror (in progress at seal time). Dropbox nightly promoted to `JJFlex_4.1.16.387_x64_nightly.zip` at top level. Rigmeter JSON snapshot at `historical\stats\2026-05-14-046ae9f4.json`. No Cloudflare/R2/rarbox/roarbox changes.
- **For-don / for-noel / for-claude:** Don's Track-A test doc created in new `for-don/` folder (07:38). for-noel and for-claude empty.
- **Active planning docs:** none in `docs/planning/active/` touched today, but `docs/planning/agile/sprint29-pileup-skip-elmer.md` substantially updated with the synthesis header.

### Decisions made today

- **Bundle vs release distinction** (per Noel: "we can bundle, but we only release when we can upload firmware"). Bundle = merge to main, build installer, nightly Debug to testers. Public 4.2.0 release = single gate, firmware-upload-works end-to-end. Captured in updated `project_main_branch_41_posture.md` + new `project_soft_launch_strategy.md`.
- **Soft launch is deliberate, not arbitrary slowness.** Sprint 29 is shipping SUPPORT INFRASTRUCTURE (crash bundles + updater + firmware updater) for the Sprint 30 audience-scaling moment (Kenwood TS-590 / Hamlib). "Tens to hundreds" of blind operators arriving via JJ Radio folding-in is what we're building for. Don't rush 4.2.0; prove foundation on small tester pool first.
- **Self-contained 150 MB installer stays.** Civ VI install-size question prompted a deep look at framework-dependent / R2R / NativeAOT trade-offs. Conclusion: 150 MB is fine in context (SmartSDR is bigger, Civ VI install dwarfs it), and the friction-tax argument for self-contained (blind users on fresh Windows don't have to install .NET separately) dominates. No code changes.
- **Cascade Rungs 5+6 slip to 4.2.x.** Last-resort rungs (subnet probe with consent, manual IP-entry fallback). Most users connect via earlier rungs; absence doesn't block firmware-upload-works release gate. Benefits from real-world rung-failure data 4.2.0 testers will produce.
- **Bundle format binding → trust the build.** Crash-bundle schema gets locked during Track C implementation, not pre-planned. Schema deliberation matters MORE at Sprint 30 audience scale, but locking pre-emptively is over-engineering at our scale.
- **Verbosity scaffold authorized for Sprint 29 (Track O).** HOLD lifted today. Scaffold only — independent speech / CW / future-braille channels with shared verbosity ladder. Full design (per-channel verbosity, "Critical only" rename) stays Sprint 30+.

### Outstanding for tomorrow

- **TRACK-INSTRUCTIONS.md for Tracks C / E / O.** Plan synthesis is done; track-instructions authorship is the next operational step. ~30 minutes for the three files.
- **Worktree + branch setup.** `../jjflex-29c`, `../jjflex-29e`, `../jjflex-29o` on `sprint29/track-c-crash-reporter`, `sprint29/track-e-firmware`, `sprint29/track-o-verbosity-scaffold`. ~5 minutes.
- **Push the day's commits.** Local-only since I deferred to Noel; `git push origin main` when ready.
- **Updater proving in soft-launch period.** Track D merged but hasn't been exercised against a real version-bump manifest end-to-end. Worth adding as explicit "Updater proving" item to the plan; deliberately publish a manifest bump and watch the auto-check + delta + helper handoff fire on Don's nightly.
- **R2 firmware staging.** Pre-spawn dependency for Track E. Decide which firmware versions to host, stage `.ssdr` files in R2 bucket, validate `data.jjflexible.radio/firmware/<model>/<version>.ssdr` resolves.
- **`project_main_branch_41_posture.md` self-prune.** Memory entry's own self-prune rule says "self-prunes when the merge happens." Merge happened. Can delete tomorrow or leave as historical context.
- **Test pass for Phase D firmware update on Don's 6300.** Mitigation: capture exhaustive pre-upgrade trace + .ssdr archive before doing the upgrade (we lose the only known FlexLib 4.2.18 silent-discovery reproducer if upgrade succeeds).

### Rigmeter snapshot — end of 2026-05-14

- **Authored grand totals:** 871 files, 171,413 lines, 870,368 words. Vendor: 188 files, 55,355 lines (up from prior days because FlexLib 4.2.18 has more files than 4.0.1 had).
- **Combined:** 1,059 files, 226,768 lines, 1,024,162 words.
- **Today (main branch only, since midnight):** 2 commits, 74 files in diff, +8,195 / -2,871 / net +5,324. The big delta is the FlexLib 4.2.18 vendor swap landing on main; ~5K of the +8K is vendor source. Authored-side change is modest (memory edits live outside repo, planning docs, conflict resolutions in 4 files).
- **Trend:** code 230,429 → 171,413 (long-term shape dominated by Phase 0 vendor-prune drop; current growth is real). Docs 967,495 → 39,934 (pruning settled long ago; current growth is real prose).
- **NAS snapshot:** `2026-05-14-046ae9f4.json` (20+ historical points).
- **Branch-scope caveat:** Civ VI accessibility work in `C:\dev\Civ-V-Access` is not counted; rigmeter measures HEAD on the current branch only.

### Setup for tomorrow

- Resume context: this entry. The next operational step is authoring TRACK-INSTRUCTIONS.md for Tracks C / E / O, then setting up the worktrees. The plan synthesis at the top of `docs/planning/agile/sprint29-pileup-skip-elmer.md` is the source of truth for what each track should accomplish.
- If picking up where this session left off: I had just finished updating the plan doc; the next message was going to be "should I author all three TRACK-INSTRUCTIONS now (Path A) or checkpoint first (Path B)?" — Noel sealed the day instead.
- If continuing with track instructions: leverage memory entries `project_user_initiated_feedback_session.md` + `project_crash_triage_bundle_flow.md` for Track C, `project_firmware_update_transport_protocol.md` for Track E, `project_verbosity_architecture_proposal.md` for Track O.
- Civ VI work has its own seal record on Noel's side.

---

## 2026-05-11 end-of-day seal: Foundation testing pass with five fixes, K1-K4 verified, two audits captured

**The headline.** Test-driven foundation day. Noel ran the K1-K4 verifications against Don's 6300 in real time, surfacing five distinct bugs across the connect / disconnect / SmartLink / UI surfaces; each got diagnosed and fixed in-session, with verification immediately after. Plus two audit punch lists captured at end of session covering keyboard-reference drift and the JJ+H context-help discoverability problem. Civ VI accessibility breakthrough happened in parallel in `C:\dev\Civ-vi-access/` (separate repo, separate seal).

### What shipped (10 code/doc commits to main + this seal)

- **`4d087709`** — Trace plain-text retention. Plain `.txt` traces now live alongside the LZMA `.zip` archive for 24h, so Don can open recent traces in Notepad without 7-Zip. LZMA archives keep their 30-day retention. Solves the Sprint 29 Track A regression where `deleteSourceAfter=True` removed the testers' familiar workflow.
- **`859139bb`** — SmartLink tri-state `ConnectToSmartLink`. New `SmartLinkConnectResult` enum (Success / NoRadios / ConnectFailed) so `setupRemote` and `TryAutoConnectRemote` can distinguish "server returned empty radio list" from "couldn't connect at all" and skip the fresh-login retry on the empty-list case. Original symptom: ~30 second hangs after Don's radio off because retry-with-fresh-login hit "Invalid state for application registration" on the already-registered session and silently timed out twice.
- **`8a9144f2`** — RigSelectorDialog UX cleanup. Dropped the redundant "No Radios Found" modal (the protocol-layer `Speak` already announces; modal added ~9s of screen-reader OK-button-hunting). Replaced the Low BW button-as-checkbox anti-pattern with a CheckBox bound to the selected radio's `LowBW` flag, disabled when nothing is selected. Per-radio memory preserved via the existing `AutoConnectConfig.SaveAutoConnectSettings` path. Alt+L still the quick-toggle accelerator.
- **`b7d19b52`** — Test matrix section K added: four Don-connected verifications (K1 SmartLink success path, K2 LowBW round-trip, K3 plain-text trace, K4 no-modal-when-found). All four verified PASS later in session.
- **`d690d61b`** — Build: `SatelliteResourceLanguages=en` in vbproj. Drops 13 .NET runtime satellite-resource folders (~17 MB raw, ~3-6 MB compressed installer). Publish folder 194 MB → 176 MB.
- **`d752c4dd`** — MainWindow `RestoreNoRadioShell()` on disconnect. Without it, `FieldsPanel`/`MetersPanel`/`PanadapterPanel` stayed Visible after disconnect — Home expander appeared that wasn't at cold-start, tab order included Home controls, focus landed on slice content. Symmetric fix: hide them back on disconnect, return focus to FreqOut, match cold-start state exactly.
- **`e19ad192`** — `CloseTheRadio` tears down WAN SmartLink session. The bug Noel reproduced deliberately: "Disconnect, then Remote again" timed out for ~27 seconds because Sprint 26 Phase 4 moved session ownership to the Coordinator without giving the user-disconnect path a teardown hook. Same protocol-level "Invalid state for application registration" pattern as the no-radios case. Partial revert of Sprint 26 Phase 4 for the user-initiated path; non-user paths keep the original "coordinator-owns-lifecycle" model.
- **`15990def`** — For-noel: 4.1.17 test matrix sections A through J extracted as a self-paced testing doc for Noel.
- **`2c1e1085`** — Folded Noel's A + B.1-B.20 results back to canonical: A.1/A.3/A.4 PASS, A.2 carries "add to Don's test case" note, B.1 FAIL (F2 missing band name), B.2 PASS-WITH-NOTE (speech anomaly "47.040"), B.10 FAIL-WITH-FINDINGS (three findings, two flagged with `****`), rest PASS. Two new TODO entries (mode-menu-checkbox, 60m digital-segment auto-mode) from the `****`-flagged findings.
- **`167bee11`** — Two audit docs in `active/`: keyboard-reference vs `KeyCommands.cs` (8 missing bindings + 1 scope collision) and JJ+H dispatcher (discoverability problem — `Ctrl+J, H` correctly invokes `LeaderKeyHelp()` which is uncontextualized by design; Noel reached for F1-style context help).

### Cross-surface activity (per pre-seal sweep)

- **Memory:** 2 files modified — new `project_radio_access_scheduling.md` capturing the design need Noel raised for time-bounded auditable radio-access grants (replacing today's credential-sharing pattern), plus the `MEMORY.md` index update.
- **Worktrees:** All three (jjflex-braille, jjflex-flexlib-42, jjflex-multi-radio) idle today. Track B (cascade) still parked on `sprint29/track-b-cascade`.
- **Sibling repos:** `Civ-vi-access/civilization-vi-screenreader-access/` had `.gitignore`, `ScreenReaderAccess.sln`, `TESTING.md` modified — major parallel arc Noel drove from "non-functional" to "navigable menus, EULA acceptable, game startable." Documented in the AAR; not counted in JJFlex rigmeter. `jjf-data`, `Hamlib`, `AetherSDR`, decompiled SmartSDR dirs all idle.
- **External infra:** NAS backups for memory + private + dev mirror. Rigmeter JSON snapshot at `historical\stats\2026-05-11-167bee11.json`. No Dropbox publish (iterative builds, not artifact builds — 2026-05-10 nightly stands per CLAUDE.md skip rule). No Cloudflare/R2/rarbox/roarbox changes.
- **For-noel / for-claude:** Two for-noel docs created today (one processed mid-session and removed after fold; one — the broader A-J test doc — staying in folder for Noel's B.21+ continuation). for-claude empty.
- **Active planning docs:** 4 docs touched today — `4.1.17-test-matrix.md` (Noel's results folded in), `JJFlex-TODO.md` (two new B.10 entries), `2026-05-11-keyboard-reference-audit.md` (new), `2026-05-11-jj-h-context-help-audit.md` (new).

### Decisions made today

- **Promote bool returns to tri-state enums where the call site wants to make different decisions per failure mode.** `ConnectToSmartLink` was the example today; generalizes to anywhere a `bool` conflates qualitatively different outcomes.
- **Protocol-layer Speak owns the announcement when both layers could speak.** RigSelectorDialog's "No Radios Found" modal was redundant against FlexBase's `setupRemote` Speak; modal removed. Pattern: pick a single source of truth for the announcement, let other layers provide focus / labels / button-state context.
- **`****` annotation convention** confirmed as the right escalation primitive for triage. Two of Noel's B.10 findings flagged with `****` became standalone TODOs; the third stayed in the matrix as a related-finding note.
- **Sprint 26 Phase 4 partial revert** for user-initiated disconnect. The `CloseTheRadio` path now explicitly tears down the WAN session via `Coordinator.DisconnectSession`. Non-user paths (crash, app-shutdown) keep the original "coordinator owns lifecycle" model intact.
- **Trace plain-text retention as architectural addition** rather than Track A revert. Plain text retained 24h alongside the LZMA archive. Solves Don's workflow without giving up the compression benefit on older traces.

### Outstanding for tomorrow

- **Noel: continue B.21+ on the for-noel test doc** at his pace. Path `docs/planning/for-noel/2026-05-11-4.1.17-test-matrix-A-through-J.md`. Each batch can be folded back to canonical via the pattern used today.
- **Doc-edit pass** to resolve the two audit punch lists. Both fix paths converge on one keyboard-reference.md edit. ~20-30 minutes when there's a slot.
- **Mode-menu-checkbox and 60m digital-segment auto-mode** are at top of `docs/planning/vision/JJFlex-TODO.md`. Sprint 29+ candidates, not foundation-critical.
- **`error|` frame parsing** in `WanServerAdapter` dispatcher would be the structural fix for the two SmartLink protocol gaps patched today. Worth a Sprint 29+ candidate even though the symptoms are gone.
- **Alt+F4 shutdown CW cutoff** observation captured in conversation tail; elevate to TODO if it surfaces again.

### Rigmeter snapshot — end of 2026-05-11

- **Authored grand totals:** 110,348 code lines, 38,486 doc lines. Docs-to-code ratio 0.35.
- **Today (main branch only, since midnight):** 11 commits, 17 unique files, +1,566 / -79 / net +1,487.
- **Branch-scope caveat:** Civ VI accessibility work in `C:\dev\Civ-vi-access/` is not counted here; rigmeter measures HEAD on the current branch only.
- **NAS snapshot:** `2026-05-11-167bee11.json` (17 historical points; will re-snapshot post-seal commit when SHA settles).
- **Trend:** code 230,429 → 110,348 (Phase 0 vendor-prune drop dominates long-term shape); docs 967,495 → 38,486 (early derived-artifact exclusion; growth since is real prose).

### Setup for tomorrow

- Resume context: this entry. If continuing test-matrix work, jump to `docs/planning/for-noel/2026-05-11-4.1.17-test-matrix-A-through-J.md` at row B.21.
- If picking up the audit doc-edit pass: both audits live at `docs/planning/active/2026-05-11-*.md`, cross-referenced as companions. One pass on `docs/help/md/keyboard-reference.md` resolves both.
- K1-K4 already verified in-session — no re-test needed on those.
- Civ VI work has its own seal record on its side.

---

## 2026-05-10 end-of-day seal: Test matrices for Sprints 28 and 29, dev-mirror NAS backup, branch cleanup

**The headline.** Planning + tooling day, not a code day. Wrote two test matrices (Sprint 29 standalone for Don; 4.1.17 Section C extended with 22 new tests for Noel covering post-Phase-9 Home polish + Sprint 28 design followup). Created `backup-dev-to-nas.ps1` rolling-mirror to NAS, wired into CLAUDE.md end-of-day-seal step 3a. Two feedback memory entries saved (PowerShell colon-path quirk + no-tables-in-screen-reader-artifacts). 27 stale sprint11-25 branches deleted (all merged into main, deleted with safety `-d`).

### What shipped (no code commits to main today; documentation + tooling + cleanup)

- **Sprint 29 test matrix** (`docs/planning/agile/sprint29-test-matrix.md`, NEW) — covers all 7 merged Sprint 29 tracks (A, D, F, G, H, J, M, N). Per-track functional checks + dual-screen-reader checks (JAWS + NVDA, bullet form not tables) + end-to-end integration block for D+M+N+J update flow. Tester notes for Don. Track B (cascade) flagged in-flight, gets its own matrix when it merges.
- **4.1.17 test matrix Section C extension** (`docs/planning/agile/4.1.17-test-matrix.md`, MODIFIED) — added "Post-Phase-9 Home polish (2026-04-24 through 2026-05-09)" subsection: 4 re-runs of fixed FAIL/CAVEAT entries (C.4f-bis, C.7g-bis, C.7h-bis, C.7j-bis) + 11 net-new Home polish tests (C.30-C.41) + 6 tests for Sprint 28 design followup commit `28e2eaec` (`RunsWithoutRadio` opt-out + action-aware no-radio announcements via ShortActionLabel) at C.42-C.47. Build prerequisite documented: needs commit `28e2eaec` or later in the test build.
- **`backup-dev-to-nas.ps1`** (NEW) — rolling-mirror of `C:\dev\` to NAS `historical\dev-mirror\` via robocopy `/MIR`. Excludes build/dependency caches (bin/obj/.vs/node_modules/etc.), KEEPS `.git` so uncommitted refs and stashes survive a drive failure. Initial run: 2.7 GB / 28,533 files in 5.9 min; incremental: 12 sec. Wired into CLAUDE.md end-of-day-seal as step 3a.
- **CLAUDE.md** (MODIFIED) — added step 3a documenting the new dev-mirror backup, with rationale and exclude list reference.
- **Memory** — 2 new feedback entries: `feedback_powershell_native_arg_colon_path.md` (`/FLAG:C:\path` splits at colon under PowerShell 7 native arg splatter; use `*> $logPath` redirection or `--%` stop-parsing) and `feedback_no_tables_in_screen_reader_artifacts.md` (generalizes existing AAR-only "no tables" rule to all screen-reader-navigable artifacts; Excel-plus-prose-pair acceptable when tabular data really is needed).
- **Branch cleanup** — 27 sprint11-25 branches deleted with `git branch -d` (safety: refuses unmerged). Per-branch SHAs preserved in shell history for any future archaeology. Worktree branches (braille-research, flexlib-42, multi-radio) and in-flight `sprint29/track-b-cascade` preserved.

### Cross-surface activity (per pre-seal sweep)

- **Memory:** 3 files modified today — 2 new feedback entries + MEMORY.md index update. No project entries touched today.
- **Worktrees:** All three (jjflex-braille, jjflex-flexlib-42, jjflex-multi-radio) idle today. No commits since 2026-05-09 seal.
- **Sibling repos (`C:\dev\jjf-data` etc.):** All idle today.
- **External infra:** NAS used for three backups (memory snapshot 418 KB; private docs 1.5 MB; new dev mirror 2.7 GB). Rigmeter snapshot also written to NAS (still keyed by yesterday's HEAD `1a517ae7`; will re-snapshot after seal commit). No Cloudflare/R2/rarbox/roarbox changes today.
- **Dropbox:** No publish today (no new debug build produced — docs/tooling-only day, so the existing nightly stands per CLAUDE.md skip rule).
- **For-claude/for-noel:** Folders don't exist; no round-tripping today.
- **Active planning docs:** None modified today (sprint28-design-followup-track-instructions.md and sprint28-bug-bundle-triage.md both untouched).

### Decisions made today

- **Dev backup model = single rolling mirror, not dated snapshots.** Recovery window is "today only" — git is the time machine for source repos with remotes; memory + private already get their own dated history under `historical\memory\` and `historical\private\`. The mirror's job is "everything that would be permanently lost if the drive failed today," which is non-git artifacts: vendor research clones (smartsdr-extracted, AetherSDR, Dot Pad SDK), per-project `.claude\` state, uncommitted worktree work. Excludes build artifacts and dependency caches; keeps `.git` for stash/ref preservation.
- **Test artifact accessibility rule generalized.** "NEVER tables" was previously AAR-specific (per `project_after_action_reports.md`); now applies to all screen-reader-navigable artifacts (test matrices, planning docs, walkthroughs, runbooks). Linear prose + bullets is the default. Tabular data acceptable only when split into Excel-fill + Word/md-prose-read pair. Triggered by Noel flagging the screen-reader sub-tables in today's first draft of the Sprint 29 matrix; converted in-session.
- **`-d` not `-D` for old-branch cleanup.** Safety-first: `git branch -d` refuses unmerged branches, so even with a stale "merged" assumption, no data loss path.

### Outstanding for tomorrow

- **Noel: testing Section C additions to 4.1.17 matrix.** Slotted-in time around catching up after a week out + migraine recovery. Recommended priority: 4 re-runs first (cheapest, highest payoff per minute), then C.42-C.47 (largest behavioural change in the bunch — `RunsWithoutRadio` + action-aware no-radio).
- **Don: testing Sprint 29 matrix when next build available.** Build prerequisite for both matrices' new tests is commit `28e2eaec` or later.
- **Don's preferred test format unknown.** Noel will ask Don whether linear script (current matrix format) or Excel-fill + prose-read split works better. Either is supported by the existing matrix structure with minimal rework.
- **Track B (cascade) still in-flight** on `sprint29/track-b-cascade`. No movement today; preserved.
- **Sprint 29 cascade matrix** still owed when Track B lands (deliberately deferred until merge).
- **Sprint 28 design followup track** in `docs/planning/active/sprint28-design-followup-track-instructions.md` was the source of commit `28e2eaec`; the active/ doc may be ready to archive now that the work has shipped (worth checking next session).

### Rigmeter snapshot — end of 2026-05-10

- **Today (pre-seal):** 0 commits, 0 unique files in git diff. All work was uncommitted at sweep time.
- **Today (post-seal projection):** 1 seal commit covering 4 files — CLAUDE.md (modified), 4.1.17-test-matrix.md (modified), sprint29-test-matrix.md (new), backup-dev-to-nas.ps1 (new). Plus 2 memory files (outside repo, not in rigmeter scope). Net change: ~+700 lines authored docs.
- **Cumulative pre-seal:** 110,348 authored code lines + 38,110 authored doc lines = 148,458 total. Docs-to-code ratio 0.35. Snapshot pre-seal at NAS as `2026-05-09-1a517ae7.json` (still keyed to yesterday's HEAD).
- **Post-seal snapshot** scheduled after commit lands so today's work is captured in time-series.

### Setup for tomorrow

- Resume context: this entry. Read top of Agent.md, then if testing matrix work, jump to `docs/planning/agile/4.1.17-test-matrix.md` Section C "Post-Phase-9 Home polish" subsection.
- Build dependency for Don's Sprint 29 testing: ensure a debug build at commit `28e2eaec` or later is published to Dropbox `debug\` before pinging Don. Sprint 29 matrix expects this build.
- If Noel tests Section C tomorrow: results land in-place via the existing `Result: ___` → `Result: PASS YYYY-MM-DD, Noel.` convention, then commit-and-push as a normal seal-day flow.

---

## RESUME HERE — 2026-05-09 end-of-day seal: Sprint 29 mass parallel landing (six tracks closed two of three 4.2.0 ship gates)

**The headline.** Sprint 29 ran a six-track parallel fan-out that closed two of three 4.2.0 ship gates plus shipped self-contained installers, a trace browser surface for users, and the cascade watchdog wiring. Tracks F + G merged earlier in the day; D + H + J + M + N merged tonight; L committed to track/flexlib-42 (rides into 4.2.0 cut). Plus orchestrator-side fixes including a format-mismatch catch (D used LZMA Alone, N pinned XZ; XZStream patch landed before D's merge).

### What shipped (commits on main today: 83 across 175 files, +27,909 / -886 lines per rigmeter)

- **Track A Phase 2** (3 commits) — killed-session detection + connection target auto-capture + AS-retry key event hooks + crashed outcome marking + manual trace archive prune helper. Trace persistence now captures the originally-motivating stuck-session forensics scenario.
- **Track C** (1 commit) — crash bundle upload to crashes.jjflexible.radio with N=3 recent traces included, Yes/No consent UX, multipart POST via fire-and-forget Task. Closes the "crash reporter is wired up" 4.2.0 ship gate.
- **CrashReporter polish** (1 commit) — Static SharedHttpClient, removed Task.Run double-wrap, dropped status code from speech, speak on prompt failure, 3-attempt retry loop with backoff.
- **Track F** (merge + 2 commits) — tuning unity (Up/Down coarse + Shift+Up/Down fine, no `C` toggle) + audio gain hotkeys retired into Audio expander + RIT/XIT 1-4 scale-adjust mode + StickyAnnouncedMode helper extraction with filter-edge retrofit.
- **Track G** (merge + 1 commit) — stuck-modal escape changelog patch (the implementation already shipped on main 2026-05-04 as `fdb987e6`; Track G discovered this and just patched the missing changelog).
- **Track D** (merge + 16 commits) — app-updater client: manifest fetch, version compare, delta computation, per-file XZ-compressed download with hash verify, staging dir + handoff to Track M's helper exe, full-bundle fallback, Settings → Updates tab with channel selector, auto-check + periodic-check, Tools → Check for Updates command. Closes the "update plan is operational" 4.2.0 ship gate.
- **Track H** (merge + 1 squash commit) — Trace Archive Browser tab in TraceAdmin with filter row, list view, selection detail panel, and action buttons (View, Copy Path, Export, Delete, Prune).
- **Track J** (merge + 3 commits) — self-contained build pipeline: vbproj `<SelfContained>true</SelfContained>` + `<PublishReadyToRun>true</PublishReadyToRun>`, NSIS installer payload covers self-contained subdirs (recursive deleteList), x64 + x86 installers verified at ~55 MB compressed each.
- **Track M** (merge + 11 commits) — JJFlexUpdaterHelper.exe: standalone single-file exe (~71 MB self-contained) for atomic file replacement during JJF restart. Backup-first + .new-rename atomic replace + rollback path + single-instance mutex + helper.log forensic record. Synthetic test pass: clean update, mid-flight SHA mismatch with rollback, mutex contention, backup blocker, all error paths.
- **Track N** (merge + 1 commit) — server-side manifest generation tooling at `tools/jjflex-manifest-gen/`: Python click CLI generates per-version manifest, LZMA-compresses each file with FORMAT_XZ, uploads to R2 at content-addressable URLs, updates app-manifest's channel pointers. Includes pytest suite + R2 client wrapper + dry-run/no-upload modes. App-manifest concurrency racy by design (acceptable; if_match support reserved for future hardening).
- **Track L (track/flexlib-42)** — NetworkChangeWatchdog wiring: `CascadeNetworkChangeHandler` registers OS-level network change subscription in ApplicationEvents.vb, emits `network_change_observed` key event into the active TraceSessionContext for forensic correlation. v1 is observe-only; cache invalidation handled implicitly by existing NLM gating (72cc0edd).
- **Documentation** — CHANGELOG renamed v4-1-17 anchors to "Unreleased / Foundation Phase" (4.1.17 retired in favor of direct-to-4.2.0); Sprint 29 plan updated with Track G + worktree paths; trace navigation design doc authored.

### Cross-surface activity (per pre-seal sweep)

- **Memory:** 12 files modified today across feedback/project types. New entries: `project_2026_05_09_priority_planning.md`, `project_self_contained_runtime_direction.md`, `feedback_track_launch_prompts_must_name_file.md`. Updated entries: `project_sprint29_updater_vision.md` (delta-from-day-one firm-up), `project_stuck_modal_escape_design.md` (SHIPPED status), `project_flexlib_4218_merge_sequencing.md` (4.1.17-skip firm-up).
- **Worktrees:** Six sprint29 worktrees spawned, all five merged ones cleaned up (D, H, J, M, N); track/flexlib-42 retained with Tracks B + L; braille-research and multi-radio quiet today.
- **External infra:** No new R2 / Cloudflare / NAS service changes today (jjf-data repo quiet). NAS used for backups (memory + private archives).
- **Dropbox:** No tester publish today (no new debug build at end-of-day; existing nightly stands). Tomorrow's plan to generate test packages (per Noel) will refresh.
- **For-claude/for-noel:** No new pull-docs round-tripped today; the stuck-modal-escape memory caught up to "shipped" status which retires that for-noel design pull.
- **Sealed cleanup:** D/H/J/M/N branches deleted; B/L preserved on track/flexlib-42; C branch deleted (its work landed directly on main).

### Decisions made today

- **No 4.1.17 release.** Going direct to 4.2.0 with FlexLib 4.2.18 absorbed. CHANGELOG section renamed "Unreleased / Foundation Phase — staging for 4.2.0."
- **Self-contained .NET 10 installer for 4.2.0 from day one.** Was previously framed as "Sprint 30 candidate"; firmed up tonight after the friction-tax-asymmetry argument. Single publish flag + NSIS template adjustment + ~150 MB installers. Defer NativeAOT (WPF reflection patterns block it; SmartSDR's NativeAOT path doesn't apply).
- **Delta updates from day one.** Previously framed as "v2 optimization"; firmed up tonight after Noel's "if it takes a few more tracks why not ship the version that does what we want." Per-file LZMA via FORMAT_XZ at upload + R2 content-addressable storage + client-side decompression. Roarbox cross-file bundling considered then deferred — bandwidth is free on R2, the additional 10-20% dedup gain doesn't justify the operational complexity.
- **Track-launch-prompt convention firmed up.** Memory entry captures: launch prompts MUST name `TRACK-INSTRUCTIONS.md` explicitly. "Follow track instructions" is ambiguous; "read TRACK-INSTRUCTIONS.md" is reliable.

### Outstanding for tomorrow

- **Track E (firmware update Phase D)** — depends on D being on main (now merged). Can spawn whenever ready. Closes the third 4.2.0 ship gate.
- **Test packages** for 4.1 work and 4.2 work — per Noel, generate tomorrow.
- **Track J's deferred items** — fresh-VM install test (need clean VM), real-radio smoke test (need radio session).
- **End-to-end delta update integration test** — once a real release manifest exists on R2, verify D + M + N work together against live infrastructure.
- **Foundation Phase changelog audit** — small polish surface; missed entries for action-aware no-radio announcements, smarter crash + diagnostic data, filter-edge unification.
- **Roarbox NVDA Remote Server install** (per Doug ask 2026-05-08) and ntfy notifier setup — both queued.

### Rigmeter snapshot — end of 2026-05-09

- **Today:** 83 commits, 175 unique files, +27,909 / -886 lines (net +27,023). All 83 commits authored by JJ Flexbot.
- **Cumulative:** 102,689 authored code lines + 29,007 authored doc lines = 131,696 total. Docs-to-code ratio 0.35. Snapshot written to NAS as `2026-05-09-eea25eec.json`.

---

## RESUME HERE — 2026-05-09 (Sprint 29 Tracks F + G merged; Track B Phase 3 in flight on cascade branch)

Today's merges to main:
- **Sprint 29 Track F** (`2e03345e`) — tuning UX bundle: StickyAnnouncedMode helper extracted, filter-edge code retrofitted, tuning unity (Up/Down coarse + Shift+Up/Down fine, no more `C` toggle), audio gain hotkeys retired into Audio expander, RIT/XIT 1-4 scale-adjust mode, Settings split (Coarse step + Fine step). Per memory/project_sprint29_tune_redesign.md and memory/project_sprint29_rit_xit_adjust_mode.md.
- **Sprint 29 Track G** (`1dd2aa32`) — stuck-modal escape changelog patch only. The implementation already shipped 2026-05-04 as `fdb987e6`; Track G discovered this and just patched the missing user-facing changelog entry. Memory `project_stuck_modal_escape_design.md` updated to reflect SHIPPED status.

Build state: clean Debug x64 build verified post-merge. Branches deleted, worktrees removed.

**Track B (`sprint29/track-b-cascade`)** still in flight in worktree `C:/dev/jjflex-flexlib-42`. Latest commit: `6de629b9 NetworkChangeWatchdog (standalone class)` — Phase 3 just started. NetworkChangeWatchdog class exists but is NOT yet wired into the cascade entry points. **Wiring is the next concrete work item** for Track B continuation.

**Track C (`sprint29/track-c-feedback`)** branch exists with no worktree; no recent commits. User-initiated feedback session work scoped per memory but not actively in flight.

Sprint 29 plan: `docs/planning/agile/sprint29-pileup-skip-elmer.md` — Track F and G entries can be marked complete; Track B entry shows Phase 1 + Phase 2 done, Phase 3 partial.

## RESUME HERE — 2026-05-08 (after Phase 0 F3-G ships via rarbox-Claude)

Phase 0 launched 2026-05-07 with the "Claude lives on rarbox" pivot. F1 + F2 done via SSH (nginx/certbot/Python/FastAPI installed). F3-G handed off to rarbox-Claude with full briefing. Resume:

1. On rarbox: `cd /home/ner/jjflex-ng && claude`
2. Paste the kickoff prompt — it tells rarbox-Claude to read `docs/planning/active/rarbox-claude-F3-G-briefing.md` (which references the procedural runbook `rarbox-setup-runbook-for-claude.md`)
3. Watch rarbox-Claude execute F3 → F4 → F5 → G; verify when prompted
4. **F5 confirmation gate:** generate Cloudflare Origin Certificate (Cloudflare dashboard → SSL/TLS → Origin Server → Create Certificate) and install at `/etc/ssl/cloudflare/jjflexible.radio.pem` + `.key` before nginx config goes in
5. After Section G passes, paste the report-back here so the rarbox-operator memory entry can be promoted from tentative to confirmed (or, if it went sideways, lessons recorded)

**Other for-noel pulls still queued (lower priority):**
- `docs/planning/for-noel/2026-05-06-discovery-cascade-v3-design-pull.md` — discovery cascade v3 design (decision: v3 supersedes R6 vs layers alongside)
- `docs/planning/for-noel/2026-05-04-foundation-drop-test-pull.md` — foundation drop test (still untouched)
- `docs/planning/for-noel/2026-05-04-sprint28-bug-bundle-questions-pull.md` — Q2 still open (RunsWithoutRadio + action-aware no-radio)
- `docs/planning/for-noel/2026-05-04-42-release-execution-plan-pull.md` — strategic context (mostly executing now via Phase 0)

---

## 2026-05-07 end-of-day seal: Phase 0 launch + "Claude lives on rarbox" pivot

**The big shifts.** Phase 0 — the Cloudflare R2 + DNS + rarbox setup that every 4.2.0 external dependency hangs on — went from "queued" to "in flight." DNS for `jjflexible.radio` moved to Cloudflare; `crashes.jjflexible.radio` resolves through Cloudflare's edge; nginx + Python + FastAPI installed on rarbox; F3-G handed off to rarbox-Claude as the first concrete trial of the "Claude lives on rarbox" execution model. Three architectural decisions captured (hybrid storage, on-box Claude model, ntfy push architecture).

### External infrastructure (today's primary work)

- **Cloudflare onboarding:** `jjflexible.radio` zone added; nameservers flipped at register.radio. Active in <5 min — `.radio` propagates faster than expected.
- **DNS A record:** `crashes.jjflexible.radio` → `178.156.204.128`, Proxied (orange cloud). Verified resolving via Google DNS to Cloudflare anycast (`104.21.31.128`, `172.67.176.135`, plus IPv6 dual-stack).
- **rarbox F1 — nginx + certbot:** `apt install -y nginx certbot python3-certbot-nginx` → nginx 1.26.3-3+deb13u2, certbot 4.0.0-2, python3-certbot-nginx 4.0.0-2. nginx.service auto-enabled. UFW already had 80/443 open per `project_rarbox_hardening.md`.
- **rarbox F2 — Python venv:** `/opt/jjflex-receiver/venv` with fastapi 0.136.1, uvicorn 0.46.0, pydantic 2.13.4, python-multipart 0.0.27. Imports clean.

### Architectural decisions captured today

- **Storage design (hybrid, approved):** zip on disk (`/var/lib/jjflex-receiver/<date>/<uuid>.zip`) for forensic preservation + SQLite index at `index.db` for triage queries + JSON sidecar per bundle (`<uuid>.json`) as the rebuildable source of truth. Honors auditable-systems preference + preserves byte-perfect user uploads.
- **"Claude lives on rarbox" pivot (tentative pending F3-G validation):** mid-trial shift from SSH-from-elsewhere to local Claude Code session ON rarbox. Triggered by quote-escape pain on F2 verify (PowerShell→bash→python heredoc) plus looking ahead at writing 100+ line app.py via heredoc. `project_claude_as_rarbox_operator.md` updated with full Evolution section; original SSH model preserved as historical context (revertable if F3-G goes sideways).
- **ntfy push architecture (firm decision):** self-hosted ntfy server (likely roarbox per dynamic-services framing) with `upstream-base-url: https://ntfy.sh` for iOS APNs relay. Topic names leak to ntfy.sh; message bodies stay self-hosted. Approved against `project_no_silent_phone_home.md` as user-initiated subscription. New memory: `project_ntfy_push_architecture.md`. Three implementation questions added to research queue (upstream-base-url verification, host decision, v1 use-case scoping).

### Repo additions (committed in this seal)

- **NEW:** `docs/planning/active/rarbox-claude-F3-G-briefing.md` — ~280-line handoff briefing for rarbox-Claude. Inlines load-bearing memory excerpts (crash-triage flow, no-silent-phone-home, auditable-systems) since rarbox-Claude has its own disjoint memory tree. Includes storage schema, endpoint contracts, validation rules, F5 confirmation gates, out-of-scope reminders.
- **MODIFIED:** `docs/planning/active/research-queue.md` — timestamp bumped to 2026-05-07; "In flight" populated with Phase 0 F3-G; Phase 0 entry restructured (Section A done / F in flight / B-E queued); three ntfy items added to Queued — agent-ready.
- **MODIFIED:** `CLAUDE.md` — End-of-day section now anchors `backup-memory-to-nas.ps1` and `backup-private-to-nas.ps1` at repo root with explicit absolute paths + invocation-pattern note (call-operator preferred over `powershell -File`, which silently exits 0 on bad paths). Tonight's seal hit this footgun — caught it because the repeated invocation revealed the silent failure.

### Memory updates (separately backed up to NAS in tonight's seal)

- `project_claude_as_rarbox_operator.md` — added Evolution section (~50 lines) for the on-box pivot; frontmatter description and MEMORY.md index line updated.
- `project_ntfy_push_architecture.md` — NEW. Decision spec, privacy posture, open implementation questions.
- `MEMORY.md` — two index lines updated (ntfy added, rarbox-operator updated).

### Cross-surface state at end of 05-07

- **Main repo** (`feature/cache-writer-backport`): 3 file changes today (briefing + research-queue + CLAUDE.md); pushed in this seal.
- **External (Cloudflare):** zone active for `jjflexible.radio`; one A record live; Sections B-E queued for Noel UI work.
- **External (rarbox):** packages installed, Python venv ready, nothing yet listening on 8000/443. Receiver app NOT yet deployed. nginx still serving the default Debian page on 80/443.
- **rarbox-Claude:** briefing + runbook files dropped at `/home/ner/jjflex-ng/docs/planning/active/`; session not yet kicked off — that's tomorrow.
- **NAS:** memory + private snapshots refreshed (`memory-20260507-235826.zip` + `private-20260507-221459.zip`).
- **Other worktrees:** no activity (`track/flexlib-42`, `track/braille-research`, `track/multi-radio` quiet).

### Skipped today (per CLAUDE.md skip rules)

- **Daily debug publish to Dropbox:** no new debug build (docs + setup work only)
- **Rigmeter snapshot:** docs-only day; values mostly unchanged from yesterday's heavy-doc snapshot

### CLAUDE.md drift check

- **Fixed tonight:** script-path anchoring + invocation-pattern note (see "Repo additions" above).
- **Still pending from 05-06 seal:** stale NAS path reference, sweep pattern reference, step ordering for publish-daily. Queued for next CLAUDE.md polish pass.
- **Still pending from 05-04 seal:** publish-daily → publish-nightly rename across script + CLAUDE.md + filenames.

### What's set up for tomorrow

- **Kick off rarbox-Claude** to execute Phase 0 Section F3-G (resume path in RESUME HERE above).
- **Generate Cloudflare Origin Certificate** at the F5 confirmation gate.
- **Promote rarbox-operator memory entry** from tentative to confirmed after F3-G completes (or record lessons if it goes sideways).
- **Phase 0 Sections B-E** (R2 bucket + `data.jjflexible.radio` custom domain + R2 API tokens + GitHub Action sync) remain queued for Noel UI work.

---

## 2026-05-06 end-of-day seal (sealed 05-07 morning after surgery-recovery pain night)

**The big wins.** R6 cache-writer backport landed end-to-end. Don connected to his radio in 296ms via the cached IP — first real-world validation that the write-then-read chain across the 4.1 / 4.2 line works. Plus discovery cascade v3 — six research streams synthesized into a unified design memo that supersedes R6 — committed and pulled to for-noel/.

### Code that landed (main repo, branch `feature/cache-writer-backport`)

- **e2d4ebea** — Cache writer backport: 4.1 line seeds `radioConnectionCacheV1.xml`. Closes the chicken-and-egg in `project_autoconnect_no_ip_dead_end.md`. Without this, R6's cache-reader has nothing to read on first connect; with this, the 4.1 line lays the cache so R6 (running off 4.2.18) finds the IP on the very next launch. Don's 296ms reconnect proves the write-then-read pipeline.
- **e7e2e3b2** — Cache writer: use `FlexVersion.ToString` for round-trip-safe version string. Polish-only fix: prior code stored the version as a packed long (something like "1127020893346674"), which R6 parses as 0.0.0.0 and trips up FlexLib 4.2.18 feature gating. Doesn't break the connect itself, so Don's 4.1.16.241 build still works fine — but the 4.1.16.242 build with this fix was archived to NAS rather than shipped to Don, since the next public build picks it up automatically.
- **1e21f563** — Discovery cascade v3 research: 6 streams complete, 1 awaiting hardware. 4,276 insertions of memos and synthesis notes.
- **0c5ddf00** — Discovery cascade v3 design memo synthesized. 826 insertions, 6 deletions.
- **f4256aa4** — Archive Don's 2026-05-06 trace corpus + for-noel v3 review pulls. 2,991 insertions. Don's traces preserved in repo for future bisects; v3 pulls added to for-noel/.
- **b46388ec** — Archive 4.1.16.242 polish build — Don doesn't need to act on it. 54 insertions (zip + NOTES on NAS only).

### Code that landed (flexlib-42 worktree, branch `track/flexlib-42`)

- **5bfc6501** — DiscoveryChain: log per-rung outcome at Info regardless of success. Makes per-rung diagnostic visibility unconditional — was Verbose-on-failure, now Info-always.
- **bd3d2035** — RadioConnectionCache: use FlexVersion.ToString for round-trip-safe write. Same fix as e7e2e3b2, applied to the 4.2 line so the cache stays format-compatible across the 4.1/4.2 boundary.

### WIP committed during this seal (Discovery.cs R6 instrumentation)

`Discovery.cs` in the flexlib-42 worktree carries 246 lines of R6 silent-discovery diagnostic instrumentation: unconditional build-marker, NIC enumeration, bind-attempt logging, `SelfTest()` (loopback / NIC-self / limited-broadcast self-test before main Receive loop), and `SyncReceiveDrain()` (5-second sync receive on the bound socket before async handoff). Per `project_flexlib_4218_discovery_investigation.md`, R1–R5 falsified every named source-level suspect and R6 chain-not-gate dissolves the investigation's blocking criticality — so this instrumentation is parked diagnostic work, not a planned shipping feature. Sealed here as a clearly-labeled WIP commit so the experiment stays preserved + pushable rather than living indefinitely as a working-tree edit.

### External infrastructure work (uncommitted on 05-06 EOD; committed in this seal)

A pivot from pure code work into ops planning. Rarbox is the existing VPS hosting `crashes.jjflexible.radio`; roarbox is the newer/faster box being repurposed (see `project_roarbox_vs_rarbox.md`). Five new planning docs:

- `rarbox-bootstrap.md` — Claude Code install runbook for the existing rarbox VPS so the on-box Claude session can take over Phase 0 receiver setup.
- `roarbox-bootstrap.md` — equivalent for roarbox: NOPASSWD setup for `noel`, install Claude Code, then hand off to on-box session.
- `roarbox-account-cleanup-runbook.md` — multi-tenant cleanup procedure (rename `patrick` → `borris`, decide Doug's NOPASSWD status, etc.).
- `roarbox-upgrade-parts-list.md` — CPU upgrade parts shopping list (1× E5-2620 v2 → 2× E5-2697 v2, the official R620 dual-CPU ceiling, plus shroud + fans).
- `chris-roarbox-inspection-questions.md` — four pre-purchase questions for Chris Polk so Noel doesn't ship the wrong shroud or fan complement.

### Helper / workflow updates

- `screen-reader-setup.md` — added one sentence noting NVDA 2026.1 is current and includes accessibility improvements relevant to web-style content (covers the SmartLink WebView2 login path).
- `research-queue.md` — added SmartLink login silent-validation manual-test entry; added NVDA-2026.1-released note in Recently completed.
- `debug-notes.txt` — replaced with the cache-writer-fix note for Don (the F2+M debug-notes content moved to `debug-notes.sprint28-bugbundle.txt` so the canonical `debug-notes.txt` always reflects the latest unsent debug build context).
- `JJFlexRadio.chm` — rebuilt (2-byte change; routine help-content sync).

### Cross-surface state at end of 05-06

- **Main repo** (`feature/cache-writer-backport`): 8 commits ahead of where the day started; sealed and pushed in this run.
- **`track/flexlib-42` worktree**: 2 commits + 1 WIP commit (Discovery.cs R6 instrumentation); pushed in this run.
- **`track/braille-research` worktree**: handoff.md edits committed and pushed in this run; no other 05-06 activity.
- **`track/multi-radio` worktree**: hamlib-integration-spike.md license clarifications (LGPL-vs-MIT phrasing tightened, `send_morse` scope note added, refresh-LICENSE.txt question added) committed and pushed in this run.
- **NAS**: 4.1.16.242 archive already in place; memory + private + stats snapshots refreshed in this seal.

### What's set up for next session

- **Discovery cascade v3 design pull** is the priority for Noel when energy returns. Short pull is `2026-05-06-discovery-cascade-v3-design-pull.md`; full memo is `2026-05-06-discovery-cascade-v3-full-memo.md`. Decision needed on whether v3 supersedes R6 in the chain or layers alongside.
- **Chris Polk has the four inspection questions** — when Chris answers, parts can be ordered, and the roarbox CPU upgrade arc can proceed.
- **rarbox / roarbox bootstraps** are runbooks Noel executes from his Windows machine; either can fire when convenient.
- **No changes pending merge to main**. Foundation/polish work on `sprint28/home-key-qsk` still awaits Noel's foundation-drop test pull. 4.2.0 work on `track/flexlib-42` still gated on the three 4.2.0 ship gates per `project_main_branch_41_posture.md`.
- **R6 instrumentation in flexlib-42** is parked as a labeled WIP commit; can be revived if a future trace surfaces a new lead, or pruned during the next flexlib-42 cleanup pass.

### Rigmeter snapshot — end of 2026-05-06

**Grand totals (authored, excluding vendor):**
- Files: 774 | Lines: 155,602 | Words: 766,534 | Chars: 7,164,564
- Per-category authored: code 102,808 lines; text_data 11,797 lines; docs 35,129 lines; build 5,868 lines
- Vendor totals: code 51,437 lines; text_data 1,211 lines; build 592 lines

**Top languages by authored lines:**
- C#: 77,453 (75.3% authored) — FlexLib wrappers, JJFlexWpf, JJTrace, JJLogLib, JJPortaudio, P-Opus, etc.
- VB.NET: 17,343 (16.9% authored) — main app + globals
- XAML: 4,985 (4.8% authored) — UI controls + dialogs
- Python: 3,027 (2.9% authored) — rigmeter + tools

**Today's git activity (05-06, main + flexlib-42 + WIP committed in this seal):**
- Commits: 8 + 1 WIP + 3 seal-day commits = ~12 across surfaces
- Insertions: ~9,150 across 30+ files
- Deletions: ~110 (mostly debug-notes.txt rewrite)
- **Caveat:** rigmeter `today` ran on 05-07 and reported 0 because we're a day past 05-06 local. Numbers above are reconstructed from `git log --shortstat` per surface.

**Fun comparisons (authored only):**
- Braille volumes (≈100K cells each): 71.6
- Moby Dicks (~210K words): 3.7
- King James Bibles (~783K words): 0.98
- Read-aloud time at 150 wpm: 85.2 hours (5,110 minutes)
- Printed pages (50 lines/page): 3,112
- Stack-of-printed-pages height: 12.4 inches (31.6 cm)
- Docs-to-code ratio: 0.34 (authored docs / authored code, by lines)

**Trend across 12 historical snapshots** — code base grew 102,607 → 102,808 (+201) and doc base grew 28,692 → 35,129 (+6,437) since 05-05. Heavy doc day.

**NAS JSON snapshot:** `\\nas.macaw-jazz.ts.net\jjflex\historical\stats\2026-05-06-b46388ec.json`

### CLAUDE.md drift check — three flagged items (Step 6)

1. **Stale NAS path reference at line 334.** CLAUDE.md says `publish-daily-to-dropbox.ps1` "Copies the newest debug zip from NAS `nightly\` to Dropbox top level." There is no `nightly\` folder on NAS — the actual layout is `historical\<version>\x64-debug\`. The script's own docstring is correct (`\\nas...\jjflex\historical\<expected>\x64-debug\JJFlex_<expected>_x64_debug_*.zip`); only CLAUDE.md is stale.

2. **Stale sweep-pattern reference at line 334.** CLAUDE.md says the publish-daily script replaces `JJFlex_*_debug*.zip` and `NOTES-*-debug*.txt`. Actual top-level Dropbox filenames are `JJFlex_<version>_x64_daily.zip` and `NOTES-daily.txt`, and today's run swept `JJFlex_4.1.16.236_x64_daily.zip` and `NOTES-daily.txt` (matching the `*_daily.*` pattern noted in the 05-04 publish-nightly rename queue, not the `*_debug*.*` pattern). The script source treats both patterns; CLAUDE.md should reflect that.

3. **Step ordering implied vs. required.** CLAUDE.md lists publish-daily as Step 1, but the script refuses on dirty-tree-with-no-matching-NAS-archive (which is the most common state when sealing the day's work). Practically, Step 5 (commit/push) must run first to reach a clean tree, then publish-daily can build and promote. Adding a one-line note about ordering — "if HEAD does not match an existing NAS archive, do Step 5 first so the tree is clean" — would prevent the next-day-Claude from hitting the same gotcha.

Plus the still-pending publish-daily → publish-nightly rename queued from 05-04. Captured in the 05-04 seal "Tomorrow's autonomous-pass" subsection. Surface still uses `daily` naming everywhere in script + CLAUDE.md + filenames.

None of these block the seal as-completed today; flagging for a future tidy-up pass.

---

## 2026-05-04 end-of-day seal: foundation drop merged to sprint28/home-key-qsk + 4.2.0 release plan + format-preference learnings

**Big day.** Two parallel tracks landed and merged: stuck-modal escape (the 200-second-trap-on-Don's-radio fix) and the 7-bug polish bundle from the 2026-04-28 testing session. Both branched from sprint28/home-key-qsk per the new "main = 4.1 working branch" strategic posture (see new memory entries) — testable against current FlexLib 4.1.5 baseline without waiting on 4.2.18 silent-discovery resolution. Strategic execution plan written for the 4.2.0 release covering crash reporter + updater + firmware updater, with Phase 0 runbook for Cloudflare R2 + DNS + rarbox setup. Five new memory entries: two architectural (independent merge events, main = 4.1 posture), three workflow (open questions to for-noel, screen-reader textual markers, test matrix vs guided paper vs for-noel pull). Plus today's substantial format-preference learnings on testing deliverables.

### Code that landed

- **Stuck-modal track** (commit `de239328` merge): 11 files, ~600 LOC. State-aware connecting modal subscribed to `ConnectionProfiler` events, always-honored Escape with Critical-level cancel speech, 60-second escalation with diagnostic-rich text from profiler events, 5-minute hard auto-cancel ceiling, verbosity opt-out toggle in Settings → Notifications, counting earcons (1, 1+1, 1+1+1) for phase progression, gating of phase speech on phases-taking-longer-than-500ms (fast common case stays unobtrusive), 73-Morse-twice fix on the disconnect path. Builds clean.
- **Bug bundle track** (commit `ee8faebb` merge): 4 files, ~187 LOC. CW prosign pause primitive in `MorseNotifier.cs` (so `73 SK <pause> EE` instead of `73 SKEE`); Squelch Level skip in arrow-nav when Squelch off (mirrors RIT/XIT pattern); window-level no-radio guard for universal Home keys (R/M/X/Q/V/=) in `MainWindow.xaml.cs` (so they speak no-radio guidance when no radio connected); Slice and Slice Operations purpose-naming ("Slice selector: slice A active" / "Slice operations: slice A controls"); Classic and Modern field-order parity; `MainContent` named to fix dialog-close "pane" announcement; `PlayCwSK` ee close fires on all three exit paths (re-verify revealed it WAS still broken — Noel's morning-of-04-28 partial retraction was based only on the with-radio path).

Both tracks pushed to origin/nromey on `sprint28/home-key-qsk`.

### Strategic + workflow memory captured today

- `project_main_branch_41_posture.md` — main = 4.1 working branch until three 4.2 ship gates clear (firmware push safety + crash reporter + update plan). Self-prunes when 4.2.0 ships. Pairs with the existing FlexLib 4.2.18 cascade memory.
- `project_independent_merge_events.md` — branch a fix from the version it's TESTABLE against, not the calendar-current major version. Diagnostic question: "what's the minimum baseline this fix can be tested against?"
- `feedback_open_questions_route_to_for_noel.md` — questions for Noel MUST be in `for-noel/`, never buried in active/working dashboards.
- `feedback_screen_reader_textual_markers.md` — NVDA/JAWS default to font-attribute-announcement OFF. Use `////text////` for strikethrough; textual prefixes (`**WARNING:**`, `**CRITICAL:**`) for emphasis; never visual-only formatting alone.
- `feedback_test_matrix_vs_guided_paper.md` — matrices are storage; for-noel test pulls are Noel's preferred execution format; guided walkthroughs are an alternate format for other testers (Don, Justin). Captured after two iterations of wrong-format delivery on the same task.

### Planning artifacts produced

- `docs/planning/active/sprint28-bug-bundle-triage.md` — full status of all 14 entries from 2026-04-28 briefing.
- `docs/planning/active/research-queue.md` — refreshed with completed tracks + new awaiting-Noel items.
- `docs/planning/for-noel/2026-05-04-sprint28-bug-bundle-questions-pull.md` — bug-bundle DESIGN-entries Q2 (RunsWithoutRadio + action-aware no-radio) still open; Q1 + Q3 auto-resolved.
- `docs/planning/for-noel/2026-05-04-42-release-execution-plan-pull.md` — five-phase execution sequence + Phase 0 runbook for Cloudflare R2 + DNS + rarbox. Five open questions; tomorrow afternoon's prep work (Azure signup + Cloudflare R2 enable) addresses some implicitly.
- `docs/planning/for-noel/2026-05-04-foundation-drop-test-pull.md` — Noel's test pull for today's two tracks. **PRIORITY for first session post-recovery.**
- `docs/planning/agile/pre-4.2-foundation-drop-test-matrix.md` — same tests in storage-matrix format for accumulation.
- `docs/planning/agile/pre-4.2-foundation-drop-guided-walkthrough.md` — same tests in guided-walkthrough format (alternate for Don/Justin).

### What's set up for tomorrow

- **Foundation drop is on sprint28/home-key-qsk and pushed.** Awaiting Noel's test results to propose the merge to main as the actual foundation drop event.
- **`track/flexlib-42`, `track/discovery-fallback-chain`, `track/braille-research`, `track/multi-radio` worktrees still parked** — they handle the 4.2.0 work and don't merge to main until firmware + crash reporter + updater are operational per `project_main_branch_41_posture.md`.
- **Bug-bundle DESIGN follow-up queued** — two entries (RunsWithoutRadio + action-aware no-radio) waiting on Noel's Q2 ACK in for-noel pull. When ACK'd, restart CLI session in fresh worktree on a new branch from sprint28/home-key-qsk for ~30-100 LOC.
- **Three days of priority pulls + execution plan await morning reading** when Noel's recovered. Test pull is the load-bearing one; execution plan is strategic context.

### Surgery context

Surgery 2026-05-05 8:30 AM Central, ~1hr procedure per `project_surgery_2026_05_05.md`. Possible light afternoon/evening capacity. Tomorrow afternoon Noel may do Cloudflare R2 + Azure prep work (Q2 + Section B from the 4.2.0 execution plan); that's optional, no pressure. Test pull for today's foundation drop is the priority when energy permits.

### Tomorrow's autonomous-pass: daily → nightly rename

Per `project_sprint29_updater_vision.md` (channel renamed 2026-05-03) and Noel's reminder tonight: the rename of `publish-daily-to-dropbox.ps1` (and its many internal "daily" references) to `publish-nightly-to-dropbox.ps1` is a focused-pass tomorrow item. ACK'd autonomous tonight. Surface includes:

- Script filename: `publish-daily-to-dropbox.ps1` → `publish-nightly-to-dropbox.ps1`
- Internal references in the script (~30 mentions of "daily" in comments, variable names, output strings, file naming patterns: `JJFlex_*_x64_daily.zip`, `NOTES-daily.txt`)
- Dropbox top-level filenames: change to `_nightly.zip` and `NOTES-nightly.txt`
- Sweep patterns: extend to handle BOTH `*_daily.*` (transitional) AND `*_nightly.*` so first post-rename run cleans up the lingering daily artifact
- CLAUDE.md references throughout the End-of-day workflow section
- Tonight's seal artifacts on Dropbox top level still use `daily` naming — they'll be swept on the first nightly-renamed run

### Rigmeter snapshot — end of 2026-05-04

**Grand totals (authored, excluding vendored FlexLib + PortAudio + Opus):**
- Files: 752 | Lines: 147,136 | Words: 689,903 | Chars: 6,587,608
- Per-category authored: code 102,696 lines; text_data 17,322 lines; docs 22,092 lines; build 5,026 lines
- Vendor totals: code 51,437 lines; text_data 1,211 lines; build 592 lines

**Today's git activity (since midnight local 2026-05-04):**
- Commits: 13 (JJ Flexbot)
- Unique files: 35
- Insertions: 4,014 | Deletions: 748 | Net: +3,266 lines

**Top languages by authored lines:**
- VB.NET: 17,343 (16.9% of authored code) — the primary app + globals
- Python: 3,027 (3.0% authored) — rigmeter + tools
- XAML: 4,985 (4.9% authored) — UI controls + dialogs
- C#: rest (FlexLib wrappers, JJFlexWpf, JJTrace, JJLogLib, JJPortaudio, P-Opus)

**Fun comparisons (authored only):**
- Braille volumes (≈100K cells each): 65.9
- Moby Dicks (~210K words): 3.3
- King James Bibles (~783K words): 0.88
- Read-aloud time at 150 wpm: 76.7 hours (4,599 minutes)
- Printed pages (50 lines/page): 2,943
- Stack-of-printed-pages height: 11.8 inches (29.9 cm)

**Docs-to-code ratio:** 0.28 (authored docs / authored code, by lines). Trending up day-over-day as the planning doc surface accumulates.

**Snapshot JSON:** `\\nas.macaw-jazz.ts.net\jjflex\historical\stats\2026-05-04-b055f84e.json`

**Trend across 10 historical snapshots:** doc base lines grew from 22,092 to current — most of today's +3,266 net is the planning doc burst (research-queue, triage, three for-noel pulls, two test-format docs, AAR). Code change today was the bug-bundle merge (~187 LOC) — net code growth is small; the day was design-density, not code-density.

## 2026-05-03 evening seal: nine-doc for-claude ingestion → seven memory entries; API.cs / HAAPI.cs ruled out of R4 investigation; SmartSDR ILSpy decompile authorized as next pivot

**No code on main today; ingestion + investigation day.** Spent the session processing the for-claude doc batch Noel promoted from for-noel during his evening review pass, plus an end-of-day FlexLib API.cs / HAAPI.cs source-diff that was the next-step in the R4 investigation per the existing memory. Seven memory entries landed (three new, four substantively updated); MEMORY.md index refreshed; nine for-claude source docs deleted per protocol; FlexLib 4.2.18 suspect surface narrowed by another pair of files. **Yesterday's seal is captured in commit `e3d877ed` and the 2026-05-02 AAR; that day was the design-backlog clearing day where five engineering tracks got build-now-ship-later authorization.**

### What changed

**Memory store — 3 new + 4 substantively updated:**

NEW:
- `project_smartsdr_decompile_authorization.md` — Noel's "stealing code" framing captured as a generalized authorization. Apply ILSpy/dnSpy on Flex vendor binaries when source diff is exhausted + a working reference implementation exists. Method comparison only, never code transplant. First application: the FlexLib 4.2.18 silent-discovery investigation + the Phase D firmware-update-flow research will inspect the same SmartSDR install for both surfaces.
- `project_brailleelement_jamie_handoff_boundary.md` — JJF builds the brailleElement primitive + JJF's own NVDA add-on. Jamie integrates into OSARA on his own. JJF doesn't author OSARA patches as a default move. Clean cross-AT contribution model that prevents JJF from inheriting OSARA maintenance burden.
- `project_blind_hams_net_helper_redesign.md` — Noel wants the bh-network helper flow (editor → queue → publisher → site) redesigned per friction-tax principle. "Super complicated, worth a re-do." Possibly DB-backed, possibly post-rarbox-migration. Treated as real initiative, not someday item.

UPDATED:
- `project_flexlib_4218_discovery_investigation.md` — added the SmartSDR ILSpy decompile pivot section (parallel investigation path, runs alongside the remaining internal-source-diff candidates). Then end-of-day appended the API.cs + HAAPI.cs diff result: both ruled out (API.cs byte-identical, HAAPI.cs delta is amplifier/metering only). Suspect ranking pivoted again — remaining internal candidates are `HighPriorityTaskScheduler.cs`, `MmcssPipelineScheduler.cs`, `VitaPacketBase.cs`, and the new Filter/NAVTEX/Waveform files.
- `project_sprint29_crash_reporter_vision.md` — full design pass ACK'd with concrete additions: crash + feedback share infrastructure but differ in trigger (crash = automatic + diagnostic-pack-required; feedback = manual + diagnostic-pack-default-on-but-optional); modal-on-next-launch batch pattern (queue crashes mid-session, prompt at next start); "whoopsie" Ctrl+F test command; URL-as-config in code (not user-editable JSON); the three tools (crash + feedback + updater) elevated to load-bearing for any 4.2.x release.
- `project_sprint29_updater_vision.md` — bundle-don't-slice (all 8 phases ship Sprint 29); channel naming RENAMED FROM "daily" TO "nightly" (compile happens overnight, matches GitHub convention for alpha-tier builds; broader rename of `publish-daily-to-dropbox.ps1` + CLAUDE.md references deferred to a focused pass); periodic launch-check OK if not within 2-hour window; silent install desired; Phase D blocked by R4 discovery problem until SmartSDR ILSpy resolves; CI/CD on roarbox → R2 architecture sketched; Phase H visible status bar at bottom-of-window (JAWS/NVDA can read); version cascade target = 4.2.0 (not 4.1.18).
- `project_multi_radio_capability_discovery.md` — added "Add a Radio" UX pattern under the existing Radios menu for surfacing non-Flex backends. Wizard flow: pick vendor + model from Hamlib list, configure connection params, save to per-radio config.
- `project_no_silent_keystrokes_rule.md` — empirical 2026-04-28 retest findings: Ctrl+Up/Down silent (different dispatch path than F3/F4 which work), single-letter R / Squelch silent in Home, Ctrl+F text-entry exception (dialog should still open with no radio so test/diagnostic input remains reachable). No code fixes have landed since 04-28, so the bugs are still open as of 05-03.

**Repo work:**

- LICENSE.txt copyright corrected from placeholder ("Othneil Drew") to Jim Shaffer + Noel Romey + contributors with vendored-third-party-retains-license note. Commit `c3ff88e1`.
- Doc routing snapshot commit `91dc93e7`: 8 for-noel docs promoted to for-claude/ (Noel's review act), 1 new claude-authored pull doc (firmware-install-strategy) staged in for-claude, 3 new priority pulls queued in for-noel/priority/ for tomorrow (CW keying over ethernet, DSP controls, TS-590 menu favorites).
- Memory ingest commit `c92cb145`: nine for-claude source docs deleted post-ingestion (substance now lives in memory entries). Keeps `91dc93e7..HEAD memory/` diffable as the audit trail of "what knowledge each doc contributed."
- New investigation memo at `docs/planning/active/don-flexlib-4218-discovery/api-haapi-diff.md` documenting the API.cs / HAAPI.cs source-diff result + recommended next steps.

### What's set up for tomorrow

- **Three priority pulls await reading**: CW keying over ethernet, DSP controls, TS-590 menu favorites. They're intact in `for-noel/priority/`. CW-keying-over-ethernet is especially worth reading alongside the Hamlib spike notes since the spike review surfaced an open question about CW keying support that referenced AetherSDR.
- **R4 next move:** 30-minute pass through `HighPriorityTaskScheduler.cs`, `MmcssPipelineScheduler.cs`, `VitaPacketBase.cs` in 4.2.18 worktree — these are the highest-suspicion remaining internal source-diff candidates. If clean, escalate to SmartSDR ILSpy decompile per the new authorization memory. Either path resolves the discovery question (success → Phase D firmware-update can proceed; failure → install-strategy concern in `project_firmware_install_dependency_strategy.md` becomes load-bearing).
- **Five engineering tracks still authorized for build-now-ship-later** from 2026-05-02: `track/multi-radio-foundation`, `track/tuning-unity`, `track/rit-xit-adjust`, `tools/brailleElement` v1, `track/stuck-modal-escape`. Plus today added: `track/crash-reporter` ("very load bearing now" per Noel's ACK).
- Memory + private docs snapshotted to NAS at `historical\memory\memory-20260503-201241.zip` and `historical\private\private-20260503-201242.zip`.
- Daily-debug promotion to Dropbox top level: SKIPPED (docs/memory-only day, no fresh debug build produced; per CLAUDE.md rule, prior day's daily still represents current code state).
- Rigmeter snapshot: SKIPPED (docs-only day, rigmeter values unchanged from yesterday per CLAUDE.md rule).

## 2026-05-01 evening seal: 1Password SSH agent stood up end-to-end across rarbox + andre + romeyserv; blind-hams-and-solar exploration brief drafted for next phase

**No code on main today; infrastructure + planning day.** Migrated Noel's SSH from passphraseless on-disk `nertop` key to 1Password vault-served keys, with per-host `~/.ssh/config` stanzas constraining each connection to its specific key (avoids the MaxAuthTries-vs-multi-key-agent footgun that bit during bootstrap). Three servers now reachable by short alias from PowerShell ssh AND SecureCRT, both routing through 1Password with biometric/PIN approval per app per unlock window. Wrote five new memory files (two feedback, two project, one reference) capturing today's lessons, plus a separate planning doc that briefs the next phase: a structured exploration of blind-hams-network + solar tooling currently scattered across his WSL + Andre's `3.onj.me` server, with an eye toward migrating the dynamic pieces to rarbox.

### What changed

**SSH stack — laptop side:**
- 1Password Developer setting "Use the SSH Agent" enabled (single toggle in current 1Password version, no separate system-wide-pipe toggle to flip).
- Critical lesson surfaced: 1Password reports "running" badge but the `\\.\pipe\openssh-ssh-agent` named pipe doesn't actually bind until 1Password is fully QUIT from system tray and relaunched. Closing the window isn't enough — pipe binds at process startup of the long-lived tray process.
- Windows OpenSSH `ssh-agent` service set to **Disabled** (so 1Password owns the standard pipe; otherwise they'd race).
- `VANDYKE_SSH_AUTH_SOCK = \\.\pipe\openssh-ssh-agent` env var set User-scope so SecureCRT delegates agent ops to the 1Password pipe.
- SecureCRT Global Options → SSH2: identity file field cleared (the hint text "(when blank, keys in the SSH Agent are tried)" is the agent fallback).
- Two new ed25519 keys generated in 1Password vault: `nertop-1pw` (Noel's main client identity, used on rarbox + romeyserv) and `andre-1pw` (used only on 3.onj.me — the "exercise" key from when we walked through one-key-per-server compartmentalization before realizing it's enterprise overkill for personal infra).
- Five orphan SSH keys (old 1Password experiments that never got working) deleted from vault. Agent now serves exactly one key per connection request.
- `~/.ssh/config` populated with three host stanzas (andre, rarbox, romeyserv) each pinning `IdentityFile` + `IdentitiesOnly yes` to constrain offerings.
- Two pubkey files on disk: `C:\Users\nrome\.ssh\nertop-1pw.pub` and `andre-1pw.pub`. These are the indirection layer ssh uses to identify which agent-served key to ask for; private halves never touch disk.

**SSH stack — server side:**
- rarbox (`rarbox.macaw-jazz.ts.net`): `nertop-1pw.pub` appended to `~ner/.ssh/authorized_keys`.
- 3.onj.me as `ner` (Andre's server): `andre-1pw.pub` appended to `~ner/.ssh/authorized_keys`.
- `nas.macaw-jazz.ts.net` as `ner` (romeyserv/NAS): `nertop-1pw.pub` appended to `~ner/.ssh/authorized_keys`.
- Old `nrome@nertop` pubkey (the on-disk passphraseless key) is still authorized on all three. Soak-week plan: retire after confidence builds; scheduled remote agent set up to draft a retirement checklist 2026-05-08 (see below).

**Repo work:**
- New planning doc: `docs/planning/blind-hams-and-solar-exploration/README.md`. Captures the next phase brief — Noel's blind-hams-network site (Jekyll, repo `nromey/bh-network`, hosted at `data.blindhams.network` via Andre's `3.onj.me`), the solar tooling (Python wrapping IRI Fortran, generates reports an ElevenLabs voice reads on AllStar), the long-standing vision for a location-aware accessible propagation site (which doesn't currently exist anywhere — PSK-reporter's map is image-only and unreadable to screen readers), and a structured exploration plan with explicit deliverables (inventory.md, categorization.md, migration-plan.md, questions-for-noel.md, risks.md). Pre-decision: hybrid execution path — autonomous overnight subagent for mechanical inventory, interactive morning session for migration-plan judgment.

### Memory updates today (5 new + index)

- **feedback** `feedback_read_hardening_memory_first.md` — when a host has a `project_*_hardening.md` memory entry, READ it before issuing commands. Counters today's miss where I used the public IP for rarbox despite the Tailscale-only lockdown being in both the most recent commit message AND the rarbox hardening memory.
- **feedback** `feedback_match_threat_model_not_textbook.md` — ask threat model before recommending compartmentalization; for personal infra, one key per laptop is usually enough. Counters today's over-architecting of the one-key-per-server pattern.
- **project** `project_1password_ssh_setup.md` — full live state of the 1Password SSH setup as of 2026-05-01. Two keys, three host stanzas, env vars, gotchas, and the not-yet-done rarbox-as-server-outbound work referencing the existing `rarbox-key-setup.txt`.
- **reference** `reference_1password_securecrt_setup_windows.md` — replicate-on-new-machine quick reference. Env var, SecureCRT settings, 1Password developer settings, the tray-quit-relaunch gotcha, the MaxAuthTries pattern, the bootstrap command shape with `IdentitiesOnly=yes` + `IdentityAgent=none` + `StrictHostKeyChecking=accept-new` flags.
- **project** `project_blind_hams_solar_context.md` — strategic context for the blind hams + solar work. The "no other accessible propagation site exists" framing is load-bearing for prioritization decisions in the upcoming exploration phase.
- MEMORY.md index updated with all five entries under appropriate sections (External Resources for the project + reference 1Password entries; Development Practices for the two feedback entries).

### Scheduled remote agent

- Created routine `trig_01PXBVXN45AzoScPg7qCdEk1` ("On-disk nertop SSH key — soak checkpoint") via `/schedule`. Fires once at 2026-05-08T13:00:00Z (Friday, 8am Central) on the standard env. Job: open a PR with `nertop-retirement-checklist.md` containing pre-flight verification commands, local file deletion commands, per-server pubkey cleanup steps, and rollback plan. Self-contained prompt (remote agent has no local access — can't ssh, can't read memory). Noel reviews PR + executes manually.

### What's set up for tomorrow

- Sleep on the blind-hams-and-solar brief at `docs/planning/blind-hams-and-solar-exploration/README.md`.
- Re-read in the morning fresh; if it captures intent, kick off Path 3 hybrid (autonomous mechanical inventory subagent + interactive afternoon migration plan).
- Decision pending: whether to install Claude Code in WSL or just shell into WSL via `wsl <command>` for the inventory phase. Brief recommends Option A for this exploration; defer the WSL install to a separate session.

## 2026-04-30 evening seal: Don's 4.2.18 silent-discovery bug narrowed across four diagnostic rounds; FLEX-8600 unbox trigger fired; firmware-update work unblocked

**Single-thread investigation day.** All session work centered on Don's report that JJFlex's `track/flexlib-42` build cannot discover his 6300 on the local LAN. Through four diagnostic build rounds (R1 → R4), each shipping to Don's Dropbox folder with screen-reader-friendly NOTES, the suspect list collapsed from "external environment / firewall / firmware" to a single specific code-level difference: the cancellation-token-aware `UdpClient.ReceiveAsync(token)` variant introduced in FlexLib 4.2.18's Discovery.cs. R4 is in Don's folder waiting for tomorrow morning's run; Noel is bringing up the FLEX-8600 with new firmware tonight as a parallel test surface on his own machine. No code committed today on main; all diagnostic instrumentation lives uncommitted in `track/flexlib-42`'s working tree, which is the deliberate posture for throwaway diagnostics.

### What happened across the day, sequentially

- **R1 (early morning):** First diagnostic build added one trace line before VITA filtering on Don's existing R2-base. Outcome: ambiguous — no `rx pkt` lines in trace could mean "right build, zero packets" OR "stale build, no instrumentation." Lesson surfaced and saved as feedback memory: *always include a non-conditional build-marker line in diagnostic instrumentation*.
- **R2 (mid-morning):** Added unconditional build marker, NIC enumeration, and bind confirmation. Don's R2 trace was decisive on three points: build verified (marker present 3x), single Ethernet adapter at 192.168.1.21/24 (no virtual junk — confirms clean environment Noel had described), bind succeeds on UDP/4992. But still zero `rx pkt` lines.
- **Firewall hypothesis (mid-day):** Proposed Windows Firewall path-scoped inbound block, since the test build runs from `C:\Users\Don\jjf-test\` (different exe path than installed JJFlex). NOTES round 3 sent Don an `Allow-an-app` PowerShell one-liner. Don ran it. Trace still showed zero packets. Hypothesis falsified — at least in the form proposed.
- **R3 (afternoon):** Added a pre-loop SelfTest sending three probes (loopback, NIC-self, limited broadcast) to the bound socket. Don's R3 trace showed all three probes received successfully on every cycle. Socket is fully healthy. Failure has to be either upstream of the socket OR in how the main async receive loop reads from it.
- **R4 (evening):** Added a 5-second synchronous receive drain after SelfTest, before the main async loop takes over. R4 deployed to Don's Dropbox folder; Don QSY'd to phone for the night, will run R4 tomorrow morning.

### Memory updates today (2 new + 1 rewritten)

- **New:** `feedback_tester_facing_language.md` — for tester-facing artifacts (NOTES files, in-app messages, error dialogs), use ham-operator vocabulary, NOT programmer vocabulary. Allow-list (socket, port, broadcast, firewall, PowerShell, OUI with brief context). Avoid-list (async, await, tokens, .NET version internals, exception type names, code-path-level abstractions). Use radio analogies where they help. Indexed in MEMORY.md under Development Practices.
- **New (saved earlier this session):** none — the language memory was the primary feedback-rule memory of the day.
- **Rewritten:** `project_8600_unbox_firmware_trigger.md` — TRIGGER MET. New firmware dropped on/around 2026-04-30. The 8600's reservation is over. Firmware-upload mechanics work is now unblocked sprint scope; Phase 10-11 positive NR provider tests on >6300-class hardware can run; Sprint 28's deferred 3+slice release-all-extras test can be run on the 8600. Original gating note flipped from "do NOT suggest unboxing" to "in active play."

### FlexLib 4.2.18 diff sweep results (autonomous parallel work)

While waiting for Don's R4 trace, ran a complete diff across FlexLib_API/*.cs between main and `track/flexlib-42`. Findings:

- **UDP / discovery / broadcast / multicast usage in FlexLib 4.2.18 lives in EXACTLY ONE FILE: `FlexLib/Discovery.cs`.** No other source file binds to UDP, joins multicast, or touches broadcast. This dramatically narrows suspect surface.
- **`FlexLib/API.cs` is byte-identical** between main and 4.2.18. `API.Init()` flow has no functional changes that could affect socket state.
- **`Vita/VitaPacketPreamble.cs`** modernized (BinaryPrimitives.ReadUInt32BigEndian replaces BitConverter+ByteOrder.SwapBytes) but semantically identical — same fields parsed from same byte positions.
- **`Vita/VitaDiscoveryPacket`** got refactored to inherit from new `Vita/VitaPacketBase.cs`, but parsing semantics preserved; AND this code runs *after* the OUI/class filter, so it's downstream of where Don's silent failure happens.
- **`FLEX_OUI = 0x1C2D` and `SL_VITA_DISCOVERY_CLASS = 0xFFFF`** — byte-identical between versions. Filter constants unchanged.
- **`Vita/VitaSocket.cs` is only used by `Radio.cs`** (post-discovery command/data connection). Discovery uses raw `UdpClient`, not VitaSocket.

**Conclusion of the diff sweep:** the bug must be in one of *exactly three places* — the cancellation-token-aware `ReceiveAsync(token)` async machinery, some subtle .NET 10 / Windows runtime interaction with the bound socket, or our race-fix patch (already exonerated by R3's self-test success). R4 disambiguates the first two.

### Fix shape, conditional on R4 outcome

- **R4 outcome A (sync drain receives radio packets):** The fix is one line in our wrapper — replace the `#if NET6_0_OR_GREATER` token-aware `await udp.ReceiveAsync(token)` with the no-token `await udp.ReceiveAsync()` (which is the 4.0.1 pattern that already works). Plus a MIGRATION.md note so future FlexLib upgrades don't re-introduce the token variant. ~15-line patch total.
- **R4 outcome B (sync drain also sees nothing):** Hypothesis space tightens to .NET 10 runtime / Windows kernel UDP delivery interaction. Next diagnostic is Wireshark from outside the JJFlex process to confirm whether radio broadcasts even reach the NIC. The 8600 bringup on Noel's machine becomes the highest-priority parallel test for this branch.

Discussed but explicitly DEFERRED: firmware-version-gated discovery code paths. The chicken-and-egg problem (you don't know firmware until after discovery succeeds) means firmware-gating belongs in *post-discovery* code paths (where `radio.Version` is known), not in discovery itself. Discovery should always use whichever receive mechanism is most robust. Per `project_chained_updater_pattern.md`, post-discovery firmware-gating composes cleanly with the chained-updater pattern.

### Side benefits opening from the 8600 trigger firing

Listed in the rewritten `project_8600_unbox_firmware_trigger.md`. Highlights:
- **Firmware-upload mechanics work in JJFlex** is now unblocked sprint scope. Per the original gating spec, it pairs with the unbox event. Sprint 29 firmware-updater scope (`project_sprint29_updater_vision.md`) and the chained-updater pattern (`project_chained_updater_pattern.md`) now have a real test surface end-to-end.
- **Phase 10-11 positive NR provider tests** on >6300-class hardware are unblocked.
- **Sprint 28 release-all-extras with 3+ slices** (deferred 2026-04-24) is now runnable on the 8600 (4 slices).
- **The "older firmware vs. new firmware" axis** of the discovery investigation just gained a real test surface — worth flagging because if the 8600 with new firmware discovers fine while Don's 6300 (probably older firmware) doesn't, the firmware-version-mismatch hypothesis we'd written off may revive in a different form.

### State of artifacts in Don's Dropbox folder (end of day)

`C:\Users\nrome\Dropbox\JJFlexRadio\don\` contains exactly three files:
- `JJFlex_v4.2.18-test_x64_debug.zip` — R4 build (active diagnostic; same filename as R3 was, R3 contents replaced)
- `JJFlex_v4.2.18-test_NOTES.txt` — round-5 instructions in ham-operator language (no async/await jargon; uses "two ways to monitor a frequency" analogy)
- `trace.zip` — Don's R3 trace + auto-connect XML (his last submission, archived to inbox/round4-r3-test/)

Five obsolete files (two large 187+182 MB crash zips from 2026-04-16, one earlier R2 trace zip, one earlier R2 trace txt, one old NOTES copy) cleared with Noel's permission. Recovered ~370 MB.

Investigation paper trail in `docs/planning/inbox/don-trace-418-discovery/`:
- All four diagnostic build zips archived (R1 → R2 → R3 → R4)
- All Don's traces archived per round (`round2/`, `round3-firewall-test/`, `round4-r3-test/`)
- Two extracted XML configs for cross-reference
- Original NOTES file from R1

### Plan for next session (5/1 morning, or tonight if Noel does the 8600 bringup)

1. **Read 8600 R4 trace from Noel's machine** if he ran the bringup test tonight. Outcome shapes whether we proceed with the receive-mechanism fix or pivot to firmware-version investigation.
2. **Read Don's R4 trace** when he runs it tomorrow morning. Outcome A → ship the one-line fix as R5 with proper commit on `track/flexlib-42`. Outcome B → pivot to Wireshark capture or .NET runtime investigation.
3. **If R4 confirms the fix, write the patch commit** with an updated MIGRATION.md note about the receive-mechanism choice. Patch goes on `track/flexlib-42` and unblocks the 4.2.0.x release path.
4. **AAR for today** lives in `JJFlex-private\after-action-reports\2026\04\2026-04-30.md`, capturing the cross-surface activity that the main-repo's zero-commit day undercounts.

### Rigmeter snapshot — end of 2026-04-30

**Skipping the full grand-totals dump per the docs-only-day rule** — no commits today on main or any active worktree. All diagnostic build instrumentation lives uncommitted in `track/flexlib-42` working tree (intentional throwaway pattern). `rigmeter today` reports 0 commits, 0 lines.

**NAS snapshot:** `\\nas.macaw-jazz.ts.net\jjflex\historical\stats\2026-04-29-425e0ef8.json` — note that the snapshot is anchored to yesterday's seal commit because no commit landed today. This is correct behavior; an AAR-style human-readable record (not rigmeter-visible) is the place where today's investigation work gets captured. 10 historical snapshots tracked.

**Cross-surface activity rigmeter doesn't see:**
- `track/flexlib-42` working tree: ~150 lines added in `Discovery.cs` (R1 → R2 → R3 → R4 instrumentation, four edit cycles, all uncommitted on purpose)
- 4 debug builds produced and archived (R1, R2, R3, R4 zips, ~7 MB each in inbox)
- 4 NOTES file rewrites in Don's Dropbox folder (final one in ham-operator-friendly language)
- 2 memory file changes (`feedback_tester_facing_language.md` new, `project_8600_unbox_firmware_trigger.md` rewritten)
- ~10 Don-trace archive operations to investigation paper trail
- Complete diff sweep across 113 FlexLib source files (output saved to investigation context)

This is exactly the kind of day the AAR convention exists to capture. The main-repo rigmeter's "0 commits, 0 lines" reading is technically correct and substantively misleading.

## 2026-04-30 late-night seal: rarbox provisioned + hardened + Tailscale tailnet-only SSH lockdown (parallel session)

**External-infrastructure stream of today's work.** Ran in parallel to the FlexLib 4.2.18 / Don / 8600 stream above. Stood up Noel's first JJFlex-orbit Hetzner box, hardened it to "production cloud baseline" status, joined it to the `macaw-jazz.ts.net` tailnet, and closed public port 22 entirely so SSH is reachable only over the tailnet. Zero JJFlex code touched; all activity was on rarbox + Tailscale + 1Password + memory files.

### What got built on rarbox (Hetzner Cloud Debian 13 trixie)

- **VPS at 178.156.204.128**, hostname `rarbox`, kernel 6.12.85, fully apt-upgraded.
- **Account model**: `root` SSH disabled and password-locked from Hetzner image. `ner` (uid 1000, sudo group) created with NOPASSWD via `/etc/sudoers.d/ner`. Both root and ner now password-locked (`!` in /etc/shadow); SSH key auth is the only path in.
- **Drop-in config files everywhere** (vendor configs untouched, survives `apt upgrade`):
  - `/etc/ssh/sshd_config.d/50-hardening.conf` — PermitRootLogin no, PasswordAuthentication no, KbdInteractiveAuthentication no, X11Forwarding no, MaxAuthTries 3, ClientAlive timers
  - `/etc/sudoers.d/ner` — NOPASSWD: ALL (mode 0440, `visudo -c` validated)
  - `/etc/fail2ban/jail.local` — sshd jail, **`backend = systemd`** (Debian 13 logs to journald only — common gotcha)
  - `/etc/apt/apt.conf.d/52unattended-upgrades-local` + `20auto-upgrades` — auto-apply security patches, no auto-reboot, daily timer at ~06:10 CDT
- **UFW final state**: 80/tcp + 443/tcp public (for future nginx); 22/tcp **only on tailscale0 interface**; default-deny incoming. Public port 22 silently dropped. fail2ban already banned 2 IPs (Tanzania, Russia) within 3 minutes of the box going live — real attacks, not theory.
- **Tailscale 1.96.4** installed via official apt repo, joined the tailnet, tailnet IP `100.68.207.12`, MagicDNS confirmed working at `rarbox.macaw-jazz.ts.net`.

### Cross-machine secret hygiene cleanup

Found seven SSH key files in `C:\Users\nrome\OneDrive\Documents\` (private + public halves of multiple keys, scattered there by SecureCRT's default-save behavior). Moved all to `C:\Users\nrome\.ssh\` with CRLF stripped (SecureCRT's OpenSSH-format export on Windows writes CRLF, breaking OpenSSL's parser — `error in libcrypto`). OneDrive Documents now has zero key files; canonical keystore is `~/.ssh/`. Saved as `feedback_securecrt_crlf_keys.md` so future sessions diagnose this 5-minute confusion in 30 seconds.

### Tailscale DNS surprise diagnosed

After Noel toggled MagicDNS in admin, laptop's NRPT entry for `*.macaw-jazz.ts.net` glitched — `nslookup nas.macaw-jazz.ts.net` failed, SMB to `\\nas.macaw-jazz.ts.net` failed, looked like the NAS had become unreachable. Diagnostic battery (ping by tailnet IP works → network healthy; `Resolve-DnsName` works but `nslookup` doesn't → NRPT-vs-nslookup mismatch; full NRPT dump → forward rule for `.macaw-jazz.ts.net` was actually present, my truncated view had hidden it). Real fix was `tailscale down && tailscale up` to force NRPT re-install; the underlying lesson is that **`nslookup` ignores NRPT on Windows, while every real application respects it** — `Resolve-DnsName` is the trustworthy diagnostic. Operational lore captured in `project_rarbox_hardening.md`.

### 1Password architecture decisions

- Noel chose **single-item layout** for rarbox secrets (consolidate everything into one Hetzner Server item; not three separate Login/Server/SSH-Key items as initially pitched). Memory updated to record this choice so future sessions don't re-pitch the three-item layout.
- **rarbox SSH key item** also created in 1Password — agent-migration-ready. Private key pasted in, passphrase entered once during import (1Password decrypted and stored; passphrase is now operational-cleanup-only, never needed again for that key once 1Password agent is enabled).
- **Migration path to 1Password SSH agent** scoped: enable Settings → Developer → Use the SSH agent + per-session config in SecureCRT; eventually delete on-disk private keys entirely. Not done tonight; queued for tomorrow.

### Memory updates today (this session — 2 new + 1 updated)

- **New:** `project_rarbox_hardening.md` — full rarbox config snapshot, account model, drop-in file pattern, NOPASSWD rationale, UFW final state, Tailscale config, 1Password layout choice, DNS troubleshooting toolchain (operational lore), open follow-ups list.
- **New:** `feedback_securecrt_crlf_keys.md` — `error in libcrypto` symptom = CRLF line endings in OpenSSH-format key from SecureCRT on Windows. `tr -d '\r'` is the fix; `~/.ssh/` is the canonical keystore (not OneDrive).
- **Updated:** `MEMORY.md` — index entries for both new memories under External Resources / Development Practices.

### Scheduled follow-up agent

`trig_01SCvctNhNV5Eb9EM6Ts19SL` — fires once on **2026-05-14T14:00:00Z (Thursday May 14, 9:00am Memphis CDT)** to surface the open todos. View at https://claude.ai/code/routines/trig_01SCvctNhNV5Eb9EM6Ts19SL. The most-important pending action it'll re-surface is **disabling Tailscale key expiry on rarbox in admin** — without it, rarbox auto-disconnects from the tailnet ~6 months after auth.

### Plan for next session (5/1 morning)

1. **FlexLib stream**: read Don's R4 trace when he runs it tomorrow morning. Outcome shapes whether the receive-mechanism fix is one-line or whether we pivot to .NET 10 / Wireshark investigation. (Per the parallel-session entry above.)
2. **rarbox stream**: run `~/.ssh/rarbox-key-setup.txt` commands inside an SSH session to rarbox (generate `~/.ssh/rarbox` Ed25519 keypair with passphrase, ssh-agent auto-start in bashrc, ssh-copy-id to NAS, test).
3. **Tailscale admin** (one-click action): tailscale.com/admin/machines → rarbox → ... menu → Disable key expiry. Most important small todo from today's setup.
4. **SecureCRT + 1Password agent setup** (when ready): enable agent in 1Password Settings → Developer; per-session SSH2 → Authentication settings in SecureCRT to use external Pageant.
5. **Save SSH key passphrase** (when generating tomorrow) to 1Password Hetzner Server item as a Concealed field.

### Rigmeter snapshot — end of 2026-04-30 (this session)

**Skipping per docs/infra-only-day rule** (consistent with the parallel FlexLib-investigation entry's reasoning). Zero JJFlex code lines authored this session. All my output landed in: rarbox config files (live on the server, not the repo), 2 new memory files, 1 MEMORY.md index update, this Agent.md entry, the upcoming AAR. Cross-surface activity that rigmeter doesn't see:
- ~12 config files written/edited on rarbox (sshd_config.d, sudoers.d, jail.local, apt.conf.d, ufw rules)
- Hetzner VPS provisioned + hardened end-to-end
- Tailscale 1.96.4 installed + authenticated + tailnet route validated
- 7 SSH key files migrated out of OneDrive to `~/.ssh/`
- 1Password Hetzner Server item populated; rarbox SSH Key item created
- 1 cloud-scheduled agent armed for 2026-05-14
- 2 NAS backup snapshots fired (memory + private docs)

The AAR captures cross-surface activity from BOTH today's sessions (FlexLib + rarbox).

## 2026-04-29 evening seal: AAR convention + three handoffs landed (FlexLib 4.2.18 / multi-radio / braille) + rarbox + Netlify + Cloudflare R2 game plan locked + firmware extraction recipe verified

**Strategic infrastructure day.** Three large research arcs reached handoff on parallel branches; the evening session sealed with the rarbox + Netlify game plan that converts the JJ Flexible Data Provider from "Andre's server interim" to a two-tier hosting story Noel controls end-to-end. No production code shipped on main today by design — the day's value lives in parked branches, locked-in decisions, and one infrastructure firm-up.

### What landed across the four worktrees today

- **`sprint28/home-key-qsk` (main repo)** — 1 commit (`5d746a2e` evening handoff doc by Track B Claude: rarbox + Netlify game plan + parallel-track posture). This session added CLAUDE.md AAR step + `.gitignore` vendor-staging guards (ride along on tonight's seal commit).
- **`track/flexlib-42`** — 12 commits, all 8 phases complete. FlexLib v4.0.1 → v4.2.18 upgrade tested, audio-confirmed live on Don's 6300 over SmartLink (~600 opus packets in 30s post-fix vs 2 pre-fix). Phase 5 firmware-floor gate stays in permanently. Branch parked at `7aa93e47` pending merge prerequisites (foundation work + firmware-update UI on main first; 4.2.0.x replaces 4.1.17 as next public release). Memory: `project_flexlib_4218_merge_sequencing.md` (SHA refreshed tonight).
- **`track/multi-radio`** — 11 commits, 10 research phase deliverables (~5,500 lines of docs, zero production code). IRadioBackend interface design, radio class taxonomy, Hamlib API survey, per-radio config strategy, audio routing for non-Flex, TS-2000 conformance scope, tester onboarding paths, architecture synthesis. Branch parked. Decision pending from Noel: greenlight Sprint Multi-1 (low-risk refactor introducing the abstraction layer with FlexLibBackend wrapping today's `theRadio`).
- **`track/braille-research`** — 7 commits, 6 phase deliverables. NVDA + JAWS surveys, OSARA prior art, cross-AT primitive design (5-method API: open/update/patch/dismiss/pan), NVDA prototype skeleton, handoff with Noel's interleaved annotations. Noel's locked-in answers captured in `project_braille_primitive_v1_decisions.md`: name `brailleElement`, standalone repo from day one, NVDA add-on first then Jamie outreach, opt-in cursor indicator in v1.

### Infrastructure decisions firmed up tonight

- **Two-tier hosting decided.** Netlify Pro for static CDN (`jjflexible.radio` marketing site); **Cloudflare R2** for `data.jjflexible.radio` (firmware blobs + manifest); Hetzner US "rarbox" for dynamic services + bh-network `data.` migration target. R2 wins for binaries because of zero egress fees and built-in global CDN distribution; Netlify ToS explicitly discourages binary distribution. New memory: `project_data_provider_hosting.md`.
- **Microsoft Trusted Signing adopted** as canonical signing infrastructure (firmware manifests, future installers, all signed JJF artifacts). Memory: `project_microsoft_trusted_signing.md`.
- **App-updater + firmware-updater interlock pattern adopted.** When any update advertises `minClientVersion` higher than running client, chain through app updater first, single combined-consent prompt. Memory: `project_chained_updater_pattern.md`.
- **`nromey/jjf-data` private GitHub repo created** (Noel, evening 2026-04-29) as source-of-truth for the data-provider. Note actual repo name is `jjf-data`, not the `jjflex-data-provider` placeholder some earlier handoff docs used. Memory: `project_jjf_data_repo.md`.
- **DNS for `jjflexible.radio` moves to Cloudflare** (decided tonight) — registration stays at the original registrar (Cloudflare doesn't sell `.radio`), but DNS management consolidates with blindhams.network and Noel's other domain under one Cloudflare account. Steps captured in the data-provider hosting memo.
- **JJ Flex Mac on the roadmap** as a future native SwiftUI port — preserves the .NET accessibility moat by going native-per-platform rather than UI-framework migration. Memory: `project_jjflex_mac_planned.md`.

### Firmware extraction recipe verified (late evening)

While exploring whether to install SmartSDR or extract firmware from the MSI offline, queried the MSI's File table directly via the WindowsInstaller COM object. Conclusion: firmware ships as plain `.ssdr` files in the MSI's File table at `[CommonAppDataFolder]\FlexRadio, Inc.\SmartSDR\Updates\` — NOT embedded in the .NET single-file exes (SmartSDR/DAX/CAT, ~600 MB combined of bundled .NET app code). Two firmware files per release: `FLEX-6x00_v<ver>.ssdr` (61 MB) covers all 6000-series radios; `FLEX-9600_v<ver>.ssdr` (368 MB) covers 9600/8000-series and Aurora. **Two-line extraction recipe** (`msiexec /a` + file copy, no third-party tools) now documented in `project_firmware_distribution_decision.md`. The JJ Flexible Data Provider extraction script is genuinely small.

### AAR convention introduced

End-of-day cross-surface synthesis convention added: write to `JJFlex-private\after-action-reports\YYYY\MM\YYYY-MM-DD.md` (private, not in public repo — names testers by personal context, references internal sequencing, would leak through `nromey/JJFlex-NG`). CLAUDE.md step 4b added; memory `project_after_action_reports.md` documents the rationale (rigmeter is branch-scoped, undercounts heavy-research days like today by ~100x). First instance: tonight's AAR.

### Memory updates today (8 new + 2 updates)

New: `project_after_action_reports.md`, `patrick_bh_network_tester.md`, `project_jjflex_mac_planned.md`, `project_jjf_data_repo.md`, `project_data_provider_hosting.md`, `project_microsoft_trusted_signing.md`, `project_chained_updater_pattern.md`, `project_firmware_distribution_decision.md`. Updates: `project_flexlib_4218_merge_sequencing.md` (SHA refresh from `023a41d4` to `7aa93e47`), `project_jjflex_data_provider.md` referenced from new hosting memo. MEMORY.md index updated.

### Rigmeter snapshot — end of 2026-04-29

**Skipping the full grand-totals dump per the docs-only-day rule.** Main-branch rigmeter today shows just 1 commit, 116 lines on `sprint28/home-key-qsk` (claude b's evening handoff doc). Tonight's seal commit will add a few more (CLAUDE.md AAR step, .gitignore vendor-staging guards, this Agent.md update).

**Cross-branch reality** (the strongest argument for the AAR convention — rigmeter doesn't see this):
- `track/flexlib-42`: 12 commits, several thousand lines of design + handoff docs.
- `track/multi-radio`: 11 commits, ~5,500 lines of research docs.
- `track/braille-research`: 7 commits, ~1,500 lines of design + ~250-line Python prototype.
- main repo: 1 commit pre-seal + tonight's seal commit.

**NAS snapshot:** `\\nas.macaw-jazz.ts.net\jjflex\historical\stats\2026-04-29-5d746a2e.json` (tokei warning harmless; fall-back counter ran). 9 historical snapshots tracked.

### Plan for next session (4/30 morning)

Track-shaped per claude b's handoff doc; the four can run in parallel in independent CLI sessions:

1. **Track A — Radio testing** on whatever foundation-phase items are next on the 4.1.17 test matrix. Noel + main-repo Claude. No prerequisite blockers.
2. **Track B — Data-provider site setup.** Prereqs: (a) transfer `jjflexible.radio` DNS to existing Cloudflare account (Noel may ask Claude to walk through nameserver flip); (b) enable R2 (one-time CC bump); (c) create `jjf-data` R2 bucket; (d) point `data.jjflexible.radio` at it; (e) generate R2 API creds + store as `nromey/jjf-data` repo secret. After that, a Claude session populates folder structure, manifest schema, GitHub Action for R2 sync, and the Windows extract+publish PowerShell script.
3. **Track C — rarbox hardening + bh `data.` migration.** Prereqs: Noel provisions Hetzner CX22 in US region with SSH key, captures IP, confirms SSH alive. Then a Claude session does SSH config tightening, ufw, fail2ban, unattended-upgrades, Caddy, Claude Code on the box, then plans the bh migration.
4. **Track D — bh-network reading + stale GitHub Action fix.** Read `nromey/bh-network` end-to-end, document conventions for Track B to mirror, fix the 2-month-old Action complaint, investigate the 502.

### What was deliberately NOT done tonight

- **R2 setup steps** — gated on Noel's CC-on-file action and bucket creation; tomorrow.
- **DNS nameserver flip for `jjflexible.radio`** — Noel does this when ready; Claude ready to walk through if asked.
- **The extract+publish PowerShell script** — gated on `jjf-data` repo layout decisions tomorrow.
- **Greenlight on Sprint Multi-1** — research handed off, decision queued for Noel.
- **Braille primitive implementation** — research-only this sprint cycle; production work post-foundation.

## 2026-04-28 evening seal: Morning sweep + FlexLib 4.2.18 backlog flag + Help-Escape Win32 hook + Track B hamlib research underway

**Two-track day with a wrap pivot.** Morning was Noel-driven 4.1.17 cleanup (the morning sweep). Evening was a dual-CLI session: a Track B autonomous run starting the hamlib research, plus a foreground Track A session that picked up the Help-dialog Escape bug. JJ Radio user base folding into JJ Flexible became the strategic frame mid-session — multi-radio commitment is now inherited, not optional. Discussion of what that changes deferred to 4/29 morning; today wraps the in-flight code work.

### Code shipped today (3 commits + this seal):

- **`64eac28e` morning sweep (Noel-driven):** C.4i regression fix + KEY CONFLICT one-liner (Ctrl+F duplicate-binding cleanup, Sprint 15+ era) + Disconnect SK notification.
- **`1419bd0b`:** TODO entry logging FlexLib 4.2.18 upgrade as upcoming major work. SmartSDR + FlexLib + ssdr unpack dirs left untracked at repo root for inspection during the upgrade work.
- **`JJFlexWpf/HelpLauncher.cs` (this session, evening):** Low-level Win32 keyboard hook closes the CHM viewer (`hh.exe`) on Escape. Honors `project_dialog_escape_rule.md` for the F1 help path. Filter is foreground-window class name (`HH Parent`); fail-safe — if `SetWindowsHookEx` fails, behavior degrades to today's Alt+F4-only.

### Architectural finding from the Help-Escape work:

The TODO entry's suspected root cause ("dialog uses ShowDialog without Escape handler") was wrong. F1 → `HelpLauncher.ShowHelp()` → `System.Windows.Forms.Help.ShowHelp()` → `hh.exe`, a separate-process OS-owned window. No JJFlex XAML or code-behind exists to add a key handler to. Three options surfaced: (A) Win32 hook the CHM viewer, (B) replace CHM with a WPF/WebView2 help viewer (would require building our own search/TOC/index — non-trivial; CHM gives all of that for free), (C) document Alt+F4 and defer. Picked A as the right 4.1.17-scoped answer; B logged as Sprint 29+ candidate. Decision rationale: CHM's built-in search is a real feature the user values, and the WebView2 path means rebuilding it.

### Strategic frame — JJ Radio folding into JJ Flexible (new memory, end-of-day):

`project_jj_radio_folding.md` captures: JJ Radio's user base migrates into JJ Flexible. The commitment to support whatever rigs they operate is inherited, not optional. Hamlib graduates from "future plan" to "downstream commitment" — Track B's research is no longer speculative architecture. Worth noting: this also reframes the .NET/UIA accessibility-moat memo from "preserve" to "load-bearing for an even larger taxonomy of radios." A cross-platform UI rewrite would now break two user bases mid-transition. Heavier veto.

### Track B status (parallel CLI session, separate context):

Hamlib research started today, output will land at `docs/planning/track-b/`. Already there: `at-scripting-research.md` (NVDA/JAWS scripting survey, Sprint 30+ input). Track B owns its own commits and files; Track A (this session) didn't touch them. Coordination is "two sessions doing unrelated work in the same repo" — closer to Sprint 27 serial pattern than to a parallel sprint with merge ordering.

### Plan for next session (4/29 morning):

1. **Test the Help-Escape hook** — F1 + Escape (expect: CHM closes), Help menu items (each entry point), CHM Find dialog (should not be swallowed because its window class is different), negative cases (Escape outside CHM still does its normal job).
2. **Multi-radio architecture discussion** — implications of JJ Radio folding for the Sprint 30+ radio-abstraction layer, IRadioBackend conformance via TS-2000 testbed, sequencing relative to Sprint 26+27 networking work.
3. **4.1.17 release-cut decision** — substance check on what's in / what's queued for Sprint 29.
4. **Continue 4.1.17 matrix testing** if time and radio access permit.

### What was deliberately NOT done tonight:

- **Stuck-modal escape-path fix** (`project_stuck_modal_escape_design.md`) — was the original Track A pick, pivoted to Help-Escape mid-session. Still a 4.1.17 candidate.
- **Multi-radio architecture discussion** — Noel deferred to tomorrow.
- **Track B file commits** — Track B owns its own commits; not Track A's to ship.

### Memory updates today (1 net new):

- `project_jj_radio_folding.md` — strategic frame for the JJ Radio merge.
- (`project_stuck_modal_escape_design.md` was created earlier in the day — accounted for in the ~2 AM entry below.)

### Rigmeter snapshot — end of 2026-04-28:

**Grand totals (authored, code + docs + build + text_data combined):** 712 files, 138,728 lines, 615,205 words, 6,038,674 chars. Vendor adds 53,240 code lines (FlexLib 48,210; PortAudioSharp 3,949; P-Opus 1,081), bringing the on-disk total to ~191k lines.

**Per-category authored:** code 101,896 / text_data 9,065 / docs 21,908 / build 5,859 lines.

**Top authored projects by line count:** JJFlexWpf 38,602 (185 files); main_app 28,577 (166); Radios 23,733 (74); docs 20,106 (124); JJLogLib 5,807; JJPortaudio 2,361; JJTrace 543; tools 3,631; rest small.

**Code language split (authored):** cs 75.5% (76,944), vb 16.6% (16,956), xaml 4.9% (4,969), py 3.0% (3,027). C# remains the dominant authoring language; the Sprint 22+ WPF rebuild is now a strong majority of the surface.

**Fun comparisons (authored):** ≈60.4 braille volumes (100K cells each), 2.9 Moby Dicks, 0.79 King James Bibles, 68.4 hours read-aloud at 150 wpm, 2,775 printed pages (a stack 11.1 inches tall).

**Docs-to-code ratio:** 0.22 (21,908 doc lines / 101,896 code lines) — keeps climbing as the planning corpus grows.

**Today's git activity:** 3 commits (`19e6d4ef..1419bd0b`), 10 unique files touched, +791 insertions / −30 deletions, net +761 lines. Help-Escape commit will appear in tomorrow's `today` view (it lands as part of this seal's commit).

**NAS snapshot:** `\\nas.macaw-jazz.ts.net\jjflex\historical\stats\2026-04-28-1419bd0b.json` (tokei warning was harmless — fall-back counter ran). 8 historical snapshots tracked now; trend across them is `code 230k → 101k → 78k → 78k → 90k → 100k → 101k` reflecting the early Phase-0 cleanup that pruned Icom/Kenwood/Generic legacy.

## 2026-04-28 end-of-day seal (~2 AM, ran into 4/28): No-radio guard fix + iterative-testing diagnostic chain + foundation findings + dispatcher-paths architectural correction

**Long iterative-testing session, autonomous late-stage.** Started 9 PM on 4/27 as "verify C.2 verbosity ladder Terse setting" — produced **18 distinct findings** across three architectural layers by 2 AM on 4/28. Noel framed it: *"Sometimes testing can be iterative, it sure was today right?"* Each finding got its own TODO entry; the chain is captured in `docs/planning/2026-04-28-morning-briefing.md` as the synthesis.

### Code shipped tonight (3 files, single commit):

- **`Radios/ScreenReaderOutput.cs`** — added `SpeakNoRadioConnected()` helper. Verbosity-aware ("JJ Flexible Home, no radio connected" / "Home, no radio" / "No radio connected" at Off). Tagged Critical-level so it speaks at every verbosity setting.
- **`ApplicationEvents.vb`** — `ExecuteCommandCallback` no-radio guard for Command Finder / menu invocations (initial fix earlier in session).
- **`JJFlexWpf/Controls/FrequencyDisplay.xaml.cs`** — `FocusFrequencyField()` belt-and-suspenders patch for menu-invoked / direct-call paths.
- **`JJFlexWpf/KeyCommands.cs`** — `DoCommand` no-radio guard. **The architectural correction**: earlier ApplicationEvents.vb guard only caught Command Finder / menu paths. Direct keystrokes flow through `DoCommand` at line 1643. Adding the guard there catches Ctrl+F, band keys, mode-switches, and the bulk of Radio-scope keystrokes.

### Test results from the session:

- **C.2 (Terse + radio):** PASS
- **C.2 (Terse + no radio, original symptom):** silent → led to no-radio guard work
- **C.4a (Chatty + no radio + F2):** PASS (after first fix)
- **C.4b (Terse + no radio + F2):** PASS
- **C.4c (Off + no radio + F2):** **DEFERRED-by-dependency** — Off-verbosity tests need mature CW nav or braille output to verify Critical-level routing. Code path exercised by C.4a/C.4b; alt-channel surfacing unverified.
- **C.4d (Ctrl+F + no radio):** **FAILED** — dialog opened anyway. Surfaced the dispatcher-paths architectural finding (my first guard was in wrong layer).
- **C.4e (band keys + no radio):** **FAILED** — silent. Same root cause.
- **C.4f (R + no radio):** **FAILED** — silent. Different sub-case (field-handler-routed, not in dispatcher).
- **C.4g (Ctrl+Shift+V + no radio):** PASS (Global scope, correctly bypasses guard)
- **C.4h (F1 + no radio):** PASS-WITH-FINDING — Help opens, but Escape doesn't close it (only Alt+F4 does).
- **C.4i (F2 + radio connected):** **POSSIBLE REGRESSION, NEEDS VERIFY** — Noel reported missing "Home" prefix at all verbosity levels. May be test-setup issue (focus didn't transition) or real regression from the FocusFrequencyField patch. Top item in morning briefing for first-thing-when-awake check.
- **C.5 / C.6 / C.7 / C.8 (arrow nav with radio):** Multiple findings logged — Slice/Slice Operations naming clarity, Squelch Level should skip when off (matches RIT/XIT pattern), Classic vs Modern field order should mirror.

### TODO entries logged tonight (12 new bugs in `docs/planning/vision/JJFlex-TODO.md`):

1. **Modal connecting state has no escape path** (CRITICAL accessibility blocker — taskkill required)
2. **Slice-acquisition failure presented as "stuck on connecting"** (state-machine bug, MultiFlex collision case)
3. **AS-retry-then-janky-reconnect pathway** — Sprint 26+27 networking-overhaul regression
4. **Connect path takes longer post-Sprint-26+27** — investigation queue with timing breakdown
5. **KEY CONFLICT: Ctrl+F bound to SetFreq AND SpeakFrequency** — clean one-line fix proposed
6. **Help dialog can't be exited with Escape** (only Alt+F4)
7. **Dialog Escape produces "pane" announcement** (focus restoration to unnamed container)
8. **Squelch Level should skip during arrow-nav when Squelch is off** (matches RIT/XIT)
9. **Slice / Slice Operations field announcements lack purpose-naming**
10. **Classic vs Modern field orders not mirrored** (until Customize Home ships)
11. **Universal Home keys silent outside Home with no radio** (field-handler routing gap)
12. **F2 connected case may have lost "Home" prefix** (regression vs test-setup, needs verify)

### New memory entries (5 design-level findings):

- **`project_dialog_escape_rule.md`** — sibling to no-silent-keystrokes; every dialog must respond to Escape AND Alt+F4
- **`project_verbosity_architecture_proposal.md`** — Sprint 30+ design memo: split speech / CW / braille into independent channels with shared verbosity ladder, smart-warn against disabling all channels
- **`project_trace_persistence_design.md`** — Sprint 29 deliverable: manifest-driven archive, LZMA2-compressed, JSON manifest, 30-day auto-prune, local-first with optional NAS mirror
- **`project_as_retry_pathway_regression.md`** — confirmed Sprint 26+27 overhaul regression with code locations and Sprint 29 investigation queue
- **`feedback_iterative_testing_pattern.md`** — pattern captured: 18 findings from one matrix entry; embrace and capture every thread
- **`feedback_dispatch_paths_not_unified.md`** — architectural correction: 4 parallel dispatch paths exist (hard-wired / DoCommand / ExecuteCommandCallback / field-handler), audits must apply to all individually

### Investigation artifacts preserved:

- `.investigation/2026-04-28-marks-radio-stuck/Trace-current-73sk-session.txt` — the actual stuck-on-Don's-radio trace (preserved before relaunch overwrite). Diagnostic gold.
- `.investigation/2026-04-28-marks-radio-stuck/Trace-old-likely-stuck-session.txt` — the brief 73-SK no-radio test (filename outdated; it's the no-radio test, not the stuck session).
- `.investigation/2026-04-28-morning-briefing.md` — synthesis briefing (also copied to `docs/planning/2026-04-28-morning-briefing.md` for git history).
- `.investigation/` is now in `.gitignore` — traces contain JWT fragments, email addresses, IP addresses, and callsigns; they stay local per the no-phone-home principle.

### Plan for next session (when Noel wakes up):

1. **C.4i regression check** (~5 min) — confirm whether F2-prefix loss is real or test-setup. Use C.1 menu-bar-bounce technique.
2. **No-radio guard re-test** (~10 min) — Ctrl+F, band keys, etc. with the new DoCommand-layer guard. Should now produce no-radio announcement.
3. **Continue C.5+ matrix work** if above passes.
4. **Triage 4.1.17 vs Sprint 29 split** for the 12 new TODO entries — stuck-modal-escape and Help-Escape are 4.1.17 candidates; AS-retry investigation, trace persistence, verbosity architecture are Sprint 29+.

### What was deliberately NOT done tonight:

- **Code fixes for the 12 newly-logged bugs.** Each needs design review with Noel. The dispatcher-path fix (KeyCommands.cs DoCommand guard) was the only code shipped because it was the architectural correction with empirical evidence.
- **The KEY CONFLICT one-liner fix.** Want Noel's confirmation of the Sprint 15 design intent before changing the binding.
- **Stuck-modal escape-path fix.** Critical accessibility but multiple design options — needs ~5-min conversation before implementing.

### Tester / public communication path tonight:

- **NAS history:** debug build copied to `\\nas.macaw-jazz.ts.net\jjflex\historical\<version>\x64-debug\`
- **Dropbox top-level daily:** updated with tonight's debug zip + NOTES (real code change shipped, daily promotion warranted)
- **Memory backup:** snapshotted to NAS `historical\memory\`
- **Private docs backup:** snapshotted to NAS `historical\private\`
- **Push:** `sprint28/home-key-qsk` pushed to origin/nromey

---

## 2026-04-26 end-of-day seal: Block A bug tranche + Rigmeter v1.2 + deferred-item pivot (universal `=` toggle, slice-jump, mode-change polish, CW close) + tester daily

**Long autonomous batch session driven by `docs/planning/2026-04-26-evening-batch.md`.** Started against the plan's three-block structure (Block A bug fixes from Section C testing, Block B rigmeter v1.2, Block C end-of-day seal), then mid-session Noel directed scope expansion: pull deferred Sprint 29 candidates into tonight's batch since the deferral was meant to be "until done testing today, not multi-day." Implemented four additional features beyond the plan. End-of-day debug build is `4.1.16.208`, published to NAS history + Dropbox tester debug + Dropbox top-level daily.

### Commits landed tonight on `sprint28/home-key-qsk` (13 total):

**Block A — bug-fix tranche (7 commits):**

- `a29aa127` — Step-entry: `+` triggers entry, `-` is now a no-op with terse "uses plus only" notice (was identical to `+`).
- `c9da95de` — Connection-event speech omits "no active slice" when `MyNumSlices` is still zero post-1.5s delay (race window was real).
- `fa150c67` — `SetupFreqoutModern` doc comment rewritten to reflect post-Sprint-26-Phase-8 reality (Modern carries 11 fields, not the old "Freq + Slice + SMeter only" claim).
- `2dbfb1c5` — Universal `V` slice cycle wraps via `CycleVFO(1, wrap: true)` at three sites (AdjustFreq Classic, TryHandleUniversalHomeKey, AdjustFreqModern). Plan named two; Modern was a third with the same bug.
- `053e8a96` — `AdjustSliceOps` letter-jump now matches Modern semantics (a-h jumps directly to slice 0-7 instead of the old `'A'`-only Jim-parity no-op).
- `46bb2646` — RIT/XIT `+`/`-` now announces "made positive/negative" + chatty `was X` delta (was silent — discoverability bug).
- `f6c00aa2` — `IsPositionSensitive` is RIT/XIT off-state-aware. Off-RIT/XIT now behaves as non-position-sensitive so arrow keys skip out instead of moving silently within the off-field. Investigation found the field width never actually collapses (the plan's diagnosis); the real bug was the unconditional position-sensitivity. Single-helper fix, much smaller than the plan's 1-2h estimate.

**Block B — Rigmeter v1.2 (2 commits):**

- `2a966f05` — Comprehensive v1.2 implementation. Tokei integration with auto-fetch (cache in `%LOCALAPPDATA%\rigmeter\tools\`, `--no-fetch` opt-out), `--verbose` flag with stdlib logging, `--format text|json|markdown|csv` (default text stays bullets, no-tables-rule preserved), four new subcommands (`authors` via git blame with Jim/Noel alias collapse, `sprint <N>` with branch auto-detection, `debt` for TODO/FIXME/HACK/XXX, `forgotten --days N`), docs-to-code ratio in `all` and `growth`, ASCII sparkline trend when ≥5 NAS snapshots exist, interactive menu wizard via `--interactive` (stdlib-only, opt-in; no-args still prints help so scripts stay safe). Schema bumped to v2 with `pure_code`/`comments`/`blanks` fields. ~1,200 net-line addition. Smoke tests passed (debt found 91 markers, forgotten reports 391 stale files >60d, JSON format parseable, verbose routes to stderr).
- `2e86672e` — README rewritten for v1.2 — feature surface, schema-v2 shape, deferrals (TUI explorer, tokei-vs-arbitrary-refs, serve subcommand).

**Pivoted to deferred items per Noel's mid-session direction (4 commits):**

- `1f7a8e65` — Mode-change coach text branches on `CurrentVerbosity` (chatty users get fuller paragraph; terse users get the existing brief hint). Single-Speak branched pattern instead of dual-Speak (which would duplicate for chatty users since both Terse and Chatty pass the filter). Plus PlayCwSK now appends `ee` (two single dits) after the SK prosign for a friendly hand-wave close.
- `a645e931` — Universal `=` transceive becomes a TOGGLE with memory of prior split TX. `ToggleTransceive()` helper centralizes the new behavior; three universal `=` sites now call it. First press from split saves prior TX, sets transceive. Second press restores. Clean idempotent re-announce on transceive-with-no-prior. Two slice-specific `=` sites (AdjustVFO, AdjustSliceOps) keep their existing "set this slice transceive" semantic — those are slice-jump-and-set, not universal toggles.
- `0f902020` — Universal slice-jump from any Home field via `Ctrl+J Shift+A` through `Ctrl+J Shift+H` (skipping `Shift+F` due to the existing RX-filter binding; only affects FLEX-6700 owners which we don't have). `JumpToSlice(int)` validates against `Rig.ValidVFO()`, sets RXVFO without disturbing focus, announces "Slice X active" with confirmation earcon. Out-of-range targets get a polite "not yet created" or "max N slices on this radio" message. Auto-create on missing slice deferred (FlexBase.NewSlice() creates next-available, not a target index — wrapping that for "create up to slice N" is its own state-machine work).

**Plus tonight's seal commit (this one) bundles:** Agent.md update, rigmeter snapshot text below, end-of-day procedure.

### What was NOT addressed tonight, deliberately:

- **`+`/`-` cross-field semantic rationalization** — the TODO entry explicitly frames this as "Decision needed: rationalize or accept. No correctness bug; pure UX-consistency question." With A.1 and A.6 landed, every `+`/`-` press now announces what it did (audible inconsistency replaces silent inconsistency), giving Noel concrete behavior to react to. Picking a rationalization unilaterally without Noel's call would be choosing a product direction. Surfaced for tomorrow's call.

### Tester / public communication path tonight:

- **NAS history:** `\\nas.macaw-jazz.ts.net\jjflex\historical\4.1.16.208\x64-debug\` — JJFlex_4.1.16.208_x64_debug_20260426-2316.zip + NOTES + exe + pdb.
- **Dropbox tester debug folder** (Don, Justin etc. read from here): purged prior, fresh `JJFlex_4.1.16.208_x64_debug.zip` + `NOTES-4.1.16.208-debug.txt`.
- **Dropbox top-level daily** (the easy "what's tonight's build?" pointer): `JJFlex_4.1.16.208_x64_daily.zip` + `NOTES-daily.txt`.
- **Memory backup:** `\\nas.macaw-jazz.ts.net\jjflex\historical\memory\memory-20260426-231654.zip` (71 files).
- **Private docs backup:** `\\nas.macaw-jazz.ts.net\jjflex\historical\private\private-20260426-231641.zip` (93 files).
- **Rigmeter JSON snapshot:** `\\nas.macaw-jazz.ts.net\jjflex\historical\stats\2026-04-26-0f902020.json` (the v1.2 shape — schema_version=2 with optional pure_code/comments/blanks fields, but tokei wasn't run during this snapshot since `--no-fetch` was used to avoid network during the seal; numbers continue the time series via the v1.1 shape and v1.2 features land empty for now).

### Plan for tomorrow:

- Test the 13 commits in real radio operation. Resume Section C of the 4.1.17 matrix (next entry: C.2 verbosity Terse F2 announcement; then C.3-C.6, then non-universal-keys subsections).
- Decision needed from Noel: `+`/`-` cross-field rationalization (rationalize how, or accept as-is).
- Verify universal slice-jump (Ctrl+J Shift+A-H) against the slice-jump-on-uncreated-slice case (announce path); the auto-create path still requires the FlexBase wrapper work to make the slice creation target a specific index.
- Verify universal `=` toggle round-trips correctly across split↔transceive flips.
- If a 6700 user surfaces, resolve the Shift+F slice-F-vs-RX-filter conflict per the strategy noted in the slice-jump commit.
- Continue 4.1.17 matrix through remaining sections (D logging, E remote SmartLink, F networking diagnostics, G settings, H help system, I MultiFlex, J accessibility cross-cutting).

### Rigmeter snapshot — end of 2026-04-26

Captured at `a9731616` (HEAD after the seal commit) and `0f902020` (the snapshot file written to NAS during the seal). v1.2 of rigmeter itself just landed; the totals here are still v1.1-shape since the seal-snapshot was run with `--no-fetch` (no network required during seal), so tokei dimensions are empty in the JSON.

**Authored vs vendor headline:**

```
Authored: 711 files / 137,704 lines / 603,253 words / 5.95M chars
Vendor:   180 files /  53,240 lines / 147,931 words / 1.88M chars
Combined: 891 files / 190,944 lines / 751,184 words / 7.83M chars
```

**Per-category authored:**

```
  code:      101,676 lines (430 files)
  docs:       21,111 lines (131 files)  ← growing fast (TODO additions, plan doc, batch doc)
  text_data:   9,065 lines  (84 files)
  build:       5,852 lines  (66 files)
```

**Code language breakdown (authored):**

```
  cs:    76,736 lines (75.5%)
  vb:    16,944 lines (16.7%)
  xaml:   4,969 lines  (4.9%)
  py:     3,027 lines  (3.0%)   ← rigmeter coming into existence
```

**Today's git activity (since midnight local 2026-04-26):**

```
  Commits:           25
  Unique files:      14
  Insertions:        4,371
  Deletions:           350
  Net line change:  +4,021
  Files in diff:        14
  Authors:           JJ Flexbot ×25
```

**Per-file change breakdown for today:**

```
  tools/rigmeter/rigmeter.py:                       +2,736 / -294   ← v1.1 → v1.2 rewrite
  docs/planning/2026-04-26-evening-batch.md:          +547 /   -0   ← the plan itself
  docs/planning/vision/JJFlex-TODO.md:                +287 /   -0   ← bugs logged today
  Agent.md:                                           +282 /  -24
  JJFlexWpf/FreqOutHandlers.cs:                       +239 /  -43   ← five fixes converged
  tools/rigmeter/README.md:                           +215 / -110
  JJFlexWpf/KeyCommands.cs:                            +65 /   -0   ← slice-jump
  JJFlexWpf/MainWindow.xaml.cs:                        +48 /  -10   ← connect speech, doc comment, mode-change coach, PlayCwSK ee
  Radios/AuthFormWebView2.cs:                          +35 /   -4   ← morning's WebView2 dispose fix
  CrashReporter.vb:                                    +19 /   -0
  ApplicationEvents.vb:                                +10 /   -0
  JJFlexWpf/Controls/FrequencyDisplay.xaml.cs:          +8 /   -5   ← cursor-orphan fix
  CLAUDE.md:                                            +1 /   -1
```

**Growth vs yesterday's seal commit (`53729124`, 2-day span):**

```
  Code base lines:   98,855  →  101,676   (+2,821 lines, +2.9%, +1.41%/day)
  Doc base lines:    19,833  →   21,202   (+1,369 lines, +6.9%, +3.41%/day)
  Docs-to-code ratio:  0.20  →     0.21   (+0.01)
```

Docs growing 2× faster than code in this window — Sprint 28 wrap + plan-doc + TODO-log activity dominates the docs side; the code side was tonight's bug-fix tranche plus the deferred-item pivot.

**Per-language growth (authored, 2-day span):**

```
  py:    568 →  3,027   (+2,459, +432.9%)   ← rigmeter v1 → v1.2 trajectory
  cs:  76,403 → 76,736   (+333,    +0.4%)
  vb:  16,915 → 16,944   (+29,     +0.2%)
  xaml: 4,969 →  4,969   (+0,       0.0%)
```

**Hot files in the 2-day span (top 10 by churn):**

```
  tools/rigmeter/rigmeter.py:                            3,071
  docs/planning/2026-04-26-evening-batch.md:               547
  tools/rigmeter/README.md:                                429
  Agent.md:                                                353
  docs/planning/vision/JJFlex-TODO.md:                     287
  JJFlexWpf/FreqOutHandlers.cs:                            282
  JJFlexWpf/KeyCommands.cs:                                 65
  JJFlexWpf/MainWindow.xaml.cs:                             58
  Radios/AuthFormWebView2.cs:                               39
  test-matrix rename (sprint28 → 4.1.17):                   22
```

**Fun comparisons (authored only):**

```
  ≈ 59.5 braille volumes (was 58 yesterday — modest doc + code growth)
  ≈ 2.87 Moby Dicks
  ≈ 0.77 King James Bibles
  ≈ 67 hours of read-aloud time at 150 wpm
  ≈ 2,754 printed pages
  ≈ 11.0 inches of stack height
```

NAS time-series JSON: `2026-04-26-0f902020.json` (continues the seven-baseline-points series — v4.1.9, v4.1.12, v4.1.15, v4.1.15.1, v4.1.16, last-night's-pre-rigmeter-v1.1-commit, and tonight). Next backfill or seal pass will pick up the v1.2 schema with tokei dimensions populated once tokei is on disk.

## 2026-04-26 (afternoon → evening) — Sprint 28 wrap commits + 4.1.17 matrix runtime testing + design discussion → CONTEXT ROTATION

**Productive long-running session.** Started with the morning's WebView2 runtime confirmation, expanded into universal-keys field audit + KeyToChar fix + WPF dispatcher exception handler, then pivoted to building out the 4.1.17 verification matrix as we ran tests. Closed the session with a context rotation to a fresh Claude Code instance for tonight's loose-end coding (rigmeter v1.2 + bug-fix tranche + end-of-day seal).

### Commits landed today on `sprint28/home-key-qsk`:

- `261f5e2d` — WebView2 Dispose marshals to UI thread to fix SmartLink crash. **Runtime confirmed by Noel this morning.**
- `4cf73823` — Universal Home keys now work from all 7 audited fields (Split, VOX, Offset, Mute, Volume, RIT, XIT). Runtime verified via Section C tests C.7a-C.7l (see matrix).
- `4b94ce47` — Crash reporter: capture WPF Dispatcher exceptions (was falling through to AppDomain). Foundational pre-Sprint-29-crash-reporter work. Untested at runtime (hard to test without forcing a crash; logged as deferred).
- `4bc11596` — Rigmeter v1.1.1: replace pipe-separated tables with screen-reader bullets. Verified end-to-end this morning.
- `7f8f0d48` — KeyToChar: distinguish '=' from '+' on the OemPlus physical key. **Three previously-dead features unblocked** (RIT→XIT copy, AdjustFreq transceive, universal `=`). Runtime verified via test C.7c (RIT field's `=` now copies RIT→XIT correctly).
- `69dac19f` — Backlog: log five fartsnoodle-discovered items from morning testing.
- `5adf19c1` — Test matrix: rename to 4.1.17 + Section C runtime verification + TODO grows. Today's afternoon-session capture.

### What ran at runtime today:

5-of-5 universal-keys directed tests passed (Test 1: M from VOX; Test 2: R from Split; Test 3: `=` from RIT copies RIT→XIT after KeyToChar fix; Test 4: Shift+M from Mute = mute-all; Test 5: Shift+, = release-all-extras). Then continued into Section C of the matrix:

- C.1 PASS — F2 focus landing returns to last Home field with full "JJ Flexible Home" prefix
- C.7a-C.7e PASS — per-field universal-keys verification (M, R, =, Shift+M, Shift+,)
- C.7f PASS — Q from Offset
- C.7g PASS-WITH-CAVEAT — V cycles forward but doesn't wrap (logged TODO)
- C.7h PASS-WITH-CAVEAT — `=` works as transceive but is set-only, not toggle (logged TODO)
- C.7i PASS — `+` from Frequency triggers step-entry (KeyToChar regression check)
- C.7j PASS — `+` from RIT at -30 flips to +30 (abs-value behavior preserved)
- C.7k BUG-OPEN — RIT/XIT cursor-orphaning after toggle-off (logged, pre-existing latent bug exposed by universal R)
- C.7l PASS — F2+M Don's-bug regression check (Classic + Modern both fire mute)

### TODO additions (8 new items today, all logged in `docs/planning/vision/JJFlex-TODO.md`):

1. Step-entry `+`/`-` symmetry bug — recommend Option A (remove `-` from the entry trigger)
2. RIT/XIT `+`/`-` press is silent — captured both terse/chatty announcement options
3. `+`/`-` cross-field inconsistency — pure UX observation, defer to Sprint 30+ review
4. Universal V cycle does not wrap — recommend `wrap: true` (2-line fix)
5. Universal `=` should be a toggle with memory of prior split TX — full design captured (Option A: memory-toggle for ham workflow)
6. Letter slice-jump (a-h) inconsistent between Classic + Modern — Modern's behavior is right
7. **Universal slice-jump from any field — full design discussion captured.** Adopted "Ctrl+J = jump to" as a NEW design principle. `Ctrl+J Shift+A` through `Ctrl+J Shift+H` for slice jump. Multi-channel feedback per `project_multi_braille_output_vision.md`. Layer-help affordances (`?` for list, F1 for full reference). Vim-style inactivity-triggered announcement. Auto-create-on-jump-to-uncreated-slice with distinct earcon.
8. Plus the morning's 5 items (connection-event "no active slice" race, RIT/XIT cursor-orphaning, SetupFreqoutModern stale doc comment, mode-change coach text polish, PlayCwSK "e e" close)

**None of the logged items block 4.1.17 release.** All have workarounds; fixes batch into the post-test polish window.

## RESUME HERE for fresh context (tonight's loose-end coding):

**Single source of truth:** `docs/planning/2026-04-26-evening-batch.md`

That file contains the comprehensive plan for tonight's three blocks of work:
- **Block A** — 7 bug fixes (steps-entry `+`/`-` symmetry, connection-event race, stale doc comment, V wrap, letter slice-jump consistency, RIT/XIT silent `+`/`-` announcement, RIT/XIT cursor-orphaning)
- **Block B** — comprehensive rigmeter v1.2 (tokei + auto-fetch, `--verbose`, `--interactive` BOTH menu and TUI, `--format=json|markdown|csv`, author attribution, sprint detection, TODO counter, docs-to-code ratio, sparkline trend, `forgotten` subcommand, full README + memory updates)
- **Block C** — full end-of-day seal per CLAUDE.md procedure

The plan doc has every implementation detail the fresh context needs: file paths, line numbers, fix recipes, design decisions already made (e.g., RIT/XIT announcement Option A: "made positive/negative"), commit message subjects, verification steps, and explicit auto-mode authorization scope.

### Resume prompt for the fresh context (copy-paste verbatim):

> Read `docs/planning/2026-04-26-evening-batch.md` and execute it end-to-end in auto mode. Commit + push as you go (origin sprint28/home-key-qsk, NEVER upstream). Run the full end-of-day seal at the end. The plan doc has all implementation details, fix recipes, file paths, and authorization scope.

### Why a single batch doc instead of scattered pointers:

Earlier this session the fresh context couldn't find the rigmeter v1.2 details (scattered across memory + TODO + Agent.md) or understand what "fix the bugs we found tonight" referred to (no consolidated list). The batch doc fixes both: every detail in one read. See `feedback_dont_under_design_or_defer_aggressively.md` for the comprehensive-scope feedback that shaped the doc.

### Tomorrow (after fresh context's work seals tonight):

- Resume Section C testing in the 4.1.17 matrix (next entry: C.2 verbosity Terse F2 announcement test, then C.3-C.6, then Section C non-universal-keys subsections)
- Continue testing through remaining matrix sections (D logging, E remote SmartLink, F networking diagnostics, G settings, H help system, I MultiFlex, J accessibility cross-cutting)
- Address Sprint 29 design items if Noel wants to dive into them (universal slice-jump, transceive toggle, +/- cross-field rationalization)

## 2026-04-26 (overnight) tooling: rigmeter v1.1 — NOT a daily for testers

**Late-night work, no radio code touched.** Noel was waiting out medication (~30-45 min) and chose to spend the time on rigmeter v1.1 rather than radio testing. Plan was to commit and push for durability, **skip the Dropbox tester daily** (yesterday's daily still represents radio code accurately — pushing testers a build that's identical-modulo-rigmeter would dilute the signal of "when there's a new daily, there's something to test"), and resume radio data work tomorrow.

**Single commit** on `sprint28/home-key-qsk`:

- `a57a2075` — **Rigmeter v1.1**: vendor split, binary deny-list, growth deltas, JSON snapshots. 1,585 insertions / 260 deletions across 3 files (`tools/rigmeter/rigmeter.py`, `tools/rigmeter/README.md`, `CLAUDE.md`). Headline shrunk from v1's 380K lines to v1.1's 135K authored-only lines (65% reduction). Full feature surface: see commit message + updated `tools/rigmeter/README.md`.

**What ships in rigmeter v1.1** (all baked in tonight, smoke-tested):

- Vendor split: `FlexLib_API/`, `P-Opus-master/`, **and the newly-discovered `PortAudioSharp-src-0.19.3/`** report as a separate "vendor" rollup; headline number means "what Noel and Jim wrote."
- Explicit `BINARY_EXTENSIONS` deny-list (39 binary files now properly tagged, was silently dropped without audit in v1).
- Switch from `os.walk` to `git ls-files` (excludes 237 untracked build-output files v1 was counting; live and ref-mode snapshots now directly comparable for the same SHA).
- Per-language % within `code` (% VB / % C# / % XAML / % Python), authored AND across-all.
- Auto-discover project buckets from top-level git paths (kills the hand-curated `PROJECT_DIRS` list which had drifted — picked up 17 new projects).
- New `growth <ref-a> [<ref-b>] | --since <date> | --use-snapshots <date-a> <date-b>` subcommand: code-base growth % AND doc-base growth % shown separately as headline lines (per Noel's specific ask). Per-day normalized rate when span ≥ 1 day.
- New `snapshot` subcommand: JSON to NAS at `\\nas.macaw-jazz.ts.net\jjflex\historical\stats\<commit-date>-<short-sha>.json` with `%LOCALAPPDATA%\rigmeter\snapshots\` fallback. `snapshot --sync` reconciles local-only files up to NAS later.
- New `backfill --refs <pattern>` subcommand. Tonight: backfilled all 5 v* tags in 3.2 seconds.
- New `installers` subcommand: parses NAS `historical/<version>/installers/Setup JJFlex_*.exe` for size trend (builds-bloat watchdog).
- `release <tag> --growth` and `--explain` flags. `releases` gained "days since prior" column.
- `--explain` flag on `all`/`growth` shows file accounting (which files included/excluded with example paths per skip reason).
- CLAUDE.md step 4a updated: seal workflow now also calls `rigmeter snapshot` for the JSON write alongside the Agent.md text paste. The two-channel approach: Agent.md is human-readable history, NAS JSONs are machine-queryable.

**NAS time-series seeded** (six baseline points written tonight):
- `2026-01-29-76c5f870.json` (v4.1.9)
- `2026-02-08-05856db7.json` (v4.1.12)
- `2026-03-07-35948dea.json` (v4.1.15)
- `2026-03-08-ad9bb1a8.json` (v4.1.15.1)
- `2026-04-19-6d6a0084.json` (v4.1.16)
- `2026-04-24-ebfe6413.json` (current HEAD pre-rigmeter-v1.1-commit)

**Memory updates:**
- UPDATED `project_rigmeter_stats_tool.md` — v1.1 shipped status, headline-shift numbers, design decisions captured, v1.2+ deferred items listed.

**CLAUDE.md drift:** Step 4a updated in this commit to reflect the new JSON snapshot path. No other drift.

**Plan for tomorrow (radio + radio data, hard):**

1. **Resume Sprint 28 Phase 9** (4.1.17 combined test matrix execution at the radio). Last session was Don-on-6300 + Noel-at-radio for the bug-bundle; pick up wherever the matrix was last marked.
2. **WebView2 thread-affinity crash fix** — queued from 2026-04-24 seal. AuthFormWebView2.Dispose runs on a background thread, touches CoreWebView2 which is UI-thread-only. Crash dump: `%APPDATA%\JJFlexRadio\Errors\JJFlexError-20260424-110554.zip`. Fix: marshaling guard (InvokeRequired pattern).
3. **Universal-keys field audit** — Doc claims M/V/R/X/Q/= work from any Home field; reality is only Frequency / Slice / Slice Operations have them universally. Audit Split, VOX, Offset, Mute, Volume, RIT, XIT handlers.
4. **Command Finder** post-deferral verification — yesterday's `3893082b` fix needs another testing pass to confirm the dispatcher.BeginInvoke past-dialog-close pattern actually eliminates the kill-task race.
5. After radio work, run a fresh `rigmeter snapshot` from the post-radio-work state to capture day-N+1 of the time series.

**Note for the morning rigmeter snapshot:** When a real seal happens tomorrow with radio code changes, the snapshot section format below changes — use authored-only headline (the new v1.1 default), include vendor as a separate line, include the per-language breakdown. Format example below.

### Rigmeter snapshot — overnight 2026-04-26 (v1.1 first-write)

Captured immediately after rigmeter v1.1 commit. The headline transition from v1 to v1.1 — same git state, different (more honest) counting:

```
Headline shift (same HEAD, different definition of "what we count"):

  v1:    1,188 files / 380,589 lines / 1,127,323 words / 17.16 M chars
  v1.1:    710 files / 135,025 lines /   583,756 words /  5.80 M chars  (authored only)
                       +  53,240 vendor lines (FlexLib_API, P-Opus, PortAudioSharp)
                       =  890 files / 188,265 lines combined

The 65% line-count drop is real signal:
  - 237 untracked build-output files no longer counted (git ls-files vs os.walk)
  - 53,240 vendor lines moved out of headline into vendor rollup
  - 39 binaries properly tagged (cty.dat, .dll, .pdb, .chm, .png, .ico, .wav, etc.)
  - .dat files reclassified as binary (upstream country-data blobs, not authorship)

Per-category (authored only):
  code      100,123 lines / 330,836 words / 430 files
  text_data   9,065 /  32,663 /  84
  docs       19,985 / 200,736 / 130
  build       5,852 /  19,521 /  66

Per-language within code (authored / all):
  cs    76,403 (76.3% authored, 84.3% all)
  vb    16,915 (16.9% / 11.2%)
  xaml   4,969 ( 5.0% /  3.3%)
  py     1,836 ( 1.8% /  1.2%)

Fun comparisons (authored only):
  Braille volumes (~100K cells each):    58.0   (was 171.7 in v1)
  Moby Dicks (~210K words):               2.8   (was   5.4)
  King James Bibles (~783K words):        0.75  (was   1.44)
  Read-aloud time at 150 wpm:            64.9 hours
  Printed pages (50 lines/page):        2,701
  Stack-of-printed-pages height:         10.8 inches (27.4 cm)

Real growth measured (v4.1.15 → HEAD, 48 days):
  Code base lines:    78,819 → 100,123  (+21,304 lines, +27.0%, +0.563%/day)
  Doc base lines:      9,894 →  20,059  (+10,165 lines, +102.7%, +2.141%/day)
  VB:                 18,448 → 16,915   ( -1,533 lines,  -8.3%) — WinForms→WPF rewrite shrinkage
  C#:                 56,393 → 76,403   (+20,010 lines, +35.5%) — most of the work
  XAML:                3,967 →  4,969   ( +1,002 lines, +25.3%) — UI work
  Python:                 11 →  1,836   ( +1,825 lines, +16,591%) — rigmeter coming into existence

Today's git activity:
  Commits:           1 (a57a2075 — rigmeter v1.1)
  Files in commit:   3
  Insertions:    1,585
  Deletions:       260
  Net change:   +1,325
  Authors:       JJ Flexbot (1)
```

## 2026-04-24 end-of-day seal: bug-bundle day — F2+M, mute-all/release-all, KeyDefs migration, About dialog focus, Command Finder, BT-on-connect delay, plus rigmeter first-draft

**Long testing session with Noel at the radio (and on Don's 6300 via SmartLink before Don signed off mid-day).** Most of today's work emerged organically as test findings — a planned testing session against the 4.1.17 matrix turned into a chain of bug discoveries, fixes, and one feature (multi-slice mute / release universals) that came out of investigating the F2+M bug. End-of-day debug build is 4.1.16.<N> (next build after this seal commit).

**Today's commits on `sprint28/home-key-qsk`** (chronological):

- `0215a675` — Regenerate CHM with today's doc updates (squelch-home + jj-flexible-home).
- `6e8c81d9` — Fix universal Home keys on Frequency field in Classic tuning mode. Don's bug: F2+M didn't mute in Classic. AdjustFreq was missing M, R, X, Q, = handlers; only AdjustFreqModern had them since Sprint 28 Phase 3.9 / Phase 5. Also changed V from delegating to AdjustVFO (memory-mode) to direct CycleVFO(1), matching Modern.
- `2193a2fc` — "Classic tuning mode" / "Modern tuning mode" terminology scrub in operating-modes.md, getting-started.md, settings-profiles.md, slice-management.md. New memory `project_tuning_mode_terminology.md` captures the rule.
- `510e5a64` — debug-notes.txt rewritten for today's bundle.
- `51a0e3c4` / `2bfe41ba` / `80b02fb1` / `86cf2a6a` / `1f163f31` / `7b5550f2` / `f93eb2d1` / `f96e2e30` — interleaved CHM rebuilds (build-debug.bat regenerates CHM each time; tracked separately to keep the binary diff out of substantive commits).
- `b0e067a6` — Plural-slice speech bug ("1 slice active" not "1 slices active") in ScreenFieldsPanel.xaml.cs Create / Release Slice handlers.
- `bd832221` — Squelch Level field always shows the numeric value; "---" placeholder dropped. Adjacent Squelch field carries the active/inactive signal. Eliminates announce-vs-display mismatch for screen-reader users.
- `6263fb13` — Shift+M mute-all and Shift+Comma release-all multi-slice universal Home keys, with new MuteAllOnTone / MuteAllOffTone tri-tone earcons (~major third above the single-slice toggle), wired into all four Home field handlers + Slice menu + ScreenFields buttons.
- `b4761429` — Rewire global Shift+M binding from MuteSlice to MuteAllSlices, add Shift+Comma binding for ReleaseAllExtraSlices, register both as KeyCommands with Command Finder discoverability.
- `a70deb13` — Release-all preserves the currently active slice instead of forcing back to Slice A. Iterates highest index down to 0, skipping the keep index.
- `9c118d08` / `8d479736` — Release-all speech announces the surviving slice's letter; matrix updated with deferred multi-slice release-all test (gated on FLEX-8600 unbox).
- `c2b3285b` — KeyCommands auto-migrate user bindings when a command's default is removed (first attempt; Noel's testing showed it didn't fire because the `saved.Key == saved.SavedDefaultKey` check failed when SavedDefaultKey was Keys.None in older XML writes).
- `6f9e9a91` — KeyCommands robust migration detection: takeover check instead of strict SavedDefaultKey match. The new logic checks "is this key now claimed by another command's default in _defaultKeys?" — if yes, migrate, regardless of SavedDefaultKey state. Verified working on Noel's restored old KeyDefs.xml.
- `f7bac701` — CW BT-on-connect 1-second delay before firing prosign. Audio pipeline initialisation was clipping BT mid-character into perceived "N U" (same elements, audible gap parsed as a character boundary).
- `608689cc` — About dialog close restores focus to JJ Flexible Home (was leaving SR on "pane"); jj-flexible-home.md gets per-field overrides subsection (universal-keys caveat for Slice / Slice Operations).
- `3893082b` — Command Finder Enter on SearchBox activates first/selected result (was inert, requiring Tab into ListView); execute deferred via Dispatcher.BeginInvoke past dialog close to eliminate the race that left the app unable to accept Enter or Escape afterward (kill-task required).

**Today's seal commit** (this one) bundles:
- `tools/rigmeter/rigmeter.py` — v1 first draft of the curiosity-driven source-statistics CLI (per `memory/project_rigmeter_stats_tool.md`). Single-file Python, stdlib only. Subcommands: all, today, week, month, year, start, releases, release, compare, fun. Per-project breakdown across the 10 known top-level dirs; categorisation into code / text-data / docs / build; fun-stats overlay (braille volumes, Moby Dicks, Bibles, read-aloud time, page-stack height). NOT YET tested or smoke-run; no README yet — first-draft only, polish pass scheduled for tomorrow morning.
- This Agent.md update.
- CHM regen.

**Bugs / observations queued for separate later work (not blocking 4.1.17):**

- WebView2 thread-affinity crash when Tab is pressed during SmartLink auth flow. AuthFormWebView2.Dispose runs on a background thread and tries to touch CoreWebView2 which is UI-thread-only. Fix is a marshaling guard (InvokeRequired pattern). Crash dump from today saved at `%APPDATA%\JJFlexRadio\Errors\JJFlexError-20260424-110554.zip`.
- Audit other Home field handlers (Split, VOX, Offset, Mute, Volume, RIT, XIT) for full universal-key set. Doc claims M/V/R/X/Q/= work from any Home field; reality is only Frequency / Slice / Slice Operations have them universally.
- Rigmeter v1 polish: smoke-test all subcommands, write README, decide whether `jj-codestat` rename is preferred over `rigmeter` per Noel's colloquial usage today.

**Memory state additions/updates today:**

- NEW: `project_tuning_mode_terminology.md` — "Classic tuning mode" / "Modern tuning mode", never just "Classic mode" / "Modern mode."
- NEW: `project_utter_output_abstraction.md` — future `OutputProcessor.utter(chatty, terse, cwHaptic, utterSpeech, utterCW)` API for unified speech / CW / haptic output. Refined mid-day with Noel's two-bool addition for selective channel suppression.
- UPDATED: `project_8600_unbox_firmware_trigger.md` — added the deferred multi-slice release-all test to the list of 8600-unbox-gated items.

**Plan for tomorrow:**

1. Smoke-test rigmeter against the repo. Verify all subcommands run cleanly and produce sensible output. Catch any reasonable bugs.
2. Write a short `tools/rigmeter/README.md` with usage examples.
3. Decide rigmeter vs jj-codestat naming. Trivial `git mv` either way.
4. Continue or close out queued items: WebView2 crash fix, broader universal-keys field audit, Command Finder testing post-deferral fix.
5. Resume Phase 9 test-matrix execution at the radio if Noel is in testing mode again.

**CLAUDE.md drift:** none surfaced today. Yesterday's added "verify build compiles" step in the seal procedure is being honoured implicitly — every build today verified clean before subsequent work.

### Rigmeter snapshot — end of 2026-04-24

Snapshot baked into the seal trail per Noel's request 2026-04-24. Future seals should include this section as a standard end-of-day step (run `python tools/rigmeter/rigmeter.py all` and `today`, paste output, commit with seal). Accumulating these per-day in Agent.md gives a queryable time-series across the project's history.

```
=== Grand Total (full repo) ===
  Files:     1,188
  Lines:     380,541
  Words:     1,126,992
  Chars:     17,163,506

=== Per-Project Breakdown ===
  main_app      234,068 lines / 517,235 words / 578 files
  FlexLib_API    47,881 / 128,342 / 120
  JJFlexWpf      38,131 / 129,209 / 185
  docs           26,863 / 239,706 / 173
  Radios         23,202 /  78,787 /  70
  JJLogLib        5,447 /  16,715 /  19
  JJPortaudio     2,241 /   7,594 /  12
  P-Opus-master   1,081 /   2,832 /  18
  tools           1,084 /   5,057 /   5
  JJTrace           543 /   1,515 /   8

=== Per-Category Totals ===
  code         182,209 lines /   580,704 words / 800 files
  text_data    162,436 /   256,181 / 104
  docs          29,658 /   269,846 / 197
  build          6,238 /    20,261 /  87

=== Fun Comparisons ===
  Braille volumes (≈100K cells each):   171.6
  Moby Dicks (~210K words):               5.4
  King James Bibles (~783K words):       1.44
  Read-aloud time at 150 wpm:           125.2 hours (7,513 minutes)
  Printed pages (50 lines/page):        7,611
  Stack-of-printed-pages height:         30.4 inches (77.3 cm)

=== Today's Activity (since midnight local) ===
  Commits:            26
  Unique files:       22
  Insertions:      1,693
  Deletions:         127
  Net line change: +1,566
  Files in diff:      22
  Authors:         JJ Flexbot (26)
```

## 2026-04-23 end-of-day seal: Phase 8c-ii + Phase 11 + full 50-file help audit landed

**Big day, lots of code + lots of docs.** Sprint 28 is now functionally complete except for Phase 9 (the 4.1.17 combined test matrix). Phase 10 was formally deferred to 4.1.18 to ride with Customize Home (decision captured in `memory/project_customize_home_vision.md`). End-of-day debug build is 4.1.16.156 (or whatever the rebuild lands at after the seal commit).

**Today's commits on `sprint28/home-key-qsk`** (chronological):

- `2c4ed40a` — Phase 8c-ii: Wire What's New into Help menu and About dialog. Three discovery paths: Help menu item, in-content version-link via custom `jjflex://` URI scheme, persistent "View What's New" button (Alt+N). About dialog widened 520→620 to fit the new fourth bottom-row button.
- `c7489da1` — Defer Phase 10 to 4.1.18 (sprint plan banner) + remove stale `JJFlexRadioReadme.htm` `<Content Include>` from `JJFlexRadio.vbproj` that was blocking every build since yesterday's seal deleted the file but left the project reference dangling.
- `a4852cad` through `3839098d` — **Sprint 28 help audit, batches 1-9.** Voice + drift pass on 49 of 50 user-facing help docs (whats-new.md skipped per Noel's 5-pass editorial 2026-04-22). One file rename (`home-region.md` → `jj-flexible-home.md`), CHM TOC + HHP file-listing updated. ~25 drift fixes surfaced; major ones below.
- `9daa8c70` — Strip phantom Ctrl+Shift+H Command Finder claim from `keyboard-reference.md` (binding doesn't exist in code; the claim had propagated through 4 docs).
- `073fe21e` — Phase 11: archive Sprint 24-28 plans/test matrices to `docs/planning/agile/archive/`. Six file renames.

**Drift fixes worth remembering** (all surfaced by the help audit close-reading):

- The SmartLink connect flow was wrong in 4 docs (claimed "Connect menu > SmartLink" — actually Radio menu > Connect to Radio > Remote button inside the Select Radio dialog). Fixed everywhere.
- `leader-key.md` had 9 wrong leader-key claims out of ~12. Verified every leader case in `KeyCommands.cs:1776-1936` and rewrote against code.
- `quick-actions.md` described the Ctrl+Tab popup palette as live, but it was disabled in Sprint 28 Phase 3.5-3.6. Rewritten as "Redesign in Progress" with pointer to alternate hotkeys.
- `meter-tones.md` had 3 wrong leader-key shortcuts (M, P, R for meter ops — actually meter operations use Ctrl+Alt+M, Ctrl+Alt+P, Ctrl+Alt+V).
- `about-dialog.md` undercounted tabs (3 vs 4) and missed the new Phase 8c-ii button.
- `getting-started.md` claimed `.NET 8 runtime` — corrected to `.NET 10` (migration shipped 2026-04-13).
- `antenna-switching.md` gave a vague "Menu > Antenna" path — actual is Slice > Antenna > RX Antenna / TX Antenna (3 levels).
- `settings-profiles.md` said "appropriate menu item" for Settings — actually Tools > Settings.
- "Home region" terminology scrubbed everywhere — file rename + 5 prose occurrences + 3 section headers. Memory `project_jjflexible_home_terminology.md` captures the rule (always "JJ Flexible Home" or "JJ Flexible Radio Access Home", never "home region").

**Code-side findings flagged** (not fixed inline — appropriate for Sprint 29 scope):

- `ScreenFieldsPanel.xaml.cs:435,461` — plural-slice speech bug ("1 slices active" instead of "1 slice active"). Two-line ternary fix.
- `globals.vb:619-622` — `DirectCW` flag set by leader keys but never read; `MainWindow.xaml.cs:2851,2867` `SentTextBox_PreviewKeyDown`/`PreviewTextInput` are stubs with `Phase 8.4+`/`Phase 8.6` TODO markers. CW direct-keying mode is un-wired in the WPF port.
- Squelch Level field announces new value when adjusted with squelch off, but visible field stays "---" (no live refresh of the placeholder while squelch is off).
- `Ctrl+Shift+H` for Command Finder — claimed by docs (until today), not actually wired. Either add the binding (one line in KeyCommands.cs ~line 978) or accept that Ctrl+/ is the only path.
- pandoc HTML conversion produces double H1 per page (filename-derived `<h1 class="title">` plus the markdown's own `# Title`). Cosmetic at worst.

**End-of-day artifacts:**

- Debug build `4.1.16.154` produced earlier today, archived to NAS at `\\nas.macaw-jazz.ts.net\jjflex\historical\4.1.16.154\x64-debug\`. A second build at the seal commit follows this Agent.md write.
- Memory backup ran (NAS `historical\memory\`).
- Private-docs backup ran (NAS `historical\private\`).
- Daily Dropbox publish runs after the seal-commit rebuild.

**Memory state additions today:**

- NEW: `feedback_user_facing_prose_voice.md` — drafting voice rules for user-facing help (articles, complete phrases, abbreviation expansion, hear-not-land framing, "you will" tense, explain-why, specificity in headings). Updated mid-day with Noel's sample-edit lessons.
- NEW: `project_jjflexible_home_terminology.md` — "JJ Flexible Home" is correct, "home region" is wrong.
- UPDATED: `project_customize_home_vision.md` — Phase 10 (Network tab progressive disclosure) added as 4.1.18 companion scope.
- UPDATED: `MEMORY.md` — two new pointers added.

**CLAUDE.md drift to address tomorrow:**

The end-of-day ceremony in CLAUDE.md should grow a "verify the build compiles cleanly" step (proposed earlier today). The `JJFlexRadioReadme.htm` orphan that broke this morning's first build was caused exactly by a docs-only seal commit shipping a project-file break un-noticed. The fix is a one-step addition to CLAUDE.md's seal procedure (run `dotnet build` before committing the seal). Not blocking; flagging for tomorrow's work.

**Plan for tomorrow:**

1. Phase 9: build the combined 4.1.17 test matrix (covers Sprints 26+27+28 organised by user flow). Drafting work first, then execute against Don's 6300 and Noel's own rig.
2. Optional: pick off one or two of the small code-side findings (plural-slice fix, Ctrl+Shift+H binding) if there's budget.
3. CLAUDE.md drift update (the build-verify seal step).
4. Help-doc review by Noel — he deferred this to "in a while," so a re-read of the audited content is on his queue at his pace.

## 2026-04-22 end-of-day seal: changelog editorial pass + Sprint 29 scope captured + Customize Home vision + code-signing locked

**No code commits today.** Today was all documentation, memory capture, and planning. Branch `sprint28/home-key-qsk` has the same commit head as 2026-04-21; Sprint 28 phases 8c-ii through 11 remain for tomorrow. Memory + private docs backed up to NAS (`memory-20260422-223433.zip`, `private-20260422-223438.zip`). No Dropbox daily promotion today since no new debug build was made — the 4.1.16.139 daily from 2026-04-21 is still current.

**Changelog editorial (5 passes on `docs/CHANGELOG.md`)**:

- Pass 1 — structural: H1 `{#top}` anchor, version TOC at top with all 21 versions linked, kebab-case `{#foo}` anchors on all section headers in 4.1.17, per-section "Return to version headlines" links, end-of-version "Return to top · Jump to versions" links. Fixed broken `(#CWTextBoxes  go away` paren and `#panel` / `PanelNav` mismatch.
- Pass 2 — prose editorial on 4.1.17: stripped all 7 `****Claude:` meta-notes, fixed ~30 typos, rewrote broken grammar in intro / Escape headline / Network bullets / Port Safety paragraph / Thanks attribution, restructured kitchen-sink bullets to noun+state phrasing per CLAUDE.md rule.
- Pass 3 — `JJ Flex` → `JJ Flexible` normalization (13 substitutions across doc). Line 100 reverted to "older JJ Flex patterns Jim Shaffer set up" for historical accuracy per Noel's call. `JJFlexRadio` internal binary name preserved everywhere.
- Pass 4 — Jim's legacy changelog moved to its own file `docs/CHANGELOG-legacy.md` (versions 1.2.1 through 3.1.12.25, converted from HTML to markdown, preserved as authored). Main CHANGELOG's version TOC links to it.
- Pass 5 — CLAUDE.md updated with a "Keyboard Audit — Definition of Done for key-map changes" section, formalizing the audit discipline as Phase 5 step 9 of the sprint lifecycle.

**Sprint 29 scope captured (three Don feedback items, all target 4.1.17 via Plan B bundle)**:

- `project_sprint29_tune_redesign.md` — "tuning unity" (Noel's canonical name): kill `C` modal toggle, `Up/Down` = coarse, `Shift+Up/Down` = fine, step presets split into two named values. Audio gain / headphones / line-out hotkeys all removed, their values now live as fields in the Audio expander. Net: 1 hotkey pair added, 5 removed, 1 mode deleted.
- `project_sprint29_rit_xit_adjust_mode.md` — RIT/XIT scale-adjust mode. `R` / `X` still toggle, but while focused on the RIT or XIT toggle field, digits `1`-`4` enter scale-adjust at 1 Hz / 10 Hz / 100 Hz / 1 kHz. Exit is focus-bound (leave the field) or `Escape` / `0` / toggle off. Announced enter + descending-chirp exit. Third application of the sticky-but-announced modal pattern (after filter edge grab).
- Memory index updated in MEMORY.md.

**Customize Home captured as second signature flagship (Sprint 30+)**: `project_customize_home_vision.md` — user-configurable Home field layout, reorderable list with show/hide checkboxes, locked Frequency+Slice as anchors, per-operator storage (Flex-only for v1; per-radio-family extension deferred until non-Flex support lands). Targets 4.1.18 with its own release headline.

**Code signing + domain milestones locked**:

- Azure Trusted Signing decided as the signing path ($9.95/mo for 50k signatures; EV-class reputation treatment without hardware token). Build-installers.bat will gain an AzureSignTool step. Code-signing memory updated with concrete plan + driver-signing research flagged for future non-Flex virtual audio.
- `jjflexible.radio` domain acquired 2026-04-22 (DNS/VPS setup deferred). Updater-vision memory updated to reflect domain no longer a blocker.

**Accessibility-end-to-end correction (feedback memory)**: `feedback_accessibility_is_end_to_end.md`. I proposed "Flex users lean on SmartSDR's CAT/DAX as interim" for the virtual-driver question; Noel course-corrected. Interim proposals must pass the end-to-end accessibility test — "functional but inaccessible to set up" is a broken feature, not a fallback. Driver work is a first-class commitment, not deferred-maybe.

**Updater-driven release cadence strategy** (one-liner added to MEMORY.md under updater-vision pointer): once the updater ships in Sprint 29, release friction drops, which enables faster release cadence for small bundles. No standalone memory; the updater entry captures it.

**Keyboard reference proofread**: Noel wrote a new "JJ Leader Key" explanation in `docs/help/md/keyboard-reference.md`; I did a typo/grammar pass preserving voice (contortionist metaphor, "cute little descending tone," "secret cheat code" all intact). Terminology normalized: the *key* is `Ctrl+J` (a.k.a. "the JJ key"); the *mode* is "the JJ layer" or "layered command mode."

**Plan for tomorrow**:

1. **Sprint 28 testing** (main task). Phases 8c-ii, 8d, 9, 10, 11 remaining; Phase 9 is the combined 4.1.17 test matrix execution against Don's radio / Noel's own rig.
2. **A couple more Sprint 28 coding-phase items** (Noel mentioned, didn't specify — will pull up tomorrow).
3. **Keyboard shortcut audit (backfill)**: grep `KeyCommands.vb` + menu builders against `docs/help/md/keyboard-reference.md` to find missing/stale/mismatched bindings. One-time backfill; going forward, the new DoD in CLAUDE.md keeps it in sync. Noel wants this file to stay **user-facing** (skip dev/debug bindings).
4. **SmartCAT / DAX accessibility self-test** (Noel, 30 min at his leisure): install SmartCAT+DAX via Flex's installer with NVDA on, document what's accessible vs. what isn't. Output drives whether the non-Flex driver arc is urgent or can stay loosely scheduled.
5. **VB-Audio commercial licensing email** (Noel, at his leisure): ask about private-label redistribution, accessibility-nonprofit framing, small-scale pricing tiers. Prep for the eventual wrap-a-virtual-audio-driver path when non-Flex support arrives.
6. **Changelog final review** (Noel): read-through of 4.1.17 section end-to-end to spot anything the editorial passes missed before 4.1.17 cuts.

**Sprint plan targets / bundle shape (Plan B)**:

- Sprint 28 + Sprint 29 → ship together as `v4.1.17` ("The Make Yourself at Home Edition")
- Customize Home (Sprint 30+) → ships as `v4.1.18` with its own release headline

**CLAUDE.md drift check:** I added the keyboard-audit DoD section today — CLAUDE.md is fresh, no drift to flag.

**Memory state additions/updates today**:

- NEW: `project_sprint29_tune_redesign.md` (tuning unity)
- NEW: `project_sprint29_rit_xit_adjust_mode.md`
- NEW: `project_customize_home_vision.md`
- NEW: `feedback_accessibility_is_end_to_end.md`
- UPDATED: `project_code_signing_cert_milestone.md` (Azure Trusted Signing concrete plan + driver-signing future)
- UPDATED: `project_sprint29_updater_vision.md` (domain acquired, code-signing cross-link)
- UPDATED: `MEMORY.md` (three new pointers + one cadence note)

## 2026-04-21 end-of-day seal: Sprint 28 large chunk landed, 4.1.17 changelog drafted, cursor routing investigation concluded

**Daily debug build 4.1.16.139** zipped to NAS `historical\4.1.16.139\x64-debug\` and promoted to Dropbox top-level as `JJFlex_4.1.16.139_x64_daily.zip`. Memory + private docs backed up to NAS. Not broadcast to testers (no `--publish` used; this is the end-of-day seal, not a tester distribution).

**Today's total commits on `sprint28/home-key-qsk`:** ~22 commits covering Phase 1 through Phase 8c-i plus extensive sub-phase tuning from runtime testing. Key landed work:

- **Phases 1-7 (DoubleTapTolerance primitive, filter-edge migration, Escape-collapse + 3 earcons, Home rename, keystroke deconflict, RequireOperatorPresence + port-forward Apply gate)** — core Sprint 28 feature work.
- **Phase 3.1-3.4** — earcon audibility tuning (synthesized gavel → two-tone replacement after Noel reported inaudible), focus-order fix for Escape-collapse, Space-re-expand via inner ToggleButton focus.
- **Phase 3.5-3.6** — Ctrl+Tab reclaimed for expander navigation (action toolbar disabled, redesign deferred to post-28 per memory `project_action_toolbar_redesign.md`); CW send/receive text boxes now hidden in non-CW modes.
- **Phase 3.7** — Home sub-field announcement on focus landing, verbosity-scaled per Noel's spec.
- **Phase 3.8** — Gavel redesigned to two-tone PlayToneSequence (1200 Hz → 400 Hz over 500ms) after the synthesized version was still inaudible.
- **Phase 3.9 / 3.9.1-3.9.4** — Squelch and Squelch Level added as Home fields with Q universal hotkey; placeholder "---" when squelch off; speech/braille channel-split extended to new fields; step-name announcement gated when RIT/XIT is off.
- **Phase 3.10-3.12** — cursor routing investigation (diagnostic hook, Ctrl+Shift+B BrailleStatusEngine toggle, peer swap experiment, IsReadOnly=False experiment). Conclusion: vanilla WPF + NVDA doesn't support cursor routing to our custom SilentTextBox-based FreqOut. Experiments reverted; Sprint 29 will build a proper NVDA Python add-on + JAWS script (FSDN) with IPC to JJFlex. Retained: Ctrl+Shift+B toggle (user preference, useful regardless of cursor routing).
- **Phase 8a** — six new help docs (home-region, escape-collapse, settings-double-tap-tolerance, squelch-home, cw-text-boxes, port-forward-ownership) + keyboard-reference.md updates + TOC + hhp updates. CHM grew from 43 to 49 topics.
- **Phase 8b** — 4.1.17 user-facing changelog "The Making Yourself at Home Edition" drafted covering Sprints 26+27+28. Noel will do voice/accuracy pass tomorrow.
- **Phase 8c-i** — CHANGELOG.md imported into CHM as "What's New" topic via build script. Top-level TOC entry right after Welcome. Single source of truth: edit CHANGELOG once, CHM regenerates.

**Phases remaining for Sprint 28 completion (tomorrow and after):**

- **Phase 8c-ii** — Help menu "What's New" item + About dialog version-link + "View What's New" button.
- **Phase 8d** — end-of-sprint help audit: cross-check existing docs for drift against Sprint 28 changes.
- **Phase 9** — combined 4.1.17 test matrix (organized by user-flow, covers Sprints 26+27+28) + execution against Don's radio / Noel's own rig.
- **Phase 10** — Network tab progressive disclosure with auto-select + off-switch. **LARGEST REMAINING PHASE** — substantive UX rework. Noel to decide tomorrow whether this lands in 4.1.17 or slips to 4.1.18.
- **Phase 11** — Sprint archive cleanup (move sprint 24 matrix, 25 matrix, 26 plan+matrix, 27 plan, 28 plan+matrix to `docs/planning/agile/archive/`).

**Plan for tomorrow:** Noel finishes changelog voice/accuracy pass; we land remaining phases with test-matrix execution at radio.

**Memory state additions today** (all captured to `~/.claude/projects/c--dev-JJFlex-NG/memory/`):
- `project_friction_tax_principle.md` — disabled users pay friction tax; minimize for core workflows
- `project_no_silent_phone_home.md` — JJFlex never sends data without explicit user action
- `project_sprint29_updater_vision.md` — updater + channels + firmware + support-package + deaf-blind accessibility + visible status + cursor routing via add-on. Substantial Sprint 29 scope captured with phase-by-phase detail
- `project_kenwood_590g_commitment.md` — Mark's commitment for TS-590G support; neuropathy as design axis
- `project_anti_patterns_from_blindcat.md` — 9-item anti-pattern checklist from competitor product, by behavior not name
- `project_code_signing_cert_milestone.md` — funding-gated, OV tier recommended
- `project_earcon_audibility_rf_environment.md` — design principle for earcons in ham noise context
- `project_action_toolbar_redesign.md` — Ctrl+Tab toolbar deferred to post-28 with design direction sketch
- `feedback_human_review_user_facing_prose.md` — AI-drafted prose gets human review; flag cross-product claims and voice passages
- `feedback_dont_name_competitor_in_repo.md` — BlindCat / Rus never appear in repo-tracked content; memory files can reference
- Plus updates to existing `user_learning_pm_through_project.md` (now covers architecture thinking emerging), `project_sprint29_updater_vision.md` (all Sprint 29 phases A-H detailed including cursor-routing investigation outcome), etc.

**JAWS dev guides staged for Sprint 29:** extracted Basic Scripting Guide (90 HTML chapters + CSS) + FSDN CHM organized under `C:\Users\nrome\JJFlex-private\jaws-dev-guides\` in `basic-scripting\` and `fsdn\` subfolders. Backed up to NAS as part of today's seal.

**CLAUDE.md drift check:** no stale guidance exposed today. The sprint plan + build scripts + release process references all still accurate. Action toolbar redesign memory captures the one deviation (Ctrl+Tab was reclaimed) but that's memory-tracked, not CLAUDE.md-tracked.

## Prior state — 2026-04-21: Sprint 28 Phases 1-7 committed, Phases 8-11 remaining

**Branch:** `sprint28/home-key-qsk` (branched off `sprint27/networking-config` which is code-complete but not yet merged to main). When Sprint 27 testing completes, Sprint 28 either rebases onto new main or merges through.

**Sprint plan:** `docs/planning/agile/sprint28-home-key-qsk.md` — 11 phases, serial execution, no worktrees.

**Phases committed on sprint28/home-key-qsk so far:**

- **Phase 1 (`973afda5`)** — DoubleTapTolerance setting primitive. New `Radios/AccessibilityConfig.cs` (enum + per-operator XML config following PttConfig pattern). Static `Current` accessor for UI-layer consumers. New Accessibility tab in Settings with 4-option radio group (Quick/Normal/Relaxed/Leisurely = 250/500/750/1000 ms), default Normal. Load wired in ApplicationEvents.vb at app startup.
- **Phase 2 (`a271dc78`)** — Filter-edge migration. One-line change at FreqOutHandlers.cs:1768 swaps hardcoded 300 ms for `Radios.AccessibilityConfig.Current.DoubleTapToleranceMs`.
- **Phase 3 (`d5662983`)** — Escape-collapse + 3 new earcons. Single Escape collapses focused group (focus to header); double Escape collapses all + returns to Home (FreqOut). Three new synthesized earcons: PlayExpand (400->1200 Hz chirp + band-pass noise sweep, 350 ms), PlayCollapse (mirror), PlayCollapseAll (140 Hz gavel with harmonic + noise attack transient + exp decay, 450 ms). Two new sample providers: BandPassNoiseSweepSampleProvider (biquad band-pass with sweep), DecayingGavelSynthesizer. Expanded/Collapsed events hooked on all 5 expanders — single source of truth for announcements and earcons.
- **Phase 4 (`e4c3d2c5`)** — Home rename. FrequencyDisplay accessibility names: "JJ Flexible Home" (outer) / "Home, frequency and VFO" (inner). KeyCommands F2 description: "Focus frequency display" -> "Go to Home". Internal class names (FreqOut/FreqOutHandlers) unchanged. Home-key-as-F2-equivalent deferred to Phase 8 or later (needs KeyDefs.xml investigation).
- **Phase 5 (`d468ac7c`)** — Keystroke deconflict. Pan triad moved from L/C/R letters to PgDn/Home/PgUp nav keys on Slice field. R is RIT toggle everywhere (all three home fields). X is XIT toggle everywhere. `=` is transceive on Slice and SliceOps (mnemonic: RX = TX). Home field unchanged (R/X were already correct). Loose unification honored — no gap-fill S/A/T on Home.
- **Phase 6+7 (`d4c079f1`)** — RequireOperatorPresence primitive + port-forward Apply guarded. Primitive in FlexBase.cs: `PresenceLevel.Passive` (checks GUIClient.IsLocalPtt) implemented; `PresenceLevel.ActiveChallenge` declared-but-NotImplementedException-stubbed for future firmware-upload work. `ApplyPortForwardButton_Click` wraps its commit path in `RequireOperatorPresence(Passive, ...)` plus a new `ConfirmPortForwardApplyDialog` (default focus on No) for defense in depth. Extracted commit body into `PerformPortForwardApply` helper.

**Phases remaining (not yet started):**

- **Phase 8** — Help docs for Home / Escape / double-tap tolerance / ownership gate; 4.1.17 changelog covering Sprints 26+27+28; What's New CHM integration; Help menu + About dialog wiring to changelog anchor; end-of-sprint help audit.
- **Phase 9** — Combined 4.1.17 release test matrix (user-flow organized, covers Sprints 26+27+28).
- **Phase 10** — Network tab progressive disclosure with auto-select + off-switch. LARGEST remaining phase. Auto-select rules (pick lowest tier that passes test). User-toggleable "Automatic network discovery" setting. Test / Copy report / Save report always visible in basic view.
- **Phase 11** — Sprint archive cleanup: move sprints 24-28 plans and test matrices to `docs/planning/agile/archive/`.

**No runtime testing yet.** All 7 commits built cleanly (0 errors; warning count stable around 1424). Earcon synthesis code is theoretically sound but hasn't been ear-tested. Port-forward Apply gate's IsLocalPtt behavior needs a Don's-radio test (local operator path + remote SmartLink path). AccessibilityConfig persistence needs a round-trip test.

**Smoke-test punch list for when Noel is at the radio:**

1. Open Settings > Accessibility. Verify new tab exists; four tolerance options announce correctly; change to Relaxed, OK the dialog, reopen Settings — Relaxed should still be selected.
2. Open a ScreenFields group (e.g., DSP via Ctrl+Shift+N). Press Escape — group should collapse, expand earcon plays during expand, collapse earcon plays during Escape, focus should land on DSP header. Press Escape twice quickly (within tolerance) — gavel earcon plays, all groups collapse, focus returns to Home, "All panels collapsed, home" speaks.
3. On Home (F2), press R — RIT should toggle. Press X — XIT should toggle. On Slice field, press R — RIT toggles (not pan right). Press X — XIT toggles (not transceive). Press `=` — transceive. Press PgUp/PgDn/Home — pan right/left/center.
4. On Don's radio via SmartLink (remote): open Settings > Network > Apply. Denial speech expected: "Cannot change SmartLink port settings. You must be the primary operator at the radio." No dialog, no action. On local-at-radio operation: confirmation dialog shown, No is default focus, must Tab to Yes to commit.

If earcons sound wrong (too loud, phase issues, biquad artifacts), flag for fixes before proceeding to Phase 10.

## Prior state — 2026-04-20 sealed: Sprint 27 code-complete + cross-sprint docs audit landed; daily 4.1.16.105 on Dropbox

**Day closed 2026-04-20 ~20:21** (re-sealed after a NOTES-encoding fix bump from 105 → 108).

Dropbox top level holds `JJFlex_4.1.16.108_x64_daily.zip` + `NOTES-daily.txt` (UTF-8 with BOM — em-dashes render correctly in Notepad now). Covers:
- Sprint 27 networking (A/B/C/D/E/F) — code-complete, NOT live-tested. Tester warning included in the notes.
- Cross-sprint help-docs audit — 12 new + 2 updated help topics, CHM grew 77 KB → 101 KB.
- 88 unit tests (up from 14 at Sprint 26 start — 6.3x growth).
- Build-pipeline improvements: Debug builds now refresh CHM via `build-debug.bat` hook; `build-help.bat` delayed-expansion fix.

Backups snapshotted: memory + private docs to NAS `historical\memory\memory-20260420-201720.zip` + `historical\private\private-20260420-201721.zip`.



**Sprint 27 Track A landed 2026-04-20** in four commits on `sprint27/networking-config` (branched off `main` which now includes Sprint 26):

- **A.0 (`28329f23`)** — pre-design audit findings in the sprint plan; revised scope of A.2 + A.3 based on existing codebase (Network tab already existed; FlexLib has no pre-connect port API).
- **A.1 (`f9ba1e40`)** — `SmartLinkAccount.ConfiguredListenPort` (nullable int) + JSON round-trip + `SmartLinkAccountManager.{Get,Set}ConfiguredPort` + `IsValidPort` helper + 13 new tests (all 27 green).
- **A.2 (`a66012f3`)** — auto-apply on connect: `FlexBase.ApplyAccountPortPreferenceIfAny()` invoked from post-connect success branch for `RemoteRig`-only. Silent no-op when no account, no preference, or radio already matches.
- **A.3 (`b49cb750`)** — existing Network tab wired to per-account persistence + new Test button (local validation only — range + common-conflict blocklist) + Tier 1 framing prose. All new UI announcements route through the existing `NetworkCurrentStateText` live region + `ScreenReaderOutput.Speak`.

**Debug build 4.1.16.79** archived to NAS `historical\4.1.16.79\x64-debug\`. No Dropbox publish — Track A has not been smoke-tested against a live SmartLink radio yet, so no tester broadcast.

**Smoke-test outstanding (non-blocking):** run against Noel's radio or Don's 6300 to verify auto-apply-on-connect actually pushes the saved port to the radio's firmware. Auto-apply is trace-instrumented so behavior is observable in the trace log when tested. Defects feed back into Track A as fix-forward; Phase 2 tracks (B/C/E/F) can start in parallel since Track A's auto-apply is a safe no-op when no preference is set.

**Current branch arc (sprint27/networking-config from main tip):**
1. Sprint 26 merge → main (commit `521c1831`) — prior session, 30 commits brought forward from `sprint26/connection-fix`
2. Sprint 27 Phase A.0 findings (`28329f23`)
3. Sprint 27 Phase A.1 persistence (`f9ba1e40`)
4. Sprint 27 Phase A.2 auto-apply (`a66012f3`)
5. Sprint 27 Phase A.3 UI wiring (`b49cb750`)
6. Sprint 27 Phase 1 rollup (this commit) — plan + Agent.md updates

**Execution mode:** Sprint 27 runs **serial on a single branch** (`sprint27/networking-config`) — no worktrees, no parallel CLI sessions. Noel's choice for this sprint; see `memory/project_sprint27_serial_execution.md`. Ignore the sprint-plan template language about "parallel after A".

**Track C landed 2026-04-20** in four commits. C.0 findings revised plan scope (FlexLib NetworkTest exposes only 5 booleans, no NAT-type/backend/auth signals), C.1 added `NetworkDiagnosticReport` + `ToMarkdown()` (9 tests), C.2 added `NetworkTestRunner` with TTL cache + dedup + timeout (13 tests), C.3 wired invocation points (a) post-connect fire-and-forget + (b) Settings "Test _network" button. Scenario (c) post-disconnect heuristic deferred to Track D. `WanSessionOwner` now owns a `NetworkTestRunner`; `IWanSessionOwner` grew three pass-through members.

**Track B landed 2026-04-20** in five commits. B.0 resolved Q27.1 in favor of native Windows UPnPNAT COM. B.1 added `UPnPPortMapper` with internal `IUPnPBackend` seam + `ComUPnPBackend` production impl (15 tests). B.2 added Tier 2 persistence + Settings UI. B.3 wired map-on-connect (private-IP gate) + release-on-disconnect.

**Track F landed 2026-04-20** in three commits. F.0 replaced B.2's `UPnPEnabled` bool with a three-state `SmartLinkConnectionMode` enum (cumulative tier semantics; JSON string-name serialization; ordinal monotonicity pinned by test). F.1 replaced the Tier 2 checkbox with an accessible three-option radio group (per-item explanations inline; mode-change speech; Tier 2/3 gated on Tier 1 port validity). F.2 threaded `holePunchPort` through `IWanServer.SendConnectMessageToRadio` + `IWanSessionOwner.ConnectToRadio`; `FlexBase.sendRemoteConnect` derives the port from the active account's ConnectionMode (AutomaticHolePunch + configured port → that port; otherwise 0). Zero new server infrastructure — Flex's SmartLink coordinates the hole-punch on its side.

**Track E landed 2026-04-20** in two commits. Three networking help docs in `docs/help/networking/` + vbproj wiring to copy to build output.

**Track D landed 2026-04-20** in four commits (the integrator):
- D.0 + D.1 added `SessionStatusMessages.ForStatusRich` + `HelpDocFor` (17 new tests), `MostRecentNetworkReport` on `IWanSessionOwner` / `NetworkTestRunner`, `DiagnosticVerbosityPreference` global toggle, and wired MainWindow's status handler to use the richer form.
- D.2 hooked a post-disconnect NetworkTest auto-run on the transition into Reconnecting (scenario c from Track C, deferred to D).
- D.3 added Copy report / Save report / Help buttons + Verbose checkbox to Settings > Network. Copy uses `Clipboard.SetText(report.ToMarkdown())`. Save uses `SaveFileDialog` with timestamped default filename. Help opens the relevant help doc via `ProcessStartInfo(UseShellExecute=true)`. Verbose flips the preference immediately (no Apply).

**Sprint 27 is code-complete on all six tracks.** Remaining sprint close-out items: test matrix, live smoke test against Noel's or Don's radio, release candidate.

**Debug build 4.1.16.101** archived to NAS `historical\4.1.16.101\x64-debug\`. Dropbox still untouched.

**Next session target:** broader cross-sprint help-docs audit per Noel's 2026-04-20 ask. Survey Sprint 25 + Sprint 26 Phase 8 + pre-existing gaps, propose prioritized list, draft or flag per `memory/feedback_docs_ship_with_features.md` (new working-practice rule: features ship with docs or an explicit 'needs doc' flag from now on).

---

## Prior state — Sprint 26 COMPLETE, merged to main

**Sprint 26 merged to `main` 2026-04-20** (commit `521c1831`, `--no-ff` so the branch history is preserved as a visible bump). All 8 phases landed; full solution builds clean; 14 unit tests green at merge; Sprint 27 Track A added another 13 tests on top.

**Sprint 26 status (2026-04-20):** all 8 phases landed on `sprint26/connection-fix`. Full solution builds clean, 14 unit tests green, no runtime smoke test yet (deferred to Sprint 27 final phase's help audit + pre-release testing).

**Sprint 26 commit arc (23 commits ahead of main):**
- Phase 0: 3 commits (audit findings, R1/R2/R3 decisions, R3-revised)
- Phase 1: 6 commits (interfaces, adapter, session owner, coordinator, sink+tests, harness)
- Phase 2: 3 commits (scaffolding, migration, cleanup)
- Phase 2.5: 2 commits (Symptom 3 fix landed as pre-work; Symptom 6 snapshot-at-subscribe; Symptoms 1/2/4/5 deferred to smoke-test investigation)
- Phase 3: 1 commit (SessionStatusMessages + MainWindow status-change announcements)
- Phase 4: 1 commit (dead wan field + unused Preserve/Restore methods deleted; GUIClient-race band-aids kept per R4)
- Phase 5: 1 commit (test matrix document — TM-1..TM-9 + TM-M1..TM-M5 + TM-R1..TM-R6)
- Phase 6: 1 commit (CW prosign bracket syntax, PlaySignoff, WPM soft cap 60)
- Phase 7: 1 commit (CW dialog investigation — unreachable surface, no-op exit)
- Phase 8: 4 commits (8a SliceOps letters + modern checkbox field parity, 8b Tuning submenu expansion, 8c mode-change announcement, docs + TODO follow-ups)

**Branch discipline:** vbproj holds at 4.1.16 (4.1.17 reserved for post-Sprint-27 release per R3 revised). No external publish, no tag, no Dropbox touch — all internal branch work.

**Next session target:** Sprint 27 kickoff — networking overhaul Tiers 1+2 (port-forward UI, NetworkTest integration, UPnP configuration). Plan doc at `docs/planning/agile/sprint27-barefoot-openport-hotel.md`. Sprint 27's final phase will be the comprehensive help audit covering Sprint 25 legacy + Sprint 26 Phase 8 Jim-parity + Sprint 27 networking features in one pass (per 2026-04-20 scope decision relocating the audit out of Sprint 26).

**Follow-ups captured in JJFlex-TODO.md during Sprint 26 (2026-04-20):**
- BUG-063: `build-installers.bat release` silently skips NAS installer publish (Sprint 27 slip-in)
- FEATURE: Auto-updater with multi-channel + silent download + optional relaunch (ungated, GitHub-only path works)
- FEATURE: On-demand tuning-key announcement hotkey (Phase 8 follow-up)
- FEATURE: First-focus frequency-home orientation announcement (Phase 8 follow-up)

---

## Prior state (pre-Sprint-26) — 4.1.16 SHIPPED

**Status:** Sprint 25 complete, merged to main, 4.1.16.44 released end-to-end on 2026-04-19.

**Release artifacts:**
- **GitHub:** https://github.com/nromey/JJFlex-NG/releases/tag/v4.1.16 with both x64 + x86 installers
- **NAS:** `\\nas.macaw-jazz.ts.net\jjflex\historical\4.1.16.44\` (installers + exe + pdb) + `stable\` (current release)
- **Dropbox top-level:** `Setup JJFlex_4.1.16.44_x64.exe` + `_x86.exe` (stable) + `JJFlex_4.1.16.44_x64_daily.zip` + `NOTES-daily.txt` (debug daily)
- **Dropbox `old\`:** prior 4.1.15.1 stables archived
- **NAS memory backup:** `historical\memory\memory-20260419-231459.zip`
- **NAS private docs backup:** `historical\private\private-20260419-231500.zip`
- **Git:** `main` at `e60cc2c0` (merge of `sprint25/track-a`), tag `v4.1.16` pushed to origin

**Stashes in repo (non-blocking, expected artifacts):**
- `install.nsi` + `deleteList.txt` — auto-regenerated by `build-installers.bat`, stashed to keep working tree clean across builds. Will regenerate on any future build; nothing to commit.

## Sprint 25 → 4.1.16 Close-out Session (2026-04-19)

All-day session: mode-key deconfliction implementation + 4.1.16 user-facing changelog (comprehensive — five sprints of work plus .NET 10 LTS migration) + Sprint 26 plan amendments (BUG-062 MultiFlex fix absorbed as Phase 2.5 + CW processor engine as Phase 6 + CW dialog mode-gating as Phase 7) + CLAUDE.md refresh (user-state-timeline rule, SDK-generated version attrs correction, end-of-day "done developing" workflow + backup hooks) + five post-foundation FEATURE captures in JJFlex-TODO (MultiFlex three-channel awareness, per-slice VFO toggles Jim parity, screen reader auto-detect + sighted support, PC NR fleshout Sprint 27 Track F, Ctrl+F commit band-segment context) + full release sequence (commit, merge --no-ff to main, tag v4.1.16, push origin, build installers + publish NAS + Dropbox, GitHub release, daily debug + backup scripts).

Testing that backed the release:
- Sprint 25 matrix fully green except Phase 10-11 positive NR half (deferred — hardware blocked on 8600 boxed + Justin 8400 SmartLink unverified).
- BUG-062 MultiFlex bugs discovered during Don co-test with WA2IWC on 6300 via SmartLink. Six symptoms logged together (slice visibility, client list propagation, kick-refuse, connect timeout, event announcement misfires, wrong-callsign-on-disconnect). All assigned to Sprint 26 Phase 2.5 rewire through the new coordinator.

Design captures logged during the session:
- MultiFlex three-channel awareness (Status Dialog + slice selector toggle + audio announcement)
- Per-slice VFO toggles (Jim parity restore with accessible nav)
- Screen reader auto-detect + Narrow/NVDA/Narrator peer options for AMD/RP-transitioning operators
- PC NR fleshout scope (RNN modes + strength + spectral presets + noise capture + A/B)
- Ctrl+F commit band-segment speech
- Flexibility principle memory captured (`project_flexibility_principle.md`) — a specific anti-pattern from another blind-operator radio app named as the guiding example (details in the memory file, kept local/non-public).

## Session 2026-04-17 (evening) — Phases 8-10 pass + CW session bookending + end-of-day seal

Final session of the day after Toastmasters. Noel returned with a Focus 40 Blue braille display, and we closed out Sprint 25's functional testing arc.

### Phase 8 — braille status line passes on Focus 40
- Noel's first real-life "watch frequencies tick up and down" moment on a braille display — he called it out as unprecedented in ham radio accessibility. Meaningful.
- Two fixes surfaced during testing:
  - `ApplySettingsChanges` (MainWindow) now re-applies braille config at runtime. Settings toggle didn't take effect without reconnect because `_brailleEngine` fields were only applied at PowerOn.
  - `BrailleStatusEngine` timer interval dropped 1s → 500ms, and the `_lastPushed` change-detection guard removed so we always push on tick. NVDA's own caret/focus-tracking braille redraws were reclaiming the display during stable state; now we continuously reassert ownership.
- Verified on SmartLink to Don's 6300: "3.730 lsb sm8" held steady and updated live to sm9 as NYC urban noise floor moved.
- Memory saved: `project_multi_braille_output_vision.md` — Dot Pad + Focus 40 two-channel tactile output for the waterfall sprint. The three-channel stack (audio + linear braille + graphics braille) makes JJFlex "an audio game with utility" per Noel's framing. MEMORY.md index updated.

### Phase 9 — Ctrl+Tab action palette passes
- Directed test passed: Ctrl+Tab opens Actions dialog, arrow-nav through items (ATU Tune, Start Tune Carrier [= Ctrl+Shift+T equivalent], Start Transmit, Speak Status, Cancel), Enter activates, Escape closes cleanly.
- Noel asked insightful design questions about the toolbar pattern and extensibility. Logged "Ctrl+Tab action palette expansion" as a FEATURE in JJFlex-TODO.md with candidate items (DSP toggles, band jump, audio workshop, mode cycles, etc.) and UX enhancement ideas (recent items, type-to-filter, grouped sections). The palette is the natural home for keyboard-native command discovery.

### Phase 10-11 negative NR gating — verified on Don's 6300
- Ctrl+J R/S/Shift+N each announce "not available on this radio" correctly.
- DSP menu/panel correctly hides RNN, NRS, NRF items on 6300.
- ANF (Ctrl+J A) works normally — positive path confirmed.
- Positive half (features visible on >6300) deferred pending 8600 unbox or Justin's 8400 SmartLink access. Marked in matrix.

### Phase 10 — CW prosign session bookending landed end to end
Big architectural arc. Current state: ham-flavored CW session narrative is complete:
- **AS** (wait / standing by) at connect-start — fires alongside "Connecting to X" speech.
- **BT** (break / ready to receive) at connect-complete — fires at end of MainWindow PowerOn CW setup.
- **CW mode name** alongside speech on mode change (verbosity gate removed — CW is now a parallel notification channel, not a speech-off replacement).
- **73 + SK** or **73 de JJF + SK** at app close depending on WPM (>=25 gets the callsign signature; below gets the short form). Bare SK never sent.

**Architectural moves to make this work:**
1. CW delegates (`PlayCwAS/BT/SK/Mode`) moved from PowerOn to MainWindow constructor. They only need `_morseNotifier` which is field-initialized. Solves the race where AS/BT fired with null delegates on first-connect.
2. User-scope CW config loaded from BaseConfigDir root at MainWindow construction. CwNotificationsEnabled + speed + sidetone set before any connect triggers AS.
3. PowerOn migrates CW settings (CwNotificationsEnabled, CwModeAnnounce, CwSidetoneHz, CwSpeedWpm) from per-radio config to root on every connect. Self-healing: historical per-radio-only CW config propagates to root automatically, so users with CW enabled don't have to open Settings once to seed root.
4. `ApplySettingsChanges` now re-applies CW config at runtime (same pattern as braille). Settings OK takes effect without reconnect.
5. `NativeMenuBar` save-to-root extended with CW fields alongside TuningHash and TypingSound. Ensures Settings OK keeps root authoritative.
6. BT moved from FlexBase connect-success sites (both TryAutoConnect and manual Connect) to end of MainWindow PowerOn CW setup. Semantically correct moment: radio up, delegates live, config applied.
7. AS added at both "Connecting to X" speech sites: FlexBase auto-connect path + RigSelectorDialog manual path.
8. Mode-change Morse verbosity gate removed — CW plays regardless of speech state.
9. `PlayCwSK` smart-branches on speed (>= 25 WPM → "73 de JJF" + prosign SK; below → "73" + prosign SK).
10. ApplicationEvents.vb shutdown SK wait bumped 2s → 5s to cover the richer farewell at lower WPM.

**Known follow-ups captured:**
- **BUG-061** extended with Noel's leading theory: "73 SK" running together because PlayString + PlaySK are two separate rendering passes through the FIFO queue. Inter-utterance gap is queue/mixer latency, not PARIS 7-unit word space. Fix path: single-pass sequence API or prosign bracket syntax in strings (`"73 <SK>"`).
- **FEATURE: Dedicated CW processor/engine** — first-class subsystem for PARIS + alternative timing, unclamped WPM, Farnsworth, single-utterance rendering, prosign bracket syntax, envelope shaping, weight/rhythm controls. Foundation for CW practice mode + on-air keying + iambic/bug/straight-key. One refactor unlocks multiple features.
- **FEATURE: CW message UI modernization** — Jim-era `CWMessageAddDialog` + `CWMessageUpdateDialog` + related VB files. Noel noted seeing them used only for send-CW-over-remote, but Jim may have had other purposes. Investigation first, then likely move to Ctrl+Tab action palette ("Send Message" with mode-aware backend) rather than simple mode-gated hide — preserves Jim's generality.

### Commits landed today on `sprint25/track-a`

1. **`c27d5f15`** — Sprint 25 Phase 3 fixes: ValueFieldControl + NRL reapply workaround + memory backup (morning session)
2. **`82fe262b`** — Sprint 25 Phases 4-7 fixes: Off-at-bottom dropdown + user-scope Settings load + private docs backup (late morning)
3. **`20f05252`** — Sprint 25 Phase 8 braille fixes: Settings-time re-apply + always-push at 500 ms (evening)
4. **`61210edc`** — Sprint 25 Phase 10 CW prosign session + user-scope CW + NR gating verified (evening)
5. **`daef92b7`** — JJFlex-TODO: three CW design captures from end-of-day review (evening)

### What's left for Sprint 25 close-out

- **Phase 22 — user-facing changelog** for 4.1.16. All tested features confirmed green; ready to write.
- **Phase 10-11 positive half** — NR providers visible on >6300. Blocked on hardware (8600 boxed per `project_8600_unbox_firmware_trigger.md`; Justin's 8400 SmartLink needs verification).
- **Sprint 25 → main merge** — after changelog.
- **Flex upstream bug email** — two bug reports ready (`flexlib-discovery-nre-report.txt` has both Discovery NRE + NRL mode reapply; `flexlib-email-cover.txt` is the cover letter).
- **Tomorrow's starter investigations:**
  - SmartLink multi-account — never checked off in matrix, needs Don + Justin.
  - Full integrated CW session smoke test — AS → BT → mode change → SK in a single uninterrupted run.
  - Investigation of Jim-era CWMessage dialogs before deciding between mode-gating vs. Ctrl+Tab action palette modernization.
  - BUG-061 CW timing pass: single-utterance sequence API so "73 SK" gaps are PARIS-compliant.

### CLAUDE.md drift noted

Still carrying from previous session + extended today: CLAUDE.md's "Nightly Debug Builds" section documents the `debug\` subfolder publish (via `build-debug.bat --publish`) but doesn't mention:
- The end-of-day top-level daily workflow that `publish-daily-to-dropbox.ps1` now drives.
- The memory backup + private docs backup hooks that ride the daily publish.

Low-impact doc gap. Worth a short CLAUDE.md update in a future session — not fixing tonight.

## Session 2026-04-17 (late morning) — Phases 4-7 pass + Off-at-bottom polish + private backup + user-scope Settings fix

Continuation of the morning session. Noel paused for a Toastmasters meeting, leaving me to work autonomously.

### Phases 4-7 walked through and passing
- **Phase 4:** earcon mute (Ctrl+J Shift+T) pass; connection menu rebuild already verified prior.
- **Phase 5:** focus-return context announcement pass.
- **Phase 6a:** access key announcements pass on NVDA + JAWS across Radio Selector, Settings, and spot-checked dialogs.
- **Phase 6b:** DSP menu checkbox state pass (Legacy NR, NB, ANF, meter tones — all report On/Off correctly).
- **Phase 7:** typing sounds + easter eggs pass. Two polish items surfaced during testing:

### Phase 7 polish: "Off" pinned to bottom of dropdown
- User observation: when easter-egg modes (Mechanical / DTMF) are unlocked, they appeared *below* "Off" in the Settings → Audio → Frequency Entry Sound dropdown. Preferred layout: Off always at the bottom.
- Fix in `SettingsDialog.xaml.cs` (both `PopulateTypingSoundCombo` and the save-side index mapping in `SaveSettings`). "Off" now added last; its index is computed as `3 + unlocks`, matching both directions of the read/save flow. No magic numbers remain on the save side pretending Off is fixed at index 3.

### Phase 7 polish: BOOM reset word recovery
- Noel designed the reset easter-egg word when he wrote the feature but never documented the plaintext — only AUTOPATCH and QRM ended up in `JJFlex-TODO-detailed.md`.
- Salt (`JJFlex-K5NER-73`) and hash (`3fc741118210...`) are in `CalibrationEngine.cs`. Plaintext was recoverable by brute-forcing a ham-themed wordlist — `ReverseBoomTone()` function name in the reset handler leaked the theme, and BOOM was on the first-pass candidate list.
- All three unlock words + salt + hashes + thematic design notes now documented authoritatively in `C:\Users\nrome\JJFlex-private\easter-egg-manifest.md` under a new "Unlock Reference" section. Future sessions look here, not at the sprawling TODO.
- `reference_private_docs.md` memory updated to point at the manifest as the unlock source of truth.

### Private docs NAS backup
- Paralleling the memory-backup work: new `backup-private-to-nas.ps1` at repo root, snapshots `C:\Users\nrome\JJFlex-private\` to `\\nas.macaw-jazz.ts.net\jjflex\historical\private\private-YYYYMMDD-HHMMSS.zip`.
- Hooked into `publish-daily-to-dropbox.ps1` tail alongside the memory hook. Both backups are non-fatal — if one fails, the daily publish still counts.
- Smoke-tested: 2 files (TODO-detailed + easter-egg-manifest) → 23 KB → `private-20260417-113711.zip`.
- `reference_private_docs.md` documents the backup location and cadence.

### Phase 7 discovered issue: unlock visibility gated on connection
- Noel reported that when disconnected, the Frequency Entry Sound dropdown showed only the three always-on modes (unlocks hidden). When he connected, the unlocks reappeared.
- Root cause in `NativeMenuBar.ShowSettingsDialog`: when `_window.OpenParms` was null (no radio), the reload branch was skipped and `audioConfig ??= new AudioOutputConfig();` kicked in with an empty TuningHash. The unlock handler had always been writing TuningHash to BaseConfigDir (root), but Settings wasn't reading from there when disconnected.
- **Fix:** refactor `ShowSettingsDialog` to resolve `rootConfigDir = handlers?.GetConfigDirectory?.Invoke()` (the VB-side BaseConfigDir, available regardless of connection). Three load branches now: connected loads per-radio + merges user-global from root; disconnected loads from root directly; no-handlers falls back to new config. On Settings OK, user-global fields (TuningHash + TypingSound) are always saved back to root, so root stays authoritative.
- This is a **minimum-viable fix** for Phase 7 acceptance. The broader architectural question — which fields in `AudioOutputConfig` should be user-global vs per-radio — is flagged for a later sprint. See `JJFlex-TODO-detailed.md` for the backlog capture.

### State when Noel returns

- **Phase 3-7 pass** in the matrix. Tasks #1-6 completed. Phase 8 (braille status line) task in-progress but not yet tested.
- **Fresh Debug build at 12:03** (exe + JJFlexWpf.dll). Noel should relaunch and verify two things when back:
  - Open Settings *disconnected* — Audio tab's Frequency Entry Sound dropdown should show Mechanical + DTMF options (unlocks are visible regardless of connection state).
  - Select Touch-tone (DTMF) or Mechanical keyboard, click OK, quit, relaunch (still disconnected), reopen Settings — selected mode should persist.
- **Not touched yet:** Phase 8 (braille), Phase 9 (action toolbar). Directed test steps for both have been prepped in conversation context.

### Backlog items captured (for `JJFlex-TODO-detailed.md`)
- **Earcon audibility under loud radio audio** — Don reports earcons sometimes unhearable; Noel agrees. Three-tier design: (1) raise AlertVolume ceiling above 100 for software amplification, (2) proper audio ducking when earcon fires, (3) better discoverability of already-existing separate-device routing for earcons vs radio audio.
- **User-scope vs per-radio config split** — `AudioOutputConfig` currently mixes user-scope fields (TuningHash, TypingSound, SpeechVerbosity, AlertVolume, CwNotificationsEnabled, EarconsEnabled) with per-radio fields (EarconDeviceNumber, MeterDeviceNumber). Today's minimum fix makes user-scope fields authoritative at root, but the file serialization still writes everything to both locations. Proper fix: split into `userPrefs.xml` (root) and `audioConfig.xml` (per-radio), migration on first load.

## Session 2026-04-17 (morning) — Phase 3 testing fixes + memory-backup wiring + NRL upstream report

Don't-publish commit. Testing-driven fixes landed on `sprint25/track-a`:

### Memory-backup to NAS historicals
- New script `backup-memory-to-nas.ps1` at repo root. Zips `C:\Users\nrome\.claude\projects\c--dev-JJFlex-NG\memory\` to `\\nas...\jjflex\historical\memory\memory-YYYYMMDD-HHMMSS.zip`. Memory gets its own sibling folder under `historical\`, not nested per-version (memory changes on its own cadence, not per build). Builds in local temp then copies to NAS (avoids partial files on network hiccups).
- `publish-daily-to-dropbox.ps1` now invokes the memory backup at the tail, after daily publish succeeds. Non-fatal if memory backup fails. So every "done developing" ceremony auto-seals a memory snapshot.
- Smoke-tested on first run: 38 files → 58,555 bytes → `memory-20260417-061401.zip` on NAS.
- Memory pointer added: `project_memory_backup_location.md`, indexed in MEMORY.md.

### Phase 3 testing — three testing-driven code fixes

Phase 1 + 2 already passed. Phase 3 testing surfaced three bugs:

1. **ValueFieldControl step hardcoded to 1** — `Settings → Audio` volume controls ignored the configured `Step = 5` because `OnPreviewKeyDown` hardcoded `AdjustValue(shift ? 5 : 1)`. Fix: honor `_step` (`ValueFieldControl.xaml.cs:135-145`). Up = Step, Shift+Up = 1 as fine-grain escape hatch.
2. **Surplus cold-Enter re-prompt** — pressing Enter when NOT in number-entry mode restarted number-entry with "Enter {label} value" prompt. Confusing on top of the digit-auto-enter path. Fix: removed the cold Enter case (`ValueFieldControl.xaml.cs:182-187`). Enter is now purely "confirm active entry."
3. **Legacy NR stops processing after DemodMode round-trip** — `Slice.NRLOn` flag reads back as true but firmware silently stops applying NR. Required three attempts to find a working client-side fix:
   - (A) Same-value re-send: ignored.
   - (B) Back-to-back off-then-on via queue: ignored (coalesced somewhere).
   - (C) Task.Run with 150 ms pre-delay then 500 ms mid-delay between off and on: works. Matches user manual uncheck-recheck cadence.
   - Implemented (C) in `FlexBase.cs` DemodMode propertyChanged handler (around line 2290). Fire-and-forget Task.Run with try/catch logging. Adds ~650 ms NR-off window on mode changes, acceptable given mode-change audio discontinuity.
   - RNN and newer NR providers do NOT exhibit this behavior, which is the hint that it's an older NRL-specific code path (FlexLib setter short-circuit or firmware-level coalescing).

### Upstream bug report

- Appended second bug section to `flexlib-discovery-nre-report.txt`. Structure mirrors the Discovery NRE writeup (Summary / Repro / What doesn't work / Likely location / Our workaround). No suggested fix — we don't have FlexLib source or firmware visibility to say where the coalescing lives.
- `flexlib-email-cover.txt` updated to "two bug reports" with concise summaries of each. The three quality-of-life asks (TLS knob, auto-TLS selection, unified tune event) are untouched.
- Filename `flexlib-discovery-nre-report.txt` is now mildly misleading (two bugs inside, not just Discovery) — noted for optional rename next time.

### Sprint 25 testing state

- Phase 1: done (hotkey, ding, slice clamp, status dialog)
- Phase 2: done (slice ops label, modern-mode freq nav)
- Phase 3: done (audio tab ValueFieldControls + DSP refresh on mode change, with the workaround above)
- Phase 4 onward: pending next session

Phase 10-11 (NR providers positive half) deferred: needs a >6300 radio. Noel's 8600 is boxed pending alpha or public firmware trigger (see `project_8600_unbox_firmware_trigger.md`); Justin's 8400 is a candidate once SmartLink access is confirmed (Justin is Mac-side — port forwarding is a router question, not a Mac question).

### Memory updates

- `project_8600_unbox_firmware_trigger.md` — 8600 stays boxed until new firmware drops; unbox + add firmware-upload mechanics to JJFlex in the same pass.
- `project_smartsdr_plus_tester_access.md` — Don and Justin both have paid SmartSDR Plus early access; tester pool covers subscription-gated features end-to-end.
- `project_memory_backup_location.md` — memory backup script and cadence.
- MEMORY.md index updated with all three new pointers; Testing Setup section now mentions Justin's 8400.

### What's still open

- **Earcon audibility design** (Don reports earcons sometimes un-hearable under loud radio audio) — logged in discussion as a Sprint 27-ish item. Three-tier sketch: raise AlertVolume max above 100 (software amp), proper audio ducking on earcon fire, better discoverability of the already-existing separate-device routing. NOT captured in JJFlex-TODO yet (deferred from this session).
- **Phase 4 onwards** — connection menu rebuild, focus-return context, access key announcements, menu checkbox state, easter eggs + typing sounds, braille status line, action toolbar, NR provider positive half (needs >6300 radio).
- **NRL mode-reapply bug report** — ready to send alongside the Discovery NRE writeup when Noel sends the email.

## Session 2026-04-16 (evening) — publish-daily rewrite + 4.1.16.33 daily published

End-of-dev-day seal. Two commits on `sprint25/track-a`:

1. **`cc870072` — `publish-daily-to-dropbox.ps1` made intelligent, always debug.** Old script was stuck in release-tier drift: it scanned `historical\<ver>\installers\` for `Setup JJFlex_*.exe` and renamed to `-daily.exe`. Memory (`feedback_daily_is_debug.md`) already said daily = debug, but the script hadn't been updated. Tonight's rewrite:
   - Computes expected version from HEAD: `base + (git rev-list --count HEAD + offset)`.
   - Looks for matching debug zip at `NAS\historical\<ver>\x64-debug\JJFlex_<ver>_x64_debug_*.zip`.
   - Match found → pure copy to Dropbox top level as `JJFlex_<ver>_x64_daily.zip` + `NOTES-daily.txt`.
   - No match + clean tree → auto-invoke `build-debug.bat`, re-scan, then copy.
   - No match + dirty tree → refuse and **list** the dirty files so the blocker is visible.
   - Purges prior daily zip/NOTES plus any stray `Setup JJFlex_*-daily.exe` left by the old release-oriented version (swept one tonight — `Setup JJFlex_4.1.16.1_x64-daily.exe` from 4/14).
   - Opt-in escape hatch: `-AutoCommit -CommitMessage "<msg>"`. Both flags required — we don't auto-generate commit messages. Stages `git add -A`, commits with the supplied message, then proceeds. Only for narrow "I know exactly what's dirty" cases.
   - `-NoBuild` skips the auto-build step for older-zip promotions.

2. **(commit above was the only code change tonight.)** The matching BUG-058 root-cause fix `a141d043` and the OpenParms/Callouts refactor TODO capture `3297317c` were the backlog that made `4.1.16.33` worth shipping as tonight's daily.

**Build + publish:** `publish-daily-to-dropbox.ps1` (no args) detected clean tree after the commit, expected `4.1.16.33`, found no matching NAS build, invoked `build-debug.bat`, cleanly built (55 s, 0 errors), archived to NAS `historical\4.1.16.33\x64-debug\`, then promoted to Dropbox top level. First real end-to-end exercise of the new flow — every branch of the logic ran clean on the first try.

**Dropbox top level after seal** (exactly what should be there):
- `JJFlex_4.1.16.33_x64_daily.zip` (21:32)
- `NOTES-daily.txt` (21:32)
- Old 4.1.15.1 stable installers (untouched)
- `CHANGELOG.md` (untouched)

**Minor drift noted for a future session:** `CLAUDE.md`'s "Nightly Debug Builds" section documents the `debug\` subfolder publish (via `build-debug.bat --publish`) but doesn't mention the end-of-day top-level daily workflow that `publish-daily-to-dropbox.ps1` now drives. Not fixing tonight — low-impact doc gap, worth a small CLAUDE.md update next session.

## Session 2026-04-16 (morning) — SWR-on-manual-carrier fix + build script gotcha

Don reported the SWR-after-tune feature from f08a1d52 wasn't speaking on his side. He uses Ctrl+Shift+T to key the carrier; an external rooftop manual tuner senses RF and matches; he releases Ctrl+Shift+T. He expected "SWR X.X to 1" — got silence.

Two commits landed on `sprint25/track-a`:

1. **`5582596c` — Fix SWR-after-tune silence on manual carrier (Ctrl+Shift+T).** Root cause: `MainWindow.ToggleTuneCarrier()` writes `RigControl.TxTune` directly. FlexBase's `TXTune` propertyChanged handler (`Radios/FlexBase.cs:2254`) only raises `FlexAntTuneStartStop` on the **rising** edge — the falling edge raises nothing. So the f08a1d52 announce path (which lives in `FlexAntTuneStartStopHandler`) never fired for manual carrier. Auto-ATU works because its OK event comes from `ATUTuneStatus` propertyChanged, a different code path. Fix: in `ToggleTuneCarrier()`'s tune-off branch (line 2141), call `AnnounceSettledSwrAfterTune(isFailure: false)` directly. Reuses the existing helper (settings gate, 200 ms settle, "SWR X.X to 1" wording). Covers both the Ctrl+Shift+T hotkey and the on-screen Tune toggle button (both route through `ToggleTuneCarrier`). Auto-ATU path unchanged.

2. **`0ad30b33` — `build-debug.bat`: use full path to Windows `find.exe`.** Yesterday's session note already flagged this as a known issue; this session it was actually blocking publish, so it had to be fixed. The working-tree-clean check at line 82 used bare `find /c /v ""`. When `cmd.exe` is launched from Git Bash, cmd inherits MSYS's PATH where GNU find shadows Windows `find.exe`. GNU find then reads `/c` as the directory to search and `/v ""` as junk options — and the script silently recurses through the entire C: drive (Recycle Bin permission errors were the giveaway), never reaching the `dotnet build` line. No error output, looks like a hung compile. Wasted 20+ minutes of confusion before the tail of `cat`'d output finally showed the GNU-find error pattern. Fix: pin the call to `%SystemRoot%\System32\find.exe` so PATH order can't substitute the wrong binary. Single defensive change, costs nothing.

**Build + publish:** `build-debug.bat --publish` shipped **`4.1.16.24`** to NAS `historical\4.1.16.24\x64-debug\` (zip + NOTES + exe + pdb, timestamped 2026-04-16 11:25) AND Dropbox `debug\` for Don to grab. Clean build, 0 errors. NOTES auto-generated from git log (no `debug-notes.txt` at repo root for this build).

### Don's morning crash report → BUG-058 + BUG-054 fixes (commit `02a723c0`)

Don dropped `JJFlexError-20260416-072117.zip` (560 MB minidump + 1.7 KB context txt) in his Dropbox folder. He flagged it as "one and done." Inspection: `Terminating: False` — UI thread NRE that the app caught and survived. Stack pointed at `globals.vb:WriteFreq` line 403, called from a leader-key lambda at line 597, dispatched from `KeyCommands.cs:1869` — i.e. the Ctrl+J → Ctrl+F (Enter Frequency) flow. Two bugs co-located on that path got fixed in one commit:

- **BUG-058 (NEW from Don's crash)** — `RigControl.Callouts.FormatFreq` is a delegate field on per-radio `OpenParms` (`AllRadios.cs:2466`), wired by `FormatFreq = p.FormatFreq` at `AllRadios.cs:2566`. If the leader command fires during the connect window before that wire-up, the delegate is null and Invoke throws NRE that surfaces at the call site. Existing code at `MainWindow.xaml.cs:1319` and `:2485` already guards the same field with `OpenParms?.FormatFreq == null`; `WriteFreq` was missed. Fix: null-guard `Callouts?.FormatFreq` and `RigControl` itself before invoking.
- **BUG-054 (logged 2026-04-15, fixed today)** — license sub-band boundary announcement was deferred to the next tune step after a Ctrl+F frequency jump. Root cause: lambda was calling `CheckBandBoundary(CULng(RigControl.VirtualRXFrequency))` after `WriteFreq(input)` — but `VirtualRXFrequency` reads through to the slice and lags behind the FlexLib round-trip, so the boundary check saw the OLD freq. Fix: `WriteFreq` now returns the parsed Hz it already had locally; lambda passes that authoritative value to `CheckBandBoundary`. Arrow-key tuning unaffected (it already passes its own `newFreq`).

**Build:** `build-debug.bat` (no `--publish`) archived **`4.1.16.26`** to NAS `historical\4.1.16.26\x64-debug\` (zip + NOTES + exe + pdb, timestamped 2026-04-16 11:36). Dropbox intentionally untouched per Noel's directive — Don still on 4.1.16.24 for this morning's SWR-fix testing.

### Don hit BUG-058 twice more — not a connect race → **published 4.1.16.28**

While Noel was off-keyboard, Don dropped two more crash zips in his Dropbox folder: `JJFlexError-20260416-124604.zip` (12:46:04Z) and `JJFlexError-20260416-124920.zip` (12:49:20Z). Both identical to the morning crash — NRE in `globals.vb:WriteFreq:403`, same stack, same leader Ctrl+F trigger.

**Important finding:** three crashes across the day (07:21, 12:46, 12:49) disproves my initial "connect-window race" theory for why `Callouts.FormatFreq` was null. If it were only unwired during connect, Don would see it once per session at most. Three hits (two within 3 minutes of each other, 5 hours after the morning crash) means **the delegate is null during normal operation**, not just during the connect handshake. The defensive guard in commit `02a723c0` still makes the crash benign (app speaks/plays nothing for the FormatFreq path and returns the parsed Hz), so Don's session keeps running. But the *root cause* — why `Callouts.FormatFreq` ends up null in a running, connected session — is a separate investigation.

Likely candidates for next session: (a) `Callouts = p` at `AllRadios.cs:2565` vs `Callouts = new OpenParms()` at `:2281` — are both constructors getting the delegate wired? (b) does something replace `Callouts` mid-session (disconnect/reconnect, client-switch, MultiFlex slot change)? (c) is `OpenParms` being deep-copied somewhere and the delegate not being re-pointed?

**Publish:** `build-debug.bat --publish` shipped **`4.1.16.28`** to both NAS `historical\4.1.16.28\x64-debug\` and Dropbox `debug\` (replacing 4.1.16.24; purge-and-push). Y landed at 28 not 27 because two doc commits (`0dad60f0` Agent.md + `05817bcf` FlexLib email cover) ticked the count between builds. Functionally identical to 4.1.16.26 — same code fixes, just intervening doc commits.

### What's still open for next session

- **Don's verification of 4.1.16.24** — manual-tune Ctrl+Shift+T should now announce SWR ~200 ms after tune-off. Setting checkbox in Settings → Notifications → Tune Feedback should silence it when off. Auto-ATU path should be unchanged.
- **Promote 4.1.16.26 to Dropbox when ready** — copy from NAS or rebuild with `--publish`. Ships the BUG-058 + BUG-054 fixes. Likely will fold into tonight's daily ceremony.
- BUG-056 (speech-off silences navigation) — design sketched for Sprint 27 Track F, still open.
- Sprint 25 testing matrix completion, Sprint 25 → main merge, Flex upstream bug report still pending.

## Session 2026-04-15 (evening) — BUG-057 queue + SWR-after-tune + WIP commits

Picked up from `session-next.md` brief. Six commits on `sprint25/track-a`, HEAD advanced from `5e030221` through to the build-publish commit. Plan in order:

1. **`9eb5017f` — Build pipeline + version-attr modernization.** Committed the other agent's in-flight work: `build-debug.bat` / `build-installers.bat` using `NAS_HISTORICAL` layout; new publish scripts (`publish-to-nas.ps1`, `publish-daily-to-dropbox.ps1`, `backfill-historical-debug.ps1`, `migrate-nas-to-historical.ps1`); `JJFlexRadio.vbproj` now has `<GenerateAssemblyInfo>true</GenerateAssemblyInfo>` with Title/Description/etc. opted out so version attrs flow from `<Version>` and `-p:Version=` override; duplicate version attrs removed from `My Project\AssemblyInfo.vb`; `install.bat` reads `FileVersion` (clean 4-part) instead of `ProductVersion` (source-link `+hash` suffix was polluting installer filenames); `install.nsi` `$PROGRAMFILES64` + x64 path; `deleteList.txt` bumped nvda/SAAPI 32→64 and dropped `dolapi32.dll`; `CLAUDE.md` docs of new nightly flow.
2. **`835cc7a4` — Sprint 26/27 planning docs** (hole-punch-lifeline-ragchew vision update + sprint26-ragchew-keepalive-kerchunk + sprint27-barefoot-openport-hotel).
3. **`19df517a` — Fix BUG-057 (CW prosign cancellation race) via FIFO queue.**
   - `EarconCwOutput`: single-consumer `Channel<QueuedSequence>`. `PlayElementsAsync` now enqueues and returns a Task that resolves when *its* sequence finishes. Consumer loop dequeues, submits to the alert mixer, awaits `totalMs + 50` tail, then dequeues the next. Rapid events (AS → BT → mode-Morse → SK) play in arrival order, nothing gets clobbered by the ~50 ms mixer-buffer window.
   - `Cancel()` retained for shutdown-style interrupts — disposes in-flight handle and drains pending. Consumer loop keeps running (later Play calls still enqueue). `Dispose()` is the terminal shutdown.
   - `MorseNotifier`: dropped the `Cancel()` calls at the top of `PlayCharacter` and `PlayString`; dropped the `_cts` field and `IsPlaying` property (neither used externally). Caller's `CancellationToken` passes straight through.
   - Same primitive is the foundation for future on-air CW message send, iambic keyer, bug, straight key, and code-practice-tutor (one primitive, N downstream features).
4. **`f08a1d52` — SWR-after-tune announcement (Don's HIGH priority ask).** `MainWindow.FlexAntTuneStartStopHandler` now defers the SWR speak 200 ms post-transition and reads the fresher `RigControl.SWRValue` (not the event-time snapshot). Wording changed from `SWR 1.3` to `SWR 1.3 to 1` (ratio form). Gated by new `AudioOutputConfig.AnnounceSwrAfterTune` (default true). Checkbox in Settings → Notifications tab under new "Tune Feedback" section.
5. **`f2ee06d0` — CW design doc additions.** Noel raised: extend the on-air CW section beyond iambic to include **bug** (auto-dit paddle + manual dah lever) and **straight key** (one input, Mark for as long as held), covering haptic / gamepad / mobile / HID inputs. Added "Sending grade" section under practice mode — element duration vs PARIS, inter-element spacing, weighting consistency, rhythm variance, per-style grading. Scope stays Sprint 28+ (engine is ready; input adapters + grading scoring are the work).
6. **`c013bf7e` — `.gitignore /debug2/`** (ad-hoc scratch folder was tripping build-debug.bat's working-tree-clean check).

**Build + publish:** `build-debug.bat --publish --no-commit` shipped **`4.1.16.21`** to NAS `historical\4.1.16.21\x64-debug\` (zip + NOTES + exe + pdb, timestamped at 2026-04-15 20:23) AND Dropbox `debug\` (prior `JJFlex_*_debug*.zip` and `NOTES-*-debug*.txt` purged first). Clean build, 0 errors, 1396 warnings (all pre-existing). `--no-commit` was used because `build-debug.bat`'s working-tree-clean check at line 82 uses `git status --porcelain | find /c /v ""` which gets resolved to MSYS Unix `find` (recurses C:\ with permission-denied spam) when the .bat is invoked from a bash-launched cmd.exe. Tree WAS clean before publish (verified via `git status --short` — only `/debug2/` gitignored). Worth fixing build-debug.bat's clean check to not rely on `find /c /v ""` in a future pass.

### What's still open for next session

- **Ear-check 4.1.16.<N> CW on a real connect sequence** — BUG-057 should make BT (connect) + mode-change Morse + SK (app close) all fire audibly now. Disconnect + reconnect should also fire BT again. Speech-off + CW-on is the full CW-assisted experience to confirm.
- **SWR-after-tune UX check** — Ctrl+Shift+T manual tune and ATU auto-tune should each announce "SWR X.X to 1" 200 ms after tuner-off. Test with tuner toggled off via the Settings checkbox (should go silent).
- **Remainder of `docs/planning/agile/sprint25-test-matrix.md`.**
- **BUG-054** (Ctrl+F license sub-band boundary announcement lag) — still logged, small fix.
- **BUG-056** (speech-off silences navigation) — design sketched for Sprint 27 Track F.
- **Sprint 25 merge to `main`** — once testing is fully green.
- **Flex upstream bug report** — `flexlib-discovery-nre-report.txt` ready; Noel to send.

## Session 2026-04-15 — testing, fixes, ship pipeline, design docs

Large session. Everything below landed on `sprint25/track-a` and is pushed to `origin`. Head: `bf2d245c`. Current debug build: **4.1.16.10** (commit `02bc948f`).

### Connection path — diagnosed and unblocked (WAS broken end-of-last-session)

- **Don's UPnP broken** on new UniFi router (`upnp_supported=0` in the DIAG trace we added to `WanServer.ParseRadioListMessage`). Switched to manual port forwarding.
- **FlexRadio internal port reality check:** radio's TLS is fixed-port internal **4994 TCP** + **4993 UDP**. `wan set public_tls_port=X` advertises the external port, the router forwards X→internal 4994/4993. **Never forward external→internal port 4992** — that's the LAN-only plaintext command port, dangerous to expose. Documented in CLAUDE.md's Release section (already there) and Network tab NOTES.
- **FlexLib `Discovery.Receive` NRE race** (pre-existing FlexLib bug, years old) — patched locally. Wrote up `flexlib-discovery-nre-report.txt` at repo root as a bug report for Flex upstream (Noel to send).
- **WAN antenna-list wait raised 5s → 20s** — matches other WAN-aware timeouts in `FlexBase.Start()`. Connection no longer fails with "no RX antenna" on slow SmartLink paths.
- **Noel end-to-end confirmed connect + audio + tuning + mode change + NR toggles over remote.**

### Sprint 25 testing results (new this session)

- **PC Neural NR**: PASS — toggles cleanly, audible noise floor drop, no crackle under rapid toggle stress.
- **PC NR mode auto-disable/re-enable** (SSB → CW → SSB while on): PASS — by-design behavior, confirmed.
- **Arrow-key tuning, Alt+C mode cycle, Ctrl+F freq entry, volume adjust, Legacy NR, mode change with NR on**: PASS x 6 remotely.
- **Waterfall tab-in → Shift+Tab out frequency drift** (200 kHz–1 MHz jumps): FIXED (see below).
- **CW prosign sound quality** (dits weak, dahs short): FIXED (see below).
- **Ctrl+F license sub-band boundary announcement lag**: LOGGED as BUG-054 — arrow-key tuning announces correctly, Ctrl+F Enter does not until next tune step. Fix is small (centralize the boundary check in a set-freq helper).

### Fixes shipped this session

- **Network tab in Settings** (`c40433a3`, earlier in the day) — "Port (TCP and UDP)" field + "Use different TCP and UDP ports (advanced)" checkbox + Apply button. Calls `FlexBase.SetSmartLinkPortForwarding` which wraps `Radio.WanSetForwardedPorts`.
- **Show panadapter toggle** (`c40433a3`) — Settings → Notifications → Display section. Hides the waterfall entirely (Visibility.Collapsed, tab-order removal, braille push suppressed).
- **Waterfall focus-transition tuning bug** (`e0e111b0`) — Tab-in + Shift+Tab out no longer spuriously tunes. Two-part fix: (a) `_panNavLastCursorPos` baseline seeded on `GotFocus`, tick only tunes if cursor actually moved; (b) pan-display callback skips `SelectionStart = pos` while the control has keyboard focus (caller owns the caret).
- **Discovery.Receive NRE fix** (`9410f7dc`) — race fix in vendor FlexLib `Discovery.cs`. Documented in MIGRATION.md for reapply on FlexLib upgrade.
- **RX antenna wait 5 → 20 s** (`553253ad`) — so WAN connects don't false-fail on slow antenna-list round-trip.
- **CW prosign engine rewrite** (`02bc948f`) — root cause was `FadeInOutSampleProvider` misuse that left almost no sustain in each tone. Replaced with:
  - `CwToneSampleProvider` — sine + raised-cosine envelope (ARRL-standard), sample-accurate timing.
  - `ICwNotificationOutput` refactored to element-batch API (was element-by-element, caused Task.Delay jitter).
  - `EarconCwOutput` composes marks + silences into one `ConcatenatingSampleProvider`, submits via `EarconPlayer.SubmitCwSequence`.
  - `MorseNotifier` builds `List<CwElement>`, dispatches.
  - External API (`PlayCwAS/BT/SK/Mode` delegates) unchanged — callers unaffected.

### Build + ship pipeline landed this session

- **`build-debug.bat`** (`10f0f01b` + `ca327e7f` fixes) — builds Debug x64 with Y injected, zips, NAS archive always, Dropbox only on `--publish`. Noel has in-flight further refactor to `historical\<ver>\x64-debug\` layout (`NAS_HISTORICAL` variable) — my `scripts/build-debug-notes.ps1` helper (`bf2d245c`) is ready to wire into that once Noel commits his changes.
- **4.1.16.8 shipped to Dropbox** for Don's test cycle (`c40433a3` contents).
- **4.1.16.10 shipped to NAS** at new historical layout (`\\nas.macaw-jazz.ts.net\jjflex\historical\4.1.16.10\x64-debug\`) — zip + NOTES + exe + pdb all present. **NOT yet published to Dropbox** — awaiting Noel's explicit go (per `feedback_dropbox_publish_is_explicit.md`).

### Design docs written this session

- **`docs/planning/design/cw-keying-design.md`** — full CW audio engine spec. PARIS timing, raised-cosine envelope math, three future-feature sections: on-air CW via SSB+tone (confirmed CW mode rejects audio input), iambic keyer, code-practice tutor. References amateur-radio standards (ARRL Handbook, QRP Labs RC1).
- **`docs/planning/design/session-latency.md`** — per-session RTT + jitter probe. One measurement, five consumers (CW PTT tail, multi-radio mixer, session health, status UI, auto-quality). Lands on `IWanSessionOwner.Latency` in Sprint 26 Phase 1.

### Verbosity testing observations (BUG-056)

- **Speech "off" silences navigation feedback along with status events** — breaks app usability for operators who want CW-assisted + braille mode.
- **"Speech off" doesn't actually silence initial connect speech** — dial is incoherent.
- **Fix path designed**: categorized speech channels (Status / Navigation / Data readout / Hints), three predefined profiles + Custom. Not implemented this session.
- **Sprint placement**: proposed as Sprint 27 Track F (parallel with Tiers 1+2+NetworkTest — new messages get categorized at birth, minimizing retro-tag work). Could punt to Sprint 27.5 / 29 if Sprint 27 tightens.

### Memory entries added this session

- `feedback_numeric_identifiers.md` — prefer monotonic integers over hex hashes in user-facing contexts (Eloquence speaks integers cleanly; hex forces phonetic spellout).
- `feedback_dont_duplicate_platform_warnings.md` — stay in our lane (radio + user interaction); don't nag about UPnP/antivirus/updates/disk.
- `feedback_dropbox_publish_is_explicit.md` — every debug build → NAS auto; Dropbox only on explicit "publish" / "ship to Don" / end-of-day.

### Priority / next-session queue

1. **SWR-after-tune announcement** (Don's ask) — HIGH PRIORITY, bumped in TODO Near-term. Small bounded scope. Probably first thing to build next session.
2. **Verify 4.1.16.10 CW sounds right** by ear. Dropbox publish once confirmed.
3. **Speech verbosity category redesign** — plan for Sprint 27 Track F or its own focused sprint.
4. **Noel's NAS history refactor** — `build-debug.bat` layout + `backfill-historical-debug.ps1` + `migrate-nas-to-historical.ps1` — all in-flight, uncommitted.
5. **Flex upstream bug report** — `flexlib-discovery-nre-report.txt` ready to send.
6. **Sprint 25 merge to main** — once basic testing is fully green.



### Phase 20: RX Audio Pipeline — COMPLETE
Wired RNNoise and spectral subtraction into the live RX audio path:
- Architecture: Opus decode → gain → PostDecodeProcessor delegate → PortAudio queue
- RxAudioPipeline class (JJFlexWpf) orchestrates chain: spectral sub → RNNoise
- Providers got ProcessInPlace() methods + standalone constructors for push-based pipeline
- JJAudioStream.PostDecodeProcessor delegate bridges JJPortaudio ↔ JJFlexWpf (no circular deps)
- FlexBase.AudioPostProcessor forwards to active stream, handles set-before and set-after audio start
- ScreenFieldsPanel creates pipeline on connect, disposes on detach, feeds mode changes
- PC-side NR works on ALL radios (6000/8000/Aurora) — processing runs on PC, not radio hardware
- UI: "PC Neural NR" and "PC Spectral NR" toggles in DSP section
- Hotkeys: Ctrl+J, Shift+R (PC RNNoise), Ctrl+J, Shift+S (PC Spectral NR)
- Foundational for waterfall FFT tap, recording tap, DSP abstraction layer

**Sprint 25 plan:** `docs/planning/barefoot-qrp-ragchew.md`
**Test matrix:** `docs/planning/agile/sprint25-test-matrix.md`

### Completed Phases (original plan)
- Phase 1: Quick fixes (hotkey conflict, ding tone, slice clamp, status dialog)
- Phase 2: Dynamic slice ops label, modern mode freq navigation
- Phase 3: Sliders to ValueFieldControl, ModeChanged event
- Phase 4: Earcon mute toggle, connection state menu rebuild
- Phase 5: Focus-return context announcement
- Phase 6a: Access key announcements (138 buttons, 37 dialogs)
- Phase 6b: Menu checkbox On/Off state announcements
- Phase 7: Easter egg unlock system, typing sounds, DTMF synthesis
- Phase 8: Braille display status line Phase 1
- Phase 9: Action toolbar (Ctrl+Tab)
- Phase 10: RNNoise ISampleProvider (not wired to audio yet)
- Phase 11: Spectral subtraction ISampleProvider (not wired to audio yet)
- Phase 12: Version bump to 4.1.16, library bumps, changelog started

### Additional Work (beyond original plan)
- SmartLink multi-account: per-account WebView2 cookie jars for session isolation
- Switch Account button on RigSelector, Set Default in Manage SmartLink
- FlexLib upgraded to v4.1.5.39794 (8000-series/Aurora compatibility)
- NR gating corrected: NRF/NRS/RNN all 8000/Aurora only, NRF exposed
- CalibrationEngine obfuscation: pre-computed hashes, no plaintext unlock words
- WebView2 retry on folder lock, connection port tracing

### Interactive Fixes (all complete)
- Status Dialog: pauses refresh when focused, deferred first-item focus
- WebView2 auth: moved to UI thread, no-radios dialog, deadlock fix
- WebView2 folder lock: reverted to shared folder (per-account caused E_ABORT)
- Set Default speech: 200ms delay survives focus-return interruption
- Typing sounds: added to Ctrl+F dialog, per-digit pitch beeps
- Band boundary checks on Ctrl+F and quick-type
- Slice boundaries: Space wraps, Up/Down clamps with speech including slice letter
- Modern tuning: F2 focus, letter filtering, frequency speech
- Connection retry: lightweight RetryConnect, revert early abort to 5s
- Live title bar status: Insert+T reads slice/freq/mode
- Mechanical keyboard sound gain boost (4x→8x)

### Foundational Features/Fixes
**DONE (untested — need directed testing session):**
- Phase 13: RigSelector "Press Remote" label fix + "(Default)" indicator in Manage SmartLink
- Phase 14: Expanded typing sounds — Single tone, Random tones, Musical notes (always available)
- Phase 15: CW prosign notifications (AS/BT/SK) + Verbosity & Notifications tab in Settings
  - ICwNotificationOutput abstraction for future haptics/vibration
  - MorseNotifier engine with configurable sidetone/WPM
  - Wired: AS on slow connection, BT on connect, SK on app close, CW mode announce when speech off
- Phase 16: Editable filter presets dialog (Settings → Tuning → Edit Filter Presets)
- Phase 17: Editable tuning step presets dialog (Settings → Tuning → Edit Tuning Steps)
- Phase 18: Braille status line display-size-aware formatting (20/32/40/80 cell profiles)
- Phase 19: MultiFlex management — client list dialog, connect/disconnect earcons + speech, kick clients
- Phase 20: RX audio pipeline — RNNoise + spectral subtraction wired into live Opus decode path, PC-side NR for all radios
- Phase 21: Trace cleanup — removed SmartLink dialog debug traces, connection traces are properly gated

**REMAINING:**
- Phase 22: Changelog finalization — after all testing complete

### Testing Results (so far)
- Earcon mute: PASS
- Menu checkbox On/Off: PASS
- Focus-return context: PASS
- Slice clamping: PASS
- Slice Operations label: PASS
- Modern mode freq skip: PASS
- Action toolbar: PASS
- Ding tone on freq entry: PASS
- Hotkey conflict fixed: PASS
- Access keys: PASS
- SmartLink multi-account (Don): PASS
- SmartLink multi-account (Justin): Auth works, radio connection fails (network/NAT issue, not our code)

### Cleanup
- Delete `FlexLib_API_v4.1.5.39794/` folder (already integrated into FlexLib_API, untracked)

## End-of-day seal — 2026-05-02

**Theme:** Foundation day. Heavy on planning, design review, and investigation; light on JJFlex-NG repo code change. Substantial activity across parallel surfaces (memory store, blind-hams worktree, Andre's server, GitHub Actions, multi-radio worktree).

### Major outcomes

1. **Inbox/outbox protocol refactored** to `for-noel/` and `for-claude/` (folder names answer "who acts next?"). Priority subfolder convention added. Saved as `feedback_batch_big_questions_to_for_noel.md`.

2. **Blind hams + solar autonomous WSL agent run completed.** 5 deliverables produced (inventory, categorization, migration-plan, questions-for-noel, risks). Major reframe: Andre's box is JUST a data broker (5 files), Jekyll site is on Netlify. Drift investigation followed: Andre is AHEAD of WSL by 4 commits (WSL is the stale clone). Migration plan addendum captures revised three-tier architecture (R2 + rarbox + roarbox) with warm-spare DNS failover. Q11 rewrite ships as the structural fix for the 502 + tmpfile leak.

3. **5 priority design pulls processed** in priority batch:
   - Chained updater pattern (ACK + second-instance candidates: drivers/NVDA add-ons/brailleElement)
   - JJ Radio folding (ACK + concrete user-need surfaced: read/change radio menus + save favorites — first-class feature inheriting from JJ Radio)
   - Tuning unity (ACK + Don's external-speakers accessibility constraint captured)
   - RIT/XIT scale-adjust (ACK + formalize-now decision: extract `StickyAnnouncedMode` helper)
   - brailleElement v1 (ACK + license-inherit + implementation milestone: "sample line + cursor routing test")

4. **R4 trace investigation pivoted.** SyncDrain confirmed Outcome B (zero packets externally). Source diff between FlexLib 4.0.1 and 4.2.18 Discovery.cs showed BYTE-IDENTICAL socket setup. **The bug is NOT in Discovery.cs.** Investigation pivots to API.cs / HAAPI.cs / new files in 4.2.18. New strategic concern memo added: `project_firmware_install_dependency_strategy.md` (rollback vs block vs force-firmware-update; conditional on R4 outcome).

5. **502 root cause located.** Publisher's non-atomic file write leaves brief windows where `next_nets.json` is missing/partial; Apache returns 404, Netlify proxy returns 502 to dev-branch users. Q11 rewrite (Python with `os.replace()`) eliminates the window structurally. Currently NOT reproducing (publisher healthy as of investigation time).

6. **Version cascade swept.** 4.1.17 → 4.2.0 across 10 memory files; foundation_phase.md restated; Customize Home repositioned to 4.2.1; Waterfall to 4.2.2+; release-process examples in MEMORY.md updated.

7. **GitHub Actions re-enabled.** Both `CQ Blind Hams Auto-Post` and `Bump Able Player` were disabled-by-inactivity; re-enabled via gh CLI per Tier 2 standing auth.

8. **Build-now-ship-later philosophy saved as memory.** When Noel says "not in [release]," treat as ship-deferral, NOT build-deferral. 5 engineering tracks (`track/multi-radio-foundation`, `track/tuning-unity`, `track/rit-xit-adjust`, `tools/brailleElement` v1, `track/stuck-modal-escape`) all ACK'd for parallel build, ready to spawn when Noel picks first.

9. **Workflow guidance saved:**
   - `feedback_no_human_time_estimates.md` — quote LOC, not human-coder time estimates
   - `feedback_github_authorization_scope.md` — Tier 2 active for nromey/bh-network
   - `feedback_batch_big_questions_to_for_noel.md` — protocol for queueing big questions vs chat-quick

10. **Hamlib spike + multi-radio existing research surfaced.** ~12 design files in `C:\dev\jjflex-multi-radio\docs\research\` reframe tomorrow's multi-radio reading from "design from scratch" to "synthesize existing work."

### Setup for tomorrow

Priority/ folder in for-noel queues morning reading in this order:
1. `2026-05-02-concert-night-autonomous-results.md` — context for everything
2. `2026-05-02-multi-radio-existing-research-index.md` — reframes multi-radio reading
3. `2026-05-02-multi-radio-braille-read-pack.md` — strategic read pack
4. `2026-05-02-hamlib-integration-spike-FROM-WORKTREE.md` — Hamlib design

After morning reading: pick first engineering track to spawn (5 are ACK'd waiting). Strong candidates for fast wins: API.cs/HAAPI.cs source diff (next R4 step), stuck-modal escape implementation, brailleElement v1 prototype.

### Rigmeter note

Skipped per docs-only-day rule. Today's rigmeter shows 0 commits in the JJFlex-NG repo (everything still uncommitted). The substantive change happened in `docs/planning/` and the memory store, plus reading + investigation work that doesn't surface as code metrics.

## End-of-day seal — 2026-05-05

**Theme:** Surgery day yield exceeded expectations. R5 trace analyzed and falsified MMCSS, R6 discovery cascade implemented + assembled + shipped to Don, 4.2.0 release execution plan processed end-to-end, Phase 0 runbook extracted, two new memory entries captured. Despite morning surgery (8:30 AM Central, salivary duct procedure with extra-cautious anesthesia handling for osteopetrosis), the evening produced a full diagnostic-round-plus-architectural-pivot deliverable.

### Major outcomes

1. **R5 trace from Don analyzed; MMCSS exonerated.** R5 redirected `MmcssPipelineScheduler.Instance` to `TaskScheduler.Default` (eliminating the 4 MMCSS Pro Audio threads). Three full discovery retry cycles all silent — SelfTest probes received fine, SyncDrain captured 0 packets, async loop captured 0. The 85% suspect drops to 0%. Investigation memory updated to reflect Outcome B (every named source-level FlexLib 4.2.18 candidate falsified). Resume-path now points at packet-capture / SmartSDR-ILSpy as the only remaining external-evidence routes if root-cause investigation ever resumes. Build-marker hygiene gap noted (R5 binary printed "R4 active" because the Discovery.cs marker line wasn't bumped); fixed for R6 — marker now self-identifies as "R6 active (chain+MMCSS-bypass)".

2. **Discovery-fallback-chain track completed.** Parallel CLI session ran the full TRACK-INSTRUCTIONS scope: FlexLib vendor patch (`Radio.CreateFromIp` + `Radio.CreateFromIpWan` factories, 53 LOC); Phase 1 + 1.5 (cached LAN/WAN rungs + `TryAutoConnect` integration, ~300 LOC); Phase 1.6 (wpfSelectorProc integration with belt-and-suspenders dedupe, 109 LOC). Three commits on `track/discovery-fallback-chain`. Build clean Debug x64 in worktree before merge. Steering directed via dated `ORCHESTRATOR-DIRECTION-2026-05-05.md` file in worktree root (new pattern — durable handoff vs ephemeral prompt-paste).

3. **R6 build assembled and shipped to Don.** `track/discovery-fallback-chain` merged into `track/flexlib-42` (no conflicts, 850 lines, 8 files). Combined with R5 MMCSS patch already on flexlib-42. Discovery.cs marker bumped R4 → R6. Clean build Debug x64 from flexlib-42 worktree, exe timestamp 21:54:23, FileVersion 4.1.16.0, 0 errors / 1467 warnings (all known). Zip + NOTES at `C:\Users\nrome\Dropbox\JJFlexRadio\don\` (overwriting R5). Historical archive at `docs/planning/active/don-flexlib-4218-discovery/JJFlex_4218-discovery-diagnostic-R6_x64_debug.zip`. NOTES written in ham-operator language (no async/IOCP jargon), explains the chain as "remembers your radio's IP from the last time it connected" + asks for trace either way.

4. **for-claude/2026-05-04-42-release-execution-plan-pull.md processed end-to-end.** All 5 questions answered: Q1 Phase 2 (crash reporter) before Phase 1 (updater); Q2 ship updater unsigned to testers, gate public release on Microsoft Trusted Signing cert; Q3 FastAPI receiver + bundle-flow design with future Claude-as-triage-agent built in; Q4 just-in-time drafts of nginx/workflow/receiver code, plus interest in "Claude operates rarbox directly" execution model; Q5 we have R5 info → discovery cascade dissolves the firmware-install-dependency concern. Phase 0 runbook extracted to `docs/planning/active/phase-0-runbook.md` as standalone executable doc.

5. **Two new memory entries captured.** `project_crash_triage_bundle_flow.md` — bundle-flow design pattern from Q3 second note; `project_claude_as_rarbox_operator.md` — execution-model option from Q4 second note (Claude SSHes via Tailscale, executes runbook sections, Noel verifies). MEMORY.md index updated. `project_firmware_install_dependency_strategy.md` rewritten to reflect DECIDED state (chain dissolves dependency).

6. **Track cleanup partial.** `track/discovery-fallback-chain` worktree could not be force-removed tonight because the parallel CLI session that built R6 still has the directory locked. Logged to research-queue.md as "awaiting Noel input — close that CLI session and ping Claude." Branch deletion deferred until worktree is removable.

### Cross-surface activity

- **Main repo (`C:\dev\JJFlex-NG`)** — research-queue.md updates, Agent.md seal, phase-0-runbook.md created, R6 zip archive, for-claude file deleted.
- **flexlib-42 worktree (`C:\dev\jjflex-flexlib-42`)** — merge commit + R6 build artifacts. Discovery.cs marker bump uncommitted (matches established diagnostic-instrumentation-uncommitted pattern).
- **discovery-chain worktree (`C:\dev\jjflex-discovery-chain`)** — three new commits (vendor patch + Phase 1 + 1.5 + 1.6), pending cleanup.
- **Memory store (`C:\Users\nrome\.claude\projects\C--dev-JJFlex-NG\memory\`)** — 4 files modified/created (firmware-install-dependency rewritten, two new entries, MEMORY.md index updated, project_flexlib_4218_discovery_investigation updated).
- **Don's Dropbox folder** — R6 zip + NOTES replace R5.
- **Historical archive (`docs/planning/active/don-flexlib-4218-discovery/`)** — R6 zip added to paper trail.

### Decisions and scope changes

- **Q1 sequencing flip:** Phase 2 (crash reporter) ships before Phase 1 (updater). Means the rarbox receiver becomes the FIRST piece of new infrastructure 4.2.0 relies on. Section F (nginx + receiver) is now critical-path.
- **Q5 firmware-install-dependency:** dissolved by the discovery cascade. Memory entry archived. No install-time gate, no rollback action, no force-firmware-update needed.
- **Discovery cascade is the chosen path forward for 4.2.0** regardless of root-cause resolution. Don's case (and the broader older-firmware population) becomes "Rung 1a wins, UDP silent-fails as today, user never sees the failure" — invisible in production while root-cause investigation parks.
- **"Claude as rarbox operator"** authorized as a model to trial on Phase 0 Section F. Not yet committed-to as a default; first concrete trial.

### Setup for tomorrow

When Noel resumes:

1. **Wait for Don's R6 trace** — R6 is in Don's folder. Trace lands when he runs the build. Three possible outcomes per investigation memory's resume-path section.
2. **Close the parallel CLI session** that locked the discovery-chain worktree, ping Claude — worktree cleanup is one-line then.
3. **Phase 0 runbook is ready to step through** at `docs/planning/active/phase-0-runbook.md`. ~2-3 hours of Cloudflare + rarbox UI work, checkpointable per section. Per Q4 second answer, Section F is also a candidate to trial the "Claude operates rarbox directly" model.
4. **Build-authorized work waiting:** Sprint 28 bug bundle merge to sprint28/home-key-qsk, stuck-modal-escape track, TS-590 metadata catalog Phase 1.
5. **Bug-bundle DESIGN follow-up** still awaiting Noel's yes/no on Q2 (RunsWithoutRadio flag + action-aware no-radio announcement).

### Rigmeter snapshot — end of 2026-05-05

**Grand totals (all-time, pre-seal-commit):**
- Authored: 751 files, 146,828 lines, 6.57M chars
- Vendor: 180 files, 53,240 lines, 1.88M chars
- Combined: 931 files, 200,068 lines, 8.45M chars

**Per-project breakdown (top 5 by authored lines):**
- JJFlexWpf: 38,813 lines (185 files)
- main_app: 29,522 lines (166 files)
- docs: 26,937 lines (163 files)
- Radios: 23,846 lines (74 files)
- JJLogLib: 5,807 lines (22 files)

**Per-category totals (authored):**
- code: 102,607 lines (430 files)
- docs: 28,529 lines (162 files)
- text_data: 9,824 lines (93 files)
- build: 5,868 lines (66 files)

**Code language breakdown:**
- C#: 77,252 lines (75.3% of authored, all-files 83.5%)
- VB: 17,343 lines (16.9%)
- XAML: 4,985 lines (4.9%)
- Python: 3,027 lines (3.0%)

**Today's git activity (main repo, pre-seal-commit):** 0 commits / 0 lines (the substantive code work today landed on `track/flexlib-42` and `track/discovery-fallback-chain` worktrees, not on the main session's branch). Post-commit rigmeter `today` will show the seal-day delta.

**Branch-scope caveat:** main session is on `sprint28/home-key-qsk`. R6 build content (~960 LOC across vendor patch + Phase 1 + 1.5 + 1.6) lives on `track/flexlib-42` after merge. Rigmeter measures the main repo HEAD only, so today's full cross-surface activity isn't captured by rigmeter alone — see the "Cross-surface activity" section above and the AAR for the complete picture.

**NAS snapshot JSON:** `python tools/rigmeter/rigmeter.py snapshot` runs after this seal commits to capture the structured time-series at `\\nas.macaw-jazz.ts.net\jjflex\historical\stats\<commit-date>-<short-sha>.json`.

## End-of-day seal — 2026-05-08 (extended past midnight into 2026-05-09)

**Theme:** ALL of Phase 0 stood up today — `crashes.jjflexible.radio` AND `data.jjflexible.radio` both went LIVE in parallel-session work earlier in the day — AND the foundation drop landed on main with sprint 28 retired. Daily → Nightly terminology cleaned up. The 4.1 trunk now carries everything the 4.1 line has been validating since late April, and tonight's nightly is buildable from a clean main. **Three huge milestones in one calendar day:** the crash-receiver server-side piece AND the data-provider tier (two of the three 4.2.0 external-infrastructure ship gates) both went operational, AND main caught up to where the 4.1 trunk should always have been.

### Phase 0 — full stack stood up today (BOTH halves, parallel sessions)

**Two major pieces of 4.2.0 external infrastructure went LIVE today.** Phase 0 includes Sections A through G; both halves shipped via separate Claude sessions earlier in the day. The original seal entry I wrote tonight missed BOTH of these — pre-seal cross-surface sweep discipline now codified in CLAUDE.md, with an additional "anchoring failure mode" lesson captured in memory entry `feedback_anchoring_on_first_relevant_artifact.md`.

#### Phase 0 Sections A-E — `data.jjflexible.radio` LIVE on Cloudflare R2

- **Section A:** DNS for `jjflexible.radio` zone on Cloudflare (active prior to today; verified).
- **Section B:** R2 bucket `jjflex-data` created via Cloudflare dashboard (account-level R2 storage, not zone-tied).
- **Section C:** Custom domain `data.jjflexible.radio` connected to the bucket. Cloudflare-fronted, TLS via free Universal SSL, proxied through CF edge for global CDN caching.
- **Section D:** R2 API tokens generated and captured in 1Password. Cache rules configured for the right edge TTL on JSON / static content.
- **Section E:** GitHub Action wired up at `nromey/jjf-data` to sync repo content → R2 on push. Two commits today: initial `Initial commit: R2 sync workflow + test file + README` (`1d5118e`) and follow-up `Pass R2 credentials via env vars instead of aws configure set` (`d3f85b9`). Test file pushed and verified arriving at the public URL via CF edge.

**What this unlocks:** The data-provider tier of the JJF infrastructure is now operational. Future content delivery rides this surface: firmware downloads (per `project_firmware_distribution_decision.md`), DSP model packs (per `project_dsp_model_pack_distribution.md`), help system if it goes web-with-offline-fallback (per Q14 of cascade ACK), and other future static content. Egress is zero-cost on R2 + CF edge cache → no marginal cost to scale. Same architecture also unblocks the queued `data.blindhams.network` migration (per `project_blindhams_data_layer_migration.md`, surfaced today as a Phase-0-shaped insight Noel had while verifying cache behavior).

#### Phase 0 F3-G — crash receiver LIVE on rarbox

A separate Claude session ran F3-G via rarbox-Claude (the on-box execution model authorized 2026-05-07 mid-Phase-0). Memory entry at `project_claude_as_rarbox_operator.md` captures full details. Headline:

- **Service active** since 2026-05-08 11:51:34 CDT. Uvicorn bound to `127.0.0.1:8000`; nginx fronts on 443 with TLS valid through 2041-05-04.
- **F3 storage layer:** `app.py` (root-owned), `/var/lib/jjflex-receiver/` (`ner:ner` 0755), SQLite `index.db` initialized with `PRAGMA user_version = 1` and four-index schema. The hybrid-storage design (zip on disk + JSON sidecar + SQLite index) held up under real-traffic test.
- **F4 systemd:** unit clean, service `active (running)`, `/healthz` returned 200 internally.
- **F5 cert/key validation:** modulus pair verified identical, both confirmation gates honored.
- **G end-to-end POST through Cloudflare edge:** HTTP 200 in **67 ms** with structured response. Bundle byte-identical to source (`cmp IDENTICAL`). Sidecar JSON populated. SQLite row written. Structured journald log line emitted.
- **Real-IP fix landed same day:** `set_real_ip_from <Cloudflare ranges>; real_ip_header CF-Connecting-IP; real_ip_recursive on;` at http-level in `/etc/nginx/conf.d/cloudflare-realip.conf`. Verified across three independent hashes: pre-fix Cloudflare-edge `940d7db595699a22`, post-fix rarbox-self IPv4 `3e78427a9bbb1355`, post-fix external Windows-client `a43647f828a2290e`. All distinct → real-IP machinery works for both internal and external paths.

**What this unlocks:** The crash-reporter ship gate for 4.2.0 is now ~50% complete — the server-side receiver is live and proven. Remaining work is client-side: WPF DispatcherUnhandledException handler that bundles a crash zip and POSTs it to `https://crashes.jjflexible.radio/`. That's part of tomorrow's Sprint 29 planning (item 1 of the three priority items Noel called out at end of day).

**Claude-as-rarbox-operator confirmed.** First end-to-end on-box-model trial. Both confirmation gates honored; nothing surprised the orchestrator-Claude during the briefing handoff. Default execution model for ops-shaped rarbox/roarbox work going forward. Drafted-runbook-Noel-executes persists for novel / high-risk work; SSH-from-elsewhere persists for one-off probes.

### Substantive work shipped tonight

**Sprint 28 design followup (RunsWithoutRadio + action-aware no-radio).** Shipped via taeraflops on `sprint28/bug-bundle-design-followup` track, branched from `sprint28/home-key-qsk`. ~110 LOC across 5 files: `Radios/KeyCommandTypes.cs` adds `RunsWithoutRadio` flag + `ShortActionLabel` field; `Radios/ScreenReaderOutput.cs` extends `SpeakNoRadioConnected` with optional action label; `JJFlexWpf/KeyCommands.cs` and `ApplicationEvents.vb` dispatchers honor the flag and pass label through; `globals.vb` `WriteFreq` handler speaks "no radio, can't tune" at apply time, `DisplayMemory` handler speaks `SpeakNoRadioConnected("show memories")` instead of opening empty viewer. ~16 commands got `ShortActionLabel` populated as Session 6 test surface (BandUp/Down, Mode toggles, BandJump40/20, TuneToggle, ATUTune, AudioGainUp/Down, ATUMemories, Reboot, TXControls, ClearRIT). One spec-vs-implementation delta: Test 23 expected silent dialog open for ShowMemory; implementation chose action-aware speech instead since memory data lives radio-side. Test plan revised to match shipped behavior.

**Foundation drop merged to main via cherry-picks.** Code path: 10 commits cherry-picked from sprint28/home-key-qsk to main (LICENSE update + stuck-modal-escape + 7 bug-bundle fixes + design-followup). Doc/seal/memory path: 15 more commits cherry-picked (evening handoff doc, 6 end-of-day seals 2026-04-29 through 2026-05-05, doc routing, memory ingest, foundation drop docs, 2 CHM refreshes, rarbox setup runbook). All clean — no conflicts. Result: main HEAD `1326b58f` carries the full sprint28 state. Foundation-drop test plan extended on main with Session 6 (tests 20-28) for the design-followup; `for-noel/` cleaned up of processed pull-doc.

**Sprint28/home-key-qsk retired.** Branch deleted locally and on origin. Tributary-for-fold framing dropped — main IS the 4.1 trunk now and seals/memory/docs belong here as durable record. Future fold-time event (when 4.2 ships) brings only the 4.2 corpus from `track/flexlib-42`, not the 4.1 docs (which are already on main).

**Discovery cascade v3 ACK locked.** Canonical memo at `docs/planning/design/discovery-fallback-chain-v3.md` §13 captures all 15 round-3 question answers verbatim with Noel's nuances: Q3 three-option toggle, Q5 strict-but-don't-block-updating, Q7 only-ask-when-needed, Q12 SmartSDR-then-alphabetical-then-manual auto-progression, Q14 view/delete-data UX with help-topic + offline help direction, Q15 stuck-tracking + crash-report escalation, AetherSDR closing direction. Frontmatter flipped to `ACK locked 2026-05-08 — build authorized` for `track/discovery-chain-full-buildout` spawn.

**Daily → Nightly terminology rename.** `publish-daily-to-dropbox.ps1` → `publish-nightly-to-dropbox.ps1` via git mv (history preserved). All 21 internal references updated via sed. Dropbox top-level destination filename pattern: `JJFlex_*_x64_daily.zip` → `JJFlex_*_x64_nightly.zip`; `NOTES-daily.txt` → `NOTES-nightly.txt`. CLAUDE.md "End-of-day done developing workflow" updated. Memory file `feedback_daily_is_debug.md` → `feedback_nightly_is_debug.md`; MEMORY.md index updated; `project_sprint29_updater_vision.md` cross-reference updated. Stale legacy `_daily` artifacts at Dropbox top level (from 5/7) manually cleaned up since the script's purge filter shifted to `_nightly` and didn't catch them. Pairs cleanly with the user-facing channel rename `debug` → `beta` coming as part of Sprint 29 updater work.

**Tonight's nightly build.** `JJFlex_4.1.16.242_x64_nightly.zip` from main HEAD `1326b58f`, built `2026-05-09 00:25:31`. NAS-archived at `\\nas.macaw-jazz.ts.net\jjflex\historical\4.1.16.242\x64-debug\`. Dropbox top level holds the nightly + NOTES. Caught a subtle script bug along the way: the existing 4.1.16.242 polish-build from 2026-05-06 (different commit lineage, same version number due to commit count coincidence) was matched by the script's version-only matching logic and would have been published as tonight's nightly. Forced fresh build to overwrite. Worth a future fix to validate commit hash, not just version, before promoting.

**Memory updates landed.** Three new entries: `project_nvda_remote_server_blindhams.md` (Doug's ask: self-host NVDA Remote at nvdaremote.blindhams.network on roarbox, low compute, LA-central US — captured for next roarbox provisioning); rigmeter v2 architecture revision in `project_rigmeter_stats_tool.md` (per-branch tracking, rich + textual + web viewer, deployable-tool not multi-tenant, --port + config file, discoverability fixes, prior "fewer deps" / "VPS-gated" deferrals reversed); plus the daily→nightly memory file rename. New planning doc `docs/planning/active/2026-05-08-foundation-phasing-agenda.md` distills 7 items from Noel's evening musings into actionable scope.

### Cross-surface activity

- **main:** received 27 commits (10 code + 15 docs/seals + test plan extension + for-noel tidy + script rename + this seal). HEAD: `1326b58f` (will advance once seal commit lands). Pushed to origin throughout the evening.
- **track/flexlib-42:** received planning corpus commit `5d0502e6` (cascade v3 ACK + design-followup spec + foundation-phasing agenda). Pushed to origin so on-box claude can read 4.2 planning state.
- **sprint28/bug-bundle-design-followup:** spawned, taeraflops shipped 110 LOC, merged into sprint28/home-key-qsk, then cherry-picked to main, then retired. Branch lifecycle complete.
- **sprint28/home-key-qsk:** retired (local + origin) after cherry-picks.
- **feature/cache-writer-backport:** untouched. Carries pre-existing WIP modifications and untracked planning docs from earlier in the week.
- **NAS:** new snapshot at `2026-05-09-1326b58f.json` (15 historical points now); new debug build at `historical\4.1.16.242\x64-debug\JJFlex_4.1.16.242_x64_debug_20260509-0025.zip`; memory snapshot `memory-20260509-002547.zip`; private docs snapshot `private-20260509-002549.zip`.
- **Dropbox:** nightly slot has `JJFlex_4.1.16.242_x64_nightly.zip` + `NOTES-nightly.txt`; debug/ subfolder still has earlier `4.1.16.239` tester broadcast (stale by 4 commits — will refresh next time `--publish` runs).
- **rarbox (external infrastructure — morning-side; via separate Claude session):** Phase 0 F3-G ran end-to-end. crash receiver service active since 11:51:34 CDT, listening on `127.0.0.1:8000` with nginx fronting on 443; `crashes.jjflexible.radio` POST verified through Cloudflare edge in 67 ms with byte-identical bundle storage; SQLite index populated; real-IP fix landed same day. **`crashes.jjflexible.radio` is operationally LIVE** — server-side piece of crash-reporter ship gate now standing.
- **Cloudflare R2 / `data.jjflexible.radio` (external infrastructure — earlier today via separate session):** Phase 0 Sections B-E completed. R2 bucket `jjflex-data` created; `data.jjflexible.radio` custom domain connected with TLS through CF edge; cache rules configured; API tokens captured; GitHub Action sync wired up at `nromey/jjf-data`. **`data.jjflexible.radio` is operationally LIVE** — data-provider tier ready for firmware, DSP model packs, future content.
- **`nromey/jjf-data` repo (separate local clone at `C:/dev/jjf-data/`):** Created and populated today. Two commits (`1d5118e` initial + `d3f85b9` env-vars fix), `.github/workflows/sync-to-r2.yml` workflow in place, README + test file. Decoupled from JJFlex-NG; lives at its own clone path which is part of the operational footprint going forward (and a sweep surface that needs to be checked at seal time per the new discipline).

### Decisions and scope changes

- **Sprint 28 retired** rather than kept as fold-time tributary. Reasoning: main IS the 4.1 trunk; seals/memory/docs belong here directly, not on a side branch.
- **Cherry-pick-not-merge** for sprint28 → main. Preserves per-commit granularity for future bisection. End state same; history cleaner.
- **Rigmeter v2 deferrals reversed.** Both `rich` (terminal rendering) and `textual` (TUI) authorized to ship. `serve` subcommand for web viewer also authorized. Multi-tenant hosting NOT pursued — deployable-tool model wins (each user runs locally on their own repo, no `stats.jjflexible.radio/SomeoneElsesRepo`). Aligns with no-silent-phone-home.
- **Daily → Nightly** terminology rename completed same evening (was originally scoped as Sprint 29).
- **Sprint28-design-followup test plan** revised post-merge to match shipped behavior on Test 23 (ShowMemory speaks rather than opens empty viewer).

### Rigmeter snapshot — end of 2026-05-08

**Authored grand totals:**
- 752 files, 147,388 lines, 690,787 words, 6,597,094 chars
- 66.0 braille volumes, 3.3 Moby Dicks, 0.88 King James Bibles
- 76.8 hours read-aloud time, 2,948 printed pages

**Per-category totals (authored):**
- code: 102,689 lines (vs 102,607 yesterday — +82 from design-followup)
- docs: 29,007 lines (vs 28,692 yesterday — +315 from foundation drop docs + planning + agenda)
- text_data: 9,824 lines (unchanged)
- build: 5,868 lines (unchanged)

**Code language breakdown:**
- C#: 75.3% of authored
- VB: 16.9%
- XAML: 4.9%
- Python: 3.0%

**Today's git activity (main repo, post-seal-commit will be ~28 commits):** rigmeter `today` (since 2026-05-09 00:00 local time) reports 18 commits / 75 unique files / +7,844 / -40 / net +7,804 lines — all on main from the cherry-pick + rename + test-plan extension flurry that ran past midnight. The 2026-05-08 calendar-day work (design-followup + planning corpus + agenda) lands on track/flexlib-42 + sprint28/bug-bundle-design-followup branches before midnight; rigmeter measures HEAD only, so the AAR captures the full 2026-05-08 cross-surface picture.

**Trend across 15 historical snapshots:**
- Code base lines: 230,429 → 102,689 (cleanup-then-growth pattern; the early Phase 0 vendor prune dropped 100K+ lines)
- Doc base lines: 967,495 → 29,007 (the early drop is the v1.1 derived-artifact exclusion; growth since is real prose)

**NAS snapshot JSON:** `2026-05-09-1326b58f.json` (15 historical points)

### Setup for 2026-05-09

**Foundation drop testing.** Test plan at `docs/planning/for-noel/2026-05-04-foundation-drop-test-pull.md` is on main, complete with 28 tests across 6 sessions. Build the nightly with `git checkout main && git pull && dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal` from `C:\dev\JJFlex-NG`. Or grab the pre-built zip at `Dropbox\JJFlexRadio\JJFlex_4.1.16.242_x64_nightly.zip`. Don isn't testing tonight; Sessions 1, 2, 5, 6 are pure solo for Noel.

**Tomorrow's parallel-track candidates (all authorized; pick subset):**
- Discovery cascade full buildout (against locked v3 spec, Phases 2+ rungs)
- Rigmeter v2 (per-branch tracking + man page + RECIPES + rich rendering, in that priority order)
- bh-data migration (private repo + R2 + GHA sync, test on data1.blindhams.network first)
- Sprint 29 plan formalization (4.2 phasing roadmap doc)
- Roarbox warm-spare buildout (mirror jjf-data + bh-data; install ntfy server; install NVDA Remote per Doug's ask)
- Azure Trusted Signing signup walkthrough (interactive; needs Noel at keyboard)
- Multi-radio + Hamlib SWIG groundwork
- Track C / BrailleElement primitive v1 build

Heavy day available — the agenda doc at `docs/planning/active/2026-05-08-foundation-phasing-agenda.md` has scoping for items 1-7 of Noel's musings. Pick what to spawn against taeraflops vs what to drive interactively.
