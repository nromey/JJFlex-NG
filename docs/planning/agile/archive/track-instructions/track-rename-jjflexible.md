# Track: Rename to jjflexible + build hygiene

Branched from `track/flexlib-4220` at `0dcb8f3d`. **Never merge `main` into this
branch** — main carries the FlexLib 4.2.18 revert and a "clean" merge silently
deletes the 4.2.20 vendor drop (sanity check: `wc -l FlexLib_API/FlexLib/Radio.cs`
must stay 15212, not 14471).

## Why now

Code signing (Microsoft Trusted Signing) happens right after this track.
SmartScreen reputation accrues against the signed file identity, so the exe must
carry its final name BEFORE the first signed release, not after.

## Scope — what changes

1. **Main assembly rename.** In `JJFlexRadio.vbproj` set
   `<AssemblyName>jjflexible</AssemblyName>`. That renames `JJFlexRadio.exe` →
   `jjflexible.exe` plus the paired `jjflexible.dll`, `.deps.json`,
   `.runtimeconfig.json`, `.pdb` automatically. Leave `RootNamespace` alone.
   Set `<Product>JJ Flexible Radio Access</Product>` and
   `<AssemblyTitle>JJ Flexible Radio Access</AssemblyTitle>` so file properties
   and Task Manager show the real name. Version plumbing (`<Version>` +
   BUILDNUM_OFFSET computation) must keep working unchanged.

2. **Support DLLs stay as-is.** Radios.dll, JJFlexWpf.dll, FlexLib.dll etc. are
   invisible to users; renaming them churns every project for zero user value.
   Decision made — do not revisit.

3. **AppData, registry, trace paths stay `JJFlexRadio`.** Non-negotiable:
   settings, per-radio configs (`radios\<serial>\config.xml`),
   SmartLinkAccounts.json, and Don's existing install all live there. Grep the
   diff to prove no AppData/registry string changed.

4. **NSIS installer** (`install template.nsi` + `install.bat`):
   - Shortcut targets, `Exec`/finish-run references, uninstaller entries → `jjflexible.exe`.
   - Installer filename stays `Setup JJFlex_<version>_<arch>.exe` (release
     scripts glob this pattern — do not change it).
   - **Upgrade cleanup (the trap):** `generate-deletelist.ps1` builds the
     uninstall list from the NEW publish output, so a machine upgrading from
     4.x keeps its old `JJFlexRadio.exe`, `JJFlexRadio.dll`,
     `JJFlexRadio.deps.json`, `JJFlexRadio.runtimeconfig.json`, `JJFlexRadio.pdb`,
     `JJFlexRadio.dll.config` — and the old Start Menu/desktop shortcuts keep
     launching the stale exe. Add explicit `Delete` lines for those legacy
     files and recreate/replace the shortcuts on install.
   - Check `JJFlexRadio.chm`: keep the chm filename as-is unless help-loading
     code derives it from the assembly name — verify, don't assume
     (grep for `.chm` in the VB and C# sources).

5. **Scripts and docs that name the exe.** Grep the whole repo for
   `JJFlexRadio.exe` (case-insensitive): `build-debug.bat`,
   `build-installers.bat`, `install.bat`, CLAUDE.md verification commands,
   any publish/backup .ps1. Update paths. Nightly zip naming stays
   `JJFlex_<version>_<arch>_debug.zip`.

6. **Single/multi-instance + process checks.** Verify how single-instance and
   the multi-instance trace naming (`JJFlexRadio2Trace.txt`) are keyed. If
   anything keys off `Process.GetCurrentProcess().ProcessName` or the assembly
   name, confirm behavior with the new name (trace FILE name may stay
   JJFlexRadio-based — that's an AppData artifact and fine). Also update any
   "is the app running" checks in build scripts (Radios.dll lock warning).

7. **build-debug.bat output hygiene.** Delete the output folder before building
   (mirror what build-installers.bat does). The Debug tree is carrying 13 stale
   satellite-language folders from May 11 that predate
   `<SatelliteResourceLanguages>en</SatelliteResourceLanguages>` — incremental
   builds never remove them and every nightly zip has been shipping them.
   One-time: also delete them from the current Debug tree.

## Verify before reporting done

- Clean build x64 AND x86 Release; `jjflexible.exe` present, timestamp current,
  `(Get-Item ...jjflexible.exe).VersionInfo.ProductName` says
  "JJ Flexible Radio Access", ProductVersion matches vbproj.
- Debug x64 build produces NO cs/de/es/... folders.
- Both installers build; extract or install one and confirm the shortcut target.
- Launch the built exe: settings, per-radio config, and SmartLink accounts from
  the existing install must all load (proves AppData path untouched).
- Grep check: no remaining `JJFlexRadio.exe` references outside docs/history.

## Commit style

Prefix `Rename:` on this branch. Commit in logical chunks (vbproj+code, NSIS,
scripts, docs). Report done to the orchestrator session; merge target is
`track/flexlib-4220`.
