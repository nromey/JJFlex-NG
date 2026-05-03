# Priority — tomorrow morning's read order (2026-05-03)

Reorganized 2026-05-02 evening for your morning reading. Read these in this order; everything else stays in `for-noel/` parent for whenever.

## Read order

### 1. `2026-05-02-concert-night-autonomous-results.md` (~10 min — read FIRST)

Your concert-night briefing. Captures everything that happened during the autonomous run while you were at the orchestra: priority answers folded into memory, R4 source diff falsified the socket-options hypothesis (big finding — bug is NOT in Discovery.cs), 502 root cause located, GitHub Actions re-enabled, multi-radio existing-research surfaced. **Read this BEFORE anything else** — it sets context for the rest.

### 2. `2026-05-02-multi-radio-existing-research-index.md` (~5 min — read SECOND)

Important reframe: there's a substantial body of multi-radio research already in `C:\dev\jjflex-multi-radio\docs\research\` (12+ files including iradio-backend-design, radio-class-taxonomy, multi-radio-architecture-synthesis). The original read pack didn't reference these. This index changes how you'd approach the multi-radio reading — from "design from scratch" to "synthesize existing work." **Read this BEFORE the read pack** so the pack lands in proper context.

### 3. `2026-05-02-multi-radio-braille-read-pack.md` (~30-40 min — read THIRD)

The original strategic read pack from yesterday. Read with the existing-research index's guidance in mind — most of the architectural questions in the pack already have proposed answers in the worktree research. Goal: form positions on the questions; some will be quick "agree with the worktree's proposed answer," others will need real thought.

### 4. `2026-05-02-hamlib-integration-spike-FROM-WORKTREE.md` (~20-30 min — read FOURTH)

The substantial Hamlib design doc from the worktree (~32KB). Read after the read pack so you have strategic context. This drills into Hamlib-specific integration choices and is implementation-shaping.

## Total estimated time

~65-85 minutes for all four. If you only have an hour: read 1 + 2 + skim 3 (focusing on the questions). The Hamlib spike (4) is the most deferrable — it's reference material for when the multi-radio engineering track actually starts.

## After these four

Move processed docs to `for-claude/`. Then it's track-selection time:

- **Multi-radio architecture working session** — synthesize what you've read into an ACK'd interface spec; spawn `track/multi-radio-foundation` with the spec
- **API.cs / HAAPI.cs source diff** — the next R4 investigative step to find where the FlexLib 4.2.18 bug actually lives
- **Stuck-modal escape implementation** — concrete, ~275 LOC, ACK'd
- **brailleElement v1 prototype** — "write a sample line, see if cursor routing shows where you click"
- **Tuning unity / RIT-XIT implementation** — Don's been waiting

Or pick something else entirely. The day's foundation work means tomorrow's choices have clear specs; engineering velocity should be high.

## Lower-priority (still in for-noel parent, read whenever)

- `2026-05-02-r4-trace-and-blind-hams-summary.md` — informational summary, no decisions needed
- `2026-05-02-sprint29-updater-pull.md` — 8-phase scope; meta-question about slicing G+H out (deserves calm thinking)
- `2026-05-02-crash-reporter-pull.md` — Sprint 29 design
- `2026-05-02-verbosity-architecture-pull.md` — Sprint 30+ scope, not blocking
- `2026-04-28-morning-briefing.md` — older, lowest priority
