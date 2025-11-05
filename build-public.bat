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

REM Kill running ChatMouse processes before cleanup
echo Checking for running ChatMouse processes...
taskkill /F /IM ChatMouse.exe >nul 2>&1
if errorlevel 1 (
    echo No running ChatMouse.exe found.
) else (
    echo Terminated running ChatMouse.exe processes.
    timeout /t 2 /nobreak >nul
)
echo.

REM Clean previous builds
echo Cleaning previous builds...
if exist bin rmdir /s /q bin 2>nul
if exist obj rmdir /s /q obj 2>nul

REM Try to remove publish directory with retry
if exist %OUTPUT_DIR% (
    echo Removing existing %OUTPUT_DIR% directory...
    rmdir /s /q %OUTPUT_DIR% 2>nul
    if exist %OUTPUT_DIR% (
        echo Waiting for file locks to be released...
        timeout /t 2 /nobreak >nul
        rmdir /s /q %OUTPUT_DIR% 2>nul
        if exist %OUTPUT_DIR% (
            echo WARNING: Could not fully remove %OUTPUT_DIR%. Some files may be locked.
            echo Attempting to remove individual files...
            del /F /Q %OUTPUT_DIR%\*.* >nul 2>&1
        )
    )
)
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
REM Ensure publish directory doesn't exist before publish
if exist %OUTPUT_DIR%\ChatMouse.exe (
    echo Removing existing ChatMouse.exe before publish...
    del /F /Q %OUTPUT_DIR%\ChatMouse.exe >nul 2>&1
    timeout /t 1 /nobreak >nul
)

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
    echo.
    echo Troubleshooting:
    echo 1. Make sure ChatMouse.exe is not running
    echo 2. Close any file explorer windows showing the publish folder
    echo 3. Check if antivirus is scanning the file
    echo 4. Try running this script as administrator
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

