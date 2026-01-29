@echo off
REM Build x64 native DLLs (libopus.dll and portaudio.dll) for JJFlexRadio
REM Requires: Visual Studio 2022+ with C++ workload, Git
REM Output: runtimes\win-x64\native\libopus.dll, portaudio.dll

setlocal enabledelayedexpansion

REM Setup paths
set "CMAKE=C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
set "BUILD_DIR=%~dp0build-native"
set "OUTPUT_DIR=%~dp0runtimes\win-x64\native"

echo ============================================
echo Building x64 Native DLLs for JJFlexRadio
echo ============================================
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
REM Build Opus
REM ============================================
echo.
echo [1/2] Building Opus (libopus.dll)...
echo.

if not exist "opus" (
    echo Cloning Opus repository...
    git clone --depth 1 https://gitlab.xiph.org/xiph/opus.git
    if errorlevel 1 (
        echo ERROR: Failed to clone Opus
        exit /b 1
    )
)

cd opus
if not exist "build-x64" mkdir "build-x64"
cd build-x64

echo Configuring Opus for x64...
"%CMAKE%" .. -A x64 -DCMAKE_BUILD_TYPE=Release -DOPUS_BUILD_SHARED_LIBRARY=ON
if errorlevel 1 (
    echo ERROR: CMake configure failed for Opus
    exit /b 1
)

echo Building Opus...
"%CMAKE%" --build . --config Release
if errorlevel 1 (
    echo ERROR: Build failed for Opus
    exit /b 1
)

REM Find and copy the DLL
set "OPUS_DLL="
if exist "Release\opus.dll" set "OPUS_DLL=Release\opus.dll"
if exist "opus.dll" set "OPUS_DLL=opus.dll"

if not defined OPUS_DLL (
    echo ERROR: Could not find opus.dll after build
    dir /s *.dll
    exit /b 1
)

echo Copying %OPUS_DLL% to %OUTPUT_DIR%\libopus.dll
copy /y "%OPUS_DLL%" "%OUTPUT_DIR%\libopus.dll"
if errorlevel 1 (
    echo ERROR: Failed to copy opus.dll
    exit /b 1
)
echo Opus build complete!

cd "%BUILD_DIR%"

REM ============================================
REM Build PortAudio
REM ============================================
echo.
echo [2/2] Building PortAudio (portaudio.dll)...
echo.

if not exist "portaudio" (
    echo Cloning PortAudio repository...
    git clone --depth 1 https://github.com/PortAudio/portaudio.git
    if errorlevel 1 (
        echo ERROR: Failed to clone PortAudio
        exit /b 1
    )
)

cd portaudio
if not exist "build-x64" mkdir "build-x64"
cd build-x64

echo Configuring PortAudio for x64...
"%CMAKE%" .. -A x64 -DCMAKE_BUILD_TYPE=Release -DPA_BUILD_SHARED=ON
if errorlevel 1 (
    echo ERROR: CMake configure failed for PortAudio
    exit /b 1
)

echo Building PortAudio...
"%CMAKE%" --build . --config Release
if errorlevel 1 (
    echo ERROR: Build failed for PortAudio
    exit /b 1
)

REM Find and copy the DLL
set "PA_DLL="
if exist "Release\portaudio.dll" set "PA_DLL=Release\portaudio.dll"
if exist "Release\portaudio_x64.dll" set "PA_DLL=Release\portaudio_x64.dll"
if exist "portaudio.dll" set "PA_DLL=portaudio.dll"

if not defined PA_DLL (
    echo ERROR: Could not find portaudio.dll after build
    dir /s *.dll
    exit /b 1
)

echo Copying %PA_DLL% to %OUTPUT_DIR%\portaudio.dll
copy /y "%PA_DLL%" "%OUTPUT_DIR%\portaudio.dll"
if errorlevel 1 (
    echo ERROR: Failed to copy portaudio.dll
    exit /b 1
)
echo PortAudio build complete!

REM ============================================
REM Summary
REM ============================================
echo.
echo ============================================
echo Build Complete!
echo ============================================
echo.
echo x64 DLLs created in: %OUTPUT_DIR%
echo.
dir "%OUTPUT_DIR%\*.dll"
echo.
echo You can now build the x64 version:
echo   dotnet build JJFlexRadio.sln -c Release -p:Platform=x64
echo.

cd /d "%~dp0"
exit /b 0
