# Sprint 33 Track K — slice changes never stick, and the profile surface that would make them stick

**Worktree:** `C:\dev\jjflex-33k` · **Branch:** `sprint33/track-k`
**Plan:** `docs/planning/active/barefoot-harness-pileup.md`
**Merges into Track A.**

Build your own worktree only:
`dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`

---

## Scope: #117 and #59, which are the same bug seen from two ends

**#59, as Noel lived it:** *"The radio came with four slices set at 14.1 USB, if
I release a slice and close the radio ... slice D comes back, again."*

**#117 is why:** slice changes never persist, because the thing that would
persist them is the radio's global profile, and our profile surface is
two-thirds stubbed.

**This is not a slice-management bug.** Noel was explicit: *"I never thought that
slice creation was a problem, or deletion, slice management worked, it just
didn't stick."* Creation and deletion work. The state simply is not saved
anywhere, so the radio hands back its stored layout on the next connect.

## What actually exists today

`Radios/FlexBase.cs` already has the FlexLib plumbing — `SaveGlobalProfile` at
`:11917`, `DeleteGlobalProfile` at `:12070`, and a `newGlobalProfile` mechanism
around `:11935`. **The gap is the operator-facing surface, not the transport.**

Start by reading what is there and reporting honestly what works, what is
stubbed, and what is missing. "Two-thirds stubbed" is a previous session's
estimate, not a measurement — verify it rather than inheriting it.

## The design question Noel raised, and it is a good one

> *"We could also have a setting that if there's been a change to the radio, it
> could offer to save the profile. I'm not sure I'd do this, but generally, if you
> tune the radio or any radio, when you turn it off, it saves stuff."*

He is right about the expectation and right to hesitate. **Both halves matter.**

The expectation is real: every other radio remembers what you did to it. An
operator who releases a slice and finds it back tomorrow reasonably concludes the
application is broken.

**The hesitation is also right, and here is the reason to take it seriously.**

## A global profile is STATION state, and that is what makes auto-saving dangerous

Global profiles belong to the radio, not to the operator. Everyone who connects
shares them.

**So auto-saving on disconnect would capture whatever the station looked like at
that moment — including another MultiFlex operator's slices.** A guest who
happened to disconnect last would overwrite the owner's layout with their own,
silently, having never asked to. That is a data-loss bug wearing a convenience
feature's clothing.

Noel settled the governing principle already: *"Users who use Connect will need
to use a local thing, not the radio's."* Operator state travels with the person;
station state stays with the rig. A global profile is emphatically the second.

**Therefore: OFFER, never assume.** If the operator is asked and says yes, it is
their decision on their station. If nobody asks, it is a silent write to shared
state. **The offer must also say what it is about to do** — that it saves the
whole station layout, not just the slice they were thinking about.

**And it must not offer at all when another operator is connected under
MultiFlex**, or the offer itself becomes the trap.

## Also settle the question Noel could not answer

> *"if I release a slice and close the radio, I don't know what happens re:
> saving the state to the radio, if I have to save to a profile or what I need to
> do in JJ Flexible to get it to stick in the radio."*

**He does not know the procedure, and he owns the radio.** That is the real
finding here: even if the feature works, it is undiscoverable. Whatever you
build, the answer to "how do I make this stick" has to be reachable from the
place where the operator changes slices — not buried in a profile dialog they
have no reason to open.

## Do NOT fix this by writing profiles from the harness

Track C's radio harness is explicitly told never to "fix" a returning slice by
saving a profile. Keep that boundary: this track builds the operator's ability to
save deliberately; it does not make anything save automatically.

## Verify against #58 while you are in here

**#58 looks addressed and needs an ear, not a fix.** Sprint 32 Track H shipped
`AnnounceSliceCensus` in `FlexBase.cs:12439` — the "used over total" census Noel
asked for, replacing the storm of per-slice mode announcements. Confirm by
listening on a real connect that the storm is genuinely gone, and report it. Do
not rewrite it.

## House rules

- **No tables** in any output, report or doc. Prose or bullets. Screen reader first.
- **User-facing prose needs Noel's approval** — especially the save offer, which
  is a prompt about shared state and has to be unmistakable. Draft, show, do not
  ship unreviewed.
- **Settings are intents, not commands** — never hide profile controls based on
  connection state. Queue and apply.
- Do not touch files outside your worktree.
- Coordinate before any run that takes the radio. Tracks C, D and G also want the
  8600, and Noel may be operating it.

## Commits

`Sprint 33 Track K: <description>`.

## Completion report

State: what the profile surface actually supports today versus the "two-thirds
stubbed" claim; the offer design and the exact wording for Noel to approve; how
an operator discovers the procedure from where they change slices; the MultiFlex
refusal; and your verdict on #58 from listening.

---

## AUTHORISATION IS BROKERED — do NOT ask Noel directly

**Decided by Noel, 2026-08-20.** Five tracks want either the radio or the live
desktop, and five agents interrupting him independently would be worse than the
collision the handshake exists to prevent.

**So: when you are ready for a run that needs the radio or drives the UI, STOP
and report "ready for a radio run" (or "ready for a UI run") to the orchestrator,
with exactly what you intend to do and roughly how long it takes.** Do not ask
Noel. Do not proceed on your own initiative.

The orchestrator batches ready tracks, asks Noel once, runs them back to back,
and reports done. You will be told when your run is authorised and when it is
over.

**Priority when tracks contend for the 8600: G first** — a build going to Don
depends on its answer — then C, then D, then K.

**While you wait, keep working.** Do everything that does not need hardware:
build the harness, write the code, reason it through. Arrive at your run with the
maximum settled in advance, because run time is the scarce resource, not compute.
