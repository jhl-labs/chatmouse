@echo off
REM ChatMouse Standalone Build Script (Batch)
REM Quick build script for Windows

echo =====================================
echo ChatMouse Standalone Build
echo =====================================
echo.

echo Cleaning previous builds...
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
if exist publish rmdir /s /q publish
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
dotnet publish ChatMouse.csproj -c Release -r win-x64 ^
    -p:PublishSingleFile=true ^
    -p:SelfContained=true ^
    -p:IncludeNativeLibrariesForSelfContract=true ^
    -p:IncludeAllContentForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:PublishTrimmed=false ^
    -p:PublishReadyToRun=true ^
    -o publish

if errorlevel 1 (
    echo Publish failed.
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

