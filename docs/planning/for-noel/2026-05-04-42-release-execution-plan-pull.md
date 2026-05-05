# 4.2.0 Release Execution Plan + Cloudflare R2 Runbook

**Type:** strategic plan + actionable runbook (read in this order)
**Urgency:** post-recovery — none of this is surgery-week work. Read when you're up to it.
**Trigger:** 2026-05-04 — your pivot from "more research" to "start the blocker work, get rarbox up, get off SmartSDR." This doc converts the existing memory + design memos into an executable sequence.

---

## TL;DR

You have **three blockers** for cutting 4.2.0 to main per `memory/project_main_branch_41_posture.md`:

1. **Firmware can be safely copied to radios** — code (firmware updater) + server (host the .ssdr blobs)
2. **Crash reporter / feedback wired up** — code (in-app upload UX) + server (rarbox receiver endpoint)
3. **Update plan operational** — code (auto-check + channel selection + silent install) + server (manifest hosting on R2)

The good news: **every one of these is mostly designed** — the memory entries `project_sprint29_crash_reporter_vision.md`, `project_sprint29_updater_vision.md`, `project_firmware_distribution_decision.md`, `project_data_provider_hosting.md`, and `project_chained_updater_pattern.md` collectively spec ~85% of the work. What's left is **execution sequencing** and a few concrete server-side setup steps that need your hands on the Cloudflare and rarbox UIs.

The plan below sequences the work into five phases. Phase 0 (server-side foundation) is the runbook half of this doc. Phases 1-4 are code tracks that I'll spec into TRACK-INSTRUCTIONS files when you're ready to spin them up.

---

## The five phases

### Phase 0 — Server-side foundation (Noel-driven, runbook below)

**Why first:** every other phase reads from or writes to this. Crash reporter needs the rarbox receiver endpoint. Updater reads the R2 manifest. Firmware updater fetches blobs from R2.

**Scope:**
- DNS for `jjflexible.radio` transferred to Cloudflare (consolidates with your existing Cloudflare account)
- R2 bucket provisioned + custom domain `data.jjflexible.radio` configured
- GitHub Action in `nromey/jjf-data` syncs to R2 on push
- nginx on rarbox + DNS for `crashes.jjflexible.radio` (the receiver endpoint)
- Receiver endpoint stub on rarbox (accepts HTTPS POST, stores bundle to disk; no parsing, no triage yet)

**Effort:** ~2-3 hours of your time on Cloudflare + rarbox UIs across one or two sessions. Not coding-shaped; mostly clicks and config edits. Step-by-step runbook is in the Phase 0 Runbook section below.

**Outputs:**
- `data.jjflexible.radio` serves files from R2 (test with a sample manifest.json)
- `crashes.jjflexible.radio` accepts uploads on rarbox (test with a curl POST)
- GitHub Action triggers on push to `nromey/jjf-data`

**Dependencies:** none — this unblocks everything else.

---

### Phase 1 — Updater code (smallest surface, validates the chain)

**Why second:** the updater is the smallest, simplest piece (download a manifest, fetch a signed installer, run it silently). Implementing it FIRST proves the R2 + manifest + signed-binary chain works end-to-end, before staking crash reporter or firmware updater on the same plumbing. If something's broken, find it here.

