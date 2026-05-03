# Categorization — Blind Hams + Solar surfaces

Applies the brief's taxonomy to the inventory. Each item is grouped
into one bucket; items that genuinely span buckets get a primary
classification with a cross-reference. Items not in `inventory.md`
aren't categorized here — go add them to inventory first.

---

## 1. Static site assets — already on Netlify, OUT OF SCOPE for migration

The Jekyll site itself does **not** need to move. It's hosted at
Netlify (`https://www.blindhams.network`) with Netlify Functions
handling its dynamic edges. The migration only touches what Apache
serves at `data.blindhams.network`.

**Static assets that live on Netlify (read-only context, don't touch):**

- `~/bhn/_site/` — Jekyll build output, deployed by Netlify
- `~/bhn/assets/` — CSS/JS/images
- `~/bhn/_includes/`, `~/bhn/_layouts/`, `~/bhn/_posts/`, `~/bhn/nets/`, `~/bhn/cq-blind-hams/`, `~/bhn/contacts/`
- `~/bhn/index.md`, `~/bhn/news.md`, `~/bhn/about.markdown`, `~/bhn/contact.md`, `~/bhn/changelog.md`, `~/bhn/404.html`
- `~/bhn/_config.yml`, `~/bhn/_config_dev.yml`, `~/bhn/_headers`, `~/bhn/_redirects`, `~/bhn/Gemfile`, `~/bhn/Gemfile.lock`, `~/bhn/.ruby-version`, `~/bhn/netlify.toml`, `~/bhn/package.json`
- `~/bhn/netlify/functions/` (Netlify Functions: `ping.js`,
  `proxy-next-nets.js`, `counter-home.js`, `suggest-net.js`)

**The only thing on Andre's box that's "static" in the
DocumentRoot-served sense:** the JSON feeds — but those are
*outputs of dynamic services* (the publishers), not authored
static assets. They're listed under §3 "Data files" because they're
regenerated continuously.

---

## 2. Dynamic services (Python + cron + systemd) — primary migration target

These are the things that actually have to move. They run on Andre's
box, they're stateful or scheduled, and they produce the JSON feeds
the Jekyll site consumes.

### 2.1 Publisher pipeline (cron-driven, runs as `root`)

Migration target. All of this needs to land somewhere on rarbox.

- `/opt/bhn/bin/deploy_all.sh` — orchestrator (cron `*/5 * * * *`)
- `/opt/bhn/bin/build_next_nets.sh` — wraps `build_next_nets.py`
- `/opt/bhn/bin/build_bhn_12w.sh` — wraps `build_bhn_12w_json.py`
- `/opt/bhn/bin/bhn.env` — env vars consumed by the .sh wrappers
- `/opt/bhn/bin/.env/` — Python venv (3.12)
- `/opt/bhn/scripts/build_next_nets.py` — emits `next_nets.json`
- `/opt/bhn/scripts/build_bhn_12w_json.py` — emits `bhn_nco_12w.json`
- `/opt/bhn/scripts/build_solar_json.py` — emits `solar.json` AND
  `solar_voice.txt` (cron `*/10 * * * *`)
- `/opt/bhn/scripts/voice_sections.yml` — controls solar_voice.txt
  structure
- `/var/spool/cron/crontabs/root` (the cron config itself) — three
  active lines: `*/5 deploy_all.sh`, `*/10 build_solar_json.py`, plus
  the `restic` stanzas which are commented out

**Reads from:** `/home/ner/bhn/_data/` on Andre (the diverged checkout
the helper writes to).
**Writes to:** `/var/www/data/` on Andre.

### 2.2 GIRO comparison cron (runs as `ner`)

- `/home/ner/iri/run_giro_cron.py` — wrapper
- `/home/ner/iri/compare_iri_with_giro.py` — does the comparison
- `/home/ner/iri/build_muf_grid.py` — used for MUF grid generation
- `/home/ner/iri/iri_muf_driver` — compiled Fortran binary
- `/home/ner/iri/iri_driver/` — driver source/build tree
- `/home/ner/iri/stations.toml` — station list
- `/home/ner/iri/mask_utils.py`, `mask_query.py` — mask helpers
- `~/.crontab` user entry: `*/30 * * * * .../run_giro_cron.py`

**Reads from:** GIRO/DIDBase upstream (`lgdc.uml.edu`).
**Writes to:** `~/iri/data/giro_iri_comparison_*.csv`,
`~/iri/giro_cron.log`.

This is *research / proto-tooling*, not user-facing yet. Its outputs
feed the future per-grid solar website. Migration question: does this
need to ride along on rarbox now, or stay on Andre as research and
move later when the solar website actually launches?

### 2.3 Nets-helper Flask service (systemd-managed)

- `/etc/systemd/system/bhn-nets-helper.service` — service unit
- `/etc/systemd/system/bhn-nets-helper.socket` — socket unit
- `/var/www/data/tools/nets-helper/app.py` (84 KB)
- `/var/www/data/tools/nets-helper/.venv/`
- `/var/www/data/tools/nets-helper/templates/` (3.5 MB)
- `/var/www/data/tools/nets-helper/help_texts.json`
- `/var/www/data/tools/nets-helper/roles.yml`
- `/var/www/data/tools/nets-helper/requirements.txt`
- `/var/www/data/tools/nets-helper/tests/`
- `/var/www/data/tools/nets-helper.backup_20251031_204722/`
  (previous-version backup; can be archived during migration)

**Reads/writes:** `/home/ner/bhn/_data/nets.json` and
`/home/ner/bhn/_data/pending/*.json` on Andre.
**Notifies:** `https://ntfy.sh/bh-nets-helper` (public topic, no token).

### 2.4 Apache reverse-proxy + auth glue for nets-helper

- `/etc/apache2/conf-enabled/nets-helper.conf` — the
  `ProxyPass unix:/run/bhn-nets-helper.sock|http://localhost/` rule
  + HTTP basic auth gate
- `/etc/apache2/.htpasswd_bhn_nets` — credentials file (port to rarbox
  carefully; treat as a secret)

---

## 3. Data files — content the publishers read or write

### 3.1 Authoring sources (read by publishers, written by helper or by Noel)

These are the inputs to the publisher pipeline.

- `/home/ner/bhn/_data/nets.json` (Andre) — current authoring source
  (helper-modified)
- `/home/ner/bhn/_data/ncos.yml` (Andre) — NCO meta
- `/home/ner/bhn/_data/bhn_ncos_schedule.yml` (Andre) — 12-week NCO
  rotation source
- `/home/ner/bhn/_data/next_net.json` (Andre) — repo-side weekly
  cache
- `/home/ner/bhn/_data/nets.backup.20260103_153952.json`,
  `nets.backup.20251117_042119.json` (Andre) — auto-backups by helper
- `/home/ner/bhn/_data/pending/` (Andre) — staged drafts awaiting
  approval
- `/home/ner/bhn/_data/...` on WSL — Noel's laptop authoring copy
  (currently DIVERGED from Andre's because publisher git-pull is
  broken — see risks.md)

### 3.2 Published outputs (served by Apache to the public)

- `/var/www/data/next_nets.json` (12 KB, refreshed every 5 min)
- `/var/www/data/bhn_nco_12w.json` (5.5 KB, refreshed every 5 min)
- `/var/www/data/solar.json` (6.7 KB, refreshed every 10 min)
- `/var/www/data/solar_voice.txt` (~2.4 KB, refreshed every 10 min)
- `/var/www/data/health.json` (40 B, refreshed every 5 min)

### 3.3 Research / cache data (research-only, not user-facing)

- `~/iri/data/giro_iri_comparison_YYYYMMDD.csv` (148 MB, 27 daily
  files spanning 2025-10-17 → 2025-11-12)
- `~/iri/solar_muf.json` (3.5 MB)
- `~/iri/demo.json` (2.4 MB)
- `~/iri/demo_muf_grid.json` (105 KB)
- `~/iri/land_mask.json` (3.4 MB)
- `~/iri/giro_cron.log` (4.3 MB) — also a "noise" candidate (rotate
  on migration)

### 3.4 Reference data (inputs to IRI compute, rarely changes)

- `~/iri/*.dat`, `~/iri/*.asc`, `~/iri/*.for` (WSL: 7.1 MB; Andre's
  copy may differ but should match upstream IRI)
- These are upstream IRI / NOAA / IGRF reference files. If the IRI
  compute moves, the reference data must move with it. If the IRI
  compute stays on Andre, this stays.

---

## 4. Config — Apache, certs, DNS, OS-level

### 4.1 Apache vhost config

- `/etc/apache2/sites-enabled/data.blindhams.network.conf`
- `/etc/apache2/sites-enabled/data.blindhams.network-le-ssl.conf`
- `/etc/apache2/sites-available/data.blindhams.network.conf`
  (currently diverged from `sites-enabled/`, see risks.md)
- `/etc/apache2/sites-available/data.blindhams.network-le-ssl.conf`
  (currently diverged from `sites-enabled/`)
- `/etc/apache2/sites-available/data.blindhams.network.conf.bak`
  (manual backup file — cleanup candidate)
- `/etc/apache2/sites-available/000-default-le-ssl.conf.bak`
- `/etc/apache2/conf-enabled/nets-helper.conf` (proxy + auth)
- `/etc/apache2/conf-available/nets-helper.conf` (the source of the
  enabled symlink)

### 4.2 TLS certs

- `/etc/letsencrypt/live/data.blindhams.network/{fullchain,privkey}.pem`
- `certbot.timer` (systemd) for auto-renewal

### 4.3 DNS

- `data.blindhams.network` → A/AAAA record currently pointing at
  `3.onj.me` (Andre's box). Hosted at Noel's DNS provider — not
  enumerated (out of scope; Noel manages this).
- `www.blindhams.network` → Netlify (NOT changing).

### 4.4 OS-level cron (the active config)

- `/var/spool/cron/crontabs/root` — three publisher entries (one
  active, two for solar)
- `/var/spool/cron/crontabs/ner` — GIRO entry

### 4.5 Auth secrets

- `/etc/apache2/.htpasswd_bhn_nets` — basic auth for `/nets-helper/`
- (No ElevenLabs/AllStar API keys found on Andre — flagged in
  questions-for-noel.md)

---

## 5. Noise — accumulated cruft, candidates for cleanup

### 5.1 On Andre's server

- `/var/www/data/.bhn_nco_12w.json.<pid>` × 2,036 — tmpfile leak
- `/var/www/data/.next_nets.json.<pid>` × ~9 — tmpfile leak
- `/www/data/` (the older directory at `/www`, not `/var/www`) —
  stale solar.json + solar_voice.txt from 2025-10-15. Apache does
  not serve from here. Pre-migration scaffolding.
- `/opt/bhn_untracked_backup/bhn_opt.tar.gz` — a 6.4 KB tarball from
  2025-10-09. Plus a file literally named `ner@3.onj.me` (looks like
  an accidental shell redirect).
- `/opt/scripts/` — older copies of `build_bhn_12w_json.py`,
  `build_next_nets.py`, `build_solar_json.py` (dated 2025-10-14),
  superseded by `/opt/bhn/scripts/`.
- `/opt/bhn/bin/build_solar_json.py` — duplicate of
  `/opt/bhn/scripts/build_solar_json.py` but older. Either the venv
  needed it locally or it's leftover scaffolding.
- `/opt/bhn/bin/data/` — looks like a dev-scratch directory with
  its own solar.json + solar_voice.txt
- `/opt/bhn/scripts/build_bhn_12w_json.py.20251010_225702` — versioned
  backup file
- `/var/www/data/tools/nets-helper.backup_20251031_204722/` — previous
  helper version backup
- `~/nets-helper/` — older standalone copy of the helper (the active
  one is at `/var/www/data/tools/nets-helper/`)
- `~/bhn_debug/` — debug scratch dir from 2025-11-05
- `~/iri_driver/`, `~/iri_muf_driver`, `~/iri_muf_driver_bundle_*.tar.gz`,
  `~/iri_muf_grid_package_*.tar.gz` — older bundles/snapshots from
  driver setup
- `~/jsonscripts/`, `~/jsonscripts/bhn.tar.gz` — early scaffolding
- `~/iri/giro_cron.log` (4.3 MB) — log file, candidate for rotation
- `/var/log/bhn_next_nets.log` — full of "Host key verification
  failed" repeats; rotate after fixing the root cause
- `~/sco.txt` (1.3 MB) — Codex log (not bhn-related); leave alone
- `/var/log/solar-json.log` — occasional warnings, generally fine

### 5.2 On WSL

- `~/bhn/node_modules/` (315 MB) — gitignored, can rebuild
- `~/bhn/vendor/bundle/` (69 MB) — gitignored, can rebuild
- `~/bhn/_site/` (4.0 MB) — gitignored, Jekyll regenerates
- `~/bhn/.jekyll-cache/` — gitignored
- `~/bhn/.pytest_cache/` — gitignored
- `~/bhn/_ChatGPT/` (32 KB) — looks like ChatGPT-generated
  scaffolding files. Worth confirming with Noel whether to keep.
- `~/bhn/solar_agents.md:Zone.Identifier` (25 B) — Windows ADS
  artifact from copy/paste through Windows. Cleanup candidate.
- `~/bhn/backups/` (8 KB) — destination for `pull_opt_bhn.sh`;
  currently ~empty because the script's `andrel` SSH alias doesn't
  resolve in this WSL session
- `~/bhn/.venv/` — local Python venv
- `~/jsonscripts/` (64 KB) — early publisher scaffolding,
  superseded
- `~/myp/` (38 MB) — generic Python venv
- `~/scripts/` (Codex helpers, not bhn)
- Top-level home video files (`Adrenne...`, `adren-duncan-graveside.mp4`)
  totaling ~3.2 GB — personal/family, not bhn

---

## 6. Uncategorized — flagged for Noel to clarify

These don't fit cleanly into any bucket and need a decision before the
migration plan can address them.

- **TTS / ElevenLabs / AllStar wiring.** No trace of TTS or
  ElevenLabs anywhere on Andre's box or in `~/bhn/`. The "ElevenLabs
  voice that AllStar consumes" must live on a third surface (Noel's
  ham infra? a different VPS? a home server?). This must be located
  before the migration plan can promise non-disruptive cutover for
  the audio side. — see questions-for-noel.md Q3.
- **The `andrel` SSH alias** referenced by
  `~/bhn/scripts/pull_opt_bhn.sh`. No `~/.ssh/config` exists in WSL,
  so the alias resolves to nothing here. Must be a config from a
  different machine (Noel's other dev box? An older WSL session?).
  — questions-for-noel.md Q4.
- **Andre's other listening sockets** (`8000`, `9000`, `13884`) on
  `206.189.112.230`. Not bhn-related per service name; Andre's
  business. Out of scope; do not touch.
- **The `~/bhn/_ChatGPT/` directory** (WSL only). Looks like
  ChatGPT-generated scaffolding files. Is it actively useful?
  Probably noise but worth a one-line confirmation. —
  questions-for-noel.md Q9.
- **`/opt/bhn_untracked_backup/`** — looks like a manual backup taken
  during initial publisher setup, root-owned. Keep? Delete? Just
  archive? — questions-for-noel.md Q10.

---

End of categorization.
