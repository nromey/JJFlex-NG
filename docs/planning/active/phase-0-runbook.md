# Phase 0 Runbook — Cloudflare R2 + DNS + rarbox

**Status:** ACK'd 2026-05-05 per for-noel queue resolution. Standalone runbook extracted from `2026-05-04-42-release-execution-plan-pull.md` so Noel can step through without scrolling the long plan doc.

**Time estimate:** 2-3 hours total, spread across 1-2 sessions if convenient. Each section is independently checkpointable — stop after any heading and resume later.

**Why this is Phase 0:** every other 4.2.0 release-blocker phase reads from or writes to this. Crash reporter needs the rarbox receiver endpoint. Updater reads the R2 manifest. Firmware updater fetches blobs from R2. Phase 0 unblocks Phases 1-4.

**Per Noel's Q1 answer (2026-05-05):** Phase 2 (crash reporter) ships before Phase 1 (updater). Section F (nginx + receiver on rarbox) is the load-bearing piece — it's the FIRST piece of new infrastructure that 4.2.0 will rely on in production.

**Per Noel's Q4 answer (2026-05-05):** Section E (GitHub Action workflow), Section F (nginx config + receiver code) drafts produced just-in-time. Tell Claude when you're at the matching section and the drafts arrive.

**Per Noel's Q4 second answer:** Noel is interested in the "Claude operates rarbox directly" execution model — Claude SSHes via Tailscale, runs sections of this runbook, Noel verifies. Captured as `project_claude_as_rarbox_operator.md` for future development.

---

## Prerequisites

