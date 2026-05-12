---
date: 2026-05-11
type: audit / diagnosis
audit-scope: behavior of Ctrl+J, H vs user expectation
status: diagnosed, fixes deferred
triggered-by: Noel pressed "JJ + H" expecting context-sensitive help during foundation testing on 2026-05-11; heard "meter tones" plus a keys-list that felt contextually wrong
---

# JJ+H context-help audit — 2026-05-11

## What triggered this audit

During foundation testing on 2026-05-11, Noel was looking for the
"mute all slices" hotkey in the keyboard reference doc, couldn't find it
(see the companion keyboard-reference audit for that drift), and reached
for what he remembered as "JJ+H" expecting context-sensitive help. The
result: the screen reader announced "meter tones" followed by a list of
keys that "weren't necessarily contextually correct."

The user-facing question: what did `JJ+H` actually do, and why did it feel
wrong?

## Findings

### "JJ" is the `Ctrl+J` leader key

Confirmed in `JJFlexWpf/KeyCommands.cs:1706` — the leader key announcement
speaks the letters "J J" when the leader is engaged. So "JJ+H" maps to
`Ctrl+J, H` in the dispatcher.

### `Ctrl+J, H` correctly invokes `LeaderKeyHelp()`

The chord dispatcher at `KeyCommands.cs:2106-2108` routes `H` after the
leader to `LeaderKeyHelp()`, defined at `KeyCommands.cs:2269-2278`. That
function speaks the complete list of leader-key commands:

> "Leader key commands: N legacy noise reduction, B noise blanker,
> W wideband NB, R neural NR, S spectral NR, A auto notch, P audio peak
> filter, M memories, D tuning debounce, F speak TX filter, Shift F
> speak RX filter, L log statistics. H for this help. Escape to cancel."

The binding is correctly registered, the handler runs, the speech is
produced. The code is functioning as designed.

### The "meter tones" speech was incidental, not part of the help text

"Meter tones" does NOT appear in `LeaderKeyHelp()`'s announcement. Best
guess from the audit: NVDA narrated the focused / nearby control's
`AutomationProperties.Name` around the same time. The `MetersPanel`
control carries `AutomationProperties.Name="Meters Configuration"`,
which a screen reader might shorten or rephrase as "meter tones" when
read in passing. Mystery announcement; not from JJFlex's speech path.

### The keys felt "not contextually correct" because the help is global

The leader-key help speaks every leader-key command regardless of the
user's current state. A user with no radio connected still hears
`B noise blanker` even though Noise Blanker requires a radio. A user
outside CW mode still hears `P audio peak filter` even though APF is
CW-only. The help isn't context-aware; it's a static dump of the
leader-key namespace.

This is what made the speech feel "wrong" to Noel — he expected F1-style
context-sensitive help (only the bindings relevant to your current
focus / state) but got the global leader-key dump.

## Verdict

**Discoverability problem, not a code bug.** The dispatcher routes the
key correctly, the handler runs correctly, the speech is correct for the
binding's design. The misfit is between user mental model
("`Ctrl+J, H` should be like F1 — context-aware") and the binding's
actual behavior ("`Ctrl+J, H` lists all leader commands").

The canonical context-sensitive help binding is **F1** (opens the CHM
help into the right page for current focus). The canonical
search-by-keyword surface is **`Ctrl+/`** (Command Finder, filtered by
typed substring). `Ctrl+J, H` is the third surface — a reference-card
dump scoped to the leader-key namespace. Three valid surfaces, three
different shapes, only one of them (F1) does what Noel was reaching for.

## Fix path

Three escalating options:

1. **Doc clarification** (small, deferred). Update
   `keyboard-reference.md` to note that `Ctrl+J, H` lists *all* leader
   commands and that **F1** is the binding for context-sensitive help.
   Bundles cleanly with the companion keyboard-reference audit's
   doc-edit pass.
2. **One-line code hint in `LeaderKeyHelp()`** (trivial, deferred).
   Append a closing sentence to the announcement: "Press F1 for
   context-sensitive help, or Ctrl-/ for Command Finder." Redirects
   any future user who reaches for `Ctrl+J, H` the same way Noel
   did. Costs nothing to ship; loses nothing if dropped.
3. **Context-aware leader-key help** (Sprint 29+ scope). Make
   `LeaderKeyHelp()` filter its announcement by current state. Hide
   bindings that need a radio when no radio is connected; hide CW-only
   bindings outside CW mode; etc. Bigger refactor, more useful end
   experience, but not foundation-cycle work.

## Status

Diagnosed; fixes deferred. Option 2 is so cheap it can ride on top of any
unrelated `KeyCommands.cs` edit that lands in the next few sprints —
worth slipping in opportunistically.

## Companion audit

The keyboard-reference audit
(`2026-05-11-keyboard-reference-audit.md`) ran in parallel today and
surfaces the doc-incompleteness side of the same broader problem: when
JJFlex tells the user about its keys, all three surfaces (the static
doc, the leader-key runtime help, the Command Finder) have gaps. A
coherent fix is to make each surface point at the next-best surface
when it doesn't have the answer.
