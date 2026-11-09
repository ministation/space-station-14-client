#!/usr/bin/env bash
# Собирает SDL3, FreeType, OpenAL Soft, zstd, libsodium под Android из исходников.
#
# Usage: android/scripts/build-native-libs.sh [arm64-v8a|x86_64]
# Result: android/native/<abi>/*.so
set -euo pipefail

ABI="${1:-${ANDROID_ABI:-arm64-v8a}}"
case "$ABI" in
    x86_64|arm64-v8a) ;;
    *) echo "Usage: $0 [x86_64|arm64-v8a]" >&2; exit 2 ;;
esac

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export ANDROID_HOME="${ANDROID_HOME:-$HOME/android-sdk}"
export ANDROID_NDK_HOME="${ANDROID_NDK_HOME:-$ANDROID_HOME/ndk/27.2.12479018}"
TOOLCHAIN="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake"
TARGET_DIR="$ROOT/android/native/$ABI"
BUILD_DIR="$ROOT/vendor/native-build/$ABI"
NPROC=$(nproc 2>/dev/null || echo 4)
export NDK_PLATFORM=android-21

[ -f "$TOOLCHAIN" ] || { echo "NDK toolchain not found: $TOOLCHAIN" >&2; exit 1; }
mkdir -p "$TARGET_DIR"
rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR"
cd "$BUILD_DIR"

COMMON=(
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN"
    -DANDROID_ABI="$ABI"
    -DANDROID_PLATFORM=android-21
    -DCMAKE_BUILD_TYPE=Release
)
JOBS="-j$NPROC"

echo "=== [$ABI] Building SDL3 ==="
mkdir -p sdl3 && cd sdl3
if [ ! -d .git ]; then
    git init -q
    git remote add origin https://github.com/libsdl-org/SDL.git
    git fetch --depth 1 origin f0e99e7c7f9aa90d5ce2e3b8a69f72c23faf257e
    git checkout -q FETCH_HEAD
fi
cmake -B build "${COMMON[@]}" \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DSDL_SHARED=ON -DSDL_STATIC=OFF -DSDL_ANDROID_JAR=OFF -DSDL_TEST_LIBRARY=OFF
cmake --build build $JOBS
cp "$(find build -name libSDL3.so | head -n 1)" "$TARGET_DIR/libSDL3.so"
cd ..

echo "=== [$ABI] Building FreeType ==="
if [ ! -d freetype/.git ]; then
    git clone --depth 1 --branch VER-2-13-3 https://github.com/freetype/freetype.git freetype
fi
cmake -B build-ft -S freetype "${COMMON[@]}" \
    -DBUILD_SHARED_LIBS=ON \
    -DFT_DISABLE_BZIP2=TRUE -DFT_DISABLE_PNG=TRUE \
    -DFT_DISABLE_HARFBUZZ=TRUE -DFT_DISABLE_BROTLI=TRUE \
    -DFT_REQUIRE_ZLIB=FALSE
cmake --build build-ft $JOBS
FT_LIB=$(find build-ft -name libfreetype.so | head -n 1)
cp "$FT_LIB" "$TARGET_DIR/libfreetype.so"
cp "$FT_LIB" "$TARGET_DIR/libfreetype6.so"

echo "=== [$ABI] Building OpenAL Soft ==="
if [ ! -d openal-soft/.git ]; then
    git clone --depth 1 --branch 1.24.3 https://github.com/kcat/openal-soft.git openal-soft
fi
cmake -B build-al -S openal-soft "${COMMON[@]}" \
    -DLIBTYPE=SHARED \
    -DALSOFT_EXAMPLES=OFF -DALSOFT_TESTS=OFF -DALSOFT_UTILS=OFF \
    -DALSOFT_EMBED_HRTF_DATA=YES
cmake --build build-al $JOBS
cp "$(find build-al -name libopenal.so | head -n 1)" "$TARGET_DIR/libopenal.so"

echo "=== [$ABI] Building Zstd ==="
if [ ! -d zstd/.git ]; then
    git clone --depth 1 --branch v1.5.7 https://github.com/facebook/zstd.git zstd
fi
cmake -B build-zstd -S zstd/build/cmake "${COMMON[@]}" \
    -DZSTD_BUILD_PROGRAMS=OFF -DZSTD_BUILD_STATIC=OFF -DZSTD_BUILD_SHARED=ON
cmake --build build-zstd $JOBS
cp "$(find build-zstd -name libzstd.so | head -n 1)" "$TARGET_DIR/libzstd.so"

echo "=== [$ABI] Building libsodium ==="
if [ ! -d libsodium/.git ]; then
    git clone --depth 1 --branch 1.0.20-RELEASE https://github.com/jedisct1/libsodium.git libsodium
fi
cd libsodium
./autogen.sh >/dev/null
export ANDROID_NDK_HOME
case "$ABI" in
    x86_64)     ./dist-build/android-x86_64.sh ;;
    arm64-v8a)  ./dist-build/android-armv8-a.sh ;;
esac
cp "$(find libsodium-android* -name libsodium.so | head -n 1)" "$TARGET_DIR/libsodium.so"
cd ..

echo "=== [$ABI] done ==="
ls -lh "$TARGET_DIR"