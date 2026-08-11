# Phase 0 Sections B-E — Click-by-Click Walkthrough

**Companion to:** `phase-0-runbook.md` (which covers all of A-G in summary form)
**Scope:** R2 bucket creation, custom domain, API tokens, GitHub Action sync — the `data.jjflexible.radio` half of Phase 0
**Status going in:** Section A (DNS for jjflexible.radio on Cloudflare) is done. Section F+G (rarbox + crashes.jjflexible.radio) is done as of 2026-05-08. This walkthrough finishes B-E.
**Estimated time:** 15-20 minutes of dashboard work

## Pre-flight

- 1Password unlocked (you'll capture API token secrets here)
- Credit card on file with Cloudflare (R2 enable requires this even on free tier — see `project_data_provider_hosting.md` "Setup gotcha")
- Access to your `nromey/jjf-data` GitHub repo (already created, currently empty)
- Local git CLI working

---

## Section B — Create R2 bucket

**Goal:** An empty R2 bucket named `jjflex-data` to receive synced files from `nromey/jjf-data`.

**Click path in Cloudflare dashboard:**

1. Left sidebar → **R2 Object Storage** → **Overview**
   - This is **account-level** navigation, NOT under the `jjflexible.radio` zone. R2 is your account-wide storage; buckets aren't tied to a specific zone.
2. Click **Create bucket**
3. **If prompted for a credit card:** add one. R2 free tier covers this usage; you won't be charged. Required even on free tier per the setup gotcha.
4. Bucket settings:
   - **Name:** `jjflex-data` (this is internal — users see the custom domain, not this bucket name)
   - **Location:** Automatic (Cloudflare picks the closest region)
   - **Default storage class:** Standard
5. Click **Create bucket**

**Verify:** Lands on bucket's empty file-listing page showing **0 objects**.

**Tell orchestrator-Claude:** "B done"

---

## Section C — Custom domain `data.jjflexible.radio`

**Goal:** `https://data.jjflexible.radio/<filename>` serves files from the R2 bucket via Cloudflare's CDN with TLS.

**Click path:**

1. From the bucket's page (still inside R2 Object Storage):
   - Top tabs: **Settings** → scroll to **Custom Domains** section
   - Click **Connect Domain**
2. Enter `data.jjflexible.radio` → **Continue**
3. Cloudflare shows a confirmation modal — verify the CNAME it'll create:
   - Name: `data`
   - Target: `<bucket-id>.r2.cloudflarestorage.com`
   - Proxy: Proxied (orange cloud) — leave it on for free SSL + CDN
4. Click **Connect Domain** to confirm
5. **Wait for SSL provisioning** — usually under 5 min. Status badge will move from "Initializing" → "Active".

**Verify SSL:** From PowerShell on your Windows box:

```powershell
curl.exe -I https://data.jjflexible.radio/
```

Should return `HTTP/2 404` (bucket is empty, that's correct) with a valid SSL handshake. If you see "SSL: no alternative certificate" or similar, give it another 2-3 minutes and retry — Cloudflare's SSL provisioning takes a moment to propagate to all edge nodes.

**Smoke test with a file:**

1. Back in the R2 bucket dashboard → **Objects** tab → **Upload**
2. Upload any small text file — name it `test.txt`, content can be `hello world`
3. From PowerShell:

```powershell
curl.exe https://data.jjflexible.radio/test.txt
```

Should return `hello world`. Then **delete** the test file from the bucket so the prod store starts empty.

**Tell orchestrator-Claude:** "C done"

---

## Section D — R2 API tokens

**Goal:** A scoped API token + endpoint URL for the GitHub Action to sync to. Captured in 1Password before the page closes.

**Click path:**

1. Cloudflare dashboard → **R2 Object Storage** (account-level) → **Manage R2 API Tokens** (top-right of the R2 overview page, sometimes labeled "API")
2. Click **Create API Token**
3. Token settings:
   - **Token name:** `jjf-data-github-action-sync`
   - **Permissions:** **Object Read & Write**
   - **Specify bucket(s):** "Apply to specific buckets only" → select `jjflex-data` (NOT all buckets — least-privilege scope)
   - **TTL:** Forever (or however long you want; can be revoked anytime)
4. Click **Create API Token**

**CRITICAL — capture all three values RIGHT NOW before closing the page:**

The page shows three text boxes. Cloudflare will never show the secret again — close this page without capturing and you'll have to regenerate.

1. **Access Key ID** — copy
2. **Secret Access Key** — copy
3. **Endpoint** — there's an "S3 API endpoint" field elsewhere on the page or in the bucket's settings. Format: `https://<account-id>.r2.cloudflarestorage.com`. Copy this too.

**1Password entry:** Create a new entry titled "Cloudflare R2 — jjf-data sync" with three custom fields:

```
R2_ACCESS_KEY_ID    = <Access Key ID from step 4>
R2_SECRET_ACCESS_KEY = <Secret Access Key from step 4>
R2_ENDPOINT         = https://<account-id>.r2.cloudflarestorage.com
```

(You'll paste these into GitHub Secrets in Section E. 1Password is the long-term home so you can rotate later without spelunking.)

**Tell orchestrator-Claude:** "D done, secrets captured"

---

## Section E — GitHub Action sync workflow

**Goal:** A push to `nromey/jjf-data` automatically syncs the repo to the R2 bucket; the change appears at `https://data.jjflexible.radio/<path>` within ~30 seconds.

### Step 1: Add GitHub secrets + variable

Navigate to the `nromey/jjf-data` repo → **Settings** (top tab) → **Secrets and variables** (left sidebar) → **Actions**.

**Secrets** tab → **New repository secret** for each:
- Name: `R2_ACCESS_KEY_ID` — value: from 1Password
- Name: `R2_SECRET_ACCESS_KEY` — value: from 1Password
- Name: `R2_ENDPOINT` — value: from 1Password (the `https://...r2.cloudflarestorage.com` URL)

**Variables** tab (different from Secrets — there's a tab toggle) → **New repository variable**:
- Name: `R2_BUCKET` — value: `jjflex-data`

(Bucket name as a variable, not secret, because it's not sensitive — it's a label, not a credential. This makes it visible in workflow logs for easier debugging.)

### Step 2: Clone the repo + add the workflow file

In a terminal on your Windows box (the repo is currently empty):

```powershell
cd C:\dev   # or wherever you keep cloned repos
git clone git@github.com:nromey/jjf-data.git
cd jjf-data
mkdir .github\workflows
```

Create `.github/workflows/sync-to-r2.yml` with this content:

```yaml
name: Sync to R2

on:
  push:
    branches: [main]
  workflow_dispatch:

jobs:
  sync:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout repository
        uses: actions/checkout@v4

      - name: Configure AWS CLI for R2
        run: |
          aws configure set aws_access_key_id "${{ secrets.R2_ACCESS_KEY_ID }}"
          aws configure set aws_secret_access_key "${{ secrets.R2_SECRET_ACCESS_KEY }}"
          aws configure set region auto
          aws configure set output json

      - name: Sync repository to R2 bucket
        run: |
          aws s3 sync . "s3://${{ vars.R2_BUCKET }}/" \
            --endpoint-url "${{ secrets.R2_ENDPOINT }}" \
            --delete \
            --exclude ".git/*" \
            --exclude ".github/*" \
            --exclude "README.md" \
            --exclude ".gitignore"
```

### Step 3: First commit + push

Create a tiny test file so the sync has something to do:

```powershell
echo "JJ Flexible Data Provider - test" | Out-File -FilePath test\hello.txt -Encoding utf8
```

Then commit and push:

```powershell
git add .github\workflows\sync-to-r2.yml test\hello.txt
git commit -m "Initial commit: sync workflow + test file"
git push -u origin main
```

### Step 4: Verify the Action ran

1. **GitHub UI:** repo → **Actions** tab → should show the "Sync to R2" workflow run
2. Click into the run — should be green ✓ in under 1 minute
3. **R2 dashboard:** bucket should now show `test/hello.txt` as an object
4. **Public path test from PowerShell:**

```powershell
curl.exe https://data.jjflexible.radio/test/hello.txt
```

Should return `JJ Flexible Data Provider - test`.

If all three (green workflow, object in bucket, public path serves the file) — Phase 0 is **DONE**.

**Tell orchestrator-Claude:** "E done"

---

## Troubleshooting

**Workflow run fails with "InvalidAccessKeyId":** Secrets weren't saved correctly. Re-paste from 1Password. Check for trailing whitespace or accidental newline.

**Workflow run succeeds but no objects in R2:** Check the workflow log for the `aws s3 sync` step. Likely cause: `R2_BUCKET` variable not set or misspelled. Variables (not secrets) are at the same Settings → Secrets and variables → Actions page, but on the "Variables" tab.

**`curl` to `data.jjflexible.radio/test/hello.txt` returns 404:** Two possibilities:
- The Action just started — give it 30s and retry
- The path-prefix exclusion is wrong — verify `aws s3 sync` log shows `upload: ./test/hello.txt`

**`curl` returns SSL error:** SSL provisioning still in flight. Wait 2-3 min, retry.

## What Phase 0 unblocks once E is done

- **Phase 1 (updater):** can publish manifests to `nromey/jjf-data` and have them appear at `data.jjflexible.radio` automatically
- **Phase 3 (firmware updater):** firmware blobs can be hosted via the same path
- **Future plugin / waveform / model packs:** all flow through the same `git push → R2` pipeline

## Cross-references

- `phase-0-runbook.md` — the canonical Phase 0 runbook (this walkthrough is the verbose B-E companion)
- `memory/project_data_provider_hosting.md` — architecture rationale + setup gotchas
- `memory/project_jjf_data_repo.md` — the `nromey/jjf-data` repo identity
