---
type: planning agenda — direct drop from Noel, distilled
date: 2026-05-08 evening
source: docs/planning/for-claude/Upcoming_work_musings.md (deleted after distillation)
status: agenda — items 1–6 each need their own decision/action; item 7 is ongoing
---

# Foundation phasing — agenda from 2026-05-08 evening musings

Noel framed this as: *"Since we now have plumbing in place, I think we need to do the following…"* — referring to the rarbox/roarbox/GitHub Actions/Cloudflare R2 infrastructure now in place. Several items are concrete next-action; some need a separate working session; one (item 5) is interactive admin-walkthrough.

## 1. Surface live planning state across all surfaces

### What Noel asked
1. Clean up `for-noel/` — keep keepers, file the rest somewhere durable.
2. Push the current feature branch to `main` so all surfaces (rarbox, roarbox, GitHub Actions, soon Netlify for `www.jjflexible.radio`) can see the active for-noel/for-claude state.
3. End-of-day seal must keep `jjf-data` up to date.

### State today
- `for-noel/` currently holds 3 docs:
  - `2026-05-04-foundation-drop-test-pull.md`
  - `2026-05-06-discovery-cascade-v3-full-memo.md` (canonical lives at `docs/planning/design/discovery-fallback-chain-v3.md`; this is the for-noel working copy)
  - `2026-05-06-track-c-braille-handoff.md` (Jamie Teh deliverable)
- `priority/` subfolder has only its README.
- Working branch is `feature/cache-writer-backport`. Main has end-of-day seal commit `7f2f31c8` (2026-05-07).
- `jjf-data` GitHub Action sync to R2 is operational per memory.

### Blockers / risks
- **Pushing feature → main is a non-trivial event.** The branch carries the cache-writer backport, R6 cascade work, foundation-drop docs, planning docs, and uncommitted working changes (per `git status`: modified CHM, modified roarbox parts list, modified TODO, deleted for-noel files, untracked for-claude files, untracked phase-0 walkthrough doc, untracked firmware-extraction research). Bundling all of that into a single main merge is consequential.
- The implicit ask is: *the main branch should reflect today's planning surface so on-box claude (rarbox / future roarbox) reads the same state.* That's reasonable as a posture. Question is whether to do it as one big merge tonight or stage it.

### Next move (asks Noel's call)
- **Option A — single merge tonight after seal:** stage current modifications, write a proper end-of-day seal commit, merge `feature/cache-writer-backport` into `main`, push. One event, big bundle.
- **Option B — file moves first, merge later:** clean up for-noel keepers, commit the for-claude/for-noel deletions and additions as a discrete "round-trip processed" commit on the feature branch first; then merge to main as a tidier event.
- **Option C — defer:** sleep on it, do the merge as a deliberate Saturday/Sunday move after surgery recovery is further along.

For the seal to keep `jjf-data` up, that needs to be wired into one of the seal scripts (memory mentions `publish-daily-to-dropbox.ps1` and the `backup-memory-to-nas.ps1` / `backup-private-to-nas.ps1` triad). Adding a `sync-jjf-data.ps1` step that pushes to the `nromey/jjf-data` repo so the GitHub Action picks it up would close the loop. This is small and additive — could land in the same commit as the cleanup.

## 2. Stand up `bh-data` / `data1.blindhams.network` test deployment

### What Noel asked
1. Create a private repo `nromey/bh-data` (sibling to `nromey/jjf-data`).
2. Stand up an R2 bucket for it.
3. Validate the deletion path — when blog posts churn, R2 needs to reflect deletions, not accumulate stale files.
4. Test on `data1.blindhams.network` first (point at it via a `www.test.blindhams.network` ⇒ caching-rules-check).
5. If the test works, swap `data1` ⇒ `data` and retire Andre's data server.
6. Then look at "the helper" in research mode — make it easier for Noel to use and approve nets.

### State today
- Existing pattern: `nromey/jjf-data` ⇒ R2 ⇒ `data.jjflexible.radio` (memory `project_data_provider_hosting.md`).
- Memory entry `project_blindhams_data_layer_migration.md` (2026-05-08) already names this as a queued migration: *"Same R2 + custom domain + GitHub Action sync pattern we built for data.jjflexible.radio applies cleanly to data.blindhams.network. ... 1-2 evening project; revisit when next touching BH infra."* Noel's musings now activate that work.

### Deletion-path question
Noel asked: *"You said that to delete files in your bucket, you would have to delete it in the repo and then re-push, how will that work on bh-data when files change constantly?"*

The answer is: a properly-written GitHub Action does git-tracked deletions correctly. The action diffs the commit against the prior state and issues `s3 sync --delete` (or equivalent R2 DELETE) for files removed from the repo. So a blog post being added is `git add + commit + push`, and a blog post being removed is `git rm + commit + push`. R2 mirrors the repo's tracked state.

