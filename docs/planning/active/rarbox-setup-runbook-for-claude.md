# Claude-Executable Rarbox Setup Runbook

**Audience:** A Claude session running with Bash tool access AND the calling machine has Noel's SSH key loaded for `ner@rarbox.macaw-jazz.ts.net` over Tailscale. This runbook produces the rarbox-side configuration for Phase 0 Sections F and G of `phase-0-runbook.md` — the crashes.jjflexible.radio receiver endpoint.

**Trial context:** This runbook is the first concrete instance of the "Claude as rarbox operator" execution model captured in `memory/project_claude_as_rarbox_operator.md`. Noel verifies critical state changes; Claude executes the runbook sections. If this trial goes well, the model generalizes to future server-side ops (security patches, config tweaks, future receiver endpoints).

**Prerequisites Claude must verify before starting:**

1. **Tailscale is up on the calling machine.** Check: `tailscale status | grep rarbox`. Should show rarbox as online. If not, STOP and report — Noel needs to bring Tailscale up.
2. **SSH to rarbox works.** Test: `ssh -o ConnectTimeout=10 ner@rarbox.macaw-jazz.ts.net "hostname && uptime"`. Should return rarbox's hostname + uptime. If it fails (Permission denied, timeout), STOP and report.
3. **Cloudflare A record for `crashes.jjflexible.radio` exists and points at rarbox's public IP `178.156.204.128`.** Test: `nslookup crashes.jjflexible.radio` from the calling machine. If it doesn't resolve, Noel hasn't done Phase 0 Section F step 1 yet — STOP and report.
4. **Noel has confirmed go-ahead for THIS RUN.** This runbook will install packages, write systemd units, modify nginx config, and reload services. It is NOT idempotent in a destructive sense (re-running won't break things, but it does write files). Confirm Noel said "go" or equivalent before starting.

If ANY prerequisite fails, STOP and report rather than proceed. Don't try to work around missing prereqs — that's Noel's call.

---

## How to use this runbook

Each section is independently checkpointable. Stop after any section heading and report state. Each section has:

- **Goal** — what state changes
- **Pre-check** — verify state before changing
- **Action** — the command(s) to run
- **Expected output** — what success looks like
- **Verify** — post-action check
- **Rollback** — what to do if the action fails

When running, log every command + verbatim output in the conversation transcript. NOT summaries. Future-debugging value comes from the actual output.

**Confirmation gates** are marked with **CONFIRM:** — for those steps, ask Noel before proceeding. The gates are placed at irreversible-or-impactful decisions, not routine steps.

**Destructive operations** are marked with **DESTRUCTIVE:** — these change persistent state (write files, restart services). Have a rollback ready before starting.

---

## Section F1 — Install nginx + certbot

**Goal:** nginx + certbot installed via apt. Idempotent — re-running is safe.

**Pre-check:**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "dpkg -l nginx 2>/dev/null | grep '^ii' && dpkg -l certbot 2>/dev/null | grep '^ii'"
```

If both are already installed (output shows two `ii` lines), skip to Section F2.

**Action (DESTRUCTIVE — installs packages):**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "sudo apt update && sudo apt install -y nginx certbot python3-certbot-nginx"
```

**Expected output:** apt update completes; install completes with "0 newly installed" (already there) or specific package install messages. No errors.

**Verify:**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "nginx -v && certbot --version"
```

**Rollback:** Not needed for installs. If the install fails partway, `sudo apt install -f` repairs.

---

## Section F2 — Install FastAPI + dependencies for the receiver

**Goal:** Python 3 + FastAPI + uvicorn available for the receiver service.

**Pre-check:**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "python3 --version && python3 -c 'import fastapi, uvicorn' 2>&1"
```

If both python3 and the import succeed, skip to Section F3.

**Action (DESTRUCTIVE — installs packages):**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "sudo apt install -y python3-pip python3-venv && sudo mkdir -p /opt/jjflex-receiver && sudo python3 -m venv /opt/jjflex-receiver/venv && sudo /opt/jjflex-receiver/venv/bin/pip install fastapi uvicorn python-multipart"
```

**Expected output:** apt install succeeds; venv created; pip install completes with "Successfully installed fastapi uvicorn python-multipart" + their dependencies.

**Verify:**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "/opt/jjflex-receiver/venv/bin/python -c 'import fastapi, uvicorn; print(fastapi.__version__, uvicorn.__version__)'"
```

**Rollback:** `sudo rm -rf /opt/jjflex-receiver/venv` removes the venv if you need to redo.

---

## Section F3 — Write the receiver Python code

**Goal:** A FastAPI receiver at `/opt/jjflex-receiver/app.py` that accepts POST to `/crashes` and `/feedback`, validates content-type + max size, writes the bundle to `/var/lib/jjflex-receiver/<YYYY-MM-DD>/<uuid>.zip`, and returns 200.

**Pre-check:**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "test -f /opt/jjflex-receiver/app.py && cat /opt/jjflex-receiver/app.py | head -5"
```

If the file already exists, **CONFIRM:** ask Noel whether to overwrite or skip. (Existing custom edits would be lost.)

**Action (DESTRUCTIVE — writes file):**

Use SCP or a heredoc to write `/opt/jjflex-receiver/app.py`. The file content is the FastAPI app — produce it now from `memory/project_crash_triage_bundle_flow.md`'s design pattern. Include:

- Pydantic models for the receive payload
- POST `/crashes` and `/feedback` routes
- Content-type whitelist (`application/zip`, `multipart/form-data`)
- Max body size (suggest 50 MB initially; tunable)
- UUID-named file write to `/var/lib/jjflex-receiver/<date>/<uuid>.zip`
- Stable header JSON inside the bundle (or alongside) for triage-friendly dedup keys
- 200 response with the UUID; 4xx for validation failures
- Structured logging to stdout (uvicorn captures)

After writing, also create the storage directory:
```bash
ssh ner@rarbox.macaw-jazz.ts.net "sudo mkdir -p /var/lib/jjflex-receiver && sudo chown ner:ner /var/lib/jjflex-receiver"
```

**Expected output:** Both the .py file and the storage directory exist with appropriate permissions.

**Verify:**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "/opt/jjflex-receiver/venv/bin/python -c 'import sys; sys.path.insert(0, \"/opt/jjflex-receiver\"); import app; print(\"app imports clean\")'"
```

**Rollback:** `sudo rm /opt/jjflex-receiver/app.py` if the file is wrong; `sudo rm -rf /var/lib/jjflex-receiver` if the directory needs reset.

---

## Section F4 — Create systemd unit for the receiver

**Goal:** `jjflex-receiver.service` running uvicorn on `127.0.0.1:8000` (Cloudflare-proxied via nginx, no public bind).

**Pre-check:**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "systemctl status jjflex-receiver 2>&1 | head -5"
```

If the service exists and is active, skip to Section F5.

**Action (DESTRUCTIVE — writes systemd unit):**

Write `/etc/systemd/system/jjflex-receiver.service`:

```ini
[Unit]
Description=JJ Flex crash + feedback receiver (FastAPI)
After=network.target

[Service]
Type=simple
User=ner
WorkingDirectory=/opt/jjflex-receiver
ExecStart=/opt/jjflex-receiver/venv/bin/uvicorn app:app --host 127.0.0.1 --port 8000 --no-access-log
Restart=on-failure
RestartSec=5
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
```

Then:
```bash
ssh ner@rarbox.macaw-jazz.ts.net "sudo systemctl daemon-reload && sudo systemctl enable jjflex-receiver && sudo systemctl start jjflex-receiver"
```

**Expected output:** `daemon-reload` silent; `enable` reports symlink created; `start` silent.

**Verify:**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "sudo systemctl status jjflex-receiver --no-pager | head -15 && curl -sf http://127.0.0.1:8000/healthz || echo 'healthz failed (add a /healthz route to app.py if missing)'"
```

**Rollback:** `sudo systemctl disable --now jjflex-receiver && sudo rm /etc/systemd/system/jjflex-receiver.service && sudo systemctl daemon-reload`.

---

## Section F5 — Configure nginx for crashes.jjflexible.radio

**Goal:** nginx site config that listens on 443, terminates TLS via Cloudflare Origin Certificate, proxies POST `/crashes` and POST `/feedback` to the receiver on 127.0.0.1:8000, returns 405 for other methods.

**Pre-check:**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "ls /etc/nginx/sites-available/jjflex-crashes 2>/dev/null"
```

If the file exists, **CONFIRM:** ask Noel whether to overwrite.

**CONFIRM with Noel before proceeding:** the Cloudflare Origin Certificate must be installed first. Per Phase 0 Section F step 6, generate via Cloudflare dashboard → SSL/TLS → Origin Server → Create Certificate. Cert and key go at `/etc/ssl/cloudflare/jjflexible.radio.pem` + `/etc/ssl/cloudflare/jjflexible.radio.key`. If those files don't exist yet, this section blocks until Noel provides them. Test:

```bash
ssh ner@rarbox.macaw-jazz.ts.net "ls /etc/ssl/cloudflare/jjflexible.radio.pem /etc/ssl/cloudflare/jjflexible.radio.key 2>&1"
```

**Action (DESTRUCTIVE — writes nginx config):**

Write `/etc/nginx/sites-available/jjflex-crashes`:

```nginx
server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name crashes.jjflexible.radio;

    ssl_certificate /etc/ssl/cloudflare/jjflexible.radio.pem;
    ssl_certificate_key /etc/ssl/cloudflare/jjflexible.radio.key;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;

    client_max_body_size 50M;

    location ~ ^/(crashes|feedback)$ {
        if ($request_method != POST) {
            return 405;
        }
        proxy_pass http://127.0.0.1:8000;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_read_timeout 60s;
    }

    location / {
        return 404;
    }
}

