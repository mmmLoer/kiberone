# Versions and release channels

Development is beta-only. The version bump helper defaults to beta. Publish or promote a suffix-free release only when the user explicitly requests that release; do not publish release and beta together during development. Existing release artifacts remain unchanged. The next development version is 2.1.2b.

The historical baseline is `2.0.0`. The update-channel feature was released as `2.1.0`; the heartbeat verification performance fix is `2.1.1`, stored in root `version.json`. Read the file for the current version rather than copying a version constant into code or scripts.

This is the project's requested version convention. Standard [SemVer](https://semver.org/) uses major for incompatible changes, minor for compatible features, patch for compatible fixes; minor may exceed 9 and prereleases normally use `-beta`. Here the requested `b` suffix and minor rollover are deliberate. Incompatible client/server or saved-data changes must also count as major changes.

Every subsequent change requires a version bump. Use a patch (micro) bump for fixes and small changes. Use a minor bump for a medium feature change and reset patch to zero. When incrementing minor 9, increment major and reset minor and patch to zero: `2.9.7` → `3.0.0`. A major change resets both minor and patch. The optional suffix `b` identifies beta; no suffix identifies release.

```powershell
# Small change: 2.1.2b -> 2.1.3b
./scripts/bump-version.ps1
# Medium feature: 2.1.2b -> 2.2.0b
./scripts/bump-version.ps1 -Kind minor
# Bump and choose a channel explicitly; default selects beta; release requires an explicit user instruction.
./scripts/bump-version.ps1 -Kind patch -Channel beta
# Preview without modifying version.json.
./scripts/bump-version.ps1 -Kind minor -WhatIf
```

The helper changes only `version.json`, preserves other JSON fields, and atomically replaces the file. It never builds, commits, pushes, creates branches, publishes, or edits a signing key. Do not bump again solely for packaging a coordinated change that already has its assigned version.

## Build overrides

The build scripts read `version.json` by default. An override selects the build version without editing that file:

```powershell
./scripts/build-installers.ps1 -Version 2.1.1
./scripts/build-installers.ps1 -Version 2.1.1b
```

```bash
KIBERONE_VERSION=2.1.1 ./scripts/build-installers.sh
KIBERONE_VERSION=2.1.1b ./scripts/build-installers.sh
```

Every publish and signer build receives `-p:KiberoneVersion=<selected version>`. Shared build properties produce a numeric assembly version and `AssemblyMetadata("KiberoneVersion", ...)`; `BuildInfo.Version` reads that metadata, including beta suffix. Inno receives the same selected version through compiler macros and rejects invocation without it.

Both Student and Tutor updates must be self-contained single-file artifacts. Tutor installer publishes must also be single-file, with native libraries included for extraction and no stale `Kiberone.Tutor.dll`; each build clears only the verified Tutor publish directories inside the workspace. Student installers retain their folder publish and explicit native bridge DLL layout. Folder-publish apphosts are unsuitable for replacing one executable. The signer receives `<exe> <version> <manifest> student|tutor`; signatures bind the app, version, size, and SHA-256. Set `KIBERONE_UPDATE_SIGNING_KEY_PATH` to the private key outside the repository. Never commit that key.

## Publication

`main` publishes only release versions; `beta` publishes only versions ending in `b`. CI accepts the same `KIBERONE_VERSION` override and validates its channel before building. Branch creation is separate from these scripts.

Build outputs contain both executables and manifests under `updates/release/` or `updates/beta/`. CI publishes hash-named immutable executables, then archives both full signed manifests at `updates/<channel>/.versions/student/<version>.json` and `.versions/tutor/<version>.json` before atomically replacing either app's current manifest. Archives retain the signature, version, size, SHA-256, hashed filename, and all other manifest fields. `StoreOpenAppUpdate` uses these exact archive paths to let clients pinned to an older version finish downloading after the current manifest advances. Same-payload retries preserve the first archive byte-for-byte, including its signature. Installers are version-filtered, hash-named, and placed in `installers/<channel>/`. No automatic cleanup removes older binaries or manifest archives.

