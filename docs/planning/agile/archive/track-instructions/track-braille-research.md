# Track C — Braille primitive cross-AT research

**Worktree:** `C:/dev/jjflex-braille`
**Branch:** `track/braille-research` (off `main` @ `09b724c3`)
**Mode:** autonomous research; no production code; web research expected

## The vision (Noel's framing, 2026-04-29)

A **braille primitive** that:

1. Displays a string on a braille display.
2. Supports panning when the string is too long for the display (panning keys, plus implicit panning via cursor routing into off-screen text).
3. Accepts cursor routing key clicks and returns which **logical element** in the displayed string was clicked.

Why it matters:

- **JJFlex use case:** scalable Home that can be panned for full-detail mode or shown as a compact line. Custom braille text for non-standard dialogs. Click-to-act on rendered controls (mute, VOX, slice select).
- **OSARA / Reaper use case:** Jamie Teh (NVDA core dev, OSARA maintainer) has expressed interest in the same primitive — for Reaper, this would enable solo'ing tracks, navigating along a track via cursor routing, and so on. Reaper's current braille support is poor.
- **Cross-application leverage:** the primitive isn't JJFlex-specific. If we ship it as an NVDA add-on + JAWS script pair, OSARA could ride on top of it for free. Both projects have similar shape claims.

Single-line displays are the universal case (most Braille displays in the wild are 14, 20, 32, 40, 80 cells, all single-line). Cursor routing keys are universal — every modern display has them.

## Goal

Produce a design synthesis document that proposes a clean cross-AT abstraction for this primitive, backed by surveys of what NVDA and JAWS already expose, plus a survey of OSARA prior art. Optional: minimal prototype that exercises the primitive in isolation (no JJFlex integration).

This track produces **design documents and research notes only** — no production JJFlex code lands on this branch from this initial research pass. Implementation work is a future sprint after Noel reviews the design.

## Background reading (do first, in order)

1. `~/.claude/projects/c--dev-JJFlex-NG/memory/project_multi_braille_output_vision.md` — Noel's existing multi-channel vision (Dot Pad + Focus 40 in parallel with linear status line)
2. `~/.claude/projects/c--dev-JJFlex-NG/memory/project_verbosity_architecture_proposal.md` — verbosity ladder including braille channel
3. `docs/planning/braille-verbosity-design.md` — earlier braille planning doc
4. `~/.claude/projects/c--dev-JJFlex-NG/memory/project_jjflexible_home_terminology.md` — naming convention for the Home concept
5. `~/.claude/projects/c--dev-JJFlex-NG/memory/feedback_accessibility_is_end_to_end.md` — design constraint: install/configure/use/maintain must all be accessible

If the multi-radio worktree (`C:/dev/jjflex-multi-radio`) has an `at-scripting-research.md` file, read it as well — it may overlap with the NVDA/JAWS research scope.

## Phase plan

Each phase produces a doc. Final commit per phase: `Track C Phase N: <description>`.

### Phase 1 — NVDA primitive survey

**Output:** `docs/planning/track-c/nvda-braille-primitive.md`

Research questions:

