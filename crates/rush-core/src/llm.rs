//! LLM mode: JSON wire protocol for agent/tool integration.
//!
//! Protocol: for each command, the shell emits a context line (JSON),
//! reads a command (plain text, JSON string, or JSON envelope),
//! executes it, and emits a result line (JSON).

use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::io::{BufRead, Write};
use std::path::Path;
use std::time::Instant;

use crate::dispatch;
use crate::env::Environment;
use crate::eval::{Evaluator, Output, StdOutput};
#[cfg(not(unix))]
use crate::{parser, process};
#[allow(unused_imports)]
use base64::Engine;

// ── JSON Types ──────────────────────────────────────────────────────

#[derive(Serialize)]
pub struct LlmContext {
    pub ready: bool,
    pub host: String,
    pub user: String,
    pub session_id: String,
    pub cwd: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub git_branch: Option<String>,
    pub git_dirty: bool,
    pub last_exit_code: i32,
    pub shell: String,
    pub version: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub lang_spec: Option<String>,
}

#[derive(Serialize, Default)]
pub struct LlmResult {
    pub status: String,
    pub exit_code: i32,
    pub cwd: String,
    // Identity — every turn. host + session_id let callers verify
    // the response came from the session they addressed (matters for
    // parallel multi-session agent work).
    #[serde(skip_serializing_if = "String::is_empty")]
    pub host: String,
    #[serde(skip_serializing_if = "String::is_empty")]
    pub session_id: String,
    // Per-turn state that can change mid-session
    #[serde(skip_serializing_if = "Option::is_none")]
    pub git_branch: Option<String>,
    #[serde(skip_serializing_if = "is_false")]
    pub git_dirty: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stdout: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stderr: Option<String>,
    pub duration_ms: u128,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub errors: Option<Vec<String>>,
    // Output limit fields
    #[serde(skip_serializing_if = "is_zero")]
    pub stdout_lines: usize,
    #[serde(skip_serializing_if = "is_zero")]
    pub stdout_bytes: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub preview: Option<String>,
    #[serde(skip_serializing_if = "is_zero")]
    pub preview_bytes: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hint: Option<String>,
    // TTY error
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error_type: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub command: Option<String>,
    // lcat fields
    #[serde(skip_serializing_if = "Option::is_none")]
    pub file: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub mime: Option<String>,
    #[serde(skip_serializing_if = "is_zero_i64")]
    pub size_bytes: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub encoding: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub content: Option<String>,
    #[serde(skip_serializing_if = "is_zero")]
    pub lines: usize,
    // Spool fields
    #[serde(skip_serializing_if = "is_zero")]
    pub spool_position: usize,
    #[serde(skip_serializing_if = "is_zero")]
    pub spool_total: usize,
}

fn is_zero(v: &usize) -> bool { *v == 0 }
fn is_zero_i64(v: &i64) -> bool { *v == 0 }
fn is_false(b: &bool) -> bool { !*b }

#[derive(Deserialize)]
#[allow(dead_code)]
struct Envelope {
    cmd: Option<String>,
    cwd: Option<String>,
    timeout: Option<u64>,
    env: Option<HashMap<String, String>>,
}

// ── Session state ───────────────────────────────────────────────────

/// Per-session mutable state threaded through LLM-mode and MCP dispatch.
/// Holding the Evaluator's Environment here is what makes variables,
/// function definitions, and class definitions survive across tool calls.
pub struct LlmSession {
    pub host: String,
    pub user: String,
    pub session_id: String,
    pub env: Environment,
    pub(crate) spool: Spool,
}

impl LlmSession {
    pub fn new() -> Self {
        Self {
            host: get_hostname(),
            user: get_username(),
            session_id: gen_session_id(),
            env: Environment::new(),
            spool: Spool::new(),
        }
    }
}

impl Default for LlmSession {
    fn default() -> Self { Self::new() }
}

/// 64-bit random session id, rendered as 16 hex chars.
/// Namespace is per-harness runtime — birthday collision at ~4B concurrent
/// sessions, well beyond any realistic bench scope.
fn gen_session_id() -> String {
    let mut bytes = [0u8; 8];
    // OS randomness failure is unrecoverable; fall back to PID+time
    // rather than crashing the entire session over an id.
    if getrandom::getrandom(&mut bytes).is_err() {
        let pid = std::process::id() as u64;
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_nanos() as u64)
            .unwrap_or(0);
        let mix = pid.wrapping_mul(0x9E3779B97F4A7C15).wrapping_add(now);
        bytes.copy_from_slice(&mix.to_ne_bytes());
    }
    let mut s = String::with_capacity(16);
    for b in bytes {
        use std::fmt::Write;
        let _ = write!(s, "{b:02x}");
    }
    s
}

// ── Output Spool ────────────────────────────────────────────────────

pub(crate) struct Spool {
    lines: Vec<String>,
    total_bytes: usize,
}

impl Spool {
    fn new() -> Self {
        Self { lines: Vec::new(), total_bytes: 0 }
    }

    fn store(&mut self, output: &str) {
        self.lines = output.split('\n').map(String::from).collect();
        self.total_bytes = output.len();
    }

    fn clear(&mut self) {
        self.lines.clear();
        self.total_bytes = 0;
    }

