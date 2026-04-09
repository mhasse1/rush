use reedline::{Prompt, PromptEditMode, PromptHistorySearch, PromptHistorySearchStatus, PromptViMode};
use rush_core::theme::Theme;
use std::borrow::Cow;

/// Rush prompt — single-line info + input on next line:
/// ```
///
/// ✓ 16:54 » mark@rocinante  src/rush  main*
///   {cursor}
/// ```
pub struct RushPrompt {
    theme: Theme,
    last_exit_code: i32,
    start_mtime: Option<std::time::SystemTime>,
}

impl RushPrompt {
    pub fn new(theme: Theme) -> Self {
        let start_mtime = std::env::current_exe()
            .ok()
            .and_then(|p| std::fs::metadata(p).ok())
            .and_then(|m| m.modified().ok());
        Self { theme, last_exit_code: 0, start_mtime }
    }

    pub fn set_exit_code(&mut self, code: i32) {
        self.last_exit_code = code;
    }

    fn is_stale(&self) -> bool {
        if let Some(start) = self.start_mtime {
            if let Some(current) = std::env::current_exe()
                .ok()
                .and_then(|p| std::fs::metadata(p).ok())
                .and_then(|m| m.modified().ok())
            {
                return current > start;
            }
        }
        false
    }
}

impl Prompt for RushPrompt {
    fn render_prompt_left(&self) -> Cow<'_, str> {
        // Everything goes in the indicator — left is just the blank line
        Cow::Borrowed("\n")
    }

    fn render_prompt_right(&self) -> Cow<'_, str> {
        Cow::Borrowed("")
    }

    fn render_prompt_indicator(&self, edit_mode: PromptEditMode) -> Cow<'_, str> {
        let t = &self.theme;
        let mut line = String::with_capacity(256);

        // Exit status: ✓ or ✗ [code]
        if self.last_exit_code == 0 {
            line.push_str(&format!("{}✓{}", t.prompt_success, t.reset));
        } else if self.last_exit_code > 1 {
            line.push_str(&format!("{}✗ {}{}", t.prompt_failed, self.last_exit_code, t.reset));
        } else {
            line.push_str(&format!("{}✗{}", t.prompt_failed, t.reset));
        }

        // Time
        let now = chrono_hhmm();
        line.push_str(&format!(" {}{}{}", t.prompt_time, now, t.reset));

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

        // Stale binary
        if self.is_stale() {
            line.push_str(&format!("  {}[stale]{}", t.warning, t.reset));
        }

        // Input on next line
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
    rush_core::platform::current().local_time_hhmm()
}

fn short_hostname() -> String {
    rush_core::platform::current().hostname()
}

fn is_ssh_session() -> bool {
    rush_core::platform::current().is_ssh()
}

fn short_cwd() -> String {
    let cwd = std::env::current_dir()
        .map(|p| p.to_string_lossy().to_string())
        .unwrap_or_else(|_| "?".to_string());

    // Normalize to forward slashes (Unix-style on all platforms)
    let cwd = cwd.replace('\\', "/");

    let display = if let Ok(home) = std::env::var("HOME") {
        let home = home.replace('\\', "/");
        if let Some(rest) = cwd.strip_prefix(&home) {
            if rest.is_empty() { "~".to_string() } else { format!("~{rest}") }
        } else {
            cwd.clone()
        }
    } else {
        cwd.clone()
    };

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
