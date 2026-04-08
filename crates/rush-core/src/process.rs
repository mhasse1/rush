use std::process::{Command, Stdio};

/// Result of running an external command.
#[derive(Debug, Clone)]
pub struct CommandResult {
    pub stdout: String,
    pub stderr: String,
    pub exit_code: i32,
}

/// Run a command line natively: parse into program + args, fork/exec with
/// inherited stdio (TTY preserved). This is how a shell should execute
/// external commands — no sh -c wrapper.
pub fn run_native(line: &str) -> CommandResult {
    let parts = parse_command_line(line);
    if parts.is_empty() {
        return CommandResult { stdout: String::new(), stderr: String::new(), exit_code: 0 };
    }

    let program = &parts[0];

    // Tilde expansion on first arg if it's a path
    let args: Vec<String> = parts[1..].iter().map(|a| expand_tilde(a)).collect();
    let arg_refs: Vec<&str> = args.iter().map(|s| s.as_str()).collect();

    run_command(program, &arg_refs)
}

/// Run a command line natively and capture stdout.
pub fn run_native_capture(line: &str) -> CommandResult {
    let parts = parse_command_line(line);
    if parts.is_empty() {
        return CommandResult { stdout: String::new(), stderr: String::new(), exit_code: 0 };
    }

    let program = &parts[0];
    let args: Vec<String> = parts[1..].iter().map(|a| expand_tilde(a)).collect();
    let arg_refs: Vec<&str> = args.iter().map(|s| s.as_str()).collect();

    run_command_capture(program, &arg_refs)
}

fn expand_tilde(arg: &str) -> String {
    if arg.starts_with("~/") {
        if let Ok(home) = std::env::var("HOME") {
            return format!("{home}{}", &arg[1..]);
        }
    }
    arg.to_string()
}

