fn owned_path(path: &str) -> bool {
    let path = path.split(['?', '#']).next().unwrap_or(path);
    let notice = path
        .strip_prefix("client/notice/config/")
        .and_then(|path| path.rsplit('/').next());
    path.starts_with("client/config/")
        || path.starts_with("client/notice/html/")
        || matches!(
            notice,
            Some(
                "LoginNotice.json"
                    | "GameNotice.json"
                    | "SecondMenuNotice.json"
                    | "PopUpPicNotice.json"
            )
        )
}

/// `(cdn-key, package)` of a client config/notice request, e.g.
/// `.../client/config/<key>/com.kurogame.punishing.grayraven.kr/4.8.0/standalone/config.tab`.
pub(crate) fn client_package(url: &str) -> Option<(&str, &str)> {
    let path = url.split(['?', '#']).next()?;
    let rest = path
        .split_once("client/notice/config/")
        .or_else(|| path.split_once("client/config/"))?
        .1;
    let mut segments = rest.split('/');
    let (key, package) = (segments.next()?, segments.next()?);
    (!key.is_empty() && !package.is_empty() && segments.next().is_some()).then_some((key, package))
}

pub(crate) fn redirected_url(origin: &str, url: &str) -> Option<String> {
    if !url.contains("://") {
        let suffix = url.trim_start_matches('/');
        return owned_path(suffix).then(|| format!("{origin}/prod/{suffix}"));
    }

    let scheme = url.find("://")?;
    let path = url[scheme + 3..]
        .find('/')
        .map(|offset| scheme + 3 + offset)?;
    let suffix = &url[path..];
    // Client log/feedback uploads (EN/TW `*zspnslog.*` hosts) stay on the local server.
    let host = url[scheme + 3..path].split(':').next().unwrap_or_default();
    if host.contains("zspnslog.") && suffix.split(['?', '#']).next() == Some("/feedback") {
        return Some(format!("{origin}{suffix}"));
    }
    owned_path(suffix.strip_prefix("/prod/")?)
        .then(|| format!("{origin}{suffix}"))
}

#[cfg(test)]
mod tests {
    use super::{client_package, redirected_url};

    const ORIGIN: &str = "http://127.0.0.1:8080";

    #[test]
    fn names_the_regional_package_of_config_and_notice_requests() {
        for (url, key, package) in [
            ("http://prod-krcdn-ak.pgr-game.com/prod/client/config/jqlCmYRizwT76uvX/com.kurogame.punishing.grayraven.kr/4.8.0/standalone/config.tab?v=1", "jqlCmYRizwT76uvX", "com.kurogame.punishing.grayraven.kr"),
            ("client/notice/config/xZx901LhZhT6G2HG/com.kurogame.punishing.grayraven.jp/4.8.0/LoginNotice.json", "xZx901LhZhT6G2HG", "com.kurogame.punishing.grayraven.jp"),
        ] {
            assert_eq!(client_package(url), Some((key, package)));
        }
        assert_eq!(client_package("client/config/bootstrap.json"), None);
        assert_eq!(client_package("https://cdn.example/prod/client/notice/pic/banner.png"), None);
    }

