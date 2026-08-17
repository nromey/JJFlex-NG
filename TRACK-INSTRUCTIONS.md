# Small-fixes sweep — the orphan papercuts

**Worktree:** `C:\dev\jjflex-sweep` · **Branch:** `bsr/track-sweep` · **Model:** Fable

**Read first:** `docs/planning/active/barefoot-splatter-ragchew.md`, sections
"Coverage audit" and "Papercuts have owners, not a someday pile".

## What a papercut is, and why this track exists

A defect that is individually trivial and collectively corrosive — nothing
broken, nothing blocked, but the software feels like nobody is looking.

**The defining property: papercuts lose every priority argument individually and
win collectively.** Any one, against any feature, is obviously less important, so
it gets deferred every time, forever. This track exists to remove that
comparison.

**They cost more in this codebase than most.** A sighted user glances past a
mislabelled control and infers the truth in a fraction of a second. A screen
reader user hears the wrong word with no cheap way to check. **Cosmetic
elsewhere, functional here.**

These are the *orphans* — items touching files no other track claims. Other
tracks carry their own.

## The work

**1. `testtone.armed` is spoken aloud** — a raw resource key leaking into speech,
found at the bench 2026-08-16. It appears **when PC audio is OFF**, which
narrows it: one branch holds a real string, the other the key. Fix the leak, and
fix the wording while you are there — on/off or armed/disarmed, **consistently**,
not mixed.

**2. The PC audio connect setting is spoken in different words than it is
labelled** — "as you left it" versus "Remember how I left it". Pick one.

**3. The tracing dialog is confusing** — and it is the front door of the
reporting pipeline, so confusion there costs more than it looks. This one is a
**design task**, not a rename. Propose before rewriting.

**4. The four open questions in the diagnostic-log design.** See
`docs/planning/active/diagnostic-log-surface.md`. Answer them in that file.

**5. Verify the installer ships a clean file list** — build litter has been
reaching output. `generate-deletelist.ps1` walks the publish output; confirm what
lands there is what should.

**6. Two small UI/help-plumbing gaps** found in an earlier sweep. Establish what
they were before fixing — they may be item 7.

**7. READ THE TWO MAY AUDITS AND DECIDE THEIR FATE.** This is the interesting one.

`docs/planning/active/2026-05-11-jj-h-context-help-audit.md` and
`docs/planning/active/2026-05-11-keyboard-reference-audit.md` both say **"fixes
deferred"** and have said so since May.

**That is the papercut failure mode in its purest form** — findings that lost a
priority argument once and were never compared against anything again. They are
either real work or obsolete. **Archiving them resolves neither; it only stops
them being visible.**

Read both. Fix what is still real, and report what is obsolete so it can be
closed honestly. They may well *be* item 6.

**8. Archive seven finished planning docs** — the list is in the plan's
"Housekeeping task" section. Move from `docs/planning/active/` to `archive/`.
**Do NOT archive** the two May audits above, nor `qsl-roster-ragchew.md` or
`elmer-beacon-patch.md` (those wait for Track A to merge), nor the three
`detached-*` files (status unknown).

## Rules

- **Reuse the symbols you find. If you conclude one should move or change
  signature, REPORT it rather than doing it.**
- You touch many files but **only additively and locally** — a wording fix, a
  string, a help page. **Anything structural belongs to the track that owns it;
  report instead.**
- If a fix belongs to a file another track owns, **note it and move on** — do not
  reach into their area.
- Build: `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
- Commit style: `Sweep: <description>`. Commit each fix separately so a bad one
  can be reverted alone.
- **Do not merge, do not push to main, do not touch other worktrees.**

## Done means

Builds clean. Every item above is either fixed or reported with a reason. The two
May audits have a verdict rather than a shrug. Report anything you found that
belongs to another track.
