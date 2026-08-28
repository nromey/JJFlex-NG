# Reader capture — an instrument that records what the screen reader RECEIVED

Task #299. Sprint 37 Track B.

JJ Flexible's own trace proves what the application **sent**. It writes "Spoke"
whether or not anything was ever heard. On 2026-08-27 five separate theories
about a JAWS silence were each killed by the operator switching readers and
listening, one at a time, because no instrument we owned could tell
"we sent it and the reader spoke it" from "we sent it and nothing happened".

This is the other half of that measurement. It runs inside the screen reader
and records what the reader **received** and what the reader **emitted**,
speech and braille, with timestamps, in a form an application can diff against
what it sent.

It is a debugging instrument for us and for testers who agree to run it. **It
is not something an operator installs in order to use JJ Flexible**, and it
should not be allowed to grow in that direction.

## Where it lives

In this repository, at `tools/readercapture/`, alongside the other instruments
that are not part of the product (`rigmeter`, `uia-probe`, `voicelab`,
`radiocheck`). It is referenced by **no project file** and is not compiled,
packaged, or shipped by any build. It sits in the same tree as the trace it
exists to be compared against, which is the whole point — a capture and a trace
that live in different repositories drift apart.

- `nvda/globalPlugins/jjfcapture/` — the NVDA global plugin.
  `_record.py` inside it is the shared capture core: the record format, the
  writer, the ring, and the positive-control bookkeeping.
- `nvda/manifest.ini` — only used when packaging a `.nvda-addon` for a tester.
- `jaws/jjflexible.jss` — the JAWS application script, named for
  `jjflexible.exe` so JAWS loads it only inside JJ Flexible.
- `jaws/jjflexible.jkm` — its key map, application scoped.
- `jaws/jaws-ingress-control.ps1` — the strong positive control for JAWS.
- `install-nvda.ps1`, `install-jaws.ps1` — installers.
- `verify/` — three checkers that run without a screen reader.

## Installing

### JAWS (do this one first; JAWS is where the open faults are)

    powershell -File tools\readercapture\install-jaws.ps1

