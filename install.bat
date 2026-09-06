@echo off
REM Robust install.bat
REM Usage: install.bat <solution_or_project_dir> <Configuration> <TargetName> [x64|x86]
REM Creates architecture-specific installers with _x64 or _x86 suffix
REM Optional 4th arg forces architecture (skips auto-detection)
REM
REM EXIT CODE 11 — the compiled help (JJFlexRadio.chm) is missing or stale, so
REM   no installer was built. See the HELP GUARD block below; the fix is
REM   always `docs\help\build-help.bat`, then rebuild, then package.
REM
REM NAMES — two of them, kept deliberately separate:
REM   exe  (3rd arg, = MSBuild $(TargetName))  the built executable, "jjflexible".
REM        This is the signed file identity and what shortcuts point at.
REM   PKG  (constant below)                    the package identity, "JJFlexRadio".
REM        Drives the install directory, the HKLM registry keys, and the shortcut
REM        names. It must NOT follow the exe rename: keeping it fixed is what
REM        makes 4.2.x land on top of an existing 4.x install (same folder, same
REM        uninstall entry) instead of installing a second copy beside it.

REM capture inputs and provide sane defaults if missing
set "cfg=%~2"
set "exe=%~3"
set "FORCE_ARCH=%~4"
if "%cfg%"=="" set "cfg=Release"
if "%exe%"=="" set "exe=jjflexible"
set "PKG=JJFlexRadio"

REM NOTE: an old sweep here deleted *.pdb / *.xml from "bin\release\" — a path
REM dead since the .NET 10 migration, so XML doc files quietly shipped for
REM months. Exclusion now happens where packaging happens: the NSIS template's
REM File /r line excludes *.pdb and *.xml. Nothing is deleted from the build
REM tree — the NAS publish step archives the exe+pdb from it afterwards.

echo Params: configuration="%~2" package="%~3"
setlocal enabledelayedexpansion

REM Program name (strip surrounding quotes)
echo Package: %PKG%   Executable: %exe%.exe

REM change to the folder passed as first arg
cd /d "%~1" || (echo Failed to change directory to "%~1" & exit /b 2)

REM Sprint 29 Track J: sed lookup removed. install.nsi generation and
REM deleteList.txt generation both use PowerShell now (PowerShell handles paths
REM with backslashes more reliably than sed, and the deleteList step needs
REM recursive directory walking that sed can't do). The legacy src.sed file
REM is left in the tree for git history and may be removed in a follow-up.

if not exist "install template.nsi" (
    echo ERROR: install template 'install template.nsi' not found in %CD%.
    exit /b 4
)

echo Generating install.nsi by replacing MYPGM...

REM Detect architecture — use forced arch if provided, otherwise auto-detect
if /i "%FORCE_ARCH%"=="x86" goto detect_x86
if /i "%FORCE_ARCH%"=="x64" goto detect_x64

:detect_x64
set "ARCH=x64"
set "OUTDIR=%~1\bin\x64\%cfg%\net10.0-windows\win-x64"
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\x64\%cfg%\net10.0-windows"
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\%cfg%\net10.0-windows\win-x64"
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\%cfg%\net10.0-windows"
if exist "%OUTDIR%\*" goto detect_done

REM If forced to x64 but not found, fail
if /i "%FORCE_ARCH%"=="x64" goto detect_done

:detect_x86
set "ARCH=x86"
set "OUTDIR=%~1\bin\x86\%cfg%\net10.0-windows\win-x86"
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\x86\%cfg%\net10.0-windows"
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\%cfg%"

:detect_done

echo Using output folder: "%OUTDIR%"
echo Detected architecture: %ARCH%

if not exist "%OUTDIR%\*" (
    echo ERROR: Expected output folder "%OUTDIR%" not found. Please build the solution first.
    exit /b 6
)

REM ---------------------------------------------------------------------------
REM HELP GUARD (#556, and #543 is why it is worth an exit code).
REM
REM The CHM is the in-app help and it reaches the installer the same way every
REM other file does: NSIS `File /r` over the publish output. Until Sprint 46 a
REM committed copy of it was always sitting in docs\help\, so it was always
REM there to be copied. It is a build artifact and is no longer committed, so
REM "it exists" and "this build produced it" have become different statements.
REM
REM Nothing downstream would notice the difference. JJFlexRadio.vbproj copies
REM it under Condition="Exists(...)", NSIS `File /r` cannot miss what was never
REM there, and generate-deletelist.ps1 walks the same output - so an installer
REM with no help in it builds, packages, installs and runs, and the only
REM symptom is a Help menu that opens nothing on a blind operator's machine.
REM
REM The same check also catches a CHM that exists but is STALE, which is #543:
REM released builds carried help 21 pages behind from 2026-08-30 to 2026-09-05
REM and nobody noticed for six days, because nothing complained.
REM
REM Called with the `||` form deliberately: `if errorlevel 1` is a
REM greater-than-or-equal test and would sail straight past the negative exit
REM code that `powershell -File` returns for a bad path. The existence check
REM above it closes that case outright.
REM ---------------------------------------------------------------------------
if not exist "%~dp0docs\help\help-stamp.ps1" (
    echo ERROR: %~dp0docs\help\help-stamp.ps1 is missing.
    echo   It is the only thing that can tell whether the compiled help is
    echo   current, and packaging without that answer is how stale help ships.
    exit /b 11
)
echo Checking compiled help is present and current...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0docs\help\help-stamp.ps1" -Mode Check -HelpDir "%~dp0docs\help" -PackagedChm "!OUTDIR!\JJFlexRadio.chm" || (
    echo.
    echo REFUSING TO PACKAGE: the compiled help did not pass the check above.
    echo   No installer was built. The in-app help is not optional.
    exit /b 11
)

REM Determine Program Files path based on architecture
if "%ARCH%"=="x64" (
    set "PROGFILES=$PROGRAMFILES64"
) else (
    set "PROGFILES=$PROGRAMFILES"
)

REM Always package the executable we just built. Infer version for naming installers.
set "PGM_EXE=%OUTDIR%\%exe%.exe"
if not exist "%PGM_EXE%" (
    echo ERROR: Expected exe "%PGM_EXE%" not found.
    exit /b 6
)
REM Read FileVersion (always a clean 4-part number). ProductVersion may carry a
REM +commit-hash suffix from SDK source-link metadata, which would pollute the
REM installer filename.
set "APPVER=0.0.0.0"
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Item '%PGM_EXE%').VersionInfo.FileVersion"`) do set "APPVER=%%v"
echo Program (final): %PKG%   Executable: %exe%.exe   Version: %APPVER%   Architecture: %ARCH%
set "VIAPPVER=%APPVER%.0.0.0"
for /f "tokens=1-4 delims=." %%a in ("%VIAPPVER%") do set "VIAPPVER=%%a.%%b.%%c.%%d"

