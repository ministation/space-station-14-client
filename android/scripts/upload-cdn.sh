#!/usr/bin/env bash
# Upload the signed APK for one version to cdn.ministation.ru.
# Env overrides: CDN_HOST, CDN_PORT, CDN_USER, CDN_PATH, APK, VERSION
set -euo pipefail

cd "$(dirname "$0")/../.."

VERSION="${VERSION:-$(grep -oP '(?<=<ApplicationDisplayVersion>)[^<]+' android/host/Content.Android.csproj | head -1)}"
APK="${APK:-android/host/bin/Release/net10.0-android/ru.ministation.client-Signed.apk}"
CDN_HOST="${CDN_HOST:-91.214.69.111}"
CDN_PORT="${CDN_PORT:-2210}"
CDN_USER="${CDN_USER:-root}"
CDN_PATH="${CDN_PATH:-/var/www}"
SSH_KEY="${SSH_KEY:-$HOME/.ssh/tunnel_ed25519}"
SSH_OPTS=(-o ProxyJump=none -o StrictHostKeyChecking=accept-new)
[ -f "$SSH_KEY" ] && SSH_OPTS+=(-i "$SSH_KEY")

test -f "$APK"
REMOTE_DIR="${CDN_PATH%/}/android/$VERSION"
LATEST_DIR="${CDN_PATH%/}/android/latest"
echo "Uploading $APK to $CDN_USER@$CDN_HOST:$REMOTE_DIR as version $VERSION"
ssh "${SSH_OPTS[@]}" -p "$CDN_PORT" "$CDN_USER@$CDN_HOST" "mkdir -p '$REMOTE_DIR' '$LATEST_DIR'"
scp "${SSH_OPTS[@]}" -P "$CDN_PORT" "$APK" "$CDN_USER@$CDN_HOST:$REMOTE_DIR/ru.ministation.client-Signed.apk"
scp "${SSH_OPTS[@]}" -P "$CDN_PORT" "$APK" "$CDN_USER@$CDN_HOST:$LATEST_DIR/ru.ministation.client-Signed.apk"
curl -fsI "https://cdn.ministation.ru/android/$VERSION/ru.ministation.client-Signed.apk"
curl -fsI "https://cdn.ministation.ru/android/latest/ru.ministation.client-Signed.apk"
echo "OK: https://cdn.ministation.ru/android/$VERSION/ru.ministation.client-Signed.apk"
