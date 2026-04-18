//! Theme system: OKLCH-based contrast-aware color generation.
//!
//! Design: Generate colors in OKLCH (perceptually uniform), lock Lightness for
//! contrast against background, cap Chroma for professional tones, rotate Hue
//! for each semantic role. Find nearest 256-color match for terminal output.
//! All foreground colors are validated against the actual background.

// ── Color Math: sRGB ───────────────────────────────────────────────

/// sRGB relative luminance (WCAG 2.0).
pub fn luminance(r: f64, g: f64, b: f64) -> f64 {
    let lin = |c: f64| {
        if c <= 0.03928 { c / 12.92 } else { ((c + 0.055) / 1.055).powf(2.4) }
    };
    0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b)
}

/// WCAG contrast ratio between two luminances.
pub fn contrast_ratio(l1: f64, l2: f64) -> f64 {
    let (light, dark) = if l1 > l2 { (l1, l2) } else { (l2, l1) };
    (light + 0.05) / (dark + 0.05)
}

/// Parse hex color (#RRGGBB or RRGGBB) to (r, g, b) in 0.0-1.0.
pub fn parse_hex(hex: &str) -> Option<(f64, f64, f64)> {
    let hex = hex.trim_start_matches('#');
    if hex.len() != 6 { return None; }
    let r = u8::from_str_radix(&hex[0..2], 16).ok()? as f64 / 255.0;
    let g = u8::from_str_radix(&hex[2..4], 16).ok()? as f64 / 255.0;
    let b = u8::from_str_radix(&hex[4..6], 16).ok()? as f64 / 255.0;
    Some((r, g, b))
}

/// sRGB gamma decode (to linear).
fn srgb_to_linear(c: f64) -> f64 {
    if c <= 0.04045 { c / 12.92 } else { ((c + 0.055) / 1.055).powf(2.4) }
}

/// Linear to sRGB gamma encode.
fn linear_to_srgb(c: f64) -> f64 {
    if c <= 0.0031308 { c * 12.92 } else { 1.055 * c.powf(1.0 / 2.4) - 0.055 }
}

// ── Color Math: OKLAB / OKLCH ──────────────────────────────────────
//
// Björn Ottosson's OKLAB: perceptually uniform color space.
// OKLCH is the polar form: L (lightness 0-1), C (chroma 0-~0.37), H (hue 0-360).
// Reference: https://bottosson.github.io/posts/oklab/

/// sRGB (0-1) → OKLAB (L, a, b).
fn srgb_to_oklab(r: f64, g: f64, b: f64) -> (f64, f64, f64) {
    let r = srgb_to_linear(r);
    let g = srgb_to_linear(g);
    let b = srgb_to_linear(b);

    let l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
    let m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
    let s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;

    let l = l.cbrt();
    let m = m.cbrt();
    let s = s.cbrt();

    let lab_l = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
    let lab_a = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
    let lab_b = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;

    (lab_l, lab_a, lab_b)
}

/// OKLAB (L, a, b) → sRGB (0-1). Returns None if out of gamut.
fn oklab_to_srgb(lab_l: f64, lab_a: f64, lab_b: f64) -> Option<(f64, f64, f64)> {
    let l = lab_l + 0.3963377774 * lab_a + 0.2158037573 * lab_b;
    let m = lab_l - 0.1055613458 * lab_a - 0.0638541728 * lab_b;
    let s = lab_l - 0.0894841775 * lab_a - 1.2914855480 * lab_b;

    let l = l * l * l;
    let m = m * m * m;
    let s = s * s * s;

    let r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
    let g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
    let b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

    let r = linear_to_srgb(r);
    let g = linear_to_srgb(g);
    let b = linear_to_srgb(b);

    // Check gamut
    if r < -0.001 || r > 1.001 || g < -0.001 || g > 1.001 || b < -0.001 || b > 1.001 {
        return None;
    }

    Some((r.clamp(0.0, 1.0), g.clamp(0.0, 1.0), b.clamp(0.0, 1.0)))
}

/// sRGB → OKLCH (L, C, H). H in degrees 0-360.
fn srgb_to_oklch(r: f64, g: f64, b: f64) -> (f64, f64, f64) {
    let (l, a, b) = srgb_to_oklab(r, g, b);
    let c = (a * a + b * b).sqrt();
    let h = b.atan2(a).to_degrees();
    let h = if h < 0.0 { h + 360.0 } else { h };
    (l, c, h)
}

/// OKLCH (L, C, H) → sRGB. Reduces chroma if out of gamut.
fn oklch_to_srgb(l: f64, c: f64, h: f64) -> (f64, f64, f64) {
    let h_rad = h.to_radians();
    let a = c * h_rad.cos();
    let b = c * h_rad.sin();

    if let Some(rgb) = oklab_to_srgb(l, a, b) {
        return rgb;
    }

    // Out of gamut — reduce chroma until it fits
    let mut lo = 0.0;
    let mut hi = c;
    for _ in 0..20 {
        let mid = (lo + hi) / 2.0;
        let a = mid * h_rad.cos();
        let b = mid * h_rad.sin();
        if oklab_to_srgb(l, a, b).is_some() {
            lo = mid;
        } else {
            hi = mid;
        }
    }

    let a = lo * h_rad.cos();
    let b = lo * h_rad.sin();
    oklab_to_srgb(l, a, b).unwrap_or((0.5, 0.5, 0.5))
}

/// Angular distance between two hues (0-180).
fn hue_distance(h1: f64, h2: f64) -> f64 {
    let d = (h1 - h2).abs();
    if d > 180.0 { 360.0 - d } else { d }
}

// ── Color Math: CIE L*a*b* + CIEDE2000 ─────────────────────────────
//
// OKLAB is used for color derivation (cohesive palette identity), but
// collision avoidance switches to ΔE2000 — the CIE 2000 formula is
// the industry-standard perceptual difference metric and gives real
// guarantees about visible distinctness. See #228.
//
// CIEDE2000 operates on CIE L*a*b* (not OKLAB), so we need the full
// sRGB → linear → XYZ (D65) → CIE Lab conversion.

/// sRGB (0-1) → CIE L*a*b* under a D65 white point.
fn srgb_to_cielab(r: f64, g: f64, b: f64) -> (f64, f64, f64) {
    let r = srgb_to_linear(r);
    let g = srgb_to_linear(g);
    let b = srgb_to_linear(b);

    // Linear sRGB → XYZ (D65). sRGB matrix, IEC 61966-2-1.
    let x = 0.4124564 * r + 0.3575761 * g + 0.1804375 * b;
    let y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
    let z = 0.0193339 * r + 0.1191920 * g + 0.9503041 * b;

    // Normalize by D65 white point.
    const XN: f64 = 0.95047;
    const YN: f64 = 1.00000;
    const ZN: f64 = 1.08883;
    let fx = lab_f(x / XN);
    let fy = lab_f(y / YN);
    let fz = lab_f(z / ZN);

    let l = 116.0 * fy - 16.0;
    let a = 500.0 * (fx - fy);
    let b_star = 200.0 * (fy - fz);
    (l, a, b_star)
}

/// CIE Lab f() helper: piecewise cube-root / linear.
fn lab_f(t: f64) -> f64 {
    // δ = 6/29; threshold = δ³, slope = 1/(3δ²), offset = 4/29
    const DELTA: f64 = 6.0 / 29.0;
    let threshold = DELTA * DELTA * DELTA;
    if t > threshold {
        t.cbrt()
    } else {
        t / (3.0 * DELTA * DELTA) + 4.0 / 29.0
    }
}

