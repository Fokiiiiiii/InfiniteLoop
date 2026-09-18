"""Region profiles shared by the Steam runner and mitmproxy bridge.

Only values already present in the bridge, its tests, or an observed client
request are recorded as known. The JP profile is populated from the observed
4.7.0 PC client log; no package, host, channel, or version values are guessed.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from fnmatch import fnmatchcase
from ipaddress import ip_address
from pathlib import PurePosixPath
from typing import Iterable


class ConfigMode(str, Enum):
    LOCAL = "local"
    AUTHORITATIVE = "authoritative"
    UNKNOWN = "unknown"


@dataclass(frozen=True)
class ConfigSmokeTarget:
    label: str
    path: str
    channel_assertion: str
    application_version: str = "4.6.0"
    document_version: str | None = None
    base_url: str | None = None


@dataclass(frozen=True)
class RegionProfile:
    name: str
    package_names: tuple[str, ...]
    config_hosts: tuple[str, ...]
    notice_hosts: tuple[str, ...]
    sdk_hosts: tuple[str, ...]
    feedback_hosts: tuple[str, ...]
    route_hosts: tuple[str, ...]
    config_mode: ConfigMode
    expected_channel: int | None
    expected_channels: tuple[int, ...]
    config_smoke_targets: tuple[ConfigSmokeTarget, ...] = ()
    metadata_status: str = "verified"
    discovery_required: tuple[str, ...] = ()

    @property
    def requires_discovery(self) -> bool:
        return bool(self.discovery_required)

    def matches_host(self, host: str | None, patterns: Iterable[str]) -> bool:
        if not host:
            return False
        normalised = host.rstrip(".").lower()
        return any(fnmatchcase(normalised, pattern.lower()) for pattern in patterns)

    def matches_config_host(self, host: str | None) -> bool:
        return self.matches_host(host, self.config_hosts)

    def matches_notice_host(self, host: str | None) -> bool:
        return self.matches_host(host, self.notice_hosts)

    def matches_sdk_host(self, host: str | None) -> bool:
        return self.matches_host(host, self.sdk_hosts)

    def matches_route_host(self, host: str | None) -> bool:
        return self.matches_host(host, self.route_hosts)

    def discovery_error(self) -> str:
        missing = ", ".join(self.discovery_required) or "region metadata"
        return f"Region '{self.name}' is UNKNOWN / discovery required: {missing}. Supply observed client/config values before enabling its smoke test."


GLOBAL_PROFILE = RegionProfile(
    name="global",
    package_names=(
        "com.kurogame.punishing.grayraven.en",
        "com.kurogame.gplay.punishing.grayraven.en",
        "com.kurogame.pc.punishing.grayraven.en",
    ),
    config_hosts=("prod-encdn-*.kurogame.net",),
    notice_hosts=(
        "prod-encdn-*.pgr-game.com",
        "prod-twcdn-*.pgr-game.com",
    ),
    sdk_hosts=(
        "sdkapi.kurogame-service.com",
        "sdkapi.kurogame-service.xyz",
    ),
    feedback_hosts=("prod.enzspnslog.kurogame.com", "prod.twzspnslog.kurogame.com"),
    # Keep the legacy default routing union: old clients can still reach the
    # TW CDN host while an explicit TW config request is passed through.
    route_hosts=(
        "sdkapi.kurogame-service.com",
        "sdkapi.kurogame-service.xyz",
        "prod-encdn-*.kurogame.net",
        "prod-twcdn-*.kurogame.net",
    ),
    config_mode=ConfigMode.LOCAL,
    expected_channel=None,
    expected_channels=(5, 205),
    config_smoke_targets=(
        ConfigSmokeTarget(
            "global-client",
            "/prod/client/config/9jY3H6OqsppPLu31/com.kurogame.punishing.grayraven.en/4.6.0/standalone/config.tab",
            "Channel\tint\t5",
        ),
        ConfigSmokeTarget(
            "steam-pc-package",
            "/prod/client/config/9jY3H6OqsppPLu31/com.kurogame.pc.punishing.grayraven.en/4.6.0/standalone/config.tab",
            "Channel\tint\t205",
        ),
    ),
)


TW_PROFILE = RegionProfile(
    name="tw",
    package_names=("com.kurogame.punishing.grayraven.tw",),
    config_hosts=("prod-twcdn-*.kurogame.net",),
    notice_hosts=("prod-twcdn-*.pgr-game.com",),
    sdk_hosts=(
        "sdkapi.kurogame-service.com",
        "sdkapi.kurogame-service.xyz",
    ),
    feedback_hosts=("prod.twzspnslog.kurogame.com",),
    route_hosts=(
        "sdkapi.kurogame-service.com",
        "sdkapi.kurogame-service.xyz",
        "prod-twcdn-*.kurogame.net",
    ),
    config_mode=ConfigMode.AUTHORITATIVE,
    expected_channel=5,
    expected_channels=(5,),
    # TW config is authoritative upstream data. The bridge tests verify the
    # pass-through/rewrite contract; this runner does not fake a local smoke.
)


JP_PROFILE = RegionProfile(
    name="jp",
    package_names=("com.kurogame.punishing.grayraven.jp",),
    config_hosts=("prod-jpcdn-*.pgr-game.com", "prod-jpcdn-*.kurogame.net"),
    notice_hosts=("prod-jpcdn-*.pgr-game.com", "prod-jpcdn-*.kurogame.net"),
    sdk_hosts=(
        "sdkapi.kurogame-service.com",
        "sdkapi.kurogame-service.xyz",
    ),
    feedback_hosts=("prod.jpzspnslog.kurogame.com",),
    route_hosts=(
        "sdkapi.kurogame-service.com",
        "sdkapi.kurogame-service.xyz",
        "prod-jpcdn-*.pgr-game.com",
        "prod-jpcdn-*.kurogame.net",
    ),
    config_mode=ConfigMode.AUTHORITATIVE,
    expected_channel=5,
    expected_channels=(5,),
    config_smoke_targets=(
        ConfigSmokeTarget(
            "jp-client",
            "/prod/client/config/BYf6VZR7DluwhM64/com.kurogame.punishing.grayraven.jp/4.7.0/standalone/config.tab",
            "Channel\tint\t5",
            base_url="https://prod-jpcdn-tx.kurogame.net",
            application_version="4.7.0",
            document_version="4.7.15",
        ),
    ),
    metadata_status="observed",
)


REGION_PROFILES: dict[str, RegionProfile] = {
    profile.name: profile for profile in (GLOBAL_PROFILE, TW_PROFILE, JP_PROFILE)
}


def region_names() -> tuple[str, ...]:
    return tuple(REGION_PROFILES)


def get_region_profile(name: str | None) -> RegionProfile:
    value = (name or "global").strip().lower()
    try:
        return REGION_PROFILES[value]
    except KeyError as exc:
        choices = ", ".join(region_names())
        raise ValueError(f"Unknown region {name!r}; expected one of: {choices}") from exc


def is_config_path(path: str | None) -> bool:
    if not path:
        return False
    clean_path = path.split("?", 1)[0]
    parts = PurePosixPath(clean_path).parts
    return (
        len(parts) >= 6
        and parts[1:4] == ("prod", "client", "config")
        and parts[-2:] == ("standalone", "config.tab")
    )


def package_from_config_path(path: str | None) -> str | None:
    if not is_config_path(path):
        return None
    parts = PurePosixPath(path.split("?", 1)[0]).parts
    # Both /config/{package}/{version}/... and
    # /config/{cdnKey}/{package}/{version}/... are accepted by the server.
    if len(parts) >= 6 and parts[-2:] == ("standalone", "config.tab"):
        return parts[-4] if len(parts) >= 7 else None
    return None


def is_ip_host(host: str | None) -> bool:
    if not host:
        return False
    try:
        ip_address(host.strip("[]"))
    except ValueError:
        return False
    return True


def authoritative_config_matches(profile: RegionProfile, host: str | None, path: str | None) -> bool:
    if profile.config_mode is not ConfigMode.AUTHORITATIVE or not is_config_path(path):
        return False
    # An empty host set remains useful for an explicitly selected region in
    # discovery mode. Known profiles still require an observed host or the
    # observed package path below when a process redirector exposes an IP.
    if not profile.config_hosts or profile.matches_config_host(host):
        return True

    # Process-scoped redirectors can expose the CDN as a resolved IP instead
    # of its configured hostname. Allow that observed-IP case, but do not let
    # an arbitrary unrelated hostname plus a familiar path opt into rewriting.
    package = package_from_config_path(path)
    return is_ip_host(host) and package in profile.package_names
