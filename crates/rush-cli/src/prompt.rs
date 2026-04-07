use reedline::{Prompt, PromptEditMode, PromptHistorySearch, PromptHistorySearchStatus, PromptViMode};
use std::borrow::Cow;

pub struct RushPrompt {
    // Could add git branch, etc. later
}

impl RushPrompt {
    pub fn new() -> Self {
        Self {}
    }

    fn cwd_display(&self) -> String {
        let cwd = std::env::current_dir()
            .map(|p| p.to_string_lossy().to_string())
            .unwrap_or_else(|_| "?".to_string());

        // Shorten home directory to ~
        if let Ok(home) = std::env::var("HOME") {
            if let Some(rest) = cwd.strip_prefix(&home) {
                return format!("~{rest}");
            }
        }
        cwd
    }
}

impl Prompt for RushPrompt {
    fn render_prompt_left(&self) -> Cow<'_, str> {
        Cow::Owned(self.cwd_display())
    }

    fn render_prompt_right(&self) -> Cow<'_, str> {
        Cow::Borrowed("")
    }

    fn render_prompt_indicator(&self, edit_mode: PromptEditMode) -> Cow<'_, str> {
        match edit_mode {
            PromptEditMode::Default | PromptEditMode::Emacs => Cow::Borrowed(" » "),
            PromptEditMode::Vi(PromptViMode::Insert) => Cow::Borrowed(" » "),
            PromptEditMode::Vi(PromptViMode::Normal) => Cow::Borrowed(" : "),
            PromptEditMode::Custom(_) => Cow::Borrowed(" » "),
        }
    }

    fn render_prompt_multiline_indicator(&self) -> Cow<'_, str> {
        Cow::Borrowed(".. ")
    }

    fn render_prompt_history_search_indicator(
        &self,
        history_search: PromptHistorySearch,
    ) -> Cow<'_, str> {
        let prefix = match history_search.status {
            PromptHistorySearchStatus::Passing => "",
            PromptHistorySearchStatus::Failing => "(failed) ",
        };
        Cow::Owned(format!("{prefix}(search: {}) ", history_search.term))
    }
}