It copies `jjflexible.jss` and `jjflexible.jkm` into the **user** settings
folder (`%AppData%\Freedom Scientific\JAWS\<version>\Settings\enu\`) and
compiles with `scompile.exe`. The user folder, not the shared one, because JAWS
updates overwrite the shared tree.

Nothing touches `default.jss`. Every binding and every override is scoped to
`jjflexible.exe`, so JAWS behaves exactly as before in every other program.
Uninstalling is deleting three files.

JAWS loads the new binary the next time JJ Flexible comes to the foreground, so
switch away and back.

Keys, inside JJ Flexible only. `Insert+Shift+J` copies the capture.
`Insert+Shift+K` plants a marker. `Insert+Control+Shift+J` pauses and resumes.
`Insert+Alt+J` runs the positive control. `Control+J` is deliberately untouched
— it is JJ Flexible's own leader key.

### NVDA

    powershell -File tools\readercapture\install-nvda.ps1

The default route copies the plugin into NVDA's **developer scratchpad**
(`%AppData%\nvda\scratchpad\globalPlugins\jjfcapture`). Nothing is installed and
nothing is registered; removing it is deleting a folder. The scratchpad has to
be switched on in NVDA's Advanced settings, and **the installer will not switch
it on for you** — that is the operator's live reader configuration. Once it is
on, `NVDA+Control+F3` reloads plugins without restarting NVDA.

For a tester who will not enable a scratchpad, `install-nvda.ps1 -Package`
builds a `.nvda-addon`, which installs the ordinary way and restarts NVDA.

Keys, everywhere, all rebindable under Input Gestures in the category
"JJ Flexible capture": `NVDA+Shift+J` copies, `NVDA+Shift+K` marks,
`NVDA+Control+Shift+J` pauses and resumes, `NVDA+Alt+J` runs the control.

## What it records

One JSON object per line, in `%LOCALAPPDATA%\jjfcapture\`, named
`jjfcapture-<reader>-<timestamp>-<pid>.jsonl`. Both readers write the same
format, so one parser reads both and a JAWS capture and an NVDA capture of the
same fault can be compared directly.

Every record carries a format version, a strictly increasing sequence number, a
local wall clock with its UTC offset, a monotonic millisecond offset from the
start of the session, the reader name, a channel, and an event.

The channel is `speech`, `braille`, or `meta`. The event is one of:

- `received` — the reader was handed this. On NVDA that is
  `speech.extensions.pre_speech` and `braille.handler.message`. On JAWS it is
  the `BrailleString` override.
- `emitted` — the reader actually pushed this at the human. On NVDA that is
  `braille.pre_writeCells`. On JAWS it is what turns up in `GetSpeechHistory`.
- `canceled` — NVDA threw queued speech away (`speech.extensions.speechCanceled`).
- `session`, `marker`, `selftest`, `error` — bookkeeping on the `meta` channel.

Speech records also carry the NVDA priority, the symbol level, the foreground
application, whether the focus object was in sleep mode, and the class names of
any non-text speech commands in the sequence. Braille records carry the cell
count actually written.

`_record.read_jsonl(path)` reads a capture back in one call.

## The question this exists to answer

After a screen-reader switch, an interrupt-mode utterance leaves our code and
never becomes audible (#298). Did the reader receive it and drop it, or did it
never arrive? Those need opposite fixes and nothing we own can currently tell
them apart. Lay the capture beside the application trace:

- In the trace, and in **neither** channel here — it never arrived. Look at the
  transport, not the reader.
- `speech received` immediately followed by `speech canceled` — it arrived and
  was thrown away. Look at interrupt handling, not the transport.
- `speech received` with `sleeping: true` — it arrived and the reader was
  asleep in that application.
- `braille received` with no matching `braille emitted` — it reached the reader
  and died before the display.

## The positive control, which is not optional

**An instrument that records nothing is indistinguishable from a reader that
received nothing.** Everything about this design bends around that fact.

A capture is not evidence until a control token has been emitted and has come
back **inside the same session**. The instrument enforces this itself: the
first line of every exported capture is the verdict, and when no control has
passed it says so in those words and tells the reader that an empty capture
proves nothing. The copy key says it out loud too. A token that arrived
*before* the control was armed does not count.

There are two controls, and they prove different amounts. The instrument
records which one you ran rather than letting a reader over-read the result.

**The internal control** (`Insert+Alt+J` on JAWS, `NVDA+Alt+J` on NVDA) speaks a
random token from inside the reader. It proves the hooks are attached and the
capture is recording. It says nothing about whether the door the application
comes through is open.

**The ingress control** (`jaws/jaws-ingress-control.ps1`) sends a token from
outside, through the exact two entry points JJ Flexible uses. Both were read out
of Prism's source rather than assumed: speech goes through
`IJawsApi::SayString(text, flush)` and braille through
`IJawsApi::RunFunction('BrailleString("...")')`, both in
`source/backends/jaws.cpp`. A token that arrives this way and appears in the
capture has proved the whole path. There is no NVDA equivalent, because NVDA
does not ship `nvdaControllerClient.dll` with the reader.

## Verifying it without a screen reader

Three checkers, in `verify/`. **Each one carries its own positive control** and
refuses to certify anything if it cannot first prove it is able to fail.

`verify_jaws_script.py` reads `Scripts/enu/builtin.jsd` from the installed
JAWS — the shipped machine-readable catalogue of every built-in function and
its parameters — and checks that every function `jjflexible.jss` calls exists
and is called with a legal number of arguments, and that every constant it
names is defined. Its own control feeds it an invented function name, a wrong
argument count, and an invented constant, and requires it to flag all three
while leaving a valid call alone. This is a name and arity check. **It does not
prove the script compiles.**

`verify_nvda_symbols.py` opens the installed NVDA's `library.zip` and proves
every symbol the plugin uses is present in that build. Its control asks for a
name that is certainly there and one that cannot exist, and fails if it cannot
tell them apart. **It proves the names exist, not that the plugin loads.**

`verify_record_core.py` runs the shipping capture core against a scratch
directory and asserts the honesty properties: no-control captures say so,
failed-control captures say not to trust them, stale tokens do not count,
what is written reads back identically, and the ring trims from the old end
while the file keeps everything. Its own control asserts a deliberate failure
first, so a screen full of passes is not an empty loop.

Run all three:

    python tools\readercapture\verify\verify_jaws_script.py
    python tools\readercapture\verify\verify_nvda_symbols.py
    python tools\readercapture\verify\verify_record_core.py

## What is NOT verified, and must be before this is trusted

Being explicit here is the point of the whole exercise.

- **Neither half has been run inside a screen reader.** The JAWS script has not
  been compiled and the NVDA plugin has not been loaded. The operator's NVDA was
  live throughout and taking it over would have been the exact disruption this
  instrument exists to avoid causing.
- **The JAWS `BrailleString` override is the least certain part.** The reasoning
  is documented: FSDN's calling hierarchy says a function is resolved in the
  application script file before the built-in, and Prism sends braille through
  `RunFunction`. But whether `RunFunction` honours an application-scoped
  override has not been observed. If it does not, braille still works — the
  override forwards to `Builtin::BrailleString` first and logs second, so a
  fault in the logging can never cost the operator their braille — but nothing
  is recorded on the braille channel. The ingress control tells you which of
  those you are looking at in one press.
- **JAWS speech timestamps are accurate to the poll, not to the utterance.**
  `SayString` from an external application does not pass through the script
  layer, so speech is observed at the other end, by polling
  `GetSpeechHistory`. `ScheduleFunction` resolution is tenths of a second and
  the poll is set to two, so a JAWS speech timestamp is worth about 200 ms.
  Braille timestamps are exact, because braille is observed on arrival.
- **The direction JAWS speech history grows is not known.** The poll handles
  growth at either end and flags a re-emission as `resync` when the buffer rolls
  or is cleared, rather than guessing quietly. First real capture will settle
  it; the flag exists so that the guess is visible rather than silent.
- **NVDA gesture collisions.** A scan of the installed NVDA's built-in gestures
  found none containing `+j`, and that scan did find 103 other gestures, so it
  was looking. It was not exhaustive.

## Notes for whoever picks this up

**There was no `brailleElement` add-on to ride.** The brief said to ride one
"already committed". What exists is `brailleElementDemo`, a 262-line research
prototype committed once on 2026-04-29 in `b28af864`, reachable only from the
branch `track/braille-research`, at `docs/planning/track-c/prototype/`. It is
not on `main`, not in any working tree, not installed in NVDA, NVDA-only, and
its own README says in as many words that it is not to be merged into the
production tree. It demonstrates braille element rendering and cursor routing,
which is a different job from capture. Riding it would have meant reviving an
unmerged prototype, on the wrong reader, to do something it was not built for.

**On NVDA's Speech Viewer showing nothing on 2026-08-27 while speech was
audible.** Not resolved, but narrowed. In NVDA 2026.1, `speechViewer.append`
`SpeechSequence` is called from exactly one place, `speech/speech.py`, and
`nvdaController_speakText` reaches that same function. So anything NVDA itself
speaks should appear in the viewer. That makes "the viewer was not actually
updating" and "the audio did not come from NVDA's speech path at all" the two
live candidates, and leaves "NVDA received it by a route the viewer cannot see"
looking unlikely. This instrument settles it: run the internal control with the
Speech Viewer open, and if the token appears in the capture but not in the
viewer, the viewer is the broken thing.

**Out of scope, named rather than fixed.** There is no NVDA ingress control,
because NVDA does not ship its controller client library; building a standalone
probe against Prism's RPC stubs would settle it and is a separate piece of work.
Nothing here diffs a capture against a JJTrace file automatically — the formats
are deliberately compatible so that tool can be written, and #277 is the other
half it would need. And this instrument watches the reader only: it says nothing
about what the audio device did after the synthesiser, which is a third
measurement again.
