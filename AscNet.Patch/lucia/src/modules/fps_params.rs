/// `ASCNET_PATCH_FPS` is opt-in: a plain positive decimal integer enables the unlock at that frame rate.
/// Absent or anything else (empty, `0`, negative, `-1` = "platform default", signs, spaces, overflow)
/// leaves the game's own frame rate alone.
pub(crate) fn target(value: Option<&str>) -> Option<i32> {
    let value = value?;
    if !value.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    value.parse().ok().filter(|&fps| fps > 0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn positive_integers_enable() {
        assert_eq!(target(Some("240")), Some(240));
        assert_eq!(target(Some("1")), Some(1));
        assert_eq!(target(Some("2147483647")), Some(i32::MAX));
    }

    #[test]
    fn everything_else_stays_vanilla() {
        for value in [None, Some(""), Some("0"), Some("000"), Some("-1"), Some("+60"), Some(" 60"), Some("60 "), Some("6.0"), Some("abc"), Some("2147483648")] {
            assert_eq!(target(value), None, "{value:?}");
        }
    }
}
