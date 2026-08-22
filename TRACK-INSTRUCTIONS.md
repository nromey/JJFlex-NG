# Sprint 34 Track B — Keys, menus, and the logging and cluster surfaces

Command confirmations plus the whole logging and cluster vocabulary. KeyCommands.cs is the biggest file here and spans every subject in the app — which is exactly why one track owns it outright instead of four tracks reaching into it.

## Before anything else: verify your base

Your worktree was cut from **`6c84acf67c285e64689a5c0e2e7d0c648cf39845`**. Confirm it:

```
git log --oneline -1
```

If that is not your HEAD, STOP and say so in your report. On 2026-08-21 four of
five agents were handed worktrees cut from an old commit, and one built an
entire feature against a library deleted four days earlier. Naming the SHA you
actually built on is part of the deliverable.

## Read the contract first

`docs/planning/active/string-store-contract.md`. It is short and every rule in
it was paid for. The essentials, so you cannot miss them:

- **Keys are hierarchical and behaviour-describing.** `connect.smartlink.offer_local_only`,
  never `settings.tab3.checkbox2`. Screens get redesigned; behaviour does not.
- **Keys must be string LITERALS at the call site.** `Lexicon.Get("audio.device.none")`.
  A key built at run time cannot be checked until something executes that line.
  If a family genuinely must be assembled (per-band, per-slice), say so in your
  report — those are the only entries with no static safety net.
- **Never reword while extracting.** Extraction and editing are separate passes.
  A track that does both makes the transcript diff worthless, because every
  intentional change hides a possible accident.
- **Report inconsistencies, never silently normalise them.** Two places saying
  the same thing differently is a finding, and which wording survives is the
  owner's call, not yours.
- **Named placeholders only.** `{radio}` and `{freq}`, never `{0}` and `{1}`.
  Carry the same name as the variable it interpolated.

## The API

`Radios.Lexicon` — a static class in the `Radios` namespace. From C# in the
Radios project it is `Lexicon.Get(...)`; from anywhere else,
`Radios.Lexicon.Get(...)`. From VB, `Radios.Lexicon.Get(...)`.

```
Lexicon.Get("connect.done")                             // plain
Lexicon.Get("connect.disconnected", verbosity)          // verbosity ladder
Lexicon.Get("connect.found", ("radio", name))           // named placeholder
Lexicon.Get("connect.found", verbosity, ("radio", name))
```

**It is not called `Strings`, and that is deliberate** — a `Radios.Strings`
namespace would shadow `Microsoft.VisualBasic.Strings` for every VB file that
imports Radios. Do not rename it.

A missing key renders as the key itself, so a string you forget announces
itself rather than going silent.

## Your partitions

The six JSON files live in `Radios/Lexicon/`. Add your keys to the right one.

Command confirmations go under `settings.*` or `connect.*` by subject; logging and cluster text goes under `logging.*`.

**Verbosity ladders: model the ones that exist, create none.** Only three exist
in the whole codebase. If you meet one — a `switch` on `CurrentVerbosity` — put
its tiers under one key as an object with `critical`, `terse` and `chatty`. If
you think a string DESERVES a ladder it does not have, that is editing. Report
it; do not build it.

## Your files, and only your files

Exactly one track owns each file. Touching a file you do not own is how six
clean merges become one bad afternoon. Yours (147 sites across 27 files):

- `JJFlexWpf/KeyCommands.cs` — 80
- `JJFlexWpf/NativeMenuBar.cs` — 8
- `DefineCommands.vb` — 6
- `JJFlexWpf/Dialogs/ProfileDialog.xaml.cs` — 6
- `Profile.vb` — 5
- `JJFlexWpf/Dialogs/KeysDialog.xaml.cs` — 4
- `ImportSetup.vb` — 3
- `JJFlexWpf/Dialogs/ListerDialog.xaml.cs` — 3
- `JJFlexWpf/Dialogs/LogCharacteristicsDialog.xaml.cs` — 3
- `JJFlexWpf/Dialogs/ManageGroupsEditDialog.xaml.cs` — 3
- `JJFlexWpf/Dialogs/ProfileWorkerDialog.xaml.cs` — 3
- `ExportSetup.vb` — 2
- `JJFlexWpf/Dialogs/ImportDialog.xaml.cs` — 2
- `JJFlexWpf/Dialogs/LOTWMergeDialog.xaml.cs` — 2
- `JJFlexWpf/Dialogs/ManageGroupsDialog.xaml.cs` — 2
- `JJLogLib/LogProc.cs` — 2
- `Lister.vb` — 2
- `ReverseBeacon.vb` — 2
- `ExportForm.vb` — 1
- `JJFlexWpf/Dialogs/DefineCommandsDialog.xaml.cs` — 1
- `JJFlexWpf/Dialogs/ExportDialog.xaml.cs` — 1
- `JJFlexWpf/Dialogs/FindLogEntryDialog.xaml.cs` — 1
- `JJFlexWpf/Dialogs/LogTemplateDialog.xaml.cs` — 1
- `JJFlexWpf/Dialogs/MenusDialog.xaml.cs` — 1
- `JJFlexWpf/StationLookupWindow.xaml.cs` — 1
- `JJLogLib/LogStats.cs` — 1
- `LogEntry.vb` — 1

If a string you need lives in a file you do not own, leave it and note it in
your report. Somebody else has it.

## Your gate — all three must pass

1. **The static check.** Every key you name must exist in the store:
   `dotnet test Radios.Tests\Radios.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~LexiconKeyCoverageTests"`
2. **The unit suite**, whole, from your worktree:
   `dotnet test Radios.Tests\Radios.Tests.csproj -c Debug -p:Platform=x64`
   It stood at **317 passing** when you started. A drop is a regression, not noise.
3. **A clean build:**
   `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
   Zero errors. Warnings are pre-existing and plentiful; errors are yours.

**Do not spawn the app.** You do not need it, and the transcript diff is run
once over the merged whole rather than six times over six domains. If you have
some reason you believe requires it, you MUST set `JJFLEX_CONFIG_DIR` to your
own temp directory first — without it you are writing the operator's real
settings, and nothing will tell you that you did. An ordinary twelve-second
launch was measured on 2026-08-22 changing 17 files in his live folder,
including his key map and his 8600's per-radio config.

## Commit as you go

`git commit` after each coherent chunk, message prefix `Sprint 34 Track B: `.
Do not push; the orchestrator merges.

## Your report, when done

Plain prose or bullets — **never a table**, the operator uses a screen reader.

- The base SHA you actually built on.
- How many sites you extracted, and how many keys you added per partition.
- **Every inconsistency you found and did NOT fix** — two places wording the
  same idea differently, a message that contradicts a label, anything that made
  you hesitate. This list is a real deliverable, not an afterthought. It is the
  reason the store is worth building.
- Any key you had to assemble dynamically, and why.
- Anything you left because it lived in another track's file.
- Test count at the end, and the three gate results.
