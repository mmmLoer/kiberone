#!/usr/bin/env bash
# Linux equivalent of build-installers.ps1: publish win-x64 Student/Tutor, zip installers, write updates manifest.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

export EnableWindowsTargeting=true
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$ROOT/.nuget/packages}"

VERSION="$(python3 - <<'PY' || true
import re, pathlib
text = pathlib.Path("src/Kiberone.Core/ClassroomNetwork.cs").read_text(encoding="utf-8")
m = re.search(r'public const string Version = "([^"]+)"', text)
print(m.group(1) if m else "")
PY
)"
if [[ -z "${VERSION}" ]]; then
  VERSION="0.10.8"
fi

echo "=== KIBERone release build v${VERSION} (linux → win-x64) ==="

NATIVE_DIR="$ROOT/src/Kiberone.VpnAgent/native"
mkdir -p "$NATIVE_DIR"

ensure_wireguard_dll() {
  if [[ -f "$NATIVE_DIR/wireguard.dll" ]]; then
    return 0
  fi
  echo "Downloading wireguard.dll (amd64)…"
  local tmp
  tmp="$(mktemp -d)"
  curl -fsSL -o "$tmp/wireguard-nt.zip" https://download.wireguard.com/wireguard-nt/wireguard-nt-1.1.zip
  unzip -qo "$tmp/wireguard-nt.zip" -d "$tmp"
  cp "$tmp/wireguard-nt/bin/amd64/wireguard.dll" "$NATIVE_DIR/wireguard.dll"
  rm -rf "$tmp"
}

ensure_native_dlls() {
  ensure_wireguard_dll
  if [[ -f "$NATIVE_DIR/tunnel.dll" ]]; then
    return 0
  fi
  local fallback="$ROOT/dist/Student-win-x64/native/tunnel.dll"
  if [[ -f "$fallback" ]]; then
    cp "$fallback" "$NATIVE_DIR/tunnel.dll"
    echo "Restored tunnel.dll from previous dist build."
    return 0
  fi
  if [[ -f "${KIBERONE_NATIVE_CACHE:-/var/lib/kiberone-hub/native-cache}/tunnel.dll" ]]; then
    cp "${KIBERONE_NATIVE_CACHE:-/var/lib/kiberone-hub/native-cache}/tunnel.dll" "$NATIVE_DIR/tunnel.dll"
    echo "Restored tunnel.dll from KIBERONE_NATIVE_CACHE."
    return 0
  fi
  if [[ "${KIBERONE_ALLOW_MISSING_TUNNEL:-0}" == "1" ]]; then
    echo "WARNING: tunnel.dll missing — Student update will build without VPN native support." >&2
    return 0
  fi
  echo "Missing tunnel.dll. Place it in src/Kiberone.VpnAgent/native/ or set KIBERONE_ALLOW_MISSING_TUNNEL=1" >&2
  exit 1
}

ensure_native_dlls

for dll in tunnel.dll wireguard.dll; do
  if [[ ! -f "$NATIVE_DIR/$dll" ]]; then
    continue
  fi
done

dotnet publish "$ROOT/src/Kiberone.Student/Kiberone.Student.csproj" \
  -c Release -r win-x64 --self-contained true \
  -o "$ROOT/dist/Student-win-x64" \
  -p:PublishSingleFile=false \
  -p:EnableWindowsTargeting=true

mkdir -p "$ROOT/dist/Student-win-x64/native" "$ROOT/dist/Student-win-x64/service"
if [[ -f "$NATIVE_DIR/tunnel.dll" ]]; then
  cp "$NATIVE_DIR/tunnel.dll" "$ROOT/dist/Student-win-x64/native/"
  cp "$NATIVE_DIR/tunnel.dll" "$ROOT/dist/Student-win-x64/"
fi
cp "$NATIVE_DIR/wireguard.dll" "$ROOT/dist/Student-win-x64/native/"
cp "$NATIVE_DIR/wireguard.dll" "$ROOT/dist/Student-win-x64/"
cp "$ROOT/scripts/install-student-vpn-service.ps1" "$ROOT/dist/Student-win-x64/service/"

dotnet publish "$ROOT/src/Kiberone.Tutor/Kiberone.Tutor.csproj" \
  -c Release -r win-x64 --self-contained true \
  -o "$ROOT/dist/Tutor-win-x64" \
  -p:PublishSingleFile=false

STAGE="$ROOT/dist/installers/_staging"
rm -rf "$STAGE"
mkdir -p "$STAGE/student/app" "$STAGE/student/service" "$STAGE/tutor/app" "$ROOT/dist/installers"

cp -a "$ROOT/dist/Student-win-x64/." "$STAGE/student/app/"
cp "$ROOT/install/Setup-Student.ps1" "$ROOT/install/Install-Student.cmd" \
  "$ROOT/install/Create-Student-Shortcut.ps1" "$ROOT/install/Repair-Student-Vpn.cmd" \
  "$ROOT/install/README-Student.txt" "$STAGE/student/"
cp "$ROOT/scripts/install-student-vpn-service.ps1" "$STAGE/student/service/"

cp -a "$ROOT/dist/Tutor-win-x64/." "$STAGE/tutor/app/"
cp "$ROOT/install/Setup-Tutor.ps1" "$ROOT/install/Install-Tutor.cmd" \
  "$ROOT/install/Create-Tutor-Shortcut.ps1" "$ROOT/install/README-Tutor.txt" "$STAGE/tutor/"

STUDENT_ZIP="$ROOT/dist/installers/KIBERoneStudent-Setup-${VERSION}-win-x64.zip"
TUTOR_ZIP="$ROOT/dist/installers/KIBERoneTutor-Setup-${VERSION}-win-x64.zip"
rm -f "$STUDENT_ZIP" "$TUTOR_ZIP"
(cd "$STAGE/student" && zip -qr "$STUDENT_ZIP" .)
(cd "$STAGE/tutor" && zip -qr "$TUTOR_ZIP" .)
rm -rf "$STAGE"

mkdir -p "$ROOT/updates"
STUDENT_EXE_SRC="$ROOT/dist/Student-win-x64/Kiberone.Student.exe"
STUDENT_EXE_DST="$ROOT/updates/KIBERoneStudent.exe"
cp "$STUDENT_EXE_SRC" "$STUDENT_EXE_DST"
cp "$STUDENT_EXE_SRC" "$ROOT/KIBERoneStudent.exe"
cp "$ROOT/dist/Tutor-win-x64/Kiberone.Tutor.exe" "$ROOT/KIBERoneTutor.exe"

SIZE="$(wc -c < "$STUDENT_EXE_DST" | tr -d ' ')"
if command -v sha256sum >/dev/null 2>&1; then
  HASH="$(sha256sum "$STUDENT_EXE_DST" | awk '{print $1}')"
else
  HASH="$(shasum -a 256 "$STUDENT_EXE_DST" | awk '{print $1}')"
fi
PUBLISHED_AT="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

cat > "$ROOT/updates/student_manifest.json" <<EOF
{
  "version": "${VERSION}",
  "filename": "KIBERoneStudent.exe",
  "size": ${SIZE},
  "sha256": "${HASH}",
  "published_at": "${PUBLISHED_AT}"
}
EOF

echo ""
echo "Installers:"
ls -lh "$STUDENT_ZIP" "$TUTOR_ZIP"
echo "Update manifest: $ROOT/updates/student_manifest.json"
echo "Done."
