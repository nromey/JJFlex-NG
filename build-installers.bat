@echo off
REM build-installers.bat - Build JJFlexRadio installers for both architectures
REM Usage: build-installers.bat [x64^|x86^|both] [release^|nopub]
REM Default: both arch, daily publish
REM
REM VERSIONING (as of 2026-04-14)
REM   Base version (first three components) lives in JJFlexRadio.vbproj <Version>.
REM   This script computes Y = git rev-list --count HEAD + BUILDNUM_OFFSET and
REM   passes -p:Version=<base>.<Y> to dotnet build so every commit gets a unique
REM   monotonic build number. AssemblyInfo.vb no longer carries version attrs —
REM   the .vbproj is the single source of truth.
REM
REM PUBLISH MODES
REM   (default)  daily publish: timestamped installer -> NAS \historical\<version>\installers\,
REM              exe+pdb -> NAS \historical\<version>\<arch>\
REM   release    daily publish, then also promote to NAS \stable\ and to
REM              Dropbox top-level (moving prior stables to archive/old).
REM              Use this for cut-a-public-release runs.
REM   nopub      skip NAS entirely. Local build only. Use when NAS is down
REM              or you're just smoke-testing the build itself.
REM
REM End-of-day: say "done developing" to Claude and the latest NAS daily
REM will be copied to Dropbox as the visible daily build. Do NOT pipe every
REM intraday build into Dropbox — it spams notifications for all shared users.

setlocal enabledelayedexpansion
cd /d "%~dp0"

REM ---------------------------------------------------------------------------
REM CONFIG
REM ---------------------------------------------------------------------------
REM Offset so Y = git commit count + offset lines up with our desired start.
REM Current git count was 469 on 2026-04-14; offset -468 makes today's Y = 1.
REM Do NOT change this without a good reason — Y must be monotonic forever.
set "BUILDNUM_OFFSET=-468"

REM ---------------------------------------------------------------------------
REM ARGS
REM ---------------------------------------------------------------------------
set "ARCH=%~1"
if "%ARCH%"=="" set "ARCH=both"

set "PUBMODE=%~2"
if /i "%PUBMODE%"=="" set "PUBMODE=daily"

if /i not "%PUBMODE%"=="daily" if /i not "%PUBMODE%"=="release" if /i not "%PUBMODE%"=="nopub" (
    echo Invalid publish mode: %PUBMODE%
    echo Usage: build-installers.bat [x64^^^|x86^^^|both] [release^^^|nopub]
    exit /b 1
)

REM Tell the post-build event in .vbproj to skip install.bat — we handle it ourselves
set "JJFLEX_SKIP_POSTBUILD_INSTALLER=1"

echo ============================================
echo JJ Flexible Radio Access Installer Builder
echo ============================================
echo.

REM ---------------------------------------------------------------------------
REM Read base version from .vbproj and compute full version
REM ---------------------------------------------------------------------------
REM Extract the base version via a strict regex so we can't be fooled by a
REM comment or attribute that happens to contain the string "<Version>".
set "BASEVER="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Select-String -Path 'JJFlexRadio.vbproj' -Pattern '<Version>([0-9][0-9.]*)</Version>' | Select-Object -First 1).Matches.Groups[1].Value"`) do set "BASEVER=%%v"
if "%BASEVER%"=="" (
    echo ERROR: Could not read ^<Version^> from JJFlexRadio.vbproj
    exit /b 2
)

for /f "usebackq delims=" %%c in (`git rev-list --count HEAD 2^>nul`) do set "GITCOUNT=%%c"
if "%GITCOUNT%"=="" (
    echo WARNING: git rev-list failed. Using Y=0.
    set "GITCOUNT=0"
)
set /a "BUILDNUM=%GITCOUNT% + %BUILDNUM_OFFSET%"
set "APPVER=%BASEVER%.%BUILDNUM%"

echo Base version      : %BASEVER%
echo Git commit count  : %GITCOUNT%
echo Build number (Y)  : %BUILDNUM%
echo Full version      : %APPVER%
echo Publish mode      : %PUBMODE%
echo Architecture(s)   : %ARCH%
echo.

REM ---------------------------------------------------------------------------
REM DISPATCH
REM ---------------------------------------------------------------------------
set "BUILD_X64=0"
set "BUILD_X86=0"
if /i "%ARCH%"=="x64"  set "BUILD_X64=1"
if /i "%ARCH%"=="x86"  set "BUILD_X86=1"
if /i "%ARCH%"=="both" set "BUILD_X64=1" & set "BUILD_X86=1"

if "%BUILD_X64%%BUILD_X86%"=="00" (
    echo Invalid architecture: %ARCH%
    echo Usage: build-installers.bat [x64^^^|x86^^^|both] [release^^^|nopub]
    exit /b 1
)

if "%BUILD_X64%%BUILD_X86%"=="11" (
    echo Cleaning all previous builds and setup files...
    if exist "bin\x64\Release" rmdir /s /q "bin\x64\Release"
    if exist "bin\x86\Release" rmdir /s /q "bin\x86\Release"
    del /q "Setup JJFlex_*_x64.exe" >nul 2>&1
    del /q "Setup JJFlex_*_x86.exe" >nul 2>&1
    del /q "Setup JJFlex_x64.exe" >nul 2>&1
    del /q "Setup JJFlex_x86.exe" >nul 2>&1
    del /q "Setup JJFlexRadio_*_x64.exe" >nul 2>&1
    del /q "Setup JJFlexRadio_*_x86.exe" >nul 2>&1
    echo.
)

