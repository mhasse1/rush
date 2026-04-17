use reedline::{
    default_emacs_keybindings, default_vi_insert_keybindings, default_vi_normal_keybindings,
    ColumnarMenu, DefaultHinter, Emacs, FileBackedHistory, KeyCode, KeyModifiers, Reedline,
    ReedlineEvent, ReedlineMenu, Signal, Vi,
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
pub fn run(is_login: bool) {
    let config = RushConfig::load();

    // Theming is opt-in. Promote the saved config bg and any
    // per-project .rushbg into RUSH_BG *if RUSH_BG isn't already set*
    // (an explicit env override wins), so theme::detect sees it.
    if std::env::var_os("RUSH_BG").is_none() {
        if !config.bg.is_empty() {
            unsafe { std::env::set_var("RUSH_BG", &config.bg) };
        }
        if let Some(bg) = theme::load_rushbg() {
            unsafe { std::env::set_var("RUSH_BG", &bg) };
        }
    }

    // Record the session baseline bg so `.rushbg` autoload (on cd) can
    // revert to it when leaving an override directory.
    builtins::set_baseline_bg(&config.bg);

    let detected_theme = theme::initialize();

    let mut output = StdOutput;
    let mut evaluator = Evaluator::new(&mut output);

    // Inject env vars and built-in variables
    builtins::inject_env_vars(is_login);
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
        let mut normal_bindings = default_vi_normal_keybindings();
        if has_fzf() {
            // fzf available: Ctrl+R, /, and ? all go to fzf
            let fzf_event = ReedlineEvent::ExecuteHostCommand("__fzf_history__".to_string());
            // Ctrl+R in both modes
            insert_bindings.add_binding(
                KeyModifiers::CONTROL, KeyCode::Char('r'), fzf_event.clone(),
            );
            normal_bindings.add_binding(
                KeyModifiers::CONTROL, KeyCode::Char('r'), fzf_event.clone(),
            );
            // / and ? in vi normal mode
            normal_bindings.add_binding(
                KeyModifiers::NONE, KeyCode::Char('/'), fzf_event.clone(),
            );
            normal_bindings.add_binding(
                KeyModifiers::NONE, KeyCode::Char('?'), fzf_event.clone(),
            );
            // Alt+/ and Alt+? in insert mode (Esc+/ sends Alt+/ in most terminals)
            insert_bindings.add_binding(
                KeyModifiers::ALT, KeyCode::Char('/'), fzf_event.clone(),
            );
            insert_bindings.add_binding(
                KeyModifiers::ALT, KeyCode::Char('?'), fzf_event,
            );
        }
        // Without fzf: / and ? use reedline's built-in search (from our fork)
        Box::new(Vi::new(insert_bindings, normal_bindings))
    };

    // Autosuggestions: most recent matching history entry, contrast-aware color
    let hint_color = nu_ansi_term::Color::Fixed(detected_theme.muted_color_index());
    let hinter = Box::new(
        DefaultHinter::default()
            .with_style(nu_ansi_term::Style::new().fg(hint_color))
    );

    // Configure external editor for vi 'v' command
    let buffer_editor_cmd = {
        let editor_var = std::env::var("VISUAL")
            .or_else(|_| std::env::var("EDITOR"))
            .unwrap_or_else(|_| "vi".to_string());
        std::process::Command::new(editor_var)
    };
    let temp_file = std::env::temp_dir().join("rush_edit_buffer.rush");

    let mut editor = Reedline::create()
        .with_edit_mode(edit_mode)
        .with_history(Box::new(history))
        .with_history_exclusion_prefix(Some(" ".to_string()))
        .with_completer(completer)
        .with_hinter(hinter)
        .with_menu(ReedlineMenu::EngineCompleter(completion_menu))
        .with_highlighter(Box::new(RushHighlighter))
        .with_validator(Box::new(RushValidator))
        .with_buffer_editor(buffer_editor_cmd, temp_file)
        .with_quick_completions(false)
        .with_partial_completions(true)
        .with_immediate_completions(false)
        .with_ansi_colors(true);

    let show_timing = config.show_timing;
    let mut last_cmd: Option<String> = None;

    // Banner
    let version = crate::rush_version();
    let mode = if config.edit_mode == "emacs" { "emacs" } else { "vi" };
    let theme_name = if detected_theme.is_dark { "dark" } else { "light" };
    let has_256 = detected_theme.bg_rgb.is_some();
    let color_info = if has_256 { "256-color" } else { "16-color" };
    println!("{}rush v{version} — a modern-day warrior{}", detected_theme.muted, detected_theme.reset);
    println!("{}Rust engine | {mode} mode | Tab | Ctrl+R | {theme_name} {color_info}{}", detected_theme.muted, detected_theme.reset);

    // Rotating startup tip (one per session, changes daily)
    if config.show_tips() {
        let tips = [
            // Navigation
            "cd -  — jump back to previous directory",
            "pushd /tmp && popd  — directory stack",
            // History
            "!!  — repeat last command  |  !$  — reuse last argument",
            "Ctrl+R  — search history  |  /pattern (vi normal mode)",
            // Pipes & Filters
            "ps aux | where CPU > 50 | select USER, PID, COMMAND",
            "ls | count  — count items (also: sum, avg, min, max)",
            "ls -la | first 5  — slice results (also: last, skip)",
            "df -h | where Capacity > 80%  — filter by field",
            "cat data.json | from json | select name, version",
            "ls | as json  — format as JSON (also: csv)",
            // Stdlib
            "File.read_lines(\"log.txt\") | where /error/i | count",
            "Dir.list(\"src\", :recurse) | where /\\.rs$/",
            "\"hello world\".upcase.split(\" \").reverse.join(\"-\")",
            "Path.join(\"src\", \"main.rs\")  — cross-platform paths",
            // PATH
            "path  — list PATH entries with existence check",
            "path add ~/bin --save  — persist to init.rush",
            "path dedupe  — remove duplicate PATH entries",
            // Settings
            "set  — show all settings  |  set -x  — trace mode",
            "set --secret ANTHROPIC_API_KEY \"sk-...\"  — save API key",
            "alias ll='ls -la' --save  — persistent alias",
            "setbg --selector  — pick terminal background color",
            // AI & Integration
            "ai \"explain this error\"  — ask AI from the prompt",
            "rush --mcp  — MCP server for Claude Code",
            "plugin.ps ... end  — run PowerShell blocks",
            // Help
            "help  — list all topics  |  help pipes  — pipeline operators",
            "help objectify  — structured data from any command",
            "file --help  — builtins support --help",
        ];
        let hint_idx = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| (d.as_secs() / 86400) as usize % tips.len())
            .unwrap_or(0);
        println!("{}  tip: {}{}", detected_theme.muted, tips[hint_idx], detected_theme.reset);
    }

    // First-run welcome
    let config_path = std::path::PathBuf::from(std::env::var("HOME").unwrap_or_default())
        .join(".config").join("rush").join("config.json");
    if !config_path.exists() {
        println!();
        println!("{}Welcome to Rush! Quick tips:{}", detected_theme.prompt_git_branch, detected_theme.reset);
        println!("{}  alias ll='ls -la'          Define alias (--save to persist){}", detected_theme.muted, detected_theme.reset);
        println!("{}  path add ~/bin             Add to PATH (--save to persist){}", detected_theme.muted, detected_theme.reset);
        println!("{}  set emacs                  Switch to emacs mode{}", detected_theme.muted, detected_theme.reset);
        println!("{}  setbg #282828 --save       Set terminal background{}", detected_theme.muted, detected_theme.reset);
        println!("{}  help                       Show all commands{}", detected_theme.muted, detected_theme.reset);
        // Create empty config so this only shows once
        std::fs::create_dir_all(config_path.parent().unwrap()).ok();
        if let Ok(cfg) = serde_json::to_string_pretty(&config) {
            std::fs::write(&config_path, cfg).ok();
        }
    }

    // Windows: check for coreutils (provides ls, cat, grep, etc.)
    #[cfg(windows)]
    {
        if crate::builtins::check_windows_coreutils(&detected_theme) {
            // coreutils found or hint shown
        }
    }

    let mut prompt = RushPrompt::new(detected_theme.clone());
    println!();

    // REPL loop
    loop {
        // Check for signal exit (SIGHUP/SIGTERM)
        if crate::signals::should_exit() {
            break;
        }

        // Report completed background jobs
        crate::builtins::JOB_TABLE.with(|jt| jt.borrow_mut().report_done());

        evaluator.env.set("$?", Value::Int(evaluator.exit_code as i64));
        prompt.set_exit_code(evaluator.exit_code);

        match editor.read_line(&prompt) {
            Ok(Signal::Success(line)) => {
                // fzf history search: intercept host command
                if line == "__fzf_history__" {
                    if let Some(selected) = fzf_history_search() {
                        // Put selected command on the line and execute it
                        let trimmed = selected.trim();
                        if !trimmed.is_empty() {
                            println!("{trimmed}");
                            last_cmd = Some(trimmed.to_string());
                            let start = std::time::Instant::now();
                            crate::run_line(&mut evaluator, trimmed);
                            let elapsed = start.elapsed();
                            if show_timing && elapsed.as_millis() > 500 {
                                let secs = elapsed.as_secs_f64();
                                eprintln!("{}  {secs:.1}s{}", detected_theme.muted, detected_theme.reset);
                            }
                        }
                    }
                    continue;
                }

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

                // Remove meta-commands from history (noise in autosuggestions)
                let first_word = trimmed.split_whitespace().next().unwrap_or("");
                if matches!(first_word, "history" | "clear" | "exit") {
                    editor.delete_last_history_entry();
                }

                // Sync history to disk after every command (like bash histappend).
                let _ = editor.sync_history();

                // Execute through unified dispatch
                let start = std::time::Instant::now();
                crate::run_line(&mut evaluator, trimmed);
                let elapsed = start.elapsed();

                // Timing for slow commands
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

                // Hints: error hints after failures, training hints after successes
                if evaluator.exit_code != 0 {
                    if let Some(hint) = rush_core::hints::hint_for_command(trimmed, evaluator.exit_code) {
                        eprintln!("{}  {hint}{}", detected_theme.muted, detected_theme.reset);
                    }
                } else {
                    // Training hints: suggest Rush alternatives for bash patterns
                    if let Some(hint) = rush_core::hints::training_hint(trimmed) {
                        eprintln!();
                        eprintln!("{}  {hint}{}", detected_theme.muted, detected_theme.reset);
                    }
                }
            }
            Ok(Signal::CtrlC) => {}
            Ok(Signal::CtrlD) => break,
            Ok(_) => {} // other signals
            Err(e) => {
                eprintln!("rush: input error: {e}");
                break;
            }
        }
    }

    // Fire EXIT trap if set
    if let Some(action) = rush_core::trap::get_exit_trap() {
        if !action.is_empty() {
            crate::run_line(&mut evaluator, &action);
        }
    }

    // Send SIGHUP to all background jobs (POSIX requirement)
    crate::builtins::JOB_TABLE.with(|jt| jt.borrow_mut().shutdown());
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

