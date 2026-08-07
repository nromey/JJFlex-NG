# QB Track J — Slice identity: position vs letter

**Recommended model: Fable.** Core correctness in the slice-mapping layer
with subtle event-ordering semantics. This is the deepest bug of the
2026-08-07 live session — treat it with test-first discipline and
document every semantic decision in a "Design decisions" section here.

## Context

One of the 2026-08-07 queue-burn tracks (plan:
`docs/planning/active/nightowl-pileup-ragchew.md`; queue: Track J
section). JJ Flex is a screen-reader-first FlexRadio client. Live
symptoms, all deterministic: Slice menu → Mode changed the WRONG slice;
arrowing to "slice D" showed slice C in the title; pressing D always
landed on C. One root cause.

## The defect (code-read, high confidence — verify then fix)

- `mySlices` is a creation-ordered `List<Slice>` (`FlexBase.cs:8828`,
  appended in sliceAdded at :5837). The app's "VFO number" is a POSITION
  in that list: `VFOToSlice(vfo)` = `mySlices[vfo]` (:6446);
  `SliceToVFO` linear-searches by radio `Index` (:6458).
- User-facing letters come from the RADIO's `Slice.Letter` (radio index).
  Position == letter only while creation order matches radio order —
  create/release churn breaks it permanently.
- Positional-letter arithmetic then lies or mistargets:
  - Direct-select treats the letter as a position: `ch - 'A'`
    (`FreqOutHandlers.cs:1292`).
  - `JumpToSlice` FABRICATES the announced letter: `(char)('A'+index)`
    (`KeyCommands.cs:2152`) — speaks "Slice D active" while activating
    whatever sits at position 3.
  - The mode menu targets `theRadio.ActiveSlice` (`FlexBase.cs:6860` via
    `RXMode`) — the radio's truth — so mode changes land on whatever the
    position actually mapped to.
- Adjacent bug class: `RXVFO`/`TXVFO` are stored POSITIONS — they go
  stale when a removal shifts the list (restore paths at
  `FlexBase.cs:10294-10360`).
- `ReleaseAllExtraSlices` (`FlexBase.cs:7263`) keeps position-`RXVFO` —
  Noel's lived "it keeps slice A, not my slice" experience is this bug.

## The fix

**The letter is the identity.** Two acceptable shapes — pick one and
justify it in Design decisions:

- (a) Keep `mySlices` SORTED by radio slice index (insert-sorted in
  sliceAdded), so position == radio order == letter, and every existing
  positional consumer becomes correct; or
- (b) Replace positional lookup with letter/index-keyed mapping
  (`VFOToSlice` resolves via `Slice.Index`), leaving list order
  irrelevant.

Shape (a) is less invasive but positions still shift on REMOVAL — either
way you must fix the stale-`RXVFO`/`TXVFO` class: re-derive them from the
slice objects (letter) after any roster change, never trust a stored int
across add/remove.

Then audit EVERY positional consumer:
1. `VFOToSlice` / `SliceToVFO` / `ValidVFO` / `VFOToLetter` semantics.
2. Direct-select `ch - 'A'` — 'D' must reach RADIO slice D or announce
   "Slice D not created" (never silently act on a different slice).
3. `JumpToSlice` — the announced letter must come from the actual slice
   (`Slice.Letter`), never arithmetic; same for its "not yet created"
   message.
4. `RXVFO`/`TXVFO` setters + every restore path (10294-10360, 10348).
5. `ReleaseAllExtraSlices` — must keep the slice the user is ON (by
   letter), move TX onto it, and announce the TRUE letter. This closes
   the queued keep-active discrepancy; note the handler doc comment at
   `FreqOutHandlers.cs:1836` says "except the first" — fix whichever of
   code/comment is wrong post-change.
6. Cycle/arrow paths (`CycleVFO`) — announcements must be true letters.
7. The sliceAdded/sliceRemoved handlers and `_RXVFO = SliceToVFO(s)`
   assignments (4714, 5138, 5845-5850) — coherent under the new model.
8. Grep for any other `mySlices[` positional indexing and `'A' +`
   arithmetic you haven't covered (`KeyCommands.cs`, `NativeMenuBar.cs`,
   `FreqOutHandlers.cs`, `FlexBase.cs`).

## Verification

- Regression canary: the Slice menu Selection submenu labels (true
  letters in position order) — after the fix they MUST read in letter
  order after any create/release sequence.
- Repro sequence to test against (Noel's live session): create 4 slices,
  release extras, re-create, then: press D → title AND announcement say
  D; menu → Mode → changes slice D; arrow through slices → announcements
  match the title at every step.
- Multi-client awareness: another client's slice churn arrives through
  the same events — the mapping must stay coherent when slices you don't
  own appear/disappear (mySlices holds only OUR slices — verify the
  ownership filter still holds under the new model).
- Noel's trace (when it lands) shows the original trigger — greps:
  `sliceAdded`, `RXVFO:`, `ActiveSlice:`, `VFOToSlice:null`.

## Ownership boundaries (do not cross)

- Yours: FlexBase slice-mapping regions (~4700-5150 status handlers,
  6440-6550 mapping, 8828, 10280-10360), FreqOutHandlers slice-select
  paths, `JumpToSlice` in KeyCommands.
- NOT yours: FlexBase ~7700-7960 (Track G's monitor/record region), the
  TX getter guards (G), menus (A/I), the Keys surface (H). If a fix
  wants to touch a menu label path, note it for the orchestrator instead.

## Build & verify

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```
Timestamp must be fresh. This track merges FIRST — keep the diff tight
and reviewable; no opportunistic cleanups outside the audit list.

## Commit style

Commit per audit item or coherent group: `QB Track J: <what changed>`.
Push to `origin` (never `upstream`). Report completion to Noel when done.
