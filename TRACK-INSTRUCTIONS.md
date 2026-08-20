# Sprint 32 Track C — The analyzer

**Worktree:** `C:\dev\jjflex-32c` · **Branch:** `sprint32/track-c`
**Branched off Track A's Phase 1** (`9954c395`), so the meter inventory is
already in your tree. **Full design:** `docs/planning/active/elmer-meter-pileup.md`,
section "Track C". Read that first.

You are the hardest design work in this sprint. Take the time.

## What Track A already built for you — USE IT, do not rebuild it

On `FlexBase`: `MeterInventory MeterInventory { get; }` (never null, live from
first connect), `ImmutableList<Meter> RadioMeters`,
`event MeterDataDel MeterData` (`object sender, Meter meter, float value`),
`event EventHandler MeterInventoryChanged`.

`Radios.MeterInventory`: `InventoryChanged`; `All`; `Groups`; `Count`;
`Find(string name)` (case-insensitive, **null if absent — that is a first-class
answer for you, not an error**); `ForHandle(string)`; `ForSource(string, int)`;
`ToText()`.

`Radios.MeterReading` gives a rule everything it needs: `Name`, `Description`,
`Source`, `SourceIndex`, `Units`, `Low`, `High`, `Value`, **`HasReading`**,
`UpdateCount`, `LastUpdateUtc`, **`Age`**, **`IsStale(TimeSpan)`**, `ValueText()`.

**Track A already made staleness a first-class state for you:** `Age` and
`IsStale` are null/true when a meter has never reported, and `ValueText()` says
*"no reading yet"* rather than a bare zero. **A present-but-silent meter is a
distinct verdict from an absent one.** Do not collapse them.

**Contract notes:** BIND to `InventoryChanged`, never sample once — the list
grows during registration. Both events fire on FlexLib's meter thread, not the
UI thread; marshal before touching WPF.

## C1. Rules as DATA, never as code

A decision tree of any size hardcoded in C# becomes unmaintainable and untestable.
Express rules as a table or file: preconditions, meter thresholds, verdict text,
remedy. Then rules can be added without a build, tested in isolation, and
eventually shipped through the Data Provider.

## C2. Three-state observability, not two — the rule that matters most

Every stage is **BROKEN**, **HEALTHY**, or **NOT OBSERVABLE FROM HERE**.

Over SmartLink some stages live on the far machine. On some models a meter is
simply absent. **"Checked 14 of 19, could not read 5" is honest. "All good" when
five were unreadable sends the operator hunting the wrong end**, which is worse
than saying nothing at all.

`FlexBase.SilentRadioAdvisory()` already models the honest fallback — read it and
copy that discipline rather than reinventing it. It is the RX sibling of what you
are building.

## C3. Staleness is a reading

Timestamp everything. Track A supplies the timestamps; use them.

## C4. #122 — the TX chain walk, as your first ruleset

Twelve stages. Nearly every observable already exists as a meter, a trace line or
a property — **nothing composes them**, which is why the entire honest-tx-audio
investigation was done by hand over weeks:

1. Is a mic selected, and present
2. Is the mic capturing (dBFS and LUFS already measured)
3. PC-side gain and boost
4. Is PC audio even on — `startRemoteAudioThread` has exactly one caller, so this
   gates everything downstream
5. Opus encoder built at the negotiated rate
6. VITA TX packets leaving, to the right port
7. Radio ACK'd the stream as OPUS
8. **Radio mic input selection — PC, MIC or BAL.** Wrong selection is silent
   transmit with everything upstream healthy
9. **Is a mic profile selected** — empty means no modulation
10. Radio TX chain: mic gain, processor, EQ, TX filter
11. **MicData meter** — the radio's own report of what it hears. A -120 floor
    here with stages 1-9 healthy is the signature that stalled the investigation
12. Forward power and SWR — did RF actually leave

**Report THE FIRST dead stage, in the operator's own words, with the fix.** The
shape is already written elsewhere in the codebase: *"Your radio has no mic
profile selected, so audio from your computer will not be transmitted."*

## C5. The evidence block — this is what makes it worth building

Copyable, for a Flex support ticket: the readings that justify the verdict, with
units, timestamps, firmware version, model and serial. **A tester should be able
to paste it into an email without translating anything.**

`MeterInventory.ToText()` exists and Track A's A6 is building a text export —
check what landed before writing your own.

This turns every user into a competent bug reporter. It costs nothing per user
and it is a real product advantage.

## C6. Start SMALL and honest

Thresholds need field calibration from real radios. What counts as a bad SWR or a
hot PA is **not a guess**. Ship a handful of high-confidence rules as a skeleton
to grow from testers' evidence — not a hundred speculative ones. **Report which
thresholds you had to invent so they can be checked at the bench.**

## C7. Placement — ruled by Noel, do not relitigate

**Audio Workshop, as new tabs.** And **move `SilentRadioAdvisory` in from
Settings > Audio** to join its TX sibling — they are the same tool pointed in
opposite directions and should not live in different rooms. Leave a pointer
behind where it used to be, the same courtesy the CW notifications move set as
precedent.

**Move the call site; do NOT change the method's signature.**

Track A split `AudioWorkshopDialog` into per-tab partial files — add your own
`AudioWorkshopDialog.<YourTab>.cs`. Do not edit the shell or the other nine.
Track G is restructuring the dialog's navigation; **build your tab so it works
under an enumerated tab set rather than a hardcoded one.**

## You own these files

The analyzer files (new), your Workshop tab partial, and `SilentRadioAdvisory`'s
call site. **Do not edit `FlexBase`'s meter section — Track A's, and done.**

## Rules that apply to every track this sprint

- **Reuse the symbols you are told to reuse. If you conclude one should MOVE or
  CHANGE SIGNATURE, report it — do not do it.**
- **NO tables, diagrams or ASCII art** in anything you write, including the
  analyzer's own output. Prose or bullets. The primary user is blind and uses
  NVDA — and this tool's entire purpose is to be read aloud.
- **Verify builds by the `N Error(s)` summary line**, never by grepping for the
  word "error". Expect ~609 pre-existing warnings.
- Commit per logical chunk with `Sprint 32 Track C: <description>`.
- Do not merge anything into your branch. The orchestrator runs the merge train.

## Build

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
```

Close any running JJFlexRadio first — `Radios.dll` locks.

## Definition of done

Rules live in data, not code; three-state observability with "could not check"
reported honestly; the TX chain walk works as the first ruleset; the evidence
block is copyable and complete; the RX advisory has moved in with a pointer left
behind; clean x64 build. **Report every threshold you invented and every stage
you could not make observable.**