server {
    listen 80;
    listen [::]:80;
    server_name crashes.jjflexible.radio;
    return 301 https://$host$request_uri;
}
```

Then:
```bash
ssh ner@rarbox.macaw-jazz.ts.net "sudo ln -sf /etc/nginx/sites-available/jjflex-crashes /etc/nginx/sites-enabled/jjflex-crashes && sudo nginx -t"
```

**Expected output:** `nginx -t` reports syntax OK and test successful. If it doesn't, STOP — fix the config before proceeding.

**CONFIRM with Noel before reloading nginx** — this is the moment the receiver becomes externally reachable.

```bash
ssh ner@rarbox.macaw-jazz.ts.net "sudo systemctl reload nginx"
```

**Verify:**
```bash
curl -sI https://crashes.jjflexible.radio/ | head -5
# expect: HTTP/2 404 (the / location returns 404 by design)
```

**Rollback:** `sudo rm /etc/nginx/sites-enabled/jjflex-crashes && sudo systemctl reload nginx`.

---

## Section G — End-to-end verification

**Goal:** Confirm a POST to `https://crashes.jjflexible.radio/feedback` reaches the receiver, writes a bundle to disk, and returns 200.

**Action:**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "echo 'test bundle' > /tmp/test.zip && curl -X POST -F 'file=@/tmp/test.zip' https://crashes.jjflexible.radio/feedback"
```

**Expected output:** JSON response from the receiver with a UUID. Response code 200.

**Verify storage:**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "ls -la /var/lib/jjflex-receiver/$(date +%Y-%m-%d)/ | head -5"
```