    fn has_data(&self) -> bool { !self.lines.is_empty() }

    fn read(&self, offset: i64, count: usize) -> (String, usize) {
        let offset = if offset < 0 {
            (self.lines.len() as i64 + offset).max(0) as usize
        } else {
            (offset as usize).min(self.lines.len())
        };
        let count = count.min(self.lines.len().saturating_sub(offset));
        if count == 0 {
            return (String::new(), offset);
        }
        let slice: Vec<&str> = self.lines[offset..offset + count].iter().map(|s| s.as_str()).collect();
        (slice.join("\n"), offset + count)
    }

    fn search(&self, pattern: &str) -> Result<Vec<(usize, String)>, String> {
        let re = regex::Regex::new(pattern).map_err(|e| format!("Invalid regex: {e}"))?;
        let mut matches = Vec::new();
        for (i, line) in self.lines.iter().enumerate() {
            if re.is_match(line) {
                matches.push((i + 1, line.clone())); // 1-based line numbers
            }
        }
        Ok(matches)
    }

    fn preview(&self, max_bytes: usize) -> String {
        let mut result = String::new();
        for line in &self.lines {
            if result.len() + line.len() + 1 > max_bytes {
                break;
            }
            if !result.is_empty() { result.push('\n'); }
            result.push_str(line);
        }
        result
    }
}

// ── TTY Blocklist ───────────────────────────────────────────────────

fn check_tty_blocklist(cmd: &str) -> Option<(String, String)> {
    let first_word = cmd.split_whitespace().next().unwrap_or("");
    let args = cmd.split_whitespace().skip(1).collect::<Vec<_>>().join(" ");

    let hint = match first_word {
        "vim" | "vi" | "nano" | "emacs" => {
            format!("Use lcat {args} to read, File.write(\"{args}\", content) to write.")
        }
        "less" | "more" => {
            format!("Use lcat {args} to read. Output is captured automatically in LLM mode.")
        }
        "top" | "htop" => "Use ps aux for process listing.".to_string(),
        _ => return None,
    };

    Some((first_word.to_string(), hint))
}

// ── lcat (file reader) ──────────────────────────────────────────────

pub fn lcat(path: &str, cwd: &str) -> LlmResult {
    let path = path.trim();
    if path.is_empty() {
        return error_result("lcat: missing file path", cwd);
    }

    let full_path = if Path::new(path).is_absolute() {
        path.to_string()
    } else {
        Path::new(cwd).join(path).to_string_lossy().to_string()
    };

    if !Path::new(&full_path).exists() {
        return error_result(&format!("lcat: {path}: No such file or directory"), cwd);
    }

    let mut mime = get_mime_type(path);
    let metadata = std::fs::metadata(&full_path).ok();
    let size = metadata.map(|m| m.len() as i64).unwrap_or(0);

    // For unknown extension, probe file content
    if mime == "application/octet-stream" && is_likely_text(&full_path) {
        mime = "text/plain".to_string();
    }

    if is_text_mime(&mime) {
        match std::fs::read_to_string(&full_path) {
            Ok(content) => {
                let line_count = content.split('\n').count();
                LlmResult {
                    status: "success".into(),
                    file: Some(full_path),
                    mime: Some(mime),
                    size_bytes: size,
                    encoding: Some("utf8".into()),
                    content: Some(content),
                    lines: line_count,
                    cwd: cwd.into(),
                    ..Default::default()
                }
            }
            Err(e) => error_result(&format!("lcat: {path}: {e}"), cwd),
        }
    } else {
        // Binary file — base64 encode
        match std::fs::read(&full_path) {
            Ok(bytes) => {
                use base64::Engine;
                let b64 = base64::engine::general_purpose::STANDARD.encode(&bytes);
                LlmResult {
                    status: "success".into(),
                    file: Some(full_path),
                    mime: Some(mime),
                    size_bytes: size,
                    encoding: Some("base64".into()),
                    content: Some(b64),
                    cwd: cwd.into(),
                    ..Default::default()
                }
            }
            Err(e) => error_result(&format!("lcat: {path}: {e}"), cwd),
        }
    }
}

fn get_mime_type(path: &str) -> String {
    let ext = Path::new(path)
        .extension()
        .map(|e| format!(".{}", e.to_string_lossy().to_lowercase()))
        .unwrap_or_default();

    match ext.as_str() {
        ".txt" | ".log" | ".ini" | ".cfg" | ".conf" => "text/plain",
        ".md" => "text/markdown",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".xml" => "text/xml",
        ".yaml" | ".yml" => "text/yaml",
        ".toml" => "text/toml",
        ".rs" => "text/x-rust",
        ".py" => "text/x-python",
        ".js" => "text/javascript",
        ".ts" => "text/typescript",
        ".go" => "text/x-go",
        ".cs" => "text/x-csharp",
        ".java" => "text/x-java",
        ".c" | ".h" => "text/x-c",
        ".cpp" => "text/x-c++",
        ".rb" => "text/x-ruby",
        ".sh" | ".bash" => "text/x-shellscript",
        ".rush" => "text/x-rush",
        ".html" => "text/html",
        ".css" => "text/css",
        ".sql" => "text/x-sql",
        ".swift" => "text/x-swift",
        ".ps1" | ".psm1" => "text/x-powershell",
        ".lua" => "text/x-lua",
        ".r" => "text/x-r",
        ".png" => "image/png",
        ".jpg" | ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".gz" => "application/gzip",
        ".tar" => "application/x-tar",
        ".exe" => "application/x-executable",
        ".wasm" => "application/wasm",
        _ => "application/octet-stream",
    }
    .to_string()
}

