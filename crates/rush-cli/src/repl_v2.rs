//! Experimental REPL backend using `rush-line` instead of `rushline`.
//!
//! Activated by setting `RUSH_LINE_V2=1` in the environment when
//! launching `rush`. The default REPL (`crate::repl::run`) is
//! untouched; this lives alongside it so the new line editor can be
//! A/B tested without losing the working baseline.
//!
//! Feature parity with the v1 REPL is *not* a goal of this file. We
//! intentionally drop history, completion, hints, custom validators
//! and highlighters, and vi mode — those will be added back to
//! rush-line in subsequent phases. What this exercises is the painter
//! integrated with rush's actual command execution path, against
//! rush's actual multi-line prompt — the surface that was eating
//! lines under rushline (#270).

use std::borrow::Cow;

use rush_core::config::RushConfig;
use rush_core::eval::{Evaluator, StdOutput};
use rush_core::theme;
use rush_core::value::Value;
use rush_line::{FileBackedHistory, LineEditor, Signal};

use crate::builtins;
use crate::prompt::RushPrompt;

fn history_path() -> std::path::PathBuf {
    let home = std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .unwrap_or_else(|_| ".".to_string());
    let dir = std::path::PathBuf::from(home).join(".config").join("rush");
    std::fs::create_dir_all(&dir).ok();
    dir.join("history")
}

/// Adapt the existing `RushPrompt` (which implements `rushline::Prompt`)
/// to `rush_line::Prompt` by concatenating left + indicator.
struct PromptAdapter<'a>(&'a RushPrompt);

impl rush_line::Prompt for PromptAdapter<'_> {
    fn render(&self) -> String {
        let left: Cow<'_, str> = rushline::Prompt::render_prompt_left(self.0);
        let indicator: Cow<'_, str> = rushline::Prompt::render_prompt_indicator(
            self.0,
            rushline::PromptEditMode::Default,
        );
        format!("{left}{indicator}")
    }
}

pub fn run(is_login: bool) {
    let config = RushConfig::load();

    // Same env / config promotion the v1 REPL does, so theming and
    // builtins behave the same way under v2.
    if std::env::var_os("RUSH_BG").is_none() {
        if !config.bg.is_empty() {
            unsafe { std::env::set_var("RUSH_BG", &config.bg) };
        }
        if let Some(bg) = theme::load_rushbg() {
            unsafe { std::env::set_var("RUSH_BG", &bg) };
        }
    }
    if std::env::var_os("RUSH_FLAVOR").is_none() && !config.flavor.is_empty() {
        unsafe { std::env::set_var("RUSH_FLAVOR", &config.flavor) };
    }
    if std::env::var_os("RUSH_ACCENT").is_none() && !config.accent.is_empty() {
        unsafe { std::env::set_var("RUSH_ACCENT", &config.accent) };
    }
    builtins::set_baseline_bg(&config.bg);

    let detected_theme = theme::initialize();

    let mut output = StdOutput;
    let mut evaluator = Evaluator::new(&mut output);

    builtins::inject_env_vars(is_login);
    builtins::inject_builtin_vars(&mut evaluator);
    builtins::load_aliases_from_config();
    builtins::load_secrets(&mut evaluator);
    builtins::load_init(&mut evaluator);

    // Share rush's history file with the v1 REPL so Up/Down recalls
    // commands from past sessions regardless of which backend the
    // user was on. `with_file` returns Err only on parent-dir creation
    // failure; if it does, fall back to in-memory so v2 still works.
    let history = FileBackedHistory::with_file(config.history_size, history_path())
        .unwrap_or_else(|_| FileBackedHistory::in_memory(config.history_size));
    let mut editor = LineEditor::new().with_history(history);
    let mut prompt = RushPrompt::new(detected_theme.clone());

    eprintln!(
        "{}rush-line v2 (RUSH_LINE_V2=1) — emacs only, no completion/hint/vi yet{}",
        detected_theme.muted, detected_theme.reset
    );
    println!();

    loop {
        if crate::signals::should_exit() {
            break;
        }

        builtins::JOB_TABLE.with(|jt| jt.borrow_mut().report_done());

        evaluator.env.set("$?", Value::Int(evaluator.exit_code as i64));
        prompt.set_exit_code(evaluator.exit_code);

        let adapter = PromptAdapter(&prompt);
        match editor.read_line(&adapter) {
            Ok(Signal::Success(line)) => {
                let trimmed = line.trim();
                if trimmed.is_empty() {
                    continue;
                }
                if trimmed == "exit" || trimmed == "quit" {
                    break;
                }
                // Sync history to disk after each submit so kills /
                // crashes don't lose recent commands. Matches the
                // bash-with-histappend behavior the v1 REPL uses.
                if let Some(history) = editor.history_mut() {
                    let _ = history.sync();
                }
                crate::run_line(&mut evaluator, trimmed);
            }
            Ok(Signal::CtrlC) => {
                // Conventional shell behavior: clear the line and prompt again.
            }
            Ok(Signal::CtrlD) => break,
            Err(e) => {
                eprintln!("rush: input error: {e}");
                break;
            }
        }
    }

    if let Some(action) = rush_core::trap::get_exit_trap()
        && !action.is_empty()
    {
        crate::run_line(&mut evaluator, &action);
    }
}
