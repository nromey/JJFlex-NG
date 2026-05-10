# Sprint 29 Track M — Updater-Helper Exe

**Branch:** `sprint29/track-m-updater-helper`
**Worktree:** `C:\dev\jjflex-updater-helper`
**Spawned:** 2026-05-09 (evening session)
**Sprint plan:** `docs/planning/agile/sprint29-pileup-skip-elmer.md` + `memory/project_sprint29_updater_vision.md` 2026-05-09 firm-up
**Target ship:** 4.2.0
**Target merge:** main

## Scope summary

Build a tiny standalone .exe that performs the actual file replacement during a JJF update. It survives JJF's process exit, atomically replaces files, deletes obsolete files, relaunches JJF, and rolls back on any failure.

This exe is launched by **Track D's client-side updater** at the moment JJF is about to exit. The two share two contracts:

1. **Staging dir layout** (Track D creates; this track reads)
2. **handoff-manifest.json schema** (Track D writes; this track parses + executes)

See Track D's TRACK-INSTRUCTIONS for the contract details.

## Why a separate exe rather than in-process

JJF cannot reliably replace its own running binaries (Windows file locking). The pattern across the industry:

- App downloads new files to a staging dir
- App launches a separate updater process
- App exits cleanly (releases file locks)
- Updater waits for app's PID to exit, then does file copy
- Updater relaunches the app

Squirrel.Windows, ClickOnce, Sparkle — all use this shape. We follow the playbook.

## Build target

A single self-contained .NET 10 console exe in a new project: `JJFlexUpdaterHelper/JJFlexUpdaterHelper.csproj`. **Self-contained** so the helper itself doesn't need .NET installed (the JJF install dir's runtime might be in flux during file replacement).

Properties to set in the csproj:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0-windows</TargetFramework>
  <SelfContained>true</SelfContained>
  <RuntimeIdentifier Condition="'$(Platform)' == 'x64'">win-x64</RuntimeIdentifier>
  <RuntimeIdentifier Condition="'$(Platform)' == 'x86'">win-x86</RuntimeIdentifier>
  <PublishSingleFile>true</PublishSingleFile>
  <UseAppHost>true</UseAppHost>