fn is_text_mime(mime: &str) -> bool {
    mime.starts_with("text/") || mime == "application/json"
}

/// Check if a file is likely text by reading the first few KB.
fn is_likely_text(path: &str) -> bool {
    match std::fs::read(path) {
        Ok(bytes) => {
            let check_len = bytes.len().min(8192);
            !bytes[..check_len].contains(&0) // No null bytes = likely text
        }
        Err(_) => false,
    }
}

// ── lwrite (file writer) ────────────────────────────────────────────

pub fn lwrite(path: &str, content: &str, encoding: Option<&str>, cwd: &str) -> LlmResult {
    let path = path.trim();
    if path.is_empty() {
        return error_result("lwrite: missing file path", cwd);
    }

    let full_path = if Path::new(path).is_absolute() {
        path.to_string()
    } else {
        Path::new(cwd).join(path).to_string_lossy().to_string()
    };

    let bytes = if encoding == Some("base64") {
        match base64::engine::general_purpose::STANDARD.decode(content) {
            Ok(b) => b,
            Err(e) => return error_result(&format!("lwrite: base64 decode error: {e}"), cwd),
        }
    } else {
        content.as_bytes().to_vec()
    };

    // Create parent directories if needed
    if let Some(parent) = Path::new(&full_path).parent() {
        if !parent.exists() {
            if let Err(e) = std::fs::create_dir_all(parent) {
                return error_result(&format!("lwrite: cannot create directory: {e}"), cwd);
            }
        }
    }

    match std::fs::write(&full_path, &bytes) {
        Ok(()) => LlmResult {
            status: "success".into(),
            file: Some(full_path),
            size_bytes: bytes.len() as i64,
            cwd: cwd.into(),
            ..Default::default()
        },
        Err(e) => error_result(&format!("lwrite: {path}: {e}"), cwd),
    }
}

// ── Helpers ─────────────────────────────────────────────────────────

pub fn error_result(msg: &str, cwd: &str) -> LlmResult {
    LlmResult {
        status: "error".into(),
        exit_code: 1,
        stderr: Some(msg.into()),
        cwd: cwd.into(),
        ..Default::default()
    }
}

pub fn get_cwd() -> String {
    std::env::current_dir()
        .map(|p| p.to_string_lossy().to_string())
        .unwrap_or_else(|_| ".".into())
}

pub fn get_hostname() -> String {
    #[cfg(unix)]
    {
        let mut buf = [0u8; 256];
        unsafe {
            if libc::gethostname(buf.as_mut_ptr() as *mut libc::c_char, buf.len()) == 0 {
                let len = buf.iter().position(|&b| b == 0).unwrap_or(buf.len());
                return String::from_utf8_lossy(&buf[..len]).to_string();
            }
        }
    }
    std::env::var("HOSTNAME").unwrap_or_else(|_| "unknown".into())
}

pub fn get_username() -> String {
    std::env::var("USER")
        .or_else(|_| std::env::var("USERNAME"))
        .unwrap_or_else(|_| "unknown".into())
}

pub fn get_git_info(cwd: &str) -> (Option<String>, bool) {
    let branch = std::process::Command::new("git")
        .args(["rev-parse", "--abbrev-ref", "HEAD"])
        .current_dir(cwd)
        .output()
        .ok()
        .filter(|o| o.status.success())
        .map(|o| String::from_utf8_lossy(&o.stdout).trim().to_string());

    let dirty = std::process::Command::new("git")
        .args(["status", "--porcelain"])
        .current_dir(cwd)
        .output()
        .ok()
        .map(|o| !o.stdout.is_empty())
        .unwrap_or(false);

    (branch, dirty)
}

// ── LLM Output Capture ─────────────────────────────────────────────

/// Output handler that captures everything into strings.
pub struct CaptureOutput {
    pub stdout_buf: String,
    pub stderr_buf: String,
}

impl CaptureOutput {
    pub fn new() -> Self {
        Self {
            stdout_buf: String::new(),
            stderr_buf: String::new(),
        }
    }
}

impl Output for CaptureOutput {
    fn puts(&mut self, s: &str) {
        self.stdout_buf.push_str(s);
        self.stdout_buf.push('\n');
    }
    fn print(&mut self, s: &str) {
        self.stdout_buf.push_str(s);
    }
    fn warn(&mut self, s: &str) {
        self.stderr_buf.push_str(s);
        self.stderr_buf.push('\n');
    }
}

// ── Main REPL ───────────────────────────────────────────────────────

const VERSION: &str = env!("CARGO_PKG_VERSION");
const OUTPUT_LIMIT: usize = 32 * 1024; // 32KB

