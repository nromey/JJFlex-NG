# Sprint 33 Track E — triage the master test list by tier

**Worktree:** `C:\dev\jjflex-33e` · **Branch:** `sprint33/track-e`
**Plan:** `docs/planning/active/barefoot-harness-pileup.md`
**Merges into Track A. Docs only — no code.**

---

## The point, and it is a real one

Task #149. The master test list runs to roughly eighty items, and most of them
are some form of "press this, does it work."

**That is not a job for a human, and it is emphatically not a job for the
project's only tester, who is blind and whose time is the scarcest resource
here.** Every tab-order check he performs by hand is time not spent on the
questions only he can answer.

## What to produce

Go through the master test list — start from
`docs/planning/for-noel/` and task #55, which created it; find the current file
rather than assuming a path — and assign every item a tier:

- **Tier 1** if an in-process automation-tree walk settles it. Control exists, is
  focusable, has a name, has help text, appears in the tree.
- **Tier 2** if it needs real keystrokes against the running application. Key
  routes, focus moves where it should, dialog opens.
- **Tier 3** if it needs the radio. The command actually changed something on the
  8600.
- **HUMAN** for the residue that genuinely needs ears and judgement.

## The residue is the deliverable

The classification is the means. **The output that matters is the short list of
things only Noel can judge**, because that is what his testing sessions become.

Things that genuinely belong to a human: does this sound right; is this earcon
audible against real band noise; is this wording clear to a ham who is not us; is
this announcement too long; does the speech get in the way when you are actually
operating; does this feel right at 20 words per minute.

Be honest about the boundary. **A test that a machine can perform badly is still
a human test.** "Does NVDA announce the callsign" can be approximated by reading
the automation tree, but what NVDA actually says depends on NVDA, and the machine
check is a proxy rather than the thing. Where you are proposing a proxy, say so
explicitly rather than quietly promoting it to Tier 1 — a suite that claims
coverage it does not have is worse than one that admits the gap.

## Also flag the stale ones

Some items on that list will have been fixed since it was written, and some will
describe surfaces that no longer exist — **description drift is this project's
dominant defect class and the test list is not immune.** Where an item looks
stale, say so and say why. Do not silently drop it; a dropped test and a passed
test look identical later.

## House rules

- **NO TABLES.** Prose or bullets, screen reader first. This one is aimed
  directly at you: a triage document is the single most tempting place in this
  sprint to reach for a table, and it is exactly the format Noel cannot read
  comfortably. Use headed sections and bullets.
- Do not touch code. Docs only.
- Do not touch files outside your worktree.

## Where it goes

`docs/planning/active/` alongside the sprint plan, named clearly enough that
someone finds it a month from now.

## Commits

`Sprint 33 Track E: <description>`.

## Completion report

State: how many items, how they split across the four tiers, and the human-only
residue as an actual list. Plus anything you found stale, with the reason.