The reason a separate "delete and re-push" came up earlier is probably about R2 *bucket* deletion (not file deletion within a bucket). For ongoing churn like blog posts, the GHA pattern is fine.

### Blockers
- Cloudflare caching rules need care: cached blog index pages need invalidation when posts change. Worth designing as part of the test deployment, not after.
- Andre's server's exact role / what's hosted on it — need to inventory before retiring it (so nothing gets dropped on the floor).

### Next move
This is a separate working session — probably a fresh CLI session focused on bh-data with internet access for R2/Cloudflare API work. Not overnight-coding scope.

## 3. 4.2 phasing — start planning

### What Noel asked
> *"4.0 phasing, Let's get on it. All we need to do is start to plan the phases, updater, crash reporter, etc. then we implement and test."*

(Read "4.0 phasing" as 4.2 — this is the version cascade currently in flight per `memory/project_foundation_phase.md`.)

Noel's also looking forward to *"sending myself fake feedback and crash stuff to see how your triage agent works on it"*.

### State today
- Foundation phase scope is: 4.1.16 → 4.2.0 cuts on substance + FlexLib 4.2 ready (per memory). 4.2.1 absorbs overflow + Customize Home.
- Sprint 29 hoped scope (per `memory/project_sprint29_updater_vision.md`): updater + channels + firmware update + auto-check.
- Sprint 29 crash reporter: foundational scope already decided (`memory/project_sprint29_crash_reporter_vision.md`), pre-Sprint-29 standalone fix landing — WPF DispatcherUnhandledException handler.
- Crash bundle triage flow designed (`memory/project_crash_triage_bundle_flow.md`): FastAPI receiver on rarbox raw-stores bundles, Claude triage agent classifies + dedupes + drafts response.
- `crashes.jjflexible.radio` is provisioned per memory.

### What's missing
A concrete sequenced plan that says: *here are the phases of 4.2, here's what ships in 4.2.0 vs 4.2.1 vs 4.2.x, here's the implementation order, here are the dependencies.* Today the pieces live across multiple memory entries and design docs but no single sprint plan unifies them.

### Next move
Write a formal Sprint 29 plan (or a "4.2 phasing roadmap" doc that Sprint 29 instantiates against). Should include:
- Updater (channel selection: stable / beta / nightly; silent install; first-time consent for nightly with "you may need to pick up the pieces" warning per item 4)
- Crash reporter (DispatcherUnhandledException handler ⇒ bundle ⇒ POST to crashes.jjflexible.radio receiver ⇒ triage agent run; *"recommend running triage on daily cadence for a while"*)
- Firmware update (chained-updater pattern per memory)
- Trace persistence (Sprint 29 priority — every networking diagnostic gets faster with persistent traces)
- Sprint 29 Diagnostics + Updates settings tabs UX consolidation
- Tuning unity + audio expander
- RIT/XIT scale-adjust mode
- Short Action Labels vocabulary (load-bearing for braille, CW nav, no-radio announcements)

This is the next big planning document. Could land as `docs/planning/agile/sprint29-plan.md` (or whatever ham-radio-flavored name we pick).

## 4. Firmware update flow — tied to crash reporter readiness

### What Noel asked
> *"Once we have all in place and the crash / feedback reporter are in place and working, I feel comfortable with releasing 4.0 because if things are broken, crash reporter (no friction tax principle) is in place to make it super easy to give us yummy data and stuff to fix."*

> *"Update agent will allow users to select the update path/channel, and this will install stuff silently. If someone's running a nightly or whatever have it silently install in the directory that it's installed in. First time (checkbox to confirm) if user selects nightly or beta (formerly debug) to let them know that they get to pick up the pieces / report them to us if they run daily/beta."*

### State today
- Firmware extraction from SmartSDR installer is authorized (`memory/project_firmware_extraction_authorization.md`).
- Firmware update transport protocol is fully reverse-engineered (`memory/project_firmware_update_transport_protocol.md`): `file filename` + `file upload N update` + raw `.ssdr` bytes. JJF needs no crypto code, fetch + relay only.
- Firmware-install dependency strategy decided (`memory/project_firmware_install_dependency_strategy.md`): no install-time gate; firmware update offered post-connect via chained-updater.
- "beta (formerly debug)" — Noel is renaming the channel. Captured as a memory update candidate (item 7).