/// CIEDE2000 color-difference between two CIE L*a*b* samples.
///
/// Implements the formula from Sharma, Wu, Dalal, "The CIEDE2000 Color-
/// Difference Formula: Implementation Notes, Supplementary Test Data,
/// and Mathematical Observations" (2005). Weighting factors kL = kC = kH
/// = 1.0 (standard observer, no weighting).
///
/// A ΔE2000 of ~2.3 is the "just noticeable difference" threshold; the
/// #228 collision-avoidance uses ≥ 5.0 for clearly distinct roles.
fn ciede2000(lab1: (f64, f64, f64), lab2: (f64, f64, f64)) -> f64 {
    let (l1, a1, b1) = lab1;
    let (l2, a2, b2) = lab2;

    // Step 1: compute C1*, C2*, and their mean C̄*
    let c1_star = (a1 * a1 + b1 * b1).sqrt();
    let c2_star = (a2 * a2 + b2 * b2).sqrt();
    let c_bar = (c1_star + c2_star) / 2.0;

    // Step 2: G factor (compensates low-chroma a* shift toward blue)
    let c_bar7 = c_bar.powi(7);
    let g = 0.5 * (1.0 - (c_bar7 / (c_bar7 + 25f64.powi(7))).sqrt());

    // Step 3: rotated a′, new C′ and h′
    let a1_prime = (1.0 + g) * a1;
    let a2_prime = (1.0 + g) * a2;
    let c1_prime = (a1_prime * a1_prime + b1 * b1).sqrt();
    let c2_prime = (a2_prime * a2_prime + b2 * b2).sqrt();
    let h1_prime = atan2_deg(b1, a1_prime);
    let h2_prime = atan2_deg(b2, a2_prime);

    // Step 4: ΔL′, ΔC′, ΔH′
    let delta_l = l2 - l1;
    let delta_c = c2_prime - c1_prime;

    let delta_h_prime_deg = if c1_prime * c2_prime == 0.0 {
        0.0
    } else {
        let d = h2_prime - h1_prime;
        if d > 180.0 { d - 360.0 } else if d < -180.0 { d + 360.0 } else { d }
    };
    let delta_h = 2.0 * (c1_prime * c2_prime).sqrt()
        * (delta_h_prime_deg.to_radians() / 2.0).sin();

    // Step 5: averages L̄′, C̄′, H̄′
    let l_bar_prime = (l1 + l2) / 2.0;
    let c_bar_prime = (c1_prime + c2_prime) / 2.0;

    let h_bar_prime = if c1_prime * c2_prime == 0.0 {
        h1_prime + h2_prime
    } else {
        let d = (h1_prime - h2_prime).abs();
        if d <= 180.0 {
            (h1_prime + h2_prime) / 2.0
        } else if h1_prime + h2_prime < 360.0 {
            (h1_prime + h2_prime + 360.0) / 2.0
        } else {
            (h1_prime + h2_prime - 360.0) / 2.0
        }
    };

    // Step 6: T, ΔΘ, R_C, S_L, S_C, S_H, R_T
    let t = 1.0
        - 0.17 * ((h_bar_prime - 30.0).to_radians()).cos()
        + 0.24 * ((2.0 * h_bar_prime).to_radians()).cos()
        + 0.32 * ((3.0 * h_bar_prime + 6.0).to_radians()).cos()
        - 0.20 * ((4.0 * h_bar_prime - 63.0).to_radians()).cos();

    let delta_theta =
        30.0 * (-((h_bar_prime - 275.0) / 25.0).powi(2)).exp();
    let c_bar_prime7 = c_bar_prime.powi(7);
    let r_c = 2.0 * (c_bar_prime7 / (c_bar_prime7 + 25f64.powi(7))).sqrt();

    let l_term = (l_bar_prime - 50.0).powi(2);
    let s_l = 1.0 + (0.015 * l_term) / (20.0 + l_term).sqrt();
    let s_c = 1.0 + 0.045 * c_bar_prime;
    let s_h = 1.0 + 0.015 * c_bar_prime * t;
    let r_t = -((2.0 * delta_theta).to_radians()).sin() * r_c;

    // Step 7: ΔE₀₀
    let dl = delta_l / s_l;
    let dc = delta_c / s_c;
    let dh = delta_h / s_h;
    (dl * dl + dc * dc + dh * dh + r_t * dc * dh).sqrt()
}

/// atan2 in degrees, normalized to [0, 360).
fn atan2_deg(y: f64, x: f64) -> f64 {
    if x == 0.0 && y == 0.0 {
        return 0.0;
    }
    let h = y.atan2(x).to_degrees();
    if h < 0.0 { h + 360.0 } else { h }
}

/// ΔE2000 between two sRGB samples (0-1). Convenience wrapper used by
/// the distinctness audit tests.
#[cfg(test)]
fn ciede2000_srgb(c1: (f64, f64, f64), c2: (f64, f64, f64)) -> f64 {
    ciede2000(srgb_to_cielab(c1.0, c1.1, c1.2), srgb_to_cielab(c2.0, c2.1, c2.2))
}

// ── xterm 256-color palette ─────────────────────────────────────────

/// Get RGB for a 256-color palette index.
fn palette_rgb(idx: u8) -> (f64, f64, f64) {
    if idx < 16 {
        let table: [(u8, u8, u8); 16] = [
            (0,0,0), (128,0,0), (0,128,0), (128,128,0), (0,0,128), (128,0,128), (0,128,128), (192,192,192),
            (128,128,128), (255,0,0), (0,255,0), (255,255,0), (0,0,255), (255,0,255), (0,255,255), (255,255,255),
        ];
        let (r, g, b) = table[idx as usize];
        (r as f64 / 255.0, g as f64 / 255.0, b as f64 / 255.0)
    } else if idx < 232 {
        let i = idx - 16;
        let r = (i / 36) as f64;
        let g = ((i % 36) / 6) as f64;
        let b = (i % 6) as f64;
        let to_val = |c: f64| if c == 0.0 { 0.0 } else { (c * 40.0 + 55.0) / 255.0 };
        (to_val(r), to_val(g), to_val(b))
    } else {
        let v = (8 + 10 * (idx as u16 - 232)) as f64 / 255.0;
        (v, v, v)
    }
}

/// Find the nearest 256-color index to a given sRGB color.
/// Skips basic 16 (terminal-dependent), searches 16-255.
#[cfg(test)]
fn nearest_256(r: f64, g: f64, b: f64) -> u8 {
    let mut best_idx: u8 = 7;
    let mut best_dist = f64::MAX;

    for idx in 16..=255u8 {
        let (pr, pg, pb) = palette_rgb(idx);
        // Perceptual distance in OKLAB
        let (l1, a1, b1) = srgb_to_oklab(r, g, b);
        let (l2, a2, b2) = srgb_to_oklab(pr, pg, pb);
        let dist = (l1 - l2).powi(2) + (a1 - a2).powi(2) + (b1 - b2).powi(2);
        if dist < best_dist {
            best_dist = dist;
            best_idx = idx;
        }
    }

    best_idx
}

/// Find the nearest 256-color index that meets minimum contrast against bg
/// and avoids already-used indices (for role distinction). Caller can
/// request chromatic-only candidates (no grayscale cells) — used when
/// the role has meaningful hue that would otherwise get flattened by
/// the grayscale ramp winning on L distance alone.
fn nearest_256_with_contrast(r: f64, g: f64, b: f64, bg_lum: f64, min_cr: f64, avoid: &[u8]) -> u8 {
    let (target_l, target_a, target_b) = srgb_to_oklab(r, g, b);
    let target_chromatic = (target_a.powi(2) + target_b.powi(2)).sqrt() > 0.03;
    let mut best_idx: Option<u8> = None;
    let mut best_dist = f64::MAX;

    for idx in 16..=255u8 {
        if avoid.contains(&idx) {
            continue;
        }
        let (pr, pg, pb) = palette_rgb(idx);

        // Chromatic target ⇒ skip grayscale cells so they don't win on
        // L distance alone (happens on mid-gray bgs where low-L green
        // squeezes into near-black and loses its hue).
        if target_chromatic {
            let is_gray = (pr - pg).abs() < 0.01 && (pg - pb).abs() < 0.01;
            if is_gray {
                continue;
            }
        }
        let lum = luminance(pr, pg, pb);
        if contrast_ratio(lum, bg_lum) < min_cr {
            continue;
        }
        // Penalize candidates too close to already-used palette cells.
        // Uses CIEDE2000 (ΔE2000 < 5 → visibly similar; see #228) so
        // cohabitation on the same background is judged by the industry-
        // standard perceptual metric rather than OKLAB Euclidean.
        let (pl, pa, pb_ok) = srgb_to_oklab(pr, pg, pb);
        let candidate_lab = srgb_to_cielab(pr, pg, pb);
        let mut collision_penalty = 0.0_f64;
        for &used_idx in avoid {
            let (ur, ug, ub) = palette_rgb(used_idx);
            let used_lab = srgb_to_cielab(ur, ug, ub);
            let de = ciede2000(candidate_lab, used_lab);
            if de < 5.0 {
                // Scale so an identical cell (de=0) pays 2.0 and a
                // cell right at the threshold (de=5) pays 0 — matches
                // the weighting curve of the previous OKLAB heuristic
                // so downstream scoring doesn't need retuning.
                collision_penalty += 2.0 * (1.0 - de / 5.0);
            }
        }
        let dist = (target_l - pl).powi(2) + (target_a - pa).powi(2) + (target_b - pb_ok).powi(2);
        let dist = dist + collision_penalty;
        if dist < best_dist {
            best_dist = dist;
            best_idx = Some(idx);
        }
    }

    best_idx.unwrap_or(if bg_lum < 0.5 { 15 } else { 0 })
}

