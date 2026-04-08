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

fn main() {
    // Install signal handlers before anything else
    signals::install();
    signals::update_terminal_size();

    let args: Vec<String> = std::env::args().collect();

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

    // rush -c "command": execute and exit
    if args.get(1).is_some_and(|a| a == "-c") {
        if let Some(cmd) = args.get(2) {
            let mut output = StdOutput;
            let mut evaluator = Evaluator::new(&mut output);
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

            // Set ARGV and __FILE__
            let script_args: Vec<rush_core::value::Value> = args[2..]
                .iter()
                .map(|a| rush_core::value::Value::String(a.clone()))
                .collect();
            evaluator.env.set("ARGV", rush_core::value::Value::Array(script_args));
            evaluator.env.set("__FILE__", rush_core::value::Value::String(file.clone()));
            if let Some(dir) = std::path::Path::new(file).parent() {
                evaluator.env.set("__DIR__", rush_core::value::Value::String(
                    dir.to_string_lossy().to_string(),
                ));
            }

            match parser::parse(&input) {
                Ok(nodes) => {
                    if let Err(e) = evaluator.exec_toplevel(&nodes) {
                        eprintln!("rush: {e}");
                        std::process::exit(1);
                    }
                }
                Err(e) => {
                    eprintln!("rush: {e}");
                    std::process::exit(1);
                }
            }
            std::process::exit(evaluator.exit_code);
        }
    }

    // Interactive REPL with reedline
    repl::run();
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
