---
type: full design memo — fresh review pass
review needed: this is a LARGE memo with 8 phases (A-H); confirm scope, slice if needed, weigh in on open questions
priority: high — directly affects 4.2.0+ release planning
source: pulled from memory/project_sprint29_updater_vision.md per your "Pull X" request
---

# Sprint 29 updater + adjacent scope — full design pass (2026-05-02)

This memo has accreted significantly since 04-21. What started as "updater + channels" now spans **8 phases (A through H)** including deaf-blind multi-channel architecture (Phase G) and visible-status feedback (Phase H). **Worth re-asking up front: is this all one sprint, or do we slice?**

You said "Pull X" so this is the full memo for review. Use `**** ` for inline answers; `**** ACK` for sections that hold up; `**** SLICE` to push a phase out to a later sprint.

---

## Meta-question first (read this before the rest)

**The 8-phase scope is genuinely large.** Phases A-F are updater-and-crash-reporter work — they belong together. Phases G (deaf-blind speech-to-braille routing + cursor-routing add-on) and H (visible-status feedback for sighted users) are **architecturally a different fix** — they're "extend ScreenReaderOutput.Speak() to multi-channel output" and could be their own sprint independent of the updater work.
**** We may need to re-do the phases since the crash reporter has changed a bit since this document was created.

Two ways to think about this:

- **Bundle (current memo shape):** All 8 phases ship in Sprint 29 as one big release. Pros: amortizes the multi-channel announcement infrastructure across phases that need it (G needs it, H needs it, future CW notifications need it). Cons: huge sprint, easy to slip, hard to bisect regressions.

- **Slice (alternative):** Sprint 29 = updater + crash reporter (A-F). Sprint 30 (or 29.5) = deaf-blind + visible-status multi-channel (G + H). Pros: each ships earlier, each easier to test independently. Cons: G's groundwork happens later, deaf-blind users wait longer.

**** I'd do it in a bundle, especially since the two are now more crucial with  crash reporter, feedback tool, and the updater are now more load baring than when this doc was created.

Per `project_build_now_ship_later.md`, you can also authorize me to start the multi-channel infrastructure on a `track/multi-channel-announcements` branch independently — that work would be ready to plug into either sprint when scope decisions firm up.

**** Agreed though these three tools willl most likely be pulled into main more quickly because simply, they'll be required more quickly.

---

## Version cascade note

This memo says "Sprint 29 targets 4.1.18 (or later)" and references "Foundation phase closes at 4.1.17." Per your 05-02 decision (next release is 4.2.0, not 4.1.17), these need to update. The substantive content stands; only the version numbers cascade. I'll sweep this when you give me the green-light on the broader version cascade flagged in the design-review-queue digest.

****This will need to be done sooner rather than later i.e. more close to now since we really need crash / feedback reporter and this will allow testers to make absolutely sure that they ahve updated source, especially since updates will be more frequent with teh advante of teh updater tool with included channel selection (stable, debug, daily etc.)


---

## Phase A — Auto-update-to-latest (minimum viable updater)

Check against a manifest hosted at `jjflexible.radio` (or subdomain) for newer versions. On app launch (or periodic), non-intrusive notification if available.

**Open from memo:**
- Update-check cadence (launch-only vs. periodic vs. user-triggered)
- How users opt OUT of auto-check (privacy-respecting default)

**My read:** launch-only is the right starting cadence — runs once at app start, doesn't ping the server during normal use. Periodic adds load + complexity for marginal benefit at JJFlex's user volume. User-triggered "Check for updates now" button covers the manual case.

