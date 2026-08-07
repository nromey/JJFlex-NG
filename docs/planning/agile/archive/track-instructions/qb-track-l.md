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

## Design decisions (appended by QB Track L, 2026-08-07)

1. **FinderDoors is a separate KeyInventory table, deliberately outside
   All().** The four absorbed Command Finder rows are DOORS (menu paths /
   dialogs), not keys — putting them through All() would have leaked them
   into the key manifest and the Keys dialog, which item 1b said must gain
   only the TXSlice keys. They are emitted verbatim (no "(on ...)"
   suffix) so speech and searchability match the old inline rows exactly;
   FixedKeyEntry gained an optional MenuText rather than a second type.
   Accepted consequence: the Command Finder now carries BOTH the combined
   TXSlice door row (with its menu path) and four per-key TXSlice rows
   from the new FieldKeys entries — redundant but each accurate, and the
   per-key rows are what every other field already gets. TXSlice help
   descriptions were normalized to house style ("Up / Down", initial
   caps); the '?' handler previously claimed the field had no keys, so
   any wording delta is strictly an honesty upgrade.

2. **The selector's identity card describes the CONNECTED radio, not the
   selected row.** NetworkIdentityInfo.BuildLines takes a FlexBase and
   renders only a connected rig's identity — that is the contract Track D
   shipped and the spec said to adopt unchanged. So the card in the
   picker answers "who am I connected to right now" (useful mid-switch),
   and says "No radio connected" pre-connect — honest, per D's null
   design. Wiring: a new GetCurrentRig callback resolved fresh on every
   refresh (the app disposes/recreates its rig object during retries),
   and a separate RadiosBox.SelectionChanged subscription — E's handler
   untouched. Hosted exactly like the Status dialog (bold heading, own
   tab stop, MaxHeight 120); dialog height 500 → 560 so the radio list
   keeps its size. A per-SELECTED-radio identity card would need a
   serial-keyed BuildLines overload — that is a Track D contract change,
   left for Noel's "non-Settings quick surface" question.

3. **Failure advice replaces the "offline" guess instead of stacking on
   it.** With a report in hand, the announcement and body say "X is not
   available. <report>"; "X is offline" survives only when no report was
   filed (an auth failure is not "offline", and D's model says bare
   wording only when evidence is genuinely absent). Both dialog variants
   were updated: the WinForms one is the live startup caller; the WPF
   twin (currently uncalled) kept in parity so whichever future path
   adopts it inherits the behavior. The WinForms form now MEASURES the
   message (TextRenderer) and sizes itself; the WPF one sizes to content.

4. **Feature Availability wiring lives in MainWindow.OnRadioStarted, not
   ApplicationEvents.vb.** The spec named ApplicationEvents.vb, but
   ShowRadioInfoDialog is an instance property on each FlexBase — it must
   be re-assigned per rig creation, and app startup has no rig.
   OnRadioStarted is where the app already wires FlexBase dialog
   delegates (ShowMemoriesDialog, and Sprint 11's own commit message says
   "wired externally by MainWindow"), so the ShowOperatorsCallback
   pattern's spirit — app-side wiring at the established site — is
   honored at the site that can actually work. The feature-availability
   text builder was ported from the deleted WinForms FlexInfo form into
   FlexBase itself (theRadio is internal to the Radios assembly; the UI
   gets thin lambdas). Nickname edits route through RenameRadio so the
   dialog rename gets the same radio-side persistence as Radio Setup's.
   The menu door speaks "Radio information is not available yet" if the
   delegate is ever null — no silent menu items.

5. **SliceStates enum went with SliceState(int).** The enum existed
   solely as that method's return type; zero other references. A short
   grave comment marks the spot with the MultiFlex rationale.
   JJTraceListener.cs also came out of JJTrace.csproj's explicit Compile
   list; "Tracing - Copy.cs" was never compiled at all. Orphan notices on
   FiltersDspControl / RadioNumberBox are line comments ABOVE the XML doc
   summaries so tooling still reads the docs.

6. **Changelog: verified-then-added.** Radio output levels
   (#radio-outputs-visible) and per-radio connection preferences
   (#radios-tab) were already covered — not duplicated. Eight new
   sections + six kitchen-sink bullets added; new headline bullets were
   appended at the end of the existing headline list to keep every
   existing anchor and narrative intact. The Audio Check section carries
   the honesty framing Noel ratified for loopback ("rough listen", SDR is
   ground truth). No track letters, no internal type names.

7. **Debug bundle bounding shipped without a new ZipUtils API.** Items
   were executed 6-then-7 as specced, but the changelog line for the
   bounding rode in item 7's commit — a claim only gets written after its
   code lands. Mechanism: the existing excludePattern drops trace-*.zip
   from the whole-tree add, and CrashReporter.GetRecentTraceArchives
   (Private → Friend) adds back the five newest SESSIONS at their real
   Traces/yyyy/MM paths so the bundled manifest.json still points at
   them. Five vs the crash bundle's three: a user-initiated support
   bundle can afford more history than a crash-path artifact. FLAG, not
   changed: the same routine also zips the entire install directory
   ("program" entry) — self-contained builds make that ~190 MB raw, now
   the dominant bundle weight. Bounding it is outside this item's mandate
   (support may want binary evidence); Noel should decide whether the
   program tree stays, shrinks to a version/hash manifest, or goes.
