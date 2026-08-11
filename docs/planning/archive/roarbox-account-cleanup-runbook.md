# Roarbox Account Cleanup Runbook

**Audience:** A Claude Code session running on roarbox (Linux host `bh`, public IP `209.124.253.203`), bootstrapped under the `noel` account with NOPASSWD sudo already configured. Noel is on his Windows machine and will assist with verification steps that require a second SSH session.

**Goal:** Bring roarbox's user accounts to the desired end state:

- NOPASSWD sudo for `cpolk`, `patrick` (later `borris`), and `ner` once created
- Doug — Noel's call: NOPASSWD, password-required sudo, or non-sudoer
- `ner` account exists with Noel's SSH keys and NOPASSWD sudo
- `noel` deleted (after `ner` is verified)
- `patrick` renamed to `borris`

**Trial context:** This is the second concrete instance of the "Claude as box operator" execution model (memory entry `project_claude_as_rarbox_operator.md` captured the first). Noel verifies critical state changes; Claude executes the runbook sections; rollback steps exist for every destructive action.

---

## Prerequisites Claude must verify before starting

```bash
# 1. Confirm host
hostname    # must return: roarbox  (post-rename; was 'bh' before the rename step in roarbox-bootstrap.md)

# 2. Confirm NOPASSWD for noel works
sudo -n true && echo "NOPASSWD OK" || echo "NOPASSWD MISSING — STOP"

# 3. Confirm running as noel
id    # uid=1003(noel) expected

# 4. Confirm JJFlex repo cloned
ls ~/JJFlex-NG/docs/planning/active/roarbox-account-cleanup-runbook.md && echo "repo present" || echo "STOP — repo missing"

# 5. Confirm doug's NOPASSWD decision is made (ask Noel if not already known)
echo "Has Noel told me whether doug gets NOPASSWD? [yes / no / non-sudoer]"
```

If any prereq fails or doug's decision isn't yet known, STOP and ask Noel before proceeding.

---

## How to use this runbook

Each section is independently checkpointable. Stop after any section heading and report state. Each section has:

- **Goal** — what state changes
- **Pre-check** — verify state before changing
- **Action** — the command(s) to run
- **Expected output** — what success looks like
- **Verify** — post-action check
- **Rollback** — what to do if the action fails or needs to be undone

When running, log every command + verbatim output in the conversation transcript. NOT summaries. Future-debugging value comes from the actual output.

**Confirmation gates** are marked with **CONFIRM:** — for those steps, ask Noel before proceeding.

**Destructive operations** are marked with **DESTRUCTIVE:** — these change persistent state.

---

## Section 1 — NOPASSWD for cpolk

**Goal:** cpolk (Chris) can sudo without password prompts.

**Pre-check:**
```bash
ls /etc/sudoers.d/cpolk 2>/dev/null && echo "already exists — skip section" || echo "not yet configured — proceed"
```

**Action (DESTRUCTIVE — modifies sudoers):**
```bash
echo 'cpolk ALL=(ALL) NOPASSWD: ALL' | sudo tee /etc/sudoers.d/cpolk
sudo chmod 440 /etc/sudoers.d/cpolk
sudo visudo -cf /etc/sudoers.d/cpolk
```

**Expected output:** `tee` echoes the line; `chmod` silent; `visudo -cf` reports `parsed OK`.

**Verify:**
```bash
sudo cat /etc/sudoers.d/cpolk
ls -la /etc/sudoers.d/cpolk    # expected: -r--r----- 1 root root
```

**Rollback:** `sudo rm /etc/sudoers.d/cpolk`

---

## Section 2 — NOPASSWD for patrick

**Goal:** patrick can sudo without password prompts. (Patrick will be renamed to `borris` in Section 6; the sudoers file gets renamed to match at that time.)

**Pre-check:**
```bash
ls /etc/sudoers.d/patrick 2>/dev/null && echo "already exists — skip" || echo "not yet — proceed"
```

**Action (DESTRUCTIVE):**
```bash
echo 'patrick ALL=(ALL) NOPASSWD: ALL' | sudo tee /etc/sudoers.d/patrick
sudo chmod 440 /etc/sudoers.d/patrick
sudo visudo -cf /etc/sudoers.d/patrick
```

