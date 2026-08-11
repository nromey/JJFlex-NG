# Sprint 28 / 4.2.0 Foundation — Bug Bundle Triage

**Created:** 2026-05-04
**Source:** the "~12 bugs" from the 2026-04-27/28 late-night iterative testing session, originally promised in the 2026-04-28 morning briefing as "needs design review before implementation, most aren't 4.1.17-critical."
**Provoking question:** "they're really nice-to-have stuff but they probably should be fixed sooner rather than later with foundation."

## Why this exists

We've spent the last week pulled toward big-future things — FlexLib 4.2.18 silent-discovery investigation, Sprint 29 updater/crash-reporter design, multi-radio Hamlib spike, BrailleElement primitive, DSP controls research. Meanwhile the small, concrete fixes from the 2026-04-28 morning briefing have been quietly aging in `JJFlex-TODO.md`. This doc is the accountability artifact: status check on each, plus a sequencing proposal that lands the cheap ones in 4.2.0 alongside `track/stuck-modal-escape`.

## Status of the 12-ish bugs

### FIXED — already shipped (3)

These landed between 2026-04-28 morning and the current branch tip. No further work needed.

- **KEY CONFLICT — Ctrl+F bound to both SetFreq and SpeakFrequency** — fixed in `64eac28e` (4.1.17 morning sweep, 2026-04-28). Ctrl+F now unambiguously SetFreq; SpeakFrequency moved to Ctrl+Shift+F per Sprint 15+21 design intent.
- **F2 announcement may have lost "Home" prefix** — fixed in `64eac28e`. Wasn't a regression from the no-radio guard work; was a pre-existing focus-event gap. Extracted `SpeakHomeDestinationPrefix()` and call it explicitly from `FocusFrequencyField()`.
- **Help dialog can't be exited with Escape** — fixed in `09b724c3` (4-28 evening seal) via Win32 hook.

Plus the FEATURE entry from same briefing:
- **Disconnect notification — speech + CW prosign** — implemented in `64eac28e`.

### IN FLIGHT — track spun up today (2 collapsed into one fix)

- **Modal connecting state has no escape path** ← THE STUCK BUG
- **Slice-acquisition failure presented as "stuck on connecting"** ← sibling, same fix surface

Both collapse into `track/stuck-modal-escape` at `C:\dev\jjflex-stuck-modal` (branched from `sprint28/home-key-qsk` — pre-4.2.0 baseline, FlexLib 4.1.5). Design memo at `memory/project_stuck_modal_escape_design.md`, ACK'd 2026-05-02. ~275 LOC across 5 files. TRACK-INSTRUCTIONS.md is in the worktree. Merges back to sprint28/home-key-qsk → main as pre-4.2.0 foundation drop; carried forward into 4.2.0 via normal main-line merge.

### OPEN — connect-time investigation class (2)

These are heavier than polish bugs — they need a trace-analysis pass before code can change. Both pair naturally with the trace persistence design memo (`project_trace_persistence_design.md`) and the stuck-modal track's cancel-mechanism work.

- **AS-retry-then-janky-reconnect** (HIGH severity) — Sprint 26+27 networking-overhaul regression. Hypothesis: radio-side state isn't fully cleaned before retry's `Connect()` begins. Investigation target: `Radios/FlexBase.cs` station-name-wait timeout + `globals.vb` openTheRadio retry loop. **May get partial fix as a side-effect of the stuck-modal track's cancel mechanism** (the radio-side cleanup work overlaps).
- **Connect path takes longer post-overhaul** (HIGH severity) — needs profiling pass: pre-Sprint-26 vs post-overhaul connect-time histograms. Design improvements to consider: adaptive timeout, phase parallelization, skip phases that aren't needed for the use case.

**Recommended sequencing:** these two ride with Sprint 29's networking-fix arc, AFTER trace persistence ships (so we can capture multiple AS-retry sessions for diff comparison). Don't bundle into 4.2.0 — they're investigation-shaped, not patch-shaped.

