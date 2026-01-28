@echo off
REM Robust install.bat
REM Usage: install.bat <solution_or_project_dir> <Configuration> <PackageName>

REM capture inputs and provide sane defaults if missing
set "cfg=%~2"
set "pgm=%~3"
if "%cfg%"=="" set "cfg=Release"
if "%pgm%"=="" set "pgm=JJFlexRadio"

REM Remove unneeded symbol/doc files before packaging to keep installer small
del /q "%~1\bin\release\*.pdb" >nul 2>&1
del /q "%~1\bin\release\*.xml" >nul 2>&1

echo Params: configuration="%~2" package="%~3"
setlocal enabledelayedexpansion

REM Program name (strip surrounding quotes)
echo Program: %pgm%

REM change to the folder passed as first arg
cd /d "%~1" || (echo Failed to change directory to "%~1" & exit /b 2)

REM Attempt to find sed (GnuWin32 or on PATH)
set "SEDPATH="
if exist "C:\Program Files (x86)\GnuWin32\bin\sed.exe" set "SEDPATH=C:\Program Files (x86)\GnuWin32\bin\sed.exe"
if not defined SEDPATH (
    for %%S in (sed.exe sed) do if exist "%%~$PATH:~0,0%%S" set "SEDPATH=%%S"
)
if not defined SEDPATH (
    where sed >nul 2>nul && for /f "usebackq delims=" %%s in (`where sed`) do set "SEDPATH=%%s"
)

REM If sed still not found we'll use PowerShell for simple replacement steps
if defined SEDPATH (
    echo sed found: %SEDPATH%
) else (
    echo sed not found; will use PowerShell text replace as fallback
)

if not exist "install template.nsi" (
    echo ERROR: install template 'install template.nsi' not found in %CD%.
    exit /b 4
)

echo Generating install.nsi by replacing MYPGM...
REM If program name missing, try to infer from newest JJFlexRadio*.exe in OUTDIR after we find OUTDIR below.

REM Prefer platform/TFM-specific output when present (CFG|x86|net48|win-x86), otherwise fall back.
set "OUTDIR=%~1\bin\x86\%cfg%\net48\win-x86"
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\x86\%cfg%\net48"
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\%cfg%\net48"
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\x86\%cfg%"
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\%cfg%"
echo Using output folder: "%OUTDIR%"
if not exist "%OUTDIR%\*" (
    echo ERROR: Expected output folder "%OUTDIR%" not found. Please build the solution first.
    exit /b 6
)

REM Always package the JJFlexRadio.exe we just built. Infer version for naming installers.
set "PGM_EXE=%OUTDIR%\JJFlexRadio.exe"
if not exist "%PGM_EXE%" (
    echo ERROR: Expected exe "%PGM_EXE%" not found.
    exit /b 6
)
set "APPVER=0.0.0.0"
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Item '%PGM_EXE%').VersionInfo.ProductVersion"`) do set "APPVER=%%v"
echo Program (final): %pgm%   Version: %APPVER%
set "VIAPPVER=%APPVER%.0.0.0"
for /f "tokens=1-4 delims=." %%a in ("%VIAPPVER%") do set "VIAPPVER=%%a.%%b.%%c.%%d"

REM Generate install.nsi with the resolved program name.
if defined SEDPATH (
    type "install template.nsi" | "%SEDPATH%" -e "s/MYPGM/%pgm%/g" -e "s/MYVER/%VIAPPVER%/g" -e "s#MYOUTDIR#%OUTDIR%#g" > install.nsi || (echo sed failed & exit /b 5)
) else (
    powershell -NoProfile -Command "$c = Get-Content 'install template.nsi' -Raw; $c = $c.Replace('MYPGM','%pgm%').Replace('MYVER','%VIAPPVER%').Replace('MYOUTDIR','%OUTDIR%'); Set-Content -Encoding UTF8 'install.nsi' $c" || (echo PowerShell replace failed & exit /b 5)
)

REM produce a temporary file list and run sed against it
pushd "!OUTDIR!" || (echo Cannot cd to output folder & exit /b 7)
dir /b > "%TEMP%\jjflex_outputs.txt"
popd

echo Creating deleteList.txt using src.sed...
if defined SEDPATH (
    "%SEDPATH%" -f "%~dp0src.sed" "%TEMP%\jjflex_outputs.txt" > "%~dp0deleteList.txt" || (echo sed processing failed & exit /b 8)
) else (
    powershell -NoProfile -Command "Get-Content '%TEMP%\jjflex_outputs.txt' | ForEach-Object { 'Delete \"`$INSTDIR\\' + $_ + '\"' } | Set-Content -Encoding UTF8 '%~dp0deleteList.txt'" || (echo fallback delete list creation failed & exit /b 8)
)

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

set "LEGACY_SETUP=%~1\Setup %pgm%.exe"
set "VERSIONED_SETUP=%~1\Setup %pgm%_%APPVER%.exe"
if exist "%VERSIONED_SETUP%" (
    echo Versioned installer created: "%VERSIONED_SETUP%"
    echo Updating legacy installer "%LEGACY_SETUP%"...
    copy /y "%VERSIONED_SETUP%" "%LEGACY_SETUP%" >nul
) else (
    echo WARNING: expected versioned installer "%VERSIONED_SETUP%" not found.
)

REM Remove legacy unversioned Setup .exe if it exists.
if exist "%~1\Setup .exe" del /q "%~1\Setup .exe"

exit /b %ERRORLEVEL%

REM Also produce a versioned installer name (if versioned EXE exists) and normalize the app EXE name.
for %%E in ("%OUTDIR%\JJFlexRadio 4.0.3.exe") do (
    if exist "%%~fE" (
        echo Normalizing app EXE name...
        copy /y "%%~fE" "%OUTDIR%\JJFlexRadio.exe" >nul
        echo Creating versioned installer Setup_4.0.3.exe...
        if exist "%~1\Setup .exe" copy /y "%~1\Setup .exe" "%~1\Setup_4.0.3.exe" >nul
    )
)
