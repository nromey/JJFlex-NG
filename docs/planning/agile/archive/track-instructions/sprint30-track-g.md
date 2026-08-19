# Sprint 30 — Track G — Rigmeter Unbolted

**Worktree:** `C:\dev\jjflex-30g`  **Branch:** `sprint30/track-g`  **Base:** `honest-tx-audio` @ `972e1438`
**Model:** sonnet  **Class:** BUILDABLE

Read `docs/planning/agile/sprint30-rescue-squelch-pileup.md` for the sprint's shape.

You own the rigmeter statistics tool: fix its provenance accuracy, then move it out of this repo
into its own. **You have zero file collisions with any other track**, by construction. Your job is
to be the one thing that can never break the merge train.

---

## House rules

**The user is blind and uses NVDA.**

- **No tables, no ASCII art, no diagrams** in anything you write — reports, READMEs, docs,
  comments. Prose or bullet lists. This applies to rigmeter's own console output too: it currently
  prints aligned columns, which is a table by another name. **Do not make that worse**, and if you
  touch output formatting, prefer labelled prose lines ("authored: 412,000 lines across 1,240
  files") over new column layouts.
- **Do not put long explanations in `AutomationProperties.HelpText`** — not relevant to you, but it
  is the sprint-wide rule and you may see it referenced.

**Build verification.** Your changes are Python, so the .NET build is unaffected — but run one
anyway before you finish, to prove you did not disturb the project files by moving things:

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
```

Verify rigmeter itself by running it:

```
python tools/rigmeter/rigmeter.py all
python tools/rigmeter/rigmeter.py today
python tools/rigmeter/rigmeter.py authors
```

**Commits and pushes.**

- Commit per completed item. Message format: `Sprint 30 Track G: <description>`.
- **Push after every commit**: `git push origin sprint30/track-g`.
- Push to `origin` (nromey). **NEVER `upstream`.**
- Never `git add -A` or `git add .`.

**Do not edit:** `CLAUDE.md` (see the note in item 2 — the orchestrator applies that change),
`docs/CHANGELOG.md`, `docs/help/md/*`.

**You are unattended. Never block on a question.** Take the most defensible option, write it down,
keep going. End your report with **"Needs Noel"**.

---

## Your work

### 1. Task #41 — blame-based provenance

**The research queue says "NOT started". That is wrong.** `tools/rigmeter/rigmeter.py` v1.2 already
ships an `authors` subcommand at lines 2253-2360, per-project plus repo-wide, vendor-excluded via
`classify_path`.

**What is missing is the part the task calls load-bearing.** The blame call in
`git_blame_authors()` (around line 2282) runs:

```python
git_run(repo_root, "blame", "--line-porcelain", "--", relpath)
```

It passes **neither `-w` nor `-C`**. That means:

- **Without `-w`**, whitespace-only changes reassign a line to whoever last reformatted it.
- **Without `-C`**, a line moved or copied between files is credited to whoever moved it, not
  whoever wrote it.

This codebase had a **whole-tree .NET 10 migration** — mass reformatting and mass file moves. So
the current numbers are not measuring authorship, they are measuring who touched things most
recently during a migration.

**The fix:** add `-w -C` (evaluate whether `-C -C`, which also searches other files in the same
commit, is worth the runtime — say what you chose and why). **Then re-check the numbers before
anyone quotes them** — report the before-and-after per author, because the whole point is that the
delta is expected to be large.

**Why this matters beyond accuracy, and it is worth knowing:** the `cmd_authors` docstring in the
file says it plainly. Jim Shaffer wrote the original JJFlexRadio and passed away in early 2026,
asking Noel to take it over. Jim never saw what Noel has built since. This number is the answer to
a quiet question, so it needs to be right rather than flattering — in either direction. Do not
tune it toward a nicer answer.

**Pinned classification rules — do not re-derive these, do not widen them:**

- **Vendored code** is exactly the three path prefixes in `VENDOR_DIR_PREFIXES` (line 117):
  `FlexLib_API/`, `P-Opus-master/`, `PortAudioSharp-src-0.19.3/`. Path-prefix matching, not
  basename. If you believe another directory is vendored, **report it, do not add it** — changing
  this constant silently rewrites every historical comparison.
- **Derived artefacts** are `DERIVED_PATH_PREFIXES`: `docs/help/pages/`, `docs/help/md/whats-new.md`.
- **Merge commits:** `git blame` attributes to the commit that introduced the line in the first
  parent's history; it does not credit merge commits themselves. You do not need to special-case
  them. If your before/after numbers show a merge-shaped anomaly, say so rather than patching
  around it.
- **Author aliases** collapse through `normalize_author()` / `AUTHOR_ALIASES_DEFAULT`. If `-w -C`
  surfaces a new spelling of an existing person, add the alias — that is a data fix, not a rule
  change.

### 2. Task #42 — extract rigmeter into its own repository

Move it to a new repo at **`C:\dev\rigmeter`**, then delete `tools/rigmeter/` from this repo.

**Non-negotiable constraints — the time series depends on these:**

- **The NAS snapshot path must not change:**
  `\\nas.macaw-jazz.ts.net\jjflex\historical\stats\<commit-date>-<short-sha>.json`, with the
  documented fallback to `%LOCALAPPDATA%\rigmeter\snapshots\` when NAS is unreachable.
- **The snapshot JSON format must not change.** Existing snapshots must stay readable, and
  `rigmeter growth --use-snapshots <date-a> <date-b>` must keep working across the move.
- **Multi-project scanning must keep working.** Rigmeter reads several repos (JJFlex-NG,
  Freight-Fate, Civ-vi-access and others), so being outside JJFlex-NG should be natural — but
  verify that any assumption about "the repo I live in" is now an explicit argument or config,
  not an implicit `..`.

**Order of operations that keeps you safe:** build and verify the new repo FIRST, run all three
subcommands from `C:\dev\rigmeter` and diff the output against the same commands run from
`tools/rigmeter/`, and only then delete the in-repo copy. Numbers identical (apart from the #41
blame change, which you should land and measure before the move so the two changes do not
confound each other).

**Initialise the new repo with git** and make a first commit, but **do NOT create a GitHub remote
or push it anywhere.** Creating a repository under Noel's account is an outward-facing action and
it is his call. Note in your report that it is ready to push.

**The one CLAUDE.md dependency, and how to handle it.** CLAUDE.md's end-of-day seal procedure,
step 4a, invokes `python tools/rigmeter/rigmeter.py all` / `today` / `snapshot`. After your merge
those paths are wrong, and **the seal runs every single dev day** — a broken path there breaks a
daily ritual.

You cannot edit CLAUDE.md. So:

- Put the **exact replacement text** for step 4a in your report — the full corrected invocations,
  ready to paste. The orchestrator applies it at merge time.
- Consider leaving a tiny shim so the transition cannot break: a `tools/rigmeter/README.md`
  (or similar) that says where it went. Use your judgement; if you think a shim is clutter, say so
  and just be precise in the report instead.

---

## Files you own

- `tools/rigmeter/rigmeter.py`
- `tools/rigmeter/README.md`
- A new repository at `C:\dev\rigmeter`
- Any rigmeter references in `docs/planning/` (but NOT `CLAUDE.md`)

## Collisions

**None.** No other track touches any file you touch. That is deliberate — you fill a concurrency
slot with work that cannot break anyone.

## Merge position

**First.** You merge before everything else, so that deleting `tools/rigmeter/` happens before any
other track could accidentally grow a dependency on it.

## Your report

The before-and-after author numbers from the `-w -C` change (this is the deliverable, not a
footnote), what you chose about `-C -C` and why, confirmation that the three subcommands produce
identical output from the new location, confirmation that an existing NAS snapshot still parses,
the **exact CLAUDE.md step 4a replacement text**, changelog lines if any are user-facing (most of
this is developer tooling and probably earns none — say so if so), and **Needs Noel**.

Known Needs-Noel item already: whether to create and push a GitHub repository for rigmeter.
