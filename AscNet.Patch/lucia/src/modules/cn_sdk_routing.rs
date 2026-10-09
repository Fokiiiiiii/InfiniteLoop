//! Pure helpers of the CN SDK module (std only, tested with `rustc --test`).

/// libcurl `CURLOPT_URL`.
pub(crate) const CURLOPT_URL: u32 = 10002;

/// The retail CN SDK (`KRSDKEx.dll`) talks to `KR_Host`/`KR_HostStandby` (`sdkapi*.kurogame.com|xyz`)
/// with exactly these server paths, so routing is by path like the Unity-side routing: the host is
/// whatever the SDK was configured with (primary, standby or a later change).
fn owned_path(path: &str) -> bool {
    let path = path.split(['?', '#']).next().unwrap_or(path);
    path.starts_with("/sdkcom/") || path == "/ad-service/v1/sendEvent"
}

/// `https://sdkapi.kurogame.com/sdkcom/v2/login/auto.lg?x=1` -> `<origin>/sdkcom/v2/login/auto.lg?x=1`.
/// Other hosts' URLs (agreement pages, payment, analytics, third-party OAuth) are left alone.
pub(crate) fn redirected_url(origin: &str, url: &str) -> Option<String> {
    let rest = url.strip_prefix("https://").or_else(|| url.strip_prefix("http://"))?;
    let end = rest.find(['/', '?', '#'])?;
    let path = rest[end..].starts_with('/').then(|| &rest[end..])?;
    owned_path(path).then(|| format!("{origin}{path}"))
}

/// Value of a top-level string member of the flat `KRSDKConfig.json`. Only what the diagnostics
/// need: no nesting, arrays or escapes (the shipped file has none in these fields).
pub(crate) fn config_string<'a>(json: &'a str, key: &str) -> Option<&'a str> {
    let json = json.trim_start_matches('\u{feff}');
    let quoted = format!("\"{key}\"");
    let mut from = 0;
    while let Some(found) = json[from..].find(&quoted) {
        let after = from + found + quoted.len();
        from = after;
        let Some(value) = json[after..].trim_start().strip_prefix(':') else { continue };
        let value = value.trim_start().strip_prefix('"')?;
        return value.find('"').map(|end| &value[..end]);
    }
    None
}

#[cfg(test)]
mod tests {
    use super::{config_string, redirected_url};

    const ORIGIN: &str = "http://127.0.0.1:8080";

    #[test]
    fn routes_sdk_server_paths_from_every_configured_host() {
        for host in [
            "https://sdkapi.kurogame.com",
            "https://sdkapi2.kurogame.com",
            "https://sdkapi3.kurogame.com",
            "https://sdkapi.kurogame.xyz",
            "http://localhost:9",
        ] {
            for path in ["/sdkcom/v2/sys/conf.lg", "/sdkcom/v2/login/accLogin.lg?a=1&b=%E4%B8%AD", "/ad-service/v1/sendEvent"] {
                assert_eq!(redirected_url(ORIGIN, &format!("{host}{path}")), Some(format!("{ORIGIN}{path}")), "{host}{path}");
            }
        }
    }

    #[test]
    fn leaves_everything_else_on_its_own_server() {
        for url in [
            "https://pro-cdn-sdk.kurogame.com/pro/sdk_personal/index.html",
            "https://sdkcdn1.kurogame.com/entry/G148/entrypoint.json",
            "https://sdkapi.kurogame.com/sdk/trade/v1/order/query",
            "https://accounts.tapapis.cn/oauth2/v1",
            "https://sdkapi.kurogame.com/ad-service/v1/other",
            "https://sdkapi.kurogame.com/sdkcomx/v2",
            "https://sdkapi.kurogame.com",
            "https://sdkapi.kurogame.com?next=/sdkcom/v2/sys/conf.lg",
            "/sdkcom/v2/sys/conf.lg",
            "ftp://sdkapi.kurogame.com/sdkcom/v2/sys/conf.lg",
        ] {
            assert_eq!(redirected_url(ORIGIN, url), None, "{url}");
        }
    }

    // Layout of the shipped 4.8.0 PGR_Data/Plugins/KRSDKRes/KRSDKConfig.json (spacing as in the file).
    const CONFIG: &str = "\u{feff}{\r\n   \"KR_GameName\" : \"战双帕弥什\",\r\n   \"KR_PackageName\" : \"com.kurogame.haru.hero\",\r\n   \"KR_ProjectId\" : \"G148\",\r\n   \"KR_ChannelId\": \"19\",\r\n   \"KR_CDN_URL\": [\"https://sdkcdn1.kurogame.com/entry\"],\r\n   \"KR_ProductId\" : \"A1393\"\r\n}";

    #[test]
    fn reads_identity_strings_whatever_the_spacing() {
        assert_eq!(config_string(CONFIG, "KR_PackageName"), Some("com.kurogame.haru.hero"));
        assert_eq!(config_string(CONFIG, "KR_ProjectId"), Some("G148"));
        assert_eq!(config_string(CONFIG, "KR_ChannelId"), Some("19"));
        assert_eq!(config_string(CONFIG, "KR_ProductId"), Some("A1393"));
        assert_eq!(config_string(CONFIG, "KR_GameName"), Some("战双帕弥什"));
        assert_eq!(config_string(CONFIG, "KR_Missing"), None);
        // An array value is not a string field.
        assert_eq!(config_string(CONFIG, "KR_CDN_URL"), None);
    }
}
