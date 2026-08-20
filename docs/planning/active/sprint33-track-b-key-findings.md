# Sprint 33 Track B — key map findings

Findings only. Nothing here is fixed; repairs get triaged one at a time
afterwards, per the track brief.

Everything below was found without touching the live UI. The dynamic half — the
sweep that presses all 243 chords — is built and waiting on an authorised run.

---

## The Alt-chord trap: clean, and here is why that is a real answer

The 2026-08-13 failure was a handler testing `e.Key == Key.L` while Alt was
held. WPF reports `Key.System` in that case and puts the real key in
`e.SystemKey`, so the comparison could never be true. It compiled, it reviewed
clean, and the chord was never handled.

`jjprobe altcheck` scanned 766 source files. Fifteen reason about Alt at all.
Two came back as suspects and both are false positives, but the second is
interesting enough to be a finding in its own right.

**Every Alt binding that ships reaches its handler through a path that
normalises.** Specifically:

- The **35 Alt chords in the KeyCommands registry** — Alt+Up and Alt+Down for
  band, the whole Alt-letter mode set (Alt+U, Alt+L, Alt+C, Alt+A, Alt+F,
  Alt+D), the Alt-letter log field jumps, Ctrl+Alt+M/P/V for meters, and the
  rest — are all dispatched through `WpfKeyConverter.ToWinFormsKeys`, whose
  first line is `e.Key == Key.System ? e.SystemKey : e.Key`. Structurally
  immune.
- The **filter Alt chords**, Alt+[ and Alt+] for preset cycling, run through
  `FreqOutHandlers.HandleFilterHotkey`, which starts with `var key = RawKey(e)`
  — the same normalisation. Immune.
- The **Audio Workshop's Alt+E, Alt+I and Alt+R** are not handlers at all. They
  are WPF access keys: the buttons carry `Content="_Export"`, `"_Import"` and
  `"_Reset"`, and AccessKeyManager delivers them. Immune to this trap, but they
  have their own failure mode — an access key only fires if its button is in the
  active access-key scope and on screen — so they still need pressing.

Suspect one, **`RadioNumberBox.xaml.cs`**, is correct code: the Alt check is a
bail-out that forwards the event to the parent window *before* the
`switch (e.Key)` is reached. The scanner cannot see that ordering.

Suspect two, **`DefineCommandsDialog.xaml.cs`**, is the same misunderstanding of
`Key.System` in the opposite shape — and worth recording even though it is
harmless today:

```csharp
if (e.Key == Key.Tab || e.Key == Key.System || ...)
    return;
```

That is a hotkey *capture* box discarding `Key.System` as a "modifier-only key".
Since every Alt chord arrives as `Key.System`, **no Alt binding could ever be
captured through that dialog.** It is unreachable — `DefineCommandsDialog` has
zero references anywhere in the repository and was superseded by `KeysDialog`,
which normalises correctly at line 263. So this is dead code carrying a live
trap, and the finding is that it would reintroduce the bug the day anyone
revived it.

**Verdict: the Alt-chord trap exists nowhere in the current key map.** The
static scan is now a repository tool, so the next time somebody adds an Alt
binding the check is one command rather than a memory.

The caveat the tool prints itself, and it is not boilerplate: a clean static
result says no file has the *shape* of the bug. Only pressing the key proves the
chord arrives.

## The `Keys.None` verdict: already answered, and now machine-checkable

Twenty-nine registry commands ship with no key. Sprint 32 Track G annotated
every one of them, and `jjprobe unbound` reads that roster straight out of the
built `JJFlexWpf.dll`:

- Reserved — 6. The six audio-gain slots freed in Sprint 29 Track F. The empty
  slot is the decision.
- Command Finder only — 5.
- Leader layer — 5. Bound in every sense that matters to an operator; the
  registry row is just not where they live.
- Menu or dialog — 8.
- Shadowed — 2. `MemoryScan` and `SpeakFrequency`, each of which carried a chord
  on paper that something else consumed at window level first.
- Retired — 2. `CycleContinuous` and `ShowMenus`, which answer with an apology
  and must never be bound.
- Vestigial — 1. `LogForm`, a default-key row with no `KeyTable` entry behind
  it.

**Zero are `Unassigned`** — the enum value that means nobody decided. So the
answer to "which of these is a gap and which is a decision" is: none of them is
a gap. Nothing here needs a key assigned.

What Track B adds is that this is now *verifiable* rather than asserted.
`ValidateKeyBindings` already checks the roster against the default table at
startup; `jjprobe unbound` makes the same answer readable from outside without
running the app in a debugger.

## KeyDisplay is prose, and something had to bridge it

`KeyInventory.FixedKeyEntry.KeyDisplay` is written for an operator to read —
"Space, Up, Down, or Q", "0-7 or A-H", "Ctrl+J, Shift+A through Shift+H", "Plus
then digits". Five surfaces consume that field and all five only ever *display*
it, so nothing has ever forced it to be machine-readable.

A harness that presses every key has to bridge that gap. `KeyDisplayExpander`
does it, and the result is better than expected: **140 inventory rows expand to
243 concrete chords with zero residue.** Every row is pressable.

Three rows expand to a *representative* rather than an enumeration, and are
flagged `Sampled` so no report can overclaim: "Digits" on both frequency
contexts, and "Plus then digits". Pressing one member proves the family is
wired; it does not prove every member.

