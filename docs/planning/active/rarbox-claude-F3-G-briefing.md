# Rarbox-Claude Handoff Briefing — Phase 0 Section F3-G

**Date:** 2026-05-07
**You are:** Claude Code running on rarbox at `/home/ner/jjflex-ng/`
**Orchestrator:** Claude Code on Noel's Windows box, who composed this briefing
**Verifier:** Noel (he checks state changes between sections)

This briefing supplements `rarbox-setup-runbook-for-claude.md` (in this same directory) — the runbook is your procedure; this briefing is the design overlay (storage schema, memory constraints, F1-F2 already-done state, endpoint contracts).

## Mission

Execute **Section F3 → F4 → F5 → G** of the rarbox setup runbook. Sections F1 and F2 are already complete (state below). When G passes, send the report-back template at the bottom of the runbook to Noel via this same workflow.

## State so far (F1-F2 done)

Both completed by orchestrator-Claude over SSH. Verified outputs:

- **F1:** `nginx 1.26.3-3+deb13u2` and `certbot 4.0.0-2` and `python3-certbot-nginx 4.0.0-2` installed via apt. nginx.service is active+enabled. UFW already had 80/443 open from rarbox-hardening baseline.
- **F2:** Python 3.13.5 + venv at `/opt/jjflex-receiver/venv/`. Inside: `fastapi 0.136.1`, `uvicorn 0.46.0`, `pydantic 2.13.4`, `python-multipart 0.0.27`. Storage dir `/var/lib/jjflex-receiver/` not yet created — do that in F3.

So F3 picks up at: write `app.py`, create `/var/lib/jjflex-receiver/`, smoke-test the import.

## Storage design (decided 2026-05-07, approved by Noel)

Three-layer storage, file-based truth + SQLite index for query/triage:

### Layer 1 — Bundles on disk (the source of truth, immutable)

- Path: `/var/lib/jjflex-receiver/<YYYY-MM-DD>/<uuid>.zip`
- Content: the user's uploaded zip, byte-for-byte. Never modified after write.
- The zip is treated as opaque. The receiver does NOT extract or inspect contents.

### Layer 2 — Metadata sidecar (per-bundle JSON, alongside the zip)

- Path: `/var/lib/jjflex-receiver/<YYYY-MM-DD>/<uuid>.json`
- Schema (write all fields; null for absent optionals):

```json
{
  "uuid": "01HXYZ...",
  "received_at": "2026-05-07T20:30:00Z",
  "kind": "crashes",
  "client_ip_hash": "<sha256 of X-Forwarded-For, hex truncated to 16 chars>",
  "content_type": "application/zip",
  "size_bytes": 12345,
  "user_agent": "<from request header>",
  "user_note": "<from form field 'user_note' if present, else null>",
  "app_version": "<from form field 'app_version' if present, else null>",
  "env_fingerprint": "<from form field 'env_fingerprint' if present, else null>",
  "dedup_hash": "<sha256 of the zip bytes, hex>",
  "triage": {
    "status": "untriaged",
    "classifications": [],
    "responses": []
  }
}
```

The `triage` block is reserved for the future triage agent (see memory excerpt below); it ships as `untriaged` with empty arrays. The triage agent will populate it post-receipt.

### Layer 3 — SQLite index (the fast query view, rebuildable from sidecars)

- Path: `/var/lib/jjflex-receiver/index.db`
- Single table `bundles`:

```sql
CREATE TABLE IF NOT EXISTS bundles (
    uuid TEXT PRIMARY KEY,
    received_at TEXT NOT NULL,        -- ISO8601 UTC
    kind TEXT NOT NULL CHECK (kind IN ('crashes', 'feedback')),
    bundle_path TEXT NOT NULL,        -- relative to /var/lib/jjflex-receiver
    metadata_path TEXT NOT NULL,      -- relative to /var/lib/jjflex-receiver
    size_bytes INTEGER NOT NULL,
    dedup_hash TEXT NOT NULL,
    app_version TEXT,
    user_agent TEXT,
    triage_status TEXT NOT NULL DEFAULT 'untriaged'
        CHECK (triage_status IN ('untriaged', 'in_progress', 'triaged', 'duplicate', 'invalid'))
);
CREATE INDEX IF NOT EXISTS idx_bundles_received_at ON bundles(received_at);
CREATE INDEX IF NOT EXISTS idx_bundles_dedup_hash ON bundles(dedup_hash);
CREATE INDEX IF NOT EXISTS idx_bundles_triage_status ON bundles(triage_status);
CREATE INDEX IF NOT EXISTS idx_bundles_kind ON bundles(kind);
```

