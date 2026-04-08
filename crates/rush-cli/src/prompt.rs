use reedline::{Prompt, PromptEditMode, PromptHistorySearch, PromptHistorySearchStatus, PromptViMode};
use rush_core::theme::Theme;
use std::borrow::Cow;

/// Rush prompt — multi-line with mode indicator:
/// ```
///
/// ✓ 14:32 » mark@macbook  src/rush  main*
///   {cursor}
/// ```
/// The mode character (» for insert, : for normal) appears inline.
pub struct RushPrompt {
    theme: Theme,
    last_exit_code: i32,
}

impl RushPrompt {
    pub fn new(theme: Theme) -> Self {
        Self { theme, last_exit_code: 0 }
    }

    pub fn set_exit_code(&mut self, code: i32) {
        self.last_exit_code = code;
    }
}

impl Prompt for RushPrompt {
    fn render_prompt_left(&self) -> Cow<'_, str> {
        let t = &self.theme;
        let mut line = String::with_capacity(256);

        // Blank line before prompt
        line.push('\n');

        // Exit status: ✓ or ✗ [code]
        if self.last_exit_code == 0 {
            line.push_str(&format!("{}✓{}", t.prompt_success, t.reset));
        } else if self.last_exit_code > 1 {
            line.push_str(&format!("{}✗ {}{}", t.prompt_failed, self.last_exit_code, t.reset));
        } else {
            line.push_str(&format!("{}✗{}", t.prompt_failed, t.reset));
        }

        // Time: HH:MM
        let now = chrono_hhmm();
        line.push_str(&format!(" {}{}{}", t.prompt_time, now, t.reset));

        // Mode character will be inserted by render_prompt_indicator

        Cow::Owned(line)
    }

    fn render_prompt_right(&self) -> Cow<'_, str> {
        Cow::Borrowed("")
    }

    fn render_prompt_indicator(&self, edit_mode: PromptEditMode) -> Cow<'_, str> {
        let t = &self.theme;
        let mut line = String::with_capacity(256);

        // Mode character
        let mode_char = match edit_mode {
            PromptEditMode::Vi(PromptViMode::Normal) => ":",
            _ => "»",
        };
        line.push_str(&format!(" {}{}{}", t.muted, mode_char, t.reset));

        // User@Host
        let user = std::env::var("USER")
            .or_else(|_| std::env::var("USERNAME"))
            .unwrap_or_default();
        let host = short_hostname();
        let is_ssh = is_ssh_session();

        line.push_str(&format!(" {}{}{}", t.prompt_user, user, t.reset));
        line.push_str(&format!("{}@{}", t.muted, t.reset));
        if is_ssh {
            line.push_str(&format!("{}{}{}", t.prompt_ssh_host, host, t.reset));
        } else {
            line.push_str(&format!("{}{}{}", t.prompt_host, host, t.reset));
        }

        // CWD
        let cwd = short_cwd();
        line.push_str(&format!("  {}{}{}", t.prompt_path, cwd, t.reset));

        // Git
        if let Some((branch, dirty)) = git_info() {
            line.push_str(&format!("  {}{}{}", t.prompt_git_branch, branch, t.reset));
            if dirty {
                line.push_str(&format!("{}*{}", t.prompt_git_dirty, t.reset));
            }
        }

        // Input on next line with 2-space prefix
        line.push_str("\n  ");

        Cow::Owned(line)
    }

    fn render_prompt_multiline_indicator(&self) -> Cow<'_, str> {
        Cow::Borrowed("    ")
    }

    fn render_prompt_history_search_indicator(
        &self,
        history_search: PromptHistorySearch,
    ) -> Cow<'_, str> {
        let prefix = match history_search.status {
            PromptHistorySearchStatus::Passing => "",
            PromptHistorySearchStatus::Failing => "(failed) ",
        };
        Cow::Owned(format!("\n{prefix}(search: {}) ", history_search.term))
    }
}

// ── Helpers ─────────────────────────────────────────────────────────

fn chrono_hhmm() -> String {
    #[cfg(unix)]
    {
        unsafe {
            let t = libc::time(std::ptr::null_mut());
            let mut tm: libc::tm = std::mem::zeroed();
            libc::localtime_r(&t, &mut tm);
            format!("{:02}:{:02}", tm.tm_hour, tm.tm_min)
        }
    }
    #[cfg(not(unix))]
    {
        "??:??".to_string()
    }
}

fn short_hostname() -> String {
    rush_core::llm::get_hostname()
        .split('.')
        .next()
        .unwrap_or("unknown")
        .to_string()
}

fn is_ssh_session() -> bool {
    std::env::var("SSH_CONNECTION").is_ok()
        || std::env::var("SSH_TTY").is_ok()
        || std::env::var("SSH_CLIENT").is_ok()
}

fn short_cwd() -> String {
    let cwd = std::env::current_dir()
        .map(|p| p.to_string_lossy().to_string())
        .unwrap_or_else(|_| "?".to_string());

    let display = if let Ok(home) = std::env::var("HOME") {
        if let Some(rest) = cwd.strip_prefix(&home) {
            if rest.is_empty() { "~".to_string() } else { format!("~{rest}") }
        } else {
            cwd.clone()
        }
    } else {
        cwd.clone()
    };

    // Shorten to last 2 path components
    let parts: Vec<&str> = display.split('/').filter(|s| !s.is_empty()).collect();
    if parts.len() > 2 {
        parts[parts.len() - 2..].join("/")
    } else {
        display
    }
}

fn git_info() -> Option<(String, bool)> {
    let branch_output = std::process::Command::new("git")
        .args(["rev-parse", "--abbrev-ref", "HEAD"])
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::null())
        .output()
        .ok()?;

    if !branch_output.status.success() {
        return None;
    }

    let branch = String::from_utf8_lossy(&branch_output.stdout)
        .trim()
        .to_string();

    if branch.is_empty() {
        return None;
    }

    let dirty = std::process::Command::new("git")
        .args(["status", "--porcelain"])
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::null())
        .output()
        .ok()
        .map(|o| !o.stdout.is_empty())
        .unwrap_or(false);

    Some((branch, dirty))
}
