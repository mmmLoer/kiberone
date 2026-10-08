#!/usr/bin/env bash
# Server job: pull main/beta, rebuild win-x64 clients, publish both product updates.
#
# Env:
#   KIBERONE_REPO        – clone path (default: script's repo root)
#   KIBERONE_HUB_DATA    – Hub data directory with updates/ (required to publish)
#   KIBERONE_GIT_BRANCH  – branch to track (default: main)
#   KIBERONE_SKIP_BUILD_IF_UNCHANGED=1 – skip only the last successfully released SHA
#   KIBERONE_RELEASE_STATE_DIR – persistent state outside the checkout (optional)
#
# Triggered by:
#   - systemd timer kiberone-release.timer (backup every 15 min)
#   - GitHub push webhook → Hub POST /api/hooks/github → systemctl start kiberone-release.service
#
# Cron example (every 15 min):
#   */15 * * * * /opt/kiberone/scripts/ci-pull-and-release.sh >> /var/log/kiberone-release.log 2>&1
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REPO="${KIBERONE_REPO:-$ROOT}"
BRANCH="${KIBERONE_GIT_BRANCH:-main}"
HUB_DATA="${KIBERONE_HUB_DATA:-}"
REPO="$(cd "$REPO" && pwd)"
STATE_DIR="${KIBERONE_RELEASE_STATE_DIR:-${HUB_DATA:-${REPO}.release-state}/release-state}"
mkdir -p "$STATE_DIR"
STATE_DIR="$(cd "$STATE_DIR" && pwd)"
case "$STATE_DIR/" in
  "$REPO/"*) echo "Release state must be outside the checkout." >&2; exit 1 ;;
esac
# Serialize pulls, builds and publication; state must survive git clean/reset.
exec 9>"$STATE_DIR/release.lock"
flock -n 9 || { echo "Release already running."; exit 0; }
case "$BRANCH" in
  main) CHANNEL=release ;;
  beta) CHANNEL=beta ;;
  *) echo "Release branch must be main or beta." >&2; exit 1 ;;
esac
LAST_SUCCESS="$STATE_DIR/lastsuccess-$CHANNEL.sha"

cd "$REPO"

echo "[$(date -u +%Y-%m-%dT%H:%M:%SZ)] pull ${BRANCH} in ${REPO}"
# Discard local build outputs so pull never fails on updates/student_manifest.json etc.
git fetch origin "$BRANCH"
git checkout "$BRANCH"
git reset --hard "origin/${BRANCH}"
git clean -fd
AFTER="$(git rev-parse HEAD)"

# Validate version/channel and reject a reused version before an expensive build.
VERSION="$(python3 - "$REPO" "$HUB_DATA" "$CHANNEL" "$AFTER" <<'PY'
import json
import os
from pathlib import Path
import re
import sys

repo, hub, channel, sha = sys.argv[1:]
version = os.environ.get("KIBERONE_VERSION") or json.loads((Path(repo) / "version.json").read_text(encoding="utf-8-sig"))["version"]
if not isinstance(version, str) or not re.fullmatch(r"(0|[1-9][0-9]*)\.[0-9]\.(0|[1-9][0-9]*)(b)?", version):
    raise ValueError("Invalid version.json: expected major.minor.patch, minor 0-9, optional b")
if ("beta" if version.endswith("b") else "release") != channel:
    raise ValueError("main requires a release version; beta requires suffix b")
if hub:
    record = Path(hub) / "updates" / channel / "versions" / f"{version}.json"
    if record.exists() and json.loads(record.read_text())["sha"] != sha:
        raise ValueError(f"Version {version} already belongs to another SHA; bump version.json")
print(version)
PY
)"

if [[ "${KIBERONE_SKIP_BUILD_IF_UNCHANGED:-0}" == "1" && -f "$LAST_SUCCESS" && "$(cat "$LAST_SUCCESS")" == "$AFTER $VERSION" ]]; then
  echo "Already released successfully (${AFTER}). Skip build."
  exit 0
fi

chmod +x "$REPO/scripts/build-installers.sh"
KIBERONE_VERSION="$VERSION" "$REPO/scripts/build-installers.sh"

if [[ -z "$HUB_DATA" ]]; then
  echo "KIBERONE_HUB_DATA not set — artifacts left in ${REPO}/updates (Tutor can still use local updates/)."
  exit 0
fi

mkdir -p "$HUB_DATA/updates"
# Publish immutable content-addressed binaries first, then atomically switch each manifest.
# Filename is not part of the product-bound signed payload (app, version, size, SHA-256).
python3 - "$REPO" "$HUB_DATA" "$CHANNEL" "$VERSION" "$AFTER" <<'PY'
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile

repo, hub = map(Path, sys.argv[1:3])
channel, version, sha = sys.argv[3:]
updates = hub / "updates" / channel
local_updates = repo / "updates" / channel
record_path = updates / "versions" / f"{version}.json"
if record_path.exists() and json.loads(record_path.read_text())["sha"] != sha:
    raise RuntimeError(f"Version {version} already published from another SHA")

def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()