### Naming change
*"beta (formerly debug)"* — the nightly debug channel is being renamed to **beta** for user-facing language. Internal builds stay "debug" (it IS a Debug build per `memory/MEMORY.md`'s nightly-debug-builds section), but the channel users pick says "Beta." This avoids the connotation that "debug" means "broken" — and makes the channel name align with industry convention.

### Next move
Folds into item 3's Sprint 29 plan. The chained-updater pattern and three-channel selector (stable / beta / nightly OR stable / beta — TBD) get speccd as part of the updater scope.

## 5. Azure Trusted Signing signup walkthrough

### What Noel asked
> *"Help me get signed up to architect signing on Azure. I swear if Azure, AWS, or Cloudflare goes down, the world's a goner."*

### State today
- Memory entry `project_microsoft_trusted_signing.md` (2026-04-29) documents the decision: *"Single Azure-hosted signing path covers firmware manifests, JJFlexible installer (when funded), and future signed artifacts. Default to it; deviate only with documented reason."*
- Code-signing cert is a **funding-gated milestone** per `memory/project_code_signing_cert_milestone.md`. It will solve the SmartScreen prompts (BlindCat anti-pattern #9).

### Next move
This is interactive admin-walkthrough work — probably best as a fresh session with Noel at the keyboard for Azure account setup, identity verification, payment, etc. Not overnight-coding scope. When we do it, walk through:
1. Azure subscription (does Noel have one already?)
2. Create Trusted Signing account
3. Identity validation (this is the slow part — can take days)
4. Configure signing policy
5. Wire into `build-installers.bat` post-build step
6. Verify SmartScreen behavior on a signed test installer

## 6. Failover — mirror jjf-data and bh-data to roarbox; warm-spare updater/crash/firmware infra

### What Noel asked
> *"Make sure that things fail over / copy a mirror of bh-data and jjf-data. We need to have roarbox set up as warm spare for updater(s), crash infra, firmware stuff, we have the space."*

> *"Put really old firmware on roarbox since it's going to have about 2 TB space. Roarbox IS ok to use for stuff that's not compute."*

### State today
- Roarbox is distinct from rarbox per `memory/project_roarbox_vs_rarbox.md` (2026-05-06): rarbox runs `crashes.jjflexible.radio`, user `ner`. Roarbox is the newer / faster server, role TBD, user being renamed to `ner`.
- Rarbox is currently the operational box; roarbox role is being defined.

### Roarbox's emerging role (from this directive)
- **Storage tier** for non-compute artifacts (~2 TB available).
- **Warm spare** for updater + crash + firmware infra.
- **Old firmware archive** — historic firmware images that don't justify hot-tier R2 storage.
- **Mirror target** for jjf-data and bh-data buckets.

### Blockers
- Roarbox's user-rename (`noel` → `ner`) needs to complete before scripted operations land cleanly.
- Mirror direction needs decision: is roarbox a passive read-only mirror, or does it stand in as primary if Cloudflare/R2 has an outage? The latter requires DNS failover (something like Cloudflare load-balancer with health checks).

### Next move
Separate operational session. Not overnight-coding scope. Item 3's Sprint 29 plan should reference this as a *deployment-time concern* (where do crash bundles land? primary R2 with roarbox sync? primary roarbox with R2 sync? both?), not as a *Sprint 29 build-time concern*.

## 7. Continue working through `for-noel/`

### What Noel asked
> *"I'll keep going through the stuff in for noel. I see lots of coding in our future."*

### State today
3 docs in for-noel/. Each will round-trip back to for-claude/ as Noel works through them on his own time. Standing process — no action needed from Claude until something lands in for-claude.

## What can ship overnight (taeraflops candidates)

Of the 7 items above, the only one that's actually *coding work that can run autonomously* is the **Sprint 28 design followup track** speccd in `sprint28-design-followup-track-instructions.md` (~80-100 LOC, well-scoped, all decisions locked). The 6 items in this agenda are planning / infra / admin work that needs Noel-in-the-loop or a focused fresh session.

If Noel wants to run something overnight on taeraflops, the design-followup track is the cleanest candidate. Everything else here lands tomorrow with Noel awake.

## Cross-references

- `docs/planning/active/sprint28-design-followup-track-instructions.md` — spawn-ready track for items (a)+(b) of the bug bundle DESIGN entries.
- `docs/planning/design/discovery-fallback-chain-v3.md` §13 — locked round-3 cascade ACKs (2026-05-08).
- `memory/project_foundation_phase.md` — 4.1.16 → 4.2.0 phasing context.
- `memory/project_sprint29_updater_vision.md` — Sprint 29 updater scope.
- `memory/project_sprint29_crash_reporter_vision.md` — crash reporter scope.
- `memory/project_blindhams_data_layer_migration.md` — bh-data migration prior planning.
- `memory/project_microsoft_trusted_signing.md` — Trusted Signing decision.
- `memory/project_roarbox_vs_rarbox.md` — roarbox/rarbox identity distinction.
- `memory/project_chained_updater_pattern.md` — chained-updater pattern firmware update follows.