Insert one row per accepted upload. If the receiver crashes mid-write, the sidecar JSON is the recovery source — a future `rebuild-index.py` can reconstruct the SQLite from sidecar files. **Don't write that rebuild script now**; just make sure the sidecar contains everything needed to rebuild it later.

Schema migrations: add a `schema_version INTEGER` table or use `PRAGMA user_version`. v1 is `1`. If you change the schema in the future, bump it.

## Endpoint contracts

### POST /crashes — accept a crash bundle

**Request:**
- Content-Type: `multipart/form-data`
- Required field `file`: the bundle as a `.zip` upload
- Optional fields: `user_note`, `app_version`, `env_fingerprint`
- Max body size: 50 MB (enforced at both nginx + FastAPI layers — nginx config in F5 has `client_max_body_size 50M`)

**Validation (return 4xx with structured error on failure):**
- 415 Unsupported Media Type if Content-Type isn't multipart/form-data
- 422 Unprocessable Entity if the `file` field is missing
- 400 Bad Request if uploaded bytes don't start with `PK\x03\x04` (zip magic)
- 413 Payload Too Large if size exceeds 50 MB (nginx will likely catch this first)

**Response (200):**
```json
{
  "uuid": "<the assigned uuid>",
  "received_at": "<ISO8601 UTC>",
  "kind": "crashes"
}
```

### POST /feedback — accept a feedback bundle

Identical shape to `/crashes` but stored with `kind: "feedback"`.

### GET /healthz — liveness probe (no auth)

Returns 200 with `{"status": "ok"}`. No persistence side-effects. Used by F4 verify step + future monitoring.

## Validation + IP handling

**Per `project_no_silent_phone_home.md` (inlined below):** the receiver hashes the client IP at receipt time and stores only the truncated hash. Never persist the raw IP. Never log the raw IP. The hash is for dedup-of-bursting-uploader detection and rate-limit signals; it's not a user identity key.

**Hash recipe:** `hashlib.sha256(client_ip.encode()).hexdigest()[:16]` — 16 hex chars (64-bit prefix) is enough collision-resistance for our scale and short enough to be log-friendly.

**Where to read client IP:** prefer `X-Forwarded-For` header (set by nginx in F5: `proxy_set_header X-Forwarded-For $remote_addr;`). Fall back to direct request client IP if header absent. Never trust XFF naively — but here nginx is the only thing setting it, and nginx is the only thing in front of you, so the value is trustworthy.

## Logging (structured JSON to stdout)

uvicorn captures stdout to journald via the systemd unit. One log line per request:

```json
{"ts":"2026-05-07T20:30:00Z","level":"info","event":"bundle_received","kind":"crashes","uuid":"01HXYZ...","size_bytes":12345,"dedup_hash":"abc...","client_ip_hash":"deadbeef..."}
```

Errors get `"level":"error"` and an `error` field with the validation reason. Use Python's stdlib `logging` with a JSON formatter — don't pull in another dep just for log structure unless you find one already in the venv.

## /healthz route — required, F4 verify depends on it

The runbook's F4 verify step runs `curl -sf http://127.0.0.1:8000/healthz`. Make sure the route exists in `app.py`. Returns 200 with `{"status":"ok"}`. No db queries; pure liveness.

## Inlined memory excerpts (load-bearing constraints)

These are excerpts from the orchestrator's memory system that constrain your design choices. They're not in your local memory — read them as authoritative project rules.

### From `project_crash_triage_bundle_flow.md`

> Three roles in the crash-handling pipeline:
>
> 1. **The user's JJFlex install.** Hits a crash, generates a bundle (zip with logs, traces, env metadata, the dispatcher exception itself). Uploads via consented "Send crash report" UX.
> 2. **The receiver (FastAPI on rarbox).** Accepts POST, validates content-type + size, writes the bundle to `/var/lib/jjflex-receiver/<YYYY-MM-DD>/<uuid>.zip`. Returns 200. **No parsing, no classification — just durable storage.**
> 3. **The triage agent (Claude session, runs after).** Reads new bundles, classifies against memory entries, drafts responses, optionally repackages the bundle with triage metadata.

**Implication for you:** the receiver does NOT crack open the zip. Don't parse, don't extract, don't validate internal structure. Just verify it's a zip (magic bytes), persist it, index it, return 200. The triage agent — a future Claude run — does the analysis.

### From `project_no_silent_phone_home.md`

> JJ Flex does NOT send data to servers without explicit per-event user action. No usage analytics, no background crash reporting, no telemetry. User-consented update checks are narrow exception. Deliberate exception to friction-tax where transparency wins.

**Implication for you:** the user has explicitly consented per-upload by the time bytes reach you (the UX gate is on the JJFlex side). But your receiver inherits the spirit: don't log identifying info you don't need. IP gets hashed. User-Agent is fine to store (for debugging). Don't add any "phone home" of your own from the receiver to anywhere else.

