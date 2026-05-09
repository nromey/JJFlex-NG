# Rarbox: Noel's Bootstrap for Claude Code Install

**Last updated:** 2026-05-06
**Audience:** Noel, executing from his Windows machine via SSH
**Goal:** Install Claude Code on rarbox (`ner@rarbox.macaw-jazz.ts.net`) and hand off to the on-box Claude session for ongoing receiver-setup work.

---

## Why a bootstrap step at all

Rarbox is already in good shape:

- `ner` account exists with **NOPASSWD sudo** already configured
- SSH key auth works from Noel's Windows machine (1Password agent provides the key)
- Tailscale is up at `rarbox.macaw-jazz.ts.net`
- Public IP `178.156.204.128`
- Phase 0 setup runbook lives at `docs/planning/active/rarbox-setup-runbook-for-claude.md` waiting for the on-box Claude to execute

But Claude Code isn't installed there yet. This bootstrap installs git, gh, Node.js, and Claude Code so the on-box session can take over.

---

## Prerequisites

1. SSH reaches rarbox: `ssh ner@rarbox.macaw-jazz.ts.net "hostname && uptime"` returns successfully.
2. ner has NOPASSWD: `ssh ner@rarbox.macaw-jazz.ts.net "sudo -n true && echo OK"` returns "OK".

If either fails, stop and fix before continuing.

---

## Bootstrap steps

### 1. Install useful auxiliary tools

Not strictly required by Claude Code itself (the native installer in Step 3 is self-contained), but git + gh are useful for repo cloning and GitHub operations the on-box Claude will want:

```bash
ssh ner@rarbox.macaw-jazz.ts.net "sudo apt update && sudo apt install -y git gh curl"
```

### 2. (Optional) Node.js if you want it for other tools

Skip unless you want Node available for tools beyond Claude. Claude Code's native installer doesn't require it.

```bash
ssh ner@rarbox.macaw-jazz.ts.net "sudo apt install -y nodejs npm && node --version"
# If older than 18 and you actually want a current Node:
# ssh ner@rarbox.macaw-jazz.ts.net "curl -fsSL https://deb.nodesource.com/setup_22.x | sudo bash && sudo apt install -y nodejs"
```

### 3. Install Claude Code via the native installer

**Verify the current install command at `https://docs.claude.com/en/docs/claude-code`** — this is the canonical source and the exact command may evolve.

The native installer is the recommended path; it bundles its own runtime and update channel, so no Node required and no `npm update` chasing later.

Approximately (verify against docs page first):

```bash
ssh ner@rarbox.macaw-jazz.ts.net "curl -fsSL https://claude.ai/install.sh | bash"
# OR (if claude.com is the current home):
# ssh ner@rarbox.macaw-jazz.ts.net "curl -fsSL https://claude.com/install.sh | bash"
```

The npm path (`sudo npm install -g @anthropic-ai/claude-code`) still works and is the right fallback if the native installer can't run on this host for any reason.

### 4. Verify install

```bash
ssh ner@rarbox.macaw-jazz.ts.net "claude --version"
```

### 5. Authenticate

SSH in interactively (Claude's first-run login flow needs a TTY):

```bash
ssh ner@rarbox.macaw-jazz.ts.net
# In the SSH session:
claude
# Follow login prompts; uses your existing Anthropic account
```

---

## Handoff to on-box Claude

Once Claude is authenticated on rarbox, paste this starter prompt into the new session:

> You're now running on rarbox (`ner@rarbox.macaw-jazz.ts.net`, public IP `178.156.204.128`, Debian, Tailscale up). NOPASSWD sudo is configured for ner. Your purpose is to handle the FastAPI crash receiver setup at `crashes.jjflexible.radio` and ongoing rarbox ops.
>
> First task: clone the JJFlex repo to `~/JJFlex-NG/`:
> ```
> git clone https://github.com/nromey/JJFlex-NG.git ~/JJFlex-NG
> ```
> Then read `~/JJFlex-NG/docs/planning/active/rarbox-setup-runbook-for-claude.md` end-to-end. That's your operating runbook — Phase 0 Sections F + G.
>
> Verify the prereq checklist at the top of that runbook. If any prereq fails, STOP and report. If they all pass, propose the first concrete step you'll take and wait for my green light before executing.
>
> Reminder: every section in that runbook has CONFIRM gates for irreversible-or-impactful steps. Don't skip them.

---

## Done

After Claude takes over on rarbox, this bootstrap doc has served its purpose. Future rarbox work happens in the on-box Claude session.
