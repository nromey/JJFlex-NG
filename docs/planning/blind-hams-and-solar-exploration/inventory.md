# Inventory — Blind Hams + Solar surfaces

**Captured:** 2026-05-02 by autonomous WSL exploration agent.
**Scope:** read-only inventory across two surfaces (WSL Ubuntu on Noel's
laptop and Andre's server `3.onj.me`) of everything that looks
blind-hams- or solar-related.

Everything below is observation, not recommendation. Categorization +
migration plan + risks live in their own files.

---

## Surface 1 — WSL Ubuntu (`nertop`, user `ner`, home `/home/ner`)

### Top-level directories that matter for this migration

| Path | Size | Last touched | What it is |
|---|---|---|---|
| `~/bhn/` | 489 MB | 2025-12-11 | Live clone of `git@github.com:nromey/bh-network` (branch `main`, in sync with `origin/main`; only untracked files are `node_modules/`, `package.json`, `package-lock.json`). This is the primary working copy of the Jekyll site + admin tooling. |
| `~/iri/` | 7.1 MB | 2025-10-15 | IRI Fortran source tree + reference data files. Pure source; no build artifacts. Stale as of 2025-10-15 — no recent activity. |
| `~/jsonscripts/` | 64 KB | 2025-10-08 | Early scaffolding of the JSON publisher pipeline. Contains `bhn/` subdir and `bhn.tar.gz` (5.4 KB). Looks like a snapshot from before the publisher was promoted to `/opt/bhn/` on Andre. Probably superseded but never cleaned up. |
| `~/myp/` | 38 MB | 2025-11-03 | Generic Python venv (`pyvenv.cfg`, `bin/`, `lib/`). Not bhn-specific by name. |
| `~/scripts/` | small | 2025-10-01 | OpenAI/Codex helper shells (`codex_sessions.sh`, `oai_LoginMethod`). Not bhn-related. |
| `~/images/` | 17 MB | 2025-10-10 | K5NER ham-callsign brand art (logo in .ai / .eps / .pdf / .png / .svg). Personal, not bhn-site. |
| `~/flexlib/`, `~/JJFlex-NG/` | — | — | JJ Flexible Radio dev work. Out of scope. |
| `~/sox-14.4.2/`, `~/sox-build/` | — | 2026-01 | Local SoX source build (audio toolkit). Out of scope unless wanted by solar audio pipeline. |

### `~/bhn/` — the Jekyll site repo

Origin: `git@github.com:nromey/bh-network.git` (Noel's fork). Branch
`main` is in sync with `origin/main`. Last commit `1fce509 — Added a
queue for published nets.` Recent commit cadence: dense — 30+ commits
since 2025-09 covering nets-helper, suggest form, queue publication,
News/CQBH backfill.

| Path | Notes |
|---|---|
| `_config.yml` | Site config. URL `https://www.blindhams.network`. Centralised `data_endpoints` block points all live data to `https://data.blindhams.network/{next_nets.json,bhn_nco_12w.json}` with same-origin `/data/...` proxy fallbacks. `data_fetch.use_proxy = false`. |
| `netlify.toml` | Netlify build config: `bundle exec jekyll build` → `_site/`. Has `[functions] directory = "netlify/functions"` (esbuild bundler). Per-context env switches for `dev` / `deploy-preview`. Plugin: `@netlify/plugin-functions-install-core`. |
| `_data/nets.json` | Authoring source for nets list (~12 KB). Top-level `time_zone: America/New_York` + `nets[]` array. Fields: `id`, `category`, `name`, `description`, `start_local`, `duration_min`, `rrule`, `location` (markdown), `mode`, `dmr_system`, `dmr_tg`, etc. |
| `_data/ncos.yml` | NCO rotation metadata (time, tz, duration, location). |
| `_data/bhn_ncos_schedule.yml` | 12-week NCO rotation source (date + nco + notes per row). |
| `_data/next_net.json` | 26 KB; generated locally by `scripts/build_next_net.py` for in-repo use (NOT what's served at data.blindhams.network — that's `next_nets.json` in `/var/www/data` on Andre). |
| `_posts/` | 173 markdown posts (CQ Blind Hams + site news, 2020–2025). |
| `assets/css/`, `assets/js/` | Site assets. JS includes `json-widgets.js`, `time-view.js`, `net-view.js`, `home-week-filter.js`, `able-init.js`, `pager.js`, `suggest-net.js`. |
| `_includes/` | Jekyll includes; `home_next_nets.html`, `next_net_card.html`, `nets_page.html` hydrate from live data endpoints. |
| `tools/nets-helper/` | Flask app source for the admin nets-helper (mirrors what's deployed at `/var/www/data/tools/nets-helper/` on Andre — see Surface 2). |
| `tools/remote/build_bhn_12w_json.py` | Local copy of the publisher script that builds `bhn_nco_12w.json` from `_data/`. The deployed copy lives at `/opt/bhn/scripts/build_bhn_12w_json.py` on Andre. |
| `scripts/build_next_net.py` | Builds `_data/next_net.json` for in-repo Jekyll consumption. Different from the publisher's `build_next_nets.py` (note plural). |
| `scripts/build_bhn_data.py`, `scripts/build_next_net.py`, `scripts/convert_nets_yaml_to_json.py`, `scripts/fetch_cqbh.py` | Repo-side data utilities. |
| `scripts/pull_opt_bhn.sh` | Shell script that SSHes to `ner@andrel` and `sudo tar` streams `/opt/bhn` back to `~/bhn/backups/`. The `andrel` SSH alias does NOT exist in `~/.ssh/config` (file is missing entirely). The script must currently be broken or rely on shell aliasing elsewhere. |
| `netlify/functions/` | Four Node.js Netlify Functions: `ping.js` (sanity), `proxy-next-nets.js` (CORS bypass on dev branches), `counter-home.js` (Netlify-Blobs-backed visit counter), `suggest-net.js` (proxies submissions from the public site to `https://data.blindhams.network/nets-helper/api/public/suggest`, optionally adding HTTP basic auth from `BHN_SUGGEST_AUTH_USER` / `BHN_SUGGEST_AUTH_PASS` env vars). |
| `solar/` | Mostly empty — only `__pycache__/` and `scripts/__pycache__/`. The actual solar work happens in `~/iri/` and on Andre's box. This dir is a stub. |
| `docs/` | Site docs: `data-server-setup.md`, `live-data-hydration.md`, `nets-data.md`, `blobs-setup.md`, `git-sync-stash-rebase.md`, `ableplayer-usage.md`, `ripgrep-recipes.md`, `admin/`. |
| `AGENTS.md`, `CHANGELOG.md`, `TODO.md` | Active project docs. AGENTS.md is the working brief for contributors; CHANGELOG.md is dense and recent; TODO.md lists outstanding work (suggest-form polish, search highlighting, batch-publish workflow, etc.). |
| `Gemfile`, `Gemfile.lock`, `vendor/bundle/` (69 MB) | Ruby gems for local Jekyll. |
| `node_modules/` (315 MB) | Node deps (Netlify Functions, etc.). Gitignored. |
| `_site/` (4.0 MB) | Jekyll build output. Gitignored. |

### `~/iri/` — IRI/MUF reference data on WSL

Pure source/data, last touched 2025-10-15. NO compiled binaries, NO
Python venvs, NO automation. This is the *upstream* IRI tree that's
been built from on Andre. Files include:

- `irisub.for`, `irifun.for`, `iriflip.for`, `iridreg.for`, `iritest.for`, `iritec.for`, `irirtam.for`, `irirtam-test.for`, `cira.for`, `igrf.for`, `rocdrift.for` — IRI Fortran sources
- `apf107.dat` (1.4 MB) — daily F10.7 / sunspot historical data (updated 2025-10-15 — likely manually refreshed)
- `ig_rz.dat` (10 KB) — ionospheric global / solar zenith parameters
- `ccir11.asc`–`ccir22.asc`, `ursi11.asc`–`ursi22.asc` — coefficient maps (one per year of cycle)
- `dgrf*.dat`, `igrf2025.dat`, `igrf2025s.dat` — geomagnetic field reference data
- `mcsat11.dat`–`mcsat22.dat` — Mass Spectrometer / Incoherent Scatter coefficients
- `00readme.txt` (6.5 KB) — IRI provenance notes

Mostly inert; the active solar work happens on Andre's box (which has a
larger 170 MB `~/iri/` with daily comparison CSVs, see Surface 2).

### `~/.ssh/`

- `id_ed25519` (private key)
- `id_ed25519.pub` (public key)
- `known_hosts`, `known_hosts.old`
- **No `~/.ssh/config`** — meaning the `andrel` host alias used by
  `~/bhn/scripts/pull_opt_bhn.sh` resolves... nowhere from this WSL
  session. SSH from this session reaches `3.onj.me` directly with
  agent-forwarded keys (1Password agent works through WSL).

---

## Surface 2 — Andre's server `ner@3.onj.me`

OS: Ubuntu (kernel `6.8.0-111-lowlatency`), hostname `Onj3`. Apache
2.x. Reachable from WSL via SSH (1Password agent forwarded).

The server hosts **other things** besides Blind Hams (notably
`/home/andrel/public_html/` is Andre's personal site at
`https://3.onj.me`, with its own letsencrypt cert). This inventory only
covers paths that touch Blind Hams or solar. Andre-only paths are
acknowledged but not enumerated.

### Web stack

| Item | Detail |
|---|---|
| Web server | Apache 2 (single binary serving multiple vhosts) |
| Listening on | `*:22` (SSH), `*:80`, `*:443`. Also `0.0.0.0:8000` and `:9000` bound to public IP `206.189.112.230` (unidentified — possibly Andre's other services), plus `:13884` IPv4+IPv6 (unidentified). |
| Apache modules enabled | Standard set + `headers`, `proxy`, `proxy_http`, `rewrite`, `ssl`, `php7.4` (Andre's stack). `proxy_unix` is implicit via `proxy_http` for socket-style ProxyPass. |
| TLS certs | `/etc/letsencrypt/live/data.blindhams.network/fullchain.pem` + privkey; `/etc/letsencrypt/live/3.onj.me/...` for Andre's vhost. Auto-renew via `certbot.timer`. |
| Vhosts enabled | `000-default.conf` + `000-default-le-ssl.conf` (Andre's `3.onj.me` site → `/home/andrel/public_html/`); `data.blindhams.network.conf` + `data.blindhams.network-le-ssl.conf` (DocumentRoot `/var/www/data`). |
| Vhost: `data.blindhams.network` | DocumentRoot `/var/www/data`. CORS: HTTP listener uses `Access-Control-Allow-Origin: *`; HTTPS listener uses `Access-Control-Allow-Origin: https://www.blindhams.network` (note: there are duplicate copies in `sites-enabled/` and `sites-available/`; the `sites-enabled/` versions are the active ones, the `-available/` ones are diverged copies — flagged for cleanup). Sets `Cache-Control: public, max-age=60, s-maxage=300`, `application/json` content-type for `*.json`, `FileETag MTime Size`. |
| Conf-enabled extra | `/etc/apache2/conf-enabled/nets-helper.conf` — wires HTTP basic auth (`AuthUserFile /etc/apache2/.htpasswd_bhn_nets`) at URL prefix `/nets-helper/`, then `ProxyPass` to `unix:/run/bhn-nets-helper.sock\|http://localhost/`. |

### `/var/www/data` — the live JSON service

Owner: `ner:www-data`, mode `drwxr-xr-x` (some files inside owned by `root`).

**Live files (the actual public data feeds):**

| Path | Size | Last modified | Description |
|---|---|---|---|
| `/var/www/data/next_nets.json` | 12,194 B | every 5 min | Combined "next net + 7-day window" feed. Hydrated by site JS. |
| `/var/www/data/bhn_nco_12w.json` | 5,470 B | every 5 min | 12-week NCO rotation. |
| `/var/www/data/solar.json` | 6,692 B | every 10 min | NOAA SWPC indices snapshot (F10.7, Kp, Boulder K, solar wind, x-ray, sunspot, flare probabilities, WWV, 3-day forecast). Includes a `sources` array listing every NOAA endpoint it pulled from. |
| `/var/www/data/health.json` | 40 B | every 5 min | `{"ok":true,"ts":"..."}` — overwritten by `deploy_all.sh`. |
| `/var/www/data/solar_voice.txt` | ~2.4 KB | every 10 min | **Plain text**, NOT audio. The TTS-script source for the AllStar voice report. Generated by `build_solar_json.py` alongside `solar.json`. |

**Tmpfile leak (operational issue, not data):**

`/var/www/data` currently contains **2,045 stale dot-prefixed temp
files** (~63 MB on disk; only ~8.6 KB of bytes — most are zero-length
because the publisher's `python3 ... > $TMP` died mid-pipeline before
writing).

- 2,036 files matching `.bhn_nco_12w.json.<pid>`
- ~9 files matching `.next_nets.json.<pid>`
- Date range: 2025-10-08 → 2025-11-13 (then stops — suggests the
  publisher started exiting before reaching the cleanup line, or a
  later code change addressed the leak; need Noel to confirm).
- Owner mostly `root:root` (matches the publisher running as root via
  cron); a few are `ner:ner` (suggests a manual run in Noel's name at
  some point).
- Source: `build_bhn_12w.sh` and `build_next_nets.sh` use a
  `mktemp`-style pattern `TMP=$DATA_DIR/.bhn_nco_12w.json.$$` then
  `python3 ... > $TMP; jq -S . $TMP > $TMP.pretty; mv ...; rm -f
  $TMP`. With `set -euo pipefail`, any failure in `python3` or `jq`
  aborts the script before the `rm -f`, leaving the tmpfile.

**Subdirs:**

| Path | Notes |
|---|---|
| `/var/www/data/tools/nets-helper/` | The deployed Flask app (gunicorn-served via systemd unit, see below). 84 KB `app.py`, 3.5 MB `templates/`, `.venv/`, `roles.yml`, `help_texts.json`, `tests/`. Owner `www-data:www-data`. Last edit 2026-03-28 (`app.py` is the freshest). |
| `/var/www/data/tools/nets-helper.backup_20251031_204722/` | Previous version preserved when the helper was upgraded on 2025-10-31. Same shape, smaller. |

### `/www/data` — STALE legacy directory

| Path | Size | Last modified | Description |
|---|---|---|---|
| `/www/data/solar.json` | 6.8 KB | **2025-10-15** | Old, never refreshed. Apache does not serve from here. |
| `/www/data/solar_voice.txt` | 2.4 KB | **2025-10-15** | Same vintage. |

This directory is NOT in any current Apache vhost. It looks like a
pre-migration scaffolding location from before DocumentRoot was set to
`/var/www/data`. Safe to leave untouched in this read-only audit, but
should be archived or removed during cleanup so future inventories
don't have to disambiguate it.

### `/opt/bhn` — the publisher pipeline

Owner: `root:root` (everything except `/opt/bhn/scripts/build_next_nets.py`
and `/opt/bhn/scripts/build_solar_json.py` and
`/opt/bhn/scripts/voice_sections.yml` which are `ner:ner`).

```
/opt/bhn/
├── bin/
│   ├── .env/                       Python venv (3.12)
│   ├── bhn.env                     1.2 KB — env vars consumed by shell scripts
│   ├── build_bhn_12w.sh            361 B  — wraps build_bhn_12w_json.py
│   ├── build_next_nets.sh          354 B  — wraps build_next_nets.py
│   ├── build_solar_json.py         65 KB  — older copy (Oct 15); current is in /opt/bhn/scripts/
│   ├── data/                       contains its own solar.json + solar_voice.txt (looks like dev scratch)
│   └── deploy_all.sh               434 B  — orchestrator: optional git pull, then run both .sh wrappers, then write health.json
└── scripts/
    ├── build_bhn_12w_json.py       4.0 KB  — emits the 12-week NCO JSON
    ├── build_bhn_12w_json.py.20251010_225702  — backup of the previous version
    ├── build_next_nets.py          14 KB   — emits the 7-day "next nets" feed
    ├── build_solar_json.py         81 KB   — fetches NOAA SWPC + emits solar.json + solar_voice.txt
    └── voice_sections.yml          326 B   — YAML controlling solar_voice.txt structure
```

**`/opt/bhn/bin/bhn.env`** (the publisher's env file):

```
BHN_REPO_ROOT="/home/ner/bhn"          # local checkout it reads from
BHN_LOCAL_TZ="America/New_York"
BHN_WINDOW_DAYS="7"
BHN_WEEKS_AHEAD="12"
BHN_AUTOPULL="1"                       # tries to git pull before publishing
BHN_DATA_DIR="/var/www/data"
BHN_LOG_DIR="/var/log"
BHN_LOG_NEXT_NETS="/var/log/bhn_next_nets.log"
BHN_LOG_NCO_12W="/var/log/bhn_nco_12w.log"
```

**`/opt/bhn/bin/deploy_all.sh`** (run every 5 min as root):
1. Source `/opt/bhn/bin/bhn.env`
2. If `BHN_AUTOPULL=1`, `git -C $BHN_REPO_ROOT pull --ff-only \|\| true`
3. Run `build_next_nets.sh` then `build_bhn_12w.sh`
4. Write `health.json`

`build_solar_json.py` is invoked separately (not from `deploy_all.sh`)
every 10 min via its own root cron entry.

### `/opt/bhn_untracked_backup/` (root-owned)

Has `bhn_opt.tar.gz` (6.4 KB, 2025-10-09), a `build_next_nets.py`
backup (13 KB), and a file literally named `ner@3.onj.me` (an unusual
name — looks like accidental `> ner@3.onj.me` redirect). Cleanup
candidate.

### `/opt/scripts/` (separate from `/opt/bhn/scripts/`)

Has older versions of `build_bhn_12w_json.py`, `build_next_nets.py`,
`build_solar_json.py` (last 2025-10-14). Owner `root:ner`. Looks like
the pre-`/opt/bhn/` layout; superseded but not removed.

### `/opt/digitalocean/`

DigitalOcean droplet agent (`do-agent`, `droplet-agent`). Andre's
infra, not bhn. Out of scope.

### `~/bhn` (on Andre, separate from WSL `~/bhn`)

A Jekyll repo checkout owned by `ner`, last `git pull` 2026-01-03 (the
`.git/` dir was last touched then). This is the checkout the publisher
reads from (`BHN_REPO_ROOT=/home/ner/bhn`). Same upstream as WSL
(`nromey/bh-network`). **The bhn-nets-helper service writes
`_data/nets.json` and `_data/pending/*.json` here when admins approve
or stage submissions.**

| `~/bhn/_data/` on Andre | |
|---|---|
| `nets.json` | 12 KB; current authoring source (owner `www-data:ner` — written by helper). |
| `nets.backup.20260103_153952.json` | Most recent nets backup from helper. |
| `nets.backup.20251117_042119.json` | Older nets backup. |
| `next_net.json` | 26 KB; matches the WSL copy. |
| `bhn_ncos_schedule.yml`, `ncos.yml` | NCO source data. |
| `pending/` | Where the helper drops draft net submissions awaiting review. |

The on-Andre checkout is **diverged** from `origin/main` because the
helper writes to it locally and the publisher's `git pull --ff-only`
fails (see "publisher git-pull failure" below). Diff between WSL
checkout and Andre checkout = pending net edits that haven't yet flowed
back to GitHub.

### `~/iri` on Andre — the active solar/MUF compute environment

170 MB total, much busier than the WSL copy.

| Path | Size | Last touched | Description |
|---|---|---|---|
| `~/iri/iri_muf_driver` (binary) | 1.6 MB | 2025-10-16 | Compiled Fortran driver — produces MUF (maximum usable frequency) data. |
| `~/iri/iri_driver/` | 7.2 MB | 2025-10-16 | Build/dev tree for the driver. |
| `~/iri/build_muf_grid.py` | 12 KB | 2025-10-17 | Builds the bisected-earth MUF grid. |
| `~/iri/compare_iri_with_giro.py` | 22 KB | 2025-10-17 | Validates IRI predictions against GIRO (DIDBase) live ionosonde observations. |
| `~/iri/run_giro_cron.py` | 5.1 KB | 2025-10-17 | Wrapper invoked by `ner` cron every 30 min. Picks ~7 default URSI stations + reads `stations.toml`. |
| `~/iri/stations.toml` | 3.8 KB | 2025-10-17 | Station list / config for the GIRO comparison. |
| `~/iri/solar_muf.json` | 3.5 MB | 2025-10-17 | Most-recent MUF grid output. |
| `~/iri/demo.json` | 2.4 MB | 2025-10-16 | Demo/example MUF dataset. |
| `~/iri/demo_muf_grid.json` | 105 KB | 2025-10-16 | Demo grid. |
| `~/iri/land_mask.json` | 3.4 MB | 2025-10-17 | Bisected-earth land/water mask used to skip oceanic grid cells. |
| `~/iri/mask_utils.py`, `mask_query.py` | <10 KB | 2025-10-17 | Mask helpers. |
| `~/iri/data/` | 148 MB | 2025-11-12 (last CSV) | Daily GIRO-vs-IRI comparison CSVs (`giro_iri_comparison_YYYYMMDD.csv`). 27 files, ~700 KB–890 KB each, spans 2025-10-17 → 2025-11-12. |
| `~/iri/giro_cron.log` | 4.3 MB | 2026-05-02 | Cron log. **Currently full of `HTTP Error 503: Service Temporarily Unavailable` from `lgdc.uml.edu` (DIDBase).** Need to confirm whether the upstream is rate-limiting or genuinely down. |
| `~/iri/NEXT_STEPS.txt` | 953 B | 2025-10-17 | Noel's notes-to-self. |

**No public web exposure** — these are research artifacts feeding
future work. The migration brief mentions a future "solar website that
gives location-specific propagation data based on bisected-earth
grid"; this dir is the prototype for that.

### `~/iri_driver/`, `~/iri_muf_driver`, `~/iri_muf_driver_bundle_*.tar.gz`, `~/iri_muf_grid_package_*.tar.gz` (Andre, top-level home)

| Path | Size | Description |
|---|---|---|
| `~/iri_driver/` | 7.2 MB | Older copy of the driver source/build. |
| `~/iri_muf_driver` | 1.5 MB | Older driver binary at home. Likely superseded by the `~/iri/iri_muf_driver` copy. |
| `~/iri_muf_driver_bundle_20251015_213640.tar.gz` | 2.1 MB | Snapshot of driver bundle. |
| `~/iri_muf_grid_package_20251016.tar.gz` | 3.0 MB | Snapshot of MUF grid package. |

These are scaffolding/snapshot artifacts from when the IRI environment
was being set up. Probably safe to archive.

### `~/nets-helper/` on Andre (NOT served from here — see `/var/www/data/tools/nets-helper/`)

| Path | Notes |
|---|---|
| `app.py` (10 KB) | OLDER copy of the helper. The systemd service runs from `/var/www/data/tools/nets-helper/app.py` (84 KB, much newer). |
| `templates/`, `README.md`, `.venv/` | Same shape; older/scaffold version. |

This is a stale copy. Worth confirming with Noel, but the active
service uses the `/var/www/data/tools/nets-helper/` deployment.

### `~/data-server-setup.md` on Andre

A 4.6 KB design doc describing the data feeds (canonical and legacy
JSON shapes for `next_nets.json` + `bhn_nco_12w.json`), CORS / cache
headers, NGINX example config, Apache `.htaccess` example, and a
reverse-proxy-from-`data.blindhams.network` example. Mirrors
`~/bhn/docs/data-server-setup.md` in the WSL repo. Reads as a hosting
blueprint that the migration plan can hew to.

### Cron jobs (live producers)

**`ner` crontab on Andre** (`crontab -l`):

```
*/30 * * * * /usr/bin/python3 /home/ner/iri/run_giro_cron.py \
    --lookback-minutes 180 --output-dir /home/ner/iri/data \
    --quiet >>/home/ner/iri/giro_cron.log 2>&1
```

Every 30 min: GIRO-vs-IRI comparison, appends a daily CSV in
`~/iri/data/`. Currently failing with HTTP 503 from DIDBase (see log
tail above) but this is upstream weather, not a config bug.

**`root` crontab on Andre** (`/var/spool/cron/crontabs/root`):

```
*/5  * * * * /opt/bhn/bin/deploy_all.sh \
    >> /var/log/bhn_next_nets.log 2>&1
*/10 * * * * /usr/bin/python3 /opt/bhn/scripts/build_solar_json.py \
    -o /var/www/data/solar.json \
    >> /var/log/solar-json.log 2>&1
```

Every 5 min: rebuild + publish `next_nets.json` + `bhn_nco_12w.json` +
`health.json`. Every 10 min: rebuild + publish `solar.json` (and
`solar_voice.txt` as a side effect of the same script).

(There are also commented-out `restic` backup lines, evidence Noel had
restic backups planned but they're not currently active. Out of scope.)

**Confirmed via `/var/log/syslog` CRON entries:** every cron tick
firing as expected, every 5 min for `deploy_all.sh`, every 10 min for
`build_solar_json.py`, every 30 min for `run_giro_cron.py`.

### Systemd services (live)

**`bhn-nets-helper.service`** + **`bhn-nets-helper.socket`**

```
[Service]
Type=simple
WorkingDirectory=/var/www/data/tools/nets-helper
Environment="BHN_NETS_FILE=/home/ner/bhn/_data/nets.json"
Environment="BHN_NETS_OUTPUT_DIR=/home/ner/bhn/_data"
Environment="BHN_NETS_AUTO_PUSH=0"
Environment="BHN_NTFY_ENDPOINT=https://ntfy.sh"
Environment="BHN_NTFY_TOPIC=bh-nets-helper"
# Environment="BHN_NTFY_TOKEN="              (commented out)
ExecStart=.../bin/python -m gunicorn --workers 2
    --bind unix:/run/bhn-nets-helper.sock app:create_app()
User=www-data
```

Active for 23h+ at inventory time; main PID 860; 44 MB memory; 2
worker procs. Socket unit binds `/run/bhn-nets-helper.sock` with
`mode=0660`, owner `www-data:www-data`. Apache reaches it via the
`nets-helper.conf` proxy at URL prefix `/nets-helper/`, gated by HTTP
basic auth (htpasswd at `/etc/apache2/.htpasswd_bhn_nets`).

The helper:
- Reads/writes `nets.json` in `~/bhn/_data/`
- Drops `pending/*.json` for review
- Notifies via `ntfy.sh` topic `bh-nets-helper` on new public
  submissions (token currently commented out — public ntfy.sh topic;
  worth flagging to Noel as that means anyone subscribing to that
  topic name can read the notifications)
- Public submission API at `/nets-helper/api/public/suggest` is what
  Netlify's `suggest-net.js` posts to (with optional HTTP Basic from
  Netlify env vars)

### Apache vhost behavior — concrete URL map

| URL | Backed by |
|---|---|
| `https://data.blindhams.network/next_nets.json` | static `/var/www/data/next_nets.json` |
| `https://data.blindhams.network/bhn_nco_12w.json` | static `/var/www/data/bhn_nco_12w.json` |
| `https://data.blindhams.network/solar.json` | static `/var/www/data/solar.json` |
| `https://data.blindhams.network/solar_voice.txt` | static `/var/www/data/solar_voice.txt` |
| `https://data.blindhams.network/health.json` | static `/var/www/data/health.json` |
| `https://data.blindhams.network/nets-helper/...` | HTTP basic auth → ProxyPass to `unix:/run/bhn-nets-helper.sock` (Flask app) |
| `https://data.blindhams.network/nets-helper/api/public/suggest` | same proxy, public-no-auth endpoint inside the Flask app (used by Netlify suggest-net.js) |
| `https://3.onj.me/...` | unrelated — Andre's personal site at `/home/andrel/public_html/` |

### Publisher git-pull failure (operational)

`/var/log/bhn_next_nets.log` is full of:

```
Host key verification failed.
fatal: Could not read from remote repository.
Please make sure you have the correct access rights and the repository exists.
```

This means `deploy_all.sh`'s `git -C $BHN_REPO_ROOT pull --ff-only`
step (Step 2) cannot reach GitHub. The `\|\| true` mask in the script
prevents this from breaking the cron, so JSON publishing continues
working — but the on-Andre checkout has been frozen at whatever commit
existed when SSH last worked. Probably:

- root cron has no SSH agent, no `~/.ssh/known_hosts` for github.com
  in root's home, or
- the root user's `~/.ssh/known_hosts` lost its github.com entry, or
- a host key rotation broke trust and was never re-pinned

This is a real bug to fix as part of the migration (don't carry
forward a publisher that pretends to pull but doesn't).

### Secrets / API keys / tokens

- No `.env` files containing API keys were found in
  `/home/ner/` on Andre (only the publisher config `bhn.env`, which
  has no secrets).
- No `~/jsonscripts/bhn/.env` on Andre with secrets either (just env
  vars same as `bhn.env`).
- The bhn-nets-helper service has `BHN_NTFY_TOKEN=` commented out
  (currently uses public ntfy.sh, no auth).
- HTTP basic auth credentials for `/nets-helper/` live in
  `/etc/apache2/.htpasswd_bhn_nets` (read-only inventory; not
  inspected — but file path is documented and migration must port
  it).
- **No ElevenLabs API key was found anywhere on Andre's box.** The
  `solar_voice.txt` file is plain text — Andre's box does not
  generate audio. Whatever TTS system reads it on-air must be
  downstream (likely on Noel's home/ham infrastructure or the AllStar
  node directly). **This is a question for Noel.**
- No AllStar / IRLP node config on Andre's box.

### Crashes / errors / warnings noticed

- The publisher git-pull error (above) — every 5 min for ~weeks.
- DIDBase HTTP 503 errors in `~/iri/giro_cron.log` (recent — current
  weather, not config).
- `/var/log/solar-json.log` shows occasional NOAA xray endpoint
  returning malformed JSON (`invalid JSON: Expecting ',' delimiter
  ...`) — cron logs `errors=1` but recovers next tick.
- 2,045 leftover tmpfiles in `/var/www/data/` (above).
- Diverged Apache vhost config (`sites-enabled/data.blindhams.network.conf` vs
  `sites-available/data.blindhams.network.conf` differ on
  `Access-Control-Allow-Origin: *` vs scoped) — minor.

---

## Cross-surface summary — what's authoritative where

| Asset | Authoritative source | Where it's also copied |
|---|---|---|
| Jekyll site source | GitHub `nromey/bh-network` (master) | WSL `~/bhn/`, Andre `~/bhn/` (the latter is diverged) |
| `_data/nets.json` (authoring) | Whichever was edited last — currently Andre's (helper-driven). WSL's may be stale. | Both. Helper writes Andre's. Noel may edit WSL's. **Synchronization is currently broken** because the publisher's `git pull` fails. |
| Built JSON feeds | `/var/www/data/*.json` on Andre | Nowhere else |
| Publisher scripts | `/opt/bhn/scripts/` + `/opt/bhn/bin/` on Andre | Stale older copies in `/opt/scripts/` and `/opt/bhn/bin/build_solar_json.py` (different versions). |
| Helper Flask app | `/var/www/data/tools/nets-helper/` on Andre | WSL `~/bhn/tools/nets-helper/`, `~/nets-helper/` on Andre (older). |
| IRI / MUF compute | `~/iri/` on Andre | WSL `~/iri/` is reference data only — no compiled binary, no recent compute. |
| Solar voice script | `/var/www/data/solar_voice.txt` on Andre | Stale `/www/data/solar_voice.txt` on Andre. NOT ELSEWHERE — and the downstream consumer (TTS + AllStar) is unaccounted for. |

---

End of inventory.
