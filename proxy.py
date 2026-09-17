import os
from urllib.parse import urlparse, urlunparse
from mitmproxy import http
from mitmproxy import ctx
from mitmproxy.proxy import layer

from region_profile import (
    ConfigMode,
    RegionProfile,
    TW_PROFILE,
    authoritative_config_matches,
    get_region_profile,
)

PINNED_HOST_PATTERNS = (
    r".*sdk-prod-cdn-aws\.kurogame-service\.(com|xyz).*",
    r".*qcloud-sg-datareceiver\.kurogame\.xyz.*",
    r".*mp-gb-sdklog\.kurogames\.net.*",
    r".*events\.appsflyer\.com.*",
    r".*anticheatexpert\.com:443",
    r"sdkapi\.kurogame-service\.(com|xyz):443",
    r"pgr\.kurogame\.net:443",
)

def load(loader):
    # ctx.options.web_open_browser = False
    # We change the connection strategy to lazy so that next_layer happens before we actually connect upstream.
    ctx.options.connection_strategy = "lazy"
    ctx.options.upstream_cert = False
    ctx.options.ssl_insecure = True
    ctx.options.ignore_hosts = list(PINNED_HOST_PATTERNS)
    if _local_capture_enabled():
        # Keep the regular pass-through list. Pinned KRSDK/service HTTPS
        # traffic must remain a raw tunnel instead of being MITM'd locally.
        ctx.options.rawtcp = True


def _local_capture_enabled():
    return os.environ.get("ASCNET_LOCAL_CAPTURE", "").strip().lower() in {"1", "true", "yes", "on"}


def _normalise_connect_host(host):
    if host in {None, "", "*", "0.0.0.0", "::", "[::]"}:
        return "127.0.0.1"

    return host

def _is_local_wildcard_host(host):
    return host in {"*", "0.0.0.0", "::", "[::]"}


def _ascnet_target():
    raw_target = os.environ.get("ASCNET_PROXY_TARGET", "http://127.0.0.1:8080").strip()
    if "://" not in raw_target:
        raw_target = f"http://{raw_target}"

    parsed = urlparse(raw_target)
    scheme = parsed.scheme or "http"
    host = _normalise_connect_host(parsed.hostname)
    port = parsed.port or (443 if scheme == "https" else 80)
    return scheme, host, port


def _flow_log_path():
    return os.environ.get("ASCNET_PROXY_LOG")


def _diagnostic_url(url):
    parsed = urlparse(url)
    return urlunparse(parsed._replace(
        netloc=parsed.netloc.rsplit("@", 1)[-1], params="", query="", fragment="",
    ))


def _log_flow(prefix, flow):
    path = _flow_log_path()
    if not path:
        return
    status = getattr(flow.response, "status_code", "-") if getattr(flow, "response", None) else "-"
    line = f"{prefix} {flow.request.method} {_diagnostic_url(flow.request.pretty_url)} -> {status}\n"
    with open(path, "a", encoding="utf-8") as handle:
        handle.write(line)


def _is_ascnet_host(host):
    return get_region_profile("global").matches_route_host(host)


def detect_region(flow=None) -> RegionProfile:
    """Resolve the configured region, retaining legacy TW auto-detection."""
    profile = get_region_profile(os.environ.get("ASCNET_REGION", "global"))
    if profile.name == "global" and flow is not None:
        request = flow.request
        if authoritative_config_matches(TW_PROFILE, request.pretty_host, request.path):
            return TW_PROFILE
    return profile


def _is_pgr_game_popup_notice_request(flow, profile: RegionProfile | None = None):
    profile = profile or detect_region(flow)
    host = flow.request.pretty_host
    path = flow.request.path.split("?", 1)[0]
    return (
        profile.matches_notice_host(host)
        and path.startswith("/prod/client/notice/config/")
        and path.endswith("/PopUpPicNotice.json")
    )



def _is_upstream_notice_html_request(flow, profile: RegionProfile | None = None):
    profile = profile or detect_region(flow)
    path = flow.request.path.split("?", 1)[0]
    return (
        path.startswith("/prod/client/notice/html/")
        and (profile.matches_route_host(flow.request.pretty_host)
             or (profile.config_mode is ConfigMode.AUTHORITATIVE and not profile.notice_hosts))
    )


def _is_ascnet_gate_request(flow):
    return flow.request.path.split("?", 1)[0] == "/api/Login/Login"


def _is_feedback_request(flow, profile: RegionProfile | None = None):
    profile = profile or detect_region(flow)
    return profile.matches_host(flow.request.pretty_host, profile.feedback_hosts) and flow.request.path.split("?", 1)[0] == "/feedback"

def _is_wildcard_connect_request(flow):
    return flow.request.method == "CONNECT" and _is_local_wildcard_host(flow.request.pretty_host)


def _is_wildcard_ascnet_request(flow):
    path = flow.request.path.split("?", 1)[0]
    return _is_local_wildcard_host(flow.request.pretty_host) and path.startswith(("/api/", "/prod/", "/sdkcom/"))


