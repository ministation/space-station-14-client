#!/usr/bin/env bash
# Allow Port.Net to access Robust.Shared internals (TransformComponentState, serializer, etc.).
# vendor/ is gitignored — run after clone-robust.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
INFO="$ROOT/vendor/RobustToolbox/Robust.Shared/AssemblyInfo.cs"
if [[ ! -f "$INFO" ]]; then
  echo "missing $INFO — clone RobustToolbox first" >&2
  exit 1
fi
if grep -q 'InternalsVisibleTo("Port.Net")' "$INFO"; then
  echo "IVT Port.Net already present"
  exit 0
fi
# Insert after the Robust.Client line when present, else append.
if grep -q 'InternalsVisibleTo("Robust.Client")' "$INFO"; then
  sed -i 's/\[assembly: InternalsVisibleTo("Robust.Client")\]/[assembly: InternalsVisibleTo("Robust.Client")]\n[assembly: InternalsVisibleTo("Port.Net")]/' "$INFO"
else
  printf '\n[assembly: InternalsVisibleTo("Port.Net")]\n' >> "$INFO"
fi
echo "patched $INFO"