#!/usr/bin/env bash
# Клонирует RobustToolbox v295.0.2 (версия движка Mini Station) в vendor/RobustToolbox
# и применяет Android-патч android/patches/robust-android-v295.patch.
# Скрипт идемпотентен: повторный запуск сбрасывает движок на тег и применяет патч заново.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ENGINE="${ENGINE_DIR:-$ROOT/vendor/RobustToolbox}"
PATCH="$ROOT/android/patches/robust-android-v295.patch"
REF="v295.0.2"

[ -f "$PATCH" ] || { echo "patch not found: $PATCH" >&2; exit 1; }

if [ ! -d "$ENGINE/.git" ]; then
    echo "cloning RobustToolbox $REF -> $ENGINE"
    mkdir -p "$(dirname "$ENGINE")"
    git clone --depth 1 --branch "$REF" https://github.com/space-wizards/RobustToolbox.git "$ENGINE"
fi

git -C "$ENGINE" fetch --depth 1 origin "refs/tags/$REF:refs/tags/$REF" 2>/dev/null || true
git -C "$ENGINE" checkout -q -f "$REF"
git -C "$ENGINE" clean -qfd -e bin -e obj
git -C "$ENGINE" submodule update --init --depth 1

echo "applying $PATCH"
git -C "$ENGINE" apply --3way --whitespace=nowarn "$PATCH"

echo "engine ready: $ENGINE"
