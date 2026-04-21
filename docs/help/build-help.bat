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

REM Convert Markdown to HTML
REM Option 1: Use pandoc if available
where pandoc >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo Using pandoc for Markdown conversion...
    for %%f in ("%MDDIR%\*.md") do (
        echo   Converting %%~nf.md
        pandoc "%%f" -f markdown -t html --standalone --css=../style.css -o "%PAGESDIR%\%%~nf.htm" --metadata title="%%~nf"
    )
) else (
    echo Pandoc not found. Using PowerShell Markdown converter...
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
