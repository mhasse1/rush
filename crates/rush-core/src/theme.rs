//! Theme system: dark/light detection, contrast-aware 256-color palette,
//! LS_COLORS/GREP_COLORS generation, setbg support.
//!
//! Design: one contrast validation path, data-driven color tables,
//! enum-indexed color roles. No duplication.

// ── Color Math ──────────────────────────────────────────────────────

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

/// Convert RGB to HSL. All values 0.0-1.0 except hue which is 0-360.
fn rgb_to_hsl(r: f64, g: f64, b: f64) -> (f64, f64, f64) {
    let max = r.max(g).max(b);
    let min = r.min(g).min(b);
    let l = (max + min) / 2.0;

    if (max - min).abs() < 1e-10 {
        return (0.0, 0.0, l);
    }

    let d = max - min;
    let s = if l > 0.5 { d / (2.0 - max - min) } else { d / (max + min) };

    let h = if (max - r).abs() < 1e-10 {
        let mut h = (g - b) / d;
        if g < b { h += 6.0; }
        h
    } else if (max - g).abs() < 1e-10 {
        (b - r) / d + 2.0
    } else {
        (r - g) / d + 4.0
    };

    (h * 60.0, s, l)
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
        // Basic 16 colors (approximate)
        let table: [(u8, u8, u8); 16] = [
            (0,0,0), (128,0,0), (0,128,0), (128,128,0), (0,0,128), (128,0,128), (0,128,128), (192,192,192),
            (128,128,128), (255,0,0), (0,255,0), (255,255,0), (0,0,255), (255,0,255), (0,255,255), (255,255,255),
        ];
        let (r, g, b) = table[idx as usize];
        (r as f64 / 255.0, g as f64 / 255.0, b as f64 / 255.0)
    } else if idx < 232 {
        // 6×6×6 color cube (indices 16-231)
        let i = idx - 16;
        let r = (i / 36) as f64;
        let g = ((i % 36) / 6) as f64;
        let b = (i % 6) as f64;
        let to_val = |c: f64| if c == 0.0 { 0.0 } else { (c * 40.0 + 55.0) / 255.0 };
        (to_val(r), to_val(g), to_val(b))
    } else {
        // Grayscale ramp (indices 232-255)
        let v = (8 + 10 * (idx as u16 - 232)) as f64 / 255.0;
        (v, v, v)
    }
}

/// Select the best 256-color index for a given role.
fn select_best_color(
    bg_lum: f64,
    bg_hue: f64,
    min_contrast: f64,
    max_contrast: f64,
    preferred_hue: Option<f64>,
    hue_weight: f64,
    used_colors: &[u8],
    is_dark: bool,
) -> u8 {
    let mut best_idx: u8 = if is_dark { 15 } else { 0 }; // fallback: white or black
    let mut best_score: f64 = f64::NEG_INFINITY;

    // Scan the 6×6×6 cube (16-231) + grayscale (232-255)
    for idx in 16..=255u8 {
        let (r, g, b) = palette_rgb(idx);
        let lum = luminance(r, g, b);
        let cr = contrast_ratio(lum, bg_lum);

        // Must be within contrast range
        if cr < min_contrast || cr > max_contrast {
            continue;
        }

        let (h, s, _l) = rgb_to_hsl(r, g, b);

        // Score: contrast bonus (prefer mid-range, not extreme)
        let contrast_mid = (min_contrast + max_contrast) / 2.0;
        let contrast_score = 2.0 - (cr - contrast_mid).abs() / contrast_mid;

        // Score: hue proximity to preferred hue
        let hue_score = if let Some(pref) = preferred_hue {
            let dist = hue_distance(h, pref);
            if dist < 90.0 { (90.0 - dist) / 90.0 * 5.0 * hue_weight } else { 0.0 }
        } else {
            0.0
        };

        // Score: saturation (prefer vivid for dark, moderate for light)
        let sat_score = if is_dark { s * 2.0 } else { (1.0 - (s - 0.5).abs()) * 1.5 };

        // Penalty: too close to background hue
        let bg_penalty = if hue_distance(h, bg_hue) < 40.0 { -8.0 } else { 0.0 };

        // Penalty: too close to already-used colors
        let collision_penalty: f64 = used_colors.iter().map(|&used| {
            let (ur, ug, ub) = palette_rgb(used);
            let ul = luminance(ur, ug, ub);
            let dist = contrast_ratio(lum, ul);
            if dist < 1.3 { -10.0 } else { 0.0 }
        }).sum();

        // Penalty: grayscale when hue is preferred
        let gray_penalty = if preferred_hue.is_some() && s < 0.1 { -3.0 } else { 0.0 };

        let score = contrast_score + hue_score + sat_score + bg_penalty + collision_penalty + gray_penalty;

        if score > best_score {
            best_score = score;
            best_idx = idx;
        }
    }

    best_idx
}

