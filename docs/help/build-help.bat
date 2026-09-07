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
    for %%f in ("%MDDIR%\*.md") do (
        echo   Converting %%~nf.md
        pandoc "%%f" -f markdown -t html --standalone --css=../style.css -o "%PAGESDIR%\%%~nf.htm" --metadata title="%%~nf"
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
REM hhc.exe returns 1 on success and 0 on failure (yes, really), so its exit
REM code is useless and this script has always checked for the file instead.
REM That check had a hole: a CHM left over from a PREVIOUS build satisfies
REM `if exist` perfectly, so a compile that failed outright still printed
REM SUCCESS and left the stale file in place. Now that install.bat refuses to
REM package a stale CHM (#556/#543), a false SUCCESS here would put a fresh
REM stamp on stale help, which is worse than no check at all.
REM
REM So the previous CHM is moved aside before hhc runs. Afterwards, "the file
REM is there" can only mean "hhc just wrote it". On failure the old one is put
REM back, because destroying working help to prove a point helps nobody.
REM ---------------------------------------------------------------------------
if exist "%HELPDIR%JJFlexRadio.chm.prev" del /q "%HELPDIR%JJFlexRadio.chm.prev"
if exist "%HELPDIR%JJFlexRadio.chm" move /y "%HELPDIR%JJFlexRadio.chm" "%HELPDIR%JJFlexRadio.chm.prev" >nul
if exist "%HELPDIR%JJFlexRadio.chm.stamp" del /q "%HELPDIR%JJFlexRadio.chm.stamp"

echo.
echo Compiling CHM...
"!HHC!" "%HELPDIR%jjflex-help.hhp"

if not exist "%HELPDIR%JJFlexRadio.chm" (
    echo.
    echo FAILED: CHM compilation failed - hhc.exe produced no output file.
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