`updates/<channel>/versions/<version>.json` permanently associates a version with its source SHA. CI rejects a different SHA using that version before building. A failed partial publication still owns the version. At the same SHA, rebuilt payloads may change only before an app's first manifest or archive publication. CI checks full signed archives, current manifests, and legacy `<version>-<app>.json` hash pins to reject changed bytes under a published version, requiring a patch bump even if source SHA is unchanged. SHA records and hash pins are CI bookkeeping; they do not substitute for the `.versions/<app>/<version>.json` signed manifests consumed by the Store. The two current app manifests switch independently after both binaries and full archives have been published, so an interruption may briefly leave current versions different; retries with the same bytes complete publication.

CI serializes releases with a persistent lock outside the checkout. A channel-specific last-success marker records SHA and version only after successful publication. Failed unchanged releases retry; changing an override at the same SHA also triggers a build. Without Hub data configured, builds leave local artifacts and do not record publication success.

## Deployment templates and rollout

`deploy/kiberone-release.service` tracks `main`. `deploy/kiberone-release-beta.service` tracks `beta` in the same `/opt/kiberone` checkout and publishes both beta products. The beta template intentionally has no `KIBERONE_VERSION` override: commit the next beta version, for example `2.1.1b`, in the beta branch's `version.json`. A beta branch still containing release version `2.1.0` must fail CI's channel check until that source change is committed and pushed. Local `2.1.0b` packaging overrides do not change this deployment rule.

Both services must share `/var/lib/kiberone-hub/release-state` (the main template's default and the beta template's explicit setting) and `/var/lib/kiberone-hub`. The CI script acquires the shared lock before fetching, checking out, resetting, cleaning, building, or publishing. A concurrent invocation exits without touching the checkout; the timers retry later. If deployments override repository, Hub, or state paths, apply matching paths to both services. Configure the same external private signing-key path for both services through deployment-managed service configuration.

`deploy/kiberone-release.timer` remains the main backup timer. `deploy/kiberone-release-beta.timer` adds the beta backup every 15 minutes with an offset boot delay. The Hub template configures `KIBERONE_RELEASE_UNIT=kiberone-release.service` and `KIBERONE_BETA_RELEASE_UNIT=kiberone-release-beta.service`. The parent's `SelectReleaseUnit` webhook routing enables beta only when the beta unit variable is configured; it maps allowed `refs/heads/main` and `refs/heads/beta` to their respective units. Both use the same GitHub signature secret configured outside the repository through `webhook.env`. Reject other refs rather than accepting a unit name from webhook input. The parent owns the routing code and its tests.

These files are templates only. Do not install, enable, or start release CI until the repository changes are committed and pushed, both remote branches contain the matching version/channel and updated build/signing code, and the Hub webhook routing is deployed. The parent owns deployment and branch creation; adding these files does not activate anything on the server. Install the beta service and timer together during that later deployment, then enable the timers for automatic recovery of failed or lock-skipped runs.

Bootstrap from the pushed code before activating CI: the deployment operator must prepare the dedicated `/opt/kiberone` release checkout with the new script and deploy the matching Hub binary and service templates. Do not use an old running CI script to bootstrap the new release logic, and do not point either unit at the server's old checkout containing uncommitted work: release CI intentionally resets and cleans its dedicated checkout. Preserve that work separately through the parent's deployment process. After the pushed branch sources and deployed webhook/templates match, configure signing and webhook secrets, install both service/timer pairs, and enable automatic triggers. No bootstrap or server activation is performed by these source changes.

## Source and fixture checks

Run `python scripts/test-release-pipeline.py` for publication regression fixtures, version override/property checks, and (when Windows PowerShell is available) bump-helper and scoped Tutor cleanup checks. The fixtures never run release CI, builds, installers, services, or VPN actions, and never modify the repository's `version.json`. Signing verification and real artifact builds are separate checks.

Use `bash -n scripts/build-installers.sh` and `bash -n scripts/ci-pull-and-release.sh` to check shell syntax without executing either script.
