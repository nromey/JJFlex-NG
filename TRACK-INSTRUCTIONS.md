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
