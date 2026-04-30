# 2026-04-29 evening handoff — rarbox + Netlify game plan + parallel-track posture

**Author:** Track B Claude (FlexLib v4.2.18 worktree at `C:/dev/jjflex-flexlib-42`)
**For:** Track A Claude tomorrow + Noel + future sessions
**Status:** Sealed at end of 2026-04-29; Noel going to sleep, will provision Hetzner tomorrow.

## What today's session actually accomplished

Even though `git diff --stat` is small, the strategic ground covered today is significant:

1. **FlexLib v4.0.1 → v4.2.18 upgrade is done and parked.** Branch `track/flexlib-42` at `7aa93e47`, pushed to origin. All 8 phases plus a real Phase 5 fix (firmware-version-gated subs after a runtime regression bisected to 4 new sub commands that confuse older firmware). Audio confirmed live on Don's 6300 over SmartLink. ~600 opus packets in 30s once the gate landed.
2. **Merge sequencing locked.** Branch does NOT merge until (a) all foundation-phase work originally destined for 4.1.17 has landed on main, AND (b) firmware-update UI is in main. 4.2.0.x replaces 4.1.17 — there is no separate 4.1.17 release. See `project_flexlib_4218_merge_sequencing.md`.
3. **Firmware-update design memo locked.** `docs/planning/firmware-update-design.md` (on `track/flexlib-42`). All six open questions answered. Microsoft Trusted Signing chosen as the signing infrastructure. App-updater + firmware-updater interlock pattern documented (chained workflow, single combined-consent prompt).
4. **Two new memory anchors.** `project_microsoft_trusted_signing.md` and `project_chained_updater_pattern.md`.
5. **Noel's reframe of parallel work.** "Worktrees can stay parked indefinitely as research/builds we link in when ready" — see "Posture" section below.

## Architectural insight worth burning into the team's posture

Today Noel observed: "It freaked me out at first to see plan files ending in 'Now I'm ready for your ok to start' on tracks I'm not ready to wire in (multi-radio, braille, etc.). But: that work doesn't have to touch main yet. Claude can do things on parked branches that we link in when we're ready for the magic. No sense leaving worktrees and research unresolved — have at it, use GitHub like it's really supposed to be used."

**Implication for every Claude on every track:** if your scoped work doesn't need to ship today, it doesn't need to wait for an "ok to start" either. Land it on a parked branch, push to origin, document the merge prerequisites, move on. The branch itself is the holding pen. Don't stall waiting for a green light when the work is independent.

The `track/flexlib-42` branch is a perfect example. It's parked, pushed, documented. Nothing is lost; nothing is blocked. When the prerequisites land on main, one `git merge --no-ff` and a version bump and we're 4.2.0.x.

## What Noel is doing tomorrow morning

1. **Sleep first** (current 2026-04-29 evening).
2. **Provision Hetzner instance** ("rarbox"). Suggested specs: CX22 (~€4-5/month, 2 vCPU, 4GB RAM, 40GB disk), region Ashburn or Hillsboro for US, Ubuntu 24.04 LTS, SSH key during provisioning (no password auth ever).
3. **Confirm SSH alive**, capture the IP.
4. (Optional, parallel) **Create empty private GitHub repo `nromey/jjflex-data-provider`** + a Netlify Pro site connected to it, custom domain `data.jjflexible.radio`.
5. Then back to Track A radio-testing whenever ready.

## The two-tier hosting story (locked)

Two services, two roles:

### Netlify Pro ($9/month, already paid for, Noel migrated up because update cadence exceeded free tier)

**Role:** Static-file CDN + auto-HTTPS + custom-domain wrapper for read-only content.

**Lives here:**
- `jjflexible.radio` (future public site, Jekyll or similar — mirror conventions from `nromey/bh-network` where applicable)
- `data.jjflexible.radio` (firmware blobs, manifest.json, release notes, future signed installer drops)
- `www.blindhams.network` (already there per Noel)

**Why Netlify here:** zero ops surface, built-in CDN (faster for users on rural broadband — relevant to our user demographic), automatic certs, deploy-via-git-push, no SSL renewal cron. The 1TB/month bandwidth ceiling is comfortably above any realistic firmware-download volume for the foreseeable future.

### Hetzner US (rarbox, to-be-provisioned)

**Role:** Compute host for anything Netlify can't do.