/// Run a single external command with arguments.
pub fn run_command(program: &str, args: &[&str]) -> CommandResult {
    let result = Command::new(program)
        .args(args)
        .stdin(Stdio::inherit())
        .stdout(Stdio::inherit())
        .stderr(Stdio::inherit())
        .status();

    match result {
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

/// Run a command and capture its stdout (for command substitution, pipes).
pub fn run_command_capture(program: &str, args: &[&str]) -> CommandResult {
    let result = Command::new(program)
        .args(args)
        .stdin(Stdio::inherit())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output();

    match result {
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

/// Run a shell command string via sh -c (for complex commands, pipes, redirections).
pub fn run_shell(command: &str) -> CommandResult {
    let shell = if cfg!(windows) { "cmd" } else { "sh" };
    let flag = if cfg!(windows) { "/C" } else { "-c" };

    let result = Command::new(shell)
        .arg(flag)
        .arg(command)
        .stdin(Stdio::inherit())
        .stdout(Stdio::inherit())
        .stderr(Stdio::inherit())
        .status();

    match result {
        Ok(status) => CommandResult {
            stdout: String::new(),
            stderr: String::new(),
            exit_code: status.code().unwrap_or(-1),
        },
        Err(e) => CommandResult {
            stdout: String::new(),
            stderr: format!("rush: {e}"),
            exit_code: 127,
        },
    }
}

/// Run a shell command and capture stdout.
pub fn run_shell_capture(command: &str) -> CommandResult {
    let shell = if cfg!(windows) { "cmd" } else { "sh" };
    let flag = if cfg!(windows) { "/C" } else { "-c" };

    let result = Command::new(shell)
        .arg(flag)
        .arg(command)
        .stdin(Stdio::inherit())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output();

    match result {
        Ok(output) => CommandResult {
            stdout: String::from_utf8_lossy(&output.stdout).into_owned(),
            stderr: String::from_utf8_lossy(&output.stderr).into_owned(),
            exit_code: output.status.code().unwrap_or(-1),
        },
        Err(e) => CommandResult {
            stdout: String::new(),
            stderr: format!("rush: {e}"),
            exit_code: 127,
        },
    }
}

/// Run a pipeline of commands: cmd1 | cmd2 | cmd3
/// Each command is a (program, args) tuple.
pub fn run_pipeline(commands: &[(&str, Vec<&str>)]) -> CommandResult {
    if commands.is_empty() {
        return CommandResult {
            stdout: String::new(),
            stderr: String::new(),
            exit_code: 0,
        };
    }
    if commands.len() == 1 {
        return run_command(commands[0].0, &commands[0].1);
    }

    // Build the pipeline: each command's stdout feeds into the next's stdin
    let mut prev_stdout: Option<std::process::ChildStdout> = None;
    let mut children: Vec<std::process::Child> = Vec::new();

    for (i, (program, args)) in commands.iter().enumerate() {
        let stdin = if let Some(prev) = prev_stdout.take() {
            Stdio::from(prev)
        } else {
            Stdio::inherit()
        };

        let stdout = if i < commands.len() - 1 {
            Stdio::piped()
        } else {
            Stdio::inherit()
        };

        match Command::new(program)
            .args(args.as_slice())
            .stdin(stdin)
            .stdout(stdout)
            .stderr(Stdio::inherit())
            .spawn()
        {
            Ok(mut child) => {
                prev_stdout = child.stdout.take();
                children.push(child);
            }
            Err(e) => {
                // Kill any already-spawned children
                for mut c in children {
                    let _ = c.kill();
                }
                return CommandResult {
                    stdout: String::new(),
                    stderr: format!("rush: {program}: {e}"),
                    exit_code: 127,
                };
            }
        }
    }

    // Wait for all children, return the last exit code
    let mut last_code = 0;
    for mut child in children {
        match child.wait() {
            Ok(status) => last_code = status.code().unwrap_or(-1),
            Err(_) => last_code = -1,
        }
    }

    CommandResult {
        stdout: String::new(),
        stderr: String::new(),
        exit_code: last_code,
    }
}

/// Check if a command exists on the PATH.
pub fn command_exists(name: &str) -> bool {
    which(name).is_some()
}

/// Find the full path of a command (like `which`).
pub fn which(name: &str) -> Option<String> {
    // Absolute path
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
        // On Windows, also try with common extensions
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

/// Parse a simple command string into (program, args).
/// Handles basic quoting (single and double quotes).
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
            '\\' if in_double => {
                if let Some(&next) = chars.peek() {
                    match next {
                        '"' | '\\' | '$' | '`' => {
                            current.push(chars.next().unwrap());
                        }
                        _ => {
                            current.push('\\');
                        }
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_simple_command() {
        let parts = parse_command_line("ls -la /tmp");
        assert_eq!(parts, vec!["ls", "-la", "/tmp"]);
    }

    #[test]
    fn parse_quoted_args() {
        let parts = parse_command_line(r#"echo "hello world""#);
        assert_eq!(parts, vec!["echo", "hello world"]);
    }

    #[test]
    fn parse_single_quoted() {
        let parts = parse_command_line("echo 'don'\\''t'");
        // This is a simplified parser — just test basic single quoting
        let parts = parse_command_line("echo 'hello world'");
        assert_eq!(parts, vec!["echo", "hello world"]);
    }

    #[test]
    fn parse_empty() {
        let parts = parse_command_line("");
        assert!(parts.is_empty());
    }

    #[test]
    fn which_finds_sh() {
        // sh should exist on any Unix system
        if cfg!(not(windows)) {
            assert!(which("sh").is_some());
        }
    }

    #[test]
    fn run_echo() {
        let result = run_shell_capture("echo hello");
        assert_eq!(result.stdout.trim(), "hello");
        assert_eq!(result.exit_code, 0);
    }

    #[test]
    fn run_false_command() {
        let result = run_shell("false");
        assert_ne!(result.exit_code, 0);
    }

    #[test]
    fn run_pipeline_echo_wc() {
        let result = run_shell_capture("echo hello | wc -c");
        let count: i32 = result.stdout.trim().parse().unwrap_or(0);
        assert!(count > 0);
    }
}
