# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `sprint28/home-key-qsk` (Phases 1-8 + 11 complete; Phase 10 deferred to 4.1.18; Phase 9 test matrix drafted; today's bug-bundle landed; rigmeter v1 first-draft uncommitted-then-rolled-into-seal)

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
