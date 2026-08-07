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

## Design decisions (appended by Track J, 2026-08-07)

**Chosen shape: (a) sorted list, PLUS identity-keyed letter entry, PLUS
identity re-derivation of stored positions.** Neither pure shape was
sufficient on its own:

- Pure (a) — sort `mySlices` by radio index — makes position ORDER equal
  letter order (the menu canary passes by construction, with zero changes
  to menu code owned by Tracks A/I, and every dense positional consumer —
  `ValidVFO`, `NextVFO`/`PriorVFO`, `CycleVFO`, menu iteration, the
  scratch-setup loops — keeps working unchanged). But position still does
  not equal letter under MultiFlex when our slices don't start at radio
  index 0 (we own C and D only: position 0 = letter C), so positional
  `ch - 'A'` entry would still mistarget.
- Pure (b) — make "VFO" the radio index — breaks every dense-range
  consumer, including menu builders in files this track is forbidden to
  touch. Rejected on ownership grounds and blast radius.

So: a "VFO" remains a POSITION in `mySlices` (dense, 0..N-1), the list is
insert-sorted by `Slice.Index`, and the three rules that make letters
honest are: (1) letter-addressed entry resolves through the new
`SliceIndexToVFO`/`LetterToVFO` (never `letter - 'A'` arithmetic on
positions); (2) stored positions are re-derived from Slice OBJECTS after
every roster mutation (never positional decrement arithmetic); (3) every
queued command captures its target Slice at call time, so the radio
command queue can never resolve a stale position.

**Identity matching is by `Slice.Index`, not the `Letter` string.** The
radio letters slices as 'A' + index universally; `Index` is assigned at
slice creation (from the status message key) and is guaranteed present
before `SliceAdded` fires, while `index_letter` arrives as a property and
could in principle lag. Announcements always read `Slice.Letter` (radio
truth); only ENTRY maps letter → index arithmetically, and only for radio
indices — which is identity-correct even for slices that don't exist yet
(used by the "Slice D not yet created" messages).

**`RXMode` targets `VFOToSlice(RXVFO)`, not `theRadio.ActiveSlice`.** The
slice the app has been announcing is the one mode changes must land on;
ActiveSlice can diverge transiently (queued `Active = true` not yet
processed) and is not even guaranteed to be OURS under MultiFlex. Fallback
to ActiveSlice only when RXVFO resolves to nothing AND ActiveSlice passes
the `myClient` filter — the old code had no ownership check at all.

**`ReleaseAllExtraSlices` keeps the slice the user is ON, by object.**
The kept slice retains its letter (releasing A while on B leaves you on
B). The FreqOutHandlers doc comment claiming "except the first / ends on
Slice A" was the wrong half — code behavior (keep active) was Noel's
intent, so the comment was fixed, not the code. Removals go through a new
identity overload `RemoveSlice(Slice)`; the positional `RemoveSlice(int)`
resolves to an object immediately and delegates, so no caller changes.

**`sliceRemoved` fallback when the RX/TX slice itself is removed:**
position 0 (lowest letter remaining), matching the previous behavior.
Deliberately NOT "nearest letter" — a removal of your own current slice
only happens through paths that already chose a successor first.

**Digit-select stays ordinal (position).** '0'-'9' on the VFO/Slice
fields still mean "n-th of MY slices" — a defensible semantic distinct
from letters, and the announcement reads the true letter so it cannot
lie. Under sorted order digits are coherent (0 = lowest letter). Letters
are identity; digits are ordinals. Not changed.

**Miss announcements added (no silent keystrokes):** direct-select
letters that don't resolve now speak "Slice X not created", "Slice X is
in use by another station" (new `SliceIndexOwnedByOther` door), or
"Slice X not available on this radio" — previously the VFO/Slice-field
sites swallowed the key silently. JumpToSlice gained the same
in-use-by-another-station distinction.

**Split memory (`=` toggle) stores identity.** `_priorSplitTxVfo`
(position, stale across churn) became `_priorSplitTxSliceIndex` (radio
index); restore degrades gracefully to plain transceive if that slice is
gone.

**`NewSlice`'s VFO restore captures radio indices** before the add and
re-resolves after — the sorted insert can shift positions downward-letter
inserts, which the old stored-int replay would have mistargeted (a bug
this track's sort would otherwise have INTRODUCED; called out for
reviewers).

**GetProfileInfo restore semantic change (deliberate):** when the old
RX/TX position was noVFO before free-slice allocation, we no longer force
`_RXVFO` back to noVFO afterwards — we keep whatever the slice-added
handlers derived for the new slices. Restoring "no active slice" over a
freshly activated slice was protecting nothing.

**Left alone, flagged for the orchestrator (out of Track J's lane):**
- `NativeMenuBar` Selection submenu iterates `TotalNumSlices` POSITIONS,
  so other clients' slots render as numeric labels ("Slice 2") that
  silently no-op on click; and "Release Slice X" bakes the letter into
  the label at menu-build time but acts on the CURRENT RXVFO at click
  time (label/action drift if the user switched slices since the last
  rebuild). Menus belong to Tracks A/I.
- `FlexBase.SliceState(int)` is a positional mine/others classifier with
  ZERO callers — dead API, nonsensical under MultiFlex ordering; left in
  place per no-opportunistic-cleanup, recommend deletion at merge.
- `PanAdapterManager.panParameterSetup` iterates `mySlices[i]` unlocked
  to unhook handlers — position-agnostic (touches every slice), no letter
  hazard; only the queued TX-frequency write there was identity-fixed.

**Verification status:** builds clean (0 errors) at every commit; the
repro sequence (create 4, release extras, re-create, press D / menu Mode /
arrow) was traced on paper against the new code and every step lands on
the letter-true slice, but LIVE verification on the 8600 is pending
Noel's radio session. Trace greps in the queue entry (`sliceAdded`,
`RXVFO:`, `ActiveSlice:`, `VFOToSlice:null`) all still match — the
sliceRemoved adjust line now reads "VFO re-derive" instead of "VFO
adjust".
