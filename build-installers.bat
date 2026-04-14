@echo off
REM build-installers.bat - Build JJFlexRadio installers for both architectures
REM Usage: build-installers.bat [x64|x86|both]
REM Default: both
REM
REM IMPORTANT: This script does a CLEAN build to ensure version numbers are correct.
REM If you only updated JJFlexRadio.vbproj but not My Project\AssemblyInfo.vb,
REM the version will be WRONG. Update BOTH files before running this script!

setlocal
cd /d "%~dp0"

REM Tell the post-build event in .vbproj to skip install.bat — we handle it ourselves
set "JJFLEX_SKIP_POSTBUILD_INSTALLER=1"

set "ARCH=%~1"
if "%ARCH%"=="" set "ARCH=both"

echo ============================================
echo JJ Flexible Radio Access Installer Builder
echo ============================================
echo.

REM Show current version from project file
for /f "tokens=2 delims=<>" %%v in ('findstr /C:"<Version>" JJFlexRadio.vbproj') do (
    echo Project version: %%v
)
echo.

if /i "%ARCH%"=="x64" goto build_x64
if /i "%ARCH%"=="x86" goto build_x86
if /i "%ARCH%"=="both" goto build_both

echo Invalid option: %ARCH%
echo Usage: build-installers.bat [x64^|x86^|both]
exit /b 1

:build_both
echo Building both x64 and x86 installers...
echo.

REM Clean slate: remove both output folders and old setup files
echo Cleaning all previous builds and setup files...
if exist "bin\x64\Release" rmdir /s /q "bin\x64\Release"
if exist "bin\x86\Release" rmdir /s /q "bin\x86\Release"
del /q "Setup JJFlex_*_x64.exe" >nul 2>&1
del /q "Setup JJFlex_*_x86.exe" >nul 2>&1
del /q "Setup JJFlex_x64.exe" >nul 2>&1
del /q "Setup JJFlex_x86.exe" >nul 2>&1
REM Also clean old-naming installers
del /q "Setup JJFlexRadio_*_x64.exe" >nul 2>&1
del /q "Setup JJFlexRadio_*_x86.exe" >nul 2>&1
echo.

:build_x64
echo [x64] Cleaning previous build...
if exist "bin\x64\Release" rmdir /s /q "bin\x64\Release"

echo [x64] Building Release...
dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --no-incremental --verbosity minimal
if errorlevel 1 (
    echo ERROR: x64 build failed!
    exit /b 1
)

echo [x64] Creating installer...
call "%~dp0install.bat" "%~dp0" Release JJFlexRadio x64

echo.
if /i "%ARCH%"=="x64" goto done

:build_x86
echo [x86] Cleaning previous build...
if exist "bin\x86\Release" rmdir /s /q "bin\x86\Release"

echo [x86] Building Release...
dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x86 --no-incremental --verbosity minimal
if errorlevel 1 (
    echo ERROR: x86 build failed!
    exit /b 1
)

echo [x86] Creating installer...
call "%~dp0install.bat" "%~dp0" Release JJFlexRadio x86

echo.

:done
REM Clean up legacy unversioned copies (install.bat creates these as convenience aliases)
del /q "Setup JJFlex_x64.exe" >nul 2>&1
del /q "Setup JJFlex_x86.exe" >nul 2>&1

echo ============================================
echo Build complete! Installers created:
echo ============================================
for %%f in ("Setup JJFlex_*_x64.exe") do (
    if exist "%%f" echo   %%~nxf
)
for %%f in ("Setup JJFlex_*_x86.exe") do (
    if exist "%%f" echo   %%~nxf
)
echo.
echo Location: %~dp0
echo ============================================

REM Verify versions match
echo.
echo Verifying embedded versions:
if exist "bin\x64\Release\net10.0-windows\win-x64\JJFlexRadio.exe" (
    for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Item 'bin\x64\Release\net10.0-windows\win-x64\JJFlexRadio.exe').VersionInfo.ProductVersion"`) do (
        echo   x64: %%v
    )
)
if exist "bin\x86\Release\net10.0-windows\win-x86\JJFlexRadio.exe" (
    for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Item 'bin\x86\Release\net10.0-windows\win-x86\JJFlexRadio.exe').VersionInfo.ProductVersion"`) do (
        echo   x86: %%v
    )
)
echo.

endlocal
