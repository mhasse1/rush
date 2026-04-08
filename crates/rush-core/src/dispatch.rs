//! Unified command dispatch — the Rust equivalent of C# ProcessCommand().
//!
//! Every command goes through this path: REPL, scripts, function bodies,
//! init.rush, -c mode. This ensures consistent behavior everywhere.

use crate::eval::Evaluator;
use crate::parser;
use crate::pipeline;
use crate::process;
use crate::triage;
use crate::value::Value;

/// Result of dispatching a command.
pub struct DispatchResult {
    pub exit_code: i32,
    pub should_exit: bool,
}

/// Shell builtins that must run in-process. Returns Some(exit_code) if handled.
pub type BuiltinHandler = dyn FnMut(&str, &str) -> Option<i32>;

/// Dispatch a command line through the full pipeline.
/// This is the single entry point for all command execution.
/// Builtins (cd, export, alias) should be checked by the caller before dispatch.
pub fn dispatch(
    line: &str,
    evaluator: &mut Evaluator,
    _builtin_handler: Option<&mut BuiltinHandler>,
) -> DispatchResult {
    let trimmed = line.trim();
    if trimmed.is_empty() {
        return DispatchResult { exit_code: 0, should_exit: false };
    }

    // Step 1: Split on chain operators (&&, ||, ;) respecting quotes
    let chains = split_chains(trimmed);

    let mut last_exit: i32 = 0;
    let mut last_failed = false;
    let mut should_exit = false;

    for chain in &chains {
        let segment = chain.command.trim();
        if segment.is_empty() {
            continue;
        }

        // Chain logic: skip based on && / || / ;
        match chain.operator {
            ChainOp::And if last_failed => continue,
            ChainOp::Or if !last_failed => continue,
            _ => {}
        }

        // Step 2: Extract inline env vars (VAR=val cmd)
        let (inline_vars, segment) = extract_inline_env_vars(segment);
        let segment = segment.as_str();

        // Set inline vars
        let mut saved_vars: Vec<(String, Option<String>)> = Vec::new();
        for (key, val) in &inline_vars {
            saved_vars.push((key.clone(), std::env::var(key).ok()));
            unsafe { std::env::set_var(key, val) };
        }

        // Step 2b: Check for ! negation
        let (negate, segment) = if segment.starts_with("! ") {
            (true, &segment[2..])
        } else {
            (false, segment)
        };

        let first_word = segment.split_whitespace().next().unwrap_or("");

        // exit/quit
        if first_word == "exit" || first_word == "quit" {
            should_exit = true;
            break;
        }

        // Step 3: Check for pipes
        let pipe_segments = pipeline::split_pipeline(segment);
        if pipe_segments.len() > 1 {
            if has_rush_pipe_ops(&pipe_segments) {
                // Rush pipeline operators (where, sort, etc.)
                let code = run_pipeline(evaluator, &pipe_segments);
                last_exit = code;
                last_failed = code != 0;
                evaluator.exit_code = code;
            } else {
                // Pure shell pipeline — native pipe chain (fork/exec/dup2)
                let result = process::run_native(segment);
                last_exit = result.exit_code;
                last_failed = last_exit != 0;
                evaluator.exit_code = last_exit;
            }
            continue;
        }

        // Step 4: Triage — Rush syntax or shell command?
        if triage::is_rush_syntax(segment) {
            match parser::parse(segment) {
                Ok(nodes) => {
                    match evaluator.exec_toplevel(&nodes) {
                        Ok(_) => {
                            last_exit = evaluator.exit_code;
                            last_failed = last_exit != 0;
                        }
                        Err(e) => {
                            eprintln!("rush: {e}");
                            last_exit = 1;
                            last_failed = true;
                            evaluator.exit_code = 1;
                        }
                    }
                }
                Err(_) => {
                    // Parse failed — run natively (fork/exec, TTY preserved)
                    let result = process::run_native(segment);
                    last_exit = result.exit_code;
                    last_failed = last_exit != 0;
                    evaluator.exit_code = last_exit;
                    if !result.stderr.is_empty() {
                        eprintln!("{}", result.stderr);
                    }
                }
            }
        } else {
            // External command — fork/exec with inherited TTY
            let result = process::run_native(segment);
            last_exit = result.exit_code;
            last_failed = last_exit != 0;
            evaluator.exit_code = last_exit;
            if !result.stderr.is_empty() {
                eprintln!("{}", result.stderr);
            }
        }

        // Apply ! negation
        if negate {
            last_exit = if last_exit == 0 { 1 } else { 0 };
            last_failed = last_exit != 0;
            evaluator.exit_code = last_exit;
        }

        // Restore inline env vars
        for (key, prev) in saved_vars {
            match prev {
                Some(val) => unsafe { std::env::set_var(&key, &val) },
                None => unsafe { std::env::remove_var(&key) },
            }
        }
    }

    DispatchResult {
        exit_code: last_exit,
        should_exit,
    }
}

