# Sprint 28 — Home-Key-QSK (Home polish, keystroke deconflict, safety primitives)

**Status:** Planning complete — pending user review before execution
**Created:** 2026-04-21
**Authors:** Noel (product direction, UX framing, scope shaping across three design conversations with Don's input on Escape-to-collapse), Claude (code investigation, design synthesis, phase decomposition)
**Parent context:** Foundation phase memory — "Foundation spans 4.1.16 → 4.1.17 (possibly → 4.1.18); 4.1.17 cuts on substance not calendar." This sprint closes the foundation phase before waterfall work begins.
**Branch target:** `sprint28/home-key-qsk` (serial execution — no tracks, no worktrees per current practice in Sprint 27)
**Release target:** Ships as part of 4.1.17 alongside Sprint 26 and Sprint 27. Release happens post-sprint after combined test matrix passes.

---

## Sprint goal

Finish the foundation phase with three categories of work that belong together:

1. **Input-timing primitives** — a shared, user-configurable double-tap tolerance that replaces the hardcoded 300 ms in filter-edge selection and powers the new Escape-to-collapse pattern.
2. **Home-screen navigation and clarity** — rename the frequency-field region to "Home" so speech, menus, help, and conversational references all have a clean one-word referent. Extend Escape to collapse field groups; double-Escape to collapse all and return to Home.
3. **Authorization primitives** — a `RequireOperatorPresence` abstraction gating destructive remote-state operations, with two strictness levels (Passive for port-forward-scale actions, ActiveChallenge reserved for firmware-class actions). First caller: the port-forward Apply button, which currently has no ownership check at all.

None of these items alone warrants a sprint. Together they're "polish trim on the built house" — the last round of fit-and-finish before the waterfall sprint arc begins.

---

## Why this package fits together

All three categories share a design signature: **define the primitive once, use it in the right places, leave the API extensible for future callers.** DoubleTapTolerance will govern any future double-press interaction. RequireOperatorPresence will govern any future destructive operation (firmware upload, factory reset, calibration save). The Home rename makes all future keyboard-shortcut documentation cleaner.

This is also a natural pairing because the Home rename and the Escape-collapse behavior interact: double-Escape's "collapse all + land focus on Home" is the closing loop of the navigation pattern — the user learns "Escape backs out of what I'm in, double-Escape takes me home." That mental model is only usable if Home has a name.

---

## Execution model

**Serial on a single branch.** All work touches overlapping files (KeyCommands.cs, FreqOutHandlers.cs, Settings UI, speech layer). Parallel tracks would collide. Following Sprint 27's serial pattern per memory `project_sprint27_serial_execution.md`.

**No worktrees, no track decomposition, no TRACK-INSTRUCTIONS.md.** Phases run sequentially on `sprint28/home-key-qsk`.

**Commit discipline:** one commit per phase minimum, with phase number in the message. WIP-prefixed commits for partial phase work if the session ends mid-phase.

**Build verification:** full solution build (`dotnet build JJFlexRadio.sln -c Release -p:Platform=x64`) after every phase; exe timestamp confirmed to be current per CLAUDE.md Build Notes. Debug build (`build-debug.bat`) for mid-sprint smoke tests.

---

## Phase breakdown

### Phase 1 — DoubleTapTolerance setting primitive

**Scope.** Introduce a shared, user-configurable double-tap tolerance. Four discrete named steps in a ComboBox or RadioGroup under Settings > Accessibility: Quick (250 ms), Normal (500 ms), Relaxed (750 ms), Leisurely (1000 ms). Default: Normal.

**Deliverables.**
- `public enum DoubleTapTolerance { Quick = 250, Normal = 500, Relaxed = 750, Leisurely = 1000 }` with enum values that ARE the millisecond counts (no translation table needed — cast to int).
- Settings storage entry (wherever app-level input preferences currently live; to be located during implementation).
- UI in Settings dialog: radio-button group matching the Tier 1/2/3 accessibility pattern established in Sprint 27. Each option announces as "Name, N milliseconds, selected/not selected." Changes announce "Double-tap tolerance, <Name>, <N> milliseconds."
- Public accessor `Settings.DoubleTapToleranceMs` returning the int ms count, so consumers don't import the enum unless they want the label.

**Accessibility notes.** Radio group gives screen-reader users discrete choices rather than a slider's fiddly increments. Announcement includes both semantic name and raw ms so users can calibrate against their actual preference.

**Exit criterion.** Setting persists across app restart. Value is readable from code via `Settings.DoubleTapToleranceMs`. UI is fully keyboard-navigable and screen-reader-announced.

### Phase 2 — Filter-edge migration to shared setting

**Scope.** The existing double-tap pattern at `FreqOutHandlers.cs:1767-1769` has a hardcoded 300 ms threshold. Migrate it to use `Settings.DoubleTapToleranceMs`.

**Deliverables.**
- One-line change: `(now - _lastBracketTime).TotalMilliseconds < 300` → `(now - _lastBracketTime).TotalMilliseconds < Settings.DoubleTapToleranceMs`.
- Verify filter-edge-entry behavior still works on Slice's frequency field.
- Changelog-worthy note: tolerance bumps from 300 → 500 ms by default. Users who preferred the snappier feel can pick Quick (250 ms) in Settings > Accessibility.

**Exit criterion.** Filter-edge double-tap still enters edge mode correctly at each of the four tolerance settings. Bracket-then-bracket-slowly test at Leisurely setting enters edge mode; same test at Quick setting does not.

### Phase 3 — Escape-collapse behavior (single and double)

**Scope.** Wire Escape behavior into the ScreenFieldsPanel: single Escape collapses the focused field group (focus lands on the group header); double Escape within the tolerance window collapses all groups (focus lands on Home).

**Deliverables.**
- `PreviewKeyDown` handler on ScreenFieldsPanel that intercepts Escape when focus is inside an expanded group.
- Single-Escape semantics: collapse the currently-focused group, focus moves to that group's header. User can now arrow-navigate to other headers or re-expand.
- Double-Escape semantics: if a second Escape arrives within `Settings.DoubleTapToleranceMs` of the first, collapse all groups, focus lands on Home (FreqOut). Announce "All panels collapsed, home" (Terse) or "All panels collapsed, JJ Flexible Home" (Chatty).
- Correct scoping: Escape handler only fires when focus is in ScreenFieldsPanel. Dialog Escape handlers continue to work normally (dialog close wins when a dialog is open).

**Accessibility notes.** After single-Escape, announce which group just collapsed ("DSP collapsed"). Focus landing on the header is what lets the screen-reader user orient. After double-Escape, the Home announcement closes the navigational loop so the user immediately knows where they landed.

**Earcons (added 2026-04-21).** Three new earcons paired with collapse/expand behavior — they land in the same commits as the collapse logic:

- **Expand** — ascending chirp 400 → 1200 Hz over ~350 ms, plus band-pass-filtered noise sweep tracking the tone. Soft attack/release envelope. Fires on group re-expand. Duration chosen for audible sweep travel (noticeable rising motion) rather than mere "blip."
- **Collapse** — mirror of expand: descending chirp 1200 → 400 Hz over ~350 ms with descending noise sweep. Fires on single-Escape.
- **Collapse-all ("gavel")** — single authoritative low-frequency hit: 140 Hz fundamental + quieter 280 Hz harmonic + brief filtered-noise attack transient, 5 ms attack with exponential decay over ~450 ms. Semantic: "case closed, collapsed everything." Fires on double-Escape. Design note: single hit preferred over Law & Order's iconic dun-DUN double hit — faster response, less pop-culture coupling, same finality feel.

Band-pass-filtered noise sweep (center frequency tracks the tone, ~1/3 octave bandwidth) gives cut-through against RF hash. Pure tones alone can get lost in broadband radio noise; narrowband noise sweep's envelope doesn't match ambient noise patterns so the ear picks it out.

**Earcon implementation.** `JJFlexWpf/EarconPlayer.cs` gains three public methods (`PlayExpand`, `PlayCollapse`, `PlayCollapseAll`) plus two new helpers: a `BandPassNoiseSweepSampleProvider` (tracks center freq over duration, configurable bandwidth) and a `DecayingGavelSynthesizer` (fundamental + harmonic + noise transient + exponential decay envelope). `PlayChirpPanned` already exists and is reused for the tone component.

**Exit criterion.** Tab into a field inside DSP → Escape → group collapses, focus on DSP header, speech confirms, collapse earcon plays. Re-expand via header + Enter → expand earcon plays. Escape again within tolerance → all groups collapse, focus on Home, speech confirms, gavel earcon plays. Open a dialog, press Escape → dialog closes (not collapse behavior, no earcon). Earcons audibly distinct from each other and from existing earcons (filter-edge enter, feature-on/off tones, etc.).

### Phase 4 — Home rename

**Scope.** Rename the frequency-field region to "Home" across speech, accessibility attributes, menu labels, help documentation, and KeyCommands descriptions. Internal code already uses "home position" in comments (see `BrailleStatusEngine.cs:13, 54, 68`) — this phase surfaces that existing internal concept to users.

**Current state (grep results 2026-04-21):**
- `FrequencyDisplay.xaml:9` — `AutomationProperties.Name="Frequency Display"`
- `FrequencyDisplay.xaml:24` — `AutomationProperties.Name="Frequency and VFO Display"`
- `KeyCommands.cs:67` — F2 description: "Focus frequency display"
- Internal: `FreqOut`, `FreqOutHandlers` (class names stay — internal identifiers not retargeted)

**Target state:**
- `FrequencyDisplay.xaml:9` — `AutomationProperties.Name="JJ Flexible Home"`
- `FrequencyDisplay.xaml:24` — `AutomationProperties.Name="Home, frequency and VFO"`
- `KeyCommands.cs:67` — F2 description: "Go to Home"
- Speech on focus landing: "JJ Flexible Home" (Chatty) / "Home" (Terse) / "Home, [current frequency and mode]" (Moderate, optional stretch goal)
- Help docs: any reference to "frequency field" or "frequency display" gets retargeted to "Home"

**Deliverables.**
- Full grep of the codebase for the user-facing strings identified above; update all occurrences.
- Internal class names (`FreqOut`, `FreqOutHandlers`) stay unchanged. Only the user-facing strings change. Renaming internal identifiers is scope creep and risks touching files this sprint otherwise doesn't need to.
- Add a secondary F2-equivalent hotkey on the Home key — matches the semantic ("take me home"). F2 stays primary for muscle memory.
- Help doc updates: any markdown file under `docs/help/md/` referencing "frequency field" gets an edit pass.

**Accessibility notes.** The rename is pure win — one-word referent replaces multi-word description. Screen-reader users gain orientation speed on every focus landing. For Braille output (memory `project_multi_braille_output_vision.md`), "Home" is shorter to render than "Frequency and VFO Display."

**Exit criterion.** NVDA and JAWS both announce "Home" or "JJ Flexible Home" when F2 fires or Home key fires. Menu label and hotkey help text both say "Home." At least one help doc updated as a spot-check that the pattern works.

### Phase 5 — Keystroke deconflict across Slice / SliceOps / Home

**Scope.** Unify the keystroke vocabulary across the three focus areas of the Home screen so the same letter means the same thing regardless of which field has focus. Resolve two specific conflicts: R (pan-right vs RIT) and X (transceive vs XIT).

**Current conflicts (from FreqOutHandlers.cs grep):**
- Slice field (line 860, `AdjustSlice`): Space cycle, M mute, T set-TX, X transceive, L/C/R pan-left/center/right, `.` new, `,` release, 0-9 jump.
- Slice Operations field (line 1055, `AdjustSliceOps`): Space mute-toggle, M mute, S sound, A active, T TX, X transceive, Up/Down volume, PageUp/PageDown pan.
- Home / frequency field (line ~1565 onward): M mute, V cycle VFO, R RIT toggle, X XIT toggle.

**Resolution (agreed in conversation 2026-04-21):**
- **Pan moves off letter keys onto navigation keys on the Slice field.** PageDown → pan left (was L), Home → pan center (was C), PageUp → pan right (was R). Matches SliceOps' existing pattern. Frees R universally.
- **Transceive rebinds to `=` on Slice and SliceOps.** Mnemonic: RX = TX. Preserves the concept of Jim-parity (a dedicated transceive hotkey continues to exist) without locking X to a single meaning. Frees X universally.
- **R becomes RIT toggle on all three fields.**
- **X becomes XIT toggle on all three fields.**
- **Loose unification on gap-fills.** Where SliceOps has keys that make semantic sense on Home (e.g., M mute, S sound, A active, T TX), propagate them. Where keys are context-specific lifecycle actions (`.` new slice, `,` release slice), leave them slice-specific — strict unification would make the keys work in nonsensical contexts.

**Deliverables.**
- Updates to `FreqOutHandlers.AdjustSlice` (line 860): pan keys moved to nav-keys, X rebinds to transceive via `=`, R path cleared.
- Updates to `FreqOutHandlers.AdjustSliceOps` (line 1055): X rebinds to transceive via `=`.
- Updates to `FreqOutHandlers.*` Home/frequency handler (around line 1584, 1598): R stays RIT toggle, X stays XIT toggle (no changes needed on Home for these two — the conflict resolution happens by changing the other two fields).
- Gap-fills on Home handler for the agreed-upon shared keys (M mute, S sound, A active, T TX — audit which already exist and add the missing ones).
- Hotkey help doc update: the keyboard reference needs to reflect the new map.

**Accessibility notes.** Key consistency across focus areas is a measurable accessibility win: users don't need to track "where am I" before pressing a letter. Announcement on first press of a remapped key ("Pan right" for PageUp on Slice, "Transceive" for `=`) confirms the new binding has taken effect.

**International keyboard caveat.** Verify `=` is directly typeable (no modifier required) on testers' keyboards. US layout: yes. If Don or Justin surfaces a layout issue, fall back to `+` or another free symbol.

**Exit criterion.** Each of the three fields responds correctly to M, S, A, T, R, X (where semantically applicable), `=` (Slice and SliceOps), and the nav-key pan triad (Slice and SliceOps). Conflict test: pressing R on the Slice field triggers RIT toggle, not pan-right. Pressing X on Slice triggers XIT toggle, not transceive. Transceive reachable via `=` on Slice and SliceOps.

### Phase 6 — RequireOperatorPresence primitive

**Scope.** Introduce an authorization primitive for destructive remote-state operations. Two strictness levels: Passive (checks `IsLocalPtt == true` on the current client — the "am I the primary operator right now" signal FlexLib already exposes); ActiveChallenge (declared in the enum, NotImplementedException throw on use — reserved for future firmware-upload work).

**Deliverables.**
- `public enum PresenceLevel { Passive, ActiveChallenge }` with an XML-doc comment on ActiveChallenge noting: "Deliberately stubbed. First caller will be firmware upload work (see memory `project_8600_unbox_firmware_trigger.md`). Implementation lives alongside that feature, not on speculation."
- `FlexBase.RequireOperatorPresence(PresenceLevel level, string reason, Action onConfirmed, Action? onDenied = null)` — the canonical API.
- Passive implementation: check `theRadio.FindGUIClientByClientHandle(this.ClientHandle)?.IsLocalPtt == true`. If yes, invoke `onConfirmed`. If no, announce denial via speech, invoke `onDenied` if provided.
- Denial announcement pattern: "Cannot <reason> — you must be the primary operator at the radio."
- ActiveChallenge implementation: `throw new NotImplementedException("ActiveChallenge is reserved for firmware-class operations; implement alongside firmware upload.")` — intentional gate to prevent accidental use before the challenge mechanism is built.

**Accessibility notes.** Denial announcement must be immediate and unambiguous. Terse verbosity: "Not authorized." Moderate: "Not authorized, you are not the primary operator." Chatty: "This action requires you to be the primary operator at the radio. You are currently connected remotely."

**Exit criterion.** Primitive callable from arbitrary code, Passive works against a connected radio, ActiveChallenge throws predictably. Unit test (or test-harness equivalent) covers both paths.

### Phase 7 — Port-forward Apply gate

**Scope.** Wrap the Apply button's action in the new primitive (Passive level). Add a confirmation dialog as defense-in-depth.

**Deliverables.**
- `SettingsDialog.xaml.cs:ApplyPortForwardButton_Click` (line 448) wraps its current body inside `RequireOperatorPresence(PresenceLevel.Passive, reason: "change SmartLink port settings", onConfirmed: () => { ... })`.
- Before `onConfirmed` runs, a confirmation dialog: "This changes SmartLink port forwarding settings on the radio itself. These settings persist on the radio across future connections by any client. Continue?" with Yes / No buttons.
- Dialog: accessible (AutomationProperties.Name on both buttons, default focus on No for conservative safety), Escape cancels (maps to No).
- Deny path: if RequireOperatorPresence denies, speech announces the denial and the Apply button stays unpressed (no dialog shown, no action taken).

**Accessibility notes.** Dialog's default focus on No matters for the "accidental Enter press" safety case. A user who muscle-memories Enter past a dialog won't change port forward state. User has to explicitly Tab to Yes and press it.

**Exit criterion.** Connected locally with `IsLocalPtt == true` (own radio at the shack) → Apply → dialog appears → Yes → action completes. Connected via SmartLink to someone else's radio → Apply → denial speech, no dialog, no action. Dialog Escape cancels action cleanly.

### Phase 8 — Help docs and 4.1.17 changelog draft

**Scope.** Update user-facing documentation for the Sprint 28 changes. Draft the cumulative 4.1.17 changelog covering Sprints 26 + 27 + 28. Tag/release happens post-sprint; this phase only drafts.

**Deliverables.**
- Help doc for Home (new file or section): what Home is, how F2 and the Home key navigate to it, what keystrokes work there.
- Help doc for Escape behavior: single-Escape collapses, double-Escape collapses all + returns to Home.
- Help doc for double-tap tolerance: where the setting lives, what the four levels mean, when to adjust.
- Help doc for the port-forward ownership gate: what it does, why it exists ("protects you from accidentally changing settings on a radio you don't own").
- `docs/CHANGELOG.md` entry for 4.1.17 — covers 26's session-owner groundwork, 27's three-tier networking, 28's home polish + safety primitives. Warm-conversational tone per CLAUDE.md. No sprint numbers, no track labels, no internal jargon.
- **Changelog integration into help CHM (added 2026-04-21).** The main `docs/CHANGELOG.md` becomes a topic inside the compiled CHM, surfaced as a top-level TOC entry titled "What's New" (not "Changelog" — user-friendlier). Single source of truth: the CHM build pulls `docs/CHANGELOG.md` into the help source tree at compile time (requires a small tweak to the help-compile step, not a mirrored file). Each version header becomes a CHM anchor for direct linking.
- **Help menu item "What's New" (added 2026-04-21).** Opens the CHM directly to the changelog topic. Single-press access to the most common user question ("what changed?"). Keyboard-accessible per existing menu conventions.
- **About dialog "What's New" integration (added 2026-04-21).** Two entry points from the About dialog:
  - The version string (e.g., "Version 4.1.17.42") becomes a clickable hyperlink that opens the CHM directly at this version's specific anchor. Answers "what's new in the version I'm actually running?" in one click.
  - A "View What's New" button that opens the CHM at the top of the changelog topic (full timeline, latest first).
  - Both entry points depend on version-exact anchors in the changelog markdown (e.g., `<a name="v4-1-17-42"></a>` at each version heading). Build-time check should verify the current-running version's anchor exists in the compiled CHM before shipping.
- **Future enhancement noted but out of Sprint 28 scope.** First-launch-after-update detection + "What's new in X.X.X?" dialog pointing at the changelog anchor is Sprint 29+ territory, now covered in `memory/project_sprint29_updater_vision.md`. Logging here so it's not forgotten; not implementing now.

**Notes on changelog tone** (reminder from CLAUDE.md): first-person voice, explain the *what* not the *how*, screen-reader details OK, no framework names, no ticket IDs, bullets describe state not developer action.

**End-of-sprint help audit (added 2026-04-21).** Beyond writing the four Sprint-28-specific help docs above, do a completeness pass across all existing help content to make sure Sprint 28's changes are reflected everywhere they should be:

- Any help doc that references the Network tab's tier vocabulary needs updating to reference the basic-view-first structure (Phase 10 changes).
- Any help doc that references the frequency field by name needs updating to use "Home" (Phase 4 rename).
- Any help doc that lists keyboard shortcuts for Slice / SliceOps / frequency needs updating to reflect the deconflicted map (Phase 5: R = RIT everywhere, X = XIT everywhere, `=` = transceive on slice fields, pan on nav-keys).
- Any help doc that mentions filter-edge bracket double-tap needs to mention the new user-configurable tolerance (Phase 1).
- The help TOC needs review to confirm "What's New" is in its intended top-level position.
- Help docs should reference `support@jjflexible.radio` as the contact channel where appropriate (lays groundwork for Sprint 29's "Ask for help" feature).

This audit is a completeness check, not a writing task — the goal is to catch anything the per-phase help-writing missed.

**Exit criterion.** All four new help docs exist and render correctly in the CHM build. Changelog entry for 4.1.17 is in `docs/CHANGELOG.md`, tone matches existing entries, no jargon. Completeness audit complete with any gaps patched.

### Phase 9 — Combined 4.1.17 release test matrix

**Scope.** Design a single test matrix covering all user-visible behaviors changed or added in Sprints 26, 27, and 28. Organized by user flow / feature area, not by sprint boundary — a tester should be able to run it without knowing which sprint any given check came from.

**File:** `docs/planning/agile/sprint28-test-matrix.md` (also serves as `release-4.1.17-test-matrix` — the two are the same document).

**Deliverables — matrix organized into these sections:**
- **Operating Home** — landing, speech on focus, Home key / F2 equivalence, all keystrokes per the deconflicted map, Escape-collapse single and double.
- **Slice operations** — per-slice volume, pan (nav-key triad), mute/sound/active/TX/transceive via `=`, lifecycle via `.` and `,`.
- **RIT / XIT** — R toggle on all three fields, X toggle on all three fields, adjustment via existing frequency-field digits.
- **Networking configuration** — all three tiers (Sprint 27): manual port config, UPnP enable, hole-punch enable; diagnostic probe run, copy report, save report; port-forward Apply gate behavior (local-operator allows; remote-SmartLink denies).
- **SmartLink session lifecycle** (Sprint 26 — WanSessionOwner work): session starts, persists across radio transitions, cleans up.
- **Double-tap tolerance** — set to each of the four levels, verify filter-edge bracket timing and Escape-collapse-all timing both respond.
- **Accessibility matrix** — JAWS, NVDA, Narrator: key announcements land correctly for each of the above flows. Optional: Braille output where the hardware is available.

**Exit criterion.** Matrix document exists, reviewed by Noel. Actual test pass happens during the release-prep phase (post-sprint).

### Phase 10 — Network tab progressive disclosure (added 2026-04-21)

**Scope.** Reshape the Network tab so basic users see a test-first entry point with plain-language controls, and the current Sprint 27 UI moves behind an "Advanced network settings" expander. No changes to the underlying `SmartLinkConnectionMode` enum or the Sprint 27 networking logic — this is a UI simplification, not a behavior change.

**Why this phase exists.** Sprint 27's UI uses "Tier 1 / Tier 2 / Tier 3" vocabulary that is accurate but jargon-heavy. A typical ham opens the Network tab with questions like "how do I reach my radio from outside?" and "is my connection working?" — not "which tier do I need?" Progressive disclosure (simple default, advanced on request) is the accessibility pattern that serves both audiences. Ship this in Sprint 28 (not Sprint 29+) so 4.1.17 users' first impression of the Network tab is the clean one, not the jargon-first one.

**Basic view (default, visible on tab open).**

The basic view's controls are specifically shaped around the support workflow as well as the end-user workflow. Anything Noel needs to direct a user to via `support@jjflexible.radio` must be visible without navigation — telling a user "click Advanced, then find Copy report" adds a step to every support exchange. The three support buttons (Test / Copy report / Save report) live at the basic level, always visible, no expander navigation required.

Always visible on the Network tab:

- Intro prose: "Most SmartLink connections work automatically. Run a test to check yours."
- **Connection status** — live-region-announced summary. Initial state "Not tested." After `Test my connection` runs, updates to "Working" or "Problem detected — try <specific suggested fix in plain language>".
- **[ ] Automatic network discovery (recommended)** — auto-select toggle (see auto-select section below).
- **[Test my connection]** button — runs the existing `NetworkTest` probe, translates results into plain language via the mapping below.
- **[Copy report]** button — always visible, enabled after the first test run. Copies the verbose diagnostic report to the clipboard for pasting into a support email.
- **[Save report]** button — always visible, enabled after the first test run. Saves the verbose diagnostic report to a file the user can attach to a support email.
- If auto-discovery is off (else hidden):
  - **[ ] Let JJ Flex set up my router automatically** (internally: UPnP, equivalent to Tier 2)
  - **[ ] Allow extended reach for hard-to-reach networks** (internally: hole-punch, equivalent to Tier 3)
- **[Advanced network settings…]** expander button that reveals the Sprint 27 advanced UI.

**Report verbosity rule (added 2026-04-21).** Copy/Save report always generate the verbose version regardless of the Verbose toggle's state. The Verbose toggle only affects in-app display of results. Rationale: reports sent for support must be maximally useful to Noel — more detail is cheap on his end, missing detail is expensive (extra round-trips asking "can you enable verbose and re-send?"). Users don't need to know to enable verbose before clicking Copy.

**Advanced view (expanded via the expander button).**

Advanced contains only the controls *specifically* for advanced users — the three support buttons (Test / Copy report / Save report) do NOT duplicate here because they already live in the basic view above. Advanced view contents:

- Tier 1/2/3 radio group (with tier vocabulary retained — at this point "Advanced" is visible so the jargon is consented-to).
- Manual port forwarding checkbox + TCP/UDP port fields + separate-ports toggle.
- Apply port forwarding / Test port buttons (these are port-manipulation-specific, not general network diagnostics).
- Verbose diagnostic toggle (affects in-app display only per the report-verbosity rule above).
- All Sprint 27 explanatory prose.

Advanced-view users see no behavioral change from Sprint 27's shipped UX aside from the removed duplication of the three support buttons (which are now always visible in basic view instead of duplicated in both places). They click one additional button to reach the tier controls and port configuration.

**User-visible label translations.**

Internal `SmartLinkConnectionMode` enum names stay. User-visible strings swap:

- `ManualPortForwardOnly` → basic: "Standard connection (recommended)"; advanced retains "Tier 1 — Manual port forwarding only (recommended default)".
- `ManualPlusUpnp` → basic: "Let JJ Flex set up my router automatically"; advanced retains "Tier 1 + 2 — Manual + UPnP".
- `AutomaticHolePunch` → basic: "Extended reach for hard-to-reach networks"; advanced retains "Tier 1 + 2 + 3 — Allow automatic hole-punch".

**Test result translation (basic view).**

- Test passed, everything reachable → "Working — your radio is reachable from outside."
- UPnP absent, manual port unreachable → "Problem detected — try 'Let JJ Flex set up my router automatically.'"
- UPnP + manual both failing → "Problem detected — try 'Allow extended reach for hard-to-reach networks.'"
- Symmetric NAT detected → "Problem detected — your network may not support SmartLink from this location. See help for alternatives."
- Network error / no SmartLink account → "Couldn't run the test. Check that you're signed in to SmartLink and connected to a radio."

**Design decision reversed 2026-04-21 — Option B ships in Sprint 28 Phase 10 with an off-switch.**

Original plan deferred auto-select to Sprint 29+ on the reasoning that "ship A, observe usage, design B from real-world failure data." That reasoning was recalibrated: JJ Flex has a small user base, so the data that would inform B's design never arrives at meaningful volume. At this scale, the 1:1 support relationship (`support@jjflexible.radio`, Noel personally) IS the data-gathering mechanism — rare edge cases become support emails, not statistical bug-report distributions. Shipping B with an off-switch lets first-principles design serve the common case; the off-switch + support email handles the edge cases. Flexibility-principle compliance: good default + user-togglable override + clear escape hatch.

**Auto-select rules (Phase 10 ships these).**

From NetworkTest probe results, three signals drive the selection. Rule: pick the *lowest* tier that passes.

- If manual port forward is reachable from outside → `ManualPortForwardOnly`. Announce: "Working — your router is already set up correctly."
- Else if UPnP is available and sets up a reachable port → `ManualPlusUpnp`. Announce: "Working — JJ Flex set up your router automatically via UPnP."
- Else if NAT preserves source ports → `AutomaticHolePunch`. Announce: "Working — using extended reach for your network type."
- Else → failure state. Announce: "Couldn't automatically reach your radio from outside. See help for setup options, or turn off auto-discovery in Advanced to configure manually."

Minimizes surface area by construction — no UPnP when manual works, no hole-punch when UPnP works. This also happens to align with what privacy/policy users would want if they had to pick, so the automatic behavior is defensible even when the user doesn't consciously consent to it.

**Off-switch: "Automatic network discovery" setting.**

- Default: on for new installs; respects existing manual tier selection on upgrade from 4.1.16 (if the user previously chose a tier manually, leave auto off; if they never touched it, default on).
- Visible in the basic view near the Test button, or at the top of Advanced expander — TBD which location during implementation, but must be discoverable.
- Description: "JJ Flex runs a test on startup and picks the best way to reach your radio. Turn this off if you have a specific reason to configure networking manually (corporate policy, security requirement, or support recommended it)."
- Keyboard-accessible, screen-reader announces state clearly.

**Manual override behavior.**

When user manually changes a mode in Advanced view, auto-discovery turns off automatically with a single announcement: "Auto-discovery turned off — you're configuring manually." No separate "first turn off auto" step required from the user.

**Revised Phase 10 scope estimate:** ~15-20 hours total (up from ~8 for A-only). Still one phase, heavier end of Sprint 28's phase distribution.

**Deliverables.**

- Modify `JJFlexWpf/Dialogs/SettingsDialog.xaml` Network tab: add expander, add basic-view controls (prose + status + test button + two toggles), move current Sprint 27 content inside the Advanced expander.
- Add basic-view event handlers in `JJFlexWpf/Dialogs/SettingsDialog.xaml.cs` that proxy to the existing advanced-view handlers (so behavior is shared, just with different labels).
- Add test-result-to-plain-language translation helper (new method or small utility class).
- Update `docs/help/md/networking-tiers-overview.md` to start with the basic-view concepts and have the tier-terminology section marked "For advanced users."

**Accessibility notes.**

- Expander has `AutomationProperties.Name="Advanced network settings. Expand to see technical options."` with clear collapsed/expanded state announcements.
- Basic-view toggles have clear `AutomationProperties.Name` values matching their visible labels.
- Test result announcements use a live region, Polite level (not interruptive — user-triggered).
- Basic view default-focuses the Test button so screen-reader users land on the actionable control first.

**Exit criterion.** Tab opens with basic view visible; tabbing hits Test button, status line, two simple toggles, Advanced expander, in that order. Test button runs probe and updates the plain-language status. Advanced expander reveals the full Sprint 27 UI intact. Mode changes made in either view reflect in the other (internal state is shared). Screen reader correctly announces expander state and test results.

### Phase 11 — Sprint archive cleanup

**Scope.** Move plan files and test matrices from prior sprints that are still loose in `docs/planning/agile/` into `docs/planning/agile/archive/`.

**Files to archive (confirmed 2026-04-21):**
- `sprint24-test-matrix.md` → archive (plan already archived)
- `sprint25-test-matrix.md` → archive (plan already archived)
- `sprint26-ragchew-keepalive-kerchunk.md` → archive
- `sprint26-test-matrix.md` → archive
- `sprint27-barefoot-openport-hotel.md` → archive
- `sprint27-test-matrix.md` (if Sprint 27 produced one before its merge) → archive
- `sprint28-home-key-qsk.md` (this file) → archive once Sprint 28 merges
- `sprint28-test-matrix.md` → archive once Sprint 28 merges

**Deliverables.** `git mv` each file to the archive subdirectory. Verify no stray references to the old paths in active code or docs.

**Exit criterion.** `docs/planning/agile/` contains only Sprint 29+ work (or is empty if nothing new started yet). All prior-sprint planning artifacts live under `archive/`.

---

## Post-sprint: 4.1.17 release ceremony

Once Sprint 28 merges to main:

1. **Test matrix pass.** Run the combined test matrix from Phase 9 against the merged main branch. Capture findings. Any blockers fix in a small follow-up branch; non-blockers roll to 4.1.18.
2. **Version bump.** Edit `JJFlexRadio.vbproj` `<Version>` to 4.1.17. Commit.
3. **Release build.** `build-installers.bat both release` — publishes to NAS nightly + historical + stable.
4. **Verify exe version.** `(Get-Item 'bin\x64\Release\net10.0-windows\win-x64\JJFlexRadio.exe').VersionInfo.FileVersion` — should read `4.1.17.<buildnum>`.
5. **Tag.** `git tag -a v4.1.17 -m "Release 4.1.17 — foundation phase closing: networking, home polish, safety primitives"`
6. **Push.** `git push origin main --tags` (to origin/nromey, NOT upstream/KevinSShaffer).
7. **Promote.** `publish-daily-to-dropbox.ps1` for the top-level debug zip; manually promote from NAS `stable\` to Dropbox top-level for the release installer.
8. **GitHub release.** `gh release create v4.1.17 --repo nromey/JJFlex-NG --title "JJFlexRadio 4.1.17" --notes "..." "Setup JJFlex_4.1.17.<N>_x64.exe" "Setup JJFlex_4.1.17.<N>_x86.exe"`

Per memory `project_foundation_phase.md`, 4.1.17 closes the foundation phase. Waterfall sprints begin after.

---

## Appendix A — Design decisions (recorded 2026-04-21)

These are the decisions reached in conversation on 2026-04-21 that shape this sprint's scope. Recorded here so the design rationale survives after the conversation scrolls out of context.

**DoubleTapTolerance — why 4 discrete steps, not a slider.** Radio-button groups with named values are more accessible to screen-reader users than continuous sliders. Names carry semantic meaning (Quick/Normal/Relaxed/Leisurely); the ms values are in the announcement for users who want calibration precision. Four steps is coarse enough that each feels meaningfully different.

**Why enum values ARE the ms count.** `Quick = 250` (not `Quick` mapped via a lookup table to `250`) means casting the enum to int gives the ms directly. No translation maintenance, no "two places to update." Adding a fifth step later is a one-line enum change; nothing else needs to touch.

**Escape pattern — single/double, focus-on-Home.** Single Escape = retreat from current scope. Double Escape = retreat to top scope (Home). Same semantic at two scales. One-sentence mental model users learn once and apply forever. Focus landing on Home after double-Escape closes the navigational loop — the action has a known destination, not "somewhere that used to be here."

**Home naming — why the rename pays back every use.** Current "Frequency Display" / "Frequency and VFO Display" is multi-word and forces functional description in every conversation, doc, menu. "Home" is one word, concept-first, and the Home key on the keyboard gets a natural second meaning. Internal code already uses "home position" in BrailleStatusEngine comments — the rename surfaces an existing concept rather than inventing one.

**Keystroke deconflict — why Loose over Strict.** Strict unification (every key must mean the same thing in every field) sounds principled but produces awkward outcomes — e.g., `.` / `,` on SliceOps or Home don't have sensible semantics. Loose resolves the actual conflicts (R, X) and fills obvious gaps (M, S, A, T) while leaving context-specific lifecycle keys where they belong. Fewer surprising behaviors, same muscle-memory payoff for the conflicting letters.

**Pan triad move from letters to nav-keys.** PgDn/Home/PgUp carry inherent spatial meaning via their keyboard layout (one direction, center, other direction). L/C/R are letter-based metaphors that require the user to know the letters' spatial associations. For screen-reader users navigating by touch, nav-keys are less cognitive load. SliceOps already uses this pattern; unifying onto it is a win, not a compromise.

**`=` for transceive.** Mnemonic is semantic: RX = TX. Preserves Jim-parity (a dedicated transceive hotkey continues to exist) while freeing X for XIT universally. International-keyboard caveat: verify with US-keyboard testers first; fall back to `+` if any tester hits a layout where `=` requires a modifier.

**RequireOperatorPresence — why two levels, why one implemented now.** Port forward is recoverable (bad setting, reset, move on). Firmware upload is consequence-heavy (brick a $5000 radio). Authorization strength should scale with consequence — over-gating everything to "maximum security" makes ceremony noise that users learn to blast through. Passive (check IsLocalPtt) suits port forward. ActiveChallenge (press PTT within N seconds to confirm) suits firmware upload. API is identical across levels so callers don't retrofit when the stricter level ships.

**Why ActiveChallenge is declared-but-not-implemented now.** Implementing the PTT-listen-with-timeout logic correctly benefits from having a real caller driving the design. Firmware upload is the first such caller and is gated on Flex firmware drops per memory `project_8600_unbox_firmware_trigger.md` — so we don't know the timing yet. Declaring the level reserves the design space and prevents accidental use; implementing comes when the firmware upload work starts.

**Why port-forward Apply gets both the primitive AND a confirmation dialog.** Defense in depth. Presence-check catches "not authorized to do this at all"; confirmation dialog catches "authorized, but pressed the wrong button accidentally." Different failure modes, both real, both deserve mitigation. Stacking them costs nothing once each exists individually.

---

## Appendix B — File inventory (files expected to change)

Not exhaustive; this list is the starting point for Phase 1's investigation.

- `JJFlexWpf/Settings.cs` (or equivalent) — new DoubleTapTolerance setting
- `JJFlexWpf/Dialogs/SettingsDialog.xaml` + `.xaml.cs` — radio group for tolerance; port-forward Apply gate wiring
- `JJFlexWpf/FreqOutHandlers.cs` — filter-edge migration (Phase 2); key deconflict across AdjustSlice / AdjustSliceOps / Home handler (Phase 5)
- `JJFlexWpf/Controls/FrequencyDisplay.xaml` — Home rename on AutomationProperties (Phase 4)
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` — Escape handler (Phase 3)
- `JJFlexWpf/KeyCommands.cs` — F2 description update (Phase 4); possible additions for the primitive's denial announcement
- `JJFlexWpf/MainWindow.xaml.cs` — focus routing for double-Escape returning to Home (Phase 3)
- `Radios/FlexBase.cs` — `RequireOperatorPresence` primitive (Phase 6)
- `docs/help/md/*.md` — Home, Escape pattern, tolerance, ownership gate (Phase 8)
- `docs/CHANGELOG.md` — 4.1.17 entry (Phase 8)
- `docs/planning/agile/sprint28-test-matrix.md` — combined release matrix (Phase 9)
- `docs/planning/agile/archive/` — receive the archived plan/matrix files (Phase 10)

---

**Plan status:** Ready for user review. No code changes yet.
