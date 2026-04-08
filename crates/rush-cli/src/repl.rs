use reedline::{
    default_vi_insert_keybindings, default_vi_normal_keybindings, ColumnarMenu, FileBackedHistory,
    KeyCode, KeyModifiers, Reedline, ReedlineEvent, ReedlineMenu, Signal, Vi,
};

use rush_core::eval::{Evaluator, StdOutput};
use rush_core::value::Value;

use crate::builtins;
use crate::completer::RushCompleter;
use crate::highlighter::RushHighlighter;
use crate::prompt::RushPrompt;
use crate::validator::RushValidator;

/// History file location.
fn history_path() -> std::path::PathBuf {
    let config_dir = config_dir();
    std::fs::create_dir_all(&config_dir).ok();
    config_dir.join("history")
}

fn config_dir() -> std::path::PathBuf {
    let home = std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .unwrap_or_else(|_| ".".to_string());
    std::path::PathBuf::from(home).join(".config").join("rush")
}

/// Run the interactive REPL.
pub fn run() {
    let mut output = StdOutput;
    let mut evaluator = Evaluator::new(&mut output);

    // Inject built-in variables
    builtins::inject_builtin_vars(&mut evaluator);

    // Load config, secrets, aliases, and init file
    builtins::load_aliases_from_config();
    builtins::load_secrets(&mut evaluator);
    builtins::load_init(&mut evaluator);

    // Build reedline editor
    let history = FileBackedHistory::with_file(10_000, history_path())
        .expect("Failed to create history file");

    let completer = Box::new(RushCompleter::new());
    let completion_menu = Box::new(ColumnarMenu::default());

    // Bind Tab to completion menu in vi insert mode
    let mut insert_bindings = default_vi_insert_keybindings();
    insert_bindings.add_binding(
        KeyModifiers::NONE,
        KeyCode::Tab,
        ReedlineEvent::UntilFound(vec![
            ReedlineEvent::Menu("columnar_menu".to_string()),
            ReedlineEvent::MenuNext,
        ]),
    );

    let editor = Reedline::create()
        .with_edit_mode(Box::new(Vi::new(
            insert_bindings,
            default_vi_normal_keybindings(),
        )))
        .with_history(Box::new(history))
        .with_completer(completer)
        .with_menu(ReedlineMenu::EngineCompleter(completion_menu))
        .with_highlighter(Box::new(RushHighlighter))
        .with_validator(Box::new(RushValidator))
        .with_ansi_colors(true);

    let mut editor = editor;
    let prompt = RushPrompt::new();

    // REPL loop
    loop {
        // Update $? before each prompt
        evaluator.env.set("$?", Value::Int(evaluator.exit_code as i64));

        match editor.read_line(&prompt) {
            Ok(Signal::Success(line)) => {
                let trimmed = line.trim();
                if trimmed.is_empty() {
                    continue;
                }

                // Shell builtins (cd, export, source, clear, etc.)
                if builtins::handle(&mut evaluator, trimmed) {
                    continue;
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
