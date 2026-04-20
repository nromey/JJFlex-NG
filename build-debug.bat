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
REM        Every build drops here: JJFlexRadio.exe + .pdb (overwritten per
REM        version) plus the distributed zip + NOTES (timestamped, never
REM        overwritten). Bisect uses the zips; symbolication uses exe+pdb.
REM   Dropbox C:\Users\nrome\Dropbox\JJFlexRadio\debug\JJFlex_<ver>_x64_debug.zip
REM           only written on --publish; purges prior debug files first so the
REM           folder holds exactly one current zip + NOTES for testers.
REM
REM NOTES FILE
REM   If debug-notes.txt exists at the repo root, its contents are used as the
REM   NOTES file body (with a header prepended). Otherwise a minimal auto-
REM   generated NOTES is produced from recent git log. Either way the output
REM   filename is NOTES-<version>-debug.txt alongside the zip.

setlocal enabledelayedexpansion
cd /d "%~dp0"

REM ---------------------------------------------------------------------------
REM CONFIG
REM ---------------------------------------------------------------------------
set "BUILDNUM_OFFSET=-468"
set "DROPBOX_DEBUG=C:\Users\nrome\Dropbox\JJFlexRadio\debug"
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
        echo   Debug builds should be reproducible from HEAD — commit first, build second.
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
REM Build
REM ---------------------------------------------------------------------------
echo [x64 Debug] Building as %APPVER%...
dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal -p:Version=%APPVER%
if errorlevel 1 (
    echo ERROR: Debug x64 build failed
    exit /b 1
)
echo.

set "BIN_DIR=bin\x64\Debug\net10.0-windows\win-x64"
if not exist "%BIN_DIR%\JJFlexRadio.exe" (
    echo ERROR: Expected exe not found at %BIN_DIR%\JJFlexRadio.exe
    exit /b 3
)

REM Verify FileVersion stamped correctly
for /f "usebackq delims=" %%f in (`powershell -NoProfile -Command "(Get-Item '%BIN_DIR%\JJFlexRadio.exe').VersionInfo.FileVersion"`) do set "EXEVER=%%f"
if /I not "%EXEVER%"=="%APPVER%" (
    echo ERROR: exe FileVersion is %EXEVER% but we expected %APPVER%
    echo   Clean build might be needed; try: dotnet clean
    exit /b 4
)
echo Built exe version : %EXEVER%  (matches expected)
echo.

REM ---------------------------------------------------------------------------
REM Zip + NOTES
REM ---------------------------------------------------------------------------
set "STAMP="
for /f "usebackq delims=" %%s in (`powershell -NoProfile -Command "Get-Date -Format 'yyyyMMdd-HHmm'"`) do set "STAMP=%%s"

set "ZIP_NAME=JJFlex_%APPVER%_x64_debug.zip"
set "ZIP_PATH=%TEMP%\%ZIP_NAME%"
set "NOTES_NAME=NOTES-%APPVER%-debug.txt"
set "NOTES_PATH=%TEMP%\%NOTES_NAME%"

echo Creating zip: %ZIP_PATH%
powershell -NoProfile -Command "Compress-Archive -Path '%CD%\%BIN_DIR%\*' -DestinationPath '%ZIP_PATH%' -Force"
if errorlevel 1 (
    echo ERROR: zip failed ^(is JJFlexRadio.exe locked by a running instance?^)
    exit /b 5
)

