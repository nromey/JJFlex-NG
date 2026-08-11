# Roarbox: Noel's Bootstrap for Claude Code Install

**Last updated:** 2026-05-06
**Audience:** Noel, executing from his Windows machine via SSH
**Goal:** Set up minimum NOPASSWD for `noel`, install Claude Code on roarbox, then hand off to the on-box Claude session to execute the account cleanup runbook.

---

## Roarbox state today

| Aspect | Value |
|---|---|
| Linux hostname | `bh` |
| Public IP | `209.124.253.203` |
| Distro | Debian 13 (trixie) |
| Chassis | Dell PowerEdge R620, 1× E5-2620 v2 (CPU upgrade pending separately) |
| Multi-tenant | `noel` (you), `cpolk` (Chris Polk), `patrick`, `doug` |
| SSH key auth | Configured for `noel@bh` via 1Password agent on Windows |
| **NOPASSWD sudo** | **Not configured yet** — sudo prompts for password |
| Claude Code | **Not installed yet** |
| Tailscale | Not yet up (in progress) |

---

## Prerequisites

1. SSH from Noel's Windows reaches roarbox: `ssh -o BatchMode=yes noel@209.124.253.203 "hostname && uptime"` returns "bh" + uptime.
2. Patrick is comfortable having his account renamed to `borris` (verify before running cleanup).
3. Decision made on Doug's NOPASSWD status (yes / no / leave non-sudoer entirely).

---

## Bootstrap steps (run as `noel`)

### 0. Rename the Linux hostname from `bh` to `roarbox`

Do this **before installing Tailscale** so the tailnet node name registers as `roarbox` from day one. No reboot or service restart needed.

```bash
ssh noel@209.124.253.203
sudo hostnamectl set-hostname roarbox
# A temporary "host lookup failure" from sudo here is normal during the rename —
# sudo's hostname cache briefly goes stale; entering your password lets it through.

# Replace the entire 127.0.1.1 line with a canonical roarbox entry.
# (Anchored whole-line replace, NOT a substring sub — substring subs leave
# half-renamed strings like 'roarbox.localbh' if the original FQDN contained 'bh'.)
sudo sed -i 's/^127\.0\.1\.1\s.*/127.0.1.1\troarbox/' /etc/hosts

# Verify /etc/hosts is correct:
grep ^127.0 /etc/hosts
# expected:
#   127.0.0.1   localhost
#   127.0.1.1   roarbox

# If no 127.0.1.1 line exists at all, add one:
# echo -e '127.0.1.1\troarbox' | sudo tee -a /etc/hosts

# Re-login so the shell prompt picks up the new hostname:
exit
ssh noel@209.124.253.203
hostname     # expected: roarbox
```

After this point, all references to `bh` in this doc and the cleanup runbook should be read as `roarbox`. Existing memory entries and prior conversation history will gradually catch up as you work.

> **If you preserved an FQDN with a `.localdomain` (or similar) suffix:** use this instead of the simple-form sed above:
>
> ```bash
> sudo sed -i 's/^127\.0\.1\.1\s.*/127.0.1.1\troarbox.localdomain\troarbox/' /etc/hosts
> ```
>
> For roarbox, the simple form is fine — Tailscale gives you a real FQDN at `roarbox.macaw-jazz.ts.net` once it's installed, and the local-only FQDN doesn't matter for anything that's running on this box.

### 1. Set NOPASSWD for `noel` (one password prompt, then never again)

SSH in interactively:

```bash
ssh noel@209.124.253.203
```

Then on the box:

```bash
sudo visudo -f /etc/sudoers.d/noel
```

Add this single line, save, exit:

```
noel ALL=(ALL) NOPASSWD: ALL
```

`visudo` validates syntax before saving — if you mistype, it refuses to write the file. That keeps you from breaking sudo with a typo.

### 2. Verify NOPASSWD works

In the same SSH session:

```bash
sudo -n whoami
# expected: root  (no password prompt)
```

### 3. Install Tailscale

