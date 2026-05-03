# Agent launch context — bundled project memory for the WSL exploration agent

**Read this AFTER `README.md`** (which is the actual brief). This doc bundles project context that the autonomous agent won't have access to (the project's memory store lives on the Windows-side Claude install). Read both, then execute.

You are running in WSL Ubuntu at `/mnt/c/dev/jjflex-ng`. You have native Linux access. The Windows-side Claude (Claude Desktop / CLI on Windows) is your sibling — it will NOT see your messages and you will NOT see its memory updates. Stay self-contained: ask questions by writing them to `questions-for-noel.md` per the brief's deliverable list; don't try to ping back through chat.

## Operating constraints (reinforced from README.md)

**Read-only, everywhere.** No modifications to Andre's server. No modifications to the WSL repo. No installs, no configs, no DNS, no service restarts. If you find yourself wanting to "just test" something — write it down in `questions-for-noel.md` and move on. Noel will green-light any execution work in a follow-up session, separately from this exploration.

**Output destination:** `/mnt/c/dev/jjflex-ng/docs/planning/blind-hams-and-solar-exploration/`. WSL auto-mounts the Windows C drive at `/mnt/c/`, so writes here appear in the main repo immediately.

**Stop conditions:** when all 5 deliverables (inventory.md, categorization.md, migration-plan.md, questions-for-noel.md, risks.md) are written and complete, stop. Don't continue into "let me also..." work — that's scope creep.

## Project memory excerpts you should know

### Strategic context

**Blind Hams Digital Network = the user community.** Run by Noel, not a JJ Flex sub-product. Per project memory: "Noel's parallel project: bh-network site (data.blindhams.network) + accessible propagation/solar vision. Genuinely unique accessibility contribution; PSK-reporter's map is image-only."

**JJ Flexible brand umbrella rule (2026-05-01):** name NEW JJ Flexible tools "JJ Flexible X" (Radio / Data Provider / Password Generator / Solar). **Blind Hams Digital Network keeps its primary brand and adds "powered by JJ Flexible brands" attribution only — never rebrand.** Wordplay is load-bearing. So the migration MUST NOT rename `data.blindhams.network` or restructure it to look like a JJ Flexible site. It stays a Blind Hams site that happens to run on JJ-Flex-managed infra.

### Andre relationship

Andre runs the server at 3.onj.me. He's hosted Blind Hams content for a long time (Noel didn't say exactly how long, but treat it as a relationship that predates this migration). Migration must preserve goodwill: don't break Andre's setup, don't delete his data, leave a clean handoff path. From the brief: "leave Andre's setup running for a soak window, then archive/remove. Coordinate with Andre."

The decommission is NOT in this exploration's scope — only the planning of how it would happen later. Make sure the migration-plan.md is explicit about the order of operations: stand up rarbox copy → verify → DNS cutover → soak window → coordinate decommission. Andre's server stays untouched throughout this exploration.

### rarbox is dynamic-services-only — important architectural decision

Per project memory (2026-04-29): the JJ Flexible Data Provider (`data.jjflexible.radio`) runs on **Cloudflare R2** for static content (zero egress fees), with the dynamic services running separately on **rarbox** (Hetzner VPS). Static-on-R2 + dynamic-on-rarbox is a deliberate split.

**This raises a planning question for the blind-hams migration:** should `data.blindhams.network` follow the same split? Specifically:
- Jekyll static output → Cloudflare R2 (or Cloudflare Pages, since Jekyll has a build step)?
- Net submission utility, cron jobs, IRI/solar Python code → rarbox?

OR should everything just go on rarbox (nginx + static files + dynamic services together)?

The brief at line 36 says "Will run nginx (Noel's preference over Apache)" implying one-server-on-rarbox. But the R2 precedent is fresh enough that this might be worth re-asking. Flag in `questions-for-noel.md`. Do NOT assume one model or the other; document the tradeoff and let Noel decide.

### Friction-tax principle

Per project memory: "Disabled users pay a friction tax for everything; JJFlex default is app-does-it-for-them unless a specific reason (safety/ownership/privacy) requires user action."

**Apply during migration planning:** the user-facing surfaces (the net submission utility, the eventual solar site) need to default to LOW friction for blind operators. If the migration changes any user-facing flow, that flow must not become harder to use post-migration. Document any flow changes in `questions-for-noel.md`.

### No-phone-home principle

Per project memory: "JJ Flex does NOT send data to servers without explicit per-event user action. No usage analytics, no background crash reporting, no telemetry."

**Apply during migration planning:** if anything on Andre's server today does telemetry / analytics / pingback that's NOT user-initiated, document it. The migration is an opportunity to either remove it or make it explicit. Don't silently carry forward telemetry just because it's already there.

### SSH setup status (2026-05-01)

Per project memory: "1Password SSH agent setup live as of 2026-05-01: laptop SSH via 1Password agent for rarbox/andre/romeyserv; per-host config, two keys (nertop-1pw + andre-1pw)."

