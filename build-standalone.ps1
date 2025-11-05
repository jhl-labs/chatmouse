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

# Clean previous builds
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
if (Test-Path "bin") { Remove-Item -Recurse -Force "bin" }
if (Test-Path "obj") { Remove-Item -Recurse -Force "obj" }
if (Test-Path "publish") { Remove-Item -Recurse -Force "publish" }

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