/// Run the LLM mode REPL on stdin/stdout.
pub fn run() {
    // Machine-friendly env
    unsafe {
        std::env::set_var("NO_COLOR", "1");
        std::env::set_var("CI", "true");
        std::env::set_var("GIT_TERMINAL_PROMPT", "0");
    }

    let stdin = std::io::stdin();
    let stdout = std::io::stdout();
    let mut session = LlmSession::new();
    let mut last_exit_code: i32 = 0;

    // Emit initial context once, including lang_spec. Per-turn state
    // (cwd, exit_code, git_branch, git_dirty) rides on each LlmResult.
    {
        let cwd = get_cwd();
        let (branch, dirty) = get_git_info(&cwd);
        let context = LlmContext {
            ready: true,
            host: session.host.clone(),
            user: session.user.clone(),
            session_id: session.session_id.clone(),
            cwd,
            git_branch: branch,
            git_dirty: dirty,
            last_exit_code,
            shell: "rush".into(),
            version: VERSION.into(),
            lang_spec: Some(crate::lang_spec::LANG_SPEC.to_string()),
        };
        let mut out = stdout.lock();
        serde_json::to_writer(&mut out, &context).ok();
        out.write_all(b"\n").ok();
        out.flush().ok();
    }

    loop {
        // Read command
        let mut line = String::new();
        if stdin.lock().read_line(&mut line).unwrap_or(0) == 0 {
            break; // EOF
        }
        let raw = line.trim_end_matches('\n').trim_end_matches('\r');
        if raw.is_empty() { continue; }

        // Dispatch
        let (result, is_exit) = if raw.starts_with('{') {
            let r = handle_envelope(raw, &mut session);
            let exit = envelope_is_exit(raw);
            (r, exit)
        } else {
            let input = if raw.starts_with('"') && raw.ends_with('"') {
                serde_json::from_str::<String>(raw).unwrap_or_else(|_| raw.to_string())
            } else {
                raw.to_string()
            };
            let first = input.split_whitespace().next().unwrap_or("");
            let exit = first == "exit" || first == "quit";
            (execute_command(&input, &mut session), exit)
        };

        last_exit_code = result.exit_code;
        let _ = last_exit_code; // retained for future context-refresh API

        // Emit result
        {
            let mut out = stdout.lock();
            serde_json::to_writer(&mut out, &result).ok();
            out.write_all(b"\n").ok();
            out.flush().ok();
        }

        if is_exit {
            break;
        }
    }
}

/// Detect whether a JSON envelope is an exit/quit command.
fn envelope_is_exit(raw: &str) -> bool {
    let env: serde_json::Value = match serde_json::from_str(raw) {
        Ok(v) => v,
        Err(_) => return false,
    };
    match env.get("cmd").and_then(|c| c.as_str()) {
        Some(c) => {
            let first = c.split_whitespace().next().unwrap_or("");
            first == "exit" || first == "quit"
        }
        None => false,
    }
}

fn handle_envelope(raw: &str, session: &mut LlmSession) -> LlmResult {
    let envelope: Envelope = match serde_json::from_str(raw) {
        Ok(e) => e,
        Err(_) => {
            // Not a valid envelope — could be a Rush hash literal starting
            // with '{' (e.g. `{name: "x"} | as json`). Fall through to the
            // normal dispatch rather than returning an envelope parse error.
            return execute_command(raw, session);
        }
    };

    // Apply cwd
    if let Some(ref cwd) = envelope.cwd {
        if Path::new(cwd).is_dir() {
            std::env::set_current_dir(cwd).ok();
        } else {
            return decorate(error_result(&format!("Requested cwd does not exist: {cwd}"), &get_cwd()), session);
        }
    }

    // Apply env vars
    if let Some(ref env) = envelope.env {
        for (k, v) in env {
            unsafe { std::env::set_var(k, v) };
        }
    }

    let cmd = match envelope.cmd {
        Some(c) => c,
        None => return decorate(error_result("Envelope missing required \"cmd\" field", &get_cwd()), session),
    };

    execute_command(&cmd, session)
}

/// Execute a single command against a fresh session. Convenience for callers
/// that do not need persistent state (tests, one-shot MCP usage).
pub fn execute_one(input: &str) -> LlmResult {
    let mut session = LlmSession::new();
    execute_command(input, &mut session)
}

/// Execute a command against a persistent session. Used by MCP to preserve
/// Rush variables, function definitions, and class definitions across tool
/// calls within the same process lifetime.
pub fn execute_one_in(input: &str, session: &mut LlmSession) -> LlmResult {
    execute_command(input, session)
}

/// Populate identity and per-turn state fields that every LlmResult carries.
fn decorate(mut result: LlmResult, session: &LlmSession) -> LlmResult {
    result.host = session.host.clone();
    result.session_id = session.session_id.clone();
    // Refresh git state per turn — cheap, and catches mid-session changes.
    let (branch, dirty) = get_git_info(&result.cwd);
    result.git_branch = branch;
    result.git_dirty = dirty;
    result
}

