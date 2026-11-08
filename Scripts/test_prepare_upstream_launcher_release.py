import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from Scripts.prepare_upstream_launcher_release import PACKAGE_FILES, prepare_archive


class PrepareUpstreamLauncherReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.source = self.root / "upstream.zip"
        self.output = self.root / "fork.zip"
        self._write_source()

    def tearDown(self):
        self.temp.cleanup()

    def _write_source(self, client_version="4.9.1", extra_files=()):
        supported = {
            "applicationVersion": client_version,
            "downloads": {
                region: {"version": client_version}
                for region in ("en", "jp", "kr", "tw", "cn")
            },
        }
        launcher = {
            "repositoryUrl": "https://github.com/reiserFSs/InfiniteLoop.git",
            "branch": "master",
        }
        with zipfile.ZipFile(self.source, "w") as archive:
            for name in PACKAGE_FILES:
                if name == "supported-client.json":
                    data = json.dumps(supported).encode()
                elif name == "launcher.json":
                    data = json.dumps(launcher).encode()
                else:
                    data = f"prebuilt:{name}".encode()
                archive.writestr(name, data)
            for name in extra_files:
                archive.writestr(name, b"unexpected")

    def test_rewrites_only_updater_target_and_keeps_prebuilt_files(self):
        version, regions = prepare_archive(
            self.source, self.output, "Fokiiiiiii/InfiniteLoop"
        )
        self.assertEqual("4.9.1", version)
        self.assertEqual({region: "4.9.1" for region in ("en", "jp", "kr", "tw", "cn")}, regions)
        with zipfile.ZipFile(self.output) as archive, zipfile.ZipFile(self.source) as upstream:
            self.assertEqual(
                upstream.read("supported-client.json"),
                archive.read("supported-client.json"),
            )
            self.assertEqual(set(PACKAGE_FILES), set(archive.namelist()))
            launcher = json.loads(archive.read("launcher.json"))
            self.assertEqual(
                "https://github.com/Fokiiiiiii/InfiniteLoop.git",
                launcher["repositoryUrl"],
            )
            self.assertEqual("main", launcher["branch"])
            for name in PACKAGE_FILES:
                if name not in ("launcher.json", "supported-client.json"):
                    self.assertEqual(f"prebuilt:{name}".encode(), archive.read(name))

    def test_replaces_launcher_with_windows_ci_binary(self):
        built_launcher = self.root / "AscNetLauncher.exe"
        built_bytes = b"MZ-fork-built-launcher"
        built_launcher.write_bytes(built_bytes)

        prepare_archive(
            self.source, self.output, "Fokiiiiiii/InfiniteLoop", built_launcher
        )

        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(built_bytes, archive.read("AscNetLauncher.exe"))

    def test_rejects_client_older_than_4_9(self):
        self._write_source(client_version="4.8.0")
        with self.assertRaisesRegex(ValueError, "4.9"):
            prepare_archive(self.source, self.output, "Fokiiiiiii/InfiniteLoop")
        self.assertFalse(self.output.exists())

    def test_rejects_unexpected_archive_members(self):
        self._write_source(extra_files=("../outside.txt",))
        with self.assertRaisesRegex(ValueError, "exactly the seven"):
            prepare_archive(self.source, self.output, "Fokiiiiiii/InfiniteLoop")
        self.assertFalse(self.output.exists())

    def test_rejects_invalid_repository(self):
        with self.assertRaisesRegex(ValueError, "owner/name"):
            prepare_archive(self.source, self.output, "https://example.test/repo")


if __name__ == "__main__":
    unittest.main()
