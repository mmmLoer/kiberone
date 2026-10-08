"""Source and fixture checks only; never runs CI, dotnet, installers, or services.

Run: python scripts/test-release-pipeline.py
"""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
CI = (ROOT / "scripts/ci-pull-and-release.sh").read_text()
PREFLIGHT, PUBLICATION = re.findall(r"<<'PY'\n(.*?)\nPY", CI, re.S)


class ReleasePipelineTests(unittest.TestCase):
    def setUp(self):
        self.sandbox = tempfile.TemporaryDirectory(prefix="kiberone-release-test-")
        self.addCleanup(self.sandbox.cleanup)
        self.repo = Path(self.sandbox.name) / "repo"
        self.hub = Path(self.sandbox.name) / "hub"
        self.repo.mkdir()
        self.version = "2.1.0"
        self.sha = "a" * 40
        self.channel = "release"
        self.env = os.environ.copy()
        self.env.pop("KIBERONE_VERSION", None)
        (self.repo / "version.json").write_text(json.dumps({"version": self.version}))
        self.write_products()

    def write_products(self, student=b"fixture-student", tutor=b"fixture-tutor"):
        output = self.repo / "updates" / self.channel
        output.mkdir(parents=True, exist_ok=True)
        for app, content in (("student", student), ("tutor", tutor)):
            product = app.title()
            (output / f"KIBERone{product}.exe").write_bytes(content)
            manifest = {
                "version": self.version, "filename": f"KIBERone{product}.exe",
                "size": len(content), "sha256": hashlib.sha256(content).hexdigest(),
                # Signature verification belongs to the product-bound signer and consumers.
                "signature": "fixture-signature",
            }
            (output / f"{app}_manifest.json").write_text(json.dumps(manifest))

    def run_code(self, code, args):
        return subprocess.run([sys.executable, "-c", code, *map(str, args)],
                              env=self.env, capture_output=True, text=True)

    def publish(self):
        return self.run_code(PUBLICATION, [self.repo, self.hub, self.channel, self.version, self.sha])

    def preflight(self):
        return self.run_code(PREFLIGHT, [self.repo, self.hub, self.channel, self.sha])

    def assert_ok(self, result):
        self.assertEqual(0, result.returncode, result.stderr)

    def manifest(self, app, channel=None):
        path = self.hub / "updates" / (channel or self.channel) / f"{app}_manifest.json"
        return json.loads(path.read_text())

    def test_both_products_publish_immutable_artifacts_and_retry(self):
        self.assert_ok(self.publish())
        for app in ("student", "tutor"):
            manifest = self.manifest(app)
            file = self.hub / "updates/release" / manifest["filename"]
            self.assertEqual(manifest["sha256"], hashlib.sha256(file.read_bytes()).hexdigest())
            self.assertIn(manifest["sha256"], manifest["filename"])
        self.assert_ok(self.publish())

    def test_changed_sha_cannot_reuse_version(self):
        self.assert_ok(self.publish())
        self.sha = "b" * 40
        self.assertNotEqual(0, self.preflight().returncode)
        self.assertNotEqual(0, self.publish().returncode)

    def test_full_signed_archives_keep_old_downloads_available(self):
        self.assert_ok(self.publish())
        originals = {}
        for app in ("student", "tutor"):
            archive = self.hub / "updates/release/.versions" / app / "2.1.0.json"
            originals[app] = archive.read_bytes()
            self.assertEqual(self.manifest(app), json.loads(originals[app]))
        self.version = "2.1.1"
        self.sha = "b" * 40
        self.write_products(student=b"next-student", tutor=b"next-tutor")
        self.assert_ok(self.publish())
        for app in ("student", "tutor"):
            archive = self.hub / "updates/release/.versions" / app / "2.1.0.json"
            self.assertEqual(originals[app], archive.read_bytes())
            old = json.loads(archive.read_text())
            file = self.hub / "updates/release" / old["filename"]
            self.assertEqual(old["sha256"], hashlib.sha256(file.read_bytes()).hexdigest())
            self.assertTrue(old["signature"])

    def test_both_archives_exist_before_either_current_manifest_switch(self):
        checked = PUBLICATION.replace(
            "os.replace(temporary, destination)",
            "assert all((updates / '.versions' / app / f'{version}.json').exists() "
            "for app in ('student', 'tutor')); os.replace(temporary, destination)",
        )
        self.assertNotEqual(checked, PUBLICATION)
        result = self.run_code(checked, [self.repo, self.hub, self.channel, self.version, self.sha])
        self.assert_ok(result)

    def test_retry_preserves_first_signed_archive_with_randomized_signature(self):
        self.assert_ok(self.publish())
        archive = self.hub / "updates/release/.versions/student/2.1.0.json"
        original = archive.read_bytes()
        local = self.repo / "updates/release/student_manifest.json"
        manifest = json.loads(local.read_text())
        manifest["signature"] = "new-randomized-signature-same-payload"
        local.write_text(json.dumps(manifest))
        self.assert_ok(self.publish())
        self.assertEqual(original, archive.read_bytes())
        self.assertEqual(json.loads(original), self.manifest("student"))

    def test_archive_without_current_manifest_still_guards_published_payload(self):
        interrupted = PUBLICATION.replace("os.replace(temporary, destination)", "raise RuntimeError('fixture interruption')")
        result = self.run_code(interrupted, [self.repo, self.hub, self.channel, self.version, self.sha])
        self.assertNotEqual(0, result.returncode)
        self.assertFalse((self.hub / "updates/release/student_manifest.json").exists())
        self.assertTrue((self.hub / "updates/release/.versions/tutor/2.1.0.json").exists())
        self.write_products(tutor=b"different-after-archive")
        self.assertNotEqual(0, self.publish().returncode)
        self.write_products()
        self.assert_ok(self.publish())

    def test_same_sha_changed_payload_requires_patch_bump(self):
        self.assert_ok(self.publish())
        before = [self.manifest(app) for app in ("student", "tutor")]
        self.write_products(tutor=b"nondeterministic-rebuild")
        result = self.publish()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("patch bump required", result.stderr)
        self.assertEqual(before, [self.manifest(app) for app in ("student", "tutor")])

    def test_before_first_manifest_retry_may_change_payload(self):
        # Model interruption after reserving a SHA but before any manifest publication.
        record = self.hub / "updates/release/versions/2.1.0.json"
        record.parent.mkdir(parents=True)
        record.write_text(json.dumps({"version": self.version, "channel": self.channel, "sha": self.sha}))
        self.write_products(tutor=b"fixed-retry")
        self.assert_ok(self.publish())

    def test_interrupted_publication_active_manifest_guards_without_pin(self):
        self.assert_ok(self.publish())
        (self.hub / "updates/release/versions/2.1.0-student.json").unlink()
        self.write_products(student=b"changed-after-manifest")
        self.assertNotEqual(0, self.publish().returncode)

    def test_history_pin_rejects_reused_payload_after_channel_advances(self):
        self.assert_ok(self.publish())
        self.version = "2.1.1"
        self.sha = "b" * 40
        self.write_products(student=b"next-version")
        self.assert_ok(self.publish())
        self.version = "2.1.0"
        self.sha = "a" * 40
        self.write_products(student=b"different-old-version")
        self.assertNotEqual(0, self.publish().returncode)
        self.assertEqual("2.1.1", self.manifest("student")["version"])

    def test_channel_isolation_and_version_override(self):
        self.assert_ok(self.publish())
        original = self.manifest("student", "release")
        self.channel = "beta"
        self.version = "2.1.0b"
        self.env["KIBERONE_VERSION"] = self.version
        self.assert_ok(self.preflight())
        self.write_products(student=b"beta-student")
        self.assert_ok(self.publish())
        self.assertEqual(original, self.manifest("student", "release"))
        self.assertEqual("2.1.0b", self.manifest("tutor", "beta")["version"])
        self.channel = "release"
        self.assertNotEqual(0, self.preflight().returncode)

    def test_invalid_tutor_leaves_student_manifest_unchanged(self):
        self.assert_ok(self.publish())
        original = self.manifest("student")
        tutor = self.repo / "updates/release/KIBERoneTutor.exe"
        tutor.write_bytes(b"corrupt-artifact")
        self.assertNotEqual(0, self.publish().returncode)
        self.assertEqual(original, self.manifest("student"))

    def test_only_current_version_installers_publish(self):
        installers = self.repo / "dist/installers"
        installers.mkdir(parents=True)
        (installers / "KIBERoneTutor-Setup-2.1.0-win-x64.zip").write_bytes(b"current")
        (installers / "KIBERoneTutor-Setup-2.0.0-win-x64.zip").write_bytes(b"stale")
        self.assert_ok(self.publish())
        published = list((self.hub / "installers/release").iterdir())
        self.assertEqual(1, len(published))
        self.assertIn("2.1.0", published[0].name)

    def test_build_sources_propagate_version_and_tutor_single_file(self):
        shell = (ROOT / "scripts/build-installers.sh").read_text()
        commands = re.findall(r"^dotnet (?:publish|run) .*?(?=\n[^ ]|\Z)", shell, re.M | re.S)
        self.assertEqual(6, len(commands))
        for command in commands:
            self.assertIn('-p:KiberoneVersion="$VERSION"', command)
            if 'dotnet publish "$ROOT/src/Kiberone.Tutor/' in command:
                self.assertIn('-p:PublishSingleFile=true', command)
                self.assertIn('-p:IncludeNativeLibrariesForSelfExtract=true', command)
        powershell = (ROOT / "scripts/build-installers.ps1").read_text()
        commands = re.findall(r"^& \$dotnet publish .*?(?=\n[^ ]|\Z)", powershell, re.M | re.S)
        self.assertEqual(4, len(commands))
        for command in commands:
            self.assertIn('"-p:KiberoneVersion=$version"', command)
            if 'src\\Kiberone.Tutor\\' in command:
                self.assertIn('-p:PublishSingleFile=true', command)
                self.assertIn('-p:IncludeNativeLibrariesForSelfExtract=true', command)
        self.assertIn('"$AFTER $VERSION"', CI)

    @unittest.skipUnless(shutil.which("powershell"), "Windows PowerShell is unavailable")
    def test_tutor_cleanup_is_limited_to_verified_publish_directory(self):
        source = (ROOT / "scripts/build-installers.ps1").read_text()
        function = source.split("function Reset-TutorPublishDirectory {", 1)[1].split("function New-StudentInstaller", 1)[0]
        function = "function Reset-TutorPublishDirectory {" + function
        target = self.repo / "dist/Tutor-win-x64"
        target.mkdir(parents=True)
        (target / "Kiberone.Tutor.dll").write_bytes(b"stale")
        sentinel = self.repo / "dist/Student-win-x64/native/keep.dll"
        sentinel.parent.mkdir(parents=True)
        sentinel.write_bytes(b"preserve-student-native")
        command = "$ErrorActionPreference='Stop'; $projectRoot='" + str(self.repo).replace("'", "''") + "';\n"
        command += function + "\nReset-TutorPublishDirectory -Name Tutor-win-x64"
        result = subprocess.run([shutil.which("powershell"), "-NoProfile", "-NonInteractive", "-Command", command],
                                capture_output=True, text=True)
        self.assert_ok(result)
        self.assertFalse(target.exists())
        self.assertEqual(b"preserve-student-native", sentinel.read_bytes())

    def test_beta_deployment_template_uses_branch_version_and_shared_lock(self):
        beta = (ROOT / "deploy/kiberone-release-beta.service").read_text()
        main = (ROOT / "deploy/kiberone-release.service").read_text()
        self.assertIn("Environment=KIBERONE_GIT_BRANCH=beta", beta)
        self.assertIn("Environment=KIBERONE_GIT_BRANCH=main", main)
        self.assertNotRegex(beta, r"(?m)^Environment=KIBERONE_VERSION=")
        for unit in (main, beta):
            self.assertIn("WorkingDirectory=/opt/kiberone", unit)
            self.assertIn("Environment=KIBERONE_HUB_DATA=/var/lib/kiberone-hub", unit)
            self.assertIn("ExecStart=/opt/kiberone/scripts/ci-pull-and-release.sh", unit)
        self.assertIn("Environment=KIBERONE_RELEASE_STATE_DIR=/var/lib/kiberone-hub/release-state", beta)
        self.assertIn('STATE_DIR="${KIBERONE_RELEASE_STATE_DIR:-${HUB_DATA:-${REPO}.release-state}/release-state}"', CI)
        self.assertLess(CI.index("flock -n 9"), CI.index('git fetch origin "$BRANCH"'))
        timer = (ROOT / "deploy/kiberone-release-beta.timer").read_text()
        self.assertIn("Unit=kiberone-release-beta.service", timer)
        self.assertIn("OnUnitActiveSec=15min", timer)
        hub = (ROOT / "deploy/kiberone-hub.service").read_text()
        self.assertIn("Environment=KIBERONE_BETA_RELEASE_UNIT=kiberone-release-beta.service", hub)
        self.assertIn("Environment=KIBERONE_RELEASE_UNIT=kiberone-release.service", hub)

    @unittest.skipUnless(shutil.which("powershell"), "Windows PowerShell is unavailable")
    def test_bump_helper_patch_minor_rollover_beta_and_preview(self):
        path = self.repo / "version.json"
        cases = (
            ("2.1.0", [], "2.1.1b"),
            ("2.1.0", ["-Kind", "minor"], "2.2.0b"),
            ("2.9.7", ["-Kind", "minor"], "3.0.0b"),
            ("2.1.0b", [], "2.1.1b"),
            ("2.1.0", ["-Channel", "beta"], "2.1.1b"),
            ("2.1.0b", ["-Channel", "release"], "2.1.1"),
            ("2.1.0", ["-Kind", "major"], "3.0.0b"),
            ("2.1.0", ["-WhatIf"], "2.1.0"),
        )
        for current, options, expected in cases:
            with self.subTest(current=current, options=options):
                path.write_text(json.dumps({"version": current, "preserved": "yes"}))
                result = subprocess.run([
                    shutil.which("powershell"), "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                    "-File", str(ROOT / "scripts/bump-version.ps1"), "-VersionFile", str(path), *options,
                ], capture_output=True, text=True)
                self.assert_ok(result)
                document = json.loads(path.read_text(encoding="utf-8-sig"))
                self.assertEqual(expected, document["version"])
                self.assertEqual("yes", document["preserved"])
                self.assertFalse(list(path.parent.glob(".version-*")))


if __name__ == "__main__":
    unittest.main(verbosity=2)