fn execute_command(input: &str, session: &mut LlmSession) -> LlmResult {
    let cwd = get_cwd();
    let start = Instant::now();
    let first_word = input.split_whitespace().next().unwrap_or("");

    // ── Builtins ────────────────────────────────────────────────────

    if first_word == "lcat" {
        let args = input.strip_prefix("lcat").unwrap_or("").trim();
        let mut result = lcat(args, &cwd);
        result.duration_ms = start.elapsed().as_millis();
        return decorate(result, session);
    }

    if first_word == "spool" {
        let result = handle_spool(input, &mut session.spool, &cwd, start);
        return decorate(result, session);
    }

    if first_word == "help" {
        let topic = input.strip_prefix("help").unwrap_or("").trim();
        let help_text = if topic.is_empty() {
            "Available builtins in LLM mode: lcat, spool, help.\nUse 'help <topic>' for details.".to_string()
        } else {
            format!("Help for '{topic}': not yet implemented in Rust port.")
        };
        return decorate(LlmResult {
            status: "success".into(),
            stdout: Some(help_text),
            cwd,
            duration_ms: start.elapsed().as_millis(),
            ..Default::default()
        }, session);
    }

    if let Some((cmd, hint)) = check_tty_blocklist(input) {
        return decorate(LlmResult {
            status: "error".into(),
            error_type: Some("tty_required".into()),
            command: Some(cmd),
            hint: Some(hint),
            cwd,
            duration_ms: start.elapsed().as_millis(),
            ..Default::default()
        }, session);
    }

    if first_word == "exit" || first_word == "quit" {
        let code: i32 = input
            .split_whitespace()
            .nth(1)
            .and_then(|s| s.parse().ok())
            .unwrap_or(0);
        return decorate(LlmResult {
            status: if code == 0 { "success".into() } else { "error".into() },
            exit_code: code,
            cwd,
            duration_ms: start.elapsed().as_millis(),
            ..Default::default()
        }, session);
    }

    if first_word == "cd" {
        let target = input.strip_prefix("cd").unwrap_or("").trim();
        let path = if target.is_empty() || target == "~" {
            std::env::var("HOME").unwrap_or_else(|_| ".".into())
        } else if let Some(rest) = target.strip_prefix("~/") {
            format!("{}/{rest}", std::env::var("HOME").unwrap_or_default())
        } else {
            target.to_string()
        };
        return decorate(match std::env::set_current_dir(&path) {
            Ok(()) => LlmResult {
                status: "success".into(),
                cwd: get_cwd(),
                duration_ms: start.elapsed().as_millis(),
                ..Default::default()
            },
            Err(e) => error_result(&format!("cd: {path}: {e}"), &cwd),
        }, session);
    }

    // ── Unified dispatch (Rush syntax, shell, pipelines, chains) ──
    // Route through dispatch::dispatch_with_jobs so --llm mode gets the
    // same feature surface as `rush -c`: pipeline operators, chain ops
    // (&& || ;), triage, and subshells. Fd-level capture picks up output
    // from paths that write directly to stdout (shell, pipelines).
    let (stdout_text, stderr_text, exit_code) = run_dispatch_captured(input, &mut session.env);
    let stdout = if stdout_text.is_empty() { None } else { Some(stdout_text.trim_end().to_string()) };
    let stderr = if stderr_text.is_empty() { None } else { Some(stderr_text.trim_end().to_string()) };

    decorate(maybe_spool(stdout, stderr, exit_code, &mut session.spool, start), session)
}

