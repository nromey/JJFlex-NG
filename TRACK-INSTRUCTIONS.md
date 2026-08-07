# QB Track H — Hotkey surface redesign + key coverage audit

**Recommended model: Fable.** Redesign from principles: the legacy editor
is orphaned from the real key system, and the new surface carries
conflict-resolution UX and a generated-manifest architecture. Document
judgment calls in a "Design decisions" section appended to this file.

## Context

One of the 2026-08-07 queue-burn tracks (plan:
`docs/planning/active/nightowl-pileup-ragchew.md`; queue of record:
`docs/planning/active/research-queue.md`, Track H section). JJ Flex is a
screen-reader-first FlexRadio client. Live falsification that started
this: Noel opened Help → Key Assignments, selected a key, pressed Update
— and could not actually change anything. Tools → Hotkey Editor is an
`AddNotImplemented` stub (`NativeMenuBar.cs:1138`).

## Diagnosis you are inheriting (verify, then build)

- `ShowKeysDialog` / `SetupKeysDialog` (Dialogs\) are Jim's legacy
  key-action system — almost certainly orphaned from Sprint 23's unified
  KeyCommands v5 dispatch (`JJFlexWpf/KeyCommands.cs`, the real system:
  registrations with scopes Global/Radio/Logging, FunctionGroups,
  Keywords, ShortActionLabels, a defaults table, leader-key system,
  Command Finder). The Sprint 7 "tabbed hotkey editor" in the changelog
  (:730) predates v5 too.
- FIRST: verify the legacy pair's persistence file isn't still consumed
  by dispatch anywhere. If it is, migration precedes deletion.

## Work items

1. **One Keys surface, backed by the KeyCommands v5 registry.** A single
   dialog that enumerates every registered command with its current
   binding and scope. Views: by scope (Global / Radio / Logging / Home
   region), alphabetical, by function group — tabs or a filter combo,
   your call, but every view arrow-readable with position announcements.
