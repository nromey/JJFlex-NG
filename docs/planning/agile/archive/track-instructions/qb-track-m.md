# QB Track M — install manifest (debug bundle goes from 190 MB to honest)

**Model: Fable.** One coherent feature: replace the debug bundle's
whole-install-directory zip with a hashed manifest plus self-verification.
Ratified by Noel 2026-08-07 evening ("Do the manifest change").

## Context

Post-ensemble follow-up of the 2026-08-07 queue-burn. JJ Flex is a
screen-reader-first FlexRadio client for blind hams. You work ONLY in
this worktree (`C:\dev\jjflex-qb-m`, branch `qb/track-m`) — never in
`C:\dev\JJFlex-NG`.

The problem: `DebugInfo.vb:87` does
`ZipUtils.AddDirectoryToArchive(archive, ".", "program")` — it zips the
entire install directory into every debug bundle. Pre-self-contained
that was ~25 MB of our files; since the .NET 10 self-contained migration
it's ~190 MB / 364 files, mostly Microsoft's runtime, identical on every
machine. It dominates the bundle, slows the save the user sits through,
and guarantees the 50 MB upload limit trips. The original diagnostic
intent (detect stale / corrupt / mixed installs) is better served by a
manifest diff.

## Work items

1. **Build-time known-good manifest.** Generate `install-manifest.json`
   into the build output as part of the build, listing every file in the
   output tree: relative path (forward slashes), size in bytes, SHA-256
   (lowercase hex), and FileVersion where the file has one. Exclude the
   manifest itself. Implementation guidance: an MSBuild target in
   `JJFlexRadio.vbproj` (AfterTargets="Build") running a PowerShell
   script checked in at the repo root (e.g. `generate-install-manifest.ps1`)
   — mirror the invocation style of the existing post-build installer
   hook; look at how `install.bat` is invoked and at
   `generate-deletelist.ps1` for the house pattern of walking the output
   tree. Must work for BOTH Debug and Release, x64 and x86, and must not
   break `build-installers.bat`. The NSIS deleteList generation walks the
   publish output, so the manifest cleans up automatically at uninstall —
   verify that assumption holds by reading `generate-deletelist.ps1`.
   Keep the target cheap (a couple of seconds); if hashing is measurably
   slow, hash in parallel.
