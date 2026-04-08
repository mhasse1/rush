//! Shell builtins that must run in-process (cd, export, source, clear, etc.)

use rush_core::eval::Evaluator;

use std::cell::RefCell;

// Previous directory for `cd -`.
thread_local! {
    static OLDPWD: RefCell<Option<String>> = const { RefCell::new(None) };
}

/// Try to handle a line as a shell builtin. Returns true if handled.
pub fn handle(evaluator: &mut Evaluator, line: &str) -> bool {
    let parts: Vec<&str> = line.splitn(2, char::is_whitespace).collect();
    let cmd = parts[0];
    let args = parts.get(1).map(|s| s.trim()).unwrap_or("");

    match cmd {
        "cd" => { handle_cd(evaluator, args); true }
        "export" => { handle_export(args); true }
        "unset" => { handle_unset(args); true }
        "source" | "." => { handle_source(evaluator, args); true }
        "clear" => { print!("\x1b[2J\x1b[H"); true }
        "exit" | "quit" => std::process::exit(evaluator.exit_code),
        "pwd" => { println!("{}", std::env::current_dir().unwrap_or_default().display()); true }
        _ => false,
    }
}

// ── cd ──────────────────────────────────────────────────────────────

fn handle_cd(evaluator: &mut Evaluator, target: &str) {
    let current = std::env::current_dir()
        .map(|p| p.to_string_lossy().to_string())
        .unwrap_or_default();

    let path = if target.is_empty() || target == "~" {
        home_dir()
    } else if target == "-" {
        // cd - : go to previous directory
        let prev = OLDPWD.with(|p| p.borrow().clone()).unwrap_or_else(home_dir);
        println!("{prev}");
        prev
    } else if let Some(rest) = target.strip_prefix("~/") {
        format!("{}/{rest}", home_dir())
    } else {
        target.to_string()
    };

    match std::env::set_current_dir(&path) {
        Ok(()) => {
            OLDPWD.with(|p| *p.borrow_mut() = Some(current));
            evaluator.exit_code = 0;
        }
        Err(e) => {
            eprintln!("cd: {path}: {e}");
            evaluator.exit_code = 1;
        }
    }
}

// ── export ──────────────────────────────────────────────────────────

fn handle_export(args: &str) {
    if args.is_empty() {
        // Show all env vars
        let mut vars: Vec<(String, String)> = std::env::vars().collect();
        vars.sort_by(|a, b| a.0.cmp(&b.0));
        for (k, v) in vars {
            println!("export {k}={v}");
        }
        return;
    }

    // export FOO=bar or export FOO="bar baz"
    if let Some((key, value)) = args.split_once('=') {
        let key = key.trim();
        let value = value.trim().trim_matches('"').trim_matches('\'');
        // SAFETY: Rush is single-threaded
        unsafe { std::env::set_var(key, value) };
    } else {
        // export FOO — export existing shell var to env (no-op if not set)
        eprintln!("export: usage: export NAME=VALUE");
    }
}

// ── unset ───────────────────────────────────────────────────────────

fn handle_unset(args: &str) {
    for name in args.split_whitespace() {
        unsafe { std::env::remove_var(name) };
    }
}

// ── source ──────────────────────────────────────────────────────────

fn handle_source(evaluator: &mut Evaluator, path: &str) {
    if path.is_empty() {
        eprintln!("source: usage: source <file>");
        return;
    }

    let expanded = if let Some(rest) = path.strip_prefix("~/") {
        format!("{}/{rest}", home_dir())
    } else {
        path.to_string()
    };

    match std::fs::read_to_string(&expanded) {
        Ok(content) => {
            match rush_core::parser::parse(&content) {
                Ok(nodes) => {
                    if let Err(e) = evaluator.exec_toplevel(&nodes) {
                        eprintln!("source: {expanded}: {e}");
                    }
                }
                Err(e) => eprintln!("source: {expanded}: {e}"),
            }
        }
        Err(e) => eprintln!("source: {expanded}: {e}"),
    }
}

// ── init.rush loading ───────────────────────────────────────────────

/// Load ~/.config/rush/init.rush if it exists.
pub fn load_init(evaluator: &mut Evaluator) {
    let init_path = config_dir().join("init.rush");
    if init_path.exists() {
        if let Ok(content) = std::fs::read_to_string(&init_path) {
            match rush_core::parser::parse(&content) {
                Ok(nodes) => {
                    if let Err(e) = evaluator.exec_toplevel(&nodes) {
                        eprintln!("init.rush: {e}");
                    }
                }
                Err(e) => eprintln!("init.rush: {e}"),
            }
        }
    }
}

/// Load ~/.config/rush/secrets.rush if it exists (API keys, etc.)
pub fn load_secrets(_evaluator: &mut Evaluator) {
    let secrets_path = config_dir().join("secrets.rush");
    if secrets_path.exists() {
        if let Ok(content) = std::fs::read_to_string(&secrets_path) {
            // secrets.rush typically contains export statements
            for line in content.lines() {
                let line = line.trim();
                if line.is_empty() || line.starts_with('#') {
                    continue;
                }
                // Handle: export KEY=VALUE or KEY=VALUE
                let export_line = line.strip_prefix("export ").unwrap_or(line);
                if let Some((key, value)) = export_line.split_once('=') {
                    let key = key.trim();
                    let value = value.trim().trim_matches('"').trim_matches('\'');
                    unsafe { std::env::set_var(key, value) };
                }
            }
        }
    }
}

// ── Inject built-in variables ───────────────────────────────────────

/// Set up built-in Rush variables (`$os`, `$hostname`, etc.)
pub fn inject_builtin_vars(evaluator: &mut Evaluator) {
    use rush_core::value::Value;

    evaluator.env.set("$os", Value::String(std::env::consts::OS.to_string()));
    evaluator.env.set("$arch", Value::String(std::env::consts::ARCH.to_string()));
    evaluator.env.set("$hostname", Value::String(
        rush_core::llm::get_hostname()
    ));
    evaluator.env.set("$user", Value::String(
        rush_core::llm::get_username()
    ));
    evaluator.env.set("$rush_version", Value::String("0.1.0".to_string()));
    evaluator.env.set("$pid", Value::Int(std::process::id() as i64));
}

// ── Helpers ─────────────────────────────────────────────────────────

fn home_dir() -> String {
    std::env::var("HOME")
        .or_else(|_| std::env::var("USERPROFILE"))
        .unwrap_or_else(|_| ".".to_string())
}

fn config_dir() -> std::path::PathBuf {
    std::path::PathBuf::from(home_dir()).join(".config").join("rush")
}