// ── OKLCH Palette Generation ───────────────────────────────────────

/// Intensity within a tonal band. Every role lives in the band chosen
/// by the background's luminance; intensity only nudges the lightness
/// axis by ±band.step so dim / bright variants of a hue stay visually
/// related. Dim roles also get a relaxed contrast floor so muted UI
/// (PID, time, comment) actually *looks* muted.
#[derive(Copy, Clone, PartialEq)]
enum Intensity {
    Neutral, // gray — chroma forced to 0, L lowered like Dim
    Dim,     // closer to bg; floor 3.5:1 — dim chromatic
    Normal,  // the tonal-band base — default for most chromatic roles
    Bright,  // farther from bg; emphasized — dirty git, root, ssh host
}

/// A semantic color role. The tonal band (chosen from the bg) decides
/// lightness + chroma uniformly for all roles, so the only per-role
/// knobs are hue (role identity) and intensity (emphasis). Uniform
/// chroma is what gives the palette its Solarized / Nord / Gruvbox
/// cohesion.
struct ColorRole {
    hue: f64,            // target hue (0-360)
    intensity: Intensity,
}

impl ColorRole {
    /// The contrast floor this role's palette index must meet against
    /// the background. Dim accepts 3.5:1 (intentional low contrast);
    /// Normal and Bright demand the WCAG AA floor (4.5:1). The
    /// contrast audit uses this value.
    fn min_contrast(&self) -> f64 {
        match self.intensity {
            Intensity::Neutral | Intensity::Dim => 3.5,
            Intensity::Normal | Intensity::Bright => 4.5,
        }
    }
}

/// A tonal band — base (L, C) in OKLCH plus the ± step that Dim /
/// Bright use to shift lightness. Picked from the background
/// luminance so the whole palette agrees on "we are in the pastel
/// band" or "we are in the deep-muted band".
#[derive(Copy, Clone)]
struct TonalBand {
    base_l: f64,
    chroma: f64,
    step: f64,
}

fn tonal_band(bg_rgb: (f64, f64, f64)) -> TonalBand {
    let bg_lum = luminance(bg_rgb.0, bg_rgb.1, bg_rgb.2);
    // Four bands, picked so each gives ≥4.5:1 contrast at base_L
    // against the entire luminance window for that band. Pastel /
    // deep-muted are low chroma (palette feels airy / inky); mid
    // bands bump chroma to compensate for the squeezed L separation.
    if bg_lum <= 0.12 {
        // Deep dark bg: airy pastels sit way above
        TonalBand { base_l: 0.82, chroma: 0.12, step: 0.05 }
    } else if bg_lum <= 0.35 {
        // Mid-dark bg (typical terminal bg #1a1a1a .. #2d2d2d):
        // slightly higher L, a little more chroma for distinct pastels
        TonalBand { base_l: 0.84, chroma: 0.12, step: 0.05 }
    } else if bg_lum <= 0.65 {
        // Mid bg (#7a7a7a range): L separation is tight; pick the
        // side with more room and bump chroma so roles stay distinct
        if bg_lum < 0.5 {
            TonalBand { base_l: 0.92, chroma: 0.14, step: 0.04 }
        } else {
            TonalBand { base_l: 0.18, chroma: 0.14, step: 0.04 }
        }
    } else {
        // Light bg: deep muted inks sit way below
        TonalBand { base_l: 0.30, chroma: 0.12, step: 0.05 }
    }
}

/// Generate an sRGB color for a role given the background.
/// The tonal band fixes lightness and chroma; the role contributes
/// only hue and intensity. If the combination fails the role's
/// contrast floor (rare at band edges), L shifts away from the bg
/// until it passes.
fn generate_role_color(role: &ColorRole, bg_rgb: (f64, f64, f64)) -> (f64, f64, f64) {
    let bg_lum = luminance(bg_rgb.0, bg_rgb.1, bg_rgb.2);
    let (_, _, bg_h) = srgb_to_oklch(bg_rgb.0, bg_rgb.1, bg_rgb.2);
    let band = tonal_band(bg_rgb);
    let bg_is_darker = bg_lum < 0.5;

    // Intensity = "distance from bg". For dark bg, fg.L > bg.L, so
    // Bright raises L and Dim lowers it. For light bg it's inverted.
    let direction = if bg_is_darker { 1.0 } else { -1.0 };
    let intensity_offset = match role.intensity {
        Intensity::Neutral | Intensity::Dim => -band.step,
        Intensity::Normal => 0.0,
        Intensity::Bright => band.step,
    };
    let mut l = (band.base_l + direction * intensity_offset).clamp(0.10, 0.97);

    // Neutral roles (host, muted, time, comment, line-number) are
    // intentionally grayscale — force chroma to 0 so the role reads
    // as "structural non-color" even when the band is colorful.
    let chroma = if role.intensity == Intensity::Neutral { 0.0 } else { band.chroma };

    // Keep hue away from the bg hue so colored backgrounds don't
    // swallow a role that happens to share their hue. Neutrals have
    // zero chroma so hue is irrelevant.
    let hue = if chroma > 0.0 && hue_distance(role.hue, bg_h) < 35.0 {
        (role.hue + 50.0) % 360.0
    } else {
        role.hue
    };

    let min_cr = role.min_contrast();
    let l_step = 0.04;

    // Shift L away from bg until contrast floor is met (or clamped).
    for _ in 0..25 {
        let rgb = oklch_to_srgb(l, chroma, hue);
        let fg_lum = luminance(rgb.0, rgb.1, rgb.2);
        if contrast_ratio(fg_lum, bg_lum) >= min_cr {
            return rgb;
        }
        let shifted = l + direction * l_step;
        if !(0.08..=0.98).contains(&shifted) {
            break;
        }
        l = shifted;
    }

    // Hard fallback — near-white on dark bg, near-black on light bg.
    if bg_is_darker { (0.92, 0.92, 0.92) } else { (0.12, 0.12, 0.12) }
}

/// Generate 256-color code for a role, avoiding already-used indices.
fn role_to_256(role: &ColorRole, bg_rgb: (f64, f64, f64), avoid: &[u8]) -> u8 {
    let bg_lum = luminance(bg_rgb.0, bg_rgb.1, bg_rgb.2);
    let (r, g, b) = generate_role_color(role, bg_rgb);

    // Neutral roles map to the grayscale ramp directly. The general
    // quantizer's collision penalty penalizes grays for being "close
    // in OKLAB" to already-placed chromatic colors on the L axis,
    // which pushes them into wildly off-hue cells. For pure grays we
    // just want the gray ramp entry with the right luminance.
    if role.intensity == Intensity::Neutral {
        return nearest_grayscale(r, bg_lum, role.min_contrast(), avoid);
    }

    nearest_256_with_contrast(r, g, b, bg_lum, role.min_contrast(), avoid)
}

