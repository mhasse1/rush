mod completer;
mod highlighter;
mod prompt;
mod repl;

use rush_core::eval::{Evaluator, StdOutput};
use rush_core::lexer::Lexer;
use rush_core::parser;
use rush_core::pipeline;
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
/// Handles pipeline operators (| where, | sort, | as json, etc.)
pub fn run_line(evaluator: &mut Evaluator, line: &str) {
    // Check for Rush pipeline operators in the line
    let segments = pipeline::split_pipeline(line);
    if segments.len() > 1 && has_rush_pipe_ops(&segments) {
        run_pipeline(evaluator, &segments);
        return;
    }

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

/// Check if any segment after the first is a Rush pipeline operator.
fn has_rush_pipe_ops(segments: &[String]) -> bool {
    segments.iter().skip(1).any(|seg| {
        let first_word = seg.split_whitespace().next().unwrap_or("");
        pipeline::is_pipe_op(first_word)
    })
}

/// Execute a pipeline: first segment produces data, subsequent ops transform it.
fn run_pipeline(evaluator: &mut Evaluator, segments: &[String]) {
    if segments.is_empty() { return; }

    // Execute first segment to get initial data
    let first = &segments[0];
    let mut value = if should_run_as_shell(evaluator, first) {
        let result = process::run_shell_capture(first);
        evaluator.exit_code = result.exit_code;
        if !result.stderr.is_empty() {
            eprintln!("{}", result.stderr);
        }
        pipeline::text_to_array(&result.stdout)
    } else {
        match parser::parse(first) {
            Ok(nodes) => evaluator.exec_toplevel(&nodes).unwrap_or(rush_core::value::Value::Nil),
            Err(_) => {
                let result = process::run_shell_capture(first);
                evaluator.exit_code = result.exit_code;
                pipeline::text_to_array(&result.stdout)
            }
        }
    };

    // Apply each pipeline operator
    for segment in &segments[1..] {
        let first_word = segment.split_whitespace().next().unwrap_or("");
        if pipeline::is_pipe_op(first_word) {
            let op = pipeline::parse_pipe_op(segment);
            value = pipeline::apply_pipe_op(value, &op);
        } else {
            // Not a Rush pipe op — fall back to shell for this segment
            // (e.g., `ls | wc -l` where wc is a shell command)
            let input_text = value.to_rush_string();
            let result = process::run_shell_capture(&format!("echo {} | {}", shell_quote(&input_text), segment));
            evaluator.exit_code = result.exit_code;
            value = rush_core::value::Value::String(result.stdout.trim_end().to_string());
        }
    }

    // Print the final result
    let output = value.to_rush_string();
    if !output.is_empty() {
        println!("{output}");
    }
}

fn shell_quote(s: &str) -> String {
    format!("'{}'", s.replace('\'', "'\\''"))
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
