# Track B — FlexLib 4.2.18 upgrade

**Worktree:** `C:/dev/jjflex-flexlib-42`
**Branch:** `track/flexlib-42` (off `main` @ `09b724c3`)
**Mode:** autonomous; no radio access; no merge to main until Noel reviews

## Goal

Upgrade FlexLib from the current v4.0.1 (in `FlexLib_API/`) to v4.2.18 (vendor source unpacked in `flexlib4218/`). Reapply the four-file patch set from `MIGRATION.md`, fix call-site drift at the JJFlex layer, get a clean build for both x64 and x86 Release/Debug, and document what needs real-radio testing under Track A.

## Authoritative reference

`MIGRATION.md` is the upgrade checklist. Read it before doing anything else. The four files that need patches in the new vendor tree:

1. `FlexLib_API/FlexLib/SslClientTls12.cs` — copy from old tree, no changes (custom TLS 1.2/1.3 wrapper).
2. `FlexLib_API/FlexLib/TlsCommandCommunication.cs` — replace `SslClient` field/ctor with `SslClientTls12`.
3. `FlexLib_API/FlexLib/Radio.cs` — keep the small change that selects `TlsCommandCommunication` when `IsWan || PublicTlsPort > 0`.
4. `FlexLib_API/FlexLib/Discovery.cs` — keep the `Receive()` race fix (local UdpClient capture, null-guard on entry, catch `ObjectDisposedException`/`SocketException`, only null the static `udp` field if it still points at the captured local).

Also: `flexlib-discovery-nre-report.txt` at repo root has the upstream-reportable Discovery NRE write-up. Don't delete it during the upgrade.

## Phase plan

Commit per phase. Use the message format `Sprint FlexLib-42 Phase N: <description>`.

### Phase 1 — Inventory (read-only)

Diff `FlexLib_API/` against `flexlib4218/`:

- New files in v4.2.18 (e.g., `DisplayMarker.cs`, `Filter.cs` are visible in cursory inspection; there will be more)
- Removed files (older API surface that v4.2.18 dropped)
- Files where the public API has changed (use `git diff` against the unpacked source vs the current `FlexLib_API/`)

Write the inventory to `docs/planning/track-b/4.2.18-inventory.md`. This becomes the map for Phase 5 call-site fixes.

Commit: `Sprint FlexLib-42 Phase 1: Inventory diff between v4.0.1 and v4.2.18`

### Phase 2 — Replace stock vendor source

The pattern is "drop in newer FlexLib, then reapply our patches." Concretely:

1. Save the current four custom files for re-application: `SslClientTls12.cs`, current `TlsCommandCommunication.cs`, current `Radio.cs`, current `Discovery.cs`. Stash them somewhere outside `FlexLib_API/` (e.g., `C:/dev/jjflex-flexlib-42/.upgrade-staging/`) — git history is also fine since they're committed.
2. Delete the contents of `FlexLib_API/{ComPortPTT, FlexLib, UiWpfFramework, Util, Vita}/` (NOT the folders themselves — keep `FlexLib_API/` and project subfolders intact).
3. Copy `flexlib4218/{ComPortPTT, FlexLib, UiWpfFramework, Util, Vita}/` contents into `FlexLib_API/` matching subfolders.
4. Resolve the `Directory.Build.props` at `flexlib4218/Directory.Build.props` — check whether it already exists at `FlexLib_API/Directory.Build.props` and merge if needed.
5. Delete `flexlib4218/` from the worktree once the copy is done — it was just staging.
6. Likewise delete `ssdr4218/` and `docs/smartsdr4218/SmartSDR-v4.2.18-Release-Notes.pdf` if they're not the final home for the release notes (or move the PDF into `docs/` if it belongs there long-term).

Commit: `Sprint FlexLib-42 Phase 2: Replace FlexLib v4.0.1 with v4.2.18 vendor source`

### Phase 3 — Apply MIGRATION.md patches

Apply each of the four patches per `MIGRATION.md`. Be careful with `Discovery.cs` — it's the most subtle. The race fix has four discrete elements:

1. Capture a local `UdpClient` reference at entry to `Receive()`.
2. Null-guard on entry.
3. Catch `ObjectDisposedException` and `SocketException` around `ReceiveAsync`.
4. Only null the static `udp` field if it still points at the captured local.

Plus: keep the `Debug.WriteLine` traces at task start/exit for future race visibility.

Verify each patch lands correctly by re-reading the new file end-to-end after the edit.

Commit: `Sprint FlexLib-42 Phase 3: Apply TLS wrapper + Discovery race fix per MIGRATION.md`

### Phase 4 — First build attempt

Build x64 Debug:

```
dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal
```

Capture the full error list. Likely categories of failure:

- FlexLib API drift (renamed properties, new required parameters, removed methods) — these surface at JJFlex call sites in `Radios/`, `JJFlexWpf/`, `main_app/`.
- New required dependencies — flag and install if needed.
- Project file mismatches — `.csproj` references that need updating.

