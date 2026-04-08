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

    if let Some(redirect) = &redirects {
        run_with_redirect(program, &args, redirect)
    } else {
        run_command(program, &args)
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
    StdoutWrite(String),   // > file
    StdoutAppend(String),  // >> file
    StdinRead(String),     // < file
}

/// Extract redirections from argument list.
fn extract_redirections(parts: Vec<String>) -> (Vec<String>, Option<Redirect>) {
    let mut redirect = None;
    let mut clean = Vec::new();
    let mut i = 0;

    while i < parts.len() {
        if parts[i] == ">" && i + 1 < parts.len() {
            redirect = Some(Redirect::StdoutWrite(parts[i + 1].clone()));
            i += 2;
        } else if parts[i] == ">>" && i + 1 < parts.len() {
            redirect = Some(Redirect::StdoutAppend(parts[i + 1].clone()));
            i += 2;
        } else if parts[i] == "<" && i + 1 < parts.len() {
            redirect = Some(Redirect::StdinRead(parts[i + 1].clone()));
            i += 2;
        } else if parts[i].starts_with('>') && parts[i].len() > 1 {
            // >file (no space)
            let file = parts[i][1..].to_string();
            redirect = Some(Redirect::StdoutWrite(file));
            i += 1;
        } else if parts[i].starts_with(">>") && parts[i].len() > 2 {
            let file = parts[i][2..].to_string();
            redirect = Some(Redirect::StdoutAppend(file));
            i += 1;
        } else {
            clean.push(parts[i].clone());
            i += 1;
        }
    }

    (clean, redirect)
}

/// Run a command with I/O redirection.
fn run_with_redirect(program: &str, args: &[&str], redirect: &Redirect) -> CommandResult {
    match redirect {
        Redirect::StdoutWrite(path) | Redirect::StdoutAppend(path) => {
            let file = if matches!(redirect, Redirect::StdoutAppend(_)) {
                std::fs::OpenOptions::new().create(true).append(true).open(path)
            } else {
                std::fs::File::create(path)
            };
            match file {
                Ok(file) => {
                    match Command::new(program)
                        .args(args)
                        .stdin(Stdio::inherit())
                        .stdout(Stdio::from(file))
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
                Err(e) => CommandResult {
                    stdout: String::new(),
                    stderr: format!("rush: {path}: {e}"),
                    exit_code: 1,
                },
            }
        }
        Redirect::StdinRead(path) => {
            match std::fs::File::open(path) {
                Ok(file) => {
                    match Command::new(program)
                        .args(args)
                        .stdin(Stdio::from(file))
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
                Err(e) => CommandResult {
                    stdout: String::new(),
                    stderr: format!("rush: {path}: {e}"),
                    exit_code: 1,
                },
            }
        }
    }
}

// ── Parsing & Expansion ─────────────────────────────────────────────

/// Parse a command line into words and expand tildes and globs.
fn parse_and_expand(line: &str) -> Vec<String> {
    let parts = parse_command_line(line);
    parts.into_iter().map(|p| expand_tilde(&p)).collect()
}

/// Expand ~ to $HOME.
fn expand_tilde(arg: &str) -> String {
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
        assert!(matches!(redir, Some(Redirect::StdoutWrite(f)) if f == "out.txt"));
    }
}
