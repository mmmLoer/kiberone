#!/usr/bin/env bash
# Server job: pull main, rebuild win-x64 clients, publish Student update into Hub data dir.
#
# Env:
#   KIBERONE_REPO        – clone path (default: script's repo root)
#   KIBERONE_HUB_DATA    – Hub data directory with updates/ (required to publish)
#   KIBERONE_GIT_BRANCH  – branch to track (default: main)
#   KIBERONE_SKIP_BUILD_IF_UNCHANGED=1 – skip build when pull made no changes
#
# Cron example (every 15 min):
#   */15 * * * * /opt/kiberone/scripts/ci-pull-and-release.sh >> /var/log/kiberone-release.log 2>&1
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REPO="${KIBERONE_REPO:-$ROOT}"
BRANCH="${KIBERONE_GIT_BRANCH:-main}"
HUB_DATA="${KIBERONE_HUB_DATA:-}"

cd "$REPO"

echo "[$(date -u +%Y-%m-%dT%H:%M:%SZ)] pull ${BRANCH} in ${REPO}"
BEFORE="$(git rev-parse HEAD)"
git fetch origin "$BRANCH"
git checkout "$BRANCH"
git pull --ff-only origin "$BRANCH"
AFTER="$(git rev-parse HEAD)"

if [[ "${KIBERONE_SKIP_BUILD_IF_UNCHANGED:-0}" == "1" && "$BEFORE" == "$AFTER" ]]; then
  echo "No new commits (${AFTER}). Skip build."
  exit 0
fi

chmod +x "$REPO/scripts/build-installers.sh"
"$REPO/scripts/build-installers.sh"

if [[ -z "$HUB_DATA" ]]; then
  echo "KIBERONE_HUB_DATA not set — artifacts left in ${REPO}/updates (Tutor can still use local updates/)."
  exit 0
fi

mkdir -p "$HUB_DATA/updates"
cp "$REPO/updates/KIBERoneStudent.exe" "$HUB_DATA/updates/KIBERoneStudent.exe"
cp "$REPO/updates/student_manifest.json" "$HUB_DATA/updates/student_manifest.json"
# optional installers for manual download
if [[ -d "$REPO/dist/installers" ]]; then
  mkdir -p "$HUB_DATA/installers"
  cp -f "$REPO/dist/installers/"KIBERone*-Setup-*-win-x64.zip "$HUB_DATA/installers/" 2>/dev/null || true
fi

echo "Published Student update to ${HUB_DATA}/updates"
cat "$HUB_DATA/updates/student_manifest.json"
