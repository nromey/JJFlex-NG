@echo off
REM Build native DLLs (libopus.dll and portaudio.dll) for JJFlexRadio
REM Builds BOTH x64 and x86 from latest release sources
REM Requires: Visual Studio with C++ workload (CMake tools), Git
REM
REM Usage:
REM   build-native.bat         Build both x64 and x86
REM   build-native.bat x64     Build x64 only
REM   build-native.bat x86     Build x86 only
REM
REM Sources:
REM   Opus:      libopus 1.6.1 from https://gitlab.xiph.org/xiph/opus (tag v1.6.1)
REM   PortAudio: v19.7.0 from https://github.com/PortAudio/portaudio (tag v19.7.0)
REM
REM Output:
REM   runtimes\win-x64\native\libopus.dll, portaudio.dll
REM   runtimes\win-x86\native\libopus.dll, portaudio.dll

setlocal enabledelayedexpansion

REM Setup paths
set "CMAKE=C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
set "BUILD_DIR=%~dp0build-native"
set "X64_DIR=%~dp0runtimes\win-x64\native"
set "X86_DIR=%~dp0runtimes\win-x86\native"

REM Parse arguments
set "BUILD_X64=0"
set "BUILD_X86=0"
if "%~1"=="" (
    set "BUILD_X64=1"
    set "BUILD_X86=1"
) else if /i "%~1"=="x64" (
    set "BUILD_X64=1"
) else if /i "%~1"=="x86" (
    set "BUILD_X86=1"
) else (
    echo Usage: build-native.bat [x64^|x86]
    echo   No argument = build both
    exit /b 1
)

echo ============================================
echo Building Native DLLs for JJFlexRadio
echo ============================================
if "%BUILD_X64%"=="1" echo   x64: YES
if "%BUILD_X86%"=="1" echo   x86: YES
echo.

REM Check cmake
if not exist "%CMAKE%" (
    echo ERROR: CMake not found at: %CMAKE%
    echo Please install Visual Studio with C++ CMake tools
    exit /b 1
)

REM Create build directory
if not exist "%BUILD_DIR%" mkdir "%BUILD_DIR%"
cd /d "%BUILD_DIR%"

REM ============================================
REM Clone/update Opus source
REM ============================================
echo.
echo [Step 1] Getting Opus source (v1.6.1)...
echo.

if not exist "opus" (
    echo Cloning Opus repository...
    git clone https://gitlab.xiph.org/xiph/opus.git
    if errorlevel 1 (
        echo ERROR: Failed to clone Opus. Trying GitHub mirror...
        git clone https://github.com/xiph/opus.git
        if errorlevel 1 (
            echo ERROR: Failed to clone Opus from both sources
            exit /b 1
        )
    )
)

cd opus
echo Fetching latest tags...
git fetch --tags 2>nul
echo Checking out v1.6.1...
git checkout v1.6.1 2>nul
if errorlevel 1 (
    echo WARNING: Tag v1.6.1 not found, trying v1.5.2...
    git checkout v1.5.2 2>nul
    if errorlevel 1 (
        echo WARNING: No release tag found, using latest main
    )
)
echo Opus source ready.
cd "%BUILD_DIR%"

REM ============================================
REM Clone/update PortAudio source
REM ============================================
echo.
echo [Step 2] Getting PortAudio source (v19.7.0)...
echo.

if not exist "portaudio" (
    echo Cloning PortAudio repository...
    git clone https://github.com/PortAudio/portaudio.git
    if errorlevel 1 (
        echo ERROR: Failed to clone PortAudio
        exit /b 1
    )
)

cd portaudio
echo Fetching latest tags...
git fetch --tags 2>nul
echo Checking out v19.7.0...
git checkout v19.7.0 2>nul
if errorlevel 1 (
    echo WARNING: Tag v19.7.0 not found, using latest main
)
echo PortAudio source ready.
cd "%BUILD_DIR%"

