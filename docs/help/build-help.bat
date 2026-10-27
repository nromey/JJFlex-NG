@echo off
REM Build JJFlex help file
REM Converts Markdown to HTML, then compiles CHM

setlocal enabledelayedexpansion

set "HELPDIR=%~dp0"
set "MDDIR=%HELPDIR%md"
set "PAGESDIR=%HELPDIR%pages"
REM Capture ProgramFiles(x86) via its alternate name first — raw expansion
REM of %ProgramFiles(x86)% inside an if-block body causes the ')' in the
REM expanded path 'C:\Program Files (x86)\...' to close the if-block
REM prematurely. Delayed expansion via !HHC! sidesteps the parse-time issue.
set "PFX86_DIR=%ProgramFiles(x86)%"
set "HHC=%PFX86_DIR%\HTML Help Workshop\hhc.exe"

REM Check for hhc.exe
if not exist "!HHC!" (
    echo ERROR: HTML Help Workshop not found at !HHC!
    echo Install from: https://web.archive.org/web/2024/https://www.microsoft.com/en-us/download/details.aspx?id=21138
    exit /b 1
)

REM Create pages directory
if not exist "%PAGESDIR%" mkdir "%PAGESDIR%"

REM Sprint 28 Phase 8c — import the project changelog as a "What's New" help
REM topic. Single source of truth: docs\CHANGELOG.md is the canonical changelog,
REM which gets copied to the help md/ folder at build time. The copy gets
REM converted like any other md file. No separate changelog maintenance.
set "CHANGELOG_SRC=%HELPDIR%..\CHANGELOG.md"
set "CHANGELOG_DST=%MDDIR%\whats-new.md"
if exist "%CHANGELOG_SRC%" (
    echo Importing CHANGELOG.md as whats-new.md for CHM inclusion...
    copy /Y "%CHANGELOG_SRC%" "%CHANGELOG_DST%" >nul
)

REM ---------------------------------------------------------------------------
REM Convert Markdown to HTML.
REM
REM Pandoc is the intended converter and has been since this script was written.
REM It silently fell back to convert-md.ps1 for months on any machine without it
REM -- one line in a sixty-file scroll -- and nobody noticed, so every shipped
REM help page was built by the fallback.
REM
REM That matters because the fallback is a line-by-line regex converter: a bold
REM phrase spanning two source lines never matches, and renders as literal
REM asterisks that a screen reader reads aloud. Same for italics, links and code
REM spans. It also emits one paragraph per source line.
REM
REM So a missing pandoc is now an ERROR, exactly like a missing HTML Help
REM Workshop above. Shipping quietly worse help is not an acceptable default.
REM Pass /fallback to use the regex converter deliberately.
REM ---------------------------------------------------------------------------
set "ALLOW_FALLBACK="
if /I "%~1"=="/fallback" set "ALLOW_FALLBACK=1"

where pandoc >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo Using pandoc for Markdown conversion...
    REM A page pandoc fails on leaves LAST build's .htm in pages\, and hhc
    REM compiles that without complaint, so a conversion error must stop the
    REM build here or the help ships a stale page under a fresh stamp (#830).
    set "PANDOC_FAILED="
    for %%f in ("%MDDIR%\*.md") do (
        echo   Converting %%~nf.md
        pandoc "%%f" -f markdown -t html --standalone --css=../style.css -o "%PAGESDIR%\%%~nf.htm" --metadata title="%%~nf"
        if errorlevel 1 (
            echo   FAILED: pandoc could not convert %%~nf.md
            set "PANDOC_FAILED=1"
        )
    )
    if defined PANDOC_FAILED (
        echo.
        echo FAILED: one or more help pages did not convert. See the lines above.
        exit /b 1
    )
) else (
    if not defined ALLOW_FALLBACK (
        echo.
        echo ERROR: pandoc is not installed, and it is the intended Markdown converter.
        echo.
        echo   Install it with:  winget install --id JohnMacFarlane.Pandoc
        echo.
        echo The PowerShell fallback converter produces WORSE help pages -- bold text
        echo spanning two source lines renders as literal asterisks, which a screen
        echo reader reads out. Building help without pandoc would ship that.
        echo.
        echo If you genuinely need the fallback, run:  build-help.bat /fallback
        echo.
        exit /b 1
    )
    echo.
    echo WARNING: pandoc not found and /fallback was given.
    echo WARNING: using the regex converter -- bold across line breaks will render
    echo WARNING: as literal asterisks. Do not ship these pages.
    echo.
    powershell -ExecutionPolicy Bypass -File "%HELPDIR%convert-md.ps1" "%MDDIR%" "%PAGESDIR%"
)

