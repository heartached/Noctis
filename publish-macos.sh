#!/bin/bash
# ============================================================
#  Noctis — macOS Publish Script
#  Builds for both Intel (x64) and Apple Silicon (arm64)
# ============================================================

set -e

echo ""
echo "  Building Noctis for macOS..."
echo ""

# Build for Apple Silicon (arm64)
echo "  [1/2] Building for macOS arm64 (Apple Silicon)..."
rm -rf publish/osx-arm64
dotnet publish src/Noctis/Noctis.csproj \
    -c Release \
    -r osx-arm64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o publish/osx-arm64

echo ""

# Build for Intel (x64)
echo "  [2/2] Building for macOS x64 (Intel)..."
rm -rf publish/osx-x64
dotnet publish src/Noctis/Noctis.csproj \
    -c Release \
    -r osx-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o publish/osx-x64

echo ""
echo "  ============================================================"
echo "   Build successful!"
echo "   Output:"
echo "     Apple Silicon: publish/osx-arm64/Noctis"
echo "     Intel:         publish/osx-x64/Noctis"
echo "  ============================================================"
echo ""
echo "  To run: chmod +x publish/osx-arm64/Noctis && ./publish/osx-arm64/Noctis"
echo ""
echo "  NOTE: On first run macOS may block the app. To allow it:"
echo "    System Settings > Privacy & Security > Allow 'Noctis'"
echo "  Or right-click the binary > Open to bypass Gatekeeper."
echo ""
