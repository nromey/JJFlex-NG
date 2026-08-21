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

REM Compile CHM
echo.
echo Compiling CHM...
"!HHC!" "%HELPDIR%jjflex-help.hhp"

REM hhc.exe returns 1 on success, 0 on failure (yes, really)
if exist "%HELPDIR%JJFlexRadio.chm" (
    echo.
    echo SUCCESS: JJFlexRadio.chm built successfully.
    dir "%HELPDIR%JJFlexRadio.chm"
) else (
    echo.
    echo FAILED: CHM compilation failed.
    exit /b 1
)