- What does NVDA's `braille` module expose for **custom region rendering**? (Look at `braille.handler`, `braille.Region`, `braille.NVDAObjectRegion`, and any add-on hooks.)
- What's the API for an add-on to **register a routing key callback**? Specifically, can an add-on say "when this string is displayed and the user routes to position N, call my function with N"?
- Does NVDA expose a clean way to **own the display surface** for a portion of time (i.e., suppress NVDA's normal braille output and put up our string instead)?
- What's the **panning semantics** — does the add-on control panning, or does NVDA's panning still apply?
- Multi-line displays (rare but exist) — does NVDA expose them differently?

Sources to consult:

- NVDA Developer Guide: https://www.nvaccess.org/files/nvda/documentation/developerGuide.html
- NVDA source on GitHub (nvaccess/nvda) — `source/braille.py` and `source/brailleDisplayDrivers/`
- NVDA add-on samples that customize braille (search GitHub topics: `nvda-addon braille`)
- `nvda-addon-template` patterns

### Phase 2 — JAWS primitive survey

**Output:** `docs/planning/track-c/jaws-braille-primitive.md`

Research questions:

- What's the JAWS scripting API for **handling `BRAILLE_ROUTING` events**? What context does the script get (cell position, current display content, current focus context)?
- Can a JAWS script **render a custom string to the braille display** instead of the default focused-control rendering? (Look at functions like `BrailleAddString`, `BrailleMessage`, `BrailleMessageWithSpeech`, virtual viewer / virtual buffer braille).
- How does JAWS handle panning for custom-rendered content?
- What's the equivalent of an NVDA add-on for JAWS — a script per app, a global script, or something else?

Sources to consult:

- FreedomScientific JAWS scripting documentation
- The JAWS documentation hosted at https://support.freedomscientific.com/Content/Documents/
- HJPad scripting examples on GitHub
- `jaws-script` topic on GitHub for sample patterns

### Phase 3 — OSARA prior art survey

**Output:** `docs/planning/track-c/osara-prior-art.md`

OSARA = Open Source Accessibility for the REAPER Application, maintained by Jamie Teh.

Research questions:

- Does OSARA already implement any braille rendering? Where in the code?
- What primitives does OSARA expose to its users for braille interaction?
- What patterns has OSARA used to integrate with NVDA's braille module?
- Are there past discussions / issues / PRs in OSARA about cursor routing and braille that map to our primitive vision?

Sources:

- OSARA on GitHub: https://github.com/jcsteh/osara
- OSARA wiki / docs
- Issues with braille labels: https://github.com/jcsteh/osara/issues?q=braille

Note: we don't need to fix OSARA. The point is to understand whether their current infrastructure could absorb our primitive design — that's a force multiplier for both projects.

### Phase 4 — Synthesis: cross-AT abstraction design

**Output:** `docs/planning/track-c/cross-at-primitive-design.md`

This is the deliverable. Propose a clean API that hides the NVDA/JAWS difference. Strawman shape:

```
DisplayElement = {
  text: str,
  id: str,            // logical identifier
  on_click: callable, // optional handler
}

display(elements: list[DisplayElement]) -> session_handle
session.on_routing_click(callback: (element_id, cell_offset) -> None)
session.pan(direction: enum)
session.dismiss()
```

Open design questions to address:

1. **Panning ownership** — does our primitive own panning, or do we cooperate with the screen reader's panning? Single-line displays imply we own it; multi-line displays imply cooperation.
2. **Persistence** — what happens when focus changes in the host application? Does our display surface get torn down, or does it persist?
3. **Layering** — if NVDA is also trying to render something to braille (e.g., user moved focus), how do the two layers interact?
4. **Cell-level vs element-level click resolution** — when user routes to cell 17, do we report "cell 17" (low-level) or "element 'mute'" (high-level)? Almost certainly element-level, but what if the click is between elements?
5. **State updates** — if an element's text changes (e.g., "VOX off" → "VOX on"), how does the display update? Do we re-render, or do we offer a partial-update API?
6. **Discoverability** — how does the user know which cells are clickable? Cursor shape? Dotted underline? Status cell?

Cite specifics from the NVDA and JAWS surveys to justify each design choice.

### Phase 5 — Optional: prototype sketch

**Output:** `docs/planning/track-c/prototype/` directory

If time and tokens permit: write a minimal NVDA add-on that exercises the primitive in isolation. Specifically: an add-on that displays a fixed string with three logical elements (e.g., "Play | Stop | Mute"), and announces (via NVDA speech) which element was clicked when the user routes into one. No JJFlex integration.

This is purely to validate the API shape. If the API design from Phase 4 falls apart when you try to write the prototype, iterate Phase 4.

### Phase 6 — Handoff document

**Output:** `docs/planning/track-c/handoff.md`

Summary of what was researched, key findings, the proposed design, open questions for Noel, and a recommended next-sprint scope ("if we want to ship this, here's what needs to happen").

Suggest reach-out to Jamie Teh after Noel reviews — Jamie's interest is the leverage that turns this from "JJFlex addon" into "ecosystem contribution."

Commit: `Track C Phase 6: Handoff document`

## Coordination

- **Inbox/outbox** — drop docs needing Noel's review in `docs/planning/inbox/` per `docs/planning/inbox/README.md`.
- **No merge to main** — research lands only on `track/braille-research` until Noel reviews.
- **No code in production paths** — this branch must NOT touch any JJFlex source code (`Radios/`, `JJFlexWpf/`, `main_app/`, etc.). Only `docs/planning/track-c/`.
- **Independence from Track B** — Track B is doing the FlexLib upgrade. Don't touch any FlexLib paths.

## What NOT to do

- Don't write production braille code (no `BrailleOutputModule.cs` in `Radios/` etc.). This is research only.
- Don't open PRs or push to origin. Local commits only.
- Don't get pulled into NVDA/JAWS internals beyond what's needed to design the primitive. The point is the primitive design, not a deep audit of the screen reader internals.
- Don't propose primitives that require modifying the screen readers themselves. The primitive must work via existing add-on/script extension surfaces.

## Resume notes

If the session ends mid-phase, leave a "RESUME HERE" marker in the most recent commit message and update `docs/planning/track-c/progress.md` with what's done and what's next.

## Tools

WebFetch and WebSearch are available via the deferred-tool mechanism (use ToolSearch to load schemas). They will be the primary research tools for Phases 1-3.
