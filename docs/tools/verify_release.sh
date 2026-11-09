#!/usr/bin/env bash
# Проверка выпущенного APK: локальная сборка, README, CDN и GitHub должны совпадать.
# Запуск: docs/tools/verify_release.sh [--github]
# Код возврата 0 значит всё сошлось.
set -euo pipefail

cd "$(dirname "$0")/../.."
REPO="ministation/space-station-14-client"
APK="android/host/bin/Release/net10.0-android/ru.ministation.client-Signed.apk"
SIGN_CERT="c5c81ad38d37a3dd772e416cb2132f75b52444bb087e038c631ad5767df58d42"
BT=$(ls -d ${ANDROID_HOME:-/home/dsh/android-sdk}/build-tools/* 2>/dev/null | tail -1 || true)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

fail=0
check() { # name expected actual
    if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: ожидалось $2, получено $3"; fail=1; fi
}

VERSION=$(sed -n 's:.*<ApplicationDisplayVersion>\(.*\)</ApplicationDisplayVersion>.*:\1:p' android/host/Content.Android.csproj)
CODE=$(sed -n 's:.*<ApplicationVersion>\(.*\)</ApplicationVersion>.*:\1:p' android/host/Content.Android.csproj)
[ -f "$APK" ] || { echo "нет локальной сборки: $APK"; exit 1; }
LOCAL=$(sha256sum "$APK" | cut -d' ' -f1)
echo "версия $VERSION (code $CODE), локальный sha256 $LOCAL"

check "README sha256" "$LOCAL" "$(grep -oE '[a-f0-9]{64}' README.md | head -1)"
check "CDN latest"    "$LOCAL" "$(curl -s --max-time 180 https://cdn.ministation.ru/android/latest/ru.ministation.client-Signed.apk | sha256sum | cut -d' ' -f1)"
check "CDN $VERSION"  "$LOCAL" "$(curl -s --max-time 180 "https://cdn.ministation.ru/android/$VERSION/ru.ministation.client-Signed.apk" | sha256sum | cut -d' ' -f1)"

if [ -n "$BT" ] && [ -x "$BT/apksigner" ]; then
    check "подпись APK" "$SIGN_CERT" "$("$BT/apksigner" verify --print-certs "$APK" 2>/dev/null | sed -n 's/.*SHA-256 digest: //p' | head -1)"
else
    echo "skip подпись APK: нет build-tools"
fi

if [ "${1:-}" = "--github" ]; then
    TMPAPK="$TMP/release.apk"
    curl -sL --max-time 600 -o "$TMPAPK" "https://github.com/$REPO/releases/download/android-v$VERSION/ru.ministation.client-Signed.apk"
    check "GitHub asset" "$LOCAL" "$(sha256sum "$TMPAPK" | cut -d' ' -f1)"
fi

python3 docs/tools/pad_sim.py > "$TMP/pad.txt" || { echo "FAIL раскладка панели"; tail -3 "$TMP/pad.txt"; fail=1; }
[ "$fail" -eq 0 ] && echo "все проверки прошли" || echo "есть расхождения"
exit "$fail"