    #[test]
    fn routes_only_server_owned_client_files() {
        for path in [
            "client/config/bootstrap.json",
            "client/notice/config/cdn-key/package/version/LoginNotice.json",
            "client/notice/config/cdn-key/package/version/GameNotice.json",
            "client/notice/config/cdn-key/package/version/SecondMenuNotice.json",
            "client/notice/config/cdn-key/package/version/PopUpPicNotice.json",
            "client/notice/html/cdn-key/package/version/en-US.html",
        ] {
            assert_eq!(
                redirected_url(ORIGIN, path),
                Some(format!("{ORIGIN}/prod/{path}"))
            );
            let absolute = format!("https://cdn.example/prod/{path}?v=1#section");
            assert_eq!(
                redirected_url(ORIGIN, &absolute),
                Some(format!("{ORIGIN}/prod/{path}?v=1#section"))
            );
        }

        assert_eq!(
            redirected_url(
                ORIGIN,
                "/client/notice/config/key/package/version/LoginNotice.json?lang=en"
            ),
            Some(format!(
                "{ORIGIN}/prod/client/notice/config/key/package/version/LoginNotice.json?lang=en"
            ))
        );

        for path in [
            "client/notice/config/cdn-key/package/version/ScrollPicNotice.json",
            "client/notice/config/cdn-key/package/version/ScrollTextNotice.json",
            "client/notice/pic/cdn-key/package/version/banner.png",
        ] {
            assert_eq!(redirected_url(ORIGIN, path), None);
            assert_eq!(
                redirected_url(ORIGIN, &format!("https://cdn.example/prod/{path}?v=1")),
                None
            );
        }

        for url in [
            "http://prod.twzspnslog.pgr-game.com:50000/feedback",
            "http://prod.enzspnslog.kurogame.com/feedback?event=login",
        ] {
            let suffix = &url[url.find("/feedback").unwrap()..];
            assert_eq!(redirected_url(ORIGIN, url), Some(format!("{ORIGIN}{suffix}")));
        }
        assert_eq!(redirected_url(ORIGIN, "http://prod.twzspnslog.pgr-game.com:50000/other"), None);
        assert_eq!(redirected_url(ORIGIN, "https://cdn.example/feedback"), None);
    }

    #[test]
    fn routes_kr_and_jp_bootstrap_hosts_by_path() {
        // Hard-coded PrimaryCdns (pgr-game.com) and SecondaryCdns (kurogame.net) of the regional 4.8.0 clients.
        for (host, key, package) in [
            ("prod-krcdn-ak.pgr-game.com", "jqlCmYRizwT76uvX", "com.kurogame.punishing.grayraven.kr"),
            ("prod-krcdn-aliyun.kurogame.net", "jqlCmYRizwT76uvX", "com.kurogame.punishing.grayraven.kr"),
            ("prod-jpcdn-ak.pgr-game.com", "xZx901LhZhT6G2HG", "com.kurogame.punishing.grayraven.jp"),
            ("prod-jpcdn-aliyun.kurogame.net", "xZx901LhZhT6G2HG", "com.kurogame.punishing.grayraven.jp"),
        ] {
            for file in [
                "client/config/{}/{}/4.8.0/standalone/config.tab",
                "client/notice/config/{}/{}/4.8.0/LoginNotice.json",
                "client/notice/config/{}/{}/4.8.0/GameNotice.json",
            ] {
                let path = file.replacen("{}", key, 1).replacen("{}", package, 1);
                assert_eq!(
                    redirected_url(ORIGIN, &format!("http://{host}/prod/{path}")),
                    Some(format!("{ORIGIN}/prod/{path}")),
                    "{host} {path}"
                );
            }
            assert_eq!(redirected_url(ORIGIN, &format!("http://{host}/prod/client/notice/pic/banner.png")), None);
        }
    }

    #[test]
    fn routes_cn_bootstrap_hosts_by_path() {
        // CN config.tab (package com.kurogame.haru.kuro, cdn key YG08qvCbLecu9DJP) and its PrimaryCdns/SecondaryCdns hosts.
        let (key, package) = ("YG08qvCbLecu9DJP", "com.kurogame.haru.kuro");
        for host in ["prod-zspns-txcdn.kurogame.com", "prod-zspnsalicdn.kurogame.com"] {
            for file in [
                "client/config/{}/{}/4.8.0/standalone/config.tab",
                "client/notice/config/{}/{}/4.8.0/LoginNotice.json",
                "client/notice/config/{}/{}/4.8.0/GameNotice.json",
            ] {
                let path = file.replacen("{}", key, 1).replacen("{}", package, 1);
                assert_eq!(
                    redirected_url(ORIGIN, &format!("http://{host}/prod/{path}")),
                    Some(format!("{ORIGIN}/prod/{path}")),
                    "{host} {path}"
                );
                assert_eq!(client_package(&format!("http://{host}/prod/{path}")), Some((key, package)));
            }
            assert_eq!(redirected_url(ORIGIN, &format!("http://{host}/prod/client/notice/pic/banner.png")), None);
        }
    }
}
