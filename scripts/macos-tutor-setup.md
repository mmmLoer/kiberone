# macOS Tutor setup (after a reinstall)

Restore the Mac that runs **KIBERone Tutor** on Apple Silicon. Student PCs stay Windows. Do not commit secrets, `.DS_Store`, or `dist/`.

## Clone

Remote: `https://github.com/mmmLoer/kiberone.git` (`origin` on `main`).

```bash
git clone https://github.com/mmmLoer/kiberone.git
cd kiberone
git pull
```

`dist/` is gitignored. After every clone or `git pull`, rebuild the `.app` (steps below).

## .NET 8 SDK

The previous machine used a user-local SDK at `$HOME/.dotnet`. A macOS reinstall **deletes that directory**. Reinstall:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet"
```

Or install the .NET 8 SDK from Microsoft. Then in `~/.zshrc`:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
```

Open a new terminal and check `dotnet --list-sdks` (need 8.x).

Repo-local CLI/cache dirs (gitignored; created on first restore):

```bash
cd /path/to/kiberone
export DOTNET_CLI_HOME="$PWD/.dotnet-home"
export NUGET_PACKAGES="$PWD/.nuget/packages"
```

`scripts/run-tutor.ps1`, `scripts/publish-tutor.ps1`, and `scripts/build-installers.sh` set `DOTNET_CLI_HOME` / `NUGET_PACKAGES` the same way. Plain `dotnet` from zsh does not, so export them before build/test.

## Tests

```bash
dotnet test tests/Kiberone.Tests/Kiberone.Tests.csproj
```

## Build Tutor `.app` (osx-arm64)

Version string: `BuildInfo.Version` in `src/Kiberone.Core/ClassroomNetwork.cs`.

```bash
VERSION="$(python3 - <<'PY'
import re, pathlib
text = pathlib.Path("src/Kiberone.Core/ClassroomNetwork.cs").read_text(encoding="utf-8")
m = re.search(r'public const string Version = "([^"]+)"', text)
print(m.group(1) if m else "0.0.0")
PY
)"

dotnet publish src/Kiberone.Tutor/Kiberone.Tutor.csproj \
  -c Release \
  -r osx-arm64 \
  --self-contained true \
  -o dist/Tutor-osx-arm64/publish \
  -p:PublishSingleFile=false

APP="dist/KIBERone Tutor.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp -a dist/Tutor-osx-arm64/publish/. "$APP/Contents/MacOS/"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key>
  <string>Kiberone.Tutor</string>
  <key>CFBundleIdentifier</key>
  <string>com.kiberone.tutor</string>
  <key>CFBundleName</key>
  <string>KIBERone Tutor</string>
  <key>CFBundleDisplayName</key>
  <string>KIBERone Tutor</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${VERSION}</string>
  <key>CFBundleVersion</key>
  <string>${VERSION}</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
EOF

codesign --force --deep --sign - "$APP"
open "$APP"
```

LAN: HTTP **8765**, discovery UDP **8766**. If 8765 is already in use, another Tutor process is still running.

Do not commit `dist/` (see `.gitignore`).

## Student (Windows) and Linux hub

Student is a Windows app (`win-x64`). Do not expect it to run on this Mac.

Linux hub (if still in deploy docs): `http://193.235.147.228:8787`. CI on push to `main` is documented in `scripts/linux-ci-release.md`. Windows installer publish from Linux: `scripts/linux-windows-build.md` and `scripts/build-installers.sh`. Location passwords and hub secrets are **not** in git (`deploy/location-secrets.json`, `deploy/location-passwords.txt` are ignored). Restore those from the VPS / password manager, not from this repo.

## What not to commit

- `.DS_Store`
- `dist/`, `bin/`, `obj/`
- `.dotnet-home/`, `.nuget/`
- `deploy/location-secrets.json`, `deploy/location-passwords.txt`
- hub/SSH passwords
