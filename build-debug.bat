@echo off
REM build-debug.bat - Build Debug x64 zip, archive to NAS, optionally publish to Dropbox
REM Usage: build-debug.bat [--publish ^| --testers] [--no-commit]
REM
REM   (default)        build + zip, copy to NAS debug\ with timestamp.
REM                    Does NOT touch Dropbox — internal iteration only.
REM   --publish        also purge and copy to Dropbox debug\ for testers.
REM   --testers        synonym for --publish.
REM   --no-commit      skip the working-tree-clean check (not recommended —
REM                    the Y number in the exe won't reproduce from any commit).
REM
REM VERSIONING
REM   Same rules as build-installers.bat. Base version lives in JJFlexRadio.vbproj.
REM   Y = git rev-list --count HEAD + BUILDNUM_OFFSET (see below).
REM
REM LAYOUT
REM   NAS  \\nas.macaw-jazz.ts.net\jjflex\historical\<ver>\x64-debug\
REM        Every build drops here: jjflexible.exe + .pdb (overwritten per
REM        version) plus the distributed zip + NOTES (timestamped, never
REM        overwritten). Bisect uses the zips; symbolication uses exe+pdb.
REM        Builds archived before the 4.2.x rename carry JJFlexRadio.exe/.pdb
REM        under their own version folders — that history stays as it was.
REM   Dropbox <DropboxRoot>\JJFlexRadio\debug\JJFlex_<ver>_x64_debug.zip
REM           only written on --publish. The new zip + NOTES are copied and
REM           read back at the right length FIRST; only then are the older
REM           debug files removed, so a failed publish leaves testers holding
REM           the previous build rather than nothing. LATEST.txt names the
REM           current pair outright instead of leaving it implied by the
REM           folder holding one file. (task #230)
REM
REM EXIT CODES
REM   0  built, archived, and (if asked) published — all verified at rest
REM   1  version could not be determined / not a git tree
REM   2  working tree dirty (use --no-commit to override)
REM   3  build failed
REM   4  build output missing
REM   5  zip failed
REM   6  a JJ Flexible instance is running (would lock Radios.dll)
REM   7  NOTES generation failed or produced no file
REM   8  Dropbox publish failed — NOTHING was deleted, testers keep their build
REM   9  NAS archive incomplete — this version has no bisectable copy
REM  10  a helper script under scripts\ is missing — see PREFLIGHT below
REM
REM NOTES FILE
REM   If debug-notes.txt exists at the repo root, its contents are used as the
REM   NOTES file body (with a header prepended). Otherwise a minimal auto-
REM   generated NOTES is produced from recent git log. Either way the output
REM   filename is NOTES-<version>-debug.txt alongside the zip.
REM
REM BUILD IDENTITY (task #268)
REM   BUILD-INFO.txt is written into the build tree before zipping, so it is
REM   INSIDE the artifact and survives delivery. Dropbox re-stamps a delivered
REM   file with the recipient's sync time, so no timestamp on a tester's disk
REM   says anything about when the build was made. The 4-part version, the
REM   build time and the commit are written into the file itself instead.
REM
REM RUNNING THIS FROM GIT BASH
REM   `cmd //c build-debug.bat` fails with "'build-debug.bat' is not recognized",
REM   even standing in this directory. Git Bash exports
REM   NoDefaultCurrentDirectoryInExePath=1, which tells cmd to stop looking in
REM   the current directory for a bare name. Name the path and it works:
REM     cmd //c ".\build-debug.bat"
REM     cmd //c "C:\dev\JJFlex-NG\build-debug.bat"
REM   Measured 2026-08-27 — the bare form is the only one that fails.

setlocal enabledelayedexpansion
cd /d "%~dp0"

REM ---------------------------------------------------------------------------
REM CONFIG
REM ---------------------------------------------------------------------------
set "BUILDNUM_OFFSET=-468"
REM Dropbox root is machine-dependent (D:\Dropbox on the ms-02; the old
REM hardcoded default was C:\Users\nrome\Dropbox). Resolve it from
REM Dropbox's own info.json; the hardcoded path once sent --publish into
REM an unsynced dead folder.
set "DROPBOX_ROOT="
for /f "usebackq delims=" %%d in (`powershell -NoProfile -Command "try { (Get-Content (Join-Path $env:LOCALAPPDATA 'Dropbox\info.json') -Raw | ConvertFrom-Json).personal.path } catch { '' }"`) do set "DROPBOX_ROOT=%%d"
if "%DROPBOX_ROOT%"=="" set "DROPBOX_ROOT=C:\Users\nrome\Dropbox"
set "DROPBOX_DEBUG=%DROPBOX_ROOT%\JJFlexRadio\debug"
set "NAS_HISTORICAL=\\nas.macaw-jazz.ts.net\jjflex\historical"

REM ---------------------------------------------------------------------------
REM ARGS
REM ---------------------------------------------------------------------------
set "PUBLISH=0"
set "NOCOMMIT=0"
:parse_args
if "%~1"=="" goto end_parse_args
if /I "%~1"=="--publish" (
    set "PUBLISH=1"
    shift
    goto parse_args
)
if /I "%~1"=="--testers" (
    set "PUBLISH=1"
    shift
    goto parse_args
)
if /I "%~1"=="--no-commit" (
    set "NOCOMMIT=1"
    shift
    goto parse_args
)
echo WARNING: unknown argument: %~1
shift
goto parse_args
:end_parse_args

REM ---------------------------------------------------------------------------
REM PREFLIGHT — the helper scripts must exist BEFORE anything is attempted.
REM ---------------------------------------------------------------------------
REM Every helper below is invoked as `powershell ... -File "%~dp0scripts\X.ps1"`
REM and every call site guards with `if errorlevel 1`. That guard does not cover
REM a missing helper, and the way it fails is worth stating exactly, because the
REM shape of it is counter-intuitive:
REM
REM   powershell -File <path that does not exist>   ->   errorlevel -196608
REM
REM `if errorlevel N` in cmd is a GREATER-THAN-OR-EQUAL test, and -196608 is not
REM greater than or equal to 1. So the guard does not fire, the script carries
REM on, and the first visible symptom is some later step failing on a file that
REM was never produced. Measured on this machine 2026-08-27, not assumed.
REM
REM CLAUDE.md warns about `powershell -File <path>` and describes the failure as
REM "exits 0 silently". The exit code is negative rather than zero; the
REM consequence is the same and slightly worse, since a negative code also
REM defeats a `neq 0` written the obvious way. One check up front, naming the
REM file, costs nothing and closes all of them at once.
set "HELPER_MISSING="
for %%H in (build-debug-zip.ps1 build-debug-notes.ps1 archive-debug-to-nas.ps1 publish-debug-to-dropbox.ps1) do (
    if not exist "%~dp0scripts\%%H" (
        echo ERROR: helper script missing: %~dp0scripts\%%H
        set "HELPER_MISSING=1"
    )
)
if defined HELPER_MISSING (
    echo.
    echo   One or more helpers under scripts\ are not there. They are not
    echo   optional - the build, the zip, the NOTES, the NAS archive and the
    echo   Dropbox publish are all delegated to them.
    echo.
    exit /b 10
)

echo ============================================
echo JJ Flex Debug Builder
if "%PUBLISH%"=="1" (
    echo Mode: build + NAS + Dropbox publish
) else (
    echo Mode: build + NAS only - Dropbox untouched
)
echo ============================================
echo.

REM ---------------------------------------------------------------------------
REM Working-tree clean check
REM ---------------------------------------------------------------------------
if "%NOCOMMIT%"=="0" (
    REM Use full path to Windows find.exe so PATH shadowing (e.g. when
    REM cmd is launched from Git Bash, which puts GNU find ahead of
    REM Windows find) cannot redirect us into a recursive C: scan.
    for /f %%c in ('git status --porcelain 2^>nul ^| %SystemRoot%\System32\find.exe /c /v ""') do set "DIRTY=%%c"
    if not "!DIRTY!"=="0" (
        echo.
        echo ERROR: Working tree has !DIRTY! uncommitted change^(s^).
        echo   Debug builds should be reproducible from HEAD - commit first, build second.
        echo   Run with --no-commit to build anyway (Y in the exe won't reproduce^).
        echo.
        exit /b 1
    )
)

REM ---------------------------------------------------------------------------
REM Compute version
REM ---------------------------------------------------------------------------
set "BASEVER="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Select-String -Path 'JJFlexRadio.vbproj' -Pattern '<Version>([0-9][0-9.]*)</Version>' | Select-Object -First 1).Matches.Groups[1].Value"`) do set "BASEVER=%%v"
if "%BASEVER%"=="" (
    echo ERROR: Could not read ^<Version^> from JJFlexRadio.vbproj
    exit /b 2
)

for /f "usebackq delims=" %%c in (`git rev-list --count HEAD 2^>nul`) do set "GITCOUNT=%%c"
if "%GITCOUNT%"=="" (
    echo WARNING: git rev-list failed. Using Y=0.
    set "GITCOUNT=0"
)
set /a "BUILDNUM=%GITCOUNT% + %BUILDNUM_OFFSET%"
set "APPVER=%BASEVER%.%BUILDNUM%"

for /f "usebackq delims=" %%h in (`git rev-parse --short HEAD 2^>nul`) do set "GITSHA=%%h"
if "%GITSHA%"=="" set "GITSHA=unknown"

echo Base version      : %BASEVER%
echo Git commit count  : %GITCOUNT%
echo Build number (Y)  : %BUILDNUM%
echo Full version      : %APPVER%
echo Git SHA (short)   : %GITSHA%
echo.

REM ---------------------------------------------------------------------------
REM Help CHM refresh (Sprint 27 Track E audit follow-up).
REM The exe bundles docs\help\JJFlexRadio.chm via <None Include="..."> in
REM JJFlexRadio.vbproj. If the CHM is stale, testers see outdated help. Run
REM build-help.bat before dotnet build so the CHM reflects the current
REM docs\help\md\*.md sources. Non-fatal on failure — if HTML Help Workshop
REM isn't installed, log and continue with the prior CHM (or none).
REM ---------------------------------------------------------------------------
echo.
echo [Help] Refreshing CHM from docs\help\md\*.md ...
call "%~dp0docs\help\build-help.bat"
if errorlevel 1 (
    echo WARNING: CHM build failed or HTML Help Workshop missing - proceeding with stale/no CHM.
    echo          Testers will see whatever JJFlexRadio.chm currently exists in docs\help\.
)
echo.

REM ---------------------------------------------------------------------------
REM Build
REM ---------------------------------------------------------------------------
set "BIN_DIR=bin\x64\Debug\net10.0-windows\win-x64"

REM A running instance holds Radios.dll (and its own exe) open, which makes the
REM purge below fail silently and then the build fail with a confusing file-lock
REM error. Say so up front instead. Both names are checked: pre-rename builds
REM run as JJFlexRadio.exe, current ones as jjflexible.exe.
for /f "usebackq delims=" %%r in (`powershell -NoProfile -Command "@(Get-Process -Name 'jjflexible','JJFlexRadio' -ErrorAction SilentlyContinue).Count"`) do set "RUNNING=%%r"
if not "%RUNNING%"=="0" (
    echo.
    echo ERROR: JJ Flexible is running ^(%RUNNING% process^(es^)^). Close it and re-run -
    echo   a running instance locks Radios.dll and the exe.
    echo.
    exit /b 6
)

REM Purge the output folder first, the way build-installers.bat does for
REM Release. Incremental Debug builds never remove files that stopped being
REM produced, so the tree accumulated 13 satellite-language folders (cs\, de\,
REM es\, ...) left over from before <SatelliteResourceLanguages>en</> was set —
REM and every nightly zip shipped them. It also guarantees a stale
REM JJFlexRadio.exe from a pre-rename build can't ride along in the zip.
if exist "bin\x64\Debug" (
    echo [x64 Debug] Cleaning previous build output...
    rmdir /s /q "bin\x64\Debug"
)

echo [x64 Debug] Building as %APPVER%...
dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal -p:Version=%APPVER%
if errorlevel 1 (
    echo ERROR: Debug x64 build failed
    exit /b 1
)
echo.

if not exist "%BIN_DIR%\jjflexible.exe" (
    echo ERROR: Expected exe not found at %BIN_DIR%\jjflexible.exe
    exit /b 3
)

REM Verify FileVersion stamped correctly
for /f "usebackq delims=" %%f in (`powershell -NoProfile -Command "(Get-Item '%BIN_DIR%\jjflexible.exe').VersionInfo.FileVersion"`) do set "EXEVER=%%f"
if /I not "%EXEVER%"=="%APPVER%" (
    echo ERROR: exe FileVersion is %EXEVER% but we expected %APPVER%
    echo   Clean build might be needed; try: dotnet clean
    exit /b 4
)
echo Built exe version : %EXEVER%  (matches expected)

REM The exe's own last-write time IS the build time, and it is the only clock
REM reading that stays true after the artifact leaves this machine. Captured
REM once here and handed to the NOTES/BUILD-INFO helper, so the two files
REM cannot straddle a minute boundary and disagree (task #268).
set "BUILT="
for /f "usebackq delims=" %%b in (`powershell -NoProfile -Command "(Get-Item '%BIN_DIR%\jjflexible.exe').LastWriteTime.ToString('yyyy-MM-dd HH:mm')"`) do set "BUILT=%%b"
if "%BUILT%"=="" (
    echo ERROR: could not read the built exe's timestamp. Refusing to stamp a
    echo   build identity from the wall clock - a wrong date confidently printed
    echo   is exactly the problem BUILD-INFO.txt exists to solve.
    exit /b 4
)
echo Built at          : %BUILT%
echo.

REM ---------------------------------------------------------------------------
REM Bundle tools — Debug builds include SmartLinkSessionHarness (Sprint 26).
REM The harness lives outside the main bin dir; copy it into a `tools\harness\`
REM subfolder of the main bin dir so the single zip below picks it up.
REM Release installers DO NOT go through this path and therefore do not bundle
REM the harness — build-installers.bat builds the vbproj only, not the sln.
REM ---------------------------------------------------------------------------
REM RID-specific since task #135, so the path carries win-x64 exactly as the
REM main app's does. Without the RID the harness dragged sixteen Android,
REM Linux and macOS natives into every tester zip.
REM THE ARROW IN THE ECHO BELOW IS ESCAPED (-^>) AND MUST STAY THAT WAY.
REM This is task #133's second failure, which sat undiagnosed from 2026-08-19
REM until 2026-08-27. The line read `echo ... %HARNESS_SRC%\ -> %HARNESS_DST%\`,
REM and cmd read the bare > as a REDIRECTION: it tried to open a file at the
REM path %HARNESS_DST%\, which is a directory, printed
REM
REM     The system cannot find the path specified.
REM
REM swallowed the message it was supposed to print, set errorlevel 0, and
REM carried on. Every symptom matched: an unattributed error appearing between
REM the version check and "Creating zip:", nothing named, nothing broken, and
REM the "Bundling harness" line simply absent. It reproduced on both branches
REM of the question people kept asking - it fails whether or not the
REM destination folder already exists, because a directory is never a valid
REM redirection target.
set "HARNESS_SRC=tools\SmartLinkSessionHarness\bin\x64\Debug\net10.0-windows\win-x64"
set "HARNESS_DST=%BIN_DIR%\tools\harness"
set "HARNESS_STATUS=not bundled"
if exist "%HARNESS_SRC%\SmartLinkSessionHarness.exe" (
    echo Bundling harness  : %HARNESS_SRC%\ -^> %HARNESS_DST%\
    if not exist "%HARNESS_DST%" mkdir "%HARNESS_DST%"
    if not exist "%HARNESS_DST%" (
        echo   WARNING: could not create %HARNESS_DST% - harness NOT bundled.
    ) else (
        xcopy /Y /Q /E "%HARNESS_SRC%\*" "%HARNESS_DST%\" >nul
        REM Also copy the harness README so testers see usage docs alongside the exe.
        if exist "tools\SmartLinkSessionHarness\README.md" (
            copy /Y "tools\SmartLinkSessionHarness\README.md" "%HARNESS_DST%\README.md" >nul
        )
        REM Checked rather than assumed. A silent step failure in a distribution
        REM script is how a build ships missing a component nobody notices - the
        REM other half of what #133 found here.
        if exist "%HARNESS_DST%\SmartLinkSessionHarness.exe" (
            set "HARNESS_STATUS=bundled"
        ) else (
            echo   WARNING: the copy ran and SmartLinkSessionHarness.exe is not at
            echo            %HARNESS_DST% - this zip has NO harness in it.
        )
    )
) else (
    echo WARNING: harness exe not found at %HARNESS_SRC% - proceeding without it.
    echo   ^(sln build should produce it; check that SmartLinkSessionHarness.csproj
    echo   is still in JJFlexRadio.sln.^)
)
echo Harness           : !HARNESS_STATUS!
echo.

REM ---------------------------------------------------------------------------
REM NOTES + BUILD-INFO, then zip
REM ---------------------------------------------------------------------------
REM ORDER MATTERS AND IT CHANGED (task #268). The NOTES step used to run AFTER
REM the zip. It now runs first, because the same helper also writes
REM BUILD-INFO.txt INTO the build tree, and that file has to be there before the
REM tree is zipped or it does not travel with the artifact.
REM
REM Running it first has a second benefit: a NOTES failure now costs nothing,
REM where before it came after a minute of compression.
set "STAMP="
for /f "usebackq delims=" %%s in (`powershell -NoProfile -Command "Get-Date -Format 'yyyyMMdd-HHmm'"`) do set "STAMP=%%s"

set "ZIP_NAME=JJFlex_%APPVER%_x64_debug.zip"
set "ZIP_PATH=%TEMP%\%ZIP_NAME%"
set "NOTES_NAME=NOTES-%APPVER%-debug.txt"
set "NOTES_PATH=%TEMP%\%NOTES_NAME%"
set "BUILDINFO_PATH=%CD%\%BIN_DIR%\BUILD-INFO.txt"

echo Generating NOTES: %NOTES_PATH%
REM Delegated to scripts\build-debug-notes.ps1. Inline PowerShell inside an
REM if/else batch block is too fragile (the "(Debug x64)" parens in the NOTES
REM header text confused cmd.exe's parser — it closed the if-block early).
REM Helper file accepts -Version/-GitSha/-Built/-OutPath/-BodyPath/-BuildInfoPath
REM and produces the same output cleanly.
if exist "%~dp0debug-notes.txt" (
    echo   using debug-notes.txt at repo root
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-debug-notes.ps1" -Version "%APPVER%" -GitSha "%GITSHA%" -Built "%BUILT%" -OutPath "%NOTES_PATH%" -BuildInfoPath "%BUILDINFO_PATH%" -BodyPath "%~dp0debug-notes.txt"
) else (
    echo   auto-generating from recent git log
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-debug-notes.ps1" -Version "%APPVER%" -GitSha "%GITSHA%" -Built "%BUILT%" -OutPath "%NOTES_PATH%" -BuildInfoPath "%BUILDINFO_PATH%"
)
REM Checked, because everything downstream copies this file around and a
REM missing NOTES would first be noticed by a tester with no notes (#230).
if errorlevel 1 (
    echo.
    echo ERROR: generating the NOTES file failed. The reason is printed above.
    exit /b 7
)
if not exist "%NOTES_PATH%" (
    echo.
    echo ERROR: the NOTES helper exited 0 and produced no file at %NOTES_PATH%.
    exit /b 7
)
if not exist "%BUILDINFO_PATH%" (
    echo.
    echo ERROR: BUILD-INFO.txt is not at %BUILDINFO_PATH%, so the zip would carry
    echo   no identity and a tester could not date this build from the artifact.
    exit /b 7
)

REM ---------------------------------------------------------------------------
REM Zip
REM ---------------------------------------------------------------------------
REM Delegated to scripts\build-debug-zip.ps1, which uses System.IO.Compression
REM directly instead of Compress-Archive. Compress-Archive could not be loaded
REM at all on this machine and the batch file's guess about the cause - a file
REM lock from a running app - was wrong. The real mechanism needs BOTH halves
REM and neither on its own does it; see the header of that helper, which now
REM carries the measurements. -ExecutionPolicy Bypass is required for the helper
REM itself to run, exactly as for the NOTES helper above.
echo Creating zip: %ZIP_PATH%
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-debug-zip.ps1" -SourceDir "%CD%\%BIN_DIR%" -DestPath "%ZIP_PATH%"
if errorlevel 1 (
    echo.
    echo ERROR: zip failed. The reason is printed directly above by the zip helper.
    echo   It is NOT a running instance of the app: this script already checked
    echo   for that before building and would have stopped with exit code 6.
    echo.
    exit /b 5
)

REM ---------------------------------------------------------------------------
REM NAS archive (always) — everything lands in historical\<ver>\x64-debug\
REM   jjflexible.exe + .pdb  — overwritten per version (symbolication target)
REM   JJFlex_<ver>_x64_debug_<stamp>.zip  — timestamped, never overwritten
REM   NOTES-<ver>-debug_<stamp>.txt       — matches the zip
REM ---------------------------------------------------------------------------
REM Delegated to scripts\archive-debug-to-nas.ps1, which copies then READS EACH
REM FILE BACK at the right length before naming it. Until 2026-08-26 this was
REM four unchecked Copy-Item calls followed by three unconditional echoes, so a
REM Tailscale drop mid-copy printed exactly what a good archive printed. See
REM task #230 and the header of that helper.
set "NAS_HIST_DIR=%NAS_HISTORICAL%\%APPVER%\x64-debug"
set "NAS_STATUS=ok"
echo.
echo NAS archive: %NAS_HIST_DIR%
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\archive-debug-to-nas.ps1" -ZipPath "%ZIP_PATH%" -NotesPath "%NOTES_PATH%" -ExePath "%CD%\%BIN_DIR%\jjflexible.exe" -PdbPath "%CD%\%BIN_DIR%\jjflexible.pdb" -DestDir "%NAS_HIST_DIR%" -Stamp "%STAMP%" -Version "%APPVER%"
if errorlevel 10 (
    echo   WARNING: skipped NAS archive ^(offline or no Tailscale^).
    echo            This version has no bisectable copy on the NAS.
    set "NAS_STATUS=skipped"
) else if errorlevel 1 (
    echo   The NAS archive FAILED. Detail is printed above.
    set "NAS_STATUS=FAILED"
)

REM ---------------------------------------------------------------------------
REM Dropbox publish (only with --publish)
REM ---------------------------------------------------------------------------
REM Delegated to scripts\publish-debug-to-dropbox.ps1. The old inline version
REM PURGED THE TESTER FOLDER FIRST, checked neither the purge nor the copies,
REM and echoed the filenames unconditionally — so a failed copy left testers
REM with an empty folder while the console reported a successful publish. The
REM helper copies, reads back at the right length, and only then removes the
REM older files. See task #230 and the header of that helper.
set "PUBLISH_STATUS=not published"
if "%PUBLISH%"=="1" (
    echo.
    echo Dropbox publish: %DROPBOX_DEBUG%
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish-debug-to-dropbox.ps1" -ZipPath "%ZIP_PATH%" -NotesPath "%NOTES_PATH%" -DestDir "%DROPBOX_DEBUG%" -Version "%APPVER%" -Built "%BUILT%" -GitSha "%GITSHA%"
    if errorlevel 1 (
        set "PUBLISH_STATUS=FAILED"
    ) else (
        set "PUBLISH_STATUS=published"
    )
) else (
    echo.
    echo Dropbox: NOT published ^(use --publish to broadcast to testers^).
)

REM ---------------------------------------------------------------------------
REM Summary — reports what happened, not what was attempted
REM ---------------------------------------------------------------------------
echo.
echo ============================================
echo Version %APPVER%  (Debug x64^)
echo Built at %BUILT%  (commit %GITSHA%^)
echo Zip at %ZIP_PATH%
echo Notes at %NOTES_PATH%
echo Harness: !HARNESS_STATUS!
echo NAS archive: !NAS_STATUS!
if "%PUBLISH%"=="1" echo Dropbox publish: !PUBLISH_STATUS!
echo ============================================

if "!PUBLISH_STATUS!"=="FAILED" (
    echo.
    echo THE TESTER PUBLISH DID NOT HAPPEN. Testers still have their previous
    echo build - nothing was deleted. Fix the cause above and re-run --publish.
    endlocal
    exit /b 8
)
if "!NAS_STATUS!"=="FAILED" (
    echo.
    echo THE NAS ARCHIVE DID NOT COMPLETE. The zip is still at %ZIP_PATH%.
    endlocal
    exit /b 9
)

echo.
echo Done.

endlocal