// ── LS_COLORS / GREP_COLORS Slot Definitions ────────────────────────

struct LsSlot {
    key: &'static str,         // LS_COLORS key (di, ln, so, etc.)
    min_contrast: f64,
    max_contrast: f64,
    hue: Option<f64>,          // preferred hue (0-360)
    hue_weight: f64,
    is_primary: bool,          // primary types get distinction enforcement
}

const DARK_LS_SLOTS: &[LsSlot] = &[
    LsSlot { key: "di", min_contrast: 5.0, max_contrast: 14.0, hue: Some(180.0), hue_weight: 1.5, is_primary: true },  // cyan
    LsSlot { key: "ln", min_contrast: 5.0, max_contrast: 14.0, hue: Some(300.0), hue_weight: 1.5, is_primary: true },  // magenta
    LsSlot { key: "so", min_contrast: 4.0, max_contrast: 9.0,  hue: Some(280.0), hue_weight: 1.0, is_primary: false }, // purple
    LsSlot { key: "pi", min_contrast: 4.0, max_contrast: 9.0,  hue: Some(50.0),  hue_weight: 1.0, is_primary: false }, // yellow
    LsSlot { key: "ex", min_contrast: 5.0, max_contrast: 14.0, hue: Some(120.0), hue_weight: 1.5, is_primary: true },  // green
    LsSlot { key: "bd", min_contrast: 4.0, max_contrast: 9.0,  hue: Some(40.0),  hue_weight: 1.0, is_primary: false }, // orange
    LsSlot { key: "cd", min_contrast: 4.0, max_contrast: 9.0,  hue: Some(40.0),  hue_weight: 1.0, is_primary: false }, // orange
];

const LIGHT_LS_SLOTS: &[LsSlot] = &[
    LsSlot { key: "di", min_contrast: 4.0, max_contrast: 10.0, hue: Some(220.0), hue_weight: 1.5, is_primary: true },  // navy
    LsSlot { key: "ln", min_contrast: 4.0, max_contrast: 10.0, hue: Some(320.0), hue_weight: 1.5, is_primary: true },  // deep magenta
    LsSlot { key: "so", min_contrast: 3.5, max_contrast: 8.0,  hue: Some(280.0), hue_weight: 1.0, is_primary: false }, // purple
    LsSlot { key: "pi", min_contrast: 3.5, max_contrast: 8.0,  hue: Some(45.0),  hue_weight: 1.0, is_primary: false }, // olive
    LsSlot { key: "ex", min_contrast: 4.0, max_contrast: 10.0, hue: Some(160.0), hue_weight: 1.5, is_primary: true },  // teal
    LsSlot { key: "bd", min_contrast: 3.5, max_contrast: 8.0,  hue: Some(30.0),  hue_weight: 1.0, is_primary: false }, // dark orange
    LsSlot { key: "cd", min_contrast: 3.5, max_contrast: 8.0,  hue: Some(30.0),  hue_weight: 1.0, is_primary: false }, // dark orange
];

struct GrepSlot {
    key: &'static str,
    min_contrast: f64,
    max_contrast: f64,
    hue: Option<f64>,
    hue_weight: f64,
}