**Scope (per `memory/project_sprint29_updater_vision.md` — bundle-don't-slice all 8 phases):**
- Phase A: manifest schema + JJ Flex client manifest fetch (read `https://data.jjflexible.radio/jjflex-manifest.json`)
- Phase B: channel selection (nightly / stable; defaults to stable)
- Phase C: download + verify Microsoft Trusted Signing signature
- Phase D: deferred — firmware updater (Phase 3 below)
- Phase E: silent install (msiexec or NSIS with `/S` flag)
- Phase F: launch-time check (with 2-hour rate limit per memory)
- Phase G: settings tab integration per `memory/project_sprint29_diagnostics_settings_tab.md`
- Phase H: status bar at bottom of window during update operations

**Effort estimate:** ~1500-2500 LOC across ~10-15 files. Settings UI, manifest parser, HTTP client, signature verifier, msiexec wrapper, channel logic, status bar control.

**Track shape:** `track/updater` worktree, branched from `sprint28/home-key-qsk` (per `project_main_branch_41_posture.md` — main = 4.1 working branch until 4.2 ships, so foundation/polish work merges to main as 4.1 trunk). Use placeholder URL during dev; swap in real URL once Phase 0's manifest is published.

**Dependencies:** Phase 0 complete (R2 bucket + manifest URL exists; Microsoft Trusted Signing certificate is the OTHER prerequisite — funding-gated per `memory/project_code_signing_cert_milestone.md`. Until cert lands, ship unsigned debug builds through the updater for testing; require signing for the actual 4.2.0 release).

---

### Phase 2 — Crash reporter / feedback code

**Why third:** depends on Phase 0's receiver endpoint. Crash reporter design is the most complete of the three (full ACK on `project_sprint29_crash_reporter_vision.md`); execution is just wiring the design.

**Scope (per the ACK'd memo):**
- WPF DispatcherUnhandledException handler verification (grep first; if missing, that's standalone first-fix)
- Modal-on-next-launch dialog batching pending crashes
- Bundle assembly: existing .dmp + .txt + recent trace tail + connected radio info + network diag
- HTTP POST to `crashes.jjflexible.radio` (URL hardcoded in code per Noel preference, swappable via DNS)
- "Show what's in the report" transparency viewer
- "Whoopsie" test command in Command Finder + SetFreq easter-egg path
- User feedback sibling — same upload path, different trigger, optional diagnostic pack
- Settings → Diagnostics tab integration

**Effort estimate:** ~800-1200 LOC. Smaller than updater because the bundle assembly already exists in `CrashReporter.vb` — adding the upload UX on top is the new work.

**Track shape:** `track/crash-reporter` worktree, also branched from `sprint28/home-key-qsk`. Build-now-ship-later authorized per the memory entry's "track authorization" section.

**Dependencies:** Phase 0 receiver endpoint live + Phase 1 settings tab infrastructure (the Diagnostics tab is shared between updater settings and crash settings — order Phase 1 first or build the tab in Phase 2 if Phase 1 hasn't gotten there yet).

---

### Phase 3 — Firmware updater code

**Why fourth:** the trickiest of the three. Code-side firmware push to the radio is the unknown — we have the .ssdr blobs and we know FlexLib has `Radio.UpdateFirmware()` or equivalent, but we haven't actually pushed firmware from JJ Flex to a radio. The SmartSDR ILSpy decompile authorization (per `memory/project_smartsdr_decompile_authorization.md`) is the fallback if the FlexLib API path turns out to be incomplete.

**Scope (per `memory/project_firmware_distribution_decision.md` + `project_chained_updater_pattern.md`):**
- Firmware manifest schema (separate from app manifest — per-radio-model entries with version, size, .ssdr URL, minClientVersion)
- Per-radio-model firmware fetch (radio reports model + serial via FlexLib; client looks up in manifest)
- Pre-flight checks (radio reachable, current firmware version queryable, no pending operations)
- Download .ssdr blob from `data.jjflexible.radio/firmware/<model>_v<version>.ssdr`
- Verify checksum against manifest (SHA-256)
- Push to radio via FlexLib firmware-update API (this is the unknown — may need ILSpy validation)
- Progress reporting + status bar integration
- Post-update verification (radio reboots, reports new firmware version)
- Chained-updater integration: if radio's firmware advertises a `minClientVersion` higher than running JJ Flex, chain through app updater first (single combined-consent prompt per `project_chained_updater_pattern.md`)

**Effort estimate:** ~1000-1500 LOC. The unknowns add risk; effort could be 2x if FlexLib's firmware API turns out to be incomplete and we need to reverse-engineer the protocol from SmartSDR.

**Track shape:** `track/firmware-updater` worktree, branched from `track/flexlib-42` (this one DOES need FlexLib 4.2.18 — firmware API may differ between FlexLib versions, and we want to test against the version we're shipping in 4.2.0).

**Dependencies:**
- Phase 0 (R2 hosting for .ssdr blobs)
- Phase 1 (chained-updater integration)
- **R4/R5 discovery resolution** (per `memory/project_flexlib_4218_discovery_investigation.md`) — if the FlexLib 4.2.18 silent-discovery bug isn't fixed, we can't reliably even GET to a connected radio to push firmware to. Don's R5 trace is the trigger; recovery-week timing.
- Firmware extraction script (the 30-line PowerShell from `project_firmware_distribution_decision.md`) running on your end whenever Flex ships a new SmartSDR.

**Open question (per `memory/project_firmware_install_dependency_strategy.md`):** if FlexLib 4.2.18 requires a specific radio firmware version, does JJ Flex 4.2 install: (a) block on missing firmware? (b) allow with rollback? (c) force firmware update? Decision pending the discovery investigation outcome.

---

### Phase 4 — Integration testing + 4.2.0 release

**Scope:**
- Wire up: app starts → updater checks manifest → user sees update available → downloads + installs → on next launch, manifest signals firmware update available for connected radio → chained prompt → firmware downloads + pushes → radio reboots → connect succeeds.
- Crash reporter exercise: trigger "whoopsie" → bundle assembles → uploads to rarbox → triage process reads it (Claude-as-triage or Noel-as-triage per `memory/project_sprint29_crash_reporter_vision.md` "Ingestion-side design").
- Don + Justin smoke test on real radios (Don's 6300, Justin's 8400).
- Microsoft Trusted Signing applied to release installer.
- Version bump JJFlexRadio.vbproj to 4.2.0; release process per CLAUDE.md "Releases" section; tag, push, GitHub Release with both x64/x86 installers.

**Effort estimate:** ~1-2 weeks of integration + smoke testing + bug fixes. The "real" effort here is in HOW MUCH SHAKES OUT during integration — could be a week of polish, could be a month of "oh that's broken too."

**Dependencies:** all prior phases.

---

## How this plan handles "what runs autonomously vs. needs Noel attention"

**Autonomous-able (CLI sessions can run while you're recovering / out / sleeping):**
- Phase 1 updater code (with placeholder URLs for testing)
- Phase 2 crash reporter code (likewise)
- Phase 3 firmware updater code, EXCEPT the real-radio push validation
- Most of integration coding

**Needs you (Noel) — surgery-week-no-go-list, post-recovery work):**
- Phase 0 entirely (Cloudflare + rarbox UIs)
- Real-radio firmware push validation (Don's 6300, Justin's 8400)
- Microsoft Trusted Signing application
- Final release decisions + version bump + GitHub Release

**Recommended posture during recovery week:**
- I do NOT spin up new code tracks autonomously. Stuck-modal + bug-bundle finish (bug-bundle CLI is still chugging), they merge, you review. That's enough activity for this week.
- Phase 0 runbook waits for whenever you have a reading-and-clicking session post-recovery. Could be next weekend.
- Code phases (1-3) wait for explicit ACK after Phase 0 completes — no point building against placeholder URLs if Phase 0 is days away anyway.

---

## Phase 0 Runbook — Cloudflare R2 + DNS + rarbox

**Time estimate:** 2-3 hours total, spread across 1-2 sessions if convenient. Each section is independently checkpointable — stop after any heading and resume later.

### Prerequisites

- 1Password unlocked (you'll need to record API tokens + DNS values)
- Credit card on file with Cloudflare (R2 enable requires this even on free tier — see `project_data_provider_hosting.md` "Setup gotcha")
- Access to your domain registrar for `jjflexible.radio` (Porkbun / NIC.RADIO / wherever you bought it)
- Tailscale up so you can SSH to rarbox

### Section A — Transfer DNS for jjflexible.radio to Cloudflare

Per the steps already documented in `memory/project_data_provider_hosting.md` "DNS for jjflexible.radio — moving to Cloudflare":

1. **Cloudflare dashboard → Add Site → enter `jjflexible.radio`.** Cloudflare assigns two nameservers (e.g. `<word1>.ns.cloudflare.com` / `<word2>.ns.cloudflare.com`). Record both in 1Password.

2. **Eyeball the imported DNS records.** Cloudflare scans your existing DNS and imports what it finds. Verify:
   - Any MX records (if you have email on the domain)
   - Any TXT records (SPF, DKIM, domain verification)
   - Existing A/CNAME records pointing at the marketing site

   If anything's missing, add it manually now BEFORE you flip nameservers — missing records mean dropped service during the propagation window.

3. **At your registrar, replace nameservers** with Cloudflare's two from step 1. Wait 5 min – few hours for propagation; Cloudflare dashboard shows "Active" once the world sees the change.

4. **Verify.** From PowerShell: `Resolve-DnsName jjflexible.radio` should show Cloudflare-served records.



### Section B — Create R2 bucket

1. **Cloudflare dashboard → R2 → Create bucket.** Name: `jjflex-data` (or similar; the bucket name doesn't surface to users — they see the custom domain). Region: Automatic.

2. **Add credit card if prompted.** First-time R2 enable requires a billing instrument. Free tier covers your usage; you won't be charged.

3. **Confirm bucket created.** Should show empty in the R2 dashboard.



### Section C — Custom domain for `data.jjflexible.radio`

1. **In R2 → your bucket → Settings → Custom Domains → Connect Domain.** Enter `data.jjflexible.radio`.

2. **Cloudflare auto-creates the CNAME record.** Verify in DNS tab; should show `data CNAME <bucket-id>.r2.cloudflarestorage.com` (proxied — orange cloud — for free SSL + CDN).

3. **Wait for SSL provisioning** (usually <5 min). Test: `curl -I https://data.jjflexible.radio/` should return a 404 (bucket is empty) but with valid SSL headers.

4. **Upload a test file** via the R2 dashboard ("Upload" button). Upload any small text file as `test.txt`.

5. **Verify access:** `curl https://data.jjflexible.radio/test.txt` should return the file contents. Delete the test file after.



### Section D — R2 API tokens for GitHub Action

1. **Cloudflare dashboard → R2 → Manage API Tokens → Create API Token.** Name: `jjf-data-github-action-sync`.

2. **Permissions:** "Object Read & Write" — scoped to bucket `jjflex-data` only.

3. **Generate token.** You'll see (one time only) the Access Key ID + Secret Access Key. Record BOTH in 1Password under a new entry "Cloudflare R2 — jjf-data sync".

4. **Endpoint URL.** Also record the R2 S3 endpoint (`https://<account-id>.r2.cloudflarestorage.com`) — you'll need this in the GitHub Action.



### Section E — GitHub Action sync in nromey/jjf-data

I'll draft the workflow file — you just need to commit it after setting secrets.

1. **In `nromey/jjf-data` GitHub settings → Secrets and variables → Actions:**
   - Add secret `R2_ACCESS_KEY_ID` (from Section D)
   - Add secret `R2_SECRET_ACCESS_KEY` (from Section D)
   - Add secret `R2_ENDPOINT` (from Section D, the `https://...r2.cloudflarestorage.com` URL)
   - Add variable (not secret) `R2_BUCKET` = `jjflex-data`

2. **Tell me to draft the workflow file** (`.github/workflows/sync-to-r2.yml`). I'll produce a ~30-line YAML using `aws-cli` + `aws s3 sync`. You commit it.

3. **Test:** push a file to `nromey/jjf-data/test/` — Action runs, file appears at `https://data.jjflexible.radio/test/<filename>`.



### Section F — nginx + DNS for crashes.jjflexible.radio on rarbox

1. **Cloudflare DNS → Add A record:** `crashes.jjflexible.radio` → `178.156.204.128` (rarbox's public IP per `project_rarbox_hardening.md`). **Proxied (orange cloud) ON** — gives free SSL + DDoS + origin-IP hiding.

2. **SSH to rarbox** (Tailscale): `ssh ner@rarbox.macaw-jazz.ts.net`.

3. **Install nginx** if not already:
   ```bash
   sudo apt update && sudo apt install -y nginx certbot python3-certbot-nginx
   ```

4. **Tell me to draft the nginx config** for the receiver endpoint. It'll be a ~20-line site config that:
   - Listens on 443 (Cloudflare-proxied, so origin can be plain HTTP technically, but we'll do TLS end-to-end via Cloudflare's Origin Certificate)
   - Routes POST `/crashes` to a simple bundle-storage path
   - Routes POST `/feedback` to the same handler with a different tag
   - Returns 200 on accepted, 4xx on rejected

5. **The actual receiver** — for v1, just a Python script (Flask or FastAPI) running under systemd that accepts POST, validates content-type + max size, writes the bundle to `/var/lib/jjflex-receiver/<YYYY-MM-DD>/<uuid>.zip`. ~50 LOC. I'll draft this when you ACK Phase 0.

6. **Cloudflare Origin Certificate** (preferred over Let's Encrypt for Cloudflare-proxied origins): Cloudflare dashboard → SSL/TLS → Origin Server → Create Certificate. Install on rarbox per nginx config.

7. **Test:** `curl -X POST -F "file=@test.zip" https://crashes.jjflexible.radio/feedback` → should return 200 and the file should appear in `/var/lib/jjflex-receiver/<today>/`.



### Section G — Verify the whole chain

After A-F, you should have:
- `data.jjflexible.radio` serving files from R2 (try `curl https://data.jjflexible.radio/test/anything.txt`)
- `crashes.jjflexible.radio` accepting uploads on rarbox (try `curl -X POST -F "file=@small.zip" .../feedback`)
- GitHub Action triggering on push to `nromey/jjf-data`

**If all three work: Phase 0 is DONE.** Code phases unblock. Tell me and I'll spin up `track/updater` first.

---

## Open questions for me to answer back

### 1. Sequencing — Phase 1 (updater) before or after Phase 2 (crash reporter)?

My recommendation: **Phase 1 first.** Smaller surface, validates the chain end-to-end before staking crash reporter on the same plumbing. Phase 2 reuses the manifest-fetch + signature-verify infrastructure from Phase 1.

The argument for Phase 2 first: it's higher user-visible value (crash reports unblock support work). And it's slightly more decoupled — the receiver endpoint exists independently of any manifest plumbing.

Either is defensible. Pick whichever you'd rather see working first.



### 2. Should we wait for the Microsoft Trusted Signing certificate before shipping any updater build?

Per `memory/project_code_signing_cert_milestone.md` the cert is funding-gated. Without it, every update download fires a SmartScreen prompt — exactly the BlindCat anti-pattern #9 we exist to avoid.

Options:
- **(a)** Don't ship the updater publicly until cert is in place. Build + test the code privately; gate public 4.2.0 release on cert acquisition.
- **(b)** Ship updater with unsigned binaries first (debug builds for testers — Don, Justin already trust SmartScreen prompts). Cert lands later, signing flips on, no code change.
- **(c)** Self-signed cert as interim — works but still produces SmartScreen warnings on user machines that haven't trusted our self-signed CA.

My recommendation: **(b)**. Don and Justin's testing of the updater chain doesn't need signing; the cert gate applies at the public 4.2.0 release moment. Phase 1 code can proceed; Phase 4 gates on cert.



### 3. Receiver endpoint — Python (Flask/FastAPI), Node, or something else?

For the rarbox receiver. v1 is "accept POST, save bundle to disk, return 200." Could be:
- **Python + FastAPI** — modern async, well-supported, ~50 LOC. Personal preference of mine.
- **Python + Flask** — simpler, more battle-tested. Same LOC.
- **Node + Express** — if you'd rather stick to one language across the stack.
- **Pure nginx** — actually sufficient for v1 (`client_body_in_file_only` directive). Zero application code. Adds difficulty for the v2 "triage agent" features later.

My recommendation: **FastAPI**. Trivial to extend with the v2 triage features (Claude-as-triage agent reads new bundles, classifies against memory entries, drafts responses). Pure-nginx forces a bigger rewrite later.



### 4. Timing — when do you actually want me to draft the nginx config + GitHub Action workflow + receiver Python code?

Three options:
- **(a)** Now — I draft everything as separate for-noel docs/files this session. You read post-recovery, ACK, execute Phase 0.
- **(b)** When you start Phase 0 — you tell me "I'm at section E, draft the workflow." Just-in-time delivery.
- **(c)** All at once when Phase 0 is fully unblocked — after your recovery, you read this plan, approve, then I draft everything in one batch.

My recommendation: **(c)**. Drafting now means stale code by the time you execute (the API tokens won't exist yet, the bucket name might change). Just-in-time means no waste.



### 5. The Phase 3 firmware-install-dependency strategy — pick now or wait for R5 trace?

Per `memory/project_firmware_install_dependency_strategy.md`. The decision is:
- **(a) Block install on missing firmware** — strictest; user can't install JJ Flex 4.2 unless their radio has compatible firmware. Forces an upstream dependency.
- **(b) Allow with rollback path** — install proceeds; if FlexLib 4.2.18 fails on their radio's firmware, JJ Flex offers to roll back to 4.1.x and restore.
- **(c) Force firmware update during install** — install includes a "your radio needs firmware X, we'll install it now" step.

The decision depends on what R5 reveals about the FlexLib 4.2.18 silent-discovery bug. If it's wrapper-fixable (some MMCSS pipeline issue), the firmware dependency relaxes. If it's not, we may need (c).

My recommendation: **wait for R5**. Don's trace is the load-bearing input here. Decision deferred until recovery-week traces come in.



---

## What happens after you answer

- **Q1** → I update this plan to reflect the chosen Phase 1 vs Phase 2 ordering. Next-track decision propagates to TRACK-INSTRUCTIONS files.
- **Q2** → if (a), Phase 1 code track waits on cert. If (b), Phase 1 starts whenever Phase 0 done. If (c), I'll draft self-signed instructions.
- **Q3** → drives the Phase 0 Section F draft I produce when triggered.
- **Q4** → drives WHEN I produce the supporting drafts (nginx config, GitHub Action workflow, receiver code).
- **Q5** → marks `project_firmware_install_dependency_strategy.md` as decided or stays open.

If you want to skip any question, write `**** SKIP` on its line.

---

## Cross-references (the design memos this plan is built on)

**Strategic frame:**
- `memory/project_main_branch_41_posture.md` — main = 4.1 line until 4.2 ships; sets why this work matters
- `memory/project_independent_merge_events.md` — sister memory; foundation drops merge to main as 4.1 trunk

**Crash reporter:**
- `memory/project_sprint29_crash_reporter_vision.md` — full ACK'd design
- `memory/project_sprint29_diagnostics_settings_tab.md` — UX consolidation

**Updater:**
- `memory/project_sprint29_updater_vision.md` — bundle-don't-slice, all 8 phases
- `memory/project_chained_updater_pattern.md` — minClientVersion cascade
- `memory/project_microsoft_trusted_signing.md` — signing infrastructure choice
- `memory/project_code_signing_cert_milestone.md` — funding gate

**Firmware:**
- `memory/project_firmware_distribution_decision.md` — extraction recipe + hosting decision
- `memory/project_firmware_install_dependency_strategy.md` — open question waiting on R5
- `memory/project_flexlib_4218_discovery_investigation.md` — the R5 investigation
- `memory/project_smartsdr_decompile_authorization.md` — fallback if FlexLib firmware API is incomplete

**Server-side foundation:**
- `memory/project_jjflex_data_provider.md` — read-only file host concept
- `memory/project_data_provider_hosting.md` — R2 + custom domain implementation
- `memory/project_jjf_data_repo.md` — `nromey/jjf-data` private repo
- `memory/project_rarbox_hardening.md` — rarbox config snapshot, account model, UFW state

**Workflow rules in scope:**
- `memory/feedback_open_questions_route_to_for_noel.md` — why this doc is here, not in active/
- `memory/feedback_screen_reader_textual_markers.md` — formatting conventions used here
