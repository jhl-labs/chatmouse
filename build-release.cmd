@echo off
REM ChatMouse Standalone Build Script (Batch)
REM Quick build script for Windows

echo =====================================
echo ChatMouse Standalone Build
echo =====================================
echo.

echo Checking for running ChatMouse processes...
taskkill /F /IM ChatMouse.exe >nul 2>&1
if errorlevel 1 (
    echo No running ChatMouse.exe found.
) else (
    echo Terminated running ChatMouse.exe processes.
    timeout /t 2 /nobreak >nul
)
echo.

echo Cleaning previous builds...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj

if exist publish (
    echo Removing existing publish directory...
    rmdir /s /q publish 2>nul
    if exist publish (
        echo Waiting for file locks to be released...
        timeout /t 2 /nobreak >nul
        rmdir /s /q publish 2>nul
        if exist publish (
            echo WARNING: Could not fully remove publish. Some files may be locked.
            del /F /Q publish\*.* >nul 2>&1
        )
    )
)
echo Done.
echo.

echo Restoring NuGet packages...
dotnet restore
if errorlevel 1 (
    echo Failed to restore packages.
    pause
    exit /b 1
)
echo Done.
echo.

echo Building project...
dotnet build -c Release
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)
echo Done.
echo.

echo Publishing as single file executable...
if exist publish\ChatMouse.exe (
    echo Removing existing ChatMouse.exe before publish...
    del /F /Q publish\ChatMouse.exe >nul 2>&1
    timeout /t 1 /nobreak >nul
)

dotnet publish ChatMouse.csproj -c Release -r win-x64 ^
    -p:PublishSingleFile=true ^
    -p:SelfContained=true ^
    -p:IncludeNativeLibrariesForSelfContained=true ^
    -p:IncludeAllContentForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:PublishTrimmed=false ^
    -p:PublishReadyToRun=true ^
    -o publish

if errorlevel 1 (
    echo Publish failed.
    echo.
    echo Troubleshooting:
    echo 1. Make sure ChatMouse.exe is not running
    echo 2. Close any file explorer windows showing the publish folder
    echo 3. Check if antivirus is scanning the file
    echo 4. Try running this script as administrator
    pause
    exit /b 1
)
echo Done.
echo.

echo =====================================
echo Build completed successfully!
echo =====================================
echo.
echo Output: %cd%\publish\ChatMouse.exe
echo.

dir publish\ChatMouse.exe

echo.
echo Run: publish\ChatMouse.exe
pause

