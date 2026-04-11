use std::process::{Command, Stdio};

use crate::flags;
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
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
            #[cfg(unix)]
            reclaim_terminal();

            // On Windows, retry via cmd.exe — echo, dir, etc. are shell builtins
            #[cfg(windows)]
            {
                return run_via_cmd(program, args, false);
            }

            #[cfg(not(windows))]
            CommandResult {
                stdout: String::new(),
                stderr: format!("rush: {program}: command not found"),
                exit_code: 127,
            }
        }
        Err(e) => {
            #[cfg(unix)]
            reclaim_terminal();

            let exit_code = if e.kind() == std::io::ErrorKind::PermissionDenied { 126 } else { 127 };
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
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
            // On Windows, retry via cmd.exe for shell builtins
            #[cfg(windows)]
            {
                return run_via_cmd(program, args, true);
            }
            #[cfg(not(windows))]
            CommandResult {
                stdout: String::new(),
                stderr: format!("rush: {program}: command not found"),
                exit_code: 127,
            }
        }
        Err(e) => CommandResult {
            stdout: String::new(),
            stderr: format!("rush: {program}: {e}"),
            exit_code: 127,
        },
    }
}

/// Windows: run a command through cmd.exe /C as fallback.
/// Handles shell builtins (echo, dir, type, etc.) and pipes/redirects.
#[cfg(windows)]
fn run_via_cmd(program: &str, args: &[&str], capture: bool) -> CommandResult {
    // Reconstruct the command line for cmd.exe
    let mut cmdline = program.to_string();
    for arg in args {
        // Quote args that contain spaces
        if arg.contains(' ') || arg.contains('"') {
            cmdline.push_str(&format!(" \"{}\"", arg.replace('"', "\\\"")));
        } else {
            cmdline.push(' ');
            cmdline.push_str(arg);
        }
    }

    let mut cmd = Command::new("cmd.exe");
    cmd.args(["/C", &cmdline]);

    if capture {
        cmd.stdin(Stdio::inherit())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());

        match cmd.output() {
            Ok(output) => CommandResult {
                stdout: String::from_utf8_lossy(&output.stdout).trim_end().to_string(),
                stderr: String::from_utf8_lossy(&output.stderr).trim_end().to_string(),
                exit_code: output.status.code().unwrap_or(-1),
            },
            Err(e) => CommandResult {
                stdout: String::new(),
                stderr: format!("rush: cmd.exe: {e}"),
                exit_code: 127,
            },
        }
    } else {
        cmd.stdin(Stdio::inherit())
            .stdout(Stdio::inherit())
            .stderr(Stdio::inherit());

        match cmd.status() {
            Ok(status) => CommandResult {
                stdout: String::new(),
                stderr: String::new(),
                exit_code: status.code().unwrap_or(-1),
            },
            Err(e) => CommandResult {
                stdout: String::new(),
                stderr: format!("rush: cmd.exe: {e}"),
                exit_code: 127,
            },
        }
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

        let spawn_result = Command::new(program)
            .args(&args)
            .stdin(stdin)
            .stdout(stdout)
            .stderr(Stdio::inherit())
            .spawn();

        let spawn_result = match spawn_result {
            Ok(child) => Ok(child),
            #[cfg(windows)]
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                // Windows fallback: try via cmd.exe for shell builtins
                let mut cmdline = program.to_string();
                for a in &args { cmdline.push(' '); cmdline.push_str(a); }
                let stdin2 = if prev_stdout.is_some() {
                    // Can't reuse taken stdin — fall back to full cmd pipeline
                    Stdio::inherit()
                } else {
                    Stdio::inherit()
                };
                let stdout2 = if is_last && !capture_last { Stdio::inherit() } else { Stdio::piped() };
                Command::new("cmd.exe")
                    .args(["/C", &cmdline])
                    .stdin(stdin2)
                    .stdout(stdout2)
                    .stderr(Stdio::inherit())
                    .spawn()
            }
            Err(e) => Err(e),
        };

        match spawn_result {
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
    StdoutClobber(String),   // >| file (override noclobber)
    StdoutAppend(String),    // >> file
    StdinRead(String),       // < file
    StderrWrite(String),     // 2> file
    StderrAppend(String),    // 2>> file
    StderrToStdout,          // 2>&1
    FdDup(i32, i32),         // N>&M or N<&M (dup fd M to fd N)
    FdClose(i32),            // N<&- or N>&-
    ReadWrite(String),       // N<>file (open for read+write)
}