/// Dispatch for use inside the evaluator (function bodies, etc.)
/// No builtin handler — just triage + eval + shell fallback.
pub fn dispatch_simple(line: &str, evaluator: &mut Evaluator) -> i32 {
    let result = dispatch(line, evaluator, None);
    result.exit_code
}

// ── Inline Env Vars ─────────────────────────────────────────────────

/// Extract leading VAR=val assignments from a command.
/// `LANG=C sort file` → vars=[("LANG","C")], remaining="sort file"
fn extract_inline_env_vars(segment: &str) -> (Vec<(String, String)>, String) {
    let mut vars = Vec::new();
    let words: Vec<&str> = segment.split_whitespace().collect();
    let mut cmd_start = 0;

    for word in &words {
        // Check if this word is VAR=VALUE (not ==, not a flag, identifier on left)
        if let Some(eq_pos) = word.find('=') {
            if eq_pos > 0 && !word.starts_with('-') && !word[..eq_pos].contains('(') {
                let left = &word[..eq_pos];
                // Left side must be a valid identifier
                if left.chars().all(|c| c.is_ascii_alphanumeric() || c == '_')
                    && left.chars().next().map_or(false, |c| c.is_ascii_alphabetic() || c == '_')
                {
                    let val = &word[eq_pos + 1..];
                    let val = val.trim_matches('"').trim_matches('\'');
                    vars.push((left.to_string(), val.to_string()));
                    cmd_start += 1;
                    continue;
                }
            }
        }
        break; // First non-assignment word = start of command
    }

    if cmd_start == 0 || cmd_start >= words.len() {
        // No inline vars, or all words are assignments (no command)
        return (vars, segment.to_string());
    }

    let remaining = words[cmd_start..].join(" ");
    (vars, remaining)
}

// ── Chain Splitting ─────────────────────────────────────────────────

#[derive(Debug, PartialEq)]
enum ChainOp {
    None,  // first segment or after ;
    And,   // after &&
    Or,    // after ||
    Semi,  // after ;
}

struct ChainSegment {
    command: String,
    operator: ChainOp,
}

/// Split a command line on &&, ||, ; respecting quotes.
fn split_chains(input: &str) -> Vec<ChainSegment> {
    let mut segments = Vec::new();
    let mut current = String::new();
    let mut current_op = ChainOp::None;
    let mut in_single = false;
    let mut in_double = false;
    let chars: Vec<char> = input.chars().collect();
    let mut i = 0;

    while i < chars.len() {
        let ch = chars[i];

        if in_single {
            if ch == '\'' { in_single = false; }
            current.push(ch);
            i += 1;
            continue;
        }
        if in_double {
            if ch == '\\' && i + 1 < chars.len() {
                current.push(ch);
                current.push(chars[i + 1]);
                i += 2;
                continue;
            }
            if ch == '"' { in_double = false; }
            current.push(ch);
            i += 1;
            continue;
        }

        if ch == '\'' { in_single = true; current.push(ch); i += 1; continue; }
        if ch == '"' { in_double = true; current.push(ch); i += 1; continue; }

        // Check for && and ||
        if ch == '&' && i + 1 < chars.len() && chars[i + 1] == '&' {
            segments.push(ChainSegment { command: std::mem::take(&mut current), operator: current_op });
            current_op = ChainOp::And;
            i += 2;
            continue;
        }
        if ch == '|' && i + 1 < chars.len() && chars[i + 1] == '|' {
            segments.push(ChainSegment { command: std::mem::take(&mut current), operator: current_op });
            current_op = ChainOp::Or;
            i += 2;
            continue;
        }
        if ch == ';' {
            segments.push(ChainSegment { command: std::mem::take(&mut current), operator: current_op });
            current_op = ChainOp::Semi;
            i += 1;
            continue;
        }

        current.push(ch);
        i += 1;
    }

    if !current.trim().is_empty() {
        segments.push(ChainSegment { command: current, operator: current_op });
    }

    segments
}