const DARK_GREP_SLOTS: &[GrepSlot] = &[
    GrepSlot { key: "fn", min_contrast: 5.0, max_contrast: 14.0, hue: Some(300.0), hue_weight: 1.0 }, // filename
    GrepSlot { key: "ln", min_contrast: 4.0, max_contrast: 9.0,  hue: Some(120.0), hue_weight: 0.5 }, // line number
    GrepSlot { key: "bn", min_contrast: 4.0, max_contrast: 9.0,  hue: Some(40.0),  hue_weight: 0.5 }, // byte offset
    GrepSlot { key: "se", min_contrast: 3.0, max_contrast: 6.0,  hue: None,         hue_weight: 0.0 }, // separator
];

const LIGHT_GREP_SLOTS: &[GrepSlot] = &[
    GrepSlot { key: "fn", min_contrast: 4.0, max_contrast: 10.0, hue: Some(280.0), hue_weight: 1.0 },
    GrepSlot { key: "ln", min_contrast: 3.5, max_contrast: 8.0,  hue: Some(160.0), hue_weight: 0.5 },
    GrepSlot { key: "bn", min_contrast: 3.5, max_contrast: 8.0,  hue: Some(30.0),  hue_weight: 0.5 },
    GrepSlot { key: "se", min_contrast: 2.5, max_contrast: 5.0,  hue: None,         hue_weight: 0.0 },
];

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
    /// Build a theme for the given background.
    /// Prompt colors always use basic ANSI (proven visible across terminals).
    /// 256-color is only used for LS_COLORS/GREP_COLORS (via generate_ls_colors).
    pub fn new(is_dark: bool, bg_rgb: Option<(f64, f64, f64)>) -> Self {
        let reset = "\x1b[0m".to_string();
        // Prompt and syntax colors: always basic ANSI (reliable, proven in C# Rush)
        Self::build_basic(is_dark, bg_rgb, &reset)
    }

    /// Build theme using basic ANSI for bright colors, 256-color for muted.
    /// When bg_rgb is known, picks a muted color with guaranteed contrast.
    fn build_basic(is_dark: bool, bg_rgb: Option<(f64, f64, f64)>, reset: &str) -> Self {
        // The muted color needs to be readable on the background.
        // \x1b[90m (dark gray) is invisible on #282828 and similar dark backgrounds.
        // Use 256-color picker when bg is known, fallback to 90m otherwise.
        let muted_code = if let Some((r, g, b)) = bg_rgb {
            let bg_lum = luminance(r, g, b);
            let (bg_hue, _, _) = rgb_to_hsl(r, g, b);
            let idx = select_best_color(bg_lum, bg_hue, 3.0, 5.5, None, 0.0, &[], is_dark);
            format!("\x1b[38;5;{idx}m")
        } else if is_dark {
            "\x1b[37m".into() // white instead of dark gray on dark unknown bg
        } else {
            "\x1b[90m".into() // dark gray is fine on light backgrounds
        };

        if is_dark {
            Self {
                is_dark, bg_rgb,
                prompt_success: "\x1b[32m".into(), prompt_failed: "\x1b[91m".into(),
                prompt_time: muted_code.clone(), prompt_user: "\x1b[36m".into(),
                prompt_host: "\x1b[37m".into(), prompt_ssh_host: "\x1b[93m".into(),
                prompt_path: "\x1b[92m".into(), prompt_git_branch: "\x1b[33m".into(),
                prompt_git_dirty: "\x1b[93m".into(), prompt_root: "\x1b[91m".into(),
                muted: muted_code.clone(), error: "\x1b[91m".into(), warning: "\x1b[33m".into(),
                reset: reset.into(),
                hl_keyword: "\x1b[38;5;204m".into(), hl_string: "\x1b[32m".into(),
                hl_number: "\x1b[36m".into(), hl_command: "\x1b[96m".into(),
                hl_unknown_cmd: "\x1b[37m".into(), hl_flag: "\x1b[33m".into(),
                hl_operator: "\x1b[35m".into(), hl_pipe: muted_code.clone(),
                hl_comment: muted_code,
            }
        } else {
            Self {
                is_dark, bg_rgb,
                prompt_success: "\x1b[32m".into(), prompt_failed: "\x1b[31m".into(),
                prompt_time: muted_code.clone(), prompt_user: "\x1b[34m".into(),
                prompt_host: muted_code.clone(), prompt_ssh_host: "\x1b[33m".into(),
                prompt_path: "\x1b[34m".into(), prompt_git_branch: "\x1b[33m".into(),
                prompt_git_dirty: "\x1b[33m".into(), prompt_root: "\x1b[31m".into(),
                muted: muted_code.clone(), error: "\x1b[31m".into(), warning: "\x1b[33m".into(),
                reset: reset.into(),
                hl_keyword: "\x1b[38;5;161m".into(), hl_string: "\x1b[32m".into(),
                hl_number: "\x1b[36m".into(), hl_command: "\x1b[34m".into(),
                hl_unknown_cmd: "\x1b[30m".into(), hl_flag: "\x1b[38;5;130m".into(),
                hl_operator: "\x1b[35m".into(), hl_pipe: muted_code.clone(),
                hl_comment: muted_code,
            }
        }
    }
}

