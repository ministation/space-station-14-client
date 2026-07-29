@echo off
setlocal enabledelayedexpansion

:: Build script for Android APK (Windows)
:: Usage: build-apk.bat [--release] [--install] [--clean] [--arch arm64|x64]

set "CONFIG=Debug"
set "INSTALL=false"
set "CLEAN=false"
set "ARCH=arm64"
set "APK_DIR=artifacts\apk"

:: Parse arguments
:parse_args
if "%~1"=="" goto :after_parse
if /i "%~1"=="--release" (
    set "CONFIG=Release"
    shift
    goto :parse_args
)
if /i "%~1"=="--install" (
    set "INSTALL=true"
    shift
    goto :parse_args
)
if /i "%~1"=="--clean" (
    set "CLEAN=true"
    shift
    goto :parse_args
)
if /i "%~1"=="--arch" (
    set "ARCH=%~2"
    shift
    shift
    goto :parse_args
)
echo Unknown argument: %~1
exit /b 1

:after_parse

:: Validate architecture
if /i not "%ARCH%"=="arm64" if /i not "%ARCH%"=="x64" (
    echo Invalid architecture: %ARCH%. Use arm64 or x64.
    exit /b 1
)

echo ========================================
echo Building Android APK
echo Configuration: %CONFIG%
echo Architecture: %ARCH%
echo Install after build: %INSTALL%
echo Clean before build: %CLEAN%
echo ========================================

:: Clean if requested
if "%CLEAN%"=="true" (
    echo Cleaning previous builds...
    if exist "%APK_DIR%" rmdir /s /q "%APK_DIR%"
    dotnet clean
)

:: Create output directory
if not exist "%APK_DIR%\%CONFIG%" mkdir "%APK_DIR%\%CONFIG%"

:: Build APK
echo Building APK for %ARCH%...
dotnet publish -c %CONFIG% -f net10.0-android -p:RuntimeIdentifier=android-%ARCH% -o "%APK_DIR%\%CONFIG%\%ARCH%"

if errorlevel 1 (
    echo Build failed!
    exit /b 1
)

:: Find the APK file
set "APK_FILE="
for %%F in ("%APK_DIR%\%CONFIG%\%ARCH%\*.apk") do (
    set "APK_FILE=%%F"
    goto :found_apk
)

:found_apk
if "%APK_FILE%"=="" (
    echo APK file not found in output directory!
    exit /b 1
)

echo ========================================
echo Build successful!
echo APK location: %APK_FILE%
echo ========================================

:: Install on device if requested
if "%INSTALL%"=="true" (
    echo Checking for ADB...
    where adb >nul 2>nul
    if errorlevel 1 (
        echo ADB not found! Please install Android SDK Platform Tools and add to PATH.
        exit /b 1
    )

    echo Checking for connected devices...
    adb devices | findstr /r "device$" >nul
    if errorlevel 1 (
        echo No Android device found. Please connect a device and enable USB debugging.
        exit /b 1
    )

    echo Installing APK on device...
    adb install -r "%APK_FILE%"
    
    if errorlevel 1 (
        echo Installation failed!
        exit /b 1
    )
    
    echo Installation successful!
)

endlocal
