# Migration plan addendum — 2026-05-02

This addendum captures decisions and findings from the 2026-05-02 review session, layered on top of `migration-plan.md` (the v1 as-built plan from the autonomous exploration agent). v1 stays canonical for the as-built state of Andre's box; this addendum overrides v1 wherever they conflict.

## What changed since v1

Three categories of changes:

1. **Architecture evolved** from "everything on rarbox" (v1's Option A recommendation) to a three-tier model (R2 + rarbox + roarbox).
2. **Drift investigation** verified the actual WSL-vs-Andre state, which is different from v1's framing.
3. **Noel answered all 12 questions**, several of which override v1 defaults.

---

## Architecture (overrides v1's Option A recommendation)

### Three-tier hosting model

- **Cloudflare R2 + CDN, 60-second TTL** — JSON data files (nets.json, solar.json, 12-week schedule). Global edge delivery, near-real-time updates via cache TTL.
- **rarbox (Hetzner VPS, 2 vCPU / 80GB)** — bhn-nets-helper Flask service, IRI/solar warm spare (lower-resolution mode), data processing for net submissions.
- **roarbox (Chris-racked box, public IP this weekend)** — primary IRI/solar compute (40 cores), AllStar nodes (phone-a-solar-report, phone-a-newsline), Patrick's ampersand stuff, propagation research.

### Bidirectional warm-spare with health-check failover

Cloudflare load-balancer pool with health checks on each service-specific endpoint (not just ICMP — service-aware). If roarbox loses power, rarbox warm-spares the AllStar nodes and runs lower-resolution solar. If rarbox loses connectivity, roarbox can carry static data serving for the helper. ~$10-20/month total Cloudflare cost.

### Visible degraded-mode banner

When failover is active, web frontend reads `X-JJF-Backend` header from the active origin and renders a banner: *"Solar reports running on backup server. Calculation resolution reduced; full service expected to resume shortly."* ARIA-live region, persistent until status changes, screen-reader announced.

---

## Drift state (verified 2026-05-02 via direct comparison)

v1 framed the drift question as "publisher's git pull is failing for weeks, helper edits diverge." The actual state is more nuanced:

- **Helper code (`app.py`) is byte-identical** across WSL, Andre live (`/var/www/data/tools/nets-helper/`), and Andre repo clone (`/home/ner/bhn/tools/nets-helper/`). Same MD5, same 82,438 bytes, same 2,232 lines. **No code drift.**
- **Andre's `/home/ner/bhn/` is AHEAD of WSL by 4 commits.** Andre HEAD = `248ac28` matches GitHub origin/main. WSL HEAD = `1fce509`. **WSL is the stale clone, not Andre.** Noel needs to `git fetch && git pull` in WSL.
- **The "broken `git pull --ff-only`" v1 found likely refers to a DIFFERENT mechanism** — not the bhn clone (which IS pulling from GitHub successfully), probably the rsync that syncs `/home/ner/bhn/` → `/var/www/data/`. Worth investigating separately during migration.
- **No Codex-edit drift exists.** All current helper code matches GitHub.

**Implication for migration:** GitHub is canonical. The migration deploys what's in GitHub (which Andre matches) to rarbox. The "merge Andre's drift back to source" step v1 implied is unnecessary for code; it's only needed for the helper-written `_data/nets.json` (Andre is canonical for that current state since the helper writes go to Andre's `/home/ner/bhn/_data/`).

---

## Locked decisions from the 12 questions

### Q1 — Source-of-truth flow (Phase 2)

**LOCKED: Option (a) PLUS Cloudflare R2 enhancement.**

- Helper writes to local `_data/nets.json` AND commits + pushes to GitHub when admin approves a net (`BHN_NETS_AUTO_PUSH=1` in the systemd unit). Requires SSH deploy key on rarbox + git author identity for the helper user.
- Publisher pulls from GitHub on every cron tick.
- **NEW:** instead of serving JSON directly from rarbox, the JSON files publish to Cloudflare R2 with 60-second TTL. R2's CDN serves user-side fetches at low latency globally; rarbox is just the writer.

This eliminates the WSL/rarbox divergence risk that today's broken arrangement carries forward, AND moves user-facing data delivery off rarbox (lower load on rarbox, better global performance).

### Q2 — requirements.txt

**LOCKED: use `/home/ner/bhn/tools/nets-helper/requirements.txt` from WSL/GitHub.**

Contents (verified 2026-05-02):
```
Flask==3.0.2
PyYAML==6.0.1
```

Plus `requirements-dev.txt`:
```
-r requirements.txt
pytest==8.3.3
```

No need to reverse-engineer from Andre's venv. Provision rarbox venv directly from `pip install -r requirements.txt`.

### Q3 — AllStar voice integration

**LOCKED: not blocking this migration.**

Patrick generates the AllStar TTS voice manually from a different source. When automation comes online, it'll run on roarbox 40-cores. Currently we only generate the text (`solar_voice.txt`); actual TTS plumbing is Patrick's surface. v1's cutover-risk concern (TLS pinning, aggressive caching) is moot since the consumer is doing manual workflow, not automated polling.

### Q4 — `andrel` SSH alias

**LOCKED: not relevant for this migration.**

`andrel.andreouis.com` is Andre's main web server — Noel has access if needed but the script (`pull_opt_bhn.sh`) is dormant. Delete during cleanup per v1's Phase 8.

### Q5 — CORS origin on rarbox

**LOCKED: scope to `https://www.blindhams.network` for both HTTP and HTTPS.**

The current Andre config is unintentional cruft (sites-available diverged from sites-enabled). Reset to a deliberate scoped value. Both `www.blindhams.network` and `blindhams.network` are Netlify-based and consume rarbox/roarbox data; scoping the response Origin to www is the cleanest. If we add R2-served data later, R2's CORS config can be set independently.

### Q6 — Notification target

**LOCKED: stay on public ntfy.sh for now.**

Local ntfy was attempted before but iOS push notifications didn't route correctly through self-hosted setup. Future: solve iOS routing problem and migrate to local ntfy on `data.blindhams.network`. Submission data is genuinely public-friendly. Document in plan that this was an explicit choice, not an oversight.

### Q7 — IRI/GIRO research location (Phase 4)

**LOCKED: Option (b) — move to rarbox alongside the publishers.**

Reasoning: warm-spare model. If roarbox loses power, rarbox can run a lower-resolution solar calculation. ~30 min runtime on Andre at current resolution; will retest on roarbox + rarbox manually post-migration. Per-user API for solar calculations is the future direction (rather than static map regeneration).

This **overrides v1's default of Option (c)** ("skip rarbox, target 40-core only"). The agent didn't know about roarbox + warm-spare model.

### Q8 — Andre coordination

**LOCKED: already done.**

Noel told Andre. Andre said leave it on as long as needed. No further coordination required pre-cutover. Andre cares about his Jamulus server; everything else is secondary for him.

### Q9 — `~/bhn/_ChatGPT/` directory

**LOCKED: delete (or keep if useful).**

32 KB, two subdirs of ChatGPT-generated scaffolding. Read for any useful info first, then delete. Doesn't affect migration either way.

### Q10 — `/opt/bhn_untracked_backup/` and `/opt/scripts/`

**LOCKED: archive useful bits to rarbox if any, delete from Andre.**

These were used to test publisher setup; not currently used. GitHub is now source of truth for everything. If we need to use Flask test fixtures later, we can recreate. Andre doesn't care about these files; cleanup is welcomed.

### Q11 — Tmpfile-leak fix (Phase 2)

**LOCKED: Option (b) — rewrite, not surgical fix.**

Collapse `build_bhn_12w.sh` and `build_next_nets.sh` into the Python scripts directly using `os.replace()` for atomic writes. Eliminates the shell layer entirely; tmpfile leaks become structurally impossible. Bigger diff than the `trap` band-aid but cleaner long-term.

This **overrides v1's default of Option (a)** (surgical `trap` fix).

### Q12 — Data shapes

**LOCKED: no other downstream consumers.**

Only Jekyll site's hydration JS + the AllStar solar-voice piece (which is Patrick's manual workflow). Migration preserves both. No PSK Reporter or other ham-radio integration consuming the data feeds.