/// Run a command through `dispatch::dispatch_with_jobs` with fd 1 and fd 2
/// redirected to pipes, so all output (shell stdout, pipeline println!,
/// Rush puts via StdOutput) is captured into strings. Preserves the
/// session's Environment via mem::take / into_env.
///
/// Unix-only for now. Windows LLM-mode callers fall back to the previous
/// split-Rush-vs-shell path (see below).
#[cfg(unix)]
fn run_dispatch_captured(input: &str, env: &mut Environment) -> (String, String, i32) {
    use std::io::Read;
    use std::os::unix::io::{FromRawFd, RawFd};
    use std::sync::Mutex;

    // fd 1 / fd 2 are process-global. Serialize captured dispatches so
    // parallel test threads (or any concurrent LlmSession usage) do not
    // stomp each other's redirections. In the --llm / --mcp runtime
    // there is only one caller at a time, so this is free.
    static FD_LOCK: Mutex<()> = Mutex::new(());
    let _guard = FD_LOCK.lock().unwrap_or_else(|p| p.into_inner());

    // Create pipes for stdout and stderr capture.
    fn make_pipe() -> std::io::Result<(RawFd, RawFd)> {
        let mut fds = [0i32; 2];
        let rc = unsafe { libc::pipe(fds.as_mut_ptr()) };
        if rc != 0 {
            return Err(std::io::Error::last_os_error());
        }
        Ok((fds[0], fds[1]))
    }

    let (out_r, out_w) = match make_pipe() {
        Ok(p) => p,
        Err(_) => return (String::new(), "rush: pipe() failed".into(), 1),
    };
    let (err_r, err_w) = match make_pipe() {
        Ok(p) => p,
        Err(_) => {
            unsafe { libc::close(out_r); libc::close(out_w); }
            return (String::new(), "rush: pipe() failed".into(), 1);
        }
    };

    // Flush any buffered libc/Rust stdio before redirecting so previous
    // writes don't leak into our capture.
    use std::io::Write;
    let _ = std::io::stdout().flush();
    let _ = std::io::stderr().flush();

    // Save current stdout/stderr so we can restore after capture.
    let saved_out = unsafe { libc::dup(1) };
    let saved_err = unsafe { libc::dup(2) };
    if saved_out < 0 || saved_err < 0 {
        unsafe {
            libc::close(out_r); libc::close(out_w);
            libc::close(err_r); libc::close(err_w);
            if saved_out >= 0 { libc::close(saved_out); }
            if saved_err >= 0 { libc::close(saved_err); }
        }
        return (String::new(), "rush: dup() failed".into(), 1);
    }

    // Redirect stdout → out_w, stderr → err_w.
    unsafe {
        libc::dup2(out_w, 1);
        libc::dup2(err_w, 2);
        libc::close(out_w);
        libc::close(err_w);
    }

    // Spawn drainer threads so the pipe buffer never blocks dispatch.
    // Ownership of the read ends goes to the threads via File (from_raw_fd).
    let out_reader = unsafe { std::fs::File::from_raw_fd(out_r) };
    let err_reader = unsafe { std::fs::File::from_raw_fd(err_r) };
    let out_handle = std::thread::spawn(move || {
        let mut buf = String::new();
        let mut reader = out_reader;
        let _ = reader.read_to_string(&mut buf);
        buf
    });
    let err_handle = std::thread::spawn(move || {
        let mut buf = String::new();
        let mut reader = err_reader;
        let _ = reader.read_to_string(&mut buf);
        buf
    });

    // Run dispatch against the session's Environment. StdOutput writes
    // to fd 1/2 (which we've redirected), so Rush puts/warn land in
    // the same capture as shell+pipeline output — preserving order.
    let exit_code;
    {
        let mut output = StdOutput;
        let owned_env = std::mem::take(env);
        let mut eval = Evaluator::with_env(owned_env, &mut output);
        let result = dispatch::dispatch_with_jobs(input, &mut eval, None);
        exit_code = result.exit_code;
        *env = eval.into_env();
    }

    // Flush any remaining output into the pipes before restoring fds.
    let _ = std::io::stdout().flush();
    let _ = std::io::stderr().flush();

    // Restore original stdout/stderr, which closes our write ends and
    // signals EOF to the drainer threads.
    unsafe {
        libc::dup2(saved_out, 1);
        libc::dup2(saved_err, 2);
        libc::close(saved_out);
        libc::close(saved_err);
    }

    let stdout = out_handle.join().unwrap_or_default();
    let stderr = err_handle.join().unwrap_or_default();
    (stdout, stderr, exit_code)
}

/// Windows fallback: the old split path (no fd redirection). Pipeline
/// operators may not work on Windows until this is unified.
#[cfg(not(unix))]
fn run_dispatch_captured(input: &str, env: &mut Environment) -> (String, String, i32) {
    // Try Rush syntax first via Evaluator with CaptureOutput.
    if let Ok(nodes) = parser::parse(input) {
        let mut capture = CaptureOutput::new();
        let owned_env = std::mem::take(env);
        let mut eval = Evaluator::with_env(owned_env, &mut capture);
        let _ = eval.exec_toplevel(&nodes);
        let exit = eval.exit_code;
        *env = eval.into_env();
        if !capture.stdout_buf.is_empty() || !capture.stderr_buf.is_empty() || exit == 0 {
            return (capture.stdout_buf, capture.stderr_buf, exit);
        }
    }
    let result = process::run_native_capture(input);
    (result.stdout, result.stderr, result.exit_code)
}

/// Apply output limit — spool if stdout exceeds 32KB.
fn maybe_spool(
    stdout: Option<String>,
    stderr: Option<String>,
    exit_code: i32,
    spool: &mut Spool,
    start: Instant,
) -> LlmResult {
    let status = if exit_code == 0 { "success" } else { "error" };

    if let Some(ref s) = stdout {
        if s.len() > OUTPUT_LIMIT {
            spool.store(s);
            let preview = spool.preview(512);
            let lines = s.split('\n').count();
            return LlmResult {
                status: status.into(),
                exit_code,
                stdout_lines: lines,
                stdout_bytes: s.len(),
                preview: Some(preview.clone()),
                preview_bytes: preview.len(),
                hint: Some(format!("Output spooled ({lines} lines, {} bytes). Use 'spool 0 50' to read.", s.len())),
                cwd: get_cwd(),
                duration_ms: start.elapsed().as_millis(),
                ..Default::default()
            };
        }
    }

    LlmResult {
        status: status.into(),
        exit_code,
        stdout,
        stderr,
        cwd: get_cwd(),
        duration_ms: start.elapsed().as_millis(),
        ..Default::default()
    }
}