### COMPLETED 2026-05-04 — polish bug bundle (7/7 shipped)

CLI session on `track/sprint28-bug-bundle` finished 2026-05-04 with all 7 bugs committed. Commit list: `6752cafa` (CW prosign pause) → `a31100fb` (Squelch Level skip) → `38a5658f` (universal Home keys window-level guard) → `41446e37` (Slice/Slice Operations purpose-naming) → `b5380563` (Classic/Modern field-order parity) → `9d425c5a` (MainContent named for dialog-close focus) → `564e9333` (PlayCwSK ee on all exit paths). Awaiting orchestrator merge to sprint28/home-key-qsk, same pattern as the stuck-modal merge.

**Originally scoped as:** "really nice-to-have" small fixes. Each is small, independently verifiable, and a clear UX or accessibility improvement. Bundle landed in `track/sprint28-bug-bundle` for foundation-drop merge alongside stuck-modal.

| # | Bug | Effort | Severity | Files |
|---|-----|--------|----------|-------|
| 1 | CW prosign concatenation: `73 SK ee` runs together as `73 SKEE` (no pause primitive) | ~30 LOC | Low (polish) | `Radios/ScreenReaderOutput.cs` (PlayCwSK), CW notification engine |
| 2 | Dialog Escape produces "pane" announcement instead of named focus | ~15 LOC | Low (polish) | The freq-input dialog close handler + the post-close focus target (likely MainWindow / panel within) |
| 3 | Squelch Level should skip arrow-nav when Squelch off (matches RIT/XIT pattern) | ~20 LOC | Medium | `JJFlexWpf/Controls/FrequencyDisplay.xaml.cs` field-nav logic; same `IsPositionSensitive` helper that gained RIT/XIT awareness in `f6c00aa2` |
| 4 | Slice / Slice Operations field announcements lack purpose-naming ("slice selector: slice A active") | ~25 LOC | Medium | `JJFlexWpf/Controls/FrequencyDisplay.xaml.cs` `NavigateToField` for the Slice + Slice Operations cases |
| 5 | Classic and Modern tuning modes have different field orders, no parity | ~40 LOC | Medium | `JJFlexWpf/FreqOutHandlers.cs` `SetupFreqOut` (Classic) vs `SetupFreqoutModern` (Modern) sequences |
| 6 | Universal Home keys (R/M/X/Q/V/=) silent when pressed outside Home with no radio | ~50 LOC | Medium | `JJFlexWpf/MainWindow.xaml.cs` `MainWindow_PreviewKeyDown` — add no-radio + universal-Home-key guard at WPF Window scope |
| 7 | PlayCwSK drops `ee` close entirely with no radio (NEEDS RE-VERIFY first; Noel partially retracted 2026-04-28 morning) | ~15 LOC IF still a bug | Low | `ScreenReaderOutput.PlayCwSK` and the close-path callers |

**Total bundle estimate: ~195 LOC** if all seven land. Distinctly smaller than the stuck-modal track. No coupling between bugs — each can land as its own commit.