/// Pick a grayscale palette index (232-255, plus basic grays 8, 7, 15)
/// whose luminance meets the contrast floor against bg and whose
/// lightness is closest to `target_l` (sRGB gamma, 0..1). Used for
/// roles flagged Intensity::Neutral.
fn nearest_grayscale(target_l: f64, bg_lum: f64, min_cr: f64, avoid: &[u8]) -> u8 {
    let candidates: &[u8] = &[
        8, 7, 15, 16, 231, // basic grays + cube black/white
        232, 233, 234, 235, 236, 237, 238, 239, 240, 241, 242, 243,
        244, 245, 246, 247, 248, 249, 250, 251, 252, 253, 254, 255,
    ];
    let mut best: Option<u8> = None;
    let mut best_dist = f64::MAX;
    for &idx in candidates {
        if avoid.contains(&idx) {
            continue;
        }
        let (r, _, _) = palette_rgb(idx);
        let lum = luminance(r, r, r);
        if contrast_ratio(lum, bg_lum) < min_cr {
            continue;
        }
        let dist = (r - target_l).abs();
        if dist < best_dist {
            best_dist = dist;
            best = Some(idx);
        }
    }
    // Fallback when no gray meets contrast: hit the far extreme
    // (white on dark bg, black on light bg) so we at least stay legible.
    best.unwrap_or(if bg_lum < 0.5 { 231 } else { 16 })
}

// ── Semantic Color Roles ───────────────────────────────────────────

// OKLCH hues (approximate):
//   0   = pink/red
//   30  = orange
//   90  = yellow
//   140 = green
//   180 = teal/cyan
//   250 = blue
//   300 = purple
//   330 = magenta

// Prompt roles. Hues are fixed identity per role; intensity says
// "how loud is it vs. the tonal band base".
const ROLE_SUCCESS: ColorRole    = ColorRole { hue: 145.0, intensity: Intensity::Normal };
const ROLE_ERROR: ColorRole      = ColorRole { hue: 25.0,  intensity: Intensity::Normal };
const ROLE_WARNING: ColorRole    = ColorRole { hue: 80.0,  intensity: Intensity::Normal };
const ROLE_PATH: ColorRole       = ColorRole { hue: 250.0, intensity: Intensity::Normal };
const ROLE_USER: ColorRole       = ColorRole { hue: 300.0, intensity: Intensity::Normal };
const ROLE_HOST: ColorRole       = ColorRole { hue: 0.0,   intensity: Intensity::Neutral };
const ROLE_SSH_HOST: ColorRole   = ColorRole { hue: 80.0,  intensity: Intensity::Bright };
const ROLE_GIT_BRANCH: ColorRole = ColorRole { hue: 100.0, intensity: Intensity::Normal };
const ROLE_GIT_DIRTY: ColorRole  = ColorRole { hue: 55.0,  intensity: Intensity::Bright };
const ROLE_ROOT: ColorRole       = ColorRole { hue: 15.0,  intensity: Intensity::Bright };
const ROLE_MUTED: ColorRole      = ColorRole { hue: 0.0,   intensity: Intensity::Neutral };
const ROLE_TIME: ColorRole       = ColorRole { hue: 0.0,   intensity: Intensity::Neutral };

// Syntax highlighting roles
const ROLE_HL_KEYWORD: ColorRole  = ColorRole { hue: 330.0, intensity: Intensity::Normal };
const ROLE_HL_STRING: ColorRole   = ColorRole { hue: 135.0, intensity: Intensity::Normal };
const ROLE_HL_NUMBER: ColorRole   = ColorRole { hue: 180.0, intensity: Intensity::Normal };
const ROLE_HL_COMMAND: ColorRole  = ColorRole { hue: 240.0, intensity: Intensity::Normal };
const ROLE_HL_UNKNOWN: ColorRole  = ColorRole { hue: 0.0,   intensity: Intensity::Neutral };
const ROLE_HL_FLAG: ColorRole     = ColorRole { hue: 55.0,  intensity: Intensity::Normal };
const ROLE_HL_OPERATOR: ColorRole = ColorRole { hue: 300.0, intensity: Intensity::Normal };

// LS_COLORS roles
const ROLE_LS_DIR: ColorRole  = ColorRole { hue: 230.0, intensity: Intensity::Normal };
const ROLE_LS_LINK: ColorRole = ColorRole { hue: 310.0, intensity: Intensity::Normal };
const ROLE_LS_SOCK: ColorRole = ColorRole { hue: 280.0, intensity: Intensity::Dim };
const ROLE_LS_PIPE: ColorRole = ColorRole { hue: 55.0,  intensity: Intensity::Dim };
const ROLE_LS_EXEC: ColorRole = ColorRole { hue: 155.0, intensity: Intensity::Normal };
const ROLE_LS_BLK: ColorRole  = ColorRole { hue: 35.0,  intensity: Intensity::Dim };
const ROLE_LS_CHR: ColorRole  = ColorRole { hue: 35.0,  intensity: Intensity::Dim };

// GREP_COLORS roles
const ROLE_GREP_FN: ColorRole = ColorRole { hue: 310.0, intensity: Intensity::Normal };
const ROLE_GREP_LN: ColorRole = ColorRole { hue: 0.0,   intensity: Intensity::Neutral };
const ROLE_GREP_BN: ColorRole = ColorRole { hue: 0.0,   intensity: Intensity::Neutral };
const ROLE_GREP_SE: ColorRole = ColorRole { hue: 0.0,   intensity: Intensity::Neutral };

// ── Theme ───────────────────────────────────────────────────────────

/// Resolved theme with ANSI escape codes for all UI elements.
#[derive(Debug, Clone)]
pub struct Theme {
    pub is_dark: bool,
    pub bg_rgb: Option<(f64, f64, f64)>,

    // Prompt
    pub prompt_success: String,
    pub prompt_failed: String,
    pub prompt_time: String,
    pub prompt_user: String,
    pub prompt_host: String,
    pub prompt_ssh_host: String,
    pub prompt_path: String,
    pub prompt_git_branch: String,
    pub prompt_git_dirty: String,
    pub prompt_root: String,
    pub muted: String,
    pub error: String,
    pub warning: String,
    pub reset: String,

    // Syntax highlighting
    pub hl_keyword: String,
    pub hl_string: String,
    pub hl_number: String,
    pub hl_command: String,
    pub hl_unknown_cmd: String,
    pub hl_flag: String,
    pub hl_operator: String,
    pub hl_pipe: String,
    pub hl_comment: String,
}

impl Theme {
    /// Get the 256-color index for the muted/hint color.
    pub fn muted_color_index(&self) -> u8 {
        if let Some(bg) = self.bg_rgb {
            role_to_256(&ROLE_MUTED, bg, &[])
        } else if self.is_dark {
            250 // light gray
        } else {
            240 // dark gray
        }
    }

    /// Build a theme for the given background.
    /// When bg_rgb is known, all colors are generated via OKLCH for
    /// guaranteed contrast. Without bg, returns a passive theme that
    /// emits no color codes — so the user's terminal keeps its own
    /// palette and defaults. Theming is opt-in via RUSH_BG, .rushbg,
    /// or config.bg (surfaced through setbg / setbg --save / setbg --local).
    pub fn new(is_dark: bool, bg_rgb: Option<(f64, f64, f64)>) -> Self {
        let reset = "\x1b[0m".to_string();

        if let Some(bg) = bg_rgb {
            Self::build_oklch(is_dark, bg, &reset)
        } else {
            Self::passive()
        }
    }

    /// Passive theme: every role is the empty string, so rush emits no
    /// color codes at all. The prompt, syntax highlighter, LS_COLORS,
    /// and GREP_COLORS all become no-ops and the terminal's own
    /// defaults show through. Active theming requires explicit opt-in.
    pub fn passive() -> Self {
        let empty = String::new();
        Self {
            is_dark: false, bg_rgb: None,
            prompt_success: empty.clone(),
            prompt_failed: empty.clone(),
            prompt_time: empty.clone(),
            prompt_user: empty.clone(),
            prompt_host: empty.clone(),
            prompt_ssh_host: empty.clone(),
            prompt_path: empty.clone(),
            prompt_git_branch: empty.clone(),
            prompt_git_dirty: empty.clone(),
            prompt_root: empty.clone(),
            muted: empty.clone(),
            error: empty.clone(),
            warning: empty.clone(),
            reset: empty.clone(),
            hl_keyword: empty.clone(),
            hl_string: empty.clone(),
            hl_number: empty.clone(),
            hl_command: empty.clone(),
            hl_unknown_cmd: empty.clone(),
            hl_flag: empty.clone(),
            hl_operator: empty.clone(),
            hl_pipe: empty.clone(),
            hl_comment: empty,
        }
    }

