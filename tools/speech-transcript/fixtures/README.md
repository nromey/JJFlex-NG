# Speech transcript fixtures

Every file here is **hand-authored**. None of them is a captured NVDA log, and
none of them may ever be replaced by one.

## Why not real captures

An NVDA log records everything NVDA spoke in **every** application — window
titles, mail, whatever was on screen — and it echoes typed words as well. It is
personal data by construction rather than by accident. **This repository is
public.** Real captures go to `C:\Users\nrome\JJFlex-private\nvda-logs` or the
NAS, and `archive-nvda-logs.ps1` puts them there.

So these fixtures contain only JJ Flexible's own utterances, with a placeholder
callsign (`N0CALL`) and a placeholder station name (`benchradio`). No real
callsign, no real station, no operator keystrokes.

## Why they are still authoritative

They reproduce **timings that were measured**, and the measurements are on the
record in the task register with their own timestamps. The fixture is a
reconstruction of the mechanism, not an invention of it:

- `known-bad-554-salvage-loop.nvdalog` carries #554's clock exactly:
  four lines handed over in 4 ms at 20:32:42.393 through .397, then the same
  block re-spoken 611 ms and 606 ms after an interrupt.
- `known-bad-521-contradiction.nvdalog` carries #521's captured sequence — a
  connection claimed between two disconnect announcements, and the picker
  inviting a connection that has already begun.
- `known-good-connect.nvdalog` is the negative control. A rule that fires on
  everything passes both known-bad files; this one is what makes those passes
  mean something.
- `parser-shapes.nvdalog` exercises the parts of NVDA's payload format that
  are easy to get wrong: double-quoted strings, SSML mark interleaving, typed
  characters, and a midnight crossing.

## Why the extension is `.nvdalog` and not `.log`

`.gitignore` line 108 ignores `*.log`, and that is a safety net worth keeping: a
genuine `nvda.log` dropped into this tree cannot be committed by accident.

Committing these four as `.log` would have needed `git add -f`, which produces a
file that is **tracked and ignored at the same time** — exactly the
contradiction #556 documents, where ignore rules stop applying to an
already-tracked file and it drifts with nothing complaining. The extension keeps
the safety net intact and needs no override.

`SpeechTranscriptHarnessTests.OnlyHandAuthoredFixturesLiveInTheRepository`
scans both extensions, so a real capture dropped in here fails a test even
though git would already have refused it.

## The interrupt lines

NVDA logs **no cancellation event at IO level** — every `CancellableSpeech` in
a measured 24,801-line log reads `still valid`. The `(interrupt)` annotations
in #554's register entry are the reader's own note, not a log line. Here they
are represented as the input gestures that cause an interrupt, which is the
mechanism the entry describes. The harness infers interrupts the same way, and
says so.
