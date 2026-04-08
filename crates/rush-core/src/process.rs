use std::process::{Command, Stdio};

use crate::platform;

/// Reclaim terminal control after a foreground job completes.
pub fn reclaim_terminal() {
    let p = platform::current();
    p.reclaim_terminal();
}

/// Result of running an external command.
#[derive(Debug, Clone)]
pub struct CommandResult {
    pub stdout: String,
    pub stderr: String,
    pub exit_code: i32,
}

// ── Single Command ──────────────────────────────────────────────────

/// Run a single command with inherited stdio (TTY preserved).
/// Sets up proper process group for job control.
pub fn run_command(program: &str, args: &[&str]) -> CommandResult {
    let mut cmd = Command::new(program);
    cmd.args(args)
        .stdin(Stdio::inherit())
        .stdout(Stdio::inherit())
        .stderr(Stdio::inherit());

    // Set up child process group and signal dispositions via platform
    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt;
        unsafe {
            cmd.pre_exec(|| {
                let p = platform::current();
                p.setup_foreground_child();
                Ok(())
            });
        }
    }

    match cmd.status() {
        Ok(status) => {
            #[cfg(unix)]
            reclaim_terminal();

            // Map signal death to 128+N
            let exit_code = status.code().unwrap_or_else(|| {
                #[cfg(unix)]
                {
                    use std::os::unix::process::ExitStatusExt;
                    status.signal().map(|s| 128 + s).unwrap_or(-1)
                }
                #[cfg(not(unix))]
                { -1 }
            });

            CommandResult {
                stdout: String::new(),
                stderr: String::new(),
                exit_code,
            }
        }
        Err(e) => {
            #[cfg(unix)]
            reclaim_terminal();

            // Distinguish 126 (not executable) from 127 (not found)
            let exit_code = if e.kind() == std::io::ErrorKind::NotFound {
                127
            } else if e.kind() == std::io::ErrorKind::PermissionDenied {
                126
            } else {
                127
            };

            CommandResult {
                stdout: String::new(),
                stderr: format!("rush: {program}: {e}"),
                exit_code,
            }
        }
    }
}

/// Run a single command and capture stdout+stderr.
pub fn run_command_capture(program: &str, args: &[&str]) -> CommandResult {
    match Command::new(program)
        .args(args)
        .stdin(Stdio::inherit())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()
    {
        Ok(output) => CommandResult {
            stdout: String::from_utf8_lossy(&output.stdout).into_owned(),
            stderr: String::from_utf8_lossy(&output.stderr).into_owned(),
            exit_code: output.status.code().unwrap_or(-1),
        },
        Err(e) => CommandResult {
            stdout: String::new(),
            stderr: format!("rush: {program}: {e}"),
            exit_code: 127,
        },
    }
}

// ── Command Line Execution ──────────────────────────────────────────

/// Spawn a command in the background. Returns (pid, pgid) on success.
pub fn spawn_background(line: &str) -> Result<(u32, u32), String> {
    let parts = parse_and_expand(line);
    if parts.is_empty() {
        return Err("empty command".to_string());
    }

    let program = &parts[0];
    let args: Vec<&str> = parts[1..].iter().map(|s| s.as_str()).collect();

    let mut cmd = Command::new(program);
    cmd.args(&args)
        .stdin(Stdio::null())  // Background jobs don't read terminal
        .stdout(Stdio::inherit())
        .stderr(Stdio::inherit());

    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt;
        unsafe {
            cmd.pre_exec(|| {
                let p = platform::current();
                p.setup_background_child();
                Ok(())
            });
        }
    }

    match cmd.spawn() {
        Ok(child) => {
            let pid = child.id();
            // Process group ID = pid (since setpgid(0,0) makes pid=pgid)
            Ok((pid, pid))
        }
        Err(e) => Err(format!("rush: {program}: {e}")),
    }
}

/// Run a command line natively: parse → expand → fork/exec.
/// Handles pipes natively (pipe/fork/dup2/exec for each segment).
pub fn run_native(line: &str) -> CommandResult {
    let line = line.trim();
    if line.is_empty() {
        return CommandResult { stdout: String::new(), stderr: String::new(), exit_code: 0 };
    }

    // Split on pipes (respecting quotes)
    let segments = split_on_pipe(line);
    if segments.len() > 1 {
        return run_pipe_chain(&segments, false);
    }

    // Single command
    let parts = parse_and_expand(line);
    if parts.is_empty() {
        return CommandResult { stdout: String::new(), stderr: String::new(), exit_code: 0 };
    }

    // Handle I/O redirections
    let (parts, redirects) = extract_redirections(parts);
    if parts.is_empty() {
        return CommandResult { stdout: String::new(), stderr: String::new(), exit_code: 0 };
    }

    let program = &parts[0];
    let args: Vec<&str> = parts[1..].iter().map(|s| s.as_str()).collect();

    if redirects.is_empty() {
        run_command(program, &args)
    } else {
        run_with_redirects(program, &args, &redirects)
    }
}

/// Run a command line natively and capture stdout.
pub fn run_native_capture(line: &str) -> CommandResult {
    let line = line.trim();
    if line.is_empty() {
        return CommandResult { stdout: String::new(), stderr: String::new(), exit_code: 0 };
    }

    let segments = split_on_pipe(line);
    if segments.len() > 1 {
        return run_pipe_chain(&segments, true);
    }

    let parts = parse_and_expand(line);
    if parts.is_empty() {
        return CommandResult { stdout: String::new(), stderr: String::new(), exit_code: 0 };
    }

    let program = &parts[0];
    let args: Vec<&str> = parts[1..].iter().map(|s| s.as_str()).collect();
    run_command_capture(program, &args)
}

// ── Pipe Chains ─────────────────────────────────────────────────────