2. **Debug bundle: manifest + self-verification instead of binaries.**
   In `DebugInfo.vb`, replace the `"program"` directory add with:
   (a) generate a LIVE manifest of the actual install directory at
   collection time (same schema, same exclusion), include it in the
   bundle as `program-manifest.json`;
   (b) if the shipped known-good `install-manifest.json` is present,
   diff live vs known-good and write `install-verification.txt` into the
   bundle: either a one-line "Install verified clean — N files match the
   shipped manifest." or a plain-prose list of mismatched (wrong
   hash/size), missing, and unexpected files, each with what was
   expected vs found. Prose and bullets only, no tables — this file gets
   read by screen reader users and by support.
   (c) include the shipped known-good manifest too (so support can diff
   against the exact release even if the live machine's copy is the
   thing that's corrupt).
   The bundle keeps everything else it already collects (config, traces
   per Track K's bounding, crash reports). Net effect: bundles shrink by
   ~190 MB and gain a self-diagnosis.
3. **Speech and status.** The collect-debug-info flow's completion
   message should reflect reality: it now finishes fast and the
   verification outcome is worth a clause — e.g. append "Install
   verified clean" or "Install verification found N differences — see
   install-verification.txt" to whatever the flow already speaks/shows.
   Follow the existing speech pattern at that site; no new dialogs.
4. **Failure honesty.** If the known-good manifest is absent (dev tree,
   ancient install), the verification file says so plainly and the flow
   continues — a missing manifest must never block bundle collection.
   Same if a file can't be read for hashing (report it as unreadable,
   continue).
5. **Changelog.** One entry in the house voice (read the Changelog
   Conventions in CLAUDE.md first): debug bundles are now small and
   fast, and they check the installation's integrity while they're at
   it. No jargon, no track letters, no "SHA-256" in the user prose
   (say "fingerprint").
6. **Housekeeping.** If `ZipUtils.AddDirectoryToArchive`'s
   whole-directory overload has no remaining callers after this change,
   note it in your report (do NOT delete — other tools may want it).

## Rules

- Every change speaks; errors never suppressed; no silent keystrokes.
- Build from the worktree root after every item:
  `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
  — verify `bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe` has a
  fresh timestamp each time, AND verify `install-manifest.json` appears
  in the output tree with plausible content (spot-check one hash against
  `Get-FileHash`).
- Commit per item: `QB Track M: <what changed>`. Push to origin after
  every commit (`git push origin qb/track-m`). Never push to upstream.
- No new key bindings, no version bump, nothing outside the items above.
- You run unattended: if an item can't complete cleanly, file it under
  "Deferred" with the reason and continue.

## Final report

Completed items, deferred items with reasons, the manifest schema you
settled on, measured build-time cost of the manifest target, measured
bundle-size before/after if obtainable, build status, final pushed SHA.
Append a "Design decisions" section to this file (committed).

## Design decisions

Written after implementation, 2026-08-07.

- **MSBuild anchoring deviates from the spec's `AfterTargets="Build"`
  guidance, deliberately.** The PostBuildEvent (install.bat → NSIS
  `File /r` + generate-deletelist.ps1) is the LAST dependency inside
  CoreBuild, so an `AfterTargets="Build"` target runs after the
  installer has already packaged the output — the manifest would miss
  the installer, or worse, a stale manifest from the previous build
  would ship. The GenerateInstallManifest target anchors
  `AfterTargets="CopyFilesToOutputDirectory"` (all outputs present,
  including the self-contained runtime) and
  `BeforeTargets="PostBuildEvent"`. Verified empirically in a Release
  build log: GenerateInstallManifest executes before PostBuildEvent,
  and generate-deletelist.ps1 emits `Delete "$INSTDIR\install-manifest.json"`,
  so uninstall cleanup is automatic as assumed.
- **Schema `jjflex-install-manifest/1`**, camelCase properties, two
  writers (PowerShell at build, VB at collection) sharing it. Top level:
  `schema`, `source` ("build" or "live"), `product`, `version` (the
  exe's 4-part FileVersion — ProductVersion can carry a +hash suffix),
  `generated` (UTC ISO 8601), `fileCount`, `totalBytes`,
  `configuration`/`platform` (build manifests), `unreadable` (live
  manifests, only when non-empty), `files`. Per file: `path`
  (forward-slash relative), `size`, `sha256` (lowercase hex),
  `fileVersion` (omitted when the file has no version resource).
  Optional fields are omitted, not null. The manifest excludes itself
  by root-relative name, both writers alike, so live-vs-shipped diffs
  align.
- **Cross-writer round-trip is tested, not assumed.** A scratchpad VB
  harness compiled InstallManifest.vb against a fake install whose
  manifest came from the real generate-install-manifest.ps1: tampered,
  deleted, and added files each landed in the right category with the
  right prose; the untouched tree verified clean.
- **Unreadable files: build fails, runtime reports.** At build time an
  unreadable output file is a broken build and the script throws
  ($ErrorActionPreference Stop). At collection time an unreadable file
  is recorded (path + reason) under `unreadable`, reported in its own
  section, counted as a difference (unverified is not verified), and
  never stops the walk.
- **The diff treats paths case-insensitively** (Windows filesystem
  semantics) and reports four categories: mismatched (size, or same
  size with different fingerprint — worded distinctly), missing,
  unexpected, unreadable. Missing and unreadable are disjoint: a file
  the live scan could not read is reported as unreadable, not missing.
- **Fingerprints in prose show the first 12 hex characters.** A screen
  reader speaking 64 hex characters per file is hostile; 12 is plenty
  to talk about. The full values ride in the two JSON manifests in the
  same bundle. User-facing text says "fingerprint," never "SHA-256."
- **The install directory is `AppContext.BaseDirectory`, not `"."`.**
  The old code zipped the process's current directory, which drifts
  with how the app was launched. BaseDirectory is where the program
  actually runs from.
- **The live manifest's version comes from the running process**
  (`Environment.ProcessPath`), not from whatever exe sits in the
  directory — if those differ, the file-level diff will say so anyway.
- **Corrupt shipped manifest is a finding, not a failure.** The raw
  manifest bytes still go into the bundle (a damaged manifest is
  evidence of a damaged install), the report says what could not be
  read and why, and collection continues. A catch-all around the whole
  verification step writes an honest note even on unforeseen failures —
  the bundle itself must always complete.
- **Cost, measured:** ~400-470 ms script time for 372 files / ~176 MB;
  ~525-555 ms end-to-end per build including PowerShell startup. Well
  inside the couple-of-seconds budget, so no parallel hashing. The
  target runs on every build (no Inputs/Outputs — the input set is the
  whole output tree, and a wrong-stale manifest is worse than half a
  second).
- **Size, measured (Debug x64):** the old `program/` portion was
  168.3 MB raw / ~74 MB compressed inside every bundle (Release with
  ReadyToRun runs larger still); the replacement is two ~107 KB JSONs
  plus a text report — they compress to well under 100 KB combined.
  Collection also drops the ~4+ seconds it spent deflating the program
  tree.
- **`ZipUtils.AddDirectoryToArchive` stays.** It is not orphaned:
  DebugInfo.vb:60 (AppData sweep) and ExportSetup.vb:26 (settings
  export) still call it. Only the whole-program-directory call site is
  gone.
