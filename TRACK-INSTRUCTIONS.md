# QB Track L — the finisher (post-train tie-up)

**Model: Fable.** Small integration items left deliberately until after
the eleven-track merge train landed. You have the FULL merged tree —
every track's code is under you. Goal: leave nothing half-wired so Noel
tests finished code tonight, once.

## Context

Final track of the 2026-08-07 queue-burn ensemble (plan:
`docs/planning/active/nightowl-pileup-ragchew.md`; landing record at the
top of `docs/planning/active/research-queue.md`). JJ Flex is a
screen-reader-first FlexRadio client for blind hams. You work ONLY in
this worktree (`C:\dev\jjflex-qb-l`, branch `qb/track-l`) — never in
`C:\dev\JJFlex-NG`.

## Work items

1. **KeyInventory absorption.** (a) In `ApplicationEvents.vb`, four
   Command Finder rows sit right after the
   `KeyInventory.CommandFinderItems()` AddRange with a merge-time
   comment saying they await absorption (Transmit slice, Power dialog,
   TX antenna, RX antenna). Move their data into the KeyInventory table
   (`JJFlexWpf/KeyInventory.cs`) so `CommandFinderItems()` emits them,
   and delete the inline adds. Speech/searchability must be identical
   after. (b) In `MainWindow.xaml.cs`, both TXSlice DisplayFields
   (Classic and Modern setup) carry inline HelpItems with a comment
   noting the pending KeyInventory row — add a TXSlice entry to the
   inventory table and switch both fields to
   `KeyInventory.HelpItemsFor("TXSlice", ...)`. The `?` handler and the
   Keys dialog built-in view should then show TXSlice keys with no other
   change.
2. **Network identity card into the selector.** Track E left the
   RigSelectorDialog detail-area grid Row 4 empty (comment marks it) for
   Track D's `NetworkIdentityCard` (`JJFlexWpf/Controls/`). Drop the
   control in — D built it to work unchanged: strictly read-only, renders
   the cached `NetworkIdentityInfo.BuildLines` output, never probes. One
   arrow-readable tab stop, sensible AccessibleName, updates when the
   selected radio changes. If selection-change wiring requires touching
   E's selection logic, keep it to an event subscription — do not
   restructure the selector.
3. **Auto-connect failure advice.** `AutoConnectFailedDialog` shows only
   the radio name. When `FlexBase.LastConnectFailureAdvice` is non-null
   at auto-connect failure time, include it in the dialog body text and
   the spoken announcement. Keep D's model: bare wording only when the
   report is genuinely absent.
4. **Wire the Feature Availability door.** `ShowRadioInfoDialog` is never
   assigned app-side (Track G's find), so RadioInfoDialog's Feature
   Availability tab is dead UI. Wire the callback in
   `ApplicationEvents.vb` following the exact pattern Track A used for
   ShowOperatorsCallback / other MainWindow callbacks. Verify the menu
   door that should open it actually speaks and opens.
5. **Dead code deletion — verify zero references first, then delete:**
   `JJTrace/JJTraceListener.cs`, `JJTrace/Tracing - Copy.cs` (Track K:
   dead after RotatingTraceListener), and `FlexBase.SliceState(int)`
   (Track J: zero callers, positional classifier that lies under
   MultiFlex). Do NOT delete FiltersDspControl or RadioNumberBox — they
   are orphaned but the wire-in-or-remove decision is Noel's; instead add
   a short header comment to each stating orphan status (never
   instantiated; Sprint 8 replacement never wired) and that a decision is
   pending.
6. **Changelog audit + gaps.** `docs/CHANGELOG.md` conventions are in
   CLAUDE.md — warm first-person ham voice, user state not developer
   action, no internal jargon, screen-reader detail welcome, NEVER
   mention track letters or sprint mechanics. Audit the current
   unreleased section: tracks A/B/E/H/K wrote entries; verify, then add
   the missing user-facing stories, at minimum: the slice identity fix
   (pressing D gets D; Release All Extra Slices keeps the slice you're
   on), the Audio Workshop hear-yourself/Audio Check session (Track G
   deferred this deliberately — it is the headline), the transmit-slice
   field and menu doors, the Power dialog with dBm on transverters, the
   connect-failure honesty (spoken reasons, router-rule text, no more
   bare "Connection failed"), radio output levels in Settings, the
   reboot/firmware Radio menu entries, and per-radio connection
   preferences. Check for and fill any other gap you find. Keep each
   entry tight — a paragraph or a few bullets.
7. **DebugInfo zip bounding — assess, act only if contained.**
   `DebugInfo.GetDebugInfo` zips the whole 30-day trace-archive
   directory (Track K flag). If a clean, small change caps it (e.g. most
   recent N sessions using K's session grouping), do it; if it needs a
   new shared ZipUtils API, write the design into your report instead
   and change nothing.

## Rules

- Every change speaks; Escape closes anything you add; no silent
  keystrokes; errors never suppressed.
- Build from the worktree root after every item:
  `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
  — verify `bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe` has a
  fresh timestamp each time.
- Commit per item: `QB Track L: <what changed>`. Push to origin after
  every commit (`git push origin qb/track-l`) — storms in the area.
- No new key bindings. No version bump. No changes outside the items
  above.
- You run unattended: if an item can't be completed cleanly, report it
  under "Needs Noel" or "Deferred" with the reason and move on.

## Final report

Completed items, deferred items with reasons, changelog entries added
(list their headings), build status, final pushed SHA. Append a "Design
decisions" section to this file (committed) for judgment calls.
