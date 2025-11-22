@echo off
REM Robust install.bat
REM Usage: install.bat <solution_or_project_dir> <Configuration> <PackageName>

if "%~2"=="Debug" exit /b 0

echo Params: configuration="%~2" package="%~3"
setlocal enabledelayedexpansion

REM Program name (strip surrounding quotes)
set "pgm=%~3"
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
if defined SEDPATH (
    type "install template.nsi" | "%SEDPATH%" "s/MYPGM/%pgm%/g" > install.nsi || (echo sed failed & exit /b 5)
) else (
    powershell -NoProfile -Command "(Get-Content 'install template.nsi') -replace 'MYPGM', [regex]::Escape('%pgm%') | Set-Content -Encoding UTF8 'install.nsi'" || (echo PowerShell replace failed & exit /b 5)
)

echo Using top-level output folder: "%~1\bin\Release"
set "OUTDIR=%~1\bin\Release"
if not exist "%OUTDIR%\*" (
	echo ERROR: Expected output folder "%OUTDIR%" not found. Please build the solution first.
	exit /b 6
)

REM produce a temporary file list and run sed against it
pushd "!OUTDIR!" || (echo Cannot cd to output folder & exit /b 7)
dir /b > "%TEMP%\jjflex_outputs.txt"
popd

echo Creating deleteList.txt using src.sed...
if defined SEDPATH (
    "%SEDPATH%" -f "%~dp0src.sed" "%TEMP%\jjflex_outputs.txt" > "%~dp0deleteList.txt" || (echo sed processing failed & exit /b 8)
) else (
    powershell -NoProfile -Command "Get-Content '%TEMP%\jjflex_outputs.txt' | ForEach-Object { $_ } | Set-Content -NoNewline -Encoding UTF8 '%~dp0deleteList.txt'" || (echo fallback delete list creation failed & exit /b 8)
)

echo Finding makensis...
set "MAKENSIS="
if exist "NSIS\makensis.exe" set "MAKENSIS=NSIS\makensis.exe"
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
exit /b %ERRORLEVEL%
@echo off
REM Robust install.bat
REM Usage: install.bat <solution_or_project_dir> <Configuration> <PackageName>

if "%~2"=="Debug" exit /b 0

echo Params: configuration="%~2" package="%~3"
setlocal enabledelayedexpansion

REM Program name (strip surrounding quotes)
set "pgm=%~3"
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
else
	echo sed not found; will use PowerShell text replace as fallback
fi

if not exist "install template.nsi" (
	echo ERROR: install template 'install template.nsi' not found in %CD%.
	exit /b 4
)

echo Generating install.nsi by replacing MYPGM...
if defined SEDPATH (
	type "install template.nsi" | "%SEDPATH%" "s/MYPGM/%pgm%/g" > install.nsi || (echo sed failed & exit /b 5)
else
	powershell -NoProfile -Command "(Get-Content 'install template.nsi') -replace 'MYPGM', [regex]::Escape('%pgm%') | Set-Content -Encoding UTF8 'install.nsi'" || (echo PowerShell replace failed & exit /b 5)
fi

echo Searching for build output folder (bin\Release... )...
set "OUTDIR="
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "Get-ChildItem -Path . -Directory -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\bin\\Release' } | Select-Object -First 1 -ExpandProperty FullName"`) do set "OUTDIR=%%D"

if not defined OUTDIR (
	echo No bin\Release folder found under %CD%. Trying alternative search...
	for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "Get-ChildItem -Path . -Directory -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'Release' -and $_.FullName -match '\\bin\\' } | Select-Object -First 1 -ExpandProperty FullName"`) do set "OUTDIR=%%D"
)

if not defined OUTDIR (
	echo ERROR: Could not locate a build output folder (bin\Release...). Please build first.
	exit /b 6
)

echo Using output folder: "!OUTDIR!"

REM produce a temporary file list and run sed against it
pushd "!OUTDIR!" || (echo Cannot cd to output folder & exit /b 7)
dir /b > "%TEMP%\jjflex_outputs.txt"
popd

echo Creating deleteList.txt using src.sed...
if defined SEDPATH (
	"%SEDPATH%" -f "%~dp0src.sed" "%TEMP%\jjflex_outputs.txt" > "%~dp0deleteList.txt" || (echo sed processing failed & exit /b 8)
else
	powershell -NoProfile -Command "Get-Content '%TEMP%\jjflex_outputs.txt' | ForEach-Object { $_ } | Set-Content -NoNewline -Encoding UTF8 '%~dp0deleteList.txt'" || (echo fallback delete list creation failed & exit /b 8)
fi

echo Finding makensis...
set "MAKENSIS="
if exist "NSIS\makensis.exe" set "MAKENSIS=NSIS\makensis.exe"
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
exit /b %ERRORLEVEL%