/// Run a pipeline: cmd1 | cmd2 | cmd3
/// Each segment is fork/exec'd with pipe connecting stdout→stdin.
/// Last segment inherits TTY stdout (unless capture=true).
fn run_pipe_chain(segments: &[String], capture_last: bool) -> CommandResult {
    if segments.is_empty() {
        return CommandResult { stdout: String::new(), stderr: String::new(), exit_code: 0 };
    }

    // Parse each segment
    let commands: Vec<Vec<String>> = segments.iter()
        .map(|s| parse_and_expand(s.trim()))
        .collect();

    if commands.len() == 1 {
        let parts = &commands[0];
        if parts.is_empty() {
            return CommandResult { stdout: String::new(), stderr: String::new(), exit_code: 0 };
        }
        let args: Vec<&str> = parts[1..].iter().map(|s| s.as_str()).collect();
        return if capture_last {
            run_command_capture(&parts[0], &args)
        } else {
            run_command(&parts[0], &args)
        };
    }

    // Build pipe chain
    let mut prev_stdout: Option<std::process::ChildStdout> = None;
    let mut children: Vec<std::process::Child> = Vec::new();

    for (i, parts) in commands.iter().enumerate() {
        if parts.is_empty() { continue; }

        let stdin = if let Some(prev) = prev_stdout.take() {
            Stdio::from(prev)
        } else {
            Stdio::inherit()
        };

        let is_last = i == commands.len() - 1;
        let stdout = if is_last && !capture_last {
            Stdio::inherit()
        } else {
            Stdio::piped()
        };

        let program = &parts[0];
        let args: Vec<&str> = parts[1..].iter().map(|s| s.as_str()).collect();

        match Command::new(program)
            .args(&args)
            .stdin(stdin)
            .stdout(stdout)
            .stderr(Stdio::inherit())
            .spawn()
        {
            Ok(mut child) => {
                // For the last child in capture mode, leave stdout for wait_with_output
                if !(is_last && capture_last) {
                    prev_stdout = child.stdout.take();
                }
                children.push(child);
            }
            Err(e) => {
                for mut c in children {
                    let _ = c.kill();
                    let _ = c.wait();
                }
                return CommandResult {
                    stdout: String::new(),
                    stderr: format!("rush: {program}: {e}"),
                    exit_code: 127,
                };
            }
        }
    }

    // Wait for all children. If capturing, use wait_with_output for last.
    let mut last_code = 0;
    let mut captured = String::new();
    let child_count = children.len();

    for (i, mut child) in children.into_iter().enumerate() {
        let is_last = i == child_count - 1;
        if is_last && capture_last {
            match child.wait_with_output() {
                Ok(output) => {
                    captured = String::from_utf8_lossy(&output.stdout).into_owned();
                    last_code = output.status.code().unwrap_or(-1);
                }
                Err(_) => last_code = -1,
            }
        } else {
            match child.wait() {
                Ok(status) => last_code = status.code().unwrap_or(-1),
                Err(_) => last_code = -1,
            }
        }
    }

    CommandResult {
        stdout: captured,
        stderr: String::new(),
        exit_code: last_code,
    }
}

// ── Redirections ────────────────────────────────────────────────────

#[derive(Debug)]
enum Redirect {
    StdoutWrite(String),     // > file
    StdoutAppend(String),    // >> file
    StdinRead(String),       // < file
    StderrWrite(String),     // 2> file
    StderrAppend(String),    // 2>> file
    StderrToStdout,          // 2>&1
}

/// Extract redirections from argument list. Supports multiple redirections.
fn extract_redirections(parts: Vec<String>) -> (Vec<String>, Vec<Redirect>) {
    let mut redirects = Vec::new();
    let mut clean = Vec::new();
    let mut i = 0;

    while i < parts.len() {
        // 2>&1
        if parts[i] == "2>&1" {
            redirects.push(Redirect::StderrToStdout);
            i += 1;
        }
        // 2>> file
        else if parts[i] == "2>>" && i + 1 < parts.len() {
            redirects.push(Redirect::StderrAppend(parts[i + 1].clone()));
            i += 2;
        }
        // 2> file
        else if parts[i] == "2>" && i + 1 < parts.len() {
            redirects.push(Redirect::StderrWrite(parts[i + 1].clone()));
            i += 2;
        }
        // 2>/dev/null or 2>file (no space)
        else if parts[i].starts_with("2>") && parts[i].len() > 2 && !parts[i].starts_with("2>&") {
            let rest = &parts[i][2..];
            if rest.starts_with('>') {
                // 2>>file
                redirects.push(Redirect::StderrAppend(rest[1..].to_string()));
            } else {
                redirects.push(Redirect::StderrWrite(rest.to_string()));
            }
            i += 1;
        }
        // >> file
        else if parts[i] == ">>" && i + 1 < parts.len() {
            redirects.push(Redirect::StdoutAppend(parts[i + 1].clone()));
            i += 2;
        }
        // > file
        else if parts[i] == ">" && i + 1 < parts.len() {
            redirects.push(Redirect::StdoutWrite(parts[i + 1].clone()));
            i += 2;
        }
        // < file
        else if parts[i] == "<" && i + 1 < parts.len() {
            redirects.push(Redirect::StdinRead(parts[i + 1].clone()));
            i += 2;
        }
        // >file (no space)
        else if parts[i].starts_with(">>") && parts[i].len() > 2 {
            redirects.push(Redirect::StdoutAppend(parts[i][2..].to_string()));
            i += 1;
        }
        else if parts[i].starts_with('>') && parts[i].len() > 1 {
            redirects.push(Redirect::StdoutWrite(parts[i][1..].to_string()));
            i += 1;
        }
        else {
            clean.push(parts[i].clone());
            i += 1;
        }
    }

    (clean, redirects)
}

