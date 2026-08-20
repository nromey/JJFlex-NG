# Sprint 33 Track I — a reference voice file, and recording as a first-class feature

**Worktree:** `C:\dev\jjflex-33i` · **Branch:** `sprint33/track-i`
**Plan:** `docs/planning/active/barefoot-harness-pileup.md`
**Merges into Track A.**

Build your own worktree only:
`dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`

---

## Task #150 — and why it is a prerequisite rather than a nicety

**Noel:** *"for testing, recommend adding a way to record a testing file /
providing one for use while testing voice content."*

**Deterministic input is what makes transmit results comparable at all.** Every
audio measurement taken so far has been against a human speaking into a
microphone — which varies in level, distance, timing and content between every
single run. Two measurements taken that way cannot be compared, so "did that
change help?" has never been answerable with evidence.

A known file removes the variable. The same audio, every run, forever.

**Everything downstream needs this.** The transmit-audio wizard (#152) is an
iterative optimiser — it CANNOT converge against an input that changes between
iterations. Tier 3b's transmit tests need a repeatable stimulus. The analyzer's
level thresholds (#123 shipped four invented numbers marked BENCH) can only be
calibrated against known input.

## Two halves, and they are different jobs

**Half one: SHIP a reference file.** A known recording that goes out with the
application. Speech, because that is what is being tested — a tone tells you
nothing about compression, processing or intelligibility.

Design questions to settle and report:
- **What is on it?** Speech with a known level and a defined dynamic range.
  Consider including a section with the standard phonetic material hams already
  use for audio checks, so a listener can judge it by ear as well.
- **What format, and where does it live?** It has to survive the installer and
  the self-contained publish — check the `Resources/` convention and
  `generate-deletelist.ps1` so the uninstaller cleans it up.
- **How is it played into the transmit path?** This is the interesting part: the
  file must reach the same place the microphone reaches, or it is testing a
  different chain than the one under test.

**Half two: RECORD your own.** Noel, on the same day: *"recording a reference
could take place in workshop or in settings and should probably be saved."*

His own audio is the honest reference for HIS station — his microphone, his room,
his voice. A shipped file is the common baseline; a recorded one is the personal
one. Support both.

## And it opens onto something bigger — read this before designing the recorder

**Noel:** *"Could use that code for record keys / recorded station keys like 'cq
field day cq field day this is K5NER K5NER calling, over'. I think Jim has some
of that code in there but we probably should look at it and if we're recording a
reference add recording quick keys for voice and CW."*

**So the recorder you build here is the same recorder a station message library
needs (#151).** Design it as a reusable recording capability, not as a
single-purpose button buried in a dialog. Do not build the message library — that
is #151 and it has its own ownership questions — but do not build something
#151 will have to throw away either.

**Go and look for Jim's code first.** Noel believes some of it exists. Finding it
and reporting what it does is part of this track. **Jim-era code gets modernised;
Jim's design does not get replaced** — if he built a recording feature, understand
his intent before writing a new one beside it.

## The ownership constraint, because it will bite later

The radio has its own Digital Voice Keyer (`FlexLib_API/FlexLib/DVK.cs`), and it
is tempting to lean on it. **Recordings the operator makes are OPERATOR STATE:
they travel with the person and must work on a radio they do not own.** DVK slots
are STATION state — they occupy the owner's slots and every MultiFlex client sees
them.

Noel settled this: *"Users who use Connect will need to use a local thing, not
the radio's."*

**So anything you record is stored locally.** DVK is an optimisation available
when you own the rig, never the default, and not this track's problem.

## Recording is a privacy-adjacent feature — be careful with the design

A microphone recorder that saves files is exactly the kind of thing that must
never surprise anyone. **Recording is always explicit and always obvious**: the
operator starts it deliberately, something says clearly that recording is
happening, and something says clearly when it stops. No silent capture, ever, for
any reason. This project's standing rule is no silent phone-home and no
keystroke capture; a microphone deserves at least the same care.

## House rules

- **No tables** in any output, report or doc. Prose or bullets. Screen reader first.
- User-facing prose — labels, the reference file's spoken content, announcements
  — needs Noel's approval. Draft it, show it, do not ship it unreviewed.
- Do not touch files outside your worktree.
- Coordinate before any run that uses the microphone or plays audio; Noel is at
  the keyboard.

## Commits

`Sprint 33 Track I: <description>`.

## Completion report

State: what Jim's existing recording code turned out to be; the reference file's
content, format and location; how audio reaches the transmit path; the recorder's
shape and why it will not need throwing away for #151; and the exact wording you
want Noel to approve.