### From `feedback_user_prefers_auditable_systems.md`

> Noel prefers auditable, editable, file-based systems over organic ones. Same ethos as JJFlex's no-silent-phone-home applied to Claude's own state. Default to explicit-and-inspectable when proposing tools, workflows, libraries.

**Implication for you:** the file-based bundle + sidecar JSON is the primary truth. SQLite is a fast index, not the truth. Anyone with `ls` and `cat` can audit what's been received and what triage state it's in, without needing to query a database. That's intentional. Don't move data INTO SQLite that isn't also in the sidecar.

## F5 confirmation gates — important

The runbook's Section F5 has TWO explicit CONFIRM gates:

1. **Cloudflare Origin Cert must exist before nginx config goes in.** The cert and key need to be at:
   - `/etc/ssl/cloudflare/jjflexible.radio.pem`
   - `/etc/ssl/cloudflare/jjflexible.radio.key`

   These need to be generated by Noel from the Cloudflare dashboard (SSL/TLS → Origin Server → Create Certificate). If they don't exist when you reach F5 pre-check, **STOP and ask Noel**. Don't try to use Let's Encrypt as a fallback — the architectural choice is Origin Cert, see the runbook.

2. **Don't reload nginx without confirmation.** F5's `sudo systemctl reload nginx` is the moment the receiver becomes externally reachable. Run `sudo nginx -t` to validate config first. Once syntax check passes, ASK NOEL before reloading.

## Reporting protocol

Per the runbook's "How to use" section: **log every command + verbatim output in your conversation transcript. NOT summaries.** Future-debugging value comes from actual output. When you run a command and it produces 30 lines of apt install messages, the orchestrator wants those 30 lines visible in the transcript, not "(install succeeded)".

After each section completes, write a one-line "Section FN complete" summary so it's easy to scan, but the supporting transcript stays full-fidelity.

After Section G passes, fill in and post the report-back template at the bottom of the runbook.

## Out-of-scope — DO NOT do these things

- **Don't bump versions or commit code.** This rarbox repo is stale and read-only for you. The orchestrator manages git state on the Windows side.
- **Don't push to git.** No `git push`, no `git commit`, no `git pull`.
- **Don't modify rarbox config beyond what F3-G calls for.** No UFW changes, no SSH config, no fail2ban. The hardening baseline is governed by `project_rarbox_hardening.md` and lives outside this scope.
- **Don't write a rebuild-index.py utility.** Future scope. Just make sure sidecar JSON contains enough to rebuild the index, and document in a comment that it's the recovery source.
- **Don't write the triage agent.** Future scope. The receiver's `triage` JSON block + SQLite `triage_status` column are placeholders for that work.
- **Don't add authentication.** The receiver is unauthenticated. Cloudflare + nginx can be configured to add auth later if abuse appears; not in v1 scope.
- **Don't add rate-limiting code in app.py.** If we need it later, it goes in nginx, not Python. v1 is permissive.
- **Don't redact or filter bundle contents.** The whole point of forensic preservation is byte-perfect storage of what the user consented to upload.

## Acceptance criteria — when you're done

You're done when ALL of these are true:

1. `app.py` exists at `/opt/jjflex-receiver/app.py`. Importable cleanly. Implements `/crashes`, `/feedback`, `/healthz`. Validates as specified. Logs as specified.
2. `/var/lib/jjflex-receiver/` exists, owned by `ner:ner`. Contains an empty `index.db` with the schema applied.
3. `jjflex-receiver.service` is `active` and `enabled` per F4. `curl http://127.0.0.1:8000/healthz` returns 200 OK with `{"status":"ok"}`.
4. Cloudflare Origin Cert is in place at `/etc/ssl/cloudflare/jjflexible.radio.pem` + `.key` (Noel-confirmed before F5).
5. nginx site `jjflex-crashes` is enabled. `nginx -t` reports OK. nginx is reloaded (Noel-confirmed before reload).
6. Section G test passes: `curl -X POST -F 'file=@/tmp/test.zip' https://crashes.jjflexible.radio/feedback` returns 200 with a UUID. The bundle lands at `/var/lib/jjflex-receiver/<today>/<uuid>.zip`. The sidecar JSON exists at `<uuid>.json`. SQLite has a row for the upload. Receiver logs the request to journald.
7. The report-back template at the bottom of `rarbox-setup-runbook-for-claude.md` is filled in and posted in your transcript.

## Cross-references

- `rarbox-setup-runbook-for-claude.md` — your procedure (in this same directory)
- `phase-0-runbook.md` — the human-oriented Phase 0 (Sections A-G); your work is the F3-G part