</PropertyGroup>
```

Single-file is fine here (and probably worth doing) because the helper is small and atomic deployment is the goal. The single-file deploy unpacks at runtime to a temp dir; that's acceptable for a one-shot updater process.

## Implementation order

1. **Scaffold project** (~30 LOC csproj). Add to solution. NOT referenced from JJFlexRadio.vbproj — they're separate exes that talk via the staging dir contract.

2. **Command-line parsing** (~40 LOC) — accepts a single argument: path to the staging dir. The handoff-manifest.json lives at `<staging-dir>\handoff-manifest.json`. Parse it; bail with a clear error if it's missing or schema-mismatched.

3. **Wait for JJF to exit** (~50 LOC) — use the PID from handoff-manifest. Poll with `Process.GetProcessById(pid)` + Catch `ArgumentException` (means process already exited). Reasonable timeout — say 30 seconds — after which the helper bails (probably means JJF hung; user can retry manually). Log progress to a helper log file alongside the staging dir.

4. **Backup current files** (~80 LOC) — for every file in the `copy_files` list of handoff-manifest, copy the current version (if it exists) to `<staging-dir>\backup\<rel_path>`. This is the rollback safety net. If backup fails for any file, abort BEFORE making any changes.

5. **Atomic file replacement** (~120 LOC) — for each file in `copy_files`:
   - Compute sha256 of source file in `<staging-dir>\files\<rel_path>`
   - Verify against `expected_sha256` in handoff-manifest (if mismatch, abort and roll back — the file in staging is corrupt)
   - Copy source to `<target-dir>\<rel_path>.new`
   - Atomic rename: delete `<target-dir>\<rel_path>` if exists, rename `.new` → final name
   - Track each successful replacement so rollback knows what to undo

   **The `.new` + rename pattern is critical** — if the helper crashes mid-copy, the partial file has the `.new` extension and isn't loaded by JJF on restart.

6. **Delete obsolete files** (~50 LOC) — for each file in `delete_files`, delete it from target-dir. Best-effort: a missing file (already deleted) is success; an in-use file (locked) is failure → roll back. Track each deletion.

7. **Rollback path** (~120 LOC) — triggered by any failure during backup, replacement, or deletion. Steps:
   - Restore backed-up files from `<staging-dir>\backup\`
   - Re-create deleted files from backup
   - Remove any `.new` files created during the failed run
   - Log the failure cause to the helper log
   - Do NOT relaunch JJF (the install is broken; user needs to know)
   - Exit with a non-zero code

8. **Relaunch JJF** (~30 LOC) — `Process.Start` with the relaunch path from handoff-manifest. `UseShellExecute=true` so the new JJF gets its own process tree.

9. **Cleanup staging dir** (~20 LOC) — on success, delete the staging dir. On failure, leave it for forensic review. Document the staging dir path in the helper log so the user can find it.

10. **Helper log** (~30 LOC) — append-only log file at `<staging-dir>\helper.log`. Records: start time, JJF PID waited for, each file copied/deleted, success/failure of each operation, end time, exit reason. Useful when something goes wrong and the user files a bug.

## What MUST NOT regress (errr, MUST WORK from day one)

1. **Atomic replacement.** A crash during file replacement must NOT leave the install in a broken state. The backup-first + .new-rename-pattern + rollback-on-failure path is the load-bearing design.
2. **No data loss.** Backup files preserved until success is confirmed, then cleaned. If anything goes wrong, restore from backup.
3. **The helper is small and fast.** Time from JJF exit → JJF relaunch should be a few seconds for typical deltas (~10 files). Avoid heavy operations or unnecessary work.
4. **The helper handles "JJF didn't exit cleanly" gracefully.** If JJF crashes during the handoff prep, the staging dir is still there; the helper should detect and proceed (don't wait forever for a dead process).
5. **Single-instance-of-helper protection.** If the user somehow triggers two updates simultaneously, only one helper runs. Use a mutex or a lock-file in the staging dir's parent.

## Build commands

```batch
dotnet build JJFlexUpdaterHelper/JJFlexUpdaterHelper.csproj -c Debug -p:Platform=x64 --verbosity minimal
```

For testing:

```batch
REM Create a synthetic staging dir manually, then:
JJFlexUpdaterHelper.exe "C:\Temp\test-staging-dir"
```

Verify: backup created, files copied, JJF relaunched (or in test mode, JJF stub launched).

## Commit conventions

Every commit prefixed `Sprint 29 Track M:`. Examples:

- `Sprint 29 Track M: scaffold JJFlexUpdaterHelper project (self-contained single-file exe)`
- `Sprint 29 Track M: command-line parsing + handoff-manifest.json deserialization`
- `Sprint 29 Track M: wait-for-JJF-exit polling with timeout`
- `Sprint 29 Track M: backup-current-files step (pre-replacement safety net)`
- `Sprint 29 Track M: atomic file replacement with .new-rename pattern + sha256 verify`
- `Sprint 29 Track M: delete-obsolete-files step`
- `Sprint 29 Track M: rollback path for any-failure scenarios`
- `Sprint 29 Track M: JJF relaunch via Process.Start UseShellExecute`
- `Sprint 29 Track M: helper.log append-only forensic record`
- `Sprint 29 Track M: single-instance mutex protection`

## Success criteria (Definition of Done)

- [ ] `JJFlexUpdaterHelper.exe` builds clean as a self-contained single-file exe
- [ ] Helper accepts staging-dir path as command-line argument
- [ ] Handoff-manifest.json deserialization handles all required fields
- [ ] Wait-for-PID-exit works with reasonable timeout
- [ ] Backup of current files happens before any replacement
- [ ] Atomic file replacement with sha256 verify on source
- [ ] Obsolete file deletion works
- [ ] Rollback restores files correctly on synthetic failure scenarios
- [ ] JJF relaunch works after successful update
- [ ] helper.log captures forensic detail
- [ ] Single-instance protection prevents concurrent helper runs
- [ ] Test with synthetic staging dir scenarios: clean update, partial failure mid-copy (manually corrupted file), JJF doesn't exit (PID still alive timeout), backup fails (read-only target dir)
- [ ] Clean Debug build

## Cross-references

- `memory/project_sprint29_updater_vision.md` — primary spec (delta-update architecture)
- Track D's TRACK-INSTRUCTIONS — staging dir + handoff-manifest contracts
- Track N — server-side manifest generation (different track)
- Squirrel.Windows source for reference architecture (https://github.com/Squirrel/Squirrel.Windows)

## Resume hint

> Resume Sprint 29 Track M from TRACK-INSTRUCTIONS.md