// ── LS_COLORS / GREP_COLORS Generation ──────────────────────────────

/// Generate 256-color LS_COLORS optimized for the background.
pub fn generate_ls_colors(theme: &Theme) -> String {
    let (bg_r, bg_g, bg_b) = theme.bg_rgb.unwrap_or(if theme.is_dark { (0.0, 0.0, 0.0) } else { (1.0, 1.0, 1.0) });
    let bg_lum = luminance(bg_r, bg_g, bg_b);
    let (bg_hue, _, _) = rgb_to_hsl(bg_r, bg_g, bg_b);

    let slots = if theme.is_dark { DARK_LS_SLOTS } else { LIGHT_LS_SLOTS };
    let mut used: Vec<u8> = Vec::new();
    let mut entries = Vec::new();

    for slot in slots {
        let idx = select_best_color(bg_lum, bg_hue, slot.min_contrast, slot.max_contrast,
            slot.hue, slot.hue_weight, &used, theme.is_dark);
        if slot.is_primary { used.push(idx); }
        entries.push(format!("{}=38;5;{idx}", slot.key));
    }

    // Special entries (fixed, not hue-selected)
    entries.push("su=37;41".into()); // setuid
    entries.push("sg=30;43".into()); // setgid
    entries.push("tw=30;42".into()); // sticky+other-writable
    entries.push("ow=34;42".into()); // other-writable

    entries.join(":")
}

/// Generate 256-color GREP_COLORS optimized for the background.
pub fn generate_grep_colors(theme: &Theme) -> String {
    let (bg_r, bg_g, bg_b) = theme.bg_rgb.unwrap_or(if theme.is_dark { (0.0, 0.0, 0.0) } else { (1.0, 1.0, 1.0) });
    let bg_lum = luminance(bg_r, bg_g, bg_b);
    let (bg_hue, _, _) = rgb_to_hsl(bg_r, bg_g, bg_b);

    let slots = if theme.is_dark { DARK_GREP_SLOTS } else { LIGHT_GREP_SLOTS };
    let mut entries = Vec::new();

    // Match highlight is always bold red
    entries.push("ms=01;31".into());
    entries.push("mc=01;31".into());
    entries.push("sl=".into());
    entries.push("cx=".into());

    for slot in slots {
        let idx = select_best_color(bg_lum, bg_hue, slot.min_contrast, slot.max_contrast,
            slot.hue, slot.hue_weight, &[], theme.is_dark);
        entries.push(format!("{}=38;5;{idx}", slot.key));
    }

    entries.join(":")
}

/// Generate BSD LSCOLORS string (macOS).
/// Format: 11 pairs of foreground+background, one pair per file type:
///   directory, symlink, socket, pipe, executable, block device,
///   char device, setuid, setgid, dir+sticky+ow, dir+ow
/// Letters: a=black b=red c=green d=brown e=blue f=magenta g=cyan h=grey
/// Uppercase = bold. x = default.
pub fn generate_lscolors(theme: &Theme) -> String {
    if theme.is_dark {
        // Bold colors for dark backgrounds — high contrast
        // dir=blue, sym=magenta, socket=cyan, pipe=yellow, exec=green
        "Gxfxcxdxbxegedabagacad".to_string()
    } else {
        // Non-bold for light backgrounds — softer
        "gxfxcxdxbxegedabagacad".to_string()
    }
}

