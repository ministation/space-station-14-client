#!/usr/bin/env bash
# Sign the release APK with the stable Mini Station certificate (v1 + v2 + v3).
# Keystore and passwords are part of the repo on purpose: the launcher is sideloaded
# and every build must keep the same signature so updates install over the old app.
set -euo pipefail

APK="${1:-bin/Release/net10.0-android/ru.ministation.client-Signed.apk}"
KEYSTORE="${KEYSTORE:-keystore/release.keystore}"
ALIAS="${ALIAS:-ministation}"
STOREPASS="${STOREPASS:-ministation}"

BT="$(ls -d "${ANDROID_HOME:-/home/dsh/android-sdk}"/build-tools/* | sort -V | tail -1)"
APKSIGNER="$BT/apksigner"

"$APKSIGNER" sign \
  --ks "$KEYSTORE" --ks-key-alias "$ALIAS" \
  --ks-pass "pass:$STOREPASS" --key-pass "pass:$STOREPASS" \
  --v1-signing-enabled true --v2-signing-enabled true --v3-signing-enabled true \
  "$APK"

"$APKSIGNER" verify -v "$APK"
