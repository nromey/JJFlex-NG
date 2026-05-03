# Migration plan — Blind Hams + Solar from `3.onj.me` → rarbox

**Status:** proposal, awaiting Noel review.
**Scope:** the data-feed and admin-tooling stack at
`data.blindhams.network` (currently on Andre's box). The Jekyll site
itself stays on Netlify and is not part of this migration.
**Constraint:** read-only on Andre's server until the migration plan
is approved AND a follow-up "execute" session is scheduled. This
document plans only.

---

## 0. Reframe of the migration scope (vs. the README)

The original brief described migrating "the Jekyll site at
`data.blindhams.network`" along with the dynamic pieces. After the
inventory, the actual picture is simpler and tighter:

| Surface | What it really hosts | Migration action |
|---|---|---|
| Netlify | The Jekyll site at `www.blindhams.network` (build from `_site/` + Netlify Functions) | **No change.** Stays on Netlify. |
| Andre's `3.onj.me` | Apache vhost at `data.blindhams.network` serving 4 JSON files + 1 Flask admin tool, fed by 3 cron-driven publishers (next-nets, 12-week-NCO, solar) and 1 systemd service (nets-helper) | **Migrate to rarbox.** |
| Noel's WSL | Authoring copy of the bh-network repo + IRI reference data | **No migration.** Continues to push to GitHub. |

That's the whole moving-parts list. The migration is "stand up an
nginx + Python + systemd stack on rarbox that produces the same five
URLs at `data.blindhams.network`."

---

## 1. Architectural decision needed before phasing — pick one

The launch context flagged this and the inventory confirms it's a real
fork in the road. The decision MUST be made before Phase 1 because the
phasing differs.

### Option A — Everything on rarbox (single nginx + Python stack)

**Layout on rarbox:**
- `nginx` listens on 80/443 with a `data.blindhams.network` server
  block.
- nginx serves `/var/www/data/*.json` directly as static files.
- nginx reverse-proxies `/nets-helper/` to the gunicorn unix socket
  for the Flask app.
- Cron + systemd publish the JSON the same way as today.

**Pros:**
- Lowest cognitive load — one box, one config style. Mirrors today's
  topology exactly, just with nginx instead of Apache.
- Shortest migration plan and lowest cutover risk.
- The publisher writes locally (no R2 upload step in the cron path),
  which keeps the 5-min cadence cheap and predictable.
- No CDN cache invalidation logic to think about (Apache's
  `Cache-Control: max-age=60` is enough; nginx mirrors that).

**Cons:**
- Doesn't follow the JJ Flexible Data Provider precedent (which
  splits R2-static + rarbox-dynamic, per memory).
- Rarbox absorbs all bandwidth for the static feeds (low — these are
  KB-scale files, polled infrequently — but still nonzero egress
  cost on Hetzner).
- If the JSON ever needs to be cached at edge later, you'd retrofit.

### Option B — R2-static + rarbox-dynamic (mirrors `data.jjflexible.radio`)

**Layout:**
- Cloudflare R2 bucket holds `next_nets.json`, `bhn_nco_12w.json`,
  `solar.json`, `solar_voice.txt`, `health.json`. Cloudflare CDN +
  custom domain (`data.blindhams.network`) cover egress.
- nginx on rarbox **only** serves `/nets-helper/` (proxied to
  gunicorn) — but that means a *second* hostname (e.g.
  `helper.blindhams.network`) has to be created and the Netlify
  `suggest-net.js` function reconfigured to point at it, OR the
  Cloudflare Worker has to route `/nets-helper/*` to rarbox while
  serving everything else from R2.
- Publishers run on rarbox (cron + systemd), but their final step
  becomes "upload the freshly-built JSON to R2" instead of writing
  to a local DocumentRoot.

**Pros:**
- Matches the JJ Flexible Data Provider architecture (consistent
  ops mental model across all JJ-Flex surfaces, even though
  Blind Hams is *not* a JJ Flexible site — see brand rule).
- Static feeds get CDN caching for free + zero egress cost on R2.
- Rarbox only does the parts that genuinely need a server.

**Cons:**
- Adds a Cloudflare R2 + Worker piece to the dependency tree of a
  service that doesn't really need it (the JSON files are 6 KB
  each and refresh every 5–10 minutes; CDN caching saves
  rounding-error bandwidth).
