# Blind Hams + Solar — Exploration & Migration Plan

**Status:** Brief drafted 2026-05-01. Not yet executed.
**Goal:** Survey what exists across WSL + Andre's server (3.onj.me), produce a plan to safely migrate the dynamic pieces to rarbox, then cut DNS over.

---

## Context — what "blind hams + solar" means

The Blind Hams Digital Network is a community of blind/visually impaired ham radio operators that Noel is closely involved with. Noel manages all nets on the Blind Hams Digital Network through the week and uses blindhams.network for that purpose.  It currently has two surfaces:

- **Blind Hams site** — Jekyll static site at `data.blindhams.network` (currently DNS-pointed at Andre's server `3.onj.me`). Lists ham radio nets, schedule data, net operators. Includes a community-contributed-net submission utility (users add, Noel approves). Backed by JSON data files; a cron job pulls 10-week schedule data for net operators and net schedules in general. Repo: `nromey/bh-network` on GitHub. Built with Netlify-style tooling + Jekyll.

- **Solar (long-standing vision, partly built)** — Python scripts that compute solar/MUF data using IRI (a Fortran package wrapped in Python). One script generates a solar report from live data which an ElevenLabs voice reads aloud on AllStar. The community's overwhelming ask is a **solar website that gives location-specific propagation data** based on bisected-earth grid (currently 100-mile squares; will tighten when Noel's 40-core build is online). Plans to develop a propagation algorithm describing current band-by-band propagation by location — not built yet, design phase. PSK-reporter has a propagation map but it's image-based and not screen-reader-accessible (the gap this project fills).

Why this matters: blind ham operators need accessible solar/propagation data the same way sighted operators do, but visual maps don't work for them. The site + the audio reader + the future location-aware tool together fill an accessibility gap that nothing else in ham radio does.

## Current state — where stuff lives

### On Noel's WSL Ubuntu (laptop)
- A local clone of `nromey/bh-network` (GitHub). Contains the Jekyll site source, Netlify-style build config, and the utility code.
- Probably: development scripts, cached IRI/solar code, possibly venv directories. Also has a good bit of javascript code that runs net display and management tasks.
- Username on WSL: `ner`.

