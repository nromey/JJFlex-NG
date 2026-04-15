# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `sprint25/track-a`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Display name:** JJ Flexible Radio Access (internals stay JJFlexRadio)
- **Migration complete:** .NET 10 LTS (migrated 2026-04-13), dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.16 (version bumped in Sprint 25 Phase 12)

## Current State — Sprint 25 In Progress

**Status:** All coding phases complete (13-21 + 20) plus a run of testing-driven fixes (see below). Remaining: Phase 22 (changelog after testing). Testing largely done for basic + remote + CW.

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