**Verify:**
```bash
sudo cat /etc/sudoers.d/patrick
```

**Rollback:** `sudo rm /etc/sudoers.d/patrick`

---

## Section 3 — Doug (CONFIRM with Noel before any action)

**Goal:** doug's account is in the state Noel chose.

**CONFIRM with Noel:** what is doug's status?

- **Option A — NOPASSWD sudoer:** Same procedure as cpolk/patrick.
- **Option B — Password-required sudoer (default):** Verify doug is in the `sudo` group; do nothing else.
- **Option C — Non-sudoer:** Remove doug from `sudo` group with `sudo gpasswd -d doug sudo` (only do this if Noel explicitly says).

**Pre-check (regardless of option):**
```bash
groups doug
ls /etc/sudoers.d/ | grep -i doug
```

**Action:** Per Noel's chosen option above. **Skip this section entirely if Noel says "leave doug alone."**

**Verify:**
```bash
sudo -n -u doug sudo -n true 2>&1
# Option A: returns 0 silently
# Option B: prompts for password (which we won't provide; just see the prompt)
# Option C: "doug is not in the sudoers file" or similar
```

**Rollback:** Reverse the action — re-add to sudo group, or remove the sudoers.d file.

---

## Section 4 — Create `ner` account

**Goal:** ner exists, has noel's SSH key, has NOPASSWD sudo, can SSH from Noel's Windows machine.

**Pre-check:**
```bash
id ner 2>&1
# If "no such user," proceed.
# If ner already exists, CONFIRM with Noel before proceeding — investigate state first.
```

**Action (DESTRUCTIVE — creates user + writes sudoers):**
```bash
# Create user with home dir, default shell, in sudo group
sudo useradd -m -s /bin/bash -G sudo ner

# Copy noel's authorized_keys to ner
sudo mkdir -p /home/ner/.ssh
sudo cp /home/noel/.ssh/authorized_keys /home/ner/.ssh/authorized_keys
sudo chown -R ner:ner /home/ner/.ssh
sudo chmod 700 /home/ner/.ssh
sudo chmod 600 /home/ner/.ssh/authorized_keys

# NOPASSWD for ner
echo 'ner ALL=(ALL) NOPASSWD: ALL' | sudo tee /etc/sudoers.d/ner
sudo chmod 440 /etc/sudoers.d/ner
sudo visudo -cf /etc/sudoers.d/ner
```

**Verify (locally on bh):**
```bash
id ner
sudo ls -la /home/ner/.ssh/
sudo cat /etc/sudoers.d/ner
sudo cat /home/ner/.ssh/authorized_keys | head -3   # confirm key copied (first 3 lines should match noel's)
```

**CONFIRM with Noel — verification from a SECOND terminal:**

Tell Noel:

> Please open a second SSH session from your Windows machine and run:
>
> ```
> ssh ner@209.124.253.203 "id && sudo whoami"
> ```
>
> Expected output: a line showing `uid=...(ner)` and `root` (no password prompt). Tell me when verified — until I hear "ner SSH and sudo verified," I won't proceed to deleting noel.

**STOP and wait for Noel's verification.** Section 5 is gated on this confirmation.

**Rollback:**
```bash
sudo userdel -r ner
sudo rm /etc/sudoers.d/ner
```

---

## Section 5 — Delete `noel` (DESTRUCTIVE; CONFIRM with Noel)

**Pre-check (must all return OK before proceeding):**
```bash
# 1. Confirm ner verification passed (Noel reported success in Section 4)
echo "Has Noel confirmed ner SSH+sudo works from Windows? [yes/no]"

# 2. Confirm noel is not currently logged in
who | grep '^noel ' && echo "STOP — noel still logged in; ask Noel to log out all noel sessions" || echo "noel offline OK"

# 3. Confirm THIS Claude session is no longer running as noel
id | grep -q "uid=.*(noel)" && echo "STOP — I'm running as noel; this session must restart as ner first" || echo "session not running as noel OK"
```

If this Claude session is running as noel, the cleanest path is: STOP, tell Noel to start a new Claude session under ner (`ssh ner@... && claude` from his Windows machine), and that new session continues from this point.