Should show a `.zip` file with the matching UUID, modest size.

**Verify logs:**
```bash
ssh ner@rarbox.macaw-jazz.ts.net "sudo journalctl -u jjflex-receiver -n 20 --no-pager"
```

Should show a log line for the POST.

If any of these fail, the relevant rollback returns to a clean state. Report exact failure to Noel; don't attempt a creative fix.

---

## Report-back template for the orchestrator session

When Section G passes, send this back to Noel:

> **Rarbox setup complete (Sections F1-F5 + G).**
>
> - F1 — nginx + certbot installed (versions: nginx X, certbot Y)
> - F2 — Python venv at /opt/jjflex-receiver/venv with FastAPI X.Y, uvicorn X.Y
> - F3 — Receiver app at /opt/jjflex-receiver/app.py; storage at /var/lib/jjflex-receiver/
> - F4 — systemd unit jjflex-receiver.service active and enabled (uptime since X)
> - F5 — nginx site jjflex-crashes enabled; Cloudflare Origin Cert in place; reload OK
> - G — POST to crashes.jjflexible.radio/feedback returns 200 with UUID; bundle landed at /var/lib/jjflex-receiver/<date>/<uuid>.zip; receiver logged the request
>
> **State changed on rarbox:**
> - Packages installed (full apt list captured in conversation transcript)
> - Files written: app.py, jjflex-receiver.service, jjflex-crashes nginx site
> - Directories created: /opt/jjflex-receiver, /var/lib/jjflex-receiver, /etc/ssl/cloudflare
> - Services enabled/started: jjflex-receiver
> - nginx reloaded
>
> **Recommended verification by Noel (Tailscale-side):**
> 1. `curl -X POST -F 'file=@small.zip' https://crashes.jjflexible.radio/feedback` from your machine — confirms public reachability through Cloudflare.
> 2. `ssh ner@rarbox.macaw-jazz.ts.net "ls /var/lib/jjflex-receiver/$(date +%Y-%m-%d)/"` — confirms the bundle landed.
> 3. `ssh ner@rarbox.macaw-jazz.ts.net "sudo journalctl -u jjflex-receiver --since '1 hour ago' --no-pager"` — read recent receiver logs.

---

## Cross-references

- `phase-0-runbook.md` — the human-oriented Phase 0 (Sections A-G); this Claude-executable runbook implements F + G specifically
- `memory/project_claude_as_rarbox_operator.md` — execution-model captured 2026-05-05; this runbook is the first trial
- `memory/project_crash_triage_bundle_flow.md` — bundle format design pattern; informs the FastAPI app's payload schema
- `memory/project_no_silent_phone_home.md` — operational constraint: no telemetry, no phone-home; the receiver is user-initiated upload only
- `memory/project_rarbox_hardening.md` — current rarbox config baseline (UFW, account model, etc.)

## Notes for future iterations

- **Cloudflare-side automation** (Sections A-D-E of phase-0-runbook.md) could become Claude-executable via the Cloudflare API + `wrangler` CLI in a future iteration. Requires Cloudflare API token to be provisioned; tonight's runbook deliberately stops at the SSH boundary.
- **Triage UX surface** (per `project_crash_triage_bundle_flow.md`'s end-section) is a separate future runbook. Lives downstream of this receiver setup.
- **Updater chain support** (Phase 1, hosting at data.jjflexible.radio) is independent of this runbook — it lives on Cloudflare R2, not rarbox.