/// Run a command with I/O redirections (supports multiple).
fn run_with_redirects(program: &str, args: &[&str], redirects: &[Redirect]) -> CommandResult {
    // Build stdio from redirections
    let mut stdin_cfg = Stdio::inherit();
    let mut stdout_cfg = Stdio::inherit();
    let mut stderr_cfg = Stdio::inherit();

    // We need to open files and convert to Stdio
    // Process redirections left to right (POSIX order)
    let mut stdin_file: Option<std::fs::File> = None;
    let mut stdout_file: Option<std::fs::File> = None;
    let mut stderr_file: Option<std::fs::File> = None;

    for redirect in redirects {
        match redirect {
            Redirect::StdoutWrite(path) => {
                match std::fs::File::create(path) {
                    Ok(f) => stdout_file = Some(f),
                    Err(e) => return CommandResult { stdout: String::new(), stderr: format!("rush: {path}: {e}"), exit_code: 1 },
                }
            }
            Redirect::StdoutAppend(path) => {
                match std::fs::OpenOptions::new().create(true).append(true).open(path) {
                    Ok(f) => stdout_file = Some(f),
                    Err(e) => return CommandResult { stdout: String::new(), stderr: format!("rush: {path}: {e}"), exit_code: 1 },
                }
            }
            Redirect::StdinRead(path) => {
                match std::fs::File::open(path) {
                    Ok(f) => stdin_file = Some(f),
                    Err(e) => return CommandResult { stdout: String::new(), stderr: format!("rush: {path}: {e}"), exit_code: 1 },
                }
            }
            Redirect::StderrWrite(path) => {
                match std::fs::File::create(path) {
                    Ok(f) => stderr_file = Some(f),
                    Err(e) => return CommandResult { stdout: String::new(), stderr: format!("rush: {path}: {e}"), exit_code: 1 },
                }
            }
            Redirect::StderrAppend(path) => {
                match std::fs::OpenOptions::new().create(true).append(true).open(path) {
                    Ok(f) => stderr_file = Some(f),
                    Err(e) => return CommandResult { stdout: String::new(), stderr: format!("rush: {path}: {e}"), exit_code: 1 },
                }
            }
            Redirect::StderrToStdout => {
                // 2>&1 — stderr goes wherever stdout goes
                // If stdout is a file, dup it; otherwise both inherit
                stderr_cfg = Stdio::inherit(); // simplified — both to terminal
            }
        }
    }

    if let Some(f) = stdin_file { stdin_cfg = Stdio::from(f); }
    if let Some(f) = stdout_file { stdout_cfg = Stdio::from(f); }
    if let Some(f) = stderr_file { stderr_cfg = Stdio::from(f); }

    run_cmd_with_stdio(program, args, stdin_cfg, stdout_cfg, stderr_cfg)
}

// Keep old single-redirect function for test compatibility
fn run_cmd_with_stdio(program: &str, args: &[&str], stdin: Stdio, stdout: Stdio, stderr: Stdio) -> CommandResult {
    match Command::new(program)
        .args(args)
        .stdin(stdin)
        .stdout(stdout)
        .stderr(stderr)
        .status()
    {
        Ok(status) => CommandResult {
            stdout: String::new(),
            stderr: String::new(),
            exit_code: status.code().unwrap_or(-1),
        },
        Err(e) => CommandResult {
            stdout: String::new(),
            stderr: format!("rush: {program}: {e}"),
            exit_code: 127,
        },
    }
}

// ── Parsing & Expansion ─────────────────────────────────────────────

/// Parse a command line into words, then expand tildes and globs.
/// This is the shell expansion pipeline: parse → tilde → glob.
/// Quoted words are never glob-expanded (matching bash/zsh behavior).
/// Full POSIX expansion pipeline:
/// env_vars (on whole line) → parse → brace → tilde → glob → quote_removal
fn parse_and_expand(line: &str) -> Vec<String> {
    // Expand $VAR, ${VAR:-default}, and $((arithmetic)) on whole line first
    // (before word splitting, so $((2 + 3 * 4)) works as one expression)
    let line = expand_env_vars(line);
    let parts = parse_command_line_with_quote_info(&line);
    let mut result = Vec::new();

    for (word, was_quoted) in parts {
        // 1. Brace expansion (unquoted only): {a,b,c} → a b c
        let brace_expanded = if !was_quoted && word.contains('{') && word.contains('}') {
            expand_braces(&word)
        } else {
            vec![word]
        };

        for w in brace_expanded {
            // 2. Tilde expansion
            // 3. Parameter expansion ($VAR, ${VAR:-default})
            // 4. Arithmetic expansion ($((expr)))
            let expanded = expand_tilde(&w);

            // 5. Pathname/glob expansion (unquoted only)
            if !was_quoted && contains_glob_chars(&expanded) {
                match glob::glob(&expanded) {
                    Ok(paths) => {
                        let mut matches: Vec<String> = paths
                            .filter_map(|p| p.ok())
                            .map(|p| p.to_string_lossy().to_string())
                            .collect();
                        if matches.is_empty() {
                            result.push(expanded);
                        } else {
                            matches.sort();
                            result.extend(matches);
                        }
                    }
                    Err(_) => result.push(expanded),
                }
            } else {
                result.push(expanded);
            }
        }
    }

    result
}

