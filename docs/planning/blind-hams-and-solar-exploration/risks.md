# Risks — Blind Hams + Solar migration to rarbox

Risks identified during the read-only exploration. Categorized by
type, with a concrete mitigation per risk. Most are addressable by
plan choices made in `migration-plan.md`; a few need vigilance during
execution.

---

## DNS / cutover risks

### R-DNS-01 — DNS propagation lag during cutover

**What:** flipping `data.blindhams.network` from Andre's IP to
rarbox's IP takes time to propagate; resolvers will return the old IP
for up to TTL seconds (currently unknown — likely 3600 s default if
Noel's DNS provider is on defaults).

**Impact:** during the propagation window, some clients keep hitting
Andre, some hit rarbox. Both are publishing valid JSON, so the
*content* is okay. But helper writes during the window go to whichever
host the writer's resolver picked, which can split state.

**Mitigation:**
- Lower TTL to 300 s at least 24 h before cutover.
- Schedule cutover during a low-traffic window (overnight US time).
- Briefly disable the bhn-nets-helper on Andre right before cutover
  to prevent split-state writes (admin UI returns 503 for ~10 min).
  This reduces user friction less than data corruption would.
- After the cutover window, manually diff
  `~/bhn/_data/nets.json` between rarbox and Andre. Apply any
  Andre-side edits to rarbox. (Should be empty if helper was
  briefly disabled — but verify.)

### R-DNS-02 — TLS cert provisioning before DNS exists at rarbox

**What:** Let's Encrypt's HTTP-01 challenge requires the public name
to point at rarbox already. Catch-22 with cutover.

**Impact:** can't easily get a public cert for the new host until DNS
has moved.

**Mitigation:**
- Use DNS-01 challenge (TXT record at the DNS provider) — works
  without DNS change. Slightly more setup but clean.
- Or, do the DNS cutover and cert provisioning in the same change
  window: flip DNS, immediately run `certbot --nginx -d
  data.blindhams.network`. Brief HTTPS-broken window for clients
  that hit rarbox before the cert is issued.
- Plan favors DNS-01 to avoid even that brief window.

### R-DNS-03 — AllStar / TTS consumer breaks during cutover

**What:** the TTS-and-AllStar consumer of `solar_voice.txt` lives on
a third surface (questions-for-noel.md Q3). If it has TLS pinning to
Andre's certificate or aggressive HTTPS connection caching, the
cutover could break audio for one or more nets.

**Impact:** a net runs without the solar report or with a stale one.

**Mitigation:**
- Identify the consumer (Q3 must be answered).
- Test the consumer against rarbox-via-`--resolve` before cutover.
- If pinning is a risk, coordinate the cert change with the consumer's
  operator (likely Noel himself or the AllStar node admin).
- Schedule cutover on a non-net-day if possible.

### R-DNS-04 — Apache vs nginx config drift on the public-headers contract

**What:** the migration plan promises identical CORS / Content-Type /
Cache-Control headers between Andre and rarbox. Apache and nginx
emit headers slightly differently (Apache's `Header always set`
behaves differently from nginx's `add_header`, especially on error
responses).

**Impact:** a corner case where a 404 or 500 from rarbox lacks the
CORS header that Apache would have included via `Header always`. CORS
preflights could fail.

**Mitigation:**
- Use `add_header ... always;` semantics in nginx (the `always`
  parameter applies the header to error responses too — added in
  nginx 1.7.5+).
- Phase 5 verification matrix specifically checks headers via
  `curl -I` with `-H "Origin: ..."` and inspects 404/500 responses.

---

## Service-continuity risks

### R-SVC-01 — Cron job overlap / split-state during dual-running phase

**What:** during Phase 5 soak, both Andre and rarbox publishers are
running. Their `~/bhn/_data/nets.json` sources are different (helper
writes go to whichever host the admin reaches at the moment). After
soak, the rarbox version of nets.json may diverge from Andre's.

**Impact:** if Phase 6 cutover rolls back to Andre, helper edits made
during the soak on rarbox are lost.

**Mitigation:**
- During Phase 5 soak, route helper traffic to *one* host
  (rarbox-via-`--resolve` from authorized testers; admin UI on Andre
  pointed at by the public DNS name still serves real admin
  traffic).
- Document explicitly: "during soak, all real admin traffic goes to
  Andre. Rarbox helper is for verification only."
- At cutover time, capture a snapshot of `_data/` from Andre,
  rsync to rarbox. Helper-on-Andre is paused right before cutover.

### R-SVC-02 — Publisher's `git pull` failure carries forward

**What:** Andre's publisher has been silently failing `git pull` for
weeks. If the migration just copies the publisher scripts wholesale
without addressing the underlying SSH issue or the architecture, the
same problem reappears on rarbox.

**Impact:** rarbox publisher publishes stale data forever (masked by
`\|\| true`), and operationally things look fine until the next time
someone notices.

**Mitigation:**
- Migration plan Phase 2 explicitly fixes this: AUTOPULL=0, with the
  helper pushing back to GitHub (Q1) so the source-of-truth flow is
  unambiguous.
- Don't preserve the `\|\| true` mask. Fail loud if something's
  wrong, so Noel finds out immediately.

### R-SVC-03 — Tmpfile leak persists on rarbox

**What:** the `build_*.sh` scripts strand tmpfiles when they fail
(see inventory.md). Without the trap-fix, rarbox accumulates the same
debris.

**Impact:** disk usage creeps; `/var/www/data/` directory listings get
slow.

**Mitigation:**
- Migration plan Phase 2 fixes this with a `trap 'rm -f
  "$TMP" "$TMP.pretty"' EXIT` in each wrapper, plus an `mktemp`-based
  filename. The fix is small and obvious.
- Also: Phase 2 cleanup adds a one-time sweep for any leftover
  tmpfiles after enabling the cron, to confirm the fix held.

### R-SVC-04 — bhn-nets-helper memory growth or worker crashes

**What:** the helper runs Flask under gunicorn with 2 workers. Memory
on Andre is 44 MB peak after 23 h. Could grow over a multi-week soak.

**Impact:** rarbox runs out of memory, helper restarts, brief
disruption.

**Mitigation:**
- Set `--max-requests 1000 --max-requests-jitter 100` in the
  gunicorn unit so workers periodically recycle (Flask + gunicorn
  best practice).
- Add `MemoryHigh=` and `MemoryMax=` to the systemd unit (e.g.
  `MemoryMax=512M`) so a runaway process doesn't take down rarbox.
- Phase 5 soak observes the helper for 7 days; any leak with a
  reasonable rate would surface in that window.

### R-SVC-05 — GIRO upstream HTTP 503 rate-limiting

**What:** `~/iri/giro_cron.log` is filling up with HTTP 503 errors
right now (May 2). DIDBase (`lgdc.uml.edu`) is rate-limiting or down.

**Impact:** GIRO comparison data hasn't been written to CSV since
2025-11-12 (29 days ago). The research data set has a 6-month gap.

**Mitigation:**
- This is upstream weather, not migration-related. Noted because if
  the migration moves the GIRO compute, the move can be done now
  without losing data the cron isn't producing anyway.
- Consider rate-limiting the GIRO requests (currently 7 stations ×
  multiple measurements every 30 min — possibly tripping a per-IP
  rate limit at DIDBase).
- Maybe email DIDBase admin if the 503s persist much longer.
- Out of scope for migration plan; flagged for separate followup.

### R-SVC-06 — Apache mod_php still installed on Andre

**What:** Apache on Andre has `php7.4` modules loaded, plus the conf.
Not used by Blind Hams. But: Andre may have his own PHP-using
content. Out of scope for migration.

**Impact:** none on Blind Hams. Mentioned because the read-only
inventory caught it.

**Mitigation:** N/A — Andre's stack, his choice. Don't touch it during
decommission Phase 8 unless he asks.

---

## Data-loss risks

### R-DATA-01 — Helper writes lost during cutover window

**What:** during DNS cutover (Phase 6), if an admin is mid-edit, the
write may go to whichever host their resolver returns. If that host
isn't the one we end up consolidating to, the edit is lost.

**Impact:** an admin's submission disappears.

**Mitigation:**
- Pause the helper on Andre right before cutover (returns 503).
- Communicate the pause window to the admin team (just Noel + a
  small group based on the inventory; not many people).
- Re-enable on rarbox after cutover succeeds.

### R-DATA-02 — Backup tarball at decommission misses a path

**What:** at Phase 8, Noel takes a final tarball of "everything
bhn-related" on Andre before decommission. If we missed something in
this inventory, it gets deleted with the rest.

**Impact:** a hidden config or data file goes away forever.

**Mitigation:**
- The inventory is thorough. Specifically, before Phase 8:
  - `find /home/ner -name "*.json" -newer ...` to catch recent
    changes.
  - `find /opt/bhn /opt/bhn_untracked_backup /opt/scripts /var/www/data
    -type f` to catch everything.
  - `tar -czf bhn-andre-final.tar.gz` of the lot, store on Noel's
    NAS (`backup-private-to-nas.ps1` pattern from CLAUDE.md applies).
- Don't actually decommission until the tarball is verified on the
  NAS.

### R-DATA-03 — `_data/` divergence between Andre, rarbox, and GitHub

**What:** the WSL copy of `~/bhn/_data/nets.json` (which Noel may
edit and push to GitHub) is stale relative to Andre's. After
migration, if the source-of-truth flow isn't crisp (Q1), three
locations could each have different versions.

**Impact:** confusion. An admin approves a net via the helper; Noel
edits the same file on his laptop and pushes; the publisher's pull
may overwrite the helper's edit (if AUTOPULL is on) or vice-versa.

**Mitigation:**
- Q1's option (a) (helper pushes to GitHub) makes GitHub the
  source-of-truth and resolves divergence by definition.
- Pre-cutover: rebase the WSL repo onto whatever's actually on
  Andre right now, so all three are aligned at the moment of
  migration.

### R-DATA-04 — Forgotten files in `/www/data` (the legacy directory)

**What:** there's a `/www/data/solar.json` and `/www/data/solar_voice.txt`
from 2025-10-15 on Andre. Apache doesn't serve from there. But: is
anything else (a script, a symlink) reading from there?

**Impact:** unlikely to break anything if they linger, but if
something *was* depending on them they've already been broken since
October without anyone noticing.

**Mitigation:**
- Inventory captured this — leaving `/www/data/` alone during
  read-only exploration. At decommission Phase 8, leave them alone
  on Andre too. They're 9 KB total — not worth surgery.

---

## Andre-relationship risks

### R-ANDRE-01 — Andre's own services break during decommission

**What:** Andre runs other things on the box (his personal site, the
unidentified ports `8000`, `9000`, `13884`). If Phase 8 decommission
is overzealous and removes shared bits (Apache modules, certbot
config, system packages), it could break his stack.

**Impact:** ruined relationship + Andre's site broken.

**Mitigation:**
- Phase 8 plan is **scoped explicitly** to:
  - The two `data.blindhams.network*` Apache vhosts
  - The `nets-helper.conf` in `conf-enabled/`
  - The systemd units `bhn-nets-helper.{service,socket}`
  - The cron entries containing `bhn`/`solar` (in `root` and `ner`
    crontabs, leaving everything else)
  - The `/opt/bhn*` directories
  - The `/var/www/data/` directory
  - The `/etc/letsencrypt/live/data.blindhams.network/` cert
- Apache itself, mod_proxy, mod_php, certbot, etc. **stay**. They
  serve Andre's needs.
- Tarball captured before any decommission step.
- Andre runs the actual decommission commands (or watches Noel run
  them) — agent doesn't auto-execute on his box.

### R-ANDRE-02 — Surprise migration

**What:** if Andre learns of the migration only at cutover, he may
feel his goodwill was taken for granted (he's hosted for years). The
brief calls this out specifically: "preserve goodwill."

**Impact:** strained relationship.

**Mitigation:**
- Notify Andre at start of Phase 5 (one week before cutover).
- Let him weigh in on cutover timing if he has constraints.
- Be explicit about scope: only blind-hams paths are touched, his
  other services stay.
- Thank him in writing. Maybe a small gift if appropriate.

### R-ANDRE-03 — Andre's box also has Noel's personal scripts/files

**What:** `~/scodex.sh`, `~/sco.txt` (1.3 MB), `~/.codex/` exist on
Andre's home. Not bhn-related — Codex / Noel-personal-tooling.

**Impact:** during decommission, it's tempting to "clean up Noel's
home dir on Andre's box" — but that's separate from blind hams.

**Mitigation:**
- Phase 8 plan is **scoped to bhn paths only**. Noel's home dir on
  Andre belongs to Noel; he can clean it whenever, separately.

---

## Rollback risks

### R-RB-01 — Rollback after Phase 6 needs `_data/` reconciliation

**What:** if cutover fails and rollback to Andre is needed, helper
edits made on rarbox during the cutover window need to be replayed
onto Andre.

**Impact:** ~5–10 minutes of admin work gone if rollback happens.
Likely zero if Phase 6 mitigation (pause helper before cutover) is
followed.

**Mitigation:**
- Phase 6 mitigation ensures no writes during cutover.
- If a write somehow lands on rarbox: capture with `git diff
  /home/ner/bhn/_data/nets.json` on rarbox vs the pre-cutover
  snapshot, manually apply to Andre.

### R-RB-02 — Phase 8 (decommission) is point-of-no-return

**What:** once Andre's bhn paths are removed, rollback to Andre
requires restoring from the backup tarball. Not impossible but
non-trivial under time pressure.

**Impact:** if a problem surfaces 3 weeks after Phase 8, restoring
Andre is harder than it would have been during the soak.

**Mitigation:**
- Don't proceed to Phase 8 until rarbox has been stable for 14+
  days post-cutover.
- Verified backup tarball in NAS before Phase 8 fires.
- All migration scripts/configs versioned in `nromey/bh-network` (or
  a separate `bhn-infra` repo if Noel wants to keep infra config out
  of the public repo) so re-creating from scratch is also possible.

### R-RB-03 — `/etc/apache2/.htpasswd_bhn_nets` is a single file copy

**What:** the helper's basic auth credentials live in a single file
on Andre. The migration plan copies it to rarbox. If the copy
silently fails or is out-of-date, the helper UI is unreachable on
rarbox.

**Impact:** admin UI returns 401 / 403 forever.

**Mitigation:**
- Phase 5 verification specifically tests basic auth on rarbox
  with known-good credentials before considering Phase 5 complete.
- File should be tracked somewhere outside Andre's box (a vault, a
  password manager) so it can be re-created if both Andre and
  rarbox versions are lost.

---

## Operational risks (additional, beyond the brief's categories)

### R-OPS-01 — `*.bak` configs and diverged `sites-available/` vs `sites-enabled/`

**What:** Andre has `data.blindhams.network.conf.bak` and a diverged
`sites-available/` version of the data vhost that differs from
`sites-enabled/` on CORS settings. Easy to copy the wrong one during
migration.

**Impact:** rarbox ends up with subtly different headers than what's
actually live.

**Mitigation:**
- Migration plan explicitly references the **enabled** vhost
  (`sites-enabled/data.blindhams.network*.conf`) as canonical, not
  the available copy.
- Phase 5 verification compares headers byte-for-byte between Andre's
  current responses and rarbox's. Any drift surfaces immediately.

### R-OPS-02 — Notification topic discoverable

**What:** the public ntfy.sh topic `bh-nets-helper` is publicly
subscribable. Anyone monitoring it sees every net submission as it
arrives.

**Impact:** mostly hypothetical — net submission contents aren't
sensitive. But: the JJ-Flex no-phone-home principle nudges away from
silently keeping public-by-default external services.

**Mitigation:** see questions-for-noel.md Q6. If Noel decides public
is fine, document it as an explicit choice.

### R-OPS-03 — Publisher cron runs as root

**What:** `/var/spool/cron/crontabs/root` runs `deploy_all.sh` and
`build_solar_json.py` as root. The scripts read from
`/home/ner/bhn/_data/` and write to `/var/www/data/`. Neither needs
root.

**Impact:** any bug in the publisher has root-level blast radius.
Currently a tmpfile leak with root ownership in the public DocumentRoot —
hasn't caused harm but illustrates the risk.

**Mitigation:** Phase 2 of the migration plan explicitly switches
the publisher to run as `ner`, not root.

### R-OPS-04 — IRI `iri_muf_driver` binary portability

**What:** the compiled Fortran binary on Andre is 1.6 MB stripped
ELF. May or may not run on rarbox's libc / Debian 13.

**Impact:** if Phase 4 (option B) moves the GIRO research to rarbox,
the binary may segfault and need recompilation from `~/iri/iri_driver/`.

**Mitigation:**
- Phase 4 default is "stay on Andre" / "move to 40-core box later" —
  so this risk doesn't fire unless Noel chooses to move now.
- If Noel chooses move-now: smoke-test the binary on rarbox before
  relying on it; rebuild from source if needed (gfortran is in
  Debian repos).

---

## Risks already mitigated by the plan as-written

For completeness — these surfaced during exploration but the plan
already addresses them:

- The publisher's broken `git pull --ff-only` (silently masked by
  `\|\| true`) — fixed in Phase 2 by switching to AUTOPULL=0.
- Tmpfile leak (2,045 stranded files) — fixed in Phase 2 with
  `trap` cleanup.
- Publisher running as root — fixed in Phase 2 by switching to
  `ner`.
- Header consistency between Apache and nginx — covered by Phase 5
  verification matrix.
- Andre's box being touched accidentally — read-only constraint
  during exploration; explicit scoping in Phase 8 decommission.

---

End of risks.