/// Extract redirections from argument list. Supports multiple redirections.
fn extract_redirections(parts: Vec<String>) -> (Vec<String>, Vec<Redirect>) {
    let mut redirects = Vec::new();
    let mut clean = Vec::new();
    let mut i = 0;

    while i < parts.len() {
        // N>&- or N<&- (close fd)
        if (parts[i].ends_with(">&-") || parts[i].ends_with("<&-")) && parts[i].len() >= 3 {
            let fd_str = &parts[i][..parts[i].len() - 3];
            if let Ok(fd) = fd_str.parse::<i32>() {
                redirects.push(Redirect::FdClose(fd));
                i += 1;
                continue;
            }
        }

        // N>&M or N<&M (general fd duplication)
        if (parts[i].contains(">&") || parts[i].contains("<&"))
            && !parts[i].ends_with("&-")
            && parts[i] != "2>&1"
        {
            // Parse N>&M
            let (before, after) = if let Some(p) = parts[i].find(">&") {
                (&parts[i][..p], &parts[i][p + 2..])
            } else if let Some(p) = parts[i].find("<&") {
                (&parts[i][..p], &parts[i][p + 2..])
            } else {
                ("", "")
            };
            if let (Ok(dst), Ok(src)) = (before.parse::<i32>(), after.parse::<i32>()) {
                redirects.push(Redirect::FdDup(dst, src));
                i += 1;
                continue;
            }
        }

        // <> file (read+write)
        if parts[i] == "<>" && i + 1 < parts.len() {
            redirects.push(Redirect::ReadWrite(parts[i + 1].clone()));
            i += 2;
            continue;
        }

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
        // >| file (clobber override)
        else if parts[i] == ">|" && i + 1 < parts.len() {
            redirects.push(Redirect::StdoutClobber(parts[i + 1].clone()));
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
                // Respect noclobber (set -C): refuse to overwrite existing file
                if flags::noclobber() && std::path::Path::new(path).exists() {
                    return CommandResult { stdout: String::new(), stderr: format!("rush: {path}: cannot overwrite existing file (noclobber)"), exit_code: 1 };
                }
                match std::fs::File::create(path) {
                    Ok(f) => stdout_file = Some(f),
                    Err(e) => return CommandResult { stdout: String::new(), stderr: format!("rush: {path}: {e}"), exit_code: 1 },
                }
            }
            Redirect::StdoutClobber(path) => {
                // >| always overwrites regardless of noclobber
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
                stderr_cfg = Stdio::inherit(); // simplified — both to terminal
            }
            Redirect::ReadWrite(path) => {
                match std::fs::OpenOptions::new().read(true).write(true).create(true).open(path) {
                    Ok(f) => {
                        // For <>, we'd need to dup the fd for both stdin and stdout.
                        // Simplified: open for stdin (most common use case for lock files)
                        stdin_file = Some(f);
                    }
                    Err(e) => return CommandResult { stdout: String::new(), stderr: format!("rush: {path}: {e}"), exit_code: 1 },
                }
            }
            Redirect::FdDup(_dst, _src) => {
                // fd duplication: N>&M needs dup2() in the child process.
                // For the common case 2>&1, we handle it via StderrToStdout.
                // General case requires pre_exec with raw dup2, which we do
                // for foreground process setup. For now, map known patterns:
                // (handled by StderrToStdout detection above)
            }
            Redirect::FdClose(fd) => {
                // Close a file descriptor — set to null
                match fd {
                    0 => { stdin_file = None; stdin_cfg = Stdio::null(); }
                    1 => { stdout_file = None; stdout_cfg = Stdio::null(); }
                    2 => { stderr_file = None; stderr_cfg = Stdio::null(); }
                    _ => {} // other fds not supported via Stdio
                }
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
    let result = Command::new(program)
        .args(args)
        .stdin(stdin)
        .stdout(stdout)
        .stderr(stderr)
        .status();

    match result {
        Ok(status) => CommandResult {
            stdout: String::new(),
            stderr: String::new(),
            exit_code: status.code().unwrap_or(-1),
        },
        #[cfg(windows)]
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
            // Windows: retry via cmd.exe for shell builtins with redirections
            let mut cmdline = program.to_string();
            for a in args { cmdline.push(' '); cmdline.push_str(a); }
            match Command::new("cmd.exe")
                .args(["/C", &cmdline])
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
                Err(e2) => CommandResult {
                    stdout: String::new(),
                    stderr: format!("rush: {program}: {e2}"),
                    exit_code: 127,
                },
            }
        }
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
/// env_vars (on whole line) → parse → brace → IFS split → tilde → glob → quote_removal
fn parse_and_expand(line: &str) -> Vec<String> {
    // Expand $VAR, ${VAR:-default}, and $((arithmetic)) on whole line first
    // (before word splitting, so $((2 + 3 * 4)) works as one expression)
    let line = expand_env_vars(line);
    let parts = parse_command_line_with_quote_info(&line);
    let mut result = Vec::new();

    for (word, was_quoted) in parts {
        // 0. IFS field splitting on unquoted words
        // If a word contains whitespace and wasn't quoted, IFS split it
        let ifs = std::env::var("IFS").unwrap_or_else(|_| " \t\n".to_string());
        let words_after_ifs: Vec<(String, bool)> = if !was_quoted && !ifs.is_empty() {
            // Only split if the word looks like it came from expansion (contains IFS chars)
            let has_ifs = word.chars().any(|c| ifs.contains(c));
            if has_ifs && word.contains(' ') {
                // IFS split: whitespace IFS chars collapse, non-whitespace delimit
                ifs_split(&word, &ifs).into_iter().map(|w| (w, false)).collect()
            } else {
                vec![(word, was_quoted)]
            }
        } else {
            vec![(word, was_quoted)]
        };

        for (word, was_quoted) in words_after_ifs {
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

            // 5. Pathname/glob expansion (unquoted only, unless set -f)
            if !was_quoted && !flags::noglob() && contains_glob_chars(&expanded) {
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
        } // end IFS word loop
    }

    result
}

/// IFS field splitting per POSIX rules.
/// Whitespace IFS chars collapse (leading/trailing stripped).
/// Non-whitespace IFS chars delimit (adjacent = empty field).
fn ifs_split(word: &str, ifs: &str) -> Vec<String> {
    if ifs.is_empty() {
        return vec![word.to_string()];
    }

    let is_ifs_ws = |c: char| ifs.contains(c) && (c == ' ' || c == '\t' || c == '\n');
    let is_ifs_char = |c: char| ifs.contains(c);

    let mut fields = Vec::new();
    let mut current = String::new();
    let chars: Vec<char> = word.chars().collect();
    let mut i = 0;

    // Strip leading IFS whitespace
    while i < chars.len() && is_ifs_ws(chars[i]) {
        i += 1;
    }

    while i < chars.len() {
        let c = chars[i];
        if is_ifs_char(c) {
            // This character is an IFS delimiter
            if !current.is_empty() || !is_ifs_ws(c) {
                fields.push(std::mem::take(&mut current));
            }
            // Skip consecutive IFS whitespace
            if is_ifs_ws(c) {
                while i + 1 < chars.len() && is_ifs_ws(chars[i + 1]) {
                    i += 1;
                }
            }
        } else {
            current.push(c);
        }
        i += 1;
    }

    if !current.is_empty() {
        fields.push(current);
    }

    fields
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
            // $'...' ANSI-C quoting
            '$' if !in_single && !in_double && chars.peek() == Some(&'\'') => {
                chars.next(); // consume '
                was_quoted = true;
                while let Some(ch) = chars.next() {
                    if ch == '\'' { break; }
                    if ch == '\\' {
                        match chars.next() {
                            Some('n') => current.push('\n'),
                            Some('t') => current.push('\t'),
                            Some('r') => current.push('\r'),
                            Some('\\') => current.push('\\'),
                            Some('\'') => current.push('\''),
                            Some('"') => current.push('"'),
                            Some('a') => current.push('\x07'),
                            Some('b') => current.push('\x08'),
                            Some('f') => current.push('\x0C'),
                            Some('v') => current.push('\x0B'),
                            Some('e') => current.push('\x1b'),
                            Some(d) if d.is_ascii_digit() && d <= '7' => {
                                // \ddd octal byte (1-3 digits)
                                let mut oct = String::new();
                                oct.push(d);
                                for _ in 0..2 {
                                    if let Some(&o) = chars.peek() {
                                        if o.is_ascii_digit() && o <= '7' { oct.push(chars.next().unwrap()); }
                                        else { break; }
                                    }
                                }
                                if let Ok(byte) = u8::from_str_radix(&oct, 8) {
                                    current.push(byte as char);
                                }
                            }
                            Some('x') => {
                                // \xHH hex byte
                                let mut hex = String::new();
                                for _ in 0..2 {
                                    if let Some(&h) = chars.peek() {
                                        if h.is_ascii_hexdigit() { hex.push(chars.next().unwrap()); }
                                        else { break; }
                                    }
                                }
                                if let Ok(byte) = u8::from_str_radix(&hex, 16) {
                                    current.push(byte as char);
                                }
                            }
                            Some(other) => { current.push('\\'); current.push(other); }
                            None => current.push('\\'),
                        }
                    } else {
                        current.push(ch);
                    }
                }
            }
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
                        // Backslash escapes the next character (POSIX behavior).
                        // On Windows, env var values have backslashes pre-escaped
                        // by normalize_path_separators() during expansion, so this is
                        // consistent across platforms.
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

/// Public wrapper for tests.
pub fn expand_env_vars_pub(arg: &str) -> String { expand_env_vars(arg) }

/// Public wrapper for IFS split tests.
pub fn ifs_split_pub(word: &str, ifs: &str) -> Vec<String> { ifs_split(word, ifs) }

/// Normalize a path-like value for Rush's internal representation.
/// On Windows, converts backslashes to forward slashes so paths are
/// consistent across platforms. On Unix, this is a no-op.
///
/// This means `$HOME` on Windows gives `C:/Users/mark` (not `C:\Users\mark`),
/// keeping all Rush scripts and prompt output Unix-style.
/// Use `.native_path` string method to convert back when needed.
#[inline]
fn normalize_path_separators(value: &str) -> String {
    if cfg!(windows) && value.contains('\\') {
        value.replace('\\', "/")
    } else {
        value.to_string()
    }
}

/// Expand $VAR, ${VAR}, ${VAR:-default}, ${VAR:=default}, ${#VAR},
/// ${VAR%pattern}, ${VAR#pattern}, $((arithmetic)), and `backtick` substitution in a string.
fn expand_env_vars(arg: &str) -> String {
    if !arg.contains('$') && !arg.contains('`') {
        return arg.to_string();
    }

    let mut result = String::with_capacity(arg.len());
    let chars: Vec<char> = arg.chars().collect();
    let mut i = 0;

    while i < chars.len() {
        // `command` — backtick command substitution
        if chars[i] == '`' {
            i += 1; // skip opening backtick
            let mut cmd = String::new();
            while i < chars.len() && chars[i] != '`' {
                // backslash escapes inside backticks: \` \$ \\ \newline
                if chars[i] == '\\' && i + 1 < chars.len() {
                    match chars[i + 1] {
                        '`' | '$' | '\\' => {
                            cmd.push(chars[i + 1]);
                            i += 2;
                            continue;
                        }
                        _ => {}
                    }
                }
                cmd.push(chars[i]);
                i += 1;
            }
            if i < chars.len() {
                i += 1; // skip closing backtick
            }
            let output = run_native_capture(&cmd);
            result.push_str(output.stdout.trim_end());
            continue;
        }

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

            // $(command) — command substitution (single paren, not double)
            if chars[i + 1] == '(' && !(i + 2 < chars.len() && chars[i + 2] == '(') {
                i += 2; // skip $(
                let mut cmd = String::new();
                let mut depth: i32 = 1;
                while i < chars.len() && depth > 0 {
                    if chars[i] == '(' { depth += 1; }
                    else if chars[i] == ')' { depth -= 1; }
                    if depth > 0 { cmd.push(chars[i]); }
                    i += 1;
                }
                // Execute command and capture stdout
                let output = run_native_capture(&cmd);
                result.push_str(output.stdout.trim_end());
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
                result.push_str(&normalize_path_separators(&expand_parameter(&content)));
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
                // Dynamic shell variables
                let value = match var_name.as_str() {
                    "RANDOM" => {
                        // Simple PRNG — good enough for shell scripts
                        let t = std::time::SystemTime::now()
                            .duration_since(std::time::UNIX_EPOCH)
                            .map(|d| d.as_nanos()).unwrap_or(0);
                        ((t ^ (t >> 16)) % 32768).to_string()
                    }
                    "SECONDS" => {
                        std::env::var("RUSH_START_TIME")
                            .ok()
                            .and_then(|s| s.parse::<u64>().ok())
                            .map(|start| {
                                let now = std::time::SystemTime::now()
                                    .duration_since(std::time::UNIX_EPOCH)
                                    .map(|d| d.as_secs()).unwrap_or(0);
                                (now - start).to_string()
                            })
                            .unwrap_or_else(|| "0".into())
                    }
                    "LINENO" => std::env::var("RUSH_LINENO").unwrap_or_else(|_| "0".into()),
                    "PPID" => {
                        #[cfg(unix)]
                        { unsafe { libc::getppid() }.to_string() }
                        #[cfg(not(unix))]
                        { "0".to_string() }
                    }
                    _ => std::env::var(&var_name).unwrap_or_default(),
                };
                result.push_str(&normalize_path_separators(&value));
                continue;
            }

            // Special parameters: $? $$ $! $0 $# $@ $* $-
            match chars[i + 1] {
                '?' => {
                    // $? — last exit code (from RUSH_LAST_EXIT env or 0)
                    let code = std::env::var("RUSH_LAST_EXIT").unwrap_or_else(|_| "0".into());
                    result.push_str(&code);
                    i += 2;
                    continue;
                }
                '$' => {
                    // $$ — PID of the shell
                    result.push_str(&std::process::id().to_string());
                    i += 2;
                    continue;
                }
                '!' => {
                    // $! — PID of last background command
                    let pid = std::env::var("RUSH_LAST_BG_PID").unwrap_or_else(|_| "0".into());
                    result.push_str(&pid);
                    i += 2;
                    continue;
                }
                '0' => {
                    // $0 — name of the shell or script
                    let name = std::env::var("RUSH_SCRIPT_NAME").unwrap_or_else(|_| "rush".into());
                    result.push_str(&name);
                    i += 2;
                    continue;
                }
                '#' => {
                    // $# — number of positional parameters
                    let argc = std::env::var("RUSH_ARGC").unwrap_or_else(|_| "0".into());
                    result.push_str(&argc);
                    i += 2;
                    continue;
                }
                '@' => {
                    // $@ — all positional params (each as separate word)
                    let args = std::env::var("RUSH_ARGV").unwrap_or_default();
                    result.push_str(&args);
                    i += 2;
                    continue;
                }
                '*' if i + 2 < chars.len() && chars[i + 2] != '/' => {
                    // $* — all positional params (joined by IFS)
                    // Only match if not followed by / (to avoid matching glob patterns like $*/)
                    let args = std::env::var("RUSH_ARGV").unwrap_or_default();
                    result.push_str(&args);
                    i += 2;
                    continue;
                }
                '-' if i + 2 >= chars.len() || !chars[i + 2].is_ascii_alphanumeric() => {
                    // $- — current shell option flags
                    result.push_str(&flags::current_flags());
                    i += 2;
                    continue;
                }
                _ => {}
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
    if arg == "~+" {
        return std::env::var("PWD").unwrap_or_else(|_|
            std::env::current_dir().map(|p| p.to_string_lossy().to_string()).unwrap_or_default()
        );
    }
    if arg == "~-" {
        return std::env::var("OLDPWD").unwrap_or_default();
    }
    if arg.starts_with("~/") {
        if let Ok(home) = std::env::var("HOME") {
            return format!("{home}{}", &arg[1..]);
        }
    }
    if arg.starts_with("~+/") {
        let pwd = std::env::var("PWD").unwrap_or_else(|_|
            std::env::current_dir().map(|p| p.to_string_lossy().to_string()).unwrap_or_default()
        );
        return format!("{pwd}{}", &arg[2..]);
    }
    if arg.starts_with("~-/") {
        let oldpwd = std::env::var("OLDPWD").unwrap_or_default();
        return format!("{oldpwd}{}", &arg[2..]);
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

/// Parse a command string into words, handling quotes including $'...'.
pub fn parse_command_line(line: &str) -> Vec<String> {
    parse_command_line_with_quote_info(line)
        .into_iter()
        .map(|(word, _)| word)
        .collect()
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
        let home = std::env::var("HOME")
            .or_else(|_| std::env::var("USERPROFILE"))
            .unwrap_or_default()
            .replace('\\', "/");
        if home.is_empty() { return; } // skip if no home dir
        let expanded = expand_tilde("~").replace('\\', "/");
        assert_eq!(expanded, home);
        let expanded_bin = expand_tilde("~/bin").replace('\\', "/");
        assert_eq!(expanded_bin, format!("{home}/bin"));
        assert_eq!(expand_tilde("/usr/bin"), "/usr/bin");
    }

    #[test]
    fn native_echo() {
        let result = run_native_capture("echo hello");
        assert_eq!(result.stdout.trim(), "hello");
        assert_eq!(result.exit_code, 0);
    }

    #[test]
    #[cfg(unix)]
    fn native_pipeline() {
        let result = run_native_capture("echo hello | wc -c");
        let count: i32 = result.stdout.trim().parse().unwrap_or(0);
        assert!(count > 0, "expected byte count > 0, got {count}");
    }

    #[test]
    #[cfg(unix)]
    fn native_false() {
        let result = run_native("/usr/bin/false");
        assert_ne!(result.exit_code, 0);
    }

    #[test]
    #[cfg(unix)]
    fn redirect_stdout() {
        let tmp = std::env::temp_dir().join("rush_redirect_test.txt");
        let path = tmp.to_string_lossy().to_string();
        run_native(&format!("echo redirect_test > {path}"));
        let content = std::fs::read_to_string(&tmp).unwrap_or_default();
        assert_eq!(content.trim(), "redirect_test");
        std::fs::remove_file(&tmp).ok();
    }

    #[test]
    #[cfg(unix)]
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

    // ── Extraction tests for additional redirect operators (#167) ──

    #[test]
    fn extract_redir_append() {
        let parts = vec!["echo".into(), "data".into(), ">>".into(), "log.txt".into()];
        let (clean, redir) = extract_redirections(parts);
        assert_eq!(clean, vec!["echo", "data"]);
        assert_eq!(redir.len(), 1);
        assert!(matches!(&redir[0], Redirect::StdoutAppend(f) if f == "log.txt"));
    }

    #[test]
    fn extract_redir_stdin() {
        let parts = vec!["cat".into(), "<".into(), "input.txt".into()];
        let (clean, redir) = extract_redirections(parts);
        assert_eq!(clean, vec!["cat"]);
        assert_eq!(redir.len(), 1);
        assert!(matches!(&redir[0], Redirect::StdinRead(f) if f == "input.txt"));
    }

    #[test]
    fn extract_redir_clobber() {
        let parts = vec!["echo".into(), "x".into(), ">|".into(), "out.txt".into()];
        let (clean, redir) = extract_redirections(parts);
        assert_eq!(clean, vec!["echo", "x"]);
        assert_eq!(redir.len(), 1);
        assert!(matches!(&redir[0], Redirect::StdoutClobber(f) if f == "out.txt"));
    }

    #[test]
    fn extract_redir_stderr_append() {
        let parts = vec!["cmd".into(), "2>>".into(), "err.log".into()];
        let (clean, redir) = extract_redirections(parts);
        assert_eq!(clean, vec!["cmd"]);
        assert_eq!(redir.len(), 1);
        assert!(matches!(&redir[0], Redirect::StderrAppend(f) if f == "err.log"));
    }

    #[test]
    fn extract_redir_combined_devnull() {
        // > /dev/null 2>&1
        let parts = vec!["cmd".into(), ">".into(), "/dev/null".into(), "2>&1".into()];
        let (clean, redir) = extract_redirections(parts);
        assert_eq!(clean, vec!["cmd"]);
        assert_eq!(redir.len(), 2);
        assert!(matches!(&redir[0], Redirect::StdoutWrite(f) if f == "/dev/null"));
        assert!(matches!(&redir[1], Redirect::StderrToStdout));
    }

    #[test]
    #[cfg(unix)]
    fn redirect_stderr_to_file() {
        let tmp = std::env::temp_dir().join("rush_redir_stderr_167.txt");
        let path = tmp.to_string_lossy().to_string();
        run_native(&format!("ls /nonexistent_rush_167 2> {path}"));
        let content = std::fs::read_to_string(&tmp).unwrap_or_default();
        assert!(!content.is_empty(), "stderr should be captured in file");
        std::fs::remove_file(&tmp).ok();
    }

    #[test]
    #[cfg(unix)]
    fn redirect_stderr_to_stdout_standalone() {
        // `2>&1` is accepted and the command completes without crash.
        // Note: full fd dup (stderr→stdout file) is simplified in current impl.
        let result = run_native("ls /nonexistent_rush_167 2>&1");
        assert_ne!(result.exit_code, 127, "ls should be found");
    }

    #[test]
    #[cfg(unix)]
    fn redirect_silence_both_devnull() {
        let result = run_native("ls /nonexistent_rush_167 > /dev/null 2>&1");
        // Should not crash; exit code is non-zero but ls was found
        assert_ne!(result.exit_code, 127);
    }

    #[test]
    #[cfg(unix)]
    fn redirect_stdin_from_file() {
        let tmp = std::env::temp_dir().join("rush_redir_stdin_167.txt");
        std::fs::write(&tmp, "stdin_line\n").ok();
        let path = tmp.to_string_lossy().to_string();
        let result = run_native_capture(&format!("cat < {path}"));
        assert_eq!(result.stdout.trim(), "stdin_line");
        std::fs::remove_file(&tmp).ok();
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

        // Use forward slashes for cross-platform compatibility
        let dir_str = dir.to_string_lossy().replace('\\', "/");
        let pattern = format!("{dir_str}/*.txt");
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
        let expected = normalize_path_separators(&home);
        let result = expand_env_vars("$HOME/bin");
        assert_eq!(result, format!("{expected}/bin"));
    }

    #[test]
    fn env_var_braces() {
        let home = std::env::var("HOME").unwrap_or_default();
        let expected = normalize_path_separators(&home);
        let result = expand_env_vars("${HOME}/bin");
        assert_eq!(result, format!("{expected}/bin"));
    }

    #[test]
    fn env_var_missing() {
        let result = expand_env_vars("$NONEXISTENT_VAR_XYZ");
        assert_eq!(result, "");
    }

    #[test]
    fn env_var_in_command() {
        let home = std::env::var("HOME")
            .or_else(|_| std::env::var("USERPROFILE"))
            .unwrap_or_default()
            .replace('\\', "/");
        if home.is_empty() { return; }
        // Use HOME on Unix, set it on Windows for the test
        if std::env::var("HOME").is_err() {
            unsafe { std::env::set_var("HOME", &home) };
        }
        let result = parse_and_expand("echo $HOME");
        assert_eq!(result, vec!["echo", &home]);
    }

    // ── Special parameters ───────────────────────────────────────────

    #[test]
    fn special_param_pid() {
        let result = expand_env_vars("$$");
        let pid = std::process::id().to_string();
        assert_eq!(result, pid);
    }

    #[test]
    fn special_param_exit_code() {
        unsafe { std::env::set_var("RUSH_LAST_EXIT", "42") };
        let result = expand_env_vars("$?");
        assert_eq!(result, "42");
        unsafe { std::env::remove_var("RUSH_LAST_EXIT") };
    }

    #[test]
    fn special_param_flags() {
        use crate::flags;
        flags::set_errexit(false);
        flags::set_xtrace(false);
        let result = expand_env_vars("$-");
        assert_eq!(result, "");
        flags::set_errexit(true);
        let result = expand_env_vars("$-");
        assert_eq!(result, "e");
        flags::set_errexit(false);
    }

    #[test]
    fn special_param_argc() {
        unsafe { std::env::set_var("RUSH_ARGC", "3") };
        let result = expand_env_vars("$#");
        assert_eq!(result, "3");
        unsafe { std::env::remove_var("RUSH_ARGC") };
    }

    #[test]
    fn special_param_script_name() {
        unsafe { std::env::set_var("RUSH_SCRIPT_NAME", "test.rush") };
        let result = expand_env_vars("$0");
        assert_eq!(result, "test.rush");
        unsafe { std::env::remove_var("RUSH_SCRIPT_NAME") };
    }

    #[test]
    fn random_var() {
        let r1 = expand_env_vars("$RANDOM");
        let n: u64 = r1.parse().unwrap_or(99999);
        assert!(n < 32768, "RANDOM should be 0-32767, got {n}");
    }

    // ── Octal in $'...' ─────────────────────────────────────────────

    #[test]
    fn dollar_single_quote_octal() {
        let result = parse_command_line("echo $'\\101'"); // \101 = 'A' (65 decimal)
        assert_eq!(result, vec!["echo", "A"]);
    }

    #[test]
    fn dollar_single_quote_null() {
        let result = parse_command_line("echo $'\\0'"); // \0 = null
        assert_eq!(result, vec!["echo", "\0"]);
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
        // Use a var we know exists on all platforms
        unsafe { std::env::set_var("_TEST_DEFAULT", "exists") };
        assert_eq!(expand_parameter("_TEST_DEFAULT:-fallback"), "exists");
        unsafe { std::env::remove_var("_TEST_DEFAULT") };
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
    // ── ANSI-C quoting ────────────────────────────────────────────

    #[test]
    fn dollar_single_quote_escapes() {
        let result = parse_command_line("echo $'hello\\nworld'");
        assert_eq!(result, vec!["echo", "hello\nworld"]);
    }

    #[test]
    fn dollar_single_quote_tab() {
        let result = parse_command_line("echo $'a\\tb'");
        assert_eq!(result, vec!["echo", "a\tb"]);
    }

    #[test]
    fn dollar_single_quote_hex() {
        let result = parse_command_line("echo $'\\x41'");
        assert_eq!(result, vec!["echo", "A"]); // 0x41 = 'A'
    }

    // ── Tilde ~+ ~- ────────────────────────────────────────────────

    #[test]
    fn tilde_plus() {
        let pwd = std::env::current_dir().unwrap().to_string_lossy().to_string();
        unsafe { std::env::set_var("PWD", &pwd); }
        assert_eq!(expand_tilde_only("~+"), pwd);
    }

    #[test]
    fn tilde_minus() {
        unsafe { std::env::set_var("OLDPWD", "/tmp"); }
        assert_eq!(expand_tilde_only("~-"), "/tmp");
        unsafe { std::env::remove_var("OLDPWD"); }
    }

    fn param_prefix_strip() {
        unsafe { std::env::set_var("_TEST_PATH", "/usr/local/bin") };
        assert_eq!(expand_parameter("_TEST_PATH#*/"), "usr/local/bin"); // shortest: just "/"
        assert_eq!(expand_parameter("_TEST_PATH##*/"), "bin");          // longest: "/usr/local/"
        unsafe { std::env::remove_var("_TEST_PATH") };
    }

    #[test]
    fn backtick_command_substitution() {
        // Simple backtick substitution — `echo hello` should produce "hello"
        let result = expand_env_vars("`echo hello`");
        assert_eq!(result, "hello");
    }

    #[test]
    fn backtick_inline() {
        // Backticks embedded in a larger string
        let result = expand_env_vars("prefix-`echo world`-suffix");
        assert_eq!(result, "prefix-world-suffix");
    }

    #[test]
    fn backtick_matches_dollar_paren() {
        // Backtick and $() should produce identical results
        let bt = expand_env_vars("`echo test123`");
        let dp = expand_env_vars("$(echo test123)");
        assert_eq!(bt, dp);
    }

    #[test]
    fn backtick_no_backticks_passthrough() {
        // Strings without $ or ` should pass through unchanged
        let result = expand_env_vars("plain text");
        assert_eq!(result, "plain text");
    }

    #[test]
    fn backtick_with_args() {
        // Backtick substitution with command arguments
        let result = expand_env_vars("`echo -n hi`");
        assert_eq!(result, "hi");
    }
}