    /// Whether this theme emits any color codes. Passive themes return
    /// false; OKLCH-computed themes return true.
    pub fn is_active(&self) -> bool {
        self.bg_rgb.is_some()
    }

    /// Build theme with OKLCH-generated colors, all contrast-validated.
    /// Generates primary roles first to claim distinct palette slots,
    /// then secondary roles avoid collisions with the primaries.
    fn build_oklch(is_dark: bool, bg: (f64, f64, f64), reset: &str) -> Self {
        let mut used = Vec::new();
        let mut pick = |role: &ColorRole| -> String {
            let idx = role_to_256(role, bg, &used);
            used.push(idx);
            format!("\x1b[38;5;{idx}m")
        };

        // Generate in priority order: most important distinctions first.
        // Each pick claims a palette slot; subsequent picks avoid it.
        let prompt_success = pick(&ROLE_SUCCESS);
        let prompt_failed = pick(&ROLE_ERROR);
        let prompt_path = pick(&ROLE_PATH);
        let prompt_user = pick(&ROLE_USER);
        let muted = pick(&ROLE_MUTED);
        let prompt_warning = pick(&ROLE_WARNING);
        let prompt_host = pick(&ROLE_HOST);
        let prompt_ssh_host = pick(&ROLE_SSH_HOST);
        let prompt_git_branch = pick(&ROLE_GIT_BRANCH);
        let prompt_git_dirty = pick(&ROLE_GIT_DIRTY);
        let prompt_root = pick(&ROLE_ROOT);
        let time = pick(&ROLE_TIME);

        // Syntax highlighting — distinct from each other and from prompt
        let hl_keyword = pick(&ROLE_HL_KEYWORD);
        let hl_string = pick(&ROLE_HL_STRING);
        let hl_command = pick(&ROLE_HL_COMMAND);
        let hl_number = pick(&ROLE_HL_NUMBER);
        let hl_flag = pick(&ROLE_HL_FLAG);
        let hl_operator = pick(&ROLE_HL_OPERATOR);
        let hl_unknown = pick(&ROLE_HL_UNKNOWN);

        Self {
            is_dark,
            bg_rgb: Some(bg),
            prompt_success,
            prompt_failed: prompt_failed.clone(),
            prompt_time: time,
            prompt_user,
            prompt_host,
            prompt_ssh_host,
            prompt_path,
            prompt_git_branch,
            prompt_git_dirty,
            prompt_root,
            muted: muted.clone(),
            error: prompt_failed,
            warning: prompt_warning,
            reset: reset.into(),
            hl_keyword,
            hl_string,
            hl_number,
            hl_command,
            hl_unknown_cmd: hl_unknown,
            hl_flag,
            hl_operator,
            hl_pipe: muted.clone(),
            hl_comment: muted,
        }
    }

    /// Kept for reference — unused. Passive mode is used instead when
    /// no background opt-in signal is present. Delete if it stays
    /// unused through the next release.
    #[allow(dead_code)]
    fn build_fallback(is_dark: bool, reset: &str) -> Self {
        if is_dark {
            Self {
                is_dark, bg_rgb: None,
                prompt_success: "\x1b[32m".into(), prompt_failed: "\x1b[91m".into(),
                prompt_time: "\x1b[37m".into(), prompt_user: "\x1b[36m".into(),
                prompt_host: "\x1b[37m".into(), prompt_ssh_host: "\x1b[93m".into(),
                prompt_path: "\x1b[92m".into(), prompt_git_branch: "\x1b[33m".into(),
                prompt_git_dirty: "\x1b[93m".into(), prompt_root: "\x1b[91m".into(),
                muted: "\x1b[37m".into(), error: "\x1b[91m".into(), warning: "\x1b[33m".into(),
                reset: reset.into(),
                hl_keyword: "\x1b[38;5;204m".into(), hl_string: "\x1b[32m".into(),
                hl_number: "\x1b[36m".into(), hl_command: "\x1b[96m".into(),
                hl_unknown_cmd: "\x1b[37m".into(), hl_flag: "\x1b[33m".into(),
                hl_operator: "\x1b[35m".into(), hl_pipe: "\x1b[37m".into(),
                hl_comment: "\x1b[37m".into(),
            }
        } else {
            Self {
                is_dark, bg_rgb: None,
                prompt_success: "\x1b[32m".into(), prompt_failed: "\x1b[31m".into(),
                prompt_time: "\x1b[90m".into(), prompt_user: "\x1b[34m".into(),
                prompt_host: "\x1b[90m".into(), prompt_ssh_host: "\x1b[33m".into(),
                prompt_path: "\x1b[34m".into(), prompt_git_branch: "\x1b[33m".into(),
                prompt_git_dirty: "\x1b[33m".into(), prompt_root: "\x1b[31m".into(),
                muted: "\x1b[90m".into(), error: "\x1b[31m".into(), warning: "\x1b[33m".into(),
                reset: reset.into(),
                hl_keyword: "\x1b[38;5;161m".into(), hl_string: "\x1b[32m".into(),
                hl_number: "\x1b[36m".into(), hl_command: "\x1b[34m".into(),
                hl_unknown_cmd: "\x1b[30m".into(), hl_flag: "\x1b[38;5;130m".into(),
                hl_operator: "\x1b[35m".into(), hl_pipe: "\x1b[90m".into(),
                hl_comment: "\x1b[90m".into(),
            }
        }
    }
}

// ── LS_COLORS / GREP_COLORS Generation ──────────────────────────────

/// Generate 256-color LS_COLORS optimized for the background via OKLCH.
pub fn generate_ls_colors(theme: &Theme) -> String {
    let bg = theme.bg_rgb.unwrap_or(if theme.is_dark { (0.0, 0.0, 0.0) } else { (1.0, 1.0, 1.0) });
    let roles: &[(&str, &ColorRole)] = &[
        ("di", &ROLE_LS_DIR),
        ("ln", &ROLE_LS_LINK),
        ("so", &ROLE_LS_SOCK),
        ("pi", &ROLE_LS_PIPE),
        ("ex", &ROLE_LS_EXEC),
        ("bd", &ROLE_LS_BLK),
        ("cd", &ROLE_LS_CHR),
    ];

    let mut used = Vec::new();
    let mut entries: Vec<String> = roles.iter()
        .map(|(key, role)| {
            let idx = role_to_256(role, bg, &used);
            used.push(idx);
            format!("{key}=38;5;{idx}")
        })
        .collect();

    // Special entries (fixed)
    entries.push("su=37;41".into());
    entries.push("sg=30;43".into());
    entries.push("tw=30;42".into());
    entries.push("ow=34;42".into());

    entries.join(":")
}

/// Generate 256-color GREP_COLORS optimized for the background via OKLCH.
pub fn generate_grep_colors(theme: &Theme) -> String {
    let bg = theme.bg_rgb.unwrap_or(if theme.is_dark { (0.0, 0.0, 0.0) } else { (1.0, 1.0, 1.0) });
    let roles = [
        ("fn", &ROLE_GREP_FN),
        ("ln", &ROLE_GREP_LN),
        ("bn", &ROLE_GREP_BN),
        ("se", &ROLE_GREP_SE),
    ];

    let mut entries = vec![
        "ms=01;31".to_string(),
        "mc=01;31".to_string(),
        "sl=".to_string(),
        "cx=".to_string(),
    ];

    let mut used = Vec::new();
    for (key, role) in &roles {
        let idx = role_to_256(role, bg, &used);
        used.push(idx);
        entries.push(format!("{key}=38;5;{idx}"));
    }

    entries.join(":")
}

