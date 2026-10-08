# Tutor self-update and Student relay

Tutor checks its own Hub update in the background after local initialization. Settings offers **Проверить обновления Tutor** and **Установить и перезапустить Tutor**. A check never downloads or installs the Tutor package. The installation button explicitly authorizes download, Windows administrator consent when necessary, shutdown and restart. Cancelled consent or preparation failure leaves Tutor open.

The channel comes from `BuildInfo.Channel` / the running build version: `2.1.0` uses release and `2.1.0b` uses beta. The saved legacy `UseTestStudentUpdates` value is retained only for settings compatibility; it no longer chooses any package. Student relay fetches both release and beta independently, validates each signed Student manifest, and imports it through `AssetDistributionService.ImportStudentRelease`; a failure in one channel does not skip the other. The relay checks the local package using `GetStudentRelease(channel)`. Production package downloads use the expected-manifest Hub overload, pinning version and SHA-256 rather than following a moving latest alias.

Tutor accepts signed standalone EXE artifacts, using the Tutor signature domain through `StudentUpdateSignature.VerifyApp("tutor", ...)`, newer same-channel versions, exact size and SHA-256. Student signatures cannot authorize Tutor packages. The original Student updater and its SYSTEM/service bridge are unchanged; Tutor installation does not use a VPN host or service.

## Bootstrap compatibility

The old installed **0.10.41 multi-file Tutor cannot bootstrap this single-file update flow**. Install the **2.1 single-file Tutor installer once** on those machines. Thereafter self-update can replace the standalone installed EXE, including Program Files installations. The running bundle is identified by an empty `AppUpdateInstaller` assembly `Location`, so stale DLLs left by the old installer do not block updates. A genuinely multi-file running build is rejected with an installer instruction. Both `Kiberone.Tutor.exe` and `KIBERoneTutor.exe` are supported.

## Apply and recovery

Packages and a copied version of the currently running helper EXE are staged beneath `%LOCALAPPDATA%\KIBERone Classroom\tutor-updates` in a unique directory. The copied helper runs an early startup mode before the desktop mutex, Avalonia or server initialization. It checks that its own hash matches the target Tutor, verifies the signed package again while the source is locked, prepares the new file in the target directory, and reports readiness before the desktop is asked to exit. The default Inno install uses `PrivilegesRequired=lowest` and `{localappdata}\Programs\KIBERone\Tutor`, so normal installed and portable updates need no UAC. Only an unwritable target (for example a manually chosen protected Program Files installation) launches the copied helper with `runas` after the explicit update action. The status mentions administrator consent only for that protected case; Tutor has no system/service bridge.

The apply helper waits for the exact original process (PID and start timestamp), then atomically replaces the EXE with `File.Replace` and keeps `<Tutor.exe>.previous` as the rollback copy. Preparation/cancellation leaves the original EXE untouched. Replacement failure leaves the original EXE in place. A separate helper launched by the original, unelevated Tutor waits for the result and restarts the same target, including the old target after apply failure, with bounded retries. It never starts Tutor elevated solely because the apply operation was elevated. The staging directory retains `install.json.result` and `install.json.restart` for diagnosis. No deployment, firewall change, VPN process, or silent elevation is involved.

## Isolated tests

`TutorUpdateTests` exercises shared signature validation, wrong app/channel/version, tampering, cancellation, writable file replacement and rollback backup, a locked-target failure, channel selection and both relay requests.

The native child test uses **the current updater code built as an old-version single-file Tutor**, for example 2.0.0, plus a separately signed/downloaded newer Tutor, for example 2.1.0. Do not pass a legacy production Tutor that predates helper modes: that binary would enter its normal desktop startup.

After parent builds finish (no concurrent builds), run:

```powershell
dotnet test tests/Kiberone.Tests/Kiberone.Tests.csproj --no-build --filter FullyQualifiedName~TutorUpdateTests
dotnet run --project tests/Kiberone.UpdateProbe/Kiberone.UpdateProbe.csproj -- <old-single-file-Tutor.exe> <downloaded-tutor-manifest.json> <downloaded-new-Tutor.exe> 2.0.0
```

The probe validates/stages the downloaded artifact with `AppUpdateInstaller.StageAsync`, copies only the old standalone EXE into an isolated temp portable directory, launches the copied EXE with `--apply-tutor-update job.json` (`ParentPid=int.MaxValue`, no restart helper), and verifies both installed SHA-256 and rollback backup. It does not execute the new Tutor, start a desktop, open a classroom server, request UAC or launch VPN. All evidence remains in the printed temp directory. The public `ApplyVerifiedPackageAsync` API supports headless validation/replacement tests without a GUI or elevation; it does not alter Student's installation route.
