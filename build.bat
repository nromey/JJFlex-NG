@echo off
REM Run from a Visual Studio Developer Command Prompt (or ensure msbuild is on PATH)
msbuild -t:restore JJFlexRadio.sln
msbuild JJFlexRadio.sln /p:Configuration=Release /p:Platform=x86 /m /nologo /clp:Summary /fileLoggerParameters:LogFile=msbuild.log
if exist msbuild.log type msbuild.log
pause