/// Brace expansion: {a,b,c} → a b c, pre{a,b}post → prea preb
fn expand_braces(word: &str) -> Vec<String> {
    // Find the first unquoted { and matching }
    let chars: Vec<char> = word.chars().collect();
    let mut depth = 0;
    let mut brace_start = None;
    let mut brace_end = None;

    for (i, &ch) in chars.iter().enumerate() {
        if ch == '{' && depth == 0 {
            brace_start = Some(i);
            depth = 1;
        } else if ch == '{' {
            depth += 1;
        } else if ch == '}' {
            depth -= 1;
            if depth == 0 {
                brace_end = Some(i);
                break;
            }
        }
    }

    let (start, end) = match (brace_start, brace_end) {
        (Some(s), Some(e)) => (s, e),
        _ => return vec![word.to_string()],
    };

    let prefix: String = chars[..start].iter().collect();
    let suffix: String = chars[end + 1..].iter().collect();
    let inner: String = chars[start + 1..end].iter().collect();

    // Split on commas (respecting nested braces)
    let alternatives = split_brace_alternatives(&inner);
    if alternatives.len() <= 1 {
        // No commas found — check for sequence {1..5}
        if let Some(seq) = expand_brace_sequence(&inner) {
            return seq.into_iter()
                .map(|s| format!("{prefix}{s}{suffix}"))
                .collect();
        }
        return vec![word.to_string()];
    }

    // Recursively expand each alternative (for nested braces)
    let mut result = Vec::new();
    for alt in &alternatives {
        let expanded = format!("{prefix}{alt}{suffix}");
        if expanded.contains('{') && expanded.contains('}') {
            result.extend(expand_braces(&expanded));
        } else {
            result.push(expanded);
        }
    }
    result
}

/// Split brace content on commas, respecting nested braces.
fn split_brace_alternatives(s: &str) -> Vec<String> {
    let mut parts = Vec::new();
    let mut current = String::new();
    let mut depth = 0;

    for ch in s.chars() {
        if ch == '{' {
            depth += 1;
            current.push(ch);
        } else if ch == '}' {
            depth -= 1;
            current.push(ch);
        } else if ch == ',' && depth == 0 {
            parts.push(std::mem::take(&mut current));
        } else {
            current.push(ch);
        }
    }
    parts.push(current);
    parts
}

/// Expand brace sequence: {1..5} → 1 2 3 4 5, {a..e} → a b c d e
fn expand_brace_sequence(inner: &str) -> Option<Vec<String>> {
    let parts: Vec<&str> = inner.splitn(2, "..").collect();
    if parts.len() != 2 { return None; }

    let start = parts[0].trim();
    let end = parts[1].trim();

    // Numeric sequence
    if let (Ok(a), Ok(b)) = (start.parse::<i64>(), end.parse::<i64>()) {
        let range: Vec<String> = if a <= b {
            (a..=b).map(|n| n.to_string()).collect()
        } else {
            (b..=a).rev().map(|n| n.to_string()).collect()
        };
        return Some(range);
    }

    // Character sequence
    if start.len() == 1 && end.len() == 1 {
        let a = start.chars().next()? as u8;
        let b = end.chars().next()? as u8;
        let range: Vec<String> = if a <= b {
            (a..=b).map(|c| (c as char).to_string()).collect()
        } else {
            (b..=a).rev().map(|c| (c as char).to_string()).collect()
        };
        return Some(range);
    }

    None
}

/// Check if a string contains glob metacharacters.
fn contains_glob_chars(s: &str) -> bool {
    s.contains('*') || s.contains('?') || s.contains('[')
}

/// Parse command line returning (word, was_quoted) pairs.
/// Quoted words should not be glob-expanded.
fn parse_command_line_with_quote_info(line: &str) -> Vec<(String, bool)> {
    let mut parts = Vec::new();
    let mut current = String::new();
    let mut was_quoted = false;
    let mut chars = line.chars().peekable();
    let mut in_single = false;
    let mut in_double = false;

    while let Some(c) = chars.next() {
        match c {
            '\'' if !in_double => {
                in_single = !in_single;
                was_quoted = true;
            }
            '"' if !in_single => {
                in_double = !in_double;
                was_quoted = true;
            }
            '\\' if !in_single => {
                if let Some(&next) = chars.peek() {
                    if in_double {
                        match next {
                            '"' | '\\' | '$' | '`' => {
                                current.push(chars.next().unwrap());
                            }
                            _ => current.push('\\'),
                        }
                    } else {
                        current.push(chars.next().unwrap());
                    }
                }
            }
            ' ' | '\t' if !in_single && !in_double => {
                if !current.is_empty() {
                    parts.push((std::mem::take(&mut current), was_quoted));
                    was_quoted = false;
                }
            }
            _ => current.push(c),
        }
    }
    if !current.is_empty() {
        parts.push((current, was_quoted));
    }
    parts
}

/// Expand ~ to $HOME. Env vars are already expanded on the whole line.
fn expand_tilde(arg: &str) -> String {
    expand_tilde_only(arg)
}

/// Expand $VAR, ${VAR}, ${VAR:-default}, ${VAR:=default}, ${#VAR},
/// ${VAR%pattern}, ${VAR#pattern}, and $((arithmetic)) in a string.
fn expand_env_vars(arg: &str) -> String {
    if !arg.contains('$') {
        return arg.to_string();
    }

    let mut result = String::with_capacity(arg.len());
    let chars: Vec<char> = arg.chars().collect();
    let mut i = 0;

    while i < chars.len() {
        if chars[i] == '$' && i + 1 < chars.len() {
            // $((arithmetic))
            if i + 2 < chars.len() && chars[i + 1] == '(' && chars[i + 2] == '(' {
                i += 3;
                let mut expr = String::new();
                // We need to find matching )). Track depth starting at 0
                // since we already consumed the opening ((
                let mut depth: i32 = 0;
                while i < chars.len() {
                    if chars[i] == '(' { depth += 1; }
                    else if chars[i] == ')' {
                        if depth == 0 {
                            // This is the first ) of the closing ))
                            i += 1;
                            // Skip the second )
                            if i < chars.len() && chars[i] == ')' { i += 1; }
                            break;
                        }
                        depth -= 1;
                    }
                    expr.push(chars[i]);
                    i += 1;
                }
                result.push_str(&eval_arithmetic(&expr));
                continue;
            }

            // ${...} form with potential modifiers
            if chars[i + 1] == '{' {
                i += 2;
                let mut content = String::new();
                let mut depth = 1;
                while i < chars.len() && depth > 0 {
                    if chars[i] == '{' { depth += 1; }
                    else if chars[i] == '}' { depth -= 1; }
                    if depth > 0 { content.push(chars[i]); }
                    i += 1;
                }
                result.push_str(&expand_parameter(&content));
                continue;
            }

            // $VAR form
            if chars[i + 1].is_ascii_alphabetic() || chars[i + 1] == '_' {
                i += 1;
                let mut var_name = String::new();
                while i < chars.len() && (chars[i].is_ascii_alphanumeric() || chars[i] == '_') {
                    var_name.push(chars[i]);
                    i += 1;
                }
                result.push_str(&std::env::var(&var_name).unwrap_or_default());
                continue;
            }

            // $? $$ $! etc — special params
            if chars[i + 1] == '?' {
                result.push_str("$?"); // let Rush evaluator handle
                i += 2;
                continue;
            }
        }
        result.push(chars[i]);
        i += 1;
    }

    result
}

