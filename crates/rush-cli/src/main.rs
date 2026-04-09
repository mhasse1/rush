mod builtins;
mod completer;
mod highlighter;
mod prompt;
mod repl;
mod signals;
mod validator;

use rush_core::dispatch;
use rush_core::eval::{Evaluator, StdOutput};
use rush_core::lexer::Lexer;
use rush_core::parser;
use std::io;

/// Build-time version: Cargo.toml version + git SHA
pub fn rush_version() -> String {
    let base = env!("CARGO_PKG_VERSION");
    let sha = env!("RUSH_GIT_SHA");
    let dirty = env!("RUSH_GIT_DIRTY");
    if sha.is_empty() {
        base.to_string()
    } else {
        format!("{base} ({sha}{dirty})")
    }
}

/// Short version for banners
pub fn rush_version_short() -> &'static str {
    env!("CARGO_PKG_VERSION")
}

fn main() {
    // Install signal handlers before anything else
    signals::install();
    signals::update_terminal_size();

    // Set SECONDS baseline
    let start_secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs()).unwrap_or(0);
    unsafe { std::env::set_var("RUSH_START_TIME", start_secs.to_string()) };

    // Set RUSHPATH for child processes (MCP-SSH uses this to find Rush)
    if let Ok(exe) = std::env::current_exe() {
        if let Ok(resolved) = std::fs::canonicalize(&exe) {
            unsafe { std::env::set_var("RUSHPATH", resolved.to_string_lossy().as_ref()) };
        }
    }

    let args: Vec<String> = std::env::args().collect();

    // Login shell detection: argv[0] starts with '-' or --login/-l flag
    let is_login = args[0].starts_with('-')
        || args.iter().any(|a| a == "--login" || a == "-l");

    // Strip --login/-l from args for further processing
    let args: Vec<String> = args.into_iter()
        .filter(|a| a != "--login" && a != "-l")
        .collect();

    // rush --version / -v
    if args.get(1).is_some_and(|a| a == "--version" || a == "-v") {
        println!("rush {}", rush_version());
        return;
    }

    // rush --help / -h
    if args.get(1).is_some_and(|a| a == "--help" || a == "-h") {
        println!("rush {} — a modern-day warrior", rush_version());
        println!();
        println!("Usage:");
        println!("  rush                           Start interactive shell");
        println!("  rush script.rush [args]        Execute Rush script file");
        println!("  rush -c 'command'              Execute command and exit");
        println!("  rush --llm                     LLM wire protocol mode (JSON I/O)");
        println!("  rush --mcp                     MCP server mode (JSON-RPC over stdio)");
        println!("  rush --mcp-ssh                 MCP SSH gateway (dynamic multi-host)");
        println!("  rush install mcp --claude      Install MCP servers into Claude");
        println!("  rush --login                   Start as login shell");
        println!("  rush --version                 Show version");
        println!("  rush --help                    Show this help");
        return;
    }

    // rush --lex: dump tokens
    if args.get(1).is_some_and(|a| a == "--lex") {
        let input = read_input(args.get(2));
        let tokens = Lexer::new(&input).tokenize();
        for token in &tokens {
            println!("{token}");
        }
        return;
    }

    // rush --parse: dump AST
    if args.get(1).is_some_and(|a| a == "--parse") {
        let input = read_input(args.get(2));
        match parser::parse(&input) {
            Ok(nodes) => {
                for node in &nodes {
                    println!("{node:#?}");
                }
            }
            Err(e) => {
                eprintln!("Parse error: {e}");
                std::process::exit(1);
            }
        }
        return;
    }

    // rush --llm: LLM wire protocol mode
    if args.get(1).is_some_and(|a| a == "--llm") {
        rush_core::llm::run();
        return;
    }

    // rush --mcp: MCP server mode (JSON-RPC 2.0 over stdio)
    if args.get(1).is_some_and(|a| a == "--mcp") {
        rush_core::mcp::run();
        return;
    }

    // rush --mcp-ssh: MCP SSH gateway mode
    if args.get(1).is_some_and(|a| a == "--mcp-ssh") {
        rush_core::mcp_ssh::run();
        return;
    }

    // rush install mcp --claude
    if args.get(1).is_some_and(|a| a == "install")
        && args.get(2).is_some_and(|a| a == "mcp")
        && args.iter().any(|a| a == "--claude")
    {
        rush_core::mcp_install::install_claude();
        return;
    }

    // rush -c "command": execute and exit
    if args.get(1).is_some_and(|a| a == "-c") {
        if let Some(cmd) = args.get(2) {
            let mut output = StdOutput;
            let mut evaluator = Evaluator::new(&mut output);
            builtins::inject_env_vars(is_login);
            builtins::inject_builtin_vars(&mut evaluator);
            run_line(&mut evaluator, cmd);
            std::process::exit(evaluator.exit_code);
        }
        return;
    }

    // rush <file> [args...]: run a script
    if let Some(file) = args.get(1) {
        if !file.starts_with('-') {
            let input = std::fs::read_to_string(file).unwrap_or_else(|e| {
                eprintln!("rush: cannot read {file}: {e}");
                std::process::exit(1);
            });
            let mut output = StdOutput;
            let mut evaluator = Evaluator::new(&mut output);
            builtins::inject_env_vars(is_login);
            builtins::inject_builtin_vars(&mut evaluator);

            // Set ARGV and __FILE__
            let script_args: Vec<rush_core::value::Value> = args[2..]
                .iter()
                .map(|a| rush_core::value::Value::String(a.clone()))
                .collect();
            evaluator.env.set("ARGV", rush_core::value::Value::Array(script_args.clone()));
            evaluator.env.set("__FILE__", rush_core::value::Value::String(file.clone()));

            // POSIX special params for $@ $# $0
            let argv_str = args[2..].join(" ");
            unsafe {
                std::env::set_var("RUSH_ARGC", (args.len() - 2).to_string());
                std::env::set_var("RUSH_ARGV", &argv_str);
                std::env::set_var("RUSH_SCRIPT_NAME", file);
            }
            if let Some(dir) = std::path::Path::new(file).parent() {
                evaluator.env.set("__DIR__", rush_core::value::Value::String(
                    dir.to_string_lossy().to_string(),
                ));
            }

            // Per-line dispatch: handles builtins, Rush syntax, shell commands,
            // heredocs, and mixed content — same as init.rush
            builtins::run_script(&mut evaluator, &input, file);
            std::process::exit(evaluator.exit_code);
        }
    }

    // Interactive REPL with reedline
    repl::run(is_login);
}

/// Execute a line through the unified dispatch system.
/// Checks builtins first (in-process), then delegates to dispatch for
/// chain operators, triage, Rush eval, and shell execution.
pub fn run_line(evaluator: &mut Evaluator, line: &str) {
    let trimmed = line.trim();

    // Only check builtins for simple commands (no chain operators).
    // This ensures "set -e; cmd" goes through dispatch for proper chain splitting.
    if !trimmed.contains("&&") && !trimmed.contains("||") && !trimmed.contains(';') {
        if builtins::handle(evaluator, trimmed) {
            return;
        }
    }

    builtins::JOB_TABLE.with(|jt| {
        dispatch::dispatch_with_jobs(trimmed, evaluator, Some(&mut jt.borrow_mut()));
    });
}

fn read_input(file: Option<&String>) -> String {
    if let Some(file) = file {
        std::fs::read_to_string(file).unwrap_or_else(|e| {
            eprintln!("rush: cannot read {file}: {e}");
            std::process::exit(1);
        })
    } else {
        let mut buf = String::new();
        io::Read::read_to_string(&mut io::stdin().lock(), &mut buf).ok();
        buf
    }
}
