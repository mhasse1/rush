use reedline::{
    default_emacs_keybindings, default_vi_insert_keybindings, default_vi_normal_keybindings,
    ColumnarMenu, Emacs, FileBackedHistory, KeyCode, KeyModifiers, Reedline, ReedlineEvent,
    ReedlineMenu, Signal, Vi,
};

use rush_core::config::RushConfig;
use rush_core::eval::{Evaluator, StdOutput};
use rush_core::value::Value;

use crate::builtins;
use crate::completer::RushCompleter;
use crate::highlighter::RushHighlighter;
use crate::prompt::RushPrompt;
use crate::validator::RushValidator;

fn history_path() -> std::path::PathBuf {
    let home = std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .unwrap_or_else(|_| ".".to_string());
    let config_dir = std::path::PathBuf::from(home).join(".config").join("rush");
    std::fs::create_dir_all(&config_dir).ok();
    config_dir.join("history")
}

/// Run the interactive REPL.
pub fn run() {
    let config = RushConfig::load();
    let mut output = StdOutput;
    let mut evaluator = Evaluator::new(&mut output);

    // Inject built-in variables
    builtins::inject_builtin_vars(&mut evaluator);

    // Load config, secrets, aliases, and init file
    builtins::load_aliases_from_config();
    builtins::load_secrets(&mut evaluator);
    builtins::load_init(&mut evaluator);

    // Build reedline editor
    let history = FileBackedHistory::with_file(config.history_size, history_path())
        .expect("Failed to create history file");

    let completer = Box::new(RushCompleter::new());
    let completion_menu = Box::new(ColumnarMenu::default());

    // Edit mode from config
    let edit_mode: Box<dyn reedline::EditMode> = if config.edit_mode == "emacs" {
        Box::new(Emacs::new(default_emacs_keybindings()))
    } else {
        let mut insert_bindings = default_vi_insert_keybindings();
        insert_bindings.add_binding(
            KeyModifiers::NONE,
            KeyCode::Tab,
            ReedlineEvent::UntilFound(vec![
                ReedlineEvent::Menu("columnar_menu".to_string()),
                ReedlineEvent::MenuNext,
            ]),
        );
        Box::new(Vi::new(insert_bindings, default_vi_normal_keybindings()))
    };

    let mut editor = Reedline::create()
        .with_edit_mode(edit_mode)
        .with_history(Box::new(history))
        .with_completer(completer)
        .with_menu(ReedlineMenu::EngineCompleter(completion_menu))
        .with_highlighter(Box::new(RushHighlighter))
        .with_validator(Box::new(RushValidator))
        .with_ansi_colors(true);

    let prompt = RushPrompt::new();
    let show_timing = config.show_timing;

    // REPL loop
    loop {
        evaluator.env.set("$?", Value::Int(evaluator.exit_code as i64));

        match editor.read_line(&prompt) {
            Ok(Signal::Success(line)) => {
                let trimmed = line.trim();
                if trimmed.is_empty() {
                    continue;
                }

                // Shell builtins
                if builtins::handle(&mut evaluator, trimmed) {
                    continue;
                }

                // Execute with timing
                let start = std::time::Instant::now();
                crate::run_line(&mut evaluator, trimmed);
                let elapsed = start.elapsed();

                // Show timing for slow commands (>500ms)
                if show_timing && elapsed.as_millis() > 500 {
                    let secs = elapsed.as_secs_f64();
                    if secs >= 60.0 {
                        let mins = secs as u64 / 60;
                        let rem = secs % 60.0;
                        eprintln!("\x1b[2m{mins}m{rem:.1}s\x1b[0m");
                    } else {
                        eprintln!("\x1b[2m{secs:.1}s\x1b[0m");
                    }
                }
            }
            Ok(Signal::CtrlC) => {}
            Ok(Signal::CtrlD) => break,
            Err(e) => {
                eprintln!("rush: input error: {e}");
                break;
            }
        }
    }
}