/// Expand ${parameter} with modifiers.
fn expand_parameter(content: &str) -> String {
    // ${#var} — string length
    if content.starts_with('#') {
        let var = &content[1..];
        return std::env::var(var).map(|v| v.len().to_string()).unwrap_or("0".into());
    }

    // ${var:-word} — use word if unset or null
    if let Some(pos) = content.find(":-") {
        let var = &content[..pos];
        let word = &content[pos + 2..];
        let val = std::env::var(var).unwrap_or_default();
        return if val.is_empty() { word.to_string() } else { val };
    }

    // ${var-word} — use word if unset (null OK)
    if let Some(pos) = content.find('-') {
        if pos > 0 && !content[..pos].contains(':') {
            let var = &content[..pos];
            let word = &content[pos + 1..];
            return std::env::var(var).unwrap_or_else(|_| word.to_string());
        }
    }

    // ${var:=word} — assign if unset or null
    if let Some(pos) = content.find(":=") {
        let var = &content[..pos];
        let word = &content[pos + 2..];
        let val = std::env::var(var).unwrap_or_default();
        if val.is_empty() {
            unsafe { std::env::set_var(var, word) };
            return word.to_string();
        }
        return val;
    }

    // ${var:+word} — use word if set and non-null
    if let Some(pos) = content.find(":+") {
        let var = &content[..pos];
        let word = &content[pos + 2..];
        let val = std::env::var(var).unwrap_or_default();
        return if val.is_empty() { String::new() } else { word.to_string() };
    }

    // ${var:?word} — error if unset or null
    if let Some(pos) = content.find(":?") {
        let var = &content[..pos];
        let word = &content[pos + 2..];
        let val = std::env::var(var).unwrap_or_default();
        if val.is_empty() {
            eprintln!("rush: {var}: {word}");
            return String::new();
        }
        return val;
    }

    // ${var%pattern} — remove shortest suffix
    if let Some(pos) = content.find('%') {
        let double = content[pos..].starts_with("%%");
        let var = &content[..pos];
        let pattern = if double { &content[pos + 2..] } else { &content[pos + 1..] };
        let val = std::env::var(var).unwrap_or_default();
        return strip_suffix(&val, pattern, double);
    }

    // ${var#pattern} — remove shortest prefix
    if let Some(pos) = content.find('#') {
        let double = content[pos..].starts_with("##");
        let var = &content[..pos];
        let pattern = if double { &content[pos + 2..] } else { &content[pos + 1..] };
        let val = std::env::var(var).unwrap_or_default();
        return strip_prefix_pat(&val, pattern, double);
    }

    // Plain ${var}
    std::env::var(content).unwrap_or_default()
}

/// Suffix stripping: ${var%pattern} / ${var%%pattern}
/// Pattern uses shell glob: * matches any string, ? matches one char
fn strip_suffix(val: &str, pattern: &str, longest: bool) -> String {
    // Try matching the pattern against suffixes of val
    // % = shortest match from end, %% = longest match from end
    if longest {
        // Try removing from the start (longest suffix)
        for i in 0..=val.len() {
            let suffix = &val[i..];
            if shell_pattern_match(suffix, pattern) {
                return val[..i].to_string();
            }
        }
    } else {
        // Try removing from the end (shortest suffix)
        for i in (0..=val.len()).rev() {
            let suffix = &val[i..];
            if shell_pattern_match(suffix, pattern) {
                return val[..i].to_string();
            }
        }
    }
    val.to_string()
}

/// Prefix stripping: ${var#pattern} / ${var##pattern}
fn strip_prefix_pat(val: &str, pattern: &str, longest: bool) -> String {
    if longest {
        // Try removing from the end (longest prefix)
        for i in (0..=val.len()).rev() {
            let prefix = &val[..i];
            if shell_pattern_match(prefix, pattern) {
                return val[i..].to_string();
            }
        }
    } else {
        // Try removing from the start (shortest prefix)
        for i in 0..=val.len() {
            let prefix = &val[..i];
            if shell_pattern_match(prefix, pattern) {
                return val[i..].to_string();
            }
        }
    }
    val.to_string()
}

/// Simple shell pattern matching (*, ?, []).
fn shell_pattern_match(text: &str, pattern: &str) -> bool {
    let t: Vec<char> = text.chars().collect();
    let p: Vec<char> = pattern.chars().collect();
    pattern_match_impl(&t, 0, &p, 0)
}

fn pattern_match_impl(text: &[char], ti: usize, pat: &[char], pi: usize) -> bool {
    if pi == pat.len() { return ti == text.len(); }
    if pat[pi] == '*' {
        // * matches zero or more characters
        for i in ti..=text.len() {
            if pattern_match_impl(text, i, pat, pi + 1) {
                return true;
            }
        }
        return false;
    }
    if ti == text.len() { return false; }
    if pat[pi] == '?' || pat[pi] == text[ti] {
        return pattern_match_impl(text, ti + 1, pat, pi + 1);
    }
    false
}