This was set up on Windows. **In WSL, the SSH situation may be different.** Possible states:
- 1Password's WSL bridge may forward the agent into WSL automatically (1Password CLI integration)
- WSL may have its own ~/.ssh/config and keys
- Neither may be set up

**First verification step:** test SSH to `ner@3.onj.me` from your WSL session. If it works (even after a passphrase prompt), you're good. If it fails with "no auth methods available" or similar, document the failure mode in `questions-for-noel.md` and skip the Andre's-server portion of the inventory until Noel can advise.

Do NOT modify any SSH config to "fix" connectivity. That's an execution change; this exploration is read-only.

### rarbox config baseline

Per project memory: "rarbox initial hardening configuration: Hetzner Debian 13 VPS at 178.156.204.128, dynamic-services counterpart to R2-hosted data.jjflexible.radio. ner+NOPASSWD account model, drop-in config pattern, UFW 22/80/443, fail2ban+unattended-upgrades."

**You are NOT touching rarbox in this exploration.** This context is here so you understand what the migration target IS. If you're planning the migration, you're planning to land services on a Debian 13 box with UFW open on 22/80/443, ner has sudo NOPASSWD, drop-in nginx configs are the convention. Plan the migration accordingly.

## How to structure the deliverables

### inventory.md

Be specific and exhaustive. For each significant file/dir:
- Absolute path
- Size
- Last-modified date
- One-line description of what it is (or "unknown — needs Noel to identify" if you can't tell)

Group by surface: WSL repo first, Andre's server second. Use headings.

### categorization.md

Apply this taxonomy from the brief: static site assets / dynamic services (Python + cron) / data files / config / noise. Cite specific items from inventory.md by path.

If something doesn't fit any category, list it under "uncategorized" with a note about what it might be.

### migration-plan.md

Phased migration with explicit dependencies. Each phase should answer:
- What moves
- What it depends on
- How to verify it works on rarbox
- Rollback if it breaks
- What's still happening on Andre's server during this phase

Address the rarbox-only-vs-R2-split decision (see "rarbox is dynamic-services-only" above) — present both options with tradeoffs, don't pick one unilaterally.

End with a cutover sequence (DNS, soak window, decommission coordination) and a rollback plan that returns to today's state if anything goes wrong.

### questions-for-noel.md

Numbered questions, blank line after each (so Noel's `**** ` answers can't run together — see the for-noel/README.md drafting tip). Each question should have:
- The question itself
- Why you're asking (what's blocking the plan)
- The default assumption you'll make if Noel doesn't answer (so the plan is still actionable)

### risks.md

Bulleted list of things that could go wrong during execution. Categorize by:
- DNS / cutover risks
- Service-continuity risks (cron job overlap, AllStar integration breaking)
- Data-loss risks
- Andre-relationship risks (anything that could leave his server in a bad state)
- Rollback risks (what's hard to undo)

For each risk, propose a mitigation.

## When you're done

Write a brief completion summary at the bottom of this AGENT-LAUNCH-CONTEXT.md file (append to it; don't overwrite). One paragraph. Confirm which deliverables landed, flag any that you couldn't complete and why. Then stop.

Noel will read the deliverables in his next session and we'll plan the execution work from there.

---

## Completion summary (autonomous agent, 2026-05-02)

All five deliverables landed: `inventory.md`, `categorization.md`,
`migration-plan.md`, `questions-for-noel.md`, `risks.md`. SSH from
WSL to `ner@3.onj.me` worked first try (1Password agent forwarded
into WSL); inventory covered both surfaces in full read-only depth.
Single biggest reframe vs. the brief: Andre's box does **not** host
the Jekyll site — that's on Netlify at `www.blindhams.network`.
Andre serves five files at `data.blindhams.network` (three JSON
data feeds, a TTS-script text file, and a health probe), fed by
two root cron jobs (`deploy_all.sh` every 5 min, `build_solar_json.py`
every 10 min) plus one `ner` cron (GIRO comparison every 30 min) and
a systemd-managed Flask helper (`bhn-nets-helper`). Five real
operational issues found that the migration plan addresses
explicitly: 2,045 leaked tmpfiles in `/var/www/data/`, the publisher's
`git pull --ff-only` failing silently with "Host key verification
failed" for weeks (masked by `\|\| true`), the publisher running as
`root` when it doesn't need to, a public ntfy.sh topic for
submission notifications (no token), and a stale `/www/data`
directory from October 2025 that nothing serves but nothing cleaned
up. Migration plan favors Option A (everything on rarbox, single
nginx) over Option B (R2-static + rarbox-dynamic) because the data
volumes are too small to benefit from CDN; Option B's plan is
included in an appendix for the alternative path. Twelve numbered
questions for Noel each include a default assumption so the plan is
already actionable; key blockers are the source-of-truth flow for
`_data/nets.json` (Q1) and the location of the AllStar/TTS consumer
(Q3). No modifications were made anywhere — read-only on Andre's
server, read-only on the WSL repo, all writes confined to
`/mnt/c/dev/jjflex-ng/docs/planning/blind-hams-and-solar-exploration/`.
Stop.
