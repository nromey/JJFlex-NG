@echo off
REM Robust install.bat
REM Usage: install.bat <solution_or_project_dir> <Configuration> <PackageName>
REM Creates architecture-specific installers with _x64 or _x86 suffix

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

REM Detect architecture from output folder - prefer x64, fallback to x86
set "ARCH=x64"
set "OUTDIR=%~1\bin\x64\%cfg%\net8.0-windows\win-x64"
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\x64\%cfg%\net8.0-windows"
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\%cfg%\net8.0-windows\win-x64"
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\%cfg%\net8.0-windows"

REM Check if we found x64, otherwise try x86
if not exist "%OUTDIR%\*" (
    set "ARCH=x86"
    set "OUTDIR=%~1\bin\x86\%cfg%\net8.0-windows\win-x86"
)
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\x86\%cfg%\net8.0-windows"
if not exist "%OUTDIR%\*" set "OUTDIR=%~1\bin\%cfg%"

echo Using output folder: "%OUTDIR%"
echo Detected architecture: %ARCH%

if not exist "%OUTDIR%\*" (
    echo ERROR: Expected output folder "%OUTDIR%" not found. Please build the solution first.
    exit /b 6
)

REM Determine Program Files path based on architecture
if "%ARCH%"=="x64" (
    set "PROGFILES=$PROGRAMFILES64"
) else (
    set "PROGFILES=$PROGRAMFILES"
)

REM Always package the JJFlexRadio.exe we just built. Infer version for naming installers.
set "PGM_EXE=%OUTDIR%\JJFlexRadio.exe"
if not exist "%PGM_EXE%" (
    echo ERROR: Expected exe "%PGM_EXE%" not found.
    exit /b 6
)
set "APPVER=0.0.0.0"
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Item '%PGM_EXE%').VersionInfo.ProductVersion"`) do set "APPVER=%%v"
echo Program (final): %pgm%   Version: %APPVER%   Architecture: %ARCH%
set "VIAPPVER=%APPVER%.0.0.0"
for /f "tokens=1-4 delims=." %%a in ("%VIAPPVER%") do set "VIAPPVER=%%a.%%b.%%c.%%d"

REM Generate install.nsi with the resolved program name and architecture-specific Program Files
REM Always use PowerShell for reliable path handling (avoids sed backslash issues)
powershell -NoProfile -Command "$c = Get-Content 'install template.nsi' -Raw; $c = $c.Replace('MYPGM','%pgm%').Replace('MYVER','%VIAPPVER%').Replace('MYOUTDIR','%OUTDIR%').Replace('MYPROGFILES','%PROGFILES%'); Set-Content -Encoding ASCII 'install.nsi' $c" || (echo PowerShell replace failed & exit /b 5)

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

REM Rename installer with architecture suffix
set "VERSIONED_SETUP=%~1\Setup %pgm%_%VIAPPVER%.exe"
set "ARCH_SETUP=%~1\Setup %pgm%_%APPVER%_%ARCH%.exe"
set "LEGACY_SETUP=%~1\Setup %pgm%_%ARCH%.exe"

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