REM ---------------------------------------------------------------------------
REM Compile CHM.
REM
REM The previous CHM is moved aside before hhc runs, so that afterwards "the
REM file is there" can only mean "hhc just wrote it" and not "a stale file was
REM left over". On failure the old one is put back, because destroying working
REM help to prove a point helps nobody.
REM
REM But "hhc wrote a file" is NOT "hhc succeeded" (#830). Measured 2026-10-26:
REM a page missing from [FILES] (HHC5003) and a contents entry pointing at a
REM missing page (HHC5013) both print an error AND write a complete CHM. The
REM file test alone passed both, and the stamp then certified errored help as
REM current. So hhc's output is captured and the build fails on either sign:
REM   - an "HHCnnnn: Error" line in its output;
REM   - exit code 0. The code is inverted, not useless as this comment used to
REM     say: 1 is a clean compile, 0 means an error was printed.
REM hhc checks no links, anchors or index targets at all; a dead index entry
REM prints nothing and exits 1. Those checks belong to the build, not to hhc.
REM ---------------------------------------------------------------------------
if exist "%HELPDIR%JJFlexRadio.chm.prev" del /q "%HELPDIR%JJFlexRadio.chm.prev"
if exist "%HELPDIR%JJFlexRadio.chm" move /y "%HELPDIR%JJFlexRadio.chm" "%HELPDIR%JJFlexRadio.chm.prev" >nul
if exist "%HELPDIR%JJFlexRadio.chm.stamp" del /q "%HELPDIR%JJFlexRadio.chm.stamp"

echo.
echo Compiling CHM...
set "HHC_LOG=%TEMP%\jjflex-hhc-%RANDOM%%RANDOM%.log"
"!HHC!" "%HELPDIR%jjflex-help.hhp" > "!HHC_LOG!" 2>&1
set "HHC_EXIT=!ERRORLEVEL!"
type "!HHC_LOG!"
set "HHC_FAILED="
set "HHC_WHY="
findstr /R /C:"HHC[0-9][0-9]*: *Error" "!HHC_LOG!" >nul 2>&1 && (
    set "HHC_FAILED=1"
    set "HHC_WHY=hhc.exe printed an error - see the HHC lines above."
)
if "!HHC_EXIT!"=="0" if not defined HHC_FAILED (
    set "HHC_FAILED=1"
    set "HHC_WHY=hhc.exe exited 0, which it does only after an error."
)
if not exist "%HELPDIR%JJFlexRadio.chm" if not defined HHC_FAILED (
    set "HHC_FAILED=1"
    set "HHC_WHY=hhc.exe produced no output file."
)
del /q "!HHC_LOG!" >nul 2>&1

if defined HHC_FAILED (
    echo.
    echo FAILED: CHM compilation failed - !HHC_WHY!
    REM An errored compile still writes a CHM. Remove it so it cannot ship.
    if exist "%HELPDIR%JJFlexRadio.chm" del /q "%HELPDIR%JJFlexRadio.chm"
    if exist "%HELPDIR%JJFlexRadio.chm.prev" (
        move /y "%HELPDIR%JJFlexRadio.chm.prev" "%HELPDIR%JJFlexRadio.chm" >nul
        echo         The previous CHM has been restored. It is now STALE and
        echo         unstamped, so packaging will refuse it until this succeeds.
    )
    exit /b 1
)

if exist "%HELPDIR%JJFlexRadio.chm.prev" del /q "%HELPDIR%JJFlexRadio.chm.prev"

echo.
echo SUCCESS: JJFlexRadio.chm built successfully.
dir "%HELPDIR%JJFlexRadio.chm"

REM Stamp it with a hash of the sources it was built from, so install.bat and
REM build-debug.bat can tell later whether it is still current. See the header
REM of help-stamp.ps1 for why a stamp, and not an mtime or a hash of the CHM.
if not exist "%HELPDIR%help-stamp.ps1" (
    echo.
    echo ERROR: docs\help\help-stamp.ps1 is missing. The CHM was built but
    echo        cannot be stamped, and an unstamped CHM will not package.
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%HELPDIR%help-stamp.ps1" -Mode Write -HelpDir "%HELPDIR%." || (
    echo.
    echo ERROR: the CHM was built but the freshness stamp could not be written.
    exit /b 1
)