/// Evaluate arithmetic expression (simple integer math).
fn eval_arithmetic(expr: &str) -> String {
    let expr = expr.trim();
    // Expand variables in the expression
    let expanded = expand_env_vars(expr);
    let expanded = expanded.trim();

    // Simple evaluation: support +, -, *, /, %, and parentheses
    match eval_arith_expr(expanded) {
        Some(n) => n.to_string(),
        None => "0".to_string(),
    }
}

/// Recursive arithmetic evaluator with operator precedence.
fn eval_arith_expr(expr: &str) -> Option<i64> {
    let expr = expr.trim();
    if expr.is_empty() { return Some(0); }

    // Try parsing as integer
    if let Ok(n) = expr.parse::<i64>() {
        return Some(n);
    }

    // Find the lowest-precedence operator (right-to-left for left-assoc)
    // Precedence: + - (lowest) → * / % (higher)
    let mut depth = 0;
    let mut last_add_sub = None;
    let mut last_mul_div = None;
    let chars: Vec<char> = expr.chars().collect();

    // Scan right-to-left for lowest precedence operator
    // Skip spaces when checking for unary context
    for (i, &ch) in chars.iter().enumerate().rev() {
        if ch == ')' { depth += 1; }
        else if ch == '(' { depth -= 1; }
        if depth != 0 { continue; }

        if (ch == '+' || ch == '-') && i > 0 {
            // Find the last non-space char before this operator
            let prev_nonspace = chars[..i].iter().rev().find(|c| !c.is_whitespace());
            let is_unary = match prev_nonspace {
                None => true, // at start
                Some(c) => !c.is_ascii_digit() && *c != ')',
            };
            if !is_unary {
                if last_add_sub.is_none() { last_add_sub = Some(i); }
            }
        }
        if ch == '*' || ch == '/' || ch == '%' {
            if last_mul_div.is_none() { last_mul_div = Some(i); }
        }
    }

    // Try lowest precedence first
    if let Some(pos) = last_add_sub {
        let left = eval_arith_expr(&expr[..pos])?;
        let right = eval_arith_expr(&expr[pos + 1..])?;
        return Some(match chars[pos] {
            '+' => left + right,
            '-' => left - right,
            _ => unreachable!(),
        });
    }

    if let Some(pos) = last_mul_div {
        let left = eval_arith_expr(&expr[..pos])?;
        let right = eval_arith_expr(&expr[pos + 1..])?;
        return Some(match chars[pos] {
            '*' => left * right,
            '/' if right != 0 => left / right,
            '%' if right != 0 => left % right,
            _ => 0,
        });
    }

    // Parentheses
    if expr.starts_with('(') && expr.ends_with(')') {
        return eval_arith_expr(&expr[1..expr.len() - 1]);
    }

    // Variable name → look up value
    if expr.chars().all(|c| c.is_ascii_alphanumeric() || c == '_') {
        let val = std::env::var(expr).unwrap_or_default();
        return val.parse::<i64>().ok().or(Some(0));
    }

    None
}

fn expand_tilde_only(arg: &str) -> String {
    if arg == "~" {
        return std::env::var("HOME").unwrap_or_else(|_| "~".to_string());
    }
    if arg.starts_with("~/") {
        if let Ok(home) = std::env::var("HOME") {
            return format!("{home}{}", &arg[1..]);
        }
    }
    arg.to_string()
}

/// Split command line on unquoted `|` characters.
fn split_on_pipe(line: &str) -> Vec<String> {
    let mut segments = Vec::new();
    let mut current = String::new();
    let mut in_single = false;
    let mut in_double = false;
    let chars: Vec<char> = line.chars().collect();
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

        // || is logical OR, not a pipe — don't split
        if ch == '|' && i + 1 < chars.len() && chars[i + 1] == '|' {
            current.push('|');
            current.push('|');
            i += 2;
            continue;
        }

        if ch == '|' {
            segments.push(std::mem::take(&mut current));
            i += 1;
            continue;
        }

        current.push(ch);
        i += 1;
    }

    if !current.trim().is_empty() {
        segments.push(current);
    }

    segments
}

/// Parse a command string into words, handling quotes.
pub fn parse_command_line(line: &str) -> Vec<String> {
    let mut parts = Vec::new();
    let mut current = String::new();
    let mut chars = line.chars().peekable();
    let mut in_single = false;
    let mut in_double = false;

    while let Some(c) = chars.next() {
        match c {
            '\'' if !in_double => {
                in_single = !in_single;
            }
            '"' if !in_single => {
                in_double = !in_double;
            }
            '\\' if !in_single => {
                // Backslash escaping (works in double-quoted and unquoted)
                if let Some(&next) = chars.peek() {
                    if in_double {
                        match next {
                            '"' | '\\' | '$' | '`' => {
                                current.push(chars.next().unwrap());
                            }
                            _ => {
                                current.push('\\');
                            }
                        }
                    } else {
                        // Unquoted: backslash-space = literal space, etc.
                        current.push(chars.next().unwrap());
                    }
                }
            }
            ' ' | '\t' if !in_single && !in_double => {
                if !current.is_empty() {
                    parts.push(std::mem::take(&mut current));
                }
            }
            _ => {
                current.push(c);
            }
        }
    }
    if !current.is_empty() {
        parts.push(current);
    }
    parts
}

// ── PATH Lookup ─────────────────────────────────────────────────────

/// Check if a command exists on the PATH.
pub fn command_exists(name: &str) -> bool {
    which(name).is_some()
}