**Lives here:**
- `data.blindhams.network` workload migration target (currently on Andre's London server — see "bh-network reality" below). Python cron jobs that derive a schedule.json from a nets.json, plus the net add/remove/edit tooling for net-listing coordinators, plus solar data.
- Future JJFlex backend bits that need state or long-running processes (crash report intake might end up here OR as a Netlify Function — TBD per scope)
- Anything else where SSH access + a real filesystem matters

**Why Hetzner here:** Netlify can't do long-running cron-ish Python workloads, can't do persistent-state services without a third-party DB, can't give us shell access for debugging. Hetzner is the dynamic-services tier. Hardening + certs is fine — Noel's done it before on Andre's box.

**Migration note (Noel's intent):** move blindhams.network's `data.` workload from Andre's London server to rarbox in the US. Lower latency for US-based users, plus consolidating ops control under Noel's account.

## bh-network reality (for any Claude who has to touch it)

The reference repo is `https://github.com/nromey/bh-network` (Noel typo'd it as `bh-netwsork` in chat).

**Current architecture:**
- `www.blindhams.network` — static Jekyll-style site on Netlify
- `data.blindhams.network` — NOT Netlify; runs on Andre's London server. Python scripts on cron read `nets.json` (net names, categories, etc.) and produce `schedule.json` consumed by the static site
- Net add/remove/edit tool (admin-tier) on Andre's server lets Noel + net-listing coordinators submit nets
- Public users can also submit nets via the site (form-fed into the same backend)
- Noel's solar data also lives in the bh-network repo

**Known issues to investigate:**
- A 502 error somewhere in the chain — Noel hasn't pinned it down. Worth a passing pair of eyes.
- A GitHub Actions workflow has been complaining for ~2 months that it needs maintenance or it'll be disabled. Find it, figure out what it wants, fix or retire it.

**Why this matters to JJF:**
1. Same Netlify-Pro setup pattern is what we want for `jjflexible.radio`. Mirror conventions (folder layout, deploy story, DNS pattern) where they apply rather than reinventing.
2. The bh `data.` migration target IS rarbox — same machine that'll later host JJF backend bits. One ops surface, two products.
3. The Python-cron-derives-static-JSON pattern is potentially reusable for JJF data products (e.g., a "current alpha firmware versions" feed if Flex ever exposes one programmatically).

## Recommended track structure tomorrow (if Noel wants parallel work)

Each can run as an independent Claude session in its own worktree. None blocks the others.

### Track A — Radio testing on the foundation-phase work
Status quo: Noel's usual real-radio loop on Sprint 28/29 deliverables that have to land before 4.2.0.x can ship. **Owner: Noel + a Claude in the main worktree.**

### Track B — Netlify data-provider site setup
Stand up `data.jjflexible.radio`. Folder structure, stub manifest, `_redirects`/`netlify.toml`, README. Smoke test with `curl`. **Owner: a Claude session that can `git push` to the new repo. Needs Noel for: repo creation + Netlify site connection + DNS confirmation.**

### Track C — Rarbox hardening + bh-network `data.` migration
Once Noel's provisioned the box and confirmed SSH:
- SSH config tightening (key-only, port maybe non-22, no root login)
- ufw firewall rules
- fail2ban
- unattended-upgrades
- Reverse proxy (Caddy preferred — auto-HTTPS, simpler than nginx for our profile)
- Install Claude Code on the box itself per Noel's "get claude loaded on rarbox" idea — lets the next Claude operate from the box without relay.
- Then plan + execute the bh `data.` migration from Andre's box. This is its own sub-plan, written when we have an inventory of what's actually running on Andre's machine.

**Owner: a Claude session given SSH access to rarbox.**

### Track D — bh-network reading + stale GitHub Action fix
Read `nromey/bh-network` end-to-end. Document conventions for Track B to mirror. Fix the stale GitHub Action. Investigate the 502.

**Owner: an autonomous Claude session that can clone, read, optionally PR. Doesn't need SSH or radio access. Smallest blast radius — good "first thing tomorrow" for whichever Claude is fastest to spin up.**

## Posture for Noel from this Claude (tone-setting, not directive)

You felt overwhelmed today seeing plan files end in "ready for your OK to start." That feeling is actually a UX bug in how Claudes (myself included earlier) hand off parallel work. The fix isn't to slow the parallel work — it's to make sure each branch self-documents its merge prerequisites so you can review on YOUR cadence, not theirs.

The `track/flexlib-42` branch is the model. It exists, it's tested, it's pushed, the merge plan is in `JJFlex-TODO.md`, and the prerequisites are clear. You don't need to act on it today, tomorrow, or next week. When the prerequisites organically land, the merge happens. Same goes for multi-radio research, braille work, anything else parallel. Branches are cheap; review when ready.

You ended today with: design memo locked, signing infrastructure decision burned in, firmware-updater interlock pattern memoized, FlexLib upgrade audio-confirmed and gated, branch on origin. That's a solid day even though `git diff --stat` is light. Sleep well.

— Track B Claude
