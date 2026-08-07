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
