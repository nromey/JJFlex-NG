# One manual, four formats — with the reference sections generated

**Filed:** 2026-08-23, from Noel's idea.
**Task:** #201.
**Goal in his words:** a quick-reference card you can emboss or read on a display,
plus Word, PDF and BRF manuals with a table of contents and every key written
down — "so if I forget how to tune, or I forget how to turn off or on Smooth
Tune, it's there." Mostly written by him. He hates writing manuals. Jim shipped a
readme in HTML.

---

## The two insights this design rests on

### 1. The part he hates writing and the part that must be exhaustively correct are different parts

The prose — what the app is for, how to think about tuning, why the audio chain
is arranged the way it is — is his, and nobody else can write it.

The reference sections — every key, every command, every scope — must be
complete and must never drift, and they are exactly the sections that can be
**generated**. `CLAUDE.md` already carries a deferred plan for a build-time pass
that introspects the KeyCommands registry and emits a canonical manifest. The
manual becomes that manifest's second consumer, which is also the argument for
finally building it.

He writes the prose. The machine writes the reference and guarantees it matches
the shipped binary. Four hand-maintained manuals in four formats would be four
independent chances for description drift — this project's dominant defect class.
One source through one pipeline is zero.

### 2. Translation and formatting are different problems, and only one of them is automatable by us

This is the axis the whole braille half splits on, and getting it wrong would
produce technically-valid braille that reads as amateur.

**liblouis translates.** It converts characters into braille cells according to a
language table. It is what NVDA itself uses, so its output is in the exact tables
our readers already read every day. What it does **not** do is know that a table
of contents has leader dots and right-aligned page numbers, that a braille
heading is centred, how running heads work, or any of the rest of the
transcription standards. It is a translator, not a formatter.

**Duxbury (DBT) translates *and* formats.** It knows the transcription standards,
it imports Word with **style mapping**, and it generates a braille TOC with
correct page numbers because it knows the final pagination. Noel holds a licensed
copy. It is the professional standard and it is the right tool for anything that
gets embossed.

So the answer is not liblouis *or* Duxbury. It is both, at different tempos.

---

## The pipeline

One Markdown source is the origin of everything.

- **HTML and CHM** — already exist, unchanged.
- **Word and PDF** — pandoc, from a template we control.
- **Draft BRF** — liblouis, on every build. Crude formatting, and that is fine.
- **Release BRF** — Duxbury, from the Word file, once per release.

### The fast loop: liblouis on every build

Its job is **proofing content, not producing braille**. Regenerate a draft BRF
each build so Noel can read the manual on a display and catch a wrong key, a
stale sentence or a missing section immediately. Formatting will be plain.
Nobody is embossing this one, so plain is correct.

This is what makes the slow loop cheap: by the time Duxbury runs, the content is
already known good.

### The release artifact: Duxbury, from Word

**The quality of the braille is decided by the Word template, not by the braille
step.** DBT maps Word *styles* to braille styles. If pandoc emits real Heading 1
/ 2 / 3 and a genuine TOC field, the mapping works and the output is
professional. If pandoc emits hand-formatted bold text that merely looks like a
heading, DBT has nothing to map and the result is flat.

So the pandoc template is load-bearing and belongs under version control with the
rest of the build. Style mapping in DBT gets configured once and reused.

### The Duxbury API is called Swift

Noel, 2026-08-23: it is **Swift**, and it works by connecting to a **Swift
server** that drives Duxbury. His caveat: "it's not very well documented but it's
out there."

**Nobody should design around it from memory, mine included.** Recording the name
here so it is not lost; the shape, licensing terms and platform constraints all
need looking up before any of this depends on them.

**But the client/server shape alone already tells us something worth acting on.**
If Swift requires a Duxbury installation with a server process behind it, then
the release-BRF step can only run on a machine that holds the licence. That means
it is **not** a build-server target — it is a "run on the machine with DBT on it"
target, however well it automates. Worth deciding deliberately rather than
discovering when a build fails somewhere else.

So the design fork is:

- **Swift viable** — the release BRF becomes a build target on the licensed
  machine, and the fast and slow loops collapse into one. Best case.
- **Swift not viable, or not worth it** — the manual Word-import route into DBT
  is the baseline, run once per release. This definitely works and the plan does
  not depend on the other branch.

Build for the second and adopt the first if it earns its place. The liblouis fast
loop is unaffected either way, which is the point of having it.

---

## Two artifacts, not one document at two lengths

The **quick-reference card** and the **full manual** have different geometry and
different selection rules.

A card wants to be a couple of embossed pages, ruthlessly selected. Braille pages
are expensive in paper and in bulk in a way print never is, so the editing
discipline is much harsher than for the PDF. The card is not an abridgement of
the manual; it is a different document that happens to share a source.

Conventional BRF geometry is 40 cells by 25 lines with form-feed page breaks. An
embosser prints it directly; a braille display reads it directly.

---

## Notes and cautions

- **A BRF that looks right in a text editor can still be wrong braille.** Test on
  a real display, and on an embosser before any of it is called done.
- **BRF is already-translated braille stored as Braille ASCII.** It is not a text
  file with a different extension, and nothing in the pipeline may treat it as
  one.
- **Check what the existing multi-braille work established before choosing
  tables** — that thread has already made decisions here.
- The manual is where a name like Smooth Tune or Flywheel becomes real to a user.
  Keep the vocabulary identical to the UI, the help and the Command Finder; the
  manual is a fourth surface for the same words and a fourth chance for them to
  disagree.
