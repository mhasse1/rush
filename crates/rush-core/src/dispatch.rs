//! Unified command dispatch — the Rust equivalent of C# ProcessCommand().
//!
//! Every command goes through this path: REPL, scripts, function bodies,
//! init.rush, -c mode. This ensures consistent behavior everywhere.

use crate::ai;
use crate::eval::Evaluator;
use crate::flags;
use crate::jobs::JobTable;
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
    dispatch_with_jobs(line, evaluator, None)
}

/// Dispatch with optional job table for background job support.
pub fn dispatch_with_jobs(
    line: &str,
    evaluator: &mut Evaluator,
    mut job_table: Option<&mut JobTable>,
) -> DispatchResult {
    let trimmed = line.trim();
    if trimmed.is_empty() {
        return DispatchResult { exit_code: 0, should_exit: false };
    }

    // Step 0: Handle heredocs — extract <<DELIM content from multi-line input
    let trimmed = expand_heredocs(trimmed);
    let trimmed = trimmed.as_str();

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

        // Step 2b: Check for background &
        let (is_background, segment) = if segment.ends_with(" &") || segment.ends_with("\t&") {
            (true, segment[..segment.len() - 1].trim())
        } else if segment == "&" {
            continue; // bare & is meaningless
        } else {
            (false, segment)
        };

        // Background job — spawn and continue
        if is_background {
            if let Some(jt) = job_table.as_deref_mut() {
                match process::spawn_background(segment) {
                    Ok((pid, pgid)) => {
                        jt.add(pid, pgid, segment);
                        unsafe { std::env::set_var("RUSH_LAST_BG_PID", pid.to_string()) };
                        last_exit = 0;
                        last_failed = false;
                    }
                    Err(e) => {
                        eprintln!("{e}");
                        last_exit = 127;
                        last_failed = true;
                    }
                }
            } else {
                // No job table — just spawn and forget
                match process::spawn_background(segment) {
                    Ok((pid, _)) => {
                        eprintln!("[bg] {pid}");
                        last_exit = 0;
                    }
                    Err(e) => {
                        eprintln!("{e}");
                        last_exit = 127;
                        last_failed = true;
                    }
                }
            }
            evaluator.exit_code = last_exit;
            // Restore inline env vars before continuing
            for (key, prev) in saved_vars {
                match prev {
                    Some(val) => unsafe { std::env::set_var(&key, &val) },
                    None => unsafe { std::env::remove_var(&key) },
                }
            }
            continue;
        }

        // Step 2c: Check for ! negation
        let (negate, segment) = if segment.starts_with("! ") {
            (true, &segment[2..])
        } else {
            (false, segment)
        };

        // set -x: print command before execution
        if flags::xtrace() {
            eprintln!("+ {segment}");
        }

        let first_word = segment.split_whitespace().next().unwrap_or("");

        // Core builtins handled directly in dispatch (work in chains)
        if first_word == "set" {
            let args = segment[first_word.len()..].trim();
            if flags::handle_set_flag(args) {
                last_exit = 0;
                last_failed = false;
                evaluator.exit_code = 0;
                // Restore inline env vars
                for (key, prev) in saved_vars {
                    match prev {
                        Some(val) => unsafe { std::env::set_var(&key, &val) },
                        None => unsafe { std::env::remove_var(&key) },
                    }
                }
                continue;
            }
        }

        // Subshell: ( list ) — execute in child process
        // Uses Rush's own binary so subshell gets Rush syntax
        if segment.starts_with('(') && segment.ends_with(')') {
            let inner = &segment[1..segment.len() - 1];
            // Try to find our own binary
            let rush_bin = std::env::current_exe()
                .map(|p| p.to_string_lossy().to_string())
                .unwrap_or_else(|_| "rush".to_string());
            let result = process::run_native_capture(&format!("{rush_bin} -c '{}'", inner.replace('\'', "'\\''")));
            if !result.stdout.is_empty() {
                print!("{}", result.stdout);
            }
            last_exit = result.exit_code;
            last_failed = last_exit != 0;
            evaluator.exit_code = last_exit;
            // Restore inline env vars
            for (key, prev) in saved_vars {
                match prev {
                    Some(val) => unsafe { std::env::set_var(&key, &val) },
                    None => unsafe { std::env::remove_var(&key) },
                }
            }
            continue;
        }

        // Brace group: { list; } — execute in current shell
        if segment.starts_with("{ ") && segment.ends_with(" }") {
            let inner = &segment[2..segment.len() - 2];
            // Recursively dispatch the inner commands
            let inner_result = dispatch_with_jobs(inner, evaluator, job_table.as_deref_mut());
            last_exit = inner_result.exit_code;
            last_failed = last_exit != 0;
            if inner_result.should_exit { should_exit = true; break; }
            // Restore inline env vars
            for (key, prev) in saved_vars {
                match prev {
                    Some(val) => unsafe { std::env::set_var(&key, &val) },
                    None => unsafe { std::env::remove_var(&key) },
                }
            }
            continue;
        }

        // Core builtins handled directly in dispatch (work in chains)
        if first_word == ":" || first_word == "true" {
            last_exit = 0;
            last_failed = false;
            evaluator.exit_code = 0;
            for (key, prev) in saved_vars { match prev { Some(val) => unsafe { std::env::set_var(&key, &val) }, None => unsafe { std::env::remove_var(&key) } } }
            continue;
        }
        if first_word == "false" {
            last_exit = 1;
            last_failed = true;
            evaluator.exit_code = 1;
            for (key, prev) in saved_vars { match prev { Some(val) => unsafe { std::env::set_var(&key, &val) }, None => unsafe { std::env::remove_var(&key) } } }
            continue;
        }

        // export — must work in chains: export FOO=bar; echo $FOO
        if first_word == "export" {
            let args = segment[first_word.len()..].trim();
            if let Some((key, value)) = args.split_once('=') {
                let key = key.trim();
                let value = value.trim().trim_matches('"').trim_matches('\'');
                unsafe { std::env::set_var(key, value) };
            }
            last_exit = 0;
            last_failed = false;
            evaluator.exit_code = 0;
            for (key, prev) in saved_vars { match prev { Some(val) => unsafe { std::env::set_var(&key, &val) }, None => unsafe { std::env::remove_var(&key) } } }
            continue;
        }

        // unset — must work in chains
        if first_word == "unset" {
            let args = segment[first_word.len()..].trim();
            for name in args.split_whitespace() {
                unsafe { std::env::remove_var(name) };
            }
            last_exit = 0;
            last_failed = false;
            evaluator.exit_code = 0;
            for (key, prev) in saved_vars { match prev { Some(val) => unsafe { std::env::set_var(&key, &val) }, None => unsafe { std::env::remove_var(&key) } } }
            continue;
        }

        // cd — must work in chains: cd /tmp && pwd
        if first_word == "cd" {
            let target = segment[first_word.len()..].trim();
            let path = if target.is_empty() || target == "~" {
                std::env::var("HOME").unwrap_or_else(|_| ".".into())
            } else if let Some(rest) = target.strip_prefix("~/") {
                format!("{}/{rest}", std::env::var("HOME").unwrap_or_default())
            } else {
                target.to_string()
            };
            match std::env::set_current_dir(&path) {
                Ok(()) => { last_exit = 0; last_failed = false; }
                Err(e) => { eprintln!("cd: {path}: {e}"); last_exit = 1; last_failed = true; }
            }
            evaluator.exit_code = last_exit;
            for (key, prev) in saved_vars { match prev { Some(val) => unsafe { std::env::set_var(&key, &val) }, None => unsafe { std::env::remove_var(&key) } } }
            continue;
        }

        // exit/quit
        if first_word == "exit" || first_word == "quit" {
            should_exit = true;
            break;
        }

        // Step 3: Check for pipes
        let pipe_segments = pipeline::split_pipeline(segment);

        // Special case: `... | ai [args]` — capture upstream output
        // and pass it to the ai builtin via piped_input. Without this,
        // the native pipeline path tries to execve `ai` (a rush-cli
        // builtin, not on PATH) and fails with 127, blaming the first
        // word in the line rather than the real missing command.
        if pipe_segments.len() > 1 {
            if let Some(last) = pipe_segments.last() {
                let last_first = last.split_whitespace().next().unwrap_or("");
                if last_first == "ai" {
                    let upstream_cmd = pipe_segments[..pipe_segments.len() - 1].join(" | ");
                    let upstream = process::run_native_capture(&upstream_cmd);
                    if !upstream.stderr.is_empty() {
                        eprintln!("{}", upstream.stderr);
                    }
                    let ai_args = last.trim_start().strip_prefix("ai").unwrap_or("").trim();
                    let (prompt, provider, model) = ai::parse_ai_args(ai_args);
                    let exit = if prompt.is_empty() {
                        eprintln!("ai: missing prompt — usage: ... | ai \"question\"");
                        2
                    } else {
                        match ai::execute(
                            provider.as_deref(),
                            model.as_deref(),
                            &prompt,
                            Some(&upstream.stdout),
                        ) {
                            Ok(_) => 0,
                            Err(e) => {
                                eprintln!("ai: {e}");
                                1
                            }
                        }
                    };
                    last_exit = exit;
                    last_failed = exit != 0;
                    evaluator.exit_code = exit;
                    for (key, prev) in saved_vars {
                        match prev {
                            Some(val) => unsafe { std::env::set_var(&key, &val) },
                            None => unsafe { std::env::remove_var(&key) },
                        }
                    }
                    continue;
                }
            }
        }

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

        // Update $? for special parameter expansion
        unsafe { std::env::set_var("RUSH_LAST_EXIT", last_exit.to_string()) };

        // set -e: exit on failure (with POSIX exceptions)
        // Exceptions: condition of if/while/until, left side of && or ||
        if flags::errexit() && last_failed {
            // Check if this segment was in an exception context
            let in_exception = matches!(chain.operator, ChainOp::And | ChainOp::Or);
            if !in_exception {
                should_exit = true;
                break;
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

// ── Heredoc Expansion ───────────────────────────────────────────────

/// Process heredocs and backslash-newline continuations in multi-line input.
/// Converts `cmd <<EOF\nbody\nEOF` into `cmd` with heredoc content piped as stdin.
/// Also joins `line \\\nline2` into `line line2`.
fn expand_heredocs(input: &str) -> String {
    let mut result = String::new();
    let lines: Vec<&str> = input.split('\n').collect();
    let mut i = 0;

    while i < lines.len() {
        let mut line = lines[i].to_string();

        // Backslash-newline continuation
        while line.ends_with('\\') && i + 1 < lines.len() {
            line.pop(); // remove trailing backslash
            i += 1;
            line.push_str(lines[i]);
        }

        // Check for heredoc: cmd <<DELIM or cmd <<-DELIM
        if let Some(heredoc_pos) = find_heredoc(&line) {
            let (cmd_part, rest) = line.split_at(heredoc_pos);
            let rest = &rest[2..]; // skip <<
            let strip_tabs = rest.starts_with('-');
            let delim_str = if strip_tabs { &rest[1..] } else { rest };
            let delim = delim_str.trim().trim_matches('\'').trim_matches('"');

            if delim.is_empty() {
                result.push_str(&line);
                result.push('\n');
                i += 1;
                continue;
            }

            // Collect heredoc body
            let mut body = String::new();
            i += 1;
            while i < lines.len() {
                let hline = if strip_tabs {
                    lines[i].trim_start_matches('\t')
                } else {
                    lines[i]
                };
                if hline.trim() == delim {
                    i += 1;
                    break;
                }
                body.push_str(hline);
                body.push('\n');
                i += 1;
            }

            // Write heredoc body to a temp file and redirect stdin
            let tmp = std::env::temp_dir().join(format!("rush_heredoc_{}", std::process::id()));
            if std::fs::write(&tmp, &body).is_ok() {
                let tmp_path = tmp.to_string_lossy();
                result.push_str(cmd_part.trim());
                result.push_str(&format!(" < {tmp_path}"));
                result.push('\n');
            }
            continue;
        }

        result.push_str(&line);
        result.push('\n');
        i += 1;
    }

    // Remove trailing newline
    if result.ends_with('\n') {
        result.pop();
    }
    result
}

/// Find the position of << in a line (not inside quotes).
fn find_heredoc(line: &str) -> Option<usize> {
    let chars: Vec<char> = line.chars().collect();
    let mut in_single = false;
    let mut in_double = false;

    for i in 0..chars.len().saturating_sub(1) {
        if chars[i] == '\'' && !in_double { in_single = !in_single; }
        if chars[i] == '"' && !in_single { in_double = !in_double; }
        if !in_single && !in_double && chars[i] == '<' && chars[i + 1] == '<' {
            // Make sure it's not <<< (herestring)
            if i + 2 < chars.len() && chars[i + 2] == '<' {
                continue;
            }
            return Some(i);
        }
    }
    None
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
    // Rush block openers that are closed by `end`. Needed so that
    // `def foo; body; end` is treated as one chunk, not three.
    const BLOCK_OPENERS: &[&str] = &[
        "def", "class", "enum", "if", "unless", "while", "until",
        "loop", "for", "case", "match", "try", "begin",
        "orchestrate", "parallel", "do",
    ];

    let mut segments = Vec::new();
    let mut current = String::new();
    let mut current_op = ChainOp::None;
    let mut in_single = false;
    let mut in_double = false;
    let mut brace_depth: i32 = 0; // track { } for brace groups
    let mut paren_depth: i32 = 0; // track ( ) for subshells
    let mut block_depth: i32 = 0; // track def/class/...end
    let chars: Vec<char> = input.chars().collect();
    let mut i = 0;

    // Helper: extract the identifier starting at position `start` (if any).
    fn word_at(chars: &[char], start: usize) -> Option<&[char]> {
        if start >= chars.len() { return None; }
        let c = chars[start];
        if !(c.is_ascii_alphabetic() || c == '_') { return None; }
        let mut end = start;
        while end < chars.len() && (chars[end].is_ascii_alphanumeric() || chars[end] == '_') {
            end += 1;
        }
        Some(&chars[start..end])
    }

    // True if position `start` is at a word boundary (start of input
    // or preceded by a non-identifier char).
    fn at_word_boundary(chars: &[char], start: usize) -> bool {
        if start == 0 { return true; }
        let prev = chars[start - 1];
        !(prev.is_ascii_alphanumeric() || prev == '_')
    }

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

        // Track brace/paren depth for { } and ( )
        if ch == '{' { brace_depth += 1; }
        if ch == '}' { brace_depth -= 1; }
        if ch == '(' { paren_depth += 1; }
        if ch == ')' { paren_depth -= 1; }

        // Track block-keyword depth (def/class/...end) at word boundaries.
        if at_word_boundary(&chars, i) {
            if let Some(w) = word_at(&chars, i) {
                let word: String = w.iter().collect();
                if word == "end" { block_depth -= 1; }
                else if BLOCK_OPENERS.iter().any(|&op| op == word) { block_depth += 1; }
                // Consume the whole word so the first char isn't re-processed.
                for c in w { current.push(*c); }
                i += w.len();
                continue;
            }
        }

        // Don't split on operators inside braces, parens, or blocks.
        if brace_depth > 0 || paren_depth > 0 || block_depth > 0 {
            current.push(ch);
            i += 1;
            continue;
        }

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

    // ── Brace group ─────────────────────────────────────────────────

    #[test]
    fn brace_group_single() {
        let (_, lines) = run("{ puts \"hello\" }");
        assert_eq!(lines, vec!["hello"]);
    }

    // ── set -x ──────────────────────────────────────────────────────

    #[test]
    fn set_xtrace() {
        use crate::flags;
        flags::set_xtrace(false); // ensure clean state
        let (_, _) = run("puts \"hello\"");
        // No xtrace output expected
        flags::set_xtrace(false);
    }
}
