mod completer;
mod highlighter;
mod prompt;
mod repl;

use rush_core::eval::{Evaluator, StdOutput};
use rush_core::lexer::Lexer;
use rush_core::parser;
use rush_core::process;
use std::io;

fn main() {
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

    // rush <file>: run a script
    if let Some(file) = args.get(1) {
        if !file.starts_with('-') {
            let input = std::fs::read_to_string(file).unwrap_or_else(|e| {
                eprintln!("rush: cannot read {file}: {e}");
                std::process::exit(1);
            });
            let mut output = StdOutput;
            let mut evaluator = Evaluator::new(&mut output);
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

/// Execute a single line — try Rush parse first, fall back to shell execution.
pub fn run_line(evaluator: &mut Evaluator, line: &str) {
    if should_run_as_shell(evaluator, line) {
        let result = process::run_shell(line);
        evaluator.exit_code = result.exit_code;
        if !result.stderr.is_empty() {
            eprintln!("{}", result.stderr);
        }
        return;
    }

    match parser::parse(line) {
        Ok(nodes) => {
            if let Err(e) = evaluator.exec_toplevel(&nodes) {
                eprintln!("rush: {e}");
            }
        }
        Err(_) => {
            let result = process::run_shell(line);
            evaluator.exit_code = result.exit_code;
            if !result.stderr.is_empty() {
                eprintln!("{}", result.stderr);
            }
        }
    }
}

/// Heuristic: should this line be run as a shell command rather than parsed as Rush?
pub fn should_run_as_shell(evaluator: &Evaluator, line: &str) -> bool {
    let first_word = line.split_whitespace().next().unwrap_or("");

    let rush_keywords = [
        "if", "elsif", "else", "end", "for", "while", "until", "unless", "loop",
        "def", "class", "enum", "return", "try", "begin", "rescue", "ensure",
        "case", "match", "when", "puts", "print", "warn", "die", "true", "false",
        "nil", "break", "next", "continue", "macos", "linux", "win64", "win32",
        "ps", "ps5",
    ];
    if rush_keywords.iter().any(|k| k.eq_ignore_ascii_case(first_word)) {
        return false;
    }

    if line.contains(" = ") || line.contains(" += ") || line.contains(" -= ")
        || line.contains("#{") || line.starts_with('[') || line.starts_with('{')
    {
        return false;
    }

    if evaluator.env.functions.contains_key(first_word) {
        return false;
    }

    if process::command_exists(first_word) {
        return true;
    }

    if line.contains(" | ") || line.contains(" > ") || line.contains(" >> ") {
        return true;
    }

    false
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
