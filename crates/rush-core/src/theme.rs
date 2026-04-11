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

/// Find the nearest 256-color index that meets minimum contrast against bg.
/// Prioritizes hue fidelity among colors that pass contrast.
fn nearest_256_with_contrast(r: f64, g: f64, b: f64, bg_lum: f64, min_cr: f64) -> u8 {
    let (target_l, target_a, target_b) = srgb_to_oklab(r, g, b);
    let mut best_idx: Option<u8> = None;
    let mut best_dist = f64::MAX;

    // Search all 240 extended colors for the closest match that passes contrast
    for idx in 16..=255u8 {
        let (pr, pg, pb) = palette_rgb(idx);
        let lum = luminance(pr, pg, pb);
        if contrast_ratio(lum, bg_lum) < min_cr {
            continue;
        }
        let (pl, pa, pb_ok) = srgb_to_oklab(pr, pg, pb);
        let dist = (target_l - pl).powi(2) + (target_a - pa).powi(2) + (target_b - pb_ok).powi(2);
        if dist < best_dist {
            best_dist = dist;
            best_idx = Some(idx);
        }
    }

    // Hard fallback: white on dark, black on light
    best_idx.unwrap_or(if bg_lum < 0.5 { 15 } else { 0 })
}

// ── OKLCH Palette Generation ───────────────────────────────────────

/// A semantic color role with its OKLCH parameters.
struct ColorRole {
    hue: f64,       // target hue (0-360)
    chroma: f64,    // max chroma (vibrancy cap)
    min_contrast: f64,
}

/// Generate an sRGB color for a role given the background.
/// Picks lightness to achieve contrast, locks hue and caps chroma.
fn generate_role_color(role: &ColorRole, bg_rgb: (f64, f64, f64)) -> (f64, f64, f64) {
    let bg_lum = luminance(bg_rgb.0, bg_rgb.1, bg_rgb.2);
    let (bg_l, _, bg_h) = srgb_to_oklch(bg_rgb.0, bg_rgb.1, bg_rgb.2);
    let is_dark = bg_lum <= 0.179;

    // If the role hue is too close to the bg hue, shift it away
    let hue = if role.chroma > 0.01 && hue_distance(role.hue, bg_h) < 35.0 {
        (role.hue + 50.0) % 360.0
    } else {
        role.hue
    };

    // Search the full lightness range on the correct side of the background.
    // For dark bg: search from bg_l upward (lighter foregrounds).
    // For light bg: search from bg_l downward (darker foregrounds).
    let (lo, hi) = if is_dark { (bg_l + 0.15, 0.97) } else { (0.10, bg_l - 0.10) };

    // Ensure valid range
    if lo >= hi {
        // Extreme case — just use white on dark, black on light
        return if is_dark { (0.9, 0.9, 0.9) } else { (0.1, 0.1, 0.1) };
    }

    // Binary search: find the L closest to the bg (least extreme) that still meets contrast.
    // Start from the "comfortable" end and push toward "extreme" only if needed.
    let mut best_rgb: Option<(f64, f64, f64)> = None;
    let mut search_lo = lo;
    let mut search_hi = hi;

    for _ in 0..30 {
        let mid = (search_lo + search_hi) / 2.0;
        let rgb = oklch_to_srgb(mid, role.chroma, hue);
        let fg_lum = luminance(rgb.0, rgb.1, rgb.2);
        let cr = contrast_ratio(fg_lum, bg_lum);

        if cr >= role.min_contrast {
            best_rgb = Some(rgb);
            // Good — try to get LESS extreme (closer to bg) while still passing
            if is_dark { search_hi = mid; } else { search_lo = mid; }
        } else {
            // Not enough contrast — push further from bg
            if is_dark { search_lo = mid; } else { search_hi = mid; }
        }
    }

    // Post-validation: if nothing met contrast, use white/black
    if let Some(rgb) = best_rgb {
        let fg_lum = luminance(rgb.0, rgb.1, rgb.2);
        let cr = contrast_ratio(fg_lum, bg_lum);
        if cr >= role.min_contrast - 0.5 {
            return rgb;
        }
    }

    // Hard fallback
    if is_dark { (0.85, 0.85, 0.85) } else { (0.15, 0.15, 0.15) }
}