- Routing `/nets-helper/` from R2-fronted host through to rarbox
  needs a Worker (or a second hostname). Worker = more code to
  maintain.
- Cutover is more complex: DNS still points at one place, but that
  place has to know how to split static-from-helper requests.
- The publisher's "publish step" goes from `mv` (atomic, local) to
  "PUT to R2" (network call, can fail, retry semantics).

### Recommendation

**Option A** is the right starting point. Rationale:

- The Blind Hams data feeds are five files totaling ~27 KB,
  polled at ~`max-age=60` cadence. CDN caching is solving a
  problem you don't have.
- The brand rule explicitly says Blind Hams is NOT a JJ Flexible
  product — it doesn't have to mirror the JJ Flexible Data Provider
  topology. It can pick the simpler thing.
- Option A's migration plan is shorter, has less rollback surface,
  and gets the dynamic pieces stable on rarbox first. If R2-static
  ever becomes desirable later, you can split it out cleanly
  (because the publisher would already be writing to a defined
  output dir on rarbox — adding "and also push to R2" is one cron
  line away).

**The phased plan below assumes Option A.** If Noel picks B, the
phasing changes — see appendix at end of this doc.

---

## 2. Pre-flight checklist (must be true before Phase 1)

Tasks here are read-only or already-done; included so the plan is
self-contained.

- [x] SSH from WSL to `ner@3.onj.me` works (confirmed
      2026-05-02 by exploration agent).
- [ ] SSH from WSL to `ner@rarbox` works. Per memory, 1Password agent
      forwarding is configured — verify in this session before
      starting Phase 1. No actions needed if already working.
- [ ] Noel has decided Option A vs B (see §1).
- [ ] Noel has answered the "where is the TTS/AllStar piece" question
      (questions-for-noel.md Q3) so the plan can either ignore it or
      account for it during cutover.
