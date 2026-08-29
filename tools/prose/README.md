# prose — edit the words the application says as writing, not as code

The Fixer's operator-facing prose lives as inline C# string literals, wrapped
across lines and joined with `+`. Tuning three words in the middle of that means
finding the right literal, minding the escaping, minding the wrap, and never
once seeing the sentence an operator will actually hear. For someone editing
with a screen reader that is friction paid on every edit, and there are hundreds
of strings.

This is the round trip out of that and back.

## What you type

```
tools\prose\prose extract
```

Writes two files:

- **`tools/prose/fixer-prose.md`** — the one you edit. A heading per sentence,
  the sentence underneath it, and nothing between the two.
- **`tools/prose/fixer-prose-read.md`** — the one you listen to. The same
  sentences with the values filled in, no keys, no braces, no markup. For
  reading the whole surface aloud and hearing where it is stilted.

Edit the first file. Then:

```
tools\prose\prose apply
```

`prose check` says what applying would do and writes nothing. `prose skipped`
lists every string the tool declined to offer, and why. `prose read` refreshes
only the listen-through file.

## What it guarantees

**A round trip with no edits changes no byte of any source file.** A sentence
whose words are unchanged produces no edit at all — not a reformat, not a
re-wrap. Proved on the real files by
`Prose.Tests.RoundTripTests.ApplyingAnUneditedFileChangesNoByteOfAnySourceFile`,
which hashes every source file before and after, and by its positive control
next door, which changes one sentence and proves exactly one file moves.

**Nothing is half-applied.** Every edit is validated, every file is rebuilt in
memory and re-parsed as C#, and only then does a byte reach disk. A refusal
anywhere stops the whole run and names the sentence and the problem.

**Somebody else editing the same file is safe.** Three things are compared, not
two: what you wrote, what the file said when you extracted it, and what the code
says now. A sentence you did not touch is left exactly as the code now has it,
so another author's work in the same file survives a run of this tool. A
sentence you DID touch that has also changed underneath you is refused, with the
new wording put in front of you.

## What the editing file looks like

    ## Stage 0 Audio setup — what this check does

    Reads the open audio stream directly: host API, input and output device, …

    > fixer.stage.audio-setup.explanation

Two heading levels, so both of a screen reader's navigation planes mean
something: 1 walks the sections, 2 walks the sentences. Under a heading the very
next thing is the words. Everything the tool needs sits on lines beginning with
a quote mark, *after* the words, where heading navigation skips it.

A sentence is one line however long. Hard wrapping would mean changing three
words rewraps a paragraph, and would leave the tool guessing whether a line
break was meant. (Read the file back after hard-wrapping one anyway and it still
does the right thing.)

Where a sentence has a value the program fills in, it shows in braces and gets
two extra quoted lines:

    ## Stage 2 Transmitter check — what pressing Run will do

    Running this counts down with three tones, then keys the radio's own tune
    carrier{AtInto} for about {SecondsPhrase}.

    > Reads as: Running this counts down with three tones, then keys the radio's
    > own tune carrier at 25 watts into ANT1 for about two seconds.
    > Keep {AtInto} and {SecondsPhrase}, in that order.
    > fixer.stage.transmitter-check.describe-run-action

Keep every brace and keep them in the same order; change every word around them
freely. Drop one and the run is refused by name.

## The two things it does that a simpler tool would not

**It shows ASSEMBLED sentences.** One sentence in this codebase is routinely
four literals across four lines, or several `.Append()` calls with a live value
spliced between them. Handed out literal by literal, each fragment reads
perfectly and none of them shows whether they join into a sentence or into
nonsense — which is exactly the defect class this repo keeps paying for
(`feedback_read_assembled_sentences_not_source_lines`). So the unit here is the
whole statement, and the `Reads as` line puts real values in the gaps.

**It says when a fragment is glued to its neighbour.** Where a piece of text
runs on from the one before it, the entry says so — because a join is precisely
where a connective goes missing, and neither half shows it.

## How it finds the words

Roslyn, not a regex. The strings are concatenations split across lines, some
inside fluent chains with live values between the halves — the shape a text scan
gets wrong, where getting it wrong means writing malformed C# into the program
an operator controls a radio with. The syntax tree gives exact spans, so a
rewrite touches the literal and nothing else.

Keys come from the SEMANTICS around a string, never from a line number: the id
of the stage it belongs to, the constructor parameter it fills, the property it
is assigned to. That is what lets an edit survive the file being rewritten
underneath it. Where two entries genuinely collide, every one of them moves to a
longer key together, so a key never depends on which of the two was read first.

## Adding a surface

`surfaces/fixer.json` lists the files, the order a person meets them, and the
three judgements a syntax tree cannot make on its own: which files are in scope,
which one-word strings are prose (`StatusPhrase` returning "passed" and
`StatusOf` returning "notrun" are the same shape and one of them is heard by a
person), and what a realistic value looks like. Copy it and change the list.

`Surface` is the pluggable seam. Everything above it — the editing file, the
assembled sentences, the round trip, the refusals — is about WORDS and knows
nothing about where they are stored. `CSharpSource` is today's only reader; a
reader over `Radios/Lexicon/*.json` drops in beside it with the editing view
unchanged. That matters because the Lexicon holds 2,500-odd strings that are
just as hard to hand-edit as these, and because it is how the Fixer's own prose
should eventually be stored.

## Tests

```
dotnet test tools\prose\Prose.Tests\Prose.Tests.csproj
```

Deliberately not in `JJFlexRadio.sln`, same footing as `tools/refvoice`. They
run against the real Fixer prose copied into a throwaway tree, and write nothing
into the working copy.

## Known gaps

`prose skipped` names every one on the day you run it. The ones worth knowing
about:

- **An element built across several statements** leaves half a tag in each, and
  half a tag is not a sentence. In `FixerPage.NextControl` that costs two real
  operator-facing phrases — `"Next: "` and `"Go to the report"` — which are
  therefore not editable here. Building that control in one statement would
  bring both in, and is a one-line change to make after Sprint 41 merges.
- **Text with a line break in it** is left alone; the editing file has no way to
  show one.
- **A verbatim string** (`@"…"`) is never rewritten: re-emitting its words would
  change its form as well as its content.