// ── fzf integration ────────────────────────────────────────────────

/// Check if fzf is available on PATH.
fn has_fzf() -> bool {
    rush_core::process::command_exists("fzf")
}

/// Run fzf with history file as input. Returns selected line or None.
fn fzf_history_search() -> Option<String> {
    let history_path = std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .map(|h| format!("{h}/.config/rush/history"))
        .ok()?;

    if !std::path::Path::new(&history_path).exists() {
        return None;
    }

    // Run fzf with history piped in, most recent first
    // tac reverses the file so recent commands appear first
    let output = if cfg!(unix) {
        std::process::Command::new("sh")
            .args(["-c", &format!(
                "tac '{}' 2>/dev/null || tail -r '{}' | awk '!seen[$0]++' | fzf --height 40% --reverse --no-sort --exact --prompt '/ '",
                history_path, history_path
            )])
            .stdin(std::process::Stdio::inherit())
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::inherit())
            .output()
            .ok()?
    } else {
        // Windows: no tac, just pipe history directly
        std::process::Command::new("cmd")
            .args(["/C", &format!(
                "type \"{}\" | fzf --height 40% --reverse --no-sort --exact --prompt \"/ \"",
                history_path
            )])
            .stdin(std::process::Stdio::inherit())
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::inherit())
            .output()
            .ok()?
    };

    if output.status.success() {
        let selected = String::from_utf8_lossy(&output.stdout).trim().to_string();
        if !selected.is_empty() {
            return Some(selected);
        }
    }

    None // user cancelled (Esc/Ctrl+C)
}