Do NOT attempt to fix anything yet. Just get the error list.

Commit nothing in this phase — it's diagnostic only. Save the error list to `docs/planning/track-b/4.2.18-build-errors-phase4.md`.

### Phase 5 — Fix call sites

This is the variable-effort phase. Work through the build errors in batches. Likely hot spots:

- `Radios/FlexBase.cs` — most FlexLib-touching code lives here. The station-name-wait loop at lines 1133-1192 is mentioned in `project_stuck_modal_escape_design.md`; pay attention if the loop uses APIs that changed.
- `Radios/AllRadios.cs` — discovery and radio table.
- `Radios/Flex6300Filters.cs` — filter/DSP UI controls.
- `Radios/ScreenReaderOutput.cs` — recently added `SpeakNoRadioConnected()` may use FlexLib types.
- `JJFlexWpf/` — anywhere using FlexLib types directly. Grep for `using Flex.`.
- `main_app/` (i.e., the VB.NET main project) — check `globals.vb` and similar for FlexLib references.

Commit per logical batch (e.g., one commit per file or per error category). Message: `Sprint FlexLib-42 Phase 5.N: <description>`.

If you encounter an API change that's ambiguous (multiple plausible new equivalents), document the choice in `docs/planning/track-b/4.2.18-api-decisions.md` and note it for Noel's review.

### Phase 6 — Cross-arch + Release builds

Once x64 Debug builds clean:

```
dotnet build JJFlexRadio.sln -c Debug -p:Platform=x86 --verbosity minimal
dotnet clean JJFlexRadio.sln -c Release -p:Platform=x64
dotnet build JJFlexRadio.sln -c Release -p:Platform=x64 --verbosity minimal
dotnet clean JJFlexRadio.sln -c Release -p:Platform=x86
dotnet build JJFlexRadio.sln -c Release -p:Platform=x86 --verbosity minimal
```

After each, verify exe timestamp matches current time:

```
powershell -Command "(Get-Item 'bin/x64/Release/net10.0-windows/win-x64/JJFlexRadio.exe').LastWriteTime"
```

Per CLAUDE.md: `--no-incremental` does not guarantee fresh output; use `dotnet clean` first.

Commit: `Sprint FlexLib-42 Phase 6: Cross-arch builds clean`

### Phase 7 — Smoke check (no radio)

Launch `bin/x64/Release/net10.0-windows/win-x64/JJFlexRadio.exe`. Expectations:

- App starts without crashing.
- Main window appears.
- "No radio connected" state announces correctly (depends on the no-radio guards from yesterday — verify they're still wired through).

You will NOT have radio access in this autonomous track. Document any startup oddities for Track A follow-up. Don't kill the app via taskkill — that's the stuck-modal pattern from `project_dialog_escape_rule.md`.

Commit nothing — the smoke check is human-readable evidence. Append findings to `docs/planning/track-b/4.2.18-smoke-check.md`.

### Phase 8 — Deferred items + handoff

Write `docs/planning/track-b/4.2.18-handoff.md` with:

- What was done (phase-by-phase summary)
- Build status (x64 Debug/Release, x86 Debug/Release)
- Smoke check results
- What needs Track A (Noel + real radio): connection test, SmartLink test, slice operations, audio (RX + TX + DAX), CW, the AS-retry pathway from `project_as_retry_pathway_regression.md`
- Open questions for Noel (any API decisions that were ambiguous)
- Any new TODO entries discovered during the upgrade — append to `docs/planning/vision/JJFlex-TODO.md` with the same per-bug format used elsewhere

Commit: `Sprint FlexLib-42 Phase 8: Handoff document for Track A testing`

## Coordination

- **Inbox/outbox** — if you produce a doc that needs Noel's review, drop it in `docs/planning/inbox/` per `docs/planning/inbox/README.md`. Watch `docs/planning/outbox/` for reviewed docs.
- **No merge to main** — this branch sits parked until Noel reviews the handoff and confirms.
- **Track A independence** — Track A (testing with Noel) is happening concurrently in a separate session. Don't touch any branches outside this worktree.
- **Track C independence** — Track C (braille research) is in `C:/dev/jjflex-braille/`. Don't touch.

## What NOT to do

- Don't bump the JJFlexRadio version. The FlexLib upgrade ships independently of the 4.1.17 release cut.
- Don't merge `main` into this branch mid-work — it'll just create churn. Forward-merge happens at handoff.
- Don't rebase. This branch will eventually merge with `--no-ff` like all sprint branches.
- Don't push to origin yet. Local commits only until Noel approves.
- Don't delete `MIGRATION.md` after the upgrade — it's the next-upgrade checklist too. Update it if anything changed about the patch surface.
- Don't refactor anything that isn't directly required by the upgrade. This is a vendor refresh, not a cleanup pass.

## Resume notes

If the session ends mid-phase, leave a "RESUME HERE" marker in the most recent commit message and update `docs/planning/track-b/4.2.18-progress.md` with what's done and what's next. Standard sprint-resume protocol.
