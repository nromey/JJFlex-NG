# The string store — the contract

**Status: ratified 2026-08-22, before any extraction track starts.**

This is the shared contract for #65. Six tracks extract in parallel; if they
each invent their own answers to the questions below, the result is six
different stores. So these are settled here, once, and every track brief
points at this file.

Read with `docs/planning/for-noel/2026-08-22-string-store-plan.md`, which is
the schedule and the rationale. This file is the rules.

## Decision 0 — JSON, not ResX

Plain JSON, loaded with `System.Text.Json` (already used in 28 files, so this
adds no dependency).

This one was settled 2026-08-13 and is the reason the whole job is worth
doing. The deciding factor is **not** localization — it is that Noel edits the
app's voice himself. JSON is readable and editable in a text editor with a
screen reader. ResX is XML behind a Visual Studio designer, and the designer
is the part that does not work.

An earlier version of this plan justified the work as "zero immediate user
benefit — English speakers see no change," and that justification is why the
idea sat for 108 days. It was never true. The one English speaker whose
judgement the app's voice depends on currently cannot change a single word of
it without a build. Externalising converts the prose workflow from "Claude
writes it, Noel reviews it in a document, Claude edits the source" into "Noel
edits the strings." Localization is the side benefit.

## Decision 1 — key shape: hierarchical, behaviour-describing

`connect.smartlink.offer_local_only`, never `settings.tab3.checkbox2`.

Dotted, lowercase, underscore-separated words within a segment. Segments go
from broad to narrow.

Keys survive a redesign; screen positions do not. And keys are read by people:
they reach bug reports and, later, translation files, where the reader cannot
see the screen the key was named after.

**Keys must be literals at the call site wherever possible.** A key assembled
at runtime — `$"connect.{phase}.done"` — cannot be checked until something
executes that line, which for an error path may be a user's machine months
from now. Pay the verbosity and keep them statically checkable. Where a family
genuinely must be built (per-band, per-slice), the track says so in its report,
because those entries' only safety net is a test that happens to reach them.

## Decision 2 — a missing key renders as the key itself

Never empty. Never silent.

Silence is invisible to exactly the operator who most needs the text, and it
is indistinguishable from "nothing was supposed to happen here." A key on
screen or in the ear is a bug report that writes itself.

This decision does a second job nobody planned. Because a missing key renders
as literal text shaped like `connect.smartlink.offer_local_only` — dotted,
lowercase, no spaces, a shape no real utterance ever has — every unextracted
string announces itself in the output transcript and is machine-detectable.
A fallback of empty string would have been invisible to the operator *and* to
the test. Two reasons, one decision, and the second only became visible once
the transcript existed.

## Decision 3 — verbosity ladders are data, but they are RARE today

Where an utterance already picks between Chatty, Terse and Critical, those
tiers belong in the store as a set under one key, so the ladder becomes
reviewable as a ladder rather than as three unrelated literals.

**Measured, 2026-08-22: three ladders exist in the whole codebase.**

- `Radios/ScreenReaderOutput.cs:701` — `SpeakNoRadioConnected`, a `switch`
- `Radios/FlexBase.cs:2294` — the disconnect announcement, a `switch`
- `Radios/FlexBase.cs:11983` — `SilentTxSpokenWarning(VerbosityLevel)`

Plus roughly three binary `CurrentVerbosity == Chatty ? a : b` ternaries
(`RigSelectorDialog.xaml.cs:897`, `MainWindow.xaml.cs:1474`,
`FrequencyDisplay.xaml.cs:883`).

That is about six sites out of 713 `Speak` calls. An earlier draft of the plan
called ladders "the single strongest argument for the whole job" and said
"several utterances already pick between the tiers." As a design goal that is
right. **As a description of the code it is wrong, and stating it as fact
sends six tracks hunting for something that is not there.**

So, explicitly:

- Model the ladders that exist as data. `SilentTxSpokenWarning` is the target
  shape — a pure function over `VerbosityLevel`, with tests asserting each
  tier is shorter than the one above it.
- **Do NOT create new ladders while extracting.** Deciding that an utterance
  deserves a Terse variant is editing, and editing during extraction destroys
  the transcript diff. Report the candidate; do not build it.

## Decision 4 — interpolation uses named placeholders

`{radio}` and `{freq}`, never `{0}` and `{1}`.

A translator reordering a sentence breaks positional placeholders silently,
and "silently" is the word that matters — the string still formats, it just
says something false. Named placeholders fail loudly or not at all.

Every existing interpolated string becomes a named placeholder carrying the
same name as the variable it interpolated, so the mapping is checkable by
reading.

## Decision 5 — six partitions, split for REVIEW, not for speed

The store is an in-memory dictionary. It is exactly the same speed whichever
file it loaded from. Say this out loud, because otherwise someone splits a hot
set across files chasing an imaginary gain, or refuses to split a large one.

The partitions are the six extraction domains: connect and session lifecycle,
audio and DSP, settings and per-radio config, logging and cluster, earcon and
CW vocabulary, and help text.

**Load strategy is a separate axis from partitioning.** The five non-help
domains load at startup and stay resident — frequency and meter announcements
fire many times a second and can never wait on a file. Help text loads lazily
on first `Ctrl+F1`.

## The three checks, and why it takes three

Each one's blind spot is another's strength. None of them is redundant.

- **Static, at build time.** Every `Strings.Get("...")` literal names a key
  that exists in the store. Catches untriggered paths precisely because it
  runs nothing — error branches, rare dialogs, failure messages.
- **Runtime, from the transcript.** No speech event's text may look like a
  key. Catches what static analysis structurally cannot: keys assembled at
  runtime, where no literal ever appears in source.
- **The diff.** Record a session before extraction, record it after, compare
  the speech events. Catches changed text, which neither of the others looks
  at.

A string that is present, reachable, and simply WRONG is only caught by the
diff. A string on a path no test walks is only caught by the static check. A
dynamically-keyed string is only caught at runtime.

## Rules every track brief carries

**Verify your base commit.** Name the SHA. On 2026-08-21 four of five agents
were handed worktrees cut from an old commit, and one built an entire feature
against a library that had been deleted four days earlier. Run
`git log --oneline -1`, fast-forward if stale, and say which SHA you built on
in your report.

**REPORT inconsistencies, never silently normalise them.** Two places saying
the same thing in different words is a finding, and choosing which wording
survives is the owner's call, not the extractor's. #71 was one instance of
that drift and it was found by ear — which does not scale, and is the entire
reason this store exists.

**Do not improve wording while extracting.** Extraction and editing are
separate passes. A track that does both makes the transcript diff useless,
because every intentional change hides a possible accident.

**Produce the transcript diff.** Before and after, same session, for your
domain. That is the deliverable. The changed files are just how you got there.

## Not in scope

- Translating anything. No second locale exists and designing for one without
  a real translator is guesswork.
- Rewording anything. See above, twice.
- Help-doc markdown bodies. Those are already file-based; localizing them is
  by-file (`docs/help/md/<culture>/`), not by-string. Track F extracts help
  *strings* — titles, labels, navigation — not the prose bodies.
- CW literals like `73 de JJF`. They are heard, not read, and "73" has no
  French variant. They live in the earcon/CW partition as a separate category
  and are never machine-translated.
