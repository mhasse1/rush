//! Theme system: dark/light detection, color slots, LS_COLORS/GREP_COLORS generation.

/// ANSI color codes for Rush UI elements.
#[derive(Debug, Clone)]
pub struct Theme {
    pub is_dark: bool,

    // Prompt colors (ANSI SGR codes)
    pub prompt_success: &'static str,    // ✓
    pub prompt_failed: &'static str,     // ✗
    pub prompt_time: &'static str,       // HH:MM
    pub prompt_user: &'static str,       // username
    pub prompt_host: &'static str,       // hostname (local)
    pub prompt_ssh_host: &'static str,   // hostname (SSH)
    pub prompt_path: &'static str,       // cwd
    pub prompt_git_branch: &'static str, // branch name
    pub prompt_git_dirty: &'static str,  // *
    pub prompt_root: &'static str,       // [ROOT]
    pub muted: &'static str,             // dim text
    pub error: &'static str,             // errors
    pub warning: &'static str,           // warnings
    pub reset: &'static str,             // reset

    // Syntax highlighting
    pub hl_keyword: &'static str,
    pub hl_string: &'static str,
    pub hl_number: &'static str,
    pub hl_command: &'static str,
    pub hl_unknown_cmd: &'static str,
    pub hl_flag: &'static str,
    pub hl_operator: &'static str,
    pub hl_pipe: &'static str,
    pub hl_comment: &'static str,
}

const RESET: &str = "\x1b[0m";

/// Dark theme (default for dark terminals)
pub const DARK: Theme = Theme {
    is_dark: true,
    prompt_success: "\x1b[32m",       // green
    prompt_failed: "\x1b[31m",        // red
    prompt_time: "\x1b[90m",          // dark gray
    prompt_user: "\x1b[36m",          // cyan
    prompt_host: "\x1b[90m",          // dark gray
    prompt_ssh_host: "\x1b[33m",      // yellow
    prompt_path: "\x1b[32m",          // green
    prompt_git_branch: "\x1b[33m",    // yellow
    prompt_git_dirty: "\x1b[93m",     // bright yellow
    prompt_root: "\x1b[31m",          // red
    muted: "\x1b[90m",               // dark gray
    error: "\x1b[31m",               // red
    warning: "\x1b[33m",             // yellow
    reset: RESET,

    hl_keyword: "\x1b[38;5;204m",    // pink
    hl_string: "\x1b[32m",           // green
    hl_number: "\x1b[36m",           // cyan
    hl_command: "\x1b[36m",          // cyan
    hl_unknown_cmd: "\x1b[37m",      // white
    hl_flag: "\x1b[33m",             // yellow
    hl_operator: "\x1b[35m",         // magenta
    hl_pipe: "\x1b[90m",             // dark gray
    hl_comment: "\x1b[90m",          // dark gray
};

/// Light theme (for light terminals)
pub const LIGHT: Theme = Theme {
    is_dark: false,
    prompt_success: "\x1b[32m",       // green
    prompt_failed: "\x1b[31m",        // red
    prompt_time: "\x1b[90m",          // dark gray
    prompt_user: "\x1b[34m",          // blue
    prompt_host: "\x1b[90m",          // dark gray
    prompt_ssh_host: "\x1b[33m",      // dark yellow
    prompt_path: "\x1b[34m",          // blue
    prompt_git_branch: "\x1b[33m",    // dark yellow
    prompt_git_dirty: "\x1b[33m",     // dark yellow
    prompt_root: "\x1b[31m",          // red
    muted: "\x1b[90m",               // dark gray
    error: "\x1b[31m",               // red
    warning: "\x1b[33m",             // dark yellow
    reset: RESET,

    hl_keyword: "\x1b[38;5;161m",    // dark pink
    hl_string: "\x1b[32m",           // green
    hl_number: "\x1b[36m",           // cyan
    hl_command: "\x1b[34m",          // blue
    hl_unknown_cmd: "\x1b[30m",      // black
    hl_flag: "\x1b[38;5;130m",       // dark orange
    hl_operator: "\x1b[35m",         // magenta
    hl_pipe: "\x1b[90m",             // dark gray
    hl_comment: "\x1b[90m",          // dark gray
};

/// Detect dark/light terminal and return appropriate theme.
pub fn detect() -> &'static Theme {
    if is_dark_terminal() { &DARK } else { &LIGHT }
}

/// Auto-detect if the terminal has a dark background.
fn is_dark_terminal() -> bool {
    // 1. RUSH_BG env var (explicit)
    if let Ok(bg) = std::env::var("RUSH_BG") {
        if let Some(lum) = hex_luminance(&bg) {
            return lum < 0.5;
        }
    }

    // 2. COLORFGBG (set by many terminals: "fg;bg" where bg=0 is dark)
    if let Ok(colorfgbg) = std::env::var("COLORFGBG") {
        if let Some(bg) = colorfgbg.rsplit(';').next() {
            if let Ok(n) = bg.parse::<u8>() {
                return n < 8; // 0-7 are dark colors
            }
        }
    }

    // 3. macOS: check system appearance
    #[cfg(target_os = "macos")]
    {
        if let Ok(output) = std::process::Command::new("defaults")
            .args(["read", "-g", "AppleInterfaceStyle"])
            .output()
        {
            let stdout = String::from_utf8_lossy(&output.stdout);
            if stdout.trim().eq_ignore_ascii_case("dark") {
                return true;
            }
            // If command succeeds but says "Dark" → dark; if fails → light (default)
            if output.status.success() {
                return stdout.trim().eq_ignore_ascii_case("dark");
            }
            return false; // AppleInterfaceStyle not set → light mode
        }
    }

    // 4. Default: assume dark (most developer terminals are dark)
    true
}

