# ChatMouse Standalone Build Script
# .NET 8.0 단일 파일 실행 파일 빌드

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "ChatMouse Standalone Build" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

# Kill running ChatMouse processes
Write-Host "Checking for running ChatMouse processes..." -ForegroundColor Yellow
$processes = Get-Process -Name "ChatMouse" -ErrorAction SilentlyContinue
if ($processes) {
    Write-Host "Terminating running ChatMouse.exe processes..." -ForegroundColor Yellow
    $processes | Stop-Process -Force
    Start-Sleep -Seconds 2
} else {
    Write-Host "No running ChatMouse.exe found." -ForegroundColor Gray
}
Write-Host ""

# Clean previous builds
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
if (Test-Path "bin") { Remove-Item -Recurse -Force "bin" -ErrorAction SilentlyContinue }
if (Test-Path "obj") { Remove-Item -Recurse -Force "obj" -ErrorAction SilentlyContinue }

if (Test-Path "publish") {
    Write-Host "Removing existing publish directory..." -ForegroundColor Yellow
    try {
        Remove-Item -Recurse -Force "publish" -ErrorAction Stop
    } catch {
        Write-Host "Waiting for file locks to be released..." -ForegroundColor Yellow
        Start-Sleep -Seconds 2
        try {
            Remove-Item -Recurse -Force "publish" -ErrorAction Stop
        } catch {
            Write-Host "WARNING: Could not fully remove publish. Attempting to remove individual files..." -ForegroundColor Yellow
            Remove-Item -Force "publish\*.*" -ErrorAction SilentlyContinue
        }
    }
}

Write-Host "Done." -ForegroundColor Green
Write-Host ""

# Restore packages
Write-Host "Restoring NuGet packages..." -ForegroundColor Yellow
dotnet restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to restore packages." -ForegroundColor Red
    exit 1
}
Write-Host "Done." -ForegroundColor Green
Write-Host ""

# Build
Write-Host "Building project..." -ForegroundColor Yellow
dotnet build -c $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed." -ForegroundColor Red
    exit 1
}
Write-Host "Done." -ForegroundColor Green
Write-Host ""

# Publish as single file
Write-Host "Publishing as single file executable..." -ForegroundColor Yellow

# Ensure existing executable is removed before publish
if (Test-Path "publish\ChatMouse.exe") {
    Write-Host "Removing existing ChatMouse.exe before publish..." -ForegroundColor Yellow
    try {
        Remove-Item -Force "publish\ChatMouse.exe" -ErrorAction Stop
        Start-Sleep -Seconds 1
    } catch {
        Write-Host "WARNING: Could not remove existing ChatMouse.exe. It may be locked." -ForegroundColor Yellow
    }
}

dotnet publish ChatMouse.csproj -c $Configuration -r $Runtime `
    -p:PublishSingleFile=true `
    -p:SelfContained=true `
    -p:IncludeNativeLibrariesForSelfContained=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=true `
    -o "publish"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed." -ForegroundColor Red
    Write-Host ""
    Write-Host "Troubleshooting:" -ForegroundColor Yellow
    Write-Host "1. Make sure ChatMouse.exe is not running"
    Write-Host "2. Close any file explorer windows showing the publish folder"
    Write-Host "3. Check if antivirus is scanning the file"
    Write-Host "4. Try running this script as administrator"
    exit 1
}
Write-Host "Done." -ForegroundColor Green
Write-Host ""

# Display results
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "Build completed successfully!" -ForegroundColor Green
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Output directory: $(Resolve-Path 'publish')" -ForegroundColor Cyan
Write-Host ""

# List files
Get-ChildItem "publish" | ForEach-Object {
    $size = if ($_.Length -gt 1MB) { "{0:N2} MB" -f ($_.Length / 1MB) } else { "{0:N2} KB" -f ($_.Length / 1KB) }
    Write-Host "  $($_.Name) - $size" -ForegroundColor Gray
}

Write-Host ""
Write-Host "Run: .\publish\ChatMouse.exe" -ForegroundColor Yellow