REM ============================================
REM Build x64
REM ============================================
if "%BUILD_X64%"=="1" (
    echo.
    echo ============================================
    echo Building x64 DLLs
    echo ============================================

    REM --- Opus x64 ---
    echo.
    echo [x64] Building Opus...
    cd "%BUILD_DIR%\opus"
    if exist "build-x64" rmdir /s /q "build-x64"
    mkdir "build-x64"
    cd "build-x64"

    "%CMAKE%" .. -A x64 -DCMAKE_BUILD_TYPE=Release -DOPUS_BUILD_SHARED_LIBRARY=ON -DOPUS_BUILD_PROGRAMS=OFF -DOPUS_BUILD_TESTING=OFF
    if errorlevel 1 (
        echo ERROR: CMake configure failed for Opus x64
        exit /b 1
    )

    "%CMAKE%" --build . --config Release
    if errorlevel 1 (
        echo ERROR: Build failed for Opus x64
        exit /b 1
    )

    set "OPUS_DLL="
    if exist "Release\opus.dll" set "OPUS_DLL=Release\opus.dll"
    if exist "opus.dll" set "OPUS_DLL=opus.dll"
    if not defined OPUS_DLL (
        echo ERROR: Could not find opus.dll after x64 build
        dir /s *.dll 2>nul
        exit /b 1
    )
    copy /y "!OPUS_DLL!" "%X64_DIR%\libopus.dll"
    echo [x64] Opus complete.

    REM --- PortAudio x64 ---
    echo.
    echo [x64] Building PortAudio...
    cd "%BUILD_DIR%\portaudio"
    if exist "build-x64" rmdir /s /q "build-x64"
    mkdir "build-x64"
    cd "build-x64"

    "%CMAKE%" .. -A x64 -DCMAKE_BUILD_TYPE=Release -DPA_BUILD_SHARED=ON -DPA_BUILD_STATIC=OFF -DPA_BUILD_TESTS=OFF -DPA_BUILD_EXAMPLES=OFF -DCMAKE_POLICY_VERSION_MINIMUM=3.5
    if errorlevel 1 (
        echo ERROR: CMake configure failed for PortAudio x64
        exit /b 1
    )

    "%CMAKE%" --build . --config Release
    if errorlevel 1 (
        echo ERROR: Build failed for PortAudio x64
        exit /b 1
    )

    set "PA_DLL="
    if exist "Release\portaudio.dll" set "PA_DLL=Release\portaudio.dll"
    if exist "Release\portaudio_x64.dll" set "PA_DLL=Release\portaudio_x64.dll"
    if exist "portaudio.dll" set "PA_DLL=portaudio.dll"
    if not defined PA_DLL (
        echo ERROR: Could not find portaudio.dll after x64 build
        dir /s *.dll 2>nul
        exit /b 1
    )
    copy /y "!PA_DLL!" "%X64_DIR%\portaudio.dll"
    echo [x64] PortAudio complete.
)

REM ============================================
REM Build x86
REM ============================================
if "%BUILD_X86%"=="1" (
    echo.
    echo ============================================
    echo Building x86 DLLs
    echo ============================================

    REM --- Opus x86 ---
    echo.
    echo [x86] Building Opus...
    cd "%BUILD_DIR%\opus"
    if exist "build-x86" rmdir /s /q "build-x86"
    mkdir "build-x86"
    cd "build-x86"

    "%CMAKE%" .. -A Win32 -DCMAKE_BUILD_TYPE=Release -DOPUS_BUILD_SHARED_LIBRARY=ON -DOPUS_BUILD_PROGRAMS=OFF -DOPUS_BUILD_TESTING=OFF
    if errorlevel 1 (
        echo ERROR: CMake configure failed for Opus x86
        exit /b 1
    )

    "%CMAKE%" --build . --config Release
    if errorlevel 1 (
        echo ERROR: Build failed for Opus x86
        exit /b 1
    )

    set "OPUS_DLL="
    if exist "Release\opus.dll" set "OPUS_DLL=Release\opus.dll"
    if exist "opus.dll" set "OPUS_DLL=opus.dll"
    if not defined OPUS_DLL (
        echo ERROR: Could not find opus.dll after x86 build
        dir /s *.dll 2>nul
        exit /b 1
    )
    copy /y "!OPUS_DLL!" "%X86_DIR%\libopus.dll"
    echo [x86] Opus complete.

    REM --- PortAudio x86 ---
    echo.
    echo [x86] Building PortAudio...
    cd "%BUILD_DIR%\portaudio"
    if exist "build-x86" rmdir /s /q "build-x86"
    mkdir "build-x86"
    cd "build-x86"

    "%CMAKE%" .. -A Win32 -DCMAKE_BUILD_TYPE=Release -DPA_BUILD_SHARED=ON -DPA_BUILD_STATIC=OFF -DPA_BUILD_TESTS=OFF -DPA_BUILD_EXAMPLES=OFF -DCMAKE_POLICY_VERSION_MINIMUM=3.5
    if errorlevel 1 (
        echo ERROR: CMake configure failed for PortAudio x86
        exit /b 1
    )

    "%CMAKE%" --build . --config Release
    if errorlevel 1 (
        echo ERROR: Build failed for PortAudio x86
        exit /b 1
    )

    set "PA_DLL="
    if exist "Release\portaudio.dll" set "PA_DLL=Release\portaudio.dll"
    if exist "Release\portaudio_x86.dll" set "PA_DLL=Release\portaudio_x86.dll"
    if exist "portaudio.dll" set "PA_DLL=portaudio.dll"
    if not defined PA_DLL (
        echo ERROR: Could not find portaudio.dll after x86 build
        dir /s *.dll 2>nul
        exit /b 1
    )
    copy /y "!PA_DLL!" "%X86_DIR%\portaudio.dll"
    echo [x86] PortAudio complete.
)

REM ============================================
REM Summary
REM ============================================
echo.
echo ============================================
echo Build Complete!
echo ============================================
echo.

if "%BUILD_X64%"=="1" (
    echo x64 DLLs in: %X64_DIR%
    dir "%X64_DIR%\libopus.dll" "%X64_DIR%\portaudio.dll" 2>nul
    echo.
)
if "%BUILD_X86%"=="1" (
    echo x86 DLLs in: %X86_DIR%
    dir "%X86_DIR%\libopus.dll" "%X86_DIR%\portaudio.dll" 2>nul
    echo.
)

echo Next steps:
echo   dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --no-incremental
echo   dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x86 --no-incremental
echo.

cd /d "%~dp0"
exit /b 0