/// Generate BSD LSCOLORS string (macOS).
pub fn generate_lscolors(theme: &Theme) -> String {
    if theme.is_dark {
        "Gxfxcxdxbxegedabagacad".to_string()
    } else {
        "gxfxcxdxbxegedabagacad".to_string()
    }
}

// ── setbg ───────────────────────────────────────────────────────────

/// Set terminal background via OSC 11 and re-theme.
pub fn set_background(hex: &str, emit_osc: bool) -> Option<Theme> {
    let (r, g, b) = parse_hex(hex)?;
    let is_dark = luminance(r, g, b) <= 0.179;

    if emit_osc {
        let hex_clean = hex.trim_start_matches('#');
        print!("\x1b]11;#{hex_clean}\x07");
        let fg = if is_dark { "ffffff" } else { "000000" };
        print!("\x1b]10;#{fg}\x07");
        use std::io::Write;
        std::io::stdout().flush().ok();
    }

    unsafe { std::env::set_var("RUSH_BG", hex) };

    Some(Theme::new(is_dark, Some((r, g, b))))
}

// ── Detection + Initialization ──────────────────────────────────────

/// Pick a theme based on explicit user opt-in. Theming is OFF by
/// default — rush doesn't guess at the terminal background and doesn't
/// inject color codes. The user turns it on via:
///
///   * `RUSH_BG=#RRGGBB` environment variable
///   * `.rushbg` file in the project directory (read by the REPL and
///     promoted to RUSH_BG before this runs)
///   * `setbg <hex>` in an interactive session, which also writes
///     RUSH_BG in the running process
///   * `set --save bg <hex>` in config.json (promoted to RUSH_BG by the
///     REPL before this runs)
///
/// When none of those are present, we return a passive theme that
/// emits no color codes and leaves the terminal alone.
pub fn detect() -> Theme {
    if let Ok(bg) = std::env::var("RUSH_BG") {
        if let Some((r, g, b)) = parse_hex(&bg) {
            let is_dark = luminance(r, g, b) <= 0.179;
            return Theme::new(is_dark, Some((r, g, b)));
        }
    }
    Theme::passive()
}

/// Set LS_COLORS, LSCOLORS, GREP_COLORS, CLICOLOR env vars.
pub fn set_native_color_env_vars(theme: &Theme) {
    if std::env::var("NO_COLOR").is_ok() { return; }

    unsafe {
        std::env::set_var("LS_COLORS", generate_ls_colors(theme));
        std::env::set_var("LSCOLORS", generate_lscolors(theme));
        std::env::set_var("GREP_COLORS", generate_grep_colors(theme));
        std::env::set_var("CLICOLOR", "1");
        std::env::set_var("CLICOLOR_FORCE", "1");
    }
}

/// Initialize theme: detect, set env vars, return theme.
/// Passive themes don't touch LS_COLORS / GREP_COLORS / CLICOLOR —
/// those remain whatever the parent shell or terminal left them.
pub fn initialize() -> Theme {
    let theme = detect();
    if theme.is_active() {
        set_native_color_env_vars(&theme);
    }
    theme
}

// ── .rushbg support ─────────────────────────────────────────────────

