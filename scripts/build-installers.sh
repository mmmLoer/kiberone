#!/usr/bin/env bash
# Linux equivalent of build-installers.ps1: publish win-x64 Student/Tutor, zip installers, write updates manifest.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd -P)"
cd "$ROOT"

if [[ ! -f "${KIBERONE_UPDATE_SIGNING_KEY_PATH:-}" ]]; then
  echo "KIBERONE_UPDATE_SIGNING_KEY_PATH must point to the private PEM key before building installers." >&2
  exit 1
fi

export EnableWindowsTargeting=true
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT/.dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$ROOT/.nuget/packages}"

VERSION="$(python3 - <<'PY'
import json, os, pathlib, re
version = os.environ.get("KIBERONE_VERSION") or json.loads(pathlib.Path("version.json").read_text(encoding="utf-8-sig"))["version"]
if not isinstance(version, str) or not re.fullmatch(r"(0|[1-9][0-9]*)\.[0-9]\.(0|[1-9][0-9]*)(b)?", version):
    raise ValueError("version.json requires major.minor.patch, minor 0-9, optional beta suffix b")
print(version)
PY
)"
CHANNEL=release
if [[ "$VERSION" == *b ]]; then CHANNEL=beta; fi
UPDATES="$ROOT/updates/$CHANNEL"

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

reset_tutor_publish_directory() {
  local name="$1" target
  case "$name" in Tutor-win-x64|Tutor-update-win-x64) ;; *) echo "Unsafe publish directory" >&2; return 1 ;; esac
  target="$ROOT/dist/$name"
  if [[ "$(realpath -m -- "$target")" != "$target" || -L "$ROOT/dist" || -L "$target" ]]; then
    echo "Publish cleanup target must be an ordinary directory inside $ROOT: $target" >&2
    return 1
  fi
  if [[ -e "$target" ]]; then
    if [[ ! -d "$target" || -n "$(find "$target" -type l -print -quit)" ]]; then
      echo "Publish cleanup refuses files or links: $target" >&2
      return 1
    fi
    rm -rf -- "$target"
  fi
}

for dll in tunnel.dll wireguard.dll; do
  if [[ ! -f "$NATIVE_DIR/$dll" ]]; then
    continue
  fi
done

dotnet publish "$ROOT/src/Kiberone.Student/Kiberone.Student.csproj" \
  -c Release -r win-x64 --self-contained true \
  -o "$ROOT/dist/Student-win-x64" \
  -p:PublishSingleFile=false \
  -p:EnableWindowsTargeting=true \
  -p:KiberoneVersion="$VERSION"

mkdir -p "$ROOT/dist/Student-win-x64/native" "$ROOT/dist/Student-win-x64/service"
if [[ -f "$NATIVE_DIR/tunnel.dll" ]]; then
  cp "$NATIVE_DIR/tunnel.dll" "$ROOT/dist/Student-win-x64/native/"
  cp "$NATIVE_DIR/tunnel.dll" "$ROOT/dist/Student-win-x64/"
fi
cp "$NATIVE_DIR/wireguard.dll" "$ROOT/dist/Student-win-x64/native/"
cp "$NATIVE_DIR/wireguard.dll" "$ROOT/dist/Student-win-x64/"
cp "$ROOT/scripts/install-student-vpn-service.ps1" "$ROOT/dist/Student-win-x64/service/"

# Single-file artifact for Tutor→Student update channel (replaces one KIBERoneStudent.exe).
echo "Publishing single-file Student update package…"
dotnet publish "$ROOT/src/Kiberone.Student/Kiberone.Student.csproj" \
  -c Release -r win-x64 --self-contained true \
  -o "$ROOT/dist/Student-update-win-x64" \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableWindowsTargeting=true \
  -p:KiberoneVersion="$VERSION"
if [[ -f "$NATIVE_DIR/tunnel.dll" ]]; then
  cp "$NATIVE_DIR/tunnel.dll" "$ROOT/dist/Student-update-win-x64/"
fi
cp "$NATIVE_DIR/wireguard.dll" "$ROOT/dist/Student-update-win-x64/"

reset_tutor_publish_directory Tutor-win-x64
dotnet publish "$ROOT/src/Kiberone.Tutor/Kiberone.Tutor.csproj" \
  -c Release -r win-x64 --self-contained true \
  -o "$ROOT/dist/Tutor-win-x64" \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:KiberoneVersion="$VERSION"

if [[ ! -f "$ROOT/dist/Tutor-win-x64/Kiberone.Tutor.exe" || -e "$ROOT/dist/Tutor-win-x64/Kiberone.Tutor.dll" ]]; then
  echo "Tutor installer publish requires a standalone EXE without Kiberone.Tutor.dll." >&2
  exit 1
fi

reset_tutor_publish_directory Tutor-update-win-x64
dotnet publish "$ROOT/src/Kiberone.Tutor/Kiberone.Tutor.csproj" \
  -c Release -r win-x64 --self-contained true \
  -o "$ROOT/dist/Tutor-update-win-x64" \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:KiberoneVersion="$VERSION"

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

mkdir -p "$UPDATES"
STUDENT_EXE_SRC="$ROOT/dist/Student-update-win-x64/Kiberone.Student.exe"
STUDENT_EXE_DST="$UPDATES/KIBERoneStudent.exe"
cp "$STUDENT_EXE_SRC" "$STUDENT_EXE_DST"
cp "$STUDENT_EXE_SRC" "$ROOT/KIBERoneStudent.exe"
TUTOR_EXE_SRC="$ROOT/dist/Tutor-update-win-x64/Kiberone.Tutor.exe"
cp "$TUTOR_EXE_SRC" "$UPDATES/KIBERoneTutor.exe"
cp "$TUTOR_EXE_SRC" "$ROOT/KIBERoneTutor.exe"

dotnet run --project "$ROOT/tools/Kiberone.UpdateSigner/Kiberone.UpdateSigner.csproj" \
  -c Release -p:KiberoneVersion="$VERSION" -- "$STUDENT_EXE_DST" "$VERSION" "$UPDATES/student_manifest.json" student
dotnet run --project "$ROOT/tools/Kiberone.UpdateSigner/Kiberone.UpdateSigner.csproj" \
  -c Release -p:KiberoneVersion="$VERSION" -- "$UPDATES/KIBERoneTutor.exe" "$VERSION" "$UPDATES/tutor_manifest.json" tutor

# Tutor serves updates from BaseDirectory/updates
mkdir -p "$ROOT/dist/Tutor-win-x64/updates/$CHANNEL"
cp "$UPDATES/"*.exe "$UPDATES/"*_manifest.json "$ROOT/dist/Tutor-win-x64/updates/$CHANNEL/"

echo ""
echo "Installers:"
ls -lh "$STUDENT_ZIP" "$TUTOR_ZIP"
echo "Update manifests: $UPDATES/{student,tutor}_manifest.json"
echo "Done."
