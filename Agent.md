# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `sprint27/networking-config` (Track A Phase 1 complete; Phase 2 fan-out next)

## Current State — 2026-04-20 sealed: Sprint 27 code-complete + cross-sprint docs audit landed; daily 4.1.16.105 on Dropbox

**Day closed 2026-04-20 ~20:17.**

Dropbox top level holds `JJFlex_4.1.16.105_x64_daily.zip` + `NOTES-daily.txt` covering:
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
- Flexibility principle memory captured (`project_flexibility_principle.md`) — BlindCat590-anti-pattern named explicitly as the guiding example.

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