**Suggested commit ordering** (cheapest first, builds momentum):
1. CW prosign pause primitive (#1) — touches isolated CW assembly code, easy to test by ear
2. Squelch Level arrow-nav skip (#3) — mirrors existing RIT/XIT pattern, minimal risk
3. Universal V wrap precedent in `2dbfb1c5` shows the pattern; field-order parity (#5) and Slice naming (#4) follow
4. Universal Home keys no-radio guard (#6) — depends on coordinating with the existing dispatcher guard work
5. Dialog Escape "pane" fix (#2) — needs runtime focus-restoration spelunking; do after the easier ones build muscle memory
6. PlayCwSK `ee` re-verify (#7) — investigation first, then fix only if still reproduces

## Two DESIGN entries from the same briefing

These aren't bugs — they're design questions Noel raised in response to the no-radio guard work. They have a different shape than the bug bundle.

- **No-radio guard should opt-out per-command** — let SetFreq dialog open even with no radio (so easter eggs / cqtest still work). Adds `RunsWithoutRadio` field on `KeyTableEntry`. **Decision needed first:** which other commands opt in? Suggested candidates: ShowMemory. Probably worth landing in 4.2.0 alongside the bundle since it's the kind of "introduced friction during foundation work, fix it during foundation" item.
- **No-radio announcement should be action-aware** — say what couldn't happen ("unable to change band, JJ Flexible Home no radio connected"). Tie to existing `KeyTableEntry.Description` field. **Pairs with the Short Action Labels vocabulary work** (`memory/project_short_action_labels_vocabulary.md`) — the labels are ALREADY defined for ~80 commands; the no-radio helper just needs to pull from there.

Both are 4.2.0-appropriate if a quick decision conversation happens. Otherwise slip to 4.2.0.x.

## Recommended schedule

**Two merge events, not one** (decided 2026-05-04 with Noel):

### Event 1 — pre-4.2.0 foundation drop (next merge to main)

- `track/stuck-modal-escape` — branched from sprint28/home-key-qsk; merges back to sprint28/home-key-qsk → main when complete.
- `track/sprint28-bug-bundle` — branched from sprint28/home-key-qsk; merges back to sprint28/home-key-qsk → main when complete.
- The two DESIGN entries — fold in if a quick design call resolves them; otherwise slip to event 2.

**Why pre-4.2.0:** these fixes touch surfaces that work identically on FlexLib 4.1.5 and 4.2.18. Branching from `track/flexlib-42` would artificially gate Don's testing on the still-broken FlexLib 4.2.18 silent-discovery path. Pre-4.2.0 means Don can run a build that's the proven 4.1.5 baseline + accessibility/polish fixes, while the 4.2.0 work continues in parallel.

### Event 2 — 4.2.0 release (when ready)

- `track/flexlib-42` (FlexLib 4.2.18 baseline)
- `track/discovery-fallback-chain` (silent-discovery workaround)
- Firmware push/update infrastructure
- The two stuck-modal + bug-bundle merges from event 1 carry forward via the normal main-line merge — no separate work needed for them at event 2.

### 4.2.0.x patches (after 4.2.0 ships)

- Anything from the foundation drop that turned out bigger than its estimate.
- The two connect-time investigation bugs IF the stuck-modal cancel-mechanism work doesn't resolve AS-retry as a side effect.

### Sprint 29 (post-4.2.0)

- AS-retry investigation (heavier than patching — needs trace persistence first).
- Connect-time profiling and adaptive timeout work.
- The diagnostic-rich error messaging escalation in the stuck-modal escalation dialog (if it didn't fully ship in event 1).

## Open questions for Noel

1. **Spin up `track/sprint28-bug-bundle` now?** Worktree at `C:/dev/jjflex-bug-bundle`, branched from `track/flexlib-42` (matches stuck-modal pattern). Could run in parallel with stuck-modal coding session.
2. **Decide on the two DESIGN entries (RunsWithoutRadio + action-aware no-radio announcement)?** Both are 5-minute decisions; bundling them with the polish track makes sense if you can give a quick read.
3. **PlayCwSK `ee` re-verify (#7) — should we just delete the entry?** You partially retracted on 2026-04-28 morning ("the dit-dit close DOES fire on app close"). If you can confirm it always fires (with-radio AND without-radio paths), the entry can be removed and the bundle drops to 6 bugs.

## Cross-references

- `docs/planning/vision/JJFlex-TODO.md` — full bug entries with root-cause analysis and fix shapes
- `docs/planning/2026-04-28-morning-briefing.md` — original "12 bugs" framing
- `memory/project_stuck_modal_escape_design.md` — the IN-FLIGHT track's design memo
- `memory/project_as_retry_pathway_regression.md` — the OPEN connect-time investigation
- `memory/project_short_action_labels_vocabulary.md` — pairs with the action-aware no-radio DESIGN entry
- `docs/planning/active/research-queue.md` — should be updated to add `track/sprint28-bug-bundle` once spun up
