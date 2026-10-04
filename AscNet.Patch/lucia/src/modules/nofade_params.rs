/// `XNpcDither.PlayerSelfDitherParameter` (four consecutive `float` fields, 16 bytes).
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct PlayerSelfDither {
    pub(crate) max_distance: f32,
    pub(crate) min_distance: f32,
    pub(crate) angle_degree_max: f32,
    pub(crate) angle_degree_min: f32,
}

/// Same values as tools/apply_pgr_nofade.py: alpha is always >= 1 (opaque).
pub(crate) const OPAQUE: PlayerSelfDither = PlayerSelfDither {
    max_distance: -1.0,
    min_distance: -2.0,
    angle_degree_max: -999.0,
    angle_degree_min: -1000.0,
};

/// Player-self dither alpha as the game computes it (before clamping to 0..=1).
#[cfg(test)]
fn alpha(p: PlayerSelfDither, distance: f32, angle: f32) -> f32 {
    ((distance - p.min_distance) / (p.max_distance - p.min_distance))
        .min((angle - p.angle_degree_min) / (p.angle_degree_max - p.angle_degree_min))
}

/// `ASCNET_PATCH_NOFADE` is opt-in: only the exact value `1` enables it.
pub(crate) fn enabled(value: Option<&str>) -> bool {
    value == Some("1")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn opaque_parameters_never_fade() {
        assert_eq!(std::mem::size_of::<PlayerSelfDither>(), 16);
        for distance in [0.0, 0.1, 1.0, 5.0, 100.0] {
            for angle in [0.0, 30.0, 90.0, 180.0] {
                assert!(alpha(OPAQUE, distance, angle) >= 1.0, "d={distance} a={angle}");
            }
        }
    }

    #[test]
    fn vanilla_like_parameters_do_fade() {
        let vanilla = PlayerSelfDither { max_distance: 1.5, min_distance: 0.5, angle_degree_max: 60.0, angle_degree_min: 30.0 };
        assert!(alpha(vanilla, 0.6, 90.0) < 1.0);
    }

    #[test]
    fn only_exact_one_enables() {
        assert!(enabled(Some("1")));
        for value in [None, Some(""), Some("0"), Some("true"), Some(" 1")] {
            assert!(!enabled(value));
        }
    }
}
