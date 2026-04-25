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
use rush_line::{FileBackedHistory, LineEditor, Signal, ViKeyMap};

use crate::builtins;
use crate::completer::RushCompleter;
use crate::highlighter::RushHighlighter;
use crate::prompt::RushPrompt;
use crate::validator::RushValidator;

/// Bridge rush's `RushHighlighter` (which implements
/// `rushline::Highlighter` and returns `StyledText`) to
/// `rush_line::Highlighter`, which expects an ANSI-formatted string.
struct HighlighterAdapter(RushHighlighter);

impl rush_line::Highlighter for HighlighterAdapter {
    fn highlight(&self, segment: &str) -> String {
        use rushline::Highlighter as _;
        // RushHighlighter ignores cursor position, so any value works.
        let styled = self.0.highlight(segment, segment.len());
        let mut out = String::new();
        for (style, text) in styled.buffer {
            // nu_ansi_term::Style::paint returns an AnsiGenericString
            // whose Display impl emits the start/end SGR escapes.
            out.push_str(&style.paint(text).to_string());
        }
        out
    }
}

/// Bridge rush's `RushValidator` (which implements
/// `rushline::Validator`) to `rush_line::Validator`. Same logic.
struct ValidatorAdapter(RushValidator);

impl rush_line::Validator for ValidatorAdapter {
    fn validate(&self, line: &str) -> rush_line::ValidationResult {
        use rushline::Validator as _;
        match self.0.validate(line) {
            rushline::ValidationResult::Complete => rush_line::ValidationResult::Complete,
            rushline::ValidationResult::Incomplete => rush_line::ValidationResult::Incomplete,
        }
    }
}

/// Bridge rush's `RushCompleter` (which implements `rushline::Completer`)
/// to `rush_line::Completer`. Same path/command-completion logic; just
/// translate between the two crates' suggestion shapes.
struct CompleterAdapter(RushCompleter);

impl rush_line::Completer for CompleterAdapter {
    fn complete(&mut self, line: &str, pos: usize) -> Vec<rush_line::Suggestion> {
        use rushline::Completer as _;
        self.0
            .complete(line, pos)
            .into_iter()
            .map(|s| rush_line::Suggestion {
                value: s.value,
                span: rush_line::Span {
                    start: s.span.start,
                    end: s.span.end,
                },
                append_whitespace: s.append_whitespace,
            })
            .collect()
    }
}

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
    let completer = CompleterAdapter(RushCompleter::new());
    let validator = ValidatorAdapter(RushValidator);
    let highlighter = HighlighterAdapter(RushHighlighter);
    let editor_builder = LineEditor::new()
        .with_history(history)
        .with_completer(completer)
        .with_validator(validator)
        .with_highlighter(highlighter);
    // Honor rush's configured edit_mode (default vi, like the v1 path).
    let use_vi = config.edit_mode != "emacs";
    let mut editor = if use_vi {
        editor_builder.with_keymap(ViKeyMap::new())
    } else {
        editor_builder
    };
    let mut prompt = RushPrompt::new(detected_theme.clone());

    let _ = use_vi; // mode is visible via cursor shape; no banner needed
    let show_timing = config.show_timing;
    let mut last_cmd: Option<String> = None;
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

                // History expansion: !!, !$, !N, !prefix.
                let expanded = crate::repl::expand_history(trimmed, &last_cmd);
                let trimmed = expanded.as_deref().unwrap_or(trimmed);

                // --help routing: "file --help" → "help file".
                let trimmed_cow = crate::repl::route_help(trimmed);
                let trimmed = trimmed_cow.as_ref();

                if trimmed == "exit" || trimmed == "quit" {
                    break;
                }

                last_cmd = Some(trimmed.to_string());

                // Filter meta-commands out of the saved history so they
                // don't pollute autosuggestion. The line was already
                // pushed by the editor's submit handler; pop it.
                let first_word = trimmed.split_whitespace().next().unwrap_or("");
                if matches!(first_word, "history" | "clear" | "exit") {
                    if let Some(history) = editor.history_mut() {
                        history.delete_last_entry();
                    }
                }

                if let Some(history) = editor.history_mut() {
                    let _ = history.sync();
                }

                let start = std::time::Instant::now();
                crate::run_line(&mut evaluator, trimmed);
                let elapsed = start.elapsed();

                // Slow-command timing (>500ms).
                if show_timing && elapsed.as_millis() > 500 {
                    let secs = elapsed.as_secs_f64();
                    if secs >= 60.0 {
                        let mins = secs as u64 / 60;
                        let rem = secs % 60.0;
                        eprintln!(
                            "{}  {mins}m{rem:.1}s{}",
                            detected_theme.muted, detected_theme.reset
                        );
                    } else {
                        eprintln!(
                            "{}  {secs:.1}s{}",
                            detected_theme.muted, detected_theme.reset
                        );
                    }
                }

                // Post-exec hints. Error hints on failure, training
                // hints on success.
                if evaluator.exit_code != 0 {
                    if let Some(hint) = rush_core::hints::hint_for_command(
                        trimmed,
                        evaluator.exit_code,
                    ) {
                        eprintln!("{}  {hint}{}", detected_theme.muted, detected_theme.reset);
                    }
                } else if let Some(hint) = rush_core::hints::training_hint(trimmed) {
                    eprintln!();
                    eprintln!("{}  {hint}{}", detected_theme.muted, detected_theme.reset);
                }
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
