#!/bin/bash
# Build script for creating Android APK for testing
# Usage: ./build-apk.sh [--release] [--install] [--clean]

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_PATH="${SCRIPT_DIR}/probes/Probe.AndroidHost/Probe.AndroidHost.csproj"
SOLUTION_PATH="${SCRIPT_DIR}/Robust.AndroidPort.sln"

# Default configuration
CONFIGURATION="Debug"
INSTALL=false
CLEAN=false
RID=""

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --release)
            CONFIGURATION="Release"
            shift
            ;;
        --install)
            INSTALL=true
            shift
            ;;
        --clean)
            CLEAN=true
            shift
            ;;
        --arm64)
            RID="-r android-arm64"
            shift
            ;;
        --x64)
            RID="-r android-x64"
            shift
            ;;
        --help)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --release   Build in Release mode (default: Debug)"
            echo "  --install   Install APK on connected device after build"
            echo "  --clean     Clean before building"
            echo "  --arm64     Build for ARM64 architecture"
            echo "  --x64       Build for x64 architecture"
            echo "  --help      Show this help message"
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            echo "Use --help for usage information"
            exit 1
            ;;
    esac
done

echo "=========================================="
echo "  Robust Android Port - APK Build Script"
echo "=========================================="
echo ""
echo "Configuration: ${CONFIGURATION}"
echo "Project: ${PROJECT_PATH}"
if [ -n "$RID" ]; then
    echo "Runtime ID: ${RID}"
fi
echo ""

# Clean if requested
if [ "$CLEAN" = true ]; then
    echo "🧹 Cleaning build artifacts..."
    dotnet clean "${SOLUTION_PATH}" -c "${CONFIGURATION}"
    echo ""
fi

# Check .NET SDK and Android workload
echo "🔍 Checking .NET SDK..."
dotnet --version
echo ""

# Build the project
echo "🔨 Building APK (${CONFIGURATION})..."
if [ "$CLEAN" = false ]; then
    # Restore first if not cleaning
    dotnet restore "${SOLUTION_PATH}"
fi

dotnet publish "${PROJECT_PATH}" \
    -c "${CONFIGURATION}" \
    ${RID} \
    -o "${SCRIPT_DIR}/artifacts/apk/${CONFIGURATION}" \
    /p:AndroidPackageFormat=apk

echo ""
echo "✅ Build completed successfully!"
echo ""

# Find the APK file
APK_OUTPUT_DIR="${SCRIPT_DIR}/artifacts/apk/${CONFIGURATION}"
APK_FILE=$(find "${APK_OUTPUT_DIR}" -name "*.apk" -type f | head -1)

if [ -z "${APK_FILE}" ]; then
    echo "❌ Error: APK file not found in ${APK_OUTPUT_DIR}"
    exit 1
fi

echo "📦 APK created: ${APK_FILE}"
echo "   Size: $(du -h "${APK_FILE}" | cut -f1)"
echo ""

# Install on device if requested
if [ "$INSTALL" = true ]; then
    echo "📱 Installing APK on device..."
    
    # Check if adb is available
    if ! command -v adb &> /dev/null; then
        echo "❌ Error: adb (Android Debug Bridge) not found."
        echo "   Please install Android SDK Platform Tools."
        exit 1
    fi
    
    # Check for connected devices
    DEVICE_COUNT=$(adb devices | grep -v "^$" | grep -v "List" | wc -l)
    if [ "${DEVICE_COUNT}" -eq 0 ]; then
        echo "❌ Error: No Android devices found."
        echo "   Connect a device or start an emulator."
        exit 1
    fi
    
    echo "Found ${DEVICE_COUNT} device(s):"
    adb devices
    echo ""
    
    # Install the APK
    echo "Installing ${APK_FILE}..."
    adb install -r "${APK_FILE}"
    
    if [ $? -eq 0 ]; then
        echo ""
        echo "✅ Installation successful!"
        echo ""
        echo "To launch the app, run:"
        echo "  adb shell am start -n ru.ministation.robust.port/.MainActivity"
    else
        echo ""
        echo "❌ Installation failed!"
        exit 1
    fi
else
    echo "💡 To install on a device, run:"
    echo "   $0 --install"
    echo ""
    echo "Or manually with adb:"
    echo "   adb install -r \"${APK_FILE}\""
fi

echo ""
echo "=========================================="
echo "  Build Summary"
echo "=========================================="
echo "APK Location: ${APK_FILE}"
echo "Configuration: ${CONFIGURATION}"
if [ -n "$RID" ]; then
    echo "Architecture: ${RID#-r }"
else
    echo "Architecture: default (multi-ABI)"
fi
echo "=========================================="
