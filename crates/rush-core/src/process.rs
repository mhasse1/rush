use std::process::{Command, Stdio};

/// Result of running an external command.
#[derive(Debug, Clone)]
pub struct CommandResult {
    pub stdout: String,
    pub stderr: String,
    pub exit_code: i32,
}

// ── Single Command ──────────────────────────────────────────────────

/// Run a single command with inherited stdio (TTY preserved).
pub fn run_command(program: &str, args: &[&str]) -> CommandResult {
    match Command::new(program)
        .args(args)
        .stdin(Stdio::inherit())
        .stdout(Stdio::inherit())
        .stderr(Stdio::inherit())
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
fn parse_and_expand(line: &str) -> Vec<String> {
    let parts = parse_command_line_with_quote_info(line);
    let mut result = Vec::new();

    for (word, was_quoted) in parts {
        let expanded = expand_tilde(&word);

        // Only glob-expand unquoted words that contain glob characters
        if !was_quoted && contains_glob_chars(&expanded) {
            match glob::glob(&expanded) {
                Ok(paths) => {
                    let mut matches: Vec<String> = paths
                        .filter_map(|p| p.ok())
                        .map(|p| p.to_string_lossy().to_string())
                        .collect();
                    if matches.is_empty() {
                        // No matches: pass through literally (bash behavior)
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

    result
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

/// Expand ~ to $HOME and $VAR to environment variable value.
fn expand_tilde(arg: &str) -> String {
    // First expand env vars
    let arg = expand_env_vars(arg);
    expand_tilde_only(&arg)
}

/// Expand $VAR and ${VAR} in a string to environment variable values.
fn expand_env_vars(arg: &str) -> String {
    if !arg.contains('$') {
        return arg.to_string();
    }

    let mut result = String::with_capacity(arg.len());
    let chars: Vec<char> = arg.chars().collect();
    let mut i = 0;

    while i < chars.len() {
        if chars[i] == '$' && i + 1 < chars.len() {
            // ${VAR} form
            if chars[i + 1] == '{' {
                i += 2;
                let mut var_name = String::new();
                while i < chars.len() && chars[i] != '}' {
                    var_name.push(chars[i]);
                    i += 1;
                }
                if i < chars.len() { i += 1; } // skip }
                result.push_str(&std::env::var(&var_name).unwrap_or_default());
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
        }
        result.push(chars[i]);
        i += 1;
    }

    result
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
}
