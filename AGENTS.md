# Version rules

- Development is beta-only. All development builds and publications must end in `b` and target the beta channel/branch. Create, publish or promote a release without the suffix only after an explicit human instruction to release that version. Never publish both channels automatically as part of development. Leave the existing release channel unchanged during beta work.

- Root `version.json` is the version source of truth. Read the current version from that file; `2.0.0` is the historical baseline.
- Bump patch (micro) for every subsequent fix or small change. Bump minor for a medium feature change and reset patch. Minor 9 rolls over to the next major, minor 0, patch 0. Major changes, including incompatible protocols or saved-data formats, reset minor and patch.
- Use `scripts/bump-version.ps1`; preserve the beta suffix unless intentionally choosing `-Channel release` or `-Channel beta`. Avoid duplicate bumps for one coordinated change already assigned a version.
- Release versions have no suffix; beta versions end in `b`. CI maps `main` to release and `beta` to beta. Never publish changed source under an already reserved version.
- Builds may use `-Version` (PowerShell) or `KIBERONE_VERSION` (Bash) to package a release and beta from the same source without changing `version.json`. Every publish and signer build must receive `KiberoneVersion`.
- Student and Tutor require separate product-bound signed single-file updates. Keep private signing keys outside the repository.
- Read `docs/versioning.md` before changing version, build, signer, or publication behavior. Do not run destructive release CI against a developer checkout.