REM Generate install.nsi with the resolved names and architecture-specific Program Files
REM Always use PowerShell for reliable path handling (avoids sed backslash issues)
powershell -NoProfile -Command "$c = Get-Content 'install template.nsi' -Raw; $c = $c.Replace('MYPGM','%PKG%').Replace('MYEXE','%exe%').Replace('MYVER','%VIAPPVER%').Replace('MYOUTDIR','%OUTDIR%').Replace('MYPROGFILES','%PROGFILES%'); Set-Content -Encoding ASCII 'install.nsi' $c" || (echo PowerShell replace failed & exit /b 5)

REM Sprint 29 Track J: build the deleteList.txt via generate-deletelist.ps1.
REM Self-contained .NET 10 brings 13 satellite-resource subdirs (cs/, de/, es/,
REM ...) plus runtimes/, help/, Resources/. The legacy approach (top-level
REM `dir /b` + sed wrap into Delete lines) left every subdirectory dangling at
REM uninstall time because NSIS Delete silently no-ops on directories. The PS1
REM helper enumerates files recursively (one Delete each) and subdirectories
REM (one RMDir /r each), and writes ASCII-without-BOM (NSIS !include chokes on
REM the BOM bytes the legacy fallback emitted).
echo Creating deleteList.txt by recursively enumerating output...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0generate-deletelist.ps1" -OutputDir "!OUTDIR!" -OutFile "%~dp0deleteList.txt" || (echo deleteList.txt generation failed & exit /b 8)

echo Finding makensis...
set "MAKENSIS="
if exist "%ProgramFiles(x86)%\NSIS\Bin\makensis.exe" set "MAKENSIS=%ProgramFiles(x86)%\NSIS\Bin\makensis.exe"
if not defined MAKENSIS if exist "%ProgramFiles%\NSIS\Bin\makensis.exe" set "MAKENSIS=%ProgramFiles%\NSIS\Bin\makensis.exe"
if not defined MAKENSIS if exist "NSIS\makensis.exe" set "MAKENSIS=NSIS\makensis.exe"
if not defined MAKENSIS if exist "JJRadio\NSIS\makensis.exe" set "MAKENSIS=JJRadio\NSIS\makensis.exe"
if not defined MAKENSIS (
    where makensis >nul 2>nul && for /f "usebackq delims=" %%m in (`where makensis`) do set "MAKENSIS=%%m"
)
if not defined MAKENSIS (
    echo ERROR: makensis not found. Install NSIS and ensure makensis is on PATH or in NSIS\makensis.exe
    exit /b 9
)
echo makensis found: %MAKENSIS%

echo Running makensis to create installer...
"%MAKENSIS%" install.nsi
echo makensis exit code: %ERRORLEVEL%

REM Rename installer with architecture suffix (short name: JJFlex)
set "VERSIONED_SETUP=%~1\Setup %PKG%_%VIAPPVER%.exe"
set "ARCH_SETUP=%~1\Setup JJFlex_%APPVER%_%ARCH%.exe"
set "LEGACY_SETUP=%~1\Setup JJFlex_%ARCH%.exe"

if exist "%VERSIONED_SETUP%" (
    echo Renaming installer with architecture suffix...
    move /y "%VERSIONED_SETUP%" "%ARCH_SETUP%" >nul
    echo Created: "%ARCH_SETUP%"
    echo Updating legacy installer "%LEGACY_SETUP%"...
    copy /y "%ARCH_SETUP%" "%LEGACY_SETUP%" >nul
) else (
    echo WARNING: expected versioned installer "%VERSIONED_SETUP%" not found.
)

REM Remove legacy unversioned Setup .exe if it exists.
if exist "%~1\Setup .exe" del /q "%~1\Setup .exe"

exit /b %ERRORLEVEL%
