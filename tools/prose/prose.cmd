@echo off
rem  prose — edit the words the application says as writing, not as code.
rem
rem    tools\prose\prose extract    pull the words out into the editing file
rem    tools\prose\prose read       write a listen-through file: sentences only
rem    tools\prose\prose check      say what applying would do; write nothing
rem    tools\prose\prose apply      put the edited words back into the code
rem    tools\prose\prose skipped    list what was left behind, and why
rem
rem  The first run builds the tool, which takes a moment. Every run after that
rem  is immediate.
setlocal
dotnet run --project "%~dp0Prose.csproj" -c Release --verbosity quiet -- %*
exit /b %ERRORLEVEL%