// ── Pipeline Execution ──────────────────────────────────────────────

fn has_rush_pipe_ops(segments: &[String]) -> bool {
    segments.iter().skip(1).any(|seg| {
        let first_word = seg.split_whitespace().next().unwrap_or("");
        pipeline::is_pipe_op(first_word)
    })
}

fn run_pipeline(
    evaluator: &mut Evaluator,
    segments: &[String],
) -> i32 {
    if segments.is_empty() { return 0; }

    let first = &segments[0];
    let auto_obj = pipeline::should_auto_objectify(first);

    // Execute first segment
    let mut value = if !triage::is_rush_syntax(first) {
        let result = process::run_native_capture(first);
        if !result.stderr.is_empty() {
            eprintln!("{}", result.stderr);
        }
        let text_val = pipeline::text_to_array(&result.stdout);
        if auto_obj {
            pipeline::apply_pipe_op(text_val, &pipeline::parse_pipe_op("objectify"))
        } else {
            text_val
        }
    } else {
        match parser::parse(first) {
            Ok(nodes) => evaluator.exec_toplevel(&nodes).unwrap_or(Value::Nil),
            Err(_) => {
                let result = process::run_native_capture(first);
                let text_val = pipeline::text_to_array(&result.stdout);
                if auto_obj {
                    pipeline::apply_pipe_op(text_val, &pipeline::parse_pipe_op("objectify"))
                } else {
                    text_val
                }
            }
        }
    };

    // Apply pipeline operators
    for segment in &segments[1..] {
        let first_word = segment.split_whitespace().next().unwrap_or("");
        if pipeline::is_pipe_op(first_word) {
            let op = pipeline::parse_pipe_op(segment);
            value = pipeline::apply_pipe_op(value, &op);
        } else {
            // Shell segment in Rush pipeline — pipe Rush value into command's stdin
            let input_text = value.to_rush_string();
            let parts = process::parse_command_line(segment);
            if !parts.is_empty() {
                let args: Vec<&str> = parts[1..].iter().map(|s| s.as_str()).collect();
                match std::process::Command::new(&parts[0])
                    .args(&args)
                    .stdin(std::process::Stdio::piped())
                    .stdout(std::process::Stdio::piped())
                    .stderr(std::process::Stdio::inherit())
                    .spawn()
                {
                    Ok(mut child) => {
                        use std::io::Write;
                        if let Some(mut stdin) = child.stdin.take() {
                            stdin.write_all(input_text.as_bytes()).ok();
                        }
                        match child.wait_with_output() {
                            Ok(output) => {
                                value = Value::String(
                                    String::from_utf8_lossy(&output.stdout).trim_end().to_string()
                                );
                            }
                            Err(_) => { value = Value::String(String::new()); }
                        }
                    }
                    Err(e) => {
                        eprintln!("rush: {}: {e}", parts[0]);
                        value = Value::String(String::new());
                    }
                }
            }
        }
    }

    // Print result
    let output = value.to_rush_string();
    if !output.is_empty() {
        println!("{output}");
    }

    evaluator.exit_code
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::eval::{Evaluator, Output};

    struct TestOutput { lines: Vec<String> }
    impl TestOutput { fn new() -> Self { Self { lines: Vec::new() } } }
    impl Output for TestOutput {
        fn puts(&mut self, s: &str) { self.lines.push(s.to_string()); }
        fn print(&mut self, s: &str) { self.lines.push(s.to_string()); }
        fn warn(&mut self, s: &str) { self.lines.push(format!("WARN: {s}")); }
    }

    fn run(input: &str) -> (i32, Vec<String>) {
        let mut output = TestOutput::new();
        let result = {
            let mut eval = Evaluator::new(&mut output);
            dispatch(input, &mut eval, None)
        };
        (result.exit_code, output.lines)
    }

    // ── Chain operators ─────────────────────────────────────────────

    #[test]
    fn chain_and_success() {
        let (_, lines) = run("puts \"a\"; puts \"b\"");
        assert_eq!(lines, vec!["a", "b"]);
    }

    #[test]
    fn chain_semicolon() {
        let (_, lines) = run("x = 1; y = 2; puts x + y");
        assert_eq!(lines, vec!["3"]);
    }

    #[test]
    fn chain_and_with_shell_false() {
        // /usr/bin/false returns exit code 1
        let (code, lines) = run("/usr/bin/false && puts \"no\"");
        assert_ne!(code, 0);
        assert!(lines.is_empty()); // puts should not run
    }

    #[test]
    fn chain_or_with_shell_false() {
        let (_, lines) = run("/usr/bin/false || puts \"yes\"");
        assert_eq!(lines, vec!["yes"]);
    }

    // ── Triage ──────────────────────────────────────────────────────

    #[test]
    fn rush_syntax_dispatched() {
        let (_, lines) = run("puts 1 + 2");
        assert_eq!(lines, vec!["3"]);
    }

    #[test]
    fn shell_command_dispatched() {
        let (code, _) = run("echo hello");
        assert_eq!(code, 0);
    }

    #[test]
    fn assignment_is_rush() {
        let (_, lines) = run("x = 42; puts x");
        assert_eq!(lines, vec!["42"]);
    }

    // ── Split chains ────────────────────────────────────────────────

    #[test]
    fn split_chains_basic() {
        let chains = split_chains("a && b || c ; d");
        assert_eq!(chains.len(), 4);
        assert_eq!(chains[0].command.trim(), "a");
        assert_eq!(chains[1].operator, ChainOp::And);
        assert_eq!(chains[2].operator, ChainOp::Or);
        assert_eq!(chains[3].operator, ChainOp::Semi);
    }

    #[test]
    fn split_chains_quoted() {
        let chains = split_chains("echo \"a && b\" ; echo c");
        assert_eq!(chains.len(), 2);
        assert!(chains[0].command.contains("a && b"));
    }

    // ── Exit ────────────────────────────────────────────────────────

    #[test]
    fn exit_sets_flag() {
        let mut output = TestOutput::new();
        let mut eval = Evaluator::new(&mut output);
        let result = dispatch("exit", &mut eval, None);
        assert!(result.should_exit);
    }

    // ── Inline env vars ─────────────────────────────────────────────

    #[test]
    fn extract_inline_vars() {
        let (vars, cmd) = extract_inline_env_vars("LANG=C sort file.txt");
        assert_eq!(vars, vec![("LANG".to_string(), "C".to_string())]);
        assert_eq!(cmd, "sort file.txt");
    }

    #[test]
    fn extract_multiple_inline_vars() {
        let (vars, cmd) = extract_inline_env_vars("FOO=1 BAR=2 cmd");
        assert_eq!(vars.len(), 2);
        assert_eq!(cmd, "cmd");
    }

    #[test]
    fn extract_no_inline_vars() {
        let (vars, cmd) = extract_inline_env_vars("ls -la");
        assert!(vars.is_empty());
        assert_eq!(cmd, "ls -la");
    }

    #[test]
    fn extract_assignment_not_inline() {
        // "x=5" alone is an assignment, not an inline var
        let (vars, _cmd) = extract_inline_env_vars("x=5");
        assert_eq!(vars.len(), 1); // It's extracted but no command follows
    }
}
