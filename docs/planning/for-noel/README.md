# for-noel — review queue from Claude to Noel

This folder holds documents Claude has drafted that need Noel's review. Pair folder is `../for-claude/`.

## How the pair works

Folder names answer "who needs to act next?" — not "where did it originate."

- **`for-noel/`** (this folder) — Claude has put something here; **Noel** needs to read / decide / annotate.
- **`for-claude/`** — Noel has put something here; **Claude** needs to act on it.

Anything sitting in the wrong folder is a state error — it should never happen.

## Protocol

1. **Claude drops a doc in `for-noel/`.** File name format: `YYYY-MM-DD-short-slug.md`. The doc has frontmatter or a top-of-file block describing what kind of review is needed (decision, info-only, sanity-check, etc.).
2. **Noel reads it on his own time.** Annotates inline using the four-asterisk comment convention (see below).
3. **Noel MOVES the annotated copy to `../for-claude/`.** Critical step: leaving the annotated doc in `for-noel/` makes it look (to Claude) like Noel hasn't touched it yet. The folder name is the signal — if it's annotated and ready for Claude, it belongs in for-claude.
4. **Claude processes for-claude.** Reads Noel's comments, then either:
   - Files the doc to its permanent home (`docs/planning/design/`, `docs/planning/agile/archive/`, the changelog, a memory entry, `JJFlex-private/`, etc.) and reports the destination in chat. Deletes the for-claude copy.
   - Re-drafts and returns it to `for-noel/` for another pass if Noel's comments raise new questions.
   - Asks clarifying questions in chat first if the comments are ambiguous.

Noel can also drop things in `for-claude/` directly without a prior round-trip — see "What Noel puts in for-claude on his own" below.

## Comment convention

Lines starting with `**** ` (four asterisks + space) are Noel speaking to Claude. They can appear anywhere in the document.

```
The original paragraph is here.

**** Noel: I disagree with the 60-second escalation. Make it 30.

The next paragraph continues.
```

For multi-line comments, prefix every line:

```
**** Noel: Two things on this section.
**** First, the cancel button label should be "Stop" not "Cancel".
**** Second, can we land this in 4.1.18 instead?
```

A standalone `**** ACK` line means Noel approves the section as-is, no changes requested.

A standalone `**** SKIP` line on a numbered question means "intentionally not answering this one — move on without it." Distinguishes a skipped question from one Noel forgot.

**Drafting tip for Claude:** when posing a numbered question list, leave a **blank line after each question** so Noel's `**** ` answer can't accidentally absorb the next question into the same line. Discovered 2026-05-02 when Q3 of a stuck-modal digest got swallowed by Q2's answer.

## What goes here

- Design proposals where Noel's call shapes the implementation
- Research summaries where Noel needs to weigh in before next steps
- Strategic frames or product-direction memos
- Checkpoint briefings after long autonomous sessions
- Anything Claude would otherwise lose track of in memory or chat

## What does NOT go here

- Code (use git)
- TODO entries (use `docs/planning/vision/JJFlex-TODO.md`)
- Sprint plans (use `docs/planning/agile/`)
- Changelog entries (use `docs/CHANGELOG.md`)
- Working notes Claude doesn't need Noel to read
- Active engineering artifacts (test builds, tester traces) — those go in `docs/planning/active/<investigation-name>/`

## Privacy

Files here are committed to git by default. If a doc shouldn't be public (private testers, sensitive customer info, etc.), Claude routes it to `JJFlex-private/for-noel/` instead.