The recommendation is **not** to convert KeyDisplay into data. It is written in
the right voice for the five surfaces that show it, and the expander is a
one-way bridge that costs nothing to maintain. But note that the expander is now
a load-bearing consumer, so a new prose form nobody has seen before will surface
as residue in a sweep report rather than being silently skipped.

## Slice F cannot be reached from the leader layer

Expanding `Ctrl+J, Shift+A through Shift+H` produced a duplicate against
`Ctrl+J, Shift+F`, which the inventory separately lists as "Speak the RX filter
width". Chasing it down:

`KeyCommands.DoLeaderCommand` handles `Shift+A`, `Shift+B`, `Shift+C`,
`Shift+D`, `Shift+E`, `Shift+G` and `Shift+H` as slice jumps — and skips
`Shift+F`, with a comment saying it collides with the existing RX filter chord.
The implementation is correct and deliberate.

The finding is in the *documentation*, and it is not cosmetic:

- The inventory advertises **a range with a hole in it, in prose**. The
  KeyDisplay says "Shift+A through Shift+H" and the description says "(Shift+F
  is reserved)". Any machine consumer expanding the range gets eight chords, one
  of which does something completely different. Track B's expander hit this on
  the first run.
- More importantly, **slice F — the sixth slice — has no leader jump at all**,
  and nothing offers an alternative. A 6600, 6700, 8600 or AU-520 operator with
  six or more slices simply cannot jump to that one from the leader layer. The
  wording "Shift+F is reserved" reads as *this key is taken*, when what it
  actually means is *this slice is unreachable*.

Not fixed here. It is worth someone deciding whether slice F gets another chord
or whether the docs should say plainly that it does not have one.

## Two documented key surfaces are missing from the inventory entirely

Cross-checking `docs/help/md/keyboard-reference.md` against `KeyInventory`
turned up two whole sections of working, documented keys that the inventory does
not know about. This is the same gap Sprint 32 Track G closed for the Audio
Workshop's F6 — "a working key nobody could discover" — and these two are still
open.

**The Audio Devices dialog: six keys.** Alt+M starts or stops the microphone
check, Alt+L speaks the current reading, Alt+S shows every sound endpoint,
Alt+R refreshes the device list, Alt+U unmutes the microphone in Windows, Alt+W
opens the Windows microphone privacy settings. `grep AudioDevices
KeyInventory.cs` returns nothing.

Alt+L is a real handler in `AudioDevicesDialog.OnPreviewKeyDown` — and it is
*the* 2026-08-13 site, now correctly normalising through `e.SystemKey`, with the
whole story in a comment above it. Alt+R, Alt+U and Alt+S are WPF access keys
(`Content="_Refresh device list"`, `"_Unmute this microphone in Windows"`,
`"_Show every sound endpoint..."`).

Because none of them is registered, none appears in the Keys dialog's built-in
view, in the Command Finder, or in the exported key manifest — and the Track B
sweep will never press them either, since the sweep works from the inventory.
An operator can only find these six by reading the help file and already knowing
to look in it.

**The Trace Archive Browser: four keys.** Enter, Ctrl+C, Delete and Ctrl+A on
the row list. Also absent from the inventory. That section additionally names a
stale route — "Help → Tracing → Archive Browser" — and the Help menu's Tracing
item was deleted in Sprint 30 Track D. `NativeMenuBar` says so in its own
comment: Tools → Diagnostics is the replacement. So the section tells an
operator to go somewhere that no longer exists.

Neither is fixed here. Registering them in `KeyInventory` is the fix for the
first half of both, and correcting the route is the fix for the second.

## An observation outside this track's remit

`docs/help/md/keyboard-reference.md` contains 256 table rows. This is the
keyboard reference for an application whose users are blind screen-reader
operators, and the project's own standing rule is that tables are not used in
screen-reader-facing material because a table read aloud becomes a wall of
coordinates.

Flagging rather than touching: this is a documentation-ownership decision, the
file is large and shared, and it may well already be known. But it is the one
document in the product a new blind operator is most likely to read end to end.

## Two structural facts the harness had to discover, worth writing down

**The Home display publishes nothing to assistive technology except what it
speaks.** `SilentTextBox` overrides `OnCreateAutomationPeer` with a
`FrameworkElementAutomationPeer` specifically to suppress TextPattern and
ValuePattern change events, and reports its control type as `Custom`. The
"fields" are caret positions inside that one text box. Focus never moves between
them.

This is a deliberate, well-documented decision — it stops NVDA chattering on
every rig-state update so the app can do its own speaking — and it is not being
questioned here. But the consequence for testing is absolute: **no external
tool, including a screen reader, can determine which Home field the operator is
on except by listening.** That is why the probe reads the trace file, and why
the sweep learns the Home layout by walking it and recording what gets said.

**The key dispatcher already logs everything a harness needs, at Info level.**
`DoCommand:<key>`, `Leader:<key>`, and — the valuable one —
`DoCommand:key not found:<key>` when a keystroke arrives and no command claims
it. A real trace from this machine contains exactly that line for Alt+F4.

This is a stronger signal than speech, because it separates *the chord never
arrived* from *the chord arrived and nothing was listening*. Those two sound
identical to a human at the keyboard, and the 2026-08-13 Alt+L failure was the
second kind. Nothing had to be added to the app to get it.

The speech channel is still needed, and is not redundant: the Home field keys
are handled in `FreqOutHandlers` and never reach the dispatcher at all, so an
utterance is their only outward sign. That channel does need Verbose, and
measured on this machine, **every Info-level trace file contains zero
`ScreenReaderOutput` lines** — so a detailed capture has to be running or the
whole Home surface reads as dead.