def publish_immutable(source, destination, expected_hash):
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".release-", dir=destination.parent)
    try:
        with os.fdopen(fd, "wb") as output, source.open("rb") as input:
            shutil.copyfileobj(input, output)
            output.flush()
            os.fsync(output.fileno())
        if digest(Path(temporary)) != expected_hash:
            raise RuntimeError(f"Artifact changed while staging: {source}")
        os.chmod(temporary, 0o644)
        try:
            os.link(temporary, destination)
        except FileExistsError:
            if digest(destination) != expected_hash:
                raise RuntimeError(f"Immutable artifact mismatch: {destination}")
    finally:
        os.unlink(temporary)

def stage_json(destination, value):
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".manifest-", dir=destination.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as output:
            json.dump(value, output, indent=2)
            output.flush()
            os.fsync(output.fileno())
        os.chmod(temporary, 0o644)
        return Path(temporary)
    except BaseException:
        os.unlink(temporary)
        raise

# Check both products completely before publishing either manifest.
products = []
for app, product in (("student", "Student"), ("tutor", "Tutor")):
    manifest = json.loads((local_updates / f"{app}_manifest.json").read_text())
    source = local_updates / f"KIBERone{product}.exe"
    hash_value = digest(source)
    if (manifest["version"] != version or source.stat().st_size != manifest["size"]
            or hash_value != manifest["sha256"].lower() or not manifest.get("signature")):
        raise RuntimeError(f"{product} artifact does not match signed version/manifest")
    manifest["filename"] = f"KIBERone{product}-{hash_value}.exe"
    products.append((app, source, hash_value, manifest))

# Published payloads are immutable even when a rebuild uses the same source SHA.
# Check archives as well as current manifests and legacy hash pins.
for app, source, hash_value, manifest in products:
    for path in (updates / f"{app}_manifest.json", updates / "versions" / f"{version}-{app}.json",
                 updates / ".versions" / app / f"{version}.json"):
        if path.exists():
            published = json.loads(path.read_text())
            if published["version"] == version and published["sha256"].lower() != hash_value:
                raise RuntimeError(f"Published {app} {version} has different bytes; patch bump required")

record = {"version": version, "channel": channel, "sha": sha}
# Reserve the version before any public artifact; partial publication still owns its version.
# Same-SHA retries may change bytes only before the product's first manifest/archive publication.
temporary = stage_json(record_path, record)
try:
    try:
        os.link(temporary, record_path)
    except FileExistsError:
        if json.loads(record_path.read_text()) != record:
            raise RuntimeError(f"Version {version} belongs to another release")
finally:
    temporary.unlink()

for app, source, hash_value, manifest in products:
    publish_immutable(source, updates / manifest["filename"], hash_value)

# Only copy installers from this version, never stale artifacts from a previous build.
for extension in ("zip", "exe"):
    for installer in sorted((repo / "dist/installers").glob(f"KIBERone*-Setup-{version}-win-x64.{extension}")):
        installer_hash = digest(installer)
        name = installer.name.removesuffix(f"-win-x64.{extension}") + f"-{installer_hash}-win-x64.{extension}"
        publish_immutable(installer, hub / "installers" / channel / name, installer_hash)

# Archive both full signed manifests before switching either current manifest.
# StoreOpenAppUpdate reads this exact path for a client pinned to an older version.
archived_products = []
for app, source, hash_value, manifest in products:
    archive_path = updates / ".versions" / app / f"{version}.json"
    temporary = stage_json(archive_path, manifest)
    try:
        try:
            os.link(temporary, archive_path)
        except FileExistsError:
            archived = json.loads(archive_path.read_text())
            if (archived["version"] != version or archived["sha256"].lower() != hash_value
                    or archived["size"] != manifest["size"] or archived["filename"] != manifest["filename"]
                    or not archived.get("signature")):
                raise RuntimeError(f"Archived {app} {version} is immutable; patch bump required")
            # Signing uses randomized RSA-PSS; retain the first full signed manifest on retry.
            manifest = archived
    finally:
        temporary.unlink()
    archived_products.append((app, source, hash_value, manifest))

for app, source, hash_value, manifest in archived_products:
    destination = updates / f"{app}_manifest.json"
    temporary = stage_json(destination, manifest)
    try:
        os.replace(temporary, destination)
    finally:
        if temporary.exists():
            temporary.unlink()
    # Pin after manifest publication so failures before publication can rebuild freely.
    # The active manifest above closes the interruption window before this pin exists.
    pin_path = updates / "versions" / f"{version}-{app}.json"
    pin = {"version": version, "app": app, "sha256": hash_value, "size": manifest["size"]}
    temporary = stage_json(pin_path, pin)
    try:
        try:
            os.link(temporary, pin_path)
        except FileExistsError:
            if json.loads(pin_path.read_text()) != pin:
                raise RuntimeError(f"Published {app} {version} is immutable; patch bump required")
    finally:
        temporary.unlink()
PY

# A failed build or publication leaves the old success SHA, so unchanged HEAD retries.
SUCCESS_TMP="$(mktemp "$STATE_DIR/.lastsuccess.XXXXXX")"
printf '%s %s\n' "$AFTER" "$VERSION" > "$SUCCESS_TMP"
mv -f "$SUCCESS_TMP" "$LAST_SUCCESS"

echo "Published Student and Tutor ${VERSION} to ${HUB_DATA}/updates/${CHANNEL}"
cat "$HUB_DATA/updates/$CHANNEL/student_manifest.json" "$HUB_DATA/updates/$CHANNEL/tutor_manifest.json"