- 1Password unlocked (you'll need to record API tokens + DNS values)
- Credit card on file with Cloudflare (R2 enable requires this even on free tier — see `project_data_provider_hosting.md` "Setup gotcha")
- Access to your domain registrar for `jjflexible.radio` (Porkbun / NIC.RADIO / wherever you bought it)
- Tailscale up so you can SSH to rarbox

## Section A — Transfer DNS for jjflexible.radio to Cloudflare

Per the steps already documented in `memory/project_data_provider_hosting.md` "DNS for jjflexible.radio — moving to Cloudflare":

1. **Cloudflare dashboard → Add Site → enter `jjflexible.radio`.** Cloudflare assigns two nameservers (e.g. `<word1>.ns.cloudflare.com` / `<word2>.ns.cloudflare.com`). Record both in 1Password.

2. **Eyeball the imported DNS records.** Cloudflare scans your existing DNS and imports what it finds. Verify:
   - Any MX records (if you have email on the domain)
   - Any TXT records (SPF, DKIM, domain verification)
   - Existing A/CNAME records pointing at the marketing site

   If anything's missing, add it manually now BEFORE you flip nameservers — missing records mean dropped service during the propagation window.

3. **At your registrar, replace nameservers** with Cloudflare's two from step 1. Wait 5 min – few hours for propagation; Cloudflare dashboard shows "Active" once the world sees the change.

4. **Verify.** From PowerShell: `Resolve-DnsName jjflexible.radio` should show Cloudflare-served records.

## Section B — Create R2 bucket

1. **Cloudflare dashboard → R2 → Create bucket.** Name: `jjflex-data` (or similar; the bucket name doesn't surface to users — they see the custom domain). Region: Automatic.

2. **Add credit card if prompted.** First-time R2 enable requires a billing instrument. Free tier covers your usage; you won't be charged.

3. **Confirm bucket created.** Should show empty in the R2 dashboard.

## Section C — Custom domain for `data.jjflexible.radio`

1. **In R2 → your bucket → Settings → Custom Domains → Connect Domain.** Enter `data.jjflexible.radio`.

2. **Cloudflare auto-creates the CNAME record.** Verify in DNS tab; should show `data CNAME <bucket-id>.r2.cloudflarestorage.com` (proxied — orange cloud — for free SSL + CDN).

3. **Wait for SSL provisioning** (usually <5 min). Test: `curl -I https://data.jjflexible.radio/` should return a 404 (bucket is empty) but with valid SSL headers.

4. **Upload a test file** via the R2 dashboard ("Upload" button). Upload any small text file as `test.txt`.

5. **Verify access:** `curl https://data.jjflexible.radio/test.txt` should return the file contents. Delete the test file after.

## Section D — R2 API tokens for GitHub Action

1. **Cloudflare dashboard → R2 → Manage API Tokens → Create API Token.** Name: `jjf-data-github-action-sync`.

2. **Permissions:** "Object Read & Write" — scoped to bucket `jjflex-data` only.

3. **Generate token.** You'll see (one time only) the Access Key ID + Secret Access Key. Record BOTH in 1Password under a new entry "Cloudflare R2 — jjf-data sync".

4. **Endpoint URL.** Also record the R2 S3 endpoint (`https://<account-id>.r2.cloudflarestorage.com`) — you'll need this in the GitHub Action.

## Section E — GitHub Action sync in nromey/jjf-data

Claude will draft the workflow file — you just need to commit it after setting secrets.

1. **In `nromey/jjf-data` GitHub settings → Secrets and variables → Actions:**
   - Add secret `R2_ACCESS_KEY_ID` (from Section D)
   - Add secret `R2_SECRET_ACCESS_KEY` (from Section D)
   - Add secret `R2_ENDPOINT` (from Section D, the `https://...r2.cloudflarestorage.com` URL)
   - Add variable (not secret) `R2_BUCKET` = `jjflex-data`

2. **Tell Claude to draft the workflow file** (`.github/workflows/sync-to-r2.yml`). ~30-line YAML using `aws-cli` + `aws s3 sync`. You commit it.

3. **Test:** push a file to `nromey/jjf-data/test/` — Action runs, file appears at `https://data.jjflexible.radio/test/<filename>`.

## Section F — nginx + DNS for crashes.jjflexible.radio on rarbox

**Critical-path per Q1.** This is what Phase 2 (crash reporter, shipping first per Noel) depends on.

1. **Cloudflare DNS → Add A record:** `crashes.jjflexible.radio` → `178.156.204.128` (rarbox's public IP per `project_rarbox_hardening.md`). **Proxied (orange cloud) ON** — gives free SSL + DDoS + origin-IP hiding.

2. **SSH to rarbox** (Tailscale): `ssh ner@rarbox.macaw-jazz.ts.net`.

3. **Install nginx** if not already:
   ```bash
   sudo apt update && sudo apt install -y nginx certbot python3-certbot-nginx
   ```

4. **Tell Claude to draft the nginx config** for the receiver endpoint. ~20-line site config that:
   - Listens on 443 (Cloudflare-proxied, so origin can be plain HTTP technically, but we'll do TLS end-to-end via Cloudflare's Origin Certificate)
   - Routes POST `/crashes` to a simple bundle-storage path
   - Routes POST `/feedback` to the same handler with a different tag
   - Returns 200 on accepted, 4xx on rejected

5. **The actual receiver** — per Q3, **FastAPI** under systemd. Accepts POST, validates content-type + max size, writes the bundle to `/var/lib/jjflex-receiver/<YYYY-MM-DD>/<uuid>.zip`. ~50 LOC. Per Q3 second note: bundle should be designed so Claude can later read + triage + repackage with metadata. See `project_crash_triage_bundle_flow.md` for the design pattern.

6. **Cloudflare Origin Certificate** (preferred over Let's Encrypt for Cloudflare-proxied origins): Cloudflare dashboard → SSL/TLS → Origin Server → Create Certificate. Install on rarbox per nginx config.

7. **Test:** `curl -X POST -F "file=@test.zip" https://crashes.jjflexible.radio/feedback` → should return 200 and the file should appear in `/var/lib/jjflex-receiver/<today>/`.

## Section G — Verify the whole chain

After A-F, you should have:
- `data.jjflexible.radio` serving files from R2 (try `curl https://data.jjflexible.radio/test/anything.txt`)
- `crashes.jjflexible.radio` accepting uploads on rarbox (try `curl -X POST -F "file=@small.zip" .../feedback`)
- GitHub Action triggering on push to `nromey/jjf-data`

**If all three work: Phase 0 is DONE.** Code phases unblock. Per Q1, Phase 2 (crash reporter code) starts first.

## What Phase 0 unlocks

- **Phase 1 (updater):** R2 manifest hosting via `data.jjflexible.radio`. Per Q2, ships unsigned to testers; cert-gates the public 4.2.0 release.
- **Phase 2 (crash reporter — ships first per Q1):** rarbox receiver via `crashes.jjflexible.radio`. FastAPI per Q3.
- **Phase 3 (firmware updater):** R2 manifest + blob hosting. Per Q5, no install-time gate needed because the discovery cascade dissolves the firmware-install dependency.
- **Phase 4 (integration testing + 4.2.0 release):** all of the above tied together for the public cut.

## Cross-references

- `memory/project_data_provider_hosting.md` — R2 + custom domain implementation details
- `memory/project_jjf_data_repo.md` — `nromey/jjf-data` private repo
- `memory/project_rarbox_hardening.md` — rarbox config snapshot, account model, UFW state
- `memory/project_crash_triage_bundle_flow.md` — bundle-flow design pattern (Q3 second note)
- `memory/project_claude_as_rarbox_operator.md` — execution model option (Q4 second note)
- `memory/project_chained_updater_pattern.md` — minClientVersion cascade for firmware
- `memory/project_microsoft_trusted_signing.md` — signing infrastructure choice (Q2 cert)
- `memory/project_firmware_install_dependency_strategy.md` — Q5 marked decided (chain dissolves dependency)