### On Andre's server (3.onj.me, accessed as `ner`, SSH set up 2026-05-01)
- `/www/data` — the data directory the Jekyll site pulls from. Contains the JSON data files (nets list, schedule, etc.) populated by the cron job.
- Apache web server (Andre's existing setup) serves the Jekyll output at `data.blindhams.network`.
- Solar Python scripts also live somewhere on Andre's box — Noel says "in my main directory" but isn't sure of the exact location; **a general survey is needed because things are a bit all over the place**.
- Cron job(s) running for schedule pulls and possibly solar data refreshes.
- An ElevenLabs-voiced solar report script that AllStar consumes.

### DNS
- `data.blindhams.network` → currently resolves to 3.onj.me / Andre's server.
- Migration target: same name, eventually pointing at rarbox once the dynamic pieces are running there.
- `www.blindhams.network` resolves to netlify. That will not change. 
### Target server
- **rarbox** (Hetzner VPS, hardened 2026-04-30, SSH set up 2026-05-01). Currently runs nothing user-facing. Will run nginx (Noel's preference over Apache).

## What "safe migration to rarbox" means

1. **Inventory first** — know what's there before moving anything.
2. **Categorize** — static (Jekyll output), dynamic (Python scripts, cron jobs, submission utility, IRI solar generator), data (JSON, possibly cached solar data).
3. **Mirror to rarbox** — set up nginx + dynamic services on rarbox, copy the data and code. Don't change Andre's setup yet.
4. **Verify on rarbox** — make sure everything works against the rarbox copy before touching DNS.
5. **DNS cutover** — switch `data.blindhams.network` to rarbox.
6. **Decommission gracefully** — leave Andre's setup running for a soak window, then archive/remove. Coordinate with Andre.

The migration is not just "rsync and go" — Noel wants to take the opportunity to clean up ("things were a bit all over the place"), tighten the net submission utility, and lay the foundation for the solar-site vision. Noel had solar data utilities residing outside of the normal bh-netwoprk tree because they were not part of the blindhams network. 

## Deliverables this exploration should produce

The autonomous run (or morning session) should produce these documents in this directory:

1. **`inventory.md`** — what files/dirs/services exist on each surface (WSL + Andre), file sizes, last-modified dates, brief description of each significant item.
2. **`categorization.md`** — group items into: static site assets, dynamic services (Python + cron), data files (JSON + cached solar), config (nginx/Apache + DNS), and noise (logs, caches, abandoned experiments).
3. **`migration-plan.md`** — proposed phased migration: what moves first, what depends on what, how to test each piece on rarbox, the cutover sequence, the rollback plan if something breaks. Explicit about what touches Andre vs only-rarbox.
4. **`questions-for-noel.md`** — anything the agent can't answer from inspection alone (e.g., "is this script still in use?", "did this cron job get retired?", "what does this Python file do?").
5. **`risks.md`** — anything that could go wrong: DNS propagation timing, AllStar integration breaking during cutover, cron job overlap during migration, etc.

## Constraints — what the autonomous agent MUST and MUST NOT do

**MUST:**
- Read-only on Andre's server. List, view, examine — but don't modify.
- Read-only on the WSL repo. Don't change files; if something's interesting, document it.
- Write only to this planning directory (`docs/planning/blind-hams-and-solar-exploration/`).
- Be specific in inventories: file paths, sizes, dates, not just summaries.
- Note anything that suggests the system has external integrations (ElevenLabs API keys, GitHub webhooks, AllStar IRLP nodes, Netlify deploy hooks) — these need careful handling at migration.

**MUST NOT:**
- Run scripts, install packages, modify configs, restart services, push to GitHub, or change DNS. Anywhere. Ever.
- Install anything on rarbox. Migration prep happens after the plan is reviewed.
- Touch any service Andre runs that ISN'T blind hams related (his server may host other things).
- Assume anything is safe to delete. If unsure, document and ask.

## Decision needed from Noel — WSL access path

Two options for how the autonomous agent gets at the WSL repo:

**Option A — Run from PowerShell, shell into WSL via `wsl <command>`**
- No Claude install needed in WSL.
- Agent runs in this Claude Code session, calls `wsl ls /home/ner/bh-network` etc.
- Simpler, fewer moving parts.
- Limitation: every WSL command is one shot; harder to do interactive exploration.
- Fine for "list, read, document" workflow which is what this exploration is.

**Option B — Install Claude Code in WSL**
- Fresh Claude session running natively in Linux.
- Better long-term if Noel wants to continue iterating on bh-network from WSL.
- Required setup: install Node, install `claude` CLI, authenticate.
- Probably worth doing eventually since Jekyll builds and Python scripts are Linux-native; doing them in WSL is the right developer ergonomics.

**Recommendation:** Start with Option A for THIS exploration — it's read-only, one-shot inventorying. Defer the WSL Claude install to a separate session when Noel has time to set it up properly. Doing both in one tired session = mistakes.

## Suggested execution paths

**Path 1: Autonomous overnight (background subagent)**
- Spawn a research subagent NOW with this brief.
- It explores both surfaces, writes the five deliverable documents into this directory.
- Noel reviews in the morning, decides next phase.
- Best fit IF the brief above is detailed enough (Noel: read it and tell me).

**Path 2: Morning interactive session**
- Sleep on it. Tomorrow morning, fresh session, walk through inventory together.
- Slower, more iterative, lower risk of misunderstanding.
- Best fit if there are questions the brief doesn't answer.

**Path 3: Hybrid**
- Spawn the inventory + categorization subagent overnight (the safe, mechanical parts).
- Reserve migration-plan + risks for the morning session where Noel can shape decisions.
- Probably the best fit — separates "look at what's there" (mechanical) from "decide what to do" (judgment).

## Open questions Noel should think about (no rush)

1. Does Andre know this migration is planned? If not, when's the right time to tell him?
2. What's Andre's preference for what stays on his box vs moves entirely?
3. Any data on Andre's box (especially in his main directory) that's NOT blind-hams related and the agent should ignore?
4. ElevenLabs API key — where is it stored currently? Going to need to re-host that on rarbox.
5. AllStar integration — what address does AllStar fetch the audio report from? That URL needs to keep working through the migration.
6. The "tightening up" of the net submission utility — separate session/sprint, or part of this migration?
7. Any existing docs or notes from when the bh-network repo was first set up that would help inventory? Anything in the README of `nromey/bh-network`?

## Next step

Noel reads this. Then tells me:
- "Looks good, run Path 3 (hybrid) overnight" → I spawn the inventory subagent now.
- "Looks good, save for morning" → I touch nothing and we resume tomorrow.
- "Edit X / add Y / strike Z" → I update the brief.