/// Generate 256-color code for a role.
fn role_to_256(role: &ColorRole, bg_rgb: (f64, f64, f64)) -> u8 {
    let bg_lum = luminance(bg_rgb.0, bg_rgb.1, bg_rgb.2);
    let (r, g, b) = generate_role_color(role, bg_rgb);
    nearest_256_with_contrast(r, g, b, bg_lum, role.min_contrast)
}

/// Generate ANSI escape code (38;5;N) for a role.
fn role_to_ansi(role: &ColorRole, bg_rgb: (f64, f64, f64)) -> String {
    let idx = role_to_256(role, bg_rgb);
    format!("\x1b[38;5;{idx}m")
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

// Prompt roles
const ROLE_SUCCESS: ColorRole    = ColorRole { hue: 145.0, chroma: 0.15, min_contrast: 4.5 };
const ROLE_ERROR: ColorRole      = ColorRole { hue: 25.0,  chroma: 0.16, min_contrast: 4.5 };
const ROLE_WARNING: ColorRole    = ColorRole { hue: 80.0,  chroma: 0.15, min_contrast: 4.5 };
const ROLE_PATH: ColorRole       = ColorRole { hue: 250.0, chroma: 0.12, min_contrast: 4.5 };
const ROLE_USER: ColorRole       = ColorRole { hue: 180.0, chroma: 0.10, min_contrast: 4.5 };
const ROLE_HOST: ColorRole       = ColorRole { hue: 0.0,   chroma: 0.00, min_contrast: 4.5 }; // neutral
const ROLE_SSH_HOST: ColorRole   = ColorRole { hue: 80.0,  chroma: 0.14, min_contrast: 4.5 };
const ROLE_GIT_BRANCH: ColorRole = ColorRole { hue: 80.0,  chroma: 0.12, min_contrast: 4.5 };
const ROLE_GIT_DIRTY: ColorRole  = ColorRole { hue: 55.0,  chroma: 0.14, min_contrast: 4.5 };
const ROLE_ROOT: ColorRole       = ColorRole { hue: 25.0,  chroma: 0.18, min_contrast: 4.5 };
const ROLE_MUTED: ColorRole      = ColorRole { hue: 0.0,   chroma: 0.00, min_contrast: 4.0 }; // gray, readable
const ROLE_TIME: ColorRole       = ColorRole { hue: 0.0,   chroma: 0.00, min_contrast: 4.0 };

// Syntax highlighting roles
const ROLE_HL_KEYWORD: ColorRole  = ColorRole { hue: 330.0, chroma: 0.14, min_contrast: 4.5 };
const ROLE_HL_STRING: ColorRole   = ColorRole { hue: 145.0, chroma: 0.12, min_contrast: 4.5 };
const ROLE_HL_NUMBER: ColorRole   = ColorRole { hue: 180.0, chroma: 0.10, min_contrast: 4.5 };
const ROLE_HL_COMMAND: ColorRole  = ColorRole { hue: 210.0, chroma: 0.12, min_contrast: 4.5 };
const ROLE_HL_UNKNOWN: ColorRole  = ColorRole { hue: 0.0,   chroma: 0.00, min_contrast: 4.5 };
const ROLE_HL_FLAG: ColorRole     = ColorRole { hue: 55.0,  chroma: 0.12, min_contrast: 4.5 };
const ROLE_HL_OPERATOR: ColorRole = ColorRole { hue: 300.0, chroma: 0.12, min_contrast: 4.5 };

// LS_COLORS roles
const ROLE_LS_DIR: ColorRole  = ColorRole { hue: 210.0, chroma: 0.14, min_contrast: 4.5 };
const ROLE_LS_LINK: ColorRole = ColorRole { hue: 310.0, chroma: 0.14, min_contrast: 4.5 };
const ROLE_LS_SOCK: ColorRole = ColorRole { hue: 280.0, chroma: 0.10, min_contrast: 4.5 };
const ROLE_LS_PIPE: ColorRole = ColorRole { hue: 55.0,  chroma: 0.10, min_contrast: 4.5 };
const ROLE_LS_EXEC: ColorRole = ColorRole { hue: 145.0, chroma: 0.14, min_contrast: 4.5 };
const ROLE_LS_BLK: ColorRole  = ColorRole { hue: 35.0,  chroma: 0.10, min_contrast: 4.5 };
const ROLE_LS_CHR: ColorRole  = ColorRole { hue: 35.0,  chroma: 0.10, min_contrast: 4.5 };

// GREP_COLORS roles
const ROLE_GREP_FN: ColorRole = ColorRole { hue: 310.0, chroma: 0.12, min_contrast: 4.5 };
const ROLE_GREP_LN: ColorRole = ColorRole { hue: 145.0, chroma: 0.08, min_contrast: 4.5 };
const ROLE_GREP_BN: ColorRole = ColorRole { hue: 35.0,  chroma: 0.08, min_contrast: 4.5 };
const ROLE_GREP_SE: ColorRole = ColorRole { hue: 0.0,   chroma: 0.00, min_contrast: 4.0 };

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
            role_to_256(&ROLE_MUTED, bg)
        } else if self.is_dark {
            250 // light gray
        } else {
            240 // dark gray
        }
    }

    /// Build a theme for the given background.
    /// When bg_rgb is known, all colors are generated via OKLCH for
    /// guaranteed contrast. Without bg, falls back to safe ANSI defaults.
    pub fn new(is_dark: bool, bg_rgb: Option<(f64, f64, f64)>) -> Self {
        let reset = "\x1b[0m".to_string();

        if let Some(bg) = bg_rgb {
            Self::build_oklch(is_dark, bg, &reset)
        } else {
            Self::build_fallback(is_dark, &reset)
        }
    }

    /// Build theme with OKLCH-generated colors, all contrast-validated.
    fn build_oklch(is_dark: bool, bg: (f64, f64, f64), reset: &str) -> Self {
        Self {
            is_dark,
            bg_rgb: Some(bg),
            prompt_success: role_to_ansi(&ROLE_SUCCESS, bg),
            prompt_failed: role_to_ansi(&ROLE_ERROR, bg),
            prompt_time: role_to_ansi(&ROLE_TIME, bg),
            prompt_user: role_to_ansi(&ROLE_USER, bg),
            prompt_host: role_to_ansi(&ROLE_HOST, bg),
            prompt_ssh_host: role_to_ansi(&ROLE_SSH_HOST, bg),
            prompt_path: role_to_ansi(&ROLE_PATH, bg),
            prompt_git_branch: role_to_ansi(&ROLE_GIT_BRANCH, bg),
            prompt_git_dirty: role_to_ansi(&ROLE_GIT_DIRTY, bg),
            prompt_root: role_to_ansi(&ROLE_ROOT, bg),
            muted: role_to_ansi(&ROLE_MUTED, bg),
            error: role_to_ansi(&ROLE_ERROR, bg),
            warning: role_to_ansi(&ROLE_WARNING, bg),
            reset: reset.into(),
            hl_keyword: role_to_ansi(&ROLE_HL_KEYWORD, bg),
            hl_string: role_to_ansi(&ROLE_HL_STRING, bg),
            hl_number: role_to_ansi(&ROLE_HL_NUMBER, bg),
            hl_command: role_to_ansi(&ROLE_HL_COMMAND, bg),
            hl_unknown_cmd: role_to_ansi(&ROLE_HL_UNKNOWN, bg),
            hl_flag: role_to_ansi(&ROLE_HL_FLAG, bg),
            hl_operator: role_to_ansi(&ROLE_HL_OPERATOR, bg),
            hl_pipe: role_to_ansi(&ROLE_MUTED, bg),
            hl_comment: role_to_ansi(&ROLE_MUTED, bg),
        }
    }

    /// Fallback: safe ANSI colors when background is unknown.
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
    let roles = [
        ("di", &ROLE_LS_DIR),
        ("ln", &ROLE_LS_LINK),
        ("so", &ROLE_LS_SOCK),
        ("pi", &ROLE_LS_PIPE),
        ("ex", &ROLE_LS_EXEC),
        ("bd", &ROLE_LS_BLK),
        ("cd", &ROLE_LS_CHR),
    ];

    let mut entries: Vec<String> = roles.iter()
        .map(|(key, role)| format!("{key}=38;5;{}", role_to_256(role, bg)))
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

    for (key, role) in &roles {
        entries.push(format!("{key}=38;5;{}", role_to_256(role, bg)));
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

/// Auto-detect terminal background and build theme.
pub fn detect() -> Theme {
    // 1. RUSH_BG env var (explicit, highest priority)
    if let Ok(bg) = std::env::var("RUSH_BG") {
        if let Some((r, g, b)) = parse_hex(&bg) {
            let is_dark = luminance(r, g, b) <= 0.179;
            return Theme::new(is_dark, Some((r, g, b)));
        }
    }

    // 2. COLORFGBG (set by many terminals: "fg;bg")
    if let Ok(colorfgbg) = std::env::var("COLORFGBG") {
        if let Some(bg) = colorfgbg.rsplit(';').next() {
            if let Ok(n) = bg.parse::<u8>() {
                let is_dark = n < 8;
                return Theme::new(is_dark, None);
            }
        }
    }

    // 3. macOS appearance
    #[cfg(target_os = "macos")]
    {
        if let Ok(output) = std::process::Command::new("defaults")
            .args(["read", "-g", "AppleInterfaceStyle"])
            .output()
        {
            let is_dark = output.status.success()
                && String::from_utf8_lossy(&output.stdout).trim().eq_ignore_ascii_case("dark");
            return Theme::new(is_dark, None);
        }
    }

    // 4. Default: dark
    Theme::new(true, None)
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
pub fn initialize() -> Theme {
    let theme = detect();
    set_native_color_env_vars(&theme);
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
            assert!(cr >= role.min_contrast - 0.5,
                "hue={} contrast {cr:.1} < min {} on dark bg", role.hue, role.min_contrast);
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
            assert!(cr >= role.min_contrast - 0.5,
                "hue={} contrast {cr:.1} < min {} on light bg", role.hue, role.min_contrast);
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
    fn theme_with_and_without_bg() {
        let with_bg = Theme::new(true, Some((0.1, 0.1, 0.15)));
        let without_bg = Theme::new(true, None);
        assert!(with_bg.prompt_success.contains("\x1b["));
        assert!(without_bg.prompt_success.contains("\x1b["));
        assert!(with_bg.bg_rgb.is_some());
        assert!(without_bg.bg_rgb.is_none());
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

            for spec in ALL_ROLES {
                total_checks += 1;
                let idx = role_to_256(spec.role, bg);
                let (fr, fg, fb) = palette_rgb(idx);
                let fg_lum = luminance(fr, fg, fb);
                let cr = contrast_ratio(fg_lum, bg_lum);

                if cr < spec.role.min_contrast {
                    failures.push(format!(
                        "  FAIL: bg={bg_name} role={:<18} fg=idx {:>3} {} cr={:.2}:1 (need {:.1}:1)",
                        spec.name,
                        idx,
                        rgb_to_hex(fr, fg, fb),
                        cr,
                        spec.role.min_contrast
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
            let colors: Vec<(&str, u8)> = primary_roles.iter()
                .map(|(name, role)| (*name, role_to_256(role, bg)))
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
                    if cr < 1.3 {
                        collisions.push(format!(
                            "  TOO SIMILAR: bg={bg_name} {name_a}(idx {idx_a} {}) vs {name_b}(idx {idx_b} {}) cr={cr:.2}:1",
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

            for spec in ALL_ROLES {
                let idx = role_to_256(spec.role, bg);
                let (fr, fg, fb) = palette_rgb(idx);
                let fg_lum = luminance(fr, fg, fb);
                let cr = contrast_ratio(fg_lum, bg_lum);
                let status = if cr >= spec.role.min_contrast { "✓" }
                    else if cr >= spec.role.min_contrast - 0.5 { "~" }
                    else { "✗" };

                println!("  {:<18} {:>4} {:>8} {:>7.2}:1  {}",
                    spec.name, idx, rgb_to_hex(fr, fg, fb), cr, status);
            }
        }
        println!("\n{:=<80}", "");
    }
}