// ── setbg ───────────────────────────────────────────────────────────

/// Set terminal background via OSC 11 and re-theme.
pub fn set_background(hex: &str, emit_osc: bool) -> Option<Theme> {
    let (r, g, b) = parse_hex(hex)?;
    let is_dark = luminance(r, g, b) < 0.5;

    if emit_osc {
        // OSC 11: set background color
        let hex_clean = hex.trim_start_matches('#');
        print!("\x1b]11;#{hex_clean}\x07");
        // OSC 10: set foreground to contrast
        let fg = if is_dark { "ffffff" } else { "000000" };
        print!("\x1b]10;#{fg}\x07");
        use std::io::Write;
        std::io::stdout().flush().ok();
    }

    // Propagate to child processes
    unsafe { std::env::set_var("RUSH_BG", hex) };

    Some(Theme::new(is_dark, Some((r, g, b))))
}

// ── Detection + Initialization ──────────────────────────────────────

/// Auto-detect terminal background and build theme.
pub fn detect() -> Theme {
    // 1. RUSH_BG env var (explicit, highest priority)
    if let Ok(bg) = std::env::var("RUSH_BG") {
        if let Some((r, g, b)) = parse_hex(&bg) {
            let is_dark = luminance(r, g, b) < 0.5;
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
/// Always sets Rush-generated values — we own the color environment.
/// Respects NO_COLOR (https://no-color.org/).
pub fn set_native_color_env_vars(theme: &Theme) {
    if std::env::var("NO_COLOR").is_ok() { return; }

    // Always set — Rush owns these. Inherited values from parent shell
    // may not match Rush's detected dark/light theme.
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
        assert!(cr > 20.0); // WCAG max is 21:1
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
    fn hsl_red() {
        let (h, s, _l) = rgb_to_hsl(1.0, 0.0, 0.0);
        assert!((h - 0.0).abs() < 1.0); // hue ~0
        assert!(s > 0.9);
    }

    #[test]
    fn hsl_green() {
        let (h, s, _l) = rgb_to_hsl(0.0, 1.0, 0.0);
        assert!((h - 120.0).abs() < 1.0);
        assert!(s > 0.9);
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
    fn select_color_dark_bg() {
        // On dark background, selected color should have high luminance
        let idx = select_best_color(0.01, 0.0, 5.0, 14.0, Some(120.0), 1.5, &[], true);
        let (r, g, b) = palette_rgb(idx);
        let lum = luminance(r, g, b);
        let cr = contrast_ratio(lum, 0.01);
        assert!(cr >= 5.0, "contrast {cr} < 5.0 for idx {idx}");
    }

    #[test]
    fn select_color_light_bg() {
        let idx = select_best_color(0.95, 0.0, 4.0, 10.0, Some(220.0), 1.5, &[], false);
        let (r, g, b) = palette_rgb(idx);
        let lum = luminance(r, g, b);
        let cr = contrast_ratio(lum, 0.95);
        assert!(cr >= 4.0, "contrast {cr} < 4.0 for idx {idx}");
    }

    #[test]
    fn select_avoids_used_colors() {
        let first = select_best_color(0.01, 0.0, 5.0, 14.0, Some(120.0), 1.5, &[], true);
        let second = select_best_color(0.01, 0.0, 5.0, 14.0, Some(120.0), 1.5, &[first], true);
        assert_ne!(first, second, "should pick different colors");
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
        // Both use basic ANSI for prompt (proven visible)
        assert!(with_bg.prompt_success.contains("\x1b["), "should have ANSI codes");
        assert!(without_bg.prompt_success.contains("\x1b["), "should have ANSI codes");
        // bg_rgb should be preserved for LS_COLORS generation
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
    fn rushbg_nonexistent() {
        // In test directory, .rushbg likely doesn't exist
        // This just tests the function doesn't crash
        let _ = load_rushbg();
    }
}
