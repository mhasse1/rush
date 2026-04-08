use reedline::{
    default_emacs_keybindings, default_vi_insert_keybindings, default_vi_normal_keybindings,
    ColumnarMenu, Emacs, FileBackedHistory, KeyCode, KeyModifiers, Reedline, ReedlineEvent,
    ReedlineMenu, Signal, Vi,
};

use rush_core::config::RushConfig;
use rush_core::eval::{Evaluator, StdOutput};
use rush_core::theme;
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

    // Initialize theme (dark/light detection, LS_COLORS, GREP_COLORS)
    let detected_theme = theme::initialize();

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

    let mut prompt = RushPrompt::new(detected_theme);
    let show_timing = config.show_timing;
    let mut last_cmd: Option<String> = None;

    // Banner
    let version = "0.1.0";
    let mode = if config.edit_mode == "emacs" { "emacs" } else { "vi" };
    let theme_name = if detected_theme.is_dark { "dark" } else { "light" };
    println!("{}rush v{version} — a modern-day warrior{}", detected_theme.muted, detected_theme.reset);
    println!("{}Rust engine | {mode} mode | Tab | Ctrl+R | {theme_name}{}", detected_theme.muted, detected_theme.reset);
    println!();

    // REPL loop
    loop {
        evaluator.env.set("$?", Value::Int(evaluator.exit_code as i64));
        prompt.set_exit_code(evaluator.exit_code);

        match editor.read_line(&prompt) {
            Ok(Signal::Success(line)) => {
                let trimmed = line.trim();
                if trimmed.is_empty() {
                    continue;
                }

                // History expansion: !!, !$, !N
                let expanded = expand_history(trimmed, &last_cmd);
                let trimmed = expanded.as_deref().unwrap_or(trimmed);

                // --help routing: "file --help" → "help file"
                let trimmed = route_help(trimmed);
                let trimmed = trimmed.as_ref();

                last_cmd = Some(trimmed.to_string());

                // Execute through unified dispatch
                let start = std::time::Instant::now();
                crate::run_line(&mut evaluator, trimmed);
                let elapsed = start.elapsed();

                if show_timing && elapsed.as_millis() > 500 {
                    let secs = elapsed.as_secs_f64();
                    if secs >= 60.0 {
                        let mins = secs as u64 / 60;
                        let rem = secs % 60.0;
                        eprintln!("{}  {mins}m{rem:.1}s{}", detected_theme.muted, detected_theme.reset);
                    } else {
                        eprintln!("{}  {secs:.1}s{}", detected_theme.muted, detected_theme.reset);
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

/// Expand history references: !!, !$, !N
fn expand_history(line: &str, last_cmd: &Option<String>) -> Option<String> {
    if !line.contains('!') {
        return None;
    }

    let last = last_cmd.as_deref().unwrap_or("");

    if line == "!!" {
        if !last.is_empty() {
            println!("{last}");
            return Some(last.to_string());
        }
        return None;
    }

    if line.contains("!$") {
        let last_arg = last.split_whitespace().last().unwrap_or("");
        let expanded = line.replace("!$", last_arg);
        println!("{expanded}");
        return Some(expanded);
    }

    if line.starts_with('!') && line.len() > 1 {
        let rest = &line[1..];
        if let Ok(n) = rest.parse::<usize>() {
            let home = std::env::var("HOME").unwrap_or_default();
            let history_path = format!("{home}/.config/rush/history");
            if let Ok(content) = std::fs::read_to_string(&history_path) {
                let lines: Vec<&str> = content.lines().collect();
                if n > 0 && n <= lines.len() {
                    let cmd = lines[n - 1].to_string();
                    println!("{cmd}");
                    return Some(cmd);
                }
            }
        }
        // !prefix — find last command starting with prefix
        let prefix = rest;
        let home = std::env::var("HOME").unwrap_or_default();
        let history_path = format!("{home}/.config/rush/history");
        if let Ok(content) = std::fs::read_to_string(&history_path) {
            for line in content.lines().rev() {
                if line.starts_with(prefix) {
                    println!("{line}");
                    return Some(line.to_string());
                }
            }
        }
    }

    None
}

/// Route "--help" flag to help system: "file --help" → "help file"
fn route_help(line: &str) -> std::borrow::Cow<'_, str> {
    if line.ends_with(" --help") || line.ends_with(" -h") {
        let word = line.split_whitespace().next().unwrap_or("");
        let topic = match word.to_lowercase().as_str() {
            "file" => "file",
            "dir" => "dir",
            "time" => "time",
            "if" | "while" | "for" | "unless" | "until" | "loop" | "case" => "control-flow",
            "def" | "return" => "functions",
            "class" | "attr" => "classes",
            "ai" => "ai",
            "where" | "select" | "sort" | "objectify" | "as" | "from" => "pipes",
            _ => return std::borrow::Cow::Borrowed(line),
        };
        std::borrow::Cow::Owned(format!("help {topic}"))
    } else {
        std::borrow::Cow::Borrowed(line)
    }
}