2. **Real editing:** select a command → "press the new key" capture →
   conflict detection that NAMES the collision ("Alt+T is Log his RST in
   Logging scope") and offers steal / cancel → live rebind without
   restart → unbind → per-key reset-to-default → reset-all (confirm
   dialog). Every action speaks. Persistence in the existing user keymap
   store (find where v5 saves overrides; extend, don't fork).
3. **Field-character keys as read-only rows.** T/=/M/X/Space and friends
   are raw field handlers (FreqOutHandlers), not rebindable — but they
   MUST appear in the surface so "what does this key do" has one answer.
   Build the field-key table as DATA (one table driving the surface, the
   `?` handler below, and the manifest).
4. **The `?` handler on home fields:** pressing ? on a home-region field
   speaks "keys here: T transmit, equals transceive, M mute…" — generated
   from the same table as item 3 so speech and docs can't drift.
5. **Menu doors:** Tools → Hotkey Editor opens the surface editable;
   Help → ONE "Key Assignments" item opens it viewing (collapse the three
   duplicate variants at `NativeMenuBar.cs:1297-1299`). Multiple doors,
   one room.
6. **Key coverage audit (the deliverable that outlives the dialog):**
   generate a canonical key manifest by introspecting the v5 registry +
   the field-key table; reconcile against `docs/help/md/
   keyboard-reference.md`; fix every gap in the doc (and every doc entry
   with no living binding). This is the seed of the CLAUDE.md
   keyboard-audit automation — write the generator as a reusable method,
   not a one-off script.
7. **No-shadow sweep:** no control-local handler may swallow a
   Global-scope chord (the Ctrl+Shift+W incident — Track G fixes that
   instance; you sweep for the class). Every bound key speaks its TRUE
   action in every state.
8. **Retire the legacy pair** (after item 0's verification): delete
   ShowKeysDialog/SetupKeysDialog, their menu wiring, and dead
   persistence — with a migration if their file held real user bindings.

## Constraints

- **CW keys: inventory, do NOT redesign.** The play/stop CW and
  send/receive queue keys are Jim-era plumbing awaiting the ratified CW
  pipeline rewrite (queue wave 2). They appear in the manifest as-is;
  their fate is not yours.
- Merge order: H lands LAST — after A, G, and I — so the manifest absorbs
  every other track's registrations. Expect to regenerate the manifest at
  merge time.
- Accessibility: Escape closes; every IsDefault button gets explicit
  AutomationProperties AccessKey/AcceleratorKey; blank lines in read-only
  text get a single space; capture mode must announce entry/exit and
  never trap a user who changed their mind (Escape cancels capture).

## Build & verify

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```
Timestamp must be fresh. Verify rebinding live: rebind a command, use the
new key, restart, confirm persistence, reset to default.

## Commit style

Commit after each work item: `QB Track H: <what changed>`. Push to
`origin` (never `upstream`). Report completion to Noel when done.

## Design decisions (appended per instructions, 2026-08-07)

**Item 0 verification — why the legacy pair could never save.**
`SetupKeysDialog`'s results flowed back through
`MainWindow.SaveKeyActionsCallback` — declared, invoked from
NativeMenuBar, and **never assigned anywhere**. The `?.Invoke` on a null
callback made Update a silent no-op, which is exactly what Noel observed
live. The pair had NO persistence file of its own: its lists were built
fresh from the v5 `Commands.KeyTable` on every open (ApplicationEvents.vb
callbacks) and its edits died with the dialog. Conclusion: nothing to
migrate — deletion is safe, and the v5 `KeyDefs.xml` remains the single
store.

**Promote the hard-wired metas instead of documenting around them.** The
window's hard-wired chords (Ctrl+Shift+M tuning mode, Ctrl+Shift+L
logging mode, Ctrl+Shift+F frequency readout, Ctrl+Alt+F filter values)
ran before registry dispatch, so they invisibly shadowed the registry's
own claims on those chords (MemoryScan, SpeakFrequency, and
SearchLog-in-Logging never fired). Two honest fixes existed: keep them
hard-wired and list them as reserved, or promote them into the registry.
I promoted them (new CommandValues: ToggleTuningMode, ToggleLoggingMode,
ToggleFreqReadout, SpeakRXFilter) — they become visible, searchable,
rebindable, and conflict-checked, and the registry moves closer to being
the actual single source of truth. ToggleFreqReadout is Radio scope (not
Global), which is what lets SearchLog keep Ctrl+Shift+F in Logging scope
conflict-free.

**MemoryScan and SpeakFrequency go to None rather than new chords.**
Their claimed defaults never worked (see above), so users lose nothing.
Inventing new default chords the night before ten tracks merge invites
collisions; unbound-but-bindable is honest and the new editor makes
binding trivial. SmartMergeDefaults migrates existing KeyDefs.xml
automatically (never-customized users get the cleared default; the new
meta-commands then claim the chords via MergeNewDefaults). Flagged in
the changelog as a heads-up per the keyboard-audit checklist.

**Views: two combos, not tabs.** "Arrange" (By scope / Alphabetical /
By function group / Built-in keys) plus a dependent "Category" combo.
Combos are cheap to arrow through with speech, keep one list control as
the single focus target (ListView rows give NVDA native x-of-y position
announcements), and avoid tab-panel focus traps. The scope view buckets
Classic/Modern under Radio — they are never active apart from it.

**Capture implemented via class-level OnPreviewKeyDown.** WPF class
handlers run before instance handlers, so capture-mode Escape cancels
the capture and marks the event handled BEFORE JJFlexDialog's
Escape-close instance handler can fire — the user who changed their mind
is never dumped out of the dialog. Modifier-only presses are ignored
(waiting for the real key); every capture outcome speaks and mirrors
into a live-region status line.

**Reserved-key policy for capture.** Refused with spoken reasons:
Ctrl+J (leader trigger), Ctrl+Space / Shift+Space (PTT), Escape / Tab /
Enter (navigation), Alt+F4 (Windows), and any unmodified non-function
key — plain letters/digits belong to the Home fields and text entry, and
a window-level binding would shadow them. Shift-only chords stay
allowed (Shift+M / Shift+Comma are existing registry defaults).

**Conflict semantics mirror the validator.** Same scope collides;
Global collides with everything; Radio collides with Classic/Modern;
Radio-family never collides with Logging; Classic never collides with
Modern. Steal is offered only when every colliding binding is
stealable; CW message keys are never stealable from here (they belong
to the CW Messages editor — CW is inventory-only this pass per the
rewrite arc).

**KeyInventory as the one table.** Field keys, universal Home keys,
filter chords, leader commands, PTT, logging-pane keys: one data module
now feeds (1) DisplayField.HelpItems, (2) the `?` handler, (3) the Keys
dialog's built-in view, (4) Command Finder informational rows
(replacing the hand-built ApplicationEvents.vb list, whose leader table
was badly wrong — W listed as "Audio Workshop", R as "speak meters", S
as "speak status"), and (5) the manifest. The truth for every row was
read out of FreqOutHandlers / DoLeaderCommand / MainWindow, not out of
the old docs.

**No-shadow sweep findings (the class, beyond Track G's instance):**
- Ctrl+Shift+M/L/F + Ctrl+Alt+F hard-wired shadows — fixed by promotion
  (above).
- `HandleFilterHotkey`'s Ctrl branch ignored Alt, so Ctrl+Alt+[ / ]
  (registry TX-filter high-edge chords) were swallowed as RX
  squeeze/pull. Branches are now modifier-strict; unmatched combos fall
  through to registry dispatch.
- `FrequencyDisplay` consumed Home unconditionally for jump-to-first-
  field, so the Slice field's documented "Home = pan center" was dead
  code. Home now offers the field handler first (the existing PageDown
  pattern); since Slice IS the first field, nothing is lost.
- Universal Home keys were missing entirely on SMeter/Squelch/
  SquelchLevel and V was missing on Slice/SliceOps — added the standard
  `TryHandleUniversalHomeKey` fall-through to all five.
- Slice jump to a non-created slice was silent — now speaks "not
  created".
- ToggleUIMode no-ops silently in Logging mode — the promoted command's
  handler speaks "In Logging mode. Press Control Shift L…" instead.
- RadioPaneControl's local Ctrl+F (logging pane) is semantically
  identical to SetFreq (enter frequency) and only reachable in Logging
  mode where SetFreq is inactive — recorded in the inventory as a
  logging-pane key, no code change.
- AudioWorkshopDialog's Ctrl+Shift+W shadow: left alone — Track G owns
  that instance.

**Manifest generator is a method + a user-facing button, not a script.**
`KeyManifest.Build(commands)` / `ToMarkdown` / `WriteToFile` — the Keys
dialog's "Export Key List" writes it next to KeyDefs.xml and opens it.
A future build-time audit can host KeyCommands with a stub context and
diff `ToMarkdown` output against `keyboard-reference.md`; the doc was
reconciled by hand this pass (drift found and fixed: phantom
Ctrl+Shift+1-5 rows, wrong scanning/DX-cluster/leader tables, false
"two second" leader timeout, false Shift+fine-step claim in value
fields, missing mode keys, missing PTT/bracket/logging-pane sections,
LogStats listed with a chord it lost to the leader key).

**View mode vs edit mode is literal.** Help → Key Assignments hides the
editing buttons and shows a one-line hint pointing at Tools → Hotkey
Editor; the Export button stays in both. One dialog class, one room,
two doors.
