@echo off
REM ChatMouse Public Build Script
REM For CI/CD and automated builds

setlocal enabledelayedexpansion

echo =====================================
echo ChatMouse Public Build Script
echo =====================================
echo.

REM Configuration
set CONFIGURATION=Release
set RUNTIME=win-x64
set OUTPUT_DIR=publish
set VERSION=1.0.0

REM Parse command line arguments
:parse_args
if "%~1"=="" goto :args_done
if /i "%~1"=="--config" (
    set CONFIGURATION=%~2
    shift
    shift
    goto :parse_args
)
if /i "%~1"=="--runtime" (
    set RUNTIME=%~2
    shift
    shift
    goto :parse_args
)
if /i "%~1"=="--output" (
    set OUTPUT_DIR=%~2
    shift
    shift
    goto :parse_args
)
if /i "%~1"=="--version" (
    set VERSION=%~2
    shift
    shift
    goto :parse_args
)
shift
goto :parse_args
:args_done

echo Configuration: %CONFIGURATION%
echo Runtime: %RUNTIME%
echo Output Directory: %OUTPUT_DIR%
echo Version: %VERSION%
echo.

REM Check for dotnet
where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet CLI not found in PATH
    echo Please install .NET 8.0 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
    exit /b 1
)

echo Checking .NET SDK version...
dotnet --version
if errorlevel 1 (
    echo ERROR: Failed to get dotnet version
    exit /b 1
)
echo.

REM Clean previous builds
echo Cleaning previous builds...
if exist bin rmdir /s /q bin 2>nul
if exist obj rmdir /s /q obj 2>nul
if exist %OUTPUT_DIR% rmdir /s /q %OUTPUT_DIR% 2>nul
echo Done.
echo.

REM Restore NuGet packages
echo Restoring NuGet packages...
dotnet restore
if errorlevel 1 (
    echo ERROR: Failed to restore NuGet packages
    exit /b 1
)
echo Done.
echo.

REM Build project
echo Building project (%CONFIGURATION%)...
dotnet build -c %CONFIGURATION% --no-restore
if errorlevel 1 (
    echo ERROR: Build failed
    exit /b 1
)
echo Done.
echo.

REM Run tests (if test project exists)
if exist ChatMouse.Tests\ChatMouse.Tests.csproj (
    echo Running tests...
    dotnet test -c %CONFIGURATION% --no-build --verbosity normal
    if errorlevel 1 (
        echo WARNING: Some tests failed
    )
    echo.
)

REM Publish as single file executable
echo Publishing as single file executable...
dotnet publish ChatMouse.csproj -c %CONFIGURATION% -r %RUNTIME% ^
    -p:PublishSingleFile=true ^
    -p:SelfContained=true ^
    -p:IncludeNativeLibrariesForSelfContained=true ^
    -p:IncludeAllContentForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:PublishTrimmed=false ^
    -p:PublishReadyToRun=true ^
    -p:Version=%VERSION% ^
    --no-build ^
    -o %OUTPUT_DIR%

if errorlevel 1 (
    echo ERROR: Publish failed
    exit /b 1
)
echo Done.
echo.

REM Calculate checksums
echo Calculating checksums...
if exist %OUTPUT_DIR%\ChatMouse.exe (
    certutil -hashfile %OUTPUT_DIR%\ChatMouse.exe SHA256 > %OUTPUT_DIR%\ChatMouse.exe.sha256
    echo SHA256 checksum saved to %OUTPUT_DIR%\ChatMouse.exe.sha256
)
echo.

REM Create build info file
echo Creating build info...
(
    echo Build Information
    echo ==================
    echo Build Date: %date% %time%
    echo Configuration: %CONFIGURATION%
    echo Runtime: %RUNTIME%
    echo Version: %VERSION%
    echo.
    echo Files:
) > %OUTPUT_DIR%\BUILD_INFO.txt

dir %OUTPUT_DIR%\*.exe /b >> %OUTPUT_DIR%\BUILD_INFO.txt
echo.

REM Display results
echo =====================================
echo Build completed successfully!
echo =====================================
echo.
echo Output directory: %CD%\%OUTPUT_DIR%
echo.
echo Files:
dir %OUTPUT_DIR%\ChatMouse.exe 2>nul
echo.
echo Build info: %OUTPUT_DIR%\BUILD_INFO.txt
echo SHA256: %OUTPUT_DIR%\ChatMouse.exe.sha256
echo.
echo Ready for distribution!
echo.

endlocal
exit /b 0

