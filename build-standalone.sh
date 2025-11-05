#!/bin/bash
# ChatMouse Standalone Build Script
# .NET 8.0 단일 파일 실행 파일 빌드

CONFIGURATION="Release"
RUNTIME="win-x64"

echo "====================================="
echo "ChatMouse Standalone Build"
echo "====================================="
echo ""

# Clean previous builds
echo "Cleaning previous builds..."
rm -rf bin obj publish
echo "Done."
echo ""

# Restore packages
echo "Restoring NuGet packages..."
dotnet restore
if [ $? -ne 0 ]; then
    echo "Failed to restore packages."
    exit 1
fi
echo "Done."
echo ""

# Build
echo "Building project..."
dotnet build -c $CONFIGURATION
if [ $? -ne 0 ]; then
    echo "Build failed."
    exit 1
fi
echo "Done."
echo ""

# Publish as single file
echo "Publishing as single file executable..."
dotnet publish ChatMouse.csproj -c $CONFIGURATION -r $RUNTIME \
    -p:PublishSingleFile=true \
    -p:SelfContained=true \
    -p:IncludeNativeLibrariesForSelfContained=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:PublishTrimmed=false \
    -p:PublishReadyToRun=true \
    -o "publish"

if [ $? -ne 0 ]; then
    echo "Publish failed."
    exit 1
fi
echo "Done."
echo ""

# Display results
echo "====================================="
echo "Build completed successfully!"
echo "====================================="
echo ""
echo "Output directory: $(pwd)/publish"
echo ""

ls -lh publish/

echo ""
echo "Run: ./publish/ChatMouse.exe"


