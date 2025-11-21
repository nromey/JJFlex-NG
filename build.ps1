# Run this in "Developer PowerShell for VS 2022" or Developer PowerShell
msbuild -t:restore JJFlexRadio.sln
msbuild JJFlexRadio.sln /p:Configuration=Release /p:Platform=x86 /m /nologo /clp:Summary /fileLoggerParameters:LogFile=msbuild.log
if (Test-Path msbuild.log) { Get-Content msbuild.log -Tail 200 }
Read-Host "Press Enter to exit"
