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

use crate::eval::{Evaluator, Output};
use crate::parser;
use crate::process;
#[allow(unused_imports)]
use base64::Engine;

// ── JSON Types ──────────────────────────────────────────────────────

#[derive(Serialize)]
pub struct LlmContext {
    pub ready: bool,
    pub host: String,
    pub user: String,
    pub cwd: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub git_branch: Option<String>,
    pub git_dirty: bool,
    pub last_exit_code: i32,
    pub shell: String,
    pub version: String,
}

#[derive(Serialize, Default)]
pub struct LlmResult {
    pub status: String,
    pub exit_code: i32,
    pub cwd: String,
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

#[derive(Deserialize)]
#[allow(dead_code)]
struct Envelope {
    cmd: Option<String>,
    cwd: Option<String>,
    timeout: Option<u64>,
    env: Option<HashMap<String, String>>,
}

// ── Output Spool ────────────────────────────────────────────────────

struct Spool {
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

const VERSION: &str = "0.1.0";
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
    let mut spool = Spool::new();
    let mut last_exit_code: i32 = 0;

    loop {
        // Emit context
        let cwd = get_cwd();
        let (branch, dirty) = get_git_info(&cwd);
        let context = LlmContext {
            ready: true,
            host: get_hostname(),
            user: get_username(),
            cwd: cwd.clone(),
            git_branch: branch,
            git_dirty: dirty,
            last_exit_code,
            shell: "rush".into(),
            version: VERSION.into(),
        };
        {
            let mut out = stdout.lock();
            serde_json::to_writer(&mut out, &context).ok();
            out.write_all(b"\n").ok();
            out.flush().ok();
        }

        // Read command
        let mut line = String::new();
        if stdin.lock().read_line(&mut line).unwrap_or(0) == 0 {
            break; // EOF
        }
        let raw = line.trim_end_matches('\n').trim_end_matches('\r');
        if raw.is_empty() { continue; }

        // Dispatch
        let result = if raw.starts_with('{') {
            handle_envelope(raw, &mut spool)
        } else {
            let input = if raw.starts_with('"') && raw.ends_with('"') {
                serde_json::from_str::<String>(raw).unwrap_or_else(|_| raw.to_string())
            } else {
                raw.to_string()
            };
            execute_command(&input, &mut spool)
        };

        last_exit_code = result.exit_code;

        // Emit result
        {
            let mut out = stdout.lock();
            serde_json::to_writer(&mut out, &result).ok();
            out.write_all(b"\n").ok();
            out.flush().ok();
        }
    }
}

fn handle_envelope(raw: &str, spool: &mut Spool) -> LlmResult {
    let envelope: Envelope = match serde_json::from_str(raw) {
        Ok(e) => e,
        Err(e) => return error_result(&format!("Invalid JSON envelope: {e}"), &get_cwd()),
    };

    // Apply cwd
    if let Some(ref cwd) = envelope.cwd {
        if Path::new(cwd).is_dir() {
            std::env::set_current_dir(cwd).ok();
        } else {
            return error_result(&format!("Requested cwd does not exist: {cwd}"), &get_cwd());
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
        None => return error_result("Envelope missing required \"cmd\" field", &get_cwd()),
    };

    execute_command(&cmd, spool)
}

/// Execute a command and return structured result (public for MCP).
pub fn execute_one(input: &str) -> LlmResult {
    let mut spool = Spool::new();
    execute_command(input, &mut spool)
}

fn execute_command(input: &str, spool: &mut Spool) -> LlmResult {
    let cwd = get_cwd();
    let start = Instant::now();
    let first_word = input.split_whitespace().next().unwrap_or("");

    // ── Builtins ────────────────────────────────────────────────────

    if first_word == "lcat" {
        let args = input.strip_prefix("lcat").unwrap_or("").trim();
        let mut result = lcat(args, &cwd);
        result.duration_ms = start.elapsed().as_millis();
        return result;
    }

    if first_word == "spool" {
        return handle_spool(input, spool, &cwd, start);
    }

    if first_word == "help" {
        let topic = input.strip_prefix("help").unwrap_or("").trim();
        let help_text = if topic.is_empty() {
            "Available builtins in LLM mode: lcat, spool, help.\nUse 'help <topic>' for details.".to_string()
        } else {
            format!("Help for '{topic}': not yet implemented in Rust port.")
        };
        return LlmResult {
            status: "success".into(),
            stdout: Some(help_text),
            cwd,
            duration_ms: start.elapsed().as_millis(),
            ..Default::default()
        };
    }

    if let Some((cmd, hint)) = check_tty_blocklist(input) {
        return LlmResult {
            status: "error".into(),
            error_type: Some("tty_required".into()),
            command: Some(cmd),
            hint: Some(hint),
            cwd,
            duration_ms: start.elapsed().as_millis(),
            ..Default::default()
        };
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
        return match std::env::set_current_dir(&path) {
            Ok(()) => LlmResult {
                status: "success".into(),
                cwd: get_cwd(),
                duration_ms: start.elapsed().as_millis(),
                ..Default::default()
            },
            Err(e) => error_result(&format!("cd: {path}: {e}"), &cwd),
        };
    }

    // ── Try Rush syntax (only if it looks like Rush, not a bare command) ──
    let is_rush_syntax = {
        let rush_indicators = [" = ", " += ", " -= ", "#{", "..", "()", "end"];
        let rush_first_words = [
            "if", "for", "while", "until", "unless", "loop", "def", "class", "enum",
            "return", "try", "begin", "case", "match", "puts", "print", "warn", "die",
            "true", "false", "nil", "break", "next", "continue",
        ];
        rush_first_words.iter().any(|k| first_word.eq_ignore_ascii_case(k))
            || rush_indicators.iter().any(|i| input.contains(i))
            || input.contains('=') && !input.contains("==")
            || input.starts_with('[') || input.starts_with('{')
    };

    if is_rush_syntax {
        if let Ok(nodes) = parser::parse(input) {
            let mut capture = CaptureOutput::new();
            let exit_code;
            {
                let mut eval = Evaluator::new(&mut capture);
                let _ = eval.exec_toplevel(&nodes);
                exit_code = eval.exit_code;
            }
            let stdout = if capture.stdout_buf.is_empty() { None } else { Some(capture.stdout_buf.trim_end().to_string()) };
            let stderr = if capture.stderr_buf.is_empty() { None } else { Some(capture.stderr_buf.trim_end().to_string()) };

            return maybe_spool(stdout, stderr, exit_code, spool, start);
        }
    }

    // ── Shell execution ─────────────────────────────────────────────
    let result = process::run_native_capture(input);
    let stdout = if result.stdout.is_empty() { None } else { Some(result.stdout.trim_end().to_string()) };
    let stderr = if result.stderr.is_empty() { None } else { Some(result.stderr.trim_end().to_string()) };

    maybe_spool(stdout, stderr, result.exit_code, spool, start)
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
            cwd: "/tmp".into(),
            git_branch: Some("main".into()),
            git_dirty: false,
            last_exit_code: 0,
            shell: "rush".into(),
            version: "0.1.0".into(),
        };
        let json = serde_json::to_string(&ctx).unwrap();
        assert!(json.contains("\"ready\":true"));
        assert!(json.contains("\"shell\":\"rush\""));
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
    fn execute_echo() {
        let mut spool = Spool::new();
        let result = execute_command("echo hello", &mut spool);
        assert_eq!(result.status, "success");
        assert_eq!(result.stdout.as_deref(), Some("hello"));
    }

    #[test]
    fn execute_rush_expr() {
        let mut spool = Spool::new();
        let result = execute_command("puts 1 + 2", &mut spool);
        assert_eq!(result.status, "success");
        assert_eq!(result.stdout.as_deref(), Some("3"));
    }
}
