#!/usr/bin/env python3
"""Validate and repack an upstream prebuilt launcher for the fork's updater."""

from __future__ import annotations

import argparse
import json
import re
import zipfile
from pathlib import Path

PACKAGE_FILES = (
    "AscNetLauncher.exe",
    "background.bmp",
    "background.mp4",
    "background.wav",
    "launcher.json",
    "setup-local.ps1",
    "supported-client.json",
)
REGIONS = ("en", "jp", "kr", "tw", "cn")
MINIMUM_CLIENT_VERSION = (4, 9, 0)
MAX_ARCHIVE_BYTES = 200 * 1024 * 1024
MAX_UNCOMPRESSED_BYTES = 512 * 1024 * 1024
VERSION_PATTERN = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")
REPOSITORY_PATTERN = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")


def _version(value: object, label: str) -> tuple[int, int, int]:
    if not isinstance(value, str) or not VERSION_PATTERN.fullmatch(value):
        raise ValueError(f"{label} is not a stable x.y.z version")
    return tuple(int(part) for part in value.split("."))


def _json_object(data: bytes, label: str) -> dict[str, object]:
    try:
        value = json.loads(data)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(f"{label} is not valid JSON") from error
    if not isinstance(value, dict):
        raise ValueError(f"{label} must contain a JSON object")
    return value


def prepare_archive(
    source_zip: Path,
    output_zip: Path,
    target_repository: str,
    launcher_exe: Path | None = None,
) -> tuple[str, dict[str, str]]:
    """Repack the upstream assets, fork updater config, and optional CI-built executable."""
    if not REPOSITORY_PATTERN.fullmatch(target_repository):
        raise ValueError("target repository must be owner/name")
    if source_zip.resolve() == output_zip.resolve():
        raise ValueError("input and output archives must be different files")
    if source_zip.stat().st_size > MAX_ARCHIVE_BYTES:
        raise ValueError("upstream launcher archive exceeds the size limit")

    with zipfile.ZipFile(source_zip) as source:
        entries = source.infolist()
        names = [entry.filename for entry in entries]
        if len(names) != len(PACKAGE_FILES) or set(names) != set(PACKAGE_FILES):
            raise ValueError("upstream archive does not contain exactly the seven launcher files")
        if len(names) != len(set(names)):
            raise ValueError("upstream archive contains duplicate file names")
        if sum(entry.file_size for entry in entries) > MAX_UNCOMPRESSED_BYTES:
            raise ValueError("upstream launcher archive expands beyond the size limit")
        if any(entry.flag_bits & 1 for entry in entries):
            raise ValueError("encrypted launcher archive members are not accepted")
        files = {name: source.read(name) for name in PACKAGE_FILES}

    if launcher_exe is not None:
        if not launcher_exe.is_file():
            raise ValueError("replacement launcher executable does not exist")
        launcher_bytes = launcher_exe.read_bytes()
        if len(launcher_bytes) < 2 or not launcher_bytes.startswith(b"MZ"):
            raise ValueError("replacement launcher is not a Windows executable")
        files["AscNetLauncher.exe"] = launcher_bytes
        if sum(len(data) for data in files.values()) > MAX_UNCOMPRESSED_BYTES:
            raise ValueError("prepared launcher archive expands beyond the size limit")

    supported = _json_object(files["supported-client.json"], "supported-client.json")
    application_version = _version(
        supported.get("applicationVersion"), "applicationVersion"
    )
    if application_version < MINIMUM_CLIENT_VERSION:
        raise ValueError("upstream package does not support client 4.9 or newer")

    downloads = supported.get("downloads")
    if not isinstance(downloads, dict) or not set(REGIONS).issubset(downloads):
        raise ValueError("supported-client.json is missing a regional download")
    regional_versions: dict[str, str] = {}
    for region in REGIONS:
        record = downloads[region]
        if not isinstance(record, dict):
            raise ValueError(f"supported-client.json has an invalid {region} record")
        raw_version = record.get("version")
        if _version(raw_version, f"{region}.version") < MINIMUM_CLIENT_VERSION:
            raise ValueError(f"upstream package drops {region} below client 4.9")
        regional_versions[region] = raw_version

    launcher = _json_object(files["launcher.json"], "launcher.json")
    if not isinstance(launcher.get("repositoryUrl"), str):
        raise ValueError("launcher.json is missing repositoryUrl")
    launcher["repositoryUrl"] = f"https://github.com/{target_repository}.git"
    launcher["branch"] = "main"
    files["launcher.json"] = (
        json.dumps(launcher, indent=2, ensure_ascii=False) + "\n"
    ).encode("utf-8")

    output_zip.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(
        output_zip, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6
    ) as output:
        for name in PACKAGE_FILES:
            output.writestr(name, files[name])

    with zipfile.ZipFile(output_zip) as result:
        result_names = [entry.filename for entry in result.infolist()]
        if len(result_names) != len(PACKAGE_FILES) or set(result_names) != set(PACKAGE_FILES):
            output_zip.unlink(missing_ok=True)
            raise ValueError("prepared launcher archive failed its file-list check")
        if output_zip.stat().st_size > MAX_ARCHIVE_BYTES:
            output_zip.unlink(missing_ok=True)
            raise ValueError("prepared launcher archive exceeds the size limit")

    return ".".join(map(str, application_version)), regional_versions


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source_zip", type=Path)
    parser.add_argument("output_zip", type=Path)
    parser.add_argument("target_repository")
    parser.add_argument("--launcher-exe", type=Path)
    args = parser.parse_args()
    version, regions = prepare_archive(
        args.source_zip, args.output_zip, args.target_repository, args.launcher_exe
    )
    print(
        f"Prepared upstream launcher client {version} for "
        f"{args.target_repository} ({', '.join(f'{key}={value}' for key, value in regions.items())})"
    )


if __name__ == "__main__":
    main()
