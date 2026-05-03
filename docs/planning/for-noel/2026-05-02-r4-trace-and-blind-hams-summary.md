---
type: summary + pointer doc — two completed background tracks
review needed: read findings, weigh in where flagged
priority: medium — Don's R4 has no blocking question for you; blind-hams has 12
---

# R4 trace + blind-hams exploration — both landed

Two parallel tracks finished while you were getting set up. Neither blocks anything you're currently doing; both have findings worth your reading attention. No rush.

## Don's R4 trace — Outcome B confirmed

**Full analysis:** `docs/planning/active/don-flexlib-4218-discovery/R4-trace-analysis.md`

**TL;DR:** The R4 trace falsified our top hypothesis (.NET 10 `ReceiveAsync(token)` async-vs-sync). The synchronous receive drain ALSO got zero packets in three 5-second windows, while the SelfTest probes (loopback + NIC-self + limited-broadcast) round-tripped cleanly. So the socket is healthy, but external broadcasts aren't being delivered to it — even though Don's 4.1.5 build on the same machine receives the same radio fine.

**New direction:** source-level diff of `Discovery.cs` between FlexLib 4.1.5 and 4.2.18, focused on socket-options-affecting calls (UdpClient constructor overload, JoinMulticastGroup, MulticastLoopback, IP_MULTICAST_IF, IP_PKTINFO, ExclusiveAddressUse, EnableBroadcast). If a difference is found, R5 reverts that one option in our wrapper and Don confirms.

**No blocking question for you.** I'll start the source diff autonomously after this — it's pure reading work, no Don round needed. Will surface findings here when I have them. If the diff turns up nothing relevant, the next step is Wireshark on Don's machine OR your 8600 parallel test (per `project_8600_unbox_firmware_trigger.md` — depends on whether you've unboxed yet).

The memory entry `project_flexlib_4218_discovery_investigation.md` is updated with the new direction (Outcome B confirmed, suspect ranking pivoted, source diff added as next step).

## Blind-hams + solar exploration — agent finished, deliverables landed

**Deliverables location:** `docs/planning/blind-hams-and-solar-exploration/`

All five deliverables landed: `inventory.md` (28KB), `categorization.md` (13KB), `migration-plan.md` (29KB), `questions-for-noel.md` (14KB), `risks.md` (18KB). The agent appended a completion summary at the bottom of `AGENT-LAUNCH-CONTEXT.md`.

### Major reframes worth noting before you read

1. **Andre's box does NOT host the Jekyll site.** The Jekyll site is on Netlify at `www.blindhams.network`. Andre serves only 5 files at `data.blindhams.network`: three JSON data feeds, a TTS-script text file, and a health probe. The brief assumed Jekyll-on-Andre; the agent corrected this. Migration scope is much smaller than the brief implied.

2. **Five real operational issues found that the migration plan addresses:**
   - 2,045 leaked tmpfiles in `/var/www/data/`
   - The publisher's `git pull --ff-only` has been silently failing for weeks with "Host key verification failed" (masked by `|| true` in the deploy script)
   - The publisher runs as `root` when it doesn't need to
   - A public ntfy.sh topic for submission notifications (no token / auth)
   - A stale `/www/data` directory from October 2025 that nothing serves but nothing cleaned up

3. **Migration plan recommends Option A (everything on rarbox, single nginx)** over Option B (R2-static + rarbox-dynamic) because the data volumes are too small to benefit from CDN. Option B's plan is included as an appendix in case you prefer the JJ-Flexible-Data-Provider-symmetric model.

### Twelve questions for you

Inline at `docs/planning/blind-hams-and-solar-exploration/questions-for-noel.md`. Each has a default assumption so the migration plan is already actionable — your answers will sharpen it but the plan can execute as-is.

The two highest-impact ones (per the agent's framing):
- **Q1:** source-of-truth flow for `_data/nets.json` after migration (publisher pulls from GitHub vs helper writes locally + cron-coalesced push). Affects Phase 2.
- **Q3:** where does the AllStar voice integration live? The agent found referenced TTS scripts but couldn't locate the consumer.

I'd suggest reading in this order:
1. The completion summary in `AGENT-LAUNCH-CONTEXT.md` (1 paragraph, fastest orientation)
2. `categorization.md` (12KB, gives you the surface map)
3. `questions-for-noel.md` — answer inline with `**** ` and move the file to `for-claude/` when done
4. `migration-plan.md` (29KB — the main artifact, but you can defer if you just want a temperature check today)
5. `risks.md` and `inventory.md` are reference material — read as needed

## Two questions FOR YOU now (chat-sized, not for-noel-sized)

These are quick — chat answers fine:

1. **R4 source diff — go ahead autonomously?** The work is pure source reading, no Don round needed. Default if you don't answer: I start it now in the foreground while you read for-noel. Reports back when I find a candidate (or rule out the diff entirely).

2. **WSL agent's 12 questions — answer in their own file or pull each into for-noel?** Default: keep them in `blind-hams-and-solar-exploration/questions-for-noel.md` (where the agent put them). Move the file to `for-claude/` when answered. Don't proliferate copies into for-noel/.

## What's now in your for-noel queue (in priority order)

1. `2026-05-02-r4-trace-and-blind-hams-summary.md` ← this doc
2. `2026-05-02-stuck-modal-escape-design.md` — quick `**** ACK` or pushback
3. `2026-05-02-design-review-queue.md` — pick which Sprint 29 / 4.2.0 design memos to engage
4. `2026-05-02-multi-radio-braille-read-pack.md` — the big reading session (~60-90 min)

PLUS the WSL deliverables in `docs/planning/blind-hams-and-solar-exploration/` — read those at whatever pace, answer questions inline.