**Inventory check before deletion:**
```bash
sudo find / -user noel -not -path '/proc/*' -not -path '/sys/*' 2>/dev/null > /tmp/noel-files-pre-delete.txt
wc -l /tmp/noel-files-pre-delete.txt
head -50 /tmp/noel-files-pre-delete.txt
```

If anything outside `/home/noel` shows up that looks important (configs, scripts, data), **CONFIRM with Noel** before deletion — these would become orphaned. Common acceptable-to-delete things: `/var/mail/noel`, `/var/spool/cron/crontabs/noel` (if empty), files in `/tmp/`. Anything else needs explicit Noel approval.

**Action (DESTRUCTIVE — removes user + home dir + mail spool):**
```bash
sudo userdel -r noel
sudo rm -f /etc/sudoers.d/noel
```

**Verify:**
```bash
id noel 2>&1 | grep -q "no such user" && echo "user deleted OK"
ls /home/ | grep -q '^noel$' && echo "STOP — home dir still present" || echo "home dir removed OK"
sudo grep -r '\bnoel\b' /etc/sudoers /etc/sudoers.d/ 2>&1 | grep -v "No such file"
# expected: no matches
```

**Rollback:** Difficult — home directory contents are gone. If urgent, recreate the account: `sudo useradd -m -G sudo noel && echo 'noel ALL=(ALL) NOPASSWD: ALL' | sudo tee /etc/sudoers.d/noel`. You'd still need to copy authorized_keys from `/home/ner/.ssh/authorized_keys` to `/home/noel/.ssh/`. But the original noel home contents won't come back.

---

## Section 6 — Rename `patrick` → `borris` (CONFIRM with Noel)

**Goal:** patrick's account is renamed to borris. UID, files, group, mail spool, and sudoers entry all migrate.

**Pre-check (all must pass):**
```bash
# 1. patrick must not be logged in
who | grep '^patrick ' && echo "STOP — patrick logged in; ask Noel to schedule a reboot or wait for patrick to log off" || echo "patrick offline OK"

# 2. patrick must have no running processes
ps -u patrick > /tmp/patrick-procs.txt
test -s /tmp/patrick-procs.txt && echo "STOP — patrick has processes:" && cat /tmp/patrick-procs.txt || echo "no patrick processes OK"

# 3. CONFIRM with Noel that Patrick has agreed to the rename
echo "Has Noel confirmed Patrick agreed to be renamed to borris? [yes/no]"
```

If patrick is logged in, options:
- Wait for him to log off
- Ask Noel to schedule a reboot (`sudo shutdown -r +5 "Reboot in 5 min for account maintenance"`)
- Ask Noel to forcibly close the session (`sudo pkill -KILL -u patrick`) — last resort, less polite

**Action (DESTRUCTIVE — renames user, moves home dir):**
```bash
# Rename login name
sudo usermod -l borris patrick

# Move home directory and update internal refs (NOTE: refers to user by NEW name now)
sudo usermod -d /home/borris -m borris

# Rename primary group (assuming it's named 'patrick' to match)
sudo groupmod -n borris patrick

# Move mail spool
sudo mv /var/mail/patrick /var/mail/borris 2>/dev/null || true

# Rename sudoers file
sudo mv /etc/sudoers.d/patrick /etc/sudoers.d/borris 2>/dev/null || true
# Edit the content to change the username inside the file
sudo sed -i 's/^patrick /borris /' /etc/sudoers.d/borris
sudo visudo -cf /etc/sudoers.d/borris
```

**Verify:**
```bash
id borris
ls -la /home/borris/
ls /home/ | grep -q '^patrick$' && echo "STOP — old home dir still present" || echo "home dir renamed OK"
sudo cat /etc/sudoers.d/borris
sudo grep -r '\bpatrick\b' /etc/sudoers /etc/sudoers.d/ /etc/cron* 2>&1 | grep -v "No such file"
# expected: no matches
```

**CONFIRM with Noel — verification of borris's authentication:**

Patrick's SSH authorized_keys came along with `usermod -m`, so SSH should still work. But ask Noel: "Does Patrick need to be told his new login name and asked to test SSH? If yes, can you tell him: `ssh borris@209.124.253.203`?"