fn handle_spool(input: &str, spool: &mut Spool, cwd: &str, start: Instant) -> LlmResult {
    let parts: Vec<&str> = input.split_whitespace().collect();

    if !spool.has_data() {
        return LlmResult {
            status: "error".into(),
            exit_code: 1,
            stderr: Some("No spooled output. Run a command first.".into()),
            cwd: cwd.into(),
            duration_ms: start.elapsed().as_millis(),
            ..Default::default()
        };
    }

    // spool search <pattern> | spool grep <pattern>
    if parts.get(1) == Some(&"search") || parts.get(1) == Some(&"grep") {
        let pattern = parts[2..].join(" ");
        if pattern.is_empty() {
            return LlmResult {
                status: "error".into(),
                exit_code: 1,
                stderr: Some("spool search: missing pattern".into()),
                cwd: cwd.into(),
                duration_ms: start.elapsed().as_millis(),
                ..Default::default()
            };
        }
        return match spool.search(&pattern) {
            Ok(matches) => {
                let match_count = matches.len();
                let content = matches.iter()
                    .map(|(n, line)| format!("{n}: {line}"))
                    .collect::<Vec<_>>()
                    .join("\n");
                LlmResult {
                    status: "success".into(),
                    stdout: if content.is_empty() { None } else { Some(content) },
                    spool_total: spool.lines.len(),
                    hint: Some(format!("{match_count} matches in {} spooled lines", spool.lines.len())),
                    cwd: cwd.into(),
                    duration_ms: start.elapsed().as_millis(),
                    ..Default::default()
                }
            }
            Err(e) => LlmResult {
                status: "error".into(),
                exit_code: 1,
                stderr: Some(e),
                cwd: cwd.into(),
                duration_ms: start.elapsed().as_millis(),
                ..Default::default()
            },
        };
    }

    // spool clear
    if parts.get(1) == Some(&"clear") {
        spool.clear();
        return LlmResult {
            status: "success".into(),
            stdout: Some("Spool cleared.".into()),
            cwd: cwd.into(),
            duration_ms: start.elapsed().as_millis(),
            ..Default::default()
        };
    }

    // spool [offset] [count]
    let offset: i64 = parts.get(1).and_then(|s| s.parse().ok()).unwrap_or(0);
    let count: usize = parts.get(2).and_then(|s| s.parse().ok()).unwrap_or(50);

    let (content, new_pos) = spool.read(offset, count);

    LlmResult {
        status: "success".into(),
        stdout: Some(content),
        spool_position: new_pos,
        spool_total: spool.lines.len(),
        cwd: cwd.into(),
        duration_ms: start.elapsed().as_millis(),
        ..Default::default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn context_serializes() {
        let ctx = LlmContext {
            ready: true,
            host: "test".into(),
            user: "mark".into(),
            session_id: "deadbeefcafebabe".into(),
            cwd: "/tmp".into(),
            git_branch: Some("main".into()),
            git_dirty: false,
            last_exit_code: 0,
            shell: "rush".into(),
            version: "0.1.0".into(),
            lang_spec: None,
        };
        let json = serde_json::to_string(&ctx).unwrap();
        assert!(json.contains("\"ready\":true"));
        assert!(json.contains("\"shell\":\"rush\""));
        assert!(json.contains("\"session_id\":\"deadbeefcafebabe\""));
        // lang_spec is None, should be omitted
        assert!(!json.contains("lang_spec"));
    }

    #[test]
    fn context_first_includes_lang_spec() {
        let ctx = LlmContext {
            ready: true,
            host: "test".into(),
            user: "mark".into(),
            session_id: "deadbeefcafebabe".into(),
            cwd: "/tmp".into(),
            git_branch: None,
            git_dirty: false,
            last_exit_code: 0,
            shell: "rush".into(),
            version: "0.1.0".into(),
            lang_spec: Some(crate::lang_spec::LANG_SPEC.to_string()),
        };
        let json = serde_json::to_string(&ctx).unwrap();
        assert!(json.contains("lang_spec"));
        assert!(json.contains("Rush Language Spec"));
    }

    #[test]
    fn result_omits_nulls() {
        let result = LlmResult {
            status: "success".into(),
            cwd: "/tmp".into(),
            ..Default::default()
        };
        let json = serde_json::to_string(&result).unwrap();
        assert!(!json.contains("stdout"));
        assert!(!json.contains("stderr"));
        assert!(!json.contains("file"));
    }

    #[test]
    fn lcat_reads_file() {
        let tmp = std::env::temp_dir().join("rush_llm_test.txt");
        std::fs::write(&tmp, "hello llm").unwrap();
        let result = lcat(&tmp.to_string_lossy(), "/tmp");
        assert_eq!(result.status, "success");
        assert_eq!(result.content.as_deref(), Some("hello llm"));
        assert_eq!(result.mime.as_deref(), Some("text/plain"));
        assert_eq!(result.encoding.as_deref(), Some("utf8"));
        std::fs::remove_file(&tmp).ok();
    }

    #[test]
    fn lcat_missing_file() {
        let result = lcat("/nonexistent/file.txt", "/tmp");
        assert_eq!(result.status, "error");
        assert!(result.stderr.unwrap().contains("No such file"));
    }

    #[test]
    fn mime_type_detection() {
        assert_eq!(get_mime_type("test.rs"), "text/x-rust");
        assert_eq!(get_mime_type("test.py"), "text/x-python");
        assert_eq!(get_mime_type("test.json"), "application/json");
        assert_eq!(get_mime_type("test.png"), "image/png");
        assert_eq!(get_mime_type("test.unknown"), "application/octet-stream");
    }

    #[test]
    fn tty_blocklist() {
        assert!(check_tty_blocklist("vim file.txt").is_some());
        assert!(check_tty_blocklist("nano").is_some());
        assert!(check_tty_blocklist("top").is_some());
        assert!(check_tty_blocklist("ls -la").is_none());
        assert!(check_tty_blocklist("echo hello").is_none());
    }

    #[test]
    fn spool_store_and_read() {
        let mut spool = Spool::new();
        spool.store("line1\nline2\nline3\nline4\nline5");

        let (content, pos) = spool.read(0, 2);
        assert_eq!(content, "line1\nline2");
        assert_eq!(pos, 2);

        let (content, pos) = spool.read(2, 2);
        assert_eq!(content, "line3\nline4");
        assert_eq!(pos, 4);

        // Negative offset (from end)
        let (content, _) = spool.read(-2, 2);
        assert_eq!(content, "line4\nline5");
    }

    #[test]
    fn spool_preview() {
        let mut spool = Spool::new();
        spool.store("short line\nanother line");
        let preview = spool.preview(20);
        assert_eq!(preview, "short line");
    }

    #[test]
    fn spool_search_literal() {
        let mut spool = Spool::new();
        spool.store("alpha\nbeta\ngamma\nalpha beta\ndelta");
        let matches = spool.search("alpha").unwrap();
        assert_eq!(matches.len(), 2);
        assert_eq!(matches[0], (1, "alpha".to_string()));
        assert_eq!(matches[1], (4, "alpha beta".to_string()));
    }

    #[test]
    fn spool_search_regex() {
        let mut spool = Spool::new();
        spool.store("line1\nline2\nline3\nother\nline22");
        let matches = spool.search("line[23]").unwrap();
        assert_eq!(matches.len(), 3);
        assert_eq!(matches[0].0, 2); // line2
        assert_eq!(matches[1].0, 3); // line3
        assert_eq!(matches[2].0, 5); // line22
    }

    #[test]
    fn spool_search_no_matches() {
        let mut spool = Spool::new();
        spool.store("alpha\nbeta\ngamma");
        let matches = spool.search("delta").unwrap();
        assert_eq!(matches.len(), 0);
    }

    #[test]
    fn spool_search_invalid_regex() {
        let mut spool = Spool::new();
        spool.store("some content");
        let result = spool.search("[invalid");
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("Invalid regex"));
    }

    #[test]
    fn lwrite_creates_file() {
        let tmp = std::env::temp_dir().join("rush_lwrite_test.txt");
        let _ = std::fs::remove_file(&tmp);
        let result = lwrite(&tmp.to_string_lossy(), "hello write", None, "/tmp");
        assert_eq!(result.status, "success");
        assert_eq!(result.size_bytes, 11);
        assert_eq!(std::fs::read_to_string(&tmp).unwrap(), "hello write");
        std::fs::remove_file(&tmp).ok();
    }

    #[test]
    fn lwrite_base64() {
        let tmp = std::env::temp_dir().join("rush_lwrite_b64.bin");
        let _ = std::fs::remove_file(&tmp);
        // "hello" in base64 = "aGVsbG8="
        let result = lwrite(&tmp.to_string_lossy(), "aGVsbG8=", Some("base64"), "/tmp");
        assert_eq!(result.status, "success");
        assert_eq!(std::fs::read(&tmp).unwrap(), b"hello");
        std::fs::remove_file(&tmp).ok();
    }

    #[test]
    fn lwrite_creates_parents() {
        let tmp = std::env::temp_dir().join("rush_lwrite_nested/sub/dir/test.txt");
        let _ = std::fs::remove_file(&tmp);
        let result = lwrite(&tmp.to_string_lossy(), "nested", None, "/tmp");
        assert_eq!(result.status, "success");
        assert_eq!(std::fs::read_to_string(&tmp).unwrap(), "nested");
        // Clean up
        let _ = std::fs::remove_dir_all(std::env::temp_dir().join("rush_lwrite_nested"));
    }

    #[test]
    fn lwrite_empty_path_error() {
        let result = lwrite("", "content", None, "/tmp");
        assert_eq!(result.status, "error");
    }

    // Tests that exercise execute_command end-to-end live in
    // crates/rush-cli/tests/audit_wire_protocol.rs — they spawn the
    // rush-cli binary so fd-level stdout capture works. Function-level
    // tests here can't coexist with fd redirection under cargo test's
    // stdout capture, so session/dispatch behavior is covered at the
    // subprocess layer instead.

    #[test]
    fn envelope_exit_detection() {
        assert!(envelope_is_exit(r#"{"cmd":"exit"}"#));
        assert!(envelope_is_exit(r#"{"cmd":"quit"}"#));
        assert!(envelope_is_exit(r#"{"cmd":"exit 3"}"#));
        assert!(!envelope_is_exit(r#"{"cmd":"echo hello"}"#));
        assert!(!envelope_is_exit(r#"{"not_a_cmd":"exit"}"#));
        assert!(!envelope_is_exit("not json"));
    }

    #[test]
    fn session_id_is_unique_per_session() {
        let a = LlmSession::new();
        let b = LlmSession::new();
        assert_ne!(a.session_id, b.session_id);
        assert_eq!(a.session_id.len(), 16);
        assert!(a.session_id.chars().all(|c| c.is_ascii_hexdigit()));
    }
}