/// Parse hex color (#RRGGBB or RRGGBB) and return relative luminance.
fn hex_luminance(hex: &str) -> Option<f64> {
    let hex = hex.trim_start_matches('#');
    if hex.len() != 6 {
        return None;
    }
    let r = u8::from_str_radix(&hex[0..2], 16).ok()? as f64 / 255.0;
    let g = u8::from_str_radix(&hex[2..4], 16).ok()? as f64 / 255.0;
    let b = u8::from_str_radix(&hex[4..6], 16).ok()? as f64 / 255.0;
    // sRGB relative luminance
    let r = if r <= 0.03928 { r / 12.92 } else { ((r + 0.055) / 1.055).powf(2.4) };
    let g = if g <= 0.03928 { g / 12.92 } else { ((g + 0.055) / 1.055).powf(2.4) };
    let b = if b <= 0.03928 { b / 12.92 } else { ((b + 0.055) / 1.055).powf(2.4) };
    Some(0.2126 * r + 0.7152 * g + 0.0722 * b)
}

/// Generate LS_COLORS for the detected theme.
pub fn generate_ls_colors(theme: &Theme) -> String {
    if theme.is_dark {
        // Dark theme: bold colors for better contrast
        "di=1;34:ln=1;36:so=1;35:pi=33:ex=1;32:bd=1;33:cd=1;33:su=37;41:sg=30;43:tw=30;42:ow=34;42".to_string()
    } else {
        // Light theme: non-bold (darker) colors
        "di=34:ln=36:so=35:pi=33:ex=32:bd=33:cd=33:su=37;41:sg=30;43:tw=30;42:ow=34;42".to_string()
    }
}

/// Generate GREP_COLORS for the detected theme.
pub fn generate_grep_colors(theme: &Theme) -> String {
    if theme.is_dark {
        "ms=01;31:mc=01;31:sl=:cx=:fn=35:ln=32:bn=32:se=36".to_string()
    } else {
        "ms=31:mc=31:sl=:cx=:fn=35:ln=32:bn=32:se=36".to_string()
    }
}

/// Set LS_COLORS, LSCOLORS, GREP_COLORS, CLICOLOR env vars.
/// Respects existing user values and NO_COLOR.
pub fn set_native_color_env_vars(theme: &Theme) {
    // Respect NO_COLOR convention
    if std::env::var("NO_COLOR").is_ok() {
        return;
    }

    // LS_COLORS (GNU/Linux)
    if std::env::var("LS_COLORS").is_err() {
        let ls_colors = generate_ls_colors(theme);
        unsafe { std::env::set_var("LS_COLORS", &ls_colors) };
    }

    // LSCOLORS (BSD/macOS)
    if std::env::var("LSCOLORS").is_err() {
        let lscolors = if theme.is_dark {
            "ExGxFxDxCxDxDxBxBxExEx"
        } else {
            "exgxfxdxcxdxdxbxbxexex"
        };
        unsafe { std::env::set_var("LSCOLORS", lscolors) };
    }

    // GREP_COLORS
    if std::env::var("GREP_COLORS").is_err() {
        let grep_colors = generate_grep_colors(theme);
        unsafe { std::env::set_var("GREP_COLORS", &grep_colors) };
    }

    // CLICOLOR (macOS: enable color for ls)
    if std::env::var("CLICOLOR").is_err() {
        unsafe { std::env::set_var("CLICOLOR", "1") };
    }
}

/// Initialize theme: detect dark/light, set color env vars.
/// Returns the detected theme.
pub fn initialize() -> &'static Theme {
    let theme = detect();
    set_native_color_env_vars(theme);
    theme
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hex_luminance_black() {
        let lum = hex_luminance("#000000").unwrap();
        assert!(lum < 0.01);
    }

    #[test]
    fn hex_luminance_white() {
        let lum = hex_luminance("#FFFFFF").unwrap();
        assert!(lum > 0.99);
    }

    #[test]
    fn hex_luminance_mid() {
        let lum = hex_luminance("#808080").unwrap();
        assert!(lum > 0.1 && lum < 0.5);
    }

    #[test]
    fn ls_colors_generated() {
        let colors = generate_ls_colors(&DARK);
        assert!(colors.contains("di="));
        assert!(colors.contains("ex="));
    }

    #[test]
    fn theme_detection_returns_something() {
        let theme = detect();
        assert!(!theme.reset.is_empty());
    }
}