---

## Scope sharpening (what does NOT migrate)

Don't accidentally rsync gigabytes of dev artifacts across machines. The migration moves what GitHub has, NOT what's in any working tree.

**Excluded from migration:**
- Local venv directories (`~/bhn/tools/nets-helper/.venv/`, `_site/`, `__pycache__/`) — rebuild fresh on rarbox from source + requirements.txt
- `node_modules/` (untracked in WSL)
- Jekyll build output (`_site/`) — Netlify rebuilds from source
- IDE caches (`.vscode/`, `.cursor-server/`)
- Old backup directories on Andre (per Q10)
- Andre's other services (Jamulus, personal site at https://3.onj.me, ports 8000/9000/13884)

**Included in migration:**
- Source code from `nromey/bh-network` GitHub repo (which Andre matches)
- Helper systemd unit (with paths adjusted from Andre's to rarbox's filesystem layout)
- Cron unit definitions (with paths adjusted, and Q11's rewrite folded in)
- Current data state from Andre's `/home/ner/bhn/_data/` (one-time copy at cutover; from then on, helper writes happen on rarbox)
- IRI/solar Python scripts + binaries (per Q7)

---

## Operational issues v1 found that STILL need addressing

The drift investigation didn't change these. They're all real and the migration is the right time to fix them:

- **2,045 leaked tmpfiles in `/var/www/data/`** — fixed structurally by Q11 rewrite, but the existing leak should be cleaned up during decommission.
- **Publisher running as `root`** when it doesn't need to — set up a dedicated user account on rarbox (e.g., `bhn-publisher`) for the cron jobs.
- **`BHN_NETS_AUTO_PUSH=0` (broken push)** — flip to `=1` per Q1, ensure SSH deploy key is in place.
- **Stale `/www/data` directory from October 2025** — left untouched during migration, surfaced in Andre decommission per v1's Phase 8.
- **Public ntfy topic with no token** — explicitly documented as intentional per Q6 (not a security oversight to silently fix).

---

## Revised phase summary

v1's phases stay valid in shape; here are the deltas:

- **Phase 0 (NEW):** Noel runs `git fetch && git pull` in WSL to catch up to GitHub. No migration work depends on stale WSL.
- **Phase 1 (nginx + filesystem prep on rarbox):** unchanged from v1, except CORS origin = `https://www.blindhams.network` per Q5.
- **Phase 2 (helper deployment):** `BHN_NETS_AUTO_PUSH=1`, requirements.txt from WSL/GitHub, Q11 rewrite of shell wrappers → Python with `os.replace()`.
- **Phase 3 (cron migration):** straightforward port from Andre to rarbox; user becomes `bhn-publisher` not root.
- **Phase 4 (IRI/solar):** moves to rarbox per Q7 (overrides v1's defer-to-roarbox).
- **Phase 5 (Cloudflare R2 + CDN setup, NEW):** stand up R2 bucket for JSON data, configure 60s TTL, point web frontend to R2 endpoints. This is largely independent of rarbox setup; can run in parallel.
- **Phase 6 (DNS cutover):** v1's plan stands.
- **Phase 7 (soak window):** v1's plan stands.
- **Phase 8 (Andre decommission):** v1's plan stands. Per Q8, Andre is informed and patient — soak window can be generous.

---

## Followup — items NOT in this migration but worth tracking

- **iOS notification routing for self-hosted ntfy** (Q6): Noel wants to migrate off public ntfy eventually but the iOS-routing-through-self-hosted problem is unsolved. Separate workstream.
- **Per-user solar API** (Q7): the future direction Noel described — instead of static map regeneration, an API that computes solar/propagation per user location on demand. Roarbox is the natural home; design + implementation is later work.
- **Visible degraded-mode banner** for failover (per this morning's discussion): ~10 LOC nginx config for the X-JJF-Backend header, ~30 LOC JS for banner rendering, ~50 LOC CSS. Ships after warm-spare failover is operational.
- **Rsync mechanism `/home/ner/bhn/` → `/var/www/data/` investigation**: v1's "broken git pull" was probably about this, not the bhn clone. Worth a focused look during migration to ensure we don't carry forward the broken assumption.

---

## What's NOT changed by this addendum

v1's risks.md, inventory.md, and categorization.md remain accurate as as-built documentation of Andre's state. The operational issues (leaked tmpfiles, root-running publisher, public ntfy, stale data dir) are still real and addressed in the revised phases above.

The 12 questions doc is now fully answered (inline `**** ` annotations); it can stay as the audit trail of the design decisions or be archived once the migration completes.