**** ACK on launch-only as default cadence. Though with two cores and r2 periodic launch if a check hasn't happened within 2 hours would be good especially if user is on daily. I don't see a need for a service to update code, this would be covered on launch. Silent install would be helpful though.`

**** Opt-out UX preference? (Settings checkbox? CLI flag? Registry key?)
**** Settigns preference and in dialog optt-out. If opted out for check on launch, settings could allow you to re-allow it. Add it to the crash/update settings page in settings.
---

## Phase B — Update channels (Stable / Beta / Nightly)

Matches three-tier distribution per `project_distribution_channels.md`. User picks channel; updater respects. Default Stable.

**My read:** clean and well-aligned with existing distribution model. No open questions worth re-litigating unless your thinking changed.

**** ack (dily, debug (less frequent) and standard. Maybe even "daily, beta, and stable, change perhaps from daily to nightly since generally that compile happens at night. With roarbox, we could do cy-cd work i.e. compile at night on a schedule since we should have bandwidth and disk and would be able to ship code to Cloudflare R2

---

## Phase C — Version picker with preview pane

List all available versions in selected channel. Arrow-keys navigate; focused version's details update a live region. Use cases: regression rescue, testing bisection, firmware pairing. Reuses Sprint 28's per-version CHM anchors.

**Open from memo (deferred to Sprint 29 planning):**
- Hide pre-release versions unless Beta/Nightly channel is selected?
- Warn loudly (or block) on downgrades from current installed version?
- Is there a "supported floor" below which versions can't be installed (e.g., pre-.NET-10-migration versions)?
- How far back does the version list go (all-time vs. last N releases with overflow "show all")?

**** Quick takes on each (`**** A: yes/no` style is fine), or `**** Defer all to sprint kickoff` if you don't want to decide now.
**** Let's cover these in chat if you would close to actually doing this track. There will be a floor for some of these updates especially if radio firmware won't support the flexlib library that's supported.  For instance, if someone's using a Flex, and firmware is 4.2, then you can't go back before 4.2 versions. It's all dependent on flexlib and how flexlib supports the firmware. Kind of dependent on the r4 bstupidity.

---

## Phase D — Firmware update path

JJFlex-managed path to upload firmware to connected FlexRadio. Uses `RequireOperatorPresence(ActiveChallenge)` primitive (Sprint 28 enum-declared). Pairs with `project_8600_unbox_firmware_trigger.md` and the FlexLib 4.2.18 merge (gated on firmware-update UI per `project_flexlib_4218_merge_sequencing.md`).

**Sequencing reminder:** the FlexLib 4.2 merge depends on this phase landing — Phase D blocks the FlexLib upgrade going to main. That makes Phase D a 4.2.0 critical-path item, NOT optional Sprint 29 scope.
**** Ack it's critical, and we'll have to see if we can figure out how to see radios on lan if we're lookin via flexl;ib 4.2. I made comments in the r4 document which explains this better. In essence, we need to figure out how smartsdr does it before we will be able to see the radio and how smartsdr connects to older radios even though version 4.2 of smart sdr and version 4.2.18 of flexlib with JJF can't see Don's radio with the current version. It's stupid, dumb, but super critical.


**** ACK that Phase D is critical-path for 4.2.0?

---

## Phase E — Support package generator ("Ask for help")

Help menu item bundles diagnostic data (network test, profile, firmware, radio model + state, app version, JJFlex session info) into a copyable text or saved file the user attaches to email. Generalization of Sprint 28's `Copy report`.

**Disclosure UX (locked-in 04-21):**
- Clear list of what's in the package, by category
- Per-category checkboxes to exclude
- Preview of actual content (not just category names)
- No-send escape hatch clearly visible
- Friction-tax exception: transparency earns its friction

**** Still good on disclosure UX? Or push back if you'd want a faster default-send-everything path (would violate principle but worth asking).
**** I'm good on disclosure UX.   Important for feedbakc and crash reporting. We could do some of this friction in setup as well, default to something and state that you can change it in settings (button shown to go to that settings page to change it).

---

## Phase F — Auto error / crash notification

On unhandled exception or crash, offer to generate crash-report package (Phase E + crash-specific additions: stack trace, last-few-actions log, memory state snapshot). User chooses to send or skip. Opt-in per crash, not persistent opt-out.

**Note:** this is the same scope as `project_sprint29_crash_reporter_vision.md` (which I'm pulling separately). The two memos overlap — this Phase F is the high-level rollup, the crash-reporter memo is the implementation detail. **Worth deciding which doc is canonical** so future memory references don't get confused.
**** See my write-up of thee crash notification junk

**** Recommendation: keep crash-reporter memo as canonical, mark Phase F here as "see crash-reporter memo." Agree?

---

## Phase G — Deaf-blind accessibility architecture

This is the architectural change — every speech announcement currently goes through `ScreenReaderOutput.Speak()`. Phase G routes that funnel to braille AS WELL.

### Cursor routing investigation (LOCKED 04-21)

Significant work already done on this — all cheap hypotheses tested and eliminated. Firm decision (04-21): **NVDA add-on + JAWS script for cursor routing.** Architectural state stabilized back to where Sprint 28 started, plus retained scaffolding (Ctrl+Shift+B BrailleStatusEngine toggle, position-comparison guard).

**Sprint 29 scope for cursor routing:**
- NVDA Python add-on hooking braille routing events, forwarding to JJFlex via IPC (named pipe or WM_COPYDATA).
- JAWS JSS script for equivalent JAWS integration.
- JJFlex IPC listener that receives routing events and invokes NavigateToField.
- NSIS installer bundles add-on/script with opt-in per screen reader.
- User-facing help doc explaining which screen readers support cursor routing.
- **Estimated 2-3 weeks combined.**

**** Still good on the add-on path? (Asking because you'd captured "blessing in disguise" framing 04-21 — checking if that conviction holds.)
***** Yep, we'll have to see if it all works.

**** Reserve `Ctrl+Shift+D` for braille toggle per the verbosity-architecture doc **** ack

### Speak-to-braille plumbing

Architecturally simpler than cursor routing — route every Speak call to braille as well. TOLK likely supports braille output via its API; if not, fall back to direct NVDA braille message API or status-line-layering. Estimated 1-2 days of work.

**** ACK on plumbing scope?
**** ack on plumbing scope if works.

---

## Phase H — Visible status feedback for sighted users

Same architectural funnel as Phase G — `ScreenReaderOutput.Speak()` becomes multi-channel. Phase H adds a status bar at bottom of main window with LiveSetting="Polite" semantics; shows most recent announcement for ~3 seconds then fades.

**Cost:** small — TextBlock bound to a property that Speak also updates. Maybe a day of work once multi-channel infrastructure exists.

**Why group with G:** same architectural fix. Doing them together amortizes infrastructure work; doing them separately duplicates the plumbing.

**** ACK on grouping G + H?
**** ack

**** Status bar position: bottom of main window? Or persistent toast at top? Or in the existing status area near the meter readouts?
**** bottom of main window, there are keys in JAWS and NVDA that can read this.

### Full channel inventory once G + H land

- Speech (existing, via ScreenReaderOutput.Speak)
- Earcon (existing, via EarconPlayer.*)
- Braille (new via G)
- Visible status (new via H)
- Future: CW prosigns / haptic for deaf-blind users with vibration transducers

JJFlex moves from "speech-primary with optional braille status" to "multi-channel announcement system."

**** ACK on the channel inventory framing?
**** ack

---

## Prerequisites status check

- `jjflexible.radio` domain — **acquired 2026-04-22** ✓
- DNS/hosting setup — deferred ("I'll set it up later"). Per WSL agent's blind-hams findings + this morning's discussion, hosting is now firming up (rarbox + roarbox + Cloudflare R2). **Status: in progress, not blocking design pass.**
- Hosted "box" for manifest + installers — rarbox available now ✓
- Code signing — **Azure Trusted Signing decided 2026-04-22** ($9.95/mo, 50k signatures/mo) ✓
- Sprint 28 foundations (CHM What's New, etc.) — landed in foundation phase ✓
**** I'll need to sign up for code signing, but I will do it when ready for all that.

**** ACK that prereqs are sufficiently in-place to start Phase A and B implementation?
**** ack

---

## Sprint 29 → release sequencing

Memo says "Sprint 29 targets 4.1.18 (or later)" — needs version-cascade update.
**** Target r.2

Realistic mapping given your 05-02 decisions:
- Phases A, B, D ship in **4.2.0** (Phase D is critical-path for FlexLib 4.2 merge per `project_flexlib_4218_merge_sequencing.md`).
- Phases C, E, F probably ship in **4.2.x** patch series — useful but not critical-path.
- Phases G + H either ship in 4.2.x or split to a later release (per the meta-question at the top).

**** ACK on this mapping, or revise?
**** See what I've written this evening and we'll see if it still maps right.

---

## Phases not yet decided / open

From the memo:
- Manifest format (JSON schema, signing, channel selector)
- Update-check cadence (launch vs. periodic vs. triggered)
- Channel-switching UX (settings dialog? first-launch opt-in for Beta?)
- Delta updates vs. full installer (probably full for simplicity)
- Firmware update UI (separate dialog? part of Radio menu?)
- How users opt OUT of auto-check (privacy-respecting default)

**** For each, `**** Quick answer` or `**** Defer to sprint kickoff` is fine.
**** Cover this in chat closer to running this, I don't think we can run this all until we figure out if / how firmware will be updated, but I'd like to say yes or no via chat.

---

## Confirm or revise

`**** ACK` — design holds, slice as proposed, ready for sprint planning.

`**** ` (specific changes) — apply changes; I'll update the memory entry. Major restructuring (e.g., extracting Phases G + H into their own memo) gets a re-pull.
**** Design holds so far unless firmware update changes require major changes to architecture based on discoverability requirements using flexlib 4.2.18