/// Find the full path of a command (like `which`).
pub fn which(name: &str) -> Option<String> {
    if name.contains('/') || (cfg!(windows) && name.contains('\\')) {
        if std::path::Path::new(name).exists() {
            return Some(name.to_string());
        }
        return None;
    }

    let path_var = std::env::var("PATH").unwrap_or_default();
    let separator = if cfg!(windows) { ';' } else { ':' };

    for dir in path_var.split(separator) {
        let candidate = std::path::Path::new(dir).join(name);
        if candidate.exists() {
            return Some(candidate.to_string_lossy().to_string());
        }
        if cfg!(windows) {
            for ext in &[".exe", ".cmd", ".bat", ".com"] {
                let with_ext = std::path::Path::new(dir).join(format!("{name}{ext}"));
                if with_ext.exists() {
                    return Some(with_ext.to_string_lossy().to_string());
                }
            }
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_simple_command() {
        assert_eq!(parse_command_line("ls -la /tmp"), vec!["ls", "-la", "/tmp"]);
    }

    #[test]
    fn parse_quoted() {
        assert_eq!(parse_command_line(r#"echo "hello world""#), vec!["echo", "hello world"]);
    }

    #[test]
    fn parse_single_quoted() {
        assert_eq!(parse_command_line("echo 'hello world'"), vec!["echo", "hello world"]);
    }

    #[test]
    fn parse_empty() {
        assert!(parse_command_line("").is_empty());
    }

    #[test]
    fn parse_backslash_space() {
        assert_eq!(parse_command_line(r"echo hello\ world"), vec!["echo", "hello world"]);
    }

    #[test]
    fn which_finds_ls() {
        if cfg!(not(windows)) {
            assert!(which("ls").is_some());
        }
    }

    #[test]
    fn split_pipe_basic() {
        let segs = split_on_pipe("ls | grep foo | wc -l");
        assert_eq!(segs.len(), 3);
        assert_eq!(segs[0].trim(), "ls");
        assert_eq!(segs[1].trim(), "grep foo");
        assert_eq!(segs[2].trim(), "wc -l");
    }

    #[test]
    fn split_pipe_quoted() {
        let segs = split_on_pipe(r#"echo "a | b" | wc"#);
        assert_eq!(segs.len(), 2);
        assert!(segs[0].contains("a | b"));
    }

    #[test]
    fn split_pipe_or_not_pipe() {
        // || is logical OR, not a pipe
        let segs = split_on_pipe("false || echo yes");
        assert_eq!(segs.len(), 1);
    }

    #[test]
    fn tilde_expansion() {
        let home = std::env::var("HOME").unwrap_or_default();
        assert_eq!(expand_tilde("~"), home);
        assert_eq!(expand_tilde("~/bin"), format!("{home}/bin"));
        assert_eq!(expand_tilde("/usr/bin"), "/usr/bin");
    }

    #[test]
    fn native_echo() {
        let result = run_native_capture("echo hello");
        assert_eq!(result.stdout.trim(), "hello");
        assert_eq!(result.exit_code, 0);
    }

    #[test]
    fn native_pipeline() {
        let result = run_native_capture("echo hello | wc -c");
        let count: i32 = result.stdout.trim().parse().unwrap_or(0);
        assert!(count > 0, "expected byte count > 0, got {count}");
    }

    #[test]
    fn native_false() {
        let result = run_native("/usr/bin/false");
        assert_ne!(result.exit_code, 0);
    }

    #[test]
    fn redirect_stdout() {
        let tmp = std::env::temp_dir().join("rush_redirect_test.txt");
        let path = tmp.to_string_lossy().to_string();
        run_native(&format!("echo redirect_test > {path}"));
        let content = std::fs::read_to_string(&tmp).unwrap_or_default();
        assert_eq!(content.trim(), "redirect_test");
        std::fs::remove_file(&tmp).ok();
    }

    #[test]
    fn redirect_append() {
        let tmp = std::env::temp_dir().join("rush_append_test.txt");
        let path = tmp.to_string_lossy().to_string();
        run_native(&format!("echo line1 > {path}"));
        run_native(&format!("echo line2 >> {path}"));
        let content = std::fs::read_to_string(&tmp).unwrap_or_default();
        let lines: Vec<&str> = content.lines().collect();
        assert_eq!(lines.len(), 2);
        assert_eq!(lines[0], "line1");
        assert_eq!(lines[1], "line2");
        std::fs::remove_file(&tmp).ok();
    }

    #[test]
    fn extract_redir_stdout() {
        let parts = vec!["echo".into(), "hello".into(), ">".into(), "out.txt".into()];
        let (clean, redir) = extract_redirections(parts);
        assert_eq!(clean, vec!["echo", "hello"]);
        assert_eq!(redir.len(), 1);
        assert!(matches!(&redir[0], Redirect::StdoutWrite(f) if f == "out.txt"));
    }

    #[test]
    fn extract_redir_stderr() {
        let parts = vec!["cmd".into(), "2>".into(), "/dev/null".into()];
        let (clean, redir) = extract_redirections(parts);
        assert_eq!(clean, vec!["cmd"]);
        assert!(matches!(&redir[0], Redirect::StderrWrite(f) if f == "/dev/null"));
    }

    #[test]
    fn extract_redir_stderr_nospace() {
        let parts = vec!["cmd".into(), "2>/dev/null".into()];
        let (clean, redir) = extract_redirections(parts);
        assert_eq!(clean, vec!["cmd"]);
        assert!(matches!(&redir[0], Redirect::StderrWrite(f) if f == "/dev/null"));
    }

    #[test]
    fn extract_redir_stderr_to_stdout() {
        let parts = vec!["cmd".into(), "2>&1".into()];
        let (clean, redir) = extract_redirections(parts);
        assert_eq!(clean, vec!["cmd"]);
        assert!(matches!(&redir[0], Redirect::StderrToStdout));
    }

    #[test]
    fn extract_multiple_redirects() {
        let parts = vec!["cmd".into(), ">".into(), "out.txt".into(), "2>".into(), "err.txt".into()];
        let (clean, redir) = extract_redirections(parts);
        assert_eq!(clean, vec!["cmd"]);
        assert_eq!(redir.len(), 2);
        assert!(matches!(&redir[0], Redirect::StdoutWrite(f) if f == "out.txt"));
        assert!(matches!(&redir[1], Redirect::StderrWrite(f) if f == "err.txt"));
    }

    // ── Glob expansion ──────────────────────────────────────────────

    #[test]
    fn glob_expansion_star() {
        // Create temp files for glob test
        let dir = std::env::temp_dir().join("rush_glob_test");
        std::fs::create_dir_all(&dir).ok();
        std::fs::write(dir.join("a.txt"), "").ok();
        std::fs::write(dir.join("b.txt"), "").ok();
        std::fs::write(dir.join("c.log"), "").ok();

        let pattern = format!("{}/*.txt", dir.to_string_lossy());
        let result = parse_and_expand(&format!("ls {pattern}"));
        // Should have "ls" + 2 .txt files
        assert!(result.len() >= 3, "expected ls + 2 files, got {:?}", result);
        assert_eq!(result[0], "ls");
        assert!(result.iter().any(|f| f.ends_with("a.txt")));
        assert!(result.iter().any(|f| f.ends_with("b.txt")));
        assert!(!result.iter().any(|f| f.ends_with("c.log")));

        // Cleanup
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn glob_no_match_passes_through() {
        let result = parse_and_expand("ls /nonexistent/*.xyz");
        assert_eq!(result, vec!["ls", "/nonexistent/*.xyz"]);
    }

    #[test]
    fn glob_quoted_not_expanded() {
        let result = parse_and_expand("echo '*.txt'");
        assert_eq!(result, vec!["echo", "*.txt"]);
    }

    // ── Env var expansion ───────────────────────────────────────────

    #[test]
    fn env_var_expansion() {
        let home = std::env::var("HOME").unwrap_or_default();
        let result = expand_env_vars("$HOME/bin");
        assert_eq!(result, format!("{home}/bin"));
    }

    #[test]
    fn env_var_braces() {
        let home = std::env::var("HOME").unwrap_or_default();
        let result = expand_env_vars("${HOME}/bin");
        assert_eq!(result, format!("{home}/bin"));
    }

    #[test]
    fn env_var_missing() {
        let result = expand_env_vars("$NONEXISTENT_VAR_XYZ");
        assert_eq!(result, "");
    }

    #[test]
    fn env_var_in_command() {
        let result = parse_and_expand("echo $HOME");
        let home = std::env::var("HOME").unwrap_or_default();
        assert_eq!(result, vec!["echo", &home]);
    }

    // ── Brace expansion ─────────────────────────────────────────────

    #[test]
    fn brace_simple() {
        assert_eq!(expand_braces("file.{txt,log,md}"),
            vec!["file.txt", "file.log", "file.md"]);
    }

    #[test]
    fn brace_prefix_suffix() {
        assert_eq!(expand_braces("pre{a,b}post"),
            vec!["preapost", "prebpost"]);
    }

    #[test]
    fn brace_sequence_numeric() {
        assert_eq!(expand_braces("{1..5}"),
            vec!["1", "2", "3", "4", "5"]);
    }

    #[test]
    fn brace_sequence_alpha() {
        assert_eq!(expand_braces("{a..e}"),
            vec!["a", "b", "c", "d", "e"]);
    }

    #[test]
    fn brace_no_expansion() {
        // No commas or .. → no expansion
        assert_eq!(expand_braces("{foo}"), vec!["{foo}"]);
    }

    #[test]
    fn brace_in_command() {
        let result = parse_and_expand("echo {a,b,c}");
        assert_eq!(result, vec!["echo", "a", "b", "c"]);
    }

    // ── Arithmetic expansion ────────────────────────────────────────

    #[test]
    fn arith_simple() {
        assert_eq!(eval_arithmetic("2 + 3"), "5");
    }

    #[test]
    fn arith_multiply() {
        assert_eq!(eval_arithmetic("4 * 5"), "20");
    }

    #[test]
    fn arith_precedence() {
        assert_eq!(eval_arithmetic("2 + 3 * 4"), "14");
    }

    #[test]
    fn arith_parens() {
        assert_eq!(eval_arithmetic("(2 + 3) * 4"), "20");
    }

    #[test]
    fn arith_in_command() {
        let result = expand_env_vars("echo $((2 + 3))");
        assert_eq!(result, "echo 5");
    }

    // ── Parameter expansion modifiers ───────────────────────────────

    #[test]
    fn param_default() {
        // ${NONEXISTENT_XYZ:-fallback}
        assert_eq!(expand_parameter("NONEXISTENT_XYZ:-fallback"), "fallback");
    }

    #[test]
    fn param_default_exists() {
        let home = std::env::var("HOME").unwrap_or_default();
        assert_eq!(expand_parameter("HOME:-fallback"), home);
    }

    #[test]
    fn param_length() {
        unsafe { std::env::set_var("_TEST_LEN", "hello") };
        assert_eq!(expand_parameter("#_TEST_LEN"), "5");
        unsafe { std::env::remove_var("_TEST_LEN") };
    }

    #[test]
    fn param_suffix_strip() {
        unsafe { std::env::set_var("_TEST_FILE", "archive.tar.gz") };
        assert_eq!(expand_parameter("_TEST_FILE%.*"), "archive.tar");
        assert_eq!(expand_parameter("_TEST_FILE%%.*"), "archive");
        unsafe { std::env::remove_var("_TEST_FILE") };
    }

    #[test]
    fn param_prefix_strip() {
        unsafe { std::env::set_var("_TEST_PATH", "/usr/local/bin") };
        assert_eq!(expand_parameter("_TEST_PATH#*/"), "usr/local/bin"); // shortest: just "/"
        assert_eq!(expand_parameter("_TEST_PATH##*/"), "bin");          // longest: "/usr/local/"
        unsafe { std::env::remove_var("_TEST_PATH") };
    }
}