**Rollback:**
```bash
sudo usermod -l patrick borris
sudo usermod -d /home/patrick -m patrick
sudo groupmod -n patrick borris
sudo mv /var/mail/borris /var/mail/patrick 2>/dev/null
sudo mv /etc/sudoers.d/borris /etc/sudoers.d/patrick 2>/dev/null
sudo sed -i 's/^borris /patrick /' /etc/sudoers.d/patrick
```

---

## Section 7 — Final state verification

**Goal:** Confirm the desired end state across all changes.

```bash
echo "=== HOME DIRECTORIES ==="
ls -la /home/
# expected: borris, cpolk, doug, ner (no noel, no patrick)

echo "=== SUDOERS FILES ==="
ls /etc/sudoers.d/
# expected: README, borris, cpolk, ner (and doug if Section 3 chose Option A)
# expected: NO 'noel' or 'patrick' files

echo "=== NOPASSWD SANITY ==="
for u in cpolk borris ner; do
  echo "--- $u ---"
  sudo -n -u $u sudo -n true 2>&1 && echo "$u NOPASSWD OK" || echo "$u NOPASSWD FAILED"
done

echo "=== ACTIVE SESSIONS ==="
who
# expected: ner (you), maybe doug if he's been around

echo "=== ORPHANED REFERENCES ==="
sudo grep -r '\b\(noel\|patrick\)\b' /etc/sudoers /etc/sudoers.d/ /etc/cron* 2>&1 | grep -v "No such file"
# expected: no matches
```

If anything looks wrong, STOP and report the discrepancy.

---

## Report-back template for the orchestrator session (Noel's Windows Claude)

When all sections pass, send this back to Noel:

> **Roarbox account cleanup complete.**
>
> - Section 1 — cpolk has NOPASSWD sudo
> - Section 2 — patrick had NOPASSWD set (entry now renamed to borris in Section 6)
> - Section 3 — doug: [NOPASSWD set / left as standard sudoer / removed from sudoers]
> - Section 4 — ner created with your SSH key and NOPASSWD; verified working from your Windows machine
> - Section 5 — noel deleted; home dir + mail spool removed; sudoers entry removed; no orphaned references
> - Section 6 — patrick renamed to borris; home dir at /home/borris; group renamed; mail moved; sudoers entry renamed and updated
> - Section 7 — final state verified: /home contains borris, cpolk, doug, ner
>
> **State changed on roarbox:**
> - Files written: /etc/sudoers.d/{cpolk,borris,ner} (+ doug if Option A)
> - Files removed: /etc/sudoers.d/{noel,patrick}
> - User accounts deleted: noel
> - User accounts created: ner
> - User accounts renamed: patrick → borris
>
> **Recommended verification by Noel from Windows:**
> 1. `ssh ner@209.124.253.203 "id && sudo whoami"` — confirms ner key auth + NOPASSWD
> 2. `ssh noel@209.124.253.203` — should fail (account deleted)
> 3. Tell Patrick to test `ssh borris@209.124.253.203`

---

## Cross-references

- `~/JJFlex-NG/docs/planning/active/roarbox-bootstrap.md` — the bootstrap doc Noel followed to install Claude here
- `~/JJFlex-NG/docs/planning/active/rarbox-bootstrap.md` — parallel bootstrap doc for rarbox
- `~/JJFlex-NG/docs/planning/active/rarbox-setup-runbook-for-claude.md` — receiver-setup runbook (analogous structure)
- `memory/project_roarbox_vs_rarbox.md` (in Noel's Claude memory on Windows) — the two-boxes distinction

---

## Notes for future iterations

- **Future user adds/renames** should follow this same pattern: NOPASSWD setup first, key copy, verification gate from a separate terminal, then state change.
- **Doug's eventual rename** (if any) follows Section 6's pattern. If Doug ever leaves Chris's office, the cleanup is `userdel -r doug` plus sudoers cleanup.
- **The `usermod -l` / `-d -m` rename pattern** is preferable to create-and-delete when feasible because it preserves UID, file ownership, and dotfiles. Create-and-delete (Section 4 + 5 for noel→ner) is only used here because Claude itself is currently logged in as noel and can't rename its own running account.
