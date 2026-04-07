use reedline::{
    default_vi_insert_keybindings, default_vi_normal_keybindings, ColumnarMenu, FileBackedHistory,
    KeyCode, KeyModifiers, Reedline, ReedlineEvent, ReedlineMenu, Signal, Vi,
};

use rush_core::eval::{Evaluator, StdOutput};

use crate::completer::RushCompleter;
use crate::highlighter::RushHighlighter;
use crate::prompt::RushPrompt;

/// History file location.
fn history_path() -> std::path::PathBuf {
    let config_dir = dirs_config().join("rush");
    std::fs::create_dir_all(&config_dir).ok();
    config_dir.join("history")
}

fn dirs_config() -> std::path::PathBuf {
    if let Ok(home) = std::env::var("HOME") {
        std::path::PathBuf::from(home).join(".config")
    } else if let Ok(home) = std::env::var("USERPROFILE") {
        std::path::PathBuf::from(home).join(".config")
    } else {
        std::path::PathBuf::from(".config")
    }
}

/// Run the interactive REPL.
pub fn run() {
    let mut output = StdOutput;
    let mut evaluator = Evaluator::new(&mut output);

    // Build reedline editor
    let history = FileBackedHistory::with_file(10_000, history_path())
        .expect("Failed to create history file");

    let completer = Box::new(RushCompleter::new());

    // Completion menu
    let completion_menu = Box::new(ColumnarMenu::default());

    let vi = Vi::new(
        default_vi_insert_keybindings(),
        default_vi_normal_keybindings(),
    );

    let mut editor = Reedline::create()
        .with_edit_mode(Box::new(vi))
        .with_history(Box::new(history))
        .with_completer(completer)
        .with_menu(ReedlineMenu::EngineCompleter(completion_menu))
        .with_highlighter(Box::new(RushHighlighter))
        .with_ansi_colors(true);

    // Bind Tab to completion menu
    let mut insert_bindings = default_vi_insert_keybindings();
    insert_bindings.add_binding(
        KeyModifiers::NONE,
        KeyCode::Tab,
        ReedlineEvent::UntilFound(vec![
            ReedlineEvent::Menu("columnar_menu".to_string()),
            ReedlineEvent::MenuNext,
        ]),
    );
    editor = editor.with_edit_mode(Box::new(Vi::new(
        insert_bindings,
        default_vi_normal_keybindings(),
    )));

    let prompt = RushPrompt::new();

    // REPL loop
    loop {
        match editor.read_line(&prompt) {
            Ok(Signal::Success(line)) => {
                let trimmed = line.trim();
                if trimmed.is_empty() {
                    continue;
                }

                // Shell builtins
                match trimmed {
                    "exit" | "quit" => break,
                    s if s.starts_with("cd ") || s == "cd" => {
                        handle_cd(&mut evaluator, trimmed);
                        continue;
                    }
                    _ => {}
                }

                crate::run_line(&mut evaluator, trimmed);
            }
            Ok(Signal::CtrlC) => {
                // Clear line, continue
            }
            Ok(Signal::CtrlD) => {
                break;
            }
            Err(e) => {
                eprintln!("rush: input error: {e}");
                break;
            }
        }
    }
}

/// Handle cd builtin — must run in-process.
fn handle_cd(evaluator: &mut Evaluator, line: &str) {
    let target = line.strip_prefix("cd").unwrap_or("").trim();
    let path = if target.is_empty() || target == "~" {
        std::env::var("HOME")
            .or_else(|_| std::env::var("USERPROFILE"))
            .unwrap_or_else(|_| ".".to_string())
    } else if let Some(rest) = target.strip_prefix("~/") {
        let home = std::env::var("HOME")
            .or_else(|_| std::env::var("USERPROFILE"))
            .unwrap_or_default();
        format!("{home}/{rest}")
    } else {
        target.to_string()
    };

    match std::env::set_current_dir(&path) {
        Ok(()) => {
            evaluator.exit_code = 0;
        }
        Err(e) => {
            eprintln!("cd: {path}: {e}");
            evaluator.exit_code = 1;
        }
    }
}