Use Tailscale's official installer — adds the apt repo and installs the package in one shot. Idempotent; safe to re-run if you already have it.

```bash
curl -fsSL https://tailscale.com/install.sh | sh
tailscale --version
```

### 4. Bring Tailscale up and authenticate

```bash
sudo tailscale up
```

This prints a URL — open it in your browser on your Windows machine and approve the device. Because you renamed the hostname in Step 0, the tailnet node registers as `roarbox` (or `roarbox-1` if that name is already taken in your tailnet, in which case you can rename it after-the-fact in the Tailscale admin UI).

After approval, on the box:

```bash
tailscale status
ip -br addr | grep tailscale0
# expected: tailscale0 interface with a 100.x.y.z IP
```

### 5. Verify Tailscale connectivity from your Windows machine

From a separate terminal on your Windows machine:

```bash
ssh noel@roarbox.macaw-jazz.ts.net "hostname && tailscale status | head -3"
```

Should return `roarbox` and the tailnet status. After this works, use `roarbox.macaw-jazz.ts.net` instead of the public IP for all future SSH — the public IP becomes a fallback rather than the primary path.

### 6. Install useful auxiliary tools

These aren't strictly required by Claude Code (the native installer in Step 8 is self-contained), but git + gh are useful for repo cloning and GitHub operations the on-box Claude will want to do:

```bash
sudo apt update
sudo apt install -y git gh curl
```

### 7. (Optional) Node.js if you want it for other things

The Claude Code native installer doesn't require Node — skip this step unless you want Node available for other tools. If you do:

```bash
sudo apt install -y nodejs npm
node --version    # if older than 18, install via NodeSource:
# curl -fsSL https://deb.nodesource.com/setup_22.x | sudo bash && sudo apt install -y nodejs
```

### 8. Install Claude Code via the native installer

**Verify the current install command at `https://docs.claude.com/en/docs/claude-code`** — this is the canonical source and the exact command may evolve.

The native installer is the recommended path; it bundles its own runtime and update channel, so no Node required and no `npm update` chasing later.

Approximately (verify against docs page first):

```bash
curl -fsSL https://claude.ai/install.sh | bash
# OR (if claude.com is the current home):
# curl -fsSL https://claude.com/install.sh | bash
claude --version
```

The npm path (`sudo npm install -g @anthropic-ai/claude-code`) still works and is the right fallback if the native installer can't run on this host for any reason — but try native first.

### 9. Authenticate Claude

```bash
claude
# Follow login flow; uses your Anthropic account credentials
```

### 10. Clone the JJFlex repo so Claude can read the cleanup runbook

```bash
git clone https://github.com/nromey/JJFlex-NG.git ~/JJFlex-NG
```

---

## Handoff to on-box Claude

Once Claude is authenticated and the repo is cloned, paste this starter prompt into the on-box Claude session:

> You're now running on roarbox (host `bh`, public IP `209.124.253.203`, Debian 13, Dell R620). I'm Noel — your NOPASSWD sudo is already configured under the `noel` account, so you can run sudo commands without prompts.
>
> Current users on this box: `noel` (me), `cpolk` (Chris Polk), `patrick`, `doug`. Roarbox is multi-tenant — Chris hosts it for me at his office and the others are colleagues of his sharing the box.
>
> Your first task is the account cleanup runbook at `~/JJFlex-NG/docs/planning/active/roarbox-account-cleanup-runbook.md`. The goal is to set NOPASSWD for everyone who needs it, create `ner` with my SSH key, verify ner works from a separate session, delete `noel` (me, the account you're currently running as), and rename `patrick` → `borris`.
>
> Read the runbook end-to-end first. Verify the prereq checklist at the top. If any prereq fails, STOP and report. If they pass, propose the first concrete step and wait for my green light before executing.
>
> Important: when the runbook calls for verification "from a SECOND terminal," I'll be the one running that — tell me when to do it and what command to run.

---

## Done

After the cleanup runbook completes, this bootstrap doc has served its purpose. Future roarbox ops happen in the on-box Claude session.