- [ ] Noel has notified Andre that a migration is being planned and
      gotten his rough OK on the timeline. (Brief said "coordinate
      with Andre"; this exploration didn't initiate that conversation
      — it's read-only by mandate.)
- [ ] Noel has confirmed Andre's box is **not** running anything
      Blind-Hams-related the inventory missed. (Inventory was
      thorough, but Andre may have his own scripts that touch
      `/var/www/data` we didn't find.)

---

## 3. Phased migration plan (Option A — everything on rarbox)

Phase numbering matches the brief's "stand up rarbox copy → verify →
DNS cutover → soak window → coordinate decommission" flow. Each phase
states what moves, what depends on it, how to verify, how to roll back,
and what's running on Andre during that phase.

### Phase 1 — Stand up the static-file foundation on rarbox

**What moves:** the nginx vhost for `data.blindhams.network` (without
DNS yet) and an empty `/var/www/data/` directory ready to receive
JSON.

**Steps:**
1. On rarbox, install nginx (already in baseline).
2. Create `/var/www/data/` (`drwxr-xr-x`, owner `ner:www-data` — match
   the Andre permissions).
3. Drop in an nginx server block for `data.blindhams.network`:
   - listens on 80 + 443
   - DocumentRoot equivalent: `root /var/www/data;`
   - Headers per `~/data-server-setup.md` blueprint:
     `Cache-Control: public, max-age=60, s-maxage=300`,
     `Access-Control-Allow-Origin: https://www.blindhams.network`
     (or `*` for the HTTP listener — match Andre's split, or
     consolidate to scoped per Noel's preference; see
     questions-for-noel.md Q5),
     `application/json` content-type for `*.json`,
     `Access-Control-Allow-Methods: GET, HEAD, OPTIONS`,
     `Access-Control-Allow-Headers: Content-Type`.
   - `try_files $uri =404;` per the blueprint.
   - Use the JJ Flexible drop-in config pattern (per rarbox memory).
4. Provision a Let's Encrypt cert for `data.blindhams.network` using
   certbot's nginx plugin or a DNS-01 challenge. **Important:** until
   DNS is cut over, an HTTP-01 challenge cannot succeed against the
   public name pointing at rarbox. Two options:
   - DNS-01 (TXT record at the DNS provider) — works without DNS
     change.
   - Provision the cert later (Phase 5, after DNS cutover) and run
     rarbox HTTPS-only on a self-signed temporary cert during
     pre-cutover testing. Verify against rarbox's IP using `curl
     --resolve` rather than against the public name.
5. Hardening: ensure UFW rules allow 80/443 (per rarbox memory, they
   already do).

**Depends on:** rarbox base config (already done, 2026-04-30).

**Verify:**
- From WSL, `curl --resolve data.blindhams.network:443:<rarbox-ip>
  https://data.blindhams.network/` returns nginx's directory-listing
  block or 404 (no JSON yet — expected).
- TLS handshake succeeds. CORS headers present.

**Rollback:** Don't enable the server block, or comment it out. No
public impact (no DNS pointed at rarbox yet).

**Andre during Phase 1:** unchanged. Still serving production.

### Phase 2 — Port the publisher pipeline to rarbox

**What moves:**
- `/opt/bhn/bin/{deploy_all,build_next_nets,build_bhn_12w}.sh`
- `/opt/bhn/scripts/{build_next_nets,build_bhn_12w_json,
  build_solar_json}.py`
- `/opt/bhn/scripts/voice_sections.yml`
- `/opt/bhn/bin/.env/` venv (or rebuild from
  `requirements.txt` — preferable to copying a pickled venv across
  Python minor versions; check rarbox python3 vs Andre's python3.12).
- A new `~/bhn` checkout on rarbox (clone of `nromey/bh-network`) for
  the publisher to read from.

**Architectural changes during port (don't carry forward as-is):**

1. **Fix the tmpfile leak** in `build_next_nets.sh` and
   `build_bhn_12w.sh`. Replace the bare `TMP=$DATA_DIR/.<name>.$$`
   pattern with a `trap 'rm -f "$TMP" "$TMP.pretty"' EXIT` so failures
   don't strand tmpfiles. Or use `mktemp -p "$DATA_DIR" .<name>.XXXX`
   plus the trap. Either way, the cron should never leave debris.
2. **Fix the broken git pull.** Either: (a) decide AUTOPULL=0 is the
   right model and remove the `git pull` from `deploy_all.sh`, with
   the source-of-truth flow being "Noel pushes to GitHub → CI deploys
   the publisher repo to rarbox"; or (b) keep AUTOPULL=1 and fix root's
   `~/.ssh/known_hosts` for github.com plus deploy a github deploy key.
   Option (a) is cleaner; it eliminates a layer of failure that's
   masked by `\|\| true`. **Recommend (a).**
3. **Sort out who owns `_data/nets.json`.** Today the publisher reads
   from `/home/ner/bhn/_data/nets.json`, which the helper writes to
   directly. With AUTOPULL=0, the helper-edited file becomes the
   authoritative version on rarbox. To get changes back to GitHub,
   the helper should commit-and-push when an admin approves a net
   (this is where `BHN_NETS_AUTO_PUSH` env var, currently `0`, would
   flip to `1`). See questions-for-noel.md Q1.
4. **Change publisher user.** Don't run cron as root. Run as `ner`
   (with write permission to `/var/www/data/` set via group membership
   or ACLs). Reduces blast radius if a bug ever ends up writing
   somewhere it shouldn't.
5. **Drop `/opt/bhn/bin/.env/` (the venv) from the migration if it
   pre-dates 2025-10-31** — rebuild fresh from `requirements.txt`
   instead. (No requirements.txt has been seen for this venv in the
   inventory — flagged in questions-for-noel.md Q2 if there isn't
   one, the migration should establish one.)

**Steps:**
1. On rarbox, create the publisher tree at `/opt/bhn/` mirroring
   Andre's, with the fixes above applied.
2. Clone `git@github.com:nromey/bh-network` to `/home/ner/bhn` on
   rarbox.
3. Build a Python venv at `/opt/bhn/bin/.env` from a pinned
   `requirements.txt`. Capture the requirements based on what's
   actually imported by the three publisher scripts (PyYAML, urllib —
   most of `build_solar_json.py` is dependency-free per its docstring;
   the others import yaml). Confirm by `grep import` on the scripts.
4. Install the cron entries under `ner` (not root):
   ```
   */5  * * * * /opt/bhn/bin/deploy_all.sh \
       >> /var/log/bhn_publish.log 2>&1
   */10 * * * * /usr/bin/python3 /opt/bhn/scripts/build_solar_json.py \
       -o /var/www/data/solar.json \
       >> /var/log/bhn_solar.log 2>&1
   ```
5. Set `BHN_AUTOPULL=0` in `bhn.env`. The repo will be updated by a
   separate trigger (manual `git pull` for now; CI later).
6. Set `BHN_NETS_AUTO_PUSH=1` in the helper unit (Phase 3).
7. Configure logrotate for `/var/log/bhn_*.log`.

**Depends on:** Phase 1 (target dir exists).

**Verify:**
- After 1 cron tick, `/var/www/data/{next_nets,bhn_nco_12w,solar,
  solar_voice,health}.json` exist on rarbox.
- `curl --resolve data.blindhams.network:443:<rarbox-ip>
  https://data.blindhams.network/next_nets.json` returns valid JSON.
- `jq . /var/www/data/next_nets.json` parses cleanly.
- No tmpfiles accumulate over a 30-min observation window.
- Compare the rarbox output to Andre's output for the same minute:
  the JSON should be byte-equivalent (modulo timestamps in the
  payload). Use `diff <(jq -S . on-rarbox.json) <(jq -S . on-andre.json)`.

**Rollback:** disable the cron entries. Phase 1 nginx still serves
nothing (or stale snapshots if you took some). No public impact.

**Andre during Phase 2:** unchanged. Still serving production.

### Phase 3 — Port the bhn-nets-helper Flask service to rarbox

**What moves:**
- `/etc/systemd/system/bhn-nets-helper.service`
- `/etc/systemd/system/bhn-nets-helper.socket`
- `/var/www/data/tools/nets-helper/` (the deployed app)
- `/etc/apache2/.htpasswd_bhn_nets` → translate to nginx auth_basic
  (`/etc/nginx/.htpasswd_bhn_nets` or wherever the rarbox convention
  lands)
- `/etc/apache2/conf-enabled/nets-helper.conf` → rewritten as an
  nginx `location /nets-helper/ { ... }` block

**Architectural changes:**

1. **Drop the older `/home/ner/nets-helper/` copy** — the active
   service runs from `/var/www/data/tools/nets-helper/`; the home-dir
   copy is stale.
2. **Set `BHN_NETS_AUTO_PUSH=1`** in the new unit so admin approvals
   flow back to GitHub automatically. This requires:
   - A SSH deploy key on rarbox configured to push to
     `nromey/bh-network`.
   - Git author identity set on rarbox for the `www-data` user (e.g.
     `git config --global user.email "bhn-helper@blindhams.network"`).
   - An understanding that helper-driven commits will appear in repo
     history under that identity. See questions-for-noel.md Q1.
3. **Replace the public ntfy.sh topic** with either an authenticated
   ntfy.sh setup (set `BHN_NTFY_TOKEN`) or a different notification
   target. Public ntfy topic = anyone subscribing to
   `bh-nets-helper` can read net suggestions, including draft fields
   submitters might consider semi-private. See questions-for-noel.md
   Q6.

**Steps:**
1. On rarbox, install dependencies (gunicorn + venv per
   `requirements.txt`).
2. Stage `/var/www/data/tools/nets-helper/` from a fresh `git clone`
   of the helper code (it's mirrored at `~/bhn/tools/nets-helper/` in
   the WSL repo, so the canonical source is `nromey/bh-network`).
3. Translate the systemd unit; update `WorkingDirectory`,
   `EnvironmentFile=`, `User=` (keep `www-data` for parity).
4. Translate the Apache `nets-helper.conf` to nginx:
   ```nginx
   location /nets-helper/ {
       auth_basic "Blind Hams Nets Helper";
       auth_basic_user_file /etc/nginx/.htpasswd_bhn_nets;
       proxy_set_header X-Forwarded-Proto https;
       proxy_set_header X-Forwarded-User $remote_user;
       proxy_pass http://unix:/run/bhn-nets-helper.sock:/;
   }
   ```
5. Copy the htpasswd file from Andre to rarbox (this is one of the
   few "secret" files — handle with `scp` directly, do not commit).
6. Enable + start the socket and service units.
7. Re-test the suggest endpoint — `curl
   https://data.blindhams.network/nets-helper/api/public/suggest -d
   '{...}'` returns the same response as Andre's (use
   `--resolve` to direct curl at rarbox).

**Depends on:** Phase 1 (vhost), Phase 2 (the helper writes to
`~/bhn/_data/` which the publisher reads from).

**Verify:**
- Browser to `https://data.blindhams.network/nets-helper/` (with
  `--resolve` via curl, or by manipulating `/etc/hosts`) — basic auth
  prompt, then the helper UI.
- Submit a test net through the public suggest API; confirm it lands
  in `~/bhn/_data/pending/` on rarbox.
- After approval, confirm the next publisher tick reflects the
  change in `next_nets.json`.

**Rollback:** stop the systemd units. Static JSON keeps publishing.
The `/nets-helper/` URL returns 502 — but only matters if you've cut
DNS over.

**Andre during Phase 3:** unchanged. Andre is still the production
helper.

### Phase 4 — Decide what to do with IRI/GIRO research compute

**What moves (or doesn't):** `~/iri/` on Andre — the IRI Fortran
binary, the GIRO comparison cron, the daily CSV outputs, the MUF grid
prototype.

**Three options for Phase 4:**

A. **Stay on Andre.** It's research, not user-facing. The brief said
   the per-grid solar website hasn't been built yet ("plans to develop
   a propagation algorithm describing current band-by-band
   propagation by location — not built yet, design phase"). Migrating
   research compute that has no public surface is premature.

B. **Move to rarbox now.** Same cron entry, same Fortran binary
   (verify it runs on rarbox's libc — `iri_muf_driver` is a stripped
   ELF; should be portable across modern x86_64 Debian/Ubuntu, but
   verify with a smoke run before relying on it). Pro: consolidates
   everything on rarbox so Andre can be fully decommissioned. Con: 148
   MB of historical CSVs come along; the GIRO upstream's current 503s
   make smoke-testing hard.

C. **Move to Noel's 40-core build host when it comes online.** The
   brief mentions tightening the grid resolution when that's ready —
   that's where the compute belongs long-term. Andre's box and
   rarbox are both wrong target for a 40-core compute job. So:
   keep on Andre as research-state for now, and plan to move it
   straight to the 40-core host later (skip rarbox entirely for the
   compute side).

**Recommendation:** **Option C.** The GIRO compute is research that
will outgrow rarbox before it ever becomes user-facing. Putting it on
rarbox just to take it off again is wasted work. Confirm with Noel.
**See questions-for-noel.md Q7.**

If Noel wants the GIRO research moved as part of *this* migration
anyway:
- Smoke-test `iri_muf_driver` on rarbox first (`./iri_muf_driver
  --help` or equivalent — needs to verify it runs without segfaulting
  on rarbox's libc).
- Move the cron entry under `ner`'s crontab on rarbox.
- Move `~/iri/data/` (148 MB CSVs) — `rsync` works.

**Andre during Phase 4:** unchanged. The GIRO cron either keeps
running there or stops there once rarbox takes over (Noel's call).

### Phase 5 — Verify on rarbox before DNS cutover

Soak window where rarbox is fully operational but DNS still points at
Andre. Both stacks running in parallel, producing the same JSON.

**Verification matrix:**

- [ ] `curl --resolve` against rarbox returns identical JSON content
      to Andre (allowing for timestamps that differ by minutes).
- [ ] CORS headers on rarbox match the Apache headers on Andre exactly
      (verify via `curl -I -H "Origin: https://www.blindhams.network"
      ...`).
- [ ] Cache-Control header matches.
- [ ] Content-Type is `application/json; charset=utf-8`.
- [ ] TLS cert issued, chain validates against public CAs.
- [ ] Helper UI loads, basic auth gate works, suggest API works.
- [ ] Publisher cron runs cleanly for 24h with no tmpfile leaks.
- [ ] Helper writes back to `_data/nets.json` and the publisher picks
      up the change within 5 min.
- [ ] (If `BHN_NETS_AUTO_PUSH=1`) helper-driven commits land in
      `nromey/bh-network` cleanly.
- [ ] Netlify's `suggest-net.js` function still works against rarbox
      via `--resolve` testing (i.e., Netlify env var
      `BHN_SUGGEST_TARGET` set to `https://<rarbox-host>/nets-helper/api/public/suggest`
      temporarily, validated, then reverted before public cutover).

**Soak duration:** at least 7 days. The 12-week NCO rollover happens
weekly; one soak period that crosses a Saturday catches the rollover
case. Solar data updates every 10 min; an issue would surface in
hours, not days, so 7 days is conservative.

### Phase 6 — DNS cutover

**What changes:** the `data.blindhams.network` A/AAAA records flip
from Andre's IP (3.onj.me) to rarbox's IP (178.156.204.128).

**Pre-cutover:**
- Lower TTL on the existing record to 300 s (5 min) at least 24 h
  before cutover, so propagation finishes faster.
- Notify Andre that DNS will move on date X (he hosts other things
  on the same box; he should know our traffic is leaving).
- Notify the broader Blind Hams community that there *might* be a
  brief blip in real-time JSON updates during cutover (in practice,
  it'll just be a slightly stale `next_nets.json` for ~5–10 min as
  caches update).
- Confirm AllStar's solar-voice consumer (whatever it is) won't
  break during the cutover. **Key blocker** —
  questions-for-noel.md Q3.

**During cutover:**
1. Update DNS A/AAAA at the DNS provider.
2. Watch resolution propagate (`dig @1.1.1.1 data.blindhams.network`,
   `dig @8.8.8.8 ...`, `dig @9.9.9.9 ...`).
3. Watch nginx access logs on rarbox for incoming traffic.
4. Watch Andre's apache access log for falling traffic.
5. Check the live site at `https://www.blindhams.network/?diag=1`
   for "Live data loaded …" lines.
6. Once propagation is mostly complete (~15 min for cached resolvers),
   check that helper writes still flow correctly and the publisher
   cron is still ticking.

**Post-cutover:**
- Restore TTL to a reasonable normal value (3600 s).
- Update Netlify env var `BHN_SUGGEST_TARGET` to the public
  `https://data.blindhams.network/nets-helper/api/public/suggest` URL
  (it was already that; no change needed if hostname unchanged).
- Confirm Netlify deploy preview / dev branches still work (they use
  the same proxy URL).

**Rollback:**
- Revert the DNS change. Andre is still up. Traffic returns to him
  within a TTL window. Helper data on rarbox is "ahead" of Andre's
  helper data — would need a backfill, but acceptable since Andre's
  is a snapshot of what was there before cutover.
- Investigate, fix on rarbox, retry cutover.

**Andre during Phase 6:** Still running, still publishing — but
nobody is fetching from him after the DNS change propagates.

### Phase 7 — Soak window with Andre running idle

After cutover, leave Andre's stack running but unused for 14 days. If
something breaks on rarbox, DNS revert returns to a working state.

**During this phase:**
- Monitor rarbox publisher logs for anything weird.
- Monitor `bhn-nets-helper.service` for stability (memory growth,
  worker restarts).
- Confirm helper-driven commits to GitHub continue to flow.
- Confirm Netlify suggest function works.

**Andre during Phase 7:** Still publishing JSON to `/var/www/data/`
on Andre's box, but nobody is fetching it. The helper still accepts
new submissions on Andre's box too, technically — the
`https://data.blindhams.network/nets-helper/` URL now goes to
rarbox via DNS, so direct submissions to Andre's URL would have to
use IP-based access. Effectively, Andre's helper is dormant. This is
fine: the soak is about being able to revert quickly, not about
running both simultaneously.

### Phase 8 — Coordinate decommission with Andre

After the 14-day soak with no rollback needed:

1. Notify Andre the migration is complete and his side can be
   decommissioned.
2. Andre's preference governs the decommission steps. Possibilities:
   - He stops the cron entries, the systemd units, removes the
     vhost — leaves the box otherwise untouched.
   - He removes everything in `/opt/bhn/`, `/var/www/data/`,
     `/etc/apache2/sites-{available,enabled}/data.blindhams.network*`,
     `/etc/apache2/conf-{available,enabled}/nets-helper.conf`,
     `/etc/letsencrypt/live/data.blindhams.network/`,
     `/var/spool/cron/crontabs/{root,ner}` (the bhn-related lines).
3. Take a final tarball of everything bhn-related on Andre's box BEFORE
   decommission, archive it on Noel's side. (1Password agent SSH
   makes a single `tar -czf` over SSH straightforward.)
4. Audit `/etc/hosts`, log files, and anything else that referenced
   `data.blindhams.network` for residue.
5. Thank Andre. Genuinely.

**Andre during Phase 8:** the migration is over; this is just
cleanup. Andre's box returns to serving only `3.onj.me` and his other
services.

---

## 4. Cutover sequence summary (one-page version)

```
T-30d  Phase 1: nginx vhost + (optional) cert on rarbox
T-25d  Phase 2: publisher cron on rarbox, writing to /var/www/data
T-20d  Phase 3: bhn-nets-helper systemd unit on rarbox
T-14d  Phase 4: decide IRI/GIRO disposition (likely: leave on Andre)
T-7d   Phase 5: 7-day soak — both stacks running, --resolve testing
T-1d   Lower DNS TTL to 300 s
T-0    Phase 6: DNS cutover. Watch logs. Confirm propagation.
T+14d  Phase 7 ends. No rollback was needed.
T+15d  Phase 8: notify Andre, decommission his side, archive backup.
```

---

## 5. Rollback plan

The plan is safe at every phase because Andre's box is never modified
until Phase 8.

| Phase | If something goes wrong, you do this |
|---|---|
| 1 | Disable nginx server block on rarbox. No public impact. |
| 2 | Disable cron entries on rarbox. Diagnose. |
| 3 | Stop systemd units on rarbox. Diagnose. |
| 4 | N/A (research compute, no production impact). |
| 5 | Don't proceed to cutover. Fix on rarbox. |
| 6 (cutover) | Revert DNS A/AAAA. Traffic returns to Andre within TTL window (5 min if pre-lowered). Helper edits made on rarbox during the brief window need to be replayed onto Andre — capture them via diff of `~/bhn/_data/nets.json` between the two boxes and apply manually. Acceptable risk for a ~5-min window. |
| 7 (soak) | Revert DNS again if a problem surfaces. Same backfill consideration as Phase 6. |
| 8 (decommission) | At this point rollback means re-creating Andre's setup from the archived tarball. Don't proceed to Phase 8 until very confident. |

---

## 6. What this plan deliberately does NOT do

- **Does not migrate the Jekyll site.** It's on Netlify; the migration
  is only for `data.blindhams.network`.
- **Does not rebrand anything.** Per the brand rule, Blind Hams keeps
  its primary brand. The migration is invisible to users —
  `data.blindhams.network` stays `data.blindhams.network`.
- **Does not collapse the 5-min and 10-min cron cadences** into
  systemd timers, even though that would be more idiomatic. Cron is
  fine; one less thing to translate.
- **Does not implement the per-grid solar website.** That's a
  separate future project and the brief explicitly said "design
  phase."
- **Does not fix every TODO in `~/bhn/TODO.md`.** Items like batch
  publish, search highlighting, deep-linking filters — those are
  separate sprints. The migration goal is "preserve current
  behavior on a different box," not "ship every backlog item."
- **Does not change the data shapes** in any of the JSON feeds.
  Whatever the public site is hydrating from today, it hydrates
  identically tomorrow.

---

## Appendix — What changes if Noel picks Option B (R2-static + rarbox-dynamic)

If Option B wins:

- **Phase 1.5** added: provision a Cloudflare R2 bucket
  `bhn-data-prod`, configure CORS + custom domain
  `data.blindhams.network` (Cloudflare DNS routing), set up a
  Cloudflare Worker that serves `/*.json` from R2 and routes
  `/nets-helper/*` to rarbox.
- **Phase 2 changes:** publishers don't write to `/var/www/data/`.
  Their final step becomes "PUT to R2" via `aws s3 cp` (S3-compatible
  endpoint) or `rclone copyto`. Add retry logic for transient PUT
  failures.
- **Phase 3 unchanged** functionally — the helper still runs on
  rarbox and is still reached via the public hostname; only the
  routing to it changes (Worker vs. nginx).
- **Phase 5 verify** must include verifying that R2 GETs return the
  fresh JSON within seconds of publisher upload (R2 is
  strongly-consistent for new objects but Cloudflare CDN cache TTL
  can lag; needs `Cache-Control: max-age=60` set on the R2 object
  metadata to match Apache's behavior today).
- **Phase 6 cutover** is via DNS at Cloudflare (CNAME flip), which is
  fast.
- **Phase 8** is unchanged.

Net: about 1.5 extra phases of complexity, maybe 2 extra days of soak
to validate the R2 caching behavior. Worth it only if the precedent /
consistency-with-JJ-Flexible-Data-Provider matters to Noel for ops
reasons.

---

End of migration plan.
