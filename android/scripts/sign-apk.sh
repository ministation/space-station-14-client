#!/usr/bin/env bash
# Sign the release APK with the stable Mini Station certificate (v1 + v2 + v3).
# Keystore and passwords are part of the repo on purpose: the launcher is sideloaded
# and every build must keep the same signature so updates install over the old app.
set -euo pipefail

HOST="$(cd "$(dirname "${BASH_SOURCE[0]}")/../host" && pwd)"
APK="${1:-$HOST/bin/Release/net10.0-android/ru.ministation.client-Signed.apk}"
# Relative to the host project, the same as the build output.
KEYSTORE="${KEYSTORE:-$HOST/keystore/release.keystore}"
ALIAS="${ALIAS:-ministation}"
STOREPASS="${STOREPASS:-ministation}"

BT="$(ls -d "${ANDROID_HOME:-/home/dsh/android-sdk}"/build-tools/* | sort -V | tail -1)"
APKSIGNER="$BT/apksigner"

"$APKSIGNER" sign \
  --ks "$KEYSTORE" --ks-key-alias "$ALIAS" \
  --ks-pass "pass:$STOREPASS" --key-pass "pass:$STOREPASS" \
  --min-sdk-version 23 \
  --v1-signing-enabled true --v2-signing-enabled true --v3-signing-enabled true \
  "$APK"

# --min-sdk-version is only for the v1 JAR signature: apksigner skips v1 when the
# manifest says SDK 26 and up. Some sideload installers only check the JAR
# signature and then fail with "package has no certificates".
"$APKSIGNER" verify --min-sdk-version 23 --verbose "$APK" | head -5