if "%BUILD_X64%"=="1" (
    echo [x64] Cleaning previous build...
    if exist "bin\x64\Release" rmdir /s /q "bin\x64\Release"

    echo [x64] Building Release as %APPVER%...
    dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --no-incremental --verbosity minimal -p:Version=%APPVER%
    if errorlevel 1 (
        echo ERROR: x64 build failed
        exit /b 1
    )

    echo [x64] Creating installer...
    call "%~dp0install.bat" "%~dp0" Release JJFlexRadio x64
    if errorlevel 1 (
        echo ERROR: x64 installer failed
        exit /b 1
    )
    echo.
)

if "%BUILD_X86%"=="1" (
    echo [x86] Cleaning previous build...
    if exist "bin\x86\Release" rmdir /s /q "bin\x86\Release"

    echo [x86] Building Release as %APPVER%...
    dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x86 --no-incremental --verbosity minimal -p:Version=%APPVER%
    if errorlevel 1 (
        echo ERROR: x86 build failed
        exit /b 1
    )

    echo [x86] Creating installer...
    call "%~dp0install.bat" "%~dp0" Release JJFlexRadio x86
    if errorlevel 1 (
        echo ERROR: x86 installer failed
        exit /b 1
    )
    echo.
)
REM Clean up legacy unversioned copies (install.bat creates these as convenience aliases)
del /q "Setup JJFlex_x64.exe" >nul 2>&1
del /q "Setup JJFlex_x86.exe" >nul 2>&1

echo ============================================
echo Build complete. Installers created:
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

REM ---------------------------------------------------------------------------
REM MANIFEST GEN HOOK (Sprint 29 Track N)
REM ---------------------------------------------------------------------------
REM Off by default for local builds. Roarbox CI/CD sets these env vars to opt
REM into manifest generation + R2 upload after the installer builds. Local
REM smoke-test mode: set JJFLEX_MANIFEST_GEN_MODE=dry-run (no R2 calls).
REM
REM Env vars consumed:
REM   JJFLEX_MANIFEST_GEN_MODE     unset/empty=off | dry-run | live
REM   JJFLEX_MANIFEST_GEN_CHANNEL  nightly | beta | stable  (required if MODE set)
REM   R2_*                         credentials (required if MODE=live; see README)
REM ---------------------------------------------------------------------------
if not "%JJFLEX_MANIFEST_GEN_MODE%"=="" (
    if "%JJFLEX_MANIFEST_GEN_CHANNEL%"=="" (
        echo WARNING: JJFLEX_MANIFEST_GEN_MODE set but JJFLEX_MANIFEST_GEN_CHANNEL is empty. Skipping manifest gen.
    ) else (
        set "MGEN_FLAGS="
        if /i "%JJFLEX_MANIFEST_GEN_MODE%"=="dry-run" set "MGEN_FLAGS=--dry-run"
        if /i "%JJFLEX_MANIFEST_GEN_MODE%"=="live" set "MGEN_FLAGS=--update-app-manifest"

        if "%BUILD_X64%"=="1" (
            echo.
            echo [manifest-gen] x64 ^(mode=%JJFLEX_MANIFEST_GEN_MODE% channel=%JJFLEX_MANIFEST_GEN_CHANNEL%^)
            python "%~dp0tools\jjflex-manifest-gen\manifest_gen.py" generate ^
                --published-dir "%~dp0bin\x64\Release\net10.0-windows\win-x64" ^
                --version %APPVER% ^
                --platform win-x64 ^
                --channel %JJFLEX_MANIFEST_GEN_CHANNEL% ^
                !MGEN_FLAGS!
            if errorlevel 1 echo WARNING: manifest-gen x64 returned non-zero.
        )

        if "%BUILD_X86%"=="1" (
            echo.
            echo [manifest-gen] x86 ^(mode=%JJFLEX_MANIFEST_GEN_MODE% channel=%JJFLEX_MANIFEST_GEN_CHANNEL%^)
            python "%~dp0tools\jjflex-manifest-gen\manifest_gen.py" generate ^
                --published-dir "%~dp0bin\x86\Release\net10.0-windows\win-x86" ^
                --version %APPVER% ^
                --platform win-x86 ^
                --channel %JJFLEX_MANIFEST_GEN_CHANNEL% ^
                !MGEN_FLAGS!
            if errorlevel 1 echo WARNING: manifest-gen x86 returned non-zero.
        )
    )
)

REM ---------------------------------------------------------------------------
REM PUBLISH
REM ---------------------------------------------------------------------------
if /i "%PUBMODE%"=="nopub" (
    echo.
    echo Publish mode: nopub  --  skipping NAS publish.
    goto :eof
)

if /i "%ARCH%"=="both" set "ARCH_LIST=x64,x86"
if /i "%ARCH%"=="x64"  set "ARCH_LIST=x64"
if /i "%ARCH%"=="x86"  set "ARCH_LIST=x86"

set "RELEASE_FLAG="
if /i "%PUBMODE%"=="release" set "RELEASE_FLAG=-Release"

echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-to-nas.ps1" -ProjectRoot "%~dp0." -Version "%APPVER%" -Archs !ARCH_LIST! %RELEASE_FLAG%

endlocal
