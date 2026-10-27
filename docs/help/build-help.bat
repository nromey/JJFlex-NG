@echo off
REM Build the JJ Flexible help: the compiled help (JJFlexRadio.chm) and the
REM HTML help (docs\help\html\), both from docs\help\outline.md and the pages
REM under docs\help\md\.
REM
REM A thin wrapper. The work, and the explanation of every check, is in
REM build-help.ps1 beside this file. It stays a .bat so build-debug.bat,
REM build-installers.bat and anyone used to typing it keep calling the same
REM name, and its exit code is the script's: 0 built and stamped, 1 failed.
REM
REM /fallback used to select a regex converter when pandoc was missing. It is
REM retired, because that converter cannot build a help with folders, links
REM and generated contents; the script says so and stops if it is given.

setlocal
set "HELP_ARGS="
if /I "%~1"=="/fallback" set "HELP_ARGS=-Fallback"
if /I "%~1"=="/nochm" set "HELP_ARGS=-NoChm"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-help.ps1" %HELP_ARGS%
exit /b %ERRORLEVEL%