def is_authoritative_config_request(flow, profile: RegionProfile | None = None) -> bool:
    return authoritative_config_matches(
        profile or detect_region(flow),
        flow.request.pretty_host,
        flow.request.path,
    )


# Compatibility alias for integrations that imported the old helper. New
# routing uses the profile-independent predicate above.
def _is_tw_config_request(flow):
    return authoritative_config_matches(TW_PROFILE, flow.request.pretty_host, flow.request.path)


def rewrite_game_server_routes(flow, profile: RegionProfile | None = None) -> bool:
    """Rewrite only endpoints that belong to the selected local server."""
    profile = profile or detect_region(flow)
    if is_authoritative_config_request(flow, profile) or _is_upstream_notice_html_request(flow, profile):
        return False

    if not (
        profile.matches_route_host(flow.request.pretty_host)
        or _is_pgr_game_popup_notice_request(flow, profile)
        or _is_ascnet_gate_request(flow)
        or _is_wildcard_ascnet_request(flow)
    ):
        return False

    scheme, host, port = _ascnet_target()
    original_host = flow.request.host
    original_scheme = flow.request.scheme

    flow.request.scheme = scheme
    flow.request.host = host
    flow.request.port = port
    flow.request.headers["Host"] = host if port in (80, 443) else f"{host}:{port}"
    flow.request.headers["X-Forwarded-Host"] = original_host
    flow.request.headers["X-Forwarded-Proto"] = original_scheme
    return True


def _ascnet_origin():
    scheme, host, port = _ascnet_target()
    if port in (80, 443):
        return f"{scheme}://{host}"
    return f"{scheme}://{host}:{port}"


def _rewrite_login_url(value, target_origin):
    # ServerListStr/ChannelServerListStr are `label#url` / `default#label#url`.
    # Keep labels and metadata, replace only the final URL's origin with the
    # local AscNet target while preserving its path.
    head, sep, url = value.rpartition("#")
    parsed = urlparse(url)
    if not (sep and parsed.scheme and parsed.hostname):
        return value
    suffix = parsed.path + (f"?{parsed.query}" if parsed.query else "")
    return head + sep + target_origin + suffix


def _rewrite_authoritative_config_body(body, target_origin):
    lines = body.split("\n")
    out = []
    for line in lines:
        cols = line.split("\t")
        if len(cols) >= 3 and cols[0] in {"ServerListStr", "ChannelServerListStr"}:
            cols[2] = _rewrite_login_url(cols[2], target_origin)
        out.append("\t".join(cols))
    return "\n".join(out)


# Kept as a small compatibility alias for callers of the pre-profile helper.
def _rewrite_tw_config_body(body, target_origin):
    return _rewrite_authoritative_config_body(body, target_origin)


def next_layer(nextlayer: layer.NextLayer):
    # Only mark hosts we intend to rewrite. HTTPS proxying is intentionally
    # avoided for pinned KRSDK/service hosts by the runner/environment.
    sni = nextlayer.context.client.sni
    if _is_ascnet_host(sni):
        ctx.log.info("ascnet candidate sni:" + sni)


def http_connect(flow: http.HTTPFlow) -> None:
    _log_flow("CONNECT", flow)

    if not _is_wildcard_connect_request(flow):
        return

    flow.response = http.Response.make(
        502,
        b"AscNet blocked invalid CONNECT target 0.0.0.0/::; restart with run_steam.py so local SDK URLs use 127.0.0.1.\n",
        {"Content-Type": "text/plain"},
    )
    _log_flow("CONNECT-BLOCK", flow)


def request(flow: http.HTTPFlow) -> None:
    _log_flow("REQ", flow)
    profile = detect_region(flow)

    if _is_feedback_request(flow, profile):
        flow.response = http.Response.make(200, b"OK", {"Content-Type": "text/plain"})
        _log_flow("SINK", flow)
        return

    # Notice metadata points at version-specific CDN HTML files. Keep those
    # requests on the original CDN so new notices work without local fixtures.
    if _is_upstream_notice_html_request(flow, profile):
        _log_flow("PASS", flow)
        return

    # Authoritative regional config carries upstream metadata (document/launch
    # version, channel, CDN list) that local AscNet does not reproduce. Let it
    # pass through unchanged; response() rewrites only login endpoints.
    if is_authoritative_config_request(flow, profile):
        _log_flow("PASS", flow)
        return

    rewrite_game_server_routes(flow, profile)


def response(flow: http.HTTPFlow) -> None:
    _log_flow("RSP", flow)

    # Authoritative regional config was passed through upstream unchanged.
    # Rewrite only login endpoint URLs so the client reaches local AscNet,
    # keeping metadata (version, channel, CDNs, labels) from the source.
    if not is_authoritative_config_request(flow) or flow.response is None:
        return

    body = flow.response.content
    if not body:
        return

    text = body.decode("utf-8", errors="replace")
    rewritten = _rewrite_authoritative_config_body(text, _ascnet_origin())
    if rewritten != text:
        flow.response.content = rewritten.encode("utf-8")
        _log_flow("TW-CONFIG-REWRITE", flow)
