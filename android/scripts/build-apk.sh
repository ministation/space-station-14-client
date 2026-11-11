#!/usr/bin/env bash
# Собирает APK Android-клиента Mini Station (настоящий движок RobustToolbox + контент Mini Station).
#
# Требования: .NET 10 SDK + workload android, Android SDK (platform 36, build-tools 36),
# JDK 21, собранные нативные библиотеки (android/scripts/build-native-libs.sh для arm64-v8a и x86_64).
#
# Usage: android/scripts/build-apk.sh [Debug|Release]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CONFIG="${1:-Release}"
PROJECT="$ROOT/android/host/Content.Android.csproj"
ENGINE="$ROOT/vendor/RobustToolbox"

export ANDROID_HOME="${ANDROID_HOME:-$HOME/android-sdk}"
export JAVA_HOME="${JAVA_HOME:-/usr/lib/jvm/java-21-openjdk-amd64}"

[ -d "$ENGINE" ] || { echo "engine missing: run android/scripts/fetch-engine.sh first" >&2; exit 1; }
for abi in arm64-v8a x86_64; do
    [ -d "$ROOT/android/native/$abi" ] || {
        echo "native libs missing for $abi: run android/scripts/build-native-libs.sh $abi" >&2
        exit 1
    }
    # csproj берёт нативы из android/host/lib/<abi>, это промежуточная папка сборки.
    mkdir -p "$ROOT/android/host/lib/$abi"
    cp -f "$ROOT/android/native/$abi"/*.so "$ROOT/android/host/lib/$abi/"
done

# TargetOS=Android обязателен: без него движок компилируется с FREEDESKTOP-ветками
# (fontconfig и desktop-пути) и ведёт себя неправильно на Android.
dotnet build "$PROJECT" \
    -c "$CONFIG" \
    -f net10.0-android \
    -p:TargetOS=Android \
    -p:AndroidSdkDirectory="$ANDROID_HOME" \
    -p:JavaSdkDirectory="$JAVA_HOME"

APK=$(ls -t "$ROOT/android/host/bin/$CONFIG/net10.0-android/"*-Signed.apk 2>/dev/null | head -n 1)
[ -n "$APK" ] || { echo "APK not found" >&2; exit 1; }
# Re-sign with v1 + v2 + v3: some sideload installers refuse APKs that only
# carry the v2/v3 signature and report a missing certificate.
ANDROID_HOME="$ANDROID_HOME" bash "$ROOT/android/scripts/sign-apk.sh" "$APK"

# Проверка, что в движок не уехала FREEDESKTOP-ветка.
if strings "$ROOT/android/host/bin/$CONFIG/net10.0-android/Robust.Client.dll" 2>/dev/null | grep -q SystemFontManagerFontconfig; then
    echo "ERROR: engine built with FREEDESKTOP branch, expected ANDROID" >&2
    exit 1
fi

echo "APK: $APK ($(du -h "$APK" | cut -f1))"