/// Check for .rushbg file in current directory (per-project background).
pub fn load_rushbg() -> Option<String> {
    std::fs::read_to_string(".rushbg").ok().map(|s| s.trim().to_string()).filter(|s| !s.is_empty())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn luminance_black() {
        assert!(luminance(0.0, 0.0, 0.0) < 0.01);
    }

    #[test]
    fn luminance_white() {
        assert!(luminance(1.0, 1.0, 1.0) > 0.99);
    }

    #[test]
    fn contrast_black_white() {
        let l1 = luminance(0.0, 0.0, 0.0);
        let l2 = luminance(1.0, 1.0, 1.0);
        let cr = contrast_ratio(l1, l2);
        assert!(cr > 20.0);
    }

    #[test]
    fn parse_hex_valid() {
        let (r, g, b) = parse_hex("#FF8000").unwrap();
        assert!((r - 1.0).abs() < 0.01);
        assert!((g - 0.502).abs() < 0.01);
        assert!(b < 0.01);
    }

    #[test]
    fn parse_hex_no_hash() {
        assert!(parse_hex("FF8000").is_some());
    }

    #[test]
    fn parse_hex_invalid() {
        assert!(parse_hex("nope").is_none());
        assert!(parse_hex("#FFF").is_none());
    }

    #[test]
    fn oklch_roundtrip() {
        // Test that sRGB → OKLCH → sRGB roundtrips reasonably
        let colors = [(1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (0.5, 0.5, 0.5)];
        for (r, g, b) in colors {
            let (l, c, h) = srgb_to_oklch(r, g, b);
            let (r2, g2, b2) = oklch_to_srgb(l, c, h);
            assert!((r - r2).abs() < 0.02, "r: {r} vs {r2}");
            assert!((g - g2).abs() < 0.02, "g: {g} vs {g2}");
            assert!((b - b2).abs() < 0.02, "b: {b} vs {b2}");
        }
    }

    #[test]
    fn oklch_gamut_clamp() {
        // Very high chroma should be clamped to gamut
        let (r, g, b) = oklch_to_srgb(0.7, 0.4, 120.0);
        assert!(r >= 0.0 && r <= 1.0);
        assert!(g >= 0.0 && g <= 1.0);
        assert!(b >= 0.0 && b <= 1.0);
    }

    #[test]
    fn role_contrast_dark_bg() {
        let bg = (0.1, 0.1, 0.1);
        let bg_lum = luminance(bg.0, bg.1, bg.2);
        let roles = [&ROLE_SUCCESS, &ROLE_ERROR, &ROLE_PATH, &ROLE_HL_KEYWORD, &ROLE_HL_COMMAND];
        for role in roles {
            let (r, g, b) = generate_role_color(role, bg);
            let fg_lum = luminance(r, g, b);
            let cr = contrast_ratio(fg_lum, bg_lum);
            assert!(cr >= role.min_contrast() - 0.5,
                "hue={} contrast {cr:.1} < min {} on dark bg", role.hue, role.min_contrast());
        }
    }

    #[test]
    fn role_contrast_light_bg() {
        let bg = (0.95, 0.95, 0.95);
        let bg_lum = luminance(bg.0, bg.1, bg.2);
        let roles = [&ROLE_SUCCESS, &ROLE_ERROR, &ROLE_PATH, &ROLE_HL_KEYWORD, &ROLE_HL_COMMAND];
        for role in roles {
            let (r, g, b) = generate_role_color(role, bg);
            let fg_lum = luminance(r, g, b);
            let cr = contrast_ratio(fg_lum, bg_lum);
            assert!(cr >= role.min_contrast() - 0.5,
                "hue={} contrast {cr:.1} < min {} on light bg", role.hue, role.min_contrast());
        }
    }

    #[test]
    fn role_contrast_mid_gray() {
        // The problematic zone — mid-gray backgrounds
        let bg = (0.4, 0.4, 0.4);
        let bg_lum = luminance(bg.0, bg.1, bg.2);
        let roles = [&ROLE_SUCCESS, &ROLE_ERROR, &ROLE_PATH];
        for role in roles {
            let (r, g, b) = generate_role_color(role, bg);
            let fg_lum = luminance(r, g, b);
            let cr = contrast_ratio(fg_lum, bg_lum);
            assert!(cr >= 3.0,
                "hue={} contrast {cr:.1} < 3.0 on mid-gray bg", role.hue);
        }
    }

    #[test]
    fn role_contrast_blue_bg() {
        // Blue background — path (also blue) should shift away
        let bg = (0.1, 0.1, 0.4);
        let bg_lum = luminance(bg.0, bg.1, bg.2);
        let (r, g, b) = generate_role_color(&ROLE_PATH, bg);
        let fg_lum = luminance(r, g, b);
        let cr = contrast_ratio(fg_lum, bg_lum);
        assert!(cr >= 3.0, "path on blue bg: contrast {cr:.1} < 3.0");
    }

    #[test]
    fn nearest_256_finds_close_match() {
        // Pure red should map to something reddish
        let idx = nearest_256(1.0, 0.0, 0.0);
        let (r, _g, _b) = palette_rgb(idx);
        assert!(r > 0.5, "nearest to red should be reddish, got idx {idx}");
    }

    #[test]
    fn palette_rgb_bounds() {
        for i in 0..=255u8 {
            let (r, g, b) = palette_rgb(i);
            assert!(r >= 0.0 && r <= 1.0, "idx {i}: r={r}");
            assert!(g >= 0.0 && g <= 1.0, "idx {i}: g={g}");
            assert!(b >= 0.0 && b <= 1.0, "idx {i}: b={b}");
        }
    }

    #[test]
    fn ls_colors_has_all_slots() {
        let theme = Theme::new(true, Some((0.05, 0.05, 0.1)));
        let ls = generate_ls_colors(&theme);
        assert!(ls.contains("di="), "missing di");
        assert!(ls.contains("ln="), "missing ln");
        assert!(ls.contains("ex="), "missing ex");
        assert!(ls.contains("su="), "missing su");
    }

    #[test]
    fn grep_colors_has_match() {
        let theme = Theme::new(true, Some((0.05, 0.05, 0.1)));
        let gc = generate_grep_colors(&theme);
        assert!(gc.contains("ms="), "missing ms");
        assert!(gc.contains("fn="), "missing fn");
    }

    #[test]
    fn theme_with_bg_is_active() {
        // Opt-in path: a known bg RGB yields a fully-populated OKLCH
        // theme with ANSI escape codes.
        let t = Theme::new(true, Some((0.1, 0.1, 0.15)));
        assert!(t.is_active());
        assert!(t.bg_rgb.is_some());
        assert!(t.prompt_success.contains("\x1b["));
    }

    #[test]
    fn theme_without_bg_is_passive() {
        // Default path: no bg → passive theme, every role is empty,
        // rush emits zero color codes and the terminal keeps its own.
        let t = Theme::new(true, None);
        assert!(!t.is_active());
        assert!(t.bg_rgb.is_none());
        assert!(t.prompt_success.is_empty());
        assert!(t.reset.is_empty());
    }

    #[test]
    fn setbg_returns_theme() {
        let theme = set_background("#282828", false).unwrap();
        assert!(theme.is_dark);
        assert!(theme.bg_rgb.is_some());
    }

    #[test]
    fn setbg_light() {
        let theme = set_background("#F5F5F5", false).unwrap();
        assert!(!theme.is_dark);
    }

    #[test]
    fn setbg_flip_point() {
        // 18% gray (#303030 ≈ luminance 0.03) should be dark
        let dark = set_background("#303030", false).unwrap();
        assert!(dark.is_dark);
        // 50% gray (#808080 ≈ luminance 0.22) should be light (above 0.179)
        let light = set_background("#808080", false).unwrap();
        assert!(!light.is_dark);
    }

    // ── CIEDE2000 reference pairs ────────────────────────────────────
    //
    // Reference values from Sharma, Wu, Dalal "The CIEDE2000 Color-
    // Difference Formula: Implementation Notes, Supplementary Test
    // Data, and Mathematical Observations" (Color Res. Appl. 30, 21-30
    // 2005), Table 1. These are the canonical test vectors used to
    // validate CIEDE2000 implementations.

    fn assert_de_close(got: f64, want: f64, tag: &str) {
        let tol = 0.01;
        assert!(
            (got - want).abs() < tol,
            "ΔE2000 {tag}: got {got:.4}, want {want:.4} (tol {tol})"
        );
    }

    #[test]
    fn ciede2000_reference_pair_1() {
        // Sharma et al. row 1: saturated blues
        let got = ciede2000((50.0, 2.6772, -79.7751), (50.0, 0.0, -82.7485));
        assert_de_close(got, 2.0425, "row 1");
    }

    #[test]
    fn ciede2000_reference_pair_2() {
        let got = ciede2000((50.0, 3.1571, -77.2803), (50.0, 0.0, -82.7485));
        assert_de_close(got, 2.8615, "row 2");
    }

    #[test]
    fn ciede2000_reference_pair_3() {
        let got = ciede2000((50.0, 2.8361, -74.0200), (50.0, 0.0, -82.7485));
        assert_de_close(got, 3.4412, "row 3");
    }

    #[test]
    fn ciede2000_large_difference() {
        // Equal-L complementary a* sanity check — not a reference
        // vector, just a guard that ΔE grows with hue separation.
        let got = ciede2000((50.0, 50.0, 0.0), (50.0, -50.0, 0.0));
        assert!(got > 50.0, "expected large ΔE for complementary, got {got}");
    }

    #[test]
    fn ciede2000_identical_is_zero() {
        let got = ciede2000((53.0, 20.0, -30.0), (53.0, 20.0, -30.0));
        assert!(got < 1e-9, "identical colors should have ΔE 0, got {got}");
    }

    #[test]
    fn srgb_to_cielab_white_is_100() {
        // sRGB white should map to L*=100, a*≈0, b*≈0 under D65.
        let (l, a, b) = srgb_to_cielab(1.0, 1.0, 1.0);
        assert!((l - 100.0).abs() < 0.1, "L* should be 100, got {l}");
        assert!(a.abs() < 0.5, "a* should be ~0, got {a}");
        assert!(b.abs() < 0.5, "b* should be ~0, got {b}");
    }

    #[test]
    fn rushbg_nonexistent() {
        let _ = load_rushbg();
    }

    // ── Comprehensive contrast audit ───────────────────────────────

    struct RoleSpec {
        name: &'static str,
        role: &'static ColorRole,
    }

    const ALL_ROLES: &[RoleSpec] = &[
        // Prompt
        RoleSpec { name: "prompt_success",    role: &ROLE_SUCCESS },
        RoleSpec { name: "prompt_error",      role: &ROLE_ERROR },
        RoleSpec { name: "prompt_warning",    role: &ROLE_WARNING },
        RoleSpec { name: "prompt_path",       role: &ROLE_PATH },
        RoleSpec { name: "prompt_user",       role: &ROLE_USER },
        RoleSpec { name: "prompt_host",       role: &ROLE_HOST },
        RoleSpec { name: "prompt_ssh_host",   role: &ROLE_SSH_HOST },
        RoleSpec { name: "prompt_git_branch", role: &ROLE_GIT_BRANCH },
        RoleSpec { name: "prompt_git_dirty",  role: &ROLE_GIT_DIRTY },
        RoleSpec { name: "prompt_root",       role: &ROLE_ROOT },
        RoleSpec { name: "muted",             role: &ROLE_MUTED },
        RoleSpec { name: "time",              role: &ROLE_TIME },
        // Syntax
        RoleSpec { name: "hl_keyword",        role: &ROLE_HL_KEYWORD },
        RoleSpec { name: "hl_string",         role: &ROLE_HL_STRING },
        RoleSpec { name: "hl_number",         role: &ROLE_HL_NUMBER },
        RoleSpec { name: "hl_command",        role: &ROLE_HL_COMMAND },
        RoleSpec { name: "hl_unknown",        role: &ROLE_HL_UNKNOWN },
        RoleSpec { name: "hl_flag",           role: &ROLE_HL_FLAG },
        RoleSpec { name: "hl_operator",       role: &ROLE_HL_OPERATOR },
        // LS_COLORS
        RoleSpec { name: "ls_dir",            role: &ROLE_LS_DIR },
        RoleSpec { name: "ls_link",           role: &ROLE_LS_LINK },
        RoleSpec { name: "ls_sock",           role: &ROLE_LS_SOCK },
        RoleSpec { name: "ls_pipe",           role: &ROLE_LS_PIPE },
        RoleSpec { name: "ls_exec",           role: &ROLE_LS_EXEC },
        RoleSpec { name: "ls_blk",            role: &ROLE_LS_BLK },
        RoleSpec { name: "ls_chr",            role: &ROLE_LS_CHR },
        // GREP_COLORS
        RoleSpec { name: "grep_fn",           role: &ROLE_GREP_FN },
        RoleSpec { name: "grep_ln",           role: &ROLE_GREP_LN },
        RoleSpec { name: "grep_bn",           role: &ROLE_GREP_BN },
        RoleSpec { name: "grep_se",           role: &ROLE_GREP_SE },
    ];

    /// Test backgrounds: dark, mid-gray, light, plus problem colors
    const TEST_BACKGROUNDS: &[(&str, f64, f64, f64)] = &[
        // Dark backgrounds
        ("black #000000",         0.0,   0.0,   0.0),
        ("near-black #1a1a1a",    0.102, 0.102, 0.102),
        ("dark gray #282828",     0.157, 0.157, 0.157),
        ("gruvbox dark #282828",  0.157, 0.157, 0.157),
        ("solarized dark #002b36",0.0,   0.169, 0.212),
        ("dark blue #253B51",     0.145, 0.231, 0.318),
        ("dark green #1a2e1a",    0.102, 0.180, 0.102),
        ("dark red #2e1a1a",      0.180, 0.102, 0.102),
        ("dark purple #2a1a2e",   0.165, 0.102, 0.180),
        // Mid-tone (the danger zone)
        ("mid gray #666666",      0.400, 0.400, 0.400),
        ("mid blue #4466aa",      0.267, 0.400, 0.667),
        ("mid green #446644",     0.267, 0.400, 0.267),
        // Light backgrounds
        ("light gray #c0c0c0",    0.753, 0.753, 0.753),
        ("solarized light #fdf6e3", 0.992, 0.965, 0.890),
        ("white #f5f5f5",         0.961, 0.961, 0.961),
        ("pure white #ffffff",    1.0,   1.0,   1.0),
    ];

    fn rgb_to_hex(r: f64, g: f64, b: f64) -> String {
        format!("#{:02X}{:02X}{:02X}",
            (r * 255.0).round() as u8,
            (g * 255.0).round() as u8,
            (b * 255.0).round() as u8)
    }

    /// Comprehensive contrast audit across all backgrounds and all roles.
    /// Reports every failure with specific colors and contrast ratios.
    #[test]
    fn contrast_audit_all_backgrounds() {
        let mut failures: Vec<String> = Vec::new();
        let mut total_checks = 0;

        for &(bg_name, bg_r, bg_g, bg_b) in TEST_BACKGROUNDS {
            let bg = (bg_r, bg_g, bg_b);
            let bg_lum = luminance(bg_r, bg_g, bg_b);
            let mut used = Vec::new();

            for spec in ALL_ROLES {
                total_checks += 1;
                let idx = role_to_256(spec.role, bg, &used);
                used.push(idx);
                let (fr, fg, fb) = palette_rgb(idx);
                let fg_lum = luminance(fr, fg, fb);
                let cr = contrast_ratio(fg_lum, bg_lum);

                if cr < spec.role.min_contrast() {
                    failures.push(format!(
                        "  FAIL: bg={bg_name} role={:<18} fg=idx {:>3} {} cr={:.2}:1 (need {:.1}:1)",
                        spec.name,
                        idx,
                        rgb_to_hex(fr, fg, fb),
                        cr,
                        spec.role.min_contrast()
                    ));
                }
            }
        }

        if !failures.is_empty() {
            let report = format!(
                "\n╔══════════════════════════════════════════════════════════════╗\n\
                 ║  CONTRAST AUDIT: {} failures out of {} checks              \n\
                 ╚══════════════════════════════════════════════════════════════╝\n\
                 {}\n",
                failures.len(),
                total_checks,
                failures.join("\n")
            );
            panic!("{report}");
        }
    }

    /// Check that primary prompt roles don't collide with each other
    /// (too similar in color on the same background).
    #[test]
    fn primary_roles_distinct() {
        let primary_roles: &[(&str, &ColorRole)] = &[
            ("success", &ROLE_SUCCESS),
            ("error",   &ROLE_ERROR),
            ("path",    &ROLE_PATH),
            ("user",    &ROLE_USER),
            ("muted",   &ROLE_MUTED),
        ];

        let mut collisions: Vec<String> = Vec::new();

        for &(bg_name, bg_r, bg_g, bg_b) in TEST_BACKGROUNDS {
            let bg = (bg_r, bg_g, bg_b);
            // Simulate sequential generation (same as build_oklch)
            let mut used = Vec::new();
            let colors: Vec<(&str, u8)> = primary_roles.iter()
                .map(|(name, role)| {
                    let idx = role_to_256(role, bg, &used);
                    used.push(idx);
                    (*name, idx)
                })
                .collect();

            for i in 0..colors.len() {
                for j in (i + 1)..colors.len() {
                    let (name_a, idx_a) = colors[i];
                    let (name_b, idx_b) = colors[j];
                    if idx_a == idx_b {
                        collisions.push(format!(
                            "  COLLISION: bg={bg_name} {name_a} and {name_b} both use idx {idx_a}"
                        ));
                        continue;
                    }
                    let (ra, ga, ba) = palette_rgb(idx_a);
                    let (rb, gb, bb) = palette_rgb(idx_b);
                    let lum_a = luminance(ra, ga, ba);
                    let lum_b = luminance(rb, gb, bb);
                    let cr = contrast_ratio(lum_a, lum_b);
                    // Perceptual distance via ΔE2000 — industry standard
                    // for "are these visibly the same color?" (#228).
                    let de = ciede2000_srgb((ra, ga, ba), (rb, gb, bb));
                    // Colors are "too similar" only if BOTH luminance AND
                    // hue are close. ΔE2000 < 5 is visibly similar; the
                    // cr < 1.3 guard keeps different-hue-same-luminance
                    // pairs (which the 256 palette sometimes forces) from
                    // tripping the audit.
                    if cr < 1.3 && de < 5.0 {
                        collisions.push(format!(
                            "  TOO SIMILAR: bg={bg_name} {name_a}(idx {idx_a} {}) vs {name_b}(idx {idx_b} {}) cr={cr:.2}:1 ΔE2000={de:.2}",
                            rgb_to_hex(ra, ga, ba),
                            rgb_to_hex(rb, gb, bb),
                        ));
                    }
                }
            }
        }

        if !collisions.is_empty() {
            let report = format!(
                "\n╔══════════════════════════════════════════════════════════════╗\n\
                 ║  COLLISION AUDIT: {} issues                                  \n\
                 ╚══════════════════════════════════════════════════════════════╝\n\
                 {}\n",
                collisions.len(),
                collisions.join("\n")
            );
            panic!("{report}");
        }
    }

    /// Printable report (not a pass/fail test) showing all color assignments.
    /// Run with: cargo test -p rush-core -- contrast_report --nocapture
    #[test]
    fn contrast_report() {
        println!("\n{:=<80}", "");
        println!("OKLCH THEME CONTRAST REPORT");
        println!("{:=<80}", "");

        for &(bg_name, bg_r, bg_g, bg_b) in TEST_BACKGROUNDS {
            let bg = (bg_r, bg_g, bg_b);
            let bg_lum = luminance(bg_r, bg_g, bg_b);
            let is_dark = bg_lum <= 0.179;

            println!("\n  bg: {bg_name} (lum={bg_lum:.3}, {})", if is_dark { "DARK" } else { "LIGHT" });
            println!("  {:<18} {:>4} {:>8} {:>8}  {}", "role", "idx", "hex", "cr", "status");
            println!("  {:-<60}", "");

            let mut used = Vec::new();
            for spec in ALL_ROLES {
                let idx = role_to_256(spec.role, bg, &used);
                used.push(idx);
                let (fr, fg, fb) = palette_rgb(idx);
                let fg_lum = luminance(fr, fg, fb);
                let cr = contrast_ratio(fg_lum, bg_lum);
                let status = if cr >= spec.role.min_contrast() { "✓" }
                    else if cr >= spec.role.min_contrast() - 0.5 { "~" }
                    else { "✗" };

                println!("  {:<18} {:>4} {:>8} {:>7.2}:1  {}",
                    spec.name, idx, rgb_to_hex(fr, fg, fb), cr, status);
            }
        }
        println!("\n{:=<80}", "");
    }
}