echo Generating NOTES: %NOTES_PATH%
REM Delegated to scripts\build-debug-notes.ps1. Inline PowerShell inside an
REM if/else batch block is too fragile (the "(Debug x64)" parens in the NOTES
REM header text confused cmd.exe's parser — it closed the if-block early).
REM Helper file accepts -Version/-GitSha/-OutPath/-BodyPath and produces the
REM same output cleanly.
if exist "%~dp0debug-notes.txt" (
    echo   using debug-notes.txt at repo root
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-debug-notes.ps1" -Version "%APPVER%" -GitSha "%GITSHA%" -OutPath "%NOTES_PATH%" -BodyPath "%~dp0debug-notes.txt"
) else (
    echo   auto-generating from recent git log
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-debug-notes.ps1" -Version "%APPVER%" -GitSha "%GITSHA%" -OutPath "%NOTES_PATH%"
)

REM ---------------------------------------------------------------------------
REM NAS archive (always) — everything lands in historical\<ver>\x64-debug\
REM   JJFlexRadio.exe + .pdb  — overwritten per version (symbolication target)
REM   JJFlex_<ver>_x64_debug_<stamp>.zip  — timestamped, never overwritten
REM   NOTES-<ver>-debug_<stamp>.txt       — matches the zip
REM ---------------------------------------------------------------------------
set "NAS_HIST_DIR=%NAS_HISTORICAL%\%APPVER%\x64-debug"
echo.
echo NAS archive: %NAS_HIST_DIR%
powershell -NoProfile -Command "if (-not (Test-Path -LiteralPath '%NAS_HISTORICAL%')) { Write-Host '  WARNING: NAS not reachable, skipping NAS archive'; exit 10 }"
if errorlevel 10 (
    echo   WARNING: skipped NAS archive ^(offline or no Tailscale^)
) else (
    powershell -NoProfile -Command "New-Item -Path '%NAS_HIST_DIR%' -ItemType Directory -Force | Out-Null"
    powershell -NoProfile -Command "Copy-Item -LiteralPath '%ZIP_PATH%' -Destination '%NAS_HIST_DIR%\JJFlex_%APPVER%_x64_debug_%STAMP%.zip' -Force"
    powershell -NoProfile -Command "Copy-Item -LiteralPath '%NOTES_PATH%' -Destination '%NAS_HIST_DIR%\NOTES-%APPVER%-debug_%STAMP%.txt' -Force"
    powershell -NoProfile -Command "Copy-Item -LiteralPath '%CD%\%BIN_DIR%\JJFlexRadio.exe' -Destination '%NAS_HIST_DIR%\JJFlexRadio.exe' -Force"
    powershell -NoProfile -Command "Copy-Item -LiteralPath '%CD%\%BIN_DIR%\JJFlexRadio.pdb' -Destination '%NAS_HIST_DIR%\JJFlexRadio.pdb' -Force"
    echo   JJFlex_%APPVER%_x64_debug_%STAMP%.zip
    echo   NOTES-%APPVER%-debug_%STAMP%.txt
    echo   JJFlexRadio.exe + .pdb ^(refreshed^)
)

REM ---------------------------------------------------------------------------
REM Dropbox publish (only with --publish)
REM ---------------------------------------------------------------------------
if "%PUBLISH%"=="1" (
    echo.
    echo Dropbox publish: %DROPBOX_DEBUG%

    if not exist "%DROPBOX_DEBUG%" (
        powershell -NoProfile -Command "New-Item -Path '%DROPBOX_DEBUG%' -ItemType Directory -Force | Out-Null"
    )

    REM Purge prior debug files — "Dropbox = latest only" invariant
    powershell -NoProfile -Command "Get-ChildItem -Path '%DROPBOX_DEBUG%' -Filter 'JJFlex_*_debug*.zip' -ErrorAction SilentlyContinue | Remove-Item -Force"
    powershell -NoProfile -Command "Get-ChildItem -Path '%DROPBOX_DEBUG%' -Filter 'NOTES-*-debug*.txt' -ErrorAction SilentlyContinue | Remove-Item -Force"

    powershell -NoProfile -Command "Copy-Item -LiteralPath '%ZIP_PATH%' -Destination '%DROPBOX_DEBUG%\%ZIP_NAME%' -Force"
    powershell -NoProfile -Command "Copy-Item -LiteralPath '%NOTES_PATH%' -Destination '%DROPBOX_DEBUG%\%NOTES_NAME%' -Force"

    echo   Purged prior debug zip^(s^) and NOTES.
    echo   %ZIP_NAME%
    echo   %NOTES_NAME%
) else (
    echo.
    echo Dropbox: NOT published ^(use --publish to broadcast to testers^).
)

REM ---------------------------------------------------------------------------
REM Summary
REM ---------------------------------------------------------------------------
echo.
echo ============================================
echo Done. Version %APPVER%  (Debug x64^)
echo Zip at %ZIP_PATH%
echo Notes at %NOTES_PATH%
echo ============================================

endlocal
